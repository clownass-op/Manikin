using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ManikinApp
{
    // Minimal animated-GIF writer, so recording the preview needs no image
    // library. GIF tops out at 256 colours, which is what wrecks a smooth
    // gradient. Two things buy the most quality back:
    //   * a palette cut from each frame's own colours (a shared table washes the
    //     whole clip towards the average, and the frame that needs many greens
    //     cannot borrow them from the frame that needs skin tones), and
    //   * Floyd-Steinberg error diffusion, which turns the surviving hard bands
    //     into a fine texture the eye reads as smooth.
    // Palette entries are also snapped to the five-bit grid Discord's media proxy
    // rounds GIFs onto anyway, so it cannot change the colours we picked.
    // Frames that do not change produce identical pixels and therefore identical
    // indices, so per-frame tables do not make a static background shimmer.
    static class GifWriter
    {
        // delaysMs[i] is how long frame i stays on screen.
        public static void Write(Stream outp, IList<Bitmap> frames, IList<int> delaysMs)
        {
            Write(outp, frames, delaysMs, false);
        }

        // transparent: reserve one palette entry as the see-through colour, which
        // is what a cut-out clip needs (GIF only has 1-bit alpha, so coverage
        // below half is dropped rather than blended).
        public static void Write(Stream outp, IList<Bitmap> frames, IList<int> delaysMs, bool transparent)
        {
            if (frames == null || frames.Count == 0) throw new ArgumentException("no frames");
            int w = frames[0].Width, h = frames[0].Height;

            Text(outp, "GIF89a");
            Short(outp, w);
            Short(outp, h);
            // Global table with the minimum two entries: every frame carries its
            // own optimised local table instead.
            outp.WriteByte(0x80 | 0x70 | 0);
            outp.WriteByte(0);   // background colour index
            outp.WriteByte(0);   // pixel aspect ratio
            outp.Write(new byte[6], 0, 6);

            // Netscape looping extension: 0 repeats means "forever".
            outp.WriteByte(0x21); outp.WriteByte(0xFF); outp.WriteByte(0x0B);
            Text(outp, "NETSCAPE2.0");
            outp.WriteByte(0x03); outp.WriteByte(0x01);
            Short(outp, 0);
            outp.WriteByte(0x00);

            var indices = new byte[w * h];
            for (int f = 0; f < frames.Count; f++)
            {
                int[] px = ReadPixels(frames[f]);
                byte[] mask = transparent ? ReadAlpha(frames[f]) : null;

                byte[] palette;
                int colours, bits;
                BuildPalette(px, mask, transparent, out palette, out colours, out bits);
                short[] nearest = NearestLut(palette, colours);
                Dither(px, indices, w, h, palette, nearest, mask, transparent ? colours : -1);

                // GIF stores timing in hundredths of a second, so round rather
                // than truncate: a 42 ms step becomes 4, not 3.
                int delay = ((f < delaysMs.Count ? delaysMs[f] : 40) + 5) / 10;
                if (delay < 2) delay = 2;
                if (delay > 600) delay = 600;

                // Graphic control extension: disposal 1 leaves the frame in place.
                // Bit 0 flags a transparent index, which is the reserved entry
                // after the real colours.
                outp.WriteByte(0x21); outp.WriteByte(0xF9); outp.WriteByte(0x04);
                outp.WriteByte((byte)(transparent ? 0x05 : 0x04));
                Short(outp, delay);
                outp.WriteByte((byte)(transparent ? colours : 0));
                outp.WriteByte(0x00);

                // Image descriptor with a local colour table sized 2^bits.
                outp.WriteByte(0x2C);
                Short(outp, 0); Short(outp, 0); Short(outp, w); Short(outp, h);
                outp.WriteByte((byte)(0x80 | (bits - 1)));
                outp.Write(palette, 0, palette.Length);
                Lzw(outp, indices, Math.Max(2, bits));
            }
            outp.WriteByte(0x3B);
        }

        // ---- palette -------------------------------------------------------

        sealed class Col
        {
            public int R, G, B, C;
        }

        // Median cut over the frame's own colours, bucketed to 5 bits per
        // channel so a photographic frame cannot produce millions of entries.
        static void BuildPalette(int[] px, byte[] mask, bool reserve, out byte[] palette, out int colours, out int bits)
        {
            const int Buckets = 32768;
            var cnt = new int[Buckets];
            var sr = new long[Buckets];
            var sg = new long[Buckets];
            var sb = new long[Buckets];

            for (int i = 0; i < px.Length; i++)
            {
                if (mask != null && mask[i] < 128) continue;   // see-through
                int c = px[i];
                int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                int k = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                cnt[k]++;
                sr[k] += r; sg[k] += g; sb[k] += b;
            }

            var cols = new List<Col>(256);
            for (int k = 0; k < Buckets; k++)
            {
                if (cnt[k] == 0) continue;
                cols.Add(new Col
                {
                    R = (int)(sr[k] / cnt[k]),
                    G = (int)(sg[k] / cnt[k]),
                    B = (int)(sb[k] / cnt[k]),
                    C = cnt[k]
                });
            }

            if (cols.Count == 0)
            {
                palette = new byte[12];
                colours = 1; bits = 2;
                return;
            }

            int limit = reserve ? 255 : 256;
            var array = cols.ToArray();
            var boxes = new List<Box>(256);
            boxes.Add(new Box(0, array.Length - 1, array));
            while (boxes.Count < limit)
            {
                Box pick = null;
                double best = 0;
                for (int i = 0; i < boxes.Count; i++)
                {
                    var b = boxes[i];
                    if (b.Count < 2) continue;
                    // Weight by spread and by mass: a huge soft gradient gets the
                    // boxes it needs to stay smooth, small bright details still
                    // earn their own entries.
                    double score = b.Widest * Math.Sqrt(b.Total);
                    if (score > best) { best = score; pick = b; }
                }
                if (pick == null) break;
                Box right = pick.Split();
                if (right == null) break;
                boxes.Add(right);
            }

            colours = boxes.Count;
            int entries = colours + (reserve ? 1 : 0);   // +1: the clear entry
            bits = 1;
            while ((1 << bits) < entries) bits++;
            if (bits < 2) bits = 2;
            palette = new byte[(1 << bits) * 3];
            for (int i = 0; i < boxes.Count; i++) boxes[i].WriteAverage(palette, i);
        }

        // A slice of the colour array; median cut keeps halving the widest one.
        sealed class Box
        {
            readonly Col[] _cols;
            public int Lo, Hi, Total;

            public Box(int lo, int hi, Col[] cols)
            {
                _cols = cols;
                Lo = lo;
                Hi = hi;
                for (int i = lo; i <= hi; i++) Total += cols[i].C;
            }

            public int Count { get { return Hi - Lo + 1; } }

            public int Widest
            {
                get
                {
                    int a = Range(0), b = Range(1), c = Range(2);
                    return Math.Max(a, Math.Max(b, c));
                }
            }

            int Range(int ch)
            {
                int min = 255, max = 0;
                for (int i = Lo; i <= Hi; i++)
                {
                    int v = Value(_cols[i], ch);
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
                return max - min;
            }

            public Box Split()
            {
                if (Count < 2) return null;
                int axis = 0, best = -1;
                for (int ch = 0; ch < 3; ch++)
                {
                    int r = Range(ch);
                    if (r > best) { best = r; axis = ch; }
                }
                if (best <= 0) return null;
                Array.Sort(_cols, Lo, Count, new AxisComparer(axis));

                int half = Math.Max(1, Total / 2), acc = 0, cut = Lo;
                for (int i = Lo; i <= Hi; i++)
                {
                    acc += _cols[i].C;
                    if (acc >= half) { cut = i; break; }
                }
                if (cut >= Hi) cut = Hi - 1;

                var right = new Box(cut + 1, Hi, _cols);
                Hi = cut;
                Total = 0;
                for (int i = Lo; i <= Hi; i++) Total += _cols[i].C;
                return right;
            }

            public void WriteAverage(byte[] pal, int at)
            {
                long r = 0, g = 0, b = 0, c = 0;
                for (int i = Lo; i <= Hi; i++)
                {
                    var col = _cols[i];
                    r += (long)col.R * col.C;
                    g += (long)col.G * col.C;
                    b += (long)col.B * col.C;
                    c += col.C;
                }
                if (c == 0) c = 1;
                pal[at * 3] = Grid(r / c);
                pal[at * 3 + 1] = Grid(g / c);
                pal[at * 3 + 2] = Grid(b / c);
            }

            public static int Value(Col c, int ch)
            {
                return ch == 0 ? c.R : ch == 1 ? c.G : c.B;
            }
        }

        sealed class AxisComparer : IComparer<Col>
        {
            readonly int _axis;
            public AxisComparer(int axis) { _axis = axis; }
            public int Compare(Col x, Col y) { return Box.Value(x, _axis).CompareTo(Box.Value(y, _axis)); }
        }

        // Nearest palette entry for every 5-bit colour, computed once per frame:
        // 32768 lookups instead of a search per pixel.
        static short[] NearestLut(byte[] pal, int colours)
        {
            var lut = new short[32768];
            for (int k = 0; k < lut.Length; k++)
            {
                int r = (((k >> 10) & 31) << 3) + 4;
                int g = (((k >> 5) & 31) << 3) + 4;
                int b = ((k & 31) << 3) + 4;
                int best = 0, bestD = int.MaxValue;
                for (int i = 0; i < colours; i++)
                {
                    int dr = r - pal[i * 3], dg = g - pal[i * 3 + 1], db = b - pal[i * 3 + 2];
                    int d = dr * dr + dg * dg + db * db;
                    if (d < bestD) { bestD = d; best = i; }
                }
                lut[k] = (short)best;
            }
            return lut;
        }

        // Floyd-Steinberg, serpentine. Ordered dithering left visible rulings
        // across a large soft gradient; diffusing the real error into the next
        // pixels is what makes 256 colours read as continuous, and because a
        // still background produces the same pattern every frame it also
        // compresses well.
        static void Dither(int[] px, byte[] indices, int w, int h, byte[] pal, short[] nearest,
            byte[] mask, int clearIndex)
        {
            var curR = new int[w + 2]; var curG = new int[w + 2]; var curB = new int[w + 2];
            var nxtR = new int[w + 2]; var nxtG = new int[w + 2]; var nxtB = new int[w + 2];

            for (int y = 0; y < h; y++)
            {
                bool reverse = (y & 1) == 1;
                int dir = reverse ? -1 : 1;
                for (int i = 0; i < w; i++)
                {
                    int x = reverse ? w - 1 - i : i;
                    int k = x + 1;
                    int c = px[y * w + x];
                    if (mask != null && mask[y * w + x] < 128)
                    {
                        // Nothing is drawn here, so there is no error to diffuse.
                        indices[y * w + x] = (byte)clearIndex;
                        continue;
                    }
                    // Half the usual error diffusion: full-strength Floyd-Steinberg
                    // looks smooth on a 1:1 screen but turns into speckle once a
                    // chat client re-encodes the file, and this art is flat enough
                    // that the palette alone covers it.
                    int r = Clamp(((c >> 16) & 0xFF) + curR[k] / 32);
                    int g = Clamp(((c >> 8) & 0xFF) + curG[k] / 32);
                    int b = Clamp((c & 0xFF) + curB[k] / 32);

                    int pi = nearest[((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)];
                    indices[y * w + x] = (byte)pi;

                    int er = r - pal[pi * 3], eg = g - pal[pi * 3 + 1], eb = b - pal[pi * 3 + 2];
                    curR[k + dir] += er * 7; curG[k + dir] += eg * 7; curB[k + dir] += eb * 7;
                    nxtR[k - dir] += er * 3; nxtG[k - dir] += eg * 3; nxtB[k - dir] += eb * 3;
                    nxtR[k] += er * 5; nxtG[k] += eg * 5; nxtB[k] += eb * 5;
                    nxtR[k + dir] += er; nxtG[k + dir] += eg; nxtB[k + dir] += eb;
                }
                var t1 = curR; curR = nxtR; nxtR = t1;
                var t2 = curG; curG = nxtG; nxtG = t2;
                var t3 = curB; curB = nxtB; nxtB = t3;
                Array.Clear(nxtR, 0, nxtR.Length);
                Array.Clear(nxtG, 0, nxtG.Length);
                Array.Clear(nxtB, 0, nxtB.Length);
            }
        }

        static int Clamp(int v) { return v < 0 ? 0 : v > 255 ? 255 : v; }

        // Discord's media proxy rounds every GIF colour to the nearest value
        // congruent to 4 mod 8 - five bits per channel - before it shows the
        // file. Snapping our own table to that same grid means the palette it
        // displays is the palette we chose, so the dithering still hides the
        // bands instead of being rounded into noise.
        static byte Grid(long v) { return (byte)Math.Min(252L, (v & 0xF8) + 4); }

        static int[] ReadPixels(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var px = new int[w * h];
            BitmapData bd = null;
            try
            {
                bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                for (int y = 0; y < h; y++)
                    Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), px, y * w, w);
            }
            catch
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        px[y * w + x] = bmp.GetPixel(x, y).ToArgb();
            }
            finally { if (bd != null) { try { bmp.UnlockBits(bd); } catch { } } }
            return px;
        }

        // Coverage of each pixel, for the transparent palette entry.
        static byte[] ReadAlpha(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var a = new byte[w * h];
            BitmapData bd = null;
            try
            {
                bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), row, 0, w * 4);
                    for (int x = 0; x < w; x++) a[y * w + x] = row[x * 4 + 3];
                }
            }
            catch
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++) a[y * w + x] = bmp.GetPixel(x, y).A;
            }
            finally { if (bd != null) { try { bmp.UnlockBits(bd); } catch { } } }
            return a;
        }

        // ---- LZW -----------------------------------------------------------

        static void Lzw(Stream outp, byte[] indices, int minCodeSize)
        {
            outp.WriteByte((byte)minCodeSize);
            int clear = 1 << minCodeSize, eoi = clear + 1;
            int next = eoi + 1, codeSize = minCodeSize + 1;
            var dict = new Dictionary<int, int>();
            var bytes = new List<byte>(indices.Length / 2 + 64);
            var bw = new BitWriter(bytes);

            bw.Write(clear, codeSize);
            int cur = indices.Length > 0 ? indices[0] : 0;
            for (int i = 1; i < indices.Length; i++)
            {
                int b = indices[i];
                int key = (cur << 8) | b;
                int code;
                if (dict.TryGetValue(key, out code)) { cur = code; continue; }

                bw.Write(cur, codeSize);
                if (next < 4096)
                {
                    dict[key] = next++;
                    // A code that no longer fits in codeSize bits widens the stream.
                    while (next - 1 > (1 << codeSize) - 1 && codeSize < 12) codeSize++;
                }
                else
                {
                    bw.Write(clear, codeSize);
                    dict.Clear();
                    next = eoi + 1;
                    codeSize = minCodeSize + 1;
                }
                cur = b;
            }
            bw.Write(cur, codeSize);
            bw.Write(eoi, codeSize);
            bw.Flush();

            byte[] data = bytes.ToArray();
            for (int off = 0; off < data.Length; off += 255)
            {
                int len = Math.Min(255, data.Length - off);
                outp.WriteByte((byte)len);
                outp.Write(data, off, len);
            }
            outp.WriteByte(0);
        }

        // GIF packs codes least-significant bit first.
        sealed class BitWriter
        {
            readonly List<byte> _out;
            int _acc, _bits;

            public BitWriter(List<byte> outp) { _out = outp; }

            public void Write(int code, int width)
            {
                _acc |= (code & ((1 << width) - 1)) << _bits;
                _bits += width;
                while (_bits >= 8)
                {
                    _out.Add((byte)(_acc & 0xFF));
                    _acc >>= 8;
                    _bits -= 8;
                }
            }

            public void Flush()
            {
                if (_bits <= 0) return;
                _out.Add((byte)(_acc & 0xFF));
                _acc = 0; _bits = 0;
            }
        }

        // ---- byte helpers --------------------------------------------------

        static void Text(Stream s, string text)
        {
            for (int i = 0; i < text.Length; i++) s.WriteByte((byte)text[i]);
        }

        static void Short(Stream s, int v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
        }
    }
}
