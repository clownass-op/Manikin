using System;
using System.IO;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace ManikinApp
{
    // GradientScene: gives the Gradient background a CC channel.
    //
    // flash\Gradient.swf is a tiny AS3 movie: its constructor calls
    // graphics.beginGradientFill(RADIAL, [rim, mid, base], ...). The three
    // colours are plain int constants in the ABC pool (ffdec shows them as
    // 2764618 / 1316646 / 658452), so the scene can be recoloured without an
    // AS3 compiler: swap those pool entries and hand the patched SWF back to
    // the player, exactly like the untouched file.
    static class GradientScene
    {
        const int TagSetBackgroundColor = 9;
        const int TagDoAbc2 = 82;

        // Defaults compiled into the scene, used when a host has no template.
        static readonly int[] DefaultStops = { 0x2A2F4A, 0x141726, 0x0A0C14 };

        // glow: the colour the gradient should bloom with (0xRRGGBB).
        // Returns null when the template is missing/unexpected, so the caller
        // can fall back to loading the scene unchanged.
        public static byte[] Build(byte[] template, int glow)
        {
            if (template == null || template.Length < 12) return null;
            int[] stops = Stops(glow);
            byte[] body;
            try { body = template[0] == (byte)'C' ? Inflate(template) : Slice(template); }
            catch { return null; }
            if (body == null) return null;

            int version = template[3];
            int p = RectLength(body) + 4;
            int tagStart = -1, tagEnd = -1, abcOffset = -1;
            while (p + 2 <= body.Length)
            {
                int start = p;
                int h = body[p] | (body[p + 1] << 8);
                p += 2;
                int code = h >> 6, len = h & 0x3F;
                if (len == 0x3F)
                {
                    if (p + 4 > body.Length) break;
                    len = BitConverter.ToInt32(body, p);
                    p += 4;
                }
                if (code == TagDoAbc2) { tagStart = start; tagEnd = p + len; abcOffset = p; break; }
                p += len;
                if (code == 0) break;
            }
            if (tagStart < 0 || tagEnd > body.Length) return null;

            byte[] newTag = RecolourAbc(body, abcOffset, tagEnd, stops);
            if (newTag == null) return null;

            var ms = new MemoryStream();
            ms.Write(body, 0, tagStart);
            ms.Write(newTag, 0, newTag.Length);
            ms.Write(body, tagEnd, body.Length - tagEnd);

            byte[] patched = ms.ToArray();
            SetBackgroundColour(patched, stops[2]);

            var swf = new MemoryStream();
            swf.WriteByte((byte)'F'); swf.WriteByte((byte)'W'); swf.WriteByte((byte)'S');
            swf.WriteByte((byte)version);
            int total = patched.Length + 8;
            swf.WriteByte((byte)total); swf.WriteByte((byte)(total >> 8));
            swf.WriteByte((byte)(total >> 16)); swf.WriteByte((byte)(total >> 24));
            swf.Write(patched, 0, patched.Length);
            return swf.ToArray();
        }

        // rim / mid / base from one picked colour: the scene's own ratio is
        // 1 : 0.48 : 0.24, and a very dark pick is lifted so the glow stays
        // visible instead of collapsing into the base (default #0A0C14 ->
        // #252C4A / #121A2D / #091219, i.e. the shipped navy look).
        static int[] Stops(int glow)
        {
            int r = (glow >> 16) & 0xFF, g = (glow >> 8) & 0xFF, b = glow & 0xFF;
            int max = Math.Max(r, Math.Max(g, b));
            if (max <= 0) return (int[])DefaultStops.Clone();
            double gain = max < 74 ? 74.0 / max : 1.0;
            Func<double, int> lift = f => Math.Min(255, (int)Math.Round(f * gain));
            int rim = (lift(r) << 16) | (lift(g) << 8) | lift(b);
            return new[] { rim, Scale(rim, 0.48), Scale(rim, 0.24) };
        }

        static int Scale(int rgb, double f)
        {
            int r = (int)Math.Round(((rgb >> 16) & 0xFF) * f);
            int g = (int)Math.Round(((rgb >> 8) & 0xFF) * f);
            int b = (int)Math.Round((rgb & 0xFF) * f);
            return (r << 16) | (g << 8) | b;
        }

        // Rewrites the three int constants the AS3 constructor pushes into
        // beginGradientFill. The pool stores count-1 values (index 0 is the
        // implicit null), and the values are U30 varints, so any re-encoded
        // colour may change the ABC length - hence a fresh tag with a long
        // header instead of an in-place overwrite.
        static byte[] RecolourAbc(byte[] body, int tagBodyStart, int tagBodyEnd, int[] stops)
        {
            int nameEnd = tagBodyStart + 4;             // DoABC2: flags UI32, then the name
            while (nameEnd < tagBodyEnd && body[nameEnd] != 0) nameEnd++;
            int abcStart = nameEnd + 1;
            if (abcStart + 4 >= tagBodyEnd) return null;

            int p = abcStart + 4;                       // minor + major version
            int count = ReadU30(body, ref p);
            int valuesStart = p;
            int stored = Math.Max(0, count - 1);
            if (stored < 1) return null;
            for (int i = 0; i < stored; i++) ReadU30(body, ref p);
            int valuesEnd = p;

            var pool = new MemoryStream();
            for (int i = 0; i < stored; i++)
                WriteU30(pool, i < stops.Length ? stops[i] : 0);

            var tag = new MemoryStream();
            // flags + name + NUL + ABC header up to the values, the new pool,
            // then everything after the old values (all indices, no offsets).
            int len = (valuesStart - tagBodyStart) + (int)pool.Length + (tagBodyEnd - valuesEnd);
            int header = (TagDoAbc2 << 6) | 0x3F;
            tag.WriteByte((byte)header);
            tag.WriteByte((byte)(header >> 8));
            tag.WriteByte((byte)len);
            tag.WriteByte((byte)(len >> 8));
            tag.WriteByte((byte)(len >> 16));
            tag.WriteByte((byte)(len >> 24));
            tag.Write(body, tagBodyStart, valuesStart - tagBodyStart);   // flags, name, ABC header
            tag.Write(pool.ToArray(), 0, (int)pool.Length);
            tag.Write(body, valuesEnd, tagBodyEnd - valuesEnd);
            return tag.ToArray();
        }

        // The movie's own backdrop, so there is no flash of another colour.
        static void SetBackgroundColour(byte[] body, int rgb)
        {
            int p = RectLength(body) + 4;
            while (p + 2 <= body.Length)
            {
                int h = body[p] | (body[p + 1] << 8);
                p += 2;
                int code = h >> 6, len = h & 0x3F;
                if (len == 0x3F)
                {
                    if (p + 4 > body.Length) return;
                    len = BitConverter.ToInt32(body, p);
                    p += 4;
                }
                if (code == TagSetBackgroundColor && len == 3)
                {
                    body[p] = (byte)(rgb >> 16);
                    body[p + 1] = (byte)(rgb >> 8);
                    body[p + 2] = (byte)rgb;
                    return;
                }
                p += len;
                if (code == 0) return;
            }
        }

        static int ReadU30(byte[] b, ref int p)
        {
            int value = 0, shift = 0;
            while (p < b.Length)
            {
                int x = b[p++];
                value |= (x & 0x7F) << shift;
                if ((x & 0x80) == 0) break;
                shift += 7;
            }
            return value;
        }

        static void WriteU30(Stream s, int value)
        {
            while (true)
            {
                int x = value & 0x7F;
                value >>= 7;
                if (value != 0) s.WriteByte((byte)(x | 0x80));
                else { s.WriteByte((byte)x); return; }
            }
        }

        static int RectLength(byte[] b)
        {
            int bits = 0;
            for (int i = 0; i < 5; i++) bits = (bits << 1) | ((b[i / 8] >> (7 - i % 8)) & 1);
            return (5 + 4 * bits + 7) / 8;
        }

        static byte[] Slice(byte[] swf)
        {
            var body = new byte[swf.Length - 8];
            Buffer.BlockCopy(swf, 8, body, 0, body.Length);
            return body;
        }

        static byte[] Inflate(byte[] swf)
        {
            var inf = new Inflater();
            inf.SetInput(swf, 8, swf.Length - 8);
            using (var ms = new MemoryStream())
            {
                var buf = new byte[64 * 1024];
                while (!inf.IsFinished)
                {
                    int n = inf.Inflate(buf);
                    if (n <= 0) break;
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            }
        }
    }
}
