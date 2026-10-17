using JoyPro.Ffb.Core.Graph;
using JoyPro.Ffb.Core.Output;
using JoyPro.Ffb.Core.Runtime;
using JoyPro.Ffb.Core.Telemetry;

namespace JoyPro.Ffb.Core.Tests;

public class RuntimeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Stick = "mock";

    private static FfbRuntimeOptions Uncapped => new()
    {
        DefaultDevice = new FfbDeviceSettings(Gain: 1, GainCap: 1)
    };

    /// <summary>Drives a replay and the runtime tick by tick at 100 Hz on a manual clock.</summary>
    private static void Drive(FfbRuntime runtime, ManualClock clock, ReplayTelemetrySource replay, double seconds, double start = 0)
    {
        for (var i = 0; i <= (int)Math.Round(seconds * 100); i++)
        {
            var t = start + i / 100.0;
            clock.Now = t;
            replay.StepTo(t);
            runtime.Tick();
        }
    }

    private static int RisingEdges(IEnumerable<double> values, double threshold = 1e-6)
    {
        var edges = 0;
        var previous = 0.0;
        foreach (var value in values)
        {
            if (Math.Abs(value) > threshold && Math.Abs(previous) <= threshold)
                edges++;
            previous = value;
        }

        return edges;
    }

    [Fact]
    public void GLoad_DrivesSpringStiffness_OverAReplayedRecording()
    {
        // 3 s: 1 G, pull to 6 G at 1.5 s, back to 1 G.
        var recording = TelemetryTests.Record(t => 1 + 5 * Math.Max(0, Math.Sin(Math.PI * t / 3)), 3.0);

        var graph = new FfbGraph { Name = "G -> spring" };
        var g = graph.Add(new TelemetryNode { Key = "il2.acc.g", Minimum = 1, Maximum = 6, Mode = TelemetryNormalisation.Unsigned });
        var smooth = graph.Add(new LowPassNode { TimeConstant = 0.05 });
        var stiffness = graph.Add(new ScaleOffsetNode { Scale = 0.7, Offset = 0.2 });
        var spring = graph.Add(new SpringNode { Axes = FfbAxisSelection.Y });
        graph.Connect(g, "value", smooth, "in");
        graph.Connect(smooth, "out", stiffness, "in");
        graph.Connect(stiffness, "out", spring, "in");

        var clock = new ManualClock();
        var backend = new MockFfbBackend();
        backend.SetStick(Stick, FfbAxis.Y, 0.5);     // stick held half forward
        using var runtime = new FfbRuntime(backend, clock, Uncapped) { Source = new ReplayTelemetrySource(recording, clock) };
        runtime.LoadGraph(graph);
        runtime.Enabled = true;
        Drive(runtime, clock, (ReplayTelemetrySource)runtime.Source!, 3.0);

        var samples = backend.SamplesFor(Stick, FfbAxis.Y);
        Assert.InRange(samples.Count, 290, 301);
        double ForceNear(double t) => samples.MinBy(s => Math.Abs(s.Time - t)).Force;

        // force = -coefficient * 0.5; coefficient = 0.2 at 1 G .. 0.9 at 6 G.
        Assert.Equal(-0.1, ForceNear(0.0), 1e-9);
        Assert.Equal(-0.45, ForceNear(1.5), 0.01);
        Assert.True(ForceNear(1.5) < ForceNear(0.75) && ForceNear(0.75) < ForceNear(0.1));
        Assert.Equal(-0.1, ForceNear(3.0), 0.03);       // the low pass lags a little behind the release
        Assert.All(samples, s => Assert.InRange(s.Force, -0.46, 0));

        var last = backend.LastCommands.Single();
        Assert.Equal(FfbEffectType.Spring, last.Type);
        Assert.Equal(FfbAxis.Y, last.Axis);
        Assert.Equal(Stick, last.Device);          // empty sink device -> the backend's first device
    }

    [Fact]
    public void ShellCounter_GivesOnePulsePerShell()
    {
        // 10 single shots 0.1 s apart from t = 0.5, then a burst of 3 shells inside one telemetry frame at t = 1.8.
        double Shells(double t)
        {
            var fired = t < 0.5 ? 0 : Math.Min(10, Math.Floor((t - 0.5) / 0.1) + 1);
            if (t >= 1.8)
                fired += 3;
            return 400 - fired;
        }

        var recording = TelemetryTests.Record(_ => 1, 2.5, shells: Shells);
        var graph = new FfbGraph();
        var shells = graph.Add(new TelemetryNode { Key = "dcs.shells" });
        var counter = graph.Add(new CounterDeltaToEventNode());
        var envelope = graph.Add(new EnvelopeNode { Hold = 0.02, Decay = 0.02, Peak = 0.7 });
        var kick = graph.Add(new ConstantForceNode { Axis = FfbAxis.Y });
        graph.Connect(shells, "value", counter, "counter");
        graph.Connect(counter, "events", envelope, "trigger");
        graph.Connect(envelope, "level", kick, "in");

        var clock = new ManualClock();
        var backend = new MockFfbBackend();
        var replay = new ReplayTelemetrySource(recording, clock);
        using var runtime = new FfbRuntime(backend, clock) { Source = replay };
        runtime.LoadGraph(graph);
        runtime.Enabled = true;
        Drive(runtime, clock, replay, 2.5);

        var forces = backend.SamplesFor(Stick, FfbAxis.Y).Select(s => s.Force).ToList();
        Assert.Equal(13, RisingEdges(forces));
        Assert.Equal(13, runtime.Evaluator!.GetOutput(counter.Id, "total").X);
        Assert.All(forces, f => Assert.InRange(f, 0, 0.7 + 1e-9));
        Assert.All(backend.SamplesFor(Stick, FfbAxis.X), s => Assert.Equal(0, s.Force));
    }

    [Fact]
    public void StaleTelemetry_SilencesAndResumesWithoutABurst()
    {
        // Frames stop between 1.0 s and 2.0 s; meanwhile 20 shells "fire" (the counter jumps by 20 at resume).
        var recording = TelemetryTests.Record(_ => 1, 3.0, shells: t => t < 1.5 ? 300 : 280, gap: (1.0, 2.0));
        var graph = new FfbGraph();
        var shells = graph.Add(new TelemetryNode { Key = "dcs.shells" });
        var counter = graph.Add(new CounterDeltaToEventNode());
        var envelope = graph.Add(new EnvelopeNode { Hold = 0.05, Decay = 0 });
        var kick = graph.Add(new ConstantForceNode { Axis = FfbAxis.Y });
        var hold = graph.Add(new ConstantForceNode { Axis = FfbAxis.X });
        var constant = graph.Add(new ConstantNode { Value = 0.3 });
        graph.Connect(shells, "value", counter, "counter");
        graph.Connect(counter, "events", envelope, "trigger");
        graph.Connect(envelope, "level", kick, "in");
        graph.Connect(constant, "value", hold, "in");

        var clock = new ManualClock();
        var backend = new MockFfbBackend { Clock = clock };
        var replay = new ReplayTelemetrySource(recording, clock);
        using var runtime = new FfbRuntime(backend, clock, Uncapped) { Source = replay };
        runtime.LoadGraph(graph);
        runtime.Enabled = true;

        Drive(runtime, clock, replay, 1.2);
        Assert.Equal(FfbRuntimeState.Active, runtime.State);   // last frame at 1.0, 0.2 s old
        Assert.Equal(0, backend.StopAllCount);

        Drive(runtime, clock, replay, 0.5, start: 1.21);
        Assert.Equal(FfbRuntimeState.Silent, runtime.State);
        Assert.Equal(1, backend.StopAllCount);                 // once, not every tick
        Assert.InRange(backend.StopAllTimes[0], 1.299, 1.311);  // within 300 ms + one tick of the last frame
        Assert.Empty(runtime.LastOutput);
        Assert.Equal(0, backend.ForceAt(Stick, FfbAxis.X, 1.7));

        Drive(runtime, clock, replay, 1.0, start: 1.71);
        Assert.Equal(FfbRuntimeState.Active, runtime.State);
        Assert.Equal(0.3, backend.ForceAt(Stick, FfbAxis.X, clock.Now), 9);
        // The counter jumped from 300 to 280 during the gap: no 20-shell burst after the reset.
        Assert.Equal(0, runtime.Evaluator!.GetOutput(counter.Id, "total").X);
        Assert.Equal(0, RisingEdges(backend.SamplesFor(Stick, FfbAxis.Y).Select(s => s.Force)));
    }

    [Fact]
    public void NoTelemetryAtAll_IsSilent()
    {
        var clock = new ManualClock();
        var backend = new MockFfbBackend();
        using var runtime = new FfbRuntime(backend, clock);
        var graph = new FfbGraph();
        var c = graph.Add(new ConstantNode { Value = 0.5 });
        var force = graph.Add(new ConstantForceNode { Axis = FfbAxis.X });
        graph.Connect(c, "value", force, "in");
        runtime.LoadGraph(graph);
        runtime.Enabled = true;
        runtime.Tick();
        Assert.Equal(FfbRuntimeState.Silent, runtime.State);
        Assert.Equal(0, backend.ApplyCount);

        runtime.Source = new SyntheticTelemetrySource(clock); // a source that never published
        runtime.Tick();
        Assert.Equal(0, backend.ApplyCount);
    }

    [Fact]
    public void GainCap_LimitsTheCombinedForcePerAxis()
    {
        var commands = new[]
        {
            new FfbEffectCommand("a", FfbAxis.X, FfbEffectType.ConstantForce, Magnitude: 0.7),
            new FfbEffectCommand("a", FfbAxis.X, FfbEffectType.ConstantForce, Magnitude: 0.7),
            new FfbEffectCommand("a", FfbAxis.Y, FfbEffectType.ConstantForce, Magnitude: -0.6),
            new FfbEffectCommand("a", FfbAxis.Y, FfbEffectType.Periodic, Magnitude: 0.6, FrequencyHz: 20),
            new FfbEffectCommand("b", FfbAxis.X, FfbEffectType.Spring, Coefficient: 0.4, Saturation: 1),
            new FfbEffectCommand("b", FfbAxis.X, FfbEffectType.Spring, Coefficient: 0.2, Centre: 0.3, Saturation: 0.5),
        };
        var settings = new Dictionary<string, FfbDeviceSettings>
        {
            ["a"] = new(Gain: 1, GainCap: 0.5),
            ["b"] = new(Gain: 0.5, GainCap: 1)
        };

        var mixed = FfbMixer.Mix(commands, d => settings[d], 0.1);

        var x = mixed.Single(c => c.Device == "a" && c.Axis == FfbAxis.X);
        Assert.Equal(0.5, x.Magnitude, 9);                    // 1.4 -> clamped 1 -> capped 0.5
        var y = mixed.Where(c => c.Device == "a" && c.Axis == FfbAxis.Y).ToList();
        Assert.Equal(-0.25, y.Single(c => c.Type == FfbEffectType.ConstantForce).Magnitude, 9);
        Assert.Equal(0.25, y.Single(c => c.Type == FfbEffectType.Periodic).Magnitude, 9);
        Assert.True(y.Sum(c => c.PeakForce) <= 0.5 + 1e-9);

        var spring = mixed.Single(c => c.Device == "b");      // same slot: the stronger spring wins
        Assert.Equal(0.2, spring.Coefficient, 9);             // 0.4 * gain 0.5
        Assert.Equal(0, spring.Centre, 9);
        Assert.Equal(0.5, spring.Saturation, 9);              // its saturation * gain
        Assert.All(mixed, c => Assert.Equal(0.1, c.DurationSeconds));
    }

    [Fact]
    public void GainCap_HoldsInTheRuntimeForAnyGraphOutput()
    {
        var clock = new ManualClock();
        var source = new SyntheticTelemetrySource(clock).Define("v", t => 3 * Math.Sin(7 * t));
        var graph = new FfbGraph();
        var v = graph.Add(new TelemetryNode { Key = "v" });
        var forceX = graph.Add(new ConstantForceNode { Axis = FfbAxis.X, Bipolar = true });
        var forceY = graph.Add(new ConstantForceNode { Axis = FfbAxis.Y, Bipolar = true, Slot = 1 });
        var periodic = graph.Add(new PeriodicNode());
        periodic.InputDefaults["frequency"] = 40;
        graph.Connect(v, "value", forceX, "in");
        graph.Connect(v, "value", forceY, "in");
        graph.Connect(v, "value", periodic, "in");

        var backend = new MockFfbBackend();
        using var runtime = new FfbRuntime(backend, clock, new FfbRuntimeOptions
        {
            Devices = new Dictionary<string, FfbDeviceSettings> { [Stick] = new(Gain: 2, GainCap: 0.4) }
        }) { Source = source };
        runtime.LoadGraph(graph);
        runtime.Enabled = true;
        for (var i = 0; i < 300; i++)
        {
            clock.Now = i / 100.0;
            source.Step(clock.Now);
            runtime.Tick();
        }

        Assert.All(backend.Samples, s => Assert.InRange(s.Force, -0.4 - 1e-9, 0.4 + 1e-9));
        Assert.Contains(backend.Samples, s => Math.Abs(s.Force) > 0.39);
    }

    [Fact]
    public void LoadingNeverEnables_AndDisablingStopsAll()
    {
        var graph = PersistenceTests.SampleGraph();
        Assert.True(graph.Enabled);
        var clock = new ManualClock();
        var backend = new MockFfbBackend();
        var source = new SyntheticTelemetrySource(clock).Define("il2.acc.g", _ => 4);
        source.Step(0);
        using var runtime = new FfbRuntime(backend, clock) { Source = source };

        runtime.LoadGraph(graph);
        Assert.False(runtime.Enabled);
        runtime.Tick();
        Assert.Equal(0, backend.ApplyCount);
        Assert.Equal(FfbRuntimeState.Idle, runtime.State);

        runtime.Enabled = true;
        runtime.Tick();
        Assert.Equal(1, backend.ApplyCount);

        runtime.LoadGraph(graph);                    // a reload switches it off again (with STOP ALL)
        Assert.False(runtime.Enabled);
        Assert.Equal(1, backend.StopAllCount);
        runtime.Tick();
        Assert.Equal(1, backend.ApplyCount);

        runtime.Enabled = true;
        runtime.Tick();
        runtime.Enabled = false;
        Assert.Equal(2, backend.StopAllCount);
        Assert.Equal(FfbRuntimeState.Idle, runtime.State);
    }

    [Fact]
    public void Exceptions_StopAllAndSwitchOff()
    {
        var clock = new ManualClock();
        var backend = new MockFfbBackend();
        var source = new SyntheticTelemetrySource(clock);
        source.Step(0);
        using var runtime = new FfbRuntime(backend, clock) { Source = source };
        var graph = new FfbGraph();
        graph.Add(new ConstantForceNode());
        runtime.LoadGraph(graph);
        var faulted = new ManualResetEventSlim();
        runtime.Faulted += _ => faulted.Set();
        runtime.Enabled = true;

        backend.FailOnApply = true;
        runtime.Tick();
        Assert.Equal(FfbRuntimeState.Faulted, runtime.State);
        Assert.False(runtime.Enabled);
        Assert.IsType<InvalidOperationException>(runtime.LastError);
        Assert.Equal(1, backend.StopAllCount);
        Assert.True(faulted.Wait(TimeSpan.FromSeconds(5)));

        backend.FailOnApply = false;
        runtime.Tick();                              // stays off until the user enables again
        Assert.Equal(0, backend.ApplyCount);
        runtime.Enabled = true;
        runtime.Tick();
        Assert.Equal(1, backend.ApplyCount);
        Assert.Equal(FfbRuntimeState.Active, runtime.State);
    }

    [Fact]
    public void Effects_HaveFiniteDurationsThatExpireWithoutRefresh()
    {
        var clock = new ManualClock();
        var backend = new MockFfbBackend();
        var source = new SyntheticTelemetrySource(clock);
        source.Step(0);
        using var runtime = new FfbRuntime(backend, clock) { Source = source };
        var graph = new FfbGraph();
        var c = graph.Add(new ConstantNode { Value = 0.5 });
        var force = graph.Add(new ConstantForceNode { Axis = FfbAxis.X });
        graph.Connect(c, "value", force, "in");
        runtime.LoadGraph(graph);
        runtime.Enabled = true;
        runtime.Tick();

        Assert.All(backend.LastCommands, cmd => Assert.Equal(0.1, cmd.DurationSeconds));
        Assert.Equal(0.5, backend.ForceAt(Stick, FfbAxis.X, 0.05), 9);
        // The loop "hangs": no more refreshes. The device lets go on its own.
        Assert.Equal(0, backend.ForceAt(Stick, FfbAxis.X, 0.15));
    }

    [Fact]
    public void UnknownDevicesAndUnsupportedEffectsAreDropped()
    {
        var clock = new ManualClock();
        var backend = new MockFfbBackend(new FfbDeviceCapabilities("pedals", "Pedals", [FfbAxis.X], [FfbEffectType.Spring], 200));
        var source = new SyntheticTelemetrySource(clock);
        source.Step(0);
        using var runtime = new FfbRuntime(backend, clock) { Source = source };
        var graph = new FfbGraph();
        var one = graph.Add(new ConstantNode { Value = 1 });
        var both = graph.Add(new SpringNode { Axes = FfbAxisSelection.Both });      // Y unsupported
        var damper = graph.Add(new DamperNode { Axes = FfbAxisSelection.X });       // effect unsupported
        var elsewhere = graph.Add(new SpringNode { Device = "elsewhere", Axes = FfbAxisSelection.X });
        graph.Connect(one, "value", both, "in");
        graph.Connect(one, "value", damper, "in");
        graph.Connect(one, "value", elsewhere, "in");
        runtime.LoadGraph(graph);
        runtime.Enabled = true;
        runtime.Tick();

        var sent = Assert.Single(backend.LastCommands);
        Assert.Equal(("pedals", FfbAxis.X, FfbEffectType.Spring), (sent.Device, sent.Axis, sent.Type));
        Assert.Equal(3, runtime.DroppedCommands);
    }

    [Fact]
    public void LoopThread_RunsNearOneHundredHertz_AndStopSendsStopAll()
    {
        var clock = new StopwatchClock();
        var backend = new MockFfbBackend();
        var source = new SyntheticTelemetrySource(clock).Define("x", _ => 0.2);
        using var runtime = new FfbRuntime(backend, clock) { Source = source };
        var graph = new FfbGraph();
        var x = graph.Add(new TelemetryNode { Key = "x" });
        var force = graph.Add(new ConstantForceNode { Axis = FfbAxis.X });
        graph.Connect(x, "value", force, "in");
        runtime.LoadGraph(graph);
        runtime.Enabled = true;

        using var feeding = new CancellationTokenSource();
        var feeder = Task.Run(async () =>
        {
            while (!feeding.IsCancellationRequested)
            {
                source.Step(clock.Now);
                await Task.Delay(10);
            }
        });

        runtime.Start();
        Assert.True(runtime.IsRunning);
        var started = clock.Now;
        Thread.Sleep(600);
        runtime.Stop();
        var elapsed = clock.Now - started;
        feeding.Cancel();
        feeder.Wait();

        var rate = runtime.Ticks / elapsed;
        output.WriteLine($"Loop rate: {rate:0.0} Hz over {elapsed:0.000} s, {backend.ApplyCount} applies");
        Assert.InRange(rate, 40, 130);
        Assert.True(backend.ApplyCount > 20);
        Assert.False(runtime.IsRunning);
        Assert.True(backend.StopAllCount >= 1);
        Assert.Equal(0, backend.ForceAt(Stick, FfbAxis.X, clock.Now));
    }

    [Fact]
    public void LoopedGraph_IsBrokenAndStillRuns()
    {
        var graph = new FfbGraph();
        var a = graph.Add(new AddNode());
        var b = graph.Add(new AbsNode());
        var force = graph.Add(new ConstantForceNode { Axis = FfbAxis.X });
        graph.Connect(a, "out", b, "in");
        graph.Connect(b, "out", force, "in");
        graph.AddConnectionUnchecked(new FfbConnection(b.Id, "out", a.Id, "a"));

        var clock = new ManualClock();
        var backend = new MockFfbBackend();
        var source = new SyntheticTelemetrySource(clock);
        source.Step(0);
        using var runtime = new FfbRuntime(backend, clock) { Source = source };

        // LoadGraph copies through the persistence, which refuses the loop wire.
        Assert.Empty(runtime.LoadGraph(graph));

        // The evaluator on the original graph cuts it.
        var evaluator = new FfbGraphEvaluator(graph);
        Assert.Single(evaluator.BrokenConnections);
        runtime.LoadEvaluator(evaluator);
        runtime.Enabled = true;
        runtime.Tick();
        Assert.Equal(FfbRuntimeState.Active, runtime.State);
    }
}
