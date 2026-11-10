using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace HaiDou
{
    /// <summary>--selftest：不弹窗、不连客户端，纯逻辑自检（失败退出码 1）</summary>
    static class SelfTest
    {
        static int pass = 0, fail = 0;
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();

        static void Check(string name, Action body)
        {
            try { body(); pass++; Console.WriteLine("  [OK]   " + name); }
            catch (Exception ex) { fail++; Console.WriteLine("  [FAIL] " + name + "  -> " + ex.Message); }
        }

        static void Assert(bool cond, string msg)
        {
            if (!cond) throw new Exception(msg);
        }

        public static int Run()
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            Console.WriteLine("== 海斗助手 C# 自检 ==");
            Maps.Load();

            Check("数据加载：英雄映射 / 方案表非空", delegate
            {
                Assert(Maps.IdToName.Count > 150, "英雄映射太少：" + Maps.IdToName.Count);
                Assert(Maps.Builds.Count > 150, "方案表太少：" + Maps.Builds.Count);
                Assert(Maps.IdToName["103"] == "黑暗之女" || Maps.IdToName.ContainsKey("103"), "103 应在映射里");
            });

            Check("马评分档边界（默认 70/40/20）", delegate
            {
                Assert(Engine.RankOf(100) == "上等马", "100");
                Assert(Engine.RankOf(70) == "上等马", "70 应上等马");
                Assert(Engine.RankOf(69) == "中等马", "69 应中等马");
                Assert(Engine.RankOf(40) == "中等马", "40 应中等马");
                Assert(Engine.RankOf(39) == "下等马", "39 应下等马");
                Assert(Engine.RankOf(20) == "下等马", "20 应下等马");
                Assert(Engine.RankOf(19) == "劣等马", "19 应劣等马");
                Assert(Engine.RankOf(0) == "劣等马", "0 应劣等马");
                Assert(Engine.RankOf(-1) == null, "没数据不能瞎评");
                // 自定义阈值
                Assert(Engine.RankOf(65, 60, 40, 20) == "上等马", "60/40/20 下 65 应上等");
                Assert(Engine.RankOf(59, 60, 40, 20) == "中等马", "60/40/20 下 59 应中等");
            });

            Check("经济差格式化", delegate
            {
                Assert(Engine.FmtVal(48213) == "48.2K", "48213 -> " + Engine.FmtVal(48213));
                Assert(Engine.FmtDiff(4330) == "+4.3K", "4330 -> " + Engine.FmtDiff(4330));
                Assert(Engine.FmtDiff(-2100) == "-2.1K", "-2100 -> " + Engine.FmtDiff(-2100));
                Assert(Engine.FmtDiff(0) == "0", "0 -> " + Engine.FmtDiff(0));
            });

            Check("阶段中文", delegate
            {
                Assert(Engine.PhaseText("ChampSelect") == "选人", "ChampSelect");
                Assert(Engine.PhaseText("PreEndOfGame") == "对局结束前", "PreEndOfGame");
                Assert(Engine.PhaseText("WaitingForStats") == "等待结算", "WaitingForStats");
            });

            Check("配置读写往返", delegate
            {
                Config c = new Config();
                c.autoAccept = true;
                c.chatReport = true;
                c.chatPrefix = "前缀 ";
                c.queues = new int[] { 2400, 2410 };
                string json = Ser.Serialize(c);
                Config back = Ser.Deserialize<Config>(json);
                Assert(back.autoAccept, "autoAccept 丢了");
                Assert(back.chatReport, "chatReport 丢了");
                Assert(back.chatPrefix == "前缀 ", "前缀丢了");
                Assert(back.QueueAllowed(2410), "队列白名单丢了");
                Assert(!back.QueueAllowed(420), "420 不该在白名单");
            });

            Check("聊天消息组包：无楼号、无括号、队友各占一行", delegate
            {
                // 直接调 Engine 的组包逻辑需要实例，这里用反射造一个最小 engine
                Engine eng = new Engine(new Config());
                System.Reflection.FieldInfo fi = typeof(Engine).GetField("mates",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                List<object> list = new List<object>();
                object mate1 = MakeMate(5, "早睡早起", "0512", 85, "5.3");
                object mate2 = MakeMate(9, "摸鱼选手", "233", 15, "3.1");   // 默认阈值 70/40/20 → 劣等马
                System.Collections.IList mates = fi.GetValue(eng) as System.Collections.IList;
                mates.Add(mate1);
                mates.Add(mate2);

                string text = eng.BuildReport(false);
                Assert(text.Contains("早睡早起#0512 上等马 kda 5.3 最近胜率 85%"), "第一条不对：" + text);
                Assert(text.Contains("摸鱼选手#233 劣等马 kda 3.1 最近胜率 15%"), "第二条不对：" + text);
                Assert(!text.Contains("6楼") && !text.Contains("10楼"), "不该出现 cellId+1 的楼号：" + text);
            });

            Regression();
            HexTests.Run(Check);
            Console.WriteLine();
            Console.WriteLine("结果：" + pass + " 通过 / " + fail + " 失败");
            return fail == 0 ? 0 : 1;
        }

        static void Regression()
        {
            Check("自动接受：阶段滞后、连续待接受不重复、取消后再匹配", delegate
            {
                FakeLcu lcu = new FakeLcu();
                Engine e = NewReady(lcu);
                e.ReadyStep(); e.ReadyStep();
                Assert(lcu.Accepts == 1, "phase=None 也应接受，且只接一次");
                lcu.Response = "Accepted"; e.ReadyStep();
                lcu.Response = "None"; e.ReadyStep();
                Assert(lcu.Accepts == 2, "重新匹配应再次接受");
                lcu.MissingReady = true; e.ReadyStep();
                lcu.MissingReady = false; e.ReadyStep();
                Assert(lcu.Accepts == 3, "404 后新一轮应接受");
            });
            Check("自动接受：大厅 gameConfig 队列覆盖上一局缓存、非目标拦截", delegate
            {
                FakeLcu lcu = new FakeLcu();
                Engine e = NewReady(lcu);
                SetField(e, "lastKnownQueue", 420);
                e.ReadyStep();
                Assert(lcu.Accepts == 1, "大厅 gameConfig.queueId 应覆盖旧缓存");
                Engine denied = NewReady(new FakeLcu { Queue = 420 });
                denied.ReadyStep();
                Assert(((FakeLcu)denied.Lcu).Accepts == 0, "排位不能自动接");
                Assert(!(bool)denied.AutoAcceptInfo()["queueAllowed"], "诊断应记录拦截");
                Engine unknown = NewReady(new FakeLcu { Queue = 0 });
                unknown.ReadyStep();
                Assert(((FakeLcu)unknown.Lcu).Accepts == 1, "未知队列兼容");
            });
            Check("自动接受：延迟期间关闭、手动接受、换连接均取消旧计划", delegate
            {
                FakeLcu lcu = new FakeLcu();
                Engine e = NewReady(lcu);
                e.Cfg.autoAcceptDelayMin = e.Cfg.autoAcceptDelayMax = 10000;
                e.ReadyStep();
                Assert((bool)e.AutoAcceptInfo()["armed"], "未安排延迟");
                e.Cfg.autoAccept = false; e.ReadyStep();
                Assert(!(bool)e.AutoAcceptInfo()["armed"] && lcu.Accepts == 0, "关闭未取消");
                e.Cfg.autoAccept = true; e.ReadyStep();
                lcu.Response = "Accepted"; e.ReadyStep();
                Assert(!(bool)e.AutoAcceptInfo()["armed"] && lcu.Accepts == 0, "已手动接受还发请求");
                lcu.Response = "None"; e.ReadyStep();
                FakeLcu other = new FakeLcu { Response = "Accepted" };
                e.Lcu = other; e.ReadyStep();
                Assert(!(bool)e.AutoAcceptInfo()["armed"] && lcu.Accepts == 0 && other.Accepts == 0, "旧连接计划没取消");
            });
            Check("自动接受：失败冷却并重试、并发不重复", delegate
            {
                FakeLcu lcu = new FakeLcu { FailAccept = true };
                Engine e = NewReady(lcu);
                e.ReadyStep(); e.ReadyStep();
                Assert(lcu.Accepts == 1, "失败后应冷却");
                Assert(Engine.Str(e.AutoAcceptInfo(), "lastError").Length > 0, "缺失败诊断");
                SetField(e, "acceptRetryAfter", DateTime.UtcNow.AddSeconds(-1));
                lcu.FailAccept = false;
                System.Threading.Thread[] threads = new System.Threading.Thread[6];
                for (int i = 0; i < threads.Length; i++)
                {
                    threads[i] = new System.Threading.Thread(e.ReadyStep);
                    threads[i].Start();
                }
                foreach (System.Threading.Thread t in threads) t.Join();
                Assert(lcu.Accepts == 2, "并发接受不应重复");
            });
            Check("轮询定时器：强制 GC 后仍查询待接受状态", delegate
            {
                FakeLcu lcu = new FakeLcu { State = "None" };
                Engine e = NewReady(lcu);
                SetField(e, "connected", true);
                SetField(e, "wsStop", true); // 自检禁止真实 WSS 请求
                e.Cfg.enableGoldDiff = false;
                try
                {
                    e.Start();
                    System.Threading.Thread.Sleep(400);
                    int before = lcu.ReadyReads;
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    System.Threading.Thread.Sleep(700);
                    Assert(lcu.ReadyReads > before, "GC 后轮询停止");
                }
                finally { e.Stop(); }
            });
            Check("经济差：装备数量、小数价格、红方视角、缺失价格不伪装 0", delegate
            {
                List<Dictionary<string, object>> players = Players(GoldFixture);
                Dictionary<string, object> data = Engine.ComputeGold(players, "ORDER");
                Assert(Engine.ToLong(data["blue"]) == 2000 && Engine.ToLong(data["red"]) == 1500, "总价值错误");
                Assert(Engine.ToLong(data["diff"]) == 500, "蓝方差值");
                Assert(Engine.ToLong(Engine.ComputeGold(players, "CHAOS")["diff"]) == -500, "红方差值");
                Assert(Engine.FindSelfTeam("红方#123", players) == "CHAOS", "自己阵营错误");
                Dictionary<string, object> item = (Dictionary<string, object>)((System.Collections.IList)players[0]["items"])[0];
                item.Remove("price");
                data = Engine.ComputeGold(players, "ORDER");
                Assert((string)data["blueText"] == "--" && data["diff"] == null, "缺价不该显示零");
                Assert(Engine.ToInt(data["unpricedItems"]) == 1, "缺价计数");
            });
            Check("经济差：Tab 持续刷新买卖装备，松开后停止推送", delegate
            {
                FakeLive e = new FakeLive(TestConfig());
                SetField(e, "isHex", true); SetField(e, "inGame", true);
                int updates = 0;
                long latest = -1;
                e.GoldVisible += delegate(bool visible, Dictionary<string, object> data)
                {
                    if (visible && data != null) { updates++; latest = Engine.ToLong(data["blue"]); }
                };
                e.SetGoldVisible(true);
                e.GoldStep();
                Assert(latest == 0, "初始空装备应为0");
                e.PlayerJson = GoldFixture;
                e.GoldStep();
                Assert(latest == 2000 && e.SelfTeam == "CHAOS", "买装备后未刷新或阵营错误");
                e.PlayerJson = EmptyFixture;
                e.GoldStep();
                Assert(latest == 0 && updates == 3, "卖装备后未刷新");
                e.SetGoldVisible(false);
                e.PlayerJson = GoldFixture; e.GoldStep();
                Assert(updates == 3, "松开后仍推送显示");
                e.PlayerJson = "{\"players\":" + GoldFixture + "}";
                string err;
                Assert(e.LivePlayerList(out err).Count == 2 && err == "", "包装格式不兼容");
                e.PlayerJson = "invalid"; e.GoldStep();
                Assert(e.GoldError.Length > 0, "解析失败没有错误");
                Assert(!(bool)((Dictionary<string, object>)e.Snapshot()["gold"])["ok"], "旧值不能掩盖失败");
            });
            Check("聊天：四位队友各发一条、格式精确、失败保留已发计数", delegate
            {
                Engine e = new Engine(TestConfig());
                AddMate(e, MakeMate(5, "哈哈哈", "123", 46, "3.1"));
                AddMate(e, MakeMate(6, "甲", "1", 70, "4.0"));
                AddMate(e, MakeMate(7, "乙", "2", 25, "2.0"));
                AddMate(e, MakeMate(9, "丙", "3", 10, "1.0"));
                e.Cfg.chatPrefix = "旧前缀不再使用";
                FakeLcu lcu = new FakeLcu();
                e.Lcu = lcu;
                Dictionary<string, object> r = e.SendChatReport(false);
                Assert((bool)r["ok"] && lcu.Messages.Count == 4, "应单发4条");
                Assert(lcu.Messages[0] == "哈哈哈#123 中等马 kda 3.1 最近胜率 46%", "消息格式：" + lcu.Messages[0]);
                Assert(lcu.Messages[1].Contains("上等马") && lcu.Messages[2].Contains("下等马") && lcu.Messages[3].Contains("劣等马"), "评级错误");
                lcu = new FakeLcu { FailChatAfter = 1 }; e.Lcu = lcu;
                r = e.SendChatReport(false);
                Assert(!(bool)r["ok"] && Engine.ToInt(r["sentCount"]) == 1 && lcu.Messages.Count == 1, "部分失败计数/重复发送");
                lcu = new FakeLcu { ChatFallback = true }; e.Lcu = lcu;
                r = e.SendChatReport(false);
                Assert((bool)r["ok"] && lcu.Messages.Count == 4, "会话回退应逐条发送");
            });
            Check("移除推荐：旧配置开启也不写文件，旧文件清理只匹配自家前缀", delegate
            {
                string root = Path.Combine(Paths.ExeDir, "selftest-recommend-" + Guid.NewGuid().ToString("N"));
                string dir = Path.Combine(root, "Config", "Global", "Recommended");
                Directory.CreateDirectory(dir);
                try
                {
                    Engine e = new Engine(TestConfig(), new HexData(delegate(string url) { throw new Exception("selftest must not use network"); }, delegate(Action a) { }));
                    e.Cfg.enableRecommend = true; e.Cfg.lolPath = root;
                    e.Lcu = new FakeLcu { GamePhase = "ChampSelect" };
                    SetField(e, "connected", true);
                    typeof(Engine).GetMethod("Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(e, null);
                    Assert(Directory.GetFiles(dir).Length == 0, "仍写入推荐文件");
                    File.WriteAllText(Path.Combine(dir, Maps.Prefix + "old.json"), "{}");
                    File.WriteAllText(Path.Combine(dir, "user.json"), "{}");
                    Assert(Maps.Cleanup(root) == 1, "旧出装未清理");
                    Assert(File.Exists(Path.Combine(dir, "user.json")), "误删用户文件");
                }
                finally
                {
                    string full = Path.GetFullPath(root);
                    Assert(full.StartsWith(Path.GetFullPath(Paths.ExeDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "测试清理超出范围");
                    Directory.Delete(full, true);
                }
            });
        }

        const string EmptyFixture = "[{\"team\":\"ORDER\",\"items\":[]},{\"team\":\"CHAOS\",\"riotId\":\"红方#123\",\"items\":[]}]";
        const string GoldFixture = "[{\"team\":\"ORDER\",\"items\":[{\"itemID\":1001,\"price\":1000.0,\"count\":2}]},{\"team\":\"CHAOS\",\"riotId\":\"红方#123\",\"items\":[{\"itemID\":3001,\"price\":\"1500.0\",\"count\":1}]}]";

        static List<Dictionary<string, object>> Players(string json)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            foreach (object o in (System.Collections.IEnumerable)Ser.DeserializeObject(json))
                list.Add((Dictionary<string, object>)o);
            return list;
        }
        static Config TestConfig() { return new Config { lolPath = Paths.ExeDir }; }
        static Engine NewReady(FakeLcu lcu)
        {
            Config cfg = TestConfig();
            cfg.autoAccept = true; cfg.autoAcceptDelayMin = cfg.autoAcceptDelayMax = 0;
            return new Engine(cfg) { Lcu = lcu };
        }
        static void SetField(Engine e, string name, object value)
        {
            typeof(Engine).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(e, value);
        }
        static void AddMate(Engine e, object mate)
        {
            ((System.Collections.IList)typeof(Engine).GetField("mates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(e)).Add(mate);
        }
        class FakeLive : Engine
        {
            public string PlayerJson = EmptyFixture;
            public FakeLive(Config cfg) : base(cfg) { }
            protected override string LiveGet(string path)
            {
                if (path == "playerlist") return PlayerJson;
                if (path == "activeplayername") return "\"红方#123\"";
                throw new Exception("Unexpected live request: " + path);
            }
        }
        class FakeLcu : LcuClient
        {
            public int Accepts, ReadyReads, Queue = 2400, FailChatAfter = -1;
            public bool MissingReady, FailAccept, ChatFallback;
            public string State = "InProgress", Response = "None", GamePhase = "None";
            public List<string> Messages = new List<string>();
            public override string Get(string path, int timeoutMs = 4000)
            {
                if (path == "/lol-matchmaking/v1/ready-check")
                {
                    System.Threading.Interlocked.Increment(ref ReadyReads);
                    if (MissingReady) throw new LcuException("ready missing", 404);
                    return "{\"state\":\"" + State + "\",\"playerResponse\":\"" + Response + "\"}";
                }
                if (path == "/lol-lobby/v2/lobby") return "{\"gameConfig\":{\"queueId\":" + Queue + "}}";
                if (path == "/lol-gameflow/v1/session") return "{\"phase\":\"" + GamePhase + "\",\"gameData\":{\"queue\":{\"id\":" + Queue + "}}}";
                if (path == "/lol-champ-select/v1/session") return "{\"localPlayerCellId\":0,\"myTeam\":[{\"cellId\":0,\"championId\":103,\"summonerId\":1}]}";
                if (path == "/lol-champ-select/v1/session/chat-room") throw new LcuException("no room", 404);
                if (path == "/lol-chat/v1/conversations") return "[{\"id\":\"champ-room\",\"type\":\"championSelect\"}]";
                throw new Exception("Unexpected LCU request: " + path);
            }
            public override string Post(string path, object body, int timeoutMs = 4000)
            {
                if (path == "/lol-matchmaking/v1/ready-check/accept")
                {
                    System.Threading.Interlocked.Increment(ref Accepts);
                    if (FailAccept) throw new LcuException("accept failure", 500);
                    return "";
                }
                if (path == "/lol-champ-select/v1/session/chat-message" || path == "/lol-chat/v1/conversations/champ-room/messages")
                {
                    if (FailChatAfter >= 0 && Messages.Count >= FailChatAfter) throw new LcuException("chat failure", 503);
                    if (ChatFallback && path.Contains("chat-message")) throw new LcuException("removed", 404);
                    Dictionary<string, object> data = (Dictionary<string, object>)body;
                    Messages.Add(Engine.Str(data, path.Contains("chat-message") ? "message" : "body"));
                    return "";
                }
                throw new Exception("Unexpected LCU POST: " + path);
            }
        }

        static object MakeMate(int cellId, string name, string tag, int winRate, string kda)
        {
            // Engine 内部的 Mate 是私有嵌套类，用反射构造
            Type t = typeof(Engine).GetNestedType("Mate", System.Reflection.BindingFlags.NonPublic);
            object m = Activator.CreateInstance(t);
            t.GetField("cellId").SetValue(m, cellId);
            t.GetField("name").SetValue(m, name);
            t.GetField("tag").SetValue(m, tag);
            t.GetField("winRate").SetValue(m, winRate);
            t.GetField("kda").SetValue(m, kda);
            t.GetField("rankName").SetValue(m, Engine.RankOf(winRate) ?? "");
            t.GetField("rankKey").SetValue(m, Engine.RankKey(winRate));
            return m;
        }
    }
}
