#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace JoyPro.CockpitPictures
{
    // A War Thunder cockpit DynModel (GRP2 class 0xB4B7D9C4), read as DagorEngine loads it
    // (prog/engine/shaders/dynSceneRes.cpp DynamicRenderableSceneLodsResource::loadResource, matVdataLoad.cpp,
    // shaderMesh.cpp, dag_dynSceneRes.h; BSD-3): the material/texture counts, the packed vertex data block
    // (ShaderMatVdata: vertex declarations, vertex buffers, meshoptimizer-packed index buffers), the LOD dump (bounds
    // and the position unpack constants), the node name map and the rigid meshes of LOD 0 with their elements.
    // The vertex data block is Oodle, zstd or zlib packed; the caller unpacks it (Unpack).
    internal sealed class WarThunderDynModel
    {
        public sealed class Channel
        {
            public int Type;    // VSDT_ >> 16
            public int Usage;   // 0 pos, 1 normal, 2 colour, 3 texcoord
            public int Index;
        }

        public sealed class VertexData
        {
            public int Count;
            public int Stride;
            public int Flags;
            public List<Channel> Channels = new List<Channel>();
            public byte[] Vertices;
            public int[] Indices;
        }

        public sealed class Element
        {
            public int Material;
            public int VertexData;
            public int StartIndex;
            public int Faces;
            public int BaseVertex;
            public bool Transparent;
        }

        public sealed class Rigid
        {
            public int Node;
            public float[] Sphere = new float[4];
            public List<Element> Elements = new List<Element>();
        }

        public const int PackedIb = 0x200;
        public const int Index32 = 0x40;

        public bool SeparateMaterials;
        public int TextureCount, MaterialCount;
        public List<string> TextureNames = new List<string>();
        public List<VertexData> Vdata = new List<VertexData>();
        public float[] PosOffset = { 0, 0, 0 };
        public float[] PosScale = { 1, 1, 1 };
        public bool BoundPack;
        public Dictionary<int, string> NodeNames = new Dictionary<int, string>();
        public List<Rigid> Rigids = new List<Rigid>();
        public int Skins;

        // compression: 2 = Oodle (raw size known), 1 = zstd, 0 = none; returns the raw bytes.
        public delegate byte[] Unpacker(int compression, byte[] data, int offset, int length, int rawSize);

        public static WarThunderDynModel Read(byte[] r, Unpacker unpack)
        {
            WarThunderDynModel m = new WarThunderDynModel();
            int p = 0;
            int resSize = BitConverter.ToInt32(r, p);
            int t0 = BitConverter.ToInt32(r, p + 4), t1 = BitConverter.ToInt32(r, p + 8);
            int vdataCount = BitConverter.ToInt32(r, p + 12);
            uint hdrWord = BitConverter.ToUInt32(r, p + 16);
            p += 20;
            if (resSize <= 0 || resSize > 1 << 20 || vdataCount < 0 || vdataCount > 64)
                throw new InvalidDataException("not a DynModel resource");
            m.SeparateMaterials = t0 == -1 && t1 == -1;
            if (!m.SeparateMaterials)
            {
                m.TextureCount = t0;
                m.MaterialCount = t1;
                // texture names: a size, then a patchable table of name offsets and the names
                int sz = BitConverter.ToInt32(r, p);
                p += 4;
                int no = BitConverter.ToInt32(r, p), nc = BitConverter.ToInt32(r, p + 4);
                for (int i = 0; i < nc && no + 8 * i + 4 <= sz; i++)
                {
                    int o = BitConverter.ToInt32(r, p + no + 8 * i);
                    m.TextureNames.Add(o >= 0 && o < sz ? WarThunderGrp.CString(r, p + o) : string.Empty);
                }
                p += sz;
            }
            // ShaderMatVdata
            int hdrSize;
            byte[] vd;
            int vdStart;
            if ((hdrWord & 0xE0000000) == 0xC0000000)
            {
                hdrSize = (int)(hdrWord & ~0xC0000000u);
                uint block = BitConverter.ToUInt32(r, p);
                int blockSize = (int)(block & 0x3FFFFFFF);
                int tag = (int)(block >> 30);
                p += 4;
                if (tag == 2)
                {
                    int raw = BitConverter.ToInt32(r, p);
                    vd = unpack(2, r, p + 4, blockSize - 4, raw);
                }
                else if (tag == 1)
                {
                    vd = unpack(1, r, p, blockSize, 0);
                }
                else
                {
                    throw new InvalidDataException("unknown vertex data packing " + tag);
                }
                vdStart = 0;
                p += blockSize;
            }
            else if ((int)hdrWord < 0)
            {
                // zlib to the end of the vertex data (seldom; the zlib stream ends itself)
                hdrSize = -(int)hdrWord;
                throw new InvalidDataException("zlib-packed vertex data is not supported");
            }
            else if ((hdrWord & 0x40000000) != 0)
            {
                throw new InvalidDataException("LZMA-packed vertex data is not supported");
            }
            else
            {
                hdrSize = (int)hdrWord;
                vd = r;
                vdStart = p;
            }
            int consumed = ReadVdata(m, vd, vdStart, hdrSize, vdataCount);
            if (vd == r)
                p = vdStart + consumed;
            // The LOD dump: lods table, bbox, the position unpack constants (ofs, mul), the lods.
            int lods = p;
            int lodsOfs = BitConverter.ToInt32(r, lods), lodCount = BitConverter.ToInt32(r, lods + 4);
            float[] bb = new float[6];
            for (int i = 0; i < 6; i++)
                bb[i] = BitConverter.ToSingle(r, lods + 16 + 4 * i);
            if (bb[0] > bb[3] || bb[1] > bb[4] || bb[2] > bb[5])
            {
                m.BoundPack = true;
                for (int i = 0; i < 3; i++)
                {
                    m.PosOffset[i] = BitConverter.ToSingle(r, lods + 40 + 4 * i);
                    m.PosScale[i] = BitConverter.ToSingle(r, lods + 56 + 4 * i);
                }
            }
            if (lodCount < 1 || lodsOfs + 16 * lodCount > resSize)
                throw new InvalidDataException("DynModel without LODs");
            int scene0Size = BitConverter.ToInt32(r, lods + lodsOfs);
            p += resSize;
            // Node names: a sorted name map with the node ids.
            int namesSize = BitConverter.ToInt32(r, p);
            p += 4;
            int nm = p;
            int mapOfs = BitConverter.ToInt32(r, nm), mapCount = BitConverter.ToInt32(r, nm + 4);
            int idOfs = BitConverter.ToInt32(r, nm + 16), idCount = BitConverter.ToInt32(r, nm + 20);
            for (int i = 0; i < mapCount && i < idCount; i++)
            {
                int o = BitConverter.ToInt32(r, nm + mapOfs + 8 * i);
                int id = BitConverter.ToUInt16(r, nm + idOfs + 2 * i);
                if (o >= 0 && o < namesSize)
                    m.NodeNames[id] = WarThunderGrp.CString(r, nm + o);
            }
            p += namesSize;
            // LOD 0: rigids (mesh resource size, bounding sphere, node), skins; then each rigid's ShaderMesh.
            int sc = p;
            int rigOfs = BitConverter.ToInt32(r, sc), rigCount = BitConverter.ToInt32(r, sc + 4);
            m.Skins = BitConverter.ToInt32(r, sc + 20);
            List<int> meshSizes = new List<int>();
            for (int i = 0; i < rigCount; i++)
            {
                int at = sc + rigOfs + 32 * i;
                Rigid rg = new Rigid { Node = BitConverter.ToInt32(r, at + 24) };
                for (int k = 0; k < 4; k++)
                    rg.Sphere[k] = BitConverter.ToSingle(r, at + 8 + 4 * k);
                meshSizes.Add((int)BitConverter.ToInt64(r, at));
                m.Rigids.Add(rg);
            }
            p += scene0Size;
            for (int i = 0; i < rigCount; i++)
            {
                ReadMesh(r, p, m.Rigids[i]);
                p += meshSizes[i];
            }
            return m;
        }

        // The ShaderMesh dump: elems (and in the older format the transparent elems as a second table), the stage
        // ends, the format word (bit 31 = modern: one table, stage ends say where the transparent stage starts).
        private static void ReadMesh(byte[] r, int at, Rigid rg)
        {
            int e0 = BitConverter.ToInt32(r, at), c0 = BitConverter.ToInt32(r, at + 4);
            uint fmt = BitConverter.ToUInt32(r, at + 32);
            bool modern = (fmt & 0x80000000) != 0;
            int transFrom = int.MaxValue;
            if (modern)
            {
                // stageEndElemIdx[i] = end of stage i: STG_opaque, STG_atest, STG_imm_decal, STG_decal, STG_trans ...
                transFrom = BitConverter.ToUInt16(r, at + 16 + 2 * 3);
                AddElements(r, at, e0, c0, rg, transFrom);
            }
            else
            {
                AddElements(r, at, e0, c0, rg, int.MaxValue);
                int e1 = BitConverter.ToInt32(r, at + 16), c1 = BitConverter.ToInt32(r, at + 20);
                AddElements(r, at, e1, c1, rg, -1);
            }
        }

        private static void AddElements(byte[] r, int at, int ofs, int count, Rigid rg, int transFrom)
        {
            for (int k = 0; k < count; k++)
            {
                int e = at + ofs + 48 * k;
                rg.Elements.Add(new Element
                {
                    Material = (int)BitConverter.ToInt64(r, e + 8),
                    VertexData = (int)BitConverter.ToInt64(r, e + 16),
                    StartIndex = BitConverter.ToInt32(r, e + 36),
                    Faces = BitConverter.ToInt32(r, e + 40),
                    BaseVertex = BitConverter.ToInt32(r, e + 44),
                    Transparent = k >= transFrom
                });
            }
        }

        // MatVdataHdr: mat table, vdata table, then per vdata: vertex count, stride:8 | packed index size low:24,
        // index size:28 | packed high:4, flags, the vertex declaration table (12 bytes per channel); then the vertex and
        // index buffers of each vdata in turn. Returns the bytes used.
        private static int ReadVdata(WarThunderDynModel m, byte[] d, int start, int hdrSize, int count)
        {
            int h = start;
            int vdOfs = BitConverter.ToInt32(d, h + 16), vdCount = BitConverter.ToInt32(d, h + 20);
            if (vdCount != count)
                count = Math.Min(count, vdCount);
            int p = start + hdrSize;
            for (int i = 0; i < count; i++)
            {
                int v = h + vdOfs + 32 * i;
                uint a = BitConverter.ToUInt32(d, v + 4), b = BitConverter.ToUInt32(d, v + 8);
                VertexData x = new VertexData
                {
                    Count = BitConverter.ToInt32(d, v),
                    Stride = (int)(a & 0xFF),
                    Flags = BitConverter.ToInt32(d, v + 12)
                };
                int idxSize = (int)(b & 0x0FFFFFFF);
                int packed = (x.Flags & PackedIb) != 0 ? (int)((a >> 8) | ((b >> 28) << 24)) : idxSize;
                int declOfs = BitConverter.ToInt32(d, v + 16), declCount = BitConverter.ToInt32(d, v + 20);
                for (int k = 0; k < declCount; k++)
                {
                    int c = h + declOfs + 12 * k;
                    x.Channels.Add(new Channel { Type = BitConverter.ToInt32(d, c) >> 16, Usage = BitConverter.ToInt32(d, c + 4), Index = BitConverter.ToInt16(d, c + 8) });
                }
                bool i32 = (x.Flags & Index32) != 0 || (x.Count > 65536 && (x.Flags & 0x20) == 0);
                int vbSize = x.Count * x.Stride;
                x.Vertices = new byte[vbSize];
                Buffer.BlockCopy(d, p, x.Vertices, 0, vbSize);
                p += vbSize;
                int indexCount = idxSize / (i32 ? 4 : 2);
                if ((x.Flags & PackedIb) != 0)
                {
                    x.Indices = MeshoptIndexSequence.Decode(d, p, packed, indexCount);
                    if (!i32)
                    {
                        for (int k = 0; k < x.Indices.Length; k++)
                            x.Indices[k] &= 0xFFFF;
                    }
                }
                else
                {
                    x.Indices = new int[indexCount];
                    for (int k = 0; k < indexCount; k++)
                        x.Indices[k] = i32 ? BitConverter.ToInt32(d, p + 4 * k) : BitConverter.ToUInt16(d, p + 2 * k);
                }
                p += packed;
                m.Vdata.Add(x);
            }
            return p - start;
        }

        public static int TypeSize(int t)
        {
            switch (t)
            {
                case 0: return 4;
                case 1: return 8;
                case 2: return 12;
                case 3: return 16;
                case 4: case 5: case 6: case 9: case 11: case 13: case 14: case 15: return 4;
                case 7: case 10: case 12: case 16: return 8;
                case 17: case 21: return 4;
                case 18: case 22: return 8;
                case 19: case 23: return 12;
                case 20: case 24: return 16;
                default: return 0;
            }
        }

        public static float Half(ushort h)
        {
            int s = (h >> 15) & 1, e = (h >> 10) & 0x1F, f = h & 0x3FF;
            float v;
            if (e == 0)
                v = (float)(f / 1024.0 * Math.Pow(2, -14));
            else if (e == 31)
                v = f == 0 ? float.PositiveInfinity : float.NaN;
            else
                v = (float)((1 + f / 1024.0) * Math.Pow(2, e - 15));
            return s == 1 ? -v : v;
        }

        // One channel of one vertex as up to four floats (normalised types to -1..1 / 0..1; SHORT2/SHORT4 texture
        // coordinates as DagorEngine packs them, 1/4096 units).
        public static void ReadChannel(byte[] vb, int at, int type, int usage, float[] o)
        {
            o[0] = o[1] = o[2] = 0;
            o[3] = 1;
            switch (type)
            {
                case 0: case 1: case 2: case 3:
                    for (int i = 0; i <= type; i++)
                        o[i] = BitConverter.ToSingle(vb, at + 4 * i);
                    break;
                case 4: // E3DCOLOR: bytes b, g, r, a
                    o[0] = vb[at + 2] / 255f;
                    o[1] = vb[at + 1] / 255f;
                    o[2] = vb[at] / 255f;
                    o[3] = vb[at + 3] / 255f;
                    break;
                case 5: // UBYTE4
                    for (int i = 0; i < 4; i++)
                        o[i] = vb[at + i] / 255f;
                    break;
                case 6: case 7: // SHORT2 / SHORT4
                {
                    int n = type == 6 ? 2 : 4;
                    float s = usage == 3 ? 1f / 4096f : 1f;
                    for (int i = 0; i < n; i++)
                        o[i] = BitConverter.ToInt16(vb, at + 2 * i) * s;
                    break;
                }
                case 9: case 10: // SHORT2N / SHORT4N
                {
                    int n = type == 9 ? 2 : 4;
                    for (int i = 0; i < n; i++)
                        o[i] = Math.Max(-1f, BitConverter.ToInt16(vb, at + 2 * i) / 32767f);
                    break;
                }
                case 11: case 12: // USHORT2N / USHORT4N
                {
                    int n = type == 11 ? 2 : 4;
                    for (int i = 0; i < n; i++)
                        o[i] = BitConverter.ToUInt16(vb, at + 2 * i) / 65535f;
                    break;
                }
                case 13: case 14: // UDEC3 / DEC3N
                {
                    uint v = BitConverter.ToUInt32(vb, at);
                    for (int i = 0; i < 3; i++)
                    {
                        int c = (int)((v >> (10 * i)) & 0x3FF);
                        o[i] = type == 13 ? c : (c >= 512 ? c - 1024 : c) / 511f;
                    }
                    break;
                }
                case 15: case 16: // HALF2 / HALF4
                {
                    int n = type == 15 ? 2 : 4;
                    for (int i = 0; i < n; i++)
                        o[i] = Half(BitConverter.ToUInt16(vb, at + 2 * i));
                    break;
                }
            }
        }
    }

    // A GeomNodeTree (the model's _skeleton resource, GRP2 class 0x56F81B6D; prog/engine/math/geomTree.cpp): nodes
    // with their local and world matrices (4x4, columns), children, parent and name. 160 bytes per node in the dump.
    internal sealed class WarThunderSkeleton
    {
        public List<string> Names = new List<string>();
        public List<int> Parents = new List<int>();
        public List<double[]> Local = new List<double[]>();   // column-major 4x4
        public List<double[]> World = new List<double[]>();
        public Dictionary<string, int> ByName = new Dictionary<string, int>(StringComparer.Ordinal);

        public static WarThunderSkeleton Read(byte[] r, WarThunderDynModel.Unpacker unpack)
        {
            uint ofs = BitConverter.ToUInt32(r, 0);
            uint cnt = BitConverter.ToUInt32(r, 4);
            bool compressed = (cnt & 0x80000000) != 0;
            int nodes = (int)(cnt & 0x7FFFFFFF);
            int size = (int)(ofs & 0xFFFFF);
            byte[] d;
            int start;
            if (compressed)
            {
                uint block = BitConverter.ToUInt32(r, 8);
                int blockSize = (int)(block & 0x3FFFFFFF);
                int tag = (int)(block >> 30);
                if (tag == 2)
                    d = unpack(2, r, 16, blockSize - 4, BitConverter.ToInt32(r, 12));
                else if (tag == 1)
                    d = unpack(1, r, 12, blockSize, 0);
                else
                    throw new InvalidDataException("unknown skeleton packing");
                start = 0;
            }
            else
            {
                d = r;
                start = 8;
            }
            if (nodes <= 0 || nodes * 160 > size || start + size > d.Length)
                throw new InvalidDataException("skeleton out of range");
            WarThunderSkeleton s = new WarThunderSkeleton();
            for (int i = 0; i < nodes; i++)
            {
                int o = start + 160 * i;
                double[] tm = new double[16];
                for (int k = 0; k < 16; k++)
                    tm[k] = BitConverter.ToSingle(d, o + 4 * k);
                tm[3] = tm[7] = tm[11] = 0;
                tm[15] = 1;
                long parent = BitConverter.ToInt64(d, o + 144);
                long name = BitConverter.ToInt64(d, o + 152);
                int pi = parent >= 0 && parent % 160 == 0 && parent / 160 < nodes && parent / 160 != i && i > 0 ? (int)(parent / 160) : -1;
                string nm = name >= 0 && name < size ? WarThunderGrp.CString(d, start + (int)name) : string.Empty;
                s.Names.Add(nm);
                s.Parents.Add(pi);
                s.Local.Add(tm);
                if (!s.ByName.ContainsKey(nm))
                    s.ByName[nm] = i;
            }
            for (int i = 0; i < nodes; i++)
            {
                int pi = s.Parents[i];
                s.World.Add(pi >= 0 && pi < i ? Mat4.Multiply(s.World[pi], s.Local[i]) : s.Local[i]);
            }
            return s;
        }

        public bool IsUnder(int node, int ancestor)
        {
            for (int n = node, guard = 0; n >= 0 && guard < 1000; n = Parents[n], guard++)
            {
                if (n == ancestor)
                    return true;
            }
            return false;
        }
    }
}
