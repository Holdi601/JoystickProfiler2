#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.CockpitPictures
{
    // What a picture shows, measured on its pixels (and, for a render, on what the renderer drew): the automatic quality
    // gate of the cockpit pictures (docs/cockpits.md "Picture quality"). A picture that is mostly one flat colour, black,
    // empty, untextured or hides its own controls scores low; the writer then frames it again or leaves it out, and a set
    // without a usable overview is marked "low quality" (the Cockpits page then prefers another game's set).
    internal sealed class PictureMetrics
    {
        // Share of the frame the model covers (0..1).
        public double Coverage;
        // Share of the frame inside the box around what is drawn (2nd to 98th percentile): a tiny grip far away is small.
        public double Fill;
        // Share of covered 8x8 blocks with visible detail (local contrast above 7 % of their brightness, at least 3 levels).
        public double Structure;
        // Brightness histogram entropy (bits, 32 bins).
        public double Entropy;
        // Share of covered pixels that are (nearly) black.
        public double Black;
        // Mean brightness of the covered pixels (0..255).
        public double Mean;
        // Share of the drawn pixels that come from a textured material (1 for screenshots).
        public double Textured = 1;
        // Share of the callouts that are in the frame and not covered by something in front (1 without callouts).
        public double Visible = 1;
        // Share of the drawn pixels within 20 cm of the near plane (an overview's camera inside an instrument or a
        // seat); 0 for screenshots.
        public double Near;
        // 24 x 16 brightness thumbnail (0..255; -1 where nothing is drawn) for finding duplicates.
        public float[] Thumb = new float[0];
        public int Score;
        public List<string> Problems = new List<string>();
    }

    internal static class PictureQuality
    {
        // At or above: a good picture. Below Keep: a panel or grip picture is left out when framing it again does not help.
        public const int Good = 60;
        public const int Keep = 45;
        public const int ThumbW = 24, ThumbH = 16;

        // rgba: 4 bytes per pixel (alpha = coverage), or rgb (3 bytes per pixel) with an optional mask (1 byte per pixel).
        public static PictureMetrics Measure(byte[] pixels, int width, int height, int channels, byte[] mask = null)
        {
            PictureMetrics m = new PictureMetrics();
            if (pixels == null || width <= 0 || height <= 0)
                return m;
            // At most ~480 pixels wide (nearest pixel): the measure is about what a reader sees, not about noise.
            int step = Math.Max(1, width / 480);
            int w = width / step, h = height / step;
            if (w < 8 || h < 8)
                return m;
            float[] y = new float[w * h];
            bool[] cov = new bool[w * h];
            int covered = 0;
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    int src = (j * step) * width + i * step;
                    int o = src * channels;
                    bool c = channels == 4 ? pixels[o + 3] > 127 : mask == null || mask[src] > 127;
                    int k = j * w + i;
                    cov[k] = c;
                    if (!c)
                        continue;
                    covered++;
                    y[k] = 0.299f * pixels[o] + 0.587f * pixels[o + 1] + 0.114f * pixels[o + 2];
                }
            }
            m.Coverage = covered / (double)(w * h);
            m.Thumb = Thumb(y, cov, w, h);
            if (covered < 50)
            {
                m.Problems.Add("empty");
                return m;
            }
            // Brightness: mean, black share, entropy.
            int[] hist = new int[32];
            double sum = 0;
            int black = 0;
            int minX = w, maxX = 0, minY = h, maxY = 0;
            int[] colCount = new int[w], rowCount = new int[h];
            for (int k = 0; k < y.Length; k++)
            {
                if (!cov[k])
                    continue;
                sum += y[k];
                if (y[k] < 8)
                    black++;
                hist[Math.Min(31, (int)(y[k] / 8))]++;
                colCount[k % w]++;
                rowCount[k / w]++;
            }
            m.Mean = sum / covered;
            m.Black = black / (double)covered;
            double ent = 0;
            foreach (int c in hist)
            {
                if (c == 0)
                    continue;
                double p = c / (double)covered;
                ent -= p * Math.Log(p, 2);
            }
            m.Entropy = ent;
            // The box around 96 % of what is drawn.
            int lo = covered / 50, acc = 0;
            for (int i = 0; i < w; i++) { acc += colCount[i]; if (acc > lo) { minX = i; break; } }
            acc = 0;
            for (int i = w - 1; i >= 0; i--) { acc += colCount[i]; if (acc > lo) { maxX = i; break; } }
            acc = 0;
            for (int i = 0; i < h; i++) { acc += rowCount[i]; if (acc > lo) { minY = i; break; } }
            acc = 0;
            for (int i = h - 1; i >= 0; i--) { acc += rowCount[i]; if (acc > lo) { maxY = i; break; } }
            m.Fill = maxX >= minX && maxY >= minY ? (maxX - minX + 1) * (double)(maxY - minY + 1) / (w * h) : 0;
            // Detail: 8x8 blocks with local contrast.
            int blocks = 0, detailed = 0;
            for (int by = 0; by + 8 <= h; by += 8)
            {
                for (int bx = 0; bx + 8 <= w; bx += 8)
                {
                    double s = 0, s2 = 0;
                    bool full = true;
                    for (int j = by; j < by + 8 && full; j++)
                    {
                        for (int i = bx; i < bx + 8; i++)
                        {
                            int k = j * w + i;
                            if (!cov[k])
                            {
                                full = false;
                                break;
                            }
                            s += y[k];
                            s2 += y[k] * y[k];
                        }
                    }
                    if (!full)
                        continue;
                    blocks++;
                    double mean = s / 64, sd = Math.Sqrt(Math.Max(0, s2 / 64 - mean * mean));
                    if (sd >= Math.Max(3.0, 0.07 * mean))
                        detailed++;
                }
            }
            m.Structure = blocks == 0 ? 0 : detailed / (double)blocks;
            return m;
        }

        private static float[] Thumb(float[] y, bool[] cov, int w, int h)
        {
            float[] t = new float[ThumbW * ThumbH];
            for (int ty = 0; ty < ThumbH; ty++)
            {
                for (int tx = 0; tx < ThumbW; tx++)
                {
                    int x0 = tx * w / ThumbW, x1 = Math.Max(x0 + 1, (tx + 1) * w / ThumbW);
                    int y0 = ty * h / ThumbH, y1 = Math.Max(y0 + 1, (ty + 1) * h / ThumbH);
                    double s = 0;
                    int n = 0;
                    for (int j = y0; j < y1; j++)
                    {
                        for (int i = x0; i < x1; i++)
                        {
                            if (!cov[j * w + i])
                                continue;
                            s += y[j * w + i];
                            n++;
                        }
                    }
                    t[ty * ThumbW + tx] = n * 2 < (x1 - x0) * (y1 - y0) ? -1 : (float)(s / n);
                }
            }
            return t;
        }

        // 0..100 from the measures; kind: overview, panel, device (a grip on its own: transparent around it is fine),
        // screenshot. Fills Problems with the reasons (short English words, for the report and pictures.json).
        public static int Rate(PictureMetrics m, string kind)
        {
            m.Problems.RemoveAll(p => p != "empty");
            if (m.Coverage < 0.005)
            {
                if (!m.Problems.Contains("empty"))
                    m.Problems.Add("empty");
                return m.Score = 0;
            }
            double s = 100;
            bool grip = kind == "device";
            double structure = Math.Max(0, 0.35 - m.Structure) * 180;
            if (structure > 12)
                m.Problems.Add("little detail");
            s -= structure;
            double entropy = Math.Max(0, 2.2 - m.Entropy) * 22;
            if (entropy > 12)
                m.Problems.Add("one flat colour");
            s -= entropy;
            double black = Math.Max(0, m.Black - 0.45) * 90;
            if (black > 10)
                m.Problems.Add("mostly black");
            s -= black;
            double size = grip ? Math.Max(0, 0.12 - m.Fill) * 300 : Math.Max(0, 0.45 - m.Coverage) * 70;
            if (size > 12)
                m.Problems.Add(grip ? "too small in the frame" : "mostly empty");
            s -= size;
            double textured = Math.Max(0, 0.6 - m.Textured) * 80;
            if (textured > 12)
                m.Problems.Add("untextured");
            s -= textured;
            // (A seat's head rest right below the camera is normal; more than half the picture right at the camera is not.)
            double near = kind == "overview" ? Math.Max(0, m.Near - 0.5) * 320 : 0;
            if (near > 12)
                m.Problems.Add("camera inside the model");
            s -= near;
            double visible = Math.Max(0, 0.5 - m.Visible) * 60;
            if (visible > 12)
                m.Problems.Add("controls hidden");
            s -= visible;
            m.Score = (int)Math.Round(Math.Max(0, Math.Min(100, s)));
            return m.Score;
        }

        // Mean difference of two thumbnails (0..255) where both have something; 255 when they hardly overlap.
        public static double Distance(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length == 0)
                return 255;
            double s = 0;
            int n = 0, mismatch = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] < 0 && b[i] < 0)
                    continue;
                if (a[i] < 0 || b[i] < 0)
                {
                    mismatch++;
                    continue;
                }
                s += Math.Abs(a[i] - b[i]);
                n++;
            }
            if (n < a.Length / 4)
                return 255;
            return s / n + mismatch * 64.0 / (n + mismatch);
        }

        // Two pictures that look the same (same panel framed twice, the same grip): the later one is a duplicate.
        public static bool SameLook(PictureMetrics a, PictureMetrics b)
        {
            return Distance(a.Thumb, b.Thumb) < 7;
        }

        // Share of the drawn pixels whose material has a texture.
        public static double TexturedShare(RenderBuffers b, CockpitScene scene)
        {
            long drawn = 0, textured = 0;
            for (int i = 0; i < b.Covered.Length; i += 3)
            {
                if (!b.Covered[i])
                    continue;
                drawn++;
                int mat = b.Material[i];
                if (mat >= 0 && mat < scene.Materials.Count && scene.Materials[mat].Albedo != null)
                    textured++;
            }
            return drawn == 0 ? 0 : textured / (double)drawn;
        }

        // The set's verdict: "low" when the overview is poor or fewer than a third of its pictures are good.
        public static bool LowSet(int overviewScore, IList<int> scores)
        {
            if (scores.Count == 0)
                return true;
            int good = scores.Count(s => s >= Good);
            return overviewScore < Keep || good * 3 < scores.Count;
        }
    }
}
