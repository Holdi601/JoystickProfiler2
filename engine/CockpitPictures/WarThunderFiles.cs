#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace JoyPro.CockpitPictures
{
    // War Thunder's plain (not obfuscated) resource formats, read only. The layouts are DagorEngine's (open source,
    // BSD-3: prog/engine/gameRes/grpData.h, sharedInclude/3d/ddsxTex.h, ioSys/dataBlock/blk_serialize.cpp).

    // A GRP2 resource pack (content.hq\pkg_cockpits\res\*.grp): a plain index of names, class ids and file offsets.
    internal sealed class WarThunderGrp
    {
        public const uint DynModelClass = 0xB4B7D9C4;
        public const uint SkeletonClass = 0x56F81B6D;
        public const uint AnimCharClass = 0xA6F87A9B;

        public sealed class Entry
        {
            public string Name = string.Empty;
            public uint ClassId;
            public long Offset;
            public int Length;
        }

        public string Path = string.Empty;
        public List<Entry> Entries = new List<Entry>();

        public static WarThunderGrp Read(string path)
        {
            using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] head = new byte[16];
                if (f.Read(head, 0, 16) != 16)
                    throw new InvalidDataException("not a GRP2 pack");
                int full = BitConverter.ToInt32(head, 8);
                if (full < 48 || full > 64 << 20)
                    throw new InvalidDataException("not a GRP2 pack");
                byte[] d = new byte[16 + full];
                Buffer.BlockCopy(head, 0, d, 0, 16);
                int got = 16;
                while (got < d.Length)
                {
                    int n = f.Read(d, got, d.Length - got);
                    if (n <= 0)
                        break;
                    got += n;
                }
                WarThunderGrp g = Parse(d, f.Length);
                g.Path = path;
                return g;
            }
        }

        // The header and description block of a pack ("GRP2", desc size, full size, rest size; then three tables:
        // names (file offsets), resources (class, offset, name index), resource data).
        public static WarThunderGrp Parse(byte[] d, long fileLength)
        {
            if (d.Length < 64 || d[0] != 'G' || d[1] != 'R' || d[2] != 'P' || d[3] != '2')
                throw new InvalidDataException("not a GRP2 pack");
            int namesOfs = BitConverter.ToInt32(d, 16), namesCount = BitConverter.ToInt32(d, 20);
            int resOfs = BitConverter.ToInt32(d, 32), resCount = BitConverter.ToInt32(d, 36);
            if (namesCount < 0 || resCount < 0 || namesOfs + namesCount * 4L > d.Length || resOfs + resCount * 12L > d.Length)
                throw new InvalidDataException("GRP2 tables out of range");
            string[] names = new string[namesCount];
            for (int i = 0; i < namesCount; i++)
            {
                int o = BitConverter.ToInt32(d, namesOfs + 4 * i);
                names[i] = o >= 0 && o < d.Length ? CString(d, o) : string.Empty;
            }
            List<Entry> list = new List<Entry>();
            for (int i = 0; i < resCount; i++)
            {
                int at = resOfs + 12 * i;
                int nameIndex = BitConverter.ToUInt16(d, at + 8);
                list.Add(new Entry
                {
                    ClassId = BitConverter.ToUInt32(d, at),
                    Offset = BitConverter.ToUInt32(d, at + 4),
                    Name = nameIndex < names.Length ? names[nameIndex] : string.Empty
                });
            }
            // The length of a resource runs to the next one in the file.
            List<Entry> sorted = new List<Entry>(list);
            sorted.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            for (int i = 0; i < sorted.Count; i++)
            {
                long end = i + 1 < sorted.Count ? sorted[i + 1].Offset : fileLength;
                sorted[i].Length = (int)Math.Max(0, Math.Min(int.MaxValue, end - sorted[i].Offset));
            }
            return new WarThunderGrp { Entries = list };
        }

        public Entry Find(string name, uint classId)
        {
            foreach (Entry e in Entries)
            {
                if (e.ClassId == classId && string.Equals(e.Name, name, StringComparison.Ordinal))
                    return e;
            }
            return null;
        }

        public byte[] ReadEntry(Entry e)
        {
            using (FileStream f = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                f.Seek(e.Offset, SeekOrigin.Begin);
                byte[] b = new byte[e.Length];
                int got = 0;
                while (got < b.Length)
                {
                    int n = f.Read(b, got, b.Length - got);
                    if (n <= 0)
                        break;
                    got += n;
                }
                return b;
            }
        }

        public static string CString(byte[] d, int o)
        {
            int e = o;
            while (e < d.Length && d[e] != 0)
                e++;
            return Encoding.UTF8.GetString(d, o, e - o);
        }
    }

    // The 32-byte DDSx header of a DagorEngine texture (ddsxTex.h).
    internal sealed class DdsxHeader
    {
        public const uint CompressionMask = 0xE0000000;
        public const uint Zstd = 0x20000000;
        public const uint SevenZip = 0x40000000;
        public const uint Oodle = 0x60000000;
        public const uint Zlib = 0x80000000;
        public const uint RevMipOrder = 0x00040000;
        public const uint HqPart = 0x00080000;
        public const uint CubeTex = 0x0800;
        public const uint VolTex = 0x1000;

        public uint Format;          // a FourCC ("DXT1", "BC7 ") or a D3DFMT number
        public uint Flags;
        public int Width, Height, Levels, HqLevels, Depth, BitsPerPixel, DxtShift;
        public int MemSize, PackedSize;

        public static DdsxHeader Parse(byte[] d, int o)
        {
            if (d.Length < o + 32 || d[o] != 'D' || d[o + 1] != 'D' || d[o + 2] != 'S' || d[o + 3] != 'x')
                return null;
            return new DdsxHeader
            {
                Format = BitConverter.ToUInt32(d, o + 4),
                Flags = BitConverter.ToUInt32(d, o + 8),
                Width = BitConverter.ToUInt16(d, o + 12),
                Height = BitConverter.ToUInt16(d, o + 14),
                Levels = d[o + 16],
                HqLevels = d[o + 17],
                Depth = BitConverter.ToUInt16(d, o + 18),
                BitsPerPixel = BitConverter.ToUInt16(d, o + 20),
                DxtShift = d[o + 23] & 0xF,
                MemSize = BitConverter.ToInt32(d, o + 24),
                PackedSize = BitConverter.ToInt32(d, o + 28)
            };
        }

        public uint Compression { get { return Flags & CompressionMask; } }

        public string FourCC
        {
            get
            {
                byte[] b = BitConverter.GetBytes(Format);
                foreach (byte c in b)
                {
                    if (c < 32 || c > 126)
                        return string.Empty;
                }
                return Encoding.ASCII.GetString(b);
            }
        }

        // Bytes of one surface of a level (ddsxTex.h calcSurfaceSz): blocks of 4x4 pixels << dxtShift, else pixels.
        public int SurfaceSize(int level)
        {
            int w = Math.Max(1, Width >> level), h = Math.Max(1, Height >> level);
            if (DxtShift == 0)
                return h * w * BitsPerPixel / 8;
            return (Math.Max(1, w >> 2) * Math.Max(1, h >> 2)) << DxtShift;
        }
    }

    // A DxP2 texture pack (*.dxp.bin): names, DDSx headers and the place of each texture's data in the file.
    internal sealed class WarThunderDxp
    {
        public sealed class Texture
        {
            public string Name = string.Empty;
            public DdsxHeader Header;
            public long Offset;
            public int Size;
            public string Pack = string.Empty;
        }

        public List<Texture> Textures = new List<Texture>();

        public static WarThunderDxp Read(string path)
        {
            using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] head = new byte[16];
                if (f.Read(head, 0, 16) != 16 || head[0] != 'D' || head[1] != 'x' || head[2] != 'P' || head[3] != '2')
                    return new WarThunderDxp();
                int dirSize = BitConverter.ToInt32(head, 12);
                if (dirSize <= 48 || dirSize > 64 << 20)
                    return new WarThunderDxp();
                byte[] d = new byte[16 + dirSize];
                Buffer.BlockCopy(head, 0, d, 0, 16);
                int got = 16;
                while (got < d.Length)
                {
                    int n = f.Read(d, got, d.Length - got);
                    if (n <= 0)
                        break;
                    got += n;
                }
                WarThunderDxp p = Parse(d);
                foreach (Texture t in p.Textures)
                    t.Pack = path;
                return p;
            }
        }

        // "DxP2", version, count, directory size; then three tables (names, DDSx headers, data records) with offsets
        // relative to byte 16. Version 2 records are 24 bytes (texture id, file offset at 12, packed size at 16).
        public static WarThunderDxp Parse(byte[] d)
        {
            WarThunderDxp p = new WarThunderDxp();
            if (d.Length < 64 || d[0] != 'D' || d[1] != 'x' || d[2] != 'P' || d[3] != '2')
                return p;
            int version = BitConverter.ToInt32(d, 4);
            const int B = 16;
            int namesOfs = BitConverter.ToInt32(d, B), namesCount = BitConverter.ToInt32(d, B + 4);
            int hdrOfs = BitConverter.ToInt32(d, B + 16), hdrCount = BitConverter.ToInt32(d, B + 20);
            int recOfs = BitConverter.ToInt32(d, B + 32), recCount = BitConverter.ToInt32(d, B + 36);
            int recSize = version == 2 ? 24 : 12;
            int n = Math.Min(namesCount, Math.Min(hdrCount, recCount));
            if (n <= 0 || B + namesOfs + 8L * namesCount > d.Length || B + hdrOfs + 32L * hdrCount > d.Length || B + recOfs + (long)recSize * recCount > d.Length)
                return p;
            for (int i = 0; i < n; i++)
            {
                int no = BitConverter.ToInt32(d, B + namesOfs + 8 * i);
                if (no < 0 || B + no >= d.Length)
                    continue;
                DdsxHeader h = DdsxHeader.Parse(d, B + hdrOfs + 32 * i);
                if (h == null)
                    continue;
                int r = B + recOfs + recSize * i;
                long ofs = version == 2 ? BitConverter.ToUInt32(d, r + 12) : BitConverter.ToUInt32(d, r + 4);
                int size = version == 2 ? BitConverter.ToInt32(d, r + 16) : BitConverter.ToInt32(d, r + 8);
                p.Textures.Add(new Texture { Name = WarThunderGrp.CString(d, B + no), Header = h, Offset = ofs, Size = size });
            }
            return p;
        }

        // "mig_21_f13_cockpit_c$hq*" / "..._c*?na16" / "..._c$tq*" -> "mig_21_f13_cockpit_c"; the quality: 2 = hq,
        // 1 = base, 0 = thumbnail.
        public static string BaseName(string name, out int quality)
        {
            string s = name ?? string.Empty;
            quality = 1;
            int q = s.IndexOf('?');
            if (q >= 0)
                s = s.Substring(0, q);
            s = s.TrimEnd('*');
            if (s.EndsWith("$hq", StringComparison.OrdinalIgnoreCase))
            {
                quality = 2;
                s = s.Substring(0, s.Length - 3);
            }
            else if (s.EndsWith("$tq", StringComparison.OrdinalIgnoreCase))
            {
                quality = 0;
                s = s.Substring(0, s.Length - 3);
            }
            return s.TrimEnd('*').ToLowerInvariant();
        }
    }

    // DagorEngine's binary BLK (blk_serialize.cpp, stream format 1: the full block with its own name map).
    internal sealed class WarThunderBlk
    {
        public string Name = string.Empty;
        public List<KeyValuePair<string, object>> Params = new List<KeyValuePair<string, object>>();
        public List<WarThunderBlk> Blocks = new List<WarThunderBlk>();

        public object Get(string name)
        {
            foreach (KeyValuePair<string, object> p in Params)
            {
                if (p.Key == name)
                    return p.Value;
            }
            return null;
        }

        public string GetString(string name)
        {
            return Get(name) as string;
        }

        public int GetInt(string name, int def)
        {
            object o = Get(name);
            return o is int ? (int)o : def;
        }

        public WarThunderBlk Block(string name)
        {
            foreach (WarThunderBlk b in Blocks)
            {
                if (b.Name == name)
                    return b;
            }
            return null;
        }

        private static uint Var(byte[] d, ref int p)
        {
            uint v = 0;
            for (int i = 0; i < 5 && p < d.Length; i++)
            {
                byte b = d[p++];
                v |= (uint)(b & 0x7F) << (7 * i);
                if ((b & 0x80) == 0)
                    break;
            }
            return v;
        }

        // names (count, size, zero-terminated strings), block count, param count, complex data size, the complex data,
        // the params (8 bytes: name id:24 | type:8, value or offset), the blocks (name id + 1, param count, block
        // count, first child block) in breadth-first order.
        public static WarThunderBlk Parse(byte[] d, int start)
        {
            int p = start;
            if (p >= d.Length || d[p] != 1)
                throw new InvalidDataException("not a binary BLK (format 1)");
            p++;
            uint nameCount = Var(d, ref p);
            List<string> names = new List<string>();
            if (nameCount > 0)
            {
                int size = (int)Var(d, ref p);
                int end = p + size;
                int s = p;
                while (names.Count < nameCount && s < end)
                {
                    int e = s;
                    while (e < end && d[e] != 0)
                        e++;
                    names.Add(Encoding.UTF8.GetString(d, s, e - s));
                    s = e + 1;
                }
                p = end;
            }
            int blockCount = (int)Var(d, ref p);
            int paramCount = (int)Var(d, ref p);
            int complexSize = (int)Var(d, ref p);
            int complex = p;
            p += complexSize;
            int paramsAt = p;
            p += 8 * paramCount;
            if (p > d.Length || blockCount <= 0)
                throw new InvalidDataException("binary BLK out of range");
            WarThunderBlk[] blocks = new WarThunderBlk[blockCount];
            int[] first = new int[blockCount], count = new int[blockCount];
            int paramIndex = 0;
            for (int i = 0; i < blockCount; i++)
            {
                int nameId = (int)Var(d, ref p) - 1;
                int pc = (int)Var(d, ref p);
                int bc = (int)Var(d, ref p);
                int fb = bc > 0 ? (int)Var(d, ref p) : 0;
                WarThunderBlk b = new WarThunderBlk { Name = nameId >= 0 && nameId < names.Count ? names[nameId] : string.Empty };
                for (int k = 0; k < pc && paramIndex < paramCount; k++, paramIndex++)
                {
                    int at = paramsAt + 8 * paramIndex;
                    uint head = BitConverter.ToUInt32(d, at);
                    uint v = BitConverter.ToUInt32(d, at + 4);
                    int pn = (int)(head & 0xFFFFFF);
                    b.Params.Add(new KeyValuePair<string, object>(pn < names.Count ? names[pn] : string.Empty, Value(d, complex, complexSize, names, (int)(head >> 24), v)));
                }
                blocks[i] = b;
                first[i] = fb;
                count[i] = bc;
            }
            for (int i = 0; i < blockCount; i++)
            {
                for (int k = 0; k < count[i]; k++)
                {
                    int c = first[i] + k;
                    if (c > i && c < blockCount)
                        blocks[i].Blocks.Add(blocks[c]);
                }
            }
            return blocks[0];
        }

        private static object Value(byte[] d, int complex, int complexSize, List<string> names, int type, uint v)
        {
            switch (type)
            {
                case 1: // string: a name map id (top bit) or an offset into the complex data
                    if ((v & 0x80000000) != 0)
                    {
                        int id = (int)(v & 0x7FFFFFFF);
                        return id < names.Count ? names[id] : string.Empty;
                    }
                    return v < complexSize ? WarThunderGrp.CString(d, complex + (int)v) : string.Empty;
                case 2:
                    return (int)v;
                case 3:
                    return BitConverter.ToSingle(BitConverter.GetBytes(v), 0);
                case 9:
                    return (v & 0xFF) != 0;
                case 4:
                case 5:
                case 6:
                {
                    int n = type == 4 ? 2 : type == 5 ? 3 : 4;
                    float[] f = new float[n];
                    for (int i = 0; i < n; i++)
                        f[i] = v + 4 * i + 4 <= complexSize ? BitConverter.ToSingle(d, complex + (int)v + 4 * i) : 0;
                    return f;
                }
                case 7:
                case 8:
                case 13:
                {
                    int n = type == 7 ? 2 : type == 8 ? 3 : 4;
                    int[] a = new int[n];
                    for (int i = 0; i < n; i++)
                        a[i] = v + 4 * i + 4 <= complexSize ? BitConverter.ToInt32(d, complex + (int)v + 4 * i) : 0;
                    return a;
                }
                case 10:
                    return v;
                default:
                    return null;
            }
        }
    }

    // meshoptimizer's index sequence codec (meshopt_decodeIndexSequence), used for DagorEngine's packed index
    // buffers. Ported from meshoptimizer (MIT License, Copyright (c) 2016-2020 Arseny Kapoulkine).
    internal static class MeshoptIndexSequence
    {
        public static int[] Decode(byte[] buffer, int offset, int length, int indexCount)
        {
            if (length < 1 + indexCount + 4)
                throw new InvalidDataException("packed index buffer too short");
            if ((buffer[offset] & 0xF0) != 0xD0 || (buffer[offset] & 0x0F) > 1)
                throw new InvalidDataException("not a meshopt index sequence");
            int p = offset + 1;
            int safeEnd = offset + length - 4;
            uint last0 = 0, last1 = 0;
            int[] result = new int[indexCount];
            for (int i = 0; i < indexCount; i++)
            {
                if (p >= safeEnd)
                    throw new InvalidDataException("packed index buffer ends early");
                uint v = buffer[p++];
                if (v >= 128)
                {
                    v &= 127;
                    int shift = 7;
                    for (int k = 0; k < 4; k++)
                    {
                        byte g = buffer[p++];
                        v |= (uint)(g & 127) << shift;
                        shift += 7;
                        if (g < 128)
                            break;
                    }
                }
                uint current = v & 1;
                v >>= 1;
                uint d = (v >> 1) ^ (uint)-(int)(v & 1);
                uint index = (current == 0 ? last0 : last1) + d;
                if (current == 0)
                    last0 = index;
                else
                    last1 = index;
                result[i] = (int)index;
            }
            if (p != safeEnd)
                throw new InvalidDataException("packed index buffer has extra data");
            return result;
        }
    }
}
