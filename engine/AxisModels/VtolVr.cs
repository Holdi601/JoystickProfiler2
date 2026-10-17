#if NET
#nullable disable
#endif
using System;

// VTOL VR: the BYOJoystick mod ("byoj", <install>\SaveData\BYOJ_<seat>.xml) and the game's own rudder/toe brakes
// ("vtolnative", Rewired inputbindings.cfg). See docs/games/vtol-vr.md.
namespace JoyPro.Engine.Axis
{
    public static class VtolVrAxisModels
    {
        public const string Byoj = "byoj";
        public const string Native = "vtolnative";

        // BYOJoystick (github.com/sicsix/BYOJoystick master, Bindings/JoystickBinding.cs, Controls/*.cs): the axis value
        // is raw / 65535 (inverted: 1 - that), centred targets (value - 0.5) * 2. The one per-binding number,
        // <Deadzone>, means different things per target:
        //   Joystick Pitch/Roll/Yaw (GetAsFloatCenteredNoDeadzone): no dead zone at all; only in multiplayer multi-crew
        //     the deflection from the centre the HOTAS needs before it takes the stick from the other crew member
        //     (max(0.005, value), half while it holds the stick). SetYaw stores it as the roll threshold and SetRoll as
        //     the yaw threshold (swapped in the mod).
        //   Throttle, Power, Collective (CThrottle.Set): only in multiplayer multi-crew, the change needed before the HOTAS
        //     takes the throttle (same rule).
        //   SOI Slew X/Y, Articulate Tilt (GetAsFloatCentered): |v| < value -> 0, else v unchanged (a cut, no rescale).
        //   Every other axis (knobs, tilt, brakes, sweep): not used.
        [GameAxisModel("VTOLVR")]
        private static AxisModel BuildByoj()
        {
            return new AxisModel
            {
                Id = Byoj,
                Title = "VTOL VR (BYOJoystick mod)",
                Description = "Per binding: invert and the mod's one \"Deadzone\" number, which is a dead zone only for SOI slew X/Y and Articulate Tilt (a cut without rescale; the preview shows it when the slew switch is on). On the stick (pitch/roll/yaw) and throttle/power/collective it is the multi-crew takeover threshold and does not change the response (linear). No curves.",
                Parameters =
                {
                    new AxisParameter { Key = "takeover", Label = "Takeover threshold (stick, throttle, power, collective)", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 0,
                        Help = "Written as <Deadzone> for Joystick Pitch/Roll/Yaw, Throttle, Power and Collective. Only in multiplayer multi-crew: how far the stick must be from the centre (throttle: how much it must move) before your HOTAS takes the control from the other crew member (at least 0.005; half of it while you hold it). No dead zone in the response. The mod applies Joystick Roll's value to yaw movement and Yaw's to roll." },
                    new AxisParameter { Key = "cut", Label = "Dead zone without rescale (SOI slew X/Y, Articulate Tilt)", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 0,
                        Help = "Written as <Deadzone> for SOI Slew X/Y and Articulate Tilt: values closer to the centre than this become 0, the rest is passed on unchanged (a jump at the edge, no rescale)." },
                    new AxisParameter { Key = "slewAxis", Label = "Slew / Articulate Tilt axis (1 = preview with the cut)", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 1, Default = 0,
                        Help = "Only for the preview: the export uses the cut for SOI Slew X/Y and Articulate Tilt and the takeover threshold for the stick and throttles whatever this says (an import sets it for slew binds)." },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = EvaluateByoj,
                Fit = FitByoj
            };
        }

        public static double EvaluateByoj(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            if (t.Inverted)
                x = -x;
            // The stick and throttle targets ignore the value for the response; slew/articulate cut (slewAxis marks them).
            if (t.NativeOr("slewAxis", 0) == 0)
                return x;
            double cut = Math.Max(0.0, Math.Min(1.0, t.NativeOr("cut", 0.0)));
            return Math.Abs(x) < cut ? 0.0 : x;
        }

        // What JoyPro wrote before the model: the relation dead zone for centred targets (a resting stick near the
        // centre should neither move the slew nor take the stick), nothing for one-sided axes.
        private static AxisTuning FitByoj(AxisTuning tuning, Func<double, double> target)
        {
            AxisTuning result = tuning.Clone();
            double dz = tuning.Slider ? 0.0 : Math.Round(Math.Max(0.0, Math.Min(1.0, tuning.Deadzone)), 2);
            result.Native["takeover"] = dz;
            result.Native["cut"] = dz;
            return result;
        }

        // The game's own Rewired input (Hardware Control Rudder): Rudder (full axis) and Wheel Brake L/R (input behaviour
        // ZeroToOneAxis, read by VTRWGetAxis: first element map inverted -> 1 - clamp01(|v|), else clamp01(v)). The only
        // per-bind settings are the element map's invert and axisRange; Rewired's calibration (dead zone 0.1 for devices
        // Rewired does not know, sensitivity) has no UI and is not written.
        [GameAxisModel("VTOLVRNative")]
        private static AxisModel BuildNative()
        {
            return new AxisModel
            {
                Id = Native,
                Title = "VTOL VR (native rudder and toe brakes)",
                Description = "Per bind (inputbindings.cfg element map): invert and, for the toe brakes, the axis range. VTOL VR reads a brake as 0..1: a toe brake that reports -1..1 brakes only on the second half of its travel unless the range is Positive. Rewired's own calibration (dead zone, sensitivity) has no UI and stays as the game has it.",
                Parameters =
                {
                    new AxisParameter { Key = "axisRange", Label = "Toe brake: whole travel (axisRange Positive)", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 1, Default = 0,
                        Help = "0 Full (what the game's Bind writes), 1 Positive: the community fix for full-travel toe brakes (Thrustmaster TPR: with Invert). Brakes only; the rudder always uses Full. Why 1 gives the whole travel is not confirmed in the game." },
                    new AxisParameter { Key = "slider", Label = "Toe brake (preview as a 0..1 brake)", Kind = AxisParameterKind.Toggle },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = EvaluateNative
            };
        }

        // Rudder: the Rewired value (invert flips it). Brake (Slider): VTRWGetAxis of the Rewired value; with axisRange 1
        // the whole travel is assumed to become 0..1 (inferred from the community fix, unverified). Output 2 * brake - 1.
        public static double EvaluateNative(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            if (!t.Slider)
                return t.Inverted ? -x : x;
            double v = t.NativeOr("axisRange", 0) >= 0.5 ? (x + 1.0) / 2.0 : x;
            if (t.Inverted)
                v = -v;
            double brake = t.Inverted ? 1.0 - Math.Min(1.0, Math.Abs(v)) : Math.Max(0.0, Math.Min(1.0, v));
            return 2.0 * brake - 1.0;
        }
    }
}
