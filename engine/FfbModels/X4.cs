using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Axis
{
    // X4: Foundations' game-wide control options (no joystick force feedback; rumble is XInput only). Settings > Controls
    // keeps them in inputmap.xml <config><param name="..." value="..."/> and Settings > Game in config.xml elements.
    // Inversion is one setting per control for all devices; JoyPro derives it from the relations (their invert XOR the
    // direction X4's own HOTAS maps use) unless it is set here. See docs/games/x4.md "Axis settings".
    public static class X4GameSettings
    {
        public const string Game = "X4";

        // JoyPro key -> inputmap.xml <param name>.
        public static readonly Dictionary<string, string> Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "deadzone", "deadzone" },
            { "bidirectionalThrottle", "bidirectionalthrottle" },
            { "adaptiveSteering", "stick_steering_adaptive" },
            { "invertSteeringYaw", "invert_steering_yaw" },
            { "invertSteeringPitch", "invert_steering_pitch" },
            { "invertSteeringRoll", "invert_steering_roll" },
            { "invertThrottle", "invert_throttle" },
            { "invertStrafeLeftRight", "invert_strafe_left_right" },
            { "invertStrafeUpDown", "invert_strafe_up_down" }
        };

        // JoyPro key -> config.xml element.
        public static readonly Dictionary<string, string> ConfigElements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "rumbleIntensity", "rumbleintensity" },
            { "autoRoll", "autoroll" },
            { "gamepadMode", "gamepadmode" }
        };

        // Flight axis (inputmap range id) -> its invert param and the value X4's shipped HOTAS maps
        // (libraries\inputmap_defaults_*.xml) use for it: pitch, throttle and roll are "Inverted" there.
        public static readonly Dictionary<string, KeyValuePair<string, bool>> RangeInvert = new Dictionary<string, KeyValuePair<string, bool>>(StringComparer.Ordinal)
        {
            { "INPUT_RANGE_STEERING_PRIMARY", new KeyValuePair<string, bool>("invertSteeringYaw", false) },
            { "INPUT_RANGE_STEERING_SECONDARY", new KeyValuePair<string, bool>("invertSteeringRoll", true) },
            { "INPUT_RANGE_STEERING_PITCH", new KeyValuePair<string, bool>("invertSteeringPitch", true) },
            { "INPUT_RANGE_THROTTLE", new KeyValuePair<string, bool>("invertThrottle", true) },
            { "INPUT_RANGE_STRAFE_LEFT_RIGHT", new KeyValuePair<string, bool>("invertStrafeLeftRight", false) },
            { "INPUT_RANGE_STRAFE_UP_DOWN", new KeyValuePair<string, bool>("invertStrafeUpDown", false) }
        };

        private const string InvertHelp = " As the game shows it (on = Inverted). Unset: JoyPro derives it from the relations on this control (their invert XOR X4's HOTAS default) when they agree. Also inverts a gamepad on this control.";

        [GameFfbModel]
        private static FfbModel Model()
        {
            return new FfbModel
            {
                Game = Game,
                Title = "X4: Foundations",
                Description = "Game-wide control options (device *): Settings > Controls (inputmap.xml <config>) and Settings > Game (config.xml), written by the x4 export. X4 has no joystick force feedback; rumble is for gamepads.",
                Parameters =
                {
                    new FfbParameter { Key = "deadzone", Label = "Joystick dead zone (%)", Min = 0, Max = 100, Default = 10, GameWide = true,
                        Help = "One dead zone for every axis of every joystick (DirectInput dead zone, % of the half travel)." },
                    new FfbParameter { Key = "bidirectionalThrottle", Label = "Bidirectional throttle", Toggle = true, Default = 1, GameWide = true,
                        Help = "Off: the throttle axis only flies forwards (0..100 % over the whole travel). Unset: follows the throttle relation (one-sided = off, centred = on)." },
                    new FfbParameter { Key = "adaptiveSteering", Label = "Adaptive steering (controller / joystick)", Toggle = true, Default = 0, GameWide = true,
                        Help = "The primary steering axis yaws or rolls depending on the ship." },
                    new FfbParameter { Key = "invertSteeringYaw", Label = "Invert steer left-right", Toggle = true, Default = 0, GameWide = true, Help = "Primary steering axis." + InvertHelp },
                    new FfbParameter { Key = "invertSteeringPitch", Label = "Invert steer up-down", Toggle = true, Default = 0, GameWide = true, Help = InvertHelp.Trim() },
                    new FfbParameter { Key = "invertSteeringRoll", Label = "Invert roll", Toggle = true, Default = 1, GameWide = true, Help = "Secondary steering axis." + InvertHelp },
                    new FfbParameter { Key = "invertThrottle", Label = "Invert throttle", Toggle = true, Default = 0, GameWide = true, Help = InvertHelp.Trim() },
                    new FfbParameter { Key = "invertStrafeLeftRight", Label = "Invert strafe left-right", Toggle = true, Default = 0, GameWide = true, Help = InvertHelp.Trim() },
                    new FfbParameter { Key = "invertStrafeUpDown", Label = "Invert strafe up-down", Toggle = true, Default = 0, GameWide = true, Help = InvertHelp.Trim() },
                    new FfbParameter { Key = "rumbleIntensity", Label = "Rumble (%)", Min = 0, Max = 100, Default = 100, GameWide = true,
                        Help = "Settings > Game > Rumble (gamepads), config.xml rumbleintensity 0..1." },
                    new FfbParameter { Key = "autoRoll", Label = "Auto-roll", Toggle = true, Default = 1, GameWide = true, Help = "Settings > Game: the ship levels its roll on its own." },
                    new FfbParameter { Key = "gamepadMode", Label = "Gamepad mode", Min = 0, Max = 2, Default = 2, GameWide = true,
                        Help = "Settings > Controls: 0 Off, 1 for all controllers/joysticks, 2 only with controllers." }
                }
            };
        }

        public static bool IsToggle(string key)
        {
            FfbParameter parameter = FfbSettings.Parameter(Game, key);
            return parameter != null && parameter.Toggle;
        }

        // inputmap.xml value text: true/false, the dead zone as a whole percent.
        public static string EncodeParam(string key, double value)
        {
            if (IsToggle(key))
                return value != 0 ? "true" : "false";
            return ((int)Math.Round(Math.Max(0, Math.Min(100, value)))).ToString(CultureInfo.InvariantCulture);
        }

        // config.xml text as X4 writes it: rumble "1.00", booleans "true", the gamepad mode as a number.
        public static string EncodeConfig(string key, double value)
        {
            if (IsToggle(key))
                return value != 0 ? "true" : "false";
            if (string.Equals(key, "rumbleIntensity", StringComparison.OrdinalIgnoreCase))
                return (Math.Max(0, Math.Min(100, value)) / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            return ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        }

        public static double? Decode(string key, string text, bool config)
        {
            text = (text ?? string.Empty).Trim();
            if (IsToggle(key))
                return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1" ? 1 : string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0" ? 0 : (double?)null;
            double number;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return null;
            return config && string.Equals(key, "rumbleIntensity", StringComparison.OrdinalIgnoreCase) ? Math.Round(number * 100.0, 4) : number;
        }

        // config.xml: set the inner text of the first <element> in place; the rest of the file keeps its bytes.
        public static bool ApplyConfig(ref string text, Dictionary<string, double> values, List<string> changes, List<string> warnings)
        {
            bool changed = false;
            foreach (KeyValuePair<string, double> value in values.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                string element;
                if (!ConfigElements.TryGetValue(value.Key, out element))
                    continue;
                Match match = Regex.Match(text, "<" + element + ">([^<]*)</" + element + ">");
                if (!match.Success)
                {
                    warnings.Add("config.xml has no <" + element + ">; " + value.Key + " was not written (start X4 once).");
                    continue;
                }
                double? current = Decode(value.Key, match.Groups[1].Value, true);
                if (current.HasValue && Math.Abs(current.Value - value.Value) < 1e-6)
                    continue;
                string rendered = EncodeConfig(value.Key, value.Value);
                Group inner = match.Groups[1];
                text = text.Substring(0, inner.Index) + rendered + text.Substring(inner.Index + inner.Length);
                changes.Add("config.xml " + element + " = " + rendered);
                changed = true;
            }
            return changed;
        }
    }
}
