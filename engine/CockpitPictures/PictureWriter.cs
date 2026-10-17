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
    // Renders the planned views of a cockpit and writes, per view, the picture (PNG, transparent around the model),
    // JoyPro's line drawing of it (lines and cel fills, "joypro-device-drawing") and the callouts, plus the set's
    // manifest "pictures.json" (format "joypro-cockpit-pictures", docs/cockpits.md "Pictures from your games").
    internal static class PictureWriter
    {
        public const string ManifestName = "pictures.json";
        public const string FormatName = "joypro-cockpit-pictures";
        public const int GeneratorVersion = 1;

        public sealed class SetInfo
        {
            public string Game = string.Empty;
            public string Plane = string.Empty;
            public List<string> Planes = new List<string>();
            public string Module = string.Empty;
            public string Source = string.Empty;
            public string GameVersion = string.Empty;
            public List<string> Notes = new List<string>();
        }

        public static void Write(CockpitScene scene, List<CockpitViewPlan> views, string folder, SetInfo info, Action<int, int, string> progress, bool clean = true)
        {
            Directory.CreateDirectory(folder);
            // The pictures of an earlier build go (they are made again from the game).
            if (clean)
            {
                foreach (string old in Directory.GetFiles(folder, "*.png").Concat(Directory.GetFiles(folder, "*.drawing.json")))
                {
                    try
                    {
                        File.Delete(old);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            StringBuilder manifest = new StringBuilder();
            Json j = new Json(manifest);
            j.Open('{');
            j.Prop("format", FormatName);
            j.Prop("version", 1);
            j.Prop("generator", GeneratorVersion);
            j.Prop("game", info.Game);
            j.Prop("plane", info.Plane);
            j.Key("planes");
            j.Open('[');
            foreach (string p in info.Planes)
                j.Value(p);
            j.Close(']');
            j.Prop("module", info.Module);
            j.Prop("source", info.Source);
            j.Prop("gameVersion", info.GameVersion);
            j.Prop("built", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            j.Key("notes");
            j.Open('[');
            foreach (string n in info.Notes)
                j.Value(n);
            j.Close(']');
            j.Key("views");
            j.Open('[');
            // The pictures, two at a time (the drawing's tracing is partly single-threaded).
            List<double[]>[] ats = new List<double[]>[views.Count];
            int done = 0;
            System.Threading.Tasks.Parallel.For(0, views.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 2 }, k =>
            {
                CockpitViewPlan v = views[k];
                string file = FileName(v.Id);
                RenderCamera big = v.Camera.Scaled(2);
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                RenderBuffers b = SoftwareRenderer.Render(scene, big, v.Meshes, null);
                if (Environment.GetEnvironmentVariable("JP_PROFILE") == "1") Console.WriteLine(" render " + sw.ElapsedMilliseconds);
                // A picture with (almost) nothing on it (a grip the model hides at rest) is left out.
                int coveredPixels = 0;
                for (int i = 0; i < b.Covered.Length && coveredPixels < 2000; i++)
                {
                    if (b.Covered[i])
                        coveredPixels++;
                }
                if (coveredPixels < 2000 && v.Kind != "overview")
                {
                    System.Threading.Interlocked.Increment(ref done);
                    return;
                }
                // The picture: the shaded render, transparent where nothing is.
                int w = v.Camera.Width, h = v.Camera.Height;
                byte[] rgba = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int cov = 0, r = 0, g = 0, bl = 0;
                        for (int dy = 0; dy < 2; dy++)
                        {
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int i = (2 * y + dy) * b.Width + 2 * x + dx;
                                if (!b.Covered[i])
                                    continue;
                                cov++;
                                r += b.Color[3 * i];
                                g += b.Color[3 * i + 1];
                                bl += b.Color[3 * i + 2];
                            }
                        }
                        int o = (y * w + x) * 4;
                        if (cov > 0)
                        {
                            rgba[o] = (byte)(r / cov);
                            rgba[o + 1] = (byte)(g / cov);
                            rgba[o + 2] = (byte)(bl / cov);
                            rgba[o + 3] = (byte)(cov * 255 / 4);
                        }
                    }
                }
                PngWriter.Write(Path.Combine(folder, file + ".png"), rgba, w, h, 4);
                // JoyPro's drawing of it.
                LineArtTracer.Result art = LineArtTracer.Trace(b, v.Camera.Ortho, 2, new LineArtTracer.Settings { Labels = v.Kind != "overview" });
                // Callouts on the picture (0..1), with whether something covers them.
                List<double[]> at = new List<double[]>();
                foreach (CockpitCallout c in v.Callouts)
                {
                    double sx, sy, depth;
                    bool ok = v.Camera.Project(c.Position, out sx, out sy, out depth);
                    bool hidden = false;
                    if (ok)
                    {
                        int px = (int)(sx * 2), py = (int)(sy * 2);
                        if (px >= 0 && py >= 0 && px < b.Width && py < b.Height)
                        {
                            float d = b.Depth[py * b.Width + px];
                            hidden = b.Covered[py * b.Width + px] && d < depth - Math.Max(0.03, depth * 0.04);
                        }
                    }
                    at.Add(ok ? new[] { sx / w, sy / h, hidden ? 1 : 0 } : new[] { -1.0, -1.0, 1 });
                }
                WriteDrawing(Path.Combine(folder, file + ".drawing.json"), info, v, art, at);
                ats[k] = at;
                int n = System.Threading.Interlocked.Increment(ref done);
                if (progress != null)
                {
                    lock (ats)
                        progress(n, views.Count, v.Title);
                }
            });
            HashSet<string> left = new HashSet<string>(views.Where((x, k) => ats[k] == null).Select(x => x.Id), StringComparer.Ordinal);
            for (int k = 0; k < views.Count; k++)
            {
                CockpitViewPlan v = views[k];
                List<double[]> at = ats[k];
                if (at == null)
                    continue;
                string file = FileName(v.Id);
                int w = v.Camera.Width, h = v.Camera.Height;
                j.Open('{');
                j.Prop("id", v.Id);
                j.Prop("kind", v.Kind);
                j.Prop("title", v.Title);
                j.Prop("area", v.Area);
                j.Prop("device", v.Device);
                j.Prop("seat", v.Seat);
                j.Prop("image", file + ".png");
                j.Prop("drawing", file + ".drawing.json");
                j.Prop("width", w);
                j.Prop("height", h);
                j.Prop("projection", v.Camera.Ortho ? "orthographic" : "perspective");
                j.Key("callouts");
                j.Open('[');
                for (int i = 0; i < v.Callouts.Count; i++)
                {
                    CockpitCallout c = v.Callouts[i];
                    if (v.Kind == "overview" && i < v.Links.Count && left.Contains(v.Links[i]))
                        continue;
                    j.Open('{');
                    j.Prop("n", c.N);
                    j.Prop("id", c.Id);
                    j.Prop("label", c.Label);
                    j.Prop("x", Math.Round(at[i][0], 4));
                    j.Prop("y", Math.Round(at[i][1], 4));
                    if (at[i][2] > 0)
                        j.Prop("hidden", true);
                    if (c.Kind.Length > 0)
                        j.Prop("kind", c.Kind);
                    if (c.Device.Length > 0)
                        j.Prop("device", c.Device);
                    if (c.Panel.Length > 0 && v.Kind == "panel")
                        j.Prop("panel", c.PanelTitle.Length > 0 ? c.PanelTitle : c.Panel);
                    if (v.Kind == "overview" && i < v.Links.Count)
                        j.Prop("link", v.Links[i]);
                    if (c.Actions.Count > 0)
                        j.Prop("actions", string.Join(",", c.Actions));
                    if (c.Args.Count > 0)
                        j.Prop("args", string.Join(",", c.Args));
                    if (c.Commands.Count > 0)
                    {
                        j.Key("commands");
                        j.Open('[');
                        foreach (CalloutCommand cc in c.Commands)
                        {
                            j.Open('{');
                            j.Prop("name", cc.Name);
                            j.Prop("category", cc.Category);
                            j.Prop("id", cc.Id);
                            j.Close('}');
                        }
                        j.Close(']');
                    }
                    j.Close('}');
                }
                j.Close(']');
                j.Close('}');
            }
            j.Close(']');
            j.Close('}');
            File.WriteAllText(Path.Combine(folder, ManifestName), manifest.ToString(), new UTF8Encoding(false));
        }

        public static string FileName(string viewId)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in viewId ?? string.Empty)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' ? char.ToLowerInvariant(c) : '_');
            string s = sb.ToString().Trim('_');
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }

        private static void WriteDrawing(string path, SetInfo info, CockpitViewPlan v, LineArtTracer.Result art, List<double[]> at)
        {
            StringBuilder sb = new StringBuilder();
            Json j = new Json(sb);
            j.Open('{');
            j.Prop("format", "joypro-device-drawing");
            j.Prop("version", 1);
            j.Prop("aircraft", info.Plane);
            j.Prop("device", v.Kind == "device" ? v.Device : v.Kind);
            j.Prop("view", v.Id);
            j.Prop("title", v.Title);
            j.Prop("origin", "Made on this PC from the game's own cockpit model (" + info.Game + "); not part of JoyPro.");
            j.Prop("width", Math.Round(art.Width, 1));
            j.Prop("height", Math.Round(art.Height, 1));
            double scale = Math.Max(art.Width, art.Height) / 1400.0;
            j.Prop("outlineWidth", Math.Round(2.4 * Math.Max(0.7, scale), 2));
            j.Prop("detailWidth", Math.Round(1.3 * Math.Max(0.7, scale), 2));
            j.Key("paths");
            j.Open('[');
            foreach (KeyValuePair<string, string> p in new[] { new KeyValuePair<string, string>("outline", art.Outline), new KeyValuePair<string, string>("detail", art.Detail), new KeyValuePair<string, string>("minor", art.Minor) })
            {
                if (p.Value.Length == 0)
                    continue;
                j.Open('{');
                j.Prop("role", p.Key);
                j.Prop("d", p.Value);
                j.Close('}');
            }
            j.Close(']');
            j.Key("fills");
            j.Open('[');
            foreach (KeyValuePair<string, string> f in art.Fills)
            {
                j.Open('{');
                j.Prop("color", f.Key);
                j.Prop("rule", "evenodd");
                j.Prop("d", f.Value);
                j.Close('}');
            }
            j.Close(']');
            j.Key("parts");
            j.Open('{');
            for (int i = 0; i < v.Callouts.Count; i++)
            {
                if (at[i][0] < 0)
                    continue;
                j.Key(v.Callouts[i].Id);
                j.Open('[');
                j.Value(Math.Round(at[i][0], 4));
                j.Value(Math.Round(at[i][1], 4));
                j.Close(']');
            }
            j.Close('}');
            j.Close('}');
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // A minimal JSON writer.
        internal sealed class Json
        {
            private readonly StringBuilder sb;
            private readonly Stack<bool> first = new Stack<bool>();

            public Json(StringBuilder sb)
            {
                this.sb = sb;
            }

            private void Sep()
            {
                if (first.Count == 0)
                    return;
                if (!first.Peek())
                    sb.Append(',');
                first.Pop();
                first.Push(false);
            }

            private bool pendingKey;

            public void Key(string k)
            {
                Sep();
                Str(k);
                sb.Append(':');
                pendingKey = true;
            }

            public void Open(char c)
            {
                if (!pendingKey)
                    Sep();
                pendingKey = false;
                sb.Append(c);
                first.Push(true);
            }

            public void Close(char c)
            {
                first.Pop();
                sb.Append(c);
            }

            public void Value(string s)
            {
                if (!pendingKey)
                    Sep();
                pendingKey = false;
                Str(s);
            }

            public void Value(double d)
            {
                if (!pendingKey)
                    Sep();
                pendingKey = false;
                sb.Append(d.ToString("0.####", CultureInfo.InvariantCulture));
            }

            public void Prop(string k, string v)
            {
                Key(k);
                pendingKey = false;
                Str(v ?? string.Empty);
            }

            public void Prop(string k, double v)
            {
                Key(k);
                pendingKey = false;
                sb.Append(v.ToString("0.####", CultureInfo.InvariantCulture));
            }

            public void Prop(string k, bool v)
            {
                Key(k);
                pendingKey = false;
                sb.Append(v ? "true" : "false");
            }

            private void Str(string s)
            {
                sb.Append('"');
                foreach (char c in s ?? string.Empty)
                {
                    switch (c)
                    {
                        case '"':
                            sb.Append("\\\"");
                            break;
                        case '\\':
                            sb.Append("\\\\");
                            break;
                        case '\n':
                            sb.Append("\\n");
                            break;
                        case '\r':
                            break;
                        case '\t':
                            sb.Append("\\t");
                            break;
                        default:
                            if (c < 32)
                                sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else
                                sb.Append(c);
                            break;
                    }
                }
                sb.Append('"');
            }
        }
    }
}
