#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.TelemetrySessions
{
    // The folder of recorded sessions: list, delete, finish sessions cut off by a crash, and the retention rules.
    public static class TelemetrySessionStore
    {
        public const string DataFolderVariable = "JOYPRO_DATA_DIR";

        // <data>\Telemetry (data = JOYPRO_DATA_DIR, else %LOCALAPPDATA%\JoyPro, as the engine's other data).
        public static string DataFolder()
        {
            string root = Environment.GetEnvironmentVariable(DataFolderVariable);
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro");
            return Path.Combine(root, "Telemetry");
        }

        public static string ConfigPath()
        {
            return Path.Combine(DataFolder(), "telemetry-recording.json");
        }

        public static string DefaultRecordingsFolder()
        {
            return Path.Combine(DataFolder(), "Recordings");
        }

        // The folder of the settings ("" = the default).
        public static string ResolveFolder(string configured)
        {
            return string.IsNullOrWhiteSpace(configured) ? DefaultRecordingsFolder() : Environment.ExpandEnvironmentVariables(configured.Trim());
        }

        // A new session's file name: 2026-10-18_14-03-22_DCS.jptel (local time; a number added if it exists).
        public static string NewSessionPath(string folder, string gameId, DateTime startLocal)
        {
            string stem = startLocal.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + "_" + SafeName(gameId);
            string path = Path.Combine(folder, stem + TelemetrySessionFormat.Extension);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(folder, stem + "_" + i.ToString(CultureInfo.InvariantCulture) + TelemetrySessionFormat.Extension);
            return path;
        }

        public static string SafeName(string text)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in text ?? string.Empty)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            string result = sb.ToString().Trim('.', '_');
            return result.Length == 0 ? "session" : result.Length > 60 ? result.Substring(0, 60) : result;
        }

        // Header and summary of one file (fast: the frames are skipped).
        public static TelemetrySessionInfo ReadInfo(string path)
        {
            FileInfo file = new FileInfo(path);
            TelemetrySessionInfo info = new TelemetrySessionInfo { Path = file.FullName, FileName = file.Name, SizeBytes = file.Exists ? file.Length : 0 };
            info.InUse = IsInUse(path);
            double lastTime;
            using (TelemetrySessionReader reader = new TelemetrySessionReader(path))
            {
                reader.SkipToSummary(out lastTime);
                info.Header = reader.Header ?? new Dictionary<string, string>(StringComparer.Ordinal);
                info.Summary = reader.Summary ?? new Dictionary<string, string>(StringComparer.Ordinal);
                info.Complete = reader.Summary != null;
            }
            info.Game = Get(info.Header, "game");
            info.GameTitle = Get(info.Header, "gameTitle");
            if (info.GameTitle.Length == 0)
            {
                TelemetryRecordingGame game = TelemetryRecordingGames.Find(info.Game);
                info.GameTitle = game != null ? game.Title : info.Game;
            }
            info.Source = Get(info.Header, "source");
            info.CreatedUtc = TelemetrySessionFormat.ParseTime(Get(info.Header, "created"));
            foreach (KeyValuePair<string, string> pair in info.Header)
            {
                if (pair.Key.StartsWith("nav.", StringComparison.Ordinal))
                {
                    TelemetrySessionNav nav = TelemetrySessionNav.Parse(pair.Key.Substring(4), pair.Value);
                    if (nav != null)
                        info.Nav.Add(nav);
                }
            }
            if (info.Complete)
            {
                double duration;
                long count;
                int channels;
                info.EndedUtc = TelemetrySessionFormat.ParseTime(Get(info.Summary, "ended"));
                info.Duration = TelemetrySessionFormat.TryParseNumber(Get(info.Summary, "duration"), out duration) ? duration : 0;
                info.Frames = long.TryParse(Get(info.Summary, "frames"), NumberStyles.Integer, CultureInfo.InvariantCulture, out count) ? count : 0;
                info.Events = long.TryParse(Get(info.Summary, "events"), NumberStyles.Integer, CultureInfo.InvariantCulture, out count) ? count : 0;
                info.RawPackets = long.TryParse(Get(info.Summary, "raw"), NumberStyles.Integer, CultureInfo.InvariantCulture, out count) ? count : 0;
                info.Channels = int.TryParse(Get(info.Summary, "channels"), NumberStyles.Integer, CultureInfo.InvariantCulture, out channels) ? channels : 0;
                info.EndReason = Get(info.Summary, "endReason");
                info.Aircraft = Lines(Get(info.Summary, "aircraft"));
                info.Maps = Lines(Get(info.Summary, "maps"));
            }
            else
            {
                info.Duration = lastTime;
                info.EndedUtc = info.CreatedUtc == DateTime.MinValue ? DateTime.MinValue : info.CreatedUtc.AddSeconds(lastTime);
                info.EndReason = info.InUse ? "recording" : "unfinished";
            }
            if (info.Aircraft.Count == 0 && info.Info("aircraft").Length > 0)
                info.Aircraft.Add(info.Info("aircraft"));
            if (info.Maps.Count == 0 && info.Info("map").Length > 0)
                info.Maps.Add(info.Info("map"));
            return info;
        }

        // Every session in the folder, newest first. Files that cannot be read are reported in problems.
        public static List<TelemetrySessionInfo> List(string folder, List<string> problems = null)
        {
            List<TelemetrySessionInfo> sessions = new List<TelemetrySessionInfo>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return sessions;
            foreach (string path in Directory.GetFiles(folder, "*" + TelemetrySessionFormat.Extension))
            {
                try
                {
                    sessions.Add(ReadInfo(path));
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                {
                    if (problems != null)
                        problems.Add(Path.GetFileName(path) + ": " + ex.Message);
                }
            }
            return sessions.OrderByDescending(s => s.CreatedUtc).ThenByDescending(s => s.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // A session given as a path, a file name in the folder, "latest", or a number of the list (1 = newest).
        public static string Resolve(string folder, string reference)
        {
            string text = (reference ?? string.Empty).Trim().Trim('"');
            if (text.Length == 0)
                return null;
            if (File.Exists(text))
                return Path.GetFullPath(text);
            if (!string.IsNullOrEmpty(folder))
            {
                string inFolder = Path.Combine(folder, text);
                if (File.Exists(inFolder))
                    return inFolder;
                if (File.Exists(inFolder + TelemetrySessionFormat.Extension))
                    return inFolder + TelemetrySessionFormat.Extension;
            }
            int number;
            if (string.Equals(text, "latest", StringComparison.OrdinalIgnoreCase))
                number = 1;
            else if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 1)
                return null;
            List<TelemetrySessionInfo> list = List(folder);
            return number <= list.Count ? list[number - 1].Path : null;
        }

        // True while the recorder still writes the file (it holds it open for writing).
        public static bool IsInUse(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    return false;
            }
            catch (IOException)
            {
                return File.Exists(path);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public static bool Delete(string path, List<string> problems = null)
        {
            try
            {
                if (IsInUse(path))
                {
                    if (problems != null)
                        problems.Add(Path.GetFileName(path) + " is being recorded right now.");
                    return false;
                }
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (problems != null)
                    problems.Add(Path.GetFileName(path) + ": " + ex.Message);
                return false;
            }
        }

        // Sessions cut off by a crash or a kill (no summary, not being written): the damaged tail is cut, the summary
        // written from the data. Returns the files finished.
        public static List<string> Recover(string folder, List<string> problems = null)
        {
            List<string> done = new List<string>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return done;
            foreach (string path in Directory.GetFiles(folder, "*" + TelemetrySessionFormat.Extension))
            {
                try
                {
                    if (RecoverFile(path))
                        done.Add(path);
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                {
                    if (problems != null)
                        problems.Add(Path.GetFileName(path) + ": " + ex.Message);
                }
            }
            return done;
        }

        public static bool RecoverFile(string path)
        {
            if (IsInUse(path))
                return false;
            double lastTime;
            using (TelemetrySessionReader probe = new TelemetrySessionReader(path))
            {
                if (probe.SkipToSummary(out lastTime))
                    return false;
            }
            long frames = 0, events = 0, raw = 0, validLength;
            double maxTime = 0;
            int channels;
            List<string> aircraft = new List<string>();
            List<string> maps = new List<string>();
            Dictionary<string, string> header;
            using (TelemetrySessionReader reader = new TelemetrySessionReader(path, includeRaw: true))
            {
                while (reader.Next())
                {
                    maxTime = Math.Max(maxTime, reader.Time);
                    switch (reader.Kind)
                    {
                        case TelemetrySessionReader.RecordKind.Frame: frames++; break;
                        case TelemetrySessionReader.RecordKind.Raw: raw++; break;
                        case TelemetrySessionReader.RecordKind.Event:
                            events++;
                            if (reader.EventKind == "aircraft" && reader.EventText.Length > 0 && !aircraft.Contains(reader.EventText))
                                aircraft.Add(reader.EventText);
                            if (reader.EventKind == "map" && reader.EventText.Length > 0 && !maps.Contains(reader.EventText))
                                maps.Add(reader.EventText);
                            break;
                    }
                }
                validLength = reader.ValidLength;
                channels = reader.Channels.Count(c => c != null);
                header = reader.Header;
            }
            string headAircraft;
            if (header != null && header.TryGetValue("info.aircraft", out headAircraft) && headAircraft.Length > 0 && !aircraft.Contains(headAircraft))
                aircraft.Insert(0, headAircraft);
            string headMap;
            if (header != null && header.TryGetValue("info.map", out headMap) && headMap.Length > 0 && !maps.Contains(headMap))
                maps.Insert(0, headMap);
            DateTime created = header != null ? TelemetrySessionFormat.ParseTime(Get(header, "created")) : DateTime.MinValue;
            DateTime ended = created == DateTime.MinValue ? File.GetLastWriteTimeUtc(path) : created.AddSeconds(maxTime);
            Dictionary<string, string> summary = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "ended", TelemetrySessionFormat.FormatTime(ended) },
                { "duration", TelemetrySessionFormat.FormatNumber(Math.Round(maxTime, 3)) },
                { "frames", frames.ToString(CultureInfo.InvariantCulture) },
                { "events", events.ToString(CultureInfo.InvariantCulture) },
                { "raw", raw.ToString(CultureInfo.InvariantCulture) },
                { "channels", channels.ToString(CultureInfo.InvariantCulture) },
                { "aircraft", string.Join("\n", aircraft) },
                { "maps", string.Join("\n", maps) },
                { "endReason", "recovered (the recording was cut off)" },
                { "recovered", TelemetrySessionFormat.FormatTime(DateTime.UtcNow) }
            };
            byte[] bytes = TelemetrySessionFormat.KeyValueBytes(summary);
            byte[] stored = TelemetrySessionFormat.Compress(bytes, bytes.Length);
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (validLength < TelemetrySessionFormat.FileMagic.Length)
                    validLength = file.Length;
                file.SetLength(validLength);
                file.Seek(validLength, SeekOrigin.Begin);
                byte[] head = new byte[TelemetrySessionFormat.BlockHeaderSize];
                Array.Copy(BitConverter.GetBytes(TelemetrySessionFormat.BlockMagic), 0, head, 0, 4);
                head[4] = TelemetrySessionFormat.KindSummary;
                Array.Copy(BitConverter.GetBytes((uint)bytes.Length), 0, head, 5, 4);
                Array.Copy(BitConverter.GetBytes((uint)stored.Length), 0, head, 9, 4);
                Array.Copy(BitConverter.GetBytes(TelemetrySessionFormat.Crc32(stored, 0, stored.Length)), 0, head, 13, 4);
                file.Write(head, 0, head.Length);
                file.Write(stored, 0, stored.Length);
                file.Flush(true);
            }
            return true;
        }

        // Retention: sessions older than keepDays (0 = no age limit) go, then the oldest until the folder is at most
        // maxTotalBytes (0 = no size limit). The session being written is never deleted. Returns the deleted files.
        public static List<string> Prune(string folder, long maxTotalBytes, int keepDays, DateTime nowUtc, List<string> problems = null)
        {
            List<string> deleted = new List<string>();
            List<TelemetrySessionInfo> sessions = List(folder, problems);
            List<TelemetrySessionInfo> oldestFirst = sessions.Where(s => !s.InUse).OrderBy(s => s.CreatedUtc).ToList();
            if (keepDays > 0)
            {
                DateTime limit = nowUtc.AddDays(-keepDays);
                foreach (TelemetrySessionInfo session in oldestFirst.Where(s => s.CreatedUtc != DateTime.MinValue && s.CreatedUtc < limit).ToList())
                {
                    if (Delete(session.Path, problems))
                    {
                        deleted.Add(session.Path);
                        oldestFirst.Remove(session);
                    }
                }
            }
            if (maxTotalBytes > 0)
            {
                long total = sessions.Where(s => !deleted.Contains(s.Path)).Sum(s => s.SizeBytes);
                foreach (TelemetrySessionInfo session in oldestFirst.ToList())
                {
                    if (total <= maxTotalBytes)
                        break;
                    if (Delete(session.Path, problems))
                    {
                        deleted.Add(session.Path);
                        total -= session.SizeBytes;
                    }
                }
            }
            return deleted;
        }

        // Where an export goes by default: <recordings>\Exports\<session name>.txt.acmi | .zip.acmi | -csv\.
        public static string ExportPath(string sessionPath, string format)
        {
            string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sessionPath)), "Exports");
            string stem = Path.GetFileNameWithoutExtension(sessionPath);
            switch (format)
            {
                case "acmi": return Path.Combine(folder, stem + ".txt.acmi");
                case "acmi-zip": return Path.Combine(folder, stem + ".zip.acmi");
                default: return Path.Combine(folder, stem + "-csv");
            }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024)
                return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024 * 1024)
                return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            if (bytes < 1024L * 1024 * 1024)
                return (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        }

        public static string FormatDuration(double seconds)
        {
            TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return span.TotalHours >= 1
                ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + span.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + span.Seconds.ToString("00", CultureInfo.InvariantCulture)
                : span.Minutes.ToString(CultureInfo.InvariantCulture) + ":" + span.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        private static string Get(Dictionary<string, string> map, string key)
        {
            string value;
            return map != null && map.TryGetValue(key, out value) ? value ?? string.Empty : string.Empty;
        }

        private static List<string> Lines(string text)
        {
            return (text ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }
    }
}
