using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.DeviceReference;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// A picture pack release as tools/build_cockpit_packs.ps1 makes it, in a local folder: one zip per set (the set's files
// as they are, PNG) and manifest.json. For the pack tests and the journey; nothing is published.
internal static class FakePictureFeed
{
    // Packs the sets <source>\<game>\<set> into <feed>; version, picturesBuilt and a wrong checksum per pack on request,
    // the release tag and the further feeds ("feeds") it names.
    public static string Create(string feed, string source, IEnumerable<(string Game, string Set, int Version)> sets, string? picturesBuilt = null, bool badHash = false,
        string tag = CockpitPicturePacks.ReleaseTag, string[]? feeds = null)
    {
        Directory.CreateDirectory(feed);
        List<object> packs = [];
        foreach ((string game, string set, int version) in sets)
        {
            string folder = Path.Combine(source, game, set);
            string asset = game + "-" + set + "-v" + version + ".zip";
            string zip = Path.Combine(feed, asset);
            if (File.Exists(zip))
                File.Delete(zip);
            ZipFile.CreateFromDirectory(folder, zip);
            string sha;
            using (FileStream stream = File.OpenRead(zip))
                sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            CockpitPictureSet loaded = CockpitPictureSet.Load(folder)!;
            packs.Add(new
            {
                id = game + "/" + set,
                game,
                set,
                planes = loaded.Planes,
                asset,
                size = new FileInfo(zip).Length,
                sha256 = badHash ? new string('0', 64) : sha,
                version,
                built = "2026-10-17T12:00:00Z",
                picturesBuilt = picturesBuilt ?? loaded.Built
            });
        }
        File.WriteAllText(Path.Combine(feed, CockpitPicturePacks.ManifestName), JsonSerializer.Serialize(new { format = CockpitPicturePacks.FormatName, packVersion = 1, tag, feeds = feeds ?? [], packs }));
        return feed;
    }

    // A copy of a set under another game and name (a second pack from the one synthetic set).
    public static void CopySet(string source, string fromGame, string fromSet, string game, string set)
    {
        string from = Path.Combine(source, fromGame, fromSet), to = Path.Combine(source, game, set);
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
    }
}

// The picture packs of JoyPro's releases (docs/cockpits.md "Picture packs"): the manifest, the checks, where a pack goes,
// the index, the version rule, the setting, the Cockpits page against a local feed, and the build tool.
[Collection("CockpitPicturePacks")]
public sealed class CockpitPicturePackTests
{
    private static string Temp(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static CockpitVariant F16()
    {
        CockpitCatalog catalog = CockpitCatalog.Load(CockpitDatabaseTests.ShippedCockpits);
        return catalog.Resolve(catalog.Variant("f-16c-block-50")!);
    }

    private const string GoodSha = "6bb7a326a928b80ccac848cd5beaf9d8884e21318f406e426f1fd68f9256b6a1";

    [Fact]
    public void Manifest_IsRead_AndUnsafePacksAreLeftOut()
    {
        string json = "{\"format\":\"joypro-cockpit-packs\",\"packVersion\":1,\"tag\":\"cockpit-pictures-v1\",\"packs\":[" +
                      "{\"id\":\"DCS/F-16C_50\",\"game\":\"DCS\",\"set\":\"F-16C_50\",\"planes\":[\"F-16C_50\",\"F-16C\"],\"asset\":\"DCS-F-16C_50.zip\",\"size\":2097152,\"sha256\":\"" + GoodSha + "\",\"version\":3,\"built\":\"2026-10-17T12:00:00Z\",\"picturesBuilt\":\"2026-10-17T10:00:00Z\"}," +
                      "{\"game\":\"DCS\",\"set\":\"..\",\"asset\":\"x.zip\",\"sha256\":\"" + GoodSha + "\"}," +
                      "{\"game\":\"DCS\",\"set\":\"A-10C\",\"asset\":\"bad name.zip\",\"sha256\":\"" + GoodSha + "\"}," +
                      "{\"game\":\"DCS\",\"set\":\"A-10C\",\"asset\":\"a.zip\",\"sha256\":\"short\"}]}";
        CockpitPicturePackManifest manifest = CockpitPicturePacks.ParseManifest(json);
        Assert.Equal("cockpit-pictures-v1", manifest.Tag);
        CockpitPicturePack pack = Assert.Single(manifest.Packs);
        Assert.Equal("DCS/F-16C_50", pack.Id);
        Assert.Equal(3, pack.Version);
        Assert.Equal(2.0, pack.SizeMb, 3);
        Assert.Equal(["F-16C_50", "F-16C"], pack.Planes);
        // The variant finds its pack by the game's aircraft name, like the sets on this PC.
        Assert.Same(pack, manifest.For(F16()));
        Assert.Throws<CockpitPicturePackException>(() => CockpitPicturePacks.ParseManifest("{\"format\":\"something-else\",\"packs\":[]}"));
        Assert.Throws<CockpitPicturePackException>(() => CockpitPicturePacks.ParseManifest("not json"));
        CockpitPicturePackException newer = Assert.Throws<CockpitPicturePackException>(() => CockpitPicturePacks.ParseManifest("{\"format\":\"joypro-cockpit-packs\",\"packVersion\":2,\"packs\":[]}"));
        Assert.Contains("newer JoyPro", newer.Message);
    }

    [Fact]
    public void FeedAddresses_ForAFolderAManifestAndGitHub()
    {
        string folder = Temp("feed-address");
        CockpitPicturePack pack = new("DCS/F-16C_50", "DCS", "F-16C_50", [], "DCS-F-16C_50.zip", 1, GoodSha, 1, "", "");
        CockpitPicturePacks local = new(folder, folder);
        Assert.Equal(Path.Combine(folder, "manifest.json"), local.ManifestUrl);
        Assert.Equal(Path.Combine(folder, "DCS-F-16C_50.zip"), local.AssetUrl(pack));
        CockpitPicturePacks github = new("https://github.com/Holdi601/JoystickProfiler2/releases/download/cockpit-pictures-v1/", folder);
        Assert.Equal("https://github.com/Holdi601/JoystickProfiler2/releases/download/cockpit-pictures-v1/manifest.json", github.ManifestUrl);
        Assert.Equal("https://github.com/Holdi601/JoystickProfiler2/releases/download/cockpit-pictures-v1/DCS-F-16C_50.zip", github.AssetUrl(pack));
        CockpitPicturePacks file = new("http://localhost:1234/feed/manifest.json", folder);
        Assert.Equal("http://localhost:1234/feed/manifest.json", file.ManifestUrl);
        Assert.Equal("http://localhost:1234/feed/DCS-F-16C_50.zip", file.AssetUrl(pack));
        // Without the variable (the tests set one): JoyPro's release.
        string? before = Environment.GetEnvironmentVariable(CockpitPicturePacks.FeedVariable);
        try
        {
            Environment.SetEnvironmentVariable(CockpitPicturePacks.FeedVariable, null);
            Assert.Equal("https://github.com/Holdi601/JoystickProfiler2/releases/download/cockpit-pictures-v1/", CockpitPicturePacks.DefaultFeed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CockpitPicturePacks.FeedVariable, before);
        }
    }

    [Fact]
    public async Task ChecksumMismatch_IsRejected_AndNothingIsInstalled()
    {
        string source = Temp("packs-src");
        CockpitPictureTests.WriteSyntheticSet(source);
        string feed = FakePictureFeed.Create(Temp("packs-feed"), source, [("DCS", "F-16C_50", 1)], badHash: true);
        string root = Temp("packs-root");
        CockpitPicturePacks packs = new(feed, root);
        CockpitPicturePack pack = (await packs.LoadManifestAsync()).Packs.Single();
        CockpitPicturePackException ex = await Assert.ThrowsAsync<CockpitPicturePackException>(() => packs.DownloadAsync(pack));
        Assert.Contains("checksum", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "DCS", "F-16C_50")));
        Assert.False(Directory.Exists(Path.Combine(root, ".downloads")) && Directory.EnumerateFileSystemEntries(Path.Combine(root, ".downloads")).Any());
        Assert.Equal(CockpitPackState.Missing, packs.StateOf(pack));
    }

    [Fact]
    public async Task Pack_IsUnpackedIntoItsSetFolder_WithPackJson_AndTheIndexKeepsTheLocalSets()
    {
        string source = Temp("packs-src");
        CockpitPictureTests.WriteSyntheticSet(source);
        string feed = FakePictureFeed.Create(Temp("packs-feed"), source, [("DCS", "F-16C_50", 1)]);
        string root = Temp("packs-root");
        // A set built on this PC ("A-10C") and a name of it the pack also has ("F-16C" in this test): both stay.
        string local = Path.Combine(root, "DCS", "A-10C");
        Directory.CreateDirectory(local);
        File.Copy(Path.Combine(source, "DCS", "F-16C_50", "pictures.json"), Path.Combine(local, "pictures.json"));
        File.WriteAllText(Path.Combine(root, "DCS", "index.json"), "{\"format\":\"joypro-cockpit-pictures-index\",\"aircraft\":{\"A-10C\":\"A-10C\",\"F-16C\":\"A-10C\"}}");

        CockpitPicturePacks packs = new(feed, root);
        CockpitPicturePack pack = (await packs.LoadManifestAsync()).Packs.Single();
        List<long> progress = [];
        Assert.True(await packs.DownloadAsync(pack, new SyncProgress(progress.Add)));
        Assert.NotEmpty(progress);
        Assert.Equal(pack.Size, progress.Max());

        string folder = Path.Combine(root, "DCS", "F-16C_50");
        Assert.True(File.Exists(Path.Combine(folder, "pictures.json")));
        Assert.True(File.Exists(Path.Combine(folder, "overview.png")));
        Assert.NotEmpty(Directory.GetFiles(folder, "*.drawing.json"));
        using (JsonDocument info = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "pack.json"))))
        {
            Assert.Equal("joypro-cockpit-pack", info.RootElement.GetProperty("format").GetString());
            Assert.Equal(1, info.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(pack.Sha256, info.RootElement.GetProperty("sha256").GetString());
        }
        Assert.False(Directory.Exists(Path.Combine(root, ".downloads")));
        using (JsonDocument index = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "DCS", "index.json"))))
        {
            JsonElement aircraft = index.RootElement.GetProperty("aircraft");
            Assert.Equal("A-10C", aircraft.GetProperty("A-10C").GetString());
            Assert.Equal("A-10C", aircraft.GetProperty("F-16C").GetString());
            Assert.Equal("F-16C_50", aircraft.GetProperty("F-16C_50").GetString());
        }
        CockpitPictureSet? set = CockpitPictureSet.For(F16(), root);
        Assert.NotNull(set);
        Assert.Equal(folder, set!.Folder);
        Assert.True(set.FromPack);
        Assert.Equal(1, set.PackVersion);
        Assert.Equal(CockpitPackState.Installed, packs.StateOf(pack));
        // Again: nothing to do.
        Assert.False(await packs.DownloadAsync(pack));
    }

    // "feeds": tags as text or {"tag"}, unsafe names and doubles left out; a further tag is the release next to the feed
    // on the web, a folder in or next to it locally (by name or by the tag in its manifest).
    [Fact]
    public void Feeds_AreReadFromTheManifest_AndResolvedNextToTheFeed()
    {
        CockpitPicturePackManifest manifest = CockpitPicturePacks.ParseManifest(
            "{\"format\":\"joypro-cockpit-packs\",\"tag\":\"cockpit-pictures-v1\",\"feeds\":[\"cockpit-pictures-wt-v1\",{\"tag\":\"more-v1\"},\"../evil\",\"a/b\",\"COCKPIT-pictures-wt-v1\",3],\"packs\":[]}");
        Assert.Equal(["cockpit-pictures-wt-v1", "more-v1"], manifest.FeedTags);
        Assert.Empty(CockpitPicturePacks.ParseManifest("{\"format\":\"joypro-cockpit-packs\",\"packs\":[]}").FeedTags);

        const string Download = "https://github.com/Holdi601/JoystickProfiler2/releases/download/";
        Assert.Equal(Download + "cockpit-pictures-wt-v1/", CockpitPicturePacks.ResolveFeed(Download + "cockpit-pictures-v1/", "cockpit-pictures-wt-v1"));
        Assert.Equal(Download + "cockpit-pictures-wt-v1/", CockpitPicturePacks.ResolveFeed(Download + "cockpit-pictures-v1/manifest.json", "cockpit-pictures-wt-v1"));
        Assert.Equal("http://localhost:1234/feeds/x-part2/", CockpitPicturePacks.ResolveFeed("http://localhost:1234/feeds/x", "x-part2"));
        Assert.Null(CockpitPicturePacks.ResolveFeed(Download + "cockpit-pictures-v1/", "../../evil"));

        string releases = Temp("packs-resolve");
        string main = Path.Combine(releases, "cockpit-packs"), wt = Path.Combine(releases, "cockpit-packs-wt"), part = Path.Combine(wt, "wt-v1-part2"), named = Path.Combine(releases, "named-v1");
        foreach ((string folder, string tag) in new[] { (main, "cockpit-pictures-v1"), (wt, "wt-v1"), (part, "wt-v1-part2"), (named, "something") })
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "manifest.json"), "{\"format\":\"joypro-cockpit-packs\",\"tag\":\"" + tag + "\",\"packs\":[]}");
        }
        Assert.Equal(wt, CockpitPicturePacks.ResolveFeed(main, "wt-v1"));                                     // by the tag in its manifest
        Assert.Equal(wt, CockpitPicturePacks.ResolveFeed(new Uri(Path.Combine(main, "manifest.json")).AbsoluteUri, "wt-v1"));
        Assert.Equal(part, CockpitPicturePacks.ResolveFeed(wt, "wt-v1-part2"));                               // in it
        Assert.Equal(named, CockpitPicturePacks.ResolveFeed(main, "named-v1"));                               // next to it, by name
        Assert.Null(CockpitPicturePacks.ResolveFeed(main, "missing-v1"));
    }

    // Several feeds: the main manifest names further tags; their packs are listed with the main ones (the first feed's
    // pack wins), download from their own release, the further feeds' own "feeds" are read too, a loop is read once, a
    // missing feed is left out and named; local folders as the build tool makes them and a localhost web feed.
    [Fact]
    public async Task Feeds_PacksOfFurtherTagsAreListed_AndDownloadFromTheirOwnRelease()
    {
        string source = Temp("packs-src");
        CockpitPictureTests.WriteSyntheticSet(source);
        FakePictureFeed.CopySet(source, "DCS", "F-16C_50", "WarThunder", "f_16c");
        FakePictureFeed.CopySet(source, "DCS", "F-16C_50", "WarThunder", "f_16a");
        void Release(string folder, string tag, (string, string, int)[] sets, string[] feeds) => FakePictureFeed.Create(folder, source, sets, tag: tag, feeds: feeds);
        void Releases(string main, string wt, string part)
        {
            Release(main, "cockpit-pictures-v1", [("DCS", "F-16C_50", 1)], ["cockpit-pictures-wt-v1", "cockpit-pictures-missing-v1"]);
            Release(wt, "cockpit-pictures-wt-v1", [("WarThunder", "f_16c", 1), ("DCS", "F-16C_50", 7)], ["cockpit-pictures-wt-v1-part2", "cockpit-pictures-v1"]);
            Release(part, "cockpit-pictures-wt-v1-part2", [("WarThunder", "f_16a", 2)], []);
        }

        // Local: <release folder>\cockpit-packs and cockpit-packs-wt with its part in it.
        string local = Temp("packs-releases");
        string localWt = Path.Combine(local, "cockpit-packs-wt"), localPart = Path.Combine(localWt, "cockpit-pictures-wt-v1-part2");
        Releases(Path.Combine(local, "cockpit-packs"), localWt, localPart);
        string root = Temp("packs-root");
        CockpitPicturePacks packs = new(Path.Combine(local, "cockpit-packs"), root);
        CockpitPicturePackManifest manifest = await packs.LoadManifestAsync();
        Assert.Equal(["DCS/F-16C_50", "WarThunder/f_16c", "WarThunder/f_16a"], manifest.Packs.Select(p => p.Id));
        Assert.Equal(1, manifest.Find("DCS", "F-16C_50")!.Version);
        Assert.Equal(["cockpit-pictures-missing-v1"], manifest.FailedFeeds);
        CockpitPicturePack f16c = manifest.Find("WarThunder", "f_16c")!, f16a = manifest.Find("WarThunder", "f_16a")!;
        Assert.Equal(Path.Combine(localWt, "WarThunder-f_16c-v1.zip"), packs.AssetUrl(f16c));
        Assert.Equal(Path.Combine(localPart, "WarThunder-f_16a-v2.zip"), packs.AssetUrl(f16a));
        Assert.Equal(Path.Combine(local, "cockpit-packs", "DCS-F-16C_50-v1.zip"), packs.AssetUrl(manifest.Find("DCS", "F-16C_50")!));
        Assert.True(await packs.DownloadAsync(f16a));
        Assert.Equal(CockpitPackState.Installed, packs.StateOf(f16a));
        Assert.True(File.Exists(Path.Combine(root, "WarThunder", "f_16a", CockpitPictureSet.PackFileName)));

        // The web: the releases next to each other (.../download/<tag>/).
        string web = Temp("packs-web");
        Releases(Path.Combine(web, "cockpit-pictures-v1"), Path.Combine(web, "cockpit-pictures-wt-v1"), Path.Combine(web, "cockpit-pictures-wt-v1-part2"));
        using LocalReleaseServer server = new(web);
        string webRoot = Temp("packs-root");
        CockpitPicturePacks http = new(server.BaseUrl + "cockpit-pictures-v1/", webRoot);
        CockpitPicturePackManifest online = await http.LoadManifestAsync();
        Assert.Equal(manifest.Packs.Select(p => p.Id), online.Packs.Select(p => p.Id));
        Assert.Equal(["cockpit-pictures-missing-v1"], online.FailedFeeds);
        CockpitPicturePack webPack = online.Find("WarThunder", "f_16c")!;
        Assert.Equal(server.BaseUrl + "cockpit-pictures-wt-v1/WarThunder-f_16c-v1.zip", http.AssetUrl(webPack));
        Assert.Equal(server.BaseUrl + "cockpit-pictures-wt-v1-part2/WarThunder-f_16a-v2.zip", http.AssetUrl(online.Find("WarThunder", "f_16a")!));
        Assert.True(await http.DownloadAsync(webPack));
        Assert.Equal(CockpitPackState.Installed, http.StateOf(webPack));

        // The page lists the games of all feeds and says that a part is missing.
        CockpitsViewModel vm = new() { PicturesRoot = Temp("packs-root"), PictureFeed = server.BaseUrl + "cockpit-pictures-v1/", PackMode = () => CockpitPicturePacks.Ask };
        await vm.LoadPackGamesAsync();
        Assert.Equal(["DCS", "WarThunder"], vm.PackGames.Select(g => g.Game).Order(StringComparer.Ordinal));
        Assert.Equal(2, vm.PackGames.Single(g => g.Game == "WarThunder").Count);
        Assert.Contains("could not be read", vm.PackListStatus);
        // The list with the missing part is used until it is read again (two minutes later).
        int requests = server.Requests;
        Assert.Same(await vm.GetPackManifestAsync(quiet: true), await vm.GetPackManifestAsync(quiet: true));
        Assert.Equal(requests, server.Requests);
    }

    [Fact]
    public void Extract_RefusesFilesOutsideTheFolderAndOtherTypes()
    {
        string dir = Temp("packs-evil");
        foreach (string name in new[] { "../evil.json", "sub/pictures.json", "run.exe" })
        {
            string zip = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".zip");
            using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using StreamWriter w = new(archive.CreateEntry(name).Open());
                w.Write("{}");
            }
            string target = Path.Combine(dir, "out-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(target);
            Assert.Throws<CockpitPicturePackException>(() => CockpitPicturePacks.Extract(zip, target));
            Assert.Empty(Directory.GetFiles(target));
            Assert.False(File.Exists(Path.Combine(dir, "evil.json")));
        }
    }

    [Fact]
    public async Task Version_ANewerPackReplacesTheInstalledOne_ANewerLocalSetIsKeptUnlessAsked()
    {
        string source = Temp("packs-src");
        CockpitPictureTests.WriteSyntheticSet(source);
        string root = Temp("packs-root");
        string feed1 = FakePictureFeed.Create(Temp("packs-feed1"), source, [("DCS", "F-16C_50", 1)]);
        string feed2 = FakePictureFeed.Create(Temp("packs-feed2"), source, [("DCS", "F-16C_50", 2)]);
        CockpitPicturePack v1 = (await new CockpitPicturePacks(feed1, root).LoadManifestAsync()).Packs.Single();
        CockpitPicturePacks packs2 = new(feed2, root);
        CockpitPicturePack v2 = (await packs2.LoadManifestAsync()).Packs.Single();
        Assert.True(await new CockpitPicturePacks(feed1, root).DownloadAsync(v1));
        Assert.Equal(CockpitPackState.UpdateAvailable, packs2.StateOf(v2));
        Assert.True(await packs2.DownloadAsync(v2));
        Assert.Equal(2, CockpitPictureSet.Load(Path.Combine(root, "DCS", "F-16C_50"))!.PackVersion);
        Assert.Equal(CockpitPackState.Installed, packs2.StateOf(v2));

        // A set built on this PC: newer than the pack's pictures -> kept; older -> replaced.
        string localRoot = Temp("packs-local");
        CockpitPictureTests.WriteSyntheticSet(localRoot);
        string marker = Path.Combine(localRoot, "DCS", "F-16C_50", "mine.txt");
        File.WriteAllText(marker, "built here");
        string oldFeed = FakePictureFeed.Create(Temp("packs-feed-old"), source, [("DCS", "F-16C_50", 1)], picturesBuilt: "2020-01-01T00:00:00Z");
        CockpitPicturePacks olderPack = new(oldFeed, localRoot);
        CockpitPicturePack old = (await olderPack.LoadManifestAsync()).Packs.Single();
        Assert.Equal(CockpitPackState.LocalNewer, olderPack.StateOf(old));
        Assert.False(CockpitPicturePacks.Wanted(CockpitPackState.LocalNewer, force: false));
        Assert.False(await olderPack.DownloadAsync(old));
        Assert.True(File.Exists(marker));
        Assert.True(await olderPack.DownloadAsync(old, force: true));
        Assert.False(File.Exists(marker));
        Assert.True(CockpitPictureSet.Load(Path.Combine(localRoot, "DCS", "F-16C_50"))!.FromPack);

        string newFeed = FakePictureFeed.Create(Temp("packs-feed-new"), source, [("DCS", "F-16C_50", 1)], picturesBuilt: "2099-01-01T00:00:00Z");
        string localRoot2 = Temp("packs-local2");
        CockpitPictureTests.WriteSyntheticSet(localRoot2);
        CockpitPicturePacks newerPack = new(newFeed, localRoot2);
        CockpitPicturePack fresh = (await newerPack.LoadManifestAsync()).Packs.Single();
        Assert.Equal(CockpitPackState.LocalOlder, newerPack.StateOf(fresh));
        Assert.True(await newerPack.DownloadAsync(fresh));

        // A build on this PC after the pack writes a new pictures.json: the set counts as built here again.
        string rebuilt = Path.Combine(localRoot2, "DCS", "F-16C_50", "pictures.json");
        File.WriteAllText(rebuilt, File.ReadAllText(rebuilt).Replace("\"built\":\"", "\"built\":\"2100"));
        Assert.False(CockpitPictureSet.Load(Path.GetDirectoryName(rebuilt)!)!.FromPack);
    }

    [Fact]
    public void Setting_AskAlwaysNever()
    {
        // ask / always / never (Settings > Privacy), kept in <data>\DeviceReference\settings.json (a test data folder).
        string before = CockpitPicturePacks.DownloadMode;
        try
        {
            Assert.Equal(CockpitPicturePacks.Ask, before);
            CockpitPicturePacks.DownloadMode = "Always";
            Assert.Equal(CockpitPicturePacks.Always, CockpitPicturePacks.DownloadMode);
            CockpitPicturePacks.DownloadMode = "nonsense";
            Assert.Equal(CockpitPicturePacks.Ask, CockpitPicturePacks.DownloadMode);
            MainWindowViewModel vm = new();
            vm.CockpitPictureDownloadMode = "Never";
            Assert.Equal(CockpitPicturePacks.Never, CockpitPicturePacks.DownloadMode);
            Assert.Equal("Never", vm.CockpitPictureDownloadMode);
        }
        finally
        {
            CockpitPicturePacks.DownloadMode = before;
        }
        Assert.Equal(CockpitPicturePacks.Ask, CockpitPicturePacks.OfferFor(CockpitPicturePacks.Ask, hasPack: true, hasPictures: false, dismissed: false));
        Assert.Equal(CockpitPicturePacks.Never, CockpitPicturePacks.OfferFor(CockpitPicturePacks.Ask, hasPack: true, hasPictures: false, dismissed: true));
        Assert.Equal(CockpitPicturePacks.Always, CockpitPicturePacks.OfferFor(CockpitPicturePacks.Always, hasPack: true, hasPictures: false, dismissed: true));
        Assert.Equal(CockpitPicturePacks.Never, CockpitPicturePacks.OfferFor(CockpitPicturePacks.Never, hasPack: true, hasPictures: false, dismissed: false));
        Assert.Equal(CockpitPicturePacks.Never, CockpitPicturePacks.OfferFor(CockpitPicturePacks.Always, hasPack: true, hasPictures: true, dismissed: false));
        Assert.Equal(CockpitPicturePacks.Never, CockpitPicturePacks.OfferFor(CockpitPicturePacks.Ask, hasPack: false, hasPictures: false, dismissed: false));
    }

    // The Cockpits page against a local HTTP feed: ask offers "Download pictures for ...?", the download shows the
    // pictures; never reads nothing; always downloads at once; the manifest is read once per session.
    [Fact]
    public async Task Page_OffersThePack_DownloadsIt_AndHonoursTheSetting()
    {
        string source = Temp("packs-src");
        CockpitPictureTests.WriteSyntheticSet(source);
        string feed = FakePictureFeed.Create(Temp("packs-feed"), source, [("DCS", "F-16C_50", 1)]);
        CockpitService.FolderOverride = () => CockpitDatabaseTests.ShippedCockpits;
        CockpitService.Reload();
        using LocalReleaseServer server = new(feed);
        try
        {
            // Never: no request at all.
            CockpitsViewModel never = new() { PicturesRoot = Temp("packs-root"), PictureFeed = server.BaseUrl, PackMode = () => CockpitPicturePacks.Never };
            never.Reload();
            Assert.True(never.Show("f-16", "f-16c-block-50"));
            await never.PackCheck;
            Assert.False(never.HasPackOffer);
            Assert.Equal(0, server.Requests);

            // Ask: the offer, then the download.
            string root = Temp("packs-root");
            CockpitsViewModel vm = new() { PicturesRoot = root, PictureFeed = server.BaseUrl, PackMode = () => CockpitPicturePacks.Ask };
            vm.Reload();
            Assert.True(vm.Show("f-16", "f-16c-block-50"));
            await vm.PackCheck;
            Assert.True(vm.HasPackOffer);
            Assert.Contains("Download pictures for", vm.PackOfferText);
            Assert.Contains("MB", vm.PackOfferText);
            Assert.False(vm.HasGamePictures);
            await vm.DownloadOfferedPacksCommand.ExecuteAsync(null);
            Assert.False(vm.HasPackOffer);
            Assert.True(vm.HasGamePictures, vm.PackStatus);
            Assert.Contains("Downloaded the pictures for 1 aircraft", vm.PackStatus);
            Assert.Contains("JoyPro", vm.GamePicturesText);
            Assert.Contains("1 downloaded as picture packs", vm.PicturesStatus);
            Assert.Contains(vm.PanelPages, p => p.Page.Id.StartsWith("game:panel:", StringComparison.Ordinal));
            Assert.Contains("picture pack version 1", vm.Pages[0].Page.SearchText);
            int afterFirst = server.Requests;
            // Another aircraft and back: the list is not read again.
            Assert.True(vm.Show("a-10", null));
            await vm.PackCheck;
            vm.Show("f-16", "f-16c-block-50");
            await vm.PackCheck;
            Assert.False(vm.HasPackOffer);
            Assert.Equal(afterFirst, server.Requests);

            // "Not now": not offered again in this session.
            CockpitsViewModel later = new() { PicturesRoot = Temp("packs-root"), PictureFeed = server.BaseUrl, PackMode = () => CockpitPicturePacks.Ask };
            later.Reload();
            later.Show("f-16", "f-16c-block-50");
            await later.PackCheck;
            Assert.True(later.HasPackOffer);
            later.DismissPackOfferCommand.Execute(null);
            later.Show("a-10", null);
            await later.PackCheck;
            later.Show("f-16", "f-16c-block-50");
            await later.PackCheck;
            Assert.False(later.HasPackOffer);

            // Always: downloaded without asking.
            CockpitsViewModel always = new() { PicturesRoot = Temp("packs-root"), PictureFeed = feed, PackMode = () => CockpitPicturePacks.Always };
            always.Reload();
            always.Show("f-16", "f-16c-block-50");
            await always.PackCheck;
            Assert.True(always.HasGamePictures, always.PackStatus);

            // All my aircraft and one game: asked first with the total, then downloaded.
            CockpitsViewModel all = new() { PicturesRoot = Temp("packs-root"), PictureFeed = feed, PackMode = () => CockpitPicturePacks.Never, IsGameInstalled = g => g == "DCS" };
            all.Reload();
            all.PacksExpanded = true;
            await all.PackGamesLoad;
            CockpitPackGameItem dcs = Assert.Single(all.PackGames);
            Assert.Equal("DCS World", dcs.Name);
            Assert.Contains("1 cockpit", dcs.Summary);
            await all.DownloadMyAircraftPacksCommand.ExecuteAsync(null);
            Assert.True(all.HasPackOffer);
            Assert.Contains("1 aircraft", all.PackOfferText);
            await all.DownloadOfferedPacksCommand.ExecuteAsync(null);
            Assert.True(File.Exists(Path.Combine(all.PicturesRoot, "DCS", "F-16C_50", "pack.json")));
            await all.DownloadGamePacksCommand.ExecuteAsync("DCS");
            Assert.False(all.HasPackOffer);
            Assert.Contains("already on this PC", all.PackStatus);
            CockpitsViewModel noDcs = new() { PicturesRoot = Temp("packs-root"), PictureFeed = feed, IsGameInstalled = _ => false };
            Assert.Empty(noDcs.PacksForMyAircraft((await noDcs.GetPackManifestAsync(quiet: true))!));
        }
        finally
        {
            CockpitService.FolderOverride = () => null;
            CockpitService.Reload();
        }
    }

    [Fact]
    public async Task Page_ShowsTheBytes_CancelStopsTheDownload_AndAChecksumFailureIsSaid()
    {
        string source = Temp("packs-src");
        CockpitPictureTests.WriteSyntheticSet(source);
        string feed = FakePictureFeed.Create(Temp("packs-feed"), source, [("DCS", "F-16C_50", 1)]);
        string bad = FakePictureFeed.Create(Temp("packs-feed-bad"), source, [("DCS", "F-16C_50", 1)], badHash: true);
        CockpitService.FolderOverride = () => CockpitDatabaseTests.ShippedCockpits;
        CockpitService.Reload();
        try
        {
            StallingTransport stalling = new();
            CockpitsViewModel vm = new() { PicturesRoot = Temp("packs-root"), PictureFeed = feed, PackTransport = stalling, PackMode = () => CockpitPicturePacks.Ask };
            vm.Reload();
            vm.Show("f-16", "f-16c-block-50");
            await vm.PackCheck;
            Assert.True(vm.HasPackOffer);
            Task download = vm.DownloadOfferedPacksCommand.ExecuteAsync(null);
            await stalling.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(vm.IsDownloadingPacks);
            Assert.Contains(" of ", vm.PackProgressText);
            Assert.Contains("MB", vm.PackProgressText);
            Assert.True(vm.PackProgress > 0);
            vm.CancelPackDownloadCommand.Execute(null);
            await download.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(vm.IsDownloadingPacks);
            Assert.Contains("cancelled", vm.PackStatus);
            Assert.False(vm.HasGamePictures);
            Assert.False(Directory.Exists(Path.Combine(vm.PicturesRoot, "DCS", "F-16C_50")));

            CockpitsViewModel broken = new() { PicturesRoot = Temp("packs-root"), PictureFeed = bad, PackMode = () => CockpitPicturePacks.Ask };
            broken.Reload();
            broken.Show("f-16", "f-16c-block-50");
            await broken.PackCheck;
            await broken.DownloadOfferedPacksCommand.ExecuteAsync(null);
            Assert.Contains("checksum", broken.PackStatus);
            Assert.False(broken.HasGamePictures);
        }
        finally
        {
            CockpitService.FolderOverride = () => null;
            CockpitService.Reload();
        }
    }

    // The build tool on a set from the picture writer: JPEG pictures with a mask for the transparent ones, the manifest
    // and CREDITS.txt; installed, the page reads them and the GUI draws the JPEG with its transparency.
    [Fact]
    public async Task BuildTool_MakesJpegPacks_ThatInstallAndShow()
    {
        string source = Temp("packs-tool-src");
        CockpitPictureTests.WriteSyntheticSet(source);
        // War Thunder has a release of its own: the main one leaves it out and names it in "feeds".
        FakePictureFeed.CopySet(source, "DCS", "F-16C_50", "WarThunder", "f_16c");
        string outDir = Temp("packs-tool-out");
        string script = Path.Combine(RepoRoot(), "tools", "build_cockpit_packs.ps1");
        ProcessStartInfo start = new("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Source \"" + source + "\" -Out \"" + outDir + "\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using (Process p = Process.Start(start)!)
        {
            string output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            Assert.True(p.ExitCode == 0, output);
            Assert.Contains("1 packs (1 built, 0 unchanged)", output);
            Assert.Contains("WarThunder: in its own release", output);
        }
        string manifestText = File.ReadAllText(Path.Combine(outDir, "manifest.json"));
        CockpitPicturePackManifest manifest = CockpitPicturePacks.ParseManifest(manifestText);
        Assert.Equal(["cockpit-pictures-wt-v1"], manifest.FeedTags);
        CockpitPicturePack pack = Assert.Single(manifest.Packs);
        Assert.Equal("DCS/F-16C_50", pack.Id);
        Assert.Equal("DCS-F-16C_50.zip", pack.Asset);
        Assert.Contains("F-16C", pack.Planes);
        Assert.Equal(1, pack.Version);
        using (ZipArchive zip = ZipFile.OpenRead(Path.Combine(outDir, pack.Asset)))
        {
            List<string> names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("overview.jpg", names);
            Assert.Contains("overview.mask.png", names);
            Assert.DoesNotContain("overview.png", names);
            Assert.Contains("CREDITS.txt", names);
            using StreamReader credits = new(zip.GetEntry("CREDITS.txt")!.Open());
            Assert.Contains("Pictures rendered from DCS World by Eagle Dynamics with JoyPro; the cockpit designs, textures and names belong to their respective owners.", credits.ReadToEnd());
        }
        // Built again unchanged: same zip, same version.
        string sha = pack.Sha256;
        using (Process again = Process.Start(start)!)
        {
            string output = await again.StandardOutput.ReadToEndAsync();
            await again.WaitForExitAsync();
            Assert.Contains("0 built, 1 unchanged", output);
        }
        Assert.Equal(sha, CockpitPicturePacks.ParseManifest(File.ReadAllText(Path.Combine(outDir, "manifest.json"))).Packs.Single().Sha256);

        string root = Temp("packs-tool-root");
        CockpitPicturePacks packs = new(outDir, root);
        Assert.True(await packs.DownloadAsync((await packs.LoadManifestAsync()).Packs.Single()));
        CockpitPictureSet set = CockpitPictureSet.For(F16(), root)!;
        CockpitPictureView overview = set.View("overview")!;
        Assert.EndsWith(".jpg", overview.Image);
        Assert.True(File.Exists(overview.Mask));
        ManualPageOptions o = new() { Folder = CockpitDatabaseTests.ShippedCockpits, Pictures = set, PictureMode = "picture" };
        CockpitArt art = CockpitManualPages.GameArt(overview, o);
        Assert.Equal(overview.Mask, art.Mask);
        AvaloniaRenderThread.Run(() =>
        {
            using Avalonia.Media.Imaging.Bitmap bitmap = Views.Controls.ManualPageView.LoadPicture(art.Picture, art.Mask);
            using SkiaSharp.SKBitmap mask = SkiaSharp.SKBitmap.Decode(art.Mask);
            Assert.Equal(mask.Width, bitmap.PixelSize.Width);
            Assert.Equal(mask.Height, bitmap.PixelSize.Height);
            // A transparent and an opaque place of the mask are the same in the picture the page draws.
            (int X, int Y)? clear = null, solid = null;
            for (int y = 0; y < mask.Height && (clear == null || solid == null); y += 3)
            {
                for (int x = 0; x < mask.Width; x += 3)
                {
                    byte a = mask.GetPixel(x, y).Red;
                    if (a < 5)
                        clear ??= (x, y);
                    else if (a > 250)
                        solid ??= (x, y);
                }
            }
            Assert.NotNull(clear);
            Assert.NotNull(solid);
            Avalonia.Media.Imaging.WriteableBitmap probe = new(bitmap.PixelSize, new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (Avalonia.Platform.ILockedFramebuffer fb = probe.Lock())
            {
                bitmap.CopyPixels(fb, Avalonia.Platform.AlphaFormat.Premul);
                Assert.True(System.Runtime.InteropServices.Marshal.ReadByte(fb.Address, clear!.Value.Y * fb.RowBytes + clear.Value.X * 4 + 3) < 10);
                Assert.True(System.Runtime.InteropServices.Marshal.ReadByte(fb.Address, solid!.Value.Y * fb.RowBytes + solid.Value.X * 4 + 3) > 245);
            }
            probe.Dispose();
        });
    }

    // Opt-in check of a pack release before it is published (JOYPRO_CHECK_PICTURE_FEED = its main folder, e.g.
    // <release folder>\cockpit-packs): every feed is read, every pack's zip is where its feed is and has its size.
    [Fact]
    public async Task OptIn_LocalRelease_EveryFeedAndEveryAssetIsThere()
    {
        string? feed = Environment.GetEnvironmentVariable("JOYPRO_CHECK_PICTURE_FEED");
        if (string.IsNullOrWhiteSpace(feed))
            return;
        CockpitPicturePacks packs = new(feed, Temp("packs-check-root"));
        CockpitPicturePackManifest manifest = await packs.LoadManifestAsync();
        Assert.Empty(manifest.FailedFeeds);
        Assert.NotEmpty(manifest.Packs);
        foreach (CockpitPicturePack pack in manifest.Packs)
        {
            string file = packs.AssetUrl(pack);
            Assert.True(File.Exists(file), file);
            Assert.Equal(pack.Size, new FileInfo(file).Length);
        }
        Console.WriteLine("Picture feed check: " + manifest.Packs.Count + " packs in " + manifest.Packs.Select(p => p.Feed).Distinct().Count() + " feeds: " +
                          string.Join(", ", manifest.Packs.GroupBy(p => p.Game).Select(g => g.Key + " " + g.Count())));
    }

    private static async Task<string> RunBuildTool(string arguments)
    {
        string script = Path.Combine(RepoRoot(), "tools", "build_cockpit_packs.ps1");
        ProcessStartInfo start = new("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" " + arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using Process p = Process.Start(start)!;
        Task<string> stdout = p.StandardOutput.ReadToEndAsync(), stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        string output = await stdout + await stderr;
        Assert.True(p.ExitCode == 0, output);
        return output;
    }

    // More packs than a release takes (MaxAssets with manifest.json): the rest goes to <Out>\<Tag>-part2, named in the
    // first part's "feeds" after the given ones; -ManifestOnly writes the manifests again from the zips, packs keep their
    // part; the GUI reads both parts.
    [Fact]
    public async Task BuildTool_SplitsABigRelease_IntoParts_ThatTheGuiReads()
    {
        string source = Temp("packs-split-src");
        CockpitPictureTests.WriteSyntheticSet(source);
        FakePictureFeed.CopySet(source, "DCS", "F-16C_50", "DCS", "F-16C_50b");
        string outDir = Temp("packs-split-out");
        string args = "-Source \"" + source + "\" -Out \"" + outDir + "\" -Tag test-pictures-v1 -Feeds other-pictures-v1 -MaxAssets 2";
        string output = await RunBuildTool(args);
        Assert.Contains("2 packs (2 built, 0 unchanged)", output);
        Assert.Contains("Release test-pictures-v1: 2 assets (1 packs + manifest.json)", output);
        Assert.Contains("Release test-pictures-v1-part2: 2 assets (1 packs + manifest.json)", output);
        string partDir = Path.Combine(outDir, "test-pictures-v1-part2");
        CockpitPicturePackManifest first = CockpitPicturePacks.ParseManifest(File.ReadAllText(Path.Combine(outDir, "manifest.json")));
        CockpitPicturePackManifest second = CockpitPicturePacks.ParseManifest(File.ReadAllText(Path.Combine(partDir, "manifest.json")));
        Assert.Equal("test-pictures-v1", first.Tag);
        Assert.Equal(["other-pictures-v1", "test-pictures-v1-part2"], first.FeedTags);
        Assert.Equal("test-pictures-v1-part2", second.Tag);
        Assert.Empty(second.FeedTags);
        CockpitPicturePack a = Assert.Single(first.Packs), b = Assert.Single(second.Packs);
        Assert.True(File.Exists(Path.Combine(outDir, a.Asset)));
        Assert.True(File.Exists(Path.Combine(partDir, b.Asset)));
        Assert.False(File.Exists(Path.Combine(outDir, b.Asset)));

        // The manifests again from the zips: nothing built, every pack in its part, the same checksums.
        output = await RunBuildTool("-Out \"" + outDir + "\" -Tag test-pictures-v1 -Feeds other-pictures-v1 -MaxAssets 2 -ManifestOnly");
        Assert.Contains("2 packs (0 built, 2 unchanged)", output);
        CockpitPicturePack a2 = CockpitPicturePacks.ParseManifest(File.ReadAllText(Path.Combine(outDir, "manifest.json"))).Packs.Single();
        CockpitPicturePack b2 = CockpitPicturePacks.ParseManifest(File.ReadAllText(Path.Combine(partDir, "manifest.json"))).Packs.Single();
        Assert.Equal((a.Id, a.Asset, a.Sha256, a.Version, a.Size), (a2.Id, a2.Asset, a2.Sha256, a2.Version, a2.Size));
        Assert.Equal((b.Id, b.Asset, b.Sha256, b.Version, b.Size), (b2.Id, b2.Asset, b2.Sha256, b2.Version, b2.Size));

        CockpitPicturePacks packs = new(outDir, Temp("packs-split-root"));
        CockpitPicturePackManifest all = await packs.LoadManifestAsync();
        Assert.Equal([a.Id, b.Id], all.Packs.Select(p => p.Id));
        Assert.Equal(["other-pictures-v1"], all.FailedFeeds);
        Assert.Equal(Path.Combine(partDir, b.Asset), packs.AssetUrl(all.Packs[1]));
        Assert.True(await packs.DownloadAsync(all.Packs[1]));
    }

    // -Only: only the named sets are built again (next version), a named set gone from the source is left out; every
    // other pack stays as it is, also one whose set is changed or not in the source any more.
    [Fact]
    public async Task BuildTool_Only_BuildsTheNamedSets_AndKeepsEveryOtherPack()
    {
        string source = Temp("packs-only-src");
        CockpitPictureTests.WriteSyntheticSet(source);
        FakePictureFeed.CopySet(source, "DCS", "F-16C_50", "DCS", "F-16C_50b");
        FakePictureFeed.CopySet(source, "DCS", "F-16C_50", "XPlane12", "f16");
        FakePictureFeed.CopySet(source, "DCS", "F-16C_50", "XPlane12", "f16_ai");
        string outDir = Temp("packs-only-out");
        string args = "-Source \"" + source + "\" -Out \"" + outDir + "\" -Tag test-pictures-v1 -Feeds other-pictures-v1";
        Assert.Contains("4 packs (4 built, 0 unchanged)", await RunBuildTool(args));
        Dictionary<string, CockpitPicturePack> before = CockpitPicturePacks.ParseManifest(File.ReadAllText(Path.Combine(outDir, "manifest.json"))).Packs.ToDictionary(p => p.Id);

        // Every set changed, one set gone from the source; only DCS/F-16C_50 and XPlane12/* are named.
        foreach (string set in new[] { Path.Combine(source, "DCS", "F-16C_50"), Path.Combine(source, "XPlane12", "f16") })
            File.AppendAllText(Path.Combine(set, "pictures.json"), " ");
        Directory.Delete(Path.Combine(source, "DCS", "F-16C_50b"), true);
        Directory.Delete(Path.Combine(source, "XPlane12", "f16_ai"), true);
        string output = await RunBuildTool(args + " -Only \"DCS/F-16C_50,XPlane12/*\"");
        Assert.Contains("3 packs (2 built, 1 unchanged)", output);
        Assert.Contains("XPlane12/f16_ai: not in the source any more, left out.", output);
        Dictionary<string, CockpitPicturePack> after = CockpitPicturePacks.ParseManifest(File.ReadAllText(Path.Combine(outDir, "manifest.json"))).Packs.ToDictionary(p => p.Id);
        Assert.Equal(2, after["DCS/F-16C_50"].Version);
        Assert.Equal(2, after["XPlane12/f16"].Version);
        CockpitPicturePack kept = after["DCS/F-16C_50b"];
        Assert.Equal((1, before["DCS/F-16C_50b"].Sha256, before["DCS/F-16C_50b"].Asset), (kept.Version, kept.Sha256, kept.Asset));
        Assert.True(File.Exists(Path.Combine(outDir, kept.Asset)));
        Assert.False(after.ContainsKey("XPlane12/f16_ai"));
        // Its zip stays on disk, named as not to be uploaded.
        Assert.Contains("Not in the manifest (left over from an earlier build, do not upload)", output);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    // Reads the manifest from disk; a download reports a third of the bytes and then waits until it is cancelled.
    private sealed class StallingTransport : IUpdateTransport
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<(HttpStatusCode Status, string Body)> GetStringAsync(string url, CancellationToken cancellationToken) =>
            DefaultUpdateTransport.Instance.GetStringAsync(url, cancellationToken);

        public async Task DownloadAsync(string url, string targetFile, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            await File.WriteAllTextAsync(targetFile, "partial", cancellationToken);
            progress?.Report(0.33);
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
