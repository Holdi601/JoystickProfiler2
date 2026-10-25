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
    // Elite Dangerous journals (<Saved Games>\Frontier Developments\Elite Dangerous\Journal.<time>.<part>.log, one JSON
    // event per line): the ship from LoadGame / Loadout / ShipyardSwap / ShipyardNew, FSDJump (jumps, JumpDist), Docked
    // and player-controlled Touchdown (landings), Bounty / FactionKillBond / PVPKill / CapShipBond (kills), Died.
    public static class EliteUsageParser
    {
        public const string Game = "EliteDangerous";
        private static readonly Regex Timestamp = new Regex(@"""timestamp""\s*:\s*""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex EventName = new Regex(@"""event""\s*:\s*""([^""]+)""", RegexOptions.Compiled);

        // The in-game names of the ships (journal ids are lowercase); a name the journal brings (Ship_Localised) wins.
        private static readonly Dictionary<string, string> ShipNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "sidewinder", "Sidewinder" }, { "eagle", "Eagle" }, { "hauler", "Hauler" }, { "adder", "Adder" }, { "empire_eagle", "Imperial Eagle" },
            { "viper", "Viper Mk III" }, { "cobramkiii", "Cobra Mk III" }, { "viper_mkiv", "Viper Mk IV" }, { "diamondback", "Diamondback Scout" },
            { "cobramkiv", "Cobra Mk IV" }, { "type6", "Type-6 Transporter" }, { "dolphin", "Dolphin" }, { "diamondbackxl", "Diamondback Explorer" },
            { "empire_courier", "Imperial Courier" }, { "independant_trader", "Keelback" }, { "asp_scout", "Asp Scout" }, { "vulture", "Vulture" },
            { "asp", "Asp Explorer" }, { "federation_dropship", "Federal Dropship" }, { "type7", "Type-7 Transporter" }, { "typex", "Alliance Chieftain" },
            { "federation_dropship_mkii", "Federal Assault Ship" }, { "empire_trader", "Imperial Clipper" }, { "typex_2", "Alliance Crusader" },
            { "typex_3", "Alliance Challenger" }, { "federation_gunship", "Federal Gunship" }, { "krait_light", "Krait Phantom" }, { "krait_mkii", "Krait Mk II" },
            { "orca", "Orca" }, { "ferdelance", "Fer-de-Lance" }, { "mamba", "Mamba" }, { "python", "Python" }, { "python_nx", "Python Mk II" },
            { "type9", "Type-9 Heavy" }, { "belugaliner", "Beluga Liner" }, { "type9_military", "Type-10 Defender" }, { "anaconda", "Anaconda" },
            { "federation_corvette", "Federal Corvette" }, { "cutter", "Imperial Cutter" }, { "mandalay", "Mandalay" }, { "cobramkv", "Cobra Mk V" },
            { "corsair", "Corsair" }, { "type8", "Type-8 Transporter" }, { "explorer_nx", "Caspian Explorer" }, { "panthermkii", "Panther Clipper Mk II" }
        };

        public static UsageParseResult Parse(UsageInput input)
        {
            UsageParseResult result = new UsageParseResult();
            UsageTimeline timeline = null;
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in input.Lines())
            {
                if (line.Length < 20)
                    continue;
                Match ts = Timestamp.Match(line);
                Match ev = EventName.Match(line);
                if (!ts.Success || !ev.Success)
                    continue;
                DateTime time = UsageText.ParseIso(ts.Groups[1].Value);
                if (time == DateTime.MinValue)
                    continue;
                if (timeline == null)
                    timeline = new UsageTimeline(Game, UsageSourceKinds.EliteJournal, time, input.DisplayName);
                timeline.Seen(time);
                string name = ev.Groups[1].Value;
                switch (name)
                {
                    case "LoadGame":
                    case "Loadout":
                    {
                        string ship = JsonString(line, "Ship");
                        string local = JsonString(line, "Ship_Localised");
                        if (ship.Length > 0)
                            EnterShip(timeline, names, ship, local, time);
                        break;
                    }
                    case "ShipyardSwap":
                    case "ShipyardNew":
                    {
                        string ship = JsonString(line, "ShipType");
                        string local = JsonString(line, "ShipType_Localised");
                        if (ship.Length > 0)
                            EnterShip(timeline, names, ship, local, time);
                        break;
                    }
                    case "FSDJump":
                    {
                        timeline.Count(UsageCounters.Jumps);
                        double dist;
                        if (UsageInput.TryDouble(JsonNumber(line, "JumpDist"), out dist) && dist > 0)
                            timeline.Count(UsageCounters.DistanceLy, dist);
                        break;
                    }
                    case "Docked":
                        timeline.Count(UsageCounters.Landings);
                        break;
                    case "Touchdown":
                        if (line.IndexOf("\"PlayerControlled\":true", StringComparison.Ordinal) >= 0 || line.IndexOf("\"PlayerControlled\": true", StringComparison.Ordinal) >= 0)
                            timeline.Count(UsageCounters.Landings);
                        break;
                    case "Undocked":
                    case "Liftoff":
                        if (name == "Undocked" || line.IndexOf("\"PlayerControlled\":true", StringComparison.Ordinal) >= 0)
                            timeline.Count(UsageCounters.TakeOffs);
                        break;
                    case "Bounty":
                    case "FactionKillBond":
                    case "PVPKill":
                    case "CapShipBond":
                        timeline.Count(UsageCounters.Kills);
                        break;
                    case "Died":
                        timeline.Count(UsageCounters.Deaths);
                        break;
                    case "Shutdown":
                        timeline.Leave(time);
                        break;
                }
            }
            if (timeline == null)
                return result;
            UsageSession session = timeline.Finish();
            session.Key = UsageSession.MakeKey(Game, "elite-journal", session.Start);
            result.Sessions.Add(session);
            return result;
        }

        private static void EnterShip(UsageTimeline timeline, Dictionary<string, string> names, string ship, string local, DateTime time)
        {
            string id = ship.ToLowerInvariant();
            if (local.Length > 0)
                names[id] = local;
            string display;
            if (!names.TryGetValue(id, out display) && !ShipNames.TryGetValue(id, out display))
                display = ship;
            timeline.Enter(id, display, time);
        }

        internal static string JsonString(string line, string key)
        {
            string pattern = "\"" + key + "\"";
            int at = line.IndexOf(pattern, StringComparison.Ordinal);
            if (at < 0)
                return string.Empty;
            int colon = line.IndexOf(':', at + pattern.Length);
            int quote = colon < 0 ? -1 : line.IndexOf('"', colon + 1);
            if (quote < 0 || line.Substring(colon + 1, quote - colon - 1).Trim().Length > 0)
                return string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = quote + 1; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\\' && i + 1 < line.Length)
                {
                    sb.Append(line[++i]);
                    continue;
                }
                if (c == '"')
                    break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        internal static string JsonNumber(string line, string key)
        {
            Match m = Regex.Match(line, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?[0-9.]+)");
            return m.Success ? m.Groups[1].Value : string.Empty;
        }
    }

    // Star Citizen game logs (<install>\<channel>\Game.log, moved to logbackups\Game Build(...).log at the next start):
    // the ship from "granted control token for '<ship>_<id>'" until "releasing control token", kills and deaths from
    // <Actor Death> (the local player's id is the "Local client node [id]"), quantum travel arrivals as jumps.
    public static class StarCitizenUsageParser
    {
        public const string Game = "StarCitizen";
        private static readonly Regex Stamp = new Regex(@"^<(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z)>", RegexOptions.Compiled);
        private static readonly Regex Token = new Regex(@"Local client node \[(\d+)\] (granted|releasing) control token for '([^']+?)(?:_\d+)?'", RegexOptions.Compiled);
        private static readonly Regex LocalNode = new Regex(@"Local client node \[(\d+)\]", RegexOptions.Compiled);
        private static readonly Regex Death = new Regex(@"CActor::Kill: '([^']*)' \[(\d+)\].*? killed by '([^']*)' \[(\d+)\]", RegexOptions.Compiled);

        // Manufacturer codes of the ship ids.
        private static readonly Dictionary<string, string> Makers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "AEGS", "Aegis" }, { "ANVL", "Anvil" }, { "AOPOA", "Aopoa" }, { "ARGO", "Argo" }, { "BANU", "Banu" }, { "CNOU", "C.O." }, { "CRUS", "Crusader" },
            { "DRAK", "Drake" }, { "ESPR", "Esperia" }, { "GAMA", "Gatac" }, { "GRIN", "Greycat" }, { "KRIG", "Kruger" }, { "MISC", "MISC" },
            { "MRAI", "Mirai" }, { "ORIG", "Origin" }, { "RSI", "RSI" }, { "TMBL", "Tumbril" }, { "VNCL", "Vanduul" }, { "XNAA", "Aopoa" },
            { "XIAN", "Aopoa" }, { "CRUSADER", "Crusader" }
        };

        public static UsageParseResult Parse(UsageInput input)
        {
            UsageParseResult result = new UsageParseResult();
            UsageTimeline timeline = null;
            string local = string.Empty;
            string lastLine = null;
            foreach (string line in input.Lines())
            {
                if (line.Length < 26 || line[0] != '<')
                    continue;
                // Only the first line and the lines that matter are parsed for their time (the logs of a year are a
                // gigabyte); the last line gives the end.
                bool interesting = timeline == null || line.IndexOf("control token for", StringComparison.Ordinal) >= 0 ||
                                   line.IndexOf("<Actor Death>", StringComparison.Ordinal) >= 0 || line.IndexOf("<Quantum Drive Arrived", StringComparison.Ordinal) >= 0 ||
                                   (local.Length == 0 && line.IndexOf("Local client node [", StringComparison.Ordinal) >= 0);
                if (!interesting)
                {
                    lastLine = line;
                    continue;
                }
                DateTime time = StampOf(line);
                if (time == DateTime.MinValue)
                    continue;
                lastLine = line;
                if (timeline == null)
                    timeline = new UsageTimeline(Game, UsageSourceKinds.StarCitizenLog, time, input.DisplayName);
                timeline.Seen(time);
                if (line.IndexOf("control token for", StringComparison.Ordinal) >= 0)
                {
                    Match token = Token.Match(line);
                    if (!token.Success)
                        continue;
                    if (local.Length == 0)
                        local = token.Groups[1].Value;
                    string ship = token.Groups[3].Value;
                    if (token.Groups[2].Value == "granted")
                    {
                        if (!string.Equals(ship, "Default", StringComparison.OrdinalIgnoreCase))
                            timeline.Enter(ship, ShipName(ship), time);
                    }
                    else if (timeline.Open != null && string.Equals(timeline.Open.Unit, ship, StringComparison.OrdinalIgnoreCase))
                    {
                        timeline.Leave(time);
                    }
                    continue;
                }
                if (local.Length == 0 && line.IndexOf("Local client node [", StringComparison.Ordinal) >= 0)
                {
                    Match node = LocalNode.Match(line);
                    if (node.Success)
                        local = node.Groups[1].Value;
                }
                if (line.IndexOf("<Actor Death>", StringComparison.Ordinal) >= 0)
                {
                    Match death = Death.Match(line);
                    if (!death.Success || local.Length == 0)
                        continue;
                    string victim = death.Groups[2].Value, killer = death.Groups[4].Value;
                    if (victim == local)
                        timeline.Count(UsageCounters.Deaths);
                    else if (killer == local)
                        timeline.Count(UsageCounters.Kills);
                    continue;
                }
                if (line.IndexOf("<Quantum Drive Arrived", StringComparison.Ordinal) >= 0)
                    timeline.Count(UsageCounters.Jumps);
            }
            if (timeline == null)
                return result;
            if (lastLine != null)
            {
                DateTime end = StampOf(lastLine);
                if (end != DateTime.MinValue)
                    timeline.Seen(end);
            }
            UsageSession session = timeline.Finish();
            session.Key = UsageSession.MakeKey(Game, "sc-log", session.Start);
            result.Sessions.Add(session);
            return result;
        }

        private static DateTime StampOf(string line)
        {
            Match stamp = Stamp.Match(line);
            return stamp.Success ? UsageText.ParseIso(stamp.Groups[1].Value) : DateTime.MinValue;
        }

        // "CRUS_Starlifter_A2" -> "Crusader Starlifter A2", "ANVL_Hornet_F7A_Mk2" -> "Anvil Hornet F7A Mk2".
        public static string ShipName(string id)
        {
            string[] parts = (id ?? string.Empty).Split('_');
            if (parts.Length == 0)
                return id;
            string maker;
            if (Makers.TryGetValue(parts[0], out maker))
                parts[0] = maker;
            return string.Join(" ", parts.Where(p => p.Length > 0));
        }
    }

    // Steam's playtime per game (<Steam>\userdata\<account>\config\localconfig.vdf: apps\<id>\Playtime in minutes,
    // LastPlayed as Unix time). Every account of this PC adds up.
    public static class SteamUsageParser
    {
        public sealed class AppTime
        {
            public int App;
            public double Minutes;
            public DateTime LastPlayed;
        }

        public static List<AppTime> ReadPlaytimes(string vdfText)
        {
            List<AppTime> list = new List<AppTime>();
            VdfNode root = Vdf.Parse(vdfText);
            // UserLocalConfigStore\Software\Valve\Steam\apps (case varies between Steam versions).
            foreach (VdfNode apps in root.FindAll("apps"))
            {
                foreach (VdfNode app in apps.Children)
                {
                    int id;
                    if (!int.TryParse(app.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                        continue;
                    string playtime = app.Value("Playtime");
                    double minutes;
                    if (string.IsNullOrEmpty(playtime) || !UsageInput.TryDouble(playtime, out minutes))
                        continue;
                    double unix;
                    DateTime last = UsageInput.TryDouble(app.Value("LastPlayed"), out unix) && unix > 0
                        ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unix)
                        : DateTime.MinValue;
                    if (list.Any(a => a.App == id))
                        continue;
                    list.Add(new AppTime { App = id, Minutes = minutes, LastPlayed = last });
                }
            }
            return list;
        }

        // The totals of every game with a Steam app id; accounts add up.
        public static List<UsageTotal> Totals(IEnumerable<string> vdfFiles, Func<string, int[]> appsOf, IEnumerable<string> games, List<string> notes)
        {
            Dictionary<int, AppTime> sum = new Dictionary<int, AppTime>();
            foreach (string file in vdfFiles)
            {
                try
                {
                    string text;
                    using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        text = reader.ReadToEnd();
                    foreach (AppTime t in ReadPlaytimes(text))
                    {
                        AppTime s;
                        if (!sum.TryGetValue(t.App, out s))
                            sum[t.App] = s = new AppTime { App = t.App };
                        s.Minutes += t.Minutes;
                        if (t.LastPlayed > s.LastPlayed)
                            s.LastPlayed = t.LastPlayed;
                    }
                }
                catch (IOException ex)
                {
                    notes.Add(Path.GetFileName(file) + ": " + ex.Message);
                }
            }
            List<UsageTotal> totals = new List<UsageTotal>();
            HashSet<int> used = new HashSet<int>();
            foreach (string game in games)
            {
                double minutes = 0;
                DateTime last = DateTime.MinValue;
                foreach (int app in appsOf(game) ?? new int[0])
                {
                    AppTime t;
                    // An app id two integrations share (VTOL VR) counts for the first one only.
                    if (!sum.TryGetValue(app, out t) || !used.Add(app))
                        continue;
                    minutes += t.Minutes;
                    if (t.LastPlayed > last)
                        last = t.LastPlayed;
                }
                if (minutes <= 0)
                    continue;
                totals.Add(new UsageTotal
                {
                    Key = game + "|" + UsageSourceKinds.SteamPlaytime,
                    Game = game,
                    Source = UsageSourceKinds.SteamPlaytime,
                    Hours = minutes / 60.0,
                    LastPlayed = last,
                    Seen = DateTime.UtcNow,
                    From = "localconfig.vdf"
                });
            }
            return totals;
        }
    }

    // Valve's KeyValues text ("name" { "key" "value" ... }), enough for localconfig.vdf.
    public sealed class VdfNode
    {
        public string Name = string.Empty;
        public string Text;
        public List<VdfNode> Children = new List<VdfNode>();

        public string Value(string key)
        {
            VdfNode child = Children.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase) && c.Text != null);
            return child == null ? null : child.Text;
        }

        public IEnumerable<VdfNode> FindAll(string name)
        {
            foreach (VdfNode child in Children)
            {
                if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase) && child.Text == null)
                    yield return child;
                foreach (VdfNode inner in child.FindAll(name))
                    yield return inner;
            }
        }
    }

    public static class Vdf
    {
        public static VdfNode Parse(string text)
        {
            VdfNode root = new VdfNode();
            Stack<VdfNode> stack = new Stack<VdfNode>();
            stack.Push(root);
            string pendingKey = null;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n')
                        i++;
                    continue;
                }
                if (c == '{')
                {
                    VdfNode node = new VdfNode { Name = pendingKey ?? string.Empty };
                    stack.Peek().Children.Add(node);
                    stack.Push(node);
                    pendingKey = null;
                    i++;
                    continue;
                }
                if (c == '}')
                {
                    if (stack.Count > 1)
                        stack.Pop();
                    pendingKey = null;
                    i++;
                    continue;
                }
                string token;
                if (c == '"')
                {
                    StringBuilder sb = new StringBuilder();
                    i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length)
                        {
                            i++;
                            char e = text[i];
                            sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e);
                        }
                        else
                            sb.Append(text[i]);
                        i++;
                    }
                    i++;
                    token = sb.ToString();
                }
                else
                {
                    int start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}' && text[i] != '"')
                        i++;
                    token = text.Substring(start, i - start);
                }
                if (pendingKey == null)
                    pendingKey = token;
                else
                {
                    stack.Peek().Children.Add(new VdfNode { Name = pendingKey, Text = token });
                    pendingKey = null;
                }
            }
            return root;
        }
    }
}
