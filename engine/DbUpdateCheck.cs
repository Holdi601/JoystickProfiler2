using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    public sealed class DbUpdateState
    {
        public string Game { get; set; }
        public string DisplayName { get; set; }
        public bool Installed { get; set; }
        public string InstalledVersion { get; set; }
        public string DbVersion { get; set; }
        // up-to-date | game-updated | no-catalog | baseline-recorded | no-baseline | not-installed
        public string State { get; set; }
        public string Message { get; set; }
        public string RefreshCommand { get; set; }
        public List<string> Details { get; set; }

        public DbUpdateState()
        {
            Game = string.Empty;
            DisplayName = string.Empty;
            InstalledVersion = string.Empty;
            DbVersion = string.Empty;
            State = string.Empty;
            Message = string.Empty;
            RefreshCommand = string.Empty;
            Details = new List<string>();
        }
    }

    public sealed class DbUpdateReport
    {
        public List<DbUpdateState> Games { get; set; }
        public int Outdated { get; set; }
        public List<string> Warnings { get; set; }

        public DbUpdateReport()
        {
            Games = new List<DbUpdateState>();
            Warnings = new List<string>();
        }
    }

    // Detects game updates that may have added key commands missing from JoyPro's DB. For every installed game the
    // current build (DCS autoupdate.cfg + module folders, Star Citizen build_manifest.id, Steam build ids, X-Plane's
    // Commands.txt) is compared with DB\<Folder>\source-version.txt, which a DB refresh writes. The first check only
    // records the build as the baseline. The command lists and their stamps live in the game-data folder
    // (<data>\GameData, GameDataPaths), so an update or rebuild of JoyPro does not make them outdated again.
    internal static class DbUpdateCheck
    {
        private const string StampFile = "source-version.txt";

        private sealed class Probe
        {
            public string Game;
            public string DisplayName;
            public string DbFolder;
            public string RefreshCommand;
            public Func<List<string>, Dictionary<string, string>> Read;
            public Func<Dictionary<string, string>, List<string>> NewCommands;
            // True when JoyPro has a command list of the game. Release packages ship none (docs/licensing-audit.md):
            // the list is built on the user's PC from the installed game by the refresh command.
            public Func<bool> HasCatalog;
        }

        public static DbUpdateReport CheckWithoutRecording()
        {
            return Check(null, false, false);
        }

        public static DbUpdateReport Check(ICollection<string> acceptGames, bool acceptAll)
        {
            return Check(acceptGames, acceptAll, true);
        }

        private static DbUpdateReport Check(ICollection<string> acceptGames, bool acceptAll, bool record)
        {
            DbUpdateReport report = new DbUpdateReport();
            foreach (Probe probe in BuildProbes())
            {
                DbUpdateState state = new DbUpdateState { Game = probe.Game, DisplayName = probe.DisplayName, RefreshCommand = probe.RefreshCommand };
                report.Games.Add(state);

                Dictionary<string, string> current;
                try
                {
                    current = probe.Read(report.Warnings);
                }
                catch (Exception ex)
                {
                    report.Warnings.Add(probe.DisplayName + ": could not read the installed version: " + ex.Message);
                    current = null;
                }
                bool hasCatalog = probe.HasCatalog == null || probe.HasCatalog();
                if (current == null || current.Count == 0)
                {
                    state.State = "not-installed";
                    if (!hasCatalog)
                        state.Message = "JoyPro builds the " + probe.DisplayName + " command list from the installed game; install it (or link its folder) first.";
                    continue;
                }

                state.Installed = true;
                state.InstalledVersion = Describe(current);

                // No command list yet (a fresh install): it has to be built from the game first. No build is recorded,
                // so the list is not taken as current before it exists.
                if (!hasCatalog)
                {
                    state.State = "no-catalog";
                    state.Message = "JoyPro has no command list of " + probe.DisplayName + " yet. Build it from your installation" +
                                    (probe.RefreshCommand.Length > 0 ? " (" + probe.RefreshCommand + ")" : string.Empty) + ".";
                    report.Outdated++;
                    continue;
                }
                Dictionary<string, string> recorded = ReadStamp(probe.DbFolder);
                state.DbVersion = recorded.Count == 0 ? string.Empty : Describe(recorded);
                bool accept = acceptAll || (acceptGames != null && acceptGames.Any(g => string.Equals(g, probe.Game, StringComparison.OrdinalIgnoreCase)));

                if (recorded.Count == 0 && !record)
                {
                    state.State = "no-baseline";
                    state.Message = "No build recorded for the " + probe.DisplayName + " DB yet.";
                    continue;
                }

                if (recorded.Count == 0 || accept)
                {
                    if (record)
                        WriteStamp(probe.DbFolder, current);
                    state.State = recorded.Count == 0 ? "baseline-recorded" : "up-to-date";
                    state.Message = recorded.Count == 0
                        ? "Recorded " + probe.DisplayName + " " + state.InstalledVersion + " as the build the DB belongs to."
                        : "Marked the " + probe.DisplayName + " DB as current.";
                    continue;
                }

                List<string> changes = Compare(recorded, current);
                if (probe.NewCommands != null)
                    changes.AddRange(probe.NewCommands(current));
                if (changes.Count == 0)
                {
                    state.State = "up-to-date";
                    continue;
                }

                state.State = "game-updated";
                state.Details = changes;
                state.Message = probe.DisplayName + " was updated since its DB was built (" + state.DbVersion + " -> " + state.InstalledVersion +
                                "). New key commands may be missing; refresh the " + probe.DisplayName + " DB" +
                                (probe.RefreshCommand.Length > 0 ? " (" + probe.RefreshCommand + ")" : string.Empty) + ".";
                report.Outdated++;
            }
            foreach (DbUpdateState state in report.Games)
                JoyPro.Logging.JoyProLog.Debug("DbUpdate", state.Game + ": " + state.State +
                                                           (string.IsNullOrEmpty(state.InstalledVersion) ? string.Empty : " | installed " + state.InstalledVersion) +
                                                           (string.IsNullOrEmpty(state.DbVersion) ? string.Empty : " | db " + state.DbVersion));
            return report;
        }

        // A successful DB refresh stores the build it was made from.
        public static void RecordCurrent(string game, List<string> warnings)
        {
            Probe probe = BuildProbes().FirstOrDefault(p => string.Equals(p.Game, game, StringComparison.OrdinalIgnoreCase));
            if (probe == null)
                return;
            try
            {
                Dictionary<string, string> current = probe.Read(warnings ?? new List<string>());
                if (current != null && current.Count > 0)
                    WriteStamp(probe.DbFolder, current);
            }
            catch (Exception ex)
            {
                warnings?.Add("Could not record the " + probe.DisplayName + " build for the DB: " + ex.Message);
            }
        }

        private static List<Probe> BuildProbes()
        {
            List<Probe> probes = new List<Probe>
            {
                new Probe { Game = "DCS", DisplayName = "DCS World", DbFolder = "DCS", RefreshCommand = "engine dcs refresh id", Read = ReadDcs,
                            // The command list (DB\DCS\*.html, dcs refresh id) and the clean profiles (CleanProfile\DCS,
                            // dcs refresh clean), both built from the user's DCS installation.
                            HasCatalog = () => DbFolderHas("DCS", "*.html") && Directory.Exists(GameDataPaths.CleanProfileFolder("DCS", false)) &&
                                               Directory.EnumerateFiles(GameDataPaths.CleanProfileFolder("DCS", false), "*.cf").Any() },
                new Probe { Game = "IL2Game", DisplayName = "IL-2 Great Battles", DbFolder = "IL2", RefreshCommand = "engine db refresh-il2", Read = w => SteamBuild("IL-2 Sturmovik Battle of Stalingrad", "IL-2 Sturmovik Great Battles"),
                            HasCatalog = () => DbFolderHas("IL2", "IL2.actions") },
                new Probe { Game = "StarCitizen", DisplayName = "Star Citizen", DbFolder = "StarCitizen", RefreshCommand = "engine db refresh-starcitizen", Read = ReadStarCitizen,
                            HasCatalog = () => DbFolderHas("StarCitizen", "AllBinds.xml") || DbFolderHas("StarCitizen", "actionmaps.xml") }
            };

            Dictionary<string, string[]> steamDirs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "Wardogs", new[] { "Wardogs" } },
                { "IL2Korea", new[] { "IL2Series" } },
                { "Arma3", new[] { "Arma 3" } },
                { "Arma2OA", new[] { "Arma 2 Operation Arrowhead" } },
                { "Arma2", new[] { "Arma 2" } },
                { "VTOLVR", new[] { "VTOL VR" } },
                { "VTOLVRNative", new[] { "VTOL VR" } },
                { "NuclearOption", new[] { "Nuclear Option" } },
                { "TinyCombatArena", new[] { "TinyCombatArena" } },
                { "GRebels", new[] { "G-Rebels" } },
                { "ArmaReforger", new[] { "Arma Reforger" } },
                { "WarThunder", new[] { "War Thunder" } },
                { "MSFS2020", new[] { "MicrosoftFlightSimulator" } },
                { "MSFS2024", new[] { "MSFS2024", "Microsoft Flight Simulator 2024" } },
                { "X4", new[] { "X4 Foundations" } },
                { "EliteDangerous", new[] { "Elite Dangerous" } },
                { "IL21946", new[] { "IL 2 Sturmovik 1946", "IL-2 Sturmovik 1946" } },
                { "IL2CoD", new[] { "IL-2 Sturmovik Cliffs of Dover" } },
                { "IL2CoDBlitz", new[] { "IL-2 Sturmovik Cliffs of Dover Blitz" } },
                { "FSXSE", new[] { "FSX" } }
            };

            foreach (IGameIntegration game in GameRegistry.All)
            {
                string command = game.SupportsCatalogRefresh ? "engine " + game.Aliases[0] + " refresh-db" : "update JoyPro";
                XPlaneIntegration xplane = game as XPlaneIntegration;
                if (xplane != null)
                {
                    probes.Add(new Probe
                    {
                        Game = game.GameId,
                        DisplayName = game.DisplayName,
                        DbFolder = game.DbFolder,
                        RefreshCommand = command,
                        Read = w => ReadXPlane(xplane),
                        NewCommands = current => NewCatalogActions(xplane, xplane.CommandsFile(), "command")
                    });
                    continue;
                }
                if (string.Equals(game.GameId, "FalconBMS", StringComparison.OrdinalIgnoreCase))
                {
                    IGameIntegration bms = game;
                    probes.Add(new Probe
                    {
                        Game = game.GameId,
                        DisplayName = game.DisplayName,
                        DbFolder = game.DbFolder,
                        RefreshCommand = command,
                        Read = ReadBms,
                        NewCommands = current => NewCatalogActions(bms, FalconBmsIntegration.FindFullKeyFile(), "callback")
                    });
                    continue;
                }

                string[] dirs;
                if (!steamDirs.TryGetValue(game.GameId, out dirs))
                    continue;
                probes.Add(new Probe { Game = game.GameId, DisplayName = game.DisplayName, DbFolder = game.DbFolder, RefreshCommand = command, Read = w => SteamBuild(dirs) });
            }

            // Games of the registry: their command list is DB\<Folder>\actions.tsv.
            foreach (Probe probe in probes.Where(p => p.HasCatalog == null))
            {
                IGameIntegration game = GameRegistry.All.FirstOrDefault(g => string.Equals(g.GameId, probe.Game, StringComparison.OrdinalIgnoreCase));
                if (game != null)
                    probe.HasCatalog = () => File.Exists(GameCatalogStore.CatalogPath(game));
            }
            return probes;
        }

        private static bool DbFolderHas(string dbFolder, string pattern)
        {
            string folder = GameDataPaths.DbFolder(dbFolder);
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, pattern).Any();
        }

        // JoyPro has a command list of the game (built on this PC, or a development copy); false for a fresh install.
        public static bool HasCatalog(string game)
        {
            Probe probe = BuildProbes().FirstOrDefault(p => string.Equals(p.Game, game, StringComparison.OrdinalIgnoreCase));
            return probe == null || probe.HasCatalog == null || probe.HasCatalog();
        }

        private static Dictionary<string, string> ReadDcs(List<string> warnings)
        {
            string install = GamePathDiscovery.DiscoverDcsInstallPaths(warnings).FirstOrDefault(Directory.Exists);
            if (install == null)
                return null;

            Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
            string config = Path.Combine(install, "autoupdate.cfg");
            if (File.Exists(config))
            {
                Match version = Regex.Match(File.ReadAllText(config), "\"version\"\\s*:\\s*\"(?<v>[^\"]+)\"");
                if (version.Success)
                    fields["version"] = version.Groups["v"].Value;
            }

            // Modules bring their own key commands; a new module folder always needs a DB refresh.
            List<string> modules = new List<string>();
            foreach (string root in new[] { Path.Combine(install, "Mods", "aircraft") }
                .Concat(GamePathDiscovery.DiscoverDcsSavedGamesPaths(warnings).Select(p => Path.Combine(p, "Mods", "aircraft"))))
            {
                if (Directory.Exists(root))
                    modules.AddRange(Directory.GetDirectories(root).Select(Path.GetFileName));
            }
            if (modules.Count > 0)
                fields["modules"] = string.Join(",", modules.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase));
            return fields;
        }

        private static Dictionary<string, string> ReadStarCitizen(List<string> warnings)
        {
            foreach (string install in GamePathDiscovery.DiscoverStarCitizenInstallPaths(warnings))
            {
                foreach (string branch in new[] { "LIVE", "PTU", "EPTU", "TECH-PREVIEW" })
                {
                    string manifest = Path.Combine(install, branch, "build_manifest.id");
                    if (!File.Exists(manifest))
                        continue;
                    Match version = Regex.Match(File.ReadAllText(manifest), "\"Version\"\\s*:\\s*\"(?<v>[^\"]+)\"");
                    if (version.Success)
                        return new Dictionary<string, string>(StringComparer.Ordinal) { { "version", branch + " " + version.Groups["v"].Value } };
                }
            }
            return null;
        }

        // X-Plane 11 / 12: the version's Commands.txt (X-Plane lists its commands locally, so the exact new ones can be
        // named).
        private static Dictionary<string, string> ReadXPlane(XPlaneIntegration xplane)
        {
            string commands = xplane.CommandsFile();
            if (commands == null)
                return null;
            using (SHA256 sha = SHA256.Create())
            {
                string hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(commands))).Replace("-", string.Empty).Substring(0, 16);
                return new Dictionary<string, string>(StringComparer.Ordinal) { { "commands", hash } };
            }
        }

        // Actions a refresh from the game's own list would add to the shipped catalog.
        private static List<string> NewCatalogActions(IGameIntegration game, string source, string noun)
        {
            List<string> changes = new List<string>();
            if (source == null)
                return changes;
            GameIoResult scratch = new GameIoResult();
            GameCatalog fresh = game.RefreshCatalog(source, null, scratch);
            GameCatalog known = GameCatalogStore.Load(game, new List<string>());
            if (fresh == null || known.Actions.Count == 0)
                return changes;
            List<string> added = fresh.Actions.Where(a => !known.Contains(a.Id)).Select(a => a.Id).ToList();
            if (added.Count > 0)
                changes.Add(added.Count.ToString(CultureInfo.InvariantCulture) + " new flight " + noun + "(s), e.g. " + string.Join(", ", added.Take(5)));
            return changes;
        }

        // BMS patches update Falcon BMS.exe and the reference key file that lists the callbacks.
        private static Dictionary<string, string> ReadBms(List<string> warnings)
        {
            string keyFile = FalconBmsIntegration.FindFullKeyFile();
            if (keyFile == null)
                return null;
            Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
            string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(keyFile)));
            string version = FalconBmsIntegration.ReadVersion(root);
            if (version.Length > 0)
                fields["version"] = version;
            using (SHA256 sha = SHA256.Create())
                fields["callbacks"] = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(keyFile))).Replace("-", string.Empty).Substring(0, 16);
            return fields;
        }

        private static Dictionary<string, string> SteamBuild(params string[] installDirs)
        {
            foreach (string library in GamePathDiscovery.GetSteamLibraryRoots(new List<string>()))
            {
                string apps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(apps))
                    continue;
                foreach (string manifest in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    string text = File.ReadAllText(manifest);
                    Match dir = Regex.Match(text, "\"installdir\"\\s*\"(?<d>[^\"]+)\"");
                    if (!dir.Success || !installDirs.Any(d => string.Equals(d, dir.Groups["d"].Value, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (!Directory.Exists(Path.Combine(apps, "common", dir.Groups["d"].Value)))
                        continue;
                    Match build = Regex.Match(text, "\"buildid\"\\s*\"(?<b>\\d+)\"");
                    if (build.Success)
                        return new Dictionary<string, string>(StringComparer.Ordinal) { { "steam-build", build.Groups["b"].Value } };
                }
            }
            return null;
        }

        private static List<string> Compare(Dictionary<string, string> recorded, Dictionary<string, string> current)
        {
            List<string> changes = new List<string>();
            foreach (KeyValuePair<string, string> entry in current)
            {
                string old;
                recorded.TryGetValue(entry.Key, out old);
                if (string.Equals(old, entry.Value, StringComparison.Ordinal))
                    continue;
                if (entry.Key == "modules")
                {
                    HashSet<string> before = new HashSet<string>((old ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                    List<string> added = entry.Value.Split(',').Where(m => !before.Contains(m)).ToList();
                    if (added.Count > 0)
                        changes.Add("new module(s): " + string.Join(", ", added));
                    continue;
                }
                changes.Add(entry.Key + ": " + (string.IsNullOrEmpty(old) ? "?" : old) + " -> " + entry.Value);
            }
            return changes;
        }

        private static string Describe(Dictionary<string, string> fields)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, string> entry in fields.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (entry.Key == "modules")
                    parts.Add(entry.Value.Split(',').Length.ToString(CultureInfo.InvariantCulture) + " modules");
                else
                    parts.Add(entry.Key == "version" ? entry.Value : entry.Key + " " + entry.Value);
            }
            return string.Join(", ", parts);
        }

        // The stamp belongs to the command list it describes: read where the list is read (GameDataPaths), written
        // only into the game-data folder.
        private static Dictionary<string, string> ReadStamp(string dbFolder)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
            string path = GameDataPaths.DbFile(dbFolder, StampFile);
            if (!File.Exists(path))
                return fields;
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && !line.StartsWith("#", StringComparison.Ordinal) && line.Substring(0, eq) != "recorded")
                    fields[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return fields;
        }

        private static void WriteStamp(string dbFolder, Dictionary<string, string> fields)
        {
            string path = GameDataPaths.WritableDbFile(dbFolder, StampFile);
            List<string> lines = new List<string> { "# Game build this DB belongs to (written by JoyPro on DB refresh; see engine db check-updates)." };
            lines.AddRange(fields.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + "=" + e.Value));
            lines.Add("recorded=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }

        public static string ToText(DbUpdateReport report)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DB Update Check");
            sb.AppendLine("Outdated: " + report.Outdated.ToString(CultureInfo.InvariantCulture));
            foreach (DbUpdateState state in report.Games)
            {
                sb.AppendLine("  - " + state.DisplayName + ": " + state.State + (state.Installed ? " | installed " + state.InstalledVersion : string.Empty) +
                              (state.DbVersion.Length > 0 ? " | DB " + state.DbVersion : string.Empty));
                if (state.Message.Length > 0)
                    sb.AppendLine("      " + state.Message);
                foreach (string detail in state.Details)
                    sb.AppendLine("      * " + detail);
            }
            foreach (string warning in report.Warnings)
                sb.AppendLine("Warning: " + warning);
            return sb.ToString();
        }
    }
}
