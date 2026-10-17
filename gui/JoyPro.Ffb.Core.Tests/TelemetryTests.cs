using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Ffb.Core.Telemetry.Dcs;

namespace JoyPro.Ffb.Core.Tests;

public class TelemetryTests
{
    /// <summary>Records a scripted source at 50 Hz for <paramref name="seconds"/>, with an optional gap.</summary>
    internal static TelemetryRecording Record(
        Func<double, double> g,
        double seconds,
        Func<double, double>? shells = null,
        (double From, double To)? gap = null)
    {
        var clock = new ManualClock();
        var source = new SyntheticTelemetrySource(clock, "Synthetic flight")
            .Define(new TelemetryDescriptor("il2.acc.g", "G load", "G", 0, 9, "Flight"), g);
        if (shells is not null)
            source.Define(new TelemetryDescriptor("dcs.shells", "Shells", "rounds", 0, 2000, "Weapons"), shells);

        var writer = new StringWriter();
        using (var recorder = new TelemetryRecorder(writer, source.Name, source.Descriptors, clock))
        {
            recorder.Attach(source);
            for (var i = 0; i <= (int)Math.Round(seconds * 50); i++)
            {
                var t = i / 50.0;
                clock.Now = 100 + t;      // receive time on the runtime clock; the recording starts at 0
                if (gap is { } window && t > window.From && t < window.To)
                    continue;
                source.Step(t);
            }

            Assert.True(recorder.FramesWritten > 0);
        }

        return TelemetryRecording.Read(new StringReader(writer.ToString()));
    }

    [Fact]
    public void Recorder_And_Replayer_RoundTrip()
    {
        var recording = Record(t => 1 + t, 1.0, shells: t => 200 - Math.Floor(t * 10));
        Assert.Equal("Synthetic flight", recording.Source);
        Assert.Equal(2, recording.Descriptors.Count);
        Assert.Equal(51, recording.Frames.Count);
        Assert.Equal(1.0, recording.Duration, 6);
        Assert.Equal(0.0, recording.Frames[0].Time);
        Assert.Equal(0.5, recording.Frames[25].Time, 6);
        Assert.True(recording.Frames[25].TryGet("il2.acc.g", out var g));
        Assert.Equal(1.5, g, 9);
        Assert.True(recording.Frames[25].TryGet("dcs.shells", out var shells));
        Assert.Equal(195, shells);

        var clock = new ManualClock();
        var replay = new ReplayTelemetrySource(recording, clock);
        Assert.Null(replay.Latest);
        Assert.Equal(1, replay.StepTo(0));
        Assert.Equal(0, replay.StepTo(0.019));
        clock.Now = 3;
        Assert.Equal(25, replay.StepTo(0.5));
        Assert.True(replay.TryGet("il2.acc.g", out var replayed));
        Assert.Equal(1.5, replayed, 9);
        Assert.Equal(3, replay.LastReceiveTime);
        Assert.Equal(0.52, replay.NextFrameTime!.Value, 6);
        replay.StepTo(10);
        Assert.True(replay.Finished);
        replay.Rewind();
        Assert.Null(replay.Latest);
    }

    [Fact]
    public void Recording_KeepsGapsAndSurvivesACutOffLastLine()
    {
        var recording = Record(_ => 1, 1.0, gap: (0.4, 0.8));
        var times = recording.Frames.Select(f => f.Time).ToList();
        Assert.Contains(times.Zip(times.Skip(1)), pair => pair.Second - pair.First > 0.39);

        var writer = new StringWriter();
        using (var recorder = new TelemetryRecorder(writer, "x", []))
        {
            recorder.Write(new TelemetryFrame(0, new Dictionary<string, double> { ["a"] = 1, ["nan"] = double.NaN }));
            recorder.Write(new TelemetryFrame(0.02, new Dictionary<string, double> { ["a"] = 2 }));
        }

        var text = writer.ToString();
        var cut = text[..(text.Length - 8)];
        var read = TelemetryRecording.Read(new StringReader(cut));
        Assert.Single(read.Frames);
        Assert.False(read.Frames[0].TryGet("nan", out _));
        Assert.Throws<InvalidDataException>(() => TelemetryRecording.Read(new StringReader("{\"format\":\"other\"}")));
    }

    [Fact]
    public async Task Replay_PlaysAtRecordedTiming()
    {
        var recording = Record(t => t, 0.3);
        var clock = new StopwatchClock();
        var replay = new ReplayTelemetrySource(recording, clock);
        var frames = 0;
        replay.FrameReceived += _ => frames++;
        var started = clock.Now;
        await replay.PlayAsync(speed: 1);
        var elapsed = clock.Now - started;
        Assert.Equal(recording.Frames.Count, frames);
        Assert.InRange(elapsed, 0.28, 5);
    }

    [Fact]
    public void Synthetic_PublishesScriptedValues()
    {
        var clock = new ManualClock(7);
        var source = new SyntheticTelemetrySource(clock).Define("x", t => t * 2);
        Assert.False(source.TryGet("x", out _));
        Assert.Null(source.LastReceiveTime);
        source.Step(1.5);
        Assert.True(source.TryGet("x", out var x));
        Assert.Equal(3, x);
        Assert.Equal(7, source.LastReceiveTime);
        Assert.Single(source.Descriptors);
    }

    [Fact]
    public void Dcs_LineProtocolParsesKnownAndUnknownKeys()
    {
        Assert.True(DcsTelemetryProtocol.TryParseFrame("t=12.5;ias=154.2;aoa=0.081;g=3.42;shells=412;newthing=1e3;bad=abc;Up=1", out var frame));
        Assert.Equal(12.5, frame!.Time);
        Assert.Equal(154.2, frame.Values["dcs.ias"]);
        Assert.Equal(3.42, frame.Values["dcs.g"]);
        Assert.Equal(412, frame.Values["dcs.shells"]);
        Assert.Equal(1000, frame.Values["dcs.newthing"]);
        Assert.False(frame.Values.ContainsKey("dcs.bad"));
        Assert.False(frame.Values.ContainsKey("dcs.Up"));

        Assert.False(DcsTelemetryProtocol.TryParseFrame("plane F-16C_50", out _));
        Assert.False(DcsTelemetryProtocol.TryParseFrame("ias=100", out _));   // no time

        var line = DcsTelemetryProtocol.FormatFrame(1.25, [new("dcs.g", 2.5), new("ias", 100)]);
        Assert.Equal("t=1.25;g=2.5;ias=100", line);

        Assert.Contains(DcsTelemetryProtocol.Channels, c => c.WireKey == "g" && c.LoGet == "LoGetAccelerationUnits().y");
        Assert.All(DcsTelemetryProtocol.Descriptors, d => Assert.StartsWith("dcs.", d.Key));
    }

    [Fact]
    public void Dcs_SourceFollowsPlaneAndExitLines()
    {
        var clock = new ManualClock(1);
        var source = new DcsTelemetrySource(clock);
        source.Ingest("plane FA-18C_hornet\nt=1;g=1.1\n");
        Assert.Equal("FA-18C_hornet", source.Aircraft);
        Assert.True(source.TryGet("dcs.g", out var g));
        Assert.Equal(1.1, g);
        Assert.Equal(1, source.FramesAccepted);

        source.Ingest("t=1.02;g=1.3"u8);
        Assert.True(source.TryGet("dcs.g", out g));
        Assert.Equal(1.3, g);

        source.Ingest("exit");
        Assert.Null(source.Aircraft);
        Assert.Null(source.Latest);
    }
}
