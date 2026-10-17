using System.Globalization;

namespace JoyPro.Ffb.Core.Output;

/// <summary>What one force feedback device can do.</summary>
/// <param name="MaxUpdateRateHz">How often the backend may be sent new parameters (USB FFB sticks: ~100-500 Hz).</param>
public sealed record FfbDeviceCapabilities(
    string DeviceId,
    string Name,
    IReadOnlyList<FfbAxis> Axes,
    IReadOnlyCollection<FfbEffectType> SupportedEffects,
    double MaxUpdateRateHz)
{
    public static readonly IReadOnlyCollection<FfbEffectType> AllEffects = Enum.GetValues<FfbEffectType>();
    public static readonly IReadOnlyList<FfbAxis> BothAxes = [FfbAxis.X, FfbAxis.Y];

    public bool Supports(FfbEffectCommand command) =>
        Axes.Contains(command.Axis) && SupportedEffects.Contains(command.Type);
}

/// <summary>Where forces go: a DirectInput device later, the <see cref="MockFfbBackend"/> now.</summary>
/// <remarks>
/// Contract for implementations:
///  - <see cref="Apply"/> receives the complete set of effects for a tick; an effect that is not in the list stops.
///  - Every command has a finite duration; the device must stop it on its own when no refresh arrives in time.
///  - <see cref="StopAll"/> silences every device at once and must never throw.
/// </remarks>
public interface IFfbBackend : IDisposable
{
    string Name { get; }
    IReadOnlyList<FfbDeviceCapabilities> Devices { get; }

    void Apply(double time, IReadOnlyList<FfbEffectCommand> commands);

    void StopAll();
}

/// <summary>Per-device output limits.</summary>
/// <param name="Gain">Multiplier for every force of the device (the user's strength slider).</param>
/// <param name="GainCap">The strongest combined force per axis, 0..1, after the gain. A hard safety limit.</param>
public sealed record FfbDeviceSettings(double Gain = 1, double GainCap = FfbDeviceSettings.DefaultCap)
{
    public const double DefaultCap = 0.8;
    public static readonly FfbDeviceSettings Default = new();
}

/// <summary>Turns the sinks' commands into what a device gets: merged, gained, capped, with a duration.</summary>
public static class FfbMixer
{
    /// <remarks>
    /// Per effect slot (device, axis, effect type, <see cref="FfbEffectCommand.Slot"/>) the strongest command wins,
    /// as several outputs on one actuator in the Forza graph (ties: the first). Effects on different slots stay
    /// separate and add up on the device. Then the device gain is applied and all ranges are clamped. Finally the gain
    /// cap: when the effects of one axis could together push harder than the cap (sum of
    /// <see cref="FfbEffectCommand.PeakForce"/>), all of them are scaled down by the same factor, so even the worst
    /// case stays within the cap.
    /// </remarks>
    public static List<FfbEffectCommand> Mix(
        IEnumerable<FfbEffectCommand> commands,
        Func<string, FfbDeviceSettings> settings,
        double durationSeconds)
    {
        var merged = new List<FfbEffectCommand>();
        foreach (var group in commands.GroupBy(c => (c.Device, c.Axis, c.Type, c.Slot)))
        {
            var strongest = default(FfbEffectCommand);
            var first = true;
            foreach (var c in group)
            {
                if (first || c.PeakForce > strongest.PeakForce)
                    strongest = c;
                first = false;
            }

            merged.Add(strongest);
        }

        var result = new List<FfbEffectCommand>(merged.Count);
        foreach (var axis in merged.GroupBy(c => (c.Device, c.Axis)))
        {
            var device = settings(axis.Key.Device);
            var gain = Math.Max(0, device.Gain);
            var cap = Math.Clamp(device.GainCap, 0, 1);
            var effects = axis.Select(c => Clamp(Scale(c, gain)) with { DurationSeconds = durationSeconds }).ToList();
            var peak = effects.Sum(c => c.PeakForce);
            var factor = peak > cap ? (peak > 0 ? cap / peak : 0) : 1;
            foreach (var effect in effects)
                result.Add(factor < 1 ? Scale(effect, factor) : effect);
        }

        return result;
    }

    /// <summary>The same effect with its force scaled (magnitude, coefficient, spring saturation).</summary>
    public static FfbEffectCommand Scale(FfbEffectCommand c, double factor) => c with
    {
        Magnitude = c.Magnitude * factor,
        Coefficient = c.Coefficient * factor,
        Saturation = c.Type == FfbEffectType.Spring ? c.Saturation * factor : c.Saturation
    };

    private static FfbEffectCommand Clamp(FfbEffectCommand c) => c with
    {
        Magnitude = c.Type == FfbEffectType.Periodic ? Math.Clamp(c.Magnitude, 0, 1) : Math.Clamp(c.Magnitude, -1, 1),
        Centre = Math.Clamp(c.Centre, -1, 1),
        Coefficient = Math.Clamp(c.Coefficient, 0, 1),
        Saturation = Math.Clamp(c.Saturation, 0, 1),
        FrequencyHz = Math.Clamp(c.FrequencyHz, 0, 500)
    };
}

/// <summary>One recorded output of the mock: the resulting force on one axis at one moment.</summary>
public readonly record struct MockFfbSample(double Time, string Device, FfbAxis Axis, double Force, int Effects);

/// <summary>A pretend force feedback device that records what it was told and the force it would produce.</summary>
/// <remarks>
/// The stick position and speed per axis are simulated values tests can set (springs and dampers need them).
/// Force model, all normalised: constant = magnitude; spring = clamp(-coefficient * (position - centre), +-saturation);
/// damper = -coefficient * velocity; friction = -coefficient * sign(velocity); periodic = magnitude * wave(f * t);
/// the sum is clamped to -1..1. Effects expire after their duration like on a real device.
/// </remarks>
public sealed class MockFfbBackend : IFfbBackend
{
    private readonly object _gate = new();
    private readonly List<MockFfbSample> _samples = [];
    private readonly List<double> _stopAllTimes = [];
    private readonly Dictionary<(string, FfbAxis), (double Position, double Velocity)> _stick = new();
    private List<FfbEffectCommand> _active = [];
    private double _activeSince;
    private double _lastTime;

    public MockFfbBackend(params FfbDeviceCapabilities[] devices)
    {
        Devices = devices.Length > 0
            ? devices
            : [new FfbDeviceCapabilities("mock", "Mock FFB stick", FfbDeviceCapabilities.BothAxes, FfbDeviceCapabilities.AllEffects, 1000)];
    }

    public string Name => "Mock";
    public IReadOnlyList<FfbDeviceCapabilities> Devices { get; }

    /// <summary>When set, STOP ALL is stamped with this clock's time (otherwise with the last Apply's time).</summary>
    public JoyPro.Ffb.Core.Telemetry.IFfbClock? Clock { get; set; }

    /// <summary>Throw from <see cref="Apply"/> (to test the runtime's STOP ALL on errors).</summary>
    public bool FailOnApply { get; set; }

    public int ApplyCount { get; private set; }
    public int StopAllCount { get { lock (_gate) return _stopAllTimes.Count; } }
    public IReadOnlyList<double> StopAllTimes { get { lock (_gate) return _stopAllTimes.ToArray(); } }
    public IReadOnlyList<MockFfbSample> Samples { get { lock (_gate) return _samples.ToArray(); } }
    public IReadOnlyList<FfbEffectCommand> ActiveEffects { get { lock (_gate) return _active.ToArray(); } }

    /// <summary>All commands of the last Apply, as sent.</summary>
    public IReadOnlyList<FfbEffectCommand> LastCommands { get { lock (_gate) return _active.ToArray(); } }

    public void SetStick(string device, FfbAxis axis, double position, double velocity = 0)
    {
        lock (_gate)
            _stick[(device, axis)] = (position, velocity);
    }

    public void Apply(double time, IReadOnlyList<FfbEffectCommand> commands)
    {
        lock (_gate)
        {
            if (FailOnApply)
                throw new InvalidOperationException("Mock backend failure (test).");
            foreach (var c in commands)
            {
                if (!(c.DurationSeconds > 0) || !double.IsFinite(c.DurationSeconds))
                    throw new ArgumentException("Effects must have a finite, positive duration.", nameof(commands));
            }

            ApplyCount++;
            _active = commands.ToList();
            _activeSince = time;
            _lastTime = time;
            foreach (var device in Devices)
            {
                foreach (var axis in device.Axes)
                    _samples.Add(new MockFfbSample(time, device.DeviceId, axis, ForceLocked(device.DeviceId, axis, time), CountLocked(device.DeviceId, axis)));
            }
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            if (Clock is not null)
                _lastTime = Clock.Now;
            _stopAllTimes.Add(_lastTime);
            _active = [];
            foreach (var device in Devices)
            {
                foreach (var axis in device.Axes)
                    _samples.Add(new MockFfbSample(_lastTime, device.DeviceId, axis, 0, 0));
            }
        }
    }

    /// <summary>The force an axis would feel at <paramref name="time"/> (0 once the effects' durations ran out).</summary>
    public double ForceAt(string device, FfbAxis axis, double time)
    {
        lock (_gate)
            return ForceLocked(device, axis, time);
    }

    public IReadOnlyList<MockFfbSample> SamplesFor(string device, FfbAxis axis)
    {
        lock (_gate)
            return _samples.Where(s => s.Device == device && s.Axis == axis).ToArray();
    }

    /// <summary>Writes "time,device,axis,force,effects" lines for plotting.</summary>
    public void WriteCsv(TextWriter writer)
    {
        writer.WriteLine("time,device,axis,force,effects");
        foreach (var s in Samples)
        {
            writer.WriteLine(string.Join(
                ",",
                s.Time.ToString("0.0000", CultureInfo.InvariantCulture),
                s.Device,
                s.Axis.ToString(),
                s.Force.ToString("0.0000", CultureInfo.InvariantCulture),
                s.Effects.ToString(CultureInfo.InvariantCulture)));
        }
    }

    public void Dispose() => StopAll();

    private int CountLocked(string device, FfbAxis axis) => _active.Count(c => c.Device == device && c.Axis == axis);

    private double ForceLocked(string device, FfbAxis axis, double time)
    {
        var (position, velocity) = _stick.GetValueOrDefault((device, axis));
        var force = 0.0;
        foreach (var c in _active)
        {
            if (c.Device != device || c.Axis != axis || time > _activeSince + c.DurationSeconds + 1e-9)
                continue;
            force += c.Type switch
            {
                FfbEffectType.ConstantForce => c.Magnitude,
                FfbEffectType.Spring => Math.Clamp(-c.Coefficient * (position - c.Centre), -c.Saturation, c.Saturation),
                FfbEffectType.Damper => Math.Clamp(-c.Coefficient * velocity, -1, 1),
                FfbEffectType.Friction => -c.Coefficient * Math.Sign(velocity),
                FfbEffectType.Periodic => c.Magnitude * FfbWaveforms.Sample(c.Waveform, c.FrequencyHz * time),
                _ => 0
            };
        }

        return Math.Clamp(force, -1, 1);
    }
}
