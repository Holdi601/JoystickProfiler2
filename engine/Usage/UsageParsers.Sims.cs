#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Usage
{
    // Falcon BMS pilot logbook (<install>\User\Config\<pilot>.lbk): Falcon 4's LB_PILOT record, XOR-scrambled with a
    // 42-byte key and chained over the plain bytes (each byte is also XORed with every plain byte before it).
    // Read: name, callsign, flight hours (float at 0x5C), dogfight kills/deaths and campaign missions/kills/deaths.
    // Checked: the decoded name or callsign is the file's name, else the file counts as unreadable.
    public static class BmsUsageParser
    {
        public const string Game = "FalconBMS";

        private static readonly byte[] Key =
        {
            0x1E, 0x7F, 0x13, 0x70, 0x1F, 0x71, 0x51, 0x38, 0x4B, 0x6B, 0x12, 0x7D, 0x08, 0x7A, 0x5A, 0x17, 0x76, 0x05, 0x71, 0x14, 0x66,
            0x20, 0x41, 0x2D, 0x4E, 0x21, 0x4F, 0x6F, 0x06, 0x75, 0x55, 0x2C, 0x43, 0x36, 0x44, 0x64, 0x29, 0x48, 0x3B, 0x4F, 0x2A, 0x58
        };

        public static byte[] Decode(byte[] data)
        {
            byte[] plain = new byte[data.Length];
            byte previous = 0;
            for (int i = 0; i < data.Length; i++)
            {
                byte running = (byte)(data[i] ^ Key[i % Key.Length]);
                plain[i] = (byte)(running ^ previous);
                previous = running;
            }
            return plain;
        }

        // The inverse (the tests write their logbooks with it).
        public static byte[] Encode(byte[] plain)
        {
            byte[] data = new byte[plain.Length];
            byte running = 0;
            for (int i = 0; i < plain.Length; i++)
            {
                running ^= plain[i];
                data[i] = (byte)(running ^ Key[i % Key.Length]);
            }
            return data;
        }

        public static UsageParseResult Parse(UsageInput input)
        {
            UsageParseResult result = new UsageParseResult();
            byte[] data = input.Bytes(64 * 1024);
            if (data.Length < 0xA0)
            {
                result.Notes.Add(input.DisplayName + ": too short for a pilot logbook");
                return result;
            }
            byte[] plain = Decode(data);
            string name = CString(plain, 0, 21);
            string callsign = CString(plain, 0x15, 13);
            string file = Path.GetFileNameWithoutExtension(input.DisplayName);
            if (!string.Equals(name, file, StringComparison.OrdinalIgnoreCase) && !string.Equals(callsign, file, StringComparison.OrdinalIgnoreCase))
            {
                result.Notes.Add(input.DisplayName + ": not readable as a BMS pilot logbook of this version");
                return result;
            }
            float hours = BitConverter.ToSingle(plain, 0x5C);
            if (float.IsNaN(hours) || float.IsInfinity(hours) || hours < 0 || hours > 100000)
                hours = 0;
            int dfKills = Short(plain, 0x70), dfKilled = Short(plain, 0x72);
            int missions = Short(plain, 0x7E), campKills = Short(plain, 0x8A), campKilled = Short(plain, 0x8C);
            UsageTotal total = new UsageTotal
            {
                Key = Game + "|" + UsageSourceKinds.BmsLogbook + "|" + (callsign.Length > 0 ? callsign : name),
                Game = Game,
                Unit = "F-16",
                Display = "F-16",
                Source = UsageSourceKinds.BmsLogbook,
                Hours = hours,
                LastPlayed = input.LastWriteUtc,
                Seen = DateTime.UtcNow,
                From = input.DisplayName
            };
            if (dfKills + campKills > 0)
                total.Counters[UsageCounters.Kills] = dfKills + campKills;
            if (dfKilled + campKilled > 0)
                total.Counters[UsageCounters.Deaths] = dfKilled + campKilled;
            if (missions > 0)
                total.Counters["missions"] = missions;
            if (total.Hours > 0 || total.Counters.Count > 0)
                result.Totals.Add(total);
            else
                result.Notes.Add("pilot " + (callsign.Length > 0 ? callsign : name) + " has no flight hours yet");
            return result;
        }

        private static int Short(byte[] data, int offset)
        {
            if (offset + 2 > data.Length)
                return 0;
            int v = BitConverter.ToInt16(data, offset);
            return v < 0 || v > 60000 ? 0 : v;
        }

        private static string CString(byte[] data, int offset, int length)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = offset; i < offset + length && i < data.Length; i++)
            {
                if (data[i] == 0)
                    break;
                if (data[i] < 32 || data[i] > 126)
                    return string.Empty;
                sb.Append((char)data[i]);
            }
            return sb.ToString().Trim();
        }
    }

    // IL-2 Great Battles / Korea mission text logs (data\missionReports\missionReport(<local time>)[n].txt, written when
    // startup.cfg has mission_text_log = 1): one mission = all files of one time stamp. AType:10 = the player's plane
    // (ISPL:1 when present), AType:3 kills (AID/TID), AType:5 take-off, AType:6 landing; T is in 1/50 s.
    public static class Il2UsageParser
    {
        private static readonly Regex FileStamp = new Regex(@"missionReport\((\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Tick = new Regex(@"^T:(\d+)\s+AType:(\d+)\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex Field = new Regex(@"(?:^|\s)(PLID|PID|AID|TID|ISPL|BOTID|PARENTID):(-?\d+)", RegexOptions.Compiled);
        private static readonly Regex TypeField = new Regex(@"TYPE:(.+?)\s+COUNTRY:", RegexOptions.Compiled);

        public static UsageParseResult Parse(UsageInput input)
        {
            UsageParseResult result = new UsageParseResult();
            Match stamp = FileStamp.Match(input.DisplayName);
            DateTime start = stamp.Success
                ? UsageInput.LocalToUtc(DateTime.ParseExact(stamp.Groups[1].Value, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture))
                : input.LastWriteUtc;
            // The other parts of the same mission ([0], [1], ...) lie next to it: read them together.
            List<string> parts = new List<string> { input.Path };
            string folder = Path.GetDirectoryName(input.Path);
            if (stamp.Success && folder != null && Directory.Exists(folder))
            {
                parts = Directory.GetFiles(folder, "missionReport(" + stamp.Groups[1].Value + ")*.txt")
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                if (parts.Count == 0)
                    parts.Add(input.Path);
            }
            string game = input.Game.Length > 0 ? input.Game : "IL2Game";
            string planeId = string.Empty, planeType = string.Empty;
            double lastTick = 0, spawnTick = -1, endTick = -1;
            UsageSegment segment = null;
            Dictionary<string, double> counters = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (string part in parts)
            {
                foreach (string line in new UsageInput(part, game, input.Kind).Lines())
                {
                    Match m = Tick.Match(line);
                    if (!m.Success)
                        continue;
                    double tick = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (tick > lastTick)
                        lastTick = tick;
                    string type = m.Groups[2].Value;
                    string rest = m.Groups[3].Value;
                    Dictionary<string, string> f = Fields(rest);
                    switch (type)
                    {
                        case "10":
                        {
                            string isPlayer;
                            bool player = !f.TryGetValue("ISPL", out isPlayer) || isPlayer == "1";
                            if (!player || planeId.Length > 0)
                                break;
                            string id;
                            if (!f.TryGetValue("PLID", out id))
                                break;
                            Match t = TypeField.Match(rest);
                            planeId = id;
                            planeType = t.Success ? t.Groups[1].Value.Trim() : "plane";
                            spawnTick = tick;
                            break;
                        }
                        case "3":
                        {
                            string aid, tid;
                            f.TryGetValue("AID", out aid);
                            f.TryGetValue("TID", out tid);
                            if (planeId.Length > 0 && aid == planeId && tid != planeId)
                                UsageText.Add(counters, UsageCounters.Kills, 1);
                            else if (planeId.Length > 0 && tid == planeId)
                                UsageText.Add(counters, UsageCounters.Deaths, 1);
                            break;
                        }
                        case "5":
                        case "6":
                        {
                            string pid;
                            if (planeId.Length > 0 && f.TryGetValue("PID", out pid) && pid == planeId)
                                UsageText.Add(counters, type == "5" ? UsageCounters.TakeOffs : UsageCounters.Landings, 1);
                            break;
                        }
                        case "4":
                        {
                            string plid;
                            if (planeId.Length > 0 && endTick < 0 && f.TryGetValue("PLID", out plid) && plid == planeId)
                                endTick = tick;
                            break;
                        }
                    }
                }
            }
            UsageSession session = new UsageSession
            {
                Key = UsageSession.MakeKey(game, "il2-mission", start),
                Game = game,
                Source = UsageSourceKinds.Il2MissionReport,
                Start = start,
                Seconds = lastTick / 50.0,
                From = Path.GetFileName(parts[0])
            };
            if (planeId.Length > 0)
            {
                double stop = endTick >= 0 ? endTick : lastTick;
                segment = new UsageSegment { Unit = planeType, Display = planeType, Start = start.AddSeconds(spawnTick / 50.0), Seconds = Math.Max(0, (stop - spawnTick) / 50.0), Counters = counters };
                session.Units.Add(segment);
                foreach (KeyValuePair<string, double> c in counters)
                    session.Counters[c.Key] = c.Value;
            }
            result.Sessions.Add(session);
            return result;
        }

        private static Dictionary<string, string> Fields(string rest)
        {
            Dictionary<string, string> f = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Field.Matches(rest))
                if (!f.ContainsKey(m.Groups[1].Value))
                    f[m.Groups[1].Value] = m.Groups[2].Value;
            return f;
        }
    }

    // X-Plane: Output\logbooks\X-Plane Pilot.txt (one line per flight: "2 <yymmdd> <from> <to> <landings> <hours> <cross
    // country> <IFR> <night> <tail> <aircraft>") and Log.txt (rewritten at every start: "X-Plane Started on <local
    // time>", "Loading airplane number 0 with Aircraft/.../<name>.acf" with the time since the start in front).
    public static class XPlaneUsageParsers
    {
        private static readonly Regex Started = new Regex(@"X-Plane Started on \w+ (\w+)\s+(\d+) (\d{2}:\d{2}:\d{2}) (\d{4})", RegexOptions.Compiled);
        private static readonly Regex Since = new Regex(@"^(\d+):(\d{2}):(\d{2}(?:\.\d+)?)\s", RegexOptions.Compiled);
        private static readonly Regex Airplane = new Regex(@"Loading airplane number 0 with (.+\.acf)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static UsageParseResult ParseLogbook(UsageInput input)
        {
            UsageParseResult result = new UsageParseResult();
            string game = input.Game.Length > 0 ? input.Game : "XPlane12";
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string raw in input.Lines())
            {
                string line = raw.Trim();
                if (!line.StartsWith("2 ", StringComparison.Ordinal))
                    continue;
                string[] t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 10)
                    continue;
                DateTime day;
                if (!DateTime.TryParseExact(t[1], new[] { "yyMMdd", "yyyy-MM-dd", "yyyyMMdd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
                    continue;
                int landings;
                double hours;
                if (!int.TryParse(t[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out landings) || !UsageInput.TryDouble(t[5], out hours))
                    continue;
                string aircraft = t.Length > 10 ? string.Join(" ", t.Skip(10)) : t[9];
                int n;
                seen.TryGetValue(line, out n);
                seen[line] = n + 1;
                DateTime start = DateTime.SpecifyKind(day.Date, DateTimeKind.Utc);
                UsageSession session = new UsageSession
                {
                    Key = UsageSession.MakeKey(game, "xplane-logbook", start, Hash(line) + "-" + n.ToString(CultureInfo.InvariantCulture)),
                    Game = game,
                    Source = UsageSourceKinds.XPlaneLogbook,
                    Start = start,
                    Seconds = hours * 3600,
                    DateOnly = true,
                    From = input.DisplayName
                };
                UsageSegment segment = new UsageSegment { Unit = aircraft, Display = aircraft, Start = start, Seconds = hours * 3600 };
                if (landings > 0)
                {
                    segment.Counters[UsageCounters.Landings] = landings;
                    session.Counters[UsageCounters.Landings] = landings;
                }
                session.Units.Add(segment);
                result.Sessions.Add(session);
            }
            return result;
        }

        public static UsageParseResult ParseLog(UsageInput input)
        {
            UsageParseResult result = new UsageParseResult();
            string game = input.Game.Length > 0 ? input.Game : "XPlane12";
            UsageTimeline timeline = null;
            DateTime start = DateTime.MinValue;
            foreach (string line in input.Lines())
            {
                if (timeline == null)
                {
                    Match started = Started.Match(line);
                    if (started.Success)
                    {
                        DateTime local;
                        string text = started.Groups[1].Value + " " + started.Groups[2].Value.PadLeft(2, '0') + " " + started.Groups[4].Value + " " + started.Groups[3].Value;
                        if (DateTime.TryParseExact(text, "MMM dd yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
                        {
                            start = UsageInput.LocalToUtc(local);
                            timeline = new UsageTimeline(game, UsageSourceKinds.XPlaneLog, start, input.DisplayName);
                        }
                    }
                    continue;
                }
                Match since = Since.Match(line);
                if (!since.Success)
                    continue;
                double seconds = int.Parse(since.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(since.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
                                 double.Parse(since.Groups[3].Value, CultureInfo.InvariantCulture);
                DateTime time = start.AddSeconds(seconds);
                timeline.Seen(time);
                if (line.IndexOf("Loading airplane number 0", StringComparison.Ordinal) >= 0)
                {
                    Match plane = Airplane.Match(line);
                    if (plane.Success)
                    {
                        string name = Path.GetFileNameWithoutExtension(plane.Groups[1].Value.Replace('/', '\\'));
                        timeline.Enter(name, name, time);
                    }
                }
            }
            if (timeline == null)
            {
                result.Notes.Add(input.DisplayName + ": no start time found");
                return result;
            }
            UsageSession session = timeline.Finish();
            session.Key = UsageSession.MakeKey(game, "xplane-log", session.Start);
            result.Sessions.Add(session);
            return result;
        }

        internal static string Hash(string text)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in text)
                    h = (h ^ c) * 16777619;
                return h.ToString("x8", CultureInfo.InvariantCulture);
            }
        }
    }

    // War Thunder replays (.wrpl): only the header is read (magic E5 AC 00 10): map, battle type, the session id and
    // the start time. The replay is written when the battle ends, so file time minus start time is its length. The
    // vehicle is not in the header, so a replay counts for the game.
    public static class WarThunderUsageParser
    {
        public const string Game = "WarThunder";

        public static UsageParseResult Parse(UsageInput input)
        {
            UsageParseResult result = new UsageParseResult();
            byte[] h = input.Bytes(0x500);
            if (h.Length < 0x3B0 || h[0] != 0xE5 || h[1] != 0xAC || h[2] != 0x00 || h[3] != 0x10)
            {
                result.Notes.Add(input.DisplayName + ": not a War Thunder replay");
                return result;
            }
            string level = Ascii(h, 0x08, 128);
            string battle = Ascii(h, 0x18C, 128);
            long session = BitConverter.ToInt64(h, 0x2E0);
            long unix = BitConverter.ToUInt32(h, 0x3A8);
            DateTime start = DateTime.MinValue;
            if (unix > 1262304000 && unix < 4102444800)
                start = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unix);
            double seconds = 0;
            if (start == DateTime.MinValue || start > input.LastWriteUtc.AddDays(1))
                start = input.LastWriteUtc;
            else
            {
                double length = (input.LastWriteUtc - start).TotalSeconds;
                if (length > 0 && length < 4 * 3600)
                    seconds = length;
            }
            UsageSession s = new UsageSession
            {
                Key = UsageSession.MakeKey(Game, "wrpl", start, session != 0 ? session.ToString("x", CultureInfo.InvariantCulture) : Path.GetFileNameWithoutExtension(input.DisplayName)),
                Game = Game,
                Source = UsageSourceKinds.WarThunderReplay,
                Start = start,
                Seconds = seconds,
                From = input.DisplayName
            };
            result.Sessions.Add(s);
            return result;
        }

        private static string Ascii(byte[] data, int offset, int length)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = offset; i < offset + length && i < data.Length && data[i] != 0; i++)
            {
                if (data[i] < 32 || data[i] > 126)
                    return string.Empty;
                sb.Append((char)data[i]);
            }
            return sb.ToString();
        }
    }

    // X4: Foundations savegames (<Documents>\Egosoft\X4\<id>\save\*.xml.gz): only the <info> block at the top of the
    // decompressed XML: the game's play time (game time="..." in seconds), the save date and the player. One total per
    // playthrough (the game's "code"/start), the newest save counts.
    public static class X4UsageParser
    {
        public const string Game = "X4";
        private static readonly Regex GameTag = new Regex(@"<game\s[^>]*>", RegexOptions.Compiled);
        private static readonly Regex SaveTag = new Regex(@"<save\s[^>]*>", RegexOptions.Compiled);
        private static readonly Regex PlayerTag = new Regex(@"<player\s[^>]*>", RegexOptions.Compiled);
        private static readonly Regex Attr = new Regex(@"(\w+)=""([^""]*)""", RegexOptions.Compiled);

        public static UsageParseResult Parse(UsageInput input)
        {
            UsageParseResult result = new UsageParseResult();
            string head;
            using (Stream file = input.Open())
            {
                Stream stream = file;
                byte[] magic = new byte[2];
                int read = file.Read(magic, 0, 2);
                file.Position = 0;
                if (read == 2 && magic[0] == 0x1F && magic[1] == 0x8B)
                    stream = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress, true);
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    char[] buffer = new char[16384];
                    int n = reader.ReadBlock(buffer, 0, buffer.Length);
                    head = new string(buffer, 0, n);
                }
            }
            int info = head.IndexOf("<info>", StringComparison.Ordinal);
            int infoEnd = head.IndexOf("</info>", StringComparison.Ordinal);
            if (info < 0 || infoEnd < info)
            {
                result.Notes.Add(input.DisplayName + ": no <info> block");
                return result;
            }
            string block = head.Substring(info, infoEnd - info);
            Dictionary<string, string> gameAttrs = Attrs(GameTag.Match(block).Value);
            Dictionary<string, string> saveAttrs = Attrs(SaveTag.Match(block).Value);
            Dictionary<string, string> playerAttrs = Attrs(PlayerTag.Match(block).Value);
            double seconds;
            string value;
            if (!gameAttrs.TryGetValue("time", out value) || !UsageInput.TryDouble(value, out seconds))
            {
                result.Notes.Add(input.DisplayName + ": no play time in <game>");
                return result;
            }
            DateTime saved = input.LastWriteUtc;
            double unix;
            if (saveAttrs.TryGetValue("date", out value) && UsageInput.TryDouble(value, out unix) && unix > 1262304000)
                saved = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unix);
            string playthrough;
            if (!gameAttrs.TryGetValue("code", out playthrough) || playthrough.Length == 0)
                gameAttrs.TryGetValue("start", out playthrough);
            if (string.IsNullOrEmpty(playthrough))
                playthrough = playerAttrs.TryGetValue("name", out value) ? value : "save";
            UsageTotal total = new UsageTotal
            {
                Key = Game + "|" + UsageSourceKinds.X4Save + "|" + playthrough,
                Game = Game,
                Source = UsageSourceKinds.X4Save,
                Hours = seconds / 3600.0,
                LastPlayed = saved,
                Seen = DateTime.UtcNow,
                From = input.DisplayName
            };
            result.Totals.Add(total);
            return result;
        }

        private static Dictionary<string, string> Attrs(string tag)
        {
            Dictionary<string, string> attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Attr.Matches(tag ?? string.Empty))
                attrs[m.Groups[1].Value] = m.Groups[2].Value;
            return attrs;
        }
    }
}
