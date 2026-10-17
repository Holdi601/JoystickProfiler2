using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using JoyPro.Ffb.Core.Graph;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views.Controls;

// The Bezier curve of the selected Curve node (the Avalonia port of the Forza app's BezierCurveEditor): drag points and
// their handles, double-click to add a point, right-click a point (or select it and press Delete) to remove it. The
// editing rules live in FfbGraphEditorViewModel; this control maps between 0..1 and pixels and draws.
public sealed class FfbCurveEditor : Control
{
    public static readonly StyledProperty<FfbGraphEditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<FfbCurveEditor, FfbGraphEditorViewModel?>(nameof(Editor));

    private const double AnchorRadius = 6;
    private const double HandleRadius = 4.5;
    private const double Left = 30;
    private const double Top = 10;
    private const double Right = 10;
    private const double Bottom = 22;

    private static readonly IBrush PanelBrush = new ImmutableSolidColorBrush(Color.FromRgb(20, 23, 28));
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(52, 57, 66)), 1);
    private static readonly IPen FramePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(125, 132, 145)), 1.2);
    private static readonly IPen HandlePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(110, 120, 140)), 1.2, new ImmutableDashStyle([4, 3], 0));
    private static readonly IPen CurvePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(73, 137, 255)), 3, lineJoin: PenLineJoin.Round);
    private static readonly IPen PointOutlinePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(20, 23, 28)), 1.5);
    private static readonly IBrush HandleBrush = new ImmutableSolidColorBrush(Color.FromRgb(150, 166, 196));
    private static readonly IBrush AnchorBrush = new ImmutableSolidColorBrush(Color.FromRgb(242, 185, 80));
    private static readonly IBrush SelectedBrush = new ImmutableSolidColorBrush(Color.FromRgb(255, 116, 84));
    private static readonly IBrush TextBrush = new ImmutableSolidColorBrush(Color.FromRgb(180, 185, 194));
    private static readonly Typeface Font = new(FontFamily.Default);

    private readonly List<FormattedText> labels = [];
    private FfbGraphEditorViewModel? editor;
    private StreamGeometry? curveGeometry;
    private int geometryVersion = -1;
    private Size geometrySize;
    private Hit drag = Hit.None;

    static FfbCurveEditor()
    {
        FocusableProperty.OverrideDefaultValue<FfbCurveEditor>(true);
        ClipToBoundsProperty.OverrideDefaultValue<FfbCurveEditor>(true);
    }

    private enum HitKind
    {
        None,
        Anchor,
        InHandle,
        OutHandle
    }

    private readonly record struct Hit(HitKind Kind, int Index)
    {
        public static Hit None => new(HitKind.None, -1);
    }

    public FfbGraphEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EditorProperty)
            return;
        if (editor != null)
        {
            editor.Changed -= OnEditorChanged;
            editor.PropertyChanged -= OnEditorPropertyChanged;
        }
        editor = change.GetNewValue<FfbGraphEditorViewModel?>();
        if (editor != null)
        {
            editor.Changed += OnEditorChanged;
            editor.PropertyChanged += OnEditorPropertyChanged;
        }
        geometryVersion = -1;
        InvalidateVisual();
    }

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        if (editor?.SelectedCurve != null)
            InvalidateVisual();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FfbGraphEditorViewModel.SelectedNode) or nameof(FfbGraphEditorViewModel.SelectedCurvePoint))
        {
            geometryVersion = -1;
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 300 : Math.Min(availableSize.Width, 340),
        double.IsInfinity(availableSize.Height) ? 210 : Math.Min(availableSize.Height, 210));

    private Rect Plot => new(Left, Top, Math.Max(10, Bounds.Width - Left - Right), Math.Max(10, Bounds.Height - Top - Bottom));

    private Point ToScreen(CurvePoint point)
    {
        Rect plot = Plot;
        return new Point(plot.Left + point.X * plot.Width, plot.Bottom - point.Y * plot.Height);
    }

    private (double X, double Y) ToCurve(Point screen)
    {
        Rect plot = Plot;
        return (Math.Clamp((screen.X - plot.Left) / plot.Width, 0, 1), Math.Clamp((plot.Bottom - screen.Y) / plot.Height, 0, 1));
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        context.DrawRectangle(PanelBrush, null, bounds, 4, 4);
        Rect plot = Plot;
        for (int i = 1; i < 4; i++)
        {
            double x = plot.Left + plot.Width * i / 4;
            double y = plot.Top + plot.Height * i / 4;
            context.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
        context.DrawRectangle(null, FramePen, plot);
        DrawLabels(context, plot);

        BezierCurve? curve = editor?.SelectedCurve;
        if (curve == null || curve.Nodes.Count < 2)
            return;

        for (int i = 0; i < curve.Nodes.Count - 1; i++)
        {
            context.DrawLine(HandlePen, ToScreen(curve.Nodes[i].Position), ToScreen(curve.Nodes[i].OutHandle));
            context.DrawLine(HandlePen, ToScreen(curve.Nodes[i + 1].Position), ToScreen(curve.Nodes[i + 1].InHandle));
        }

        if (curveGeometry == null || geometryVersion != editor!.Version || geometrySize != Bounds.Size)
        {
            geometryVersion = editor!.Version;
            geometrySize = Bounds.Size;
            curveGeometry = new StreamGeometry();
            using StreamGeometryContext g = curveGeometry.Open();
            g.BeginFigure(ToScreen(curve.Nodes[0].Position), false);
            for (int i = 0; i < curve.Nodes.Count - 1; i++)
                g.CubicBezierTo(ToScreen(curve.Nodes[i].OutHandle), ToScreen(curve.Nodes[i + 1].InHandle), ToScreen(curve.Nodes[i + 1].Position));
            g.EndFigure(false);
        }
        using (context.PushClip(plot.Inflate(2)))
            context.DrawGeometry(null, CurvePen, curveGeometry);

        for (int i = 0; i < curve.Nodes.Count; i++)
        {
            if (i > 0)
                context.DrawEllipse(HandleBrush, PointOutlinePen, ToScreen(curve.Nodes[i].InHandle), HandleRadius, HandleRadius);
            if (i < curve.Nodes.Count - 1)
                context.DrawEllipse(HandleBrush, PointOutlinePen, ToScreen(curve.Nodes[i].OutHandle), HandleRadius, HandleRadius);
        }
        for (int i = 0; i < curve.Nodes.Count; i++)
            context.DrawEllipse(i == editor.SelectedCurvePoint ? SelectedBrush : AnchorBrush, PointOutlinePen, ToScreen(curve.Nodes[i].Position), AnchorRadius, AnchorRadius);
    }

    private void DrawLabels(DrawingContext context, Rect plot)
    {
        if (labels.Count == 0)
        {
            foreach (string text in new[] { "0", "1", "in", "1", "out" })
                labels.Add(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10, TextBrush));
        }
        context.DrawText(labels[0], new Point(plot.Left - 3, plot.Bottom + 4));
        context.DrawText(labels[1], new Point(plot.Right - labels[1].Width, plot.Bottom + 4));
        context.DrawText(labels[2], new Point(plot.Center.X - labels[2].Width / 2, plot.Bottom + 4));
        context.DrawText(labels[3], new Point(plot.Left - 6 - labels[3].Width, plot.Top - 2));
        context.DrawText(labels[4], new Point(plot.Left - 6 - labels[4].Width, plot.Center.Y - labels[4].Height / 2));
    }

    private Hit HitTest(Point point)
    {
        BezierCurve? curve = editor?.SelectedCurve;
        if (curve == null)
            return Hit.None;
        for (int i = 0; i < curve.Nodes.Count; i++)
        {
            if (Distance(ToScreen(curve.Nodes[i].Position), point) <= AnchorRadius * 2)
                return new Hit(HitKind.Anchor, i);
        }
        for (int i = 0; i < curve.Nodes.Count; i++)
        {
            if (i > 0 && Distance(ToScreen(curve.Nodes[i].InHandle), point) <= HandleRadius * 2)
                return new Hit(HitKind.InHandle, i);
            if (i < curve.Nodes.Count - 1 && Distance(ToScreen(curve.Nodes[i].OutHandle), point) <= HandleRadius * 2)
                return new Hit(HitKind.OutHandle, i);
        }
        return Hit.None;
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (editor?.SelectedCurve == null)
            return;
        Focus();
        PointerPoint point = e.GetCurrentPoint(this);
        Hit hit = HitTest(point.Position);
        e.Handled = true;
        if (point.Properties.IsRightButtonPressed)
        {
            if (hit.Kind == HitKind.Anchor)
                editor.RemoveCurvePoint(hit.Index);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2 && hit.Kind == HitKind.None && Plot.Contains(point.Position))
        {
            (double x, double y) = ToCurve(point.Position);
            editor.AddCurvePoint(x, y);
            return;
        }
        if (hit.Kind == HitKind.None)
            return;
        drag = hit;
        editor.SelectedCurvePoint = hit.Index;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (drag.Kind == HitKind.None || editor == null)
            return;
        (double x, double y) = ToCurve(e.GetPosition(this));
        if (drag.Kind == HitKind.Anchor)
            editor.MoveCurveAnchor(drag.Index, x, y);
        else
            editor.MoveCurveHandle(drag.Index, drag.Kind == HitKind.InHandle, x, y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        drag = Hit.None;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        drag = Hit.None;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (editor != null && (e.Key == Key.Delete || e.Key == Key.Back))
        {
            editor.RemoveSelectedCurvePointCommand.Execute(null);
            e.Handled = true;
        }
    }
}
