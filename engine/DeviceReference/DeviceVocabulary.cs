#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.DeviceReference
{
    // The control vocabulary of docs/devices.md, seen as physical parts: which controls are one part (the four
    // directions and the press of a hat, both stages of a trigger, the positions of a switch), what kind of part it is
    // and how it is called in plain words.
    public static class DeviceVocabulary
    {
        private static readonly HashSet<string> Directions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "up", "down", "left", "right", "press", "push", "pull", "fwd", "aft", "center", "mid", "cw", "ccw", "in", "out",
            "upright", "upleft", "downright", "downleft", "ul", "ur", "dl", "dr", "stage1", "stage2"
        };

        // The part a control belongs to ("stick.hat.tms.up" -> "stick.hat.tms"); its direction in the part goes to
        // direction ("up"; "" for a part of its own).
        public static string ElementOf(string control, out string direction)
        {
            direction = string.Empty;
            string c = (control ?? string.Empty).Trim();
            if (c.Length == 0)
                return string.Empty;
            string lower = c.ToLowerInvariant();
            Match axis = Regex.Match(lower, @"^(\w+)\.axis\.(ministick2?)\.(x|y)$");
            if (axis.Success)
            {
                direction = axis.Groups[3].Value;
                return axis.Groups[1].Value + "." + axis.Groups[2].Value;
            }
            if (lower == "stick.axis.roll" || lower == "stick.axis.pitch")
            {
                direction = lower.Substring(11);
                return "stick.gimbal";
            }
            if (lower == "yoke.axis.roll" || lower == "yoke.axis.pitch")
            {
                direction = lower.Substring(10);
                return "yoke.column";
            }
            Match selector = Regex.Match(lower, @"^(.+\.selector\.\d+)\.pos\.(\d+)$");
            if (selector.Success)
            {
                direction = "pos" + selector.Groups[2].Value;
                return selector.Groups[1].Value;
            }
            if (lower.StartsWith("throttle.detent.", StringComparison.Ordinal) || lower.StartsWith("quadrant.detent.", StringComparison.Ordinal))
                return lower;
            string[] parts = lower.Split('.');
            if (parts.Length >= 3 && Directions.Contains(parts[parts.Length - 1]))
            {
                direction = parts[parts.Length - 1];
                return string.Join(".", parts.Take(parts.Length - 1));
            }
            return lower;
        }

        public static string ElementOf(string control)
        {
            string direction;
            return ElementOf(control, out direction);
        }

        // hat, ministick, trigger, button, release (weapon release), switch, encoder, selector, rocker, osb, key, paddle,
        // lever (gear/flaps lever, throttle or quadrant lever), slider, knob (rotary axis), wheel, twist, gimbal,
        // rudder, brake, detent.
        public static string ElementType(string element)
        {
            string e = (element ?? string.Empty).ToLowerInvariant();
            if (e.EndsWith(".gimbal", StringComparison.Ordinal) || e == "yoke.column")
                return "gimbal";
            if (e.EndsWith(".ministick", StringComparison.Ordinal) || e.EndsWith(".ministick2", StringComparison.Ordinal))
                return "ministick";
            if (e.StartsWith("panel.axis", StringComparison.Ordinal))
                return "knob";
            if (e.Contains(".axis."))
            {
                if (e.EndsWith(".twist", StringComparison.Ordinal) || e.EndsWith(".throttle_twist", StringComparison.Ordinal))
                    return "twist";
                if (e.Contains(".rudder"))
                    return "rudder";
                if (e.Contains(".brake"))
                    return "brake";
                if (e.Contains(".slider") || e.Contains(".friction"))
                    return "slider";
                if (e.Contains(".rotary"))
                    return "knob";
                if (e.Contains(".wheel"))
                    return "wheel";
                return "lever";
            }
            if (e.Contains(".detent."))
                return "detent";
            if (e.Contains(".trigger"))
                return "trigger";
            if (e.EndsWith("weapon_release", StringComparison.Ordinal))
                return "release";
            if (e.Contains(".hat.") || e.EndsWith(".coolie", StringComparison.Ordinal) || e.Contains(".hat"))
                return "hat";
            if (e.Contains(".ministick"))
                return "ministick";
            if (e.Contains(".osb."))
                return "osb";
            if (e.Contains(".rocker"))
                return "rocker";
            if (e.Contains(".key."))
                return "key";
            if (e.Contains(".selector."))
                return "selector";
            if (e.Contains(".encoder.") || e.Contains(".rotary."))
                return "encoder";
            if (e.Contains(".lever.gear") || e.Contains(".lever.flaps"))
                return "lever";
            if (e.EndsWith(".pinky.lever", StringComparison.Ordinal) || e.EndsWith("pinky", StringComparison.Ordinal) && e.StartsWith("throttle", StringComparison.Ordinal))
                return "paddle";
            if (e.Contains(".toggle") || e.Contains(".switch.") || e.EndsWith(".speedbrake", StringComparison.Ordinal) ||
                e.EndsWith(".boat", StringComparison.Ordinal) || e.EndsWith(".china", StringComparison.Ordinal) ||
                e.EndsWith(".dogfight", StringComparison.Ordinal))
                return "switch";
            return "button";
        }

        // A control in plain words: "TMS hat up", "Trigger, second stage", "Base toggle 3 down".
        public static string Describe(string control, string label)
        {
            string direction;
            string element = ElementOf(control, out direction);
            string name = (label ?? string.Empty).Trim();
            string part = name.Length > 0 ? name : ElementName(element);
            string dir = DirectionName(direction);
            if (dir.Length == 0 || part.IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0)
                return part;
            return part + " " + dir;
        }

        public static string DirectionName(string direction)
        {
            switch ((direction ?? string.Empty).ToLowerInvariant())
            {
                case "": return string.Empty;
                case "upright": case "ur": return "up-right";
                case "upleft": case "ul": return "up-left";
                case "downright": case "dr": return "down-right";
                case "downleft": case "dl": return "down-left";
                case "fwd": return "forward";
                case "aft": return "back";
                case "cw": return "clockwise";
                case "ccw": return "counter-clockwise";
                case "stage1": return "first stage";
                case "stage2": return "second stage";
                case "x": return "X";
                case "y": return "Y";
                default:
                    if (direction.StartsWith("pos", StringComparison.OrdinalIgnoreCase))
                        return "position " + direction.Substring(3);
                    return direction.ToLowerInvariant();
            }
        }

        // The part in plain words: "TMS hat", "Trigger", "Throttle base toggle 3".
        public static string ElementName(string element)
        {
            string e = (element ?? string.Empty).ToLowerInvariant();
            switch (e)
            {
                case "stick.gimbal": return "Stick (roll / pitch)";
                case "yoke.column": return "Yoke (roll / pitch)";
                case "stick.trigger": return "Trigger";
                case "stick.trigger.flip": return "Flip trigger";
                case "stick.weapon_release": return "Weapon release";
                case "stick.hat.trim": return "Trim hat";
                case "stick.hat.tms": return "TMS hat";
                case "stick.hat.dms": return "DMS hat";
                case "stick.hat.cms": return "CMS hat";
                case "stick.pinky.lever": return "Pinky lever";
                case "stick.pinky.button": return "Pinky button";
                case "stick.button.index_lower": return "Lower index button";
                case "stick.ministick": return "Thumb stick";
                case "stick.ministick2": return "Second thumb stick";
                case "stick.axis.twist": return "Twist";
                case "stick.axis.brake_lever": return "Brake lever";
                case "stick.axis.slider": return "Slider";
                case "throttle.hat.mic": return "Mic switch";
                case "throttle.speedbrake": return "Speed brake";
                case "throttle.boat": return "Boat switch";
                case "throttle.china": return "China hat";
                case "throttle.coolie": return "Coolie hat";
                case "throttle.dogfight": return "Dogfight switch";
                case "throttle.cage": return "Cage button";
                case "throttle.pinky": return "Pinky switch";
                case "throttle.pinky.button": return "Pinky button";
                case "throttle.pinky.lever": return "Pinky lever";
                case "throttle.ministick": return "Thumb stick";
                case "throttle.ministick2": return "Second thumb stick";
                case "throttle.axis.main": return "Throttle";
                case "throttle.axis.left": return "Left throttle";
                case "throttle.axis.right": return "Right throttle";
                case "throttle.axis.friction": return "Friction";
                case "pedals.axis.rudder": return "Rudder";
                case "pedals.axis.brake_left": return "Left toe brake";
                case "pedals.axis.brake_right": return "Right toe brake";
                case "collective.axis.main": return "Collective";
                case "collective.axis.throttle_twist": return "Throttle twist";
                case "panel.lever.gear": return "Gear lever";
                case "panel.lever.flaps": return "Flaps lever";
            }
            Match numberedAxis = Regex.Match(e, @"^(\w+)\.axis\.(\d+)$");
            if (numberedAxis.Success)
            {
                string family = numberedAxis.Groups[1].Value, number = numberedAxis.Groups[2].Value;
                return family == "panel" ? "Axis " + number : char.ToUpperInvariant(family[0]) + family.Substring(1) + " axis " + number;
            }
            string[] parts = e.Split('.');
            if (parts.Length == 0)
                return element;
            string device = parts[0];
            List<string> words = new List<string>();
            for (int i = 1; i < parts.Length; i++)
            {
                string p = parts[i];
                switch (p)
                {
                    case "axis": continue;
                    case "base": words.Add("base"); continue;
                    case "hat": words.Add("hat"); continue;
                    case "button": words.Add("button"); continue;
                    case "thumb": words.Add("thumb"); continue;
                    case "index": words.Add("index"); continue;
                    case "toggle": words.Add("toggle"); continue;
                    case "toggle3": words.Add("3-way toggle"); continue;
                    case "encoder": words.Add("encoder"); continue;
                    case "rotary": words.Add("rotary"); continue;
                    case "selector": words.Add("selector"); continue;
                    case "slider": words.Add("slider"); continue;
                    case "wheel": words.Add("wheel"); continue;
                    case "lever": words.Add("lever"); continue;
                    case "osb": words.Add("OSB"); continue;
                    case "rocker": words.Add("rocker"); continue;
                    case "switch": words.Add("switch"); continue;
                    case "detent": words.Add("detent"); continue;
                    case "key": words.Add("key"); continue;
                    default:
                        words.Add(p.Replace('_', ' '));
                        continue;
                }
            }
            string device0 = device == "stick" || device == "throttle" || device == "panel" || device == "mfd" ? string.Empty : device;
            if (e.StartsWith("throttle.base", StringComparison.Ordinal) || e.StartsWith("stick.base", StringComparison.Ordinal))
                device0 = string.Empty;
            if (e.StartsWith("throttle.hat.", StringComparison.Ordinal) || e.StartsWith("throttle.button.", StringComparison.Ordinal))
                device0 = "throttle";
            string text = (device0.Length > 0 ? device0 + " " : string.Empty) + string.Join(" ", words);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            // "hat 2" reads better than "hat 2" with a leading device word on stick hats.
            return text.Length == 0 ? element : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        // A short name for a marker badge ("TMS", "T1", "B3").
        public static string ShortName(string element, string label)
        {
            string e = (element ?? string.Empty).ToLowerInvariant();
            Match osb = Regex.Match(e, @"\.osb\.([tblr]\d+)$");
            if (osb.Success)
                return osb.Groups[1].Value.ToUpperInvariant();
            Match hat = Regex.Match(e, @"\.hat\.(tms|dms|cms|trim|mic)$");
            if (hat.Success)
                return hat.Groups[1].Value.ToUpperInvariant();
            return string.Empty;
        }

        // The control type of a captured DirectInput input for a new user definition (the marker assistant).
        public static string InputKind(string input)
        {
            string i = (input ?? string.Empty).ToUpperInvariant();
            if (i.StartsWith("JOY_BTN_POV", StringComparison.Ordinal))
                return "hat";
            if (i.StartsWith("JOY_BTN", StringComparison.Ordinal))
                return "button";
            if (i.StartsWith("JOY_", StringComparison.Ordinal))
                return "axis";
            return "key";
        }

        public static string HatDirection(string input)
        {
            Match m = Regex.Match((input ?? string.Empty).ToUpperInvariant(), @"^JOY_BTN_POV(\d+)_(U|UR|R|DR|D|DL|L|UL)$");
            if (!m.Success)
                return string.Empty;
            switch (m.Groups[2].Value)
            {
                case "U": return "up";
                case "UR": return "upright";
                case "R": return "right";
                case "DR": return "downright";
                case "D": return "down";
                case "DL": return "downleft";
                case "L": return "left";
                default: return "upleft";
            }
        }

        public static int HatNumber(string input)
        {
            Match m = Regex.Match((input ?? string.Empty).ToUpperInvariant(), @"^JOY_BTN_POV(\d+)_");
            int n;
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        // Sort key that keeps numbers in order ("button.2" before "button.10").
        public static string NaturalKey(string text)
        {
            return Regex.Replace(text ?? string.Empty, @"\d+", m => m.Value.PadLeft(6, '0'));
        }

        public static string Slug(string text)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in (text ?? string.Empty).ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) && c < 128)
                    sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-')
                    sb.Append('-');
            }
            return sb.ToString().Trim('-');
        }
    }
}
