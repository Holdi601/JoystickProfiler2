#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.CockpitPictures
{
    // A control the game makes clickable (or a grip's part), with where it is in the model and what it does.
    internal sealed class CockpitCallout
    {
        public string Id = string.Empty;          // the game's element name (connector), or the control id on a grip
        public string Label = string.Empty;       // the game's hint text ("MASTER ARM Switch, MASTER ARM/OFF/SIMULATE")
        public string Panel = string.Empty;       // the panel it belongs to (a key)
        public string PanelTitle = string.Empty;  // the panel's name
        public string Area = string.Empty;        // left-console, front-center ... (JoyPro's areas)
        public Vec3 Position;
        public string Kind = string.Empty;        // switch, button, knob, lever ... (the game's class)
        public string Device = string.Empty;      // the game's device (system) name
        public List<string> Actions = new List<string>();
        public List<string> Args = new List<string>();
        public List<CalloutCommand> Commands = new List<CalloutCommand>();
        public int N;
        // The animation behind the control where the game's files name it (draw argument, dataref, node ...), and the
        // game's own kind of control (DCS: TUMB, LEV; X-Plane: the manipulator; MSFS: the behaviour template).
        public ControlAnimation Animation;
        public string GameKind = string.Empty;
        public int Seat = 1;
    }

    // What moves when a control is used, as the game's files say (pictures.json "controls", docs/cockpits.md
    // "Animated controls"): where it comes from (draw argument, dataref, node, event, interaction, input action), its
    // name there, the range and the positions of a switch (NaN / empty: not known).
    internal sealed class ControlAnimation
    {
        public string Source = string.Empty;
        public string Name = string.Empty;
        public double Min = double.NaN;
        public double Max = double.NaN;
        public List<double> Positions = new List<double>();
    }

    // One vocabulary for the kinds of controls of every game: button, switch-2, switch-3, switch-N (more positions),
    // switch (positions not known), rotary, lever, axis, grip, hat, trigger, screen, cover, wheel, pedal, other.
    internal static class ControlKinds
    {
        public static string Of(CockpitCallout c)
        {
            string k = ((c.Kind ?? string.Empty) + " " + (c.GameKind ?? string.Empty)).ToLowerInvariant();
            int positions = c.Animation == null ? 0 : c.Animation.Positions.Count;
            // A part on a grip: by its name (hat, trigger, a thumb stick, else a button).
            if (k.Trim() == "part" || k.Trim() == "part part")
            {
                string n = ((c.Id ?? string.Empty) + " " + (c.Label ?? string.Empty)).ToLowerInvariant();
                if (System.Text.RegularExpressions.Regex.IsMatch(n, "trigger|trg|pickle"))
                    return "trigger";
                if (System.Text.RegularExpressions.Regex.IsMatch(n, @"hat|tms|dms|cms|coolie|slew|cursor|\bcur\b|ministick|thumb|jstck|joystick|castle"))
                    return "hat";
                if (System.Text.RegularExpressions.Regex.IsMatch(n, @"lever|paddle|lvr|switch|sw\b|rng|antn|shift"))
                    return "switch";
                return "button";
            }
            if (k.Contains("screen") || k.Contains("display") || k.Contains("mfd"))
                return "screen";
            if (k.Contains("cover") || k.Contains("guard"))
                return "cover";
            if (k.Contains("grip") || k.Contains("stick") || k.Contains("yoke") || k.Contains("cyclic"))
                return "grip";
            if (k.Contains("pedal"))
                return "pedal";
            if (k.Contains("wheel") && !k.Contains("button"))
                return "wheel";
            if (k.Contains("button") || k.Contains("push") || k.Contains("btn") || k.Contains("command ") || k.Trim() == "command")
                return positions >= 3 ? "switch-" + positions : "button";
            if (k.Contains("switch") || k.Contains("tumb") || k.Contains("toggle") || k.Contains("radio"))
                return positions == 2 ? "switch-2" : positions == 3 ? "switch-3" : positions > 3 ? "switch-" + positions : "switch";
            if (k.Contains("knob") || k.Contains("rotary") || k.Contains("lev,") || k.EndsWith(" lev") || k.Contains("axis_knob") || k.Contains("wrap") || k.Contains("delta"))
                return positions >= 2 && positions <= 12 ? "switch-" + positions : "rotary";
            if (k.Contains("lever") || k.Contains("throttle") || k.Contains("handle") || k.Contains("movable_lev") || k.Contains("drag"))
                return "lever";
            if (k.Contains("axis"))
                return "axis";
            if (positions == 2)
                return "switch-2";
            if (positions == 3)
                return "switch-3";
            return k.Trim().Length == 0 ? "other" : (c.Kind ?? "other").ToLowerInvariant();
        }
    }

    internal sealed class CalloutCommand
    {
        public string Name = string.Empty;
        public string Category = string.Empty;
        public string Id = string.Empty;
    }

    // A grip (stick, throttle ...) of the model: its meshes and the named parts on it.
    internal sealed class CockpitGrip
    {
        public string Device = "stick";
        // What moves the grip in the game's files (the stick's arguments, the yoke's datarefs), where the reader knows it.
        public ControlAnimation Animation;
        public List<int> Meshes = new List<int>();
        public List<CockpitCallout> Parts = new List<CockpitCallout>();
        public Vec3 Pivot;
    }

    // One picture to make: a camera, the meshes, the callouts on it.
    internal sealed class CockpitViewPlan
    {
        public string Id = string.Empty;         // overview, panel:<key>, device:<device>
        public string Kind = string.Empty;       // overview, panel, device
        public string Title = string.Empty;
        public string Area = string.Empty;
        public string Device = string.Empty;
        public int Seat = 1;
        public RenderCamera Camera;
        public List<int> Meshes;                 // null = all
        // Other cameras for the same picture, tried in order while the picture is poor (PictureWriter's quality gate).
        public List<RenderCamera> Alternatives = new List<RenderCamera>();
        // A grip picture: the grip itself as a control (pictures.json "controls": the stick, the throttle).
        public CockpitCallout GripControl;
        public List<CockpitCallout> Callouts = new List<CockpitCallout>();
        // The overview's callouts lead to these views (panel keys), in callout order.
        public List<string> Links = new List<string>();
    }

    // Plans the pictures of a cockpit: the overview from behind the pilot's head, a frontal picture of every panel
    // (clickable controls grouped by panel, big groups split by place), and every grip on its own.
    internal static class CockpitViews
    {
        public const int MaxPerPanel = 36;
        public const double MaxPanelExtent = 0.45;

        public static List<CockpitViewPlan> Plan(CockpitScene scene, Vec3 head, List<CockpitCallout> callouts, List<CockpitGrip> grips, int width)
        {
            List<CockpitViewPlan> views = new List<CockpitViewPlan>();
            // Controls more than 4 m from the pilot are not in this cockpit (unplaced connectors, another crew station).
            callouts = callouts.Where(c => (c.Position - head).Length <= 4.0).ToList();
            List<List<CockpitCallout>> panels = Panels(callouts, head);
            // The grips nearest the pilot first; a second grip of a kind (the other pilot's, a second lever) gets its own id.
            grips = grips.OrderBy(g => (g.Pivot - head).Length).ToList();
            Dictionary<string, int> kinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<CockpitGrip, string> gripIds = new Dictionary<CockpitGrip, string>();
            foreach (CockpitGrip g in grips)
            {
                int count;
                kinds.TryGetValue(g.Device, out count);
                kinds[g.Device] = ++count;
                gripIds[g] = "device:" + g.Device + (count > 1 ? "-" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
            }
            // Panel pictures leave the grips out (a stick or a throttle in front of a console hides it).
            HashSet<int> gripMeshes = new HashSet<int>(grips.SelectMany(g => g.Meshes));
            List<int> panelMeshes = gripMeshes.Count == 0 ? null : Enumerable.Range(0, scene.Meshes.Count).Where(i => !gripMeshes.Contains(i)).ToList();
            // Panel views.
            List<CockpitViewPlan> panelViews = new List<CockpitViewPlan>();
            foreach (List<CockpitCallout> group in panels)
            {
                CockpitViewPlan v = PanelView(scene, head, group, width);
                if (v != null)
                {
                    v.Meshes = panelMeshes;
                    panelViews.Add(v);
                }
            }
            // Panels that share a name (one panel split by place, or the same area) are numbered.
            foreach (IGrouping<string, CockpitViewPlan> same in panelViews.GroupBy(x => x.Title, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                int k = 1;
                foreach (CockpitViewPlan pv in same.OrderByDescending(x => x.Camera.Eye.X).ThenBy(x => x.Camera.Eye.Z))
                    pv.Title += " (" + (k++).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            }
            // Overview: every panel and grip in one picture from behind the head.
            List<Vec3> all = callouts.Select(c => c.Position).ToList();
            foreach (CockpitGrip g in grips)
                all.Add(g.Pivot);
            if (all.Count == 0)
            {
                Vec3 mn, mx;
                scene.Bounds(Enumerable.Range(0, scene.Meshes.Count), out mn, out mx);
                all.Add((mn + mx) * 0.5);
            }
            CockpitViewPlan overview = OverviewView(head, all, width, 0.32, 0.22);
            // Closer to the head (a big cabin seen from too far back) and further back (a head inside an instrument).
            overview.Alternatives.Add(OverviewView(head, all, width, 0.12, 0.12).Camera);
            overview.Alternatives.Add(OverviewView(head, all, width, 0.0, 0.06).Camera);
            overview.Alternatives.Add(OverviewView(head, all, width, 0.6, 0.35).Camera);
            int n = 1;
            foreach (CockpitViewPlan pv in panelViews.OrderBy(p => AreaOrder(p.Area)).ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase))
            {
                Vec3 centre = Centre(pv.Callouts.Select(c => c.Position).ToList());
                overview.Callouts.Add(new CockpitCallout { Id = pv.Id, Label = pv.Title, Position = centre, N = n++, Area = pv.Area });
                overview.Links.Add(pv.Id);
            }
            foreach (CockpitGrip g in grips)
            {
                overview.Callouts.Add(new CockpitCallout { Id = gripIds[g], Label = Title(g.Device), Position = g.Pivot, N = n++ });
                overview.Links.Add(gripIds[g]);
            }
            views.Add(overview);
            foreach (CockpitGrip g in grips)
            {
                CockpitViewPlan gv = GripView(scene, head, g, width);
                if (gv != null)
                {
                    gv.Id = gripIds[g];
                    views.Add(gv);
                }
            }
            views.AddRange(panelViews.OrderBy(p => AreaOrder(p.Area)).ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase));
            return views;
        }

        public static string Title(string device)
        {
            if (string.IsNullOrEmpty(device))
                return string.Empty;
            return char.ToUpperInvariant(device[0]) + device.Substring(1).Replace('-', ' ');
        }

        private static int AreaOrder(string area)
        {
            string[] order = { "glareshield", "front-left", "front-center", "front-right", "left-console", "center-pedestal", "right-console", "overhead", "left-side", "right-side", "floor", "rear", "seat" };
            int i = Array.IndexOf(order, area);
            return i < 0 ? order.Length : i;
        }

        // The per-axis median (robust against a few points far away).
        public static Vec3 Median(List<Vec3> pts)
        {
            if (pts.Count == 0)
                return new Vec3(0, 0, 0);
            Func<IEnumerable<double>, double> med = v =>
            {
                List<double> l = v.OrderBy(x => x).ToList();
                return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
            };
            return new Vec3(med(pts.Select(p => p.X)), med(pts.Select(p => p.Y)), med(pts.Select(p => p.Z)));
        }

        // Leaves out the callouts farther than maxDistance from the median of all; returns how many.
        public static int DropOutliers(List<CockpitCallout> callouts, double maxDistance)
        {
            if (callouts.Count < 4)
                return 0;
            Vec3 m = Median(callouts.Select(c => c.Position).ToList());
            return callouts.RemoveAll(c => (c.Position - m).Length > maxDistance || double.IsNaN(c.Position.X));
        }

        public static Vec3 Centre(List<Vec3> pts)
        {
            if (pts.Count == 0)
                return new Vec3(0, 0, 0);
            Vec3 s = new Vec3(0, 0, 0);
            foreach (Vec3 p in pts)
                s = s + p;
            return s * (1.0 / pts.Count);
        }

        // Where a point is, seen from the head: JoyPro's area names.
        public static string AreaOf(Vec3 p, Vec3 head)
        {
            // Left and right of the aircraft's centre line (a side-by-side cockpit's panel is one panel).
            Vec3 d = p - head;
            if (Math.Abs(head.Z) < 0.7)
                d.Z = p.Z;
            if (d.Y > 0.12 && d.X < 0.45)
                return "overhead";
            if (d.X < -0.05 && Math.Abs(d.Z) < 0.35)
                return "rear";
            if (d.Z < -0.25 && d.X < 0.45)
                return d.Y < -0.25 ? "left-console" : "left-side";
            if (d.Z > 0.25 && d.X < 0.45)
                return d.Y < -0.25 ? "right-console" : "right-side";
            if (d.Y < -0.62 && d.X < 0.6)
                return "center-pedestal";
            if (d.Y > -0.12)
                return "glareshield";
            if (d.Z < -0.12)
                return "front-left";
            if (d.Z > 0.12)
                return "front-right";
            return "front-center";
        }

        // ------------------------------------------------------------------ panels

        private static List<List<CockpitCallout>> Panels(List<CockpitCallout> callouts, Vec3 head)
        {
            List<List<CockpitCallout>> result = new List<List<CockpitCallout>>();
            foreach (IGrouping<string, CockpitCallout> g in callouts.GroupBy(c => c.Panel, StringComparer.OrdinalIgnoreCase))
            {
                foreach (List<CockpitCallout> part in Split(g.ToList()))
                    result.Add(part);
            }
            // Tiny groups (one or two controls) join the nearest group nearby, so a cockpit is a few dozen pages and not
            // hundreds.
            bool merged = true;
            while (merged)
            {
                merged = false;
                foreach (List<CockpitCallout> small in result.Where(g => g.Count <= 2).OrderBy(g => g.Count).ToList())
                {
                    Vec3 c = Centre(small.Select(x => x.Position).ToList());
                    List<CockpitCallout> near = result.Where(g => g != small && g.Count + small.Count <= MaxPerPanel)
                        .OrderBy(g => (Centre(g.Select(x => x.Position).ToList()) - c).Length).FirstOrDefault();
                    if (near == null || (Centre(near.Select(x => x.Position).ToList()) - c).Length > 0.16)
                        continue;
                    string title = near.Count >= small.Count ? near[0].PanelTitle : small[0].PanelTitle;
                    string key = near.Count >= small.Count ? near[0].Panel : small[0].Panel;
                    near.AddRange(small);
                    foreach (CockpitCallout x in near)
                    {
                        x.PanelTitle = title;
                        x.Panel = key;
                    }
                    result.Remove(small);
                    merged = true;
                    break;
                }
            }
            // Name and area of each group.
            foreach (List<CockpitCallout> group in result)
            {
                Vec3 c = Centre(group.Select(x => x.Position).ToList());
                string area = AreaOf(c, head);
                foreach (CockpitCallout x in group)
                    x.Area = area;
            }
            return result;
        }

        // Splits a group whose controls lie far apart or are too many for one picture (k-means in two).
        private static IEnumerable<List<CockpitCallout>> Split(List<CockpitCallout> group)
        {
            Vec3 mn = group.Select(c => c.Position).Aggregate(Vec3.Min), mx = group.Select(c => c.Position).Aggregate(Vec3.Max);
            Vec3 ext = mx - mn;
            double extent = Math.Max(ext.X, Math.Max(ext.Y, ext.Z));
            if (group.Count <= 1 || (group.Count <= MaxPerPanel && extent <= MaxPanelExtent))
            {
                yield return group;
                yield break;
            }
            // Split along the longest axis at the largest gap near the middle, else at the median.
            Func<CockpitCallout, double> axis = ext.X >= ext.Y && ext.X >= ext.Z ? (Func<CockpitCallout, double>)(c => c.Position.X) : ext.Y >= ext.Z ? (c => c.Position.Y) : (Func<CockpitCallout, double>)(c => c.Position.Z);
            List<CockpitCallout> sorted = group.OrderBy(axis).ToList();
            int cut = sorted.Count / 2;
            double bestGap = -1;
            for (int i = Math.Max(1, sorted.Count / 4); i < Math.Min(sorted.Count, sorted.Count * 3 / 4 + 1); i++)
            {
                double gap = axis(sorted[i]) - axis(sorted[i - 1]);
                if (gap > bestGap)
                {
                    bestGap = gap;
                    cut = i;
                }
            }
            List<CockpitCallout> a = sorted.Take(cut).ToList(), b = sorted.Skip(cut).ToList();
            string title = group[0].PanelTitle;
            foreach (List<CockpitCallout> half in new[] { a, b })
            {
                foreach (List<CockpitCallout> part in Split(half))
                    yield return part;
            }
        }

        private static CockpitViewPlan PanelView(CockpitScene scene, Vec3 head, List<CockpitCallout> group, int width)
        {
            if (group.Count == 0)
                return null;
            List<Vec3> pts = group.Select(c => c.Position).ToList();
            Vec3 c0 = Centre(pts);
            Vec3 toHead = (head - c0).Normalized;
            Vec3 normal = PlaneNormal(pts);
            if (normal.Length < 0.5)
                normal = toHead;
            if (Vec3.Dot(normal, toHead) < 0)
                normal = normal * -1;
            // Mostly frontal, a little towards the pilot (as the manual's figures).
            Vec3 view = (normal * 0.8 + toHead * 0.2).Normalized;
            RenderCamera cam = new RenderCamera { Ortho = true };
            cam.Forward = view * -1;
            // Up: the world's up on the panel, or forward (+x) for panels seen from above (consoles).
            Vec3 up = new Vec3(0, 1, 0);
            if (Math.Abs(Vec3.Dot(up, cam.Forward)) > 0.75)
                up = new Vec3(1, 0, 0);
            cam.Right = Vec3.Cross(cam.Forward, up).Normalized;
            cam.Up = Vec3.Cross(cam.Right, cam.Forward).Normalized;
            // Extent on the picture.
            double minR = double.MaxValue, maxR = double.MinValue, minU = double.MaxValue, maxU = double.MinValue, maxDepth = 0;
            foreach (Vec3 p in pts)
            {
                Vec3 d = p - c0;
                double r = Vec3.Dot(d, cam.Right), u = Vec3.Dot(d, cam.Up), f = Vec3.Dot(d, cam.Forward);
                minR = Math.Min(minR, r);
                maxR = Math.Max(maxR, r);
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                maxDepth = Math.Max(maxDepth, Math.Abs(f));
            }
            double margin = 0.045;
            double w = maxR - minR + 2 * margin, h = maxU - minU + 2 * margin;
            w = Math.Max(w, 0.24);
            h = Math.Max(h, 0.18);
            double aspect = Math.Max(0.75, Math.Min(2.2, w / h));
            Vec3 centre = c0 + cam.Right * ((minR + maxR) / 2) + cam.Up * ((minU + maxU) / 2);
            cam.Width = width;
            cam.Height = (int)Math.Round(width / aspect);
            cam.HalfHeight = Math.Max(h / 2, w / 2 / aspect);
            double distance = 1.5;
            cam.Eye = centre - cam.Forward * distance;
            // Cut away what lies between the camera and the panel (the stick, a seat edge, the other pilot).
            cam.Near = distance - maxDepth - 0.07;
            string title = group[0].PanelTitle.Length > 0 ? group[0].PanelTitle : group[0].Panel;
            CockpitViewPlan v = new CockpitViewPlan { Kind = "panel", Title = title, Area = group[0].Area, Camera = cam };
            // When the picture is poor: the same view with more room around the controls, then the pilot's own view of
            // them (from the head: nothing of the cockpit in between, the side the pilot sees).
            RenderCamera wide = new RenderCamera { Ortho = true, Forward = cam.Forward, Up = cam.Up, Right = cam.Right, Width = cam.Width, Height = cam.Height, HalfHeight = cam.HalfHeight * 2.2, Eye = cam.Eye, Near = cam.Near };
            v.Alternatives.Add(wide);
            RenderCamera pilot = PilotView(head, pts, width);
            if (pilot != null)
                v.Alternatives.Add(pilot);
            v.Id = "panel:" + Key(title + "-" + group[0].Area + "-" + Math.Round(c0.X * 100) + "-" + Math.Round(c0.Z * 100));
            // Callouts in reading order: top to bottom, left to right.
            int n = 1;
            foreach (CockpitCallout c in group.OrderBy(c => -Math.Round(Vec3.Dot(c.Position - centre, cam.Up) / 0.03)).ThenBy(c => Vec3.Dot(c.Position - centre, cam.Right)))
            {
                c.N = n++;
                v.Callouts.Add(c);
            }
            return v;
        }

        // The controls as the pilot sees them: from the head (a little in front of it), the field of view fitted to them.
        private static RenderCamera PilotView(Vec3 head, List<Vec3> pts, int width)
        {
            Vec3 c = Centre(pts);
            Vec3 dir = c - head;
            if (dir.Length < 0.08)
                return null;
            Vec3 eye = head + dir.Normalized * Math.Min(0.15, dir.Length * 0.3);
            RenderCamera cam = RenderCamera.LookAt(eye, c, 40, width, (int)Math.Round(width / 1.33));
            double maxX = 0, maxY = 0;
            foreach (Vec3 p in pts)
            {
                Vec3 d = p - eye;
                double z = Vec3.Dot(d, cam.Forward);
                if (z < 0.05)
                    return null;
                maxX = Math.Max(maxX, Math.Abs(Vec3.Dot(d, cam.Right)) / z);
                maxY = Math.Max(maxY, Math.Abs(Vec3.Dot(d, cam.Up)) / z);
            }
            double halfY = Math.Max(maxY, maxX / 1.33) * 1.25 + 0.08;
            cam.FovY = Math.Max(20, Math.Min(90, 2 * Math.Atan(halfY) * 180 / Math.PI));
            cam.Near = 0.05;
            return cam;
        }

        public static string Key(string s)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (char ch in (s ?? string.Empty).ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(ch);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-')
                    sb.Append('-');
            }
            return sb.ToString().Trim('-');
        }

        // The normal of the plane through the points (smallest axis of their spread), or zero when they lie on a line.
        public static Vec3 PlaneNormal(List<Vec3> pts)
        {
            if (pts.Count < 3)
                return new Vec3(0, 0, 0);
            Vec3 c = Centre(pts);
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (Vec3 p in pts)
            {
                Vec3 d = p - c;
                xx += d.X * d.X;
                xy += d.X * d.Y;
                xz += d.X * d.Z;
                yy += d.Y * d.Y;
                yz += d.Y * d.Z;
                zz += d.Z * d.Z;
            }
            double[,] m = { { xx, xy, xz }, { xy, yy, yz }, { xz, yz, zz } };
            double[] values;
            Vec3[] vectors;
            Eigen(m, out values, out vectors);
            // values sorted ascending
            if (values[1] < 1e-6)
                return new Vec3(0, 0, 0); // a line
            return vectors[0].Normalized;
        }

        // Jacobi eigen decomposition of a symmetric 3x3 matrix; ascending values.
        private static void Eigen(double[,] a, out double[] values, out Vec3[] vectors)
        {
            double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            a = (double[,])a.Clone();
            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
                if (off < 1e-15)
                    break;
                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-18)
                            continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0)
                            t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < 3; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - s * akq;
                            a[k, q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - s * aqk;
                            a[q, k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq;
                            v[k, q] = s * vkp + c * vkq;
                        }
                    }
                }
            }
            int[] order = new[] { 0, 1, 2 }.OrderBy(i => a[i, i]).ToArray();
            values = order.Select(i => a[i, i]).ToArray();
            vectors = order.Select(i => new Vec3(v[0, i], v[1, i], v[2, i])).ToArray();
        }

        // ------------------------------------------------------------------ overview

        private static CockpitViewPlan OverviewView(Vec3 head, List<Vec3> pts, int width, double back, double up)
        {
            // From behind and above the head (between the seats of a side-by-side cockpit), looking down at the middle
            // of the controls in front of the pilot; what lies behind the head does not widen the picture.
            Vec3 eye = new Vec3(head.X - back, head.Y + up, Math.Abs(head.Z) < 0.7 ? 0 : head.Z);
            List<Vec3> front = pts.Where(p => p.X >= head.X - 0.05).ToList();
            if (front.Count >= 3)
                pts = front;
            Vec3 aim = Centre(pts);
            int height = (int)Math.Round(width / 1.6);
            RenderCamera cam = RenderCamera.LookAt(eye, aim, 60, width, height);
            // Fit the field of view to the points (with room for the panels around them).
            double maxX = 0, maxY = 0;
            foreach (Vec3 p in pts)
            {
                Vec3 d = p - eye;
                double z = Vec3.Dot(d, cam.Forward);
                if (z < 0.05)
                    continue;
                maxX = Math.Max(maxX, Math.Abs(Vec3.Dot(d, cam.Right)) / z);
                maxY = Math.Max(maxY, Math.Abs(Vec3.Dot(d, cam.Up)) / z);
            }
            double halfY = Math.Max(maxY, maxX * height / (double)width) * 1.12 + 0.04;
            cam.FovY = Math.Max(40, Math.Min(105, 2 * Math.Atan(halfY) * 180 / Math.PI));
            // The seat's head rest right below the camera is cut away; in a side-by-side cockpit (the camera between the
            // seats) everything up to just in front of the heads, so the seat backs do not hide the panel.
            bool sideBySide = Math.Abs(head.Z - eye.Z) > 0.15;
            cam.Near = sideBySide ? Math.Max(0.3, (head.X + 0.1 - eye.X) / Math.Max(0.3, cam.Forward.X)) : 0.3;
            return new CockpitViewPlan { Id = "overview", Kind = "overview", Title = "Cockpit overview", Camera = cam };
        }

        // ------------------------------------------------------------------ grips

        // The meshes of a grip that belong together (boxes less than 12 cm apart); the group nearest the pilot. Two
        // yokes that move together, a left and a right throttle knob or a part far away made one tiny picture of both.
        internal static List<int> NearestCluster(CockpitScene scene, List<int> meshes, Vec3 head)
        {
            if (meshes.Count < 2)
                return meshes;
            int n = meshes.Count;
            Vec3[] lo = new Vec3[n], hi = new Vec3[n];
            for (int i = 0; i < n; i++)
                scene.Bounds(new[] { meshes[i] }, out lo[i], out hi[i]);
            int[] group = Enumerable.Range(0, n).ToArray();
            Func<int, int> find = null;
            find = i => group[i] == i ? i : (group[i] = find(group[i]));
            for (int i = 0; i < n; i++)
            {
                for (int k = i + 1; k < n; k++)
                {
                    double gx = Math.Max(0, Math.Max(lo[i].X - hi[k].X, lo[k].X - hi[i].X));
                    double gy = Math.Max(0, Math.Max(lo[i].Y - hi[k].Y, lo[k].Y - hi[i].Y));
                    double gz = Math.Max(0, Math.Max(lo[i].Z - hi[k].Z, lo[k].Z - hi[i].Z));
                    if (Math.Sqrt(gx * gx + gy * gy + gz * gz) < 0.12)
                        group[find(i)] = find(k);
                }
            }
            List<List<int>> clusters = Enumerable.Range(0, n).GroupBy(find).Select(c => c.Select(i => meshes[i]).ToList()).ToList();
            if (clusters.Count < 2)
                return meshes;
            return clusters.OrderBy(c =>
            {
                Vec3 a, b;
                scene.Bounds(c, out a, out b);
                return ((a + b) * 0.5 - head).Length;
            }).First();
        }

        private static CockpitViewPlan GripView(CockpitScene scene, Vec3 head, CockpitGrip g, int width)
        {
            if (g.Meshes.Count == 0)
                return null;
            List<int> kept = NearestCluster(scene, g.Meshes, head);
            if (kept.Count < g.Meshes.Count)
            {
                Vec3 a, b;
                scene.Bounds(kept, out a, out b);
                Vec3 margin = new Vec3(0.08, 0.08, 0.08);
                g = new CockpitGrip { Device = g.Device, Animation = g.Animation, Meshes = kept, Pivot = (a + b) * 0.5, Parts = g.Parts.Where(p => p.Position.X >= a.X - margin.X && p.Position.X <= b.X + margin.X && p.Position.Y >= a.Y - margin.Y && p.Position.Y <= b.Y + margin.Y && p.Position.Z >= a.Z - margin.Z && p.Position.Z <= b.Z + margin.Z).ToList() };
            }
            Vec3 mn, mx;
            scene.Bounds(g.Meshes, out mn, out mx);
            Vec3 c = (mn + mx) * 0.5;
            double size = (mx - mn).Length;
            if (size <= 0.001)
                return null;
            // From behind, above and the thumb's side: a right-hand grip from its left, a left-hand grip from its right.
            bool leftHand = g.Device == "throttle" || g.Device == "collective" || g.Device == "quadrant" || g.Device == "thc" || (c.Z < head.Z - 0.12 && g.Device != "stick" && g.Device != "cyclic");
            Vec3 dir = (leftHand ? new Vec3(-0.55, 0.55, 0.65) : new Vec3(-0.75, 0.62, -0.55)).Normalized;
            double fov = 30;
            double distance = size * 0.5 / Math.Tan(fov * Math.PI / 360) * 1.25;
            int height = (int)Math.Round(width * 1.0);
            RenderCamera cam = RenderCamera.LookAt(c + dir * distance, c, fov, width, height);
            // Fit to the projected box.
            double halfX = 0, halfY = 0;
            foreach (Vec3 corner in Corners(mn, mx))
            {
                Vec3 d = corner - cam.Eye;
                double z = Vec3.Dot(d, cam.Forward);
                halfX = Math.Max(halfX, Math.Abs(Vec3.Dot(d, cam.Right)) / z);
                halfY = Math.Max(halfY, Math.Abs(Vec3.Dot(d, cam.Up)) / z);
            }
            double aspect = Math.Max(0.7, Math.Min(1.4, halfX / Math.Max(1e-6, halfY)));
            cam.Height = (int)Math.Round(width / aspect);
            cam.FovY = 2 * Math.Atan(Math.Max(halfY, halfX / aspect) * 1.08) * 180 / Math.PI;
            cam.Near = 0.02;
            CockpitViewPlan v = new CockpitViewPlan { Id = "device:" + g.Device, Kind = "device", Device = g.Device, Title = Title(g.Device), Camera = cam, Meshes = g.Meshes };
            v.GripControl = new CockpitCallout { Id = "grip:" + g.Device, Label = Title(g.Device), Position = g.Pivot, Kind = "grip", Device = g.Device, Animation = g.Animation };
            int n = 1;
            foreach (CockpitCallout p in g.Parts)
            {
                p.N = n++;
                v.Callouts.Add(p);
            }
            return v;
        }

        private static IEnumerable<Vec3> Corners(Vec3 a, Vec3 b)
        {
            for (int i = 0; i < 8; i++)
                yield return new Vec3((i & 1) == 0 ? a.X : b.X, (i & 2) == 0 ? a.Y : b.Y, (i & 4) == 0 ? a.Z : b.Z);
        }
    }
}
