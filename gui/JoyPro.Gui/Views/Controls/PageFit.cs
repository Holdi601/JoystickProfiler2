using System;
using Avalonia;
using Avalonia.Controls;

namespace JoyPro.Gui.Views.Controls;

// The pages inside the page scroller (MainWindow.axaml): a page gets exactly the height of the viewport, as without a
// scroller (its lists keep their own scrolling), unless it needs more (a big text size of Settings > Appearance, a big
// UI scale, a low window): then it gets what it needs and the page scrolls as a whole, instead of being cut off or
// drawn over the bottom bar.
public sealed class PageFit : Decorator
{
    public static readonly StyledProperty<Size> ViewportProperty =
        AvaloniaProperty.Register<PageFit, Size>(nameof(Viewport));

    // The least height a page gets (ThemeService: JoyPro's page height times the text size above 100 %): with big text
    // the fixed parts of a page grow, and its lists keep room instead of being squeezed under them.
    public static readonly StyledProperty<double> MinPageHeightProperty =
        AvaloniaProperty.Register<PageFit, double>(nameof(MinPageHeight));

    static PageFit()
    {
        AffectsMeasure<PageFit>(ViewportProperty, MinPageHeightProperty);
    }

    public double MinPageHeight
    {
        get => GetValue(MinPageHeightProperty);
        set => SetValue(MinPageHeightProperty, value);
    }

    public Size Viewport
    {
        get => GetValue(ViewportProperty);
        set => SetValue(ViewportProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child == null)
            return default;
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : Viewport.Width;
        double height = Viewport.Height > 0 ? Viewport.Height : double.IsFinite(availableSize.Height) ? availableSize.Height : 600;
        if (double.IsFinite(MinPageHeight) && MinPageHeight > height)
            height = MinPageHeight;
        Child.Measure(new Size(width, height));
        double need = Child.DesiredSize.Height;
        if (need > height + 1)
        {
            Child.Measure(new Size(width, need));
            height = Math.Max(height, Child.DesiredSize.Height);
        }
        return new Size(Math.Min(width, Child.DesiredSize.Width > 0 ? Math.Max(Child.DesiredSize.Width, width) : width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
