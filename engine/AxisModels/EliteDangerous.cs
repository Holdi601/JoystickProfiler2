#if NET
#nullable disable
#endif
using System;

namespace JoyPro.Engine.Axis
{
    // Elite Dangerous: every axis element of a .binds preset has <Inverted Value="0|1"/> and <Deadzone Value="0..1"/>
    // and nothing else (no saturation, no curve; the only curves of the game are the mouse's). The Ship preset's
    // <ThrottleRange> makes ThrottleAxis forward-only ("Bindings_ThrottleForewardOnly") or full range (""); JoyPro ties
    // it to Slider. Invert is JoyPro's (DCS) direction: the export writes it XOR the direction Elite's stock joystick
    // presets use for the element (pitch, throttle, vertical thrust are inverted there), see docs/games/elite-dangerous.md.
    internal static class EliteAxisModel
    {
        public const string Id = "elite";

        [GameAxisModel("EliteDangerous")]
        private static AxisModel Build()
        {
            return new AxisModel
            {
                Id = Id,
                Title = "Elite Dangerous",
                Description = "Per axis element (all ships): invert and dead zone (0..1 of the half travel, file units); Slider on the throttle axis = Throttle Axis Range \"Forward only\" (game-wide ThrottleRange), else full range with reverse.",
                Parameters =
                {
                    new AxisParameter { Key = "deadzone", Label = "Dead zone", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 0,
                        Help = "<Deadzone Value> of the axis element, a share of the half travel around the centre." },
                    new AxisParameter { Key = "platformDeadzone", Label = "Game default dead zone", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 1, Default = 0,
                        Help = "1: write the game's symbol DefaultPlatformDeadzone instead of a number (stock gamepad presets use it; its value is not known, the preview shows no dead zone)." },
                    new AxisParameter { Key = "slider", Label = "Forward-only throttle (ThrottleAxis)", Kind = AxisParameterKind.Toggle },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = Evaluate
            };
        }

        // Invert and a linear dead zone around the centre of the raw axis; a forward-only throttle maps the whole travel
        // to 0..100 % (-1..1 here, as DCS sliders). Elite's rescaling outside the dead zone is not verified (linear assumed).
        private static double Evaluate(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            if (t.Inverted)
                x = -x;
            double dz = t.NativeOr("platformDeadzone", 0) >= 0.5 ? 0.0 : Math.Max(0.0, Math.Min(0.999, t.Deadzone));
            double u = Math.Abs(x);
            if (u <= dz)
                return 0.0;
            return Math.Sign(x) * Math.Min(1.0, (u - dz) / (1.0 - dz));
        }
    }
}
