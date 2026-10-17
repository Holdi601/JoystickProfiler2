using System;
using System.IO;
using JoyPro.Gui.Services;
using SkiaSharp;
using Xunit;

namespace JoyPro.Gui.Tests;

// The device icon (docs/device-reference.md, "Device icon"): cut out, cropped square, kept per device name.
public sealed class DeviceIconTests
{
    [Fact]
    public void IconFromBitmap_CutsThePlainBackgroundAndCropsSquareAroundTheDevice()
    {
        using SKBitmap photo = new(new SKImageInfo(600, 300, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (SKCanvas canvas = new(photo))
        {
            canvas.Clear(SKColors.White);
            using SKPaint paint = new() { Color = new SKColor(30, 30, 30) };
            canvas.DrawRect(new SKRect(420, 60, 520, 260), paint);   // a tall dark device off to the right
        }
        byte[] png = DeviceIconService.IconFromBitmap(photo, cutBackground: true);
        using SKBitmap icon = SKBitmap.Decode(png);
        Assert.Equal(DeviceIconService.IconSize, icon.Width);
        Assert.Equal(DeviceIconService.IconSize, icon.Height);
        // The device fills the height (with a small margin) and sits in the middle; the white around it is gone.
        Assert.True(icon.GetPixel(128, 128).Alpha > 200);
        Assert.True(icon.GetPixel(128, 20).Alpha > 200);
        Assert.Equal(0, icon.GetPixel(10, 128).Alpha);
        Assert.Equal(0, icon.GetPixel(245, 128).Alpha);
    }

    [Fact]
    public void IconFromBitmap_BoundsFromAnotherPicture_KeepsThePaper()
    {
        using SKBitmap bare = new(new SKImageInfo(400, 400, SKColorType.Bgra8888, SKAlphaType.Premul));
        using SKBitmap paper = new(new SKImageInfo(400, 400, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (SKCanvas canvas = new(bare))
        {
            canvas.Clear(SKColors.Transparent);
            using SKPaint ink = new() { Color = SKColors.Black, IsStroke = true, StrokeWidth = 6 };
            canvas.DrawCircle(200, 200, 60, ink);
        }
        using (SKCanvas canvas = new(paper))
        {
            canvas.Clear(SKColors.White);
            using SKPaint ink = new() { Color = SKColors.Black, IsStroke = true, StrokeWidth = 6 };
            canvas.DrawCircle(200, 200, 60, ink);
        }
        using SKBitmap icon = SKBitmap.Decode(DeviceIconService.IconFromBitmap(paper, cutBackground: false, boundsFrom: bare));
        // Cropped to the circle, the white paper stays (a two-colour icon).
        Assert.Equal(255, icon.GetPixel(128, 128).Alpha);
        Assert.Equal(SKColors.White.Red, icon.GetPixel(128, 128).Red);
        Assert.True(icon.GetPixel(128, 6).Red < 128 || icon.GetPixel(128, 12).Red < 128);  // the circle reaches the edge
    }

    [Fact]
    public void Choice_IsKeptPerDeviceName_AndCanBeTakenBack()
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "icons-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", folder);
        try
        {
            const string device = "L-VPC Stick WarBRD {00000000-0000-0000-0000-0000000000F1}";
            Assert.Null(DeviceIconService.ChoiceOf(device));
            DeviceIconService.IconChoice choice = DeviceIconService.SetIcon(device, "celart", "Cel-shaded", [1, 2, 3]);
            Assert.True(File.Exists(choice.File));
            Assert.StartsWith(folder, choice.File, StringComparison.OrdinalIgnoreCase);
            // Windows gave the device a new GUID: still its icon.
            Assert.Equal("Cel-shaded", DeviceIconService.ChoiceOf("L-VPC Stick WarBRD {00000000-0000-0000-0000-0000000000F9}")?.Title);
            Assert.Null(DeviceIconService.ChoiceOf("R-VPC Stick WarBRD {00000000-0000-0000-0000-0000000000F1}"));
            DeviceIconService.ClearIcon(device);
            Assert.Null(DeviceIconService.ChoiceOf(device));
            Assert.False(File.Exists(choice.File));
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous);
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
