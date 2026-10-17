#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;

// Microsoft Flight Simulator 2020/2024: the device's Sensitivity page per physical axis (<Device><Axes><Axis/>) and the
// per-action flags and override (<Action Flag> 1024 no sensitivity curve, 4096 axis override + <Axis/>, 128 invert).
// Values are in the file's units (the UI's percent, docs/games/msfs.md). Plain math, C# 7.3: the GUI links this file.
namespace JoyPro.Engine.Axis
{
    public static class MsfsAxisModel
    {
        public const string Id = "msfs";

        // Native keys (file attribute in brackets).
        public const string SensitivityPlus = "sensitivityPlus";      // AxisSensitivy, -100..100
        public const string SensitivityMinus = "sensitivityMinus";    // AxisSensitivyMinus, -100..100
        public const string Neutral = "neutral";                      // AxisNeutral, -100..100
        public const string DeadZone = "axisDeadZone";                // AxisDeadZone, 0..100
        public const string ExtremityDeadZone = "extremityDeadZone";  // AxisOutDeadZone, 0..100
        public const string Reactivity = "reactivity";                // AxisResponseRate, -1 (off) or 100..2000
        public const string NoSensitivityCurve = "noSensitivityCurve"; // Flag 1024 (MOD_NO_SENS_CURVE)
        public const string AxisOverride = "axisOverride";            // Flag 4096 (MOD_AXIS_OVERRIDE) + <Axis/> in <Action>

        // The Sensitivity page keys in the order of the file's attributes, with the attribute names (misspelled by MS).
        public static readonly string[] PageKeys = { SensitivityPlus, SensitivityMinus, Neutral, DeadZone, ExtremityDeadZone, Reactivity };
        public static readonly string[] PageAttributes = { "AxisSensitivy", "AxisSensitivyMinus", "AxisNeutral", "AxisDeadZone", "AxisOutDeadZone", "AxisResponseRate" };
        public static readonly int[] PageDefaults = { 0, 0, 0, 0, 0, -1 };

        [GameAxisModel("MSFS2024", "MSFS2020", "MSFS")]
        private static AxisModel Build()
        {
            return new AxisModel
            {
                Id = Id,
                Title = "Microsoft Flight Simulator 2020/2024",
                Description = "Sensitivity page of the device axis (one set per physical axis, shared by every action on it): " +
                              "sensitivity -/+ per half, neutral, dead zone, extremity dead zone, reactivity; per action: no sensitivity curve, " +
                              "axis override (own values for this action, MSFS 2024 profiles), invert. File units (the game's %). " +
                              "Preview measured in MSFS 2020 (SimConnect, 2026-10-10): the device axis is evaluated mirrored (Sensitivity - acts on the " +
                              "positive stick half, neutral moves the zero point to -neutral), dead zone in stick units from that point, curve " +
                              "v(1+s)/(1-s+2sv) over the rest of the half, extremity dead zone caps the output at 1 - e; reactivity is smoothing over time " +
                              "(not in the preview).",
                Parameters =
                {
                    new AxisParameter { Key = SensitivityMinus, Label = "Sensitivity - (negative half)", Kind = AxisParameterKind.Number, Min = -100, Max = 100, Step = 1, Default = 0,
                        Help = "Below 0: flatter around the centre, steeper towards the end; above 0: the other way round." },
                    new AxisParameter { Key = SensitivityPlus, Label = "Sensitivity + (positive half)", Kind = AxisParameterKind.Number, Min = -100, Max = 100, Step = 1, Default = 0,
                        Help = "Below 0: flatter around the centre, steeper towards the end; above 0: the other way round." },
                    new AxisParameter { Key = DeadZone, Label = "Dead zone %", Kind = AxisParameterKind.Number, Min = 0, Max = 100, Step = 1, Default = 0 },
                    new AxisParameter { Key = ExtremityDeadZone, Label = "Extremity dead zone %", Kind = AxisParameterKind.Number, Min = 0, Max = 100, Step = 1, Default = 0,
                        Help = "Caps the output: at the end of the travel the action gets 100 % minus this value (measured)." },
                    new AxisParameter { Key = Neutral, Label = "Neutral %", Kind = AxisParameterKind.Number, Min = -100, Max = 100, Step = 1, Default = 0,
                        Help = "Moves the stick position that gives zero output." },
                    new AxisParameter { Key = Reactivity, Label = "Reactivity (response rate)", Kind = AxisParameterKind.Number, Min = -1, Max = 2000, Step = 1, Default = -1,
                        Help = "AxisResponseRate: -1 off (default; the page shows 100 %), else 100..2000 (smoothing over time; the page's slider shows (rate - 100) / 19 %)." },
                    new AxisParameter { Key = NoSensitivityCurve, Label = "No sensitivity curve (this action)", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 1, Default = 0,
                        Help = "1: the action gets the raw axis, without the device's Sensitivity page (flag 1024)." },
                    new AxisParameter { Key = AxisOverride, Label = "Axis override (this action)", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 1, Default = 0,
                        Help = "1: the values are written for this action only (flag 4096 + <Axis/>, MSFS 2024 profiles); 0: to the device's Sensitivity page." },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = Evaluate,
                Fit = Fit,
                SharedPerAxisKeys = new List<string>(PageKeys),
                PerActionKey = AxisOverride
            };
        }

        // A value in file units. A tuning without the key falls back to the generic fields (an MSFS tuning saved before
        // this model had a dead zone and saturation only), else to the game's default.
        public static double Value(AxisTuning t, string key)
        {
            double value;
            if (t.Native != null && t.Native.TryGetValue(key, out value))
                return value;
            switch (key)
            {
                case DeadZone: return Math.Max(0, Math.Min(100, t.Deadzone * 100.0));
                case ExtremityDeadZone: return Math.Max(0, Math.Min(100, (1.0 - (t.SaturationY <= 0 ? 1.0 : t.SaturationY)) * 100.0));
                case Reactivity: return -1;
                default: return 0;
            }
        }

        // What JoyPro writes for a key: null when the tuning does not set it (the file keeps its value).
        public static int? FileValue(AxisTuning t, string key)
        {
            double value;
            bool has = t.Native != null && t.Native.TryGetValue(key, out value);
            if (!has)
            {
                if (key == DeadZone && t.Deadzone > 0)
                    return (int)Math.Round(Value(t, key));
                if (key == ExtremityDeadZone && t.SaturationY > 0 && t.SaturationY < 1)
                    return (int)Math.Round(Value(t, key));
                return null;
            }
            return Clamp(key, Value(t, key));
        }

        // A value as the file holds it (whole numbers in the documented range; reactivity -1 or 100..2000).
        public static int Clamp(string key, double value)
        {
            int v = (int)Math.Round(value);
            switch (key)
            {
                case DeadZone:
                case ExtremityDeadZone:
                    return Math.Max(0, Math.Min(100, v));
                case Reactivity:
                    return v <= 0 ? -1 : Math.Max(100, Math.Min(2000, v));
                default:
                    return Math.Max(-100, Math.Min(100, v));
            }
        }

        // Stick position -1..1 -> axis value -1..1 as the action gets it (before an action's own 0..100 % range).
        // Measured in MSFS 2020 with tools\e2e\games\MsfsCurves.ps1 (vJoy sweep, SimConnect ELEVATOR POSITION and throttle
        // lever; exact to 1e-4 over all points). The throttle event (THROTTLE_AXIS_SET_EX1) gets the value negated.
        public static double Evaluate(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            // Flag 1024 (no sensitivity curve): the raw axis, but the dead zone still cuts it (not rescaled; measured).
            double d = Math.Max(0.0, Math.Min(1.0, Value(t, DeadZone) / 100.0));
            double y = t.NativeOr(NoSensitivityCurve, 0) >= 0.5 ? (Math.Abs(x) <= d ? 0.0 : x) : Curve(t, x);
            // Invert is a flag of the action; the device curve works on the raw axis.
            return t.Inverted ? -y : y;
        }

        // MSFS works on the mirrored axis: its "+" half is the negative stick half and its neutral is at -neutral in stick
        // terms. The dead zone is in stick units from that point; the rest of the half maps through v(1+s)/(1-s+2sv) (s =
        // sensitivity / 100, at most 0.99 either way: -100 is a step at the end); the extremity dead zone caps the output.
        private static double Curve(AxisTuning t, double x)
        {
            double n = -Math.Max(-0.99, Math.Min(0.99, Value(t, Neutral) / 100.0));
            double d = Math.Max(0.0, Math.Min(1.0, Value(t, DeadZone) / 100.0));
            double e = Math.Max(0.0, Math.Min(1.0, Value(t, ExtremityDeadZone) / 100.0));
            double side = x >= n ? 1.0 : -1.0;
            double a = Math.Abs(x - n);
            double h = side > 0 ? 1.0 - n : 1.0 + n;
            if (a <= d)
                return 0.0;
            double v = h - d <= 1e-9 ? 1.0 : Math.Min(1.0, (a - d) / (h - d));
            double s = Math.Max(-0.99, Math.Min(0.99, Value(t, side > 0 ? SensitivityMinus : SensitivityPlus) / 100.0));
            return side * (1.0 - e) * v * (1.0 + s) / (1.0 - s + 2.0 * s * v);
        }

        // The base (DCS) curve as the Sensitivity page: dead zone, extremity dead zone = 1 - saturation Y (both cap the
        // output), one sensitivity for both halves that comes closest (least squares on 20 points; saturation X and the
        // curvature go into that fit). A one-sided base (slider) has no counterpart (its dead zone is at the low end only): linear.
        private static AxisTuning Fit(AxisTuning tuning, Func<double, double> target)
        {
            AxisTuning result = tuning.Clone();
            if (tuning.Slider)
            {
                result.Native[DeadZone] = 0;
                result.Native[ExtremityDeadZone] = 0;
                result.Native[SensitivityPlus] = 0;
                result.Native[SensitivityMinus] = 0;
                return result;
            }
            double sy = tuning.SaturationY <= 0 ? 1.0 : tuning.SaturationY;
            result.Native[DeadZone] = Clamp(DeadZone, tuning.Deadzone * 100.0);
            result.Native[ExtremityDeadZone] = Clamp(ExtremityDeadZone, (1.0 - sy) * 100.0);
            AxisTuning probe = result.Clone();
            probe.Inverted = false;
            probe.Native.Remove(NoSensitivityCurve);
            probe.Native.Remove(Neutral);
            double best = double.MaxValue;
            int bestS = 0;
            for (int s = -100; s <= 100; s++)
            {
                probe.Native[SensitivityPlus] = s;
                probe.Native[SensitivityMinus] = s;
                double err = 0;
                for (int i = 1; i <= 20; i++)
                {
                    double x = i / 20.0;
                    double diff = Curve(probe, x) - target(x);
                    err += diff * diff;
                }
                if (err < best - 1e-12)
                {
                    best = err;
                    bestS = s;
                }
            }
            result.Native[SensitivityPlus] = bestS;
            result.Native[SensitivityMinus] = bestS;
            return result;
        }
    }
}
