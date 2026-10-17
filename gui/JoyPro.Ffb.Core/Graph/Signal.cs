namespace JoyPro.Ffb.Core.Graph;

/// <summary>What travels over a connection.</summary>
/// <remarks>
/// Scalar: one signed number. Normalised signals live in -1..1 (forces, coefficients 0..1), but raw telemetry
/// (m/s, G, rounds) is allowed before a node normalises it -- the Forza graph clamped every node to 0..1, this one
/// clamps only where a value becomes a force (the effect sinks and the mixer).
/// Vec2: two numbers (x, y), e.g. a stick force direction.
/// Event: the number of events that happened in this tick (0 = none). One shell fired = 1.
/// </remarks>
public enum PortType
{
    Scalar,
    Vec2,
    Event
}

/// <summary>A named, typed port of a node. Unconnected inputs read <see cref="Default"/> (or the node's override).</summary>
/// <param name="Multi">An input that takes any number of wires (Forza: group and output nodes); the node's
/// <see cref="FfbNode.CombineModeFor"/> merges them into one value.</param>
public sealed record PortSpec(string Name, PortType Type, double Default = 0, bool Multi = false)
{
    public static PortSpec Scalar(string name, double defaultValue = 0) => new(name, PortType.Scalar, defaultValue);
    public static PortSpec MultiScalar(string name, double defaultValue = 0) => new(name, PortType.Scalar, defaultValue, Multi: true);
    public static PortSpec Vec2(string name) => new(name, PortType.Vec2);
    public static PortSpec Event(string name) => new(name, PortType.Event);
}

/// <summary>How a multi input (and a Group node) merges its wires. Saved by name in the JSON.</summary>
/// <remarks>The Forza graph's SignalGroupMode: maximum, minimum, average, or the sum clamped to 0..1.</remarks>
public enum CombineMode
{
    Maximum,
    Minimum,
    Average,
    SumClamped
}

public static class CombineModes
{
    /// <summary>Merges <paramref name="count"/> values; no values gives <paramref name="empty"/>.</summary>
    public static double Combine(CombineMode mode, ReadOnlySpan<double> values, double empty = 0)
    {
        if (values.Length == 0)
            return empty;
        var result = mode switch
        {
            CombineMode.Minimum => double.PositiveInfinity,
            CombineMode.Maximum => double.NegativeInfinity,
            _ => 0.0
        };
        foreach (var value in values)
        {
            switch (mode)
            {
                case CombineMode.Minimum: result = Math.Min(result, value); break;
                case CombineMode.Maximum: result = Math.Max(result, value); break;
                default: result += value; break;
            }
        }

        return mode switch
        {
            CombineMode.Average => result / values.Length,
            CombineMode.SumClamped => Math.Clamp(result, 0, 1),
            _ => result
        };
    }
}

/// <summary>One value on a port. Scalars use X, Vec2 uses X and Y, events carry their count in X.</summary>
public readonly record struct Signal(double X, double Y = 0)
{
    public static readonly Signal Zero = new(0, 0);

    public int EventCount => X >= 1 ? (int)Math.Min(int.MaxValue, Math.Floor(X)) : 0;

    public static Signal Events(int count) => new(Math.Max(0, count));

    /// <summary>Non-finite numbers (NaN from a broken packet, infinities) never travel further than one node.</summary>
    public Signal Sanitised() => new(double.IsFinite(X) ? X : 0, double.IsFinite(Y) ? Y : 0);
}

public static class PortTypes
{
    /// <summary>Same type, or an event count read as a number. Scalars never become events: use a Threshold.</summary>
    public static bool CanConnect(PortType from, PortType to) =>
        from == to || (from == PortType.Event && to == PortType.Scalar);
}
