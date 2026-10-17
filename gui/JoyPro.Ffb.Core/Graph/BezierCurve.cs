namespace JoyPro.Ffb.Core.Graph;

public readonly record struct CurvePoint(double X, double Y);

public sealed class BezierNode
{
    public BezierNode(CurvePoint position, CurvePoint inHandle, CurvePoint outHandle)
    {
        Position = position;
        InHandle = inHandle;
        OutHandle = outHandle;
    }

    public CurvePoint Position { get; set; }
    public CurvePoint InHandle { get; set; }
    public CurvePoint OutHandle { get; set; }
}

/// <summary>A piecewise cubic Bezier transfer function on 0..1 -> 0..1.</summary>
/// <remarks>
/// Ported from the Forza app (ForzaHaptics.Tester/BezierCurve.cs) with the same evaluation (bisection on x, 24
/// iterations) and node editing rules; PointF became <see cref="CurvePoint"/> (double, no System.Drawing), and the
/// default is linear instead of inverted. The Curve node handles signed inputs by mirroring (see CurveNode).
/// </remarks>
public sealed class BezierCurve
{
    public BezierCurve()
    {
        ResetLinear();
    }

    public List<BezierNode> Nodes { get; } = [];

    public double Evaluate(double input)
    {
        input = Math.Clamp(double.IsFinite(input) ? input : 0, 0, 1);
        if (Nodes.Count < 2)
            return 0;

        var segment = Nodes.Count - 2;
        for (var index = 0; index < Nodes.Count - 1; index++)
        {
            if (input <= Nodes[index + 1].Position.X)
            {
                segment = index;
                break;
            }
        }

        var start = Nodes[segment];
        var end = Nodes[segment + 1];
        var low = 0.0;
        var high = 1.0;

        for (var iteration = 0; iteration < 24; iteration++)
        {
            var middle = (low + high) / 2.0;
            if (Cubic(start.Position.X, start.OutHandle.X, end.InHandle.X, end.Position.X, middle) < input)
                low = middle;
            else
                high = middle;
        }

        return Math.Clamp(
            Cubic(start.Position.Y, start.OutHandle.Y, end.InHandle.Y, end.Position.Y, (low + high) / 2.0),
            0,
            1);
    }

    public int AddNode(CurvePoint position)
    {
        position = new CurvePoint(Math.Clamp(position.X, 0.01, 0.99), Math.Clamp(position.Y, 0, 1));

        var insertAt = Nodes.FindIndex(node => node.Position.X > position.X);
        if (insertAt <= 0)
            insertAt = Nodes.Count - 1;

        var previous = Nodes[insertAt - 1];
        var next = Nodes[insertAt];
        var spacing = Math.Min(position.X - previous.Position.X, next.Position.X - position.X) / 3.0;
        var slope = (next.Position.Y - previous.Position.Y) / Math.Max(0.001, next.Position.X - previous.Position.X);

        var inHandle = new CurvePoint(position.X - spacing, Math.Clamp(position.Y - slope * spacing, 0, 1));
        var outHandle = new CurvePoint(position.X + spacing, Math.Clamp(position.Y + slope * spacing, 0, 1));

        Nodes.Insert(insertAt, new BezierNode(position, inHandle, outHandle));
        previous.OutHandle = new CurvePoint(Math.Min(previous.OutHandle.X, position.X), previous.OutHandle.Y);
        next.InHandle = new CurvePoint(Math.Max(next.InHandle.X, position.X), next.InHandle.Y);
        return insertAt;
    }

    public bool RemoveNode(int index)
    {
        if (index <= 0 || index >= Nodes.Count - 1)
            return false;

        Nodes.RemoveAt(index);
        return true;
    }

    public void ResetInverted()
    {
        Nodes.Clear();
        Nodes.Add(new BezierNode(new CurvePoint(0, 1), new CurvePoint(0, 1), new CurvePoint(0.15, 1)));
        Nodes.Add(new BezierNode(new CurvePoint(1, 0), new CurvePoint(0.65, 0), new CurvePoint(1, 0)));
    }

    public void ResetLinear()
    {
        Nodes.Clear();
        Nodes.Add(new BezierNode(new CurvePoint(0, 0), new CurvePoint(0, 0), new CurvePoint(0.33, 0.33)));
        Nodes.Add(new BezierNode(new CurvePoint(1, 1), new CurvePoint(0.67, 0.67), new CurvePoint(1, 1)));
    }

    private static double Cubic(double p0, double p1, double p2, double p3, double t)
    {
        var inverse = 1.0 - t;
        return inverse * inverse * inverse * p0 +
               3 * inverse * inverse * t * p1 +
               3 * inverse * t * t * p2 +
               t * t * t * p3;
    }
}
