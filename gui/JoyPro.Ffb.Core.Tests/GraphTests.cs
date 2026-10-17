using JoyPro.Ffb.Core.Graph;
using JoyPro.Ffb.Core.Output;
using JoyPro.Ffb.Core.Telemetry;

namespace JoyPro.Ffb.Core.Tests;

public class GraphTests
{
    [Fact]
    public void Connect_RejectsCyclesAtEditTime()
    {
        var graph = new FfbGraph();
        var a = graph.Add(new AddNode());
        var b = graph.Add(new LowPassNode());
        var c = graph.Add(new MultiplyNode());

        Assert.Equal(ConnectResult.Ok, graph.TryConnect(a, "out", b, "in"));
        Assert.Equal(ConnectResult.Ok, graph.TryConnect(b, "out", c, "a"));
        Assert.Equal(ConnectResult.Cycle, graph.TryConnect(c, "out", a, "a"));   // a -> b -> c -> a
        Assert.Equal(ConnectResult.Cycle, graph.TryConnect(a, "out", a, "b"));   // self loop
        Assert.Equal(2, graph.Connections.Count);
        Assert.Throws<InvalidOperationException>(() => graph.Connect(c, "out", a, "b"));
    }

    [Fact]
    public void Connect_RewiringAnInputReplacesTheOldWire()
    {
        var graph = new FfbGraph();
        var a = graph.Add(new ConstantNode());
        var b = graph.Add(new AbsNode());
        var c = graph.Add(new AbsNode());
        graph.Connect(a, "value", b, "in");
        graph.Connect(b, "out", c, "in");
        Assert.Equal(ConnectResult.Cycle, graph.TryConnect(c, "out", b, "in"));

        // Feeding a from somewhere else is fine and an input holds one wire only.
        var d = graph.Add(new ConstantNode());
        Assert.Equal(ConnectResult.Ok, graph.TryConnect(d, "value", b, "in"));
        Assert.Single(graph.Connections, w => w.ToNode == b.Id && w.ToPort == "in");
        Assert.Equal(d.Id, graph.Connections.Single(w => w.ToNode == b.Id).FromNode);
    }

    [Fact]
    public void Connect_ChecksPortsAndTypes()
    {
        var graph = new FfbGraph();
        var constant = graph.Add(new ConstantNode());
        var envelope = graph.Add(new EnvelopeNode());
        var threshold = graph.Add(new ThresholdNode());
        var spring = graph.Add(new SpringNode());
        var split = graph.Add(new SplitVec2Node());

        Assert.Equal(ConnectResult.UnknownPort, graph.TryConnect(constant, "nope", spring, "in"));
        Assert.Equal(ConnectResult.UnknownNode, graph.TryConnect("missing", "value", spring.Id, "in"));
        Assert.Equal(ConnectResult.TypeMismatch, graph.TryConnect(constant, "value", envelope, "trigger")); // scalar -> event
        Assert.Equal(ConnectResult.TypeMismatch, graph.TryConnect(constant, "value", split, "vec"));        // scalar -> vec2
        Assert.Equal(ConnectResult.Ok, graph.TryConnect(threshold, "rising", envelope, "trigger"));         // event -> event
        Assert.Equal(ConnectResult.Ok, graph.TryConnect(threshold, "rising", spring, "in"));                // event count -> scalar
    }

    [Fact]
    public void TopologicalOrder_PutsSourcesFirst()
    {
        var graph = new FfbGraph();
        var sink = graph.Add(new ConstantForceNode());
        var clamp = graph.Add(new ClampNode());
        var source = graph.Add(new ConstantNode { Value = 2 });
        graph.Connect(clamp, "out", sink, "in");
        graph.Connect(source, "value", clamp, "in");

        var order = graph.TopologicalOrder(out var broken);
        Assert.Empty(broken);
        Assert.Equal([source.Id, clamp.Id, sink.Id], order.Select(n => n.Id));
    }

    [Fact]
    public void Evaluator_BreaksLoopsSafelyAtRuntime()
    {
        // A loop that only a hand-edited file could contain.
        var graph = new FfbGraph();
        var a = graph.Add(new AddNode());
        var b = graph.Add(new ScaleOffsetNode { Scale = 2 });
        var force = graph.Add(new ConstantForceNode { Axis = FfbAxis.X });
        graph.Connect(a, "out", b, "in");
        graph.Connect(b, "out", force, "in");
        graph.AddConnectionUnchecked(new FfbConnection(b.Id, "out", a.Id, "a"));
        a.InputDefaults["b"] = 0.1;

        var evaluator = new FfbGraphEvaluator(graph);
        Assert.Single(evaluator.BrokenConnections);
        Assert.Equal(a.Id, evaluator.BrokenConnections[0].ToNode);

        // The cut input reads its default (0), so the value does not run away tick after tick.
        for (var i = 0; i < 100; i++)
        {
            var commands = evaluator.Evaluate(null, i * 0.01);
            Assert.Equal(0.2, commands.Single(c => c.Axis == FfbAxis.X).Magnitude, 9);
        }
    }

    [Fact]
    public void Evaluator_CarriesSignedValuesAndUsesInputDefaults()
    {
        var graph = new FfbGraph();
        var source = graph.Add(new ConstantNode { Value = -0.6 });
        var multiply = graph.Add(new MultiplyNode());
        multiply.InputDefaults["b"] = 0.5;
        var force = graph.Add(new ConstantForceNode { Axis = FfbAxis.Y, Bipolar = true });
        graph.Connect(source, "value", multiply, "a");
        graph.Connect(multiply, "out", force, "in");

        var commands = new FfbGraphEvaluator(graph).Evaluate(null, 0);
        Assert.Equal(-0.3, commands.Single(c => c.Axis == FfbAxis.Y).Magnitude, 9);
        Assert.DoesNotContain(commands, c => c.Axis == FfbAxis.X);
    }

    [Fact]
    public void TelemetryNode_MissingValueIsSilentEvenWhenInverted()
    {
        var node = new TelemetryNode
        {
            Key = "grip",
            Mode = TelemetryNormalisation.Unsigned,
            Minimum = 0,
            Maximum = 1,
            Invert = true
        };
        var outputs = new Signal[2];
        node.Evaluate(new FfbEvalContext(null, 0, 0), [], outputs);
        Assert.Equal(0, outputs[0].X);
        Assert.Equal(0, outputs[1].X);

        var clock = new ManualClock();
        var source = new SyntheticTelemetrySource(clock).Define("grip", _ => 0.25, 0, 1);
        source.Step(0);
        node.Evaluate(new FfbEvalContext(source, 0, 0), [], outputs);
        Assert.Equal(0.75, outputs[0].X, 9);
        Assert.Equal(1, outputs[1].X);
    }

    [Theory]
    [InlineData(TelemetryNormalisation.Raw, false, 150, 150)]
    [InlineData(TelemetryNormalisation.Raw, true, 150, -150)]
    [InlineData(TelemetryNormalisation.Unsigned, false, 150, 0.75)]
    [InlineData(TelemetryNormalisation.Unsigned, false, 999, 1)]
    [InlineData(TelemetryNormalisation.Signed, false, 100, 0)]
    [InlineData(TelemetryNormalisation.Signed, false, 0, -1)]
    [InlineData(TelemetryNormalisation.Signed, true, 150, -0.5)]
    public void TelemetryNode_Normalises(TelemetryNormalisation mode, bool invert, double raw, double expected)
    {
        var node = new TelemetryNode { Minimum = 0, Maximum = 200, Mode = mode, Invert = invert };
        Assert.Equal(expected, node.Normalise(raw), 9);
    }

    [Fact]
    public void Evaluator_SanitisesNonFiniteValues()
    {
        var graph = new FfbGraph();
        var source = graph.Add(new TelemetryNode { Key = "bad" });
        var force = graph.Add(new ConstantForceNode { Axis = FfbAxis.X, SilenceAtZero = false, MinStrength = 0.1 });
        graph.Connect(source, "value", force, "in");
        var clock = new ManualClock();
        var telemetry = new SyntheticTelemetrySource(clock).Define("bad", _ => double.NaN);
        telemetry.Step(0);

        var commands = new FfbGraphEvaluator(graph).Evaluate(telemetry, 0);
        Assert.All(commands, c => Assert.True(double.IsFinite(c.Magnitude)));
        Assert.Equal(0.1, commands.Single(c => c.Axis == FfbAxis.X).Magnitude, 9);   // NaN read as 0: the 0 % value
    }

    [Theory]
    [InlineData(CombineMode.Maximum, 0.6)]
    [InlineData(CombineMode.Minimum, 0.2)]
    [InlineData(CombineMode.Average, 0.4)]
    [InlineData(CombineMode.SumClamped, 0.8)]
    public void GroupNode_CombinesAnyNumberOfWires(CombineMode mode, double expected)
    {
        var graph = new FfbGraph();
        var a = graph.Add(new ConstantNode { Value = 0.2 });
        var b = graph.Add(new ConstantNode { Value = 0.6 });
        var group = graph.Add(new GroupNode { Mode = mode });
        var force = graph.Add(new ConstantForceNode { Axis = FfbAxis.X });
        graph.Connect(a, "value", group, "in");
        graph.Connect(b, "value", group, "in");
        graph.Connect(a, "value", group, "in");                     // the same wire twice stays one wire
        graph.Connect(group, "out", force, "in");
        Assert.Equal(2, graph.Connections.Count(c => c.ToNode == group.Id));

        var evaluator = new FfbGraphEvaluator(graph);
        evaluator.Evaluate(null, 0);
        Assert.Equal(expected, evaluator.GetOutput(group.Id, "out").X, 9);

        var sum = new FfbGraph();
        var x = sum.Add(new ConstantNode { Value = 0.7 });
        var y = sum.Add(new ConstantNode { Value = 0.7 });
        var clamped = sum.Add(new GroupNode { Mode = CombineMode.SumClamped });
        sum.Connect(x, "value", clamped, "in");
        sum.Connect(y, "value", clamped, "in");
        var sumEvaluator = new FfbGraphEvaluator(sum);
        sumEvaluator.Evaluate(null, 0);
        Assert.Equal(1, sumEvaluator.GetOutput(clamped.Id, "out").X, 9);
    }

    [Fact]
    public void OutputNode_TakesSeveralWires_WithItsInputMode()
    {
        var graph = new FfbGraph();
        var a = graph.Add(new ConstantNode { Value = 0.2 });
        var b = graph.Add(new ConstantNode { Value = 0.6 });
        var periodic = graph.Add(new PeriodicNode
        {
            Axes = FfbAxisSelection.X, InputMode = CombineMode.Minimum,
            MinStrength = 0, MaxStrength = 0.5, Modulation = FfbModulation.StrengthAndFrequency, MinFrequencyHz = 10, MaxFrequencyHz = 20
        });
        graph.Connect(a, "value", periodic, "in");
        graph.Connect(b, "value", periodic, "in");

        var command = new FfbGraphEvaluator(graph).Evaluate(null, 0).Single();
        Assert.Equal(0.1, command.Magnitude, 9);      // min(0.2, 0.6) = 0.2 -> 0..0.5
        Assert.Equal(12, command.FrequencyHz, 9);     // 10..20 Hz

        // Removing one wire keeps the other.
        Assert.True(graph.Disconnect(graph.Connections.First(c => c.FromNode == a.Id)));
        Assert.Equal(0.3, new FfbGraphEvaluator(graph).Evaluate(null, 0).Single().Magnitude, 9);
    }

    [Fact]
    public void OutputNodes_SilentAtZero_PulsedBursts_AndConstantActive()
    {
        var graph = new FfbGraph();
        var constant = graph.Add(new ConstantNode { Value = 1 });
        var guns = graph.Add(new PeriodicNode
        {
            Axes = FfbAxisSelection.Y, Waveform = FfbWaveform.Square, MinStrength = 0.4, MaxStrength = 0.4,
            MinFrequencyHz = 25, Pulsed = true, PulseRateHz = 10, DutyCycle = 0.5, Effect = "guns", Slot = 3
        });
        graph.Connect(constant, "value", guns, "in");
        var evaluator = new FfbGraphEvaluator(graph);

        var on = evaluator.Evaluate(null, 0.02).Single();     // 20 % into a 100 ms pulse period: on
        var off = evaluator.Evaluate(null, 0.07).Single();    // 70 %: off, the effect stays with strength 0
        Assert.Equal(0.4, on.Magnitude, 9);
        Assert.Equal(0, off.Magnitude);
        Assert.Equal("guns", on.Tag);
        Assert.Equal(3, on.Slot);
        Assert.Equal(FfbAxis.Y, on.Axis);

        constant.Active = false;
        Assert.Empty(evaluator.Evaluate(null, 0.12));          // input 0: no effect at all

        guns.SilenceAtZero = false;
        Assert.Equal(0.4, evaluator.Evaluate(null, 0.12).Single().Magnitude, 9);
    }

    [Fact]
    public void Mixer_StrongestWinsPerSlot_SlotsAddUp_ThenTheCap()
    {
        FfbEffectCommand Periodic(double magnitude, int slot) =>
            new("stick", FfbAxis.X, FfbEffectType.Periodic, Magnitude: magnitude, FrequencyHz: 10, Slot: slot);

        var mixed = FfbMixer.Mix([Periodic(0.2, 1), Periodic(0.5, 1), Periodic(0.1, 2)], _ => new FfbDeviceSettings(1, 1), 0.1);
        Assert.Equal(2, mixed.Count);
        Assert.Equal(0.5, mixed.Single(c => c.Slot == 1).Magnitude, 9);
        Assert.Equal(0.1, mixed.Single(c => c.Slot == 2).Magnitude, 9);

        var capped = FfbMixer.Mix([Periodic(0.5, 1), Periodic(0.5, 2)], _ => new FfbDeviceSettings(1, 0.6), 0.1);
        Assert.Equal(0.6, capped.Sum(c => c.PeakForce), 9);
    }

    [Fact]
    public void FlightPreset_IsValid_AndEveryEffectNodeIsNamed()
    {
        var graph = FfbPresets.Flight();
        var evaluator = new FfbGraphEvaluator(graph);
        Assert.Empty(evaluator.BrokenConnections);
        Assert.Equal(graph.Connections.Count, graph.ValidConnections().Count());
        var sinks = graph.Nodes.OfType<EffectSinkNode>().ToList();
        Assert.All(sinks, s => Assert.Contains(s.Effect, FfbEffectNames.All.Select(e => e.Name)));
        Assert.All(FfbEffectNames.All, e => Assert.Contains(sinks, s => s.Effect == e.Name));

        // Round trip through JSON keeps it identical.
        var json = FfbGraphPersistence.ToJson(graph);
        Assert.Equal(json, FfbGraphPersistence.ToJson(FfbGraphPersistence.FromJson(json).Graph));
    }
}
