using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Games;
using JoyPro.Logging;

namespace JoyPro.Engine
{
    // engine support bundle [--game <id>] [--days N] [--include-profile true|false] [--history-cap-mb N] [--list] --out <zip>
    // (docs/logging.md "Support file"): one zip a tester sends when something does not work, made on the user's PC and
    // never uploaded, so nobody has to ask for a second file. It holds:
    //   - summary.txt: versions, system, JoyPro's folders, per game the environment, the connected devices, every part
    //     with its size and what was trimmed, the open profile and its number of history versions;
    //   - logs/ (all of JoyPro's logs, GUI, engine, tray and watcher, force feedback: at least the last 14 days and back
    //     to the oldest history version of the open profile, at most 100 MB, the oldest trimmed first), crash-reports/
    //     and errors/last-errors.txt (the last warnings and errors with a stack trace from those logs);
    //   - state/: JoyPro's settings (ui-settings.json, export-settings.json, ...), its data folder's state files
    //     (data-migration.json, update and pack records), the versions of the game lists and the installed packs;
    //   - devices/: the device list and the vJoy / HidHide state;
    //   - profile/ (personal, --include-profile): the open profile and its history (EngineSupportBundle.Profile.cs);
    //   - games/<id>/: the watcher's record, the list of JoyPro's backups and the newest backup set, for MSFS the
    //     profiles JoyPro reads; personal (--include-profile): current/, the game's bind files as they are now.
    // Every text goes through LogScrubber (user folder -> %USERPROFILE%, user and PC name, e-mail, secrets, Steam ids);
    // the profile and its history get the same on the strings inside (copies only; the originals are only read).
    // Other binary files are left out. --list prints what would be included without writing anything.
    internal static partial class EngineSupportBundle
    {
        public const int DefaultDays = 14;
        public const long DefaultLogCapBytes = 100L * 1024 * 1024;
        public const long DefaultHistoryCapBytes = 50L * 1024 * 1024;
        private const long MaxTextFile = 512 * 1024;
        private const long MaxGameFile = 4L * 1024 * 1024;
        private const long GameFilesCapBytes = 50L * 1024 * 1024;
        private const int MaxProfilesPerGame = 80;
        private const int MaxErrors = 50;

        // The parts in the order of summary.txt (personal parts only with --include-profile).
        internal static readonly string[][] Parts =
        {
            new[] { "summary", "summary.txt: versions, system, the games' environment, the devices and this list" },
            new[] { "logs", "JoyPro's logs (GUI, engine, tray and watcher, force feedback)" },
            new[] { "errors", "crash reports and the last errors with their stack traces" },
            new[] { "state", "JoyPro's settings and state (game list and pack versions, updates, data folder)" },
            new[] { "devices", "devices, vJoy and HidHide" },
            new[] { "profile", "the open profile and its history (your binds and device names)" },
            new[] { "joypro1", "JoyPro 1's profiles from Documents\\JoyPro (your old binds and device names)" },
            new[] { "games", "per game: the watcher's record, JoyPro's backups, MSFS profiles read" },
            new[] { "game-binds", "per game: the game's current bind files (your binds and device names)" }
        };

        internal sealed class Entry
        {
            public string Name;
            public string Source;
            public string GeneratedText;
            public byte[] Bytes;
            public string What;
            public long Size;
            public string Part;
            // Only the end of the file (a long text log of the settings folder).
            public long TailBytes;
            // A game file: read only when it does not change while it is read.
            public bool GameFile;
            // Bytes holds the cleaned text (compressed in the zip); false: Bytes is binary as it is (stored).
            public bool Prepared;
            // Made while the zip is written (it needs what was written before, or takes a moment).
            public Action<ZipArchive, Bundle> Writer;
        }

        internal sealed class Options
        {
            public string Game;
            public int Days = DefaultDays;
            public bool IncludePersonal;
            public string ProfilePath;
            public long HistoryCapBytes = DefaultHistoryCapBytes;
            public long LogCapBytes = DefaultLogCapBytes;
            public DateTime Now = DateTime.Now;
        }

        internal sealed class Bundle
        {
            public Options Options;
            public List<string> Games = new List<string>();
            public List<Entry> Entries = new List<Entry>();
            public List<string> Notes = new List<string>();
            public DateTime LogsFrom;
            public string LogWindow = string.Empty;
            public ProfileSource Profile;
            public string ProfileLine = string.Empty;
            public List<string> Errors = new List<string>();
            public Dictionary<string, long> WrittenBytes = new Dictionary<string, long>();
            public Dictionary<string, int> WrittenFiles = new Dictionary<string, int>();
            public bool Writing;
        }

        public static int Run(List<string> args, string zipPath, bool textOutput, out string payload)
        {
            payload = string.Empty;
            List<string> rest = (args ?? new List<string>()).ToList();
            string action = rest.Count == 0 ? string.Empty : rest[0].ToLowerInvariant();
            if (action == "unpack")
            {
                rest.RemoveAt(0);
                return RunUnpack(rest, textOutput, out payload);
            }
            if (action != "bundle")
            {
                payload = "Unknown support action: " + action + ". Use: engine support bundle [--game <id>] [--days N] [--include-profile true|false] [--list] --out <zip>, or engine support unpack <zip> --to <folder>.";
                return 1;
            }
            rest.RemoveAt(0);
            Options options = new Options { ProfilePath = EngineProfileHistory.ProfilePath ?? string.Empty };
            bool listOnly = Pop(rest, "--list");
            options.Game = PopValue(rest, "--game");
            string daysText = PopValue(rest, "--days");
            int days;
            if (!string.IsNullOrWhiteSpace(daysText) && int.TryParse(daysText, NumberStyles.Integer, CultureInfo.InvariantCulture, out days) && days >= 1)
                options.Days = days;
            string include = PopValue(rest, "--include-profile");
            if (include == null)
                options.IncludePersonal = !string.IsNullOrWhiteSpace(options.ProfilePath);
            else if (string.Equals(include, "true", StringComparison.OrdinalIgnoreCase) || include == "1" || string.Equals(include, "on", StringComparison.OrdinalIgnoreCase))
                options.IncludePersonal = true;
            else if (string.Equals(include, "false", StringComparison.OrdinalIgnoreCase) || include == "0" || string.Equals(include, "off", StringComparison.OrdinalIgnoreCase))
                options.IncludePersonal = false;
            else
            {
                payload = "--include-profile takes true or false, not " + include + ".";
                return 1;
            }
            string capText = PopValue(rest, "--history-cap-mb");
            double capMb;
            if (capText != null)
            {
                if (!double.TryParse(capText, NumberStyles.Float, CultureInfo.InvariantCulture, out capMb) || capMb < 0)
                {
                    payload = "--history-cap-mb takes a number of megabytes, not " + capText + ".";
                    return 1;
                }
                options.HistoryCapBytes = (long)(capMb * 1024 * 1024);
            }
            if (rest.Count > 0)
            {
                payload = "Unknown support bundle argument(s): " + string.Join(" ", rest);
                return 1;
            }
            if (!listOnly && string.IsNullOrWhiteSpace(zipPath))
            {
                payload = "engine support bundle needs --out <zip> (or --list to see what it would hold).";
                return 1;
            }
            EngineProgress.Report(0, 1, "Collecting what the support file holds");
            Bundle bundle = Collect(options);
            string written = null;
            if (!listOnly)
            {
                written = Path.GetFullPath(zipPath);
                Write(bundle, written);
                JoyProLog.Info("Support", "Support file written: " + written + " (" + bundle.Entries.Count.ToString(CultureInfo.InvariantCulture) + " entries, profile " +
                                          (bundle.Profile == null ? "not included" : "included") + ")");
            }
            List<object> parts = PartTotals(bundle).Select(p => (object)new { Part = p.Key, What = PartWhat(p.Key), Files = p.Value.Key, Bytes = p.Value.Value }).ToList();
            if (!textOutput)
            {
                payload = EngineSerialization.ToJson(new
                {
                    Zip = written ?? string.Empty,
                    ListOnly = listOnly,
                    Days = options.Days,
                    LogsFrom = bundle.LogsFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Game = options.Game ?? string.Empty,
                    IncludeProfile = options.IncludePersonal,
                    Profile = bundle.Profile == null ? string.Empty : bundle.Profile.ZipName,
                    HistoryVersions = bundle.Profile == null ? 0 : bundle.Profile.Versions,
                    HistoryIncluded = bundle.Profile == null ? 0 : bundle.Profile.IncludedVersions,
                    HistoryLeftOut = bundle.Profile == null ? new List<int>() : bundle.Profile.LeftOut,
                    Parts = parts,
                    Notes = bundle.Notes,
                    Entries = bundle.Entries.Select(e => new { e.Name, e.What, e.Size, e.Part }).ToList()
                });
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(listOnly ? "Support file contents (not written)" : "Support file: " + written);
            sb.AppendLine("Entries: " + bundle.Entries.Count.ToString(CultureInfo.InvariantCulture) + ", " + (bundle.Entries.Sum(e => e.Size) / 1024).ToString(CultureInfo.InvariantCulture) + " KB before compression");
            sb.AppendLine(bundle.ProfileLine);
            sb.AppendLine("Parts:");
            foreach (KeyValuePair<string, KeyValuePair<int, long>> part in PartTotals(bundle))
                sb.AppendLine("  + " + part.Key + " | " + PartWhat(part.Key) + " | " + part.Value.Key.ToString(CultureInfo.InvariantCulture) + " file(s) | " + FormatBytes(part.Value.Value));
            if (bundle.Notes.Count > 0)
            {
                sb.AppendLine("Trimmed or left out:");
                foreach (string note in bundle.Notes)
                    sb.AppendLine("  ! " + note);
            }
            foreach (Entry entry in bundle.Entries)
                sb.AppendLine("  - " + entry.Name + " | " + entry.What + " | " + entry.Size.ToString(CultureInfo.InvariantCulture) + " bytes");
            payload = sb.ToString();
            return 0;
        }

        // What the bundle holds (sizes of the files before scrubbing).
        internal static Bundle Collect(Options options)
        {
            Bundle bundle = new Bundle { Options = options };
            bundle.Games = SelectGames(options.Game);

            // The profile first: its oldest history version sets how far back the logs go.
            if (options.IncludePersonal)
                bundle.Profile = PlanProfile(bundle);
            else
                bundle.ProfileLine = "Profile: not included (--include-profile false / the box in Settings > Report a problem was off)";

            bundle.LogsFrom = options.Now.AddDays(-options.Days);
            bundle.LogWindow = "the last " + options.Days.ToString(CultureInfo.InvariantCulture) + " days";
            if (bundle.Profile != null && bundle.Profile.OldestUtc.HasValue && bundle.Profile.OldestUtc.Value.ToLocalTime() < bundle.LogsFrom)
            {
                bundle.LogsFrom = bundle.Profile.OldestUtc.Value.ToLocalTime();
                bundle.LogWindow = "back to the oldest history version of the profile (" + bundle.LogsFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")";
            }
            AddLogs(bundle);
            AddState(bundle);
            bundle.Entries.Add(new Entry { Name = "devices/devices.txt", Part = "devices", What = "the connected devices (VID:PID, GUIDs, HID names)", Writer = (zip, b) => Put(zip, b, "devices", "devices/devices.txt", EngineDiagnostics.DevicesText()) });
            bundle.Entries.Add(new Entry { Name = "devices/vjoy-hidhide.txt", Part = "devices", What = "vJoy, ViGEm and HidHide: installed, devices, state, JoyPro's session", Writer = (zip, b) => Put(zip, b, "devices", "devices/vjoy-hidhide.txt", VJoyText()) });
            if (bundle.Profile != null)
                AddProfile(bundle);
            if (options.IncludePersonal)
                AddJoyPro1Profiles(bundle);
            foreach (string id in bundle.Games)
                AddGame(bundle, id);
            bundle.Entries.Add(new Entry { Name = "errors/last-errors.txt", Part = "errors", What = "the last warnings and errors with a stack trace from the logs", Writer = WriteErrors });
            bundle.Entries.Add(new Entry { Name = "summary.txt", Part = "summary", What = "versions, system, the games' environment, the devices, the parts and what was trimmed", Writer = WriteSummary });
            return bundle;
        }

        private static void AddLogs(Bundle bundle)
        {
            string logFolder = JoyProLog.DefaultFolder();
            long logBytes = 0;
            List<FileInfo> trimmed = new List<FileInfo>();
            foreach (FileInfo log in Files(logFolder, "*.log", SearchOption.TopDirectoryOnly).Where(f => f.LastWriteTime >= bundle.LogsFrom).OrderByDescending(f => f.LastWriteTime))
            {
                if (logBytes + log.Length > bundle.Options.LogCapBytes)
                {
                    trimmed.Add(log);
                    continue;
                }
                logBytes += log.Length;
                bundle.Entries.Add(new Entry { Name = "logs/" + log.Name, Source = log.FullName, What = "JoyPro log", Size = log.Length, Part = "logs" });
            }
            if (trimmed.Count > 0)
                bundle.Notes.Add("logs: " + trimmed.Count.ToString(CultureInfo.InvariantCulture) + " older log file(s) of " + FormatBytes(trimmed.Sum(f => f.Length)) + " left out (cap " + FormatBytes(bundle.Options.LogCapBytes) +
                                 "), written " + trimmed.Min(f => f.LastWriteTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " to " + trimmed.Max(f => f.LastWriteTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                                 ": " + string.Join(", ", trimmed.Select(f => f.Name).Take(30)) + (trimmed.Count > 30 ? ", ..." : string.Empty));
            string crashFolder = Path.Combine(Path.GetDirectoryName(logFolder.TrimEnd('\\')) ?? logFolder, "CrashReports");
            List<FileInfo> crashes = Files(crashFolder, "*.txt", SearchOption.AllDirectories).Where(f => f.LastWriteTime >= bundle.LogsFrom && f.Length <= MaxTextFile).OrderByDescending(f => f.LastWriteTime).ToList();
            foreach (FileInfo crash in crashes.Take(40))
                bundle.Entries.Add(new Entry { Name = "crash-reports/" + Relative(crashFolder, crash.FullName), Source = crash.FullName, What = "crash report", Size = crash.Length, Part = "errors" });
            if (crashes.Count > 40)
                bundle.Notes.Add("crash reports: the " + (crashes.Count - 40).ToString(CultureInfo.InvariantCulture) + " oldest of " + crashes.Count.ToString(CultureInfo.InvariantCulture) + " left out (the newest 40 are in).");
        }

        // The GUI's settings folder (JoyPro.Watch JoyProPaths.SettingsFolder): <JOYPRO_DATA_DIR>\settings or %APPDATA%\JoyPro2.
        internal static string SettingsFolder()
        {
            string overrideDir = Environment.GetEnvironmentVariable(JoyProDataFolder.Variable);
            return string.IsNullOrWhiteSpace(overrideDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JoyPro2")
                : Path.Combine(overrideDir.Trim(), "settings");
        }

        private static readonly string[] StateExtensions = { ".json", ".txt", ".log", ".xml", ".ini", ".cfg" };

        // Folders of <data> whose files are not state (pictures, caches, the history and backups, which have parts of
        // their own); the data folder listing still counts them.
        private static readonly string[] NotState = { "ProfileHistory", "GameBackups", "Logs", "CrashReports", "Cache", "DeviceImages", "HudImages", "Media", "LayoutPreview", "Telemetry", "Usage", "AutoLayouts", "Layouts", "Backups", "Updates" };

        private static void AddState(Bundle bundle)
        {
            string settings = SettingsFolder();
            foreach (FileInfo file in Files(settings, "*", SearchOption.TopDirectoryOnly).Where(f => StateExtensions.Contains(f.Extension.ToLowerInvariant())))
                AddStateFile(bundle, "state/settings/" + file.Name, file, "a setting of the app (" + file.Name + ")");
            string data = GameInstalls.DataRoot();
            foreach (FileInfo file in Files(data, "*", SearchOption.TopDirectoryOnly).Where(f => StateExtensions.Contains(f.Extension.ToLowerInvariant())))
                AddStateFile(bundle, "state/data/" + file.Name, file, "JoyPro's state (" + file.Name + ")");
            string[][] picks =
            {
                new[] { "Logs", "log-settings.txt" }, new[] { "Updates", "*.json" }, new[] { "Updates", "*.log" }, new[] { "DeviceReference", "settings.json" },
                new[] { "DeviceReference\\PhotoList", "*.json" }, new[] { "Llm", "settings.json" }, new[] { "Ffb", "*.json" }, new[] { "Ffb\\Telemetry", "*.json" },
                new[] { "Themes", "*.json" }, new[] { "Sync", "*.json" }
            };
            foreach (string[] pick in picks)
            {
                foreach (FileInfo file in Files(Path.Combine(data, pick[0]), pick[1], SearchOption.TopDirectoryOnly))
                    AddStateFile(bundle, "state/data/" + pick[0].Replace('\\', '/') + "/" + file.Name, file, "JoyPro's state (" + pick[0] + ")");
            }
            foreach (FileInfo file in Files(Path.Combine(data, "Ownership"), "*.json", SearchOption.AllDirectories).Take(200))
                AddStateFile(bundle, "state/data/Ownership/" + Relative(Path.Combine(data, "Ownership"), file.FullName), file, "which game file JoyPro owns");
            bundle.Entries.Add(new Entry { Name = "state/versions.txt", Part = "state", What = "the versions of the game lists (DB) and of the installed cockpit, photo and 3D packs", Writer = (zip, b) => Put(zip, b, "state", "state/versions.txt", VersionsText(data)) });
            bundle.Entries.Add(new Entry { Name = "state/data-folder.txt", Part = "state", What = "JoyPro's data folder: files and size per folder", Writer = (zip, b) => Put(zip, b, "state", "state/data-folder.txt", DataFolderText(data, settings)) });
        }

        private static void AddStateFile(Bundle bundle, string name, FileInfo file, string what)
        {
            if (IsSmallText(file.FullName))
                bundle.Entries.Add(new Entry { Name = name, Source = file.FullName, What = what, Size = file.Length, Part = "state" });
            else if (file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                bundle.Entries.Add(new Entry { Name = name, Source = file.FullName, What = what + ", the last 512 KB", Size = MaxTextFile, TailBytes = MaxTextFile, Part = "state" });
        }

        // The game lists' versions (GameData\DB\<game>\source-version.txt, the record of engine db check-updates) and every
        // installed pack (pack.json: cockpit pictures, device photos, 3D models).
        internal static string VersionsText(string data)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Game lists in use (GameData\\DB\\<game>\\source-version.txt):");
            string db = Path.Combine(data, "GameData", "DB");
            foreach (string folder in SafeDirectories(db))
            {
                string file = Path.Combine(folder, "source-version.txt");
                string text = File.Exists(file) ? ReadTextSafe(file).Replace("\r", string.Empty).Trim().Replace("\n", " | ") : "(no source-version.txt)";
                sb.AppendLine("  " + Path.GetFileName(folder) + ": " + text + " (folder written " + Directory.GetLastWriteTime(folder).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ")");
            }
            sb.AppendLine();
            sb.AppendLine("Installed packs (pack.json):");
            foreach (FileInfo pack in Files(data, "pack.json", SearchOption.AllDirectories).Where(f => !IsUnder(data, f.FullName, NotState) && f.Length <= 8192).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine("  " + Relative(data, pack.FullName) + ": " + ReadTextSafe(pack.FullName).Replace("\r", string.Empty).Replace("\n", " ").Trim());
            return sb.ToString();
        }

        internal static string DataFolderText(string data, string settings)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Data folder: " + data);
            sb.AppendLine("Settings folder: " + settings);
            sb.AppendLine("Log folder: " + JoyProLog.DefaultFolder());
            foreach (string folder in SafeDirectories(data))
            {
                List<FileInfo> files = Files(folder, "*", SearchOption.AllDirectories).ToList();
                sb.AppendLine("  " + Path.GetFileName(folder) + ": " + files.Count.ToString(CultureInfo.InvariantCulture) + " file(s), " + FormatBytes(files.Sum(f => f.Length)) +
                              (files.Count == 0 ? string.Empty : ", newest " + files.Max(f => f.LastWriteTime).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
            }
            return sb.ToString();
        }

        private static string VJoyText()
        {
            try
            {
                return EngineVJoy.StatusText(EngineVJoy.BuildStatus());
            }
            catch (Exception ex)
            {
                return "vJoy / HidHide state not read: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static List<string> SelectGames(string filter)
        {
            List<string> all = EngineSync.AllGameIds();
            if (!string.IsNullOrWhiteSpace(filter))
            {
                string resolved = GameInstalls.Resolve(filter);
                return all.Where(g => string.Equals(g, filter.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(g, resolved, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            List<string> installed = new List<string>();
            foreach (string id in all)
            {
                try
                {
                    GameInstall install = GameInstalls.Find(id, new List<string>());
                    if (install != null && install.Installed && !install.Disabled)
                        installed.Add(id);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    JoyProLog.Debug("Support", id + ": install not checked: " + ex.Message);
                }
            }
            return installed;
        }

        private static void WriteSummary(ZipArchive zip, Bundle bundle)
        {
            Put(zip, bundle, "summary", "summary.txt", Summary(bundle));
        }

        private static string Summary(Bundle bundle)
        {
            Options options = bundle.Options;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("JoyPro2 support file (made on this PC; nothing was uploaded)");
            sb.AppendLine("Time: " + options.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " (local), UTC offset " + TimeZoneInfo.Local.GetUtcOffset(options.Now).ToString(@"hh\:mm", CultureInfo.InvariantCulture));
            object[] attributes = typeof(EngineSupportBundle).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            sb.AppendLine("Engine: " + (attributes.Length > 0 ? ((System.Reflection.AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion : typeof(EngineSupportBundle).Assembly.GetName().Version.ToString()));
            sb.AppendLine("System: " + JoyProLog.SystemDescription());
            sb.AppendLine("JoyPro data: " + GameInstalls.DataRoot());
            sb.AppendLine("Settings: " + SettingsFolder());
            sb.AppendLine("Logs: " + JoyProLog.DefaultFolder() + " (" + bundle.LogWindow + ", from " + bundle.LogsFrom.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ")");
            sb.AppendLine(bundle.ProfileLine);
            sb.AppendLine("Games: " + (bundle.Games.Count == 0 ? "none installed" : string.Join(", ", bundle.Games)));
            sb.AppendLine();
            sb.AppendLine("Parts (size before compression):");
            foreach (KeyValuePair<string, KeyValuePair<int, long>> part in PartTotals(bundle))
                sb.AppendLine("  " + part.Key.PadRight(11) + part.Value.Key.ToString(CultureInfo.InvariantCulture).PadLeft(6) + " file(s) " + (part.Key == "summary" ? "this file" : FormatBytes(part.Value.Value)).PadLeft(10) + "  " + PartWhat(part.Key));
            sb.AppendLine();
            sb.AppendLine("Trimmed or left out:");
            if (bundle.Notes.Count == 0)
                sb.AppendLine("  nothing");
            foreach (string note in bundle.Notes)
                sb.AppendLine("  - " + note);
            sb.AppendLine();
            foreach (string id in bundle.Games)
            {
                string name;
                List<string> paths;
                try
                {
                    paths = EngineSync.ConfigPathsOf(id, out name);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    paths = new List<string>();
                    name = id;
                    sb.AppendLine(id + ": config paths not found (" + ex.Message + ")");
                }
                sb.AppendLine(EngineDiagnostics.EnvironmentText(id, paths, false));
                sb.AppendLine();
            }
            sb.AppendLine("Devices:");
            sb.Append(EngineDiagnostics.DevicesText());
            return sb.ToString();
        }

        // Per part: files and bytes (written: as written; --list: the sizes of the sources).
        private static List<KeyValuePair<string, KeyValuePair<int, long>>> PartTotals(Bundle bundle)
        {
            List<KeyValuePair<string, KeyValuePair<int, long>>> totals = new List<KeyValuePair<string, KeyValuePair<int, long>>>();
            foreach (string[] part in Parts)
            {
                string key = part[0];
                int files;
                long bytes;
                if (bundle.Writing)
                {
                    bundle.WrittenFiles.TryGetValue(key, out files);
                    bundle.WrittenBytes.TryGetValue(key, out bytes);
                }
                else
                {
                    List<Entry> entries = bundle.Entries.Where(e => e.Part == key).ToList();
                    files = entries.Count;
                    bytes = entries.Sum(e => e.Size);
                }
                if (files > 0 || key == "summary")
                    totals.Add(new KeyValuePair<string, KeyValuePair<int, long>>(key, new KeyValuePair<int, long>(Math.Max(files, key == "summary" ? 1 : 0), bytes)));
            }
            return totals;
        }

        private static string PartWhat(string key)
        {
            string[] part = Parts.FirstOrDefault(p => p[0] == key);
            return part == null ? key : part[1];
        }

        private static void AddGame(Bundle bundle, string id)
        {
            string prefix = "games/" + id + "/";
            string record = Path.Combine(GameInstalls.DataRoot(), "Sync", id + ".json");
            if (File.Exists(record))
                bundle.Entries.Add(new Entry { Name = prefix + "sync-record.json", Source = record, What = "the watcher's record of the game's state", Size = new FileInfo(record).Length, Part = "games" });
            List<GameBackupManifest> sets = GameBackups.List(id);
            if (sets.Count > 0)
            {
                StringBuilder list = new StringBuilder();
                list.AppendLine("JoyPro's backups of " + id + " (newest first; the newest set's text files are in backup-<set>/):");
                foreach (GameBackupManifest backup in sets)
                    list.AppendLine("  " + backup.Id + " | " + backup.Created + " | " + backup.Command + " | " + backup.Files.Count.ToString(CultureInfo.InvariantCulture) + " file(s)" +
                                    (backup.RegistryExports.Count > 0 ? ", " + backup.RegistryExports.Count.ToString(CultureInfo.InvariantCulture) + " registry key(s)" : string.Empty));
                string text = list.ToString();
                bundle.Entries.Add(new Entry { Name = prefix + "backups.txt", GeneratedText = text, What = "the list of JoyPro's backups (names and dates)", Size = Encoding.UTF8.GetByteCount(text), Part = "games" });
            }
            GameBackupManifest set = sets.FirstOrDefault();
            if (set != null)
            {
                string folder = Path.Combine(GameBackups.Root(), set.Id);
                string manifest = Path.Combine(folder, "manifest.json");
                if (File.Exists(manifest))
                    bundle.Entries.Add(new Entry { Name = prefix + "backup-" + set.Id + "/manifest.json", Source = manifest, What = "JoyPro's newest backup set (" + set.Command + ")", Size = new FileInfo(manifest).Length, Part = "games" });
                foreach (GameBackupFile file in set.Files.Where(f => f.Existed && !string.IsNullOrEmpty(f.Stored)))
                {
                    string stored = Path.Combine(folder, "files", file.Stored);
                    if (IsSmallText(stored))
                        bundle.Entries.Add(new Entry { Name = prefix + "backup-" + set.Id + "/" + file.Stored, Source = stored, What = "JoyPro's copy of " + file.Original + " before its export", Size = new FileInfo(stored).Length, Part = "games" });
                }
            }
            int sim = id == "MSFS2024" ? 2024 : id == "MSFS2020" ? 2020 : 0;
            if (sim > 0)
                AddMsfs(bundle.Entries, prefix, sim);
            if (bundle.Options.IncludePersonal)
                AddGameBinds(bundle, id, prefix, sim > 0);
        }

        // The game's bind files as they are now (what JoyPro imports from and exports to) and JoyPro's files next to them.
        private static void AddGameBinds(Bundle bundle, string id, string prefix, bool msfs)
        {
            List<string> files;
            string registryKey;
            try
            {
                files = EngineSync.StateFilesOf(id, out registryKey);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                bundle.Notes.Add(id + ": the game's bind files were not listed: " + ex.Message);
                return;
            }
            if (registryKey.Length > 0)
            {
                string text;
                try
                {
                    text = EngineSync.RegistryStateText(id, registryKey);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    text = "Registry key " + registryKey + " not read: " + ex.Message;
                }
                bundle.Entries.Add(new Entry { Name = prefix + "current/registry.txt", GeneratedText = text, What = "the game's binds in the registry as they are now", Size = Encoding.UTF8.GetByteCount(text), Part = "game-binds" });
                return;
            }
            if (files.Count == 0)
                return;
            string root = CommonFolder(files);
            StringBuilder listing = new StringBuilder();
            listing.AppendLine(id + ": the game's bind files as they are now (" + files.Count.ToString(CultureInfo.InvariantCulture) + "), in " + root);
            long total = 0;
            int skipped = 0;
            foreach (FileInfo file in files.Select(f => new FileInfo(f)).Where(f => f.Exists).OrderByDescending(f => f.LastWriteTime))
            {
                string status;
                if (msfs && !file.Name.EndsWith(".jp", StringComparison.OrdinalIgnoreCase) && !file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    status = "see profiles/ (decoded)";
                else if (!IsText(file.FullName, MaxGameFile))
                    status = file.Length > MaxGameFile ? "too big, not copied" : "binary, not copied";
                else if (total + file.Length > GameFilesCapBytes)
                {
                    status = "left out (cap " + FormatBytes(GameFilesCapBytes) + " per game)";
                    skipped++;
                }
                else
                {
                    total += file.Length;
                    status = "copied";
                    bundle.Entries.Add(new Entry { Name = prefix + "current/" + SafeZipPath(Relative(root, file.FullName)), Source = file.FullName, What = "the game's file as it is now", Size = file.Length, Part = "game-binds", GameFile = true });
                }
                listing.AppendLine("  " + file.FullName + " | " + file.Length.ToString(CultureInfo.InvariantCulture) + " bytes | " + file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " | " + status);
            }
            if (skipped > 0)
                bundle.Notes.Add(id + ": " + skipped.ToString(CultureInfo.InvariantCulture) + " of the oldest bind files left out (cap " + FormatBytes(GameFilesCapBytes) + " per game; games/" + id + "/current-files.txt lists them).");
            string text2 = listing.ToString();
            bundle.Entries.Add(new Entry { Name = prefix + "current-files.txt", GeneratedText = text2, What = "the list of the game's bind files (size, time, copied or not)", Size = Encoding.UTF8.GetByteCount(text2), Part = "game-binds" });
        }

        private static void AddMsfs(List<Entry> entries, string prefix, int sim)
        {
            int n = 0;
            foreach (MsfsProfileSource source in MsfsUserProfiles.UserSources(sim, new List<string>()))
            {
                foreach (string file in source.Files)
                {
                    if (++n > MaxProfilesPerGame)
                        break;
                    FileInfo info = new FileInfo(file);
                    if (!info.Exists || info.Length > MaxTextFile * 4)
                        continue;
                    string text;
                    try
                    {
                        text = MsfsUserProfiles.DecodeText(SafeFileIo.ReadAllBytes(file));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        text = "(not readable: " + ex.Message + ")";
                    }
                    entries.Add(new Entry
                    {
                        Name = prefix + "profiles/" + n.ToString("D3", CultureInfo.InvariantCulture) + "-" + source.Kind.ToString().ToLowerInvariant() + "-" + Path.GetFileName(file) + ".xml",
                        GeneratedText = "<!-- " + source.Kind + ": " + file + ", " + info.Length.ToString(CultureInfo.InvariantCulture) + " bytes, written " +
                                        info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z -->\r\n" + text,
                        What = "an input profile JoyPro reads (" + source.Kind + ")",
                        Size = info.Length,
                        Part = "games"
                    });
                }
            }
            if (sim == 2024)
            {
                string community = MsfsCommunityPackage.FindCommunityFolder();
                string package = string.IsNullOrEmpty(community) ? null : Path.Combine(community, MsfsCommunityPackage.PackageName);
                if (package != null && Directory.Exists(package))
                {
                    foreach (FileInfo file in Files(package, "*", SearchOption.AllDirectories).Where(f => IsSmallText(f.FullName)).Take(200))
                        entries.Add(new Entry { Name = prefix + "package/" + file.FullName.Substring(package.Length).TrimStart('\\').Replace('\\', '/'), Source = file.FullName, What = "JoyPro's preset package", Size = file.Length, Part = "games" });
                }
                foreach (FileInfo file in Files(MsfsIntegration.ImportFilesFolder(), "*.xml", SearchOption.TopDirectoryOnly).Where(f => f.Length <= MaxTextFile))
                    entries.Add(new Entry { Name = prefix + "import-files/" + file.Name, Source = file.FullName, What = "an import file JoyPro wrote", Size = file.Length, Part = "games" });
            }
            string records = SimRecordsText(sim);
            entries.Add(new Entry { Name = prefix + "sim-records.txt", GeneratedText = records, What = "the sim's device records (profile_00) as JoyPro reads them", Size = Encoding.UTF8.GetByteCount(records), Part = "games" });
        }

        internal static string SimRecordsText(int sim)
        {
            StringBuilder sb = new StringBuilder();
            MsfsSimProfile profile = MsfsSimProfile.Load(sim, null);
            sb.AppendLine("MSFS " + sim.ToString(CultureInfo.InvariantCulture) + " device records (profile_00, read only): " + (profile.Sources.Count == 0 ? "none found" : string.Join(", ", profile.Sources)));
            foreach (KeyValuePair<Guid, List<MsfsSimProfile.Record>> device in profile.Records.OrderBy(r => r.Key.ToString()))
            {
                int productId;
                sb.AppendLine("{" + device.Key.ToString().ToUpperInvariant() + "} product ID " + (profile.ProductIds.TryGetValue(device.Key, out productId) ? productId.ToString(CultureInfo.InvariantCulture) : "?") + ": " +
                              device.Value.Count.ToString(CultureInfo.InvariantCulture) + " record(s)");
                foreach (MsfsSimProfile.Record record in device.Value)
                    sb.AppendLine("  " + (record.Aircraft ?? "?") + " | " + (record.Category ?? "-") + " | general " + (record.General ?? "-") + " | category " + (record.CategoryPreset ?? "-") + " | aircraft " + (record.AircraftPreset ?? "-"));
            }
            return sb.ToString();
        }

        // ---- writing ------------------------------------------------------------------------------------------------

        private static void Write(Bundle bundle, string zipPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
            string temp = zipPath + ".tmp";
            bundle.Writing = true;
            int index = 0;
            int total = bundle.Entries.Count;
            // How long each part took (the log; a slow support file shows where the time went).
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            Dictionary<string, long> partMs = new Dictionary<string, long>();
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                // Reading and cleaning the files runs on a few threads, 64 entries at a time (the zip is written in
                // order; the writers that need what came before run in their turn).
                int threads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
                for (int chunk = 0; chunk < total; chunk += 64)
                {
                    List<Entry> batch = bundle.Entries.Skip(chunk).Take(64).ToList();
                    long prepareStarted = clock.ElapsedMilliseconds;
                    System.Threading.Tasks.Parallel.ForEach(batch.Where(e => e.Writer == null && e.Bytes == null), new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads }, e => Prepare(e, bundle));
                    foreach (Entry entry in batch)
                    {
                        index++;
                        if (index == 1 || index % 25 == 0 || index == total)
                            EngineProgress.Report(index, total, "Writing the support file", entry.Name);
                        long started = clock.ElapsedMilliseconds;
                        WriteEntry(zip, bundle, entry);
                        string key = entry.Writer != null ? entry.Name : entry.Part;
                        long ms;
                        partMs.TryGetValue(key, out ms);
                        partMs[key] = ms + clock.ElapsedMilliseconds - started;
                    }
                    long prepared;
                    partMs.TryGetValue("reading", out prepared);
                    partMs["reading"] = prepared + clock.ElapsedMilliseconds - prepareStarted;
                }
            }
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            File.Move(temp, zipPath);
            JoyProLog.Info("Support", "Support file timing (ms): " + string.Join(", ", partMs.Select(p => p.Key + "=" + p.Value.ToString(CultureInfo.InvariantCulture))));
        }

        private static void WriteEntry(ZipArchive zip, Bundle bundle, Entry entry)
        {
            if (entry.Writer != null)
            {
                try
                {
                    entry.Writer(zip, bundle);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is ArgumentException)
                {
                    bundle.Notes.Add(entry.Name + ": not written: " + ex.Message);
                }
                return;
            }
            if (entry.Bytes == null)
                Prepare(entry, bundle);
            PutBytes(zip, bundle, entry.Part, entry.Name, entry.Bytes, entry.Prepared);
            entry.Bytes = null;
        }

        // Reads and cleans a file entry into Bytes (thread-safe: the errors list is locked).
        private static void Prepare(Entry entry, Bundle bundle)
        {
            string text = entry.GeneratedText;
            if (text == null)
            {
                try
                {
                    text = entry.GameFile ? ReadStableText(entry.Source) : entry.TailBytes > 0 ? ReadTail(entry.Source, entry.TailBytes) : ReadText(entry.Source);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    text = "(not readable: " + ex.Message + ")";
                }
            }
            if (entry.Part == "logs")
            {
                List<string> errors = new List<string>();
                CollectErrors(text, Path.GetFileName(entry.Source), errors);
                lock (bundle.Errors)
                    bundle.Errors.AddRange(errors);
            }
            entry.Bytes = new UTF8Encoding(false).GetBytes(LogScrubber.Scrub(text ?? string.Empty));
            entry.Prepared = true;
        }

        // A scrubbed text entry.
        internal static void Put(ZipArchive zip, Bundle bundle, string part, string name, string text)
        {
            PutBytes(zip, bundle, part, name, new UTF8Encoding(false).GetBytes(LogScrubber.Scrub(text ?? string.Empty)), true);
        }

        internal static void PutBytes(ZipArchive zip, Bundle bundle, string part, string name, byte[] bytes, bool compress)
        {
            ZipArchiveEntry zipEntry = zip.CreateEntry(name, compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
            using (Stream output = zipEntry.Open())
                output.Write(bytes, 0, bytes.Length);
            long sum;
            bundle.WrittenBytes.TryGetValue(part, out sum);
            bundle.WrittenBytes[part] = sum + bytes.Length;
            int count;
            bundle.WrittenFiles.TryGetValue(part, out count);
            bundle.WrittenFiles[part] = count + 1;
        }

        // ---- the last errors ----------------------------------------------------------------------------------------

        private static readonly Regex LogHead = new Regex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} ", RegexOptions.CultureInvariant);
        private static readonly Regex ErrorHead = new Regex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} (WARN |ERROR|FATAL)", RegexOptions.CultureInvariant);

        // Warnings and errors of a log that carry a stack trace ("   at ..." lines), as "<file>: <record>".
        internal static void CollectErrors(string text, string file, List<string> into)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("   at ", StringComparison.Ordinal) < 0)
                return;
            StringBuilder current = null;
            bool hasStack = false;
            int lines = 0;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (LogHead.IsMatch(line))
                {
                    if (current != null && hasStack)
                        into.Add(current.ToString());
                    current = null;
                    hasStack = false;
                    lines = 0;
                    if (ErrorHead.IsMatch(line))
                        current = new StringBuilder(file + ": " + line + "\n");
                    continue;
                }
                if (current == null || ++lines > 80)
                    continue;
                if (line.TrimStart().StartsWith("at ", StringComparison.Ordinal))
                    hasStack = true;
                current.Append(line).Append('\n');
            }
            if (current != null && hasStack)
                into.Add(current.ToString());
        }

        private static void WriteErrors(ZipArchive zip, Bundle bundle)
        {
            // "<file>: yyyy-MM-dd HH:mm:ss.fff ..." sorted by the time of the line (the newest last), at most MaxErrors.
            List<string> errors = bundle.Errors
                .OrderBy(e => { int colon = e.IndexOf(": ", StringComparison.Ordinal); return colon < 0 ? e : e.Substring(colon + 2, Math.Min(23, e.Length - colon - 2)); }, StringComparer.Ordinal)
                .ToList();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("The last " + Math.Min(errors.Count, MaxErrors).ToString(CultureInfo.InvariantCulture) + " of " + errors.Count.ToString(CultureInfo.InvariantCulture) +
                          " warnings and errors with a stack trace in the logs of this file (oldest first). Crash reports: crash-reports/.");
            sb.AppendLine();
            foreach (string error in errors.Skip(Math.Max(0, errors.Count - MaxErrors)))
                sb.AppendLine(error);
            Put(zip, bundle, "errors", "errors/last-errors.txt", sb.ToString());
        }

        // ---- reading ------------------------------------------------------------------------------------------------

        // Text of a file (UTF-8, or UTF-16 with its byte order mark).
        private static string ReadText(string path)
        {
            byte[] bytes = SafeFileIo.ReadAllBytes(path);
            return MsfsUserProfiles.DecodeText(bytes);
        }

        private static string ReadTextSafe(string path)
        {
            try
            {
                return ReadText(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "(not readable: " + ex.Message + ")";
            }
        }

        // A game file only when it did not change while it was read (the game or JoyPro writing it): read twice at most.
        private static string ReadStableText(string path)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                FileInfo before = new FileInfo(path);
                if (!before.Exists)
                    return "(gone while the support file was made)";
                string text = ReadText(path);
                FileInfo after = new FileInfo(path);
                if (after.Exists && after.Length == before.Length && after.LastWriteTimeUtc == before.LastWriteTimeUtc)
                    return text;
                System.Threading.Thread.Sleep(300);
            }
            return "(the file was being written while the support file was made; not copied)";
        }

        private static string ReadTail(string path, long bytes)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, stream.Length - bytes);
                stream.Seek(start, SeekOrigin.Begin);
                byte[] buffer = new byte[stream.Length - start];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0)
                        break;
                    read += n;
                }
                return (start > 0 ? "(the last " + FormatBytes(bytes) + " of " + FormatBytes(stream.Length) + ")\n" : string.Empty) + Encoding.UTF8.GetString(buffer, 0, read);
            }
        }

        // A file of at most 512 KB without NUL bytes (UTF-16 with its byte order mark counts as text).
        internal static bool IsSmallText(string path)
        {
            return IsText(path, MaxTextFile);
        }

        internal static bool IsText(string path, long maxBytes)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length > maxBytes)
                    return false;
                byte[] head = new byte[Math.Min(8192, (int)info.Length)];
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    stream.Read(head, 0, head.Length);
                if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
                    return true;
                return !head.Contains((byte)0);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static IEnumerable<FileInfo> Files(string folder, string pattern, SearchOption option)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    return Enumerable.Empty<FileInfo>();
                return new DirectoryInfo(folder).GetFiles(pattern, option);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                JoyProLog.Debug("Support", "Not listed: " + folder + ": " + ex.Message);
                return Enumerable.Empty<FileInfo>();
            }
        }

        private static IEnumerable<string> SafeDirectories(string folder)
        {
            try
            {
                return Directory.Exists(folder) ? Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        private static bool IsUnder(string root, string path, IEnumerable<string> folders)
        {
            string relative = Relative(root, path);
            int slash = relative.IndexOf('/');
            string first = slash < 0 ? string.Empty : relative.Substring(0, slash);
            return folders.Any(f => string.Equals(f, first, StringComparison.OrdinalIgnoreCase));
        }

        // "a/b/c.txt" of a path under root (forward slashes, for zip names).
        internal static string Relative(string root, string path)
        {
            string full = Path.GetFullPath(path);
            string prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
            string relative = full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(prefix.Length) : Path.GetFileName(full);
            return relative.Replace('\\', '/');
        }

        private static string CommonFolder(List<string> files)
        {
            string common = Path.GetDirectoryName(files[0]) ?? string.Empty;
            foreach (string file in files.Skip(1))
            {
                string folder = Path.GetDirectoryName(file) ?? string.Empty;
                while (common.Length > 0 && !(folder + "\\").StartsWith(common.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    common = Path.GetDirectoryName(common) ?? string.Empty;
            }
            return common;
        }

        // A zip entry name made of a path: the scrubber's replacements ("<user>") without the characters Windows refuses.
        internal static string SafeZipPath(string path)
        {
            string scrubbed = LogScrubber.Scrub(path ?? string.Empty);
            StringBuilder sb = new StringBuilder(scrubbed.Length);
            foreach (char c in scrubbed)
                sb.Append(c == '<' || c == '>' || c == ':' || c == '"' || c == '|' || c == '?' || c == '*' || c == '%' ? '_' : c);
            return sb.ToString();
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024)
                return (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (bytes >= 1024)
                return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        private static bool Pop(List<string> args, string name)
        {
            int index = args.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return false;
            args.RemoveAt(index);
            return true;
        }

        private static string PopValue(List<string> args, string name)
        {
            int index = args.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= args.Count)
                return null;
            string value = args[index + 1];
            args.RemoveRange(index, 2);
            return value;
        }
    }
}
