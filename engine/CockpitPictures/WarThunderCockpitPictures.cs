#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.CockpitPictures
{
    // War Thunder: the cockpit pictures of every <plane>_cockpit DynModel of content.hq\pkg_cockpits (read only):
    // the GRP2 packs (models and their _skeleton node trees), dynModelDesc.bin (zstd, binary BLK: each model's
    // materials and texture names) and the DxP2 texture packs. The models' vertex data and most textures are Oodle
    // Leviathan packed: they are unpacked by the War Thunder Oodle helper (tools\wt-oodle, a separate GPL-3 program
    // started as a process; without it the game is skipped with a note). Nothing obfuscated is read (aces.vromfs.bin
    // with the flight models and the cockpit instruments' setup, the other obfuscated vromfs, grp_hdr.vromfs.bin).
    // The eye is the pilot figure's head bone in the model's skeleton; the grips are the model's stick and throttle
    // nodes; the callouts are the model's own named control and instrument nodes (gears, flaps, trimmer, speed ...).
    internal static class WarThunderCockpitPictures
    {
        public const string Game = "WarThunder";

        public sealed class Job
        {
            public string GameRoot = string.Empty;
            public string OutRoot = string.Empty;
            public List<string> Only = new List<string>();
            public bool Force;
            public int Width = 1600;
            public string ViewFilter = string.Empty;
            public string OodleHelper = string.Empty;
            // zstd frames (dynModelDesc.bin, zstd-packed textures): bytes, offset, length -> raw bytes.
            public Func<byte[], int, int, byte[]> Zstd;
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

        public sealed class Model
        {
            public string Name = string.Empty;    // mig_21_f13_cockpit
            public string Key = string.Empty;     // mig_21_f13
            public WarThunderGrp Pack;
            public WarThunderGrp.Entry Entry;
        }

        public static string ResFolder(string gameRoot)
        {
            return Path.Combine(gameRoot ?? string.Empty, "content.hq", "pkg_cockpits", "res");
        }

        public static string GameVersion(string gameRoot)
        {
            try
            {
                string exe = Path.Combine(gameRoot, "win64", "aces.exe");
                if (File.Exists(exe))
                {
                    FileVersionInfo v = FileVersionInfo.GetVersionInfo(exe);
                    return (v.ProductVersion ?? v.FileVersion ?? string.Empty).Trim();
                }
            }
            catch (Exception)
            {
            }
            return string.Empty;
        }

        // The helper: JOYPRO_WT_OODLE, else tools\wt-oodle\bin\<Release|Debug>\net9.0\JoyPro.WtOodle.exe in the
        // engine's folder or one of its parents (a development checkout).
        public static string FindOodleHelper()
        {
            string env = Environment.GetEnvironmentVariable("JOYPRO_WT_OODLE");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
                return env;
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                foreach (string cfg in new[] { "Release", "Debug" })
                {
                    string p = Path.Combine(dir, "tools", "wt-oodle", "bin", cfg, "net9.0", "JoyPro.WtOodle.exe");
                    if (File.Exists(p))
                        return p;
                }
                string side = Path.Combine(dir, "JoyPro.WtOodle.exe");
                if (File.Exists(side))
                    return side;
                dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }
            return string.Empty;
        }

        public static List<Model> Models(string resFolder, List<string> warnings)
        {
            List<Model> list = new List<Model>();
            if (!Directory.Exists(resFolder))
                return list;
            foreach (string grp in Directory.GetFiles(resFolder, "*.grp", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                WarThunderGrp pack;
                try
                {
                    pack = WarThunderGrp.Read(grp);
                }
                catch (Exception ex)
                {
                    if (warnings != null)
                        warnings.Add(Path.GetFileName(grp) + ": " + ex.Message);
                    continue;
                }
                foreach (WarThunderGrp.Entry e in pack.Entries)
                {
                    if (e.ClassId != WarThunderGrp.DynModelClass || !e.Name.EndsWith("_cockpit", StringComparison.Ordinal))
                        continue;
                    if (list.Any(m => m.Name == e.Name))
                        continue;
                    list.Add(new Model { Name = e.Name, Key = e.Name.Substring(0, e.Name.Length - "_cockpit".Length), Pack = pack, Entry = e });
                }
            }
            return list.OrderBy(m => m.Key, StringComparer.Ordinal).ToList();
        }

        // The names a cockpit is known by: the model's name ("mig_21_f13"), written with dashes ("mig-21-f13"), and
        // shortened by its last words while a type number is left ("f_16a_block_10_iaf" -> "f_16a_block_10",
        // "f_16a_block", "f_16a"). A shortened name belongs to the shortest model name that has it (OwnNames).
        public static List<string> Names(string key)
        {
            List<string> names = new List<string> { key, key.Replace('_', '-') };
            List<string> parts = key.Split('_').ToList();
            while (parts.Count > 2)
            {
                parts.RemoveAt(parts.Count - 1);
                string shorter = string.Join("_", parts);
                if (!Regex.IsMatch(shorter, "[0-9]"))
                    break;
                names.Add(shorter);
                names.Add(shorter.Replace('_', '-'));
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The names of each model: its own two, and the shortened ones that no other model has as its own name and
        // no shorter model name took first.
        public static Dictionary<string, List<string>> OwnNames(IEnumerable<string> keys)
        {
            List<string> all = keys.Distinct(StringComparer.Ordinal).OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).ToList();
            HashSet<string> modelNames = new HashSet<string>(all.Select(NameKey), StringComparer.Ordinal);
            HashSet<string> claimed = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, List<string>> map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string k in all)
            {
                string mine = NameKey(k);
                List<string> names = new List<string>();
                foreach (string n in Names(k))
                {
                    string nk = NameKey(n);
                    if (nk == mine || names.Any(x => NameKey(x) == nk) || (!modelNames.Contains(nk) && !claimed.Contains(nk)))
                        names.Add(n);
                }
                foreach (string n in names)
                    claimed.Add(NameKey(n));
                map[k] = names;
            }
            return map;
        }

        private static string NameKey(string s)
        {
            return Regex.Replace((s ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]", string.Empty);
        }

        public static List<SetResult> Build(Job job)
        {
            List<SetResult> results = new List<SetResult>();
            string res = ResFolder(job.GameRoot);
            if (!Directory.Exists(res))
            {
                results.Add(new SetResult { Plane = "War Thunder", Error = "content.hq\\pkg_cockpits\\res was not found (high-quality cockpit pack not installed)" });
                return results;
            }
            string helper = string.IsNullOrEmpty(job.OodleHelper) ? FindOodleHelper() : job.OodleHelper;
            if (helper.Length == 0 || !File.Exists(helper))
            {
                results.Add(new SetResult { Plane = "War Thunder", Error = "the War Thunder Oodle helper (tools\\wt-oodle, a maintainer tool) is not built; War Thunder's cockpit models are Oodle packed" });
                return results;
            }
            List<string> warnings = new List<string>();
            List<Model> models = Models(res, warnings);
            Dictionary<string, WarThunderBlk> desc = Descriptions(Path.Combine(res, "dynModelDesc.bin"), job.Zstd);
            Dictionary<string, WarThunderDxp.Texture> textures = TextureIndex(res);
            string version = GameVersion(job.GameRoot);
            Dictionary<string, List<string>> ownNames = OwnNames(models.Select(x => x.Key));
            Dictionary<string, TextureImage> images = new Dictionary<string, TextureImage>(StringComparer.Ordinal);
            using (WarThunderOodle oodle = new WarThunderOodle(helper))
            {
                int i = 0;
                foreach (Model m in models)
                {
                    i++;
                    if (job.Cancelled != null && job.Cancelled())
                        break;
                    List<string> names = ownNames[m.Key];
                    if (job.Only.Count > 0 && !job.Only.Any(o => names.Any(n => string.Equals(n, o, StringComparison.OrdinalIgnoreCase)) || m.Key.IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;
                    string folder = Path.Combine(job.OutRoot, Regex.Replace(m.Key, "[^A-Za-z0-9_\\-]", "_"));
                    SetResult r = new SetResult { Plane = m.Key, Planes = names, Folder = folder };
                    results.Add(r);
                    string manifest = Path.Combine(folder, PictureWriter.ManifestName);
                    if (!job.Force && File.Exists(manifest) && File.GetLastWriteTimeUtc(manifest) > File.GetLastWriteTimeUtc(m.Pack.Path) &&
                        File.ReadAllText(manifest).Contains("\"generator\":" + PictureWriter.GeneratorVersion.ToString(CultureInfo.InvariantCulture)))
                    {
                        r.Skipped = true;
                        continue;
                    }
                    if (job.Progress != null)
                        job.Progress(i, models.Count, "War Thunder, cockpit pictures", m.Key);
                    try
                    {
                        WarThunderBlk d;
                        desc.TryGetValue(m.Name, out d);
                        // The decoded textures are kept for the next cockpits (national versions and shared gauges)
                        // up to about 600 MB.
                        if (images.Values.Where(x => x != null).Sum(x => (long)x.Width * x.Height * 4) > 600L << 20)
                            images.Clear();
                        BuildOne(job, oodle, m, d, textures, images, folder, version, r);
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

        // dynModelDesc.bin: a 4-byte header, then a zstd frame with a binary BLK: one block per model.
        public static Dictionary<string, WarThunderBlk> Descriptions(string file, Func<byte[], int, int, byte[]> zstd)
        {
            Dictionary<string, WarThunderBlk> map = new Dictionary<string, WarThunderBlk>(StringComparer.Ordinal);
            if (!File.Exists(file) || zstd == null)
                return map;
            byte[] packed = File.ReadAllBytes(file);
            if (packed.Length < 8)
                return map;
            byte[] raw = zstd(packed, 4, packed.Length - 4);
            WarThunderBlk root = WarThunderBlk.Parse(raw, 0);
            foreach (WarThunderBlk b in root.Blocks)
            {
                if (!map.ContainsKey(b.Name))
                    map[b.Name] = b;
            }
            return map;
        }

        // Every texture of the cockpit packs by its base name; the best quality wins (hq, then base, then thumbnail).
        public static Dictionary<string, WarThunderDxp.Texture> TextureIndex(string resFolder)
        {
            Dictionary<string, WarThunderDxp.Texture> map = new Dictionary<string, WarThunderDxp.Texture>(StringComparer.Ordinal);
            Dictionary<string, int> quality = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string f in Directory.GetFiles(resFolder, "*.dxp.bin", SearchOption.AllDirectories))
            {
                WarThunderDxp p;
                try
                {
                    p = WarThunderDxp.Read(f);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (WarThunderDxp.Texture t in p.Textures)
                {
                    if (t.Header.MemSize <= 0)
                        continue;
                    int q;
                    string key = WarThunderDxp.BaseName(t.Name, out q);
                    int had;
                    if (!quality.TryGetValue(key, out had) || q > had)
                    {
                        quality[key] = q;
                        map[key] = t;
                    }
                }
            }
            return map;
        }

        // ------------------------------------------------------------------ the scene

        // Parts that are not the cockpit as the pilot sees it: damage, glass dirt, flames, the pilot figure's parts.
        // The outside of the aircraft the model also has (propellers, wheels and gear legs, control surfaces, pylons).
        private static readonly Regex SkipNode = new Regex("^(holes?\\d*|glass_dirt|glass_lamp|fire\\d*_\\d*|jet_flame\\d*|flare\\d*|.*_dmg\\d*|.*broken.*|chute\\d*|emtr_.*|.*_dm|Bip01.*|skin\\d*" +
            "|prop\\d*_\\d*|prop_side_?\\d*|wheel_[lrc]\\d*|gear_[lrc]\\d*|gear_2c\\d*|nozzle\\d+_\\d+|radiator\\d+_\\d+|wing_[lr]\\d*|tail\\d*|aileron_[lr]\\d*|elevator\\d*" +
            "|rudder\\d*|flap\\d*_[lr]|slat_[lr]\\d*|airbrake_[lr]\\d*|.*pylon.*|antenna\\d*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Material classes that are not drawn: glass, invisible helpers, blended decals (dirt, damage), muzzle fire,
        // the gunsight's reflected image.
        private static readonly Regex SkipClass = new Regex("glass|null|alpha_blend|weapon_fire|crack|gyro_sight|collimator|flame|refraction|distortion|shadow", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void BuildOne(Job job, WarThunderOodle oodle, Model model, WarThunderBlk desc, Dictionary<string, WarThunderDxp.Texture> textures, Dictionary<string, TextureImage> images, string folder, string version, SetResult r)
        {
            WarThunderDynModel.Unpacker unpack = (compression, data, offset, length, raw) =>
            {
                if (compression == 2)
                    return oodle.Decode(data, offset, length, raw, 0);
                if (compression == 1 && job.Zstd != null)
                    return job.Zstd(data, offset, length);
                throw new InvalidDataException("packing " + compression + " is not supported");
            };
            WarThunderDynModel dm = WarThunderDynModel.Read(model.Pack.ReadEntry(model.Entry), unpack);
            WarThunderSkeleton skel = null;
            WarThunderGrp.Entry se = model.Pack.Find(model.Name + "_skeleton", WarThunderGrp.SkeletonClass);
            if (se != null)
                skel = WarThunderSkeleton.Read(model.Pack.ReadEntry(se), unpack);
            else
                r.Notes.Add("the model has no skeleton: parts are drawn where the model stores them");
            List<WarThunderBlk> mats = desc != null && desc.Block("matR") != null ? desc.Block("matR").Blocks : new List<WarThunderBlk>();
            List<string> texNames = desc != null && desc.Block("tex") != null ? desc.Block("tex").Params.Where(p => p.Key == "tex").Select(p => p.Value as string ?? string.Empty).ToList() : dm.TextureNames;
            if (desc == null)
                r.Notes.Add("no material description in dynModelDesc.bin: flat colours");

            // The eye: the pilot figure's head bone (a little up and forward to the eyes); a model without the figure
            // (some bombers' cockpits): above and behind the stick's pivot, else behind the middle of the instruments
            // (the pivots of the model's instrument nodes).
            Vec3? headBone = null;
            string eyeNote = null;
            int hb;
            if (skel != null && skel.ByName.TryGetValue("Bip01 Head01", out hb))
            {
                headBone = ToScene(Mat4.Point(skel.World[hb], 0, 0, 0)) + new Vec3(0.08, 0.1, 0);
            }
            else if (skel != null)
            {
                List<Vec3> sticks = new List<Vec3>(), gauges = new List<Vec3>();
                foreach (string n in dm.NodeNames.Values)
                {
                    int sk;
                    if (!skel.ByName.TryGetValue(n, out sk))
                        continue;
                    Vec3 at = ToScene(Mat4.Point(skel.World[sk], 0, 0, 0));
                    if (Regex.IsMatch(n, "^(stick|cyclic)_(elevator|ailerons)$", RegexOptions.IgnoreCase))
                        sticks.Add(at);
                    CalloutText t = Label(n);
                    if (t != null && t.Kind == "gauge")
                        gauges.Add(at);
                }
                if (sticks.Count > 0)
                {
                    Vec3 s = sticks.OrderBy(v => Math.Abs(v.Z)).First();
                    headBone = s + new Vec3(-0.45, 0.65, 0);
                    eyeNote = "the model has no pilot figure: the eye is placed above and behind the stick";
                }
                else if (gauges.Count >= 3)
                {
                    Vec3 c = new Vec3(gauges.Average(v => v.X), gauges.Average(v => v.Y), gauges.Average(v => v.Z));
                    headBone = c + new Vec3(-0.7, 0.3, 0);
                    eyeNote = "the model has no pilot figure and no stick: the eye is placed behind the middle of the instruments";
                }
            }

            CockpitScene scene = new CockpitScene();
            Dictionary<int, int> materialOf = new Dictionary<int, int>();
            HashSet<string> missingTex = new HashSet<string>(StringComparer.Ordinal), undecodable = new HashSet<string>(StringComparer.Ordinal);
            int part = 0, far = 0;
            Dictionary<int, List<int>> nodeMeshes = new Dictionary<int, List<int>>();
            foreach (WarThunderDynModel.Rigid rg in dm.Rigids)
            {
                part++;
                string nodeName;
                dm.NodeNames.TryGetValue(rg.Node, out nodeName);
                nodeName = nodeName ?? string.Empty;
                if (SkipNode.IsMatch(nodeName))
                    continue;
                int sk = -1;
                double[] world = Mat4.Identity();
                if (skel != null && skel.ByName.TryGetValue(nodeName, out sk))
                    world = skel.World[sk];
                double[] nmat = Mat4.NormalMatrix(world);
                foreach (WarThunderDynModel.Element el in rg.Elements)
                {
                    if (el.Transparent || el.VertexData < 0 || el.VertexData >= dm.Vdata.Count || el.Faces <= 0)
                        continue;
                    WarThunderBlk mat = el.Material >= 0 && el.Material < mats.Count ? mats[el.Material] : null;
                    string cls = mat != null ? mat.GetString("cls") ?? string.Empty : string.Empty;
                    if (SkipClass.IsMatch(cls))
                        continue;
                    int material;
                    if (!materialOf.TryGetValue(el.Material, out material))
                    {
                        material = scene.Materials.Count;
                        scene.Materials.Add(Material(mat, texNames, textures, images, oodle, job.Zstd, missingTex, undecodable));
                        materialOf[el.Material] = material;
                    }
                    SceneMesh mesh = Mesh(dm, el, world, nmat, material, part, nodeName);
                    if (mesh == null)
                        continue;
                    if (headBone.HasValue)
                    {
                        // Parts far from the pilot (wings, tail, wheels, the nose) are the outside of the aircraft.
                        Vec3 a, b;
                        CockpitScene one = new CockpitScene();
                        one.Meshes.Add(mesh);
                        one.Bounds(new[] { 0 }, out a, out b);
                        if (((a + b) * 0.5 - headBone.Value).Length > 2.6 || (b - a).Length > 6)
                        {
                            far++;
                            continue;
                        }
                    }
                    mesh.Node = sk;
                    if (sk >= 0)
                    {
                        List<int> l;
                        if (!nodeMeshes.TryGetValue(sk, out l))
                            nodeMeshes[sk] = l = new List<int>();
                        l.Add(scene.Meshes.Count);
                    }
                    scene.Meshes.Add(mesh);
                }
            }
            if (scene.Meshes.Count == 0)
            {
                r.Error = "no cockpit geometry could be read";
                return;
            }
            if (missingTex.Count > 0)
                r.Notes.Add(missingTex.Count.ToString(CultureInfo.InvariantCulture) + " textures not in the cockpit packs (flat colour)");
            if (undecodable.Count > 0)
                r.Notes.Add(undecodable.Count.ToString(CultureInfo.InvariantCulture) + " textures in a format JoyPro does not decode (flat colour)");
            if (dm.Skins > 0)
                r.Notes.Add("the pilot figure (skinned mesh) is not drawn");

            Vec3 mn, mx;
            scene.Bounds(Enumerable.Range(0, scene.Meshes.Count), out mn, out mx);
            // Grips: the model's stick and throttle nodes with everything under them.
            List<CockpitGrip> grips = new List<CockpitGrip>();
            if (skel != null)
            {
                AddGrip(scene, skel, dm, nodeMeshes, "stick", n => Regex.IsMatch(n, "^(stick|cyclic)(_|$)", RegexOptions.IgnoreCase) && !Regex.IsMatch(n, "hand|_base", RegexOptions.IgnoreCase), grips);
                AddGrip(scene, skel, dm, nodeMeshes, "throttle", n => Regex.IsMatch(n, "^(throttle|collective)(_?\\d+)?$", RegexOptions.IgnoreCase), grips);
            }
            Vec3 head = headBone.HasValue ? headBone.Value : new Vec3((mn.X + mx.X) / 2, mn.Y + (mx.Y - mn.Y) * 0.7, 0);
            if (eyeNote != null)
                r.Notes.Add(eyeNote);
            else if (!headBone.HasValue)
                r.Notes.Add("the model has no pilot figure, stick or instruments: the eye is the middle of the model");

            // Callouts: the model's control and instrument nodes.
            List<CockpitCallout> callouts = new List<CockpitCallout>();
            if (skel != null)
            {
                foreach (KeyValuePair<int, string> kv in dm.NodeNames.OrderBy(k => k.Key))
                {
                    CalloutText text = Label(kv.Value);
                    if (text == null)
                        continue;
                    int sk;
                    if (!skel.ByName.TryGetValue(kv.Value, out sk))
                        continue;
                    List<int> meshes = nodeMeshes.Where(x => skel.IsUnder(x.Key, sk)).SelectMany(x => x.Value).ToList();
                    if (meshes.Count == 0)
                        continue;
                    Vec3 a, b;
                    scene.Bounds(meshes, out a, out b);
                    if ((b - a).Length > 1.2)
                        continue;
                    Vec3 at = (a + b) * 0.5;
                    // One callout per instrument: the needles of one dial (altitude_hour, altitude_min) merge.
                    string kind = Regex.Replace(text.Label, " \\(.*\\)$", string.Empty);
                    CockpitCallout same = callouts.FirstOrDefault(c => Regex.Replace(c.Label, " \\(.*\\)$", string.Empty) == kind && (c.Position - at).Length < 0.08);
                    if (same != null)
                    {
                        if (!same.Args.Contains(kv.Value))
                            same.Args.Add(kv.Value);
                        continue;
                    }
                    CockpitCallout co = new CockpitCallout
                    {
                        Id = "node:" + kv.Value,
                        Label = text.Label,
                        Position = at,
                        Kind = text.Kind
                    };
                    co.Args.Add(kv.Value);
                    co.Commands.Add(new CalloutCommand { Name = kv.Value, Category = "War Thunder model node", Id = kv.Value });
                    co.Animation = new ControlAnimation { Source = "animated node", Name = kv.Value };
                    co.GameKind = text.Kind;
                    string area = CockpitViews.AreaOf(at, head);
                    co.Panel = area;
                    co.PanelTitle = XPlaneCockpitPictures.AreaTitle(area);
                    callouts.Add(co);
                }
            }
            List<CockpitViewPlan> views = CockpitViews.Plan(scene, head, callouts, grips, job.Width);
            if (job.ViewFilter.Length > 0)
                views = views.Where(v => v.Id.IndexOf(job.ViewFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            PictureWriter.SetInfo info = new PictureWriter.SetInfo
            {
                Game = Game,
                Plane = r.Plane,
                Planes = r.Planes,
                Module = model.Name,
                Source = "content.hq/pkg_cockpits/res/" + Path.GetFileName(model.Pack.Path) + ":" + model.Name,
                GameVersion = version,
                Notes = r.Notes
            };
            PictureWriter.Write(scene, views, folder, info, (cur, total, title) =>
            {
                if (job.Progress != null)
                    job.Progress(cur, total, "War Thunder, " + r.Plane + " pictures", title);
            }, job.ViewFilter.Length == 0);
            r.Views = views.Count;
            r.Callouts = callouts.Count + grips.Sum(g => g.Parts.Count);
        }

        // DagorEngine (y up, left-handed: z to the left) to the scene (x forward, y up, z right).
        private static Vec3 ToScene(Vec3 v)
        {
            return new Vec3(v.X, v.Y, -v.Z);
        }

        public static SceneMesh Mesh(WarThunderDynModel dm, WarThunderDynModel.Element el, double[] world, double[] nmat, int material, int part, string name)
        {
            WarThunderDynModel.VertexData vd = dm.Vdata[el.VertexData];
            int posAt = -1, norAt = -1, uvAt = -1, posType = 0, norType = 0, uvType = 0, ofs = 0;
            foreach (WarThunderDynModel.Channel c in vd.Channels)
            {
                if (c.Usage == 0 && posAt < 0)
                {
                    posAt = ofs;
                    posType = c.Type;
                }
                else if (c.Usage == 1 && norAt < 0)
                {
                    norAt = ofs;
                    norType = c.Type;
                }
                else if (c.Usage == 3 && c.Index == 0 && uvAt < 0)
                {
                    uvAt = ofs;
                    uvType = c.Type;
                }
                ofs += WarThunderDynModel.TypeSize(c.Type);
            }
            if (posAt < 0)
                return null;
            int first = el.StartIndex, count = el.Faces * 3;
            if (first < 0 || first + count > vd.Indices.Length)
                return null;
            Dictionary<int, int> remap = new Dictionary<int, int>();
            List<float> pos = new List<float>(), nor = new List<float>(), uv = new List<float>();
            int[] idx = new int[count];
            float[] t = new float[4];
            for (int k = 0; k < count; k++)
            {
                int v = vd.Indices[first + k] + el.BaseVertex;
                if (v < 0 || v >= vd.Count)
                    return null;
                int m;
                if (!remap.TryGetValue(v, out m))
                {
                    m = remap.Count;
                    remap[v] = m;
                    int at = v * vd.Stride;
                    WarThunderDynModel.ReadChannel(vd.Vertices, at + posAt, posType, 0, t);
                    double x = t[0], y = t[1], z = t[2];
                    if (dm.BoundPack && (posType == 10 || posType == 9 || posType == 7 || posType == 6))
                    {
                        x = x * dm.PosScale[0] + dm.PosOffset[0];
                        y = y * dm.PosScale[1] + dm.PosOffset[1];
                        z = z * dm.PosScale[2] + dm.PosOffset[2];
                    }
                    Vec3 w = ToScene(Mat4.Point(world, x, y, z));
                    pos.Add((float)w.X);
                    pos.Add((float)w.Y);
                    pos.Add((float)w.Z);
                    if (norAt >= 0)
                    {
                        WarThunderDynModel.ReadChannel(vd.Vertices, at + norAt, norType, 1, t);
                        double nx = t[0], ny = t[1], nz = t[2];
                        if (norType == 4 || norType == 5)
                        {
                            nx = nx * 2 - 1;
                            ny = ny * 2 - 1;
                            nz = nz * 2 - 1;
                        }
                        Vec3 n = ToScene(Mat4.Direction(nmat, nx, ny, nz)).Normalized;
                        nor.Add((float)n.X);
                        nor.Add((float)n.Y);
                        nor.Add((float)n.Z);
                    }
                    if (uvAt >= 0)
                    {
                        WarThunderDynModel.ReadChannel(vd.Vertices, at + uvAt, uvType, 3, t);
                        uv.Add(t[0]);
                        uv.Add(t[1]);
                    }
                }
                idx[k] = m;
            }
            return new SceneMesh
            {
                Name = name,
                Material = material,
                Part = part,
                Positions = pos.ToArray(),
                Normals = nor.Count > 0 ? nor.ToArray() : null,
                Uvs = uv.Count > 0 ? uv.ToArray() : null,
                Indices = idx
            };
        }

        private static SceneMaterial Material(WarThunderBlk mat, List<string> texNames, Dictionary<string, WarThunderDxp.Texture> textures, Dictionary<string, TextureImage> images, WarThunderOodle oodle, Func<byte[], int, int, byte[]> zstd, HashSet<string> missing, HashSet<string> undecodable)
        {
            SceneMaterial sm = new SceneMaterial();
            if (mat == null)
                return sm;
            string cls = mat.GetString("cls") ?? string.Empty;
            string par = mat.GetString("par") ?? string.Empty;
            sm.Name = cls;
            sm.AlphaTest = par.IndexOf("atest", StringComparison.OrdinalIgnoreCase) >= 0;
            sm.SelfIllumination = cls.IndexOf("illum", StringComparison.OrdinalIgnoreCase) >= 0;
            float[] diff = mat.Get("diff") as float[];
            if (diff != null && diff.Length >= 3)
                sm.Color = new[] { (byte)Math.Max(0, Math.Min(255, diff[0] * 255)), (byte)Math.Max(0, Math.Min(255, diff[1] * 255)), (byte)Math.Max(0, Math.Min(255, diff[2] * 255)) };
            int t0 = mat.GetInt("t0", -1);
            if (t0 < 0 || t0 >= texNames.Count)
                return sm;
            int q;
            string key = WarThunderDxp.BaseName(texNames[t0], out q);
            sm.Name = cls + " " + key;
            TextureImage img;
            if (!images.TryGetValue(key, out img))
            {
                WarThunderDxp.Texture t;
                if (textures.TryGetValue(key, out t))
                {
                    try
                    {
                        img = DecodeTexture(t, oodle, zstd, 512);
                    }
                    catch (Exception)
                    {
                        img = null;
                    }
                }
                images[key] = img;
            }
            if (img == null)
            {
                if (textures.ContainsKey(key))
                    undecodable.Add(key);
                else
                    missing.Add(key);
            }
            if (img != null)
            {
                sm.Albedo = img;
                sm.Color = new[] { img.Mean[0], img.Mean[1], img.Mean[2] };
                // The texture's alpha holds masks (paint, metalness) unless the material tests it.
            }
            return sm;
        }

        // A DxP2 texture as a TextureImage: unpack, take level 0 (the end of the data when the mips are stored
        // smallest first), wrap it in a DDS header (BC7 in a DX10 header) and decode at most about maxSize * 2.
        public static TextureImage DecodeTexture(WarThunderDxp.Texture t, WarThunderOodle oodle, Func<byte[], int, int, byte[]> zstd, int maxSize)
        {
            DdsxHeader h = t.Header;
            if ((h.Flags & (DdsxHeader.CubeTex | DdsxHeader.VolTex)) != 0)
                return null;
            byte[] packed = new byte[t.Size > 0 ? t.Size : h.MemSize];
            using (FileStream f = new FileStream(t.Pack, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                f.Seek(t.Offset, SeekOrigin.Begin);
                int got = 0;
                while (got < packed.Length)
                {
                    int n = f.Read(packed, got, packed.Length - got);
                    if (n <= 0)
                        break;
                    got += n;
                }
            }
            byte[] raw;
            switch (h.Compression)
            {
                case DdsxHeader.Oodle:
                    raw = oodle.Decode(packed, 0, packed.Length, h.MemSize, 0);
                    break;
                case DdsxHeader.Zstd:
                    if (zstd == null)
                        return null;
                    raw = zstd(packed, 0, packed.Length);
                    break;
                case DdsxHeader.Zlib:
                    using (MemoryStream ms = new MemoryStream(packed, 2, packed.Length - 2))
                    using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
                    using (MemoryStream o = new MemoryStream())
                    {
                        ds.CopyTo(o);
                        raw = o.ToArray();
                    }
                    break;
                case 0:
                    raw = packed;
                    break;
                default:
                    return null;
            }
            return ToImage(h, raw, maxSize);
        }

        public static TextureImage ToImage(DdsxHeader h, byte[] raw, int maxSize)
        {
            int level0 = h.SurfaceSize(0);
            if (level0 <= 0 || raw == null || raw.Length < level0)
                return null;
            int start = (h.Flags & DdsxHeader.RevMipOrder) != 0 ? raw.Length - level0 : 0;
            byte[] dds = Dds(h, raw, start, level0);
            return dds == null ? null : TextureImage.Decode(dds, "texture.dds", maxSize);
        }

        public static byte[] Dds(DdsxHeader h, byte[] data, int start, int size)
        {
            string four = h.FourCC;
            bool dx10 = four == "BC7 ";
            uint pfFlags, fourcc = 0, bits = 0, rm = 0, gm = 0, bm = 0, am = 0;
            if (four == "DXT1" || four == "DXT3" || four == "DXT5" || four == "ATI1" || four == "ATI2" || four == "BC4U" || four == "BC5U")
            {
                pfFlags = 4;
                fourcc = h.Format;
            }
            else if (dx10)
            {
                pfFlags = 4;
                fourcc = 0x30315844; // "DX10"
            }
            else if (h.Format == 21 || h.Format == 22) // A8R8G8B8 / X8R8G8B8
            {
                pfFlags = h.Format == 21 ? 0x41u : 0x40u;
                bits = 32;
                rm = 0x00FF0000;
                gm = 0x0000FF00;
                bm = 0x000000FF;
                am = 0xFF000000;
            }
            else if (h.Format == 32) // A8B8G8R8
            {
                pfFlags = 0x41;
                bits = 32;
                rm = 0x000000FF;
                gm = 0x0000FF00;
                bm = 0x00FF0000;
                am = 0xFF000000;
            }
            else if (h.Format == 50) // L8
            {
                pfFlags = 0x20000;
                bits = 8;
                rm = gm = bm = 0xFF;
            }
            else
            {
                return null;
            }
            int head = 128 + (dx10 ? 20 : 0);
            byte[] dds = new byte[head + size];
            Action<int, uint> put = (o, v) => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, dds, o, 4);
            dds[0] = (byte)'D';
            dds[1] = (byte)'D';
            dds[2] = (byte)'S';
            dds[3] = (byte)' ';
            put(4, 124);
            put(8, 0x1007);
            put(12, (uint)h.Height);
            put(16, (uint)h.Width);
            put(20, (uint)size);
            put(28, 1);
            put(76, 32);
            put(80, pfFlags);
            put(84, fourcc);
            put(88, bits);
            put(92, rm);
            put(96, gm);
            put(100, bm);
            put(104, am);
            put(108, 0x1000);
            if (dx10)
            {
                put(128, 98); // DXGI_FORMAT_BC7_UNORM
                put(132, 3);  // texture 2D
                put(140, 1);
            }
            Buffer.BlockCopy(data, start, dds, head, size);
            return dds;
        }

        // ------------------------------------------------------------------ grips and callouts

        private static void AddGrip(CockpitScene scene, WarThunderSkeleton skel, WarThunderDynModel dm, Dictionary<int, List<int>> nodeMeshes, string device, Func<string, bool> isGrip, List<CockpitGrip> grips)
        {
            List<int> roots = new List<int>();
            foreach (string n in dm.NodeNames.Values)
            {
                int sk;
                if (isGrip(n) && skel.ByName.TryGetValue(n, out sk))
                    roots.Add(sk);
            }
            // The topmost of them (stick_ailerons under stick_elevator is one grip).
            roots = roots.Where(x => !roots.Any(o => o != x && skel.IsUnder(x, o))).Distinct().ToList();
            foreach (int root in roots)
            {
                List<int> meshes = nodeMeshes.Where(x => skel.IsUnder(x.Key, root)).SelectMany(x => x.Value).Distinct().ToList();
                if (meshes.Count == 0)
                    continue;
                Vec3 a, b;
                scene.Bounds(meshes, out a, out b);
                // A linkage rod (a second engine's lever rod under the floor) is not a grip.
                if ((b - a).Length > 1.5 || (b - a).Length < 0.05 || meshes.All(k => Il2CockpitPictures.IsRod(scene.Meshes[k])))
                    continue;
                CockpitGrip g = grips.FirstOrDefault(x => x.Device == device && (x.Pivot - (a + b) * 0.5).Length < 0.35);
                if (g != null)
                {
                    g.Meshes = g.Meshes.Concat(meshes).Distinct().ToList();
                    scene.Bounds(g.Meshes, out a, out b);
                    g.Pivot = (a + b) * 0.5;
                    continue;
                }
                grips.Add(new CockpitGrip { Device = device, Meshes = meshes, Pivot = (a + b) * 0.5, Animation = new ControlAnimation { Source = "animated node", Name = root >= 0 && root < skel.Names.Count ? skel.Names[root] : string.Empty } });
            }
        }

        public sealed class CalloutText
        {
            public string Label = string.Empty;
            public string Kind = string.Empty;
        }

        // The model's control and instrument node names (the names War Thunder's cockpits share) and what they are.
        private static readonly Tuple<string, string, string>[] NodeLabels =
        {
            Tuple.Create("^gears?\\d*$", "Landing gear lever", "lever"),
            Tuple.Create("^flaps\\d*$", "Flaps lever", "lever"),
            Tuple.Create("^airbrake_lever\\d*$", "Air brake lever", "lever"),
            Tuple.Create("^trimmer\\d*$", "Trim control", "knob"),
            Tuple.Create("^trimmer_rudder\\d*$", "Rudder trim control", "knob"),
            Tuple.Create("^mixture(_?\\d+)?$", "Mixture lever", "lever"),
            Tuple.Create("^prop_pitch(_?\\d+)?$", "Propeller pitch lever", "lever"),
            Tuple.Create("^supercharger(_?\\d+)?$", "Supercharger control", "lever"),
            Tuple.Create("^radiator_lever\\d*(_\\d+)?$", "Radiator lever", "lever"),
            Tuple.Create("^oil_radiator_lever\\d*(_\\d+)?$", "Oil radiator lever", "lever"),
            Tuple.Create("^speed\\d*$", "Airspeed indicator", "gauge"),
            Tuple.Create("^altitude(\\d)?_(hour|min|10k)$", "Altimeter", "gauge"),
            Tuple.Create("^vario\\d*$", "Vertical speed indicator", "gauge"),
            Tuple.Create("^compass\\d*$", "Compass", "gauge"),
            Tuple.Create("^(bank|turn)\\d*$", "Turn and bank indicator", "gauge"),
            Tuple.Create("^aviahorizon_(roll|pitch)\\d*$", "Artificial horizon", "gauge"),
            Tuple.Create("^fuel\\d*$", "Fuel gauge", "gauge"),
            Tuple.Create("^fuel_pressure\\d*$", "Fuel pressure gauge", "gauge"),
            Tuple.Create("^oil_pressure\\d*$", "Oil pressure gauge", "gauge"),
            Tuple.Create("^oil_temperature\\d*$", "Oil temperature gauge", "gauge"),
            Tuple.Create("^water_temperature\\d*$", "Engine temperature gauge", "gauge"),
            Tuple.Create("^head_temperature\\d*$", "Cylinder head temperature gauge", "gauge"),
            Tuple.Create("^carb_temperature\\d*$", "Air temperature gauge", "gauge"),
            Tuple.Create("^rpm(_hour|_min)?\\d*$", "Tachometer", "gauge"),
            Tuple.Create("^manifold_pressure\\d*$", "Manifold pressure gauge", "gauge"),
            Tuple.Create("^clock_(hour|min|sec)\\d*$", "Clock", "gauge"),
            Tuple.Create("^g_meter$", "Accelerometer (g)", "gauge"),
            Tuple.Create("^mach\\d*$", "Mach meter", "gauge"),
            Tuple.Create("^aoa\\d*$", "Angle of attack indicator", "gauge"),
            Tuple.Create("^radio_altitude\\d*$", "Radar altimeter", "gauge"),
            Tuple.Create("^flaps_indicator\\d*$", "Flaps position indicator", "gauge"),
            Tuple.Create("^trimmer_indicator\\d*$", "Trim indicator", "gauge"),
            Tuple.Create("^airbrake_indicator\\d*$", "Air brake indicator", "gauge"),
            Tuple.Create("^gear_lamp_(up|down|off)$", "Landing gear lights", "lamp"),
            Tuple.Create("^ammo_counter\\d*$", "Ammunition counter", "gauge"),
        };

        public static CalloutText Label(string node)
        {
            foreach (Tuple<string, string, string> t in NodeLabels)
            {
                // Instruments carry the node's name: the model reuses the channel names across aircraft types (a jet's
                // engine temperature dial is driven by "water_temperature").
                if (Regex.IsMatch(node ?? string.Empty, t.Item1, RegexOptions.IgnoreCase))
                    return new CalloutText { Label = t.Item3 == "gauge" ? t.Item2 + " (" + node + ")" : t.Item2, Kind = t.Item3 };
            }
            return null;
        }
    }

    // The War Thunder Oodle helper (tools\wt-oodle\JoyPro.WtOodle.exe, GPL-3, started as a separate process): "JPWO",
    // then requests on its stdin (packed length, raw length, stop-after, packed bytes), answers on its stdout (status,
    // length, bytes). A helper that stops (corrupt data) is started again for the next request.
    internal sealed class WarThunderOodle : IDisposable
    {
        private readonly string helper;
        private Process process;
        private BinaryWriter input;
        private BinaryReader output;
        private readonly StringBuilder errors = new StringBuilder();

        public WarThunderOodle(string helper)
        {
            this.helper = helper;
            Start();
        }

        private void Start()
        {
            ProcessStartInfo psi = new ProcessStartInfo(helper, "serve")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            lock (errors)
                errors.Clear();
            process = Process.Start(psi);
            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    lock (errors)
                    {
                        if (errors.Length < 4000)
                            errors.AppendLine(e.Data);
                    }
                }
            };
            process.BeginErrorReadLine();
            input = new BinaryWriter(process.StandardInput.BaseStream);
            output = new BinaryReader(process.StandardOutput.BaseStream);
            // A start mark: the helper skips what comes before it (the input encoding's preamble on some systems).
            input.Write(new[] { (byte)'J', (byte)'P', (byte)'W', (byte)'O' });
            input.Flush();
        }

        public byte[] Decode(byte[] data, int offset, int length, int rawLength, int stopAfter)
        {
            lock (this)
            {
                if (process == null || process.HasExited)
                {
                    Stop();
                    Start();
                }
                try
                {
                    input.Write(length);
                    input.Write(rawLength);
                    input.Write(stopAfter);
                    input.Write(data, offset, length);
                    input.Flush();
                    int status = output.ReadInt32();
                    int n = output.ReadInt32();
                    byte[] answer = output.ReadBytes(n);
                    if (answer.Length != n)
                        throw new EndOfStreamException();
                    if (status != 0)
                        throw new InvalidDataException("Oodle: " + Encoding.UTF8.GetString(answer));
                    return answer;
                }
                catch (IOException ex)
                {
                    try
                    {
                        process.WaitForExit(2000);
                    }
                    catch (Exception)
                    {
                    }
                    string err;
                    lock (errors)
                        err = errors.ToString().Trim();
                    Stop();
                    throw new InvalidDataException("the Oodle helper stopped" + (err.Length > 0 ? ": " + err : " (" + ex.Message + ")"));
                }
            }
        }

        private void Stop()
        {
            if (process == null)
                return;
            try
            {
                if (!process.HasExited)
                {
                    input.Write(-1);
                    input.Flush();
                    process.StandardInput.Close();
                    if (!process.WaitForExit(5000))
                        process.Kill();
                }
            }
            catch (Exception)
            {
                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                }
            }
            process.Dispose();
            process = null;
        }

        public void Dispose()
        {
            lock (this)
                Stop();
        }
    }
}
