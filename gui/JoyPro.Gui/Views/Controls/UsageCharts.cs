using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace JoyPro.Gui.Views.Controls;

// The Statistics page's charts (docs/usage-statistics.md#statistics-page). They draw only marks: every number and
// label beside them is a TextBlock in the theme's text colours, so the contrast checks see it and a translation fits.
// One series per chart, in the theme's accent (JP.AccentBrush, light and dark have their own step); thin marks with
// rounded data ends, a 2 px gap between columns and a recessive baseline (JP.BorderBrush).
public abstract class UsageChartBase : Control
{
    protected IBrush Accent => Brush("JP.AccentBrush", Brushes.SteelBlue);

    protected IBrush Track => Brush("JP.SurfaceAltBrush", Brushes.Transparent);

    protected IBrush Baseline => Brush("JP.BorderBrush", Brushes.Gray);

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out object? found) && found is IBrush brush ? brush : fallback;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property.Name == "ActualThemeVariant")
            InvalidateVisual();
    }
}

// One horizontal bar (0..1 of the row's width), its end rounded.
public sealed class UsageBar : UsageChartBase
{
    public static readonly StyledProperty<double> FractionProperty = AvaloniaProperty.Register<UsageBar, double>(nameof(Fraction));

    static UsageBar()
    {
        AffectsRender<UsageBar>(FractionProperty);
    }

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 10);

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        double h = Math.Min(10, bounds.Height);
        double y = (bounds.Height - h) / 2;
        context.DrawRectangle(Track, null, new Rect(0, y, bounds.Width, h), 4, 4);
        double w = Math.Clamp(Fraction, 0, 1) * bounds.Width;
        if (w > 0)
            context.DrawRectangle(Accent, null, new Rect(0, y, Math.Max(4, w), h), 4, 4);
    }
}

// Columns from a baseline (weekdays, hours of the day, months), each column the same width with a 2 px gap.
public sealed class UsageColumns : UsageChartBase
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty = AvaloniaProperty.Register<UsageColumns, IReadOnlyList<double>?>(nameof(Values));

    static UsageColumns()
    {
        AffectsRender<UsageColumns>(ValuesProperty);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        IReadOnlyList<double> values = Values ?? [];
        context.DrawRectangle(Baseline, null, new Rect(0, bounds.Height - 1, bounds.Width, 1));
        if (values.Count == 0)
            return;
        double max = values.Max();
        double slot = bounds.Width / values.Count;
        double gap = Math.Min(2, slot / 4);
        for (int i = 0; i < values.Count; i++)
        {
            if (max <= 0 || values[i] <= 0)
                continue;
            double height = Math.Max(2, (bounds.Height - 2) * values[i] / max);
            Rect column = new(i * slot + gap / 2, bounds.Height - 1 - height, Math.Max(1, slot - gap), height);
            // Rounded at the top only: a rounded rectangle with its lower corners covered by a square foot.
            double r = Math.Min(4, column.Width / 2);
            context.DrawRectangle(Accent, null, column, r, r);
            if (height > r)
                context.DrawRectangle(Accent, null, new Rect(column.X, column.Bottom - r, column.Width, r));
        }
    }
}

// A 2 px line over time (the profile's growth), the first and last point marked.
public sealed class UsageLine : UsageChartBase
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty = AvaloniaProperty.Register<UsageLine, IReadOnlyList<double>?>(nameof(Values));

    static UsageLine()
    {
        AffectsRender<UsageLine>(ValuesProperty);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        IReadOnlyList<double> values = Values ?? [];
        context.DrawRectangle(Baseline, null, new Rect(0, bounds.Height - 1, bounds.Width, 1));
        if (values.Count < 2)
            return;
        double max = Math.Max(1, values.Max());
        double pad = 5;
        Point At(int i) => new(pad + (bounds.Width - 2 * pad) * i / (values.Count - 1), pad + (bounds.Height - 2 * pad) * (1 - values[i] / max));
        Pen pen = new(Accent, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        StreamGeometry line = new();
        using (StreamGeometryContext g = line.Open())
        {
            g.BeginFigure(At(0), false);
            for (int i = 1; i < values.Count; i++)
                g.LineTo(At(i));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, line);
        context.DrawEllipse(Accent, null, At(0), 4, 4);
        context.DrawEllipse(Accent, null, At(values.Count - 1), 4, 4);
    }
}
