using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ManikinApp
{
    // Manikin.exe --headless [options]
    //
    // Runs the real app (WebView2 page + Flash ActiveX + char6 player) in an
    // offscreen window with no overlays or dialogs, executes the render
    // regression suite below, writes test-results\report.json/.md plus PNG
    // snapshots, and exits 0 (all passed) / 1 (failures) / 2 (could not run).
    //
    //   --out <dir>        results folder        (default <exe>\test-results)
    //   --fixtures <dir>   item SWF cache        (default <exe>\test-fixtures)
    //   --player <swf>     player to test        (default flash\char6.swf)
    //   --only <a,b>       run only tests whose name contains one of these
    //   --offline          skip tests that need game.aq.com
    //   --timeout <sec>    whole-run watchdog    (default 600)
    //   --char <name>      also load this live character and save
    //                      snapshots\char_<name>.png (test live_char_snapshot)
    static class HeadlessRunner
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int pid);

        public static bool Requested(string[] args)
        {
            return args != null && args.Any(a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase));
        }

        public static int Run(string[] args)
        {
            // WinExe has no console of its own: borrow the parent's so the
            // summary shows up in the terminal that launched the run.
            try
            {
                AttachConsole(-1);
                var o = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(o);
            }
            catch { }

            var opt = HeadlessOptions.Parse(args);
            AppForm.Headless = true;
            if (!string.IsNullOrEmpty(opt.Player)) FlashCore.PlayerOverride = Path.GetFullPath(opt.Player);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            int code = 2;
            var form = new AppForm();
            var watchdog = new Timer { Interval = opt.TimeoutSec * 1000 };
            watchdog.Tick += (s, e) =>
            {
                watchdog.Stop();
                Console.WriteLine("HEADLESS: timed out after " + opt.TimeoutSec + "s");
                code = 2;
                form.Close();
            };
            form.Shown += async (s, e) =>
            {
                watchdog.Start();
                try { code = await new HeadlessSuite(form, opt).RunAsync(); }
                catch (Exception ex) { Console.WriteLine("HEADLESS: crashed: " + ex); code = 2; }
                watchdog.Stop();
                form.Close();
            };
            Application.Run(form);
            return code;
        }
    }

    class HeadlessOptions
    {
        public string OutDir, FixtureDir, Player, Char;
        public string[] Only = new string[0];
        public bool Offline;
        public int TimeoutSec = 600;

        public static HeadlessOptions Parse(string[] args)
        {
            string exe = AppDomain.CurrentDomain.BaseDirectory;
            var o = new HeadlessOptions
            {
                OutDir = Path.Combine(exe, "test-results"),
                FixtureDir = Path.Combine(exe, "test-fixtures")
            };
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a)
                {
                    case "--out": o.OutDir = next; i++; break;
                    case "--fixtures": o.FixtureDir = next; i++; break;
                    case "--player": o.Player = next; i++; break;
                    case "--only": o.Only = (next ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries); i++; break;
                    case "--offline": o.Offline = true; break;
                    case "--char": o.Char = next; i++; break;
                    case "--timeout": int.TryParse(next, out o.TimeoutSec); i++; break;
                }
            }
            if (o.TimeoutSec <= 0) o.TimeoutSec = 600;
            return o;
        }
    }

    class TestFailure : Exception
    {
        public TestFailure(string m) : base(m) { }
    }

    class HeadlessSuite
    {
        readonly AppForm _form;
        readonly HeadlessOptions _opt;
        readonly List<string> _log = new List<string>();
        string _snapDir;

        // Fixture items (paths relative to the game file root, exactly as a
        // CharPage lists them). Picked for what they exercise:
        const string ArmorRobeF = "classes/F/ccsephA.swf";          // Robe + RobeBack
        const string ArmorRobeOnlyF = "classes/F/BeleenSXY.swf";    // Robe, no RobeBack
        const string ArmorRobeM = "classes/M/PalidanRevamp.swf";    // Robe + RobeBack
        const string ArmorPlainM = "classes/M/warrior2a_skin.swf";  // no robe pieces
        const string ArmorF2 = "classes/F/InfinityAlphaPirateGoldDragon.swf";
        const string HairBackF = "hair/F/Alina3.swf";               // Hair + HairBack
        const string HairM = "hair/M/Normal.swf";
        const string HairMaleOnly = "hair/M/DFWarStyle.swf";        // only an MHair symbol
        const string HelmBackhair = "items/helms/cmagicianHLocksHat.swf"; // helm + _backhair
        const string HelmPlain = "items/helms/RevGenHood1.swf";
        const string Dagger = "items/daggers/mugCysero.swf";
        const string Sword = "items/swords/sword01.swf";
        const string Cape = "items/capes/PalidanRevampCape.swf";
        const string Pet = "items/pets/4up-29Aug16.swf";
        const string Gauntlet = "items/gauntlets/FClericGOrb.swf";  // Inferna Glass (wiki: Type Gauntlet)

        public HeadlessSuite(AppForm form, HeadlessOptions opt) { _form = form; _opt = opt; }

        FlashCore Flash { get { return _form.Flash; } }

        void Log(string s)
        {
            _log.Add(s);
            Console.WriteLine(s);
        }

        // ---------------------------------------------------------------- run

        public async Task<int> RunAsync()
        {
            Directory.CreateDirectory(_opt.OutDir);
            _snapDir = Path.Combine(_opt.OutDir, "snapshots");
            Directory.CreateDirectory(_snapDir);
            foreach (var f in Directory.GetFiles(_snapDir, "*.png")) try { File.Delete(f); } catch { }

            Log("Manikin headless run " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Log("player: " + (FlashCore.PlayerOverride ?? "flash\\char6.swf"));
            if (!await WaitUntil(() => _form.ReadyForTests && Flash != null, 60000))
            {
                Log("HEADLESS: page/player never came up (WebView2 or Flash ActiveX missing?)");
                return 2;
            }
            if (!await WaitPlayerReady())
            {
                Log("HEADLESS: player has no isReady/getAvatarState callbacks - wrong char6.swf?");
                string boot = Flash.QueryFlash("getBootError");
                if (!string.IsNullOrEmpty(boot)) Log("HEADLESS: player boot error: " + boot);
                return 2;
            }

            var tests = new List<Tuple<string, bool, Func<Task>>>
            {
                // name, needs network (fixtures may already be cached), body
                T("armor_every_piece_attached", true, ArmorEveryPiece),
                T("armor_swap_clears_stale_robe", true, ArmorSwapClearsRobe),
                T("armor_swap_male_plain_hides_robe", true, ArmorSwapMalePlain),
                T("walk_front_foot_uses_armor", true, WalkFrontFoot),
                T("walk_front_foot_hidden_armor", true, WalkFrontFootHiddenArmor),
                T("color_dark_shade_channels", true, ColorDarkChannels),
                T("color_update_recolors_live", true, ColorUpdateLive),
                T("emote_labels_all_play", false, EmoteLabelsPlay),
                T("emote_rest_keeps_head", true, EmoteRestKeepsHead),
                T("emote_unsheath_keeps_head", true, EmoteUnsheathKeepsHead),
                T("emote_stern_loop_count_stable", false, EmoteSternLoops),
                T("hair_backhair_from_hair_file", true, HairBackhair),
                T("helm_hide_shows_hair", true, HelmHideShowsHair),
                T("helm_backhair_survives_late_hair", true, HelmBackhairLateHair),
                T("resize_keeps_facing", false, ResizeKeepsFacing),
                T("dagger_then_sword", true, DaggerThenSword),
                T("dagger_attack_both_hands", true, DaggerAttackBothHands),
                T("gauntlet_worn_on_hands", true, GauntletWornOnHands),
                T("gauntlet_detected_without_type", true, GauntletDetected),
                T("emote_panel_has_all_combat_anims", false, EmotePanelCombatAnims),
                T("armor_back_hand_silhouette", true, BackHandSilhouette),
                T("pet_resize_keeps_facing", true, PetResizeKeepsFacing),
                T("closeuii_before_misc_name", false, CloseUiiBeforeMisc),
                T("flash_call_escapes_names", false, FlashCallEscapes),
                T("ui_hide_checkboxes_toggle", true, UiHideCheckboxes),
                T("ui_vars_apply_colors_gender", true, UiVarsColorsGender),
                T("ui_background_color_clears_scene", false, UiBackgroundColor),
                T("ui_layout_no_clipping", false, UiLayout),
                T("ui_title_bar_color_option", false, UiTitleBar),
                T("ui_empty_background", false, UiEmptyBackground),
                T("ui_gradient_background", false, UiGradientBackground),
                T("ui_wardrobe_catalog", false, UiWardrobeCatalog),
                T("live_wardrobe_try_on", true, LiveWardrobeTryOn),
                T("dagger_offhand_follows_unarmed", true, DaggerOffhandFollowsUnarmed),
                T("ui_wardrobe_details_toggle", false, UiWardrobeDetails),
                T("live_wardrobe_gender_switch", true, LiveWardrobeGender),
                T("ui_monitor_no_side_scroll", false, UiMonitorScroll),
                T("ui_names_follow_outfit", true, UiNamesFollowOutfit),
                T("name_tag_over_avatar", true, NameTagOverAvatar),
                T("ui_copy_log_button", false, UiCopyLog),
                T("ui_gear_icons_sync_checkboxes", true, UiGearIconSync),
                T("loader_line_avoids_gear_list", false, LoaderPlacement),
                T("downloads_cache_keeps_gender_paths", false, DownloadsCachePaths),
                T("linkage_parser_prefers_gender", true, LinkagePrefersGender),
                T("live_charpage_roundtrip", true, LiveCharPage),
                T("live_load_keeps_ui_responsive", true, LiveLoadKeepsUiResponsive),
                T("live_char_snapshot", true, LiveCharSnapshot),
            };

            var results = new JArray();
            int pass = 0, fail = 0, skip = 0;
            foreach (var t in tests)
            {
                string name = t.Item1;
                if (_opt.Only.Length > 0 && !_opt.Only.Any(x => name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                var r = new JObject { { "name", name } };
                var sw = Stopwatch.StartNew();
                _notes = new List<string>();
                if (name.StartsWith("live_") && _opt.Offline)
                {
                    r["status"] = "skip"; r["detail"] = "offline"; skip++;
                    Log("SKIP " + name + " (offline)");
                    results.Add(r);
                    continue;
                }
                try
                {
                    await ResetPlayer();
                    await t.Item3();
                    r["status"] = "pass"; pass++;
                    Log("PASS " + name + " (" + sw.ElapsedMilliseconds + " ms)");
                }
                catch (Exception ex)
                {
                    bool skipped = ex is FixtureUnavailable;
                    r["status"] = skipped ? "skip" : "fail";
                    r["detail"] = ex is TestFailure || skipped ? ex.Message : ex.ToString();
                    if (skipped) skip++; else fail++;
                    Log((skipped ? "SKIP " : "FAIL ") + name + ": " + ex.Message);
                    try { Snap("FAIL_" + name); } catch { }
                }
                r["ms"] = sw.ElapsedMilliseconds;
                if (_notes.Count > 0) r["notes"] = new JArray(_notes);
                results.Add(r);
            }

            Log(string.Format("RESULT: {0} passed, {1} failed, {2} skipped", pass, fail, skip));
            var report = new JObject
            {
                { "when", DateTime.Now.ToString("o") },
                { "player", FlashCore.PlayerOverride ?? "flash\\char6.swf" },
                { "passed", pass }, { "failed", fail }, { "skipped", skip },
                { "tests", results }
            };
            File.WriteAllText(Path.Combine(_opt.OutDir, "report.json"), report.ToString(Formatting.Indented));
            File.WriteAllText(Path.Combine(_opt.OutDir, "report.md"), ToMarkdown(report));
            return fail == 0 ? 0 : 1;
        }

        static Tuple<string, bool, Func<Task>> T(string n, bool net, Func<Task> f) { return Tuple.Create(n, net, f); }

        List<string> _notes = new List<string>();
        void Note(string s) { _notes.Add(s); }

        static string ToMarkdown(JObject rep)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Manikin headless test report");
            sb.AppendLine();
            sb.AppendLine("- When: " + rep["when"]);
            sb.AppendLine("- Player: `" + rep["player"] + "`");
            sb.AppendLine("- Result: **" + rep["passed"] + " passed, " + rep["failed"] + " failed, " + rep["skipped"] + " skipped**");
            sb.AppendLine();
            sb.AppendLine("| Test | Status | ms | Detail |");
            sb.AppendLine("|---|---|---|---|");
            foreach (JObject t in rep["tests"])
            {
                string d = ((string)t["detail"] ?? "").Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
                if (d.Length > 300) d = d.Substring(0, 300) + "...";
                sb.AppendLine("| " + t["name"] + " | " + t["status"] + " | " + t["ms"] + " | " + d + " |");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------ helpers

        static async Task<bool> WaitUntil(Func<bool> cond, int ms, int step = 50)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                bool ok = false;
                try { ok = cond(); } catch { }
                if (ok) return true;
                await Task.Delay(step);
            }
            try { return cond(); } catch { return false; }
        }

        async Task<bool> WaitPlayerReady()
        {
            return await WaitUntil(() => Flash.QueryFlash("isReady") == "1" && State() != null, 20000, 100);
        }

        async Task ResetPlayer()
        {
            Flash.ResetFlash();
            if (!await WaitPlayerReady()) throw new TestFailure("player did not come back after reset");
            // Let the rig settle on its Idle frame before driving it.
            await Task.Delay(150);
        }

        JObject State()
        {
            string s = Flash.QueryFlash("getAvatarState");
            if (string.IsNullOrEmpty(s)) return null;
            try { return JObject.Parse(s); } catch { return null; }
        }

        JObject MustState()
        {
            var s = State();
            if (s == null) throw new TestFailure("getAvatarState returned nothing");
            return s;
        }

        void Call(string method, params string[] args)
        {
            string v;
            if (!Flash.TryCall(method, out v, args))
                throw new TestFailure("player call " + method + "(" + string.Join(",", args) + ") threw");
        }

        class FixtureUnavailable : Exception { public FixtureUnavailable(string m) : base(m) { } }

        async Task<string> Fixture(string rel)
        {
            string local = Path.Combine(_opt.FixtureDir, rel.Replace('/', '\\'));
            if (File.Exists(local) && new FileInfo(local).Length > 0) return local;
            if (_opt.Offline) throw new FixtureUnavailable("fixture not cached (offline): " + rel);
            Directory.CreateDirectory(Path.GetDirectoryName(local));
            if (!await Downloads.DownloadAsync(Downloads.GAME + rel, local))
                throw new FixtureUnavailable("could not download fixture " + rel);
            return local;
        }

        static string[] Kids(JObject st, string part)
        {
            var p = st["parts"][part] as JObject;
            if (p == null) return new string[0];
            return p["kids"].Select(k => (string)k).ToArray();
        }

        static bool Visible(JObject st, string part)
        {
            var p = st["parts"][part] as JObject;
            return p != null && (bool)p["v"];
        }

        static void Expect(bool cond, string msg)
        {
            if (!cond) throw new TestFailure(msg);
        }

        async Task Load(string type, string rel, string weaponType = null)
        {
            string path = await Fixture(rel);
            Flash.LoadItem(type, path, weaponType);
        }

        async Task SetGender(string g)
        {
            Flash.FlashCall("setGender", new[] { g });
            await Task.Yield();
        }

        async Task<JObject> WaitState(Func<JObject, bool> cond, string what, int ms = 8000)
        {
            JObject last = null;
            bool ok = await WaitUntil(() => { last = State(); return last != null && cond(last); }, ms, 50);
            if (!ok) throw new TestFailure("timed out waiting for " + what + "; last state: " + Short(last));
            return last;
        }

        static string Short(JObject st)
        {
            if (st == null) return "(none)";
            var s = st.ToString(Formatting.None);
            return s.Length > 1500 ? s.Substring(0, 1500) + "..." : s;
        }

        static bool Has(IEnumerable<string> kids, string prefix)
        {
            return kids.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
        }

        async Task<JObject> LoadArmor(string gender, string rel, string link)
        {
            await SetGender(gender);
            await Load("Armor", rel);
            return await WaitState(s => Has(Kids(s, "chest"), link + gender + "Chest"), "armor " + link);
        }

        void Snap(string name)
        {
            using (var bmp = Flash.Snapshot())
            {
                if (bmp == null) return;
                bmp.Save(Path.Combine(_snapDir, name + ".png"), ImageFormat.Png);
            }
        }

        // -------------------------------------------------------------- tests

        static readonly string[] PieceParts =
        {
            "chest:Chest", "hip:Hip", "idlefoot:FootIdle", "backfoot:Foot", "frontfoot:Foot",
            "frontshoulder:Shoulder", "backshoulder:Shoulder", "fronthand:Hand", "backhand:Hand",
            "frontthigh:Thigh", "backthigh:Thigh", "frontshin:Shin", "backshin:Shin",
            "robe:Robe", "backrobe:RobeBack"
        };

        // Wrong armor layering: every rig slot must hold exactly the matching
        // armor symbol (one child, no leftovers stacked on top).
        async Task ArmorEveryPiece()
        {
            var st = await LoadArmor("F", ArmorRobeF, "ccsephA");
            await Task.Delay(200);
            st = MustState();
            var bad = new List<string>();
            foreach (var pp in PieceParts)
            {
                var a = pp.Split(':');
                var kids = Kids(st, a[0]);
                string want = "ccsephAF" + a[1];
                if (kids.Length != 1 || !kids[0].StartsWith(want))
                    bad.Add(a[0] + "=[" + string.Join(",", kids) + "] want " + want);
            }
            Expect(Visible(st, "robe") && Visible(st, "backrobe"), "robe/backrobe should be visible for a robed armor");
            var head = Kids(st, "head");
            Expect(head.Length > 0 && head[0].StartsWith("ccsephAFHead"), "head face should be the armor head, got " + string.Join(",", head));
            Snap("armor_ccsephA_idle");
            Expect(bad.Count == 0, "armor pieces wrong: " + string.Join("; ", bad));
        }

        // Swapping to an armor without a RobeBack must not leave the previous
        // armor's back robe drawn behind the new one.
        async Task ArmorSwapClearsRobe()
        {
            await LoadArmor("F", ArmorRobeF, "ccsephA");
            var st = await LoadArmor("F", ArmorRobeOnlyF, "BeleenSXY");
            await Task.Delay(200);
            st = MustState();
            Expect(Has(Kids(st, "robe"), "BeleenSXYFRobe") && Visible(st, "robe"), "robe should be BeleenSXYFRobe");
            bool staleBack = Visible(st, "backrobe") && Has(Kids(st, "backrobe"), "ccsephA");
            Snap("armor_swap_beleen");
            Expect(!staleBack, "stale back robe from previous armor still visible: " + string.Join(",", Kids(st, "backrobe")));
        }

        async Task ArmorSwapMalePlain()
        {
            await LoadArmor("M", ArmorRobeM, "PalidanRevamp");
            var st = await LoadArmor("M", ArmorPlainM, "Warrior2a");
            await Task.Delay(200);
            st = MustState();
            Expect(!Visible(st, "robe"), "robe should be hidden for a robe-less armor, has " + string.Join(",", Kids(st, "robe")));
            Expect(!Visible(st, "backrobe"), "backrobe should be hidden for a robe-less armor, has " + string.Join(",", Kids(st, "backrobe")));
        }

        // Right (front) foot during Walk must be the armor's Foot symbol.
        async Task WalkFrontFoot()
        {
            await LoadArmor("F", ArmorRobeF, "ccsephA");
            Call("loadEmote", "Walk");
            var st = await WaitState(s => (string)s["label"] == "Walk" && Visible(s, "frontfoot"), "Walk with front foot shown");
            await Task.Delay(120);
            st = MustState();
            Snap("walk_ccsephA");
            var kids = Kids(st, "frontfoot");
            Expect(!Visible(st, "idlefoot"), "idle foot should be hidden while walking");
            Expect(kids.Length == 1 && kids[0].StartsWith("ccsephAFFoot"), "front foot during Walk shows [" + string.Join(",", kids) + "], want ccsephAFFoot");
            Expect(Has(Kids(st, "backfoot"), "ccsephAFFoot"), "back foot should be the armor foot too");
            // Back to idle: front foot hidden again.
            Call("loadEmote", "Idle");
            await WaitState(s => (string)s["label"] == "Idle" && !Visible(s, "frontfoot") && Visible(s, "idlefoot"), "Idle foot restored");
        }

        // hideArmor must also keep the walking front foot hidden.
        async Task WalkFrontFootHiddenArmor()
        {
            await LoadArmor("F", ArmorRobeF, "ccsephA");
            Call("hideArmor", "True");
            Call("loadEmote", "Walk");
            var st = await WaitState(s => (string)s["label"] == "Walk", "Walk");
            await Task.Delay(200);
            st = MustState();
            Expect(!Visible(st, "frontfoot"), "front foot visible while armor is hidden");
            Call("hideArmor", "False");
            await Task.Delay(150);
            st = MustState();
            Expect(Visible(st, "frontfoot") && Visible(st, "chest"), "armor/front foot should return after unhide while walking");
        }

        static int Ch(int rgb, int shift) { return (rgb >> shift) & 0xFF; }

        // Expected dye transform, per the live game's AvatarMC.changeColor.
        static int[] ExpectedOffsets(int rgb, string loc, string shade)
        {
            int r = Ch(rgb, 16), g = Ch(rgb, 8), b = Ch(rgb, 0);
            switch ((shade ?? "").ToUpperInvariant())
            {
                case "LIGHT": r += 100; g += 100; b += 100; break;
                case "DARK": r -= loc == "Skin" ? 25 : 50; g -= 50; b -= 50; break;
                case "DARKER": r -= 125; g -= 125; b -= 125; break;
            }
            return new[] { r, g, b };
        }

        List<string> CheckColors(JObject st)
        {
            var bad = new List<string>();
            var colors = st["colors"] as JObject;
            foreach (JObject c in st["colored"])
            {
                string loc = (string)c["loc"], shade = (string)c["shade"];
                if (colors == null || colors[loc] == null) { bad.Add("no color for " + loc); continue; }
                int rgb = (int)Convert.ToDouble((string)colors[loc].ToString());
                var want = ExpectedOffsets(rgb, loc, shade);
                int r = (int)(double)c["r"], g = (int)(double)c["g"], b = (int)(double)c["b"];
                if (r != want[0] || g != want[1] || b != want[2] || (double)c["m"] != 0)
                    bad.Add(loc + "/" + shade + " got (" + r + "," + g + "," + b + ") want (" + string.Join(",", want) + ")");
            }
            return bad;
        }

        // Dye channels: every colored clip must carry its location's color
        // with the game's shade offsets (Dark is -50 red except on Skin).
        // Armors that dye Base/Trim/Accessory with shades (most modern art
        // only dyes Skin/Eye); the first one with a non-skin Dark shade wins.
        static readonly string[][] DyedArmors =
        {
            new[] { "M", ArmorPlainM, "Warrior2a" },
            new[] { "M", "classes/M/zhoom_skin.swf", "Zhoom" },
            new[] { "M", "classes/M/NewCysero.swf", "NewCysero" },
            new[] { "F", ArmorRobeOnlyF, "BeleenSXY" },
            new[] { "M", ArmorRobeM, "PalidanRevamp" },
        };

        async Task ColorDarkChannels()
        {
            JObject st = null;
            List<string> shades = null;
            foreach (var a in DyedArmors)
            {
                await ResetPlayer();
                // Hair art carries the Hair/Dark shades most armors lack.
                await SetGender(a[0]);
                await Load("Hair", a[0] == "M" ? HairM : HairBackF);
                await LoadArmor(a[0], a[1], a[2]);
                await Task.Delay(300);
                foreach (var kv in new Dictionary<string, int> {
                    { "Hair", 0x336699 }, { "Skin", 0xE0B090 }, { "Eye", 0x2244AA },
                    { "Trim", 0xCC3311 }, { "Base", 0x88AA22 }, { "Accessory", 0x7722CC } })
                    Call(kv.Key, kv.Value.ToString());
                await Task.Delay(150);
                st = MustState();
                shades = st["colored"].Select(c => (string)c["loc"] + "/" + (string)c["shade"]).Distinct().ToList();
                Note(a[2] + " colored clips: " + string.Join(" ", shades));
                if (shades.Any(s => s.EndsWith("/Dark") && !s.StartsWith("Skin"))) { Snap("colors_" + a[2]); break; }
            }
            Expect(shades.Any(s => s.EndsWith("/Dark") && !s.StartsWith("Skin")), "no fixture armor has non-skin Dark clips to check");
            var bad = CheckColors(st);
            Expect(bad.Count == 0, "dye mismatch: " + string.Join("; ", bad.Distinct().Take(12)));
        }

        // A color change after load must recolor existing clips.
        async Task ColorUpdateLive()
        {
            await LoadArmor("F", ArmorRobeF, "ccsephA");
            await Task.Delay(200);
            Call("Base", 0x00FF00.ToString());
            Call("Trim", 0x0000FF.ToString());
            await Task.Delay(100);
            var st = MustState();
            var bad = CheckColors(st);
            Expect(bad.Count == 0, "after live recolor: " + string.Join("; ", bad.Distinct().Take(12)));
        }

        // Same list the Emotes panel offers.
        static readonly string[] UiEmotes = { "Idle", "Dance", "Dance2", "Laugh", "Point", "Wave", "Bow", "Cheer", "Salute", "Salute2", "Cry", "Cry2", "Sleep", "Rest", "Kneel", "Punt", "Unsheath", "Thrash", "Facepalm", "Airguitar", "Backflip", "Headbang", "Stepdance", "Samba", "Spar", "Dazed", "Dodge", "Powerup", "Jumpcheer", "Psychic1", "Psychic2", "Throw", "FireBreath", "Swordplay", "Feign", "Dead", "Stern", "Jump", "Attack1", "Attack2",
            "Use", "Danceweapon", "Useweapon",
            // Only in the game's newer skeleton (transplanted by build_char6.py):
            "Card", "WhipAttack", "GunAttack", "GunAttack2", "RifleAttack", "RifleAttack2", "RangedAttack3", "UnarmedAttack3" };

        async Task EmoteLabelsPlay()
        {
            var bad = new List<string>();
            // Everything the Emotes panel offers, plus the fixed list.
            var list = new List<string>(UiEmotes);
            try
            {
                var page = JArray.Parse(JsonConvert.DeserializeObject<string>(await Js("JSON.stringify(Object.values(EMOTE_GROUPS).flat())")));
                foreach (var e in page) if (!list.Contains((string)e)) list.Add((string)e);
            }
            catch { bad.Add("could not read the page's emote list"); }
            Note(list.Count + " emotes checked");
            foreach (var e in list)
            {
                Call("loadEmote", e);
                await Task.Delay(90);
                var a = MustState();
                string lab = (string)a["label"];
                if (lab == null || !lab.StartsWith(e)) { bad.Add(e + "->" + lab); continue; }
                if (e == "Idle") continue;
                await Task.Delay(160);
                var b = MustState();
                // Advancing (or legitimately parked on a stop() frame of the same emote).
                // Stances (…Fight) and Dead/Feign legitimately stop on their last frame.
                if ((int)b["frame"] == (int)a["frame"] && (string)b["label"] == lab && e != "Dead" && e != "Feign" && !e.EndsWith("Fight"))
                    bad.Add(e + " frozen at " + b["frame"]);
                if (!(bool)b["headOk"]) bad.Add(e + " lost head");
            }
            Expect(bad.Count == 0, "emotes: " + string.Join(", ", bad));
        }

        async Task EmoteKeepsHead(string emote, string snapName)
        {
            await SetGender("F");
            await Load("Hair", HairBackF);
            await Load("Helm", HelmPlain);
            await LoadArmor("F", ArmorRobeF, "ccsephA");
            await WaitState(s => s["helmHead"] != null && Has(s["helmHead"]["kids"].Select(k => (string)k), "RevGenHood1"), "helm");
            Call("loadEmote", emote);
            var sw = Stopwatch.StartNew();
            var problems = new List<string>();
            bool snapped = false;
            while (sw.ElapsedMilliseconds < 2600)
            {
                var st = MustState();
                string lab = (string)st["label"];
                var face = st["parts"]["head"] as JObject;
                var faceKids = face == null ? new string[0] : face["kids"].Select(k => (string)k).ToArray();
                if (!(bool)st["headOk"]) problems.Add(lab + "@" + st["frame"] + " head detached");
                else if (!Has(faceKids, "ccsephAFHead")) problems.Add(lab + "@" + st["frame"] + " face=" + string.Join(",", faceKids));
                if (!snapped && lab == emote && sw.ElapsedMilliseconds > 250) { Snap(snapName); snapped = true; }
                await Task.Delay(30);
            }
            var end = MustState();
            Expect(problems.Count == 0, emote + ": " + string.Join("; ", problems.Distinct().Take(6)));
            // After the emote the head must still be the live, dressed one.
            Expect(end["helmHead"] != null && Has(end["helmHead"]["kids"].Select(k => (string)k), "RevGenHood1"), "helm gone after " + emote);
            Call("hideHelm", "True");
            await Task.Delay(60);
            var hid = MustState();
            Expect(!(bool)hid["helm"]["v"], "hideHelm after " + emote + " did not reach the visible head");
        }

        Task EmoteRestKeepsHead() { return EmoteKeepsHead("Rest", "emote_rest"); }
        Task EmoteUnsheathKeepsHead() { return EmoteKeepsHead("Unsheath", "emote_unsheath"); }

        // Stern loops its SternLoop section 3 times - but only if the loop
        // counter starts at 0. Measure loop-backs fresh and after Laugh.
        async Task<int> CountSternLoopBacks()
        {
            Call("loadEmote", "Stern");
            int backs = 0, last = -1;
            var sw = Stopwatch.StartNew();
            bool seenLoop = false;
            while (sw.ElapsedMilliseconds < 6000)
            {
                var st = MustState();
                string lab = (string)st["label"];
                int f = (int)st["frame"];
                if (lab == "SternLoop")
                {
                    seenLoop = true;
                    if (last >= 0 && f < last) backs++;
                    last = f;
                }
                else if (seenLoop) break;
                await Task.Delay(15);
            }
            return backs;
        }

        async Task EmoteSternLoops()
        {
            int fresh = await CountSternLoopBacks();
            Call("loadEmote", "Laugh");
            await Task.Delay(1800); // let Laugh run its own 3 loops
            int afterLaugh = await CountSternLoopBacks();
            Note("SternLoop loop-backs fresh=" + fresh + " afterLaugh=" + afterLaugh);
            Expect(fresh == 2, "fresh Stern should loop back twice (3 plays), got " + fresh);
            Expect(afterLaugh == fresh, "Stern loop count depends on previous emote: fresh " + fresh + " vs after Laugh " + afterLaugh);
        }

        async Task HairBackhair()
        {
            await SetGender("F");
            await Load("Hair", HairBackF);
            var st = await WaitState(s => s["hair"] != null && Has(s["hair"]["kids"].Select(k => (string)k), "Alina3FHair"), "hair");
            await Task.Delay(150);
            st = MustState();
            Snap("hair_alina3");
            Expect(Visible(st, "backhair") && Has(Kids(st, "backhair"), "Alina3FHairBack"),
                "back hair should be Alina3FHairBack and visible, got v=" + Visible(st, "backhair") + " [" + string.Join(",", Kids(st, "backhair")) + "]");
        }

        async Task HelmHideShowsHair()
        {
            await SetGender("F");
            await Load("Hair", HairBackF);
            await WaitState(s => Has(s["hair"]["kids"].Select(k => (string)k), "Alina3FHair"), "hair");
            await Load("Helm", HelmBackhair);
            var st = await WaitState(s => s["helmHead"] != null && Has(s["helmHead"]["kids"].Select(k => (string)k), "cmagicianHLocksHat"), "helm");
            await Task.Delay(100);
            st = MustState();
            Expect((bool)st["helm"]["v"] && !(bool)st["hair"]["v"], "helm on: helm visible, hair hidden");
            Expect(Has(Kids(st, "backhair"), "cmagicianHLocksHat_backhair") && Visible(st, "backhair"), "helm on: back hair should be the helm's locks");
            Call("hideHelm", "True");
            await Task.Delay(100);
            st = MustState();
            Snap("helm_hidden_hair");
            Expect(!(bool)st["helm"]["v"], "helm hidden");
            Expect((bool)st["hair"]["v"], "hair must show when the helm is hidden (bald head otherwise)");
            Expect(Visible(st, "backhair") && Has(Kids(st, "backhair"), "Alina3FHairBack"),
                "hair's own back hair must show when the helm is hidden, got v=" + Visible(st, "backhair") + " [" + string.Join(",", Kids(st, "backhair")) + "]");
            Call("hideHelm", "False");
            await Task.Delay(100);
            st = MustState();
            Expect((bool)st["helm"]["v"] && !(bool)st["hair"]["v"], "helm back on: helm visible, hair hidden");
            Expect(Has(Kids(st, "backhair"), "cmagicianHLocksHat_backhair") && Visible(st, "backhair"), "helm back on: helm locks restored");
        }

        // Hair finishing after the helm (download order is not guaranteed)
        // must not put the hair back over a worn helm.
        async Task HelmBackhairLateHair()
        {
            await SetGender("F");
            await Load("Helm", HelmBackhair);
            await WaitState(s => s["helmHead"] != null && Has(s["helmHead"]["kids"].Select(k => (string)k), "cmagicianHLocksHat"), "helm");
            await Load("Hair", HairBackF);
            await WaitState(s => Has(s["hair"]["kids"].Select(k => (string)k), "Alina3FHair"), "hair");
            await Task.Delay(150);
            var st = MustState();
            Expect(!(bool)st["hair"]["v"], "hair drawn over the helm after a late hair load");
            Expect(Has(Kids(st, "backhair"), "cmagicianHLocksHat_backhair") && Visible(st, "backhair"),
                "helm locks replaced by hair back after a late hair load: [" + string.Join(",", Kids(st, "backhair")) + "]");
        }

        // Walking turns the avatar (negative scaleX); resizing must keep that
        // facing instead of snapping back. setFacing drives the same flip the
        // walk code does (AvatarMC.turn), without needing a real mouse.
        async Task ResizeKeepsFacing()
        {
            Call("loadResize", "1");
            var st = MustState();
            double sx0 = (double)st["scaleX"];
            Call("setFacing", "toggle");
            st = await WaitState(s => Math.Sign((double)s["scaleX"]) != Math.Sign(sx0), "avatar to turn", 2000);
            Call("loadResize", "1.5");
            await Task.Delay(50);
            var st2 = MustState();
            Expect(Math.Sign((double)st2["scaleX"]) == Math.Sign((double)st["scaleX"]),
                "loadResize flipped the avatar: scaleX " + st["scaleX"] + " -> " + st2["scaleX"]);
            Expect(Math.Abs(Math.Abs((double)st2["scaleX"]) - 1.5) < 0.001 && Math.Abs((double)st2["scaleY"] - 1.5) < 0.001, "scale not applied");
        }

        async Task DaggerThenSword()
        {
            await Load("Weapon", Dagger, "Dagger");
            var st = await WaitState(s => Has(Kids(s, "weapon"), "mugCysero") && Has(Kids(s, "weaponOff"), "mugCysero"), "dagger in both hands");
            Expect(Visible(st, "weaponOff"), "off-hand dagger visible");
            await Load("Weapon", Sword, "Sword");
            st = await WaitState(s => !Has(Kids(s, "weapon"), "mugCysero"), "dagger removed");
            await Task.Delay(100);
            st = MustState();
            Expect(!Visible(st, "weaponOff"), "off hand should be hidden for a single sword");
            Expect(!Has(Kids(st, "weaponOff"), "mugCysero"), "stale dagger in off hand");
        }

        // The far limbs are black silhouettes; the skeleton's timeline does
        // that for all of them except the back hand, which the game blacks
        // out in code. The front hand keeps its colors.
        async Task BackHandSilhouette()
        {
            var st = await LoadArmor("F", ArmorRobeF, "ccsephA");
            await Task.Delay(100);
            st = MustState();
            double back = (double)st["parts"]["backhand"]["m0"], front = (double)st["parts"]["fronthand"]["m0"];
            Expect(back == 0, "back hand armor piece should be blacked out (red multiplier 0), got " + back);
            Expect(front == 1, "front hand must keep its colors, got multiplier " + front);
        }

        async Task PetResizeKeepsFacing()
        {
            await Load("Pet", Pet);
            var st = await WaitState(s => s["pet"] != null && s["pet"].Type == JTokenType.Object, "pet loaded");
            Call("setFacing", "toggle");
            await Task.Delay(50);
            st = MustState();
            double sx0 = (double)st["pet"]["sx"];
            Call("loadResizePet", "1.5");
            await Task.Delay(50);
            st = MustState();
            double sx1 = (double)st["pet"]["sx"];
            Expect(Math.Sign(sx1) == Math.Sign(sx0) && Math.Abs(Math.Abs(sx1) - 1.5) < 0.001,
                "pet scaleX " + sx0 + " -> " + sx1 + " after loadResizePet(1.5); facing lost");
        }

        // Dagger (dual wield) attacks swing both blades.
        async Task DaggerAttackBothHands()
        {
            await LoadArmor("F", ArmorRobeF, "ccsephA");
            await Load("Weapon", Dagger, "Dagger");
            await WaitState(s => Has(Kids(s, "weapon"), "mugCysero") && Has(Kids(s, "weaponOff"), "mugCysero"), "dagger in both hands");
            Call("loadEmote", "DuelWield/DaggerAttack1");
            await Task.Delay(250);
            var st = MustState();
            Snap("dagger_attack1");
            Expect(((string)st["label"] ?? "").StartsWith("DuelWield/DaggerAttack1"), "label " + st["label"]);
            Expect(Visible(st, "weaponOff") && Has(Kids(st, "weaponOff"), "mugCysero"), "off-hand dagger not shown during the attack");
            await WaitState(s => (string)s["label"] == "Idle", "back to Idle after the attack", 4000);
        }

        static bool HandHas(JObject st, string hand, string cls, bool visible)
        {
            var k = Kids(st, hand);
            return k.Length == 2 && k[1].StartsWith(cls) && (k[1].EndsWith("(hidden)") != visible);
        }

        // Gauntlets are worn: one copy in each hand clip above the hand art
        // (game: AvatarMC.onLoadWeaponComplete), none in the weapon slot.
        async Task GauntletWornOnHands()
        {
            await LoadArmor("F", ArmorRobeF, "ccsephA");
            await Load("Weapon", Gauntlet, "Gauntlet");
            var st = await WaitState(s => (bool)s["gauntlet"] && HandHas(s, "fronthand", "FClericGOrb", true), "gauntlet on the hands");
            await Task.Delay(150);
            st = MustState();
            Snap("gauntlet_idle");
            Expect(Has(Kids(st, "fronthand"), "ccsephAFHand") && Kids(st, "fronthand")[0].StartsWith("ccsephAFHand"), "armor hand must stay under the gauntlet: " + string.Join(",", Kids(st, "fronthand")));
            Expect(HandHas(st, "backhand", "FClericGOrb", true), "back hand gauntlet: " + string.Join(",", Kids(st, "backhand")));
            Expect(!Visible(st, "weapon"), "weapon slot should be hidden for a gauntlet");
            Expect((double)st["parts"]["backhand"]["m0"] == 0, "back hand armor piece should stay a silhouette");

            // A new armor keeps the gauntlet on.
            await LoadArmor("F", ArmorRobeOnlyF, "BeleenSXY");
            await Task.Delay(150);
            st = MustState();
            Expect(Kids(st, "fronthand")[0].StartsWith("BeleenSXYFHand") && HandHas(st, "fronthand", "FClericGOrb", true), "armor swap dropped the gauntlet: " + string.Join(",", Kids(st, "fronthand")));

            // Hide weapon hides the gauntlet, and back.
            Call("unarmed", "True");
            await Task.Delay(60);
            st = MustState();
            Expect(HandHas(st, "fronthand", "FClericGOrb", false) && HandHas(st, "backhand", "FClericGOrb", false), "Hide weapon left the gauntlet visible");
            Call("unarmed", "False");
            await Task.Delay(60);
            Expect(HandHas(MustState(), "fronthand", "FClericGOrb", true), "un-hiding did not bring the gauntlet back");

            // Switching to a held weapon clears the hands and uses the slot.
            await Load("Weapon", Sword, "Sword");
            st = await WaitState(s => !(bool)s["gauntlet"] && Has(Kids(s, "weapon"), "flash.display::MovieClip") && Kids(s, "fronthand").Length == 1, "sword in the hand, gauntlet gone");
            Expect(Visible(st, "weapon"), "weapon slot hidden after switching from a gauntlet");
            Expect(Kids(st, "backhand").Length == 1, "gauntlet left on the back hand");
        }

        // Dropped files / blank CharPage types: the SWF itself says gauntlet.
        async Task GauntletDetected()
        {
            string g = await Fixture(Gauntlet), sw = await Fixture(Sword), ax = await Fixture("items/axes/axe05.swf");
            Expect(FlashCore.DetectWeaponType(File.ReadAllBytes(g)) == "Gauntlet", "gauntlet SWF not detected");
            Expect(FlashCore.DetectWeaponType(File.ReadAllBytes(sw)) == "", "sword misdetected as " + FlashCore.DetectWeaponType(File.ReadAllBytes(sw)));
            Expect(FlashCore.DetectWeaponType(File.ReadAllBytes(ax)) == "", "axe misdetected");
            await LoadArmor("F", ArmorRobeF, "ccsephA");
            Flash.LoadItem("Weapon", g, null);
            var st = await WaitState(s => (bool)s["gauntlet"], "gauntlet placed without a weapon type");
            Expect(HandHas(st, "fronthand", "FClericGOrb", true), "hands: " + string.Join(",", Kids(st, "fronthand")));
        }

        // Every animation the game treats as combat (World.as combatAnims in
        // the decompiled client, minus the sub-part GunAttack3End... which it
        // does not list) must be offered in the Emotes panel.
        static readonly string[] GameCombatAnims = { "Attack1", "Attack2", "Attack3", "Attack4", "Hit", "Knockout", "Getup", "Stab", "Thrash", "Castgood", "Cast1", "Cast2", "Cast3", "Sword/ShieldFight", "Sword/ShieldAttack1", "Sword/ShieldAttack2", "ShieldBlock", "DuelWield/DaggerFight", "DuelWield/DaggerAttack1", "DuelWield/DaggerAttack2", "FistweaponFight", "FistweaponAttack1", "FistweaponAttack2", "PolearmFight", "PolearmAttack1", "PolearmAttack2", "RangedFight", "RangedAttack1", "RangedAttack2", "RangedAttack3", "UnarmedFight", "UnarmedAttack1", "UnarmedAttack2", "UnarmedAttack3", "KickAttack", "FlipAttack", "Dodge", "WhipAttack", "GunAttack", "GunAttack2", "GunAttack3", "RifleAttack", "RifleAttack2", "RifleFight", "Card" };

        async Task EmotePanelCombatAnims()
        {
            var page = JArray.Parse(JsonConvert.DeserializeObject<string>(await Js("JSON.stringify(Object.values(EMOTE_GROUPS).flat())"))).Select(x => (string)x).ToList();
            var missing = GameCombatAnims.Where(a => !page.Contains(a)).ToList();
            Expect(missing.Count == 0, "Emotes panel lacks: " + string.Join(", ", missing));
        }

        async Task CloseUiiBeforeMisc()
        {
            string v;
            Expect(Flash.TryCall("closeUii", out v, "True"), "closeUii(True) threw before any misc name was set");
            Expect(Flash.TryCall("closeUii", out v, "False"), "closeUii(False) threw");
            await Task.Yield();
        }

        // Names with XML-special characters ("Shield & Blade") must reach the
        // player through the normal FlashCall path.
        async Task FlashCallEscapes()
        {
            string name = "Prince's Shield & Blade <of> \"Doom\"";
            Flash.FlashCall("changeWeaponName", new[] { name });
            Flash.FlashCall("changeUserName", new[] { "A&B" });
            await Task.Delay(30);
            var st = MustState();
            var names = st["names"] as JObject;
            Expect(names != null, "player exposes no name fields");
            Expect((string)names["weapon"] == name, "weapon name = '" + names["weapon"] + "', want '" + name + "'");
            Expect((string)names["user"] == "A&B", "user name = '" + names["user"] + "'");
        }

        async Task<string> Js(string js)
        {
            return await _form.EvalAsync(js);
        }

        async Task JsAwait(string promiseExpr, int ms = 60000)
        {
            await Js("window.__hl='busy';Promise.resolve().then(()=>(" + promiseExpr + ")).then(()=>{window.__hl='done'},e=>{window.__hl='err:'+(e&&e.message||e)})");
            string st = null;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                st = JsonConvert.DeserializeObject<string>(await Js("window.__hl"));
                if (st != "busy") break;
                await Task.Delay(100);
            }
            if (st != "done") throw new TestFailure("page call failed: " + promiseExpr + " -> " + st);
        }

        async Task UiHideCheckboxes()
        {
            await SetGender("F");
            await Load("Hair", HairBackF);
            await Load("Helm", HelmPlain);
            await Load("Cape", Cape);
            await WaitState(s => s["helmHead"] != null && Has(s["helmHead"]["kids"].Select(k => (string)k), "RevGenHood1") && Has(Kids(s, "cape"), "PalidanRevampCape"), "helm+cape");
            foreach (var box in new[] { "hideHelm", "hideCape" })
            {
                await Js("document.getElementById('" + box + "').click()");
                await Task.Delay(150);
                var on = MustState();
                bool vis = box == "hideHelm" ? (bool)on["helm"]["v"] : Visible(on, "cape");
                Expect(!vis, box + " checked: item still visible");
                await Js("document.getElementById('" + box + "').click()");
                await Task.Delay(150);
                var off = MustState();
                vis = box == "hideHelm" ? (bool)off["helm"]["v"] : Visible(off, "cape");
                Expect(vis, box + " unchecked: item stays hidden (checkbox sends lowercase true/false)");
            }
        }

        // Canned CharPage FlashVars: female character wearing a hair from the
        // male folder (real case) - gender must come from strGender, colors
        // from intColor*.
        async Task UiVarsColorsGender()
        {
            foreach (var rel in new[] { ArmorF2, HairMaleOnly, HelmPlain }) await Fixture(rel);
            var vars = new JObject
            {
                { "intColorHair", "16247807" }, { "intColorSkin", "16765338" }, { "intColorEye", "9765013" },
                { "intColorTrim", "6749952" }, { "intColorBase", "6684825" }, { "intColorAccessory", "255" },
                { "strGender", "F" },
                { "strHairFile", HairMaleOnly }, { "strHairName", "DFWarStyle" },
                { "strClassName", "Test" }, { "strClassFile", "InfinityAlphaPirateGoldDragon.swf" }, { "strClassLink", "InfinityAlphaPirateGoldDragon" },
                { "strArmorName", "Test Armor" },
                { "strHelmFile", HelmPlain }, { "strHelmLink", "RevGenHood1" }, { "strHelmName", "Hood" },
                { "strWeaponFile", "none" }, { "strCapeFile", "none" }, { "strPetFile", "none" }, { "strMiscFile", "none" }
            };
            await Js("document.getElementById('folder').value=" + JsonConvert.ToString(_opt.FixtureDir));
            await JsAwait("loadVars('Headless Warlic'," + vars.ToString(Formatting.None) + ")");
            var st = await WaitState(s => Has(Kids(s, "chest"), "InfinityAlphaPirateGoldDragon"), "armor from vars", 15000);
            await Task.Delay(300);
            st = MustState();
            Snap("ui_vars_warlic");
            Expect((string)st["gender"] == "F", "gender should be F from strGender, got " + st["gender"]);
            Expect(Has(Kids(st, "chest"), "InfinityAlphaPirateGoldDragonFChest"), "female armor pieces");
            var c = st["colors"];
            foreach (var k in new[] { "Hair", "Skin", "Eye", "Trim", "Base", "Accessory" })
            {
                string want = (string)vars["intColor" + k];
                Expect(c[k] != null && c[k].ToString() == want, "color " + k + " = " + c[k] + ", CharPage says " + want);
            }
            var bad = CheckColors(st);
            Expect(bad.Count == 0, "dye transforms: " + string.Join("; ", bad.Distinct().Take(8)));
        }


        Color CornerPixel()
        {
            using (var b = Flash.Snapshot()) return b.GetPixel(4, 4); // top-left: always background
        }

        async Task UiBackgroundColor()
        {
            await Js("project.bgScene='Hill';sendBackground();");
            await Task.Delay(700);
            await Js("setBgColor('#ff00ff')");
            await Task.Delay(500);
            using (var b = Flash.Snapshot()) b.Save(Path.Combine(_snapDir, "bg_color_magenta.png"), ImageFormat.Png);
            Color px = CornerPixel();
            Expect(px.R > 200 && px.G < 60 && px.B > 200, "background color not applied (corner pixel " + px + ")");
            await Js("setBgColor('#0a0c14')");
        }

        // Gradient scene (flash\Gradient.swf, built from tools\bg\Gradient.as):
        // listed like any scene, and paints the navy glow - dark at the top,
        // lighter and bluer at the bottom centre. The corner alone can't tell
        // it from the default color (both #0A0C14), so compare the two.
        async Task UiGradientBackground()
        {
            await Js("setBgColor('#0a0c14')");
            bool listed = JsonConvert.DeserializeObject<bool>(await Js("[...document.querySelectorAll('#bgGrid .bg-pick')].some(b => b.textContent === 'Gradient')"));
            Expect(listed, "Gradient is not in the scene list");
            await Js("project.bgScene='Gradient';sendBackground();");
            await Task.Delay(700);
            using (var b = Flash.Snapshot())
            {
                b.Save(Path.Combine(_snapDir, "bg_gradient.png"), ImageFormat.Png);
                Color top = b.GetPixel(4, 4), low = b.GetPixel(b.Width / 2, b.Height - 4);
                Expect(top.R < 20 && top.G < 22 && top.B < 30, "top corner should be near #0A0C14, got " + top);
                Expect(low.B > top.B + 25 && low.B > low.R, "bottom centre should glow navy, got " + low + " (top " + top + ")");
            }
            await Js("project.bgScene='';sendBackground();");
        }

        // Wardrobe panel (catalog\wardrobe.json): per-slot search, the four
        // access filters, cleaned CDN paths, and the 200-row cap.
        async Task<JObject> WardrobeQuery(string slot, string text, string access)
        {
            await JsAwait("(wd.slot=" + JsonConvert.ToString(slot) + ", wd.q=" + JsonConvert.ToString(text)
                + ", wd.access=" + JsonConvert.ToString(access) + ", wdQuery())");
            return JObject.Parse(JsonConvert.DeserializeObject<string>(await Js("JSON.stringify(wd.res)")));
        }

        static bool HasItem(JObject res, string name) { return res["items"].Any(i => (string)i["n"] == name); }

        async Task UiWardrobeCatalog()
        {
            await Js("window.__noUiLock = true");
            await JsAwait("openTab('wardrobe')");
            var all = await WardrobeQuery("Helm", "leprechaun hat", "all");
            Expect((bool)all["ok"], "catalog query failed: " + all["error"]);
            Expect((int)all["inSlot"] > 10000, "helm count " + all["inSlot"]);
            Expect(HasItem(all, "Leprechaun Hat"), "search 'leprechaun hat' missed Leprechaun Hat");
            Expect(!HasItem(await WardrobeQuery("Helm", "leprechaun hat", "ac"), "Leprechaun Hat"), "AC filter kept a gold item");
            Expect(HasItem(await WardrobeQuery("Helm", "leprechaun hat", "free"), "Leprechaun Hat"), "Free filter dropped a free item");
            Expect(!HasItem(await WardrobeQuery("Helm", "leprechaun hat", "member"), "Leprechaun Hat"), "Member filter kept a free item");
            var dk = await WardrobeQuery("Armor", "legion doomknight", "all");
            Expect(dk["items"].Any(i => (string)i["f"] == "LegionDoomKnightr3.swf"), "Legion DoomKnight path missing or not cleaned: " + dk["items"].ToString(Formatting.None));
            // Paging: a page per query, more pages as the list scrolls, until
            // every match is listed once.
            Func<Task<int>> rowCount = async () => JsonConvert.DeserializeObject<int>(await Js("document.querySelectorAll('#wdList .wd-row').length"));
            Func<Task> scrollEnd = async () => { await Js("(l => { l.scrollTop = l.scrollHeight; wdFill(); })(document.getElementById('wdList'))"); await Task.Delay(250); };
            var ground = await WardrobeQuery("Misc", "", "all");
            int total = (int)ground["total"];
            string sub = JsonConvert.DeserializeObject<string>(await Js("document.getElementById('sheetSub').textContent"));
            Expect(sub.EndsWith("ground runes"), "subtitle '" + sub + "'");
            Expect(await rowCount() == Math.Min(200, total), "first page rendered " + await rowCount() + " rows");
            for (int i = 0; i < 6 && await rowCount() < total; i++) await scrollEnd();
            Expect(await rowCount() == total, "scrolling listed " + await rowCount() + " of " + total + " ground runes");
            Expect(JsonConvert.DeserializeObject<bool>(await Js("!document.getElementById('wdFoot') && new Set(wd.res.items.map(i => i.f)).size === wd.res.items.length")),
                "full list has a 'Loading more' footer or repeated rows");
            await WardrobeQuery("Helm", "", "all");
            Expect(JsonConvert.DeserializeObject<string>(await Js("(document.getElementById('wdFoot') || {}).textContent || ''")) == "Loading more…", "no 'Loading more' footer on a long list");
            await scrollEnd();
            Expect(await rowCount() >= 400, "scrolling the helm list did not add a page (" + await rowCount() + " rows)");
            await Js("closeSheet()");
        }

        // Wardrobe try-on, live CDN: the item lands in the slot and on the
        // avatar, Revert restores the slot, Keep commits, Take off clears it.
        async Task LiveWardrobeTryOn()
        {
            await Js("window.__noUiLock = true");
            await JsAwait("openTab('wardrobe')");
            await WardrobeQuery("Helm", "leprechaun hat", "all");
            const string pick = "wdTry(wd.res.items.find(i => i.n === 'Leprechaun Hat'))";
            string before = JsonConvert.DeserializeObject<string>(await Js("JSON.stringify(project.slots.Helm || null)"));
            await JsAwait(pick);
            string err = JsonConvert.DeserializeObject<string>(await Js("wd.err"));
            Expect(string.IsNullOrEmpty(err), "try-on failed: " + err);
            var slot = JObject.Parse(JsonConvert.DeserializeObject<string>(await Js("JSON.stringify(project.slots.Helm)")));
            Expect(((string)slot["file"]).EndsWith("LeprechaunHatF.swf", StringComparison.OrdinalIgnoreCase), "Helm slot file " + slot["file"]);
            Expect(File.Exists((string)slot["path"]), "downloaded SWF missing: " + slot["path"]);
            Expect(JsonConvert.DeserializeObject<string>(await Js("project.gear.Helm")) == "Leprechaun Hat", "gear name not updated");
            await WaitState(s => s["helm"] != null && s["helm"].Type == JTokenType.Object && (bool)s["helm"]["v"] && s["helm"]["kids"].Any(), "tried helm on the avatar");
            Snap("wardrobe_try_on");
            await _form.SavePagePng(Path.Combine(_snapDir, "wardrobe_panel.png"));
            Expect(JsonConvert.DeserializeObject<bool>(await Js("!!document.querySelector('#wdList .wd-row.try') && !document.getElementById('wdCard').hidden")), "row/card not in the trying state");
            await JsAwait("wdRevert()");
            string after = JsonConvert.DeserializeObject<string>(await Js("JSON.stringify(project.slots.Helm || null)"));
            Expect(after == before, "Revert left Helm = " + after + ", was " + before);
            await JsAwait(pick);
            await Js("wdKeep()");
            Expect(JsonConvert.DeserializeObject<bool>(await Js("wd.trial === null && !!project.slots.Helm")), "Keep did not commit the helm");
            await JsAwait("wdTakeOff()");
            Expect(JsonConvert.DeserializeObject<bool>(await Js("!project.slots.Helm")), "Take off left the helm in the slot");
            await WaitState(s => s["helm"] == null || s["helm"].Type != JTokenType.Object || !(bool)s["helm"]["v"], "helm hidden after Take off");
            await Js("closeSheet()");
        }

        // Unequipping a dual (Dagger) set - Take off, or the hide-weapon
        // toggle - used to leave the off-hand copy in the hand. unarmed now
        // hides both hands and un-hiding brings both back.
        async Task DaggerOffhandFollowsUnarmed()
        {
            await Load("Weapon", Dagger, "Dagger");
            await WaitState(s => Visible(s, "weapon") && Visible(s, "weaponOff"), "dagger in both hands");
            Call("unarmed", "True");
            await WaitState(s => !Visible(s, "weapon") && !Visible(s, "weaponOff"), "both hands empty after unarmed");
            Call("unarmed", "False");
            await WaitState(s => Visible(s, "weapon") && Visible(s, "weaponOff"), "dagger back in both hands");
            Call("daggerMode", "False");
            await WaitState(s => Visible(s, "weapon") && !Visible(s, "weaponOff"), "daggerMode off hides only the off hand");
        }

        // Wardrobe card: a slim name + actions row by default; the "i" button
        // swaps in the full details card and is remembered.
        async Task UiWardrobeDetails()
        {
            await Js("window.__noUiLock = true");
            await JsAwait("openTab('wardrobe')");
            await Js(@"localStorage.setItem('fb.wdDetails', '0'); wd.details = false; wd.slot = 'Helm'; wd.trial = null;
              project.slots.Helm = { file: 'items\\helms\\Test.swf', path: 'C:\\test\\Test.swf', enabled: true }; project.gear.Helm = 'Test Helm'; wdRender();");
            Func<Task<JObject>> card = async () => JObject.Parse(JsonConvert.DeserializeObject<string>(await Js(@"(() => {
              const c = document.getElementById('wdCard');
              return JSON.stringify({ hidden: c.hidden, slim: c.classList.contains('slim'), code: !!c.querySelector('code'),
                buttons: [...c.querySelectorAll('button')].map(b => b.textContent), h: c.getBoundingClientRect().height,
                info: document.getElementById('wdInfo').classList.contains('on') });
            })()")));
            var slim = await card();
            Expect(!(bool)slim["hidden"] && (bool)slim["slim"] && !(bool)slim["code"], "default card should be the slim row: " + slim.ToString(Formatting.None));
            Expect(slim["buttons"].ToString(Formatting.None) == "[\"Take off\"]", "slim row buttons " + slim["buttons"].ToString(Formatting.None));
            Expect((double)slim["h"] <= 44, "slim row is " + slim["h"] + "px tall");
            await Js("document.getElementById('wdInfo').click()");
            var full = await card();
            Expect(!(bool)full["slim"] && (bool)full["code"] && (bool)full["info"], "i button did not open the full card: " + full.ToString(Formatting.None));
            Expect(JsonConvert.DeserializeObject<string>(await Js("localStorage.getItem('fb.wdDetails')")) == "1", "details choice not remembered");
            await Js("document.getElementById('wdInfo').click(); delete project.slots.Helm; delete project.gear.Helm; wdRender(); closeSheet();");
        }

        // Gender switch with a catalog armor on: the armor is fetched again
        // from classes/M/, the player gets the new gender, and back again.
        async Task LiveWardrobeGender()
        {
            await Js("window.__noUiLock = true");
            await JsAwait("openTab('wardrobe')");
            await Js("project.gender = 'F'; wdReset();");
            await WardrobeQuery("Armor", "legion doomknight", "all");
            await JsAwait("wdTry(wd.res.items.find(i => i.f === 'LegionDoomKnightr3.swf'))");
            await Js("wdKeep()");
            Func<Task<string>> armor = async () => JsonConvert.DeserializeObject<string>(await Js("(project.slots.Armor && project.slots.Armor.file) || ''"));
            Expect((await armor()).Replace('/', '\\').StartsWith(@"classes\F\", StringComparison.OrdinalIgnoreCase), "female armor file " + await armor());
            await JsAwait("wdSetGender('M')");
            string err = JsonConvert.DeserializeObject<string>(await Js("wd.err"));
            Expect(string.IsNullOrEmpty(err), "gender switch reported: " + err);
            Expect((await armor()).Replace('/', '\\').StartsWith(@"classes\M\", StringComparison.OrdinalIgnoreCase), "armor not refetched for M: " + await armor());
            Expect(File.Exists(JsonConvert.DeserializeObject<string>(await Js("project.slots.Armor.path"))), "male armor SWF not on disk");
            Expect(JsonConvert.DeserializeObject<bool>(await Js("document.querySelector('#wdGender .wd-g.on').dataset.g === 'M'")), "M not highlighted");
            await WaitState(s => (string)s["gender"] == "M", "player gender M");
            Snap("wardrobe_gender_m");
            await JsAwait("wdSetGender('F')");
            Expect((await armor()).Replace('/', '\\').StartsWith(@"classes\F\", StringComparison.OrdinalIgnoreCase), "armor not refetched for F: " + await armor());
            await WaitState(s => (string)s["gender"] == "F", "player gender F");
            await JsAwait("wdTakeOff()");
            await Js("closeSheet()");
        }

        // BG tab: clicking the selected scene again clears it, leaving the
        // plain background color (no "Empty" chip).
        async Task UiEmptyBackground()
        {
            await Js("setBgColor('#0a0c14')");
            await Js("project.bgScene='Hill';sendBackground();");
            await Task.Delay(700);
            Color scene = CornerPixel();
            await Js("[...document.querySelectorAll('#bgGrid .bg-pick')].find(b => b.textContent === 'Hill').click()");
            await Task.Delay(500);
            Color px = CornerPixel();
            Expect(!(scene.R == 10 && scene.G == 12 && scene.B == 20), "fixture scene did not load (corner " + scene + ")");
            Expect(px.R == 10 && px.G == 12 && px.B == 20, "Deselecting the scene should leave the default #0A0C14, corner pixel " + px);
            bool anyOn = JsonConvert.DeserializeObject<bool>(await Js("[...document.querySelectorAll('#bgGrid .bg-pick')].some(b => b.classList.contains('on'))"));
            Expect(!anyOn, "a scene chip is still highlighted after deselect");
        }

        // Title bar option now follows the chosen background color.
        async Task UiTitleBar()
        {
            string js = @"(() => {
              const t = document.querySelector('.titlebar'), cb = document.getElementById('matchTitle');
              if (cb.checked) cb.click();
              const dark = getComputedStyle(t).backgroundColor;
              const rail = 'rgb(11, 13, 23)'; // --rail: title bar default (no rail element anymore)
              cb.click();
              setBgColor('#3366cc');
              const blue = getComputedStyle(t).backgroundColor, blueLight = t.classList.contains('light');
              setBgColor('#f0f0e0');
              const pale = getComputedStyle(t).backgroundColor, paleLight = t.classList.contains('light');
              let stored = null; try { stored = localStorage.getItem('fb.matchTitle'); } catch (e) {}
              cb.click();
              setBgColor('#0a0c14');
              return JSON.stringify({ dark, rail, blue, blueLight, pale, paleLight, stored, off: getComputedStyle(t).backgroundColor });
            })()";
            var r = JObject.Parse(JsonConvert.DeserializeObject<string>(await Js(js)));
            Expect((string)r["dark"] == (string)r["rail"], "default title bar should match the UI (" + r["rail"] + "), is " + r["dark"]);
            Expect((string)r["blue"] == "rgb(51, 102, 204)" && !(bool)r["blueLight"], "title bar should take the background color, got " + r["blue"]);
            Expect((string)r["pale"] == "rgb(240, 240, 224)" && (bool)r["paleLight"], "pale background: color " + r["pale"] + ", dark ink " + r["paleLight"]);
            Expect((string)r["stored"] == "1", "option not remembered (localStorage=" + r["stored"] + ")");
            Expect((string)r["off"] == (string)r["rail"], "turning the option off should restore the dark bar, is " + r["off"]);
        }

        // No white scrollbar corner: Monitor/Log text never scrolls sideways
        // and the corner is styled.
        async Task UiMonitorScroll()
        {
            await Js(@"(() => { const m = document.getElementById('monitor'); m.textContent = Array(80).fill('[ok] (cos) Helm  items\\helms\\' + 'X'.repeat(90) + '.swf').join('\n'); })()");
            await JsAwait("openTab('monitor')");
            string js = @"(() => {
              const m = document.getElementById('monitor');
              const over = m.scrollWidth - m.clientWidth;
              const corner = [...document.styleSheets].some(ss => { try { return [...ss.cssRules].some(r => r.selectorText && r.selectorText.indexOf('scrollbar-corner') >= 0); } catch (e) { return false; } });
              closeSheet();
              return JSON.stringify({ over, corner });
            })()";
            var r = JObject.Parse(JsonConvert.DeserializeObject<string>(await Js(js)));
            Expect((int)r["over"] <= 0, "monitor scrolls sideways by " + r["over"] + "px (two scrollbars -> corner square)");
            Expect((bool)r["corner"], "no ::-webkit-scrollbar-corner rule");
        }

        JArray GearRows() { return JArray.Parse(_form.LastGearRows ?? "[]"); }
        string GearName(string icon) { var row = GearRows().FirstOrDefault(x => (string)x["icon"] == icon); return row == null ? null : (string)row["name"]; }

        // Names tab: shows/edits the active outfit's names (cosmetic ones
        // too), feeds the gear list, and "Show item names" toggles them there.
        async Task UiNamesFollowOutfit()
        {
            foreach (var rel in new[] { ArmorRobeF, HairBackF, HelmBackhair, HelmPlain }) await Fixture(rel);
            var vars = new JObject
            {
                { "strGender", "F" }, { "strHairFile", HairBackF }, { "strHairName", "Alina3" },
                { "strClassName", "C" }, { "strClassFile", "ccsephA.swf" }, { "strClassLink", "ccsephA" }, { "strArmorName", "Base Armor" },
                { "strHelmFile", HelmBackhair }, { "strHelmLink", "cmagicianHLocksHat" }, { "strHelmName", "Base Hat" },
                { "strCustHelmFile", HelmPlain }, { "strCustHelmLink", "RevGenHood1" }, { "strCustHelmName", "Cosmetic Hood" },
                { "strWeaponFile", "none" }, { "strCapeFile", "none" }, { "strPetFile", "none" }, { "strMiscFile", "none" }
            };
            await Js("document.getElementById('folder').value=" + JsonConvert.ToString(_opt.FixtureDir));
            await JsAwait("loadVars('Names Test'," + vars.ToString(Formatting.None) + ")");
            Func<string, Task<string>> field = async id => JsonConvert.DeserializeObject<string>(await Js("document.getElementById('" + id + "').value"));
            Expect(await field("nmHelm") == "Base Hat", "equipped helm name field: " + await field("nmHelm"));
            Expect(GearName("helm") == "Base Hat", "gear list helm: " + GearName("helm"));
            // No checkbox in the panel any more: the cosmetic outfit is the
            // native shirt button, which the page polls via cosSeq. Drive the
            // same page-side toggle the poll calls.
            await Js("toggleCosmetics()");
            await WaitUntil(() => GearName("helm") == "Cosmetic Hood", 3000);
            Expect(await field("nmHelm") == "Cosmetic Hood", "cosmetic outfit on: Names tab should show the cosmetic helm, shows " + await field("nmHelm"));
            Expect(GearName("helm") == "Cosmetic Hood", "gear list should show the cosmetic helm, shows " + GearName("helm"));
            await Js("const e=document.getElementById('nmHelm'); e.value='Renamed Hood'; e.dispatchEvent(new Event('input'));");
            await Task.Delay(100);
            Expect(GearName("helm") == "Renamed Hood", "editing the name did not reach the gear list: " + GearName("helm"));
            await Js("document.getElementById('showNames').click()");
            await Task.Delay(100);
            Expect(GearName("helm") == "" && GearRows().Count > 0, "Show item names off: gear list should keep icons without names, has '" + GearName("helm") + "'");
            await Js("document.getElementById('showNames').click()");
            await Task.Delay(100);
            Expect(GearName("helm") == "Renamed Hood", "names back on: " + GearName("helm"));
            Expect(JsonConvert.DeserializeObject<bool>(await Js("project.cosmeticsOn")), "cosmetic outfit toggle did not stick");
            await Js("toggleCosmetics()");
        }

        // Character name tag sits above the avatar's head.
        async Task NameTagOverAvatar()
        {
            await Load("Helm", HelmPlain);
            await LoadArmor("F", ArmorRobeF, "ccsephA");
            Call("changeUserName", "Zee");
            Call("showUserName", "True");
            await Task.Delay(120);
            var t = MustState()["nameTag"];
            Snap("name_tag");
            Expect((bool)t["v"], "name tag hidden");
            Expect((double)t["bottom"] <= (double)t["headTop"] + 2, "name tag (bottom " + t["bottom"] + ") overlaps the head (top " + t["headTop"] + ")");
            Expect((double)t["cx"] > (double)t["bodyLeft"] && (double)t["cx"] < (double)t["bodyRight"], "name tag not over the avatar: cx " + t["cx"] + " body " + t["bodyLeft"] + ".." + t["bodyRight"]);
            Call("loadEmote", "Walk");
            await Task.Delay(300);
            t = MustState()["nameTag"];
            Expect((double)t["bottom"] <= (double)t["headTop"] + 12, "name tag lost the avatar while walking");
            Call("showUserName", "False");
            await Task.Delay(50);
            Expect(!(bool)MustState()["nameTag"]["v"], "showUserName(False) did not hide the tag");
        }

        // Panel layout regressions from user screenshots: squashed chip text,
        // cut-off Browse button / horizontal scroll, oblong dye swatches.
        // (openTab is async - it awaits the overlay-hole round-trip after
        // revealing the sheet - so each tab is awaited open before measuring.)
        async Task UiLayout()
        {
            await Js("window.__noUiLock = true"); // rapid tab loop must not hit transition discipline
            await Js(@"window.__measureTab = (tab) => {
              const out = [];
              const inner = document.querySelector('.sheet-inner');
              if (inner.scrollWidth > inner.clientWidth + 1) out.push(tab + ': horizontal overflow ' + (inner.scrollWidth - inner.clientWidth) + 'px');
              document.querySelectorAll('#p-' + tab + ' .bg-pick, #p-' + tab + ' .emote-btn').forEach(b => {
                if (b.offsetParent && b.scrollHeight > b.clientHeight + 1) out.push(tab + ': clipped chip ' + b.textContent + ' ' + b.clientHeight + '/' + b.scrollHeight);
              });
              return JSON.stringify(out);
            };
            document.querySelectorAll('.sheet').forEach(d => d.style.transition = 'none');");
            var found = new List<string>();
            // Every dock button must have a matching panel, a title and a
            // subtitle: a tab with no panel opens as an empty sheet.
            // Js() returns raw JSON, so booleans come back unquoted.
            var tabs = JArray.Parse(JsonConvert.DeserializeObject<string>(await Js(
                "JSON.stringify([...document.querySelectorAll('.dock-btn')].map(b => b.dataset.t))")));
            foreach (var t in tabs)
            {
                var q = JsonConvert.ToString((string)t);
                if (!JsonConvert.DeserializeObject<bool>(await Js("!!document.getElementById('p-' + " + q + ")")))
                    found.Add(t + ": no #p-" + t + " panel");
                if (JsonConvert.DeserializeObject<bool>(await Js("typeof TAB_TITLES[" + q + "] !== 'string'")))
                    found.Add(t + ": no TAB_TITLES entry");
            }
            foreach (var tab in new[] { "backgrounds", "emotes", "colors", "log", "names", "wardrobe", "monitor" })
            {
                await JsAwait("openTab('" + tab + "')");
                var res = JArray.Parse(JsonConvert.DeserializeObject<string>(await Js("window.__measureTab('" + tab + "')")));
                foreach (var s in res) found.Add(s.Value<string>());
                // Output folder row (Browse) now lives in the Names panel.
                if (tab == "names")
                {
                    // Js() hands back raw JSON, so the quoted string must be
                    // decoded before it can be reported as a failure.
                    var cut = JsonConvert.DeserializeObject<string>(await Js(@"(() => {
                      const br = document.getElementById('btnBrowse').getBoundingClientRect();
                      const dr = document.querySelector('.sheet-inner').getBoundingClientRect();
                      return (br.right > dr.right + 1 || br.width < 20) ? 'names: Browse button cut off' : '';
                    })()"));
                    if (!string.IsNullOrEmpty(cut)) found.Add(cut);
                }
                if (found.Count >= 12) break;
            }
            await Js("closeSheet()");
            Expect(found.Count == 0, "layout: " + string.Join("; ", found));
        }


        async Task UiCopyLog()
        {
            string marker = "headless-copy-marker-" + Guid.NewGuid().ToString("N");
            AppForm.Dbg(marker);
            Clipboard.Clear();
            await Js("document.getElementById('btnCopyLog').click()");
            string text = "";
            await WaitUntil(() => { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; return text.Contains(marker); }, 3000, 100);
            Expect(text.Contains(marker), "clipboard does not hold the debug log (" + text.Length + " chars)");
        }

        // Gear-list icon clicks and the Misc checkboxes are one state.
        async Task UiGearIconSync()
        {
            await SetGender("F");
            await Load("Helm", HelmPlain);
            await WaitState(s => s["helmHead"] != null && Has(s["helmHead"]["kids"].Select(k => (string)k), "RevGenHood1"), "helm");
            await Js("if (document.getElementById('hideHelm').checked) document.getElementById('hideHelm').click()");
            _form.OnGearIcon("helm");
            var st = await WaitState(s => !(bool)s["helm"]["v"], "helm hidden by gear icon", 3000);
            Expect(JsonConvert.DeserializeObject<bool>(await Js("document.getElementById('hideHelm').checked")), "gear icon hid the helm but the Hide helm box stayed unticked");
            _form.OnGearIcon("helm");
            await WaitState(s => (bool)s["helm"]["v"], "helm shown again by gear icon", 3000);
            Expect(!JsonConvert.DeserializeObject<bool>(await Js("document.getElementById('hideHelm').checked")), "Hide helm box still ticked after un-hiding via gear icon");
        }

        // Typing line: over the preview, never over the gear list.
        async Task LoaderPlacement()
        {
            var cases = new[]
            {
                // preview, gear list (client coords) - gear sits left by
                // default, right while the sheet menu is open
                Tuple.Create(new Rectangle(264, 40, 568, 488), new Rectangle(276, 144, 240, 280)),
                Tuple.Create(new Rectangle(404, 40, 412, 449), new Rectangle(564, 124, 240, 280)),
                Tuple.Create(new Rectangle(45, 40, 771, 449), new Rectangle(57, 157, 240, 214)),
                // short preview, gear on the right: centered gear reaches the
                // top and overlaps the centered typing line -> squeezes left
                Tuple.Create(new Rectangle(45, 40, 771, 300), new Rectangle(564, 50, 240, 280)),
                // short preview, gear on the left -> squeezes right
                Tuple.Create(new Rectangle(45, 40, 771, 300), new Rectangle(57, 50, 240, 280)),
                Tuple.Create(new Rectangle(45, 40, 771, 449), Rectangle.Empty),
            };
            foreach (var c in cases)
            {
                var r = AppForm.LoaderRect(c.Item1, c.Item2, 46);
                Expect(c.Item1.Contains(r), "typing line " + r + " leaves the preview " + c.Item1);
                Expect(c.Item2.IsEmpty || !r.IntersectsWith(c.Item2), "typing line " + r + " covers the gear list " + c.Item2);
            }
            await Task.Yield();
        }

        // Character fetch/downloads used to block the UI thread for their
        // whole duration; a UI timer must keep ticking during a live load.
        // --char <name>: render any live character (manual visual checks).
        async Task LiveCharSnapshot()
        {
            if (string.IsNullOrEmpty(_opt.Char)) throw new FixtureUnavailable("no --char given");
            await Js("document.getElementById('folder').value=" + JsonConvert.ToString(Path.Combine(_opt.FixtureDir, "live")));
            await Js("document.getElementById('charName').value=" + JsonConvert.ToString(_opt.Char));
            await JsAwait("loadCharacter()", 120000);
            await Task.Delay(1500);
            string safe = string.Concat(_opt.Char.Split(Path.GetInvalidFileNameChars()));
            Snap("char_" + safe);
            var st = MustState();
            Note("label " + st["label"] + ", gauntlet " + st["gauntlet"] + ", weapon visible " + Visible(st, "weapon"));
        }

        async Task LiveLoadKeepsUiResponsive()
        {
            string dir = Path.Combine(_opt.FixtureDir, "live");
            await Js("document.getElementById('folder').value=" + JsonConvert.ToString(dir));
            await Js("document.getElementById('charName').value='Artix'");
            long maxGap = 0, last = 0;
            var sw = Stopwatch.StartNew();
            var t = new Timer { Interval = 25 };
            t.Tick += (s, e) => { long now = sw.ElapsedMilliseconds; if (last > 0) maxGap = Math.Max(maxGap, now - last); last = now; };
            t.Start();
            try { await JsAwait("loadCharacter()", 90000); }
            finally { t.Stop(); t.Dispose(); }
            Note("load took " + sw.ElapsedMilliseconds + " ms, longest UI stall " + maxGap + " ms");
            Expect(maxGap < 300, "UI thread stalled " + maxGap + " ms during the character load");
        }

        async Task DownloadsCachePaths()
        {
            var male = Downloads.BuildDownloads(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "strGender", "M" }, { "strHairName", "Bob" }, { "strHairFile", "hair/M/Bob.swf" },
                { "strArmorName", "A" }, { "strClassFile", "same.swf" }
            });
            var female = Downloads.BuildDownloads(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "strGender", "F" }, { "strHairName", "Bob" }, { "strHairFile", "hair/F/Bob.swf" },
                { "strArmorName", "A" }, { "strClassFile", "same.swf" }
            });
            foreach (var t in new[] { "Hair", "Armor" })
            {
                var m = male.First(x => x.Type == t);
                var f = female.First(x => x.Type == t);
                Expect(!string.Equals(m.File, f.File, StringComparison.OrdinalIgnoreCase),
                    t + ": male and female files share one cache path (" + m.File + ") - the wrong gender gets reused");
            }
            await Task.Yield();
        }

        // Gender belongs to the character (strGender / setGender), not to
        // whatever symbols an item file happens to export: a female
        // character wearing a hair from the male folder must stay female.
        async Task LinkagePrefersGender()
        {
            string hair = await Fixture(HairMaleOnly);
            await SetGender("F");
            Flash.LoadItem("Hair", hair, null);
            await Task.Delay(300);
            var st = MustState();
            Expect((string)st["gender"] == "F", "loading a male-folder hair flipped the character to " + st["gender"]);
        }

        async Task LiveCharPage()
        {
            string dir = Path.Combine(_opt.FixtureDir, "live");
            await Js("document.getElementById('folder').value=" + JsonConvert.ToString(dir));
            await Js("document.getElementById('charName').value='Alina'");
            await JsAwait("loadCharacter()", 90000);
            string varsJson = await Js("JSON.stringify(project.vars||null)");
            var vars = JObject.Parse(JsonConvert.DeserializeObject<string>(varsJson));
            string link = (string)vars["strClassLink"];
            var st = await WaitState(s => Has(Kids(s, "chest"), link), "live armor " + link, 20000);
            await Task.Delay(500);
            st = MustState();
            Snap("live_alina");
            foreach (var k in new[] { "Hair", "Skin", "Eye", "Trim", "Base", "Accessory" })
                Expect(st["colors"][k].ToString() == (string)vars["intColor" + k], "live " + k + " color not applied");
            Expect((string)st["gender"] == (string)vars["strGender"], "live gender");
            Expect(Has(Kids(st, "frontfoot"), link), "live front foot");
        }
    }
}
