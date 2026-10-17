namespace JoyPro.Ffb.Core.Telemetry;

/// <summary>Scripted telemetry for tests and the editor's preview: each key is a function of time.</summary>
/// <example>
/// <code>
/// var source = new SyntheticTelemetrySource(clock)
///     .Define(new TelemetryDescriptor("g", "G load", "G", -3, 9), t => 1 + 4 * Math.Sin(t));
/// source.Step(0.5);   // publishes one frame for t = 0.5
/// </code>
/// </example>
public sealed class SyntheticTelemetrySource : FrameTelemetrySource
{
    private readonly List<(TelemetryDescriptor Descriptor, Func<double, double> Value)> _channels = [];

    public SyntheticTelemetrySource(IFfbClock clock, string name = "Synthetic")
        : base(name, [], clock)
    {
    }

    public SyntheticTelemetrySource Define(TelemetryDescriptor descriptor, Func<double, double> value)
    {
        _channels.RemoveAll(c => c.Descriptor.Key == descriptor.Key);
        _channels.Add((descriptor, value));
        SetDescriptors(_channels.Select(c => c.Descriptor).ToArray());
        return this;
    }

    public SyntheticTelemetrySource Define(string key, Func<double, double> value, double min = -1, double max = 1) =>
        Define(new TelemetryDescriptor(key, key, "", min, max, "Synthetic"), value);

    /// <summary>Publishes the frame for script time <paramref name="time"/>.</summary>
    public TelemetryFrame Step(double time)
    {
        var frame = new TelemetryFrame(
            time,
            _channels.Select(c => new KeyValuePair<string, double>(c.Descriptor.Key, c.Value(time))));
        Publish(frame);
        return frame;
    }
}
