using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    internal static class GamePathDiscovery
    {
        private static readonly Guid SavedGamesFolderId = new Guid("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4");

        public static GamePathCatalog DiscoverAll(List<string> warnings = null)
        {
            List<string> collector = warnings ?? new List<string>();

            GamePathCatalog catalog = new GamePathCatalog();
            catalog.Games.Add(DiscoverDcs(collector));
            catalog.Games.Add(DiscoverIl2(collector));
            catalog.Games.Add(DiscoverStarCitizen(collector));
            catalog.Warnings = collector
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (GamePathEntry entry in catalog.Games)
                JoyPro.Logging.JoyProLog.Debug("Paths", entry.Game + ": install " + (entry.InstallPaths.Count == 0 ? "(none)" : string.Join(" ; ", entry.InstallPaths)) +
                                                        " | config " + (entry.ConfigPaths.Count == 0 ? "(none)" : string.Join(" ; ", entry.ConfigPaths)));
            if (catalog.Warnings.Count > 0)
                JoyPro.Logging.JoyProLog.Debug("Paths", "Discovery warnings: " + string.Join(" | ", catalog.Warnings));
            return catalog;
        }

        public static GamePathEntry DiscoverDcs(List<string> warnings = null)
        {
            GamePathEntry entry = new GamePathEntry();
            entry.Game = "DCS";
            entry.InstallPaths = DiscoverDcsInstallPaths(warnings);
            entry.ConfigPaths = DiscoverDcsSavedGamesPaths(warnings);
            entry.PreferredInstallPath = entry.InstallPaths.FirstOrDefault() ?? string.Empty;
            entry.PreferredConfigPath = entry.ConfigPaths.FirstOrDefault() ?? string.Empty;
            return entry;
        }

        public static GamePathEntry DiscoverIl2(List<string> warnings = null)
        {
            GamePathEntry entry = new GamePathEntry();
            entry.Game = "IL2Game";
            entry.InstallPaths = DiscoverIl2InstallPaths(warnings);
            entry.ConfigPaths = DiscoverIl2ConfigPaths(warnings);
            entry.PreferredInstallPath = entry.InstallPaths.FirstOrDefault() ?? string.Empty;
            entry.PreferredConfigPath = entry.ConfigPaths.FirstOrDefault() ?? string.Empty;
            return entry;
        }

        public static GamePathEntry DiscoverStarCitizen(List<string> warnings = null)
        {
            GamePathEntry entry = new GamePathEntry();
            entry.Game = "StarCitizen";
            entry.InstallPaths = DiscoverStarCitizenInstallPaths(warnings);
            entry.ConfigPaths = DiscoverStarCitizenConfigPaths(warnings);
            entry.PreferredInstallPath = entry.InstallPaths.FirstOrDefault() ?? string.Empty;
            entry.PreferredConfigPath = entry.ConfigPaths.FirstOrDefault() ?? string.Empty;
            return entry;
        }

        public static List<string> DiscoverDcsSavedGamesPaths(List<string> warnings = null)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> roots = GetSavedGamesRoots(warnings);
            for (int i = 0; i < roots.Count; i++)
            {
                string root = roots[i];
                if (!Directory.Exists(root))
                    continue;

                try
                {
                    string[] subDirs = Directory.GetDirectories(root, "DCS*", SearchOption.TopDirectoryOnly);
                    for (int j = 0; j < subDirs.Length; j++)
                        AddDirectoryCandidate(output, subDirs[j]);
                }
                catch (Exception ex)
                {
                    warnings?.Add("DCS Saved Games discovery failed at '" + root + "': " + ex.Message);
                }
            }

            // The user's config location (Settings > Games, engine games config set) comes first.
            return GameInstalls.WithConfigOverride("DCS", output
                .OrderBy(path => ScoreDcsSavedGamesPath(path))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList());
        }

        // The user's folder (Settings > Games) replaces detection.
        public static List<string> DiscoverDcsInstallPaths(List<string> warnings = null)
        {
            return GameInstalls.OverrideOr("DCS", () => DetectDcsInstallPaths(warnings));
        }

        internal static List<string> DetectDcsInstallPaths(List<string> warnings = null)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] dcsKeys =
            {
                @"SOFTWARE\Eagle Dynamics\DCS World",
                @"SOFTWARE\Eagle Dynamics\DCS World OpenBeta"
            };
            for (int i = 0; i < dcsKeys.Length; i++)
            {
                string subKey = dcsKeys[i];
                AddRegistryDirectoryCandidates(output, RegistryHive.CurrentUser, subKey, "Path", warnings);
                AddRegistryDirectoryCandidates(output, RegistryHive.LocalMachine, subKey, "Path", warnings);
            }

            const string dcsSteamUninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 223750";
            AddRegistryDirectoryCandidates(output, RegistryHive.CurrentUser, dcsSteamUninstall, "InstallLocation", warnings);
            AddRegistryDirectoryCandidates(output, RegistryHive.LocalMachine, dcsSteamUninstall, "InstallLocation", warnings);

            List<string> roots = GetProgramFilesRoots(warnings);
            for (int i = 0; i < roots.Count; i++)
            {
                string root = roots[i];
                AddDirectoryCandidate(output, Path.Combine(root, "Eagle Dynamics", "DCS World"));
                AddDirectoryCandidate(output, Path.Combine(root, "Eagle Dynamics", "DCS World OpenBeta"));
            }

            List<string> steamRoots = GetSteamLibraryRoots(warnings);
            for (int i = 0; i < steamRoots.Count; i++)
            {
                string root = steamRoots[i];
                AddDirectoryCandidate(output, Path.Combine(root, "steamapps", "common", "DCSWorld"));
                AddDirectoryCandidate(output, Path.Combine(root, "steamapps", "common", "DCS World"));
                AddDirectoryCandidate(output, Path.Combine(root, "steamapps", "common", "DCS World OpenBeta"));
            }

            return output
                .OrderBy(path => ScoreDcsInstallPath(path))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<string> DiscoverIl2InstallPaths(List<string> warnings = null)
        {
            return GameInstalls.OverrideOr("IL2Game", () => DetectIl2InstallPaths(warnings));
        }

        internal static List<string> DetectIl2InstallPaths(List<string> warnings = null)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            const string il2SteamUninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 307960";
            AddRegistryDirectoryCandidates(output, RegistryHive.CurrentUser, il2SteamUninstall, "InstallLocation", warnings);
            AddRegistryDirectoryCandidates(output, RegistryHive.LocalMachine, il2SteamUninstall, "InstallLocation", warnings);

            List<string> steamRoots = GetSteamLibraryRoots(warnings);
            for (int i = 0; i < steamRoots.Count; i++)
            {
                string root = steamRoots[i];
                AddDirectoryCandidate(output, Path.Combine(root, "steamapps", "common", "IL-2 Sturmovik Battle of Stalingrad"));
                AddDirectoryCandidate(output, Path.Combine(root, "steamapps", "common", "IL-2 Sturmovik Great Battles"));
                AddDirectoryCandidate(output, Path.Combine(root, "steamapps", "common", "IL-2 Sturmovik Battle of Moscow"));
            }

            return output
                .OrderBy(path => ScoreIl2InstallPath(path))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<string> DiscoverIl2ConfigPaths(List<string> warnings = null)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> installs = DiscoverIl2InstallPaths(warnings);
            for (int i = 0; i < installs.Count; i++)
            {
                string configPath = Path.Combine(installs[i], "data", "input");
                AddDirectoryCandidate(output, configPath);
            }

            return output
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<string> DiscoverStarCitizenInstallPaths(List<string> warnings = null)
        {
            return GameInstalls.OverrideOr("StarCitizen", () => DetectStarCitizenInstallPaths(warnings));
        }

        internal static List<string> DetectStarCitizenInstallPaths(List<string> warnings = null)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddStarCitizenRootsFromGameConfigStore(output, warnings);

            List<string> roots = GetProgramFilesRoots(warnings);
            for (int i = 0; i < roots.Count; i++)
            {
                string root = roots[i];
                AddDirectoryCandidate(output, Path.Combine(root, "Roberts Space Industries", "StarCitizen"));
                AddDirectoryCandidate(output, Path.Combine(root, "Cloud Imperium Games", "StarCitizen"));
            }

            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    DriveInfo drive = drives[i];
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                        continue;
                    AddDirectoryCandidate(output, Path.Combine(drive.RootDirectory.FullName, "Roberts Space Industries", "StarCitizen"));
                }
            }
            catch (Exception ex)
            {
                warnings?.Add("Star Citizen drive probe failed: " + ex.Message);
            }

            return output
                .OrderBy(path => ScoreStarCitizenInstallPath(path))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<string> DiscoverStarCitizenConfigPaths(List<string> warnings = null)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> installs = DiscoverStarCitizenInstallPaths(warnings);
            for (int i = 0; i < installs.Count; i++)
            {
                string install = installs[i];
                if (!Directory.Exists(install))
                    continue;

                try
                {
                    string[] branches = Directory.GetDirectories(install);
                    for (int j = 0; j < branches.Length; j++)
                    {
                        string branch = branches[j];
                        string defaultProfilePath = Path.Combine(branch, "USER", "Client", "0", "Profiles", "default");
                        string profilesPath = Path.Combine(branch, "USER", "Client", "0", "Profiles");
                        if (Directory.Exists(defaultProfilePath))
                            AddDirectoryCandidate(output, defaultProfilePath);
                        else if (Directory.Exists(profilesPath))
                            AddDirectoryCandidate(output, profilesPath);
                    }
                }
                catch (Exception ex)
                {
                    warnings?.Add("Star Citizen profile discovery failed at '" + install + "': " + ex.Message);
                }
            }

            return GameInstalls.WithConfigOverride("StarCitizen", output
                .OrderBy(path => ScoreStarCitizenConfigPath(path))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList());
        }

        private static int ScoreDcsSavedGamesPath(string path)
        {
            string name = (Path.GetFileName(path) ?? string.Empty).Trim().ToLowerInvariant();
            if (name == "dcs.openbeta")
                return 0;
            if (name.Contains("openbeta"))
                return 1;
            if (name == "dcs")
                return 2;
            if (name.StartsWith("dcs", StringComparison.Ordinal))
                return 3;
            return 4;
        }

        private static int ScoreDcsInstallPath(string path)
        {
            string name = (Path.GetFileName(path) ?? string.Empty).Trim().ToLowerInvariant();
            if (name.Contains("openbeta"))
                return 0;
            if (name == "dcs world")
                return 1;
            if (name.Contains("dcs"))
                return 2;
            return 3;
        }

        private static int ScoreIl2InstallPath(string path)
        {
            string configPath = Path.Combine(path ?? string.Empty, "data", "input");
            return Directory.Exists(configPath) ? 0 : 1;
        }

        private static int ScoreStarCitizenInstallPath(string path)
        {
            string liveBin = Path.Combine(path ?? string.Empty, "LIVE", "Bin64", "StarCitizen.exe");
            if (File.Exists(liveBin))
                return 0;
            if (Directory.Exists(Path.Combine(path ?? string.Empty, "LIVE")))
                return 1;
            return 2;
        }

        private static int ScoreStarCitizenConfigPath(string path)
        {
            string upper = (path ?? string.Empty).ToUpperInvariant();
            if (upper.Contains("\\LIVE\\"))
                return 0;
            if (upper.Contains("\\EPTU\\"))
                return 1;
            if (upper.Contains("\\PTU\\"))
                return 2;
            return 3;
        }

        private static List<string> GetSavedGamesRoots(List<string> warnings)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string knownSavedGames = TryGetSavedGamesKnownFolderPath(warnings);
            AddDirectoryCandidate(output, knownSavedGames);

            string profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profilePath))
                AddDirectoryCandidate(output, Path.Combine(profilePath, "Saved Games"));

            string envSavedGames = Environment.GetEnvironmentVariable("SAVEDGAMES");
            AddDirectoryCandidate(output, envSavedGames);

            return output
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string TryGetSavedGamesKnownFolderPath(List<string> warnings)
        {
            IntPtr outPath = IntPtr.Zero;
            try
            {
                int hr = SHGetKnownFolderPath(SavedGamesFolderId, 0, IntPtr.Zero, out outPath);
                if (hr < 0 || outPath == IntPtr.Zero)
                    return string.Empty;

                string value = Marshal.PtrToStringUni(outPath) ?? string.Empty;
                return NormalizePath(value);
            }
            catch (Exception ex)
            {
                warnings?.Add("Known folder lookup for Saved Games failed: " + ex.Message);
                return string.Empty;
            }
            finally
            {
                if (outPath != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(outPath);
            }
        }

        private static List<string> GetProgramFilesRoots(List<string> warnings)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddDirectoryCandidate(output, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            AddDirectoryCandidate(output, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    DriveInfo drive = drives[i];
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                        continue;

                    string root = drive.RootDirectory.FullName;
                    AddDirectoryCandidate(output, Path.Combine(root, "Program Files"));
                    AddDirectoryCandidate(output, Path.Combine(root, "Program Files (x86)"));
                }
            }
            catch (Exception ex)
            {
                warnings?.Add("Program Files root discovery failed: " + ex.Message);
            }

            return output
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static List<string> GetSteamLibraryRoots(List<string> warnings)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Test drives (GameInstalls.TestDrivesVariable): only their SteamLibrary folders, no registry.
            List<string> testDrives = GameInstalls.TestDrives();
            if (testDrives != null)
            {
                foreach (string drive in testDrives)
                    AddDirectoryCandidate(output, Path.Combine(drive, "SteamLibrary"));
                return output.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
            }
            List<string> steamRoots = GetSteamInstallRoots(warnings);
            for (int i = 0; i < steamRoots.Count; i++)
            {
                string root = steamRoots[i];
                AddDirectoryCandidate(output, root);

                List<string> libraries = ReadSteamLibrariesFromVdf(root, warnings);
                for (int j = 0; j < libraries.Count; j++)
                    AddDirectoryCandidate(output, libraries[j]);
            }

            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    DriveInfo drive = drives[i];
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                        continue;
                    AddDirectoryCandidate(output, Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
                }
            }
            catch (Exception ex)
            {
                warnings?.Add("Steam library fallback probe failed: " + ex.Message);
            }

            return output
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static List<string> GetSteamInstallRoots(List<string> warnings)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddRegistryDirectoryCandidates(output, RegistryHive.CurrentUser, @"SOFTWARE\Valve\Steam", "SteamPath", warnings);
            AddRegistryDirectoryCandidates(output, RegistryHive.CurrentUser, @"SOFTWARE\Valve\Steam", "InstallPath", warnings);
            AddRegistryDirectoryCandidates(output, RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "SteamPath", warnings);
            AddRegistryDirectoryCandidates(output, RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath", warnings);

            AddDirectoryCandidate(output, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            AddDirectoryCandidate(output, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));

            return output
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> ReadSteamLibrariesFromVdf(string steamRoot, List<string> warnings)
        {
            List<string> output = new List<string>();
            if (string.IsNullOrWhiteSpace(steamRoot))
                return output;

            string vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath))
                return output;

            try
            {
                string[] lines = File.ReadAllLines(vdfPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = (lines[i] ?? string.Empty).Trim();
                    if (line.Length == 0)
                        continue;

                    Match pathMatch = Regex.Match(line, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                    if (!pathMatch.Success)
                        pathMatch = Regex.Match(line, "^\"\\d+\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                    if (!pathMatch.Success)
                        continue;

                    string raw = pathMatch.Groups[1].Value.Replace("\\\\", "\\").Trim();
                    if (raw.Length == 0)
                        continue;

                    string normalized = NormalizePath(raw);
                    if (normalized.Length > 0 && Directory.Exists(normalized))
                        output.Add(normalized);
                }
            }
            catch (Exception ex)
            {
                warnings?.Add("Steam library parse failed at '" + vdfPath + "': " + ex.Message);
            }

            return output
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddStarCitizenRootsFromGameConfigStore(HashSet<string> output, List<string> warnings)
        {
            RegistryView[] views = GetRegistryViews();
            for (int i = 0; i < views.Length; i++)
            {
                RegistryView view = views[i];
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                    using (RegistryKey children = baseKey.OpenSubKey(@"System\GameConfigStore\Children"))
                    {
                        if (children == null)
                            continue;

                        string[] keys = children.GetSubKeyNames();
                        for (int k = 0; k < keys.Length; k++)
                        {
                            using (RegistryKey child = children.OpenSubKey(keys[k]))
                            {
                                if (child == null)
                                    continue;
                                string exePath = Convert.ToString(child.GetValue("MatchedExeFullPath"), CultureInfo.InvariantCulture) ?? string.Empty;
                                if (exePath.IndexOf("StarCitizen.exe", StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;
                                string root = GetStarCitizenRootFromExecutable(exePath);
                                AddDirectoryCandidate(output, root);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings?.Add("Star Citizen registry lookup failed (GameConfigStore " + view + "): " + ex.Message);
                }
            }
        }

        private static string GetStarCitizenRootFromExecutable(string executablePath)
        {
            string fullExecutable = NormalizePath(executablePath);
            if (fullExecutable.Length == 0)
                return string.Empty;
            if (!fullExecutable.EndsWith("StarCitizen.exe", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            try
            {
                DirectoryInfo bin64 = new DirectoryInfo(Path.GetDirectoryName(fullExecutable) ?? string.Empty);
                DirectoryInfo branch = bin64.Parent;
                DirectoryInfo install = branch == null ? null : branch.Parent;
                if (install == null)
                    return string.Empty;
                return NormalizePath(install.FullName);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void AddRegistryDirectoryCandidates(
            HashSet<string> output,
            RegistryHive hive,
            string subKey,
            string valueName,
            List<string> warnings)
        {
            List<string> values = ReadRegistryValues(hive, subKey, valueName, warnings);
            for (int i = 0; i < values.Count; i++)
                AddDirectoryCandidate(output, values[i]);
        }

        private static List<string> ReadRegistryValues(
            RegistryHive hive,
            string subKey,
            string valueName,
            List<string> warnings)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            RegistryView[] views = GetRegistryViews();
            for (int i = 0; i < views.Length; i++)
            {
                RegistryView view = views[i];
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (RegistryKey key = baseKey.OpenSubKey(subKey))
                    {
                        if (key == null)
                            continue;

                        object rawValue = key.GetValue(valueName);
                        if (rawValue == null)
                            continue;

                        string text = Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? string.Empty;
                        if (text.Trim().Length > 0)
                            output.Add(text.Trim());
                    }
                }
                catch (Exception ex)
                {
                    warnings?.Add("Registry query failed (" + hive + "\\" + subKey + " :: " + valueName + ", " + view + "): " + ex.Message);
                }
            }
            return output
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static RegistryView[] GetRegistryViews()
        {
            if (!Environment.Is64BitOperatingSystem)
                return new[] { RegistryView.Default };
            return new[] { RegistryView.Registry64, RegistryView.Registry32 };
        }

        private static void AddDirectoryCandidate(HashSet<string> output, string path)
        {
            if (output == null || string.IsNullOrWhiteSpace(path))
                return;

            string normalized = NormalizePath(path);
            if (normalized.Length == 0 || !Directory.Exists(normalized))
                return;

            output.Add(normalized);
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                string trimmed = path.Trim().Trim('"');
                if (trimmed.Length == 0)
                    return string.Empty;
                return Path.GetFullPath(trimmed);
            }
            catch
            {
                return string.Empty;
            }
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
            uint dwFlags,
            IntPtr hToken,
            out IntPtr ppszPath);
    }

    internal sealed class GamePathCatalog
    {
        public List<GamePathEntry> Games { get; set; }
        public List<string> Warnings { get; set; }

        public GamePathCatalog()
        {
            Games = new List<GamePathEntry>();
            Warnings = new List<string>();
        }
    }

    internal sealed class GamePathEntry
    {
        public string Game { get; set; }
        public string PreferredInstallPath { get; set; }
        public string PreferredConfigPath { get; set; }
        public List<string> InstallPaths { get; set; }
        public List<string> ConfigPaths { get; set; }

        public GamePathEntry()
        {
            Game = string.Empty;
            PreferredInstallPath = string.Empty;
            PreferredConfigPath = string.Empty;
            InstallPaths = new List<string>();
            ConfigPaths = new List<string>();
        }
    }
}
