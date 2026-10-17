using System;
using Avalonia;
using Avalonia.Controls;

namespace JoyPro.Gui.Views.Controls;

// Places the HUD widgets at their positions in the 1920 x 1080 reference screen (attached Left / Top).
// Fixed: always the whole reference screen (designer preview). Otherwise only as large as the widgets' bounding box,
// whose top-left corner is Origin; the on-screen HUD window sizes and places itself from that.
public sealed class HudPanel : Panel
{
    public static readonly AttachedProperty<double> LeftProperty = AvaloniaProperty.RegisterAttached<HudPanel, Control, double>("Left");
    public static readonly AttachedProperty<double> TopProperty = AvaloniaProperty.RegisterAttached<HudPanel, Control, double>("Top");
    public static readonly StyledProperty<bool> FixedProperty = AvaloniaProperty.Register<HudPanel, bool>(nameof(Fixed));

    static HudPanel()
    {
        AffectsParentMeasure<HudPanel>(LeftProperty, TopProperty);
        AffectsMeasure<HudPanel>(FixedProperty);
    }

    public static double GetLeft(Control control) => control.GetValue(LeftProperty);
    public static void SetLeft(Control control, double value) => control.SetValue(LeftProperty, value);
    public static double GetTop(Control control) => control.GetValue(TopProperty);
    public static void SetTop(Control control, double value) => control.SetValue(TopProperty, value);

    public bool Fixed
    {
        get => GetValue(FixedProperty);
        set => SetValue(FixedProperty, value);
    }

    public Point Origin { get; private set; }

    public event EventHandler? OriginChanged;

    protected override Size MeasureOverride(Size availableSize)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = 0, maxY = 0;
        foreach (Control child in Children)
        {
            child.Measure(Size.Infinity);
            if (!child.IsVisible)
                continue;
            double left = GetLeft(child), top = GetTop(child);
            minX = Math.Min(minX, left);
            minY = Math.Min(minY, top);
            maxX = Math.Max(maxX, left + child.DesiredSize.Width);
            maxY = Math.Max(maxY, top + child.DesiredSize.Height);
        }
        if (Fixed)
        {
            SetOrigin(new Point(0, 0));
            return new Size(Models.HudLayout.ReferenceWidth, Models.HudLayout.ReferenceHeight);
        }
        if (minX == double.MaxValue)
        {
            SetOrigin(new Point(0, 0));
            return new Size(1, 1);
        }
        SetOrigin(new Point(minX, minY));
        return new Size(Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (Control child in Children)
            child.Arrange(new Rect(new Point(GetLeft(child) - Origin.X, GetTop(child) - Origin.Y), child.DesiredSize));
        return finalSize;
    }

    private void SetOrigin(Point origin)
    {
        if (origin == Origin)
            return;
        Origin = origin;
        OriginChanged?.Invoke(this, EventArgs.Empty);
    }
}
