using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Net.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HaiDou
{
    // 每次选英雄重新获取版本索引和该英雄统计，不读写推荐缓存。
    class HexData
    {
        public event Action Changed;
        readonly object gate = new object();
        readonly Func<string, string> fetch;
        readonly Action<Action> queue;
        int generation, heroId;
        string dataset = "high";
        Dictionary<string, object> state = Empty(0, "", "high", "idle");

        public HexData() : this(Download, delegate(Action a) { ThreadPool.QueueUserWorkItem(delegate { a(); }); }) { }
        internal HexData(Func<string, string> get, Action<Action> run) { fetch = get; queue = run; }
        public Dictionary<string, object> Snapshot() { lock (gate) return state; }

        static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 100 };
        }
        static Dictionary<string, object> Empty(int id, string name, string ds, string status)
        {
            return new Dictionary<string, object>
            {
                { "status", status }, { "championId", id }, { "champion", name }, { "dataset", ds },
                { "source", "ARAMKit" }, { "version", "" }, { "dataDate", "" }, { "fetchedAt", "" },
                { "error", "" }, { "augments", new object[0] }, { "stages", new Dictionary<string, object>() },
                { "combinations", new object[0] }, { "championSamples", 0 }, { "datasetMatches", 0 },
            };
        }
        void Notify() { Action handler = Changed; if (handler != null) handler(); }
        public void Select(int id, string name, string ds, bool force)
        {
            ds = ds == "all" ? "all" : "high";
            int request;
            lock (gate)
            {
                if (!force && id == heroId && ds == dataset) return;
                heroId = id; dataset = ds; request = ++generation;
                state = Empty(id, name, ds, id > 0 ? "loading" : "idle");
            }
            Notify();
            if (id <= 0) return;
            queue(delegate { Load(request, id, name, ds); });
        }
        bool Current(int request) { lock (gate) return request == generation; }
        void Publish(int request, Dictionary<string, object> next)
        {
            lock (gate) { if (request != generation) return; state = next; }
            Notify();
        }
        string Read(int request, string url)
        {
            if (!Current(request)) throw new OperationCanceledException();
            string result = fetch(url);
            if (!Current(request)) throw new OperationCanceledException();
            return result;
        }
        void Load(int request, int id, string name, string ds)
        {
            List<string> errors = new List<string>();
            foreach (string host in new string[] { "https://data.aramkit.cn/", "https://data.aramkit.com/" })
            {
                if (!Current(request)) return;
                try
                {
                    Dictionary<string, object> index = Parse(Read(request, host + "data/versions.json"));
                    Dictionary<string, object> version = Latest(index);
                    string path = SafePath(Engine.Str(version, "dataPath"), "data");
                    string resources = SafePath(Engine.Str(version, "resourcePath"), "resources");
                    string detailUrl = host + path + "/stats/" + ds + "/champion-details/" + id + ".json";
                    string namesUrl = host + resources + "/zh-CN/resources/augments.json";
                    Task<string> detail = Task.Factory.StartNew(delegate { return Read(request, detailUrl); });
                    Task<string> names = Task.Factory.StartNew(delegate { return Read(request, namesUrl); });
                    Task.WaitAll(detail, names);
                    Dictionary<string, object> result = Normalize(Parse(detail.Result), Parse(names.Result), id, name, ds, version);
                    result["endpoint"] = detailUrl;
                    result["fetchedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                    Publish(request, result);
                    return;
                }
                catch (Exception ex)
                {
                    if (!Current(request)) return;
                    Exception root = ex.GetBaseException();
                    errors.Add(new Uri(host).Host + ": " + root.Message);
                }
            }
            Dictionary<string, object> failed = Empty(id, name, ds, "error");
            failed["error"] = "实时获取失败，请检查网络后重试。";
            failed["details"] = string.Join(" | ", errors.ToArray());
            Publish(request, failed);
        }
        internal static Dictionary<string, object> Parse(string json)
        {
            Dictionary<string, object> value = Serializer().DeserializeObject(json) as Dictionary<string, object>;
            if (value == null) throw new Exception("统计数据不是 JSON 对象");
            return value;
        }
        internal static Dictionary<string, object> Latest(Dictionary<string, object> index)
        {
            string latest = Engine.Str(index, "latest");
            foreach (object o in Engine.Arr(Engine.Get(index, "versions")))
            {
                Dictionary<string, object> row = o as Dictionary<string, object>;
                if (row != null && !string.IsNullOrEmpty(latest) && Engine.Str(row, "version") == latest) return row;
            }
            throw new Exception("最新版本索引缺失，未改用旧版本");
        }
        static string SafePath(string path, string prefix)
        {
            if (string.IsNullOrEmpty(path) || !Regex.IsMatch(path, "^" + prefix + "/[A-Za-z0-9_][A-Za-z0-9_.-]*$") || path.Contains(".."))
                throw new Exception("数据路径格式异常");
            return path;
        }
        static double? Rate(Dictionary<string, object> row, string key)
        {
            object raw = Engine.Get(row, key);
            double value;
            if (raw == null || !double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1) return null;
            return value;
        }
        static string Description(Dictionary<string, object> resource)
        {
            List<string> blocks = new List<string>();
            Dictionary<string, object> tooltip = Engine.Get(resource, "tooltip") as Dictionary<string, object>;
            foreach (object b in Engine.Arr(Engine.Get(tooltip, "blocks")))
            {
                StringBuilder text = new StringBuilder();
                Dictionary<string, object> block = b as Dictionary<string, object>;
                foreach (object s in Engine.Arr(Engine.Get(block, "spans")))
                {
                    Dictionary<string, object> span = s as Dictionary<string, object>;
                    text.Append(Engine.Str(span, "text") ?? Engine.Str(span, "label") ?? "");
                }
                if (text.Length > 0) blocks.Add(text.ToString());
            }
            return string.Join("\n", blocks.ToArray());
        }
        static Dictionary<string, object> Resource(int id, Dictionary<string, object> resources)
        {
            Dictionary<string, object> row = Engine.Get(resources, id.ToString(CultureInfo.InvariantCulture)) as Dictionary<string, object>;
            return new Dictionary<string, object>
            {
                { "id", id }, { "name", Engine.Str(row, "name") ?? ("海克斯 #" + id) },
                { "rarity", Engine.Str(row, "rarity") ?? "" }, { "description", Description(row) },
            };
        }
        static List<object> Augments(object input, Dictionary<string, object> resources)
        {
            List<object> result = new List<object>();
            HashSet<int> seen = new HashSet<int>();
            foreach (object o in Engine.Arr(input))
            {
                Dictionary<string, object> row = o as Dictionary<string, object>;
                int id = Engine.ToInt(Engine.Get(row, "id"));
                double? win = Rate(row, "winRate");
                long samples = Engine.ToLong(Engine.Get(row, "sampleCount"));
                if (id <= 0 || !win.HasValue || samples <= 0 || !seen.Add(id) || object.Equals(Engine.Get(row, "hasData"), false)) continue;
                Dictionary<string, object> normalized = Resource(id, resources);
                normalized["winRate"] = win.Value; normalized["sampleCount"] = samples;
                normalized["pickRate"] = Rate(row, "pickRate"); normalized["tier"] = Engine.Str(row, "tier") ?? "";
                normalized["rank"] = Engine.ToInt(Engine.Get(row, "rank"));
                normalized["stageAgnostic"] = object.Equals(Engine.Get(row, "stageAgnostic"), true);
                normalized["availableStages"] = Engine.Get(row, "availableStages") ?? new object[0];
                result.Add(normalized);
            }
            return result;
        }
        internal static Dictionary<string, object> Normalize(Dictionary<string, object> detail,
            Dictionary<string, object> resources, int id, string name, string ds, Dictionary<string, object> version)
        {
            Dictionary<string, object> champion = Engine.Get(detail, "champion") as Dictionary<string, object>;
            if (Engine.ToInt(Engine.Get(champion, "id")) != id) throw new Exception("返回的英雄与当前选择不一致");
            if (resources.Count == 0) throw new Exception("海克斯名称字典为空");
            Dictionary<string, object> augments = Engine.Get(detail, "augments") as Dictionary<string, object>;
            Dictionary<string, object> stages = Engine.Get(augments, "stages") as Dictionary<string, object>;
            Dictionary<string, object> stageRows = new Dictionary<string, object>();
            for (int i = 1; i <= 4; i++) stageRows[i.ToString()] = Augments(Engine.Get(stages, i.ToString()), resources);
            List<object> rows = Augments(Engine.Get(augments, "all"), resources), combinations = new List<object>();
            HashSet<string> seen = new HashSet<string>();
            foreach (object o in Engine.Arr(Engine.Get(detail, "augmentCombinations")))
            {
                Dictionary<string, object> row = o as Dictionary<string, object>;
                double? win = Rate(row, "winRate");
                long samples = Engine.ToLong(Engine.Get(row, "sampleCount"));
                List<int> ids = new List<int>();
                foreach (object a in Engine.Arr(Engine.Get(row, "augmentIds")))
                {
                    int value = Engine.ToInt(a);
                    if (value > 0 && !ids.Contains(value)) ids.Add(value);
                }
                ids.Sort();
                string key = string.Join("+", ids.ConvertAll<string>(delegate(int value) { return value.ToString(); }).ToArray());
                if (ids.Count < 2 || !win.HasValue || samples <= 0 || !seen.Add(key)) continue;
                List<object> members = ids.ConvertAll<object>(delegate(int value) { return Resource(value, resources); });
                combinations.Add(new Dictionary<string, object>
                {
                    { "key", key }, { "augments", members }, { "winRate", win.Value }, { "sampleCount", samples },
                    { "pickRate", Rate(row, "pickRate") }, { "rank", Engine.ToInt(Engine.Get(row, "rank")) },
                });
            }
            Dictionary<string, object> result = Empty(id, name, ds, rows.Count > 0 || combinations.Count > 0 ? "ready" : "empty");
            result["version"] = Engine.Str(version, "version") ?? "";
            result["dataDate"] = Engine.Str(version, "dataDate") ?? "";
            result["buildTimeUnixMs"] = Engine.Get(version, "buildTimeUnixMs");
            result["datasetMatches"] = Engine.ToLong(Engine.Get(version, ds == "all" ? "allMatches" : "highMatches"));
            Dictionary<string, object> stats = Engine.Get(champion, "stats") as Dictionary<string, object>;
            result["championSamples"] = Engine.ToLong(Engine.Get(stats, "sampleCount"));
            result["championWinRate"] = Rate(stats, "winRate");
            result["augments"] = rows; result["stages"] = stageRows; result["combinations"] = combinations;
            return result;
        }
        internal static string Download(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string fresh = url + (url.Contains("?") ? "&" : "?") + "_=" + DateTime.UtcNow.Ticks;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(fresh);
            req.Timeout = 10000; req.ReadWriteTimeout = 10000; req.KeepAlive = false;
            req.UserAgent = "HaiDouAssistant/1.3.0";
            req.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            req.Headers[HttpRequestHeader.CacheControl] = "no-cache, no-store";
            req.ServerCertificateValidationCallback = delegate(object sender, System.Security.Cryptography.X509Certificates.X509Certificate cert,
                System.Security.Cryptography.X509Certificates.X509Chain chain, SslPolicyErrors errors) { return errors == SslPolicyErrors.None; };
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.ServicePoint.ConnectionLimit = 8;
            using (HttpWebResponse response = (HttpWebResponse)req.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                StringBuilder text = new StringBuilder(); char[] buffer = new char[8192]; int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    text.Append(buffer, 0, count);
                    if (text.Length > 8 * 1024 * 1024) throw new Exception("统计数据超过大小限制");
                }
                return text.ToString();
            }
        }
    }
}
