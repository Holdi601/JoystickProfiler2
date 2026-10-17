#if NET
#nullable disable
#endif
using System;
using System.Globalization;

// IL-2 Sturmovik: 1946: Hardware Setup > Input, per device axis, stored in conf.ini [rts_joystick] (or [rts_joystick<k>]
// for [rts] JoyProfile=k) as "1<axis>[<device>]=<dead band> <10 curve points> <filter> <one-sided>". Values in the file's
// units: dead band in raw units of the half travel (+-1023, the UI offers 0..50), curve points as percent of the linear
// output at 10 %, 20 % .. 100 % stick (all 100 = linear), filter 0..100, one-sided 0 centred / 1 one-sided / 2 one-sided
// reversed. From the game's input code as published by modders and real conf.ini files (docs/games/il2-1946.md);
// NOT YET VERIFIED on a real install. Plain math, C# 7.3: the GUI links this file.
namespace JoyPro.Engine.Axis
{
    public static class Il21946AxisModel
    {
        public const string Id = "il21946";
        public const string DeadBand = "deadBand";
        public const string Filter = "filter";
        public const string OneSided = "oneSided";
        public const int DeadBandMax = 50;
        public const int Points = 10;

        public static string PointKey(int k)
        {
            return "c" + k.ToString(CultureInfo.InvariantCulture);
        }

        [GameAxisModel("IL21946")]
        private static AxisModel Build()
        {
            AxisModel model = new AxisModel
            {
                Id = Id,
                Title = "IL-2 Sturmovik: 1946",
                Description = "Per device axis (conf.ini [rts_joystick], shared by every command on that axis): dead band (raw units of the half travel, 0..50 of 1023, cut without rescaling), " +
                              "10 curve points at 10 %..100 % stick as percent of the linear output (100 = linear, the curve can only be at or below linear), filter (smoothing 0..100) and " +
                              "one-sided axis (throttle type). Formula from the game's input code (not yet verified in game).",
                Evaluate = Evaluate,
                Fit = Fit
            };
            model.Parameters.Add(new AxisParameter { Key = DeadBand, Label = "Dead band (of 1023)", Kind = AxisParameterKind.Number, Min = 0, Max = DeadBandMax, Step = 1, Default = 0,
                Help = "Raw stick values closer to the centre than this (of 1023 per half) give 0; the rest is not rescaled." });
            for (int k = 1; k <= Points; k++)
                model.Parameters.Add(new AxisParameter { Key = PointKey(k), Label = "Curve at " + (k * 10).ToString(CultureInfo.InvariantCulture) + " %", Kind = AxisParameterKind.Number, Min = 0, Max = 100, Step = 1, Default = 100,
                    Help = "Output at this stick position in percent of the linear output (100 = linear)." });
            model.Parameters.Add(new AxisParameter { Key = Filter, Label = "Filter", Kind = AxisParameterKind.Number, Min = 0, Max = 100, Step = 1, Default = 0,
                Help = "Smoothing over time (the Input page shows it as 0..10); not in the preview." });
            model.Parameters.Add(new AxisParameter { Key = OneSided, Label = "One-sided axis (0 centred, 1 one-sided, 2 one-sided reversed)", Kind = AxisParameterKind.Number, Min = 0, Max = 2, Step = 1, Default = 0,
                Help = "1/2: the whole travel maps to the output range (throttle type). The game stores 2 when the axis was bound with a '-' command." });
            model.Parameters.Add(new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle });
            model.SharedPerAxisKeys.Add(DeadBand);
            for (int k = 1; k <= Points; k++)
                model.SharedPerAxisKeys.Add(PointKey(k));
            model.SharedPerAxisKeys.Add(Filter);
            model.SharedPerAxisKeys.Add(OneSided);
            return model;
        }

        public static int DeadBandOf(AxisTuning t)
        {
            return (int)Math.Max(0, Math.Min(DeadBandMax, Math.Round(t.NativeOr(DeadBand, 0))));
        }

        public static int PointOf(AxisTuning t, int k)
        {
            return (int)Math.Max(0, Math.Min(100, Math.Round(t.NativeOr(PointKey(k), 100))));
        }

        public static int FilterOf(AxisTuning t)
        {
            return (int)Math.Max(0, Math.Min(100, Math.Round(t.NativeOr(Filter, 0))));
        }

        public static int OneSidedOf(AxisTuning t)
        {
            double fallback = t.Slider ? (t.Inverted ? 2 : 1) : 0;
            return (int)Math.Max(0, Math.Min(2, Math.Round(t.NativeOr(OneSided, fallback))));
        }

        // The 13 numbers of the conf.ini line.
        public static int[] Values(AxisTuning t)
        {
            int[] values = new int[13];
            values[0] = DeadBandOf(t);
            for (int k = 1; k <= Points; k++)
                values[k] = PointOf(t, k);
            values[11] = FilterOf(t);
            values[12] = OneSidedOf(t);
            return values;
        }

        public static void SetValues(AxisTuning t, int[] values)
        {
            t.Native[DeadBand] = values[0];
            for (int k = 1; k <= Points; k++)
                t.Native[PointKey(k)] = values[k];
            t.Native[Filter] = values[11];
            t.Native[OneSided] = values[12];
        }

        public static bool IsDefault(int[] values)
        {
            if (values[0] != 0 || values[11] != 0 || values[12] != 0)
                return false;
            for (int k = 1; k <= Points; k++)
            {
                if (values[k] != 100)
                    return false;
            }
            return true;
        }

        // The game's piecewise curve: point k is the output at k * 10 % as percent of the linear value there.
        private static double Curve(AxisTuning t, double u)
        {
            u = Math.Max(0.0, Math.Min(1.0, u)) * 10.0;
            int i = Math.Min(9, (int)Math.Floor(u));
            double f1 = i == 0 ? 0.0 : PointOf(t, i) * i / 10.0;
            double f2 = PointOf(t, i + 1) * (i + 1) / 10.0;
            return (f1 + (f2 - f1) * (u - i)) / 100.0;
        }

        // Stick position -1..1 -> output -1..1. Centred: |raw| below the dead band gives 0 (no rescaling), else the curve
        // of |x|. One-sided: the whole travel (-1..1) is 0..1 of the curve, output -1..1; reversed when the line says 2 or
        // the command is inverted ('-'; the game sets both together, the preview reverses once).
        public static double Evaluate(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            int oneSided = OneSidedOf(t);
            double raw = x * 1023.0;
            if (oneSided == 0)
            {
                if (t.Inverted)
                    x = -x;
                if (Math.Abs(raw) < DeadBandOf(t))
                    return 0.0;
                return Math.Sign(x) * Curve(t, Math.Abs(x));
            }
            double y;
            if (raw + 1023.0 < DeadBandOf(t))
                y = -1.0;
            else
                y = Curve(t, (x + 1.0) / 2.0) * 2.0 - 1.0;
            return t.Inverted || oneSided == 2 ? -y : y;
        }

        // The base (DCS) curve as the game's values: dead zone -> dead band (raw units, at most 50), the shape -> the ten
        // points as percent of linear (a curve above linear, e.g. a saturation below 1, is capped at 100), sliders ->
        // one-sided (2 when inverted, as the game stores it).
        private static AxisTuning Fit(AxisTuning tuning, Func<double, double> target)
        {
            AxisTuning result = tuning.Clone();
            int[] values = new int[13];
            values[0] = (int)Math.Max(0, Math.Min(DeadBandMax, Math.Round(tuning.Deadzone * (tuning.Slider ? 2046.0 : 1023.0))));
            for (int k = 1; k <= Points; k++)
            {
                double p = k / 10.0;
                double y = tuning.Slider ? (target(2.0 * p - 1.0) + 1.0) / 2.0 : target(p);
                values[k] = (int)Math.Max(0, Math.Min(100, Math.Round(100.0 * y / p)));
            }
            values[11] = (int)Math.Round(tuning.NativeOr(Filter, 0));
            values[12] = tuning.Slider ? (tuning.Inverted ? 2 : 1) : 0;
            SetValues(result, values);
            return result;
        }
    }
}
