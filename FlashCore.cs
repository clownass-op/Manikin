using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Xml.Linq;
using AxShockwaveFlashObjects;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;

namespace ManikinApp
{
    // FlashCore: hosts the real Flash ActiveX and the char6 avatar player
    // in-process. This is the combined-app version of FlashHost2 -- no pipe,
    // no external process. The control is positioned by AppForm over the UI's
    // preview region.
    class FlashCore : Panel
    {
        static readonly string DebugLog = Path.Combine(Path.GetTempPath(), "fb-debug.log");

        static void Dbg(string line)
        {
            try { System.IO.File.AppendAllText(DebugLog, DateTime.Now.ToString("HH:mm:ss.fff") + " [flash] " + line + Environment.NewLine); } catch { }
        }

        AxShockwaveFlash _flash;
        string _baseDir;
        byte[] _char6;
        Dictionary<string, byte[]> _backgrounds = new Dictionary<string, byte[]>();

        public event Action Ready;

        // Headless test runs can point the host at a specific player build.
        public static string PlayerOverride;

        public FlashCore(string baseDir)
        {
            _baseDir = baseDir;
            BackColor = Color.FromArgb(0x0A, 0x0C, 0x14);
            CreateFlash();
        }

        // Never take keyboard focus: clicking the avatar/scene would
        // otherwise yank focus out of the WebView2 page, and the NEXT click
        // back into the page gets swallowed re-focusing it (dock buttons,
        // titlebar buttons and drawer inputs randomly "not opening" on
        // first click). Mouse input is unaffected - only activation is
        // suppressed, same as the NOACTIVATE overlay windows. Applied to
        // the whole Flash subtree (AxHost wrapper + inner OCX window).
        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE;
                return cp;
            }
        }

        // Resize cursor tolerance, shared with AppForm so the arrow and the
        // area that actually resizes can never drift apart.
        //
        // The cursor itself is driven from AppForm's edge-cursor timer, not
        // from here: the ActiveX answers WM_SETCURSOR itself and paints its
        // own arrow, so a handler on this panel (or its parent) never runs.
        public const int EdgeCursorTol = 10;

        static void NoActivateTree(IntPtr root)
        {
            try
            {
                if (root == IntPtr.Zero) return;
                try { SetWindowLong(root, GWL_EXSTYLE, GetWindowLong(root, GWL_EXSTYLE) | WS_EX_NOACTIVATE); } catch { }
                try
                {
                    EnumChildWindows(root, (h, p) =>
                    {
                        try { SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE); } catch { }
                        return true;
                    }, IntPtr.Zero);
                }
                catch { }
            }
            catch { }
        }

        // Plain-color themes: paint the hosting surface (panel + ActiveX
        // letterbox) the same color so no dark seam shows around the
        // player on light backgrounds.
        public void SetSurfaceColor(Color c)
        {
            try { BackColor = c; } catch { }
            try { if (_flash != null) _flash.BackColor = c; } catch { }
        }

        // Punches transparent, click-through holes in the Flash surface so
        // HTML (the dock/sheet overlay, which lives in the WebView2 sibling
        // window) can visually and interactively sit on top of the live
        // Flash content instead of being painted over by it. Rectangles are
        // in this panel's own client coordinates. The Region stays on this
        // panel (not the ActiveX), so it survives player rebuilds.
        // cornerRadius rounds each hole to match the overlay's border-radius,
        // so no square page-background points peek out around rounded
        // corners (hit-testing follows the rounded shape too).
        Region _overlayRegion;
        // Each hole carries its own corner radius (dock/sheet 18 to match
        // their border-radius, gear rail 0 - it has no background, so a
        // rounded hole would fringe a visible gray seam around it).
        public void SetOverlayHoles(IEnumerable<Tuple<Rectangle, int>> holesInPanelCoords)
        {
            try
            {
                if (_overlayRegion != null) { _overlayRegion.Dispose(); _overlayRegion = null; }
                Region region = new Region(this.ClientRectangle);
                // Exclude one hole at a time: slide pre-cuts report both the
                // resting rect and the live rect for the same panel, and those
                // heavily overlap. Adding both subpaths to ONE GraphicsPath
                // leaves their overlap filled (even-odd) so it is NOT excluded
                // - Flash then paints over the panel mid-slide and it flashes.
                foreach (var hole in holesInPanelCoords)
                {
                    var r = hole.Item1;
                    int cornerRadius = hole.Item2;
                    if (cornerRadius > 0)
                    {
                        using (var rounded = RoundedHole(r, cornerRadius))
                            region.Exclude(rounded);
                    }
                    else
                    {
                        region.Exclude(r);
                    }
                }
                _overlayRegion = region;
                this.Region = region;
            }
            catch { }
        }

        // A window region is 1-bit, so a rounded hole can never be smooth on
        // its own. The hole keeps ONLY the pixels the panel's rounded shape
        // covers completely: Flash stays under every anti-aliased edge pixel
        // and EdgeOverlay paints those on top with real alpha. A path-built
        // region included half-covered pixels too, and those showed the page's
        // dark background blended into the corner - the jagged dark steps.
        static Region RoundedHole(Rectangle r, int radius)
        {
            int rad = CornerRadius(r, radius);
            var reg = new Region(new Rectangle(r.X, r.Y + rad, r.Width, r.Height - 2 * rad));
            for (int j = 0; j < rad; j++)
            {
                int inset = CornerInset(rad, j);
                int w = r.Width - 2 * inset;
                if (w <= 0) continue;
                reg.Union(new Rectangle(r.X + inset, r.Y + j, w, 1));
                reg.Union(new Rectangle(r.X + inset, r.Bottom - 1 - j, w, 1));
            }
            return reg;
        }

        // CSS shrinks a radius that doesn't fit (the short dock on small windows).
        internal static int CornerRadius(Rectangle r, int radius)
        {
            return Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
        }

        // How many pixels of corner row j (0 = outermost) the rounded shape
        // does NOT fully cover. A pixel is fully covered when its corner
        // farthest from the arc center is still inside the circle.
        internal static int CornerInset(int rad, int j)
        {
            double dy = rad - j;
            return (int)Math.Ceiling(rad - Math.Sqrt(Math.Max(0, (double)rad * rad - dy * dy)) - 1e-9);
        }

        public void ClearOverlayHoles()
        {
            try
            {
                if (_overlayRegion != null) { _overlayRegion.Dispose(); _overlayRegion = null; }
                this.Region = null;
            }
            catch { }
        }

        bool _started;

        // True once the player movie has been handed to the control. The
        // ActiveX usually gets its handle (parking window) inside this
        // constructor, i.e. before anyone could subscribe to Ready, so
        // subscribers must check this after attaching.
        public bool Started { get { return _started; } }

        // Native window of the Flash ActiveX itself (child of this panel).
        internal IntPtr ActiveHandle
        {
            get { try { return _flash != null ? _flash.Handle : IntPtr.Zero; } catch { return IntPtr.Zero; } }
        }

        void CreateFlash()
        {
            _flash = new AxShockwaveFlash();
            ((System.ComponentModel.ISupportInitialize)_flash).BeginInit();
            SuspendLayout();
            _flash.Dock = DockStyle.Fill;
            _flash.Enabled = true;
            _flash.AllowDrop = true;
            Controls.Add(_flash);
            ((System.ComponentModel.ISupportInitialize)_flash).EndInit();
            ResumeLayout(true);
            _flash.HandleCreated += OnFlashHandleCreated;
            if (_flash.IsHandleCreated) OnFlashHandleCreated(this, EventArgs.Empty);
        }

        void OnFlashHandleCreated(object sender, EventArgs e)
        {
            if (_started) return;
            _started = true;
            NoActivateTree(Handle);
            if (_flash != null) NoActivateTree(_flash.Handle);
            LoadPlayer();
            var r = Ready;
            if (r != null) r();
        }

        void LoadPlayer()
        {
            // Always re-read char6.swf from disk (not cached): a rebuild/repatch
            // must take effect on the next reset without restarting the app.
            {
                // char6.swf now has native loadMisc/hideMisc (ground runes).
                // char6-orig.swf is the untouched backup.
                string p = PlayerOverride;
                if (string.IsNullOrEmpty(p)) p = Path.Combine(_baseDir, "char6.swf");
                if (!File.Exists(p)) p = Path.Combine(_baseDir, "flash", "char6.swf");
                if (File.Exists(p)) _char6 = File.ReadAllBytes(p);
                Dbg("player loaded: " + (_char6 != null ? _char6.Length + " bytes from " + p : "NOT FOUND"));
                // Background scenes: every *.swf in flash\ (or the app dir)
                // except the player itself. A new scene also needs its name
                // in BG_SCENES (ui/index.html) to appear in the list.
                _backgrounds.Clear();
                foreach (var dir in new[] { Path.Combine(_baseDir, "flash"), _baseDir })
                {
                    if (!Directory.Exists(dir)) continue;
                    string[] files;
                    try { files = Directory.GetFiles(dir, "*.swf"); }
                    catch { continue; }
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (var fp in files)
                    {
                        string key = Path.GetFileNameWithoutExtension(fp);
                        if (key.Equals("char6", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("char6-orig", StringComparison.OrdinalIgnoreCase)) continue;
                        if (_backgrounds.ContainsKey(key)) continue;
                        try { _backgrounds[key] = File.ReadAllBytes(fp); } catch { }
                    }
                }
                Dbg("backgrounds loaded: " + _backgrounds.Count);
            }
            if (_char6 == null) return;
            try { LoadMovie(_char6); Dbg("LoadMovie done, player running"); } catch (Exception ex) { Dbg("LoadMovie EX: " + ex.Message); }
        }

        // LoadMovie via OcxState (mirrors the classic LoadMovie helper)
        void LoadMovie(byte[] swf)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(8 + swf.Length);
                bw.Write(1432769894);
                bw.Write(swf.Length);
                bw.Write(swf);
                ms.Seek(0, SeekOrigin.Begin);
                ((AxHost)_flash).OcxState = new AxHost.State(ms, 1, false, null);
            }
        }

        // ---- commands driven by the UI ----

        // Character gender as last told to the player ("M"/"F"). Linkage
        // parsing prefers this gender's symbols; item files never change it
        // (the old parser flipped the character to whatever gender a hair or
        // armor file listed first - e.g. a female character wearing a hair
        // from the male folder turned male and lost her armor).
        public string Gender { get; private set; } = "F";

        // Set once the UI pins dagger mode on. While pinned, loading a weapon no
        // longer re-decides it - otherwise the Settings toggle would silently
        // flip back off the moment any non-dagger weapon is loaded.
        public bool DaggerPinned { get; private set; }

        // The game speaks "True"/"False" for every boolean call.
        static bool ParseBool(string s) { return string.Equals(s, "True", StringComparison.OrdinalIgnoreCase); }

        public bool FlashCall(string method, string[] args)
        {
            if (_flash == null) return false;
            args = args ?? new string[0];
            if (method == "setGender" && args.Length > 0 && (args[0] == "M" || args[0] == "F")) Gender = args[0];
            // A daggerMode call arriving from the UI is a deliberate choice, not
            // the auto-detect, so remember it and let it outrank LoadItem.
            if (method == "daggerMode" && args.Length > 0) DaggerPinned = ParseBool(args[0]);
            try { return Call(method, args); } catch { return false; }
        }

        public void LoadItem(string itemType, string itemFile, string weaponType, string link = null)
        {
            if (string.IsNullOrEmpty(itemFile) || !File.Exists(itemFile))
            {
                Dbg("LoadItem " + itemType + " MISSING file: " + itemFile);
                return;
            }
            ItemType type;
            if (!Enum.TryParse(itemType, true, out type)) { Dbg("LoadItem unknown type: " + itemType); return; }
            byte[] bytes;
            try { bytes = File.ReadAllBytes(itemFile); }
            catch (Exception ex) { Dbg("LoadItem read fail: " + ex.Message); return; }
            LoadItemBytes(itemType, bytes, Path.GetFileName(itemFile), weaponType, link);
        }

        // Same as LoadItem but from in-memory bytes (drag-and-drop SWFs that
        // were never saved to disk). link: the CharPage linkage when known;
        // used only if the SWF really exports it, else the SWF is parsed.
        public void LoadItemBytes(string itemType, byte[] bytes, string name, string weaponType, string link = null)
        {
            if (bytes == null || bytes.Length < 4) return;
            ItemType type;
            if (!Enum.TryParse(itemType, true, out type)) { Dbg("LoadItem unknown type: " + itemType); return; }
            try
            {
                string linkage = LinkageParser.Resolve(bytes, type, Gender, link);
                string b64 = Convert.ToBase64String(bytes);
                Dbg("LoadItem " + itemType + " " + name + " size=" + bytes.Length + " base64=" + b64.Length + " linkage=" + (linkage ?? "NULL") + (string.IsNullOrEmpty(link) ? "" : " (charpage " + link + ")"));
                if (type == ItemType.Weapon)
                {
                    // Dual sets (Dagger) split across weapon/weaponOff and leave
                    // the player in dual mode: a later single weapon would then
                    // mirror into the off hand too. Sync daggerMode to the
                    // incoming weapon FIRST so the load attaches correctly.
                    // No type from the CharPage: read it from the SWF itself so a
                    // gauntlet still goes on the hands, a split set on both.
                    // DaggerPinned (the Settings toggle) outranks the auto-detect.
                    if (string.IsNullOrEmpty(weaponType)) weaponType = DetectWeaponType(bytes);
                    bool dual = DaggerPinned || string.Equals(weaponType, "Dagger", StringComparison.OrdinalIgnoreCase);
                    Call("daggerMode", dual ? "True" : "False");
                    if (!string.IsNullOrEmpty(weaponType))
                        Call("loadWeapon", b64, linkage ?? "", weaponType);
                    else
                        Call("loadWeapon", b64, linkage ?? "");
                }
                else
                    Call("load" + type.ToString(), b64, linkage ?? "");
            }
            catch (Exception ex) { Dbg("LoadItem fail: " + ex.Message); }
        }

        // The player now owns helm/hair/back-hair visibility (it tracks the
        // helm's and the hair's own back-hair symbols), so unhiding is a
        // plain call; the old reload-the-helm workaround is gone.
        public void UnhideHelm()
        {
            FlashCall("hideHelm", new[] { "False" });
        }

        // Weapon types that change WHERE the art goes (the game special-cases
        // only these two in AvatarMC.onLoadWeaponComplete):
        //  - Gauntlet: worn on both hands. Gauntlet art picks its fore/back-
        //    hand look by checking whether it sits in "fronthand" or
        //    "backhand", so those clip names appear in its bytecode.
        //  - Dagger (dual wield / split sets): the dual-wield parent check
        //    references "weaponOff".
        // Used when no strWeaponType is known (dropped files, blank CharPage
        // types); "" = ordinary one-handed placement.
        public static string DetectWeaponType(byte[] swf)
        {
            try
            {
                byte[] data = swf;
                if (data.Length > 8 && data[0] == (byte)'C' && data[1] == (byte)'W' && data[2] == (byte)'S')
                {
                    using (var ms = new MemoryStream(data, 8, data.Length - 8))
                    using (var inf = new InflaterInputStream(ms))
                    using (var outMs = new MemoryStream())
                    {
                        inf.CopyTo(outMs);
                        data = outMs.ToArray();
                    }
                }
                if (Contains(data, "fronthand") || Contains(data, "backhand")) return "Gauntlet";
                if (Contains(data, "weaponOff")) return "Dagger";
            }
            catch { }
            return "";
        }

        static bool Contains(byte[] data, string text)
        {
            byte[] needle = Encoding.ASCII.GetBytes(text);
            for (int i = 0; i + needle.Length <= data.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && data[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }

        // Synchronous no-arg query (used for isReady/getStatus polling).
        // Returns "" when the player has no such callback yet (still loading).
        public string QueryFlash(string method)
        {
            string val;
            return TryCall(method, out val) ? val : "";
        }

        // Invoke a player callback and report whether it completed. A false
        // return means the callback is missing or threw inside the player
        // (ExternalInterface surfaces both as a COMException).
        public bool TryCall(string method, out string value, params string[] args)
        {
            value = "";
            if (_flash == null || string.IsNullOrEmpty(method)) return false;
            try
            {
                var node = XElement.Parse(_flash.CallFunction(BuildInvoke(method, args))).FirstNode;
                value = node != null ? System.Net.WebUtility.HtmlDecode(node.ToString()) : "";
                return true;
            }
            catch { return false; }
        }

        static string BuildInvoke(string function, string[] args)
        {
            var sb = new StringBuilder();
            sb.Append("<invoke name=\"").Append(function).Append("\" returntype=\"xml\">");
            if (args != null && args.Length != 0)
            {
                sb.Append("<arguments>");
                foreach (var a in args)
                    sb.Append("<string>").Append(System.Security.SecurityElement.Escape(a ?? "")).Append("</string>");
                sb.Append("</arguments>");
            }
            sb.Append("</invoke>");
            return sb.ToString();
        }

        // Render the current Flash frame into a bitmap (IViewObject::Draw on
        // the OCX), so snapshots work even when the host window is offscreen.
        [ComImport, Guid("0000010d-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IViewObject
        {
            [PreserveSig]
            int Draw(uint dwDrawAspect, int lindex, IntPtr pvAspect, IntPtr ptd, IntPtr hdcTargetDev,
                IntPtr hdcDraw, [In] ref RECTL lprcBounds, IntPtr lprcWBounds, IntPtr pfnContinue, IntPtr dwContinue);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECTL { public int left, top, right, bottom; }

        public Bitmap Snapshot()
        {
            if (_flash == null) return null;
            int w = Math.Max(1, _flash.Width), h = Math.Max(1, _flash.Height);
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var vo = _flash.GetOcx() as IViewObject;
                if (vo == null) return bmp;
                IntPtr hdc = g.GetHdc();
                try
                {
                    var r = new RECTL { left = 0, top = 0, right = w, bottom = h };
                    vo.Draw(1 /* DVASPECT_CONTENT */, -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, hdc, ref r, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                }
                finally { g.ReleaseHdc(hdc); }
            }
            return bmp;
        }

        // A scene name from flash\*.swf, or "#RRGGBB" for a plain color (the
        // color picker's value used to be ignored here, so picking a color
        // kept the previous scene on screen). Anything else clears it.
        public void LoadBackground(string name)
        {
            byte[] bg;
            name = name ?? "";
            if (_backgrounds.TryGetValue(name, out bg))
                try { Call("loadBackground", Convert.ToBase64String(bg)); } catch { }
            else if (name.Length == 7 && name[0] == '#')
            {
                int rgb;
                if (int.TryParse(name.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out rgb))
                    Call("setBackgroundColor", rgb.ToString());
            }
            else
                Call("clearBackground");
        }

        public void ResetFlash()
        {
            try
            {
                _flash.Dispose();
                Controls.Remove(_flash);
                _flash = null;
                _started = false;
                CreateFlash();
                var h = _flash.Handle; // force handle creation so the rebuilt control is ready immediately
            }
            catch { }
        }

        // ---- Flash invoke (mirrors the classic Flash.Call helper) ----
        bool Call(string function, params string[] args)
        {
            // Arguments are XML-escaped: item/character names routinely
            // contain '&' or apostrophes ("Shield & Blade"), and an unescaped
            // one makes the whole invoke malformed so the call never lands.
            string invoke = BuildInvoke(function, args);
            try
            {
                var res = _flash.CallFunction(invoke);
                XElement.Parse(res);   // malformed XML means the invoke never landed
                return true;
            }
            catch (Exception ex)
            {
                // The player's ExternalInterface may not be up yet right after
                // reset/LoadMovie; give it one beat and retry once. E_FAIL also
                // means "the callback threw" - then the player answers isReady
                // and a retry would only repeat the side effects after blocking
                // the UI thread for 150 ms.
                string ready;
                if (ex.Message.Contains("E_FAIL") && !(TryCall("isReady", out ready) && ready == "1"))
                {
                    System.Threading.Thread.Sleep(150);
                    try
                    {
                        var res = _flash.CallFunction(invoke);
                        XElement.Parse(res);
                        return true;
                    }
                    catch (Exception ex2) { Dbg("Call " + function + " EX: " + ex2.Message); return false; }
                }
                Dbg("Call " + function + " EX: " + ex.Message);
                return false;
            }
        }
    }

    enum ItemType : byte
    {
        Hair, Helm, Armor, Cape, Weapon, Pet, Misc
    }

    // LinkageParser: scans SWF tags (SymbolClass tag 76) for item linkage names.
    static class LinkageParser
    {
        static readonly string[] ArmorParts = { "Chest", "Hip", "FootIdle", "Foot", "Shoulder", "Hand", "Thigh", "Shin", "Head", "RobeBack", "Robe" };

        // Linkage to hand the player for this item. A CharPage linkage
        // (strXLink) wins when the SWF really exports it; otherwise the SWF's
        // own symbols decide, preferring the character's gender. Pure: it
        // never changes the character's gender (that comes from strGender).
        public static string Resolve(byte[] swf, ItemType type, string gender, string link)
        {
            var syms = Symbols(swf);
            string g = gender == "M" ? "M" : "F", other = g == "M" ? "F" : "M";
            if (!string.IsNullOrEmpty(link) && Exports(syms, type, link)) return link;
            switch (type)
            {
                case ItemType.Hair:
                    return BaseWithSuffix(syms, g + "Hair") ?? BaseWithSuffix(syms, g + "HairBack")
                        ?? BaseWithSuffix(syms, other + "Hair") ?? BaseWithSuffix(syms, other + "HairBack");
                case ItemType.Armor:
                    foreach (var gg in new[] { g, other })
                        foreach (var part in ArmorParts)
                        {
                            string b = BaseWithSuffix(syms, gg + part);
                            if (b != null) return b;
                        }
                    return null;
                case ItemType.Helm:
                    // Helms with hair (e.g. cmagicianHLocksHat) export an extra
                    // "<base>_backhair" symbol first; attaching that as the helm
                    // puts a locks blob on the face. The player derives
                    // base+"_backhair" itself, so take the first linkage that
                    // is neither _fla nor *backhair/*hairback.
                    foreach (var n in syms)
                        if (!IsFla(n) && !n.EndsWith("backhair", StringComparison.OrdinalIgnoreCase)
                            && !n.EndsWith("hairback", StringComparison.OrdinalIgnoreCase))
                            return n;
                    return syms.Find(n => !IsFla(n));
                default:
                    // Weapons, capes, pets, ground runes: first real symbol
                    // (skip "<name>_fla.*" timeline classes), else the first.
                    return syms.Find(n => !IsFla(n)) ?? (syms.Count > 0 ? syms[0] : null);
            }
        }

        static bool IsFla(string n) { return n.Contains("_fla"); }

        static bool Exports(List<string> syms, ItemType type, string link)
        {
            switch (type)
            {
                case ItemType.Hair:
                    return syms.Exists(n => n == link + "MHair" || n == link + "FHair");
                case ItemType.Armor:
                    return syms.Exists(n => n.StartsWith(link, StringComparison.Ordinal)
                        && (n.Substring(link.Length) == "MChest" || n.Substring(link.Length) == "FChest"));
                default:
                    return syms.Contains(link);
            }
        }

        static string BaseWithSuffix(List<string> syms, string suffix)
        {
            foreach (var n in syms)
                if (n.Length > suffix.Length && n.EndsWith(suffix, StringComparison.Ordinal))
                    return n.Substring(0, n.Length - suffix.Length);
            return null;
        }

        // Every exported class name, in SymbolClass order.
        public static List<string> Symbols(byte[] swf)
        {
            var list = new List<string>();
            var r = Open(swf);
            if (r == null) return list;
            try
            {
                while (r.BaseStream.Position < r.BaseStream.Length)
                {
                    var h = new RecordHeader(r);
                    if (h.TagCode == 0) break;
                    long end = r.BaseStream.Position + h.TagLength;
                    if (h.TagCode == 76)
                    {
                        ushort count = r.ReadUInt16();
                        for (int i = 0; i < count && r.BaseStream.Position < end; i++)
                        {
                            r.ReadUInt16();
                            string name = ReadString(r);
                            if (!string.IsNullOrEmpty(name)) list.Add(name);
                        }
                    }
                    r.BaseStream.Position = end;
                }
            }
            catch { }
            finally { r.Close(); }
            return list;
        }

        static BinaryReader Open(byte[] swf)
        {
            try
            {
                byte[] data = swf;
                if (data.Length > 0 && data[0] == (byte)'C')
                    data = Inflate(swf);
                var ms = new MemoryStream(data);
                ms.Position = FirstTagOffset(data);
                return new BinaryReader(ms, Encoding.UTF8);
            }
            catch { return null; }
        }

        // Walk the SWF header (signature/version/length + bit-packed RECT +
        // frame rate/count) to find where the first tag record begins.
        static int FirstTagOffset(byte[] data)
        {
            if (data.Length < 8) return 8;
            int bitPos = 8 * 8;
            int nBits = ReadBits(data, ref bitPos, 5);
            for (int i = 0; i < 4; i++) ReadBits(data, ref bitPos, nBits);
            bitPos = (bitPos + 7) & ~7;
            int pos = (bitPos >> 3) + 4;
            if (pos < 0 || pos > data.Length) pos = 8;
            return pos;
        }

        static int ReadBits(byte[] d, ref int bitPos, int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++)
            {
                int byteIdx = bitPos >> 3;
                int bitIdx = 7 - (bitPos & 7);
                v = (v << 1) | (byteIdx < d.Length ? ((d[byteIdx] >> bitIdx) & 1) : 0);
                bitPos++;
            }
            return v;
        }

        static byte[] Inflate(byte[] swf)
        {
            int fileLength = BitConverter.ToInt32(swf, 4);
            byte[] outBuf = new byte[fileLength];
            Array.Copy(swf, 0, outBuf, 0, 8);
            using (var ms = new MemoryStream(swf, 8, swf.Length - 8))
            using (var inf = new InflaterInputStream(ms))
            {
                // Stream.Read may return less than asked; a single call could
                // leave the tail (where SymbolClass usually sits) zeroed.
                int off = 8;
                while (off < fileLength)
                {
                    int got = inf.Read(outBuf, off, fileLength - off);
                    if (got <= 0) break;
                    off += got;
                }
            }
            outBuf[0] = (byte)'F';
            return outBuf;
        }

        static string ReadString(BinaryReader r)
        {
            var list = new List<byte>();
            while (true)
            {
                byte b = r.ReadByte();
                if (b == 0) break;
                list.Add(b);
            }
            return Encoding.UTF8.GetString(list.ToArray());
        }

        struct RecordHeader
        {
            public readonly int TagCode;
            public readonly uint TagLength;

            public RecordHeader(BinaryReader r)
            {
                ushort n = r.ReadUInt16();
                TagCode = n >> 6;
                TagLength = (uint)(n - (TagCode << 6));
                if (TagLength == 63)
                    TagLength = r.ReadUInt32();
            }
        }
    }
}
