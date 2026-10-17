using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // Two optional changes to the DCS installation itself (docs/games/dcs.md, "Default binds in the DCS install"),
    // both off unless the user switches them on (Settings > DCS: default binds):
    //
    // 1. Strip: every joystick input profile of the install (each *.lua in a folder named "joystick" under
    //    <DCS>\Mods, <DCS>\Config and Saved Games\DCS*\Mods, except *.diff.lua, that has keyCommands, axisCommands or
    //    external_profile) is wrapped in a function, and the table it returns gets combos = nil on every keyCommands
    //    and axisCommands entry: DCS then has no default joystick binds at all, for every device, also the ones
    //    JoyPro does not write. Same marker line and backup name as the community script
    //    (DCS_STRIP_JOYSTICK_DEFAULTS v1, <file>.nocombo.bak), so either one can undo the other's work.
    // 2. Device files: the device-specific default files ("<device type>.lua", template diffs
    //    "<device type>.diff.lua") move into JoyPro's backup folder, so DCS uses default.lua for every device (what
    //    JoyPro 1 did). Files another Lua file of the module refers to stay; the UI layer (VR controllers) stays.
    //
    // A DCS update or repair brings the original files back: with the option on, JoyPro does it again at its start,
    // before every DCS list refresh and on "auto". Patched files fail the integrity check of servers that enforce it.
    internal static class EngineDcsDefaultsStrip
    {
        public const string Marker = "-- [[DCS_STRIP_JOYSTICK_DEFAULTS v1]]";
        public const string MarkerToken = "DCS_STRIP_JOYSTICK_DEFAULTS v1";
        public const string BackupSuffix = ".nocombo.bak";
        public const string StateFolderName = "DcsDefaults";
        private const string StateFileName = "state.json";
        private const string DeviceLuasFolderName = "DeviceLuas";
        private const string DeviceLuasManifestName = "manifest.json";
        private static readonly string[] DcsProcessNames = { "DCS", "DCS_updater" };
        private static readonly Regex ProfileContent = new Regex(@"keyCommands|axisCommands|external_profile", RegexOptions.Compiled);
        private static readonly Regex DefaultAssignmentKey = new Regex("default_assignments\\s*\\[\\s*\"(?<name>(?:\\\\.|[^\"\\\\])*)\"\\s*\\]\\s*=", RegexOptions.Compiled);

        // ---- Paths ----

        public static string ResolveInstallPath(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
                return Path.GetFullPath(explicitPath.Trim());
            try
            {
                return GamePathDiscovery.DiscoverDcsInstallPaths(new List<string>()).FirstOrDefault(Directory.Exists) ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        // Saved Games\DCS* folders whose Mods are scanned: the given one, else every one found.
        public static List<string> ResolveSavedGamesPaths(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
                return new List<string> { Path.GetFullPath(explicitPath.Trim()) };
            try
            {
                return GamePathDiscovery.DiscoverDcsSavedGamesPaths(new List<string>()).Where(Directory.Exists).ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        public static string StateFolder()
        {
            return Path.Combine(GameInstalls.DataRoot(), StateFolderName);
        }

        // ---- DCS's DefaultAssignments.lua ----

        // The device types Scripts\Input\DefaultAssignments.lua names: default.lua gives them axes of their own
        // (defaultDeviceAssignmentFor), so their defaults differ from the generic stick's.
        public static HashSet<string> DefaultAssignmentDeviceTypes(string installPath)
        {
            HashSet<string> types = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(installPath))
                return types;
            string file = Path.Combine(installPath, "Scripts", "Input", "DefaultAssignments.lua");
            try
            {
                if (!File.Exists(file))
                    return types;
                foreach (Match m in DefaultAssignmentKey.Matches(File.ReadAllText(file, Encoding.UTF8)))
                {
                    string name = m.Groups["name"].Value;
                    if (name.Length > 0 && name != "default" && name != "Keyboard")
                        types.Add(name);
                }
            }
            catch (Exception)
            {
            }
            return types;
        }

        // ---- State ----

        public sealed class DcsDefaultsState
        {
            public bool StripEnabled { get; set; }
            public bool SkipUiLayer { get; set; }
            public bool RemoveDeviceLuas { get; set; }
            public string InstallPath { get; set; }
            public string DcsVersion { get; set; }
            public string LastApplied { get; set; }
            // The device files changed after the clean profiles were built: they no longer match what DCS loads.
            public bool CleanProfilesStale { get; set; }

            public DcsDefaultsState()
            {
                InstallPath = string.Empty;
                DcsVersion = string.Empty;
                LastApplied = string.Empty;
            }
        }

        public static DcsDefaultsState LoadState()
        {
            try
            {
                string path = Path.Combine(StateFolder(), StateFileName);
                if (File.Exists(path))
                    return EngineSerialization.FromJson<DcsDefaultsState>(File.ReadAllText(path, Encoding.UTF8)) ?? new DcsDefaultsState();
            }
            catch (Exception)
            {
            }
            return new DcsDefaultsState();
        }

        public static void SaveState(DcsDefaultsState state, List<string> warnings)
        {
            try
            {
                Directory.CreateDirectory(StateFolder());
                string path = Path.Combine(StateFolder(), StateFileName);
                string temp = path + ".tmp";
                File.WriteAllText(temp, EngineSerialization.ToJson(state), new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
            }
            catch (Exception ex)
            {
                warnings?.Add("Could not save the DCS default-bind settings: " + ex.Message);
            }
        }

        // Called after a full clean refresh: the clean profiles match the installation again.
        public static void MarkCleanProfilesCurrent()
        {
            DcsDefaultsState state = LoadState();
            if (!state.CleanProfilesStale)
                return;
            state.CleanProfilesStale = false;
            SaveState(state, null);
        }

        public static string ReadDcsVersion(string installPath)
        {
            try
            {
                string config = Path.Combine(installPath ?? string.Empty, "autoupdate.cfg");
                if (File.Exists(config))
                {
                    Match version = Regex.Match(File.ReadAllText(config), "\"version\"\\s*:\\s*\"(?<v>[^\"]+)\"");
                    if (version.Success)
                        return version.Groups["v"].Value;
                }
            }
            catch (Exception)
            {
            }
            return string.Empty;
        }

        // ---- Results ----

        public sealed class DcsStripResult
        {
            public string Action { get; set; }
            public bool DryRun { get; set; }
            public string InstallPath { get; set; }
            public List<string> SavedGamesPaths { get; set; }
            public string DcsVersion { get; set; }
            public bool StripEnabled { get; set; }
            public bool SkipUiLayer { get; set; }
            public bool RemoveDeviceLuas { get; set; }
            public bool CleanProfilesStale { get; set; }
            public int ProfileFiles { get; set; }
            public int Patched { get; set; }
            public int AlreadyPatched { get; set; }
            public int NotPatched { get; set; }
            public int Restored { get; set; }
            public int LoadChecked { get; set; }
            // Patched files DCS's own input loader read next to their original (same commands, no combos left).
            public int LoadCompared { get; set; }
            public int DefaultBindsSwitchedOff { get; set; }
            public int DeviceFilesInInstall { get; set; }
            public int DeviceFilesMoved { get; set; }
            public int DeviceFilesBackedUp { get; set; }
            public int DeviceFilesRestored { get; set; }
            public bool NeedsReapply { get; set; }
            public string IntegrityNote { get; set; }
            public List<string> Files { get; set; }
            public List<string> Warnings { get; set; }
            public List<string> Errors { get; set; }

            public DcsStripResult()
            {
                Action = string.Empty;
                InstallPath = string.Empty;
                SavedGamesPaths = new List<string>();
                DcsVersion = string.Empty;
                IntegrityNote = "Patched DCS files fail the integrity check of multiplayer servers that enforce it (pure scripts / IC). Restore puts the original files back.";
                Files = new List<string>();
                Warnings = new List<string>();
                Errors = new List<string>();
            }
        }

        public sealed class StripOptions
        {
            public string InstallPath { get; set; }
            public string SavedGamesPath { get; set; }
            public bool DryRun { get; set; }
            public bool? SkipUiLayer { get; set; }
            public bool Remember { get; set; } = true;
            public string LuaePath { get; set; }
            public bool SkipLoadCheck { get; set; }
        }

        // ---- Strip: finding the files ----

        // Every joystick input profile of the install and of Saved Games\DCS*\Mods (the community script's rules).
        public static List<string> FindProfileFiles(string installPath, IEnumerable<string> savedGamesPaths, bool skipUiLayer, List<string> warnings)
        {
            List<string> files = new List<string>();
            foreach (string folder in FindJoystickFolders(installPath, savedGamesPaths, warnings))
            {
                if (skipUiLayer && IsUiLayerFolder(installPath, folder))
                    continue;
                string[] luas;
                try
                {
                    luas = Directory.GetFiles(folder, "*.lua", SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex)
                {
                    warnings.Add("Could not list " + folder + ": " + ex.Message);
                    continue;
                }
                foreach (string file in luas.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (file.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string text = ReadText(file);
                    if (text == null || !ProfileContent.IsMatch(text))
                        continue;
                    files.Add(file);
                }
            }
            return files;
        }

        public static List<string> FindJoystickFolders(string installPath, IEnumerable<string> savedGamesPaths, List<string> warnings)
        {
            List<string> roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(installPath))
            {
                roots.Add(Path.Combine(installPath, "Mods"));
                roots.Add(Path.Combine(installPath, "Config"));
            }
            foreach (string savedGames in savedGamesPaths ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(savedGames))
                    roots.Add(Path.Combine(savedGames, "Mods"));

            List<string> folders = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                if (!Directory.Exists(root))
                    continue;
                Stack<string> pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    string dir = pending.Pop();
                    string[] subs;
                    try
                    {
                        subs = Directory.GetDirectories(dir);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Could not list " + dir + ": " + ex.Message);
                        continue;
                    }
                    foreach (string sub in subs)
                    {
                        if (string.Equals(Path.GetFileName(sub), "joystick", StringComparison.OrdinalIgnoreCase))
                        {
                            if (seen.Add(Path.GetFullPath(sub)))
                                folders.Add(sub);
                        }
                        else
                        {
                            pending.Push(sub);
                        }
                    }
                }
            }
            return folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsUiLayerFolder(string installPath, string folder)
        {
            string parent = Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? string.Empty;
            if (!string.Equals(Path.GetFileName(parent), "UiLayer", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        public static bool HasMarker(string file)
        {
            try
            {
                using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] head = new byte[4096];
                    int read = stream.Read(head, 0, head.Length);
                    return Encoding.ASCII.GetString(head, 0, read).IndexOf(MarkerToken, StringComparison.Ordinal) >= 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ReadText(string file)
        {
            try
            {
                return File.ReadAllText(file, Encoding.GetEncoding(1252));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- Strip: the wrapper ----

        // The original file becomes the body of a function; what it returns loses its default combos. The bytes of
        // the original stay as they are (code page, line ends), a UTF-8 byte order mark stays in front.
        public static byte[] Wrap(byte[] original)
        {
            byte[] bom = { 0xEF, 0xBB, 0xBF };
            bool hasBom = original.Length >= 3 && original[0] == bom[0] && original[1] == bom[1] && original[2] == bom[2];
            int start = hasBom ? 3 : 0;
            bool crlf = IndexOf(original, new byte[] { 13, 10 }) >= 0;
            string nl = crlf ? "\r\n" : "\n";
            string head =
                Marker + nl +
                "-- JoyPro: DCS's default joystick binds of this file are switched off (combos = nil). The original is" + nl +
                "-- <this file>" + BackupSuffix + "; JoyPro (Settings > DCS: default binds) or the strip script restores it." + nl +
                "local __joypro_strip_source = function(...)" + nl;
            string tail =
                nl + "end" + nl +
                "local __joypro_strip_result = __joypro_strip_source(...)" + nl +
                "if type(__joypro_strip_result) == 'table' then" + nl +
                "\tfor _, __joypro_strip_name in ipairs({ 'keyCommands', 'axisCommands' }) do" + nl +
                "\t\tlocal __joypro_strip_list = __joypro_strip_result[__joypro_strip_name]" + nl +
                "\t\tif type(__joypro_strip_list) == 'table' then" + nl +
                "\t\t\tfor _, __joypro_strip_command in ipairs(__joypro_strip_list) do" + nl +
                "\t\t\t\tif type(__joypro_strip_command) == 'table' then __joypro_strip_command.combos = nil end" + nl +
                "\t\t\tend" + nl +
                "\t\tend" + nl +
                "\tend" + nl +
                "end" + nl +
                "return __joypro_strip_result" + nl;
            using (MemoryStream output = new MemoryStream())
            {
                if (hasBom)
                    output.Write(bom, 0, 3);
                byte[] headBytes = Encoding.ASCII.GetBytes(head);
                output.Write(headBytes, 0, headBytes.Length);
                output.Write(original, start, original.Length - start);
                byte[] tailBytes = Encoding.ASCII.GetBytes(tail);
                output.Write(tailBytes, 0, tailBytes.Length);
                return output.ToArray();
            }
        }

        private static int IndexOf(byte[] data, byte[] pattern)
        {
            for (int i = 0; i + pattern.Length <= data.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length && match; j++)
                    match = data[i + j] == pattern[j];
                if (match)
                    return i;
            }
            return -1;
        }

        // ---- Strip: apply / restore / status ----

        public static DcsStripResult Strip(StripOptions options)
        {
            DcsStripResult result = NewResult("strip", options);
            if (!CheckInstall(result))
                return result;
            DcsDefaultsState state = LoadState();
            bool skipUi = options.SkipUiLayer ?? state.SkipUiLayer;
            result.SkipUiLayer = skipUi;
            if (!options.DryRun && !RefuseWhileDcsRuns(result))
                return result;

            List<string> files = FindProfileFiles(result.InstallPath, result.SavedGamesPaths, skipUi, result.Warnings);
            result.ProfileFiles = files.Count;
            List<string> written = new List<string>();
            foreach (string file in files)
            {
                if (HasMarker(file))
                {
                    result.AlreadyPatched++;
                    continue;
                }
                result.Files.Add(file);
                if (options.DryRun)
                {
                    result.Patched++;
                    continue;
                }
                try
                {
                    byte[] original = File.ReadAllBytes(file);
                    string backup = file + BackupSuffix;
                    // A backup left from before a DCS update is older than the file DCS brought back: replace it.
                    WriteAtomically(backup, original);
                    WriteAtomically(file, Wrap(original));
                    written.Add(file);
                    result.Patched++;
                }
                catch (UnauthorizedAccessException ex)
                {
                    result.Errors.Add("No write access to " + file + " (" + ex.Message + "). Give your Windows user write access to the DCS folder, or start JoyPro once as administrator.");
                    break;
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Could not patch " + file + ": " + ex.Message);
                }
            }

            // DCS's own Lua must load every patched file: one that does not is put back at once.
            if (written.Count > 0 && !options.SkipLoadCheck)
            {
                Dictionary<string, string> failures = LoadCheck(written, result.InstallPath, options.LuaePath, result.Warnings, out int checkedCount, out int compared, out int bindsOff);
                result.LoadChecked = checkedCount;
                result.LoadCompared = compared;
                result.DefaultBindsSwitchedOff = bindsOff;
                foreach (KeyValuePair<string, string> failure in failures)
                {
                    result.Errors.Add("DCS's Lua cannot load the patched " + failure.Key + " (" + failure.Value + "); the original was put back.");
                    RestoreOne(failure.Key, result);
                    result.Patched--;
                }
            }

            if (!options.DryRun && options.Remember)
            {
                state.StripEnabled = true;
                state.SkipUiLayer = skipUi;
                Remember(state, result);
            }
            FillState(result, LoadStateOr(state, options));
            return result;
        }

        public static DcsStripResult Unstrip(StripOptions options)
        {
            DcsStripResult result = NewResult("restore", options);
            if (!CheckInstall(result))
                return result;
            DcsDefaultsState state = LoadState();
            result.SkipUiLayer = false;
            if (!options.DryRun && !RefuseWhileDcsRuns(result))
                return result;
            // Every profile file, the UI layer too: a restore never leaves a patched file behind.
            foreach (string file in FindProfileFiles(result.InstallPath, result.SavedGamesPaths, false, result.Warnings))
            {
                result.ProfileFiles++;
                if (!HasMarker(file))
                {
                    result.NotPatched++;
                    continue;
                }
                result.Files.Add(file);
                if (options.DryRun)
                {
                    result.Restored++;
                    continue;
                }
                RestoreOne(file, result);
            }
            if (!options.DryRun && options.Remember)
            {
                state.StripEnabled = false;
                Remember(state, result);
            }
            FillState(result, LoadStateOr(state, options));
            return result;
        }

        private static void RestoreOne(string file, DcsStripResult result)
        {
            string backup = file + BackupSuffix;
            if (!File.Exists(backup))
            {
                result.Errors.Add("No backup " + backup + " for the patched " + file + ": repair DCS (DCS_updater.exe repair) to get the original back.");
                return;
            }
            try
            {
                WriteAtomically(file, File.ReadAllBytes(backup));
                File.Delete(backup);
                result.Restored++;
            }
            catch (Exception ex)
            {
                result.Errors.Add("Could not restore " + file + ": " + ex.Message);
            }
        }

        public static DcsStripResult Status(StripOptions options)
        {
            DcsStripResult result = NewResult("status", options);
            if (!CheckInstall(result))
                return result;
            DcsDefaultsState state = LoadState();
            result.SkipUiLayer = state.SkipUiLayer;
            foreach (string file in FindProfileFiles(result.InstallPath, result.SavedGamesPaths, false, result.Warnings))
            {
                bool skipped = state.SkipUiLayer && IsUiLayerFolder(result.InstallPath, Path.GetDirectoryName(file));
                result.ProfileFiles++;
                if (HasMarker(file))
                    result.AlreadyPatched++;
                else if (!skipped)
                {
                    result.NotPatched++;
                    if (state.StripEnabled)
                        result.Files.Add(file);
                }
            }
            List<DeviceLuaFile> deviceFiles = FindDeviceLuas(result.InstallPath, result.SavedGamesPaths, result.Warnings);
            result.DeviceFilesInInstall = deviceFiles.Count;
            result.DeviceFilesBackedUp = LoadDeviceManifest().Count;
            FillState(result, state);
            result.NeedsReapply = (state.StripEnabled && result.NotPatched > 0) || (state.RemoveDeviceLuas && deviceFiles.Count > 0);
            return result;
        }

        // Re-applies what is switched on when DCS brought files back (update, repair). Cheap when nothing is on.
        public static DcsStripResult Auto(StripOptions options)
        {
            DcsDefaultsState state = LoadState();
            DcsStripResult result = NewResult("auto", options);
            if (!state.StripEnabled && !state.RemoveDeviceLuas)
            {
                FillState(result, state);
                result.Warnings.Add("Nothing to do: neither \"strip DCS default binds\" nor \"remove device files\" is switched on.");
                return result;
            }
            if (!CheckInstall(result))
                return result;
            if (state.RemoveDeviceLuas)
            {
                DcsStripResult moved = RemoveDeviceLuas(new StripOptions { InstallPath = result.InstallPath, SavedGamesPath = options.SavedGamesPath, DryRun = options.DryRun, LuaePath = options.LuaePath });
                Merge(result, moved);
            }
            if (state.StripEnabled)
            {
                DcsStripResult stripped = Strip(new StripOptions { InstallPath = result.InstallPath, SavedGamesPath = options.SavedGamesPath, DryRun = options.DryRun, LuaePath = options.LuaePath });
                Merge(result, stripped);
            }
            FillState(result, LoadState());
            return result;
        }

        // Before a DCS list refresh (the refresh then sees the installation as DCS will load it). Never throws.
        public static void AutoBeforeRefresh(string installPath, string savedGamesPath, List<string> warnings, List<string> steps)
        {
            try
            {
                DcsDefaultsState state = LoadState();
                if (!state.StripEnabled && !state.RemoveDeviceLuas)
                    return;
                DcsStripResult result = Auto(new StripOptions { InstallPath = installPath });
                if (result.Patched > 0 || result.DeviceFilesMoved > 0)
                    steps.Add("DCS default binds in the install switched off again after a DCS update: " + result.Patched.ToString(CultureInfo.InvariantCulture) +
                              " file(s) patched, " + result.DeviceFilesMoved.ToString(CultureInfo.InvariantCulture) + " device file(s) moved.");
                foreach (string error in result.Errors)
                    warnings.Add("DCS default binds in the install: " + error);
            }
            catch (Exception ex)
            {
                warnings.Add("DCS default binds in the install: " + ex.Message);
            }
        }

        // ---- Device files ----

        public sealed class DeviceLuaFile
        {
            public string Original { get; set; }
            public string Backup { get; set; }
            public string Sha256 { get; set; }
            public string Moved { get; set; }
        }

        // Device-specific default files in the joystick folders: "<type>.lua" (not default.lua) and template diffs
        // "<type>.diff.lua". A file another Lua file of the same Input folder names (a shared include) stays, and
        // so does the UI layer (the VR controllers' defaults there make the menus usable in VR).
        public static List<DeviceLuaFile> FindDeviceLuas(string installPath, IEnumerable<string> savedGamesPaths, List<string> warnings)
        {
            List<DeviceLuaFile> found = new List<DeviceLuaFile>();
            Dictionary<string, string> inputTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in FindJoystickFolders(installPath, savedGamesPaths, warnings))
            {
                if (IsUiLayerFolder(installPath, folder))
                    continue;
                string[] files;
                try
                {
                    files = Directory.GetFiles(folder, "*.lua", SearchOption.TopDirectoryOnly);
                }
                catch (Exception)
                {
                    continue;
                }
                string inputFolder = Path.GetDirectoryName(Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? string.Empty) ?? string.Empty;
                foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(file);
                    if (string.Equals(name, "default.lua", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string type = name.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase)
                        ? name.Substring(0, name.Length - ".diff.lua".Length)
                        : name.Substring(0, name.Length - ".lua".Length);
                    if (type.Trim().Length == 0 || string.Equals(type, "default", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (IsReferenced(inputFolder, file, name, inputTexts))
                        continue;
                    found.Add(new DeviceLuaFile { Original = file });
                }
            }
            return found;
        }

        // True when another Lua file of the Input folder names this file (external_profile, dofile, ...).
        private static bool IsReferenced(string inputFolder, string file, string fileName, Dictionary<string, string> cache)
        {
            if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder))
                return false;
            string all;
            if (!cache.TryGetValue(inputFolder, out all))
            {
                StringBuilder text = new StringBuilder();
                try
                {
                    foreach (string lua in Directory.GetFiles(inputFolder, "*.lua", SearchOption.AllDirectories))
                    {
                        if (lua.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase))
                            continue;
                        // Another device file names it (one device's defaults built on another's): both move.
                        if (string.Equals(Path.GetFileName(Path.GetDirectoryName(lua)), "joystick", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(Path.GetFileName(lua), "default.lua", StringComparison.OrdinalIgnoreCase))
                            continue;
                        text.Append('\u0001').Append(Path.GetFullPath(lua)).Append('\u0002').Append(ReadText(lua) ?? string.Empty);
                    }
                }
                catch (Exception)
                {
                }
                all = text.ToString();
                cache[inputFolder] = all;
            }
            string self = Path.GetFullPath(file);
            foreach (string part in all.Split('\u0001'))
            {
                int split = part.IndexOf('\u0002');
                if (split < 0)
                    continue;
                if (string.Equals(part.Substring(0, split), self, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (part.IndexOf(fileName, split, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string DeviceLuasFolder()
        {
            return Path.Combine(StateFolder(), DeviceLuasFolderName);
        }

        private static List<DeviceLuaFile> LoadDeviceManifest()
        {
            try
            {
                string path = Path.Combine(DeviceLuasFolder(), DeviceLuasManifestName);
                if (File.Exists(path))
                    return EngineSerialization.FromJson<List<DeviceLuaFile>>(File.ReadAllText(path, Encoding.UTF8)) ?? new List<DeviceLuaFile>();
            }
            catch (Exception)
            {
            }
            return new List<DeviceLuaFile>();
        }

        private static void SaveDeviceManifest(List<DeviceLuaFile> manifest, List<string> errors)
        {
            try
            {
                Directory.CreateDirectory(DeviceLuasFolder());
                WriteAtomically(Path.Combine(DeviceLuasFolder(), DeviceLuasManifestName), new UTF8Encoding(false).GetBytes(EngineSerialization.ToJson(manifest)));
            }
            catch (Exception ex)
            {
                errors.Add("Could not save the list of moved DCS device files: " + ex.Message);
            }
        }

        private static string BackupPathFor(string original)
        {
            // <data>\DcsDefaults\DeviceLuas\<folder hash>\<file name>: unique per original and short (the engine
            // runs on .NET Framework, where paths over 260 characters fail); the manifest names the original.
            string full = Path.GetFullPath(original);
            string folder = (Path.GetDirectoryName(full) ?? string.Empty).ToUpperInvariant();
            string hash = Sha256(Encoding.UTF8.GetBytes(folder)).Substring(0, 12);
            return Path.Combine(DeviceLuasFolder(), hash, Path.GetFileName(full));
        }

        public static DcsStripResult RemoveDeviceLuas(StripOptions options)
        {
            DcsStripResult result = NewResult("device-luas remove", options);
            if (!CheckInstall(result))
                return result;
            if (!options.DryRun && !RefuseWhileDcsRuns(result))
                return result;
            List<DeviceLuaFile> found = FindDeviceLuas(result.InstallPath, result.SavedGamesPaths, result.Warnings);
            result.DeviceFilesInInstall = found.Count;
            List<DeviceLuaFile> manifest = LoadDeviceManifest();
            foreach (DeviceLuaFile file in found)
            {
                result.Files.Add(file.Original);
                if (options.DryRun)
                {
                    result.DeviceFilesMoved++;
                    continue;
                }
                try
                {
                    string backup = BackupPathFor(file.Original);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    byte[] bytes = File.ReadAllBytes(file.Original);
                    // A file DCS brought back after an update replaces the older copy JoyPro kept.
                    WriteAtomically(backup, bytes);
                    string strippedBackup = file.Original + BackupSuffix;
                    if (File.Exists(strippedBackup))
                    {
                        WriteAtomically(backup + BackupSuffix, File.ReadAllBytes(strippedBackup));
                        File.Delete(strippedBackup);
                    }
                    File.Delete(file.Original);
                    manifest.RemoveAll(m => string.Equals(m.Original, file.Original, StringComparison.OrdinalIgnoreCase));
                    manifest.Add(new DeviceLuaFile { Original = file.Original, Backup = backup, Sha256 = Sha256(bytes), Moved = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) });
                    result.DeviceFilesMoved++;
                }
                catch (UnauthorizedAccessException ex)
                {
                    result.Errors.Add("No write access to " + file.Original + " (" + ex.Message + "). Give your Windows user write access to the DCS folder, or start JoyPro once as administrator.");
                    break;
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Could not move " + file.Original + ": " + ex.Message);
                }
            }
            DcsDefaultsState state = LoadState();
            if (!options.DryRun)
            {
                SaveDeviceManifest(manifest, result.Errors);
                if (options.Remember)
                    state.RemoveDeviceLuas = true;
                if (result.DeviceFilesMoved > 0)
                    state.CleanProfilesStale = true;
                if (options.Remember || result.DeviceFilesMoved > 0)
                    Remember(state, result);
            }
            result.DeviceFilesBackedUp = options.DryRun ? manifest.Count : LoadDeviceManifest().Count;
            FillState(result, LoadStateOr(state, options));
            return result;
        }

        public static DcsStripResult RestoreDeviceLuas(StripOptions options)
        {
            DcsStripResult result = NewResult("device-luas restore", options);
            if (!CheckInstall(result))
                return result;
            if (!options.DryRun && !RefuseWhileDcsRuns(result))
                return result;
            List<DeviceLuaFile> manifest = LoadDeviceManifest();
            List<DeviceLuaFile> left = new List<DeviceLuaFile>();
            foreach (DeviceLuaFile file in manifest)
            {
                result.Files.Add(file.Original);
                if (options.DryRun)
                {
                    result.DeviceFilesRestored++;
                    left.Add(file);
                    continue;
                }
                try
                {
                    if (File.Exists(file.Original))
                    {
                        // DCS brought the file back itself (update, repair): that one is newer, JoyPro's copy goes.
                        result.Warnings.Add("Kept the file DCS brought back: " + file.Original);
                    }
                    else if (File.Exists(file.Backup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file.Original));
                        WriteAtomically(file.Original, File.ReadAllBytes(file.Backup));
                        if (File.Exists(file.Backup + BackupSuffix))
                            WriteAtomically(file.Original + BackupSuffix, File.ReadAllBytes(file.Backup + BackupSuffix));
                        result.DeviceFilesRestored++;
                    }
                    else
                    {
                        result.Errors.Add("JoyPro's copy of " + file.Original + " is missing (" + file.Backup + "): repair DCS to get it back.");
                        left.Add(file);
                        continue;
                    }
                    TryDelete(file.Backup);
                    TryDelete(file.Backup + BackupSuffix);
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Could not put back " + file.Original + ": " + ex.Message);
                    left.Add(file);
                }
            }
            DcsDefaultsState state = LoadState();
            if (!options.DryRun)
            {
                SaveDeviceManifest(left, result.Errors);
                if (options.Remember)
                    state.RemoveDeviceLuas = false;
                if (result.DeviceFilesRestored > 0)
                    state.CleanProfilesStale = true;
                Remember(state, result);
            }
            string legacy = result.SavedGamesPaths.Select(p => Path.Combine(p, "BACKUP_JOYSTICK_DEFAULTS")).FirstOrDefault(Directory.Exists);
            if (legacy != null)
                result.Warnings.Add("An older JoyPro moved device files to " + legacy + ": engine dcs defaults restore puts those back.");
            result.DeviceFilesBackedUp = options.DryRun ? manifest.Count : left.Count;
            FillState(result, LoadStateOr(state, options));
            return result;
        }

        public static DcsStripResult ListDeviceLuas(StripOptions options)
        {
            DcsStripResult result = NewResult("device-luas list", options);
            if (!CheckInstall(result))
                return result;
            List<DeviceLuaFile> found = FindDeviceLuas(result.InstallPath, result.SavedGamesPaths, result.Warnings);
            result.DeviceFilesInInstall = found.Count;
            result.Files.AddRange(found.Select(f => f.Original));
            result.DeviceFilesBackedUp = LoadDeviceManifest().Count;
            FillState(result, LoadState());
            return result;
        }

        // ---- DCS's Lua load check ----

        // luae.exe of the DCS install (or the given one) runs loadfile on every file: what Lua 5.1 cannot parse is
        // reported with its message. Without luae.exe nothing is checked (a warning says so).
        public static Dictionary<string, string> LoadCheck(List<string> files, string installPath, string luaePath, List<string> warnings, out int checkedCount)
        {
            return LoadCheck(files, installPath, luaePath, warnings, out checkedCount, out _, out _);
        }

        // compared: files DCS's loader read in both versions (the others were parsed only); bindsOff: the default
        // binds the originals had.
        public static Dictionary<string, string> LoadCheck(List<string> files, string installPath, string luaePath, List<string> warnings,
            out int checkedCount, out int compared, out int bindsOff)
        {
            checkedCount = 0;
            compared = 0;
            bindsOff = 0;
            Dictionary<string, string> failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string luae = !string.IsNullOrWhiteSpace(luaePath) ? luaePath : Path.Combine(installPath ?? string.Empty, "bin", "luae.exe");
            if (!File.Exists(luae))
            {
                string real = ResolveInstallPath(null);
                if (!string.IsNullOrEmpty(real) && File.Exists(Path.Combine(real, "bin", "luae.exe")))
                    luae = Path.Combine(real, "bin", "luae.exe");
            }
            if (!File.Exists(luae))
            {
                warnings.Add("DCS's luae.exe was not found: the patched files were not load-checked.");
                return failures;
            }
            string dcsRoot = Path.GetDirectoryName(Path.GetDirectoryName(luae)) ?? string.Empty;
            bool useDcsLoader = File.Exists(Path.Combine(dcsRoot, "Scripts", "Input", "Data.lua"));
            List<string> output = RunLoadCheck(files, luae, dcsRoot, useDcsLoader, warnings, out bool fatal);
            if (useDcsLoader && fatal)
            {
                warnings.Add("DCS's input loader could not run in luae.exe: the patched files were only parsed.");
                output = RunLoadCheck(files, luae, dcsRoot, false, warnings, out fatal);
            }
            Dictionary<string, string> byLuaPath = files.ToDictionary(f => f.Replace('\\', '/'), f => f, StringComparer.OrdinalIgnoreCase);
            foreach (string line in output)
            {
                if (line.StartsWith("JOYPRO_LOAD_OK|", StringComparison.Ordinal))
                {
                    checkedCount++;
                    System.Text.RegularExpressions.Match off = Regex.Match(line, @"\|\d+ keys, \d+ axes, (?<n>\d+) default binds");
                    if (off.Success)
                    {
                        compared++;
                        bindsOff += int.Parse(off.Groups["n"].Value, CultureInfo.InvariantCulture);
                    }
                    else if (Environment.GetEnvironmentVariable("JOYPRO_DCS_LOADCHECK_DEBUG") == "1")
                        warnings.Add("[load check] " + line.Substring("JOYPRO_LOAD_OK|".Length));
                }
                else if (line.StartsWith("JOYPRO_LOAD_ERR|", StringComparison.Ordinal))
                {
                    checkedCount++;
                    string[] parts = line.Split(new[] { '|' }, 3);
                    string original;
                    if (parts.Length >= 2 && byLuaPath.TryGetValue(parts[1], out original))
                        failures[original] = parts.Length > 2 ? parts[2] : "load error";
                }
            }
            return failures;
        }

        private static List<string> RunLoadCheck(List<string> files, string luae, string dcsRoot, bool dcsLoader, List<string> warnings, out bool fatal)
        {
            fatal = false;
            List<string> lines = new List<string>();
            string script = Path.Combine(Path.GetTempPath(), "joypro_dcs_loadcheck_" + Guid.NewGuid().ToString("N") + ".lua");
            string scratch = Path.Combine(Path.GetTempPath(), "JoyPro_dcs_loadcheck_" + Guid.NewGuid().ToString("N"));
            try
            {
                StringBuilder lua = new StringBuilder();
                if (dcsLoader)
                {
                    // DCS's own loader (Input.Data.loadDeviceProfileFromFile, as DCS loads an aircraft's defaults)
                    // reads the patched file and its original: the patched one must give the same commands, all
                    // without combos. An original that does not load on its own (a part another file includes) is
                    // only parsed.
                    Directory.CreateDirectory(scratch);
                    EngineDcsRefresh.AppendHeadlessPreludeWithoutProfiles(lua, scratch);
                    lua.AppendLine("  loadfile = rawLoadfile");
                    lua.AppendLine("  local function clean(s) return (tostring(s or ''):gsub('[|\\r\\n]', ' ')) end");
                    lua.AppendLine("  local files = {");
                    foreach (string file in files)
                        lua.AppendLine("    '" + LuaEscape(file.Replace('\\', '/')) + "',");
                    lua.AppendLine("  }");
                    lua.AppendLine("  local function count(t) local n, c = 0, 0; for _, cmd in ipairs(t or {}) do n = n + 1; if type(cmd) == 'table' and cmd.combos and next(cmd.combos) then c = c + 1 end end; return n, c end");
                    lua.AppendLine("  for i = 1, #files do");
                    lua.AppendLine("    local p = files[i]");
                    lua.AppendLine("    local f, err = rawLoadfile(p)");
                    lua.AppendLine("    if not f then");
                    lua.AppendLine("      print('JOYPRO_LOAD_ERR|' .. p .. '|' .. clean(err))");
                    lua.AppendLine("    else");
                    lua.AppendLine("      local folder = p:match('^(.*/)') or './'");
                    lua.AppendLine("      local okO, orig = pcall(InputData.loadDeviceProfileFromFile, p .. '" + BackupSuffix + "', 'JoyPro Check', folder)");
                    lua.AppendLine("      local okP, pat = pcall(InputData.loadDeviceProfileFromFile, p, 'JoyPro Check', folder)");
                    lua.AppendLine("      if not okO or type(orig) ~= 'table' then");
                    lua.AppendLine("        print('JOYPRO_LOAD_OK|' .. p .. '|parsed: ' .. clean(orig))");
                    lua.AppendLine("      elseif not okP or type(pat) ~= 'table' then");
                    lua.AppendLine("        print('JOYPRO_LOAD_ERR|' .. p .. '|DCS loads the original but not the patched file: ' .. clean(pat))");
                    lua.AppendLine("      else");
                    lua.AppendLine("        local ok, ko = count(orig.keyCommands); local pk, pkc = count(pat.keyCommands)");
                    lua.AppendLine("        local oa, oac = count(orig.axisCommands); local pa, pac = count(pat.axisCommands)");
                    lua.AppendLine("        if ok ~= pk or oa ~= pa then");
                    lua.AppendLine("          print('JOYPRO_LOAD_ERR|' .. p .. '|commands differ: keys ' .. ok .. '/' .. pk .. ', axes ' .. oa .. '/' .. pa)");
                    lua.AppendLine("        elseif pkc + pac > 0 then");
                    lua.AppendLine("          print('JOYPRO_LOAD_ERR|' .. p .. '|' .. (pkc + pac) .. ' commands still have default binds')");
                    lua.AppendLine("        else");
                    lua.AppendLine("          print('JOYPRO_LOAD_OK|' .. p .. '|' .. ok .. ' keys, ' .. oa .. ' axes, ' .. (ko + oac) .. ' default binds switched off')");
                    lua.AppendLine("        end");
                    lua.AppendLine("      end");
                    lua.AppendLine("    end");
                    lua.AppendLine("  end");
                    lua.AppendLine("end)");
                    lua.AppendLine("if not ok then print('JOYPRO_FATAL|' .. tostring(fatal)) end");
                }
                else
                {
                    lua.AppendLine("local files = {");
                    foreach (string file in files)
                        lua.AppendLine("  '" + LuaEscape(file.Replace('\\', '/')) + "',");
                    lua.AppendLine("}");
                    lua.AppendLine("for i = 1, #files do");
                    lua.AppendLine("  local f, err = loadfile(files[i])");
                    lua.AppendLine("  if f then print('JOYPRO_LOAD_OK|' .. files[i]) else print('JOYPRO_LOAD_ERR|' .. files[i] .. '|' .. tostring(err):gsub('[\\r\\n|]', ' ')) end");
                    lua.AppendLine("end");
                }
                File.WriteAllText(script, lua.ToString(), Encoding.Default);
                ProcessStartInfo start = new ProcessStartInfo(luae, "\"" + script + "\"")
                {
                    WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(luae)) ?? Environment.CurrentDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.Default,
                    CreateNoWindow = true
                };
                using (Process process = Process.Start(start))
                {
                    System.Threading.Tasks.Task<string> errors = process.StandardError.ReadToEndAsync();
                    string output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(600000))
                    {
                        try { process.Kill(); } catch { }
                        warnings.Add("The load check with DCS's luae.exe did not finish.");
                        fatal = true;
                        return lines;
                    }
                    errors.Wait(5000);
                    lines.AddRange(output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                    fatal = lines.Any(l => l.StartsWith("JOYPRO_FATAL|", StringComparison.Ordinal)) ||
                            !lines.Any(l => l.StartsWith("JOYPRO_LOAD_", StringComparison.Ordinal));
                }
            }
            catch (Exception ex)
            {
                warnings.Add("The load check with DCS's luae.exe failed: " + ex.Message);
                fatal = true;
            }
            finally
            {
                TryDelete(script);
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch { }
            }
            return lines;
        }

        private static string LuaEscape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'");
        }

        // ---- Helpers ----

        private static DcsStripResult NewResult(string action, StripOptions options)
        {
            DcsStripResult result = new DcsStripResult();
            result.Action = action;
            result.DryRun = options.DryRun;
            result.InstallPath = ResolveInstallPath(options.InstallPath);
            result.SavedGamesPaths = ResolveSavedGamesPaths(options.SavedGamesPath);
            result.DcsVersion = ReadDcsVersion(result.InstallPath);
            return result;
        }

        private static bool CheckInstall(DcsStripResult result)
        {
            if (string.IsNullOrWhiteSpace(result.InstallPath) || !Directory.Exists(result.InstallPath))
            {
                result.Errors.Add("DCS World was not found. Set its folder in Settings > Games or pass --install-path.");
                return false;
            }
            if (!Directory.Exists(Path.Combine(result.InstallPath, "Mods")) && !Directory.Exists(Path.Combine(result.InstallPath, "Config")))
            {
                result.Errors.Add("No Mods or Config folder in " + result.InstallPath + ": this is not a DCS World folder.");
                return false;
            }
            return true;
        }

        private static bool RefuseWhileDcsRuns(DcsStripResult result)
        {
            List<string> running = Games.GameProcessGuard.FindRunning(DcsProcessNames);
            if (running.Count == 0)
                return true;
            result.Errors.Add("DCS is running (" + string.Join(", ", running) + "). Close DCS and its updater first.");
            return false;
        }

        private static void Remember(DcsDefaultsState state, DcsStripResult result)
        {
            state.InstallPath = result.InstallPath;
            state.DcsVersion = result.DcsVersion;
            state.LastApplied = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            SaveState(state, result.Warnings);
        }

        private static DcsDefaultsState LoadStateOr(DcsDefaultsState fallback, StripOptions options)
        {
            return options.DryRun ? fallback : LoadState();
        }

        private static void FillState(DcsStripResult result, DcsDefaultsState state)
        {
            result.StripEnabled = state.StripEnabled;
            result.RemoveDeviceLuas = state.RemoveDeviceLuas;
            result.CleanProfilesStale = state.CleanProfilesStale;
            if (result.Action == "status" || result.Action == "auto")
                result.SkipUiLayer = state.SkipUiLayer;
        }

        private static void Merge(DcsStripResult target, DcsStripResult part)
        {
            target.ProfileFiles = Math.Max(target.ProfileFiles, part.ProfileFiles);
            target.Patched += part.Patched;
            target.AlreadyPatched += part.AlreadyPatched;
            target.LoadChecked += part.LoadChecked;
            target.LoadCompared += part.LoadCompared;
            target.DefaultBindsSwitchedOff += part.DefaultBindsSwitchedOff;
            target.DeviceFilesInInstall += part.DeviceFilesInInstall;
            target.DeviceFilesMoved += part.DeviceFilesMoved;
            target.DeviceFilesBackedUp = Math.Max(target.DeviceFilesBackedUp, part.DeviceFilesBackedUp);
            target.Files.AddRange(part.Files);
            target.Warnings.AddRange(part.Warnings);
            target.Errors.AddRange(part.Errors);
        }

        private static void WriteAtomically(string path, byte[] bytes)
        {
            string temp = path + ".joypro-tmp";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
            }
        }

        private static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
        }

        public static string ToText(DcsStripResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DCS default binds in the install (" + result.Action + (result.DryRun ? ", dry run" : string.Empty) + ")");
            sb.AppendLine("Install: " + result.InstallPath);
            if (result.SavedGamesPaths.Count > 0)
                sb.AppendLine("Saved Games: " + string.Join(" ; ", result.SavedGamesPaths));
            sb.AppendLine("DCS Version: " + (result.DcsVersion.Length == 0 ? "(unknown)" : result.DcsVersion));
            sb.AppendLine("Strip Enabled: " + result.StripEnabled);
            sb.AppendLine("Skip UI Layer: " + result.SkipUiLayer);
            sb.AppendLine("Remove Device Files: " + result.RemoveDeviceLuas);
            sb.AppendLine("Profile Files: " + result.ProfileFiles);
            sb.AppendLine("Patched: " + result.Patched);
            sb.AppendLine("Already Patched: " + result.AlreadyPatched);
            sb.AppendLine("Not Patched: " + result.NotPatched);
            sb.AppendLine("Restored: " + result.Restored);
            sb.AppendLine("Load Checked: " + result.LoadChecked + " (compared with the original by DCS's loader: " + result.LoadCompared + ", default binds switched off: " + result.DefaultBindsSwitchedOff + ")");
            sb.AppendLine("Device Files In Install: " + result.DeviceFilesInInstall);
            sb.AppendLine("Device Files Moved: " + result.DeviceFilesMoved);
            sb.AppendLine("Device Files Restored: " + result.DeviceFilesRestored);
            sb.AppendLine("Device Files Kept By JoyPro: " + result.DeviceFilesBackedUp);
            sb.AppendLine("Needs Reapply: " + result.NeedsReapply);
            sb.AppendLine("Clean Profiles Stale: " + result.CleanProfilesStale);
            sb.AppendLine("Note: " + result.IntegrityNote);
            if (result.Files.Count > 0)
            {
                sb.AppendLine("Files:");
                foreach (string file in result.Files)
                    sb.AppendLine("  " + file);
            }
            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                foreach (string warning in result.Warnings)
                    sb.AppendLine("  - " + warning);
            }
            if (result.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                foreach (string error in result.Errors)
                    sb.AppendLine("  - " + error);
            }
            return sb.ToString();
        }
    }
}
