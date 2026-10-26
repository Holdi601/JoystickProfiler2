#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace JoyPro.CockpitPictures
{
    // A Star Citizen model: CryEngine "#ivo" chunk files (a .cga/.cgf with the nodes, the material name and the node
    // placement, its .cgam/.cgfm with the mesh). Chunks used: 0xC201973C the compiled bones (names, parents, the mesh
    // object of each node, local and object-space placement as quaternion + position), 0x70697FDA the node/mesh table
    // (the object-space 3x4 matrix of every mesh object), 0x83353333 the material file, 0xB8757777 the mesh (vertex
    // positions as 16-bit numbers in the mesh's box, texture coordinates as half floats, the triangles, the sub-meshes
    // with their material and mesh object). Positions here are in CryEngine's frame (x right, y forward, z up, metres).
    internal sealed class IvoModel
    {
        public const uint BonesChunk = 0xC201973C, NodeMeshChunk = 0x70697FDA, MaterialChunk = 0x83353333, MeshChunk = 0xB8757777;
        public const uint IndexStream = 0xEECDC168, VertexStream = 0xB3A70D5E, VertexFloatStream = 0x91329AE9;

        public sealed class Node
        {
            public string Name = string.Empty;
            public int Parent = -1;
            public int ObjectIndex = -1;
            public double[] World = Mat4.Identity();
        }

        public sealed class Subset
        {
            public int Material;
            public int Object;
            public int FirstIndex;
            public int IndexCount;
            public int FirstVertex;
            public int VertexCount;
        }

        public List<Node> Nodes = new List<Node>();
        // Object-space placement of each mesh object (index = the sub-meshes' object).
        public List<double[]> Objects = new List<double[]>();
        public string Material = string.Empty;
        public float[] Positions;
        public float[] Uvs;
        public int[] Indices;
        public List<Subset> Subsets = new List<Subset>();
        public string Problem = string.Empty;

        public int VertexCount { get { return Positions == null ? 0 : Positions.Length / 3; } }

        private sealed class Chunk
        {
            public uint Type;
            public uint Version;
            public int Offset;
            public int End;
        }

        private static List<Chunk> Chunks(byte[] d)
        {
            List<Chunk> list = new List<Chunk>();
            if (d == null || d.Length < 16 || d[0] != '#' || d[1] != 'i' || d[2] != 'v' || d[3] != 'o')
                return list;
            int n = BitConverter.ToInt32(d, 8), table = BitConverter.ToInt32(d, 12);
            if (n < 0 || n > 10000 || table < 0 || table + 16L * n > d.Length)
                return list;
            for (int i = 0; i < n; i++)
            {
                int p = table + 16 * i;
                long off = BitConverter.ToInt64(d, p + 8);
                if (off < 0 || off > d.Length)
                    continue;
                list.Add(new Chunk { Type = BitConverter.ToUInt32(d, p), Version = BitConverter.ToUInt32(d, p + 4), Offset = (int)off });
            }
            list = list.OrderBy(c => c.Offset).ToList();
            for (int i = 0; i < list.Count; i++)
                list[i].End = i + 1 < list.Count ? list[i + 1].Offset : d.Length;
            return list;
        }

        public static bool IsIvo(byte[] d)
        {
            return d != null && d.Length >= 16 && d[0] == '#' && d[1] == 'i' && d[2] == 'v' && d[3] == 'o';
        }

        // The model of a .cga/.cgf (nodes, material) and its mesh file (.cgam/.cgfm; null: the mesh is in the first).
        public static IvoModel Read(byte[] model, byte[] mesh)
        {
            IvoModel m = new IvoModel();
            List<Chunk> mc = Chunks(model);
            if (mc.Count == 0)
            {
                m.Problem = "not an #ivo model";
                return m;
            }
            foreach (Chunk c in mc)
            {
                try
                {
                    if (c.Type == BonesChunk)
                        m.ReadBones(model, c);
                    else if (c.Type == NodeMeshChunk)
                        m.ReadNodeMeshes(model, c);
                    else if (c.Type == MaterialChunk)
                        m.Material = CString(model, c.Offset, c.End);
                }
                catch (Exception ex)
                {
                    m.Problem = "chunk " + c.Type.ToString("x8", CultureInfo.InvariantCulture) + ": " + ex.Message;
                }
            }
            Chunk meshChunk = null;
            byte[] md = null;
            foreach (byte[] f in new[] { mesh, model })
            {
                if (f == null)
                    continue;
                meshChunk = Chunks(f).FirstOrDefault(c => c.Type == MeshChunk);
                if (meshChunk != null)
                {
                    md = f;
                    break;
                }
            }
            if (meshChunk == null)
            {
                if (m.Problem.Length == 0)
                    m.Problem = "no mesh";
                return m;
            }
            m.ReadMesh(md, meshChunk);
            return m;
        }

        private static string CString(byte[] d, int at, int end)
        {
            int e = at;
            while (e < end && d[e] != 0)
                e++;
            return System.Text.Encoding.UTF8.GetString(d, at, e - at);
        }

        // Compiled bones: count, string table size, 40 bytes, 16 bytes per node (name hash, first child, child count,
        // parent, two more, mesh object), the names, the local placements, the object-space placements (quaternion
        // x, y, z, w and position; 28 bytes each).
        private void ReadBones(byte[] d, Chunk c)
        {
            int n = BitConverter.ToInt32(d, c.Offset), slen = BitConverter.ToInt32(d, c.Offset + 4);
            int entries = c.Offset + 48;
            int strings = entries + 16 * n;
            int world = strings + slen + 28 * n;
            if (n <= 0 || n > 100000 || slen < 0 || world + 28L * n > c.End)
                throw new InvalidDataException("bad bone table");
            List<string> names = new List<string>();
            int s = strings;
            for (int i = 0; i < n; i++)
            {
                int e = s;
                while (e < strings + slen && d[e] != 0)
                    e++;
                names.Add(System.Text.Encoding.UTF8.GetString(d, s, e - s));
                s = Math.Min(strings + slen, e + 1);
            }
            for (int i = 0; i < n; i++)
            {
                int p = entries + 16 * i;
                int parent = BitConverter.ToUInt16(d, p + 8);
                int obj = BitConverter.ToUInt16(d, p + 14);
                int w = world + 28 * i;
                double qx = BitConverter.ToSingle(d, w), qy = BitConverter.ToSingle(d, w + 4), qz = BitConverter.ToSingle(d, w + 8), qw = BitConverter.ToSingle(d, w + 12);
                double[] m = Mat4.Multiply(Mat4.Translation(BitConverter.ToSingle(d, w + 16), BitConverter.ToSingle(d, w + 20), BitConverter.ToSingle(d, w + 24)), Mat4.Rotation(new[] { qw, qx, qy, qz }));
                Nodes.Add(new Node { Name = names[i], Parent = parent == 0xFFFF || parent >= n ? -1 : parent, ObjectIndex = obj == 0xFFFF ? -1 : obj, World = m });
            }
        }

        // The node/mesh table: header (8 numbers: 32, node count, mesh count, .., sub-mesh count, ..), 32 bytes, then 208
        // bytes per object: its object-space 3x4 matrix (rows), its parent-relative matrix, ...
        private void ReadNodeMeshes(byte[] d, Chunk c)
        {
            int n = BitConverter.ToInt32(d, c.Offset + 4);
            int first = c.Offset + 64;
            if (n <= 0 || n > 100000 || first + 208L * n > c.End)
                throw new InvalidDataException("bad node table");
            for (int i = 0; i < n; i++)
            {
                int p = first + 208 * i;
                double[] r = new double[12];
                for (int k = 0; k < 12; k++)
                    r[k] = BitConverter.ToSingle(d, p + 4 * k);
                double[] m = Mat4.Identity();
                m[0] = r[0]; m[4] = r[1]; m[8] = r[2]; m[12] = r[3];
                m[1] = r[4]; m[5] = r[5]; m[9] = r[6]; m[13] = r[7];
                m[2] = r[8]; m[6] = r[9]; m[10] = r[10]; m[14] = r[11];
                if (r.Any(x => double.IsNaN(x) || double.IsInfinity(x) || Math.Abs(x) > 1e5))
                    m = Mat4.Identity();
                Objects.Add(m);
            }
        }

        // The mesh: flags, then vertex count, index count, sub-mesh count; the box of the placed mesh and the box the
        // 16-bit positions span; the sub-meshes (48 bytes: material, object, first index, index count, first vertex, ..,
        // vertex count, ..) from byte 168; then the streams (tag, bytes per element, data, padded to 8 bytes).
        private void ReadMesh(byte[] d, Chunk c)
        {
            int o = c.Offset;
            int nv = BitConverter.ToInt32(d, o + 8), ni = BitConverter.ToInt32(d, o + 12), ns = BitConverter.ToInt32(d, o + 16);
            if (nv <= 0 || ni <= 0 || ns <= 0 || nv > 20000000 || ni > 60000000 || ns > 100000 || o + 168 + 48L * ns > c.End)
            {
                Problem = "bad mesh header";
                return;
            }
            double[] box = new double[6];
            for (int k = 0; k < 6; k++)
                box[k] = BitConverter.ToSingle(d, o + 48 + 4 * k);
            for (int i = 0; i < ns; i++)
            {
                int p = o + 168 + 48 * i;
                Subset s = new Subset
                {
                    Material = BitConverter.ToUInt16(d, p),
                    Object = BitConverter.ToUInt16(d, p + 2),
                    FirstIndex = BitConverter.ToInt32(d, p + 4),
                    IndexCount = BitConverter.ToInt32(d, p + 8),
                    FirstVertex = BitConverter.ToInt32(d, p + 12),
                    VertexCount = BitConverter.ToInt32(d, p + 20)
                };
                if (s.FirstIndex < 0 || s.IndexCount < 0 || (long)s.FirstIndex + s.IndexCount > ni)
                {
                    Problem = "bad sub-mesh";
                    return;
                }
                Subsets.Add(s);
            }
            int at = o + 168 + 48 * ns;
            while (at + 8 <= c.End)
            {
                uint tag = BitConverter.ToUInt32(d, at);
                int bpe = BitConverter.ToInt32(d, at + 4);
                int count = tag == IndexStream ? ni : nv;
                if (bpe <= 0 || bpe > 64 || at + 8 + (long)bpe * count > c.End)
                    break;
                int data = at + 8;
                if (tag == IndexStream)
                {
                    Indices = new int[ni];
                    for (int i = 0; i < ni; i++)
                        Indices[i] = bpe == 2 ? BitConverter.ToUInt16(d, data + 2 * i) : BitConverter.ToInt32(d, data + 4 * i);
                }
                else if (tag == VertexStream && bpe == 16)
                {
                    ReadPackedVertices(d, data, nv, box);
                }
                else if (tag == VertexFloatStream && bpe >= 20)
                {
                    Positions = new float[3 * nv];
                    Uvs = new float[2 * nv];
                    for (int i = 0; i < nv; i++)
                    {
                        int p = data + bpe * i;
                        Positions[3 * i] = BitConverter.ToSingle(d, p);
                        Positions[3 * i + 1] = BitConverter.ToSingle(d, p + 4);
                        Positions[3 * i + 2] = BitConverter.ToSingle(d, p + 8);
                        Uvs[2 * i] = Half(BitConverter.ToUInt16(d, p + bpe - 4));
                        Uvs[2 * i + 1] = Half(BitConverter.ToUInt16(d, p + bpe - 2));
                    }
                }
                at = data + bpe * count;
                at = (at + 7) & ~7;
            }
            if (Indices == null || Positions == null)
                Problem = "mesh streams not understood";
            else if (!Rebase(nv))
                Problem = "bad triangles";
        }

        // 16-bit triangles of a large mesh count from a base vertex (it moves on when the next vertices no longer fit
        // in 16 bits): makes every sub-mesh's triangles absolute. A sub-mesh's base is the previous one when its
        // triangles fit its vertex range with it, else its first vertex minus its smallest index.
        private bool Rebase(int nv)
        {
            long prevBase = 0;
            foreach (Subset s in Subsets)
            {
                if (s.IndexCount == 0)
                    continue;
                int min = int.MaxValue, max = int.MinValue;
                for (int i = s.FirstIndex; i < s.FirstIndex + s.IndexCount; i++)
                {
                    min = Math.Min(min, Indices[i]);
                    max = Math.Max(max, Indices[i]);
                }
                long lo = s.FirstVertex, hi = (long)s.FirstVertex + s.VertexCount;
                long b;
                if (min >= lo && max < hi)
                    b = 0;
                else if (min + prevBase >= lo && max + prevBase < hi)
                    b = prevBase;
                else
                    b = lo - min;
                if (min + b < 0 || max + b >= nv || max + b >= hi)
                    return false;
                if (b != 0)
                {
                    for (int i = s.FirstIndex; i < s.FirstIndex + s.IndexCount; i++)
                        Indices[i] += (int)b;
                    prevBase = b;
                }
            }
            return true;
        }

        // 16-bit positions in the box: value / 32767 * half size (at least 1) + centre, per axis.
        private void ReadPackedVertices(byte[] d, int data, int nv, double[] box)
        {
            Positions = new float[3 * nv];
            Uvs = new float[2 * nv];
            double[] centre = new double[3], scale = new double[3];
            for (int k = 0; k < 3; k++)
            {
                double half = (box[3 + k] - box[k]) / 2;
                centre[k] = (box[3 + k] + box[k]) / 2;
                scale[k] = half > 1 ? half : 1;
            }
            for (int i = 0; i < nv; i++)
            {
                int p = data + 16 * i;
                for (int k = 0; k < 3; k++)
                    Positions[3 * i + k] = (float)(BitConverter.ToInt16(d, p + 2 * k) / 32767.0 * scale[k] + centre[k]);
                Uvs[2 * i] = Half(BitConverter.ToUInt16(d, p + 12));
                Uvs[2 * i + 1] = Half(BitConverter.ToUInt16(d, p + 14));
            }
        }

        public static float Half(ushort h)
        {
            int sign = (h >> 15) & 1, exp = (h >> 10) & 31, man = h & 1023;
            float v;
            if (exp == 0)
                v = (float)(man / 1024.0 * Math.Pow(2, -14));
            else if (exp == 31)
                v = man == 0 ? float.PositiveInfinity : float.NaN;
            else
                v = (float)((1 + man / 1024.0) * Math.Pow(2, exp - 15));
            return sign == 1 ? -v : v;
        }

        // The object-space placement of a sub-mesh's mesh object.
        public double[] ObjectMatrix(int obj)
        {
            if (obj >= 0 && obj < Objects.Count)
                return Objects[obj];
            Node n = Nodes.FirstOrDefault(x => x.ObjectIndex == obj);
            return n != null ? n.World : Mat4.Identity();
        }

        public Node Find(string name)
        {
            return Nodes.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // The node of a mesh object (for the part names of the sub-meshes).
        public Node NodeOfObject(int obj)
        {
            return Nodes.FirstOrDefault(n => n.ObjectIndex == obj);
        }

        public bool IsUnder(Node n, Node ancestor)
        {
            int guard = 0;
            int i = Nodes.IndexOf(n), a = Nodes.IndexOf(ancestor);
            while (i >= 0 && guard++ < 512)
            {
                if (i == a)
                    return true;
                i = Nodes[i].Parent;
            }
            return false;
        }
    }

    // A material of a .mtl file (a sub-material): how it is drawn in the pictures.
    internal sealed class StarCitizenMaterial
    {
        public string Name = string.Empty;
        public string Shader = string.Empty;
        // draw, skip (no-draw, glass, decals, holograms), screen (a display)
        public string Kind = "draw";
        public double[] Diffuse = { 0.5, 0.5, 0.5 };   // linear
        public double[] Specular = { 0.04, 0.04, 0.04 };   // linear (a metal: its colour, the diffuse is black)
        public string Texture = string.Empty;          // the diffuse texture (TexSlot1)
        public string LayerPath = string.Empty;        // HardSurface: the first layer's material
        public double[] LayerTint;                     // linear
        public double LayerUvTiling = 1;               // HardSurface: the first layer's UVTiling
        public double Tiling = 1;                      // a layer material: its LayerTiling
        public bool AlphaTest;

        // A metal (CryEngine's PBR: a black diffuse and a bright specular colour).
        public bool Metal
        {
            get { return 0.299 * Specular[0] + 0.587 * Specular[1] + 0.114 * Specular[2] >= 0.12; }
        }

        public static List<StarCitizenMaterial> Parse(CryXmlNode root)
        {
            List<StarCitizenMaterial> list = new List<StarCitizenMaterial>();
            if (root == null)
                return list;
            CryXmlNode subs = root.Child("SubMaterials");
            IEnumerable<CryXmlNode> mats = subs != null ? subs.Children.Where(c => string.Equals(c.Tag, "Material", StringComparison.OrdinalIgnoreCase)) : new[] { root };
            foreach (CryXmlNode m in mats)
                list.Add(From(m));
            return list;
        }

        private static readonly string[] SkipShaders = { "nodraw", "glass", "meshdecal", "decal", "hologram", "holocig", "uiplane", "fx", "particle", "water", "sky", "displacementdecal" };

        public static StarCitizenMaterial From(CryXmlNode m)
        {
            StarCitizenMaterial s = new StarCitizenMaterial { Name = m.Get("Name"), Shader = m.Get("Shader") };
            string shader = s.Shader.ToLowerInvariant();
            string mask = m.Get("StringGenMask").ToUpperInvariant();
            string name = s.Name.ToLowerInvariant();
            double[] diff = CryXml.Numbers(m.Get("Diffuse"));
            if (diff.Length >= 3)
                s.Diffuse = new[] { diff[0], diff[1], diff[2] };
            double[] spec = CryXml.Numbers(m.Get("Specular"));
            if (spec.Length >= 3)
                s.Specular = new[] { spec[0], spec[1], spec[2] };
            double tiling;
            CryXmlNode pub = m.Child("PublicParams");
            if (pub != null && double.TryParse(pub.Get("LayerTiling"), NumberStyles.Float, CultureInfo.InvariantCulture, out tiling) && tiling > 0.01 && tiling < 100)
                s.Tiling = tiling;
            double opacity;
            if (!double.TryParse(m.Get("Opacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out opacity))
                opacity = 1;
            double alphaTest;
            s.AlphaTest = double.TryParse(m.Get("AlphaTest"), NumberStyles.Float, CultureInfo.InvariantCulture, out alphaTest) && alphaTest > 0.05;
            CryXmlNode textures = m.Child("Textures");
            if (textures != null)
            {
                foreach (CryXmlNode t in textures.Children)
                {
                    string map = t.Get("Map");
                    string file = t.Get("File");
                    if ((string.Equals(map, "TexSlot1", StringComparison.OrdinalIgnoreCase) || string.Equals(map, "Diffuse", StringComparison.OrdinalIgnoreCase)) && file.Length > 0 && !file.StartsWith("$", StringComparison.Ordinal))
                        s.Texture = file;
                }
            }
            CryXmlNode layers = m.Child("MatLayers");
            if (layers != null)
            {
                CryXmlNode first = layers.Children.FirstOrDefault(c => c.Get("Path").Length > 0);
                if (first != null)
                {
                    s.LayerPath = first.Get("Path");
                    double[] tint = CryXml.Numbers(first.Get("TintColor"));
                    if (tint.Length >= 3)
                        s.LayerTint = new[] { tint[0], tint[1], tint[2] };
                    double uvTiling;
                    if (double.TryParse(first.Get("UVTiling"), NumberStyles.Float, CultureInfo.InvariantCulture, out uvTiling) && uvTiling > 0.01 && uvTiling < 100)
                        s.LayerUvTiling = uvTiling;
                }
            }
            bool screen = shader == "displayscreen" || shader.Contains("monitor") || (mask.Contains("MONITOREFFECTS") && shader != "uiplane");
            if (screen)
                s.Kind = "screen";
            else if (SkipShaders.Contains(shader) || mask.Contains("%DECAL") || name.Contains("glass") || name.Contains("decal") || name.Contains("proxy") || name.Contains("shadow") || (opacity < 0.5 && !s.AlphaTest))
                s.Kind = "skip";
            return s;
        }

        public static byte Srgb(double linear)
        {
            return (byte)Math.Round(Math.Pow(Math.Max(0, Math.Min(1, linear)), 1 / 2.2) * 255);
        }

        public static double Linear(byte srgb)
        {
            return Math.Pow(srgb / 255.0, 2.2);
        }
    }

    // Star Citizen's textures: DDS split in mip files (x.dds: the header and the smallest levels, x.dds.1 .. x.dds.N:
    // the larger levels, N the largest). Only the levels up to the wanted size are read and put back into one DDS.
    internal static class StarCitizenTextures
    {
        public static string DdsName(string texture)
        {
            string t = (texture ?? string.Empty).Replace('/', '\\');
            int dot = t.LastIndexOf('.');
            int slash = t.LastIndexOf('\\');
            if (dot > slash)
                t = t.Substring(0, dot);
            return StarCitizenP4k.Normalize(t + ".dds");
        }

        public static TextureImage Load(StarCitizenP4k p4k, string texture, int maxSize)
        {
            string name = DdsName(texture);
            byte[] head = p4k.Read(name);
            if (head == null)
                return null;
            byte[] dds = Assemble(head, k => p4k.Read(name + "." + k.ToString(CultureInfo.InvariantCulture)), k => p4k.Exists(name + "." + k.ToString(CultureInfo.InvariantCulture)), maxSize);
            return dds == null ? null : TextureImage.Decode(dds, name, maxSize);
        }

        // One DDS from the head file and the mip files, starting at the largest level not above maxSize.
        public static byte[] Assemble(byte[] head, Func<int, byte[]> mip, Func<int, bool> exists, int maxSize)
        {
            if (head == null || head.Length < 128 || head[0] != 'D' || head[1] != 'D' || head[2] != 'S')
                return null;
            int splits = 0;
            while (splits < 16 && exists(splits + 1))
                splits++;
            if (splits == 0)
                return head;
            int headerSize = 128 + (System.Text.Encoding.ASCII.GetString(head, 84, 4) == "DX10" ? 20 : 0);
            int height = BitConverter.ToInt32(head, 12), width = BitConverter.ToInt32(head, 16), mips = Math.Max(1, BitConverter.ToInt32(head, 28));
            // Level L sits in split file (splits - L) for L < splits, else in the head file.
            int level = 0;
            while (level + 1 < Math.Min(mips, splits) && (Math.Max(1, width >> level) > maxSize || Math.Max(1, height >> level) > maxSize))
                level++;
            using (MemoryStream o = new MemoryStream())
            {
                byte[] h = (byte[])head.Clone();
                Array.Copy(BitConverter.GetBytes(Math.Max(1, height >> level)), 0, h, 12, 4);
                Array.Copy(BitConverter.GetBytes(Math.Max(1, width >> level)), 0, h, 16, 4);
                Array.Copy(BitConverter.GetBytes(Math.Max(1, mips - level)), 0, h, 28, 4);
                // The pitch/linear size field no longer fits: clear it.
                Array.Copy(BitConverter.GetBytes(0), 0, h, 20, 4);
                o.Write(h, 0, headerSize);
                for (int l = level; l < splits; l++)
                {
                    byte[] part = mip(splits - l);
                    if (part == null)
                        return null;
                    o.Write(part, 0, part.Length);
                }
                o.Write(head, headerSize, head.Length - headerSize);
                return o.ToArray();
            }
        }
    }
}
