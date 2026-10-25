using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Games;
using Microsoft.Win32;

namespace JoyPro.Engine
{
    // Is a game installed, and where? One place for every game JoyPro supports:
    //   1. the user's override (Settings > Games, engine games install set): that folder is the installation;
    //   2. Steam (appmanifest of the game's app ids in every Steam library);
    //   3. game-specific places (DCS / IL-2 / Star Citizen discovery, Falcon BMS registry, X-Plane's install file,
    //      Microsoft Store packages, launcher folders);
    //   4. Windows' uninstall entries (DisplayName -> InstallLocation).
    // A found folder must contain one of the game's marker files (its executable). Import, export, scan, DB refresh
    // and backup restore are refused for a game that is neither found nor overridden.
    // Next to the installation folders the data folder keeps the user's config location per game (game-configs.json:
    // e.g. Saved Games\DCS.openbeta instead of Saved Games\DCS) and the games the user does not have
    // (games-disabled.json, the first-start setup's "I have this game"): those count as not installed.
    internal static class GameInstalls
    {
        // Test hooks: detection off (only overrides count), or every game counts as installed.
        public const string DetectionVariable = "JOYPRO_INSTALL_DETECTION";
        public const string AssumeInstalledVariable = "JOYPRO_TEST_ASSUME_INSTALLED";
        private const string OverridesFile = "game-installs.json";
        private const string ConfigOverridesFile = "game-configs.json";
        private const string DisabledFile = "games-disabled.json";

        private sealed class Spec
        {
            public string GameId;
            public string Name;
            public int[] SteamApps = new int[0];
            public string[] Markers = new string[0];
            public string[] UninstallNames = new string[0];
            // Uninstall key names (e.g. an Inno Setup AppId "{...}_is1"), matched whatever name the entry shows.
            public string[] UninstallKeys = new string[0];
            public Func<List<string>, IEnumerable<string>> Places;
        }

        private static readonly List<Spec> Specs = new List<Spec>
        {
            new Spec { GameId = "DCS", Name = "DCS World", SteamApps = new[] { 223750 }, Markers = new[] { @"bin\DCS.exe", @"bin-mt\DCS.exe" },
                UninstallNames = new[] { "DCS World" }, Places = w => GamePathDiscovery.DetectDcsInstallPaths(w) },
            new Spec { GameId = "IL2Game", Name = "IL-2 Sturmovik: Great Battles", SteamApps = new[] { 307960 }, Markers = new[] { @"bin\game\Il-2.exe" },
                UninstallNames = new[] { "IL-2 Sturmovik" }, Places = w => GamePathDiscovery.DetectIl2InstallPaths(w) },
            // Steam: steamapps\common\IL2Series. Standalone (1C Game Studios' Inno Setup installer, AppId
            // {222bc1b4-be6e-4451-9291-ef5aeb7c64a2}, shown as "Korea. IL2 Series"): the chosen folder (default Program
            // Files\IL2Series) holds Launcher.exe and the game in its "game" subfolder (game\bin\game\IL2Series.exe,
            // game\data\input); that subfolder is the installation (ResolveInstallFolder).
            new Spec { GameId = "IL2Korea", Name = "IL-2 Series: Korea", SteamApps = new[] { 247970 }, Markers = new[] { @"bin\game\IL2Series.exe" },
                UninstallNames = new[] { "Korea. IL2 Series", "Korea. IL-2 Series", "Korea IL2 Series", "Korea IL-2 Series", "IL-2 Series", "IL2 Series" },
                UninstallKeys = new[] { "{222bc1b4-be6e-4451-9291-ef5aeb7c64a2}_is1", "Steam App 247970" }, Places = Il2KoreaPlaces },
            new Spec { GameId = "StarCitizen", Name = "Star Citizen", Markers = new[] { @"LIVE\Bin64\StarCitizen.exe", @"PTU\Bin64\StarCitizen.exe", @"Bin64\StarCitizen.exe" },
                UninstallNames = new[] { "Star Citizen" }, Places = w => GamePathDiscovery.DetectStarCitizenInstallPaths(w) },
            // MSFS and X-Plane: one game per version (side by side, each with its own installation and config files).
            new Spec { GameId = "MSFS2024", Name = "Microsoft Flight Simulator 2024", SteamApps = new[] { 2537590 },
                Markers = new[] { "FlightSimulator2024.exe", "UserCfg.opt", @"LocalCache\UserCfg.opt" },
                UninstallNames = new[] { "Microsoft Flight Simulator 2024" }, Places = w => MsfsPlaces(2024) },
            new Spec { GameId = "MSFS2020", Name = "Microsoft Flight Simulator 2020", SteamApps = new[] { 1250410 },
                Markers = new[] { "FlightSimulator.exe", "UserCfg.opt", @"LocalCache\UserCfg.opt" },
                UninstallNames = new[] { "Microsoft Flight Simulator" }, Places = w => MsfsPlaces(2020) },
            new Spec { GameId = "XPlane12", Name = "X-Plane 12", SteamApps = new[] { 2014780 }, Markers = new[] { "X-Plane.exe" },
                UninstallNames = new[] { "X-Plane 12" }, Places = w => XPlaneIntegration.InstallLogRoots(12) },
            new Spec { GameId = "XPlane11", Name = "X-Plane 11", SteamApps = new[] { 269950 }, Markers = new[] { "X-Plane.exe" },
                UninstallNames = new[] { "X-Plane 11" }, Places = w => XPlaneIntegration.InstallLogRoots(11) },
            new Spec { GameId = "WarThunder", Name = "War Thunder", SteamApps = new[] { 236390 }, Markers = new[] { @"win64\aces.exe", "launcher.exe", "aces.exe" },
                UninstallNames = new[] { "War Thunder" }, Places = w => Existing(Path.Combine(LocalAppData(), "WarThunder"), Path.Combine(ProgramFiles(), "War Thunder")) },
            new Spec { GameId = "Arma3", Name = "Arma 3", SteamApps = new[] { 107410 }, Markers = new[] { "arma3_x64.exe", "arma3.exe" },
                UninstallNames = new[] { "Arma 3" }, Places = w => RegistryFolders(@"SOFTWARE\Bohemia Interactive\ArmA 3", "main") },
            new Spec { GameId = "Arma2", Name = "Arma 2", SteamApps = new[] { 33900, 33910 }, Markers = new[] { "arma2.exe" },
                UninstallNames = new[] { "Arma 2" }, Places = w => RegistryFolders(@"SOFTWARE\Bohemia Interactive Studio\ArmA 2", "main") },
            new Spec { GameId = "Arma2OA", Name = "Arma 2: Operation Arrowhead", SteamApps = new[] { 33930 }, Markers = new[] { "ArmA2OA.exe", "arma2oa.exe" },
                UninstallNames = new[] { "Arma 2: Operation Arrowhead", "Arma 2 Operation Arrowhead" }, Places = w => RegistryFolders(@"SOFTWARE\Bohemia Interactive Studio\ArmA 2 OA", "main") },
            new Spec { GameId = "ArmaReforger", Name = "Arma Reforger", SteamApps = new[] { 1874880 }, Markers = new[] { "ArmaReforgerSteam.exe", "ArmaReforger.exe", "ArmaReforger_BE.exe" },
                UninstallNames = new[] { "Arma Reforger" } },
            new Spec { GameId = "Wardogs", Name = "WARDOGS", SteamApps = new[] { 1867240 }, Markers = new[] { "WardogsLauncher-Shipping.exe", @"Wardogs\Binaries\Win64" },
                UninstallNames = new[] { "WARDOGS" } },
            new Spec { GameId = "HLLVietnam", Name = "Hell Let Loose: Vietnam", SteamApps = new[] { 3079210 }, Markers = new[] { "HLLVietnam.exe", "Launch_HLL.exe" },
                UninstallNames = new[] { "Hell Let Loose: Vietnam", "Hell Let Loose - Vietnam" } },
            new Spec { GameId = "Battlefield6", Name = "Battlefield 6", SteamApps = new[] { 2807960 }, Markers = new[] { "bf6.exe" },
                UninstallNames = new[] { "Battlefield 6", "Battlefield™ 6" } },
            new Spec { GameId = "Battlefield2042", Name = "Battlefield 2042", SteamApps = new[] { 1517290 }, Markers = new[] { "BF2042.exe" },
                UninstallNames = new[] { "Battlefield 2042", "Battlefield™ 2042" } },
            new Spec { GameId = "AceCombat8", Name = "ACE COMBAT 8: Wings of Theve", SteamApps = new[] { 2288340 }, Markers = new[] { @"Game\Binaries\Win64\AceCombat8.exe", "start_protected_game.exe" },
                UninstallNames = new[] { "ACE COMBAT 8", "ACE COMBAT™ 8" } },
            new Spec { GameId = "AceCombat7", Name = "ACE COMBAT 7: Skies Unknown", SteamApps = new[] { 502500 }, Markers = new[] { "Ace7Game.exe", @"Ace7Game\Binaries\Win64" },
                UninstallNames = new[] { "ACE COMBAT 7", "ACE COMBAT™ 7: SKIES UNKNOWN" } },
            new Spec { GameId = "ProjectWingman", Name = "Project Wingman", SteamApps = new[] { 895870 }, Markers = new[] { "ProjectWingman.exe", @"ProjectWingman\Binaries\Win64\ProjectWingman-Win64-Shipping.exe" },
                UninstallNames = new[] { "Project Wingman" } },
            new Spec { GameId = "X4", Name = "X4: Foundations", SteamApps = new[] { 392160 }, Markers = new[] { "X4.exe" },
                UninstallNames = new[] { "X4: Foundations", "X4 Foundations" } },
            new Spec { GameId = "EliteDangerous", Name = "Elite Dangerous", SteamApps = new[] { 359320 }, Markers = new[] { "EDLaunch.exe", @"Products\elite-dangerous-odyssey-64\EliteDangerous64.exe", @"Products\elite-dangerous-64\EliteDangerous64.exe" },
                UninstallNames = new[] { "Elite Dangerous" }, Places = w => Existing(Path.Combine(ProgramFilesX86(), "Frontier", "EDLaunch"), Path.Combine(ProgramFiles(), "Frontier", "EDLaunch")) },
            new Spec { GameId = "FalconBMS", Name = "Falcon BMS", Markers = new[] { @"Bin\x64\Falcon BMS.exe", "Launcher.exe" },
                UninstallNames = new[] { "Falcon BMS" }, Places = BmsPlaces },
            new Spec { GameId = "VTOLVR", Name = "VTOL VR (BYOJoystick mod)", SteamApps = new[] { 667970 }, Markers = new[] { "VTOLVR.exe" }, UninstallNames = new[] { "VTOL VR" } },
            new Spec { GameId = "VTOLVRNative", Name = "VTOL VR (native rudder and brakes)", SteamApps = new[] { 667970 }, Markers = new[] { "VTOLVR.exe" }, UninstallNames = new[] { "VTOL VR" } },
            new Spec { GameId = "GRebels", Name = "G-Rebels", SteamApps = new[] { 2445980 }, Markers = new[] { "G_Rebels.exe", @"G_Rebels\Binaries\Win64\G_Rebels-Win64-Shipping.exe" }, UninstallNames = new[] { "G-Rebels" } },
            new Spec { GameId = "TinyCombatArena", Name = "Tiny Combat Arena", SteamApps = new[] { 1347550 }, Markers = new[] { "Arena.exe", @"Arena_Data\Managed\Rewired_Core.dll" }, UninstallNames = new[] { "Tiny Combat Arena" } },
            new Spec { GameId = "NuclearOption", Name = "Nuclear Option", SteamApps = new[] { 2168680 }, Markers = new[] { "NuclearOption.exe" }, UninstallNames = new[] { "Nuclear Option" } },
            // IL-2 1946 / Cliffs of Dover / FSX / Prepar3D: docs/games/il2-1946.md, il2-cliffs-of-dover.md, fsx-p3d.md.
            new Spec { GameId = "IL21946", Name = "IL-2 Sturmovik: 1946", SteamApps = new[] { 15320 }, Markers = new[] { "il2fb.exe" },
                UninstallNames = new[] { "IL-2 Sturmovik: 1946", "IL-2 Sturmovik 1946", "IL 2 Sturmovik 1946" } },
            new Spec { GameId = "IL2CoD", Name = "IL-2 Sturmovik: Cliffs of Dover", SteamApps = new[] { 63950 }, Markers = new[] { "Launcher.exe", "Launcher64.exe" },
                UninstallNames = new[] { "IL-2 Sturmovik: Cliffs of Dover" } },
            new Spec { GameId = "IL2CoDBlitz", Name = "IL-2 Sturmovik: Cliffs of Dover Blitz", SteamApps = new[] { 754530 }, Markers = new[] { "Launcher64.exe" },
                UninstallNames = new[] { "IL-2 Sturmovik: Cliffs of Dover Blitz", "IL-2 Sturmovik: Cliffs of Dover Blitz Edition" } },
            new Spec { GameId = "FSX", Name = "Microsoft Flight Simulator X", Markers = new[] { "fsx.exe" },
                UninstallNames = new[] { "Microsoft Flight Simulator X" },
                Places = w => RegistryFolders(@"SOFTWARE\Microsoft\Microsoft Games\Flight Simulator\10.0", "SetupPath").Concat(RegistryFolders(@"SOFTWARE\Microsoft\Microsoft Games\Flight Simulator\10.0", "AppPath")) },
            new Spec { GameId = "FSXSE", Name = "Microsoft Flight Simulator X: Steam Edition", SteamApps = new[] { 314160 }, Markers = new[] { "fsx.exe" },
                UninstallNames = new[] { "Microsoft Flight Simulator X: Steam Edition" },
                Places = w => RegistryFolders(@"SOFTWARE\Microsoft\Microsoft Games\Flight Simulator - Steam Edition\10.0", "AppPath") },
            new Spec { GameId = "P3D4", Name = "Prepar3D v4", Markers = new[] { "Prepar3D.exe" }, UninstallNames = new[] { "Lockheed Martin Prepar3D v4", "Prepar3D v4" },
                Places = w => RegistryFolders(@"SOFTWARE\Lockheed Martin\Prepar3D v4", "SetupPath") },
            new Spec { GameId = "P3D5", Name = "Prepar3D v5", Markers = new[] { "Prepar3D.exe" }, UninstallNames = new[] { "Lockheed Martin Prepar3D v5", "Prepar3D v5" },
                Places = w => RegistryFolders(@"SOFTWARE\Lockheed Martin\Prepar3D v5", "SetupPath") },
            new Spec { GameId = "P3D6", Name = "Prepar3D v6", Markers = new[] { "Prepar3D.exe" }, UninstallNames = new[] { "Lockheed Martin Prepar3D v6", "Prepar3D v6" },
                Places = w => RegistryFolders(@"SOFTWARE\Lockheed Martin\Prepar3D v6", "SetupPath") },
        };

        // Games that keep their controls inside the installation: their config location follows the installation folder
        // (IL-2 data\input, Falcon BMS User\Config, X-Plane Output\preferences, VTOL VR SaveData).
        private static readonly HashSet<string> ConfigInInstallation = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IL2Game", "IL2Korea", "FalconBMS", "XPlane12", "XPlane11", "VTOLVR", "IL21946"
        };

        private static Dictionary<string, GameInstall> cache;

        public static IEnumerable<string> GameIds
        {
            get { return Specs.Select(s => s.GameId); }
        }

        // The game's Steam app ids (empty when it is not on Steam or unknown).
        public static int[] SteamAppIds(string gameId)
        {
            Spec spec = Specs.FirstOrDefault(s => string.Equals(s.GameId, gameId, StringComparison.OrdinalIgnoreCase));
            return spec == null ? new int[0] : spec.SteamApps;
        }

        public static string DataRoot()
        {
            return global::JoyPro.Logging.JoyProDataFolder.Root();
        }

        // "dcs", "il2", "bms", "Falcon BMS" -> the game id (empty when unknown).
        public static string Resolve(string game)
        {
            if (string.IsNullOrWhiteSpace(game))
                return string.Empty;
            string trimmed = game.Trim();
            Spec spec = Specs.FirstOrDefault(s => string.Equals(s.GameId, trimmed, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (spec != null)
                return spec.GameId;
            switch (trimmed.ToLowerInvariant())
            {
                case "dcs": return "DCS";
                case "il2": case "il-2": case "il2game": return "IL2Game";
                case "sc": case "starcitizen": return "StarCitizen";
            }
            IGameIntegration integration;
            return GameRegistry.TryResolve(trimmed, out integration) ? integration.GameId : string.Empty;
        }

        public static List<GameInstall> All(List<string> warnings)
        {
            return Specs.Select(s => Find(s.GameId, warnings)).ToList();
        }

        // Fills Actions and ActionsNote: a game whose actions JoyPro cannot find anywhere is not offered as supported
        // (the Start page leaves it out; Settings > Games says why).
        public static void CountActions(GameInstall install)
        {
            int count;
            switch (install.GameId)
            {
                case "DCS":
                    string dcs = GameDataPaths.DbFolder("DCS");
                    count = Directory.Exists(dcs) ? Directory.GetFiles(dcs, "*.html").Length : 0;
                    break;
                case "IL2Game":
                    count = File.Exists(Path.Combine(GameDataPaths.DbFolder("IL2"), "IL2.actions")) ? 1 : 0;
                    break;
                case "StarCitizen":
                    count = File.Exists(Path.Combine(GameDataPaths.DbFolder("StarCitizen"), "AllBinds.xml")) ? 1 : 0;
                    break;
                default:
                    IGameIntegration game;
                    count = GameRegistry.TryResolve(install.GameId, out game) ? GameCatalogStore.Load(game, null).Actions.Count : 0;
                    break;
            }
            install.Actions = count;
            install.ActionsNote = count > 0 ? string.Empty
                : install.Installed ? "JoyPro has no command list of it yet: Update on its card builds it from the game's own files."
                : "JoyPro has no command list of it: the list is built from the installed game, and the game was not found on this PC.";
        }

        // Detected again on the next Find (the engine server between two commands: a game may have been installed).
        public static void ResetCache()
        {
            cache = null;
        }

        public static GameInstall Find(string game, List<string> warnings)
        {
            string gameId = Resolve(game);
            if (cache != null && cache.ContainsKey(gameId))
                return cache[gameId];
            Spec spec = Specs.FirstOrDefault(s => s.GameId == gameId);
            GameInstall install = new GameInstall { GameId = gameId.Length > 0 ? gameId : game ?? string.Empty, Name = spec?.Name ?? game ?? string.Empty };
            if (spec == null)
            {
                install.Note = "JoyPro does not know this game.";
                return install;
            }
            install.Markers = spec.Markers.ToList();
            install.ConfigInInstallation = ConfigInInstallation.Contains(spec.GameId);
            string configOverride;
            if (ConfigOverrides().TryGetValue(spec.GameId, out configOverride) && !string.IsNullOrWhiteSpace(configOverride))
            {
                install.ConfigOverridden = true;
                install.ConfigOverride = configOverride;
            }

            string overridden;
            if (Overrides().TryGetValue(spec.GameId, out overridden) && !string.IsNullOrWhiteSpace(overridden))
            {
                // The user may have picked the game's executable, its launcher or a folder inside it (older versions
                // saved such a path as it was): the installation is the folder with the marker.
                string folder = ResolveInstallFolder(spec.Markers, overridden);
                install.Overridden = true;
                install.InstallPath = folder;
                install.Source = "override";
                install.Installed = Directory.Exists(folder);
                install.MarkerFound = HasMarker(spec.Markers, folder);
                install.Note = !install.Installed
                    ? "The folder you set does not exist (any more)."
                    : install.MarkerFound ? string.Empty : "Set by you; " + spec.Markers[0] + " is not in this folder.";
            }
            else if (Environment.GetEnvironmentVariable(DetectionVariable) != "off")
            {
                foreach (KeyValuePair<string, string> candidate in Candidates(spec, warnings))
                {
                    if (!Directory.Exists(candidate.Key))
                        continue;
                    // A launcher folder (uninstall entry of a standalone installer) may keep the game in a subfolder.
                    string folder = ResolveInstallFolder(spec.Markers, candidate.Key, false);
                    bool marker = HasMarker(spec.Markers, folder);
                    // Steam's manifest is proof enough; other places need the game's executable.
                    if (!marker && candidate.Value != "steam")
                        continue;
                    install.Installed = true;
                    install.InstallPath = folder;
                    install.Source = candidate.Value;
                    install.MarkerFound = marker;
                    break;
                }
                if (!install.Installed)
                    install.Note = "Not found. If it is installed, set its folder (Settings > Games, or engine games install set " + spec.GameId + " <folder>).";
            }
            else
            {
                install.Note = "Install detection is off (" + DetectionVariable + "); only folders you set count.";
            }

            if (Environment.GetEnvironmentVariable(AssumeInstalledVariable) == "1" && !install.Installed)
            {
                install.Installed = true;
                install.Source = "test";
            }
            // The user said they do not have the game: it counts as not installed, wherever it was found.
            if (Disabled().Contains(spec.GameId))
            {
                install.Disabled = true;
                install.Installed = false;
                install.Note = "You do not have this game (your choice in Settings > Games); JoyPro hides it. " +
                               "To use it: Settings > Games, or engine games install enable " + spec.GameId + ".";
            }
            if (cache == null)
                cache = new Dictionary<string, GameInstall>(StringComparer.OrdinalIgnoreCase);
            cache[spec.GameId] = install;
            JoyPro.Logging.JoyProLog.Debug("Installs", spec.GameId + ": " + (install.Installed ? "installed at " + install.InstallPath + " (" + install.Source + (install.MarkerFound ? ", marker found" : ", no marker") + ")" : "not installed") +
                                                       (install.Disabled ? ", hidden by the user" : string.Empty) +
                                                       (install.ConfigOverridden ? ", config set by the user: " + install.ConfigOverride : string.Empty) +
                                                       (string.IsNullOrEmpty(install.Note) ? string.Empty : " | " + install.Note));
            return install;
        }

        // The user's folder for the game when it exists, else null.
        public static string OverrideFolder(string game)
        {
            string gameId = Resolve(game);
            string folder;
            if (!Overrides().TryGetValue(gameId, out folder) || string.IsNullOrWhiteSpace(folder))
                return null;
            Spec spec = Specs.FirstOrDefault(s => s.GameId == gameId);
            if (spec != null)
                folder = ResolveInstallFolder(spec.Markers, folder);
            return Directory.Exists(folder) ? folder : null;
        }

        // The game's markers (empty for an unknown game).
        public static List<string> MarkersOf(string game)
        {
            Spec spec = Specs.FirstOrDefault(s => s.GameId == Resolve(game));
            return spec == null ? new List<string>() : spec.Markers.ToList();
        }

        internal static bool HasMarker(IEnumerable<string> markers, string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return false;
            try
            {
                return markers.Any(m => File.Exists(Path.Combine(folder, m)) || Directory.Exists(Path.Combine(folder, m)));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The installation folder for what the user picked (or a place detection found): the folder itself when one of
        // the game's markers is in it; else a subfolder that has one (the standalone IL-2 Korea keeps the game in
        // <launcher folder>\game; "game" is tried first); else, for a picked path, a parent folder that has one (the
        // user picked bin\game\IL2Series.exe, bin\DCS.exe or data\input). A file counts as its folder. When no marker is
        // found anywhere near, the picked folder stays as it is (the GUI then says the marker is missing).
        internal static string ResolveInstallFolder(IEnumerable<string> markers, string path, bool searchParents = true)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            List<string> markerList = (markers ?? Enumerable.Empty<string>()).ToList();
            string folder = path.Trim().Trim('"');
            try
            {
                folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder));
                if (File.Exists(folder))
                    folder = Path.GetDirectoryName(folder) ?? folder;
                folder = folder.TrimEnd('\\', '/');
                if (folder.EndsWith(":", StringComparison.Ordinal))
                    folder += "\\";
                if (markerList.Count == 0 || !Directory.Exists(folder) || HasMarker(markerList, folder))
                    return folder;
                List<string> children = Directory.GetDirectories(folder)
                    .OrderBy(d => string.Equals(Path.GetFileName(d), "game", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                string child = children.FirstOrDefault(d => HasMarker(markerList, d));
                if (child != null)
                    return child;
                if (searchParents)
                {
                    DirectoryInfo parent = Directory.GetParent(folder);
                    for (int level = 0; level < 4 && parent != null; level++, parent = parent.Parent)
                    {
                        if (HasMarker(markerList, parent.FullName))
                            return parent.FullName;
                        string game = Path.Combine(parent.FullName, "game");
                        if (HasMarker(markerList, game))
                            return game;
                    }
                }
            }
            catch (Exception)
            {
                // No access to a folder: what was picked stays the folder.
            }
            return folder;
        }

        // The user's folder alone when set, else what detection finds.
        public static List<string> OverrideOr(string game, Func<List<string>> detect)
        {
            string folder = OverrideFolder(game);
            return folder != null ? new List<string> { folder } : detect();
        }

        // The user's config location for the game (file or folder) when it exists, else null.
        public static string ConfigOverride(string game)
        {
            string path;
            return ConfigOverrides().TryGetValue(Resolve(game), out path) && (Directory.Exists(path) || File.Exists(path)) ? path : null;
        }

        // The user's config location first, then what discovery found.
        public static List<string> WithConfigOverride(string game, List<string> found)
        {
            string own = ConfigOverride(game);
            if (own == null)
                return found ?? new List<string>();
            List<string> paths = new List<string> { own };
            paths.AddRange((found ?? new List<string>()).Where(p => !string.Equals(p, own, StringComparison.OrdinalIgnoreCase)));
            return paths;
        }

        // Where the game's controls are saved (with the user's folder for games that keep them in the installation, and
        // the user's config location first).
        public static List<string> ConfigPaths(string game, List<string> warnings)
        {
            string gameId = Resolve(game);
            List<string> paths = new List<string>();
            string own = ConfigOverride(gameId);
            if (own != null)
                paths.Add(own);
            try
            {
                switch (gameId)
                {
                    case "DCS":
                        paths.AddRange(GamePathDiscovery.DiscoverDcsSavedGamesPaths(warnings));
                        break;
                    case "IL2Game":
                        paths.AddRange(GamePathDiscovery.DiscoverIl2ConfigPaths(warnings));
                        break;
                    case "StarCitizen":
                        paths.AddRange(GamePathDiscovery.DiscoverStarCitizenConfigPaths(warnings));
                        break;
                    default:
                        IGameIntegration integration;
                        if (GameRegistry.TryResolve(gameId, out integration))
                            paths.AddRange(integration.DiscoverConfigPaths(warnings));
                        break;
                }
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Warn("Installs", gameId + " config paths", ex);
                warnings?.Add(gameId + " config paths: " + ex.Message);
            }
            List<string> distinct = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            JoyPro.Logging.JoyProLog.Debug("Installs", gameId + " config paths: " + (distinct.Count == 0 ? "(none found)" : string.Join(" ; ", distinct)) +
                                                       (own != null ? " (first one set by the user)" : string.Empty));
            return distinct;
        }

        // Null when the game may be used; otherwise the message for the refused command.
        public static string RefuseIfMissing(string game, string action, List<string> warnings)
        {
            GameInstall install = Find(game, warnings);
            if (install.Installed || install.GameId.Length == 0 || !Specs.Any(s => s.GameId == install.GameId))
                return null;
            if (install.Disabled)
                return "You said you do not have " + install.Name + " (Settings > Games), so JoyPro does not " + action + " it. " +
                       "If you have it: \"I have it\" in Settings > Games in JoyPro, or engine games install enable " + install.GameId + ".";
            return install.Name + " is not installed (not found on this PC), so JoyPro does not " + action + " it. " +
                   "If it is installed, set its folder: Settings > Games in JoyPro, or engine games install set " + install.GameId + " \"<folder>\".";
        }

        public static GameInstall SetOverride(string game, string folder, List<string> warnings)
        {
            string gameId = Resolve(game);
            if (!Specs.Any(s => s.GameId == gameId))
                throw new ArgumentException("Unknown game: " + game);
            Dictionary<string, string> overrides = Overrides();
            if (string.IsNullOrWhiteSpace(folder))
                overrides.Remove(gameId);
            else
            {
                string picked = Path.GetFullPath(folder.Trim().Trim('"'));
                string resolved = ResolveInstallFolder(Specs.First(s => s.GameId == gameId).Markers, picked);
                overrides[gameId] = resolved;
                if (!string.Equals(resolved.TrimEnd('\\'), picked.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    warnings?.Add("You picked " + picked + "; JoyPro uses the game's folder " + resolved + ".");
            }
            SaveMap(OverridesFile, overrides);
            cache = null;
            return Find(gameId, warnings);
        }

        // The user's config location (file or folder); empty or null forgets it. Games that keep their controls in the
        // installation take the installation folder instead.
        public static GameInstall SetConfigOverride(string game, string path, List<string> warnings)
        {
            string gameId = Resolve(game);
            if (!Specs.Any(s => s.GameId == gameId))
                throw new ArgumentException("Unknown game: " + game);
            if (!string.IsNullOrWhiteSpace(path) && ConfigInInstallation.Contains(gameId))
                throw new ArgumentException(Specs.First(s => s.GameId == gameId).Name + " keeps its controls in its installation folder; " +
                                            "set that instead: engine games install set " + gameId + " <folder>.");
            Dictionary<string, string> overrides = ConfigOverrides();
            if (string.IsNullOrWhiteSpace(path))
                overrides.Remove(gameId);
            else
                overrides[gameId] = Path.GetFullPath(path.Trim().Trim('"'));
            SaveMap(ConfigOverridesFile, overrides);
            cache = null;
            return Find(gameId, warnings);
        }

        // "I do not have this game" (disabled) or "I have it" again, for several games at once.
        public static List<GameInstall> SetDisabled(IEnumerable<string> games, bool disabled, List<string> warnings)
        {
            List<string> requested = games.ToList();
            string unknown = requested.FirstOrDefault(g => !Specs.Any(s => s.GameId == Resolve(g)));
            List<string> ids = requested.Select(Resolve).ToList();
            if (unknown != null)
                throw new ArgumentException("Unknown game: " + unknown);
            HashSet<string> set = Disabled();
            foreach (string id in ids)
            {
                if (disabled)
                    set.Add(id);
                else
                    set.Remove(id);
            }
            Directory.CreateDirectory(DataRoot());
            StringBuilder sb = new StringBuilder();
            sb.Append("[");
            List<string> sorted = set.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < sorted.Count; i++)
                sb.Append(i == 0 ? "\r\n  \"" : ",\r\n  \"").Append(sorted[i]).Append('"');
            sb.Append(sorted.Count > 0 ? "\r\n]\r\n" : "]\r\n");
            File.WriteAllText(Path.Combine(DataRoot(), DisabledFile), sb.ToString(), new UTF8Encoding(false));
            cache = null;
            return ids.Distinct(StringComparer.OrdinalIgnoreCase).Select(id => Find(id, warnings)).ToList();
        }

        // The games the user said they do not have.
        public static HashSet<string> Disabled()
        {
            HashSet<string> disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string file = Path.Combine(DataRoot(), DisabledFile);
            if (!File.Exists(file))
                return disabled;
            try
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(file), "\"(?<game>[^\"]+)\""))
                {
                    string game = match.Groups["game"].Value;
                    // "I do not have it" for a game that is one game per version now (MSFS, XPlane): every version.
                    if (LegacyGameIds.IsLegacy(game))
                        disabled.UnionWith(LegacyGameIds.Expand(game));
                    else
                        disabled.Add(game);
                }
            }
            catch (Exception)
            {
                // A broken file counts as no disabled games; the next change rewrites it.
            }
            return disabled;
        }

        public static Dictionary<string, string> Overrides()
        {
            return MigrateLegacyKeys(ReadMap(OverridesFile));
        }

        public static Dictionary<string, string> ConfigOverrides()
        {
            return MigrateLegacyKeys(ReadMap(ConfigOverridesFile));
        }

        // A folder (or config location) set for a game that is one game per version now (MSFS, XPlane) belongs to the
        // version it shows; the next save writes it under that version.
        private static Dictionary<string, string> MigrateLegacyKeys(Dictionary<string, string> map)
        {
            foreach (string legacy in map.Keys.Where(LegacyGameIds.IsLegacy).ToList())
            {
                string path = map[legacy];
                string target = LegacyVersionOf(legacy, path);
                map.Remove(legacy);
                if (!map.ContainsKey(target))
                    map[target] = path;
            }
            return map;
        }

        private static string LegacyVersionOf(string legacy, string path)
        {
            string value = (path ?? string.Empty).Trim();
            if (string.Equals(legacy, "XPlane", StringComparison.OrdinalIgnoreCase))
                return XPlaneIntegration.VersionOfRoot(value) == 11 || value.IndexOf("X-Plane 11", StringComparison.OrdinalIgnoreCase) >= 0 ? "XPlane11" : "XPlane12";
            bool is2020 = File.Exists(Path.Combine(value, "FlightSimulator.exe")) || value.IndexOf("1250410", StringComparison.Ordinal) >= 0 ||
                          value.IndexOf("Microsoft.FlightSimulator_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          (value.IndexOf("2024", StringComparison.Ordinal) < 0 && value.IndexOf("Limitless", StringComparison.OrdinalIgnoreCase) < 0 &&
                           !File.Exists(Path.Combine(value, "FlightSimulator2024.exe")));
            return is2020 ? "MSFS2020" : "MSFS2024";
        }

        private static Dictionary<string, string> ReadMap(string fileName)
        {
            Dictionary<string, string> overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string file = Path.Combine(DataRoot(), fileName);
            if (!File.Exists(file))
                return overrides;
            try
            {
                string text = File.ReadAllText(file);
                foreach (Match match in Regex.Matches(text, "\"(?<game>[^\"]+)\"\\s*:\\s*\"(?<path>(?:\\\\.|[^\"])*)\""))
                    overrides[match.Groups["game"].Value] = Regex.Unescape(match.Groups["path"].Value);
            }
            catch (Exception)
            {
                // A broken file counts as no overrides; the next set rewrites it.
            }
            return overrides;
        }

        private static void SaveMap(string fileName, Dictionary<string, string> overrides)
        {
            Directory.CreateDirectory(DataRoot());
            StringBuilder sb = new StringBuilder();
            sb.Append("{\r\n");
            List<KeyValuePair<string, string>> entries = overrides.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < entries.Count; i++)
            {
                sb.Append("  \"").Append(entries[i].Key).Append("\": \"")
                  .Append(entries[i].Value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"')
                  .Append(i < entries.Count - 1 ? ",\r\n" : "\r\n");
            }
            sb.Append("}\r\n");
            File.WriteAllText(Path.Combine(DataRoot(), fileName), sb.ToString(), new UTF8Encoding(false));
        }

        // Candidate folders with where they come from, best first.
        private static IEnumerable<KeyValuePair<string, string>> Candidates(Spec spec, List<string> warnings)
        {
            foreach (string folder in SteamFolders(spec.SteamApps, warnings))
                yield return new KeyValuePair<string, string>(folder, "steam");
            IEnumerable<string> places = Enumerable.Empty<string>();
            try
            {
                if (spec.Places != null)
                    places = spec.Places(warnings).ToList();
            }
            catch (Exception ex)
            {
                warnings?.Add(spec.Name + ": " + ex.Message);
            }
            foreach (string folder in places)
                yield return new KeyValuePair<string, string>(folder, "known place");
            foreach (string folder in UninstallFolders(spec.UninstallNames, spec.UninstallKeys))
                yield return new KeyValuePair<string, string>(folder, "Windows apps list");
        }

        // IL-2 Korea outside Steam: the standalone installer's default folder (Program Files\IL2Series) and the usual
        // game folders on every fixed drive; a Steam library folder whose manifest is gone (copied install) as well.
        // Each holds the game in its "game" subfolder (standalone) or is the game folder (Steam).
        private static IEnumerable<string> Il2KoreaPlaces(List<string> warnings)
        {
            List<string> folders = new List<string>();
            if (TestDrives() == null)
            {
                folders.Add(Path.Combine(ProgramFiles(), "IL2Series"));
                folders.Add(Path.Combine(ProgramFilesX86(), "IL2Series"));
            }
            try
            {
                foreach (string root in FixedDriveRoots())
                {
                    foreach (string name in new[] { "IL2Series", "Korea IL2 Series", "Korea. IL2 Series", "IL-2 Series Korea", "IL-2 Sturmovik Korea" })
                    {
                        folders.Add(Path.Combine(root, name));
                        folders.Add(Path.Combine(root, "Games", name));
                        folders.Add(Path.Combine(root, "Program Files", name));
                        folders.Add(Path.Combine(root, "Program Files (x86)", name));
                    }
                }
            }
            catch (Exception ex)
            {
                warnings?.Add("IL-2 Korea drive probe failed: " + ex.Message);
            }
            try
            {
                foreach (string library in GamePathDiscovery.GetSteamLibraryRoots(warnings ?? new List<string>()))
                    folders.Add(Path.Combine(library, "steamapps", "common", "IL2Series"));
            }
            catch (Exception ex)
            {
                warnings?.Add("IL-2 Korea Steam libraries: " + ex.Message);
            }
            return folders.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
        }

        // Test hook: these folders stand in for the PC's fixed drives (Steam libraries <drive>\SteamLibrary, game folders);
        // the registry (Steam, uninstall entries) is then not read, see FakeUninstallVariable.
        public const string TestDrivesVariable = "JOYPRO_TEST_DRIVES";

        internal static List<string> TestDrives()
        {
            string value = Environment.GetEnvironmentVariable(TestDrivesVariable);
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return value.Split(';').Select(v => v.Trim().Trim('"')).Where(v => v.Length > 0).ToList();
        }

        internal static List<string> FixedDriveRoots()
        {
            List<string> test = TestDrives();
            if (test != null)
                return test;
            try
            {
                return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        // "[key]" lines start an entry, "Name=Value" lines fill it ("DisplayName", "InstallLocation", "Inno Setup: App Path").
        private static List<KeyValuePair<string, Dictionary<string, string>>> ReadFakeUninstallEntries(string file)
        {
            List<KeyValuePair<string, Dictionary<string, string>>> entries = new List<KeyValuePair<string, Dictionary<string, string>>>();
            if (!File.Exists(file))
                return entries;
            Dictionary<string, string> current = null;
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    entries.Add(new KeyValuePair<string, Dictionary<string, string>>(line.Substring(1, line.Length - 2), current));
                    continue;
                }
                int eq = line.IndexOf('=');
                if (current != null && eq > 0)
                    current[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return entries;
        }

        // The installation folder of one uninstall entry when it belongs to the game (by its shown name or its key
        // name), else empty. Inno Setup also keeps the folder as "Inno Setup: App Path".
        private static string UninstallLocation(string keyName, IDictionary<string, string> values, string[] names, string[] keys)
        {
            string display, location, innoPath;
            values.TryGetValue("DisplayName", out display);
            values.TryGetValue("InstallLocation", out location);
            values.TryGetValue("Inno Setup: App Path", out innoPath);
            display = display ?? string.Empty;
            bool ours = names.Any(n => display.StartsWith(n, StringComparison.OrdinalIgnoreCase)) ||
                        keys.Any(k => string.Equals(k, keyName, StringComparison.OrdinalIgnoreCase));
            if (!ours)
                return string.Empty;
            string folder = !string.IsNullOrWhiteSpace(location) ? location : innoPath ?? string.Empty;
            return folder.Trim().Trim('"');
        }

        internal static List<string> SteamFolders(int[] appIds, List<string> warnings)
        {
            List<string> folders = new List<string>();
            if (appIds.Length == 0)
                return folders;
            foreach (string library in GamePathDiscovery.GetSteamLibraryRoots(warnings ?? new List<string>()))
            {
                foreach (int appId in appIds)
                {
                    string manifest = Path.Combine(library, "steamapps", "appmanifest_" + appId.ToString(CultureInfo.InvariantCulture) + ".acf");
                    if (!File.Exists(manifest))
                        continue;
                    try
                    {
                        Match dir = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
                        if (dir.Success)
                            folders.Add(Path.Combine(library, "steamapps", "common", dir.Groups[1].Value));
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            return folders;
        }

        // Test hook: a .reg-like text file of uninstall entries ("[key]" then "Name=Value" lines) read instead of the
        // registry, so detection can be tested with fake entries.
        public const string FakeUninstallVariable = "JOYPRO_TEST_UNINSTALL_FILE";

        private static IEnumerable<string> UninstallFolders(string[] names, string[] keys)
        {
            List<string> folders = new List<string>();
            if (names.Length == 0 && keys.Length == 0)
                return folders;
            string fake = Environment.GetEnvironmentVariable(FakeUninstallVariable);
            if (!string.IsNullOrWhiteSpace(fake))
            {
                foreach (KeyValuePair<string, Dictionary<string, string>> entry in ReadFakeUninstallEntries(fake))
                {
                    string location = UninstallLocation(entry.Key, entry.Value, names, keys);
                    if (location.Length > 0)
                        folders.Add(location);
                }
                return folders;
            }
            if (TestDrives() != null)
                return folders;
            string[] roots = { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (string root in roots)
                {
                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                        using (RegistryKey uninstall = baseKey.OpenSubKey(root))
                        {
                            if (uninstall == null)
                                continue;
                            foreach (string sub in uninstall.GetSubKeyNames())
                            {
                                using (RegistryKey app = uninstall.OpenSubKey(sub))
                                {
                                    if (app == null)
                                        continue;
                                    Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                    {
                                        { "DisplayName", app.GetValue("DisplayName") as string ?? string.Empty },
                                        { "InstallLocation", app.GetValue("InstallLocation") as string ?? string.Empty },
                                        { "Inno Setup: App Path", app.GetValue("Inno Setup: App Path") as string ?? string.Empty }
                                    };
                                    string location = UninstallLocation(sub, values, names, keys);
                                    if (location.Length > 0)
                                        folders.Add(location);
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // No access to a hive: the other places still count.
                    }
                }
            }
            return folders;
        }

        private static IEnumerable<string> RegistryFolders(string keyPath, string valueName)
        {
            List<string> folders = new List<string>();
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey key = baseKey.OpenSubKey(keyPath))
                    {
                        string value = key?.GetValue(valueName) as string;
                        if (!string.IsNullOrWhiteSpace(value))
                            folders.Add(value.Trim().Trim('"'));
                    }
                }
                catch (Exception)
                {
                }
            }
            return folders;
        }

        // Falcon BMS: HKLM\SOFTWARE\(WOW6432Node\)Benchmark Sims\Falcon BMS 4.xx -> baseDir, and C:\Falcon BMS 4.xx.
        private static IEnumerable<string> BmsPlaces(List<string> warnings)
        {
            List<string> folders = new List<string>();
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey sims = baseKey.OpenSubKey(@"SOFTWARE\Benchmark Sims"))
                    {
                        if (sims == null)
                            continue;
                        foreach (string version in sims.GetSubKeyNames().OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase))
                        {
                            using (RegistryKey key = sims.OpenSubKey(version))
                            {
                                string dir = key?.GetValue("baseDir") as string;
                                if (!string.IsNullOrWhiteSpace(dir))
                                    folders.Add(dir.Trim().Trim('"'));
                            }
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                try
                {
                    folders.AddRange(Directory.GetDirectories(drive.RootDirectory.FullName, "Falcon BMS*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase));
                }
                catch (Exception)
                {
                }
            }
            return folders;
        }

        // Microsoft Store / Xbox app: the game's package folder holds UserCfg.opt (2024: Microsoft.Limitless, 2020:
        // Microsoft.FlightSimulator), as does the sim's AppData folder.
        private static IEnumerable<string> MsfsPlaces(int sim)
        {
            string packages = Path.Combine(LocalAppData(), "Packages");
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return sim == 2020
                ? Existing(Path.Combine(packages, "Microsoft.FlightSimulator_8wekyb3d8bbwe"), Path.Combine(roaming, "Microsoft Flight Simulator"))
                : Existing(Path.Combine(packages, "Microsoft.Limitless_8wekyb3d8bbwe"), Path.Combine(roaming, "Microsoft Flight Simulator 2024"));
        }

        private static IEnumerable<string> Existing(params string[] folders)
        {
            return folders.Where(Directory.Exists).ToList();
        }

        private static string LocalAppData()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        private static string ProgramFiles()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }

        private static string ProgramFilesX86()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        }

        public static string ToText(List<GameInstall> installs)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Game Installs");
            foreach (GameInstall install in installs)
            {
                sb.AppendLine("  " + install.GameId + " (" + install.Name + "): " + (install.Disabled ? "you do not have it" : install.Installed ? "installed" : "not installed") +
                              (install.InstallPath.Length > 0 ? " - " + install.InstallPath : string.Empty) +
                              (install.Source.Length > 0 ? " [" + install.Source + "]" : string.Empty));
                if (install.ConfigOverridden)
                    sb.AppendLine("    Config location (yours): " + install.ConfigOverride);
                if (install.Note.Length > 0)
                    sb.AppendLine("    " + install.Note);
            }
            return sb.ToString().TrimEnd();
        }
    }

    public sealed class GameInstall
    {
        public string GameId { get; set; }
        public string Name { get; set; }
        public bool Installed { get; set; }
        public string InstallPath { get; set; }
        // steam, known place, Windows apps list, override (or test).
        public string Source { get; set; }
        public bool Overridden { get; set; }
        // The game's executable (or another marker file) is in the folder.
        public bool MarkerFound { get; set; }
        // The files (relative to the installation folder) that prove a folder is the game's installation.
        public List<string> Markers { get; set; }
        // The user said they do not have the game (counts as not installed).
        public bool Disabled { get; set; }
        // The controls are kept in the installation folder (the config location follows the installation).
        public bool ConfigInInstallation { get; set; }
        // The user's config location (game-configs.json); it comes first in ConfigPaths when it exists.
        public bool ConfigOverridden { get; set; }
        public string ConfigOverride { get; set; }
        public List<string> ConfigPaths { get; set; }
        public string Note { get; set; }
        // How many actions JoyPro knows for the game on this PC (its command list, or JoyPro's own table; DCS: the
        // aircraft lists, IL-2 / Star Citizen: 1 when the list is there). -1 = not counted (engine games installs counts).
        public int Actions { get; set; }
        // Why there are none (empty when there are).
        public string ActionsNote { get; set; }

        public GameInstall()
        {
            Actions = -1;
            GameId = Name = InstallPath = Source = Note = ConfigOverride = ActionsNote = string.Empty;
            Markers = new List<string>();
            ConfigPaths = new List<string>();
        }
    }
}
