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
    // Nuclear Option: the cockpit pictures of every aircraft of the game, made from its Unity data (resources.assets, open
    // and unencrypted): the aircraft prefabs' cockpit objects (the interior, the stick, the throttle, the screens) where
    // they sit in the prefab, and the pilot's helmet camera as the eye. The cockpits have no separate switches (they are
    // painted on), so the callouts name only the grips and the screens; the grips' pages carry the game's own flight
    // control actions (Rewired's action list of the game, as the game names them).
    internal static class NuclearOptionCockpitPictures
    {
        public sealed class Job
        {
            public string GameRoot = string.Empty;
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

        // One of the game's input actions (Rewired): its name, the name the game shows, and the category.
        public sealed class GameAction
        {
            public int Id;
            public string Name = string.Empty;
            public string Title = string.Empty;
            public bool Axis;
            public string Positive = string.Empty;
            public string Negative = string.Empty;
            public int CategoryId;
            public string Category = string.Empty;
        }

        public static string DataFolder(string root)
        {
            if (string.IsNullOrEmpty(root))
                return string.Empty;
            if (File.Exists(Path.Combine(root, "resources.assets")))
                return root;
            string d = Path.Combine(root, "NuclearOption_Data");
            return Directory.Exists(d) ? d : string.Empty;
        }

        // The game's Steam build (the game keeps no version file of its own).
        public static string GameVersion(string root)
        {
            try
            {
                string common = Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd('\\', '/'));
                string apps = common == null ? null : Path.GetDirectoryName(common);
                string manifest = apps == null ? null : Path.Combine(apps, "appmanifest_2168680.acf");
                if (manifest != null && File.Exists(manifest))
                {
                    Match m = Regex.Match(File.ReadAllText(manifest), "\"buildid\"\\s+\"(\\d+)\"");
                    if (m.Success)
                        return "Steam build " + m.Groups[1].Value;
                }
            }
            catch (Exception)
            {
            }
            return string.Empty;
        }

        // ------------------------------------------------------------------ the prefab hierarchy

        internal sealed class Node
        {
            public long Transform;
            public long GameObject;
            public string Name = string.Empty;
            public long Parent;
            public List<long> Children = new List<long>();
            public double[] Local = Mat4.Identity();
            public bool Active = true;
            public long Mesh;
            public List<long> Materials = new List<long>();
            public bool Rendered;
        }

        internal sealed class Model
        {
            public Dictionary<long, Node> Nodes = new Dictionary<long, Node>();

            public static Model Load(UnityAssetFile f)
            {
                Model m = new Model();
                Dictionary<long, string> names = new Dictionary<long, string>();
                Dictionary<long, bool> active = new Dictionary<long, bool>();
                foreach (UnityObjectInfo o in f.OfClass(1))
                {
                    Dictionary<string, object> g = f.Read(o);
                    if (g == null)
                        continue;
                    names[o.PathId] = UnityAssetFile.Str(g, "m_Name");
                    object a = UnityAssetFile.Field(g, "m_IsActive");
                    active[o.PathId] = !(a is bool) || (bool)a;
                }
                Dictionary<long, long> meshOf = new Dictionary<long, long>();
                foreach (UnityObjectInfo o in f.OfClass(33))
                {
                    Dictionary<string, object> mf = f.Read(o);
                    if (mf != null)
                        meshOf[UnityAssetFile.PathIdOf(UnityAssetFile.Field(mf, "m_GameObject"))] = UnityAssetFile.PathIdOf(UnityAssetFile.Field(mf, "m_Mesh"));
                }
                Dictionary<long, Dictionary<string, object>> renderers = new Dictionary<long, Dictionary<string, object>>();
                foreach (UnityObjectInfo o in f.OfClass(23))
                {
                    Dictionary<string, object> mr = f.Read(o);
                    if (mr != null)
                        renderers[UnityAssetFile.PathIdOf(UnityAssetFile.Field(mr, "m_GameObject"))] = mr;
                }
                foreach (UnityObjectInfo o in f.OfClass(4))
                {
                    Dictionary<string, object> t = f.Read(o);
                    if (t == null)
                        continue;
                    Node n = new Node { Transform = o.PathId, GameObject = UnityAssetFile.PathIdOf(UnityAssetFile.Field(t, "m_GameObject")), Parent = UnityAssetFile.PathIdOf(UnityAssetFile.Field(t, "m_Father")) };
                    string name;
                    n.Name = names.TryGetValue(n.GameObject, out name) ? name : string.Empty;
                    bool act;
                    n.Active = !active.TryGetValue(n.GameObject, out act) || act;
                    foreach (object c in UnityAssetFile.List(t, "m_Children"))
                        n.Children.Add(UnityAssetFile.PathIdOf(c));
                    double[] q = UnityAssetFile.Vector(t, "m_LocalRotation", "x", "y", "z", "w");
                    double[] p = UnityAssetFile.Vector(t, "m_LocalPosition", "x", "y", "z");
                    double[] s = UnityAssetFile.Vector(t, "m_LocalScale", "x", "y", "z");
                    n.Local = LocalMatrix(p, q, s);
                    long mesh;
                    if (meshOf.TryGetValue(n.GameObject, out mesh))
                        n.Mesh = mesh;
                    Dictionary<string, object> mr;
                    if (renderers.TryGetValue(n.GameObject, out mr))
                    {
                        object en = UnityAssetFile.Field(mr, "m_Enabled");
                        n.Rendered = !(en is bool) || (bool)en;
                        foreach (object mat in UnityAssetFile.List(mr, "m_Materials"))
                            n.Materials.Add(UnityAssetFile.PathIdOf(mat));
                    }
                    m.Nodes[o.PathId] = n;
                }
                return m;
            }

            public Node Root(Node n)
            {
                int guard = 0;
                Node p;
                while (n.Parent != 0 && Nodes.TryGetValue(n.Parent, out p) && guard++ < 200)
                    n = p;
                return n;
            }

            public double[] World(Node n)
            {
                double[] m = n.Local;
                int guard = 0;
                Node p;
                Node at = n;
                while (at.Parent != 0 && Nodes.TryGetValue(at.Parent, out p) && guard++ < 200)
                {
                    m = Mat4.Multiply(p.Local, m);
                    at = p;
                }
                return m;
            }

            public IEnumerable<Node> Subtree(Node n)
            {
                Stack<Node> todo = new Stack<Node>();
                todo.Push(n);
                HashSet<long> seen = new HashSet<long>();
                while (todo.Count > 0)
                {
                    Node x = todo.Pop();
                    if (!seen.Add(x.Transform))
                        continue;
                    yield return x;
                    for (int i = x.Children.Count - 1; i >= 0; i--)
                    {
                        Node c;
                        if (Nodes.TryGetValue(x.Children[i], out c))
                            todo.Push(c);
                    }
                }
            }
        }

        // Unity's local matrix (translation * rotation * scale), column-major; quaternion x, y, z, w.
        public static double[] LocalMatrix(double[] p, double[] q, double[] s)
        {
            double[] r = Mat4.Rotation(new[] { q[3], q[0], q[1], q[2] });
            return Mat4.Multiply(Mat4.Translation(p[0], p[1], p[2]), Mat4.Multiply(r, Mat4.Scale(s[0], s[1], s[2])));
        }

        // Unity's frame (x right, y up, z forward; left-handed) to the scene's (x forward, y up, z right).
        public static Vec3 ToScene(Vec3 u)
        {
            return new Vec3(u.Z, u.Y, u.X);
        }

        // ------------------------------------------------------------------ aircraft, names and actions

        private static readonly Regex GripName = new Regex("^(joystick|stick|throttle|collective)(_?[LRFB])?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ScreenName = new Regex("tacscreen|targetscreen", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex InteriorName = new Regex("(^|_)(int|interior|internal)(_|$)|cockpitint|joystick|throttle|collective|tacscreen|targetscreen|seat", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SkipName = new Regex("simple|glass|colmesh|lod|warning|light|shadow|exterior|pilot_|helmet|visor|mirror_?cam", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SkipMaterial = new Regex("glass|canopy_int|^lod$|warninglight|navlight|shadow|_lit$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The aircraft prefabs: the roots of the hierarchies with a cockpit stick ("<type>_joystick" mesh).
        public static List<Node> Aircraft(UnityAssetFile f, Model model, Dictionary<long, string> meshNames)
        {
            Dictionary<long, Node> roots = new Dictionary<long, Node>();
            foreach (Node n in model.Nodes.Values)
            {
                string mesh;
                bool stick = n.Mesh != 0 && meshNames.TryGetValue(n.Mesh, out mesh) && Regex.IsMatch(mesh, "_joystick$", RegexOptions.IgnoreCase);
                if (!stick)
                    continue;
                Node r = model.Root(n);
                roots[r.Transform] = r;
            }
            // One prefab per name (the one with the most objects).
            return roots.Values.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(r => model.Subtree(r).Count()).First())
                .Where(r => r.Name.Length > 0)
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The names the game shows for its aircraft (unit name, short code) by prefab name: the aircraft definitions
        // (a MonoBehaviour named like the prefab whose fields hold the prefab's name, the unit name, the description and
        // the code), else the game's exported table of unit parts ("UnitName;Part;...").
        public static Dictionary<string, List<string>> DisplayNames(UnityAssetFile f, IEnumerable<string> prefabs)
        {
            HashSet<string> wanted = new HashSet<string>(prefabs, StringComparer.Ordinal);
            Dictionary<string, List<string>> map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (UnityObjectInfo o in f.OfClass(114))
            {
                if (map.Count == wanted.Count)
                    break;
                string name = f.NameOf(o);
                if (!wanted.Contains(name) || map.ContainsKey(name))
                    continue;
                Dictionary<string, object> mb = f.Read(o);
                byte[] rest = UnityAssetFile.Bytes(mb, "_rest");
                List<string> found = DefinitionNames(rest, name);
                if (found.Count > 0)
                    map[name] = found;
            }
            if (map.Count < wanted.Count)
            {
                foreach (UnityObjectInfo o in f.OfClass(49))
                {
                    if (f.NameOf(o) != "UnitParts")
                        continue;
                    Dictionary<string, object> ta = f.Read(o);
                    string text = Encoding.UTF8.GetString(UnityAssetFile.Bytes(ta, "m_Script"));
                    foreach (string line in text.Split('\n'))
                    {
                        string[] p = line.Split(';').Select(x => x.Trim()).ToArray();
                        if (p.Length > 1 && wanted.Contains(p[1]) && !map.ContainsKey(p[1]) && p[0].Any(char.IsLetterOrDigit))
                            map[p[1]] = new List<string> { p[0] };
                    }
                }
            }
            return map;
        }

        // In an aircraft definition's fields: the prefab's name as a string, then the unit name, the description and
        // the short code ("CAS1", "A-19 Brawler", "The Brawler is ...", "A-19").
        public static List<string> DefinitionNames(byte[] d, string prefab)
        {
            List<string> names = new List<string>();
            byte[] key = Encoding.UTF8.GetBytes(prefab);
            for (int i = 0; i + 4 + key.Length <= d.Length; i += 4)
            {
                if (BitConverter.ToInt32(d, i) != key.Length || !Same(d, i + 4, key))
                    continue;
                int at = Align(i + 4 + key.Length);
                string unit = AlignedString(d, ref at, 64, false);
                if (unit == null || unit.Length < 2 || unit == prefab || !unit.Any(char.IsLetterOrDigit))
                    continue;
                names.Add(unit);
                // An empty field may come before the description.
                string description = AlignedString(d, ref at, 8000, true);
                for (int k = 0; k < 2 && description != null && description.Length == 0; k++)
                    description = AlignedString(d, ref at, 8000, true);
                if (description != null)
                {
                    string code = AlignedString(d, ref at, 16, false);
                    if (code != null && code.Length >= 2 && unit.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0)
                        names.Add(code);
                }
                break;
            }
            return names;
        }

        private static bool Same(byte[] d, int at, byte[] key)
        {
            for (int k = 0; k < key.Length; k++)
            {
                if (d[at + k] != key[k])
                    return false;
            }
            return true;
        }

        private static int Align(int p)
        {
            return (p + 3) & ~3;
        }

        // A length-prefixed, aligned string at "at" (printable text only), or null.
        private static string AlignedString(byte[] d, ref int at, int max, bool anyText)
        {
            if (at + 4 > d.Length)
                return null;
            int n = BitConverter.ToInt32(d, at);
            if (n < 0 || n > max || at + 4 + n > d.Length)
                return null;
            for (int k = 0; k < n; k++)
            {
                byte b = d[at + 4 + k];
                if (b < 32 && !(anyText && (b == 10 || b == 13 || b == 9)))
                    return null;
                if (!anyText && b >= 127)
                    return null;
            }
            string s = Encoding.UTF8.GetString(d, at + 4, n);
            at = Align(at + 4 + n);
            return s;
        }

        // The game's input actions: the Rewired input manager's action list (each action: id, name, type, the shown
        // name, the positive and negative names, ..., behaviour, user assignable, category) followed by the categories.
        public static List<GameAction> Actions(UnityAssetFile f, Model model)
        {
            foreach (Node n in model.Nodes.Values.Where(x => x.Name.IndexOf("Rewired Input Manager", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Dictionary<string, object> go = f.Read(n.GameObject);
                foreach (object c in UnityAssetFile.List(go, "m_Component"))
                {
                    long id = UnityAssetFile.PathIdOf(UnityAssetFile.Field(c as Dictionary<string, object>, "component"));
                    UnityObjectInfo o;
                    if (id == 0 || !f.Objects.TryGetValue(id, out o) || o.ClassId != 114)
                        continue;
                    byte[] raw = f.RawData(o);
                    List<GameAction> list = ParseActions(raw);
                    if (list.Count > 0)
                        return list;
                }
            }
            return new List<GameAction>();
        }

        public static List<GameAction> ParseActions(byte[] d)
        {
            for (int p = 0; p + 8 <= d.Length; p += 4)
            {
                int count = BitConverter.ToInt32(d, p);
                if (count < 3 || count > 2000)
                    continue;
                int at = p + 4;
                List<GameAction> actions = new List<GameAction>();
                bool ok = true;
                for (int i = 0; i < count && ok; i++)
                {
                    GameAction a = ReadAction(d, ref at);
                    if (a == null)
                        ok = false;
                    else
                        actions.Add(a);
                }
                if (!ok)
                    continue;
                // The categories: count, then name, shown name, two strings, id, user assignable.
                if (at + 4 > d.Length)
                    continue;
                int cats = BitConverter.ToInt32(d, at);
                if (cats < 1 || cats > 200)
                    continue;
                at += 4;
                Dictionary<int, string> names = new Dictionary<int, string>();
                for (int i = 0; i < cats && ok; i++)
                {
                    string name = AlignedString(d, ref at, 200, false), title = AlignedString(d, ref at, 200, false);
                    string s1 = AlignedString(d, ref at, 200, false), s2 = AlignedString(d, ref at, 200, false);
                    if (name == null || title == null || s1 == null || s2 == null || at + 8 > d.Length)
                    {
                        ok = false;
                        break;
                    }
                    int id = BitConverter.ToInt32(d, at);
                    at += 8;
                    names[id] = title.Length > 0 ? title : name;
                }
                if (!ok || actions.Any(a => !names.ContainsKey(a.CategoryId)))
                    continue;
                foreach (GameAction a in actions)
                    a.Category = names[a.CategoryId];
                return actions;
            }
            return new List<GameAction>();
        }

        private static GameAction ReadAction(byte[] d, ref int at)
        {
            if (at + 8 > d.Length)
                return null;
            GameAction a = new GameAction { Id = BitConverter.ToInt32(d, at) };
            if (a.Id < 0 || a.Id > 1000000)
                return null;
            at += 4;
            a.Name = AlignedString(d, ref at, 200, false);
            if (string.IsNullOrEmpty(a.Name) || at + 4 > d.Length)
                return null;
            int type = BitConverter.ToInt32(d, at);
            if (type != 0 && type != 1)
                return null;
            a.Axis = type == 0;
            at += 4;
            a.Title = AlignedString(d, ref at, 200, false);
            a.Positive = AlignedString(d, ref at, 200, false);
            a.Negative = AlignedString(d, ref at, 200, false);
            for (int k = 0; k < 3; k++)
            {
                if (AlignedString(d, ref at, 200, false) == null)
                    return null;
            }
            if (a.Title == null || a.Positive == null || a.Negative == null || at + 12 > d.Length)
                return null;
            int assignable = BitConverter.ToInt32(d, at + 4);
            a.CategoryId = BitConverter.ToInt32(d, at + 8);
            at += 12;
            if ((assignable != 0 && assignable != 1) || a.CategoryId < 0 || a.CategoryId > 100000)
                return null;
            return a;
        }

        // ------------------------------------------------------------------ building

        public static List<SetResult> Build(Job job)
        {
            List<SetResult> results = new List<SetResult>();
            string data = DataFolder(job.GameRoot);
            string file = data.Length == 0 ? string.Empty : Path.Combine(data, "resources.assets");
            if (!File.Exists(file))
            {
                results.Add(new SetResult { Plane = "Nuclear Option", Error = "resources.assets was not found in the game folder" });
                return results;
            }
            string version = GameVersion(job.GameRoot);
            using (UnityAssetFile f = UnityAssetFile.Open(file))
            {
                if (job.Progress != null)
                    job.Progress(0, 1, "Nuclear Option, reading the game data", Path.GetFileName(file));
                Model model = Model.Load(f);
                Dictionary<long, string> meshNames = new Dictionary<long, string>();
                foreach (UnityObjectInfo o in f.OfClass(43))
                    meshNames[o.PathId] = f.NameOf(o);
                List<Node> aircraft = Aircraft(f, model, meshNames);
                Dictionary<string, List<string>> display = DisplayNames(f, aircraft.Select(a => a.Name));
                List<GameAction> actions = Actions(f, model);
                List<GameAction> flight = actions.Where(a => a.Category.IndexOf("Flight", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                Cache cache = new Cache { File = f, Model = model, MeshNames = meshNames };
                int i = 0;
                foreach (Node root in aircraft)
                {
                    i++;
                    if (job.Cancelled != null && job.Cancelled())
                        break;
                    List<string> names = new List<string>();
                    List<string> shown;
                    if (display.TryGetValue(root.Name, out shown))
                        names.AddRange(shown);
                    names.Add(root.Name);
                    names = names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (job.Only.Count > 0 && !job.Only.Any(o => names.Any(n => n.IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0)))
                        continue;
                    string key = Regex.Replace(root.Name, "[^A-Za-z0-9_\\-]", "_");
                    string folder = Path.Combine(job.OutRoot, key);
                    SetResult r = new SetResult { Plane = names[0], Planes = names, Folder = folder };
                    results.Add(r);
                    string manifest = Path.Combine(folder, PictureWriter.ManifestName);
                    if (!job.Force && job.ViewFilter.Length == 0 && File.Exists(manifest) && File.GetLastWriteTimeUtc(manifest) > File.GetLastWriteTimeUtc(file) &&
                        File.ReadAllText(manifest).Contains("\"generator\":" + PictureWriter.GeneratorVersion.ToString(CultureInfo.InvariantCulture)))
                    {
                        r.Skipped = true;
                        continue;
                    }
                    if (job.Progress != null)
                        job.Progress(i, aircraft.Count, "Nuclear Option, cockpit pictures", names[0]);
                    try
                    {
                        BuildOne(job, cache, root, flight, folder, version, r);
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

        internal sealed class Cache
        {
            public UnityAssetFile File;
            public Model Model;
            public Dictionary<long, string> MeshNames;
            public Dictionary<long, UnityMesh> Meshes = new Dictionary<long, UnityMesh>();
            public Dictionary<long, Dictionary<string, object>> Materials = new Dictionary<long, Dictionary<string, object>>();
            public Dictionary<long, TextureImage> Textures = new Dictionary<long, TextureImage>();

            public UnityMesh Mesh(long id)
            {
                UnityMesh m;
                if (!Meshes.TryGetValue(id, out m))
                {
                    Dictionary<string, object> d = File.Read(id);
                    m = d == null ? null : UnityMesh.From(d, File, File.UnityVersion);
                    Meshes[id] = m;
                }
                return m;
            }

            public Dictionary<string, object> Material(long id)
            {
                Dictionary<string, object> m;
                if (!Materials.TryGetValue(id, out m))
                {
                    m = id == 0 ? null : File.Read(id);
                    Materials[id] = m;
                }
                return m;
            }

            public TextureImage Texture(long id)
            {
                TextureImage t;
                if (!Textures.TryGetValue(id, out t))
                {
                    UnityObjectInfo o;
                    t = null;
                    if (id != 0 && File.Objects.TryGetValue(id, out o) && o.ClassId == 28)
                    {
                        Dictionary<string, object> d = File.Read(o);
                        try
                        {
                            t = d == null ? null : UnityTextures.Decode(d, File, 2048);
                        }
                        catch (Exception)
                        {
                            t = null;
                        }
                    }
                    Textures[id] = t;
                }
                return t;
            }
        }

        // The albedo texture of a material (_BaseMap of the Universal pipeline, else _MainTex) and its colour.
        private static long AlbedoOf(Dictionary<string, object> mat, out byte[] color, out bool alphaClip)
        {
            color = null;
            alphaClip = false;
            Dictionary<string, object> props = UnityAssetFile.Obj(mat, "m_SavedProperties");
            long tex = 0;
            foreach (string want in new[] { "_BaseMap", "_MainTex", "_BaseColorMap", "_Albedo" })
            {
                foreach (Dictionary<string, object> pair in UnityAssetFile.List(props, "m_TexEnvs").OfType<Dictionary<string, object>>())
                {
                    if (UnityAssetFile.Str(pair, "first") != want)
                        continue;
                    long id = UnityAssetFile.PathIdOf(UnityAssetFile.Field(UnityAssetFile.Obj(pair, "second"), "m_Texture"));
                    if (id != 0)
                    {
                        tex = id;
                        break;
                    }
                }
                if (tex != 0)
                    break;
            }
            foreach (string want in new[] { "_BaseColor", "_Color" })
            {
                Dictionary<string, object> c = UnityAssetFile.List(props, "m_Colors").OfType<Dictionary<string, object>>().FirstOrDefault(p => UnityAssetFile.Str(p, "first") == want);
                if (c == null)
                    continue;
                Dictionary<string, object> v = UnityAssetFile.Obj(c, "second");
                color = new[] { Byte(UnityAssetFile.Num(v, "r", 0.6)), Byte(UnityAssetFile.Num(v, "g", 0.6)), Byte(UnityAssetFile.Num(v, "b", 0.6)) };
                break;
            }
            Dictionary<string, object> clip = UnityAssetFile.List(props, "m_Floats").OfType<Dictionary<string, object>>().FirstOrDefault(p => UnityAssetFile.Str(p, "first") == "_AlphaClip");
            alphaClip = clip != null && UnityAssetFile.Num(clip, "second") > 0.5;
            return tex;
        }

        private static byte Byte(double v)
        {
            // Linear colour values to sRGB-ish bytes.
            double s = Math.Pow(Math.Max(0, Math.Min(1, v)), 1 / 2.2);
            return (byte)Math.Round(s * 255);
        }

        private static void BuildOne(Job job, Cache cache, Node root, List<GameAction> flight, string folder, string version, SetResult r)
        {
            Model model = cache.Model;
            List<Node> all = model.Subtree(root).ToList();
            Dictionary<long, double[]> world = new Dictionary<long, double[]>();
            foreach (Node n in all)
                world[n.Transform] = model.World(n);
            Func<Node, Vec3> at = n => { double[] m = world[n.Transform]; return ToScene(new Vec3(m[12], m[13], m[14])); };
            // Grips (the stick, the throttle or collective; two of a kind in a cockpit for two).
            List<Node> gripNodes = all.Where(n => GripName.IsMatch(n.Name) && model.Subtree(n).Any(x => x.Mesh != 0)).ToList();
            List<Node> sticks = gripNodes.Where(n => Regex.IsMatch(n.Name, "^(joy)?stick", RegexOptions.IgnoreCase)).ToList();
            // The pilot's eye: the helmet camera of the pilot nearest to a stick (the second seat's camera is
            // "helmetCamPoint2"), else above the seat, else behind and above the stick.
            Vec3 head;
            List<Node> cams = all.Where(n => n.Name.StartsWith("helmetCamPoint", StringComparison.OrdinalIgnoreCase)).ToList();
            if (cams.Count > 0)
            {
                Node best = cams.OrderBy(c => (sticks.Count == 0 ? 0 : sticks.Min(s => (at(s) - at(c)).Length)) + (c.Name.EndsWith("2", StringComparison.Ordinal) ? 0.5 : 0)).First();
                head = at(best);
            }
            else
            {
                Node seat = all.FirstOrDefault(n => n.Name.IndexOf("seat", StringComparison.OrdinalIgnoreCase) >= 0);
                if (seat != null)
                {
                    head = at(seat) + new Vec3(0, 0.85, 0);
                }
                else if (sticks.Count > 0)
                {
                    head = at(sticks[0]) + new Vec3(-0.55, 0.6, 0);
                    r.Notes.Add("no pilot camera found; the eye is placed behind the stick");
                }
                else
                {
                    r.Error = "no pilot camera, seat or stick found";
                    return;
                }
            }
            // The cockpit's objects near the eye.
            CockpitScene scene = new CockpitScene();
            Dictionary<long, int> materialIndex = new Dictionary<long, int>();
            Dictionary<long, List<int>> meshesOfNode = new Dictionary<long, List<int>>();
            int missing = 0, part = 0, compressed = 0;
            foreach (Node n in all)
            {
                string meshName;
                cache.MeshNames.TryGetValue(n.Mesh, out meshName);
                meshName = meshName ?? string.Empty;
                // The cockpit's renderers are switched on by the game in the cockpit view only: switched-off ones count.
                if (n.Mesh == 0 || n.Materials.Count == 0)
                    continue;
                if (!(InteriorName.IsMatch(n.Name) || InteriorName.IsMatch(meshName)) || SkipName.IsMatch(n.Name) || SkipName.IsMatch(meshName))
                    continue;
                if (!AncestorsActive(model, n))
                    continue;
                if ((at(n) - head).Length > 4.5)
                    continue;
                UnityMesh um = cache.Mesh(n.Mesh);
                if (um == null || um.Positions == null)
                {
                    if (um != null && um.Problem.StartsWith("compressed", StringComparison.Ordinal))
                        compressed++;
                    continue;
                }
                double[] m = world[n.Transform];
                double[] nm = Mat4.NormalMatrix(m);
                float[] pos = new float[um.Positions.Length];
                float[] nor = um.Normals == null ? null : new float[um.Normals.Length];
                for (int v = 0; v < um.VertexCount; v++)
                {
                    Vec3 w = ToScene(Mat4.Point(m, um.Positions[3 * v], um.Positions[3 * v + 1], um.Positions[3 * v + 2]));
                    pos[3 * v] = (float)w.X;
                    pos[3 * v + 1] = (float)w.Y;
                    pos[3 * v + 2] = (float)w.Z;
                    if (nor != null)
                    {
                        Vec3 nn = ToScene(Mat4.Direction(nm, um.Normals[3 * v], um.Normals[3 * v + 1], um.Normals[3 * v + 2])).Normalized;
                        nor[3 * v] = (float)nn.X;
                        nor[3 * v + 1] = (float)nn.Y;
                        nor[3 * v + 2] = (float)nn.Z;
                    }
                }
                part++;
                for (int s = 0; s < um.SubMeshes.Count; s++)
                {
                    long matId = n.Materials[Math.Min(s, n.Materials.Count - 1)];
                    Dictionary<string, object> mat = cache.Material(matId);
                    string matName = UnityAssetFile.Str(mat, "m_Name");
                    if (mat == null || SkipMaterial.IsMatch(matName) || um.SubMeshes[s].Length < 3)
                        continue;
                    int mi;
                    if (!materialIndex.TryGetValue(matId, out mi))
                    {
                        byte[] color;
                        bool clip;
                        long tex = AlbedoOf(mat, out color, out clip);
                        TextureImage img = tex == 0 ? null : cache.Texture(tex);
                        if (tex != 0 && img == null)
                            missing++;
                        SceneMaterial sm = new SceneMaterial { Name = matName, Albedo = img, AlphaTest = clip && img != null };
                        if (img != null)
                            sm.Color = new[] { img.Mean[0], img.Mean[1], img.Mean[2] };
                        else if (color != null)
                            sm.Color = color;
                        mi = scene.Materials.Count;
                        scene.Materials.Add(sm);
                        materialIndex[matId] = mi;
                    }
                    // The sub-mesh's vertices only.
                    int[] tri = um.SubMeshes[s];
                    Dictionary<int, int> remap = new Dictionary<int, int>();
                    List<float> p3 = new List<float>(), n3 = new List<float>(), uv = new List<float>();
                    int[] idx = new int[tri.Length];
                    for (int k = 0; k < tri.Length; k++)
                    {
                        int v = tri[k], mapped;
                        if (!remap.TryGetValue(v, out mapped))
                        {
                            mapped = remap.Count;
                            remap[v] = mapped;
                            p3.Add(pos[3 * v]);
                            p3.Add(pos[3 * v + 1]);
                            p3.Add(pos[3 * v + 2]);
                            if (nor != null)
                            {
                                n3.Add(nor[3 * v]);
                                n3.Add(nor[3 * v + 1]);
                                n3.Add(nor[3 * v + 2]);
                            }
                            if (um.Uvs != null)
                            {
                                uv.Add(um.Uvs[2 * v]);
                                uv.Add(um.Uvs[2 * v + 1]);
                            }
                        }
                        idx[k] = mapped;
                    }
                    int index = scene.Meshes.Count;
                    scene.Meshes.Add(new SceneMesh { Name = n.Name + "/" + meshName, Node = (int)Math.Min(int.MaxValue, n.Transform), Material = mi, Part = part, Positions = p3.ToArray(), Normals = nor == null ? null : n3.ToArray(), Uvs = um.Uvs == null ? null : uv.ToArray(), Indices = idx });
                    List<int> list;
                    if (!meshesOfNode.TryGetValue(n.Transform, out list))
                        meshesOfNode[n.Transform] = list = new List<int>();
                    list.Add(index);
                }
            }
            if (scene.Meshes.Count == 0)
            {
                r.Error = "no cockpit objects could be read" + (compressed > 0 ? " (compressed meshes)" : string.Empty);
                return;
            }
            // Grips with their meshes; the game's flight control actions as each grip's part (the game binds any of them
            // to any button; it names no buttons of its own grips).
            List<CockpitGrip> grips = new List<CockpitGrip>();
            foreach (Node g in gripNodes)
            {
                List<int> meshes = model.Subtree(g).SelectMany(x => meshesOfNode.ContainsKey(x.Transform) ? meshesOfNode[x.Transform] : new List<int>()).ToList();
                if (meshes.Count == 0)
                    continue;
                Vec3 mn, mx;
                scene.Bounds(meshes, out mn, out mx);
                if ((mx - mn).Length > 1.5 || ((mn + mx) * 0.5 - head).Length > 2.5)
                    continue;
                string device = Regex.IsMatch(g.Name, "^(joy)?stick", RegexOptions.IgnoreCase) ? "stick" : "throttle";
                CockpitGrip cg = new CockpitGrip { Device = device, Meshes = meshes, Pivot = (mn + mx) * 0.5, Animation = new ControlAnimation { Source = "animated part", Name = g.Name } };
                if (flight.Count > 0)
                {
                    CockpitCallout p = new CockpitCallout
                    {
                        Id = "grip",
                        Label = (device == "stick" ? "Stick" : "Throttle") + " grip: the game's flight controls (bind them to any button)",
                        Position = Highest(scene, meshes),
                        Kind = "part"
                    };
                    foreach (GameAction a in flight)
                        p.Commands.Add(new CalloutCommand { Name = a.Title.Length > 0 ? a.Title : a.Name, Category = a.Category, Id = a.Name });
                    cg.Parts.Add(p);
                }
                grips.Add(cg);
            }
            // The screens.
            List<CockpitCallout> screens = new List<CockpitCallout>();
            foreach (Node sNode in all.Where(n => ScreenName.IsMatch(n.Name) && meshesOfNode.ContainsKey(n.Transform)))
            {
                Vec3 mn, mx;
                scene.Bounds(meshesOfNode[sNode.Transform], out mn, out mx);
                Vec3 c = OnSurface(scene, meshesOfNode[sNode.Transform], (mn + mx) * 0.5);
                if (screens.Any(x => (x.Position - c).Length < 0.05))
                    continue;
                screens.Add(new CockpitCallout
                {
                    Id = "screen:" + sNode.Name,
                    Label = sNode.Name.IndexOf("target", StringComparison.OrdinalIgnoreCase) >= 0 ? "Targeting screen" : "Tactical screen",
                    Position = c,
                    Kind = "display",
                    Panel = "screens",
                    PanelTitle = "Screens"
                });
            }
            List<CockpitViewPlan> views = CockpitViews.Plan(scene, head, screens.Select(Copy).ToList(), grips, job.Width);
            // The screens are named on the overview only (no switches around them to show on a page of their own).
            views.RemoveAll(v => v.Kind == "panel");
            CockpitViewPlan overview = views.FirstOrDefault(v => v.Kind == "overview");
            if (overview != null)
            {
                List<CockpitCallout> calls = new List<CockpitCallout>();
                List<string> links = new List<string>();
                for (int k = 0; k < overview.Callouts.Count; k++)
                {
                    string link = k < overview.Links.Count ? overview.Links[k] : string.Empty;
                    if (link.StartsWith("panel:", StringComparison.Ordinal))
                        continue;
                    calls.Add(overview.Callouts[k]);
                    links.Add(link);
                }
                foreach (CockpitCallout s in screens)
                {
                    calls.Add(new CockpitCallout { Id = s.Id, Label = s.Label, Position = s.Position, Kind = s.Kind });
                    links.Add(string.Empty);
                }
                for (int k = 0; k < calls.Count; k++)
                    calls[k].N = k + 1;
                overview.Callouts = calls;
                overview.Links = links;
                // Framed to the screens' whole extent and the grips (the plan frames points: the screens' centres).
                List<Vec3> frame = grips.Select(g => g.Pivot).ToList();
                foreach (Node sNode in all.Where(n => ScreenName.IsMatch(n.Name) && meshesOfNode.ContainsKey(n.Transform)))
                {
                    Vec3 mn, mx;
                    scene.Bounds(meshesOfNode[sNode.Transform], out mn, out mx);
                    frame.Add(mn);
                    frame.Add(mx);
                    frame.Add(new Vec3(mn.X, mx.Y, mn.Z));
                    frame.Add(new Vec3(mx.X, mn.Y, mx.Z));
                }
                Refit(overview.Camera, frame, head);
            }
            if (job.ViewFilter.Length > 0)
                views = views.Where(v => v.Id.IndexOf(job.ViewFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            r.Notes.Add("the cockpit has no separate switches (they are painted on): the callouts name the grips and the screens");
            if (flight.Count == 0)
                r.Notes.Add("the game's action list was not found");
            if (missing > 0)
                r.Notes.Add(missing.ToString(CultureInfo.InvariantCulture) + " textures could not be read (drawn in a flat colour)");
            if (compressed > 0)
                r.Notes.Add(compressed.ToString(CultureInfo.InvariantCulture) + " compressed meshes left out");
            PictureWriter.SetInfo info = new PictureWriter.SetInfo
            {
                Game = "NuclearOption",
                Plane = r.Plane,
                Planes = r.Planes,
                Module = root.Name,
                Source = "resources.assets",
                GameVersion = version,
                Notes = r.Notes
            };
            PictureWriter.Write(scene, views, folder, info, (cur, total, title) =>
            {
                if (job.Progress != null)
                    job.Progress(cur, total, "Nuclear Option, " + r.Plane + " pictures", title);
            }, job.ViewFilter.Length == 0);
            r.Views = views.Count;
            r.Callouts = (overview == null ? 0 : overview.Callouts.Count) + grips.Sum(g => g.Parts.Count);
        }

        // The highest point of a grip's meshes (the top of the stick, the throttle's handle).
        private static Vec3 Highest(CockpitScene scene, List<int> meshes)
        {
            Vec3 best = new Vec3(0, double.MinValue, 0);
            foreach (int m in meshes)
            {
                float[] p = scene.Meshes[m].Positions;
                for (int i = 0; i + 2 < p.Length; i += 3)
                {
                    if (p[i + 1] > best.Y)
                        best = new Vec3(p[i], p[i + 1], p[i + 2]);
                }
            }
            return best;
        }

        // The centre of the triangle nearest to a point (a screen's centre on the screen, not between two screens).
        private static Vec3 OnSurface(CockpitScene scene, List<int> meshes, Vec3 near)
        {
            Vec3 best = near;
            double bestD = double.MaxValue;
            foreach (int m in meshes)
            {
                SceneMesh sm = scene.Meshes[m];
                for (int t = 0; t + 2 < sm.Indices.Length; t += 3)
                {
                    Vec3 c = new Vec3(0, 0, 0);
                    for (int k = 0; k < 3; k++)
                    {
                        int v = sm.Indices[t + k];
                        c = c + new Vec3(sm.Positions[3 * v], sm.Positions[3 * v + 1], sm.Positions[3 * v + 2]);
                    }
                    c = c * (1.0 / 3);
                    double d = (c - near).Length;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = c;
                    }
                }
            }
            return best;
        }

        // Aims the overview camera (from where the plan put it) at the middle of the points in front of the head and
        // widens it until they all fit, with room around them.
        public static void Refit(RenderCamera cam, List<Vec3> points, Vec3 head)
        {
            List<Vec3> pts = points.Where(p => p.X >= head.X - 0.05).ToList();
            if (pts.Count < 2)
                return;
            Vec3 aim = CockpitViews.Centre(pts);
            RenderCamera fit = RenderCamera.LookAt(cam.Eye, aim, cam.FovY, cam.Width, cam.Height);
            double maxX = 0, maxY = 0;
            foreach (Vec3 p in pts)
            {
                Vec3 d = p - fit.Eye;
                double z = Vec3.Dot(d, fit.Forward);
                if (z < 0.05)
                    continue;
                maxX = Math.Max(maxX, Math.Abs(Vec3.Dot(d, fit.Right)) / z);
                maxY = Math.Max(maxY, Math.Abs(Vec3.Dot(d, fit.Up)) / z);
            }
            double halfY = Math.Max(maxY, maxX * cam.Height / (double)cam.Width) * 1.12 + 0.04;
            cam.Forward = fit.Forward;
            cam.Right = fit.Right;
            cam.Up = fit.Up;
            // Not wider than a pilot sees the panel (a wide bomber panel is shown in part rather than tiny).
            cam.FovY = Math.Max(40, Math.Min(80, 2 * Math.Atan(halfY) * 180 / Math.PI));
        }

        private static CockpitCallout Copy(CockpitCallout c)
        {
            return new CockpitCallout { Id = c.Id, Label = c.Label, Position = c.Position, Kind = c.Kind, Panel = c.Panel, PanelTitle = c.PanelTitle };
        }

        private static bool AncestorsActive(Model model, Node n)
        {
            int guard = 0;
            Node p;
            while (n.Parent != 0 && model.Nodes.TryGetValue(n.Parent, out p) && guard++ < 200)
            {
                // The model's low-detail and exterior-only branches are switched off at rest; a switched-off parent of
                // the cockpit (an animated canopy, a hidden crew) still counts.
                if (!p.Active && SkipName.IsMatch(p.Name))
                    return false;
                n = p;
            }
            return true;
        }
    }
}
