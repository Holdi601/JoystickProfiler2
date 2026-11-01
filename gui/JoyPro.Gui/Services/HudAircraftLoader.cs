using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using SkiaSharp;

namespace JoyPro.Gui.Services;

// Finds the stick and throttle of an aircraft for the HUD's aircraft widget (docs/hud.md "Aircraft stick and throttle"):
//   1. the grip pictures of the aircraft's cockpit picture set (rendered from the game's own model on this PC or a
//      picture pack, <data>\GameData\CockpitPictures, docs/cockpits.md), with the controls the model names for a part;
//   2. else JoyPro's line drawing of the real grip (DB\Cockpits\line-drawings) with its part positions;
//   3. else nothing (the device widgets stay the fallback: the device's own picture with its lit markers).
// The pictures are cropped to the grip and made small once (the HUD never scales a 1600 px picture per frame), and
// encoded as PNG once for the stream page.
public static class HudAircraftLoader
{
    // Pixel height of the prepared pictures (twice the widget's picture height: sharp up to a widget scale of 2).
    public const int PictureHeightPx = 480;

    // Why the last picture could not be prepared (the support log, tests).
    public static string LastError { get; private set; } = string.Empty;

    // The picture sets on this PC, as "<game>/<set>" with a name ("F-16C_50 · DCS World"): the aircraft widget's list.
    public static List<HudChoice> Choices(string? root = null)
    {
        root ??= CockpitPictureSet.Root();
        List<HudChoice> choices = [];
        if (!Directory.Exists(root))
            return choices;
        foreach (string game in CockpitPictureSet.Games.Concat(Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string folder = Path.Combine(root, game);
            if (!Directory.Exists(folder))
                continue;
            foreach (string set in Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(set, "pictures.json")))
                    choices.Add(new HudChoice(game + "/" + Path.GetFileName(set), Path.GetFileName(set) + " · " + CockpitManualPages.GameName(game)));
            }
        }
        return choices;
    }

    // What the widget will show: the set or the variant, and the pictures still to be made on the UI thread (line drawings).
    public sealed class Found
    {
        public string Key = string.Empty;
        public string Title = string.Empty;
        public string Credit = string.Empty;
        public Prepared? Stick;
        public Prepared? Throttle;
        public DeviceLineArt? StickArt;
        public DeviceLineArt? ThrottleArt;
        public CockpitVariant? Variant;
        public CockpitPictureView? StickView;
        public CockpitPictureView? ThrottleView;
    }

    // A grip picture cropped and made small: the bitmap, its PNG and where the crop sits in the original (0..1).
    public sealed class Prepared
    {
        public Bitmap Bitmap = null!;
        public byte[] Png = [];
        public double CropX, CropY, CropW = 1, CropH = 1;

        public (double X, double Y) Map(double x, double y) => ((x - CropX) / CropW, (y - CropY) / CropH);
    }

    // choice: "<game>/<set>" (the widget's pick) or empty; plane: the aircraft JoyPro knows (DCS unit or choice).
    // Runs off the UI thread (reads and decodes the pictures).
    public static Found? Find(string? choice, string? plane, string? root = null)
    {
        // One at a time: the cockpit catalog resolves its variants into a shared cache.
        lock (FindGate)
        {
            try
            {
                return FindCore(choice, plane, root);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }
    }

    private static readonly object FindGate = new();

    private static Found? FindCore(string? choice, string? plane, string? root)
    {
        root ??= CockpitPictureSet.Root();
        CockpitCatalog? catalog = null;
        try
        {
            catalog = CockpitService.Catalog;
        }
        catch (Exception)
        {
        }
        CockpitPictureSet? set = null;
        CockpitVariant? variant = null;
        string key = string.Empty;
        if (!string.IsNullOrWhiteSpace(choice))
        {
            string folder = Path.Combine(root, choice.Replace('/', Path.DirectorySeparatorChar));
            if (!choice.Contains("..", StringComparison.Ordinal) && File.Exists(Path.Combine(folder, "pictures.json")))
                set = SafeLoad(folder);
            key = choice;
        }
        else if (!string.IsNullOrWhiteSpace(plane))
        {
            variant = catalog?.VariantOfPlane(plane) ?? catalog?.FindVariant(plane, null);
            if (variant != null)
            {
                try
                {
                    set = CockpitPictureSet.For(variant, root);
                }
                catch (Exception)
                {
                    set = null;
                }
            }
            // The DCS unit type ("A-10C_2") by the DCS pictures' index (it names the set by every name of the aircraft).
            string? folder = set == null ? IndexNames(Path.Combine(root, "DCS")).FirstOrDefault(n => string.Equals(n.Key, plane, StringComparison.OrdinalIgnoreCase)).Value : null;
            if (folder != null && !folder.Contains("..", StringComparison.Ordinal) && File.Exists(Path.Combine(root, "DCS", folder, "pictures.json")))
                set = SafeLoad(Path.Combine(root, "DCS", folder));
            key = "plane:" + plane;
        }
        if (set != null && variant == null && catalog != null)
        {
            // The catalog knows the aircraft by JoyPro's names ("A-10C II"); the index of the set's game has them all.
            string setFolder = Path.GetFileName(set.Folder);
            IEnumerable<string> names = new[] { set.Plane }.Concat(set.Planes)
                .Concat(IndexNames(Path.GetDirectoryName(set.Folder) ?? string.Empty).Where(n => string.Equals(n.Value, setFolder, StringComparison.OrdinalIgnoreCase)).Select(n => n.Key));
            variant = names.Where(p => !string.IsNullOrWhiteSpace(p)).Select(catalog.VariantOfPlane).FirstOrDefault(v => v != null);
        }

        Found found = new() { Key = key, Variant = variant };
        if (set != null)
        {
            int seat = variant != null ? set.SeatOf(variant) : 1;
            found.StickView = set.Grip(seat, "stick");
            found.ThrottleView = set.Grip(seat, "throttle");
            found.Stick = found.StickView == null ? null : Prepare(found.StickView.Image, found.StickView.Mask);
            found.Throttle = found.ThrottleView == null ? null : Prepare(found.ThrottleView.Image, found.ThrottleView.Mask);
            found.Title = variant?.Name is { Length: > 0 } name ? name : set.Plane;
            found.Credit = T("Rendered from the cockpit model of {0}", CockpitManualPages.GameName(set.Game));
            found.Key = set.Game + "/" + Path.GetFileName(set.Folder);
        }
        if (found.Stick == null && found.Throttle == null && variant != null)
        {
            string cockpits = CockpitService.Folder();
            found.StickArt = CockpitManualPages.DeviceArt(cockpits, variant, StickSide(variant));
            found.ThrottleArt = CockpitManualPages.DeviceArt(cockpits, variant, ThrottleSide(variant));
            found.Title = variant.Name;
            found.Credit = T("JoyPro's drawing of the grips");
            found.Key = "drawing:" + variant.Id;
        }
        return found.Stick == null && found.Throttle == null && found.StickArt == null && found.ThrottleArt == null ? null : found;
    }

    // The widget's model (on the UI thread: line drawings are drawn into bitmaps here).
    public static HudAircraft Build(Found found)
    {
        Prepared? stick = found.Stick ?? Rasterize(found.StickArt);
        Prepared? throttle = found.Throttle ?? Rasterize(found.ThrottleArt);
        List<HudAircraft.Control> controls = [];
        CockpitVariant? v = found.Variant;
        if (v != null)
        {
            Dictionary<string, CockpitPictureCallout> stickMatch = CockpitPictureSet.MatchGrip(found.StickView, v.Controls.Where(c => IsStickSide(c.Device)));
            Dictionary<string, CockpitPictureCallout> throttleMatch = CockpitPictureSet.MatchGrip(found.ThrottleView, v.Controls.Where(c => IsThrottleSide(c.Device)));
            foreach (CockpitControl c in v.Controls.Where(c => IsStickSide(c.Device) || IsThrottleSide(c.Device)))
            {
                string side = IsThrottleSide(c.Device) ? "throttle" : "stick";
                HudAircraft.Control control = new(c.Id, c.Name, side);
                Prepared? picture = side == "throttle" ? throttle : stick;
                DeviceLineArt? art = side == "throttle" ? found.ThrottleArt : found.StickArt;
                (double X, double Y)? at = null;
                if ((side == "throttle" ? throttleMatch : stickMatch).TryGetValue(c.Id, out CockpitPictureCallout? callout) && !callout.Hidden && callout.X >= 0)
                    at = (callout.X, callout.Y);
                else if (art != null && found.Stick == null && found.Throttle == null && art.Parts.TryGetValue(c.PartId, out double[]? xy) && xy.Length >= 2)
                    at = (xy[0], xy[1]);
                if (at is { } point && picture != null)
                {
                    (double mx, double my) = picture.Map(point.X, point.Y);
                    if (mx >= -0.05 && mx <= 1.05 && my >= -0.05 && my <= 1.05)
                        control.Markers.Add(new HudAircraftMarker(c.Id, c.Name, Math.Clamp(mx, 0, 1), Math.Clamp(my, 0, 1)));
                }
                controls.Add(control);
            }
        }
        return new HudAircraft(found.Key, found.Title, found.Credit, stick?.Bitmap, throttle?.Bitmap, controls)
        {
            StickPng = stick?.Png,
            ThrottlePng = throttle?.Png
        };
    }

    public static bool IsStickSide(string device) => CockpitCatalog.StickDevices.Contains(device, StringComparer.OrdinalIgnoreCase);

    public static bool IsThrottleSide(string device) => CockpitCatalog.ThrottleDevices.Contains(device, StringComparer.OrdinalIgnoreCase);

    private static string StickSide(CockpitVariant v) => v.Controls.Select(c => c.Device).FirstOrDefault(IsStickSide) ?? "stick";

    private static string ThrottleSide(CockpitVariant v) => v.Controls.Select(c => c.Device).FirstOrDefault(IsThrottleSide) ?? "throttle";

    // index.json of a game's pictures: every aircraft name (as the game and JoyPro write it) -> its set folder.
    private static List<KeyValuePair<string, string>> IndexNames(string gameFolder)
    {
        List<KeyValuePair<string, string>> names = [];
        try
        {
            string file = Path.Combine(gameFolder, "index.json");
            if (!File.Exists(file))
                return names;
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("aircraft", out System.Text.Json.JsonElement aircraft) && aircraft.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (System.Text.Json.JsonProperty pair in aircraft.EnumerateObject())
                {
                    if (pair.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        names.Add(new KeyValuePair<string, string>(pair.Name, pair.Value.GetString() ?? string.Empty));
                }
            }
        }
        catch (Exception)
        {
        }
        return names;
    }

    private static CockpitPictureSet? SafeLoad(string folder)
    {
        try
        {
            return CockpitPictureSet.Load(folder);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The grip of a picture (JPEG + grey mask, or a PNG with alpha): cut to what is drawn (with a little room), made
    // PictureHeightPx high, premultiplied for Avalonia and as PNG for the stream page.
    public static Prepared? Prepare(string file, string? mask)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            return null;
        try
        {
            using SKBitmap? decoded = SKBitmap.Decode(file);
            if (decoded == null)
                return null;
            using SKBitmap rgba = decoded.Copy(SKColorType.Rgba8888) ?? throw new InvalidDataException("picture");
            int w = rgba.Width, h = rgba.Height;
            byte[] px = rgba.Bytes;
            if (!string.IsNullOrEmpty(mask) && File.Exists(mask))
            {
                using SKBitmap? m = SKBitmap.Decode(mask);
                using SKBitmap? grey = m?.Copy(SKColorType.Rgba8888);
                if (grey != null && grey.Width == w && grey.Height == h)
                {
                    byte[] a = grey.Bytes;
                    for (int i = 0; i < w * h; i++)
                        px[i * 4 + 3] = a[i * 4];
                }
            }
            // What is drawn: alpha above a little noise.
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    if (px[row + x * 4 + 3] > 24)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0)
                return null;
            int pad = (int)Math.Round(Math.Max(maxX - minX, maxY - minY) * 0.04);
            minX = Math.Max(0, minX - pad);
            minY = Math.Max(0, minY - pad);
            maxX = Math.Min(w - 1, maxX + pad);
            maxY = Math.Min(h - 1, maxY + pad);
            int cw = maxX - minX + 1, ch = maxY - minY + 1;
            using SKBitmap unpremul = new(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            Marshal.Copy(px, 0, unpremul.GetPixels(), px.Length);
            using SKBitmap crop = new(new SKImageInfo(cw, ch, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            if (!unpremul.ExtractSubset(crop, new SKRectI(minX, minY, maxX + 1, maxY + 1)))
                return null;
            int th = Math.Min(PictureHeightPx, ch), tw = Math.Max(1, (int)Math.Round(cw * (double)th / ch));
            using SKBitmap small = new(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (SKCanvas canvas = new(small))
            using (SKPaint paint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High })
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(crop, new SKRect(0, 0, tw, th), paint);
            }
            byte[] png;
            using (SKImage image = SKImage.FromBitmap(small))
            using (SKData data = image.Encode(SKEncodedImageFormat.Png, 90))
                png = data.ToArray();
            Bitmap bitmap = new(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul, small.GetPixels(), new PixelSize(tw, th), new Vector(96, 96), small.RowBytes);
            return new Prepared { Bitmap = bitmap, Png = png, CropX = (double)minX / w, CropY = (double)minY / h, CropW = (double)cw / w, CropH = (double)ch / h };
        }
        catch (Exception ex)
        {
            LastError = Path.GetFileName(file) + ": " + ex.Message;
            return null;
        }
    }

    // JoyPro's line drawing of a grip in the dark drawing colours on a transparent background.
    private static Prepared? Rasterize(DeviceLineArt? art)
    {
        if (art == null || art.Width <= 0 || art.Height <= 0)
            return null;
        try
        {
            Views.Controls.LineArtImage image = new(art, DeviceReferenceService.ScreenDrawingStyle) { Dark = true, Page = false };
            double scale = PictureHeightPx / art.Height;
            PixelSize size = new(Math.Max(1, (int)Math.Round(art.Width * scale)), PictureHeightPx);
            RenderTargetBitmap target = new(size);
            using (DrawingContext context = target.CreateDrawingContext())
                ((IImage)image).Draw(context, new Rect(image.Size), new Rect(0, 0, size.Width, size.Height));
            using MemoryStream stream = new();
            target.Save(stream);
            return new Prepared { Bitmap = target, Png = stream.ToArray() };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
