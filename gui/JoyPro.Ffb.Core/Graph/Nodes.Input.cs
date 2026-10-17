using System.Text.Json.Nodes;
using JoyPro.Ffb.Core.Input;
using JoyPro.Ffb.Core.Output;

namespace JoyPro.Ffb.Core.Graph;

/// <summary>Flips the sign of a value: a force or a trim then acts the other way. Off passes the value through.</summary>
/// <remarks>The visible, one-click way to turn a direction around in a graph (the device-wide invert lives in the
/// settings: it fixes how a stick maps JoyPro's directions; this node changes what one effect does).</remarks>
public sealed class InvertNode : UnaryNode
{
    public bool Enabled { get; set; } = true;

    public override NodeKind Kind => NodeKind.Invert;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs) =>
        outputs[0] = new Signal(Enabled ? -inputs[0].X : inputs[0].X);

    internal override void SaveParams(JsonObject json) => json["enabled"] = Enabled;
    internal override void LoadParams(JsonObject json) => Enabled = json.Bool("enabled", true);
}

/// <summary>A button or hat direction of any device: "held" is 1 while it is down, "pressed" fires once per press.</summary>
/// <remarks>
/// <see cref="Device"/> "" = the force feedback stick itself (read through its own acquisition), otherwise the device as
/// the "press the button now" capture names it ("Name {instance guid}"); those are read without exclusive access, in
/// the background, so a throttle hat works while the game has the focus. <see cref="Input"/> is "JOY_BTN5" or
/// "JOY_BTN_POV1_U". Unassigned or missing devices read 0 (never pressed).
/// </remarks>
public sealed class DeviceButtonNode : FfbNode
{
    private static readonly PortSpec[] NoInputs = [];
    private static readonly PortSpec[] Outs = [PortSpec.Scalar("held"), PortSpec.Event("pressed")];

    private string _input = "";
    private FfbInputRef _parsed;
    private bool _was;

    public string Device { get; set; } = "";

    public string Input
    {
        get => _input;
        set
        {
            _input = (value ?? "").Trim();
            _parsed = FfbInputRef.Parse(_input);
        }
    }

    internal FfbInputRef Parsed => _parsed;

    public override NodeKind Kind => NodeKind.DeviceButton;
    public override IReadOnlyList<PortSpec> Inputs => NoInputs;
    public override IReadOnlyList<PortSpec> Outputs => Outs;

    public override void Reset() => _was = false;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs)
    {
        var held = false;
        if (!_parsed.IsNone && context.Inputs is { } source && source.TryGetButton(Device, _parsed, out var pressed))
            held = pressed;
        outputs[0] = new Signal(held ? 1 : 0);
        outputs[1] = Signal.Events(held && !_was ? 1 : 0);
        _was = held;
    }

    internal override void SaveParams(JsonObject json)
    {
        json["device"] = Device;
        json["input"] = Input;
    }

    internal override void LoadParams(JsonObject json)
    {
        Device = json.Str("device", "");
        Input = json.Str("input", "");
    }
}

/// <summary>Where the force feedback stick is: x and y -1..1 (right and towards the pilot positive), valid 1 while known.</summary>
public sealed class StickPositionNode : FfbNode
{
    private static readonly PortSpec[] NoInputs = [];
    private static readonly PortSpec[] Outs = [PortSpec.Scalar("x"), PortSpec.Scalar("y"), PortSpec.Scalar("valid")];

    /// <summary>"" = the force feedback stick (the only device whose position is read today).</summary>
    public string Device { get; set; } = "";

    public override NodeKind Kind => NodeKind.StickPosition;
    public override IReadOnlyList<PortSpec> Inputs => NoInputs;
    public override IReadOnlyList<PortSpec> Outputs => Outs;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs)
    {
        if (context.Inputs is { } source && source.TryGetStick(Device, FfbAxis.X, out var x) && source.TryGetStick(Device, FfbAxis.Y, out var y))
        {
            outputs[0] = new Signal(Math.Clamp(x, -1, 1));
            outputs[1] = new Signal(Math.Clamp(y, -1, 1));
            outputs[2] = new Signal(1);
            return;
        }

        outputs[0] = Signal.Zero;
        outputs[1] = Signal.Zero;
        outputs[2] = Signal.Zero;
    }

    internal override void SaveParams(JsonObject json) => json["device"] = Device;
    internal override void LoadParams(JsonObject json) => Device = json.Str("device", "");
}

/// <summary>Stick trim: the spring's centre, moved by trim buttons, set to where the stick is, or reset.</summary>
/// <remarks>
/// Inputs are buttons (held = 0.5 or more; wire <see cref="DeviceButtonNode"/>.held in):
///  - "here": trim to the current stick position. While it is held the centre follows the stick (the spring lets go,
///    like a helicopter's force trim release); when it is let go the centre stays where the stick was.
///  - "up"/"down"/"left"/"right": move the centre one <see cref="Step"/> per press; held longer than
///    <see cref="RepeatDelay"/>, it keeps moving <see cref="RepeatRate"/> steps a second. Up is nose down: the centre
///    moves forward (away from the pilot).
///  - "reset": back to the middle.
/// Outputs "x"/"y" (-1..1, JoyPro's convention: right and towards the pilot positive) go into a Spring's "centre"
/// (one per axis). <see cref="InvertX"/>/<see cref="InvertY"/> turn the buttons' directions around.
/// <see cref="MoveRate"/> limits how fast the centre moves after a reset or a big trim, so the stick does not jump.
/// The trim survives <see cref="Reset"/> (a paused game, a telemetry gap): only "reset", a new graph or a restart clear it.
/// "here" needs the stick position (the force feedback device must be acquired); without it the press is ignored.
/// </remarks>
public sealed class TrimNode : FfbNode
{
    private static readonly PortSpec[] Ins =
    [
        PortSpec.Scalar("here"), PortSpec.Scalar("up"), PortSpec.Scalar("down"), PortSpec.Scalar("left"),
        PortSpec.Scalar("right"), PortSpec.Scalar("reset")
    ];

    private static readonly PortSpec[] Outs = [PortSpec.Scalar("x"), PortSpec.Scalar("y")];

    private double _targetX;
    private double _targetY;
    private double _x;
    private double _y;
    private readonly ButtonState[] _buttons = new ButtonState[6];

    /// <summary>Centre change per press (fraction of the full travel to one side).</summary>
    public double Step { get; set; } = 0.02;

    /// <summary>Seconds a trim button must be held before it repeats.</summary>
    public double RepeatDelay { get; set; } = 0.35;

    /// <summary>Steps per second while a trim button is held (after the delay); 0 = no repeat.</summary>
    public double RepeatRate { get; set; } = 10;

    /// <summary>The largest centre offset, 0..1.</summary>
    public double Limit { get; set; } = 0.8;

    /// <summary>How fast the centre may move, travel per second (0 = at once).</summary>
    public double MoveRate { get; set; } = 1.5;

    public bool InvertX { get; set; }
    public bool InvertY { get; set; }

    public override NodeKind Kind => NodeKind.Trim;
    public override IReadOnlyList<PortSpec> Inputs => Ins;
    public override IReadOnlyList<PortSpec> Outputs => Outs;

    /// <summary>The trimmed centre now (after the move limit).</summary>
    public (double X, double Y) Centre => (_x, _y);

    /// <summary>Keeps the trim; only the button edges start over (a held button does not fire again after a pause).</summary>
    public override void Reset()
    {
        for (var i = 0; i < _buttons.Length; i++)
            _buttons[i] = new ButtonState(true, 0, 0, Blocked: true);
    }

    /// <summary>Back to the middle at once (tests, a new aircraft).</summary>
    public void ClearTrim()
    {
        _targetX = _targetY = _x = _y = 0;
    }

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs)
    {
        var dt = Math.Max(0, context.Dt);
        var limit = Math.Clamp(Limit, 0, 1);
        var follow = false;

        // here: trim to the stick (and follow it while held).
        if (Held(inputs[0]) && context.Inputs is { } source &&
            source.TryGetStick("", FfbAxis.X, out var stickX) && source.TryGetStick("", FfbAxis.Y, out var stickY))
        {
            _targetX = Math.Clamp(stickX, -limit, limit);
            _targetY = Math.Clamp(stickY, -limit, limit);
            follow = true;
        }

        var up = Repeats(1, Held(inputs[1]), dt);
        var down = Repeats(2, Held(inputs[2]), dt);
        var left = Repeats(3, Held(inputs[3]), dt);
        var right = Repeats(4, Held(inputs[4]), dt);
        var step = Math.Max(0, Step);
        var signX = InvertX ? -1 : 1;
        var signY = InvertY ? -1 : 1;
        // up = nose down = forward = negative Y in JoyPro's convention.
        _targetY = Math.Clamp(_targetY + signY * step * (down - up), -limit, limit);
        _targetX = Math.Clamp(_targetX + signX * step * (right - left), -limit, limit);

        if (Repeats(5, Held(inputs[5]), dt, repeat: false) > 0)
        {
            _targetX = 0;
            _targetY = 0;
        }

        if (follow || MoveRate <= 0)
        {
            _x = _targetX;
            _y = _targetY;
        }
        else
        {
            var max = MoveRate * dt;
            _x += Math.Clamp(_targetX - _x, -max, max);
            _y += Math.Clamp(_targetY - _y, -max, max);
        }

        outputs[0] = new Signal(_x);
        outputs[1] = new Signal(_y);
    }

    private static bool Held(Signal signal) => signal.X >= 0.5;

    // Steps this tick for a trim button: 1 on the press, then RepeatRate per second after RepeatDelay.
    private int Repeats(int index, bool held, double dt, bool repeat = true)
    {
        var state = _buttons[index];
        if (!held)
        {
            _buttons[index] = new ButtonState(false, 0, 0);
            return 0;
        }

        if (!state.Down)
        {
            _buttons[index] = new ButtonState(true, 0, 0);
            return 1;
        }

        var heldFor = state.HeldFor + dt;
        var count = 0;
        var accumulator = state.Accumulator;
        if (repeat && RepeatRate > 0 && heldFor > Math.Max(0, RepeatDelay) && !state.Blocked)
        {
            accumulator += RepeatRate * Math.Min(dt, heldFor - Math.Max(0, RepeatDelay));
            count = (int)Math.Floor(accumulator);
            accumulator -= count;
        }

        _buttons[index] = state with { HeldFor = heldFor, Accumulator = accumulator };
        return count;
    }

    internal override void SaveParams(JsonObject json)
    {
        json["step"] = Step;
        json["repeatDelay"] = RepeatDelay;
        json["repeatRate"] = RepeatRate;
        json["limit"] = Limit;
        json["moveRate"] = MoveRate;
        json["invertX"] = InvertX;
        json["invertY"] = InvertY;
    }

    internal override void LoadParams(JsonObject json)
    {
        Step = Math.Clamp(json.Num("step", 0.02), 0, 1);
        RepeatDelay = Math.Max(0, json.Num("repeatDelay", 0.35));
        RepeatRate = Math.Max(0, json.Num("repeatRate", 10));
        Limit = Math.Clamp(json.Num("limit", 0.8), 0, 1);
        MoveRate = Math.Max(0, json.Num("moveRate", 1.5));
        InvertX = json.Bool("invertX", false);
        InvertY = json.Bool("invertY", false);
    }

    // Down: was held last tick. Blocked: held since before a Reset (no repeat until it is let go once).
    private readonly record struct ButtonState(bool Down, double HeldFor, double Accumulator, bool Blocked = false);
}
