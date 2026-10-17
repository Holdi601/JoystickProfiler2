using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace JoyPro.Gui.Views.Controls;

// Detent mode's preview of one aircraft group: device travel 0..100 % (x, back end to front end) against the game's
// throttle 0..100 % (y), with the device's detent (dashed vertical line) and the afterburner range (shaded band from
// the aircraft's afterburner start).
public sealed class DetentCurveView : Control
{
    public static readonly StyledProperty<IReadOnlyList<Point>?> PointsProperty =
        AvaloniaProperty.Register<DetentCurveView, IReadOnlyList<Point>?>(nameof(Points));

    public static readonly StyledProperty<double> DetentTravelProperty =
        AvaloniaProperty.Register<DetentCurveView, double>(nameof(DetentTravel), double.NaN);

    public static readonly StyledProperty<double> AbStartProperty =
        AvaloniaProperty.Register<DetentCurveView, double>(nameof(AbStart), double.NaN);

    private const double PadLeft = 30;
    private const double PadRight = 8;
    private const double PadTop = 8;
    private const double PadBottom = 18;

    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.Parse("#0F172A"));
    private static readonly IPen FramePen = new Pen(new SolidColorBrush(Color.Parse("#334155")), 1);
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#1E293B")), 1);
    private static readonly IPen IdentityPen = new Pen(new SolidColorBrush(Color.Parse("#475569")), 1, new DashStyle([2, 3], 0));
    private static readonly IPen CurvePen = new Pen(new SolidColorBrush(Color.Parse("#38BDF8")), 2.2, lineJoin: PenLineJoin.Round);
    private static readonly IPen DetentPen = new Pen(new SolidColorBrush(Color.Parse("#F59E0B")), 1.4, new DashStyle([4, 3], 0));
    private static readonly IBrush AbBrush = new SolidColorBrush(Color.Parse("#33F97316"));
    private static readonly IBrush AbTextBrush = new SolidColorBrush(Color.Parse("#FDBA74"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#94A3B8"));
    private static readonly IBrush DetentTextBrush = new SolidColorBrush(Color.Parse("#FCD34D"));
    private static readonly Typeface Font = new(FontFamily.Default);

    static DetentCurveView()
    {
        AffectsRender<DetentCurveView>(PointsProperty, DetentTravelProperty, AbStartProperty);
    }

    public IReadOnlyList<Point>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public double DetentTravel
    {
        get => GetValue(DetentTravelProperty);
        set => SetValue(DetentTravelProperty, value);
    }

    public double AbStart
    {
        get => GetValue(AbStartProperty);
        set => SetValue(AbStartProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 220 : Math.Min(220, availableSize.Width);
        double h = double.IsInfinity(availableSize.Height) ? 150 : Math.Min(150, availableSize.Height);
        return new Size(w, h);
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        context.DrawRectangle(PanelBrush, FramePen, bounds, 6, 6);
        Rect plot = new(PadLeft, PadTop, Math.Max(10, bounds.Width - PadLeft - PadRight), Math.Max(10, bounds.Height - PadTop - PadBottom));
        Point Map(double x, double y) => new(plot.Left + Math.Clamp(x, 0, 1) * plot.Width, plot.Bottom - Math.Clamp(y, 0, 1) * plot.Height);

        double ab = AbStart;
        if (!double.IsNaN(ab) && ab > 0 && ab < 1)
        {
            context.DrawRectangle(AbBrush, null, new Rect(Map(0, 1), Map(1, ab)));
            Label(context, "AB", Map(0, 1) + new Point(3, 1), AbTextBrush, 0);
        }
        for (int i = 1; i < 4; i++)
        {
            double v = i / 4.0;
            context.DrawLine(GridPen, Map(v, 0), Map(v, 1));
            context.DrawLine(GridPen, Map(0, v), Map(1, v));
        }
        context.DrawRectangle(null, FramePen, plot);
        context.DrawLine(IdentityPen, Map(0, 0), Map(1, 1));
        Label(context, "0", new Point(plot.Left, plot.Bottom + 2), LabelBrush, 0.5);
        Label(context, "travel", new Point(plot.Center.X, plot.Bottom + 2), LabelBrush, 0.5);
        Label(context, "100", new Point(plot.Right, plot.Bottom + 2), LabelBrush, 0.5);
        Label(context, "100", new Point(plot.Left - 3, plot.Top - 6), LabelBrush, 1);
        Label(context, "0", new Point(plot.Left - 3, plot.Bottom - 7), LabelBrush, 1);

        double detent = DetentTravel;
        if (!double.IsNaN(detent))
        {
            context.DrawLine(DetentPen, Map(detent, 0), Map(detent, 1));
            Label(context, "detent", Map(detent, 0) + new Point(-3, -14), DetentTextBrush, 1);
        }

        IReadOnlyList<Point>? points = Points;
        if (points != null && points.Count > 1)
        {
            using (context.PushClip(plot.Inflate(2)))
            {
                StreamGeometry geometry = new();
                using (StreamGeometryContext g = geometry.Open())
                {
                    g.BeginFigure(Map(points[0].X, points[0].Y), false);
                    for (int i = 1; i < points.Count; i++)
                        g.LineTo(Map(points[i].X, points[i].Y));
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, CurvePen, geometry);
            }
        }
    }

    private static void Label(DrawingContext context, string text, Point at, IBrush brush, double alignX)
    {
        FormattedText formatted = new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10, brush);
        context.DrawText(formatted, new Point(at.X - formatted.Width * alignX, at.Y));
    }
}

// The calibration wizard's live bar: the raw axis 0..100 % with the device's position and the marks confirmed so far.
public sealed class DetentTravelBar : Control
{
    public static readonly StyledProperty<double> RawProperty =
        AvaloniaProperty.Register<DetentTravelBar, double>(nameof(Raw), double.NaN);

    public static readonly StyledProperty<IReadOnlyList<(string Label, double Raw)>?> MarksProperty =
        AvaloniaProperty.Register<DetentTravelBar, IReadOnlyList<(string Label, double Raw)>?>(nameof(Marks));

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#1E293B"));
    private static readonly IPen FramePen = new Pen(new SolidColorBrush(Color.Parse("#334155")), 1);
    private static readonly IBrush FillBrush = new SolidColorBrush(Color.Parse("#0EA5E9"));
    private static readonly IPen MarkPen = new Pen(new SolidColorBrush(Color.Parse("#F59E0B")), 2);
    private static readonly IBrush MarkText = new SolidColorBrush(Color.Parse("#FCD34D"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#94A3B8"));
    private static readonly Typeface Font = new(FontFamily.Default);

    static DetentTravelBar()
    {
        AffectsRender<DetentTravelBar>(RawProperty, MarksProperty);
    }

    public double Raw
    {
        get => GetValue(RawProperty);
        set => SetValue(RawProperty, value);
    }

    public IReadOnlyList<(string Label, double Raw)>? Marks
    {
        get => GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        return new Size(w, 46);
    }

    public override void Render(DrawingContext context)
    {
        Rect track = new(6, 16, Math.Max(10, Bounds.Width - 12), 14);
        context.DrawRectangle(TrackBrush, FramePen, track, 4, 4);
        double raw = Raw;
        if (!double.IsNaN(raw))
        {
            double x = track.Left + Math.Clamp(raw, 0, 1) * track.Width;
            context.DrawRectangle(FillBrush, null, new Rect(track.Left, track.Top, Math.Max(0, x - track.Left), track.Height), 4, 4);
            context.DrawRectangle(Brushes.White, null, new Rect(x - 1.5, track.Top - 3, 3, track.Height + 6));
        }
        IReadOnlyList<(string Label, double Raw)>? marks = Marks;
        if (marks != null)
        {
            foreach ((string label, double value) in marks)
            {
                double x = track.Left + Math.Clamp(value, 0, 1) * track.Width;
                context.DrawLine(MarkPen, new Point(x, track.Top - 4), new Point(x, track.Bottom + 4));
                FormattedText text = new(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10, MarkText);
                context.DrawText(text, new Point(Math.Clamp(x - text.Width / 2, 0, Math.Max(0, Bounds.Width - text.Width)), 0));
            }
        }
        FormattedText lo = new("0 %", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10, LabelBrush);
        FormattedText hi = new("100 %", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10, LabelBrush);
        context.DrawText(lo, new Point(track.Left, track.Bottom + 3));
        context.DrawText(hi, new Point(track.Right - hi.Width, track.Bottom + 3));
    }
}
