using System;
using System.Collections.Generic;
using System.IO;

namespace ManikinApp
{
    // Animated WebP with every frame the full canvas.
    //
    // libwebp's animation encoder - and therefore ffmpeg's libwebp_anim - stores
    // each frame as a sub-rectangle that alpha-blends over the previous canvas,
    // and only the first frame covers the whole picture. Discord's media decoder
    // mishandles that layout: the file loads as a broken image. Writing the
    // container here keeps the lossless VP8L frames but lays them out the way
    // every client expects, and it also carries each frame's real duration
    // instead of a rounded 30 fps cadence.
    static class WebpWriter
    {
        // stills: one standalone lossless WebP per frame, same size as the canvas.
        public static void Write(Stream output, IList<byte[]> stills, IList<int> delays, int w, int h)
        {
            if (stills == null || stills.Count == 0) throw new ArgumentException("no frames");
            var body = new MemoryStream();

            // VP8X: animation (0x02) and alpha (0x10) are both in play.
            var vp8x = new byte[10];
            vp8x[0] = 0x12;
            Put24(vp8x, 4, w - 1);
            Put24(vp8x, 7, h - 1);
            Chunk(body, "VP8X", vp8x);

            // ANIM: background is unused because every frame covers the canvas;
            // loop count 0 means forever.
            var anim = new byte[6];
            anim[0] = 0xff; anim[1] = 0xff; anim[2] = 0xff; anim[3] = 0xff;
            Chunk(body, "ANIM", anim);

            for (int i = 0; i < stills.Count; i++)
            {
                var parts = FrameChunks(stills[i]);
                if (parts.Count == 0) throw new InvalidDataException("a frame encoded to no image data");
                int inner = 0;
                foreach (var p in parts) inner += 8 + p.Data.Length + (p.Data.Length & 1);
                var anmf = new byte[16 + inner];
                Put24(anmf, 0, 0);            // x / 2
                Put24(anmf, 3, 0);            // y / 2
                Put24(anmf, 6, w - 1);
                Put24(anmf, 9, h - 1);
                int dur = delays != null && i < delays.Count ? delays[i] : 40;
                Put24(anmf, 12, Math.Max(10, Math.Min(0xFFFFFF, dur)));
                // Bit 1 set: overwrite instead of blending, so a frame with
                // partial alpha replaces the one before it rather than stacking.
                anmf[15] = 0x02;
                int at = 16;
                foreach (var p in parts)
                {
                    anmf[at] = p.Tag0; anmf[at + 1] = p.Tag1; anmf[at + 2] = p.Tag2; anmf[at + 3] = p.Tag3;
                    BitConverter.GetBytes((uint)p.Data.Length).CopyTo(anmf, at + 4);
                    Buffer.BlockCopy(p.Data, 0, anmf, at + 8, p.Data.Length);
                    at += 8 + p.Data.Length + (p.Data.Length & 1);
                }
                Chunk(body, "ANMF", anmf);
            }

            byte[] payload = body.ToArray();
            var riff = new byte[12];
            riff[0] = (byte)'R'; riff[1] = (byte)'I'; riff[2] = (byte)'F'; riff[3] = (byte)'F';
            BitConverter.GetBytes((uint)(payload.Length + 4)).CopyTo(riff, 4);
            riff[8] = (byte)'W'; riff[9] = (byte)'E'; riff[10] = (byte)'B'; riff[11] = (byte)'P';
            output.Write(riff, 0, 12);
            output.Write(payload, 0, payload.Length);
        }

        struct Part
        {
            public byte Tag0, Tag1, Tag2, Tag3;
            public byte[] Data;
        }

        // The image chunks of a standalone WebP, ready to be nested in ANMF.
        // VP8X is a canvas description and must not be copied; ALPH (lossy
        // alpha) has to travel with its VP8 frame.
        static List<Part> FrameChunks(byte[] webp)
        {
            var parts = new List<Part>(2);
            if (webp.Length < 12) return parts;
            int pos = 12;
            while (pos + 8 <= webp.Length)
            {
                int size = (int)BitConverter.ToUInt32(webp, pos + 4);
                if (size < 0 || pos + 8 + size > webp.Length) break;
                string tag = "" + (char)webp[pos] + (char)webp[pos + 1] + (char)webp[pos + 2] + (char)webp[pos + 3];
                if (tag == "VP8L" || tag == "VP8 " || tag == "ALPH")
                {
                    var data = new byte[size];
                    Buffer.BlockCopy(webp, pos + 8, data, 0, size);
                    parts.Add(new Part { Tag0 = webp[pos], Tag1 = webp[pos + 1], Tag2 = webp[pos + 2], Tag3 = webp[pos + 3], Data = data });
                }
                pos += 8 + size + (size & 1);
            }
            return parts;
        }

        static void Chunk(Stream s, string tag, byte[] body)
        {
            var hdr = new byte[8];
            for (int i = 0; i < 4; i++) hdr[i] = (byte)tag[i];
            BitConverter.GetBytes((uint)body.Length).CopyTo(hdr, 4);
            s.Write(hdr, 0, 8);
            s.Write(body, 0, body.Length);
            if ((body.Length & 1) != 0) s.WriteByte(0);
        }

        static void Put24(byte[] a, int at, int v)
        {
            a[at] = (byte)(v & 0xff);
            a[at + 1] = (byte)((v >> 8) & 0xff);
            a[at + 2] = (byte)((v >> 16) & 0xff);
        }
    }
}
