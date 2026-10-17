namespace JoyPro.Ffb.Core.Graph;

/// <summary>A wire from an output port to an input port, by node id and port name.</summary>
public sealed record FfbConnection(string FromNode, string FromPort, string ToNode, string ToPort);

public enum ConnectResult
{
    Ok,
    UnknownNode,
    UnknownPort,
    TypeMismatch,
    Cycle
}

/// <summary>The blueprint graph: nodes, wires, and whether the user switched it on.</summary>
/// <remarks>
/// Ported from the Forza SignalGraph (node list, connection list, Enabled, Connect/RemoveNode). New: wires go between
/// named ports, an input holds at most one wire (connecting again replaces it) unless it is a multi input
/// (<see cref="PortSpec.Multi"/>: Group and effect nodes take any number, as in the Forza graph), and a wire that would
/// close a loop is refused here, at edit time. Graphs that still contain a loop (hand-edited files) are broken safely by
/// <see cref="TopologicalOrder"/>.
/// </remarks>
public sealed class FfbGraph
{
    private readonly List<FfbNode> _nodes = [];
    private readonly List<FfbConnection> _connections = [];

    public string Name { get; set; } = "";

    /// <summary>The user's switch. Never true after loading (see FfbGraphPersistence).</summary>
    public bool Enabled { get; set; }

    public IReadOnlyList<FfbNode> Nodes => _nodes;
    public IReadOnlyList<FfbConnection> Connections => _connections;

    public T Add<T>(T node) where T : FfbNode
    {
        ArgumentNullException.ThrowIfNull(node);
        if (string.IsNullOrWhiteSpace(node.Id) || FindNode(node.Id) is not null)
            node.Id = UniqueId();
        _nodes.Add(node);
        return node;
    }

    public FfbNode? FindNode(string id) => _nodes.FirstOrDefault(node => node.Id == id);

    public bool RemoveNode(string id)
    {
        var removed = _nodes.RemoveAll(node => node.Id == id) > 0;
        _connections.RemoveAll(c => c.FromNode == id || c.ToNode == id);
        return removed;
    }

    public ConnectResult TryConnect(string fromNode, string fromPort, string toNode, string toPort)
    {
        var check = Check(fromNode, fromPort, toNode, toPort);
        if (check != ConnectResult.Ok)
            return check;

        if (IsMultiInput(toNode, toPort))
            _connections.RemoveAll(c => c.ToNode == toNode && c.ToPort == toPort && c.FromNode == fromNode && c.FromPort == fromPort);
        else
            _connections.RemoveAll(c => c.ToNode == toNode && c.ToPort == toPort);
        _connections.Add(new FfbConnection(fromNode, fromPort, toNode, toPort));
        return ConnectResult.Ok;
    }

    public ConnectResult TryConnect(FfbNode from, string fromPort, FfbNode to, string toPort) =>
        TryConnect(from.Id, fromPort, to.Id, toPort);

    /// <summary>Connects or throws (for code that builds graphs; the editor uses <see cref="TryConnect(string,string,string,string)"/>).</summary>
    public void Connect(FfbNode from, string fromPort, FfbNode to, string toPort)
    {
        var result = TryConnect(from.Id, fromPort, to.Id, toPort);
        if (result != ConnectResult.Ok)
            throw new InvalidOperationException($"Cannot connect {from.Title}.{fromPort} -> {to.Title}.{toPort}: {result}");
    }

    /// <summary>Removes every wire into an input.</summary>
    public bool Disconnect(string toNode, string toPort) =>
        _connections.RemoveAll(c => c.ToNode == toNode && c.ToPort == toPort) > 0;

    /// <summary>Removes one wire (a multi input keeps its other wires).</summary>
    public bool Disconnect(FfbConnection connection) => _connections.Remove(connection);

    /// <summary>True when the input takes several wires.</summary>
    public bool IsMultiInput(string nodeId, string port)
    {
        var node = FindNode(nodeId);
        var index = node?.InputIndex(port) ?? -1;
        return index >= 0 && node!.Inputs[index].Multi;
    }

    /// <summary>What <see cref="TryConnect(string,string,string,string)"/> would answer, without changing anything.</summary>
    public ConnectResult Check(string fromNode, string fromPort, string toNode, string toPort)
    {
        var from = FindNode(fromNode);
        var to = FindNode(toNode);
        if (from is null || to is null)
            return ConnectResult.UnknownNode;

        var output = from.OutputIndex(fromPort);
        var input = to.InputIndex(toPort);
        if (output < 0 || input < 0)
            return ConnectResult.UnknownPort;

        if (!PortTypes.CanConnect(from.Outputs[output].Type, to.Inputs[input].Type))
            return ConnectResult.TypeMismatch;

        // The new wire closes a loop when "to" already reaches "from" (the wire it would replace does not count; a
        // multi input replaces nothing).
        var ignore = to.Inputs[input].Multi ? ("", "") : (toNode, toPort);
        if (fromNode == toNode || Reaches(toNode, fromNode, ignore))
            return ConnectResult.Cycle;

        return ConnectResult.Ok;
    }

    /// <summary>Adds a wire without any check (persistence of legacy files and tests of the runtime cycle breaker).</summary>
    internal void AddConnectionUnchecked(FfbConnection connection) => _connections.Add(connection);

    /// <summary>Evaluation order: every node after the nodes feeding it.</summary>
    /// <param name="broken">Wires ignored to break loops (their inputs read the default value).</param>
    /// <remarks>Kahn's algorithm in node-list order, so the order is deterministic. When only loops remain, the first
    /// remaining node is taken and its wires from unevaluated nodes are cut.</remarks>
    public IReadOnlyList<FfbNode> TopologicalOrder(out IReadOnlyList<FfbConnection> broken)
    {
        var cut = new List<FfbConnection>();
        var valid = ValidConnections().ToList();
        var done = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<FfbNode>(_nodes.Count);
        var remaining = _nodes.ToList();

        while (remaining.Count > 0)
        {
            var progressed = false;
            for (var i = 0; i < remaining.Count; i++)
            {
                var node = remaining[i];
                if (valid.Any(c => c.ToNode == node.Id && !done.Contains(c.FromNode)))
                    continue;

                order.Add(node);
                done.Add(node.Id);
                remaining.RemoveAt(i);
                progressed = true;
                break;
            }

            if (progressed)
                continue;

            var forced = remaining[0];
            foreach (var wire in valid.Where(c => c.ToNode == forced.Id && !done.Contains(c.FromNode)).ToList())
            {
                cut.Add(wire);
                valid.Remove(wire);
            }
        }

        broken = cut;
        return order;
    }

    /// <summary>Wires whose nodes and ports exist and whose types fit; anything else is ignored by the evaluator.</summary>
    internal IEnumerable<FfbConnection> ValidConnections()
    {
        var seenInputs = new HashSet<(string, string)>();
        var seenWires = new HashSet<FfbConnection>();
        foreach (var c in _connections)
        {
            var from = FindNode(c.FromNode);
            var to = FindNode(c.ToNode);
            if (from is null || to is null)
                continue;
            var output = from.OutputIndex(c.FromPort);
            var input = to.InputIndex(c.ToPort);
            if (output < 0 || input < 0 || !PortTypes.CanConnect(from.Outputs[output].Type, to.Inputs[input].Type))
                continue;
            if (to.Inputs[input].Multi ? !seenWires.Add(c) : !seenInputs.Add((c.ToNode, c.ToPort)))
                continue;
            yield return c;
        }
    }

    private bool Reaches(string start, string target, (string Node, string Port) ignore)
    {
        var stack = new Stack<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        stack.Push(start);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == target)
                return true;
            if (!seen.Add(current))
                continue;
            foreach (var c in _connections)
            {
                if (c.FromNode == current && !(c.ToNode == ignore.Node && c.ToPort == ignore.Port))
                    stack.Push(c.ToNode);
            }
        }

        return false;
    }

    private string UniqueId()
    {
        string id;
        do
        {
            id = FfbNode.NewId();
        } while (FindNode(id) is not null);

        return id;
    }
}

/// <summary>Creates an empty node of a kind (persistence, editor palette).</summary>
public static class FfbNodeFactory
{
    public static FfbNode Create(NodeKind kind) => kind switch
    {
        NodeKind.Constant => new ConstantNode(),
        NodeKind.Telemetry => new TelemetryNode(),
        NodeKind.Group => new GroupNode(),
        NodeKind.Add => new AddNode(),
        NodeKind.Multiply => new MultiplyNode(),
        NodeKind.ScaleOffset => new ScaleOffsetNode(),
        NodeKind.Clamp => new ClampNode(),
        NodeKind.Abs => new AbsNode(),
        NodeKind.Min => new MinNode(),
        NodeKind.Max => new MaxNode(),
        NodeKind.Curve => new CurveNode(),
        NodeKind.Select => new SelectNode(),
        NodeKind.LowPass => new LowPassNode(),
        NodeKind.SlewLimit => new SlewLimitNode(),
        NodeKind.Derivative => new DerivativeNode(),
        NodeKind.CounterDeltaToEvent => new CounterDeltaToEventNode(),
        NodeKind.Envelope => new EnvelopeNode(),
        NodeKind.Threshold => new ThresholdNode(),
        NodeKind.MakeVec2 => new MakeVec2Node(),
        NodeKind.SplitVec2 => new SplitVec2Node(),
        NodeKind.ConstantForce => new ConstantForceNode(),
        NodeKind.Spring => new SpringNode(),
        NodeKind.Damper => new DamperNode(),
        NodeKind.Friction => new FrictionNode(),
        NodeKind.Periodic => new PeriodicNode(),
        NodeKind.Invert => new InvertNode(),
        NodeKind.DeviceButton => new DeviceButtonNode(),
        NodeKind.StickPosition => new StickPositionNode(),
        NodeKind.Trim => new TrimNode(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown node kind")
    };
}
