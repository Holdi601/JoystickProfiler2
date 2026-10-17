using JoyPro.Ffb.Core.Output;
using JoyPro.Ffb.Core.Telemetry;

namespace JoyPro.Ffb.Core.Graph;

/// <summary>Runs a graph tick by tick: nodes in topological order, state kept between ticks.</summary>
/// <remarks>
/// Replaces the Forza SignalGraphEvaluator, which recursed through the graph on every packet with a "visiting" set
/// and no state. Here the order is computed once (<see cref="FfbGraph.TopologicalOrder"/>), the wiring is resolved
/// to array indices, and each tick is a flat loop without allocations except the command list.
/// The evaluator uses the node instances it was built from (their state lives there): give it a private copy of
/// the graph (<see cref="FfbGraphPersistence.Clone"/>) when the editor keeps changing the original.
/// </remarks>
public sealed class FfbGraphEvaluator
{
    private readonly FfbNode[] _order;
    private readonly Dictionary<string, int> _indexById;
    private readonly (int Node, int Port)[][][] _wiring;
    private double[] _combine = new double[8];
    private readonly Signal[][] _inputs;
    private readonly Signal[][] _outputs;
    private double? _lastTime;

    public FfbGraphEvaluator(FfbGraph graph)
    {
        _order = graph.TopologicalOrder(out var broken).ToArray();
        BrokenConnections = broken;
        _indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _order.Length; i++)
            _indexById[_order[i].Id] = i;

        var wires = graph.ValidConnections().Except(broken).ToList();
        _wiring = new (int, int)[_order.Length][][];
        _inputs = new Signal[_order.Length][];
        _outputs = new Signal[_order.Length][];
        for (var i = 0; i < _order.Length; i++)
        {
            var node = _order[i];
            _inputs[i] = new Signal[node.Inputs.Count];
            _outputs[i] = new Signal[node.Outputs.Count];
            _wiring[i] = new (int, int)[node.Inputs.Count][];
            for (var p = 0; p < node.Inputs.Count; p++)
            {
                var portName = node.Inputs[p].Name;
                _wiring[i][p] = wires
                    .Where(c => c.ToNode == node.Id && c.ToPort == portName)
                    .Select(c => (_indexById[c.FromNode], _order[_indexById[c.FromNode]].OutputIndex(c.FromPort)))
                    .ToArray();
            }
        }
    }

    /// <summary>Wires cut to break loops (empty for graphs built through <see cref="FfbGraph.TryConnect(string,string,string,string)"/>).</summary>
    public IReadOnlyList<FfbConnection> BrokenConnections { get; }

    public IReadOnlyList<FfbNode> Order => _order;

    /// <summary>Clears all node state and the tick time (after stale telemetry, on enable).</summary>
    public void Reset()
    {
        foreach (var node in _order)
            node.Reset();
        foreach (var outputs in _outputs)
            Array.Clear(outputs);
        _lastTime = null;
    }

    /// <summary>Runs one tick at <paramref name="time"/> (seconds) and returns the effects the sinks asked for.</summary>
    /// <param name="devices">Buttons and the stick position for input and trim nodes (already polled for this tick).</param>
    public List<FfbEffectCommand> Evaluate(ITelemetrySource? telemetry, double time, JoyPro.Ffb.Core.Input.IFfbInputs? devices = null)
    {
        var dt = _lastTime is { } last ? Math.Clamp(time - last, 0, 1) : 0;
        _lastTime = time;
        var context = new FfbEvalContext(telemetry, time, dt, devices);
        var commands = new List<FfbEffectCommand>();

        for (var i = 0; i < _order.Length; i++)
        {
            var node = _order[i];
            var inputs = _inputs[i];
            var wiring = _wiring[i];
            for (var p = 0; p < inputs.Length; p++)
            {
                var sources = wiring[p];
                if (sources.Length == 0)
                {
                    inputs[p] = new Signal(node.DefaultFor(node.Inputs[p]));
                }
                else if (sources.Length == 1)
                {
                    inputs[p] = _outputs[sources[0].Node][sources[0].Port];
                }
                else
                {
                    if (_combine.Length < sources.Length)
                        _combine = new double[sources.Length * 2];
                    for (var w = 0; w < sources.Length; w++)
                        _combine[w] = _outputs[sources[w].Node][sources[w].Port].X;
                    inputs[p] = new Signal(CombineModes.Combine(node.CombineModeFor(p), _combine.AsSpan(0, sources.Length)));
                }
            }

            if (node is EffectSinkNode sink)
            {
                sink.Emit(context, inputs, commands);
                continue;
            }

            var outputs = _outputs[i];
            node.Evaluate(context, inputs, outputs);
            for (var p = 0; p < outputs.Length; p++)
                outputs[p] = outputs[p].Sanitised();
        }

        for (var c = 0; c < commands.Count; c++)
            commands[c] = Sanitise(commands[c]);
        return commands;
    }

    /// <summary>The value the first output of every node had after the last tick (the editor's live view).</summary>
    public void CopyNodeValues(IDictionary<string, double> into)
    {
        for (var i = 0; i < _order.Length; i++)
        {
            var node = _order[i];
            if (_outputs[i].Length > 0)
                into[node.Id] = _outputs[i][0].X;
            else if (_inputs[i].Length > 0)
                into[node.Id] = _inputs[i][0].X;
        }
    }

    /// <summary>The value an output port had after the last tick (editor preview, tests).</summary>
    public Signal GetOutput(string nodeId, string port)
    {
        if (!_indexById.TryGetValue(nodeId, out var index))
            return Signal.Zero;
        var p = _order[index].OutputIndex(port);
        return p < 0 ? Signal.Zero : _outputs[index][p];
    }

    private static FfbEffectCommand Sanitise(FfbEffectCommand command) => command with
    {
        Magnitude = Finite(command.Magnitude),
        Centre = Finite(command.Centre),
        Coefficient = Finite(command.Coefficient),
        Saturation = Finite(command.Saturation),
        FrequencyHz = Finite(command.FrequencyHz)
    };

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
}
