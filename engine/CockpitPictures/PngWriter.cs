#if NET
#nullable disable
#endif
using System;
using System.IO;
using System.IO.Compression;

namespace JoyPro.CockpitPictures
{
    // Writes 8-bit RGB or RGBA pictures as PNG (zlib via DeflateStream), without System.Drawing.
    internal static class PngWriter
    {
        public static void Write(string path, byte[] pixels, int width, int height, int channels)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, Encode(pixels, width, height, channels));
        }

        public static byte[] Encode(byte[] pixels, int width, int height, int channels)
        {
            using (MemoryStream o = new MemoryStream())
            {
                o.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }, 0, 8);
                byte[] ihdr = new byte[13];
                Be(ihdr, 0, width);
                Be(ihdr, 4, height);
                ihdr[8] = 8;
                ihdr[9] = (byte)(channels == 4 ? 6 : channels == 1 ? 0 : 2);
                Chunk(o, "IHDR", ihdr);
                int stride = width * channels;
                byte[] raw = new byte[(stride + 1) * height];
                for (int y = 0; y < height; y++)
                {
                    // Filter "Up" for rows below the first (smaller files for pictures).
                    int row = y * (stride + 1);
                    raw[row] = (byte)(y == 0 ? 0 : 2);
                    for (int i = 0; i < stride; i++)
                    {
                        int v = pixels[y * stride + i];
                        if (y > 0)
                            v -= pixels[(y - 1) * stride + i];
                        raw[row + 1 + i] = (byte)v;
                    }
                }
                using (MemoryStream z = new MemoryStream())
                {
                    z.WriteByte(0x78);
                    z.WriteByte(0x9C);
                    using (DeflateStream d = new DeflateStream(z, CompressionLevel.Optimal, true))
                        d.Write(raw, 0, raw.Length);
                    uint adler = Adler32(raw);
                    z.WriteByte((byte)(adler >> 24));
                    z.WriteByte((byte)(adler >> 16));
                    z.WriteByte((byte)(adler >> 8));
                    z.WriteByte((byte)adler);
                    Chunk(o, "IDAT", z.ToArray());
                }
                Chunk(o, "IEND", new byte[0]);
                return o.ToArray();
            }
        }

        private static void Be(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 24);
            b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8);
            b[at + 3] = (byte)v;
        }

        private static void Chunk(Stream o, string type, byte[] data)
        {
            byte[] len = new byte[4];
            Be(len, 0, data.Length);
            o.Write(len, 0, 4);
            byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
            o.Write(t, 0, 4);
            o.Write(data, 0, data.Length);
            uint crc = Crc(t, 0xFFFFFFFF);
            crc = Crc(data, crc) ^ 0xFFFFFFFF;
            byte[] c = new byte[4];
            Be(c, 0, (int)crc);
            o.Write(c, 0, 4);
        }

        private static uint[] crcTable;

        private static uint Crc(byte[] data, uint crc)
        {
            if (crcTable == null)
            {
                uint[] t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                crcTable = t;
            }
            foreach (byte b in data)
                crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            int i = 0;
            while (i < data.Length)
            {
                int n = Math.Min(5552, data.Length - i);
                for (int k = 0; k < n; k++)
                {
                    a += data[i++];
                    b += a;
                }
                a %= 65521;
                b %= 65521;
            }
            return (b << 16) | a;
        }
    }
}
