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

        private static readonly Regex SkipObject = new Regex("glass|broken|lights?\\.obj|_lit\\.|pilot|passenger|prop(_|\\.|disc)|bobble|shadow|reflect|people|crew|wheel_?chock|tie_?down|cover", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void BuildOne(Job job, string acf, Dictionary<string, string> p, string folder, string version, SetResult r)
        {
            string acfDir = Path.GetDirectoryName(acf);
            string objects = Path.Combine(acfDir, "objects");
            CockpitScene scene = new CockpitScene();
            List<CockpitCallout> callouts = new List<CockpitCallout>();
            Dictionary<string, List<int>> grips = new Dictionary<string, List<int>>();
            Dictionary<string, int> materials = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<CockpitCallout, string> gripParts = new Dictionary<CockpitCallout, string>();
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
                    bool draw = obj.Texture.Length > 0 && !SkipObject.IsMatch(Path.GetFileName(file));
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
                            SceneMaterial sm = new SceneMaterial { Name = Path.GetFileName(texKey), Albedo = img, AlphaTest = true, FlipV = !dds };
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
                            string onGrip = GripOf(run.Datarefs);
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
                        string grip = GripOf(run.Datarefs);
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
                    Vec3 mn, mx;
                    scene.Bounds(g.Value, out mn, out mx);
                    if ((mx - mn).Length > 1.5)
                        continue;
                    CockpitGrip cg = new CockpitGrip { Device = g.Key, Meshes = g.Value, Pivot = (mn + mx) * 0.5 };
                    gripList.Add(cg);
                }
                // The grip nearest to the pilot of each kind (both yokes move together).
                gripList = gripList.GroupBy(g => g.Device).Select(g => g.OrderBy(x => (x.Pivot - head).Length).First()).ToList();
                foreach (CockpitCallout c in callouts)
                {
                    string area = CockpitViews.AreaOf(c.Position, head);
                    c.Panel = area;
                    c.PanelTitle = AreaTitle(area);
                }
                // Controls that move with a grip (the yoke's trim and autopilot buttons) are its parts.
                foreach (KeyValuePair<CockpitCallout, string> gp in gripParts)
                {
                    CockpitGrip g = gripList.FirstOrDefault(x => x.Device == gp.Value);
                    if (g == null)
                        continue;
                    if (!g.Parts.Any(x => x.Label == gp.Key.Label))
                        g.Parts.Add(new CockpitCallout { Id = gp.Key.Id, Label = gp.Key.Label, Position = gp.Key.Position, Kind = "part", Commands = gp.Key.Commands });
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

        private static string GripOf(List<string> datarefs)
        {
            foreach (string d in datarefs)
            {
                string s = d.ToLowerInvariant();
                if (s.Contains("yoke_pitch") || s.Contains("yoke_roll") || s.Contains("total_pitch_ratio") || s.Contains("total_roll_ratio") || s.Contains("yoke_heading") && false)
                    return "stick";
                if (s.Contains("throttle_ratio") || s.Contains("throttle_beta") || s.Contains("thro_use") || s.Contains("collective"))
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
