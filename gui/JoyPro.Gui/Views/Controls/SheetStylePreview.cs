using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Views.Controls;

// The colours and fonts of a sheet style on the GUI's canvases (SheetStyle is shared with the engine).
public static class SheetStyleLook
{
    public static Color ToColor(uint argb) => Color.FromUInt32(argb);

    public static Color ColorOf(string? hex, Color fallback) =>
        SheetStyle.TryParse(hex ?? string.Empty, out uint argb) && !string.IsNullOrWhiteSpace(hex) ? Color.FromUInt32(argb) : fallback;

    public static IBrush BrushOf(string? hex, Color fallback) => new SolidColorBrush(ColorOf(hex, fallback));

    // The sheet's font: the style's font first, then the sheet's own (Bahnschrift SemiCondensed) and the language's fallbacks.
    public static FontFamily SheetFont(SheetStyle? style) =>
        SheetFonts.Get((style != null && style.FontFamily.Trim().Length > 0 ? style.FontFamily.Trim().Replace(",", string.Empty) + ", " : string.Empty) +
                       "Bahnschrift SemiCondensed, Bahnschrift, Segoe UI, ");

    // The labels' font on a layout picture: the style's font, else the layout's own.
    public static FontFamily LabelFont(SheetStyle? style, string layoutFont) =>
        SheetFonts.Get((style != null && style.FontFamily.Trim().Length > 0 ? style.FontFamily.Trim().Replace(",", string.Empty) + ", " : string.Empty) +
                       (string.IsNullOrWhiteSpace(layoutFont) ? string.Empty : layoutFont.Trim() + ", ") + "Segoe UI, ");

    public static FontWeight Weight(SheetStyle? style) =>
        style?.NormalizedWeight switch
        {
            "semibold" => FontWeight.SemiBold,
            "bold" => FontWeight.Bold,
            _ => FontWeight.Normal
        };
}

// A small sheet in the style (the style panel's preview and its render test): the page, a picture with a marker of each
// kind and a label, a leader line to a table with its header row, rows with the table text and a modifier column with
// the first modifier's colour.
public sealed class SheetStylePreview : Control
{
    public static readonly StyledProperty<SheetStyle?> StyleValueProperty =
        AvaloniaProperty.Register<SheetStylePreview, SheetStyle?>(nameof(StyleValue));

    static SheetStylePreview()
    {
        AffectsRender<SheetStylePreview>(StyleValueProperty);
    }

    public SheetStyle? StyleValue
    {
        get => GetValue(StyleValueProperty);
        set => SetValue(StyleValueProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 360 : Math.Min(availableSize.Width, 520), 176);

    public override void Render(DrawingContext context)
    {
        SheetStyle style = StyleValue ?? SheetStyle.Preset("joypro");
        bool dark = style.IsDarkPage(true);
        SheetPalette pal = style.Resolve(dark);
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 40 || h < 40)
            return;
        Rect page = new(0, 0, w, h);
        context.DrawRectangle(new SolidColorBrush(SheetStyleLook.ToColor(pal.Page)), new Pen(new SolidColorBrush(SheetStyleLook.ToColor(pal.Border)), 1), page, 6, 6);

        // The picture with a marker of every kind and a label in the label colours.
        Rect picture = new(10, 10, Math.Max(60, w * 0.36), h - 20);
        context.DrawRectangle(new SolidColorBrush(SheetStyleLook.ToColor(pal.PictureBack)), null, picture, 4, 4);
        string[] kinds = ["button", "axis", "pov", "slider"];
        for (int i = 0; i < kinds.Length; i++)
        {
            Point at = new(picture.X + 18 + i * Math.Min(26, (picture.Width - 30) / 4), picture.Y + 20);
            Color c = SheetStyleLook.ColorOf(style.MarkerColor(kinds[i]), Colors.Gray);
            context.DrawEllipse(new SolidColorBrush(c), new Pen(Brushes.Black, 1), at, 7, 7);
        }
        FontFamily labelFont = SheetStyleLook.LabelFont(style, string.Empty);
        double labelSize = Math.Clamp(style.FontSize > 0 ? style.FontSize * 0.75 : 12, 8, 26);
        using (TextLayout label = new(T("Gear up"), new Typeface(labelFont, FontStyle.Normal, SheetStyleLook.Weight(style)), labelSize,
                   SheetStyleLook.BrushOf(style.TextColor, Colors.White), maxWidth: picture.Width - 16))
        {
            Rect box = new(picture.X + 8, picture.Y + 44, Math.Min(picture.Width - 16, label.WidthIncludingTrailingWhitespace + 8), label.Height + 4);
            Color back = SheetStyleLook.ColorOf(style.LabelBackground, Colors.Transparent);
            if (back.A > 0)
                context.DrawRectangle(new SolidColorBrush(back), null, box, 3, 3);
            Color halo = SheetStyleLook.ColorOf(style.OutlineColor, Colors.Transparent);
            if (halo.A > 0)
            {
                using TextLayout shadow = new(T("Gear up"), new Typeface(labelFont, FontStyle.Normal, SheetStyleLook.Weight(style)), labelSize, new SolidColorBrush(halo), maxWidth: picture.Width - 16);
                foreach ((double dx, double dy) in new[] { (-1.0, 0.0), (1.0, 0.0), (0.0, -1.0), (0.0, 1.0) })
                    shadow.Draw(context, new Point(box.X + 4 + dx, box.Y + 2 + dy));
            }
            label.Draw(context, new Point(box.X + 4, box.Y + 2));
        }
        Point spot = new(picture.X + picture.Width * 0.62, picture.Bottom - 34);
        context.DrawEllipse(new SolidColorBrush(SheetStyleLook.ToColor(pal.Line)), new Pen(new SolidColorBrush(SheetStyleLook.ToColor(pal.Border)), 1), spot, 4, 4);

        // A table: header row, rows, the modifier column.
        double tx = picture.Right + 18, tw = w - tx - 10;
        if (tw < 80)
            return;
        double rowH = Math.Clamp(22 * style.TextScale, 18, 34), titleH = Math.Round(rowH * 0.9);
        int rows = 3;
        double th = titleH + rows * rowH;
        Rect table = new(tx, Math.Max(10, (h - th) / 2), tw, th);
        IPen line = new Pen(new SolidColorBrush(SheetStyleLook.ToColor(pal.Line)), 2);
        context.DrawLine(line, spot, new Point(tx - 9, spot.Y));
        context.DrawLine(line, new Point(tx - 9, spot.Y), new Point(tx - 9, table.Y + titleH / 2));
        context.DrawLine(line, new Point(tx - 9, table.Y + titleH / 2), new Point(tx, table.Y + titleH / 2));
        context.FillRectangle(new SolidColorBrush(SheetStyleLook.ToColor(pal.Table)), table);
        context.FillRectangle(new SolidColorBrush(SheetStyleLook.ToColor(pal.Title)), new Rect(table.X, table.Y, table.Width, titleH));
        double modW = Math.Round(tw * 0.34);
        Rect modHead = new(table.Right - modW, table.Y, modW, titleH);
        context.FillRectangle(new SolidColorBrush(SheetStyleLook.ToColor(pal.Mod)), new Rect(modHead.X, table.Y, modW, th));
        string firstModifier = style.Modifiers.Count > 0 ? style.Modifiers[0].Name : string.Empty;
        Color modColor = SheetStyleLook.ColorOf(style.ModifierColor(firstModifier), Colors.Transparent);
        if (modColor.A > 0)
            context.FillRectangle(new SolidColorBrush(modColor), new Rect(modHead.X, modHead.Y, modHead.Width, Math.Max(2, titleH * 0.14)));
        FontFamily font = SheetStyleLook.SheetFont(style);
        Typeface bold = new(font, FontStyle.Normal, FontWeight.Bold), regular = new(font, FontStyle.Normal, SheetStyleLook.Weight(style));
        IBrush titleText = new SolidColorBrush(SheetStyleLook.ToColor(pal.TitleText));
        IBrush text = new SolidColorBrush(SheetStyleLook.ToColor(pal.Text));
        IBrush faint = new SolidColorBrush(SheetStyleLook.ToColor(pal.Faint));
        Text(context, T("TMS hat"), bold, titleH * 0.55, titleText, new Rect(table.X + 6, table.Y, table.Width - modW - 10, titleH));
        Text(context, firstModifier.Length > 0 ? firstModifier : "—", bold, titleH * 0.48, faint, new Rect(modHead.X + 4, modHead.Y, modW - 6, titleH));
        IPen grid = new Pen(new SolidColorBrush(SheetStyleLook.ToColor(pal.Grid)), 1);
        string[] binds = [T("Target lock"), T("Radar mode"), T("Zoom in")];
        string[] mods = [T("Wingman"), string.Empty, T("Zoom out")];
        for (int i = 0; i < rows; i++)
        {
            double y = table.Y + titleH + i * rowH;
            context.DrawLine(grid, new Point(table.X, y), new Point(table.Right, y));
            context.DrawEllipse(text, null, new Point(table.X + rowH * 0.5, y + rowH / 2), rowH * 0.22, rowH * 0.14);
            Text(context, binds[i], regular, rowH * 0.5, text, new Rect(table.X + rowH, y, table.Width - modW - rowH - 4, rowH));
            Text(context, mods[i], regular, rowH * 0.5, text, new Rect(modHead.X + 4, y, modW - 6, rowH));
        }
        context.DrawLine(grid, new Point(modHead.X, table.Y), new Point(modHead.X, table.Bottom));
        context.DrawRectangle(null, new Pen(new SolidColorBrush(SheetStyleLook.ToColor(pal.Border)), 2), table);
    }

    private static void Text(DrawingContext context, string text, Typeface face, double size, IBrush brush, Rect box)
    {
        if (string.IsNullOrEmpty(text) || box.Width < 4)
            return;
        using TextLayout layout = new(text, face, Math.Max(6, size), brush, TextAlignment.Left, TextWrapping.NoWrap, TextTrimming.CharacterEllipsis, maxWidth: box.Width, maxHeight: box.Height);
        layout.Draw(context, new Point(box.X, box.Y + (box.Height - layout.Height) / 2));
    }
}
