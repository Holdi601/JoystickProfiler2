using JoyPro.Ffb.Core.Graph;
using JoyPro.Ffb.Core.Host;
using JoyPro.Ffb.Core.Input;
using JoyPro.Ffb.Core.Output;
using JoyPro.Ffb.Core.Runtime;
using JoyPro.Ffb.Core.Telemetry;

namespace JoyPro.Ffb.Core.Tests;

/// <summary>Invert, device buttons, stick position, trim, centring spring and the direction test (all with fakes).</summary>
public class TrimInvertTests
{
    private const string Stick = "mock";
    private const string Throttle = "Throttle {11111111-2222-3333-4444-555555555555}";

    // ------------------------------------------------------------------ input references

    [Theory]
    [InlineData("JOY_BTN1", FfbInputKind.Button, 0)]
    [InlineData("joy_btn128", FfbInputKind.Button, 127)]
    [InlineData("JOY_BTN_POV1_U", FfbInputKind.Hat, 0)]
    [InlineData("JOY_BTN_POV2_DL", FfbInputKind.Hat, 1)]
    [InlineData("", FfbInputKind.None, 0)]
    [InlineData("JOY_BTN0", FfbInputKind.None, 0)]
    [InlineData("JOY_BTN129", FfbInputKind.None, 0)]
    [InlineData("LCtrl", FfbInputKind.None, 0)]
    public void InputRef_ParsesCaptureNames(string text, FfbInputKind kind, int index)
    {
        var parsed = FfbInputRef.Parse(text);
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(index, parsed.Index);
        if (kind != FfbInputKind.None)
            Assert.Equal(text.ToUpperInvariant(), parsed.ToString());
    }

    [Fact]
    public void Hat_StraightDirectionsAlsoMatchTheirDiagonals()
    {
        var up = FfbInputRef.Parse("JOY_BTN_POV1_U");
        Assert.True(up.HatMatches(0));
        Assert.True(up.HatMatches(4500));
        Assert.True(up.HatMatches(31500));
        Assert.False(up.HatMatches(9000));
        Assert.False(up.HatMatches(-1));          // centred
        Assert.False(up.HatMatches(65535 % 36000 + 36000)); // out of range = centred
        var upRight = FfbInputRef.Parse("JOY_BTN_POV1_UR");
        Assert.True(upRight.HatMatches(4500));
        Assert.False(upRight.HatMatches(0));
    }

    [Fact]
    public void DeviceMatch_ByGuidFirstThenByName()
    {
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.True(FfbDeviceMatch.Matches(Throttle, guid, "Other name", "x"));
        Assert.False(FfbDeviceMatch.Matches(Throttle, Guid.NewGuid(), "Throttle", "Throttle"));
        Assert.True(FfbDeviceMatch.Matches("Throttle", Guid.NewGuid(), "throttle", "x"));
        Assert.False(FfbDeviceMatch.Matches("", guid, "", ""));
        Assert.Equal("Throttle", FfbDeviceMatch.NameOf(Throttle));
    }

    // ------------------------------------------------------------------ nodes

    [Fact]
    public void InvertNode_FlipsTheSign_AndPassesThroughWhenOff()
    {
        var graph = new FfbGraph();
        var c = graph.Add(new ConstantNode { Value = 0.4 });
        var invert = graph.Add(new InvertNode());
        var force = graph.Add(new ConstantForceNode { Axis = FfbAxis.X, Bipolar = true });
        graph.Connect(c, "value", invert, "in");
        graph.Connect(invert, "out", force, "in");
        var evaluator = new FfbGraphEvaluator(graph);
        Assert.Equal(-0.4, evaluator.Evaluate(null, 0).Single().Magnitude, 9);

        invert.Enabled = false;
        Assert.Equal(0.4, new FfbGraphEvaluator(graph).Evaluate(null, 0).Single().Magnitude, 9);
    }

    [Fact]
    public void ConstantForce_NegativeInvertsAndSpringInvertCentreFlipsTheTrim()
    {
        var graph = new FfbGraph();
        var c = graph.Add(new ConstantNode { Value = 0.5 });
        var force = graph.Add(new ConstantForceNode { Axis = FfbAxis.Y, Negative = true });
        var spring = graph.Add(new SpringNode { Axes = FfbAxisSelection.Y, InvertCentre = true, MaxStiffness = 0.5 });
        graph.Connect(c, "value", force, "in");
        graph.Connect(c, "value", spring, "in");
        graph.Connect(c, "value", spring, "centre");
        var commands = new FfbGraphEvaluator(graph).Evaluate(null, 0);
        Assert.Equal(-0.5, commands.Single(x => x.Type == FfbEffectType.ConstantForce).Magnitude, 9);
        Assert.Equal(-0.5, commands.Single(x => x.Type == FfbEffectType.Spring).Centre, 9);
    }

    [Fact]
    public void NewNodes_SurviveSaveAndLoad()
    {
        var graph = new FfbGraph();
        graph.Add(new InvertNode { Enabled = false });
        graph.Add(new DeviceButtonNode { Device = Throttle, Input = "JOY_BTN_POV1_U" });
        graph.Add(new StickPositionNode());
        graph.Add(new TrimNode { Step = 0.05, RepeatRate = 4, RepeatDelay = 0.2, Limit = 0.6, MoveRate = 0, InvertY = true });
        graph.Add(new SpringNode { InvertCentre = true });
        var loaded = FfbGraphPersistence.FromJson(FfbGraphPersistence.ToJson(graph)).Graph;
        Assert.False(loaded.Nodes.OfType<InvertNode>().Single().Enabled);
        var button = loaded.Nodes.OfType<DeviceButtonNode>().Single();
        Assert.Equal(Throttle, button.Device);
        Assert.Equal(FfbInputKind.Hat, button.Parsed.Kind);
        var trim = loaded.Nodes.OfType<TrimNode>().Single();
        Assert.Equal((0.05, 4.0, 0.2, 0.6, 0.0, true), (trim.Step, trim.RepeatRate, trim.RepeatDelay, trim.Limit, trim.MoveRate, trim.InvertY));
        Assert.True(loaded.Nodes.OfType<SpringNode>().Single().InvertCentre);
        Assert.Single(loaded.Nodes.OfType<StickPositionNode>());
    }

    [Fact]
    public void DeviceButton_HeldAndOnePressedEventPerPress_MissingDeviceReadsReleased()
    {
        var inputs = new FakeFfbInputs();
        var graph = new FfbGraph();
        var button = graph.Add(new DeviceButtonNode { Device = Throttle, Input = "JOY_BTN3" });
        var missing = graph.Add(new DeviceButtonNode { Device = "Nope", Input = "JOY_BTN3" });
        var evaluator = new FfbGraphEvaluator(graph);

        evaluator.Evaluate(null, 0, inputs);
        Assert.Equal(0, evaluator.GetOutput(button.Id, "held").X);
        inputs.SetButton(Throttle, "JOY_BTN3", true);
        evaluator.Evaluate(null, 0.01, inputs);
        Assert.Equal(1, evaluator.GetOutput(button.Id, "held").X);
        Assert.Equal(1, evaluator.GetOutput(button.Id, "pressed").EventCount);
        evaluator.Evaluate(null, 0.02, inputs);
        Assert.Equal(1, evaluator.GetOutput(button.Id, "held").X);
        Assert.Equal(0, evaluator.GetOutput(button.Id, "pressed").EventCount);
        Assert.Equal(0, evaluator.GetOutput(missing.Id, "held").X);

        // Without an input source at all (editor preview): released.
        evaluator.Evaluate(null, 0.03);
        Assert.Equal(0, evaluator.GetOutput(button.Id, "held").X);
    }

    [Fact]
    public void StickPosition_ReadsTheForceFeedbackStick()
    {
        var inputs = new FakeFfbInputs();
        var graph = new FfbGraph();
        var stick = graph.Add(new StickPositionNode());
        var evaluator = new FfbGraphEvaluator(graph);
        evaluator.Evaluate(null, 0, inputs);
        Assert.Equal(0, evaluator.GetOutput(stick.Id, "valid").X);
        inputs.SetStick("", FfbAxis.X, 0.25);
        inputs.SetStick("", FfbAxis.Y, -0.5);
        evaluator.Evaluate(null, 0.01, inputs);
        Assert.Equal((0.25, -0.5, 1.0), (evaluator.GetOutput(stick.Id, "x").X, evaluator.GetOutput(stick.Id, "y").X, evaluator.GetOutput(stick.Id, "valid").X));
    }

    // ------------------------------------------------------------------ trim

    private sealed class TrimRig
    {
        public readonly FakeFfbInputs Inputs = new();
        public readonly FfbGraph Graph = new();
        public readonly TrimNode Trim;
        public readonly FfbGraphEvaluator Evaluator;
        public double Time;

        public TrimRig(Action<TrimNode>? configure = null)
        {
            Trim = Graph.Add(new TrimNode { MoveRate = 0 });
            configure?.Invoke(Trim);
            foreach (var port in new[] { "here", "up", "down", "left", "right", "reset" })
            {
                var button = Graph.Add(new DeviceButtonNode { Device = Throttle, Input = "JOY_BTN" + (Array.IndexOf(new[] { "here", "up", "down", "left", "right", "reset" }, port) + 1) });
                Graph.Connect(button, "held", Trim, port);
            }

            var spring = Graph.Add(new SpringNode { Axes = FfbAxisSelection.Y });
            var one = Graph.Add(new ConstantNode { Value = 1 });
            Graph.Connect(one, "value", spring, "in");
            Graph.Connect(Trim, "y", spring, "centre");
            Evaluator = new FfbGraphEvaluator(Graph);
        }

        public void Press(string port, bool down) =>
            Inputs.SetButton(Throttle, "JOY_BTN" + (Array.IndexOf(new[] { "here", "up", "down", "left", "right", "reset" }, port) + 1), down);

        public List<FfbEffectCommand> Tick(double dt = 0.01)
        {
            Time += dt;
            return Evaluator.Evaluate(null, Time, Inputs);
        }

        public (double X, double Y) Centre => Trim.Centre;
    }

    [Fact]
    public void Trim_HatStepsPerPress_UpIsNoseDown()
    {
        var rig = new TrimRig(t => t.Step = 0.05);
        rig.Tick();
        rig.Press("up", true);
        var commands = rig.Tick();
        Assert.Equal(-0.05, rig.Centre.Y, 9);                       // forward = negative Y
        Assert.Equal(-0.05, commands.Single().Centre, 9);           // into the spring's centre
        rig.Tick();
        rig.Press("up", false);
        rig.Tick();
        rig.Press("right", true);
        rig.Tick();
        rig.Press("right", false);
        rig.Tick();
        Assert.Equal((0.05, -0.05), (Math.Round(rig.Centre.X, 9), Math.Round(rig.Centre.Y, 9)));
        rig.Press("down", true);
        rig.Tick();
        rig.Press("down", false);
        rig.Tick();
        Assert.Equal(0, rig.Centre.Y, 9);
    }

    [Fact]
    public void Trim_RepeatsWhileHeld_AtTheConfiguredRate_AndStopsAtTheLimit()
    {
        var rig = new TrimRig(t =>
        {
            t.Step = 0.01;
            t.RepeatDelay = 0.3;
            t.RepeatRate = 10;
            t.Limit = 0.1;
        });
        rig.Press("down", true);
        rig.Tick();                                  // press: 1 step
        for (var i = 0; i < 29; i++)
            rig.Tick();                              // 0.3 s held: still 1 step
        Assert.Equal(0.01, rig.Centre.Y, 9);
        for (var i = 0; i < 60; i++)
            rig.Tick();                              // 0.6 s more at 10 steps/s: 5 to 6 more
        Assert.InRange(rig.Centre.Y, 0.059, 0.071);
        for (var i = 0; i < 200; i++)
            rig.Tick();
        Assert.Equal(0.1, rig.Centre.Y, 9);          // the limit
    }

    [Fact]
    public void Trim_HereFollowsTheStickWhileHeld_KeepsItAfterwards_ResetCentres()
    {
        var rig = new TrimRig();
        rig.Inputs.SetStick("", FfbAxis.X, 0.2);
        rig.Inputs.SetStick("", FfbAxis.Y, 0.3);
        rig.Press("here", true);
        rig.Tick();
        Assert.Equal((0.2, 0.3), rig.Centre);
        rig.Inputs.SetStick("", FfbAxis.Y, 0.4);
        rig.Tick();
        Assert.Equal(0.4, rig.Centre.Y, 9);          // follows while held (the spring lets go)
        rig.Press("here", false);
        rig.Inputs.SetStick("", FfbAxis.Y, 0);
        rig.Tick();
        Assert.Equal(0.4, rig.Centre.Y, 9);          // stays where the stick was
        rig.Press("reset", true);
        rig.Tick();
        Assert.Equal((0.0, 0.0), rig.Centre);
    }

    [Fact]
    public void Trim_HereWithoutStickPosition_IsIgnored()
    {
        var rig = new TrimRig();
        rig.Press("up", true);
        rig.Tick();
        rig.Press("up", false);
        rig.Press("here", true);
        rig.Tick();                                  // no stick position: keeps the hat trim
        Assert.Equal(-0.02, rig.Centre.Y, 9);
    }

    [Fact]
    public void Trim_SurvivesAResetOfTheGraphState_AndAHeldButtonDoesNotFireAgain()
    {
        var rig = new TrimRig(t => t.Step = 0.1);
        rig.Press("up", true);
        rig.Tick();
        Assert.Equal(-0.1, rig.Centre.Y, 9);
        rig.Evaluator.Reset();                       // telemetry gap / pause
        for (var i = 0; i < 100; i++)
            rig.Tick();                              // still held: no new press, no repeat
        Assert.Equal(-0.1, rig.Centre.Y, 9);
        rig.Press("up", false);
        rig.Tick();
        rig.Press("up", true);
        rig.Tick();
        Assert.Equal(-0.2, rig.Centre.Y, 9);
    }

    [Fact]
    public void Trim_InvertAndMoveRate()
    {
        var rig = new TrimRig(t =>
        {
            t.Step = 0.5;
            t.InvertY = true;
            t.MoveRate = 1;
        });
        rig.Tick();                                  // the first tick has no time step
        rig.Press("up", true);
        rig.Tick(0.1);
        Assert.Equal(0.1, rig.Centre.Y, 9);          // inverted: up goes back; 1 travel/s
        rig.Press("up", false);
        for (var i = 0; i < 10; i++)
            rig.Tick(0.1);
        Assert.Equal(0.5, rig.Centre.Y, 9);
    }

    [Fact]
    public void FlightPreset_HasTheStickTrimWiredIntoBothSprings()
    {
        var graph = FfbPresets.Flight();
        var trim = graph.Nodes.OfType<TrimNode>().Single();
        foreach (var (title, port) in FfbPresets.TrimButtons.All)
        {
            var button = graph.Nodes.OfType<DeviceButtonNode>().Single(b => b.Title == title);
            Assert.Contains(graph.Connections, c => c.FromNode == button.Id && c.ToNode == trim.Id && c.ToPort == port);
            Assert.Equal("", button.Input);          // assigned by the user
        }

        var springs = graph.Nodes.OfType<SpringNode>().ToList();
        Assert.Equal(2, springs.Count);
        Assert.All(springs, s => Assert.Contains(graph.Connections, c => c.ToNode == s.Id && c.ToPort == "centre"));

        // Hat up on the stick trims the pitch spring forward.
        var inputs = new FakeFfbInputs();
        var up = graph.Nodes.OfType<DeviceButtonNode>().Single(b => b.Title == FfbPresets.TrimButtons.Up);
        up.Input = "JOY_BTN_POV1_U";
        var evaluator = new FfbGraphEvaluator(graph);
        evaluator.Evaluate(null, 0, inputs);
        inputs.SetButton("", "JOY_BTN_POV1_U", true);
        List<FfbEffectCommand> commands = [];
        for (var t = 0.01; t < 1; t += 0.01)
            commands = evaluator.Evaluate(null, t, inputs);
        var pitch = commands.Single(c => c.Type == FfbEffectType.Spring && c.Axis == FfbAxis.Y);
        Assert.True(pitch.Centre < -0.05, "pitch centre " + pitch.Centre);
        Assert.Equal(0, commands.Single(c => c.Type == FfbEffectType.Spring && c.Axis == FfbAxis.X).Centre, 9);
    }

    // ------------------------------------------------------------------ centring spring

    private static (FfbRuntime Runtime, MockFfbBackend Backend, ManualClock Clock, SyntheticTelemetrySource Source) Rig(FfbGraph graph, FfbCentring centring)
    {
        var clock = new ManualClock();
        var backend = new MockFfbBackend();
        var source = new SyntheticTelemetrySource(clock);
        source.Step(0);
        var runtime = new FfbRuntime(backend, clock, new FfbRuntimeOptions { DefaultDevice = new FfbDeviceSettings(1, 1) }) { Source = source, Centring = centring };
        runtime.LoadGraph(graph);
        runtime.Enabled = true;
        return (runtime, backend, clock, source);
    }

    private static FfbGraph PitchSpringGraph(double centre)
    {
        var graph = new FfbGraph();
        var one = graph.Add(new ConstantNode { Value = 1 });
        var c = graph.Add(new ConstantNode { Value = centre });
        var spring = graph.Add(new SpringNode { Axes = FfbAxisSelection.Y, MaxStiffness = 0.4, Saturation = 0.6 });
        graph.Connect(one, "value", spring, "in");
        graph.Connect(c, "value", spring, "centre");
        return graph;
    }

    [Fact]
    public void Centring_Ensure_AddsAPlainSpringOnAxesWithout()
    {
        var (runtime, backend, _, _) = Rig(PitchSpringGraph(0.2), new FfbCentring(FfbCentringMode.Ensure, 0.3, 0.5));
        using var _r = runtime;
        runtime.Tick();
        var springs = backend.LastCommands.Where(c => c.Type == FfbEffectType.Spring).ToList();
        Assert.Equal(2, springs.Count);
        var x = springs.Single(s => s.Axis == FfbAxis.X);
        Assert.Equal((0.3, 0.5, 0.0, FfbCentring.Tag), (x.Coefficient, x.Saturation, x.Centre, x.Tag));
        Assert.Equal(0.2, springs.Single(s => s.Axis == FfbAxis.Y).Centre, 9);   // the graph's own spring stays
    }

    [Fact]
    public void Centring_Off_SendsNoSprings_GraphOnly_LeavesTheGraphAlone()
    {
        var (off, offBackend, _, _) = Rig(PitchSpringGraph(0), new FfbCentring(FfbCentringMode.Off));
        using (off)
        {
            off.Tick();
            Assert.DoesNotContain(offBackend.LastCommands, c => c.Type == FfbEffectType.Spring);
        }

        var (graphOnly, graphBackend, _, _) = Rig(PitchSpringGraph(0), FfbCentring.GraphOnly);
        using (graphOnly)
        {
            graphOnly.Tick();
            Assert.Single(graphBackend.LastCommands, c => c.Type == FfbEffectType.Spring);
        }
    }

    [Fact]
    public void Centring_Ensure_HoldsTheTrimmedSpringsWhileTheTelemetryPauses_StopAllOnDisable()
    {
        var graph = PitchSpringGraph(-0.3);
        var c = graph.Add(new ConstantNode { Value = 0.5 });
        var force = graph.Add(new ConstantForceNode { Axis = FfbAxis.X });
        graph.Connect(c, "value", force, "in");
        var (runtime, backend, clock, source) = Rig(graph, new FfbCentring(FfbCentringMode.Ensure, 0.3, 0.5));
        using var _r = runtime;
        runtime.Tick();
        Assert.Contains(backend.LastCommands, x => x.Type == FfbEffectType.ConstantForce);

        clock.Now = 1;                               // stale: no telemetry for 1 s
        runtime.Tick();
        Assert.Equal(FfbRuntimeState.Silent, runtime.State);
        Assert.True(runtime.Holding);
        Assert.Equal(0, backend.StopAllCount);       // the device is kept: springs only
        Assert.DoesNotContain(backend.LastCommands, x => x.Type == FfbEffectType.ConstantForce);
        var pitch = backend.LastCommands.Single(x => x.Axis == FfbAxis.Y);
        Assert.Equal(-0.3, pitch.Centre, 9);         // with its trim
        Assert.Equal(FfbEffectType.Spring, backend.LastCommands.Single(x => x.Axis == FfbAxis.X).Type);
        Assert.True(pitch.DurationSeconds >= 3 * runtime.Options.IdleWaitSeconds);

        // Held springs are refreshed slowly, not every tick.
        var applies = backend.ApplyCount;
        clock.Now = 1.1;
        runtime.Tick();
        Assert.Equal(applies, backend.ApplyCount);
        clock.Now = 1.6;
        runtime.Tick();
        Assert.Equal(applies + 1, backend.ApplyCount);

        // Telemetry back: forces again.
        clock.Now = 2;
        source.Step(2);
        runtime.Tick();
        Assert.Equal(FfbRuntimeState.Active, runtime.State);
        Assert.False(runtime.Holding);
        Assert.Contains(backend.LastCommands, x => x.Type == FfbEffectType.ConstantForce);

        runtime.Enabled = false;                     // switching off always lets go
        Assert.Equal(1, backend.StopAllCount);
    }

    [Fact]
    public void Centring_GraphOnly_StaleTelemetryStillStopsAll()
    {
        var (runtime, backend, clock, _) = Rig(PitchSpringGraph(0), FfbCentring.GraphOnly);
        using var _r = runtime;
        runtime.Tick();
        clock.Now = 1;
        runtime.Tick();
        Assert.Equal(1, backend.StopAllCount);
        Assert.False(runtime.Holding);
    }

    [Fact]
    public void Runtime_PollsTheInputsOncePerEvaluation()
    {
        var inputs = new FakeFfbInputs();
        var (runtime, _, _, _) = Rig(PitchSpringGraph(0), FfbCentring.GraphOnly);
        using var _r = runtime;
        runtime.Inputs = inputs;
        runtime.Tick();
        runtime.Tick();
        Assert.Equal(2, inputs.Polls);
    }

    // ------------------------------------------------------------------ config

    [Fact]
    public void Config_DeviceInvertsAndCentring_RoundTrip()
    {
        var config = new TelemetryFfbConfig { InvertY = false, CentringSpring = false, CentringStiffness = 0.45 };
        config.Devices["{AAAAAAAA-0000-0000-0000-000000000001}"] = new DeviceFfbSettings { InvertY = true };
        var loaded = TelemetryFfbConfig.Parse(config.ToJson());
        Assert.False(loaded.CentringSpring);
        Assert.Equal(0.45, loaded.CentringStiffness, 9);
        Assert.Equal((false, true), loaded.InvertFor("{aaaaaaaa-0000-0000-0000-000000000001}"));
        Assert.Equal((false, true), loaded.InvertFor("aaaaaaaa-0000-0000-0000-000000000001"));
        Assert.Equal((false, false), loaded.InvertFor("{BBBBBBBB-0000-0000-0000-000000000001}"));   // global
        Assert.Equal(FfbCentringMode.Off, loaded.Centring().Mode);

        var defaults = TelemetryFfbConfig.Parse("{}");
        Assert.True(defaults.CentringSpring);
        Assert.Equal(FfbCentringMode.Ensure, defaults.Centring().Mode);
        Assert.Equal(TelemetryFfbConfig.DefaultCentringStiffness, defaults.Centring().Stiffness, 9);
    }
}
