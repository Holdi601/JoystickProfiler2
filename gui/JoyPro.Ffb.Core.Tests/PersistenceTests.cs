using System.Text.Json.Nodes;
using JoyPro.Ffb.Core.Graph;
using JoyPro.Ffb.Core.Output;

namespace JoyPro.Ffb.Core.Tests;

public class PersistenceTests
{
    internal static FfbGraph SampleGraph()
    {
        var graph = new FfbGraph { Name = "G spring + gun kick", Enabled = true };
        var g = graph.Add(new TelemetryNode
        {
            Title = "G load",
            Key = "il2.acc.g",
            Minimum = 1,
            Maximum = 6,
            Mode = TelemetryNormalisation.Unsigned,
            X = 10,
            Y = 20
        });
        var smooth = graph.Add(new LowPassNode { TimeConstant = 0.08 });
        var curve = graph.Add(new CurveNode { Symmetric = false });
        curve.Curve.AddNode(new CurvePoint(0.4, 0.2));
        var stiffness = graph.Add(new ScaleOffsetNode { Scale = 0.7, Offset = 0.2 });
        var spring = graph.Add(new SpringNode { Device = "stick-1", Axes = FfbAxisSelection.Y, Saturation = 0.7, Effect = "spring" });
        spring.InputDefaults["centre"] = 0.1;
        var shells = graph.Add(new TelemetryNode { Key = "il2.ev.gun_fire" });
        var counter = graph.Add(new CounterDeltaToEventNode { Direction = CounterDirection.Increase, MaxDelta = 40 });
        var envelope = graph.Add(new EnvelopeNode { Attack = 0.005, Hold = 0.02, Decay = 0.03, Peak = 0.6 });
        var kick = graph.Add(new ConstantForceNode { Device = "stick-1", Negative = true, MaxStrength = 0.4, Slot = 2 });
        var buzz = graph.Add(new PeriodicNode
        {
            Waveform = FfbWaveform.Triangle, Axes = FfbAxisSelection.X, Pulsed = true, PulseRateHz = 12, DutyCycle = 0.3,
            Modulation = FfbModulation.FrequencyOnly, MinFrequencyHz = 5, MaxFrequencyHz = 40, InputMode = CombineMode.Average
        });
        var threshold = graph.Add(new ThresholdNode { On = 0.7, Off = 0.5 });
        var slew = graph.Add(new SlewLimitNode { RiseRate = 3, FallRate = 1 });
        var group = graph.Add(new GroupNode { Mode = CombineMode.SumClamped });
        var test = graph.Add(new ConstantNode { Value = 0.25, Active = false });

        graph.Connect(g, "value", smooth, "in");
        graph.Connect(smooth, "out", curve, "in");
        graph.Connect(curve, "out", stiffness, "in");
        graph.Connect(stiffness, "out", spring, "in");
        graph.Connect(shells, "value", counter, "counter");
        graph.Connect(counter, "events", envelope, "trigger");
        graph.Connect(envelope, "level", kick, "in");
        graph.Connect(smooth, "out", threshold, "in");
        graph.Connect(threshold, "state", slew, "in");
        graph.Connect(slew, "out", group, "in");
        graph.Connect(test, "value", group, "in");
        graph.Connect(group, "out", buzz, "in");
        graph.Connect(test, "value", buzz, "in");
        return graph;
    }

    [Fact]
    public void RoundTrip_KeepsEverythingExceptEnabled()
    {
        var graph = SampleGraph();
        var json = FfbGraphPersistence.ToJson(graph);

        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("joypro-ffb-graph", root["format"]!.GetValue<string>());
        Assert.Equal(FfbGraphPersistence.CurrentVersion, root["version"]!.GetValue<int>());
        Assert.True(root["enabled"]!.GetValue<bool>());
        Assert.Equal("value", root["connections"]![0]!["fromPort"]!.GetValue<string>());

        var loaded = FfbGraphPersistence.FromJson(json);
        Assert.Empty(loaded.Warnings);
        var copy = loaded.Graph;
        Assert.False(copy.Enabled);                         // never enabled after loading
        Assert.Equal(graph.Name, copy.Name);
        Assert.Equal(graph.Nodes.Select(n => (n.Id, n.Kind, n.Title)), copy.Nodes.Select(n => (n.Id, n.Kind, n.Title)));
        Assert.Equal(graph.Connections, copy.Connections);

        var g = Assert.IsType<TelemetryNode>(copy.FindNode(graph.Nodes[0].Id));
        Assert.Equal(("il2.acc.g", 1.0, 6.0, TelemetryNormalisation.Unsigned, 10.0, 20.0), (g.Key, g.Minimum, g.Maximum, g.Mode, g.X, g.Y));
        var curve = copy.Nodes.OfType<CurveNode>().Single();
        Assert.False(curve.Symmetric);
        Assert.Equal(3, curve.Curve.Nodes.Count);
        Assert.Equal(0.4, curve.Curve.Nodes[1].Position.X, 9);
        var spring = copy.Nodes.OfType<SpringNode>().Single();
        Assert.Equal(("stick-1", FfbAxisSelection.Y, 0.1, 0.7, "spring"), (spring.Device, spring.Axes, spring.InputDefaults["centre"], spring.Saturation, spring.Effect));
        var counter = copy.Nodes.OfType<CounterDeltaToEventNode>().Single();
        Assert.Equal((CounterDirection.Increase, 40.0), (counter.Direction, counter.MaxDelta));
        var envelope = copy.Nodes.OfType<EnvelopeNode>().Single();
        Assert.Equal((0.005, 0.02, 0.03, 0.6), (envelope.Attack, envelope.Hold, envelope.Decay, envelope.Peak));
        var buzz = copy.Nodes.OfType<PeriodicNode>().Single();
        Assert.Equal((FfbWaveform.Triangle, true, 12.0, 0.3, FfbModulation.FrequencyOnly, 5.0, 40.0, CombineMode.Average),
            (buzz.Waveform, buzz.Pulsed, buzz.PulseRateHz, buzz.DutyCycle, buzz.Modulation, buzz.MinFrequencyHz, buzz.MaxFrequencyHz, buzz.InputMode));
        var kick = copy.Nodes.OfType<ConstantForceNode>().Single();
        Assert.Equal((true, 0.4, 2, FfbAxis.Y), (kick.Negative, kick.MaxStrength, kick.Slot, kick.Axis));
        Assert.Equal(CombineMode.SumClamped, copy.Nodes.OfType<GroupNode>().Single().Mode);
        Assert.False(copy.Nodes.OfType<ConstantNode>().Single().Active);
        Assert.Equal(2, copy.Connections.Count(c => c.ToNode == buzz.Id && c.ToPort == "in"));
        Assert.Equal((3.0, 1.0), copy.Nodes.OfType<SlewLimitNode>().Select(s => (s.RiseRate, s.FallRate)).Single());

        // Saving the loaded copy gives the same file (apart from "enabled").
        Assert.Equal(json.Replace("\"enabled\": true", "\"enabled\": false"), FfbGraphPersistence.ToJson(copy));
    }

    [Fact]
    public void SaveAndLoad_ThroughAFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "joypro-ffb-" + Guid.NewGuid().ToString("N") + ".ffbgraph.json");
        try
        {
            FfbGraphPersistence.Save(SampleGraph(), path);
            var loaded = FfbGraphPersistence.Load(path);
            Assert.False(loaded.Graph.Enabled);
            Assert.Equal(14, loaded.Graph.Nodes.Count);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Clone_KeepsEnabledAndIdsButNotState()
    {
        var graph = SampleGraph();
        var clone = FfbGraphPersistence.Clone(graph);
        Assert.True(clone.Enabled);
        Assert.Equal(graph.Nodes.Select(n => n.Id), clone.Nodes.Select(n => n.Id));
        Assert.NotSame(graph.Nodes[0], clone.Nodes[0]);
    }

    [Fact]
    public void Load_RejectsNewerVersionsAndForeignFiles()
    {
        var newer = """{ "format": "joypro-ffb-graph", "version": 99, "nodes": [] }""";
        var ex = Assert.Throws<InvalidDataException>(() => FfbGraphPersistence.FromJson(newer));
        Assert.Contains("newer", ex.Message);
        Assert.Throws<InvalidDataException>(() => FfbGraphPersistence.FromJson("""{ "format": "forza", "version": 1 }"""));
        Assert.Throws<InvalidDataException>(() => FfbGraphPersistence.FromJson("not json"));
    }

    [Fact]
    public void Load_SkipsUnknownNodesAndBadWiresWithWarnings()
    {
        var json = """
        {
          "format": "joypro-ffb-graph", "version": 1, "enabled": true,
          "nodes": [
            { "id": "a", "kind": "Constant", "params": { "value": 0.5 } },
            { "id": "b", "kind": "Abs" },
            { "id": "c", "kind": "Teleporter" },
            { "id": "a", "kind": "Abs" }
          ],
          "connections": [
            { "from": "a", "fromPort": "value", "to": "b", "toPort": "in" },
            { "from": "b", "fromPort": "out", "to": "b", "toPort": "in" },
            { "from": "a", "fromPort": "value", "to": "c", "toPort": "in" },
            { "from": "a", "fromPort": "nope", "to": "b", "toPort": "in" }
          ]
        }
        """;
        var result = FfbGraphPersistence.FromJson(json);
        Assert.False(result.Graph.Enabled);
        Assert.Equal(3, result.Graph.Nodes.Count);
        Assert.NotEqual("a", result.Graph.Nodes[2].Id);    // duplicate id replaced
        Assert.Single(result.Graph.Connections);
        Assert.Equal(5, result.Warnings.Count);            // unknown kind, duplicate id, cycle, unknown node, unknown port
        Assert.Equal(0.5, Assert.IsType<ConstantNode>(result.Graph.FindNode("a")).Value);
    }
}
