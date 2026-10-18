using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Views.Controls;

// One page of the Cockpits page's manual (CockpitManualPages: the overview, a grip with its HOTAS table, a panel with its
// numbered list), drawn at the control's width. A click on an item with a link (an overview area) raises LinkClicked; the
// pointer over a table cell shows what it maps to (TagText).
public sealed class ManualPageView : Control
{
    public static readonly StyledProperty<ManualPage?> PageProperty =
        AvaloniaProperty.Register<ManualPageView, ManualPage?>(nameof(Page));

    public static readonly StyledProperty<DrawingStyle?> DrawingStyleProperty =
        AvaloniaProperty.Register<ManualPageView, DrawingStyle?>(nameof(DrawingStyle));

    // The tags of the items to mark ("click:<id>", "item:<panel>:<n>", several joined by "|"): the control picked in the
    // cockpit guide, drawn with a ring and a filled callout.
    public static readonly StyledProperty<string?> SelectedTagProperty =
        AvaloniaProperty.Register<ManualPageView, string?>(nameof(SelectedTag));

    private readonly SchematicPainter painter = new();
    private readonly Dictionary<DeviceLineArt, LineArtImage> art = [];

    static ManualPageView()
    {
        AffectsRender<ManualPageView>(PageProperty, DrawingStyleProperty, SelectedTagProperty);
        AffectsMeasure<ManualPageView>(PageProperty);
    }

    public ManualPage? Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }

    public DrawingStyle? DrawingStyle { get => GetValue(DrawingStyleProperty); set => SetValue(DrawingStyleProperty, value); }

    public string? SelectedTag { get => GetValue(SelectedTagProperty); set => SetValue(SelectedTagProperty, value); }

    // A callout or list line with a tag (and no link) was clicked: its tag ("click:<id>", "item:<panel>:<n>" ...).
    public event EventHandler<string>? TagClicked;

    // A link of the page was clicked ("panel:<id>", "device:<device>").
    public event EventHandler<string>? LinkClicked;

    // The text a tag shows as a tooltip ("fn:<id>" -> the JoyPro function's name).
    public Func<string, string?>? TagText { get; set; }

    public bool Dark { get; set; }

    private double Scale => Page is { Width: > 0 } p && Bounds.Width > 0 ? Bounds.Width / p.Width : 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Page is not { Width: > 0 } p)
            return new Size(0, 0);
        double width = double.IsInfinity(availableSize.Width) ? p.Width : Math.Min(availableSize.Width, p.Width * 1.4);
        return new Size(width, p.Height * width / p.Width);
    }

    public override void Render(DrawingContext context)
    {
        if (Page is not { Width: > 0 } page)
            return;
        Draw(context, page, Scale, DrawingStyle ?? JoyPro.DeviceReference.DrawingStyle.Default, Dark, painter, art, SelectedTag);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedTagProperty || change.Property == PageProperty)
            Avalonia.Threading.Dispatcher.UIThread.Post(RevealSelected, Avalonia.Threading.DispatcherPriority.Background);
    }

    // Scrolls the marked callout into view (the page is taller than the window).
    private void RevealSelected()
    {
        if (Page is not { } page || string.IsNullOrEmpty(SelectedTag) || Bounds.Width <= 0)
            return;
        ManualPageItem? dot = page.Items.FirstOrDefault(i => i.Kind == "dot" && IsSelected(i.Tag, SelectedTag)) ??
                              page.Items.FirstOrDefault(i => i.Kind != "drawing" && IsSelected(i.Tag, SelectedTag));
        if (dot == null)
            return;
        double s = Scale;
        RaiseEvent(new RequestBringIntoViewEventArgs
        {
            RoutedEvent = RequestBringIntoViewEvent,
            TargetObject = this,
            TargetRect = new Rect(dot.X * s - 120, dot.Y * s - 160, dot.W * s + 240, dot.H * s + 320)
        });
    }

    // An item's tag is one of the selected tags ("click:a|fn:x" is "click:a").
    public static bool IsSelected(string tag, string? selected)
    {
        if (string.IsNullOrEmpty(selected) || string.IsNullOrEmpty(tag))
            return false;
        string[] wanted = selected.Split('|');
        foreach (string part in tag.Split('|'))
        {
            if (part.Length > 0 && Array.IndexOf(wanted, part) >= 0)
                return true;
        }
        return false;
    }

    // Draws a page at a scale (also used for the PNG and PDF export).
    public static void Draw(DrawingContext context, ManualPage page, double s, DrawingStyle style, bool dark, SchematicPainter? painter = null,
        Dictionary<DeviceLineArt, LineArtImage>? cache = null, string? selectedTag = null)
    {
        painter ??= new SchematicPainter();
        context.FillRectangle(Brush(page.Paper), new Rect(0, 0, page.Width * s, page.Height * s));
        List<ManualPageItem> marked = [];
        // The mark's colour follows the page's paper (a light page in the dark theme too).
        string markFill = JoyPro.DeviceReference.DrawingStyle.IsDarkColor(page.Paper) ? "#5A4A12" : "#FFE680";
        foreach (ManualPageItem item in page.Items)
        {
            Rect r = new(item.X * s, item.Y * s, Math.Max(0, item.W * s), Math.Max(0, item.H * s));
            bool selected = IsSelected(item.Tag, selectedTag);
            if (selected && item.Kind != "drawing")
                marked.Add(item);
            switch (item.Kind)
            {
                case "rect":
                case "cell":
                    IPen? pen = item.Stroke.Length > 0 ? new Pen(Brush(item.Stroke), item.StrokeWidth * s * (selected ? 2.2 : 1)) : null;
                    string fill = selected && item.Kind == "cell" ? markFill : item.Fill;
                    context.DrawRectangle(fill.Length > 0 ? Brush(fill) : null, pen, r, item.Radius * s, item.Radius * s);
                    if (item.Lines.Count > 0)
                        DrawLines(context, item, r, s, true);
                    break;
                case "text":
                    if (selected)
                        context.DrawRectangle(Brush(markFill), null, r.Inflate(3 * s), 3 * s, 3 * s);
                    DrawLines(context, item, r, s, false);
                    break;
                case "line":
                    if (item.Points is { Length: >= 4 } pts)
                    {
                        Pen line = new(Brush(item.Stroke), item.StrokeWidth * s * (selected ? 2.4 : 1), lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
                        for (int i = 2; i + 1 < pts.Length; i += 2)
                            context.DrawLine(line, new Point(pts[i - 2] * s, pts[i - 1] * s), new Point(pts[i] * s, pts[i + 1] * s));
                    }
                    break;
                case "dot":
                    context.DrawEllipse(Brush(item.Fill), null, r.Center, r.Width / 2, r.Height / 2);
                    break;

                case "drawing":
                    if (item.Art is { HasPicture: true } && Picture(item.Art.Picture, item.Art.Mask) is { } picture)
                    {
                        // A picture made from the user's game.
                        context.DrawImage(picture, new Rect(0, 0, picture.Size.Width, picture.Size.Height), r);
                    }
                    else if (item.Art?.LineArt is { } lineArt)
                    {
                        LineArtImage image;
                        if (cache == null || !cache.TryGetValue(lineArt, out image!))
                        {
                            image = new LineArtImage(lineArt, style) { Dark = dark };
                            if (cache != null)
                                cache[lineArt] = image;
                        }
                        image.Draw(context, new Rect(0, 0, lineArt.Width, lineArt.Height), r);
                    }
                    else if (item.Art?.Schematic is { } schematic)
                    {
                        context.FillRectangle(Brush(dark ? "#0B1220" : "#EEF2F7"), r);
                        painter.Draw(context, schematic, r, dark);
                    }
                    break;
            }
        }
        // The marked controls on top of the picture: a ring around each callout point.
        foreach (ManualPageItem item in marked.Where(i => i.Kind == "dot"))
        {
            Point c = new((item.X + item.W / 2) * s, (item.Y + item.H / 2) * s);
            context.DrawEllipse(null, new Pen(Brush("#000000"), 5 * s), c, 15 * s, 15 * s);
            context.DrawEllipse(null, new Pen(Brush("#FFD34D"), 3 * s), c, 15 * s, 15 * s);
        }
    }

    // The game pictures, read once each (a few recent ones are kept; a file written again is read again).
    private static readonly Dictionary<string, Bitmap?> Pictures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> PictureOrder = [];

    private static Bitmap? Picture(string file, string? mask = null)
    {
        string key;
        try
        {
            key = file + "|" + (File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0) + "|" + mask;
        }
        catch (Exception)
        {
            key = file;
        }
        lock (Pictures)
        {
            if (Pictures.TryGetValue(key, out Bitmap? hit))
                return hit;
            Bitmap? bitmap = null;
            try
            {
                if (File.Exists(file))
                    bitmap = LoadPicture(file, mask);
            }
            catch (Exception)
            {
                bitmap = null;
            }
            Pictures[key] = bitmap;
            PictureOrder.Add(key);
            while (PictureOrder.Count > 24)
            {
                string old = PictureOrder[0];
                PictureOrder.RemoveAt(0);
                if (Pictures.Remove(old, out Bitmap? gone))
                    gone?.Dispose();
            }
            return bitmap;
        }
    }

    // A PNG as it is; a JPEG of a picture pack with its transparency from the grey mask PNG next to it (docs/cockpits.md
    // "Picture packs"). Without a usable mask the JPEG is shown opaque.
    internal static Bitmap LoadPicture(string file, string? mask)
    {
        if (string.IsNullOrEmpty(mask) || !File.Exists(mask))
            return new Bitmap(file);
        using SkiaSharp.SKBitmap? color = SkiaSharp.SKBitmap.Decode(file);
        using SkiaSharp.SKBitmap? alpha = SkiaSharp.SKBitmap.Decode(mask);
        if (color == null)
            return new Bitmap(file);
        if (alpha == null || alpha.Width != color.Width || alpha.Height != color.Height)
            return new Bitmap(file);
        int w = color.Width, h = color.Height;
        using SkiaSharp.SKBitmap? rgb = color.Copy(SkiaSharp.SKColorType.Bgra8888);
        using SkiaSharp.SKBitmap? grey = alpha.Copy(SkiaSharp.SKColorType.Bgra8888);
        if (rgb == null || grey == null)
            return new Bitmap(file);
        byte[] px = rgb.Bytes;
        byte[] m = grey.Bytes;
        const int mStep = 4, mOffset = 2;
        byte[] premul = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int row = y * rgb.RowBytes, mrow = y * grey.RowBytes;
            for (int x = 0; x < w; x++)
            {
                int i = row + 4 * x, o = (y * w + x) * 4;
                int a = m[mrow + mStep * x + mOffset];
                premul[o] = (byte)(px[i] * a / 255);
                premul[o + 1] = (byte)(px[i + 1] * a / 255);
                premul[o + 2] = (byte)(px[i + 2] * a / 255);
                premul[o + 3] = (byte)a;
            }
        }
        System.Runtime.InteropServices.GCHandle handle = System.Runtime.InteropServices.GCHandle.Alloc(premul, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul, handle.AddrOfPinnedObject(), new PixelSize(w, h), new Vector(96, 96), w * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    private static readonly Dictionary<string, IBrush> Brushes = new(StringComparer.OrdinalIgnoreCase);

    private static IBrush Brush(string hex)
    {
        if (!Brushes.TryGetValue(hex, out IBrush? brush))
        {
            brush = Color.TryParse(hex, out Color c) ? new SolidColorBrush(c).ToImmutable() : Avalonia.Media.Brushes.Gray;
            Brushes[hex] = brush;
        }
        return brush;
    }

    // Segoe UI as the manual pages are, then the language's fallbacks (CJK of the language, symbols).
    private static Typeface Regular => new(SheetFonts.Get("Segoe UI, Inter, Arial, "));
    private static Typeface Bold => new(SheetFonts.Get("Segoe UI, Inter, Arial, "), FontStyle.Normal, FontWeight.Bold);

    private static void DrawLines(DrawingContext context, ManualPageItem item, Rect r, double s, bool centre)
    {
        double size = item.Size * s;
        double lineH = item.Size * ManualLayout.LineHeight * s;
        double y = centre ? r.Y + (r.Height - lineH * item.Lines.Count) / 2 : r.Y;
        IBrush ink = item.Color.Length > 0 ? Brush(item.Color) : Avalonia.Media.Brushes.Black;
        foreach (string line in item.Lines)
        {
            FormattedText text = new(line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, item.Bold ? Bold : Regular, Math.Max(1, size), ink);
            double room = r.Width - 6 * s;
            if (text.Width > room && room > 0)
                text.SetFontSize(Math.Max(size * 0.6, size * room / text.Width));
            double x = item.Align == "center" ? r.X + (r.Width - text.Width) / 2 : r.X + (item.Kind == "cell" ? 6 * s : 0);
            context.DrawText(text, new Point(x, y + (lineH - text.Height) / 2));
            y += lineH;
        }
    }

    // ------------------------------------------------------------------ pointer: links and tooltips

    private ManualPageItem? ItemAt(Point p, Func<ManualPageItem, bool> filter)
    {
        if (Page is not { } page)
            return null;
        double s = Scale;
        Point at = new(p.X / s, p.Y / s);
        for (int i = page.Items.Count - 1; i >= 0; i--)
        {
            ManualPageItem item = page.Items[i];
            if (!filter(item))
                continue;
            if (item.Kind == "line" && item.Points is { Length: >= 4 } pts)
            {
                for (int k = 2; k + 1 < pts.Length; k += 2)
                {
                    if (DistanceToSegment(at, new Point(pts[k - 2], pts[k - 1]), new Point(pts[k], pts[k + 1])) < 5)
                        return item;
                }
                continue;
            }
            if (at.X >= item.X && at.X <= item.X + item.W && at.Y >= item.Y && at.Y <= item.Y + item.H)
                return item;
        }
        return null;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        Vector ab = b - a;
        double len = ab.X * ab.X + ab.Y * ab.Y;
        double t = len <= 0 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len, 0, 1);
        Point q = a + ab * t;
        return Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point p = e.GetPosition(this);
        ManualPageItem? link = ItemAt(p, i => i.Link.Length > 0);
        Cursor = link != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        ManualPageItem? tagged = ItemAt(p, i => i.Tag.Length > 0 && i.Kind != "drawing");
        string? tip = tagged == null ? null : TagText?.Invoke(tagged.Tag);
        if (!Equals(ToolTip.GetTip(this), tip))
        {
            ToolTip.SetTip(this, tip);
            ToolTip.SetIsOpen(this, tip != null);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        ManualPageItem? link = ItemAt(e.GetPosition(this), i => i.Link.Length > 0);
        if (link != null)
        {
            LinkClicked?.Invoke(this, link.Link);
            e.Handled = true;
            return;
        }
        ManualPageItem? tagged = ItemAt(e.GetPosition(this), i => i.Tag.Length > 0 && i.Kind != "drawing" && !i.Tag.StartsWith("fn:", StringComparison.Ordinal));
        if (tagged != null)
        {
            TagClicked?.Invoke(this, tagged.Tag);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ export

    // The page as a bitmap at a scale (offscreen, the user's drawing style).
    public static RenderTargetBitmap RenderBitmap(ManualPage page, double scale, DrawingStyle style, bool dark)
    {
        PixelSize size = new(Math.Max(1, (int)Math.Ceiling(page.Width * scale)), Math.Max(1, (int)Math.Ceiling(page.Height * scale)));
        RenderTargetBitmap bitmap = new(size);
        using (DrawingContext context = bitmap.CreateDrawingContext())
            Draw(context, page, scale, style, dark);
        return bitmap;
    }

    public static void SavePng(ManualPage page, string file, double scale, DrawingStyle style, bool dark)
    {
        using RenderTargetBitmap bitmap = RenderBitmap(page, scale, style, dark);
        bitmap.Save(file);
    }

    // The pages as a PDF (one page each, A4 width), through SkiaSharp.
    public static void SavePdf(IEnumerable<ManualPage> pages, string file, double scale, DrawingStyle style, bool dark)
    {
        using FileStream stream = File.Create(file);
        using SkiaSharp.SKDocument document = SkiaSharp.SKDocument.CreatePdf(stream);
        foreach (ManualPage page in pages)
        {
            using RenderTargetBitmap bitmap = RenderBitmap(page, scale, style, dark);
            using MemoryStream png = new();
            bitmap.Save(png);
            png.Position = 0;
            using SkiaSharp.SKBitmap image = SkiaSharp.SKBitmap.Decode(png);
            float width = 595f, height = (float)(595.0 * image.Height / Math.Max(1, image.Width));
            SkiaSharp.SKCanvas canvas = document.BeginPage(width, height);
            canvas.DrawBitmap(image, new SkiaSharp.SKRect(0, 0, width, height));
            document.EndPage();
        }
        document.Close();
    }
}
