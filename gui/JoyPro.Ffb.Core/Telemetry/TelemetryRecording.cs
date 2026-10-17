using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JoyPro.Ffb.Core.Telemetry;

/// <summary>A recorded telemetry session: the source's catalogue and its frames with recording times.</summary>
/// <remarks>
/// File format (*.ffbrec.jsonl), one JSON object per line:
/// <code>
/// {"format":"joypro-ffb-telemetry","version":1,"source":"IL-2","created":"2026-10-05T12:00:00Z","descriptors":[{"key":"il2.eas","title":"EAS","unit":"m/s","min":0,"max":250,"category":"Flight","origin":"..."}]}
/// {"t":0,"v":{"il2.eas":81.25,"il2.acc.g":1.02}}
/// {"t":0.04,"v":{"il2.eas":81.31,"il2.acc.g":1.05}}
/// </code>
/// "t" is seconds since the recording started (receive time, so a replay reproduces the real packet timing and its
/// gaps); "v" holds the frame's values. Non-finite values are not written. Line-based so a crash loses at most the
/// last line and a recording can be inspected or cut with a text editor.
/// </remarks>
public sealed class TelemetryRecording
{
    public const string Format = "joypro-ffb-telemetry";
    public const int CurrentVersion = 1;

    public string Source { get; init; } = "";
    public IReadOnlyList<TelemetryDescriptor> Descriptors { get; init; } = [];
    public List<TelemetryFrame> Frames { get; } = [];

    public double Duration => Frames.Count == 0 ? 0 : Frames[^1].Time - Frames[0].Time;

    public static TelemetryRecording Load(string path)
    {
        using var reader = new StreamReader(path);
        return Read(reader);
    }

    public static TelemetryRecording Read(TextReader reader)
    {
        var header = reader.ReadLine();
        if (header is null)
            throw new InvalidDataException("The recording is empty.");

        JsonObject head;
        try
        {
            head = JsonNode.Parse(header) as JsonObject ?? throw new InvalidDataException("Bad recording header.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Bad recording header: " + ex.Message, ex);
        }

        if (head["format"]?.GetValue<string>() != Format)
            throw new InvalidDataException("Not a JoyPro telemetry recording.");
        var version = head["version"]?.GetValue<int>() ?? 0;
        if (version is < 1 or > CurrentVersion)
            throw new InvalidDataException($"Unsupported recording version {version}.");

        var descriptors = new List<TelemetryDescriptor>();
        if (head["descriptors"] is JsonArray list)
        {
            foreach (var d in list.OfType<JsonObject>())
            {
                descriptors.Add(new TelemetryDescriptor(
                    d["key"]?.GetValue<string>() ?? "",
                    d["title"]?.GetValue<string>() ?? "",
                    d["unit"]?.GetValue<string>() ?? "",
                    d["min"]?.GetValue<double>() ?? 0,
                    d["max"]?.GetValue<double>() ?? 1,
                    d["category"]?.GetValue<string>() ?? "",
                    d["origin"]?.GetValue<string>() ?? ""));
            }
        }

        var recording = new TelemetryRecording
        {
            Source = head["source"]?.GetValue<string>() ?? "",
            Descriptors = descriptors
        };

        string? line;
        var lineNumber = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (line.Length == 0)
                continue;
            JsonObject item;
            try
            {
                item = JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException();
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // A recording cut off by a crash ends with a half line: keep everything before it.
                if (reader.Peek() < 0)
                    break;
                throw new InvalidDataException($"Bad frame on line {lineNumber}.", ex);
            }

            var time = item["t"]?.GetValue<double>() ?? 0;
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            if (item["v"] is JsonObject v)
            {
                foreach (var pair in v)
                {
                    if (pair.Value is JsonValue value && value.TryGetValue<double>(out var number))
                        values[pair.Key] = number;
                }
            }

            recording.Frames.Add(new TelemetryFrame(time, values));
        }

        return recording;
    }
}

/// <summary>Writes frames to a recording as they arrive (attach it to any <see cref="FrameTelemetrySource"/>).</summary>
public sealed class TelemetryRecorder : IDisposable
{
    private readonly TextWriter _writer;
    private readonly IFfbClock? _clock;
    private readonly object _gate = new();
    private readonly List<FrameTelemetrySource> _attached = [];
    private double? _start;
    private bool _disposed;

    /// <param name="clock">With a clock, "t" is the receive time since the first frame; without, the frame's own time.</param>
    public TelemetryRecorder(TextWriter writer, string source, IReadOnlyList<TelemetryDescriptor> descriptors, IFfbClock? clock = null)
    {
        _writer = writer;
        _clock = clock;
        var list = new JsonArray();
        foreach (var d in descriptors)
        {
            list.Add(new JsonObject
            {
                ["key"] = d.Key,
                ["title"] = d.Title,
                ["unit"] = d.Unit,
                ["min"] = d.Min,
                ["max"] = d.Max,
                ["category"] = d.Category,
                ["origin"] = d.Origin
            });
        }

        var header = new JsonObject
        {
            ["format"] = TelemetryRecording.Format,
            ["version"] = TelemetryRecording.CurrentVersion,
            ["source"] = source,
            ["created"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["descriptors"] = list
        };
        _writer.WriteLine(header.ToJsonString());
    }

    public static TelemetryRecorder Create(string path, ITelemetrySource source, IFfbClock? clock = null) =>
        new(new StreamWriter(path, false), source.Name, source.Descriptors, clock);

    public int FramesWritten { get; private set; }

    public void Attach(FrameTelemetrySource source)
    {
        source.FrameReceived += Write;
        lock (_gate)
            _attached.Add(source);
    }

    public void Write(TelemetryFrame frame)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            double time;
            if (_clock is not null)
            {
                var now = _clock.Now;
                _start ??= now;
                time = now - _start.Value;
            }
            else
            {
                time = frame.Time;
            }

            var values = new JsonObject();
            foreach (var pair in frame.Values)
            {
                if (double.IsFinite(pair.Value))
                    values[pair.Key] = pair.Value;
            }

            var line = new JsonObject { ["t"] = Math.Round(time, 6), ["v"] = values };
            _writer.WriteLine(line.ToJsonString());
            FramesWritten++;
        }
    }

    public void Flush()
    {
        lock (_gate)
            _writer.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var source in _attached)
                source.FrameReceived -= Write;
            _writer.Dispose();
        }
    }
}

/// <summary>Plays a recording back as a live source.</summary>
/// <remarks>
/// Tests call <see cref="StepTo"/> with recording times (deterministic, no threads). <see cref="PlayAsync"/> plays at
/// the recorded timing on the clock, so gaps in the recording reach the runtime as stale telemetry, exactly as live.
/// </remarks>
public sealed class ReplayTelemetrySource : FrameTelemetrySource
{
    private readonly TelemetryRecording _recording;
    private readonly double _origin;
    private int _next;

    public ReplayTelemetrySource(TelemetryRecording recording, IFfbClock clock)
        : base("Replay: " + recording.Source, recording.Descriptors, clock)
    {
        _recording = recording;
        _origin = recording.Frames.Count == 0 ? 0 : recording.Frames[0].Time;
    }

    public bool Finished => _next >= _recording.Frames.Count;

    /// <summary>Recording time (since the first frame) of the next frame, or null at the end.</summary>
    public double? NextFrameTime => Finished ? null : _recording.Frames[_next].Time - _origin;

    /// <summary>Publishes every frame up to recording time <paramref name="time"/> (seconds since the first frame).</summary>
    /// <returns>The number of frames published.</returns>
    public int StepTo(double time)
    {
        var published = 0;
        while (_next < _recording.Frames.Count && _recording.Frames[_next].Time - _origin <= time + 1e-9)
        {
            Publish(_recording.Frames[_next]);
            _next++;
            published++;
        }

        return published;
    }

    public void Rewind()
    {
        _next = 0;
        Clear();
    }

    /// <summary>Plays the recording at its recorded timing (speed 2 = twice as fast) until the end or cancellation.</summary>
    public async Task PlayAsync(double speed = 1, bool loop = false, CancellationToken cancellation = default)
    {
        speed = speed > 0 ? speed : 1;
        do
        {
            Rewind();
            var start = Clock.Now;
            while (!Finished)
            {
                cancellation.ThrowIfCancellationRequested();
                StepTo((Clock.Now - start) * speed);
                await Task.Delay(1, cancellation).ConfigureAwait(false);
            }
        } while (loop && !cancellation.IsCancellationRequested);
    }
}
