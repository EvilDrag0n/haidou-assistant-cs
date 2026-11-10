using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HaiDou
{
    class HexWindow : Form
    {
        readonly PanelForm panel;
        readonly WebView2 web;
        readonly Timer follow;
        readonly Action<Dictionary<string, object>> statusHandler;
        readonly JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        Dictionary<string, object> status;
        IntPtr clientWindow;
        DateTime lastFind = DateTime.MinValue;
        bool ready, manual;
        int suppressedHero;

        [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
        delegate bool EnumWindow(IntPtr hwnd, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr data);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        public HexWindow(PanelForm ownerPanel)
        {
            panel = ownerPanel;
            Text = "海斗助手 · 英雄海克斯";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(390, 700);
            MinimumSize = new Size(330, 360);
            BackColor = Color.FromArgb(15, 17, 21);
            web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(web);
            IntPtr createdHandle = Handle; // 隐藏窗口也要先在 UI 线程建句柄，供异步状态转发。
            statusHandler = UpdateStatus;
            panel.OnStatusForDebug += statusHandler;
            status = Program.Eng.Snapshot();
            follow = new Timer { Interval = 300 };
            follow.Tick += delegate { FollowClient(); };
            follow.Start();
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true; manual = false;
                suppressedHero = Engine.ToInt(Engine.Get(status, "championId"));
                Hide();
            };
            Initialize();
        }

        protected override bool ShowWithoutActivation { get { return !manual; } }

        async void Initialize()
        {
            try
            {
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.DataDir, "webview2"), null);
                await web.EnsureCoreWebView2Async(env);
                if (IsDisposed) return;
                await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(PanelForm.BridgeScript);
                web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                web.CoreWebView2.WebMessageReceived += OnMessage;
                web.CoreWebView2.NavigationCompleted += delegate { ready = true; SendStatus(); };
                web.CoreWebView2.Navigate(new Uri(Path.Combine(Paths.WwwRoot, "hex.html")).AbsoluteUri);
            }
            catch (Exception ex) { Log.Boot("海克斯侧窗初始化失败：" + ex.Message); }
        }
        void UpdateStatus(Dictionary<string, object> next)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)delegate { UpdateStatus(next); }); } catch { }
                return;
            }
            status = next;
            SendStatus();
        }
        void SendStatus()
        {
            if (!ready || IsDisposed || web.CoreWebView2 == null) return;
            web.CoreWebView2.PostWebMessageAsJson(ser.Serialize(new Dictionary<string, object> { { "ev", "status" }, { "data", status } }));
        }
        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> message;
            try { message = ser.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); } catch { return; }
            if (message == null) return;
            Dictionary<string, object> reply = new Dictionary<string, object> { { "id", Engine.Get(message, "id") } };
            try
            {
                reply["data"] = panel.Dispatch(Engine.Str(message, "cmd"), Engine.Get(message, "arg"));
                reply["ok"] = true;
            }
            catch (Exception ex) { reply["ok"] = false; reply["error"] = ex.Message; }
            web.CoreWebView2.PostWebMessageAsJson(ser.Serialize(reply));
        }
        public void OpenManual()
        {
            manual = true; suppressedHero = 0;
            Rectangle area = Screen.FromControl(panel).WorkingArea;
            if (!Visible) Location = new Point(area.Right - Width - 12, Math.Max(area.Top, area.Top + 40));
            Show(); Activate(); SendStatus();
        }

        internal static Rectangle DockBounds(Rectangle client, Rectangle area, Size desired, out string side)
        {
            int width = Math.Min(desired.Width, area.Width), height = Math.Min(desired.Height, area.Height);
            int x;
            if (client.Right + 8 + width <= area.Right) { x = client.Right + 8; side = "右侧吸附"; }
            else if (client.Left - 8 - width >= area.Left) { x = client.Left - 8 - width; side = "左侧吸附"; }
            else { x = area.Right - width; side = "空间不足 · 浮动"; }
            x = Math.Max(area.Left, Math.Min(x, area.Right - width));
            int y = Math.Max(area.Top, Math.Min(client.Top, area.Bottom - height));
            return new Rectangle(x, y, width, height);
        }

        IntPtr FindClient()
        {
            HashSet<uint> ids = new HashSet<uint>();
            foreach (Process p in Process.GetProcessesByName("LeagueClientUx"))
            {
                using (p) { try { ids.Add((uint)p.Id); } catch { } }
            }
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hwnd, IntPtr data)
            {
                uint pid; GetWindowThreadProcessId(hwnd, out pid);
                NativeRect r;
                if (ids.Contains(pid) && IsWindowVisible(hwnd) && GetWindowRect(hwnd, out r) && r.Right - r.Left >= 500 && r.Bottom - r.Top >= 350)
                { found = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        void FollowClient()
        {
            if (IsDisposed) return;
            if (manual)
            {
                if (Engine.Str(status, "phase") != "ChampSelect" || !Program.Eng.Cfg.hexDock) return;
                manual = false;
            }
            if (Engine.Str(status, "phase") != "ChampSelect") { suppressedHero = 0; if (Visible) Hide(); return; }
            int hero = Engine.ToInt(Engine.Get(status, "championId"));
            if (!Program.Eng.Cfg.hexDock || !object.Equals(Engine.Get(status, "isHex"), true) || hero <= 0 || hero == suppressedHero)
            { if (Visible) Hide(); return; }
            try
            {
                if (clientWindow == IntPtr.Zero || !IsWindowVisible(clientWindow))
                {
                    if ((DateTime.UtcNow - lastFind).TotalSeconds < 1) return;
                    lastFind = DateTime.UtcNow; clientWindow = FindClient();
                }
                if (clientWindow == IntPtr.Zero || IsIconic(clientWindow)) { if (Visible) Hide(); return; }
                IntPtr foreground = GetForegroundWindow();
                uint foregroundPid, clientPid;
                GetWindowThreadProcessId(foreground, out foregroundPid);
                GetWindowThreadProcessId(clientWindow, out clientPid);
                if (foregroundPid != clientPid && GetAncestor(foreground, 2) != Handle)
                { if (Visible) Hide(); return; }
                NativeRect r;
                if (!GetWindowRect(clientWindow, out r)) return;
                string side;
                Rectangle target = DockBounds(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom),
                    Screen.FromHandle(clientWindow).WorkingArea, Size, out side);
                if (Bounds != target) Bounds = target;
                Text = "海斗助手 · " + (Engine.Str(status, "champion") ?? "英雄海克斯") + " · " + side;
                if (!Visible) Show();
            }
            catch (Exception ex) { Log.Boot("海克斯吸附失败：" + ex.Message); if (Visible) Hide(); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                follow.Stop(); follow.Dispose();
                panel.OnStatusForDebug -= statusHandler;
            }
            base.Dispose(disposing);
        }
    }
}
