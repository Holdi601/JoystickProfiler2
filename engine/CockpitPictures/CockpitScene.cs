#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;

namespace JoyPro.CockpitPictures
{
    // A cockpit as triangles in one coordinate frame (x forward, y up, z right, metres), ready to draw: meshes with
    // their material, the named points (the game's clickable controls, the grips' pivots), and groups of meshes (the
    // stick, the throttle) by name. Every game's reader (DCS EDM, X-Plane OBJ ...) builds one.
    internal sealed class CockpitScene
    {
        public List<SceneMesh> Meshes = new List<SceneMesh>();
        public List<SceneMaterial> Materials = new List<SceneMaterial>();
        // Named points: world position (x, y, z) and, where known, the extent of the control's box.
        public Dictionary<string, ScenePoint> Points = new Dictionary<string, ScenePoint>(StringComparer.OrdinalIgnoreCase);
        // Named groups of meshes (indices into Meshes): "stick", "throttle", "collective" ...
        public Dictionary<string, List<int>> Groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings = new List<string>();
        // The model's node tree where the reader has one: the meshes under each node, the names and the parents.
        public Dictionary<int, List<int>> NodeMeshes = new Dictionary<int, List<int>>();
        public List<string> NodeNames = new List<string>();
        public List<int> NodeParents = new List<int>();

        public void Bounds(IEnumerable<int> meshes, out Vec3 min, out Vec3 max)
        {
            min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            foreach (int m in meshes)
            {
                float[] p = Meshes[m].Positions;
                for (int i = 0; i + 2 < p.Length; i += 3)
                {
                    min = Vec3.Min(min, new Vec3(p[i], p[i + 1], p[i + 2]));
                    max = Vec3.Max(max, new Vec3(p[i], p[i + 1], p[i + 2]));
                }
            }
        }

        public int TriangleCount
        {
            get
            {
                long n = 0;
                foreach (SceneMesh m in Meshes)
                    n += m.Indices.Length / 3;
                return (int)Math.Min(int.MaxValue, n);
            }
        }
    }

    internal sealed class SceneMesh
    {
        public string Name = string.Empty;
        // The node (part) it hangs on, for grouping and the callouts.
        public int Node = -1;
        public int Material;
        // An id per part, for the outlines between parts.
        public int Part;
        public float[] Positions;   // x, y, z per vertex (world)
        public float[] Normals;     // x, y, z per vertex (world, may be null)
        public float[] Uvs;         // u, v per vertex (may be null)
        public int[] Indices;       // triangles
    }

    internal sealed class SceneMaterial
    {
        public string Name = string.Empty;
        public TextureImage Albedo;
        // The colour without a texture (r, g, b 0..255).
        public byte[] Color = { 150, 150, 150 };
        public bool AlphaTest;
        public bool SelfIllumination;
        // The texture coordinates were flipped for this texture (X-Plane: PNG upside down against DDS).
        public bool FlipV;
    }

    internal sealed class ScenePoint
    {
        public string Name = string.Empty;
        public Vec3 Position;
        // Half size of the control's box where known (0 = a point).
        public double Radius;
        public int Node = -1;
    }

    internal struct Vec3
    {
        public double X, Y, Z;

        public Vec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec3 operator *(Vec3 a, double s) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }
        public static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        public static Vec3 Cross(Vec3 a, Vec3 b) { return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }
        public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }
        public Vec3 Normalized { get { double l = Length; return l > 1e-12 ? new Vec3(X / l, Y / l, Z / l) : new Vec3(0, 0, 0); } }
        public static Vec3 Min(Vec3 a, Vec3 b) { return new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)); }
        public static Vec3 Max(Vec3 a, Vec3 b) { return new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)); }
        public static Vec3 Lerp(Vec3 a, Vec3 b, double t) { return a + (b - a) * t; }

        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
        }
    }

    // 4x4 matrices, column-major (m[col * 4 + row]) like the EDM files and OpenGL.
    internal static class Mat4
    {
        public static double[] Identity()
        {
            return new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        }

        public static double[] Multiply(double[] a, double[] b)
        {
            double[] r = new double[16];
            for (int c = 0; c < 4; c++)
            {
                for (int row = 0; row < 4; row++)
                {
                    double s = 0;
                    for (int k = 0; k < 4; k++)
                        s += a[k * 4 + row] * b[c * 4 + k];
                    r[c * 4 + row] = s;
                }
            }
            return r;
        }

        public static double[] Translation(double x, double y, double z)
        {
            double[] m = Identity();
            m[12] = x;
            m[13] = y;
            m[14] = z;
            return m;
        }

        public static double[] Scale(double x, double y, double z)
        {
            double[] m = Identity();
            m[0] = x;
            m[5] = y;
            m[10] = z;
            return m;
        }

        // From a quaternion (w, x, y, z).
        public static double[] Rotation(double[] q)
        {
            double w = q[0], x = q[1], y = q[2], z = q[3];
            double n = Math.Sqrt(w * w + x * x + y * y + z * z);
            if (n < 1e-12)
                return Identity();
            w /= n;
            x /= n;
            y /= n;
            z /= n;
            double[] m = Identity();
            m[0] = 1 - 2 * (y * y + z * z);
            m[1] = 2 * (x * y + z * w);
            m[2] = 2 * (x * z - y * w);
            m[4] = 2 * (x * y - z * w);
            m[5] = 1 - 2 * (x * x + z * z);
            m[6] = 2 * (y * z + x * w);
            m[8] = 2 * (x * z + y * w);
            m[9] = 2 * (y * z - x * w);
            m[10] = 1 - 2 * (x * x + y * y);
            return m;
        }

        public static Vec3 Point(double[] m, double x, double y, double z)
        {
            return new Vec3(m[0] * x + m[4] * y + m[8] * z + m[12], m[1] * x + m[5] * y + m[9] * z + m[13], m[2] * x + m[6] * y + m[10] * z + m[14]);
        }

        public static Vec3 Direction(double[] m, double x, double y, double z)
        {
            return new Vec3(m[0] * x + m[4] * y + m[8] * z, m[1] * x + m[5] * y + m[9] * z, m[2] * x + m[6] * y + m[10] * z);
        }

        public static double Determinant3(double[] m)
        {
            return m[0] * (m[5] * m[10] - m[9] * m[6]) - m[4] * (m[1] * m[10] - m[9] * m[2]) + m[8] * (m[1] * m[6] - m[5] * m[2]);
        }

        // The inverse transpose of the upper 3x3 (for normals), as a 4x4 without translation.
        public static double[] NormalMatrix(double[] m)
        {
            double a = m[0], b = m[4], c = m[8], d = m[1], e = m[5], f = m[9], g = m[2], h = m[6], i = m[10];
            double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            if (Math.Abs(det) < 1e-18)
                return Identity();
            double inv = 1 / det;
            // inverse (row-major cofactors / det), then transposed, stored column-major
            double[] r = Identity();
            r[0] = (e * i - f * h) * inv;
            r[4] = (c * h - b * i) * inv;
            r[8] = (b * f - c * e) * inv;
            r[1] = (f * g - d * i) * inv;
            r[5] = (a * i - c * g) * inv;
            r[9] = (c * d - a * f) * inv;
            r[2] = (d * h - e * g) * inv;
            r[6] = (b * g - a * h) * inv;
            r[10] = (a * e - b * d) * inv;
            // transpose
            double[] t = Identity();
            for (int col = 0; col < 3; col++)
                for (int row = 0; row < 3; row++)
                    t[col * 4 + row] = r[row * 4 + col];
            return t;
        }
    }
}
