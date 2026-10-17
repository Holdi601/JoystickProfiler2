#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JoyPro.CockpitPictures
{
    // Turns a render into JoyPro's line drawing ("joypro-device-drawing", docs/device-reference.md): the outlines
    // (silhouettes, steps in depth), the details (edges between parts, creases) and the fine lines (printed labels and
    // markings of the textures) as vector paths, and for the cel-shaded style a few flat colour regions under them.
    // The drawing is painted in the user's drawing style like every other JoyPro drawing.
    internal static class LineArtTracer
    {
        public sealed class Result
        {
            public double Width;
            public double Height;
            public string Outline = string.Empty;
            public string Detail = string.Empty;
            public string Minor = string.Empty;
            public List<KeyValuePair<string, string>> Fills = new List<KeyValuePair<string, string>>();
        }

        public sealed class Settings
        {
            // Relative depth step that makes an outline (perspective) or absolute metres (orthographic).
            public double DepthStep = 0.025;
            public double OrthoDepthStep = 0.012;
            public double CreaseDegrees = 38;
            public int LabelContrast = 48;
            public bool Labels = true;
            public int PaletteSize = 6;
        }

        private const byte None = 0, MinorRole = 1, DetailRole = 2, OutlineRole = 3;

        // b: buffers at "factor" times the drawing's size.
        public static Result Trace(RenderBuffers b, bool ortho, int factor, Settings s)
        {
            s = s ?? new Settings();
            int w = b.Width, h = b.Height;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            bool profile = Environment.GetEnvironmentVariable("JP_PROFILE") == "1";
            byte[] role = Classify(b, ortho, s);
            if (profile) Console.WriteLine("  classify " + sw.ElapsedMilliseconds);
            byte[] skeleton = Thin(role, w, h);
            if (profile) Console.WriteLine("  thin " + sw.ElapsedMilliseconds);
            Result r = new Result { Width = w / (double)factor, Height = h / (double)factor };
            StringBuilder outline = new StringBuilder(), detail = new StringBuilder(), minor = new StringBuilder();
            List<List<int>> edgeChains = Chains(skeleton, w, h);
            if (profile) Console.WriteLine("  chains " + sw.ElapsedMilliseconds + " n=" + edgeChains.Count);
            foreach (List<int> chain in edgeChains)
            {
                // The chain's role: the strongest role most of its pixels carry.
                int[] count = new int[4];
                foreach (int p in chain)
                    count[role[p]]++;
                byte cr = count[OutlineRole] * 3 >= chain.Count ? OutlineRole : DetailRole;
                int min = cr == OutlineRole ? 5 : 7;
                if (chain.Count < min * factor / 2)
                    continue;
                Emit(cr == OutlineRole ? outline : detail, chain, w, factor, 0.45);
            }
            // Printed labels and markings: the strokes themselves (bright print on darker paint), as centre lines.
            if (s.Labels)
            {
                if (profile) Console.WriteLine("  emitted " + sw.ElapsedMilliseconds);
                byte[] print = Print(b, s);
                if (profile) Console.WriteLine("  print " + sw.ElapsedMilliseconds);
                byte[] strokes = Thin(print, w, h);
                foreach (List<int> chain in Chains(strokes, w, h))
                {
                    if (chain.Count < 3 * factor)
                        continue;
                    Emit(minor, chain, w, factor, 0.35);
                }
            }
            if (profile) Console.WriteLine("  chains+labels " + sw.ElapsedMilliseconds);
            r.Outline = outline.ToString().Trim();
            r.Detail = detail.ToString().Trim();
            r.Minor = minor.ToString().Trim();
            r.Fills = Fills(b, factor, s);
            if (profile) Console.WriteLine("  fills " + sw.ElapsedMilliseconds);
            return r;
        }

        private static void Emit(StringBuilder sb, List<int> chain, int w, int factor, double tol)
        {
            List<double[]> pts = chain.Select(p => new[] { (p % w + 0.5) / factor, (p / w + 0.5) / factor }).ToList();
            pts = Simplify(pts, tol);
            if (pts.Count < 2)
                return;
            AppendSmooth(sb, pts, chain.Count > 3 && chain[0] == chain[chain.Count - 1]);
        }

        // Pixels of printed labels: clearly brighter than the paint around them (9x9 mean), on the same part.
        private static byte[] Print(RenderBuffers b, Settings s)
        {
            int w = b.Width, h = b.Height;
            int[] lum = new int[w * h];
            for (int i = 0; i < lum.Length; i++)
                lum[i] = b.Covered[i] ? (b.Albedo[3 * i] * 54 + b.Albedo[3 * i + 1] * 183 + b.Albedo[3 * i + 2] * 19) >> 8 : 0;
            // Integral images of the luminance and the coverage.
            long[] sum = new long[(w + 1) * (h + 1)];
            int[] cnt = new int[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                long row = 0;
                int rc = 0;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    row += lum[i];
                    rc += b.Covered[i] ? 1 : 0;
                    sum[(y + 1) * (w + 1) + x + 1] = sum[y * (w + 1) + x + 1] + row;
                    cnt[(y + 1) * (w + 1) + x + 1] = cnt[y * (w + 1) + x + 1] + rc;
                }
            }
            byte[] m = new byte[w * h];
            int rad = 16;
            Parallel.For(0, h, y =>
            {
                int y0 = Math.Max(0, y - rad), y1 = Math.Min(h, y + rad + 1);
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!b.Covered[i] || lum[i] < 105)
                        continue;
                    int x0 = Math.Max(0, x - rad), x1 = Math.Min(w, x + rad + 1);
                    long su = sum[y1 * (w + 1) + x1] - sum[y0 * (w + 1) + x1] - sum[y1 * (w + 1) + x0] + sum[y0 * (w + 1) + x0];
                    int c = cnt[y1 * (w + 1) + x1] - cnt[y0 * (w + 1) + x1] - cnt[y1 * (w + 1) + x0] + cnt[y0 * (w + 1) + x0];
                    if (c < 200)
                        continue;
                    double mean = su / (double)c;
                    if (lum[i] - mean > s.LabelContrast && mean < 150)
                        m[i] = 1;
                }
            });
            return m;
        }

        // ------------------------------------------------------------------ edge pixels

        private static byte[] Classify(RenderBuffers b, bool ortho, Settings s)
        {
            int w = b.Width, h = b.Height;
            byte[] role = new byte[w * h];
            double cosCrease = Math.Cos(s.CreaseDegrees * Math.PI / 180);
            int[] lum = new int[w * h];
            for (int i = 0; i < lum.Length; i++)
                lum[i] = (b.Albedo[3 * i] * 54 + b.Albedo[3 * i + 1] * 183 + b.Albedo[3 * i + 2] * 19) >> 8;
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    // right and bottom neighbours
                    for (int k = 0; k < 2; k++)
                    {
                        int nx = x + (k == 0 ? 1 : 0), ny = y + (k == 1 ? 1 : 0);
                        if (nx >= w || ny >= h)
                            continue;
                        int j = ny * w + nx;
                        bool ci = b.Covered[i], cj = b.Covered[j];
                        if (!ci && !cj)
                            continue;
                        if (ci != cj)
                        {
                            Mark(role, ci ? i : j, OutlineRole);
                            continue;
                        }
                        float di = b.Depth[i], dj = b.Depth[j];
                        int near = di <= dj ? i : j;
                        double step = Math.Abs(di - dj);
                        if (ortho ? step > s.OrthoDepthStep : step > s.DepthStep * Math.Min(di, dj))
                        {
                            Mark(role, near, OutlineRole);
                            continue;
                        }
                        if (b.Part[i] != b.Part[j])
                        {
                            Mark(role, near, DetailRole);
                            continue;
                        }
                        double dot = b.Normal[3 * i] * b.Normal[3 * j] + b.Normal[3 * i + 1] * b.Normal[3 * j + 1] + b.Normal[3 * i + 2] * b.Normal[3 * j + 2];
                        if (Math.Abs(dot) < cosCrease)
                        {
                            Mark(role, near, DetailRole);
                            continue;
                        }
                    }
                }
            });
            return role;
        }

        private static void Mark(byte[] role, int i, byte r)
        {
            if (role[i] < r)
                role[i] = r;
        }

        // Zhang-Suen thinning to one pixel wide lines; the result carries 1 for line pixels.
        private static byte[] Thin(byte[] role, int w, int h)
        {
            byte[] m = new byte[w * h];
            for (int i = 0; i < m.Length; i++)
                m[i] = role[i] != None ? (byte)1 : (byte)0;
            List<int> remove = new List<int>();
            bool changed = true;
            int guard = 0;
            while (changed && guard++ < 20)
            {
                changed = false;
                for (int pass = 0; pass < 2; pass++)
                {
                    remove.Clear();
                    for (int y = 1; y < h - 1; y++)
                    {
                        for (int x = 1; x < w - 1; x++)
                        {
                            int i = y * w + x;
                            if (m[i] == 0)
                                continue;
                            int p2 = m[i - w], p3 = m[i - w + 1], p4 = m[i + 1], p5 = m[i + w + 1], p6 = m[i + w], p7 = m[i + w - 1], p8 = m[i - 1], p9 = m[i - w - 1];
                            int bn = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                            if (bn < 2 || bn > 6)
                                continue;
                            int a = (p2 == 0 && p3 == 1 ? 1 : 0) + (p3 == 0 && p4 == 1 ? 1 : 0) + (p4 == 0 && p5 == 1 ? 1 : 0) + (p5 == 0 && p6 == 1 ? 1 : 0) +
                                    (p6 == 0 && p7 == 1 ? 1 : 0) + (p7 == 0 && p8 == 1 ? 1 : 0) + (p8 == 0 && p9 == 1 ? 1 : 0) + (p9 == 0 && p2 == 1 ? 1 : 0);
                            if (a != 1)
                                continue;
                            if (pass == 0 ? (p2 * p4 * p6 == 0 && p4 * p6 * p8 == 0) : (p2 * p4 * p8 == 0 && p2 * p6 * p8 == 0))
                                remove.Add(i);
                        }
                    }
                    foreach (int i in remove)
                        m[i] = 0;
                    if (remove.Count > 0)
                        changed = true;
                }
            }
            return m;
        }

        private static readonly int[] DX = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] DY = { 0, 1, 1, 1, 0, -1, -1, -1 };

        // Follows the one-pixel lines from end to end (and around closed loops).
        private static List<List<int>> Chains(byte[] m, int w, int h)
        {
            int[] degree = new int[w * h];
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    if (m[i] == 0)
                        continue;
                    int d = 0;
                    for (int k = 0; k < 8; k++)
                        d += m[i + DY[k] * w + DX[k]];
                    degree[i] = d;
                }
            }
            // The steps already walked: a bit per pixel and direction (both ends of a step are marked).
            byte[] used = new byte[w * h];
            List<List<int>> chains = new List<List<int>>();
            bool[] visited = new bool[w * h];
            // From the ends and junctions first, then the closed loops that are left.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < m.Length; i++)
                {
                    if (m[i] == 0 || (pass == 0 ? degree[i] == 2 : visited[i] || degree[i] != 2))
                        continue;
                    int x = i % w, y = i / w;
                    if (x == 0 || y == 0 || x == w - 1 || y == h - 1)
                        continue;
                    for (int k = 0; k < 8; k++)
                    {
                        int j = i + DY[k] * w + DX[k];
                        if (m[j] == 0 || (used[i] & (1 << k)) != 0)
                            continue;
                        List<int> chain = Walk(m, degree, w, h, i, k, used, visited);
                        if (chain.Count >= 2)
                            chains.Add(chain);
                        if (pass == 1)
                            break;
                    }
                    visited[i] = true;
                }
            }
            return chains;
        }

        private static void Use(byte[] used, int w, int a, int k)
        {
            used[a] |= (byte)(1 << k);
            used[a + DY[k] * w + DX[k]] |= (byte)(1 << ((k + 4) & 7));
        }

        private static List<int> Walk(byte[] m, int[] degree, int w, int h, int start, int dir, byte[] used, bool[] visited)
        {
            List<int> chain = new List<int> { start };
            int prev = start, cur = start + DY[dir] * w + DX[dir];
            Use(used, w, start, dir);
            while (true)
            {
                chain.Add(cur);
                if (degree[cur] != 2 || cur == start)
                    break;
                visited[cur] = true;
                int x = cur % w, y = cur / w;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1)
                    break;
                int found = -1;
                // Prefer the 4-neighbours (straighter), then the diagonals.
                for (int pass = 0; pass < 2 && found < 0; pass++)
                {
                    for (int k = pass; k < 8; k += 2)
                    {
                        int j = cur + DY[k] * w + DX[k];
                        if (m[j] == 0 || j == prev || (used[cur] & (1 << k)) != 0)
                            continue;
                        found = k;
                        break;
                    }
                }
                if (found < 0)
                    break;
                Use(used, w, cur, found);
                prev = cur;
                cur = cur + DY[found] * w + DX[found];
                if (chain.Count > 200000)
                    break;
            }
            return chain;
        }

        // Douglas-Peucker.
        public static List<double[]> Simplify(List<double[]> pts, double tol)
        {
            if (pts.Count <= 2)
                return pts;
            bool[] keep = new bool[pts.Count];
            keep[0] = keep[pts.Count - 1] = true;
            Stack<int[]> stack = new Stack<int[]>();
            stack.Push(new[] { 0, pts.Count - 1 });
            while (stack.Count > 0)
            {
                int[] seg = stack.Pop();
                int a = seg[0], bIdx = seg[1];
                double best = -1;
                int bi = -1;
                double ax = pts[a][0], ay = pts[a][1], bx = pts[bIdx][0], by = pts[bIdx][1];
                double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
                for (int i = a + 1; i < bIdx; i++)
                {
                    double d = len < 1e-9 ? Math.Sqrt((pts[i][0] - ax) * (pts[i][0] - ax) + (pts[i][1] - ay) * (pts[i][1] - ay))
                        : Math.Abs(dy * pts[i][0] - dx * pts[i][1] + bx * ay - by * ax) / len;
                    if (d > best)
                    {
                        best = d;
                        bi = i;
                    }
                }
                if (best > tol && bi > 0)
                {
                    keep[bi] = true;
                    stack.Push(new[] { a, bi });
                    stack.Push(new[] { bi, bIdx });
                }
            }
            List<double[]> o = new List<double[]>();
            for (int i = 0; i < pts.Count; i++)
                if (keep[i])
                    o.Add(pts[i]);
            return o;
        }

        // A polyline as a smooth path: straight for two points, else quadratic curves through the midpoints.
        private static void AppendSmooth(StringBuilder sb, List<double[]> p, bool closed)
        {
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append('M').Append(N(p[0][0])).Append(',').Append(N(p[0][1]));
            if (p.Count == 2)
            {
                sb.Append(" L").Append(N(p[1][0])).Append(',').Append(N(p[1][1]));
                return;
            }
            for (int i = 1; i < p.Count - 1; i++)
            {
                // Keep sharp corners sharp.
                double ax = p[i][0] - p[i - 1][0], ay = p[i][1] - p[i - 1][1], bx = p[i + 1][0] - p[i][0], by = p[i + 1][1] - p[i][1];
                double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
                double cos = la * lb > 1e-9 ? (ax * bx + ay * by) / (la * lb) : 1;
                double mx = (p[i][0] + p[i + 1][0]) / 2, my = (p[i][1] + p[i + 1][1]) / 2;
                if (cos < 0.5)
                {
                    sb.Append(" L").Append(N(p[i][0])).Append(',').Append(N(p[i][1]));
                    continue;
                }
                sb.Append(" Q").Append(N(p[i][0])).Append(',').Append(N(p[i][1])).Append(' ').Append(N(mx)).Append(',').Append(N(my));
            }
            sb.Append(" L").Append(N(p[p.Count - 1][0])).Append(',').Append(N(p[p.Count - 1][1]));
            if (closed)
                sb.Append(" Z");
        }

        private static string N(double v)
        {
            return Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ cel fills

        private static List<KeyValuePair<string, string>> Fills(RenderBuffers b, int factor, Settings s)
        {
            int w = b.Width / factor, h = b.Height / factor;
            // The shaded colour at the drawing's size, posterised: a few tones per hue.
            int[] label = new int[w * h];
            byte[] rgb = new byte[w * h * 3];
            bool[] covered = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int cov = 0, r = 0, g = 0, bl = 0;
                    for (int dy = 0; dy < factor; dy++)
                    {
                        for (int dx = 0; dx < factor; dx++)
                        {
                            int i = (y * factor + dy) * b.Width + x * factor + dx;
                            if (!b.Covered[i])
                                continue;
                            cov++;
                            r += b.Color[3 * i];
                            g += b.Color[3 * i + 1];
                            bl += b.Color[3 * i + 2];
                        }
                    }
                    int o = y * w + x;
                    covered[o] = cov * 2 >= factor * factor;
                    if (cov > 0)
                    {
                        rgb[3 * o] = (byte)(r / cov);
                        rgb[3 * o + 1] = (byte)(g / cov);
                        rgb[3 * o + 2] = (byte)(bl / cov);
                    }
                }
            }
            List<int[]> palette = Palette(rgb, covered, s.PaletteSize);
            if (palette.Count == 0)
                return new List<KeyValuePair<string, string>>();
            for (int i = 0; i < w * h; i++)
            {
                if (!covered[i])
                {
                    label[i] = -1;
                    continue;
                }
                int best = 0;
                double bd = double.MaxValue;
                for (int k = 0; k < palette.Count; k++)
                {
                    double d = Sq(rgb[3 * i] - palette[k][0]) + Sq(rgb[3 * i + 1] - palette[k][1]) + Sq(rgb[3 * i + 2] - palette[k][2]);
                    if (d < bd)
                    {
                        bd = d;
                        best = k;
                    }
                }
                label[i] = best;
            }
            // Clean small specks (majority of the 3x3 neighbourhood), twice.
            for (int pass = 0; pass < 4; pass++)
                label = Majority(label, w, h, palette.Count);
            List<KeyValuePair<string, string>> fills = new List<KeyValuePair<string, string>>();
            // Darkest first so lighter regions read on top (they never overlap anyway).
            foreach (int k in Enumerable.Range(0, palette.Count).OrderBy(k => palette[k][0] + palette[k][1] + palette[k][2]))
            {
                string d = Contours(label, w, h, k);
                if (d.Length == 0)
                    continue;
                fills.Add(new KeyValuePair<string, string>(string.Format("#{0:X2}{1:X2}{2:X2}", palette[k][0], palette[k][1], palette[k][2]), d));
            }
            return fills;
        }

        private static double Sq(double v)
        {
            return v * v;
        }

        // k-means on a sample of the covered pixels; returns the colours, lifted a little towards flat poster tones.
        private static List<int[]> Palette(byte[] rgb, bool[] covered, int k)
        {
            List<int> sample = new List<int>();
            int n = covered.Length;
            int step = Math.Max(1, n / 40000);
            for (int i = 0; i < n; i += step)
                if (covered[i])
                    sample.Add(i);
            if (sample.Count == 0)
                return new List<int[]>();
            k = Math.Min(k, sample.Count);
            // Start from luminance quantiles.
            List<int> byLum = sample.OrderBy(i => rgb[3 * i] * 3 + rgb[3 * i + 1] * 6 + rgb[3 * i + 2]).ToList();
            double[][] c = new double[k][];
            for (int j = 0; j < k; j++)
            {
                int i = byLum[(int)((j + 0.5) / k * (byLum.Count - 1))];
                c[j] = new double[] { rgb[3 * i], rgb[3 * i + 1], rgb[3 * i + 2] };
            }
            int[] assign = new int[sample.Count];
            for (int iter = 0; iter < 12; iter++)
            {
                double[][] sum = new double[k][];
                int[] cnt = new int[k];
                for (int j = 0; j < k; j++)
                    sum[j] = new double[3];
                for (int si = 0; si < sample.Count; si++)
                {
                    int i = sample[si];
                    int best = 0;
                    double bd = double.MaxValue;
                    for (int j = 0; j < k; j++)
                    {
                        double d = Sq(rgb[3 * i] - c[j][0]) + Sq(rgb[3 * i + 1] - c[j][1]) + Sq(rgb[3 * i + 2] - c[j][2]);
                        if (d < bd)
                        {
                            bd = d;
                            best = j;
                        }
                    }
                    assign[si] = best;
                    cnt[best]++;
                    sum[best][0] += rgb[3 * i];
                    sum[best][1] += rgb[3 * i + 1];
                    sum[best][2] += rgb[3 * i + 2];
                }
                for (int j = 0; j < k; j++)
                {
                    if (cnt[j] > 0)
                        c[j] = new[] { sum[j][0] / cnt[j], sum[j][1] / cnt[j], sum[j][2] / cnt[j] };
                }
            }
            return c.Select(v => new[] { Clamp(v[0] * 1.08 + 6), Clamp(v[1] * 1.08 + 6), Clamp(v[2] * 1.08 + 6) }).ToList();
        }

        private static int Clamp(double v)
        {
            return (int)Math.Max(0, Math.Min(255, Math.Round(v)));
        }

        private static int[] Majority(int[] label, int w, int h, int k)
        {
            int[] o = (int[])label.Clone();
            Parallel.For(1, h - 1, y =>
            {
                int[] cnt = new int[k + 1];
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    if (label[i] < 0)
                        continue;
                    Array.Clear(cnt, 0, cnt.Length);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int v = label[i + dy * w + dx];
                            cnt[v < 0 ? k : v]++;
                        }
                    int best = label[i], bc = cnt[label[i]];
                    for (int j = 0; j < k; j++)
                    {
                        if (cnt[j] > bc)
                        {
                            bc = cnt[j];
                            best = j;
                        }
                    }
                    if (bc >= 5)
                        o[i] = best;
                }
            });
            return o;
        }

        // The outlines of all pixels with a label, as closed paths (even-odd: holes are cut out).
        private static string Contours(int[] label, int w, int h, int k)
        {
            // Directed boundary edges with the region on the left, on the pixel-corner grid (w + 1) x (h + 1).
            Dictionary<int, List<int>> next = new Dictionary<int, List<int>>();
            int vw = w + 1;
            Func<int, int, bool> inside = (x, y) => x >= 0 && y >= 0 && x < w && y < h && label[y * w + x] == k;
            int edges = 0;
            for (int y = 0; y <= h; y++)
            {
                for (int x = 0; x <= w; x++)
                {
                    // horizontal edge between (x,y)-(x+1,y): pixels (x,y-1) above and (x,y) below
                    if (x < w)
                    {
                        bool above = inside(x, y - 1), below = inside(x, y);
                        if (above != below)
                        {
                            // region below: walk right to left? Region on the left when walking: below => left is down when walking -x.
                            int a = y * vw + x, bb = y * vw + x + 1;
                            if (below)
                                Add(next, bb, a);
                            else
                                Add(next, a, bb);
                            edges++;
                        }
                    }
                    if (y < h)
                    {
                        bool left = inside(x - 1, y), right = inside(x, y);
                        if (left != right)
                        {
                            int a = y * vw + x, bb = (y + 1) * vw + x;
                            if (right)
                                Add(next, a, bb);
                            else
                                Add(next, bb, a);
                            edges++;
                        }
                    }
                }
            }
            if (edges == 0)
                return string.Empty;
            StringBuilder sb = new StringBuilder();
            List<int> starts = next.Keys.ToList();
            foreach (int s0 in starts)
            {
                List<int> outs;
                while (next.TryGetValue(s0, out outs) && outs.Count > 0)
                {
                    List<double[]> loop = new List<double[]>();
                    int cur = s0;
                    int guard = 0;
                    int prevDir = -1;
                    do
                    {
                        List<int> o;
                        if (!next.TryGetValue(cur, out o) || o.Count == 0)
                            break;
                        int pick = 0;
                        if (o.Count > 1 && prevDir >= 0)
                        {
                            // At a corner touching two regions: turn left first.
                            for (int t = 0; t < o.Count; t++)
                            {
                                int d = Dir(cur, o[t], vw);
                                if (d == (prevDir + 3) % 4)
                                {
                                    pick = t;
                                    break;
                                }
                            }
                        }
                        int nx = o[pick];
                        o.RemoveAt(pick);
                        prevDir = Dir(cur, nx, vw);
                        loop.Add(new double[] { cur % vw, cur / vw });
                        cur = nx;
                    }
                    while (cur != s0 && guard++ < 10000000);
                    if (loop.Count < 4)
                        continue;
                    if (Area(loop) < 24)
                        continue;
                    loop.Add(loop[0]);
                    List<double[]> simple = Simplify(loop, 0.9);
                    if (simple.Count < 4)
                        continue;
                    if (sb.Length > 0)
                        sb.Append(' ');
                    sb.Append('M').Append(N(simple[0][0])).Append(',').Append(N(simple[0][1]));
                    for (int i = 1; i < simple.Count - 1; i++)
                        sb.Append(" L").Append(N(simple[i][0])).Append(',').Append(N(simple[i][1]));
                    sb.Append(" Z");
                }
            }
            return sb.ToString();
        }

        private static void Add(Dictionary<int, List<int>> next, int a, int b)
        {
            List<int> l;
            if (!next.TryGetValue(a, out l))
                next[a] = l = new List<int>(1);
            l.Add(b);
        }

        // 0 right, 1 down, 2 left, 3 up.
        private static int Dir(int a, int b, int vw)
        {
            int d = b - a;
            if (d == 1)
                return 0;
            if (d == vw)
                return 1;
            if (d == -1)
                return 2;
            return 3;
        }

        private static double Area(List<double[]> loop)
        {
            double a = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                double[] p = loop[i], q = loop[(i + 1) % loop.Count];
                a += p[0] * q[1] - q[0] * p[1];
            }
            return Math.Abs(a / 2);
        }
    }
}
