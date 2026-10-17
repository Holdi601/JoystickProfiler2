using System.Text.Json.Nodes;

namespace JoyPro.Ffb.Core.Graph;

/// <summary>First-order low pass: y += (x - y) * (1 - e^(-dt / tau)). Starts at the first input (no ramp from 0).</summary>
public sealed class LowPassNode : UnaryNode
{
    private bool _primed;
    private double _y;

    /// <summary>Time constant in seconds; 0 passes the input through.</summary>
    public double TimeConstant { get; set; } = 0.1;

    public override NodeKind Kind => NodeKind.LowPass;

    public override void Reset()
    {
        _primed = false;
        _y = 0;
    }

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs)
    {
        var x = inputs[0].X;
        if (!_primed || TimeConstant <= 0)
        {
            _y = x;
            _primed = true;
        }
        else if (context.Dt > 0)
        {
            _y += (x - _y) * (1 - Math.Exp(-context.Dt / TimeConstant));
        }

        outputs[0] = new Signal(_y);
    }

    internal override void SaveParams(JsonObject json) => json["timeConstant"] = TimeConstant;
    internal override void LoadParams(JsonObject json) => TimeConstant = Math.Max(0, json.Num("timeConstant", 0.1));
}

/// <summary>Limits how fast a value may change (units per second). Starts at 0, so a graph never jolts on start.</summary>
public sealed class SlewLimitNode : UnaryNode
{
    private double _y;

    /// <summary>Largest increase per second; 0 or less = unlimited.</summary>
    public double RiseRate { get; set; } = 2;

    /// <summary>Largest decrease per second; 0 or less = unlimited.</summary>
    public double FallRate { get; set; } = 2;

    public override NodeKind Kind => NodeKind.SlewLimit;

    public override void Reset() => _y = 0;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs)
    {
        var delta = inputs[0].X - _y;
        var up = RiseRate > 0 ? RiseRate * context.Dt : double.PositiveInfinity;
        var down = FallRate > 0 ? FallRate * context.Dt : double.PositiveInfinity;
        _y += Math.Clamp(delta, -down, up);
        outputs[0] = new Signal(_y);
    }

    internal override void SaveParams(JsonObject json)
    {
        json["riseRate"] = RiseRate;
        json["fallRate"] = FallRate;
    }

    internal override void LoadParams(JsonObject json)
    {
        RiseRate = json.Num("riseRate", 2);
        FallRate = json.Num("fallRate", 2);
    }
}

/// <summary>Rate of change per second, low-passed (telemetry arrives at 25-50 Hz, the graph ticks at 100 Hz).</summary>
public sealed class DerivativeNode : UnaryNode
{
    private bool _primed;
    private double _previous;
    private double _y;

    /// <summary>Smoothing time constant in seconds; 0 = raw difference quotient.</summary>
    public double Smoothing { get; set; } = 0.05;

    public override NodeKind Kind => NodeKind.Derivative;

    public override void Reset()
    {
        _primed = false;
        _previous = 0;
        _y = 0;
    }

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs)
    {
        var x = inputs[0].X;
        if (!_primed || context.Dt <= 0)
        {
            _previous = x;
            _primed = true;
            outputs[0] = new Signal(_y);
            return;
        }

        var raw = (x - _previous) / context.Dt;
        _previous = x;
        _y = Smoothing <= 0 ? raw : _y + (raw - _y) * (1 - Math.Exp(-context.Dt / Smoothing));
        outputs[0] = new Signal(_y);
    }

    internal override void SaveParams(JsonObject json) => json["smoothing"] = Smoothing;
    internal override void LoadParams(JsonObject json) => Smoothing = Math.Max(0, json.Num("smoothing", 0.05));
}

public enum CounterDirection
{
    /// <summary>Rounds left dropping: one event per round fired (DCS shells, ammo counters).</summary>
    Decrease,

    /// <summary>Counters going up: IL-2 GUN_FIRE / HIT event counts.</summary>
    Increase,
    Both
}

/// <summary>Turns a counter into events: a shell counter that drops by 3 gives 3 events.</summary>
/// <remarks>
/// The first value only sets the baseline. A change against <see cref="Direction"/> (rearm, reload) moves the
/// baseline without events; so does a jump larger than <see cref="MaxDelta"/> (new aircraft, mission restart).
/// Fractions are kept, so a counter in steps of 0.5 with Step 1 fires every second step.
/// </remarks>
public sealed class CounterDeltaToEventNode : FfbNode
{
    private static readonly PortSpec[] Ins = [PortSpec.Scalar("counter")];
    private static readonly PortSpec[] Outs = [PortSpec.Event("events"), PortSpec.Scalar("total")];

    private bool _primed;
    private double _baseline;
    private long _total;

    public CounterDirection Direction { get; set; } = CounterDirection.Decrease;

    /// <summary>Counter units per event (1 = one event per round).</summary>
    public double Step { get; set; } = 1;

    /// <summary>Changes larger than this are treated as a reset, not as fire.</summary>
    public double MaxDelta { get; set; } = 100;

    public override NodeKind Kind => NodeKind.CounterDeltaToEvent;
    public override IReadOnlyList<PortSpec> Inputs => Ins;
    public override IReadOnlyList<PortSpec> Outputs => Outs;

    public override void Reset()
    {
        _primed = false;
        _baseline = 0;
        _total = 0;
    }

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs)
    {
        var x = inputs[0].X;
        var events = 0;
        if (!_primed)
        {
            _baseline = x;
            _primed = true;
        }
        else
        {
            var step = Step > 0 ? Step : 1;
            var delta = x - _baseline;
            var counts = Direction switch
            {
                CounterDirection.Decrease => delta < 0,
                CounterDirection.Increase => delta > 0,
                _ => delta != 0
            };

            if (Math.Abs(delta) > MaxDelta || (!counts && delta != 0))
            {
                _baseline = x;
            }
            else if (counts)
            {
                var steps = (int)Math.Floor(Math.Abs(delta) / step + 1e-9);
                if (steps > 0)
                {
                    events = steps;
                    _baseline += Math.Sign(delta) * steps * step;
                    _total += steps;
                }
            }
        }

        outputs[0] = Signal.Events(events);
        outputs[1] = new Signal(_total);
    }

    internal override void SaveParams(JsonObject json)
    {
        json["direction"] = Direction.ToString();
        json["step"] = Step;
        json["maxDelta"] = MaxDelta;
    }

    internal override void LoadParams(JsonObject json)
    {
        Direction = json.Enum("direction", CounterDirection.Decrease);
        Step = json.Num("step", 1);
        MaxDelta = json.Num("maxDelta", 100);
    }
}

/// <summary>One-shot attack/hold/decay pulse per event (a recoil kick, a hit thump).</summary>
/// <remarks>
/// With <see cref="Queue"/> (default) every event gets its own pulse: events that arrive while a pulse runs wait
/// and start one tick after it ended, so pulses stay countable (at most <see cref="MaxQueue"/> wait). Without it a
/// new event restarts the running pulse.
/// </remarks>
public sealed class EnvelopeNode : FfbNode
{
    private static readonly PortSpec[] Ins = [PortSpec.Event("trigger")];
    private static readonly PortSpec[] Outs = [PortSpec.Scalar("level"), PortSpec.Event("pulse")];

    private bool _active;
    private double _t;
    private int _pending;

    public double Attack { get; set; }
    public double Hold { get; set; } = 0.03;
    public double Decay { get; set; } = 0.05;
    public double Peak { get; set; } = 1;
    public bool Queue { get; set; } = true;
    public int MaxQueue { get; set; } = 8;

    public override NodeKind Kind => NodeKind.Envelope;
    public override IReadOnlyList<PortSpec> Inputs => Ins;
    public override IReadOnlyList<PortSpec> Outputs => Outs;

    public override void Reset()
    {
        _active = false;
        _t = 0;
        _pending = 0;
    }

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs)
    {
        var total = Math.Max(0, Attack) + Math.Max(0, Hold) + Math.Max(0, Decay);
        var endedNow = false;
        if (_active)
        {
            _t += context.Dt;
            if (_t >= total)
            {
                _active = false;
                endedNow = true;
            }
        }

        var started = 0;
        var events = inputs[0].EventCount;
        if (events > 0)
        {
            if (Queue)
            {
                _pending = Math.Min(_pending + events, Math.Max(1, MaxQueue));
            }
            else
            {
                _active = true;
                _t = 0;
                started = 1;
            }
        }

        if (!_active && !endedNow && _pending > 0)
        {
            _pending--;
            _active = true;
            _t = 0;
            started = 1;
        }

        outputs[0] = new Signal(_active ? Level(_t) : 0);
        outputs[1] = Signal.Events(started);
    }

    private double Level(double t)
    {
        var attack = Math.Max(0, Attack);
        var hold = Math.Max(0, Hold);
        var decay = Math.Max(0, Decay);
        if (t < attack)
            return Peak * t / attack;
        if (t < attack + hold)
            return Peak;
        if (t < attack + hold + decay)
            return Peak * (1 - (t - attack - hold) / decay);
        return 0;
    }

    internal override void SaveParams(JsonObject json)
    {
        json["attack"] = Attack;
        json["hold"] = Hold;
        json["decay"] = Decay;
        json["peak"] = Peak;
        json["queue"] = Queue;
        json["maxQueue"] = MaxQueue;
    }

    internal override void LoadParams(JsonObject json)
    {
        Attack = json.Num("attack", 0);
        Hold = json.Num("hold", 0.03);
        Decay = json.Num("decay", 0.05);
        Peak = json.Num("peak", 1);
        Queue = json.Bool("queue", true);
        MaxQueue = (int)Math.Clamp(json.Num("maxQueue", 8), 1, 1000);
    }
}

/// <summary>Schmitt trigger: on at in >= On, off again only below Off.</summary>
public sealed class ThresholdNode : FfbNode
{
    private static readonly PortSpec[] Ins = [PortSpec.Scalar("in")];
    private static readonly PortSpec[] Outs = [PortSpec.Scalar("state"), PortSpec.Event("rising"), PortSpec.Event("falling")];

    private bool _state;

    public double On { get; set; } = 0.5;
    public double Off { get; set; } = 0.4;

    public override NodeKind Kind => NodeKind.Threshold;
    public override IReadOnlyList<PortSpec> Inputs => Ins;
    public override IReadOnlyList<PortSpec> Outputs => Outs;

    public override void Reset() => _state = false;

    public override void Evaluate(in FfbEvalContext context, ReadOnlySpan<Signal> inputs, Span<Signal> outputs)
    {
        var x = inputs[0].X;
        var off = Math.Min(Off, On);
        var rising = 0;
        var falling = 0;
        if (!_state && x >= On)
        {
            _state = true;
            rising = 1;
        }
        else if (_state && x < off)
        {
            _state = false;
            falling = 1;
        }

        outputs[0] = new Signal(_state ? 1 : 0);
        outputs[1] = Signal.Events(rising);
        outputs[2] = Signal.Events(falling);
    }

    internal override void SaveParams(JsonObject json)
    {
        json["on"] = On;
        json["off"] = Off;
    }

    internal override void LoadParams(JsonObject json)
    {
        On = json.Num("on", 0.5);
        Off = json.Num("off", 0.4);
    }
}
