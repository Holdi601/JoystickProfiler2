namespace JoyPro.Ffb.Core.Output;

/// <summary>The effect types a sink node produces. They map 1:1 onto DirectInput condition/constant/periodic effects.</summary>
public enum FfbEffectType
{
    ConstantForce,
    Spring,
    Damper,
    Friction,
    Periodic
}

public enum FfbAxis
{
    X = 0,
    Y = 1
}

public enum FfbWaveform
{
    Sine,
    Square,
    Triangle,
    SawUp,
    SawDown
}

/// <summary>One effect on one device axis for one tick.</summary>
/// <remarks>
/// Units, all normalised to the device's full force:
///   ConstantForce: Magnitude -1..1 (sign = direction).
///   Spring: Centre -1..1 (stick position), Coefficient 0..1 (force per unit of deflection), Saturation 0..1 (force cap).
///   Damper/Friction: Coefficient 0..1.
///   Periodic: Magnitude 0..1, FrequencyHz, Waveform.
/// Duration is finite on purpose: the runtime refreshes every effect every tick, so a hung loop or crashed process
/// leaves the stick silent after at most Duration (hardware backends must download it as the DirectInput duration).
/// Tag is the effect name of the sink ("buffet", ...) for the per-game switches; Slot (0..3) separates effects of
/// the same type on one axis (same slot: the strongest wins).
/// </remarks>
public readonly record struct FfbEffectCommand(
    string Device,
    FfbAxis Axis,
    FfbEffectType Type,
    double Magnitude = 0,
    double Centre = 0,
    double Coefficient = 0,
    double Saturation = 1,
    double FrequencyHz = 0,
    FfbWaveform Waveform = FfbWaveform.Sine,
    double DurationSeconds = 0,
    string SourceNodeId = "",
    string Tag = "",
    int Slot = 0)
{
    /// <summary>The strongest force this effect can produce, used for the gain cap and for "is it silent".</summary>
    public double PeakForce => Type switch
    {
        FfbEffectType.ConstantForce or FfbEffectType.Periodic => Math.Abs(Magnitude),
        FfbEffectType.Spring => Math.Min(Math.Abs(Coefficient) * 2, Math.Abs(Saturation)),
        _ => Math.Abs(Coefficient)
    };
}

public static class FfbWaveforms
{
    /// <summary>Value of a waveform at a phase in cycles (0..1 repeats), -1..1.</summary>
    public static double Sample(FfbWaveform waveform, double phaseCycles)
    {
        var p = phaseCycles - Math.Floor(phaseCycles);
        return waveform switch
        {
            FfbWaveform.Sine => Math.Sin(2 * Math.PI * p),
            FfbWaveform.Square => p < 0.5 ? 1 : -1,
            FfbWaveform.Triangle => p < 0.25 ? 4 * p : p < 0.75 ? 2 - 4 * p : 4 * p - 4,
            FfbWaveform.SawUp => 2 * p - 1,
            FfbWaveform.SawDown => 1 - 2 * p,
            _ => 0
        };
    }
}
