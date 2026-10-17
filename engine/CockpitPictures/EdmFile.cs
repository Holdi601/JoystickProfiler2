#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace JoyPro.CockpitPictures
{
    // A reader for DCS World's binary model format (.edm, versions 8 and 10), enough to draw a cockpit: the node tree
    // (transforms, argument animations, visibility, LODs), the render nodes (vertices, indices, material), the
    // materials (textures, vertex layout) and the connectors (the named points the game's clickable cockpit and the VR
    // hands use). Static analysis of the file only; nothing is written.
    //
    // The layout follows the public descriptions of the format: the EDM specification of the Blender_ioEDM project
    // (Nicholas Devenish, MIT licence) and the DCS-EDM-Blender-Importer (MIT licence), checked against the files of
    // the modules installed here.
    internal sealed class EdmFile
    {
        public int Version;
        public List<string> Strings = new List<string>();
        public List<EdmNode> Nodes = new List<EdmNode>();
        public List<EdmRenderNode> RenderNodes = new List<EdmRenderNode>();
        public List<EdmConnector> Connectors = new List<EdmConnector>();
        public List<EdmMaterial> Materials = new List<EdmMaterial>();
        public List<string> Warnings = new List<string>();
        public double[] BoundsMin = new double[3];
        public double[] BoundsMax = new double[3];

        public static EdmFile Read(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            EdmFile file = new EdmFile();
            new Parser(data, file).Parse();
            return file;
        }

        public static EdmFile Read(byte[] data)
        {
            EdmFile file = new EdmFile();
            new Parser(data, file).Parse();
            return file;
        }

        private sealed class Parser
        {
            private readonly byte[] d;
            private readonly EdmFile f;
            private int p;
            private int version;
            private List<string> strings;

            public Parser(byte[] data, EdmFile file)
            {
                d = data;
                f = file;
            }

            public void Parse()
            {
                if (d.Length < 5 || d[0] != (byte)'E' || d[1] != (byte)'D' || d[2] != (byte)'M')
                    throw new InvalidDataException("not an EDM file");
                p = 3;
                version = U16();
                if (version != 8 && version != 10)
                    throw new InvalidDataException("EDM version " + version + " is not supported");
                f.Version = version;
                if (version == 10)
                {
                    int size = (int)U32();
                    strings = new List<string>();
                    int start = p, end = p + size;
                    for (int i = start; i <= end; i++)
                    {
                        if (i == end || d[i] == 0)
                        {
                            strings.Add(Encoding1251.GetString(d, start, i - start));
                            start = i + 1;
                        }
                    }
                    p = end;
                    f.Strings = strings;
                }
                IndexMap();
                IndexMap();
                string rootType = Str();
                if (rootType != "model::RootNode")
                    throw new InvalidDataException("EDM: expected the root node, found " + rootType);
                Root();
                uint count = U32();
                for (uint i = 0; i < count; i++)
                    f.Nodes.Add(Node());
                for (int i = 0; i < f.Nodes.Count; i++)
                    f.Nodes[i].Parent = I32();
                uint categories = U32();
                for (uint c = 0; c < categories; c++)
                {
                    string category = Str();
                    uint items = U32();
                    for (uint i = 0; i < items; i++)
                    {
                        int at = p;
                        try
                        {
                            object item = RenderItem(category);
                            EdmRenderNode rn = item as EdmRenderNode;
                            EdmConnector cn = item as EdmConnector;
                            if (rn != null)
                                f.RenderNodes.Add(rn);
                            else if (cn != null)
                                f.Connectors.Add(cn);
                        }
                        catch (Exception ex)
                        {
                            // An item whose layout is not known: find the next known item.
                            p = at;
                            string what = string.Empty;
                            try
                            {
                                what = Str() + " at " + at;
                            }
                            catch (Exception)
                            {
                                what = "at " + at;
                            }
                            p = at;
                            ex = new InvalidDataException(what + ": " + ex.Message);
                            if (!Resync(category))
                            {
                                f.Warnings.Add(category + ": stopped after " + i + " items: " + ex.Message);
                                return;
                            }
                            f.Warnings.Add(category + ": skipped an unreadable item (" + ex.Message + ")");
                        }
                    }
                }
            }

            // ---- primitives

            private int U16()
            {
                int v = d[p] | (d[p + 1] << 8);
                p += 2;
                return v;
            }

            private uint U32()
            {
                uint v = BitConverter.ToUInt32(d, p);
                p += 4;
                return v;
            }

            private int I32()
            {
                int v = BitConverter.ToInt32(d, p);
                p += 4;
                return v;
            }

            private byte U8()
            {
                return d[p++];
            }

            private float F32()
            {
                float v = BitConverter.ToSingle(d, p);
                p += 4;
                return v;
            }

            private double F64()
            {
                double v = BitConverter.ToDouble(d, p);
                p += 8;
                return v;
            }

            private double[] F64s(int n)
            {
                double[] v = new double[n];
                for (int i = 0; i < n; i++)
                    v[i] = F64();
                return v;
            }

            private float[] F32s(int n)
            {
                float[] v = new float[n];
                Buffer.BlockCopy(d, p, v, 0, n * 4);
                p += n * 4;
                return v;
            }

            // A length-prefixed string (node names in both versions).
            private string UStr()
            {
                uint len = U32();
                if (len > 100000 || p + len > d.Length)
                    throw new InvalidDataException("bad string length " + len);
                string s = Encoding1251.GetString(d, p, (int)len);
                p += (int)len;
                return s;
            }

            // A string: an index into the string table in version 10.
            private string Str()
            {
                if (version == 10)
                {
                    uint i = U32();
                    if (i >= strings.Count)
                        throw new InvalidDataException("bad string index " + i);
                    return strings[(int)i];
                }
                return UStr();
            }

            private void IndexMap()
            {
                uint n = U32();
                for (uint i = 0; i < n; i++)
                {
                    Str();
                    U32();
                }
            }

            // Quaternion stored x, y, z, w; returned as w, x, y, z.
            private double[] Quat()
            {
                double x = F64(), y = F64(), z = F64(), w = F64();
                return new[] { w, x, y, z };
            }

            // ---- properties

            private Dictionary<string, object> Properties()
            {
                Dictionary<string, object> props = new Dictionary<string, object>(StringComparer.Ordinal);
                uint n = U32();
                for (uint i = 0; i < n; i++)
                {
                    string type = Str();
                    string name;
                    object value = Property(type, out name);
                    props[name] = value;
                }
                return props;
            }

            private object Property(string type, out string name)
            {
                switch (type)
                {
                    case "model::Property<float>":
                        name = Str();
                        return F32();
                    case "model::Property<unsigned int>":
                        name = Str();
                        return U32();
                    case "model::Property<osg::Vec2f>":
                        name = Str();
                        return F32s(2);
                    case "model::Property<osg::Vec3f>":
                        name = Str();
                        return F32s(3);
                    case "model::Property<osg::Vec4f>":
                        name = Str();
                        return F32s(4);
                    case "model::Property<const char*>":
                        name = Str();
                        return Str();
                    case "model::ArgumentProperty":
                        name = Str();
                        return (int)U32();
                    case "model::AnimatedProperty<float>":
                        return Animated(1, out name);
                    case "model::AnimatedProperty<osg::Vec2f>":
                        return Animated(2, out name);
                    case "model::AnimatedProperty<osg::Vec3f>":
                        return Animated(3, out name);
                    default:
                        throw new InvalidDataException("unknown property type " + type);
                }
            }

            private object Animated(int floats, out string name)
            {
                name = Str();
                U32(); // argument
                uint keys = U32();
                for (uint k = 0; k < keys; k++)
                {
                    F64();
                    F32s(floats);
                }
                return null;
            }

            // ---- root and materials

            private void Root()
            {
                UStr();
                U32();
                Properties();
                if (version == 8)
                    U8();
                f.BoundsMin = F64s(3);
                f.BoundsMax = F64s(3);
                F64s(12);
                uint n = U32();
                for (uint i = 0; i < n; i++)
                    f.Materials.Add(Material());
                U32();
                U32();
            }

            private EdmMaterial Material()
            {
                EdmMaterial m = new EdmMaterial();
                uint n = U32();
                for (uint i = 0; i < n; i++)
                {
                    string key = Str();
                    switch (key)
                    {
                        case "BLENDING":
                            m.Blending = U8();
                            break;
                        case "CULLING":
                            m.Culling = U8();
                            break;
                        case "DEPTH_BIAS":
                            U32();
                            break;
                        case "VERTEX_FORMAT":
                            m.VertexFormat = VertexFormat();
                            break;
                        case "TEXTURE_COORDINATES_CHANNELS":
                            {
                                uint c = U32();
                                p += (int)c * 4;
                                break;
                            }
                        case "MATERIAL_NAME":
                            m.Shader = Str();
                            break;
                        case "NAME":
                            m.Name = Str();
                            break;
                        case "SHADOWS":
                            U8();
                            break;
                        case "TEXTURES":
                            {
                                uint c = U32();
                                for (uint t = 0; t < c; t++)
                                {
                                    int index = (int)U32();
                                    I32();
                                    string tex = Str();
                                    p += 16;   // four uints
                                    p += 64;   // a texture matrix (16 floats)
                                    m.Textures.Add(new EdmTexture { Index = index, Name = tex });
                                }
                                break;
                            }
                        case "UNIFORMS":
                            m.Uniforms = Properties();
                            break;
                        case "ANIMATED_UNIFORMS":
                            Properties();
                            break;
                        case "DECAL":
                        case "FLAT_COLOR_RENDERING":
                        case "HAS_ALPHA_CHANNEL":
                        case "DAMAGE_REQUIRED":
                        case "NIGHT_LIGHTING_ALPHA":
                        case "LIGHT_MAP":
                            {
                                byte b = U8();
                                if (key == "HAS_ALPHA_CHANNEL")
                                    m.HasAlpha = b != 0;
                                if (key == "DECAL")
                                    m.Decal = b;
                                break;
                            }
                        case "DAMAGE_TEXTURE_OFFSET":
                        case "Z_OFFSET":
                            F32();
                            break;
                        default:
                            throw new InvalidDataException("unknown material key " + key);
                    }
                }
                return m;
            }

            private byte[] VertexFormat()
            {
                uint n = U32();
                byte[] c = new byte[n];
                Array.Copy(d, p, c, 0, (int)n);
                p += (int)n;
                return c;
            }

            // ---- scene nodes

            private EdmNode Node()
            {
                string type = Str();
                EdmNode n = new EdmNode { Type = type };
                if (type == "model::RootNode")
                    throw new InvalidDataException("a second root node");
                n.Name = UStr();
                U32();
                n.Props = Properties();
                switch (type)
                {
                    case "model::Node":
                        break;
                    case "model::TransformNode":
                        n.Kind = EdmNodeKind.Transform;
                        n.Matrix = F64s(16);
                        break;
                    case "model::Bone":
                        n.Kind = EdmNodeKind.Transform;
                        n.Matrix = F64s(16);
                        F64s(16);
                        break;
                    case "model::LodNode":
                        {
                            n.Kind = EdmNodeKind.Lod;
                            uint c = U32();
                            n.Lods = new List<double[]>();
                            for (uint i = 0; i < c; i++)
                                n.Lods.Add(new[] { Math.Sqrt(Math.Max(0, F64())), Math.Sqrt(Math.Max(0, F64())) });
                            break;
                        }
                    case "model::BillboardNode":
                        p += 154;
                        break;
                    case "model::NumberNode":
                        U32();
                        break;
                    case "model::ArgAnimationNode":
                    case "model::ArgRotationNode":
                    case "model::ArgPositionNode":
                    case "model::ArgScaleNode":
                    case "model::ArgAnimatedBone":
                        n.Kind = EdmNodeKind.Animation;
                        n.Matrix = F64s(16);
                        n.Position = F64s(3);
                        n.Quat1 = Quat();
                        n.Quat2 = Quat();
                        n.Scale = F64s(3);
                        {
                            uint c = U32();
                            for (uint i = 0; i < c; i++)
                            {
                                EdmKeys k = new EdmKeys { Arg = (int)U32() };
                                uint keys = U32();
                                for (uint j = 0; j < keys; j++)
                                {
                                    k.Frames.Add(F64());
                                    k.Values.Add(F64s(3));
                                }
                                n.PositionKeys.Add(k);
                            }
                            c = U32();
                            for (uint i = 0; i < c; i++)
                            {
                                EdmKeys k = new EdmKeys { Arg = (int)U32() };
                                uint keys = U32();
                                for (uint j = 0; j < keys; j++)
                                {
                                    k.Frames.Add(F64());
                                    k.Values.Add(Quat());
                                }
                                n.RotationKeys.Add(k);
                            }
                            c = U32();
                            for (uint i = 0; i < c; i++)
                            {
                                EdmKeys k = new EdmKeys { Arg = (int)U32() };
                                uint a = U32();
                                for (uint j = 0; j < a; j++)
                                {
                                    k.Frames.Add(F64());
                                    k.Values.Add(F64s(4));
                                }
                                uint b = U32();
                                p += (int)b * 32;
                                n.ScaleKeys.Add(k);
                            }
                        }
                        if (type == "model::ArgAnimatedBone")
                            F64s(16);
                        break;
                    case "model::ArgVisibilityNode":
                        {
                            n.Kind = EdmNodeKind.Visibility;
                            uint c = U32();
                            for (uint i = 0; i < c; i++)
                            {
                                EdmVisibility v = new EdmVisibility { Arg = (int)U32() };
                                uint r = U32();
                                for (uint j = 0; j < r; j++)
                                    v.Ranges.Add(new[] { F64(), F64() });
                                n.Visibility.Add(v);
                            }
                            break;
                        }
                    default:
                        throw new InvalidDataException("unknown node type " + type);
                }
                return n;
            }

            // ---- render items

            private object RenderItem(string category)
            {
                string type = Str();
                switch (type)
                {
                    case "model::RenderNode":
                        {
                            EdmRenderNode r = new EdmRenderNode { Name = UStr() };
                            U32();
                            r.Props = Properties();
                            U32();
                            r.Material = (int)U32();
                            uint c = U32();
                            if (c == 1)
                            {
                                r.Parents.Add((int)U32());
                                r.DamageArgs.Add(I32());
                            }
                            else
                            {
                                for (uint i = 0; i < c; i++)
                                {
                                    r.Parents.Add((int)U32());
                                    I32();
                                    r.DamageArgs.Add(I32());
                                }
                            }
                            Vertices(r);
                            r.Indices = Indices();
                            return r;
                        }
                    case "model::SkinNode":
                        {
                            EdmRenderNode r = new EdmRenderNode { Name = UStr(), Skin = true };
                            U32();
                            r.Props = Properties();
                            U32();
                            r.Material = (int)U32();
                            uint bones = U32();
                            p += (int)bones * 4;
                            U32();
                            Vertices(r);
                            r.Indices = Indices();
                            return r;
                        }
                    case "model::ShellNode":
                        {
                            UStr();
                            U32();
                            Properties();
                            U32();
                            VertexFormat();
                            EdmRenderNode r = new EdmRenderNode();
                            Vertices(r);
                            Indices();
                            return null;
                        }
                    case "model::SegmentsNode":
                        {
                            UStr();
                            U32();
                            Properties();
                            U32();
                            uint c = U32();
                            p += (int)c * 24;
                            return null;
                        }
                    case "model::Connector":
                        {
                            EdmConnector cn = new EdmConnector { Name = UStr() };
                            U32();
                            cn.Props = Properties();
                            cn.Parent = (int)U32();
                            U32();
                            return cn;
                        }
                    case "model::LightNode":
                        {
                            UStr();
                            U32();
                            Properties();
                            U32();
                            U8();
                            Properties();
                            U8();
                            return null;
                        }
                    case "model::FakeOmniLightsNode":
                        {
                            UStr();
                            U32();
                            Properties();
                            p += 20;
                            uint c = U32();
                            p += (int)c * 48;
                            return null;
                        }
                    case "model::FakeSpotLightsNode":
                        {
                            UStr();
                            U32();
                            Properties();
                            U32();
                            U32();
                            uint c = U32();
                            p += (int)c * 20;
                            uint n = U32();
                            p += (int)n * 65;
                            return null;
                        }
                    case "model::FakeALSNode":
                        {
                            UStr();
                            U32();
                            Properties();
                            p += 12;
                            uint c = U32();
                            p += (int)c * 80;
                            return null;
                        }
                    case "model::NumberNode":
                        {
                            // A render node (digit strips) followed by its value controls (argument, scale ...).
                            UStr();
                            U32();
                            Properties();
                            U32();
                            U32();
                            uint c = U32();
                            p += c == 1 ? 8 : (int)c * 12;
                            EdmRenderNode r = new EdmRenderNode();
                            Vertices(r);
                            Indices();
                            uint n = U32();
                            if (n > 4096)
                                throw new InvalidDataException("number node");
                            p += (int)n * 16;
                            return null;
                        }
                    default:
                        throw new InvalidDataException("unknown render item " + type);
                }
            }

            private void Vertices(EdmRenderNode r)
            {
                uint count = U32();
                uint stride = U32();
                long n = (long)count * stride;
                if (n < 0 || p + n * 4 > d.Length)
                    throw new InvalidDataException("bad vertex block");
                r.Stride = (int)stride;
                r.Vertices = F32s((int)n);
            }

            private int[] Indices()
            {
                byte type = U8();
                uint n = U32();
                U32();
                int[] idx = new int[n];
                switch (type)
                {
                    case 0:
                        for (int i = 0; i < n; i++)
                            idx[i] = d[p + i];
                        p += (int)n;
                        break;
                    case 1:
                        for (int i = 0; i < n; i++)
                            idx[i] = d[p + 2 * i] | (d[p + 2 * i + 1] << 8);
                        p += (int)n * 2;
                        break;
                    case 2:
                        Buffer.BlockCopy(d, p, idx, 0, (int)n * 4);
                        p += (int)n * 4;
                        break;
                    default:
                        throw new InvalidDataException("bad index type " + type);
                }
                return idx;
            }

            // Moves to the next render item of a known type after one that could not be read.
            private bool Resync(string category)
            {
                if (version != 10)
                    return false;
                HashSet<uint> wanted = new HashSet<uint>();
                string[] names = category == "CONNECTORS" ? new[] { "model::Connector" }
                    : category == "RENDER_NODES" ? new[] { "model::RenderNode", "model::SkinNode", "model::FakeOmniLightsNode", "model::FakeSpotLightsNode", "model::FakeALSNode" }
                    : new string[0];
                for (int i = 0; i < strings.Count; i++)
                {
                    if (Array.IndexOf(names, strings[i]) >= 0)
                        wanted.Add((uint)i);
                }
                if (wanted.Count == 0)
                    return false;
                int start = p + 4;
                int end = Math.Min(d.Length - 4, start + (8 << 20));
                for (int i = start; i < end; i++)
                {
                    uint v = BitConverter.ToUInt32(d, i);
                    if (!wanted.Contains(v))
                        continue;
                    // The type must be followed by a plausible name length.
                    uint len = BitConverter.ToUInt32(d, i + 4);
                    if (len > 512)
                        continue;
                    p = i;
                    return true;
                }
                return false;
            }
        }

        private static readonly Encoding Encoding1251 = CreateEncoding();

        private static Encoding CreateEncoding()
        {
            try
            {
                return Encoding.GetEncoding(1251);
            }
            catch (Exception)
            {
                return Encoding.UTF8;
            }
        }
    }

    internal enum EdmNodeKind
    {
        Plain,
        Transform,
        Animation,
        Visibility,
        Lod
    }

    internal sealed class EdmNode
    {
        public string Type = string.Empty;
        public string Name = string.Empty;
        public EdmNodeKind Kind;
        public int Parent = -1;
        public Dictionary<string, object> Props;
        // Column-major 4x4 (TransformNode, Bone, the animation's base matrix).
        public double[] Matrix;
        public double[] Position;
        public double[] Quat1;
        public double[] Quat2;
        public double[] Scale;
        public List<EdmKeys> PositionKeys = new List<EdmKeys>();
        public List<EdmKeys> RotationKeys = new List<EdmKeys>();
        public List<EdmKeys> ScaleKeys = new List<EdmKeys>();
        public List<EdmVisibility> Visibility = new List<EdmVisibility>();
        public List<double[]> Lods;
    }

    internal sealed class EdmKeys
    {
        public int Arg;
        public List<double> Frames = new List<double>();
        public List<double[]> Values = new List<double[]>();
    }

    internal sealed class EdmVisibility
    {
        public int Arg;
        public List<double[]> Ranges = new List<double[]>();
    }

    internal sealed class EdmRenderNode
    {
        public string Name = string.Empty;
        public bool Skin;
        public int Material;
        public Dictionary<string, object> Props;
        public List<int> Parents = new List<int>();
        public List<int> DamageArgs = new List<int>();
        public int Stride;
        public float[] Vertices;
        public int[] Indices;
    }

    internal sealed class EdmConnector
    {
        public string Name = string.Empty;
        public int Parent = -1;
        public Dictionary<string, object> Props;
    }

    internal sealed class EdmMaterial
    {
        public string Name = string.Empty;
        public string Shader = string.Empty;
        public int Blending;
        public int Culling;
        public int Decal;
        public bool HasAlpha;
        public byte[] VertexFormat = new byte[0];
        public List<EdmTexture> Textures = new List<EdmTexture>();
        public Dictionary<string, object> Uniforms;

        // The float offset of a vertex channel (0 position, 1 normal, 4 the first texture coordinates), or -1.
        public int Offset(int channel)
        {
            if (channel >= VertexFormat.Length || VertexFormat[channel] == 0)
                return -1;
            int o = 0;
            for (int i = 0; i < channel; i++)
                o += VertexFormat[i];
            return o;
        }

        public string Texture(int index)
        {
            foreach (EdmTexture t in Textures)
            {
                if (t.Index == index)
                    return t.Name;
            }
            return string.Empty;
        }
    }

    internal sealed class EdmTexture
    {
        public int Index;
        public string Name = string.Empty;
    }
}
