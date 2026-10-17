using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using JoyPro.Ffb.Core.Graph;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views.Controls;

// The blueprint canvas of the telemetry force feedback editor (the Avalonia port of the Forza app's BlueprintCanvas):
// nodes as boxes with a coloured header, named ports (inputs left, outputs right; square ports take several wires),
// Bezier wires, live values as bars. Pan by dragging the background or with the middle mouse button, zoom with the
// wheel around the pointer. Drag from an output to an input to connect (the graph refuses loops and type mismatches),
// click a wire and press Delete (or right-click it) to remove it.
// Drawing only happens when Avalonia asks for it: nothing runs while the page is hidden. Texts and wire geometries
// are cached per graph version, so a frame allocates nothing but what the drawing context needs.
public sealed class FfbGraphCanvas : Control
{
    public static readonly StyledProperty<FfbGraphEditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<FfbGraphCanvas, FfbGraphEditorViewModel?>(nameof(Editor));

    private const double MinZoom = 0.15;
    private const double MaxZoom = 2.5;
    private const double PortRadius = 5;
    private const double GridStep = 24;

    private static readonly IBrush BackgroundBrush = new ImmutableSolidColorBrush(Color.FromRgb(15, 17, 21));
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(29, 32, 38)), 1);
    private static readonly IBrush BodyBrush = new ImmutableSolidColorBrush(Color.FromRgb(31, 35, 42));
    private static readonly IPen OutlinePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(80, 87, 100)), 1);
    private static readonly IPen SelectedPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(255, 190, 70)), 2.5);
    private static readonly IBrush TitleBrush = Brushes.White;
    private static readonly IBrush DetailBrush = new ImmutableSolidColorBrush(Color.FromRgb(185, 190, 200));
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromRgb(210, 214, 222));
    private static readonly IBrush ScalarBrush = new ImmutableSolidColorBrush(Color.FromRgb(95, 163, 255));
    private static readonly IBrush VectorBrush = new ImmutableSolidColorBrush(Color.FromRgb(108, 203, 95));
    private static readonly IBrush EventBrush = new ImmutableSolidColorBrush(Color.FromRgb(245, 190, 75));
    private static readonly IBrush OkBrush = new ImmutableSolidColorBrush(Color.FromRgb(74, 222, 128));
    private static readonly IBrush RefusedBrush = new ImmutableSolidColorBrush(Color.FromRgb(239, 68, 68));
    private static readonly IPen PortOutlinePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(15, 17, 21)), 1.5);
    private static readonly IPen WirePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(220, 95, 163, 255)), 2.5);
    private static readonly IPen SelectedWirePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(255, 190, 70)), 4);
    private static readonly IPen DragWirePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(245, 190, 75)), 2.5, new ImmutableDashStyle([4, 3], 0));
    private static readonly IBrush BarBackBrush = new ImmutableSolidColorBrush(Color.FromRgb(20, 23, 28));
    private static readonly IBrush BarBrush = new ImmutableSolidColorBrush(Color.FromRgb(56, 189, 248));
    private static readonly IBrush BarNegativeBrush = new ImmutableSolidColorBrush(Color.FromRgb(244, 114, 182));
    private static readonly IBrush HintBrush = new ImmutableSolidColorBrush(Color.FromArgb(200, 148, 163, 184));
    private static readonly Typeface Font = new(FontFamily.Default);
    private static readonly Typeface BoldFont = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private readonly Dictionary<string, NodeVisual> visuals = new(StringComparer.Ordinal);
    private readonly Dictionary<FfbConnection, StreamGeometry> wires = [];
    private readonly Dictionary<(string Node, string Port), ConnectResult> dragChecks = [];
    private readonly List<string> staleIds = [];
    private FfbGraphEditorViewModel? editor;
    private FormattedText? hint;
    private int cacheVersion = -1;
    private double zoom = 1;
    private Vector pan = new(20, 20);
    private bool fitPending = true;
    private Size fittedSize;
    private DragMode dragMode;
    private Point dragStartScreen;
    private Vector panAtDragStart;
    private FfbNode? dragNode;
    private Vector dragNodeOffset;
    private FfbNode? wireFrom;
    private int wireFromPort;
    private Point wirePointer;

    static FfbGraphCanvas()
    {
        FocusableProperty.OverrideDefaultValue<FfbGraphCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<FfbGraphCanvas>(true);
    }

    private enum DragMode
    {
        None,
        Pan,
        Node,
        Wire
    }

    public FfbGraphEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    public double Zoom => zoom;

    public Vector Pan => pan;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EditorProperty)
        {
            if (editor != null)
            {
                editor.Changed -= OnEditorChanged;
                editor.FitRequested -= OnFitRequested;
            }
            editor = change.GetNewValue<FfbGraphEditorViewModel?>();
            if (editor != null)
            {
                editor.Changed += OnEditorChanged;
                editor.FitRequested += OnFitRequested;
            }
            cacheVersion = -1;
            fitPending = true;
            fittedSize = default;
            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty)
        {
            UpdateViewCenter();
        }
    }

    private void OnEditorChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnFitRequested(object? sender, EventArgs e)
    {
        fitPending = true;
        FitToContent();
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height);

    // A loaded graph stays fitted while the canvas changes its size, until the user pans, zooms or moves a node.
    protected override Size ArrangeOverride(Size finalSize)
    {
        Size size = base.ArrangeOverride(finalSize);
        if (fitPending && finalSize != fittedSize && finalSize.Width > 10 && finalSize.Height > 10)
            FitToContent(finalSize);
        return size;
    }

    // Zoom and pan so the whole graph fits (at most 100 %).
    public void FitToContent() => FitToContent(Bounds.Size);

    private void FitToContent(Size size)
    {
        if (editor == null || size.Width < 10 || size.Height < 10)
            return;
        fittedSize = size;
        IReadOnlyList<FfbNode> nodes = editor.Graph.Nodes;
        if (nodes.Count == 0)
        {
            zoom = 1;
            pan = new Vector(20, 20);
        }
        else
        {
            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            foreach (FfbNode node in nodes)
            {
                left = Math.Min(left, node.X);
                top = Math.Min(top, node.Y);
                right = Math.Max(right, node.X + FfbGraphLayout.NodeWidth);
                bottom = Math.Max(bottom, node.Y + FfbGraphLayout.Height(node));
            }
            const double margin = 30;
            double width = right - left + 2 * margin;
            double height = bottom - top + 2 * margin;
            zoom = Math.Clamp(Math.Min(size.Width / width, size.Height / height), MinZoom, 1);
            pan = new Vector(
                (size.Width - (right - left) * zoom) / 2 - left * zoom,
                (size.Height - (bottom - top) * zoom) / 2 - top * zoom);
        }
        UpdateViewCenter();
        InvalidateVisual();
    }

    private void UpdateViewCenter()
    {
        if (editor == null)
            return;
        editor.ViewCenterX = (Bounds.Width / 2 - pan.X) / zoom;
        editor.ViewCenterY = (Bounds.Height / 2 - pan.Y) / zoom;
    }

    private Point ToWorld(Point screen) => new((screen.X - pan.X) / zoom, (screen.Y - pan.Y) / zoom);

    // ---- Drawing ----

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        context.FillRectangle(BackgroundBrush, bounds);
        DrawGrid(context, bounds);
        FfbGraphEditorViewModel? model = editor;
        if (model == null)
            return;
        EnsureCache(model);

        using (context.PushTransform(Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(pan.X, pan.Y)))
        {
            foreach (KeyValuePair<FfbConnection, StreamGeometry> wire in wires)
                context.DrawGeometry(null, wire.Key == model.SelectedConnection ? SelectedWirePen : WirePen, wire.Value);

            if (dragMode == DragMode.Wire && wireFrom != null)
            {
                (double x, double y) = FfbGraphLayout.OutputPort(wireFrom, wireFromPort);
                context.DrawLine(DragWirePen, new Point(x, y), wirePointer);
            }

            foreach (FfbNode node in model.Graph.Nodes)
            {
                if (visuals.TryGetValue(node.Id, out NodeVisual? visual))
                    DrawNode(context, model, node, visual);
            }
        }

        if (model.Graph.Nodes.Count == 0)
        {
            hint ??= new FormattedText(T("Empty graph: add nodes from the list on the left."), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 13, HintBrush);
            context.DrawText(hint, new Point(16, 14));
        }
    }

    private void DrawGrid(DrawingContext context, Rect bounds)
    {
        double step = GridStep * zoom;
        if (step < 8)
            return;
        double startX = pan.X % step;
        double startY = pan.Y % step;
        for (double x = startX; x < bounds.Width; x += step)
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, bounds.Height));
        for (double y = startY; y < bounds.Height; y += step)
            context.DrawLine(GridPen, new Point(0, y), new Point(bounds.Width, y));
    }

    private void DrawNode(DrawingContext context, FfbGraphEditorViewModel model, FfbNode node, NodeVisual visual)
    {
        double height = visual.Height;
        Rect box = new(node.X, node.Y, FfbGraphLayout.NodeWidth, height);
        bool selected = ReferenceEquals(model.SelectedNode, node);
        context.DrawRectangle(BodyBrush, null, box, 6, 6);
        context.DrawRectangle(visual.HeaderBrush, null, new Rect(node.X, node.Y, FfbGraphLayout.NodeWidth, FfbGraphLayout.HeaderHeight), 6, 6);
        context.FillRectangle(visual.HeaderBrush, new Rect(node.X, node.Y + FfbGraphLayout.HeaderHeight - 6, FfbGraphLayout.NodeWidth, 6));
        context.DrawRectangle(null, selected ? SelectedPen : OutlinePen, box, 6, 6);
        context.DrawText(visual.Title, new Point(node.X + 8, node.Y + (FfbGraphLayout.HeaderHeight - visual.Title.Height) / 2));

        for (int i = 0; i < node.Inputs.Count; i++)
        {
            (double x, double y) = FfbGraphLayout.InputPort(node, i);
            PortSpec port = node.Inputs[i];
            IBrush brush = PortBrush(port.Type);
            if (dragMode == DragMode.Wire && dragChecks.TryGetValue((node.Id, port.Name), out ConnectResult check))
                brush = check == ConnectResult.Ok ? OkBrush : RefusedBrush;
            DrawPort(context, new Point(x, y), brush, port.Multi);
            FormattedText label = visual.InputLabels[i];
            context.DrawText(label, new Point(x + 10, y - label.Height / 2));
        }
        for (int i = 0; i < node.Outputs.Count; i++)
        {
            (double x, double y) = FfbGraphLayout.OutputPort(node, i);
            DrawPort(context, new Point(x, y), PortBrush(node.Outputs[i].Type), false);
            FormattedText label = visual.OutputLabels[i];
            context.DrawText(label, new Point(x - 10 - label.Width, y - label.Height / 2));
        }

        double footerTop = node.Y + height - FfbGraphLayout.FooterHeight;
        context.DrawText(visual.Detail, new Point(node.X + 8, footerTop + 1));

        if (model.LiveValues.TryGetValue(node.Id, out double value))
        {
            visual.UpdateValue(value);
            if (visual.Value != null)
                context.DrawText(visual.Value, new Point(node.X + FfbGraphLayout.NodeWidth - 8 - visual.Value.Width, footerTop + 1));
            Rect bar = new(node.X + 8, node.Y + height - 9, FfbGraphLayout.NodeWidth - 16, 4);
            context.FillRectangle(BarBackBrush, bar);
            double amount = Math.Clamp(Math.Abs(value), 0, 1);
            if (amount > 0)
                context.FillRectangle(value < 0 ? BarNegativeBrush : BarBrush, new Rect(bar.X, bar.Y, bar.Width * amount, bar.Height));
        }
    }

    private static void DrawPort(DrawingContext context, Point center, IBrush brush, bool multi)
    {
        if (multi)
            context.DrawRectangle(brush, PortOutlinePen, new Rect(center.X - PortRadius, center.Y - PortRadius - 1, 2 * PortRadius, 2 * PortRadius + 2), 2, 2);
        else
            context.DrawEllipse(brush, PortOutlinePen, center, PortRadius, PortRadius);
    }

    private static IBrush PortBrush(PortType type) => type switch
    {
        PortType.Vec2 => VectorBrush,
        PortType.Event => EventBrush,
        _ => ScalarBrush
    };

    private static IBrush HeaderBrushFor(NodeKind kind) => kind switch
    {
        NodeKind.Constant => HeaderBrushes.Constant,
        NodeKind.Telemetry => HeaderBrushes.Telemetry,
        NodeKind.Group => HeaderBrushes.Group,
        NodeKind.Curve => HeaderBrushes.Curve,
        NodeKind.LowPass or NodeKind.SlewLimit or NodeKind.Derivative or NodeKind.CounterDeltaToEvent or NodeKind.Envelope or NodeKind.Threshold => HeaderBrushes.Stateful,
        NodeKind.MakeVec2 or NodeKind.SplitVec2 => HeaderBrushes.Vector,
        NodeKind.ConstantForce or NodeKind.Spring or NodeKind.Damper or NodeKind.Friction or NodeKind.Periodic => HeaderBrushes.Effect,
        _ => HeaderBrushes.Maths
    };

    private static class HeaderBrushes
    {
        public static readonly IBrush Constant = new ImmutableSolidColorBrush(Color.FromRgb(170, 123, 38));
        public static readonly IBrush Telemetry = new ImmutableSolidColorBrush(Color.FromRgb(43, 112, 92));
        public static readonly IBrush Group = new ImmutableSolidColorBrush(Color.FromRgb(100, 75, 145));
        public static readonly IBrush Curve = new ImmutableSolidColorBrush(Color.FromRgb(51, 101, 174));
        public static readonly IBrush Maths = new ImmutableSolidColorBrush(Color.FromRgb(71, 85, 105));
        public static readonly IBrush Stateful = new ImmutableSolidColorBrush(Color.FromRgb(21, 118, 128));
        public static readonly IBrush Vector = new ImmutableSolidColorBrush(Color.FromRgb(77, 110, 46));
        public static readonly IBrush Effect = new ImmutableSolidColorBrush(Color.FromRgb(157, 77, 50));
    }

    // Short summary of a node's settings for its footer line.
    internal static string Describe(FfbNode node)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        string effect = node is EffectSinkNode sink && sink.Effect.Length > 0 ? T("{0}, slot {1}", sink.Effect, sink.Slot.ToString(c)) : string.Empty;
        return node switch
        {
            ConstantNode constant => constant.Active ? "value " + constant.Value.ToString("0.###", c) : T("inactive (0)"),
            TelemetryNode telemetry => telemetry.Key.Length == 0
                ? T("no key")
                : telemetry.Key + (telemetry.Mode == TelemetryNormalisation.Raw ? " raw" : " " + telemetry.Minimum.ToString("0.##", c) + ".." + telemetry.Maximum.ToString("0.##", c)),
            GroupNode group => group.Mode.ToString(),
            CurveNode curve => curve.Curve.Nodes.Count.ToString(c) + " points" + (curve.Symmetric ? T(", symmetric") : string.Empty),
            ScaleOffsetNode scale => "x" + scale.Scale.ToString("0.###", c) + " + " + scale.Offset.ToString("0.###", c),
            ClampNode clamp => clamp.Minimum.ToString("0.##", c) + " .. " + clamp.Maximum.ToString("0.##", c),
            LowPassNode low => "tau " + low.TimeConstant.ToString("0.###", c) + " s",
            PeriodicNode periodic => T("{0} {1}{2} Hz{3}{4}", periodic.Waveform, periodic.MinFrequencyHz.ToString("0.#", c), (periodic.MaxFrequencyHz.Equals(periodic.MinFrequencyHz) ? string.Empty : "-" + periodic.MaxFrequencyHz.ToString("0.#", c)), (periodic.Pulsed ? " pulsed" : string.Empty), (effect.Length > 0 ? ", " + effect : string.Empty)),
            ConstantForceNode force => "axis " + force.Axis + (effect.Length > 0 ? ", " + effect : string.Empty),
            EffectSinkNode => effect.Length > 0 ? effect : T("no effect name"),
            _ => node.Kind.ToString()
        };
    }

    private void EnsureCache(FfbGraphEditorViewModel model)
    {
        if (cacheVersion == model.Version)
            return;
        cacheVersion = model.Version;

        staleIds.Clear();
        foreach (string id in visuals.Keys)
        {
            if (model.Graph.FindNode(id) == null)
                staleIds.Add(id);
        }
        foreach (string id in staleIds)
            visuals.Remove(id);

        foreach (FfbNode node in model.Graph.Nodes)
        {
            if (!visuals.TryGetValue(node.Id, out NodeVisual? visual) || visual.Kind != node.Kind)
            {
                visual = new NodeVisual(node);
                visuals[node.Id] = visual;
            }
            visual.Update(node);
        }

        wires.Clear();
        foreach (FfbConnection connection in model.Graph.Connections)
        {
            FfbNode? from = model.Graph.FindNode(connection.FromNode);
            FfbNode? to = model.Graph.FindNode(connection.ToNode);
            int output = from?.OutputIndex(connection.FromPort) ?? -1;
            int input = to?.InputIndex(connection.ToPort) ?? -1;
            if (output < 0 || input < 0)
                continue;
            (double x1, double y1) = FfbGraphLayout.OutputPort(from!, output);
            (double x2, double y2) = FfbGraphLayout.InputPort(to!, input);
            double dx = WireBend(x1, x2);
            StreamGeometry geometry = new();
            using (StreamGeometryContext g = geometry.Open())
            {
                g.BeginFigure(new Point(x1, y1), false);
                g.CubicBezierTo(new Point(x1 + dx, y1), new Point(x2 - dx, y2), new Point(x2, y2));
                g.EndFigure(false);
            }
            wires[connection] = geometry;
        }
    }

    private static double WireBend(double x1, double x2) => Math.Max(40, Math.Abs(x2 - x1) / 2);

    // ---- Hit testing (world coordinates) ----

    private FfbNode? NodeAt(Point world)
    {
        if (editor == null)
            return null;
        IReadOnlyList<FfbNode> nodes = editor.Graph.Nodes;
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            FfbNode node = nodes[i];
            if (world.X >= node.X && world.X <= node.X + FfbGraphLayout.NodeWidth && world.Y >= node.Y && world.Y <= node.Y + FfbGraphLayout.Height(node))
                return node;
        }
        return null;
    }

    private (FfbNode Node, int Port)? PortAt(Point world, bool outputs, double radius)
    {
        if (editor == null)
            return null;
        IReadOnlyList<FfbNode> nodes = editor.Graph.Nodes;
        for (int n = nodes.Count - 1; n >= 0; n--)
        {
            FfbNode node = nodes[n];
            int count = outputs ? node.Outputs.Count : node.Inputs.Count;
            for (int i = 0; i < count; i++)
            {
                (double x, double y) = outputs ? FfbGraphLayout.OutputPort(node, i) : FfbGraphLayout.InputPort(node, i);
                double dx = world.X - x;
                double dy = world.Y - y;
                if (dx * dx + dy * dy <= radius * radius)
                    return (node, i);
            }
        }
        return null;
    }

    private FfbConnection? WireAt(Point world)
    {
        if (editor == null)
            return null;
        double tolerance = 6 / zoom;
        foreach (FfbConnection connection in editor.Graph.Connections)
        {
            FfbNode? from = editor.Graph.FindNode(connection.FromNode);
            FfbNode? to = editor.Graph.FindNode(connection.ToNode);
            int output = from?.OutputIndex(connection.FromPort) ?? -1;
            int input = to?.InputIndex(connection.ToPort) ?? -1;
            if (output < 0 || input < 0)
                continue;
            (double x1, double y1) = FfbGraphLayout.OutputPort(from!, output);
            (double x2, double y2) = FfbGraphLayout.InputPort(to!, input);
            double dx = WireBend(x1, x2);
            Point previous = new(x1, y1);
            for (int s = 1; s <= 24; s++)
            {
                double t = s / 24.0;
                double u = 1 - t;
                double bx = u * u * u * x1 + 3 * u * u * t * (x1 + dx) + 3 * u * t * t * (x2 - dx) + t * t * t * x2;
                double by = u * u * u * y1 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y2;
                Point current = new(bx, by);
                if (DistanceToSegment(world, previous, current) <= tolerance)
                    return connection;
                previous = current;
            }
        }
        return null;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        double vx = b.X - a.X;
        double vy = b.Y - a.Y;
        double length = vx * vx + vy * vy;
        double t = length < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / length, 0, 1);
        double cx = a.X + t * vx - p.X;
        double cy = a.Y + t * vy - p.Y;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    // ---- Input ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (editor == null)
            return;
        Focus();
        PointerPoint point = e.GetCurrentPoint(this);
        Point world = ToWorld(point.Position);
        e.Handled = true;

        if (point.Properties.IsMiddleButtonPressed)
        {
            StartPan(e, point.Position);
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            if (WireAt(world) is { } wire)
                editor.RemoveConnection(wire);
            else if (NodeAt(world) is { } clicked)
                editor.SelectedNode = clicked;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (PortAt(world, outputs: true, radius: 10) is { } output)
        {
            wireFrom = output.Node;
            wireFromPort = output.Port;
            wirePointer = world;
            dragChecks.Clear();
            string fromPort = output.Node.Outputs[output.Port].Name;
            foreach (FfbNode target in editor.Graph.Nodes)
            {
                foreach (PortSpec input in target.Inputs)
                    dragChecks[(target.Id, input.Name)] = editor.CheckConnection(output.Node.Id, fromPort, target.Id, input.Name);
            }
            dragMode = DragMode.Wire;
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        if (NodeAt(world) is { } node)
        {
            editor.SelectedNode = node;
            dragNode = node;
            dragNodeOffset = new Vector(world.X - node.X, world.Y - node.Y);
            dragMode = DragMode.Node;
            fitPending = false;
            e.Pointer.Capture(this);
            return;
        }

        if (WireAt(world) is { } connection)
        {
            editor.SelectedConnection = connection;
            return;
        }

        editor.SelectedNode = null;
        editor.SelectedConnection = null;
        StartPan(e, point.Position);
    }

    private void StartPan(PointerPressedEventArgs e, Point screen)
    {
        dragMode = DragMode.Pan;
        fitPending = false;
        dragStartScreen = screen;
        panAtDragStart = pan;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragMode == DragMode.None || editor == null)
            return;
        Point screen = e.GetPosition(this);
        switch (dragMode)
        {
            case DragMode.Pan:
                pan = panAtDragStart + (screen - dragStartScreen);
                UpdateViewCenter();
                InvalidateVisual();
                break;
            case DragMode.Node when dragNode != null:
                Point world = ToWorld(screen);
                editor.MoveNode(dragNode, world.X - dragNodeOffset.X, world.Y - dragNodeOffset.Y);
                break;
            case DragMode.Wire:
                wirePointer = ToWorld(screen);
                InvalidateVisual();
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragMode == DragMode.Wire && wireFrom != null && editor != null)
        {
            Point world = ToWorld(e.GetPosition(this));
            if (PortAt(world, outputs: false, radius: 14) is { } input)
                editor.Connect(wireFrom.Id, wireFrom.Outputs[wireFromPort].Name, input.Node.Id, input.Node.Inputs[input.Port].Name);
            else
                editor.Message = T("Drop the wire on an input port (the left side of a node).");
        }
        EndDrag();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        bool redraw = dragMode == DragMode.Wire;
        dragMode = DragMode.None;
        dragNode = null;
        wireFrom = null;
        if (redraw)
            InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Point screen = e.GetPosition(this);
        Point world = ToWorld(screen);
        double next = Math.Clamp(zoom * Math.Pow(1.15, e.Delta.Y), MinZoom, MaxZoom);
        if (Math.Abs(next - zoom) < 1e-6)
            return;
        zoom = next;
        fitPending = false;
        pan = new Vector(screen.X - world.X * zoom, screen.Y - world.Y * zoom);
        UpdateViewCenter();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (editor != null && (e.Key == Key.Delete || e.Key == Key.Back))
        {
            editor.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    // The cached texts of one node; rebuilt only when what they show changes.
    private sealed class NodeVisual
    {
        private string titleSource = "\0";
        private string detailSource = "\0";
        private double shownValue = double.NaN;

        public NodeVisual(FfbNode node)
        {
            Kind = node.Kind;
            HeaderBrush = HeaderBrushFor(node.Kind);
            InputLabels = new FormattedText[node.Inputs.Count];
            for (int i = 0; i < node.Inputs.Count; i++)
                InputLabels[i] = Small(node.Inputs[i].Name, LabelBrush);
            OutputLabels = new FormattedText[node.Outputs.Count];
            for (int i = 0; i < node.Outputs.Count; i++)
                OutputLabels[i] = Small(node.Outputs[i].Name, LabelBrush);
            Title = Small(string.Empty, TitleBrush);
            Detail = Small(string.Empty, DetailBrush);
        }

        public NodeKind Kind { get; }

        public IBrush HeaderBrush { get; }

        public FormattedText Title { get; private set; }

        public FormattedText Detail { get; private set; }

        public FormattedText[] InputLabels { get; }

        public FormattedText[] OutputLabels { get; }

        public FormattedText? Value { get; private set; }

        public double Height { get; private set; }

        public void Update(FfbNode node)
        {
            Height = FfbGraphLayout.Height(node);
            if (!string.Equals(titleSource, node.Title, StringComparison.Ordinal))
            {
                titleSource = node.Title;
                Title = new FormattedText(node.Title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, BoldFont, 12, TitleBrush)
                {
                    MaxTextWidth = FfbGraphLayout.NodeWidth - 16,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis
                };
            }
            string detail = Describe(node);
            if (!string.Equals(detailSource, detail, StringComparison.Ordinal))
            {
                detailSource = detail;
                Detail = Small(detail, DetailBrush);
                Detail.MaxTextWidth = FfbGraphLayout.NodeWidth - 70;
                Detail.MaxLineCount = 1;
                Detail.Trimming = TextTrimming.CharacterEllipsis;
            }
        }

        // A new text only when the shown number changes (values arrive 5-10 times a second).
        public void UpdateValue(double value)
        {
            double rounded = Math.Round(value, 3);
            if (rounded.Equals(shownValue) && Value != null)
                return;
            shownValue = rounded;
            Value = Small(rounded.ToString("0.000", CultureInfo.InvariantCulture), TitleBrush);
        }

        private static FormattedText Small(string text, IBrush brush) =>
            new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 11, brush);
    }
}
