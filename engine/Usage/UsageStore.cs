#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JoyPro.DeviceReference;

namespace JoyPro.Usage
{
    // A file the harvest has read: size, write time and a hash of its first bytes (a renamed or moved copy of a known
    // file, e.g. Star Citizen's Game.log moved into logbackups, is recognised by the hash and size).
    public sealed class UsageFileStamp
    {
        public long Size;
        public long WriteMs;
        public string Head = string.Empty;
        public string Kind = string.Empty;
        public DateTime Read;

        public bool SameAs(long size, long writeMs)
        {
            return Size == size && WriteMs == writeMs;
        }
    }

    // JoyPro's own usage summary (docs/usage-statistics.md): <data>\Usage, data = JOYPRO_DATA_DIR or %LOCALAPPDATA%\JoyPro
    // (so a portable setup with its own data folder carries it along).
    //   sessions.jsonl   one session per line; a session read again (same key) is appended again and the more complete
    //                    read wins when the file is read; compacted when it holds many replaced lines
    //   totals.json      totals the games keep themselves (Steam playtime, logbook hours), never lowered
    //   seen.json        the files already read (path -> size, write time, head hash), so nothing is counted twice
    //   exports.jsonl    one line per export JoyPro made (game, time)
    //   overrides.json   the ranking's pinned and excluded units
    //   Live\<game>\     the live copies of game logs the watcher made while the game ran (docs: live tail)
    //   Backups\<day>\   a copy of the files above, once a day before the first change (the newest 7 days stay)
    // Every change is one batched write under a lock file, so the GUI, its watcher and a command line run never mix.
    public sealed class UsageStore
    {
        public const string FolderName = "Usage";
        private const string SessionsFile = "sessions.jsonl";
        private const string TotalsFile = "totals.json";
        private const string SeenFile = "seen.json";
        private const string ExportsFile = "exports.jsonl";
        private const string OverridesFile = "overrides.json";
        private const string LockFile = ".lock";
        public const int BackupDays = 7;

        private int sessionLines;

        public UsageStore(string folder)
        {
            Folder = folder;
        }

        public string Folder { get; private set; }

        public string LiveFolder
        {
            get { return Path.Combine(Folder, "Live"); }
        }

        public string BackupFolder
        {
            get { return Path.Combine(Folder, "Backups"); }
        }

        public Dictionary<string, UsageSession> Sessions { get; } = new Dictionary<string, UsageSession>(StringComparer.Ordinal);
        public Dictionary<string, UsageTotal> Totals { get; } = new Dictionary<string, UsageTotal>(StringComparer.Ordinal);
        public Dictionary<string, UsageFileStamp> Seen { get; } = new Dictionary<string, UsageFileStamp>(StringComparer.OrdinalIgnoreCase);
        public List<KeyValuePair<string, DateTime>> Exports { get; } = new List<KeyValuePair<string, DateTime>>();
        public UsageOverrides Overrides { get; private set; } = new UsageOverrides();

        // Test hook: the clock for backups and stamps.
        public static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        public static string FolderIn(string dataRoot)
        {
            return Path.Combine(dataRoot, FolderName);
        }

        public static UsageStore Open(string folder)
        {
            UsageStore store = new UsageStore(folder);
            store.Load();
            return store;
        }

        public void Load()
        {
            Sessions.Clear();
            Totals.Clear();
            Seen.Clear();
            Exports.Clear();
            Overrides = new UsageOverrides();
            sessionLines = 0;
            foreach (string line in ReadLines(Path.Combine(Folder, SessionsFile)))
            {
                UsageSession session = UsageJson.ReadSession(line);
                if (session == null)
                    continue;
                sessionLines++;
                UsageSession old;
                Sessions.TryGetValue(session.Key, out old);
                Sessions[session.Key] = UsageSession.MoreComplete(old, session);
            }
            Dictionary<string, object> totals = ReadObject(Path.Combine(Folder, TotalsFile));
            foreach (object item in UsageJson.List(totals, "items"))
            {
                UsageTotal total = UsageJson.ReadTotal(item as Dictionary<string, object>);
                if (total != null)
                    Totals[total.Key] = total;
            }
            Dictionary<string, object> seen = ReadObject(Path.Combine(Folder, SeenFile));
            Dictionary<string, object> files = seen != null && seen.ContainsKey("files") ? seen["files"] as Dictionary<string, object> : null;
            if (files != null)
            {
                foreach (KeyValuePair<string, object> pair in files)
                {
                    Dictionary<string, object> f = pair.Value as Dictionary<string, object>;
                    if (f == null)
                        continue;
                    Seen[pair.Key] = new UsageFileStamp
                    {
                        Size = (long)UsageJson.Number(f, "size"),
                        WriteMs = (long)UsageJson.Number(f, "write"),
                        Head = UsageJson.Str(f, "head"),
                        Kind = UsageJson.Str(f, "kind"),
                        Read = UsageText.ParseIso(UsageJson.Str(f, "read"))
                    };
                }
            }
            foreach (string line in ReadLines(Path.Combine(Folder, ExportsFile)))
            {
                Dictionary<string, object> e = ParseObject(line);
                if (e == null)
                    continue;
                string game = UsageJson.Str(e, "g");
                DateTime time = UsageText.ParseIso(UsageJson.Str(e, "t"));
                if (game.Length > 0)
                    Exports.Add(new KeyValuePair<string, DateTime>(game, time));
            }
            Dictionary<string, object> overrides = ReadObject(Path.Combine(Folder, OverridesFile));
            foreach (object p in UsageJson.List(overrides, "pinned"))
                if (p is string)
                    Overrides.Pinned.Add((string)p);
            foreach (object x in UsageJson.List(overrides, "excluded"))
                if (x is string)
                    Overrides.Excluded.Add((string)x);
        }

        // Writes what a harvest found, in one go: new and changed sessions appended, totals and the read files rewritten.
        public void Commit(IEnumerable<UsageSession> sessions, IEnumerable<UsageTotal> totals, IDictionary<string, UsageFileStamp> stamps)
        {
            List<UsageSession> changedSessions = new List<UsageSession>();
            foreach (UsageSession session in sessions ?? new UsageSession[0])
            {
                if (session == null || string.IsNullOrEmpty(session.Key))
                    continue;
                UsageSession old;
                Sessions.TryGetValue(session.Key, out old);
                UsageSession winner = UsageSession.MoreComplete(old, session);
                if (old != null && ReferenceEquals(winner, old))
                    continue;
                if (old != null && UsageJson.WriteSession(old) == UsageJson.WriteSession(winner))
                    continue;
                Sessions[session.Key] = winner;
                changedSessions.Add(winner);
            }
            bool totalsChanged = false;
            foreach (UsageTotal total in totals ?? new UsageTotal[0])
            {
                if (total == null || string.IsNullOrEmpty(total.Key))
                    continue;
                UsageTotal old;
                Totals.TryGetValue(total.Key, out old);
                UsageTotal merged = UsageTotal.Merge(old, total);
                if (old != null && UsageJson.Write(UsageJson.TotalObject(old)) == UsageJson.Write(UsageJson.TotalObject(merged)))
                    continue;
                Totals[total.Key] = merged;
                totalsChanged = true;
            }
            bool seenChanged = false;
            if (stamps != null)
            {
                foreach (KeyValuePair<string, UsageFileStamp> stamp in stamps)
                {
                    Seen[stamp.Key] = stamp.Value;
                    seenChanged = true;
                }
            }
            if (changedSessions.Count == 0 && !totalsChanged && !seenChanged)
                return;

            WithLock(() =>
            {
                Directory.CreateDirectory(Folder);
                if (changedSessions.Count > 0 || totalsChanged)
                    BackupOncePerDay();
                if (changedSessions.Count > 0)
                {
                    sessionLines += changedSessions.Count;
                    if (sessionLines > Math.Max(200, Sessions.Count * 2))
                        Compact();
                    else
                    {
                        StringBuilder sb = new StringBuilder();
                        foreach (UsageSession session in changedSessions)
                            sb.Append(UsageJson.WriteSession(session)).Append('\n');
                        using (FileStream stream = new FileStream(Path.Combine(Folder, SessionsFile), FileMode.Append, FileAccess.Write, FileShare.Read))
                        {
                            byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
                            stream.Write(bytes, 0, bytes.Length);
                            stream.Flush(true);
                        }
                    }
                }
                if (totalsChanged)
                    WriteTotals();
                if (seenChanged)
                    WriteSeen();
            });
        }

        // One line per export JoyPro made.
        public void RecordExport(string game, DateTime utc)
        {
            if (string.IsNullOrWhiteSpace(game))
                return;
            Exports.Add(new KeyValuePair<string, DateTime>(game, utc));
            WithLock(() =>
            {
                Directory.CreateDirectory(Folder);
                string line = UsageJson.Write(new Dictionary<string, object> { { "g", game }, { "t", UsageText.Iso(utc) } }) + "\n";
                File.AppendAllText(Path.Combine(Folder, ExportsFile), line, new UTF8Encoding(false));
            });
        }

        public void SaveOverrides()
        {
            WithLock(() =>
            {
                Directory.CreateDirectory(Folder);
                BackupOncePerDay();
                Dictionary<string, object> root = new Dictionary<string, object>
                {
                    { "v", 1 },
                    { "pinned", Overrides.Pinned.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Cast<object>().ToList() },
                    { "excluded", Overrides.Excluded.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Cast<object>().ToList() }
                };
                WriteAtomic(Path.Combine(Folder, OverridesFile), UsageJson.Write(root, true));
            });
        }

        // Rewrites sessions.jsonl with one line per session (oldest first).
        public void Compact()
        {
            StringBuilder sb = new StringBuilder();
            foreach (UsageSession session in Sessions.Values.OrderBy(s => s.Start).ThenBy(s => s.Key, StringComparer.Ordinal))
                sb.Append(UsageJson.WriteSession(session)).Append('\n');
            WriteAtomic(Path.Combine(Folder, SessionsFile), sb.ToString());
            sessionLines = Sessions.Count;
        }

        public int SessionLineCount
        {
            get { return sessionLines; }
        }

        private void WriteTotals()
        {
            Dictionary<string, object> root = new Dictionary<string, object>
            {
                { "v", 1 },
                { "items", Totals.Values.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => (object)UsageJson.TotalObject(t)).ToList() }
            };
            WriteAtomic(Path.Combine(Folder, TotalsFile), UsageJson.Write(root, true));
        }

        private void WriteSeen()
        {
            Dictionary<string, object> files = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, UsageFileStamp> pair in Seen.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                files[pair.Key] = new Dictionary<string, object>
                {
                    { "size", pair.Value.Size }, { "write", pair.Value.WriteMs }, { "head", pair.Value.Head }, { "kind", pair.Value.Kind }, { "read", UsageText.Iso(pair.Value.Read) }
                };
            }
            WriteAtomic(Path.Combine(Folder, SeenFile), UsageJson.Write(new Dictionary<string, object> { { "v", 1 }, { "files", files } }, true));
        }

        // A copy of the store's files into Backups\<yyyy-MM-dd> before the first change of the day; the newest days stay.
        private void BackupOncePerDay()
        {
            try
            {
                string day = UtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                string target = Path.Combine(BackupFolder, day);
                if (Directory.Exists(target))
                    return;
                string[] names = { SessionsFile, TotalsFile, ExportsFile, OverridesFile, SeenFile };
                if (!names.Any(n => File.Exists(Path.Combine(Folder, n))))
                    return;
                Directory.CreateDirectory(target);
                foreach (string name in names)
                {
                    string source = Path.Combine(Folder, name);
                    if (File.Exists(source))
                        File.WriteAllBytes(Path.Combine(target, name), File.ReadAllBytes(source));
                }
                foreach (string old in Directory.GetDirectories(BackupFolder).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).Skip(BackupDays))
                {
                    try
                    {
                        Directory.Delete(old, true);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (IOException)
            {
                // A backup that cannot be written never stops the harvest.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public List<string> BackupDaysList()
        {
            if (!Directory.Exists(BackupFolder))
                return new List<string>();
            return Directory.GetDirectories(BackupFolder).Select(Path.GetFileName).OrderByDescending(d => d, StringComparer.Ordinal).ToList();
        }

        // Puts a backup day back (the current files go into today's backup first when there is none yet).
        public bool Restore(string day)
        {
            string source = Path.Combine(BackupFolder, day ?? string.Empty);
            if (string.IsNullOrWhiteSpace(day) || !Directory.Exists(source))
                return false;
            WithLock(() =>
            {
                // Read first: today's backup may push the oldest day (this one) out.
                Dictionary<string, byte[]> files = Directory.GetFiles(source).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
                BackupOncePerDay();
                foreach (KeyValuePair<string, byte[]> file in files)
                    WriteAtomicBytes(Path.Combine(Folder, file.Key), file.Value);
            });
            Load();
            return true;
        }

        private void WithLock(Action action)
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, LockFile);
            DateTime until = DateTime.UtcNow.AddSeconds(20);
            while (true)
            {
                FileStream lockStream = null;
                try
                {
                    lockStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    if (DateTime.UtcNow > until)
                        throw;
                    Thread.Sleep(50);
                    continue;
                }
                try
                {
                    action();
                }
                finally
                {
                    lockStream.Dispose();
                }
                return;
            }
        }

        private static void WriteAtomic(string path, string text)
        {
            WriteAtomicBytes(path, new UTF8Encoding(false).GetBytes(text));
        }

        private static void WriteAtomicBytes(string path, byte[] bytes)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                        File.Replace(temp, path, null);
                    else
                        File.Move(temp, path);
                    return;
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(25 * (attempt + 1));
                }
                catch (UnauthorizedAccessException) when (attempt < 20)
                {
                    Thread.Sleep(25 * (attempt + 1));
                }
            }
        }

        private static IEnumerable<string> ReadLines(string path)
        {
            if (!File.Exists(path))
                yield break;
            string text;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    text = reader.ReadToEnd();
            }
            catch (IOException)
            {
                yield break;
            }
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                    yield return trimmed;
            }
        }

        private static Dictionary<string, object> ReadObject(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    return ParseObject(reader.ReadToEnd());
            }
            catch (IOException)
            {
                return null;
            }
        }

        internal static Dictionary<string, object> ParseObject(string json)
        {
            try
            {
                return string.IsNullOrWhiteSpace(json) ? null : MiniJson.Parse(json) as Dictionary<string, object>;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    // The store's JSON (MiniJson objects; short keys keep sessions.jsonl small).
    public static class UsageJson
    {
        public static string Write(object value, bool indented = false)
        {
            return MiniJson.Write(value, indented);
        }

        public static string WriteSession(UsageSession s)
        {
            Dictionary<string, object> root = new Dictionary<string, object>
            {
                { "k", s.Key }, { "g", s.Game }, { "src", s.Source }, { "st", UsageText.Iso(s.Start) }, { "sec", Math.Round(s.Seconds, 1) }
            };
            if (s.DateOnly)
                root["day"] = true;
            if (s.Counters.Count > 0)
                root["c"] = Counters(s.Counters);
            if (s.Units.Count > 0)
            {
                root["u"] = s.Units.Select(u =>
                {
                    Dictionary<string, object> unit = new Dictionary<string, object> { { "u", u.Unit }, { "st", UsageText.Iso(u.Start) }, { "sec", Math.Round(u.Seconds, 1) } };
                    if (!string.IsNullOrEmpty(u.Display) && u.Display != u.Unit)
                        unit["n"] = u.Display;
                    if (u.Counters.Count > 0)
                        unit["c"] = Counters(u.Counters);
                    return (object)unit;
                }).ToList();
            }
            if (!string.IsNullOrEmpty(s.From))
                root["f"] = s.From;
            return MiniJson.Write(root, false);
        }

        public static UsageSession ReadSession(string line)
        {
            Dictionary<string, object> o = UsageStore.ParseObject(line);
            if (o == null)
                return null;
            UsageSession s = new UsageSession
            {
                Key = Str(o, "k"), Game = Str(o, "g"), Source = Str(o, "src"), Start = UsageText.ParseIso(Str(o, "st")), Seconds = Number(o, "sec"),
                DateOnly = Bool(o, "day"), From = Str(o, "f"), Counters = ReadCounters(o, "c")
            };
            foreach (object item in List(o, "u"))
            {
                Dictionary<string, object> u = item as Dictionary<string, object>;
                if (u == null)
                    continue;
                string unit = Str(u, "u");
                s.Units.Add(new UsageSegment { Unit = unit, Display = Str(u, "n").Length > 0 ? Str(u, "n") : unit, Start = UsageText.ParseIso(Str(u, "st")), Seconds = Number(u, "sec"), Counters = ReadCounters(u, "c") });
            }
            return s.Key.Length == 0 || s.Game.Length == 0 ? null : s;
        }

        public static Dictionary<string, object> TotalObject(UsageTotal t)
        {
            Dictionary<string, object> o = new Dictionary<string, object>
            {
                { "k", t.Key }, { "g", t.Game }, { "u", t.Unit }, { "src", t.Source }, { "h", Math.Round(t.Hours, 3) }
            };
            if (!string.IsNullOrEmpty(t.Display) && t.Display != t.Unit)
                o["n"] = t.Display;
            if (t.Counters.Count > 0)
                o["c"] = Counters(t.Counters);
            if (t.LastPlayed != DateTime.MinValue)
                o["last"] = UsageText.Iso(t.LastPlayed);
            if (!string.IsNullOrEmpty(t.From))
                o["f"] = t.From;
            return o;
        }

        public static UsageTotal ReadTotal(Dictionary<string, object> o)
        {
            if (o == null)
                return null;
            UsageTotal t = new UsageTotal
            {
                Key = Str(o, "k"), Game = Str(o, "g"), Unit = Str(o, "u"), Source = Str(o, "src"), Hours = Number(o, "h"),
                LastPlayed = UsageText.ParseIso(Str(o, "last")), From = Str(o, "f"), Counters = ReadCounters(o, "c")
            };
            t.Display = Str(o, "n").Length > 0 ? Str(o, "n") : t.Unit;
            return t.Key.Length == 0 ? null : t;
        }

        private static Dictionary<string, object> Counters(Dictionary<string, double> counters)
        {
            Dictionary<string, object> o = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> c in counters.OrderBy(c => c.Key, StringComparer.Ordinal))
                o[c.Key] = Math.Round(c.Value, 3);
            return o;
        }

        private static Dictionary<string, double> ReadCounters(Dictionary<string, object> o, string key)
        {
            Dictionary<string, double> counters = new Dictionary<string, double>(StringComparer.Ordinal);
            object value;
            Dictionary<string, object> map = o != null && o.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
            if (map == null)
                return counters;
            foreach (KeyValuePair<string, object> pair in map)
            {
                if (pair.Value is double)
                    counters[pair.Key] = (double)pair.Value;
            }
            return counters;
        }

        public static string Str(Dictionary<string, object> o, string key)
        {
            object value;
            return o != null && o.TryGetValue(key, out value) && value is string ? (string)value : string.Empty;
        }

        public static double Number(Dictionary<string, object> o, string key)
        {
            object value;
            return o != null && o.TryGetValue(key, out value) && value is double ? (double)value : 0;
        }

        public static bool Bool(Dictionary<string, object> o, string key)
        {
            object value;
            return o != null && o.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        public static List<object> List(Dictionary<string, object> o, string key)
        {
            object value;
            return o != null && o.TryGetValue(key, out value) && value is List<object> ? (List<object>)value : new List<object>();
        }
    }
}
