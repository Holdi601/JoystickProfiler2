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
        public const int GeneratorVersion = 2;

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

        // Builds the set: every view is rendered, measured (PictureQuality) and, when poor, framed again (a grip zoomed to
        // what is drawn, the planner's other cameras); a panel or grip picture that stays poor is left out, one that looks
        // like an earlier panel picture gives its callouts to that one. The set gets its verdict ("quality" in
        // pictures.json).
        public static void Write(CockpitScene scene, List<CockpitViewPlan> views, string folder, SetInfo info, Action<int, int, string> progress, bool clean = true)
        {
            Directory.CreateDirectory(folder);
            // The pictures of an earlier build go (they are made again from the game).
            if (clean)
            {
                // (also a picture pack's JPEGs and its pack.json: the set is made on this PC now)
                foreach (string old in Directory.GetFiles(folder, "*.png").Concat(Directory.GetFiles(folder, "*.jpg")).Concat(Directory.GetFiles(folder, "*.drawing.json")).Concat(Directory.GetFiles(folder, "pack.json")))
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
            UniqueIds(views);
            // Meshes larger than any aircraft (broken or placeholder geometry hundreds of metres across, the F-100D has
            // some) cut slabs through every picture: left out.
            List<int> huge = new List<int>();
            for (int i = 0; i < scene.Meshes.Count; i++)
            {
                Vec3 mn, mx;
                scene.Bounds(new[] { i }, out mn, out mx);
                Vec3 ext = mx - mn;
                if (ext.X > 200 || ext.Y > 200 || ext.Z > 200 || double.IsNaN(ext.X))
                    huge.Add(i);
            }
            if (huge.Count > 0)
            {
                HashSet<int> skip = new HashSet<int>(huge);
                List<int> all = Enumerable.Range(0, scene.Meshes.Count).Where(i => !skip.Contains(i)).ToList();
                foreach (CockpitViewPlan v in views)
                    v.Meshes = v.Meshes == null ? all : v.Meshes.Where(i => !skip.Contains(i)).ToList();
            }
            // A model without any texture (synthetic, or nothing decodable) keeps its pictures: they are all there is
            // (the set is still rated).
            bool keepAll = Environment.GetEnvironmentVariable("JOYPRO_PICTURE_KEEP_ALL") == "1" || !scene.Materials.Any(m => m.Albedo != null);
            // The pictures, two at a time (the drawing's tracing is partly single-threaded).
            ViewResult[] results = new ViewResult[views.Count];
            int done = 0;
            System.Threading.Tasks.Parallel.For(0, views.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 2 }, k =>
            {
                CockpitViewPlan v = views[k];
                ViewResult r = RenderGated(scene, v, keepAll);
                results[k] = r;
                if (r != null)
                {
                    PngWriter.Write(Path.Combine(folder, FileName(v.Id) + ".png"), r.Rgba, r.Width, r.Height, 4);
                    r.Rgba = null;
                }
                int n = System.Threading.Interlocked.Increment(ref done);
                if (progress != null)
                {
                    lock (results)
                        progress(n, views.Count, v.Title);
                }
            });
            // Panel pictures that look like an earlier one (the same panel, framed for another group of its controls):
            // the later one's callouts move to the earlier picture where they are in its frame.
            Dictionary<string, string> mergedInto = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int k = 0; k < views.Count; k++)
            {
                ViewResult r = results[k];
                if (r == null || views[k].Kind != "panel")
                    continue;
                for (int e = 0; e < k; e++)
                {
                    ViewResult keep = results[e];
                    // The same seat, a camera at nearly the same place looking the same way, and the same look (two dark
                    // panels elsewhere can look alike as thumbnails).
                    if (keep == null || views[e].Kind != "panel" || views[e].Seat != views[k].Seat || !PictureQuality.SameLook(keep.Metrics, r.Metrics) ||
                        (keep.Camera.Eye - r.Camera.Eye).Length > 0.35 || Vec3.Dot(keep.Camera.Forward, r.Camera.Forward) < 0.9)
                        continue;
                    if (Merge(views[e], keep, views[k], r))
                    {
                        mergedInto[views[k].Id] = views[e].Id;
                        results[k] = null;
                        try
                        {
                            File.Delete(Path.Combine(folder, FileName(views[k].Id) + ".png"));
                        }
                        catch (Exception)
                        {
                        }
                    }
                    break;
                }
            }
            // The drawings (after the merges: they carry the callouts' places).
            for (int k = 0; k < views.Count; k++)
            {
                if (results[k] != null)
                    WriteDrawing(Path.Combine(folder, FileName(views[k].Id) + ".drawing.json"), info, views[k], results[k].Art, results[k].At);
            }
            ViewResult overviewResult = results.FirstOrDefault(x => x != null && x.View.Kind == "overview");
            List<int> scores = results.Where(x => x != null).Select(x => x.Metrics.Score).ToList();
            int dropped = results.Count(x => x == null) - mergedInto.Count;
            bool low = PictureQuality.LowSet(overviewResult == null ? 0 : overviewResult.Metrics.Score, scores);

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
            j.Key("quality");
            j.Open('{');
            j.Prop("score", overviewResult == null ? 0 : overviewResult.Metrics.Score);
            j.Prop("mean", scores.Count == 0 ? 0 : Math.Round(scores.Average()));
            j.Prop("low", low);
            j.Prop("views", scores.Count);
            j.Prop("dropped", dropped);
            j.Prop("merged", mergedInto.Count);
            j.Close('}');
            WriteControls(j, scene, views, results);
            j.Key("views");
            j.Open('[');
            for (int k = 0; k < views.Count; k++)
            {
                CockpitViewPlan v = views[k];
                ViewResult r = results[k];
                if (r == null)
                    continue;
                List<double[]> at = r.At;
                string file = FileName(v.Id);
                int w = r.Width, h = r.Height;
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
                j.Prop("projection", r.Camera.Ortho ? "orthographic" : "perspective");
                j.Prop("quality", r.Metrics.Score);
                if (r.Metrics.Problems.Count > 0)
                {
                    j.Key("problems");
                    j.Open('[');
                    foreach (string p in r.Metrics.Problems)
                        j.Value(p);
                    j.Close(']');
                }
                if (!DrawingReadable(v, r))
                    j.Prop("drawingReadable", false);
                j.Key("callouts");
                j.Open('[');
                for (int i = 0; i < v.Callouts.Count; i++)
                {
                    CockpitCallout c = v.Callouts[i];
                    string link = v.Kind == "overview" && i < v.Links.Count ? v.Links[i] : null;
                    if (link != null)
                    {
                        string target;
                        if (mergedInto.TryGetValue(link, out target))
                            link = target;
                        int li = views.FindIndex(x => x.Id == link);
                        if (li < 0 || results[li] == null)
                            continue;
                        // Two panels merged into one: one callout leads to it.
                        bool earlier = false;
                        for (int q = 0; q < i && !earlier; q++)
                        {
                            string other = q < v.Links.Count ? v.Links[q] : null;
                            if (other != null && mergedInto.TryGetValue(other, out target))
                                other = target;
                            earlier = other == link;
                        }
                        if (earlier)
                            continue;
                    }
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
                    if (link != null)
                        j.Prop("link", link);
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
            LastVerdict = new SetVerdict { Low = low, Score = overviewResult == null ? 0 : overviewResult.Metrics.Score, Views = scores.Count, Dropped = dropped, Merged = mergedInto.Count };
            // For the build's report (the builders share the list with SetInfo; the manifest is already written).
            string verdict = LastVerdict.Note();
            if (verdict.Length > 0 && info.Notes != null)
                info.Notes.Add(verdict);
        }

        // "controls": every control of the cockpit once (the callouts of the panel and grip pictures and the model's other
        // animated controls, CockpitScene.Controls) with its kind, its animation and its place on every picture it is
        // visible in (its own picture with its number; elsewhere where its surface is drawn at its place).
        private static void WriteControls(Json j, CockpitScene scene, List<CockpitViewPlan> views, ViewResult[] results)
        {
            List<CockpitCallout> controls = new List<CockpitCallout>();
            Dictionary<CockpitCallout, int> own = new Dictionary<CockpitCallout, int>();
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            for (int k = 0; k < views.Count; k++)
            {
                if (results[k] == null || views[k].Kind == "overview")
                    continue;
                foreach (CockpitCallout c in views[k].Callouts)
                {
                    if (own.ContainsKey(c))
                        continue;
                    own[c] = k;
                    c.Seat = views[k].Seat;
                    controls.Add(c);
                    ids.Add(c.Seat + "|" + c.Id);
                }
            }
            // The grips themselves (their picture's id after the planner's numbering: device:stick-2).
            for (int k = 0; k < views.Count; k++)
            {
                CockpitCallout g = views[k].GripControl;
                if (results[k] == null || g == null)
                    continue;
                g.Id = "grip:" + views[k].Id.Substring(views[k].Id.IndexOf(':') + 1);
                g.Seat = views[k].Seat;
                if (ids.Add(g.Seat + "|" + g.Id))
                    controls.Add(g);
            }
            foreach (CockpitCallout c in scene.Controls)
            {
                if (ids.Add(c.Seat + "|" + c.Id))
                    controls.Add(c);
            }
            j.Key("controls");
            j.Open('[');
            foreach (CockpitCallout c in controls)
            {
                j.Open('{');
                j.Prop("id", c.Id);
                j.Prop("label", c.Label);
                j.Prop("kind", ControlKinds.Of(c));
                if (c.GameKind.Length > 0)
                    j.Prop("gameKind", c.GameKind);
                else if (c.Kind.Length > 0)
                    j.Prop("gameKind", c.Kind);
                if (c.PanelTitle.Length > 0 || c.Panel.Length > 0)
                    j.Prop("panel", c.PanelTitle.Length > 0 ? c.PanelTitle : c.Panel);
                if (c.Device.Length > 0)
                    j.Prop("device", c.Device);
                j.Prop("seat", c.Seat);
                if (c.Animation != null)
                {
                    j.Key("animation");
                    j.Open('{');
                    j.Prop("source", c.Animation.Source);
                    j.Prop("name", c.Animation.Name);
                    if (!double.IsNaN(c.Animation.Min))
                        j.Prop("min", Math.Round(c.Animation.Min, 4));
                    if (!double.IsNaN(c.Animation.Max))
                        j.Prop("max", Math.Round(c.Animation.Max, 4));
                    if (c.Animation.Positions.Count > 0)
                    {
                        j.Key("positions");
                        j.Open('[');
                        foreach (double p in c.Animation.Positions)
                            j.Value(Math.Round(p, 4));
                        j.Close(']');
                    }
                    j.Close('}');
                }
                if (c.Commands.Count > 0)
                {
                    j.Key("commands");
                    j.Open('[');
                    foreach (CalloutCommand cc in c.Commands)
                        j.Value(cc.Id.Length > 0 ? cc.Id : cc.Name);
                    j.Close(']');
                }
                j.Key("views");
                j.Open('[');
                for (int k = 0; k < views.Count; k++)
                {
                    ViewResult r = results[k];
                    if (r == null || views[k].Seat != c.Seat)
                        continue;
                    int index = views[k].Callouts.IndexOf(c);
                    double x, y;
                    if (views[k].GripControl == c)
                    {
                        x = 0.5;
                        y = 0.5;
                    }
                    else if (index >= 0 && index < r.At.Count && r.At[index][0] >= 0)
                    {
                        x = r.At[index][0];
                        y = r.At[index][1];
                    }
                    else if (index >= 0 || !SpotOn(r, c.Position, out x, out y))
                        continue;
                    j.Open('{');
                    j.Prop("view", views[k].Id);
                    if (index >= 0)
                        j.Prop("n", c.N);
                    j.Prop("x", Math.Round(x, 4));
                    j.Prop("y", Math.Round(y, 4));
                    j.Close('}');
                }
                j.Close(']');
                j.Close('}');
            }
            j.Close(']');
        }

        // Where a point is on a picture (0..1) when the picture shows a surface right there (not hidden behind something,
        // not empty background).
        private static bool SpotOn(ViewResult r, Vec3 p, out double x, out double y)
        {
            x = y = -1;
            double sx, sy, depth;
            if (r.Depth == null || !r.Camera.Project(p, out sx, out sy, out depth) || sx < 0 || sy < 0 || sx >= r.Width || sy >= r.Height)
                return false;
            int px = Math.Min(r.DepthW - 1, (int)(sx / 4)), py = Math.Min(r.DepthH - 1, (int)(sy / 4));
            float d = r.Depth[py * r.DepthW + px];
            if (float.IsInfinity(d) || Math.Abs(d - depth) > Math.Max(0.06, depth * 0.06))
                return false;
            x = sx / r.Width;
            y = sy / r.Height;
            return true;
        }

        // The verdict of the last set written on this thread (for the builders' reports).
        [ThreadStatic]
        public static SetVerdict LastVerdict;

        public sealed class SetVerdict
        {
            public bool Low;
            public int Score;
            public int Views;
            public int Dropped;
            public int Merged;

            public string Note()
            {
                string s = Low ? "low quality (overview " + Score.ToString(CultureInfo.InvariantCulture) + "/100)" : string.Empty;
                if (Dropped > 0)
                    s += (s.Length > 0 ? "; " : string.Empty) + Dropped.ToString(CultureInfo.InvariantCulture) + " unusable pictures left out";
                if (Merged > 0)
                    s += (s.Length > 0 ? "; " : string.Empty) + Merged.ToString(CultureInfo.InvariantCulture) + " duplicate pictures merged";
                return s;
            }
        }

        // Views with the same id (two groups of controls at the same place) would share their files: numbered; the
        // overview's links follow (its n-th link to an id leads to the n-th view of that id).
        internal static void UniqueIds(List<CockpitViewPlan> views)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<string>> finals = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (CockpitViewPlan v in views)
            {
                string original = v.Id, id = v.Id;
                int n = 2;
                while (seen.Contains(id) || files.Contains(FileName(id)))
                    id = original + "-" + (n++).ToString(CultureInfo.InvariantCulture);
                v.Id = id;
                seen.Add(id);
                files.Add(FileName(id));
                List<string> list;
                if (!finals.TryGetValue(original, out list))
                    finals[original] = list = new List<string>();
                list.Add(id);
            }
            foreach (CockpitViewPlan o in views.Where(x => x.Kind == "overview"))
            {
                Dictionary<string, int> occurrence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < o.Links.Count; i++)
                {
                    List<string> list;
                    if (!finals.TryGetValue(o.Links[i], out list) || list.Count < 2)
                        continue;
                    int k;
                    occurrence.TryGetValue(o.Links[i], out k);
                    occurrence[o.Links[i]] = k + 1;
                    string target = list[Math.Min(k, list.Count - 1)];
                    if (i < o.Callouts.Count && string.Equals(o.Callouts[i].Id, o.Links[i], StringComparison.OrdinalIgnoreCase))
                        o.Callouts[i].Id = target;
                    o.Links[i] = target;
                }
            }
        }

        internal sealed class ViewResult
        {
            public CockpitViewPlan View;
            public RenderCamera Camera;
            public byte[] Rgba;
            public int Width, Height;
            public PictureMetrics Metrics;
            public List<double[]> At;
            public LineArtTracer.Result Art;
            // Depth at a quarter of the picture's size (for callouts merged in from a duplicate).
            public float[] Depth;
            public int DepthW, DepthH;
            public bool Covered2000;
        }

        // One view: rendered, measured, framed again while poor; null = left out.
        private static ViewResult RenderGated(CockpitScene scene, CockpitViewPlan v, bool keepAll)
        {
            RenderBuffers best;
            ViewResult r = RenderOnce(scene, v, v.Camera, out best);
            if (r == null && v.Kind != "overview")
                return null;
            // A grip far smaller than its frame (a part of it far away, a hidden lever): zoomed to what is drawn.
            if (r != null && v.Kind == "device" && r.Metrics.Fill < 0.3)
            {
                RenderCamera zoom = Refit(v.Camera, best);
                if (zoom != null)
                {
                    RenderBuffers b2;
                    ViewResult r2 = RenderOnce(scene, v, zoom, out b2);
                    if (r2 != null && r2.Metrics.Score >= r.Metrics.Score)
                    {
                        r = r2;
                        best = b2;
                    }
                }
            }
            // A grip with parts: the side that shows most of them (front-side buttons were hidden from the thumb's side).
            bool grip = v.Kind == "device" && v.Callouts.Count > 0;
            string altDump = Environment.GetEnvironmentVariable("JOYPRO_PICTURE_DEBUG_ALTS");
            int altNo = 0;
            foreach (RenderCamera alt in v.Alternatives)
            {
                if (grip && r != null)
                {
                    RenderBuffers bg;
                    ViewResult rg = RenderOnce(scene, v, alt, out bg);
                    // JOYPRO_PICTURE_DEBUG_ALTS=<folder>: every side of a grip as its own picture (for checking parts).
                    if (!string.IsNullOrEmpty(altDump) && rg != null)
                    {
                        Directory.CreateDirectory(altDump);
                        PngWriter.Write(Path.Combine(altDump, FileName(v.Id) + "-alt" + (++altNo).ToString(CultureInfo.InvariantCulture) + ".png"), rg.Rgba, rg.Width, rg.Height, 4);
                    }
                    if (rg != null && rg.Metrics.Score >= PictureQuality.Keep && (rg.Metrics.Visible > r.Metrics.Visible + 0.01 || (rg.Metrics.Visible >= r.Metrics.Visible - 0.01 && rg.Metrics.Score > r.Metrics.Score + 10)))
                    {
                        r = rg;
                        best = bg;
                    }
                    continue;
                }
                if (r != null && r.Metrics.Score >= PictureQuality.Good)
                    break;
                RenderBuffers b2;
                ViewResult r2 = RenderOnce(scene, v, alt, out b2);
                if (r2 != null && (r == null || r2.Metrics.Score > r.Metrics.Score + 3))
                {
                    r = r2;
                    best = b2;
                }
            }
            if (r != null && grip && r.Metrics.Fill < 0.3 && r.Camera != v.Camera)
            {
                RenderCamera zoom = Refit(r.Camera, best);
                if (zoom != null)
                {
                    RenderBuffers b2;
                    ViewResult r2 = RenderOnce(scene, v, zoom, out b2);
                    if (r2 != null && r2.Metrics.Score >= r.Metrics.Score && r2.Metrics.Visible >= r.Metrics.Visible - 0.01)
                    {
                        r = r2;
                        best = b2;
                    }
                }
            }
            if (r == null)
                return null;
            if (!keepAll && v.Kind != "overview" && r.Metrics.Score < PictureQuality.Keep)
                return null;
            // JoyPro's drawing of it.
            r.Art = LineArtTracer.Trace(best, r.Camera.Ortho, 2, new LineArtTracer.Settings { Labels = v.Kind != "overview" });
            return r;
        }

        private static ViewResult RenderOnce(CockpitScene scene, CockpitViewPlan v, RenderCamera camera, out RenderBuffers b)
        {
            RenderCamera big = camera.Scaled(2);
            b = SoftwareRenderer.Render(scene, big, v.Meshes, LightFor(v));
            // A picture with (almost) nothing on it (a grip the model hides at rest) is left out.
            int coveredPixels = 0;
            for (int i = 0; i < b.Covered.Length && coveredPixels < 2000; i++)
            {
                if (b.Covered[i])
                    coveredPixels++;
            }
            if (coveredPixels < 2000 && v.Kind != "overview")
                return null;
            // The picture: the shaded render, transparent where nothing is.
            int w = camera.Width, h = camera.Height;
            byte[] rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int cov = 0, red = 0, g = 0, bl = 0;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int i = (2 * y + dy) * b.Width + 2 * x + dx;
                            if (!b.Covered[i])
                                continue;
                            cov++;
                            red += b.Color[3 * i];
                            g += b.Color[3 * i + 1];
                            bl += b.Color[3 * i + 2];
                        }
                    }
                    int o = (y * w + x) * 4;
                    if (cov > 0)
                    {
                        rgba[o] = (byte)(red / cov);
                        rgba[o + 1] = (byte)(g / cov);
                        rgba[o + 2] = (byte)(bl / cov);
                        rgba[o + 3] = (byte)(cov * 255 / 4);
                    }
                }
            }
            Expose(rgba);
            // Callouts on the picture (0..1), with whether something covers them.
            List<double[]> at = new List<double[]>();
            int visible = 0;
            foreach (CockpitCallout c in v.Callouts)
            {
                double sx, sy, depth;
                bool ok = camera.Project(c.Position, out sx, out sy, out depth);
                bool hidden = false, inside = false;
                if (ok)
                {
                    int px = (int)(sx * 2), py = (int)(sy * 2);
                    if (px >= 0 && py >= 0 && px < b.Width && py < b.Height)
                    {
                        inside = true;
                        float d = b.Depth[py * b.Width + px];
                        hidden = b.Covered[py * b.Width + px] && d < depth - Math.Max(0.03, depth * 0.04);
                    }
                }
                // On a grip picture a part must sit on the grip's surface (a model's part whose place is only its node's
                // pivot can float beside the grip): else it is listed without a place, not drawn at a wrong one.
                if (ok && inside && v.Kind == "device" && !OnSurface(b, sx * 2, sy * 2, depth))
                {
                    at.Add(new[] { -1.0, -1.0, 1 });
                    continue;
                }
                if (inside && !hidden)
                    visible++;
                at.Add(ok ? new[] { sx / w, sy / h, hidden ? 1 : 0 } : new[] { -1.0, -1.0, 1 });
            }
            PictureMetrics m = PictureQuality.Measure(rgba, w, h, 4);
            m.Textured = PictureQuality.TexturedShare(b, scene);
            if (!camera.Ortho)
            {
                long drawn = 0, near = 0;
                for (int i = 0; i < b.Covered.Length; i += 5)
                {
                    if (!b.Covered[i])
                        continue;
                    drawn++;
                    if (b.Depth[i] < camera.Near + 0.2)
                        near++;
                }
                m.Near = drawn == 0 ? 0 : near / (double)drawn;
            }
            // The overview's callouts are the panels' places (some always behind a seat or a lever): not counted.
            m.Visible = v.Callouts.Count == 0 || v.Kind == "overview" ? 1 : visible / (double)v.Callouts.Count;
            PictureQuality.Rate(m, v.Kind);
            ViewResult r = new ViewResult { View = v, Camera = camera, Rgba = rgba, Width = w, Height = h, Metrics = m, At = at };
            // A small depth map for merging duplicates.
            int dw = Math.Max(1, w / 4), dh = Math.Max(1, h / 4);
            float[] dm = new float[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                for (int x = 0; x < dw; x++)
                {
                    int i = (y * 8) * b.Width + x * 8;
                    dm[y * dw + x] = i < b.Depth.Length && b.Covered[i] ? b.Depth[i] : float.PositiveInfinity;
                }
            }
            r.Depth = dm;
            r.DepthW = dw;
            r.DepthH = dh;
            return r;
        }

        // A dark picture (black panels, a dim cabin) brightened like a photo's exposure: the brightest 5 % of the model
        // reach about 225 of 255, by at most 1.5 times.
        internal static void Expose(byte[] rgba)
        {
            int[] hist = new int[256];
            int n = 0;
            for (int o = 0; o + 3 < rgba.Length; o += 4)
            {
                if (rgba[o + 3] < 128)
                    continue;
                hist[(rgba[o] * 77 + rgba[o + 1] * 150 + rgba[o + 2] * 29) >> 8]++;
                n++;
            }
            if (n < 100)
                return;
            int acc = 0, p95 = 255;
            for (int i = 255; i >= 0; i--)
            {
                acc += hist[i];
                if (acc >= n / 20)
                {
                    p95 = Math.Max(1, i);
                    break;
                }
            }
            double gain = Math.Max(1.0, Math.Min(1.5, 225.0 / p95));
            if (gain < 1.03)
                return;
            for (int o = 0; o + 3 < rgba.Length; o += 4)
            {
                if (rgba[o + 3] == 0)
                    continue;
                for (int c = 0; c < 3; c++)
                    rgba[o + c] = (byte)Math.Min(255, (int)(rgba[o + c] * gain + 0.5));
            }
        }

        // The light of a picture: a panel or a grip is lit mostly from the camera (as a manual's figure or a photo with a
        // flash): a vertical panel under the top light alone was drawn at 60 % of its colours, a black panel nearly black.
        // The overview keeps the cabin's top light with a little more from the camera.
        internal static SoftwareRenderer.Light LightFor(CockpitViewPlan v)
        {
            if (v.Kind == "overview")
                return new SoftwareRenderer.Light { Ambient = 0.42, Diffuse = 0.58, Headlight = 0.28 };
            return new SoftwareRenderer.Light { Ambient = 0.42, Diffuse = 0.36, Headlight = 0.42 };
        }

        // Whether the render drew a surface within a few pixels of a point at about its depth (in front of it or up to a
        // few centimetres behind: the part's own face).
        internal static bool OnSurface(RenderBuffers b, double px, double py, double depth)
        {
            int r = Math.Max(4, b.Width / 160);
            int cx = (int)px, cy = (int)py;
            for (int y = Math.Max(0, cy - r); y <= Math.Min(b.Height - 1, cy + r); y++)
            {
                for (int x = Math.Max(0, cx - r); x <= Math.Min(b.Width - 1, cx + r); x++)
                {
                    int i = y * b.Width + x;
                    if (b.Covered[i] && b.Depth[i] < depth + Math.Max(0.03, depth * 0.05))
                        return true;
                }
            }
            return false;
        }

        // A perspective (or orthographic) camera turned and narrowed to the box around what the render drew.
        internal static RenderCamera Refit(RenderCamera cam, RenderBuffers b)
        {
            int[] cols = new int[b.Width], rows = new int[b.Height];
            int covered = 0;
            for (int y = 0; y < b.Height; y++)
            {
                for (int x = 0; x < b.Width; x++)
                {
                    if (!b.Covered[y * b.Width + x])
                        continue;
                    cols[x]++;
                    rows[y]++;
                    covered++;
                }
            }
            if (covered < 200)
                return null;
            int lo = covered / 50;
            int x0 = Edge(cols, lo, false), x1 = Edge(cols, lo, true), y0 = Edge(rows, lo, false), y1 = Edge(rows, lo, true);
            // In the camera's own pixels.
            double s = cam.Width / (double)b.Width;
            double cx = (x0 + x1 + 1) / 2.0 * s, cy = (y0 + y1 + 1) / 2.0 * s;
            double bw = Math.Max(4, (x1 - x0 + 1) * s), bh = Math.Max(4, (y1 - y0 + 1) * s);
            double aspect = Math.Max(0.7, Math.Min(1.4, bw / bh));
            RenderCamera c = new RenderCamera { Ortho = cam.Ortho, Width = cam.Width, Near = cam.Near };
            c.Height = (int)Math.Round(cam.Width / aspect);
            double margin = 1.12;
            if (cam.Ortho)
            {
                double unit = cam.HalfHeight * 2 / cam.Height;
                c.Forward = cam.Forward;
                c.Up = cam.Up;
                c.Right = cam.Right;
                c.Eye = cam.Eye + cam.Right * ((cx - cam.Width / 2.0) * unit) - cam.Up * ((cy - cam.Height / 2.0) * unit);
                c.HalfHeight = Math.Max(bh / 2, bw / 2 / aspect) * unit * margin;
                return c;
            }
            double f = cam.Focal;
            Vec3 dir = cam.Forward * f + cam.Right * (cx - cam.Width / 2.0) - cam.Up * (cy - cam.Height / 2.0);
            c.Eye = cam.Eye;
            c.Aim(dir);
            // Half the box in the old focal units, as an angle; a little extra for the turn.
            double half = Math.Max(bh / 2, bw / 2 / aspect) / f * margin;
            c.FovY = Math.Max(4, Math.Min(100, 2 * Math.Atan(half) * 180 / Math.PI));
            return c;
        }

        private static int Edge(int[] counts, int skip, bool fromEnd)
        {
            int acc = 0;
            if (!fromEnd)
            {
                for (int i = 0; i < counts.Length; i++)
                {
                    acc += counts[i];
                    if (acc > skip)
                        return i;
                }
                return 0;
            }
            for (int i = counts.Length - 1; i >= 0; i--)
            {
                acc += counts[i];
                if (acc > skip)
                    return i;
            }
            return counts.Length - 1;
        }

        // The callouts of a duplicate picture (dup) onto the kept one: each projected into the kept picture; false (and
        // nothing changed) when one of them is outside its frame.
        private static bool Merge(CockpitViewPlan keepView, ViewResult keep, CockpitViewPlan dupView, ViewResult dup)
        {
            List<double[]> added = new List<double[]>();
            foreach (CockpitCallout c in dupView.Callouts)
            {
                double sx, sy, depth;
                if (!keep.Camera.Project(c.Position, out sx, out sy, out depth) || sx < 0 || sy < 0 || sx >= keep.Width || sy >= keep.Height)
                    return false;
                int px = Math.Min(keep.DepthW - 1, (int)(sx / 4)), py = Math.Min(keep.DepthH - 1, (int)(sy / 4));
                float d = keep.Depth[py * keep.DepthW + px];
                bool hidden = !float.IsInfinity(d) && d < depth - Math.Max(0.03, depth * 0.04);
                added.Add(new[] { sx / keep.Width, sy / keep.Height, hidden ? 1 : 0 });
            }
            int n = keepView.Callouts.Count == 0 ? 0 : keepView.Callouts.Max(c => c.N);
            for (int i = 0; i < dupView.Callouts.Count; i++)
            {
                CockpitCallout c = dupView.Callouts[i];
                if (keepView.Callouts.Any(x => x.Id == c.Id && x.Label == c.Label))
                    continue;
                c.N = ++n;
                keepView.Callouts.Add(c);
                keep.At.Add(added[i]);
            }
            return true;
        }

        // JoyPro's line drawing is a poster of the render: readable for a clear grip or panel, a mess for a busy
        // perspective (a whole cockpit) or a poor picture. Then the page shows the picture in its place.
        internal static bool DrawingReadable(CockpitViewPlan v, ViewResult r)
        {
            if (r.Art == null || r.Metrics.Score < PictureQuality.Good)
                return false;
            double mp = Math.Max(0.01, r.Art.Width * r.Art.Height / 1e6);
            int lines = CountMoves(r.Art.Outline) + CountMoves(r.Art.Detail) + CountMoves(r.Art.Minor);
            double perMp = lines / mp;
            return perMp <= (v.Kind == "overview" ? 1500 : 2200);
        }

        private static int CountMoves(string d)
        {
            int n = 0;
            foreach (char c in d ?? string.Empty)
            {
                if (c == 'M')
                    n++;
            }
            return n;
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
