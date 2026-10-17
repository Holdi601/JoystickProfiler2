#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;

// Falcon BMS 4.36+ axis response (docs/games/falcon-bms.md). Per physical axis the launcher's
// device file holds invert and two 4-step dropdowns that it writes to axismapping.dat as DirectInput-style ints
// (dead zone 0/100/500/1000, saturation -1/9500/9000/8500 of 10000); Falcon BMS User.cfg adds an exponential curve and
// an exponential saturation for pitch, roll, yaw and cursor X/Y (g_nAxisExp_AXIS_* 0..100, g_nAxisSat_AXIS_* 0..99).
namespace JoyPro.Engine.Axis
{
    public static class BmsAxisModel
    {
        public const string Id = "bms";
        public const string DeadzoneKey = "deadzoneStep";
        public const string SaturationKey = "saturationStep";
        public const string ExpKey = "axisExp";
        public const string ExpSatKey = "axisSat";

        public static readonly string[] StepNames = { "None", "Small", "Medium", "Large" };
        // What the launcher writes to axismapping.dat (OverrideSetting.GetAxDeadZoneByte / GetAxSaturationByte).
        public static readonly int[] DeadzoneInts = { 0, 100, 500, 1000 };
        public static readonly int[] SaturationInts = { -1, 9500, 9000, 8500 };

        [GameAxisModel("FalconBMS")]
        private static AxisModel Build()
        {
            return new AxisModel
            {
                Id = Id,
                Title = "Falcon BMS",
                Description = "Per physical axis (launcher device file): invert, dead zone and saturation in the launcher's steps (0 None, 1 Small, 2 Medium, 3 Large); one-sided axes (Slider: throttles, brakes, volumes, brightness, knobs) have no dead zone. Pitch, roll, yaw and cursor X/Y also have Falcon BMS User.cfg's exponential curve (g_nAxisExp_AXIS_*) and exponential saturation (g_nAxisSat_AXIS_*). Preview: dead zone/saturation as DirectInput applies its ints (UNVERIFIED in game), exponent as a linear-to-cubic blend and saturation as less travel to full output (described by the BMS forum, UNVERIFIED).",
                Parameters =
                {
                    new AxisParameter { Key = DeadzoneKey, Label = "Dead zone (launcher step)", Kind = AxisParameterKind.Number, Min = 0, Max = 3, Step = 1, Default = 0,
                        Help = "0 None, 1 Small (1 %), 2 Medium (5 %), 3 Large (10 %) of the half travel around the centre; axismapping.dat 0/100/500/1000. Not on one-sided axes." },
                    new AxisParameter { Key = SaturationKey, Label = "Saturation (launcher step)", Kind = AxisParameterKind.Number, Min = 0, Max = 3, Step = 1, Default = 0,
                        Help = "0 None, 1 Small, 2 Medium, 3 Large: axismapping.dat 9500/9000/8500, read as full output at 95/90/85 % of the half travel (the user manual says 1/5/10 %; UNVERIFIED)." },
                    new AxisParameter { Key = ExpKey, Label = "Exponential curve (pitch/roll/yaw/cursor)", Kind = AxisParameterKind.Number, Min = 0, Max = 100, Step = 1, Default = 0,
                        Help = "Falcon BMS User.cfg g_nAxisExp_AXIS_PITCH/ROLL/YAW/CURSOR_X/CURSOR_Y, 0 = off. Blends linear (1) to cubic (99) according to the BMS forum; formula UNVERIFIED." },
                    new AxisParameter { Key = ExpSatKey, Label = "Exponential saturation (pitch/roll/yaw/cursor)", Kind = AxisParameterKind.Number, Min = 0, Max = 99, Step = 1, Default = 0,
                        Help = "Falcon BMS User.cfg g_nAxisSat_AXIS_*, 0 = off; % less stick travel needed for full output (BMS forum; UNVERIFIED)." },
                    new AxisParameter { Key = "slider", Label = "One-sided axis (throttle, brakes, ...)", Kind = AxisParameterKind.Toggle },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = Evaluate,
                Fit = Fit
            };
        }

        // The launcher step of a tuning: its own value, else the nearest step of the base dead zone / saturation (what
        // JoyPro always wrote). One-sided axes have no dead zone.
        public static int DeadzoneStep(AxisTuning t)
        {
            if (t == null || t.Slider)
                return 0;
            double value;
            if (t.Native != null && t.Native.TryGetValue(DeadzoneKey, out value))
                return Clamp((int)Math.Round(value), 0, 3);
            return NearestDeadzoneStep(t.Deadzone);
        }

        public static int SaturationStep(AxisTuning t)
        {
            if (t == null)
                return 0;
            double value;
            if (t.Native != null && t.Native.TryGetValue(SaturationKey, out value))
                return Clamp((int)Math.Round(value), 0, 3);
            return NearestSaturationStep(t.SaturationX);
        }

        public static int Exp(AxisTuning t)
        {
            return t == null ? 0 : Clamp((int)Math.Round(t.NativeOr(ExpKey, 0)), 0, 100);
        }

        public static int ExpSat(AxisTuning t)
        {
            return t == null ? 0 : Clamp((int)Math.Round(t.NativeOr(ExpSatKey, 0)), 0, 99);
        }

        public static int NearestDeadzoneStep(double deadzone)
        {
            return deadzone < 0.005 ? 0 : deadzone < 0.03 ? 1 : deadzone < 0.075 ? 2 : 3;
        }

        public static int NearestSaturationStep(double saturation)
        {
            if (saturation <= 0)
                return 0;
            return saturation > 0.975 ? 0 : saturation > 0.925 ? 1 : saturation > 0.875 ? 2 : 3;
        }

        public static double DeadzoneFraction(int step)
        {
            return DeadzoneInts[Clamp(step, 0, 3)] / 10000.0;
        }

        public static double SaturationFraction(int step)
        {
            int value = SaturationInts[Clamp(step, 0, 3)];
            return value < 0 ? 1.0 : value / 10000.0;
        }

        // Stick position -1..1 (one-sided axes: -1 = start) -> what BMS uses. DirectInput applies dead zone and
        // saturation around the centre of the physical axis, also on one-sided axes; then the exponential saturation
        // (less travel to full output) and the linear-to-cubic exponent.
        public static double Evaluate(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            if (t.Inverted)
                x = -x;
            double sign = x < 0 ? -1.0 : 1.0;
            double u = Math.Abs(x);
            double dz = DeadzoneFraction(DeadzoneStep(t));
            double sat = SaturationFraction(SaturationStep(t));
            double v = u <= dz ? 0.0 : u >= sat ? 1.0 : (u - dz) / (sat - dz);
            double s = ExpSat(t) / 100.0;
            if (s > 0)
                v = Math.Min(1.0, v / (1.0 - s));
            double e = Exp(t) / 100.0;
            if (e > 0)
                v = (1.0 - e) * v + e * v * v * v;
            return sign * v;
        }

        // The base curve in BMS terms: nearest launcher steps (a slider's saturation is a share of the whole travel in
        // the base, of the half travel in BMS). The base curvature is NOT turned into g_nAxisExp yet: the BMS forum calls
        // that curve "very aggressive" (2-3 recommended), so a fitted value from an unverified formula could make the
        // stick unflyable. Once tools\e2e\games\BmsSettings.ps1 has measured the formula, fit the exponent here
        // (least squares on 20 points, as FitKorea).
        private static AxisTuning Fit(AxisTuning tuning, Func<double, double> target)
        {
            AxisTuning result = tuning.Clone();
            double sx = tuning.SaturationX <= 0 ? 1.0 : tuning.SaturationX;
            result.Native[DeadzoneKey] = tuning.Slider ? 0 : NearestDeadzoneStep(tuning.Deadzone);
            result.Native[SaturationKey] = NearestSaturationStep(tuning.Slider ? 2.0 * sx - 1.0 : sx);
            // No exponent keys: the export leaves User.cfg's curve lines alone when the tuning does not set them.
            result.Native.Remove(ExpKey);
            result.Native.Remove(ExpSatKey);
            return result;
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
