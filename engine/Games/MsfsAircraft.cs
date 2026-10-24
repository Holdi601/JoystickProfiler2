using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace JoyPro.Engine.Games
{
    // One aircraft of MSFS 2020/2024 as JoyPro lists it (DB\MSFS2020\aircraft.tsv and DB\MSFS2024\aircraft.tsv, each with the aircraft of its sim). The key is the aircraft's package
    // name without the "fs20-"/"fs24-" prefix of the 2024 content list: MSFS 2024 keeps its per device x aircraft
    // input record under that name (profile_00: "asobo-aircraft-c172sp-as1000" for the streamed package
    // fs24-asobo-aircraft-c172sp-as1000), and an MSFS 2020 package has the same name. One package can hold several
    // SimObjects (floats, skis): they share the key; their user-selectable titles are the variants.
    internal sealed class MsfsAircraft
    {
        public string Key = string.Empty;
        public string Title = string.Empty;
        // airplane, helicopter, glider, lighter-than-air (from aircraft.cfg [GENERAL] category or the SimObjects folder);
        // empty when the sim keeps the aircraft only in its streamed, encrypted files.
        public string Category = string.Empty;
        public SortedSet<string> Sims = new SortedSet<string>(StringComparer.Ordinal);
        public string IcaoType = string.Empty;
        public string Manufacturer = string.Empty;
        public SortedSet<string> Packages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        public List<string> Variants = new List<string>();
        // Where the title comes from: manifest (package title), aircraft.cfg (FLTSIM title) or package name (derived).
        public string TitleSource = string.Empty;
    }

    // Finds the installed MSFS aircraft, read only:
    //   - package folders with manifest.json content_type AIRCRAFT (liveries are LIVERY): Official\<store>\*,
    //     Official2020\*, Official2024\*, Community, Community2024 of the InstalledPackagesPath in UserCfg.opt, and
    //     the install's Packages folder; aircraft.cfg (2020: SimObjects\<cat>\<obj>\aircraft.cfg, 2024 modular:
    //     ...\common\config\ and ...\presets\<creator>\<preset>\config\) or, for packed packages, the aircraft.cfg
    //     paths in layout.json;
    //   - MSFS 2024 streams its aircraft (StreamedPackages hold only cached, encrypted .fsarchive parts): the content
    //     list <UserCfg folder>\<id>\Content.xml names every package the user has with its state (Activated,
    //     UserDisabled, ...); aircraft packages are the "-aircraft-" ones that are no livery, AI traffic
    //     (passiveaircraft), base or shared ("-common") package. Without a Content.xml the StreamedPackages names are used.
    internal static class MsfsAircraftCatalog
    {
        public const string FileName = "aircraft.tsv";
        private static readonly Regex InstalledPackagesPath = new Regex("^\\s*InstalledPackagesPath\\s+\"(?<path>[^\"]+)\"", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex SimPrefix = new Regex("^fs(20|24)-", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ContentPackage = new Regex("<Package\\s+name=\"(?<name>[^\"]+)\"(?:\\s+active=\"(?<active>[^\"]*)\")?", RegexOptions.Compiled);
        private static readonly string[] NotAircraft = { "-livery", "passiveaircraft", "fs-base-", "simattachmentlib", "trainings-", "efb-app" };

        // DB\<game>\aircraft.tsv to read (game-data folder first, GameDataPaths); Save writes into the game-data folder.
        public static string FilePath(string gameId)
        {
            return GameDataPaths.DbFile(gameId, FileName);
        }

        // The package name as JoyPro's key: without the fs20-/fs24- prefix the 2024 content list adds.
        public static string KeyOf(string packageName)
        {
            return SimPrefix.Replace((packageName ?? string.Empty).Trim(), string.Empty);
        }

        // An aircraft package by its name alone (2024 content list / streamed packages).
        public static bool IsAircraftPackageName(string packageName)
        {
            string name = (packageName ?? string.Empty).Trim().ToLowerInvariant();
            if (name.IndexOf("-aircraft-", StringComparison.Ordinal) < 0 || name.EndsWith("-common", StringComparison.Ordinal))
                return false;
            return !NotAircraft.Any(n => name.IndexOf(n, StringComparison.Ordinal) >= 0);
        }

        // Folders to look in, with the sim they belong to ("" = decide by folder name).
        private sealed class Holder
        {
            public string Folder;
            public string Sim;
            public bool ContentListOnly;
        }

        private static List<Holder> Holders(List<string> warnings)
        {
            List<Holder> holders = new List<Holder>();
            foreach (string gameId in new[] { "MSFS2024", "MSFS2020" })
            {
                GameInstall found = GameInstalls.Find(gameId, warnings);
                if (found != null && found.Installed && !string.IsNullOrEmpty(found.InstallPath))
                    holders.Add(new Holder { Folder = found.InstallPath, Sim = SimOfInstall(found.InstallPath) });
            }
            // JOYPRO_INSTALL_DETECTION=off (tests): only the folder set with "engine games install set".
            if (Environment.GetEnvironmentVariable(GameInstalls.DetectionVariable) == "off")
                return holders.Where(h => Directory.Exists(h.Folder)).ToList();

            foreach (string install in GameInstalls.SteamFolders(new[] { 2537590 }, warnings))
                holders.Add(new Holder { Folder = install, Sim = "2024" });
            foreach (string install in GameInstalls.SteamFolders(new[] { 1250410 }, warnings))
                holders.Add(new Holder { Folder = install, Sim = "2020" });
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (KeyValuePair<string, string> cfg in new[]
            {
                new KeyValuePair<string, string>(Path.Combine(roaming, "Microsoft Flight Simulator 2024", "UserCfg.opt"), "2024"),
                new KeyValuePair<string, string>(Path.Combine(roaming, "Microsoft Flight Simulator", "UserCfg.opt"), "2020"),
                new KeyValuePair<string, string>(Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"), "2024"),
                new KeyValuePair<string, string>(Path.Combine(local, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"), "2020")
            })
            {
                try
                {
                    if (!File.Exists(cfg.Key))
                        continue;
                    holders.Add(new Holder { Folder = Path.GetDirectoryName(cfg.Key), Sim = cfg.Value, ContentListOnly = true });
                    Match m = InstalledPackagesPath.Match(File.ReadAllText(cfg.Key));
                    if (m.Success && Directory.Exists(m.Groups["path"].Value))
                        holders.Add(new Holder { Folder = m.Groups["path"].Value, Sim = cfg.Value });
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return holders.Where(h => Directory.Exists(h.Folder)).ToList();
        }

        private static string SimOfInstall(string folder)
        {
            if (File.Exists(Path.Combine(folder, "FlightSimulator2024.exe")))
                return "2024";
            if (File.Exists(Path.Combine(folder, "FlightSimulator.exe")))
                return "2020";
            return string.Empty;
        }

        // Every installed aircraft of both sims (merged by key). Steps get the sources and counts.
        public static List<MsfsAircraft> Discover(List<string> warnings, List<string> steps)
        {
            Dictionary<string, MsfsAircraft> found = new Dictionary<string, MsfsAircraft>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> scannedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> scannedContentLists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> streamedFolders = new List<string>();
            int disabled = 0;
            foreach (Holder holder in Holders(warnings))
            {
                string folder = Path.GetFullPath(holder.Folder).TrimEnd('\\');
                if (!holder.ContentListOnly)
                {
                    foreach (KeyValuePair<string, string> container in Containers(folder, holder.Sim))
                    {
                        List<string> packages = SafeDirectories(container.Key).ToList();
                        // Several packages are read at once (a package's manifest, aircraft.cfg files and layout.json;
                        // MSFS 2024 has hundreds); the results are merged in folder order as before.
                        bool[] read = packages.Select(p => scannedPackages.Add(Path.GetFullPath(p) + "|" + container.Value)).ToArray();
                        MsfsAircraft[] results = new MsfsAircraft[packages.Count];
                        List<string>[] packageWarnings = new List<string>[packages.Count];
                        int packageIndex = 0;
                        object progressGate = new object();
                        string packageSim = container.Value;
                        string phase = "MSFS " + holder.Sim + ", looking for aircraft";
                        Parallel.For(0, packages.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount)) }, (i, state) =>
                        {
                            if (EngineProgress.CancelRequested)
                            {
                                state.Stop();
                                return;
                            }
                            lock (progressGate)
                                EngineProgress.Report(++packageIndex, packages.Count, phase, Path.GetFileName(packages[i]));
                            if (!read[i])
                                return;
                            packageWarnings[i] = new List<string>();
                            results[i] = ReadPackage(packages[i], packageSim, packageWarnings[i]);
                        });
                        EngineProgress.ThrowIfCancelled("looking for MSFS aircraft");
                        for (int i = 0; i < packages.Count; i++)
                        {
                            if (packageWarnings[i] != null)
                                warnings.AddRange(packageWarnings[i]);
                            if (results[i] != null)
                                MergeInto(found, results[i]);
                        }
                    }
                }

                // MSFS 2024's content list (owned packages, also the streamed ones).
                string sim = holder.Sim == "2020" ? "2020" : "2024";
                List<string> lists = ContentLists(folder);
                foreach (string list in lists)
                {
                    if (!scannedContentLists.Add(Path.GetFullPath(list)))
                        continue;
                    int added = 0;
                    foreach (Match match in ContentPackage.Matches(SafeRead(list)))
                    {
                        string name = match.Groups["name"].Value;
                        if (!IsAircraftPackageName(name))
                            continue;
                        string active = match.Groups["active"].Success ? match.Groups["active"].Value : string.Empty;
                        if (active.Length > 0 && !string.Equals(active, "Activated", StringComparison.OrdinalIgnoreCase))
                        {
                            disabled++;
                            continue;
                        }
                        MergeInto(found, FromPackageName(name, sim));
                        added++;
                    }
                    steps.Add("MSFS content list " + list + ": " + added.ToString(CultureInfo.InvariantCulture) + " aircraft packages");
                }
                string streamed = Path.Combine(folder, "StreamedPackages");
                if (!holder.ContentListOnly && Directory.Exists(streamed))
                    streamedFolders.Add(streamed);
            }
            // The streamed packages only when no content list was found: their cache also keeps packages switched off.
            if (scannedContentLists.Count == 0)
            {
                foreach (string streamed in streamedFolders.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    int added = 0;
                    foreach (string package in SafeDirectories(streamed))
                    {
                        string name = Path.GetFileName(package);
                        if (!IsAircraftPackageName(name))
                            continue;
                        MergeInto(found, FromPackageName(name, "2024"));
                        added++;
                    }
                    steps.Add("MSFS 2024 streamed packages " + streamed + " (no content list found): " + added.ToString(CultureInfo.InvariantCulture) + " aircraft packages");
                }
            }
            if (disabled > 0)
                steps.Add("MSFS aircraft packages switched off in the content list (not listed): " + disabled.ToString(CultureInfo.InvariantCulture));
            return found.Values.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Folders holding package folders, with their sim: Community2024/Official2024/Official2020 belong to 2024 (2024
        // keeps the 2020 content it uses in Official2020), the others to the holder's sim (2020 when unknown).
        private static IEnumerable<KeyValuePair<string, string>> Containers(string folder, string sim)
        {
            string fallback = sim.Length > 0 ? sim : "2020";
            List<KeyValuePair<string, string>> containers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(Path.Combine(folder, "Packages"), fallback),
                new KeyValuePair<string, string>(Path.Combine(folder, "Community"), fallback),
                new KeyValuePair<string, string>(Path.Combine(folder, "Community2024"), "2024")
            };
            foreach (string official in SafeDirectories(folder).Where(d => Path.GetFileName(d).StartsWith("Official", StringComparison.OrdinalIgnoreCase)))
            {
                string name = Path.GetFileName(official);
                string officialSim = name.Equals("Official2024", StringComparison.OrdinalIgnoreCase) || name.Equals("Official2020", StringComparison.OrdinalIgnoreCase) ? "2024" : fallback;
                foreach (string store in SafeDirectories(official))
                    containers.Add(new KeyValuePair<string, string>(store, officialSim));
            }
            return containers.Where(c => Directory.Exists(c.Key));
        }

        private static List<string> ContentLists(string folder)
        {
            List<string> lists = new List<string>();
            string direct = Path.Combine(folder, "Content.xml");
            if (File.Exists(direct))
                lists.Add(direct);
            foreach (string sub in SafeDirectories(folder))
            {
                string file = Path.Combine(sub, "Content.xml");
                if (File.Exists(file))
                    lists.Add(file);
            }
            return lists;
        }

        private static MsfsAircraft FromPackageName(string packageName, string sim)
        {
            MsfsAircraft aircraft = new MsfsAircraft { Key = KeyOf(packageName), TitleSource = "package name" };
            aircraft.Title = TitleFromName(aircraft.Key);
            aircraft.Sims.Add(sim);
            aircraft.Packages.Add(packageName.Trim());
            return aircraft;
        }

        // "microsoft-aircraft-c185f-skywagon" -> "C185F Skywagon" (only when the sim has no readable title).
        public static string TitleFromName(string key)
        {
            string name = key ?? string.Empty;
            int at = name.IndexOf("-aircraft-", StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
                name = name.Substring(at + "-aircraft-".Length);
            List<string> words = new List<string>();
            foreach (string part in name.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
                words.Add(part.Any(char.IsDigit) ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part.Substring(1));
            return words.Count == 0 ? key : string.Join(" ", words);
        }

        private static MsfsAircraft ReadPackage(string package, string sim, List<string> warnings)
        {
            string manifestPath = Path.Combine(package, "manifest.json");
            if (!File.Exists(manifestPath))
                return null;
            Dictionary<string, object> manifest;
            try
            {
                manifest = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(manifestPath)) as Dictionary<string, object>;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException)
            {
                warnings.Add("MSFS package manifest not readable: " + manifestPath + " (" + ex.Message + ")");
                return null;
            }
            if (manifest == null || !string.Equals(Text(manifest, "content_type"), "AIRCRAFT", StringComparison.OrdinalIgnoreCase))
                return null;

            string packageName = Path.GetFileName(package);
            MsfsAircraft aircraft = new MsfsAircraft { Key = KeyOf(packageName) };
            aircraft.Sims.Add(sim);
            aircraft.Packages.Add(packageName);

            // SimObjects\<category folder>\<object>\...\aircraft.cfg, on disk or (packed packages) in layout.json.
            List<string> cfgPaths = new List<string>();
            string simObjects = Path.Combine(package, "SimObjects");
            if (Directory.Exists(simObjects))
            {
                try
                {
                    cfgPaths.AddRange(Directory.GetFiles(simObjects, "aircraft.cfg", SearchOption.AllDirectories)
                        .Select(f => f.Substring(package.Length + 1).Replace('\\', '/')));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            foreach (string path in LayoutPaths(package))
            {
                if (path.StartsWith("SimObjects/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/aircraft.cfg", StringComparison.OrdinalIgnoreCase) &&
                    !cfgPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    cfgPaths.Add(path);
            }

            bool anyAircraft = false;
            string folderCategory = string.Empty;
            foreach (string relative in cfgPaths.OrderBy(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string[] parts = relative.Split('/');
                if (parts.Length < 4)
                    continue;
                string file = Path.Combine(package, relative.Replace('/', '\\'));
                Dictionary<string, Dictionary<string, string>> cfg = File.Exists(file) ? ReadCfg(file) : null;
                if (cfg != null && Value(cfg, "VARIATION", "base_container").Length > 0)
                    continue; // a livery of another SimObject
                anyAircraft = true;
                if (folderCategory.Length == 0)
                    folderCategory = parts[1];
                if (cfg == null)
                    continue;
                if (aircraft.Category.Length == 0)
                    aircraft.Category = NormalizeCategory(Value(cfg, "GENERAL", "category"));
                if (aircraft.IcaoType.Length == 0)
                    aircraft.IcaoType = Value(cfg, "GENERAL", "icao_type_designator");
                if (aircraft.Manufacturer.Length == 0)
                    aircraft.Manufacturer = Value(cfg, "GENERAL", "icao_manufacturer");
                foreach (KeyValuePair<string, Dictionary<string, string>> section in cfg.Where(s => s.Key.StartsWith("FLTSIM.", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(s => SectionIndex(s.Key)))
                {
                    string title;
                    string selectable;
                    if (!section.Value.TryGetValue("title", out title) || title.Length == 0)
                        continue;
                    if (section.Value.TryGetValue("isuserselectable", out selectable) && selectable.Trim() == "0")
                        continue; // AI traffic variant
                    if (!aircraft.Variants.Contains(title, StringComparer.OrdinalIgnoreCase))
                        aircraft.Variants.Add(title);
                }
            }
            if (!anyAircraft)
                return null;
            if (aircraft.Category.Length == 0)
                aircraft.Category = NormalizeCategory(folderCategory);

            string manifestManufacturer = Text(manifest, "manufacturer");
            if (manifestManufacturer.Length > 0)
                aircraft.Manufacturer = manifestManufacturer;
            string manifestTitle = Text(manifest, "title");
            if (manifestTitle.Length > 0)
            {
                aircraft.Title = manifestTitle;
                aircraft.TitleSource = "manifest";
            }
            else if (aircraft.Variants.Count > 0)
            {
                aircraft.Title = aircraft.Variants[0];
                aircraft.TitleSource = "aircraft.cfg";
            }
            else
            {
                aircraft.Title = TitleFromName(aircraft.Key);
                aircraft.TitleSource = "package name";
            }
            return aircraft;
        }

        private static int SectionIndex(string section)
        {
            int index;
            return int.TryParse(section.Substring("FLTSIM.".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out index) ? index : int.MaxValue;
        }

        public static string NormalizeCategory(string raw)
        {
            string value = (raw ?? string.Empty).Trim().Trim('"').ToLowerInvariant();
            if (value.Length == 0)
                return string.Empty;
            if (value.Contains("heli") || value.Contains("rotor"))
                return "helicopter";
            if (value.Contains("glider"))
                return "glider";
            if (value.Contains("balloon") || value.Contains("lighter") || value.Contains("airship") || value.Contains("blimp"))
                return "lighter-than-air";
            if (value.Contains("plane"))
                return "airplane";
            return value;
        }

        private static List<string> LayoutPaths(string package)
        {
            List<string> paths = new List<string>();
            string layout = Path.Combine(package, "layout.json");
            if (!File.Exists(layout))
                return paths;
            try
            {
                Dictionary<string, object> doc = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(layout)) as Dictionary<string, object>;
                object content;
                if (doc == null || !doc.TryGetValue("content", out content) || !(content is object[]))
                    return paths;
                foreach (object item in (object[])content)
                {
                    Dictionary<string, object> entry = item as Dictionary<string, object>;
                    string path = entry == null ? string.Empty : Text(entry, "path");
                    if (path.Length > 0)
                        paths.Add(path.Replace('\\', '/'));
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException)
            {
            }
            return paths;
        }

        // aircraft.cfg: [SECTION] and key = value lines; a quoted value ends at its closing quote, others at ; or //.
        // Section names are upper case, keys lower case.
        private static Dictionary<string, Dictionary<string, string>> ReadCfg(string file)
        {
            Dictionary<string, Dictionary<string, string>> sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> current = null;
            foreach (string raw in SafeRead(file).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line.StartsWith("//", StringComparison.Ordinal))
                    continue;
                if (line[0] == '[')
                {
                    int close = line.IndexOf(']');
                    string name = (close > 0 ? line.Substring(1, close - 1) : line.Substring(1)).Trim().ToUpperInvariant();
                    if (!sections.TryGetValue(name, out current))
                    {
                        current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        sections[name] = current;
                    }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (current == null || eq <= 0)
                    continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string value = line.Substring(eq + 1).Trim();
                if (value.StartsWith("\"", StringComparison.Ordinal))
                {
                    int end = value.IndexOf('"', 1);
                    value = end > 0 ? value.Substring(1, end - 1) : value.Substring(1);
                }
                else
                {
                    int cut = value.IndexOf(';');
                    int slash = value.IndexOf("//", StringComparison.Ordinal);
                    if (slash >= 0 && (cut < 0 || slash < cut))
                        cut = slash;
                    if (cut >= 0)
                        value = value.Substring(0, cut);
                }
                if (!current.ContainsKey(key))
                    current[key] = Clean(value);
            }
            return sections;
        }

        private static string Value(Dictionary<string, Dictionary<string, string>> cfg, string section, string key)
        {
            Dictionary<string, string> values;
            string value;
            return cfg != null && cfg.TryGetValue(section, out values) && values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static string Text(Dictionary<string, object> item, string key)
        {
            object value;
            return item != null && item.TryGetValue(key, out value) && value != null ? Clean(Convert.ToString(value, CultureInfo.InvariantCulture)) : string.Empty;
        }

        // No tabs, line breaks or list separators inside a TSV field.
        private static string Clean(string value)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in (value ?? string.Empty))
                sb.Append(char.IsControl(c) || c == '|' ? ' ' : c);
            return Regex.Replace(sb.ToString(), " {2,}", " ").Trim();
        }

        private static string SafeRead(string file)
        {
            try
            {
                return File.ReadAllText(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return string.Empty;
        }

        private static IEnumerable<string> SafeDirectories(string folder)
        {
            try
            {
                return Directory.Exists(folder) ? Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return new List<string>();
        }

        // Adds an aircraft to the list: sims, packages and variants are combined; a title read from the sim's files
        // replaces one derived from the package name.
        private static void MergeInto(Dictionary<string, MsfsAircraft> list, MsfsAircraft aircraft)
        {
            MsfsAircraft known;
            if (!list.TryGetValue(aircraft.Key, out known))
            {
                list[aircraft.Key] = aircraft;
                return;
            }
            Combine(known, aircraft);
        }

        private static void Combine(MsfsAircraft target, MsfsAircraft source)
        {
            bool targetDerived = target.TitleSource == "package name" || target.Title.Length == 0;
            bool sourceDerived = source.TitleSource == "package name" || source.Title.Length == 0;
            if (source.Title.Length > 0 && (targetDerived || !sourceDerived && source.TitleSource == "manifest" && target.TitleSource != "manifest"))
            {
                target.Title = source.Title;
                target.TitleSource = source.TitleSource;
            }
            if (target.Category.Length == 0)
                target.Category = source.Category;
            if (target.IcaoType.Length == 0)
                target.IcaoType = source.IcaoType;
            if (target.Manufacturer.Length == 0)
                target.Manufacturer = source.Manufacturer;
            target.Sims.UnionWith(source.Sims);
            target.Packages.UnionWith(source.Packages);
            foreach (string variant in source.Variants.Where(v => !target.Variants.Contains(v, StringComparer.OrdinalIgnoreCase)))
                target.Variants.Add(variant);
        }

        // DB\<MSFS2020|MSFS2024>\aircraft.tsv: key, title, category, sims, ICAO type, manufacturer, packages, variants, title source.
        public static List<MsfsAircraft> Load(string path)
        {
            List<MsfsAircraft> list = new List<MsfsAircraft>();
            string file = path ?? string.Empty;
            if (!File.Exists(file))
                return list;
            foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (raw.Length == 0 || raw[0] == '#')
                    continue;
                string[] parts = raw.Split('\t');
                if (parts[0].Trim().Length == 0)
                    continue;
                MsfsAircraft aircraft = new MsfsAircraft
                {
                    Key = parts[0].Trim(),
                    Title = Field(parts, 1),
                    Category = Field(parts, 2),
                    IcaoType = Field(parts, 4),
                    Manufacturer = Field(parts, 5),
                    TitleSource = Field(parts, 8)
                };
                aircraft.Sims.UnionWith(Split(Field(parts, 3), ','));
                aircraft.Packages.UnionWith(Split(Field(parts, 6), '|'));
                aircraft.Variants.AddRange(Split(Field(parts, 7), '|'));
                list.Add(aircraft);
            }
            return list;
        }

        public static List<string> Keys(string gameId)
        {
            return Load(FilePath(gameId)).Select(a => a.Key).ToList();
        }

        private static string Field(string[] parts, int index)
        {
            return parts.Length > index ? parts[index].Trim() : string.Empty;
        }

        private static IEnumerable<string> Split(string value, char separator)
        {
            return value.Split(separator).Select(v => v.Trim()).Where(v => v.Length > 0);
        }

        // The stored list with the found aircraft added or updated; aircraft found before stay (like the DCS planes).
        public static List<MsfsAircraft> Merge(List<MsfsAircraft> stored, List<MsfsAircraft> found)
        {
            Dictionary<string, MsfsAircraft> list = new Dictionary<string, MsfsAircraft>(StringComparer.OrdinalIgnoreCase);
            foreach (MsfsAircraft aircraft in stored)
                list[aircraft.Key] = Copy(aircraft);
            foreach (MsfsAircraft aircraft in found)
            {
                MsfsAircraft known;
                if (!list.TryGetValue(aircraft.Key, out known))
                {
                    list[aircraft.Key] = Copy(aircraft);
                    continue;
                }
                // Fresh data from the sim wins; what it does not say (another sim's title, variants) stays.
                MsfsAircraft fresh = Copy(aircraft);
                if (fresh.TitleSource == "package name" && known.Title.Length > 0 && known.TitleSource != "package name")
                {
                    fresh.Title = known.Title;
                    fresh.TitleSource = known.TitleSource;
                }
                Combine(fresh, known);
                list[aircraft.Key] = fresh;
            }
            return list.Values.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static MsfsAircraft Copy(MsfsAircraft source)
        {
            MsfsAircraft copy = new MsfsAircraft
            {
                Key = source.Key,
                Title = source.Title,
                Category = source.Category,
                IcaoType = source.IcaoType,
                Manufacturer = source.Manufacturer,
                TitleSource = source.TitleSource
            };
            copy.Sims.UnionWith(source.Sims);
            copy.Packages.UnionWith(source.Packages);
            copy.Variants.AddRange(source.Variants);
            return copy;
        }

        public static string Render(List<MsfsAircraft> list, string gameId)
        {
            StringBuilder sb = new StringBuilder();
            string sim = gameId.EndsWith("2020", StringComparison.Ordinal) ? "2020" : "2024";
            sb.Append("# Microsoft Flight Simulator " + sim + " aircraft found by engine " + gameId.ToLowerInvariant() + " refresh-db (read only from the sims' package folders and the 2024 content list; see docs/games/msfs.md).\r\n");
            sb.Append("# key = package name without fs20-/fs24- (MSFS 2024 keeps its device x aircraft input record under it); relations use it as plane: --node " + gameId + ":<action>:<key>.\r\n");
            sb.Append("# key\ttitle\tcategory\tsims\ticao type\tmanufacturer\tpackages\tvariants\ttitle source\r\n");
            foreach (MsfsAircraft aircraft in list)
            {
                sb.Append(aircraft.Key).Append('\t')
                  .Append(aircraft.Title).Append('\t')
                  .Append(aircraft.Category).Append('\t')
                  .Append(string.Join(",", aircraft.Sims)).Append('\t')
                  .Append(aircraft.IcaoType).Append('\t')
                  .Append(aircraft.Manufacturer).Append('\t')
                  .Append(string.Join("|", aircraft.Packages)).Append('\t')
                  .Append(string.Join("|", aircraft.Variants)).Append('\t')
                  .Append(aircraft.TitleSource).Append("\r\n");
            }
            return sb.ToString();
        }

        // Writes the list when it differs from the file; returns whether it wrote.
        public static bool Save(List<MsfsAircraft> list, string gameId)
        {
            string path = FilePath(gameId);
            string text = Render(list, gameId);
            if (File.Exists(path) && string.Equals(File.ReadAllText(path, Encoding.UTF8), text, StringComparison.Ordinal))
                return false;
            File.WriteAllText(GameDataPaths.WritableDbFile(gameId, FileName), text, new UTF8Encoding(false));
            return true;
        }
    }
}
