using System.Globalization;
using System.Text;
using JoyPro.Ffb.Core.Output;

namespace JoyPro.Ffb;

/// <summary>The "dry" sink: accepts every effect, moves nothing, and can record what it was told (JSON lines).</summary>
/// <remarks>
/// Record format, one object per line:
/// <code>
/// {"t":1.234,"effects":[{"axis":"Y","type":"Periodic","tag":"buffet","slot":0,"magnitude":0.3,"frequency":12,
///   "coefficient":0,"centre":0,"saturation":1,"duration":0.1}]}
/// {"t":2.5,"stopAll":true}
/// </code>
/// The smoke test reads it to check effect amplitudes, timeouts and the force cap without hardware.
/// </remarks>
public sealed class DryFfbBackend : IFfbBackend
{
    private readonly StreamWriter? _writer;
    private readonly StringBuilder _line = new(512);
    private readonly JoyPro.Ffb.Core.Telemetry.IFfbClock? _clock;
    private double _lastTime;

    public DryFfbBackend(string? recordPath, JoyPro.Ffb.Core.Telemetry.IFfbClock? clock = null)
    {
        _clock = clock;
        if (!string.IsNullOrWhiteSpace(recordPath))
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(recordPath));
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            _writer = new StreamWriter(recordPath, append: false, new UTF8Encoding(false)) { AutoFlush = false };
        }
    }

    public string Name => "Dry (no output)";

    public IReadOnlyList<FfbDeviceCapabilities> Devices { get; } =
        [new FfbDeviceCapabilities("dry", "Dry sink", FfbDeviceCapabilities.BothAxes, FfbDeviceCapabilities.AllEffects, 1000)];

    public long ApplyCount { get; private set; }
    public long StopAllCount { get; private set; }

    public void Apply(double time, IReadOnlyList<FfbEffectCommand> commands)
    {
        ApplyCount++;
        _lastTime = time;
        if (_writer is null)
            return;
        _line.Clear();
        _line.Append("{\"t\":").Append(Num(time)).Append(",\"effects\":[");
        for (var i = 0; i < commands.Count; i++)
        {
            var c = commands[i];
            if (i > 0)
                _line.Append(',');
            _line.Append("{\"axis\":\"").Append(c.Axis).Append("\",\"type\":\"").Append(c.Type)
                .Append("\",\"tag\":\"").Append(c.Tag.Replace("\"", "'", StringComparison.Ordinal)).Append("\",\"slot\":").Append(c.Slot.ToString(CultureInfo.InvariantCulture))
                .Append(",\"magnitude\":").Append(Num(c.Magnitude))
                .Append(",\"frequency\":").Append(Num(c.FrequencyHz))
                .Append(",\"coefficient\":").Append(Num(c.Coefficient))
                .Append(",\"centre\":").Append(Num(c.Centre))
                .Append(",\"saturation\":").Append(Num(c.Saturation))
                .Append(",\"peak\":").Append(Num(c.PeakForce))
                .Append(",\"duration\":").Append(Num(c.DurationSeconds)).Append('}');
        }

        _line.Append("]}");
        _writer.WriteLine(_line);
        if (ApplyCount % 50 == 0)
            _writer.Flush();
    }

    public void StopAll()
    {
        StopAllCount++;
        if (_clock is not null)
            _lastTime = _clock.Now;
        if (_writer is null)
            return;
        _writer.WriteLine("{\"t\":" + Num(_lastTime) + ",\"stopAll\":true}");
        _writer.Flush();
    }

    public void Dispose()
    {
        _writer?.Flush();
        _writer?.Dispose();
    }

    private static string Num(double value) => (double.IsFinite(value) ? value : 0).ToString("0.#####", CultureInfo.InvariantCulture);
}
