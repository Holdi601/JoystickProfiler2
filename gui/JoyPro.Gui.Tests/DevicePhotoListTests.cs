using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.DeviceReference;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The device photo list from JoyPro's releases (DevicePhotoList, docs/device-reference.md): a newer valid list replaces
// the shipped one, an older or equal one, a broken one, one whose checksum differs or one with a disallowed source is
// ignored, offline the shipped list stays, at most one look a day, nothing while the makers' photos are switched off.
// The feed is a local folder (as the picture pack tests do); nothing goes to the internet.
[SupportedOSPlatform("windows")]
[Collection("DeviceImageCache")]
public sealed class DevicePhotoListTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "photo-list-" + Guid.NewGuid().ToString("N"));
    private readonly string? previousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");

    public DevicePhotoListTests()
    {
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(temp, "data"));
        DeviceReferenceService.CatalogFolderOverride = () => DeviceReferenceTests.ShippedCatalog;
        DeviceReferenceService.Reload();
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
    }

    private static string Shipped => DevicePhotoDb.PhotoFolderOf(DeviceReferenceTests.ShippedCatalog);

    // A feed folder: the shipped photo files with the list version set, changed by `edit` (the staged folder), zipped,
    // and its manifest. Returns the feed folder.
    private string Feed(string name, int version, Action<string>? edit = null, Func<string, string>? manifestSha = null, int? sizeOverride = null)
    {
        string stage = Path.Combine(temp, name + "-stage");
        Directory.CreateDirectory(stage);
        foreach (string file in Directory.GetFiles(Shipped, "*.json"))
            File.Copy(file, Path.Combine(stage, Path.GetFileName(file)));
        JsonNode sources = JsonNode.Parse(File.ReadAllText(Path.Combine(stage, "_sources.json")))!;
        sources["version"] = version;
        File.WriteAllText(Path.Combine(stage, "_sources.json"), sources.ToJsonString());
        edit?.Invoke(stage);
        string feed = Path.Combine(temp, name);
        Directory.CreateDirectory(feed);
        string asset = "device-photos-v" + version + ".zip";
        string zip = Path.Combine(feed, asset);
        ZipFile.CreateFromDirectory(stage, zip);
        string sha;
        using (FileStream stream = File.OpenRead(zip))
            sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        File.WriteAllText(Path.Combine(feed, "manifest.json"), JsonSerializer.Serialize(new
        {
            format = "joypro-device-photos", version, tag = "joypro-device-photos", asset, size = sizeOverride ?? new FileInfo(zip).Length,
            sha256 = manifestSha == null ? sha : manifestSha(sha), built = "2026-10-25T00:00:00Z"
        }));
        return feed;
    }

    // The first maker photo of a model with a single maker host, moved to another file name on the same host (a
    // "repaired link").
    private static (string File, string Model, string OldUrl, string NewUrl) RepairOne(string stage)
    {
        string file = Path.Combine(stage, "vkb.json");
        JsonNode root = JsonNode.Parse(File.ReadAllText(file))!;
        JsonNode photo = root["photos"]!.AsArray().First(p => ((string)p!["url"]!).StartsWith("https://vkbsimcontrollers.com/", StringComparison.Ordinal))!;
        string old = (string)photo["url"]!;
        string repaired = old.Replace(".webp", "-moved.webp", StringComparison.Ordinal);
        photo["url"] = repaired;
        File.WriteAllText(file, root.ToJsonString());
        return (file, (string)photo["model"]!, old, repaired);
    }

    private static DeviceCatalog Catalog() => DeviceCatalog.Load(DeviceReferenceTests.ShippedCatalog);

    [Fact]
    public async Task NewerFeed_ReplacesTheShippedList_AndTheCatalogUsesIt()
    {
        string newUrl = string.Empty, model = string.Empty;
        string feed = Feed("v2", 2, stage => { (_, model, _, newUrl) = RepairOne(stage); });
        DevicePhotoList list = new(feed);
        Assert.Null(list.FolderFor(Shipped));

        Assert.Equal(DevicePhotoList.Outcome.Installed, await list.CheckAsync(Catalog(), Shipped, force: true));
        Assert.Equal(list.ListFolder, list.FolderFor(Shipped));
        Assert.Equal(2, list.ReadState().Version);
        DeviceReferenceService.Reload();
        RefDevice device = DeviceReferenceService.Catalog.Find(model)!;
        Assert.Contains(device.Photos, p => p.Url == newUrl);

        // The same version again: nothing to do; a look within the day is not due.
        Assert.Equal(DevicePhotoList.Outcome.UpToDate, await list.CheckAsync(Catalog(), Shipped, force: true));
        Assert.Equal(DevicePhotoList.Outcome.NotDue, await list.CheckAsync(Catalog(), Shipped));
    }

    [Fact]
    public async Task OlderOrEqualFeed_IsIgnored()
    {
        int shipped = DevicePhotoList.VersionOf(Shipped);
        Assert.True(shipped >= 1);
        DevicePhotoList list = new(Feed("same", shipped));
        Assert.Equal(DevicePhotoList.Outcome.UpToDate, await list.CheckAsync(Catalog(), Shipped, force: true));
        Assert.Null(list.FolderFor(Shipped));
        Assert.False(Directory.Exists(list.ListFolder));
    }

    [Fact]
    public async Task BrokenFeeds_AreIgnored()
    {
        // A manifest that is not JSON / not this format.
        string bad = Path.Combine(temp, "bad");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "manifest.json"), "{ not json");
        Assert.Equal(DevicePhotoList.Outcome.Rejected, await new DevicePhotoList(bad).CheckAsync(Catalog(), Shipped, force: true));
        // A zip with something else than JSON files in it.
        string extra = Feed("extra", 5, stage => File.WriteAllText(Path.Combine(stage, "run.exe"), "MZ"));
        DevicePhotoList withExe = new(extra);
        Assert.Equal(DevicePhotoList.Outcome.Rejected, await withExe.CheckAsync(Catalog(), Shipped, force: true));
        Assert.Null(withExe.FolderFor(Shipped));
        // An emptied list (fewer than half the shipped photos) never wipes the pictures.
        string empty = Feed("empty", 6, stage =>
        {
            foreach (string f in Directory.GetFiles(stage, "*.json").Where(f => !Path.GetFileName(f).StartsWith('_')))
                File.WriteAllText(f, "{ \"photos\": [] }");
        });
        Assert.Equal(DevicePhotoList.Outcome.Rejected, await new DevicePhotoList(empty).CheckAsync(Catalog(), Shipped, force: true));
    }

    [Fact]
    public async Task ChecksumMismatch_IsIgnored()
    {
        string feed = Feed("sha", 3, stage => RepairOne(stage), sha => new string('0', 64));
        DevicePhotoList list = new(feed);
        Assert.Equal(DevicePhotoList.Outcome.Rejected, await list.CheckAsync(Catalog(), Shipped, force: true));
        Assert.Null(list.FolderFor(Shipped));
        Assert.Equal(0, list.ReadState().Version);
    }

    [Fact]
    public async Task DisallowedSource_RejectsTheList()
    {
        // A shop address as a maker's photo.
        string shop = Feed("shop", 4, stage =>
        {
            string file = Path.Combine(stage, "vkb.json");
            JsonNode root = JsonNode.Parse(File.ReadAllText(file))!;
            root["photos"]!.AsArray().Add(JsonNode.Parse("{\"model\":\"vkb-gunfighter-mcg-pro\",\"url\":\"https://www.amazon.com/images/I/x.jpg\",\"view\":\"front\"}"));
            File.WriteAllText(file, root.ToJsonString());
        });
        Assert.Equal(DevicePhotoList.Outcome.Rejected, await new DevicePhotoList(shop).CheckAsync(Catalog(), Shipped, force: true));
        // A review photo of a site the list does not name.
        string review = Feed("review", 4, stage =>
        {
            string file = Path.Combine(stage, "vkb.json");
            JsonNode root = JsonNode.Parse(File.ReadAllText(file))!;
            root["photos"]!.AsArray().Add(JsonNode.Parse("{\"model\":\"vkb-gunfighter-mcg-pro\",\"url\":\"https://forum.example.com/x.jpg\",\"view\":\"front\",\"source\":\"review\"}"));
            File.WriteAllText(file, root.ToJsonString());
        });
        Assert.Equal(DevicePhotoList.Outcome.Rejected, await new DevicePhotoList(review).CheckAsync(Catalog(), Shipped, force: true));
        // A whole shared CDN as a maker host.
        string host = Feed("host", 4, stage => File.WriteAllText(Path.Combine(stage, "_hosts.json"), "{\"models\":{\"vkb-gunfighter-mcg-pro\":[\"cdn.shopify.com\"]}}"));
        Assert.Equal(DevicePhotoList.Outcome.Rejected, await new DevicePhotoList(host).CheckAsync(Catalog(), Shipped, force: true));
        Assert.Contains("a shared host", DevicePhotoDb.HostEntryProblem("cdn.shopify.com"));
        Assert.Empty(DevicePhotoDb.HostEntryProblem("cdn.shopify.com/s/files/1/0571/3192/5689/"));
        Assert.Empty(DevicePhotoDb.HostEntryProblem("d7qztf2ityad6.cloudfront.net"));
        Assert.NotEmpty(DevicePhotoDb.HostEntryProblem("web.archive.org"));
    }

    private sealed class OfflineTransport : IUpdateTransport
    {
        public Task<(HttpStatusCode Status, string Body)> GetStringAsync(string url, CancellationToken cancellationToken) =>
            throw new System.Net.Http.HttpRequestException("offline");

        public Task DownloadAsync(string url, string targetFile, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new System.Net.Http.HttpRequestException("offline");
    }

    [Fact]
    public async Task Offline_KeepsTheListItHas()
    {
        // A newer list installed earlier stays in use when the feed cannot be reached.
        DevicePhotoList first = new(Feed("v7", 7, stage => RepairOne(stage)));
        Assert.Equal(DevicePhotoList.Outcome.Installed, await first.CheckAsync(Catalog(), Shipped, force: true));
        DevicePhotoList offline = new("https://example.invalid/feed/", null, new OfflineTransport());
        Assert.Equal(DevicePhotoList.Outcome.Unreachable, await offline.CheckAsync(Catalog(), Shipped, force: true));
        Assert.Equal(offline.ListFolder, offline.FolderFor(Shipped));
        // Without one, the shipped list.
        Directory.Delete(offline.Folder, recursive: true);
        Assert.Equal(DevicePhotoList.Outcome.Unreachable, await offline.CheckAsync(Catalog(), Shipped, force: true));
        Assert.Null(offline.FolderFor(Shipped));
    }

    [Fact]
    public async Task NoLook_WhileTheMakersPhotosAreSwitchedOff()
    {
        string feed = Feed("privacy", 8, stage => RepairOne(stage));
        MainWindowViewModel vm = new(loadPersistedUiSettings: false) { DevicePhotoListFactory = () => new DevicePhotoList(feed) };
        DevicePhotoConsent.Mode = DevicePhotoConsent.Never;
        Assert.Equal(DevicePhotoList.Outcome.Off, await vm.CheckDevicePhotoListAsync(force: true));
        Assert.Null(new DevicePhotoList(feed).FolderFor(Shipped));
        DevicePhotoConsent.Mode = DevicePhotoConsent.Ask;
        // A local feed is checked (the internet stays blocked in the tests).
        Assert.Equal(DevicePhotoList.Outcome.Installed, await vm.CheckDevicePhotoListAsync(force: true));
        vm.StopGameConfigWatcher();
    }
}
