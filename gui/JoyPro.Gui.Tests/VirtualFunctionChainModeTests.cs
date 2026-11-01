using System;
using System.Collections.Generic;
using System.Linq;
using JoyPro.Engine.VJoy;
using Xunit;

namespace JoyPro.Gui.Tests;

// Function chaining (a function reads another function's output, evaluated in dependency order every pass, loops
// refused), modes (named layers with inheritance, switched by mode switch functions; a mode's own function takes an
// input over), the remap type and the idle hint (NeedsClock) of engine/VJoy/VirtualFunctionRuntime.cs - the code the
// forwarder runs (docs/virtual-joystick.md "Function chaining", "Modes").
public sealed class VirtualFunctionChainModeTests
{
    private const string S0 = "Stick {00000000-0000-0000-0000-0000000000A0}";
    private const string S1 = "Throttle {00000000-0000-0000-0000-0000000000A1}";
    private static readonly List<string> Sources = [S0, S1];

    private static VirtualFunction Fn(string id, string type, Action<VirtualFunction> setup)
    {
        VirtualFunction function = VirtualFunctionCatalog.Create(type, id);
        function.Id = id;
        function.Inputs.Clear();
        function.Outputs.Clear();
        setup(function);
        function.Normalize();
        return function;
    }

    private static VfInput In(string role, string device, string input) => new() { Role = role, Device = device, Input = input };

    private static VfOutput Out(string role, string target = "") => new() { Role = role, Target = target, VJoyDevice = target.Length > 0 ? 1 : 0, Mode = role.StartsWith("band", StringComparison.Ordinal) ? "hold" : "" };

    private static VfRuntime Build(IList<VirtualFunction> functions, IList<VfModeSet>? modes = null)
    {
        foreach (VirtualFunction function in functions)
            VfResolver.Resolve(function, Sources, [1]);
        return new VfRuntime(functions, Sources.Count, 1, 0, 32767, null, modes);
    }

    private static void Axis(VfRuntime rt, int source, int axis, double unit) =>
        VfRuntime.WriteInt(rt.State(source), VfRuntime.AxisOffset + axis * 4, (int)Math.Round(unit * 32767));

    private static void Button(VfRuntime rt, int source, int button, bool down) =>
        rt.State(source)[VfRuntime.ButtonOffset + button - 1] = down ? (byte)0x80 : (byte)0;

    private static bool VButton(VfRuntime rt, int button) => (rt.Buttons[(button - 1) >> 5] & (1 << ((button - 1) & 31))) != 0;

    private static double VAxis(VfRuntime rt, int axis) => rt.Axes[axis] / 32767.0;

    private static VirtualFunction BandsToToggleProducer() => Fn("f1", VirtualFunctionCatalog.Bands, f =>
    {
        f.Edges = [0.5];
        f.Params["hysteresis"] = 0.0;
        f.Inputs.Add(In("in", S0, "JOY_X"));
        f.Outputs.Add(Out("band1"));
        f.Outputs.Add(Out("band2")); // internal: only the toggle reads it
    });

    private static VirtualFunction BandsToToggleConsumer() => Fn("f2", VirtualFunctionCatalog.Toggle, f =>
    {
        f.Inputs.Add(In("in", VfChain.Device("f1"), "band2"));
        f.Outputs.Add(Out("out", "JOY_BTN10"));
    });

    [Fact]
    public void AChainedOutputFeedsTheNextFunctionInTheSamePass()
    {
        VfRuntime rt = Build([BandsToToggleProducer(), BandsToToggleConsumer()]);
        Assert.Empty(rt.Problems);
        Axis(rt, 0, 0, 0.2);
        rt.Tick(0);
        Assert.False(VButton(rt, 10));
        Axis(rt, 0, 0, 0.9);
        rt.Tick(10_000);
        Assert.True(VButton(rt, 10)); // the band entered and the toggle latched in one pass
        Axis(rt, 0, 0, 0.2);
        rt.Tick(20_000);
        Assert.True(VButton(rt, 10)); // latched
        Axis(rt, 0, 0, 0.9);
        rt.Tick(30_000);
        Assert.False(VButton(rt, 10));
        // The internal output writes nothing to vJoy: no owned button besides the toggle's.
        Assert.Equal(1 << 9, rt.OwnedButtons[0]);
    }

    [Fact]
    public void DependencyOrderDoesNotDependOnTheListOrder()
    {
        VfRuntime rt = Build([BandsToToggleConsumer(), BandsToToggleProducer()]);
        Assert.Equal(["f1", "f2"], rt.Functions.Select(f => f.Id).ToArray());
        Axis(rt, 0, 0, 0.2);
        rt.Tick(0);
        Axis(rt, 0, 0, 0.9);
        rt.Tick(10_000);
        Assert.True(VButton(rt, 10));
    }

    [Fact]
    public void LoopsAreRefusedWithAClearMessage()
    {
        VirtualFunction a = Fn("f1", VirtualFunctionCatalog.Toggle, f =>
        {
            f.Name = "A";
            f.Inputs.Add(In("in", VfChain.Device("f2"), "out"));
            f.Outputs.Add(Out("out", "JOY_BTN1"));
        });
        VirtualFunction b = Fn("f2", VirtualFunctionCatalog.Toggle, f =>
        {
            f.Name = "B";
            f.Inputs.Add(In("in", VfChain.Device("f1"), "out"));
            f.Outputs.Add(Out("out", "JOY_BTN2"));
        });
        VirtualFunction reader = Fn("f3", VirtualFunctionCatalog.Toggle, f =>
        {
            f.Name = "C";
            f.Inputs.Add(In("in", VfChain.Device("f2"), "out"));
            f.Outputs.Add(Out("out", "JOY_BTN3"));
        });
        string problem = VfChain.LoopProblem([a, b, reader]);
        Assert.Contains("loop", problem);
        Assert.Contains("f1 \"A\"", problem);
        Assert.Contains("f2 \"B\"", problem);
        VfRuntime rt = Build([a, b, reader]);
        Assert.Empty(rt.Functions);
        Assert.Contains(rt.Problems, p => p.Contains("read each other in a loop"));
        Assert.Contains(rt.Problems, p => p.Contains("f3 \"C\" reads a function in a loop"));

        VirtualFunction self = Fn("f4", VirtualFunctionCatalog.Toggle, f =>
        {
            f.Inputs.Add(In("in", VfChain.Device("f4"), "out"));
            f.Outputs.Add(Out("out", "JOY_BTN4"));
        });
        Assert.Contains(VirtualFunctionCatalog.Validate(self, [self], null), p => p.Contains("cannot read its own output"));
        Assert.Contains("cannot read itself", VfChain.LoopProblem([self]));
    }

    [Fact]
    public void ValidationChecksTheChainedInputAgainstTheGame()
    {
        VirtualFunction producer = BandsToToggleProducer();
        VirtualFunction consumer = BandsToToggleConsumer();
        List<VirtualFunction> all = [producer, consumer];
        // The producer's band2 feeds the toggle: no vJoy target needed (the bands' roles are optional anyway).
        Assert.Empty(VirtualFunctionCatalog.Validate(consumer, all, VfChain.ChainedRoles(consumer, all)));
        VirtualFunction missing = Fn("f5", VirtualFunctionCatalog.Toggle, f =>
        {
            f.Inputs.Add(In("in", VfChain.Device("f9"), "out"));
            f.Outputs.Add(Out("out", "JOY_BTN5"));
        });
        Assert.Contains(VirtualFunctionCatalog.Validate(missing, [producer, missing], null), p => p.Contains("does not exist"));
        VirtualFunction wrongKind = Fn("f6", VirtualFunctionCatalog.Curve, f =>
        {
            f.Inputs.Add(In("in", VfChain.Device("f1"), "band1"));
            f.Outputs.Add(Out("out", "JOY_Z"));
        });
        Assert.Contains(VirtualFunctionCatalog.Validate(wrongKind, [producer, wrongKind], null), p => p.Contains("needs an axis"));
        // A required output that another function reads needs no target.
        VirtualFunction curve = Fn("f7", VirtualFunctionCatalog.Curve, f =>
        {
            f.Inputs.Add(In("in", S1, "JOY_Z"));
            f.Outputs.Add(Out("out"));
        });
        VirtualFunction split = Fn("f8", VirtualFunctionCatalog.Split, f =>
        {
            f.Inputs.Add(In("in", VfChain.Device("f7"), "out"));
            f.Outputs.Add(Out("low", "JOY_RX"));
            f.Outputs.Add(Out("high", "JOY_RY"));
        });
        List<VirtualFunction> pair = [curve, split];
        Assert.Contains(VirtualFunctionCatalog.Validate(curve), p => p.Contains("no virtual output"));
        Assert.Empty(VirtualFunctionCatalog.Validate(curve, pair, VfChain.ChainedRoles(curve, pair)));
        Assert.Empty(VirtualFunctionCatalog.Validate(split, pair, VfChain.ChainedRoles(split, pair)));
    }

    [Fact]
    public void AnInternalAxisFeedsAnotherFunction()
    {
        VirtualFunction curve = Fn("f7", VirtualFunctionCatalog.Curve, f =>
        {
            f.Tuning = "inv=1;sl=0;dz=0;sx=1;sy=1;cv=0";
            f.Inputs.Add(In("in", S1, "JOY_Z"));
            f.Outputs.Add(Out("out"));
        });
        VirtualFunction split = Fn("f8", VirtualFunctionCatalog.Split, f =>
        {
            f.Inputs.Add(In("in", VfChain.Device("f7"), "out"));
            f.Outputs.Add(Out("low", "JOY_RX"));
            f.Outputs.Add(Out("high", "JOY_RY"));
        });
        VfRuntime rt = Build([split, curve]);
        Assert.Empty(rt.Problems);
        Axis(rt, 1, 2, 0.0); // inverted by the curve: 1.0 into the split -> high end full
        rt.Tick(0);
        Assert.Equal(1.0, VAxis(rt, 4), 2);
        Assert.Equal(0.0, VAxis(rt, 3), 2);
        Axis(rt, 1, 2, 1.0);
        rt.Tick(10_000);
        Assert.Equal(1.0, VAxis(rt, 3), 2);
        Assert.False(rt.AxisOwned[2]); // the internal output owns no vJoy axis
    }

    [Fact]
    public void RemapFollowsTheInputOnEveryOutput()
    {
        VirtualFunction buttons = Fn("r1", VirtualFunctionCatalog.Remap, f =>
        {
            f.Inputs.Add(In("in", S0, "JOY_BTN3"));
            f.Outputs.Add(Out("out1", "JOY_BTN20"));
            f.Outputs.Add(Out("out2", "JOY_SLIDER1"));
        });
        VirtualFunction axis = Fn("r2", VirtualFunctionCatalog.Remap, f =>
        {
            f.Params["invert"] = 1;
            f.Inputs.Add(In("in", S1, "JOY_X"));
            f.Outputs.Add(Out("out1", "JOY_Z"));
        });
        VfRuntime rt = Build([buttons, axis]);
        Assert.Empty(rt.Problems);
        Axis(rt, 1, 0, 0.25);
        rt.Tick(0);
        Assert.False(VButton(rt, 20));
        Assert.Equal(0.0, VAxis(rt, 6), 3); // a button on an axis: at rest = low end
        Assert.Equal(0.75, VAxis(rt, 2), 2);
        Button(rt, 0, 3, true);
        rt.Tick(10_000);
        Assert.True(VButton(rt, 20));
        Assert.Equal(1.0, VAxis(rt, 6), 3);
        Assert.False(rt.NeedsClock); // a remap only reacts to input changes
    }

    // Default <- Combat, Default <- Landing; start Default.
    private static VfModeSet Modes(string start = "Default") => new()
    {
        Group = "DCS",
        Start = start,
        Modes = [new VfMode { Name = "Default" }, new VfMode { Name = "Combat", Parent = "Default" }, new VfMode { Name = "Landing", Parent = "Default" }]
    };

    private static List<VirtualFunction> ModeFunctions() =>
    [
        Fn("m1", VirtualFunctionCatalog.Remap, f => { f.Inputs.Add(In("in", S0, "JOY_BTN1")); f.Outputs.Add(Out("out1", "JOY_BTN1")); }),
        Fn("m2", VirtualFunctionCatalog.Remap, f => { f.Modes = ["Combat"]; f.Inputs.Add(In("in", S0, "JOY_BTN1")); f.Outputs.Add(Out("out1", "JOY_BTN30")); }),
        Fn("m3", VirtualFunctionCatalog.Remap, f => { f.Modes = ["Default"]; f.Inputs.Add(In("in", S0, "JOY_BTN2")); f.Outputs.Add(Out("out1", "JOY_BTN2")); }),
        Fn("m4", VirtualFunctionCatalog.ModeSwitch, f => { f.Params["action"] = VirtualFunctionCatalog.ModeActionHold; f.SwitchModes = ["Landing"]; f.Inputs.Add(In("in", S0, "JOY_BTN5")); }),
        Fn("m5", VirtualFunctionCatalog.ModeSwitch, f => { f.Params["action"] = VirtualFunctionCatalog.ModeActionCycle; f.SwitchModes = ["Default", "Combat", "Landing"]; f.Inputs.Add(In("in", S0, "JOY_BTN6")); })
    ];

    [Fact]
    public void ModesInheritAndAModesOwnFunctionTakesTheInputOver()
    {
        VfModeSet set = Modes();
        List<VirtualFunction> functions = ModeFunctions();
        bool[][] table = set.ActiveTable(functions);
        // m1 (no modes) works in Default and Landing; in Combat m2 takes JOY_BTN1 over.
        Assert.Equal([true, false, true], table[0]);
        Assert.Equal([false, true, false], table[1]);
        // m3 is Default's and inherited by both children.
        Assert.Equal([true, true, true], table[2]);
        Assert.Equal("Default, Combat (inherited), Landing (inherited)", set.Describe(functions[2], functions));
        Assert.Contains("does not exist", set.Check(Fn("x", VirtualFunctionCatalog.Remap, f => f.Modes = ["Taxi"]))[0]);
    }

    [Fact]
    public void ModeSwitchesCycleHoldAndComeBack()
    {
        VfRuntime rt = Build(ModeFunctions(), [Modes()]);
        Assert.Empty(rt.Problems);
        Assert.Equal("Default", rt.CurrentModeName(0));
        Button(rt, 0, 1, true);
        rt.Tick(0);
        Assert.True(VButton(rt, 1));
        Assert.False(VButton(rt, 30));
        // Cycle to Combat: the switch takes effect after the pass.
        Button(rt, 0, 6, true);
        rt.Tick(10_000);
        Assert.Equal("Combat", rt.CurrentModeName(0));
        Assert.True(rt.NeedsClock);
        rt.Tick(20_000);
        Assert.True(VButton(rt, 30));
        Assert.False(VButton(rt, 1)); // owned by m1, which does not run in Combat
        Button(rt, 0, 2, true);
        rt.Tick(30_000);
        Assert.True(VButton(rt, 2)); // inherited from Default
        // Hold Landing while button 5 is held, then back to Combat.
        Button(rt, 0, 6, false);
        Button(rt, 0, 5, true);
        rt.Tick(40_000);
        Assert.Equal("Landing", rt.CurrentModeName(0));
        rt.Tick(50_000);
        Assert.True(VButton(rt, 1));
        Button(rt, 0, 5, false);
        rt.Tick(60_000);
        Assert.Equal("Combat", rt.CurrentModeName(0));
        Assert.Equal(3, rt.ModeChanges(0));
    }

    [Fact]
    public void AHeldButtonIsNoNewPressInTheNewMode()
    {
        // Button 7 switches Default -> Combat; in Combat the same button switches back. One press = one switch.
        List<VirtualFunction> functions =
        [
            Fn("s1", VirtualFunctionCatalog.ModeSwitch, f => { f.Modes = ["Default"]; f.SwitchModes = ["Combat"]; f.Inputs.Add(In("in", S0, "JOY_BTN7")); }),
            Fn("s2", VirtualFunctionCatalog.ModeSwitch, f => { f.Modes = ["Combat"]; f.SwitchModes = ["Default"]; f.Inputs.Add(In("in", S0, "JOY_BTN7")); }),
            Fn("t1", VirtualFunctionCatalog.Toggle, f => { f.Modes = ["Combat"]; f.Inputs.Add(In("in", S0, "JOY_BTN7")); f.Outputs.Add(Out("out", "JOY_BTN40")); })
        ];
        VfRuntime rt = Build(functions, [Modes()]);
        Button(rt, 0, 7, true);
        for (int i = 0; i < 5; i++)
            rt.Tick(i * 10_000);
        Assert.Equal("Combat", rt.CurrentModeName(0));
        Assert.False(VButton(rt, 40)); // the toggle saw the held button as it was, not as a press
        Button(rt, 0, 7, false);
        rt.Tick(60_000);
        Button(rt, 0, 7, true);
        rt.Tick(70_000);
        Assert.Equal("Default", rt.CurrentModeName(0));
        Assert.True(VButton(rt, 40)); // the toggle in Combat took this press too, in the same pass
    }

    [Fact]
    public void TwoGamesKeepTheirOwnModes()
    {
        VirtualFunction dcs = Fn("g1", VirtualFunctionCatalog.ModeSwitch, f => { f.Group = "DCS"; f.SwitchModes = ["Combat"]; f.Inputs.Add(In("in", S0, "JOY_BTN1")); });
        VirtualFunction il2 = Fn("g2", VirtualFunctionCatalog.Remap, f => { f.Group = "IL2"; f.Modes = ["Taxi"]; f.Inputs.Add(In("in", S0, "JOY_BTN2")); f.Outputs.Add(Out("out1", "JOY_BTN50")); });
        VfModeSet il2Modes = new() { Group = "IL2", Modes = [new VfMode { Name = "Flight" }, new VfMode { Name = "Taxi" }], Start = "Taxi" };
        VfRuntime rt = Build([dcs, il2], [Modes(), il2Modes]);
        Assert.Equal(2, rt.ModeGroupCount);
        Button(rt, 0, 1, true);
        Button(rt, 0, 2, true);
        rt.Tick(0);
        rt.Tick(10_000);
        Assert.Equal("Combat", rt.CurrentModeName(0));
        Assert.Equal("Taxi", rt.CurrentModeName(1));
        Assert.True(VButton(rt, 50));
    }

    [Fact]
    public void NeedsClockOnlyWhileSomethingRuns()
    {
        VirtualFunction turbo = Fn("c1", VirtualFunctionCatalog.Turbo, f => { f.Inputs.Add(In("in", S0, "JOY_BTN1")); f.Outputs.Add(Out("out", "JOY_BTN60")); });
        VirtualFunction presses = Fn("c2", VirtualFunctionCatalog.Presses, f => { f.Inputs.Add(In("in", S0, "JOY_BTN2")); f.Outputs.Add(Out("short", "JOY_BTN61")); f.Outputs.Add(Out("long", "JOY_BTN62")); });
        VfRuntime rt = Build([turbo, presses]);
        rt.Tick(0);
        Assert.False(rt.NeedsClock);
        Button(rt, 0, 1, true);
        rt.Tick(10_000);
        Assert.True(rt.NeedsClock); // turbo runs
        Button(rt, 0, 1, false);
        rt.Tick(20_000);
        Assert.False(rt.NeedsClock);
        Button(rt, 0, 2, true);
        rt.Tick(30_000);
        Assert.True(rt.NeedsClock); // waits for the long press
        Button(rt, 0, 2, false);
        rt.Tick(40_000);
        Assert.True(rt.NeedsClock); // the short pulse runs
        rt.Tick(200_000);
        Assert.False(rt.NeedsClock);
    }

    // NextDueUs: the forwarder sleeps until a timed function's next edge instead of running every period
    // (docs/virtual-joystick.md "The forwarder"). The time is exact and a pass at it completes the edge.
    [Fact]
    public void NextDueIsTheNextTimedEdge()
    {
        VirtualFunction turbo = Fn("c1", VirtualFunctionCatalog.Turbo, f => { f.Inputs.Add(In("in", S0, "JOY_BTN1")); f.Outputs.Add(Out("out", "JOY_BTN60")); });
        VirtualFunction presses = Fn("c2", VirtualFunctionCatalog.Presses, f => { f.Inputs.Add(In("in", S0, "JOY_BTN2")); f.Outputs.Add(Out("short", "JOY_BTN61")); f.Outputs.Add(Out("long", "JOY_BTN62")); });
        VirtualFunction axis = Fn("c3", VirtualFunctionCatalog.ButtonsAxis, f => { f.Inputs.Add(In("up", S0, "JOY_BTN3")); f.Outputs.Add(Out("out", "JOY_Z")); });
        VfRuntime rt = Build([turbo, presses, axis]);
        Assert.Empty(rt.Problems);
        rt.Tick(0);
        Assert.Equal(long.MaxValue, rt.NextDueUs);
        Button(rt, 0, 1, true);
        rt.Tick(10_000);
        Assert.Equal(60_000, rt.NextDueUs); // turbo: on for 50 ms
        Assert.True(VButton(rt, 60));
        rt.Tick(60_000);
        Assert.False(VButton(rt, 60));
        Assert.Equal(110_000, rt.NextDueUs); // off for 50 ms
        Button(rt, 0, 1, false);
        Button(rt, 0, 2, true);
        rt.Tick(70_000);
        Assert.Equal(570_000, rt.NextDueUs); // the long press
        rt.Tick(570_000);
        Assert.True(VButton(rt, 62));
        Button(rt, 0, 2, false);
        rt.Tick(600_000);
        Assert.Equal(long.MaxValue, rt.NextDueUs);
        Button(rt, 0, 3, true);
        rt.Tick(610_000);
        Assert.Equal(610_000, rt.NextDueUs); // a moving axis: the very next pass
    }

    // A runtime ticked only at input changes and at NextDueUs gives the same outputs as one ticked every millisecond.
    [Fact]
    public void TickingAtNextDueMatchesTickingEveryPass()
    {
        List<VirtualFunction> Functions() =>
        [
            Fn("t1", VirtualFunctionCatalog.Turbo, f => { f.Params["onMs"] = 37; f.Params["offMs"] = 61; f.Inputs.Add(In("in", S0, "JOY_BTN1")); f.Outputs.Add(Out("out", "JOY_BTN60")); }),
            Fn("t2", VirtualFunctionCatalog.Turbo, f => { f.Params["toggle"] = 1; f.Inputs.Add(In("in", S0, "JOY_BTN8")); f.Outputs.Add(Out("out", "JOY_BTN59")); }),
            Fn("p1", VirtualFunctionCatalog.Presses, f => { f.Inputs.Add(In("in", S0, "JOY_BTN2")); f.Outputs.Add(Out("short", "JOY_BTN61")); f.Outputs.Add(Out("long", "JOY_BTN62")); f.Outputs.Add(Out("double", "JOY_BTN63")); }),
            Fn("p2", VirtualFunctionCatalog.Presses, f => { f.Params["longHold"] = 0; f.Inputs.Add(In("in", S0, "JOY_BTN9")); f.Outputs.Add(Out("short", "JOY_BTN57")); f.Outputs.Add(Out("long", "JOY_BTN58")); }),
            Fn("m1", VirtualFunctionCatalog.Macro, f =>
            {
                f.Inputs.Add(In("trigger", S0, "JOY_BTN3"));
                f.Outputs.Add(Out("out1", "JOY_BTN64"));
                f.Outputs.Add(Out("out2", "JOY_BTN56"));
                f.Steps = [new VfStep { Output = "out1", DelayMs = 0, HoldMs = 40 }, new VfStep { Output = "out2", DelayMs = 25, HoldMs = 1 }, new VfStep { Output = "out1", DelayMs = 130, HoldMs = 80 }];
            }),
            Fn("h1", VirtualFunctionCatalog.ButtonsHat, f =>
            {
                f.Inputs.Add(In("up", S0, "JOY_BTN4"));
                f.Inputs.Add(In("right", S0, "JOY_BTN5"));
                f.Inputs.Add(In("down", S0, "JOY_BTN6"));
                f.Inputs.Add(In("left", S0, "JOY_BTN7"));
                f.Outputs.Add(Out("hat", "POV1"));
            }),
            Fn("g1", VirtualFunctionCatalog.Toggle, f => { f.Inputs.Add(In("in", "fn:t1", "OUT")); f.Outputs.Add(Out("out", "JOY_BTN55")); }),
        ];
        VfRuntime every = Build(Functions());
        VfRuntime due = Build(Functions());
        Assert.Empty(every.Problems);
        Assert.Empty(due.Problems);
        // Input changes (ms, button, down): holds, taps, a double tap, a long press, a macro, diagonal hats, a toggle.
        (int Ms, int Button, bool Down)[] script =
        [
            (20, 1, true), (400, 1, false), (450, 2, true), (520, 2, false), (600, 2, true), (650, 2, false),
            (900, 2, true), (1600, 2, false), (1700, 3, true), (1720, 3, false), (1800, 4, true), (1810, 5, true),
            (1900, 4, false), (1905, 5, false), (2000, 8, true), (2050, 8, false), (2100, 9, true), (2800, 9, false),
            (2900, 8, true), (2950, 8, false), (3000, 6, true), (3001, 7, true), (3002, 6, false), (3100, 7, false),
        ];
        int[] buttons = new int[due.Buttons.Length];
        uint[] hats = new uint[due.Hats.Length];
        long nextDue = long.MaxValue;
        int dueTicks = 0;
        for (long ms = 0; ms <= 3600; ms++)
        {
            long now = ms * 1000;
            bool changed = false;
            foreach ((int Ms, int Button, bool Down) step in script.Where(s => s.Ms == ms))
            {
                Button(every, 0, step.Button, step.Down);
                Button(due, 0, step.Button, step.Down);
                changed = true;
            }
            every.Tick(now);
            if (changed || now >= nextDue || ms == 0)
            {
                due.Tick(now);
                dueTicks++;
                nextDue = due.NeedsClock ? Math.Max(due.NextDueUs, now + 1000) : long.MaxValue;
                Array.Copy(due.Buttons, buttons, buttons.Length);
                Array.Copy(due.Hats, hats, hats.Length);
            }
            Assert.True(every.Buttons.SequenceEqual(buttons), "buttons differ at " + ms + " ms");
            Assert.True(every.Hats.SequenceEqual(hats), "hats differ at " + ms + " ms");
        }
        Assert.True(dueTicks < 400, "ticks at the due times only: " + dueTicks);
    }

    [Fact]
    public void ChainsAndModesAllocateNothingPerPass()
    {
        List<VirtualFunction> functions = ModeFunctions();
        functions.Add(BandsToToggleProducer());
        functions.Add(BandsToToggleConsumer());
        VfRuntime rt = Build(functions, [Modes()]);
        for (int i = 0; i < 200; i++)
            rt.Tick(i * 1000L);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20_000; i++)
        {
            Axis(rt, 0, 0, i % 100 / 100.0);
            Button(rt, 0, 6, i % 50 < 25);
            Button(rt, 0, 5, i % 70 < 10);
            rt.Tick(200_000 + i * 1000L);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
