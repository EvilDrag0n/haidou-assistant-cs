using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace HaiDou
{
    /// <summary>路径解析：数据目录跟着程序走（同 Electron 版的规则）</summary>
    static class Paths
    {
        public static string ExeDir
        {
            get { return Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location); }
        }

        public static string DataDir { get; private set; }
        public static string Source { get; private set; }

        static Paths()
        {
            string arg = null;
            string[] argv = Environment.GetCommandLineArgs();
            foreach (string a in argv)
            {
                if (a.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    arg = a.Substring("--data-dir=".Length).Trim('"');
                }
            }

            string env = Environment.GetEnvironmentVariable("HXDOU_DATA_DIR");
            string candidate;
            if (!string.IsNullOrEmpty(arg)) { candidate = Path.GetFullPath(arg); Source = "--data-dir"; }
            else if (!string.IsNullOrEmpty(env)) { candidate = Path.GetFullPath(env.Trim('"')); Source = "HXDOU_DATA_DIR"; }
            else { candidate = Path.Combine(ExeDir, "hxdou-data"); Source = "exe 同目录"; }

            if (EnsureWritable(candidate))
            {
                DataDir = candidate;
            }
            else
            {
                // 写不进去就退回 %APPDATA%
                string appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hxdou");
                DataDir = appData;
                Source = Source + "（不可写，退回 %APPDATA%）";
                EnsureWritable(appData);
            }
        }

        static bool EnsureWritable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static string ConfigFile { get { return Path.Combine(DataDir, "config.json"); } }
        public static string BootLog { get { return Path.Combine(DataDir, "hxdou-startup.log"); } }
        public static string WwwRoot { get { return Path.Combine(ExeDir, "wwwroot"); } }
        public static string DataFolder { get { return Path.Combine(ExeDir, "data"); } }
    }

    /// <summary>启动日志（带轮转，超 64KB 截断）</summary>
    static class Log
    {
        static readonly object gate = new object();

        public static void Boot(string msg)
        {
            try
            {
                lock (gate)
                {
                    FileInfo fi = new FileInfo(Paths.BootLog);
                    if (fi.Exists && fi.Length > 64 * 1024)
                    {
                        string all = File.ReadAllText(Paths.BootLog);
                        string keep = all.Length > 32 * 1024 ? all.Substring(all.Length - 32 * 1024) : all;
                        File.WriteAllText(Paths.BootLog, "[truncated]\r\n" + keep);
                    }
                    File.AppendAllText(Paths.BootLog,
                        "[" + DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff") + "] " + msg + "\r\n",
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    /// <summary>配置（字段与 Electron 版一致，方便你直接换用）</summary>
    class Config
    {
        public bool autoAccept = false;
        public int autoAcceptDelayMin = 800;
        public int autoAcceptDelayMax = 1500;
        public bool enableRecommend = false; // 旧配置字段保留；推荐写入已移除
        public bool enableGoldDiff = true;
        public bool chatReport = false;
        public string chatPrefix = "";
        public bool chatIncludeSelf = false;
        public string goldDiffKey = "Tab";
        public int[] queues = new int[] { 2400, 2410, 3270 };
        public string lolPath = "";
        public bool panelAlwaysOnTop = false;
        public bool hexDock = true;
        public string hexDataset = "high";
        public int hexMinSamples = 100;
        // 马评分档下限（默认 70/40/20：上等 >=70，中等 40~69，下等 20~39，劣等 <20）
        public int rankTop = 70;
        public int rankMid = 40;
        public int rankLow = 20;
        public bool disableGpu = false;
        public int logLimit = 300;

        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();

        public static Config Load()
        {
            try
            {
                if (File.Exists(Paths.ConfigFile))
                {
                    string json = File.ReadAllText(Paths.ConfigFile, Encoding.UTF8);
                    Config c = Ser.Deserialize<Config>(json);
                    if (c != null)
                    {
                        c.enableRecommend = false;
                        c.hexDataset = c.hexDataset == "all" ? "all" : "high";
                        if (c.hexMinSamples < 1 || c.hexMinSamples > 10000) c.hexMinSamples = 100;
                        if (c.queues == null || c.queues.Length == 0) c.queues = new int[] { 2400, 2410, 3270 };
                        return c;
                    }
                }
            }
            catch (Exception ex) { Log.Boot("config 读取失败：" + ex.Message); }
            return new Config();
        }

        public bool Save(out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                File.WriteAllText(Paths.ConfigFile, Ser.Serialize(this), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool QueueAllowed(int qid)
        {
            if (queues == null) return false;
            foreach (int q in queues) if (q == qid) return true;
            return false;
        }
    }
}
