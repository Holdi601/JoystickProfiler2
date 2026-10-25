#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Usage
{
    // DCS World: dcs.log (each game start, the aircraft the player spawned in), MissionEditor\logbook.lua (the pilot's
    // totals) and tracks (.trk: the player's aircraft of a recorded mission).
    public static class DcsUsageParsers
    {
        public const string Game = "DCS";

        // "=== Log opened UTC 2026-10-24 12:44:35"
        private static readonly Regex Opened = new Regex(@"^=== Log opened UTC (\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})", RegexOptions.Compiled);
        // "2026-10-24 12:44:35.033 INFO    ..." (UTC, like the header)
        private static readonly Regex Stamp = new Regex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})(?:\.\d+)?\s", RegexOptions.Compiled);
        // "MissionSpawn:spawnLocalPlayer 16777472,F-16C_50" (the player takes a seat)
        private static readonly Regex Spawn = new Regex(@"spawnLocalPlayer\s+\d+\s*,\s*([^\s,]+)", RegexOptions.Compiled);
        // "(dDispatcher)enterToState_:3" back in the menu, 5 = the game ends
        private static readonly Regex State = new Regex(@"\(dDispatcher\)enterToState_:(\d+)", RegexOptions.Compiled);

        public static UsageParseResult ParseLog(UsageInput input)
        {
            UsageParseResult result = new UsageParseResult();
            UsageTimeline timeline = null;
            foreach (string line in input.Lines())
            {
                if (line.Length == 0)
                    continue;
                if (line[0] == '=')
                {
                    Match opened = Opened.Match(line);
                    if (opened.Success)
                    {
                        if (timeline != null)
                            result.Sessions.Add(Close(timeline));
                        timeline = new UsageTimeline(Game, UsageSourceKinds.DcsLog, ParseUtc(opened.Groups[1].Value), input.DisplayName);
                    }
                    continue;
                }
                if (line[0] < '0' || line[0] > '9')
                    continue;
                Match stamp = Stamp.Match(line);
                if (!stamp.Success)
                    continue;
                DateTime time = ParseUtc(stamp.Groups[1].Value);
                if (timeline == null)
                    timeline = new UsageTimeline(Game, UsageSourceKinds.DcsLog, time, input.DisplayName);
                timeline.Seen(time);
                if (line.IndexOf("spawnLocalPlayer", StringComparison.Ordinal) >= 0)
                {
                    Match spawn = Spawn.Match(line);
                    if (spawn.Success)
                        timeline.Enter(spawn.Groups[1].Value, spawn.Groups[1].Value, time);
                    continue;
                }
                if (line.IndexOf("enterToState_", StringComparison.Ordinal) >= 0)
                {
                    Match state = State.Match(line);
                    if (state.Success && (state.Groups[1].Value == "3" || state.Groups[1].Value == "5"))
                        timeline.Leave(time);
                }
            }
            if (timeline != null)
                result.Sessions.Add(Close(timeline));
            return result;
        }

        private static UsageSession Close(UsageTimeline timeline)
        {
            UsageSession session = timeline.Finish();
            session.Key = UsageSession.MakeKey(Game, "dcs-log", session.Start);
            return session;
        }

        private static DateTime ParseUtc(string text)
        {
            return DateTime.SpecifyKind(DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc);
        }

        // logbook.lua: the totals of every pilot (flight hours, landings, kills, deaths, ejections). DCS keeps them per
        // pilot, not per aircraft, so they count for the game.
        private static readonly Regex LuaNumber = new Regex(@"\[""(\w+)""\]\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
        private static readonly Regex LuaString = new Regex(@"\[""(name|callsign)""\]\s*=\s*""([^""]*)""", RegexOptions.Compiled);

        public static UsageParseResult ParseLogbook(UsageInput input)
        {
            UsageParseResult result = new UsageParseResult();
            string text = input.Text();
            int search = 0;
            while (true)
            {
                int stats = text.IndexOf("[\"statistics\"]", search, StringComparison.Ordinal);
                if (stats < 0)
                    break;
                int open = text.IndexOf('{', stats);
                int close = open < 0 ? -1 : MatchingBrace(text, open);
                if (close < 0)
                    break;
                string block = text.Substring(open, close - open + 1);
                search = close;
                // The pilot's name: the nearest ["name"] before the statistics (same player table).
                string pilot = "pilot";
                foreach (Match m in LuaString.Matches(text.Substring(Math.Max(0, stats - 4000), Math.Min(4000, stats))))
                    if (m.Groups[1].Value == "name")
                        pilot = m.Groups[2].Value;
                Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in LuaNumber.Matches(block))
                {
                    double v;
                    if (UsageInput.TryDouble(m.Groups[2].Value, out v) && !values.ContainsKey(m.Groups[1].Value))
                        values[m.Groups[1].Value] = v;
                }
                double hours = Value(values, "flightHours");
                // Seconds in some versions (a pilot does not fly 100 000 hours).
                if (hours > 100000)
                    hours /= 3600.0;
                UsageTotal total = new UsageTotal
                {
                    Key = Game + "|" + UsageSourceKinds.DcsLogbook + "|" + pilot,
                    Game = Game,
                    Source = UsageSourceKinds.DcsLogbook,
                    Hours = hours,
                    LastPlayed = input.LastWriteUtc,
                    Seen = DateTime.UtcNow,
                    From = input.DisplayName
                };
                Put(total.Counters, UsageCounters.Landings, Value(values, "landings"));
                Put(total.Counters, UsageCounters.Kills, Value(values, "aaKills") + Value(values, "agKills") + Value(values, "static") + Value(values, "naval"));
                Put(total.Counters, UsageCounters.Deaths, Value(values, "deaths") + Value(values, "ejections"));
                if (total.Hours > 0 || total.Counters.Count > 0)
                    result.Totals.Add(total);
                else
                    result.Notes.Add("logbook.lua of " + pilot + " holds no flights yet");
            }
            return result;
        }

        private static void Put(Dictionary<string, double> counters, string name, double value)
        {
            if (value > 0)
                counters[name] = value;
        }

        private static double Value(Dictionary<string, double> values, string key)
        {
            double v;
            return values.TryGetValue(key, out v) ? v : 0;
        }

        internal static int MatchingBrace(string text, int open)
        {
            int depth = 0;
            bool inString = false;
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (c == '\\')
                        i++;
                    else if (c == '"')
                        inString = false;
                    continue;
                }
                if (c == '"')
                    inString = true;
                else if (c == '{')
                    depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }
            return -1;
        }

        // A track (.trk, a zip): the mission file names the player's aircraft (["skill"] = "Player" or "Client" in its
        // unit table). The recording time is the file's time; the length is not kept in a form JoyPro reads, so a track
        // counts as a flight without hours.
        private static readonly Regex UnitType = new Regex(@"\[""type""\]\s*=\s*""([^""]+)""", RegexOptions.Compiled);

        public static UsageParseResult ParseTrack(UsageInput input)
        {
            UsageParseResult result = new UsageParseResult();
            string mission;
            using (Stream stream = input.Open())
                mission = UsageInput.ZipEntryText(stream, n => string.Equals(n, "mission", StringComparison.OrdinalIgnoreCase));
            if (mission == null)
            {
                result.Notes.Add(input.DisplayName + ": no mission inside");
                return result;
            }
            string unit = PlayerUnit(mission);
            if (unit.Length == 0)
            {
                result.Notes.Add(input.DisplayName + ": no player aircraft found");
                return result;
            }
            DateTime when = input.LastWriteUtc;
            UsageSession session = new UsageSession
            {
                Key = UsageSession.MakeKey(Game, "dcs-track", when, Path.GetFileNameWithoutExtension(input.DisplayName)),
                Game = Game,
                Source = UsageSourceKinds.DcsTrack,
                Start = when,
                From = input.DisplayName
            };
            session.Units.Add(new UsageSegment { Unit = unit, Display = unit, Start = when });
            result.Sessions.Add(session);
            return result;
        }

        internal static string PlayerUnit(string mission)
        {
            foreach (string skill in new[] { "\"Player\"", "\"Client\"" })
            {
                int at = mission.IndexOf("[\"skill\"] = " + skill, StringComparison.Ordinal);
                if (at < 0)
                    continue;
                // The unit table around the skill: from its opening brace to its closing one.
                int open = at;
                int depth = 0;
                while (open > 0)
                {
                    char c = mission[open];
                    if (c == '}')
                        depth++;
                    else if (c == '{')
                    {
                        if (depth == 0)
                            break;
                        depth--;
                    }
                    open--;
                }
                int close = MatchingBrace(mission, open);
                if (close < 0)
                    continue;
                string table = mission.Substring(open, close - open + 1);
                Match type = UnitType.Match(table);
                if (type.Success)
                    return type.Groups[1].Value;
            }
            return string.Empty;
        }
    }
}
