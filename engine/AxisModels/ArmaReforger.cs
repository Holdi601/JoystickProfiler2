#if NET
#nullable disable
#endif
using System;

namespace JoyPro.Engine.Axis
{
    // Arma Reforger: the per-binding filter of each axis source in JoyPro's joystick preset (docs/games/arma-reforger.md),
    // Filter InputFilterValue "{id}" { Multiplier <m> UseInvert <0|1> } (stable 1.8: property names in the exe; 1.9 adds
    // Offset). Each half-axis source gives 0..1 times the multiplier and the action sums them [INF]; the engine's own
    // per-axis joystick profile (DeadZone/Transition) is not written (format unknown).
    internal static class ArmaReforgerAxisModel
    {
        public const string Id = "reforger";

        [GameAxisModel("ArmaReforger")]
        private static AxisModel Build()
        {
            return new AxisModel
            {
                Id = Id,
                Title = "Arma Reforger",
                Description = "Per bind: the multiplier of the axis filter (InputFilterValue Multiplier) and whether the game's invert options apply (UseInvert); invert itself swaps the half axes. Not verified in the game.",
                Parameters =
                {
                    new AxisParameter { Key = "multiplier", Label = "Multiplier", Kind = AxisParameterKind.Number, Min = 0, Max = 3, Step = 0.05, Default = 1,
                        Help = "Scales what the axis gives the action (1 = unchanged)." },
                    new AxisParameter { Key = "useInvert", Label = "Apply the game's invert options (UseInvert)", Kind = AxisParameterKind.Toggle,
                        Help = "The game's description: the invert mouse vertically / invert gamepad vertically options are applied." },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = Evaluate
            };
        }

        public static double Evaluate(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            double y = Math.Max(-1.0, Math.Min(1.0, x * t.NativeOr("multiplier", 1)));
            return t.Inverted ? -y : y;
        }
    }
}
