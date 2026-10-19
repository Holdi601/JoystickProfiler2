using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // Versioned backups of every game file JoyPro changes. Before the first write to a file in an engine run, the
    // file is copied into a backup set (<data>\GameBackups\<id>\, one set per game and run) whose manifest lists the
    // original paths; files that did not exist yet are recorded too, so a restore removes them again. "engine backups"
    // lists, shows and restores sets. A restore first backs up the current files, so it can be undone as well. The
    // newest KeepPerGame sets per game are kept. Dry runs write nothing.
    internal static class GameBackups
    {
        public const string DataFolderVariable = "JOYPRO_DATA_DIR";
        public const int KeepPerGame = 30;
        private const string ManifestName = "manifest.json";

        private static string currentGame = string.Empty;
        private static string currentCommand = string.Empty;
        private static bool disabled;
        private static readonly Dictionary<string, OpenSet> OpenSets = new Dictionary<string, OpenSet>(StringComparer.OrdinalIgnoreCase);

        private sealed class OpenSet
        {
            public string Folder = string.Empty;
            public GameBackupManifest Manifest = new GameBackupManifest();
            public HashSet<string> Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // Called once per engine run with the game the command works on ("DCS", "StarCitizen", "IL2Game", a registry id).
        public static void Begin(string game, string command, bool dryRun)
        {
            currentGame = game ?? string.Empty;
            currentCommand = command ?? string.Empty;
            disabled = dryRun;
            EngineDiagnostics.BeginRun(currentGame, currentCommand);
            JoyPro.Logging.JoyProLog.Debug("Backups", "Backup scope: game=" + (currentGame.Length == 0 ? "-" : currentGame) + (dryRun ? ", dry run (nothing is written)" : string.Empty) + ", store " + Root());
        }

        public static string Root()
        {
            string root = Environment.GetEnvironmentVariable(DataFolderVariable);
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro");
            return Path.Combine(root, "GameBackups");
        }

        // Before JoyPro writes, replaces, moves or deletes a game file.
        public static void Protect(string filePath, List<string> warnings)
        {
            Protect(currentGame, filePath, warnings);
        }

        public static void Protect(string game, string filePath, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;
            if (disabled)
            {
                JoyPro.Logging.JoyProLog.Debug("Backups", "Dry run, not changed: " + filePath);
                return;
            }
            try
            {
                string fullPath = Path.GetFullPath(filePath);
                if (fullPath.StartsWith(Root(), StringComparison.OrdinalIgnoreCase))
                    return;
                OpenSet set = Open(string.IsNullOrWhiteSpace(game) ? "Other" : game);
                if (!set.Paths.Add(fullPath))
                    return;
                EngineDiagnostics.BeforeWrite(game, fullPath);
                GameBackupFile entry = new GameBackupFile { Original = fullPath, Existed = File.Exists(fullPath) };
                if (entry.Existed)
                {
                    entry.Stored = (set.Manifest.Files.Count + 1).ToString("D4", CultureInfo.InvariantCulture) + "_" + Path.GetFileName(fullPath);
                    string target = Path.Combine(set.Folder, "files", entry.Stored);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    SafeFileIo.CopyFile(fullPath, target, true, ownTarget: true);
                    entry.Size = new FileInfo(target).Length;
                }
                set.Manifest.Files.Add(entry);
                File.WriteAllText(Path.Combine(set.Folder, ManifestName), EngineSerialization.ToJson(set.Manifest), new UTF8Encoding(false));
                JoyPro.Logging.JoyProLog.Info("Backups", "Game file about to change (" + (string.IsNullOrWhiteSpace(game) ? "Other" : game) + "): " + fullPath +
                                                         (entry.Existed ? " (" + entry.Size.ToString(CultureInfo.InvariantCulture) + " bytes, backed up to set " + set.Manifest.Id + ")" : " (new file, set " + set.Manifest.Id + ")"));
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Warn("Backups", "Could not back up '" + filePath + "' before changing it", ex);
                warnings?.Add("Could not back up '" + filePath + "' before changing it: " + ex.Message);
            }
        }

        // Before JoyPro changes values of a registry key (Unity PlayerPrefs games such as Nuclear Option): the values
        // whose names start with ownedPrefix (the ones JoyPro may change or create) go into the run's backup set as a
        // JSON snapshot (registry\NNNN_<key>.json), once per key and run. "engine backups restore" writes them back and
        // removes owned values the snapshot does not have. (reg.exe's export stops at value names of 255+ characters,
        // which Rewired uses, so a .reg file would not do.) Returns the snapshot's path, or an empty string.
        public static string ProtectRegistry(string game, string keyPath, string ownedPrefix, List<string> warnings)
        {
            if (disabled || string.IsNullOrWhiteSpace(keyPath))
                return string.Empty;
            try
            {
                OpenSet set = Open(string.IsNullOrWhiteSpace(game) ? "Other" : game);
                string marker = "registry:" + keyPath.Trim();
                string name = Sanitize(keyPath.Trim()) + ".json";
                string existing = set.Manifest.RegistryExports.FirstOrDefault(r => r.EndsWith("_" + name, StringComparison.OrdinalIgnoreCase));
                if (!set.Paths.Add(marker))
                    return existing == null ? string.Empty : Path.Combine(set.Folder, "registry", existing);
                name = (set.Manifest.RegistryExports.Count + 1).ToString("D4", CultureInfo.InvariantCulture) + "_" + name;
                string target = Path.Combine(set.Folder, "registry", name);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllText(target, EngineSerialization.ToJson(RegistrySnapshot.Read(keyPath.Trim(), ownedPrefix)), new UTF8Encoding(false));
                set.Manifest.RegistryExports.Add(name);
                File.WriteAllText(Path.Combine(set.Folder, ManifestName), EngineSerialization.ToJson(set.Manifest), new UTF8Encoding(false));
                return target;
            }
            catch (Exception ex)
            {
                warnings?.Add("Could not back up the registry key '" + keyPath + "' before changing it: " + ex.Message);
                return string.Empty;
            }
        }

        // The backup sets of this run (for the command's result).
        public static List<string> OpenedSets()
        {
            return OpenSets.Values.Select(s => s.Manifest.Id).ToList();
        }

        public static List<GameBackupManifest> List(string game)
        {
            List<GameBackupManifest> sets = new List<GameBackupManifest>();
            string root = Root();
            if (!Directory.Exists(root))
                return sets;
            foreach (string folder in Directory.GetDirectories(root))
            {
                GameBackupManifest manifest = Load(folder);
                if (manifest != null && (string.IsNullOrWhiteSpace(game) || string.Equals(manifest.Game, game, StringComparison.OrdinalIgnoreCase)))
                    sets.Add(manifest);
            }
            return sets.OrderByDescending(s => s.Created, StringComparer.Ordinal).ThenByDescending(s => s.Id, StringComparer.Ordinal).ToList();
        }

        // "latest" (optionally of one game) or a set id.
        public static GameBackupManifest Find(string id, string game)
        {
            if (string.Equals(id, "latest", StringComparison.OrdinalIgnoreCase))
                return List(game).FirstOrDefault(s => !s.Command.StartsWith("restore ", StringComparison.Ordinal)) ?? List(game).FirstOrDefault();
            string folder = Path.Combine(Root(), id ?? string.Empty);
            return Directory.Exists(folder) ? Load(folder) : null;
        }

        public static GameBackupRestoreResult Restore(GameBackupManifest set, bool dryRun, IEnumerable<string> processNames)
        {
            GameBackupRestoreResult result = new GameBackupRestoreResult { Id = set.Id, Game = set.Game, DryRun = dryRun };
            if (Environment.GetEnvironmentVariable(Games.GameProcessGuard.IgnoreRunningGamesVariable) != "1")
            {
                foreach (string name in processNames ?? Enumerable.Empty<string>())
                {
                    Process[] running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
                    if (running.Length > 0)
                        result.Errors.Add(set.Game + " is running (" + name + "): close it before restoring, it would overwrite the files again.");
                    foreach (Process process in running)
                        process.Dispose();
                }
            }
            string folder = Path.Combine(Root(), set.Id);
            List<RegistrySnapshot> snapshots = new List<RegistrySnapshot>();
            foreach (string export in set.RegistryExports ?? new List<string>())
            {
                try
                {
                    snapshots.Add(EngineSerialization.FromJson<RegistrySnapshot>(File.ReadAllText(Path.Combine(folder, "registry", export), Encoding.UTF8)));
                }
                catch (Exception ex)
                {
                    result.Errors.Add("The registry backup '" + export + "' is missing or unreadable: " + ex.Message);
                }
            }
            foreach (GameBackupFile file in set.Files.Where(f => f.Existed && !File.Exists(Path.Combine(folder, "files", f.Stored))))
                result.Errors.Add("The backup copy of '" + file.Original + "' is missing.");
            if (result.Errors.Count > 0)
                return result;

            if (!dryRun)
            {
                // The current state goes into a set of its own first: a restore can be undone like an export.
                Begin(set.Game, "restore " + set.Id, false);
                foreach (GameBackupFile file in set.Files)
                    Protect(set.Game, file.Original, result.Warnings);
                foreach (RegistrySnapshot snapshot in snapshots)
                    ProtectRegistry(set.Game, snapshot.Key, snapshot.OwnedPrefix, result.Warnings);
                result.UndoSet = OpenedSets().FirstOrDefault() ?? string.Empty;
            }
            foreach (RegistrySnapshot snapshot in snapshots)
            {
                try
                {
                    int changed = dryRun ? snapshot.Values.Count : snapshot.Apply();
                    result.Restored.Add(snapshot.Key + " (" + changed.ToString(CultureInfo.InvariantCulture) + " registry value(s))");
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Could not restore the registry key '" + snapshot.Key + "': " + ex.Message);
                }
            }
            foreach (GameBackupFile file in set.Files)
            {
                try
                {
                    if (file.Existed)
                    {
                        if (!dryRun)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(file.Original));
                            int? before = WarThunderCloudVersion.Read(file.Original);
                            SafeFileIo.CopyFile(Path.Combine(folder, "files", file.Stored), file.Original, true, ownTarget: false);
                            // War Thunder keeps the higher of the file's and the server's version: an older copy
                            // needs a version above the one it replaces, or the server's copy comes back at login.
                            if (WarThunderCloudVersion.RaiseAbove(file.Original, before))
                                result.Restored.Add(file.Original + " (War Thunder version raised so the game uploads it)");
                        }
                        result.Restored.Add(file.Original);
                    }
                    else if (File.Exists(file.Original))
                    {
                        if (!dryRun)
                            File.Delete(file.Original);
                        result.Removed.Add(file.Original);
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Could not restore '" + file.Original + "': " + ex.Message);
                }
            }
            JoyPro.Logging.JoyProLog.Info("Backups", "Restore of set " + set.Id + (dryRun ? " (dry run)" : string.Empty) + ": " +
                                                     result.Restored.Count.ToString(CultureInfo.InvariantCulture) + " restored, " +
                                                     result.Removed.Count.ToString(CultureInfo.InvariantCulture) + " removed, " +
                                                     result.Errors.Count.ToString(CultureInfo.InvariantCulture) + " error(s)" +
                                                     (result.Errors.Count > 0 ? ": " + string.Join(" | ", result.Errors) : string.Empty) +
                                                     (string.IsNullOrEmpty(result.UndoSet) ? string.Empty : "; undo set " + result.UndoSet));
            return result;
        }

        private static OpenSet Open(string game)
        {
            OpenSet set;
            if (OpenSets.TryGetValue(game, out set))
                return set;
            DateTime now = DateTime.Now;
            string id = now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "_" + Sanitize(game);
            set = new OpenSet { Folder = Path.Combine(Root(), id) };
            set.Manifest.Id = id;
            set.Manifest.Game = game;
            set.Manifest.Command = currentCommand;
            set.Manifest.Created = now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            Directory.CreateDirectory(set.Folder);
            OpenSets[game] = set;
            JoyPro.Logging.JoyProLog.Debug("Backups", "New backup set " + id + " in " + set.Folder);
            Prune(game, id);
            return set;
        }

        private static void Prune(string game, string keepId)
        {
            foreach (GameBackupManifest old in List(game).Where(s => s.Id != keepId).Skip(KeepPerGame - 1))
            {
                try
                {
                    Directory.Delete(Path.Combine(Root(), old.Id), true);
                }
                catch (Exception)
                {
                }
            }
        }

        private static GameBackupManifest Load(string folder)
        {
            string path = Path.Combine(folder, ManifestName);
            if (!File.Exists(path))
                return null;
            try
            {
                GameBackupManifest manifest = EngineSerialization.FromJson<GameBackupManifest>(File.ReadAllText(path, Encoding.UTF8));
                if (manifest != null && string.IsNullOrWhiteSpace(manifest.Id))
                    manifest.Id = Path.GetFileName(folder);
                return manifest;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Sanitize(string value)
        {
            return new string((value ?? string.Empty).Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        }

        public static string ToText(List<GameBackupManifest> sets)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Backups: " + sets.Count.ToString(CultureInfo.InvariantCulture) + " (" + Root() + ")");
            foreach (GameBackupManifest set in sets)
                sb.AppendLine("  - " + set.Id + " | " + set.Game + " | " + set.Created.Replace('T', ' ') + " | " + set.Files.Count.ToString(CultureInfo.InvariantCulture) + " files | " + set.Command);
            return sb.ToString();
        }

        public static string ToText(GameBackupManifest set)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Backup: " + set.Id);
            sb.AppendLine("Game: " + set.Game);
            sb.AppendLine("Created: " + set.Created.Replace('T', ' '));
            sb.AppendLine("Command: " + set.Command);
            sb.AppendLine("Files:");
            foreach (GameBackupFile file in set.Files)
                sb.AppendLine("  - " + file.Original + (file.Existed ? " (" + file.Size.ToString(CultureInfo.InvariantCulture) + " bytes)" : " (did not exist; a restore removes it)"));
            if (set.RegistryExports != null && set.RegistryExports.Count > 0)
            {
                sb.AppendLine("Registry snapshots:");
                foreach (string export in set.RegistryExports)
                    sb.AppendLine("  - " + Path.Combine(Root(), set.Id, "registry", export));
            }
            return sb.ToString();
        }

        public static string ToText(GameBackupRestoreResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Restore Backup: " + result.Id);
            sb.AppendLine("Game: " + result.Game);
            sb.AppendLine("Dry Run: " + result.DryRun);
            sb.AppendLine("Files Restored: " + result.Restored.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string path in result.Restored)
                sb.AppendLine("  - " + path);
            sb.AppendLine("Files Removed: " + result.Removed.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string path in result.Removed)
                sb.AppendLine("  - " + path);
            if (result.UndoSet.Length > 0)
                sb.AppendLine("Undo: engine backups restore " + result.UndoSet);
            foreach (string warning in result.Warnings)
                sb.AppendLine("Warning: " + warning);
            foreach (string error in result.Errors)
                sb.AppendLine("Error: " + error);
            return sb.ToString();
        }
    }

    public sealed class GameBackupManifest
    {
        public string Id { get; set; }
        public string Game { get; set; }
        public string Command { get; set; }
        public string Created { get; set; }
        public List<GameBackupFile> Files { get; set; }
        // Snapshots of registry keys the run changed (RegistrySnapshot JSON in the set's "registry" folder).
        public List<string> RegistryExports { get; set; }

        public GameBackupManifest()
        {
            Id = Game = Command = Created = string.Empty;
            Files = new List<GameBackupFile>();
            RegistryExports = new List<string>();
        }
    }

    // The values of an HKCU registry key JoyPro may change (names starting with OwnedPrefix), REG_BINARY / REG_DWORD data
    // as "Binary:<base64>" / "DWord:<base64>". Apply writes them back and deletes owned values the snapshot does not have.
    public sealed class RegistrySnapshot
    {
        public string Key { get; set; }
        public string OwnedPrefix { get; set; }
        public Dictionary<string, string> Values { get; set; }

        public RegistrySnapshot()
        {
            Key = OwnedPrefix = string.Empty;
            Values = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        internal static string SubKeyOf(string key)
        {
            string value = (key ?? string.Empty).Trim();
            foreach (string root in new[] { @"HKCU\", @"HKEY_CURRENT_USER\" })
            {
                if (value.StartsWith(root, StringComparison.OrdinalIgnoreCase) && value.Length > root.Length)
                    return value.Substring(root.Length);
            }
            throw new ArgumentException("Only HKEY_CURRENT_USER keys can be backed up: " + key);
        }

        public static RegistrySnapshot Read(string key, string ownedPrefix)
        {
            RegistrySnapshot snapshot = new RegistrySnapshot { Key = key, OwnedPrefix = ownedPrefix ?? string.Empty };
            using (Microsoft.Win32.RegistryKey registry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SubKeyOf(key), false))
            {
                if (registry == null)
                    return snapshot;
                foreach (string name in registry.GetValueNames().Where(n => n.StartsWith(snapshot.OwnedPrefix, StringComparison.Ordinal)))
                {
                    object value = registry.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value is byte[])
                        snapshot.Values[name] = "Binary:" + Convert.ToBase64String((byte[])value);
                    else if (value is int)
                        snapshot.Values[name] = "DWord:" + Convert.ToBase64String(BitConverter.GetBytes((int)value));
                }
            }
            return snapshot;
        }

        // Returns the number of values written or deleted.
        public int Apply()
        {
            int changed = 0;
            using (Microsoft.Win32.RegistryKey registry = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SubKeyOf(Key)))
            {
                foreach (string name in registry.GetValueNames().Where(n => n.StartsWith(OwnedPrefix ?? string.Empty, StringComparison.Ordinal) && !Values.ContainsKey(n)).ToList())
                {
                    registry.DeleteValue(name, false);
                    changed++;
                }
                foreach (KeyValuePair<string, string> value in Values)
                {
                    int colon = value.Value.IndexOf(':');
                    byte[] data = Convert.FromBase64String(value.Value.Substring(colon + 1));
                    if (value.Value.StartsWith("DWord:", StringComparison.Ordinal))
                        registry.SetValue(value.Key, BitConverter.ToInt32(data, 0), Microsoft.Win32.RegistryValueKind.DWord);
                    else
                        registry.SetValue(value.Key, data, Microsoft.Win32.RegistryValueKind.Binary);
                    changed++;
                }
            }
            return changed;
        }
    }

    public sealed class GameBackupFile
    {
        public string Original { get; set; }
        public bool Existed { get; set; }
        public string Stored { get; set; }
        public long Size { get; set; }

        public GameBackupFile()
        {
            Original = Stored = string.Empty;
        }
    }

    public sealed class GameBackupRestoreResult
    {
        public string Id { get; set; }
        public string Game { get; set; }
        public bool DryRun { get; set; }
        public string UndoSet { get; set; }
        public List<string> Restored { get; set; }
        public List<string> Removed { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public GameBackupRestoreResult()
        {
            Id = Game = UndoSet = string.Empty;
            Restored = new List<string>();
            Removed = new List<string>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }
}
