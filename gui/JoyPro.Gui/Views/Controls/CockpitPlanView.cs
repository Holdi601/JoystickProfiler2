using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Views.Controls;

// A JoyPro drawing (DeviceSchematic) fitted into the control: the Cockpits page's plan of the cockpit around the pilot
// (CockpitSchematic.Plan), drawn with the device drawings' painter in the theme's colours.
public sealed class CockpitPlanView : Control
{
    public static readonly StyledProperty<DeviceSchematic?> SchematicProperty =
        AvaloniaProperty.Register<CockpitPlanView, DeviceSchematic?>(nameof(Schematic));

    private readonly SchematicPainter painter = new();

    static CockpitPlanView()
    {
        AffectsRender<CockpitPlanView>(SchematicProperty);
        AffectsMeasure<CockpitPlanView>(SchematicProperty);
    }

    public DeviceSchematic? Schematic { get => GetValue(SchematicProperty); set => SetValue(SchematicProperty, value); }

    private bool IsDark => ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ||
                           (ActualThemeVariant != Avalonia.Styling.ThemeVariant.Light && Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Schematic is not { Width: > 0, Height: > 0 } s)
            return new Size(0, 0);
        double width = double.IsInfinity(availableSize.Width) ? s.Width : availableSize.Width;
        return new Size(width, width * s.Height / s.Width);
    }

    public override void Render(DrawingContext context)
    {
        if (Schematic is not { Width: > 0, Height: > 0 } s || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        double scale = System.Math.Min(Bounds.Width / s.Width, Bounds.Height / s.Height);
        Rect target = new((Bounds.Width - s.Width * scale) / 2, (Bounds.Height - s.Height * scale) / 2, s.Width * scale, s.Height * scale);
        context.FillRectangle(new SolidColorBrush(IsDark ? Color.Parse("#0B1220") : Color.Parse("#EEF2F7")), target);
        painter.Draw(context, s, target, IsDark);
    }
}
