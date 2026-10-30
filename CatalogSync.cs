using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ManikinApp
{
    // CatalogSync: compares the public AQ Closet catalogue (aqcloset.com, no key,
    // 300 calls/min) against our own Supabase `swfs` table and adds whatever we
    // are missing. Two steps so nothing writes until the user says so:
    //   Plan()  - read the API, diff by SWF path, remember the missing items
    //   Apply() - insert them in batches with the publishable key
    static class CatalogSync
    {
        const string Api = "https://aqcloset.com";
        const int PageSize = 200;      // /api/items/latest caps limit at 200
        const int BatchSize = 200;     // rows per PostgREST insert

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
                Wardrobe.Preload();
                var have = Wardrobe.ExistingFiles();
                if (have.Count == 0) return Fail("Our catalog did not load, so there is nothing to compare against.");

                _phase = "Reading aqcloset.com";
                PageResult first = FetchPage(0);
                int total = first.Total > 0 ? first.Total : first.Items.Count;
                _total = total;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var missing = new List<Remote>();
                int repeat = 0, held = 0;
                for (int offset = 0; offset < total; offset += PageSize)
                {
                    PageResult page = offset == 0 ? first : FetchPage(offset);
                    if (page.Items.Count == 0) break;
                    foreach (Remote it in page.Items)
                    {
                        if (string.IsNullOrEmpty(it.Name) || string.IsNullOrEmpty(it.File)) continue;
                        if (!seen.Add(it.File)) { repeat++; continue; }     // same art listed twice
                        if (have.Contains(it.File)) { held++; continue; }   // already in our table
                        missing.Add(it);
                    }
                    _done = Math.Min(offset + PageSize, total);
                    _missing = missing.Count;
                    Thread.Sleep(210);                             // stay under 300 calls/min
                }

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
            if (string.IsNullOrEmpty(Wardrobe.SupabaseUrl) || string.IsNullOrEmpty(Wardrobe.SupabaseKey))
                return Fail("No Supabase credentials, so items cannot be added.");

            // Re-check against a freshly loaded catalog: rows may have arrived
            // since the scan (a second click, another machine), and the plan
            // itself can hold the same path twice.
            _phase = "Checking for duplicates"; _done = 0; _total = plan.Count;
            int plannedBefore = plan.Count;
            try
            {
                Wardrobe.Reload();
                HashSet<string> have = Wardrobe.ExistingFiles();
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
                return JsonConvert.SerializeObject(new { ok = true, planned = 0, added = 0, failed = 0, skipped, error = "" });
            }

            _phase = "Adding items"; _done = 0; _total = plan.Count;
            int added = 0, failed = 0;
            string lastError = "";
            AppForm.Dbg("catalog sync: adding " + plan.Count + " items (" + skipped + " duplicates skipped)");
            try
            {
                for (int i = 0; i < plan.Count; i += BatchSize)
                {
                    List<Remote> slice = plan.GetRange(i, Math.Min(BatchSize, plan.Count - i));
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

                    string url = Wardrobe.SupabaseUrl + "?apikey=" + Uri.EscapeDataString(Wardrobe.SupabaseKey);
                    using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        req.Content = new StringContent(rows.ToString(Formatting.None), Encoding.UTF8, "application/json");
                        req.Headers.Add("apikey", Wardrobe.SupabaseKey);
                        req.Headers.Add("Authorization", "Bearer " + Wardrobe.SupabaseKey);
                        req.Headers.Add("Prefer", "return=minimal");
                        var resp = Http.SendAsync(req).GetAwaiter().GetResult();
                        if (resp.IsSuccessStatusCode)
                        {
                            added += slice.Count;
                            // One line per item, in the Log tab's monitor.
                            var lines = new List<string>(slice.Count);
                            foreach (Remote r in slice)
                            {
                                lines.Add("catalog sync: added " + r.Name + " (" + r.File + ")");
                                Note("+ " + r.Name + "  (" + r.File + ")");
                            }
                            AppForm.DbgMany(lines);
                        }
                        else
                        {
                            failed += slice.Count;
                            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            if (body != null && body.Length > 200) body = body.Substring(0, 200);
                            lastError = (int)resp.StatusCode + " " + body;
                            AppForm.Dbg("catalog sync batch failed: " + lastError);
                        }
                    }
                    _done = Math.Min(i + BatchSize, plan.Count);
                    AppForm.Dbg("catalog sync: " + _done + "/" + plan.Count + " processed");
                }

                AppForm.Dbg("catalog sync: done - added " + added + ", failed " + failed + ", skipped " + skipped);
                _phase = "Done";
                lock (_gate) _plan.Clear();   // a second click re-scans instead of re-inserting
                Wardrobe.Reload();            // pick the new rows up on the next query
                return JsonConvert.SerializeObject(new
                {
                    ok = failed == 0,
                    planned = plan.Count,
                    added,
                    failed,
                    skipped,
                    error = lastError
                });
            }
            catch (Exception ex) { return Fail(ex.Message); }
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
        static string Normalize(string file)
        {
            if (file == null) return "";
            return file.Trim().Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        }
    }
}
