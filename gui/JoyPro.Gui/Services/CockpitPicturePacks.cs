using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Services;

// One picture pack of the manifest: the cockpit pictures of one aircraft set of one game (docs/cockpits.md "Picture
// packs"). PicturesBuilt is when its pictures were rendered (pictures.json "built"), Built when the pack was made.
public sealed record CockpitPicturePack(string Id, string Game, string Set, IReadOnlyList<string> Planes, string Asset, long Size, string Sha256,
    int Version, string Built, string PicturesBuilt)
{
    public double SizeMb => Size / 1048576.0;

    // The feed (folder or address) whose manifest listed the pack: its asset lies next to that manifest (the release of
    // a further feed). Empty: the feed the list was read from.
    public string Feed { get; init; } = string.Empty;

    // The set's pictures were rated "low quality" when they were made (pictures.json "quality"): a pack of another game
    // for the same aircraft is offered first.
    public bool Low { get; init; }
}

// manifest.json of a picture pack release ("joypro-cockpit-packs", tools/build_cockpit_packs.ps1).
public sealed class CockpitPicturePackManifest
{
    public int PackVersion { get; init; }

    public string Tag { get; init; } = string.Empty;

    public IReadOnlyList<CockpitPicturePack> Packs { get; init; } = [];

    // "feeds": the release tags of further manifests with more packs (a GitHub release takes at most 1000 assets).
    public IReadOnlyList<string> FeedTags { get; init; } = [];

    // The further feeds that could not be read: their packs are missing (read the list again later).
    public IReadOnlyList<string> FailedFeeds { get; init; } = [];

    // The pack of a variant: the names of the packs of each game, found like the sets on this PC (CockpitPictureSet).
    public CockpitPicturePack? For(CockpitVariant? variant)
    {
        if (variant == null)
            return null;
        // A good set first; a low-quality one only when no game has a better one.
        Tuple<string, string>? hit = CockpitPictureSet.Locate(variant, IndexOf, (game, set) => Find(game, set) is { Low: false })
            ?? CockpitPictureSet.Locate(variant, IndexOf, (_, _) => true);
        return hit == null ? null : Find(hit.Item1, hit.Item2);
    }

    public CockpitPicturePack? Find(string game, string set) =>
        Packs.FirstOrDefault(p => string.Equals(p.Game, game, StringComparison.OrdinalIgnoreCase) && string.Equals(p.Set, set, StringComparison.OrdinalIgnoreCase));

    private readonly Dictionary<string, Dictionary<string, string>> indexes = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, string> IndexOf(string game)
    {
        lock (indexes)
        {
            if (!indexes.TryGetValue(game, out Dictionary<string, string>? index))
            {
                index = CockpitPictureSet.IndexOf(Packs.Where(p => string.Equals(p.Game, game, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(p => p.Planes.Append(p.Set).Select(name => new KeyValuePair<string, string>(name, p.Set))));
                indexes[game] = index;
            }
            return index;
        }
    }
}

// What a pack is to this PC: not there, installed (current or older), or a set built on this PC (newer or older than
// the pack's pictures).
public enum CockpitPackState
{
    Missing,
    Installed,
    UpdateAvailable,
    LocalNewer,
    LocalOlder
}

// The pack is not what the manifest says (checksum) or not a picture pack: nothing was installed.
public sealed class CockpitPicturePackException(string message) : Exception(message);

// The picture packs of JoyPro's releases (docs/cockpits.md "Picture packs"): the cockpit pictures rendered from the games
// by the maintainer, shipped as release assets (never in git), one zip per aircraft set plus manifest.json. Downloaded on
// the Cockpits page (ask / always / never, Settings > Privacy), checked against their SHA-256, unpacked to a temporary
// folder and moved to <data>\GameData\CockpitPictures\<game>\<set>; the game's index.json gets the pack's aircraft names
// (names of sets built on this PC stay); pack.json in the set folder keeps the pack's version. A newer version in the
// manifest replaces an installed pack; a set built on this PC that is newer than the pack is kept unless asked.
public sealed class CockpitPicturePacks
{
    public const string FormatName = "joypro-cockpit-packs";
    public const int SupportedPackVersion = 1;
    public const string ManifestName = "manifest.json";
    public const string ReleaseTag = "cockpit-pictures-v1";
    // The most further feeds one list reads ("feeds" of the manifests, also of the further ones).
    public const int MaxFeeds = 16;
    // Another feed (a folder, a file:// or http(s) address of a folder or of its manifest.json): tests and checking a
    // pack release before it is published.
    public const string FeedVariable = "JOYPRO_COCKPIT_PICTURE_FEED";

    // Settings > Privacy: whether the Cockpits page offers (ask), downloads by itself (always) or never looks (never).
    public const string Ask = "ask";
    public const string Always = "always";
    public const string Never = "never";

    // The most a pack may unpack to (a broken or hostile zip stops there).
    private const long MaxUnpackedBytes = 3L * 1024 * 1024 * 1024;

    private static readonly Regex SafeName = new("^[A-Za-z0-9][A-Za-z0-9._ ()+-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeAsset = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly string[] PackFileTypes = [".json", ".jpg", ".jpeg", ".png", ".txt"];

    private readonly IUpdateTransport transport;

    public CockpitPicturePacks(string? feed = null, string? root = null, IUpdateTransport? transport = null)
    {
        Feed = string.IsNullOrWhiteSpace(feed) ? DefaultFeed : feed.Trim();
        Root = string.IsNullOrWhiteSpace(root) ? CockpitPictureSet.Root() : root;
        this.transport = transport ?? DefaultUpdateTransport.Instance;
    }

    public static string DefaultFeed
    {
        get
        {
            string? feed = Environment.GetEnvironmentVariable(FeedVariable);
            return !string.IsNullOrWhiteSpace(feed) ? feed.Trim()
                : "https://github.com/" + AppInfo.RepositoryOwner + "/" + AppInfo.RepositoryName + "/releases/download/" + ReleaseTag + "/";
        }
    }

    public string Feed { get; }

    public string Root { get; }

    // The setting (Settings > Privacy), kept with the device photo answers in <data>\DeviceReference\settings.json.
    public static string DownloadMode
    {
        get
        {
            string mode = (DeviceReferenceService.ReadSettings().CockpitPictureDownloads ?? string.Empty).Trim().ToLowerInvariant();
            return mode is Always or Never ? mode : Ask;
        }
        set
        {
            string mode = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (mode is not (Ask or Always or Never))
                mode = Ask;
            DeviceReferenceService.ReferenceSettings settings = DeviceReferenceService.ReadSettings();
            settings.CockpitPictureDownloads = mode;
            DeviceReferenceService.WriteSettings(settings);
        }
    }

    // What the Cockpits page does for a variant without pictures that has a pack: offer it, download it, or nothing.
    public static string OfferFor(string mode, bool hasPack, bool hasPictures, bool dismissed)
    {
        if (!hasPack || hasPictures || mode == Never)
            return Never;
        if (mode == Always)
            return Always;
        return dismissed ? Never : Ask;
    }

    public string ManifestUrl => ManifestUrlOf(Feed);

    public static string ManifestUrlOf(string feed)
    {
        if (feed.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return feed;
        if (DefaultUpdateTransport.LocalPath(feed) is { } folder)
            return Path.Combine(folder, ManifestName);
        return feed.TrimEnd('/') + "/" + ManifestName;
    }

    // Where the asset of a pack is: next to the manifest of its feed.
    public string AssetUrl(CockpitPicturePack pack)
    {
        string feed = pack.Feed.Length > 0 ? pack.Feed : Feed;
        if (DefaultUpdateTransport.LocalPath(feed) is { } local)
            return Path.Combine(LocalFolder(local), pack.Asset);
        return BaseUrl(feed) + Uri.EscapeDataString(pack.Asset);
    }

    private static string LocalFolder(string local) =>
        local.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(local) ?? local : local;

    private static string BaseUrl(string feed) =>
        feed.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? feed[..(feed.LastIndexOf('/') + 1)] : feed.TrimEnd('/') + "/";

    // The feed of a further tag a manifest names: on the web the release next to it (.../download/<tag>/); in a local
    // folder (packs checked before they are published) the folder <tag> in it or next to it, else the folder next to it
    // whose manifest.json has that tag. Null: not found.
    public static string? ResolveFeed(string parent, string tag)
    {
        if (!SafeAsset.IsMatch(tag))
            return null;
        if (DefaultUpdateTransport.LocalPath(parent) is { } local)
        {
            string folder = LocalFolder(local).TrimEnd('\\', '/');
            string? up = Path.GetDirectoryName(folder);
            foreach (string? candidate in new[] { Path.Combine(folder, tag), up == null ? null : Path.Combine(up, tag) })
            {
                if (candidate != null && File.Exists(Path.Combine(candidate, ManifestName)))
                    return candidate;
            }
            if (up == null || !Directory.Exists(up))
                return null;
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(up).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (string.Equals(TagOf(Path.Combine(dir, ManifestName)), tag, StringComparison.Ordinal))
                        return dir;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            return null;
        }
        string here = BaseUrl(parent).TrimEnd('/');
        int slash = here.LastIndexOf('/');
        return slash < 0 ? null : here[..(slash + 1)] + Uri.EscapeDataString(tag) + "/";
    }

    // The "tag" of a local manifest (the build tool writes it near the top); empty when there is none.
    private static string TagOf(string manifest)
    {
        try
        {
            if (!File.Exists(manifest))
                return string.Empty;
            using FileStream stream = File.OpenRead(manifest);
            byte[] head = new byte[4096];
            int read = stream.Read(head, 0, head.Length);
            Match m = Regex.Match(Encoding.UTF8.GetString(head, 0, read), "\"tag\"\\s*:\\s*\"([^\"\\\\]+)\"");
            return m.Success ? m.Groups[1].Value : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    // The list of all packs: the feed's manifest and the further feeds it names ("feeds", also those of the further
    // ones, at most MaxFeeds). A pack (game and set) comes from the first feed that has it; a further feed that cannot be
    // read is left out (FailedFeeds), only the first feed's failure fails the list.
    public async Task<CockpitPicturePackManifest> LoadManifestAsync(CancellationToken cancellationToken = default)
    {
        CockpitPicturePackManifest main = await LoadFeedAsync(Feed, cancellationToken);
        List<CockpitPicturePack> packs = [];
        HashSet<string> have = new(StringComparer.OrdinalIgnoreCase);
        void Add(CockpitPicturePackManifest m)
        {
            foreach (CockpitPicturePack p in m.Packs)
            {
                if (have.Add(p.Game + "/" + p.Set))
                    packs.Add(p);
            }
        }
        Add(main);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { main.Tag };
        List<string> failed = [];
        Queue<(string Parent, string Tag)> queue = new(main.FeedTags.Select(t => (Feed, t)));
        int read = 0;
        while (queue.Count > 0 && read < MaxFeeds)
        {
            (string parent, string tag) = queue.Dequeue();
            if (!seen.Add(tag))
                continue;
            read++;
            string? feed = ResolveFeed(parent, tag);
            if (feed == null)
            {
                failed.Add(tag);
                continue;
            }
            CockpitPicturePackManifest more;
            try
            {
                more = await LoadFeedAsync(feed, cancellationToken);
            }
            catch (CockpitPicturePackException)
            {
                failed.Add(tag);
                continue;
            }
            Add(more);
            foreach (string next in more.FeedTags)
                queue.Enqueue((feed, next));
        }
        return new CockpitPicturePackManifest { PackVersion = main.PackVersion, Tag = main.Tag, Packs = packs, FeedTags = main.FeedTags, FailedFeeds = failed };
    }

    // One manifest; its packs know the feed their assets are in.
    private async Task<CockpitPicturePackManifest> LoadFeedAsync(string feed, CancellationToken cancellationToken)
    {
        HttpStatusCode status;
        string body;
        try
        {
            (status, body) = await transport.GetStringAsync(ManifestUrlOf(feed), cancellationToken);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new CockpitPicturePackException(T("Could not reach JoyPro's picture packs (no internet?): {0}", ex.Message));
        }
        if (status == HttpStatusCode.NotFound)
            throw new CockpitPicturePackException(T("No picture packs are published yet."));
        if ((int)status < 200 || (int)status > 299)
            throw new CockpitPicturePackException(T("The picture pack server answered {0}.", (int)status));
        CockpitPicturePackManifest m = ParseManifest(body);
        return new CockpitPicturePackManifest
        {
            PackVersion = m.PackVersion, Tag = m.Tag, FeedTags = m.FeedTags, Packs = m.Packs.Select(p => p with { Feed = feed }).ToList()
        };
    }

    // The manifest; packs with unsafe names, no checksum or no asset are left out.
    public static CockpitPicturePackManifest ParseManifest(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new CockpitPicturePackException(T("The list of picture packs could not be read."));
        }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !string.Equals(Str(root, "format"), FormatName, StringComparison.OrdinalIgnoreCase))
                throw new CockpitPicturePackException(T("The list of picture packs could not be read."));
            int packVersion = Int(root, "packVersion", 1);
            if (packVersion > SupportedPackVersion)
                throw new CockpitPicturePackException(T("These picture packs need a newer JoyPro."));
            List<CockpitPicturePack> packs = [];
            if (root.TryGetProperty("packs", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement p in list.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object)
                        continue;
                    string game = Str(p, "game"), set = Str(p, "set"), asset = Str(p, "asset"), sha = Str(p, "sha256").ToLowerInvariant();
                    if (!SafeName.IsMatch(game) || !SafeName.IsMatch(set) || set.Contains("..", StringComparison.Ordinal) || !SafeAsset.IsMatch(asset) || !Regex.IsMatch(sha, "^[0-9a-f]{64}$"))
                        continue;
                    List<string> planes = [];
                    if (p.TryGetProperty("planes", out JsonElement names) && names.ValueKind == JsonValueKind.Array)
                        planes.AddRange(names.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString() ?? string.Empty).Where(n => n.Length > 0));
                    string id = Str(p, "id");
                    packs.Add(new CockpitPicturePack(id.Length > 0 ? id : game + "/" + set, game, set, planes, asset, Long(p, "size"), sha,
                        Math.Max(1, Int(p, "version", 1)), Str(p, "built"), Str(p, "picturesBuilt"))
                    {
                        Low = p.TryGetProperty("low", out JsonElement low) && low.ValueKind == JsonValueKind.True
                    });
                }
            }
            // "feeds": tags as text or {"tag": ...}; only safe names.
            List<string> feeds = [];
            if (root.TryGetProperty("feeds", out JsonElement feedList) && feedList.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement f in feedList.EnumerateArray())
                {
                    string tag = f.ValueKind == JsonValueKind.String ? f.GetString() ?? string.Empty : f.ValueKind == JsonValueKind.Object ? Str(f, "tag") : string.Empty;
                    if (SafeAsset.IsMatch(tag) && !feeds.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        feeds.Add(tag);
                }
            }
            return new CockpitPicturePackManifest { PackVersion = packVersion, Tag = Str(root, "tag"), Packs = packs, FeedTags = feeds };
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : 0;

    public string FolderOf(CockpitPicturePack pack) => Path.Combine(Root, pack.Game, pack.Set);

    public CockpitPackState StateOf(CockpitPicturePack pack)
    {
        CockpitPictureSet? set;
        try
        {
            set = CockpitPictureSet.Load(FolderOf(pack));
        }
        catch (Exception)
        {
            set = null;
        }
        if (set == null)
            return CockpitPackState.Missing;
        if (set.FromPack)
            return set.PackVersion < pack.Version ? CockpitPackState.UpdateAvailable : CockpitPackState.Installed;
        // Built on this PC: kept when it is at least as new as the pack's pictures (same time format, ISO 8601 UTC).
        return pack.PicturesBuilt.Length > 0 && string.CompareOrdinal(set.Built, pack.PicturesBuilt) < 0 ? CockpitPackState.LocalOlder : CockpitPackState.LocalNewer;
    }

    // Whether a download would change something: a missing set, a newer pack, an older set of this PC; with force also
    // a current pack and a newer set of this PC.
    public static bool Wanted(CockpitPackState state, bool force) =>
        state is CockpitPackState.Missing or CockpitPackState.UpdateAvailable or CockpitPackState.LocalOlder || force;

    // Downloads, checks and installs a pack (progress in bytes). False: nothing to do (see Wanted).
    public async Task<bool> DownloadAsync(CockpitPicturePack pack, IProgress<long>? progress = null, CancellationToken cancellationToken = default, bool force = false)
    {
        if (!Wanted(StateOf(pack), force))
            return false;
        string work = Path.Combine(Root, ".downloads");
        Directory.CreateDirectory(work);
        string zip = Path.Combine(work, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            IProgress<double>? fraction = progress == null ? null : new SyncProgress<double>(f => progress.Report((long)(f * Math.Max(pack.Size, 1))));
            try
            {
                await transport.DownloadAsync(AssetUrl(pack), zip, fraction, cancellationToken);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException)
            {
                throw new CockpitPicturePackException(T("The download of the pictures failed: {0}", ex.Message));
            }
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => Install(pack, zip, force), cancellationToken);
            return true;
        }
        finally
        {
            TryDelete(zip);
            try
            {
                if (Directory.Exists(work) && !Directory.EnumerateFileSystemEntries(work).Any())
                    Directory.Delete(work);
            }
            catch (Exception)
            {
            }
        }
    }

    // Progress reported on the caller's thread at once (the view model throttles the text itself).
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public static string Sha256Of(string file)
    {
        using FileStream stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // Checks the zip against the manifest, unpacks it next to the set and puts it in place of the set.
    public void Install(CockpitPicturePack pack, string zipFile, bool force = false)
    {
        if (!string.Equals(Sha256Of(zipFile), pack.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CockpitPicturePackException(T("The download of the pictures for {0} does not match its checksum; nothing was installed. Try again later.", pack.Set));
        if (!Wanted(StateOf(pack), force))
            return;
        string work = Path.Combine(Root, ".downloads");
        string temp = Path.Combine(work, "new-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Extract(zipFile, temp);
            CockpitPictureSet set = CockpitPictureSet.Load(temp)
                                    ?? throw new CockpitPicturePackException(T("The download for {0} is no picture pack; nothing was installed.", pack.Set));
            WritePackInfo(temp, pack, set.Built);
            string target = FolderOf(pack);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string? old = null;
            if (Directory.Exists(target))
            {
                old = Path.Combine(work, "old-" + Guid.NewGuid().ToString("N"));
                Directory.Move(target, old);
            }
            try
            {
                Directory.Move(temp, target);
            }
            catch (Exception)
            {
                if (old != null && !Directory.Exists(target))
                    Directory.Move(old, target);
                throw;
            }
            if (old != null)
                TryDeleteFolder(old);
            MergeIndex(Path.Combine(Root, pack.Game), pack.Set, pack.Planes.Concat(set.Planes).Append(set.Plane).Append(pack.Set));
        }
        finally
        {
            TryDeleteFolder(temp);
        }
    }

    // Only plain files of the picture formats at the zip's top level; nothing outside the folder.
    internal static void Extract(string zipFile, string folder)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipFile);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            string name = entry.FullName;
            if (name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal) || !SafeName.IsMatch(name) ||
                !PackFileTypes.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                throw new CockpitPicturePackException(T("The picture pack holds a file it should not have ({0}); nothing was installed.", name));
            total += entry.Length;
            if (total > MaxUnpackedBytes)
                throw new CockpitPicturePackException(T("The picture pack is too big; nothing was installed."));
            entry.ExtractToFile(Path.Combine(folder, name), overwrite: true);
        }
    }

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static void WritePackInfo(string folder, CockpitPicturePack pack, string picturesBuilt)
    {
        Dictionary<string, object> info = new()
        {
            ["format"] = CockpitPictureSet.PackFormatName,
            ["id"] = pack.Id,
            ["game"] = pack.Game,
            ["set"] = pack.Set,
            ["version"] = pack.Version,
            ["built"] = pack.Built,
            ["picturesBuilt"] = picturesBuilt,
            ["asset"] = pack.Asset,
            ["sha256"] = pack.Sha256,
            ["installed"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)
        };
        File.WriteAllText(Path.Combine(folder, CockpitPictureSet.PackFileName), JsonSerializer.Serialize(info, Compact), new UTF8Encoding(false));
    }

    // The game's index.json (aircraft name -> set folder) with the pack's names; a name that already leads to another
    // set on this PC keeps it.
    public static void MergeIndex(string gameFolder, string set, IEnumerable<string> names)
    {
        Directory.CreateDirectory(gameFolder);
        string file = Path.Combine(gameFolder, "index.json");
        List<KeyValuePair<string, string>> map = [];
        try
        {
            if (File.Exists(file))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("aircraft", out JsonElement aircraft) && aircraft.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in aircraft.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.String)
                            map.Add(new KeyValuePair<string, string>(p.Name, p.Value.GetString() ?? string.Empty));
                    }
                }
            }
        }
        catch (Exception)
        {
            map.Clear();
        }
        foreach (string name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string norm = CockpitPictureSet.Norm(name);
            if (norm.Length == 0)
                continue;
            int at = map.FindIndex(kv => CockpitPictureSet.Norm(kv.Key) == norm);
            if (at < 0)
            {
                map.Add(new KeyValuePair<string, string>(name, set));
                continue;
            }
            string current = map[at].Value;
            bool otherSet = !string.Equals(current, set, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(gameFolder, current, "pictures.json"));
            if (!otherSet)
                map[at] = new KeyValuePair<string, string>(map[at].Key, set);
        }
        Dictionary<string, string> aircraftMap = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> kv in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            aircraftMap.TryAdd(kv.Key, kv.Value);
        Dictionary<string, object> index = new() { ["format"] = "joypro-cockpit-pictures-index", ["aircraft"] = aircraftMap };
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(index, Compact), new UTF8Encoding(false));
        File.Move(tmp, file, overwrite: true);
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
        catch (Exception)
        {
        }
    }
}
