#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.CockpitPictures
{
    // A read-only reader of Unity's serialized asset files (the "SerializedFile" format of a game's resources.assets,
    // sharedassetsN.assets and levelN; format 14 and newer, Unity 5.x to 6). An object is read with the file's own type
    // tree where the build kept it, else with JoyPro's copy of Unity 2019-2022's layouts of the classes a cockpit needs
    // (GameObject, Transform, MeshFilter, MeshRenderer, Mesh, Material, Texture2D, TextAsset and the MonoBehaviour
    // header). What it cannot read it skips: Read returns null.
    internal sealed class UnityAssetFile : IDisposable
    {
        public string Path = string.Empty;
        public int Format;
        public bool BigEndian;
        public string UnityVersion = string.Empty;
        public int Platform;
        public bool HasTypeTrees;
        public long DataOffset;
        public List<UnityType> Types = new List<UnityType>();
        public Dictionary<long, UnityObjectInfo> Objects = new Dictionary<long, UnityObjectInfo>();
        public List<UnityObjectInfo> ObjectList = new List<UnityObjectInfo>();
        public List<string> Externals = new List<string>();
        public List<string> Warnings = new List<string>();

        private Stream stream;
        private readonly Dictionary<string, Stream> resources = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, UnityTypeNode> builtIn = new Dictionary<int, UnityTypeNode>();

        public static UnityAssetFile Open(string path)
        {
            FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            try
            {
                UnityAssetFile f = Parse(fs);
                f.Path = path;
                return f;
            }
            catch (Exception)
            {
                fs.Dispose();
                throw;
            }
        }

        // Reads the header and the metadata (types, objects, external files) of a serialized file; the stream stays
        // with the file (objects are read from it on demand).
        public static UnityAssetFile Parse(Stream s)
        {
            UnityAssetFile f = new UnityAssetFile { stream = s };
            UnityReader r = new UnityReader(s) { BigEndian = true };
            s.Position = 0;
            uint metadataSize = r.UInt32();
            long fileSize = r.UInt32();
            f.Format = (int)r.UInt32();
            long dataOffset = r.UInt32();
            if (f.Format < 14 || f.Format > 40)
                throw new InvalidDataException("not a Unity serialized file of a supported format (" + f.Format.ToString(CultureInfo.InvariantCulture) + ")");
            f.BigEndian = r.Byte() != 0;
            r.Bytes(3);
            if (f.Format >= 22)
            {
                metadataSize = r.UInt32();
                fileSize = r.Int64();
                dataOffset = r.Int64();
                r.Int64();
            }
            if (fileSize > s.Length + 16 || dataOffset > s.Length || metadataSize > s.Length)
                throw new InvalidDataException("the header's sizes do not fit the file");
            f.DataOffset = dataOffset;
            r.BigEndian = f.BigEndian;
            f.UnityVersion = r.CString();
            f.Platform = r.Int32();
            f.HasTypeTrees = r.Byte() != 0;
            int typeCount = r.Int32();
            if (typeCount < 0 || typeCount > 100000)
                throw new InvalidDataException("bad type count");
            for (int i = 0; i < typeCount; i++)
                f.Types.Add(f.ReadType(r, false));
            int objectCount = r.Int32();
            if (objectCount < 0 || objectCount > 10000000)
                throw new InvalidDataException("bad object count");
            for (int i = 0; i < objectCount; i++)
            {
                r.Align(4);
                UnityObjectInfo o = new UnityObjectInfo { PathId = r.Int64() };
                o.Offset = (f.Format >= 22 ? r.Int64() : r.UInt32()) + dataOffset;
                o.Size = r.UInt32();
                o.TypeIndex = r.Int32();
                if (f.Format < 16)
                {
                    o.ClassId = r.UInt16();
                    r.Int16();
                    if (f.Format == 15)
                        r.Byte();
                }
                else if (o.TypeIndex >= 0 && o.TypeIndex < f.Types.Count)
                {
                    o.ClassId = f.Types[o.TypeIndex].ClassId;
                }
                f.Objects[o.PathId] = o;
                f.ObjectList.Add(o);
            }
            int scripts = r.Int32();
            for (int i = 0; i < scripts && i < 1000000; i++)
            {
                r.Int32();
                r.Align(4);
                r.Int64();
            }
            int externals = r.Int32();
            for (int i = 0; i < externals && i < 100000; i++)
            {
                r.CString();
                r.Bytes(16);
                r.Int32();
                f.Externals.Add(r.CString());
            }
            return f;
        }

        private UnityType ReadType(UnityReader r, bool isRef)
        {
            UnityType t = new UnityType { ClassId = r.Int32() };
            if (Format >= 16)
                r.Byte();
            if (Format >= 17)
                t.ScriptTypeIndex = r.Int16();
            if ((isRef && t.ScriptTypeIndex >= 0) || (Format < 16 && t.ClassId < 0) || (Format >= 16 && t.ClassId == 114))
                r.Bytes(16);
            r.Bytes(16);
            if (HasTypeTrees)
            {
                t.Tree = UnityTypeNode.ReadBlob(r, Format);
                if (Format >= 21)
                {
                    if (isRef)
                    {
                        r.CString();
                        r.CString();
                        r.CString();
                    }
                    else
                    {
                        int deps = r.Int32();
                        for (int i = 0; i < deps && i < 100000; i++)
                            r.Int32();
                    }
                }
            }
            return t;
        }

        public IEnumerable<UnityObjectInfo> OfClass(int classId)
        {
            return ObjectList.Where(o => o.ClassId == classId);
        }

        public byte[] RawData(UnityObjectInfo o, int max = int.MaxValue)
        {
            int n = (int)Math.Min(o.Size, (long)max);
            byte[] b = new byte[n];
            lock (stream)
            {
                stream.Position = o.Offset;
                int got = 0;
                while (got < n)
                {
                    int k = stream.Read(b, got, n - got);
                    if (k <= 0)
                        break;
                    got += k;
                }
                if (got < n)
                    Array.Resize(ref b, got);
            }
            return b;
        }

        // The type tree an object is read with: the file's own, else JoyPro's copy for the class (null: unknown).
        public UnityTypeNode TreeOf(UnityObjectInfo o)
        {
            if (o.TypeIndex >= 0 && o.TypeIndex < Types.Count && Types[o.TypeIndex].Tree != null)
                return Types[o.TypeIndex].Tree;
            UnityTypeNode t;
            if (!builtIn.TryGetValue(o.ClassId, out t))
            {
                t = UnityLayouts.For(o.ClassId, UnityVersion);
                builtIn[o.ClassId] = t;
            }
            return t;
        }

        // An object as nested values (Dictionary per structure, List per array, byte[] per byte array), or null when it
        // cannot be read. A MonoBehaviour without the file's type tree is read up to its name ("m_Name"); the script's
        // own fields are left in "_rest" (bytes).
        public Dictionary<string, object> Read(long pathId)
        {
            UnityObjectInfo o;
            return Objects.TryGetValue(pathId, out o) ? Read(o) : null;
        }

        public Dictionary<string, object> Read(UnityObjectInfo o)
        {
            UnityTypeNode tree = TreeOf(o);
            if (tree == null)
                return null;
            try
            {
                byte[] data = RawData(o);
                UnityReader r = new UnityReader(new MemoryStream(data, false)) { BigEndian = BigEndian };
                Dictionary<string, object> v = r.Value(tree) as Dictionary<string, object>;
                if (v == null)
                    return null;
                long left = data.Length - r.Position;
                if (left > 0)
                {
                    bool own = o.TypeIndex >= 0 && o.TypeIndex < Types.Count && Types[o.TypeIndex].Tree != null;
                    // JoyPro's copy of a layout that leaves bytes over does not fit this build: better nothing than
                    // wrong values (a MonoBehaviour's script fields follow its header).
                    if (!own && o.ClassId != 114)
                        return null;
                    byte[] rest = new byte[left];
                    Buffer.BlockCopy(data, (int)r.Position, rest, 0, (int)left);
                    v["_rest"] = rest;
                }
                return v;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The name of an object (its first field for most named classes, after the header for a MonoBehaviour); empty
        // when it has none. Reads only the start of the object.
        public string NameOf(UnityObjectInfo o)
        {
            try
            {
                byte[] head = RawData(o, 512);
                UnityReader r = new UnityReader(new MemoryStream(head, false)) { BigEndian = BigEndian };
                switch (o.ClassId)
                {
                    case 1: // GameObject: components, layer, name
                        {
                            int n = r.Int32();
                            if (n < 0 || n > 40)
                                return string.Empty;
                            r.Bytes(n * 12);
                            r.Int32();
                            return r.AlignedString(256);
                        }
                    case 114: // MonoBehaviour: game object, enabled, script, name
                        r.Bytes(12);
                        r.Byte();
                        r.Align(4);
                        r.Bytes(12);
                        return r.AlignedString(256);
                    default:
                        return r.AlignedString(256);
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        // Bytes of a streamed resource (".resS", ".resource") next to this file.
        public byte[] Resource(string path, long offset, long size)
        {
            if (string.IsNullOrEmpty(path) || size <= 0 || size > int.MaxValue)
                return null;
            string name = path;
            int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            if (slash >= 0)
                name = name.Substring(slash + 1);
            string file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? string.Empty, name);
            Stream s;
            lock (resources)
            {
                if (!resources.TryGetValue(file, out s))
                {
                    s = File.Exists(file) ? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16) : null;
                    resources[file] = s;
                }
            }
            if (s == null || offset < 0 || offset + size > s.Length)
                return null;
            byte[] b = new byte[size];
            lock (s)
            {
                s.Position = offset;
                int got = 0;
                while (got < b.Length)
                {
                    int k = s.Read(b, got, b.Length - got);
                    if (k <= 0)
                        return null;
                    got += k;
                }
            }
            return b;
        }

        public void Dispose()
        {
            try
            {
                if (stream != null)
                    stream.Dispose();
            }
            catch (Exception)
            {
            }
            lock (resources)
            {
                foreach (Stream s in resources.Values)
                {
                    if (s != null)
                        s.Dispose();
                }
                resources.Clear();
            }
        }

        // ------------------------------------------------------------------ helpers on read values

        public static object Field(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            return Field(d, key) as Dictionary<string, object>;
        }

        public static List<object> List(Dictionary<string, object> d, string key)
        {
            return Field(d, key) as List<object> ?? new List<object>();
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v = Field(d, key);
            if (v is string)
                return (string)v;
            if (v is byte[])
                return Encoding.UTF8.GetString((byte[])v);
            return string.Empty;
        }

        public static byte[] Bytes(Dictionary<string, object> d, string key)
        {
            object v = Field(d, key);
            if (v is byte[])
                return (byte[])v;
            if (v is string)
                return Encoding.UTF8.GetBytes((string)v);
            return new byte[0];
        }

        public static double Num(Dictionary<string, object> d, string key, double fallback = 0)
        {
            object v = Field(d, key);
            if (v == null || v is string || v is Dictionary<string, object> || v is List<object> || v is byte[])
                return fallback;
            try
            {
                return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        // A PPtr's path id (0 = none); fileId 0 is this file, others index Externals (1-based).
        public static long PathIdOf(object pptr, out int fileId)
        {
            Dictionary<string, object> p = pptr as Dictionary<string, object>;
            fileId = (int)Num(p, "m_FileID");
            return (long)Num(p, "m_PathID");
        }

        public static long PathIdOf(object pptr)
        {
            int fileId;
            long id = PathIdOf(pptr, out fileId);
            return fileId == 0 ? id : 0;
        }

        public static double[] Vector(Dictionary<string, object> d, string key, params string[] parts)
        {
            Dictionary<string, object> v = Obj(d, key);
            double[] r = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                r[i] = Num(v, parts[i]);
            return r;
        }
    }

    internal sealed class UnityObjectInfo
    {
        public long PathId;
        public long Offset;
        public long Size;
        public int TypeIndex;
        public int ClassId;
    }

    internal sealed class UnityType
    {
        public int ClassId;
        public short ScriptTypeIndex = -1;
        public UnityTypeNode Tree;
    }

    // A node of a Unity type tree: a field with its type, name, size (-1: variable) and flags (0x4000: align to four
    // bytes after it; IsArray: the "Array" node of a vector, string or map).
    internal sealed class UnityTypeNode
    {
        public string Type = string.Empty;
        public string Name = string.Empty;
        public int Level;
        public int ByteSize;
        public int MetaFlag;
        public bool IsArray;
        public List<UnityTypeNode> Children = new List<UnityTypeNode>();

        public const int AlignFlag = 0x4000;

        public bool Align { get { return (MetaFlag & AlignFlag) != 0; } }

        // Unity's shared strings of type trees (an offset with the top bit set points here).
        public static readonly string[] CommonStrings =
        {
            "AABB", "AnimationClip", "AnimationCurve", "AnimationState", "Array", "Base", "BitField", "bitset", "bool", "char", "ColorRGBA", "Component", "data",
            "deque", "double", "dynamic_array", "FastPropertyName", "first", "float", "Font", "GameObject", "Generic Mono", "GradientNEW", "GUID", "GUIStyle",
            "int", "list", "long long", "map", "Matrix4x4f", "MdFour", "MonoBehaviour", "MonoScript", "m_ByteSize", "m_Curve", "m_EditorClassIdentifier",
            "m_EditorHideFlags", "m_Enabled", "m_ExtensionPtr", "m_GameObject", "m_Index", "m_IsArray", "m_IsStatic", "m_MetaFlag", "m_Name",
            "m_ObjectHideFlags", "m_PrefabInternal", "m_PrefabParentObject", "m_Script", "m_StaticEditorFlags", "m_Type", "m_Version", "Object", "pair",
            "PPtr<Component>", "PPtr<GameObject>", "PPtr<Material>", "PPtr<MonoBehaviour>", "PPtr<MonoScript>", "PPtr<Object>", "PPtr<Prefab>", "PPtr<Sprite>",
            "PPtr<TextAsset>", "PPtr<Texture>", "PPtr<Texture2D>", "PPtr<Transform>", "Prefab", "Quaternionf", "Rectf", "RectInt", "RectOffset", "second", "set",
            "short", "size", "SInt16", "SInt32", "SInt64", "SInt8", "staticvector", "string", "TextAsset", "TextMesh", "Texture", "Texture2D", "Transform",
            "TypelessData", "UInt16", "UInt32", "UInt64", "UInt8", "unsigned int", "unsigned long long", "unsigned short", "vector", "Vector2f", "Vector3f",
            "Vector4f", "m_ScriptingClassIdentifier", "Gradient", "Type*", "int2_storage", "int3_storage", "BoundsInt", "m_CorrespondingSourceObject",
            "m_PrefabInstance", "m_PrefabAsset", "FileSize", "Hash128", "RenderingLayerMask", "fixed_array", "EntityId", "LoadableObjectId", "LoadableSceneId"
        };

        private static Dictionary<uint, string> commonByOffset;

        public static string Common(uint offset)
        {
            if (commonByOffset == null)
            {
                Dictionary<uint, string> map = new Dictionary<uint, string>();
                uint at = 0;
                foreach (string s in CommonStrings)
                {
                    map[at] = s;
                    at += (uint)s.Length + 1;
                }
                commonByOffset = map;
            }
            string v;
            return commonByOffset.TryGetValue(offset, out v) ? v : offset.ToString(CultureInfo.InvariantCulture);
        }

        public static uint CommonOffset(string s)
        {
            uint at = 0;
            foreach (string c in CommonStrings)
            {
                if (c == s)
                    return at | 0x80000000u;
                at += (uint)c.Length + 1;
            }
            return uint.MaxValue;
        }

        // The type tree "blob" of a serialized file (format 12 and newer): the nodes flat with their level, then the
        // strings.
        public static UnityTypeNode ReadBlob(UnityReader r, int format)
        {
            int count = r.Int32();
            int strings = r.Int32();
            if (count <= 0 || count > 200000 || strings < 0 || strings > 50000000)
                throw new InvalidDataException("bad type tree");
            int nodeSize = format >= 19 ? 32 : 24;
            byte[] nodes = r.Bytes(count * nodeSize);
            byte[] buffer = r.Bytes(strings);
            List<UnityTypeNode> flat = new List<UnityTypeNode>(count);
            uint[] typeAt = new uint[count], nameAt = new uint[count];
            for (int i = 0; i < count; i++)
            {
                UnityReader n = new UnityReader(new MemoryStream(nodes, i * nodeSize, nodeSize, false)) { BigEndian = r.BigEndian };
                n.UInt16();
                UnityTypeNode node = new UnityTypeNode();
                node.Level = n.Byte();
                node.IsArray = (n.Byte() & 1) != 0;
                typeAt[i] = n.UInt32();
                nameAt[i] = n.UInt32();
                node.ByteSize = n.Int32();
                n.Int32();
                node.MetaFlag = n.Int32();
                node.Type = Text(buffer, typeAt[i]);
                node.Name = Text(buffer, nameAt[i]);
                flat.Add(node);
            }
            return Tree(flat);
        }

        private static string Text(byte[] buffer, uint offset)
        {
            if ((offset & 0x80000000u) != 0)
                return Common(offset & 0x7FFFFFFFu);
            if (offset >= buffer.Length)
                return offset.ToString(CultureInfo.InvariantCulture);
            int end = (int)offset;
            while (end < buffer.Length && buffer[end] != 0)
                end++;
            return Encoding.UTF8.GetString(buffer, (int)offset, end - (int)offset);
        }

        // The nodes nested by level (the first one is the root).
        public static UnityTypeNode Tree(List<UnityTypeNode> flat)
        {
            if (flat.Count == 0)
                return null;
            Stack<UnityTypeNode> path = new Stack<UnityTypeNode>();
            UnityTypeNode root = flat[0];
            path.Push(root);
            for (int i = 1; i < flat.Count; i++)
            {
                UnityTypeNode n = flat[i];
                while (path.Count > 0 && path.Peek().Level >= n.Level)
                    path.Pop();
                if (path.Count == 0)
                    break;
                path.Peek().Children.Add(n);
                path.Push(n);
            }
            return root;
        }
    }

    // Little or big endian reads with Unity's four-byte alignment.
    internal sealed class UnityReader
    {
        private readonly Stream s;
        private readonly byte[] buf = new byte[8];
        public bool BigEndian;

        public UnityReader(Stream s)
        {
            this.s = s;
        }

        public long Position { get { return s.Position; } set { s.Position = value; } }

        private byte[] Fill(int n)
        {
            int got = 0;
            while (got < n)
            {
                int k = s.Read(buf, got, n - got);
                if (k <= 0)
                    throw new EndOfStreamException();
                got += k;
            }
            if (BigEndian == BitConverter.IsLittleEndian && n > 1)
                Array.Reverse(buf, 0, n);
            return buf;
        }

        public byte Byte()
        {
            int b = s.ReadByte();
            if (b < 0)
                throw new EndOfStreamException();
            return (byte)b;
        }

        public short Int16() { return BitConverter.ToInt16(Fill(2), 0); }
        public ushort UInt16() { return BitConverter.ToUInt16(Fill(2), 0); }
        public int Int32() { return BitConverter.ToInt32(Fill(4), 0); }
        public uint UInt32() { return BitConverter.ToUInt32(Fill(4), 0); }
        public long Int64() { return BitConverter.ToInt64(Fill(8), 0); }
        public ulong UInt64() { return BitConverter.ToUInt64(Fill(8), 0); }
        public float Single() { return BitConverter.ToSingle(Fill(4), 0); }
        public double Double() { return BitConverter.ToDouble(Fill(8), 0); }

        public byte[] Bytes(int n)
        {
            if (n < 0 || n > s.Length - s.Position)
                throw new EndOfStreamException();
            byte[] b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int k = s.Read(b, got, n - got);
                if (k <= 0)
                    throw new EndOfStreamException();
                got += k;
            }
            return b;
        }

        public void Align(int n)
        {
            long p = s.Position;
            long m = (p + n - 1) / n * n;
            if (m != p)
                s.Position = m;
        }

        public string CString()
        {
            List<byte> b = new List<byte>();
            while (true)
            {
                int c = s.ReadByte();
                if (c <= 0)
                    break;
                b.Add((byte)c);
                if (b.Count > 65536)
                    throw new InvalidDataException("string too long");
            }
            return Encoding.UTF8.GetString(b.ToArray());
        }

        public string AlignedString(int max = 1 << 24)
        {
            int n = Int32();
            if (n < 0 || n > max)
                throw new InvalidDataException("bad string");
            string v = Encoding.UTF8.GetString(Bytes(n));
            Align(4);
            return v;
        }

        // One value of a type tree node.
        public object Value(UnityTypeNode node)
        {
            object v;
            List<UnityTypeNode> ch = node.Children;
            if (ch.Count == 0)
            {
                v = Primitive(node);
            }
            else if (node.Type == "string" && ch[0].IsArray)
            {
                int n = Int32();
                v = Encoding.UTF8.GetString(Bytes(n));
                if (ch[0].Align)
                    Align(4);
            }
            else if (node.Type == "TypelessData" || (node.IsArray && ch.Count == 2))
            {
                v = ArrayValue(node);
            }
            else if (ch.Count == 1 && ch[0].IsArray)
            {
                v = ArrayValue(ch[0]);
            }
            else
            {
                Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (UnityTypeNode c in ch)
                    d[c.Name] = Value(c);
                v = d;
            }
            if (node.Align)
                Align(4);
            return v;
        }

        private object ArrayValue(UnityTypeNode array)
        {
            int n = Int32();
            if (n < 0 || n > s.Length - s.Position)
                throw new InvalidDataException("bad array");
            UnityTypeNode element = array.Children.Count >= 2 ? array.Children[1] : null;
            object v;
            if (element == null || (element.Children.Count == 0 && (element.ByteSize == 1 || element.Type == "UInt8" || element.Type == "char" || element.Type == "SInt8")))
            {
                v = Bytes(n);
            }
            else
            {
                List<object> list = new List<object>(Math.Min(n, 1 << 16));
                for (int i = 0; i < n; i++)
                    list.Add(Value(element));
                v = list;
            }
            if (array.Align)
                Align(4);
            return v;
        }

        private object Primitive(UnityTypeNode n)
        {
            switch (n.Type)
            {
                case "bool":
                    return Byte() != 0;
                case "SInt8":
                    return (sbyte)Byte();
                case "UInt8":
                case "char":
                    return Byte();
                case "SInt16":
                case "short":
                    return Int16();
                case "UInt16":
                case "unsigned short":
                    return UInt16();
                case "int":
                case "SInt32":
                case "Type*":
                    return Int32();
                case "UInt32":
                case "unsigned int":
                    return UInt32();
                case "SInt64":
                case "long long":
                    return Int64();
                case "UInt64":
                case "unsigned long long":
                case "FileSize":
                    return UInt64();
                case "float":
                    return Single();
                case "double":
                    return Double();
                default:
                    if (n.ByteSize > 0)
                        return Bytes(n.ByteSize);
                    return null;
            }
        }
    }

    // JoyPro's copy of the layouts of the classes a cockpit needs, for builds without type trees (Unity 2019.3 to
    // 2022.3; checked against Unity 2022.3's own trees). A compact notation, expanded into Unity's own form: one field
    // per line, two spaces per level, "type name", "*" at the end for "align after it"; "string", "PPtr",
    // "Vector2f/3f/4f", "Quaternionf", "ColorRGBA", "AABB", "bytes" (a byte array) and "StreamingInfo" are written
    // short; "vector name" and "map name" take their element (or first and second) as children.
    internal static class UnityLayouts
    {
        private const string GameObject = @"GameObject Base
  vector m_Component
    ComponentPair data
      PPtr component
  unsigned int m_Layer
  string m_Name
  UInt16 m_Tag
  bool m_IsActive";

        private const string Transform = @"Transform Base
  PPtr m_GameObject
  Quaternionf m_LocalRotation
  Vector3f m_LocalPosition
  Vector3f m_LocalScale *
  vector m_Children
    PPtr data
  PPtr m_Father";

        private const string MeshFilter = @"MeshFilter Base
  PPtr m_GameObject
  PPtr m_Mesh";

        private const string MeshRenderer = @"MeshRenderer Base
  PPtr m_GameObject
  bool m_Enabled
  UInt8 m_CastShadows
  UInt8 m_ReceiveShadows
  UInt8 m_DynamicOccludee
  UInt8 m_StaticShadowCaster
  UInt8 m_MotionVectors
  UInt8 m_LightProbeUsage
  UInt8 m_ReflectionProbeUsage
  UInt8 m_RayTracingMode
  UInt8 m_RayTraceProcedural *
  unsigned int m_RenderingLayerMask
  int m_RendererPriority
  UInt16 m_LightmapIndex
  UInt16 m_LightmapIndexDynamic
  Vector4f m_LightmapTilingOffset
  Vector4f m_LightmapTilingOffsetDynamic
  vector m_Materials
    PPtr data
  StaticBatchInfo m_StaticBatchInfo
    UInt16 firstSubMesh
    UInt16 subMeshCount
  PPtr m_StaticBatchRoot
  PPtr m_ProbeAnchor
  PPtr m_LightProbeVolumeOverride *
  int m_SortingLayerID
  SInt16 m_SortingLayer
  SInt16 m_SortingOrder *
  PPtr m_AdditionalVertexStreams
  PPtr m_EnlightenVertexStream";

        private const string PackedFloats = @"
      unsigned int m_NumItems
      float m_Range
      float m_Start
      bytes m_Data
      UInt8 m_BitSize *";

        private const string PackedInts = @"
      unsigned int m_NumItems
      bytes m_Data
      UInt8 m_BitSize *";

        private static readonly string Mesh = @"Mesh Base
  string m_Name
  vector m_SubMeshes
    SubMesh data
      unsigned int firstByte
      unsigned int indexCount
      int topology
      unsigned int baseVertex
      unsigned int firstVertex
      unsigned int vertexCount
      AABB localAABB
  BlendShapeData m_Shapes
    vector vertices
      BlendShapeVertex data
        Vector3f vertex
        Vector3f normal
        Vector3f tangent
        unsigned int index
    vector shapes
      MeshBlendShape data
        unsigned int firstVertex
        unsigned int vertexCount
        bool hasNormals
        bool hasTangents *
    vector channels
      MeshBlendShapeChannel data
        string name
        unsigned int nameHash
        int frameIndex
        int frameCount
    vector fullWeights
      float data
  vector m_BindPose
    Matrix4x4f data
      bytes64 m
  vector m_BoneNameHashes
    unsigned int data
  unsigned int m_RootBoneNameHash
  vector m_BonesAABB
    MinMaxAABB data
      Vector3f m_Min
      Vector3f m_Max
  VariableBoneCountWeights m_VariableBoneCountWeights
    vector m_Data
      unsigned int data
  UInt8 m_MeshCompression
  bool m_IsReadable
  bool m_KeepVertices
  bool m_KeepIndices *
  int m_IndexFormat
  bytes m_IndexBuffer
  VertexData m_VertexData *
    unsigned int m_VertexCount
    vector m_Channels
      ChannelInfo data
        UInt8 stream
        UInt8 offset
        UInt8 format
        UInt8 dimension
    TypelessData m_DataSize *
  CompressedMesh m_CompressedMesh
    PackedBitVector m_Vertices" + PackedFloats + @"
    PackedBitVector m_UV" + PackedFloats + @"
    PackedBitVector m_Normals" + PackedFloats + @"
    PackedBitVector m_Tangents" + PackedFloats + @"
    PackedBitVector m_Weights" + PackedInts + @"
    PackedBitVector m_NormalSigns" + PackedInts + @"
    PackedBitVector m_TangentSigns" + PackedInts + @"
    PackedBitVector m_FloatColors" + PackedFloats + @"
    PackedBitVector m_BoneIndices" + PackedInts + @"
    PackedBitVector m_Triangles" + PackedInts + @"
    unsigned int m_UVInfo
  AABB m_LocalAABB
  int m_MeshUsageFlags
  int m_CookingOptions
  bytes m_BakedConvexCollisionMesh
  bytes m_BakedTriangleCollisionMesh
  float m_MeshMetrics[0]
  float m_MeshMetrics[1] *
  StreamingInfo m_StreamData";

        private const string Material = @"Material Base
  string m_Name
  PPtr m_Shader
  vector m_ValidKeywords
    string data
  vector m_InvalidKeywords
    string data
  unsigned int m_LightmapFlags
  bool m_EnableInstancingVariants
  bool m_DoubleSidedGI *
  int m_CustomRenderQueue
  map stringTagMap
    string first
    string second
  vector disabledShaderPasses
    string data
  UnityPropertySheet m_SavedProperties
    map m_TexEnvs
      string first
      UnityTexEnv second
        PPtr m_Texture
        Vector2f m_Scale
        Vector2f m_Offset
    map m_Ints
      string first
      int second
    map m_Floats
      string first
      float second
    map m_Colors
      string first
      ColorRGBA second
  vector m_BuildTextureStacks
    BuildTextureStackReference data
      string groupName
      string itemName";

        private const string Texture2D = @"Texture2D Base
  string m_Name
  int m_ForcedFallbackFormat
  bool m_DownscaleFallback
  bool m_IsAlphaChannelOptional *
  int m_Width
  int m_Height
  unsigned int m_CompleteImageSize
  int m_MipsStripped
  int m_TextureFormat
  int m_MipCount
  bool m_IsReadable
  bool m_IsPreProcessed
  bool m_IgnoreMipmapLimit *
  string m_MipmapLimitGroupName
  bool m_StreamingMipmaps *
  int m_StreamingMipmapsPriority *
  int m_ImageCount
  int m_TextureDimension
  GLTextureSettings m_TextureSettings
    int m_FilterMode
    int m_Aniso
    float m_MipBias
    int m_WrapU
    int m_WrapV
    int m_WrapW
  int m_LightmapFormat
  int m_ColorSpace
  bytes m_PlatformBlob
  TypelessData image data *
  StreamingInfo m_StreamData";

        private const string TextAsset = @"TextAsset Base
  string m_Name
  bytes m_Script";

        private const string MonoBehaviour = @"MonoBehaviour Base
  PPtr m_GameObject
  UInt8 m_Enabled *
  PPtr m_Script
  string m_Name";

        private const string MonoScript = @"MonoScript Base
  string m_Name
  int m_ExecutionOrder
  bytes16 m_PropertiesHash
  string m_ClassName
  string m_Namespace
  string m_AssemblyName";

        // The layout of a class for a Unity version (null when JoyPro has none for it).
        public static UnityTypeNode For(int classId, string unityVersion)
        {
            int major = 0, minor = 0;
            string[] parts = (unityVersion ?? string.Empty).Split('.');
            if (parts.Length >= 2)
            {
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major);
                int.TryParse(new string(parts[1].TakeWhile(char.IsDigit).ToArray()), NumberStyles.Integer, CultureInfo.InvariantCulture, out minor);
            }
            // The copies are Unity 2022.3's; the classes read here kept this layout from 2022.2 to 2022.3 (a newer or
            // older build is read only where its own type trees are kept).
            bool v2022 = major == 2022 && minor >= 2;
            string spec;
            switch (classId)
            {
                case 1:
                    spec = major >= 2019 ? GameObject : null;
                    break;
                case 4:
                    spec = major >= 2019 ? Transform : null;
                    break;
                case 33:
                    spec = major >= 2019 ? MeshFilter : null;
                    break;
                case 49:
                    spec = major >= 2019 ? TextAsset : null;
                    break;
                case 114:
                    spec = major >= 2019 ? MonoBehaviour : null;
                    break;
                case 115:
                    spec = major >= 2019 ? MonoScript : null;
                    break;
                case 23:
                    spec = v2022 ? MeshRenderer : null;
                    break;
                case 43:
                    spec = v2022 ? Mesh : null;
                    break;
                case 21:
                    spec = v2022 ? Material : null;
                    break;
                case 28:
                    spec = v2022 ? Texture2D : null;
                    break;
                default:
                    spec = null;
                    break;
            }
            return spec == null ? null : Parse(spec);
        }

        // Expands the compact notation into type tree nodes.
        public static UnityTypeNode Parse(string spec)
        {
            List<KeyValuePair<int, string>> lines = new List<KeyValuePair<int, string>>();
            foreach (string raw in spec.Replace("\r", string.Empty).Split('\n'))
            {
                if (raw.Trim().Length == 0)
                    continue;
                int indent = raw.Length - raw.TrimStart(' ').Length;
                lines.Add(new KeyValuePair<int, string>(indent / 2, raw.Trim()));
            }
            int at = 0;
            return Node(lines, ref at);
        }

        private static UnityTypeNode Node(List<KeyValuePair<int, string>> lines, ref int at)
        {
            int level = lines[at].Key;
            string text = lines[at].Value;
            at++;
            bool align = text.EndsWith(" *", StringComparison.Ordinal);
            if (align)
                text = text.Substring(0, text.Length - 2);
            string type, name;
            // Multi-word types ("unsigned int") come first: the name is the last word, except "image data".
            if (text.EndsWith(" image data", StringComparison.Ordinal))
            {
                type = text.Substring(0, text.Length - " image data".Length);
                name = "image data";
            }
            else
            {
                int sp = text.LastIndexOf(' ');
                type = text.Substring(0, sp);
                name = text.Substring(sp + 1);
            }
            List<UnityTypeNode> kids = new List<UnityTypeNode>();
            while (at < lines.Count && lines[at].Key > level)
                kids.Add(Node(lines, ref at));
            UnityTypeNode n = Expand(type, name, kids);
            if (align)
                n.MetaFlag |= UnityTypeNode.AlignFlag;
            return n;
        }

        private static UnityTypeNode Leaf(string type, string name, int size)
        {
            return new UnityTypeNode { Type = type, Name = name, ByteSize = size };
        }

        private static UnityTypeNode Struct(string type, string name, params UnityTypeNode[] children)
        {
            UnityTypeNode n = new UnityTypeNode { Type = type, Name = name, ByteSize = -1 };
            n.Children.AddRange(children);
            return n;
        }

        private static UnityTypeNode ArrayOf(UnityTypeNode element, bool align)
        {
            element.Name = element.Name.Length == 0 ? "data" : element.Name;
            UnityTypeNode a = Struct("Array", "Array", Leaf("int", "size", 4), element);
            a.IsArray = true;
            if (align)
                a.MetaFlag = UnityTypeNode.AlignFlag;
            return a;
        }

        private static UnityTypeNode Floats(string type, string name, params string[] parts)
        {
            return Struct(type, name, parts.Select(p => Leaf("float", p, 4)).ToArray());
        }

        private static UnityTypeNode Expand(string type, string name, List<UnityTypeNode> kids)
        {
            if (kids.Count > 0)
            {
                if (type == "vector")
                    return Struct("vector", name, ArrayOf(kids[0], true));
                if (type == "map")
                    return Struct("map", name, ArrayOf(Struct("pair", "data", kids[0], kids[1]), false));
                return Struct(type, name, kids.ToArray());
            }
            switch (type)
            {
                case "string":
                    return Struct("string", name, ArrayOf(Leaf("char", "data", 1), true));
                case "bytes":
                    return Struct("vector", name, ArrayOf(Leaf("UInt8", "data", 1), true));
                case "TypelessData":
                    {
                        UnityTypeNode t = Struct("TypelessData", name, Leaf("int", "size", 4), Leaf("UInt8", "data", 1));
                        t.IsArray = true;
                        return t;
                    }
                case "bytes16":
                    return Struct("Hash128", name, Enumerable.Range(0, 16).Select(i => Leaf("UInt8", "bytes[" + i.ToString(CultureInfo.InvariantCulture) + "]", 1)).ToArray());
                case "bytes64":
                    return Struct("floats", name, Enumerable.Range(0, 16).Select(i => Leaf("float", "e" + i.ToString(CultureInfo.InvariantCulture), 4)).ToArray());
                case "PPtr":
                    return Struct("PPtr", name, Leaf("int", "m_FileID", 4), Leaf("SInt64", "m_PathID", 8));
                case "Vector2f":
                    return Floats(type, name, "x", "y");
                case "Vector3f":
                    return Floats(type, name, "x", "y", "z");
                case "Vector4f":
                case "Quaternionf":
                    return Floats(type, name, "x", "y", "z", "w");
                case "ColorRGBA":
                    return Floats(type, name, "r", "g", "b", "a");
                case "AABB":
                    return Struct("AABB", name, Floats("Vector3f", "m_Center", "x", "y", "z"), Floats("Vector3f", "m_Extent", "x", "y", "z"));
                case "StreamingInfo":
                    return Struct("StreamingInfo", name, Leaf("UInt64", "offset", 8), Leaf("unsigned int", "size", 4), Struct("string", "path", ArrayOf(Leaf("char", "data", 1), true)));
                default:
                    return Leaf(type, name, Size(type));
            }
        }

        private static int Size(string type)
        {
            switch (type)
            {
                case "bool":
                case "SInt8":
                case "UInt8":
                case "char":
                    return 1;
                case "SInt16":
                case "UInt16":
                case "short":
                case "unsigned short":
                    return 2;
                case "SInt64":
                case "UInt64":
                case "long long":
                case "unsigned long long":
                case "double":
                    return 8;
                default:
                    return 4;
            }
        }
    }

    // A Unity mesh as plain arrays (Unity's own frame and units): positions, normals, the first texture coordinates and
    // the triangles of each sub-mesh.
    internal sealed class UnityMesh
    {
        public string Name = string.Empty;
        public int VertexCount;
        public float[] Positions;
        public float[] Normals;
        public float[] Uvs;
        public List<int[]> SubMeshes = new List<int[]>();
        public string Problem = string.Empty;

        // Size in bytes of one component of a vertex format (Unity 2019 and newer).
        public static int FormatSize(int format, bool legacy)
        {
            if (legacy)
            {
                switch (format)
                {
                    case 0:
                        return 4;
                    case 1:
                        return 2;
                    case 2:
                    case 3:
                        return 1;
                    case 4:
                        return 4;
                    default:
                        return 4;
                }
            }
            switch (format)
            {
                case 0:
                case 10:
                case 11:
                    return 4;
                case 1:
                case 4:
                case 5:
                case 8:
                case 9:
                    return 2;
                default:
                    return 1;
            }
        }

        // One component as a float (normalised formats to -1..1 / 0..1).
        public static float Component(byte[] d, int at, int format, bool legacy)
        {
            if (legacy)
            {
                switch (format)
                {
                    case 0:
                        return BitConverter.ToSingle(d, at);
                    case 1:
                        return Half(BitConverter.ToUInt16(d, at));
                    case 2:
                        return d[at] / 255f;
                    case 3:
                        return d[at];
                    default:
                        return BitConverter.ToUInt32(d, at);
                }
            }
            switch (format)
            {
                case 0:
                    return BitConverter.ToSingle(d, at);
                case 1:
                    return Half(BitConverter.ToUInt16(d, at));
                case 2:
                    return d[at] / 255f;
                case 3:
                    return Math.Max(-1f, (sbyte)d[at] / 127f);
                case 4:
                    return BitConverter.ToUInt16(d, at) / 65535f;
                case 5:
                    return Math.Max(-1f, BitConverter.ToInt16(d, at) / 32767f);
                case 6:
                    return d[at];
                case 7:
                    return (sbyte)d[at];
                case 8:
                    return BitConverter.ToUInt16(d, at);
                case 9:
                    return BitConverter.ToInt16(d, at);
                case 10:
                    return BitConverter.ToUInt32(d, at);
                case 11:
                    return BitConverter.ToInt32(d, at);
                default:
                    return 0;
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
            return sign != 0 ? -v : v;
        }

        // A mesh from its read values; the vertex data from the object, or streamed from the file's resource.
        public static UnityMesh From(Dictionary<string, object> m, UnityAssetFile file, string unityVersion)
        {
            UnityMesh r = new UnityMesh { Name = UnityAssetFile.Str(m, "m_Name") };
            int major = 0;
            int.TryParse((unityVersion ?? string.Empty).Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major);
            bool legacy = major > 0 && major < 2019;
            if (UnityAssetFile.Num(m, "m_MeshCompression") != 0)
            {
                r.Problem = "compressed mesh (not read)";
                return r;
            }
            Dictionary<string, object> vd = UnityAssetFile.Obj(m, "m_VertexData");
            int count = (int)UnityAssetFile.Num(vd, "m_VertexCount");
            byte[] data = UnityAssetFile.Bytes(vd, "m_DataSize");
            if (data.Length == 0)
            {
                Dictionary<string, object> sd = UnityAssetFile.Obj(m, "m_StreamData");
                long size = (long)UnityAssetFile.Num(sd, "size");
                if (size > 0 && file != null)
                    data = file.Resource(UnityAssetFile.Str(sd, "path"), (long)UnityAssetFile.Num(sd, "offset"), size);
            }
            if (data == null || data.Length == 0 || count <= 0)
            {
                r.Problem = "no vertex data";
                return r;
            }
            List<int[]> channels = new List<int[]>();
            foreach (Dictionary<string, object> c in UnityAssetFile.List(vd, "m_Channels").OfType<Dictionary<string, object>>())
                channels.Add(new[] { (int)UnityAssetFile.Num(c, "stream"), (int)UnityAssetFile.Num(c, "offset"), (int)UnityAssetFile.Num(c, "format"), (int)UnityAssetFile.Num(c, "dimension") & 15 });
            r.Decode(data, count, channels, legacy);
            r.Indices(UnityAssetFile.Bytes(m, "m_IndexBuffer"), (int)UnityAssetFile.Num(m, "m_IndexFormat"), UnityAssetFile.List(m, "m_SubMeshes").OfType<Dictionary<string, object>>().ToList());
            return r;
        }

        // The vertex channels (stream, offset, format, dimension per channel: 0 position, 1 normal, 4 uv0); the streams
        // lie one after another, each starting at a multiple of 16 bytes.
        public void Decode(byte[] data, int count, List<int[]> channels, bool legacy = false)
        {
            VertexCount = count;
            int streams = channels.Count == 0 ? 0 : channels.Where(c => c[3] > 0).Select(c => c[0]).DefaultIfEmpty(0).Max() + 1;
            int[] stride = new int[streams], start = new int[streams];
            foreach (int[] c in channels)
            {
                if (c[3] > 0 && c[0] < streams)
                    stride[c[0]] += FormatSize(c[2], legacy) * c[3];
            }
            int offset = 0;
            for (int s = 0; s < streams; s++)
            {
                start[s] = offset;
                offset += stride[s] * count;
                offset = (offset + 15) & ~15;
            }
            Positions = Channel(data, count, channels, 0, 3, stride, start, legacy);
            Normals = Channel(data, count, channels, 1, 3, stride, start, legacy);
            // Unity 2019 and newer: position, normal, tangent, colour, uv0..7 ...; before: position, normal, colour, uv0 ...
            Uvs = Channel(data, count, channels, legacy ? 3 : 4, 2, stride, start, legacy);
            if (Positions == null)
                Problem = "no positions";
        }

        private static float[] Channel(byte[] data, int count, List<int[]> channels, int index, int want, int[] stride, int[] start, bool legacy)
        {
            if (index >= channels.Count)
                return null;
            int[] c = channels[index];
            if (c[3] == 0 || c[0] >= stride.Length)
                return null;
            int size = FormatSize(c[2], legacy);
            float[] o = new float[count * want];
            for (int v = 0; v < count; v++)
            {
                int at = start[c[0]] + v * stride[c[0]] + c[1];
                for (int k = 0; k < want && k < c[3]; k++)
                {
                    int p = at + k * size;
                    if (p + size > data.Length)
                        return null;
                    o[v * want + k] = Component(data, p, c[2], legacy);
                }
            }
            return o;
        }

        // The triangles of each sub-mesh (16 or 32-bit indices; quads split in two).
        public void Indices(byte[] buffer, int format, List<Dictionary<string, object>> subMeshes)
        {
            int size = format == 1 ? 4 : 2;
            foreach (Dictionary<string, object> sm in subMeshes)
            {
                long first = (long)UnityAssetFile.Num(sm, "firstByte") / size;
                long n = (long)UnityAssetFile.Num(sm, "indexCount");
                int topology = (int)UnityAssetFile.Num(sm, "topology");
                int baseVertex = (int)UnityAssetFile.Num(sm, "baseVertex");
                List<int> tri = new List<int>();
                if ((first + n) * size <= buffer.Length && (topology == 0 || topology == 2))
                {
                    int[] idx = new int[n];
                    for (long i = 0; i < n; i++)
                    {
                        long at = (first + i) * size;
                        idx[i] = (size == 4 ? (int)BitConverter.ToUInt32(buffer, (int)at) : BitConverter.ToUInt16(buffer, (int)at)) + baseVertex;
                    }
                    if (topology == 0)
                    {
                        for (int i = 0; i + 2 < idx.Length; i += 3)
                        {
                            if (idx[i] < VertexCount && idx[i + 1] < VertexCount && idx[i + 2] < VertexCount)
                                tri.AddRange(new[] { idx[i], idx[i + 1], idx[i + 2] });
                        }
                    }
                    else
                    {
                        for (int i = 0; i + 3 < idx.Length; i += 4)
                        {
                            if (idx.Skip(i).Take(4).All(x => x < VertexCount))
                                tri.AddRange(new[] { idx[i], idx[i + 1], idx[i + 2], idx[i], idx[i + 2], idx[i + 3] });
                        }
                    }
                }
                SubMeshes.Add(tri.ToArray());
            }
        }
    }

    // Unity's textures (the formats the cockpits use) to TextureImage: the largest mip level up to the size asked for.
    internal static class UnityTextures
    {
        public static TextureImage Decode(Dictionary<string, object> t, UnityAssetFile file, int maxSize)
        {
            int w = (int)UnityAssetFile.Num(t, "m_Width"), h = (int)UnityAssetFile.Num(t, "m_Height");
            int format = (int)UnityAssetFile.Num(t, "m_TextureFormat");
            int mips = Math.Max(1, (int)UnityAssetFile.Num(t, "m_MipCount", 1));
            if (w <= 0 || h <= 0 || w > 16384 || h > 16384)
                return null;
            byte[] data = UnityAssetFile.Bytes(t, "image data");
            if (data.Length == 0)
            {
                Dictionary<string, object> sd = UnityAssetFile.Obj(t, "m_StreamData");
                long size = (long)UnityAssetFile.Num(sd, "size");
                if (size > 0 && file != null)
                    data = file.Resource(UnityAssetFile.Str(sd, "path"), (long)UnityAssetFile.Num(sd, "offset"), size);
            }
            if (data == null || data.Length == 0)
                return null;
            byte[] dds = Dds(data, w, h, mips, format);
            return dds == null ? null : TextureImage.Decode(dds, "texture.dds", maxSize);
        }

        // A DDS file around Unity's texture data (the mip levels are stored the same way, largest first).
        public static byte[] Dds(byte[] data, int w, int h, int mips, int format)
        {
            string four = null;
            int dxgi = 0, bits = 0, pfFlags = 4;
            uint r = 0, g = 0, b = 0, a = 0;
            switch (format)
            {
                case 10: // DXT1
                    four = "DXT1";
                    break;
                case 12: // DXT5
                    four = "DXT5";
                    break;
                case 26: // BC4
                    four = "ATI1";
                    break;
                case 27: // BC5
                    four = "ATI2";
                    break;
                case 25: // BC7
                    four = "DX10";
                    dxgi = 98;
                    break;
                case 4: // RGBA32
                    pfFlags = 0x41;
                    bits = 32;
                    r = 0xFF;
                    g = 0xFF00;
                    b = 0xFF0000;
                    a = 0xFF000000;
                    break;
                case 5: // ARGB32
                    pfFlags = 0x41;
                    bits = 32;
                    a = 0xFF;
                    r = 0xFF00;
                    g = 0xFF0000;
                    b = 0xFF000000;
                    break;
                case 14: // BGRA32
                    pfFlags = 0x41;
                    bits = 32;
                    b = 0xFF;
                    g = 0xFF00;
                    r = 0xFF0000;
                    a = 0xFF000000;
                    break;
                case 3: // RGB24
                    pfFlags = 0x40;
                    bits = 24;
                    r = 0xFF;
                    g = 0xFF00;
                    b = 0xFF0000;
                    break;
                case 1: // Alpha8
                case 63: // R8
                    pfFlags = 0x40;
                    bits = 8;
                    r = 0xFF;
                    break;
                default:
                    return null;
            }
            int header = 128 + (dxgi != 0 ? 20 : 0);
            byte[] d = new byte[header + data.Length];
            Action<int, int> put = (at, v) => BitConverter.GetBytes(v).CopyTo(d, at);
            d[0] = (byte)'D';
            d[1] = (byte)'D';
            d[2] = (byte)'S';
            d[3] = (byte)' ';
            put(4, 124);
            put(8, 0x1007 | 0x20000);
            put(12, h);
            put(16, w);
            put(28, mips);
            put(76, 32);
            put(80, pfFlags);
            if (four != null)
                Encoding.ASCII.GetBytes(four).CopyTo(d, 84);
            put(88, bits);
            put(92, (int)r);
            put(96, (int)g);
            put(100, (int)b);
            put(104, (int)a);
            if (dxgi != 0)
            {
                put(128, dxgi);
                put(132, 3);
                put(140, 1);
            }
            Buffer.BlockCopy(data, 0, d, header, data.Length);
            return d;
        }
    }
}
