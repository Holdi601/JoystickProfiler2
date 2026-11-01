using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using SkiaSharp;
using Xunit;

namespace JoyPro.Gui.Tests;

// Detected control spots (docs/device-reference.md, experimental): the shipped scorer loads and refuses broken files,
// the detector finds the round controls of a drawn device photo, a placement snaps only to a near spot, the switch is
// kept, and on the Layout page the rings come with the marker assistant on a photo and a click near one puts the marker
// on its centre. Opt-in render (1280, light and dark): JOYPRO_DETECT_RENDER_OUT=<folder>, optional
// JOYPRO_DETECT_RENDER_PHOTO=<a device photo> (else the drawn one) and JOYPRO_DETECT_RENDER_MODEL=<catalog id>.
[SupportedOSPlatform("windows")]
public sealed class ControlSpotTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "spots-" + Guid.NewGuid().ToString("N"));
    private readonly string? previousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");

    public ControlSpotTests()
    {
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(temp, "data"));
        DeviceReferenceService.CatalogFolderOverride = () => DeviceReferenceTests.ShippedCatalog;
        DeviceReferenceService.Reload();
        DeviceImageCache.InternetPicturesEnabled = false;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previousData);
        DeviceReferenceService.CatalogFolderOverride = () => null;
        DeviceReferenceService.Reload();
        try
        {
            Directory.Delete(temp, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static SpotScorer Scorer()
    {
        SpotScorer? scorer = SpotScorer.Cached(DeviceReferenceTests.ShippedCatalog);
        Assert.NotNull(scorer);
        return scorer!;
    }

    // The round controls of the drawn device (0..1).
    private static readonly (double X, double Y)[] Buttons = [(0.35, 0.30), (0.50, 0.30), (0.65, 0.30), (0.42, 0.55), (0.58, 0.55)];

    // A device photo as a maker shoots it: a plain light background, a dark body, light and dark round buttons with a rim.
    private static SKBitmap DrawnDevice(int size = 640)
    {
        SKBitmap bitmap = new(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(new SKColor(0xF4, 0xF4, 0xF4));
        using SKPaint body = new() { Color = new SKColor(0x2A, 0x2D, 0x33), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(size * 0.22f, size * 0.15f, size * 0.78f, size * 0.80f), size * 0.06f, size * 0.06f, body);
        using SKPaint rim = new() { Color = new SKColor(0x9A, 0xA0, 0xA8), IsAntialias = true };
        using SKPaint light = new() { Color = new SKColor(0xD8, 0xDC, 0xE0), IsAntialias = true };
        using SKPaint dark = new() { Color = new SKColor(0x10, 0x10, 0x12), IsAntialias = true };
        for (int i = 0; i < Buttons.Length; i++)
        {
            (double x, double y) = Buttons[i];
            canvas.DrawCircle((float)(x * size), (float)(y * size), size * 0.032f, rim);
            canvas.DrawCircle((float)(x * size), (float)(y * size), size * 0.024f, i % 2 == 0 ? light : dark);
        }
        return bitmap;
    }

    private static List<ControlSpot> Detect(SKBitmap bitmap, int max)
    {
        byte[] bytes = bitmap.Bytes;
        return ControlSpotDetector.Detect(bytes, bitmap.Width, bitmap.Height, bitmap.RowBytes, Scorer(), max);
    }

    [Fact]
    public void ShippedScorer_Loads_AndBrokenFilesAreRefused()
    {
        SpotScorer scorer = Scorer();
        Assert.Equal(640, scorer.Work);
        Assert.Equal(SpotScorer.Features, scorer.FeatureMean.Length);
        Assert.Throws<FormatException>(() => SpotScorer.Parse("{\"format\":\"something-else\"}"));
        Assert.Throws<FormatException>(() => SpotScorer.Parse("{\"format\":\"joypro-spot-scorer\",\"conv\":[],\"fc\":[]}"));
    }

    [Fact]
    public void Detector_FindsTheRoundControlsOfADrawnDevice()
    {
        using SKBitmap bitmap = DrawnDevice();
        List<ControlSpot> spots = Detect(bitmap, 10);
        Assert.InRange(spots.Count, 1, 10);
        Assert.All(spots, s =>
        {
            Assert.InRange(s.X, 0, 1);
            Assert.InRange(s.Y, 0, 1);
            Assert.InRange(s.Score, 0, 1);
        });
        // Every spot inside the body (none on the plain background), most buttons found.
        Assert.All(spots, s => Assert.True(s.X > 0.2 && s.X < 0.8 && s.Y > 0.13 && s.Y < 0.82, s.X + ", " + s.Y));
        int found = ControlSpotDetector.Found(Buttons.Select(b => new[] { b.X, b.Y }), spots, bitmap.Width, bitmap.Height);
        Assert.True(found >= 4, "found " + found + " of " + Buttons.Length + ": " + string.Join(" ", spots.Select(s => s.X.ToString("0.00") + "/" + s.Y.ToString("0.00"))));
        // The same picture gives the same spots (nothing random).
        List<ControlSpot> again = Detect(bitmap, 10);
        Assert.Equal(spots.Select(s => (s.X, s.Y)), again.Select(s => (s.X, s.Y)));
    }

    [Fact]
    public void Nearest_SnapsOnlyWithinTheSnapDistance()
    {
        List<ControlSpot> spots = [new ControlSpot { X = 0.5, Y = 0.5 }, new ControlSpot { X = 0.52, Y = 0.5 }];
        Assert.Same(spots[1], ControlSpotDetector.Nearest(spots, 0.515, 0.5, 1000, 1000, 0.025));
        Assert.Null(ControlSpotDetector.Nearest(spots, 0.6, 0.6, 1000, 1000, 0.025));
        Assert.Null(ControlSpotDetector.Nearest(spots, 0.5, 0.5, 0, 0, 0.025));
    }

    [Fact]
    public void Switch_IsKeptAcrossRestarts_AndOffByDefault()
    {
        Assert.False(DeviceReferenceService.DetectedSpotsEnabled);
        DeviceReferenceService.DetectedSpotsEnabled = true;
        Assert.True(DeviceReferenceService.ReadSettings().DetectedSpots);
        DeviceReferenceService.DetectedSpotsEnabled = false;
        Assert.False(DeviceReferenceService.DetectedSpotsEnabled);
    }

    // On the Layout page with the user's photo: off, nothing; on, the rings come with the assistant; a click near a ring
    // places the asked part on the ring's centre, a click far from every ring where it was clicked.
    [Fact]
    public void LayoutPage_SpotsComeWithTheAssistant_AndAClickSnaps()
    {
        string work = Path.Combine(temp, "layout");
        Directory.CreateDirectory(work);
        string photo = Path.Combine(work, "my-photo.png");
        using (SKBitmap bitmap = DrawnDevice())
        using (FileStream stream = File.Create(photo))
            bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(PhotoPageAsync(work, "thrustmaster-t16000m", photo));
            try
            {
                DeviceReferenceViewModel reference = vm.DeviceReference;
                Assert.Equal("user", reference.PictureKind);
                Assert.False(reference.ShowDetectedSpots);
                Assert.Empty(vm.WorkspaceDetectedSpots ?? []);
                reference.ShowDetectedSpots = true;
                AvaloniaRenderThread.Wait(LayoutRedesignRenderTests.WaitForAsync(() => vm.WorkspaceDetectedSpots is { Count: > 0 }));
                IReadOnlyList<Point> spots = vm.WorkspaceDetectedSpots!;
                Assert.NotEmpty(spots);
                // Not written to the settings in tests.
                Assert.False(DeviceReferenceService.DetectedSpotsEnabled);

                MarkerAssistantViewModel assistant = reference.Assistant!;
                ReferenceMarker part = assistant.NextToPlace!;
                Point spot = spots[0];
                vm.LayoutCanvasClickedCommand.Execute(new DeviceCanvasClick(spot.X + 6, spot.Y - 5, false));
                Assert.True(part.IsShown);
                Assert.Equal(spot.X / reference.PictureWidth, part.X, 3);
                Assert.Equal(spot.Y / reference.PictureHeight, part.Y, 3);

                // Far from every ring: where it was clicked.
                ReferenceMarker next = assistant.NextToPlace!;
                (double fx, double fy) = (0.05 * reference.PictureWidth, 0.95 * reference.PictureHeight);
                Assert.True(spots.All(s => Math.Abs(s.X - fx) > 40 || Math.Abs(s.Y - fy) > 40));
                vm.LayoutCanvasClickedCommand.Execute(new DeviceCanvasClick(fx, fy, false));
                Assert.Equal(0.05, next.X, 3);
                Assert.Equal(0.95, next.Y, 3);

                // Off again (or the assistant closed): no rings.
                reference.ShowDetectedSpots = false;
                AvaloniaRenderThread.Wait(LayoutRedesignRenderTests.WaitForAsync(() => vm.WorkspaceDetectedSpots is not { Count: > 0 }));
                Assert.Empty(vm.WorkspaceDetectedSpots ?? []);
            }
            finally
            {
                vm.StopGameConfigWatcher();
            }
        });
    }

    private static async Task<MainWindowViewModel> PhotoPageAsync(string work, string model, string photo)
    {
        LayoutRedesignRenderTests.LayoutFakeEngine fake = new(photo, work);
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
        vm.CurrentStage = UiStage.Layout;
        vm.IsCheatSheetsSection = false;
        await vm.ShowDeviceReferenceModelAsync(model);
        await LayoutRedesignRenderTests.WaitForAsync(() => vm.HasReferenceModel);
        await vm.DeviceReference.SetUserPhotoAsync(photo);
        vm.OpenReferenceAssistantCommand.Execute(null);
        return vm;
    }

    [Fact]
    public void RenderLayoutPageWithDetectedSpots()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_DETECT_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string work = Path.Combine(temp, "render");
        Directory.CreateDirectory(work);
        string model = Environment.GetEnvironmentVariable("JOYPRO_DETECT_RENDER_MODEL") is { Length: > 0 } m ? m : "thrustmaster-t16000m";
        string photo = Path.Combine(work, "photo.png");
        string? source = Environment.GetEnvironmentVariable("JOYPRO_DETECT_RENDER_PHOTO");
        // The user's own photo: a copy of the given photo one pixel smaller (another content than JoyPro's curated one,
        // so the page starts without positions, as with any photo of the user's).
        using (SKBitmap original = string.IsNullOrWhiteSpace(source) ? DrawnDevice() : SKBitmap.Decode(source))
        using (SKBitmap cropped = new(original.Width - 2, original.Height - 2))
        {
            original.ExtractSubset(cropped, new SKRectI(1, 1, original.Width - 1, original.Height - 1));
            using SKBitmap copy = cropped.Copy();
            using FileStream stream = File.Create(photo);
            copy.Encode(stream, SKEncodedImageFormat.Png, 100);
        }
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(PhotoPageAsync(work, model, photo));
            try
            {
                vm.DeviceReference.Assistant!.ShowMoreTools = true;
                // The assistant's panel dragged to the bottom of the picture (the grip's head with most controls on top).
                vm.SetMarkerAssistantOffset(0, 5000, keep: false);
                vm.DeviceReference.ShowDetectedSpots = true;
                AvaloniaRenderThread.Wait(LayoutRedesignRenderTests.WaitForAsync(() => vm.WorkspaceDetectedSpots is { Count: > 0 }));
                Assert.NotEmpty(vm.WorkspaceDetectedSpots ?? []);
                foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    AvaloniaRenderThread.Pump();
                    UiDesignRenderTests.RenderWindow(vm, 1280, 1250, Path.Combine(outDir, "layout-detected-spots-" + model + "-1280-" + theme + ".png"));
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
    }
}
