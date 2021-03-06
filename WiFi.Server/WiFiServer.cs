﻿using NewLife;
using NewLife.Net;
using NewLife.Threading;
using System;
using WiFi.Entity;
using XCode.Membership;

namespace WiFi.Server
{
    class WiFiServer : NetServer<WiFiSession>
    {
        /// <summary>日志命令。打开后显示收发命令详情</summary>
        public Boolean CommandLog { get; set; }

        private TimerX _timer;

        protected override void OnStart()
        {
            base.OnStart();

            _timer = new TimerX(s => ClearExpire(10 * 60), null, 10_000, 10_000) { Async = true };
        }

        protected override void OnStop()
        {
            _timer.TryDispose();
            _timer = null;

            base.OnStop();
        }

        #region 清理超时
        /// <summary>清理超时会话</summary>
        /// <param name="secTimeout"></param>
        /// <returns></returns>
        public Int32 ClearExpire(Int32 secTimeout)
        {
            return DeviceOnline.ClearExpire(secTimeout).Count;
        }
        #endregion
    }

    class WiFiSession : NetSession<WiFiServer>
    {
        #region 属性
        #endregion

        #region 主循环
        protected override void OnReceive(ReceivedEventArgs e)
        {
            var str = e.Packet.ToStr().Trim();
            if (str.IsNullOrEmpty()) return;

            ManageProvider.UserHost = Remote?.EndPoint.Address + "";

            if (Host.CommandLog) WriteLog(str);

            //Process(str);
            var ss = str.Split("\r", "\n");
            if (ss != null && ss.Length > 0)
            {
                foreach (var item in ss)
                {
                    Process(item);
                }
            }
        }

        public void Process(String data)
        {
            var rd = Parse(data);
            if (rd == null) return;

            // 登录在线
            var host = Check(rd.HostMAC, null, DeviceKinds.Host);
            var dest = Check(rd.RouteMAC, rd.Remark, rd.IsRoute ? DeviceKinds.Device : DeviceKinds.Route);
            var src = Check(rd.DeviceMAC, null, rd.IsRoute ? DeviceKinds.Route : DeviceKinds.Device);

            // 计算距离
            var pa = 60;
            var pn = 3.3;
            if (host != null)
            {
                var dv = host.Device;
                if (dv != null)
                {
                    if (dv.ParameterA > 0) pa = dv.ParameterA;
                    if (dv.ParameterN > 0.01) pn = dv.ParameterN;
                }
            }
            rd.Distance = GetDistance(rd.Rssi, pa, pn);

            // 设备状态
            if (src != null)
            {
                src.Rssi = rd.Rssi;
                src.Distance = rd.Distance;

                if (host != null) src.HostID = host.DeviceID;
                if (dest != null) src.RouteID = dest.DeviceID;

                src.SaveAsync();

                // 设备属性
                var dv = src.Device;
                if (dv != null)
                {
                    dv.LastHostID = host.DeviceID;
                    dv.LastRouteID = dest.DeviceID;
                    dv.LastRSSI = rd.Rssi;
                    dv.LastDistance = rd.Distance;
                    dv.SaveAsync();

                    rd.DeviceID = dv.ID;
                }
            }

            // 更新路由器名称
            if (dest != null && !rd.Remark.IsNullOrEmpty())
            {
                dest.Name = rd.Remark;
                dest.SaveAsync();

                var dv = dest.Device;
                if (dv != null)
                {
                    dv.Name = rd.Remark;
                    dv.SaveAsync();
                }
            }

            // 入库
            rd.SaveAsync();
        }

        protected virtual DeviceOnline Check(String mac, String name, DeviceKinds kind)
        {
            var olt = GetOnline(mac);
            if (olt == null)
            {
                var ip = Remote?.EndPoint.Address + "";
                var dv = Login(mac, name, ip, kind);
                olt = CreateOnline(mac, dv);
            }
            olt.Kind = kind;
            olt.Total++;
            olt.SaveAsync(5_000);

            return olt;
        }
        #endregion

        #region 解析
        public virtual RawData Parse(String str)
        {
            str = str.Trim();
            if (str.IsNullOrEmpty()) return null;

            var ss = str.Split('|', ' ');
            if (ss == null || ss.Length < 6) return null;

            var rd = new RawData();

            // TZ-007
            if (ss[2].Length >= 6)
            {
                // B4:E6:2D:09:62:ED|78:DA:07:85:86:7E|70:AF:6A:78:45:0A|01|09|2|-73|0|0|0|FeiFan
                rd.HostMAC = ss[0];
                rd.DeviceMAC = ss[1];
                rd.RouteMAC = ss[2];
                rd.FrameType = ss[3].ToInt();
                rd.FrameType2 = ss[4].ToInt();
                rd.Channel = ss[5].ToInt();
                rd.Rssi = ss[6].ToInt();

                if (ss.Length >= 7 + 1) rd.PowerSave = ss[7] == "1";

                //// 抛弃路由器发出的数据
                //if (ss.Length >= 10 && ss[9] == "0") return null;
                //if (ss.Length >= 9 + 1) rd.IsRoute = ss[9] == "1";

                //rd.Remark = ss.Last();
                if (ss.Length >= 10 + 1) rd.Remark = ss[10];

                // 手机发给路由的有名称，路由发给手机没名称
                if (rd.Remark.IsNullOrEmpty()) rd.IsRoute = true;
            }
            else
            {

            }

            //// 计算距离
            //rd.Distance = GetDistance(rd.Rssi);

            var ip = Remote?.EndPoint.Address + "";
            rd.CreateTime = DateTime.Now;
            rd.CreateIP = ip;

            return rd;
        }
        #endregion

        #region 在线
        /// <summary>登录</summary>
        /// <param name="code"></param>
        /// <param name="name"></param>
        /// <param name="ip"></param>
        /// <returns></returns>
        protected virtual Device Login(String code, String name, String ip, DeviceKinds kind)
        {
            var dv = Device.GetOrAdd(code);
            if (!dv.Enable) throw new Exception($"[{dv.Name}/{dv.Code}]禁止登录");

            if (!name.IsNullOrEmpty())
            {
                // 路由直接覆盖，其它只能在没有时填充
                if (kind == DeviceKinds.Route)
                    dv.Name = name;
                else if (dv.Name.IsNullOrEmpty())
                    dv.Name = name;
            }

            dv.Kind = kind;

            dv.Logins++;
            dv.LastLogin = DateTime.Now;
            dv.LastLoginIP = ip;

            dv.SaveAsync();

            //Device = dv;

            // 登录历史
            WriteHistory("登录", dv);

            return dv;
        }

        protected virtual DeviceOnline GetOnline(String mac)
        {
            var sid = $"{mac}@{Remote.EndPoint}";
            return DeviceOnline.FindBySessionID(sid);
        }

        /// <summary>检查在线</summary>
        /// <returns></returns>
        protected virtual DeviceOnline CreateOnline(String mac, IDevice dv)
        {
            var sid = $"{mac}@{Remote.EndPoint}";
            //var olt = new DeviceOnline
            //{
            //    SessionID = sid,
            //    DeviceID = dv.ID,
            //    Name = dv + "",
            //    Kind = dv.Kind,
            //};
            var olt = DeviceOnline.GetOrAdd(sid);
            olt.DeviceID = dv.ID;
            olt.Name = dv + "";
            olt.Kind = dv.Kind;

            //olt.Insert();
            olt.SaveAsync();

            return olt;
        }
        #endregion

        #region 历史
        protected virtual DeviceHistory WriteHistory(String action, IDevice dv)
        {
            var ip = Remote?.EndPoint.Address + "";
            var hi = new DeviceHistory
            {
                DeviceID = dv.ID,
                Name = dv.Name,
                Action = action,
                Success = true,
                CreateDeviceID = dv.ID,

                CreateTime = DateTime.Now,
                CreateIP = ip,
            };

            hi.SaveAsync();

            return hi;
        }
        #endregion

        #region 距离计算
        public static Double GetDistance(Int32 rssi, Int32 pa = 60, Double pn = 3.3)
        {
            rssi = Math.Abs(rssi);
            var power = (rssi - pa) / (10 * pn);
            return Math.Pow(10, power);
        }
        #endregion
    }
}