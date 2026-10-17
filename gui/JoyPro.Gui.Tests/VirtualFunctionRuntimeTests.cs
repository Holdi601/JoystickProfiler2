using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JoyPro.Engine.VJoy;
using JoyPro.VJoy;
using Xunit;
using Xunit.Abstractions;

namespace JoyPro.Gui.Tests;

// The virtual functions' evaluation (engine/VJoy/VirtualFunctionRuntime.cs, the same code the forwarder runs) with
// simulated input streams: band edges with hysteresis, cycle wrap, short/long/double timing, shift layers, merge/split
// math, the incremental axis rate, encoder pulses, turbo and macro timing, hat helpers; the merge into the vJoy
// positions and the Xbox report, the forwarder table's JSON, no allocation per pass and a loop benchmark.
public sealed class VirtualFunctionRuntimeTests(ITestOutputHelper output)
{
    private const double Rate = 240;

    private static VirtualFunction Make(string type, Action<VirtualFunction>? setup = null)
    {
        VirtualFunction function = VirtualFunctionCatalog.Create(type, type);
        function.Id = "t1";
        setup?.Invoke(function);
        return function;
    }

    // Steps the simulator from..to (ms) at Rate; returns the times (ms) at which the output went on.
    private static List<double> Run(VfSimulator sim, double from, double to, string output, Action<double>? drive = null)
    {
        List<double> rising = [];
        bool last = sim.IsOn(output);
        for (double t = from; t <= to + 1e-9; t += 1000.0 / Rate)
        {
            drive?.Invoke(t);
            sim.Step(t);
            bool on = sim.IsOn(output);
            if (on && !last)
                rising.Add(t);
            last = on;
        }
        return rising;
    }

    private static void Hold(VfSimulator sim, double from, double to)
    {
        for (double t = from; t <= to + 1e-9; t += 1000.0 / Rate)
            sim.Step(t);
    }

    [Fact]
    public void BandsChangeOnlyPastTheEdgePlusHysteresis()
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.Bands, f =>
        {
            f.Edges = [0.3, 0.7];
            f.Params["hysteresis"] = 0.02;
        }));
        sim.SetAxis("in", 0.5);
        sim.Step(0);
        Assert.Equal(1, sim.Compiled!.State);
        Assert.True(sim.IsOn("band2"));
        sim.SetAxis("in", 0.71); // past the edge, not past the hysteresis
        sim.Step(5);
        Assert.Equal(1, sim.Compiled.State);
        sim.SetAxis("in", 0.73);
        sim.Step(10);
        Assert.Equal(2, sim.Compiled.State);
        Assert.True(sim.IsOn("band3"));
        Assert.False(sim.IsOn("band2"));
        sim.SetAxis("in", 0.69); // back below the edge: stays until 0.68
        sim.Step(15);
        Assert.Equal(2, sim.Compiled.State);
        sim.SetAxis("in", 0.67);
        sim.Step(20);
        Assert.Equal(1, sim.Compiled.State);
        // A fast jump over a band goes straight to the last one.
        sim.SetAxis("in", 0.0);
        sim.Step(25);
        Assert.Equal(0, sim.Compiled.State);
        Assert.True(sim.IsOn("band1"));
    }

    [Fact]
    public void BandsPulseOnEnteringAndLeaving()
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.Bands, f =>
        {
            f.Edges = [0.5];
            f.Params["hysteresis"] = 0;
            f.Params["pulseMs"] = 80;
            f.OutputOf("band1")!.Target = "JOY_BTN1";
            f.OutputOf("band1")!.Mode = VirtualFunctionCatalog.ModeLeave;
            f.OutputOf("band2")!.Target = "JOY_BTN2";
            f.OutputOf("band2")!.Mode = VirtualFunctionCatalog.ModeEnter;
        }));
        sim.SetAxis("in", 0.2);
        sim.Step(0);
        Assert.False(sim.IsOn("band1")); // pulse modes do not hold
        sim.SetAxis("in", 0.8);
        sim.Step(100);
        Assert.True(sim.IsOn("band2")); // entered band 2
        Assert.True(sim.IsOn("band1")); // left band 1
        sim.Step(179);
        Assert.True(sim.IsOn("band2"));
        sim.Step(181);
        Assert.False(sim.IsOn("band2"));
        Assert.False(sim.IsOn("band1"));
    }

    [Fact]
    public void AxisEndsHoldTheirButtonsWithHysteresis()
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.AxisButtons, f =>
        {
            f.Params["threshold"] = 0.1;
            f.Params["hysteresis"] = 0.02;
        }));
        sim.SetAxis("in", 0.5);
        sim.Step(0);
        Assert.False(sim.IsOn("low"));
        sim.SetAxis("in", 0.07);
        sim.Step(5);
        Assert.True(sim.IsOn("low"));
        sim.SetAxis("in", 0.11);
        sim.Step(10);
        Assert.True(sim.IsOn("low")); // within the hysteresis
        sim.SetAxis("in", 0.13);
        sim.Step(15);
        Assert.False(sim.IsOn("low"));
        sim.SetAxis("in", 0.95);
        sim.Step(20);
        Assert.True(sim.IsOn("high"));
    }

    private static List<string> PulsesOfCycle(VfSimulator sim, string role, int presses, ref double t)
    {
        List<string> fired = [];
        for (int i = 0; i < presses; i++)
        {
            sim.SetButton(role, true);
            sim.Step(t);
            fired.Add(string.Join(",", Enumerable.Range(1, 3).Where(s => sim.IsOn("step" + s)).Select(s => "step" + s)));
            t += 50;
            sim.SetButton(role, false);
            sim.Step(t);
            t += 150; // past the pulse
            sim.Step(t);
        }
        return fired;
    }

    [Fact]
    public void CycleStepsAndWraps()
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.Cycle));
        double t = 0;
        sim.Step(t);
        Assert.Equal(-1, sim.Compiled!.State);
        Assert.Equal(["step1", "step2", "step3", "step1"], PulsesOfCycle(sim, "next", 4, ref t));
        Assert.Equal(["step3"], PulsesOfCycle(sim, "prev", 1, ref t)); // back from step 1 wraps to step 3
        sim.SetButton("reset", true);
        sim.Step(t += 10);
        sim.SetButton("reset", false);
        sim.Step(t += 200);
        Assert.Equal(-1, sim.Compiled.State);
        Assert.Equal(["step1"], PulsesOfCycle(sim, "next", 1, ref t));
    }

    [Fact]
    public void CycleWithoutWrapStaysAtTheEndAndHoldModeHolds()
    {
        VfSimulator stay = new(Make(VirtualFunctionCatalog.Cycle, f => f.Params["wrap"] = 0));
        double t = 0;
        Assert.Equal(["step1", "step2", "step3", ""], PulsesOfCycle(stay, "next", 4, ref t));
        Assert.Equal(2, stay.Compiled!.State);

        VfSimulator hold = new(Make(VirtualFunctionCatalog.Cycle, f => f.Params["hold"] = 1));
        hold.Step(0);
        Assert.True(hold.IsOn("step1"));
        hold.SetButton("next", true);
        hold.Step(10);
        hold.SetButton("next", false);
        hold.Step(1000);
        Assert.True(hold.IsOn("step2"));
        Assert.False(hold.IsOn("step1"));
    }

    [Fact]
    public void ToggleLatchesAndSwitchGivesSeparatePulses()
    {
        VfSimulator toggle = new(Make(VirtualFunctionCatalog.Toggle));
        toggle.Step(0);
        Assert.False(toggle.IsOn("out"));
        toggle.SetButton("in", true);
        toggle.Step(10);
        toggle.SetButton("in", false);
        toggle.Step(500);
        Assert.True(toggle.IsOn("out"));
        toggle.SetButton("in", true);
        toggle.Step(510);
        Assert.False(toggle.IsOn("out"));

        VfSimulator sw = new(Make(VirtualFunctionCatalog.Switch, f => f.Params["syncAtStart"] = 1));
        sw.Step(0);
        Assert.True(sw.IsOn("off")); // the position at start
        sw.Step(100);
        Assert.False(sw.IsOn("off"));
        sw.SetButton("in", true);
        sw.Step(200);
        Assert.True(sw.IsOn("on"));
        Assert.False(sw.IsOn("off"));
        sw.Step(300);
        Assert.False(sw.IsOn("on"));
        sw.SetButton("in", false);
        sw.Step(400);
        Assert.True(sw.IsOn("off"));
    }

    [Fact]
    public void ShortAndLongPressWithoutDoubleFireAtOnce()
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.Presses, f =>
        {
            f.OutputOf("short")!.Target = "JOY_BTN1";
            f.OutputOf("long")!.Target = "JOY_BTN2";
            f.Params["longMs"] = 500;
        }));
        Assert.Null(sim.Output("double") is { Configured: true } ? "configured" : null);
        // Short: released after 100 ms, the short pulse comes at the release (no double press to wait for).
        sim.SetButton("in", true);
        Hold(sim, 0, 100);
        sim.SetButton("in", false);
        sim.Step(105);
        Assert.True(sim.IsOn("short"));
        Hold(sim, 110, 400);
        Assert.False(sim.IsOn("short"));
        // Long: held 800 ms, the long output holds from 500 ms until the release.
        sim.SetButton("in", true);
        Hold(sim, 1000, 1490);
        Assert.False(sim.IsOn("long"));
        Hold(sim, 1495, 1510);
        Assert.True(sim.IsOn("long"));
        Hold(sim, 1515, 1800);
        Assert.True(sim.IsOn("long"));
        sim.SetButton("in", false);
        sim.Step(1805);
        Assert.False(sim.IsOn("long"));
        Assert.False(sim.IsOn("short"));
    }

    [Fact]
    public void DoublePressWaitsForTheSecondPress()
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.Presses, f =>
        {
            f.OutputOf("short")!.Target = "JOY_BTN1";
            f.Outputs.Add(new VfOutput { Role = "double", Target = "JOY_BTN3" });
            f.Params["doubleMs"] = 300;
        }));
        // Two quick presses: double, no short.
        sim.SetButton("in", true);
        Hold(sim, 0, 60);
        sim.SetButton("in", false);
        Hold(sim, 65, 150);
        sim.SetButton("in", true);
        sim.Step(155);
        Assert.True(sim.IsOn("double"));
        sim.SetButton("in", false);
        Hold(sim, 160, 700);
        Assert.False(sim.IsOn("short"));
        // One press: the short pulse comes doubleMs after the release.
        sim.SetButton("in", true);
        Hold(sim, 1000, 1060);
        sim.SetButton("in", false);
        List<double> shorts = Run(sim, 1065, 1500, "short");
        Assert.Single(shorts);
        Assert.InRange(shorts[0], 1355, 1375);
    }

    [Fact]
    public void ShiftLayerMovesButtonsAndFreezesAxes()
    {
        VfSimulator buttons = new(Make(VirtualFunctionCatalog.Shift, f =>
        {
            f.OutputOf("out1")!.Target = "JOY_BTN11";
            f.Outputs.Add(new VfOutput { Role = "base1", Target = "JOY_BTN10", VJoyDevice = 1 });
        }));
        buttons.SetButton("in1", true);
        buttons.Step(0);
        Assert.True(buttons.IsOn("base1"));
        Assert.False(buttons.IsOn("out1"));
        buttons.SetButton("shift", true);
        buttons.Step(10);
        Assert.False(buttons.IsOn("base1"));
        Assert.True(buttons.IsOn("out1"));

        VfSimulator axes = new(Make(VirtualFunctionCatalog.Shift, f =>
        {
            f.InputOf("in1")!.Device = "Stick {00000000-0000-0000-0000-000000000001}";
            f.InputOf("in1")!.Input = "JOY_X";
            f.OutputOf("out1")!.Target = "JOY_Y";
            f.Outputs.Add(new VfOutput { Role = "base1", Target = "JOY_X", VJoyDevice = 1 });
        }));
        axes.SetAxis("in1", 0.8);
        axes.Step(0);
        Assert.Equal(0.8, axes.AxisValue("base1"), 3);
        axes.SetButton("shift", true);
        axes.SetAxis("in1", 0.2);
        axes.Step(10);
        Assert.Equal(0.2, axes.AxisValue("out1"), 3);
        Assert.Equal(0.8, axes.AxisValue("base1"), 3); // frozen while shifted
        axes.SetButton("shift", false);
        axes.SetAxis("in1", 0.4);
        axes.Step(20);
        Assert.Equal(0.4, axes.AxisValue("base1"), 3);
        Assert.Equal(0.2, axes.AxisValue("out1"), 3);
    }

    [Fact]
    public void ShiftLatchToggles()
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.Shift, f => f.Params["latch"] = 1));
        sim.SetButton("shift", true);
        sim.Step(0);
        sim.SetButton("shift", false);
        sim.Step(10);
        Assert.Equal(1, sim.Compiled!.State);
        sim.SetButton("shift", true);
        sim.Step(20);
        Assert.Equal(0, sim.Compiled.State);
    }

    [Theory]
    [InlineData(0, 1.0, 0.0, 1.0)]
    [InlineData(0, 0.0, 1.0, 0.0)]
    [InlineData(0, 0.4, 0.4, 0.5)]
    [InlineData(1, 0.3, 0.4, 0.7)]
    [InlineData(1, 0.8, 0.8, 1.0)]
    [InlineData(2, 0.2, 0.6, 0.4)]
    [InlineData(3, 0.2, 0.6, 0.6)]
    [InlineData(4, 0.2, 0.6, 0.2)]
    public void MergeMath(int mode, double a, double b, double expected)
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.Merge, f => f.Params["mode"] = mode));
        sim.SetAxis("a", a);
        sim.SetAxis("b", b);
        sim.Step(0);
        Assert.Equal(expected, sim.AxisValue("out"), 3);
    }

    [Fact]
    public void MergeInvertsAnInput()
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.Merge, f =>
        {
            f.Params["mode"] = 3;
            f.Params["invertB"] = 1;
        }));
        sim.SetAxis("a", 0.1);
        sim.SetAxis("b", 0.2);
        sim.Step(0);
        Assert.Equal(0.8, sim.AxisValue("out"), 3);
    }

    [Theory]
    [InlineData(0.0, 1.0, 0.0)]
    [InlineData(0.25, 0.5, 0.0)]
    [InlineData(0.5, 0.0, 0.0)]
    [InlineData(0.75, 0.0, 0.5)]
    [InlineData(1.0, 0.0, 1.0)]
    public void SplitMath(double input, double low, double high)
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.Split));
        sim.SetAxis("in", input);
        sim.Step(0);
        Assert.Equal(low, sim.AxisValue("low"), 2);
        Assert.Equal(high, sim.AxisValue("high"), 2);
    }

    [Fact]
    public void CurveUsesJoyProsAxisTuning()
    {
        VfSimulator plain = new(Make(VirtualFunctionCatalog.Curve, f => f.Tuning = "inv=0;sl=0;dz=0.1;sx=1;sy=1;cv=0"));
        plain.SetAxis("in", 0.54); // x = 0.08 inside the dead zone
        plain.Step(0);
        Assert.Equal(0.5, plain.AxisValue("out"), 2);
        plain.SetAxis("in", 1);
        plain.Step(5);
        Assert.Equal(1.0, plain.AxisValue("out"), 2);
        VfSimulator inverted = new(Make(VirtualFunctionCatalog.Curve, f => f.Tuning = "inv=1;sl=0;dz=0;sx=1;sy=1;cv=0"));
        inverted.SetAxis("in", 1);
        inverted.Step(0);
        Assert.Equal(0.0, inverted.AxisValue("out"), 2);
        VfSimulator curved = new(Make(VirtualFunctionCatalog.Curve, f => f.Tuning = "inv=0;sl=0;dz=0;sx=1;sy=1;cv=0.5"));
        curved.SetAxis("in", 0.75); // half deflection: a positive curvature gives less
        curved.Step(0);
        Assert.InRange(curved.AxisValue("out"), 0.5, 0.7);
    }

    [Fact]
    public void AxesToHat()
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.AxesHat));
        sim.SetAxis("x", 0.5);
        sim.SetAxis("y", 0.5);
        sim.Step(0);
        Assert.Equal(-1, sim.HatValue("hat"));
        sim.SetAxis("x", 1);
        sim.Step(5);
        Assert.Equal(9000, sim.HatValue("hat"));
        sim.SetAxis("y", 0); // up and right: a diagonal
        sim.Step(10);
        Assert.Equal(4500, sim.HatValue("hat"));
        VfSimulator four = new(Make(VirtualFunctionCatalog.AxesHat, f => f.Params["ways"] = 4));
        four.SetAxis("x", 1);
        four.SetAxis("y", 0.35);
        four.Step(0);
        Assert.Equal(9000, four.HatValue("hat"));
    }

    [Fact]
    public void ButtonsToAxisMovesAtItsRate()
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.ButtonsAxis, f =>
        {
            f.Params["rate"] = 0.5;
            f.Params["start"] = 0.25;
        }));
        sim.Step(0);
        Assert.Equal(0.25, sim.AxisValue("out"), 3);
        sim.SetButton("up", true);
        Hold(sim, 0, 1000);
        Assert.Equal(0.75, sim.AxisValue("out"), 2);
        sim.SetButton("up", false);
        Hold(sim, 1001, 2000);
        Assert.Equal(0.75, sim.AxisValue("out"), 2); // stays (trim)
        sim.SetButton("down", true);
        Hold(sim, 2001, 2400);
        Assert.Equal(0.55, sim.AxisValue("out"), 1);
        sim.SetButton("down", false);
        sim.SetButton("centre", true);
        sim.Step(2410);
        Assert.Equal(0.25, sim.AxisValue("out"), 3);

        VfSimulator spring = new(Make(VirtualFunctionCatalog.ButtonsAxis, f =>
        {
            f.Params["rate"] = 1;
            f.Params["returnRate"] = 1;
        }));
        spring.SetButton("up", true);
        Hold(spring, 0, 300);
        Assert.Equal(0.8, spring.AxisValue("out"), 1);
        spring.SetButton("up", false);
        Hold(spring, 301, 700);
        Assert.Equal(0.5, spring.AxisValue("out"), 2);
    }

    [Fact]
    public void EncoderQueuesOnePulsePerStep()
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.Encoder, f =>
        {
            f.Params["step"] = 0.05;
            f.Params["pulseMs"] = 60;
            f.Params["gapMs"] = 60;
        }));
        sim.SetAxis("in", 0.5);
        sim.Step(0);
        sim.SetAxis("in", 0.751); // five steps up at once
        List<double> ups = Run(sim, 5, 1000, "up");
        Assert.Equal(5, ups.Count);
        for (int i = 1; i < ups.Count; i++)
            Assert.InRange(ups[i] - ups[i - 1], 119, 130);
        sim.SetAxis("in", 0.6);
        Assert.Equal(3, Run(sim, 1005, 2000, "down").Count);
    }

    [Fact]
    public void TurboRepeatsWhileHeld()
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.Turbo, f =>
        {
            f.Params["onMs"] = 50;
            f.Params["offMs"] = 50;
        }));
        sim.SetButton("in", true);
        List<double> presses = Run(sim, 0, 495, "out");
        Assert.Equal(5, presses.Count);
        sim.SetButton("in", false);
        Assert.Empty(Run(sim, 500, 1000, "out"));
        VfSimulator toggle = new(Make(VirtualFunctionCatalog.Turbo, f => f.Params["toggle"] = 1));
        toggle.SetButton("in", true);
        toggle.Step(0);
        toggle.SetButton("in", false);
        Assert.Equal(5, Run(toggle, 5, 500, "out").Count + 1);
    }

    [Fact]
    public void MacroPlaysItsTimeline()
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.Macro, f =>
        {
            f.Steps = [new VfStep { Output = "out1", Action = "tap", DelayMs = 0, HoldMs = 80 }, new VfStep { Output = "out2", Action = "tap", DelayMs = 500, HoldMs = 80 }];
        }));
        sim.Step(0);
        sim.SetButton("trigger", true);
        sim.Step(100);
        Assert.True(sim.IsOn("out1"));
        sim.SetButton("trigger", false);
        Hold(sim, 101, 175);
        Assert.True(sim.IsOn("out1"));
        Hold(sim, 176, 185);
        Assert.False(sim.IsOn("out1"));
        Hold(sim, 186, 675);
        Assert.False(sim.IsOn("out2"));
        Hold(sim, 676, 690);
        Assert.True(sim.IsOn("out2"));
        Hold(sim, 691, 1000);
        Assert.False(sim.IsOn("out2"));
        Assert.Equal(-1, sim.Compiled!.State);
    }

    [Fact]
    public void MacroPressShorterThanAPassIsStillSeen()
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.Macro, f => f.Steps = [new VfStep { Output = "out1", Action = "tap", DelayMs = 0, HoldMs = 1 }]));
        sim.SetButton("trigger", true);
        sim.Step(0);
        Assert.True(sim.IsOn("out1"));
        sim.Step(1000 / Rate);
        Assert.False(sim.IsOn("out1"));
    }

    [Fact]
    public void MacroKeysOnlyWithKeyboardOutputOn()
    {
        VirtualFunction off = Make(VirtualFunctionCatalog.Macro, f => f.Steps = [new VfStep { Output = "KEY_A", Action = "tap", HoldMs = 50 }]);
        Assert.Contains(VirtualFunctionCatalog.Validate(off), p => p.Contains("keyboard key", StringComparison.Ordinal));
        VfSimulator noKeys = new(off);
        noKeys.SetButton("trigger", true);
        Hold(noKeys, 0, 200);
        Assert.Empty(noKeys.KeyLog);

        VfSimulator keys = new(Make(VirtualFunctionCatalog.Macro, f =>
        {
            f.Keyboard = true;
            f.Steps = [new VfStep { Output = "KEY_A", Action = "tap", HoldMs = 50 }];
        }));
        keys.SetButton("trigger", true);
        Hold(keys, 0, 200);
        Assert.Equal(2, keys.KeyLog.Count);
        Assert.Contains("key 0x1E down", keys.KeyLog[0]);
        Assert.Contains("key 0x1E up", keys.KeyLog[1]);
    }

    [Fact]
    public void HatToButtonsFourAndEightWays()
    {
        VfSimulator four = new(Make(VirtualFunctionCatalog.HatButtons));
        four.SetHat("hat", 4500);
        four.Step(0);
        Assert.True(four.IsOn("up"));
        Assert.True(four.IsOn("right"));
        Assert.False(four.IsOn("down"));
        VfSimulator eight = new(Make(VirtualFunctionCatalog.HatButtons, f =>
        {
            f.Params["ways"] = 8;
            f.OutputOf("up")!.Target = "JOY_BTN1";
            f.OutputOf("right")!.Target = "JOY_BTN2";
            f.Outputs.Add(new VfOutput { Role = "upright", Target = "JOY_BTN5" });
        }));
        eight.SetHat("hat", 4500);
        eight.Step(0);
        Assert.True(eight.IsOn("upright"));
        Assert.False(eight.IsOn("up"));
        eight.SetHat("hat", -1);
        eight.Step(5);
        Assert.False(eight.IsOn("upright"));
    }

    [Fact]
    public void ButtonsToHatSettlesOnDiagonals()
    {
        VfSimulator sim = new(Make(VirtualFunctionCatalog.ButtonsHat, f => f.Params["settleMs"] = 30));
        sim.SetButton("up", true);
        sim.SetButton("right", true);
        sim.Step(0);
        Assert.Equal(4500, sim.HatValue("hat"));
        sim.SetButton("up", false);
        sim.Step(10);
        Assert.Equal(4500, sim.HatValue("hat")); // settling
        sim.Step(45);
        Assert.Equal(9000, sim.HatValue("hat"));
        sim.SetButton("right", false);
        sim.Step(50);
        Assert.Equal(-1, sim.HatValue("hat"));
    }

    [Fact]
    public void EveryTypeHasADefaultStreamThatProducesOutput()
    {
        foreach (VfTypeInfo type in VirtualFunctionCatalog.Types)
        {
            VirtualFunction function = Make(type.Id);
            string stream = VfSimulator.DefaultStream(function, out double duration);
            Assert.False(string.IsNullOrEmpty(stream), type.Id);
            if (type.Id == VirtualFunctionCatalog.Presses)
            {
                function.OutputOf("short")!.Target = "JOY_BTN1";
                function.OutputOf("long")!.Target = "JOY_BTN2";
            }
            VfSimulator sim = new(function);
            Assert.True(sim.Problems.Count == 0, type.Id + ": " + string.Join(" ", sim.Problems));
            List<VfEvent> events = sim.Run(VfSimulator.ParseStream(stream), Rate, duration);
            Assert.True(events.Count > 0, type.Id + " gave no output for its default stream");
            Assert.False(string.IsNullOrEmpty(type.Icon));
            Assert.False(string.IsNullOrEmpty(type.Summary));
        }
    }

    // ---- the merge into the forwarder's outputs

    [Fact]
    public void FunctionsOwnTheirOutputsInTheVJoyPosition()
    {
        ForwarderConfig config = new()
        {
            VJoyDevices = [1],
            Sources = [new ForwarderSource("S {00000000-0000-0000-0000-000000000001}", "S", Guid.Empty)],
            Slots = [new ForwarderSlot(0, "button", 0, 1, 0), new ForwarderSlot(0, "button", 1, 1, 1)]
        };
        VirtualFunction toggle = Make(VirtualFunctionCatalog.Toggle, f =>
        {
            f.InputOf("in")!.Device = "S {00000000-0000-0000-0000-000000000001}";
            f.InputOf("in")!.Input = "JOY_BTN3";
            f.OutputOf("out")!.Target = "JOY_BTN40";
            f.OutputOf("out")!.VJoyDevice = 1;
            f.Params["startOn"] = 1;
        });
        // A shift layer that takes physical button 2 (slot vJoy button 2) away while shifted.
        VirtualFunction shift = Make(VirtualFunctionCatalog.Shift, f =>
        {
            f.Id = "t2";
            f.InputOf("shift")!.Device = f.InputOf("in1")!.Device = "S {00000000-0000-0000-0000-000000000001}";
            f.InputOf("shift")!.Input = "JOY_BTN4";
            f.InputOf("in1")!.Input = "JOY_BTN2";
            f.OutputOf("out1")!.Target = "JOY_BTN41";
            f.Outputs.Add(new VfOutput { Role = "base1", Target = "JOY_BTN2", VJoyDevice = 1 });
        });
        List<string> sources = config.Sources.Select(s => s.Device).ToList();
        Assert.Empty(VfResolver.Resolve(toggle, sources, [1]));
        Assert.Empty(VfResolver.Resolve(shift, sources, [1]));
        ForwardingTable table = new(config);
        VfRuntime runtime = new([toggle, shift], 1, 1);
        byte[] state = new byte[ForwardingTable.StateSize];
        VfRuntime.Neutral(state);
        state[ForwardingTable.ButtonOffset + 0] = 0x80; // physical button 1 (plain slot)
        state[ForwardingTable.ButtonOffset + 1] = 0x80; // physical button 2 (on the layer)
        table.Apply(0, state);
        runtime.SetState(0, state);
        runtime.Tick(0);
        table.MergeFunctions(runtime);
        Assert.True(table.Position(0).GetButton(0));
        Assert.True(table.Position(0).GetButton(1)); // unshifted: its own slot
        Assert.True(table.Position(0).GetButton(39)); // the toggle started on
        Assert.False(table.Position(0).GetButton(40));
        state[ForwardingTable.ButtonOffset + 3] = 0x80; // shift
        table.Apply(0, state);
        runtime.SetState(0, state);
        runtime.Tick(5000);
        table.MergeFunctions(runtime);
        Assert.True(table.Position(0).GetButton(0)); // not owned: untouched
        Assert.False(table.Position(0).GetButton(1)); // taken away while shifted
        Assert.True(table.Position(0).GetButton(40));
    }

    [Fact]
    public void FunctionsWriteTheXboxPad()
    {
        ForwarderConfig config = new()
        {
            Sources = [new ForwarderSource("S {00000000-0000-0000-0000-000000000001}", "S", Guid.Empty)],
            XboxGame = "HLLVietnam",
            Xbox = [new XboxSlotConfig(0, "button", 0, "XB_A")]
        };
        VirtualFunction turbo = Make(VirtualFunctionCatalog.Toggle, f =>
        {
            f.InputOf("in")!.Device = "S {00000000-0000-0000-0000-000000000001}";
            f.InputOf("in")!.Input = "JOY_BTN2";
            f.OutputOf("out")!.Target = "XB_Y";
            f.Params["startOn"] = 1;
        });
        VirtualFunction trigger = Make(VirtualFunctionCatalog.ButtonsAxis, f =>
        {
            f.Id = "t2";
            f.InputOf("up")!.Device = f.InputOf("down")!.Device = "S {00000000-0000-0000-0000-000000000001}";
            f.InputOf("up")!.Input = "JOY_BTN3";
            f.InputOf("down")!.Input = "JOY_BTN4";
            f.OutputOf("out")!.Target = "XB_RT";
            f.Params["start"] = 1;
        });
        List<string> sources = ["S {00000000-0000-0000-0000-000000000001}"];
        VfResolver.Resolve(turbo, sources, []);
        VfResolver.Resolve(trigger, sources, []);
        Assert.True(new ForwarderConfig { Functions = [turbo] }.UsesXbox);
        XboxTable xbox = new(config);
        VfRuntime runtime = new([turbo, trigger], 1, 0);
        byte[] state = new byte[ForwardingTable.StateSize];
        VfRuntime.Neutral(state);
        state[ForwardingTable.ButtonOffset] = 0x80;
        xbox.Apply(0, state);
        runtime.SetState(0, state);
        runtime.Tick(0);
        XboxReport report = default;
        xbox.MergeFunctions(runtime, ref report);
        Assert.Equal(XboxReport.A | XboxReport.Y, report.Buttons);
        Assert.Equal(255, report.RightTrigger);
    }

    [Fact]
    public void ForwarderConfigReadsTheEnginesFunctions()
    {
        // As the engine writes it (JavaScriptSerializer: PascalCase).
        const string json = """
        {"version":1,"vjoyDevices":[1],"sources":[{"device":"S {00000000-0000-0000-0000-000000000001}","name":"S","instanceGuid":"00000000-0000-0000-0000-000000000001"}],"slots":[],
         "functions":[{"Id":"f1","Name":"Detents","Type":"bands","Enabled":true,"Keyboard":false,
           "Inputs":[{"Role":"in","Device":"S {00000000-0000-0000-0000-000000000001}","Input":"JOY_Z","Source":0,"Kind":"axis","Index":2,"Direction":-1}],
           "Outputs":[{"Role":"band1","Target":"JOY_BTN2","VJoyDevice":1,"Mode":"hold","Kind":"button","DeviceIndex":0,"Index":1,"Mask":0},
                      {"Role":"band2","Target":"JOY_BTN3","VJoyDevice":1,"Mode":"enter","Kind":"button","DeviceIndex":0,"Index":2,"Mask":0}],
           "Params":{"Hysteresis":0.01,"pulseMs":80},"Edges":[0.5],"Steps":[],"Tuning":""}]}
        """;
        ForwarderConfig config = ForwarderConfig.Parse(json);
        VirtualFunction function = Assert.Single(config.Functions);
        Assert.Equal("bands", function.Type);
        Assert.Equal(0.01, function.Param("hysteresis", 0)); // keys are case-insensitive after Normalize
        Assert.Equal(0, function.Inputs[0].Source);
        Assert.False(config.UsesXbox);
        VfRuntime runtime = new(config.Functions, 1, 1);
        Assert.Single(runtime.Functions);
        Assert.Empty(runtime.Problems);
    }

    // ---- performance: nothing allocated per pass, the cost of 50 functions per pass

    [Fact]
    public void TickAllocatesNothing()
    {
        List<VirtualFunction> functions = VfBenchmark.Mix(50, 4);
        VfRuntime runtime = new(functions, 4, 1);
        byte[] state = new byte[ForwardingTable.StateSize];
        for (int pass = 0; pass < 200; pass++)
            Pass(runtime, state, pass);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int pass = 200; pass < 10_200; pass++)
            Pass(runtime, state, pass);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(50, runtime.Functions.Length);
        Assert.All(runtime.Functions, f => Assert.True(f.Passes > 0));
    }

    private static void Pass(VfRuntime runtime, byte[] state, long pass)
    {
        for (int s = 0; s < runtime.SourceCount; s++)
        {
            VfBenchmark.Drive(state, s, pass, 0, 32767);
            runtime.SetState(s, state);
        }
        runtime.Tick((long)(pass * 1_000_000 / Rate));
    }

    // The forwarder's pass without devices: 4 changing sources through the table, 50 functions, merge, compare. Prints
    // the cost per pass; a pass at 240 Hz has 4166 us, so even a slow CI machine stays far below.
    [Fact]
    public void LoopBenchmarkWithFiftyFunctions()
    {
        ForwarderConfig config = new()
        {
            VJoyDevices = [1],
            Sources = Enumerable.Range(0, 4).Select(i => new ForwarderSource("BENCH" + i, "BENCH", Guid.Empty)).ToList(),
            Slots = Enumerable.Range(0, 8).Select(a => new ForwarderSlot(0, "axis", a, 1, a)).Concat(Enumerable.Range(0, 32).Select(b => new ForwarderSlot(b / 8, "button", b % 8, 1, b))).ToList(),
            Functions = VfBenchmark.Mix(50, 4)
        };
        ForwardingTable table = new(config);
        VfRuntime runtime = new(config.Functions, 4, 1);
        byte[][] states = Enumerable.Range(0, 4).Select(_ => new byte[ForwardingTable.StateSize]).ToArray();
        VJoyPosition sent = default;
        int passes = (int)(Rate * 60); // one minute of passes at 240 Hz
        long writes = 0;
        for (int warm = 0; warm < 500; warm++)
            OnePass(warm);
        Stopwatch watch = new();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        watch.Start();
        for (int pass = 500; pass < 500 + passes; pass++)
            writes += OnePass(pass);
        watch.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        double perPassUs = watch.Elapsed.TotalMilliseconds * 1000 / passes;
        output.WriteLine($"50 functions, 4 sources: {perPassUs:0.00} us per pass ({passes} passes), {writes} vJoy writes, {allocated} bytes allocated; " +
                         $"at 240 Hz that is {perPassUs * 240 / 10_000:0.000} % of one core, at 144 Hz {perPassUs * 144 / 10_000:0.000} %.");
        Assert.Equal(0, allocated);
        Assert.True(perPassUs < 500, "a pass took " + perPassUs + " us");

        int OnePass(long pass)
        {
            for (int s = 0; s < 4; s++)
            {
                VfBenchmark.Drive(states[s], s, pass, 0, 32767);
                table.Apply(s, states[s]);
                runtime.SetState(s, states[s]);
            }
            runtime.Tick((long)(pass * 1_000_000 / Rate));
            table.MergeFunctions(runtime);
            ref VJoyPosition position = ref table.Position(0);
            if (System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref position, 1))
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref sent, 1))))
                return 0;
            sent = position;
            return 1;
        }
    }
}
