#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JoyPro.Usage
{
    // What the profile adds to the statistics (the engine fills it from the open profile and its history).
    public sealed class UsageProfileFacts
    {
        public List<Dictionary<string, object>> Devices = new List<Dictionary<string, object>>();
        public Dictionary<string, object> OldestUnusedBind;
        public List<Dictionary<string, object>> Growth = new List<Dictionary<string, object>>();
        public string ProfileName = string.Empty;
    }

    // engine usage stats: everything fun the store holds (docs/usage-statistics.md#statistics). Facts are ids with
    // values; the GUI turns them into sentences in the user's language (UsageFacts.English is the CLI's text).
    public static class UsageStats
    {
        public static Dictionary<string, object> Build(UsageStore store, UsageAggregate aggregate, UsageRanking ranking, UsageProfileFacts profile,
                                                       Func<string, string> gameName, DateTime nowUtc, int topUnits = 50)
        {
            gameName = gameName ?? (g => g);
            List<UsageGameStat> games = ranking.Games.Where(g => !g.Excluded).ToList();
            List<UsageUnitStat> units = ranking.Units.Where(u => !u.Excluded && u.Evidence != "binds").ToList();
            double totalHours = games.Sum(g => g.Hours);
            List<UsageFlight> sessions = aggregate.GameSessions.Where(s => !IsExcludedGame(ranking, s.Game)).ToList();

            Dictionary<string, object> root = new Dictionary<string, object>();
            root["Generated"] = UsageText.Iso(nowUtc);
            root["HasData"] = games.Count > 0;

            // Streaks over the days with any play (local days).
            HashSet<DateTime> days = new HashSet<DateTime>();
            foreach (UsageFlight s in sessions)
            {
                DateTime first = s.DateOnly ? s.Start.Date : UsageAggregate.LocalDay(s.Start);
                DateTime last = s.DateOnly ? first : UsageAggregate.LocalDay(s.Start.AddSeconds(s.Seconds));
                for (DateTime d = first; d <= last && (d - first).TotalDays < 3; d = d.AddDays(1))
                    days.Add(d);
            }
            List<DateTime> ordered = days.OrderBy(d => d).ToList();
            int longest = 0, run = 0;
            DateTime longestEnd = DateTime.MinValue, previous = DateTime.MinValue;
            foreach (DateTime d in ordered)
            {
                run = previous != DateTime.MinValue && (d - previous).TotalDays == 1 ? run + 1 : 1;
                if (run > longest)
                {
                    longest = run;
                    longestEnd = d;
                }
                previous = d;
            }
            DateTime today = UsageAggregate.LocalDay(nowUtc);
            int current = 0;
            for (DateTime d = days.Contains(today) ? today : today.AddDays(-1); days.Contains(d); d = d.AddDays(-1))
                current++;

            DateTime firstPlay = sessions.Count == 0 ? DateTime.MinValue : sessions.Min(s => s.Start);
            DateTime lastPlay = games.Count == 0 ? DateTime.MinValue : games.Max(g => g.Last);
            root["Totals"] = new Dictionary<string, object>
            {
                { "Hours", Math.Round(totalHours, 2) },
                { "LogHours", Math.Round(games.Sum(g => g.LogHours), 2) },
                { "Sessions", sessions.Count },
                { "Games", games.Count },
                { "Units", units.Count },
                { "First", UsageText.Iso(firstPlay) },
                { "Last", UsageText.Iso(lastPlay) },
                { "ActiveDays", days.Count },
                { "LongestStreakDays", longest },
                { "LongestStreakStart", longest == 0 ? string.Empty : longestEnd.AddDays(1 - longest).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                { "LongestStreakEnd", longest == 0 ? string.Empty : longestEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                { "CurrentStreakDays", current }
            };

            int rank = 0;
            root["Games"] = games.Select(g => (object)UsageRanking.GameObject(g, ++rank)).ToList();
            rank = 0;
            root["Units"] = units.Take(topUnits).Select(u => (object)UsageRanking.UnitObject(u, ++rank, gameName)).ToList();

            // Counters of every game together, and per game.
            List<object> counters = new List<object>();
            foreach (string name in UsageCounters.All)
            {
                Dictionary<string, object> perGame = new Dictionary<string, object>(StringComparer.Ordinal);
                double sum = 0;
                foreach (UsageGameStat g in games)
                {
                    double v;
                    if (g.Counters.TryGetValue(name, out v) && v > 0)
                    {
                        perGame[g.Game] = Math.Round(v, 1);
                        sum += v;
                    }
                }
                if (sum > 0)
                    counters.Add(new Dictionary<string, object> { { "Name", name }, { "Total", Math.Round(sum, 1) }, { "PerGame", perGame } });
            }
            root["Counters"] = counters;

            // When the user plays: hours per weekday (Monday first) and sessions per hour of the day, months.
            double[] weekdays = new double[7];
            int[] hours = new int[24];
            Dictionary<string, double> months = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (UsageFlight s in sessions)
            {
                DateTime local = s.DateOnly ? s.Start : ToLocal(s.Start);
                weekdays[((int)local.DayOfWeek + 6) % 7] += s.Seconds / 3600.0;
                if (!s.DateOnly)
                    hours[local.Hour]++;
                string month = local.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                double m;
                months.TryGetValue(month, out m);
                months[month] = m + s.Seconds / 3600.0;
            }
            root["Weekdays"] = weekdays.Select(w => (object)Math.Round(w, 2)).ToList();
            root["HoursOfDay"] = hours.Select(h => (object)h).ToList();
            root["Months"] = months.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => (object)new Dictionary<string, object> { { "Month", m.Key }, { "Hours", Math.Round(m.Value, 2) } }).ToList();

            UsageFlight longestSession = sessions.Where(s => !s.DateOnly).OrderByDescending(s => s.Seconds).FirstOrDefault();
            if (longestSession != null)
            {
                UsageFlight inUnit = aggregate.Flights.Where(f => string.Equals(f.Game, longestSession.Game, StringComparison.OrdinalIgnoreCase) && f.Start >= longestSession.Start &&
                                                                  f.Start <= longestSession.Start.AddSeconds(longestSession.Seconds)).OrderByDescending(f => f.Seconds).FirstOrDefault();
                root["LongestSession"] = new Dictionary<string, object>
                {
                    { "Game", longestSession.Game }, { "GameName", gameName(longestSession.Game) }, { "Hours", Math.Round(longestSession.Seconds / 3600.0, 2) },
                    { "Start", UsageText.Iso(longestSession.Start) }, { "Unit", inUnit == null ? string.Empty : inUnit.Display }
                };
            }

            // Exports JoyPro made, per game.
            root["Exports"] = store.Exports.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => (object)new Dictionary<string, object> { { "Game", g.Key }, { "Name", gameName(g.Key) }, { "Count", g.Count() }, { "Last", UsageText.Iso(g.Max(e => e.Value)) } }).ToList();

            profile = profile ?? new UsageProfileFacts();
            root["Profile"] = profile.ProfileName;
            root["Devices"] = profile.Devices.Cast<object>().ToList();
            root["OldestUnusedBind"] = profile.OldestUnusedBind;
            root["Growth"] = profile.Growth.Cast<object>().ToList();
            root["Facts"] = Facts(aggregate, ranking, store, gameName, games, units, sessions, longest, longestEnd).Cast<object>().ToList();
            root["Sources"] = UsageHarvester.LastSources(store).Cast<object>().ToList();
            return root;
        }

        private static bool IsExcludedGame(UsageRanking ranking, string game)
        {
            return ranking.Games.Any(g => g.Excluded && string.Equals(g.Game, game, StringComparison.OrdinalIgnoreCase));
        }

        private static DateTime ToLocal(DateTime utc)
        {
            try
            {
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local);
            }
            catch (ArgumentException)
            {
                return utc;
            }
        }

        // Fun facts, most interesting first. Each: Id and Values (strings; numbers invariant, dates yyyy-MM-dd).
        private static List<Dictionary<string, object>> Facts(UsageAggregate aggregate, UsageRanking ranking, UsageStore store, Func<string, string> gameName,
                                                             List<UsageGameStat> games, List<UsageUnitStat> units, List<UsageFlight> sessions, int longestStreak, DateTime streakEnd)
        {
            List<Dictionary<string, object>> facts = new List<Dictionary<string, object>>();
            Action<string, object[]> add = (id, values) => facts.Add(new Dictionary<string, object> { { "Id", id }, { "Values", values.Select(v => (object)Convert.ToString(v, CultureInfo.InvariantCulture)).ToList() } });
            Func<double, string> num = v => Math.Round(v, v < 10 ? 1 : 0).ToString(CultureInfo.InvariantCulture);

            // The top unit against a whole other class.
            UsageUnitStat top = units.FirstOrDefault(u => u.Hours > 0);
            if (top != null)
            {
                foreach (KeyValuePair<string, string> other in new[]
                         {
                             new KeyValuePair<string, string>("rotary_wing", "more-than-helicopters"),
                             new KeyValuePair<string, string>("fixed_wing", "more-than-planes"),
                             new KeyValuePair<string, string>("spaceship", "more-than-spaceships")
                         })
                {
                    if (top.Class == other.Key)
                        continue;
                    List<UsageUnitStat> others = units.Where(u => u.Class == other.Key && u.Hours > 0).ToList();
                    if (others.Count >= 2 && top.Hours > others.Sum(u => u.Hours))
                    {
                        add(other.Value, new object[] { top.Display, num(top.Hours), num(others.Sum(u => u.Hours)) });
                        break;
                    }
                }
            }
            double total = games.Sum(g => g.Hours);
            if (total >= 24)
                add("total-days", new object[] { num(total), num(total / 24.0) });
            UsageGameStat topGame = games.FirstOrDefault(g => g.Hours > 0);
            if (topGame != null && games.Count > 1)
                add("top-game", new object[] { topGame.Name, num(topGame.Hours), Math.Round(100 * topGame.Hours / Math.Max(0.01, total)).ToString(CultureInfo.InvariantCulture) });
            UsageFlight longest = sessions.Where(s => !s.DateOnly).OrderByDescending(s => s.Seconds).FirstOrDefault();
            if (longest != null && longest.Seconds >= 1800)
                add("longest-session", new object[] { num(longest.Seconds / 3600.0), gameName(longest.Game), UsageAggregate.LocalDay(longest.Start).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
            if (longestStreak >= 2)
                add("streak", new object[] { longestStreak, streakEnd.AddDays(1 - longestStreak).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), streakEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
            UsageUnitStat ship = units.FirstOrDefault(u => u.Class == "spaceship" && u.Hours > 0);
            if (ship != null && !ReferenceEquals(ship, top))
                add("favourite-ship", new object[] { ship.Display, num(ship.Hours) });
            double ly;
            UsageGameStat elite = games.FirstOrDefault(g => g.Counters.TryGetValue(UsageCounters.DistanceLy, out ly) && ly > 0);
            if (elite != null)
            {
                double distance = elite.Counters[UsageCounters.DistanceLy];
                // The Milky Way is about 100 000 light years across: worth saying from a tenth of it on.
                if (distance >= 10000)
                    add("distance", new object[] { Math.Round(distance).ToString(CultureInfo.InvariantCulture), (distance / 100000.0).ToString("0.##", CultureInfo.InvariantCulture) });
                else
                    add("distance-short", new object[] { Math.Round(distance).ToString(CultureInfo.InvariantCulture) });
            }
            UsageFlight mostKills = aggregate.Flights.Where(f => f.Counters.ContainsKey(UsageCounters.Kills)).OrderByDescending(f => f.Counters[UsageCounters.Kills]).FirstOrDefault();
            if (mostKills != null && mostKills.Counters[UsageCounters.Kills] >= 3)
                add("most-kills", new object[] { mostKills.Counters[UsageCounters.Kills], mostKills.Display, gameName(mostKills.Game) });
            double[] weekdays = new double[7];
            foreach (UsageFlight s in sessions)
                weekdays[((int)(s.DateOnly ? s.Start : ToLocal(s.Start)).DayOfWeek + 6) % 7] += s.Seconds;
            if (sessions.Count >= 5 && weekdays.Max() > 0)
                add("busiest-weekday", new object[] { Array.IndexOf(weekdays, weekdays.Max()) });
            int night = sessions.Count(s => !s.DateOnly && ToLocal(s.Start).Hour < 4);
            if (night >= 3)
                add("night-owl", new object[] { night });
            UsageFlight first = aggregate.Flights.Where(f => f.Seconds > 0 || f.DateOnly).OrderBy(f => f.Start).FirstOrDefault();
            if (first != null)
                add("first-flight", new object[] { UsageAggregate.LocalDay(first.Start).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), first.Display, gameName(first.Game) });
            int exports = store.Exports.Count;
            if (exports >= 3)
                add("exports", new object[] { exports });
            UsageGameStat steamAhead = games.Where(g => g.SteamHours >= 10 && g.LogHours > 0 && g.SteamHours > 4 * g.LogHours).OrderByDescending(g => g.SteamHours).FirstOrDefault();
            if (steamAhead != null)
                add("steam-only", new object[] { steamAhead.Name, num(steamAhead.SteamHours) });
            return facts;
        }

        public static string FactEnglish(string id, IList<string> v)
        {
            Func<int, string> at = i => v != null && i < v.Count ? v[i] : string.Empty;
            switch (id)
            {
                case "more-than-helicopters": return "You flew the " + at(0) + " more than all helicopters together (" + at(1) + " h vs " + at(2) + " h).";
                case "more-than-planes": return "You spent more time in the " + at(0) + " than in all airplanes together (" + at(1) + " h vs " + at(2) + " h).";
                case "more-than-spaceships": return "You flew the " + at(0) + " more than all spaceships together (" + at(1) + " h vs " + at(2) + " h).";
                case "total-days": return at(0) + " hours in total: that is " + at(1) + " full days.";
                case "top-game": return "Most played: " + at(0) + " with " + at(1) + " hours (" + at(2) + " % of your time).";
                case "longest-session": return "Your longest session: " + at(0) + " hours of " + at(1) + " on " + at(2) + ".";
                case "streak": return "Your longest streak: " + at(0) + " days in a row (" + at(1) + " to " + at(2) + ").";
                case "favourite-ship": return "Your favourite ship: the " + at(0) + " (" + at(1) + " h).";
                case "distance": return "You jumped " + at(0) + " light years, " + at(1) + " times across the Milky Way.";
                case "distance-short": return "You jumped " + at(0) + " light years in Elite Dangerous.";
                case "most-kills": return "Most kills in one flight: " + at(0) + " in the " + at(1) + " (" + at(2) + ").";
                case "busiest-weekday": return "Your busiest day of the week: " + CultureInfo.InvariantCulture.DateTimeFormat.GetDayName((DayOfWeek)((int.Parse(at(0), CultureInfo.InvariantCulture) + 1) % 7)) + ".";
                case "night-owl": return at(0) + " sessions started after midnight. Night owl!";
                case "first-flight": return "Your first recorded flight: " + at(0) + " in the " + at(1) + " (" + at(2) + ").";
                case "exports": return "JoyPro wrote your binds into your games " + at(0) + " times.";
                case "steam-only": return at(0) + ": " + at(1) + " hours on Steam, most of them from before JoyPro started counting.";
                default: return id + ": " + string.Join(", ", v ?? new List<string>());
            }
        }

        public static string ToText(Dictionary<string, object> stats)
        {
            StringBuilder sb = new StringBuilder();
            Dictionary<string, object> totals = stats["Totals"] as Dictionary<string, object>;
            sb.AppendLine("Usage statistics");
            sb.AppendLine("  Total: " + UsageText.Hours(Convert.ToDouble(totals["Hours"], CultureInfo.InvariantCulture)) + " in " + totals["Games"] + " game(s), " + totals["Sessions"] + " session(s), " + totals["ActiveDays"] + " active day(s)");
            sb.AppendLine("  Longest streak: " + totals["LongestStreakDays"] + " day(s); current streak: " + totals["CurrentStreakDays"] + " day(s)");
            sb.AppendLine("Games");
            foreach (Dictionary<string, object> g in ((List<object>)stats["Games"]).Cast<Dictionary<string, object>>())
                sb.AppendLine("  " + g["Rank"] + ". " + g["Name"] + ": " + UsageText.Hours(Convert.ToDouble(g["Hours"], CultureInfo.InvariantCulture)) + " (logs " + UsageText.Hours(Convert.ToDouble(g["LogHours"], CultureInfo.InvariantCulture)) +
                              ", Steam " + UsageText.Hours(Convert.ToDouble(g["SteamHours"], CultureInfo.InvariantCulture)) + "), " + g["Sessions"] + " session(s)");
            sb.AppendLine("Top aircraft, ships and vehicles");
            foreach (Dictionary<string, object> u in ((List<object>)stats["Units"]).Cast<Dictionary<string, object>>().Take(10))
                sb.AppendLine("  " + u["Rank"] + ". " + u["Name"] + " (" + u["GameName"] + "): " + UsageText.Hours(Convert.ToDouble(u["Hours"], CultureInfo.InvariantCulture)) + ", " + u["Sessions"] + " session(s)");
            foreach (Dictionary<string, object> c in ((List<object>)stats["Counters"]).Cast<Dictionary<string, object>>())
                sb.AppendLine("  " + c["Name"] + ": " + Convert.ToString(c["Total"], CultureInfo.InvariantCulture));
            List<object> facts = (List<object>)stats["Facts"];
            if (facts.Count > 0)
            {
                sb.AppendLine("Fun facts");
                foreach (Dictionary<string, object> f in facts.Cast<Dictionary<string, object>>())
                    sb.AppendLine("  - " + FactEnglish((string)f["Id"], ((List<object>)f["Values"]).Cast<string>().ToList()));
            }
            return sb.ToString().TrimEnd();
        }
    }
}
