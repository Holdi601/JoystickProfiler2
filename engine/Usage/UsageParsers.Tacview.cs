#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Usage
{
    // Tacview recordings (.acmi text, or .zip.acmi with the text inside; format "text/acmi/tacview" 2.x): the game from
    // DataSource, the player's aircraft (the object whose Pilot is the recording's Author, else the only piloted
    // aircraft), its time in the recording, take-offs and landings (Event=TakenOff / Landed) and its destruction.
    public static class TacviewUsageParser
    {
        private sealed class TacObject
        {
            public string Id;
            public string Type = string.Empty;
            public string Name = string.Empty;
            public string Pilot = string.Empty;
            public double First = -1;
            public double Last = -1;
            public bool Removed;
        }

        public static UsageParseResult Parse(UsageInput input)
        {
            UsageParseResult result = new UsageParseResult();
            string text;
            byte[] head = input.Bytes(4);
            if (UsageInput.IsZip(head))
            {
                using (Stream stream = input.Open())
                    text = UsageInput.ZipEntryText(stream, n => n.EndsWith(".acmi", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                text = input.Text();
            }
            if (string.IsNullOrEmpty(text) || text.IndexOf("FileType=text/acmi/tacview", StringComparison.Ordinal) < 0)
            {
                result.Notes.Add(input.DisplayName + ": not a Tacview text recording");
                return result;
            }
            return ParseText(text, input.DisplayName, input.LastWriteUtc, input.Game);
        }

        public static UsageParseResult ParseText(string text, string from, DateTime fileTime, string gameHint)
        {
            UsageParseResult result = new UsageParseResult();
            string dataSource = string.Empty, author = string.Empty, referenceTime = string.Empty, recordingTime = string.Empty;
            Dictionary<string, TacObject> objects = new Dictionary<string, TacObject>(StringComparer.Ordinal);
            List<KeyValuePair<string, string>> events = new List<KeyValuePair<string, string>>();
            List<double> eventTimes = new List<double>();
            double now = 0, end = 0;
            StringBuilder pending = null;
            using (StringReader reader = new StringReader(text))
            {
                string raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    // A line ending in a backslash continues on the next one.
                    if (raw.EndsWith("\\", StringComparison.Ordinal))
                    {
                        (pending ?? (pending = new StringBuilder())).Append(raw, 0, raw.Length - 1).Append('\n');
                        continue;
                    }
                    string line = raw;
                    if (pending != null)
                    {
                        line = pending.Append(raw).ToString();
                        pending = null;
                    }
                    if (line.Length == 0 || line[0] == '/')
                        continue;
                    if (line[0] == '#')
                    {
                        double t;
                        if (UsageInput.TryDouble(line.Substring(1), out t))
                        {
                            now = t;
                            if (t > end)
                                end = t;
                        }
                        continue;
                    }
                    if (line[0] == '-')
                    {
                        TacObject gone;
                        if (objects.TryGetValue(line.Substring(1).Trim(), out gone))
                        {
                            gone.Removed = true;
                            gone.Last = now;
                        }
                        continue;
                    }
                    int comma = line.IndexOf(',');
                    if (comma <= 0)
                        continue;
                    string id = line.Substring(0, comma);
                    string props = line.Substring(comma + 1);
                    if (id == "0")
                    {
                        string value;
                        if (Prop(props, "DataSource", out value)) dataSource = value;
                        if (Prop(props, "Author", out value)) author = value;
                        if (Prop(props, "ReferenceTime", out value)) referenceTime = value;
                        if (Prop(props, "RecordingTime", out value)) recordingTime = value;
                        if (Prop(props, "Event", out value))
                        {
                            events.Add(new KeyValuePair<string, string>(value, string.Empty));
                            eventTimes.Add(now);
                        }
                        continue;
                    }
                    TacObject obj;
                    if (!objects.TryGetValue(id, out obj))
                    {
                        obj = new TacObject { Id = id, First = now };
                        objects[id] = obj;
                    }
                    obj.Last = now;
                    string v;
                    if (Prop(props, "Type", out v)) obj.Type = v;
                    if (Prop(props, "Name", out v)) obj.Name = v;
                    if (Prop(props, "Pilot", out v)) obj.Pilot = v;
                }
            }
            string game = GameOf(dataSource);
            if (game.Length == 0)
                game = gameHint ?? string.Empty;
            if (game.Length == 0)
            {
                result.Notes.Add(from + ": unknown game (" + (dataSource.Length == 0 ? "no DataSource" : dataSource) + ")");
                return result;
            }
            DateTime start = UsageText.ParseIso(referenceTime);
            if (start == DateTime.MinValue)
                start = UsageText.ParseIso(recordingTime);
            if (start == DateTime.MinValue)
                start = fileTime.AddSeconds(-end);

            List<TacObject> aircraft = objects.Values.Where(o => o.Type.IndexOf("Air", StringComparison.OrdinalIgnoreCase) >= 0 && o.Pilot.Length > 0 && o.Name.Length > 0).ToList();
            TacObject player = author.Length > 0 ? aircraft.FirstOrDefault(o => string.Equals(o.Pilot, author, StringComparison.OrdinalIgnoreCase)) : null;
            if (player == null && aircraft.Count == 1)
                player = aircraft[0];

            UsageSession session = new UsageSession
            {
                Game = game,
                Source = UsageSourceKinds.Tacview,
                Start = start,
                Seconds = end,
                From = from
            };
            if (player != null)
            {
                double last = player.Removed ? player.Last : end;
                UsageSegment segment = new UsageSegment { Unit = player.Name, Display = player.Name, Start = start.AddSeconds(Math.Max(0, player.First)), Seconds = Math.Max(0, last - Math.Max(0, player.First)) };
                for (int i = 0; i < events.Count; i++)
                {
                    string[] parts = events[i].Key.Split('|');
                    if (parts.Length < 2 || parts[1] != player.Id)
                        continue;
                    if (string.Equals(parts[0], "Landed", StringComparison.OrdinalIgnoreCase))
                        UsageText.Add(segment.Counters, UsageCounters.Landings, 1);
                    else if (string.Equals(parts[0], "TakenOff", StringComparison.OrdinalIgnoreCase))
                        UsageText.Add(segment.Counters, UsageCounters.TakeOffs, 1);
                    else if (string.Equals(parts[0], "Destroyed", StringComparison.OrdinalIgnoreCase))
                        UsageText.Add(segment.Counters, UsageCounters.Deaths, 1);
                }
                foreach (KeyValuePair<string, double> c in segment.Counters)
                    UsageText.Add(session.Counters, c.Key, c.Value);
                session.Units.Add(segment);
            }
            else
            {
                result.Notes.Add(from + ": the player's aircraft is not marked (no Author / Pilot match)");
            }
            session.Key = UsageSession.MakeKey(game, "tacview", start, player == null ? null : player.Name);
            result.Sessions.Add(session);
            return result;
        }

        // "DCS 2.9.0", "Falcon 4.0 (BMS 4.37)", "IL-2 Sturmovik", "X-Plane 12", "Microsoft Flight Simulator"
        public static string GameOf(string dataSource)
        {
            string s = (dataSource ?? string.Empty).Trim();
            if (s.Length == 0)
                return string.Empty;
            if (s.StartsWith("DCS", StringComparison.OrdinalIgnoreCase))
                return "DCS";
            if (s.IndexOf("BMS", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("Falcon", StringComparison.OrdinalIgnoreCase) >= 0)
                return "FalconBMS";
            if (s.IndexOf("Korea", StringComparison.OrdinalIgnoreCase) >= 0 && s.IndexOf("IL", StringComparison.OrdinalIgnoreCase) >= 0)
                return "IL2Korea";
            if (s.IndexOf("IL-2", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("IL2", StringComparison.OrdinalIgnoreCase) >= 0)
                return "IL2Game";
            if (s.IndexOf("X-Plane 11", StringComparison.OrdinalIgnoreCase) >= 0)
                return "XPlane11";
            if (s.IndexOf("X-Plane", StringComparison.OrdinalIgnoreCase) >= 0)
                return "XPlane12";
            if (s.IndexOf("2024", StringComparison.OrdinalIgnoreCase) >= 0 && s.IndexOf("Flight Simulator", StringComparison.OrdinalIgnoreCase) >= 0)
                return "MSFS2024";
            if (s.IndexOf("Flight Simulator", StringComparison.OrdinalIgnoreCase) >= 0 || s.StartsWith("MSFS", StringComparison.OrdinalIgnoreCase))
                return "MSFS2020";
            if (s.IndexOf("War Thunder", StringComparison.OrdinalIgnoreCase) >= 0)
                return "WarThunder";
            return string.Empty;
        }

        // One property of an object line: Name=Value pairs separated by commas (a comma inside a value is "\,").
        private static bool Prop(string props, string name, out string value)
        {
            value = string.Empty;
            int start = 0;
            while (start < props.Length)
            {
                int end = start;
                while (end < props.Length && !(props[end] == ',' && (end == 0 || props[end - 1] != '\\')))
                    end++;
                int eq = props.IndexOf('=', start);
                if (eq > start && eq < end && end - start > name.Length && string.CompareOrdinal(props, start, name, 0, name.Length) == 0 && eq == start + name.Length)
                {
                    value = props.Substring(eq + 1, end - eq - 1).Replace("\\,", ",").Trim();
                    return true;
                }
                start = end + 1;
            }
            return false;
        }
    }
}
