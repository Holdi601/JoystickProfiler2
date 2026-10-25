#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.Usage
{
    public sealed class UsageUnitStat
    {
        public string Game = string.Empty;
        public string Unit = string.Empty;
        public string Display = string.Empty;
        public string Class = string.Empty;
        public double Hours;
        public double LogHours;
        public double TotalHours;
        public int Sessions;
        public DateTime First = DateTime.MinValue;
        public DateTime Last = DateTime.MinValue;
        public Dictionary<string, double> Counters = new Dictionary<string, double>(StringComparer.Ordinal);
        // logs (sessions), total (a game's own total such as a logbook), binds (only the profile's binds)
        public string Evidence = "logs";
        public int Binds;
        public bool Pinned;
        public bool Excluded;

        public string Id
        {
            get { return UsageOverrides.Id(Game, Unit); }
        }
    }

    public sealed class UsageGameStat
    {
        public string Game = string.Empty;
        public string Name = string.Empty;
        public double Hours;
        public double LogHours;
        public double SteamHours;
        public double TotalHours;
        public int Sessions;
        public DateTime First = DateTime.MinValue;
        public DateTime Last = DateTime.MinValue;
        public Dictionary<string, double> Counters = new Dictionary<string, double>(StringComparer.Ordinal);
        public List<string> Sources = new List<string>();
        public bool Pinned;
        public bool Excluded;
    }

    // A flight (or game session) after overlaps between sources are removed: what the statistics count.
    public sealed class UsageFlight
    {
        public string Game = string.Empty;
        public string Unit = string.Empty;
        public string Display = string.Empty;
        public DateTime Start;
        public double Seconds;
        public bool DateOnly;
        public Dictionary<string, double> Counters = new Dictionary<string, double>(StringComparer.Ordinal);
    }

    // Sessions and totals of every source turned into hours per game and unit. Sources overlap (Tacview inside a game
    // log, a logbook line for a logged flight), so:
    //   - game time is the union of the game's session intervals (nothing counts twice);
    //   - a unit segment counts unless more than half of it lies in a segment of a more trusted source;
    //   - a logbook line (day only) counts unless a timed flight of the same unit was logged that day (its landings go
    //     to that flight when the flight has none);
    //   - totals the game keeps (Steam, logbooks) give the hours when they are higher than the logged ones.
    public sealed class UsageAggregate
    {
        public Dictionary<string, UsageGameStat> Games = new Dictionary<string, UsageGameStat>(StringComparer.OrdinalIgnoreCase);
        public List<UsageUnitStat> Units = new List<UsageUnitStat>();
        public List<UsageFlight> Flights = new List<UsageFlight>();
        // Merged game sessions (one per game run), for streaks, weekdays and the longest session.
        public List<UsageFlight> GameSessions = new List<UsageFlight>();

        public UsageUnitStat Unit(string game, string unit)
        {
            return Units.FirstOrDefault(u => string.Equals(u.Game, game, StringComparison.OrdinalIgnoreCase) && string.Equals(u.Unit, unit, StringComparison.OrdinalIgnoreCase));
        }

        public static UsageAggregate Build(IEnumerable<UsageSession> sessions, IEnumerable<UsageTotal> totals, Func<string, string> gameName = null, Func<string, string, string> classOf = null)
        {
            UsageAggregate result = new UsageAggregate();
            gameName = gameName ?? (g => g);
            List<UsageSession> all = (sessions ?? new UsageSession[0]).Where(s => s != null && s.Game.Length > 0).ToList();
            List<UsageTotal> allTotals = (totals ?? new UsageTotal[0]).Where(t => t != null && t.Game.Length > 0).ToList();
            foreach (string game in all.Select(s => s.Game).Concat(allTotals.Select(t => t.Game)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                UsageGameStat stat = new UsageGameStat { Game = game, Name = gameName(game) ?? game };
                result.Games[game] = stat;
                List<UsageSession> mine = all.Where(s => string.Equals(s.Game, game, StringComparison.OrdinalIgnoreCase)).ToList();
                stat.Sources.AddRange(mine.Select(s => s.Source).Concat(allTotals.Where(t => string.Equals(t.Game, game, StringComparison.OrdinalIgnoreCase)).Select(t => t.Source))
                    .Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal));
                BuildGame(result, stat, mine);
            }

            // Totals: Steam for the game, logbooks and saves for the game or a unit; the higher of logged and kept wins.
            foreach (UsageTotal total in allTotals)
            {
                UsageGameStat stat = result.Games[total.Game];
                if (total.LastPlayed > stat.Last)
                    stat.Last = total.LastPlayed;
                if (total.Unit.Length == 0)
                {
                    if (total.Source == UsageSourceKinds.SteamPlaytime)
                        stat.SteamHours += total.Hours;
                    else
                        stat.TotalHours += total.Hours;
                    foreach (KeyValuePair<string, double> c in total.Counters)
                    {
                        double logged;
                        stat.Counters.TryGetValue(c.Key, out logged);
                        if (c.Value > logged)
                            stat.Counters[c.Key] = c.Value;
                    }
                    continue;
                }
                UsageUnitStat unit = result.Unit(total.Game, total.Unit);
                if (unit == null)
                {
                    unit = new UsageUnitStat { Game = total.Game, Unit = total.Unit, Display = total.Display.Length > 0 ? total.Display : total.Unit, Evidence = "total" };
                    result.Units.Add(unit);
                }
                unit.TotalHours += total.Hours;
                if (total.LastPlayed > unit.Last)
                    unit.Last = total.LastPlayed;
                foreach (KeyValuePair<string, double> c in total.Counters)
                {
                    double logged;
                    unit.Counters.TryGetValue(c.Key, out logged);
                    if (c.Value > logged)
                        unit.Counters[c.Key] = c.Value;
                    double gameLogged;
                    stat.Counters.TryGetValue(c.Key, out gameLogged);
                    if (c.Value > gameLogged)
                        stat.Counters[c.Key] = c.Value;
                }
            }
            foreach (UsageUnitStat unit in result.Units)
            {
                unit.Hours = Math.Max(unit.LogHours, unit.TotalHours);
                if (classOf != null)
                    unit.Class = classOf(unit.Game, unit.Unit) ?? string.Empty;
            }
            foreach (UsageGameStat stat in result.Games.Values)
            {
                double unitTotals = result.Units.Where(u => string.Equals(u.Game, stat.Game, StringComparison.OrdinalIgnoreCase)).Sum(u => u.TotalHours);
                stat.Hours = new[] { stat.LogHours, stat.SteamHours, stat.TotalHours, unitTotals }.Max();
            }
            return result;
        }

        private sealed class Candidate
        {
            public UsageSession Session;
            public UsageSegment Segment;
            public int Priority;
        }

        private static void BuildGame(UsageAggregate result, UsageGameStat stat, List<UsageSession> sessions)
        {
            List<UsageSession> timed = sessions.Where(s => !s.DateOnly).OrderBy(s => s.Start).ToList();

            // Game time: union of the intervals.
            DateTime runStart = DateTime.MinValue, runEnd = DateTime.MinValue;
            UsageSession runSession = null;
            Action flush = () =>
            {
                if (runSession == null)
                    return;
                stat.LogHours += (runEnd - runStart).TotalHours;
                stat.Sessions++;
                result.GameSessions.Add(new UsageFlight { Game = stat.Game, Start = runStart, Seconds = (runEnd - runStart).TotalSeconds });
            };
            foreach (UsageSession s in timed)
            {
                if (runSession != null && s.Start <= runEnd)
                {
                    if (s.End > runEnd)
                        runEnd = s.End;
                    continue;
                }
                flush();
                runSession = s;
                runStart = s.Start;
                runEnd = s.End;
            }
            flush();
            foreach (UsageSession s in timed)
            {
                if (stat.First == DateTime.MinValue || s.Start < stat.First)
                    stat.First = s.Start;
                if (s.End > stat.Last)
                    stat.Last = s.End;
            }

            // Game counters: a session's counters count unless a more trusted session with counters covers most of it.
            foreach (UsageSession s in timed)
            {
                bool covered = timed.Any(o => !ReferenceEquals(o, s) && UsageSourceKinds.Priority(o.Source) > UsageSourceKinds.Priority(s.Source) &&
                                              o.Counters.Count > 0 && Overlap(o.Start, o.End, s.Start, s.End) > 0.5 * Math.Max(1, s.Seconds));
                if (covered)
                    continue;
                foreach (KeyValuePair<string, double> c in s.Counters)
                    UsageText.Add(stat.Counters, c.Key, c.Value);
            }

            // Unit segments by trust; a segment mostly inside an accepted one of another source is left out.
            List<Candidate> candidates = timed.SelectMany(s => s.Units.Select(u => new Candidate { Session = s, Segment = u, Priority = UsageSourceKinds.Priority(s.Source) }))
                .OrderByDescending(c => c.Priority).ThenByDescending(c => c.Segment.Seconds).ThenBy(c => c.Segment.Start).ToList();
            List<Candidate> accepted = new List<Candidate>();
            foreach (Candidate c in candidates)
            {
                // Only another source's segments push one out (one source never logs a flight twice).
                double overlap = accepted.Where(a => a.Session.Source != c.Session.Source)
                    .Sum(a => Overlap(a.Segment.Start, a.Segment.End, c.Segment.Start, c.Segment.End));
                if (c.Segment.Seconds > 0 && overlap > 0.5 * c.Segment.Seconds)
                    continue;
                if (c.Segment.Seconds <= 0 && accepted.Any(a => a.Session.Source != c.Session.Source && SameUnit(a.Segment.Unit, c.Segment.Unit) &&
                                                              c.Segment.Start >= a.Segment.Start.AddMinutes(-5) && c.Segment.Start <= a.Segment.End.AddMinutes(5)))
                    continue;
                accepted.Add(c);
            }
            List<UsageFlight> flights = accepted.Select(a => new UsageFlight
            {
                Game = stat.Game, Unit = a.Segment.Unit, Display = a.Segment.Display, Start = a.Segment.Start, Seconds = a.Segment.Seconds,
                Counters = new Dictionary<string, double>(a.Segment.Counters, StringComparer.Ordinal)
            }).ToList();

            // Logbook lines (a day only): skipped when a timed flight of that unit was logged that day.
            foreach (UsageSession s in sessions.Where(x => x.DateOnly))
            {
                DateTime day = s.Start.Date;
                bool anyUnit = false;
                foreach (UsageSegment u in s.Units.DefaultIfEmpty(new UsageSegment { Unit = string.Empty, Start = s.Start, Seconds = s.Seconds }))
                {
                    UsageFlight logged = flights.FirstOrDefault(f => !f.DateOnly && SameUnit(f.Unit, u.Unit) && LocalDay(f.Start) == day);
                    if (logged != null)
                    {
                        foreach (KeyValuePair<string, double> c in u.Counters.Count > 0 ? u.Counters : s.Counters)
                        {
                            if (!logged.Counters.ContainsKey(c.Key))
                            {
                                logged.Counters[c.Key] = c.Value;
                                UsageText.Add(stat.Counters, c.Key, c.Value);
                            }
                        }
                        anyUnit = true;
                        continue;
                    }
                    if (u.Unit.Length > 0)
                        flights.Add(new UsageFlight { Game = stat.Game, Unit = u.Unit, Display = u.Display, Start = day, Seconds = u.Seconds, DateOnly = true, Counters = new Dictionary<string, double>(u.Counters, StringComparer.Ordinal) });
                }
                if (anyUnit)
                    continue;
                stat.LogHours += s.Seconds / 3600.0;
                stat.Sessions++;
                foreach (KeyValuePair<string, double> c in s.Counters)
                    UsageText.Add(stat.Counters, c.Key, c.Value);
                result.GameSessions.Add(new UsageFlight { Game = stat.Game, Start = day, Seconds = s.Seconds, DateOnly = true });
                if (stat.First == DateTime.MinValue || day < stat.First)
                    stat.First = day;
                if (day.AddSeconds(s.Seconds) > stat.Last)
                    stat.Last = day.AddSeconds(s.Seconds);
            }

            result.Flights.AddRange(flights);
            foreach (IGrouping<string, UsageFlight> group in flights.GroupBy(f => f.Unit, StringComparer.OrdinalIgnoreCase))
            {
                UsageUnitStat unit = new UsageUnitStat
                {
                    Game = stat.Game,
                    Unit = group.First().Unit,
                    Display = group.Select(f => f.Display).FirstOrDefault(d => !string.IsNullOrEmpty(d) && d != group.Key) ?? group.First().Unit,
                    LogHours = group.Sum(f => f.Seconds) / 3600.0,
                    Sessions = group.Count(),
                    First = group.Min(f => f.Start),
                    Last = group.Max(f => f.Start.AddSeconds(f.Seconds)),
                    Evidence = "logs"
                };
                foreach (UsageFlight f in group)
                    foreach (KeyValuePair<string, double> c in f.Counters)
                        UsageText.Add(unit.Counters, c.Key, c.Value);
                result.Units.Add(unit);
            }
        }

        // The day as the user lived it (local time); logbook days are local already.
        public static DateTime LocalDay(DateTime utc)
        {
            try
            {
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local).Date;
            }
            catch (ArgumentException)
            {
                return utc.Date;
            }
        }

        public static double Overlap(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
        {
            DateTime start = aStart > bStart ? aStart : bStart;
            DateTime end = aEnd < bEnd ? aEnd : bEnd;
            return end > start ? (end - start).TotalSeconds : 0;
        }

        // "Cirrus SR22" and "Cirrus_SR22", "F-16C_50" and "F-16C 50": the same unit.
        public static bool SameUnit(string a, string b)
        {
            return string.Equals(Squash(a), Squash(b), StringComparison.OrdinalIgnoreCase);
        }

        public static string Squash(string s)
        {
            return new string((s ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }
    }
}
