#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// The background log of every JoyPro process (docs/logging.md). One file per component and day in <data>\Logs
// (data = JOYPRO_DATA_DIR, else %LOCALAPPDATA%\JoyPro2; JOYPRO_LOG_DIR replaces the whole folder), shared by the GUI,
// the engine and JoyPro.Ffb. Plain C# 7.3: the engine (.NET Framework 4.8) compiles this file, JoyPro.Ffb.Core
// (net9.0) links it, and the GUI uses it through JoyPro.Ffb.Core.
//
// Lines: "2026-10-11 10:44:12.345 INFO  engine#4711 s:3f9a2c1d c:gui-17 t:1 [Backups] message". s = the session (the
// GUI's, inherited by the engine and JoyPro.Ffb it starts through JOYPRO_LOG_SESSION), c = the call that started the
// process (JOYPRO_LOG_PARENT, the GUI logs the same id when it starts the call). Continuation lines (stack traces) are
// indented. Every text goes through LogScrubber first (user folder, user name, e-mail, passwords/tokens, Steam ids).
//
// Writing never blocks the caller on disk: lines are queued and written at most twice a second (warnings and errors
// at once), files are opened per write under a named mutex (several engine processes can append to one file),
// rolled at MaxFileBytes and capped at MaxRollsPerDay files per component and day. Files older than the retention
// (default 7 days, <Logs>\log-settings.txt, set in the GUI) are deleted at most once an hour, at a process start.
// Nothing is written before Start, so library code (and tests that never call Start) logs nothing.
namespace JoyPro.Logging
{
    public enum JoyProLogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
        Off = 4
    }

    public static class JoyProLog
    {
        public const string DataFolderVariable = "JOYPRO_DATA_DIR";
        public const string FolderVariable = "JOYPRO_LOG_DIR";
        public const string SessionVariable = "JOYPRO_LOG_SESSION";
        public const string ParentVariable = "JOYPRO_LOG_PARENT";
        public const string RetentionVariable = "JOYPRO_LOG_RETENTION_DAYS";
        public const string LevelVariable = "JOYPRO_LOG_LEVEL";
        public const string FolderName = "Logs";
        public const string SettingsFileName = "log-settings.txt";
        public const string CleanupMarkerName = ".last-cleanup";
        public const int DefaultRetentionDays = 7;
        public const int MinRetentionDays = 1;
        public const int MaxRetentionDays = 365;
        public const int CrashReportMinRetentionDays = 30;
        public const long DefaultMaxFileBytes = 10L * 1024 * 1024;
        public const int MaxRollsPerDay = 5;
        public const long DefaultMaxTotalBytes = 300L * 1024 * 1024;

        private static readonly object Gate = new object();
        private static readonly object WriteGate = new object();
        private static readonly Dictionary<string, DateTime> RateLimits = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static List<string> pending = new List<string>();
        private static int pendingChars;
        private static Timer flushTimer;
        private static bool timerArmed;
        private static bool started;
        private static bool handlersInstalled;
        private static string folder = string.Empty;
        private static string component = string.Empty;
        private static string sessionId = string.Empty;
        private static string parentId = string.Empty;
        private static int processId;
        private static JoyProLogLevel minLevel = JoyProLogLevel.Debug;
        private static int retentionDays = DefaultRetentionDays;
        private static string currentDay = string.Empty;
        private static int currentIndex;
        private static int callCounter;
        private static Mutex fileMutex;

        public static long MaxFileBytes { get; set; } = DefaultMaxFileBytes;

        public static bool IsStarted
        {
            get { lock (Gate) return started; }
        }

        public static string Component
        {
            get { return component; }
        }

        public static string SessionId
        {
            get { return sessionId; }
        }

        public static string ParentId
        {
            get { return parentId; }
        }

        // The folder this process writes to (empty before Start).
        public static string Folder
        {
            get { return folder; }
        }

        public static int RetentionDays
        {
            get { return retentionDays; }
        }

        public static JoyProLogLevel Level
        {
            get { return minLevel; }
        }

        // <JOYPRO_LOG_DIR>, else <data>\Logs (JoyProDataFolder: JOYPRO_DATA_DIR, else %LOCALAPPDATA%\JoyPro2).
        public static string DefaultFolder()
        {
            string overrideFolder = Environment.GetEnvironmentVariable(FolderVariable);
            if (!string.IsNullOrWhiteSpace(overrideFolder))
                return overrideFolder.Trim();
            return Path.Combine(JoyProDataFolder.Root(), FolderName);
        }

        // Starts logging for this process: reads the settings, deletes old logs (at most once an hour) and writes the
        // header (version, OS, .NET, culture, command line). A second call does nothing until Stop.
        public static void Start(string componentName, string version, string[] args = null, string folderOverride = null)
        {
            // Every JoyPro process starts here: an old %LOCALAPPDATA%\JoyPro is moved into JoyPro2 first (once).
            JoyProDataFolder.EnsureMigrated();
            lock (Gate)
            {
                if (started)
                    return;
                folder = string.IsNullOrWhiteSpace(folderOverride) ? DefaultFolder() : folderOverride;
                component = CleanToken(string.IsNullOrWhiteSpace(componentName) ? "app" : componentName);
                string inheritedSession = Environment.GetEnvironmentVariable(SessionVariable);
                sessionId = string.IsNullOrWhiteSpace(inheritedSession) ? NewId(8) : CleanToken(inheritedSession);
                string inheritedParent = Environment.GetEnvironmentVariable(ParentVariable);
                parentId = string.IsNullOrWhiteSpace(inheritedParent) ? string.Empty : CleanToken(inheritedParent);
                processId = CurrentProcessId();
                JoyProLogSettings settings = JoyProLogSettings.Read(folder);
                minLevel = settings.Level;
                retentionDays = settings.RetentionDays;
                currentDay = string.Empty;
                currentIndex = 0;
                callCounter = 0;
                RateLimits.Clear();
                if (minLevel == JoyProLogLevel.Off)
                    return;
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception)
                {
                    return;
                }
                fileMutex = CreateMutex(folder);
                started = true;
                if (!handlersInstalled)
                {
                    handlersInstalled = true;
                    AppDomain.CurrentDomain.ProcessExit += (s, e) => Flush();
                    // The programs' own crash handlers log the exception; this one makes sure it reaches the file.
                    AppDomain.CurrentDomain.UnhandledException += (s, e) => Flush();
                }
            }

            List<string> deleted = CleanupIfDue(folder, retentionDays, DateTime.UtcNow);
            Info("Log", "=== " + component + " " + (version ?? "?") + " started (pid " + processId.ToString(CultureInfo.InvariantCulture) +
                        ", session " + sessionId + (parentId.Length > 0 ? ", started by " + parentId : string.Empty) + ") ===");
            Info("Log", "System: " + SystemDescription());
            DataMigrationResult moved = JoyProDataFolder.LastResult;
            if (moved != null && moved.Status != "none")
                Info("Data", JoyProDataFolder.Describe(moved));
            TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
            Info("Log", "Culture: " + CultureName(CultureInfo.CurrentCulture) + " / UI " + CultureName(CultureInfo.CurrentUICulture) +
                        ", time zone " + TimeZoneInfo.Local.Id + " (UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture) + ")");
            if (args != null)
                Info("Log", "Command line: " + JoinArguments(args));
            Debug("Log", "Logs: " + folder + " (level " + minLevel.ToString().ToLowerInvariant() + ", keep " +
                         retentionDays.ToString(CultureInfo.InvariantCulture) + " days, " + (MaxFileBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) +
                         " MB per file, " + MaxRollsPerDay.ToString(CultureInfo.InvariantCulture) + " files per day)");
            if (deleted.Count > 0)
                Debug("Log", "Deleted " + deleted.Count.ToString(CultureInfo.InvariantCulture) + " old log/crash file(s): " + string.Join(", ", deleted.Select(Path.GetFileName)));
        }

        // Writes what is queued and stops logging (tests; a later Start begins again).
        public static void Stop()
        {
            Flush();
            lock (Gate)
            {
                started = false;
                if (flushTimer != null)
                {
                    flushTimer.Dispose();
                    flushTimer = null;
                }
                timerArmed = false;
                pending = new List<string>();
                pendingChars = 0;
                if (fileMutex != null)
                {
                    fileMutex.Dispose();
                    fileMutex = null;
                }
            }
        }

        public static void Debug(string category, string message)
        {
            Write(JoyProLogLevel.Debug, category, message, null);
        }

        public static void Info(string category, string message)
        {
            Write(JoyProLogLevel.Info, category, message, null);
        }

        public static void Warn(string category, string message, Exception exception = null)
        {
            Write(JoyProLogLevel.Warn, category, message, exception);
        }

        public static void Error(string category, string message, Exception exception = null)
        {
            Write(JoyProLogLevel.Error, category, message, exception);
        }

        public static bool IsEnabled(JoyProLogLevel level)
        {
            return started && level >= minLevel && level != JoyProLogLevel.Off;
        }

        // True at most once per interval and key: for summaries from loops (FFB ticks, watcher events).
        public static bool ShouldLog(string key, TimeSpan interval)
        {
            if (!started)
                return false;
            lock (Gate)
            {
                DateTime now = DateTime.UtcNow;
                DateTime last;
                if (RateLimits.TryGetValue(key ?? string.Empty, out last) && now - last < interval)
                    return false;
                RateLimits[key ?? string.Empty] = now;
                return true;
            }
        }

        // The call a long-running process (the engine server) works for now: its log lines carry this id (c:gui-17).
        public static void SetParent(string callId)
        {
            lock (Gate)
                parentId = string.IsNullOrWhiteSpace(callId) ? string.Empty : CleanToken(callId);
        }

        // A new id for a child process this process starts ("gui-17"); log it with the call, pass it with ChildEnvironment.
        public static string NewCallId()
        {
            int n = Interlocked.Increment(ref callCounter);
            return (component.Length == 0 ? "app" : component) + "-" + n.ToString(CultureInfo.InvariantCulture);
        }

        // Environment variables for a child process so its log lines carry this session and the call id.
        public static IDictionary<string, string> ChildEnvironment(string callId)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (sessionId.Length > 0)
                values[SessionVariable] = sessionId;
            if (!string.IsNullOrWhiteSpace(callId))
                values[ParentVariable] = callId;
            return values;
        }

        public static void Write(JoyProLogLevel level, string category, string message, Exception exception)
        {
            if (!started || level < minLevel || level == JoyProLogLevel.Off)
                return;
            string line;
            try
            {
                line = FormatLine(DateTime.Now, level, category, message, exception);
            }
            catch (Exception)
            {
                return;
            }

            bool flushNow;
            lock (Gate)
            {
                if (!started)
                    return;
                pending.Add(line);
                pendingChars += line.Length;
                flushNow = level >= JoyProLogLevel.Warn || pendingChars > 256 * 1024;
                if (!flushNow && !timerArmed)
                {
                    if (flushTimer == null)
                        flushTimer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
                    flushTimer.Change(500, Timeout.Infinite);
                    timerArmed = true;
                }
            }
            if (flushNow)
                Flush();
        }

        private static void OnTimer()
        {
            lock (Gate)
                timerArmed = false;
            Flush();
        }

        public static void Flush()
        {
            List<string> lines;
            lock (Gate)
            {
                if (pending.Count == 0)
                    return;
                lines = pending;
                pending = new List<string>();
                pendingChars = 0;
            }
            lock (WriteGate)
            {
                try
                {
                    WriteLines(lines);
                }
                catch (Exception)
                {
                    // Logging never breaks JoyPro.
                }
            }
        }

        internal static string FormatLine(DateTime time, JoyProLogLevel level, string category, string message, Exception exception)
        {
            StringBuilder sb = new StringBuilder(160);
            sb.Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            sb.Append(' ');
            sb.Append(LevelName(level));
            sb.Append(' ');
            sb.Append(component.Length == 0 ? "app" : component);
            sb.Append('#');
            sb.Append(processId.ToString(CultureInfo.InvariantCulture));
            sb.Append(" s:");
            sb.Append(sessionId.Length == 0 ? "-" : sessionId);
            sb.Append(" c:");
            sb.Append(parentId.Length == 0 ? "-" : parentId);
            sb.Append(" t:");
            sb.Append(Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture));
            sb.Append(" [");
            sb.Append(string.IsNullOrWhiteSpace(category) ? "-" : category.Trim());
            sb.Append("] ");
            string text = message ?? string.Empty;
            if (exception != null)
                text = (text.Length > 0 ? text + Environment.NewLine : string.Empty) + exception;
            text = LogScrubber.Scrub(text).Replace("\r\n", "\n").TrimEnd('\n', ' ');
            string[] parts = text.Split('\n');
            sb.Append(parts[0].TrimEnd('\r'));
            for (int i = 1; i < parts.Length; i++)
            {
                sb.Append(Environment.NewLine);
                sb.Append("    ");
                sb.Append(parts[i].TrimEnd('\r'));
            }
            sb.Append(Environment.NewLine);
            return sb.ToString();
        }

        private static string LevelName(JoyProLogLevel level)
        {
            switch (level)
            {
                case JoyProLogLevel.Debug:
                    return "DEBUG";
                case JoyProLogLevel.Info:
                    return "INFO ";
                case JoyProLogLevel.Warn:
                    return "WARN ";
                default:
                    return "ERROR";
            }
        }

        // <component>-yyyyMMdd.log, then <component>-yyyyMMdd-1.log ... when a file reaches MaxFileBytes.
        public static string FileName(string componentName, DateTime day, int index)
        {
            string stem = componentName + "-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            return index <= 0 ? stem + ".log" : stem + "-" + index.ToString(CultureInfo.InvariantCulture) + ".log";
        }

        private static void WriteLines(List<string> lines)
        {
            string targetFolder = folder;
            if (string.IsNullOrEmpty(targetFolder))
                return;
            byte[] bytes = new UTF8Encoding(false).GetBytes(string.Concat(lines));
            DateTime now = DateTime.Now;
            string day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (day != currentDay)
            {
                currentDay = day;
                currentIndex = 0;
            }

            bool owned = false;
            Mutex mutex = fileMutex;
            try
            {
                if (mutex != null)
                {
                    try
                    {
                        owned = mutex.WaitOne(2000);
                    }
                    catch (AbandonedMutexException)
                    {
                        owned = true;
                    }
                }

                Directory.CreateDirectory(targetFolder);
                while (true)
                {
                    string path = Path.Combine(targetFolder, FileName(component, now, currentIndex));
                    long length = File.Exists(path) ? new FileInfo(path).Length : 0;
                    if (length > 0 && length + bytes.Length > MaxFileBytes)
                    {
                        if (currentIndex + 1 < MaxRollsPerDay)
                        {
                            currentIndex++;
                            continue;
                        }
                        // Daily cap reached: the rest of the day is dropped (a runaway loop must not fill the disk).
                        return;
                    }
                    using (FileStream stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        stream.Seek(0, SeekOrigin.End);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    return;
                }
            }
            finally
            {
                if (owned)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        // ------------------------------------------------------------------ retention

        // Runs Cleanup when the last one is older than an hour (one file time read otherwise).
        public static List<string> CleanupIfDue(string logFolder, int days, DateTime nowUtc)
        {
            try
            {
                string marker = Path.Combine(logFolder, CleanupMarkerName);
                if (File.Exists(marker) && nowUtc - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromHours(1))
                    return new List<string>();
                Directory.CreateDirectory(logFolder);
                File.WriteAllText(marker, nowUtc.ToString("o", CultureInfo.InvariantCulture));
                File.SetLastWriteTimeUtc(marker, nowUtc);
                List<string> deleted = Cleanup(logFolder, days, DefaultMaxTotalBytes, nowUtc);
                // Crash reports (a sibling folder of the default Logs folder) are kept at least 30 days: the GUI shows
                // them at its next start, which can be a while.
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FolderVariable)))
                {
                    string parent = Path.GetDirectoryName(Path.GetFullPath(logFolder).TrimEnd(Path.DirectorySeparatorChar));
                    if (!string.IsNullOrEmpty(parent))
                        deleted.AddRange(CleanupCrashReports(Path.Combine(parent, "CrashReports"), Math.Max(days, CrashReportMinRetentionDays), nowUtc));
                }
                return deleted;
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        // Deletes *.log files last written more than `days` days ago, then the oldest ones while all together are
        // larger than maxTotalBytes. Returns the deleted paths.
        public static List<string> Cleanup(string logFolder, int days, long maxTotalBytes, DateTime nowUtc)
        {
            List<string> deleted = new List<string>();
            if (string.IsNullOrWhiteSpace(logFolder) || !Directory.Exists(logFolder))
                return deleted;
            DateTime cutoff = nowUtc - TimeSpan.FromDays(ClampDays(days));
            List<FileInfo> files = new DirectoryInfo(logFolder).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (FileInfo file in files)
            {
                bool tooOld = file.LastWriteTimeUtc < cutoff;
                bool overCap = !tooOld && total + file.Length > maxTotalBytes && total > 0;
                if (tooOld || overCap)
                {
                    if (TryDelete(file.FullName))
                        deleted.Add(file.FullName);
                    continue;
                }
                total += file.Length;
            }
            return deleted;
        }

        public static List<string> CleanupCrashReports(string crashFolder, int days, DateTime nowUtc)
        {
            List<string> deleted = new List<string>();
            if (string.IsNullOrWhiteSpace(crashFolder) || !Directory.Exists(crashFolder))
                return deleted;
            DateTime cutoff = nowUtc - TimeSpan.FromDays(Math.Max(1, days));
            // The cli subfolder holds the reports of command-line and tool runs (Program.WriteCrashReport).
            foreach (FileInfo file in new DirectoryInfo(crashFolder).GetFiles("*.txt", SearchOption.AllDirectories))
            {
                if (file.LastWriteTimeUtc < cutoff && TryDelete(file.FullName))
                    deleted.Add(file.FullName);
            }
            return deleted;
        }

        private static bool TryDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static int ClampDays(int days)
        {
            return Math.Max(MinRetentionDays, Math.Min(MaxRetentionDays, days));
        }

        // ------------------------------------------------------------------ helpers

        public static string SystemDescription()
        {
            string os;
            string framework;
            try
            {
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim() + " " + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            }
            catch (Exception)
            {
                os = Environment.OSVersion.ToString();
                framework = ".NET " + Environment.Version;
            }
            return os + ", " + (Environment.Is64BitProcess ? "64-bit" : "32-bit") + " process, " + framework +
                   " (CLR " + Environment.Version + "), " + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture) + " CPUs";
        }

        public static string JoinArguments(IEnumerable<string> args)
        {
            return string.Join(" ", (args ?? Enumerable.Empty<string>()).Select(a => a == null ? string.Empty : (a.Length == 0 || a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a)));
        }

        // The first maxChars characters of a text (engine output in the log), with a note how much was left out.
        public static string Truncate(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
                return text ?? string.Empty;
            return text.Substring(0, maxChars) + Environment.NewLine + "... (" + (text.Length - maxChars).ToString(CultureInfo.InvariantCulture) + " more characters not logged)";
        }

        private static string CultureName(CultureInfo culture)
        {
            return string.IsNullOrEmpty(culture.Name) ? "invariant" : culture.Name;
        }

        private static string NewId(int length)
        {
            return Guid.NewGuid().ToString("N").Substring(0, length);
        }

        private static string CleanToken(string value)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in value.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
                    sb.Append(c);
                if (sb.Length >= 40)
                    break;
            }
            return sb.Length == 0 ? "x" : sb.ToString();
        }

        private static int CurrentProcessId()
        {
            try
            {
                using (Process self = Process.GetCurrentProcess())
                    return self.Id;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static Mutex CreateMutex(string logFolder)
        {
            try
            {
                string key = Path.GetFullPath(logFolder).TrimEnd('\\', '/').ToUpperInvariant();
                uint hash = 2166136261;
                foreach (char c in key)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return new Mutex(false, @"Local\JoyPro.Log." + hash.ToString("X8", CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    // <Logs>\log-settings.txt ("retention-days=7", "level=debug"), written by the GUI's settings page and read by every
    // component at start. JOYPRO_LOG_RETENTION_DAYS / JOYPRO_LOG_LEVEL override it.
    public sealed class JoyProLogSettings
    {
        public int RetentionDays { get; set; } = JoyProLog.DefaultRetentionDays;

        public JoyProLogLevel Level { get; set; } = JoyProLogLevel.Debug;

        public static JoyProLogSettings Read(string logFolder)
        {
            JoyProLogSettings settings = new JoyProLogSettings();
            try
            {
                string path = Path.Combine(logFolder ?? string.Empty, JoyProLog.SettingsFileName);
                if (File.Exists(path))
                {
                    foreach (string raw in File.ReadAllLines(path))
                    {
                        string line = raw.Trim();
                        int eq = line.IndexOf('=');
                        if (line.StartsWith("#", StringComparison.Ordinal) || eq <= 0)
                            continue;
                        settings.Apply(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                    }
                }
            }
            catch (Exception)
            {
            }
            string days = Environment.GetEnvironmentVariable(JoyProLog.RetentionVariable);
            if (!string.IsNullOrWhiteSpace(days))
                settings.Apply("retention-days", days);
            string level = Environment.GetEnvironmentVariable(JoyProLog.LevelVariable);
            if (!string.IsNullOrWhiteSpace(level))
                settings.Apply("level", level);
            return settings;
        }

        public void Write(string logFolder)
        {
            Directory.CreateDirectory(logFolder);
            File.WriteAllText(Path.Combine(logFolder, JoyProLog.SettingsFileName),
                "# JoyPro log settings (docs/logging.md); the GUI's Settings page changes them." + Environment.NewLine +
                "retention-days=" + JoyProLog.ClampDays(RetentionDays).ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                "level=" + Level.ToString().ToLowerInvariant() + Environment.NewLine,
                new UTF8Encoding(false));
        }

        private void Apply(string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "retention-days":
                    int days;
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out days))
                        RetentionDays = JoyProLog.ClampDays(days);
                    break;
                case "level":
                    JoyProLogLevel level;
                    if (Enum.TryParse(value, true, out level))
                        Level = level;
                    break;
            }
        }
    }

    // Removes personal data from log lines and problem reports: the user folder becomes %USERPROFILE%, the Windows user
    // name <user>, the PC name <pc>, e-mail addresses <email>, values of password/token/key/secret settings and
    // "Bearer" tokens <redacted>, Steam ids (7656119..., userdata\<id>) "steam#" + 8 hex digits of a hash salted with
    // values that never reach a log (the same id gives the same hash on one PC, so lines stay comparable).
    public static class LogScrubber
    {
        private static readonly object Gate = new object();
        private static List<KeyValuePair<Regex, string>> rules;
        private static string salt = string.Empty;

        private static readonly Regex Email = new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant);
        // Passwords and secrets of any length; token-like values only from 12 characters on (layout "token: JOY_BTN1"
        // lines stay readable).
        private static readonly Regex Secret = new Regex(
            @"(?i)\b(password|passwd|pwd|secret|client_secret)(\s*[:=]\s*)(""?)([^\s""'&;,)\]}]+)",
            RegexOptions.CultureInvariant);
        private static readonly Regex TokenValue = new Regex(
            @"(?i)\b(token|access_token|refresh_token|id_token|api[_\-]?key|apikey|sessionid|session_id|cookie|authorization|auth)(\s*[:=]\s*)(""?)([^\s""'&;,)\]}]{12,})",
            RegexOptions.CultureInvariant);
        private static readonly Regex Bearer = new Regex(@"(?i)\b(bearer)\s+[A-Za-z0-9\-._~+/]{8,}=*", RegexOptions.CultureInvariant);
        private static readonly Regex SteamId64 = new Regex(@"(?<![0-9])7656119[0-9]{10}(?![0-9])", RegexOptions.CultureInvariant);
        private static readonly Regex SteamUserData = new Regex(@"(?i)(userdata[\\/]+)([0-9]{3,12})(?![0-9])", RegexOptions.CultureInvariant);
        // Game account folders: War Thunder "Saves\<account id>", X4 "Egosoft\X4\<account id>".
        private static readonly Regex AccountFolder = new Regex(@"(?i)((?:[\\/]Saves|Egosoft[\\/]+X4)[\\/]+)([0-9]{4,12})(?![0-9])", RegexOptions.CultureInvariant);

        // Tests set the values; null keeps the real ones (Environment).
        public static void Configure(string userProfile, string userName, string machineName)
        {
            lock (Gate)
                rules = BuildRules(userProfile, userName, machineName);
        }

        public static void Reset()
        {
            lock (Gate)
                rules = null;
        }

        public static string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? string.Empty;
            List<KeyValuePair<Regex, string>> current;
            lock (Gate)
            {
                if (rules == null)
                    rules = BuildRules(null, null, null);
                current = rules;
            }
            string result = text;
            if (result.IndexOf('@') >= 0)
                result = Email.Replace(result, "<email>");
            foreach (KeyValuePair<Regex, string> rule in current)
                result = rule.Key.Replace(result, rule.Value);
            result = SteamUserData.Replace(result, m => m.Groups[1].Value + SteamHash(m.Groups[2].Value));
            result = AccountFolder.Replace(result, m => m.Groups[1].Value + "id#" + SteamHash(m.Groups[2].Value).Substring("steam#".Length));
            result = SteamId64.Replace(result, m => SteamHash(m.Value));
            result = Bearer.Replace(result, m => m.Groups[1].Value + " <redacted>");
            result = Secret.Replace(result, m => m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + "<redacted>");
            result = TokenValue.Replace(result, m => m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + "<redacted>");
            return result;
        }

        public static string SteamHash(string id)
        {
            string key;
            lock (Gate)
                key = salt;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes("JoyPro|" + key + "|" + id));
                StringBuilder sb = new StringBuilder("steam#");
                for (int i = 0; i < 4; i++)
                    sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static List<KeyValuePair<Regex, string>> BuildRules(string userProfile, string userName, string machineName)
        {
            string profile = userProfile ?? SafeGet(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            string user = userName ?? SafeGet(() => Environment.UserName);
            string machine = machineName ?? SafeGet(() => Environment.MachineName);
            salt = (machine ?? string.Empty) + "|" + (user ?? string.Empty);
            List<KeyValuePair<Regex, string>> list = new List<KeyValuePair<Regex, string>>();
            string trimmedProfile = (profile ?? string.Empty).TrimEnd('\\', '/');
            if (trimmedProfile.Length >= 4)
            {
                // JSON-escaped (C:\\Users\\name), plain and forward-slash spellings of the user folder.
                string escaped = trimmedProfile.Replace("\\", "\\\\");
                list.Add(Rule(Regex.Escape(escaped) + @"(?![A-Za-z0-9])", "%USERPROFILE%"));
                list.Add(Rule(Regex.Escape(trimmedProfile) + @"(?![A-Za-z0-9])", "%USERPROFILE%"));
                list.Add(Rule(Regex.Escape(trimmedProfile.Replace('\\', '/')) + @"(?![A-Za-z0-9])", "%USERPROFILE%"));
            }
            if (!string.IsNullOrWhiteSpace(user) && user.Trim().Length >= 2)
                list.Add(Rule(@"(?<![A-Za-z0-9%<])" + Regex.Escape(user.Trim()) + @"(?![A-Za-z0-9])", "<user>"));
            if (!string.IsNullOrWhiteSpace(machine) && machine.Trim().Length >= 3)
                list.Add(Rule(@"(?<![A-Za-z0-9])" + Regex.Escape(machine.Trim()) + @"(?![A-Za-z0-9])", "<pc>"));
            return list;
        }

        private static KeyValuePair<Regex, string> Rule(string pattern, string replacement)
        {
            return new KeyValuePair<Regex, string>(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), replacement.Replace("$", "$$"));
        }

        private static string SafeGet(Func<string> read)
        {
            try
            {
                return read() ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
