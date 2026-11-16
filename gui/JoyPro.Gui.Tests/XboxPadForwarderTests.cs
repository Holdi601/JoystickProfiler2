using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using JoyPro.Engine.Axis;
using JoyPro.Engine.VJoy;
using JoyPro.VJoy;
using Xunit;

namespace JoyPro.Gui.Tests;

// The virtual Xbox pad's table in JoyPro.VJoy (gui/JoyPro.VJoy/ForwardingTable.cs, linked): the relation's axis curve
// applied on the pad (its games get no export), a hat direction on a pad button, and a twist made into LB/RB by an
// "Axis ends to buttons" function, with a stream of device states as the forwarder feeds them.
public sealed class XboxPadForwarderTests
{
    private const string Stick = "S {00000000-0000-0000-0000-000000000001}";

    private static ForwarderConfig Config(params XboxSlotConfig[] slots) => new()
    {
        Sources = [new ForwarderSource(Stick, "S", Guid.Empty), new ForwarderSource("T {00000000-0000-0000-0000-000000000002}", "T", Guid.Empty), new ForwarderSource("G {00000000-0000-0000-0000-000000000003}", "G", Guid.Empty)],
        XboxGame = "AceCombat8",
        Xbox = slots.ToList()
    };

    private static byte[] State(int x = 16384, uint hat = ForwardingTable.HatCentred, int z = 16384)
    {
        byte[] state = new byte[ForwardingTable.StateSize];
        VfRuntime.Neutral(state);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(ForwardingTable.AxisOffset, 4), x);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(ForwardingTable.AxisOffset + 8, 4), z);
        BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(ForwardingTable.HatOffset, 4), hat);
        return state;
    }

    // The curve as the engine samples it for the game (AxisModels.Sample, the Binds page's preview formula).
    private static double[] Curve(AxisTuning tuning) =>
        AxisModels.Sample(AxisModels.Get(AxisModels.Dcs), tuning, 1025).Select(p => p.Value).ToArray();

    [Fact]
    public void AxisCurve_IsAppliedOnThePadStick_AsThePreviewShowsIt()
    {
        AxisTuning tuning = new() { Deadzone = 0.2, Curvature = [0.3] };
        XboxTable table = new(Config(new XboxSlotConfig(0, "axis", 0, "XB_LX") { Curve = Curve(tuning) }));
        Assert.Equal(1, table.CurveCount);

        // Inside the dead zone: centred.
        table.Apply(0, State(x: 16384 + 3000));
        Assert.Equal(0, table.Report.LeftX);
        // Full right stays full; half way out is what the preview says (DCS's formula), within a step of the table.
        table.Apply(0, State(x: 32767));
        Assert.Equal(32767, table.Report.LeftX);
        table.Apply(0, State(x: 16384 + 8192));
        double expected = AxisModels.EvaluateDcs(tuning, 8192 / (double)(32767 - 16384));
        Assert.InRange(table.Report.LeftX / 32767.0, expected - 0.002, expected + 0.002);
        table.Apply(0, State(x: 0));
        Assert.Equal(-32767, table.Report.LeftX);
    }

    [Fact]
    public void AxisCurve_CarriesTheInvert_AndAStraightLineStaysOneToOne()
    {
        XboxTable inverted = new(Config(new XboxSlotConfig(0, "axis", 0, "XB_LX") { Curve = Curve(new AxisTuning { Inverted = true }) }));
        inverted.Apply(0, State(x: 32767));
        Assert.Equal(-32767, inverted.Report.LeftX);

        XboxTable straight = new(Config(new XboxSlotConfig(0, "axis", 0, "XB_LX") { Curve = Curve(new AxisTuning()) }));
        Assert.Equal(0, straight.CurveCount);
        XboxTable none = new(Config(new XboxSlotConfig(0, "axis", 0, "XB_LX")));
        foreach (int x in new[] { 0, 5000, 16384, 20000, 32767 })
        {
            straight.Apply(0, State(x: x));
            none.Apply(0, State(x: x));
            Assert.Equal(none.Report.LeftX, straight.Report.LeftX);
        }
    }

    [Fact]
    public void AxisCurve_TableIsBuiltOnce_ApplyAllocatesNothing()
    {
        XboxTable table = new(Config(new XboxSlotConfig(0, "axis", 0, "XB_LX") { Curve = Curve(new AxisTuning { Deadzone = 0.1, Curvature = [0.5] }) }));
        byte[][] states = Enumerable.Range(0, 64).Select(i => State(x: i * 512)).ToArray();
        for (int i = 0; i < 200; i++)
            table.Apply(0, states[i % states.Length]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++)
            table.Apply(0, states[i % states.Length]);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ForwarderConfig_ReadsTheCurveAndTheHatDirection()
    {
        const string json = """
        {"version":1,"vjoyDevices":[],"sources":[{"device":"S {00000000-0000-0000-0000-000000000001}","name":"S","instanceGuid":"00000000-0000-0000-0000-000000000001"}],"slots":[],
         "xboxGame":"AceCombat8","xbox":[{"source":0,"kind":"axis","index":0,"target":"XB_LX","curve":[-1,0,1]},{"source":0,"kind":"hatdir","index":1,"target":"XB_Y","direction":2}]}
        """;
        ForwarderConfig config = ForwarderConfig.Parse(json);
        Assert.Equal([-1.0, 0.0, 1.0], config.Xbox[0].Curve);
        Assert.Null(config.Xbox[1].Curve);
        Assert.Equal(2, config.Xbox[1].Direction);
        Assert.Equal(2, new XboxTable(config).SlotCount);
    }

    // A relation bound to one hat direction (JOY_BTN_POV1_R) whose action the game has on Y drives Y, only there.
    [Fact]
    public void HatDirection_PressesItsPadButtonOnlyInThatDirection()
    {
        XboxTable table = new(Config(new XboxSlotConfig(0, "hatdir", 0, "XB_Y") { Direction = 2 }));
        table.Apply(0, State(hat: 9000)); // right
        Assert.Equal(XboxReport.Y, table.Report.Buttons);
        table.Apply(0, State(hat: 4500)); // up-right: another direction
        Assert.Equal(0, table.Report.Buttons);
        table.Apply(0, State(hat: 0)); // up
        Assert.Equal(0, table.Report.Buttons);
        table.Apply(0, State());
        Assert.Equal(0, table.Report.Buttons);
    }

    // The live test (2026-11-14): a twist (third device, axis Z) as "Axis ends to buttons" on LB/RB. A stream of states
    // from fully left to fully right must press LB, then nothing, then RB, merged into the pad report.
    [Fact]
    public void TwistOnLbRb_TheFunctionDrivesThePadButtons()
    {
        VirtualFunction twist = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.AxisButtons, "Yaw");
        twist.Id = "f1";
        twist.Normalize();
        twist.InputOf("in")!.Device = "G {00000000-0000-0000-0000-000000000003}";
        twist.InputOf("in")!.Input = "JOY_Z";
        twist.OutputOf("low")!.Target = "XB_LB";
        twist.OutputOf("high")!.Target = "XB_RB";
        twist.Params["threshold"] = 0.1;
        List<string> sources = [Stick, "T {00000000-0000-0000-0000-000000000002}", "G {00000000-0000-0000-0000-000000000003}"];
        Assert.Empty(VfResolver.Resolve(twist, sources, []));
        ForwarderConfig config = Config();
        config.Functions.Add(twist);
        Assert.True(config.UsesXbox);
        XboxTable table = new(config);
        VfRuntime runtime = new([twist], 3, 0);
        XboxReport report = default;
        ushort Pass(int z, long now)
        {
            byte[] state = State(z: z);
            table.Apply(2, state);
            runtime.SetState(2, state);
            runtime.Tick(now);
            table.MergeFunctions(runtime, ref report);
            return report.Buttons;
        }

        Assert.Equal(XboxReport.LeftShoulder, Pass(0, 0));
        Assert.Equal(XboxReport.LeftShoulder, Pass(1500, 10_000));
        Assert.Equal(0, Pass(16384, 20_000));
        Assert.Equal(0, Pass(28000, 30_000));
        Assert.Equal(XboxReport.RightShoulder, Pass(32767, 40_000));
        Assert.Equal(0, Pass(16384, 50_000));
    }

    // ACE COMBAT's High-G turn: one button holds both triggers fully; the throttle split on the triggers keeps working
    // (the larger value wins), so letting go gives the throttle back.
    [Fact]
    public void HighGButton_HoldsBothTriggers_OverTheThrottleSplit()
    {
        VirtualFunction highG = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Remap, "High-G turn");
        highG.Id = "f1";
        highG.Normalize();
        highG.InputOf("in")!.Device = Stick;
        highG.InputOf("in")!.Input = "JOY_BTN4";
        while (highG.Outputs.Count < 2)
            highG.Outputs.Add(new VfOutput { Role = "out" + (highG.Outputs.Count + 1) });
        highG.Outputs[0].Role = "out1";
        highG.Outputs[0].Target = "XB_LT";
        highG.Outputs[1].Role = "out2";
        highG.Outputs[1].Target = "XB_RT";
        Assert.Empty(VfResolver.Resolve(highG, [Stick], []));
        ForwarderConfig config = Config(new XboxSlotConfig(0, "axis", 0, "XB_TRIGGERS"));
        config.Functions.Add(highG);
        XboxTable table = new(config);
        VfRuntime runtime = new([highG], 3, 0);
        XboxReport report = default;
        (byte Lt, byte Rt) Pass(int lever, bool button, long now)
        {
            byte[] state = State(x: lever);
            state[ForwardingTable.ButtonOffset + 3] = (byte)(button ? 0x80 : 0);
            table.Apply(0, state);
            runtime.SetState(0, state);
            runtime.Tick(now);
            table.MergeFunctions(runtime, ref report);
            return (report.LeftTrigger, report.RightTrigger);
        }

        Assert.Equal(((byte)0, (byte)255), Pass(32767, false, 0)); // lever forward: RT
        Assert.Equal(((byte)255, (byte)255), Pass(32767, true, 10_000)); // High-G: both full
        Assert.Equal(((byte)0, (byte)255), Pass(32767, false, 20_000)); // let go: the throttle again
        Assert.Equal(((byte)255, (byte)0), Pass(0, false, 30_000)); // lever back: LT
    }

    // Flares (ACE COMBAT 8, unconfirmed): L3 + R3 pressed together for 120 ms by a macro with "pressed together".
    [Fact]
    public void ChordMacro_PressesItsStepsTogether()
    {
        VirtualFunction chord = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Macro, "Flares");
        chord.Id = "f1";
        chord.Normalize();
        chord.InputOf("trigger")!.Device = Stick;
        chord.InputOf("trigger")!.Input = "JOY_BTN3";
        chord.OutputOf("out1")!.Target = "XB_LS";
        chord.OutputOf("out2")!.Target = "XB_RS";
        chord.Steps = [new VfStep { Output = "out1", HoldMs = 120, DelayMs = 50 }, new VfStep { Output = "out2", HoldMs = 120 }];
        chord.Params["together"] = 1;
        Assert.Empty(VfResolver.Resolve(chord, [Stick], []));
        XboxTable table = new(Config());
        VfRuntime runtime = new([chord], 3, 0);
        XboxReport report = default;
        ushort Pass(bool button, long now)
        {
            byte[] state = State();
            state[ForwardingTable.ButtonOffset + 2] = (byte)(button ? 0x80 : 0);
            runtime.SetState(0, state);
            runtime.Tick(now);
            table.MergeFunctions(runtime, ref report);
            return report.Buttons;
        }

        Pass(false, 0);
        Assert.Equal(XboxReport.LeftThumb | XboxReport.RightThumb, Pass(true, 1_000));
        Assert.Equal(XboxReport.LeftThumb | XboxReport.RightThumb, Pass(false, 100_000));
        Assert.Equal(0, Pass(false, 130_000));
    }

    // The sign chain DirectInput -> forwarder -> XInput, exact values (ACE COMBAT 8 live test 2026-11-14, "rolling is
    // inverted"). DirectInput (DIPROP_RANGE 0..32767, the forwarder's range): X 0 = full left .. 32767 = full right;
    // Y 0 = pushed forward .. 32767 = pulled back. XInput: LX + = right, LY + = up. No curve, no invert: stick right ->
    // LX +32767, stick forward -> LY +32767 (the pad stick pushed up), before any game convention.
    private static byte[] StickState(int x, int y)
    {
        byte[] state = State(x: x);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(ForwardingTable.AxisOffset + 4, 4), y);
        return state;
    }

    [Fact]
    public void SignChain_DirectInputToXInput_ExactValues()
    {
        XboxTable table = new(Config(new XboxSlotConfig(0, "axis", 0, "XB_LX"), new XboxSlotConfig(0, "axis", 1, "XB_LY")));
        table.Apply(0, StickState(32767, 16384)); // full right
        Assert.Equal(32767, table.Report.LeftX);
        Assert.Equal(0, table.Report.LeftY);
        table.Apply(0, StickState(0, 16384)); // full left
        Assert.Equal(-32767, table.Report.LeftX);
        table.Apply(0, StickState(16384, 0)); // pushed forward
        Assert.Equal(0, table.Report.LeftX);
        Assert.Equal(32767, table.Report.LeftY);
        table.Apply(0, StickState(16384, 32767)); // pulled back
        Assert.Equal(-32767, table.Report.LeftY);
        table.Apply(0, StickState(16384, 16384)); // centred
        Assert.Equal(0, table.Report.LeftX);
        Assert.Equal(0, table.Report.LeftY);
    }

    // The same chain with the forwarder-config the engine wrote for the owner's profile (Holdi-2026-11-04, ACE COMBAT 8,
    // `vjoy forwarder-config --xbox-game AceCombat8`): Roll = Rhino JOY_X with the curve [-1 .. 1] (base, not inverted:
    // a straight line, so 1:1), Pitch = Rhino JOY_Y with [1 .. -1] (AceCombat8Integration.PadInvertDefault reverses
    // Pad_Pitch). Stick right -> LX +32767 (nothing reverses roll); stick forward -> LY -32767 (pad stick down).
    [Fact]
    public void SignChain_OwnersAceCombat8Config_RollStraight_PitchReversed()
    {
        const string json = """
        {"version":1,"vjoyDevices":[],"sources":[{"device":"VPforce Rhino FFB Joystick {AD5168C0-B52F-11f1-8001-444553540000}","name":"VPforce Rhino FFB Joystick","instanceGuid":"ad5168c0-b52f-11f1-8001-444553540000"}],"slots":[],
         "xboxGame":"AceCombat8","xbox":[{"source":0,"kind":"axis","index":0,"target":"XB_LX","curveScope":"base","curveRelation":"Roll","curve":[-1,-0.5,0,0.5,1]},
                                         {"source":0,"kind":"axis","index":1,"target":"XB_LY","curveScope":"base","curveRelation":"Pitch","curve":[1,0.5,0,-0.5,-1]}]}
        """;
        XboxTable table = new(ForwarderConfig.Parse(json));
        Assert.Equal(1, table.CurveCount); // roll's straight line needs no table
        table.Apply(0, StickState(32767, 16384)); // stick full right
        Assert.Equal(32767, table.Report.LeftX);
        table.Apply(0, StickState(0, 16384)); // stick full left
        Assert.Equal(-32767, table.Report.LeftX);
        table.Apply(0, StickState(16384, 0)); // stick forward
        Assert.Equal(-32767, table.Report.LeftY);
        table.Apply(0, StickState(16384, 32767)); // stick back
        Assert.Equal(32767, table.Report.LeftY);
    }

    // Look around on a hat (the owner, 2026-11-14: the right stick on the topmost hat): "Hat to axes" on RX/RY. Hat up ->
    // the right stick pushed up (RY +32767), right -> RX +32767, a diagonal both, centred -> both 0.
    [Fact]
    public void HatToAxes_DrivesTheRightStick_FullWhileHeld()
    {
        VirtualFunction look = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.HatAxes, "Look");
        look.Id = "f1";
        look.Normalize();
        look.InputOf("hat")!.Device = Stick;
        look.InputOf("hat")!.Input = "POV1";
        look.OutputOf("x")!.Target = "XB_RX";
        look.OutputOf("y")!.Target = "XB_RY";
        Assert.Empty(VirtualFunctionCatalog.Validate(look));
        Assert.Empty(VfResolver.Resolve(look, [Stick], []));
        ForwarderConfig config = Config();
        config.Functions.Add(look);
        Assert.True(config.UsesXbox);
        XboxTable table = new(config);
        VfRuntime runtime = new([look], 3, 0);
        XboxReport report = default;
        (short Rx, short Ry) Pass(uint hat, long now)
        {
            byte[] state = State(hat: hat);
            table.Apply(0, state);
            runtime.SetState(0, state);
            runtime.Tick(now);
            table.MergeFunctions(runtime, ref report);
            return (report.RightX, report.RightY);
        }

        Assert.Equal(((short)0, (short)0), Pass(ForwardingTable.HatCentred, 0));
        Assert.Equal(((short)0, (short)32767), Pass(0, 10_000)); // up
        Assert.Equal(((short)32767, (short)0), Pass(9000, 20_000)); // right
        Assert.Equal(((short)0, (short)-32767), Pass(18000, 30_000)); // down
        Assert.Equal(((short)-32767, (short)0), Pass(27000, 40_000)); // left
        Assert.Equal(((short)-32767, (short)-32767), Pass(22500, 50_000)); // down-left
        Assert.Equal(((short)0, (short)0), Pass(ForwardingTable.HatCentred, 60_000));
        // The left stick (flight) is untouched.
        Assert.Equal(0, report.LeftX);
        Assert.Equal(0, report.LeftY);
    }

    [Fact]
    public void BenchCurveIsNotAStraightLine()
    {
        // The bench's --xbox-curves must measure real lookups (a straight line would skip the table).
        double[] curve = AxisModels.Sample(AxisModels.Get(AxisModels.Dcs), new AxisTuning { Deadzone = 0.05, SaturationY = 0.9, Curvature = [0.3] }, 1025).Select(p => p.Value).ToArray();
        Assert.NotNull(XboxTable.BuildCurveTable(curve, 0, 32767));
    }
}
