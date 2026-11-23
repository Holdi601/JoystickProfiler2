#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;

namespace JoyPro.Engine.Axis
{
    // FreeSpace Open (Fate of the Galaxy): an axis bind in the preset file has only the INVERTED flag; the response is
    // game-wide (FsoGameSettings: dead zone 0..45 and sensitivity 0..9, controlsconfig.cpp joy_get_scaled_reading): the
    // dead zone is size x 2 % of the half travel (10 = 20 %), the rest is rescaled to 0..1 and shaped as
    // p * s + p^5 * (1 - s) with s = sensitivity / 9. The absolute throttle (Slider) skips both: the raw travel 0..1 becomes
    // -10 .. 110 % and is clipped, so the last 1/12 at each end is a stop. Invert is JoyPro's (DCS) direction; the export
    // writes INVERTED XOR the direction FSO needs (FreeSpaceOpenIntegration.HotasInvert: the relative throttle only).
    internal static class FsoAxisModel
    {
        public const string Id = "fso";

        [GameAxisModel("FateOfTheGalaxy")]
        private static AxisModel Build()
        {
            return new AxisModel
            {
                Id = Id,
                Title = "Fate of the Galaxy (FreeSpace Open)",
                Units = "the game's options: dead zone 0..45, sensitivity 0..9 (game-wide)",
                Description = "Per axis only invert. Dead zone and sensitivity are one setting for all joystick axes (engine ffb set --device * --game FateOfTheGalaxy --param deadZone=<0..45> / sensitivity=<0..9>); the preview (engine and GUI) uses them (FSO's defaults 10 and 9 when not set). Slider = the absolute throttle: no dead zone, -10..110 % clipped.",
                Parameters =
                {
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = Evaluate
            };
        }

        // The game settings (engine: the profile's; GUI: the ones it loaded), FSO's default when not set.
        private static double Setting(string key, double fallback)
        {
            try
            {
                double? value = AxisModels.GameSetting == null ? null : AxisModels.GameSetting("FateOfTheGalaxy", key);
                return value ?? fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        public static double Evaluate(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            if (t.Inverted)
                x = -x;
            if (t.Slider)
            {
                double u = (x + 1.0) / 2.0;
                double v = Math.Max(0.0, Math.Min(1.0, u * 1.2 - 0.1));
                return v * 2.0 - 1.0;
            }
            double dz = Math.Max(0, Math.Min(45, Setting("deadZone", 10))) / 50.0;      // FsoGameSettings.DeadZone
            double s = Math.Max(0, Math.Min(9, Setting("sensitivity", 9))) / 9.0;      // FsoGameSettings.Sensitivity
            double a = Math.Abs(x);
            if (a <= dz)
                return 0.0;
            double p = Math.Min(1.0, (a - dz) / (1.0 - dz));
            double shaped = p * s + Math.Pow(p, 5) * (1.0 - s);
            return Math.Sign(x) * shaped;
        }
    }
}
