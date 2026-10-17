using JoyPro.Ffb.Core.Graph;

namespace JoyPro.Ffb.Core.Tests;

public class NodeTests
{
    private static Signal[] Run(FfbNode node, double dt, params Signal[] inputs)
    {
        var outputs = new Signal[node.Outputs.Count];
        node.Evaluate(new FfbEvalContext(null, 0, dt), inputs, outputs);
        return outputs;
    }

    private static double Run1(FfbNode node, double dt, double input) => Run(node, dt, new Signal(input))[0].X;

    [Fact]
    public void LowPass_StartsAtInputAndApproachesStepWithTimeConstant()
    {
        var node = new LowPassNode { TimeConstant = 0.1 };
        Assert.Equal(0, Run1(node, 0, 0));
        double y = 0;
        for (var i = 0; i < 10; i++)
            y = Run1(node, 0.01, 1);
        Assert.Equal(1 - Math.Exp(-1), y, 6); // one time constant: 63 %
        node.Reset();
        Assert.Equal(5, Run1(node, 0.01, 5));
    }

    [Fact]
    public void SlewLimit_LimitsRiseAndFallSeparately()
    {
        var node = new SlewLimitNode { RiseRate = 1, FallRate = 4 };
        Assert.Equal(0.01, Run1(node, 0.01, 1), 9);
        for (var i = 0; i < 99; i++)
            Run1(node, 0.01, 1);
        Assert.Equal(1, Run1(node, 0.01, 1), 9);
        Assert.Equal(0.96, Run1(node, 0.01, -1), 9);
    }

    [Fact]
    public void Derivative_MeasuresRateOfChange()
    {
        var node = new DerivativeNode { Smoothing = 0 };
        Assert.Equal(0, Run1(node, 0, 0));
        Assert.Equal(50, Run1(node, 0.02, 1), 9);
        Assert.Equal(0, Run1(node, 0.02, 1), 9);
        Assert.Equal(-100, Run1(node, 0.01, 0), 9);
    }

    [Fact]
    public void CounterDelta_OneEventPerRound_IgnoresReloadAndResets()
    {
        var node = new CounterDeltaToEventNode();
        int Events(double counter) => Run(node, 0.01, new Signal(counter))[0].EventCount;

        Assert.Equal(0, Events(500));   // baseline
        Assert.Equal(0, Events(500));
        Assert.Equal(1, Events(499));
        Assert.Equal(3, Events(496));
        Assert.Equal(0, Events(600));   // rearmed: new baseline, no events
        Assert.Equal(2, Events(598));
        Assert.Equal(0, Events(100));   // jump of 498 > MaxDelta: new aircraft
        Assert.Equal(1, Events(99));
        Assert.Equal(7, Run(node, 0.01, new Signal(99))[1].X); // total
    }

    [Fact]
    public void CounterDelta_IncreaseModeCountsIl2FireEvents()
    {
        var node = new CounterDeltaToEventNode { Direction = CounterDirection.Increase };
        int Events(double counter) => Run(node, 0.01, new Signal(counter))[0].EventCount;
        Assert.Equal(0, Events(0));
        Assert.Equal(2, Events(2));
        Assert.Equal(0, Events(0));     // counter reset (new source)
        Assert.Equal(1, Events(1));
    }

    [Fact]
    public void Envelope_AttackHoldDecayShape()
    {
        var node = new EnvelopeNode { Attack = 0.02, Hold = 0.02, Decay = 0.04, Peak = 0.8 };
        var first = Run(node, 0.01, Signal.Events(1));
        Assert.Equal(0, first[0].X, 9);          // t = 0, start of attack
        Assert.Equal(1, first[1].EventCount);    // pulse started
        Assert.Equal(0.4, Run(node, 0.01, Signal.Zero)[0].X, 9);   // t = .01
        Assert.Equal(0.8, Run(node, 0.01, Signal.Zero)[0].X, 9);   // t = .02 hold
        Assert.Equal(0.8, Run(node, 0.01, Signal.Zero)[0].X, 9);   // t = .03
        Assert.Equal(0.8, Run(node, 0.01, Signal.Zero)[0].X, 9);   // t = .04 decay starts
        Assert.Equal(0.6, Run(node, 0.01, Signal.Zero)[0].X, 9);   // t = .05
        for (var i = 0; i < 2; i++)
            Run(node, 0.01, Signal.Zero);
        Assert.Equal(0, Run(node, 0.01, Signal.Zero)[0].X, 9);     // t = .08 done
    }

    [Fact]
    public void Envelope_QueuesEventsSoEveryEventGetsItsOwnPulse()
    {
        var node = new EnvelopeNode { Hold = 0.02, Decay = 0 };
        var pulses = 0;
        var levels = new List<double>();
        for (var tick = 0; tick < 30; tick++)
        {
            var output = Run(node, 0.01, tick == 0 ? Signal.Events(3) : Signal.Zero);
            pulses += output[1].EventCount;
            levels.Add(output[0].X);
        }

        Assert.Equal(3, pulses);
        var risingEdges = levels.Where((level, i) => level > 0 && (i == 0 || levels[i - 1] == 0)).Count();
        Assert.Equal(3, risingEdges);
    }

    [Fact]
    public void Envelope_WithoutQueueRetriggers()
    {
        var node = new EnvelopeNode { Hold = 0.1, Decay = 0, Queue = false };
        Run(node, 0.01, Signal.Events(1));
        for (var i = 0; i < 5; i++)
            Run(node, 0.01, Signal.Zero);
        var retrigger = Run(node, 0.01, Signal.Events(2));
        Assert.Equal(1, retrigger[1].EventCount);
        Assert.Equal(1, retrigger[0].X);
    }

    [Fact]
    public void Threshold_HasHysteresis()
    {
        var node = new ThresholdNode { On = 0.6, Off = 0.3 };
        Assert.Equal(0, Run(node, 0.01, new Signal(0.5))[0].X);
        var on = Run(node, 0.01, new Signal(0.6));
        Assert.Equal(1, on[0].X);
        Assert.Equal(1, on[1].EventCount);
        Assert.Equal(1, Run(node, 0.01, new Signal(0.4))[0].X);     // still on between Off and On
        var off = Run(node, 0.01, new Signal(0.29));
        Assert.Equal(0, off[0].X);
        Assert.Equal(1, off[2].EventCount);
    }

    [Fact]
    public void Curve_IsSymmetricForSignedInputs()
    {
        var node = new CurveNode();
        node.Curve.Nodes.Clear();
        node.Curve.Nodes.Add(new BezierNode(new CurvePoint(0, 0), new CurvePoint(0, 0), new CurvePoint(0.67, 0)));
        node.Curve.Nodes.Add(new BezierNode(new CurvePoint(1, 1), new CurvePoint(0.87, 0.112), new CurvePoint(1, 1)));
        var positive = Run1(node, 0, 0.8);
        var negative = Run1(node, 0, -0.8);
        Assert.InRange(positive, 0.01, 0.8);
        Assert.Equal(-positive, negative, 12);
        Assert.Equal(1, Run1(node, 0, 1), 6);

        node.Symmetric = false;
        Assert.Equal(0, Run1(node, 0, -0.8), 9);
    }

    [Fact]
    public void BezierCurve_LinearDefaultAndNodeEditing()
    {
        var curve = new BezierCurve();
        Assert.Equal(0.5, curve.Evaluate(0.5), 3);
        Assert.Equal(1, curve.Evaluate(5), 1e-6);   // clamped input; bisection with 24 steps
        var index = curve.AddNode(new CurvePoint(0.5, 0.9));
        Assert.Equal(1, index);
        Assert.Equal(0.9, curve.Evaluate(0.5), 3);
        Assert.False(curve.RemoveNode(0));
        Assert.True(curve.RemoveNode(1));
    }

    [Fact]
    public void Maths_Nodes()
    {
        Assert.Equal(-0.5, Run(new AddNode(), 0, new Signal(-1), new Signal(0.5))[0].X);
        Assert.Equal(-0.5, Run(new MultiplyNode(), 0, new Signal(-1), new Signal(0.5))[0].X);
        Assert.Equal(-1, Run(new MinNode(), 0, new Signal(-1), new Signal(0.5))[0].X);
        Assert.Equal(0.5, Run(new MaxNode(), 0, new Signal(-1), new Signal(0.5))[0].X);
        Assert.Equal(0.7, Run1(new ScaleOffsetNode { Scale = 0.5, Offset = 0.2 }, 0, 1), 9);
        Assert.Equal(-1, Run1(new ClampNode(), 0, -3));
        Assert.Equal(0.25, Run1(new ClampNode { Minimum = 0.25, Maximum = 0.75 }, 0, 0));
        Assert.Equal(3, Run1(new AbsNode(), 0, -3));
        Assert.Equal(2, Run(new SelectNode(), 0, new Signal(1), new Signal(2), new Signal(0.5))[0].X);
        Assert.Equal(1, Run(new SelectNode(), 0, new Signal(1), new Signal(2), new Signal(0.49))[0].X);

        var vec = Run(new MakeVec2Node(), 0, new Signal(0.3), new Signal(-0.4))[0];
        Assert.Equal(new Signal(0.3, -0.4), vec);
        var split = Run(new SplitVec2Node(), 0, vec);
        Assert.Equal(0.3, split[0].X);
        Assert.Equal(-0.4, split[1].X);
        Assert.Equal(0.5, split[2].X, 9);
    }
}
