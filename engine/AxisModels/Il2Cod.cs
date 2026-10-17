#if NET
#nullable disable
#endif
using System;
using System.Globalization;

// IL-2 Sturmovik: Cliffs of Dover (+ Blitz): Options > Controls axis settings, per axis command in confUser.ini
// [Config pilotMove] "<command>=<values>". Three values on the flight controls (aileron, elevator, rudder):
// sensitivity (0 linear .. 1 most curved), dead zone at the centre, dead zone at the ends; two values on the other
// axes: dead zone at the low and at the high end; five on the throttles (meaning not known; kept as they are). Slider
// positions 0..1. From public user files and forum explanations of the sliders; the response formula of the preview is
// an assumption. NOT YET VERIFIED on a real install (docs/games/il2-cliffs-of-dover.md).
namespace JoyPro.Engine.Axis
{
    public static class Il2CodAxisModel
    {
        public const string Id = "il2cod";
        public const string Sensitivity = "sensitivity";
        public const string DeadZoneCentre = "deadZoneCentre";
        public const string DeadZoneEnd = "deadZoneEnd";
        public const string DeadZoneLow = "deadZoneLow";
        public const string DeadZoneHigh = "deadZoneHigh";
        public const int RawCount = 5;

        public static string RawKey(int i)
        {
            return "raw" + i.ToString(CultureInfo.InvariantCulture);
        }

        [GameAxisModel("IL2CoD", "IL2CoDBlitz")]
        private static AxisModel Build()
        {
            AxisModel model = new AxisModel
            {
                Id = Id,
                Title = "IL-2 Sturmovik: Cliffs of Dover",
                Description = "Per axis command (confUser.ini [Config pilotMove]): stick, rudder: sensitivity (0 linear .. 1 most curved), centre and end dead zone; other axes: low and high end dead zone; " +
                              "throttles: five values kept as the game wrote them. Slider positions 0..1. The preview assumes a linear-to-cubic blend (not yet verified in game).",
                Evaluate = Evaluate,
                Fit = Fit
            };
            model.Parameters.Add(new AxisParameter { Key = Sensitivity, Label = "Sensitivity (aileron, elevator, rudder)", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 0,
                Help = "0 = linear, 1 = flattest around the centre." });
            model.Parameters.Add(new AxisParameter { Key = DeadZoneCentre, Label = "Dead zone: centre", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 0 });
            model.Parameters.Add(new AxisParameter { Key = DeadZoneEnd, Label = "Dead zone: ends", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 0 });
            model.Parameters.Add(new AxisParameter { Key = DeadZoneLow, Label = "Dead zone: low end (other axes)", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 0 });
            model.Parameters.Add(new AxisParameter { Key = DeadZoneHigh, Label = "Dead zone: high end (other axes)", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 0 });
            for (int i = 0; i < RawCount; i++)
                model.Parameters.Add(new AxisParameter { Key = RawKey(i), Label = "Throttle value " + (i + 1).ToString(CultureInfo.InvariantCulture) + " (meaning not known)", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 0,
                    Help = "The throttle lines hold five values; JoyPro keeps them as imported." });
            model.Parameters.Add(new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle });
            return model;
        }

        private static double Clamp01(double value)
        {
            return Math.Max(0.0, Math.Min(1.0, value));
        }

        // Assumption: dead zones cut and the rest is rescaled; sensitivity blends linear and cubic.
        public static double Evaluate(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            if (t.Inverted)
                x = -x;
            if (t.Slider)
            {
                double low = Clamp01(t.NativeOr(DeadZoneLow, 0));
                double high = Clamp01(t.NativeOr(DeadZoneHigh, 0));
                double u = (x + 1.0) / 2.0;
                double span = 1.0 - low - high;
                double v = span <= 1e-9 ? (u >= 0.5 ? 1.0 : 0.0) : Clamp01((u - low) / span);
                return 2.0 * v - 1.0;
            }
            double s = Clamp01(t.NativeOr(Sensitivity, 0));
            double centre = Clamp01(t.NativeOr(DeadZoneCentre, 0));
            double end = Clamp01(t.NativeOr(DeadZoneEnd, 0));
            double a = Math.Abs(x);
            double room = 1.0 - centre - end;
            if (a <= centre)
                return 0.0;
            double w = room <= 1e-9 ? 1.0 : Clamp01((a - centre) / room);
            return Math.Sign(x) * ((1.0 - s) * w + s * w * w * w);
        }

        // The base (DCS) values as the sliders: dead zone -> centre dead zone (sliders: low end), 1 - saturation X ->
        // end dead zone, a positive curvature -> sensitivity. The throttle values are not derived (kept from the file).
        private static AxisTuning Fit(AxisTuning tuning, Func<double, double> target)
        {
            AxisTuning result = tuning.Clone();
            double sx = tuning.SaturationX <= 0 ? 1.0 : tuning.SaturationX;
            double curvature = tuning.Curvature == null || tuning.Curvature.Count != 1 ? 0.0 : tuning.Curvature[0];
            result.Native[Sensitivity] = Math.Round(Clamp01(curvature), 2);
            result.Native[DeadZoneCentre] = tuning.Slider ? 0.0 : Math.Round(Clamp01(tuning.Deadzone), 2);
            result.Native[DeadZoneEnd] = Math.Round(Clamp01(1.0 - sx), 2);
            result.Native[DeadZoneLow] = tuning.Slider ? Math.Round(Clamp01(tuning.Deadzone), 2) : 0.0;
            result.Native[DeadZoneHigh] = Math.Round(Clamp01(1.0 - sx), 2);
            return result;
        }
    }
}
