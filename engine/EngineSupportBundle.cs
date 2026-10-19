using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using JoyPro.Engine.Games;
using JoyPro.Logging;

namespace JoyPro.Engine
{
    // engine support bundle [--game <id>] [--days 3] [--list] --out <zip> (docs/logging.md "Support file"): one zip a
    // tester sends when something does not work, made on the user's PC and never uploaded. It holds:
    //   - summary.txt: versions, system, JoyPro's folders, per game the environment (install kind and folder, config
    //     folders with drive, file system, free space and attributes such as EFS) and the connected devices;
    //   - logs/ (JoyPro's logs of the last days) and crash-reports/;
    //   - games/<id>/: the watcher's record of the game, the newest backup set JoyPro made before it wrote the game's
    //     files (its small text config files: JoyPro's own copies), and for MSFS the input profiles JoyPro reads (Steam
    //     remote folder, Microsoft Store save; text only), JoyPro's preset package, the import files and a summary of
    //     the sim's device records (not the save file itself).
    // Every text goes through LogScrubber (user folder -> %USERPROFILE%, user and PC name, e-mail, secrets, Steam ids);
    // binary files are left out. --list prints what would be included without writing anything.
    internal static class EngineSupportBundle
    {
        public const int DefaultDays = 3;
        private const long MaxTextFile = 512 * 1024;
        private const long MaxLogBytes = 60L * 1024 * 1024;
        private const int MaxProfilesPerGame = 80;

        internal sealed class Entry
        {
            public string Name;
            public string Source;
            public string GeneratedText;
            public string What;
            public long Size;
        }

        public static int Run(List<string> args, string zipPath, bool textOutput, out string payload)
        {
            payload = string.Empty;
            List<string> rest = (args ?? new List<string>()).ToList();
            string action = rest.Count == 0 ? string.Empty : rest[0].ToLowerInvariant();
            if (action != "bundle")
            {
                payload = "Unknown support action: " + action + ". Use: engine support bundle [--game <id>] [--days N] [--list] --out <zip>.";
                return 1;
            }
            rest.RemoveAt(0);
            bool listOnly = Pop(rest, "--list");
            string game = PopValue(rest, "--game");
            string daysText = PopValue(rest, "--days");
            int days;
            if (string.IsNullOrWhiteSpace(daysText) || !int.TryParse(daysText, NumberStyles.Integer, CultureInfo.InvariantCulture, out days) || days < 1)
                days = DefaultDays;
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
            List<Entry> entries = Collect(game, days, DateTime.Now);
            string written = null;
            if (!listOnly)
            {
                written = Path.GetFullPath(zipPath);
                Write(entries, written);
                JoyProLog.Info("Support", "Support file written: " + written + " (" + entries.Count.ToString(CultureInfo.InvariantCulture) + " entries)");
            }
            if (!textOutput)
            {
                payload = EngineSerialization.ToJson(new
                {
                    Zip = written ?? string.Empty,
                    ListOnly = listOnly,
                    Days = days,
                    Game = game ?? string.Empty,
                    Entries = entries.Select(e => new { e.Name, e.What, e.Size }).ToList()
                });
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(listOnly ? "Support file contents (not written)" : "Support file: " + written);
            sb.AppendLine("Entries: " + entries.Count.ToString(CultureInfo.InvariantCulture) + ", " + (entries.Sum(e => e.Size) / 1024).ToString(CultureInfo.InvariantCulture) + " KB before compression");
            foreach (Entry entry in entries)
                sb.AppendLine("  - " + entry.Name + " | " + entry.What + " | " + entry.Size.ToString(CultureInfo.InvariantCulture) + " bytes");
            payload = sb.ToString();
            return 0;
        }

        // What the bundle holds (sizes of the files before scrubbing).
        internal static List<Entry> Collect(string gameFilter, int days, DateTime now)
        {
            List<Entry> entries = new List<Entry>();
            List<string> games = SelectGames(gameFilter);
            entries.Add(new Entry { Name = "summary.txt", GeneratedText = Summary(games, days, now), What = "versions, system, the games' environment and the connected devices" });
            entries[0].Size = Encoding.UTF8.GetByteCount(entries[0].GeneratedText);

            // Logs of the last days, newest first, at most MaxLogBytes.
            string logFolder = JoyProLog.DefaultFolder();
            long logBytes = 0;
            foreach (FileInfo log in Files(logFolder, "*.log", SearchOption.TopDirectoryOnly).Where(f => f.LastWriteTime >= now.AddDays(-days)).OrderByDescending(f => f.LastWriteTime))
            {
                if (logBytes + log.Length > MaxLogBytes)
                    break;
                logBytes += log.Length;
                entries.Add(new Entry { Name = "logs/" + log.Name, Source = log.FullName, What = "JoyPro log", Size = log.Length });
            }
            string crashFolder = Path.Combine(Path.GetDirectoryName(logFolder.TrimEnd('\\')) ?? logFolder, "CrashReports");
            foreach (FileInfo crash in Files(crashFolder, "*.txt", SearchOption.AllDirectories).Where(f => f.LastWriteTime >= now.AddDays(-days) && f.Length <= MaxTextFile).OrderByDescending(f => f.LastWriteTime).Take(20))
                entries.Add(new Entry { Name = "crash-reports/" + crash.Name, Source = crash.FullName, What = "crash report", Size = crash.Length });

            foreach (string id in games)
                AddGame(entries, id);
            return entries;
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

        private static string Summary(List<string> games, int days, DateTime now)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("JoyPro2 support file (made on this PC; nothing was uploaded)");
            sb.AppendLine("Time: " + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " (local), UTC offset " + TimeZoneInfo.Local.GetUtcOffset(now).ToString(@"hh\:mm", CultureInfo.InvariantCulture));
            object[] attributes = typeof(EngineSupportBundle).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            sb.AppendLine("Engine: " + (attributes.Length > 0 ? ((System.Reflection.AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion : typeof(EngineSupportBundle).Assembly.GetName().Version.ToString()));
            sb.AppendLine("System: " + JoyProLog.SystemDescription());
            sb.AppendLine("JoyPro data: " + GameInstalls.DataRoot());
            sb.AppendLine("Logs: " + JoyProLog.DefaultFolder() + " (the last " + days.ToString(CultureInfo.InvariantCulture) + " day(s))");
            sb.AppendLine("Games: " + (games.Count == 0 ? "none installed" : string.Join(", ", games)));
            sb.AppendLine();
            foreach (string id in games)
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

        private static void AddGame(List<Entry> entries, string id)
        {
            string prefix = "games/" + id + "/";
            string record = Path.Combine(GameInstalls.DataRoot(), "Sync", id + ".json");
            if (File.Exists(record))
                entries.Add(new Entry { Name = prefix + "sync-record.json", Source = record, What = "the watcher's record of the game's state", Size = new FileInfo(record).Length });
            GameBackupManifest set = GameBackups.List(id).FirstOrDefault();
            if (set != null)
            {
                string folder = Path.Combine(GameBackups.Root(), set.Id);
                string manifest = Path.Combine(folder, "manifest.json");
                if (File.Exists(manifest))
                    entries.Add(new Entry { Name = prefix + "backup-" + set.Id + "/manifest.json", Source = manifest, What = "JoyPro's newest backup set (" + set.Command + ")", Size = new FileInfo(manifest).Length });
                foreach (GameBackupFile file in set.Files.Where(f => f.Existed && !string.IsNullOrEmpty(f.Stored)))
                {
                    string stored = Path.Combine(folder, "files", file.Stored);
                    if (IsSmallText(stored))
                        entries.Add(new Entry { Name = prefix + "backup-" + set.Id + "/" + file.Stored, Source = stored, What = "JoyPro's copy of " + file.Original + " before its export", Size = new FileInfo(stored).Length });
                }
            }
            int sim = id == "MSFS2024" ? 2024 : id == "MSFS2020" ? 2020 : 0;
            if (sim > 0)
                AddMsfs(entries, prefix, sim);
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
                        Size = info.Length
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
                        entries.Add(new Entry { Name = prefix + "package/" + file.FullName.Substring(package.Length).TrimStart('\\').Replace('\\', '/'), Source = file.FullName, What = "JoyPro's preset package", Size = file.Length });
                }
                foreach (FileInfo file in Files(MsfsIntegration.ImportFilesFolder(), "*.xml", SearchOption.TopDirectoryOnly).Where(f => f.Length <= MaxTextFile))
                    entries.Add(new Entry { Name = prefix + "import-files/" + file.Name, Source = file.FullName, What = "an import file JoyPro wrote", Size = file.Length });
            }
            string records = SimRecordsText(sim);
            entries.Add(new Entry { Name = prefix + "sim-records.txt", GeneratedText = records, What = "the sim's device records (profile_00) as JoyPro reads them", Size = Encoding.UTF8.GetByteCount(records) });
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

        private static void Write(List<Entry> entries, string zipPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
            string temp = zipPath + ".tmp";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (Entry entry in entries)
                {
                    string text = entry.GeneratedText;
                    if (text == null)
                    {
                        try
                        {
                            text = ReadText(entry.Source);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            text = "(not readable: " + ex.Message + ")";
                        }
                    }
                    ZipArchiveEntry zipEntry = zip.CreateEntry(entry.Name, CompressionLevel.Optimal);
                    using (Stream output = zipEntry.Open())
                    {
                        byte[] bytes = new UTF8Encoding(false).GetBytes(LogScrubber.Scrub(text));
                        output.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            File.Move(temp, zipPath);
        }

        // Text of a file (UTF-8, or UTF-16 with its byte order mark).
        private static string ReadText(string path)
        {
            byte[] bytes = SafeFileIo.ReadAllBytes(path);
            return MsfsUserProfiles.DecodeText(bytes);
        }

        // A file of at most 512 KB without NUL bytes (UTF-16 with its byte order mark counts as text).
        internal static bool IsSmallText(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxTextFile)
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
