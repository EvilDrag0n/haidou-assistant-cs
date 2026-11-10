using System;
using System.IO;
using System.Management;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HaiDou
{
    /// <summary>LCU（英雄联盟客户端本地接口）连接与请求</summary>
    class LcuClient
    {
        public int Port { get; private set; }
        public string Token { get; private set; }
        public string ExePath { get; private set; }
        public string BaseUrl { get { return "https://127.0.0.1:" + Port; } }

        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();

        static LcuClient()
        {
            // LCU 是自签证书
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
        }

        /// <summary>从客户端进程命令行里拿 --app-port / --remoting-auth-token</summary>
        public static LcuClient Connect()
        {
            string cmdline = null;
            string exePath = null;

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ExecutablePath, CommandLine FROM Win32_Process WHERE Name='LeagueClientUx.exe'"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        object c = mo["CommandLine"];
                        object e = mo["ExecutablePath"];
                        if (c != null) cmdline = c.ToString();
                        if (e != null) exePath = e.ToString();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("无法查询客户端进程（" + ex.Message + "）");
            }

            if (string.IsNullOrEmpty(cmdline))
                throw new Exception("客户端未启动");

            Match mp = Regex.Match(cmdline, @"--app-port=(\d+)");
            Match mt = Regex.Match(cmdline, @"--remoting-auth-token=([^\s""]+)");
            if (!mp.Success || !mt.Success)
                throw new Exception("命令行里没有 app-port / remoting-auth-token");

            LcuClient c2 = new LcuClient();
            c2.Port = int.Parse(mp.Groups[1].Value);
            c2.Token = mt.Groups[1].Value;
            c2.ExePath = exePath;
            return c2;
        }

        string AuthHeader()
        {
            string raw = "riot:" + Token;
            return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        }

        /// <summary>GET，返回原始 JSON 文本；非 2xx 抛异常（带 StatusCode）</summary>
        public virtual string Get(string path, int timeoutMs = 4000)
        {
            return Request("GET", path, null, timeoutMs);
        }

        public virtual string Post(string path, object body, int timeoutMs = 4000)
        {
            return Request("POST", path, body, timeoutMs);
        }

        string Request(string method, string path, object body, int timeoutMs)
        {
            string url = BaseUrl + path;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Headers["Authorization"] = AuthHeader();
            req.Accept = "application/json";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.KeepAlive = false;
            req.Proxy = null;
            req.ServicePoint.ConnectionLimit = 8;
            req.AutomaticDecompression = DecompressionMethods.GZip;

            if (body != null)
            {
                byte[] data = Encoding.UTF8.GetBytes(Ser.Serialize(body));
                req.ContentType = "application/json";
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) { s.Write(data, 0, data.Length); }
            }

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    return sr.ReadToEnd();
                }
            }
            catch (WebException we)
            {
                HttpWebResponse r = we.Response as HttpWebResponse;
                if (r != null)
                {
                    int code = (int)r.StatusCode;
                    r.Close();
                    throw new LcuException("LCU " + method + " " + path + " -> " + code, code);
                }
                throw;   // 网络层错误（无 status）保留原样，用于判断"是否真的掉线"
            }
        }

        /// <summary>GET 并反序列化成字典（失败返回 null）</summary>
        public System.Collections.Generic.Dictionary<string, object> GetObject(string path, int timeoutMs = 4000)
        {
            try { return Ser.Deserialize<System.Collections.Generic.Dictionary<string, object>>(Get(path, timeoutMs)); }
            catch { return null; }
        }

        public string GameflowJson() { return Get("/lol-gameflow/v1/session"); }
        public string ChampSelectJson() { return Get("/lol-champ-select/v1/session"); }
    }

    /// <summary>带 HTTP 状态码的 LCU 异常——用来区分"客户端活着但资源不存在"和"真的掉线"</summary>
    class LcuException : Exception
    {
        public int Status { get; private set; }
        public LcuException(string msg, int status) : base(msg) { Status = status; }
    }
}
