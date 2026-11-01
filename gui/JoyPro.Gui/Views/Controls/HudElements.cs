using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Views.Controls;

// The root of one HUD widget: puts the widget's design (HudWidget.Look: its own style or the HUD's) into its resources,
// so every element below reads colours, sizes and shapes with DynamicResource. Listens to the widget only while it is
// shown (no leak when the devices are read again).
public sealed class HudWidgetHost : Grid
{
    private HudWidget? observed;
    private HudLook? applied;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Observe(DataContext as HudWidget);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Observe(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot != null)
            Observe(DataContext as HudWidget);
    }

    private void Observe(HudWidget? widget)
    {
        if (ReferenceEquals(widget, observed))
        {
            Apply();
            return;
        }
        if (observed != null)
            observed.PropertyChanged -= OnWidgetChanged;
        observed = widget;
        if (observed != null)
            observed.PropertyChanged += OnWidgetChanged;
        Apply();
    }

    private void OnWidgetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HudWidget.Look))
            Apply();
    }

    private void Apply()
    {
        HudLook? look = observed?.Look;
        if (look == null || ReferenceEquals(look, applied))
            return;
        applied = look;
        look.ApplyTo(Resources);
    }
}

// The stick element: where the stick is (a dot, a pipper), its twist, drawn in one of the shapes of HudLook.GimbalShapes.
// Drawn directly (no child controls, pens made when a colour changes, never per frame).
public sealed class HudGimbalView : Control
{
    public static readonly StyledProperty<double> XProperty = AvaloniaProperty.Register<HudGimbalView, double>(nameof(X));
    public static readonly StyledProperty<double> YProperty = AvaloniaProperty.Register<HudGimbalView, double>(nameof(Y));
    public static readonly StyledProperty<double> TwistProperty = AvaloniaProperty.Register<HudGimbalView, double>(nameof(Twist));
    public static readonly StyledProperty<bool> ShowTwistProperty = AvaloniaProperty.Register<HudGimbalView, bool>(nameof(ShowTwist));
    public static readonly StyledProperty<string> ShapeProperty = AvaloniaProperty.Register<HudGimbalView, string>(nameof(Shape), "Ring");
    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<HudGimbalView, IBrush?>(nameof(Accent));
    public static readonly StyledProperty<IBrush?> SoftProperty = AvaloniaProperty.Register<HudGimbalView, IBrush?>(nameof(Soft));
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<HudGimbalView, IBrush?>(nameof(Track));

    private static readonly IBrush Faint = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#1FFFFFFF"));
    private static readonly IBrush Well = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#14FFFFFF"));
    private static readonly IPen WhitePen = new Avalonia.Media.Immutable.ImmutablePen(Brushes.White, 2);
    private IPen? ringPen, trackPen, faintPen, accentPen, tickPen;

    static HudGimbalView()
    {
        AffectsRender<HudGimbalView>(XProperty, YProperty, TwistProperty, ShowTwistProperty, ShapeProperty, AccentProperty, SoftProperty, TrackProperty);
    }

    // -1..1 each (the stick element's position; twist -1..1 = -90..90 degrees of the tick).
    public double X { get => GetValue(XProperty); set => SetValue(XProperty, value); }
    public double Y { get => GetValue(YProperty); set => SetValue(YProperty, value); }
    public double Twist { get => GetValue(TwistProperty); set => SetValue(TwistProperty, value); }
    public bool ShowTwist { get => GetValue(ShowTwistProperty); set => SetValue(ShowTwistProperty, value); }
    public string Shape { get => GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public IBrush? Soft { get => GetValue(SoftProperty); set => SetValue(SoftProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AccentProperty || change.Property == SoftProperty || change.Property == TrackProperty)
            ringPen = trackPen = faintPen = accentPen = tickPen = null;
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;
        IBrush accent = Accent ?? Brushes.DeepSkyBlue;
        ringPen ??= new Avalonia.Media.Immutable.ImmutablePen((Soft ?? accent).ToImmutable(), 2);
        trackPen ??= new Avalonia.Media.Immutable.ImmutablePen((Track ?? Faint).ToImmutable(), 1);
        faintPen ??= new Avalonia.Media.Immutable.ImmutablePen((Track ?? Faint).ToImmutable(), 0.75);
        IBrush well = Track ?? Well;
        accentPen ??= new Avalonia.Media.Immutable.ImmutablePen(accent.ToImmutable(), 2);
        tickPen ??= new Avalonia.Media.Immutable.ImmutablePen(accent.ToImmutable(), 4, lineCap: PenLineCap.Round);
        double size = Math.Min(w, h), r = size / 2 - 2;
        Point c = new(w / 2, h / 2);
        double travel = r * 0.8;
        Point dot = new(c.X + Math.Clamp(X, -1, 1) * travel, c.Y + Math.Clamp(Y, -1, 1) * travel);
        switch (Shape)
        {
            case "Square":
                context.DrawRectangle(well, ringPen, new Rect(c.X - r, c.Y - r, 2 * r, 2 * r), 4, 4);
                context.DrawRectangle(null, trackPen, new Rect(c.X - r * 0.36, c.Y - r * 0.36, r * 0.72, r * 0.72));
                context.DrawLine(faintPen, new Point(c.X, c.Y - r + 4), new Point(c.X, c.Y + r - 4));
                context.DrawLine(faintPen, new Point(c.X - r + 4, c.Y), new Point(c.X + r - 4, c.Y));
                context.DrawRectangle(accent, WhitePen, new Rect(dot.X - 6, dot.Y - 6, 12, 12), 2, 2);
                break;
            case "Crosshair":
                context.DrawLine(trackPen, new Point(c.X, c.Y - r), new Point(c.X, c.Y + r));
                context.DrawLine(trackPen, new Point(c.X - r, c.Y), new Point(c.X + r, c.Y));
                for (int i = 1; i <= 4; i++)
                {
                    double t = r * i / 4;
                    context.DrawLine(trackPen, new Point(c.X - 3, c.Y - t), new Point(c.X + 3, c.Y - t));
                    context.DrawLine(trackPen, new Point(c.X - 3, c.Y + t), new Point(c.X + 3, c.Y + t));
                    context.DrawLine(trackPen, new Point(c.X - t, c.Y - 3), new Point(c.X - t, c.Y + 3));
                    context.DrawLine(trackPen, new Point(c.X + t, c.Y - 3), new Point(c.X + t, c.Y + 3));
                }
                context.DrawLine(accentPen, new Point(dot.X - 9, dot.Y), new Point(dot.X - 3, dot.Y));
                context.DrawLine(accentPen, new Point(dot.X + 3, dot.Y), new Point(dot.X + 9, dot.Y));
                context.DrawLine(accentPen, new Point(dot.X, dot.Y - 9), new Point(dot.X, dot.Y - 3));
                context.DrawLine(accentPen, new Point(dot.X, dot.Y + 3), new Point(dot.X, dot.Y + 9));
                context.DrawEllipse(null, accentPen, dot, 5, 5);
                break;
            case "Radar":
                context.DrawEllipse(well, ringPen, c, r, r);
                context.DrawEllipse(null, trackPen, c, r * 0.66, r * 0.66);
                context.DrawEllipse(null, trackPen, c, r * 0.33, r * 0.33);
                context.DrawLine(faintPen, new Point(c.X, c.Y - r), new Point(c.X, c.Y + r));
                context.DrawLine(faintPen, new Point(c.X - r, c.Y), new Point(c.X + r, c.Y));
                context.DrawLine(trackPen, c, dot);
                context.DrawEllipse(accent, null, dot, 6, 6);
                break;
            case "Dot":
                context.DrawEllipse(well, null, c, r, r);
                context.DrawEllipse(accent, null, dot, 9, 9);
                break;
            default:
                context.DrawEllipse(well, ringPen, c, r, r);
                context.DrawEllipse(null, trackPen, c, r * 0.36, r * 0.36);
                context.DrawLine(faintPen, new Point(c.X, c.Y - r + 6), new Point(c.X, c.Y + r - 6));
                context.DrawLine(faintPen, new Point(c.X - r + 6, c.Y), new Point(c.X + r - 6, c.Y));
                context.DrawEllipse(accent, WhitePen, dot, 7, 7);
                break;
        }
        if (ShowTwist && Shape != "Crosshair")
        {
            double angle = Math.Clamp(Twist, -1, 1) * Math.PI / 2;
            double sin = Math.Sin(angle), cos = Math.Cos(angle);
            context.DrawLine(tickPen, new Point(c.X + sin * (r - 9), c.Y - cos * (r - 9)), new Point(c.X + sin * (r - 2), c.Y - cos * (r - 2)));
        }
    }
}

// An axis bar (horizontal, bipolar from the middle or from the left) or a lever (vertical, from the bottom), in one of
// the shapes of HudLook.BarShapes. Drawn directly, nothing allocated per frame.
public sealed class HudBarView : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<HudBarView, double>(nameof(Value), 0.5);
    public static readonly StyledProperty<bool> BipolarProperty = AvaloniaProperty.Register<HudBarView, bool>(nameof(Bipolar));
    public static readonly StyledProperty<bool> VerticalProperty = AvaloniaProperty.Register<HudBarView, bool>(nameof(Vertical));
    public static readonly StyledProperty<string> ShapeProperty = AvaloniaProperty.Register<HudBarView, string>(nameof(Shape), "Rounded");
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<HudBarView, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<HudBarView, IBrush?>(nameof(Track));

    private static readonly IBrush Centre = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#80FFFFFF"));
    private IPen? outlinePen, thinPen;

    static HudBarView()
    {
        AffectsRender<HudBarView>(ValueProperty, BipolarProperty, VerticalProperty, ShapeProperty, FillProperty, TrackProperty);
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool Bipolar { get => GetValue(BipolarProperty); set => SetValue(BipolarProperty, value); }
    public bool Vertical { get => GetValue(VerticalProperty); set => SetValue(VerticalProperty, value); }
    public string Shape { get => GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FillProperty || change.Property == TrackProperty)
            outlinePen = thinPen = null;
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;
        IBrush fill = Fill ?? Brushes.DeepSkyBlue, track = Track ?? Brushes.Gray;
        double v = Math.Clamp(double.IsNaN(Value) ? 0.5 : Value, 0, 1);
        double length = Vertical ? h : w, thick = Vertical ? w : h;
        // The filled part along the bar: [from, to] in 0..length (bipolar: from the middle).
        double from = Bipolar ? Math.Min(v, 0.5) * length : 0;
        double to = Bipolar ? Math.Max(v, 0.5) * length : v * length;
        if (to - from < 2)
            to = Math.Min(length, from + 2);
        Rect Along(double a, double b, double inset = 0) => Vertical
            ? new Rect(inset, h - b, w - 2 * inset, Math.Max(0, b - a))
            : new Rect(a, inset, Math.Max(0, b - a), h - 2 * inset);
        switch (Shape)
        {
            case "Square":
                context.DrawRectangle(track, null, new Rect(0, 0, w, h));
                context.DrawRectangle(fill, null, Along(from, to));
                break;
            case "Thin":
            {
                thinPen ??= new Avalonia.Media.Immutable.ImmutablePen(track.ToImmutable(), 2);
                double mid = thick / 2;
                if (Vertical)
                    context.DrawLine(thinPen, new Point(mid, 0), new Point(mid, h));
                else
                    context.DrawLine(thinPen, new Point(0, mid), new Point(w, mid));
                Rect line = Along(from, to, Math.Max(0, mid - 1.5));
                context.DrawRectangle(fill, null, line);
                double end = Bipolar && v < 0.5 ? from : to;
                Point knob = Vertical ? new Point(mid, h - end) : new Point(end, mid);
                context.DrawEllipse(fill, null, knob, Math.Max(3, thick / 2), Math.Max(3, thick / 2));
                break;
            }
            case "Segmented":
            {
                int count = Vertical ? 12 : 14;
                double gap = 2, seg = (length - gap * (count - 1)) / count;
                for (int i = 0; i < count; i++)
                {
                    double a = i * (seg + gap), b = a + seg, centre = (a + b) / 2;
                    bool on = centre >= from && centre <= to;
                    context.DrawRectangle(on ? fill : track, null, Along(a, b), 1, 1);
                }
                break;
            }
            case "Outline":
                outlinePen ??= new Avalonia.Media.Immutable.ImmutablePen(fill.ToImmutable(), 1);
                context.DrawRectangle(null, outlinePen, new Rect(0.5, 0.5, w - 1, h - 1), 2, 2);
                context.DrawRectangle(fill, null, Along(Math.Max(2, from), Math.Min(length - 2, to), 2), 1, 1);
                break;
            default:
            {
                double radius = thick / 2;
                context.DrawRectangle(track, null, new Rect(0, 0, w, h), radius, radius);
                context.DrawRectangle(fill, null, Along(from, to), radius, radius);
                break;
            }
        }
        if (Bipolar)
        {
            double mid = length / 2;
            context.DrawRectangle(Centre, null, Vertical ? new Rect(0, mid - 1, w, 2) : new Rect(mid - 1, 0, 2, h));
        }
    }
}
