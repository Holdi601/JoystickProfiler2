using System.Text.Json.Nodes;
using JoyPro.Ffb.Core.Output;
using JoyPro.Ffb.Core.Telemetry;

namespace JoyPro.Ffb.Core.Graph;

/// <summary>Every node type. Saved by name in the JSON ("kind"), so names never change.</summary>
public enum NodeKind
{
    // Sources
    Constant,
    Telemetry,

    // Combining (Forza: group node)
    Group,

    // Stateless maths
    Add,
    Multiply,
    ScaleOffset,
    Clamp,
    Abs,
    Min,
    Max,
    Curve,
    Select,

    // Stateful (per tick, with dt)
    LowPass,
    SlewLimit,
    Derivative,
    CounterDeltaToEvent,
    Envelope,
    Threshold,

    // Vectors
    MakeVec2,
    SplitVec2,

    // Effect sinks (outputs)
    ConstantForce,
    Spring,
    Damper,
    Friction,
    Periodic,

    // Direction, stick input and trim (appended: kinds are saved by name, the order does not matter)
    Invert,
    DeviceButton,
    StickPosition,
    Trim
}

/// <summary>What a node sees in one tick.</summary>
/// <param name="Telemetry">The live source, or null (then Telemetry nodes read as invalid / 0).</param>
/// <param name="Time">Runtime time in seconds.</param>
/// <param name="Dt">Seconds since the previous tick (0 on the first one).</param>
/// <param name="Inputs">Buttons, hats and the stick position (null: input nodes read as released / centred).</param>
public readonly record struct FfbEvalContext(ITelemetrySource? Telemetry, double Time, double Dt, JoyPro.Ffb.Core.Input.IFfbInputs? Inputs = null);

/// <summary>A node of the blueprint graph: typed named ports, editable parameters, optional per-tick state.</summary>
/// <remarks>
/// Ported from the Forza app's SignalNode (title + location + type), redesigned: the Forza nodes had one implicit
/// 0..1 port and no state; these have named ports (<see cref="Inputs"/>, <see cref="Outputs"/>), signed values and
/// may keep state between ticks (<see cref="Reset"/> clears it). Ids are strings and survive save/load unchanged.
/// </remarks>
public abstract class FfbNode
{
    protected FfbNode()
    {
        Id = NewId();
        Title = Kind.ToString();
    }

    public string Id { get; internal set; }
    public string Title { get; set; }

    /// <summary>Editor position (the blueprint canvas); the engine ignores it.</summary>
    public double X { get; set; }
    public double Y { get; set; }

    public abstract NodeKind Kind { get; }
    public abstract IReadOnlyList<PortSpec> Inputs { get; }
    public abstract IReadOnlyList<PortSpec> Outputs { get; }

    /// <summary>Per-node values for unconnected inputs (port name -> value); missing ports use <see cref="PortSpec.Default"/>.</summary>
    public Dictionary<string, double> InputDefaults { get; } = new(StringComparer.Ordinal);

    public bool IsSink => this is EffectSinkNode;

    public double DefaultFor(PortSpec port) =>
        InputDefaults.TryGetValue(port.Name, out var value) ? value : port.Default;

    /// <summary>How the wires of a multi input (<see cref="PortSpec.Multi"/>) are merged; Maximum unless a node says otherwise.</summary>
    public virtual CombineMode CombineModeFor(int inputIndex) => CombineMode.Maximum;

    public int InputIndex(string name) => IndexOf(Inputs, name);
    public int OutputIndex(string name) => IndexOf(Outputs, name);

    /// <summary>Clears per-tick state (filters, envelopes, counter baselines).</summary>
    public virtual void Reset()
    {
    }

    /// <summary>Computes the outputs of one tick. Inputs are in <see cref="Inputs"/> order, outputs in <see cref="Outputs"/> order.</summary>
    public abstract void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs);

    internal abstract void SaveParams(JsonObject json);
    internal abstract void LoadParams(JsonObject json);

    internal static string NewId() => "n" + Guid.NewGuid().ToString("N")[..12];

    private static int IndexOf(IReadOnlyList<PortSpec> ports, string name)
    {
        for (var i = 0; i < ports.Count; i++)
        {
            if (string.Equals(ports[i].Name, name, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}

/// <summary>A node without outputs that turns its inputs into force effects on one device (Forza: OutputSignalNode).</summary>
/// <remarks>
/// As in the Forza graph the first input ("in") takes any number of wires, merged by <see cref="InputMode"/>, and the
/// node maps that 0..1 value onto its effect with values at 0 % and 100 % input. Several nodes on the same effect slot
/// (device, axis, effect type, <see cref="Slot"/>): the strongest wins (FfbMixer).
/// </remarks>
public abstract class EffectSinkNode : FfbNode
{
    private static readonly PortSpec[] NoOutputs = [];

    /// <summary>The device the effect goes to (empty = the device the user picked for telemetry force feedback).</summary>
    public string Device { get; set; } = "";

    /// <summary>The effect name the per-game on/off switches and strengths refer to ("buffet", "guns", ...; empty = none).</summary>
    public string Effect { get; set; } = "";

    /// <summary>Effect slot 0..3: nodes of the same type on the same slot compete (strongest wins), other slots add up.</summary>
    public int Slot { get; set; }

    /// <summary>How several wires into "in" are merged.</summary>
    public CombineMode InputMode { get; set; } = CombineMode.Maximum;

    /// <summary>No effect at all while the input is 0 (otherwise the 0 % values play).</summary>
    public bool SilenceAtZero { get; set; } = true;

    public override IReadOnlyList<PortSpec> Outputs => NoOutputs;

    public override CombineMode CombineModeFor(int inputIndex) => inputIndex == 0 ? InputMode : CombineMode.Maximum;

    protected bool IsSilent(double input) => SilenceAtZero && Math.Abs(input) <= 1e-4;

    protected static double Lerp(double at0, double at100, double amount) => at0 + (at100 - at0) * Math.Clamp(amount, 0, 1);

    protected FfbEffectCommand Command(FfbAxis axis, FfbEffectType type) =>
        new(Device, axis, type, SourceNodeId: Id, Tag: Effect, Slot: Math.Clamp(Slot, 0, MaxSlot));

    public const int MaxSlot = 3;

    public sealed override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs)
    {
    }

    /// <summary>Adds this tick's effect commands (duration is filled in by the runtime).</summary>
    public abstract void Emit(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, List<FfbEffectCommand> commands);

    internal override void SaveParams(JsonObject json)
    {
        json["device"] = Device;
        json["effect"] = Effect;
        json["slot"] = Slot;
        json["inputMode"] = InputMode.ToString();
        json["silenceAtZero"] = SilenceAtZero;
        SaveSinkParams(json);
    }

    internal override void LoadParams(JsonObject json)
    {
        Device = json.Str("device", "");
        Effect = json.Str("effect", "");
        Slot = (int)Math.Clamp(json.Num("slot", 0), 0, MaxSlot);
        InputMode = json.Enum("inputMode", CombineMode.Maximum);
        SilenceAtZero = json.Bool("silenceAtZero", true);
        LoadSinkParams(json);
    }

    internal virtual void SaveSinkParams(JsonObject json)
    {
    }

    internal virtual void LoadSinkParams(JsonObject json)
    {
    }
}

internal static class JsonParams
{
    public static double Num(this JsonObject json, string name, double fallback)
    {
        if (json[name] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number))
            return number;
        return fallback;
    }

    public static bool Bool(this JsonObject json, string name, bool fallback) =>
        json[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    public static string Str(this JsonObject json, string name, string fallback) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;

    public static T Enum<T>(this JsonObject json, string name, T fallback) where T : struct, System.Enum =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var text) &&
        System.Enum.TryParse<T>(text, true, out var parsed) && System.Enum.IsDefined(parsed)
            ? parsed
            : fallback;
}
