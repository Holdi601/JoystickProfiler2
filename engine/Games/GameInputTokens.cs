using System;
using System.Globalization;

namespace JoyPro.Engine.Games
{
    internal enum JoyInputKind
    {
        None,
        Axis,
        Button,
        Pov
    }

    // A JoyPro joystick token decoded into DirectInput indices.
    internal struct JoyInput
    {
        public JoyInputKind Kind;
        // Axis: DIJOYSTATE2 slot 0..7 (X, Y, Z, Rx, Ry, Rz, Slider1, Slider2). Button: 0-based. Pov: 0-based hat.
        public int Index;
        // Pov only: 0..7 clockwise from up (U, UR, R, DR, D, DL, L, UL).
        public int Direction;
    }

    internal static class GameInputTokens
    {
        private static readonly string[] AxisTokens = { "JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_SLIDER2" };
        private static readonly string[] PovDirections = { "U", "UR", "R", "DR", "D", "DL", "L", "UL" };

        public static bool TryParse(string token, out JoyInput input)
        {
            input = new JoyInput();
            string value = (token ?? string.Empty).Trim().ToUpperInvariant();
            if (value.Length == 0)
                return false;

            for (int i = 0; i < AxisTokens.Length; i++)
            {
                if (value == AxisTokens[i])
                {
                    input.Kind = JoyInputKind.Axis;
                    input.Index = i;
                    return true;
                }
            }

            if (value.StartsWith("JOY_BTN_POV", StringComparison.Ordinal))
            {
                // JOY_BTN_POV1_U is the first hat (DCS convention); POV0 is accepted as the first hat too.
                string rest = value.Substring("JOY_BTN_POV".Length);
                int split = rest.IndexOf('_');
                int hat;
                if (split <= 0 || !int.TryParse(rest.Substring(0, split), NumberStyles.Integer, CultureInfo.InvariantCulture, out hat))
                    return false;
                int direction = Array.IndexOf(PovDirections, rest.Substring(split + 1));
                if (direction < 0)
                    return false;

                input.Kind = JoyInputKind.Pov;
                input.Index = Math.Max(0, hat - 1);
                input.Direction = direction;
                return true;
            }

            if (value.StartsWith("JOY_BTN", StringComparison.Ordinal))
            {
                int button;
                if (!int.TryParse(value.Substring("JOY_BTN".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out button) || button < 1)
                    return false;
                input.Kind = JoyInputKind.Button;
                input.Index = button - 1;
                return true;
            }

            return false;
        }

        public static string AxisToken(int axisIndex)
        {
            return axisIndex >= 0 && axisIndex < AxisTokens.Length ? AxisTokens[axisIndex] : string.Empty;
        }

        public static string ButtonToken(int zeroBasedButton)
        {
            return zeroBasedButton >= 0 ? "JOY_BTN" + (zeroBasedButton + 1).ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        public static string PovToken(int zeroBasedHat, int direction)
        {
            if (zeroBasedHat < 0 || direction < 0 || direction >= PovDirections.Length)
                return string.Empty;
            return "JOY_BTN_POV" + (zeroBasedHat + 1).ToString(CultureInfo.InvariantCulture) + "_" + PovDirections[direction];
        }

        public static string ToToken(JoyInput input)
        {
            switch (input.Kind)
            {
                case JoyInputKind.Axis: return AxisToken(input.Index);
                case JoyInputKind.Button: return ButtonToken(input.Index);
                case JoyInputKind.Pov: return PovToken(input.Index, input.Direction);
                default: return string.Empty;
            }
        }
    }
}
