using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ManikinApp
{
    // Wardrobe: the catalog behind the Wardrobe panel.
    // Now queries Supabase DB (zcuywyfxqvrmrmeivmcn) instead of local wardrobe.json.
    // Falls back to JSON if Supabase is unavailable.
    static class Wardrobe
    {
        // Supabase configuration. Neither value is in the repo: they come from
        // the environment or an ignored supabase.key file (see Config below).
        internal static readonly string SupabaseUrl;
        internal static readonly string SupabaseKey;

        // HTTP client for Supabase API calls; null when no credentials exist.
        static readonly HttpClient SupabaseClient;

        static Wardrobe()
        {
            string url, key;
            Config.Read(out url, out key);
            SupabaseUrl = url;
            SupabaseKey = key;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            {
                AppForm.Dbg("wardrobe: no Supabase credentials; using the local JSON catalog");
                return;
            }
            SupabaseClient = new HttpClient { BaseAddress = new Uri(url) };
            // apikey header is required by the Supabase REST API.
            SupabaseClient.DefaultRequestHeaders.Add("apikey", key);
            // Bearer token for additional auth.
            SupabaseClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");
            SupabaseClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        // Credentials source: MANIKIN_SUPABASE_URL / MANIKIN_SUPABASE_KEY win,
        // then an optional supabase.key next to the exe or one folder up, and
        // finally the catalog compiled in below - so a clone needs no key file.
        static class Config
        {
            // The catalog compiled into the app, so no key file is needed.
            // This is a Supabase *publishable* key: only safe to publish while
            // the swfs table is SELECT-only for the anon role (see README).
            const string DefaultUrl = "https://zcuywyfxqvrmrmeivmcn.supabase.co/rest/v1/swfs";
            const string DefaultKey = "sb_publishable_LaucQb2EgiRwu0q9_j8G9A_4abfiHXY";

            public static void Read(out string url, out string key)
            {
                url = Env("MANIKIN_SUPABASE_URL");
                key = Env("MANIKIN_SUPABASE_KEY");
                string exe = AppDomain.CurrentDomain.BaseDirectory;
                string root = null;
                try { root = Path.GetDirectoryName(exe.TrimEnd(Path.DirectorySeparatorChar)); } catch { }
                foreach (var dir in new[] { exe, root })
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    string file = Path.Combine(dir, "supabase.key");
                    try
                    {
                        if (!File.Exists(file)) continue;
                        foreach (var raw in File.ReadAllLines(file))
                        {
                            string line = (raw ?? "").Trim();
                            if (line.Length == 0 || line[0] == '#') continue;
                            int eq = line.IndexOf('=');
                            if (eq <= 0) continue;
                            string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                            string v = line.Substring(eq + 1).Trim().Trim('"');
                            if (k == "url" && string.IsNullOrEmpty(url)) url = v;
                            else if (k == "key" && string.IsNullOrEmpty(key)) key = v;
                        }
                    }
                    catch { }
                    if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(key)) return;
                }
                // Nothing overrode it: fall back to the built-in catalog.
                if (string.IsNullOrEmpty(url)) url = DefaultUrl;
                if (string.IsNullOrEmpty(key)) key = DefaultKey;
            }

            static string Env(string name)
            {
                try { return Environment.GetEnvironmentVariable(name); } catch { return null; }
            }
        }

        public class Item
        {
            public string Slot, File, Name, Link, WeaponType, Rarity, Where;
            public string[] Alts;
            public bool Member, Ac;
            internal string Haystack;   // lower-case name + alts + file, for search
        }

        // Panel slot (ItemType name) <-> catalog slot.
        static readonly Dictionary<string, string> CatalogSlot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Helm", "helm" }, { "Armor", "armor" }, { "Cape", "cape" },
            { "Weapon", "weapon" }, { "Pet", "pet" }, { "Misc", "ground" }
        };

        // Cached items from Supabase; loaded lazily
        static Task<List<Item>> _load;

        public static void Preload()
        {
            if (_load == null) _load = Task.Run(() => LoadFromSupabase());
        }

        // Load items from Supabase database
        // Load the whole swfs table from Supabase, exactly like the Skua
        // cosmetic plugin does: one request for the row count (Prefer:
        // count=exact), then the remaining pages fetched CONCURRENTLY
        // (batches of 8). 50k+ rows spread across ~51 pages finish in a
        // couple of seconds instead of a minute of sequential requests.
        static async Task<List<Item>> LoadFromSupabase()
        {
            var list = new List<Item>();
            try
            {
                if (SupabaseClient == null) return LoadFromJsonFallback();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                const int pageSize = 1000;
                const int maxParallel = 8;

                string first = await FetchPage(0, pageSize);
                JArray rows = ParseRows(first, 0);
                if (rows == null || rows.Count == 0)
                {
                    AppForm.Dbg("Supabase: first page empty/error, falling back to JSON");
                    return LoadFromJsonFallback();
                }
                foreach (JToken r in rows)
                {
                    try { AddRow(list, r); }
                    catch { }
                }

                // Total count: PostgREST answers with "Content-Range: 0-999/50795".
                int total = list.Count;
                int counted = lastTotal;
                if (counted > list.Count) total = counted;
                if (total > 400000) total = 400000;   // sanity cap

                var offsets = new List<long>();
                for (long off = pageSize; off < total; off += pageSize) offsets.Add(off);

                for (int start = 0; start < offsets.Count; start += maxParallel)
                {
                    var batch = new List<Task<List<Item>>>();
                    for (int i = start; i < offsets.Count && i < start + maxParallel; i++)
                    {
                        long off = offsets[i];
                        batch.Add(Task.Run(async () =>
                        {
                            var items = new List<Item>();
                            try
                            {
                                JArray page = ParseRows(await FetchPage(off, pageSize), off);
                                if (page != null)
                                    foreach (JToken r in page)
                                    {
                                        try { AddRow(items, r); }
                                        catch { }
                                    }
                            }
                            catch (Exception ex) { AppForm.Dbg("Supabase page " + off + " failed: " + ex.Message); }
                            return items;
                        }));
                    }
                    foreach (var part in await Task.WhenAll(batch)) list.AddRange(part);
                }

                if (list.Count == 0)
                {
                    AppForm.Dbg("Supabase: no rows loaded, falling back to JSON");
                    return LoadFromJsonFallback();
                }

                // Drop duplicate slot+path rows (the DB repeats some items e.g.
                // "Legion DoomKnight" as Armor and Class); rows with real meta win.
                var seen = new Dictionary<string, Item>();
                var slim = new List<Item>(list.Count);
                foreach (var it in list)
                {
                    string key = it.Slot + "\t" + it.File.ToLowerInvariant();
                    Item old;
                    if (!seen.TryGetValue(key, out old)) { seen[key] = it; slim.Add(it); }
                    else if (old.Rarity == "(no rarity)" && it.Rarity != "(no rarity)")
                    {
                        int i = slim.IndexOf(old);
                        if (i >= 0) slim[i] = it;
                        seen[key] = it;
                    }
                }
                list = slim;

                // A-Z by name, matching the old pre-sorted JSON catalog.
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                var counts = new Dictionary<string, int>();
                foreach (var it in list)
                {
                    int n; counts.TryGetValue(it.Slot, out n);
                    counts[it.Slot] = n + 1;
                }
                var parts = new List<string>();
                foreach (var kv in counts) parts.Add(kv.Key + "=" + kv.Value);
                AppForm.Dbg("wardrobe: " + list.Count + " items from Supabase in " +
                    sw.ElapsedMilliseconds + " ms (" + string.Join(" ", parts.ToArray()) + ")");
                return list;
            }
            catch (Exception ex)
            {
                AppForm.Dbg("Supabase wardrobe load error: " + ex.Message);
                return LoadFromJsonFallback();
            }
        }

        // Catalog: last Content-Range total the fetcher saw (Requests, the
        // PostgREST "="count=exact" header). Volatile: written/read across
        // UI thread and worker tasks. 0 = not fetched yet.
        static volatile int lastTotal;

        // One page body from Supabase. apikey MUST be a query param (headers
        // alone are rejected by this deployment); the first page additionally
        // asks for an exact row count.
        static async Task<string> FetchPage(long offset, int pageSize)
        {
            string url = "?select=name,path,category,item_group,link,meta" +
                         "&order=created_at.desc&limit=" + pageSize +
                         "&offset=" + offset + "&apikey=" + SupabaseKey;
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset == 0)
            {
                req.Headers.Add("Prefer", "count=exact");
                lastTotal = 0;
            }
            var resp = await SupabaseClient.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            string body = await resp.Content.ReadAsStringAsync();
            if (offset == 0)
            {
                // .NET files Content-Range under HttpContentHeaders (not
                // HttpResponseHeaders) - this ate the count for ages and left
                // the loader at 1000 rows.
                IEnumerable<string> vals;
                string cr = null;
                if (resp.Content.Headers.TryGetValues("Content-Range", out vals)) cr = vals.FirstOrDefault();
                else if (resp.Headers.TryGetValues("Content-Range", out vals)) cr = vals.FirstOrDefault();
                int slash = cr == null ? -1 : cr.LastIndexOf('/');
                int t;
                if (slash >= 0 && int.TryParse(cr.Substring(slash + 1), out t)) lastTotal = t;
                AppForm.Dbg("Supabase count: status=" + (int)resp.StatusCode + " content-range=" + (cr ?? "-") + " total=" + lastTotal);
            }
            return body;
        }

        // Page body -> row array; tolerates a wrapped { "rows": [ ... ] } and
        // logs (rather than throws on) PostgREST error objects.
        static JArray ParseRows(string body, long offset)
        {
            try
            {
                JToken tok = JToken.Parse(body);
                JArray rows = tok as JArray;
                if (rows != null) return rows;
                JObject w = tok as JObject;
                JArray inner = w != null ? w["rows"] as JArray : null;
                if (inner != null) return inner;
                string s = tok.ToString(Formatting.None);
                if (s.Length > 300) s = s.Substring(0, 300) + "...";
                AppForm.Dbg("Supabase page " + offset + ": unexpected object: " + s);
                return null;
            }
            catch
            {
                AppForm.Dbg("Supabase page " + offset + ": bad JSON (" + (body == null ? 0 : body.Length) + " chars)");
                return null;
            }
        }

        // One Supabase row -> catalog item. Same fields as the plugin's
        // SupabaseRow: name, path, category, item_group, link, meta.
        static void AddRow(List<Item> list, JToken r)
        {
            JObject row = r as JObject;
            if (row == null) return;

            string name = TrimOr((string)row["name"]);
            string path = TrimOr((string)row["path"]);
            string category = TrimOr((string)row["category"]);
            string itemGroup = TrimOr((string)row["item_group"]);
            if (name.Length == 0 || path.Length == 0) return;
            if (!path.EndsWith(".swf", StringComparison.OrdinalIgnoreCase)) return;   // junk rows like "...swf/t"

            // meta is a JSON *string* ({"rarity":"Rare","where":...,"ac":true,
            // "member":false}); some rows hold junk like "227" - parse tolerantly.
            string rarity = "(no rarity)";
            string where = "";
            bool member = false, ac = false;
            try
            {
                JToken meta = row["meta"];
                string metaText = (meta == null || meta.Type == JTokenType.Null) ? "" : TrimOr(meta.ToString());
                if (metaText.StartsWith("{"))
                {
                    JObject m;
                    try { m = JObject.Parse(metaText); }
                    catch { m = null; }
                    if (m != null)
                    {
                        JToken rv = m["rarity"];
                        if (rv != null && rv.Type != JTokenType.Null && ((string)rv).Length > 0) rarity = (string)rv;
                        JToken wv = m["where"];
                        if (wv != null && wv.Type != JTokenType.Null) where = (string)wv;
                        ac = BoolMeta(m["ac"]);
                        member = BoolMeta(m["member"]);
                    }
                }
            }
            catch { }

            // Name may carry alternates like "Name|alt1|alt2".
            string[] alts = new string[0];
            int pipe = name.IndexOf('|');
            if (pipe >= 0)
            {
                alts = name.Substring(pipe + 1).Split('|');
                name = name.Substring(0, pipe);
            }

            var it = new Item
            {
                Slot = MapSlot(category, itemGroup),
                // keep the FULL relative path ("items/helms/Starsteel.swf") so the
                // downloader hits the same folder; Fetch() adds classes/<gender> for Armor only
                File = path.Replace('\\', '/'),
                Name = name,
                Alts = alts,
                Link = TrimOr((string)row["link"]),
                WeaponType = itemGroup.Equals("Weapon", StringComparison.OrdinalIgnoreCase) ? category : "",
                Member = member, Ac = ac, Rarity = rarity, Where = where
            };
            it.Haystack = (it.Name + "\n" + string.Join("|", alts) + "\n" + it.File).ToLowerInvariant();
            list.Add(it);
        }

        // Slot names MUST be the lowercase catalog slots, which is exactly what
        // Query() compares with (case-sensitive ==), otherwise every row is hidden.
        // Mirrors the plugin's ItemCategory usage: Class counts as Armor,
        // weapon subtypes (Dagger/Sword/Staff/...) all count as Weapon.
        static string MapSlot(string category, string itemGroup)
        {
            switch ((category ?? "").Trim().ToLowerInvariant())
            {
                case "helm": return "helm";
                case "cape": case "back": return "cape";
                case "armor": case "class": return "armor";
                case "pet": return "pet";
                case "sword": case "axe": case "dagger": case "gun": case "handgun":
                case "rifle": case "bow": case "mace": case "gauntlet": case "polearm":
                case "staff": case "wand": case "whip": case "weapon": return "weapon";
                case "misc": case "ground": case "ground rune": return "ground";
            }
            // fall back to the item_group codes used in the data
            switch ((itemGroup ?? "").Trim().ToLowerInvariant())
            {
                case "weapon": return "weapon";
                case "he": return "helm";
                case "co": case "ar": return "armor";
                case "ba": return "cape";
                case "pe": return "pet";
                case "mi": return "ground";
            }
            return "ground";   // ground runes and anything unrecognized -> Misc
        }

        static bool BoolMeta(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return false;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            string s = t.ToString().Trim().ToLowerInvariant();
            return s == "true" || s == "1" || s == "yes";
        }

        static string TrimOr(string s) { return s == null ? "" : s.Trim(); }

        // Load items from local JSON (fallback) - with proper path handling
        static List<Item> LoadFromJsonFallback()
        {
            // Try multiple possible locations for the wardrobe.json
            string[] possiblePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "catalog", "wardrobe.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "catalog", "wardrobe.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wardrobe.json"),
            };

            foreach (string path in possiblePaths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        AppForm.Dbg("wardrobe: loading from " + path);
                        return LoadFromJson(path);
                    }
                }
                catch { continue; }
            }

            // No JSON found - return empty list, Query() will show error
            AppForm.Dbg("wardrobe: no JSON fallback found, returning empty list");
            return new List<Item>();
        }

        // Load items from local JSON
        static List<Item> LoadFromJson(string path)
        {
            var list = new List<Item>();
            try
            {
                var rows = (JArray)JObject.Parse(File.ReadAllText(path))["rows"];
                foreach (JArray r in rows)
                {
                    string alts = (string)r[3];
                    var it = new Item
                    {
                        Slot = (string)r[0], File = (string)r[1], Name = (string)r[2],
                        Alts = string.IsNullOrEmpty(alts) ? new string[0] : alts.Split('|'),
                        Link = (string)r[4], WeaponType = (string)r[5],
                        Member = ((int)r[6] & 1) != 0, Ac = ((int)r[6] & 2) != 0,
                        Rarity = (string)r[7], Where = (string)r[8]
                    };
                    it.Haystack = (it.Name + "\n" + alts + "\n" + it.File).ToLowerInvariant();
                    list.Add(it);
                }
                AppForm.Dbg("wardrobe: " + list.Count + " items from local JSON");
            }
            catch (Exception ex) { AppForm.Dbg("wardrobe JSON load failed (" + path + "): " + ex.Message); }
            return list;
        }

        static List<Item> Items
        {
            get { Preload(); return _load.Result; }
        }

        // Lower-case SWF paths we already hold: the diff key for CatalogSync.
        internal static HashSet<string> ExistingFiles()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var it in Items)
                    if (!string.IsNullOrEmpty(it.File)) set.Add(it.File.ToLowerInvariant());
            }
            catch (Exception ex) { AppForm.Dbg("wardrobe: ExistingFiles failed: " + ex.Message); }
            return set;
        }

        // Forget the cached catalog so the next query reloads it (after a sync).
        internal static void Reload()
        {
            _load = null;
            Preload();
        }

        // One page (offset, limit) of the rows for a slot, filtered by access
        // ("all" | "free" | "member" | "ac") and by every word of q. Rows come
        // pre-sorted A-Z; total is the full match count.
        public static string Query(string slot, string q, string access, int offset, int limit)
        {
            string cs;
            if (slot == null || !CatalogSlot.TryGetValue(slot, out cs))
                return JsonConvert.SerializeObject(new { ok = false, error = "unknown slot " + slot });
            // Never block the UI thread while Supabase is still streaming (the
            // full catalog is 50k+ rows); the page retries a "loading" answer.
            Preload();
            if (!_load.IsCompleted)
                return JsonConvert.SerializeObject(new { ok = false, loading = true, error = "Loading catalog from Supabase…" });
            if (Items.Count == 0)
                return JsonConvert.SerializeObject(new { ok = false, error = "No catalog: unable to load from Supabase or JSON." });
            var words = (q ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int total = 0, inSlot = 0;
            var outRows = new JArray();
            foreach (var it in Items)
            {
                if (it.Slot != cs) continue;
                inSlot++;
                // "Free" = buyable with gold: member-only and AC items are both
                // excluded. AC items sit on the member side of the split so
                // free + member still covers every row in the slot.
                if (access == "free" && (it.Member || it.Ac)) continue;
                if (access == "member" && !it.Member && !it.Ac) continue;
                if (access == "ac" && !it.Ac) continue;
                bool hit = true;
                foreach (var w in words) if (it.Haystack.IndexOf(w, StringComparison.Ordinal) < 0) { hit = false; break; }
                if (!hit) continue;
                if (total++ >= offset && outRows.Count < limit)
                    outRows.Add(new JObject
                    {
                        { "n", it.Name }, { "f", it.File }, { "alt", new JArray(it.Alts) }, { "link", it.Link },
                        { "w", it.WeaponType }, { "m", it.Member }, { "ac", it.Ac }, { "r", it.Rarity }, { "where", it.Where }
                    });
            }
            return JsonConvert.SerializeObject(new { ok = true, slot, inSlot, total, items = outRows });
        }

        // Download (or reuse the cached copy of) one catalog item's SWF.
        // Armor lives under classes/<gender>/ on the CDN.
        public static string Fetch(string slot, string file, string gender, string dir)
        {
            try
            {
                string rel = (file ?? "").Trim().Replace('\\', '/');
                if (string.Equals(slot, "Armor", StringComparison.OrdinalIgnoreCase) &&
                    rel.Length > 0 && !rel.StartsWith("classes/", StringComparison.OrdinalIgnoreCase))
                    rel = "classes/" + (gender == "M" ? "M" : "F") + "/" + rel;
                var it = new Downloads.ItemDownload { Type = slot, Url = Downloads.GAME + rel, File = Downloads.LocalPathOf(rel) };
                string path = Downloads.EnsureLocalAsync(it, Downloads.EffectiveDir(dir)).GetAwaiter().GetResult();
                if (path == null) return JsonConvert.SerializeObject(new { ok = false, error = "Download failed: " + rel });
                return JsonConvert.SerializeObject(new { ok = true, path, file = it.File });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { ok = false, error = ex.Message }); }
        }
    }
}