#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Setup
{
    // How an experienced pilot ranks functions (docs/auto-assign.md#flight-phases): the flight phases a function belongs
    // to (slot_controls.tsv column phase), how fast it must be reached (column crit) and how much each phase matters for
    // the aircraft's role (a fighter's air-to-air weapons before its landing lights, a transport's navigation before
    // weapons). Plus the rules of special devices: cockpit-replica panels take only the functions their printed labels
    // name, MFDs only MFD functions in OSB order. Plain C# 7.3: Auto Assign (engine) and the Setup wizard's planner (engine
    // and GUI) both use it.
    public static class SetupPhases
    {
        public const string Pilot = "pilot";
        public const string Tol = "tol";
        public const string Nav = "nav";
        public const string A2A = "a2a";
        public const string A2G = "a2g";
        public const string Systems = "systems";
        public const string Comms = "comms";
        public const string View = "view";

        public static readonly string[] All = { Pilot, Tol, Nav, A2A, A2G, Systems, Comms, View };

        public const string Instant = "instant";
        public const string Seconds = "seconds";
        public const string Minutes = "minutes";
        public const string Idle = "idle";

        public static List<string> Parse(string text)
        {
            return (text ?? string.Empty).Split('|', ',', ';').Select(p => p.Trim().ToLowerInvariant()).Where(p => All.Contains(p)).Distinct().ToList();
        }

        public static string NormalizeCrit(string crit)
        {
            switch ((crit ?? string.Empty).Trim().ToLowerInvariant())
            {
                case Instant: return Instant;
                case Seconds: return Seconds;
                case Minutes: return Minutes;
                case Idle: return Idle;
                default: return string.Empty;
            }
        }

        // Time criticality as a factor of the importance: what must be reached in a split second ranks highest.
        public static double CritWeight(string crit)
        {
            switch (NormalizeCrit(crit))
            {
                case Instant: return 1.5;
                case Seconds: return 1.2;
                case Minutes: return 0.85;
                case Idle: return 0.6;
                default: return 1.0;
            }
        }

        // Reach: time-critical functions belong on the grips, functions that can wait off them (0..1 scale of the planner).
        public static double GripBonus(string crit)
        {
            switch (NormalizeCrit(crit))
            {
                case Instant: return 0.35;
                case Seconds: return 0.1;
                case Minutes: return -0.12;
                case Idle: return -0.2;
                default: return 0;
            }
        }

        // A function behind a shift button costs time: more for the time-critical ones.
        public static double LayerPenaltyFactor(string crit)
        {
            switch (NormalizeCrit(crit))
            {
                case Instant: return 1.8;
                case Seconds: return 1.2;
                case Minutes: return 0.8;
                case Idle: return 0.7;
                default: return 1.0;
            }
        }

        // The aircraft's role as the phase table knows it (aircraft.json role, spaceships by their type).
        public static string RoleKey(string role, string type)
        {
            if (string.Equals(type, "spaceship", StringComparison.OrdinalIgnoreCase))
                return "space";
            string r = (role ?? string.Empty).Trim().ToLowerInvariant();
            return Weights.ContainsKey(r) ? r : string.Empty;
        }

        //                       pilot  tol   nav   a2a   a2g   systems comms view
        private static readonly Dictionary<string, double[]> Weights = new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            { "fighter",              new[] { 1.0, 0.9, 0.6, 1.0, 0.6, 0.4, 0.5, 0.6 } },
            { "multirole",            new[] { 1.0, 0.9, 0.6, 0.9, 0.9, 0.4, 0.5, 0.6 } },
            { "attack",               new[] { 1.0, 0.9, 0.7, 0.5, 1.0, 0.4, 0.5, 0.6 } },
            { "trainer",              new[] { 1.0, 1.0, 0.8, 0.5, 0.5, 0.6, 0.6, 0.6 } },
            { "aerobatic",            new[] { 1.0, 1.0, 0.6, 0.2, 0.2, 0.5, 0.4, 0.7 } },
            { "transport",            new[] { 1.0, 1.0, 1.0, 0.1, 0.2, 0.8, 0.8, 0.6 } },
            { "helicopter_attack",    new[] { 1.0, 1.0, 0.7, 0.4, 1.0, 0.5, 0.5, 0.6 } },
            { "helicopter_utility",   new[] { 1.0, 1.0, 0.8, 0.2, 0.6, 0.6, 0.7, 0.6 } },
            { "helicopter_transport", new[] { 1.0, 1.0, 0.9, 0.1, 0.3, 0.7, 0.8, 0.6 } },
            { "space",                new[] { 1.0, 0.8, 0.9, 1.0, 0.6, 0.6, 0.5, 0.6 } }
        };

        public static double PhaseWeight(string roleKey, string phase)
        {
            double[] row;
            int index = Array.IndexOf(All, phase);
            if (index < 0)
                return 0.8;
            return Weights.TryGetValue(roleKey ?? string.Empty, out row) ? row[index] : 0.8;
        }

        // How much a function matters for one aircraft role: its most important phase. Without phases 0.8 (neutral).
        public static double RoleFactor(string roleKey, IList<string> phases)
        {
            if (phases == null || phases.Count == 0)
                return 0.8;
            return phases.Max(p => PhaseWeight(roleKey, p));
        }

        // A function used only in one combat phase can sit on that phase's layer of a mode switch.
        public static string ExclusivePhase(IList<string> phases)
        {
            if (phases == null || phases.Count != 1)
                return string.Empty;
            return phases[0] == A2A || phases[0] == A2G ? phases[0] : string.Empty;
        }

        public static string Title(string phase)
        {
            switch (phase)
            {
                case Pilot: return "Flying";
                case Tol: return "Take-off and landing";
                case Nav: return "Navigation";
                case A2A: return "Air to air";
                case A2G: return "Air to ground";
                case Systems: return "Systems";
                case Comms: return "Comms";
                case View: return "Views";
                default: return phase ?? string.Empty;
            }
        }

        // ---- special devices ----

        // Labels that only number a control ("B1", "SW1 UP", "Key 01", "5-position rotary", "KNOB LEFT") are not printed
        // function labels.
        private static readonly Regex GenericLabel = new Regex(
            @"^(B|A|C|D|E|F|K|T|S|SW|BTN|BUTTON|KEY|TOGGLE|TGL|ENC|ENCODER|ROTARY|ROT|HAT|POV|AXIS|LEVER|SWITCH)\s*-?\s*\d+[A-Z]?(\s*\(\d-position\))?(\s+(UP|DOWN|DN|CENTER|CENTRE|LEFT|RIGHT|PUSH|PRESS|CW|CCW|FWD|AFT|ON|OFF))?$|position rotary|pushbutton$|^(OUTER |INNER )?KNOB( (CW|CCW|LEFT|RIGHT|PRESS|PUSH|\+|-))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsFunctionLabel(string label)
        {
            string text = (label ?? string.Empty).Trim();
            return text.Length > 0 && !GenericLabel.IsMatch(text) && !DescribesControlOnly(text);
        }

        // Words a device catalog uses to describe a control (where it sits, what it is), not a function printed on it
        // (review 6 B10: "4-way hat Left (buttons 5-8)", "8-way POV hat", "Left position button" are descriptions; the
        // "4" took the UFC key 4, "position" the position lights). Printed panel labels are written in capitals.
        private static readonly HashSet<string> ControlWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hat", "hats", "pov", "povs", "button", "buttons", "btn", "rocker", "switch", "switches", "toggle", "toggles", "lever", "levers", "paddle",
            "trigger", "triggers", "ministick", "mini", "thumbstick", "joystick", "stick", "encoder", "dial", "wheel", "knob", "knobs", "slider", "detent",
            "detents", "position", "positions", "pos", "way", "grip", "base", "pinky", "thumb", "index", "scroll", "axis", "axes", "cover", "flip", "press",
            "pressed", "push", "pushbutton", "side", "top", "front", "rear", "back", "upper", "lower", "middle", "centre", "center", "central", "left", "right",
            "bottom", "up", "down", "fwd", "aft", "forward", "backward", "cw", "ccw", "red", "black", "white", "grey", "gray", "silver", "ring", "mode", "modes",
            "the", "of", "a", "an", "and", "or", "with", "on", "off", "in", "out", "near", "next", "to", "below", "above", "under", "behind", "at", "single",
            "dual", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "first", "second", "third", "stage", "half", "full", "small",
            "large", "big", "round", "metal", "folding", "rotary", "momentary", "latched", "upleft", "upright", "downleft", "downright"
        };
        // Control abbreviations a description writes in capitals ("8-way POV hat").
        private static readonly HashSet<string> ControlAbbreviations = new HashSet<string>(StringComparer.Ordinal) { "POV", "BTN", "TGL", "SW", "ENC", "ROT", "HAT" };

        private static bool ControlWord(string token)
        {
            return ControlWords.Contains(token) || token.Length == 1 || Regex.IsMatch(token, @"^\d+(way|pos|position|st|nd|rd|th)?$", RegexOptions.IgnoreCase);
        }

        private static bool PrintedWord(string token)
        {
            return token.Length >= 2 && token.Any(char.IsLetter) && !token.Any(char.IsLower) && !ControlAbbreviations.Contains(token);
        }

        // A catalog description that only says which control it is ("4-way hat Left (buttons 5-8)", "Left position button",
        // "Mode dial position 2", "2 (middle, below the hat)"): written in lower case and every word describes the control.
        // A word in capitals among them is printed ("POSITION left" on the MOZA MTP is the position lights knob).
        public static bool DescribesControlOnly(string label)
        {
            string text = (label ?? string.Empty).Trim();
            if (text.Length == 0 || !text.Any(char.IsLower))
                return false;
            string[] tokens = Regex.Split(text, @"[^A-Za-z0-9]+").Where(t => t.Length > 0).ToArray();
            return tokens.Length > 0 && tokens.All(t => ControlWord(t) && !PrintedWord(t));
        }

        // A function that never goes on a cockpit label naming another system: time-critical (instant) or a weapon
        // (master arm, weapon release, trigger, countermeasures, jettison ...).
        public static bool IsCriticalFunction(string id, string crit, string category)
        {
            return string.Equals(crit, "instant", StringComparison.Ordinal) || string.Equals(category, SetupImportance.Weapons, StringComparison.Ordinal) ||
                   Regex.IsMatch(id ?? string.Empty, @"master_?arm|launch_?weapon|weapon|trigger|pickle|gun|cms|chaff|flare|jett|missile|bomb|rocket", RegexOptions.IgnoreCase);
        }

        // The label speaks of the function's system but means something else ("LDG GEAR WARN SILENCE" for the gear): one
        // of its keywords is on the label, a meaning-changing word is not its own.
        public static bool LabelContradicts(IList<string> keywords, string functionWords, string label)
        {
            return keywords != null && !string.IsNullOrWhiteSpace(label) && keywords.Any(k => LabelKeywordMatches(k, label)) && !LabelNames(keywords, functionWords, label);
        }

        // A label printed like a cockpit's on a base (all capitals: "LDG GEAR WARN SILENCE", "APU START", "LAUNCH BAR UP"),
        // not a description ("left base 1", "F1 (GNX base)") and not a mode selector position ("MODE 2").
        public static bool IsCockpitLabel(string label)
        {
            string text = (label ?? string.Empty).Trim();
            if (!IsFunctionLabel(text) || text.Any(char.IsLower) || Regex.IsMatch(text, @"^MODE\s*\d+$", RegexOptions.IgnoreCase))
                return false;
            return Regex.IsMatch(text, @"[A-Z]{3,}");
        }

        // A cockpit replica (WinWing take-off panel, UFC, ICP, combat-ready panel ...): at least half of its controls, and at
        // least four, carry printed function labels.
        public static bool IsReplicaPanel(string kind, IEnumerable<string> labels)
        {
            if (string.Equals(kind, "mfd", StringComparison.OrdinalIgnoreCase))
                return true;
            List<string> list = (labels ?? Enumerable.Empty<string>()).ToList();
            int printed = list.Count(IsFunctionLabel);
            return list.Count > 0 && printed >= 4 && printed * 2 >= list.Count;
        }

        public static bool IsMfdControl(string control)
        {
            return (control ?? string.Empty).StartsWith("mfd.", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsMfdFunction(IList<string> prefs)
        {
            return prefs != null && prefs.Count > 0 && prefs.All(p => p.StartsWith("mfd.", StringComparison.OrdinalIgnoreCase));
        }

        // A label keyword: whole words anywhere ("ANTI SKID"), "^1" = the label starts with the word 1 ("1 T-ILS"), "=A" =
        // the label is exactly A.
        public static bool LabelKeywordMatches(string keyword, string label)
        {
            if (string.IsNullOrEmpty(keyword) || string.IsNullOrEmpty(label))
                return false;
            string text = label.Trim();
            Match match;
            if (keyword[0] == '=')
                match = string.Equals(text, keyword.Substring(1), StringComparison.OrdinalIgnoreCase) ? Regex.Match(text, "^.*$", RegexOptions.Singleline) : Match.Empty;
            else if (keyword[0] == '^')
                match = Regex.Match(text, "^(" + Regex.Escape(keyword.Substring(1)) + @")($|[^A-Za-z0-9])", RegexOptions.IgnoreCase);
            else
                match = Regex.Match(text, @"(^|[^A-Za-z0-9])(" + Regex.Escape(keyword) + @")($|[^A-Za-z0-9])", RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;
            // A keyword made of words that also describe controls ("POSITION", the UFC's "4" and "8") names a function on a
            // description in lower case only where it is printed in capitals: "POSITION left" yes, "Left position button" and
            // "4-way hat" no (review 6 B10).
            string body = keyword.TrimStart('=', '^');
            if (text.Any(char.IsLower) && Regex.Split(body, @"[^A-Za-z0-9]+").Where(t => t.Length > 0).All(ControlWord))
            {
                string found = keyword[0] == '=' ? match.Value : match.Groups[keyword[0] == '^' ? 1 : 2].Value;
                return found.Any(char.IsLetter) && !found.Any(char.IsLower);
            }
            return true;
        }

        // Words that change what a printed label means: "LDG GEAR WARN SILENCE" silences the gear warning horn, it does not
        // move the gear; "MASTER ARM TEST", "LDG LIGHT", "ALT FLAPS", "EMERG BRAKE". Label word -> its meaning, and the
        // spellings that say the same in a function's id, name or keywords.
        private static readonly Dictionary<string, string> ModifierWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "WARN", "warn" }, { "WARNING", "warn" }, { "WRN", "warn" }, { "CAUTION", "warn" },
            { "SILENCE", "silence" }, { "SIL", "silence" }, { "MUTE", "silence" },
            { "HORN", "horn" },
            { "TEST", "test" }, { "TST", "test" },
            { "LIGHT", "light" }, { "LIGHTS", "light" }, { "LT", "light" }, { "LTS", "light" }, { "LAMP", "light" }, { "LAMPS", "light" },
            { "BRT", "bright" }, { "BRIGHT", "bright" }, { "BRIGHTNESS", "bright" }, { "DIM", "bright" }, { "DIMMER", "bright" },
            { "ALT", "alt" }, { "ALTN", "alt" }, { "ALTER", "alt" }, { "ALTERNATE", "alt" },
            { "EMERG", "emerg" }, { "EMER", "emerg" }, { "EMERGENCY", "emerg" }
        };

        private static readonly Dictionary<string, string[]> ModifierSpellings = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "warn", new[] { "warn", "caution" } },
            { "silence", new[] { "silence", "mute" } },
            { "horn", new[] { "horn" } },
            { "test", new[] { "test" } },
            { "light", new[] { "light", "lamp", "lts" } },
            { "bright", new[] { "bright", "brt", "dim" } },
            { "alt", new[] { "alt" } },
            { "emerg", new[] { "emerg", "emer" } }
        };

        // A printed label names the function: one of its keywords is on the label, and every word on the label that changes
        // the meaning (WARN, SILENCE, HORN, TEST, LIGHT, BRT / DIM, ALT, EMERG) is the function's own too (its id, name or
        // keywords). Else the label is skipped for that function.
        public static bool LabelNames(IList<string> keywords, string functionWords, string label)
        {
            if (keywords == null || string.IsNullOrWhiteSpace(label) || !keywords.Any(k => LabelKeywordMatches(k, label)))
                return false;
            string own = (functionWords ?? string.Empty).ToLowerInvariant() + " " + string.Join(" ", keywords).ToLowerInvariant();
            foreach (string word in Regex.Split(label, @"[^A-Za-z0-9]+"))
            {
                string meaning;
                if (word.Length == 0 || !ModifierWords.TryGetValue(word, out meaning))
                    continue;
                if (!ModifierSpellings[meaning].Any(s => own.IndexOf(s, StringComparison.Ordinal) >= 0))
                    return false;
            }
            return true;
        }

        public static bool LabelNames(SetupFunction function, string label)
        {
            return function != null && LabelNames(function.Labels, function.Id + " " + function.Name, label);
        }

        // Slew is not TMS (the user's style, docs/auto-group.md#free-slots-and-your-style): slew directions never on a TMS
        // hat, the TMS / DMS / CMS / sensor hats never on a thumb stick, unless the function's own position list names it
        // (TMS on a stick's ministick when the grip has no TMS hat).
        public static bool SlewTmsClash(string functionId, string group, string control)
        {
            string id = functionId ?? string.Empty;
            string c = control ?? string.Empty;
            if (id.StartsWith("tdc_slew_", StringComparison.Ordinal) && c.IndexOf(".hat.tms.", StringComparison.Ordinal) >= 0)
                return true;
            string g = (group ?? string.Empty).Split(':')[0];
            return (g == "tms" || g == "dms" || g == "cms" || g == "sensor") && c.IndexOf("ministick", StringComparison.Ordinal) >= 0;
        }

        // ---- directional sets (TMS, DMS, CMS, trim, slew ...) ----

        private static readonly HashSet<string> DirectionRoles = new HashSet<string>(StringComparer.Ordinal) { "up", "down", "left", "right", "press" };

        // The hat groups of the catalog: a group whose members take at least three of up / down / left / right (TMS, DMS,
        // CMS, trim, sensor select, weapon select, slew, the AI menu ...). Their members go together on the matching
        // directions of one hat (or thumb stick), or they are not placed.
        public static HashSet<string> DirectionalGroups(IEnumerable<string> groups)
        {
            Dictionary<string, HashSet<string>> roles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (string group in groups ?? Enumerable.Empty<string>())
            {
                string[] parts = (group ?? string.Empty).Split(':');
                if (parts.Length != 2 || !DirectionRoles.Contains(parts[1]))
                    continue;
                HashSet<string> set;
                if (!roles.TryGetValue(parts[0], out set))
                    roles[parts[0]] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(parts[1]);
            }
            return new HashSet<string>(roles.Where(r => r.Value.Count(v => v != "press") >= 3).Select(r => r.Key), StringComparer.Ordinal);
        }

        // "tms:up" -> ("tms", "up") when the group is a directional set, else empty.
        public static bool DirectionalMember(string group, HashSet<string> directional, out string key, out string role)
        {
            key = role = string.Empty;
            string[] parts = (group ?? string.Empty).Split(':');
            if (parts.Length != 2 || directional == null || !directional.Contains(parts[0]) || !DirectionRoles.Contains(parts[1]))
                return false;
            key = parts[0];
            role = parts[1];
            return true;
        }

        // A control that is the given direction of a hat, coolie or thumb stick ("stick.hat.tms.up" for up).
        public static bool MatchesDirection(string role, string control)
        {
            string c = (control ?? string.Empty).ToLowerInvariant();
            if (c.IndexOf(".hat.", StringComparison.Ordinal) < 0 && c.IndexOf(".ministick", StringComparison.Ordinal) < 0 && c.IndexOf(".coolie", StringComparison.Ordinal) < 0)
                return false;
            if (c.IndexOf(".axis.", StringComparison.Ordinal) >= 0)
                return false;
            int dot = c.LastIndexOf('.');
            return dot >= 0 && string.Equals(c.Substring(dot + 1), role, StringComparison.Ordinal);
        }

        // The hat of a direction control ("stick.hat.tms.up" -> "stick.hat.tms").
        public static string HatOf(string control)
        {
            string c = control ?? string.Empty;
            int dot = c.LastIndexOf('.');
            return dot > 0 ? c.Substring(0, dot) : c;
        }

        // ---- directions on printed labels ----

        // Position words of printed labels (the last one counts: "MASTER ARM SAFE" = off, "LDG GEAR CENTER" = centre).
        private static readonly Dictionary<string, string> DirectionWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "UP", "up" }, { "RET", "up" }, { "RETRACT", "up" }, { "STOW", "up" },
            { "DOWN", "down" }, { "DN", "down" }, { "EXTEND", "down" }, { "FULL", "down" }, { "DEPLOY", "down" },
            { "CENTER", "center" }, { "CENTRE", "center" }, { "CTR", "center" }, { "HALF", "center" }, { "HOLD", "center" }, { "MID", "center" },
            { "NEUTRAL", "center" }, { "NORM", "center" }, { "STBY", "center" },
            { "ON", "on" }, { "ARM", "on" }, { "START", "on" }, { "OFF", "off" }, { "SAFE", "off" }, { "STOP", "off" },
            { "OPEN", "open" }, { "CLOSE", "close" }, { "CLOSED", "close" }, { "FOLD", "fold" }, { "SPREAD", "spread" }
        };

        private static readonly Dictionary<string, string> FunctionWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "up", "up" }, { "in", "up" }, { "retract", "up" }, { "stow", "up" }, { "down", "down" }, { "out", "down" }, { "extend", "down" },
            { "neutral", "center" }, { "center", "center" }, { "on", "on" }, { "arm", "on" }, { "start", "on" }, { "off", "off" }, { "safe", "off" },
            { "stop", "off" }, { "open", "open" }, { "close", "close" }, { "fold", "fold" }, { "spread", "spread" }
        };

        private static readonly Regex FunctionDirectionPattern = new Regex(@"(up|down|in|out|on|off|open|close|extend|retract|neutral|center|arm|safe|start|stop|fold|spread|stow)\d*(_2)?$", RegexOptions.Compiled);

        // The position a function asks for, from its on/off pair role or the end of its id (gearin = up, flapsdown = down,
        // master_arm_on = on). Keypad keys and toggles have none.
        public static string FunctionDirection(string id, string pairRole)
        {
            string role;
            if (!string.IsNullOrEmpty(pairRole) && FunctionWords.TryGetValue(pairRole, out role))
                return role;
            string text = (id ?? string.Empty).ToLowerInvariant();
            if (text.StartsWith("ufc", StringComparison.Ordinal) || text.StartsWith("mfd", StringComparison.Ordinal) || text.Contains("toggle") || text.Contains("onoff"))
                return string.Empty;
            Match m = FunctionDirectionPattern.Match(text);
            return m.Success && FunctionWords.TryGetValue(m.Groups[1].Value, out role) ? role : string.Empty;
        }

        public static string LabelDirection(string label)
        {
            string found = string.Empty;
            foreach (string word in Regex.Split(label ?? string.Empty, @"[^A-Za-z0-9]+"))
            {
                string direction;
                if (word.Length > 0 && DirectionWords.TryGetValue(word, out direction))
                    found = direction;
            }
            return found;
        }

        // A printed position that is not the function's ("LDG GEAR CENTER" for gear up, "MASTER ARM SAFE" for arm on).
        public static bool DirectionClash(string functionDirection, string labelDirection)
        {
            return functionDirection.Length > 0 && labelDirection.Length > 0 && !SameDirection(functionDirection, labelDirection);
        }

        // On is up and off is down on a switch ("Switch 2 up" = on), as in every cockpit.
        public static bool SameDirection(string a, string b)
        {
            if (a == b)
                return true;
            return (a == "on" && b == "up") || (a == "up" && b == "on") || (a == "off" && b == "down") || (a == "down" && b == "off");
        }

        // How much of a printed label a function's keywords explain (position words left out): "JETT SAFE" explains
        // "SELECT JETT SAFE" better than "JETT" explains "SELECT JETT L FUS MSL". 0 when no keyword matches.
        public static double LabelFit(IList<string> keywords, string label)
        {
            if (keywords == null || keywords.Count == 0 || string.IsNullOrWhiteSpace(label))
                return 0;
            int words = Regex.Split(label.Trim(), @"\s+").Count(w => w.Length > 0 && !DirectionWords.ContainsKey(w));
            double best = 0;
            foreach (string keyword in keywords)
            {
                if (!LabelKeywordMatches(keyword, label))
                    continue;
                // (only called for a label that names the function: LabelNames)
                string text = keyword.TrimStart('^', '=');
                int count = Math.Max(1, Regex.Split(text.Trim(), @"\s+").Count(w => w.Length > 0));
                best = Math.Max(best, Math.Min(1.0, count / (double)Math.Max(1, words)));
            }
            return best;
        }

        // ---- phase layers (a latched mode switch) ----

        // Positions of latched switches that can carry a phase layer: the two outer positions of a toggle switch on a base
        // or collective (the centre of a 3-position switch is the normal layer), a mode selector's first two positions,
        // a yoke's mode switch. Switches with a fixed role (boat, dogfight, speed brake, pinky) and labelled cockpit
        // switches are not mode switches. Rank: lower is better (a switch labelled MODE first, then bases, then panels).
        public static bool ModeSwitchPosition(string control, string type, string label, out string switchKey, out string position, out int rank)
        {
            switchKey = position = string.Empty;
            rank = 0;
            string c = control ?? string.Empty;
            if (!string.Equals(type, "latched", StringComparison.OrdinalIgnoreCase))
                return false;
            bool modeLabel = Regex.IsMatch(label ?? string.Empty, @"\bmode\b", RegexOptions.IgnoreCase);
            // (a switch labelled for a function - ENG OPER L, ENG 1 MASTER, L GEN, ATEM ARMED - is that function's on a base
            // and a grip too, never the mode switch)
            if (!modeLabel && (IsCockpitLabel(label) || (c.StartsWith("panel.", StringComparison.Ordinal) && IsFunctionLabel(label))))
                return false;
            // (a guarded switch - under a cover, for something that must not be flipped by accident - and a switch whose
            // label names anything but the control itself - MOZA MTP "RUD TRIM (3-way switch) EXTEND / RETRACT" - is never
            // the mode switch either)
            string text = label ?? string.Empty;
            if (Regex.IsMatch(text, @"guard|cover", RegexOptions.IgnoreCase))
                return false;
            if (!modeLabel && text.Trim().Length > 0 && !SetupPlanner.ControlNameLabel(Regex.Replace(text, @"\((\d-way )?(switch|toggle)[^)]*\)", " ", RegexOptions.IgnoreCase)))
                return false;
            Match m = Regex.Match(c, @"^((stick|throttle|collective|yoke)\.base\.toggle3?\.[^.]+|(collective|yoke)\.switch\.[^.]+|panel\.toggle3?\.[^.]+)\.(up|down|left|right)$");
            if (m.Success)
            {
                switchKey = m.Groups[1].Value;
                position = m.Groups[4].Value == "left" ? "up" : m.Groups[4].Value == "right" ? "down" : m.Groups[4].Value;
                rank = (modeLabel ? 0 : 10) + (c.StartsWith("panel.", StringComparison.Ordinal) ? 20 : 0);
                return true;
            }
            m = Regex.Match(c, @"^((stick|throttle|collective)\.base\.selector\.[^.]+)\.pos\.([12])$");
            if (m.Success)
            {
                switchKey = m.Groups[1].Value;
                position = m.Groups[3].Value == "1" ? "up" : "down";
                rank = modeLabel ? 0 : 5;
                return true;
            }
            return false;
        }

        // Whether a game handles modifier buttons (engine games support): "yes", "partial", "no". A phase layer is a held
        // (latched) modifier: only games with full modifier support; shift layers also in games with partial support.
        public static bool AllowsLayer(string support, bool phaseLayer)
        {
            string s = (support ?? "yes").Trim().ToLowerInvariant();
            if (s == "no")
                return false;
            return !phaseLayer || s == "yes";
        }
    }
}
