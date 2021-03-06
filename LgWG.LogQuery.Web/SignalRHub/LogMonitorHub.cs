﻿using Abp.Dependency;
using Abp.Runtime.Session;
using LgWG.LogQuery.LogMonitor;
using LgWG.LogQuery.LogMonitor.DTO;
using LgWG.LogQuery.LogTrace;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Hubs;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LgWG.LogQuery.Web.SignalRHub
{

    //系统监控集线器
    [HubName("logMonitorHubMini")]
    public class LogMonitorHub : Hub<IClinet>
    {
        public static List<string> ConnectionIdList = new List<string>();//需要实时更新的客户端,添加删除的时候考虑并发问题（加锁，完善阶段）

        public IAbpSession AbpSession { get; set; }
        readonly object lockObj = new object();
        readonly LogMonitorDal _logMonitorDal;

        public LogMonitorHub() : this(LogMonitorDal.Instance)
        {
        }
        public LogMonitorHub(LogMonitorDal logMonitorDal)
        {
            AbpSession = NullAbpSession.Instance;
            _logMonitorDal = logMonitorDal;
        }

        //SignalR程序的一般工作过程：
        //1、前台页面初始化时，前台的getAllLogMonitorDatas方法调用服务器端集线器的GetAllLogMonitorDatas方法获取初始数据；
        //2、服务器端定时更新信息，前台的updateLogMonitorDatas方法被调用完成所有前台的同步更新(Clients.All.updateLogMonitorDatas)。
        //在本程序中，初始化时ajax方法从后台获取了数据，因此省去了第一步的过程。
        public override Task OnConnected()
        {
            Trace.WriteLine("客户端" + Context.ConnectionId + "连接成功");
            return base.OnConnected();
        }

        public override Task OnReconnected()
        {
            lock (lockObj)
            {
                ConnectionIdList.Remove(Context.ConnectionId);
            }
            return base.OnReconnected();
        }

        //前台使用，定时获取后端的数据
        public string GetAllLogMonitorDatas(string range, string speed, bool bRTMode, string userid)
        {
            var result = _logMonitorDal.GetAllLogMonitorDatas(range, userid, Context.ConnectionId);
            return result;
        }


        //前台调用，将参数传递到后台,range只有格式，说明是只获取最新一条
        public void InitParasTrans(string range, string speed, bool bRTMode, string userid)
        {
            var connectionId = Context.ConnectionId;
            var ueserName = Context.User.Identity.Name;
            //  var sysIDs = Context.User.Identity.Claims["UserSysCategorys"];           
            lock (lockObj)
            {
                if (bRTMode)
                {
                    ConnectionIdList.Add(connectionId);
                }
                else
                {
                    ConnectionIdList.Remove(connectionId);
                }
                ConnectionIdList = ConnectionIdList.Distinct().ToList();
            }
            _logMonitorDal.InitParasTrans(connectionId, range, speed, bRTMode);
        }

    }


    //前台客户端定义的方法
    public interface IClinet
    {
        void updateLogMonitorDatas(string lineProducts);
    }


    public class LogMonitorDal : ITransientDependency
    {

        public IAbpSession AbpSession { get; set; }
        public class State
        {
            public string command;
            public Timer timer;
            public TimeSpan interval;
        }

        // 单一实例对象
        readonly static Lazy<LogMonitorDal> _instance = new Lazy<LogMonitorDal>(() => new LogMonitorDal(GlobalHost.ConnectionManager.GetHubContext<LogMonitorHub, IClinet>().Clients));
        readonly object _updateProductNumsLock = new object();

        Timer _timer = null;

        private volatile bool _updatingProductNums = false;
        private LogMonitorDal(IHubConnectionContext<IClinet> clients) : this(clients, _logTraceService, _logMonitorService)
        {
            AbpSession = NullAbpSession.Instance;
            _clients = clients;
        }

        readonly static Lazy<LogMonitorDal> _instance2 = new Lazy<LogMonitorDal>(() => new LogMonitorDal(GlobalHost.ConnectionManager.GetHubContext<LogMonitorHub, IClinet>().Clients, _logTraceService, _logMonitorService));
        static ILog_OperateTraceService _logTraceService;
        static ILog_SystemMonitorService _logMonitorService;

        public LogMonitorDal(IHubConnectionContext<IClinet> clients, ILog_OperateTraceService logTraceService, ILog_SystemMonitorService logMonitorService)
        {
            _logTraceService = logTraceService;
            _logMonitorService = logMonitorService;
        }

        public string GetAllLogMonitorDatas(string range, string userid, string _connectionId)
        {
            var str = GetMonitorChartDataFromAPI(range,  userid);
            _clients.Client(_connectionId).updateLogMonitorDatas(str);  //更新一个客户端
            return str;
        }

        public void InitParasTrans(string connectionId, string range, string speed, bool bRTMode)
        {
            _connectionId = connectionId;
            _range = range;
            _speed = speed;
            _bRTMode = bRTMode;
          //  StartIntervalTask();
        }


        public static LogMonitorDal Instance { get { return _instance.Value; } }
        IHubConnectionContext<IClinet> _clients { get; set; }
        string _connectionId;

        string _range = "H:m";
        string _speed = "10"; //10s
        bool _bRTMode = true;

        TimeSpan GetRefreshSpanSec()
        {
            double spanSec = 10;
            try
            {
                spanSec = Convert.ToDouble(_speed);
            }
            catch
            {

            }
            spanSec = spanSec <= 0 ? 10 : spanSec;
            var _updateInterval = TimeSpan.FromSeconds(spanSec);
            return _updateInterval;
        }


        void StartIntervalTask()
        {
            if (!_bRTMode)
            {
                if (_timer == null)
                {
                    return;
                }
                else if (LogMonitorHub.ConnectionIdList.Count <= 0)
                {
                    _timer.Change(Timeout.Infinite, 0);
                }
            }
            else
            {
                var refreshInterval = GetRefreshSpanSec();
                State s = new State();
                if (_timer == null)
                {
                    _timer = new Timer(RefreshLogMonitorDatas, s, refreshInterval, refreshInterval);
                }
                else
                {
                    _timer.Change(refreshInterval, refreshInterval);
                }
                s.interval = refreshInterval;
                s.timer = _timer;
            }

        }

        string GetMonitorChartDataFromAPI(string range, string userid)
        {
            LogMonitorVM servers = GetMonitorChartDataVMFromAPI( range,userid);
            var str = JsonConvert.SerializeObject(servers);
            return str;
        }

        LogMonitorVM GetMonitorChartDataVMFromAPI(string range, string userid)
        {
            string baseUrl = "";
            string requestMethod = "/api/services/app/log_SystemMonitorService/GetMonitorChartData?range=" + range + "&userId=" + userid;
            string resultMsg = "";
            LogMonitorVM servers = new WebApiHelper().HttpClientDoPost<LogMonitorVM, LogMonitorVM>(baseUrl, "", requestMethod, null, out resultMsg).FirstOrDefault();
            return servers;
        }


        void RefreshLogMonitorDatas(object state)
        {
            string curAction = "";
            try
            {
                //curAction = UserActionHandler.ActionFlag;
                // curAction = System.Web.HttpContext.Current.Session["command"].ToString();
            }
            catch (Exception ex)
            {

            }
            State s = state as State;
            s.command = curAction.ToString();
            if (!string.IsNullOrEmpty(s.command) && s.command.Contains("stop"))
            {
                s.timer.Change(Timeout.Infinite, 0);
                return;
            }
            else if (s.command == "continue")
            {
                s.timer.Change(s.interval, s.interval);
            }
            lock (_updateProductNumsLock)
            {
                if (!_updatingProductNums)
                {
                    _updatingProductNums = true;
                    BroadcastLogMonitorDatas();
                    _updatingProductNums = false;
                }
            }
        }

        void BroadcastLogMonitorDatas()
        {
            var str = GetMonitorChartDataFromAPI(_range,  null);
            //_clients.All.updateLogMonitorDatas(str);  //更新所有客户端
            //  _clients.Client(_connectionId).updateLogMonitorDatas(str);  //更新一个客户端
            var list = LogMonitorHub.ConnectionIdList;
            if (list != null && list.Count > 0)
            {
                _clients.Clients(list).updateLogMonitorDatas(str); //更新一组客户端
            }
        }

    }







}