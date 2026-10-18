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
using JoyPro.DeviceReference;

namespace JoyPro.CockpitPictures
{
    // Microsoft Flight Simulator 2020: the cockpit pictures of the aircraft whose interior model the packages hold as
    // open glTF (the official and community packages that are not encrypted; packages with .fsarchive files and the
    // MSFS 2024 packages are encrypted and never read). The interior model (model.cfg "interior") names its parts by
    // function (ELECTRICAL_Switch_Battery_Master, HANDLING_Yoke_1 ...) and the model's behaviours (the interior xml)
    // say which of them the pilot can work: the callouts sit on those parts, named as the model names them.
    internal static class MsfsCockpitPictures
    {
        private const double Feet = 0.3048;

        public sealed class Job
        {
            public List<string> PackageRoots = new List<string>();
            public string OutRoot = string.Empty;
            public List<string> Only = new List<string>();
            public bool Force;
            public int Width = 1600;
            public string ViewFilter = string.Empty;
            public string GameVersion = string.Empty;
            public Action<int, int, string, string> Progress;
            public Func<bool> Cancelled;
        }

        public sealed class Aircraft
        {
            public string Folder = string.Empty;       // SimObjects\Airplanes\<name>
            public string Package = string.Empty;
            public string Interior = string.Empty;     // the interior model's glTF (highest detail)
            public string Behaviours = string.Empty;   // the interior model's xml (behaviours), "" if the package has none
            public List<string> Names = new List<string>();
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

        // ------------------------------------------------------------------ finding the aircraft

        // InstalledPackagesPath of a UserCfg.opt.
        public static string PackagesPath(string userCfg)
        {
            try
            {
                if (!File.Exists(userCfg))
                    return string.Empty;
                Match m = Regex.Match(File.ReadAllText(userCfg), "InstalledPackagesPath\\s+\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        // The package folders of MSFS 2020: the InstalledPackagesPath of its UserCfg.opt (Steam, then Microsoft Store)
        // and the install's own Packages folder.
        public static List<string> PackageRoots(string installPath)
        {
            List<string> roots = new List<string>();
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (string cfg in new[] { Path.Combine(roaming, "Microsoft Flight Simulator", "UserCfg.opt"), Path.Combine(local, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt") })
            {
                string p = PackagesPath(cfg);
                if (p.Length > 0 && Directory.Exists(p))
                    roots.Add(p);
            }
            if (!string.IsNullOrWhiteSpace(installPath) && Directory.Exists(Path.Combine(installPath, "Packages")))
                roots.Add(Path.Combine(installPath, "Packages"));
            return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The package folders under the roots (Official\<store>\*, Community\*).
        public static List<string> Packages(IEnumerable<string> packageRoots)
        {
            List<string> packages = new List<string>();
            foreach (string root in packageRoots.Where(Directory.Exists))
            {
                foreach (string d in SubDirs(root))
                {
                    string n = Path.GetFileName(d);
                    if (string.Equals(n, "Official", StringComparison.OrdinalIgnoreCase))
                        packages.AddRange(SubDirs(d).SelectMany(SubDirs));
                    else if (string.Equals(n, "Community", StringComparison.OrdinalIgnoreCase))
                        packages.AddRange(SubDirs(d));
                }
            }
            return packages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Every aircraft with an open interior model. Encrypted packages are counted in "encrypted".
        public static List<Aircraft> Find(IEnumerable<string> packageRoots, List<string> encrypted = null)
        {
            List<string> packages = Packages(packageRoots);
            List<Aircraft> list = new List<Aircraft>();
            foreach (string package in packages)
            {
                string pname = Path.GetFileName(package);
                // Liveries only point at the aircraft's own model.
                if (pname.IndexOf("-livery", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (Encrypted(package))
                {
                    if (encrypted != null && pname.IndexOf("aircraft", StringComparison.OrdinalIgnoreCase) >= 0)
                        encrypted.Add(pname);
                    continue;
                }
                foreach (string kind in new[] { "Airplanes", "Rotorcraft" })
                {
                    foreach (string folder in SubDirs(Path.Combine(package, "SimObjects", kind)))
                    {
                        string xml;
                        string interior = Interior(folder, package, packages, out xml);
                        if (interior.Length == 0)
                            continue;
                        if (!PlainGltf(interior))
                        {
                            if (encrypted != null)
                                encrypted.Add(pname);
                            continue;
                        }
                        Aircraft a = new Aircraft { Folder = folder, Package = pname, Interior = interior, Behaviours = xml };
                        a.Names.Add(Path.GetFileName(folder));
                        foreach (string n in CfgNames(Path.Combine(folder, "aircraft.cfg")))
                            a.Names.Add(n);
                        a.Names.Add(pname);
                        a.Names = a.Names.Where(n => n.Length > 0 && !n.StartsWith("$", StringComparison.Ordinal) && !n.StartsWith("@", StringComparison.Ordinal) && !n.StartsWith("TT:", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        // Variants (floats, skis) share one interior: one set, named by all of them.
                        Aircraft same = list.FirstOrDefault(x => string.Equals(x.Interior, interior, StringComparison.OrdinalIgnoreCase));
                        if (same != null)
                            same.Names = same.Names.Concat(a.Names).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        else
                            list.Add(a);
                    }
                }
            }
            return list;
        }

        // A package whose content is in encrypted archives (Marketplace and some official ones): never read.
        public static bool Encrypted(string package)
        {
            try
            {
                return Directory.GetFiles(package, "*.fsarchive", SearchOption.TopDirectoryOnly).Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // An open glTF (JSON text); an encrypted model is not.
        public static bool PlainGltf(string file)
        {
            try
            {
                using (FileStream f = File.OpenRead(file))
                {
                    int b;
                    while ((b = f.ReadByte()) == ' ' || b == '\r' || b == '\n' || b == '\t' || b == 0xEF || b == 0xBB || b == 0xBF)
                    {
                    }
                    return b == '{';
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static IEnumerable<string> SubDirs(string d)
        {
            try
            {
                return Directory.Exists(d) ? Directory.GetDirectories(d) : new string[0];
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        // A path of the sim's virtual file system: as it is, else the same path in another package (a variant's model
        // points into the base aircraft's package with ..\..\).
        internal static string Resolve(string path, string package, List<string> packages)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return string.Empty;
            }
            if (File.Exists(full))
                return full;
            string pkg = Path.GetFullPath(package).TrimEnd('\\') + "\\";
            if (!full.StartsWith(pkg, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            string rel = full.Substring(pkg.Length);
            foreach (string other in packages)
            {
                string candidate = Path.Combine(other, rel);
                if (File.Exists(candidate))
                    return candidate;
            }
            return string.Empty;
        }

        // model*\model.cfg [models] interior=<xml>; the xml's first LOD (the most detailed) is the glTF. Packages
        // without the xml: the interior glTF with the lowest LOD number. xml = the behaviours file ("" if none).
        internal static string Interior(string folder, string package, List<string> packages, out string xml)
        {
            xml = string.Empty;
            try
            {
                foreach (string modelDir in Directory.GetDirectories(folder, "model*").OrderBy(d => Path.GetFileName(d).Length))
                {
                    string cfg = Directory.GetFiles(modelDir, "*.cfg").OrderBy(f => string.Equals(Path.GetFileName(f), "model.cfg", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
                    if (cfg == null)
                        continue;
                    Match m = Regex.Match(File.ReadAllText(cfg), "^\\s*interior\\s*=\\s*([^\\r\\n;]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                    if (!m.Success)
                        continue;
                    string xmlPath = Resolve(Path.Combine(modelDir, m.Groups[1].Value.Trim()), package, packages);
                    if (xmlPath.Length > 0)
                    {
                        Match lod = Regex.Match(File.ReadAllText(xmlPath), "ModelFile=\"([^\"]+)\"", RegexOptions.IgnoreCase);
                        string gltf = lod.Success ? Resolve(Path.Combine(Path.GetDirectoryName(xmlPath), lod.Groups[1].Value), package, packages) : string.Empty;
                        if (gltf.Length > 0)
                        {
                            xml = xmlPath;
                            return gltf;
                        }
                    }
                    string best = Directory.GetFiles(modelDir, "*.gltf")
                        .Where(f => Regex.IsMatch(Path.GetFileName(f), "interior|cockpit", RegexOptions.IgnoreCase))
                        .OrderBy(LodNumber).FirstOrDefault();
                    if (best != null)
                        return best;
                }
            }
            catch (Exception)
            {
            }
            return string.Empty;
        }

        internal static int LodNumber(string file)
        {
            Match m = Regex.Match(Path.GetFileNameWithoutExtension(file), "lod_?(\\d+)$", RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        private static IEnumerable<string> CfgNames(string cfg)
        {
            if (!File.Exists(cfg))
                yield break;
            string text;
            try
            {
                text = File.ReadAllText(cfg);
            }
            catch (Exception)
            {
                yield break;
            }
            foreach (string key in new[] { "title", "ui_type", "ui_variation", "icao_type_designator", "icao_model" })
            {
                foreach (Match m in Regex.Matches(text, "^\\s*" + key + "\\s*=\\s*\"?([^\"\\r\\n;]+)\"?", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                    yield return m.Groups[1].Value.Trim();
            }
        }

        // ------------------------------------------------------------------ build

        public static List<SetResult> Build(Job job, List<string> encrypted = null)
        {
            List<SetResult> results = new List<SetResult>();
            List<Aircraft> all = Find(job.PackageRoots, encrypted);
            int i = 0;
            foreach (Aircraft a in all)
            {
                i++;
                if (job.Cancelled != null && job.Cancelled())
                    break;
                if (job.Only.Count > 0 && !job.Only.Any(o => a.Names.Any(n => n.IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0)))
                    continue;
                string key = Regex.Replace(Path.GetFileName(a.Folder), "[^A-Za-z0-9_\\-]", "_");
                string folder = Path.Combine(job.OutRoot, key);
                SetResult r = new SetResult { Plane = a.Names[0], Planes = a.Names, Folder = folder };
                results.Add(r);
                string manifest = Path.Combine(folder, PictureWriter.ManifestName);
                if (!job.Force && File.Exists(manifest) && File.GetLastWriteTimeUtc(manifest) > File.GetLastWriteTimeUtc(a.Interior) &&
                    File.ReadAllText(manifest).Contains("\"generator\":" + PictureWriter.GeneratorVersion.ToString(CultureInfo.InvariantCulture)))
                {
                    r.Skipped = true;
                    continue;
                }
                if (job.Progress != null)
                    job.Progress(i, all.Count, "Microsoft Flight Simulator 2020, cockpit pictures", a.Names[0]);
                try
                {
                    BuildOne(job, a, folder, r);
                }
                catch (Exception ex)
                {
                    r.Error = ex.GetType().Name + ": " + ex.Message;
                }
            }
            // A name two sets share (the ICAO code "C152" of the C152 and its Aerobat) goes to the base aircraft: the
            // set with the shortest folder name claims it first.
            HashSet<string> claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<DcsCockpitPictures.SetResult> index = new List<DcsCockpitPictures.SetResult>();
            foreach (SetResult x in results.OrderBy(x => Path.GetFileName(x.Folder).Length).ThenBy(x => x.Folder, StringComparer.OrdinalIgnoreCase))
            {
                List<string> own = x.Planes.Where(n => claimed.Add(n)).ToList();
                index.Add(new DcsCockpitPictures.SetResult { Plane = x.Plane, Planes = own, Folder = x.Folder, Error = x.Error });
            }
            DcsCockpitPictures.WriteIndex(job.OutRoot, index);
            return results;
        }

        // Nodes never drawn: collision and hider meshes, the hands, glass, the tutorial highlights.
        private static readonly Regex SkipNode = new Regex("collision|colision|highlight|hider|occluder|(^|_)hands?(_|$)|shadow|tuto|windshield|glass|reflect|hidden|^Pilot|^CoPilot", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        internal static readonly Regex Loose = new Regex("^(?<sys>[A-Za-z][A-Za-z0-9]*?)_(?:.*_)?(?<kind>switch|knob|lever|push|button|handle|wheel|toggle|selector|pull|rocker|slider|guard|cover|crank|plunger|yoke)s?(?=_|$|\\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LooseNot = new Regex("text|light|hitbox|visib|anim|plastic|holder|wire|stair|hydrolic|^Point|highlight|collision|_(base|static|cable|axes|rod)(_|$|\\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SkipParent = new Regex("collision|colision|tuto|(^|_)hands?(_|$)|^Pilot|^CoPilot", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // A part named by function: <SYSTEM>_<Kind>_<name> (Asobo's naming, used by most studios).
        internal static readonly Regex Interactive = new Regex("^(?<sys>[A-Z][A-Z0-9]+)_(?<kind>Switch|Knob|Lever|Push|Button|Handle|Wheel|Toggle|Selector|Pull|Rocker|Slider|Key|Guard|Cover|Crank|Plunger|Yoke|Stick|Pedal|Pedals|Rudder|Collective|Cyclic)s?(_(?<rest>.*))?$", RegexOptions.Compiled);
        private static readonly Regex NotACallout = new Regex("_(Visual|Highlight|Collision|Colision|Hider|Axes|Cable|Logo|Static|Base|Shadow|Emissive|Needle|Lit|Decal|Anim|Mark|Text|Screen|Rod|Log0|Element\\d*|Brake)(_|$)|_Hands?(_|$)|OnlyVisual|_Ring|_Indicator|ForeAft|LeftRight", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void BuildOne(Job job, Aircraft a, string folder, SetResult r)
        {
            Gltf g = Gltf.Load(a.Interior);
            if (g == null)
            {
                r.Error = "the interior model could not be read";
                return;
            }
            Behaviours behaviours = Behaviours.Read(a.Behaviours);
            CockpitScene scene = new CockpitScene();
            using (TextureLibrary lib = new TextureLibrary())
            {
                AddTextureFolders(lib, a);
                Dictionary<int, double[]> world;
                Dictionary<int, int> parent;
                g.World(out world, out parent);
                Dictionary<int, List<int>> meshesOfNode = new Dictionary<int, List<int>>();
                Dictionary<int, int> materialMap = new Dictionary<int, int>();
                int part = 0;
                foreach (KeyValuePair<int, double[]> nw in world)
                {
                    Dictionary<string, object> node = g.Nodes[nw.Key];
                    if (!node.ContainsKey("mesh") || Hidden(g.Nodes, parent, nw.Key))
                        continue;
                    int meshIndex = (int)Num(node, "mesh");
                    if (meshIndex < 0 || meshIndex >= g.Meshes.Count)
                        continue;
                    double[] m = nw.Value;
                    double[] nm = Mat4.NormalMatrix(m);
                    bool mirrored = Mat4.Determinant3(m) < 0;
                    foreach (Dictionary<string, object> prim in Objects(g.Meshes[meshIndex], "primitives"))
                    {
                        part++;
                        int mode = prim.ContainsKey("mode") ? (int)Num(prim, "mode") : 4;
                        Dictionary<string, object> attrs = Obj(prim, "attributes");
                        if (mode != 4 || attrs == null || !attrs.ContainsKey("POSITION") || !prim.ContainsKey("indices"))
                            continue;
                        int sceneMat = SceneMaterialOf(g, prim.ContainsKey("material") ? (int)Num(prim, "material") : -1, lib, scene, materialMap);
                        if (sceneMat < 0)
                            continue;
                        float[] pos = g.Accessor((int)Num(attrs, "POSITION"), false);
                        float[] nor = attrs.ContainsKey("NORMAL") ? g.Accessor((int)Num(attrs, "NORMAL"), false) : null;
                        float[] uv = attrs.ContainsKey("TEXCOORD_0") ? g.Accessor((int)Num(attrs, "TEXCOORD_0"), true) : null;
                        float[] idx = g.Accessor((int)Num(prim, "indices"), false);
                        if (pos == null || idx == null || pos.Length < 3)
                            continue;
                        int vc = pos.Length / 3;
                        int nc = nor == null ? 0 : nor.Length / vc;
                        int uc = uv == null ? 0 : uv.Length / vc;
                        // MSFS's optimised models: the primitives of a mesh share one vertex and one index buffer;
                        // extras.ASOBO_primitive gives each its range (StartIndex, PrimitiveCount triangles) and the
                        // BaseVertexIndex added to its 16-bit indices.
                        int first = 0, count = idx.Length / 3, baseVertex = 0;
                        Dictionary<string, object> asobo = Obj(Obj(prim, "extras"), "ASOBO_primitive");
                        if (asobo != null && (asobo.ContainsKey("StartIndex") || asobo.ContainsKey("BaseVertexIndex") || asobo.ContainsKey("PrimitiveCount")))
                        {
                            first = (int)Num(asobo, "StartIndex");
                            baseVertex = (int)Num(asobo, "BaseVertexIndex");
                            if (asobo.ContainsKey("PrimitiveCount"))
                                count = (int)Num(asobo, "PrimitiveCount");
                        }
                        if (first < 0 || first >= idx.Length)
                            continue;
                        count = Math.Min(count, (idx.Length - first) / 3);
                        // Only the vertices the primitive uses, in the scene's frame.
                        Dictionary<int, int> remap = new Dictionary<int, int>();
                        List<float> wp = new List<float>(), wn = new List<float>(), wu = new List<float>();
                        List<int> tris = new List<int>(count * 3);
                        int[] corner = new int[3];
                        for (int t = 0; t < count; t++)
                        {
                            bool ok = true;
                            for (int c = 0; c < 3; c++)
                            {
                                int v = (int)idx[first + 3 * t + c] + baseVertex;
                                if (v < 0 || v >= vc)
                                {
                                    ok = false;
                                    break;
                                }
                                int mapped;
                                if (!remap.TryGetValue(v, out mapped))
                                {
                                    mapped = remap.Count;
                                    remap[v] = mapped;
                                    Vec3 p = ToScene(Mat4.Point(m, pos[3 * v], pos[3 * v + 1], pos[3 * v + 2]));
                                    wp.Add((float)p.X);
                                    wp.Add((float)p.Y);
                                    wp.Add((float)p.Z);
                                    Vec3 n = nc >= 3 ? ToScene(Mat4.Direction(nm, nor[nc * v], nor[nc * v + 1], nor[nc * v + 2])).Normalized : new Vec3(0, 1, 0);
                                    wn.Add((float)n.X);
                                    wn.Add((float)n.Y);
                                    wn.Add((float)n.Z);
                                    wu.Add(uc >= 2 ? uv[uc * v] : 0);
                                    wu.Add(uc >= 2 ? uv[uc * v + 1] : 0);
                                }
                                corner[c] = mapped;
                            }
                            if (!ok)
                                continue;
                            // A mirrored node turns its triangles over: keep them facing out.
                            tris.Add(corner[0]);
                            tris.Add(mirrored ? corner[2] : corner[1]);
                            tris.Add(mirrored ? corner[1] : corner[2]);
                        }
                        if (tris.Count < 3)
                            continue;
                        int mesh = scene.Meshes.Count;
                        scene.Meshes.Add(new SceneMesh { Name = Str(node, "name"), Node = nw.Key, Material = sceneMat, Part = part, Positions = wp.ToArray(), Normals = wn.ToArray(), Uvs = wu.ToArray(), Indices = tris.ToArray() });
                        for (int k = nw.Key, guard = 0; guard < 64; guard++)
                        {
                            List<int> l;
                            if (!meshesOfNode.TryGetValue(k, out l))
                                meshesOfNode[k] = l = new List<int>();
                            l.Add(mesh);
                            int pk;
                            if (!parent.TryGetValue(k, out pk))
                                break;
                            k = pk;
                        }
                    }
                }
                scene.NodeMeshes = meshesOfNode;
                if (scene.Meshes.Count == 0)
                {
                    r.Error = "no cockpit geometry could be read";
                    return;
                }

                // Callouts: the parts the model's behaviours make interactive (or, without behaviours, the parts named
                // by function), at their geometry.
                List<CockpitCallout> callouts = new List<CockpitCallout>();
                CockpitGrip yoke = null, throttle = null;
                foreach (KeyValuePair<int, double[]> nw in world.OrderBy(x => x.Key))
                {
                    string name = Str(g.Nodes[nw.Key], "name");
                    // The pilot's yoke or stick by its name alone (older models: YOKE_LEFT).
                    if (yoke == null && Regex.IsMatch(name, "^(YOKE|STICK)(_LEFT|_L|_1)?$", RegexOptions.IgnoreCase) && !Hidden(g.Nodes, parent, nw.Key))
                    {
                        List<int> gm;
                        if (meshesOfNode.TryGetValue(nw.Key, out gm) && gm.Count > 0)
                        {
                            Vec3 gmn, gmx;
                            scene.Bounds(gm, out gmn, out gmx);
                            if ((gmx - gmn).Length < 1.2)
                            {
                                yoke = new CockpitGrip { Device = "stick", Meshes = gm.ToList(), Pivot = (gmn + gmx) * 0.5, Animation = new ControlAnimation { Source = "model node", Name = name } };
                                continue;
                            }
                        }
                    }
                    Match m = Interactive.Match(name);
                    // Older models name the parts in other cases and orders (DEICESYSTEM_SWITCH_AIRFRAME, LEVER_THROTTLE).
                    bool loose = false;
                    if (!m.Success && !LooseNot.IsMatch(name))
                    {
                        m = Loose.Match(name);
                        loose = m.Success;
                    }
                    string template;
                    bool byBehaviour = behaviours.Nodes.TryGetValue(name, out template);
                    if (!byBehaviour && behaviours.Nodes.Count > 0 && behaviours.Nodes.TryGetValue(Regex.Replace(name, "_\\d+$", string.Empty), out template))
                        byBehaviour = true;
                    if (!m.Success || NotACallout.IsMatch(name) || Hidden(g.Nodes, parent, nw.Key))
                        continue;
                    // The behaviours add the template and its events; the part names decide (many controls are made by
                    // templates of the sim's own behaviour library that name their nodes themselves).
                    List<int> ms;
                    if (!meshesOfNode.TryGetValue(nw.Key, out ms) || ms.Count == 0)
                        continue;
                    Vec3 mn, mx;
                    scene.Bounds(ms, out mn, out mx);
                    if ((mx - mn).Length > 1.2)
                        continue;
                    Vec3 at = (mn + mx) * 0.5;
                    string kind = m.Groups["kind"].Value;
                    if (kind.Length > 0)
                        kind = char.ToUpperInvariant(kind[0]) + kind.Substring(1).ToLowerInvariant();
                    if (yoke == null && Regex.IsMatch(name, "^HANDLING_(Yoke|Stick|Cyclic)(_1|_L|_Left)?$|^(YOKE|STICK)(_LEFT|_L|_1)?$", RegexOptions.IgnoreCase))
                    {
                        yoke = new CockpitGrip { Device = "stick", Meshes = ms.ToList(), Pivot = at, Animation = new ControlAnimation { Source = "model node", Name = name } };
                        continue;
                    }
                    if (throttle == null && Regex.IsMatch(name, "^(ENGINE|HANDLING)_(Lever_Throttle|Throttle|Lever_Collective|Collective)(_1)?$|^LEVER_THROTTLE(_1|_HANDLE)?$", RegexOptions.IgnoreCase))
                        throttle = new CockpitGrip { Device = "throttle", Meshes = ms.ToList(), Pivot = at, Animation = new ControlAnimation { Source = "model node", Name = name } };
                    // The other pilot's yoke or stick is no control of a panel.
                    if (kind == "Yoke" || kind == "Stick" || kind == "Cyclic" || callouts.Any(c => c.Id == name))
                        continue;
                    CockpitCallout co = new CockpitCallout { Id = name, Label = loose ? LooseLabel(name) : Label(m), Position = at, Kind = KindOf(kind), Device = m.Groups["sys"].Value };
                    if (byBehaviour && template.Length > 0)
                        co.Commands.Add(new CalloutCommand { Name = template, Category = "MSFS behaviour", Id = template });
                    List<string> events;
                    if (behaviours.Events.TryGetValue(name, out events))
                    {
                        foreach (string e in events.Distinct().Take(6))
                            co.Commands.Add(new CalloutCommand { Name = e, Category = "MSFS event", Id = e });
                    }
                    // What moves: the animated node (its behaviour template drives it where the interior names one).
                    co.Animation = new ControlAnimation { Source = byBehaviour && template.Length > 0 ? "behaviour" : "model node", Name = byBehaviour && template.Length > 0 ? name + " (" + template + ")" : name };
                    co.GameKind = (kind + " " + (byBehaviour ? template : string.Empty)).Trim();
                    callouts.Add(co);
                }

                // The pilot's eye: the cockpit camera of cameras.cfg (eyepoint from the reference datum plus the
                // camera's offset); else behind and above the yoke.
                Vec3 head;
                if (!EyePoint(a.Folder, out head))
                {
                    if (yoke != null)
                        head = new Vec3(yoke.Pivot.X - 0.6, yoke.Pivot.Y + 0.4, yoke.Pivot.Z);
                    else
                    {
                        Vec3 mn, mx;
                        scene.Bounds(Enumerable.Range(0, scene.Meshes.Count), out mn, out mx);
                        head = new Vec3(mn.X + (mx.X - mn.X) * 0.4, mn.Y + (mx.Y - mn.Y) * 0.6, (mn.Z + mx.Z) * 0.5);
                    }
                    r.Notes.Add("no cockpit camera found; the eye is placed behind the controls");
                }
                foreach (CockpitCallout c in callouts)
                {
                    string area = CockpitViews.AreaOf(c.Position, head);
                    c.Panel = area;
                    c.PanelTitle = XPlaneCockpitPictures.AreaTitle(area);
                }
                List<CockpitGrip> grips = new List<CockpitGrip>();
                if (yoke != null)
                {
                    grips.Add(yoke);
                    // Controls that hang on the yoke (its trim, timer, autopilot buttons) are its parts.
                    HashSet<int> onYoke = new HashSet<int>(yoke.Meshes);
                    foreach (CockpitCallout c in callouts.ToList())
                    {
                        List<int> cm;
                        int node = NodeOf(g, c.Id);
                        if (node >= 0 && meshesOfNode.TryGetValue(node, out cm) && cm.Count > 0 && cm.All(onYoke.Contains))
                        {
                            yoke.Parts.Add(new CockpitCallout { Id = c.Id, Label = c.Label, Position = c.Position, Kind = "part", Commands = c.Commands, Animation = c.Animation, GameKind = c.GameKind });
                            callouts.Remove(c);
                        }
                    }
                }
                if (throttle != null)
                {
                    grips.Add(throttle);
                    CockpitCallout self = callouts.FirstOrDefault(c => throttle.Meshes.SequenceEqual(meshesOfNode.ContainsKey(NodeOf(g, c.Id)) ? meshesOfNode[NodeOf(g, c.Id)] : new List<int>()));
                    if (self != null)
                        throttle.Parts.Add(new CockpitCallout { Id = self.Id, Label = self.Label, Position = self.Position, Kind = "part", Commands = self.Commands, Animation = self.Animation, GameKind = self.GameKind });
                }
                List<CockpitViewPlan> plans = CockpitViews.Plan(scene, head, callouts, grips, job.Width);
                if (job.ViewFilter.Length > 0)
                    plans = plans.Where(v => v.Id.IndexOf(job.ViewFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                int missing = scene.Materials.Count(x => x.Albedo == null && x.Name.Length > 0);
                if (missing > 0)
                    r.Notes.Add(missing.ToString(CultureInfo.InvariantCulture) + " materials without a readable texture (drawn in a flat colour)");
                if (behaviours.Nodes.Count == 0)
                    r.Notes.Add("no behaviour file: callouts from the part names");
                PictureWriter.SetInfo info = new PictureWriter.SetInfo
                {
                    Game = "MSFS2020",
                    Plane = r.Plane,
                    Planes = r.Planes,
                    Module = a.Package,
                    Source = Path.GetFileName(a.Interior),
                    GameVersion = job.GameVersion,
                    Notes = r.Notes
                };
                PictureWriter.Write(scene, plans, folder, info, (cur, total, title) =>
                {
                    if (job.Progress != null)
                        job.Progress(cur, total, "Microsoft Flight Simulator 2020, " + r.Plane + " pictures", title);
                }, job.ViewFilter.Length == 0);
                r.Views = plans.Count;
                r.Callouts = callouts.Count;
            }
        }

        private static int NodeOf(Gltf g, string name)
        {
            for (int i = 0; i < g.Nodes.Count; i++)
            {
                if (Str(g.Nodes[i], "name") == name)
                    return i;
            }
            return -1;
        }

        private static bool IsGripName(string name)
        {
            return Regex.IsMatch(name, "^HANDLING_(Yoke|Stick|Cyclic)(_1|_L|_Left)?$|^(ENGINE|HANDLING)_(Lever_Throttle|Throttle|Lever_Collective|Collective)(_1)?$", RegexOptions.IgnoreCase);
        }

        private static string KindOf(string kind)
        {
            switch (kind)
            {
                case "Knob":
                case "Selector":
                case "Wheel":
                case "Crank":
                    return "knob";
                case "Lever":
                case "Handle":
                case "Slider":
                case "Pull":
                case "Plunger":
                case "Collective":
                    return "lever";
                case "Push":
                case "Button":
                case "Key":
                    return "button";
                case "Guard":
                case "Cover":
                    return "cover";
                default:
                    return "switch";
            }
        }

        // The aircraft's textures, then the folders its texture.cfg falls back to.
        private static void AddTextureFolders(TextureLibrary lib, Aircraft a)
        {
            List<string> folders = new List<string>();
            foreach (string tex in SafeDirs(a.Folder, "texture*"))
                folders.Add(tex);
            // The model's own folder may sit in another package (variants): its texture folders too.
            string modelAircraft = Path.GetDirectoryName(Path.GetDirectoryName(a.Interior));
            if (!string.Equals(modelAircraft, a.Folder, StringComparison.OrdinalIgnoreCase))
                folders.AddRange(SafeDirs(modelAircraft, "texture*"));
            foreach (string tex in folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                lib.AddFolder(tex, false);
                string cfg = Path.Combine(tex, "texture.cfg");
                if (!File.Exists(cfg))
                    continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(cfg), "fallback\\.\\d+\\s*=\\s*([^\\r\\n;]+)", RegexOptions.IgnoreCase))
                {
                    try
                    {
                        string f = Path.GetFullPath(Path.Combine(tex, m.Groups[1].Value.Trim()));
                        if (Directory.Exists(f))
                            lib.AddFolder(f, false);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private static IEnumerable<string> SafeDirs(string folder, string pattern)
        {
            try
            {
                return Directory.Exists(folder) ? Directory.GetDirectories(folder, pattern) : new string[0];
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        // cameras.cfg: [VIEWS] eyepoint (feet from the reference datum: longitudinal, lateral, vertical) plus the
        // InitialXyz (metres: lateral, vertical, longitudinal) of the default pilot camera; the datum's place in the
        // model is flight_model.cfg's reference_datum_position (feet: longitudinal, lateral, vertical).
        internal static bool EyePoint(string aircraftFolder, out Vec3 eye)
        {
            eye = new Vec3(0, 0, 0);
            string cameras = Path.Combine(aircraftFolder, "cameras.cfg");
            string aircraftCfg = Path.Combine(aircraftFolder, "aircraft.cfg");
            string text = (File.Exists(cameras) ? File.ReadAllText(cameras) : string.Empty) + "\n" + (File.Exists(aircraftCfg) ? File.ReadAllText(aircraftCfg) : string.Empty);
            double[] eyepoint = Triple(text, "eyepoint");
            if (eyepoint == null)
                return false;
            string fm = Path.Combine(aircraftFolder, "flight_model.cfg");
            double[] datum = Triple((File.Exists(fm) ? File.ReadAllText(fm) : string.Empty) + "\n" + text, "reference_datum_position") ?? new double[3];
            double[] offset = new double[3];
            string camText = File.Exists(cameras) ? File.ReadAllText(cameras) : string.Empty;
            foreach (string section in Regex.Split(camText, "(?=^\\s*\\[CAMERADEFINITION)", RegexOptions.Multiline))
            {
                if (!Regex.IsMatch(section, "^\\s*Category\\s*=\\s*\"?Cockpit", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                    continue;
                double[] xyz = Triple(section, "InitialXyz");
                if (xyz == null)
                    continue;
                offset = xyz;
                if (Regex.IsMatch(section, "DefaultPilot", RegexOptions.IgnoreCase))
                    break;
            }
            double longitudinal = (datum[0] + eyepoint[0]) * Feet + offset[2];
            double lateral = (datum[1] + eyepoint[1]) * Feet + offset[0];
            double vertical = (datum[2] + eyepoint[2]) * Feet + offset[1];
            eye = new Vec3(longitudinal, vertical, lateral);
            return true;
        }

        private static double[] Triple(string text, string key)
        {
            Match m = Regex.Match(text, "^\\s*" + key + "\\s*=\\s*([-+0-9.eE]+)\\s*,\\s*([-+0-9.eE]+)\\s*,\\s*([-+0-9.eE]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (!m.Success)
                return null;
            double[] v = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (!double.TryParse(m.Groups[i + 1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                    return null;
            }
            return v;
        }

        // "DEICESYSTEM_SWITCH_AIRFRAME" -> "Deicesystem switch airframe".
        internal static string LooseLabel(string name)
        {
            string t = Regex.Replace(name, "([a-z])([A-Z])", "$1 $2").Replace('_', ' ').Trim().ToLowerInvariant();
            t = Regex.Replace(t, "\\s+", " ");
            return t.Length == 0 ? name : char.ToUpperInvariant(t[0]) + t.Substring(1);
        }

        // "ELECTRICAL_Switch_Battery_Master" -> "Battery Master switch (electrical)".
        internal static string Label(Match m)
        {
            string raw = m.Groups["rest"].Value;
            string number = Regex.Match(raw, "_(\\d+)$").Groups[1].Value;
            string rest = Regex.Replace(raw, "_?\\d+$", string.Empty).Replace('_', ' ').Trim();
            rest = Regex.Replace(rest, "([a-z])([A-Z])", "$1 $2");
            string kind = m.Groups["kind"].Value.ToLowerInvariant();
            string sys = m.Groups["sys"].Value.ToLowerInvariant().Replace('_', ' ');
            return (rest.Length > 0 ? rest + " " : string.Empty) + kind + (number.Length > 0 ? " " + number : string.Empty) + " (" + sys + ")";
        }

        // glTF (y up, z forward, x left, metres) to the scene (x forward, y up, z right).
        internal static Vec3 ToScene(Vec3 g)
        {
            return new Vec3(g.Z, g.Y, -g.X);
        }

        private static bool Hidden(List<Dictionary<string, object>> nodes, Dictionary<int, int> parent, int node)
        {
            for (int k = node, guard = 0; guard < 64; guard++)
            {
                // A node's own name decides for it; of its parents only collision, tutorial and hand nodes hide the
                // children (a "Hider" parent is the sim's switch to hide the yoke, its children are the yoke).
                string n = Str(nodes[k], "name");
                if (k == node ? SkipNode.IsMatch(n) : SkipParent.IsMatch(n))
                    return true;
                int p;
                if (!parent.TryGetValue(k, out p))
                    return false;
                k = p;
            }
            return false;
        }

        private static int SceneMaterialOf(Gltf g, int index, TextureLibrary lib, CockpitScene scene, Dictionary<int, int> map)
        {
            int found;
            if (map.TryGetValue(index, out found))
                return found;
            int result = -1;
            if (index >= 0 && index < g.Materials.Count)
            {
                Dictionary<string, object> mat = g.Materials[index];
                string name = Str(mat, "name");
                Dictionary<string, object> ext = Obj(mat, "extensions");
                Dictionary<string, object> pbr = Obj(mat, "pbrMetallicRoughness");
                List<object> factor = pbr != null && pbr.ContainsKey("baseColorFactor") ? pbr["baseColorFactor"] as List<object> : null;
                double alpha = factor != null && factor.Count >= 4 ? Convert.ToDouble(factor[3], CultureInfo.InvariantCulture) : 1;
                bool invisible = ext != null && (ext.ContainsKey("ASOBO_material_invisible") || ext.ContainsKey("ASOBO_material_glass") || ext.ContainsKey("ASOBO_material_windshield_v2") ||
                                                 ext.ContainsKey("ASOBO_material_windshield") || ext.ContainsKey("ASOBO_material_kitty_glass") || ext.ContainsKey("ASOBO_material_environment_occluder") ||
                                                 ext.ContainsKey("ASOBO_material_fake_terrain") || ext.ContainsKey("ASOBO_material_shadow_options") && name.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0);
                // Old-style Asobo materials name their type in extras.
                Dictionary<string, object> extras = Obj(mat, "extras");
                Dictionary<string, object> asoboMat = extras == null ? null : Obj(extras, "ASOBO_material_code") ?? extras;
                string code = asoboMat == null ? string.Empty : Str(asoboMat, "ASOBO_material_code");
                if (Regex.IsMatch(code, "Invisible|Glass|Windshield|EnvironmentOccluder|FakeTerrain", RegexOptions.IgnoreCase))
                    invisible = true;
                bool blend = string.Equals(Str(mat, "alphaMode"), "BLEND", StringComparison.OrdinalIgnoreCase);
                if (!invisible && (alpha >= 0.3 || !blend) && !Regex.IsMatch(name, "invisible|collision|glass|windshield|occluder|tuto", RegexOptions.IgnoreCase))
                {
                    SceneMaterial sm = new SceneMaterial { Name = name, AlphaTest = string.Equals(Str(mat, "alphaMode"), "MASK", StringComparison.OrdinalIgnoreCase) || blend };
                    if (factor != null && factor.Count >= 3)
                        sm.Color = new[] { ToByte(factor[0]), ToByte(factor[1]), ToByte(factor[2]) };
                    Dictionary<string, object> baseTex = pbr == null ? null : Obj(pbr, "baseColorTexture");
                    if (baseTex != null)
                    {
                        string uri = g.ImageUri((int)Num(baseTex, "index"));
                        if (uri.Length > 0)
                        {
                            sm.Albedo = lib.Get(uri);
                            if (sm.Albedo != null)
                                sm.Color = new[] { sm.Albedo.Mean[0], sm.Albedo.Mean[1], sm.Albedo.Mean[2] };
                        }
                    }
                    else
                    {
                        sm.Name = string.Empty; // a plain colour on purpose
                    }
                    if (ext != null && ext.ContainsKey("ASOBO_material_blend_gbuffer") && sm.Albedo == null)
                        sm = null; // a decal without its texture would cover the panel in one colour
                    if (sm != null)
                    {
                        result = scene.Materials.Count;
                        scene.Materials.Add(sm);
                    }
                }
            }
            else if (index < 0)
            {
                result = scene.Materials.Count;
                scene.Materials.Add(new SceneMaterial());
            }
            map[index] = result;
            return result;
        }

        private static byte ToByte(object v)
        {
            double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            // Linear factor to sRGB.
            d = d <= 0.0031308 ? d * 12.92 : 1.055 * Math.Pow(d, 1 / 2.4) - 0.055;
            return (byte)Math.Max(0, Math.Min(255, Math.Round(d * 255)));
        }

        // ------------------------------------------------------------------ the behaviours (interior xml)

        internal sealed class Behaviours
        {
            // Node -> the template that makes it interactive.
            public Dictionary<string, string> Nodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Node -> the sim events its template block names (K: events, B: input events).
            public Dictionary<string, List<string>> Events = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            private static readonly Regex TemplateKind = new Regex("_(Switch|Knob|Lever|Push|Button|Handle|Wheel|Toggle|Selector|Pull|Rocker|Slider|Key|Guard|Cover|Crank|Plunger|Yoke|Stick|Pedal|Pedals|Collective|Cyclic)s?(_|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            private static readonly Regex NotInteractive = new Regex("^ASOBO_(GT_|INSTRUMENT_Needle|.*_Emissive|.*_Needle|.*Indicator_)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            public static Behaviours Read(string xml)
            {
                Behaviours b = new Behaviours();
                if (string.IsNullOrEmpty(xml) || !File.Exists(xml))
                    return b;
                string text;
                try
                {
                    text = File.ReadAllText(xml);
                }
                catch (Exception)
                {
                    return b;
                }
                text = Regex.Replace(text, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
                // Each <UseTemplate Name="..."> ... </UseTemplate> (or self-closed): its NODE_ID, else the node the
                // template is named after (ASOBO_ELECTRICAL_Switch_Battery_Master_Template -> ELECTRICAL_Switch_Battery_Master).
                foreach (Match m in Regex.Matches(text, "<UseTemplate\\s+Name\\s*=\\s*\"(?<name>[^\"]+)\"\\s*(/>|>(?<body>.*?)</UseTemplate>)", RegexOptions.Singleline))
                {
                    string template = m.Groups["name"].Value;
                    string body = m.Groups["body"].Value;
                    if (NotInteractive.IsMatch(template) || !TemplateKind.IsMatch(template) && body.IndexOf("NODE_ID", StringComparison.Ordinal) < 0)
                        continue;
                    List<string> nodes = Regex.Matches(body, "<(NODE_ID|NODE_ID_\\w+)>\\s*([^<\\s]+)\\s*</").Cast<Match>().Select(x => x.Groups[2].Value).ToList();
                    if (nodes.Count == 0)
                    {
                        string derived = Regex.Replace(Regex.Replace(template, "^ASOBO_", string.Empty), "_Template$", string.Empty);
                        nodes.Add(derived);
                    }
                    List<string> events = Regex.Matches(body, "\\((?:>|&gt;)(K|B):(?:\\d+:)?([A-Za-z0-9_]+)").Cast<Match>().Select(x => x.Groups[1].Value + ":" + x.Groups[2].Value).ToList();
                    events.AddRange(Regex.Matches(body, "<(?:EVENT_ID|KEY_EVENT|INPUT_EVENT_ID_SOURCE|B_EVENT)\\w*>\\s*([A-Za-z0-9_]+)\\s*</").Cast<Match>().Select(x => x.Groups[1].Value));
                    foreach (string n in nodes)
                    {
                        if (!TemplateKind.IsMatch(template) && !Interactive.IsMatch(n))
                            continue;
                        if (!b.Nodes.ContainsKey(n))
                            b.Nodes[n] = template;
                        if (events.Count > 0)
                        {
                            List<string> l;
                            if (!b.Events.TryGetValue(n, out l))
                                b.Events[n] = l = new List<string>();
                            l.AddRange(events);
                        }
                    }
                }
                return b;
            }
        }

        // ------------------------------------------------------------------ glTF

        internal sealed class Gltf
        {
            public string Folder = string.Empty;
            public List<byte[]> Buffers = new List<byte[]>();
            public List<Dictionary<string, object>> Nodes, Meshes, Materials, Accessors, Views, Textures, Images, Scenes;

            public static Gltf Load(string file)
            {
                Dictionary<string, object> root = MiniJson.Parse(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                if (root == null)
                    return null;
                Gltf g = new Gltf { Folder = Path.GetDirectoryName(file) };
                foreach (Dictionary<string, object> b in Objects(root, "buffers"))
                {
                    string f = Path.Combine(g.Folder, Str(b, "uri"));
                    g.Buffers.Add(Str(b, "uri").Length > 0 && File.Exists(f) ? File.ReadAllBytes(f) : new byte[0]);
                }
                g.Nodes = Objects(root, "nodes").ToList();
                g.Meshes = Objects(root, "meshes").ToList();
                g.Materials = Objects(root, "materials").ToList();
                g.Accessors = Objects(root, "accessors").ToList();
                g.Views = Objects(root, "bufferViews").ToList();
                g.Textures = Objects(root, "textures").ToList();
                g.Images = Objects(root, "images").ToList();
                g.Scenes = Objects(root, "scenes").ToList();
                return g;
            }

            // Every node's world matrix (from the scene's roots; nodes no scene holds count as roots).
            public void World(out Dictionary<int, double[]> world, out Dictionary<int, int> parent)
            {
                world = new Dictionary<int, double[]>();
                parent = new Dictionary<int, int>();
                List<int> roots = new List<int>();
                Dictionary<string, object> scene = Scenes.FirstOrDefault();
                if (scene != null)
                    roots.AddRange(Ints(scene, "nodes"));
                if (roots.Count == 0)
                {
                    HashSet<int> children = new HashSet<int>(Nodes.SelectMany(n => Ints(n, "children")));
                    roots.AddRange(Enumerable.Range(0, Nodes.Count).Where(i => !children.Contains(i)));
                }
                Stack<Tuple<int, double[]>> stack = new Stack<Tuple<int, double[]>>();
                foreach (int r in roots)
                    stack.Push(Tuple.Create(r, Mat4.Identity()));
                while (stack.Count > 0)
                {
                    Tuple<int, double[]> t = stack.Pop();
                    if (t.Item1 < 0 || t.Item1 >= Nodes.Count || world.ContainsKey(t.Item1))
                        continue;
                    double[] m = Mat4.Multiply(t.Item2, Local(Nodes[t.Item1]));
                    world[t.Item1] = m;
                    foreach (int c in Ints(Nodes[t.Item1], "children"))
                    {
                        parent[c] = t.Item1;
                        stack.Push(Tuple.Create(c, m));
                    }
                }
            }

            public string ImageUri(int texture)
            {
                if (texture < 0 || texture >= Textures.Count)
                    return string.Empty;
                Dictionary<string, object> t = Textures[texture];
                int source = t.ContainsKey("source") ? (int)Num(t, "source") : -1;
                Dictionary<string, object> ext = Obj(t, "extensions");
                Dictionary<string, object> dds = ext == null ? null : Obj(ext, "MSFT_texture_dds");
                if (dds != null)
                    source = (int)Num(dds, "source");
                return source >= 0 && source < Images.Count ? Str(Images[source], "uri") : string.Empty;
            }

            // The values of an accessor as floats, with the buffer view's stride (MSFS interleaves its vertices).
            // Normalised integers are scaled; signed bytes (MSFS's normals and tangents) are normalised always; the
            // 16-bit texture coordinates of MSFS's vertex format are half floats.
            public float[] Accessor(int index, bool halfShorts)
            {
                if (index < 0 || index >= Accessors.Count)
                    return null;
                Dictionary<string, object> acc = Accessors[index];
                if (!acc.ContainsKey("bufferView"))
                    return null;
                int vi = (int)Num(acc, "bufferView");
                if (vi < 0 || vi >= Views.Count)
                    return null;
                Dictionary<string, object> view = Views[vi];
                int bi = (int)Num(view, "buffer");
                if (bi < 0 || bi >= Buffers.Count)
                    return null;
                // The primitives of a mesh share their accessors: read each once.
                long key = index * 2L + (halfShorts ? 1 : 0);
                float[] values;
                if (!cache.TryGetValue(key, out values))
                    cache[key] = values = ReadAccessor(acc, view, Buffers[bi], halfShorts);
                return values;
            }

            private readonly Dictionary<long, float[]> cache = new Dictionary<long, float[]>();
        }

        internal static float[] ReadAccessor(Dictionary<string, object> acc, Dictionary<string, object> view, byte[] buf, bool halfShorts)
        {
            int count = (int)Num(acc, "count");
            int comp = (int)Num(acc, "componentType");
            string type = Str(acc, "type");
            int n = type == "SCALAR" ? 1 : type == "VEC2" ? 2 : type == "VEC3" ? 3 : type == "VEC4" ? 4 : type == "MAT4" ? 16 : 1;
            int size = comp == 5126 || comp == 5125 ? 4 : comp == 5122 || comp == 5123 ? 2 : 1;
            int stride = view.ContainsKey("byteStride") && Num(view, "byteStride") > 0 ? (int)Num(view, "byteStride") : n * size;
            long start = (long)Num(view, "byteOffset") + (long)Num(acc, "byteOffset");
            bool normalized = acc.ContainsKey("normalized") && acc["normalized"] is bool && (bool)acc["normalized"];
            if (count <= 0 || start < 0 || start + (long)(count - 1) * stride + n * size > buf.Length)
                return null;
            float[] o = new float[count * n];
            for (int i = 0; i < count; i++)
            {
                int at = (int)(start + (long)i * stride);
                for (int c = 0; c < n; c++)
                {
                    int p = at + c * size;
                    float v;
                    switch (comp)
                    {
                        case 5126:
                            v = BitConverter.ToSingle(buf, p);
                            break;
                        case 5125:
                            v = BitConverter.ToUInt32(buf, p);
                            break;
                        case 5123:
                            v = BitConverter.ToUInt16(buf, p);
                            if (normalized)
                                v /= 65535f;
                            break;
                        case 5122:
                            v = halfShorts ? Half(BitConverter.ToUInt16(buf, p)) : normalized ? Math.Max(-1f, BitConverter.ToInt16(buf, p) / 32767f) : BitConverter.ToInt16(buf, p);
                            break;
                        case 5121:
                            v = buf[p];
                            if (normalized)
                                v /= 255f;
                            break;
                        case 5120:
                            v = Math.Max(-1f, (sbyte)buf[p] / 127f);
                            break;
                        default:
                            v = 0;
                            break;
                    }
                    o[i * n + c] = v;
                }
            }
            return o;
        }

        internal static float Half(ushort h)
        {
            int sign = (h >> 15) & 1, exp = (h >> 10) & 31, mant = h & 1023;
            float v;
            if (exp == 0)
                v = (float)(mant / 1024.0 * Math.Pow(2, -14));
            else if (exp == 31)
                v = 0;
            else
                v = (float)((1 + mant / 1024.0) * Math.Pow(2, exp - 15));
            return sign == 1 ? -v : v;
        }

        internal static double[] Local(Dictionary<string, object> node)
        {
            List<object> matrix = node.ContainsKey("matrix") ? node["matrix"] as List<object> : null;
            if (matrix != null && matrix.Count == 16)
                return matrix.Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
            double[] t = Vec(node, "translation", 0), s = Vec(node, "scale", 1);
            List<object> r = node.ContainsKey("rotation") ? node["rotation"] as List<object> : null;
            double[] q = r != null && r.Count == 4 ? new[] { Convert.ToDouble(r[3], CultureInfo.InvariantCulture), Convert.ToDouble(r[0], CultureInfo.InvariantCulture), Convert.ToDouble(r[1], CultureInfo.InvariantCulture), Convert.ToDouble(r[2], CultureInfo.InvariantCulture) } : new double[] { 1, 0, 0, 0 };
            return Mat4.Multiply(Mat4.Multiply(Mat4.Translation(t[0], t[1], t[2]), Mat4.Rotation(q)), Mat4.Scale(s[0], s[1], s[2]));
        }

        private static double[] Vec(Dictionary<string, object> node, string key, double fallback)
        {
            List<object> v = node.ContainsKey(key) ? node[key] as List<object> : null;
            if (v == null || v.Count < 3)
                return new[] { fallback, fallback, fallback };
            return v.Take(3).Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
        }

        private static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> o, string key)
        {
            object v;
            if (o != null && o.TryGetValue(key, out v) && v is List<object>)
                return ((List<object>)v).OfType<Dictionary<string, object>>();
            return new Dictionary<string, object>[0];
        }

        private static Dictionary<string, object> Obj(Dictionary<string, object> o, string key)
        {
            object v;
            return o != null && o.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        private static IEnumerable<int> Ints(Dictionary<string, object> o, string key)
        {
            object v;
            if (o != null && o.TryGetValue(key, out v) && v is List<object>)
                return ((List<object>)v).Select(x => (int)Convert.ToDouble(x, CultureInfo.InvariantCulture));
            return new int[0];
        }

        private static string Str(Dictionary<string, object> o, string key)
        {
            object v;
            return o != null && o.TryGetValue(key, out v) && v is string ? (string)v : string.Empty;
        }

        private static double Num(Dictionary<string, object> o, string key)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null)
                return 0;
            try
            {
                return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
