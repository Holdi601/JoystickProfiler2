#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.CockpitPictures
{
    // DCS World: the cockpit pictures of every installed module, made from the module's own cockpit model (EDM), its
    // clickable cockpit (clickabledata.lua: which connector is which switch, its hint text, device and commands), the
    // grips' animation arguments (mainpanel_init.lua) and the pilot's eye points (Views.lua). The callouts sit where the
    // game's clickable connectors are; their functions are the game's own hint texts and the bindable commands of the
    // same device and command number (JoyPro's DCS command list of that aircraft).
    internal static class DcsCockpitPictures
    {
        public sealed class Job
        {
            public string DcsRoot = string.Empty;
            public string OutRoot = string.Empty;     // <data>\GameData\CockpitPictures\DCS
            public string WorkDir = string.Empty;     // temporary files (Lua harness)
            public Func<string, string> CommandList;  // plane -> JoyPro's DCS command list (html) or empty
            public List<string> Only = new List<string>();
            public bool Force;
            public int Width = 1600;
            // Only the views whose id contains this text (development; empty = all).
            public string ViewFilter = string.Empty;
            public Action<int, int, string, string> Progress; // current, total, phase, item
            public Func<bool> Cancelled;
        }

        public sealed class SetResult
        {
            public string Plane = string.Empty;
            public List<string> Planes = new List<string>();
            public string Module = string.Empty;
            public string Folder = string.Empty;
            public int Views;
            public int Callouts;
            public bool Skipped;
            public string Error = string.Empty;
            public List<string> Notes = new List<string>();
        }

        public static string GameVersion(string dcsRoot)
        {
            try
            {
                string cfg = Path.Combine(dcsRoot, "autoupdate.cfg");
                if (!File.Exists(cfg))
                    return string.Empty;
                Match m = Regex.Match(File.ReadAllText(cfg), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        // The cockpits this DCS installation has: one entry per cockpit model, with the aircraft that use it.
        public sealed class Cockpit
        {
            public string Module = string.Empty;
            public string CockpitPath = string.Empty;
            public string Edm = string.Empty;
            public List<string> Planes = new List<string>();
            public List<DcsLua.Gauge> Gauges = new List<DcsLua.Gauge>();
        }

        public static List<Cockpit> Cockpits(string dcsRoot, string workDir, List<string> notes, List<string> only = null)
        {
            List<Cockpit> list = new List<Cockpit>();
            string mods = Path.Combine(dcsRoot, "Mods", "aircraft");
            foreach (DcsLua.Flyable f in DcsLua.Flyables(dcsRoot, workDir))
            {
                if (only != null && only.Count > 0 && !only.Any(o => string.Equals(o, f.Plane, StringComparison.OrdinalIgnoreCase) || string.Equals(o, f.Module, StringComparison.OrdinalIgnoreCase)))
                    continue;
                string moduleDir = Path.Combine(mods, f.Module);
                string cockpitPath = f.CockpitPath.Length > 0 ? f.CockpitPath : Path.Combine(moduleDir, "Cockpit", "Scripts");
                if (!Directory.Exists(cockpitPath) || !File.Exists(Path.Combine(cockpitPath, "clickabledata.lua")) && !File.Exists(Path.Combine(cockpitPath, "mainpanel_init.lua")))
                {
                    notes.Add(f.Plane + ": no cockpit scripts (" + cockpitPath + ")");
                    continue;
                }
                Cockpit known = list.FirstOrDefault(c => string.Equals(c.CockpitPath.TrimEnd('\\'), cockpitPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                if (known != null)
                {
                    known.Planes.Add(f.Plane);
                    continue;
                }
                string shape;
                List<DcsLua.Gauge> gauges = DcsLua.Gauges(dcsRoot, cockpitPath, workDir, out shape);
                string edm = FindEdm(moduleDir, shape);
                if (edm.Length == 0)
                {
                    notes.Add(f.Plane + ": no cockpit model found" + (shape.Length > 0 ? " (" + shape + ")" : string.Empty));
                    continue;
                }
                // Two cockpit folders with the same model (versions of one aircraft) share the pictures too.
                Cockpit sameModel = list.FirstOrDefault(c => string.Equals(c.Edm, edm, StringComparison.OrdinalIgnoreCase));
                if (sameModel != null && Path.GetFileName(cockpitPath.TrimEnd('\\')).Length > 0 && sameModel.Module == f.Module && SameClickables(sameModel.CockpitPath, cockpitPath))
                {
                    sameModel.Planes.Add(f.Plane);
                    continue;
                }
                list.Add(new Cockpit { Module = f.Module, CockpitPath = cockpitPath, Edm = edm, Planes = new List<string> { f.Plane }, Gauges = gauges });
            }
            return list;
        }

        private static bool SameClickables(string a, string b)
        {
            try
            {
                string fa = Path.Combine(a, "clickabledata.lua"), fb = Path.Combine(b, "clickabledata.lua");
                return File.Exists(fa) && File.Exists(fb) && new FileInfo(fa).Length == new FileInfo(fb).Length;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The cockpit model: the shape mainpanel_init.lua names, else the largest cockpit model of the module.
        public static string FindEdm(string moduleDir, string shape)
        {
            List<string> all;
            try
            {
                all = Directory.EnumerateFiles(moduleDir, "*.edm", SearchOption.AllDirectories).ToList();
            }
            catch (Exception)
            {
                return string.Empty;
            }
            if (shape.Length > 0)
            {
                string name = Path.GetFileNameWithoutExtension(shape.Replace('/', '\\'));
                string hit = all.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
                if (hit != null)
                    return hit;
            }
            string best = all.Where(f => Regex.IsMatch(Path.GetFileName(f), "cockpit|cpt", RegexOptions.IgnoreCase) && !Regex.IsMatch(Path.GetFileName(f), "pilot|helmet|visor|glass|ihadss|monocle", RegexOptions.IgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            return best ?? string.Empty;
        }

        public static List<SetResult> Build(Job job)
        {
            List<SetResult> results = new List<SetResult>();
            List<string> notes = new List<string>();
            Report(job, 0, 0, "DCS World, reading the modules", string.Empty);
            List<Cockpit> cockpits = Cockpits(job.DcsRoot, job.WorkDir, notes, job.Only);
            if (job.Only.Count > 0)
                cockpits = cockpits.Where(c => job.Only.Any(o => c.Planes.Any(p => string.Equals(p, o, StringComparison.OrdinalIgnoreCase)) || string.Equals(c.Module, o, StringComparison.OrdinalIgnoreCase))).ToList();
            string version = GameVersion(job.DcsRoot);
            AddInputNames(job.DcsRoot, cockpits);
            int i = 0;
            foreach (Cockpit c in cockpits)
            {
                i++;
                if (job.Cancelled != null && job.Cancelled())
                    break;
                string folderName = PictureWriter.FileName(c.Planes[0]).Length > 0 ? SafeFolder(c.Planes[0]) : SafeFolder(c.Module);
                string folder = Path.Combine(job.OutRoot, folderName);
                SetResult r = new SetResult { Plane = c.Planes[0], Planes = c.Planes, Module = c.Module, Folder = folder };
                results.Add(r);
                if (!job.Force && UpToDate(folder, version, c.Edm))
                {
                    r.Skipped = true;
                    continue;
                }
                Report(job, i, cockpits.Count, "DCS World, cockpit pictures", string.Join(", ", c.Planes));
                try
                {
                    BuildOne(job, c, folder, version, r);
                }
                catch (Exception ex)
                {
                    r.Error = ex.GetType().Name + ": " + ex.Message;
                }
            }
            WriteIndex(job.OutRoot, results);
            return results;
        }

        // The names JoyPro's DCS command lists use are the module's input profile folders (Input\<name>: "A-10C II",
        // "F-14_RIO", "AH-64D_BLK_II_CPG"); each joins the cockpit of the module whose aircraft name is most like it.
        public static void AddInputNames(string dcsRoot, List<Cockpit> cockpits)
        {
            foreach (IGrouping<string, Cockpit> module in cockpits.GroupBy(c => c.Module, StringComparer.OrdinalIgnoreCase))
            {
                // The declared profile names ("A-10C II" -> Input\A-10C_2): the cockpit whose names are most like the
                // profile's folder.
                foreach (string[] profile in DcsLua.InputProfiles.Where(x => string.Equals(x[0], module.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    if (module.Any(c => c.Planes.Any(p => string.Equals(p, profile[1], StringComparison.OrdinalIgnoreCase))))
                        continue;
                    string folder = Path.GetFileName(profile[2].Replace('/', '\\').TrimEnd('\\'));
                    Cockpit target = module.OrderByDescending(c => c.Planes.Max(p => Math.Max(Similarity(p, folder), Similarity(p, profile[1])))).First();
                    target.Planes.Add(profile[1]);
                }
                string input = Path.Combine(dcsRoot, "Mods", "aircraft", module.Key, "Input");
                if (!Directory.Exists(input))
                    continue;
                foreach (string dir in Directory.GetDirectories(input))
                {
                    string name = Path.GetFileName(dir);
                    if (module.Any(c => c.Planes.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase))))
                        continue;
                    // Device type folders directly under Input (modules with one profile) are no aircraft names.
                    if (Regex.IsMatch(name, "^(joystick|keyboard|mouse|trackir|headtracker|gamepad|default)$", RegexOptions.IgnoreCase))
                        continue;
                    Cockpit best = module.OrderByDescending(c => c.Planes.Max(p => Similarity(p, name))).First();
                    best.Planes.Add(name);
                }
            }
        }

        // Common prefix of the letters and digits, in characters (case-insensitive).
        public static int Similarity(string a, string b)
        {
            string x = Regex.Replace(a.ToLowerInvariant(), "[^a-z0-9]", string.Empty), y = Regex.Replace(b.ToLowerInvariant(), "[^a-z0-9]", string.Empty);
            int n = 0;
            while (n < x.Length && n < y.Length && x[n] == y[n])
                n++;
            return n;
        }

        private static string SafeFolder(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char ch in s ?? string.Empty)
                sb.Append(Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch);
            return sb.ToString();
        }

        private static void Report(Job job, int cur, int total, string phase, string item)
        {
            if (job.Progress != null)
                job.Progress(cur, total, phase, item);
        }

        private static bool UpToDate(string folder, string version, string edm)
        {
            string manifest = Path.Combine(folder, PictureWriter.ManifestName);
            if (!File.Exists(manifest))
                return false;
            try
            {
                string text = File.ReadAllText(manifest);
                Match g = Regex.Match(text, "\"generator\":(\\d+)");
                Match v = Regex.Match(text, "\"gameVersion\":\"([^\"]*)\"");
                return g.Success && g.Groups[1].Value == PictureWriter.GeneratorVersion.ToString(CultureInfo.InvariantCulture) && v.Success && v.Groups[1].Value == version &&
                       File.GetLastWriteTimeUtc(manifest) > File.GetLastWriteTimeUtc(edm);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // index.json: aircraft name -> folder, for every set of this game.
        public static void WriteIndex(string outRoot, List<SetResult> results)
        {
            Directory.CreateDirectory(outRoot);
            string file = Path.Combine(outRoot, "index.json");
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(file))
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(file), "\"([^\"]+)\":\"([^\"]+)\""))
                        map[m.Groups[1].Value] = m.Groups[2].Value;
                }
            }
            catch (Exception)
            {
            }
            map.Remove("format");
            foreach (SetResult r in results)
            {
                if (r.Error.Length > 0 || !File.Exists(Path.Combine(r.Folder, PictureWriter.ManifestName)))
                    continue;
                string folder = Path.GetFileName(r.Folder);
                foreach (string p in r.Planes)
                {
                    // A name several sets share ("Cessna 172 SP" of the plain, the G1000 and the seaplane version) leads to
                    // the plain one: no special-version word, then the shorter folder name.
                    string other;
                    if (map.TryGetValue(p, out other) && !string.Equals(other, folder, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(p, folder, StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(outRoot, other)) &&
                        IndexRank(other) <= IndexRank(folder))
                        continue;
                    map[p] = folder;
                }
            }
            StringBuilder sb = new StringBuilder();
            PictureWriter.Json j = new PictureWriter.Json(sb);
            j.Open('{');
            j.Prop("format", "joypro-cockpit-pictures-index");
            j.Key("aircraft");
            j.Open('{');
            foreach (KeyValuePair<string, string> kv in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                j.Prop(kv.Key, kv.Value);
            j.Close('}');
            j.Close('}');
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        }

        private static readonly Regex SpecialVersion = new Regex("(^|[_\\- ])(seaplane|floats?|floaters|skis?|amphib(ian)?|mariner|ai|tundra)($|[_\\- ])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Lower is a better set for a shared aircraft name.
        internal static int IndexRank(string folder)
        {
            return (SpecialVersion.IsMatch(folder ?? string.Empty) ? 1000 : 0) + (folder ?? string.Empty).Length;
        }

        private static void BuildOne(Job job, Cockpit c, string folder, string version, SetResult r)
        {
            string moduleDir = Path.Combine(job.DcsRoot, "Mods", "aircraft", c.Module);
            EdmFile edm = EdmFile.Read(c.Edm);
            foreach (string w in edm.Warnings.Take(3))
                r.Notes.Add("model: " + w);
            using (TextureLibrary lib = new TextureLibrary())
            {
                foreach (string dir in Directory.GetDirectories(moduleDir))
                {
                    string name = Path.GetFileName(dir);
                    if (Regex.IsMatch(name, "^(Liveries|Sounds|Doc|Docs|Missions|ImagesGui|Input|Options|Skins|l10n|bin|Entry|FM|Kneeboard)$", RegexOptions.IgnoreCase))
                        continue;
                    lib.AddFolder(dir, true);
                }
                // Some modules keep their textures in CoreMods (the C-130J, the F-14, the OH-58D: CoreMods/aircraft/<module>/Textures).
                string coreDir = Path.Combine(job.DcsRoot, "CoreMods", "aircraft", c.Module);
                if (Directory.Exists(coreDir))
                {
                    foreach (string dir in Directory.GetDirectories(coreDir))
                    {
                        if (!Regex.IsMatch(Path.GetFileName(dir), "^(Liveries|Sounds|Doc|Docs|Missions|bin)$", RegexOptions.IgnoreCase))
                            lib.AddFolder(dir, true);
                    }
                }
                lib.AddFolder(Path.Combine(job.DcsRoot, "Bazar", "World", "textures"), false);
                lib.AddFolder(Path.Combine(job.DcsRoot, "Bazar", "Textures"), false);
                CockpitScene scene = EdmScene.Build(edm, new EdmScene.Options { Texture = lib.Get });
                int missing = scene.Warnings.Count(w => w.StartsWith("texture not found", StringComparison.Ordinal));
                if (missing > 0)
                    r.Notes.Add(missing + " textures not found (drawn in a flat colour)");
                // The game's clickable controls.
                List<string> errors = new List<string>();
                List<DcsLua.Clickable> clickables = DcsLua.Clickables(job.DcsRoot, c.CockpitPath, job.WorkDir, errors);
                foreach (string e in errors.Take(2))
                    r.Notes.Add("clickabledata: " + e);
                // The command lists of every name of the aircraft (and of its seats: "AH-64D_BLK_II_PLT", "_CPG").
                List<InputCommand> commands = new List<InputCommand>();
                HashSet<string> read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string plane in c.Planes)
                {
                    string html = job.CommandList != null ? job.CommandList(plane) : string.Empty;
                    if (string.IsNullOrEmpty(html))
                        continue;
                    List<string> files = new List<string>();
                    if (File.Exists(html))
                        files.Add(html);
                    string dir = Path.GetDirectoryName(html);
                    if (Directory.Exists(dir))
                        files.AddRange(Directory.GetFiles(dir, Path.GetFileNameWithoutExtension(html) + "_*.html").Where(f => !Regex.IsMatch(Path.GetFileName(f), "_easy|_AI_Menu", RegexOptions.IgnoreCase)));
                    foreach (string f in files.Where(read.Add))
                    {
                        foreach (InputCommand ic in ReadCommands(f))
                        {
                            if (!commands.Any(x => x.Id == ic.Id))
                                commands.Add(ic);
                        }
                    }
                }
                if (commands.Count == 0)
                    r.Notes.Add("no command list of this aircraft yet (refresh DCS in JoyPro): callouts show the game's hint texts only");
                List<CockpitCallout> callouts = Callouts(scene, clickables, commands);
                // Connectors far from all the others (unplaced elements: the F-100D has some hundreds of metres away)
                // are no places in the cockpit: they pulled the eye estimate out of the cockpit.
                int outliers = CockpitViews.DropOutliers(callouts, 2.5);
                if (outliers > 0)
                    r.Notes.Add(outliers + " clickable connectors far outside the cockpit left out");
                // Seats and the pilot's eye.
                // Views.lua next to entry.lua, or in Entry\ (the F-100D).
                string viewsLua = Path.Combine(moduleDir, "Views.lua");
                if (!File.Exists(viewsLua) && File.Exists(Path.Combine(moduleDir, "Entry", "Views.lua")))
                    viewsLua = Path.Combine(moduleDir, "Entry", "Views.lua");
                List<DcsLua.Seat> seats = DcsLua.Seats(job.DcsRoot, viewsLua, job.WorkDir);
                List<Vec3> heads = seats.Select(s => new Vec3(s.EyePoint[0], s.EyePoint[1], s.EyePoint[2])).ToList();
                // An eye point of (0, 0, 0) is "not set" (the game then uses its own default): checked against the
                // controls below like the model's origin.
                bool unset = heads.Count > 0 && heads.All(h => h.Length < 1e-6);
                if (unset)
                    heads.Clear();
                ScenePoint headPoint;
                if (heads.Count == 0 && scene.Points.TryGetValue("head_cpt", out headPoint))
                    heads.Add(headPoint.Position);
                List<CockpitGrip> grips = Grips(edm, scene, c.Gauges);
                // The parts checked against the module's manual (GripParts: names fixed, non-parts left out).
                foreach (CockpitGrip g in grips)
                    GripParts.Apply("DCS", c.Module, g);
                // Without Views.lua the game's camera sits at the model's origin (plus DCS's usual eye offset).
                Vec3 origin = new Vec3(0.05, 0.1, 0);
                if (heads.Count == 0 && callouts.Count > 0 && (CockpitViews.Median(callouts.Select(x => x.Position).ToList()) - origin).Length < 1.2)
                    heads.Add(origin);
                if (heads.Count == 0)
                    heads.Add(EstimateHead(scene, grips, callouts));
                // An eye below most of the controls (the origin of a model whose eye is not there: the F-100D's camera sat
                // inside its compass) is estimated from the stick and the controls instead.
                if (heads.Count == 1 && callouts.Count >= 8 && callouts.Count(x => x.Position.Y > heads[0].Y - 0.1) > callouts.Count * 0.6)
                {
                    r.Notes.Add("the eye point is below most controls: placed above and behind the stick");
                    heads[0] = EstimateHead(scene, grips, callouts);
                }
                // Eye points far from every control (a seat of another cockpit model) are dropped.
                heads = heads.Where(h => callouts.Count == 0 || callouts.Any(x => (x.Position - h).Length < 1.4)).ToList();
                if (heads.Count == 0)
                    heads.Add(EstimateHead(scene, grips, callouts));
                // JOYPRO_PICTURE_DEBUG=1: where the eyes, grips and controls were found (in the build report).
                if (Environment.GetEnvironmentVariable("JOYPRO_PICTURE_DEBUG") == "1")
                {
                    Vec3 bmn, bmx;
                    scene.Bounds(Enumerable.Range(0, scene.Meshes.Count), out bmn, out bmx);
                    r.Notes.Add("debug: heads " + string.Join(" ", heads) + "; seats " + seats.Count + (unset ? " (unset)" : "") + "; grips " + string.Join(" ", grips.Select(g => g.Device + g.Pivot)) +
                                "; callouts centre " + (callouts.Count > 0 ? CockpitViews.Centre(callouts.Select(x => x.Position).ToList()).ToString() : "-") + "; bounds " + bmn + " " + bmx +
                                (callouts.Count > 0 ? "; callouts box " + callouts.Select(x => x.Position).Aggregate(Vec3.Min) + " " + callouts.Select(x => x.Position).Aggregate(Vec3.Max) : ""));
                }
                List<CockpitViewPlan> views = new List<CockpitViewPlan>();
                for (int s = 0; s < heads.Count; s++)
                {
                    int seat = s + 1;
                    List<CockpitCallout> mine = callouts.Where(x => Nearest(heads, x.Position) == s).ToList();
                    List<CockpitGrip> myGrips = grips.Where(g => Nearest(heads, g.Pivot) == s).ToList();
                    if (mine.Count == 0 && myGrips.Count == 0 && s > 0)
                        continue;
                    List<CockpitViewPlan> seatViews = CockpitViews.Plan(scene, heads[s], mine, myGrips, job.Width);
                    foreach (CockpitViewPlan v in seatViews)
                    {
                        v.Seat = seat;
                        if (seat > 1)
                        {
                            v.Id += "@" + seat;
                            for (int k = 0; k < v.Links.Count; k++)
                                v.Links[k] += "@" + seat;
                        }
                    }
                    views.AddRange(seatViews);
                }
                if (job.ViewFilter.Length > 0)
                    views = views.Where(v => v.Id.IndexOf(job.ViewFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                PictureWriter.SetInfo info = new PictureWriter.SetInfo
                {
                    Game = "DCS",
                    Plane = c.Planes[0],
                    Planes = c.Planes,
                    Module = c.Module,
                    Source = Path.GetFileName(c.Edm),
                    GameVersion = version,
                    Notes = r.Notes
                };
                PictureWriter.Write(scene, views, folder, info, (cur, total, title) => Report(job, cur, total, "DCS World, " + c.Planes[0] + " pictures", title), job.ViewFilter.Length == 0);
                r.Views = views.Count;
                r.Callouts = callouts.Count;
            }
        }

        private static int Nearest(List<Vec3> heads, Vec3 p)
        {
            int best = 0;
            double bd = double.MaxValue;
            for (int i = 0; i < heads.Count; i++)
            {
                // Along the aircraft (tandem seats) counts most.
                Vec3 d = p - heads[i];
                double dist = Math.Abs(d.X) * 1.0 + Math.Abs(d.Z) * 0.35 + Math.Abs(d.Y) * 0.2;
                if (dist < bd)
                {
                    bd = dist;
                    best = i;
                }
            }
            return best;
        }

        private static Vec3 EstimateHead(CockpitScene scene, List<CockpitGrip> grips, List<CockpitCallout> callouts)
        {
            CockpitGrip stick = grips.FirstOrDefault(g => g.Device == "stick");
            if (stick != null)
                return stick.Pivot + new Vec3(-0.3, 0.6, -stick.Pivot.Z);
            if (callouts.Count > 0)
            {
                Vec3 c = CockpitViews.Median(callouts.Select(x => x.Position).ToList());
                return c + new Vec3(-0.6, 0.35, 0);
            }
            return new Vec3(0, 0.1, 0);
        }

        // ------------------------------------------------------------------ commands

        public sealed class InputCommand
        {
            public string Name = string.Empty;
            public string Category = string.Empty;
            public string Id = string.Empty;
            public string Down = string.Empty, Pressed = string.Empty, Up = string.Empty, Device = string.Empty;
        }

        private static readonly Regex RowRx = new Regex("<tr>\\s*<td>(.*?)</td>\\s*<td>(.*?)</td>\\s*<td>(.*?)</td>\\s*<td>(.*?)</td>", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex HashRx = new Regex("^d(?<d>.+?)p(?<p>.+?)u(?<u>.+?)cd(?<cd>.+?)vd", RegexOptions.Compiled);

        public static List<InputCommand> ReadCommands(string html)
        {
            List<InputCommand> list = new List<InputCommand>();
            foreach (Match m in RowRx.Matches(File.ReadAllText(html, Encoding.UTF8)))
            {
                string id = WebUtility.HtmlDecode(m.Groups[4].Value).Trim();
                Match h = HashRx.Match(id);
                if (!h.Success)
                    continue;
                list.Add(new InputCommand
                {
                    Name = WebUtility.HtmlDecode(m.Groups[2].Value).Trim(),
                    Category = WebUtility.HtmlDecode(m.Groups[3].Value).Trim(),
                    Id = id,
                    Down = h.Groups["d"].Value,
                    Pressed = h.Groups["p"].Value,
                    Up = h.Groups["u"].Value,
                    Device = h.Groups["cd"].Value
                });
            }
            return list;
        }

        private static readonly Regex TokenRx = new Regex("^(?:PTR|PNT|PTN)[-_]+([A-Za-z0-9]+)[-_]", RegexOptions.Compiled);

        private static List<CockpitCallout> Callouts(CockpitScene scene, List<DcsLua.Clickable> clickables, List<InputCommand> commands)
        {
            Dictionary<string, List<InputCommand>> byDevice = commands.Where(x => x.Device != "nil").GroupBy(x => x.Device).ToDictionary(g => g.Key, g => g.ToList());
            List<CockpitCallout> list = new List<CockpitCallout>();
            foreach (DcsLua.Clickable k in clickables)
            {
                ScenePoint pt;
                if (!scene.Points.TryGetValue(k.Name, out pt))
                    continue;
                CockpitCallout c = new CockpitCallout
                {
                    Id = k.Name,
                    Label = k.Hint.Length > 0 ? k.Hint : k.Name,
                    Position = pt.Position,
                    Kind = Kind(k.Classes),
                    Device = k.DeviceName,
                    Actions = k.Actions.Distinct().ToList(),
                    Args = k.Args.Distinct().ToList()
                };
                c.Animation = DcsAnimation(k);
                c.GameKind = string.Join(",", k.Classes.Distinct());
                List<InputCommand> dev;
                if (k.Device >= 0 && byDevice.TryGetValue(k.Device.ToString(CultureInfo.InvariantCulture), out dev))
                {
                    foreach (InputCommand ic in dev.Where(x => c.Actions.Contains(x.Down) || c.Actions.Contains(x.Up) || c.Actions.Contains(x.Pressed)))
                        c.Commands.Add(new CalloutCommand { Name = ic.Name, Category = ic.Category, Id = ic.Id });
                }
                c.PanelTitle = CategoryPanel(c);
                Match t = TokenRx.Match(k.Name);
                c.Panel = t.Success && t.Groups[1].Value.Length >= 2 && !Regex.IsMatch(t.Groups[1].Value, "^[0-9]+$") ? "token:" + t.Groups[1].Value.ToUpperInvariant() : string.Empty;
                if (c.Panel.Length == 0 && k.DeviceName.Length > 0)
                    c.Panel = "device:" + k.DeviceName;
                list.Add(c);
            }
            // The panel of each control: the connector's panel token where the model names its connectors so (most
            // of them), else the panel the bindable commands name, else the game's device. A panel's title is the
            // name most of its commands give, else the token.
            bool tokens = list.Count > 0 && list.Count(x => x.Panel.StartsWith("token:", StringComparison.Ordinal)) * 10 >= list.Count * 6;
            foreach (CockpitCallout c in list)
            {
                if (!tokens || !c.Panel.StartsWith("token:", StringComparison.Ordinal))
                    c.Panel = c.PanelTitle.Length > 0 ? "panel:" + c.PanelTitle : c.Panel;
            }
            // Controls without a panel name join the nearest control that has one (within 8 cm), else their device.
            foreach (CockpitCallout c in list.Where(x => x.Panel.StartsWith("device:", StringComparison.Ordinal) || x.Panel.Length == 0).ToList())
            {
                CockpitCallout near = list.Where(x => x != c && x.Panel.StartsWith("panel:", StringComparison.Ordinal) || x != c && x.Panel.StartsWith("token:", StringComparison.Ordinal))
                    .OrderBy(x => (x.Position - c.Position).Length).FirstOrDefault();
                if (near != null && (near.Position - c.Position).Length < 0.08)
                    c.Panel = near.Panel;
                else if (c.Panel.Length == 0)
                    c.Panel = "Controls";
            }
            foreach (IGrouping<string, CockpitCallout> g in list.GroupBy(x => x.Panel))
            {
                string title = g.Select(x => x.PanelTitle).Where(x => x.Length > 0).GroupBy(x => x).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault();
                if (string.IsNullOrEmpty(title))
                {
                    string key = g.Key.Substring(g.Key.IndexOf(':') + 1);
                    title = g.Key.StartsWith("device:", StringComparison.Ordinal) ? Humanize(key) : key;
                }
                foreach (CockpitCallout c in g)
                    c.PanelTitle = title;
            }
            return list;
        }

        // The panel the control's bindable commands name ("Left Console; ENG & JET START Control Panel"), or empty.
        private static string CategoryPanel(CockpitCallout c)
        {
            string category = c.Commands.GroupBy(x => x.Category).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? string.Empty;
            string panel = category.Split(';').Select(s => s.Trim()).LastOrDefault(s => s.Length > 0) ?? string.Empty;
            if (Regex.IsMatch(panel, "^(General|Systems|Cockpit|Miscellaneous|Misc|Others?|Special For Joystick|View Cockpit|Flight Control|Sensors|Weapons|Countermeasures|Communications|Navigation|Autopilot|Engine|Lights?|Fuel|Electric|Hydraulic)$", RegexOptions.IgnoreCase))
                return string.Empty;
            return panel;
        }

        // The clickable element's draw argument, its range (arg_lim) and, for a switch, its positions (arg_value steps
        // over the range): clickabledata.lua of the cockpit.
        internal static ControlAnimation DcsAnimation(DcsLua.Clickable k)
        {
            string arg = k.Args.FirstOrDefault(x => x.Length > 0 && x != "nil");
            if (arg == null)
                return null;
            ControlAnimation a = new ControlAnimation { Source = "draw argument", Name = arg };
            double min = double.NaN, max = double.NaN, step = double.NaN;
            foreach (string l in k.Limits)
            {
                string[] mm = (l ?? string.Empty).Split(':');
                double lo, hi;
                if (mm.Length == 2 && double.TryParse(mm[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lo) && double.TryParse(mm[1], NumberStyles.Float, CultureInfo.InvariantCulture, out hi))
                {
                    min = double.IsNaN(min) ? Math.Min(lo, hi) : Math.Min(min, Math.Min(lo, hi));
                    max = double.IsNaN(max) ? Math.Max(lo, hi) : Math.Max(max, Math.Max(lo, hi));
                }
            }
            foreach (string v in k.Values)
            {
                double d;
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && Math.Abs(d) > 1e-9)
                {
                    step = double.IsNaN(step) ? Math.Abs(d) : Math.Min(step, Math.Abs(d));
                }
            }
            a.Min = min;
            a.Max = max;
            string cls = (k.Classes.FirstOrDefault() ?? string.Empty).ToUpperInvariant();
            if (!double.IsNaN(min) && !double.IsNaN(max) && !double.IsNaN(step) && max > min && (cls == "TUMB" || cls == "BTN" || cls == "BTN_FIX" || cls == "SNGBTN"))
            {
                int n = (int)Math.Round((max - min) / step) + 1;
                if (n >= 2 && n <= 24)
                {
                    for (int i = 0; i < n; i++)
                        a.Positions.Add(Math.Round(min + i * step, 4));
                }
            }
            return a;
        }

        private static string Kind(List<string> classes)
        {
            string c = classes.FirstOrDefault() ?? string.Empty;
            switch (c.ToUpperInvariant())
            {
                case "BTN":
                case "BTN_FIX":
                case "SNGBTN":
                    return "button";
                case "TUMB":
                    return "switch";
                case "LEV":
                case "MOVABLE_LEV":
                    return "knob";
                default:
                    return c.ToLowerInvariant();
            }
        }

        public static string Humanize(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return string.Empty;
            string t = s.Replace('_', ' ').Trim();
            return t.Length <= 4 ? t.ToUpperInvariant() : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(t.ToLowerInvariant());
        }

        // ------------------------------------------------------------------ grips

        private static readonly Regex StickRx = new Regex("stick|cyclic|ruchk|joystick|control_?column|yoke|rss", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AxisRx = new Regex("pitch|roll|bank|elev|ail|longitud|lateral", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ThrottleRx = new Regex("throttle|thrott|trottle|trtl|\\brud\\b|rud_|sector|thrust|power_?lever|engine_?lever|gas", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CollectiveRx = new Regex("collective|hc_?pos|step", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NotGripRx = new Regex("hide|visib|show|friction|cutoff|stop|lock|tension|trim|sw|btn|button|light|temp|rpm|indicator|needle|gauge|arrow", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex StickNodeRx = new Regex("^(main_|flcs_|pilot_)?(stick|joystick|ruchk|cyclic)([-_ ]?(root|base|assembly|aasembly|lever.*|\\d+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ThrottleNodeRx = new Regex("^(pilot_)?(throttle|trottle|trtl|collective|power_?lever)([-_ ]?(l|r|left|right|\\d+|dummy\\d*|normal|handle|axis|lever|root|base|assembly|quadrant))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NotGripNodeRx = new Regex("ghost|missile|hide|visib|dimmer|helper|cover|emerg|friction|rubber|finger|bone|wire", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static List<CockpitGrip> Grips(EdmFile edm, CockpitScene scene, List<DcsLua.Gauge> gauges)
        {
            // Animation arguments per grip kind.
            Dictionary<int, string> argKind = new Dictionary<int, string>();
            foreach (DcsLua.Gauge g in gauges)
            {
                string text = g.Name + " " + g.Controller;
                string kind = null;
                if (NotGripRx.IsMatch(g.Name))
                    continue;
                if (CollectiveRx.IsMatch(text) && !Regex.IsMatch(text, "pedal", RegexOptions.IgnoreCase))
                    kind = "throttle";
                else if (StickRx.IsMatch(text) && AxisRx.IsMatch(text))
                    kind = "stick";
                else if (ThrottleRx.IsMatch(text) && !Regex.IsMatch(text, "rudder|pedal", RegexOptions.IgnoreCase))
                    kind = "throttle";
                if (kind == null)
                    continue;
                foreach (int a in g.Args)
                {
                    if (!argKind.ContainsKey(a))
                        argKind[a] = kind;
                }
            }
            int n = edm.Nodes.Count;
            string[] nodeKind = new string[n];
            for (int i = 0; i < n; i++)
            {
                EdmNode node = edm.Nodes[i];
                if (node.Kind != EdmNodeKind.Animation)
                    continue;
                foreach (EdmKeys k in node.RotationKeys.Concat(node.PositionKeys))
                {
                    string kind;
                    if (argKind.TryGetValue(k.Arg, out kind))
                        nodeKind[i] = kind;
                }
            }
            // The handles the VR hands use mark grips too.
            foreach (EdmConnector cn in edm.Connectors)
            {
                string kind = Regex.IsMatch(cn.Name, "^STICK_(HANDLE|PIVOT)", RegexOptions.IgnoreCase) ? "stick"
                    : Regex.IsMatch(cn.Name, "^(THROTTLE|TROTTLE)_.*HANDLE|^COLLECTIVE", RegexOptions.IgnoreCase) ? "throttle" : null;
                if (kind == null || cn.Parent < 0 || cn.Parent >= n)
                    continue;
                // The nearest animated ancestor.
                int a = cn.Parent;
                for (int guard = 0; a >= 0 && a < n && guard < 64; guard++)
                {
                    if (edm.Nodes[a].Kind == EdmNodeKind.Animation)
                    {
                        if (nodeKind[a] == null)
                            nodeKind[a] = kind;
                        break;
                    }
                    a = edm.Nodes[a].Parent;
                }
            }
            // Modules that animate the grips in their own code: the grips by the model's node names.
            foreach (string kind in new[] { "stick", "throttle" })
            {
                if (nodeKind.Any(k => k == kind))
                    continue;
                Regex rx = kind == "stick" ? StickNodeRx : ThrottleNodeRx;
                for (int i = 0; i < n; i++)
                {
                    string name = edm.Nodes[i].Name;
                    if (nodeKind[i] == null && rx.IsMatch(name) && !NotGripNodeRx.IsMatch(name))
                        nodeKind[i] = kind;
                }
            }
            // Topmost marked node of each chain, extended over visibility and marked parents.
            List<int> roots = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (nodeKind[i] == null)
                    continue;
                int top = i;
                int p = edm.Nodes[i].Parent;
                bool inside = false;
                for (int guard = 0; p >= 0 && p < n && guard < 128; guard++)
                {
                    if (nodeKind[p] != null)
                    {
                        inside = true;
                        break;
                    }
                    p = edm.Nodes[p].Parent;
                }
                if (inside)
                    continue;
                // Climb over visibility nodes and transform nodes that hold only this grip.
                int q = edm.Nodes[top].Parent;
                while (q > 0 && q < n && (edm.Nodes[q].Kind == EdmNodeKind.Visibility) && edm.Nodes[q].Parent >= 0)
                {
                    top = q;
                    q = edm.Nodes[q].Parent;
                }
                if (!roots.Contains(top))
                    roots.Add(top);
            }
            List<CockpitGrip> grips = new List<CockpitGrip>();
            foreach (int root in roots)
            {
                List<int> meshes;
                if (!scene.NodeMeshes.TryGetValue(root, out meshes) || meshes.Count == 0)
                    continue;
                string kind = KindOf(edm, root, nodeKind);
                Vec3 mn, mx;
                scene.Bounds(meshes, out mn, out mx);
                Vec3 size = mx - mn;
                if (size.Length > 1.2 || size.Length < 0.02)
                    continue;
                Vec3 centre = (mn + mx) * 0.5;
                // Grips of one kind close together are one (two throttle levers in one quadrant).
                CockpitGrip near = grips.FirstOrDefault(g => g.Device == kind && (g.Pivot - centre).Length < 0.32);
                if (near == null)
                {
                    near = new CockpitGrip { Device = kind, Animation = new ControlAnimation { Source = "model node", Name = edm.Nodes[root].Name } };
                    grips.Add(near);
                }
                foreach (int m in meshes)
                {
                    if (!near.Meshes.Contains(m))
                        near.Meshes.Add(m);
                }
                Vec3 a, b;
                scene.Bounds(near.Meshes, out a, out b);
                near.Pivot = (a + b) * 0.5;
                // JOYPRO_PICTURE_DEBUG_GRIPS=<folder>: every node under the grip (name, meshes, place) for checking the parts.
                string dump = Environment.GetEnvironmentVariable("JOYPRO_PICTURE_DEBUG_GRIPS");
                if (!string.IsNullOrEmpty(dump))
                {
                    try
                    {
                        Directory.CreateDirectory(dump);
                        List<string> lines = new List<string>();
                        foreach (int child in Descendants(edm, root))
                        {
                            List<int> cm2;
                            Vec3 q0 = new Vec3(0, 0, 0), q1 = new Vec3(0, 0, 0);
                            bool has = scene.NodeMeshes.TryGetValue(child, out cm2) && cm2.Count > 0;
                            if (has)
                                scene.Bounds(cm2, out q0, out q1);
                            ScenePoint sp;
                            string pt = scene.Points.TryGetValue(edm.Nodes[child].Name, out sp) ? sp.Position.ToString() : "";
                            lines.Add(child + "	" + edm.Nodes[child].Name + "	" + edm.Nodes[child].Kind + "	parent " + edm.Nodes[child].Parent + "	" + (has ? cm2.Count + " meshes " + ((q0 + q1) * 0.5) + " size " + (q1 - q0).Length.ToString("0.000", CultureInfo.InvariantCulture) : "no mesh") + "	" + pt);
                        }
                        File.WriteAllLines(Path.Combine(dump, kind + "-" + edm.Nodes[root].Name.Replace('/', '_') + ".tsv"), lines);
                    }
                    catch (Exception)
                    {
                    }
                }
                // Named parts on the grip (buttons, hats, levers): nodes under it with a name of their own.
                foreach (int child in Descendants(edm, root))
                {
                    string name = edm.Nodes[child].Name;
                    if (!Meaningful(name) || near.Parts.Any(x => x.Id == name) || child == root || Regex.IsMatch(name, "helper|pivot|base|_ref|ref$|visib|hide|bone|dummy|stick$|^main", RegexOptions.IgnoreCase))
                        continue;
                    List<int> cm;
                    Vec3 pos;
                    if (scene.NodeMeshes.TryGetValue(child, out cm) && cm.Count > 0 && cm.Count < meshes.Count)
                    {
                        Vec3 p0, p1;
                        scene.Bounds(cm, out p0, out p1);
                        pos = (p0 + p1) * 0.5;
                    }
                    else
                    {
                        continue;
                    }
                    near.Parts.Add(new CockpitCallout { Id = name, Label = PartLabel(name), Position = pos, Kind = "part" });
                }
            }
            return grips;
        }

        private static string KindOf(EdmFile edm, int root, string[] nodeKind)
        {
            if (nodeKind[root] != null)
                return nodeKind[root];
            foreach (int d in Descendants(edm, root))
            {
                if (nodeKind[d] != null)
                    return nodeKind[d];
            }
            return "stick";
        }

        private static IEnumerable<int> Descendants(EdmFile edm, int root)
        {
            int n = edm.Nodes.Count;
            for (int i = 0; i < n; i++)
            {
                int p = edm.Nodes[i].Parent;
                for (int guard = 0; p >= 0 && p < n && guard < 128; guard++)
                {
                    if (p == root)
                    {
                        yield return i;
                        break;
                    }
                    p = edm.Nodes[p].Parent;
                }
            }
        }

        private static readonly Regex GenericNameRx = new Regex("^(dummy|object|box|mesh|cylinder|plane|line|group|point|sphere|helper|bone|node|default|scene|shape|loft|tube|torus|cone|pivot|stick_handle|throttle_handle|connector)[\\s_\\-]*\\d*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static bool Meaningful(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length < 3)
                return false;
            if (GenericNameRx.IsMatch(name) || Regex.IsMatch(name, "^\\d+$") || Regex.IsMatch(name, "^Object\\d+", RegexOptions.IgnoreCase))
                return false;
            return Regex.IsMatch(name, "[A-Za-z]{3}");
        }

        public static string PartLabel(string name)
        {
            string s = Regex.Replace(name, "^(DUM|PTR|PNT|TRTL|STICK|THR|STK|LVR|TMB|BTN|HAT)[-_]", string.Empty, RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "\\d+$", string.Empty);
            s = s.Replace('_', ' ').Replace('-', ' ').Trim();
            return s.Length == 0 ? name : s;
        }
    }
}
