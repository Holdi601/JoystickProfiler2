using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using JoyPro.Gui.Services.Help;

namespace JoyPro.Gui.Views.Controls;

// The small drawn pictures of the tour cards (docs/ui/help-and-tours.md, "Mini explainers"): what a joystick axis is,
// what the pretend Xbox controller is, what Export does, Alt+Tab. Plain shapes in the theme's colours, no words inside (the card's
// caption says it in the user's language), drawn only while a card with a picture shows.
public sealed class TourPicture : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<TourPicture, string>(nameof(Kind), string.Empty);

    static TourPicture()
    {
        AffectsRender<TourPicture>(KindProperty);
    }

    public string Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    // The caption under the picture (English, shown through T()).
    public static string Caption(string kind) => kind switch
    {
        TourPictures.Axis => N("Move the stick, and the bar fills. That is an axis."),
        TourPictures.Pad => N("Your stick drives a pretend controller. The game sees the controller."),
        TourPictures.Export => N("Export copies your buttons into the game. Undo brings the old ones back."),
        TourPictures.AltTab => N("Hold Alt, then press Tab. That takes you from the game to JoyPro and back."),
        _ => string.Empty
    };

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width, 320), 96);

    public override void Render(DrawingContext context)
    {
        IBrush accent = Brush("JP.AccentBrush", Brushes.DodgerBlue);
        IBrush ink = Brush("JP.TextBrush", Brushes.White);
        IBrush soft = Brush("JP.AccentSoftBrush", new SolidColorBrush(Color.FromArgb(60, 30, 144, 255)));
        IBrush ok = Brush("JP.OkTextBrush", Brushes.LimeGreen);
        Pen line = new(ink, 3, lineCap: PenLineCap.Round);
        Pen accentLine = new(accent, 4, lineCap: PenLineCap.Round);
        double w = Bounds.Width, h = Bounds.Height;
        double scale = Math.Min(w / 320, h / 96);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((w - 320 * scale) / 2, (h - 96 * scale) / 2)))
        {
            switch (Kind)
            {
                case TourPictures.Axis:
                    DrawAxis(context, line, accentLine, accent, soft);
                    break;
                case TourPictures.Pad:
                    DrawPad(context, line, accentLine, accent, soft);
                    break;
                case TourPictures.Export:
                    DrawExport(context, line, accentLine, accent, soft, ok);
                    break;
                case TourPictures.AltTab:
                    DrawAltTab(context, line, accentLine, accent, soft, ink);
                    break;
            }
        }
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush ? brush : fallback;

    // A stick tilted from its base, its old place faint, and a bar that fills as far as the stick moved.
    private static void DrawAxis(DrawingContext c, Pen line, Pen accentLine, IBrush accent, IBrush soft)
    {
        c.DrawRectangle(soft, line, new Rect(20, 70, 80, 16), 6);
        c.DrawLine(new Pen(line.Brush, 3, new DashStyle([2, 2], 0), PenLineCap.Round), new Point(60, 70), new Point(60, 22));
        c.DrawLine(new Pen(accent, 6, lineCap: PenLineCap.Round), new Point(60, 70), new Point(88, 26));
        c.DrawEllipse(accent, null, new Point(88, 26), 9, 9);
        Arrow(c, accentLine, new Point(66, 14), new Point(94, 14));
        Arrow(c, line, new Point(120, 50), new Point(160, 50));
        c.DrawRectangle(null, line, new Rect(176, 36, 128, 28), 6);
        c.DrawRectangle(accent, null, new Rect(180, 40, 84, 20), 4);
    }

    // stick -> pretend Xbox controller -> the game's screen.
    private static void DrawPad(DrawingContext c, Pen line, Pen accentLine, IBrush accent, IBrush soft)
    {
        c.DrawRectangle(soft, line, new Rect(12, 72, 56, 14), 5);
        c.DrawLine(new Pen(line.Brush, 6, lineCap: PenLineCap.Round), new Point(40, 72), new Point(40, 26));
        c.DrawEllipse(accent, null, new Point(40, 24), 9, 9);
        Arrow(c, accentLine, new Point(78, 52), new Point(108, 52));
        // the controller: a rounded body with two grips, two sticks and four buttons
        StreamGeometry pad = new();
        using (StreamGeometryContext g = pad.Open())
        {
            g.BeginFigure(new Point(124, 40), true);
            g.CubicBezierTo(new Point(130, 28), new Point(194, 28), new Point(200, 40));
            g.CubicBezierTo(new Point(210, 62), new Point(214, 82), new Point(198, 82));
            g.CubicBezierTo(new Point(186, 82), new Point(182, 66), new Point(162, 66));
            g.CubicBezierTo(new Point(142, 66), new Point(138, 82), new Point(126, 82));
            g.CubicBezierTo(new Point(110, 82), new Point(114, 62), new Point(124, 40));
            g.EndFigure(true);
        }
        c.DrawGeometry(soft, line, pad);
        c.DrawEllipse(null, line, new Point(142, 48), 7, 7);
        c.DrawEllipse(null, line, new Point(172, 60), 6, 6);
        c.DrawEllipse(accent, null, new Point(186, 42), 3.5, 3.5);
        c.DrawEllipse(accent, null, new Point(194, 49), 3.5, 3.5);
        c.DrawEllipse(accent, null, new Point(178, 49), 3.5, 3.5);
        c.DrawEllipse(accent, null, new Point(186, 56), 3.5, 3.5);
        Arrow(c, accentLine, new Point(222, 52), new Point(252, 52));
        // the game: a screen with a little plane
        c.DrawRectangle(soft, line, new Rect(262, 26, 50, 40), 4);
        c.DrawLine(line, new Point(287, 66), new Point(287, 78));
        c.DrawLine(line, new Point(274, 80), new Point(300, 80));
        StreamGeometry plane = new();
        using (StreamGeometryContext g = plane.Open())
        {
            g.BeginFigure(new Point(287, 34), true);
            g.LineTo(new Point(298, 54));
            g.LineTo(new Point(287, 50));
            g.LineTo(new Point(276, 54));
            g.EndFigure(true);
        }
        c.DrawGeometry(accent, null, plane);
    }

    // JoyPro's box -> the game's box, and the undo arrow back below.
    private static void DrawExport(DrawingContext c, Pen line, Pen accentLine, IBrush accent, IBrush soft, IBrush ok)
    {
        c.DrawRectangle(soft, line, new Rect(20, 16, 70, 50), 8);
        // a small stick inside JoyPro's box
        c.DrawLine(new Pen(accent, 4, lineCap: PenLineCap.Round), new Point(55, 54), new Point(55, 30));
        c.DrawEllipse(accent, null, new Point(55, 28), 6, 6);
        c.DrawLine(line, new Point(42, 56), new Point(68, 56));
        Arrow(c, new Pen(accent, 5, lineCap: PenLineCap.Round), new Point(104, 34), new Point(212, 34));
        c.DrawRectangle(soft, line, new Rect(226, 16, 76, 50), 8);
        for (int i = 0; i < 3; i++)
            c.DrawLine(new Pen(accent, 3, lineCap: PenLineCap.Round), new Point(240, 30 + i * 12), new Point(288 - i * 10, 30 + i * 12));
        // undo: a curved arrow back
        Pen back = new(ok, 3, new DashStyle([3, 2], 0), PenLineCap.Round);
        StreamGeometry curve = new();
        using (StreamGeometryContext g = curve.Open())
        {
            g.BeginFigure(new Point(250, 72), false);
            g.CubicBezierTo(new Point(230, 94), new Point(90, 94), new Point(66, 74));
            g.EndFigure(false);
        }
        c.DrawGeometry(null, back, curve);
        c.DrawLine(new Pen(ok, 3, lineCap: PenLineCap.Round), new Point(66, 74), new Point(78, 74));
        c.DrawLine(new Pen(ok, 3, lineCap: PenLineCap.Round), new Point(66, 74), new Point(70, 86));
    }

    // The keys Alt (held: filled) + Tab, and the full-screen game and JoyPro's window with arrows both ways. The key
    // caps carry the key names, which stay the same in every language (printed on the keyboard).
    private static void DrawAltTab(DrawingContext c, Pen line, Pen accentLine, IBrush accent, IBrush soft, IBrush ink)
    {
        Key(c, line, accent, new Rect(8, 30, 52, 36), "Alt", Brushes.White); // nonloc
        c.DrawLine(line, new Point(68, 48), new Point(80, 48));
        c.DrawLine(line, new Point(74, 42), new Point(74, 54));
        Key(c, line, soft, new Rect(88, 30, 60, 36), "Tab", ink); // nonloc
        // the game (a screen with a plane) <-> JoyPro (a window with a stick)
        c.DrawRectangle(soft, line, new Rect(168, 18, 56, 44), 4);
        StreamGeometry plane = new();
        using (StreamGeometryContext g = plane.Open())
        {
            g.BeginFigure(new Point(196, 26), true);
            g.LineTo(new Point(208, 48));
            g.LineTo(new Point(196, 44));
            g.LineTo(new Point(184, 48));
            g.EndFigure(true);
        }
        c.DrawGeometry(accent, null, plane);
        Arrow(c, accentLine, new Point(230, 32), new Point(252, 32));
        Arrow(c, accentLine, new Point(252, 50), new Point(230, 50));
        c.DrawRectangle(soft, line, new Rect(258, 18, 56, 44), 4);
        c.DrawLine(line, new Point(258, 27), new Point(314, 27));
        c.DrawLine(new Pen(accent, 4, lineCap: PenLineCap.Round), new Point(286, 56), new Point(286, 36));
        c.DrawEllipse(accent, null, new Point(286, 35), 5, 5);
    }

    private static void Key(DrawingContext c, Pen line, IBrush fill, Rect rect, string text, IBrush textBrush)
    {
        c.DrawRectangle(fill, line, rect, 6);
        FormattedText label = new(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), 16, textBrush);
        c.DrawText(label, new Point(rect.X + (rect.Width - label.Width) / 2, rect.Y + (rect.Height - label.Height) / 2));
    }

    private static void Arrow(DrawingContext c, Pen pen, Point from, Point to)
    {
        c.DrawLine(pen, from, to);
        Vector d = to - from;
        double len = Math.Max(1, Math.Sqrt(d.X * d.X + d.Y * d.Y));
        Vector u = new(d.X / len, d.Y / len);
        Vector n = new(-u.Y, u.X);
        c.DrawLine(pen, to, to - u * 9 + n * 7);
        c.DrawLine(pen, to, to - u * 9 - n * 7);
    }
}
