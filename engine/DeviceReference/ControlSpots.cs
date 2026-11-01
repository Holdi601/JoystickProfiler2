#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.DeviceReference
{
    // Detected control spots on a device photo (docs/device-reference.md, "Detected control spots", experimental): where
    // the buttons, hats, switches and knobs of a photo probably are, offline and without any download. Classic image
    // processing finds candidate spots (bright and dark blobs at the size of a control, inside the device's silhouette),
    // and a tiny network shipped as numbers (DB\DeviceDetect\spot-scorer.json, about 10 000 weights, trained on JoyPro's
    // curated positions) scores each candidate from two small crops around it and a few measurements. The spots carry no
    // input: which button is which is not known from a photo (the pilot of 2026-10-31 put the right input on 3-7% of the
    // controls), so the Layout page offers them only as snap targets while the user places markers.
    public sealed class ControlSpot
    {
        // The centre (0..1 of the photo) and the spot's size (radius, 0..1 of the photo's diagonal).
        public double X;
        public double Y;
        public double Radius;
        // 0..1: how much the spot looks like a control.
        public double Score;
    }

    // The scorer's numbers (format joypro-spot-scorer): the work size, the blob scales, the crops and the network.
    public sealed class SpotScorer
    {
        public const string FormatName = "joypro-spot-scorer";
        public const string FileName = "spot-scorer.json";
        public const string FolderName = "DeviceDetect";

        public int Work = 640;
        public double[] Sigmas = { 0.004, 0.006, 0.009, 0.0135, 0.02 };
        public int Patch = 24;
        public double[] Fovs = { 0.045, 0.11 };
        public int MaxCandidates = 220;
        public float[] FeatureMean = new float[0];
        public float[] FeatureStd = new float[0];
        internal readonly List<Layer> Conv = new List<Layer>();
        internal readonly List<Layer> Fc = new List<Layer>();

        internal sealed class Layer
        {
            public int In;
            public int Out;
            public float[] W;
            public float[] B;
        }

        // DB\DeviceDetect next to a catalog folder (DB\Devices).
        public static string FolderOf(string catalogFolder)
        {
            string parent = string.IsNullOrWhiteSpace(catalogFolder) ? null : Path.GetDirectoryName(Path.GetFullPath(catalogFolder).TrimEnd('\\', '/'));
            return parent == null ? string.Empty : Path.Combine(parent, FolderName);
        }

        private static readonly object Gate = new object();
        private static string cachedFile;
        private static SpotScorer cached;

        // The shipped scorer of a catalog folder, read once; null when it is missing or broken.
        public static SpotScorer Cached(string catalogFolder)
        {
            string file = Path.Combine(FolderOf(catalogFolder), FileName);
            lock (Gate)
            {
                if (cached != null && string.Equals(cachedFile, file, StringComparison.OrdinalIgnoreCase))
                    return cached;
                try
                {
                    cached = File.Exists(file) ? Parse(File.ReadAllText(file, Encoding.UTF8)) : null;
                }
                catch (FormatException)
                {
                    cached = null;
                }
                cachedFile = file;
                return cached;
            }
        }

        public static SpotScorer Parse(string json)
        {
            Dictionary<string, object> root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null || !string.Equals(DeviceCatalog.Text(root, "format"), FormatName, StringComparison.Ordinal))
                throw new FormatException("not a JoyPro spot scorer (format '" + FormatName + "')");
            SpotScorer scorer = new SpotScorer
            {
                Work = (int)Number(root, "work", 640),
                Patch = (int)Number(root, "patch", 24),
                MaxCandidates = (int)Number(root, "maxCandidates", 220),
                Sigmas = Numbers(root, "sigmas").Select(v => (double)v).ToArray(),
                Fovs = Numbers(root, "fovs").Select(v => (double)v).ToArray(),
                FeatureMean = Numbers(root, "featureMean"),
                FeatureStd = Numbers(root, "featureStd")
            };
            foreach (Dictionary<string, object> layer in DeviceCatalog.Objects(root, "conv"))
                scorer.Conv.Add(ReadLayer(layer, 9));
            foreach (Dictionary<string, object> layer in DeviceCatalog.Objects(root, "fc"))
                scorer.Fc.Add(ReadLayer(layer, 1));
            if (scorer.Sigmas.Length < 3 || scorer.Fovs.Length == 0 || scorer.Conv.Count != 3 || scorer.Fc.Count != 2 ||
                scorer.Conv[0].In != 3 * scorer.Fovs.Length || scorer.FeatureMean.Length != Features || scorer.FeatureStd.Length != Features ||
                scorer.Fc[0].In != scorer.Conv[2].Out + Features || scorer.Fc[1].Out != 1 || scorer.Patch % 4 != 0)
                throw new FormatException("the spot scorer's layers do not fit together");
            return scorer;
        }

        // The measurements per candidate (feat.py FEATURES, the order of the network's input).
        public const int Features = 20;

        private static Layer ReadLayer(Dictionary<string, object> item, int kernel)
        {
            Layer layer = new Layer { In = (int)Number(item, "in", 0), Out = (int)Number(item, "out", 0), W = Numbers(item, "w"), B = Numbers(item, "b") };
            if (layer.In <= 0 || layer.Out <= 0 || layer.W.Length != layer.In * layer.Out * kernel || layer.B.Length != layer.Out)
                throw new FormatException("a spot scorer layer has the wrong number of weights");
            return layer;
        }

        private static double Number(Dictionary<string, object> item, string key, double fallback)
        {
            object value;
            return item.TryGetValue(key, out value) && value is double ? (double)value : fallback;
        }

        private static float[] Numbers(Dictionary<string, object> item, string key)
        {
            object value;
            if (!item.TryGetValue(key, out value) || !(value is List<object>))
                return new float[0];
            return ((List<object>)value).Select(v => v is double ? (float)(double)v : 0f).ToArray();
        }
    }

    public static class ControlSpotDetector
    {
        // The spots of a photo: BGRA bytes (alpha over white), best first, at least 2 % of the diagonal apart. maxSpots
        // is usually twice the model's part count.
        public static List<ControlSpot> Detect(byte[] bgra, int width, int height, int stride, SpotScorer scorer, int maxSpots)
        {
            if (bgra == null || scorer == null || width <= 0 || height <= 0)
                return new List<ControlSpot>();
            int w, h;
            float[] rgb = Resize(bgra, width, height, stride, scorer.Work, out w, out h);
            bool[] mask = Foreground(rgb, w, h);
            List<Candidate> candidates = Candidates(rgb, mask, w, h, scorer);
            double diag = Math.Sqrt((double)w * w + (double)h * h);
            foreach (Candidate c in candidates)
                c.Score = Score(scorer, Patches(rgb, w, h, c.X, c.Y, scorer), c.Features);
            List<Candidate> kept = new List<Candidate>();
            double sep = 0.02 * diag;
            foreach (Candidate c in candidates.OrderByDescending(c => c.Score))
            {
                if (kept.All(o => (c.X - o.X) * (c.X - o.X) + (c.Y - o.Y) * (c.Y - o.Y) > sep * sep))
                    kept.Add(c);
                if (kept.Count >= Math.Max(1, maxSpots))
                    break;
            }
            return kept.Select(c => new ControlSpot
            {
                X = (double)c.X / w,
                Y = (double)c.Y / h,
                Radius = scorer.Sigmas[c.Level] * 1.6,
                Score = 1.0 / (1.0 + Math.Exp(-c.Score))
            }).ToList();
        }

        private sealed class Candidate
        {
            public int X;
            public int Y;
            public int Level;
            public int Polarity;
            public float Value;
            public float[] Features;
            public double Score;
        }

        // ------------------------------------------------------------------ the picture at the work size

        // Area average to the work size (the longer side), alpha over white, RGB 0..1.
        internal static float[] Resize(byte[] bgra, int width, int height, int stride, int work, out int w, out int h)
        {
            double s = (double)work / Math.Max(width, height);
            w = Math.Max(1, (int)Math.Round(width * s, MidpointRounding.AwayFromZero));
            h = Math.Max(1, (int)Math.Round(height * s, MidpointRounding.AwayFromZero));
            float[] src = new float[width * height * 3];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * stride + x * 4;
                    float a = bgra[i + 3] / 255f;
                    int o = (y * width + x) * 3;
                    src[o] = (bgra[i + 2] / 255f) * a + (1 - a);
                    src[o + 1] = (bgra[i + 1] / 255f) * a + (1 - a);
                    src[o + 2] = (bgra[i] / 255f) * a + (1 - a);
                }
            }
            float[] rows = new float[w * height * 3];
            for (int y = 0; y < height; y++)
                BoxLine(src, y * width * 3, 3, width, rows, y * w * 3, 3, w);
            float[] dst = new float[w * h * 3];
            for (int x = 0; x < w; x++)
                BoxLine(rows, x * 3, w * 3, height, dst, x * 3, w * 3, h);
            // 8 bits per channel, as the scorer's training pictures.
            for (int i = 0; i < dst.Length; i++)
                dst[i] = (float)Math.Round(dst[i] * 255, MidpointRounding.AwayFromZero) / 255f;
            return dst;
        }

        // One line of an area-average resampling (3 channels).
        private static void BoxLine(float[] src, int srcStart, int srcStep, int srcLength, float[] dst, int dstStart, int dstStep, int dstLength)
        {
            double scale = (double)srcLength / dstLength;
            for (int i = 0; i < dstLength; i++)
            {
                double a = i * scale, b = Math.Min(srcLength, (i + 1) * scale);
                double r = 0, g = 0, bl = 0, total = 0;
                for (int j = (int)Math.Floor(a); j < b && j < srcLength; j++)
                {
                    double weight = Math.Min(b, j + 1) - Math.Max(a, j);
                    if (weight <= 0)
                        continue;
                    int k = srcStart + j * srcStep;
                    r += src[k] * weight;
                    g += src[k + 1] * weight;
                    bl += src[k + 2] * weight;
                    total += weight;
                }
                int o = dstStart + i * dstStep;
                if (total <= 0)
                {
                    int k = srcStart + Math.Min(srcLength - 1, (int)a) * srcStep;
                    dst[o] = src[k];
                    dst[o + 1] = src[k + 1];
                    dst[o + 2] = src[k + 2];
                }
                else
                {
                    dst[o] = (float)(r / total);
                    dst[o + 1] = (float)(g / total);
                    dst[o + 2] = (float)(bl / total);
                }
            }
        }

        // ------------------------------------------------------------------ the device's silhouette

        // The device against the photo's plain background: the border's median colour is the background; whatever
        // differs clearly from it, closed, without holes, in pieces of at least 1 % of the picture.
        internal static bool[] Foreground(float[] rgb, int w, int h)
        {
            List<int> border = new List<int>();
            for (int x = 0; x < w; x++)
                border.Add(x);
            for (int x = 0; x < w; x++)
                border.Add((h - 1) * w + x);
            for (int y = 0; y < h; y++)
                border.Add(y * w);
            for (int y = 0; y < h; y++)
                border.Add(y * w + w - 1);
            float[] bg = new float[3];
            for (int c = 0; c < 3; c++)
                bg[c] = Median(border.Select(i => rgb[i * 3 + c]).ToList());
            Func<int, float> distance = i =>
            {
                float dr = rgb[i * 3] - bg[0], dg = rgb[i * 3 + 1] - bg[1], db = rgb[i * 3 + 2] - bg[2];
                return (float)Math.Sqrt(dr * dr + dg * dg + db * db);
            };
            float spread = Median(border.Select(distance).ToList());
            float threshold = Math.Max(0.08f, 3 * spread);
            bool[] m = new bool[w * h];
            for (int i = 0; i < m.Length; i++)
                m[i] = distance(i) > threshold;
            for (int k = 0; k < 3; k++)
                m = Morph(m, w, h, dilate: true);
            for (int k = 0; k < 3; k++)
                m = Morph(m, w, h, dilate: false);
            FillHoles(m, w, h);
            int[] label = new int[w * h];
            List<int> sizes = new List<int> { 0 };
            for (int i = 0; i < m.Length; i++)
            {
                if (m[i] && label[i] == 0)
                    sizes.Add(Flood(m, label, w, h, i, sizes.Count, true));
            }
            if (sizes.Count == 1)
            {
                for (int i = 0; i < m.Length; i++)
                    m[i] = true;
                return m;
            }
            bool[] keep = new bool[sizes.Count];
            for (int n = 1; n < sizes.Count; n++)
                keep[n] = sizes[n] >= 0.01 * w * h;
            if (!keep.Any(k => k))
                keep[Enumerable.Range(1, sizes.Count - 1).OrderByDescending(n => sizes[n]).First()] = true;
            for (int i = 0; i < m.Length; i++)
                m[i] = keep[label[i]];
            return m;
        }

        private static float Median(List<float> values)
        {
            values.Sort();
            int n = values.Count;
            return n == 0 ? 0 : n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
        }

        // One step of a dilation or erosion with the 4-neighbour cross; outside the picture counts as background.
        private static bool[] Morph(bool[] m, int w, int h, bool dilate)
        {
            bool[] o = new bool[m.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    bool l = x > 0 && m[i - 1], r = x < w - 1 && m[i + 1], u = y > 0 && m[i - w], d = y < h - 1 && m[i + w];
                    o[i] = dilate ? m[i] || l || r || u || d : m[i] && l && r && u && d;
                }
            }
            return o;
        }

        // Background not reachable from the picture's edge is a hole of the device.
        private static void FillHoles(bool[] m, int w, int h)
        {
            bool[] inverse = m.Select(v => !v).ToArray();
            int[] label = new int[w * h];
            for (int x = 0; x < w; x++)
            {
                if (inverse[x] && label[x] == 0) Flood(inverse, label, w, h, x, 1, false);
                int b = (h - 1) * w + x;
                if (inverse[b] && label[b] == 0) Flood(inverse, label, w, h, b, 1, false);
            }
            for (int y = 0; y < h; y++)
            {
                int a = y * w, b = y * w + w - 1;
                if (inverse[a] && label[a] == 0) Flood(inverse, label, w, h, a, 1, false);
                if (inverse[b] && label[b] == 0) Flood(inverse, label, w, h, b, 1, false);
            }
            for (int i = 0; i < m.Length; i++)
            {
                if (inverse[i] && label[i] == 0)
                    m[i] = true;
            }
        }

        // 4-connected flood fill of the set pixels from start; returns the number of pixels.
        private static int Flood(bool[] m, int[] label, int w, int h, int start, int id, bool count)
        {
            Stack<int> stack = new Stack<int>();
            stack.Push(start);
            label[start] = id;
            int n = 0;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                n++;
                int x = i % w, y = i / w;
                if (x > 0 && m[i - 1] && label[i - 1] == 0) { label[i - 1] = id; stack.Push(i - 1); }
                if (x < w - 1 && m[i + 1] && label[i + 1] == 0) { label[i + 1] = id; stack.Push(i + 1); }
                if (y > 0 && m[i - w] && label[i - w] == 0) { label[i - w] = id; stack.Push(i - w); }
                if (y < h - 1 && m[i + w] && label[i + w] == 0) { label[i + w] = id; stack.Push(i + w); }
            }
            return count ? n : 0;
        }

        // ------------------------------------------------------------------ candidates and their measurements

        private static List<Candidate> Candidates(float[] rgb, bool[] mask, int w, int h, SpotScorer scorer)
        {
            double diag = Math.Sqrt((double)w * w + (double)h * h);
            int n = w * h;
            float[] gray = new float[n];
            for (int i = 0; i < n; i++)
                gray[i] = 0.299f * rgb[i * 3] + 0.587f * rgb[i * 3 + 1] + 0.114f * rgb[i * 3 + 2];
            double sum = 0, sum2 = 0;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                if (!mask[i])
                    continue;
                sum += gray[i];
                sum2 += (double)gray[i] * gray[i];
                count++;
            }
            double lumFg = count > 0 ? sum / count : 0;
            double gstd = Math.Max(0.03, count > 0 ? Math.Sqrt(Math.Max(0, sum2 / count - lumFg * lumFg)) : 0);
            double[] sig = scorer.Sigmas.Select(f => diag * f).ToArray();
            float[][] blurs = sig.Select(s => Gauss(gray, w, h, s)).ToArray();
            int levels = sig.Length - 1;
            float[][] dogs = new float[levels][];
            for (int k = 0; k < levels; k++)
            {
                dogs[k] = new float[n];
                for (int i = 0; i < n; i++)
                    dogs[k][i] = (float)((blurs[k][i] - blurs[k + 1][i]) / gstd);
            }
            List<Candidate> raw = new List<Candidate>();
            foreach (int pol in new[] { 1, -1 })
            {
                float[][] d = dogs.Select(a => pol == 1 ? a : a.Select(v => -v).ToArray()).ToArray();
                float[][] spatial = d.Select(a => MaxFilter(a, w, h, 2)).ToArray();
                for (int k = 0; k < levels; k++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        float v = d[k][i];
                        if (v <= 0.05f || !mask[i])
                            continue;
                        float best = spatial[k][i];
                        if (k > 0) best = Math.Max(best, spatial[k - 1][i]);
                        if (k < levels - 1) best = Math.Max(best, spatial[k + 1][i]);
                        if (v == best)
                            raw.Add(new Candidate { X = i % w, Y = i / w, Level = k, Polarity = pol, Value = v });
                    }
                }
            }
            List<Candidate> list = new List<Candidate>();
            double sep = 0.012 * diag;
            foreach (Candidate c in raw.OrderByDescending(c => c.Value))
            {
                if (list.All(o => (double)(c.X - o.X) * (c.X - o.X) + (double)(c.Y - o.Y) * (c.Y - o.Y) > sep * sep))
                    list.Add(c);
                if (list.Count >= scorer.MaxCandidates)
                    break;
            }

            float[] smooth = Gauss(gray, w, h, 1.0);
            float[] grad = new float[n];
            float[] sat = new float[n];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float gx = (At(smooth, w, h, x + 1, y - 1) - At(smooth, w, h, x - 1, y - 1)) + 2 * (At(smooth, w, h, x + 1, y) - At(smooth, w, h, x - 1, y)) +
                               (At(smooth, w, h, x + 1, y + 1) - At(smooth, w, h, x - 1, y + 1));
                    float gy = (At(smooth, w, h, x - 1, y + 1) - At(smooth, w, h, x - 1, y - 1)) + 2 * (At(smooth, w, h, x, y + 1) - At(smooth, w, h, x, y - 1)) +
                               (At(smooth, w, h, x + 1, y + 1) - At(smooth, w, h, x + 1, y - 1));
                    int i = y * w + x;
                    grad[i] = (float)Math.Sqrt(gx * gx + gy * gy);
                    float r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
                    sat[i] = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                }
            }
            Integral iGray = new Integral(gray, w, h), iGray2 = new Integral(gray.Select(v => v * v).ToArray(), w, h);
            Integral iGrad = new Integral(grad, w, h), iSat = new Integral(sat, w, h), iMask = new Integral(mask.Select(v => v ? 1f : 0f).ToArray(), w, h);
            int fx0 = w - 1, fx1 = 0, fy0 = h - 1, fy1 = 0;
            for (int i = 0; i < n; i++)
            {
                if (!mask[i])
                    continue;
                int x = i % w, y = i / w;
                fx0 = Math.Min(fx0, x); fx1 = Math.Max(fx1, x); fy0 = Math.Min(fy0, y); fy1 = Math.Max(fy1, y);
            }
            for (int index = 0; index < list.Count; index++)
            {
                Candidate c = list[index];
                int x = c.X, y = c.Y, i = y * w + x;
                double r1 = Math.Max(2.0, sig[c.Level] * 1.5), r2 = r1 * 2.0;
                double m1 = iGray.Mean(x, y, r1), m2 = iGray.Mean(x, y, r2);
                double a1 = (2 * r1 + 1) * (2 * r1 + 1), a2 = (2 * r2 + 1) * (2 * r2 + 1);
                double ring = (m2 * a2 - m1 * a1) / Math.Max(1, a2 - a1);
                double g1 = iGrad.Mean(x, y, r1), g2 = iGrad.Mean(x, y, r2);
                double var1 = Math.Max(0, iGray2.Mean(x, y, r1) - m1 * m1);
                double s1 = iSat.Mean(x, y, r1), s2 = iSat.Mean(x, y, r2);
                c.Features = new[]
                {
                    dogs[0][i] * c.Polarity, dogs[1][i] * c.Polarity, dogs[2][i] * c.Polarity, dogs[3][i] * c.Polarity,
                    c.Level / 3f, c.Polarity,
                    (float)((m1 - ring) / gstd), (float)((m1 - m2) / gstd), (float)(g1 / gstd), (float)(g2 / gstd), (float)(g1 / (g2 + 1e-3)),
                    (float)(Math.Sqrt(var1) / gstd), (float)s1, (float)(s1 - s2), (float)((gray[i] - lumFg) / gstd),
                    (float)iMask.Mean(x, y, 0.02 * diag), (float)iMask.Mean(x, y, 0.04 * diag),
                    (float)(x - fx0) / Math.Max(1, fx1 - fx0), (float)(y - fy0) / Math.Max(1, fy1 - fy0), (float)index / scorer.MaxCandidates
                };
            }
            return list;
        }

        private static int Clamp(int v, int n) => v < 0 ? 0 : v >= n ? n - 1 : v;

        private static float At(float[] a, int w, int h, int x, int y) => a[Clamp(y, h) * w + Clamp(x, w)];

        // A Gaussian blur with clamped edges (the kernel reaches 3 sigma, as scipy's gaussian_filter with truncate=3).
        internal static float[] Gauss(float[] a, int w, int h, double sigma)
        {
            int radius = (int)(3.0 * sigma + 0.5);
            double[] k = new double[2 * radius + 1];
            double total = 0;
            for (int i = -radius; i <= radius; i++)
            {
                k[i + radius] = Math.Exp(-0.5 * i * i / (sigma * sigma));
                total += k[i + radius];
            }
            for (int i = 0; i < k.Length; i++)
                k[i] /= total;
            float[] tmp = new float[a.Length], o = new float[a.Length];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    double s = 0;
                    for (int j = -radius; j <= radius; j++)
                        s += k[j + radius] * a[row + Clamp(x + j, w)];
                    tmp[row + x] = (float)s;
                }
            }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double s = 0;
                    for (int j = -radius; j <= radius; j++)
                        s += k[j + radius] * tmp[Clamp(y + j, h) * w + x];
                    o[y * w + x] = (float)s;
                }
            }
            return o;
        }

        // The maximum over a (2r+1) square with clamped edges.
        private static float[] MaxFilter(float[] a, int w, int h, int r)
        {
            float[] tmp = new float[a.Length], o = new float[a.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float m = float.MinValue;
                    for (int j = -r; j <= r; j++)
                        m = Math.Max(m, a[y * w + Clamp(x + j, w)]);
                    tmp[y * w + x] = m;
                }
            }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float m = float.MinValue;
                    for (int j = -r; j <= r; j++)
                        m = Math.Max(m, tmp[Clamp(y + j, h) * w + x]);
                    o[y * w + x] = m;
                }
            }
            return o;
        }

        // Box means through an integral image (the box clipped to the picture).
        private sealed class Integral
        {
            private readonly double[] s;
            private readonly int w, h;

            public Integral(float[] a, int w, int h)
            {
                this.w = w;
                this.h = h;
                s = new double[(w + 1) * (h + 1)];
                for (int y = 0; y < h; y++)
                {
                    double row = 0;
                    for (int x = 0; x < w; x++)
                    {
                        row += a[y * w + x];
                        s[(y + 1) * (w + 1) + x + 1] = s[y * (w + 1) + x + 1] + row;
                    }
                }
            }

            public double Mean(int x, int y, double r)
            {
                int x0 = Math.Max(0, (int)(x - r)), y0 = Math.Max(0, (int)(y - r));
                int x1 = Math.Min(w, (int)(x + r) + 1), y1 = Math.Min(h, (int)(y + r) + 1);
                int n = Math.Max(1, (x1 - x0) * (y1 - y0));
                return (s[y1 * (w + 1) + x1] - s[y0 * (w + 1) + x1] - s[y1 * (w + 1) + x0] + s[y0 * (w + 1) + x0]) / n;
            }
        }

        // ------------------------------------------------------------------ the network

        // Two RGB crops around the spot (the fields of view, a share of the diagonal), bilinear with clamped edges.
        private static float[] Patches(float[] rgb, int w, int h, int px, int py, SpotScorer scorer)
        {
            int p = scorer.Patch;
            double diag = Math.Sqrt((double)w * w + (double)h * h);
            float[] o = new float[3 * scorer.Fovs.Length * p * p];
            double cx = px - 0.5, cy = py - 0.5;
            for (int f = 0; f < scorer.Fovs.Length; f++)
            {
                double size = scorer.Fovs[f] * diag;
                for (int row = 0; row < p; row++)
                {
                    double y = Math.Min(h - 1, Math.Max(0, cy + ((row + 0.5) / p - 0.5) * size));
                    int y0 = (int)Math.Floor(y), y1 = Math.Min(y0 + 1, h - 1);
                    double fy = y - y0;
                    for (int col = 0; col < p; col++)
                    {
                        double x = Math.Min(w - 1, Math.Max(0, cx + ((col + 0.5) / p - 0.5) * size));
                        int x0 = (int)Math.Floor(x), x1 = Math.Min(x0 + 1, w - 1);
                        double fx = x - x0;
                        for (int c = 0; c < 3; c++)
                        {
                            double v = rgb[(y0 * w + x0) * 3 + c] * (1 - fx) * (1 - fy) + rgb[(y0 * w + x1) * 3 + c] * fx * (1 - fy) +
                                       rgb[(y1 * w + x0) * 3 + c] * (1 - fx) * fy + rgb[(y1 * w + x1) * 3 + c] * fx * fy;
                            double b = Math.Floor(Math.Min(255, Math.Max(0, v * 255 + 0.5)));
                            o[((3 * f + c) * p + row) * p + col] = (float)(b / 255.0 - 0.5);
                        }
                    }
                }
            }
            return o;
        }

        // conv 3x3 + ReLU + max pool, conv + ReLU + max pool, conv + ReLU + mean, then two dense layers: the logit.
        private static double Score(SpotScorer scorer, float[] input, float[] features)
        {
            int size = scorer.Patch;
            float[] a = Conv(scorer.Conv[0], input, size);
            a = Pool(a, scorer.Conv[0].Out, size);
            size /= 2;
            a = Conv(scorer.Conv[1], a, size);
            a = Pool(a, scorer.Conv[1].Out, size);
            size /= 2;
            a = Conv(scorer.Conv[2], a, size);
            int channels = scorer.Conv[2].Out;
            float[] x = new float[channels + SpotScorer.Features];
            for (int c = 0; c < channels; c++)
            {
                double s = 0;
                for (int i = 0; i < size * size; i++)
                    s += a[c * size * size + i];
                x[c] = (float)(s / (size * size));
            }
            for (int i = 0; i < SpotScorer.Features; i++)
                x[channels + i] = (features[i] - scorer.FeatureMean[i]) / scorer.FeatureStd[i];
            float[] hidden = Dense(scorer.Fc[0], x, relu: true);
            return Dense(scorer.Fc[1], hidden, relu: false)[0];
        }

        private static float[] Conv(SpotScorer.Layer layer, float[] input, int size)
        {
            float[] o = new float[layer.Out * size * size];
            for (int oc = 0; oc < layer.Out; oc++)
            {
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        double s = layer.B[oc];
                        for (int ic = 0; ic < layer.In; ic++)
                        {
                            int wBase = (oc * layer.In + ic) * 9, iBase = ic * size * size;
                            for (int ky = 0; ky < 3; ky++)
                            {
                                int yy = y + ky - 1;
                                if (yy < 0 || yy >= size)
                                    continue;
                                for (int kx = 0; kx < 3; kx++)
                                {
                                    int xx = x + kx - 1;
                                    if (xx >= 0 && xx < size)
                                        s += layer.W[wBase + ky * 3 + kx] * input[iBase + yy * size + xx];
                                }
                            }
                        }
                        o[(oc * size + y) * size + x] = s > 0 ? (float)s : 0f;
                    }
                }
            }
            return o;
        }

        private static float[] Pool(float[] a, int channels, int size)
        {
            int half = size / 2;
            float[] o = new float[channels * half * half];
            for (int c = 0; c < channels; c++)
            {
                for (int y = 0; y < half; y++)
                {
                    for (int x = 0; x < half; x++)
                    {
                        int b = c * size * size;
                        o[(c * half + y) * half + x] = Math.Max(Math.Max(a[b + 2 * y * size + 2 * x], a[b + 2 * y * size + 2 * x + 1]),
                            Math.Max(a[b + (2 * y + 1) * size + 2 * x], a[b + (2 * y + 1) * size + 2 * x + 1]));
                    }
                }
            }
            return o;
        }

        private static float[] Dense(SpotScorer.Layer layer, float[] x, bool relu)
        {
            float[] o = new float[layer.Out];
            for (int j = 0; j < layer.Out; j++)
            {
                double s = layer.B[j];
                for (int i = 0; i < layer.In; i++)
                    s += layer.W[j * layer.In + i] * x[i];
                o[j] = relu && s < 0 ? 0f : (float)s;
            }
            return o;
        }

        // ------------------------------------------------------------------ the pilot's measure

        // Against curated positions: a spot within 2 % of the photo's diagonal of a marker finds it.
        public static int Found(IEnumerable<double[]> markers, IList<ControlSpot> spots, double width, double height)
        {
            double tolerance = 0.02 * Math.Sqrt(width * width + height * height);
            return markers.Count(m => spots.Any(s => Math.Sqrt(Math.Pow((m[0] - s.X) * width, 2) + Math.Pow((m[1] - s.Y) * height, 2)) <= tolerance));
        }

        // The spots that sit on one of the markers (the share of useful snap targets).
        public static int OnMarkers(IEnumerable<double[]> markers, IList<ControlSpot> spots, double width, double height)
        {
            double tolerance = 0.02 * Math.Sqrt(width * width + height * height);
            List<double[]> list = markers.ToList();
            return spots.Count(s => list.Any(m => Math.Sqrt(Math.Pow((m[0] - s.X) * width, 2) + Math.Pow((m[1] - s.Y) * height, 2)) <= tolerance));
        }

        // The spot a click snaps to: the nearest within the snap distance (0..1 coordinates, the share of the diagonal).
        public static ControlSpot Nearest(IEnumerable<ControlSpot> spots, double x, double y, double width, double height, double snapShare)
        {
            if (spots == null || width <= 0 || height <= 0)
                return null;
            double limit = snapShare * Math.Sqrt(width * width + height * height);
            ControlSpot best = null;
            double bestDistance = double.MaxValue;
            foreach (ControlSpot spot in spots)
            {
                double d = Math.Sqrt(Math.Pow((spot.X - x) * width, 2) + Math.Pow((spot.Y - y) * height, 2));
                if (d <= limit && d < bestDistance)
                {
                    best = spot;
                    bestDistance = d;
                }
            }
            return best;
        }
    }
}
