using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace JoyPro.Gui.Services;

// Product photos stand on a white (or other plain) background. For the HUD the background around the device becomes
// transparent: a flood fill from the picture's border through pixels close to the border colour, with a soft edge.
// White parts inside the device (labels, buttons) are not connected to the border and stay. Pictures are scaled to at
// most MaxSize pixels and cached as PNG in %LOCALAPPDATA%\JoyPro2\HudImages.
public static class ImageCutout
{
    private const int Clear = 18;
    private const int Soft = 46;
    private const int MaxSize = 900;

    // The cut-out PNG for a local picture file, or the file itself when it cannot be read.
    public static string CutoutFile(string sourceFile)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro2", "HudImages");
            FileInfo info = new(sourceFile);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks)))[..32];
            string target = Path.Combine(folder, key + ".png");
            if (File.Exists(target))
                return target;
            using SKBitmap? source = SKBitmap.Decode(sourceFile);
            if (source == null)
                return sourceFile;
            using SKBitmap cut = Cut(source);
            Directory.CreateDirectory(folder);
            using SKImage image = SKImage.FromBitmap(cut);
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
            using (FileStream stream = File.Create(target))
                png.SaveTo(stream);
            return target;
        }
        catch (Exception)
        {
            return sourceFile;
        }
    }

    // The scaled picture with a transparent background (unchanged background when the border is not one plain colour
    // or already transparent).
    internal static SKBitmap Cut(SKBitmap source)
    {
        double scale = Math.Min(1.0, (double)MaxSize / Math.Max(source.Width, source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        SKBitmap bitmap = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using SKPaint paint = new() { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawBitmap(source, new SKRect(0, 0, width, height), paint);
        }
        if (width < 8 || height < 8)
            return bitmap;

        byte[] px = bitmap.Bytes;
        int Index(int x, int y) => (y * width + x) * 4;

        // The background colour: the border pixels, which must be (nearly) one opaque colour.
        List<(int B, int G, int R, int A)> border = [];
        void Sample(int x, int y)
        {
            int i = Index(x, y);
            border.Add((px[i], px[i + 1], px[i + 2], px[i + 3]));
        }
        for (int x = 0; x < width; x += Math.Max(1, width / 64))
        {
            Sample(x, 0);
            Sample(x, height - 1);
        }
        for (int y = 0; y < height; y += Math.Max(1, height / 64))
        {
            Sample(0, y);
            Sample(width - 1, y);
        }
        if (border.FindAll(c => c.A < 250).Count > border.Count / 4)
            return bitmap;
        (int B, int G, int R) background = (Median(border.ConvertAll(c => c.B)), Median(border.ConvertAll(c => c.G)), Median(border.ConvertAll(c => c.R)));
        int Distance(int i)
        {
            int b = px[i] - background.B;
            int g = px[i + 1] - background.G;
            int r = px[i + 2] - background.R;
            return (int)Math.Sqrt(r * r + g * g + b * b);
        }
        int plain = 0;
        foreach ((int b, int g, int r, int _) in border)
        {
            int db = b - background.B, dg = g - background.G, dr = r - background.R;
            if (Math.Sqrt(db * db + dg * dg + dr * dr) <= Clear)
                plain++;
        }
        if (plain < border.Count * 7 / 10)
            return bitmap;

        bool[] visited = new bool[width * height];
        Queue<int> queue = new();
        void Seed(int x, int y)
        {
            int p = y * width + x;
            if (!visited[p] && Distance(p * 4) <= Soft)
            {
                visited[p] = true;
                queue.Enqueue(p);
            }
        }
        for (int x = 0; x < width; x++)
        {
            Seed(x, 0);
            Seed(x, height - 1);
        }
        for (int y = 0; y < height; y++)
        {
            Seed(0, y);
            Seed(width - 1, y);
        }

        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            int i = p * 4;
            int distance = Distance(i);
            int alpha = distance <= Clear ? 0 : Math.Clamp((distance - Clear) * 255 / (Soft - Clear), 0, 255);
            alpha = Math.Min(alpha, px[i + 3]);
            // Premultiplied: colour channels scale with the new alpha.
            int old = Math.Max(1, (int)px[i + 3]);
            px[i] = (byte)(px[i] * alpha / old);
            px[i + 1] = (byte)(px[i + 1] * alpha / old);
            px[i + 2] = (byte)(px[i + 2] * alpha / old);
            px[i + 3] = (byte)alpha;
            // Only clearly background pixels spread the fill; the soft edge stops it.
            if (distance > Clear)
                continue;
            int x = p % width;
            int y = p / width;
            if (x > 0) Seed(x - 1, y);
            if (x < width - 1) Seed(x + 1, y);
            if (y > 0) Seed(x, y - 1);
            if (y < height - 1) Seed(x, y + 1);
        }
        Marshal.Copy(px, 0, bitmap.GetPixels(), px.Length);
        return bitmap;
    }

    private static int Median(List<int> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}
