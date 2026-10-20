using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using SkiaSharp;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Layout page's "Download the reference photo" (DeviceReferenceViewModel.ReferencePhoto): only for a line drawing
// made from a maker's photo, only after the click and the device's consent, into the device picture cache, then shown
// as a picture of its own. A fake HTTP server answers; nothing goes to the internet.
[SupportedOSPlatform("windows")]
public sealed class ReferencePhotoDownloadTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "reference-photo-" + Guid.NewGuid().ToString("N"));
    private readonly string? previousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
    private readonly string? previousCache = Environment.GetEnvironmentVariable("JOYPRO_DEVICE_IMAGE_CACHE");
    private readonly bool enabled = DeviceImageCache.InternetPicturesEnabled, blocked = DeviceImageCache.InternetPicturesBlocked;
    private readonly TimeSpan spacing = DeviceImageCache.RequestSpacing;
    private readonly Func<ProcessStartInfo, Process?> start = ExternalProcess.Start;

    public ReferencePhotoDownloadTests()
    {
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(temp, "data"));
        Environment.SetEnvironmentVariable("JOYPRO_DEVICE_IMAGE_CACHE", Path.Combine(temp, "cache"));
        DeviceReferenceService.CatalogFolderOverride = () => DeviceReferenceTests.ShippedCatalog;
        DeviceReferenceService.Reload();
        DeviceImageCache.InternetPicturesEnabled = true;
        DeviceImageCache.InternetPicturesBlocked = false;
        DeviceImageCache.RequestSpacing = TimeSpan.FromMilliseconds(1);
    }

    public void Dispose()
    {
        DeviceImageCache.TestHandler = null;
        DeviceImageCache.InternetPicturesEnabled = enabled;
        DeviceImageCache.InternetPicturesBlocked = blocked;
        DeviceImageCache.RequestSpacing = spacing;
        ExternalProcess.Start = start;
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previousData);
        Environment.SetEnvironmentVariable("JOYPRO_DEVICE_IMAGE_CACHE", previousCache);
        DeviceReferenceService.CatalogFolderOverride = () => null;
        DeviceReferenceService.Reload();
        try
        {
            Directory.Delete(temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeServer(HttpStatusCode code) : HttpMessageHandler
    {
        public int Calls;
        public HttpStatusCode Code = code;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (Code != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(Code));
            using SKBitmap bitmap = new(320, 240);
            using (SKCanvas canvas = new(bitmap))
                canvas.Clear(new SKColor(90, 100, 120));
            using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 90);
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(png.ToArray()) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(response);
        }
    }

    // A shipped model with a line drawing made from one of its maker's photos.
    private static string ModelWithPhotoDrawing()
    {
        foreach (string id in DeviceReferenceService.LineArts().Select(a => a.Model).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
        {
            RefDevice? model = DeviceReferenceService.Catalog.Find(id);
            if (model == null || DevicePhotoDb.OfficialPhotos(model).Count == 0)
                continue;
            DeviceReferenceViewModel probe = new();
            probe.Model = model.Clone();
            if (DeviceReferenceService.LineArtsOf(model).Any(a => probe.ReferencePhotoOf(new ReferencePictureOption { Kind = "lineart", LineArt = a.Art, View = a.Art.View, Key = a.Key }) != null))
                return id;
        }
        throw new InvalidOperationException("no shipped line drawing with a maker's photo");
    }

    private static void WaitFor(Func<bool> done)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(20);
        while (!done() && DateTime.UtcNow < end)
        {
            AvaloniaRenderThread.Pump();
            Thread.Sleep(10);
        }
        Assert.True(done(), "timed out");
    }

    [Fact]
    public void ReferencePhoto_OnlyForPhotoDrawings_OnlyAfterClickAndYes_LandsInTheCache_AndIsAPictureOfItsOwn()
    {
        AvaloniaRenderThread.Run(() =>
        {
            FakeServer server = new(HttpStatusCode.OK);
            DeviceImageCache.TestHandler = server;
            string? opened = null;
            ExternalProcess.Start = info => { opened = info.FileName + " " + info.Arguments; return null; };
            string id = ModelWithPhotoDrawing();
            DeviceReferenceViewModel vm = new();
            AvaloniaRenderThread.Wait(vm.ShowModelAsync(id));
            AvaloniaRenderThread.Pump();

            // Not for a drawing of the class or the schematic.
            ReferencePictureOption drawing = vm.Pictures.First(p => p.Kind is "schematic" or "class");
            AvaloniaRenderThread.Wait(vm.SelectPictureAsync(drawing));
            Assert.False(vm.CanDownloadReferencePhoto);

            ReferencePictureOption lineArt = vm.Pictures.First(p => p.IsLineArt && vm.ReferencePhotoOf(p) != null);
            string url = vm.ReferencePhotoOf(lineArt)!.Url;
            AvaloniaRenderThread.Wait(vm.SelectPictureAsync(lineArt));
            Assert.True(vm.CanDownloadReferencePhoto);
            Assert.Equal(0, server.Calls);

            // The click asks first (no consent yet): nothing is downloaded until the yes.
            AvaloniaRenderThread.Wait(vm.DownloadReferencePhotoCommand.ExecuteAsync(null));
            Assert.True(vm.AwaitsReferencePhotoConsent);
            Assert.True(vm.AsksPhotoConsent);
            Assert.Equal(0, server.Calls);
            vm.AnswerPhotoConsentCommand.Execute("yes");
            WaitFor(() => vm.HasReferencePhotoFile && !vm.IsDownloadingReferencePhoto && vm.PictureKind == "vendor");
            Assert.False(vm.AwaitsReferencePhotoConsent);
            Assert.Equal(1, server.Calls);
            Assert.StartsWith(Path.Combine(temp, "cache"), vm.ReferencePhotoFile);
            Assert.True(File.Exists(vm.ReferencePhotoFile));
            Assert.Equal(url, vm.SelectedPicture!.Source);
            Assert.Contains(vm.Pictures, p => p.Kind == "vendor" && p.Source == url);
            // The fake photo is not the one JoyPro drew from: a reference without markers.
            Assert.Equal(0, vm.KnownMarkerCount);
            Assert.Contains("without markers", vm.ReferencePhotoStatus);

            vm.ShowReferencePhotoInFolderCommand.Execute(null);
            Assert.NotNull(opened);
            Assert.Contains("/select,", opened);
            Assert.Contains(vm.ReferencePhotoFile, opened);

            // An error in plain words, nothing saved (the photo is gone from the maker's site).
            File.Delete(vm.ReferencePhotoFile);
            server.Code = HttpStatusCode.NotFound;
            DeviceReferenceViewModel again = new();
            AvaloniaRenderThread.Wait(again.ShowModelAsync(id));
            ReferencePictureOption sameDrawing = again.Pictures.First(p => p.IsLineArt && again.ReferencePhotoOf(p)?.Url == url);
            AvaloniaRenderThread.Wait(again.SelectPictureAsync(sameDrawing));
            AvaloniaRenderThread.Wait(again.DownloadReferencePhotoCommand.ExecuteAsync(null));
            Assert.False(again.AwaitsReferencePhotoConsent);
            Assert.Equal("The maker has removed this photo from the site.", again.ReferencePhotoStatus);
            Assert.False(again.HasReferencePhotoFile);
            Assert.True(again.PictureKind == "lineart");
        });
    }
}
