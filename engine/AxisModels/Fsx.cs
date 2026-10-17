#if NET
#nullable disable
#endif
using System;

// FSX / FSX: Steam Edition / Prepar3D v4-v6: Controls > Calibration per device axis in Controls\Standard.xml:
// <AxScale> = the Sensitivity slider (0..127, negative = reversed axis), <AxNull> = the Null zone slider (0..127,
// left out at 0). Values in the file's units. Public user files and forum notes (docs/games/fsx-p3d.md); the preview's
// formula (null zone cut and rescaled, output times scale / 127, clipped) is an assumption. NOT YET VERIFIED on a real
// install. Plain math, C# 7.3: the GUI links this file.
namespace JoyPro.Engine.Axis
{
    public static class FsxAxisModel
    {
        public const string Id = "fsx";
        public const string Scale = "scale";
        public const string NullZone = "nullZone";
        public const int Max = 127;

        [GameAxisModel("FSX", "FSXSE", "P3D4", "P3D5", "P3D6")]
        private static AxisModel Build()
        {
            return new AxisModel
            {
                Id = Id,
                Title = "FSX / Prepar3D",
                Description = "Per device axis (Standard.xml <AxScale>, <AxNull>): sensitivity 0..127 (the file stores a reversed axis as a negative value) and null zone 0..127. " +
                              "The preview assumes a rescaled null zone and an output scaled by sensitivity / 127 (not yet verified in the sim).",
                Parameters =
                {
                    new AxisParameter { Key = Scale, Label = "Sensitivity", Kind = AxisParameterKind.Number, Min = 0, Max = Max, Step = 1, Default = Max,
                        Help = "The Calibration page's sensitivity slider (127 = full). Invert is written as a negative value." },
                    new AxisParameter { Key = NullZone, Label = "Null zone", Kind = AxisParameterKind.Number, Min = 0, Max = Max, Step = 1, Default = 0,
                        Help = "The Calibration page's null zone slider (0 = none)." },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = Evaluate,
                Fit = Fit,
                SharedPerAxisKeys = { Scale, NullZone }
            };
        }

        public static int ScaleOf(AxisTuning t)
        {
            return (int)Math.Max(0, Math.Min(Max, Math.Round(Math.Abs(t.NativeOr(Scale, Max)))));
        }

        public static int NullOf(AxisTuning t)
        {
            return (int)Math.Max(0, Math.Min(Max, Math.Round(t.NativeOr(NullZone, 0))));
        }

        public static double Evaluate(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            if (t.Inverted)
                x = -x;
            double nz = NullOf(t) / (double)Max;
            double a = Math.Abs(x);
            if (a <= nz)
                return 0.0;
            double v = nz >= 1.0 ? 0.0 : (a - nz) / (1.0 - nz);
            return Math.Sign(x) * Math.Min(1.0, v * ScaleOf(t) / (double)Max);
        }

        // The base (DCS) values as the sliders: dead zone -> null zone, saturation Y -> sensitivity (the curve itself
        // has no FSX equivalent).
        private static AxisTuning Fit(AxisTuning tuning, Func<double, double> target)
        {
            AxisTuning result = tuning.Clone();
            double sy = tuning.SaturationY <= 0 ? 1.0 : Math.Min(1.0, tuning.SaturationY);
            result.Native[NullZone] = Math.Round(Math.Max(0.0, Math.Min(1.0, tuning.Deadzone)) * Max);
            result.Native[Scale] = Math.Round(sy * Max);
            return result;
        }
    }
}
