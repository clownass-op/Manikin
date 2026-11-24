"""Rebuild flash/char6.swf from the untouched backup plus player-src/.

    python tools/build_char6.py            # -> flash/char6.swf
    python tools/build_char6.py --out X    # build somewhere else
    python tools/build_char6.py --no-timeline-fix

Two steps, both reproducible from flash/char6-orig.swf:

1. Timeline fix (binary): the avatar skeleton (mcSkel, DefineSprite 300)
   briefly places a second, blank instance named "head" in the Rest and
   Unsheath emotes and removes it one frame later. The timeline rebinds
   mcSkel.head to that throwaway template head, so the real head (face,
   hair, helm) is orphaned mid-emote. The live game skeleton
   (assets.swf, mcSkel) has no such placement; they are stripped here.
2. Script replace: player-src/*.as are compiled into the SWF with the
   JPEXS FFDec CLI (-replace), replacing AvatarMC, mcSkel and
   character5_fla.MainTimeline.
3. The Sixseven emote: the keyframed 61-frame range from the SwordShine
   532c player is appended to the end of the skeleton (label + move-only
   placements), so it needs no character-id remap and no frame actions.
"""
import argparse
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKELETON_ID = 300
# Keyframed Sixseven emote (label + 61 move-only frames) extracted from
# the SwordShine 532c player. It only moves existing rig depths, so it needs
# no character-id remapping and no frame actions.
SIX_SEVEN_BIN = os.path.join(ROOT, "player-src", "sixseven.bin")
SIX_SEVEN_FRAMES = 61
# Dependency order: each class compiles against the ones replaced before it.
SCRIPTS = [
    ("mcSkel", "mcSkel.as"),
    ("AvatarMC", "AvatarMC.as"),
    ("character5_fla.MainTimeline", os.path.join("character5_fla", "MainTimeline.as")),
]
FFDEC_CANDIDATES = [
    os.environ.get("FFDEC_CLI", ""),
    r"C:\Program Files (x86)\FFDec\ffdec-cli.exe",
    r"C:\Program Files\FFDec\ffdec-cli.exe",
]


def read_swf(path):
    d = open(path, "rb").read()
    if d[:3] == b"CWS":
        d = b"FWS" + d[3:8] + zlib.decompress(d[8:])
    elif d[:3] != b"FWS":
        raise SystemExit("unsupported SWF signature: %r" % d[:3])
    return bytearray(d)


def first_tag(d):
    nbits = d[8] >> 3
    return 8 + (5 + 4 * nbits + 7) // 8 + 4


def iter_tags(d, pos, end):
    while pos < end:
        start = pos
        hdr = struct.unpack_from("<H", d, pos)[0]
        pos += 2
        code, ln = hdr >> 6, hdr & 63
        if ln == 63:
            ln = struct.unpack_from("<I", d, pos)[0]
            pos += 4
        yield code, start, pos, ln
        pos += ln
        if code == 0:
            break


def tag_bytes(code, body):
    if len(body) >= 63 or code == 39:
        return struct.pack("<HI", (code << 6) | 63, len(body)) + body
    return struct.pack("<H", (code << 6) | len(body)) + body


def po2_name(d, p):
    """Return (depth, name) of a PlaceObject2/3 body starting at p."""
    flags = d[p]
    depth = struct.unpack_from("<H", d, p + 1)[0]
    if not flags & 0x20:
        return depth, None
    q = p + 3
    if flags & 0x02:
        q += 2
    bit = q * 8

    def rd(n):
        nonlocal bit
        v = 0
        for _ in range(n):
            v = (v << 1) | ((d[bit >> 3] >> (7 - (bit & 7))) & 1)
            bit += 1
        return v

    def align():
        nonlocal bit
        bit = (bit + 7) & ~7
        return bit >> 3

    if flags & 0x04:  # MATRIX
        if rd(1):
            n = rd(5); rd(n); rd(n)
        if rd(1):
            n = rd(5); rd(n); rd(n)
        n = rd(5); rd(n); rd(n)
        q = align()
    bit = q * 8
    if flags & 0x08:  # CXFORMWITHALPHA
        ha, hm, n = rd(1), rd(1), rd(4)
        rd(n * 4 * hm)
        rd(n * 4 * ha)
        q = align()
    if flags & 0x10:  # ratio
        q += 2
    return depth, bytes(d[q:d.index(b"\0", q)]).decode("utf-8", "replace")


def add_six_seven(d):
    """Append the keyframed Sixseven emote to the skeleton (sprite 300).

    A real 61-frame timeline block (label + moves only), so both arms bend
    at the elbow while the whole rig stays connected. Appending it at the
    end keeps every existing frame number and frame script untouched."""
    blob = open(SIX_SEVEN_BIN, "rb").read()
    label = None
    frames = 0
    for c, s2, b2, l2 in iter_tags(blob, 0, len(blob)):
        if c == 43 and label is None:
            label = bytes(blob[b2:blob.index(b"\0", b2)]).decode("utf-8")
        elif c == 1:
            frames += 1
        elif c in (26, 70):
            if blob[b2] & 2:
                raise SystemExit("Sixseven blob places a character id; needs remapping")
        else:
            raise SystemExit("Sixseven blob: unexpected tag %d" % c)
    if label != "Sixseven" or frames != SIX_SEVEN_FRAMES:
        raise SystemExit("bad Sixseven blob: label=%r frames=%d" % (label, frames))
    for code, start, body, ln in iter_tags(d, first_tag(d), len(d)):
        if code != 39 or struct.unpack_from("<H", d, body)[0] != SKELETON_ID:
            continue
        count = struct.unpack_from("<H", d, body + 2)[0]
        end = body + ln - 2
        if struct.unpack_from("<H", d, end)[0] != 0:
            raise SystemExit("sprite %d has no trailing End tag" % SKELETON_ID)
        new_body = bytearray(d[body:end]) + blob + bytearray(d[end:body + ln])
        struct.pack_into("<H", new_body, 2, count + SIX_SEVEN_FRAMES)
        out = d[:start] + tag_bytes(39, bytes(new_body)) + d[body + ln:]
        struct.pack_into("<I", out, 4, len(out))
        return out, count + SIX_SEVEN_FRAMES
    raise SystemExit("skeleton sprite %d not found" % SKELETON_ID)


def strip_stray_heads(d):
    """Drop frame>1 placements named 'head' in the skeleton sprite, plus the
    RemoveObject2 that later clears the same depth."""
    for code, start, body, ln in iter_tags(d, first_tag(d), len(d)):
        if code != 39 or struct.unpack_from("<H", d, body)[0] != SKELETON_ID:
            continue
        kept = bytearray(d[body:body + 4])
        frame, doomed, removed = 1, set(), 0
        for c2, s2, b2, l2 in iter_tags(d, body + 4, body + ln):
            raw = bytes(d[s2:b2 + l2])
            if c2 == 1:
                frame += 1
            elif c2 in (26, 70) and frame > 1:
                depth, name = po2_name(d, b2)
                if name == "head":
                    doomed.add(depth)
                    removed += 1
                    continue
            elif c2 == 28:
                depth = struct.unpack_from("<H", d, b2)[0]
                if depth in doomed:
                    doomed.discard(depth)
                    removed += 1
                    continue
            kept += raw
        new_tag = tag_bytes(39, bytes(kept))
        out = d[:start] + new_tag + d[body + ln:]
        struct.pack_into("<I", out, 4, len(out))
        return out, removed
    raise SystemExit("skeleton sprite %d not found" % SKELETON_ID)


# ---- skeleton upgrade ------------------------------------------------------
# char6 shipped an older avatar skeleton (1674 frames, 79 labels). The live
# game's skeleton (mcSkel, DefineSprite 3286 in the decompiled assets.swf,
# 1888 frames) adds WhipAttack, Gun/Rifle attacks, RangedAttack3,
# UnarmedAttack3, horse/throne walks and Card. Its timeline replaces
# sprite 300 here. Body-part containers (chest, head, weapon, ...) are
# re-pointed at char6's own container sprites, so the art-loading code keeps
# its expectations (head.helm.mhead, weapon.hi, ...); only new animation art
# (emote FX, arrows, props) is copied, under its original ids (no overlap
# with char6's 1..329). The two class-linked clips map to char6's
# equivalents so their frame scripts still run; the PvP flag and two static
# texts whose fonts are not in assets.swf are dropped.
GAME_SKELETON = 3286
GAME_TO_CHAR6 = {
    2938: 126,   # hitbox
    3015: 129,   # backhair
    3040: 130,   # cape container (holds the linked mcCape)
    3050: 140,   # weapon / weaponOff / weaponFist / weaponFistOff (has "hi")
    3053: 143,   # shoulders
    3056: 146,   # hands
    3059: 149,   # backrobe
    3062: 152,   # feet
    3065: 155,   # thighs
    3068: 158,   # shins
    3071: 161,   # chest
    3073: 163,   # hip
    3076: 166,   # idlefoot
    3081: 171,   # robe
    3086: 177,   # head (face / helm.mhead / hair)
    3088: 179,   # shield
    3139: 101,   # T_AvatarMon_fla_fla.fxx_51 -> AvatarMonPet_fla.fxx_54
    3182: 97,    # AbyssalBow
}
GAME_DROP = {3012, 3090, 3091}  # pvpFlag, unrenderable static texts
DEFINE_TAGS = {2, 22, 32, 83, 39, 10, 48, 75, 11, 33, 37, 46, 84, 6, 21, 35, 36, 20, 90, 14, 87, 62, 73, 88, 13, 91}
BITMAP_TAGS = {6, 21, 35, 36, 20, 90}
DEFAULT_GAME_ASSETS = os.path.join(os.path.dirname(ROOT), "references", "AQW decompiled game",
                                   "Game3098r27", "scripts", "_assets", "assets.swf")
SKELETON_CACHE = os.path.join(ROOT, "player-src", "game-skeleton.bin")


def po_char(d, p, code):
    """(offset of the character id, flags, depth) for a PlaceObject2/3 body."""
    if code == 26:
        flags = d[p]
        return (p + 3 if flags & 2 else None), flags, struct.unpack_from("<H", d, p + 1)[0]
    f1, f2 = d[p], d[p + 1]
    depth = struct.unpack_from("<H", d, p + 2)[0]
    q = p + 4
    if f2 & 0x08 or (f2 & 0x10 and f1 & 0x02):
        q = d.index(b"\0", q) + 1
    return (q if f1 & 2 else None), f1, depth


def rewrite_sprite_tags(d, start, end):
    """Remap character ids per GAME_TO_CHAR6 and drop GAME_DROP placements
    (plus later moves/removals of the depths they occupied)."""
    out, dropped = bytearray(), set()
    for code, s, b, ln in iter_tags(d, start, end):
        raw = bytearray(d[s:b + ln])
        if code in (26, 70):
            off, flags, depth = po_char(d, b, code)
            if off is not None:
                cid = struct.unpack_from("<H", d, off)[0]
                if cid in GAME_DROP:
                    dropped.add(depth)
                    continue
                dropped.discard(depth)
                if cid in GAME_TO_CHAR6:
                    struct.pack_into("<H", raw, off - s, GAME_TO_CHAR6[cid])
            elif depth in dropped:
                continue
        elif code == 28:
            depth = struct.unpack_from("<H", d, b)[0]
            if depth in dropped:
                dropped.discard(depth)
                continue
        out += raw
    return bytes(out)


def extract_game_skeleton(path):
    """Return (definition tags to insert, new body for sprite 300)."""
    g = read_swf(path)
    defs, order = {}, []
    for code, s, b, ln in iter_tags(g, first_tag(g), len(g)):
        if code in DEFINE_TAGS:
            cid = struct.unpack_from("<H", g, b)[0]
            defs[cid] = (code, s, b, ln)
            order.append(cid)
    bitmaps = [c for c, v in defs.items() if v[0] in BITMAP_TAGS]
    need, stack = set(), [GAME_SKELETON]
    while stack:
        cid = stack.pop()
        if cid in need or cid not in defs or cid in GAME_TO_CHAR6 or cid in GAME_DROP:
            continue
        need.add(cid)
        code, s, b, ln = defs[cid]
        if code == 39:
            for c2, s2, b2, l2 in iter_tags(g, b + 4, b + ln):
                if c2 in (26, 70):
                    off, _, _ = po_char(g, b2, c2)
                    if off is not None:
                        stack.append(struct.unpack_from("<H", g, off)[0])
        else:
            # Shapes reference bitmap fills by id; include any bitmap whose id
            # occurs in the body (a false positive only costs a few bytes).
            body = bytes(g[b + 2:b + ln])
            stack += [bm for bm in bitmaps if struct.pack("<H", bm) in body]
    skeleton_body = None
    inserts = bytearray()
    for cid in order:
        if cid not in need:
            continue
        code, s, b, ln = defs[cid]
        if code == 39:
            body = bytes(g[b:b + 4]) + rewrite_sprite_tags(g, b + 4, b + ln)
            if cid == GAME_SKELETON:
                skeleton_body = struct.pack("<H", SKELETON_ID) + body[2:]
                continue
            inserts += tag_bytes(39, body)
        else:
            inserts += bytes(g[s:b + ln])
    if skeleton_body is None:
        raise SystemExit("game skeleton sprite %d not found in %s" % (GAME_SKELETON, path))
    return bytes(inserts), skeleton_body


def load_game_skeleton(path):
    if path and os.path.isfile(path):
        inserts, body = extract_game_skeleton(path)
        with open(SKELETON_CACHE, "wb") as f:
            f.write(b"FBSK" + struct.pack("<I", len(inserts)) + inserts + struct.pack("<I", len(body)) + body)
        return inserts, body
    if os.path.isfile(SKELETON_CACHE):
        blob = open(SKELETON_CACHE, "rb").read()
        if blob[:4] != b"FBSK":
            raise SystemExit("bad skeleton cache " + SKELETON_CACHE)
        n = struct.unpack_from("<I", blob, 4)[0]
        inserts = blob[8:8 + n]
        m = struct.unpack_from("<I", blob, 8 + n)[0]
        return inserts, blob[12 + n:12 + n + m]
    raise SystemExit("game assets.swf not found and no %s; pass --game-assets" % SKELETON_CACHE)


def upgrade_skeleton(d, inserts, body):
    for code, start, b, ln in iter_tags(d, first_tag(d), len(d)):
        if code == 39 and struct.unpack_from("<H", d, b)[0] == SKELETON_ID:
            out = d[:start] + inserts + tag_bytes(39, body) + d[b + ln:]
            struct.pack_into("<I", out, 4, len(out))
            return out
    raise SystemExit("skeleton sprite %d not found" % SKELETON_ID)


def ffdec():
    for c in FFDEC_CANDIDATES:
        if c and os.path.isfile(c):
            return c
    raise SystemExit("FFDec CLI not found; set FFDEC_CLI")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default=os.path.join(ROOT, "flash", "char6-orig.swf"))
    ap.add_argument("--src", default=os.path.join(ROOT, "player-src"))
    ap.add_argument("--out", default=os.path.join(ROOT, "flash", "char6.swf"))
    ap.add_argument("--no-timeline-fix", action="store_true")
    ap.add_argument("--old-skeleton", action="store_true",
                    help="keep char6's original skeleton (player-src/mcSkel.as must match it)")
    ap.add_argument("--game-assets", default=DEFAULT_GAME_ASSETS,
                    help="decompiled game assets.swf; falls back to player-src/game-skeleton.bin")
    a = ap.parse_args()

    d = read_swf(a.base)
    if not a.old_skeleton:
        inserts, body = load_game_skeleton(a.game_assets)
        d = upgrade_skeleton(d, inserts, body)
        print("skeleton: game mcSkel timeline (%d bytes of new art)" % len(inserts))
    if not a.no_timeline_fix:
        d, n = strip_stray_heads(d)
        print("timeline: removed %d stray head tags" % n)
    d, total = add_six_seven(d)
    print("timeline: appended keyframed Sixseven emote (%d frames total)" % total)
    tmp = tempfile.mkdtemp(prefix="char6-")
    try:
        stage = os.path.join(tmp, "stage.swf")
        open(stage, "wb").write(d)
        args = [ffdec(), "-replace", stage, os.path.join(tmp, "out.swf")]
        for cls, rel in SCRIPTS:
            args += [cls, os.path.join(a.src, rel)]
        r = subprocess.run(args, capture_output=True, text=True)
        sys.stdout.write(r.stdout)
        out = os.path.join(tmp, "out.swf")
        if r.returncode != 0 or not os.path.isfile(out) or "error" in (r.stdout + r.stderr).lower():
            sys.stderr.write(r.stderr)
            raise SystemExit("FFDec compile failed")
        shutil.copyfile(out, a.out)
        print("wrote", a.out, os.path.getsize(a.out), "bytes")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    main()
