using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// 用已抓取的线上响应检查真实 WebView2 渲染；不向正式程序增加演示数据。
class UiSmoke : Form
{
    readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill };
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
    readonly string root;
    Dictionary<string, object> state;
    UiSmoke(string workspace)
    {
        root = workspace; Text = "HaiDou UI validation"; ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual; Location = new Point(-20000, -20000);
        ClientSize = new Size(358, 680); Controls.Add(web);
        Shown += async delegate { try { await Run(); } catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; } finally { Close(); } };
    }
    object Invoke(Type t, string method, params object[] values)
    {
        return t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, values);
    }
    async Task LoadPage(string page)
    {
        TaskCompletionSource<bool> done = new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = null;
        handler = delegate(object s, CoreWebView2NavigationCompletedEventArgs e) { web.CoreWebView2.NavigationCompleted -= handler; done.SetResult(e.IsSuccess); };
        web.CoreWebView2.NavigationCompleted += handler;
        web.CoreWebView2.Navigate(new Uri(Path.Combine(root, "hxdou-cs", "wwwroot", page)).AbsoluteUri);
        if (!await done.Task) throw new Exception("Navigation failed: " + page);
        await Task.Delay(300);
    }
    async Task Check(string code, string label)
    {
        string result = await web.CoreWebView2.ExecuteScriptAsync("(()=>{try{return !!(" + code + ")}catch(e){return e.message}})()");
        if (result != "true") throw new Exception(label + ": " + result);
        Console.WriteLine("[OK] " + label);
    }
    async Task SavePreview(string name)
    {
        await Task.Delay(200);
        using (FileStream stream = File.Create(Path.Combine(root, "_research", "hex-live", name)))
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
    }
    async Task Run()
    {
        Assembly app = Assembly.LoadFrom(Path.Combine(root, "hxdou-cs", "out", "HaiDouAssistant.exe"));
        Type type = app.GetType("HaiDou.HexData", true);
        string dataDir = Path.Combine(root, "_research", "hex-live");
        object detail = Invoke(type, "Parse", File.ReadAllText(Path.Combine(dataDir, "526.json")));
        object names = Invoke(type, "Parse", File.ReadAllText(Path.Combine(dataDir, "augments.json")));
        object index = Invoke(type, "Parse", File.ReadAllText(Path.Combine(dataDir, "versions.json")));
        object version = Invoke(type, "Latest", index);
        Dictionary<string, object> hex = (Dictionary<string, object>)Invoke(type, "Normalize", detail, names, 526, "镕铁少女", "high", version);
        hex["fetchedAt"] = DateTimeOffset.Now.ToString("o");
        object[] mates = new object[4];
        for (int i = 0; i < 4; i++) mates[i] = new Dictionary<string, object> {
            { "cellId", i + 1 }, { "name", "测试队友" + (i + 1) }, { "tag", "123" }, { "champion", "九尾妖狐" },
            { "rank", new { name = "中等马", key = "mid" } }, { "history", new { total = 10, wins = 5, winRate = 50, kda = "3.1" } }
        };
        state = new Dictionary<string, object> {
            { "hex", hex }, { "config", new { hexDataset = "high", hexMinSamples = 100, hexDock = true, autoAccept = false, autoAcceptDelayMin = 200, autoAcceptDelayMax = 500 } },
            { "connected", true }, { "phase", "ChampSelect" }, { "phaseText", "选人" }, { "isHex", true }, { "queueId", 2400 },
            { "champion", "镕铁少女" }, { "championId", 526 }, { "teammates", mates }, { "port", 12345 }, { "gold", new { visible = false } }, { "keyKind", "win32" }
        };
        CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDir, "ui-profile"), null);
        await web.EnsureCoreWebView2Async(env);
        await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.uiErrors=[];addEventListener('error',e=>uiErrors.push(e.message));addEventListener('unhandledrejection',e=>uiErrors.push(String(e.reason)));window.testState=" + json.Serialize(state) + ";window.hx={onStatus:f=>window.pushStatus=f,onLog:()=>{},getBootstrap:()=>Promise.resolve({status:testState,version:'1.3.0',logs:[]}),setConfig:p=>{Object.assign(testState.config,p);pushStatus(testState);return Promise.resolve({ok:true})},refreshHex:()=>Promise.resolve(true)};");
        await LoadPage("hex.html");
        await Check("document.querySelectorAll('.hex-card').length===6 && document.body.innerText.includes('镕铁少女') && document.body.innerText.includes('2026-10-30')", "hero, dated online statistics, six recommendations");
        await Check("document.documentElement.scrollWidth<=innerWidth", "side panel has no horizontal overflow at 358px");
        await SavePreview("preview-augments.png");
        await web.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('[role=tab]')[1].click()");
        await Check("document.querySelectorAll('.hex-combo').length>0 && document.querySelector('select[aria-label=品质]').disabled", "real combinations and distinct filter scope");
        await SavePreview("preview-combinations.png");
        await web.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('[role=tab]')[0].click();let rarity=document.querySelector('select[aria-label=品质]');rarity.value='gold';rarity.dispatchEvent(new Event('change'))");
        await Check("[...document.querySelectorAll('.hex-card')].every(x=>x.dataset.rarity==='gold')", "rarity filter");
        await web.CoreWebView2.ExecuteScriptAsync("let stage=document.querySelector('select[aria-label=轮次]');stage.value='1';stage.dispatchEvent(new Event('change'))");
        await Check("document.querySelectorAll('.hex-card').length>0", "round filter");
        await web.CoreWebView2.ExecuteScriptAsync("testState.hex={status:'loading',champion:'九尾妖狐',championId:103};pushStatus(testState)");
        await Check("document.querySelectorAll('.hex-card').length===0 && !document.body.innerText.includes('镕铁少女')", "hero change removes stale cards immediately");
        await web.CoreWebView2.ExecuteScriptAsync("testState.hex={status:'error',championId:103,error:'实时获取失败'};pushStatus(testState)");
        await Check("document.querySelectorAll('.hex-card').length===0 && !!document.querySelector('.hex-error') && !document.querySelector('.hex-toolbar button').disabled", "network failure is visible and retry remains available");
        await Check("uiErrors.length===0", "side panel has no script or promise errors");
        ClientSize = new Size(464, 680);
        await LoadPage("panel.html");
        await Check("document.querySelectorAll('.mate').length===4 && document.querySelector('.scroll .sec h3').textContent.includes('海克斯')", "main panel puts hero and teammates first");
        await Check("document.querySelector('.who').getBoundingClientRect().width>180 && document.documentElement.scrollWidth<=innerWidth", "teammate names have room and main panel fits");
        await Check("!document.querySelector('.stat').title.includes('undefined') && document.querySelector('#auto-delay-summary').textContent.includes('0.2~0.5s')", "accurate history tooltip and configured delay label");
        await Check("document.querySelectorAll('.mate')[3].getBoundingClientRect().bottom<=document.querySelector('.scroll').getBoundingClientRect().bottom", "all four teammates visible at default scroll position");
        await Check("uiErrors.length===0", "main panel has no script or promise errors");
        await SavePreview("preview-panel.png");
    }
    [STAThread] static void Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new UiSmoke(args[0]));
    }
}
