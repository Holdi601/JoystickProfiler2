#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Usage
{
    // Where a game keeps what JoyPro reads: the engine answers from its install detection (GameInstalls, Saved Games,
    // Steam); the tests answer with fixture folders.
    public interface IUsageLocator
    {
        IEnumerable<string> Games { get; }
        string GameName(string game);
        // The installation folder, empty when the game is not installed (or the user said they do not have it).
        string InstallFolder(string game);
        IEnumerable<string> DcsSavedGames();
        string SavedGamesRoot();
        string DocumentsRoot();
        IEnumerable<string> SteamRoots();
        int[] SteamApps(string game);
        IEnumerable<string> ProcessNames(string game);
    }

    // One place to read: a folder and a file pattern of one source kind. LiveTail: the watcher follows these files
    // while the game runs (game logs that are overwritten at the next start, journals).
    public sealed class UsageSourceSpec
    {
        public string Game = string.Empty;
        public string Kind = string.Empty;
        public string Directory = string.Empty;
        public string Pattern = "*";
        public bool Recursive;
        public bool LiveTail;
        public string Note = string.Empty;

        public IEnumerable<string> Files()
        {
            if (string.IsNullOrEmpty(Directory) || !System.IO.Directory.Exists(Directory))
                return new string[0];
            try
            {
                return System.IO.Directory.GetFiles(Directory, Pattern, Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (IOException)
            {
                return new string[0];
            }
            catch (UnauthorizedAccessException)
            {
                return new string[0];
            }
        }
    }

    public static class UsageSources
    {
        // Star Citizen's release channels next to each other in the installation folder.
        private static readonly string[] ScChannels = { "LIVE", "PTU", "EPTU", "TECH-PREVIEW", "HOTFIX" };

        public static List<UsageSourceSpec> Discover(IUsageLocator locator, ICollection<string> games = null)
        {
            List<UsageSourceSpec> specs = new List<UsageSourceSpec>();
            Func<string, bool> wanted = g => games == null || games.Count == 0 || games.Contains(g, StringComparer.OrdinalIgnoreCase);
            string documents = locator.DocumentsRoot() ?? string.Empty;
            string savedGames = locator.SavedGamesRoot() ?? string.Empty;

            if (wanted("DCS") && Installed(locator, "DCS"))
            {
                foreach (string saved in locator.DcsSavedGames() ?? new string[0])
                {
                    specs.Add(new UsageSourceSpec { Game = "DCS", Kind = UsageSourceKinds.DcsLog, Directory = Path.Combine(saved, "Logs"), Pattern = "dcs.log", LiveTail = true });
                    specs.Add(new UsageSourceSpec { Game = "DCS", Kind = UsageSourceKinds.DcsLog, Directory = Path.Combine(saved, "Logs"), Pattern = "dcs.log.old" });
                    specs.Add(new UsageSourceSpec { Game = "DCS", Kind = UsageSourceKinds.DcsLogbook, Directory = Path.Combine(saved, "MissionEditor"), Pattern = "logbook.lua" });
                    specs.Add(new UsageSourceSpec { Game = "DCS", Kind = UsageSourceKinds.DcsTrack, Directory = Path.Combine(saved, "Tracks"), Pattern = "*.trk", Recursive = true });
                }
            }
            // Tacview's own folder serves every game (the game comes from each recording).
            if (documents.Length > 0 && (games == null || games.Count == 0 || games.Any(g => g == "DCS" || g == "FalconBMS" || g.StartsWith("IL2", StringComparison.OrdinalIgnoreCase))))
                specs.Add(new UsageSourceSpec { Game = string.Empty, Kind = UsageSourceKinds.Tacview, Directory = Path.Combine(documents, "Tacview"), Pattern = "*.acmi", Recursive = true });

            string bms = wanted("FalconBMS") ? locator.InstallFolder("FalconBMS") : string.Empty;
            if (!string.IsNullOrEmpty(bms))
            {
                specs.Add(new UsageSourceSpec { Game = "FalconBMS", Kind = UsageSourceKinds.BmsLogbook, Directory = Path.Combine(bms, "User", "Config"), Pattern = "*.lbk" });
                specs.Add(new UsageSourceSpec { Game = "FalconBMS", Kind = UsageSourceKinds.Tacview, Directory = Path.Combine(bms, "User", "Acmi"), Pattern = "*.acmi" });
            }

            foreach (string il2 in new[] { "IL2Game", "IL2Korea" })
            {
                string folder = wanted(il2) ? locator.InstallFolder(il2) : string.Empty;
                if (string.IsNullOrEmpty(folder))
                    continue;
                UsageSourceSpec spec = new UsageSourceSpec { Game = il2, Kind = UsageSourceKinds.Il2MissionReport, Directory = Path.Combine(folder, "data", "missionReports"), Pattern = "missionReport*.txt", LiveTail = true };
                if (Il2TextLogOff(folder))
                    spec.Note = "IL-2 writes no mission text logs (startup.cfg: mission_text_log = 0)";
                specs.Add(spec);
            }

            foreach (string xp in new[] { "XPlane12", "XPlane11" })
            {
                string folder = wanted(xp) ? locator.InstallFolder(xp) : string.Empty;
                if (string.IsNullOrEmpty(folder))
                    continue;
                specs.Add(new UsageSourceSpec { Game = xp, Kind = UsageSourceKinds.XPlaneLogbook, Directory = Path.Combine(folder, "Output", "logbooks"), Pattern = "*.txt" });
                specs.Add(new UsageSourceSpec { Game = xp, Kind = UsageSourceKinds.XPlaneLog, Directory = folder, Pattern = "Log.txt", LiveTail = true });
            }

            foreach (string msfs in new[] { "MSFS2024", "MSFS2020" })
            {
                if (wanted(msfs) && Installed(locator, msfs))
                    specs.Add(new UsageSourceSpec { Game = msfs, Kind = UsageSourceKinds.SteamPlaytime, Note = "The flight logbook is kept online; only Steam's playtime is on this PC" });
            }

            string wt = wanted("WarThunder") ? locator.InstallFolder("WarThunder") : string.Empty;
            if (!string.IsNullOrEmpty(wt))
            {
                specs.Add(new UsageSourceSpec { Game = "WarThunder", Kind = UsageSourceKinds.WarThunderReplay, Directory = Path.Combine(wt, "Replays"), Pattern = "*.wrpl" });
                if (documents.Length > 0)
                    specs.Add(new UsageSourceSpec { Game = "WarThunder", Kind = UsageSourceKinds.WarThunderReplay, Directory = Path.Combine(documents, "My Games", "WarThunder", "Replays"), Pattern = "*.wrpl" });
            }

            if (wanted("EliteDangerous") && Installed(locator, "EliteDangerous") && savedGames.Length > 0)
                specs.Add(new UsageSourceSpec { Game = "EliteDangerous", Kind = UsageSourceKinds.EliteJournal, Directory = Path.Combine(savedGames, "Frontier Developments", "Elite Dangerous"), Pattern = "Journal.*.log", LiveTail = true });

            string sc = wanted("StarCitizen") ? locator.InstallFolder("StarCitizen") : string.Empty;
            if (!string.IsNullOrEmpty(sc))
            {
                // The detected folder may be the channel folder itself (...\StarCitizen\LIVE).
                string root = ScChannels.Any(c => string.Equals(Path.GetFileName(sc.TrimEnd('\\', '/')), c, StringComparison.OrdinalIgnoreCase)) ? Path.GetDirectoryName(sc.TrimEnd('\\', '/')) : sc;
                foreach (string channel in ScChannels)
                {
                    string folder = Path.Combine(root, channel);
                    if (!System.IO.Directory.Exists(folder))
                        continue;
                    specs.Add(new UsageSourceSpec { Game = "StarCitizen", Kind = UsageSourceKinds.StarCitizenLog, Directory = folder, Pattern = "Game.log", LiveTail = true });
                    specs.Add(new UsageSourceSpec { Game = "StarCitizen", Kind = UsageSourceKinds.StarCitizenLog, Directory = Path.Combine(folder, "logbackups"), Pattern = "*.log" });
                }
            }

            if (wanted("X4") && Installed(locator, "X4") && documents.Length > 0)
            {
                string x4 = Path.Combine(documents, "Egosoft", "X4");
                if (System.IO.Directory.Exists(x4))
                {
                    foreach (string user in SafeDirectories(x4))
                    {
                        specs.Add(new UsageSourceSpec { Game = "X4", Kind = UsageSourceKinds.X4Save, Directory = Path.Combine(user, "save"), Pattern = "*.xml.gz" });
                        specs.Add(new UsageSourceSpec { Game = "X4", Kind = UsageSourceKinds.X4Save, Directory = Path.Combine(user, "save"), Pattern = "*.xml" });
                    }
                }
            }
            return specs;
        }

        // Steam's localconfig.vdf of every account on this PC.
        public static List<string> SteamConfigs(IUsageLocator locator)
        {
            List<string> files = new List<string>();
            foreach (string root in locator.SteamRoots() ?? new string[0])
            {
                string userdata = Path.Combine(root, "userdata");
                foreach (string account in SafeDirectories(userdata))
                {
                    string file = Path.Combine(account, "config", "localconfig.vdf");
                    if (File.Exists(file) && !files.Contains(file, StringComparer.OrdinalIgnoreCase))
                        files.Add(file);
                }
            }
            return files;
        }

        private static bool Installed(IUsageLocator locator, string game)
        {
            return !string.IsNullOrEmpty(locator.InstallFolder(game));
        }

        private static IEnumerable<string> SafeDirectories(string folder)
        {
            try
            {
                return System.IO.Directory.Exists(folder) ? System.IO.Directory.GetDirectories(folder) : new string[0];
            }
            catch (IOException)
            {
                return new string[0];
            }
            catch (UnauthorizedAccessException)
            {
                return new string[0];
            }
        }

        private static bool Il2TextLogOff(string install)
        {
            try
            {
                string cfg = Path.Combine(install, "data", "startup.cfg");
                if (!File.Exists(cfg))
                    return false;
                Match m = Regex.Match(File.ReadAllText(cfg), @"mission_text_log\s*=\s*(\d)");
                return m.Success && m.Groups[1].Value == "0";
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
