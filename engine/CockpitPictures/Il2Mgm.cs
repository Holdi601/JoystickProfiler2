#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace JoyPro.CockpitPictures
{
    // An IL-2 Great Battles model (.mgm): a tree of objects written one after the other, each object as its class name
    // ("struct model::Node", "struct model::Chunk", "struct model::Animation", "class model::Model"), a version and an
    // object id; an id seen before is a reference, a new one is followed by the object's fields. The nodes carry the
    // transforms (rows of a 4x4 matrix for row vectors: the node's frame in its parent and the geometry's offset in the
    // node), the chunks the material (shader, textures) and where their vertices and triangles are in the file; every
    // node draws a range of its chunk. The animations say which nodes an argument (the game's control or gauge
    // number) moves or shows: per node a track of 36-byte keys (time 0..1, visibility, position, a quaternion of shorts,
    // a scale of shorts).
    internal sealed class Il2Mgm
    {
        public const int KeySize = 36;

        public sealed class Node
        {
            public string Id = string.Empty;
            public int Index;
            public string Name = string.Empty;
            public float[] Box = new float[6];
            public Node Parent;
            public double[] Frame = Mat4.Identity();   // the node in its parent (column-major for column vectors)
            public double[] Offset = Mat4.Identity();  // the geometry in the node
            public int[] Words = new int[12];          // [8] vertex count, [9] index count, [10] vertex byte offset, [11] index byte offset
            public int Stride;
            public string Shader = string.Empty;
            public List<Node> Children = new List<Node>();
            public Chunk Chunk;
            public int Slot;

            public int VertexCount { get { return Words[8]; } }
            public int IndexCount { get { return Words[9]; } }
            public int IndexByteOffset { get { return Words[11]; } }
        }

        public sealed class Chunk
        {
            public string Id = string.Empty;
            public string Shader = string.Empty;
            public byte[] Colors = new byte[16];
            public List<string> Textures = new List<string>();
            public int VertexCount;
            public int IndexCount;
            public long VertexOffset;
            public long IndexOffset;
            public int Stride;
            public List<Node> Nodes = new List<Node>();
            public List<Node> Bones = new List<Node>();
        }

        public sealed class Track
        {
            public int Node;      // the node's index
            public int Flags;
            public byte[] Keys;
        }

        public sealed class Animation
        {
            public string Id = string.Empty;
            public int Arg;
            public List<Track> Tracks = new List<Track>();
            public List<int[]> Targets = new List<int[]>();
        }

        public byte[] Data;
        public Node Root;
        public readonly List<Node> Nodes = new List<Node>();
        public readonly List<Chunk> Chunks = new List<Chunk>();
        public readonly List<Animation> Animations = new List<Animation>();
        public readonly Dictionary<int, Node> ByIndex = new Dictionary<int, Node>();

        private int pos;
        private readonly Dictionary<string, object> seen = new Dictionary<string, object>(StringComparer.Ordinal);

        public static Il2Mgm Read(byte[] data)
        {
            Il2Mgm m = new Il2Mgm { Data = data };
            object root = m.Object();
            if (!(root is ModelMarker))
                throw new FormatException("not an IL-2 model");
            foreach (Node n in m.Nodes)
            {
                if (!m.ByIndex.ContainsKey(n.Index))
                    m.ByIndex[n.Index] = n;
            }
            return m;
        }

        private sealed class ModelMarker
        {
        }

        // ------------------------------------------------------------------ reading

        private void Need(int n)
        {
            if (n < 0 || pos + n > Data.Length)
                throw new FormatException("model ends early at " + pos);
        }

        private int I32()
        {
            Need(4);
            int v = BitConverter.ToInt32(Data, pos);
            pos += 4;
            return v;
        }

        private int U16()
        {
            Need(2);
            int v = BitConverter.ToUInt16(Data, pos);
            pos += 2;
            return v;
        }

        private float F32()
        {
            Need(4);
            float v = BitConverter.ToSingle(Data, pos);
            pos += 4;
            return v;
        }

        private string Str()
        {
            int n = I32();
            if (n < 0 || n > 65536)
                throw new FormatException("bad string at " + (pos - 4));
            Need(n);
            string s = Encoding.GetEncoding(28591).GetString(Data, pos, n);
            pos += n;
            return s;
        }

        private double[] Matrix()
        {
            double[] m = new double[16];
            for (int i = 0; i < 16; i++)
                m[i] = F32();
            return m;
        }

        // An object (or a reference to one read before); null for an empty pointer.
        private object Object()
        {
            string type = Str();
            I32();
            string id = Str();
            if (type.Length == 0)
                return null;
            object known;
            if (seen.TryGetValue(id, out known))
                return known;
            switch (type)
            {
                case "class model::Model":
                {
                    ModelMarker mm = new ModelMarker();
                    seen[id] = mm;
                    Root = Object() as Node;
                    int n = I32();
                    for (int k = 0; k < n; k++)
                    {
                        int arg = I32();
                        Animation a = Object() as Animation;
                        if (a != null)
                        {
                            a.Arg = arg;
                            Animations.Add(a);
                        }
                    }
                    return mm;
                }
                case "struct model::Node":
                {
                    Node node = new Node { Id = id };
                    seen[id] = node;
                    ReadNode(node);
                    return node;
                }
                case "struct model::Chunk":
                {
                    Chunk c = new Chunk { Id = id };
                    seen[id] = c;
                    ReadChunk(c);
                    return c;
                }
                case "struct model::Animation":
                {
                    Animation a = new Animation { Id = id };
                    seen[id] = a;
                    ReadAnimation(a);
                    return a;
                }
                default:
                    throw new FormatException("unknown object " + type);
            }
        }

        private void ReadNode(Node n)
        {
            Nodes.Add(n);
            n.Index = I32();
            n.Name = Str();
            for (int i = 0; i < 6; i++)
                n.Box[i] = F32();
            n.Parent = Object() as Node;
            I32();
            n.Frame = Matrix();
            I32();
            n.Offset = Matrix();
            for (int i = 0; i < 12; i++)
                n.Words[i] = I32();
            n.Stride = I32();
            n.Shader = Str();
            int children = I32();
            for (int i = 0; i < children; i++)
            {
                Node c = Object() as Node;
                if (c != null)
                    n.Children.Add(c);
            }
            U16();
            I32();
            n.Chunk = Object() as Chunk;
            n.Slot = I32();
        }

        private void ReadChunk(Chunk c)
        {
            Chunks.Add(c);
            c.Shader = Str();
            Need(16);
            Array.Copy(Data, pos, c.Colors, 0, 16);
            pos += 16;
            int textures = I32();
            if (textures < 0 || textures > 64)
                throw new FormatException("bad texture count");
            for (int i = 0; i < textures; i++)
                c.Textures.Add(Str());
            c.VertexCount = I32();
            c.IndexCount = I32();
            c.VertexOffset = (uint)I32();
            c.IndexOffset = (uint)I32();
            Need(1);
            pos++;
            c.Stride = I32();
            int nodes = I32();
            for (int i = 0; i < nodes; i++)
            {
                Node n = Object() as Node;
                if (n != null)
                    c.Nodes.Add(n);
            }
            int map = I32();
            Need(8 * map);
            pos += 8 * map;
            int bones = I32();
            for (int i = 0; i < bones; i++)
            {
                Node b = Object() as Node;
                if (b != null)
                    c.Bones.Add(b);
            }
        }

        private void ReadAnimation(Animation a)
        {
            int tracks = I32();
            for (int i = 0; i < tracks; i++)
            {
                Track t = new Track { Node = U16(), Flags = U16() };
                int bytes = I32();
                Need(bytes);
                t.Keys = new byte[bytes];
                Array.Copy(Data, pos, t.Keys, 0, bytes);
                pos += bytes;
                a.Tracks.Add(t);
            }
            int targets = I32();
            for (int i = 0; i < targets; i++)
                a.Targets.Add(new[] { I32(), I32() });
        }

        // ------------------------------------------------------------------ geometry

        // The node's geometry in the model: the offset, the pose its animations give it (none: the model's rest), the
        // node's frame, then the same for the parents.
        public static double[] World(Node n, Dictionary<Node, double[]> pose = null)
        {
            double[] m = Mat4.Multiply(Local(n, pose, true), n.Offset);
            for (Node p = n.Parent; p != null; p = p.Parent)
                m = Mat4.Multiply(Local(p, pose, false), m);
            return m;
        }

        // The node's frame in the model (its pivot), without the geometry offset.
        public static double[] Pivot(Node n, Dictionary<Node, double[]> pose = null)
        {
            double[] m = Local(n, pose, false);
            for (Node p = n.Parent; p != null; p = p.Parent)
                m = Mat4.Multiply(Local(p, pose, false), m);
            return m;
        }

        // The node in its parent with its pose (the key's scale only for the node's own geometry).
        private static double[] Local(Node n, Dictionary<Node, double[]> pose, bool own)
        {
            double[] k;
            if (pose == null || !pose.TryGetValue(n, out k))
                return n.Frame;
            double[] part = new double[16];
            Array.Copy(k, own || k.Length < 32 ? 0 : 16, part, 0, 16);
            return Mat4.Multiply(n.Frame, part);
        }

        private static double[] Combine(double[] a, double[] b)
        {
            double[] r = new double[32];
            double[] full = Mat4.Multiply(Part(a, 0), Part(b, 0)), rigid = Mat4.Multiply(Part(a, 16), Part(b, 16));
            Array.Copy(full, 0, r, 0, 16);
            Array.Copy(rigid, 0, r, 16, 16);
            return r;
        }

        private static double[] Part(double[] k, int start)
        {
            double[] p = new double[16];
            Array.Copy(k, k.Length >= start + 16 ? start : 0, p, 0, 16);
            return p;
        }

        // The pose of the animated nodes at the given argument values: per node the product of its tracks' keys
        // (interpolated between the keys around the value), applied in the node before its frame.
        public Dictionary<Node, double[]> Pose(Func<int, double> argValue)
        {
            Dictionary<Node, double[]> pose = new Dictionary<Node, double[]>();
            foreach (Animation a in Animations)
            {
                double value = argValue(a.Arg);
                foreach (Track t in a.Tracks)
                {
                    Node n;
                    double[] k = KeyAt(t, value);
                    if (k == null || !ByIndex.TryGetValue(t.Node, out n))
                        continue;
                    double[] have;
                    pose[n] = pose.TryGetValue(n, out have) ? Combine(have, k) : k;
                }
            }
            return pose;
        }

        // The argument values a track has keys at (the control's positions or the ends of its travel).
        public static List<double> KeyTimes(Track t)
        {
            List<double> times = new List<double>();
            if (t.Keys == null || t.Keys.Length < KeySize || t.Keys.Length % KeySize != 0)
                return times;
            for (int k = 0; k + KeySize <= t.Keys.Length; k += KeySize)
                times.Add(BitConverter.ToSingle(t.Keys, k));
            return times;
        }

        // A track's transform at a value: position (floats at 8), rotation (shorts at 20: x, y, z, w; the model's
        // quaternions turn the other way than the column-vector convention), scale (shorts at 28, 1000 = 1).
        public static double[] KeyAt(Track t, double value)
        {
            if (t.Keys == null || t.Keys.Length < KeySize || t.Keys.Length % KeySize != 0)
                return null;
            int count = t.Keys.Length / KeySize;
            int a = 0;
            for (int k = 0; k < count; k++)
            {
                if (BitConverter.ToSingle(t.Keys, k * KeySize) <= value + 1e-6)
                    a = k;
            }
            int b = Math.Min(count - 1, a + 1);
            double ta = BitConverter.ToSingle(t.Keys, a * KeySize), tb = BitConverter.ToSingle(t.Keys, b * KeySize);
            double f = b == a || tb <= ta ? 0 : Math.Max(0, Math.Min(1, (value - ta) / (tb - ta)));
            double[] pa = KeyParts(t.Keys, a * KeySize), pb = KeyParts(t.Keys, b * KeySize);
            double[] p = new double[10];
            // Quaternions on the same side before blending.
            double dot = pa[3] * pb[3] + pa[4] * pb[4] + pa[5] * pb[5] + pa[6] * pb[6];
            for (int i = 0; i < 10; i++)
            {
                double vb = i >= 3 && i <= 6 && dot < 0 ? -pb[i] : pb[i];
                p[i] = pa[i] + (vb - pa[i]) * f;
            }
            // [0..15] the key with its scale (for the node's own geometry), [16..31] without (what hangs under the
            // node does not stretch with it: the cables of a pedal chain scale link by link).
            double[] r = Mat4.Rotation(new[] { p[6], -p[3], -p[4], -p[5] });
            double[] m = Mat4.Multiply(r, Mat4.Scale(p[7], p[8], p[9]));
            double[] both = new double[32];
            for (int i = 0; i < 16; i++)
            {
                both[i] = m[i];
                both[16 + i] = r[i];
            }
            for (int i = 0; i < 3; i++)
            {
                both[12 + i] = p[i];
                both[28 + i] = p[i];
            }
            return both;
        }

        // x, y, z, qx, qy, qz, qw, sx, sy, sz of a key.
        private static double[] KeyParts(byte[] keys, int o)
        {
            double[] p = new double[10];
            p[0] = BitConverter.ToSingle(keys, o + 8);
            p[1] = BitConverter.ToSingle(keys, o + 12);
            p[2] = BitConverter.ToSingle(keys, o + 16);
            for (int i = 0; i < 4; i++)
                p[3 + i] = BitConverter.ToInt16(keys, o + 20 + 2 * i) / 32767.0;
            for (int i = 0; i < 3; i++)
            {
                double s = BitConverter.ToUInt16(keys, o + 28 + 2 * i) / 1000.0;
                p[7 + i] = s <= 0 || double.IsNaN(s) ? 1 : s;
            }
            for (int i = 0; i < 3; i++)
            {
                if (double.IsNaN(p[i]) || double.IsInfinity(p[i]) || Math.Abs(p[i]) > 100)
                    p[i] = 0;
            }
            return p;
        }

        public bool HasGeometry(Node n)
        {
            return n.Chunk != null && n.IndexCount >= 3 && n.Chunk.Stride >= 32;
        }

        // The node's triangles: chunk-relative vertex numbers.
        public int[] Indices(Node n)
        {
            if (!HasGeometry(n))
                return new int[0];
            long start = n.Chunk.IndexOffset + n.IndexByteOffset;
            int count = n.IndexCount - n.IndexCount % 3;
            if (start < 0 || start + 2L * count > Data.Length)
                return new int[0];
            int[] idx = new int[count];
            for (int i = 0; i < count; i++)
                idx[i] = BitConverter.ToUInt16(Data, (int)(start + 2 * i));
            return idx;
        }

        // Position (0), normal (12) and texture coordinates (24) of a vertex of the chunk.
        public bool Vertex(Chunk c, int v, out float x, out float y, out float z, out float nx, out float ny, out float nz, out float u, out float w)
        {
            long o = c.VertexOffset + (long)v * c.Stride;
            x = y = z = nx = ny = nz = u = w = 0;
            if (v < 0 || o < 0 || o + 32 > Data.Length)
                return false;
            int p = (int)o;
            x = BitConverter.ToSingle(Data, p);
            y = BitConverter.ToSingle(Data, p + 4);
            z = BitConverter.ToSingle(Data, p + 8);
            nx = BitConverter.ToSingle(Data, p + 12);
            ny = BitConverter.ToSingle(Data, p + 16);
            nz = BitConverter.ToSingle(Data, p + 20);
            u = BitConverter.ToSingle(Data, p + 24);
            w = BitConverter.ToSingle(Data, p + 28);
            return true;
        }

        public const int SkinStride = 116;

        // Whether a chunk is skinned (the vertices hang on the chunk's bones: up to three positions and normals, one
        // per bone, then texture coordinates at 76 and (bone, weight) pairs at 84).
        public static bool Skinned(Chunk c)
        {
            return c != null && c.Stride == SkinStride && c.Bones.Count > 0;
        }

        // A skinned vertex in the model: the weighted positions in the bones' frames.
        public bool SkinVertex(Chunk c, int v, Func<Node, double[]> boneFrame, out Vec3 position, out Vec3 normal, out float u, out float w)
        {
            position = normal = new Vec3(0, 0, 0);
            u = w = 0;
            long o = c.VertexOffset + (long)v * c.Stride;
            if (v < 0 || o < 0 || o + SkinStride > Data.Length)
                return false;
            int p = (int)o;
            u = BitConverter.ToSingle(Data, p + 76);
            w = BitConverter.ToSingle(Data, p + 80);
            double total = 0;
            for (int i = 0; i < 3; i++)
            {
                int bone = (int)Math.Round(BitConverter.ToSingle(Data, p + 84 + 8 * i));
                double weight = BitConverter.ToSingle(Data, p + 88 + 8 * i);
                if (weight <= 1e-4 || bone < 0 || bone >= c.Bones.Count || double.IsNaN(weight))
                    continue;
                double[] m = boneFrame(c.Bones[bone]);
                Vec3 local = Mat4.Point(m, BitConverter.ToSingle(Data, p + 12 * i), BitConverter.ToSingle(Data, p + 12 * i + 4), BitConverter.ToSingle(Data, p + 12 * i + 8));
                Vec3 n = Mat4.Direction(m, BitConverter.ToSingle(Data, p + 36 + 12 * i), BitConverter.ToSingle(Data, p + 40 + 12 * i), BitConverter.ToSingle(Data, p + 44 + 12 * i));
                position = position + local * weight;
                normal = normal + n * weight;
                total += weight;
            }
            if (total <= 1e-4)
                return false;
            position = position * (1 / total);
            normal = normal.Normalized;
            return true;
        }

        // ------------------------------------------------------------------ animations

        // Whether a track shows its node at an argument value (the key at or before the value; visibility byte 6).
        public static bool VisibleAt(Track t, double value)
        {
            if (t.Keys == null || t.Keys.Length < KeySize || t.Keys.Length % KeySize != 0)
                return true;
            int best = 0;
            for (int k = 0; k + KeySize <= t.Keys.Length; k += KeySize)
            {
                float time = BitConverter.ToSingle(t.Keys, k);
                if (time <= value + 1e-6)
                    best = k;
            }
            return t.Keys[best + 6] != 0;
        }

        // The nodes hidden at the given argument values (and everything under them).
        public HashSet<Node> Hidden(Func<int, double> argValue)
        {
            HashSet<Node> hidden = new HashSet<Node>();
            foreach (Animation a in Animations)
            {
                double value = argValue(a.Arg);
                foreach (Track t in a.Tracks)
                {
                    Node n;
                    if (ByIndex.TryGetValue(t.Node, out n) && !VisibleAt(t, value))
                        hidden.Add(n);
                }
            }
            foreach (Node n in hidden.ToList())
                AddSubtree(n, hidden);
            return hidden;
        }

        public static void AddSubtree(Node n, HashSet<Node> set)
        {
            Stack<Node> stack = new Stack<Node>();
            stack.Push(n);
            while (stack.Count > 0)
            {
                Node k = stack.Pop();
                set.Add(k);
                foreach (Node c in k.Children)
                {
                    if (!set.Contains(c))
                        stack.Push(c);
                }
            }
        }

        public static bool IsUnder(Node n, Node ancestor)
        {
            for (Node p = n; p != null; p = p.Parent)
            {
                if (p == ancestor)
                    return true;
            }
            return false;
        }

        // The nodes an argument moves or shows.
        public List<Node> NodesOf(int arg)
        {
            List<Node> list = new List<Node>();
            foreach (Animation a in Animations.Where(x => x.Arg == arg))
            {
                foreach (Track t in a.Tracks)
                {
                    Node n;
                    if (ByIndex.TryGetValue(t.Node, out n) && !list.Contains(n))
                        list.Add(n);
                }
            }
            return list;
        }
    }
}
