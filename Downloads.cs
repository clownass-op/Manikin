using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ManikinApp
{
    // Downloads: character-page fetch, FlashVars parsing, and item SWF downloads
    // (port of the Electron main.js network logic).
    class Downloads
    {
        const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Manikin/1.0";
        internal const string GAME = "https://game.aq.com/game/gamefiles/";

        static readonly string[] INVALID = { "none", "undefined" };
        static bool Valid(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string s = name.ToLowerInvariant();
            foreach (var b in INVALID) if (s == b) return false;
            return true;
        }
        // Local cache path for a game file: the CDN-relative path, kept as
        // folders (hair\F\Bob.swf, classes\M\x.swf). Caching by bare file
        // name made same-named files collide - most visibly the male and
        // female versions of a hair or class armor, so the next character
        // silently reused the wrong gender's art. ".." / rooted segments are
        // dropped so a CharPage value can never escape the output folder.
        public static string LocalPathOf(string relUrl)
        {
            var keep = new List<string>();
            foreach (var raw in (relUrl ?? "").Split('/', '\\'))
            {
                string p = raw.Split('?', '#')[0].Trim();
                if (p.Length == 0 || p == "." || p == "..") continue;
                foreach (char c in Path.GetInvalidFileNameChars()) p = p.Replace(c, '_');
                keep.Add(p);
            }
            return string.Join("\\", keep);
        }

        // Fetch a character page (follows redirects), returns raw HTML body.
        public static async Task<string> GetAsync(string url)
        {
            for (int hop = 0; hop < 8; hop++)
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = UA;
                req.Timeout = 60000;
                req.AllowAutoRedirect = false;
                try
                {
                    using (var resp = (HttpWebResponse)await req.GetResponseAsync())
                    {
                        if ((int)resp.StatusCode >= 300 && (int)resp.StatusCode < 400 && resp.Headers["Location"] != null)
                        {
                            url = resp.Headers["Location"];
                            resp.Close();
                            continue;
                        }
                        if ((int)resp.StatusCode != 200)
                        {
                            resp.Close();
                            return null;
                        }
                        using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                            return await sr.ReadToEndAsync();
                    }
                }
                catch { return null; }
            }
            return null;
        }

        // Download a binary file (follows redirects).
        public static async Task<bool> DownloadAsync(string url, string dest)
        {
            for (int hop = 0; hop < 8; hop++)
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = UA;
                req.Timeout = 60000;
                req.AllowAutoRedirect = false;
                try
                {
                    using (var resp = (HttpWebResponse)await req.GetResponseAsync())
                    {
                        if ((int)resp.StatusCode >= 300 && (int)resp.StatusCode < 400 && resp.Headers["Location"] != null)
                        {
                            url = resp.Headers["Location"];
                            resp.Close();
                            continue;
                        }
                        if ((int)resp.StatusCode != 200)
                        {
                            resp.Close();
                            return false;
                        }
                        // Write to a side file and move it into place: an
                        // interrupted download must not leave a truncated
                        // SWF that the cache would then reuse forever.
                        string part = dest + ".part";
                        string folder = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                        using (var src = resp.GetResponseStream())
                        using (var dst = File.Create(part))
                        {
                            await src.CopyToAsync(dst);
                        }
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(part, dest);
                        return true;
                    }
                }
                catch
                {
                    try { if (File.Exists(dest + ".part")) File.Delete(dest + ".part"); } catch { }
                    return false;
                }
            }
            return false;
        }

        // Parse modern character-page flashvars.
        public static Dictionary<string, string> ParseFlashVars(string html)
        {
            if (html == null) return null;
            var m = Regex.Match(html, "flashvars=\"([^\"]*)\"");
            if (!m.Success) return null;
            string s = m.Groups[1].Value
                .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">");
            if (s.StartsWith("?")) s = s.Substring(1);
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in s.Split('&'))
            {
                if (kv.Length == 0) continue;
                int i = kv.IndexOf('=');
                string k, v;
                if (i == -1) { k = kv; v = ""; }
                else { k = kv.Substring(0, i); v = kv.Substring(i + 1); }
                try { k = Uri.UnescapeDataString(k); } catch { }
                try { v = Uri.UnescapeDataString(v); } catch { }
                if (k.Length > 0 && !vars.ContainsKey(k)) vars[k] = v;
            }
            return vars;
        }

        public class ItemDownload
        {
            public string Type;
            public string Url;
            public string File;   // cache path relative to the output folder
            public string Link;   // CharPage linkage (strXLink), "" = parse the SWF
            public bool Cosmetic;
        }

        static string Var(Dictionary<string, string> vars, string key)
        {
            string v;
            return vars.TryGetValue(key, out v) && Valid(v) ? v : "";
        }

        public static List<ItemDownload> BuildDownloads(Dictionary<string, string> vars)
        {
            var outList = new List<ItemDownload>();
            string gender = Var(vars, "strGender");
            Add(outList, vars, "Hair", "strHairName", "strHairFile", "strHairName", false);
            Add(outList, vars, "Helm", "strHelmName", "strHelmFile", "strHelmLink", false);
            Add(outList, vars, "Cape", "strCapeName", "strCapeFile", "strCapeLink", false);
            AddArmor(outList, vars, gender, "strArmorName", "strClassFile", "strClassLink", false);
            Add(outList, vars, "Weapon", "strWeaponName", "strWeaponFile", "strWeaponLink", false);
            Add(outList, vars, "Pet", "strPetName", "strPetFile", "strPetLink", false);
            Add(outList, vars, "Misc", "strMiscName", "strMiscFile", "strMiscLink", false);
            // Cosmetic (custom) outfit: same slots, shown by the CharPage
            // "Cosmetics" toggle. Armor follows the same classes/{gender}/ rule.
            Add(outList, vars, "Helm", "strCustHelmName", "strCustHelmFile", "strCustHelmLink", true);
            Add(outList, vars, "Cape", "strCustCapeName", "strCustCapeFile", "strCustCapeLink", true);
            Add(outList, vars, "Weapon", "strCustWeaponName", "strCustWeaponFile", "strCustWeaponLink", true);
            AddArmor(outList, vars, gender, "strCustArmorName", "strCustArmorFile", "strCustArmorLink", true);
            return outList;
        }

        static void AddArmor(List<ItemDownload> list, Dictionary<string, string> vars, string gender, string nameKey, string fileKey, string linkKey, bool cosmetic)
        {
            string name = Var(vars, nameKey), file = Var(vars, fileKey);
            if (name.Length == 0 || file.Length == 0) return;
            string rel = "classes/" + gender + "/" + file;
            list.Add(new ItemDownload { Type = "Armor", Url = GAME + rel, File = LocalPathOf(rel), Link = Var(vars, linkKey), Cosmetic = cosmetic });
        }

        static void Add(List<ItemDownload> list, Dictionary<string, string> vars, string type, string nameKey, string fileKey, string linkKey, bool cosmetic)
        {
            string name = Var(vars, nameKey), file = Var(vars, fileKey);
            if (name.Length == 0 || file.Length == 0) return;
            list.Add(new ItemDownload { Type = type, Url = GAME + file, File = LocalPathOf(file), Link = Var(vars, linkKey), Cosmetic = cosmetic });
        }

        // Settings > Download SWFs. When off, fetched items are cached in a
        // temp folder instead of the configured one, so the swf folder is left
        // untouched (nothing is saved next to the exe).
        public static bool SaveToFolder = true;

        // The folder a fetched item is actually written to.
        public static string EffectiveDir(string dir)
        {
            if (SaveToFolder && !string.IsNullOrEmpty(dir)) return dir;
            return Path.Combine(Path.GetTempPath(), "Manikin", "swf");
        }

        // Cached copy if present, else download. Returns the local path or
        // null. (Both the host-object and web-message paths use this.)
        public static async Task<string> EnsureLocalAsync(ItemDownload it, string dir)
        {
            if (string.IsNullOrEmpty(it.File)) return null;
            string dest = Path.Combine(dir, it.File);
            bool ok;
            try { ok = System.IO.File.Exists(dest) && new FileInfo(dest).Length > 0; } catch { ok = false; }
            if (!ok) ok = await DownloadAsync(it.Url, dest).ConfigureAwait(false);
            return ok ? dest : null;
        }
    }
}
