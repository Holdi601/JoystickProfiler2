using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Services;

// The device photo list from JoyPro's releases (docs/device-reference.md "The photo list in the releases"): repaired or
// new photo addresses reach the users without an app update. The release "joypro-device-photos" (never marked latest)
// carries manifest.json ("joypro-device-photos": version, asset, size, sha256) and the asset, a zip of the DevicePhotos
// files with _sources.json and _hosts.json (tools/build_device_photo_list.ps1). JoyPro looks at most once a day, in the
// background, only while the makers' photos are not switched off (DevicePhotoConsent mode never) and pictures from the
// internet are allowed. The asset is checked against the manifest's SHA-256, unpacked to a temporary folder, validated
// by the rules of the shipped list (DevicePhotoDb.ValidateList) and moved to <data>\DeviceReference\PhotoList. The
// catalog uses it instead of the shipped DB\DevicePhotos only when its version is higher. Whatever fails, JoyPro keeps
// the list it has (silently).
public sealed class DevicePhotoList
{
    public const string FormatName = "joypro-device-photos";
    public const string ReleaseTag = "joypro-device-photos";
    public const string ManifestName = "manifest.json";
    // Another feed (a folder, a file:// or http(s) address of a folder or of its manifest.json): tests and checking a list
    // before it is published.
    public const string FeedVariable = "JOYPRO_DEVICE_PHOTO_FEED";
    private const long MaxAssetBytes = 20L * 1024 * 1024;
    private static readonly Regex SafeAsset = new("^[A-Za-z0-9][A-Za-z0-9._-]*\\.zip$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeEntry = new("^_?[A-Za-z0-9][A-Za-z0-9._-]*\\.json$", RegexOptions.CultureInvariant);

    private readonly IUpdateTransport transport;

    public DevicePhotoList(string? feed = null, string? folder = null, IUpdateTransport? transport = null)
    {
        Feed = string.IsNullOrWhiteSpace(feed) ? DefaultFeed : feed.Trim();
        Folder = string.IsNullOrWhiteSpace(folder) ? Path.Combine(DeviceReferenceService.DataFolder(), "PhotoList") : folder;
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

    // <data>\DeviceReference\PhotoList: list\ (the files), state.json (version, sha256, last check).
    public string Folder { get; }

    public string ListFolder => Path.Combine(Folder, "list");

    private string StateFile => Path.Combine(Folder, "state.json");

    public sealed class State
    {
        public int Version { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public DateTime LastCheck { get; set; }
    }

    public State ReadState()
    {
        try
        {
            return File.Exists(StateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) ?? new State() : new State();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new State();
        }
    }

    private void WriteState(State state)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    // The version of a photo folder (its _sources.json "version"; 0 = none).
    public static int VersionOf(string folder) => Directory.Exists(folder) ? DevicePhotoSources.Load(folder, null).Version : 0;

    // The folder the catalog should use instead of the shipped one: the downloaded list when it is there, valid by its
    // state and newer than the shipped list; null otherwise.
    public string? FolderFor(string shippedFolder)
    {
        State state = ReadState();
        if (state.Version <= 0 || !Directory.Exists(ListFolder) || VersionOf(ListFolder) != state.Version)
            return null;
        return state.Version > VersionOf(shippedFolder) ? ListFolder : null;
    }

    // Whether a look is due: at most once a day.
    public bool IsDue(DateTime now) => now - ReadState().LastCheck >= TimeSpan.FromDays(1);

    public enum Outcome
    {
        NotDue,
        Off,
        Unreachable,
        UpToDate,
        Rejected,
        Installed
    }

    // Looks at the feed and installs a newer valid list. shippedFolder: DB\DevicePhotos (its version and, through the
    // catalog, the models and maker hosts the list is checked against). Never throws for a network or content problem.
    public async Task<Outcome> CheckAsync(DeviceCatalog catalog, string shippedFolder, bool force = false, CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;
        if (!force && !IsDue(now))
            return Outcome.NotDue;
        State state = ReadState();
        state.LastCheck = now;
        try
        {
            WriteState(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Outcome.Rejected;
        }
        int have = Math.Max(VersionOf(shippedFolder), FolderFor(shippedFolder) != null ? state.Version : 0);
        (int version, string asset, long size, string sha)? manifest;
        try
        {
            manifest = await ReadManifestAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            return Outcome.Unreachable;
        }
        if (manifest == null)
            return Outcome.Rejected;
        if (manifest.Value.version <= have)
            return Outcome.UpToDate;
        string temp = Path.Combine(Folder, "incoming-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            string zip = Path.Combine(temp, "list.zip");
            try
            {
                await transport.DownloadAsync(AssetUrl(manifest.Value.asset), zip, null, cancellationToken);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
            {
                return Outcome.Unreachable;
            }
            FileInfo info = new(zip);
            if (!info.Exists || info.Length != manifest.Value.size || info.Length > MaxAssetBytes)
                return Outcome.Rejected;
            string actual;
            using (FileStream stream = File.OpenRead(zip))
                actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actual, manifest.Value.sha, StringComparison.OrdinalIgnoreCase))
                return Outcome.Rejected;
            string unpacked = Path.Combine(temp, "list");
            if (!Unpack(zip, unpacked))
                return Outcome.Rejected;
            if (VersionOf(unpacked) != manifest.Value.version)
                return Outcome.Rejected;
            // At least half the shipped photos: a broken or emptied list never wipes the pictures.
            int shippedPhotos = Directory.Exists(shippedFolder)
                ? Directory.GetFiles(shippedFolder, "*.json").Where(f => !Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal))
                    .Sum(f => CountPhotos(f))
                : 0;
            if (DevicePhotoDb.ValidateList(catalog, unpacked, shippedPhotos / 2).Count > 0)
                return Outcome.Rejected;
            if (Directory.Exists(ListFolder))
                Directory.Delete(ListFolder, recursive: true);
            Directory.Move(unpacked, ListFolder);
            state.Version = manifest.Value.version;
            state.Sha256 = actual;
            WriteState(state);
            return Outcome.Installed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Outcome.Rejected;
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static int CountPhotos(string file)
    {
        try
        {
            return DevicePhotoDb.ParsePhotoFile(File.ReadAllText(file)).Count;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private string ManifestUrl => Feed.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Feed
        : DefaultUpdateTransport.LocalPath(Feed) is { } folder ? Path.Combine(folder, ManifestName) : Feed.TrimEnd('/') + "/" + ManifestName;

    private string AssetUrl(string asset)
    {
        if (DefaultUpdateTransport.LocalPath(Feed) is { } local)
            return Path.Combine(local.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(local) ?? local : local, asset);
        string baseUrl = Feed.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Feed[..(Feed.LastIndexOf('/') + 1)] : Feed.TrimEnd('/') + "/";
        return baseUrl + Uri.EscapeDataString(asset);
    }

    // manifest.json: { "format": "joypro-device-photos", "version": n, "asset": "device-photos-vn.zip", "size": n, "sha256": "..." }.
    // Null: missing, not this format or not plausible.
    private async Task<(int, string, long, string)?> ReadManifestAsync(CancellationToken cancellationToken)
    {
        (HttpStatusCode status, string body) = await transport.GetStringAsync(ManifestUrl, cancellationToken);
        if ((int)status < 200 || (int)status > 299)
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out JsonElement format) || format.GetString() != FormatName)
                return null;
            int version = root.TryGetProperty("version", out JsonElement v) && v.TryGetInt32(out int vv) ? vv : 0;
            string asset = root.TryGetProperty("asset", out JsonElement a) ? a.GetString() ?? string.Empty : string.Empty;
            long size = root.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long ss) ? ss : 0;
            string sha = root.TryGetProperty("sha256", out JsonElement h) ? h.GetString() ?? string.Empty : string.Empty;
            if (version <= 0 || !SafeAsset.IsMatch(asset) || size <= 0 || size > MaxAssetBytes || sha.Length != 64)
                return null;
            return (version, asset, size, sha);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // Only flat .json files (the photo files, _sources.json, _hosts.json) at most 4 MB each; anything else rejects the list.
    private static bool Unpack(string zip, string target)
    {
        Directory.CreateDirectory(target);
        using ZipArchive archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count == 0 || archive.Entries.Count > 200)
            return false;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (!SafeEntry.IsMatch(entry.FullName) || entry.Length > 4L * 1024 * 1024)
                return false;
            entry.ExtractToFile(Path.Combine(target, entry.FullName), overwrite: false);
        }
        return File.Exists(Path.Combine(target, DevicePhotoSources.FileName));
    }
}
