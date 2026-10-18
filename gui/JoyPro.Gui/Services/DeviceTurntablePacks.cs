using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace JoyPro.Gui.Services;

// One 3D view pack of the manifest: the turntable of one device model (docs/device-reference.md "3D view").
public sealed record DeviceTurntablePack(string Model, string Asset, long Size, string Sha256, int Version, string Built, string Source)
{
    public double SizeMb => Size / 1048576.0;
}

// manifest.json of the 3D view release ("joypro-device-3d-packs", tools/build_device_3d_packs.ps1).
public sealed class DeviceTurntablePackManifest
{
    public int PackVersion { get; init; }

    public string Tag { get; init; } = string.Empty;

    public IReadOnlyList<DeviceTurntablePack> Packs { get; init; } = [];

    public DeviceTurntablePack? Find(string model) =>
        Packs.FirstOrDefault(p => string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));
}

public enum DeviceTurntablePackState
{
    Missing,
    Installed,
    UpdateAvailable,
    // A turntable made on this PC (no pack.json): never replaced.
    Local
}

public sealed class DeviceTurntablePackException(string message) : Exception(message);

// The 3D views of the devices are not part of JoyPro's package: they are release assets (GitHub release
// "device-3d-v1"), one zip per device model plus manifest.json, made by tools/build_device_3d_packs.ps1. When a
// connected device has a pack, JoyPro downloads it (Settings > Privacy "Pictures from JoyPro's releases": ask / always /
// never, the setting of the cockpit picture packs), checks its SHA-256, unpacks it to a temporary folder and moves it to
// <data>\DeviceReference\Turntables\<model> (pack.json keeps the version). Without a pack (offline, never) the Layout
// page shows the flat drawing.
public sealed class DeviceTurntablePacks
{
    public const string FormatName = "joypro-device-3d-packs";
    public const string PackFormatName = "joypro-device-3d-pack";
    public const int SupportedPackVersion = 1;
    public const string ManifestName = "manifest.json";
    public const string PackFileName = "pack.json";
    public const string ReleaseTag = "device-3d-v1";
    // Another feed (a folder, a file:// or http(s) address of a folder or of its manifest.json): tests and checking the
    // packs before they are published.
    public const string FeedVariable = "JOYPRO_DEVICE_3D_FEED";

    private const long MaxUnpackedBytes = 256L * 1024 * 1024;
    private static readonly Regex SafeModel = new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeAsset = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeFile = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

    private readonly IUpdateTransport transport;

    public DeviceTurntablePacks(string? feed = null, string? root = null, IUpdateTransport? transport = null)
    {
        Feed = string.IsNullOrWhiteSpace(feed) ? DefaultFeed : feed.Trim();
        Root = string.IsNullOrWhiteSpace(root) ? InstalledRoot() : root;
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

    // Where the downloaded 3D views live: <data>\DeviceReference\Turntables.
    public static string InstalledRoot() => Path.Combine(DeviceReferenceService.DataFolder(), "Turntables");

    public string Feed { get; }

    public string Root { get; }

    // The same setting as the cockpit picture packs (pictures from JoyPro's releases).
    public static string DownloadMode => CockpitPicturePacks.DownloadMode;

    public string ManifestUrl => CockpitPicturePacks.ManifestUrlOf(Feed);

    public string AssetUrl(DeviceTurntablePack pack)
    {
        if (DefaultUpdateTransport.LocalPath(Feed) is { } local)
        {
            string folder = local.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(local) ?? local : local;
            return Path.Combine(folder, pack.Asset);
        }
        string baseUrl = Feed.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Feed[..(Feed.LastIndexOf('/') + 1)] : Feed.TrimEnd('/') + "/";
        return baseUrl + Uri.EscapeDataString(pack.Asset);
    }

    public async Task<DeviceTurntablePackManifest> LoadManifestAsync(CancellationToken cancellationToken = default)
    {
        HttpStatusCode status;
        string body;
        try
        {
            (status, body) = await transport.GetStringAsync(ManifestUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            throw new DeviceTurntablePackException(T("Could not reach JoyPro's 3D views (no internet?): {0}", ex.Message));
        }
        if (status == HttpStatusCode.NotFound)
            throw new DeviceTurntablePackException(T("No 3D views are published yet."));
        if ((int)status < 200 || (int)status > 299)
            throw new DeviceTurntablePackException(T("The 3D view server answered {0}.", (int)status));
        return ParseManifest(body);
    }

    // Packs with an unsafe model or asset name or without a checksum are left out.
    public static DeviceTurntablePackManifest ParseManifest(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new DeviceTurntablePackException(T("The list of 3D views could not be read."));
        }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !string.Equals(Str(root, "format"), FormatName, StringComparison.OrdinalIgnoreCase))
                throw new DeviceTurntablePackException(T("The list of 3D views could not be read."));
            int packVersion = Int(root, "packVersion", 1);
            if (packVersion > SupportedPackVersion)
                throw new DeviceTurntablePackException(T("These 3D views need a newer JoyPro."));
            List<DeviceTurntablePack> packs = [];
            if (root.TryGetProperty("packs", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement p in list.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object)
                        continue;
                    string model = Str(p, "model"), asset = Str(p, "asset"), sha = Str(p, "sha256").ToLowerInvariant();
                    if (!SafeModel.IsMatch(model) || model.Contains("..", StringComparison.Ordinal) || !SafeAsset.IsMatch(asset) || !Regex.IsMatch(sha, "^[0-9a-f]{64}$"))
                        continue;
                    if (packs.Any(x => string.Equals(x.Model, model, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    packs.Add(new DeviceTurntablePack(model, asset, Long(p, "size"), sha, Math.Max(1, Int(p, "version", 1)), Str(p, "built"), Str(p, "source")));
                }
            }
            return new DeviceTurntablePackManifest { PackVersion = packVersion, Tag = Str(root, "tag"), Packs = packs };
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : 0;

    public string FolderOf(string model) => Path.Combine(Root, model);

    public DeviceTurntablePackState StateOf(DeviceTurntablePack pack)
    {
        string folder = FolderOf(pack.Model);
        if (!File.Exists(Path.Combine(folder, "turntable.json")))
            return DeviceTurntablePackState.Missing;
        string info = Path.Combine(folder, PackFileName);
        if (!File.Exists(info))
            return DeviceTurntablePackState.Local;
        int version = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(info));
            version = Int(doc.RootElement, "version", 0);
        }
        catch (Exception)
        {
        }
        return version < pack.Version ? DeviceTurntablePackState.UpdateAvailable : DeviceTurntablePackState.Installed;
    }

    public static bool Wanted(DeviceTurntablePackState state) => state is DeviceTurntablePackState.Missing or DeviceTurntablePackState.UpdateAvailable;

    // The packs of the given models that would add or update a 3D view on this PC.
    public IReadOnlyList<DeviceTurntablePack> Wanted(DeviceTurntablePackManifest manifest, IEnumerable<string> models) =>
        models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(manifest.Find).OfType<DeviceTurntablePack>().Where(p => Wanted(StateOf(p))).ToList();

    // Downloads, checks and installs a pack. False: nothing to do.
    public async Task<bool> DownloadAsync(DeviceTurntablePack pack, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Wanted(StateOf(pack)))
            return false;
        string work = Path.Combine(Root, ".downloads");
        Directory.CreateDirectory(work);
        string zip = Path.Combine(work, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            try
            {
                await transport.DownloadAsync(AssetUrl(pack), zip, progress, cancellationToken);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException)
            {
                throw new DeviceTurntablePackException(T("The download of the 3D view failed: {0}", ex.Message));
            }
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => Install(pack, zip), cancellationToken);
            return true;
        }
        finally
        {
            try
            {
                if (File.Exists(zip))
                    File.Delete(zip);
                if (Directory.Exists(work) && !Directory.EnumerateFileSystemEntries(work).Any())
                    Directory.Delete(work);
            }
            catch (Exception)
            {
            }
        }
    }

    public void Install(DeviceTurntablePack pack, string zipFile)
    {
        if (!string.Equals(CockpitPicturePacks.Sha256Of(zipFile), pack.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new DeviceTurntablePackException(T("The download of the 3D view does not match its checksum; nothing was installed. Try again later."));
        string work = Path.Combine(Root, ".downloads");
        string temp = Path.Combine(work, "new-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Extract(zipFile, temp);
            if (DeviceTurntable.Load(temp, pack.Model) is not { Frames.Count: > 0 } turntable || turntable.Frames.Any(f => !File.Exists(turntable.PathOf(f))))
                throw new DeviceTurntablePackException(T("The download is no 3D view; nothing was installed."));
            Dictionary<string, object> info = new()
            {
                ["format"] = PackFormatName,
                ["model"] = pack.Model,
                ["version"] = pack.Version,
                ["built"] = pack.Built,
                ["asset"] = pack.Asset,
                ["sha256"] = pack.Sha256,
                ["installed"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)
            };
            File.WriteAllText(Path.Combine(temp, PackFileName), JsonSerializer.Serialize(info, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
            string target = FolderOf(pack.Model);
            Directory.CreateDirectory(Root);
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
            DeviceTurntable.ClearCache();
        }
        finally
        {
            TryDeleteFolder(temp);
        }
    }

    // turntable.json, CREDITS.txt and pack.json at the top, the frames as PNG in frames/; nothing else.
    internal static void Extract(string zipFile, string folder)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipFile);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            string[] parts = entry.FullName.Split('/');
            bool ok = parts.All(p => SafeFile.IsMatch(p) && !p.Contains("..", StringComparison.Ordinal)) && !entry.FullName.Contains('\\') &&
                      (parts.Length == 1 && parts[0] is "turntable.json" or "CREDITS.txt" or PackFileName ||
                       parts.Length == 2 && parts[0] == "frames" && parts[1].EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            if (!ok)
                throw new DeviceTurntablePackException(T("The 3D view holds a file it should not have ({0}); nothing was installed.", entry.FullName));
            total += entry.Length;
            if (total > MaxUnpackedBytes)
                throw new DeviceTurntablePackException(T("The 3D view is too big; nothing was installed."));
            string target = Path.Combine(folder, Path.Combine(parts));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
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
