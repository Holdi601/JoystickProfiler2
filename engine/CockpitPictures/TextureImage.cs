#if NET
#nullable disable
#endif
using System;
using System.IO;
using System.IO.Compression;

namespace JoyPro.CockpitPictures
{
    // An RGBA picture (8 bits per channel, rows top to bottom) read from the texture formats games ship: DDS
    // (uncompressed, BC1/DXT1, BC2/DXT3, BC3/DXT5, BC4, BC5, BC7), TGA (uncompressed and RLE) and PNG. Only the largest
    // mip level not above MaxSize is decoded, to keep memory and time small.
    internal sealed class TextureImage
    {
        public int Width;
        public int Height;
        public byte[] Rgba;
        // The mean colour (for the flat shaded styles).
        public byte[] Mean = { 128, 128, 128, 255 };

        public static TextureImage Decode(byte[] data, string nameHint, int maxSize)
        {
            if (data == null || data.Length < 18)
                return null;
            TextureImage img = null;
            try
            {
                if (data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ')
                    img = Dds(data, maxSize);
                else if (data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
                    img = Png(data);
                else if ((nameHint ?? string.Empty).EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                    img = Tga(data);
            }
            catch (Exception)
            {
                img = null;
            }
            if (img == null)
                return null;
            while (img.Width > maxSize * 2 || img.Height > maxSize * 2)
                img = img.Half();
            img.ComputeMean();
            return img;
        }

        private void ComputeMean()
        {
            long r = 0, g = 0, b = 0, a = 0;
            int n = 0;
            int step = Math.Max(1, (Width * Height) / 4096);
            for (int i = 0; i < Width * Height; i += step)
            {
                r += Rgba[4 * i];
                g += Rgba[4 * i + 1];
                b += Rgba[4 * i + 2];
                a += Rgba[4 * i + 3];
                n++;
            }
            if (n > 0)
                Mean = new[] { (byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n) };
        }

        public TextureImage Half()
        {
            int w = Math.Max(1, Width / 2), h = Math.Max(1, Height / 2);
            byte[] o = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Min(Width - 1, 2 * x), x1 = Math.Min(Width - 1, 2 * x + 1);
                    int y0 = Math.Min(Height - 1, 2 * y), y1 = Math.Min(Height - 1, 2 * y + 1);
                    for (int c = 0; c < 4; c++)
                    {
                        int s = Rgba[(y0 * Width + x0) * 4 + c] + Rgba[(y0 * Width + x1) * 4 + c] + Rgba[(y1 * Width + x0) * 4 + c] + Rgba[(y1 * Width + x1) * 4 + c];
                        o[(y * w + x) * 4 + c] = (byte)((s + 2) / 4);
                    }
                }
            }
            return new TextureImage { Width = w, Height = h, Rgba = o };
        }

        // Bilinear sample with wrap-around; u, v in texture space (0..1, v down). Writes r, g, b, a.
        public void Sample(float u, float v, out int r, out int g, out int b, out int a)
        {
            float fx = u * Width - 0.5f, fy = v * Height - 0.5f;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            int x1 = x0 + 1, y1 = y0 + 1;
            x0 = Wrap(x0, Width);
            x1 = Wrap(x1, Width);
            y0 = Wrap(y0, Height);
            y1 = Wrap(y1, Height);
            int i00 = (y0 * Width + x0) * 4, i10 = (y0 * Width + x1) * 4, i01 = (y1 * Width + x0) * 4, i11 = (y1 * Width + x1) * 4;
            float w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty;
            r = (int)(Rgba[i00] * w00 + Rgba[i10] * w10 + Rgba[i01] * w01 + Rgba[i11] * w11);
            g = (int)(Rgba[i00 + 1] * w00 + Rgba[i10 + 1] * w10 + Rgba[i01 + 1] * w01 + Rgba[i11 + 1] * w11);
            b = (int)(Rgba[i00 + 2] * w00 + Rgba[i10 + 2] * w10 + Rgba[i01 + 2] * w01 + Rgba[i11 + 2] * w11);
            a = (int)(Rgba[i00 + 3] * w00 + Rgba[i10 + 3] * w10 + Rgba[i01 + 3] * w01 + Rgba[i11 + 3] * w11);
        }

        private static int Wrap(int i, int n)
        {
            i %= n;
            return i < 0 ? i + n : i;
        }

        // ------------------------------------------------------------------ DDS

        private static TextureImage Dds(byte[] d, int maxSize)
        {
            int height = BitConverter.ToInt32(d, 12);
            int width = BitConverter.ToInt32(d, 16);
            int mips = Math.Max(1, BitConverter.ToInt32(d, 28));
            int pfFlags = BitConverter.ToInt32(d, 80);
            string four = System.Text.Encoding.ASCII.GetString(d, 84, 4);
            int bits = BitConverter.ToInt32(d, 88);
            uint rMask = BitConverter.ToUInt32(d, 92), gMask = BitConverter.ToUInt32(d, 96), bMask = BitConverter.ToUInt32(d, 100), aMask = BitConverter.ToUInt32(d, 104);
            int offset = 128;
            string format;
            if ((pfFlags & 4) != 0)
            {
                format = four;
                if (four == "DX10")
                {
                    int dxgi = BitConverter.ToInt32(d, 128);
                    offset += 20;
                    switch (dxgi)
                    {
                        case 71:
                        case 72:
                            format = "DXT1";
                            break;
                        case 74:
                        case 75:
                            format = "DXT3";
                            break;
                        case 77:
                        case 78:
                            format = "DXT5";
                            break;
                        case 80:
                        case 81:
                            format = "BC4";
                            break;
                        case 83:
                        case 84:
                            format = "BC5";
                            break;
                        case 98:
                        case 99:
                            format = "BC7";
                            break;
                        case 28:
                        case 29:
                            format = "RGBA8";
                            break;
                        case 87:
                        case 91:
                            format = "BGRA8";
                            break;
                        default:
                            return null;
                    }
                }
                else if (four == "ATI1" || four == "BC4U")
                    format = "BC4";
                else if (four == "ATI2" || four == "BC5U")
                    format = "BC5";
            }
            else
            {
                format = "RAW";
            }
            bool block = format == "DXT1" || format == "DXT3" || format == "DXT5" || format == "BC4" || format == "BC5" || format == "BC7" || format == "DXT2" || format == "DXT4";
            int blockBytes = format == "DXT1" || format == "BC4" ? 8 : 16;
            int pixelBytes = format == "RGBA8" || format == "BGRA8" ? 4 : Math.Max(1, bits / 8);
            // Pick the mip level.
            int w = width, h = height, level = 0;
            while (level + 1 < mips && (w > maxSize || h > maxSize))
            {
                offset += Size(w, h, block, blockBytes, pixelBytes);
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
                level++;
            }
            if (offset + Size(w, h, block, blockBytes, pixelBytes) > d.Length)
                return null;
            TextureImage img = new TextureImage { Width = w, Height = h, Rgba = new byte[w * h * 4] };
            if (!block)
            {
                if (format == "RGBA8")
                    Raw(d, offset, img, 4, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000);
                else if (format == "BGRA8")
                    Raw(d, offset, img, 4, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000);
                else
                    Raw(d, offset, img, pixelBytes, rMask, gMask, bMask, (pfFlags & 1) != 0 ? aMask : 0);
                return img;
            }
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            byte[] px = new byte[64];
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    int at = offset + (by * bw + bx) * blockBytes;
                    switch (format)
                    {
                        case "DXT1":
                            Bc1(d, at, px, true);
                            break;
                        case "DXT2":
                        case "DXT3":
                            Bc1(d, at + 8, px, false);
                            for (int i = 0; i < 16; i++)
                            {
                                int nib = (d[at + i / 2] >> ((i & 1) * 4)) & 15;
                                px[i * 4 + 3] = (byte)(nib * 17);
                            }
                            break;
                        case "DXT4":
                        case "DXT5":
                            Bc1(d, at + 8, px, false);
                            Bc4(d, at, px, 3);
                            break;
                        case "BC4":
                            Bc4(d, at, px, 0);
                            for (int i = 0; i < 16; i++)
                            {
                                px[i * 4 + 1] = px[i * 4 + 2] = px[i * 4];
                                px[i * 4 + 3] = 255;
                            }
                            break;
                        case "BC5":
                            Bc4(d, at, px, 0);
                            Bc4(d, at + 8, px, 1);
                            for (int i = 0; i < 16; i++)
                            {
                                px[i * 4 + 2] = 255;
                                px[i * 4 + 3] = 255;
                            }
                            break;
                        case "BC7":
                            Bc7.DecodeBlock(d, at, px);
                            break;
                    }
                    for (int y = 0; y < 4; y++)
                    {
                        int py = by * 4 + y;
                        if (py >= h)
                            break;
                        for (int x = 0; x < 4; x++)
                        {
                            int pxx = bx * 4 + x;
                            if (pxx >= w)
                                break;
                            Buffer.BlockCopy(px, (y * 4 + x) * 4, img.Rgba, (py * w + pxx) * 4, 4);
                        }
                    }
                }
            }
            return img;
        }

        private static int Size(int w, int h, bool block, int blockBytes, int pixelBytes)
        {
            return block ? Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes : w * h * pixelBytes;
        }

        private static void Raw(byte[] d, int offset, TextureImage img, int bytes, uint rm, uint gm, uint bm, uint am)
        {
            for (int i = 0; i < img.Width * img.Height; i++)
            {
                uint v = 0;
                for (int b = 0; b < bytes && b < 4; b++)
                    v |= (uint)d[offset + i * bytes + b] << (8 * b);
                img.Rgba[i * 4] = Channel(v, rm);
                img.Rgba[i * 4 + 1] = Channel(v, gm);
                img.Rgba[i * 4 + 2] = Channel(v, bm);
                img.Rgba[i * 4 + 3] = am == 0 ? (byte)255 : Channel(v, am);
            }
        }

        private static byte Channel(uint v, uint mask)
        {
            if (mask == 0)
                return 0;
            int shift = 0;
            while (((mask >> shift) & 1) == 0)
                shift++;
            uint max = mask >> shift;
            return (byte)(((v & mask) >> shift) * 255 / max);
        }

        private static void Bc1(byte[] d, int at, byte[] px, bool alpha)
        {
            int c0 = d[at] | (d[at + 1] << 8), c1 = d[at + 2] | (d[at + 3] << 8);
            int[] r = new int[4], g = new int[4], b = new int[4], a = { 255, 255, 255, 255 };
            r[0] = ((c0 >> 11) & 31) * 255 / 31;
            g[0] = ((c0 >> 5) & 63) * 255 / 63;
            b[0] = (c0 & 31) * 255 / 31;
            r[1] = ((c1 >> 11) & 31) * 255 / 31;
            g[1] = ((c1 >> 5) & 63) * 255 / 63;
            b[1] = (c1 & 31) * 255 / 31;
            if (c0 > c1 || !alpha)
            {
                r[2] = (2 * r[0] + r[1]) / 3;
                g[2] = (2 * g[0] + g[1]) / 3;
                b[2] = (2 * b[0] + b[1]) / 3;
                r[3] = (r[0] + 2 * r[1]) / 3;
                g[3] = (g[0] + 2 * g[1]) / 3;
                b[3] = (b[0] + 2 * b[1]) / 3;
            }
            else
            {
                r[2] = (r[0] + r[1]) / 2;
                g[2] = (g[0] + g[1]) / 2;
                b[2] = (b[0] + b[1]) / 2;
                r[3] = g[3] = b[3] = 0;
                a[3] = 0;
            }
            uint bitsv = BitConverter.ToUInt32(d, at + 4);
            for (int i = 0; i < 16; i++)
            {
                int k = (int)((bitsv >> (2 * i)) & 3);
                px[i * 4] = (byte)r[k];
                px[i * 4 + 1] = (byte)g[k];
                px[i * 4 + 2] = (byte)b[k];
                px[i * 4 + 3] = (byte)a[k];
            }
        }

        private static void Bc4(byte[] d, int at, byte[] px, int channel)
        {
            int a0 = d[at], a1 = d[at + 1];
            int[] v = new int[8];
            v[0] = a0;
            v[1] = a1;
            if (a0 > a1)
            {
                for (int i = 1; i <= 6; i++)
                    v[i + 1] = ((7 - i) * a0 + i * a1) / 7;
            }
            else
            {
                for (int i = 1; i <= 4; i++)
                    v[i + 1] = ((5 - i) * a0 + i * a1) / 5;
                v[6] = 0;
                v[7] = 255;
            }
            ulong bitsv = 0;
            for (int i = 0; i < 6; i++)
                bitsv |= (ulong)d[at + 2 + i] << (8 * i);
            for (int i = 0; i < 16; i++)
                px[i * 4 + channel] = (byte)v[(int)((bitsv >> (3 * i)) & 7)];
        }

        // ------------------------------------------------------------------ TGA

        private static TextureImage Tga(byte[] d)
        {
            int idLen = d[0], cmapType = d[1], type = d[2];
            if (cmapType != 0 || (type != 2 && type != 10 && type != 3 && type != 11))
                return null;
            int w = d[12] | (d[13] << 8), h = d[14] | (d[15] << 8), bpp = d[16], desc = d[17];
            if (w <= 0 || h <= 0 || (bpp != 24 && bpp != 32 && bpp != 8))
                return null;
            int bytes = bpp / 8;
            int p = 18 + idLen;
            byte[] o = new byte[w * h * 4];
            int n = 0;
            bool rle = type >= 9;
            while (n < w * h && p < d.Length)
            {
                int run = 1;
                bool repeat = false;
                if (rle)
                {
                    int hdr = d[p++];
                    run = (hdr & 127) + 1;
                    repeat = (hdr & 128) != 0;
                }
                for (int i = 0; i < run && n < w * h; i++)
                {
                    int q = repeat ? p : p + i * bytes;
                    if (q + bytes > d.Length)
                        return null;
                    if (bytes == 1)
                    {
                        o[n * 4] = o[n * 4 + 1] = o[n * 4 + 2] = d[q];
                        o[n * 4 + 3] = 255;
                    }
                    else
                    {
                        o[n * 4] = d[q + 2];
                        o[n * 4 + 1] = d[q + 1];
                        o[n * 4 + 2] = d[q];
                        o[n * 4 + 3] = bytes == 4 ? d[q + 3] : (byte)255;
                    }
                    n++;
                }
                p += repeat ? bytes : run * bytes;
            }
            // Origin at the bottom unless bit 5 of the descriptor says top.
            if ((desc & 0x20) == 0)
            {
                byte[] row = new byte[w * 4];
                for (int y = 0; y < h / 2; y++)
                {
                    Buffer.BlockCopy(o, y * w * 4, row, 0, w * 4);
                    Buffer.BlockCopy(o, (h - 1 - y) * w * 4, o, y * w * 4, w * 4);
                    Buffer.BlockCopy(row, 0, o, (h - 1 - y) * w * 4, w * 4);
                }
            }
            return new TextureImage { Width = w, Height = h, Rgba = o };
        }

        // ------------------------------------------------------------------ PNG

        public static TextureImage Png(byte[] d)
        {
            int p = 8;
            int w = 0, h = 0, depth = 0, color = 0, interlace = 0;
            MemoryStream idat = new MemoryStream();
            byte[] palette = null, trns = null;
            while (p + 8 <= d.Length)
            {
                int len = (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
                string type = System.Text.Encoding.ASCII.GetString(d, p + 4, 4);
                int body = p + 8;
                if (len < 0 || body + len > d.Length)
                    break;
                if (type == "IHDR")
                {
                    w = (d[body] << 24) | (d[body + 1] << 16) | (d[body + 2] << 8) | d[body + 3];
                    h = (d[body + 4] << 24) | (d[body + 5] << 16) | (d[body + 6] << 8) | d[body + 7];
                    depth = d[body + 8];
                    color = d[body + 9];
                    interlace = d[body + 12];
                }
                else if (type == "PLTE")
                {
                    palette = new byte[len];
                    Array.Copy(d, body, palette, 0, len);
                }
                else if (type == "tRNS")
                {
                    trns = new byte[len];
                    Array.Copy(d, body, trns, 0, len);
                }
                else if (type == "IDAT")
                    idat.Write(d, body, len);
                else if (type == "IEND")
                    break;
                p = body + len + 4;
            }
            if (w <= 0 || h <= 0 || depth != 8 || interlace != 0)
                return null;
            int channels = color == 0 ? 1 : color == 2 ? 3 : color == 3 ? 1 : color == 4 ? 2 : color == 6 ? 4 : 0;
            if (channels == 0)
                return null;
            byte[] raw;
            idat.Position = 2; // zlib header
            using (DeflateStream z = new DeflateStream(idat, CompressionMode.Decompress))
            using (MemoryStream o = new MemoryStream())
            {
                z.CopyTo(o);
                raw = o.ToArray();
            }
            int stride = w * channels;
            byte[] cur = new byte[stride], prev = new byte[stride];
            byte[] rgba = new byte[w * h * 4];
            int q = 0;
            for (int y = 0; y < h; y++)
            {
                if (q + 1 + stride > raw.Length)
                    return null;
                int filter = raw[q++];
                for (int i = 0; i < stride; i++)
                {
                    int x = raw[q + i];
                    int a = i >= channels ? cur[i - channels] : 0;
                    int b = prev[i];
                    int c = i >= channels ? prev[i - channels] : 0;
                    switch (filter)
                    {
                        case 1:
                            x += a;
                            break;
                        case 2:
                            x += b;
                            break;
                        case 3:
                            x += (a + b) / 2;
                            break;
                        case 4:
                            {
                                int pp = a + b - c, pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
                                x += pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                                break;
                            }
                    }
                    cur[i] = (byte)x;
                }
                q += stride;
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    switch (color)
                    {
                        case 0:
                            rgba[o] = rgba[o + 1] = rgba[o + 2] = cur[x];
                            rgba[o + 3] = 255;
                            break;
                        case 2:
                            rgba[o] = cur[3 * x];
                            rgba[o + 1] = cur[3 * x + 1];
                            rgba[o + 2] = cur[3 * x + 2];
                            rgba[o + 3] = 255;
                            break;
                        case 3:
                            {
                                int k = cur[x];
                                if (palette != null && 3 * k + 2 < palette.Length)
                                {
                                    rgba[o] = palette[3 * k];
                                    rgba[o + 1] = palette[3 * k + 1];
                                    rgba[o + 2] = palette[3 * k + 2];
                                }
                                rgba[o + 3] = trns != null && k < trns.Length ? trns[k] : (byte)255;
                                break;
                            }
                        case 4:
                            rgba[o] = rgba[o + 1] = rgba[o + 2] = cur[2 * x];
                            rgba[o + 3] = cur[2 * x + 1];
                            break;
                        case 6:
                            Buffer.BlockCopy(cur, 4 * x, rgba, o, 4);
                            break;
                    }
                }
                byte[] t = prev;
                prev = cur;
                cur = t;
            }
            return new TextureImage { Width = w, Height = h, Rgba = rgba };
        }
    }
}
