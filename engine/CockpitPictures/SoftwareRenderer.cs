#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace JoyPro.CockpitPictures
{
    // A camera: perspective (field of view) or orthographic, looking along Forward with Up, x to the right.
    internal sealed class RenderCamera
    {
        public Vec3 Eye;
        public Vec3 Forward = new Vec3(1, 0, 0);
        public Vec3 Up = new Vec3(0, 1, 0);
        public Vec3 Right = new Vec3(0, 0, 1);
        public bool Ortho;
        // Vertical field of view in degrees (perspective) or half the visible height in metres (orthographic).
        public double FovY = 60;
        public double HalfHeight = 0.5;
        public int Width = 1600;
        public int Height = 1000;
        public double Near = 0.03;

        public static RenderCamera LookAt(Vec3 eye, Vec3 target, double fovY, int width, int height)
        {
            RenderCamera c = new RenderCamera { Eye = eye, FovY = fovY, Width = width, Height = height };
            c.Aim(target - eye);
            return c;
        }

        public void Aim(Vec3 direction)
        {
            Forward = direction.Normalized;
            Vec3 worldUp = new Vec3(0, 1, 0);
            // Straight up: aft at the top (the head tilted back); straight down: forward at the top.
            if (Math.Abs(Vec3.Dot(Forward, worldUp)) > 0.98)
                worldUp = Forward.Y > 0 ? new Vec3(-1, 0, 0) : new Vec3(1, 0, 0);
            Right = Vec3.Cross(Forward, worldUp).Normalized;
            Up = Vec3.Cross(Right, Forward).Normalized;
        }

        public double Focal { get { return Height / 2.0 / Math.Tan(FovY * Math.PI / 360.0); } }

        // Screen position (pixels) and view depth of a world point; false when it is behind the camera.
        public bool Project(Vec3 p, out double sx, out double sy, out double depth)
        {
            Vec3 d = p - Eye;
            double vx = Vec3.Dot(d, Right), vy = Vec3.Dot(d, Up), vz = Vec3.Dot(d, Forward);
            depth = vz;
            if (Ortho)
            {
                double s = Height / 2.0 / HalfHeight;
                sx = Width / 2.0 + vx * s;
                sy = Height / 2.0 - vy * s;
                return true;
            }
            if (vz < Near)
            {
                sx = sy = 0;
                return false;
            }
            double f = Focal;
            sx = Width / 2.0 + vx / vz * f;
            sy = Height / 2.0 - vy / vz * f;
            return true;
        }

        public RenderCamera Scaled(int factor)
        {
            RenderCamera c = (RenderCamera)MemberwiseClone();
            c.Width = Width * factor;
            c.Height = Height * factor;
            return c;
        }
    }

    // What one render leaves per pixel: the depth (view distance), the part and material drawn there, the surface
    // normal (world), the texture colour (albedo) and the shaded colour.
    internal sealed class RenderBuffers
    {
        public int Width;
        public int Height;
        public float[] Depth;
        public int[] Part;
        public int[] Material;
        public float[] Normal;   // 3 per pixel
        public byte[] Albedo;    // 3 per pixel
        public byte[] Color;     // 3 per pixel (shaded)
        public bool[] Covered;

        public RenderBuffers(int w, int h)
        {
            Width = w;
            Height = h;
            Depth = new float[w * h];
            Part = new int[w * h];
            Material = new int[w * h];
            Normal = new float[w * h * 3];
            Albedo = new byte[w * h * 3];
            Color = new byte[w * h * 3];
            Covered = new bool[w * h];
            for (int i = 0; i < Depth.Length; i++)
            {
                Depth[i] = float.PositiveInfinity;
                Part[i] = -1;
                Material[i] = -1;
            }
        }
    }

    // A small software rasteriser for the cockpit pictures: z-buffered, perspective-correct texture coordinates and
    // normals, alpha-tested materials, no back-face culling (cockpit models are often single-sided). Bands of rows are
    // drawn in parallel.
    internal static class SoftwareRenderer
    {
        public sealed class Light
        {
            public Vec3 Direction = new Vec3(-0.3, -1, 0.2);   // towards which the light shines
            public double Ambient = 0.42;
            public double Diffuse = 0.62;
            public double Headlight = 0.18;
        }

        // Draws the meshes (all, or only those listed) into new buffers at the camera's size.
        public static RenderBuffers Render(CockpitScene scene, RenderCamera cam, ICollection<int> only, Light light)
        {
            light = light ?? new Light();
            RenderBuffers b = new RenderBuffers(cam.Width, cam.Height);
            List<int> meshes = new List<int>();
            HashSet<int> onlySet = only == null ? null : new HashSet<int>(only);
            for (int i = 0; i < scene.Meshes.Count; i++)
            {
                if (onlySet == null || onlySet.Contains(i))
                    meshes.Add(i);
            }
            // View-space positions per mesh.
            float[][] view = new float[scene.Meshes.Count][];
            Parallel.ForEach(meshes, i =>
            {
                float[] p = scene.Meshes[i].Positions;
                float[] v = new float[p.Length];
                for (int k = 0; k + 2 < p.Length; k += 3)
                {
                    Vec3 d = new Vec3(p[k] - cam.Eye.X, p[k + 1] - cam.Eye.Y, p[k + 2] - cam.Eye.Z);
                    v[k] = (float)Vec3.Dot(d, cam.Right);
                    v[k + 1] = (float)Vec3.Dot(d, cam.Up);
                    v[k + 2] = (float)Vec3.Dot(d, cam.Forward);
                }
                view[i] = v;
            });
            int bands = Math.Max(1, Math.Min(64, Environment.ProcessorCount * 2));
            int bandH = (cam.Height + bands - 1) / bands;
            Parallel.For(0, bands, band =>
            {
                int y0 = band * bandH, y1 = Math.Min(cam.Height, y0 + bandH);
                if (y0 >= y1)
                    return;
                Raster r = new Raster(scene, cam, b, y0, y1);
                foreach (int i in meshes)
                    r.Mesh(i, view[i]);
            });
            Shade(scene, cam, b, light);
            return b;
        }

        private static void Shade(CockpitScene scene, RenderCamera cam, RenderBuffers b, Light light)
        {
            Vec3 l = (light.Direction * -1).Normalized;
            Parallel.For(0, b.Height, y =>
            {
                for (int x = 0; x < b.Width; x++)
                {
                    int i = y * b.Width + x;
                    if (!b.Covered[i])
                        continue;
                    Vec3 n = new Vec3(b.Normal[3 * i], b.Normal[3 * i + 1], b.Normal[3 * i + 2]);
                    Vec3 viewDir = ViewRay(cam, x, y, b.Width, b.Height);
                    // Two-sided: the side facing the camera.
                    if (Vec3.Dot(n, viewDir) > 0)
                        n = n * -1;
                    double diff = Math.Max(0, Vec3.Dot(n, l));
                    double head = Math.Max(0, -Vec3.Dot(n, viewDir));
                    double k = light.Ambient + light.Diffuse * diff + light.Headlight * head;
                    for (int c = 0; c < 3; c++)
                        b.Color[3 * i + c] = (byte)Math.Max(0, Math.Min(255, b.Albedo[3 * i + c] * k));
                }
            });
        }

        public static Vec3 ViewRay(RenderCamera cam, double x, double y, int w, int h)
        {
            if (cam.Ortho)
                return cam.Forward;
            double f = h / 2.0 / Math.Tan(cam.FovY * Math.PI / 360.0);
            return (cam.Forward * f + cam.Right * (x - w / 2.0) - cam.Up * (y - h / 2.0)).Normalized;
        }

        private sealed class Raster
        {
            private readonly CockpitScene scene;
            private readonly RenderCamera cam;
            private readonly RenderBuffers b;
            private readonly int y0, y1;
            private readonly double focal, orthoScale;
            private readonly double[] cx = new double[9], cy = new double[9], cz = new double[9];
            private readonly double[,] attr = new double[9, 5];

            public Raster(CockpitScene scene, RenderCamera cam, RenderBuffers b, int y0, int y1)
            {
                this.scene = scene;
                this.cam = cam;
                this.b = b;
                this.y0 = y0;
                this.y1 = y1;
                focal = cam.Focal;
                orthoScale = cam.Height / 2.0 / cam.HalfHeight;
            }

            public void Mesh(int index, float[] v)
            {
                SceneMesh m = scene.Meshes[index];
                SceneMaterial mat = m.Material >= 0 && m.Material < scene.Materials.Count ? scene.Materials[m.Material] : null;
                int[] idx = m.Indices;
                float[] nor = m.Normals, uv = m.Uvs, pos = m.Positions;
                double near = cam.Near;
                double[] vx = new double[3], vy = new double[3], vz = new double[3];
                double[,] a = new double[3, 5];
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    bool behind = false, allBehind = true;
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = idx[t + k];
                        vx[k] = v[3 * vi];
                        vy[k] = v[3 * vi + 1];
                        vz[k] = v[3 * vi + 2];
                        if (vz[k] < near)
                            behind = true;
                        else
                            allBehind = false;
                    }
                    if (allBehind)
                        continue;
                    // Quick reject on the band (projected y range) for triangles in front.
                    if (!behind)
                    {
                        double minY = double.MaxValue, maxY = double.MinValue;
                        for (int k = 0; k < 3; k++)
                        {
                            double sy = cam.Ortho ? cam.Height / 2.0 - vy[k] * orthoScale : cam.Height / 2.0 - vy[k] / vz[k] * focal;
                            if (sy < minY) minY = sy;
                            if (sy > maxY) maxY = sy;
                        }
                        if (maxY < y0 - 1 || minY > y1 + 1)
                            continue;
                    }
                    // Face normal (world) for meshes without normals.
                    Vec3 fn = new Vec3(0, 1, 0);
                    if (nor == null)
                    {
                        int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
                        Vec3 p0 = new Vec3(pos[3 * i0], pos[3 * i0 + 1], pos[3 * i0 + 2]);
                        Vec3 p1 = new Vec3(pos[3 * i1], pos[3 * i1 + 1], pos[3 * i1 + 2]);
                        Vec3 p2 = new Vec3(pos[3 * i2], pos[3 * i2 + 1], pos[3 * i2 + 2]);
                        fn = Vec3.Cross(p1 - p0, p2 - p0).Normalized;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = idx[t + k];
                        a[k, 0] = uv != null ? uv[2 * vi] : 0;
                        a[k, 1] = uv != null ? uv[2 * vi + 1] : 0;
                        a[k, 2] = nor != null ? nor[3 * vi] : fn.X;
                        a[k, 3] = nor != null ? nor[3 * vi + 1] : fn.Y;
                        a[k, 4] = nor != null ? nor[3 * vi + 2] : fn.Z;
                    }
                    int count = Clip(vx, vy, vz, a, behind);
                    for (int k = 1; k + 1 < count; k++)
                        Triangle(0, k, k + 1, m, mat);
                }
            }

            // Copies the triangle into the clip arrays, clipped at the near plane when needed; returns the corner count.
            private int Clip(double[] vx, double[] vy, double[] vz, double[,] a, bool clip)
            {
                if (!clip)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        cx[k] = vx[k];
                        cy[k] = vy[k];
                        cz[k] = vz[k];
                        for (int j = 0; j < 5; j++)
                            attr[k, j] = a[k, j];
                    }
                    return 3;
                }
                int n = 0;
                double near = cam.Near;
                for (int k = 0; k < 3; k++)
                {
                    int k2 = (k + 1) % 3;
                    bool inA = vz[k] >= near, inB = vz[k2] >= near;
                    if (inA)
                    {
                        cx[n] = vx[k];
                        cy[n] = vy[k];
                        cz[n] = vz[k];
                        for (int j = 0; j < 5; j++)
                            attr[n, j] = a[k, j];
                        n++;
                    }
                    if (inA != inB)
                    {
                        double s = (near - vz[k]) / (vz[k2] - vz[k]);
                        cx[n] = vx[k] + (vx[k2] - vx[k]) * s;
                        cy[n] = vy[k] + (vy[k2] - vy[k]) * s;
                        cz[n] = near;
                        for (int j = 0; j < 5; j++)
                            attr[n, j] = a[k, j] + (a[k2, j] - a[k, j]) * s;
                        n++;
                    }
                }
                return n;
            }

            private void Triangle(int i0, int i1, int i2, SceneMesh m, SceneMaterial mat)
            {
                int w = cam.Width, h = cam.Height;
                double sx0, sy0, sx1, sy1, sx2, sy2, iz0, iz1, iz2;
                ToScreen(i0, out sx0, out sy0, out iz0);
                ToScreen(i1, out sx1, out sy1, out iz1);
                ToScreen(i2, out sx2, out sy2, out iz2);
                double area = (sx1 - sx0) * (sy2 - sy0) - (sx2 - sx0) * (sy1 - sy0);
                if (Math.Abs(area) < 1e-9)
                    return;
                int minX = Math.Max(0, (int)Math.Floor(Math.Min(sx0, Math.Min(sx1, sx2))));
                int maxX = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(sx0, Math.Max(sx1, sx2))));
                int minY = Math.Max(y0, (int)Math.Floor(Math.Min(sy0, Math.Min(sy1, sy2))));
                int maxY = Math.Min(y1 - 1, (int)Math.Ceiling(Math.Max(sy0, Math.Max(sy1, sy2))));
                if (minX > maxX || minY > maxY)
                    return;
                double inv = 1.0 / area;
                TextureImage tex = mat != null ? mat.Albedo : null;
                byte[] flat = mat != null ? mat.Color : new byte[] { 150, 150, 150 };
                bool alphaTest = mat != null && mat.AlphaTest && tex != null;
                for (int y = minY; y <= maxY; y++)
                {
                    double py = y + 0.5;
                    for (int x = minX; x <= maxX; x++)
                    {
                        double px = x + 0.5;
                        double w0 = ((sx1 - px) * (sy2 - py) - (sx2 - px) * (sy1 - py)) * inv;
                        double w1 = ((sx2 - px) * (sy0 - py) - (sx0 - px) * (sy2 - py)) * inv;
                        double w2 = 1 - w0 - w1;
                        if (w0 < -1e-7 || w1 < -1e-7 || w2 < -1e-7)
                            continue;
                        double iz = w0 * iz0 + w1 * iz1 + w2 * iz2;
                        if (iz <= 0)
                            continue;
                        float depth = (float)(cam.Ortho ? iz : 1.0 / iz);
                        if (cam.Ortho)
                            depth = (float)(w0 * cz[i0] + w1 * cz[i1] + w2 * cz[i2]);
                        int pi = y * w + x;
                        if (depth >= b.Depth[pi])
                            continue;
                        // Perspective-correct weights.
                        double q0, q1, q2;
                        if (cam.Ortho)
                        {
                            q0 = w0;
                            q1 = w1;
                            q2 = w2;
                        }
                        else
                        {
                            double z = 1.0 / iz;
                            q0 = w0 * iz0 * z;
                            q1 = w1 * iz1 * z;
                            q2 = w2 * iz2 * z;
                        }
                        int r, g, bl, al = 255;
                        if (tex != null)
                        {
                            double u = q0 * attr[i0, 0] + q1 * attr[i1, 0] + q2 * attr[i2, 0];
                            double v = q0 * attr[i0, 1] + q1 * attr[i1, 1] + q2 * attr[i2, 1];
                            tex.Sample((float)u, (float)v, out r, out g, out bl, out al);
                            if (alphaTest && al < 110)
                                continue;
                        }
                        else
                        {
                            r = flat[0];
                            g = flat[1];
                            bl = flat[2];
                        }
                        b.Depth[pi] = depth;
                        b.Part[pi] = m.Part;
                        b.Material[pi] = m.Material;
                        b.Covered[pi] = true;
                        double nx = q0 * attr[i0, 2] + q1 * attr[i1, 2] + q2 * attr[i2, 2];
                        double ny = q0 * attr[i0, 3] + q1 * attr[i1, 3] + q2 * attr[i2, 3];
                        double nz = q0 * attr[i0, 4] + q1 * attr[i1, 4] + q2 * attr[i2, 4];
                        double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (nl > 1e-9)
                        {
                            nx /= nl;
                            ny /= nl;
                            nz /= nl;
                        }
                        b.Normal[3 * pi] = (float)nx;
                        b.Normal[3 * pi + 1] = (float)ny;
                        b.Normal[3 * pi + 2] = (float)nz;
                        b.Albedo[3 * pi] = (byte)r;
                        b.Albedo[3 * pi + 1] = (byte)g;
                        b.Albedo[3 * pi + 2] = (byte)bl;
                    }
                }
            }

            private void ToScreen(int i, out double sx, out double sy, out double iz)
            {
                if (cam.Ortho)
                {
                    sx = cam.Width / 2.0 + cx[i] * orthoScale;
                    sy = cam.Height / 2.0 - cy[i] * orthoScale;
                    iz = 1;
                    return;
                }
                double z = Math.Max(cam.Near, cz[i]);
                sx = cam.Width / 2.0 + cx[i] / z * focal;
                sy = cam.Height / 2.0 - cy[i] / z * focal;
                iz = 1.0 / z;
            }
        }

        // Halves the picture (2x2 box filter) of an RGB buffer.
        public static byte[] Downsample(byte[] rgb, int w, int h, int factor, out int ow, out int oh)
        {
            ow = w / factor;
            oh = h / factor;
            byte[] o = new byte[ow * oh * 3];
            int ww = ow, f = factor;
            Parallel.For(0, oh, y =>
            {
                for (int x = 0; x < ww; x++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int s = 0;
                        for (int dy = 0; dy < f; dy++)
                            for (int dx = 0; dx < f; dx++)
                                s += rgb[((y * f + dy) * w + x * f + dx) * 3 + c];
                        o[(y * ww + x) * 3 + c] = (byte)(s / (f * f));
                    }
                }
            });
            return o;
        }
    }
}
