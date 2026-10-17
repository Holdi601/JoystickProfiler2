using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Views.Controls;

// A small device picture (Start page and Settings > Devices cards): the picture the Layout page shows for the device
// (the user's layout or photo, the maker's photo) when there is one, else JoyPro's drawing of the model, cropped to the
// drawn device so it fills the box.
public sealed class DeviceThumbnail : Control
{
    public static readonly StyledProperty<IImage?> PictureProperty =
        AvaloniaProperty.Register<DeviceThumbnail, IImage?>(nameof(Picture));

    public static readonly StyledProperty<DeviceSchematic?> SchematicProperty =
        AvaloniaProperty.Register<DeviceThumbnail, DeviceSchematic?>(nameof(Schematic));

    private readonly SchematicPainter painter = new();

    static DeviceThumbnail()
    {
        AffectsRender<DeviceThumbnail>(PictureProperty, SchematicProperty);
    }

    public IImage? Picture
    {
        get => GetValue(PictureProperty);
        set => SetValue(PictureProperty, value);
    }

    public DeviceSchematic? Schematic
    {
        get => GetValue(SchematicProperty);
        set => SetValue(SchematicProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;
        if (Picture is { } image && image.Size.Width > 0 && image.Size.Height > 0)
        {
            double scale = System.Math.Min(bounds.Width / image.Size.Width, bounds.Height / image.Size.Height);
            Size size = new(image.Size.Width * scale, image.Size.Height * scale);
            Rect target = new(new Point((bounds.Width - size.Width) / 2, (bounds.Height - size.Height) / 2), size);
            context.DrawImage(image, new Rect(image.Size), target);
            return;
        }
        if (Schematic is { Width: > 0, Height: > 0 } schematic)
        {
            // Cropped tight to the drawn device, in the bright palette of small pictures.
            Rect target = painter.FitContent(schematic, bounds);
            if (target.Width <= 0)
                return;
            bool dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ||
                        (ActualThemeVariant != Avalonia.Styling.ThemeVariant.Light && Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
            using (context.PushClip(bounds))
                painter.Draw(context, schematic, target, dark, thumbnail: true);
        }
    }
}
