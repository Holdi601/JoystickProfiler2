using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JoyPro.Ffb.Core.Host;
using JoyPro.Ffb.Core.Output;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Ffb.Core.Telemetry.Dcs;
using JoyPro.Ffb.Core.Telemetry.Il2;
using JoyPro.Ffb.Core.Telemetry.Recording;
using JoyPro.Ffb.Core.Telemetry.WarThunder;
using JoyPro.TelemetrySessions;

namespace JoyPro.Ffb.Core.Tests;

// The session recorder (capture, decimation, session start/end, raw, crash safety) and the hosts that run it, fed with
// recorded fixture streams of the existing parsers (DCS lines, IL-2 packets, War Thunder JSON).
public class TelemetryRecorderTests : IDisposable
{
    private readonly string _folder = Path.Combine(TestEnvironment.DataFolder, "rec-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class TestSource(IFfbClock clock) : FrameTelemetrySource("Test", [new TelemetryDescriptor("t.lat", "Latitude", "deg", -90, 90, "Position")], clock)
    {
        public override IReadOnlyList<TelemetryNavMapping> NavMappings { get; } =
            [new(TelemetryNav.Latitude, "t.lat"), new(TelemetryNav.Longitude, "t.lon"), new(TelemetryNav.Altitude, "t.alt")];

        public void Frame(double t, params (string Key, double Value)[] values) =>
            Publish(new TelemetryFrame(t, values.Select(v => new KeyValuePair<string, double>(v.Key, v.Value))));

        public void Raw(string stream, string text) => PublishRaw(stream, Encoding.ASCII.GetBytes(text));
        public void Event(string kind, string text = "") => RaiseEvent(kind, text);
        public void Info(string key, string value) => SetInfo(key, value);
    }

    private TelemetryRecordingConfig Config(double sampleHz = 0, bool raw = true, double timeout = 10) => new()
    {
        Enabled = true,
        Folder = _folder,
        SampleHz = sampleHz,
        KeepRaw = raw,
        SessionTimeoutSeconds = timeout,
        MaxTotalMb = 0
    };

    private static TelemetrySessionRecorderOptions Manual => new() { TickPeriod = TimeSpan.Zero, MinFreeBytes = 0 };

    private static List<(TelemetrySessionReader.RecordKind Kind, double T, string Text)> ReadAll(string path, out TelemetrySessionReader readerOut)
    {
        var list = new List<(TelemetrySessionReader.RecordKind, double, string)>();
        var reader = new TelemetrySessionReader(path, includeRaw: true);
        while (reader.Next())
        {
            list.Add((reader.Kind, reader.Time, reader.Kind switch
            {
                TelemetrySessionReader.RecordKind.Event => reader.EventKind + ":" + reader.EventText,
                TelemetrySessionReader.RecordKind.Raw => reader.RawStream + ":" + Encoding.ASCII.GetString(reader.RawData),
                _ => ""
            }));
        }

        readerOut = reader;
        return list;
    }

    [Fact]
    public void Recorder_WritesASession_WithInfoEventsRawAndWithoutDerivedKeys()
    {
        var clock = new ManualClock(100);
        var source = new TestSource(clock);
        source.Info(TelemetryInfoKeys.Aircraft, "F-16C_50");
        string? started = null;
        using (var recorder = new TelemetrySessionRecorder(source, "DCS", Config(), Manual))
        {
            recorder.SessionStarted += path => started = path;
            source.Event(TelemetryEventKinds.Map, "Caucasus");   // before the first frame: goes into the session
            for (var i = 0; i < 50; i++)
            {
                clock.Now = 100 + i * 0.02;
                source.Raw("udp", "packet " + i);
                source.Frame(i * 0.02, ("t.lat", 41 + i * 1e-4), ("t.lon", 42), ("t.alt", 1000), ("ac.ias", 99));
                if (i == 10)
                    source.Event(TelemetryEventKinds.Message, "hello");
            }

            Assert.True(recorder.WaitIdle(TimeSpan.FromSeconds(5)));
            Assert.True(recorder.Recording);
            Assert.NotNull(recorder.CurrentPath);
            Assert.True(TelemetrySessionStore.IsInUse(recorder.CurrentPath!));
        }

        Assert.NotNull(started);
        var info = TelemetrySessionStore.ReadInfo(started!);
        Assert.True(info.Complete);
        Assert.Equal("recording stopped", info.EndReason);
        Assert.Equal("DCS", info.Game);
        Assert.Equal("F-16C_50", info.Info("aircraft"));
        Assert.Equal(50, info.Frames);
        Assert.Equal(0.98, info.Duration, 3);
        Assert.True(info.HasPosition);
        Assert.Contains("Caucasus", info.Maps);

        var records = ReadAll(started!, out var reader);
        using (reader)
        {
            Assert.Equal(-1, reader.ChannelIndex("ac.ias"));          // derived values are not written
            Assert.Equal("Latitude", reader.Channels[reader.ChannelIndex("t.lat")].Title);
        }

        Assert.Equal(50, records.Count(r => r.Kind == TelemetrySessionReader.RecordKind.Raw));
        Assert.Contains(records, r => r.Text == "udp:packet 0");    // the packet before the first frame too
        Assert.Contains(records, r => r.Text == "map:Caucasus" && r.T == 0);
        Assert.Contains(records, r => r.Text == "message:hello" && Math.Abs(r.T - 0.2) < 1e-6);
    }

    [Fact]
    public void Recorder_Decimates_ToTheSampleRate()
    {
        var clock = new ManualClock();
        var source = new TestSource(clock);
        string? path = null;
        using (var recorder = new TelemetrySessionRecorder(source, "DCS", Config(sampleHz: 10), Manual))
        {
            recorder.SessionStarted += p => path = p;
            for (var i = 0; i < 1000; i++)
            {
                clock.Now = i / 100.0;                                 // 100 Hz for 10 s
                source.Raw("udp", "x");
                source.Raw("file:Journal", "line " + i);              // file streams are never decimated
                source.Frame(clock.Now, ("t.lat", i));
            }

            recorder.WaitIdle(TimeSpan.FromSeconds(5));
        }

        var info = TelemetrySessionStore.ReadInfo(path!);
        Assert.InRange(info.Frames, 99, 101);
        var records = ReadAll(path!, out var reader);
        reader.Dispose();
        Assert.InRange(records.Count(r => r.Text == "udp:x"), 99, 101);
        Assert.Equal(1000, records.Count(r => r.Text.StartsWith("file:Journal:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Recorder_EndsOnTimeout_OnSessionEnd_AndOnANewSession()
    {
        var clock = new ManualClock();
        var source = new TestSource(clock);
        var ended = new List<(string Path, string Reason)>();
        using var recorder = new TelemetrySessionRecorder(source, "DCS", Config(timeout: 5), Manual);
        recorder.SessionEnded += (p, r) => { lock (ended) ended.Add((p, r)); };

        source.Frame(0, ("t.lat", 1));
        clock.Now = 3;
        recorder.Tick();
        Assert.True(recorder.Recording);
        clock.Now = 6;
        recorder.Tick();                                              // 6 s without a frame
        Assert.False(recorder.Recording);

        clock.Now = 10;
        source.Frame(10, ("t.lat", 2));
        source.Event(TelemetryEventKinds.SessionEnd, "mission ended");
        Assert.False(recorder.Recording);

        clock.Now = 20;
        source.Frame(20, ("t.lat", 3));
        source.Event(TelemetryEventKinds.SessionStart);              // a new flight: the old one ends
        Assert.False(recorder.Recording);
        source.Frame(21, ("t.lat", 4));
        Assert.True(recorder.Recording);
        recorder.EndSession("game exited");
        Assert.True(recorder.WaitIdle(TimeSpan.FromSeconds(5)));
        lock (ended)
        {
            Assert.Equal(["no telemetry for 5 s", "mission ended", "a new session started", "game exited"], ended.Select(e => e.Reason));
            Assert.Equal(4, ended.Select(e => e.Path).Distinct().Count());
        }

        Assert.Equal(4, TelemetrySessionStore.List(_folder).Count);
        Assert.All(TelemetrySessionStore.List(_folder), s => Assert.True(s.Complete));
    }

    [Fact]
    public void Recorder_WithoutRaw_DoesNotListen()
    {
        var clock = new ManualClock();
        var source = new TestSource(clock);
        Assert.False(source.WantsRaw);
        using (new TelemetrySessionRecorder(source, "DCS", Config(raw: false), Manual))
            Assert.False(source.WantsRaw);
        using (new TelemetrySessionRecorder(source, "DCS", Config(raw: true), Manual))
            Assert.True(source.WantsRaw);
        Assert.False(source.WantsRaw);                              // detached after Dispose
    }

    [Fact]
    public void Recorder_FinishesASessionCutOffBefore()
    {
        Directory.CreateDirectory(_folder);
        var cut = TelemetrySessionTests.WriteFlight(_folder, "old.jptel", finish: false);
        var clock = new ManualClock();
        var source = new TestSource(clock);
        using (var recorder = new TelemetrySessionRecorder(source, "DCS", Config(), Manual))
        {
            source.Frame(0, ("t.lat", 1));
            recorder.WaitIdle(TimeSpan.FromSeconds(5));
        }

        Assert.True(TelemetrySessionStore.ReadInfo(cut).Complete);
    }

    [Fact]
    public void Recorder_NeverBlocksTheCaller()
    {
        // 100000 frames at once: the capture side only enqueues; the writer catches up in the background.
        var clock = new ManualClock();
        var source = new TestSource(clock);
        using var recorder = new TelemetrySessionRecorder(source, "DCS", Config(), Manual);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 100000; i++)
        {
            clock.Now = i * 0.001;
            source.Frame(clock.Now, ("t.lat", i), ("t.lon", 1), ("t.alt", 2));
        }

        var enqueue = watch.Elapsed;
        Assert.True(enqueue < TimeSpan.FromSeconds(5), "enqueue took " + enqueue);
        Assert.True(recorder.WaitIdle(TimeSpan.FromSeconds(60)));
        Assert.Equal(100000 - recorder.Dropped, recorder.CurrentFrames);
    }

    // ---------------------------------------------------------------- the hosts with fixture streams

    private static async Task<bool> Until(Func<bool> condition, double seconds = 5)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            if (condition())
                return true;
            await Task.Delay(20);
        }

        return condition();
    }

    [Fact]
    public async Task RecordHost_Dcs_RecordsTheUdpStream_AndExportsAcmi()
    {
        var config = Config();
        using var host = new TelemetryRecordHost("DCS", config, new StopwatchClock(), new TelemetryRecordHostOptions
        {
            IgnoreGameProcess = true,
            Feed = new TelemetryFeedOptions { Port = 0 },
            Recorder = Manual
        });
        var states = new List<string>();
        host.StatusChanged += s => { lock (states) states.Add(s.State); };
        host.Start();
        var feed = Assert.IsType<UdpTelemetryFeed>(host.Feed);
        using var udp = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, feed.Port);
        void Send(string text) => udp.Send(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetByteCount(text), target);
        Send("plane F-16C_50");
        for (var i = 0; i < 40; i++)
        {
            // The hook's line protocol, with the position keys of the recording hook.
            Send(DcsTelemetryProtocol.FormatFrame(i * 0.05, new Dictionary<string, double>
            {
                ["ias"] = 150, ["alt"] = 3000, ["g"] = 1 + i * 0.01, ["lat"] = 41.6 + i * 1e-5, ["lon"] = 41.6, ["alt_msl"] = 3000, ["heading"] = 1, ["pitch"] = 0.05, ["bank"] = 0.1
            }));
            await Task.Delay(5);
        }

        Assert.True(await Until(() => host.Recorder?.CurrentFrames >= 30), "frames " + host.Recorder?.CurrentFrames);
        Send("exit");
        Assert.True(await Until(() => host.Recorder is { CurrentPath: null, SessionsWritten: 1 }));
        lock (states)
            Assert.Contains("recording", states);
        host.Stop();

        var session = Assert.Single(TelemetrySessionStore.List(_folder));
        Assert.True(session.Complete);
        Assert.Equal("F-16C_50", session.Aircraft.FirstOrDefault());
        Assert.InRange(session.Frames, 30, 40);
        Assert.InRange(session.RawPackets, 31, 42);   // "plane", the frames and "exit" (DCS raw streams udp + udp:notice)
        if (session.HasPosition)
        {
            var acmi = Path.Combine(_folder, "dcs.txt.acmi");
            TelemetryAcmiExport.Export(session.Path, acmi, zip: false);
            TelemetrySessionTests.AssertValidAcmi(File.ReadAllText(acmi), out var objects);
            Assert.Contains(objects, l => l.Contains("Name=F-16C_50", StringComparison.Ordinal));
        }

        var csv = TelemetryCsvExport.Export(session.Path, Path.Combine(_folder, "csv"));
        Assert.True(csv.Rows >= 30);
        Assert.Contains("dcs.g", File.ReadAllLines(csv.Files[0])[0]);
    }

    [Fact]
    public async Task RecordHost_Il2_RecordsPackets_AndTheAircraft()
    {
        using var host = new TelemetryRecordHost("IL2Game", Config(), new StopwatchClock(), new TelemetryRecordHostOptions
        {
            IgnoreGameProcess = true,
            Feed = new TelemetryFeedOptions { Port = 0 },
            Recorder = Manual
        });
        host.Start();
        var feed = Assert.IsType<UdpTelemetryFeed>(host.Feed);
        using var udp = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, feed.Port);
        var focus = Il2PacketBuilder.BuildTelemetry(1, [], [((ushort)Il2EventId.SetFocus, Il2PacketBuilder.Str("Bf 109 G-14"))]);
        udp.Send(focus, focus.Length, target);
        for (uint i = 2; i < 40; i++)
        {
            var packet = Il2PacketBuilder.BuildTelemetry(i, [((ushort)Il2IndicatorId.Eas, [80 + i]), ((ushort)Il2IndicatorId.Agl, [500])], []);
            udp.Send(packet, packet.Length, target);
            var motion = Il2PacketBuilder.BuildMotion(i, 0.1f, 0.2f, 0.3f, 0, 0, 0, 0, 9.8f, 0);
            udp.Send(motion, motion.Length, target);
            await Task.Delay(5);
        }

        Assert.True(await Until(() => host.Recorder?.CurrentFrames >= 60));
        host.Stop();
        var session = Assert.Single(TelemetrySessionStore.List(_folder));
        Assert.Equal("IL2Game", session.Game);
        Assert.Contains("Bf 109 G-14", session.Aircraft);
        Assert.False(session.HasPosition);
        using var reader = new TelemetrySessionReader(session.Path);
        double eas = 0, yaw = double.NaN;
        while (reader.Next())
        {
            if (reader.Kind != TelemetrySessionReader.RecordKind.Frame)
                continue;
            var value = reader.Value(reader.ChannelIndex("il2.eas"));
            if (!double.IsNaN(value))
                eas = Math.Max(eas, value);
            if (!double.IsNaN(reader.Value(reader.ChannelIndex("il2.motion.yaw"))))
                yaw = reader.Value(reader.ChannelIndex("il2.motion.yaw"));
        }

        Assert.Equal(119, eas, 3);
        Assert.Equal(0.1, yaw, 5);
    }

    private sealed class StubWarThunder : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref Calls);
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/state" => "{\"valid\": true, \"IAS, km/h\": " + (300 + n) + ", \"H, m\": 1500, \"Ny\": 1.1}",
                "/indicators" => "{\"valid\": true, \"type\": \"spitfire_mk1\", \"speed\": 83.3}",
                _ => "{\"valid\": false}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task RecordHost_WarThunder_PollsOnlyWhileRecording()
    {
        var stub = new StubWarThunder();
        var config = Config();
        using var host = new TelemetryRecordHost("WarThunder", config, new StopwatchClock(), new TelemetryRecordHostOptions
        {
            IgnoreGameProcess = true,
            Feed = new TelemetryFeedOptions { WarThunderHandler = stub, WarThunderUri = new Uri("http://127.0.0.1:8111/"), WarThunderPollHz = 50 },
            Recorder = Manual
        });
        host.Start();
        Assert.True(await Until(() => host.Recorder?.CurrentFrames >= 10));
        var off = config.Clone();
        off.Enabled = false;
        host.Reload(off);
        Assert.Null(host.Recorder);
        Assert.Equal("off", host.Status.State);
        var calls = stub.Calls;
        await Task.Delay(300);
        Assert.Equal(calls, stub.Calls);                              // no requests while recording is off
        host.Stop();
        var session = Assert.Single(TelemetrySessionStore.List(_folder));
        Assert.Equal("recording switched off", session.EndReason);
        Assert.Contains("spitfire_mk1", session.Aircraft.Concat([session.Info("aircraft")]));
    }

    [Fact]
    public void RecordHost_WaitsForTheGame()
    {
        using var host = new TelemetryRecordHost("FalconBMS", Config(), new StopwatchClock(), new TelemetryRecordHostOptions
        {
            GamePid = int.MaxValue - 7,                               // no such process
            Feed = new TelemetryFeedOptions { BmsMemoryPrefix = "JoyProTest" + Guid.NewGuid().ToString("N") },
            Recorder = Manual
        });
        var exited = new ManualResetEventSlim();
        host.GameExited += () => exited.Set();
        try
        {
            host.Start();
        }
        catch (ArgumentException)
        {
            return;                                                   // no BMS reader in this build
        }

        Assert.True(exited.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal("waiting", host.Status.State);
        Assert.Null(host.Recorder);
    }

    [Fact]
    public async Task FfbHost_RecordsAlongWithForceFeedback()
    {
        var ffb = TelemetryFfbConfig.Parse("""{ "enabled": true }""");
        var clock = new StopwatchClock();
        using var host = new TelemetryFfbHost(ffb, TelemetryGame.Find("DCS")!, new MockFfbBackend(), clock, new TelemetryFfbHostOptions
        {
            IgnoreGameProcess = true,
            PortOverride = 0,
            RecordingConfig = Config(),
            RecorderOptions = Manual
        });
        var changes = 0;
        host.RecordingChanged += () => Interlocked.Increment(ref changes);
        host.Start();
        Assert.NotNull(host.Recorder);
        using var udp = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, host.Port);
        for (var i = 0; i < 30; i++)
        {
            var line = Encoding.UTF8.GetBytes(SyntheticFlight.DcsLine(SyntheticFlight.At(4 + i * 0.02)));
            udp.Send(line, line.Length, target);
            await Task.Delay(5);
        }

        Assert.True(await Until(() => host.Recorder?.CurrentFrames >= 20));
        var off = Config();
        off.Games["DCS"] = false;
        host.ReloadRecording(off);
        Assert.Null(host.Recorder);
        Assert.True(changes >= 1);
        host.Stop();
        var session = Assert.Single(TelemetrySessionStore.List(_folder));
        Assert.True(session.Complete);
        using var reader = new TelemetrySessionReader(session.Path);
        while (reader.Next())
        {
        }

        Assert.True(reader.ChannelIndex("dcs.ias") >= 0);
        Assert.Equal(-1, reader.ChannelIndex(AircraftTelemetry.Ias));   // the derived model is not stored
    }

    [Fact]
    public void Config_RoundTrips_AndDefaultsAreOff()
    {
        var fresh = TelemetryRecordingConfig.Parse("{}");
        Assert.False(fresh.Enabled);
        Assert.False(fresh.Records("DCS"));
        Assert.Equal(TelemetryRecordingConfig.DefaultSampleHz, fresh.SampleHz);
        Assert.True(fresh.KeepRaw);
        var config = Config(sampleHz: 50);
        config.Games["IL2Game"] = false;
        config.KeepDays = 30;
        var path = Path.Combine(_folder, "cfg", "telemetry-recording.json");
        config.Save(path);
        var loaded = TelemetryRecordingConfig.Load(path);
        Assert.True(loaded.Records("DCS"));
        Assert.False(loaded.Records("IL2Game"));
        Assert.Equal(50, loaded.SampleHz);
        Assert.Equal(30, loaded.KeepDays);
        Assert.Equal(_folder, loaded.ResolvedFolder());
        Assert.Equal(TelemetrySessionStore.DefaultRecordingsFolder(), new TelemetryRecordingConfig().ResolvedFolder());
        Assert.StartsWith(TestEnvironment.DataFolder, TelemetrySessionStore.DefaultRecordingsFolder());
    }
}
