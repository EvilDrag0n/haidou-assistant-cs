using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HaiDou
{
    static class Program
    {
        const string ShowEventName = "HaiDouAssistant-CS-ShowPanel";
        static System.Threading.EventWaitHandle ShowEvent;

        public static Engine Eng;
        public static Form Panel;

        [STAThread]
        static void Main(string[] argv)
        {
            foreach (string a in argv)
            {
                if (a == "--selftest")
                {
                    int code = SelfTest.Run();
                    Environment.ExitCode = code;
                    return;
                }
            }

            bool created;
            using (Mutex mutex = new Mutex(true, "HaiDouAssistant-CS-SingleInstance", out created))
            {
                if (!created)
                {
                    // 已经在跑：通知它把面板叫出来（托盘图标可能在 Win11 的溢出区里不好找）
                    try
                    {
                        using (System.Threading.EventWaitHandle ev = System.Threading.EventWaitHandle.OpenExisting(ShowEventName))
                        {
                            ev.Set();
                        }
                    }
                    catch
                    {
                        MessageBox.Show("海斗助手已经在运行（托盘图标里）。找不带的话，注销后重开或结束进程再启动。", "海斗助手");
                    }
                    return;
                }

                // 第一个实例：起个线程等「唤回」信号
                try
                {
                    ShowEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, ShowEventName);
                    System.Threading.Thread th = new System.Threading.Thread(delegate ()
                    {
                        while (true)
                        {
                            ShowEvent.WaitOne();
                            try
                            {
                                if (Program.Panel != null)
                                    Program.Panel.BeginInvoke((Action)delegate { Program.Panel.Show(); Program.Panel.WindowState = FormWindowState.Normal; Program.Panel.Activate(); });
                            }
                            catch { }
                        }
                    });
                    th.IsBackground = true;
                    th.Start();
                }
                catch { }

                Log.Boot("--- launch pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + " dataDir=" + Paths.DataDir + "（" + Paths.Source + "）");

                AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
                {
                    Log.Boot("FATAL " + e.ExceptionObject);
                };
                Application.ThreadException += delegate (object s, System.Threading.ThreadExceptionEventArgs e)
                {
                    Log.Boot("UI 异常 " + e.Exception);
                };
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

                try
                {
                    Maps.Load();
                    Log.Boot("数据加载完成：英雄 " + Maps.IdToName.Count + " / 方案 " + Maps.Builds.Count);

                    Config cfg = Config.Load();
                    Log.Boot("配置加载完成：autoAccept=" + cfg.autoAccept);
                    Eng = new Engine(cfg);

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new PanelForm());
                    Eng.Stop();
                    Log.Boot("正常退出");
                }
                catch (Exception ex)
                {
                    Log.Boot("启动失败：" + ex);
                    try { MessageBox.Show("启动失败：\n" + ex.Message, "海斗助手"); } catch { }
                }

                mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>面板窗口：WinForms 外壳 + 系统 WebView2 渲染原有 HTML</summary>
    class PanelForm : Form
    {
        WebView2 web;
        GoldOverlay overlay;
        TrayIcon tray;
        HexWindow hexWindow;
        readonly List<Dictionary<string, object>> pending = new List<Dictionary<string, object>>();
        bool webReady = false;
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();

        public PanelForm()
        {
            Text = "海克斯大乱斗助手";
            Width = 480; Height = 720;
            MinimumSize = new Size(420, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(15, 17, 21);
            Icon = LoadIcon();

            web = new WebView2();
            web.Dock = DockStyle.Fill;
            Controls.Add(web);

            overlay = new GoldOverlay();
            tray = new TrayIcon(this);

            HookEngine();
            hexWindow = new HexWindow(this);

            web.CoreWebView2InitializationCompleted += OnWebReady;
            InitWebView();

            // 最小化 = 收进托盘（托盘有图标，双击恢复）
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    Hide();
                    tray.NotifyHidden();
                }
            };

            FormClosing += delegate (object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                    tray.NotifyHidden();
                }
            };

            Program.Eng.Start();
            Program.Panel = this;
        }

        static Icon LoadIcon()
        {
            try
            {
                string p = Path.Combine(Paths.ExeDir, "app.ico");
                if (File.Exists(p)) return new Icon(p);
            }
            catch { }
            return SystemIcons.Application;
        }

        public event System.Action<Dictionary<string, object>> OnStatusForDebug;

        void HookEngine()
        {
            Program.Eng.LogAdded += delegate (string level, string text)
            {
                Dictionary<string, object> e = new Dictionary<string, object>
                {
                    { "level", level }, { "text", text },
                    { "at", DateTimeOffset.Now.ToUnixTimeMilliseconds() },
                };
                Post("log", e);
            };
            Program.Eng.StatusChanged += delegate (Dictionary<string, object> st)
            {
                Post("status", st);
                if (OnStatusForDebug != null) { try { OnStatusForDebug(st); } catch { } }
            };
            Program.Eng.GoldVisible += delegate (bool visible, Dictionary<string, object> data)
            {
                if (this.InvokeRequired) { this.BeginInvoke((Action)delegate { OnGold(visible, data); }); return; }
                OnGold(visible, data);
            };
        }

        void OnGold(bool visible, Dictionary<string, object> data)
        {
            if (visible && data != null)
            {
                overlay.SetData(Eng2(data, "blueText"), Eng2(data, "diffText"), Eng2(data, "redText"), Engine.ToInt(Engine.Get(data, "diff")) > 0);
                overlay.ShowNoActivate();
            }
            else overlay.Hide();
        }

        static string Eng2(Dictionary<string, object> d, string k)
        {
            object v = Engine.Get(d, k);
            return v == null ? "--" : v.ToString();
        }

        void Post(string ev, object data)
        {
            // 注意：CoreWebView2 只能在 UI 线程访问，必须先判断 InvokeRequired 再碰它，
            // 否则后台定时器一调用就抛 "CoreWebView2 can only be accessed from the UI thread"
            if (!IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)delegate { Post(ev, data); }); } catch { }
                return;
            }
            if (!webReady || web.CoreWebView2 == null) return;
            Dictionary<string, object> msg = new Dictionary<string, object> { { "ev", ev }, { "data", data } };
            try { web.CoreWebView2.PostWebMessageAsJson(Ser.Serialize(msg)); } catch { }
        }

        /// <summary>把 WebView2 的用户数据也放进 hxdou-data（否则会在 exe 旁边建一个巨大的缓存目录）</summary>
        async void InitWebView()
        {
            try
            {
                string udf = Path.Combine(Paths.DataDir, "webview2");
                Directory.CreateDirectory(udf);
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, udf, null);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                Log.Boot("WebView2 初始化失败：" + ex.Message);
                MessageBox.Show("WebView2 初始化失败：\n" + ex.Message +
                    "\n\n这台电脑缺少 WebView2 运行时。Win11 自带；Win10 需要装一次（搜 “WebView2 Runtime”）。",
                    "海斗助手");
            }
        }

        async void OnWebReady(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                MessageBox.Show("WebView2 初始化失败：" + e.InitializationException);
                return;
            }
            try
            {
                // 注入桥：让原有 HTML 里的 window.hx / window.debug 能用
                await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);
                web.CoreWebView2.WebMessageReceived += OnWebMessage;
                webReady = true;

                string page = Path.Combine(Paths.WwwRoot, "panel.html");
                if (!File.Exists(page))
                {
                    MessageBox.Show("找不到界面文件：\n" + page);
                    return;
                }
                web.CoreWebView2.Navigate(new Uri(page).AbsoluteUri);
            }
            catch (Exception ex) { MessageBox.Show("桥接初始化失败：" + ex.Message); }
        }

        void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> msg;
            try { msg = Ser.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); }
            catch { return; }
            if (msg == null) return;

            object idObj = Engine.Get(msg, "id");
            string cmd = Engine.Str(msg, "cmd");
            object arg = Engine.Get(msg, "arg");

            Dictionary<string, object> reply = new Dictionary<string, object>();
            reply["id"] = idObj;
            try
            {
                reply["ok"] = true;
                reply["data"] = Dispatch(cmd, arg);
            }
            catch (Exception ex)
            {
                reply["ok"] = false;
                reply["error"] = ex.Message;
            }
            try { web.CoreWebView2.PostWebMessageAsJson(Ser.Serialize(reply)); } catch { }
        }

        DebugWindow debugWin;

        public void OpenDebugWindow()
        {
            if (debugWin != null && !debugWin.IsDisposed)
            {
                debugWin.Show();
                debugWin.Activate();
                return;
            }
            debugWin = new DebugWindow(this);
            debugWin.Show();
        }

        public object Dispatch(string cmd, object arg)
        {
            Engine eng = Program.Eng;
            switch (cmd)
            {
                case "bootstrap":
                    return new Dictionary<string, object>
                    {
                        { "status", eng.Snapshot() },
                        { "version", "1.3.0" },
                        { "logs", new List<object>() },
                        { "dataDir", Paths.DataDir },
                        { "configPath", Paths.ConfigFile },
                    };

                case "config:set":
                {
                    Dictionary<string, object> patch = arg as Dictionary<string, object>;
                    if (patch != null) ApplyPatch(eng.Cfg, patch);
                    string err;
                    if (!eng.Cfg.Save(out err))
                    {
                        eng.Log("error", "设置保存失败：" + err);
                        return new Dictionary<string, object>
                        {
                            { "ok", false }, { "error", err }, { "path", Paths.ConfigFile }, { "config", eng.Cfg },
                        };
                    }
                    NormalizeThresholds(eng.Cfg);
                    eng.ReapplyRanks();
                    eng.RefreshHex(false);
                    eng.Push();
                    return new Dictionary<string, object> { { "ok", true }, { "config", eng.Cfg } };
                }

                case "action:cleanup": return eng.CleanupAll();
                case "action:hex-refresh":
                    eng.RefreshHex(true);
                    return true;
                case "action:hex-window":
                    if (InvokeRequired) BeginInvoke((Action)delegate { hexWindow.OpenManual(); });
                    else hexWindow.OpenManual();
                    return true;
                case "action:reconnect":
                    eng.Log("info", "手动重连客户端");
                    return true;
                case "action:send-chat":
                    return eng.SendChatReport(false);
                case "action:test-gold":
                    if (this.InvokeRequired) { this.BeginInvoke((Action)delegate { TestGold(); }); }
                    else TestGold();
                    return true;
                case "action:open-config":
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + Paths.ConfigFile + "\"");
                    return true;
                }
                case "action:open-data":
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + Paths.DataDir + "\"");
                    return Paths.DataDir;
                case "action:accept": return Wrap(DebugProbe.Accept(Program.Eng));
                case "action:open-folder":
                {
                    string p = arg == null ? "" : arg.ToString();
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                        System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + p + "\"");
                    else
                        System.Diagnostics.Process.Start("explorer.exe", "\"" + Paths.DataDir + "\"");
                    return true;
                }
                case "action:open-debug":
                {
                    if (InvokeRequired) { BeginInvoke((Action)OpenDebugWindow); }
                    else OpenDebugWindow();
                    return true;
                }
                case "action:detect-lol":
                {
                    string found = Maps.FindLolPath(eng.Lcu != null ? eng.Lcu.ExePath : null);
                    List<Dictionary<string, object>> chain = Maps.DetectChain(eng.Lcu != null ? eng.Lcu.ExePath : null);
                    return new Dictionary<string, object>
                    {
                        { "ok", found != null }, { "path", found }, { "by", found != null ? "自动探测" : null },
                        { "tried", chain.Count > 8 ? chain.GetRange(0, 8) : chain },
                    };
                }
                case "debug:run": return DebugRun(Engine.Str(new Dictionary<string, object> { { "c", arg == null ? "" : arg.ToString() } }, "c"));
                default:
                    throw new Exception("未知指令：" + cmd);
            }
        }

        void TestGold()
        {
            Dictionary<string, object> demo = new Dictionary<string, object>
            {
                { "blueText", "48.2K" }, { "diffText", "+4.3K" }, { "redText", "43.9K" }, { "diff", 4330 },
            };
            OnGold(true, demo);
            System.Threading.Timer t = null;
            t = new System.Threading.Timer(delegate { try { this.BeginInvoke((Action)delegate { overlay.Hide(); }); } catch { } finally { if (t != null) t.Dispose(); } }, null, 4000, Timeout.Infinite);
        }

        object DebugRun(string action)
        {
            switch (action)
            {
                case "connect":
                {
                    bool ok = Program.Eng.Lcu != null;
                    string text;
                    if (ok)
                    {
                        text = string.Join(Environment.NewLine, new string[]
                        {
                            "端口 " + Program.Eng.Lcu.Port,
                            "客户端路径 " + (Program.Eng.Lcu.ExePath ?? "(未知)"),
                            "阶段 " + Engine.PhaseText(Engine.Str(Program.Eng.Snapshot(), "phase")),
                            "WSS " + Program.Eng.WsState,
                        });
                    }
                    else text = "还没连上客户端。先开英雄联盟客户端，再点一次。";
                    return new Dictionary<string, object> { { "ok", ok }, { "title", "连接自检" }, { "text", text } };
                }
                case "globals": return Wrap(DebugProbe.Globals(Program.Eng));
                case "champselect": return Wrap(DebugProbe.ChampSelect(Program.Eng));
                case "teammates": return Wrap(DebugProbe.Teammates(Program.Eng));
                case "live": return Wrap(DebugProbe.Live(Program.Eng));
                case "readycheck": return Wrap(DebugProbe.ReadyCheck(Program.Eng));
                case "accept": return Wrap(DebugProbe.Accept(Program.Eng));
                case "swagger": return Wrap(DebugProbe.Swagger(Program.Eng));
                case "recommend": return new Dictionary<string, object> { { "ok", false }, { "text", "推荐出装已移除" } };
                case "env": return Wrap(DebugProbe.Env(Program.Eng));
                case "lolpath":
                {
                    string exe = Program.Eng.Lcu != null ? Program.Eng.Lcu.ExePath : null;
                    string found = Maps.FindLolPath(exe);
                    List<Dictionary<string, object>> chain = Maps.DetectChain(exe);
                    List<string> lines = new List<string>();
                    foreach (Dictionary<string, object> r in chain)
                    {
                        object okObj = Engine.Get(r, "ok");
                        bool isOk = okObj is bool && (bool)okObj;
                        lines.Add((isOk ? "✅ " : "   ") + "[" + Engine.Str(r, "source") + "] " + Engine.Str(r, "path"));
                        if (isOk) break;
                    }
                    string head = found != null
                        ? "找到：" + found
                        : "没找到。请在面板「路径」里手动填，例如 D:\\WeGameApps\\英雄联盟";
                    lines.Insert(0, "");
                    lines.Insert(0, head);
                    return new Dictionary<string, object>
                    {
                        { "ok", found != null }, { "title", "定位 LoL 目录" },
                        { "text", string.Join(Environment.NewLine, lines.ToArray()) },
                    };
                }
                case "chattest":
                {
                    Dictionary<string, object> r = Program.Eng.SendChatReport(false);
                    List<string> lines = new List<string> { "每个通道的结果：", "" };
                    List<object> attempts = r.ContainsKey("attempts") ? r["attempts"] as List<object> : null;
                    if (attempts != null)
                    {
                        foreach (object a in attempts)
                        {
                            Dictionary<string, object> d = a as Dictionary<string, object>;
                            if (d == null) continue;
                            bool aok = Engine.Get(d, "ok") is bool && (bool)Engine.Get(d, "ok");
                            lines.Add("  " + (aok ? "✅ " : "❌ ") + Engine.Str(d, "level") +
                                (aok ? "" : "  (" + (Engine.Get(d, "status") ?? "") + " " + (Engine.Str(d, "error") ?? "") + ")"));
                        }
                    }
                    bool sent = r["ok"] is bool && (bool)r["ok"];
                    lines.Add("");
                    lines.Add(sent ? "✅ 成功通道：" + r["level"] : "❌ 全部通道都失败。把这段原样发回去。");
                    return new Dictionary<string, object>
                    {
                        { "ok", sent }, { "title", "发送测试消息" },
                        { "text", string.Join(Environment.NewLine, lines.ToArray()) }, { "raw", attempts },
                    };
                }
                case "gold": return new Dictionary<string, object> { { "ok", true }, { "title", "经济差浮层" }, { "text", "已请求预览（4 秒后自动隐藏）。" } };
                case "cleanup": return new Dictionary<string, object> { { "ok", true }, { "title", "清理出装文件" }, { "text", "已清理 " + Program.Eng.CleanupAll() + " 个。" } };
                case "openProbe":
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + Paths.DataDir + "\"");
                    return new Dictionary<string, object> { { "ok", true }, { "title", "探测输出目录" }, { "text", Paths.DataDir } };
                case "export": return ExportDiag();
                default: return new Dictionary<string, object> { { "ok", false }, { "title", "未知动作" }, { "text", string.IsNullOrEmpty(action) ? "(空)" : action } };
            }
        }

        static Dictionary<string, object> Wrap(DebugProbe.Result r)
        {
            return new Dictionary<string, object>
            {
                { "ok", r.ok }, { "title", r.title }, { "text", r.text }, { "raw", r.raw },
            };
        }
        object ExportDiag()
        {
            try
            {
                string liveError;
                List<Dictionary<string, object>> livePlayers = Program.Eng.LivePlayerList(out liveError);
                Dictionary<string, object> payload = new Dictionary<string, object>
                {
                    { "env", new Dictionary<string, object>
                        {
                            { "appVersion", "1.3.0-cs" },
                            { "dataDir", Paths.DataDir },
                            { "dataSource", Paths.Source },
                            { "configPath", Paths.ConfigFile },
                            { "os", Environment.OSVersion.ToString() },
                            { "clr", Environment.Version.ToString() },
                            { "webView2", web.CoreWebView2 != null ? "ok" : "unknown" },
                        }
                    },
                    { "status", Program.Eng.Snapshot() },
                    { "livePlayers", livePlayers },
                    { "liveError", liveError },
                    { "clientExe", Program.Eng.Lcu != null ? Program.Eng.Lcu.ExePath : null },
                    { "lolDetect", Maps.DetectChain(Program.Eng.Lcu != null ? Program.Eng.Lcu.ExePath : null) },
                };
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
                string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "hxdou-cs-diag-" + stamp + ".json");
                File.WriteAllText(file, Ser.Serialize(payload), Encoding.UTF8);
                return new Dictionary<string, object>
                {
                    { "ok", true }, { "title", "导出诊断" },
                    { "text", "已写到桌面：\n" + file }, { "paths", new Dictionary<string, object> { { "json", file } } },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "ok", false }, { "title", "导出诊断" }, { "text", "失败：" + ex.Message } };
            }
        }

        static void NormalizeThresholds(Config c)
        {
            if (c.rankTop > 100) c.rankTop = 100;
            if (c.rankMid > 100) c.rankMid = 100;
            if (c.rankLow > 100) c.rankLow = 100;
            if (c.rankTop < 0) c.rankTop = 0;
            if (c.rankMid < 0) c.rankMid = 0;
            if (c.rankLow < 0) c.rankLow = 0;
            // 必须严格递减：top > mid > low
            if (c.rankMid >= c.rankTop) c.rankMid = Math.Max(0, c.rankTop - 1);
            if (c.rankLow >= c.rankMid) c.rankLow = Math.Max(0, c.rankMid - 1);
            if (c.autoAcceptDelayMin < 0) c.autoAcceptDelayMin = 0;
            if (c.autoAcceptDelayMax < 0) c.autoAcceptDelayMax = 0;
            if (c.autoAcceptDelayMax < c.autoAcceptDelayMin)
            {
                int t = c.autoAcceptDelayMin; c.autoAcceptDelayMin = c.autoAcceptDelayMax; c.autoAcceptDelayMax = t;
            }
        }

        static void ApplyPatch(Config c, Dictionary<string, object> patch)
        {
            foreach (KeyValuePair<string, object> kv in patch)
            {
                object v = kv.Value;
                switch (kv.Key)
                {
                    case "autoAccept": c.autoAccept = v is bool && (bool)v; break;
                    case "enableRecommend": c.enableRecommend = false; break;
                    case "enableGoldDiff": c.enableGoldDiff = v is bool && (bool)v; break;
                    case "chatReport": c.chatReport = v is bool && (bool)v; break;
                    case "chatPrefix": c.chatPrefix = v == null ? "" : v.ToString(); break;
                    case "chatIncludeSelf": c.chatIncludeSelf = v is bool && (bool)v; break;
                    case "lolPath": c.lolPath = v == null ? "" : v.ToString(); break;
                    case "panelAlwaysOnTop": c.panelAlwaysOnTop = v is bool && (bool)v; break;
                    case "hexDock": c.hexDock = v is bool && (bool)v; break;
                    case "hexDataset": c.hexDataset = v != null && v.ToString() == "all" ? "all" : "high"; break;
                    case "hexMinSamples": c.hexMinSamples = Math.Max(1, Math.Min(10000, Engine.ToInt(v))); break;
                    case "autoAcceptDelayMin": c.autoAcceptDelayMin = Engine.ToInt(v); break;
                    case "autoAcceptDelayMax": c.autoAcceptDelayMax = Engine.ToInt(v); break;
                    case "rankTop": c.rankTop = Engine.ToInt(v); break;
                    case "rankMid": c.rankMid = Engine.ToInt(v); break;
                    case "rankLow": c.rankLow = Engine.ToInt(v); break;
                }
            }
        }

        internal const string BridgeScript = @"
(function(){
  var seq = 0, pending = {}, subs = {status: [], log: []};
  function call(cmd, arg){
    return new Promise(function(resolve, reject){
      var id = ++seq;
      pending[id] = {resolve: resolve, reject: reject};
      window.chrome.webview.postMessage({id: id, cmd: cmd, arg: arg});
    });
  }
  window.chrome.webview.addEventListener('message', function(ev){
    var m = ev.data;
    if (!m) return;
    if (m.ev) { (subs[m.ev] || []).forEach(function(cb){ try{ cb(m.data); }catch(e){} }); return; }
    var p = pending[m.id];
    if (!p) return;
    delete pending[m.id];
    if (m.ok) p.resolve(m.data); else p.reject(new Error(m.error || 'error'));
  });
  window.hx = {
    getBootstrap: function(){ return call('bootstrap'); },
    setConfig: function(patch){ return call('config:set', patch); },
    cleanup: function(){ return call('action:cleanup'); },
    reconnect: function(){ return call('action:reconnect'); },
    openConfig: function(){ return call('action:open-config'); },
    openDebug: function(){ return call('action:open-debug'); },
    openData: function(){ return call('action:open-data'); },
    detectLol: function(){ return call('action:detect-lol'); },
    sendChat: function(){ return call('action:send-chat'); },
    refreshHex: function(){ return call('action:hex-refresh'); },
    openHex: function(){ return call('action:hex-window'); },
    testGold: function(){ return call('action:test-gold'); },
    onStatus: function(cb){ subs.status.push(cb); return function(){}; },
    onLog: function(cb){ subs.log.push(cb); return function(){}; }
  };
  window.debug = {
    bootstrap: function(){ return call('bootstrap'); },
    run: function(action){ return call('debug:run', action); },
    accept: function(){ return call('action:accept'); },
    goldPreview: function(){ return call('action:test-gold'); },
    openFolder: function(p){ return call('action:open-folder', p); },
    onStatus: function(cb){ subs.status.push(cb); return function(){}; }
  };
})();
";
    }
}
