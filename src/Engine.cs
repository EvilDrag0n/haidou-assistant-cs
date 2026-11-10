using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace HaiDou
{
    /// <summary>引擎：连接 / 阶段 / 出装 / 队友 / 经济差 / 自动接受 / 聊天播报</summary>
    class Engine
    {
        public event Action<Dictionary<string, object>> StatusChanged;
        public event Action<string, string> LogAdded;          // level, text
        public event Action<bool, Dictionary<string, object>> GoldVisible;

        public Config Cfg;
        public LcuClient Lcu;
        public readonly HexData Hex;
        public string SelfTeam = "ORDER";
        public string SelfCell = "";
        public long SelfSummonerId = 0;

        // 状态
        bool connected = false, connecting = false, inGame = false, isHex = false;
        int waitAttempts = 0, tickSkips = 0, failCount = 0;
        string phase = "None", error = "", champion = "";
        int championId = 0, lastKnownQueue = 0;
        bool disconnectLogged = false;
        string chatSentKey = "", lastWaitError = "";
        bool namesMerged = false;

        // 队友
        class Mate
        {
            public int cellId; public int championId; public string champion = "";
            public long summonerId = 0; public string puuid = "";
            public string name = "", tag = "", note = "";
            public int total, wins, winRate = -1; public string kda = ""; public string rankName = ""; public string rankKey = "";
        }
        readonly List<Mate> mates = new List<Mate>();
        readonly Dictionary<string, string> nameCache = new Dictionary<string, string>();      // key -> name|tag
        readonly Dictionary<string, string> historyCache = new Dictionary<string, string>();   // puuid -> summary json
        string lastIdentityKey = "";
        public int nameRequests = 0, historyRequests = 0, builds = 0;   // 公开给调试页

        // 经济差
        Dictionary<string, object> goldData = null;
        public bool GoldShown = false;
        int goldBusy = 0;

        // 自动接受
        bool autoArmed = false, acceptedThisGame = false;
        int readyBusy = 0;
        DateTime acceptDue = DateTime.MinValue, acceptRetryAfter = DateTime.MinValue;
        LcuClient readyClient;
        string readyState = "", readyResponse = "", readyError = "", readyUpdatedAt = "";
        int readyQueue = 0;

        // 聊天
        public Dictionary<string, object> ChatState = new Dictionary<string, object>();

        // MaxJsonLength 默认只有 2MB，战绩接口响应会超 → 静默失败。必须放开。
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 1000,
        };
        readonly List<System.Threading.Timer> timers = new List<System.Threading.Timer>();

        public Engine(Config cfg) : this(cfg, new HexData()) { }
        internal Engine(Config cfg, HexData hex)
        {
            Cfg = cfg;
            Hex = hex;
            Hex.Changed += delegate { Safe(Push); };
            ChatState["lastOk"] = null; ChatState["lastError"] = ""; ChatState["lastText"] = ""; ChatState["attempts"] = new List<object>();
        }

        // ===== WSS 事件流（阶段变化立刻响应；轮询仍然是兜底）=====
        System.Threading.Thread wsThread;
        volatile bool wsStop = false;
        public volatile string WsState = "off";

        public void StartWs()
        {
            if (wsThread != null) return;
            wsThread = new System.Threading.Thread(WsLoop);
            wsThread.IsBackground = true;
            wsThread.Start();
        }

        void WsLoop()
        {
            int attempt = 0;
            while (!wsStop)
            {
                LcuClient lcu = Lcu;
                if (lcu == null || !connected) { System.Threading.Thread.Sleep(1500); continue; }
                System.Net.WebSockets.ClientWebSocket c = null;
                try
                {
                    c = new System.Net.WebSockets.ClientWebSocket();
                    c.Options.SetRequestHeader("Authorization",
                        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("riot:" + lcu.Token)));
                    c.ConnectAsync(new Uri("wss://127.0.0.1:" + lcu.Port + "/"), System.Threading.CancellationToken.None)
                     .Wait(5000);

                    string sub = "[5,\"OnJsonApiEvent_lol-gameflow_v1_gameflow-phase\"]";
                    byte[] sb = Encoding.UTF8.GetBytes(sub);
                    c.SendAsync(new ArraySegment<byte>(sb), System.Net.WebSockets.WebSocketMessageType.Text, true,
                        System.Threading.CancellationToken.None).Wait(3000);

                    WsState = "on"; attempt = 0;
                    byte[] buf = new byte[8192];
                    while (c.State == System.Net.WebSockets.WebSocketState.Open && !wsStop)
                    {
                        System.Threading.Tasks.Task<System.Net.WebSockets.WebSocketReceiveResult> rt =
                            c.ReceiveAsync(new ArraySegment<byte>(buf), System.Threading.CancellationToken.None);
                        if (!rt.Wait(60000)) break;
                        if (rt.Result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                        Safe(delegate { Tick(); });   // 阶段事件 → 立刻 tick 一次
                    }
                }
                catch { }
                finally
                {
                    WsState = "off";
                    try { if (c != null) c.Dispose(); } catch { }
                }
                if (wsStop) break;
                attempt++;
                System.Threading.Thread.Sleep(Math.Min(30000, 2000 * attempt));
            }
        }

        public void Start()
        {
            Log("info", "引擎启动（C# 版）");
            if (timers.Count > 0) return;
            // KeepAlive 只活到本方法返回；必须在引擎整个生命周期持有定时器。
            timers.Add(new System.Threading.Timer(delegate { Safe(ConnectStep); }, null, 0, 3000));
            timers.Add(new System.Threading.Timer(delegate { Safe(Tick); }, null, 200, 500));
            timers.Add(new System.Threading.Timer(delegate { Safe(GoldStep); }, null, 1000, 1000));
            timers.Add(new System.Threading.Timer(delegate { Safe(ReadyStep); }, null, 300, 300));
            StartWs();
            Push();
        }

        void Safe(Action a)
        {
            try { a(); }
            catch (Exception ex) { Log("debug", "内部：" + ex.Message); }
        }

        public void Stop()
        {
            wsStop = true;
            Hex.Select(0, "", Cfg.hexDataset, true);
            foreach (System.Threading.Timer t in timers) t.Dispose();
            timers.Clear();
        }

        public void Log(string level, string text)
        {
            if (LogAdded != null) LogAdded(level, text);
        }

        /* ---------------- 连接 ---------------- */
        void ConnectStep()
        {
            if (connecting || connected) return;
            connecting = true;
            waitAttempts++;
            try
            {
                Lcu = LcuClient.Connect();
                connected = true; waitAttempts = 0; failCount = 0; error = ""; lastWaitError = "";
                Log("ok", "已连接游戏客户端（端口 " + Lcu.Port + "）");
                if (!string.IsNullOrEmpty(Lcu.ExePath)) Log("debug", "客户端路径：" + Lcu.ExePath);
                SelfTeam = "ORDER"; namesMerged = false; mates.Clear(); lastIdentityKey = "";
                Push();
            }
            catch (Exception ex)
            {
                connected = false; error = ex.Message;
                if (ex.Message != lastWaitError || waitAttempts % 6 == 1)
                {
                    lastWaitError = ex.Message;
                    Log("info", "等待游戏客户端… " + ex.Message + "（第 " + waitAttempts + " 次，3s 后重试）");
                }
                Push();
            }
            finally { connecting = false; }
        }

        /* ---------------- 主轮询 ---------------- */
        int tickBusy = 0;

        void Tick()
        {
            // 防重入：WSS 事件 + 500ms 轮询会同时打进来
            if (System.Threading.Interlocked.CompareExchange(ref tickBusy, 1, 0) != 0) { tickSkips++; return; }
            try { TickInner(); }
            finally { System.Threading.Interlocked.Exchange(ref tickBusy, 0); }
        }

        void TickInner()
        {
            if (Lcu == null || !connected) return;

            string json;
            try
            {
                json = Lcu.GameflowJson();
                failCount = 0; disconnectLogged = false;
            }
            catch (LcuException le)
            {
                // 4xx/5xx：客户端活着，只是当前没这个资源，不当掉线
                error = le.Message;
                return;
            }
            catch (Exception ex)
            {
                failCount++;
                error = ex.Message;
                if (failCount >= 15)
                {
                    failCount = 0; connected = false; Lcu = null;
                    championId = 0; champion = ""; phase = "None"; isHex = false;
                    Hex.Select(0, "", Cfg.hexDataset, true);
                    if (!disconnectLogged) { disconnectLogged = true; Log("warn", "与客户端失联（" + ex.Message + "），正在重连…"); }
                    Push();
                }
                return;
            }

            Dictionary<string, object> gf = Ser.Deserialize<Dictionary<string, object>>(json);
            string newPhase = Str(gf, "phase") ?? "None";
            int qid = QueueIdOf(gf);
            if (qid > 0) lastKnownQueue = qid;

            bool phaseChanged = newPhase != phase;
            phase = newPhase;
            isHex = Cfg.QueueAllowed(qid);
            inGame = phase == "GameStart" || phase == "InProgress" || phase == "Reconnect";

            if (phaseChanged)
            {
                Log("info", "阶段 → " + PhaseText(phase) + (isHex ? "（海克斯大乱斗）" : (qid > 0 ? "（队列 " + qid + "）" : "")));
                HandleSession();
            }
            else HandleSession();

            if (phaseChanged) Push();
        }

        void HandleSession()
        {
            if (Lcu == null) return;

            if (isHex && phase == "ChampSelect")
            {
                Dictionary<string, object> cs = null;
                try { cs = Ser.Deserialize<Dictionary<string, object>>(Lcu.ChampSelectJson()); }
                catch { return; }
                if (cs == null) return;

                object lpc = Get(cs, "localPlayerCellId");
                int selfCell = lpc == null ? 0 : ToInt(lpc);
                int cid = 0;
                foreach (object o in Arr(Get(cs, "myTeam")))
                {
                    Dictionary<string, object> p = o as Dictionary<string, object>;
                    if (p == null) continue;
                    if (ToInt(Get(p, "cellId")) == selfCell)
                    {
                        cid = ToInt(Get(p, "championId"));
                        object sid = Get(p, "summonerId");
                        if (sid != null) SelfSummonerId = ToLong(sid);
                    }
                }
                string hero = cid > 0 ? Maps.ChampionName(cid) : "";
                if (cid > 0 && string.IsNullOrEmpty(hero)) hero = "英雄 #" + cid;

                if (cid != championId)
                {
                    championId = cid; champion = hero;
                    Hex.Select(cid, hero, Cfg.hexDataset, false);
                    if (cid > 0) Log("info", "你的英雄：" + hero + "，正在实时查询海克斯");
                    Push();
                }
                RefreshTeammates(cs);
                return;
            }

            if (isHex && inGame)
            {
                if (!namesMerged && mates.Count > 0)
                {
                    namesMerged = true;
                    MergeNamesFromLive();
                }
                return;
            }

            if (championId > 0)
            {
                championId = 0; champion = "";
                Hex.Select(0, "", Cfg.hexDataset, false);
            }


            if (mates.Count > 0 || namesMerged)
            {
                mates.Clear(); namesMerged = false; chatSentKey = ""; lastIdentityKey = "";
                Push();
            }
        }

        public string LolPath
        {
            get
            {
                if (!string.IsNullOrEmpty(Cfg.lolPath)) return Cfg.lolPath;
                string p = Maps.FindLolPath(Lcu != null ? Lcu.ExePath : null);
                return p;
            }
        }

        public void RefreshHex(bool force)
        {
            Hex.Select(championId, champion, Cfg.hexDataset, force);
        }

        public int CleanupAll()
        {
            int n = Maps.Cleanup(LolPath);

            Log("info", "手动清理出装 " + n + " 个");
            Push();
            return n;
        }

        /* ---------------- 队友 ---------------- */
        void RefreshTeammates(Dictionary<string, object> cs)
        {
            object lpc = Get(cs, "localPlayerCellId");
            int selfCell = lpc == null ? 0 : ToInt(lpc);
            List<Dictionary<string, object>> team = new List<Dictionary<string, object>>();
            foreach (object o in Arr(Get(cs, "myTeam")))
            {
                Dictionary<string, object> p = o as Dictionary<string, object>;
                if (p != null) team.Add(p);
            }

            List<string> ids = new List<string>();
            foreach (Dictionary<string, object> p in team)
            {
                if (ToInt(Get(p, "cellId")) == selfCell) continue;
                object sid = Get(p, "summonerId"); object pud = Get(p, "puuid"); object ob = Get(p, "obfuscatedSummonerId");
                ids.Add(ToInt(Get(p, "cellId")) + ":" + (sid != null ? sid.ToString() : (pud != null ? pud.ToString() : (ob != null ? ob.ToString() : ""))));
            }
            ids.Sort();
            string identity = string.Join("|", ids.ToArray());

            if (identity == lastIdentityKey && mates.Count > 0)
            {
                // 人没变：只同步英雄名，0 请求
                bool changed = false;
                foreach (Mate m in mates)
                {
                    foreach (Dictionary<string, object> p in team)
                    {
                        if (ToInt(Get(p, "cellId")) != m.cellId) continue;
                        int cid = ToInt(Get(p, "championId"));
                        if (cid != m.championId) { m.championId = cid; m.champion = Maps.ChampionName(cid); changed = true; }
                    }
                }
                if (changed) Push();
                return;
            }

            lastIdentityKey = identity;
            mates.Clear();
            builds++;

            foreach (Dictionary<string, object> p in team)
            {
                if (ToInt(Get(p, "cellId")) == selfCell) continue;
                Mate m = new Mate();
                m.cellId = ToInt(Get(p, "cellId"));
                m.championId = ToInt(Get(p, "championId"));
                m.champion = Maps.ChampionName(m.championId);
                object sid = Get(p, "summonerId");
                if (sid != null) m.summonerId = ToLong(sid);
                object pud = Get(p, "puuid");
                if (pud != null) m.puuid = pud.ToString();
                mates.Add(m);
            }

            foreach (Mate m in mates)
            {
                ResolveName(m);
                ResolveHistory(m);
                Thread.Sleep(150);
            }

            Log("info", "队友信息：" + mates.Count + " 人（名字请求 " + nameRequests + " 次 / 战绩请求 " + historyRequests + " 次；之后换英雄不再请求）");
            Push();

            if (Cfg.chatReport) MaybeAutoChat();
        }

        void ResolveName(Mate m)
        {
            string key = m.summonerId > 0 ? m.summonerId.ToString() : m.puuid;
            if (string.IsNullOrEmpty(key)) { m.note = "ID 未下发"; return; }
            string cached;
            if (nameCache.TryGetValue(key, out cached)) { SplitName(cached, m); return; }

            try
            {
                if (m.summonerId > 0)
                {
                    nameRequests++;
                    Dictionary<string, object> s = Ser.Deserialize<Dictionary<string, object>>(
                        Lcu.Get("/lol-summoner/v1/summoners/" + m.summonerId));
                    string n = Str(s, "gameName") ?? Str(s, "displayName");
                    if (!string.IsNullOrEmpty(n))
                    {
                        string t = Str(s, "tagLine") ?? "";
                        nameCache[key] = n + "\u0001" + t;
                        SplitName(nameCache[key], m);
                        return;
                    }
                }
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(m.puuid))
                {
                    nameRequests++;
                    Dictionary<string, object> s = Ser.Deserialize<Dictionary<string, object>>(
                        Lcu.Get("/lol-summoner/v2/summoners/puuid/" + m.puuid));
                    string n = Str(s, "gameName") ?? Str(s, "displayName");
                    if (!string.IsNullOrEmpty(n))
                    {
                        string t = Str(s, "tagLine") ?? "";
                        nameCache[key] = n + "\u0001" + t;
                        SplitName(nameCache[key], m);
                        return;
                    }
                }
            }
            catch { }

            m.note = "名称已隐藏";
        }

        static void SplitName(string raw, Mate m)
        {
            string[] parts = raw.Split('\u0001');
            m.name = parts.Length > 0 ? parts[0] : raw;
            m.tag = parts.Length > 1 ? parts[1] : "";
        }

        void ResolveHistory(Mate m)
        {
            if (string.IsNullOrEmpty(m.puuid)) { m.note = m.note.Length > 0 ? m.note : "无 puuid"; return; }
            string cached;
            if (historyCache.TryGetValue(m.puuid, out cached)) { ParseHistory(cached, m); return; }

            string[] urls = new string[]
            {
                "/lol-match-history/v1/products/lol/" + m.puuid + "/matches?begIndex=0&endIndex=9",
                "/lol-match-history/v3/matchlist/account/" + m.puuid + "?begIndex=0&endIndex=9",
            };
            foreach (string u in urls)
            {
                try
                {
                    historyRequests++;
                    string raw = Lcu.Get(u);
                    string sum = Summarize(raw, m.puuid);
                    if (sum != null) { historyCache[m.puuid] = sum; ParseHistory(sum, m); return; }
                }
                catch (LcuException le)
                {
                    if (le.Status == 404 || le.Status == 403)
                    {
                        historyCache[m.puuid] = "note:战绩不可见（非好友/隐私限制）";
                        ParseHistory(historyCache[m.puuid], m);
                        return;
                    }
                }
                catch { }
            }
            historyCache[m.puuid] = "note:战绩获取失败";
            ParseHistory(historyCache[m.puuid], m);
        }

        static string Summarize(string raw, string puuid)
        {
            try
            {
                object root = Ser.DeserializeObject(raw);
                IEnumerable games = null;
                Dictionary<string, object> r = root as Dictionary<string, object>;
                if (r != null)
                {
                    // 三种形状：{games:{games:[...]}} / {games:[...]} / 直接是数组
                    object g1 = Get(r, "games");
                    Dictionary<string, object> gd = g1 as Dictionary<string, object>;
                    if (gd != null) games = Get(gd, "games") as IEnumerable;
                    else games = g1 as IEnumerable;
                }
                else
                {
                    games = root as IEnumerable;
                }
                if (games == null) return null;

                int total = 0, wins = 0, k = 0, d = 0, a = 0;
                foreach (object go in games)
                {
                    if (total >= 10) break;
                    Dictionary<string, object> g = go as Dictionary<string, object>;
                    if (g == null) continue;

                    Dictionary<string, object> me = FindParticipant(g, puuid);
                    if (me == null) continue;

                    total++;
                    object win = Get(g, "win");
                    if (win == null) win = Get(me, "win");
                    Dictionary<string, object> st = Get(me, "stats") as Dictionary<string, object>;
                    if (win == null && st != null) win = Get(st, "win");
                    if (win is bool && (bool)win) wins++;

                    object kk = Get(me, "kills"), dd = Get(me, "deaths"), aa = Get(me, "assists");
                    if (st != null) { kk = Get(st, "kills"); dd = Get(st, "deaths"); aa = Get(st, "assists"); }
                    k += ToInt(kk); d += ToInt(dd); a += ToInt(aa);
                }
                if (total == 0) return null;
                double kda = (k + a) / (double)Math.Max(1, d);
                int wr = (int)Math.Round(wins * 100.0 / total);
                return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3:F1}", total, wins, wr, kda);
            }
            catch { return null; }
        }

        /// <summary>在一场比赛里找到"我"这一条：先按 puuid 直查，再走 participantIdentities 老结构</summary>
        static Dictionary<string, object> FindParticipant(Dictionary<string, object> game, string puuid)
        {
            object partsObj = Get(game, "participants");
            List<Dictionary<string, object>> parts = new List<Dictionary<string, object>>();
            foreach (object po in Arr(partsObj))
            {
                Dictionary<string, object> p = po as Dictionary<string, object>;
                if (p != null) parts.Add(p);
            }
            if (parts.Count == 0) return null;

            foreach (Dictionary<string, object> p in parts)
            {
                if (Str(p, "puuid") == puuid) return p;
            }

            // 老结构：participantIdentities[].player.puuid + participantId
            int wantId = -1;
            foreach (object io in Arr(Get(game, "participantIdentities")))
            {
                Dictionary<string, object> ident = io as Dictionary<string, object>;
                if (ident == null) continue;
                Dictionary<string, object> player = Get(ident, "player") as Dictionary<string, object>;
                if (player == null) continue;
                if (Str(player, "puuid") != puuid) continue;
                wantId = ToInt(Get(ident, "participantId"));
                break;
            }
            if (wantId > 0)
            {
                foreach (Dictionary<string, object> p in parts)
                {
                    if (ToInt(Get(p, "participantId")) == wantId) return p;
                }
            }
            return null;
        }

        void ParseHistory(string sum, Mate m)
        {
            if (sum != null && sum.StartsWith("note:")) { m.note = m.note.Length > 0 ? m.note : sum.Substring(5); m.winRate = -1; return; }
            string[] p = sum.Split('|');
            if (p.Length < 4) return;
            m.total = ToInt(p[0]); m.wins = ToInt(p[1]); m.winRate = ToInt(p[2]); m.kda = p[3];
            SetRank(m);
        }

        public static string RankOf(int winRate) { return RankOf(winRate, 70, 40, 20); }

        public static string RankOf(int winRate, int top, int mid, int low)
        {
            if (winRate < 0) return null;
            if (winRate >= top) return "上等马";
            if (winRate >= mid) return "中等马";
            if (winRate >= low) return "下等马";
            return "劣等马";
        }

        public static string RankKey(int winRate) { return RankKey(winRate, 70, 40, 20); }

        public static string RankKey(int winRate, int top, int mid, int low)
        {
            if (winRate < 0) return "";
            if (winRate >= top) return "top";
            if (winRate >= mid) return "mid";
            if (winRate >= low) return "low";
            return "bad";
        }

        /// <summary>改了阈值后立刻按已有胜率重算评级（不需要重新请求战绩）</summary>
        public void ReapplyRanks()
        {
            foreach (Mate m in mates) SetRank(m);
        }

        void SetRank(Mate m)
        {
            m.rankName = RankOf(m.winRate, Cfg.rankTop, Cfg.rankMid, Cfg.rankLow) ?? "";
            m.rankKey = RankKey(m.winRate, Cfg.rankTop, Cfg.rankMid, Cfg.rankLow);
        }

        void MergeNamesFromLive()
        {
            try
            {
                string liveErr;
                List<Dictionary<string, object>> players = LivePlayerList(out liveErr);
                if (players == null) return;
                foreach (Mate m in mates)
                {
                    if (!string.IsNullOrEmpty(m.name)) continue;
                    foreach (Dictionary<string, object> p in players)
                    {
                        if (ToInt(Get(p, "championId")) != m.championId) continue;
                        string n = Str(p, "riotId") ?? Str(p, "summonerName");
                        if (!string.IsNullOrEmpty(n)) { m.name = n; m.note = "名字来自对局内数据"; }
                        break;
                    }
                }
                Push();
            }
            catch { }
        }

        /* ---------------- 经济差 ---------------- */
        public List<Dictionary<string, object>> LivePlayers() { string e; return LivePlayerList(out e); }

        protected virtual string LiveGet(string path)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://127.0.0.1:2999/liveclientdata/" + path);
            req.Timeout = 2500; req.ReadWriteTimeout = 2500; req.KeepAlive = false;
            req.Proxy = null;
            req.ServerCertificateValidationCallback = delegate { return true; };
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        public List<Dictionary<string, object>> LivePlayerList(out string error)
        {
            error = "";
            try
            {
                object arr = Ser.DeserializeObject(LiveGet("playerlist"));
                Dictionary<string, object> wrapper = arr as Dictionary<string, object>;
                if (wrapper != null) arr = Get(wrapper, "players");
                if (!(arr is IEnumerable) || arr is string || arr is Dictionary<string, object>)
                    throw new Exception("2999 playerlist 格式异常");
                List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
                foreach (object o in (IEnumerable)arr)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d != null) list.Add(d);
                }
                if (list.Count == 0) error = "2999 尚无玩家数据";
                return list;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        public string ChampionName { get { return champion; } }
        public int ChampionId { get { return championId; } }
        public string GoldError = "";
        public string GoldUpdatedAt = "";
        bool selfTeamResolved = false;

        public bool ForceGoldRefresh()
        {
            GoldStep();
            return goldData != null;
        }

        internal static string FindSelfTeam(string name, List<Dictionary<string, object>> players)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Dictionary<string, object> p in players)
            {
                string riotId = Str(p, "riotId");
                string gameName = Str(p, "riotIdGameName");
                string tag = Str(p, "riotIdTagLine");
                if (name != riotId && name != Str(p, "summonerName") && name != gameName &&
                    name != gameName + "#" + tag) continue;
                string team = Str(p, "team");
                if (team == "ORDER" || team == "CHAOS") return team;
            }
            return null;
        }

        internal static Dictionary<string, object> ComputeGold(List<Dictionary<string, object>> players, string selfTeam)
        {
            long blue = 0, red = 0;
            int priced = 0, missing = 0;
            bool blueComplete = true, redComplete = true;
            foreach (Dictionary<string, object> p in players)
            {
                string team = Str(p, "team");
                if (team != "ORDER" && team != "CHAOS") continue;
                long value = 0;
                foreach (object io in Arr(Get(p, "items")))
                {
                    Dictionary<string, object> item = io as Dictionary<string, object>;
                    if (item == null || ToLong(Get(item, "itemID")) == 0) continue;
                    object raw = Get(item, "price");
                    decimal price;
                    if (raw == null || !decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out price) || price < 0)
                    {
                        missing++;
                        if (team == "ORDER") blueComplete = false; else redComplete = false;
                        continue;
                    }
                    value += (long)Math.Round(price * Math.Max(1, ToInt(Get(item, "count"))));
                    priced++;
                }
                if (team == "ORDER") blue += value; else red += value;
            }
            long diff = selfTeam == "CHAOS" ? red - blue : blue - red;
            bool complete = missing == 0;
            return new Dictionary<string, object>
            {
                { "blue", blueComplete ? (object)blue : null }, { "red", redComplete ? (object)red : null },
                { "diff", complete ? (object)diff : null },
                { "blueText", blueComplete ? FmtVal(blue) : "--" }, { "redText", redComplete ? FmtVal(red) : "--" },
                { "diffText", complete ? FmtDiff(diff) : "--" }, { "ahead", complete && diff > 0 },
                { "selfTeam", selfTeam }, { "at", DateTimeOffset.Now.ToUnixTimeMilliseconds() },
                { "players", players.Count }, { "pricedItems", priced }, { "unpricedItems", missing },
            };
        }

        internal void GoldStep()
        {
            if (Interlocked.CompareExchange(ref goldBusy, 1, 0) != 0) return;
            try
            {
                if (!Cfg.enableGoldDiff || !isHex)
                {
                    goldData = null; selfTeamResolved = false;
                    SetGoldVisible(false);
                    return;
                }
                List<Dictionary<string, object>> players = LivePlayerList(out GoldError);
                if (players == null || players.Count == 0)
                {
                    if (!inGame) { goldData = null; selfTeamResolved = false; SetGoldVisible(false); }
                    return;
                }
                if (!selfTeamResolved)
                {
                    try
                    {
                        object active = Ser.DeserializeObject(LiveGet("activeplayername"));
                        Dictionary<string, object> obj = active as Dictionary<string, object>;
                        string name = active as string;
                        if (obj != null) name = Str(obj, "riotId") ?? Str(obj, "summonerName");
                        string team = FindSelfTeam(name, players);
                        if (team != null) { SelfTeam = team; selfTeamResolved = true; }
                    }
                    catch { }
                }
                goldData = ComputeGold(players, SelfTeam);
                GoldError = ToInt(Get(goldData, "unpricedItems")) > 0
                    ? "2999 返回的装备缺少价格，未知数值显示 --；请导出对局内数据" : "";
                GoldUpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                if (GoldShown && GoldVisible != null) GoldVisible(true, goldData);
            }
            catch (Exception ex) { GoldError = ex.Message; }
            finally { Interlocked.Exchange(ref goldBusy, 0); }
        }

        public static string FmtVal(long v)
        {
            long r = Math.Abs(v);
            if (r >= 1000) return (v / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "K";
            return v.ToString(CultureInfo.InvariantCulture);
        }

        public static string FmtDiff(long v)
        {
            if (v == 0) return "0";
            return (v > 0 ? "+" : "-") + FmtVal(Math.Abs(v));
        }

        public void SetGoldVisible(bool visible)
        {
            GoldShown = visible;
            if (GoldVisible != null) GoldVisible(GoldShown, goldData);
            Push();
        }

        /// <summary>给浮层取当前经济差数据（没数据返回 null）</summary>
        public Dictionary<string, object> GoldData() { return goldData; }

        /* ---------------- 自动接受 ---------------- */
        public Dictionary<string, object> AutoAcceptInfo()
        {
            return new Dictionary<string, object>
            {
                { "enabled", Cfg.autoAccept }, { "armed", autoArmed },
                { "acceptedThisGame", acceptedThisGame }, { "queueId", readyQueue },
                { "queueAllowed", readyQueue == 0 || Cfg.QueueAllowed(readyQueue) },
                { "delayMin", Cfg.autoAcceptDelayMin }, { "delayMax", Cfg.autoAcceptDelayMax },
                { "phase", phase }, { "state", readyState }, { "playerResponse", readyResponse },
                { "lastError", readyError }, { "updatedAt", readyUpdatedAt },
            };
        }

        internal static int LobbyQueueId(Dictionary<string, object> lobby)
        {
            int q = ToInt(Get(Get(lobby, "gameConfig") as Dictionary<string, object>, "queueId"));
            return q > 0 ? q : ToInt(Get(lobby, "queueId"));
        }

        int CurrentQueueId(Dictionary<string, object> rc)
        {
            int q = ToInt(Get(rc, "queueId"));
            if (q > 0) return q;
            try
            {
                q = LobbyQueueId(Ser.Deserialize<Dictionary<string, object>>(Lcu.Get("/lol-lobby/v2/lobby")));
                if (q > 0) return q;
            }
            catch { }
            try
            {
                q = QueueIdOf(Ser.Deserialize<Dictionary<string, object>>(Lcu.GameflowJson()));
                if (q > 0) return q;
            }
            catch { }
            return lastKnownQueue;
        }

        void ResetReady()
        {
            autoArmed = false;
            acceptedThisGame = false;
            acceptDue = DateTime.MinValue;
            acceptRetryAfter = DateTime.MinValue;
        }

        internal void ReadyStep()
        {
            if (Interlocked.CompareExchange(ref readyBusy, 1, 0) != 0) return;
            try
            {
                LcuClient client = Lcu;
                if (client != readyClient) { ResetReady(); readyClient = client; readyQueue = 0; }
                if (client == null || !Cfg.autoAccept) { ResetReady(); return; }

                // ready-check 是判定依据，不能被滞后的 gameflow.phase 挡住。
                Dictionary<string, object> rc;
                try
                {
                    rc = Ser.Deserialize<Dictionary<string, object>>(client.Get("/lol-matchmaking/v1/ready-check"));
                    readyState = Str(rc, "state") ?? "";
                    readyResponse = Str(rc, "playerResponse") ?? "";
                    if (DateTime.UtcNow >= acceptRetryAfter) readyError = "";
                    readyUpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                }
                catch (Exception ex)
                {
                    readyError = ex.Message;
                    LcuException le = ex as LcuException;
                    if (le != null && le.Status == 404) ResetReady();
                    return;
                }

                // 包括被他人拒绝/超时后重新匹配：不用等到进选人才复位。
                if (readyState != "InProgress" || readyResponse != "None") { ResetReady(); return; }
                if (acceptedThisGame || DateTime.UtcNow < acceptRetryAfter) return;

                if (!autoArmed)
                {
                    readyQueue = CurrentQueueId(rc);
                    if (readyQueue > 0 && !Cfg.QueueAllowed(readyQueue))
                    {
                        if (lastSkipLog != readyQueue)
                        {
                            lastSkipLog = readyQueue;
                            Log("debug", "队列 " + readyQueue + " 不在海斗白名单，不自动接受");
                        }
                        return;
                    }
                    int lo = Math.Max(0, Math.Min(10000, Math.Min(Cfg.autoAcceptDelayMin, Cfg.autoAcceptDelayMax)));
                    int hi = Math.Max(lo, Math.Min(10000, Math.Max(Cfg.autoAcceptDelayMin, Cfg.autoAcceptDelayMax)));
                    int delay = lo == hi ? lo : new Random().Next(lo, hi + 1);
                    acceptDue = DateTime.UtcNow.AddMilliseconds(delay);
                    autoArmed = true;
                    Log("info", "检测到待接受对局 · 队列 " + readyQueue + " · " + delay + "ms 后自动接受");
                    Push();
                }

                if (DateTime.UtcNow < acceptDue) return;
                // 延迟期间每轮都会复查待接受状态、开关和连接；不再用失去引用的一次性 Timer。
                if (!Cfg.autoAccept || client != Lcu) { ResetReady(); return; }
                try
                {
                    client.Post("/lol-matchmaking/v1/ready-check/accept", new Dictionary<string, object>());
                    acceptedThisGame = true;
                    readyError = "";
                    Log("ok", "已自动接受对局");
                }
                catch (Exception ex)
                {
                    readyError = ex.Message;
                    acceptRetryAfter = DateTime.UtcNow.AddSeconds(5);
                    Log("warn", "自动接受失败：" + ex.Message + "（5s 后重试）");
                }
                finally { autoArmed = false; Push(); }
            }
            finally { Interlocked.Exchange(ref readyBusy, 0); }
        }

        int lastSkipLog;

        /* ---------------- 聊天播报 ---------------- */
        public string[] BuildReportMessages()
        {
            List<string> messages = new List<string>();
            foreach (Mate m in mates)
            {
                string who = !string.IsNullOrEmpty(m.name)
                    ? (string.IsNullOrEmpty(m.tag) ? m.name : m.name + "#" + m.tag) : "ID已隐藏";
                string rank = string.IsNullOrEmpty(m.rankName) ? "无数据" : m.rankName;
                string stat = m.winRate >= 0
                    ? "kda " + (string.IsNullOrEmpty(m.kda) ? "--" : m.kda) + " 最近胜率 " + m.winRate + "%"
                    : "kda -- 最近胜率 --";
                messages.Add(who + " " + rank + " " + stat);
            }
            return messages.ToArray();
        }

        public string BuildReport(bool includeSelf)
        {
            return string.Join("\n", BuildReportMessages());
        }

        int chatBusy = 0;
        public Dictionary<string, object> SendChatReport(bool auto)
        {
            if (Interlocked.CompareExchange(ref chatBusy, 1, 0) != 0)
                return new Dictionary<string, object> { { "ok", false }, { "error", "队友战绩正在发送" } };
            try
            {
                if (Lcu == null)
                    return new Dictionary<string, object> { { "ok", false }, { "error", "未连接客户端" } };
                string[] messages = BuildReportMessages();
                if (messages.Length == 0)
                    return new Dictionary<string, object> { { "ok", false }, { "error", "没有可发的队友数据" } };
                List<object> attempts = new List<object>(), results = new List<object>();
                int sent = 0;
                string level = "";
                string preferredLevel = "", conversationId = "";
                foreach (string text in messages)
                {
                    if (sent > 0) Thread.Sleep(350);
                    Dictionary<string, object> result = SendChatMessage(text, ref preferredLevel, ref conversationId);
                    results.Add(result);
                    foreach (object attempt in (List<object>)result["attempts"])
                    {
                        Dictionary<string, object> a = (Dictionary<string, object>)attempt;
                        a["messageIndex"] = sent + 1;
                        attempts.Add(a);
                    }
                    if (!(bool)result["ok"]) break;
                    sent++;
                    level = Str(result, "level") ?? "";
                }
                bool ok = sent == messages.Length;
                string error = ok ? "" : "已发送 " + sent + "/" + messages.Length + " 条，后续发送失败";
                string report = string.Join("\n", messages);
                ChatState["lastOk"] = ok; ChatState["lastError"] = error;
                ChatState["lastText"] = report; ChatState["lastLevel"] = level; ChatState["attempts"] = attempts;
                ChatState["sentCount"] = sent; ChatState["totalCount"] = messages.Length; ChatState["messages"] = results;
                Log(ok ? "ok" : "warn", ok
                    ? "已逐条发送队友战绩 " + sent + " 条（" + (auto ? "自动" : "手动") + "）" : error);
                Push();
                return new Dictionary<string, object>
                {
                    { "ok", ok }, { "error", error }, { "text", report }, { "level", level },
                    { "sentCount", sent }, { "totalCount", messages.Length }, { "messages", results }, { "attempts", attempts },
                };
            }
            finally { Interlocked.Exchange(ref chatBusy, 0); }
        }


        Dictionary<string, object> SendChatMessage(string text, ref string preferredLevel, ref string conversationId)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            List<object> attempts = new List<object>();
            string okLevel = null;

            // 通道 1-3：champ-select chat-message 的几种 body
            object[][] bodies = new object[][]
            {
                new object[] { "champ-message", new Dictionary<string, object> { { "message", text } } },
                new object[] { "champ-message-typed", new Dictionary<string, object> { { "message", text }, { "type", "chat" } } },
                new object[] { "champ-message-championselect", new Dictionary<string, object> { { "message", text }, { "type", "championSelect" } } },
            };
            foreach (object[] b in bodies)
            {
                string name = (string)b[0];
                if (preferredLevel.Length > 0 && preferredLevel != name) continue;
                try
                {
                    Lcu.Post("/lol-champ-select/v1/session/chat-message", b[1]);
                    attempts.Add(new Dictionary<string, object> { { "level", name }, { "ok", true } });
                    okLevel = name; break;
                }
                catch (LcuException le)
                {
                    attempts.Add(new Dictionary<string, object> { { "level", name }, { "ok", false }, { "status", le.Status }, { "error", le.Message } });
                    if (le.Status != 400 && le.Status != 404 && le.Status != 405) break;
                }
                catch (Exception ex)
                {
                    attempts.Add(new Dictionary<string, object> { { "level", name }, { "ok", false }, { "status", 0 }, { "error", ex.Message } });
                }
            }

            // 通道 4-5：发现选人会话后走 lol-chat
            if (okLevel == null)
            {
                string convId = string.IsNullOrEmpty(conversationId) ? DiscoverChampSelectConversation() : conversationId;
                conversationId = convId;
                if (!string.IsNullOrEmpty(convId))
                {
                    object[][] chatBodies = new object[][]
                    {
                        new object[] { "conversation-basic", new Dictionary<string, object> { { "body", text }, { "type", "chat" } } },
                        new object[] { "conversation-extended", new Dictionary<string, object> { { "body", text }, { "type", "chat" }, { "fromSummonerId", SelfSummonerId } } },
                    };
                    foreach (object[] b in chatBodies)
                    {
                        string name = (string)b[0];
                        if (preferredLevel.StartsWith("conversation-") && preferredLevel != name) continue;
                        try
                        {
                            Lcu.Post("/lol-chat/v1/conversations/" + Uri.EscapeDataString(convId) + "/messages", b[1]);
                            attempts.Add(new Dictionary<string, object> { { "level", name }, { "ok", true } });
                            okLevel = name; break;
                        }
                        catch (LcuException le)
                        {
                            attempts.Add(new Dictionary<string, object> { { "level", name }, { "ok", false }, { "status", le.Status }, { "error", le.Message } });
                        }
                        catch (Exception ex)
                        {
                            attempts.Add(new Dictionary<string, object> { { "level", name }, { "ok", false }, { "status", 0 }, { "error", ex.Message } });
                        }
                    }
                }
                else
                {
                    attempts.Add(new Dictionary<string, object>
                    {
                        { "level", "conversation" },
                        { "ok", false },
                        { "status", 0 },
                        { "error", "没发现选人会话" },
                    });
                }
            }

            bool ok = okLevel != null;
            if (ok) preferredLevel = okLevel;
            r["ok"] = ok; r["level"] = okLevel; r["text"] = text; r["attempts"] = attempts;
            if (!ok) r["error"] = "所有通道都失败";
            return r;
        }

        string DiscoverChampSelectConversation()
        {
            try
            {
                string room = Lcu.Get("/lol-champ-select/v1/session/chat-room");
                Dictionary<string, object> d = Ser.Deserialize<Dictionary<string, object>>(room);
                string jid = Str(d, "jid") ?? Str(d, "chatRoomName");
                if (!string.IsNullOrEmpty(jid)) return jid;
            }
            catch { }

            try
            {
                object arr = Ser.DeserializeObject(Lcu.Get("/lol-chat/v1/conversations"));
                foreach (object o in arr as IEnumerable)
                {
                    Dictionary<string, object> c = o as Dictionary<string, object>;
                    if (c == null) continue;
                    string id = Str(c, "id"); string type = (Str(c, "type") ?? "").ToLowerInvariant();
                    if (string.IsNullOrEmpty(id)) continue;
                    if (type.Contains("champ") || id.ToLowerInvariant().Contains("champ")) return id;
                }
            }
            catch { }
            return null;
        }

        void MaybeAutoChat()
        {
            if (string.IsNullOrEmpty(lastIdentityKey) || mates.Count == 0) return;
            if (chatSentKey == lastIdentityKey) return;
            chatSentKey = lastIdentityKey;
            SendChatReport(true);
        }

        /* ---------------- 状态快照 ---------------- */
        public Dictionary<string, object> Snapshot()
        {
            Dictionary<string, object> s = new Dictionary<string, object>();
            s["connected"] = connected;
            s["connecting"] = connecting;
            s["waitAttempts"] = waitAttempts;
            s["error"] = error;
            s["tickSkips"] = tickSkips;
            s["port"] = Lcu != null ? Lcu.Port : 0;
            s["phase"] = phase;
            s["phaseText"] = PhaseText(phase);
            s["queueId"] = lastKnownQueue;
            s["isHex"] = isHex;
            s["inGame"] = inGame;
            s["champion"] = champion;
            s["championId"] = championId;
            s["hex"] = Hex.Snapshot();



            s["lolPath"] = LolPath;
            s["keyKind"] = "win32";
            s["ws"] = WsState;
            s["gold"] = new Dictionary<string, object>
            {
                { "visible", GoldShown },
                { "data", goldData },
                { "ok", goldData != null && GoldError.Length == 0 },
                { "error", GoldError.Length > 0 ? GoldError : (goldData == null ? "等待对局数据" : "") },
                { "updatedAt", GoldUpdatedAt },
            };

            List<object> ms = new List<object>();
            foreach (Mate m in mates)
            {
                ms.Add(new Dictionary<string, object>
                {
                    { "cellId", m.cellId },
                    { "championId", m.championId },
                    { "champion", m.champion },
                    { "name", m.name },
                    { "tag", m.tag },
                    { "note", m.note },
                    { "rank", string.IsNullOrEmpty(m.rankName) ? null : new Dictionary<string, object> { { "name", m.rankName }, { "key", m.rankKey } } },
                    { "history", m.winRate >= 0
                        ? new Dictionary<string, object> { { "total", m.total }, { "wins", m.wins }, { "winRate", m.winRate }, { "kda", m.kda }, { "avg", "" } }
                        : new Dictionary<string, object> { { "note", m.note } } },
                });
            }
            s["teammates"] = ms;
            s["teammateStats"] = new Dictionary<string, object>
            {
                { "nameRequests", nameRequests }, { "historyRequests", historyRequests }, { "builds", builds },
            };
            s["chat"] = new Dictionary<string, object>
            {
                { "enabled", Cfg.chatReport },
                { "sent", !string.IsNullOrEmpty(chatSentKey) },
                { "lastOk", ChatState.ContainsKey("lastOk") ? ChatState["lastOk"] : null },
                { "lastError", ChatState.ContainsKey("lastError") ? ChatState["lastError"] : "" },
                { "lastLevel", ChatState.ContainsKey("lastLevel") ? ChatState["lastLevel"] : "" },
                { "attempts", ChatState.ContainsKey("attempts") ? ChatState["attempts"] : new List<object>() },
                { "lastText", ChatState.ContainsKey("lastText") ? ChatState["lastText"] : "" },
                { "sentCount", Get(ChatState, "sentCount") ?? 0 },
                { "totalCount", Get(ChatState, "totalCount") ?? 0 },
                { "messages", Get(ChatState, "messages") ?? new List<object>() },
            };
            s["autoAccept"] = AutoAcceptInfo();
            s["config"] = Cfg;
            return s;
        }

        public void Push()
        {
            Dictionary<string, object> s = Snapshot();
            try { if (StatusChanged != null) StatusChanged(s); } catch { }
        }

        public static string PhaseText(string p)
        {
            switch (p)
            {
                case "None": return "未启动";
                case "Lobby": return "大厅";
                case "Matchmaking": return "匹配中";
                case "ReadyCheck": return "待接受";
                case "ChampSelect": return "选人";
                case "GameStart": return "载入";
                case "InProgress": return "对局中";
                case "Reconnect": return "重连中";
                case "WaitingForStats": return "等待结算";
                case "PreEndOfGame": return "对局结束前";
                case "EndOfGame": return "结算";
                case "TerminatedInError": return "异常结束";
                default: return p;
            }
        }

        /* ---------------- JSON 小工具 ---------------- */
        public static object Get(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            return v == null ? null : v.ToString();
        }

        public static IEnumerable Arr(object o) { return o as IEnumerable ?? new object[0]; }

        public static int ToInt(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        public static long ToLong(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt64(o, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        static int QueueIdOf(Dictionary<string, object> gf)
        {
            object gd = Get(gf, "gameData");
            Dictionary<string, object> d = gd as Dictionary<string, object>;
            if (d == null) return 0;
            object q = Get(d, "queue");
            Dictionary<string, object> qd = q as Dictionary<string, object>;
            if (qd != null) return ToInt(Get(qd, "id"));
            return ToInt(Get(d, "queueId"));
        }
    }
}
