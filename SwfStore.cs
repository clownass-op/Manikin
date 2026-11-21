using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ManikinApp
{
    // SwfStore: the item SWFs the CharPage loader needs, kept in Supabase
    // (table char_swfs, see tools/char_swfs.sql) instead of on the user's disk.
    // One row per CDN path: name, linkage, bytes, size. Keyed by path so two
    // characters wearing the same helm share one copy, and a second machine can
    // render a character without going near the game CDN.
    //
    // Both calls are best-effort: a missing table or a denied policy leaves the
    // bytes in memory, so the preview still works - it just does not persist.
    static class SwfStore
    {
        // <rest>/swfs -> <rest>/char_swfs (same project and key as the catalog).
        static readonly string Endpoint;
        static readonly string Key;
        static readonly HttpClient Http;

        // Session cache: a reload, a cosmetic toggle or a gender flip asks for
        // the same paths again, and the player wants them immediately.
        static readonly Dictionary<string, byte[]> _mem = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        static readonly object _gate = new object();
        static bool _warned;
        static bool _noName;   // table predates the name/linkage columns

        static SwfStore()
        {
            string url = Wardrobe.SupabaseUrl;
            string key = Wardrobe.SupabaseKey;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)) return;
            url = url.TrimEnd('/');
            int slash = url.LastIndexOf('/');
            if (slash > 0) url = url.Substring(0, slash) + "/char_swfs";
            Endpoint = url;
            Key = key;
            Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        public static bool Enabled { get { return Endpoint != null; } }

        // Row key: "items/helms/X.swf" (lower case, forward slashes), matching
        // the path convention already used by the swfs catalog.
        public static string KeyOf(string rel)
        {
            return (rel ?? "").Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
        }

        // This session's copy, else one row from the table. null = not stored.
        public static byte[] Get(string key)
        {
            if (!Enabled || string.IsNullOrEmpty(key)) return null;
            byte[] hit;
            lock (_gate) if (_mem.TryGetValue(key, out hit)) return hit;
            try
            {
                string url = Endpoint + "?select=data&path=eq." + Uri.EscapeDataString(key) + "&limit=1";
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.Add("apikey", Key);
                    req.Headers.Add("Authorization", "Bearer " + Key);
                    var resp = Http.SendAsync(req).GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode)
                    {
                        Warn((int)resp.StatusCode, ReadBody(resp), "read");
                        return null;
                    }
                    var rows = JArray.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    if (rows.Count == 0) return null;
                    string b64 = (string)rows[0]["data"];
                    if (string.IsNullOrEmpty(b64)) return null;
                    byte[] bytes = Convert.FromBase64String(b64);
                    Remember(key, bytes);
                    return bytes;
                }
            }
            catch (Exception ex) { AppForm.Dbg("char swf store: read failed: " + ex.Message); return null; }
        }

        // Upsert one row (resolution=merge-duplicates, so an existing path
        // updates instead of failing the insert). false = not persisted; the
        // caller still holds the bytes, so this never blocks the preview.
        public static bool Put(string key, byte[] bytes, string name = null, string linkage = null)
        {
            if (!Enabled || string.IsNullOrEmpty(key) || bytes == null || bytes.Length < 4) return false;
            Remember(key, bytes);
            int status;
            string err = Send(key, bytes, name, linkage, out status);

            // A table created before name/linkage existed rejects the whole row.
            // Drop the extras (once, for the session) rather than lose the art -
            // the names are recoverable from the swfs catalog until then.
            if (err.Length != 0 && !_noName && status == 400 &&
                (err.IndexOf("42703", StringComparison.Ordinal) >= 0 ||
                 err.IndexOf("PGRST204", StringComparison.Ordinal) >= 0))
            {
                _noName = true;
                AppForm.Dbg("char swf store: no name/linkage columns yet - storing the SWF only " +
                    "(re-run tools/char_swfs.sql to record names)");
                err = Send(key, bytes, null, null, out status);
            }

            if (err.Length == 0) return true;
            Warn(status, err, "save");
            return false;
        }

        static string Send(string key, byte[] bytes, string name, string linkage, out int status)
        {
            var row = new JObject
            {
                { "path", key },
                { "data", Convert.ToBase64String(bytes) },
                { "size", bytes.Length }
            };
            if (!_noName)
            {
                // Only the name the character page actually printed; the column
                // stays null when the page gave none.
                if (!string.IsNullOrEmpty(name)) row["name"] = name;
                if (!string.IsNullOrEmpty(linkage)) row["linkage"] = linkage;
            }
            status = 0;
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, Endpoint))
                {
                    req.Content = new StringContent(new JArray(row).ToString(Formatting.None), Encoding.UTF8, "application/json");
                    req.Headers.Add("apikey", Key);
                    req.Headers.Add("Authorization", "Bearer " + Key);
                    req.Headers.Add("Prefer", "resolution=merge-duplicates,return=minimal");
                    var resp = Http.SendAsync(req).GetAwaiter().GetResult();
                    status = (int)resp.StatusCode;
                    return resp.IsSuccessStatusCode ? "" : ReadBody(resp);
                }
            }
            catch (Exception ex) { status = -1; return ex.Message; }
        }

        static void Remember(string key, byte[] bytes)
        {
            lock (_gate) _mem[key] = bytes;
        }

        static string ReadBody(HttpResponseMessage resp)
        {
            string body = "";
            try { body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() ?? ""; } catch { }
            return body.Length > 200 ? body.Substring(0, 200) : body;
        }

        // One line per run, not per item: 10 items would otherwise repeat it.
        static void Warn(int status, string body, string what)
        {
            if (_warned) return;
            _warned = true;
            AppForm.Dbg("char swf store: " + what + " rejected " + status + " " + body +
                " - run tools/char_swfs.sql in Supabase (items stay in memory until it exists)");
        }
    }
}
