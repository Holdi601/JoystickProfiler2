#if NET
#nullable disable
#endif
using System;

namespace JoyPro.Engine.Axis
{
    // Arma 3: per device and physical axis in <Name>.Arma3Profile, class JoysticksList (docs/games/arma3.md):
    // sensitivity[16] (a = + half, a+8 = - half, -1..1), deadZone[8] (0..0.1), steepness[8] (Customize combo
    // 1.0/1.2/1.5/1.9/2.4/3.0). Read from arma3_x64.exe (2026-10-05, static):
    //   dead zone: DIPROP_DEADZONE = (int)(deadZone * 10000) per axis (0x1410414e0), DirectInput rescales the rest [INF];
    //   half value v = clamp(+-x, 0, 1); full axis (both halves on one action) v = (pos - neg + 1) / 2 (0x141078160);
    //   s = sensitivity[local] (local < 16, else [a]); s >= 0: f = s * v^(1/3) + (1 - s) * v;
    //   s < 0: f = |s| * v^3 + (1 - |s|) * v; out = clamp(steepness[a] * f, 0, 1) (0x1410715e0).
    internal static class Arma3AxisModel
    {
        public const string Id = "arma3";
        public static readonly double[] SteepnessChoices = { 1.0, 1.2, 1.5, 1.9, 2.4, 3.0 };
        // The engine's dead zone where a profile has no (or a short) deadZone[] (axes X, Y, Z, RX, RY, RZ, S1, S2).
        public static readonly double[] DefaultDeadZones = { 0.07, 0.07, 0, 0.07, 0.07, 0.07, 0, 0 };

        [GameAxisModel("Arma3")]
        private static AxisModel Build()
        {
            return new AxisModel
            {
                Id = Id,
                Title = "Arma 3",
                Description = "Per device and physical axis, for every vehicle and on foot (Controls > Controllers > Customize): sensitivity per half axis (-1 cubic, 0 linear, +1 cube root), dead zone (max 10 %), steepness (output multiplier, saturates earlier). Formula read from arma3_x64.exe; the dead zone rescaling is DirectInput's. Arma 3 does not take over the base curve: only values set for Arma 3 (or imported) are written.",
                Parameters =
                {
                    new AxisParameter { Key = "sensitivityPos", Label = "Sensitivity, + half (right / back)", Kind = AxisParameterKind.Number, Min = -1, Max = 1, Step = 0.01, Default = 0,
                        Help = "sensitivity[a]: -1 = cubic (fine around the centre), 0 = linear, +1 = cube root (quick around the centre). A full axis (throttle on one action) uses this value for the whole travel." },
                    new AxisParameter { Key = "sensitivityNeg", Label = "Sensitivity, - half (left / forward)", Kind = AxisParameterKind.Number, Min = -1, Max = 1, Step = 0.01, Default = 0,
                        Help = "sensitivity[a+8], the half a stick pushed forward or left reports; unset = the + half. The game's Customize dialog has one slider and saves it to both halves on OK." },
                    new AxisParameter { Key = "deadZone", Label = "Dead zone (max 10 %)", Kind = AxisParameterKind.Number, Min = 0, Max = 0.1, Step = 0.01, Default = 0.07,
                        Help = "Share of the half travel around the centre; the game's default is 0.07 on X, Y, RX, RY, RZ and 0 on Z and the sliders." },
                    new AxisParameter { Key = "steepness", Label = "Steepness", Kind = AxisParameterKind.Number, Min = 1, Max = 3, Step = 0.1, Default = 1,
                        Help = "Multiplies the output (full deflection is reached earlier). The game offers 1.0, 1.2, 1.5, 1.9, 2.4, 3.0; other values are written as the nearest of these." },
                    new AxisParameter { Key = "slider", Label = "Full axis (throttle on one action)", Kind = AxisParameterKind.Toggle },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = Evaluate
            };
        }

        public static double SnapSteepness(double value)
        {
            double best = SteepnessChoices[0];
            foreach (double choice in SteepnessChoices)
            {
                if (Math.Abs(choice - value) < Math.Abs(best - value))
                    best = choice;
            }
            return best;
        }

        public static double Curve(double v, double s, double k)
        {
            v = Math.Max(0.0, Math.Min(1.0, v));
            s = Math.Max(-1.0, Math.Min(1.0, s));
            double f = s >= 0 ? s * Math.Pow(v, 1.0 / 3.0) + (1.0 - s) * v : -s * v * v * v + (1.0 + s) * v;
            return Math.Max(0.0, Math.Min(1.0, k * f));
        }

        public static double Evaluate(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            double d = Math.Truncate(Math.Max(0, t.NativeOr("deadZone", 0.07)) * 10000.0) / 10000.0;
            double u = Math.Abs(x);
            double xd = u <= d || d >= 1 ? 0.0 : Math.Sign(x) * (u - d) / (1.0 - d);
            double k = SnapSteepness(t.NativeOr("steepness", 1));
            double y;
            if (t.Slider)
                y = 2.0 * Curve((xd + 1.0) / 2.0, t.NativeOr("sensitivityPos", 0), k) - 1.0;
            else
                y = xd >= 0 ? Curve(xd, t.NativeOr("sensitivityPos", 0), k) : -Curve(-xd, t.NativeOr("sensitivityNeg", t.NativeOr("sensitivityPos", 0)), k);
            return t.Inverted ? -y : y;
        }
    }
}
