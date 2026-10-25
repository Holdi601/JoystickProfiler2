using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.DeviceReference;
using JoyPro.History;
using JoyPro.Setup;
using JoyPro.Usage;

namespace JoyPro.Engine
{
    // engine usage ...: the usage summary store, its ranking and statistics (docs/usage-statistics.md).
    //   usage harvest [--game G]... [--full] [--dry-run] [--no-steam]   read what is new into the store
    //   usage sources [--game G]...                                     what JoyPro reads per game, and the live-tail targets
    //   usage ranking [--game G] [--top N]                              most used games and aircraft / ships / vehicles
    //   usage stats [--top N]                                           everything for the Statistics page
    //   usage pin|unpin|exclude|include <game> [<unit>]                 the user's choices on the ranking
    //   usage backups | usage restore <day>                             the store's daily backups
    // The store lives in <data>\Usage (data = JOYPRO_DATA_DIR or %LOCALAPPDATA%\JoyPro); recording always runs and
    // everything stays on this PC. JOYPRO_USAGE_ROOT=<folder> (tests) replaces every place JoyPro reads: <root>\Games\<id>
    // are the installations, <root>\Saved Games, <root>\Documents and <root>\Steam the user's folders.
    // The global option --usage-style off stops Auto Assign, Auto Group, the Setup wizard and Lay out with AI from
    // using the ranking as their style reference.
    internal static partial class Program
    {
        public const string UsageRootVariable = "JOYPRO_USAGE_ROOT";
        internal static bool UsageStyleEnabled = true;

        internal static string UsageFolder()
        {
            return UsageStore.FolderIn(GameInstalls.DataRoot());
        }

        private static int RunUsage(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "stats" : command[0].ToLowerInvariant();
            List<string> args = command.Skip(1).ToList();
            UsageStore store = UsageStore.Open(UsageFolder());
            switch (action)
            {
                case "harvest":
                {
                    UsageHarvestOptions options = new UsageHarvestOptions
                    {
                        Full = PopFlag(args, "--full"),
                        DryRun = PopFlag(args, "--dry-run"),
                        Steam = !PopFlag(args, "--no-steam")
                    };
                    foreach (string game in PopOptionValues(args, "--game"))
                        options.Games.Add(ResolveUsageGame(game));
                    if (args.Count > 0)
                        return Fail("Unknown usage harvest argument(s): " + string.Join(" ", args));
                    UsageHarvestReport report = UsageHarvester.Run(store, new EngineUsageLocator(), options);
                    JoyPro.Logging.JoyProLog.Info("Usage", "Harvest: " + report.FilesRead + " file(s) read, " + report.FilesSkipped + " known, " + report.SessionsNew + " new session(s), " +
                                                           report.SessionsUpdated + " updated, " + report.LiveCopiesRead + " live copies in " + report.Seconds.ToString(CultureInfo.InvariantCulture) + " s");
                    EmitPayload(textOutput ? HarvestText(report) : Json(UsageHarvester.ReportObject(report)), outPath);
                    return 0;
                }
                case "sources":
                {
                    List<string> games = PopOptionValues(args, "--game").Select(ResolveUsageGame).ToList();
                    if (args.Count > 0)
                        return Fail("Unknown usage sources argument(s): " + string.Join(" ", args));
                    return EmitUsageSources(store, games, outPath, textOutput);
                }
                case "ranking":
                {
                    string game = PopOptionValue(args, "--game");
                    int top = ParseTop(PopOptionValue(args, "--top"), 25);
                    if (args.Count > 0)
                        return Fail("Unknown usage ranking argument(s): " + string.Join(" ", args));
                    UsageAggregate aggregate;
                    UsageRanking ranking = BuildUsageRanking(store, true, out aggregate, readableNames: true);
                    string only = string.IsNullOrWhiteSpace(game) ? null : ResolveUsageGame(game);
                    if (textOutput)
                    {
                        if (only != null)
                        {
                            ranking.Units = ranking.Units.Where(u => string.Equals(u.Game, only, StringComparison.OrdinalIgnoreCase)).ToList();
                            ranking.Games = ranking.Games.Where(g => string.Equals(g.Game, only, StringComparison.OrdinalIgnoreCase)).ToList();
                        }
                        EmitPayload(ranking.ToText(UsageGameName, top), outPath);
                        return 0;
                    }
                    int rank = 0, gameRank = 0;
                    Dictionary<string, object> refs = ranking.References().ToDictionary(r => r.Key, r => (object)r.Value.Id);
                    Dictionary<string, object> root = new Dictionary<string, object>
                    {
                        { "Folder", store.Folder },
                        { "Games", ranking.Games.Where(g => only == null || string.Equals(g.Game, only, StringComparison.OrdinalIgnoreCase)).Take(top).Select(g => (object)UsageRanking.GameObject(g, ++gameRank)).ToList() },
                        { "Units", ranking.Units.Where(u => only == null || string.Equals(u.Game, only, StringComparison.OrdinalIgnoreCase)).Take(top).Select(u => (object)UsageRanking.UnitObject(u, ++rank, UsageGameName)).ToList() },
                        { "References", refs },
                        { "Pinned", store.Overrides.Pinned.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Cast<object>().ToList() },
                        { "Excluded", store.Overrides.Excluded.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Cast<object>().ToList() }
                    };
                    EmitPayload(Json(root), outPath);
                    return 0;
                }
                case "stats":
                case "statistics":
                {
                    int top = ParseTop(PopOptionValue(args, "--top"), 50);
                    if (args.Count > 0)
                        return Fail("Unknown usage stats argument(s): " + string.Join(" ", args));
                    UsageAggregate aggregate;
                    UsageRanking ranking = BuildUsageRanking(store, true, out aggregate, readableNames: true);
                    UsageProfileFacts facts = UsageProfileFactsOf(ranking);
                    Dictionary<string, object> stats = UsageStats.Build(store, aggregate, ranking, facts, UsageGameName, DateTime.UtcNow, top);
                    EmitPayload(textOutput ? UsageStats.ToText(stats) : Json(stats), outPath);
                    return 0;
                }
                case "pin":
                case "unpin":
                case "exclude":
                case "include":
                {
                    if (args.Count < 1 || args.Count > 2)
                        return Fail("usage " + action + " <game> [<unit>]");
                    string game = ResolveUsageGame(args[0]);
                    string id = UsageOverrides.Id(game, args.Count > 1 ? args[1] : string.Empty);
                    if (action == "pin")
                    {
                        store.Overrides.Pinned.Add(id);
                        store.Overrides.Excluded.Remove(id);
                    }
                    else if (action == "unpin")
                        store.Overrides.Pinned.Remove(id);
                    else if (action == "exclude")
                    {
                        store.Overrides.Excluded.Add(id);
                        store.Overrides.Pinned.Remove(id);
                    }
                    else
                        store.Overrides.Excluded.Remove(id);
                    store.SaveOverrides();
                    EmitPayload(textOutput ? (action == "pin" ? "Pinned " : action == "unpin" ? "Unpinned " : action == "exclude" ? "Excluded " : "Included ") + id
                                    : Json(new Dictionary<string, object> { { "Action", action }, { "Id", id }, { "Pinned", store.Overrides.Pinned.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Cast<object>().ToList() }, { "Excluded", store.Overrides.Excluded.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Cast<object>().ToList() } }),
                                outPath);
                    return 0;
                }
                case "backups":
                    EmitPayload(textOutput ? "Usage store backups (newest first):" + string.Concat(store.BackupDaysList().Select(d => Environment.NewLine + "  " + d))
                                    : Json(new Dictionary<string, object> { { "Folder", store.BackupFolder }, { "Days", store.BackupDaysList().Cast<object>().ToList() } }), outPath);
                    return 0;
                case "restore":
                    if (args.Count != 1)
                        return Fail("usage restore <day> (engine usage backups lists them)");
                    if (!store.Restore(args[0]))
                        return Fail("No usage backup of " + args[0] + ".");
                    EmitPayload(textOutput ? "Restored the usage store of " + args[0] + "." : Json(new Dictionary<string, object> { { "Restored", args[0] }, { "Sessions", store.Sessions.Count } }), outPath);
                    return 0;
                default:
                    return Fail("Unknown usage action: " + action + ". Use harvest, sources, ranking, stats, pin, unpin, exclude, include, backups or restore.");
            }
        }

        private static string Json(object value)
        {
            return EngineSerialization.AsciiOnly(MiniJson.Write(value, false));
        }

        private static int ParseTop(string value, int fallback)
        {
            int top;
            return !string.IsNullOrWhiteSpace(value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out top) && top > 0 ? top : fallback;
        }

        private static string ResolveUsageGame(string game)
        {
            string resolved = GameInstalls.Resolve(game);
            return resolved.Length > 0 ? resolved : (game ?? string.Empty).Trim();
        }

        private static string UsageGameName(string game)
        {
            if (string.IsNullOrEmpty(game))
                return string.Empty;
            string name = GameInstallsName(game);
            return name.Length > 0 ? name : game;
        }

        private static readonly Dictionary<string, string> GameNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string GameInstallsName(string game)
        {
            string name;
            if (GameNames.TryGetValue(game, out name))
                return name;
            try
            {
                name = GameInstalls.Find(game, new List<string>()).Name ?? string.Empty;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                name = string.Empty;
            }
            GameNames[game] = name;
            return name;
        }

        private static string HarvestText(UsageHarvestReport report)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Usage harvest" + (report.DryRun ? " (dry run, nothing written)" : string.Empty) + ": " + report.FilesRead + " file(s) read, " + report.FilesSkipped +
                          " already known, " + report.SessionsNew + " new session(s), " + report.SessionsUpdated + " updated, " + report.LiveCopiesRead + " live copies (" +
                          report.Seconds.ToString(CultureInfo.InvariantCulture) + " s)");
            foreach (IGrouping<string, UsageHarvestSource> game in report.Sources.GroupBy(s => s.Game.Length == 0 ? "(any game)" : s.Game).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(game.Key == "(any game)" ? game.Key : UsageGameName(game.Key) + " (" + game.Key + ")");
                foreach (UsageHarvestSource s in game)
                {
                    string what = s.Kind == UsageSourceKinds.SteamPlaytime
                        ? (s.Totals > 0 ? UsageText.Hours(s.TotalHours) + " on Steam" : "nothing on Steam")
                        : !s.Exists && s.Files == 0
                            ? "not there"
                            : s.Files + " file(s): " + s.Sessions + " session(s), " + UsageText.Hours(s.Hours) + (s.Units > 0 ? ", " + s.Units + " aircraft/ship(s)" : string.Empty) +
                              (s.Totals > 0 ? ", totals " + UsageText.Hours(s.TotalHours) : string.Empty);
                    sb.AppendLine("  " + s.Title + ": " + what + (s.LiveTail ? " [followed live]" : string.Empty));
                    foreach (string note in s.Notes.Take(3))
                        sb.AppendLine("    " + note);
                }
            }
            foreach (string note in report.Notes)
                sb.AppendLine("Note: " + note);
            return sb.ToString().TrimEnd();
        }

        private static int EmitUsageSources(UsageStore store, List<string> games, string outPath, bool textOutput)
        {
            EngineUsageLocator locator = new EngineUsageLocator();
            List<UsageSourceSpec> specs = UsageSources.Discover(locator, games);
            Dictionary<string, List<string>> processes = EngineSync.AllGames().ToDictionary(g => g.Id, g => g.ProcessNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Usage sources (store: " + store.Folder + ")");
                foreach (UsageSourceSpec s in specs)
                    sb.AppendLine("  " + (s.Game.Length == 0 ? "(any game)" : s.Game) + ": " + UsageSourceKinds.Title(s.Kind) + (s.Directory.Length > 0 ? " - " + Path.Combine(s.Directory, s.Pattern) : string.Empty) +
                                  (s.Directory.Length > 0 ? (Directory.Exists(s.Directory) ? string.Empty : " (not there)") : string.Empty) + (s.LiveTail ? " [followed live]" : string.Empty) +
                                  (s.Note.Length > 0 ? " - " + s.Note : string.Empty));
                foreach (string vdf in UsageSources.SteamConfigs(locator))
                    sb.AppendLine("  Steam playtime: " + vdf);
                EmitPayload(sb.ToString().TrimEnd(), outPath);
                return 0;
            }
            Dictionary<string, object> root = new Dictionary<string, object>
            {
                { "Folder", store.Folder },
                { "LiveFolder", store.LiveFolder },
                { "Sources", specs.Select(s => (object)new Dictionary<string, object>
                    {
                        { "Game", s.Game }, { "Kind", s.Kind }, { "Title", UsageSourceKinds.Title(s.Kind) }, { "Directory", s.Directory }, { "Pattern", s.Pattern },
                        { "Recursive", s.Recursive }, { "LiveTail", s.LiveTail }, { "Exists", s.Directory.Length > 0 && Directory.Exists(s.Directory) }, { "Note", s.Note }
                    }).ToList() },
                // What the watcher follows while a game runs: the folder, the file filter, the game's processes.
                { "Tail", specs.Where(s => s.LiveTail && s.Directory.Length > 0 && s.Game.Length > 0).Select(s => (object)new Dictionary<string, object>
                    {
                        { "Game", s.Game }, { "Kind", s.Kind }, { "Directory", s.Directory }, { "Filter", s.Pattern },
                        { "ProcessNames", (processes.ContainsKey(s.Game) ? processes[s.Game] : new List<string>()).Cast<object>().ToList() }
                    }).ToList() },
                { "Steam", UsageSources.SteamConfigs(locator).Cast<object>().ToList() }
            };
            EmitPayload(Json(root), outPath);
            return 0;
        }

        // The ranking of the store; with the open profile's binds as the weak fallback when bindDensity is set.
        internal static UsageRanking BuildUsageRanking(UsageStore store, bool bindDensity, out UsageAggregate aggregate, bool readableNames = false)
        {
            aggregate = UsageAggregate.Build(store.Sessions.Values, store.Totals.Values, UsageGameName, UsageClassOf);
            UsageRanking ranking = UsageRanking.Build(aggregate, store.Overrides, bindDensity ? UsageBindCounts() : null, UsageClassOf);
            if (readableNames)
            {
                // No raw ids in the UI: every unit and flight gets the name a person reads ("F-16C_50" -> the cockpit
                // database's "F-16C Block 50/52", else DCS's own module name, else the id made readable).
                foreach (UsageUnitStat unit in ranking.Units)
                    unit.Display = UsageUnitName(unit.Game, unit.Unit, unit.Display);
                foreach (UsageFlight flight in aggregate.Flights)
                    flight.Display = UsageUnitName(flight.Game, flight.Unit, flight.Display);
            }
            return ranking;
        }

        // ---- readable unit names ----------------------------------------------------------------------------------

        private static Dictionary<string, string> dcsModuleNames;
        private static Dictionary<string, string> cockpitUnitNames;

        // The name a person reads for an aircraft / ship / vehicle id of a game's log. A display name the log brought
        // (Star Citizen "RSI Polaris", Elite "Python Mk II") stays; ids get the cockpit database's variant name, DCS's own
        // module name (entry.lua displayName) or the id made readable ("Cessna_172SP" -> "Cessna 172SP").
        internal static string UsageUnitName(string game, string unit, string display)
        {
            string shown = string.IsNullOrWhiteSpace(display) ? unit ?? string.Empty : display;
            bool raw = string.Equals(shown, unit, StringComparison.OrdinalIgnoreCase) || shown.IndexOf('_') >= 0;
            if (!raw || string.IsNullOrWhiteSpace(unit))
                return shown;
            string name;
            Dictionary<string, string> cockpit = CockpitUnitNames();
            if (cockpit.TryGetValue(game + "|" + unit, out name))
                return name;
            if (EngineAutoGroup.IsDcs(game))
            {
                Dictionary<string, string> dcs = DcsModuleNames();
                string plane = dcs.ContainsKey(unit) ? unit : UsageCatalogPlane(unit, dcs.Keys);
                if (plane.Length > 0 && cockpit.TryGetValue(game + "|" + plane, out name))
                    return name;
                if (plane.Length > 0 && dcs.TryGetValue(plane, out name) && name.Length > 0)
                    return name;
            }
            return ReadableUnitId(shown);
        }

        // "FA-18C_hornet" -> "FA-18C Hornet", "Cessna_172SP" -> "Cessna 172SP".
        internal static string ReadableUnitId(string id)
        {
            string[] words = Regex.Split((id ?? string.Empty).Replace('_', ' ').Trim(), @"\s+");
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                if (w.Length > 2 && w.All(char.IsLower))
                    words[i] = char.ToUpperInvariant(w[0]) + w.Substring(1);
            }
            return string.Join(" ", words.Where(w => w.Length > 0));
        }

        private static Dictionary<string, string> CockpitUnitNames()
        {
            if (cockpitUnitNames != null)
                return cockpitUnitNames;
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                CockpitCatalog catalog = LoadCockpitCatalog();
                foreach (CockpitAircraft aircraft in catalog.Aircraft)
                {
                    foreach (CockpitVariant variant in aircraft.Variants)
                    {
                        foreach (CockpitGameRef reference in variant.Games.Where(g => !string.IsNullOrWhiteSpace(g.Plane)))
                        {
                            string game = EngineAutoGroup.IsDcs(reference.Game) ? "DCS" : reference.Game;
                            string key = game + "|" + reference.Plane;
                            if (!names.ContainsKey(key) && !string.IsNullOrWhiteSpace(variant.Name))
                                names[key] = variant.Name;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
            {
                JoyPro.Logging.JoyProLog.Debug("Usage", "Cockpit names not read: " + ex.Message);
            }
            cockpitUnitNames = names;
            return names;
        }

        // DCS's module names of this PC (entry.lua displayName per input profile and flyable type); none in the tests
        // (JOYPRO_USAGE_ROOT), so their names do not depend on the PC.
        private static Dictionary<string, string> DcsModuleNames()
        {
            if (dcsModuleNames != null)
                return dcsModuleNames;
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UsageRootVariable)))
            {
                try
                {
                    foreach (EngineMediaAircraft a in EngineMedia.Aircraft(new List<string>()))
                        if (!names.ContainsKey(a.Plane) && !string.IsNullOrWhiteSpace(a.DisplayName))
                            names[a.Plane] = a.DisplayName;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    JoyPro.Logging.JoyProLog.Debug("Usage", "DCS module names not read: " + ex.Message);
                }
            }
            dcsModuleNames = names;
            return names;
        }

        // Binds of the open profile per DCS aircraft ("DCS|F-16C_50" -> count): a relation's bind counts for every
        // aircraft it is active for.
        private static Dictionary<string, int> UsageBindCounts()
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> pair in InternalDataManagement.AllRelations)
            {
                Bind bind;
                if (pair.Value == null || pair.Value.NODES == null || !InternalDataManagement.AllBinds.TryGetValue(pair.Key, out bind) || bind == null ||
                    string.IsNullOrWhiteSpace(bind.Joystick) || string.IsNullOrWhiteSpace(pair.Value.ISAXIS ? bind.JAxis : bind.JButton))
                    continue;
                foreach (string plane in pair.Value.NODES.Where(n => n != null && EngineAutoGroup.IsDcs(string.IsNullOrWhiteSpace(n.Game) ? "DCS" : n.Game) && n.AIRCRAFT != null)
                             .SelectMany(n => n.AIRCRAFT.Where(a => a.Value).Select(a => a.Key)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (UsageAircraftType(plane) == "layer")
                        continue;
                    int n;
                    counts.TryGetValue("DCS|" + plane, out n);
                    counts["DCS|" + plane] = n + 1;
                }
            }
            return counts;
        }

        // ---- aircraft classes (DB\Functions\aircraft.json) --------------------------------------------------------------

        private static Dictionary<string, string> aircraftTypes;
        private static DateTime aircraftTypesStamp;

        private static Dictionary<string, string> AircraftTypes()
        {
            string file = Path.Combine(EngineNlGroup.FunctionsFolder(), "aircraft.json");
            DateTime stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            if (aircraftTypes != null && stamp == aircraftTypesStamp)
                return aircraftTypes;
            Dictionary<string, string> types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Dictionary<string, object> root = File.Exists(file) ? MiniJson.Parse(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object> : null;
                foreach (KeyValuePair<string, object> pair in root ?? new Dictionary<string, object>())
                {
                    Dictionary<string, object> entry = pair.Value as Dictionary<string, object>;
                    object type;
                    if (entry != null && entry.TryGetValue("type", out type) && type is string)
                        types[pair.Key] = (string)type;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is InvalidOperationException)
            {
                JoyPro.Logging.JoyProLog.Debug("Usage", "aircraft.json unreadable: " + ex.Message);
            }
            aircraftTypes = types;
            aircraftTypesStamp = stamp;
            return types;
        }

        private static string UsageAircraftType(string plane)
        {
            string type;
            return AircraftTypes().TryGetValue(plane ?? string.Empty, out type) ? type : string.Empty;
        }

        // A profile aircraft for a unit name of a log ("AH-64D_BLK_II" -> "AH-64D_BLK_II_PLT"), empty when none fits.
        internal static string UsageCatalogPlane(string unit, IEnumerable<string> planes)
        {
            string squash = UsageAggregate.Squash(unit);
            if (squash.Length == 0)
                return string.Empty;
            List<string> list = planes.ToList();
            string exact = list.FirstOrDefault(p => UsageAggregate.Squash(p) == squash);
            if (exact != null)
                return exact;
            return list.Where(p => UsageAggregate.Squash(p).StartsWith(squash, StringComparison.Ordinal) || squash.StartsWith(UsageAggregate.Squash(p), StringComparison.Ordinal))
                       .OrderBy(p => Math.Abs(UsageAggregate.Squash(p).Length - squash.Length)).FirstOrDefault() ?? string.Empty;
        }

        internal static string UsageClassOf(string game, string unit)
        {
            switch (game)
            {
                case "StarCitizen":
                case "EliteDangerous":
                case "X4":
                    return "spaceship";
                case "WarThunder":
                    return string.Empty;
            }
            Dictionary<string, string> types = AircraftTypes();
            string plane = UsageCatalogPlane(unit, types.Keys);
            string type;
            if (plane.Length > 0 && types.TryGetValue(plane, out type) && type != "layer")
                return type;
            string u = (unit ?? string.Empty).ToLowerInvariant();
            if (Regex.IsMatch(u, @"(^|[^a-z])(uh|ah|mi|ka|ch|oh|sa|bell|r22|r44|ec|h1|heli)"))
                return "rotary_wing";
            return game == "DCS" || game.StartsWith("IL2", StringComparison.OrdinalIgnoreCase) || game == "FalconBMS" || game.StartsWith("XPlane", StringComparison.OrdinalIgnoreCase) ||
                   game.StartsWith("MSFS", StringComparison.OrdinalIgnoreCase)
                ? "fixed_wing"
                : string.Empty;
        }

        // ---- style reference ---------------------------------------------------------------------------------------------

        // For every DCS aircraft of the catalog list that the user has not flown (yet): the most used aircraft of its class
        // that the profile has (plane -> reference plane). Empty when nothing was recorded or --usage-style off.
        internal static Dictionary<string, string> UsageStyleReferences(IEnumerable<string> catalogPlanes, Func<string, string> typeOf)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!UsageStyleEnabled)
                return map;
            UsageStore store;
            try
            {
                store = UsageStore.Open(UsageFolder());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return map;
            }
            if (store.Sessions.Count == 0 && store.Totals.Count == 0 && store.Overrides.Pinned.Count == 0)
                return map;
            UsageAggregate aggregate;
            UsageRanking ranking = BuildUsageRanking(store, false, out aggregate);
            Dictionary<string, UsageUnitStat> refs = ranking.References();
            List<string> planes = catalogPlanes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string plane in planes)
            {
                string type = typeOf(plane) ?? string.Empty;
                if (type.Length == 0 || type == "layer" || type == "ground")
                    continue;
                if (!ranking.IsUnused("DCS", plane) || !ranking.IsUnused("DCS", UsageCatalogPlane(plane, ranking.Units.Where(u => u.Game == "DCS").Select(u => u.Unit))))
                    continue;
                UsageUnitStat reference;
                if (!refs.TryGetValue(UsageRanking.ReferenceKey("DCS", type), out reference))
                    continue;
                string refPlane = UsageCatalogPlane(reference.Unit, planes);
                if (refPlane.Length > 0 && !string.Equals(refPlane, plane, StringComparison.OrdinalIgnoreCase))
                    map[plane] = refPlane;
            }
            return map;
        }

        // Setup wizard and Lay out with AI: every function the reference aircraft of a class has on a control of this
        // setup gets a bonus on that control for the whole class, so unbound relations of new aircraft land where the
        // user's most used aircraft has them (bound relations keep their binds anyway).
        internal static void AddUsageStyleRules(SetupRequest request)
        {
            if (!UsageStyleEnabled || request == null || request.Existing.Count == 0)
                return;
            Dictionary<string, string> refs = UsageStyleReferences(request.Aircraft.Values.Where(a => EngineAutoGroup.IsDcs(a.Game)).Select(a => a.Key), p =>
            {
                SetupAircraft a;
                return request.Aircraft.TryGetValue(p, out a) ? a.Type : string.Empty;
            });
            if (refs.Count == 0)
                return;
            int added = 0;
            foreach (string refPlane in refs.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                SetupAircraft reference;
                if (!request.Aircraft.TryGetValue(refPlane, out reference))
                    continue;
                foreach (SetupExistingRelation existing in request.Existing.Where(e => e.Function.Length > 0 && e.Device.Length > 0 && e.OnSetup &&
                                                                                       e.Nodes.Any(n => string.Equals(n.Aircraft, refPlane, StringComparison.OrdinalIgnoreCase))))
                {
                    SetupInput input = request.Inputs.FirstOrDefault(i => i.Device == existing.Device && string.Equals(i.Input, existing.Input, StringComparison.OrdinalIgnoreCase));
                    if (input == null || string.IsNullOrEmpty(input.Control))
                        continue;
                    string id = "usage-style:" + refPlane + ":" + existing.Function;
                    if (request.Options.Rules.Any(r => r.Id == id))
                        continue;
                    request.Options.Rules.Add(new SetupRule
                    {
                        Id = id,
                        Functions = "^" + Regex.Escape(existing.Function) + "$",
                        Class = reference.Class,
                        Controls = "^" + Regex.Escape(input.Control) + "$",
                        Bonus = 2.0
                    });
                    added++;
                }
            }
            if (added > 0)
                request.Notes.Add("New aircraft follow the layout of your most used one (" + string.Join(", ", refs.Values.Distinct(StringComparer.OrdinalIgnoreCase)) + "): " + added + " preference(s) from the usage ranking.");
        }

        // ---- the profile's part of the statistics ------------------------------------------------------------------

        private static UsageProfileFacts UsageProfileFactsOf(UsageRanking ranking)
        {
            UsageProfileFacts facts = new UsageProfileFacts();
            if (InternalDataManagement.AllBinds == null || InternalDataManagement.AllBinds.Count == 0)
                return facts;
            facts.ProfileName = string.IsNullOrWhiteSpace(EngineProfileHistory.ProfilePath) ? string.Empty : Path.GetFileNameWithoutExtension(EngineProfileHistory.ProfilePath);
            Dictionary<string, int[]> devices = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Bind> pair in InternalDataManagement.AllBinds)
            {
                Bind bind = pair.Value;
                if (bind == null || string.IsNullOrWhiteSpace(bind.Joystick))
                    continue;
                bool axis = !string.IsNullOrWhiteSpace(bind.JAxis);
                if (!axis && string.IsNullOrWhiteSpace(bind.JButton))
                    continue;
                string device = UsageDeviceLabel(bind.Joystick);
                int[] c;
                if (!devices.TryGetValue(device, out c))
                    devices[device] = c = new int[2];
                c[axis ? 0 : 1]++;
            }
            facts.Devices = devices.OrderByDescending(d => d.Value[0] + d.Value[1]).ThenBy(d => d.Key, StringComparer.OrdinalIgnoreCase)
                .Select(d => new Dictionary<string, object> { { "Device", d.Key }, { "Binds", d.Value[0] + d.Value[1] }, { "Axes", d.Value[0] }, { "Buttons", d.Value[1] } }).ToList();
            try
            {
                UsageHistoryFacts(facts, ranking);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is FormatException)
            {
                JoyPro.Logging.JoyProLog.Debug("Usage", "Profile history not read for the statistics: " + ex.Message);
            }
            return facts;
        }

        private static string UsageDeviceLabel(string device)
        {
            string text = (device ?? string.Empty).Trim();
            int brace = text.IndexOf('{');
            return brace > 0 ? text.Substring(0, brace).Trim() : text;
        }

        // Growth (relations and binds over time, up to 40 versions spread over the history) and the oldest bind whose
        // aircraft or game has no recorded use. The counts per version are cached in <data>\Usage\Cache.
        private static void UsageHistoryFacts(UsageProfileFacts facts, UsageRanking ranking)
        {
            string profile = EngineProfileHistory.ProfilePath;
            if (string.IsNullOrWhiteSpace(profile))
                return;
            ProfileHistoryLog log = ProfileHistory.Read(profile);
            if (log == null || log.Versions.Count == 0)
                return;
            List<ProfileVersion> versions = log.Versions.Where(v => !string.IsNullOrEmpty(v.Hash)).OrderBy(v => v.Utc).ToList();
            List<ProfileVersion> samples = new List<ProfileVersion>();
            const int MaxSamples = 40;
            for (int i = 0; i < MaxSamples && versions.Count > 0; i++)
            {
                ProfileVersion v = versions[(int)Math.Round((double)i * (versions.Count - 1) / Math.Max(1, MaxSamples - 1))];
                if (!samples.Contains(v))
                    samples.Add(v);
            }
            string cacheFile = Path.Combine(UsageFolder(), "Cache", "growth-" + ProfileHistory.IdFor(profile) + ".json");
            Dictionary<string, object> cache = File.Exists(cacheFile) ? UsageStoreParse(File.ReadAllText(cacheFile, Encoding.UTF8)) : null;
            Dictionary<string, object> cached = cache ?? new Dictionary<string, object>();
            bool changed = false;
            Dictionary<string, HashSet<string>> bindSets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (ProfileVersion v in samples)
            {
                object hit;
                Dictionary<string, object> entry = cached.TryGetValue(v.Hash, out hit) ? hit as Dictionary<string, object> : null;
                IList<string> items = null;
                if (entry == null || !entry.ContainsKey("b"))
                {
                    items = ProfileHistory.ReadItemsOf(log.Folder, v.Hash);
                    if (items == null)
                        continue;
                    List<string> binds = items.Where(i => i.StartsWith("B\t", StringComparison.Ordinal)).Select(BindKeyOfItem).Where(k => k.Length > 0).ToList();
                    entry = new Dictionary<string, object>
                    {
                        { "r", items.Count(i => i.StartsWith("R\t", StringComparison.Ordinal)) },
                        { "n", binds.Count },
                        { "b", binds.Cast<object>().ToList() }
                    };
                    cached[v.Hash] = entry;
                    changed = true;
                }
                facts.Growth.Add(new Dictionary<string, object> { { "Utc", UsageText.Iso(v.Utc) }, { "Relations", UsageJson.Number(entry, "r") }, { "Binds", UsageJson.Number(entry, "n") } });
                bindSets[v.Hash] = new HashSet<string>(UsageJson.List(entry, "b").OfType<string>(), StringComparer.Ordinal);
            }
            if (changed)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
                    File.WriteAllText(cacheFile, MiniJson.Write(cached, false), new UTF8Encoding(false));
                }
                catch (IOException)
                {
                }
            }

            // The oldest bind: the current binds, the first sample each one is in.
            if (samples.Count == 0)
                return;
            HashSet<string> current = bindSets.ContainsKey(samples[samples.Count - 1].Hash) ? bindSets[samples[samples.Count - 1].Hash] : new HashSet<string>();
            foreach (ProfileVersion v in samples)
            {
                HashSet<string> set;
                if (!bindSets.TryGetValue(v.Hash, out set))
                    continue;
                foreach (string key in set.Where(current.Contains).OrderBy(k => k, StringComparer.Ordinal))
                {
                    string relationName = key.Substring(0, key.IndexOf('\t'));
                    Relation relation;
                    Bind bind;
                    if (!InternalDataManagement.AllRelations.TryGetValue(relationName, out relation) || relation == null || !InternalDataManagement.AllBinds.TryGetValue(relationName, out bind) || bind == null)
                        continue;
                    if (!RelationUnused(relation, ranking))
                        continue;
                    facts.OldestUnusedBind = new Dictionary<string, object>
                    {
                        { "Relation", relationName },
                        { "Device", UsageDeviceLabel(bind.Joystick) },
                        { "Input", relation.ISAXIS ? bind.JAxis ?? string.Empty : bind.JButton ?? string.Empty },
                        { "Since", UsageText.Iso(v.Utc) },
                        { "SinceFirstVersion", ReferenceEquals(v, samples[0]) }
                    };
                    return;
                }
            }
        }

        private static string BindKeyOfItem(string item)
        {
            string[] parts = item.Split('\t');
            return parts.Length > 4 && parts[4].Length > 0 && parts[3].Trim().Length > 0 ? parts[1] + "\t" + parts[4] : string.Empty;
        }

        // A relation none of whose aircraft (DCS) or games has recorded use.
        private static bool RelationUnused(Relation relation, UsageRanking ranking)
        {
            foreach (RelationItem node in (relation.NODES ?? new List<RelationItem>()).Where(n => n != null))
            {
                string game = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game;
                if (EngineAutoGroup.IsDcs(game))
                {
                    foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT ?? new Dictionary<string, bool>())
                        if (plane.Value && !ranking.IsUnused("DCS", plane.Key))
                            return false;
                }
                else
                {
                    string id = ResolveUsageGame(game);
                    if (ranking.Games.Any(g => string.Equals(g.Game, id, StringComparison.OrdinalIgnoreCase) && g.Hours >= 0.5))
                        return false;
                }
            }
            return true;
        }

        private static Dictionary<string, object> UsageStoreParse(string json)
        {
            try
            {
                return MiniJson.Parse(json) as Dictionary<string, object>;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // After every successful export (EngineSync.RecordAfterExport): one line in the store's export log.
        internal static void RecordUsageExport(string game)
        {
            try
            {
                new UsageStore(UsageFolder()).RecordExport(game, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                JoyPro.Logging.JoyProLog.Debug("Usage", "Export of " + game + " not counted: " + ex.Message);
            }
        }
    }

    // Where the games keep what the usage harvest reads, from JoyPro's install detection (or JOYPRO_USAGE_ROOT).
    internal sealed class EngineUsageLocator : IUsageLocator
    {
        private readonly string root = Environment.GetEnvironmentVariable(Program.UsageRootVariable);
        private readonly Dictionary<string, string> installs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private bool Test
        {
            get { return !string.IsNullOrWhiteSpace(root); }
        }

        public IEnumerable<string> Games
        {
            get { return GameInstalls.GameIds; }
        }

        public string GameName(string game)
        {
            return GameInstalls.Find(game, new List<string>()).Name;
        }

        public string InstallFolder(string game)
        {
            string folder;
            if (installs.TryGetValue(game, out folder))
                return folder;
            if (Test)
            {
                string candidate = Path.Combine(root, "Games", game);
                folder = Directory.Exists(candidate) ? candidate : string.Empty;
            }
            else
            {
                try
                {
                    GameInstall install = GameInstalls.Find(game, new List<string>());
                    folder = install != null && install.Installed && !install.Disabled && Directory.Exists(install.InstallPath ?? string.Empty) ? install.InstallPath : string.Empty;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    folder = string.Empty;
                }
            }
            installs[game] = folder;
            return folder;
        }

        public IEnumerable<string> DcsSavedGames()
        {
            if (Test)
            {
                string saved = SavedGamesRoot();
                return Directory.Exists(saved) ? Directory.GetDirectories(saved, "DCS*").OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
            }
            List<string> found = GamePathDiscovery.DiscoverDcsSavedGamesPaths(new List<string>()) ?? new List<string>();
            string configured = GameInstalls.ConfigOverride("DCS");
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured) && !found.Contains(configured, StringComparer.OrdinalIgnoreCase))
                found.Insert(0, configured);
            return found.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public string SavedGamesRoot()
        {
            if (Test)
                return Path.Combine(root, "Saved Games");
            string known = GamePathDiscovery.TryGetSavedGamesKnownFolderPath(new List<string>());
            if (!string.IsNullOrWhiteSpace(known) && Directory.Exists(known))
                return known;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
        }

        public string DocumentsRoot()
        {
            return Test ? Path.Combine(root, "Documents") : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        public IEnumerable<string> SteamRoots()
        {
            if (Test)
            {
                string steam = Path.Combine(root, "Steam");
                return Directory.Exists(steam) ? new List<string> { steam } : new List<string>();
            }
            return GamePathDiscovery.GetSteamInstallRoots(new List<string>());
        }

        public int[] SteamApps(string game)
        {
            return GameInstalls.SteamAppIds(game);
        }

        public IEnumerable<string> ProcessNames(string game)
        {
            EngineSync.SyncGame sync = EngineSync.AllGames().FirstOrDefault(g => string.Equals(g.Id, game, StringComparison.OrdinalIgnoreCase));
            return sync == null ? new List<string>() : sync.ProcessNames ?? new List<string>();
        }
    }
}
