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
    // IL-2 Sturmovik: Great Battles: the cockpit pictures of every aircraft with a pilot's cockpit, made from the game's
    // archives (data\*.gtp, read only): the plane's script (luascripts/worldobjects/planes/<plane>.txt: the name, the
    // cockpit model, where the pilot sits), the cockpit model (<plane>-c.mgm: geometry, textures, the animations that
    // tie the game's argument numbers to the parts they move) and the argument names (<plane>-c.arg). The callouts sit
    // on the parts the cockpit's control arguments move; they are named by the game's own input actions (English
    // locale) where the argument is plainly that control, else by the argument's own name in the game's files.
    internal static class Il2CockpitPictures
    {
        public const string Game = "IL2Game";

        public sealed class Job
        {
            public string DataFolder = string.Empty;   // <IL-2 install>\data
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

        // A plane of the game with a pilot's cockpit.
        public sealed class Plane
        {
            public string Key = string.Empty;          // the script's name: "bf109f4"
            public string Name = string.Empty;         // the game's name: "Bf 109 F-4"
            public string Script = string.Empty;       // /luascripts/worldobjects/planes/bf109f4.txt
            public string Cockpit = string.Empty;      // /graphics/planes/bf109f4/bf109f4-c.mgm
            public string Text = string.Empty;
        }

        public static string DataFolder(string installPath)
        {
            if (string.IsNullOrWhiteSpace(installPath))
                return string.Empty;
            string d = Path.Combine(installPath, "data");
            return Directory.Exists(d) ? d : installPath;
        }

        public static string GameVersion(string dataFolder)
        {
            try
            {
                string exe = Path.Combine(Path.GetDirectoryName(dataFolder.TrimEnd('\\', '/')) ?? string.Empty, "bin", "game", "Il-2.exe");
                if (File.Exists(exe))
                {
                    System.Diagnostics.FileVersionInfo v = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                    return (v.ProductVersion ?? v.FileVersion ?? string.Empty).Trim();
                }
            }
            catch (Exception)
            {
            }
            return string.Empty;
        }

        public static List<Plane> Planes(Il2Gtp gtp)
        {
            List<Plane> list = new List<Plane>();
            foreach (string path in gtp.Paths("/luascripts/worldobjects/planes/"))
            {
                if (!Regex.IsMatch(path, "^/luascripts/worldobjects/planes/[^/]+\\.txt$"))
                    continue;
                string text = gtp.ReadText(path);
                if (text == null)
                    continue;
                Match vi = Regex.Match(text, "^\\s*VisualImage\\s*=\\s*1\\s*,\\s*\"([^\"]+)\"", RegexOptions.Multiline);
                if (!vi.Success)
                    continue;
                string mgm = Il2Gtp.Normalize(vi.Groups[1].Value);
                if (!gtp.Has(mgm))
                    continue;
                Match name = Regex.Match(text, "object_name\\s*=\\s*\"([^\"]*)\"");
                list.Add(new Plane
                {
                    Key = Path.GetFileNameWithoutExtension(path),
                    Name = name.Success ? name.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(path),
                    Script = path,
                    Cockpit = mgm,
                    Text = text
                });
            }
            return list;
        }

        // The names a plane is known by: the script's name, the game's name, the cockpit's folder and the game's name
        // with its abbreviations written out ("Yak-1 ser.69" also as "Yak-1 series 69", "Il-2 mod.1942" as "mod 1942").
        public static List<string> Names(Plane p)
        {
            List<string> names = new List<string> { p.Key, p.Name };
            string folder = p.Cockpit.Split('/').Reverse().Skip(1).FirstOrDefault() ?? string.Empty;
            names.Add(folder);
            string spelled = Regex.Replace(p.Name, "\\bser\\.\\s*", "series ", RegexOptions.IgnoreCase);
            spelled = Regex.Replace(spelled, "\\bmod\\.\\s*", "mod. ", RegexOptions.IgnoreCase);
            names.Add(spelled);
            names.Add(Regex.Replace(p.Name, "\\s*\\(.*\\)\\s*$", string.Empty));
            // Without the production block ("P-51B-5" is also "P-51B") and the mark alone ("Spitfire Mk.IXc": "Mk IXc").
            Match block = Regex.Match(p.Name, "^([A-Za-z]+-\\d+[A-Za-z]+)-\\d+$");
            if (block.Success)
                names.Add(block.Groups[1].Value);
            Match mark = Regex.Match(p.Name, "\\bMk\\.?\\s*([IVX]+[a-z]?)\\b");
            if (mark.Success)
                names.Add("Mk " + mark.Groups[1].Value);
            return names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<SetResult> Build(Job job)
        {
            List<SetResult> results = new List<SetResult>();
            using (Il2Gtp gtp = Il2Gtp.Open(job.DataFolder))
            {
                if (gtp.Count == 0)
                {
                    results.Add(new SetResult { Plane = "IL-2", Error = "no game archives (data\\*.gtp) could be read" });
                    return results;
                }
                List<Plane> planes = Planes(gtp);
                Il2Input input = Il2Input.Load(gtp);
                Dictionary<int, string> commonNames = CommonArgNames(gtp);
                DateTime newest = gtp.Archives.Select(a => File.GetLastWriteTimeUtc(a)).DefaultIfEmpty(DateTime.MinValue).Max();
                string version = GameVersion(job.DataFolder);
                int i = 0;
                foreach (Plane p in planes)
                {
                    i++;
                    if (job.Cancelled != null && job.Cancelled())
                        break;
                    List<string> names = Names(p);
                    if (job.Only.Count > 0 && !job.Only.Any(o => names.Any(n => n.IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0)))
                        continue;
                    string folder = Path.Combine(job.OutRoot, Regex.Replace(p.Key, "[^A-Za-z0-9_\\-]", "_"));
                    SetResult r = new SetResult { Plane = p.Name, Planes = names, Folder = folder };
                    results.Add(r);
                    string manifest = Path.Combine(folder, PictureWriter.ManifestName);
                    if (!job.Force && File.Exists(manifest) && File.GetLastWriteTimeUtc(manifest) > newest &&
                        File.ReadAllText(manifest).Contains("\"generator\":" + PictureWriter.GeneratorVersion.ToString(CultureInfo.InvariantCulture)))
                    {
                        r.Skipped = true;
                        continue;
                    }
                    if (job.Progress != null)
                        job.Progress(i, planes.Count, "IL-2 Great Battles, cockpit pictures", p.Name);
                    try
                    {
                        BuildOne(job, gtp, p, input, commonNames, folder, version, r);
                    }
                    catch (Exception ex)
                    {
                        r.Error = ex.GetType().Name + ": " + ex.Message;
                    }
                }
            }
            DcsCockpitPictures.WriteIndex(job.OutRoot, results.Select(x => new DcsCockpitPictures.SetResult { Plane = x.Plane, Planes = x.Planes, Folder = x.Folder, Error = x.Error }).ToList());
            return results;
        }

        // ------------------------------------------------------------------ argument names

        // Arg=<number>, "<name>"; lines of a -c.arg file.
        public static Dictionary<int, string> ArgNames(string text)
        {
            Dictionary<int, string> map = new Dictionary<int, string>();
            if (string.IsNullOrEmpty(text))
                return map;
            foreach (Match m in Regex.Matches(text, "Arg\\s*=\\s*(\\d+)\\s*,\\s*\"([^\"]*)\""))
            {
                int arg = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                string name = m.Groups[2].Value.Trim();
                if (name.Length > 0 && !map.ContainsKey(arg))
                    map[arg] = name;
            }
            return map;
        }

        // The control names the cockpits share: an argument number every -c.arg file of the game names the same kind of
        // control ("Control ..." in all of them) gets its most common name; numbers used for different things are left out.
        public static Dictionary<int, string> CommonArgNames(Il2Gtp gtp)
        {
            Dictionary<int, Dictionary<string, int>> all = new Dictionary<int, Dictionary<string, int>>();
            foreach (string path in gtp.AllPaths.Where(x => x.StartsWith("/graphics/planes/", StringComparison.Ordinal) && x.EndsWith("-c.arg", StringComparison.Ordinal)).ToList())
            {
                foreach (KeyValuePair<int, string> kv in ArgNames(gtp.ReadText(path)))
                {
                    Dictionary<string, int> c;
                    if (!all.TryGetValue(kv.Key, out c))
                        all[kv.Key] = c = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    int n;
                    c.TryGetValue(kv.Value, out n);
                    c[kv.Value] = n + 1;
                }
            }
            return CommonNames(all);
        }

        public static Dictionary<int, string> CommonNames(Dictionary<int, Dictionary<string, int>> all)
        {
            Dictionary<int, string> map = new Dictionary<int, string>();
            foreach (KeyValuePair<int, Dictionary<string, int>> kv in all)
            {
                if (kv.Value.Keys.All(n => n.StartsWith("Control ", StringComparison.OrdinalIgnoreCase)))
                    map[kv.Key] = kv.Value.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).First().Key;
            }
            return map;
        }

        // "Control landing gears" -> "Landing gears".
        public static string ArgLabel(string name)
        {
            string s = Regex.Replace(name ?? string.Empty, "^\\s*(1st|2nd|3rd)?\\s*control\\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (s.Length == 0)
                return string.Empty;
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        // Defaults of the arguments: the cockpit's DefArg lines and the plane's CockpitArg defaults.
        public static Dictionary<int, double> Defaults(string cockpitText, string planeText)
        {
            Dictionary<int, double> map = new Dictionary<int, double>();
            if (!string.IsNullOrEmpty(planeText))
            {
                foreach (Match m in Regex.Matches(planeText, "^\\s*CockpitArg\\s*=\\s*-?\\d+\\s*,\\s*\\d+\\s*,\\s*\\d+\\s*,\\s*(\\d+)\\s*,\\s*(-?[0-9.]+)", RegexOptions.Multiline))
                {
                    double d;
                    if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                        map[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = d;
                }
            }
            if (!string.IsNullOrEmpty(cockpitText))
            {
                foreach (Match m in Regex.Matches(cockpitText, "^\\s*DefArg\\s*=\\s*(\\d+)\\s*,\\s*(-?[0-9.]+)", RegexOptions.Multiline))
                {
                    double d;
                    if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                        map[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = d;
                }
            }
            return map;
        }

        public static Vec3? ReadVector(string text, string pattern)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            Match m = Regex.Match(text, pattern + "\\s*=\\s*(-?[0-9.]+)\\s*,\\s*(-?[0-9.]+)\\s*,\\s*(-?[0-9.]+)", RegexOptions.IgnoreCase);
            if (!m.Success)
                return null;
            double x, y, z;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                !double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                return null;
            return new Vec3(x, y, z);
        }

        // The pilot's eye: the pilot's seat point ([attach=0] position) and a sitting man's eye height above it; else the
        // centre of the cockpit's close-view sphere.
        public static Vec3? Eye(string planeText, string cockpitText)
        {
            if (!string.IsNullOrEmpty(planeText))
            {
                Match a = Regex.Match(planeText, "\\[attach=0\\][^\\[]*?position\\s*=\\s*(-?[0-9.]+)\\s*,\\s*(-?[0-9.]+)\\s*,\\s*(-?[0-9.]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (a.Success)
                {
                    double x = double.Parse(a.Groups[1].Value, CultureInfo.InvariantCulture), y = double.Parse(a.Groups[2].Value, CultureInfo.InvariantCulture), z = double.Parse(a.Groups[3].Value, CultureInfo.InvariantCulture);
                    return new Vec3(x, y + 0.72, z);
                }
            }
            return ReadVector(cockpitText, "CloseSphere");
        }

        // ------------------------------------------------------------------ the scene

        private static readonly Regex SkipShader = new Regex("^(CanopyGlass|Drops|BlendMask|BlendAngle|Transparent|MeshPattern)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Damaged, lower-detail, shadow and collision variants go with everything under them; effects and helpers
        // (smoke, muzzle flash, glass, dummies) only with their own geometry.
        private static readonly Regex SkipTree = new Regex("broken|dam+age|_dmg|lod[1-9]|shadow|collision", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SkipOwn = new Regex("smoke|flash|halo|particle|^glass|^dummy|torec|bullet|reflect", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void BuildOne(Job job, Il2Gtp gtp, Plane p, Il2Input input, Dictionary<int, string> commonNames, string folder, string version, SetResult r)
        {
            byte[] data = gtp.Read(p.Cockpit);
            if (data == null)
            {
                r.Error = "the cockpit model could not be read";
                return;
            }
            Il2Mgm model = Il2Mgm.Read(data);
            string baseName = p.Cockpit.Substring(0, p.Cockpit.Length - 4);
            string cockpitText = gtp.ReadText(baseName + ".txt") ?? string.Empty;
            Dictionary<int, string> argNames = ArgNames(gtp.ReadText(baseName + ".arg"));
            if (argNames.Count == 0)
                r.Notes.Add("the cockpit has no argument names (-c.arg): controls without an input action are named as the other cockpits name the same argument");
            Dictionary<int, double> defaults = Defaults(cockpitText, p.Text);
            string planeFolder = baseName.Substring(0, baseName.LastIndexOf('/') + 1);
            HashSet<Il2Mgm.Node> hidden = model.Hidden(arg => DefaultOf(arg, defaults));
            Dictionary<Il2Mgm.Node, double[]> pose = model.Pose(arg => DefaultOf(arg, defaults));
            foreach (Il2Mgm.Node n in model.Nodes.Where(n => SkipTree.IsMatch(n.Name)).ToList())
                Il2Mgm.AddSubtree(n, hidden);

            CockpitScene scene = new CockpitScene();
            Dictionary<Il2Mgm.Node, List<int>> nodeMeshes = new Dictionary<Il2Mgm.Node, List<int>>();
            Dictionary<Il2Mgm.Chunk, int> materials = new Dictionary<Il2Mgm.Chunk, int>();
            Dictionary<string, TextureImage> textures = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> textureIndex = TextureIndexOf(gtp);
            int part = 0, misplaced = 0;
            foreach (Il2Mgm.Node n in model.Nodes)
            {
                part++;
                if (hidden.Contains(n) || !model.HasGeometry(n) || SkipShader.IsMatch(n.Chunk.Shader) || SkipOwn.IsMatch(n.Name))
                    continue;
                int material;
                if (!materials.TryGetValue(n.Chunk, out material))
                {
                    material = scene.Materials.Count;
                    scene.Materials.Add(Material(gtp, n.Chunk, planeFolder, textureIndex, textures));
                    materials[n.Chunk] = material;
                }
                SceneMesh mesh = Mesh(model, n, pose, material, part);
                if (mesh == null)
                    continue;
                // A part this reader places far outside any cockpit (a few models scale their frames in ways it does not
                // follow) is left out rather than drawn across the picture.
                Vec3 d0, d1;
                CockpitScene one = new CockpitScene();
                one.Meshes.Add(mesh);
                one.Bounds(new[] { 0 }, out d0, out d1);
                if ((d1 - d0).Length > 8 || Math.Max(d0.Length, d1.Length) > 12 || FarFromItsBox(n, d0, d1))
                {
                    misplaced++;
                    continue;
                }
                nodeMeshes[n] = new List<int> { scene.Meshes.Count };
                scene.Meshes.Add(mesh);
            }
            if (misplaced > 0)
                r.Notes.Add(misplaced.ToString(CultureInfo.InvariantCulture) + " parts left out (their frames put them far outside the cockpit)");
            if (scene.Meshes.Count == 0)
            {
                r.Error = "no cockpit geometry could be read";
                return;
            }
            Vec3 mn, mx;
            scene.Bounds(Enumerable.Range(0, scene.Meshes.Count), out mn, out mx);
            Vec3 head = Eye(p.Text, cockpitText) ?? new Vec3((mn.X + mx.X) / 2 - 0.2, mn.Y + (mx.Y - mn.Y) * 0.7, 0);

            // Grips: the stick (pitch and roll) and the throttle levers of the engines, each with what hangs on it.
            List<CockpitGrip> grips = new List<CockpitGrip>();
            Dictionary<Il2Mgm.Node, CockpitGrip> gripRoots = new Dictionary<Il2Mgm.Node, CockpitGrip>();
            AddGrip(model, scene, nodeMeshes, hidden, pose, "stick", new[] { 406, 407 }, grips, gripRoots);
            AddGrip(model, scene, nodeMeshes, hidden, pose, "throttle", ThrottleArgs(model), grips, gripRoots);

            // Callouts: one per control argument the cockpit animates.
            List<CockpitCallout> callouts = new List<CockpitCallout>();
            int engines = Enumerable.Range(1, 4).Count(e => model.Animations.Any(a => a.Arg == 1000 + 100 * e));
            foreach (int arg in model.Animations.Select(a => a.Arg).Distinct().OrderBy(a => a))
            {
                if (arg == 406 || arg == 407)
                    continue;
                string own;
                argNames.TryGetValue(arg, out own);
                CalloutText text = Label(arg, own, commonNames, input, engines);
                if (text == null)
                    continue;
                List<Il2Mgm.Node> nodes = model.NodesOf(arg).Where(n => !hidden.Contains(n)).ToList();
                Vec3? at = Centre(model, scene, nodeMeshes, nodes);
                if (at == null)
                    continue;
                CockpitCallout c = new CockpitCallout
                {
                    Id = "arg" + arg.ToString(CultureInfo.InvariantCulture),
                    Label = text.Label,
                    Position = at.Value,
                    Kind = text.Kind
                };
                c.Args.Add(arg.ToString(CultureInfo.InvariantCulture));
                c.Commands.AddRange(text.Commands);
                // A control on a grip (the trigger on the stick, a button on the throttle) is the grip's part.
                CockpitGrip on = null;
                foreach (KeyValuePair<Il2Mgm.Node, CockpitGrip> g in gripRoots)
                {
                    // The lever's own argument (the throttle on the throttle) with any of its parts, others with all.
                    bool lever = ThrottleArgs(model).Contains(arg) && g.Value.Device == "throttle";
                    if (nodes.Count > 0 && (lever ? nodes.Any(n => Il2Mgm.IsUnder(n, g.Key)) : nodes.All(n => Il2Mgm.IsUnder(n, g.Key))))
                        on = g.Value;
                }
                if (on != null)
                {
                    if (!on.Parts.Any(x => x.Label == c.Label))
                        on.Parts.Add(new CockpitCallout { Id = c.Id, Label = c.Label, Position = c.Position, Kind = "part", Commands = c.Commands, Args = c.Args });
                    continue;
                }
                if (ThrottleArgs(model).Contains(arg) && grips.Any(g => g.Device == "throttle"))
                    continue;
                string area = CockpitViews.AreaOf(c.Position, head);
                c.Panel = area;
                c.PanelTitle = XPlaneCockpitPictures.AreaTitle(area);
                callouts.Add(c);
            }
            List<CockpitViewPlan> views = CockpitViews.Plan(scene, head, callouts, grips, job.Width);
            if (job.ViewFilter.Length > 0)
                views = views.Where(v => v.Id.IndexOf(job.ViewFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            PictureWriter.SetInfo info = new PictureWriter.SetInfo
            {
                Game = Game,
                Plane = r.Plane,
                Planes = r.Planes,
                Module = p.Key,
                Source = p.Cockpit.TrimStart('/'),
                GameVersion = version,
                Notes = r.Notes
            };
            PictureWriter.Write(scene, views, folder, info, (cur, total, title) =>
            {
                if (job.Progress != null)
                    job.Progress(cur, total, "IL-2 Great Battles, " + r.Plane + " pictures", title);
            }, job.ViewFilter.Length == 0);
            r.Views = views.Count;
            r.Callouts = callouts.Count + grips.Sum(g => g.Parts.Count);
        }

        private static double DefaultOf(int arg, Dictionary<int, double> defaults)
        {
            double d;
            return defaults.TryGetValue(arg, out d) ? d : 0;
        }

        // The throttle arguments of the engines (x100: engine 1 = 1100, engine 2 = 1200 ...).
        private static int[] ThrottleArgs(Il2Mgm model)
        {
            return Enumerable.Range(1, 4).Select(e => 1000 + 100 * e).Where(a => model.Animations.Any(x => x.Arg == a)).ToArray();
        }

        // A grip: the topmost nodes the grip's arguments move (with everything under them), the ones near the biggest.
        private static void AddGrip(Il2Mgm model, CockpitScene scene, Dictionary<Il2Mgm.Node, List<int>> nodeMeshes, HashSet<Il2Mgm.Node> hidden, Dictionary<Il2Mgm.Node, double[]> pose, string device, int[] args, List<CockpitGrip> grips, Dictionary<Il2Mgm.Node, CockpitGrip> roots)
        {
            List<Il2Mgm.Node> moved = args.SelectMany(a => model.NodesOf(a)).Where(n => !hidden.Contains(n)).Distinct().ToList();
            List<Il2Mgm.Node> tops = moved.Where(n => !moved.Any(o => o != n && Il2Mgm.IsUnder(n, o))).ToList();
            List<Tuple<Il2Mgm.Node, List<int>, Vec3, double>> parts = new List<Tuple<Il2Mgm.Node, List<int>, Vec3, double>>();
            foreach (Il2Mgm.Node t in tops)
            {
                HashSet<Il2Mgm.Node> sub = new HashSet<Il2Mgm.Node>();
                Il2Mgm.AddSubtree(t, sub);
                List<int> meshes = sub.Where(nodeMeshes.ContainsKey).SelectMany(n => nodeMeshes[n]).ToList();
                if (meshes.Count == 0)
                    continue;
                // The linkage rods under a lever (to the engine, to the tail) are not the grip: only the parts near the
                // lever's pivot and not long.
                double[] pv = Il2Mgm.Pivot(t, pose);
                Vec3 own = new Vec3(pv[12], pv[13], pv[14]);
                HashSet<int> ownMeshes = new HashSet<int>(nodeMeshes.ContainsKey(t) ? nodeMeshes[t] : new List<int>());
                meshes = meshes.Where(k =>
                {
                    if (ownMeshes.Contains(k))
                        return true;
                    Vec3 k0, k1;
                    scene.Bounds(new[] { k }, out k0, out k1);
                    return ((k0 + k1) * 0.5 - own).Length < 0.7 && (k1 - k0).Length < 0.9 && !IsRod(scene.Meshes[k]);
                }).ToList();
                if (meshes.Count == 0)
                    continue;
                Vec3 mn, mx;
                scene.Bounds(meshes, out mn, out mx);
                if ((mx - mn).Length > 1.2)
                    continue;
                parts.Add(Tuple.Create(t, meshes, (mn + mx) * 0.5, (mx - mn).Length));
            }
            if (parts.Count == 0)
                return;
            // The part with the grip's buttons (the stick's triggers, the throttle's switches) is the grip; else the
            // biggest (not a linkage: those were cut above).
            HashSet<Il2Mgm.Node> buttons = new HashSet<Il2Mgm.Node>(new[] { 9000, 9001, 9002, 9003, 1106, 1108 }.SelectMany(a => model.NodesOf(a)));
            Tuple<Il2Mgm.Node, List<int>, Vec3, double> main = parts.OrderByDescending(x => buttons.Any(b => Il2Mgm.IsUnder(b, x.Item1)) ? 1 : 0).ThenByDescending(x => x.Item4).First();
            List<Tuple<Il2Mgm.Node, List<int>, Vec3, double>> near = parts.Where(x => (x.Item3 - main.Item3).Length < 0.3).ToList();
            CockpitGrip g = new CockpitGrip { Device = device, Meshes = near.SelectMany(x => x.Item2).Distinct().ToList() };
            Vec3 a0, a1;
            scene.Bounds(g.Meshes, out a0, out a1);
            g.Pivot = (a0 + a1) * 0.5;
            grips.Add(g);
            foreach (Tuple<Il2Mgm.Node, List<int>, Vec3, double> x in near)
                roots[x.Item1] = g;
        }

        // The model keeps each node's box in the cockpit as the game places it (max corner, min corner). A part far from
        // its box is one the game places itself at run time (push rods that follow their levers): left out.
        public static bool FarFromItsBox(Il2Mgm.Node n, Vec3 min, Vec3 max)
        {
            float[] b = n.Box;
            Vec3 a = new Vec3(b[0], b[1], b[2]), c = new Vec3(b[3], b[4], b[5]);
            Vec3 lo = Vec3.Min(a, c), hi = Vec3.Max(a, c);
            double boxSize = (hi - lo).Length;
            if (boxSize < 1e-4)
                return false;
            double size = (max - min).Length;
            double off = ((min + max) * 0.5 - (lo + hi) * 0.5).Length;
            return size > boxSize * 2 + 0.5 || off > 1.0;
        }

        // A long thin part (a push rod, a cable): longer than 25 cm and no thicker than 3 cm around its axis.
        public static bool IsRod(SceneMesh m)
        {
            float[] p = m.Positions;
            int n = p.Length / 3;
            if (n < 2)
                return false;
            Vec3 c = new Vec3(0, 0, 0);
            for (int i = 0; i < n; i++)
                c = c + new Vec3(p[3 * i], p[3 * i + 1], p[3 * i + 2]);
            c = c * (1.0 / n);
            Vec3 a = Farthest(p, c), b = Farthest(p, a);
            Vec3 axis = b - a;
            double length = axis.Length;
            if (length < 0.25)
                return false;
            axis = axis * (1 / length);
            double thick = 0;
            for (int i = 0; i < n; i++)
            {
                Vec3 d = new Vec3(p[3 * i], p[3 * i + 1], p[3 * i + 2]) - a;
                thick = Math.Max(thick, (d - axis * Vec3.Dot(d, axis)).Length);
            }
            return thick < 0.03;
        }

        private static Vec3 Farthest(float[] p, Vec3 from)
        {
            Vec3 best = from;
            double bd = -1;
            for (int i = 0; i + 2 < p.Length; i += 3)
            {
                Vec3 v = new Vec3(p[i], p[i + 1], p[i + 2]);
                double d = (v - from).Length;
                if (d > bd)
                {
                    bd = d;
                    best = v;
                }
            }
            return best;
        }

        // Where an argument's parts are: the middle of the nodes' geometry (or of what hangs under them, or their
        // pivots); for parts far apart, the first node's.
        private static Vec3? Centre(Il2Mgm model, CockpitScene scene, Dictionary<Il2Mgm.Node, List<int>> nodeMeshes, List<Il2Mgm.Node> nodes)
        {
            List<Vec3> centres = new List<Vec3>();
            foreach (Il2Mgm.Node n in nodes)
            {
                HashSet<Il2Mgm.Node> sub = new HashSet<Il2Mgm.Node>();
                Il2Mgm.AddSubtree(n, sub);
                List<int> meshes = nodeMeshes.ContainsKey(n) ? nodeMeshes[n] : sub.Where(nodeMeshes.ContainsKey).SelectMany(k => nodeMeshes[k]).ToList();
                if (meshes.Count > 0)
                {
                    Vec3 mn, mx;
                    scene.Bounds(meshes, out mn, out mx);
                    if ((mx - mn).Length < 1.0)
                        centres.Add((mn + mx) * 0.5);
                }
            }
            if (centres.Count == 0)
                return null;
            Vec3 c = CockpitViews.Centre(centres);
            if (centres.Any(x => (x - c).Length > 0.2))
                return centres[0];
            return c;
        }

        private static SceneMesh Mesh(Il2Mgm model, Il2Mgm.Node n, Dictionary<Il2Mgm.Node, double[]> pose, int material, int part)
        {
            int[] idx = model.Indices(n);
            if (idx.Length < 3)
                return null;
            double[] world = Il2Mgm.World(n, pose);
            double[] normals = Mat4.NormalMatrix(world);
            Dictionary<int, int> remap = new Dictionary<int, int>();
            List<float> pos = new List<float>(), nor = new List<float>(), uv = new List<float>();
            List<int> tri = new List<int>(idx.Length);
            int limit = n.Chunk.VertexCount > 0 ? n.Chunk.VertexCount : int.MaxValue;
            bool skinned = Il2Mgm.Skinned(n.Chunk);
            Dictionary<Il2Mgm.Node, double[]> bones = new Dictionary<Il2Mgm.Node, double[]>();
            Func<Il2Mgm.Node, double[]> boneFrame = b =>
            {
                double[] f;
                if (!bones.TryGetValue(b, out f))
                    bones[b] = f = Il2Mgm.World(b, pose);
                return f;
            };
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                if (idx[t] >= limit || idx[t + 1] >= limit || idx[t + 2] >= limit)
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = idx[t + k];
                    int m;
                    if (!remap.TryGetValue(v, out m))
                    {
                        float x, y, z, nx, ny, nz, u, w;
                        Vec3 p, nn;
                        if (skinned)
                        {
                            if (!model.SkinVertex(n.Chunk, v, boneFrame, out p, out nn, out u, out w))
                                return null;
                        }
                        else
                        {
                            if (!model.Vertex(n.Chunk, v, out x, out y, out z, out nx, out ny, out nz, out u, out w))
                                return null;
                            p = Mat4.Point(world, x, y, z);
                            nn = Mat4.Direction(normals, nx, ny, nz).Normalized;
                        }
                        m = remap.Count;
                        remap[v] = m;
                        pos.Add((float)p.X);
                        pos.Add((float)p.Y);
                        pos.Add((float)p.Z);
                        nor.Add((float)nn.X);
                        nor.Add((float)nn.Y);
                        nor.Add((float)nn.Z);
                        uv.Add(u);
                        uv.Add(w);
                    }
                    tri.Add(m);
                }
            }
            if (tri.Count < 3)
                return null;
            return new SceneMesh { Name = n.Name, Node = n.Index, Material = material, Part = part, Positions = pos.ToArray(), Normals = nor.ToArray(), Uvs = uv.ToArray(), Indices = tri.ToArray() };
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Il2Gtp, Dictionary<string, string>> TextureIndexes = new System.Runtime.CompilerServices.ConditionalWeakTable<Il2Gtp, Dictionary<string, string>>();

        private static Dictionary<string, string> TextureIndexOf(Il2Gtp gtp)
        {
            return TextureIndexes.GetValue(gtp, TextureIndex);
        }

        // The game's textures by file name (DDS in the archives' graphics folders).
        private static Dictionary<string, string> TextureIndex(Il2Gtp gtp)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in gtp.AllPaths)
            {
                if (!path.EndsWith(".dds", StringComparison.Ordinal) || !path.StartsWith("/graphics/", StringComparison.Ordinal))
                    continue;
                string key = Path.GetFileNameWithoutExtension(path);
                if (!map.ContainsKey(key))
                    map[key] = path;
            }
            return map;
        }

        private static SceneMaterial Material(Il2Gtp gtp, Il2Mgm.Chunk c, string planeFolder, Dictionary<string, string> index, Dictionary<string, TextureImage> cache)
        {
            string name = c.Textures.Count > 0 ? c.Textures[0] : string.Empty;
            // The textures' alpha is the gloss, not the coverage.
            SceneMaterial m = new SceneMaterial { Name = name, AlphaTest = false };
            m.Color = new[] { c.Colors[0], c.Colors[1], c.Colors[2] };
            if (name.Length == 0 || Regex.IsMatch(name, "^(white|normal|black)\\.", RegexOptions.IgnoreCase))
                return m;
            string key = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
            TextureImage img;
            if (!cache.TryGetValue(key, out img))
            {
                string path = planeFolder + "textures/" + key + ".dds";
                if (!gtp.Has(path))
                    index.TryGetValue(key, out path);
                byte[] d = path == null ? null : gtp.Read(path);
                try
                {
                    img = d == null ? null : TextureImage.Decode(d, path, 2048);
                }
                catch (Exception)
                {
                    img = null;
                }
                cache[key] = img;
            }
            m.Albedo = img;
            if (img != null)
                m.Color = new[] { img.Mean[0], img.Mean[1], img.Mean[2] };
            return m;
        }

        // ------------------------------------------------------------------ labels

        public sealed class CalloutText
        {
            public string Label = string.Empty;
            public string Kind = "lever";
            public List<CalloutCommand> Commands = new List<CalloutCommand>();
        }

        // The game's input actions of the cockpit control arguments whose meaning the game's own names make plain
        // ("Control landing gears" is the gear: gear, gear_up, gear_dn). {e}: the engine's number.
        private static readonly Dictionary<int, string[]> ArgActions = new Dictionary<int, string[]>
        {
            { 121, new[] { "parking_brakes", "parking_brakes_on", "parking_brakes_off" } },
            { 402, new[] { "rpc_yaw" } },
            { 403, new[] { "rpc_wheel_brakes_left" } },
            { 404, new[] { "rpc_wheel_brakes_right" } },
            { 411, new[] { "tailwheel_lock", "tailwheel_lock_on", "tailwheel_lock_off" } },
            { 420, new[] { "rpc_wheel_brakes" } },
            { 423, new[] { "gear", "gear_up", "gear_dn" } },
            { 430, new[] { "flaps_dn", "flaps_up" } },
            { 431, new[] { "airbrakes", "airbrakes_on", "airbrakes_off" } },
            { 434, new[] { "rpc_adjustable_stabilizer", "adjustable_stabilizer_up", "adjustable_stabilizer_dn" } },
            { 435, new[] { "rpc_roll_trim", "roll_trim_switch_left", "roll_trim_switch_right" } },
            { 436, new[] { "rpc_pitch_trim", "pitch_trim_switch_up", "pitch_trim_switch_dn" } },
            { 437, new[] { "rpc_yaw_trim", "yaw_trim_switch_left", "yaw_trim_switch_right" } },
            { 2301, new[] { "turn_fuel_selector", "select_fuel_selector" } },
            { 4320, new[] { "navigation_lights" } },
            { 4321, new[] { "landing_lights" } },
            { 4323, new[] { "cockpit_lights" } },
            { 9000, new[] { "weapon_group_1_fire", "all_guns_fire" } },
            { 9001, new[] { "weapon_group_2_fire", "all_guns_fire" } },
            { 9002, new[] { "throw_bombs_command" } },
            { 9003, new[] { "launch_rockets_command" } },
            { 9042, new[] { "bombsafety_toggle", "bombsafety_on", "bombsafety_off" } },
            { 9070, new[] { "all_guns_reload" } },
            { 9071, new[] { "all_guns_reload" } },
            { 9080, new[] { "all_guns_reload" } },
            { 9081, new[] { "all_guns_reload" } },
            { 9090, new[] { "all_guns_reload" } }
        };

        // Engine controls: x100 + the offset, x = 1 + the engine's number.
        private static readonly Dictionary<int, string[]> EngineActions = new Dictionary<int, string[]>
        {
            { 0, new[] { "rpc_engine_{e}_throttle", "rpc_all_engines_throttles" } },
            { 6, new[] { "rpc_engine_{e}_prop_pitch", "rpc_all_engines_props_pitch" } },
            { 8, new[] { "engine_{e}_prop_pitch_control_mode", "all_engines_props_pitch_control_mode" } },
            { 10, new[] { "engine_{e}_supercharger", "all_engines_superchargers" } },
            { 11, new[] { "rpc_engine_{e}_mixture", "rpc_all_engines_mixtures" } },
            { 30, new[] { "rpc_engine_{e}_oil_radiator_shutter", "rpc_all_engines_oil_radiators_shutters" } },
            { 82, new[] { "rpc_engine_{e}_water_radiator_shutter", "rpc_all_engines_water_radiators_shutters" } }
        };

        // Arguments whose number means different things in different cockpits: they count only with the cockpit's own
        // "Control ..." name.
        private static readonly HashSet<int> NeedsOwnName = new HashSet<int> { 9003, 9042, 2301 };

        // Controls the game works with two actions, one per direction (flaps down, flaps up): both name it.
        private static readonly HashSet<int> Halves = new HashSet<int> { 430 };

        public static List<string> ActionsOf(int arg, int engines)
        {
            string[] list;
            if (ArgActions.TryGetValue(arg, out list))
                return list.ToList();
            int engine = arg / 100 - 10;
            if (arg >= 1100 && arg < 1500 && engine >= 1 && EngineActions.TryGetValue(arg % 100, out list))
            {
                List<string> a = list.Select(x => x.Replace("{e}", engine.ToString(CultureInfo.InvariantCulture))).ToList();
                // One engine: the all-engines action first (the one players bind).
                if (engines <= 1)
                    a.Reverse();
                return a;
            }
            return new List<string>();
        }

        public static CalloutText Label(int arg, string ownName, Dictionary<int, string> commonNames, Il2Input input, int engines)
        {
            string name = ownName;
            bool ownControl = !string.IsNullOrEmpty(ownName) && Regex.IsMatch(ownName, "^\\s*(1st|2nd|3rd)?\\s*control\\s", RegexOptions.IgnoreCase);
            if (string.IsNullOrEmpty(name) && commonNames != null)
                commonNames.TryGetValue(arg, out name);
            bool control = !string.IsNullOrEmpty(name) && Regex.IsMatch(name, "^\\s*(1st|2nd|3rd)?\\s*control\\s", RegexOptions.IgnoreCase);
            List<string> actions = ActionsOf(arg, engines).Where(a => input == null || input.Has(a)).ToList();
            if (NeedsOwnName.Contains(arg) && !ownControl)
                actions.Clear();
            // A named non-control (a gauge, a lamp) is no callout, whatever its number.
            if (!string.IsNullOrEmpty(ownName) && !ownControl)
                return null;
            CalloutText t = new CalloutText();
            if (actions.Count > 0)
            {
                foreach (string a in actions)
                    t.Commands.Add(new CalloutCommand { Name = input == null ? a : input.Text(a), Category = "IL-2 input", Id = a });
                List<string> texts = t.Commands.Select(c => c.Name).Where(x => x.Length > 0).Distinct().ToList();
                // Two halves of one control (flaps down / up) read as one; else the first action names it.
                t.Label = Halves.Contains(arg) && texts.Count == 2 ? texts[0] + " / " + texts[1] : texts.FirstOrDefault() ?? actions[0];
            }
            else if (control)
            {
                t.Label = ArgLabel(name);
            }
            else
            {
                return null;
            }
            string lower = (name ?? string.Empty).ToLowerInvariant() + " " + string.Join(" ", actions);
            t.Kind = Regex.IsMatch(lower, "switch|selector|magneto|lights?\\b") ? "switch" : Regex.IsMatch(lower, "button|trigger|fire|command|primer|pump") ? "button" : Regex.IsMatch(lower, "trim|wheel|tuning|volume") ? "knob" : "lever";
            if (t.Label.Length == 0)
                return null;
            return t;
        }
    }

    // The game's input actions (swf/il2/input/actions.txt) and their English names (swf/il2/locale/input.locale=eng.txt:
    // "&inp<action>=<name>").
    internal sealed class Il2Input
    {
        private readonly HashSet<string> actions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static Il2Input Load(Il2Gtp gtp)
        {
            return Parse(gtp.ReadText("/swf/il2/input/actions.txt") ?? string.Empty, gtp.ReadText("/swf/il2/locale/input.locale=eng.txt", Encoding.UTF8) ?? string.Empty);
        }

        public static Il2Input Parse(string actionsText, string localeText)
        {
            Il2Input i = new Il2Input();
            foreach (string raw in actionsText.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("&", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal))
                    continue;
                Match m = Regex.Match(line, "^([a-z0-9_]+)\\s*[,|]", RegexOptions.IgnoreCase);
                if (m.Success)
                    i.actions.Add(m.Groups[1].Value);
            }
            foreach (Match m in Regex.Matches(localeText, "^&inp([a-z0-9_]+)=(.*?)\\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                string text = m.Groups[2].Value.Trim().TrimEnd(',').Trim();
                if (text.Length > 0 && !i.names.ContainsKey(m.Groups[1].Value))
                    i.names[m.Groups[1].Value] = text;
            }
            return i;
        }

        public bool Has(string action)
        {
            return actions.Count == 0 ? names.ContainsKey(action) : actions.Contains(action);
        }

        public string Text(string action)
        {
            string t;
            return names.TryGetValue(action, out t) ? t : action;
        }

        public int Count { get { return actions.Count; } }
    }
}
