using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Views.Controls;

// JoyPro's own drawing of a device (DeviceLineArt) as a picture every picture control can show (the Layout page's
// canvas, the callout sheet, the Start page's device cards), painted in the user's drawing style (DrawingStyle, the
// same as the engine's exports): the line style (ink on paper; the "joypro" colours follow the theme) or the
// cel-shaded style (flat colour fills under bold outlines). Vector data, sharp at every zoom.
public sealed class LineArtImage : IImage
{
    private readonly List<(Geometry Geometry, bool Outline, LineArtPath Path)> paths = [];
    private readonly List<(Geometry Geometry, Color Color)> fills = [];

    public LineArtImage(DeviceLineArt art, DrawingStyle? style = null)
    {
        Art = art;
        Style = style ?? DrawingStyle.Default;
        foreach (LineArtPath path in art.Paths)
        {
            try
            {
                paths.Add((StreamGeometry.Parse(path.D), path.IsOutline, path));
            }
            catch (System.Exception)
            {
                // A path the parser does not take is left out; the shipped drawings are checked by the tests.
            }
        }
        foreach (LineArtFill fill in art.Fills)
        {
            try
            {
                fills.Add((StreamGeometry.Parse((fill.EvenOdd ? "F0 " : "F1 ") + fill.D), Color.Parse(fill.Color)));
            }
            catch (System.Exception)
            {
            }
        }
    }

    public DeviceLineArt Art { get; }

    public DrawingStyle Style { get; }

    // null = follow the application's theme (the "joypro" colours).
    public bool? Dark { get; init; }

    // Draw the page behind the drawing.
    public bool Page { get; init; } = true;

    public Size Size => new(Art.Width, Art.Height);

    public bool ShowsFills => Style.IsCel && fills.Count > 0;

    public DrawingColors Colors(bool dark) => Style.Resolve(dark);

    public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
    {
        if (sourceRect.Width <= 0 || sourceRect.Height <= 0 || destRect.Width <= 0 || destRect.Height <= 0)
            return;
        bool dark = Dark ?? Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        DrawingColors colors = Style.Resolve(dark);
        bool cel = colors.Cel && fills.Count > 0;
        if (Page)
            context.FillRectangle(new SolidColorBrush(Color.Parse(colors.Paper)), destRect);
        double sx = destRect.Width / sourceRect.Width, sy = destRect.Height / sourceRect.Height;
        double scale = System.Math.Min(sx, sy);
        IBrush ink = new SolidColorBrush(Color.Parse(colors.StrokeFor(Art.HasFills)));
        IBrush outlineInk = new SolidColorBrush(Color.Parse(colors.StrokeFor(Art.HasFills, outline: true)));
        Matrix transform = Matrix.CreateTranslation(-sourceRect.X, -sourceRect.Y) * Matrix.CreateScale(sx, sy) * Matrix.CreateTranslation(destRect.X, destRect.Y);
        using (context.PushClip(destRect))
        using (context.PushTransform(transform))
        {
            if (cel)
            {
                foreach ((Geometry geometry, Color color) in fills)
                    context.DrawGeometry(new SolidColorBrush(color), null, geometry);
            }
            foreach ((Geometry geometry, bool isOutline, LineArtPath path) in paths)
            {
                // A stroke never thinner than about 0.8 screen pixels: a small card still shows the drawing.
                double width = System.Math.Max(Art.StrokeWidth(path, cel), (isOutline ? 0.9 : 0.75) / scale);
                context.DrawGeometry(null, new Pen(isOutline ? outlineInk : ink, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
            }
        }
    }
}
