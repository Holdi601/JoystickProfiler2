#if NET
#nullable disable
#endif
using System;

// WARDOGS: Settings > GAMEPAD > HOTAS, per axis line of GameUserSettings.ini [/Script/WDJoystick.WDJoystickSettings]
// (Roll, Pitch, Yaw, Throttle = "Collective"; LookPitch/LookYaw carry the same fields without a row in the menu):
// bInvert, Sensitivity (default 1.0) and DeadZone (default 0.05), file units = the slider values. See docs/games/wardogs.md.
namespace JoyPro.Engine.Axis
{
    public static class WardogsAxisModel
    {
        public const string Id = "wardogs";
        // Slider ranges read from the menu screenshots (knob positions); UNVERIFIED until the in-game check
        // (tools\e2e\games\WardogsSettings.ps1) drags them to both ends.
        public const double SensitivityMin = 0.1;
        public const double SensitivityMax = 2.0;
        // The menu's slider moves in steps of 0.05 and the game saves the value it snapped to (checked in game:
        // 1.04 -> 1.05, 0.87 -> 0.85, 1.26 -> 1.25); 2.0 and a dead zone of 0.5 are the slider ends (3.5 / 0.9 in the
        // file came back as 2.00 / 0.50).
        public const double SensitivityStep = 0.05;
        public const double DeadZoneMax = 0.5;

        [GameAxisModel("Wardogs")]
        private static AxisModel Build()
        {
            return new AxisModel
            {
                Id = Id,
                Title = "WARDOGS",
                Description = "Per axis (Roll, Pitch, Yaw, Collective; the look axes have the same fields without a menu row): sensitivity and dead zone as the HOTAS page shows them (GameUserSettings.ini). No curve: curvature and user curves cannot be exported. The game's formula is not readable (packed code); the preview assumes a rescaled dead zone times the sensitivity, clipped at full deflection (unverified).",
                Parameters =
                {
                    new AxisParameter { Key = "sensitivity", Label = "Sensitivity", Kind = AxisParameterKind.Number, Min = SensitivityMin, Max = SensitivityMax, Step = SensitivityStep, Default = 1,
                        Help = "The game: \"Scales how strongly the control responds. 1.0 = unchanged; lower is gentler, higher is sharper.\" Lower values also cap the maximum rate." },
                    new AxisParameter { Key = "deadZone", Label = "Dead zone", Kind = AxisParameterKind.Number, Min = 0, Max = DeadZoneMax, Step = 0.01, Default = 0.05,
                        Help = "Share of the half travel around the centre that is ignored (the game's default 0.05). Also on the collective, where it probably sits at mid travel." },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = Evaluate,
                Fit = Fit
            };
        }

        // Nearest slider step, within the slider's ends.
        public static double Snap(double sensitivity)
        {
            double snapped = Math.Round(Math.Round(sensitivity / SensitivityStep, MidpointRounding.AwayFromZero) * SensitivityStep, 2);
            return Math.Max(SensitivityMin, Math.Min(SensitivityMax, snapped));
        }

        public static double Sensitivity(AxisTuning t)
        {
            return Snap(t.NativeOr("sensitivity", 1.0));
        }

        // The dead zone in file units; tunings stored before the model (import of older JoyPro versions) only have the
        // relation dead zone.
        public static double DeadZone(AxisTuning t)
        {
            return Math.Max(0.0, Math.Min(DeadZoneMax, t.NativeOr("deadZone", t.Deadzone)));
        }

        // Working hypothesis (typical Unreal axial dead zone, the game's own description of Sensitivity): invert, cut
        // the dead zone and rescale the rest to 0..1, multiply by the sensitivity, clip at 1. UNVERIFIED: whether the
        // dead zone is rescaled or simply cut and whether a sensitivity above 1 is clipped.
        public static double Evaluate(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            if (t.Inverted)
                x = -x;
            double dz = DeadZone(t);
            double u = Math.Abs(x);
            if (u <= dz)
                return 0.0;
            double v = dz >= 1.0 ? 0.0 : (u - dz) / (1.0 - dz);
            return Math.Sign(x) * Math.Min(1.0, Sensitivity(t) * v);
        }

        // The base (DCS) values as WARDOGS can hold them: the centre dead zone stays (same meaning, both rescale); the
        // saturation becomes the slope (sensitivity = slope of the DCS line between dead zone and saturation X, scaled
        // to WARDOGS' rescaled travel). A slider's dead zone sits at the start of the travel in DCS, but WARDOGS treats
        // the collective as a centred axis: it would become a flat spot at mid collective, so it is not carried over.
        // Curvature has no equivalent (the export says so).
        private static AxisTuning Fit(AxisTuning tuning, Func<double, double> target)
        {
            AxisTuning result = tuning.Clone();
            double sx = tuning.SaturationX <= 0 ? 1.0 : tuning.SaturationX;
            double sy = tuning.SaturationY;
            double dz = tuning.Slider ? 0.0 : Math.Round(Math.Max(0.0, Math.Min(DeadZoneMax, tuning.Deadzone)), 2);
            double sensitivity = 1.0;
            if (!tuning.Slider && (Math.Abs(sx - 1.0) > 1e-9 || Math.Abs(sy - 1.0) > 1e-9) && sx - dz > 1e-6)
                sensitivity = sy * (1.0 - dz) / (sx - dz);
            result.Native["deadZone"] = dz;
            result.Native["sensitivity"] = Snap(sensitivity);
            return result;
        }
    }
}
