using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Reflection;

namespace HaiDou
{
    static class HexTests
    {
        const string Index = "{\"latest\":\"16.21\",\"versions\":[{\"version\":\"16.21\",\"dataPath\":\"data/16.21-20261030-test\",\"resourcePath\":\"resources/16.21-test\",\"dataDate\":\"2026-10-30\",\"highMatches\":7979689,\"allMatches\":31918755}]}";
        const string Names = "{\"1001\":{\"name\":\"测试强化甲\",\"rarity\":\"gold\",\"tooltip\":{\"blocks\":[{\"spans\":[{\"text\":\"提高\"},{\"text\":\"护甲\"}]}]}},\"1002\":{\"name\":\"测试强化乙\",\"rarity\":\"silver\"}}";
        static string Detail(int id)
        {
            return "{\"champion\":{\"id\":" + id + ",\"stats\":{\"sampleCount\":300000,\"winRate\":0.53}}," +
                "\"augments\":{\"all\":[{\"id\":1001,\"winRate\":0.61,\"sampleCount\":1000,\"tier\":\"S\"},{\"id\":1002,\"winRate\":0.72,\"sampleCount\":50},{\"id\":1001,\"winRate\":0.99,\"sampleCount\":1000},{\"id\":9999,\"sampleCount\":100}]," +
                "\"stages\":{\"1\":[{\"id\":1001,\"winRate\":0.60,\"sampleCount\":500}]}}," +
                "\"augmentCombinations\":[{\"augmentIds\":[1001,1002],\"winRate\":0.68,\"sampleCount\":150},{\"augmentIds\":[1002,1001],\"winRate\":0.99,\"sampleCount\":200},{\"augmentIds\":[1001],\"winRate\":0.8,\"sampleCount\":100}]}";
        }
        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        static int Count(Dictionary<string, object> value, string key) { return ((System.Collections.ICollection)value[key]).Count; }
        class SelectionClient : LcuClient
        {
            public int Hero = 526;
            public string Phase = "ChampSelect";
            public bool Offline;
            public override string Get(string path, int timeoutMs = 4000)
            {
                if (Offline) throw new Exception("offline");
                if (path == "/lol-gameflow/v1/session") return "{\"phase\":\"" + Phase + "\",\"gameData\":{\"queue\":{\"id\":2400}}}";
                if (path == "/lol-champ-select/v1/session") return "{\"localPlayerCellId\":0,\"myTeam\":[{\"cellId\":0,\"championId\":" + Hero + ",\"summonerId\":1}]}";
                throw new Exception("Unexpected: " + path);
            }
        }
        public static void Run(Action<string, Action> check)
        {
            check("海克斯：LCU选人、换人、离开重选与断线均驱动正确状态", delegate
            {
                List<Action> pending = new List<Action>();
                HexData hex = new HexData(delegate(string url) { throw new Exception("No network in selftest"); }, delegate(Action a) { pending.Add(a); });
                Engine engine = new Engine(new Config(), hex);
                SelectionClient client = new SelectionClient(); engine.Lcu = client;
                typeof(Engine).GetField("connected", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(engine, true);
                MethodInfo tick = typeof(Engine).GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Instance);
                tick.Invoke(engine, null); tick.Invoke(engine, null);
                Assert(pending.Count == 1 && Engine.ToInt(hex.Snapshot()["championId"]) == 526, "选人未触发或轮询重复请求");
                client.Hero = 103; tick.Invoke(engine, null);
                Assert(pending.Count == 2 && Engine.ToInt(hex.Snapshot()["championId"]) == 103, "换人未立即更新");
                client.Phase = "Lobby"; tick.Invoke(engine, null);
                Assert((string)hex.Snapshot()["status"] == "idle", "离开选人未清空");
                client.Phase = "ChampSelect"; tick.Invoke(engine, null);
                Assert(pending.Count == 3, "下一局同一英雄未重新联网");
                client.Offline = true;
                for (int i = 0; i < 15; i++) tick.Invoke(engine, null);
                Assert((string)hex.Snapshot()["status"] == "idle" && engine.ChampionId == 0, "断线残留旧英雄");
                engine.Stop();
            });
            check("海克斯：按英雄统计与真实组合解析，比例不混淆、不相乘", delegate
            {
                Dictionary<string, object> version = HexData.Latest(HexData.Parse(Index));
                Dictionary<string, object> model = HexData.Normalize(HexData.Parse(Detail(526)), HexData.Parse(Names), 526, "镕铁少女", "high", version);
                Assert(Count(model, "augments") == 2, "缺失胜率和重复条目应排除");
                Assert(Count(model, "combinations") == 1, "组合重复及单个强化应排除");
                Dictionary<string, object> combo = (Dictionary<string, object>)((List<object>)model["combinations"])[0];
                Assert(Convert.ToDouble(combo["winRate"]) == 0.68, "必须使用真实组合胜率");
                Dictionary<string, object> first = (Dictionary<string, object>)((List<object>)model["augments"])[0];
                Assert((string)first["description"] == "提高护甲" && (string)first["name"] == "测试强化甲", "中文资源未关联");
                Assert((long)model["datasetMatches"] == 7979689 && (string)model["dataDate"] == "2026-10-30", "元信息不准确");
                bool rejected = false;
                try { HexData.Normalize(HexData.Parse(Detail(103)), HexData.Parse(Names), 526, "镕铁少女", "high", version); }
                catch { rejected = true; }
                Assert(rejected, "不能接受错误英雄的数据");
            });
            check("海克斯：每次换英雄、重新选同英雄、手动刷新均重新联网", delegate
            {
                List<Action> pending = new List<Action>(); int indexes = 0, details = 0, names = 0;
                HexData hex = new HexData(delegate(string url)
                {
                    if (url.EndsWith("versions.json")) { Interlocked.Increment(ref indexes); return Index; }
                    if (url.EndsWith("augments.json")) { Interlocked.Increment(ref names); return Names; }
                    Interlocked.Increment(ref details); return Detail(url.EndsWith("/526.json") ? 526 : 103);
                }, delegate(Action a) { pending.Add(a); });
                hex.Select(526, "镕铁少女", "high", false); pending[0]();
                hex.Select(526, "镕铁少女", "high", false);
                Assert(pending.Count == 1, "500ms 同英雄轮询不应刷屏请求");
                hex.Select(103, "九尾妖狐", "high", false); pending[1]();
                hex.Select(526, "镕铁少女", "high", false); pending[2]();
                hex.Select(526, "镕铁少女", "high", true); pending[3]();
                hex.Select(0, "", "high", false);
                hex.Select(526, "镕铁少女", "high", false); pending[4]();
                Assert(indexes == 5 && details == 5 && names == 5, "不能复用索引、英雄或字典缓存");
                Assert((string)hex.Snapshot()["status"] == "ready", "刷新未成功");
            });
            check("海克斯：更换英雄即清空，失败不回退到旧数据", delegate
            {
                List<Action> pending = new List<Action>(); bool fail = false;
                HexData hex = new HexData(delegate(string url)
                {
                    if (fail) throw new Exception("offline");
                    return url.EndsWith("versions.json") ? Index : url.EndsWith("augments.json") ? Names : Detail(526);
                }, delegate(Action a) { pending.Add(a); });
                hex.Select(526, "镕铁少女", "high", false); pending[0]();
                fail = true; hex.Select(103, "九尾妖狐", "high", false);
                Assert(Count(hex.Snapshot(), "augments") == 0 && (string)hex.Snapshot()["status"] == "loading", "加载时残留上一位英雄");
                pending[1]();
                Assert((string)hex.Snapshot()["status"] == "error" && Count(hex.Snapshot(), "combinations") == 0, "失败仍显示旧组合");
                Assert(Engine.ToInt(hex.Snapshot()["championId"]) == 103, "失败英雄错误");
            });
            check("海克斯：旧英雄慢响应不能覆盖新英雄结果", delegate
            {
                List<Action> pending = new List<Action>();
                using (ManualResetEvent entered = new ManualResetEvent(false))
                using (ManualResetEvent release = new ManualResetEvent(false))
                {
                    HexData hex = new HexData(delegate(string url)
                    {
                        if (url.EndsWith("versions.json")) return Index;
                        if (url.EndsWith("augments.json")) return Names;
                        if (url.EndsWith("/526.json")) { entered.Set(); if (!release.WaitOne(5000)) throw new Exception("timeout"); return Detail(526); }
                        return Detail(103);
                    }, delegate(Action a) { pending.Add(a); });
                    hex.Select(526, "镕铁少女", "high", false);
                    Thread old = new Thread(delegate() { pending[0](); }); old.Start();
                    try
                    {
                        Assert(entered.WaitOne(3000), "旧请求未进入");
                        hex.Select(103, "九尾妖狐", "all", false); pending[1]();
                        Assert(Engine.ToInt(hex.Snapshot()["championId"]) == 103 && (string)hex.Snapshot()["dataset"] == "all", "新请求错误");
                    }
                    finally { release.Set(); old.Join(5000); }
                    Assert((string)hex.Snapshot()["status"] == "ready" && Engine.ToInt(hex.Snapshot()["championId"]) == 103, "旧响应覆盖新结果");
                }
            });
            check("吸附位置：右侧、左侧、空间不足和副屏负坐标", delegate
            {
                string side;
                Rectangle area = new Rectangle(0, 0, 1920, 1040);
                Rectangle right = HexWindow.DockBounds(new Rectangle(100, 100, 1280, 720), area, new Size(390, 700), out side);
                Assert(side == "右侧吸附" && right.Left == 1388, "右侧");
                Rectangle left = HexWindow.DockBounds(new Rectangle(600, 100, 1280, 720), area, new Size(390, 700), out side);
                Assert(side == "左侧吸附" && left.Right == 592, "左侧");
                Rectangle floating = HexWindow.DockBounds(area, area, new Size(390, 1200), out side);
                Assert(area.Contains(floating) && side.Contains("浮动"), "窄屏窗口越界");
                Rectangle second = new Rectangle(-1920, -200, 1920, 1040);
                Rectangle dock = HexWindow.DockBounds(new Rectangle(-1820, -100, 1280, 720), second, new Size(390, 700), out side);
                Assert(second.Contains(dock) && dock.Left < 0, "副屏坐标错误");
            });
        }
    }
}
