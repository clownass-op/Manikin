using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ManikinApp
{
    // GitHub: the catalog mirror repo doubles as the store.
    //
    // Reads are plain raw.githubusercontent.com GETs - no token, no API quota.
    // Writes go through the Contents API and need a token the user supplies
    // (MANIKIN_GITHUB_TOKEN, or a github.key file next to the exe). The token is
    // deliberately NOT compiled in: this exe is published, and a write token in
    // it would let anyone push to the repo that every copy downloads from.
    //
    // Without a token GitHub.Writable is false and the callers keep their old
    // Supabase path, so a token-less build still saves items.
    static class GitHub
    {
        internal const string Owner = "clownass-op";
        internal const string Repo = "Manikin-Catalog";
        internal const string Branch = "main";

        // null/empty = read-only.
        internal static readonly string Token;

        static readonly HttpClient Http = CreateClient();

        static GitHub() { Token = Config.ReadToken(); }

        public static bool Writable { get { return !string.IsNullOrEmpty(Token); } }

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            c.DefaultRequestHeaders.Add("User-Agent", "Manikin/1.1 github-store");
            return c;
        }

        // raw.githubusercontent.com/<owner>/<repo>/<branch>/<path>, escaped one
        // segment at a time so an odd file name cannot break the URL.
        public static string RawUrl(string repoPath)
        {
            var sb = new StringBuilder("https://raw.githubusercontent.com/");
            sb.Append(Owner).Append('/').Append(Repo).Append('/').Append(Branch);
            string clean = (repoPath ?? "").Replace('\\', '/').Trim('/');
            if (clean.Length > 0)
                foreach (string part in clean.Split('/'))
                    if (part.Length > 0) sb.Append('/').Append(Uri.EscapeDataString(part));
            return sb.ToString();
        }

        static string ApiUrl(string repoPath)
        {
            return "https://api.github.com/repos/" + Owner + "/" + Repo + "/contents/" +
                   (repoPath ?? "").Replace('\\', '/').Trim('/');
        }

        // One raw GET. null = missing or unreachable; the caller decides whether
        // that is a cache miss or a failure.
        public static byte[] GetRaw(string repoPath)
        {
            try
            {
                using (var resp = Http.GetAsync(RawUrl(repoPath)).GetAwaiter().GetResult())
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    return resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                }
            }
            catch { return null; }
        }

        // Does a folder exist in the mirror? One call answers it for every path
        // underneath, so a store that was never populated costs a single probe
        // per session instead of a 404 on every item.
        public static bool FolderExists(string repoPath)
        {
            try
            {
                using (var resp = Http.GetAsync(ApiUrl(repoPath) + "?ref=" + Branch).GetAwaiter().GetResult())
                    return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // Contents API read. sha is what an update has to send back, and getting
        // both in one call saves a round trip before a rewrite.
        public static byte[] GetFile(string repoPath, out string sha, out string error)
        {
            sha = null; error = "";
            if (!Writable) { error = "no GitHub token"; return null; }
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl(repoPath) + "?ref=" + Branch))
                {
                    Auth(req, null);
                    using (var resp = Http.SendAsync(req).GetAwaiter().GetResult())
                    {
                        string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if ((int)resp.StatusCode == 404) { error = "not found"; return null; }
                        if (!resp.IsSuccessStatusCode) { error = (int)resp.StatusCode + " " + Short(body); return null; }
                        var json = JObject.Parse(body);
                        sha = (string)json["sha"];
                        string b64 = (string)json["content"];
                        // Over 1 MB the API answers with metadata only ("encoding":
                        // "none"), so the bytes need the raw media type. Under 1 MB
                        // the inline base64 saves a round trip.
                        if (!string.IsNullOrEmpty(b64))
                            return Convert.FromBase64String(b64.Replace("\n", "").Replace("\r", ""));
                        return RawBytes(repoPath, out error);
                    }
                }
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        // Same endpoint, raw media type: the only way to get a file the JSON
        // form leaves out. Same ref, so no stale CDN copy like download_url.
        static byte[] RawBytes(string repoPath, out string error)
        {
            error = "";
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl(repoPath) + "?ref=" + Branch))
                {
                    Auth(req, "application/vnd.github.raw");
                    using (var resp = Http.SendAsync(req).GetAwaiter().GetResult())
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            error = (int)resp.StatusCode + " raw";
                            return null;
                        }
                        return resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    }
                }
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        // Create or update one file. sha "" = the caller believes it is new; in
        // that case "sha wasn't supplied" means someone stored it first, which
        // for a path-keyed store is success, not a failure.
        public static bool PutFile(string repoPath, byte[] bytes, string message, string sha, out string error)
        {
            error = "";
            if (!Writable) { error = "no GitHub token"; return false; }
            try
            {
                var payload = new JObject
                {
                    { "message", message },
                    { "content", Convert.ToBase64String(bytes) },
                    { "branch", Branch }
                };
                if (!string.IsNullOrEmpty(sha)) payload["sha"] = sha;

                using (var req = new HttpRequestMessage(HttpMethod.Put, ApiUrl(repoPath)))
                {
                    Auth(req, null);
                    req.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                    using (var resp = Http.SendAsync(req).GetAwaiter().GetResult())
                    {
                        string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (resp.IsSuccessStatusCode) return true;
                        if ((int)resp.StatusCode == 422 && body != null &&
                            (body.IndexOf("sha", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             body.IndexOf("already exists", StringComparison.OrdinalIgnoreCase) >= 0))
                        { error = "already stored"; return true; }
                        error = (int)resp.StatusCode + " " + Short(body);
                        return false;
                    }
                }
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // Accept is per request now: JSON for metadata and writes, raw for the
        // bytes of a file too big to inline.
        static void Auth(HttpRequestMessage req, string accept)
        {
            req.Headers.Add("Authorization", "Bearer " + Token);
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            req.Headers.TryAddWithoutValidation("Accept", accept ?? "application/vnd.github+json");
        }

        static string Short(string s)
        {
            s = (s ?? "").Replace("\n", " ").Replace("\r", " ");
            return s.Length > 160 ? s.Substring(0, 160) : s;
        }

        // Token source: MANIKIN_GITHUB_TOKEN wins, then github.key next to the
        // exe or one folder up (git-ignored). One
        // `token=` line; a bare token line also works.
        static class Config
        {
            public static string ReadToken()
            {
                string token = Env("MANIKIN_GITHUB_TOKEN");
                if (!string.IsNullOrEmpty(token)) return token.Trim();
                try
                {
                    string exe = AppDomain.CurrentDomain.BaseDirectory;
                    string root = null;
                    try { root = Path.GetDirectoryName(exe.TrimEnd(Path.DirectorySeparatorChar)); } catch { }
                    foreach (var dir in new[] { exe, root })
                    {
                        if (string.IsNullOrEmpty(dir)) continue;
                        string file = Path.Combine(dir, "github.key");
                        if (!File.Exists(file)) continue;
                        foreach (var raw in File.ReadAllLines(file))
                        {
                            string line = (raw ?? "").Trim();
                            if (line.Length == 0 || line[0] == '#') continue;
                            int eq = line.IndexOf('=');
                            string value = (eq > 0 ? line.Substring(eq + 1) : line).Trim().Trim('"');
                            if (value.Length > 0) return value;
                        }
                    }
                }
                catch { }
                return null;
            }

            static string Env(string name)
            {
                try { return Environment.GetEnvironmentVariable(name); } catch { return null; }
            }
        }
    }
}
