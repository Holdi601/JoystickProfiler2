#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JoyPro.Usage
{
    // The usage summary store's records (docs/usage-statistics.md). Plain C# 7.3: compiled into the engine and linked
    // into the GUI tests. Times are UTC. Counters use the names of UsageCounters.
    public static class UsageCounters
    {
        public const string Landings = "landings";
        public const string Kills = "kills";
        public const string Deaths = "deaths";
        public const string Jumps = "jumps";
        // Elite Dangerous: light years jumped.
        public const string DistanceLy = "distanceLy";
        public const string TakeOffs = "takeoffs";

        public static readonly string[] All = { Landings, TakeOffs, Kills, Deaths, Jumps, DistanceLy };
    }

    // Where a record came from, and how much it is trusted when two sources describe the same flight (higher wins).
    public static class UsageSourceKinds
    {
        public const string DcsLog = "dcs-log";
        public const string DcsLogbook = "dcs-logbook";
        public const string DcsTrack = "dcs-track";
        public const string Tacview = "tacview";
        public const string BmsLogbook = "bms-logbook";
        public const string Il2MissionReport = "il2-mission-report";
        public const string XPlaneLogbook = "xplane-logbook";
        public const string XPlaneLog = "xplane-log";
        public const string WarThunderReplay = "warthunder-replay";
        public const string EliteJournal = "elite-journal";
        public const string StarCitizenLog = "sc-log";
        public const string X4Save = "x4-save";
        public const string SteamPlaytime = "steam";

        public static readonly string[] All =
        {
            DcsLog, DcsLogbook, DcsTrack, Tacview, BmsLogbook, Il2MissionReport, XPlaneLogbook, XPlaneLog, WarThunderReplay,
            EliteJournal, StarCitizenLog, X4Save, SteamPlaytime
        };

        // Tacview and tracks follow one flight closely; game logs cover the whole game session; a logbook line has a
        // date only.
        public static int Priority(string kind)
        {
            switch (kind)
            {
                case Tacview: return 40;
                case DcsTrack: return 35;
                case Il2MissionReport: return 35;
                case WarThunderReplay: return 30;
                case DcsLog:
                case StarCitizenLog:
                case EliteJournal:
                case XPlaneLog: return 20;
                case XPlaneLogbook: return 10;
                default: return 0;
            }
        }

        public static string Title(string kind)
        {
            switch (kind)
            {
                case DcsLog: return "DCS log (dcs.log)";
                case DcsLogbook: return "DCS logbook (logbook.lua)";
                case DcsTrack: return "DCS tracks (.trk)";
                case Tacview: return "Tacview recordings (.acmi)";
                case BmsLogbook: return "Falcon BMS pilot logbook (.lbk)";
                case Il2MissionReport: return "IL-2 mission text logs";
                case XPlaneLogbook: return "X-Plane logbook (X-Plane Pilot.txt)";
                case XPlaneLog: return "X-Plane log (Log.txt)";
                case WarThunderReplay: return "War Thunder replays (.wrpl)";
                case EliteJournal: return "Elite Dangerous journals";
                case StarCitizenLog: return "Star Citizen game logs (Game.log)";
                case X4Save: return "X4 savegames";
                case SteamPlaytime: return "Steam playtime (localconfig.vdf)";
                default: return kind;
            }
        }
    }

    // A part of a session in one aircraft, ship or vehicle.
    public sealed class UsageSegment
    {
        public string Unit = string.Empty;
        public string Display = string.Empty;
        public DateTime Start;
        public double Seconds;
        public Dictionary<string, double> Counters = new Dictionary<string, double>(StringComparer.Ordinal);

        public DateTime End
        {
            get { return Start.AddSeconds(Math.Max(0, Seconds)); }
        }

        public UsageSegment Clone()
        {
            return new UsageSegment { Unit = Unit, Display = Display, Start = Start, Seconds = Seconds, Counters = new Dictionary<string, double>(Counters, StringComparer.Ordinal) };
        }
    }

    // One game session (a game start to its end, one replay, one logbook line).
    public sealed class UsageSession
    {
        // Stable over re-reads of the same session (game, source family, start): the same session read again from the
        // live copy, the game's log or its backup replaces the record instead of adding one.
        public string Key = string.Empty;
        public string Game = string.Empty;
        public string Source = string.Empty;
        public DateTime Start;
        public double Seconds;
        // A logbook line: only the day is known (Start is that day, 00:00 UTC; Seconds the logged time).
        public bool DateOnly;
        public List<UsageSegment> Units = new List<UsageSegment>();
        public Dictionary<string, double> Counters = new Dictionary<string, double>(StringComparer.Ordinal);
        // The file it was read from (a name, for the statistics' "from" line; never a full path).
        public string From = string.Empty;

        public DateTime End
        {
            get { return Start.AddSeconds(Math.Max(0, Seconds)); }
        }

        public UsageSession Clone()
        {
            return new UsageSession
            {
                Key = Key, Game = Game, Source = Source, Start = Start, Seconds = Seconds, DateOnly = DateOnly, From = From,
                Units = Units.Select(u => u.Clone()).ToList(), Counters = new Dictionary<string, double>(Counters, StringComparer.Ordinal)
            };
        }

        // The more complete of two reads of one session: the longer one, at a tie the one with more events.
        public static UsageSession MoreComplete(UsageSession a, UsageSession b)
        {
            if (a == null)
                return b;
            if (b == null)
                return a;
            if (Math.Abs(a.Seconds - b.Seconds) > 0.5)
                return a.Seconds > b.Seconds ? a : b;
            double ea = a.Counters.Values.Sum() + a.Units.Count;
            double eb = b.Counters.Values.Sum() + b.Units.Count;
            return eb >= ea ? b : a;
        }

        public static string MakeKey(string game, string family, DateTime start, string extra = null)
        {
            return game + "|" + family + "|" + start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) +
                   (string.IsNullOrEmpty(extra) ? string.Empty : "|" + extra);
        }
    }

    // A running total a game keeps itself (Steam playtime, a logbook's flight hours): the newest value counts, and the
    // highest value ever seen stays when the game later shows less (a reset or a deleted logbook).
    public sealed class UsageTotal
    {
        public string Key = string.Empty;
        public string Game = string.Empty;
        // Empty: the whole game.
        public string Unit = string.Empty;
        public string Display = string.Empty;
        public string Source = string.Empty;
        public double Hours;
        public Dictionary<string, double> Counters = new Dictionary<string, double>(StringComparer.Ordinal);
        public DateTime LastPlayed;
        public DateTime Seen;
        public string From = string.Empty;

        public UsageTotal Clone()
        {
            return new UsageTotal
            {
                Key = Key, Game = Game, Unit = Unit, Display = Display, Source = Source, Hours = Hours, LastPlayed = LastPlayed, Seen = Seen, From = From,
                Counters = new Dictionary<string, double>(Counters, StringComparer.Ordinal)
            };
        }

        // The stored total after a new read: never lower than before (history survives a reset or a deleted file).
        public static UsageTotal Merge(UsageTotal stored, UsageTotal read)
        {
            if (stored == null)
                return read;
            if (read == null)
                return stored;
            UsageTotal merged = read.Clone();
            merged.Hours = Math.Max(stored.Hours, read.Hours);
            foreach (KeyValuePair<string, double> c in stored.Counters)
            {
                double value;
                merged.Counters[c.Key] = read.Counters.TryGetValue(c.Key, out value) ? Math.Max(value, c.Value) : c.Value;
            }
            if (stored.LastPlayed > merged.LastPlayed)
                merged.LastPlayed = stored.LastPlayed;
            if (string.IsNullOrEmpty(merged.Display))
                merged.Display = stored.Display;
            return merged;
        }
    }

    // What one parse of one file gave.
    public sealed class UsageParseResult
    {
        public List<UsageSession> Sessions = new List<UsageSession>();
        public List<UsageTotal> Totals = new List<UsageTotal>();
        public List<string> Notes = new List<string>();

        public bool IsEmpty
        {
            get { return Sessions.Count == 0 && Totals.Count == 0; }
        }
    }

    // The user's choices on the ranking: pinned units come first and are the style reference of their class,
    // excluded ones never count.
    public sealed class UsageOverrides
    {
        public HashSet<string> Pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // "<game>|<unit>", or "<game>|" for a whole game.
        public static string Id(string game, string unit)
        {
            return (game ?? string.Empty) + "|" + (unit ?? string.Empty);
        }
    }

    public static class UsageText
    {
        // "F-14B", "RSI_Polaris" -> "RSI Polaris", hours as "12.5 h".
        public static string Hours(double hours)
        {
            if (hours <= 0)
                return "0 h";
            if (hours < 1)
                return Math.Round(hours * 60).ToString(CultureInfo.InvariantCulture) + " min";
            return hours.ToString(hours < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " h";
        }

        public static string Iso(DateTime utc)
        {
            return utc == DateTime.MinValue ? string.Empty : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        public static DateTime ParseIso(string text)
        {
            DateTime value;
            if (!string.IsNullOrWhiteSpace(text) && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value))
                return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            return DateTime.MinValue;
        }

        public static void Add(Dictionary<string, double> counters, string name, double amount)
        {
            if (counters == null || string.IsNullOrEmpty(name) || amount == 0)
                return;
            double value;
            counters.TryGetValue(name, out value);
            counters[name] = value + amount;
        }
    }
}
