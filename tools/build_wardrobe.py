"""Build catalog/wardrobe.json (the Wardrobe panel's item list) from an
AQ Closet catalog snapshot.

    python tools/build_wardrobe.py <AQClosetCatalog.json>   # -> catalog/wardrobe.json

One row per distinct art: records sharing a slot and SWF path are merged, the
shortest non-"Preview" name becomes the row name and the rest are listed as
same-art names. Paths are cleaned of junk after ".swf" ("?2", "/t" - the CDN
answers 403 to the latter). Armor rows keep the bare file name; the app adds
classes/<gender>/.

Row layout (arrays keep the file small):
    [slot, file, name, alts ("|"-joined), link, weaponType, flags, rarity, where]
    flags: 1 = member-only (every variant is member), 2 = some variant is AC
"""
import json
import os
import re
import sys
from collections import Counter, defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SLOTS = ("helm", "armor", "cape", "weapon", "pet", "ground")


def clean_file(f):
    f = (f or "").strip().replace("\\", "/")
    m = re.search(r"\.swf", f, re.I)
    return f[:m.end()] if m else ""


def name_key(n):
    return ("preview" in n.lower(), len(n), n.lower())


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    src = json.load(open(sys.argv[1], encoding="utf-8"))
    groups = defaultdict(list)
    for it in src:
        slot, f = it.get("slot"), clean_file(it.get("file"))
        if slot in SLOTS and f:
            groups[(slot, f.lower())].append((f, it))

    rows = []
    for (slot, _), recs in groups.items():
        names = sorted({(it.get("name") or "").strip() for _, it in recs} - {""}, key=name_key)
        if not names:
            continue
        primary = next(it for _, it in recs if (it.get("name") or "").strip() == names[0])
        link = next((it.get("link") for _, it in recs if it.get("link")), "")
        wtypes = Counter(it.get("wtype") for _, it in recs if it.get("wtype"))
        flags = (1 if all(it.get("member") for _, it in recs) else 0) | (2 if any(it.get("ac") for _, it in recs) else 0)
        where = (primary.get("where") or [""])[0] or next(((it.get("where") or [""])[0] for _, it in recs if it.get("where")), "")
        rows.append([slot, recs[0][0], names[0], "|".join(names[1:]), link or "",
                     wtypes.most_common(1)[0][0] if wtypes else "", flags,
                     primary.get("rarity") or "", where or ""])

    rows.sort(key=lambda r: (SLOTS.index(r[0]), r[2].lower(), r[1].lower()))
    out = os.path.join(ROOT, "catalog", "wardrobe.json")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8", newline="\n") as fh:
        json.dump({"source": "AQ Closet catalog snapshot", "rows": rows}, fh, ensure_ascii=False, separators=(",", ":"))
    counts = Counter(r[0] for r in rows)
    print(out, os.path.getsize(out), "bytes,", len(rows), "rows", dict(counts))


if __name__ == "__main__":
    main()
