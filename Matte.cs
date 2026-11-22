using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ManikinApp
{
    // Recovers the avatar's real alpha from two renders of the same frame, one
    // over black and one over white. Flash cannot draw its stage transparent, so
    // a cut-out has to be measured instead of asked for:
    //
    //   pixel over black  C = a*F
    //   pixel over white  W = a*F + (1-a)*255
    //   so                C - W = (1-a)*255   ->   a = 1 + (C-W)/255
    //
    // A pixel the character covers solidly is identical in both renders (a=1);
    // background is 0 in the first and 255 in the second (a=0); an anti-aliased
    // edge lands in between, which is exactly the alpha it should keep. The
    // colour is then un-blended from the black render (F = C/a), so no dark halo
    // is left behind the way a plain colour key would leave one.
    static class Matte
    {
        // The frame with its alpha, untrimmed: clips need one box for every frame,
        // so trimming happens afterwards over the whole take (TrimAll).
        public static Bitmap Recover(Bitmap dark, Bitmap light)
        {
            if (dark == null || light == null) return null;
            int w = Math.Min(dark.Width, light.Width), h = Math.Min(dark.Height, light.Height);
            if (w < 2 || h < 2) return null;

            var outp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData bdD = null, bdL = null, bdO = null;
            try
            {
                var rect = new Rectangle(0, 0, w, h);
                bdD = dark.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                bdL = light.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                bdO = outp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                var rowD = new byte[w * 4];
                var rowL = new byte[w * 4];
                var rowO = new byte[w * 4];

                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(bdD.Scan0, y * bdD.Stride), rowD, 0, w * 4);
                    Marshal.Copy(IntPtr.Add(bdL.Scan0, y * bdL.Stride), rowL, 0, w * 4);
                    Array.Clear(rowO, 0, rowO.Length);
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 4;   // BGRA in memory
                        int db = rowD[i] - rowL[i];
                        int dg = rowD[i + 1] - rowL[i + 1];
                        int dr = rowD[i + 2] - rowL[i + 2];
                        double a = 1.0 + (db + dg + dr) / 765.0;
                        // Below one 8-bit step of separation there is no evidence
                        // of coverage: call it background and keep it fully clear.
                        if (a <= 0.006) continue;
                        if (a > 1.0) a = 1.0;
                        int alpha = (int)Math.Round(a * 255.0);
                        if (alpha < 4) continue;

                        int b = (int)Math.Round(rowD[i] / a);
                        int g = (int)Math.Round(rowD[i + 1] / a);
                        int r = (int)Math.Round(rowD[i + 2] / a);
                        rowO[i] = (byte)(b > 255 ? 255 : b);
                        rowO[i + 1] = (byte)(g > 255 ? 255 : g);
                        rowO[i + 2] = (byte)(r > 255 ? 255 : r);
                        rowO[i + 3] = (byte)alpha;
                    }
                    Marshal.Copy(rowO, 0, IntPtr.Add(bdO.Scan0, y * bdO.Stride), w * 4);
                }
                return outp;
            }
            finally
            {
                if (bdO != null) { try { outp.UnlockBits(bdO); } catch { } }
                if (bdL != null) { try { light.UnlockBits(bdL); } catch { } }
                if (bdD != null) { try { dark.UnlockBits(bdD); } catch { } }
            }
        }

        // One frame, trimmed to its own content: the still cut-out.
        public static Bitmap Cut(Bitmap dark, Bitmap light)
        {
            var full = Recover(dark, light);
            if (full == null) return null;
            var box = ContentBox(full, 2);
            if (box.Width >= full.Width && box.Height >= full.Height) return full;
            var cut = full.Clone(box, PixelFormat.Format32bppArgb);
            full.Dispose();
            return cut;
        }

        // Crops every frame of a take to the same box (the union of their alpha),
        // so nothing moves relative to the frame while the clip plays. The input
        // frames are disposed; the returned list is the caller's to dispose.
        public static List<Bitmap> TrimAll(IList<Bitmap> frames)
        {
            if (frames == null || frames.Count == 0) return null;
            int w = frames[0].Width, h = frames[0].Height;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            foreach (var f in frames)
            {
                var box = ContentBox(f, 0);
                if (box.Width <= 0) continue;
                if (box.X < minX) minX = box.X;
                if (box.Y < minY) minY = box.Y;
                if (box.Right > maxX) maxX = box.Right;
                if (box.Bottom > maxY) maxY = box.Bottom;
            }
            if (maxX < minX || maxY < minY) return new List<Bitmap>(frames);

            const int pad = 2;
            int x0 = Math.Max(0, minX - pad), y0 = Math.Max(0, minY - pad);
            int x1 = Math.Min(w, maxX + pad), y1 = Math.Min(h, maxY + pad);
            var crop = new Rectangle(x0, y0, x1 - x0, y1 - y0);
            var outp = new List<Bitmap>(frames.Count);
            foreach (var f in frames)
            {
                if (crop.Width >= w && crop.Height >= h)
                {
                    outp.Add(f);
                    continue;
                }
                outp.Add(f.Clone(crop, PixelFormat.Format32bppArgb));
                try { f.Dispose(); } catch { }
            }
            return outp;
        }

        // Box of the pixels that have any coverage at all, grown by pad.
        static Rectangle ContentBox(Bitmap b, int pad)
        {
            int w = b.Width, h = b.Height;
            if (w < 1 || h < 1) return Rectangle.Empty;
            BitmapData bd = null;
            try
            {
                bd = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var row = new byte[w * 4];
                int minX = w, minY = h, maxX = -1, maxY = -1;
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), row, 0, w * 4);
                    for (int x = 0; x < w; x++)
                    {
                        if (row[x * 4 + 3] < 4) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
                if (maxX < minX || maxY < minY) return Rectangle.Empty;
                int x0 = Math.Max(0, minX - pad), y0 = Math.Max(0, minY - pad);
                int x1 = Math.Min(w - 1, maxX + pad), y1 = Math.Min(h - 1, maxY + pad);
                return new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            }
            finally { if (bd != null) { try { b.UnlockBits(bd); } catch { } } }
        }
    }
}
