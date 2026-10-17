using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Axis
{
    // Star Citizen's game-wide control settings: <Attr name="..." value="..."/> lines of the user profile's attributes.xml
    // (LIVE\user\client\0\Profiles\default\attributes.xml, next to actionmaps.xml). Names and defaults are the game's
    // own (Data.p4k Data\Libs\Config\Profiles\default\attributes.xml, Version 35, SC 4.10); the game writes only changed
    // values, sorted by name. Ranges are not in the game files (the menu's limits live in the encrypted DataCore):
    // the ones below are generous guesses (unverified).
    public static class StarCitizenGameSettings
    {
        public const string Game = "StarCitizen";

        [GameFfbModel]
        private static FfbModel Build()
        {
            return new FfbModel
            {
                Game = Game,
                Title = "Star Citizen",
                Description = "Game-wide (device *), Options > Comms/Controls and Keybindings: attributes.xml of the user profile, written in place (other lines stay). Vibration is the only force feedback setting the game has.",
                Parameters =
                {
                    Toggle("Vibration", "Vibration (force feedback / rumble)", 1, "i_forcefeedback; whether DirectInput force feedback sticks get effects is unverified."),
                    Toggle("FlightThrustInvertToggle", "Flight - HOTAS thrust invert toggle", 1, "1: the thrust invert button toggles, 0: hold (v_ifcs2_input.thrustInvert_isToggle)."),
                    Number("FlightRelativeThrottleTrapTime", "Pilot - relative throttle - axis crossing trap time (s)", 0, 5, 0.3, null),
                    Number("FlightRelativeThrottleTrapThreshold", "Pilot - relative throttle - axis crossing trap threshold", 0, 1, 0.25, null),
                    Number("FlightCoreDisabledSensitivityRotation", "IFCS core disabled - rotation sensitivity modifier", 0, 10, 2, null),
                    Number("FlightCoreDisabledSensitivityTranslation", "IFCS core disabled - translation sensitivity modifier", 0, 10, 2, null),
                    Toggle("IFCS_Setting_EspEnabled", "Defaults - flight - ESP on", 1, "Enhanced Stick Precision."),
                    Number("PilotEspStrength", "Pilot - ESP - assist strength", 0, 1, 1, "The game's menu slider ends at 1.00 (in-game test: 1.05 and 1.21 show as 1.00)."),
                    Number("PilotEspDampeningAmount", "Pilot - ESP - dampening amount", 0, 1, 0.15, null),
                    Toggle("PilotEspDampeningIncludeRoll", "Pilot - ESP - dampening includes roll", 1, null),
                    Toggle("TurretsEspDefaultOn", "Turret E.S.P. enabled by default", 1, null),
                    Number("TurretsEspStrength", "Turret E.S.P. strength", 0, 2, 1, null),
                    Number("TurretsEspDampening", "Turret E.S.P. dampening", 0, 2, 1, null),
                    Toggle("IFCS_Setting_StickyThrottleEnabled", "Defaults - sticky throttle", 0, null),
                    Toggle("IFCS_Setting_SpeedLimiterOn", "Defaults - speed limiter on", 1, null),
                    Toggle("IFCS_Setting_CoupledEnabled", "Defaults - coupled mode", 1, null),
                    Toggle("IFCS_Setting_GSafeEnabled", "Defaults - G-safe", 1, null),
                    Toggle("IFCS_Setting_GSafeDisabledByBoost", "Defaults - boost disables G-safe", 1, null),
                    Toggle("IFCS_Setting_AutoPrecisionModeEnabled", "Defaults - auto precision mode", 1, null),
                    Toggle("IFCS_Setting_SpacebrakeEnablesBoost", "Defaults - space brake enables boost", 1, null),
                    Toggle("InvertYController", "Invert flight controller (Y)", 0, "cl_invertController: gamepad and view pitch (option groups with invert_cvar); joysticks use the option group's invert.")
                }
            };
        }

        private static FfbParameter Toggle(string key, string label, double def, string help)
        {
            return new FfbParameter { Key = key, Label = label, Toggle = true, Min = 0, Max = 1, Default = def, GameWide = true, Help = help };
        }

        private static FfbParameter Number(string key, string label, double min, double max, double def, string help)
        {
            return new FfbParameter { Key = key, Label = label, Min = min, Max = max, Default = def, GameWide = true, Help = help };
        }

        public static string Render(FfbParameter parameter, double value)
        {
            if (parameter != null && parameter.Toggle)
                return value != 0 ? "1" : "0";
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        // The model's values attributes.xml holds (engine sync compares them with the profile's).
        public static Dictionary<string, double> Read(string text)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attr in Regex.Matches(text ?? string.Empty, "<Attr\\s+name=\"(?<n>[^\"]*)\"[^>]*?\\svalue=\"(?<v>[^\"]*)\""))
            {
                FfbParameter parameter = FfbSettings.Parameter(Game, attr.Groups["n"].Value);
                double value;
                if (parameter != null && double.TryParse(attr.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    values[parameter.Key] = value;
            }
            return values;
        }

        // Sets the values in place: an existing <Attr name="K" value="..."/> gets the new value, a missing one a new line
        // at its sorted place (the game sorts by name). Every other byte stays. Only names of the model are written.
        public static bool Apply(ref string text, Dictionary<string, double> values, List<string> changes)
        {
            if (string.IsNullOrEmpty(text) || values == null || values.Count == 0)
                return false;
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            bool changed = false;
            foreach (KeyValuePair<string, double> value in values.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                FfbParameter parameter = FfbSettings.Parameter(Game, value.Key);
                if (parameter == null)
                    continue;
                string rendered = Render(parameter, value.Value);
                Match existing = Regex.Match(text, "<Attr\\s+name=\"" + Regex.Escape(parameter.Key) + "\"(?<mid>[^>]*?)\\svalue=\"(?<v>[^\"]*)\"");
                if (existing.Success)
                {
                    Group v = existing.Groups["v"];
                    double parsed;
                    if (string.Equals(v.Value, rendered, StringComparison.Ordinal) ||
                        (double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && Math.Abs(parsed - value.Value) < 1e-9))
                        continue;
                    text = text.Substring(0, v.Index) + rendered + text.Substring(v.Index + v.Length);
                }
                else
                {
                    int close = text.LastIndexOf("</Attributes>", StringComparison.Ordinal);
                    if (close < 0)
                        return changed;
                    string indent = " ";
                    Match lineIndent = Regex.Match(text, "\\n(?<i>[ \\t]*)<Attr\\s");
                    if (lineIndent.Success)
                        indent = lineIndent.Groups["i"].Value;
                    string line = indent + "<Attr name=\"" + parameter.Key + "\" value=\"" + rendered + "\"/>" + nl;
                    // Before the first line whose name sorts after the new one, else before </Attributes>.
                    int at = -1;
                    foreach (Match attr in Regex.Matches(text, "\\n(?<i>[ \\t]*)<Attr\\s+name=\"(?<n>[^\"]*)\""))
                    {
                        if (string.Compare(attr.Groups["n"].Value, parameter.Key, StringComparison.OrdinalIgnoreCase) > 0)
                        {
                            at = attr.Index + 1;
                            break;
                        }
                    }
                    if (at < 0)
                    {
                        at = text.LastIndexOf('\n', close) + 1;
                        if (at <= 0 || text.Substring(at, close - at).Trim().Length > 0)
                        {
                            text = text.Substring(0, close) + nl + line + text.Substring(close);
                            changed = true;
                            changes.Add(parameter.Key + " = " + rendered);
                            continue;
                        }
                    }
                    text = text.Substring(0, at) + line + text.Substring(at);
                }
                changed = true;
                changes.Add(parameter.Key + " = " + rendered);
            }
            return changed;
        }
    }
}
