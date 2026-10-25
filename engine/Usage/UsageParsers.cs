#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.Usage
{
    // Every parser by its source kind. Parsers never throw for a file they do not understand: they return what they
    // could read plus a note.
    public static class UsageParsers
    {
        public static UsageParseResult Parse(UsageInput input)
        {
            try
            {
                switch (input.Kind)
                {
                    case UsageSourceKinds.DcsLog: return DcsUsageParsers.ParseLog(input);
                    case UsageSourceKinds.DcsLogbook: return DcsUsageParsers.ParseLogbook(input);
                    case UsageSourceKinds.DcsTrack: return DcsUsageParsers.ParseTrack(input);
                    case UsageSourceKinds.Tacview: return TacviewUsageParser.Parse(input);
                    case UsageSourceKinds.BmsLogbook: return BmsUsageParser.Parse(input);
                    case UsageSourceKinds.Il2MissionReport: return Il2UsageParser.Parse(input);
                    case UsageSourceKinds.XPlaneLogbook: return XPlaneUsageParsers.ParseLogbook(input);
                    case UsageSourceKinds.XPlaneLog: return XPlaneUsageParsers.ParseLog(input);
                    case UsageSourceKinds.WarThunderReplay: return WarThunderUsageParser.Parse(input);
                    case UsageSourceKinds.EliteJournal: return EliteUsageParser.Parse(input);
                    case UsageSourceKinds.StarCitizenLog: return StarCitizenUsageParser.Parse(input);
                    case UsageSourceKinds.X4Save: return X4UsageParser.Parse(input);
                    default:
                        UsageParseResult unknown = new UsageParseResult();
                        unknown.Notes.Add("No reader for " + input.Kind);
                        return unknown;
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is FormatException ||
                                       ex is System.IO.InvalidDataException || ex is ArgumentException || ex is InvalidOperationException ||
                                       ex is OverflowException || ex is IndexOutOfRangeException)
            {
                UsageParseResult failed = new UsageParseResult();
                failed.Notes.Add(input.DisplayName + ": " + ex.Message);
                return failed;
            }
        }
    }

    // Builds a session from a timeline: unit switches open segments, counters go to the session and the open segment.
    internal sealed class UsageTimeline
    {
        private readonly UsageSession session;
        private UsageSegment open;
        private DateTime last;

        public UsageTimeline(string game, string source, DateTime start, string from)
        {
            session = new UsageSession { Game = game, Source = source, Start = start, From = from };
            last = start;
        }

        public UsageSession Session
        {
            get { return session; }
        }

        public UsageSegment Open
        {
            get { return open; }
        }

        public DateTime Last
        {
            get { return last; }
        }

        public void Seen(DateTime time)
        {
            if (time > last)
                last = time;
        }

        public void Enter(string unit, string display, DateTime time)
        {
            Seen(time);
            if (open != null && string.Equals(open.Unit, unit, StringComparison.OrdinalIgnoreCase))
                return;
            Leave(time);
            open = new UsageSegment { Unit = unit, Display = string.IsNullOrEmpty(display) ? unit : display, Start = time };
        }

        public void Leave(DateTime time)
        {
            Seen(time);
            if (open == null)
                return;
            open.Seconds = Math.Max(0, (time - open.Start).TotalSeconds);
            Add(open);
            open = null;
        }

        public void Count(string counter, double amount = 1)
        {
            UsageText.Add(session.Counters, counter, amount);
            if (open != null)
                UsageText.Add(open.Counters, counter, amount);
        }

        // Ends the session at the last time seen; segments of the same unit merge into one per unit visit order.
        public UsageSession Finish(DateTime? end = null)
        {
            DateTime stop = end ?? last;
            if (stop < last)
                stop = last;
            Leave(stop);
            session.Seconds = Math.Max(0, (stop - session.Start).TotalSeconds);
            return session;
        }

        private void Add(UsageSegment segment)
        {
            // A segment shorter than 5 s (a control token handed over at once) adds nothing but noise.
            if (segment.Seconds < 5 && segment.Counters.Count == 0)
                return;
            session.Units.Add(segment);
        }
    }
}
