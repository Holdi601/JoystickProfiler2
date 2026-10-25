#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace JoyPro.Usage
{
    public sealed class UsageHarvestOptions
    {
        public HashSet<string> Games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool Steam = true;
        // Read every file again, also the ones the store already knows (sessions still replace, never add).
        public bool Full;
        // Read, but write nothing.
        public bool DryRun;
    }

    public sealed class UsageHarvestSource
    {
        public string Game = string.Empty;
        public string Kind = string.Empty;
        public string Title = string.Empty;
        public string Location = string.Empty;
        public bool Exists;
        public bool LiveTail;
        public int Files;
        public int FilesRead;
        public int Sessions;
        public double Hours;
        public int Totals;
        public double TotalHours;
        public int Units;
        public List<string> Notes = new List<string>();
    }

    public sealed class UsageHarvestReport
    {
        public string Folder = string.Empty;
        public DateTime Utc;
        public double Seconds;
        public int FilesSeen;
        public int FilesRead;
        public int FilesSkipped;
        public int SessionsNew;
        public int SessionsUpdated;
        public int TotalsUpdated;
        public int LiveCopiesRead;
        public int LiveCopiesRemoved;
        public bool DryRun;
        public List<UsageHarvestSource> Sources = new List<UsageHarvestSource>();
        public List<string> Notes = new List<string>();
    }

    // Reads what is new since the last harvest (at app start, after a game closed, on demand) into the store. A file
    // whose size and write time the store knows is skipped; a copy of a known file (same first bytes and size, e.g. a
    // log moved into a backup folder) too. A session read again replaces its record (same key), so nothing counts twice.
    public static class UsageHarvester
    {
        // The live copies the watcher writes: <kind>~<file name>~<ticks>.live while the game runs, .done afterwards.
        public const string LiveExtension = ".live";
        public const string DoneExtension = ".done";

        public static UsageHarvestReport Run(UsageStore store, IUsageLocator locator, UsageHarvestOptions options)
        {
            options = options ?? new UsageHarvestOptions();
            Stopwatch clock = Stopwatch.StartNew();
            UsageHarvestReport report = new UsageHarvestReport { Folder = store.Folder, Utc = UsageStore.UtcNow(), DryRun = options.DryRun };
            List<UsageSession> sessions = new List<UsageSession>();
            List<UsageTotal> totals = new List<UsageTotal>();
            Dictionary<string, UsageFileStamp> stamps = new Dictionary<string, UsageFileStamp>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> knownHeads = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, UsageFileStamp> seen in store.Seen)
            {
                if (!string.IsNullOrEmpty(seen.Value.Head))
                    knownHeads[seen.Value.Head + "|" + seen.Value.Size.ToString(CultureInfo.InvariantCulture)] = seen.Key;
            }

            List<UsageSourceSpec> specs = UsageSources.Discover(locator, options.Games);
            foreach (UsageSourceSpec spec in specs)
            {
                UsageHarvestSource source = SourceOf(report, spec.Game, spec.Kind, spec.Directory);
                source.LiveTail |= spec.LiveTail;
                if (spec.Note.Length > 0 && !source.Notes.Contains(spec.Note))
                    source.Notes.Add(spec.Note);
                if (spec.Kind == UsageSourceKinds.SteamPlaytime)
                    continue;
                source.Exists |= Directory.Exists(spec.Directory);
                foreach (string file in spec.Files())
                {
                    FileInfo info;
                    try
                    {
                        info = new FileInfo(file);
                        if (!info.Exists || info.Length == 0)
                            continue;
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    source.Files++;
                    report.FilesSeen++;
                    long writeMs = UnixMs(info.LastWriteTimeUtc);
                    UsageFileStamp stamp;
                    if (!options.Full && store.Seen.TryGetValue(file, out stamp) && stamp.SameAs(info.Length, writeMs))
                    {
                        report.FilesSkipped++;
                        continue;
                    }
                    string head = UsageInput.HeadHash(file);
                    string copyOf;
                    if (!options.Full && head.Length > 0 && knownHeads.TryGetValue(head + "|" + info.Length.ToString(CultureInfo.InvariantCulture), out copyOf) &&
                        !string.Equals(copyOf, file, StringComparison.OrdinalIgnoreCase))
                    {
                        // The same bytes under another name: known, nothing new to read.
                        stamps[file] = new UsageFileStamp { Size = info.Length, WriteMs = writeMs, Head = head, Kind = spec.Kind, Read = report.Utc };
                        report.FilesSkipped++;
                        continue;
                    }
                    UsageParseResult parsed = UsageParsers.Parse(new UsageInput(file, spec.Game, spec.Kind));
                    source.FilesRead++;
                    report.FilesRead++;
                    sessions.AddRange(parsed.Sessions);
                    totals.AddRange(parsed.Totals);
                    foreach (string note in parsed.Notes.Take(5))
                        if (!source.Notes.Contains(note) && source.Notes.Count < 12)
                            source.Notes.Add(note);
                    stamps[file] = new UsageFileStamp { Size = info.Length, WriteMs = writeMs, Head = head, Kind = spec.Kind, Read = report.Utc };
                    if (head.Length > 0)
                        knownHeads[head + "|" + info.Length.ToString(CultureInfo.InvariantCulture)] = file;
                }
            }

            // Steam: one total per game with a Steam app id (accounts add up).
            if (options.Steam)
            {
                List<string> vdfs = UsageSources.SteamConfigs(locator);
                List<string> steamGames = locator.Games.Where(g => options.Games.Count == 0 || options.Games.Contains(g)).ToList();
                List<string> notes = new List<string>();
                List<UsageTotal> steam = SteamUsageParser.Totals(vdfs, locator.SteamApps, steamGames, notes);
                totals.AddRange(steam);
                foreach (UsageTotal t in steam)
                {
                    UsageHarvestSource s = SourceOf(report, t.Game, UsageSourceKinds.SteamPlaytime, string.Join("; ", vdfs));
                    s.Exists = true;
                    s.Files = vdfs.Count;
                    s.FilesRead = vdfs.Count;
                }
                report.Notes.AddRange(notes);
            }

            // The watcher's live copies: read like the game's own file; a finished copy goes once its session is stored.
            List<string> finished = new List<string>();
            if (Directory.Exists(store.LiveFolder))
            {
                foreach (string file in Directory.GetFiles(store.LiveFolder, "*", SearchOption.AllDirectories))
                {
                    bool done = file.EndsWith(DoneExtension, StringComparison.OrdinalIgnoreCase);
                    if (!done && !file.EndsWith(LiveExtension, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string game = Path.GetFileName(Path.GetDirectoryName(file)) ?? string.Empty;
                    if (options.Games.Count > 0 && !options.Games.Contains(game))
                        continue;
                    string kind, original;
                    if (!ParseLiveName(Path.GetFileName(file), out kind, out original))
                        continue;
                    UsageParseResult parsed = UsageParsers.Parse(new UsageInput(file, game, kind, original));
                    report.LiveCopiesRead++;
                    sessions.AddRange(parsed.Sessions);
                    UsageHarvestSource source = SourceOf(report, game, kind, null);
                    source.LiveTail = true;
                    DateTime written = File.GetLastWriteTimeUtc(file);
                    // A live copy left behind (JoyPro ended while the game ran) is finished after two days.
                    if (done || written < report.Utc.AddDays(-2))
                        finished.Add(file);
                }
            }

            Dictionary<string, string> previous = store.Sessions.ToDictionary(p => p.Key, p => UsageJson.WriteSession(p.Value), StringComparer.Ordinal);
            if (!options.DryRun)
            {
                store.Commit(sessions, totals, stamps);
                foreach (string file in finished)
                {
                    try
                    {
                        File.Delete(file);
                        report.LiveCopiesRemoved++;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            foreach (string key in sessions.Select(x => x.Key).Distinct(StringComparer.Ordinal))
            {
                string old;
                UsageSession now;
                if (!previous.TryGetValue(key, out old))
                    report.SessionsNew++;
                else if (store.Sessions.TryGetValue(key, out now) && old != UsageJson.WriteSession(now))
                    report.SessionsUpdated++;
            }
            report.TotalsUpdated = totals.Count;

            // What each source holds now (all harvests together), for the report and the statistics page.
            IEnumerable<UsageSession> all = options.DryRun ? (IEnumerable<UsageSession>)sessions : store.Sessions.Values;
            foreach (UsageHarvestSource source in report.Sources)
            {
                List<UsageSession> mine = all.Where(s => string.Equals(s.Source, source.Kind, StringComparison.Ordinal) &&
                                                        (source.Game.Length == 0 || string.Equals(s.Game, source.Game, StringComparison.OrdinalIgnoreCase))).ToList();
                source.Sessions = mine.Count;
                source.Hours = Math.Round(mine.Sum(s => s.Seconds) / 3600.0, 2);
                source.Units = mine.SelectMany(s => s.Units).Select(u => u.Unit).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                IEnumerable<UsageTotal> myTotals = (options.DryRun ? totals : store.Totals.Values.ToList())
                    .Where(t => string.Equals(t.Source, source.Kind, StringComparison.Ordinal) && (source.Game.Length == 0 || string.Equals(t.Game, source.Game, StringComparison.OrdinalIgnoreCase)));
                source.Totals = myTotals.Count();
                source.TotalHours = Math.Round(myTotals.Sum(t => t.Hours), 2);
            }
            report.Seconds = Math.Round(clock.Elapsed.TotalSeconds, 2);
            if (!options.DryRun)
                SaveReport(store, report);
            return report;
        }

        private static UsageHarvestSource SourceOf(UsageHarvestReport report, string game, string kind, string location)
        {
            UsageHarvestSource source = report.Sources.FirstOrDefault(s => string.Equals(s.Game, game, StringComparison.OrdinalIgnoreCase) && s.Kind == kind);
            if (source == null)
            {
                source = new UsageHarvestSource { Game = game ?? string.Empty, Kind = kind, Title = UsageSourceKinds.Title(kind), Location = location ?? string.Empty };
                report.Sources.Add(source);
            }
            else if (!string.IsNullOrEmpty(location) && source.Location.IndexOf(location, StringComparison.OrdinalIgnoreCase) < 0)
            {
                source.Location = source.Location.Length == 0 ? location : source.Location + "; " + location;
            }
            return source;
        }

        public static string LiveName(string kind, string originalFile, DateTime startedUtc)
        {
            string safe = new string((originalFile ?? "log").Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '~' ? '_' : c).ToArray());
            return kind + "~" + safe + "~" + startedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + LiveExtension;
        }

        public static bool ParseLiveName(string name, out string kind, out string original)
        {
            kind = original = string.Empty;
            string bare = name;
            if (bare.EndsWith(DoneExtension, StringComparison.OrdinalIgnoreCase))
                bare = bare.Substring(0, bare.Length - DoneExtension.Length);
            if (bare.EndsWith(LiveExtension, StringComparison.OrdinalIgnoreCase))
                bare = bare.Substring(0, bare.Length - LiveExtension.Length);
            string[] parts = bare.Split('~');
            if (parts.Length < 3 || !UsageSourceKinds.All.Contains(parts[0]))
                return false;
            kind = parts[0];
            original = parts[1];
            return true;
        }

        public static long UnixMs(DateTime utc)
        {
            return (long)(utc.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private const string ReportFile = "last-harvest.json";

        private static void SaveReport(UsageStore store, UsageHarvestReport report)
        {
            try
            {
                Directory.CreateDirectory(store.Folder);
                File.WriteAllText(Path.Combine(store.Folder, ReportFile), UsageJson.Write(ReportObject(report), true), new System.Text.UTF8Encoding(false));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public static List<Dictionary<string, object>> LastSources(UsageStore store)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            try
            {
                string path = Path.Combine(store.Folder, ReportFile);
                if (!File.Exists(path))
                    return list;
                Dictionary<string, object> root = UsageStore.ParseObject(File.ReadAllText(path));
                foreach (object item in UsageJson.List(root, "Sources"))
                    if (item is Dictionary<string, object>)
                        list.Add((Dictionary<string, object>)item);
            }
            catch (IOException)
            {
            }
            return list;
        }

        public static Dictionary<string, object> ReportObject(UsageHarvestReport r)
        {
            return new Dictionary<string, object>
            {
                { "Folder", r.Folder }, { "Utc", UsageText.Iso(r.Utc) }, { "Seconds", r.Seconds }, { "DryRun", r.DryRun },
                { "FilesSeen", r.FilesSeen }, { "FilesRead", r.FilesRead }, { "FilesSkipped", r.FilesSkipped },
                { "SessionsNew", r.SessionsNew }, { "SessionsUpdated", r.SessionsUpdated }, { "TotalsUpdated", r.TotalsUpdated },
                { "LiveCopiesRead", r.LiveCopiesRead }, { "LiveCopiesRemoved", r.LiveCopiesRemoved },
                { "Sources", r.Sources.OrderBy(s => s.Game, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Kind, StringComparer.Ordinal).Select(s => (object)new Dictionary<string, object>
                    {
                        { "Game", s.Game }, { "Kind", s.Kind }, { "Title", s.Title }, { "Location", s.Location }, { "Exists", s.Exists }, { "LiveTail", s.LiveTail },
                        { "Files", s.Files }, { "FilesRead", s.FilesRead }, { "Sessions", s.Sessions }, { "Hours", s.Hours }, { "Units", s.Units },
                        { "Totals", s.Totals }, { "TotalHours", s.TotalHours }, { "Notes", s.Notes.Cast<object>().ToList() }
                    }).ToList() },
                { "Notes", r.Notes.Cast<object>().ToList() }
            };
        }
    }
}
