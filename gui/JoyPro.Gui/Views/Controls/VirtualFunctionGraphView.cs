using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using JoyPro.Engine.VJoy;

namespace JoyPro.Gui.Views.Controls;

// What the graph asks the page to do (MainWindowViewModel.VirtualModes.cs): every change goes through the engine.
public interface IVfGraphHost
{
    IReadOnlyList<VfMode> Modes { get; }

    Task<bool> ConnectVirtualFunctionsAsync(string producerId, string outputRole, string consumerId, string inputRole);

    Task<bool> DisconnectVirtualFunctionInputAsync(string consumerId, string inputRole);

    Task RemoveVirtualFunctionAsync(string id);

    void OpenVirtualFunction(string id);
}

// One port of a node: an input (left edge) or an output (right edge).
public sealed class VfGraphPort
{
    public required VfGraphNode Node { get; init; }
    public required string Role { get; init; }
    public required string Label { get; init; }
    public required string Kind { get; init; }
    public required bool IsInput { get; init; }
    public string Detail { get; set; } = string.Empty;
    // Inputs: the function and output they read (chaining), else empty.
    public string SourceId { get; set; } = string.Empty;
    public string SourceRole { get; set; } = string.Empty;
    public Point Anchor { get; set; }
    public Rect Row { get; set; }

    public bool Chained => SourceId.Length > 0;
}

public sealed class VfGraphNode
{
    public required VirtualFunction Function { get; init; }
    public int Column { get; set; }
    public Rect Bounds { get; set; }
    public Rect Close { get; set; }
    public List<VfGraphPort> Inputs { get; } = [];
    public List<VfGraphPort> Outputs { get; } = [];
    public string ModesText { get; set; } = string.Empty;
}

public sealed record VfGraphWire(VfGraphPort From, VfGraphPort To);

// The layout of a game's functions: columns by dependency (a function stands right of the ones it reads), inputs on
// the left of a node, outputs on its right; wires from an output to the inputs that read it. Pure, for the control
// and the tests.
public static class VfGraphLayout
{
    public const double NodeWidth = 236;
    public const double ColumnGap = 64;
    public const double Header = 44;
    public const double RowHeight = 20;
    public const double Top = 12;
    public const double Left = 12;

    public static (List<VfGraphNode> Nodes, List<VfGraphWire> Wires, Size Size) Build(IReadOnlyList<VirtualFunction> functions, IReadOnlyList<VfMode> modes)
    {
        List<VfGraphNode> nodes = functions.Select(f => new VfGraphNode { Function = f }).ToList();
        Dictionary<string, VfGraphNode> byId = nodes.Where(n => n.Function.Id.Length > 0).GroupBy(n => n.Function.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // Column = 1 + the deepest function it reads (loops cannot be stored; a guard keeps it finite anyway).
        Dictionary<VfGraphNode, int> depth = [];
        int Depth(VfGraphNode node, int guard)
        {
            if (depth.TryGetValue(node, out int known))
                return known;
            if (guard > 32)
                return 0;
            int d = 0;
            foreach (string id in VfChain.DependsOn(node.Function))
            {
                if (byId.TryGetValue(id, out VfGraphNode? source) && !ReferenceEquals(source, node))
                    d = Math.Max(d, Depth(source, guard + 1) + 1);
            }
            depth[node] = d;
            return d;
        }
        VfModeSet set = new() { Modes = modes.Select(m => m.Clone()).ToList() };
        set.Normalize();
        double[] columnY = new double[nodes.Count + 1];
        double width = 0;
        double height = 0;
        foreach (VfGraphNode node in nodes)
        {
            node.Column = Depth(node, 0);
            VirtualFunction f = node.Function;
            foreach (VfRoleInfo role in VirtualFunctionCatalog.ExpandInputs(f))
            {
                VfInput? input = f.InputOf(role.Role);
                VfGraphPort port = new() { Node = node, Role = role.Role, Label = role.Label, Kind = role.Kind, IsInput = true };
                if (input != null && VfChain.IsChain(input))
                {
                    port.SourceId = VfChain.FunctionId(input);
                    port.SourceRole = VfChain.Role(input);
                    port.Detail = "← " + port.SourceId + " " + port.SourceRole;
                }
                else if (input != null && input.Input.Length > 0)
                    port.Detail = VfTokens.Describe(input.Input) + " · " + DeviceName(input.Device);
                else
                    port.Detail = role.Optional ? T("(optional)") : T("(not set)");
                node.Inputs.Add(port);
            }
            foreach (VfRoleInfo role in VirtualFunctionCatalog.ExpandOutputs(f))
            {
                VfOutput? output = f.OutputOf(role.Role);
                VfGraphPort port = new() { Node = node, Role = role.Role, Label = role.Label, Kind = VirtualFunctionCatalog.OutputKind(f, role.Role, functions.ToList()), IsInput = false };
                port.Detail = output == null || output.Target.Length == 0 ? string.Empty : (output.VJoyDevice > 0 && !output.Target.StartsWith("XB_", StringComparison.Ordinal) && !output.Target.StartsWith("KEY_", StringComparison.Ordinal) ? "vJoy " + output.VJoyDevice.ToString(CultureInfo.InvariantCulture) + " " : string.Empty) + VfTokens.Describe(output.Target);
                node.Outputs.Add(port);
            }
            if (set.Modes.Count > 0)
                node.ModesText = set.Describe(f, functions.ToList());
            int rows = Math.Max(1, node.Inputs.Count + node.Outputs.Count);
            double nodeHeight = Header + rows * RowHeight + 8;
            double x = Left + node.Column * (NodeWidth + ColumnGap);
            double y = Top + columnY[node.Column];
            columnY[node.Column] += nodeHeight + 16;
            node.Bounds = new Rect(x, y, NodeWidth, nodeHeight);
            node.Close = new Rect(node.Bounds.Right - 22, node.Bounds.Y + 4, 18, 18);
            double rowY = y + Header;
            foreach (VfGraphPort port in node.Inputs.Concat(node.Outputs))
            {
                port.Row = new Rect(x, rowY, NodeWidth, RowHeight);
                port.Anchor = new Point(port.IsInput ? x : x + NodeWidth, rowY + RowHeight / 2);
                rowY += RowHeight;
            }
            width = Math.Max(width, node.Bounds.Right + Left);
            height = Math.Max(height, node.Bounds.Bottom + Top);
        }
        // Outputs that other functions read show it.
        List<VfGraphWire> wires = [];
        foreach (VfGraphPort input in nodes.SelectMany(n => n.Inputs).Where(p => p.Chained))
        {
            if (!byId.TryGetValue(input.SourceId, out VfGraphNode? source))
                continue;
            VfGraphPort? output = source.Outputs.FirstOrDefault(o => string.Equals(o.Role, input.SourceRole, StringComparison.OrdinalIgnoreCase));
            if (output == null)
                continue;
            wires.Add(new VfGraphWire(output, input));
            if (output.Detail.Length == 0)
                output.Detail = "→ " + input.Node.Function.Id;
        }
        return (nodes, wires, new Size(Math.Max(width, 300), Math.Max(height, 160)));
    }

    private static string DeviceName(string device)
    {
        int brace = device.IndexOf('{');
        return brace > 0 ? device[..brace].Trim() : device;
    }

    // A point on the wire's bezier (t 0..1).
    public static Point WirePoint(Point from, Point to, double t)
    {
        double dx = Math.Max(30, Math.Abs(to.X - from.X) / 2);
        Point c1 = new(from.X + dx, from.Y);
        Point c2 = new(to.X - dx, to.Y);
        double u = 1 - t;
        return new Point(
            u * u * u * from.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * to.X,
            u * u * u * from.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * to.Y);
    }
}

// The editable node view of a game's virtual functions (docs/virtual-joystick.md "Function chaining"): drag from an
// output dot (right edge of a node) to an input dot (left edge of another node) to chain them; click a wire to select
// it, then its × (or Delete) disconnects; the × of a node removes the function (the page asks first); a double-click
// opens the function's editor (physical inputs, outputs, settings). Draws only when something changed.
public sealed class VirtualFunctionGraphView : Control
{
    public static readonly StyledProperty<IReadOnlyList<VirtualFunction>?> FunctionsProperty =
        AvaloniaProperty.Register<VirtualFunctionGraphView, IReadOnlyList<VirtualFunction>?>(nameof(Functions));

    public static readonly StyledProperty<IVfGraphHost?> HostProperty =
        AvaloniaProperty.Register<VirtualFunctionGraphView, IVfGraphHost?>(nameof(Host));

    private static readonly IBrush BackgroundBrush = new ImmutableSolidColorBrush(Color.FromRgb(15, 17, 21));
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(29, 32, 38)), 1);
    private static readonly IBrush BodyBrush = new ImmutableSolidColorBrush(Color.FromRgb(31, 35, 42));
    private static readonly IPen OutlinePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(80, 87, 100)), 1);
    private static readonly IPen SelectedNodePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(245, 190, 75)), 1.6);
    private static readonly IBrush HeaderBrush = new ImmutableSolidColorBrush(Color.FromRgb(245, 190, 75));
    private static readonly IBrush OffHeaderBrush = new ImmutableSolidColorBrush(Color.FromRgb(120, 120, 128));
    private static readonly IBrush TitleBrush = new ImmutableSolidColorBrush(Color.FromRgb(15, 17, 21));
    private static readonly IBrush TextBrush = Brushes.White;
    private static readonly IBrush DetailBrush = new ImmutableSolidColorBrush(Color.FromRgb(185, 190, 200));
    private static readonly IBrush InputDot = new ImmutableSolidColorBrush(Color.FromRgb(95, 163, 255));
    private static readonly IBrush OutputDot = new ImmutableSolidColorBrush(Color.FromRgb(108, 203, 95));
    private static readonly IBrush TargetBrush = new ImmutableSolidColorBrush(Color.FromArgb(70, 108, 203, 95));
    private static readonly IBrush DangerBrush = new ImmutableSolidColorBrush(Color.FromRgb(239, 68, 68));
    private static readonly IPen WirePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(220, 108, 203, 95)), 2.2);
    private static readonly IPen SelectedWirePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(245, 190, 75)), 3.4);
    private static readonly IPen DragPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(245, 190, 75)), 2, new ImmutableDashStyle([4, 3], 0));
    private static readonly Typeface Font = new(FontFamily.Default);
    private static readonly Typeface BoldFont = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private List<VfGraphNode> nodes = [];
    private List<VfGraphWire> wires = [];
    private Size content = new(300, 160);
    private VfGraphPort? dragFrom;
    private Point dragPoint;
    private VfGraphPort? dropTarget;

    static VirtualFunctionGraphView()
    {
        AffectsMeasure<VirtualFunctionGraphView>(FunctionsProperty);
        AffectsRender<VirtualFunctionGraphView>(FunctionsProperty);
        FocusableProperty.OverrideDefaultValue<VirtualFunctionGraphView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<VirtualFunctionGraphView>(true);
    }

    public IReadOnlyList<VirtualFunction>? Functions
    {
        get => GetValue(FunctionsProperty);
        set => SetValue(FunctionsProperty, value);
    }

    public IVfGraphHost? Host
    {
        get => GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    public IReadOnlyList<VfGraphNode> Nodes => nodes;

    public IReadOnlyList<VfGraphWire> Wires => wires;

    public VfGraphWire? SelectedWire { get; private set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FunctionsProperty || change.Property == HostProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        (nodes, wires, content) = VfGraphLayout.Build(Functions ?? [], Host?.Modes ?? []);
        SelectedWire = null;
        dragFrom = null;
        dropTarget = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Max(content.Width, double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width), content.Height);

    // ---- interaction (the pointer handlers call these; the tests too)

    public VfGraphPort? PortAt(Point point, bool inputs)
    {
        foreach (VfGraphNode node in nodes)
        {
            foreach (VfGraphPort port in inputs ? node.Inputs : node.Outputs)
            {
                // The row's half on the port's side, or within 10 px of the dot.
                Rect side = port.IsInput ? new Rect(port.Row.X - 8, port.Row.Y, port.Row.Width / 2 + 8, port.Row.Height) : new Rect(port.Row.Center.X, port.Row.Y, port.Row.Width / 2 + 8, port.Row.Height);
                if (side.Contains(point))
                    return port;
            }
        }
        return null;
    }

    public VfGraphWire? WireAt(Point point)
    {
        foreach (VfGraphWire wire in wires)
        {
            for (int i = 0; i <= 24; i++)
            {
                Point p = VfGraphLayout.WirePoint(wire.From.Anchor, wire.To.Anchor, i / 24.0);
                if (Math.Abs(p.X - point.X) <= 6 && Math.Abs(p.Y - point.Y) <= 6)
                    return wire;
            }
        }
        return null;
    }

    private static Rect WireClose(VfGraphWire wire)
    {
        Point mid = VfGraphLayout.WirePoint(wire.From.Anchor, wire.To.Anchor, 0.5);
        return new Rect(mid.X - 9, mid.Y - 9, 18, 18);
    }

    public void PressAt(Point point, int clickCount)
    {
        IVfGraphHost? host = Host;
        if (SelectedWire != null && WireClose(SelectedWire).Contains(point))
        {
            VfGraphWire wire = SelectedWire;
            SelectedWire = null;
            InvalidateVisual();
            _ = host?.DisconnectVirtualFunctionInputAsync(wire.To.Node.Function.Id, wire.To.Role);
            return;
        }
        VfGraphNode? closing = nodes.FirstOrDefault(n => n.Close.Contains(point));
        if (closing != null)
        {
            _ = host?.RemoveVirtualFunctionAsync(closing.Function.Id);
            return;
        }
        VfGraphPort? output = PortAt(point, false);
        if (output != null)
        {
            dragFrom = output;
            dragPoint = point;
            SelectedWire = null;
            InvalidateVisual();
            return;
        }
        VfGraphPort? input = PortAt(point, true);
        VfGraphWire? hit = input != null && input.Chained ? wires.FirstOrDefault(w => ReferenceEquals(w.To, input)) : WireAt(point);
        if (hit != null)
        {
            SelectedWire = hit;
            InvalidateVisual();
            return;
        }
        VfGraphNode? node = nodes.FirstOrDefault(n => n.Bounds.Contains(point));
        if (node != null && clickCount >= 2)
            host?.OpenVirtualFunction(node.Function.Id);
        if (SelectedWire != null)
        {
            SelectedWire = null;
            InvalidateVisual();
        }
    }

    public void MoveTo(Point point)
    {
        if (dragFrom == null)
            return;
        dragPoint = point;
        VfGraphPort? target = PortAt(point, true);
        if (target != null && (ReferenceEquals(target.Node, dragFrom.Node) || !VfTokens.Fits(target.Kind, dragFrom.Kind, true)))
            target = null;
        dropTarget = target;
        InvalidateVisual();
    }

    // Ends a drag: over a fitting input of another function, the two are connected (by the page, through the engine).
    public Task<bool> ReleaseAt(Point point)
    {
        VfGraphPort? from = dragFrom;
        MoveTo(point);
        VfGraphPort? target = dropTarget;
        dragFrom = null;
        dropTarget = null;
        InvalidateVisual();
        if (from == null || target == null || Host == null)
            return Task.FromResult(false);
        return Host.ConnectVirtualFunctionsAsync(from.Node.Function.Id, from.Role, target.Node.Function.Id, target.Role);
    }

    public Task<bool> DisconnectSelected()
    {
        VfGraphWire? wire = SelectedWire;
        SelectedWire = null;
        InvalidateVisual();
        return wire == null || Host == null ? Task.FromResult(false) : Host.DisconnectVirtualFunctionInputAsync(wire.To.Node.Function.Id, wire.To.Role);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        PressAt(e.GetPosition(this), e.ClickCount);
        if (dragFrom != null)
            e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragFrom != null)
            MoveTo(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragFrom == null)
            return;
        e.Pointer.Capture(null);
        _ = ReleaseAt(e.GetPosition(this));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if ((e.Key == Key.Delete || e.Key == Key.Back) && SelectedWire != null)
        {
            _ = DisconnectSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && (dragFrom != null || SelectedWire != null))
        {
            dragFrom = null;
            SelectedWire = null;
            InvalidateVisual();
            e.Handled = true;
        }
    }

    // ---- drawing

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        context.DrawRectangle(BackgroundBrush, null, bounds, 8, 8);
        for (double x = 12; x < bounds.Width; x += 24)
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, bounds.Height));
        for (double y = 12; y < bounds.Height; y += 24)
            context.DrawLine(GridPen, new Point(0, y), new Point(bounds.Width, y));
        if (nodes.Count == 0)
        {
            Text(context, T("No functions for this game yet: add one on the left, or import a Joystick Gremlin profile."), 16, 16, DetailBrush, 12);
            return;
        }

        foreach (VfGraphNode node in nodes)
            DrawNode(context, node);
        foreach (VfGraphWire wire in wires)
        {
            bool selected = ReferenceEquals(wire, SelectedWire);
            Wire(context, wire.From.Anchor, wire.To.Anchor, selected ? SelectedWirePen : WirePen);
            if (selected)
            {
                Rect close = WireClose(wire);
                context.DrawEllipse(DangerBrush, null, close.Center, 9, 9);
                Text(context, "×", close.X + 5, close.Y + 1, TextBrush, 13, bold: true);
            }
        }
        if (dragFrom != null)
        {
            if (dropTarget != null)
                context.DrawRectangle(TargetBrush, null, dropTarget.Row, 3, 3);
            Wire(context, dragFrom.Anchor, dropTarget?.Anchor ?? dragPoint, DragPen);
        }
    }

    private void DrawNode(DrawingContext context, VfGraphNode node)
    {
        VirtualFunction f = node.Function;
        VfTypeInfo? type = VirtualFunctionCatalog.Get(f.Type);
        bool touched = SelectedWire != null && (ReferenceEquals(SelectedWire.From.Node, node) || ReferenceEquals(SelectedWire.To.Node, node));
        context.DrawRectangle(BodyBrush, touched ? SelectedNodePen : OutlinePen, node.Bounds, 6, 6);
        context.DrawRectangle(f.Enabled ? HeaderBrush : OffHeaderBrush, null, new Rect(node.Bounds.X, node.Bounds.Y, node.Bounds.Width, 24), 6, 6);
        string name = f.Name.Length > 0 ? f.Name : type?.Name ?? f.Type;
        Text(context, Fit((type?.Icon ?? "?") + " " + name, VfGraphLayout.NodeWidth - 34), node.Bounds.X + 8, node.Bounds.Y + 4, TitleBrush, 12, bold: true);
        Text(context, "×", node.Close.X + 4, node.Close.Y, TitleBrush, 14, bold: true);
        string sub = f.Id + " · " + (type?.Name ?? f.Type) + (f.Enabled ? string.Empty : " · " + T("off")) + (node.ModesText.Length > 0 ? " · " + node.ModesText : string.Empty);
        Text(context, Fit(sub, VfGraphLayout.NodeWidth - 16), node.Bounds.X + 8, node.Bounds.Y + 27, DetailBrush, 10);
        foreach (VfGraphPort port in node.Inputs)
        {
            context.DrawEllipse(port.Chained ? OutputDot : InputDot, null, port.Anchor, 4.5, 4.5);
            Text(context, Fit(port.Label + ": " + port.Detail, VfGraphLayout.NodeWidth - 18), port.Row.X + 10, port.Row.Y + 3, port.Chained ? TextBrush : DetailBrush, 10.5);
        }
        foreach (VfGraphPort port in node.Outputs)
        {
            context.DrawEllipse(OutputDot, null, port.Anchor, 4.5, 4.5);
            string label = Fit(port.Label + (port.Detail.Length > 0 ? " → " + port.Detail.TrimStart('→', ' ') : string.Empty), VfGraphLayout.NodeWidth - 18);
            FormattedText formatted = Formatted(label, TextBrush, 10.5, false);
            context.DrawText(formatted, new Point(port.Row.Right - 10 - formatted.Width, port.Row.Y + 3));
        }
    }

    private static void Wire(DrawingContext context, Point from, Point to, IPen pen)
    {
        StreamGeometry geometry = new();
        using (StreamGeometryContext g = geometry.Open())
        {
            double dx = Math.Max(30, Math.Abs(to.X - from.X) / 2);
            g.BeginFigure(from, false);
            g.CubicBezierTo(new Point(from.X + dx, from.Y), new Point(to.X - dx, to.Y), to);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private static string Fit(string text, double width)
    {
        int max = Math.Max(4, (int)(width / 5.9));
        return text.Length <= max ? text : text[..Math.Max(1, max - 1)] + "…";
    }

    private static FormattedText Formatted(string text, IBrush brush, double size, bool bold) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? BoldFont : Font, size, brush);

    private static void Text(DrawingContext context, string text, double x, double y, IBrush brush, double size, bool bold = false)
    {
        if (!string.IsNullOrEmpty(text))
            context.DrawText(Formatted(text, brush, size, bold), new Point(x, y));
    }
}
