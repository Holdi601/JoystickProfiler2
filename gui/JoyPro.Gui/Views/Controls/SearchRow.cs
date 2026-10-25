using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace JoyPro.Gui.Views.Controls;

// A search box with its filters on one line: the first child (the box) takes what the others leave, at least
// FirstMinWidth; when that does not fit (a narrow pane, a big text size of Settings > Appearance) the box gets a line of
// its own and the others follow on the next line(s), so nothing overlaps.
public sealed class SearchRow : Panel
{
    public static readonly StyledProperty<double> FirstMinWidthProperty =
        AvaloniaProperty.Register<SearchRow, double>(nameof(FirstMinWidth), 220);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SearchRow, double>(nameof(Spacing), 6);

    static SearchRow()
    {
        AffectsMeasure<SearchRow>(FirstMinWidthProperty, SpacingProperty);
    }

    public double FirstMinWidth
    {
        get => GetValue(FirstMinWidthProperty);
        set => SetValue(FirstMinWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private bool stacked;

    protected override Size MeasureOverride(Size availableSize)
    {
        Control[] visible = Children.Where(c => c.IsVisible).ToArray();
        if (visible.Length == 0)
            return default;
        Size unlimited = new(double.PositiveInfinity, availableSize.Height);
        foreach (Control c in visible.Skip(1))
            c.Measure(unlimited);
        double rest = visible.Skip(1).Sum(c => c.DesiredSize.Width) + Spacing * (visible.Length - 1);
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : rest + FirstMinWidth;
        stacked = width - rest < FirstMinWidth;
        if (!stacked)
        {
            visible[0].Measure(new Size(Math.Max(0, width - rest), availableSize.Height));
            double h = visible.Max(c => c.DesiredSize.Height);
            return new Size(width, h);
        }
        visible[0].Measure(new Size(width, availableSize.Height));
        double height = visible[0].DesiredSize.Height;
        double x = 0, lineHeight = 0, y = height + Spacing;
        foreach (Control c in visible.Skip(1))
        {
            c.Measure(new Size(width, availableSize.Height));
            if (x > 0 && x + c.DesiredSize.Width > width)
            {
                y += lineHeight + Spacing;
                x = 0;
                lineHeight = 0;
            }
            x += c.DesiredSize.Width + Spacing;
            lineHeight = Math.Max(lineHeight, c.DesiredSize.Height);
        }
        return new Size(width, y + lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Control[] visible = Children.Where(c => c.IsVisible).ToArray();
        if (visible.Length == 0)
            return finalSize;
        double width = finalSize.Width;
        if (!stacked)
        {
            double rest = visible.Skip(1).Sum(c => c.DesiredSize.Width) + Spacing * (visible.Length - 1);
            double h = finalSize.Height;
            visible[0].Arrange(new Rect(0, 0, Math.Max(0, width - rest), h));
            double x = Math.Max(0, width - rest) + Spacing;
            foreach (Control c in visible.Skip(1))
            {
                c.Arrange(new Rect(x, (h - c.DesiredSize.Height) / 2, c.DesiredSize.Width, c.DesiredSize.Height));
                x += c.DesiredSize.Width + Spacing;
            }
            return finalSize;
        }
        double first = visible[0].DesiredSize.Height;
        visible[0].Arrange(new Rect(0, 0, width, first));
        double cx = 0, line = 0, cy = first + Spacing;
        foreach (Control c in visible.Skip(1))
        {
            if (cx > 0 && cx + c.DesiredSize.Width > width)
            {
                cy += line + Spacing;
                cx = 0;
                line = 0;
            }
            c.Arrange(new Rect(cx, cy, Math.Min(width, c.DesiredSize.Width), c.DesiredSize.Height));
            cx += c.DesiredSize.Width + Spacing;
            line = Math.Max(line, c.DesiredSize.Height);
        }
        return finalSize;
    }
}
