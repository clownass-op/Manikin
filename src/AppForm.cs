using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[assembly: ComVisible(true)]

namespace ManikinApp
{
    // FbHost: COM proxy exposed to the page via AddHostObjectToScript. Uses a
    // completely different delivery path than PostWebMessageAsJson (which is
    // silently broken on this machine: C#->JS web messages never arrive).
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public class FbHost
    {
        readonly AppForm _form;

        public FbHost(AppForm form) { _form = form; Wardrobe.Preload(); }

        void Ui(Action a) { if (_form.IsHandleCreated) _form.Invoke((Action)a); else a(); }
        T Ui<T>(Func<T> f) { if (_form.IsHandleCreated) return (T)_form.Invoke((Func<T>)f); return f(); }

        public string Ping(string s) { return "pong:" + s; }
        public void JsLog(string json) { AppForm.Dbg("JS: " + json); }
        public string GetAppDir() { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }

        // Settings > "Download SWFs". Off: fetched items are cached in a temp
        // folder instead of the swf folder, so nothing is written there.
        public bool SetDownloadSwf(bool on) { Downloads.SaveToFolder = on; return Downloads.SaveToFolder; }
        public string GetLog() { return AppForm.GetLog(); }
        public void ClearLog() { AppForm.ClearLog(); }

        public void Minimize() { Ui(() => _form.DoMinimize()); }
        public void Maximize() { Ui(() => _form.DoMaximize()); }
        public void CloseWindow() { Ui(() => _form.DoClose()); }
        public void StartDrag() { Ui(() => _form.DoStartDrag()); }
        public void StartResize(string edge) { Ui(() => _form.DoStartResize(edge)); }
        public bool IsMaximized() { return Ui(() => _form.IsMaximized); }
        public void SetOverlaysHidden(bool hidden) { Ui(() => _form.DoSetOverlaysHidden(hidden)); }
        public void ReportRect(int x, int y, int w, int h) { Ui(() => _form.DoReportRect(x, y, w, h)); }
        public void FlashCall(string method, string paramsJson) { Ui(() => _form.DoFlashCall(method, paramsJson)); }
        public void Gear(string rowsJson) { Ui(() => _form.DoGear(rowsJson)); }
        public void Cosmetics(string stateJson) { Ui(() => _form.DoCosmeticsButton(stateJson)); }
        // Click counter for the overlay shirt button. The page polls this
        // (see cosSeq): C#->JS messages don't arrive on this machine, so a
        // push is impossible. Plain int read is atomic; only the UI thread
        // ever writes it.
        public int CosmeticsSeq() { return _form.CosmeticsToggleSeq; }
        // Loader line: the page polls for a committed name (same reason as
        // CosmeticsSeq: C#->JS pushes don't arrive). Take-and-clear.
        public string LoadNameRequest() { return _form.TakePendingLoad(); }
        // NOTE: never Task.Run()+GetResult() here (that deadlocks when the
        // host call arrives on the UI thread: pool waits on Invoke while the
        // UI thread waits on the pool). InvokeRequired-gated direct call is
        // safe from either thread.
        public string FlashQuery(string method)
        {
            if (_form.InvokeRequired) return (string)_form.Invoke(new Func<string>(() => _form.DoFlashQuery(method)));
            return _form.DoFlashQuery(method);
        }
        public void Background(string name) { Ui(() => _form.DoBackground(name)); }
        public void ThemeColor(string hex) { Ui(() => _form.DoThemeColor(hex)); }
        // dockHidden/sheetHidden ride along so the rail can tell a hidden
        // panel (whose rects are still reported to keep the slide-path hole
        // cut) from a shown one. object (not bool) so single-arg callers
        // (Type.Missing) don't trip COM marshalling; omitted/false = infer.
        public void ReportOverlayRects(string rectsJson, object dockHidden, object sheetHidden)
        {
            bool dockH = dockHidden is bool b && b;
            bool sheetH = sheetHidden is bool s && s;
            Ui(() => _form.DoOverlayRects(rectsJson, dockH, sheetH));
        }
        public void Reset() { Ui(() => _form.DoReset()); }
        public void LoadItem(string itemType, string itemFile, string weaponType, string link) { Ui(() => _form.DoLoadItem(itemType, itemFile, weaponType, link)); }
        public void DropItemBytes(string itemType, string fileName, string base64) { Ui(() => _form.DoDropItem(itemType, fileName, base64)); }
        // Loader items come back from Supabase as bytes; they never existed as a
        // file on this machine, so they go to the player without a path.
        public void LoadItemB64(string itemType, string fileName, string base64, string weaponType, string link)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64 ?? ""); }
            catch { AppForm.Dbg("LoadItemB64 bad data: " + fileName); return; }
            if (bytes.Length < 4) return;
            Ui(() => _form.DoLoadItemBytes(itemType, fileName, bytes, weaponType, link));
        }

        // Network work runs as a background job the page polls for. Host
        // object calls arrive on the UI thread, so the old blocking
        // LoadChar/DownloadAll froze the whole window for the duration of
        // the fetch (up to 2 x 60 s on a dead network).
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> _jobs =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>>();
        static int _jobSeq;

        static string StartJob(Func<string> work)
        {
            string id = "job" + System.Threading.Interlocked.Increment(ref _jobSeq);
            _jobs[id] = Task.Run(work);
            return id;
        }

        public string BeginLoadChar(string name) { return StartJob(() => LoadCharCore(name)); }
        // Char-load items stream back to the page as each one resolves, so the
        // avatar starts equipping on the first SWF instead of the last.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string,
            System.Collections.Concurrent.ConcurrentQueue<JObject>> _streams =
            new System.Collections.Concurrent.ConcurrentDictionary<string,
                System.Collections.Concurrent.ConcurrentQueue<JObject>>();

        public string BeginDownloadAll(string varsJson, string dir)
        {
            string id = "job" + System.Threading.Interlocked.Increment(ref _jobSeq);
            var q = new System.Collections.Concurrent.ConcurrentQueue<JObject>();
            _streams[id] = q;
            _jobs[id] = Task.Run(() => DownloadAllCore(varsJson, dir, q));
            return id;
        }

        // Everything finished since the last poll. done = the job is over, so the
        // page stops polling and the id is forgotten.
        public string CharLoadPoll(string id)
        {
            System.Collections.Concurrent.ConcurrentQueue<JObject> q;
            if (id == null || !_streams.TryGetValue(id, out q))
                return JsonConvert.SerializeObject(new { done = true, ok = false, error = "unknown job", items = new JArray() });
            var items = new JArray();
            JObject got;
            while (q.TryDequeue(out got)) items.Add(got);
            Task<string> t;
            if (!_jobs.TryGetValue(id, out t) || !t.IsCompleted)
                return JsonConvert.SerializeObject(new { done = false, items });
            string res = t.Status == TaskStatus.RanToCompletion
                ? t.Result
                : JsonConvert.SerializeObject(new { ok = false, error = t.Exception == null ? "job failed" : t.Exception.GetBaseException().Message });
            JObject root = JObject.Parse(res);
            bool ok = root["ok"] != null && root["ok"].Type == JTokenType.Boolean && (bool)root["ok"];
            JArray arr = root["items"] as JArray;
            string error = root["error"] == null ? "" : (string)root["error"];
            _jobs.TryRemove(id, out t);
            _streams.TryRemove(id, out q);
            return JsonConvert.SerializeObject(new
            {
                done = true,
                ok,
                error,
                total = arr == null ? items.Count : arr.Count,
                items
            });
        }
        // Gender swap for an item that came from the loader (armor and hair art
        // is per gender). Same DB-then-CDN route as the first load.
        public string BeginCharItem(string type, string file, string gender, string name) { return StartJob(() => CharItemCore(type, file, gender, name)); }

        // Wardrobe panel: catalog search (synchronous - a few ms once the
        // catalog has loaded) and one item's SWF download (a job).
        public string WardrobeQuery(string slot, string q, string access, int offset, int limit, string rarity, string state) { return Wardrobe.Query(slot, q, access, offset, limit, rarity, state); }
        public string BeginWardrobeFetch(string slot, string file, string gender, string dir) { return StartJob(() => Wardrobe.Fetch(slot, file, gender, dir)); }

        // Settings > Catalog: diff aqcloset.com against our table, then insert.
        public string BeginCatalogSyncPlan() { return StartJob(() => CatalogSync.Plan()); }
        public string BeginCatalogSyncApply() { return StartJob(() => CatalogSync.Apply()); }
        public string CatalogSyncProgress() { return CatalogSync.Progress(); }

        // "" while running; the job's JSON once done (then forgotten).
        public string JobResult(string id)
        {
            Task<string> t;
            if (id == null || !_jobs.TryGetValue(id, out t)) return JsonConvert.SerializeObject(new { ok = false, error = "unknown job" });
            if (!t.IsCompleted) return "";
            _jobs.TryRemove(id, out t);
            if (t.IsFaulted) return JsonConvert.SerializeObject(new { ok = false, error = t.Exception.GetBaseException().Message });
            return t.Result;
        }

        // Gear-list icon clicks, drained by the page so the icons and the
        // Misc checkboxes share one hidden/shown state.
        public string TakeGearClicks() { return _form.TakeGearClicks(); }

        // Copy the whole debug log to the clipboard (Log tab button).
        public bool CopyLog() { return Ui(() => _form.DoCopyLog()); }

        // Copy the preview to the clipboard as an image (camera button).
        public bool SnapImage() { return Ui(() => _form.DoSnapImage()); }
        // Copy the character alone, scene removed, as a transparent PNG. The page
        // calls this as snapChar, so the COM name has to match exactly.
        public bool SnapChar() { return Ui(() => _form.DoSnapCutout()); }

        // Record the preview's animation as a GIF and copy it (Capture tab).
        // An int flag rather than a bool: IDispatch drops boolean arguments on
        // this host object, and a silent false is what made cut-out takes record
        // the scene.
        public bool GifStart(int ms, string fmt, int cut) { return Ui(() => _form.BeginGif(ms, fmt, cut != 0)); }
        public string GifStop() { return Ui(() => _form.StopGif()); }
        public string GifPoll() { return Ui(() => _form.GifPoll()); }

        static string LoadCharCore(string name)
        {
            try
            {
                string body = null;
                foreach (var url in new[]
                {
                    "https://account.aq.com/CharPage?id=" + Uri.EscapeDataString(name),
                    "http://www.aq.com/character.asp?id=" + Uri.EscapeDataString(name)
                })
                {
                    body = Downloads.GetAsync(url).GetAwaiter().GetResult();
                    if (body != null) break;
                }
                if (body == null)
                    return JsonConvert.SerializeObject(new { ok = false, error = "Could not reach the character page." });
                var vars = Downloads.ParseFlashVars(body);
                if (vars == null)
                    return JsonConvert.SerializeObject(new { ok = false, error = "No FlashVars on that character page." });
                var v = new JObject();
                foreach (var kv in vars) v[kv.Key] = kv.Value;
                return JsonConvert.SerializeObject(new { ok = true, vars = v });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { ok = false, error = ex.Message }); }
        }

        // dir is unused now: a character load reads from Supabase and falls back
        // to the game CDN, so nothing is written to the output folder. The page
        // still passes the configured folder, so the signature stays.
        static string DownloadAllCore(string varsJson, string dir, System.Collections.Concurrent.ConcurrentQueue<JObject> sink = null)
        {
            try
            {
                var vo = JObject.Parse(varsJson);
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in vo.Properties()) vars[p.Name] = p.Value.Value<string>();
                var items = Downloads.BuildDownloads(vars);
                var results = new JObject[items.Count];
                int fromDb = 0, fetched = 0, stored = 0, failed = 0;
                // Parallel: each item is one ~0.5-2 s Supabase round trip, so a
                // serial loop made the load the SUM of seven latencies. Six at a
                // time makes it roughly the slowest one. Each finished item goes
                // to the page right away, so equipping no longer waits for the
                // set to complete.
                System.Threading.Tasks.Parallel.For(0, items.Count,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 6 },
                    i =>
                    {
                        int db = 0, net = 0, put = 0;
                        JObject r = ResolveItem(items[i], ref db, ref net, ref put);
                        results[i] = r;
                        System.Threading.Interlocked.Add(ref fromDb, db);
                        System.Threading.Interlocked.Add(ref fetched, net);
                        System.Threading.Interlocked.Add(ref stored, put);
                        if (!(bool)r["ok"]) System.Threading.Interlocked.Increment(ref failed);
                        if (sink != null) sink.Enqueue(r);
                    });
                var all = new JArray();
                foreach (var r in results) if (r != null) all.Add(r);
                AppForm.Dbg("charload: " + items.Count + " items - " + fromDb + " from the database, " +
                    fetched + " fetched from the game CDN (" + stored + " queued to store), " + failed + " failed");
                return JsonConvert.SerializeObject(new { ok = failed == 0, items = all, dir = "" });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { ok = false, error = ex.Message }); }
        }

        // One loader item: the store and the game CDN are asked at the same time
        // and whichever answers first wins. Asking the store first is what put a
        // two-second gap between the names appearing and the avatar wearing them:
        // the same six files arrive from the CDN in ~0.08 s and take ~1.4 s as
        // PostgREST rows. The store is still what renders gear the CDN has
        // dropped, because a failed CDN answer is not a win. Either way the bytes
        // go straight to the player - no intermediate file on this device.
        static JObject ResolveItem(Downloads.ItemDownload it, ref int fromDb, ref int fetched, ref int stored)
        {
            string key = SwfStore.KeyOf(it.File);
            byte[] bytes = SwfStore.Cached(key);
            if (bytes != null) { fromDb++; return ItemResult(it, key, bytes); }

            var store = System.Threading.Tasks.Task.Run(() => SwfStore.Get(key));
            var cdn = Downloads.FetchBytesAsync(it.Url);
            bool fromCdn;
            bytes = FirstBytes(cdn, store, out fromCdn);
            if (bytes != null)
            {
                if (fromCdn)
                {
                    fetched++;
                    // Only when the store had nothing to offer: writing a path it
                    // already holds would be a commit that changes no bytes.
                    bool storeEmpty = false;
                    try { storeEmpty = store.IsCompleted && store.Result == null; } catch { }
                    if (storeEmpty) { SwfStore.PutLater(key, bytes, it.Name, it.Link); stored++; }
                }
                else fromDb++;
            }
            return ItemResult(it, key, bytes);
        }

        // Whichever task produced usable bytes first. An empty or failed answer
        // is not a win, so a CDN that no longer serves the file falls through to
        // the store read that is still in flight.
        static byte[] FirstBytes(System.Threading.Tasks.Task<byte[]> first, System.Threading.Tasks.Task<byte[]> second, out bool firstWon)
        {
            firstWon = false;
            var pending = new System.Collections.Generic.List<System.Threading.Tasks.Task<byte[]>> { first, second };
            int until = Environment.TickCount + 60000;
            while (pending.Count > 0)
            {
                int left = until - Environment.TickCount;
                if (left <= 0) break;
                var live = pending.ToArray();
                int i = System.Threading.Tasks.Task.WaitAny(live, left);
                if (i < 0) break;
                var done = live[i];
                pending.Remove(done);
                try
                {
                    byte[] v = done.Result;
                    if (v != null && v.Length > 4) { firstWon = ReferenceEquals(done, first); return v; }
                }
                catch { }
            }
            return null;
        }

        // Gender swap for a loader entry: armor is classes/<gender>/<file>, hair
        // already carries hair/<gender>/. Same resolver, so a body switch on a
        // character loaded from the page never writes an SWF to disk either.
        static string CharItemCore(string type, string file, string gender, string name)
        {
            try
            {
                string rel = (file ?? "").Trim().Replace('\\', '/');
                if (string.Equals(type, "Armor", StringComparison.OrdinalIgnoreCase))
                    rel = "classes/" + (gender == "M" ? "M" : "F") + "/" + rel;
                var it = new Downloads.ItemDownload { Type = type, Name = name, Url = Downloads.GAME + rel, File = Downloads.LocalPathOf(rel) };
                int fromDb = 0, fetched = 0, stored = 0;
                JObject r = ResolveItem(it, ref fromDb, ref fetched, ref stored);
                bool ok = (bool)r["ok"];
                return JsonConvert.SerializeObject(new
                {
                    ok,
                    file = (string)r["file"],
                    path = (string)r["path"],
                    b64 = (string)r["b64"],
                    error = ok ? "" : "Download failed: " + rel
                });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { ok = false, error = ex.Message }); }
        }

        internal static JObject ItemResult(Downloads.ItemDownload it, string key, byte[] bytes)
        {
            bool ok = bytes != null && bytes.Length > 4;
            return new JObject
            {
                { "type", it.Type },
                { "file", it.File },
                { "ok", ok },
                // A char_swfs row key, not a file path: the page hands the base64
                // back to the player instead of opening anything on disk.
                { "path", ok ? key : "" },
                { "b64", ok ? Convert.ToBase64String(bytes) : "" },
                { "link", it.Link ?? "" },
                { "cosmetic", it.Cosmetic }
            };
        }

        public string PickFolder() { return Ui(() => _form.DoPickFolder()); }
        public string SaveFile(string data, string name) { return Ui(() => _form.DoSaveFile(data, name)); }
        public string OpenFile() { return Ui(() => _form.DoOpenFile()); }
    }

    // AppForm: the combined one-window app. A WebView2 fills the window and
    // renders ui/index.html; FlashCore (the real Flash ActiveX) is a SIBLING
    // control positioned over the HTML preview region, so the Chromium
    // compositor never paints over it. Page -> C# calls go through the FbHost
    // COM proxy (AddHostObjectToScript); C# -> page state is polled by the
    // page (the web-message channel silently fails C#->JS on some machines).

    // OleDrop: shared COM drag-drop plumbing (IDropTarget + CF_HDROP file
    // extraction). Register on any hit-testable window handle; drops carry
    // real Explorer file paths.
    static class OleDrop
    {
        [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ITarget
        {
            void DragEnter([In, MarshalAs(UnmanagedType.Interface)] object pDataObj, [In] int grfKeyState, [In] POINT pt, [In, Out] ref int pdwEffect);
            void DragOver([In] int grfKeyState, [In] POINT pt, [In, Out] ref int pdwEffect);
            void DragLeave();
            void Drop([In, MarshalAs(UnmanagedType.Interface)] object pDataObj, [In] int grfKeyState, [In] POINT pt, [In, Out] ref int pdwEffect);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("ole32.dll")]
        public static extern int RegisterDragDrop(IntPtr hwnd, ITarget pDropTarget);
        [DllImport("ole32.dll")]
        public static extern int RevokeDragDrop(IntPtr hwnd);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern uint DragQueryFile(IntPtr hDrop, uint iFile, System.Text.StringBuilder lpszFile, int cch);
        [DllImport("ole32.dll")]
        static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM pmedium);

        public const int CF_HDROP = 15;
        public const int COPY = 1;
        public const int NONE = 0;

        public static List<string> Files(object pDataObj)
        {
            var list = new List<string>();
            try
            {
                var data = (System.Runtime.InteropServices.ComTypes.IDataObject)pDataObj;
                var fmt = new System.Runtime.InteropServices.ComTypes.FORMATETC
                {
                    cfFormat = (short)CF_HDROP,
                    ptd = IntPtr.Zero,
                    dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
                    lindex = -1,
                    tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
                };
                var med = new System.Runtime.InteropServices.ComTypes.STGMEDIUM();
                data.GetData(ref fmt, out med);
                try
                {
                    uint n = DragQueryFile(med.unionmember, 0xFFFFFFFF, null, 0);
                    for (uint i = 0; i < n; i++)
                    {
                        uint len = DragQueryFile(med.unionmember, i, null, 0);
                        var sb = new System.Text.StringBuilder((int)len + 1);
                        if (DragQueryFile(med.unionmember, i, sb, sb.Capacity) > 0)
                            list.Add(sb.ToString());
                    }
                }
                finally { try { ReleaseStgMedium(ref med); } catch { } }
            }
            catch { }
            return list;
        }
    }

    // HudOverlay: transparent, click-through layered window floating over the
    // Flash preview's edge (CharPage-style icon + name list: sword, helm,
    // armor names...). Transparent - icons + names float over the scene
    // with no background. Parked left by default; nudged just right of the
    // sheet menu while it is open, then back. A normal
    // WinForms panel can't do this: "transparent" only fakes the parent's
    // background, never the sibling Flash window beneath. A WS_EX_LAYERED
    // window composited with per-pixel alpha really floats above the scene,
    // and WS_EX_TRANSPARENT lets every click fall through to the avatar.
    public class HudOverlay : Form
    {
        public class Row
        {
            public string Key;
            public Image Icon;
            public string Name;
            public bool Dim;
        }

        readonly List<Row> _rows = new List<Row>();
        readonly Dictionary<string, Image> _icons = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        readonly Font _font;
        const int IconSize = 26;
        const int RowH = 34;
        const int PadX = 10;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        const int ULW_ALPHA = 2;
        const byte AC_SRC_ALPHA = 1;
        // Clickable icons, click-through everywhere else: the window keeps
        // WS_EX_LAYERED (per-pixel alpha) but drops WS_EX_TRANSPARENT in
        // favor of WM_NCHITTEST hit-testing - HTCLIENT over an icon rect,
        // HTTRANSPARENT anywhere else so avatar clicks pass through.
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_NOACTIVATE = 0x8000000;
        const int WM_NCHITTEST = 0x84;
        const int HTCLIENT = 1;
        const int HTTRANSPARENT = -1;

        public event Action<string> IconClicked;
        public event Action<string, string> FileDropped;

        readonly List<Tuple<Rectangle, string>> _hits = new List<Tuple<Rectangle, string>>();

        class GearDrop : OleDrop.ITarget
        {
            readonly HudOverlay _o;
            public GearDrop(HudOverlay o) { _o = o; }
            public void DragEnter(object pDataObj, int grfKeyState, OleDrop.POINT pt, ref int pdwEffect)
            {
                var f = OleDrop.Files(pDataObj).FindAll(x => x.EndsWith(".swf", StringComparison.OrdinalIgnoreCase));
                AppForm.Dbg("GearDrop DragEnter files=" + f.Count);
                pdwEffect = f.Count > 0 ? OleDrop.COPY : OleDrop.NONE;
            }
            public void DragOver(int grfKeyState, OleDrop.POINT pt, ref int pdwEffect) { pdwEffect = OleDrop.COPY; }
            public void DragLeave() { }
            public void Drop(object pDataObj, int grfKeyState, OleDrop.POINT pt, ref int pdwEffect)
            {
                var files = OleDrop.Files(pDataObj).FindAll(x => x.EndsWith(".swf", StringComparison.OrdinalIgnoreCase));
                string slot = _o.SlotAt(_o.PointToClient(new Point(pt.X, pt.Y)));
                AppForm.Dbg("GearDrop Drop files=" + files.Count + " slot=" + (slot ?? "-"));
                pdwEffect = OleDrop.NONE;
                if (slot == null) return;
                var cb = _o.FileDropped;
                if (cb == null) return;
                foreach (var f in files)
                {
                    try { cb(slot, f); } catch { }
                }
                pdwEffect = files.Count > 0 ? OleDrop.COPY : OleDrop.NONE;
            }
        }

        readonly GearDrop _dropTarget;

        public string SlotAt(Point p)
        {
            foreach (var h in _hits) { if (h.Item1.Contains(p)) return h.Item2; }
            return null;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                int lp = m.LParam.ToInt32();
                var p = PointToClient(new Point(lp & 0xFFFF, (lp >> 16) & 0xFFFF));
                foreach (var h in _hits)
                {
                    if (h.Item1.Contains(p)) { m.Result = (IntPtr)HTCLIENT; return; }
                }
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            foreach (var h in _hits)
            {
                if (h.Item1.Contains(e.Location))
                {
                    var cb = IconClicked;
                    if (cb != null) cb(h.Item2);
                    return;
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = false;
            foreach (var h in _hits) { if (h.Item1.Contains(e.Location)) { over = true; break; } }
            Cursor = over ? Cursors.Hand : Cursors.Default;
        }

        public bool HasHit(Point p)
        {
            foreach (var h in _hits) { if (h.Item1.Contains(p)) return true; }
            return false;
        }

        public bool ClickAt(Point p)
        {
            foreach (var h in _hits)
            {
                if (h.Item1.Contains(p))
                {
                    var cb = IconClicked;
                    if (cb != null) cb(h.Item2);
                    return true;
                }
            }
            return false;
        }

        [DllImport("user32.dll")]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);
        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hDC);

        public HudOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            _font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            Width = 240;
            _dropTarget = new GearDrop(this);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int rc = OleDrop.RegisterDragDrop(Handle, _dropTarget);
                AppForm.Dbg("GearDrop RegisterDragDrop rc=" + rc);
            }
            catch (Exception ex) { AppForm.Dbg("GearDrop register EX: " + ex.Message); }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try { OleDrop.RevokeDragDrop(Handle); } catch { }
            base.OnHandleDestroyed(e);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        Image GetIcon(string dir, string name)
        {
            Image img;
            if (_icons.TryGetValue(name, out img)) return img;
            try
            {
                string p = Path.Combine(dir, "ui", "icons", name + ".png");
                if (File.Exists(p)) img = Image.FromFile(p);
            }
            catch { img = null; }
            _icons[name] = img;
            return img;
        }

        public void SetRows(string appDir, JArray rows)
        {
            _rows.Clear();
            if (rows != null)
            {
                foreach (var r in rows)
                {
                    if (!(r is JObject)) continue;
                    var o = (JObject)r;
                    string key = (o.Value<string>("icon") ?? "").ToLowerInvariant();
                    _rows.Add(new Row
                    {
                        Key = key,
                        Icon = GetIcon(appDir, key),
                        Name = o.Value<string>("name") ?? "",
                        Dim = o.Value<bool>("dim")
                    });
                }
            }
            if (_rows.Count == 0) { Hide(); return; }
            Render();
        }

        public bool HasRows { get { return _rows.Count > 0; } }

        void Render()
        {
            // Measure pass: long names (e.g. "Prince of Dragon's Shield and
            // Blade") word-wrap to at most 2 lines and those rows grow taller
            // instead of running off the 240px window edge.
            float maxW = Width - (PadX + IconSize + 8) - PadX;
            using (var fmt = StringFormat.GenericDefault)
            {
            var heights = new List<int>(_rows.Count);
            using (var mBmp = new Bitmap(1, 1))
            using (var measure = Graphics.FromImage(mBmp))
            {
                float lineH = _font.GetHeight(measure);
                foreach (var r in _rows)
                {
                    var sz = measure.MeasureString(r.Name ?? "", _font, new SizeF(maxW, 1000), fmt);
                    int lines = Math.Max(1, Math.Min(2, (int)Math.Ceiling(sz.Height / lineH - 0.01)));
                    heights.Add(Math.Max(RowH, (int)Math.Ceiling(lines * lineH) + 10));
                }
            }
            int total = 8;
            foreach (var h in heights) total += h;
            Height = total;
            using (var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    // No background: icons + names float transparently over
                    // the scene (per-pixel alpha), exactly like before.
                    using (var white = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                    using (var shade = new SolidBrush(Color.FromArgb(230, 0, 0, 0)))
                    {
                    _hits.Clear();
                        int y = 4;
                        // Names sit low next to their icons: center the text
                        // block in the row (measurement above is already done,
                        // so changing alignment here only moves rendering).
                        fmt.LineAlignment = StringAlignment.Center;
                        for (int i = 0; i < _rows.Count; i++)
                        {
                            var r = _rows[i];
                            int rh = heights[i];
                            int iy = y + (rh - IconSize) / 2;
                            if (r.Icon != null)
                            {
                                _hits.Add(Tuple.Create(new Rectangle(PadX, iy, IconSize, IconSize), r.Key));
                                var fit = FitIcon(r.Icon, PadX, iy, IconSize, IconSize);
                                using (var sil = MakeSilhouette(r.Icon))
                                {
                                    for (int ox = -1; ox <= 1; ox++)
                                        for (int oy = -1; oy <= 1; oy++)
                                            if (ox != 0 || oy != 0)
                                                g.DrawImage(sil, fit.X + ox, fit.Y + oy, fit.Width, fit.Height);
                                }
                                g.DrawImage(r.Icon, fit);
                            }
                            float tx = PadX + IconSize + 8;
                            var layout = new RectangleF(tx, y + 5, maxW, rh - 10);
                            foreach (var o in new[] { new PointF(-1, 0), new PointF(1, 0), new PointF(0, -1), new PointF(0, 1), new PointF(-1, -1), new PointF(1, -1), new PointF(-1, 1), new PointF(1, 1) })
                            {
                                var lr = layout; lr.Offset(o.X, o.Y);
                                g.DrawString(r.Name, _font, shade, lr, fmt);
                            }
                            g.DrawString(r.Name, _font, white, layout, fmt);
                            y += rh;
                        }
                    }
                }
                IntPtr screenDc = GetDC(IntPtr.Zero);
                IntPtr memDc = CreateCompatibleDC(screenDc);
                IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                IntPtr oldBmp = SelectObject(memDc, hBmp);
                try
                {
                    var pos = new Point(Left, Top);
                    var size = new Size(Width, Height);
                    var src = new Point(0, 0);
                    var blend = new BlendFunction { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
                    UpdateLayeredWindow(Handle, screenDc, ref pos, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
                }
                finally
                {
                    SelectObject(memDc, oldBmp);
                    DeleteObject(hBmp);
                    DeleteDC(memDc);
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
            }
        }

        // Black silhouette of an icon (alpha kept, RGB flattened) for outlining.
        static Bitmap MakeSilhouette(Image src)
        {
            var bmp = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var attrs = new System.Drawing.Imaging.ImageAttributes())
            {
                attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(new float[][]
                {
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 1, 0 },
                    new float[] { 0, 0, 0, 0, 1 }
                }));
                g.DrawImage(src, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            }
            return bmp;
        }

        // Contain-fit: icons ship at all aspect ratios/paddings, and
        // stretching them into the cell is what makes the column look
        // ragged. Scale to fit and center instead, so every glyph sits on
        // the same optical box and the name column lines up.
        static internal Rectangle FitIcon(Image icon, int x, int y, int w, int h)
        {
            float s = Math.Min((float)w / icon.Width, (float)h / icon.Height);
            int dw = Math.Max(1, (int)(icon.Width * s));
            int dh = Math.Max(1, (int)(icon.Height * s));
            return new Rectangle(x + (w - dw) / 2, y + (h - dh) / 2, dw, dh);
        }

        public void Follow(Rectangle flashClientRect, Func<Point, Point> toScreen, bool rightSide)
        {
            // Default: beside the dock. When the sheet menu opens the caller passes
            // an x just right of the sheet (see FollowGear) instead of the
            // far right edge, so the list only nudges across a little.
            // Position even while hidden so the first Show lands in place
            // instead of flashing at the default window spot.
            FollowAt(flashClientRect, toScreen, rightSide ? flashClientRect.Right - Width - 12 : flashClientRect.X + 12);
        }

        public void FollowAt(Rectangle flashClientRect, Func<Point, Point> toScreen, int xClient)
        {
            Location = GearTargetPoint(flashClientRect, toScreen, xClient, Width, Height);
        }

        internal static Point GearTargetPoint(Rectangle flashClientRect, Func<Point, Point> toScreen, int xClient, int w, int h)
        {
            int y = flashClientRect.Y + Math.Max(0, (flashClientRect.Height - h) / 2);
            int x = Math.Max(flashClientRect.X, Math.Min(xClient, flashClientRect.Right - w));
            // Keep clear of the bottom-right corner buttons when parked over
            // on the right side.
            if (x + w > flashClientRect.Right - 70)
            {
                int maxY = flashClientRect.Bottom - h - 60;
                if (maxY > flashClientRect.Y + 8 && y > maxY) y = maxY;
            }
            return toScreen(new Point(x, y));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_font != null) _font.Dispose(); } catch { }
                foreach (var kv in _icons) try { if (kv.Value != null) kv.Value.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    // CosmeticsOverlay: CharPage-style "Cosmetics" shirt button floating over
    // the Flash preview's bottom-right corner. Same layered-window trick as
    // the gear HUD (per-pixel alpha; click-through everywhere but the
    // button). char6.swf ships no such asset, so the shirt icon is drawn for
    // this app (ui/icons/cosmetic.png), not ripped game art.
    // State flows one way only: the page pushes {has,on} down via the
    // Cosmetics host call, and button clicks bump a counter the page polls
    // via CosmeticsSeq (C#->JS web messages don't arrive on this machine,
    // so the page can't be notified any other way).
    public class CosmeticsOverlay : Form
    {
        // Bare icon with a black silhouette outline, exactly like the gear
        // list icons (no plate, no label, no dimming).
        const int IconBox = 40;
        const int Pad = 6;

        Image _icon;
        Rectangle _hit = Rectangle.Empty;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        const int ULW_ALPHA = 2;
        const byte AC_SRC_ALPHA = 1;
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_NOACTIVATE = 0x8000000;
        const int WM_NCHITTEST = 0x84;
        const int HTCLIENT = 1;
        const int HTTRANSPARENT = -1;

        public event Action Clicked;

        [DllImport("user32.dll")]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);
        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hDC);

        readonly string _iconFile;

        public CosmeticsOverlay() : this("cosmetic.png") { }

        public CosmeticsOverlay(string iconFile)
        {
            _iconFile = string.IsNullOrEmpty(iconFile) ? "cosmetic.png" : iconFile;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Width = IconBox + Pad * 2;
            Height = IconBox + Pad * 2;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                int lp = m.LParam.ToInt32();
                var p = PointToClient(new Point(lp & 0xFFFF, (lp >> 16) & 0xFFFF));
                m.Result = (IntPtr)(_hit.Contains(p) ? HTCLIENT : HTTRANSPARENT);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (_hit.Contains(e.Location))
            {
                var cb = Clicked;
                if (cb != null) cb();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = _hit.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        public void SetState(string appDir, bool has, bool on)
        {
            if (_icon == null)
            {
                try
                {
                    string p = Path.Combine(appDir, "ui", "icons", _iconFile);
                    if (File.Exists(p)) _icon = Image.FromFile(p);
                    else AppForm.Dbg("Overlay icon missing: " + p);
                }
                catch (Exception ex) { AppForm.Dbg("Cosmetics icon load EX: " + ex.Message); }
            }
            Render();
        }

        static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void Render()
        {
            var dst = new Rectangle(Pad, Pad, IconBox, IconBox);
            _hit = dst;
            using (var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    if (_icon != null)
                    {
                        // Black silhouette outline, same as the gear icons.
                        // Contain-fit like the gear list so the non-square
                        // sprite isn't stretched.
                        var fit = HudOverlay.FitIcon(_icon, dst.X, dst.Y, dst.Width, dst.Height);
                        using (var sil = MakeSilhouette(_icon))
                        {
                            for (int ox = -1; ox <= 1; ox++)
                                for (int oy = -1; oy <= 1; oy++)
                                    if (ox != 0 || oy != 0)
                                        g.DrawImage(sil, fit.X + ox, fit.Y + oy, fit.Width, fit.Height);
                        }
                        g.DrawImage(_icon, fit);
                    }
                }
                IntPtr screenDc = GetDC(IntPtr.Zero);
                IntPtr memDc = CreateCompatibleDC(screenDc);
                IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                IntPtr oldBmp = SelectObject(memDc, hBmp);
                try
                {
                    var pos = new Point(Left, Top);
                    var size = new Size(Width, Height);
                    var src = new Point(0, 0);
                    var blend = new BlendFunction { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
                    UpdateLayeredWindow(Handle, screenDc, ref pos, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
                }
                finally
                {
                    SelectObject(memDc, oldBmp);
                    DeleteObject(hBmp);
                    DeleteDC(memDc);
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        // Black silhouette of the icon (alpha kept, RGB flattened), same
        // outlining helper as the gear list.
        static Bitmap MakeSilhouette(Image src)
        {
            var bmp = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var attrs = new System.Drawing.Imaging.ImageAttributes())
            {
                attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(new float[][]
                {
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 1, 0 },
                    new float[] { 0, 0, 0, 0, 1 }
                }));
                g.DrawImage(src, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            }
            return bmp;
        }

        public void Follow(Rectangle flashClientRect, Func<Point, Point> toScreen)
        {
            if (!Visible) return;
            var p = toScreen(new Point(flashClientRect.Right - Width - 4, flashClientRect.Bottom - Height - 4));
            Location = p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_icon != null) _icon.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    // EdgeOverlay: the anti-aliased rims of the rounded HTML panels (dock,
    // sheet). Those panels show through 1-bit window-region holes in the
    // Flash surface, which can only stair-step a curve. FlashCore cuts each
    // hole to the fully covered pixels only; this click-through layered
    // window paints the partly covered ones over the Flash scene with real
    // per-pixel alpha, colored from the page's own rendering so gradients,
    // borders and the theme accent all match.
    public class EdgeOverlay : Form
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        const int ULW_ALPHA = 2;
        const byte AC_SRC_ALPHA = 1;
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_NOACTIVATE = 0x8000000;
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WM_NCHITTEST = 0x84;
        const int HTTRANSPARENT = -1;
        const int Samples = 8; // per axis: 64 coverage levels per edge pixel

        [DllImport("user32.dll")]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);
        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hDC);

        // Top-left of the painted area in the owner's client coordinates,
        // so a window move only has to re-place it.
        public Point ClientOrigin { get; private set; }

        public EdgeOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
            base.WndProc(ref m);
        }

        // panels: rounded rects in the owner's client coordinates (= page
        // pixels). page: a capture of the page; scale maps page pixels to it.
        // Returns false when there is nothing to paint.
        public bool Render(List<Tuple<Rectangle, int>> panels, Bitmap page, double scale, Func<Point, Point> toScreen)
        {
            Rectangle bounds = Rectangle.Empty;
            foreach (var p in panels)
                if (p.Item2 > 0) bounds = bounds.IsEmpty ? p.Item1 : Rectangle.Union(bounds, p.Item1);
            if (bounds.Width <= 0 || bounds.Height <= 0) return false;

            var px = new int[bounds.Width * bounds.Height];
            foreach (var p in panels)
            {
                var r = p.Item1;
                int rad = FlashCore.CornerRadius(r, p.Item2);
                if (rad <= 0) continue;
                for (int j = 0; j < rad; j++)
                {
                    int inset = FlashCore.CornerInset(rad, j);
                    for (int i = 0; i < inset; i++)
                    {
                        int cov = Coverage(rad, i, j);
                        if (cov == 0) continue;
                        // Color: the outermost fully covered page pixel on
                        // the way in along the radius (the panel's border).
                        Point s = InnerPixel(rad, i, j);
                        for (int corner = 0; corner < 4; corner++)
                        {
                            bool right = corner == 1 || corner == 2, bottom = corner >= 2;
                            int x = right ? r.Right - 1 - i : r.X + i;
                            int y = bottom ? r.Bottom - 1 - j : r.Y + j;
                            if (InsideOther(panels, p, x, y)) continue;
                            int sx = right ? r.Right - 1 - s.X : r.X + s.X;
                            int sy = bottom ? r.Bottom - 1 - s.Y : r.Y + s.Y;
                            Color c = Sample(page, sx, sy, scale);
                            int a = cov * 255 / (Samples * Samples);
                            px[(y - bounds.Y) * bounds.Width + (x - bounds.X)] = (a << 24) | (c.R << 16) | (c.G << 8) | c.B;
                        }
                    }
                }
            }

            ClientOrigin = bounds.Location;
            using (var bmp = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                try
                {
                    for (int row = 0; row < bmp.Height; row++)
                        Marshal.Copy(px, row * bmp.Width, data.Scan0 + row * data.Stride, bmp.Width);
                }
                finally { bmp.UnlockBits(data); }
                Bounds = new Rectangle(toScreen(bounds.Location), bounds.Size);
                IntPtr screenDc = GetDC(IntPtr.Zero);
                IntPtr memDc = CreateCompatibleDC(screenDc);
                IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                IntPtr oldBmp = SelectObject(memDc, hBmp);
                try
                {
                    var pos = new Point(Left, Top);
                    var size = new Size(Width, Height);
                    var src = new Point(0, 0);
                    var blend = new BlendFunction { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
                    UpdateLayeredWindow(Handle, screenDc, ref pos, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
                }
                finally
                {
                    SelectObject(memDc, oldBmp);
                    DeleteObject(hBmp);
                    DeleteDC(memDc);
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
            return true;
        }

        // Supersampled share of corner pixel (i, j) inside the arc, counted
        // in the top-left corner (arc center at rad, rad); the other three
        // corners are mirror images.
        static int Coverage(int rad, int i, int j)
        {
            int n = 0;
            double r2 = (double)rad * rad;
            for (int sy = 0; sy < Samples; sy++)
            {
                double dy = rad - (j + (sy + 0.5) / Samples);
                for (int sx = 0; sx < Samples; sx++)
                {
                    double dx = rad - (i + (sx + 0.5) / Samples);
                    if (dx * dx + dy * dy <= r2) n++;
                }
            }
            return n;
        }

        static Point InnerPixel(int rad, int i, int j)
        {
            double dx = i + 0.5 - rad, dy = j + 0.5 - rad;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) return new Point(rad, rad);
            dx /= len; dy /= len;
            for (double d = rad - 0.5; d > 0; d -= 0.5)
            {
                int x = (int)Math.Floor(rad + dx * d), y = (int)Math.Floor(rad + dy * d);
                if (x >= rad || y >= rad || x >= FlashCore.CornerInset(rad, y)) return new Point(x, y);
            }
            return new Point(rad, rad);
        }

        // Where two reported rects overlap (a resting and a live rect of the
        // same panel), the other one's hole already shows the page there.
        static bool InsideOther(List<Tuple<Rectangle, int>> panels, Tuple<Rectangle, int> self, int x, int y)
        {
            foreach (var p in panels)
                if (!ReferenceEquals(p, self) && p.Item1.Contains(x, y)) return true;
            return false;
        }

        static Color Sample(Bitmap page, int x, int y, double scale)
        {
            int sx = Math.Max(0, Math.Min(page.Width - 1, (int)(x * scale)));
            int sy = Math.Max(0, Math.Min(page.Height - 1, (int)(y * scale)));
            return page.GetPixel(sx, sy);
        }
    }

    // LoaderOverlay: minimal centered typing line for character names. The
    // middle of the preview is Flash territory so this is native, not HTML:
    // a borderless dark line with an accent underline. Enter commits the
    // name for the page to pick up (LoadNameRequest poll); Esc cancels.
    public class LoaderOverlay : Form
    {
        readonly TextBox _box;
        const string CueText = "character name...";

        // Native placeholder (shown even while focused): the old fake one was
        // real text that the first focus erased, so the line came up blank.
        const int EM_SETCUEBANNER = 0x1501;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public event Action<string> Committed;
        public event Action Cancelled;

        const int WS_EX_TOOLWINDOW = 0x80;

        public LoaderOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // Transparent body (TransparencyKey): only the text strip and
            // the accent underline show, the preview shows through the rest.
            BackColor = Color.FromArgb(255, 1, 2, 3);
            TransparencyKey = BackColor;
            Width = 360;
            Height = 46;
            _box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                // Same as the transparency key: the edit field itself goes
                // invisible too, leaving only the typed glyphs floating.
                BackColor = BackColor,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 14f),
                Location = new Point(16, 9),
                Width = Width - 32,
                // The line is resized to fit the preview (CenterLoader).
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
            };
            _box.HandleCreated += (o, e) => SendMessage(_box.Handle, EM_SETCUEBANNER, (IntPtr)1, CueText);
            _box.KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); }
                else if (e.KeyCode == Keys.Escape) { Cancel(); }
            };
            Controls.Add(_box);
            var line = new Panel { BackColor = Color.White, Height = 2, Dock = DockStyle.Bottom };
            Controls.Add(line);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        void Commit()
        {
            string t = _box.Text.Trim();
            if (t.Length == 0) return;
            var cb = Committed;
            if (cb != null) cb(t);
        }

        void Cancel()
        {
            var cb = Cancelled;
            if (cb != null) cb();
        }

        // Owned by the main window so it stays above it: unowned, any click
        // on the app brought the main window forward and buried the line.
        public void Present(Form owner)
        {
            try { if (!Visible) Show(owner); } catch { try { Show(); } catch { } }
            Refocus(true);
        }

        public void Refocus(bool selectAll)
        {
            try
            {
                if (!Visible || _box.Focused) return;
                _box.Focus();
                if (selectAll) _box.SelectAll();
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_box != null) { _box.Font.Dispose(); _box.Dispose(); } } catch { }
            }
            base.Dispose(disposing);
        }
    }

    public class AppForm : Form
    {
        WebView2 _web;
        CoreWebView2 _core;
        FlashCore _flash;
        HudOverlay _gear;
        CosmeticsOverlay _cos;
        LoaderOverlay _loader;
        CosmeticsOverlay _mag;
        EdgeOverlay _edges;
        bool _edgesBusy, _edgesAgain;
        readonly object _loadLock = new object();
        string _pendingLoad;
        volatile int _cosToggleSeq;
        bool _cosHas, _cosOn;                 // last state pushed by the page
        bool _cosPushedHas, _cosPushedOn;     // state last rendered (kept for diagnostics)
        System.Windows.Forms.Timer _watchdog; // re-assert overlays: layered windows can miss a show/position (minimize, DWM transitions)
        // Gear glide state: ease-out slide between parking spots on menu
        // toggles, plus slide in from / out to the left screen edge on
        // show/hide. Drags/resizes snap (tracked below) so the rail never
        // lags behind the window. Hide() lands only after an out-glide
        // (see _gearAnimHidePending), so a watchdog FollowGear mid-glide
        // can't yank the rail back into view.
        System.Windows.Forms.Timer _gearAnimTimer;
        Point _gearAnimFrom, _gearAnimTo;
        int _gearAnimStart;
        bool _gearAnimHidePending;   // current/next glide ends in Hide()
        bool _gearAnimHideWas;      // hide flag of the running glide
        const int GearAnimMs = 170;
        Point _lastFormGeomLoc;
        Size _lastFormGeomSize;
        // The stripped frame (WM_NCCALCSIZE -> 0) corrupts restore
        // bookkeeping: every maximized -> normal transition lands one
        // invisible frame + caption bigger (+16x39), cumulatively. Remember
        // the last user-driven normal bounds and snap back once the
        // transition settles (verified with a harness: single correction,
        // user resizes respected).
        Rectangle _normalBounds;
        bool _normalBoundsValid;
        bool _restoreFixPending;
        EdgeResizeFilter _edgeFilter; // edge resizing over the Flash surface (see below)
        // Resize-cursor polling. The Flash ActiveX calls SetCursor itself when
        // the mouse crosses a hotspot, so a one-shot WM_SETCURSOR handler is
        // overwritten on the very next move - which is why the page's CSS
        // cursor worked on the top edge and nothing showed on the other three.
        // Re-asserting from a short timer is the approach that survives it.
        System.Windows.Forms.Timer _cursorTimer;
        Cursor _lastEdgeCursor;
        volatile bool _resizing; // a native sizing loop is running
        bool _holesDirty;         // holes were deferred during a drag
        bool _flashReady;
        bool _pageReady;
        bool _hostReadySent;

        static readonly bool AutoTest = Environment.GetCommandLineArgs().Length > 1 && Array.IndexOf(Environment.GetCommandLineArgs(), "--autotest") >= 0;

        // --headless: offscreen window, no overlays, no modal dialogs; the
        // HeadlessRunner drives the page and the player through these.
        public static bool Headless;
        internal FlashCore Flash { get { return _flash; } }
        internal bool ReadyForTests { get { return _pageReady && _flashReady && _core != null; } }
        internal Task<string> EvalAsync(string js) { return _core.ExecuteScriptAsync(js); }
        // Headless: the page as Chromium painted it (panels; the Flash
        // surface is a separate window and shows as the page behind it).
        internal async Task SavePagePng(string path)
        {
            using (var fs = File.Create(path)) await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        }
        protected override bool ShowWithoutActivation { get { return Headless; } }

        static readonly Color UiBg = Color.FromArgb(0x0A, 0x0C, 0x14);

        public AppForm()
        {
            Text = "Manikin";
            // Real frame, custom-drawn over: the OS supplies minimize/maximize
            // animations, Aero snap, taskbar thumbnails and shadow
            // (Chrome-style). WM_NCCALCSIZE below strips the non-client area
            // so only our dark titlebar shows.
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(800, 450);
            MinimumSize = new Size(560, 400);
            BackColor = UiBg;
            if (Headless)
            {
                // Real window (Flash and WebView2 both need one), parked past
                // the right edge of every monitor and kept off the taskbar.
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                var vs = SystemInformation.VirtualScreen;
                Location = new Point(vs.Right + 200, vs.Top);
                ClientSize = new Size(1280, 760);
            }

            _web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);

            Load += OnLoad;
            FormClosed += OnFormClosed;
            Shown += (o, e) =>
            {
                // First-shown reseat: owned layered windows shown during
                // startup can end up under the Flash surface until something
                // (minimize/restore, alt-tab) forces recomposition. Reseat
                // them once the owner is really on screen.
                // (The loader line is NOT presented here: the booting Flash
                // ActiveX grabs focus seconds after start, killing typing.
                // It presents from OnFlashReady instead, once boot settles.)
                try
                {
                    // Show the rail straight from the form rect: don't wait
                    // for the page's first reportRect (WebView init takes
                    // seconds on a cold start). Snug left (dock starts
                    // closed); FollowGear seats it exactly once Flash
                    // reports in.
                    if (!IsDisposed && _gear != null && _gear.HasRows && !_gear.Visible)
                    {
                        int gy = 40 + Math.Max(0, (ClientSize.Height - 40 - _gear.Height) / 2);
                        _gear.Location = PointToScreen(new Point(12, gy));
                        try { _gear.Show(this); } catch { try { _gear.Show(); } catch { } }
                        // Owned layered windows shown during startup can end up
                        // under the Flash surface and never re-seat on their
                        // own; the watchdog now recovers that, but a first-shot
                        // reseat here makes startup deterministic instead of
                        // waiting up to 2 s for it.
                        try { _gear.BringToFront(); } catch { }
                    }
                    if (!IsDisposed && _flash != null && _flash.Visible) FollowGear();
                }
                catch { }
                FocusPage();
            };
        }

        // Normal-app close: the gear overlay is a separate top-level window,
        // so close it explicitly. (Owned windows usually follow the owner,
        // but the fallback Show() path has no owner and would linger.)
        void OnFormClosed(object s, FormClosedEventArgs e)
        {
            try { if (_watchdog != null) { _watchdog.Stop(); _watchdog.Dispose(); } } catch { }
            try { if (_gearAnimTimer != null) { _gearAnimTimer.Stop(); _gearAnimTimer.Dispose(); _gearAnimTimer = null; } } catch { }
            try { if (_edgeFilter != null) { Application.RemoveMessageFilter(_edgeFilter); _edgeFilter = null; } } catch { }
            try { if (_cursorTimer != null) { _cursorTimer.Stop(); _cursorTimer.Dispose(); _cursorTimer = null; } } catch { }
            try { if (_lastEdgeCursor != null) { Cursor.Current = Cursors.Default; _lastEdgeCursor = null; } } catch { }
            try { if (_gear != null) _gear.Close(); } catch { }
            try { if (_loader != null) _loader.Close(); } catch { }
            try { if (_mag != null) _mag.Close(); } catch { }
            try { if (_cos != null) _cos.Close(); } catch { }
            try { if (_edges != null) _edges.Close(); } catch { }
            try { if (_flash != null) _flash.Dispose(); } catch { }
        }

        // Classic drop shadow for the borderless window (Chrome-style).
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;
                return cp;
            }
        }

        async void OnLoad(object s, EventArgs e)
        {
            // Each subsystem initializes independently: a missing WebView2
            // runtime or an unregistered Flash ActiveX degrades to a usable
            // window with an explanation, never a silent instant exit.
            // (async-void exceptions otherwise kill the process with no UI.)
            try { ApplyWindowRounding(); } catch { }
            try { _edgeFilter = new EdgeResizeFilter(this); Application.AddMessageFilter(_edgeFilter); } catch { }
            // Re-assert the resize cursor over the edges (see UpdateEdgeCursor).
            try
            {
                _cursorTimer = new System.Windows.Forms.Timer { Interval = 40 };
                _cursorTimer.Tick += (o2, e2) => UpdateEdgeCursor();
                _cursorTimer.Start();
            }
            catch { _cursorTimer = null; }
            // User-driven geometry only (modal sizing loop end + moves):
            // transition resizes (maximize/restore) must never overwrite it.
            _normalBounds = Bounds;
            _normalBoundsValid = true;
            ResizeEnd += (oo, ee) => { if (WindowState == FormWindowState.Normal) { _normalBounds = Bounds; _normalBoundsValid = true; } };
            Move += (oo, ee) => { if (WindowState == FormWindowState.Normal) { _normalBounds = Bounds; _normalBoundsValid = true; } };
            // Landing in Normal with anything but the remembered bounds means
            // the stripped frame leaked back in: correct once, deferred past
            // the transition (a synchronous set storms against it). Covers
            // every path here (titlebar button, double-click, taskbar, snap).
            SizeChanged += (oo, ee) =>
            {
                if (WindowState == FormWindowState.Normal && _normalBoundsValid && !_restoreFixPending && Bounds != _normalBounds)
                {
                    _restoreFixPending = true;
                    BeginInvoke((Action)(() =>
                    {
                        _restoreFixPending = false;
                        if (IsDisposed || WindowState != FormWindowState.Normal || !_normalBoundsValid) return;
                        if (Bounds != _normalBounds) Bounds = _normalBounds;
                    }));
                }
            };

            try
            {
                // Keep the browser profile in %LOCALAPPDATA%: the default
                // (next to the exe) fails to start from a read-only install
                // folder such as Program Files.
                string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Manikin", Headless ? "WebView2-headless" : "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, profile);
                await _web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                Dbg("WebView2 init failed: " + ex);
                Alert(
                    "Manikin needs the WebView2 Runtime to show its control panel.\n\n" +
                    "Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and relaunch Manikin.\n\n" +
                    "Technical detail: " + ex.Message, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _core = _web.CoreWebView2;
                _core.Settings.IsStatusBarEnabled = false;
                _core.Settings.IsZoomControlEnabled = false;
                _core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                _core.AddHostObjectToScript("fbhost", new FbHost(this));
                _core.DOMContentLoaded += (o, ev) =>
                {
                    _pageReady = true;
                    MaybeSendHostReady();
                };
                _core.NavigationCompleted += OnNavigationCompleted;
            }
            catch (Exception ex)
            {
                Dbg("WebView2 setup failed: " + ex);
                Alert("WebView2 setup failed: " + ex.Message, MessageBoxIcon.Warning);
                return;
            }

            // FlashCore is created AFTER the WebView2 channel is up, so the
            // legacy Flash ActiveX can't corrupt WebView2 init (seen before:
            // C#->JS web messages silently never arriving). If Flash isn't
            // registered the control panel still works; only the avatar
            // preview is unavailable.
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                _flash = new FlashCore(baseDir);
                _flash.Bounds = new Rectangle(0, 0, 0, 0);
                _flash.Visible = false;
                _flash.Ready += OnFlashReady;
                Controls.Add(_flash);
                _flash.BringToFront();
                _web.BringToFront();
                _flash.BringToFront();
            }
            catch (Exception ex)
            {
                Dbg("Flash init failed (continuing without avatar preview): " + ex);
                _flash = null;
                Alert(
                    "Flash Player ActiveX is not registered on this machine, so the avatar preview is unavailable.\n" +
                    "The control panel still works.\n\nTechnical detail: " + ex.Message, MessageBoxIcon.Warning);
            }

            // Headless runs have no screen presence: skip the floating
            // layered windows (gear list, buttons, typing line) entirely.
            if (!Headless) CreateOverlays();

            // FlashCore.Ready usually fires inside its constructor, before
            // the subscription above, which used to leave _flashReady false
            // forever: no hostReady push and no startup typing line. Catch
            // up now that the overlays exist.
            if (_flash != null && _flash.Started) OnFlashReady();

            try
            {
                string html = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "index.html");
                if (!File.Exists(html))
                {
                    Dbg("UI missing: " + html);
                    Alert("Manikin UI files are missing:\n" + html +
                        "\n\nReinstall Manikin with its ui\\ folder next to the exe.", MessageBoxIcon.Error);
                    return;
                }
                _core.Navigate("file:///" + html.Replace('\\', '/'));
                FocusPage();
            }
            catch (Exception ex)
            {
                Dbg("Navigation failed: " + ex);
                Alert("Manikin could not open its control panel: " + ex.Message, MessageBoxIcon.Error);
            }
        }

        void Alert(string text, MessageBoxIcon icon)
        {
            if (Headless) { Dbg("ALERT: " + text); return; }
            MessageBox.Show(this, text, "Manikin", MessageBoxButtons.OK, icon);
        }

        void CreateOverlays()
        {
            try
            {
                _gear = new HudOverlay();
                _gear.IconClicked += OnGearIcon;
                _gear.FileDropped += (slot, path) => DoDropFile(slot, path);
                // Slot icons visible from the start (dimmed, no names): they
                // double as drop targets for SWF files. Stay hidden until
                // FollowGear positions over the Flash preview: showing now
                // flashes the icons at the default (0,0) window spot for a
                // split second on startup.
                _gear.SetRows(AppDomain.CurrentDomain.BaseDirectory, DefaultGearRows());
                LocationChanged += (o, ev) => FollowGear();
                SizeChanged += (o, ev) => FollowGear();
            }
            catch (Exception ex)
            {
                Dbg("Gear overlay init failed (continuing without it): " + ex);
                _gear = null;
            }

            try
            {
                // Shirt button, dimmed until the page reports a cosmetic set.
                _cos = new CosmeticsOverlay();
                _cos.Clicked += OnCosmeticsClicked;
                _cos.SetState(AppDomain.CurrentDomain.BaseDirectory, false, false);
            }
            catch (Exception ex)
            {
                Dbg("Cosmetics button init failed (continuing without it): " + ex);
                _cos = null;
            }

            try
            {
                // Center typing line (empty-state loader) + magnifier button
                // that re-summons it.
                _loader = new LoaderOverlay();
                _loader.Committed += OnLoaderCommit;
                _loader.Cancelled += () => { try { if (_loader != null) _loader.Hide(); } catch { } };
                _mag = new CosmeticsOverlay("search.png");
                _mag.Clicked += () => ToggleLoader();
                _mag.SetState(AppDomain.CurrentDomain.BaseDirectory, true, false);
            }
            catch (Exception ex)
            {
                Dbg("Loader init failed (continuing without it): " + ex);
                _loader = null;
                _mag = null;
            }

            try
            {
                _edges = new EdgeOverlay();
                LocationChanged += (o, ev) => FollowEdges();
            }
            catch (Exception ex)
            {
                Dbg("Edge overlay init failed (continuing without it): " + ex);
                _edges = null;
            }

            try
            {
                _watchdog = new System.Windows.Forms.Timer { Interval = 2000 };
                _watchdog.Tick += (o, ev) =>
                {
                    try
                    {
                        if (!IsDisposed && _flash != null && _flash.Visible)
                        {
                            FollowGear();
                            // The rail can also be dropped behind the Flash
                            // surface (a windowed ActiveX, airspace
                            // ownership). Visible-but-not-painted is the
                            // symptom, so reseat it every tick; a hide is
                            // respected because FollowGear only re-shows
                            // when _overlaysHidden is clear.
                            try
                            {
                                if (_gear != null && _gear.HasRows && !_gear.Visible && !_overlaysHidden)
                                    GearSlideIn(HudOverlay.GearTargetPoint(_flash.Bounds, p => PointToScreen(p), GearRestX(_flash.Bounds), _gear.Width, _gear.Height));
                            }
                            catch { }
                            // Z-order insurance: if anything (Flash surface,
                            // DWM hiccup) covers the button overlays, reseat
                            // them. NOACTIVATE windows can't steal focus.
                            // SWP_NOACTIVATE: BringToFront activated the overlay,
                            // which closed any open page popup (the dye color
                            // picker vanished within ~2 s, on this tick).
                            RaiseNoActivate(_cos);
                            RaiseNoActivate(_mag);
                            RaiseNoActivate(_edges);
                            // Typing-line guard: pull focus back when it was
                            // yanked elsewhere in our own windows (Flash boot
                            // does this). Never from another app, never from
                            // the page (drawer inputs need their focus).
                            try
                            {
                                if (_loader != null && _loader.Visible && ForegroundIsOurs() && !FocusInPage())
                                    _loader.Refocus(false);
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex) { Dbg("watchdog EX: " + ex.Message); }
                };
                _watchdog.Start();
            }
            catch (Exception ex) { Dbg("watchdog init EX: " + ex.Message); }
        }

        void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess) { Dbg("Navigation failed"); return; }
            if (AutoTest) _ = RunAutoTestAsync();
        }

        async Task RunAutoTestAsync()
        {
            try
            {
                for (int i = 0; i < 60; i++) { if (_pageReady && _flashReady) break; await Task.Delay(200); }
                await Task.Delay(800);
                Dbg("AUTOTEST: invoke loadCharacter('alina')");
                await _core.ExecuteScriptAsync("document.getElementById('charName').value='alina';loadCharacter();");
            }
            catch (Exception ex) { Dbg("AUTOTEST EX: " + ex.Message); }
        }

        void OnFlashReady()
        {
            if (InvokeRequired) { BeginInvoke((Action)OnFlashReady); return; }
            Dbg("FlashCore ready");
            _flashReady = true;
            MaybeSendHostReady();
            FocusPage();
            // No auto-present: the typing line only appears when summoned
            // with the magnifier button.
        }

        void MaybeSendHostReady()
        {
            if (_hostReadySent || !_pageReady || !_flashReady || _core == null) return;
            _hostReadySent = true;
            Dbg("-> hostReady");
            _core.PostWebMessageAsJson("{\"ev\":\"hostReady\"}");
        }

        static readonly string DebugLog = Path.Combine(Path.GetTempPath(), "fb-debug.log");

        // One writer at a time: the UI thread and a take's encode thread both log,
        // and a File.AppendAllText that collides throws "file in use", which the
        // catch below would swallow - lines disappeared exactly when a take was
        // being encoded, which is when they mattered.
        static readonly object _dbgLock = new object();

        public static void Dbg(string line)
        {
            try
            {
                lock (_dbgLock)
                    File.AppendAllText(DebugLog, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
            }
            catch { }
        }

        // Many lines in one write: a bulk insert logging per row would crawl
        // otherwise (one file open per line).
        public static void DbgMany(IEnumerable<string> lines)
        {
            if (lines == null) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                string stamp = DateTime.Now.ToString("HH:mm:ss.fff");
                foreach (var line in lines) sb.Append(stamp).Append(' ').Append(line).Append(Environment.NewLine);
                if (sb.Length > 0) { lock (_dbgLock) File.AppendAllText(DebugLog, sb.ToString()); }
            }
            catch { }
        }

        public static string GetLog()
        {
            try
            {
                if (!File.Exists(DebugLog)) return "";
                using (var fs = new FileStream(DebugLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    string all = sr.ReadToEnd();
                    if (all.Length > 200000) all = all.Substring(all.Length - 200000);
                    return all;
                }
            }
            catch { return ""; }
        }

        public static void ClearLog()
        {
            try { File.WriteAllText(DebugLog, ""); } catch { }
        }

        void PostEvent(JToken obj)
        {
            try
            {
                if (_core == null) return;
                Dbg("-> ev " + obj["ev"]);
                _core.PostWebMessageAsJson(obj.ToString(Formatting.None));
            }
            catch (Exception ex) { Dbg("PostEvent: " + ex); }
        }

        // ---- preview rect -> position the Flash sibling over the webview ----
        void PositionFlash(JToken args)
        {
            if (_flash == null) return;
            int x = args.Value<int>("x");
            int y = args.Value<int>("y");
            int w = args.Value<int>("w");
            int h = args.Value<int>("h");
            if (w <= 0 || h <= 0) { _flash.Visible = false; if (_gear != null && _gear.Visible) _gear.Hide(); if (_cos != null && _cos.Visible) _cos.Hide(); return; }
            _flash.SetBounds(x, y, w, h);
            _flash.Visible = true;
            _flash.BringToFront();
            ApplyOverlayHoles();   // <-- add this: reapply hole against the new true offset
            FollowGear();
        }

        // Rail parking, from the live holes the page reports (narrow = the
        // dock pill, wide = the sheet): the right edge of whichever panel is
        // showing, + 12. Both edges are measured, not hardcoded, so a
        // restyled dock or sheet tracks automatically. _lastDockHidden /
        // _lastSheetHidden come from the page: a hidden panel may still report
        // its rects (a closing hole must stay cut for the duration of the
        // animation), so width alone cannot be trusted to mean "visible".
        int GearRestX(Rectangle flashClient)
        {
            bool dockVisible = false, sheetVisible = false;
            int sheetRight = 0, dockRight = 0;
            foreach (var h in _lastOverlayRectsFormCoords)
            {
                if (h.Item1.Width > 100) { sheetVisible = true; if (h.Item1.Right > sheetRight) sheetRight = h.Item1.Right; }
                else { dockVisible = true; if (h.Item1.Right > dockRight) dockRight = h.Item1.Right; }
            }
            dockVisible = dockVisible && !_lastDockHidden;
            sheetVisible = sheetVisible && !_lastSheetHidden;
            if (sheetVisible && sheetRight > flashClient.X) return sheetRight + 12;
            if (dockVisible && dockRight > flashClient.X) return dockRight + 12;
            return flashClient.X + 12;
        }

        // Keep the resize cursor showing over the window edges.
        //
        // The page's transparent grips carry a CSS resize cursor, but the page
        // only sees the mouse where the Flash surface is not on top of it, so
        // the left, right and bottom edges got a plain arrow even though
        // dragging there resized fine. Handling WM_SETCURSOR is not enough:
        // the ActiveX calls SetCursor directly whenever the mouse crosses a
        // movie hotspot, so a one-shot handler loses the very next move. A
        // short poll that only re-asserts while the pointer is actually in an
        // edge band survives that, and resets to Default the moment it leaves
        // so ordinary cursors (text caret, hand) come back.
        void UpdateEdgeCursor()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                // Never fight another app for the cursor, and a maximized or
                // minimized window has no edges to grab. Also stay out of the
                // way while a sizing loop is running - poking the cursor
                // mid-drag makes the drag stutter.
                if (WindowState != FormWindowState.Normal || !ForegroundIsOurs() || _resizing)
                {
                    if (_lastEdgeCursor != null) { Cursor.Current = Cursors.Default; _lastEdgeCursor = null; }
                    return;
                }
                Point c = PointToClient(Cursor.Position);
                var r = ClientRectangle;
                const int tol = FlashCore.EdgeCursorTol;
                bool l = c.X < tol, rt = c.X >= r.Width - tol;
                bool t = c.Y < tol, b = c.Y >= r.Height - tol;
                Cursor want = null;
                if (l || rt || t || b)
                {
                    bool h = l || rt, v = t || b;
                    want = (h && v) ? ((l && t) || (rt && b) ? Cursors.SizeNWSE : Cursors.SizeNESW)
                         : h ? Cursors.SizeWE : Cursors.SizeNS;
                }
                if (want == null)
                {
                    if (_lastEdgeCursor != null) { Cursor.Current = Cursors.Default; _lastEdgeCursor = null; }
                    return;
                }
                // Assign EVERY tick, not only on change. The ActiveX overwrites
                // the cursor on every mouse move over a hotspot, so a
                // "set once, remember it" timer thinks it is still in place
                // and the arrow vanishes a frame later. Re-setting the same
                // handle is a no-op, so this cannot flicker.
                Cursor.Current = want;
                _lastEdgeCursor = want;
            }
            catch { }
        }

        void StopGearAnim()
        {
            try { if (_gearAnimTimer != null) _gearAnimTimer.Stop(); } catch { }
            // A glide that never reached its Hide() (interrupted by a snap, a
            // follow, or a close) must not leave the flag latched: it gates
            // every later re-show, so a stale true parks the rail off-screen
            // for the rest of the session.
            if (_gearAnimHidePending && _gear != null && !_gear.Visible)
            {
                _gearAnimHidePending = false;
                try { _gear.Hide(); } catch { }
            }
        }

        void AnimateGearTo(Point screenTarget, bool hideOnArrive = false)
        {
            if (_gear == null || _gear.IsDisposed) { StopGearAnim(); return; }
            if (!_gear.Visible) { try { _gear.Location = screenTarget; } catch { } StopGearAnim(); return; }
            int dx = screenTarget.X - _gear.Location.X, dy = screenTarget.Y - _gear.Location.Y;
            if (dx * dx + dy * dy < 16)
            {
                if (hideOnArrive) { _gearAnimHidePending = false; try { _gear.Hide(); } catch { } }
                StopGearAnim();
                return;
            }
            _gearAnimHidePending = hideOnArrive;
            // Same destination already gliding: let it ride, don't restart.
            if (_gearAnimTimer != null && _gearAnimTimer.Enabled && _gearAnimTo == screenTarget && _gearAnimHideWas == hideOnArrive) return;
            _gearAnimFrom = _gear.Location;
            _gearAnimTo = screenTarget;
            _gearAnimHideWas = hideOnArrive;
            _gearAnimStart = Environment.TickCount;
            if (_gearAnimTimer == null)
            {
                _gearAnimTimer = new System.Windows.Forms.Timer { Interval = 15 };
                _gearAnimTimer.Tick += (o, e) =>
                {
                    try
                    {
                        if (_gear == null || _gear.IsDisposed) { StopGearAnim(); return; }
                        double t = (Environment.TickCount - _gearAnimStart) / (double)GearAnimMs;
                        if (t >= 1)
                        {
                            _gear.Location = _gearAnimTo;
                            if (_gearAnimHideWas) { _gearAnimHidePending = false; try { _gear.Hide(); } catch { } }
                            StopGearAnim();
                            return;
                        }
                        double k = 1 - Math.Pow(1 - t, 3); // ease-out cubic
                        _gear.Location = new Point(
                            _gearAnimFrom.X + (int)Math.Round((_gearAnimTo.X - _gearAnimFrom.X) * k),
                            _gearAnimFrom.Y + (int)Math.Round((_gearAnimTo.Y - _gearAnimFrom.Y) * k));
                    }
                    catch { StopGearAnim(); }
                };
            }
            _gearAnimTimer.Start();
        }

        // Slide the rail in from the left screen edge (its parking side).
        // Used on every hidden -> shown transition; cancels a pending
        // slide-out. The window is parked fully off-screen first so the
        // glide starts from the edge, not from its old spot.
        void GearSlideIn(Point rest)
        {
            if (_gear == null || _gear.IsDisposed) return;
            _gearAnimHidePending = false;
            if (!_gear.Visible)
            {
                try { _gear.Location = new Point(rest.X - _gear.Width - 8, rest.Y); } catch { }
                // Show() has to be called whether or not we already own the
                // window. Guarding the call on "not yet owned" skipped it
                // entirely once the owner was set - no exception, so the catch
                // never fired either - and the rail could never come back after
                // a hide. Passing the owner only helps the first time.
                try { if (!IsDisposed && _gear.Owner != this) _gear.Show(this); else _gear.Show(); }
                catch { try { _gear.Show(); } catch { } }
            }
            AnimateGearTo(rest);
        }

        // Slide the rail out to the left screen edge; Hide() lands only once
        // the glide arrives (AnimateGearTo with hideOnArrive).
        void GearSlideOut()
        {
            if (_gear == null || _gear.IsDisposed || !_gear.Visible) return;
            var target = new Point(_gear.Location.X - _gear.Width - 8, _gear.Location.Y);
            AnimateGearTo(target, true);
        }

        void FollowGear()
        {
            if (_flash == null || !_flash.Visible || _overlaysHidden) return;
            var b = _flash.Bounds;
            if (_gear != null)
            {
                // Snug left edge when the dock is closed; beside the dock
                // when it is open (never on top of it); nudged just right
                // of the sheet's edge while the menu is open. Derived fresh
                // from the reported holes every time, so any dropped signal
                // self-heals on the next follow. Window drags/resizes snap;
                // menu toggles glide (AnimateGearTo).
                Point target = HudOverlay.GearTargetPoint(b, p => PointToScreen(p), GearRestX(b), _gear.Width, _gear.Height);
                bool geomMoved = Location != _lastFormGeomLoc || ClientSize != _lastFormGeomSize;
                _lastFormGeomLoc = Location;
                _lastFormGeomSize = ClientSize;
                if (geomMoved || !_gear.Visible) { try { _gear.Location = target; } catch { } StopGearAnim(); }
                else if (!_gearAnimHidePending) AnimateGearTo(target); // never fight a slide-out
                if (_gear.HasRows && !_gear.Visible && !_gearAnimHidePending) GearSlideIn(target);
                // The rail is a native layered window competing with the
                // ActiveX for z-order, and WinForms can drop it behind the
                // Flash surface on startup (owned layered windows shown
                // before the owner is really on screen never re-seat
                // themselves). Visible-but-empty is the symptom, so re-show it
                // whenever it is somehow not on screen.
            }
            // Shirt button tracks the preview's bottom-right corner
            // (CharPage parity), independent of the gear list.
            if (_cos != null)
            {
                if (!_cos.Visible)
                {
                    try { if (!IsDisposed) _cos.Show(this); } catch { try { _cos.Show(); } catch { } }
                }
                // Always repaint: if the layered surface was dropped without
                // the handle going away (Visible stays true, nothing logs),
                // the button looks gone until the next repaint. A 52px blit
                // is cheap enough to redo on every follow + watchdog tick.
                _cos.SetState(AppDomain.CurrentDomain.BaseDirectory, _cosHas, _cosOn);
                _cosPushedHas = _cosHas; _cosPushedOn = _cosOn;
                _cos.Follow(b, p => PointToScreen(p));
            }
            // Magnifier summons the loader line; sits left of the shirt.
            if (_mag != null)
            {
                if (!_mag.Visible)
                {
                    try { if (!IsDisposed) _mag.Show(this); } catch { try { _mag.Show(); } catch { } }
                    // Static button: paint once per show, not every tick.
                    _mag.SetState(AppDomain.CurrentDomain.BaseDirectory, true, false);
                }
                _mag.Follow(b, p => PointToScreen(p));
                if (_cos != null && _cos.Visible)
                    _mag.Location = new Point(_cos.Location.X - _mag.Width - 8, _cos.Location.Y);
            }
            // Loader line stays centered while visible.
            if (_loader != null && _loader.Visible) CenterLoader();
        }

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        static void RaiseNoActivate(Form f)
        {
            try
            {
                const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
                if (f != null && f.Visible && f.IsHandleCreated)
                    SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
            catch { }
        }

        void CenterLoader()
        {
            if (_loader == null || IsDisposed) return;
            try
            {
                Rectangle preview = _flash != null && _flash.Visible ? _flash.Bounds : ClientRectangle;
                Rectangle gear = Rectangle.Empty;
                if (_gear != null && _gear.Visible && _gear.HasRows)
                    gear = RectangleToClient(_gear.Bounds);
                _loader.Bounds = RectangleToScreen(LoaderRect(preview, gear, _loader.Height));
            }
            catch { }
        }

        // Typing-line placement: centered over the preview (not the whole
        // window) near its top edge, where it can't cover the gear list or
        // the avatar; narrowed to fit, and shifted clear of the gear list
        // on whichever edge it sits (left by default, right while the
        // sheet menu is open) when the two would still overlap.
        internal static Rectangle LoaderRect(Rectangle preview, Rectangle gear, int height)
        {
            const int Margin = 16, MaxW = 360, MinW = 160;
            int w = Math.Max(MinW, Math.Min(MaxW, preview.Width - 2 * Margin));
            int x = preview.X + (preview.Width - w) / 2;
            int y = preview.Y + Margin;
            var r = new Rectangle(x, y, w, height);
            if (!gear.IsEmpty && r.IntersectsWith(gear))
            {
                bool gearOnRight = gear.Left + gear.Width / 2 >= preview.Left + preview.Width / 2;
                if (gearOnRight)
                {
                    int right = gear.Left - Margin;
                    w = Math.Max(MinW, Math.Min(w, right - (preview.X + Margin)));
                    r = new Rectangle(Math.Max(preview.X + Margin, right - w), y, w, height);
                }
                else
                {
                    int left = gear.Right + Margin;
                    w = Math.Max(MinW, Math.Min(w, preview.Right - Margin - left));
                    r = new Rectangle(left, y, w, height);
                }
            }
            return r;
        }

        void PresentLoader()
        {
            if (_loader == null || IsDisposed) return;
            CenterLoader();
            _loader.Present(this);
        }

        void ToggleLoader()
        {
            if (_loader == null || IsDisposed) return;
            if (_loader.Visible)
            {
                try { _loader.Hide(); } catch { }
            }
            else PresentLoader();
        }

        void OnLoaderCommit(string name)
        {
            Dbg("Loader commit: " + name);
            lock (_loadLock) { _pendingLoad = name; }
            try { if (_loader != null) _loader.Hide(); } catch { }
        }

        public string TakePendingLoad()
        {
            lock (_loadLock) { string n = _pendingLoad; _pendingLoad = null; return n ?? ""; }
        }

        // Rounded top-level window corners (Windows 11; silent no-op older).
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // Loader focus guard: something (Flash boot/content changes) can
        // yank focus out of the typing line seconds after it presents. These
        // let the watchdog pull it back only when safe: never from another
        // app, never from the page itself (drawer inputs live there).
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")]
        static extern IntPtr GetFocus();
        [DllImport("user32.dll")]
        static extern IntPtr GetParent(IntPtr hWnd);

        static bool ForegroundIsOurs()
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch { return false; }
        }

        bool FocusInPage()
        {
            try
            {
                if (_web == null || _web.IsDisposed) return false;
                IntPtr target = _web.Handle;
                IntPtr h = GetFocus();
                while (h != IntPtr.Zero)
                {
                    if (h == target) return true;
                    h = GetParent(h);
                }
            }
            catch { }
            return false;
        }

        void ApplyWindowRounding()
        {
            try
            {
                int pref = 2;
                DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
            }
            catch { }
        }

        // WebView2-in-WinForms quirk: the first click into the page can be
        // swallowed to activate/focus the control instead of reaching the
        // page. Focus it up front (navigate, shown, and Flash-ready - the
        // last focus-grabber in the boot sequence) so the user's first click
        // counts. Skipped when another app is foreground or the loader line
        // owns focus, so typing is never interrupted.
        void FocusPage()
        {
            try
            {
                if (_web == null || _web.IsDisposed || IsDisposed) return;
                if (!ForegroundIsOurs()) return;
                if (_loader != null && _loader.Visible && _loader.ContainsFocus) return;
                _web.Focus();
            }
            catch { }
        }

        // ---- window drag via caption trick ----
        void StartDrag()
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
        }

        // ---- wrappers callable from the FbHost COM proxy (UI thread) ----
        public void DoMinimize() { WindowState = FormWindowState.Minimized; }
        public void DoMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            PostEvent(new JObject { { "ev", "win-state" }, { "max", WindowState == FormWindowState.Maximized } });
        }
        public void DoClose() { Close(); }
        public void DoStartDrag() { StartDrag(); }

        // Edge resizing over the Flash surface: the Flash ActiveX is a native
        // window above the page, so the HTML edge grips can't receive the
        // mouse where it covers them. This filter watches button-downs that
        // land inside the Flash window near a client edge and starts a native
        // sizing loop instead, keeping the preview full-bleed with no dead
        // strips. Anything else (titlebar, rail, drawer, overlay buttons,
        // non-normal window state) passes through untouched. The HTML grips
        // remain as a fallback for when Flash is hidden.
        class EdgeResizeFilter : IMessageFilter
        {
            readonly AppForm _f;
            public EdgeResizeFilter(AppForm f) { _f = f; }
            const int WM_LBUTTONDOWN = 0x201;
            public bool PreFilterMessage(ref Message m)
            {
                var f = _f;
                if (f == null || f.IsDisposed || m.Msg != WM_LBUTTONDOWN) return false;
                try
                {
                    if (f.WindowState != FormWindowState.Normal) return false;
                    if (!f.IsFlashWindow(m.HWnd)) return false;
                    Point c = f.PointToClient(Cursor.Position);
                    // 6px was a 3px target in practice (the window rect
                    // includes the stripped frame, so the usable band is half
                    // the tolerance), which reads as "cannot resize".
                    // 10px matches what a normal window border gives you, and
                    // matches FlashCore.EdgeCursorTol so the resize arrow
                    // shows over exactly the strip that resizes.
                    const int tol = FlashCore.EdgeCursorTol;
                    bool l = c.X < tol;
                    bool r = c.X >= f.ClientSize.Width - tol;
                    bool b = c.Y >= f.ClientSize.Height - tol;
                    if (!l && !r && !b) return false;
                    int ht = b ? (l ? 16 : r ? 17 : 15) : (l ? 10 : 11);
                    // SendMessage runs the modal sizing loop inline, so this
                    // flag is up for the whole drag: the edge-cursor poll
                    // stands down while the user is resizing.
                    f._resizing = true;
                    try
                    {
                        ReleaseCapture();
                        SendMessage(f.Handle, WM_NCLBUTTONDOWN, ht, IntPtr.Zero);
                    }
                    finally { f.EndResize(); }
                    return true;
                }
                catch { return false; }
            }
        }

        // True when h belongs to the Flash surface (panel or ActiveX child).
        // Overlay buttons, the loader line, WebView2 and foreign windows
        // never match, so their clicks always pass through.
        bool IsFlashWindow(IntPtr h)
        {
            try
            {
                if (h == IntPtr.Zero || _flash == null) return false;
                IntPtr panel = IntPtr.Zero, ax = IntPtr.Zero;
                try { panel = _flash.Handle; } catch { }
                try { ax = _flash.ActiveHandle; } catch { }
                IntPtr p = h;
                while (p != IntPtr.Zero && p != Handle)
                {
                    if (p == panel || p == ax) return true;
                    p = GetParent(p);
                }
            }
            catch { }
            return false;
        }

        // Page edge handles -> native sizing loop (same trick as the titlebar
        // drag: hand the button-down to Windows as a frame hit).
        public void DoStartResize(string edge)
        {
            if (WindowState != FormWindowState.Normal) return;
            int ht;
            switch ((edge ?? "").ToLowerInvariant())
            {
                case "left": ht = 10; break;
                case "right": ht = 11; break;
                case "top": ht = 12; break;
                case "topleft": ht = 13; break;
                case "topright": ht = 14; break;
                case "bottom": ht = 15; break;
                case "bottomleft": ht = 16; break;
                case "bottomright": ht = 17; break;
                default: return;
            }
            // Same as the message-filter path: hold the flag across the modal
            // sizing loop so the edge-cursor poll stays out of the drag.
            _resizing = true;
            try
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, ht, IntPtr.Zero);
            }
            finally { EndResize(); }
        }

        // Leave a sizing loop: clear the flag and flush whatever the drag
        // deferred. Called from the finally of both resize entry points.
        void EndResize()
        {
            _resizing = false;
            if (_holesDirty)
            {
                _holesDirty = false;
                try { ApplyOverlayHoles(); } catch { }
            }
        }

        public bool IsMaximized { get { return WindowState == FormWindowState.Maximized; } }

        // Misc > Hide interface: the native overlays over the preview (gear
        // list, magnifier, shirt button) - clean screenshots. The gear rail
        // slides out to the left edge (Hide lands on glide arrival); the
        // buttons hide instantly.
        bool _overlaysHidden;
        public void DoSetOverlaysHidden(bool hidden)
        {
            _overlaysHidden = hidden;
            if (hidden)
            {
                GearSlideOut();
                foreach (Form f in new Form[] { _cos, _mag })
                {
                    try { if (f != null && f.Visible) f.Hide(); } catch { }
                }
            }
            else
            {
                // Un-hiding inside the slide-out glide: the pending flag makes
                // FollowGear skip both the re-show and the re-glide, and the
                // glide then lands its Hide() - leaving the rail gone with no
                // way back. Cancel the exit and re-assert the overlays, which
                // also repairs a rail that went missing some other way.
                StopGearAnim();
                _gearAnimHidePending = false;
                foreach (Form f in new Form[] { _cos, _mag })
                {
                    try { if (f != null && f.IsHandleCreated) f.Show(this); } catch { try { if (f != null) f.Show(); } catch { } }
                }
                FollowGear();
            }
        }
        // Full-menu dock starts closed for a clean preview (toggled by the
        // titlebar settings button); the gear rail itself always shows.
        public void DoReportRect(int x, int y, int w, int h) { PositionFlash(new JObject { { "x", x }, { "y", y }, { "w", w }, { "h", h } }); }
        // Page -> overlay: {has (character owns a cosmetic set), on}.
        public void DoCosmeticsButton(string stateJson)
        {
            if (_cos == null) return;
            bool has = false, on = false;
            try
            {
                var o = JObject.Parse(stateJson ?? "{}");
                has = o.Value<bool>("has");
                on = o.Value<bool>("on");
            }
            catch { }
            _cosHas = has; _cosOn = on;
            _cos.SetState(AppDomain.CurrentDomain.BaseDirectory, has, on);
            _cosPushedHas = has; _cosPushedOn = on;
        }
        // Overlay -> page: bumped on every shirt-button click; the page
        // polls CosmeticsSeq and toggles when it changes.
        public int CosmeticsToggleSeq { get { return _cosToggleSeq; } }
        void OnCosmeticsClicked()
        {
            Dbg("Cosmetics overlay clicked");
            _cosToggleSeq++;
        }
        public void DoFlashCall(string method, string paramsJson)
        {
            if (_flash == null) return;
            string[] ps = null;
            try { ps = JsonConvert.DeserializeObject<string[]>(paramsJson); } catch { }
            _flash.FlashCall(method, ps ?? new string[0]);
        }
        // Last rows the page sent for the gear list (headless tests read it:
        // there is no overlay window in headless runs).
        internal string LastGearRows = "[]";

        public void DoGear(string rowsJson)
        {
            LastGearRows = rowsJson ?? "[]";
            if (_gear == null || _flash == null) return;
            JArray rows = null;
            try { rows = JArray.Parse(rowsJson ?? "[]"); } catch { }
            _gear.SetRows(AppDomain.CurrentDomain.BaseDirectory, rows);
            if (!_gear.HasRows) return;
            // No manual Show during a pending slide-out: the glide's Hide()
            // landing would fight a mid-flight re-show (watchdog tick).
            if (!_gearAnimHidePending)
            {
                // Same reasoning as GearSlideIn: Show() must run even when we
                // already own the window, or a rail hidden by a menu toggle
                // never comes back when the page re-reports its rows.
                try
                {
                    if (!IsDisposed && _gear.Owner != this) _gear.Show(this); else _gear.Show();
                }
                catch { try { _gear.Show(); } catch { } }
            }
            FollowGear();
        }

        // Slot icons shown before any character loads (dimmed, unnamed): the
        // gear icons double as SWF drop targets from the start.
        static JArray DefaultGearRows()
        {
            var rows = new JArray();
            foreach (var t in new[] { "weapon", "armor", "helm", "cape", "pet", "misc" })
            {
                rows.Add(new JObject
                {
                    { "icon", t },
                    { "name", "" },
                    { "dim", true }
                });
            }
            return rows;
        }


        static string HideMethodFor(string icon)
        {
            switch ((icon ?? "").ToLowerInvariant())
            {
                case "weapon": return "unarmed";
                case "armor": return "hideArmor";
                case "helm": return "hideHelm";
                case "cape": return "hideCape";
                case "pet": return "hidePet";
                case "misc": return "hideMisc";
                case "hair": return "hideHair";
                default: return null;
            }
        }

        // Gear icon clicks toggle the same hide state as the Misc tab's
        // checkboxes: the page owns that state, so clicks are queued here and
        // drained by the page (TakeGearClicks), which flips the checkbox.
        // (They used to keep a separate C# copy, so the two drifted apart.)
        readonly Queue<string> _gearClicks = new Queue<string>();

        internal void OnGearIcon(string icon)
        {
            if (HideMethodFor(icon) == null) return;
            lock (_gearClicks) _gearClicks.Enqueue(icon.ToLowerInvariant());
        }

        public string TakeGearClicks()
        {
            lock (_gearClicks)
            {
                if (_gearClicks.Count == 0) return "[]";
                var arr = new JArray(_gearClicks.ToArray());
                _gearClicks.Clear();
                return arr.ToString(Formatting.None);
            }
        }

        public bool DoCopyLog()
        {
            string log = GetLog();
            try
            {
                if (string.IsNullOrEmpty(log)) Clipboard.Clear();
                else Clipboard.SetText(log);
                return true;
            }
            catch (Exception ex) { Dbg("CopyLog EX: " + ex.Message); return false; }
        }
        // Copy the current preview to the clipboard as an image. The bitmap
        // comes from FlashCore.Snapshot() (IViewObject::Draw), not from the
        // screen, so it captures the render even when another window covers
        // it and it never includes our own panels or the window frame.
        // The Bitmap format paints into Paint/Word; the PNG stream is what
        // browsers, Electron apps (Discord, Slack) and chat clients read.
        public bool DoSnapImage()
        {
            Bitmap bmp = null;
            try
            {
                if (_flash == null) return false;
                // 2x the preview, capped: the player renders vectors at the
                // control's size, so this is extra real detail, not upscaling.
                int pw = Math.Max(1, _flash.Width), ph = Math.Max(1, _flash.Height);
                double sc = Math.Min(2.0, 2600.0 / Math.Max(pw, ph));
                int sw = Math.Max(2, (int)Math.Round(pw * sc));
                int sh = Math.Max(2, (int)Math.Round(ph * sc));
                bool big = sw > pw + 2 || sh > ph + 2;
                if (big) _flash.Supersample(sw, sh);
                try { bmp = _flash.Snapshot(); }
                finally { if (big) _flash.RestoreSize(); }
                if (bmp == null || bmp.Width < 2 || bmp.Height < 2) return false;
                var data = new DataObject();
                data.SetData(DataFormats.Bitmap, true, bmp);
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    data.SetData("PNG", false, ms);
                    Clipboard.SetDataObject(data, true);   // copy: the stream can close after this
                }
                return true;
            }
            catch (Exception ex) { Dbg("SnapImage EX: " + ex.Message); return false; }
            finally { if (bmp != null) bmp.Dispose(); }
        }

        // Copy the character alone: the scene is swapped for black and then for
        // white behind the avatar, and the two renders give the alpha channel
        // (see Matte.Cut). The scene the user picked is put back in the same
        // call, so the preview only flickers for the two frames it takes.
        public bool DoSnapCutout()
        {
            Bitmap dark = null, light = null, cut = null;
            try
            {
                if (_flash == null) return false;
                int pw = Math.Max(1, _flash.Width), ph = Math.Max(1, _flash.Height);
                double sc = Math.Min(2.0, 2600.0 / Math.Max(pw, ph));
                int sw = Math.Max(2, (int)Math.Round(pw * sc));
                int sh = Math.Max(2, (int)Math.Round(ph * sc));
                bool big = sw > pw + 2 || sh > ph + 2;
                try
                {
                    if (big) _flash.Supersample(sw, sh);
                    _flash.SetBackgroundColor(0x000000);
                    dark = _flash.Snapshot();
                    _flash.SetBackgroundColor(0xFFFFFF);
                    light = _flash.Snapshot();
                }
                finally
                {
                    try { _flash.LoadBackground(_bgName); } catch { }
                    if (big) _flash.RestoreSize();
                }
                if (dark == null || light == null) return false;
                cut = Matte.Cut(dark, light);
                if (cut == null || cut.Width < 2 || cut.Height < 2) return false;
                var data = new DataObject();
                data.SetData(DataFormats.Bitmap, true, cut);
                using (var ms = new MemoryStream())
                {
                    cut.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    // The PNG stream is what carries the transparency: the DIB
                    // clipboard format is what a plain picture paste would read,
                    // and several clients flatten its alpha.
                    data.SetData("PNG", false, ms);
                    Clipboard.SetDataObject(data, true);
                }
                return true;
            }
            catch (Exception ex) { Dbg("SnapCutout EX: " + ex.Message); return false; }
            finally
            {
                if (dark != null) dark.Dispose();
                if (light != null) light.Dispose();
                if (cut != null) cut.Dispose();
            }
        }

        // ---- GIF recording -------------------------------------------------
        // The preview is sampled on a timer while the animation plays, but only
        // frames that really changed are kept, and each keeps its own timestamp.
        // That way the GIF's delays follow the animation, not the timer's jitter,
        // and Flash dropping a tick can never duplicate a frame.
        Timer _gifTimer;
        List<Bitmap> _gifFrames;
        List<int> _gifTimes;
        System.Diagnostics.Stopwatch _gifClock;
        int _gifHash, _gifMaxMs = 8000, _gifCaptureW, _gifCaptureH, _gifMaxFrames = 180;
        bool _gifRunning, _gifFinished, _gifOk;
        string _gifError = "", _gifFile = "";
        int _gifBytes, _gifOut, _gifMsOut, _gifW, _gifH;

        string _gifFormat = "gif";
        // Cut-out take: the scene is swapped for black and every kept frame is
        // rendered a second time over white, so the clip keeps real alpha.
        bool _gifCut;
        // A take's encode runs on a worker (see EncodeTake), so these two flags
        // and the take id are the only state the UI thread and that worker share.
        bool _gifEncoding;
        int _gifTake;
        readonly object _gifLock = new object();
        readonly object _encodeLock = new object();

        public bool BeginGif(int ms, string fmt, bool cut)
        {
            if (_flash == null) return false;
            string want = (fmt ?? "").Trim().ToLowerInvariant();
            _gifFormat = want == "webp" ? "webp" : "gif";
            _gifCut = cut;
            Dbg("take begin: " + _gifFormat + " cut=" + _gifCut + " " +
                Math.Max(1, _flash.Width) + "x" + Math.Max(1, _flash.Height));
            DropGifFrames();
            // A take records the preview at the size it is on screen. It used to be
            // rendered 1.5x-2x larger for extra detail, but Supersample resizes the
            // live player, so the preview visibly zoomed for the whole take and
            // every frame cost that many more pixels to draw - fewer frames per
            // second, which is what made the clip stutter.
            // A cut-out take still has to repaint the backdrop black and white, so
            // the player is covered and the cover follows the take (see GifTick).
            // It starts as the current frame, so nothing flashes white before the
            // first recorded frame lands.
            if (_gifCut)
            {
                Bitmap frozen = null;
                try { frozen = _flash.Snapshot(); } catch { }
                try { _flash.Shield(frozen); } catch { }   // FlashCore owns the bitmap now
            }
            // The take runs over black; StopGif puts the chosen scene back.
            if (_gifCut) { try { _flash.SetBackgroundColor(0x000000); } catch { } }
            _gifFrames = new List<Bitmap>();
            _gifTimes = new List<int>();
            _gifHash = 0;
            _gifCaptureW = 0;
            _gifCaptureH = 0;
            _gifMaxFrames = 180;
            _gifMaxMs = Math.Max(1000, Math.Min(15000, ms));
            _gifTake++;
            _gifEncoding = false;
            _gifRunning = true; _gifFinished = false; _gifOk = false;
            _gifError = ""; _gifFile = "";
            _gifBytes = 0; _gifOut = 0; _gifMsOut = 0; _gifW = 0; _gifH = 0;
            _gifClock = System.Diagnostics.Stopwatch.StartNew();
            _gifTimer = new Timer { Interval = 15 };
            _gifTimer.Tick += GifTick;
            _gifTimer.Start();
            return true;
        }

        void GifTick(object sender, EventArgs e)
        {
            if (!_gifRunning || _flash == null) return;
            // The stop test lives here as well as at the end: a tick that fails to
            // draw returns early below, and a run of those used to keep a take
            // going well past its length (a 900 ms take ran 1.6 s and recorded
            // 39 frames instead of ~20).
            if (_gifClock != null && (_gifClock.ElapsedMilliseconds >= _gifMaxMs ||
                (_gifFrames != null && _gifFrames.Count >= _gifMaxFrames)))
            {
                StopGif();
                return;
            }
            Bitmap shot = null, white = null, frame = null;
            try
            {
                // A cut-out take always renders over white first: the silhouette is
                // crisp there, so motion is visible even for a character drawn
                // mostly in black. The same frame over black then gives the matte.
                if (_gifCut)
                {
                    try { _flash.SetBackgroundColor(0xFFFFFF); white = _flash.Snapshot(); } catch { }
                    if (white == null) return;
                }
                else
                {
                    try { shot = _flash.Snapshot(); } catch { }
                    if (shot == null) return;
                }

                int hash = FrameHash(_gifCut ? white : shot);
                if (_gifFrames.Count == 0 || hash != _gifHash)
                {
                    _gifHash = hash;
                    if (_gifCut)
                    {
                        try
                        {
                            _flash.SetBackgroundColor(0x000000);
                            shot = _flash.Snapshot();
                            _flash.SetBackgroundColor(0xFFFFFF);
                        }
                        catch { }
                        if (shot != null) frame = Matte.Recover(shot, white);
                    }
                    else frame = shot;
                }

                if (frame != null)
                {
                    // The cover follows the take, so a cut-out recording shows the
                    // clip being made (moving) rather than a frozen picture, and the
                    // black/white flip behind it stays off screen.
                    if (_gifCut) { try { _flash.Shield((Bitmap)frame.Clone()); } catch { } }
                    if (_gifCaptureW == 0) ResolveGifSize(frame.Width, frame.Height);
                    if (_gifCaptureW == frame.Width && _gifCaptureH == frame.Height)
                    {
                        _gifFrames.Add(frame);   // lossless: exactly as rendered
                        // The list owns it now. For a plain take frame *is* shot (a
                        // cut-out frame is a separate matte), and the finally below
                        // disposes shot - which freed every full-size frame the moment
                        // it was kept. GIF only survived because it records from a
                        // scaled copy; WebP at native size did not.
                        if (ReferenceEquals(frame, shot)) shot = null;
                        if (ReferenceEquals(frame, white)) white = null;
                        frame = null;
                    }
                    else
                    {
                        // The player only ever draws at its own size, so shrinking for
                        // GIF happens here, once per kept frame.
                        _gifFrames.Add(ScaleTo(frame, _gifCaptureW, _gifCaptureH));
                    }
                    _gifTimes.Add((int)_gifClock.ElapsedMilliseconds);
                }
            }
            finally
            {
                if (shot != null) shot.Dispose();
                if (white != null) white.Dispose();
                if (frame != null) frame.Dispose();
            }
            // Stop on time or frame count; the cap already follows the pixel
            // budget, so an unattended take cannot exhaust the process.
            if (_gifClock.ElapsedMilliseconds >= _gifMaxMs || _gifFrames.Count >= _gifMaxFrames) StopGif();
        }

        // WebP keeps the supersampled pixels (lossless); GIF carries a 256-colour
        // table and pays for the pixels twice, so it is shrunk. The frame cap
        // follows the pixel count, so a long take cannot eat gigabytes.
        void ResolveGifSize(int sw, int sh)
        {
            sw = Math.Max(1, sw);
            sh = Math.Max(1, sh);
            if (_gifFormat == "gif" || _gifFormat == "webp")
            {
                // A working size well above the final 400x300 box: the crop is
                // taken here, and the fit down to the box happens afterwards, so
                // the pixels spent on the character are not wasted. WebP is
                // lossless, so it keeps every pixel it is given - which is the whole
                // point of recording it - and the ceiling only exists to stop a very
                // large window from forcing a multi-hundred-megabyte memory and
                // encode bill. 1920 covers a maximised window on a 1080p screen
                // without shrinking it; the frame budget below still guards memory.
                double cap = _gifFormat == "webp" ? 1920.0 : 1200.0;
                double s = Math.Min(1.0, cap / Math.Max(sw, sh));
                _gifCaptureW = Math.Max(2, (int)Math.Round(sw * s));
                _gifCaptureH = Math.Max(2, (int)Math.Round(sh * s));
            }
            else
            {
                _gifCaptureW = sw;
                _gifCaptureH = sh;
            }
            long bytes = (long)_gifCaptureW * _gifCaptureH * 4;
            _gifMaxFrames = (int)Math.Max(24, Math.Min(300, (500L * 1024 * 1024) / Math.Max(1, bytes)));
        }

        // Animated WebP: full colour (alpha included) with real per-frame delays,
        // lossless, so nothing is thrown away before it reaches the chat client.
        void WriteWebp(string path, List<Bitmap> frames, List<int> delays, bool alpha)
        {
            // Every distinct frame becomes one lossless still, and the container
            // is written by WebpWriter with full-canvas frames. libwebp's own
            // animation encoder stores later frames as blended sub-rectangles,
            // and Discord's media decoder shows nothing at all for that layout.
            var stills = EncodeWebpStills(frames, alpha);
            using (var fs = File.Create(path))
                WebpWriter.Write(fs, stills, delays, frames[0].Width, frames[0].Height);
        }

        // Pipes the frames to ffmpeg and returns one standalone lossless WebP per
        // frame, in order. They go to a temp folder rather than a pipe so a
        // half-written frame can never be taken for a complete one.
        List<byte[]> EncodeWebpStills(List<Bitmap> frames, bool alpha)
        {
            string exe = FindFfmpeg();
            if (exe == null) throw new InvalidOperationException("ffmpeg.exe is missing next to the app");
            int w = frames[0].Width, h = frames[0].Height;
            string dir = Path.Combine(Path.GetTempPath(), "manikin-frames-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe,
                    "-y -f rawvideo -pix_fmt bgra -video_size " + w + "x" + h +
                    " -framerate 30 -i - -an -loglevel error" +
                    // Maximum quality: lossless VP8L at the encoder's highest effort
                    // (6 is the ceiling; it costs encode time, not pixels). The pixel
                    // format is always stated - RGB for an opaque take, RGBA where
                    // alpha matters - so the frames can never be routed through a
                    // 4:2:0 conversion, which would drop chroma even though the
                    // codec itself is lossless.
                    " -c:v libwebp -lossless 1 -compression_level 6" +
                    " -pix_fmt " + (alpha ? "bgra" : "rgb24") +
                    " -f image2 \"" + Path.Combine(dir, "f%05d.webp") + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardError = true;

                var diag = new System.Text.StringBuilder();
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    proc.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null && diag.Length < 2000) diag.AppendLine(e.Data);
                    };
                    proc.BeginErrorReadLine();
                    using (var input = proc.StandardInput.BaseStream)
                    {
                        var row = new byte[w * 4];
                        foreach (var bmp in frames)
                        {
                            var bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                            try
                            {
                                for (int y = 0; y < h; y++)
                                {
                                    Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), row, 0, w * 4);
                                    input.Write(row, 0, row.Length);
                                }
                            }
                            finally { bmp.UnlockBits(bd); }
                        }
                    }
                    if (!proc.WaitForExit(300000))
                    {
                        try { proc.Kill(); } catch { }
                        throw new InvalidOperationException("ffmpeg timed out encoding the frames");
                    }
                    if (proc.ExitCode != 0)
                        throw new InvalidOperationException("ffmpeg could not encode the frames (exit " + proc.ExitCode +
                            "): " + diag.ToString().Trim());
                }
                var files = Directory.GetFiles(dir, "f*.webp");
                Array.Sort(files, StringComparer.Ordinal);
                if (files.Length != frames.Count)
                    throw new InvalidOperationException("ffmpeg encoded " + files.Length + " of " + frames.Count + " frames");
                var stills = new List<byte[]>(files.Length);
                foreach (var f in files)
                {
                    var bytes = File.ReadAllBytes(f);
                    if (bytes.Length < 64) throw new InvalidOperationException("a WebP frame came back empty");
                    stills.Add(bytes);
                }
                return stills;
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static string FindFfmpeg()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var dir in new[] { baseDir, Path.Combine(baseDir, "tools") })
            {
                try
                {
                    string p = Path.Combine(dir, "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        // Crops the recorded frames to where the animation actually moves. The
        // stage is mostly empty space, and a chat client that re-encodes a 1200 px
        // frame turns a character occupying a tenth of it into a smudge; cropping
        // keeps that character at full pixel density.
        // Scales the cropped clip into the box Discord shows GIFs in, with the
        // bicubic filter its proxy does not use.
        List<Bitmap> FitGifBox(List<Bitmap> frames)
        {
            int w = frames[0].Width, h = frames[0].Height;
            double s = Math.Min(1.0, Math.Min(400.0 / w, 300.0 / h));
            if (s >= 0.999) return frames;
            int nw = Math.Max(2, (int)Math.Round(w * s));
            int nh = Math.Max(2, (int)Math.Round(h * s));
            var scaled = new List<Bitmap>(frames.Count);
            foreach (var b in frames)
            {
                scaled.Add(ScaleTo(b, nw, nh));
                b.Dispose();
            }
            return scaled;
        }

        List<Bitmap> CropToMotion(List<Bitmap> frames)
        {
            int w = frames[0].Width, h = frames[0].Height;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            int[] first = FramePixels(frames[0]);
            for (int f = 1; f < frames.Count; f++)
            {
                int[] px = FramePixels(frames[f]);
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int p = first[row + x], q = px[row + x];
                        if (p == q) continue;
                        int dr = Math.Abs(((p >> 16) & 0xFF) - ((q >> 16) & 0xFF));
                        int dg = Math.Abs(((p >> 8) & 0xFF) - ((q >> 8) & 0xFF));
                        int db = Math.Abs((p & 0xFF) - (q & 0xFF));
                        if (dr + dg + db < 24) continue;   // ignore re-encode noise
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < minX || maxY < minY) return frames;   // nothing moved: keep the stage

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            int pad = Math.Max(12, (int)(Math.Max(bw, bh) * 0.30));
            int x0 = Math.Max(0, minX - pad), y0 = Math.Max(0, minY - pad);
            int x1 = Math.Min(w - 1, maxX + pad), y1 = Math.Min(h - 1, maxY + pad);
            var box = new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            // If the whole scene is moving (weather, parallax) there is nothing to
            // gain, and the full frame is the honest clip.
            if ((long)box.Width * box.Height > (long)w * h * 85 / 100) return frames;

            var cropped = new List<Bitmap>(frames.Count);
            foreach (var b in frames)
            {
                cropped.Add(b.Clone(box, System.Drawing.Imaging.PixelFormat.Format32bppArgb));
                b.Dispose();
            }
            return cropped;
        }

        static int[] FramePixels(Bitmap b)
        {
            int w = b.Width, h = b.Height;
            var px = new int[w * h];
            System.Drawing.Imaging.BitmapData bd = null;
            try
            {
                bd = b.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                for (int y = 0; y < h; y++)
                    Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), px, y * w, w);
            }
            catch { }
            finally { if (bd != null) { try { b.UnlockBits(bd); } catch { } } }
            return px;
        }

        static Bitmap ScaleTo(Bitmap src, int w, int h)
        {
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, w, h);
            }
            return bmp;
        }

        public string StopGif()
        {
            if (!_gifRunning) return GifPoll();
            _gifRunning = false;
            try { if (_gifTimer != null) { _gifTimer.Stop(); _gifTimer.Dispose(); } } catch { }
            _gifTimer = null;
            Dbg("take stop: frames=" + (_gifFrames == null ? 0 : _gifFrames.Count) +
                " ms=" + (_gifClock == null ? 0 : _gifClock.ElapsedMilliseconds));
            // A cut-out take ran against a black backdrop; the chosen scene goes
            // back before anything else is drawn.
            if (_gifCut) { try { _flash.LoadBackground(_bgName); } catch { } }
            // The cover comes off before anything else: the frames are already snapped,
            // so only the encode has to wait. This used to run after the "nothing
            // moved" return, which could leave the cover up for good.
            try { _flash.Shield(null); } catch { }
            // A still pose still produces a file in the chosen format, so the
            // clipboard can never keep the previous take's file.
            if (_gifFrames == null || _gifFrames.Count == 0)
            {
                lock (_gifLock) { _gifFinished = true; _gifOk = false; _gifError = "nothing moved while recording"; }
                return GifPoll();
            }
            // Hand the take to a worker. Cropping and ffmpeg used to run here, on
            // the UI thread, which froze the whole window for the length of the
            // encode. The frames move out of the recording fields first, so a new
            // take cannot dispose them while they are being encoded.
            var frames = _gifFrames;
            var times = _gifTimes;
            int take = _gifTake;
            string fmt = _gifFormat;
            bool cut = _gifCut;
            _gifFrames = null;
            _gifTimes = null;
            lock (_gifLock) { _gifEncoding = true; _gifFinished = false; }
            // A thread of its own, not Task.Run: the encode blocks on ffmpeg for
            // seconds, and the pool is busy with the catalog sync, so a take could
            // sit in the queue with the panel stuck on "Encoding..." (and the
            // clipboard still holding the previous clip) until the pool freed up.
            new System.Threading.Thread(() =>
            {
                // An exception on this thread is fatal to the whole app (Task.Run
                // swallowed it), so a failed encode has to be reported instead.
                try { EncodeTake(take, fmt, frames, times, cut); }
                catch (Exception ex)
                {
                    Dbg("encode thread EX: " + ex.GetType().Name + ": " + ex.Message);
                    lock (_gifLock)
                    {
                        _gifEncoding = false;
                        if (take == _gifTake)
                        {
                            _gifFinished = true; _gifOk = false;
                            _gifError = "encoding failed: " + ex.Message;
                        }
                    }
                    foreach (var f in frames) { try { f.Dispose(); } catch { } }
                }
            })
            { IsBackground = true, Name = "manikin-encode" }.Start();
            return GifPoll();
        }

        // Runs on a worker thread: no control is touched here, and the only state
        // shared with the UI thread is published at the end under _gifLock.
        void EncodeTake(int take, string fmt, List<Bitmap> frames, List<int> times, bool cut)
        {
            string path = "", error = null;
            int n = frames.Count, outN = n, msOut = 0;
            long bytes = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Count first: a disposed bitmap throws on .Width, so the log has to be
            // able to say "the frames arrived dead" before it reads one.
            Dbg("encode start: " + fmt + " cut=" + cut + " n=" + n);
            int w = frames[0].Width, h = frames[0].Height;
            lock (_encodeLock)
            {
                try
                {
                    // Both formats are cut to the character, not the stage: the
                    // stage is mostly empty, and a chat client that scales the whole
                    // frame down turns the figure into a smudge. A cut-out take
                    // already isolates the character, so it only needs one box for
                    // the whole clip; otherwise CropToMotion keeps a wide margin
                    // around everything that moves.
                    if (cut) frames = Matte.TrimAll(frames);
                    else frames = CropToMotion(frames);
                    // Discord's media proxy resamples any GIF bigger than its 400x300
                    // display box, and its filter is what turns a clean clip into a
                    // smudge. Doing that resize here, before the palette is chosen,
                    // means the file that leaves is the file that gets shown.
                    if (fmt == "gif") frames = FitGifBox(frames);
                    n = frames.Count;
                    // Split out so a slow take can be told apart: cropping is ours,
                    // the rest is ffmpeg (see the take encode line below).
                    int cropMs = (int)clock.ElapsedMilliseconds;
                    Dbg("take crop: " + fmt + " cut=" + cut + " " + frames[0].Width + "x" +
                        frames[0].Height + " frames=" + n + " in " + cropMs + "ms");

                    var delays = new List<int>(n);
                    for (int i = 0; i < n; i++)
                    {
                        int step = i + 1 < n ? times[i + 1] - times[i]
                            : (n > 1 ? times[n - 1] - times[n - 2] : 40);
                        delays.Add(Math.Max(20, Math.Min(3000, step)));
                    }
                    string ext = fmt == "gif" ? ".gif" : ".webp";
                    path = Path.Combine(Path.GetTempPath(),
                        "manikin-" + fmt + "-" + DateTime.Now.ToString("HHmmss") + "-" + n + "f" + ext);
                    if (fmt == "webp")
                    {
                        // Lossless colour that Discord actually animates.
                        WriteWebp(path, frames, delays, cut);
                    }
                    else
                    {
                        // GIF is the palette one, which is what most chat clients
                        // reliably animate.
                        using (var fs = File.Create(path))
                            GifWriter.Write(fs, frames, delays, cut);
                    }
                    bytes = new FileInfo(path).Length;
                    Dbg("take encode: " + fmt + " cut=" + cut + " frames=" + n +
                        " " + frames[0].Width + "x" + frames[0].Height +
                        " bytes=" + bytes + " crop=" + cropMs + "ms" +
                        " encode=" + (clock.ElapsedMilliseconds - cropMs) + "ms");
                    outN = n;
                    msOut = times[n - 1] + delays[n - 1];
                    w = frames[0].Width;
                    h = frames[0].Height;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Dbg("Gif EX: " + ex.Message);
                }
                finally
                {
                    foreach (var f in frames) { try { f.Dispose(); } catch { } }
                }
            }
            lock (_gifLock)
            {
                _gifEncoding = false;
                // A newer take owns the panel now; its result is the one to show.
                if (take == _gifTake)
                {
                    _gifFinished = true;
                    _gifOk = error == null;
                    if (error == null)
                    {
                        _gifFile = path; _gifBytes = (int)bytes; _gifOut = outN;
                        _gifMsOut = msOut; _gifW = w; _gifH = h;
                    }
                    else _gifError = error;
                }
            }
            if (error == null) CopyClip(path);
        }

        // The clipboard is OLE and belongs to the UI thread (the receiving app
        // reads it there), so the finished file is handed back with BeginInvoke
        // instead of being set from the encoder's thread.
        void CopyClip(string path)
        {
            Action put = () =>
            {
                try
                {
                    // The file is what chat clients upload. A Bitmap or PNG next to
                    // it would let a client paste a still instead of the animation,
                    // so the file drop is the only thing on the clipboard.
                    var data = new DataObject();
                    data.SetData(DataFormats.FileDrop, new[] { path });
                    Clipboard.SetDataObject(data, true);
                }
                catch (Exception ex) { Dbg("Clip EX: " + ex.Message); }
            };
            try
            {
                if (IsHandleCreated && InvokeRequired) BeginInvoke(put);
                else put();
            }
            catch { }
        }

        public string GifPoll()
        {
            var o = new JObject();
            lock (_gifLock)
            {
                o["take"] = _gifTake;
                o["recording"] = _gifRunning;
                o["encoding"] = _gifEncoding;
                o["frames"] = _gifFrames == null ? 0 : _gifFrames.Count;
                o["ms"] = _gifRunning && _gifClock != null ? (int)_gifClock.ElapsedMilliseconds : _gifMsOut;
                o["done"] = _gifFinished;
                if (_gifFinished)
                {
                    o["ok"] = _gifOk;
                    if (_gifOk)
                    {
                        o["frames"] = _gifOut;
                        o["ms"] = _gifMsOut;
                        o["bytes"] = _gifBytes;
                        o["w"] = _gifW;
                        o["h"] = _gifH;
                        o["file"] = _gifFile;
                        o["fmt"] = _gifFormat;
                    }
                    else o["error"] = _gifError;
                }
            }
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        void DropGifFrames()
        {
            if (_gifFrames == null) return;
            foreach (var f in _gifFrames) { try { f.Dispose(); } catch { } }
            _gifFrames.Clear();
        }

        // Change detector over every pixel, so a one-pixel glow flicker is a
        // frame of its own rather than being silently merged away.
        static int FrameHash(Bitmap b)
        {
            int w = b.Width, h = b.Height;
            System.Drawing.Imaging.BitmapData bd = null;
            try
            {
                bd = b.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var row = new int[w];
                int hash = 17;
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), row, 0, w);
                    for (int x = 0; x < w; x++) hash = hash * 31 + row[x];
                }
                return hash;
            }
            catch { return 0; }
            finally { if (bd != null) { try { b.UnlockBits(bd); } catch { } } }
        }

        public string DoFlashQuery(string method) { if (_flash == null) return ""; return _flash.QueryFlash(method); }
        public void DoBackground(string name)
        {
            _bgName = name ?? "";
            if (_flash != null) _flash.LoadBackground(_bgName);
        }
        // Remembered so a cut-out capture can put the user's scene back after it
        // has swapped the background for black and then white.
        string _bgName = "";
        // Cache of the last overlay rects the page reported (in Form-client
        // coordinates, same space PositionFlash receives). Re-applied every time
        // PositionFlash runs so the punched hole always tracks _flash's true
        // current position, instead of depending on which arrived first at boot.
        List<Tuple<Rectangle, int>> _lastOverlayRectsFormCoords = new List<Tuple<Rectangle, int>>();
        // Last dock/sheet-hidden flags from the page (a hidden panel still
        // reports rects for the slide-path hole; GearRestX must not read
        // them as "visible"). Default false = old width-inference behavior.
        bool _lastDockHidden;
        bool _lastSheetHidden;

        // Every hole is cut to EXACTLY the rect the page reported: no outset,
        // no overshoot. An outset sounds like it would hide the region's
        // aliased edge, but the gap it opens is page background showing through
        // the hole - a gray frame around every panel, which is far more obvious
        // than a slightly stair-stepped silhouette. Exact is the only value
        // where the panel covers its hole completely and nothing else does.
        void ApplyOverlayHoles()
        {
            if (_flash == null) return;
            // Assigning a window region forces a full repaint of the Flash
            // surface. During a resize drag the page re-reports its rects on
            // every frame, so applying each one repaints the whole preview
            // dozens of times a second - that is the stutter. Hold the update
            // until the drag settles; EndResize applies the final one.
            if (_resizing) { _holesDirty = true; RefreshEdges(); return; }
            if (_lastOverlayRectsFormCoords.Count == 0) { _flash.ClearOverlayHoles(); RefreshEdges(); return; }
            var holes = new List<Tuple<Rectangle, int>>();
            foreach (var h in _lastOverlayRectsFormCoords)
            {
                var r = h.Item1;
                if (r.Width <= 0 || r.Height <= 0) continue;
                holes.Add(Tuple.Create(new Rectangle(r.X - _flash.Left, r.Y - _flash.Top, r.Width, r.Height), h.Item2));
            }
            _flash.SetOverlayHoles(holes);
            RefreshEdges();
        }

        // ---- anti-aliased panel corners (EdgeOverlay) ----
        // Colors come from a capture of the page, which is async. On every
        // rect change the rims of panels that did not move are repainted at
        // once from the last capture (gone or moved ones vanish with their
        // hole), then a fresh capture repaints them all.
        Bitmap _edgesPage;
        double _edgesScale = 1;
        List<Rectangle> _edgesCaptured = new List<Rectangle>();

        List<Tuple<Rectangle, int>> EdgePanels()
        {
            var panels = new List<Tuple<Rectangle, int>>();
            if (_resizing || _flash == null || !_flash.Visible) return panels;
            foreach (var h in _lastOverlayRectsFormCoords)
            {
                bool wide = h.Item1.Width > 100; // same inference as GearRestX
                if (h.Item2 > 0 && !(wide ? _lastSheetHidden : _lastDockHidden)) panels.Add(h);
            }
            return panels;
        }

        void PaintEdges(List<Tuple<Rectangle, int>> panels, Bitmap page, double scale)
        {
            try
            {
                if (panels.Count == 0 || page == null || !_edges.Render(panels, page, scale, p => PointToScreen(p))) { HideEdges(); return; }
                if (!_edges.Visible)
                {
                    try { _edges.Show(this); } catch { try { _edges.Show(); } catch { } }
                }
            }
            catch (Exception ex) { Dbg("PaintEdges EX: " + ex.Message); HideEdges(); }
        }

        void RefreshEdges()
        {
            if (_edges == null) return;
            PaintEdges(EdgePanels().FindAll(p => _edgesCaptured.Contains(p.Item1)), _edgesPage, _edgesScale);
            UpdateEdges();
        }

        void HideEdges()
        {
            try { if (_edges != null && _edges.Visible) _edges.Hide(); } catch { }
        }

        void FollowEdges()
        {
            try { if (_edges != null && _edges.Visible) _edges.Location = PointToScreen(_edges.ClientOrigin); } catch { }
        }

        // A burst of rect reports (theme wheel drag, tab switching) coalesces
        // into one more pass once the running capture lands, always with the
        // latest rects.
        async void UpdateEdges()
        {
            if (_edges == null || _core == null) return;
            if (_edgesBusy) { _edgesAgain = true; return; }
            _edgesBusy = true;
            try
            {
                do
                {
                    _edgesAgain = false;
                    // The page reveals a panel right AFTER its hole is cut
                    // (openTab): give that reveal a frame to paint first.
                    await Task.Delay(50);
                    var panels = EdgePanels();
                    if (IsDisposed || panels.Count == 0) { HideEdges(); continue; }
                    using (var ms = new MemoryStream())
                    {
                        await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                        if (_edgesAgain || IsDisposed) continue;
                        ms.Position = 0;
                        using (var png = new Bitmap(ms))
                        {
                            if (_edgesPage != null) _edgesPage.Dispose();
                            _edgesPage = new Bitmap(png);
                        }
                    }
                    _edgesScale = _web.ClientSize.Width > 0 ? _edgesPage.Width / (double)_web.ClientSize.Width : 1;
                    panels = EdgePanels();
                    _edgesCaptured = panels.ConvertAll(p => p.Item1);
                    PaintEdges(panels, _edgesPage, _edgesScale);
                }
                while (_edgesAgain);
            }
            catch (Exception ex) { Dbg("UpdateEdges EX: " + ex.Message); }
            finally { _edgesBusy = false; }
        }
        public void DoOverlayRects(string rectsJson, bool dockHidden, bool sheetHidden)
        {
            if (_flash == null) return;
            var rects = new List<Tuple<Rectangle, int>>();
            try
            {
                var arr = JArray.Parse(rectsJson ?? "[]");
                foreach (var r in arr)
                {
                    int w = r.Value<int>("w"), h = r.Value<int>("h");
                    if (w <= 0 || h <= 0) continue;
                    int rr = (int?)r["r"] ?? 0;
                    rects.Add(Tuple.Create(new Rectangle(r.Value<int>("x"), r.Value<int>("y"), w, h), rr));
                }
            }
            catch (Exception ex) { Dbg("OverlayRects EX: " + ex.Message); }
            // Any hole at all is useless: the gear rail is a native layered
            // window drawn on top, and the dock/sheet holes are reported
            // whenever they are up. An empty list can only mean a hole was
            // dropped while its panel stayed on screen, which leaves a dead
            // rectangle of page background stamped over the scene (the rail
            // then cannot be re-shown either - nothing ever cuts it back).
            // Refuse to believe it and keep the last good set.
            if (rects.Count == 0 && _lastOverlayRectsFormCoords.Count > 0 &&
                !(dockHidden && sheetHidden))
            {
                Dbg("OverlayRects: ignoring empty set (panels still up) - keeping " + _lastOverlayRectsFormCoords.Count);
                return;
            }
            // Same rects again (the page re-reports them after a theme change so
            // the corner rims get recolored): re-cutting an identical region
            // only forces a full Flash repaint, once per color-wheel step.
            if (!_holesDirty && dockHidden == _lastDockHidden && sheetHidden == _lastSheetHidden &&
                rects.Count == _lastOverlayRectsFormCoords.Count &&
                rects.TrueForAll(r => _lastOverlayRectsFormCoords.Exists(o => o.Item1 == r.Item1 && o.Item2 == r.Item2)))
            {
                UpdateEdges();
                return;
            }
            _lastOverlayRectsFormCoords = rects;
            _lastDockHidden = dockHidden;
            _lastSheetHidden = sheetHidden;
            ApplyOverlayHoles();
            // Re-follow from the fresh rects so the rail nudges at once.
            FollowGear();
        }
        // Theme color (#RRGGBB): paints the Flash hosting surface, the form's
        // own background and the native window frame border to match, so no
        // dark hairlines show around the preview. The form matters too: a
        // single unpainted row between the page and the ActiveX (the seam just
        // under the title bar) shows whatever the form is painted with, and it
        // was still the hardcoded dark UiBg. DWMWA_BORDER_COLOR is Win11
        // (silently ignored older).
        public void DoThemeColor(string hex)
        {
            try
            {
                if (string.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#') return;
                int rgb = int.Parse(hex.Substring(1), System.Globalization.NumberStyles.HexNumber);
                var c = System.Drawing.Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
                if (_flash != null) _flash.SetSurfaceColor(c);
                try { BackColor = c; } catch { }
                int border = (c.B << 16) | (c.G << 8) | c.R;
                DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
            }
            catch (Exception ex) { Dbg("ThemeColor EX: " + ex.Message); }
        }
        public void DoReset() { if (_flash != null) _flash.ResetFlash(); }
        public void DoLoadItem(string itemType, string itemFile, string weaponType, string link) { if (_flash != null) _flash.LoadItem(itemType, itemFile, weaponType, link); }
        public void DoLoadItemBytes(string itemType, string fileName, byte[] bytes, string weaponType, string link) { if (_flash != null) _flash.LoadItemBytes(itemType, bytes, fileName, weaponType, link); }
        public void DoDropFile(string slot, string path)
        {
            Dbg("DoDropFile/native slot=" + slot + " path=" + path);
            if (_flash == null || string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path) || !path.EndsWith(".swf", StringComparison.OrdinalIgnoreCase)) return;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch { return; }
            if (bytes.Length < 4) return;
            string wt = slot.Equals("Weapon", StringComparison.OrdinalIgnoreCase) ? FlashCore.DetectWeaponType(bytes) : null;
            _flash.LoadItemBytes(slot, bytes, Path.GetFileName(path), wt);
        }
        public void DoDropItem(string itemType, string fileName, string base64)
        {
            Dbg("DoDropItem/ui type=" + itemType + " file=" + fileName);
            if (_flash == null) return;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64 ?? ""); }
            catch { Dbg("DropItem bad data: " + fileName); return; }
            if (bytes.Length < 4 || bytes[1] != (byte)'W' || bytes[2] != (byte)'S' ||
                (bytes[0] != (byte)'F' && bytes[0] != (byte)'C' && bytes[0] != (byte)'Z'))
            { Dbg("DropItem not an SWF: " + fileName); return; }
            string wt = string.Equals(itemType, "Weapon", StringComparison.OrdinalIgnoreCase)
                ? FlashCore.DetectWeaponType(bytes) : null;
            _flash.LoadItemBytes(itemType, bytes, fileName ?? "dropped.swf", wt);
        }
        public string DoPickFolder() { return PickFolder(); }
        public string DoSaveFile(string data, string name) { return SaveFile(data, name); }
        public string DoOpenFile() { return OpenFile(); }

        // ---- folder/file dialogs ----
        string PickFolder()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Choose output folder for SWFs" })
            {
                return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : "";
            }
        }

        string SaveFile(string data, string defaultName)
        {
            using (var dlg = new SaveFileDialog
            {
                Filter = "Manikin project (*.fbp)|*.fbp|JSON (*.json)|*.json|All files (*.*)|*.*",
                FileName = string.IsNullOrEmpty(defaultName) ? "manikin.fbp" : defaultName,
                AddExtension = true
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return "";
                try { File.WriteAllText(dlg.FileName, data ?? ""); return dlg.FileName; }
                catch { return ""; }
            }
        }

        string OpenFile()
        {
            using (var dlg = new OpenFileDialog
            {
                Filter = "Manikin project (*.fbp)|*.fbp|JSON (*.json)|*.json|All files (*.*)|*.*"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return "";
                try
                {
                    string data = File.ReadAllText(dlg.FileName);
                    return JsonConvert.SerializeObject(new { path = dlg.FileName, data });
                }
                catch { return ""; }
            }
        }

        // ---- custom frame: real OS window, client covers everything ----
        // Keeping WS_CAPTION/WS_THICKFRAME gives us OS minimize/maximize
        // animations, Aero snap, taskbar thumbnails and shadow. Stripping the
        // non-client area below hides the native titlebar so only our dark
        // bar shows. Edge resizing stays native (no custom branch needed).
        const int WM_NCLBUTTONDOWN = 0xA1;
        const int HTCAPTION = 0x2;
        const int WM_NCCALCSIZE = 0x83;
        const int WM_NCHITTEST = 0x84;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        const int MONITOR_DEFAULTTONEAREST = 2;
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);

        protected override void WndProc(ref Message m)
        {
            // Hide the native titlebar/borders: with a real frame kept, the
            // OS still supplies animations, snap and shadow. Edge resizing
            // comes from the page's edge handles (StartResize): the WebView2
            // covers the whole client area, so the form never sees the mouse
            // at its edges.
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                // Maximized windows are placed overhanging the monitor by
                // the (now invisible) frame thickness; with the whole window
                // as client area that overhang cut the UI off at every edge.
                // Clamp the client rect to the monitor's work area instead
                // (this also keeps the taskbar uncovered on every monitor).
                if (IsZoomed(Handle))
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (GetMonitorInfo(MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST), ref mi))
                        Marshal.StructureToPtr(mi.rcWork, m.LParam, false);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wp, IntPtr lp);
    }
}
