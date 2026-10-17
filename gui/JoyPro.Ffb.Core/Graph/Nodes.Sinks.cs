using System.Text.Json.Nodes;
using JoyPro.Ffb.Core.Output;

namespace JoyPro.Ffb.Core.Graph;

/// <summary>Which stick axes a sink drives.</summary>
public enum FfbAxisSelection
{
    X,
    Y,
    Both
}

/// <summary>What the input of a periodic effect changes (Forza: HapticModulationMode).</summary>
public enum FfbModulation
{
    StrengthOnly,
    FrequencyOnly,
    StrengthAndFrequency
}

/// <summary>Base for the sinks that act on one or both axes with the same parameters.</summary>
public abstract class AxisEffectSinkNode : EffectSinkNode
{
    public FfbAxisSelection Axes { get; set; } = FfbAxisSelection.Both;

    public override void Emit(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, List<FfbEffectCommand> commands)
    {
        if (!TryBuild(context, inputs, out var effect))
            return;
        if (Axes != FfbAxisSelection.Y)
            commands.Add(effect with { Axis = FfbAxis.X });
        if (Axes != FfbAxisSelection.X)
            commands.Add(effect with { Axis = FfbAxis.Y });
    }

    /// <summary>The effect for this tick (the axis is filled in by <see cref="Emit"/>); false = silent.</summary>
    protected abstract bool TryBuild(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, out FfbEffectCommand effect);

    internal override void SaveSinkParams(JsonObject json)
    {
        json["axes"] = Axes.ToString();
        SaveEffectParams(json);
    }

    internal override void LoadSinkParams(JsonObject json)
    {
        Axes = json.Enum("axes", FfbAxisSelection.Both);
        LoadEffectParams(json);
    }

    internal abstract void SaveEffectParams(JsonObject json);

    internal abstract void LoadEffectParams(JsonObject json);

    protected static double Unit(double value) => Math.Clamp(value, 0, 1);
}

/// <summary>A push along one axis: strength from the input (G load pulling the stick, a recoil kick).</summary>
/// <remarks>
/// Strength = MinStrength..MaxStrength over input 0..1, pushing to the positive end of <see cref="Axis"/> (right on X,
/// towards the pilot on Y) unless <see cref="Negative"/> (the editor shows it as "Invert direction"). <see cref="Bipolar"/> reads a signed input (-1..1): the sign picks the direction.
/// </remarks>
public sealed class ConstantForceNode : EffectSinkNode
{
    private static readonly PortSpec[] Ins = [PortSpec.MultiScalar("in")];

    public FfbAxis Axis { get; set; } = FfbAxis.Y;
    public bool Negative { get; set; }
    public bool Bipolar { get; set; }
    public double MinStrength { get; set; }
    public double MaxStrength { get; set; } = 1;

    public override NodeKind Kind => NodeKind.ConstantForce;
    public override IReadOnlyList<PortSpec> Inputs => Ins;

    public override void Emit(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, List<FfbEffectCommand> commands)
    {
        var x = inputs[0].X;
        var amount = Bipolar ? Math.Min(1, Math.Abs(x)) : Math.Clamp(x, 0, 1);
        if (IsSilent(amount))
            return;
        var sign = (Bipolar && x < 0 ? -1 : 1) * (Negative ? -1 : 1);
        commands.Add(Command(Axis, FfbEffectType.ConstantForce) with { Magnitude = sign * Lerp(MinStrength, MaxStrength, amount) });
    }

    internal override void SaveSinkParams(JsonObject json)
    {
        json["axis"] = Axis.ToString();
        json["negative"] = Negative;
        json["bipolar"] = Bipolar;
        json["minStrength"] = MinStrength;
        json["maxStrength"] = MaxStrength;
    }

    internal override void LoadSinkParams(JsonObject json)
    {
        Axis = json.Enum("axis", FfbAxis.Y);
        Negative = json.Bool("negative", false);
        Bipolar = json.Bool("bipolar", false);
        MinStrength = json.Num("minStrength", 0);
        MaxStrength = json.Num("maxStrength", 1);
    }
}

/// <summary>Centring spring: force = -stiffness * (position - centre), capped at saturation.</summary>
/// <remarks>
/// "in" (0..1) sets the stiffness between MinStiffness and MaxStiffness (airspeed stiffening); "centre" (-1..1, times
/// <see cref="CentreScale"/>) moves the rest position (trim; a <see cref="TrimNode"/> output or the game's trim).
/// <see cref="InvertCentre"/> moves the centre the other way (a trim that goes the wrong direction on this stick).
/// </remarks>
public sealed class SpringNode : AxisEffectSinkNode
{
    private static readonly PortSpec[] Ins = [PortSpec.MultiScalar("in"), PortSpec.Scalar("centre")];

    public double MinStiffness { get; set; }
    public double MaxStiffness { get; set; } = 1;
    public double Saturation { get; set; } = 1;
    public double CentreScale { get; set; } = 1;
    public bool InvertCentre { get; set; }

    public override NodeKind Kind => NodeKind.Spring;
    public override IReadOnlyList<PortSpec> Inputs => Ins;

    protected override bool TryBuild(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, out FfbEffectCommand effect)
    {
        var amount = Unit(inputs[0].X);
        effect = Command(FfbAxis.X, FfbEffectType.Spring) with
        {
            Coefficient = Unit(Lerp(MinStiffness, MaxStiffness, amount)),
            Centre = Math.Clamp(inputs[1].X * CentreScale * (InvertCentre ? -1 : 1), -1, 1),
            Saturation = Unit(Saturation)
        };
        return !IsSilent(amount);
    }

    internal override void SaveEffectParams(JsonObject json)
    {
        json["minStiffness"] = MinStiffness;
        json["maxStiffness"] = MaxStiffness;
        json["saturation"] = Saturation;
        json["centreScale"] = CentreScale;
        json["invertCentre"] = InvertCentre;
    }

    internal override void LoadEffectParams(JsonObject json)
    {
        MinStiffness = json.Num("minStiffness", 0);
        MaxStiffness = json.Num("maxStiffness", 1);
        Saturation = json.Num("saturation", 1);
        CentreScale = json.Num("centreScale", 1);
        InvertCentre = json.Bool("invertCentre", false);
    }
}

/// <summary>Base of damper and friction: the input sets the coefficient between the 0 % and 100 % values.</summary>
public abstract class CoefficientSinkNode : AxisEffectSinkNode
{
    private static readonly PortSpec[] Ins = [PortSpec.MultiScalar("in")];

    public double MinCoefficient { get; set; }
    public double MaxCoefficient { get; set; } = 1;

    public override IReadOnlyList<PortSpec> Inputs => Ins;

    protected abstract FfbEffectType EffectType { get; }

    protected override bool TryBuild(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, out FfbEffectCommand effect)
    {
        var amount = Unit(inputs[0].X);
        effect = Command(FfbAxis.X, EffectType) with { Coefficient = Unit(Lerp(MinCoefficient, MaxCoefficient, amount)) };
        return !IsSilent(amount);
    }

    internal override void SaveEffectParams(JsonObject json)
    {
        json["minCoefficient"] = MinCoefficient;
        json["maxCoefficient"] = MaxCoefficient;
    }

    internal override void LoadEffectParams(JsonObject json)
    {
        MinCoefficient = json.Num("minCoefficient", 0);
        MaxCoefficient = json.Num("maxCoefficient", 1);
    }
}

/// <summary>Resistance proportional to stick speed.</summary>
public sealed class DamperNode : CoefficientSinkNode
{
    public override NodeKind Kind => NodeKind.Damper;
    protected override FfbEffectType EffectType => FfbEffectType.Damper;
}

/// <summary>Constant resistance against any stick movement.</summary>
public sealed class FrictionNode : CoefficientSinkNode
{
    public override NodeKind Kind => NodeKind.Friction;
    protected override FfbEffectType EffectType => FfbEffectType.Friction;
}

/// <summary>Vibration (stall buffet, gear rumble, runway) or pulsed bursts (guns): Forza's rumble / pulsed beep.</summary>
/// <remarks>
/// The input sets strength and/or frequency (<see cref="Modulation"/>) between the 0 % and 100 % values. Pulsed
/// gates the vibration on and off <see cref="PulseRateHz"/> times a second for <see cref="DutyCycle"/> of each period;
/// in the off part the effect keeps running with strength 0, so the device keeps its slot. A wire into "frequency"
/// (Hz, e.g. IL-2's cockpit shake frequency) replaces the frequency mapping while it is above 0.
/// </remarks>
public sealed class PeriodicNode : AxisEffectSinkNode
{
    private static readonly PortSpec[] Ins = [PortSpec.MultiScalar("in"), PortSpec.Scalar("frequency")];

    public FfbWaveform Waveform { get; set; } = FfbWaveform.Sine;
    public FfbModulation Modulation { get; set; } = FfbModulation.StrengthOnly;
    public double MinStrength { get; set; }
    public double MaxStrength { get; set; } = 1;
    public double MinFrequencyHz { get; set; } = 10;
    public double MaxFrequencyHz { get; set; } = 10;
    public bool Pulsed { get; set; }
    public double PulseRateHz { get; set; } = 10;
    public double DutyCycle { get; set; } = 0.5;

    public override NodeKind Kind => NodeKind.Periodic;
    public override IReadOnlyList<PortSpec> Inputs => Ins;

    public const double MinHz = 0.5;
    public const double MaxHz = 500;

    protected override bool TryBuild(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, out FfbEffectCommand effect)
    {
        var amount = Unit(inputs[0].X);
        var strength = Modulation == FfbModulation.FrequencyOnly ? MaxStrength : Lerp(MinStrength, MaxStrength, amount);
        var frequency = Modulation == FfbModulation.StrengthOnly ? MinFrequencyHz : Lerp(MinFrequencyHz, MaxFrequencyHz, amount);
        if (inputs[1].X > 0)
            frequency = inputs[1].X;
        if (Pulsed)
        {
            var phase = context.Time * Math.Max(0.1, PulseRateHz);
            if (phase - Math.Floor(phase) >= Math.Clamp(DutyCycle, 0.05, 0.95))
                strength = 0;
        }

        effect = Command(FfbAxis.X, FfbEffectType.Periodic) with
        {
            Magnitude = Unit(strength),
            FrequencyHz = Math.Clamp(frequency, MinHz, MaxHz),
            Waveform = Waveform
        };
        return !IsSilent(amount);
    }

    internal override void SaveEffectParams(JsonObject json)
    {
        json["waveform"] = Waveform.ToString();
        json["modulation"] = Modulation.ToString();
        json["minStrength"] = MinStrength;
        json["maxStrength"] = MaxStrength;
        json["minFrequency"] = MinFrequencyHz;
        json["maxFrequency"] = MaxFrequencyHz;
        json["pulsed"] = Pulsed;
        json["pulseRate"] = PulseRateHz;
        json["dutyCycle"] = DutyCycle;
    }

    internal override void LoadEffectParams(JsonObject json)
    {
        Waveform = json.Enum("waveform", FfbWaveform.Sine);
        Modulation = json.Enum("modulation", FfbModulation.StrengthOnly);
        MinStrength = json.Num("minStrength", 0);
        MaxStrength = json.Num("maxStrength", 1);
        MinFrequencyHz = json.Num("minFrequency", 10);
        MaxFrequencyHz = json.Num("maxFrequency", 10);
        Pulsed = json.Bool("pulsed", false);
        PulseRateHz = json.Num("pulseRate", 10);
        DutyCycle = json.Num("dutyCycle", 0.5);
    }
}
