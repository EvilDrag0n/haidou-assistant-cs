using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace HaiDou
{
    /// <summary>调试页的能力：全部只读（除了明确标注的 accept）</summary>
    static class DebugProbe
    {
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();

        public struct Result
        {
            public bool ok;
            public string title;
            public string text;
            public object raw;
        }

        static Result R(bool ok, string title, string text)
        {
            return R(ok, title, text, null);
        }

        static Result R(bool ok, string title, string text, object raw)
        {
            Result r = new Result();
            r.ok = ok; r.title = title; r.text = text; r.raw = raw;
            return r;
        }

        static string Get(LcuClient lcu, string path)
        {
            return lcu.Get(path);
        }

        /* ---------------- 全局接口 ---------------- */
        public static Result Globals(Engine eng)
        {
            if (eng.Lcu == null) return R(false, "全局接口探测", "未连接客户端");
            string[] urls =
            {
                "/lol-champ-select/v1/session",
                "/lol-gameflow/v1/session",
                "/lol-lobby/v2/lobby",
                "/lol-lobby/v2/lobby/members",
                "/lol-summoner/v1/current-summoner",
                "/lol-chat/v1/friends",
            };
            List<string> lines = new List<string>();
            Dictionary<string, object> raw = new Dictionary<string, object>();
            foreach (string u in urls)
            {
                try
                {
                    string body = Get(eng.Lcu, u);
                    lines.Add("✅ " + u);
                    raw[u] = "ok";
                }
                catch (LcuException le)
                {
                    bool notFound = le.Status == 404;
                    lines.Add((notFound ? "○  " : "❌ ") + u + "  " + le.Status +
                        (notFound ? "（当前阶段无此资源，正常）" : ""));
                    raw[u] = le.Status;
                }
                catch (Exception ex)
                {
                    lines.Add("❌ " + u + "  " + ex.Message);
                    raw[u] = ex.Message;
                }
            }
            return R(true, "全局接口探测", string.Join("\n", lines.ToArray()), raw);
        }

        /* ---------------- 选人会话 ---------------- */
        public static Result ChampSelect(Engine eng)
        {
            if (eng.Lcu == null) return R(false, "选人会话", "未连接客户端");
            Dictionary<string, object> cs;
            try { cs = Ser.Deserialize<Dictionary<string, object>>(Get(eng.Lcu, "/lol-champ-select/v1/session")); }
            catch (LcuException le)
            {
                return R(false, "选人会话",
                    "拿不到选人会话（" + le.Status + "）\n要在「选人阶段」运行（大厅/游戏中都不行）。", null);
            }

            int selfCell = Engine.ToInt(Engine.Get(cs, "localPlayerCellId"));
            List<string> lines = new List<string>();
            Dictionary<string, object> timer = Engine.Get(cs, "timer") as Dictionary<string, object>;
            lines.Add("timer.phase   " + (Engine.Str(timer, "phase") ?? "?"));
            int count = 0;
            foreach (object o in Engine.Arr(Engine.Get(cs, "myTeam"))) count++;
            lines.Add("我方人数      " + count);
            lines.Add("我（cellId " + selfCell + "）");

            foreach (object o in Engine.Arr(Engine.Get(cs, "myTeam")))
            {
                Dictionary<string, object> p = o as Dictionary<string, object>;
                if (p == null) continue;
                int cell = Engine.ToInt(Engine.Get(p, "cellId"));
                lines.Add("");
                lines.Add("  cellId=" + cell + " championId=" + Engine.ToInt(Engine.Get(p, "championId")));
                lines.Add("    summonerId=" + (Engine.Get(p, "summonerId") ?? "null"));
                lines.Add("    obfuscatedSummonerId=" + (Engine.Get(p, "obfuscatedSummonerId") ?? "null"));
                object pu = Engine.Get(p, "puuid");
                lines.Add("    puuid=" + (pu != null ? Short(pu.ToString()) : "null"));
                object opu = Engine.Get(p, "obfuscatedPuuid");
                lines.Add("    obfuscatedPuuid=" + (opu != null ? Short(opu.ToString()) : "null"));
            }
            return R(true, "选人会话", string.Join("\n", lines.ToArray()), cs);
        }

        static bool ToBool(Dictionary<string, object> d, string k)
        {
            object v = Engine.Get(d, k);
            return v is bool && (bool)v;
        }

        static string Short(string s)
        {
            return s.Length > 14 ? s.Substring(0, 14) + "…" : s;
        }

        /* ---------------- 队友接口逐个探 ---------------- */
        public static Result Teammates(Engine eng)
        {
            if (eng.Lcu == null) return R(false, "队友接口探测", "未连接客户端");
            Dictionary<string, object> cs;
            try { cs = Ser.Deserialize<Dictionary<string, object>>(Get(eng.Lcu, "/lol-champ-select/v1/session")); }
            catch (LcuException le)
            {
                return R(false, "队友接口探测", "不在选人阶段（" + le.Status + "）。要在「选人阶段」运行。", null);
            }

            int selfCell = Engine.ToInt(Engine.Get(cs, "localPlayerCellId"));
            List<string> lines = new List<string>();
            Dictionary<string, object> raw = new Dictionary<string, object>();
            int mates = 0;

            foreach (object o in Engine.Arr(Engine.Get(cs, "myTeam")))
            {
                Dictionary<string, object> p = o as Dictionary<string, object>;
                if (p == null || Engine.ToInt(Engine.Get(p, "cellId")) == selfCell) continue;
                mates++;
                long sid = Engine.ToLong(Engine.Get(p, "summonerId"));
                string puuid = Engine.Get(p, "puuid") == null ? null : Engine.Get(p, "puuid").ToString();
                lines.Add("□ cellId=" + Engine.ToInt(Engine.Get(p, "cellId")) + " championId=" + Engine.ToInt(Engine.Get(p, "championId")));
                lines.Add("   summonerId=" + (sid > 0 ? sid.ToString() : "null") + "  puuid=" + (puuid == null ? "null" : Short(puuid)));

                List<string> calls = new List<string>();
                if (sid > 0)
                {
                    calls.Add("/lol-summoner/v1/summoners/" + sid);
                    calls.Add("/lol-summoner/v2/summoners/ids?ids=" + sid);
                }
                if (!string.IsNullOrEmpty(puuid))
                {
                    calls.Add("/lol-summoner/v2/summoners/puuid/" + puuid);
                    calls.Add("/lol-match-history/v1/products/lol/" + puuid + "/matches?begIndex=0&endIndex=9");
                    calls.Add("/lol-match-history/v3/matchlist/account/" + puuid + "?begIndex=0&endIndex=9");
                    calls.Add("/lol-champion-mastery/v1/" + puuid + "/champion-mastery");
                }

                foreach (string u in calls)
                {
                    try
                    {
                        Get(eng.Lcu, u);
                        lines.Add("   ✅ " + Strip(u));
                        raw[u] = "ok";
                    }
                    catch (LcuException le)
                    {
                        bool nf = le.Status == 404;
                        lines.Add("   " + (nf ? "○  " : "❌ ") + Strip(u) + "  " + le.Status + (nf ? "（当前阶段无此资源，正常）" : ""));
                        raw[u] = le.Status;
                    }
                    catch (Exception ex)
                    {
                        lines.Add("   ❌ " + Strip(u) + "  " + ex.Message);
                        raw[u] = ex.Message;
                    }
                }
                lines.Add("");
            }

            if (mates == 0) lines.Add("选人阶段里没有队友（人数不足或都还没进）。");
            lines.Add("—— 引擎内的请求计数（换英雄不应增长）——");
            lines.Add("名字请求 " + eng.nameRequests + " 次 / 战绩请求 " + eng.historyRequests + " 次 / 重建队友列表 " + eng.builds + " 次");
            return R(true, "队友接口探测", string.Join("\n", lines.ToArray()), raw);
        }

        static string Strip(string url)
        {
            int q = url.IndexOf('?');
            return q > 0 ? url.Substring(0, q) : url;
        }

        /* ---------------- 对局内 2999 ---------------- */
        public static Result Live(Engine eng)
        {
            List<Dictionary<string, object>> players = eng.LivePlayers();
            if (players == null) return R(false, "对局内数据（2999）", "拿不到：2999 没响应。\n要进到游戏里（载入/对局中）才有。", null);

            List<string> lines = new List<string>();
            foreach (Dictionary<string, object> p in players)
            {
                string team = Engine.Str(p, "team");
                string champ = Engine.Str(p, "championName");
                if (string.IsNullOrEmpty(champ)) champ = Engine.ToInt(Engine.Get(p, "championId")).ToString();
                string who = Engine.Str(p, "riotId") ?? Engine.Str(p, "summonerName") ?? "—";
                lines.Add("  " + (team == "ORDER" ? "蓝" : "红") + "  " + champ + "  " + who);
            }
            return R(true, "对局内数据（2999）", string.Join("\n", lines.ToArray()), players);
        }

        /* ---------------- 待接受（只读） ---------------- */
        public static Result ReadyCheck(Engine eng)
        {
            if (eng.Lcu == null) return R(false, "待接受状态（只读）", "未连接客户端");
            try
            {
                Dictionary<string, object> rc = Ser.Deserialize<Dictionary<string, object>>(Get(eng.Lcu, "/lol-matchmaking/v1/ready-check"));
                string text = "state = " + (Engine.Str(rc, "state") ?? "?") +
                              "\nplayerResponse = " + (Engine.Str(rc, "playerResponse") ?? "?") +
                              "\n\n判据：state=InProgress 且 playerResponse=None 才算「待接受」。";
                Dictionary<string, object> info = eng.AutoAcceptInfo();
                text += "\n\n自动接受判定：\n" + Ser.Serialize(info);
                return R(true, "待接受状态（只读）", text, new Dictionary<string, object> { { "readyCheck", rc }, { "decision", info } });
            }
            catch (Exception ex)
            {
                return R(false, "待接受状态（只读）", "查询失败：" + ex.Message, null);
            }
        }

        /* ---------------- 立即接受（会改状态） ---------------- */
        public static Result Accept(Engine eng)
        {
            if (eng.Lcu == null) return R(false, "立即接受", "未连接客户端");
            try
            {
                string resp = eng.Lcu.Post("/lol-matchmaking/v1/ready-check/accept", new Dictionary<string, object>());
                eng.Log("warn", "调试页：手动接受了一次对局");
                return R(true, "立即接受", "已发出接受请求。\n\n" + (string.IsNullOrEmpty(resp) ? "(空响应)" : resp), resp);
            }
            catch (Exception ex)
            {
                return R(false, "立即接受", "失败：" + ex.Message, null);
            }
        }

        /* ---------------- 客户端自带接口清单 ---------------- */
        public static Result Swagger(Engine eng)
        {
            if (eng.Lcu == null) return R(false, "导出接口清单", "未连接客户端");

            string[] paths = { "/swagger/v3/openapi.json", "/swagger/v2/openapi.json", "/swagger.json" };
            string used = null;
            string body = null;
            List<string> errors = new List<string>();
            foreach (string p in paths)
            {
                try
                {
                    body = eng.Lcu.Get(p, 15000);
                    used = p;
                    break;
                }
                catch (Exception ex) { errors.Add(p + ": " + ex.Message); }
            }
            if (body == null)
            {
                return R(false, "导出接口清单",
                    "没取到客户端自带的 swagger：\n  " + string.Join("\n  ", errors.ToArray()) +
                    "\n\n说明这个补丁把 swagger 也关了，只能靠社区镜像对照。", null);
            }

            Dictionary<string, object> doc = Ser.Deserialize<Dictionary<string, object>>(body);
            Dictionary<string, object> pathsObj = Engine.Get(doc, "paths") as Dictionary<string, object>;
            int total = pathsObj == null ? 0 : pathsObj.Count;

            List<string> chat = new List<string>();
            List<string> champ = new List<string>();
            if (pathsObj != null)
            {
                foreach (KeyValuePair<string, object> kv in pathsObj)
                {
                    string low = kv.Key.ToLowerInvariant();
                    if (low.Contains("chat") || low.Contains("conversation") || low.Contains("message") || low.Contains("social"))
                        chat.Add("  " + Methods(kv.Value) + " " + kv.Key);
                    if (low.Contains("champ-select"))
                        champ.Add("  " + Methods(kv.Value) + " " + kv.Key);
                }
            }

            string file = Path.Combine(Paths.DataDir, "lcu-swagger.json");
            long size = 0;
            try
            {
                File.WriteAllText(file, body, new UTF8Encoding(false));
                size = new FileInfo(file).Length;
            }
            catch (Exception ex) { file = "保存失败：" + ex.Message; }

            Dictionary<string, object> info = Engine.Get(doc, "info") as Dictionary<string, object>;
            List<string> lines = new List<string>();
            lines.Add("来源：" + used);
            lines.Add("标题：" + (Engine.Str(info, "title") ?? "") + "  版本：" + (Engine.Str(info, "version") ?? ""));
            lines.Add("接口总数：" + total);
            lines.Add("");
            lines.Add("—— 含 chat / conversation / message 的路径 ——");
            lines.AddRange(chat.Count > 0 ? chat : new List<string> { "  （一个都没有）" });
            lines.Add("");
            lines.Add("—— 含 champ-select 的路径 ——");
            lines.AddRange(champ.Count > 0 ? champ : new List<string> { "  （一个都没有）" });
            lines.Add("");
            lines.Add(size > 0
                ? "完整清单已保存：\n  " + file + "  (" + (size / 1024) + " KB)"
                : file);
            return R(true, "导出接口清单", string.Join("\n", lines.ToArray()), new Dictionary<string, object> { { "total", total }, { "chat", chat } });
        }

        static string Methods(object item)
        {
            Dictionary<string, object> d = item as Dictionary<string, object>;
            if (d == null) return "";
            List<string> ms = new List<string>();
            foreach (string k in d.Keys)
            {
                string up = k.ToUpperInvariant();
                if (up == "GET" || up == "POST" || up == "PUT" || up == "PATCH" || up == "DELETE" || up == "HEAD") ms.Add(up);
            }
            return string.Join(",", ms.ToArray()).PadRight(10);
        }

        /* ---------------- 运行环境 ---------------- */
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

        public static Result Env(Engine eng)
        {
            List<string> lines = new List<string>();
            lines.Add("appVersion      1.3.1-cs");
            lines.Add("os              " + Environment.OSVersion);
            lines.Add("clr             " + Environment.Version);
            lines.Add("64bit           " + Environment.Is64BitProcess);
            lines.Add("dataDir         " + Paths.DataDir);
            lines.Add("dataSource      " + Paths.Source);
            lines.Add("configFile      " + Paths.ConfigFile);
            lines.Add("exe             " + System.Reflection.Assembly.GetExecutingAssembly().Location);
            lines.Add("exeDir          " + Paths.ExeDir);

            string key;
            try
            {
                short st = GetAsyncKeyState(0x09);
                key = "ok（Tab 当前状态 " + st + "）";
            }
            catch (Exception ex) { key = "失败：" + ex.Message; }
            lines.Add("按键读取        " + key);

            string lol = eng.LolPath;
            lines.Add("LoL 目录        " + (lol ?? "(未找到)"));

            string boot = Paths.BootLog;
            lines.Add("");
            lines.Add("--- 启动日志（最近 12 行）---");
            try
            {
                string[] all = File.ReadAllLines(boot);
                int from = Math.Max(0, all.Length - 12);
                for (int i = from; i < all.Length; i++) lines.Add(all[i]);
            }
            catch { lines.Add("(读取失败)"); }

            return R(true, "运行环境", string.Join("\n", lines.ToArray()), null);
        }
    }
}
