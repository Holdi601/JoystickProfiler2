#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;

// Star Citizen's axis settings (docs/games/starcitizen.md). Two stages, as the game's files and menus show them:
//   1. per device and physical axis (actionmaps.xml <deviceoptions name="<Product>"><option input="x" deadzone=
//      saturation=/>): a dead zone around the centre and a saturation (the stick travel that gives full output);
//   2. per joystick instance and option group (<options type="joystick" instance="N"><flight_move_pitch invert= exponent=
//      sensitivity=> or <nonlinearity_curve><point in out/></nonlinearity_curve>): invert, exponent or curve, sensitivity.
// The formula is the documented behaviour (curve editor, exponent, linear dead zone/saturation), not calibrated against
// the game: UNVERIFIED (no table the game writes back exists). Curve points are joined by straight lines (UNVERIFIED).
namespace JoyPro.Engine.Axis
{
    public static class StarCitizenAxisModel
    {
        public const string Id = "starcitizen";
        public const string ExponentKey = "exponent";
        public const string SensitivityKey = "sensitivityScale";
        // The game's curves in JoyPro's 11-point user curve: the outputs at 0.1 .. 0.9 are the game's points, the ends
        // are fixed at (0,0) and (1,1).
        public const int GamePoints = 9;

        [GameAxisModel("StarCitizen")]
        private static AxisModel Build()
        {
            return new AxisModel
            {
                Id = Id,
                Title = "Star Citizen",
                Description = "Per device axis (all actions on it): dead zone and saturation; per joystick slot and option group (e.g. Flight Pitch, shared by the actions of the group): invert, exponent or a curve (9 points at 10..90 %; setting one deletes the other in the game), sensitivity. No one-sided axes: throttle modes are separate actions. Preview from the documented behaviour, not measured in the game (unverified).",
                Parameters =
                {
                    new AxisParameter { Key = "deadzone", Label = "Dead zone (device axis)", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 0,
                        Help = "Per device and physical axis (actionmaps.xml deviceoptions): every action bound to this axis gets it." },
                    new AxisParameter { Key = "saturationX", Label = "Saturation (device axis)", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 1,
                        Help = "Stick travel that gives full output, per device and physical axis (deviceoptions)." },
                    new AxisParameter { Key = ExponentKey, Label = "Exponent", Kind = AxisParameterKind.Number, Min = 1, Max = 3, Step = 0.01, Default = 1,
                        Help = "Joystick Sensitivity Curves > Exponent of the option group (1 = linear). Range 1..3 as community tools use it (unverified)." },
                    new AxisParameter { Key = "userCurve", Label = "Curve (replaces the exponent)", Kind = AxisParameterKind.UserCurve, Min = 0, Max = 1, Step = 0.01, Default = 0,
                        Help = "The game's curve editor: the points at 10..90 % are written, the ends stay at 0 and 100 %." },
                    new AxisParameter { Key = SensitivityKey, Label = "Sensitivity (unverified)", Kind = AxisParameterKind.Number, Min = 0.01, Max = 2, Step = 0.01, Default = 1,
                        Help = "Option group sensitivity multiplier (0.01..2.0 in the game's joystick tree); whether joysticks use it is unverified." },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = Evaluate,
                Fit = Fit
            };
        }

        public static double Exponent(AxisTuning t)
        {
            double e = t.NativeOr(ExponentKey, 1.0);
            return e > 0 ? e : 1.0;
        }

        public static double Sensitivity(AxisTuning t)
        {
            double s = t.NativeOr(SensitivityKey, 1.0);
            return s > 0 ? s : 1.0;
        }

        // The game's 9 curve outputs (in = 0.1 .. 0.9), or null when the tuning has no curve (exponent).
        public static List<double> GameCurve(AxisTuning t)
        {
            if (!t.IsUserCurve)
                return null;
            return t.Curvature.Skip(1).Take(GamePoints).Select(v => Math.Max(0.0, Math.Min(1.0, v))).ToList();
        }

        public static double Evaluate(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            double sign = x < 0 ? -1.0 : 1.0;
            if (t.Inverted)
                sign = -sign;
            return sign * OptionStage(t, DeviceStage(t, Math.Abs(x)));
        }

        // Dead zone and saturation of the device axis: linear between them (u = |stick|, 0..1).
        public static double DeviceStage(AxisTuning t, double u)
        {
            double dz = Math.Max(0.0, Math.Min(1.0, t.Deadzone));
            double sat = t.SaturationX <= 0 ? 1.0 : Math.Min(1.0, t.SaturationX);
            if (u <= dz)
                return 0.0;
            if (u >= sat || sat <= dz)
                return 1.0;
            return (u - dz) / (sat - dz);
        }

        // Curve (points joined by straight lines) or exponent, then the sensitivity (0..1 -> 0..1).
        public static double OptionStage(AxisTuning t, double v)
        {
            List<double> curve = GameCurve(t);
            double y;
            if (curve != null)
            {
                double pos = v * (GamePoints + 1);
                int i = Math.Max(0, Math.Min(GamePoints, (int)Math.Floor(pos)));
                double y0 = i == 0 ? 0.0 : curve[i - 1];
                double y1 = i >= GamePoints ? 1.0 : curve[i];
                y = y0 + (y1 - y0) * (pos - i);
            }
            else
                y = Math.Pow(Math.Max(0.0, v), Exponent(t));
            return Math.Max(0.0, Math.Min(1.0, y * Sensitivity(t)));
        }

        // The base (DCS parameters) in Star Citizen terms: dead zone and saturation X go to the device axis; the curve
        // shape (curvature, saturation Y) becomes the exponent that fits it best (least squares, 1..3) or, when no
        // exponent comes close (negative curvature, saturation Y), the curve itself; a user curve passes through.
        // One-sided axes (Slider) have no counterpart (Star Citizen's dead zone sits at the centre): neutral.
        private static AxisTuning Fit(AxisTuning tuning, Func<double, double> target)
        {
            AxisTuning result = tuning.Clone();
            result.Native.Remove(ExponentKey);
            result.Native.Remove(SensitivityKey);
            if (tuning.Slider)
            {
                result.Deadzone = 0.0;
                result.SaturationX = 1.0;
                result.Curvature = new List<double> { 0.0 };
                result.Native[ExponentKey] = 1.0;
                return result;
            }
            if (tuning.IsUserCurve)
                return result;
            AxisTuning shape = tuning.Clone();
            shape.Deadzone = 0.0;
            shape.SaturationX = 1.0;
            shape.Inverted = false;
            shape.Slider = false;
            double k = tuning.Curvature == null || tuning.Curvature.Count == 0 ? 0.0 : tuning.Curvature[0];
            result.Curvature = new List<double> { 0.0 };
            if (k == 0.0 && Math.Abs(tuning.SaturationY - 1.0) < 1e-9)
            {
                result.Native[ExponentKey] = 1.0;
                return result;
            }
            double best = double.MaxValue;
            double bestE = 1.0;
            for (int step = 0; step <= 200; step++)
            {
                double e = 1.0 + step * 0.01;
                double err = 0.0;
                for (int i = 1; i <= 20; i++)
                {
                    double x = i / 20.0;
                    double diff = Math.Pow(x, e) - AxisModels.EvaluateDcs(shape, x);
                    err += diff * diff;
                }
                if (err < best)
                {
                    best = err;
                    bestE = e;
                }
            }
            if (Math.Sqrt(best / 20.0) <= 0.01)
            {
                result.Native[ExponentKey] = Math.Round(bestE, 2);
                return result;
            }
            result.Curvature = Enumerable.Range(0, AxisModels.UserCurvePoints)
                .Select(i => Math.Round(Math.Max(0.0, Math.Min(1.0, AxisModels.EvaluateDcs(shape, i / (double)(AxisModels.UserCurvePoints - 1)))), 4))
                .ToList();
            return result;
        }
    }
}
