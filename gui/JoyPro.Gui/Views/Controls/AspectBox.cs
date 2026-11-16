using System;
using Avalonia;
using Avalonia.Controls;

namespace JoyPro.Gui.Views.Controls;

// Gives its child the full width it gets and the height that keeps Aspect (width / height), within MaxBoxHeight: a
// device's callout sheet in the layout window grows with the window and never needs a scroll bar of its own.
public sealed class AspectBox : Decorator
{
    public static readonly StyledProperty<double> AspectProperty =
        AvaloniaProperty.Register<AspectBox, double>(nameof(Aspect), 1.6);

    public static readonly StyledProperty<double> MaxBoxHeightProperty =
        AvaloniaProperty.Register<AspectBox, double>(nameof(MaxBoxHeight), double.PositiveInfinity);

    static AspectBox()
    {
        AffectsMeasure<AspectBox>(AspectProperty, MaxBoxHeightProperty);
    }

    public double Aspect { get => GetValue(AspectProperty); set => SetValue(AspectProperty, value); }

    public double MaxBoxHeight { get => GetValue(MaxBoxHeightProperty); set => SetValue(MaxBoxHeightProperty, value); }

    private Size Wanted(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 800 : available.Width;
        double aspect = Aspect > 0.1 ? Aspect : 1.6;
        double height = MaxBoxHeight > 200 ? Math.Min(width / aspect, MaxBoxHeight) : width / aspect;
        if (!double.IsInfinity(available.Height))
            height = Math.Min(height, Math.Max(available.Height, 200));
        return new Size(width, Math.Max(120, height));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Size size = Wanted(availableSize);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
