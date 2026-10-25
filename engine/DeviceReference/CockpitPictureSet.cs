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

namespace JoyPro.DeviceReference
{
    // The cockpit pictures JoyPro made on this PC from the user's own games (docs/cockpits.md "Pictures from your
    // games"): <data>\GameData\CockpitPictures\<game>\<aircraft>\pictures.json ("joypro-cockpit-pictures") with, per
    // view (overview, a grip, a panel), the rendered picture (PNG, or JPEG with a mask PNG for its transparency), JoyPro's
    // line drawing of it and the callouts at the game's own clickable controls. Not in the repository: the engine builds
    // them ("cockpits pictures build"), or the GUI downloads a picture pack of JoyPro's releases (docs/cockpits.md
    // "Picture packs", pack.json next to pictures.json); the Cockpits page shows them instead of JoyPro's drawings.
    public sealed class CockpitPictureSet
    {
        public const string FolderName = "CockpitPictures";
        public const string FormatName = "joypro-cockpit-pictures";
        public const string PackFileName = "pack.json";
        public const string PackFormatName = "joypro-cockpit-pack";

        public string Game = string.Empty;
        public string Plane = string.Empty;
        public List<string> Planes = new List<string>();
        public string Module = string.Empty;
        public string Source = string.Empty;
        public string GameVersion = string.Empty;
        public string Built = string.Empty;
        public List<string> Notes = new List<string>();
        public string Folder = string.Empty;
        // The version of the picture pack the set came from; 0: made on this PC.
        public int PackVersion;
        public List<CockpitPictureView> Views = new List<CockpitPictureView>();
        // The quality gate's verdict when the set was made (pictures.json "quality", docs/cockpits.md "Picture quality"):
        // the overview's score (0..100, -1 = not rated) and "low" (nothing usable): another game's set is shown first.
        public int QualityScore = -1;
        public bool LowQuality;
        // Every control of the cockpit once (pictures.json "controls", docs/cockpits.md "Animated controls"): its kind,
        // what moves it in the game's files and its place on each picture it is visible in.
        public List<CockpitPictureControl> Controls = new List<CockpitPictureControl>();

        public bool FromPack { get { return PackVersion > 0; } }

        public static string Root()
        {
            return Path.Combine(DeviceCatalog.DataRoot(), "GameData", FolderName);
        }

        public static CockpitPictureSet Load(string folder)
        {
            string file = Path.Combine(folder ?? string.Empty, "pictures.json");
            if (!File.Exists(file))
                return null;
            Dictionary<string, object> root = MiniJson.Parse(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
            if (root == null || !string.Equals(DeviceCatalog.Text(root, "format"), FormatName, StringComparison.OrdinalIgnoreCase))
                return null;
            CockpitPictureSet set = new CockpitPictureSet
            {
                Game = DeviceCatalog.Text(root, "game"),
                Plane = DeviceCatalog.Text(root, "plane"),
                Module = DeviceCatalog.Text(root, "module"),
                Source = DeviceCatalog.Text(root, "source"),
                GameVersion = DeviceCatalog.Text(root, "gameVersion"),
                Built = DeviceCatalog.Text(root, "built"),
                Folder = folder
            };
            set.Planes = Strings(root, "planes");
            set.Notes = Strings(root, "notes");
            Dictionary<string, object> quality = DeviceCatalog.Object(root, "quality");
            if (quality != null)
            {
                set.QualityScore = (int)Number(quality, "score", -1);
                set.LowQuality = quality.ContainsKey("low") && quality["low"] is bool && (bool)quality["low"];
            }
            foreach (Dictionary<string, object> c in DeviceCatalog.Objects(root, "controls"))
            {
                CockpitPictureControl control = new CockpitPictureControl
                {
                    Id = DeviceCatalog.Text(c, "id"),
                    Label = DeviceCatalog.Text(c, "label"),
                    Kind = DeviceCatalog.Text(c, "kind"),
                    GameKind = DeviceCatalog.Text(c, "gameKind"),
                    Panel = DeviceCatalog.Text(c, "panel"),
                    Device = DeviceCatalog.Text(c, "device"),
                    Seat = (int)Number(c, "seat", 1)
                };
                Dictionary<string, object> a = DeviceCatalog.Object(c, "animation");
                if (a != null)
                {
                    control.AnimationSource = DeviceCatalog.Text(a, "source");
                    control.AnimationName = DeviceCatalog.Text(a, "name");
                    control.Min = Number(a, "min", double.NaN);
                    control.Max = Number(a, "max", double.NaN);
                    object positions;
                    if (a.TryGetValue("positions", out positions) && positions is List<object>)
                        control.Positions.AddRange(((List<object>)positions).Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)));
                }
                control.Commands.AddRange(Strings(c, "commands"));
                foreach (Dictionary<string, object> at in DeviceCatalog.Objects(c, "views"))
                    control.Views.Add(new CockpitPictureSpot { View = DeviceCatalog.Text(at, "view"), N = (int)Number(at, "n", 0), X = Number(at, "x", -1), Y = Number(at, "y", -1) });
                set.Controls.Add(control);
            }
            foreach (Dictionary<string, object> v in DeviceCatalog.Objects(root, "views"))
            {
                CockpitPictureView view = new CockpitPictureView
                {
                    Id = DeviceCatalog.Text(v, "id"),
                    Kind = DeviceCatalog.Text(v, "kind"),
                    Title = DeviceCatalog.Text(v, "title"),
                    Area = DeviceCatalog.Text(v, "area"),
                    Device = DeviceCatalog.Text(v, "device"),
                    Seat = (int)Number(v, "seat", 1),
                    Image = Path.Combine(folder, DeviceCatalog.Text(v, "image")),
                    Mask = DeviceCatalog.Text(v, "mask").Length > 0 ? Path.Combine(folder, DeviceCatalog.Text(v, "mask")) : string.Empty,
                    Drawing = Path.Combine(folder, DeviceCatalog.Text(v, "drawing")),
                    Width = Number(v, "width", 1),
                    Height = Number(v, "height", 1),
                    Quality = (int)Number(v, "quality", -1),
                    DrawingReadable = !(v.ContainsKey("drawingReadable") && v["drawingReadable"] is bool && !(bool)v["drawingReadable"]),
                    Set = set
                };
                foreach (Dictionary<string, object> c in DeviceCatalog.Objects(v, "callouts"))
                {
                    CockpitPictureCallout callout = new CockpitPictureCallout
                    {
                        N = (int)Number(c, "n", 0),
                        Id = DeviceCatalog.Text(c, "id"),
                        Label = DeviceCatalog.Text(c, "label"),
                        X = Number(c, "x", -1),
                        Y = Number(c, "y", -1),
                        Hidden = c.ContainsKey("hidden") && c["hidden"] is bool && (bool)c["hidden"],
                        Kind = DeviceCatalog.Text(c, "kind"),
                        Device = DeviceCatalog.Text(c, "device"),
                        Panel = DeviceCatalog.Text(c, "panel"),
                        Link = DeviceCatalog.Text(c, "link")
                    };
                    callout.Positions = CockpitCatalog.Strings(c, "positions").ToList();
                    if (callout.Positions.Count == 0)
                    {
                        int states = (int)Number(c, "states", 0);
                        for (int i = 1; states >= 2 && states <= 12 && i <= states; i++)
                            callout.Positions.Add("Position " + i.ToString(CultureInfo.InvariantCulture));
                    }
                    foreach (Dictionary<string, object> cmd in DeviceCatalog.Objects(c, "commands"))
                        callout.Commands.Add(new CockpitPictureCommand { Name = DeviceCatalog.Text(cmd, "name"), Category = DeviceCatalog.Text(cmd, "category"), Id = DeviceCatalog.Text(cmd, "id") });
                    view.Callouts.Add(callout);
                }
                set.Views.Add(view);
            }
            ReadPack(set);
            return set;
        }

        // A set from a picture pack of JoyPro's releases has the pack's pack.json next to pictures.json (written when it
        // was installed). It counts only while pictures.json is the pack's: a build on this PC writes a new one.
        private static void ReadPack(CockpitPictureSet set)
        {
            try
            {
                string file = Path.Combine(set.Folder, PackFileName);
                if (!File.Exists(file))
                    return;
                Dictionary<string, object> pack = MiniJson.Parse(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                if (pack == null || !string.Equals(DeviceCatalog.Text(pack, "format"), PackFormatName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(DeviceCatalog.Text(pack, "picturesBuilt"), set.Built, StringComparison.Ordinal))
                    return;
                set.PackVersion = Math.Max(1, (int)Number(pack, "version", 1));
            }
            catch (Exception)
            {
                set.PackVersion = 0;
            }
        }

        private static List<string> Strings(Dictionary<string, object> item, string key)
        {
            object value;
            List<string> list = new List<string>();
            if (item.TryGetValue(key, out value) && value is List<object>)
                list.AddRange(((List<object>)value).OfType<string>());
            return list;
        }

        private static double Number(Dictionary<string, object> item, string key, double fallback)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value) || value == null)
                return fallback;
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        // ------------------------------------------------------------------ finding the set of a variant

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Tuple<DateTime, CockpitPictureSet>> Cache = new Dictionary<string, Tuple<DateTime, CockpitPictureSet>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Tuple<DateTime, Dictionary<string, string>>> Indexes = new Dictionary<string, Tuple<DateTime, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

        // The game folders the sets are kept in, per JoyPro game id (the order is the order of preference: the game with
        // the most cockpit detail first).
        // Pictures rendered from a game's models (callouts on the controls) come before MSFS 2024's screenshots (its
        // models cannot be read): an MSFS 2024 aircraft shows the same aircraft's pictures from MSFS 2020 or X-Plane.
        public static readonly string[] Games = { "DCS", "XPlane12", "MSFS2020", "MSFS2024", "IL2Game", "IL2Korea", "NuclearOption", "StarCitizen", "WarThunder" };

        // Where a game's sets are: MSFS 2024 keeps its models encrypted, so its aircraft use the MSFS 2020 models.
        private static IEnumerable<string> FoldersOf(string game)
        {
            yield return game;
            if (game == "MSFS2024")
                yield return "MSFS2020";
        }

        // The set for a variant: its games in the order above; for each, the index of that game's folder names the set
        // by the game's aircraft name, else by the variant's or the aircraft's names, else (games without aircraft names
        // in JoyPro's tables) by the type number and words they share ("Cessna 172 Skyhawk" and "Cessna_172SP").
        public static CockpitPictureSet For(CockpitVariant v, string root = null)
        {
            root = root ?? Root();
            if (!Directory.Exists(root))
                return null;
            CockpitPictureSet found = null;
            // A good set first (in the order of the games); a low-quality one only when no game has a better one.
            Locate(v, game => Index(Path.Combine(root, game)), (game, folder) =>
            {
                CockpitPictureSet s = Cached(Path.Combine(root, game, folder));
                if (s == null || s.LowQuality)
                    return false;
                found = s;
                return true;
            });
            if (found != null)
                return found;
            Locate(v, game => Index(Path.Combine(root, game)), (game, folder) =>
            {
                found = Cached(Path.Combine(root, game, folder));
                return found != null;
            });
            return found;
        }

        // Every set of a variant, one per game folder: the good ones in the order of the games, then the low-quality
        // ones (the Cockpits page offers the choice when there are several: "Pictures from").
        public static List<CockpitPictureSet> AllFor(CockpitVariant v, string root = null)
        {
            root = root ?? Root();
            List<CockpitPictureSet> found = new List<CockpitPictureSet>();
            if (!Directory.Exists(root))
                return found;
            foreach (string game in Games.Concat(new[] { "MSFS2020" }).Distinct())
            {
                string only = game;
                Locate(v, g => string.Equals(g, only, StringComparison.OrdinalIgnoreCase) ? Index(Path.Combine(root, g)) : new Dictionary<string, string>(), (g, folder) =>
                {
                    CockpitPictureSet s = Cached(Path.Combine(root, g, folder));
                    if (s == null || found.Any(x => string.Equals(x.Folder, s.Folder, StringComparison.OrdinalIgnoreCase)))
                        return false;
                    found.Add(s);
                    return true;
                });
            }
            return found.Where(x => !x.LowQuality).Concat(found.Where(x => x.LowQuality)).ToList();
        }

        // The game folder and the set folder of a variant by the indexes of the game folders (normalised aircraft name ->
        // set folder, as IndexOf makes them): the first one the caller accepts ("usable"), or null. Used for the sets on
        // this PC (For) and for the picture packs of JoyPro's releases (the names in their manifest).
        public static Tuple<string, string> Locate(CockpitVariant v, Func<string, Dictionary<string, string>> indexOf, Func<string, string, bool> usable)
        {
            if (v == null || (v.Aircraft != null && v.Aircraft.Fiction && !v.Games.Any(g => g.Game == "StarCitizen" || g.Game == "NuclearOption")))
                return null;
            foreach (string game in Games)
            {
                List<CockpitGameRef> refs = v.Games.Where(g => string.Equals(g.Game, game, StringComparison.OrdinalIgnoreCase)).ToList();
                if (refs.Count == 0)
                    continue;
                List<string> names = refs.Select(r => r.Plane).Where(p => p.Length > 0).ToList();
                bool planeNames = names.Count > 0;
                names.Add(v.Id);
                names.AddRange(v.Aliases);
                names.Add(v.Name);
                // The family's names ("Cessna 172 Skyhawk", "C172") last: for a version without the game's aircraft
                // names they come after the version's own match (the G1000 version found the plain 172 by "C172").
                List<string> family = new List<string>();
                if (v.Aircraft != null)
                {
                    family.Add(v.Aircraft.Name);
                    family.AddRange(v.Aircraft.Aliases);
                }
                if (planeNames)
                    names.AddRange(family);
                foreach (string folderGame in FoldersOf(game))
                {
                    Dictionary<string, string> index = indexOf(folderGame) ?? new Dictionary<string, string>();
                    if (index.Count == 0)
                        continue;
                    foreach (string name in names)
                    {
                        string folder;
                        if (index.TryGetValue(Norm(name), out folder) && usable(folderGame, folder))
                            return Tuple.Create(folderGame, folder);
                    }
                    if (planeNames)
                    {
                        // A seat's name of the game's aircraft ("AH-64D_BLK_II_PLT" of "AH-64D_BLK_II"): the longest
                        // aircraft name it starts with.
                        foreach (string plane in refs.Select(r => Norm(r.Plane)).Where(p => p.Length > 0))
                        {
                            string prefix = index.Keys.Where(k => k.Length >= 5 && plane.StartsWith(k, StringComparison.Ordinal)).OrderByDescending(k => k.Length).FirstOrDefault();
                            if (prefix != null && usable(folderGame, index[prefix]))
                                return Tuple.Create(folderGame, index[prefix]);
                        }
                        continue;
                    }
                    string fuzzy = Fuzzy(index, names.Concat(family));
                    if (fuzzy != null && usable(folderGame, fuzzy))
                        return Tuple.Create(folderGame, fuzzy);
                    foreach (string name in family)
                    {
                        string folder;
                        if (index.TryGetValue(Norm(name), out folder) && usable(folderGame, folder))
                            return Tuple.Create(folderGame, folder);
                    }
                }
            }
            return null;
        }

        // An index of aircraft names to set folders (the names normalised; the first folder of a name wins).
        public static Dictionary<string, string> IndexOf(IEnumerable<KeyValuePair<string, string>> names)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in names)
            {
                string key = Norm(pair.Key);
                if (key.Length > 0 && !string.IsNullOrEmpty(pair.Value) && !map.ContainsKey(key))
                    map[key] = pair.Value;
            }
            return map;
        }

        private static readonly HashSet<string> Avionics = new HashSet<string>(StringComparer.Ordinal) { "1000", "3000", "5000", "950", "430", "530" };

        // Type designations of a name: letters with the number right after them ("A-10C" -> a10, "asobo_c172sp" -> c172).
        private static IEnumerable<string> Designations(string s)
        {
            foreach (Match m in Regex.Matches((s ?? string.Empty).ToLowerInvariant(), "([a-z]+)[-_ ]?([0-9]+)"))
            {
                string d = m.Groups[1].Value + m.Groups[2].Value;
                if (m.Groups[1].Value != "asobo" && d.Length >= 3 && !Avionics.Contains(m.Groups[2].Value))
                    yield return d;
            }
        }

        // Words and numbers of a name: "cessna172sp" -> cessna, 172, sp.
        private static IEnumerable<string> Tokens(string s)
        {
            foreach (Match m in Regex.Matches((s ?? string.Empty).ToLowerInvariant(), "[a-z]+|[0-9]+"))
                yield return m.Value;
        }

        // Words that name a special version (floats, skis, an AI-only model): they count against a set the variant does
        // not name them for.
        private static readonly HashSet<string> Versions = new HashSet<string>(StringComparer.Ordinal)
        {
            "seaplane", "floats", "float", "floaters", "skis", "ski", "amphibian", "amphib", "mariner", "ai", "tundra"
        };

        private static readonly HashSet<string> Common = new HashSet<string>(StringComparer.Ordinal)
        {
            "the", "and", "asobo", "aircraft", "airplane", "airplanes", "microsoft", "laminar", "research", "classic", "default", "series", "type", "model", "version", "block", "mk"
        };

        // The folder whose names share the most with the variant's: a type number of three or more digits (or two) and a
        // word (the maker or the type's name: "Cessna 172" and "Cessna_172SP") must be shared, so a number alone never
        // matches across families (the A350 and the King Air 350, the CRJ-1000 and a 172's G1000, the PA-28's "Cherokee
        // 180" and the 172's "180HP"): no picture is better than a wrong one. A number the variant does not have ("1000" of
        // a G1000 version) counts against a set.
        public static string Fuzzy(Dictionary<string, string> index, IEnumerable<string> variantNames)
        {
            List<string> vn = variantNames.Where(n => !string.IsNullOrEmpty(n)).ToList();
            HashSet<string> vt = new HashSet<string>(vn.Select(Norm).SelectMany(Tokens).Concat(vn.SelectMany(Tokens)).Where(t => !Common.Contains(t)), StringComparer.Ordinal);
            HashSet<string> vd = new HashSet<string>(vn.SelectMany(Designations), StringComparer.Ordinal);
            string best = null;
            int bestScore = 0;
            foreach (IGrouping<string, string> folder in index.GroupBy(kv => kv.Value, kv => kv.Key).OrderBy(g => g.Key.Length).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                HashSet<string> ft = new HashSet<string>(folder.SelectMany(Tokens).Concat(Tokens(folder.Key)).Where(t => !Common.Contains(t)), StringComparer.Ordinal);
                // (The avionics' numbers, G1000 / G3000 ..., say nothing about the aircraft.)
                List<string> numbers = ft.Where(t => char.IsDigit(t[0]) && vt.Contains(t) && !Avionics.Contains(t)).ToList();
                int words = ft.Count(t => char.IsLetter(t[0]) && t.Length >= 3 && !Versions.Contains(t) && vt.Contains(t));
                // A type designation both have counts as a shared word ("c172" of "c172-g1000" and "asobo_c172sp_g1000").
                words += folder.Concat(new[] { folder.Key }).SelectMany(Designations).Distinct().Count(d => vd.Contains(d));
                if (words == 0 || !numbers.Any(n => n.Length >= 2))
                    continue;
                int extra = ft.Count(t => char.IsDigit(t[0]) && t.Length >= 3 && !vt.Contains(t)) + ft.Count(t => Versions.Contains(t) && !vt.Contains(t));
                int avionics = ft.Count(t => Avionics.Contains(t) && vt.Contains(t));
                int score = numbers.Count * 10 + words * 2 + avionics * 8 - extra * 3;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = folder.Key;
                }
            }
            return best;
        }

        public static string Norm(string s)
        {
            return Regex.Replace((s ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]", string.Empty);
        }

        // index.json of a game folder: normalised aircraft name -> folder.
        private static Dictionary<string, string> Index(string gameFolder)
        {
            string file = Path.Combine(gameFolder, "index.json");
            lock (Gate)
            {
                DateTime stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
                Tuple<DateTime, Dictionary<string, string>> hit;
                if (Indexes.TryGetValue(file, out hit) && hit.Item1 == stamp)
                    return hit.Item2;
                Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    if (stamp != DateTime.MinValue)
                    {
                        Dictionary<string, object> root = MiniJson.Parse(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                        Dictionary<string, object> aircraft = root == null ? null : DeviceCatalog.Object(root, "aircraft");
                        if (aircraft != null)
                        {
                            foreach (KeyValuePair<string, object> pair in aircraft)
                            {
                                string key = Norm(pair.Key);
                                if (key.Length > 0 && pair.Value is string && !map.ContainsKey(key))
                                    map[key] = (string)pair.Value;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                }
                Indexes[file] = Tuple.Create(stamp, map);
                return map;
            }
        }

        private static CockpitPictureSet Cached(string folder)
        {
            string file = Path.Combine(folder, "pictures.json");
            lock (Gate)
            {
                DateTime stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
                Tuple<DateTime, CockpitPictureSet> hit;
                if (Cache.TryGetValue(folder, out hit) && hit.Item1 == stamp)
                    return hit.Item2;
                CockpitPictureSet set = null;
                try
                {
                    set = stamp == DateTime.MinValue ? null : Load(folder);
                }
                catch (Exception)
                {
                    set = null;
                }
                Cache[folder] = Tuple.Create(stamp, set);
                return set;
            }
        }

        // The seat of a variant in the set: 2 for a back seat (CPG, RIO, WSO, operator) when the set has one.
        public int SeatOf(CockpitVariant v)
        {
            bool second = v != null && (v.SeatOf.Length > 0 || Regex.IsMatch(v.Seat ?? string.Empty, "cpg|rio|wso|operator|gunner|copilot|co-pilot|rear|instructor", RegexOptions.IgnoreCase));
            return second && Views.Any(x => x.Seat == 2) ? 2 : 1;
        }

        public List<CockpitPictureView> ViewsOf(int seat)
        {
            return Views.Where(x => x.Seat == seat).ToList();
        }

        public CockpitPictureView View(string id)
        {
            return Views.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // The view of a grip of the variant: the stick side (stick, cyclic, yoke, side-stick, RHC) or the throttle side.
        public CockpitPictureView Grip(int seat, string device)
        {
            string kind = CockpitCatalog.IsThrottleDevice(device) ? "throttle" : "stick";
            return Views.FirstOrDefault(x => x.Seat == seat && x.Kind == "device" && string.Equals(x.Device, kind, StringComparison.OrdinalIgnoreCase));
        }

        // Which named part of the game's grip each control of the variant is (by the words in the part's name and in the
        // control's id and name: "TRTL-R-HAT-SLEW" is the slew control); parts without a match stay unused.
        public static Dictionary<string, CockpitPictureCallout> MatchGrip(CockpitPictureView grip, IEnumerable<CockpitControl> controls)
        {
            Dictionary<string, CockpitPictureCallout> match = new Dictionary<string, CockpitPictureCallout>(StringComparer.OrdinalIgnoreCase);
            if (grip == null)
                return match;
            HashSet<CockpitPictureCallout> used = new HashSet<CockpitPictureCallout>();
            List<Tuple<CockpitControl, CockpitPictureCallout, int>> pairs = new List<Tuple<CockpitControl, CockpitPictureCallout, int>>();
            foreach (CockpitControl c in controls)
            {
                HashSet<string> cw = Words(c.Id + " " + c.Name + " " + c.Part);
                // What the control is: its id's words and the first words of its name ("A/P DISC" of "A/P DISC / TRIM INT
                // switch"). A part must share one of them: a word further on ("trim") alone does not place it.
                HashSet<string> core = Words(c.Id + " " + string.Join(" ", Regex.Split(c.Name ?? string.Empty, "[^A-Za-z0-9]+").Where(x => x.Length > 0).Take(2)) + " " + c.Part);
                foreach (CockpitPictureCallout p in grip.Callouts.Where(x => x.X >= 0))
                {
                    HashSet<string> pw = Words(p.Id + " " + p.Label);
                    if (!core.Any(pw.Contains))
                        continue;
                    // A general word ("weapon", "fire") counts less than one that names the control ("trigger", "tms").
                    int score = cw.Where(w => pw.Contains(w)).Sum(w => General.Contains(w) ? 1 : 2);
                    if (score > 0)
                        pairs.Add(Tuple.Create(c, p, score));
                }
            }
            foreach (Tuple<CockpitControl, CockpitPictureCallout, int> t in pairs.OrderByDescending(t => t.Item3))
            {
                if (match.ContainsKey(t.Item1.Id) || used.Contains(t.Item2))
                    continue;
                match[t.Item1.Id] = t.Item2;
                used.Add(t.Item2);
            }
            return match;
        }

        private static readonly HashSet<string> General = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "weapon", "group", "select", "selector", "mode", "master", "control", "system", "engine", "release"
        };

        private static readonly Dictionary<string, string> Synonyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "fire", "trigger" }, { "wpr", "weapon" }, { "rls", "release" }, { "steer", "nosewheel" }, { "steering", "nosewheel" }, { "nw", "nosewheel" },
            { "trig", "trigger" }, { "trg", "trigger" }, { "pickle", "release" }, { "wpn", "weapon" }, { "weap", "weapon" }, { "bomb", "release" },
            { "spdbrk", "speedbrake" }, { "speedbrk", "speedbrake" }, { "sb", "speedbrake" }, { "brakes", "speedbrake" }, { "airbrake", "speedbrake" },
            { "cur", "cursor" }, { "jstck", "cursor" }, { "slew", "cursor" }, { "ministick", "cursor" }, { "tdc", "cursor" },
            { "rng", "range" }, { "antn", "antenna" }, { "ant", "antenna" }, { "elev", "antenna" }, { "dgft", "dogfight" },
            { "nws", "nosewheel" }, { "pinky", "pinky" }, { "paddle", "paddle" }, { "mic", "mic" }, { "comm", "mic" }, { "radio", "mic" },
            { "cms", "cms" }, { "tms", "tms" }, { "dms", "dms" }, { "trim", "trim" }, { "coolie", "coolie" }, { "china", "china" }, { "boat", "boat" },
            { "cage", "cage" }, { "uncage", "cage" }, { "mmc", "mmcb" }, { "flaps", "flaps" }, { "flap", "flaps" }, { "gun", "trigger" }
        };

        private static readonly HashSet<string> Noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "stick", "throttle", "trtl", "thr", "btn", "button", "hat", "tmb", "lvr", "lever", "switch", "sw", "dum", "ptr", "pnt", "grip", "left", "right", "l", "r",
            "control", "the", "and", "of", "handle", "knob", "4", "way", "position", "axis", "main", "collective", "cyclic", "hand", "red", "black"
        };

        private static HashSet<string> Words(string s)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in Regex.Split(s ?? string.Empty, "[^A-Za-z0-9]+"))
            {
                string w = Regex.Replace(raw, "[0-9]+$", string.Empty).ToLowerInvariant();
                if (w.Length < 2 || Noise.Contains(w))
                    continue;
                string syn;
                set.Add(Synonyms.TryGetValue(w, out syn) ? syn : w);
            }
            return set;
        }

        private readonly Dictionary<string, DeviceLineArt> drawings = new Dictionary<string, DeviceLineArt>(StringComparer.OrdinalIgnoreCase);

        // JoyPro's line drawing of a view (read once), or null.
        public DeviceLineArt DrawingOf(CockpitPictureView view)
        {
            if (view == null)
                return null;
            lock (drawings)
            {
                DeviceLineArt art;
                if (drawings.TryGetValue(view.Drawing, out art))
                    return art;
                try
                {
                    art = File.Exists(view.Drawing) ? DeviceLineArt.Parse(File.ReadAllText(view.Drawing, Encoding.UTF8), view.Drawing) : null;
                }
                catch (Exception)
                {
                    art = null;
                }
                drawings[view.Drawing] = art;
                return art;
            }
        }

        // ------------------------------------------------------------------ status (Cockpits page, cockpits pictures status)

        public sealed class GameStatus
        {
            public string Game = string.Empty;
            public int Sets;
            // Of them from picture packs (pack.json next to pictures.json).
            public int Packs;
            public string Built = string.Empty;
        }

        public static List<GameStatus> Status(string root = null)
        {
            root = root ?? Root();
            List<GameStatus> list = new List<GameStatus>();
            if (!Directory.Exists(root))
                return list;
            foreach (string dir in Directory.GetDirectories(root))
            {
                List<string> manifests = Directory.GetDirectories(dir).Select(d => Path.Combine(d, "pictures.json")).Where(File.Exists).ToList();
                if (manifests.Count == 0)
                    continue;
                list.Add(new GameStatus
                {
                    Game = Path.GetFileName(dir),
                    Sets = manifests.Count,
                    Packs = manifests.Count(f => File.Exists(Path.Combine(Path.GetDirectoryName(f), PackFileName))),
                    Built = manifests.Max(f => File.GetLastWriteTime(f)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                });
            }
            return list;
        }
    }

    public sealed class CockpitPictureView
    {
        public string Id = string.Empty;
        public string Kind = string.Empty;   // overview, device, panel
        public string Title = string.Empty;
        public string Area = string.Empty;
        public string Device = string.Empty; // stick, throttle (grips)
        public int Seat = 1;
        public string Image = string.Empty;
        // A JPEG picture's transparency: an 8-bit grey PNG of the same size (255 = opaque); empty for none.
        public string Mask = string.Empty;
        public string Drawing = string.Empty;
        public double Width = 1;
        public double Height = 1;
        // The quality gate's score of the picture (0..100, -1 = not rated) and whether JoyPro's line drawing of it is
        // readable (a busy perspective or a poor picture is not: the drawing mode shows the picture then).
        public int Quality = -1;
        public bool DrawingReadable = true;
        public List<CockpitPictureCallout> Callouts = new List<CockpitPictureCallout>();
        public CockpitPictureSet Set;
    }

    // A control of the cockpit (pictures.json "controls"): Kind is JoyPro's (button, switch-2, switch-3, switch-N,
    // rotary, lever, axis, grip, screen, cover, ...), GameKind the game's own; the animation's source (draw argument,
    // dataref, animation argument, animated node, behaviour, interaction point, model node) and name, its range (NaN:
    // not known) and a switch's positions; the commands to bind; where it is on the pictures (N: its number there).
    public sealed class CockpitPictureControl
    {
        public string Id = string.Empty;
        public string Label = string.Empty;
        public string Kind = string.Empty;
        public string GameKind = string.Empty;
        public string Panel = string.Empty;
        public string Device = string.Empty;
        public int Seat = 1;
        public string AnimationSource = string.Empty;
        public string AnimationName = string.Empty;
        public double Min = double.NaN;
        public double Max = double.NaN;
        public List<double> Positions = new List<double>();
        public List<string> Commands = new List<string>();
        public List<CockpitPictureSpot> Views = new List<CockpitPictureSpot>();
    }

    public sealed class CockpitPictureSpot
    {
        public string View = string.Empty;
        public int N;
        public double X = -1;
        public double Y = -1;
    }

    public sealed class CockpitPictureCallout
    {
        public int N;
        public string Id = string.Empty;
        public string Label = string.Empty;
        public double X = -1;
        public double Y = -1;
        public bool Hidden;
        public string Kind = string.Empty;
        public string Device = string.Empty;
        public string Panel = string.Empty;
        public string Link = string.Empty;
        public List<CockpitPictureCommand> Commands = new List<CockpitPictureCommand>();
        // The positions (states) of an animated part where the set knows them ("positions": names, or "states": a count).
        public List<string> Positions = new List<string>();
    }

    public sealed class CockpitPictureCommand
    {
        public string Name = string.Empty;
        public string Category = string.Empty;
        public string Id = string.Empty;
    }
}
