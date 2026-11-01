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
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using JoyPro.DeviceReference;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using SkiaSharp;
using Xunit;

namespace JoyPro.Gui.Tests;

// Photos of cockpits no game has (docs/cockpits.md "Photos of cockpits no game has"): the shipped verified photos and
// their labels, "Download photos" against a fake server (SHA-256 check, the web archive's copy as the fallback, a dead
// photo reported), the photo page with JoyPro's labels on their points and the photo's credit, and that a variant with
// good pictures from a game gets no photo page. Nothing goes to the internet.
[SupportedOSPlatform("windows")]
[Collection("DeviceImageCache")]
public sealed class CockpitPhotoTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "cockpit-photos-" + Guid.NewGuid().ToString("N"));
    private readonly string? previousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
    private readonly TimeSpan spacing = DeviceImageCache.RequestSpacing;

    public CockpitPhotoTests()
    {
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(temp, "data"));
        DeviceImageCache.RequestSpacing = TimeSpan.FromMilliseconds(1);
    }

    public void Dispose()
    {
        DeviceImageCache.TestHandler = null;
        DeviceImageCache.RequestSpacing = spacing;
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previousData);
        CockpitService.FolderOverride = () => null;
        CockpitService.Reload();
        try
        {
            Directory.Delete(temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] Png(byte shade, int width = 400, int height = 300)
    {
        using SKBitmap bitmap = new(width, height);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(new SKColor(shade, 90, 110));
            using SKPaint panel = new() { Color = new SKColor(30, 30, 30) };
            canvas.DrawRect(width * 0.3f, height * 0.4f, width * 0.4f, height * 0.3f, panel);
        }
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 90);
        return png.ToArray();
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // address -> bytes; any other address answers 404.
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

    // A cockpit database with one aircraft and three verified photos: "front" (alive), "overhead" (changed on its site,
    // the archive has the recorded one) and "pedestal" (dead, no copy anywhere). The front photo has labels.
    private (string Folder, byte[] Front, byte[] Overhead, byte[] Pedestal) WriteCatalog()
    {
        string folder = Path.Combine(temp, "Cockpits");
        Directory.CreateDirectory(Path.Combine(folder, CockpitPhotos.LabelFolderName));
        byte[] front = Png(40), overhead = Png(120), pedestal = Png(200);
        string Photo(string name, byte[] data, string extra = "") =>
            "{ \"url\": \"https://upload.example/" + name + ".png\", \"page\": \"https://commons.wikimedia.org/wiki/File:" + name + ".png\", \"view\": \"cockpit\"," +
            " \"credit\": \"Test Photographer\", \"license\": \"cc-by-sa-4.0\", \"caption\": \"Test jet " + name + "\", \"sha256\": \"" + Sha(data) + "\"," +
            " \"width\": 400, \"height\": 300, \"source\": \"commons\"" + extra + " }";
        string json = """
            { "format": "joypro-cockpits", "version": 2, "aircraft": [ {
              "id": "test-jet", "name": "Test Jet", "category": "fixed-wing", "maker": "Test", "nations": ["US"], "role": "fighter", "era": "modern",
              "summary": "A jet for the tests.",
              "photos": [ PHOTOS ],
              "variants": [ { "id": "test-jet-a", "name": "Test Jet A", "years": "2020-", "games": [ { "game": "MSFS2024" } ], "confidence": "low",
                "sources": [ { "title": "Test", "url": "https://example.com", "kind": "reference" } ],
                "controls": [ { "id": "stick.trigger", "device": "stick", "name": "Trigger", "type": "trigger", "where": "front", "functions": [ { "dir": "", "text": "Gun" } ] },
                              { "id": "throttle.axis.main", "device": "throttle", "name": "Throttle", "type": "axis", "where": "left", "functions": [ { "dir": "axis", "text": "Thrust" } ] } ],
                "panels": [ { "id": "main-panel", "name": "Main instrument panel", "area": "front-center", "text": "The instruments in front of the pilot. More text." } ] } ] } ] }
            """.Replace("PHOTOS", string.Join(",", Photo("front", front), Photo("overhead", overhead, ", \"archive\": \"https://web.archive.org/web/2024id_/https://upload.example/overhead.png\""), Photo("pedestal", pedestal)));
        File.WriteAllText(Path.Combine(folder, "test.json"), json);
        File.WriteAllText(Path.Combine(folder, CockpitPhotos.LabelFolderName, "test-jet." + Sha(front)[..12] + ".json"), """
            { "format": "joypro-cockpit-photo-labels", "version": 1, "aircraft": "test-jet",
              "photo": { "url": "https://upload.example/front.png", "sha256": "SHA" }, "title": "Front cockpit",
              "labels": [ { "n": 1, "text": "Main instrument panel", "x": 0.5, "y": 0.55, "panel": "main-panel" },
                          { "n": 2, "text": "Stick", "x": 0.52, "y": 0.9, "panel": "stick" },
                          { "n": 3, "text": "Canopy frame", "x": 0.1, "y": 0.1, "note": "The metal frame that holds the glass bubble over the pilot." } ] }
            """.Replace("SHA", Sha(front)));
        return (folder, front, overhead, pedestal);
    }

    [Fact]
    public void ShippedPhotos_AreVerifiedWithLabelsOnThePhoto()
    {
        CockpitCatalog catalog = CockpitCatalog.Load(CockpitDatabaseTests.ShippedCockpits);
        List<(CockpitAircraft Aircraft, CockpitPhoto Photo)> verified = catalog.Aircraft.SelectMany(a => a.Photos.Where(p => p.Verified).Select(p => (a, p))).ToList();
        Assert.True(verified.Count >= 25, "verified cockpit photos: " + verified.Count);
        Assert.Empty(catalog.LabelOrphans.Select(o => o.File));
        Assert.Empty(CockpitPhotos.Check(catalog));
        foreach ((CockpitAircraft a, CockpitPhoto p) in verified)
        {
            Assert.StartsWith("https://", p.Url);
            Assert.True(p.Width > 0 && p.Height > 0, a.Id + " " + p.Url);
            Assert.False(string.IsNullOrWhiteSpace(p.Credit), a.Id + " " + p.Url);
            Assert.NotNull(p.Labels);
            Assert.True(p.Labels!.Labels.Count >= 3, a.Id + " " + p.Url + ": " + p.Labels.Labels.Count + " labels");
            // The fallback when the address dies: a pinned raw Wayback capture of the photo (cockpit_photos.py
            // snapshots --all checked its bytes against the recorded hash).
            Assert.Matches(@"^https://web\.archive\.org/web/\d{14}id_/https://", p.Archive);
            Assert.EndsWith(p.Url, p.Archive);
            // A fictional craft has no photo of a real cockpit; its maker's own pictures are links like any other.
            if (a.Fiction)
                Assert.NotEqual("commons", p.Source);
        }
    }

    [Fact]
    public void Check_ReportsLabelsOffThePhoto_UnknownPanels_AndOrphans()
    {
        (string folder, byte[] front, _, _) = WriteCatalog();
        string labels = Path.Combine(folder, CockpitPhotos.LabelFolderName, "test-jet." + Sha(front)[..12] + ".json");
        File.WriteAllText(labels, File.ReadAllText(labels).Replace("\"x\": 0.1,", "\"x\": 1.4,").Replace("\"panel\": \"main-panel\"", "\"panel\": \"no-such-panel\""));
        File.WriteAllText(Path.Combine(folder, CockpitPhotos.LabelFolderName, "test-jet.000000000000.json"),
            "{ \"format\": \"joypro-cockpit-photo-labels\", \"aircraft\": \"test-jet\", \"photo\": { \"sha256\": \"" + new string('0', 64) + "\" }, \"labels\": [] }");
        CockpitCatalog catalog = CockpitCatalog.Load(folder);
        List<string> problems = CockpitPhotos.Check(catalog);
        Assert.Contains(problems, p => p.Contains("x and y lie within the photo"));
        Assert.Contains(problems, p => p.Contains("'no-such-panel' is not a panel or grip"));
        Assert.Contains(problems, p => p.Contains("no verified photo of 'test-jet'"));
    }

    [Theory]
    [InlineData("cc-by-sa-4.0", "CC BY-SA 4.0")]
    [InlineData("cc-by-2.0", "CC BY 2.0")]
    [InlineData("cc0", "CC0")]
    [InlineData("public-domain", "public domain")]
    [InlineData("gfdl-1.2", "GFDL 1.2")]
    [InlineData("copyrighted", "copyrighted")]
    public void LicenceName_IsReadable(string id, string name) => Assert.Equal(name, CockpitManualPages.LicenceName(id));

    // The labels are data drawn on the photo: each dot at its point, each box inside the photo and clear of the others,
    // the line from the box to the point; the list and the credit (author, licence, site) below.
    [Fact]
    public void PhotoPage_PutsEveryLabelOnItsPoint_AndShowsTheCredit()
    {
        (string folder, byte[] front, _, _) = WriteCatalog();
        CockpitCatalog catalog = CockpitCatalog.Load(folder);
        CockpitVariant v = catalog.Resolve(catalog.Aircraft[0].Variants[0]);
        CockpitPhoto photo = catalog.Aircraft[0].Photos[0];
        string file = Path.Combine(temp, "front.png");
        File.WriteAllBytes(file, front);
        ManualPage page = CockpitManualPages.PhotoPage(v, photo, file, new ManualPageOptions { Folder = folder });

        Assert.Equal("photo", page.Kind);
        Assert.Equal("Photo: Front cockpit", page.Title);
        ManualPageItem drawing = Assert.Single(page.Items, i => i.Kind == "drawing");
        Assert.Equal(file, drawing.Art!.Picture);
        Assert.Equal(400.0 / 300.0, drawing.W / drawing.H, 3);
        List<ManualPageItem> boxes = page.Items.Where(i => i.Kind == "cell" && i.Tag.StartsWith("photo:", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, boxes.Count);
        foreach (CockpitPhotoLabel label in photo.Labels!.Labels)
        {
            double px = drawing.X + label.X * drawing.W, py = drawing.Y + label.Y * drawing.H;
            string tag = "photo:" + photo.Sha256[..12] + ":" + label.N;
            Assert.Contains(page.Items, i => i.Kind == "dot" && i.Tag == tag && Math.Abs(i.X + i.W / 2 - px) < 0.01 && Math.Abs(i.Y + i.H / 2 - py) < 0.01);
            ManualPageItem line = page.Items.Last(i => i.Kind == "line" && i.Tag == tag);
            Assert.Equal(px, line.Points![2], 3);
            Assert.Equal(py, line.Points![3], 3);
            ManualPageItem box = boxes.Single(b => b.Tag == tag);
            Assert.StartsWith(label.N + " " + label.Text.Split(' ')[0], box.Lines[0]);
            Assert.True(box.X >= drawing.X && box.Y >= drawing.Y && box.X + box.W <= drawing.X + drawing.W + 0.01 && box.Y + box.H <= drawing.Y + drawing.H + 0.01, "label " + label.N + " leaves the photo");
        }
        for (int i = 0; i < boxes.Count; i++)
        {
            for (int k = i + 1; k < boxes.Count; k++)
            {
                ManualPageItem a = boxes[i], b = boxes[k];
                Assert.False(a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H, a.Tag + " overlaps " + b.Tag);
            }
        }
        // Links: the panel's page and the stick's page; the list gives the panel's first sentence and the label's note.
        Assert.Contains(page.Items, i => i.Tag.EndsWith(":1", StringComparison.Ordinal) && i.Link == "panel:main-panel");
        Assert.Contains(page.Items, i => i.Tag.EndsWith(":2", StringComparison.Ordinal) && i.Link == "device:stick");
        Assert.Contains("The instruments in front of the pilot.", page.SearchText);
        Assert.Contains("glass bubble", page.SearchText);
        ManualPageItem credit = Assert.Single(page.Items, i => i.Tag == "photo-credit");
        string text = string.Join(" ", credit.Lines);
        Assert.Contains("Test Photographer", text);
        Assert.Contains("CC BY-SA 4.0", text);
        Assert.Contains("Wikimedia Commons", text);
    }

    // Photos are for cockpits no game has: with good pictures from a game the variant gets no photo page; with a set
    // rated low, or none, it does.
    [Fact]
    public void PhotoPages_OnlyWithoutGoodPicturesFromAGame()
    {
        (string folder, byte[] front, _, _) = WriteCatalog();
        CockpitCatalog catalog = CockpitCatalog.Load(folder);
        CockpitVariant v = catalog.Resolve(catalog.Aircraft[0].Variants[0]);
        string file = Path.Combine(temp, "front.png");
        File.WriteAllBytes(file, front);
        Func<CockpitPhoto, string> local = p => p.Sha256 == Sha(front) ? file : null!;
        Assert.Contains(CockpitManualPages.Build(catalog, v, new ManualPageOptions { Folder = folder, PhotoFile = local }), p => p.Kind == "photo");
        Assert.Contains(CockpitManualPages.Build(catalog, v, new ManualPageOptions { Folder = folder, PhotoFile = local, Pictures = new CockpitPictureSet { LowQuality = true } }), p => p.Kind == "photo");
        Assert.DoesNotContain(CockpitManualPages.Build(catalog, v, new ManualPageOptions { Folder = folder, PhotoFile = local, Pictures = new CockpitPictureSet() }), p => p.Kind == "photo");
        // Not on this PC: no page.
        Assert.DoesNotContain(CockpitManualPages.Build(catalog, v, new ManualPageOptions { Folder = folder, PhotoFile = _ => null! }), p => p.Kind == "photo");
    }

    // One click downloads every photo of the aircraft: checked against its SHA-256, a changed photo from the web
    // archive's copy, a dead one reported; the click is the yes for the aircraft, the photo page follows, the offer stays
    // for the photo still missing. A store on the same folder knows the photos again (kept in <data>).
    [Fact]
    public async Task DownloadPhotos_VerifiesFallsBackToTheArchive_AndShowsThePhotoPage()
    {
        (string folder, byte[] front, byte[] overhead, _) = WriteCatalog();
        CockpitService.FolderOverride = () => folder;
        CockpitService.Reload();
        FakeServer server = new();
        server.Files["https://upload.example/front.png"] = front;
        server.Files["https://upload.example/overhead.png"] = Png(250);
        server.Files["https://web.archive.org/web/2024id_/https://upload.example/overhead.png"] = overhead;
        DeviceImageCache.TestHandler = server;
        string store = Path.Combine(temp, "data", CockpitPhotos.StoreFolderName);
        CockpitsViewModel vm = new() { PicturesRoot = Path.Combine(temp, "no-pictures") };
        vm.PhotoStore = new ReferencePhotoStore(store) { Fetch = (uri, progress, _) => DeviceImageCache.FetchBytesAsync(uri, progress) };
        vm.Reload();
        Assert.True(vm.Show("test-jet"));
        Assert.Equal(PhotoConsentState.Ask, CockpitPhotoConsent.For("test-jet"));
        Assert.True(vm.HasVerifiedPhotos);
        Assert.True(vm.HasPhotoOffer);
        Assert.Contains("No game has pictures of this cockpit.", vm.PhotoOfferText);
        Assert.Contains("JoyPro knows: 3 (", vm.PhotoOfferText);
        Assert.False(vm.AskPhotos);
        Assert.DoesNotContain(vm.Pages, p => p.Page.Kind == "photo");
        Assert.Empty(server.Requests);

        vm.DownloadPhotosCommand.Execute(null);
        await vm.PhotoDownload;

        Assert.False(vm.IsDownloadingPhotos);
        Assert.Equal(PhotoConsentState.Allowed, CockpitPhotoConsent.For("test-jet"));
        ReferencePhotoStore again = new(store);
        string? frontFile = again.FileOf("https://upload.example/front.png");
        Assert.NotNull(frontFile);
        Assert.Equal(front, File.ReadAllBytes(frontFile!));
        Assert.Equal("original", again.Entry("https://upload.example/front.png")!.Origin);
        Assert.Equal(overhead, File.ReadAllBytes(again.FileOf("https://upload.example/overhead.png")!));
        Assert.Equal("archive", again.Entry("https://upload.example/overhead.png")!.Origin);
        Assert.Null(again.FileOf("https://upload.example/pedestal.png"));
        Assert.Contains("Photos downloaded and checked: 2.", vm.PhotoDownloadStatus);
        Assert.Contains("web archive", vm.PhotoDownloadStatus);
        Assert.Contains("Not downloaded", vm.PhotoDownloadStatus);
        Assert.True(vm.HasPhotoOffer); // the dead one is still missing
        List<ManualPage> photoPages = vm.Pages.Select(p => p.Page).Where(p => p.Kind == "photo").ToList();
        Assert.Equal(2, photoPages.Count);
        Assert.Equal("photo:" + Sha(front)[..12], vm.CurrentPage!.Id);
        Assert.Contains(vm.MainPages, p => p.Page.Kind == "photo");
        await vm.PhotoLoad;
        // The cards (their pictures need Avalonia's renderer: the opt-in render below shows them).
        Assert.Equal(3, vm.Photos.Count(p => p.Verified != null));
        Assert.Contains(vm.Photos, p => p.Credit.Contains("CC BY-SA 4.0") && p.Credit.Contains("Test Photographer"));
    }

    [Fact]
    public async Task DownloadPhotos_Cancelled_KeepsWhatItHas()
    {
        (string folder, byte[] front, _, _) = WriteCatalog();
        CockpitService.FolderOverride = () => folder;
        CockpitService.Reload();
        CockpitsViewModel vm = new() { PicturesRoot = Path.Combine(temp, "no-pictures") };
        TaskCompletionSource gate = new();
        int calls = 0;
        vm.PhotoStore = new ReferencePhotoStore(Path.Combine(temp, "store"))
        {
            Fetch = async (uri, _, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    return (front, string.Empty);
                await gate.Task.WaitAsync(token);
                return (null, "never");
            }
        };
        vm.Reload();
        Assert.True(vm.Show("test-jet"));
        vm.DownloadPhotosCommand.Execute(null);
        while (calls < 2)
            await Task.Delay(10);
        vm.CancelPhotoDownloadCommand.Execute(null);
        await vm.PhotoDownload;
        Assert.Contains("Cancelled", vm.PhotoDownloadStatus);
        Assert.NotNull(vm.PhotoStore.FileOf("https://upload.example/front.png"));
        Assert.True(vm.HasPhotoOffer);
    }

    // Opt-in: the Cockpits tab with downloaded photos of the shipped database, at 1280 and large, light and dark.
    // JOYPRO_COCKPIT_PHOTO_RENDER=<out folder>, JOYPRO_COCKPIT_PHOTO_ARCHIVE=<the maintainer's archive with
    // sha256\<hash>.<ext>> (the downloads come from there, nothing goes to the internet),
    // JOYPRO_COCKPIT_PHOTO_AIRCRAFT=a350,uh-60:uh-60m,...
    [Fact]
    public void OptIn_RendersTheCockpitsTabWithPhotos()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_COCKPIT_PHOTO_RENDER");
        string? archive = Environment.GetEnvironmentVariable("JOYPRO_COCKPIT_PHOTO_ARCHIVE");
        if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(archive))
            return;
        Directory.CreateDirectory(outDir);
        CockpitService.FolderOverride = () => CockpitDatabaseTests.ShippedCockpits;
        CockpitService.Reload();
        Dictionary<string, string> files = CockpitService.Catalog.Aircraft.SelectMany(a => a.Photos).Where(p => p.Verified)
            .GroupBy(p => p.Url).ToDictionary(g => g.Key, g => Directory.GetFiles(Path.Combine(archive, "sha256"), g.First().Sha256 + ".*").FirstOrDefault() ?? string.Empty);
        string picks = Environment.GetEnvironmentVariable("JOYPRO_COCKPIT_PHOTO_AIRCRAFT") ?? "a350,uh-60:uh-60m,apollo:apollo-cm,f-22";
        AvaloniaRenderThread.Run(() =>
        {
            CockpitsViewModel vm = new() { PicturesRoot = Path.Combine(temp, "no-pictures") };
            vm.PhotoStore = new ReferencePhotoStore(Path.Combine(temp, "render-store"))
            {
                Fetch = (uri, _, _) => Task.FromResult<(byte[]?, string)>(files.TryGetValue(uri.ToString(), out string? f) && File.Exists(f) ? (File.ReadAllBytes(f), string.Empty) : (null, "not in the archive"))
            };
            vm.Reload();
            foreach (string pick in picks.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = pick.Split(':');
                Assert.True(vm.Show(parts[0], parts.Length > 1 ? parts[1] : null), pick);
                string name = CockpitCatalog.Slug(pick.Replace(':', '-'));
                Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                Render(new CockpitsView { DataContext = vm }, 1280, 900, Path.Combine(outDir, name + "-offer-1280-light.png"), false);
                vm.DownloadPhotosCommand.Execute(null);
                AvaloniaRenderThread.Wait(vm.PhotoDownload);
                AvaloniaRenderThread.Wait(vm.PhotoLoad);
                ManualPage? first = vm.Pages.Select(p => p.Page).FirstOrDefault(p => p.Kind == "photo");
                Assert.NotNull(first);
                vm.OpenPage(first!.Id);
                foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    vm.OpenPage("overview");
                    vm.OpenPage(first.Id);
                    Render(new CockpitsView { DataContext = vm }, 1280, 2100, Path.Combine(outDir, name + "-1280-" + theme + ".png"), theme == "dark");
                    Render(new CockpitsView { DataContext = vm }, 2200, 1700, Path.Combine(outDir, name + "-large-" + theme + ".png"), theme == "dark");
                }
            }
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        });
    }

    // JOYPRO_COCKPIT_RENDER_OUT=<out folder>, JOYPRO_COCKPIT_PICTURES=<a pictures root with the game's sets>,
    // JOYPRO_COCKPIT_RENDER_AIRCRAFT=sc-anvil:sc-carrack,...: the Cockpits tab of craft only a game has, with the render
    // from the game where the photo would be (nothing is downloaded).
    [Fact]
    public void OptIn_RendersTheGameRenderInThePhotos()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_COCKPIT_RENDER_OUT");
        string? root = Environment.GetEnvironmentVariable("JOYPRO_COCKPIT_PICTURES");
        if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(root))
            return;
        Directory.CreateDirectory(outDir);
        CockpitService.FolderOverride = () => CockpitDatabaseTests.ShippedCockpits;
        CockpitService.Reload();
        string picks = Environment.GetEnvironmentVariable("JOYPRO_COCKPIT_RENDER_AIRCRAFT") ??
                       "sc-anvil:sc-carrack,sc-cnou:sc-nomad,sc-banu:sc-defender,sc-crusader:sc-c2-hercules,sc-crusader:sc-mercury-star-runner,sc-aegis:sc-gladius";
        AvaloniaRenderThread.Run(() =>
        {
            CockpitsViewModel vm = new() { PicturesRoot = root };
            vm.PhotoStore = new ReferencePhotoStore(Path.Combine(temp, "render-store")) { Fetch = (_, _, _) => Task.FromResult<(byte[]?, string)>((null, "offline")) };
            vm.Reload();
            foreach (string pick in picks.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = pick.Split(':');
                Assert.True(vm.Show(parts[0], parts.Length > 1 ? parts[1] : null), pick);
                AvaloniaRenderThread.Wait(vm.PhotoLoad);
                File.AppendAllText(Path.Combine(outDir, "renders.txt"), pick + ": " + (vm.HasRenderPhoto ? vm.Photos[0].Caption : "no render") + Environment.NewLine);
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                Render(new CockpitsView { DataContext = vm }, 1280, 2400, Path.Combine(outDir, CockpitCatalog.Slug(pick.Replace(':', '-')) + "-dark.png"), true);
            }
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        });
    }

    private static void Render(Control control, int width, int height, string file, bool dark)
    {
        Size size = new(width, height);
        Border page = new() { Background = new SolidColorBrush(dark ? Color.Parse("#202020") : Color.Parse("#F3F3F3")), Child = control };
        Window host = new() { Content = page, Width = width, Height = height };
        for (int pass = 0; pass < 6; pass++)
        {
            host.Measure(size);
            host.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
            // The view stacks the guide above the page while it is arranged (CockpitsView.ArrangeOverride); a window that
            // is not shown has no layout manager to measure the grid again, so every control is measured again here.
            if (pass == 2)
            {
                foreach (Avalonia.Layout.Layoutable l in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(page).OfType<Avalonia.Layout.Layoutable>())
                    l.InvalidateMeasure();
                page.InvalidateMeasure();
                host.InvalidateMeasure();
            }
        }
        using RenderTargetBitmap bitmap = new(new PixelSize(width, height));
        bitmap.Render(page);
        bitmap.Save(file);
        page.Child = null;
        host.Content = null;
    }
}
