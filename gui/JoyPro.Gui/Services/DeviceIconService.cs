using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using JoyPro.DeviceReference;
using SkiaSharp;

namespace JoyPro.Gui.Services;

// The icon of a device (docs/device-reference.md, "Device icon"): which of its pictures the device cards, the bind
// list, the HUD designer and the Start page show for it. Chosen on the Layout page; without a choice every place shows
// what it showed before (your layout, your photo, JoyPro's line drawing, the maker's photo). The chosen picture is
// cut out of its plain background, cropped square around the device and kept as a PNG in
// <data>\DeviceReference\Icons, with the choice in icons.json (by device name without its GUID: Windows gives a
// device a new GUID now and then).
public static class DeviceIconService
{
    public const int IconSize = 256;

    public sealed class IconChoice
    {
        public string Kind { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
    }

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string Folder() => Path.Combine(DeviceReferenceService.DataFolder(), "Icons");

    private static string ChoicesFile() => Path.Combine(DeviceReferenceService.DataFolder(), "icons.json");

    public static string KeyOf(string? device) => DeviceProbe.RemoveGuid((device ?? string.Empty).Trim()).Trim();

    // The chosen icon of a device, null when none was chosen (or its file is gone).
    public static IconChoice? ChoiceOf(string? device)
    {
        string key = KeyOf(device);
        if (key.Length == 0)
            return null;
        Dictionary<string, IconChoice> choices = Read();
        return choices.TryGetValue(key, out IconChoice? choice) && File.Exists(choice.File) ? choice : null;
    }

    // Keeps a picture (PNG bytes, already square) as the device's icon.
    public static IconChoice SetIcon(string device, string kind, string title, byte[] png)
    {
        string key = KeyOf(device);
        if (key.Length == 0)
            throw new ArgumentException("No device.", nameof(device));
        lock (Gate)
        {
            Directory.CreateDirectory(Folder());
            string file = Path.Combine(Folder(), DeviceVocabulary.Slug(key) + ".png");
            File.WriteAllBytes(file, png);
            Dictionary<string, IconChoice> choices = Read();
            IconChoice choice = new() { Kind = kind, Title = title, File = file };
            choices[key] = choice;
            Write(choices);
            return choice;
        }
    }

    // Back to the automatic icon.
    public static void ClearIcon(string device)
    {
        string key = KeyOf(device);
        lock (Gate)
        {
            Dictionary<string, IconChoice> choices = Read();
            if (!choices.Remove(key, out IconChoice? old))
                return;
            Write(choices);
            try
            {
                if (File.Exists(old.File))
                    File.Delete(old.File);
            }
            catch (IOException)
            {
            }
        }
    }

    // A picture file as an icon (PNG bytes), null when it cannot be read.
    public static byte[]? IconFromFile(string file, bool cutBackground = true)
    {
        try
        {
            using SKBitmap? source = SKBitmap.Decode(file);
            return source == null ? null : IconFromBitmap(source, cutBackground);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A rendered picture as an icon: the plain background cut away (cutBackground), cropped to a square around what
    // is left with a small margin, scaled to IconSize, transparent around it.
    // boundsFrom: a picture of the same size whose opaque part is the device (e.g. a drawing without its paper): the
    // square is taken around that, the picture itself is kept whole (paper and all).
    public static byte[] IconFromBitmap(SKBitmap source, bool cutBackground, SKBitmap? boundsFrom = null)
    {
        using SKBitmap cut = cutBackground ? ImageCutout.Cut(source) : source.Copy();
        bool other = boundsFrom != null && boundsFrom.Width == source.Width && boundsFrom.Height == source.Height;
        SKRectI bounds = other ? ContentBounds(boundsFrom!) : ContentBounds(cut);
        if (other && cut.Width != source.Width)
        {
            // The cut-out is scaled down: the bounds of the other picture scale along.
            float scale = cut.Width / (float)source.Width;
            bounds = new SKRectI((int)(bounds.Left * scale), (int)(bounds.Top * scale), (int)Math.Ceiling(bounds.Right * scale), (int)Math.Ceiling(bounds.Bottom * scale));
        }
        int side = (int)Math.Ceiling(Math.Max(bounds.Width, bounds.Height) * 1.08);
        float cx = bounds.MidX, cy = bounds.MidY;
        SKRect from = new(cx - side / 2f, cy - side / 2f, cx + side / 2f, cy + side / 2f);
        using SKBitmap icon = new(new SKImageInfo(IconSize, IconSize, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (SKCanvas canvas = new(icon))
        {
            canvas.Clear(SKColors.Transparent);
            using SKPaint paint = new() { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawBitmap(cut, from, new SKRect(0, 0, IconSize, IconSize), paint);
        }
        using SKImage image = SKImage.FromBitmap(icon);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    // The area that is not transparent (the whole picture when all of it is, or nothing is).
    internal static SKRectI ContentBounds(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        int left = w, top = h, right = -1, bottom = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha < 40)
                    continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }
        return right < left || bottom < top ? new SKRectI(0, 0, w, h) : new SKRectI(left, top, right + 1, bottom + 1);
    }

    private static Dictionary<string, IconChoice> Read()
    {
        try
        {
            string file = ChoicesFile();
            Dictionary<string, IconChoice>? map = File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, IconChoice>>(File.ReadAllText(file, Encoding.UTF8), JsonOptions)
                : null;
            return map == null ? new(StringComparer.OrdinalIgnoreCase) : new(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Write(Dictionary<string, IconChoice> choices)
    {
        Directory.CreateDirectory(DeviceReferenceService.DataFolder());
        File.WriteAllText(ChoicesFile(), JsonSerializer.Serialize(choices, JsonOptions), new UTF8Encoding(false));
    }
}
