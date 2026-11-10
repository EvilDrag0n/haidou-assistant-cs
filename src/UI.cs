using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace HaiDou
{
    /// <summary>经济差浮层：透明 + 置顶 + 点击穿透 + 不抢焦点，按住 Tab 出现</summary>
    class GoldOverlay : Form
    {
        string blue = "--", diff = "--", red = "--";
        bool ahead = false;
        readonly System.Windows.Forms.Timer keyTimer;

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint flags);
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        const int VK_TAB = 0x09;

        bool lastDown = false;

        public GoldOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(360, 92);
            BackColor = Color.FromArgb(10, 13, 18);
            DoubleBuffered = true;

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            // 放在屏幕上方居中，避免遮挡下方的海克斯详情。
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + 80);

            keyTimer = new System.Windows.Forms.Timer();
            keyTimer.Interval = 40;
            keyTimer.Tick += delegate { PollTab(); };
            keyTimer.Start();
        }

        void PollTab()
        {
            if (Program.Eng == null) return;
            bool down = (GetAsyncKeyState(VK_TAB) & 0x8000) != 0;
            if (down == lastDown) return;
            lastDown = down;
            if (down)
            {
                if (!Program.Eng.Cfg.enableGoldDiff) return;
                Program.Eng.SetGoldVisible(true);
                if (Program.Eng.GoldData() == null)
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate { Program.Eng.ForceGoldRefresh(); });
            }
            else Program.Eng.SetGoldVisible(false);
        }

        static string S(Dictionary<string, object> d, string k)
        {
            object v = Engine.Get(d, k);
            return v == null ? "--" : v.ToString();
        }

        public void SetData(string b, string df, string r, bool isAhead)
        {
            blue = b; diff = df; red = r; ahead = isAhead;
            if (InvokeRequired) { BeginInvoke((Action)Invalidate); return; }
            Invalidate();
        }

        public void ShowNoActivate()
        {
            if (InvokeRequired) { BeginInvoke((Action)ShowNoActivate); return; }
            Show();
            // 面板窗口也是普通窗口，光设 TopMost 有时会被压在下面；显式提到最顶层且不抢焦点
            try
            {
                // 光设 TopMost 会被同进程的普通窗口压住，必须显式置顶 + 刷一次 z-order
                TopMost = false; TopMost = true;
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            catch (Exception ex) { Log.Boot("overlay ShowNoActivate 异常：" + ex.Message); }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT 点击穿透
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW 不进任务栏
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE 不抢焦点
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (SolidBrush bg = new SolidBrush(Color.FromArgb(235, 10, 13, 18)))
            using (Pen border = new Pen(Color.FromArgb(60, 120, 140, 180)))
            {
                Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath path = Round(r, 12)) { g.FillPath(bg, path); g.DrawPath(border, path); }
            }

            using (Font fLabel = new Font("Microsoft YaHei UI", 9f))
            using (Font fBig = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold))
            using (Brush bBlue = new SolidBrush(Color.FromArgb(106, 168, 255)))
            using (Brush bDiff = new SolidBrush(ahead ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113)))
            using (Brush bRed = new SolidBrush(Color.FromArgb(255, 122, 114)))
            using (Brush bDim = new SolidBrush(Color.FromArgb(125, 135, 152)))
            {
                StringFormat c = new StringFormat { Alignment = StringAlignment.Center };
                g.DrawString("蓝方", fLabel, bDim, new RectangleF(0, 12, 100, 16), c);
                g.DrawString(blue, fBig, bBlue, new RectangleF(0, 32, 100, 32), c);
                g.DrawString("经济差", fLabel, bDim, new RectangleF(130, 12, 100, 16), c);
                g.DrawString(diff, fBig, bDiff, new RectangleF(130, 32, 100, 32), c);
                g.DrawString("红方", fLabel, bDim, new RectangleF(260, 12, 100, 16), c);
                g.DrawString(red, fBig, bRed, new RectangleF(260, 32, 100, 32), c);
            }
        }

        static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>托盘图标与菜单</summary>
    class TrayIcon
    {
        readonly NotifyIcon icon;
        readonly Form panel;
        bool warned;

        public TrayIcon(Form panelForm)
        {
            panel = panelForm;
            icon = new NotifyIcon();
            icon.Icon = LoadAppIcon();
            icon.Text = "海克斯大乱斗助手";   // 63 字符以内
            icon.Visible = true;
            icon.DoubleClick += delegate { Restore(); };
            icon.MouseClick += delegate (object s2, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) Restore();
            };
            Rebuild();
        }

        static Icon LoadAppIcon()
        {
            try
            {
                string p = Path.Combine(Paths.ExeDir, "app.ico");
                if (File.Exists(p)) return new Icon(p);
            }
            catch { }
            return SystemIcons.Application;
        }

        public void NotifyHidden()
        {
            if (warned) return;
            warned = true;
            try { icon.ShowBalloonTip(2000, "海斗助手", "已最小化到托盘，双击图标可恢复。", ToolTipIcon.Info); } catch { }
        }

        void Restore()
        {
            panel.Show();
            panel.WindowState = FormWindowState.Normal;
            panel.Activate();
        }

        public void Rebuild()
        {
            Config c = Program.Eng.Cfg;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示面板", null, delegate { Restore(); });
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem auto = new ToolStripMenuItem("自动接受对局");
            auto.Checked = c.autoAccept;
            auto.Click += delegate { c.autoAccept = !c.autoAccept; string e; c.Save(out e); Rebuild(); };
            menu.Items.Add(auto);

            ToolStripMenuItem gold = new ToolStripMenuItem("按住 Tab 显示经济差");
            gold.Checked = c.enableGoldDiff;
            gold.Click += delegate { c.enableGoldDiff = !c.enableGoldDiff; string e; c.Save(out e); Rebuild(); };
            menu.Items.Add(gold);

            ToolStripMenuItem chat = new ToolStripMenuItem("选人阶段发送队友战绩");
            chat.Checked = c.chatReport;
            chat.Click += delegate { c.chatReport = !c.chatReport; string e; c.Save(out e); Rebuild(); };
            menu.Items.Add(chat);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("清理出装文件", null, delegate { Program.Eng.CleanupAll(); });
            menu.Items.Add("退出", null, delegate
            {
                icon.Visible = false;
                Application.Exit();
            });

            icon.ContextMenuStrip = menu;
        }
    }
}

namespace HaiDou
{
    /// <summary>调试页：同一个 WebView2 桥，直接跑 wwwroot\debug.html</summary>
    class DebugWindow : Form
    {
        readonly WebView2 web;
        readonly PanelForm owner;
        static readonly System.Web.Script.Serialization.JavaScriptSerializer Ser =
            new System.Web.Script.Serialization.JavaScriptSerializer();

        public DebugWindow(PanelForm ownerForm)
        {
            owner = ownerForm;
            Text = "海斗助手 · 调试页";
            Width = 760; Height = 800;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = System.Drawing.Color.FromArgb(15, 17, 21);

            web = new WebView2();
            web.Dock = DockStyle.Fill;
            Controls.Add(web);

            web.CoreWebView2InitializationCompleted += delegate(object s, Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
            {
                if (!e.IsSuccess)
                {
                    MessageBox.Show("WebView2 初始化失败：" + e.InitializationException);
                    return;
                }
                Init(e);
            };
            InitAsync();
        }

        async void InitAsync()
        {
            try
            {
                string udf = System.IO.Path.Combine(Paths.DataDir, "webview2-debug");
                System.IO.Directory.CreateDirectory(udf);
                Microsoft.Web.WebView2.Core.CoreWebView2Environment env =
                    await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, udf, null);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                MessageBox.Show("WebView2 初始化失败：" + ex.Message);
            }
        }

        async void Init(Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
        {
            try
            {
                await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(PanelForm.BridgeScript);
                web.CoreWebView2.WebMessageReceived += OnMessage;

                // 状态推送：转发给调试页
                owner.OnStatusForDebug += delegate(System.Collections.Generic.Dictionary<string, object> st)
                {
                    try
                    {
                        System.Collections.Generic.Dictionary<string, object> msg =
                            new System.Collections.Generic.Dictionary<string, object> { { "ev", "status" }, { "data", st } };
                        if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(Ser.Serialize(msg));
                    }
                    catch { }
                };

                string page = System.IO.Path.Combine(Paths.WwwRoot, "debug.html");
                web.CoreWebView2.Navigate(new Uri(page).AbsoluteUri);
            }
            catch (Exception ex) { MessageBox.Show("桥接初始化失败：" + ex.Message); }
        }

        void OnMessage(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            System.Collections.Generic.Dictionary<string, object> msg;
            try { msg = Ser.Deserialize<System.Collections.Generic.Dictionary<string, object>>(e.WebMessageAsJson); }
            catch { return; }
            if (msg == null) return;

            object idObj = Engine.Get(msg, "id");
            string cmd = Engine.Str(msg, "cmd");
            object arg = Engine.Get(msg, "arg");

            System.Collections.Generic.Dictionary<string, object> reply = new System.Collections.Generic.Dictionary<string, object>();
            reply["id"] = idObj;
            try
            {
                reply["ok"] = true;
                reply["data"] = owner.Dispatch(cmd, arg);
            }
            catch (Exception ex)
            {
                reply["ok"] = false;
                reply["error"] = ex.Message;
            }
            try { web.CoreWebView2.PostWebMessageAsJson(Ser.Serialize(reply)); } catch { }
        }
    }
}
