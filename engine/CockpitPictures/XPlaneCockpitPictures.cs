#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.CockpitPictures
{
    // X-Plane 12: the cockpit pictures of every aircraft in the Aircraft folder, made from its .acf (the objects, where
    // they sit, the pilot's eye) and its OBJ8 objects (open text: geometry, animations and the manipulators). The
    // callouts sit where the manipulators (the clickable parts) are; their functions are the manipulators' own tooltips
    // and the X-Plane commands or datarefs they work.
    internal static class XPlaneCockpitPictures
    {
        private const double Feet = 0.3048;
        internal static bool FlipDds = Environment.GetEnvironmentVariable("JOYPRO_XP_FLIPDDS") != "0";

        public sealed class Job
        {
            public string XPlaneRoot = string.Empty;
            public string OutRoot = string.Empty;
            public List<string> Only = new List<string>();
            public bool Force;
            public int Width = 1600;
            public string ViewFilter = string.Empty;
            public Action<int, int, string, string> Progress;
            public Func<bool> Cancelled;
        }

        public sealed class SetResult
        {
            public string Plane = string.Empty;
            public List<string> Planes = new List<string>();
            public string Folder = string.Empty;
            public int Views;
            public int Callouts;
            public bool Skipped;
            public string Error = string.Empty;
            public List<string> Notes = new List<string>();
        }

        public static string GameVersion(string root)
        {
            try
            {
                string log = Path.Combine(root, "Log.txt");
                if (File.Exists(log))
                {
                    using (StreamReader r = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                    {
                        for (int i = 0; i < 20; i++)
                        {
                            string line = r.ReadLine();
                            if (line == null)
                                break;
                            Match m = Regex.Match(line, "X-Plane (\\d+\\.\\d+[^ ]*)");
                            if (m.Success)
                                return m.Groups[1].Value;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return string.Empty;
        }

        public static List<string> Aircraft(string root)
        {
            string folder = Path.Combine(root, "Aircraft");
            if (!Directory.Exists(folder))
                return new List<string>();
            List<string> list = new List<string>();
            Collect(folder, 0, list);
            return list.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void Collect(string folder, int depth, List<string> list)
        {
            try
            {
                list.AddRange(Directory.GetFiles(folder, "*.acf"));
                if (depth < 3)
                {
                    foreach (string d in Directory.GetDirectories(folder))
                    {
                        string n = Path.GetFileName(d).ToLowerInvariant();
                        if (n == "objects" || n == "liveries" || n == "plugins" || n == "cockpit_3d" || n == "fmod" || n == "airfoils" || n == "weapons")
                            continue;
                        Collect(d, depth + 1, list);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public static Dictionary<string, string> ReadAcf(string file)
        {
            Dictionary<string, string> p = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines(file))
            {
                if (!line.StartsWith("P ", StringComparison.Ordinal))
                    continue;
                int sp = line.IndexOf(' ', 2);
                if (sp < 0)
                    continue;
                p[line.Substring(2, sp - 2)] = line.Substring(sp + 1).Trim();
            }
            return p;
        }

        private static double D(Dictionary<string, string> p, string key)
        {
            string v;
            double d;
            return p.TryGetValue(key, out v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        // The names an aircraft is known by: the file name, the name, the description, the ICAO code and the folder.
        public static List<string> Names(string acf, Dictionary<string, string> p)
        {
            List<string> names = new List<string> { Path.GetFileNameWithoutExtension(acf) };
            string v;
            foreach (string key in new[] { "acf/_name", "acf/_descrip", "acf/_ICAO" })
            {
                if (p.TryGetValue(key, out v) && v.Trim().Length > 0)
                    names.Add(v.Trim());
            }
            names.Add(Path.GetFileName(Path.GetDirectoryName(acf)));
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<SetResult> Build(Job job)
        {
            List<SetResult> results = new List<SetResult>();
            List<string> acfs = Aircraft(job.XPlaneRoot);
            string version = GameVersion(job.XPlaneRoot);
            int i = 0;
            foreach (string acf in acfs)
            {
                i++;
                if (job.Cancelled != null && job.Cancelled())
                    break;
                Dictionary<string, string> p;
                try
                {
                    p = ReadAcf(acf);
                }
                catch (Exception)
                {
                    continue;
                }
                List<string> names = Names(acf, p);
                // An AI-only model next to the flyable one ("A330_AI.acf": no cockpit of its own).
                if (IsAiOnly(acf, acfs))
                    continue;
                if (job.Only.Count > 0 && !job.Only.Any(o => names.Any(n => n.IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0)))
                    continue;
                string key = Regex.Replace(Path.GetFileNameWithoutExtension(acf), "[^A-Za-z0-9_\\-]", "_");
                string folder = Path.Combine(job.OutRoot, key);
                SetResult r = new SetResult { Plane = names[0], Planes = names, Folder = folder };
                results.Add(r);
                if (!job.Force && File.Exists(Path.Combine(folder, PictureWriter.ManifestName)) && File.GetLastWriteTimeUtc(Path.Combine(folder, PictureWriter.ManifestName)) > File.GetLastWriteTimeUtc(acf) &&
                    File.ReadAllText(Path.Combine(folder, PictureWriter.ManifestName)).Contains("\"generator\":" + PictureWriter.GeneratorVersion.ToString(CultureInfo.InvariantCulture)))
                {
                    r.Skipped = true;
                    continue;
                }
                if (job.Progress != null)
                    job.Progress(i, acfs.Count, "X-Plane 12, cockpit pictures", names[0]);
                try
                {
                    BuildOne(job, acf, p, folder, version, r);
                }
                catch (Exception ex)
                {
                    r.Error = ex.GetType().Name + ": " + ex.Message;
                }
            }
            DcsCockpitPictures.WriteIndex(job.OutRoot, results.Select(x => new DcsCockpitPictures.SetResult { Plane = x.Plane, Planes = x.Planes, Folder = x.Folder, Error = x.Error }).ToList());
            return results;
        }

        // Objects that are not the cockpit: glass, the figures (pilots, helmets, passengers; not the pilot's panels and
        // instruments such as "F-14D_panels_pilot.obj"), props, shadows, covers.
        internal static bool IsAiOnly(string acf, IEnumerable<string> all)
        {
            string name = Path.GetFileNameWithoutExtension(acf);
            if (!Regex.IsMatch(name, "[_\\- ]AI$", RegexOptions.IgnoreCase))
                return false;
            string dir = Path.GetDirectoryName(acf);
            return all.Any(o => !string.Equals(o, acf, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetDirectoryName(o), dir, StringComparison.OrdinalIgnoreCase));
        }

        private static readonly Regex SkipObject = new Regex("glass|broken|lights?\\.obj|_lit\\.|prop(_|\\.|disc)|bobble|shadow|reflect|wheel_?chock|tie_?down|cover", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // The figures: "pilot.obj", "n844x_pilotstill.obj", "Vans RV-10_piottrans.obj" (sic), "cockpit_pilots.obj",
        // "copilot_liner.obj", anything in a "crew" or "pilot" folder.
        private static readonly Regex FigureObject = new Regex("(^|[_\\- ])(co)?(pilots?|piots?|helmets?|passengers?|people|crew|figures?|persons?)(still|trans|static)?\\.obj$|^(co)?(pilots?|helmets?|passengers?)[_\\-]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FigureFolder = new Regex("^(crew|pilots?|copilots?|people|passengers?|figures?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static bool Skipped(string objectFile)
        {
            string file = (objectFile ?? string.Empty).Replace('/', '\\');
            string name = Path.GetFileName(file);
            if (SkipObject.IsMatch(name))
                return true;
            // The pilot's panels and instruments stay ("F-14D_panels_pilot.obj").
            if (Regex.IsMatch(name, "panel|instrument", RegexOptions.IgnoreCase))
                return false;
            return FigureObject.IsMatch(name) || FigureFolder.IsMatch(Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty));
        }

        private static void BuildOne(Job job, string acf, Dictionary<string, string> p, string folder, string version, SetResult r)
        {
            string acfDir = Path.GetDirectoryName(acf);
            string objects = Path.Combine(acfDir, "objects");
            CockpitScene scene = new CockpitScene();
            List<CockpitCallout> callouts = new List<CockpitCallout>();
            Dictionary<string, List<int>> grips = new Dictionary<string, List<int>>();
            Dictionary<string, int> materials = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<CockpitCallout, string> gripParts = new Dictionary<CockpitCallout, string>();
            bool helicopter = D(p, "acf/_is_helicopter") > 0;
            using (TextureLibrary lib = new TextureLibrary())
            {
                lib.AddFolder(acfDir, true);
                int count = 0;
                while (p.ContainsKey("_obja/" + count + "/_v10_att_file_stl"))
                    count++;
                int part = 0;
                for (int k = 0; k < count; k++)
                {
                    string rel = p["_obja/" + k + "/_v10_att_file_stl"].Replace('/', '\\');
                    string file = Path.GetFullPath(Path.Combine(objects, rel));
                    if (!File.Exists(file))
                        file = Path.GetFullPath(Path.Combine(acfDir, rel));
                    if (!File.Exists(file))
                        continue;
                    double ox = D(p, "_obja/" + k + "/_v10_att_x_acf_prt_ref") * Feet;
                    double oy = D(p, "_obja/" + k + "/_v10_att_y_acf_prt_ref") * Feet;
                    double oz = D(p, "_obja/" + k + "/_v10_att_z_acf_prt_ref") * Feet;
                    XPlaneObj obj;
                    try
                    {
                        obj = XPlaneObj.Read(file);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    bool draw = obj.Texture.Length > 0 && !Skipped(file);
                    int material = -1;
                    bool flipFromPng = true;
                    if (draw)
                    {
                        string texKey = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file), obj.Texture));
                        if (!materials.TryGetValue(texKey, out material))
                        {
                            string ddsPath = Path.ChangeExtension(texKey, ".dds");
                            TextureImage img = null;
                            bool dds = File.Exists(ddsPath);
                            try
                            {
                                string path = dds ? ddsPath : texKey;
                                if (File.Exists(path))
                                    img = TextureImage.Decode(File.ReadAllBytes(path), path, lib.MaxSize);
                            }
                            catch (Exception)
                            {
                                img = null;
                            }
                            SceneMaterial sm = new SceneMaterial { Name = Path.GetFileName(texKey), Albedo = img, AlphaTest = true, FlipV = FlipDds || !dds };
                            if (img != null)
                                sm.Color = new[] { img.Mean[0], img.Mean[1], img.Mean[2] };
                            material = scene.Materials.Count;
                            scene.Materials.Add(sm);
                            materials[texKey] = material;
                        }
                        flipFromPng = scene.Materials[material].FlipV;
                    }
                    int vcount = obj.Vertices.Length / 8;
                    foreach (XPlaneTris run in obj.Runs)
                    {
                        part++;
                        if (run.Offset < 0 || run.Count < 3 || run.Offset + run.Count > obj.Indices.Length)
                            continue;
                        // World positions of the run's corners.
                        List<int> corners = new List<int>(run.Count);
                        for (int q = 0; q < run.Count; q++)
                            corners.Add(obj.Indices[run.Offset + q]);
                        if (corners.Any(c => c < 0 || c >= vcount))
                            continue;
                        if (run.Manip != null && run.Manip.Clickable && run.Manip.Tooltip.Length + run.Manip.Commands.Count > 0)
                        {
                            Vec3 sum = new Vec3(0, 0, 0);
                            foreach (int c in corners.Distinct())
                                sum = sum + World(run.Matrix, obj.Vertices, c, ox, oy, oz);
                            Vec3 at = sum * (1.0 / corners.Distinct().Count());
                            CockpitCallout added = AddCallout(callouts, run.Manip, at);
                            string onGrip = GripOf(run.Datarefs, helicopter);
                            if (added != null && onGrip != null)
                                gripParts[added] = onGrip;
                        }
                        if (!draw || run.Hidden)
                            continue;
                        double[] nm = Mat4.NormalMatrix(run.Matrix);
                        Dictionary<int, int> remap = new Dictionary<int, int>();
                        List<float> pos = new List<float>(), nor = new List<float>(), uv = new List<float>();
                        List<int> idx = new List<int>(corners.Count);
                        foreach (int c in corners)
                        {
                            int m;
                            if (!remap.TryGetValue(c, out m))
                            {
                                m = remap.Count;
                                remap[c] = m;
                                Vec3 w = World(run.Matrix, obj.Vertices, c, ox, oy, oz);
                                pos.Add((float)w.X);
                                pos.Add((float)w.Y);
                                pos.Add((float)w.Z);
                                Vec3 n = Mat4.Direction(nm, obj.Vertices[8 * c + 3], obj.Vertices[8 * c + 4], obj.Vertices[8 * c + 5]);
                                n = new Vec3(-n.Z, n.Y, n.X).Normalized;
                                nor.Add((float)n.X);
                                nor.Add((float)n.Y);
                                nor.Add((float)n.Z);
                                uv.Add(obj.Vertices[8 * c + 6]);
                                uv.Add(flipFromPng ? 1 - obj.Vertices[8 * c + 7] : obj.Vertices[8 * c + 7]);
                            }
                            idx.Add(m);
                        }
                        int mesh = scene.Meshes.Count;
                        scene.Meshes.Add(new SceneMesh { Name = Path.GetFileName(file), Node = k, Material = material, Part = part, Positions = pos.ToArray(), Normals = nor.ToArray(), Uvs = uv.ToArray(), Indices = idx.ToArray() });
                        string grip = GripOf(run.Datarefs, helicopter);
                        if (grip != null)
                        {
                            List<int> list;
                            if (!grips.TryGetValue(grip, out list))
                                grips[grip] = list = new List<int>();
                            list.Add(mesh);
                        }
                    }
                }
                if (scene.Meshes.Count == 0)
                {
                    r.Error = "no cockpit objects could be read";
                    return;
                }
                // The pilot's eye (feet; x right, y up, z aft).
                Vec3 head = new Vec3(-D(p, "acf/_pe_xyz/2") * Feet, D(p, "acf/_pe_xyz/1") * Feet, D(p, "acf/_pe_xyz/0") * Feet);
                // Panels: by place (X-Plane names no panels); a control at a grip goes with the grip.
                List<CockpitGrip> gripList = new List<CockpitGrip>();
                foreach (KeyValuePair<string, List<int>> g in grips)
                {
                    // The pilot's own grip: two yokes or sidesticks (the A330, the 737), the front and rear throttles
                    // (the L-5) moved by one dataref made a group too big for a grip.
                    List<int> own = CockpitViews.NearestCluster(scene, g.Value, head);
                    Vec3 mn, mx;
                    scene.Bounds(own, out mn, out mx);
                    // One triangle run with both yokes (the MD-82) or the front and rear throttles and their rod (the
                    // L-5): split into its connected parts first.
                    if ((mx - mn).Length > 1.5)
                    {
                        List<int> parts = g.Value.SelectMany(m => SplitParts(scene, m)).ToList();
                        own = CockpitViews.NearestCluster(scene, parts, head);
                        scene.Bounds(own, out mn, out mx);
                        // Still too big: the rod between the two throttles joins them.
                        if ((mx - mn).Length > 1.5)
                        {
                            List<int> noRods = parts.Where(m => !Il2CockpitPictures.IsRod(scene.Meshes[m])).ToList();
                            if (noRods.Count > 0)
                            {
                                own = CockpitViews.NearestCluster(scene, noRods, head);
                                scene.Bounds(own, out mn, out mx);
                            }
                        }
                    }
                    if (Environment.GetEnvironmentVariable("JOYPRO_PICTURE_DEBUG") == "1")
                        r.Notes.Add("debug grip " + g.Key + ": " + g.Value.Count + " meshes, nearest " + own.Count + " size " + (mx - mn).Length.ToString("0.00", CultureInfo.InvariantCulture) + " at " + ((mn + mx) * 0.5) + " (" + string.Join(", ", own.Select(m => scene.Meshes[m].Name).Distinct()) + ")");
                    if ((mx - mn).Length > 1.5)
                        continue;
                    CockpitGrip cg = new CockpitGrip { Device = g.Key, Meshes = own, Pivot = (mn + mx) * 0.5, Animation = new ControlAnimation { Source = "dataref", Name = g.Key == "stick" ? "sim/cockpit2/controls/yoke_pitch_ratio, yoke_roll_ratio" : "sim/cockpit2/engine/actuators/throttle_ratio" } };
                    gripList.Add(cg);
                }
                // The grip nearest to the pilot of each kind (both yokes move together).
                gripList = gripList.GroupBy(g => g.Device).Select(g => g.OrderBy(x => (x.Pivot - head).Length).First()).ToList();
                // X-Plane names no panels: the system its commands name (sim/autopilot, laminar/B738/fuel ...) and the place.
                ControlSystems.Assign(callouts, head, AreaTitle);
                // Controls that move with a grip (the yoke's trim and autopilot buttons) are its parts.
                foreach (KeyValuePair<CockpitCallout, string> gp in gripParts)
                {
                    CockpitGrip g = gripList.FirstOrDefault(x => x.Device == gp.Value);
                    if (g == null)
                        continue;
                    if (!g.Parts.Any(x => x.Label == gp.Key.Label))
                        g.Parts.Add(new CockpitCallout { Id = gp.Key.Id, Label = gp.Key.Label, Position = gp.Key.Position, Kind = "part", Commands = gp.Key.Commands, Animation = gp.Key.Animation, GameKind = gp.Key.GameKind });
                    callouts.Remove(gp.Key);
                }
                List<CockpitViewPlan> views = CockpitViews.Plan(scene, head, callouts, gripList, job.Width);
                if (job.ViewFilter.Length > 0)
                    views = views.Where(v => v.Id.IndexOf(job.ViewFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                PictureWriter.SetInfo info = new PictureWriter.SetInfo
                {
                    Game = "XPlane12",
                    Plane = r.Plane,
                    Planes = r.Planes,
                    Module = Path.GetFileName(acfDir),
                    Source = Path.GetFileName(acf),
                    GameVersion = version,
                    Notes = r.Notes
                };
                PictureWriter.Write(scene, views, folder, info, (cur, total, title) =>
                {
                    if (job.Progress != null)
                        job.Progress(cur, total, "X-Plane 12, " + r.Plane + " pictures", title);
                }, job.ViewFilter.Length == 0);
                r.Views = views.Count;
                r.Callouts = callouts.Count;
            }
        }

        // Splits a mesh into its connected parts (triangles sharing vertices): the mesh keeps the first part, the others
        // are added to the scene with the same material and part id (the picture is unchanged). Returns the meshes.
        private static List<int> SplitParts(CockpitScene scene, int mesh)
        {
            SceneMesh m = scene.Meshes[mesh];
            int vc = m.Positions.Length / 3;
            int[] root = Enumerable.Range(0, vc).ToArray();
            Func<int, int> find = null;
            find = i => root[i] == i ? i : (root[i] = find(root[i]));
            for (int t = 0; t + 2 < m.Indices.Length; t += 3)
            {
                root[find(m.Indices[t])] = find(m.Indices[t + 1]);
                root[find(m.Indices[t + 1])] = find(m.Indices[t + 2]);
            }
            List<IGrouping<int, int>> parts = Enumerable.Range(0, m.Indices.Length / 3).GroupBy(t => find(m.Indices[3 * t])).ToList();
            List<int> result = new List<int> { mesh };
            if (parts.Count < 2 || parts.Count > 2000)
                return result;
            bool first = true;
            foreach (IGrouping<int, int> part in parts)
            {
                Dictionary<int, int> remap = new Dictionary<int, int>();
                List<int> idx = new List<int>();
                foreach (int t in part)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int v = m.Indices[3 * t + k], nv;
                        if (!remap.TryGetValue(v, out nv))
                            remap[v] = nv = remap.Count;
                        idx.Add(nv);
                    }
                }
                int[] order = remap.OrderBy(x => x.Value).Select(x => x.Key).ToArray();
                SceneMesh piece = new SceneMesh
                {
                    Name = m.Name,
                    Node = m.Node,
                    Material = m.Material,
                    Part = m.Part,
                    Positions = order.SelectMany(v => new[] { m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2] }).ToArray(),
                    Normals = m.Normals == null ? null : order.SelectMany(v => new[] { m.Normals[3 * v], m.Normals[3 * v + 1], m.Normals[3 * v + 2] }).ToArray(),
                    Uvs = m.Uvs == null ? null : order.SelectMany(v => new[] { m.Uvs[2 * v], m.Uvs[2 * v + 1] }).ToArray(),
                    Indices = idx.ToArray()
                };
                if (first)
                {
                    scene.Meshes[mesh] = piece;
                    first = false;
                }
                else
                {
                    result.Add(scene.Meshes.Count);
                    scene.Meshes.Add(piece);
                }
            }
            return result;
        }

        private static bool Inside(Vec3 p, Vec3 mn, Vec3 mx, double margin)
        {
            return p.X >= mn.X - margin && p.Y >= mn.Y - margin && p.Z >= mn.Z - margin && p.X <= mx.X + margin && p.Y <= mx.Y + margin && p.Z <= mx.Z + margin;
        }

        // OBJ (x right, y up, z aft, metres, plus the object's offset) to the scene (x forward, y up, z right).
        private static Vec3 World(double[] m, float[] v, int c, double ox, double oy, double oz)
        {
            Vec3 l = Mat4.Point(m, v[8 * c], v[8 * c + 1], v[8 * c + 2]);
            return new Vec3(-(l.Z + oz), l.Y + oy, l.X + ox);
        }

        // The aircraft's own throttle datarefs (laminar/md82/engine/throttle_left, laminar/CitX/throttle/ratio_L, the
        // airliners' throttle_jet_rev_ratio), not the switches, lamps and stops around the throttles.
        private static readonly Regex OwnThrottleRx = new Regex("throttle", RegexOptions.Compiled);
        private static readonly Regex NotThrottleRx = new Regex("auto|switch|annun|light|lamp|disc|toga|stow|detent|mixture|friction|lock|cutoff|warn|fuel|_ind|indicator", RegexOptions.Compiled);

        private static string GripOf(List<string> datarefs, bool helicopter = false)
        {
            foreach (string d in datarefs)
            {
                string s = d.ToLowerInvariant();
                if (s.Contains("yoke_pitch") || s.Contains("yoke_roll") || s.Contains("total_pitch_ratio") || s.Contains("total_roll_ratio") || s.Contains("yoke_heading") && false)
                    return "stick";
                // The rotor head's collective (Main_Rotor.obj) is no grip.
                if (s.Contains("rotor"))
                    continue;
                if (s.Contains("throttle_ratio") || s.Contains("throttle_beta") || s.Contains("thro_use") || s.Contains("collective") || OwnThrottleRx.IsMatch(s) && !NotThrottleRx.IsMatch(s))
                    return "throttle";
                // A helicopter's collective lever is X-Plane's prop (pitch) control (the R22's prop_ratio).
                if (helicopter && s.Contains("actuators/prop_ratio"))
                    return "throttle";
            }
            return null;
        }

        private static CockpitCallout AddCallout(List<CockpitCallout> list, XPlaneManip m, Vec3 at)
        {
            string label = m.Tooltip.Length > 0 ? m.Tooltip : string.Join(" / ", m.Commands.Select(Humanize));
            if (label.Length == 0)
                return null;
            // One callout per control: manipulators of one control (up / down halves) near each other merge.
            CockpitCallout same = list.FirstOrDefault(c => string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase) && (c.Position - at).Length < 0.05);
            if (same != null)
            {
                foreach (string cmd in m.Commands.Where(x => !same.Commands.Any(c => c.Id == x)))
                    same.Commands.Add(new CalloutCommand { Name = cmd, Category = "X-Plane command", Id = cmd });
                return same;
            }
            CockpitCallout c0 = new CockpitCallout
            {
                Id = "m" + list.Count.ToString(CultureInfo.InvariantCulture),
                Label = label,
                Position = at,
                Kind = m.Kind.StartsWith("command_knob", StringComparison.Ordinal) || m.Kind.StartsWith("axis_knob", StringComparison.Ordinal) ? "knob"
                    : m.Kind.Contains("switch") ? "switch" : m.Kind.StartsWith("drag", StringComparison.Ordinal) ? "lever" : "button"
            };
            foreach (string cmd in m.Commands)
                c0.Commands.Add(new CalloutCommand { Name = cmd, Category = "X-Plane command", Id = cmd });
            foreach (string d in m.Datarefs)
                c0.Commands.Add(new CalloutCommand { Name = d, Category = "X-Plane dataref", Id = d });
            c0.GameKind = m.Kind;
            c0.Animation = m.Animation();
            list.Add(c0);
            return c0;
        }

        private static string Humanize(string command)
        {
            string s = command;
            int slash = s.LastIndexOf('/');
            if (slash >= 0)
                s = s.Substring(slash + 1);
            return s.Replace('_', ' ');
        }

        public static string AreaTitle(string area)
        {
            switch (area)
            {
                case "glareshield":
                    return "Glareshield";
                case "front-left":
                    return "Instrument panel, left";
                case "front-center":
                    return "Instrument panel, centre";
                case "front-right":
                    return "Instrument panel, right";
                case "left-console":
                    return "Left console";
                case "right-console":
                    return "Right console";
                case "center-pedestal":
                    return "Pedestal";
                case "overhead":
                    return "Overhead panel";
                case "left-side":
                    return "Left side";
                case "right-side":
                    return "Right side";
                case "rear":
                    return "Behind the seats";
                default:
                    return "Controls";
            }
        }
    }
}
