using System.Text.Json.Nodes;

namespace JoyPro.Ffb.Core.Graph;

/// <summary>A fixed value (Forza: ConstantSignalNode, now signed and without the 0..1 clamp).</summary>
/// <remarks>An exact test value that works without the game; <see cref="Active"/> off gives 0 (the Forza way to stop
/// a hardware test).</remarks>
public sealed class ConstantNode : FfbNode
{
    private static readonly PortSpec[] NoInputs = [];
    private static readonly PortSpec[] Outs = [PortSpec.Scalar("value")];

    public double Value { get; set; }
    public bool Active { get; set; } = true;
    public override NodeKind Kind => NodeKind.Constant;
    public override IReadOnlyList<PortSpec> Inputs => NoInputs;
    public override IReadOnlyList<PortSpec> Outputs => Outs;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs) =>
        outputs[0] = new Signal(Active ? Value : 0);

    internal override void SaveParams(JsonObject json)
    {
        json["value"] = Value;
        json["active"] = Active;
    }

    internal override void LoadParams(JsonObject json)
    {
        Value = json.Num("value", 0);
        Active = json.Bool("active", true);
    }
}

/// <summary>Any number of wires merged into one value (Forza: GroupSignalNode).</summary>
public sealed class GroupNode : FfbNode
{
    private static readonly PortSpec[] Ins = [PortSpec.MultiScalar("in")];
    private static readonly PortSpec[] Outs = [PortSpec.Scalar("out")];

    public CombineMode Mode { get; set; } = CombineMode.Maximum;
    public override NodeKind Kind => NodeKind.Group;
    public override IReadOnlyList<PortSpec> Inputs => Ins;
    public override IReadOnlyList<PortSpec> Outputs => Outs;

    public override CombineMode CombineModeFor(int inputIndex) => Mode;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs) =>
        outputs[0] = new Signal(Mode == CombineMode.SumClamped ? Math.Clamp(inputs[0].X, 0, 1) : inputs[0].X);

    internal override void SaveParams(JsonObject json) => json["mode"] = Mode.ToString();
    internal override void LoadParams(JsonObject json) => Mode = json.Enum("mode", CombineMode.Maximum);
}

public enum TelemetryNormalisation
{
    /// <summary>The raw value in its unit (m/s, G, rounds).</summary>
    Raw,

    /// <summary>Min..Max -> 0..1, clamped.</summary>
    Unsigned,

    /// <summary>Min..Max -> -1..1, clamped.</summary>
    Signed
}

/// <summary>Reads one telemetry key (Forza: TelemetrySignalNode, plus raw and signed modes and a "valid" output).</summary>
/// <remarks>
/// A missing key or missing source gives value 0 and valid 0 -- even with Invert. The Forza graph learned that the
/// hard way: an inverted node without packets meant full strength (see SignalGraph.CreateDefault there).
/// </remarks>
public sealed class TelemetryNode : FfbNode
{
    private static readonly PortSpec[] NoInputs = [];
    private static readonly PortSpec[] Outs = [PortSpec.Scalar("value"), PortSpec.Scalar("valid")];

    public string Key { get; set; } = "";
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 1;
    public TelemetryNormalisation Mode { get; set; } = TelemetryNormalisation.Raw;
    public bool Absolute { get; set; }
    public bool Invert { get; set; }

    public override NodeKind Kind => NodeKind.Telemetry;
    public override IReadOnlyList<PortSpec> Inputs => NoInputs;
    public override IReadOnlyList<PortSpec> Outputs => Outs;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs)
    {
        if (context.Telemetry is null || Key.Length == 0 || !context.Telemetry.TryGet(Key, out var raw) ||
            !double.IsFinite(raw))
        {
            outputs[0] = Signal.Zero;
            outputs[1] = Signal.Zero;
            return;
        }

        outputs[0] = new Signal(Normalise(raw));
        outputs[1] = new Signal(1);
    }

    public double Normalise(double raw)
    {
        if (Absolute)
            raw = Math.Abs(raw);

        var range = Maximum - Minimum;
        double value;
        switch (Mode)
        {
            case TelemetryNormalisation.Unsigned:
                value = Math.Abs(range) < 1e-12 ? 0 : Math.Clamp((raw - Minimum) / range, 0, 1);
                return Invert ? 1 - value : value;
            case TelemetryNormalisation.Signed:
                value = Math.Abs(range) < 1e-12 ? 0 : Math.Clamp(2 * (raw - Minimum) / range - 1, -1, 1);
                return Invert ? -value : value;
            default:
                return Invert ? -raw : raw;
        }
    }

    internal override void SaveParams(JsonObject json)
    {
        json["key"] = Key;
        json["min"] = Minimum;
        json["max"] = Maximum;
        json["mode"] = Mode.ToString();
        json["absolute"] = Absolute;
        json["invert"] = Invert;
    }

    internal override void LoadParams(JsonObject json)
    {
        Key = json.Str("key", "");
        Minimum = json.Num("min", 0);
        Maximum = json.Num("max", 1);
        Mode = json.Enum("mode", TelemetryNormalisation.Raw);
        Absolute = json.Bool("absolute", false);
        Invert = json.Bool("invert", false);
    }
}

/// <summary>Base for nodes with two scalar inputs a, b and one output.</summary>
public abstract class BinaryNode : FfbNode
{
    private static readonly PortSpec[] Outs = [PortSpec.Scalar("out")];
    private readonly PortSpec[] _inputs;

    protected BinaryNode(double defaultB) => _inputs = [PortSpec.Scalar("a"), PortSpec.Scalar("b", defaultB)];

    public override IReadOnlyList<PortSpec> Inputs => _inputs;
    public override IReadOnlyList<PortSpec> Outputs => Outs;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs) =>
        outputs[0] = new Signal(Apply(inputs[0].X, inputs[1].X));

    protected abstract double Apply(double a, double b);

    internal override void SaveParams(JsonObject json)
    {
    }

    internal override void LoadParams(JsonObject json)
    {
    }
}

public sealed class AddNode : BinaryNode
{
    public AddNode() : base(0) { }
    public override NodeKind Kind => NodeKind.Add;
    protected override double Apply(double a, double b) => a + b;
}

public sealed class MultiplyNode : BinaryNode
{
    public MultiplyNode() : base(1) { }
    public override NodeKind Kind => NodeKind.Multiply;
    protected override double Apply(double a, double b) => a * b;
}

public sealed class MinNode : BinaryNode
{
    public MinNode() : base(0) { }
    public override NodeKind Kind => NodeKind.Min;
    protected override double Apply(double a, double b) => Math.Min(a, b);
}

public sealed class MaxNode : BinaryNode
{
    public MaxNode() : base(0) { }
    public override NodeKind Kind => NodeKind.Max;
    protected override double Apply(double a, double b) => Math.Max(a, b);
}

/// <summary>Base for nodes with one scalar input "in" and one output "out".</summary>
public abstract class UnaryNode : FfbNode
{
    private static readonly PortSpec[] Ins = [PortSpec.Scalar("in")];
    private static readonly PortSpec[] Outs = [PortSpec.Scalar("out")];

    public override IReadOnlyList<PortSpec> Inputs => Ins;
    public override IReadOnlyList<PortSpec> Outputs => Outs;
}

/// <summary>out = in * Scale + Offset.</summary>
public sealed class ScaleOffsetNode : UnaryNode
{
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }
    public override NodeKind Kind => NodeKind.ScaleOffset;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs) =>
        outputs[0] = new Signal(inputs[0].X * Scale + Offset);

    internal override void SaveParams(JsonObject json)
    {
        json["scale"] = Scale;
        json["offset"] = Offset;
    }

    internal override void LoadParams(JsonObject json)
    {
        Scale = json.Num("scale", 1);
        Offset = json.Num("offset", 0);
    }
}

public sealed class ClampNode : UnaryNode
{
    public double Minimum { get; set; } = -1;
    public double Maximum { get; set; } = 1;
    public override NodeKind Kind => NodeKind.Clamp;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs)
    {
        var low = Math.Min(Minimum, Maximum);
        var high = Math.Max(Minimum, Maximum);
        outputs[0] = new Signal(Math.Clamp(inputs[0].X, low, high));
    }

    internal override void SaveParams(JsonObject json)
    {
        json["min"] = Minimum;
        json["max"] = Maximum;
    }

    internal override void LoadParams(JsonObject json)
    {
        Minimum = json.Num("min", -1);
        Maximum = json.Num("max", 1);
    }
}

public sealed class AbsNode : UnaryNode
{
    public override NodeKind Kind => NodeKind.Abs;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs) =>
        outputs[0] = new Signal(Math.Abs(inputs[0].X));

    internal override void SaveParams(JsonObject json)
    {
    }

    internal override void LoadParams(JsonObject json)
    {
    }
}

/// <summary>Bezier transfer function (Forza: CurveSignalNode).</summary>
/// <remarks>
/// The curve is drawn on 0..1. Symmetric (default) applies it to |in| and keeps the sign, so a -1..1 force gets the
/// same shape both ways; otherwise the input is clamped to 0..1 first.
/// </remarks>
public sealed class CurveNode : UnaryNode
{
    public BezierCurve Curve { get; } = new();
    public bool Symmetric { get; set; } = true;
    public override NodeKind Kind => NodeKind.Curve;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs)
    {
        var x = inputs[0].X;
        outputs[0] = Symmetric
            ? new Signal(Math.Sign(x) * Curve.Evaluate(Math.Abs(x)))
            : new Signal(Curve.Evaluate(x));
    }

    internal override void SaveParams(JsonObject json)
    {
        json["symmetric"] = Symmetric;
        var points = new JsonArray();
        foreach (var node in Curve.Nodes)
        {
            points.Add(new JsonObject
            {
                ["x"] = node.Position.X,
                ["y"] = node.Position.Y,
                ["inX"] = node.InHandle.X,
                ["inY"] = node.InHandle.Y,
                ["outX"] = node.OutHandle.X,
                ["outY"] = node.OutHandle.Y
            });
        }

        json["points"] = points;
    }

    internal override void LoadParams(JsonObject json)
    {
        Symmetric = json.Bool("symmetric", true);
        if (json["points"] is not JsonArray points || points.Count < 2)
        {
            Curve.ResetLinear();
            return;
        }

        Curve.Nodes.Clear();
        foreach (var item in points.OfType<JsonObject>())
        {
            Curve.Nodes.Add(new BezierNode(
                new CurvePoint(item.Num("x", 0), item.Num("y", 0)),
                new CurvePoint(item.Num("inX", 0), item.Num("inY", 0)),
                new CurvePoint(item.Num("outX", 0), item.Num("outY", 0))));
        }

        if (Curve.Nodes.Count < 2)
            Curve.ResetLinear();
    }
}

/// <summary>Switch: out = (select >= Threshold) ? b : a.</summary>
public sealed class SelectNode : FfbNode
{
    private static readonly PortSpec[] Ins = [PortSpec.Scalar("a"), PortSpec.Scalar("b"), PortSpec.Scalar("select")];
    private static readonly PortSpec[] Outs = [PortSpec.Scalar("out")];

    public double Threshold { get; set; } = 0.5;
    public override NodeKind Kind => NodeKind.Select;
    public override IReadOnlyList<PortSpec> Inputs => Ins;
    public override IReadOnlyList<PortSpec> Outputs => Outs;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs) =>
        outputs[0] = inputs[2].X >= Threshold ? inputs[1] : inputs[0];

    internal override void SaveParams(JsonObject json) => json["threshold"] = Threshold;
    internal override void LoadParams(JsonObject json) => Threshold = json.Num("threshold", 0.5);
}

/// <summary>Two scalars to a vector.</summary>
public sealed class MakeVec2Node : FfbNode
{
    private static readonly PortSpec[] Ins = [PortSpec.Scalar("x"), PortSpec.Scalar("y")];
    private static readonly PortSpec[] Outs = [PortSpec.Vec2("vec")];

    public override NodeKind Kind => NodeKind.MakeVec2;
    public override IReadOnlyList<PortSpec> Inputs => Ins;
    public override IReadOnlyList<PortSpec> Outputs => Outs;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs) =>
        outputs[0] = new Signal(inputs[0].X, inputs[1].X);

    internal override void SaveParams(JsonObject json)
    {
    }

    internal override void LoadParams(JsonObject json)
    {
    }
}

/// <summary>A vector to two scalars plus its length.</summary>
public sealed class SplitVec2Node : FfbNode
{
    private static readonly PortSpec[] Ins = [PortSpec.Vec2("vec")];
    private static readonly PortSpec[] Outs = [PortSpec.Scalar("x"), PortSpec.Scalar("y"), PortSpec.Scalar("length")];

    public override NodeKind Kind => NodeKind.SplitVec2;
    public override IReadOnlyList<PortSpec> Inputs => Ins;
    public override IReadOnlyList<PortSpec> Outputs => Outs;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs)
    {
        var v = inputs[0];
        outputs[0] = new Signal(v.X);
        outputs[1] = new Signal(v.Y);
        outputs[2] = new Signal(Math.Sqrt(v.X * v.X + v.Y * v.Y));
    }

    internal override void SaveParams(JsonObject json)
    {
    }

    internal override void LoadParams(JsonObject json)
    {
    }
}
