using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ManikinApp
{
    // Wardrobe: the catalog behind the Wardrobe panel (catalog\wardrobe.json,
    // built by tools\build_wardrobe.py from an AQ Closet snapshot). Loaded once
    // in the background at startup; queries are a plain scan of ~50k rows,
    // which is a few ms - fast enough to run on every keystroke.
    static class Wardrobe
    {
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

        static Task<List<Item>> _load;

        public static void Preload()
        {
            if (_load == null) _load = Task.Run(() => Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "catalog", "wardrobe.json")));
        }

        static List<Item> Load(string path)
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
                AppForm.Dbg("wardrobe: " + list.Count + " items from " + path);
            }
            catch (Exception ex) { AppForm.Dbg("wardrobe load failed (" + path + "): " + ex.Message); }
            return list;
        }

        static List<Item> Items
        {
            get { Preload(); return _load.Result; }
        }

        // One page (offset, limit) of the rows for a slot, filtered by access
        // ("all" | "free" | "member" | "ac") and by every word of q. Rows come
        // pre-sorted A-Z; total is the full match count.
        public static string Query(string slot, string q, string access, int offset, int limit)
        {
            string cs;
            if (slot == null || !CatalogSlot.TryGetValue(slot, out cs))
                return JsonConvert.SerializeObject(new { ok = false, error = "unknown slot " + slot });
            if (Items.Count == 0)
                return JsonConvert.SerializeObject(new { ok = false, error = "No catalog: put wardrobe.json in catalog\\ next to Manikin.exe." });
            var words = (q ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int total = 0, inSlot = 0;
            var outRows = new JArray();
            foreach (var it in Items)
            {
                if (it.Slot != cs) continue;
                inSlot++;
                if (access == "free" && it.Member) continue;
                if (access == "member" && !it.Member) continue;
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
                string rel = (file ?? "").Replace('\\', '/');
                if (string.Equals(slot, "Armor", StringComparison.OrdinalIgnoreCase))
                    rel = "classes/" + (gender == "M" ? "M" : "F") + "/" + rel;
                var it = new Downloads.ItemDownload { Type = slot, Url = Downloads.GAME + rel, File = Downloads.LocalPathOf(rel) };
                string path = Downloads.EnsureLocalAsync(it, dir).GetAwaiter().GetResult();
                if (path == null) return JsonConvert.SerializeObject(new { ok = false, error = "Download failed: " + rel });
                return JsonConvert.SerializeObject(new { ok = true, path, file = it.File });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { ok = false, error = ex.Message }); }
        }
    }
}
