using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JoyPro.Ffb.Core.Graph;
using JoyPro.Ffb.Core.Host;
using JoyPro.Ffb.Core.Output;
using JoyPro.Ffb.Core.Runtime;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Ffb.Core.Telemetry.Dcs;
using JoyPro.Ffb.Core.Telemetry.Il2;
using JoyPro.Ffb.Core.Telemetry.WarThunder;

namespace JoyPro.Ffb.Core.Tests;

/// <summary>The built-in flight graph end to end on the synthetic flight (SyntheticFlight), per game, without hardware:
/// the source parses what the game would send, the normalised model derives movement and fire, the graph and the
/// mixer make the effects a dry backend records.</summary>
public class TelemetryFlightTests
{
    /// <summary>Records every Apply (time and commands) and every STOP ALL.</summary>
    private sealed class RecordingBackend : IFfbBackend
    {
        public List<(double Time, IReadOnlyList<FfbEffectCommand> Commands)> Applies { get; } = [];
        public List<double> StopAlls { get; } = [];
        public IFfbClock? Clock { get; init; }
        public string Name => "Recording";
        public IReadOnlyList<FfbDeviceCapabilities> Devices { get; } =
            [new FfbDeviceCapabilities("dry", "Dry", FfbDeviceCapabilities.BothAxes, FfbDeviceCapabilities.AllEffects, 1000)];

        /// <summary>The next Apply calls throw (an unplugged device, a game holding it exclusively).</summary>
        public int FailNext { get; set; }

        public void Apply(double time, IReadOnlyList<FfbEffectCommand> commands)
        {
            lock (Applies)
            {
                if (FailNext > 0)
                {
                    FailNext--;
                    throw new InvalidOperationException("DirectInput Acquire failed (test)");
                }

                Applies.Add((time, commands.ToArray()));
            }
        }

        public void StopAll()
        {
            lock (Applies)
                StopAlls.Add(Clock?.Now ?? double.NaN);
        }

        public void Dispose()
        {
        }

        public double Peak(string tag, double from, double to)
        {
            lock (Applies)
            {
                return Applies.Where(a => a.Time >= from && a.Time <= to)
                    .SelectMany(a => a.Commands)
                    .Where(c => c.Tag == tag)
                    .Select(c => c.PeakForce)
                    .DefaultIfEmpty(-1)
                    .Max();
            }
        }

        public bool Sent(string tag, double from, double to) => Peak(tag, from, to) >= 0;

        public IEnumerable<double> Magnitudes(string tag, double from, double to)
        {
            lock (Applies)
                return Applies.Where(a => a.Time >= from && a.Time <= to).SelectMany(a => a.Commands).Where(c => c.Tag == tag).Select(c => c.Magnitude).ToArray();
        }
    }

    private static Dictionary<string, double> Off(params string[] effects) => effects.ToDictionary(e => e, _ => 0.0);

    /// <summary>Flies <paramref name="seconds"/> of the synthetic flight at 50 Hz on a manual clock.</summary>
    private static RecordingBackend Fly(string game, double seconds, double cap, IReadOnlyDictionary<string, double>? scales = null,
        (double From, double To)? gap = null, Action<FfbRuntime>? check = null)
    {
        var clock = new ManualClock();
        var backend = new RecordingBackend { Clock = clock };
        FrameTelemetrySource source;
        Action<double, FlightState, FlightState?> feed;
        switch (game)
        {
            case TelemetryGame.Dcs:
                var dcs = new DcsTelemetrySource(clock) { Enricher = AircraftTelemetryDeriver.ForDcs() };
                feed = (_, s, _) => dcs.Ingest(SyntheticFlight.DcsLine(s));
                source = dcs;
                break;
            case TelemetryGame.WarThunder:
                var wt = new WarThunderTelemetrySource(clock) { Enricher = AircraftTelemetryDeriver.ForWarThunder() };
                feed = (_, s, _) => wt.Ingest(Encoding.UTF8.GetBytes(SyntheticFlight.WarThunderState(s)), Encoding.UTF8.GetBytes(SyntheticFlight.WarThunderIndicators(s)));
                source = wt;
                break;
            default:
                var il2 = new Il2TelemetrySource(clock) { Enricher = AircraftTelemetryDeriver.ForIl2() };
                feed = (_, s, previous) => il2.Ingest(SyntheticFlight.Il2Packet(s, previous));
                source = il2;
                break;
        }

        using var runtime = new FfbRuntime(backend, clock, new FfbRuntimeOptions
        {
            TickRateHz = 50,
            StaleAfterSeconds = 0.5,
            DefaultDevice = new FfbDeviceSettings(1, cap)
        }) { Source = source, EffectScales = scales ?? new Dictionary<string, double>() };
        runtime.LoadGraph(FfbPresets.Flight());
        runtime.Enabled = true;
        FlightState? previous = null;
        for (var i = 0; i <= (int)Math.Round(seconds * 50); i++)
        {
            var t = i / 50.0;
            clock.Now = t;
            if (gap is not { } g || t < g.From || t >= g.To)
            {
                var state = SyntheticFlight.At(t);
                feed(t, state, previous);
                previous = state;
            }

            runtime.Tick();
            check?.Invoke(runtime);
        }

        return backend;
    }

    [Theory]
    [InlineData(TelemetryGame.Dcs)]
    [InlineData(TelemetryGame.Il2)]
    [InlineData(TelemetryGame.WarThunder)]
    public void Buffet_RisesWithAoaBeyondTheThreshold(string game)
    {
        var r = Fly(game, 6, 1, Off(FfbEffectNames.Spring, FfbEffectNames.GForce));
        // AoA 3 deg until 2 s, then 3 -> 22 deg over 3 s: 10 deg at 3.1 s.
        Assert.False(r.Sent(FfbEffectNames.Buffet, 0, 3.0));
        Assert.True(r.Sent(FfbEffectNames.Buffet, 3.3, 5.0));
        var early = r.Peak(FfbEffectNames.Buffet, 3.3, 3.6);
        var late = r.Peak(FfbEffectNames.Buffet, 4.6, 4.98);
        Assert.True(late > early + 0.1, $"buffet {early} at ~11 deg, {late} at ~20 deg");
        Assert.False(r.Sent(FfbEffectNames.Buffet, 5.1, 6));          // AoA back to 3 deg
        Assert.False(r.Sent(FfbEffectNames.Spring, 0, 6));            // switched off
        Assert.False(r.Sent(FfbEffectNames.GForce, 0, 6));
    }

    [Theory]
    [InlineData(TelemetryGame.Dcs)]
    [InlineData(TelemetryGame.Il2)]
    [InlineData(TelemetryGame.WarThunder)]
    public void GearRumble_OnlyWhileTheGearMoves_GunsBurst_RunwayOnTheGround(string game)
    {
        var r = Fly(game, 11.5, 1);
        Assert.False(r.Sent(FfbEffectNames.Gear, 0, 5.98));
        Assert.True(r.Sent(FfbEffectNames.Gear, 6.05, 6.95));
        Assert.False(r.Sent(FfbEffectNames.Gear, 7.4, 11.5));          // down and locked: quiet (0.3 s hold after 7 s)

        Assert.False(r.Sent(FfbEffectNames.Guns, 0, 7.0));
        var guns = r.Magnitudes(FfbEffectNames.Guns, 7.1, 7.95).ToList();
        Assert.Contains(guns, m => m > 0.1);                            // pulses on
        Assert.Contains(guns, m => m == 0);                             // and off
        Assert.False(r.Sent(FfbEffectNames.Guns, 8.3, 11.5));

        Assert.False(r.Sent(FfbEffectNames.Runway, 0, 7.95));
        Assert.True(r.Sent(FfbEffectNames.Runway, 8.2, 9.8));
        Assert.True(r.Peak(FfbEffectNames.Runway, 8.1, 8.4) > r.Peak(FfbEffectNames.Runway, 9.5, 9.8));   // slower, weaker
        Assert.False(r.Sent(FfbEffectNames.Runway, 10.6, 11.5));        // stopped
        if (game == TelemetryGame.Il2)
            Assert.True(r.Sent(FfbEffectNames.Hits, 7.5, 7.7));
    }

    [Theory]
    [InlineData(TelemetryGame.Dcs)]
    [InlineData(TelemetryGame.Il2)]
    [InlineData(TelemetryGame.WarThunder)]
    public void ForceNeverExceedsTheCap(string game)
    {
        var r = Fly(game, 12, 0.4);
        Assert.NotEmpty(r.Applies);
        foreach (var (time, commands) in r.Applies)
        {
            foreach (var axis in new[] { FfbAxis.X, FfbAxis.Y })
                Assert.True(commands.Where(c => c.Axis == axis).Sum(c => c.PeakForce) <= 0.4 + 1e-9, $"{game} {axis} at {time}");
        }

        Assert.Contains(r.Applies, a => a.Commands.Count >= 4);         // several effects at once were capped together
    }

    [Fact]
    public void TelemetryTimeout_StopsAllOnce_AndNothingUntilTelemetryReturns()
    {
        var silent = new List<double>();
        var r = Fly(TelemetryGame.Dcs, 7, 0.6, gap: (3, 5), check: runtime =>
        {
            if (runtime.State == FfbRuntimeState.Silent)
                silent.Add(runtime.Ticks);
        });
        Assert.Single(r.StopAlls, t => t > 0.1 && t < 5.1);
        var stop = r.StopAlls.Single(t => t > 0.1 && t < 5.1);
        Assert.InRange(stop, 3.48, 3.56);                               // last frame 2.98 s + 0.5 s timeout
        Assert.DoesNotContain(r.Applies, a => a.Time > stop && a.Time < 5);
        Assert.Contains(r.Applies, a => a.Time >= 5 && a.Time < 5.1);        // resumes with the first frame
        Assert.NotEmpty(silent);
    }

    [Fact]
    public void Config_ScalesAndSwitches_PerGameAndAircraft()
    {
        var config = TelemetryFfbConfig.Parse("""
            { "enabled": true, "strength": 0.8, "maxForce": 0.5,
              "games": { "DCS": { "strength": 0.5, "graph": "dcs.jpffb.json",
                                  "effects": { "buffet": { "enabled": false }, "guns": { "strength": 1.5 } },
                                  "aircraft": { "F-16C_50": { "graph": "viper.jpffb.json", "strength": 2, "effects": { "buffet": { "enabled": true, "strength": 0.3 } } } } } } }
            """);
        Assert.Equal(0.4, config.GainFor("DCS", null), 9);
        Assert.Equal(0.8, config.GainFor("DCS", "F-16C_50"), 9);
        Assert.Equal(0.8, config.GainFor("IL2Game", null), 9);
        Assert.Equal(0, config.EffectScales("DCS", null)["buffet"]);
        Assert.Equal(1.5, config.EffectScales("DCS", null)["guns"]);
        Assert.Equal(0.3, config.EffectScales("DCS", "F-16C_50")["buffet"]);
        Assert.Equal("viper.jpffb.json", config.GraphFor("DCS", "F-16C_50"));
        Assert.Equal("dcs.jpffb.json", config.GraphFor("DCS", "A-10C_2"));
        Assert.Equal(41992, config.PortFor(TelemetryGame.Find(TelemetryGame.Dcs)!));

        // A broken file means off; the engine's PascalCase is read too; the round trip keeps everything.
        Assert.False(TelemetryFfbConfig.Parse("{ not json").Enabled);
        Assert.True(TelemetryFfbConfig.Parse("""{ "Enabled": true, "MaxForce": 0.3 }""").Enabled);
        Assert.Equal(0.3, TelemetryFfbConfig.Parse("""{ "Enabled": true, "MaxForce": 0.3 }""").MaxForce);
        Assert.Equal(TelemetryFfbConfig.DefaultMaxForce, new TelemetryFfbConfig().MaxForce);
        Assert.Equal(config.ToJson(), TelemetryFfbConfig.Parse(config.ToJson()).ToJson());

        var r = Fly(TelemetryGame.Dcs, 6, 1, config.EffectScales("DCS", null));
        Assert.False(r.Sent(FfbEffectNames.Buffet, 0, 6));
    }

    [Fact]
    public void AircraftModel_DerivesMovementFireAndGroundRoll()
    {
        var deriver = AircraftTelemetryDeriver.ForDcs();
        TelemetryFrame Frame(double t, double gear, double shells, double rod) => deriver.Enrich(new TelemetryFrame(t, new Dictionary<string, double>
        {
            ["dcs.gear"] = gear, ["dcs.shells"] = shells, ["dcs.rod_l"] = rod, ["dcs.gs"] = 40, ["dcs.aoa"] = 0.2
        }));

        var a = Frame(0, 0, 500, 0);
        Assert.Equal(0, a.Values[AircraftTelemetry.GearMoving]);
        Assert.Equal(0, a.Values[AircraftTelemetry.Firing]);
        Assert.Equal(0, a.Values[AircraftTelemetry.GroundRoll]);
        Assert.Equal(0.2 * 180 / Math.PI, a.Values[AircraftTelemetry.Aoa], 9);
        var b = Frame(0.02, 0.05, 497, 0);
        Assert.Equal(1, b.Values[AircraftTelemetry.GearMoving]);
        Assert.Equal(1, b.Values[AircraftTelemetry.Firing]);
        Assert.Equal(3, b.Values[AircraftTelemetry.RoundsFired]);
        var c = Frame(0.5, 0.05, 497, 0.2);
        Assert.Equal(0, c.Values[AircraftTelemetry.GearMoving]);       // 0.3 s hold is over
        Assert.Equal(0, c.Values[AircraftTelemetry.Firing]);
        Assert.Equal(40, c.Values[AircraftTelemetry.GroundRoll]);
        var rearm = Frame(0.52, 0.05, 1000, 0.2);                        // rearm: no fire
        Assert.Equal(0, rearm.Values[AircraftTelemetry.Firing]);
        var jump = Frame(0.54, 1, 1000, 0.2);                            // a jump is a new aircraft, not movement
        Assert.Equal(0, jump.Values[AircraftTelemetry.GearMoving]);
    }

    [Fact]
    public async Task Host_OverLoopbackUdp_FollowsTheGameProcess_AndStopsWhenItExits()
    {
        using var game = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        var config = TelemetryFfbConfig.Parse("""{ "enabled": true, "maxForce": 0.5, "tickRateHz": 50, "timeoutSeconds": 0.5 }""");
        var clock = new StopwatchClock();
        var backend = new RecordingBackend { Clock = clock };
        using var host = new TelemetryFfbHost(config, TelemetryGame.Find("DCS")!, backend, clock, new TelemetryFfbHostOptions { GamePid = game.Id, PortOverride = 0 });
        var exited = new TaskCompletionSource();
        var states = new List<TelemetryFfbState>();
        host.GameExited += () => exited.TrySetResult();
        host.StatusChanged += s => { lock (states) states.Add(s.State); };
        host.Start();
        Assert.Equal(TelemetryFfbState.NoTelemetry, host.Status.State);
        Assert.Contains("no telemetry on UDP " + host.Port, host.Status.Reason);

        using (var udp = new UdpClient())
        {
            var target = new IPEndPoint(IPAddress.Loopback, host.Port);
            var plane = Encoding.UTF8.GetBytes("plane Synthetic-F");
            udp.Send(plane, plane.Length, target);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 1.5)
            {
                var line = Encoding.UTF8.GetBytes(SyntheticFlight.DcsLine(SyntheticFlight.At(3 + watch.Elapsed.TotalSeconds)));
                udp.Send(line, line.Length, target);
                await Task.Delay(20);
            }
        }

        Assert.Equal(TelemetryFfbState.Active, host.Status.State);
        Assert.Equal("Synthetic-F", host.Aircraft);
        Assert.Contains(backend.Applies, a => a.Commands.Any(c => c.Tag == FfbEffectNames.Buffet));

        var stopsBefore = backend.StopAlls.Count;
        game.Kill();
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TelemetryFfbState.WaitingForGame, host.Status.State);
        Assert.True(backend.StopAlls.Count > stopsBefore);
        var applies = backend.Applies.Count;
        await Task.Delay(300);
        Assert.Equal(applies, backend.Applies.Count);                   // nothing more after the game is gone
        lock (states)
            Assert.Contains(TelemetryFfbState.Active, states);
    }

    [Fact]
    public async Task WarThunder_PollsTheLocalServer_OnlyValidStatesBecomeFrames()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var inFlight = true;
        var requests = 0;
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                        try
                        {
                            while (await reader.ReadLineAsync() is { } request)
                            {
                                while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                                {
                                }

                                Interlocked.Increment(ref requests);
                                var state = SyntheticFlight.At(4);
                                var body = request.Contains("/indicators")
                                    ? SyntheticFlight.WarThunderIndicators(state, inFlight)
                                    : SyntheticFlight.WarThunderState(state, inFlight);
                                var bytes = Encoding.UTF8.GetBytes(body);
                                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\n\r\n"));
                                await stream.WriteAsync(bytes);
                            }
                        }
                        catch (IOException)
                        {
                        }
                    }
                });
            }
        });

        var clock = new StopwatchClock();
        using var source = new WarThunderTelemetrySource(clock, new Uri($"http://127.0.0.1:{port}/"), 20) { Enricher = AircraftTelemetryDeriver.ForWarThunder() };
        source.Start();
        await WaitUntil(() => source.FramesPublished >= 5);
        Assert.Equal("synthetic_fighter", source.Vehicle);
        Assert.True(source.TryGet(AircraftTelemetry.Aoa, out var aoa) && aoa > 9);
        Assert.True(source.TryGet("wt.ias_km_h", out _));
        Assert.True(source.TryGet("wt.ind.trimmer", out _));

        inFlight = false;
        await Task.Delay(200);
        var frames = source.FramesPublished;
        await Task.Delay(400);
        Assert.Equal(frames, source.FramesPublished);                   // "valid": false -> no frames -> silence
        Assert.True(source.ServerAnswering);

        source.Stop();
        var after = Volatile.Read(ref requests);
        await Task.Delay(300);
        Assert.Equal(after, Volatile.Read(ref requests));                // stopped: no more requests
        stop.Cancel();
        listener.Stop();
        await server;
    }

    [Fact]
    public async Task Host_AfterADeviceError_StopsAndTriesAgain()
    {
        var config = TelemetryFfbConfig.Parse("""{ "enabled": true, "maxForce": 0.5 }""");
        var clock = new StopwatchClock();
        var backend = new RecordingBackend { Clock = clock, FailNext = 1 };
        using var host = new TelemetryFfbHost(config, TelemetryGame.Find("DCS")!, backend, clock,
            new TelemetryFfbHostOptions { IgnoreGameProcess = true, PortOverride = 0, RetrySeconds = 1 });
        var states = new List<string>();
        host.StatusChanged += s => { lock (states) states.Add(s.Reason); };
        host.Start();
        using var udp = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, host.Port);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 2.5)
        {
            var line = Encoding.UTF8.GetBytes(SyntheticFlight.DcsLine(SyntheticFlight.At(4)));
            udp.Send(line, line.Length, target);
            await Task.Delay(20);
        }

        lock (states)
            Assert.Contains(states, s => s.StartsWith("stopped after an error: DirectInput Acquire failed (test)", StringComparison.Ordinal));
        Assert.Equal(TelemetryFfbState.Active, host.Status.State);    // tried again after 1 s and works
        Assert.NotEmpty(backend.Applies);
        Assert.NotEmpty(backend.StopAlls);
    }

    [Fact]
    public void WarThunder_KeysAreSanitised()
    {
        Assert.Equal("ias_km_h", WarThunderProtocol.Sanitise("IAS, km/h"));
        Assert.Equal("gear_pct", WarThunderProtocol.Sanitise("gear, %"));
        Assert.Equal("throttle_1_pct", WarThunderProtocol.Sanitise("throttle 1, %"));
        Assert.Equal("aoa_deg", WarThunderProtocol.Sanitise("AoA, deg"));
        Assert.Equal("ny", WarThunderProtocol.Sanitise("Ny"));
        var values = new Dictionary<string, double>();
        Assert.False(WarThunderProtocol.Read("{\"valid\": false}"u8, "wt.", values, out _));
        Assert.True(WarThunderProtocol.Read("{\"valid\": true, \"type\": \"x\", \"M\": 0.5, \"flag\": true, \"nested\": {\"a\": 1}}"u8, "wt.", values, out var type));
        Assert.Equal("x", type);
        Assert.Equal(0.5, values["wt.m"]);
        Assert.Equal(1, values["wt.flag"]);
        Assert.False(values.ContainsKey("wt.a"));
    }

    [Fact]
    public void Runtime_IdleLoop_UsesNoTicksWithoutTelemetry()
    {
        var clock = new StopwatchClock();
        var backend = new RecordingBackend { Clock = clock };
        var source = new DcsTelemetrySource(clock);
        using var runtime = new FfbRuntime(backend, clock, new FfbRuntimeOptions { TickRateHz = 100, IdleWaitSeconds = 1 }) { Source = source };
        runtime.LoadGraph(FfbPresets.Flight());
        runtime.Enabled = true;
        runtime.Start();
        Thread.Sleep(1200);
        Assert.InRange(runtime.Ticks, 1, 4);                             // one check a second, not 100
        Assert.Equal(0, runtime.Evaluations);

        // A frame wakes it at once.
        source.FrameReceived += _ => runtime.Wake();
        source.Ingest(SyntheticFlight.DcsLine(SyntheticFlight.At(4)));
        Thread.Sleep(100);
        Assert.True(runtime.Evaluations >= 3, "evaluations " + runtime.Evaluations);
        runtime.Stop();
    }

    private static async Task WaitUntil(Func<bool> condition, double seconds = 5)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > seconds)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }
}
