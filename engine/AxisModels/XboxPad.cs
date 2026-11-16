#if NET
#nullable disable
#endif

// Games that read only Xbox controllers (ACE COMBAT 8, Hell Let Loose: Vietnam): they get no JoyPro export, so the
// axis curve reaches them only through the virtual Xbox pad. JoyPro's forwarder bends each axis that goes to a pad
// stick or trigger with this curve before the pad gets it (docs/virtual-joystick.md "Axis curves on the Xbox pad").
// Dead zone, saturation X/Y, curvature or an 11-point user curve, slider and invert, evaluated like the base curve
// (AxisModels.EvaluateDcs), so the base curve carries over unchanged.
namespace JoyPro.Engine.Axis
{
    public static class XboxPadAxisModel
    {
        public const string Id = "xboxpad";

        [GameAxisModel("AceCombat8", "HLLVietnam")]
        private static AxisModel Build()
        {
            return new AxisModel
            {
                Id = Id,
                Title = "Virtual Xbox pad (JoyPro applies the curve)",
                Units = "0..1 of the travel (dead zone, saturation, user curve), curvature -1..1",
                Description = "The game reads only Xbox controllers and gets no export: JoyPro's forwarder applies this curve itself to the pad stick or trigger (dead zone, saturation X/Y, curvature or user curve, slider, invert).",
                Parameters =
                {
                    new AxisParameter { Key = "deadzone", Label = "Dead zone", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 0 },
                    new AxisParameter { Key = "saturationX", Label = "Saturation X", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 1 },
                    new AxisParameter { Key = "saturationY", Label = "Saturation Y", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 1 },
                    new AxisParameter { Key = "curvature", Label = "Curvature", Kind = AxisParameterKind.Number, Min = -1, Max = 1, Step = 0.01, Default = 0 },
                    new AxisParameter { Key = "userCurve", Label = "User curve", Kind = AxisParameterKind.UserCurve, Min = 0, Max = 1, Step = 0.01, Default = 0 },
                    new AxisParameter { Key = "slider", Label = "Slider", Kind = AxisParameterKind.Toggle },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = AxisModels.EvaluateDcs
            };
        }
    }
}
