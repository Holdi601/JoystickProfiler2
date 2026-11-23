using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Views.Controls;

// The axis curve manager's response preview: stick position -1..1 (x) against what the game gets -1..1 (y), with a
// grid, the identity line, the response curve, an optional dashed reference (the inherited base) and the live stick.
// Optional handles (the user curve's or free curve points) can be dragged with the mouse.
public sealed class AxisCurveView : Control
{
    public static readonly StyledProperty<IReadOnlyList<Point>?> CurveProperty =
        AvaloniaProperty.Register<AxisCurveView, IReadOnlyList<Point>?>(nameof(Curve));

    public static readonly StyledProperty<IReadOnlyList<Point>?> ReferenceCurveProperty =
        AvaloniaProperty.Register<AxisCurveView, IReadOnlyList<Point>?>(nameof(ReferenceCurve));

    // NaN: no live position.
    public static readonly StyledProperty<double> StickInputProperty =
        AvaloniaProperty.Register<AxisCurveView, double>(nameof(StickInput), double.NaN);

    public static readonly StyledProperty<double> StickOutputProperty =
        AvaloniaProperty.Register<AxisCurveView, double>(nameof(StickOutput));

    // Draggable points (curve coordinates); dragging one runs HandleMovedCommand with an AxisCurveHandleMove.
    public static readonly StyledProperty<IReadOnlyList<Point>?> HandlesProperty =
        AvaloniaProperty.Register<AxisCurveView, IReadOnlyList<Point>?>(nameof(Handles));

    public static readonly StyledProperty<ICommand?> HandleMovedCommandProperty =
        AvaloniaProperty.Register<AxisCurveView, ICommand?>(nameof(HandleMovedCommand));

    // The numbers follow the unit of the boxes next to the curve: DCS's dialog shows In / Out -100 .. 100, and 0 .. 100 on a
    // slider axis. Only the labels change; the curve's coordinates stay -1..1.
    public static readonly StyledProperty<bool> ShowsPercentProperty =
        AvaloniaProperty.Register<AxisCurveView, bool>(nameof(ShowsPercent));

    public static readonly StyledProperty<bool> OneSidedProperty =
        AvaloniaProperty.Register<AxisCurveView, bool>(nameof(OneSided));

    // Detent mode (2.7.1 report: "make visible in the graph where the afterburner starts"): the device's detent as a
    // curve input (a dashed vertical line) and the afterburner range of the game's throttle as a band from the output
    // where it starts to the output at full; NaN: none.
    public static readonly StyledProperty<double> DetentInputProperty =
        AvaloniaProperty.Register<AxisCurveView, double>(nameof(DetentInput), double.NaN);

    public static readonly StyledProperty<double> AfterburnerOutputProperty =
        AvaloniaProperty.Register<AxisCurveView, double>(nameof(AfterburnerOutput), double.NaN);

    public static readonly StyledProperty<double> FullOutputProperty =
        AvaloniaProperty.Register<AxisCurveView, double>(nameof(FullOutput), double.NaN);

    // 2.7.4: the detent band from the detent line to this curve input (resting there keeps the afterburner off), a
    // faint yellow strip; NaN: none.
    public static readonly StyledProperty<double> DetentBandEndProperty =
        AvaloniaProperty.Register<AxisCurveView, double>(nameof(DetentBandEnd), double.NaN);

    private const double Pad = 22;
    private const double HandleRadius = 4.5;
    private const double GrabDistance = 9;
    private int dragIndex = -1;
    private Cursor? handCursor;

    // The chart colours of the theme (Settings > Appearance > Charts), read when the curve draws.
    private IBrush PanelBrush => JoyPro.Gui.Services.ThemeService.Brush(this, "JP.Chart.PanelBrush", "#0F172A");
    private IPen FramePen => new Pen(JoyPro.Gui.Services.ThemeService.Brush(this, "JP.Chart.GridBrush", "#334155"), 1);
    private IPen GridPen => new Pen(JoyPro.Gui.Services.ThemeService.Brush(this, "JP.Chart.GridBrush", "#1E293B"), 1);
    private IPen AxisPen => new Pen(JoyPro.Gui.Services.ThemeService.Brush(this, "JP.Chart.AxisBrush", "#64748B"), 1);
    private static readonly IPen IdentityPen = new Pen(new SolidColorBrush(Color.Parse("#475569")), 1);
    private IPen CurvePen => new Pen(JoyPro.Gui.Services.ThemeService.Brush(this, "JP.Chart.CurveBrush", "#38BDF8"), 2.2, lineJoin: PenLineJoin.Round);
    private IPen ReferencePen => new Pen(JoyPro.Gui.Services.ThemeService.Brush(this, "JP.Chart.ReferenceBrush", "#F59E0B"), 1.5, new DashStyle([4, 3], 0));
    private static readonly IPen GuidePen = new Pen(new SolidColorBrush(Color.Parse("#99F43F5E")), 1, new DashStyle([2, 2], 0));
    private IBrush DotBrush => JoyPro.Gui.Services.ThemeService.Brush(this, "JP.Chart.PointBrush", "#F43F5E");
    private IBrush LabelBrush => JoyPro.Gui.Services.ThemeService.Brush(this, "JP.Chart.LabelBrush", "#94A3B8");
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#E2E8F0"));
    private static readonly Typeface Font = new(FontFamily.Default);
    private static readonly IBrush HandleBrush = new SolidColorBrush(Color.Parse("#F8FAFC"));
    private static readonly IBrush HandleActiveBrush = new SolidColorBrush(Color.Parse("#FDE68A"));
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.Parse("#0EA5E9")), 1.6);
    private static readonly IBrush AfterburnerBrush = new SolidColorBrush(Color.Parse("#33F97316"));
    private static readonly IPen AfterburnerPen = new Pen(new SolidColorBrush(Color.Parse("#F97316")), 1.2);
    private static readonly IBrush AfterburnerTextBrush = new SolidColorBrush(Color.Parse("#FDBA74"));
    private static readonly IPen DetentPen = new Pen(new SolidColorBrush(Color.Parse("#FDE047")), 1.4, new DashStyle([5, 3], 0));
    private static readonly IBrush DetentTextBrush = new SolidColorBrush(Color.Parse("#FDE047"));
    private static readonly IBrush DetentBandBrush = new SolidColorBrush(Color.Parse("#33FDE047"));

    static AxisCurveView()
    {
        AffectsRender<AxisCurveView>(CurveProperty, ReferenceCurveProperty, StickInputProperty, StickOutputProperty, HandlesProperty, ShowsPercentProperty, OneSidedProperty,
            DetentInputProperty, AfterburnerOutputProperty, FullOutputProperty, DetentBandEndProperty);
    }

    public double DetentInput
    {
        get => GetValue(DetentInputProperty);
        set => SetValue(DetentInputProperty, value);
    }

    public double AfterburnerOutput
    {
        get => GetValue(AfterburnerOutputProperty);
        set => SetValue(AfterburnerOutputProperty, value);
    }

    public double FullOutput
    {
        get => GetValue(FullOutputProperty);
        set => SetValue(FullOutputProperty, value);
    }

    public double DetentBandEnd
    {
        get => GetValue(DetentBandEndProperty);
        set => SetValue(DetentBandEndProperty, value);
    }

    public bool ShowsPercent
    {
        get => GetValue(ShowsPercentProperty);
        set => SetValue(ShowsPercentProperty, value);
    }

    public bool OneSided
    {
        get => GetValue(OneSidedProperty);
        set => SetValue(OneSidedProperty, value);
    }

    // A curve coordinate -1..1 as the curve and the live line say it.
    public static string Number(double value, bool percent, bool oneSided)
    {
        if (!percent)
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        double shown = oneSided ? (value + 1) * 50 : value * 100;
        return Math.Round(shown).ToString("0", CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<Point>? Handles
    {
        get => GetValue(HandlesProperty);
        set => SetValue(HandlesProperty, value);
    }

    public ICommand? HandleMovedCommand
    {
        get => GetValue(HandleMovedCommandProperty);
        set => SetValue(HandleMovedCommandProperty, value);
    }

    public IReadOnlyList<Point>? Curve
    {
        get => GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public IReadOnlyList<Point>? ReferenceCurve
    {
        get => GetValue(ReferenceCurveProperty);
        set => SetValue(ReferenceCurveProperty, value);
    }

    public double StickInput
    {
        get => GetValue(StickInputProperty);
        set => SetValue(StickInputProperty, value);
    }

    public double StickOutput
    {
        get => GetValue(StickOutputProperty);
        set => SetValue(StickOutputProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 300 : Math.Min(300, availableSize.Width);
        double h = double.IsInfinity(availableSize.Height) ? 300 : Math.Min(300, availableSize.Height);
        return new Size(w, h);
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        context.DrawRectangle(PanelBrush, FramePen, bounds, 6, 6);

        Rect plot = PlotRect();
        Point Map(double x, double y) => ToScreen(plot, x, y);

        double afterburner = AfterburnerOutput;
        double full = FullOutput;
        bool hasBand = !double.IsNaN(afterburner) && !double.IsNaN(full) && Math.Abs(full - afterburner) > 1e-6;
        if (hasBand)
            context.DrawRectangle(AfterburnerBrush, null, new Rect(Map(-1, Math.Max(afterburner, full)), Map(1, Math.Min(afterburner, full))));

        for (int i = -4; i <= 4; i++)
        {
            double v = i / 4.0;
            IPen pen = i == 0 ? AxisPen : GridPen;
            context.DrawLine(pen, Map(v, -1), Map(v, 1));
            context.DrawLine(pen, Map(-1, v), Map(1, v));
        }
        context.DrawRectangle(null, FramePen, plot);
        context.DrawLine(IdentityPen, Map(-1, -1), Map(1, 1));

        string low = !ShowsPercent ? "-1" : OneSided ? "0" : "-100";
        string middle = !ShowsPercent ? "0" : OneSided ? "50" : "0";
        string high = ShowsPercent ? "100" : "1";
        DrawLabel(context, low, new Point(plot.Left, plot.Bottom + 3), 0.5);
        DrawLabel(context, middle, new Point(plot.Center.X, plot.Bottom + 3), 0.5);
        DrawLabel(context, high, new Point(plot.Right, plot.Bottom + 3), 0.5);
        DrawLabel(context, high, new Point(plot.Left - 4, plot.Top - 6), 1);
        DrawLabel(context, low, new Point(plot.Left - 4, plot.Bottom - 6), 1);

        if (hasBand)
        {
            context.DrawLine(AfterburnerPen, Map(-1, afterburner), Map(1, afterburner));
            // Inside the band, in the corner away from the curve's full end and the live value's box: top right when full
            // is up, bottom left when it is down.
            FormattedText ab = new(T("afterburner"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 10, AfterburnerTextBrush);
            Point corner = full > afterburner ? Map(1, full) : Map(-1, full);
            context.DrawText(ab, full > afterburner
                ? new Point(corner.X - ab.Width - 4, corner.Y + 2)
                : new Point(corner.X + 4, corner.Y - ab.Height - 2));
        }
        double detent = DetentInput;
        if (!double.IsNaN(detent))
        {
            double bandEnd = DetentBandEnd;
            if (!double.IsNaN(bandEnd) && Math.Abs(bandEnd - detent) > 1e-6)
                context.DrawRectangle(DetentBandBrush, null, new Rect(Map(detent, 1), Map(bandEnd, -1)).Normalize());
            context.DrawLine(DetentPen, Map(detent, -1), Map(detent, 1));
            // At the end of the line away from the band (the band's side is busy).
            FormattedText label = new(T("Detent"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 10, DetentTextBrush);
            bool bandBelow = hasBand && full < afterburner;
            Point end = Map(detent, bandBelow ? 1 : -1);
            double x = Math.Clamp(end.X - label.Width - 3, plot.Left + 2, plot.Right - label.Width - 2);
            context.DrawText(label, new Point(x, bandBelow ? end.Y + 22 : end.Y - label.Height - 2));
        }

        using (context.PushClip(plot.Inflate(2)))
        {
            DrawPolyline(context, ReferenceCurve, ReferencePen, Map);
            DrawPolyline(context, Curve, CurvePen, Map);
        }

        IReadOnlyList<Point>? handles = Handles;
        if (handles != null)
        {
            for (int i = 0; i < handles.Count; i++)
                context.DrawEllipse(i == dragIndex ? HandleActiveBrush : HandleBrush, HandlePen, Map(handles[i].X, handles[i].Y), HandleRadius, HandleRadius);
        }

        double input = StickInput;
        if (!double.IsNaN(input))
        {
            double output = StickOutput;
            Point dot = Map(input, output);
            context.DrawLine(GuidePen, Map(input, -1), dot);
            context.DrawLine(GuidePen, Map(-1, output), dot);
            context.DrawEllipse(DotBrush, new Pen(Brushes.White, 1.5), dot, 5, 5);
            string text = T("in {0} -> out {1}", Number(input, ShowsPercent, OneSided), Number(output, ShowsPercent, OneSided));
            FormattedText formatted = new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 12, TextBrush);
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#CC0F172A")), null, new Rect(plot.X + 4, plot.Y + 4, formatted.Width + 8, formatted.Height + 4), 3, 3);
            context.DrawText(formatted, new Point(plot.X + 8, plot.Y + 6));
        }
    }

    private Rect PlotRect()
    {
        double size = Math.Max(10, Math.Min(Bounds.Width, Bounds.Height) - 2 * Pad);
        return new Rect((Bounds.Width - size) / 2, (Bounds.Height - size) / 2, size, size);
    }

    private static Point ToScreen(Rect plot, double x, double y) => new(
        plot.X + (Math.Clamp(x, -1.1, 1.1) + 1) / 2 * plot.Width,
        plot.Y + (1 - (Math.Clamp(y, -1.1, 1.1) + 1) / 2) * plot.Height);

    private static Point ToCurve(Rect plot, Point screen) => new(
        Math.Clamp((screen.X - plot.X) / plot.Width * 2 - 1, -1, 1),
        Math.Clamp(1 - (screen.Y - plot.Y) / plot.Height * 2, -1, 1));

    // The handle under the pointer (nearest within GrabDistance), -1 for none.
    private int HandleAt(Point screen)
    {
        IReadOnlyList<Point>? handles = Handles;
        if (handles == null)
            return -1;
        Rect plot = PlotRect();
        int best = -1;
        double bestDistance = GrabDistance;
        for (int i = 0; i < handles.Count; i++)
        {
            Point at = ToScreen(plot, handles[i].X, handles[i].Y);
            double distance = Math.Sqrt((at.X - screen.X) * (at.X - screen.X) + (at.Y - screen.Y) * (at.Y - screen.Y));
            if (distance <= bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (HandleMovedCommand == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        int index = HandleAt(e.GetPosition(this));
        if (index < 0)
            return;
        dragIndex = index;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragIndex < 0)
        {
            bool overHandle = HandleMovedCommand != null && HandleAt(e.GetPosition(this)) >= 0;
            if (overHandle != (Cursor == handCursor && handCursor != null))
                Cursor = overHandle ? handCursor ??= new Cursor(StandardCursorType.Hand) : Cursor.Default;
            return;
        }
        Point curve = ToCurve(PlotRect(), e.GetPosition(this));
        AxisCurveHandleMove move = new(dragIndex, curve.X, curve.Y);
        if (HandleMovedCommand?.CanExecute(move) == true)
            HandleMovedCommand.Execute(move);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragIndex < 0)
            return;
        dragIndex = -1;
        e.Pointer.Capture(null);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        dragIndex = -1;
        InvalidateVisual();
    }

    private static void DrawPolyline(DrawingContext context, IReadOnlyList<Point>? points, IPen pen, Func<double, double, Point> map)
    {
        if (points == null || points.Count < 2)
            return;
        StreamGeometry geometry = new();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(map(points[0].X, points[0].Y), false);
            for (int i = 1; i < points.Count; i++)
                g.LineTo(map(points[i].X, points[i].Y));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    // anchorX: 0 = left edge at the point, 0.5 = centred, 1 = right edge at the point.
    private void DrawLabel(DrawingContext context, string text, Point at, double anchorX)
    {
        FormattedText formatted = new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10, LabelBrush);
        context.DrawText(formatted, new Point(at.X - formatted.Width * anchorX, at.Y));
    }
}
