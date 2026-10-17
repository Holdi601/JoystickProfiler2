#if NET
#nullable disable
#endif
using System;

namespace JoyPro.Engine.Axis
{
    // Arma 2 and Arma 2: Operation Arrowhead: per device and physical axis, sensitivity[16] in class JoysticksList
    // (docs/games/arma2.md). The Customize slider p (-1..1) is stored as s = 10^p: the dialog's bar shows log10 of the
    // file value (checked in game, OA, 2026-10-05: 0.2512 shows at -0.60, 0.6310 at -0.20). The game reads
    // sensitivity[a] for both halves (0x6f0aa9, 0x6f0ad5) and applies v^(1/s) = v^(10^-p) per half after the fixed
    // 7 % DirectInput dead zone on X, Y, Z, RX, RY, RZ (DIPROP_DEADZONE 700, 0x6da49c; the sliders have none). A full
    // axis (OA) is (f(pos) - f(neg) + 1) / 2, the same curve over the whole lever.
    internal static class Arma2AxisModel
    {
        public const string Id = "arma2";
        public const double DirectInputDeadZone = 0.07;

        [GameAxisModel("Arma2", "Arma2OA")]
        private static AxisModel Build()
        {
            return new AxisModel
            {
                Id = Id,
                Title = "Arma 2 / Operation Arrowhead",
                Description = "Per device and physical axis, for every vehicle and on foot (Controls > Controllers > Customize): one sensitivity slider p (-1..1 as the dialog shows it, stored as 10^p; output = v^(10^-p) per half axis) after the game's fixed 7 % dead zone (X, Y, Z, RX, RY, RZ; the preview assumes such an axis). Exact formula from ArmA2OA.exe. Arma 2 does not take over the base curve: only values set for Arma 2 (or imported) are written.",
                Parameters =
                {
                    new AxisParameter { Key = "sensitivity", Label = "Sensitivity slider", Kind = AxisParameterKind.Number, Min = -1, Max = 1, Step = 0.01, Default = 0,
                        Help = "The Customize slider: p = 0 linear; p > 0 (right) quicker around the centre (exponent 10^-p, down to 0.1); p < 0 (left) flatter around the centre (up to 10). The profile stores 10^p in sensitivity[a] and [a+8]." },
                    new AxisParameter { Key = "slider", Label = "Full axis (throttle on one action)", Kind = AxisParameterKind.Toggle },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = Evaluate
            };
        }

        public static double FileValue(double p)
        {
            p = Math.Max(-1.0, Math.Min(1.0, p));
            return Math.Pow(10.0, p);
        }

        public static double SliderValue(double fileValue)
        {
            return fileValue > 0 ? Math.Max(-1.0, Math.Min(1.0, Math.Log10(fileValue))) : 0.0;
        }

        public static double Evaluate(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            double u = Math.Abs(x);
            double v = u <= DirectInputDeadZone ? 0.0 : (u - DirectInputDeadZone) / (1.0 - DirectInputDeadZone);
            double y = Math.Sign(x) * Math.Pow(v, 1.0 / FileValue(t.NativeOr("sensitivity", 0)));
            return t.Inverted ? -y : y;
        }
    }
}
