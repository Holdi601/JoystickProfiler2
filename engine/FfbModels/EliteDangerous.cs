using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JoyPro.Engine.Axis
{
    // Elite Dangerous has no force feedback for joysticks (no DirectInput effects in the exe). Its game-wide flight
    // control options are value elements of the Ship preset (<YawToRollMode Value="Bindings_YawIntoRollNone" />), next
    // to the binds JoyPro edits; these are the joystick-relevant ones (mouse, SRV, on-foot and gamepad rumble are not
    // managed; ThrottleRange follows the throttle relation's Slider). See docs/games/elite-dangerous.md.
    public static class EliteGameSettings
    {
        public const string Game = "EliteDangerous";

        private static readonly string[] YawModes = { "Bindings_YawIntoRollNone", "Bindings_YawIntoRollTime", "Bindings_YawIntoRollLowRoll" };
        // Keyboard throttle steps as the menu lists them ($ThrottleIncrement_Continuous/1in10/1in8/1in6/1in4;) and the
        // step stored for each (0 = continuous; the step sizes are inferred, check in the game).
        private static readonly double[] ThrottleSteps = { 0.0, 0.1, 0.125, 1.0 / 6.0, 0.25 };
        private static readonly double[] HeadlookSteps = { 0.0, 0.25, 1.0 / 3.0, 0.5 };

        // JoyPro key -> element of the .binds file.
        public static readonly Dictionary<string, string> Elements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "yawToRollMode", "YawToRollMode" },
            { "yawToRollModeFaOff", "YawToRollMode_FAOff" },
            { "yawToRollModeLanding", "YawToRollMode_Landing" },
            { "yawToRollSensitivity", "YawToRollSensitivity" },
            { "throttleIncrement", "ThrottleIncrement" },
            { "headlookAccumulate", "HeadlookMode" },
            { "headlookIncrement", "HeadlookIncrement" },
            { "headlookSensitivity", "HeadlookSensitivity" },
            { "headlookSmoothing", "HeadlookSmoothing" },
            { "headlookResetOnToggle", "HeadlookResetOnToggle" }
        };

        [GameFfbModel]
        private static FfbModel Model()
        {
            return new FfbModel
            {
                Game = Game,
                Title = "Elite Dangerous",
                Description = "Game-wide flight control options (device *), written into the Ship preset (Options\\Bindings\\<preset>.4.x.binds) by the elite export. No joystick force feedback in Elite. Throttle Axis Range follows the throttle relation's Slider (one-sided = Forward only).",
                Parameters =
                {
                    new FfbParameter { Key = "yawToRollMode", Label = "Yaw into roll", Min = 0, Max = 2, Default = 0, GameWide = true,
                        Help = "0 None, 1 Time (Bindings_YawIntoRollTime), 2 Low roll (Bindings_YawIntoRollLowRoll)." },
                    new FfbParameter { Key = "yawToRollModeFaOff", Label = "Yaw into roll, flight assist off", Min = 0, Max = 3, Default = 0, GameWide = true,
                        Help = "0 Same as normal (empty value), 1 None, 2 Time, 3 Low roll." },
                    new FfbParameter { Key = "yawToRollModeLanding", Label = "Yaw into roll, landing mode", Min = 0, Max = 3, Default = 0, GameWide = true,
                        Help = "0 Same as normal (empty value), 1 None, 2 Time, 3 Low roll." },
                    new FfbParameter { Key = "yawToRollSensitivity", Label = "Yaw into roll sensitivity", Min = 0, Max = 1, Default = 0.4, GameWide = true },
                    new FfbParameter { Key = "throttleIncrement", Label = "Keyboard throttle increment", Min = 0, Max = 4, Default = 0, GameWide = true,
                        Help = "0 Continuous, 1 one in 10, 2 one in 8, 3 one in 6, 4 one in 4 (stored as the step 0.1 .. 0.25)." },
                    new FfbParameter { Key = "headlookAccumulate", Label = "Head look mode: accumulate", Toggle = true, Default = 1, GameWide = true,
                        Help = "On: Accumulate (the view stays where the axis moved it), off: Direct (the view follows the axis)." },
                    new FfbParameter { Key = "headlookIncrement", Label = "Head look increment", Min = 0, Max = 3, Default = 0, GameWide = true,
                        Help = "0 Continuous, 1 one in 4, 2 one in 3, 3 one in 2." },
                    new FfbParameter { Key = "headlookSensitivity", Label = "Head look sensitivity", Min = 0, Max = 1, Default = 1, GameWide = true },
                    new FfbParameter { Key = "headlookSmoothing", Label = "Head look smoothing", Toggle = true, Default = 1, GameWide = true },
                    new FfbParameter { Key = "headlookResetOnToggle", Label = "Reset head look on toggle", Toggle = true, Default = 1, GameWide = true }
                }
            };
        }

        // The file text for a value, as the game writes it (floats are float32 with 8 decimals: 0.4 -> 0.40000001).
        public static string Encode(string key, double value)
        {
            int index = (int)Math.Round(value);
            switch (Elements[key])
            {
                case "YawToRollMode":
                    return YawModes[Math.Max(0, Math.Min(2, index))];
                case "YawToRollMode_FAOff":
                case "YawToRollMode_Landing":
                    return index <= 0 ? string.Empty : YawModes[Math.Min(2, index - 1)];
                case "ThrottleIncrement":
                    return Float(ThrottleSteps[Math.Max(0, Math.Min(ThrottleSteps.Length - 1, index))]);
                case "HeadlookIncrement":
                    return Float(HeadlookSteps[Math.Max(0, Math.Min(HeadlookSteps.Length - 1, index))]);
                case "HeadlookMode":
                    return value != 0 ? "Bindings_HeadlookModeAccumulate" : "Bindings_HeadlookModeDirect";
                case "HeadlookSmoothing":
                case "HeadlookResetOnToggle":
                    return value != 0 ? "1" : "0";
                default:
                    return Float(Math.Max(0, Math.Min(1, value)));
            }
        }

        // The JoyPro value of a file text; null when it is not one JoyPro knows.
        public static double? Decode(string key, string text)
        {
            text = (text ?? string.Empty).Trim();
            double number;
            bool isNumber = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            switch (Elements[key])
            {
                case "YawToRollMode":
                    {
                        int at = Array.IndexOf(YawModes, text);
                        return at >= 0 ? at : (double?)null;
                    }
                case "YawToRollMode_FAOff":
                case "YawToRollMode_Landing":
                    {
                        if (text.Length == 0)
                            return 0;
                        int at = Array.IndexOf(YawModes, text);
                        return at >= 0 ? at + 1 : (double?)null;
                    }
                case "ThrottleIncrement":
                    return isNumber ? Nearest(ThrottleSteps, number) : (double?)null;
                case "HeadlookIncrement":
                    return isNumber ? Nearest(HeadlookSteps, number) : (double?)null;
                case "HeadlookMode":
                    return text == "Bindings_HeadlookModeAccumulate" ? 1 : text == "Bindings_HeadlookModeDirect" ? 0 : (double?)null;
                default:
                    return isNumber ? number : (double?)null;
            }
        }

        // Same value as the file has (the game's float rounding and the empty/default forms count as equal).
        public static bool Same(string key, string fileText, double value)
        {
            double? current = Decode(key, fileText);
            return current.HasValue && Math.Abs(current.Value - value) < 1e-6;
        }

        private static double Nearest(double[] steps, double value)
        {
            int best = 0;
            for (int i = 1; i < steps.Length; i++)
            {
                if (Math.Abs(steps[i] - value) < Math.Abs(steps[best] - value))
                    best = i;
            }
            return best;
        }

        private static string Float(double value)
        {
            return ((double)(float)value).ToString("0.00000000", CultureInfo.InvariantCulture);
        }

        public static string Describe(Dictionary<string, double> values)
        {
            return string.Join(", ", values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Select(v => v.Key + "=" + AxisTuning.Num(v.Value)));
        }
    }
}
