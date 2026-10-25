using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using SkiaSharp;
using Xunit;

namespace JoyPro.Gui.Tests;

// Reference photos on this PC (docs/device-reference.md): the photo fields (source, licence, archive, size), the allowed
// review sites, the download from the original address with the SHA-256 check, the Wayback fallback on a dead address
// or a changed photo, the store in the data folder, the offer and the persisted "download automatically" setting.
// A fake HTTP server answers; nothing goes to the internet.
[SupportedOSPlatform("windows")]
[Collection("DeviceImageCache")]
public sealed class ReferencePhotoStoreTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "reference-store-" + Guid.NewGuid().ToString("N"));
    private readonly string? previousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
    private readonly bool enabled = DeviceImageCache.InternetPicturesEnabled, blocked = DeviceImageCache.InternetPicturesBlocked;
    private readonly TimeSpan spacing = DeviceImageCache.RequestSpacing;
    private readonly ReferencePhotoStore previousStore = ReferencePhotoStore.Default;

    public ReferencePhotoStoreTests()
    {
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(temp, "data"));
        DeviceReferenceService.CatalogFolderOverride = () => DeviceReferenceTests.ShippedCatalog;
        DeviceReferenceService.Reload();
        DeviceImageCache.RequestSpacing = TimeSpan.FromMilliseconds(1);
    }

    public void Dispose()
    {
        DeviceImageCache.TestHandler = null;
        DeviceImageCache.InternetPicturesEnabled = enabled;
        DeviceImageCache.InternetPicturesBlocked = blocked;
        DeviceImageCache.RequestSpacing = spacing;
        ReferencePhotoStore.Default = previousStore;
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
    }

    private static byte[] Png(byte shade)
    {
        using SKBitmap bitmap = new(64, 48);
        using (SKCanvas canvas = new(bitmap))
            canvas.Clear(new SKColor(shade, 100, 120));
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 90);
        return png.ToArray();
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // A fake HTTP server: address -> bytes; any other address answers 404. Counts the requests per address.
    private sealed class FakeServer : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            lock (Requests)
                Requests.Add(url);
            if (!Files.TryGetValue(url, out byte[]? data))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(response);
        }
    }

    // A store whose downloads go through DeviceImageCache's real HTTP path to the fake server.
    private ReferencePhotoStore StoreWith(FakeServer server)
    {
        DeviceImageCache.TestHandler = server;
        return new ReferencePhotoStore(Path.Combine(temp, "store")) { Fetch = (uri, progress, _) => DeviceImageCache.FetchBytesAsync(uri, progress) };
    }

    [Fact]
    public void PhotoFields_ParseAndRoundTrip()
    {
        string json = """
            { "photos": [ { "model": "x", "url": "https://upload.wikimedia.org/a.jpg", "view": "front", "page": "https://commons.wikimedia.org/wiki/File:A.jpg",
                            "sha256": "ABC", "confidence": "high", "source": "commons", "site": "Wikimedia Commons",
                            "licence": "CC BY-SA 3.0, Someone (Wikimedia Commons)", "archive": "https://web.archive.org/web/2024id_/https://upload.wikimedia.org/a.jpg",
                            "width": 1600, "height": 1200 } ] }
            """;
        DevicePhoto photo = DevicePhotoDb.ParsePhotoFile(json).Single();
        Assert.Equal("commons", photo.Source);
        Assert.True(photo.IsReview);
        Assert.Equal("Wikimedia Commons", photo.Site);
        Assert.StartsWith("CC BY-SA 3.0", photo.Licence);
        Assert.Equal(1600, photo.Width);
        Assert.Equal(1200, photo.Height);
        Assert.Equal("abc", photo.Sha256);
        Assert.Equal(photo.Archive, DevicePhotoDb.ArchiveUrl(photo));
        Dictionary<string, object> back = DevicePhotoDb.PhotoToJson(photo);
        Assert.Equal("commons", back["source"]);
        Assert.Equal(photo.Licence, back["licence"]);
        Assert.Equal(photo.Archive, back["archive"]);
        Assert.Equal(1600, back["width"]);
        // A maker's photo keeps its short form (no source field), and without a snapshot the newest one is asked for.
        DevicePhoto maker = new() { Model = "x", Url = "https://maker.example/p.jpg" };
        Assert.False(DevicePhotoDb.PhotoToJson(maker).ContainsKey("source"));
        Assert.Equal("https://web.archive.org/web/2id_/https://maker.example/p.jpg", DevicePhotoDb.ArchiveUrl(maker));
    }

    [Fact]
    public void AllowedSources_MakerHostsReviewSitesAndArchiveOnlyPhotos()
    {
        RefDevice device = new() { Id = "x", Vendor = "Maker" };
        device.OfficialHosts.AddRange(["maker.example", "cdn.shopify.com/s/files/1/0001/"]);
        Assert.True(DevicePhotoDb.IsAllowed(device, new DevicePhoto { Url = "https://img.maker.example/a.jpg" }));
        Assert.True(DevicePhotoDb.IsAllowed(device, new DevicePhoto { Url = "https://cdn.shopify.com/s/files/1/0001/files/a.jpg" }));
        // Another shop on the same CDN is not the maker.
        Assert.False(DevicePhotoDb.IsAllowed(device, new DevicePhoto { Url = "https://cdn.shopify.com/s/files/1/0002/files/a.jpg" }));
        Assert.False(DevicePhotoDb.IsAllowed(device, new DevicePhoto { Url = "https://shop.example/a.jpg" }));
        // A review photo only from a listed review site (the shipped _sources.json), never as a "maker" photo.
        Assert.True(DevicePhotoDb.IsAllowed(device, new DevicePhoto { Url = "https://www.simflight.com/a.jpg", Source = "review" }));
        Assert.False(DevicePhotoDb.IsAllowed(device, new DevicePhoto { Url = "https://www.simflight.com/a.jpg" }));
        Assert.False(DevicePhotoDb.IsAllowed(device, new DevicePhoto { Url = "https://www.amazon.com/a.jpg", Source = "review" }));
        Assert.True(DevicePhotoDb.IsAllowed(device, new DevicePhoto { Url = "https://upload.wikimedia.org/a.jpg", Source = "commons" }));
        Assert.False(DevicePhotoDb.IsAllowed(device, new DevicePhoto { Url = "https://upload.wikimedia.org/a.jpg", Source = "review" }));
        // An old http maker address: only through its Wayback snapshot.
        Assert.False(DevicePhotoDb.IsAllowed(device, new DevicePhoto { Url = "http://www.maker.example/old.jpg" }));
        DevicePhoto archived = new() { Url = "http://www.maker.example/old.jpg", Archive = "https://web.archive.org/web/2009id_/http://www.maker.example/old.jpg" };
        Assert.True(DevicePhotoDb.IsAllowed(device, archived));
        Assert.True(archived.ArchiveOnly);
        Assert.False(DevicePhotoDb.IsAllowed(device, new DevicePhoto { Url = "http://www.maker.example/old.jpg", Archive = "https://evil.example/web/x" }));
    }

    [Fact]
    public void ShippedPhotos_AllAllowedWithLicenceForReviews()
    {
        DeviceCatalog catalog = DeviceReferenceService.Catalog;
        List<string> problems = [];
        foreach (RefDevice model in catalog.Devices)
        {
            foreach (DevicePhoto photo in model.Photos.Where(p => p.IsReview || p.Archive.Length > 0))
            {
                if (!DevicePhotoDb.IsAllowed(model, photo))
                    problems.Add(model.Id + ": not allowed " + photo.Url);
                if (photo.IsReview && photo.Licence.Length == 0)
                    problems.Add(model.Id + ": review photo without a licence note " + photo.Url);
                if (photo.Archive.Length > 0 && !DevicePhotoDb.IsArchiveUrl(photo.Archive))
                    problems.Add(model.Id + ": archive is not a Wayback address " + photo.Archive);
            }
        }
        Assert.Empty(problems);
        Assert.True(catalog.Devices.Count(m => DevicePhotoDb.UsablePhotos(m).Count > 0) >= 420);
        Assert.True(catalog.Devices.Count(m => DevicePhotoDb.UsablePhotos(m).Select(p => p.View).Where(v => !v.StartsWith("gallery-", StringComparison.Ordinal)).Distinct().Count() >= 3) >= 260);
    }

    [Fact]
    public async Task Download_FromOriginal_HashChecked_StoredInDataFolder()
    {
        byte[] data = Png(10);
        FakeServer server = new();
        server.Files["https://maker.example/front.png"] = data;
        ReferencePhotoStore store = StoreWith(server);
        DevicePhoto photo = new() { Model = "test-stick", Url = "https://maker.example/front.png", View = "front", Sha256 = Sha(data) };

        ReferencePhotoStore.PhotoResult result = await store.EnsureAsync("test-stick", photo);

        Assert.NotNull(result.File);
        Assert.Equal("original", result.Origin);
        Assert.False(result.Changed);
        Assert.StartsWith(Path.Combine(temp, "store", "test-stick"), result.File);
        Assert.Equal(data, File.ReadAllBytes(result.File!));
        Assert.DoesNotContain(server.Requests, r => r.Contains("web.archive.org"));
        // Kept: a new store on the same folder knows it from index.json, without downloading again.
        ReferencePhotoStore again = StoreWith(server);
        Assert.Equal(result.File, again.FileOf(photo.Url));
        int calls = server.Requests.Count;
        await again.EnsureAsync("test-stick", photo);
        Assert.Equal(calls, server.Requests.Count);
    }

    [Fact]
    public async Task HashMismatch_FallsBackToTheArchive()
    {
        byte[] recorded = Png(20), changed = Png(200);
        FakeServer server = new();
        server.Files["https://maker.example/side.png"] = changed;
        server.Files["https://web.archive.org/web/2023id_/https://maker.example/side.png"] = recorded;
        ReferencePhotoStore store = StoreWith(server);
        DevicePhoto photo = new()
        {
            Model = "test-stick", Url = "https://maker.example/side.png", View = "side", Sha256 = Sha(recorded),
            Archive = "https://web.archive.org/web/2023id_/https://maker.example/side.png"
        };

        ReferencePhotoStore.PhotoResult result = await store.EnsureAsync("test-stick", photo);

        Assert.Equal("archive", result.Origin);
        Assert.False(result.Changed);
        Assert.Equal(recorded, File.ReadAllBytes(result.File!));
        Assert.Equal("archive", store.Entry(photo.Url)!.Origin);
    }

    [Fact]
    public async Task DeadLink_UsesTheNewestSnapshot_AndAChangedPhotoWithoutCopyIsMarked()
    {
        byte[] recorded = Png(30);
        FakeServer server = new();
        // The address is gone (404); no snapshot recorded: the newest one is asked for.
        server.Files["https://web.archive.org/web/2id_/https://maker.example/top.png"] = recorded;
        ReferencePhotoStore store = StoreWith(server);
        DevicePhoto dead = new() { Model = "test-stick", Url = "https://maker.example/top.png", View = "top", Sha256 = Sha(recorded) };
        ReferencePhotoStore.PhotoResult result = await store.EnsureAsync("test-stick", dead);
        Assert.Equal("archive", result.Origin);
        Assert.Contains("https://maker.example/top.png", server.Requests);

        // The maker changed the photo and the archive has no copy: the new photo, marked as changed.
        byte[] now = Png(99);
        server.Files["https://maker.example/back.png"] = now;
        DevicePhoto changed = new() { Model = "test-stick", Url = "https://maker.example/back.png", View = "back", Sha256 = Sha(Png(1)) };
        ReferencePhotoStore.PhotoResult second = await store.EnsureAsync("test-stick", changed);
        Assert.True(second.Changed);
        Assert.Equal(now, File.ReadAllBytes(second.File!));

        // Nothing anywhere: no file, a reason in plain words.
        DevicePhoto gone = new() { Model = "test-stick", Url = "https://maker.example/gone.png", View = "base", Sha256 = Sha(Png(2)) };
        ReferencePhotoStore.PhotoResult third = await store.EnsureAsync("test-stick", gone);
        Assert.Null(third.File);
        Assert.NotEqual(string.Empty, third.Error);

        // An archive-only photo (old http site) never asks the plain http address.
        byte[] old = Png(77);
        server.Files["https://web.archive.org/web/2009id_/http://www.maker.example/old.png"] = old;
        DevicePhoto archived = new() { Model = "test-stick", Url = "http://www.maker.example/old.png", View = "front", Sha256 = Sha(old), Archive = "https://web.archive.org/web/2009id_/http://www.maker.example/old.png" };
        Assert.Equal("archive", (await store.EnsureAsync("test-stick", archived)).Origin);
        Assert.DoesNotContain(server.Requests, r => r.StartsWith("http://", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ErrorPage_IsNotAPicture()
    {
        FakeServer server = new();
        server.Files["https://maker.example/page.png"] = "<html>moved</html>"u8.ToArray();
        ReferencePhotoStore store = StoreWith(server);
        ReferencePhotoStore.PhotoResult result = await store.EnsureAsync("m", new DevicePhoto { Model = "m", Url = "https://maker.example/page.png", View = "front" });
        Assert.Null(result.File);
    }

    [Fact]
    public void AutoDownloadSetting_Persists_AndNeverSwitchesItOff()
    {
        Assert.False(DevicePhotoConsent.AutoDownload);
        DevicePhotoConsent.AutoDownload = true;
        // Read back from <data>\DeviceReference\settings.json (what a restart reads).
        string settings = File.ReadAllText(Path.Combine(temp, "data", "DeviceReference", "settings.json"));
        Assert.Contains("\"AutoDownloadPhotos\": true", settings);
        Assert.True(DevicePhotoConsent.AutoDownload);
        Assert.Equal(DevicePhotoConsent.Always, DevicePhotoConsent.Mode);
        DevicePhotoConsent.Mode = DevicePhotoConsent.Never;
        Assert.False(DevicePhotoConsent.AutoDownload);
        DevicePhotoConsent.Mode = DevicePhotoConsent.Ask;
        Assert.False(DevicePhotoConsent.AutoDownload);
    }

    // A shipped model with a few usable photos (the fake server serves them with their recorded hashes unknown).
    private static RefDevice ModelWithPhotos() =>
        DeviceReferenceService.Catalog.Devices.Where(m => DevicePhotoDb.UsablePhotos(m).Count >= 2 && DevicePhotoDb.UsablePhotos(m).All(p => !p.ArchiveOnly))
            .OrderBy(m => m.Id, StringComparer.Ordinal).First();

    private static FakeServer ServerFor(RefDevice model, out Dictionary<string, byte[]> files)
    {
        FakeServer server = new();
        files = [];
        byte shade = 40;
        foreach (DevicePhoto photo in DevicePhotoDb.UsablePhotos(model))
        {
            byte[] data = Png(shade++);
            server.Files[photo.Url] = data;
            // The recorded hash is the real maker file's; the fake one differs: the archive answers with "the same".
            server.Files[DevicePhotoDb.ArchiveUrl(photo)] = data;
            files[photo.Url] = data;
        }
        return server;
    }

    [Fact]
    public async Task Offer_OneClickDownloadsEveryPhoto_ConsentGivenAndProgressShown()
    {
        RefDevice model = ModelWithPhotos();
        FakeServer server = ServerFor(model, out _);
        ReferencePhotoStore store = StoreWith(server);
        MainWindowViewModel vm = new(loadPersistedUiSettings: false) { ReferencePhotoStoreFactory = () => store };

        await vm.CheckReferencePhotosAsync([model.Id]);
        Assert.True(vm.HasReferencePhotoOffer);
        Assert.Contains(model.Name, vm.ReferencePhotoOfferText);
        Assert.Empty(server.Requests);

        await vm.AcceptReferencePhotosCommand.ExecuteAsync(null);
        Assert.False(vm.HasReferencePhotoOffer);
        Assert.False(vm.IsDownloadingReferencePhotos);
        Assert.Equal(1.0, vm.ReferencePhotoDownloadProgress, 3);
        Assert.True(store.HasAll(model));
        Assert.True(DevicePhotoConsent.Allows(model.Id));
        // Everything there: nothing offered again.
        await vm.CheckReferencePhotosAsync([model.Id]);
        Assert.False(vm.HasReferencePhotoOffer);
    }

    [Fact]
    public async Task Offer_NotNowAndNo_AreRemembered_AlwaysDownloadsInTheBackground()
    {
        RefDevice model = ModelWithPhotos();
        FakeServer server = ServerFor(model, out _);
        ReferencePhotoStore store = StoreWith(server);
        MainWindowViewModel vm = new(loadPersistedUiSettings: false) { ReferencePhotoStoreFactory = () => store };

        await vm.CheckReferencePhotosAsync([model.Id]);
        vm.DismissReferencePhotosCommand.Execute(null);
        await vm.CheckReferencePhotosAsync([model.Id]);
        Assert.False(vm.HasReferencePhotoOffer);

        MainWindowViewModel second = new(loadPersistedUiSettings: false) { ReferencePhotoStoreFactory = () => store };
        await second.CheckReferencePhotosAsync([model.Id]);
        second.NeverReferencePhotosCommand.Execute(null);
        Assert.Equal(PhotoConsentState.Denied, DevicePhotoConsent.For(model.Id));
        await second.CheckReferencePhotosAsync([model.Id]);
        Assert.False(second.HasReferencePhotoOffer);
        Assert.Empty(server.Requests);

        // "Always download": persisted, and the next device that appears is downloaded without asking.
        DevicePhotoConsent.ForgetAnswers();
        MainWindowViewModel third = new(loadPersistedUiSettings: false) { ReferencePhotoStoreFactory = () => store };
        await third.CheckReferencePhotosAsync([model.Id]);
        await third.AlwaysReferencePhotosCommand.ExecuteAsync(null);
        Assert.True(third.AutoDownloadReferencePhotos);
        Assert.True(store.HasAll(model));
        RefDevice other = DeviceReferenceService.Catalog.Devices.Where(m => m.Id != model.Id && DevicePhotoDb.UsablePhotos(m).Count >= 1 && DevicePhotoDb.UsablePhotos(m).All(p => !p.ArchiveOnly))
            .OrderBy(m => m.Id, StringComparer.Ordinal).First();
        foreach (KeyValuePair<string, byte[]> file in ServerFor(other, out _).Files)
            server.Files[file.Key] = file.Value;
        await third.CheckReferencePhotosAsync([other.Id]);
        Assert.False(third.HasReferencePhotoOffer);
        Assert.True(store.HasAll(other));
        DevicePhotoConsent.AutoDownload = false;
        DevicePhotoConsent.Mode = DevicePhotoConsent.Ask;
    }

    // The offer on the Layout page and the Start page, the progress card, the privacy setting: rendered in a hidden
    // window (1280 / 2267, Light and Dark) into JOYPRO_REFPHOTO_RENDER_OUT when set; the offer's texts and buttons are
    // on screen and inside the window.
    [Fact]
    public void Offer_Renders_OnTheLayoutAndStartPages()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_REFPHOTO_RENDER_OUT");
        // The device the page shows (the Warthog throttle of the fake engine).
        RefDevice model = DeviceReferenceService.Catalog.Find("thrustmaster-hotas-warthog-throttle")!;
        AvaloniaRenderThread.Run(() =>
        {
            try
            {
                foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
                {
                    foreach ((int width, int height) in new[] { (1280, 800), (2267, 1250) })
                    {
                        string stand = Path.Combine(temp, "stand-in.png");
                        using (RenderTargetBitmap bitmap = new(new PixelSize(400, 300)))
                            bitmap.Save(stand);
                        LayoutRedesignRenderTests.LayoutFakeEngine fake = new(stand, temp);
                        FakeServer server = ServerFor(model, out _);
                        ReferencePhotoStore store = StoreWith(server);
                        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
                        {
                            EnginePath = typeof(MainWindowViewModel).Assembly.Location, ReferencePhotoStoreFactory = () => store
                        };
                        vm.CurrentStage = UiStage.Layout;
                        vm.IsCheatSheetsSection = false;
                        AvaloniaRenderThread.Wait(vm.ShowWorkspaceDeviceAsync("Throttle - HOTAS Warthog {00000000-0000-0000-0000-0000000000a2}"));
                        AvaloniaRenderThread.Wait(LayoutRedesignRenderTests.WaitForAsync(() => vm.HasReferenceModel));
                        AvaloniaRenderThread.Wait(vm.CheckReferencePhotosAsync([model.Id]));
                        Assert.True(vm.HasReferencePhotoOffer);
                        using (HiddenWindow main = NavPagesTests.Show(vm, variant, width, height))
                        {
                            main.Settle();
                            if (outDir != null)
                            {
                                Directory.CreateDirectory(outDir);
                                NavPagesTests.Save(main, Path.Combine(outDir, "refphotos-layout-offer-" + width + "-" + theme + ".png"));
                            }
                            Border? offer = main.Page.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "LayoutReferencePhotoOffer");
                            Assert.True(offer != null, "no offer; named borders: " + string.Join(",", main.Page.GetVisualDescendants().OfType<Border>().Select(b => b.Name).Where(n => !string.IsNullOrEmpty(n)).Take(40)));
                            Assert.True(offer.IsEffectivelyVisible);
                            Assert.Contains(offer.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? string.Empty).Contains(model.Name));
                            Rect bounds = offer.Bounds;
                            Point? origin = offer.TranslatePoint(new Point(0, 0), main.Page);
                            Assert.NotNull(origin);
                            Assert.True(origin!.Value.X >= 0 && origin.Value.X + bounds.Width <= width + 1, "the offer leaves the window");
                            // A banner above the picture: it never covers the canvas.
                            Control canvas = main.Page.GetVisualDescendants().OfType<Control>().First(c => c.Name == "DeviceReferenceHost");
                            Point? canvasTop = canvas.TranslatePoint(new Point(0, 0), main.Page);
                            Assert.True(canvasTop!.Value.Y >= origin.Value.Y + bounds.Height - 0.5, "the offer covers the picture");
                            if (outDir != null)
                            {
                                Directory.CreateDirectory(outDir);
                                NavPagesTests.Save(main, Path.Combine(outDir, "refphotos-layout-offer-" + width + "-" + theme + ".png"));
                            }
                            // Downloading: the progress card instead of the offer.
                            vm.ReferencePhotoOfferText = string.Empty;
                            vm.IsDownloadingReferencePhotos = true;
                            vm.ReferencePhotoDownloadStatus = JoyPro.Gui.Localization.Loc.T("Downloading reference photos: {0} of {1} ({2})...", 3, 7, model.Name);
                            vm.ReferencePhotoDownloadProgress = 3 / 7.0;
                            main.Settle();
                            if (outDir != null)
                                NavPagesTests.Save(main, Path.Combine(outDir, "refphotos-layout-progress-" + width + "-" + theme + ".png"));
                            vm.IsDownloadingReferencePhotos = false;
                        }
                        AvaloniaRenderThread.Wait(vm.CheckReferencePhotosAsync([model.Id]));
                        vm.CurrentStage = UiStage.Start;
                        using (HiddenWindow main = NavPagesTests.Show(vm, variant, width, height))
                        {
                            main.Settle();
                            Control start = main.Page.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "StartReferencePhotoOffer");
                            Assert.True(start.IsEffectivelyVisible);
                            if (outDir != null)
                                NavPagesTests.Save(main, Path.Combine(outDir, "refphotos-start-offer-" + width + "-" + theme + ".png"));
                        }
                        // No profile is open: nothing on the Layout or Start page ran a profile command
                        // ("Profile path required" in the status line).
                        Assert.Empty(vm.ProfileCommandsWithoutProfile);
                        Assert.NotEqual("Profile path required for this command.", vm.StatusText);
                        vm.StopGameConfigWatcher();
                    }
                }
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
    }
}
