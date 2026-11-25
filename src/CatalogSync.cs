using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ManikinApp
{
    // CatalogSync: compares the public AQ Closet catalogue (aqcloset.com, no key,
    // 300 calls/min) against what we already have and adds whatever we are
    // missing. Two steps so nothing writes until the user says so:
    //   Plan()  - read the API, diff by SWF path, remember the missing items
    //   Apply() - write them to catalog.json.gz on GitHub (one commit, plus one
    //             for its meta) when a token is configured, else to the Supabase
    //             `swfs` table
    // The nightly snapshot unions the two stores, so neither can lose rows the
    // other added.
    static class CatalogSync
    {
        const string Api = "https://aqcloset.com";
        const int PageSize = 200;      // /api/items/latest caps limit at 200
        const int BatchSize = 200;     // rows per PostgREST insert
        const int RequestGapMs = 210;  // 300 calls/min is the API's limit: ~285/min
        const int Workers = 4;         // requests in flight while the gap paces them

        sealed class Remote
        {
            public string Name, File, Slot, Wtype, Link;
        }

        sealed class PageResult
        {
            public int Total;
            public List<Remote> Items = new List<Remote>();
        }

        static readonly object _gate = new object();
        static List<Remote> _plan = new List<Remote>();
        static readonly HttpClient Http = CreateClient();

        // Read by the page while a job runs, so it can show progress.
        static volatile string _phase = "";
        static volatile int _done, _total, _missing;

        // Last items added, newest last. The Monitor panel renders these, so a
        // scan does not have to be watched in the Log tab.
        static readonly Queue<string> _recent = new Queue<string>();

        static void Note(string line)
        {
            lock (_recent)
            {
                _recent.Enqueue(line);
                while (_recent.Count > 300) _recent.Dequeue();
            }
        }

        // One request may start every RequestGapMs, no matter how long each
        // takes, so Workers requests overlap the network wait instead of queuing
        // behind it. A 341-page walk finishes in about 70 s this way; issuing
        // them one at a time cost roughly three minutes.
        static long _nextSlot;
        static readonly object _rateGate = new object();

        static void WaitSlot()
        {
            long wait;
            lock (_rateGate)
            {
                long now = Environment.TickCount;
                if (_nextSlot < now) _nextSlot = now;
                wait = _nextSlot - now;
                _nextSlot = now + wait + RequestGapMs;
            }
            if (wait > 0) Thread.Sleep((int)wait);
        }

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.Add("Accept", "application/json");
            c.DefaultRequestHeaders.Add("User-Agent", "Manikin/1.1 catalog-sync");
            return c;
        }

        public static string Progress()
        {
            string[] recent;
            lock (_recent) recent = _recent.ToArray();
            return JsonConvert.SerializeObject(new { phase = _phase, done = _done, total = _total, missing = _missing, recent });
        }

        static string Fail(string message)
        {
            _phase = "";
            return JsonConvert.SerializeObject(new { ok = false, error = message });
        }

        // Walk the whole catalogue, keep what our table does not have.
        public static string Plan()
        {
            try
            {
                _phase = "Loading our catalog"; _done = 0; _total = 0; _missing = 0;
                lock (_recent) _recent.Clear();
                // Paths only: the diff never looks at names, so there is no
                // reason to pull the whole catalog to compare against.
                var have = Wardrobe.AllPaths();
                if (have.Count == 0) return Fail("Our catalog did not load, so there is nothing to compare against.");

                _phase = "Reading aqcloset.com";
                PageResult first = FetchPage(0);
                int total = first.Total > 0 ? first.Total : first.Items.Count;
                _total = total;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var missing = new List<Remote>();
                var counters = new int[3];        // repeats, already held, junk
                var planGate = new object();

                Action<PageResult> take = page =>
                {
                    int rep = 0, hld = 0, jnk = 0;
                    var mine = new List<Remote>(page.Items.Count);
                    foreach (Remote it in page.Items)
                    {
                        if (string.IsNullOrEmpty(it.Name) || string.IsNullOrEmpty(it.File)) continue;
                        // Not a real file name ("...swf/t", a trailing tab): the
                        // wardrobe drops these rows anyway, so writing them only
                        // litters the table and poisons later batches.
                        if (!it.File.EndsWith(".swf", StringComparison.OrdinalIgnoreCase)) { jnk++; continue; }
                        if (have.Contains(it.File)) { hld++; continue; }    // already in our table
                        if (!seen.Add(it.File)) { rep++; continue; }        // same art listed twice
                        mine.Add(it);
                    }
                    lock (planGate)
                    {
                        counters[0] += rep; counters[1] += hld; counters[2] += jnk;
                        missing.AddRange(mine);
                        _done = Math.Min(_done + page.Items.Count, total);
                        _missing = missing.Count;
                    }
                };
                take(first);

                // Pages 1..n run on Workers tasks, each one waiting its turn for
                // a request slot rather than sleeping after a whole round trip.
                var offsets = new List<int>();
                for (int off = PageSize; off < total; off += PageSize) offsets.Add(off);
                int nextPage = 0;
                var jobs = new Task[Workers];
                for (int w = 0; w < Workers; w++)
                {
                    jobs[w] = Task.Run(() =>
                    {
                        while (true)
                        {
                            int i = Interlocked.Increment(ref nextPage) - 1;
                            if (i >= offsets.Count) break;
                            WaitSlot();
                            PageResult page;
                            try { page = FetchPage(offsets[i]); }
                            catch (Exception ex)
                            {
                                AppForm.Dbg("catalog scan page " + offsets[i] + " failed: " + ex.Message);
                                continue;
                            }
                            take(page);
                        }
                    });
                }
                Task.WaitAll(jobs);

                int repeat = counters[0], held = counters[1], junk = counters[2];

                lock (_gate) _plan = missing;
                _phase = "Scan complete";
                // Lower-case keys: the page reads name/file/slot, matching the API.
                var sample = missing.Take(300).Select(m => new JObject
                {
                    { "name", m.Name }, { "file", m.File }, { "slot", m.Slot }
                }).ToArray();
                return JsonConvert.SerializeObject(new
                {
                    ok = true,
                    remote = total,
                    ours = have.Count,
                    missing = missing.Count,
                    alreadyHave = held,
                    repeats = repeat,
                    junk = junk,
                    target = Target(),
                    sample
                });
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        // Insert the items Plan() found. Idempotent enough: a second scan after
        // this sees the new rows and plans nothing.
        public static string Apply()
        {
            List<Remote> plan;
            lock (_gate) plan = new List<Remote>(_plan);
            if (plan.Count == 0) return Fail("Nothing planned. Run the scan first.");
            // GitHub needs no Supabase credentials; only the table fallback does.
            if (!GitHub.Writable && (string.IsNullOrEmpty(Wardrobe.SupabaseUrl) ||
                                     string.IsNullOrEmpty(Wardrobe.SupabaseKey)))
                return Fail("No GitHub token and no Supabase credentials, so items cannot be added.");

            // Re-check against a freshly loaded catalog: rows may have arrived
            // since the scan (a second click, another machine), and the plan
            // itself can hold the same path twice.
            _phase = "Checking for duplicates"; _done = 0; _total = plan.Count;
            int plannedBefore = plan.Count;
            try
            {
                HashSet<string> have = Wardrobe.AllPaths();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var fresh = new List<Remote>(plan.Count);
                foreach (Remote r in plan)
                {
                    if (have.Contains(r.File)) continue;   // already in the table
                    if (!seen.Add(r.File)) continue;       // listed twice in the plan
                    fresh.Add(r);
                }
                plan = fresh;
            }
            catch (Exception ex) { AppForm.Dbg("catalog sync dedupe failed: " + ex.Message); }
            int skipped = plannedBefore - plan.Count;
            if (plan.Count == 0)
            {
                lock (_gate) _plan.Clear();
                _phase = "Done";
                return JsonConvert.SerializeObject(new { ok = true, target = Target(), planned = 0, added = 0, failed = 0, skipped, error = "" });
            }

            // One commit for the whole plan: rewriting the 2 MB mirror once
            // beats thousands of row inserts, and the app reads it straight back.
            if (GitHub.Writable) return ApplyToGitHub(plan, skipped);

            _phase = "Adding items"; _done = 0; _total = plan.Count;
            int added = 0, failed = 0, already = 0;
            string lastError = "";
            AppForm.Dbg("catalog sync: adding " + plan.Count + " items (" + skipped + " duplicates skipped)");
            try
            {
                for (int i = 0; i < plan.Count; i += BatchSize)
                {
                    List<Remote> slice = plan.GetRange(i, Math.Min(BatchSize, plan.Count - i));
                    var failedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string error;
                    HashSet<string> landed = Post(slice, out error);
                    if (landed == null)
                    {
                        // Server refused the batch: retry it a row at a time so
                        // one duplicate cannot take the other rows with it.
                        AppForm.Dbg("catalog sync: batch refused (" + error + "), retrying one row at a time");
                        landed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (Remote r in slice)
                        {
                            string one;
                            if (Post(new List<Remote> { r }, out one) != null) landed.Add(r.File);
                            else if (!IsDuplicate(one)) { failedPaths.Add(r.File); lastError = one; }
                        }
                    }

                    // One line per item, in the Log tab's monitor.
                    var lines = new List<string>(slice.Count);
                    foreach (Remote r in slice)
                    {
                        if (landed.Contains(r.File))
                        {
                            added++;
                            lines.Add("catalog sync: added " + r.Name + " (" + r.File + ")");
                            Note("+ " + r.Name + "  (" + r.File + ")");
                        }
                        else if (failedPaths.Contains(r.File)) { failed++; }
                        else
                        {
                            // Refused because the row is already in the table:
                            // the diff missed it, which is what used to 409 the
                            // batch and roll every good row back with it.
                            already++;
                            lines.Add("catalog sync: already there " + r.Name + " (" + r.File + ")");
                        }
                    }
                    AppForm.DbgMany(lines);
                    _done = Math.Min(i + BatchSize, plan.Count);
                    AppForm.Dbg("catalog sync: " + _done + "/" + plan.Count + " processed");
                }

                AppForm.Dbg("catalog sync: done - added " + added + ", failed " + failed +
                            ", already there " + already + ", skipped " + skipped);
                _phase = "Done";
                lock (_gate) _plan.Clear();   // a second click re-scans instead of re-inserting
                Wardrobe.Reload();            // pick the new rows up on the next query
                return JsonConvert.SerializeObject(new
                {
                    ok = failed == 0,
                    planned = plan.Count,
                    added,
                    failed,
                    already,
                    skipped,
                    error = lastError
                });
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        // Where Apply() writes: GitHub when a token is configured, else the old
        // Supabase table.
        static string Target() { return GitHub.Writable ? "github" : "supabase"; }

        // Append the plan to the GitHub mirror and commit it once. Rows already
        // in the file are skipped, and the file is rewritten sorted by path so a
        // diff shows only the new items.
        static string ApplyToGitHub(List<Remote> plan, int skipped)
        {
            _phase = "Reading the GitHub catalog"; _done = 0; _total = plan.Count;
            string sha, error;
            byte[] current = GitHub.GetFile("catalog.json.gz", out sha, out error);
            if (current == null) return Fail("GitHub catalog unreadable: " + error);

            JArray rows;
            try
            {
                using (var gz = new GZipStream(new MemoryStream(current), CompressionMode.Decompress))
                using (var reader = new StreamReader(gz, Encoding.UTF8))
                    rows = JArray.Parse(reader.ReadToEnd());
            }
            catch (Exception ex) { return Fail("GitHub catalog unreadable: " + ex.Message); }

            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken t in rows)
            {
                string p = (string)t["path"];
                if (string.IsNullOrEmpty(p)) continue;
                have.Add(p);
            }

            int added = 0, already = 0;
            foreach (Remote r in plan)
            {
                if (r == null || string.IsNullOrEmpty(r.File)) continue;
                if (!have.Add(r.File)) { already++; continue; }
                rows.Add(new JObject
                {
                    { "name", r.Name },
                    { "path", r.File },
                    { "category", r.Slot },
                    { "item_group", r.Wtype ?? "" },
                    { "link", r.Link ?? "" }
                });
                added++;
                _done = added;
                if (added % 250 == 0) AppForm.Dbg("catalog sync: " + added + " new items staged");
            }

            if (added == 0)
            {
                _phase = "Done";
                lock (_gate) _plan.Clear();
                return JsonConvert.SerializeObject(new
                {
                    ok = true, target = "github", planned = plan.Count,
                    added = 0, failed = 0, already, skipped, error = ""
                });
            }

            var sorted = new JArray();
            foreach (JToken t in rows.OrderBy(r => (string)r["path"] ?? "", StringComparer.Ordinal))
                sorted.Add(t);

            byte[] raw = Encoding.UTF8.GetBytes(sorted.ToString(Formatting.None));
            byte[] gzBytes;
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(raw, 0, raw.Length);
                gzBytes = ms.ToArray();
            }

            _phase = "Writing the GitHub catalog";
            string message = "catalog: add " + added + " items";
            if (!GitHub.PutFile("catalog.json.gz", gzBytes, message, sha, out error))
                return Fail("GitHub write failed: " + error);

            WriteMeta(sorted, raw);

            _phase = "Done";
            lock (_gate) _plan.Clear();
            Wardrobe.Reload();
            AppForm.Dbg("catalog sync: added " + added + " items to GitHub (" + already + " already there)");
            return JsonConvert.SerializeObject(new
            {
                ok = true, target = "github", planned = plan.Count,
                added, failed = 0, already, skipped, error = ""
            });
        }

        // catalog.meta.json is the mirror's own report: row count, uncompressed
        // size and sha256 of the catalog it sits beside. The nightly snapshot
        // writes it too, but until then an app-side add would leave a stale
        // count behind, so refresh it here. A failure is logged, not fatal: the
        // catalog itself is already committed.
        static void WriteMeta(JArray rows, byte[] raw)
        {
            try
            {
                string sha, error;
                byte[] current = GitHub.GetFile("catalog.meta.json", out sha, out error);
                string source = "public.swfs";
                if (current != null)
                {
                    try
                    {
                        JObject old = JObject.Parse(Encoding.UTF8.GetString(current));
                        if (old["source"] != null && ((string)old["source"]).Length > 0)
                            source = (string)old["source"];
                    }
                    catch { }
                }

                string hash;
                using (SHA256 sha256 = SHA256.Create())
                    hash = BitConverter.ToString(sha256.ComputeHash(raw)).Replace("-", "").ToLowerInvariant();

                // Key order matches snapshot.py (indent 2, sorted keys, LF) so the
                // two writers produce the same bytes for the same catalog.
                var meta = new JObject
                {
                    { "bytes", raw.Length },
                    { "count", rows.Count },
                    { "sha256", hash },
                    { "source", source }
                };
                string text = meta.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n";
                if (!GitHub.PutFile("catalog.meta.json", Encoding.UTF8.GetBytes(text),
                        "catalog: meta for " + rows.Count + " items", sha, out error))
                    AppForm.Dbg("catalog sync: meta not updated (" + error + ") - the nightly snapshot will rewrite it");
            }
            catch (Exception ex) { AppForm.Dbg("catalog sync: meta not updated (" + ex.Message + ")"); }
        }

        // Insert one batch, tolerating paths that are already in the table.
        // The old plain insert answered 409 / 23505 ("duplicate key value
        // violates unique constraint swfs_pkey") and PostgREST rolls the whole
        // statement back, so a single stale row lost every good row beside it.
        // resolution=ignore-duplicates makes that ON CONFLICT (path) DO NOTHING,
        // and return=representation reports exactly which rows landed.
        // Returns null (with error) only if the server refused outright.
        static HashSet<string> Post(List<Remote> slice, out string error)
        {
            error = "";
            var rows = new JArray();
            foreach (Remote r in slice)
                rows.Add(new JObject
                {
                    { "name", r.Name },
                    { "path", r.File },
                    { "category", r.Slot },
                    { "item_group", r.Wtype ?? "" },
                    { "link", r.Link ?? "" }
                });

            string url = Wardrobe.SupabaseUrl + "?apikey=" + Uri.EscapeDataString(Wardrobe.SupabaseKey) + "&on_conflict=path";
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Content = new StringContent(rows.ToString(Formatting.None), Encoding.UTF8, "application/json");
                req.Headers.Add("apikey", Wardrobe.SupabaseKey);
                req.Headers.Add("Authorization", "Bearer " + Wardrobe.SupabaseKey);
                req.Headers.Add("Prefer", "return=representation,resolution=ignore-duplicates");
                var resp = Http.SendAsync(req).GetAwaiter().GetResult();
                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    if (body != null && body.Length > 200) body = body.Substring(0, 200);
                    error = (int)resp.StatusCode + " " + body;
                    return null;
                }
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                JArray back = null;
                try { back = JArray.Parse(body); } catch { }
                if (back == null) return set;
                foreach (JToken t in back)
                {
                    string p = (string)t["path"];
                    if (!string.IsNullOrEmpty(p)) set.Add(p);
                }
                return set;
            }
        }

        static bool IsDuplicate(string error)
        {
            return error != null && (error.Contains("23505") || error.Contains("duplicate key"));
        }

        static PageResult FetchPage(int offset)
        {
            string url = Api + "/api/items/latest?limit=" + PageSize + "&offset=" + offset;
            for (int attempt = 0; ; attempt++)
            {
                var resp = Http.GetAsync(url).GetAwaiter().GetResult();
                if ((int)resp.StatusCode == 429 && attempt < 2)
                {
                    // Public API: 300 calls a minute. Wait it out and try again.
                    Thread.Sleep(2000);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var root = JObject.Parse(body);
                var page = new PageResult();
                JToken tot = root["total"];
                if (tot != null && tot.Type != JTokenType.Null) page.Total = (int)tot;
                var results = root["results"] as JArray;
                if (results != null)
                {
                    foreach (JToken t in results)
                    {
                        page.Items.Add(new Remote
                        {
                            Name = ((string)t["name"] ?? "").Trim(),
                            File = Normalize((string)t["file"]),
                            Slot = ((string)t["slot"] ?? "").Trim().ToLowerInvariant(),
                            Wtype = (string)t["wtype"] ?? "",
                            Link = (string)t["link"] ?? ""
                        });
                    }
                }
                return page;
            }
        }

        // Paths are the join key: both sides store items/helms/X.swf style paths.
        // aqcloset tags some files "…swf?2", and '?' cannot appear in a real file
        // name, so the tag is dropped: the clean path is the one we can download
        // and the one the table should hold.
        static string Normalize(string file)
        {
            if (file == null) return "";
            string s = file.Trim().Replace('\\', '/').TrimStart('/');
            int q = s.IndexOf('?');
            if (q >= 0) s = s.Substring(0, q);
            return s.ToLowerInvariant();
        }
    }
}
