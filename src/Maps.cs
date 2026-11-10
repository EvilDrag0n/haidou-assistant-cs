using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace HaiDou
{
    class BuildSet
    {
        public string title { get; set; }
        public string[] ids { get; set; }
        public string[] names { get; set; }
    }

    /// <summary>静态数据、LoL 目录探测及旧出装清理</summary>
    static class Maps
    {
        public const string Prefix = "!!!hxdou-";


        public static Dictionary<string, string> IdToName = new Dictionary<string, string>();
        public static Dictionary<string, string> NameToKey = new Dictionary<string, string>();
        public static Dictionary<string, List<BuildSet>> Builds = new Dictionary<string, List<BuildSet>>();

        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();

        public static void Load()
        {
            IdToName = LoadDict("champion_id_to_name.json");
            NameToKey = LoadDict("champion_name_to_key.json");
            try
            {
                string p = Path.Combine(Paths.DataFolder, "builds-flat.json");
                Builds = Ser.Deserialize<Dictionary<string, List<BuildSet>>>(File.ReadAllText(p, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Log.Boot("builds-flat.json 读取失败：" + ex.Message);
                Builds = new Dictionary<string, List<BuildSet>>();
            }
        }

        static Dictionary<string, string> LoadDict(string file)
        {
            try
            {
                string p = Path.Combine(Paths.DataFolder, file);
                return Ser.Deserialize<Dictionary<string, string>>(File.ReadAllText(p, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Log.Boot(file + " 读取失败：" + ex.Message);
                return new Dictionary<string, string>();
            }
        }

        public static string ChampionName(int championId)
        {
            string n;
            return IdToName.TryGetValue(championId.ToString(), out n) ? n : "";
        }

        /// <summary>旧推荐文件的候选目录（仅供清理）</summary>
        public static List<string> RecommendedDirs(string installPath)
        {
            List<string> outDirs = new List<string>();
            if (string.IsNullOrEmpty(installPath)) return outDirs;
            string root = NormalizeLolRoot(installPath) ?? installPath;
            string a = Path.Combine(root, "Config", "Global", "Recommended");
            string b = Path.Combine(root, "Game", "Config", "Global", "Recommended");
            // 清理只访问现有目录，不创建游戏目录。
            if (Directory.Exists(Path.Combine(root, "Game", "Config", "Global"))) outDirs.Add(b);
            if (Directory.Exists(Path.Combine(root, "Config", "Global"))) outDirs.Add(a);
            if (outDirs.Count == 0) outDirs.Add(b);
            return outDirs;
        }

        public static int Cleanup(string installPath)
        {
            int n = 0;
            foreach (string dir in RecommendedDirs(installPath))
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (string f in Directory.GetFiles(dir, Prefix + "*.json")) { File.Delete(f); n++; }
                }
                catch { }
            }
            return n;
        }

        public static string GetRecommendedDir(string installPath)
        {
            if (string.IsNullOrEmpty(installPath)) return null;
            string root = NormalizeLolRoot(installPath) ?? installPath;
            string a = Path.Combine(root, "Config", "Global", "Recommended");
            string b = Path.Combine(root, "Game", "Config", "Global", "Recommended");
            if (Directory.Exists(a)) return a;
            if (Directory.Exists(b)) return b;
            return Directory.Exists(Path.Combine(root, "Game")) ? b : a;
        }

        /* ---------------- LoL 目录探测 ---------------- */

        public static string NormalizeLolRoot(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            string d = dir.Trim().Trim('"');
            string name = Path.GetFileName(d).ToLowerInvariant();
            if (name == "game" || name == "leagueclient") d = Path.GetDirectoryName(d);
            if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) return null;

            if (Directory.Exists(Path.Combine(d, "LeagueClient"))
                || Directory.Exists(Path.Combine(d, "Config", "Global"))
                || Directory.Exists(Path.Combine(d, "Game", "Config", "Global")))
                return d;
            return null;
        }

        static IEnumerable<string> FromProcess(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) yield break;
            string root = Path.GetDirectoryName(Path.GetDirectoryName(exePath));
            if (!string.IsNullOrEmpty(root))
            {
                yield return root;
                yield return Path.Combine(root, "Game");
                string up = Path.GetDirectoryName(root);
                if (!string.IsNullOrEmpty(up)) yield return up;
            }
        }

        static IEnumerable<string> FromRegistry()
        {
            string[][] keys = new string[][]
            {
                new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Riot Game league_of_legends.live", "InstallLocation" },
                new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Riot Game league_of_legends.live", "InstallLocation" },
                new[] { @"SOFTWARE\WOW6432Node\Tencent\LOL", "InstallPath" },
                new[] { @"SOFTWARE\Tencent\LOL", "InstallPath" },
            };
            foreach (string[] k in keys)
            {
                string v = null;
                try
                {
                    using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(k[0]))
                        if (rk != null) v = rk.GetValue(k[1]) as string;
                    if (string.IsNullOrEmpty(v))
                        using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(k[0]))
                            if (rk != null) v = rk.GetValue(k[1]) as string;
                }
                catch { }
                if (!string.IsNullOrEmpty(v)) yield return v;
            }
        }

        static string[] CommonPaths()
        {
            string[] drives = { "C:", "D:", "E:", "F:" };
            string[] rels =
            {
                @"Riot Games\League of Legends",
                @"Program Files\Riot Games\League of Legends",
                @"Program Files (x86)\Riot Games\League of Legends",
                @"WeGameApps\英雄联盟",
                @"WeGameApps\LOL",
                @"Program Files\腾讯游戏\英雄联盟",
                @"腾讯游戏\英雄联盟",
                @"Games\League of Legends",
                @"LOL",
            };
            List<string> all = new List<string>();
            foreach (string d in drives) foreach (string r in rels) all.Add(Path.Combine(d + "\\", r));
            return all.ToArray();
        }

        public static string FindLolPath(string exePath)
        {
            foreach (string c in FromProcess(exePath)) { string hit = NormalizeLolRoot(c); if (hit != null) return hit; }
            foreach (string c in FromRegistry()) { string hit = NormalizeLolRoot(c); if (hit != null) return hit; }
            foreach (string c in CommonPaths()) { string hit = NormalizeLolRoot(c); if (hit != null) return hit; }
            return null;
        }

        public static List<Dictionary<string, object>> DetectChain(string exePath)
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, IEnumerable<string>> scan = delegate (string src, IEnumerable<string> list)
            {
                foreach (string p in list)
                {
                    bool ok = NormalizeLolRoot(p) != null;
                    rows.Add(new Dictionary<string, object> { { "source", src }, { "path", p }, { "ok", ok } });
                    if (ok) return;
                    if (rows.Count > 60) return;
                }
            };
            scan("进程路径", FromProcess(exePath));
            scan("注册表", FromRegistry());
            scan("常见路径", CommonPaths());
            return rows;
        }
    }
}
