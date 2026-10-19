using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using JoyPro.Engine.Games;
using JoyPro.Logging;

namespace JoyPro.Engine
{
    // What a game run worked on (docs/logging.md "Game runs"): once per run and game an environment snapshot (install kind
    // and folder, the config folders with their drive, file system, free space and attributes such as EFS encryption and
    // read only, the connected devices), and per file JoyPro changes a stamp before and after (size, write time,
    // attributes, a short content hash). Everything goes to the background log at debug level; a failed run prints the
    // snapshot and the files to stderr (the GUI's output panel and log), and an engine crash report carries it. Fed by
    // GameBackups (Begin per run, Protect before every write) and the game integrations (LogEnvironment with their
    // config paths, NoteRead for files they read). One run per process, so the state is static.
    internal static class EngineDiagnostics
    {
        private static readonly object Gate = new object();
        private static string runGame = string.Empty;
        private static string runCommand = string.Empty;
        private static readonly HashSet<string> LoggedEnvironments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> EnvironmentTexts = new List<string>();
        // Path -> stamp before JoyPro wrote it (written files) or when it was read (read files).
        private static readonly Dictionary<string, string> Written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> Read = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private const int MaxNotedFiles = 400;

        public static void BeginRun(string game, string command)
        {
            lock (Gate)
            {
                runGame = game ?? string.Empty;
                runCommand = command ?? string.Empty;
                LoggedEnvironments.Clear();
                EnvironmentTexts.Clear();
                devicesText = null;
                Written.Clear();
                Read.Clear();
            }
        }

        public static string RunGame
        {
            get { lock (Gate) return runGame; }
        }

        // Once per run and game: the environment of the given config paths (files or folders).
        public static void LogEnvironment(string game, IEnumerable<string> paths)
        {
            string id = string.IsNullOrWhiteSpace(game) ? RunGame : game;
            if (string.IsNullOrWhiteSpace(id))
                return;
            lock (Gate)
            {
                if (!LoggedEnvironments.Add(id))
                    return;
            }
            string text;
            try
            {
                text = EnvironmentText(id, paths, true);
            }
            catch (Exception ex)
            {
                text = "Environment of " + id + ": could not be read (" + ex.Message + ")";
            }
            lock (Gate)
                EnvironmentTexts.Add(text);
            JoyProLog.Debug("Diagnostics", text);
        }

        // The install (kind, folder), the paths with their drive and folder attributes, and (withDevices) the connected
        // game devices.
        public static string EnvironmentText(string game, IEnumerable<string> paths, bool withDevices)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Environment of ").Append(game).Append(runCommand.Length > 0 ? " (" + runCommand + ")" : string.Empty).AppendLine(":");
            GameInstall install = null;
            try
            {
                install = GameInstalls.Find(game, new List<string>());
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                sb.AppendLine("  install: unknown (" + ex.Message + ")");
            }
            if (install != null)
                sb.AppendLine("  install: " + (install.Installed ? InstallKind(install) + ", " + install.InstallPath : "not found") + " (source " + (install.Source ?? "-") + ")" +
                              (install.Disabled ? ", marked as not owned" : string.Empty) + (string.IsNullOrEmpty(install.ConfigOverride) ? string.Empty : ", config set to " + install.ConfigOverride));
            foreach (string path in (paths ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
                sb.AppendLine("  path: " + PathInfo(path));
            if (withDevices)
                sb.Append(DevicesText());
            return sb.ToString().TrimEnd();
        }

        private static string devicesText;

        // The connected game devices (read once per run).
        internal static string DevicesText()
        {
            lock (Gate)
            {
                if (devicesText != null)
                    return devicesText;
            }
            StringBuilder sb = new StringBuilder();
            {
                try
                {
                    GameDeviceDirectory devices = GameDeviceDirectory.Discover(new List<string>());
                    List<GameDevice> connected = devices.Devices.Where(d => d.Connected).ToList();
                    sb.AppendLine("  connected devices: " + connected.Count.ToString(CultureInfo.InvariantCulture));
                    foreach (GameDevice device in connected)
                        sb.AppendLine("    " + (string.IsNullOrWhiteSpace(device.InstanceName) ? device.JoyProName : device.InstanceName.Trim()) +
                                      (device.HasVidPid ? " " + device.VendorId.ToString("X4", CultureInfo.InvariantCulture) + ":" + device.ProductId.ToString("X4", CultureInfo.InvariantCulture) : string.Empty) +
                                      " {" + device.InstanceGuid.ToString().ToUpperInvariant() + "}");
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    sb.AppendLine("  connected devices: unknown (" + ex.Message + ")");
                }
            }
            lock (Gate)
                devicesText = sb.ToString();
            return sb.ToString();
        }

        // Steam, Microsoft Store, or a standalone install (by the detection source and the folder).
        public static string InstallKind(GameInstall install)
        {
            string path = (install.InstallPath ?? string.Empty).ToLowerInvariant();
            string source = (install.Source ?? string.Empty).ToLowerInvariant();
            if (source.Contains("steam") || path.Contains("\\steamapps\\"))
                return "Steam";
            if (source.Contains("store") || source.Contains("appx") || path.Contains("\\xboxgames\\") || path.Contains("\\windowsapps\\") || path.Contains("\\packages\\microsoft."))
                return "Microsoft Store";
            return "standalone";
        }

        // "<path> [folder|file|missing], drive C:\ NTFS fixed, 12.3 GB free, attributes Encrypted|ReadOnly".
        public static string PathInfo(string path)
        {
            StringBuilder sb = new StringBuilder(path);
            try
            {
                bool isFolder = Directory.Exists(path);
                bool isFile = !isFolder && File.Exists(path);
                sb.Append(isFolder ? " [folder]" : isFile ? " [file]" : " [missing]");
                string existing = path;
                while (!string.IsNullOrEmpty(existing) && !Directory.Exists(existing) && !File.Exists(existing))
                    existing = Path.GetDirectoryName(existing);
                if (!string.IsNullOrEmpty(existing))
                {
                    sb.Append(", attributes ").Append(Attributes(File.GetAttributes(existing)));
                    if (!string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                        sb.Append(" (of ").Append(existing).Append(')');
                }
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(root))
                {
                    if (root.StartsWith("\\\\", StringComparison.Ordinal))
                        sb.Append(", network share ").Append(root);
                    else
                    {
                        DriveInfo drive = new DriveInfo(root);
                        sb.Append(", drive ").Append(root).Append(' ').Append(drive.IsReady ? drive.DriveFormat : "not ready").Append(' ').Append(drive.DriveType.ToString().ToLowerInvariant());
                        if (drive.IsReady)
                            sb.Append(", ").Append((drive.AvailableFreeSpace / 1024.0 / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture)).Append(" GB free");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                sb.Append(" (" + ex.Message + ")");
            }
            return sb.ToString();
        }

        public static string Attributes(FileAttributes attributes)
        {
            List<string> names = new List<string>();
            if ((attributes & FileAttributes.Encrypted) != 0) names.Add("Encrypted (EFS)");
            if ((attributes & FileAttributes.ReadOnly) != 0) names.Add("ReadOnly");
            if ((attributes & FileAttributes.Hidden) != 0) names.Add("Hidden");
            if ((attributes & FileAttributes.System) != 0) names.Add("System");
            if ((attributes & FileAttributes.Compressed) != 0) names.Add("Compressed");
            if ((attributes & FileAttributes.ReparsePoint) != 0) names.Add("ReparsePoint");
            if ((attributes & FileAttributes.Offline) != 0 || ((int)attributes & 0x400000) != 0) names.Add("CloudPlaceholder");
            return names.Count == 0 ? "normal" : string.Join("|", names);
        }

        // "size 1234, written 2026-10-19 08:51:08Z, attributes normal, sha 1a2b3c4d5e6f" (or "missing" / why not readable).
        public static string FileStamp(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists)
                    return "missing";
                string hash;
                try
                {
                    using (SHA256 sha = SHA256.Create())
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        hash = BitConverter.ToString(sha.ComputeHash(stream), 0, 6).Replace("-", string.Empty).ToLowerInvariant();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    hash = "unreadable (" + ex.Message.Trim() + ")";
                }
                return "size " + info.Length.ToString(CultureInfo.InvariantCulture) + ", written " + info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z, attributes " +
                       Attributes(info.Attributes) + ", sha " + hash;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return "not readable (" + ex.Message + ")";
            }
        }

        // Before JoyPro changes a game file (GameBackups.Protect): its stamp, and the environment of its folder once.
        public static void BeforeWrite(string game, string path)
        {
            string stamp = FileStamp(path);
            lock (Gate)
            {
                if (Written.ContainsKey(path) || Written.Count >= MaxNotedFiles)
                    return;
                Written[path] = stamp;
            }
            LogEnvironment(game, new[] { Path.GetDirectoryName(path) });
            JoyProLog.Debug("Diagnostics", "Before write: " + path + " | " + stamp);
        }

        // A game file a run read (once per file and run).
        public static void NoteRead(string path, string what = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            string stamp = FileStamp(path);
            lock (Gate)
            {
                if (Read.ContainsKey(path) || Read.Count >= MaxNotedFiles)
                    return;
                Read[path] = stamp;
            }
            JoyProLog.Debug("Diagnostics", "Read: " + path + (string.IsNullOrEmpty(what) ? string.Empty : " (" + what + ")") + " | " + stamp);
        }

        // After the command: the stamps of the changed files again; a failed run prints the snapshot to stderr.
        public static void EndRun(int exitCode)
        {
            List<KeyValuePair<string, string>> written;
            lock (Gate)
                written = Written.ToList();
            foreach (KeyValuePair<string, string> file in written)
            {
                string after = FileStamp(file.Key);
                JoyProLog.Debug("Diagnostics", "After write: " + file.Key + " | " + after + (after == file.Value ? " (unchanged)" : string.Empty));
            }
            if (exitCode == 0 || exitCode == EngineProgress.CancelledExitCode)
                return;
            string snapshot = Snapshot();
            if (snapshot.Length == 0)
                return;
            JoyProLog.Warn("Diagnostics", "The command failed (exit code " + exitCode.ToString(CultureInfo.InvariantCulture) + "):" + Environment.NewLine + snapshot);
            try
            {
                Console.Error.WriteLine("Diagnostics (environment and files of this run):");
                Console.Error.WriteLine(LogScrubber.Scrub(snapshot));
            }
            catch (IOException)
            {
            }
        }

        // The run's environment and files (empty when the run touched no game).
        public static string Snapshot()
        {
            lock (Gate)
            {
                if (EnvironmentTexts.Count == 0 && Written.Count == 0 && Read.Count == 0)
                    return string.Empty;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Run: " + (runGame.Length == 0 ? "-" : runGame) + " | " + runCommand);
                foreach (string text in EnvironmentTexts)
                    sb.AppendLine(text);
                foreach (KeyValuePair<string, string> file in Read.Take(60))
                    sb.AppendLine("  read: " + file.Key + " | " + file.Value);
                if (Read.Count > 60)
                    sb.AppendLine("  read: ... " + (Read.Count - 60).ToString(CultureInfo.InvariantCulture) + " more");
                foreach (KeyValuePair<string, string> file in Written)
                    sb.AppendLine("  written: " + file.Key + " | before: " + file.Value + " | now: " + FileStamp(file.Key));
                return sb.ToString().TrimEnd();
            }
        }
    }
}
