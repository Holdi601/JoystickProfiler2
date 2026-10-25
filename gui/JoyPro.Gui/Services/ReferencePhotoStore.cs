using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Services;

// The device reference photos on this PC (docs/device-reference.md "Reference photos on this PC"): JoyPro ships only
// their addresses (DB\DevicePhotos, a SHA-256 per photo); the user's PC downloads them from the original address, checks
// the SHA-256 and keeps them in <data>\DeviceReference\ReferencePhotos\<model>\<view>-<sha12>.<ext> with index.json.
// When the address fails or its content changed, the photo comes from its Wayback Machine snapshot (web.archive.org),
// checked against the same SHA-256. A photo the maker changed and the archive does not have is kept as it is now
// ("changed": JoyPro's curated positions do not apply to it). Only with the user's consent for the device
// (DevicePhotoConsent); the caller checks it.
public sealed class ReferencePhotoStore
{
    public static ReferencePhotoStore Default { get; set; } = new();

    private readonly object gate = new();
    private readonly string? folderOverride;
    private Dictionary<string, StoredPhoto>? index;
    private string? indexFolder;

    public ReferencePhotoStore(string? folder = null)
    {
        folderOverride = folder;
    }

    public string Folder => folderOverride ?? Path.Combine(DeviceReferenceService.DataFolder(), "ReferencePhotos");

    // Downloads a photo (tests: a fake server); null = DeviceImageCache's download (site gate, retries, progress).
    public Func<Uri, IProgress<double>?, CancellationToken, Task<(byte[]? Data, string Error)>>? Fetch { get; set; }

    public sealed class StoredPhoto
    {
        public string Model { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string View { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        // Relative to Folder.
        public string File { get; set; } = string.Empty;
        // "original" or "archive": where the bytes came from.
        public string Origin { get; set; } = "original";
        // The content differs from the SHA-256 on record (the maker changed the photo, the archive had no copy).
        public bool Changed { get; set; }
        public DateTime Fetched { get; set; }
    }

    public sealed record PhotoResult(string? File, string Origin, bool Changed, string Error);

    public sealed record SetResult(int Downloaded, int Already, int FromArchive, List<string> Failed);

    private string IndexFile => Path.Combine(Folder, "index.json");

    private Dictionary<string, StoredPhoto> Index()
    {
        lock (gate)
        {
            if (index != null && indexFolder == Folder)
                return index;
            Dictionary<string, StoredPhoto> loaded = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (System.IO.File.Exists(IndexFile))
                {
                    foreach (StoredPhoto photo in JsonSerializer.Deserialize<List<StoredPhoto>>(System.IO.File.ReadAllText(IndexFile)) ?? [])
                    {
                        if (photo.Url.Length > 0)
                            loaded[photo.Url] = photo;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // An unreadable index starts over; the files are found again by the next download.
            }
            index = loaded;
            indexFolder = Folder;
            return loaded;
        }
    }

    private void SaveIndex()
    {
        lock (gate)
        {
            if (index == null)
                return;
            Directory.CreateDirectory(Folder);
            string temp = IndexFile + ".tmp";
            System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(index.Values.OrderBy(p => p.Model).ThenBy(p => p.View).ToList(), new JsonSerializerOptions { WriteIndented = true }));
            System.IO.File.Move(temp, IndexFile, true);
        }
    }

    // The checked local file of a photo address; null when it is not on this PC.
    public string? FileOf(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Index().TryGetValue(url.Trim(), out StoredPhoto? stored))
            return null;
        string file = Path.Combine(Folder, stored.File);
        return System.IO.File.Exists(file) ? file : null;
    }

    public StoredPhoto? Entry(string url) => !string.IsNullOrWhiteSpace(url) && Index().TryGetValue(url.Trim(), out StoredPhoto? stored) ? stored : null;

    // The photos of a model JoyPro may download that are not on this PC yet.
    public List<DevicePhoto> Missing(RefDevice? model) =>
        model == null ? [] : DevicePhotoDb.UsablePhotos(model).Where(p => FileOf(p.Url) == null).ToList();

    public bool HasAll(RefDevice? model) => model != null && DevicePhotoDb.UsablePhotos(model).Count > 0 && Missing(model).Count == 0;

    // Whether this store can download at all (pictures from the internet on; the tests block them unless they bring a
    // fake server).
    public bool CanDownload => Fetch != null || (DeviceImageCache.InternetPicturesEnabled && !DeviceImageCache.InternetPicturesBlocked);

    private Task<(byte[]? Data, string Error)> FetchAsync(Uri uri, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (Fetch != null)
            return Fetch(uri, progress, cancellationToken);
        if (!CanDownload)
            return Task.FromResult<(byte[]?, string)>((null, T("Pictures from the internet are switched off (Settings > Privacy).")));
        return DeviceImageCache.FetchBytesAsync(uri, progress);
    }

    // One photo: the original address (unless only the archive has it), checked against the SHA-256 on record; on a
    // failure or another content the Wayback snapshot, checked the same way.
    public async Task<PhotoResult> EnsureAsync(string model, DevicePhoto photo, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        string? existing = FileOf(photo.Url);
        if (existing != null)
        {
            progress?.Report(1);
            StoredPhoto stored = Index()[photo.Url.Trim()];
            return new PhotoResult(existing, stored.Origin, stored.Changed, string.Empty);
        }
        string expected = (photo.Sha256 ?? string.Empty).Trim().ToLowerInvariant();
        byte[]? changedOriginal = null;
        string error = string.Empty;
        if (!photo.ArchiveOnly && Uri.TryCreate(photo.Url, UriKind.Absolute, out Uri? original) && original.Scheme == Uri.UriSchemeHttps)
        {
            (byte[]? data, string fetchError) = await FetchAsync(original, progress, cancellationToken).ConfigureAwait(false);
            if (data is { Length: > 0 } && IsPicture(data))
            {
                if (expected.Length == 0 || Sha(data) == expected)
                    return Save(model, photo, data, "original", false);
                changedOriginal = data;
                error = T("The photo on the site has changed since JoyPro recorded it.");
            }
            else
                error = fetchError.Length > 0 ? fetchError : T("The site sent no picture.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        string archive = DevicePhotoDb.ArchiveUrl(photo);
        if (Uri.TryCreate(archive, UriKind.Absolute, out Uri? archiveUri))
        {
            (byte[]? data, string archiveError) = await FetchAsync(archiveUri, progress, cancellationToken).ConfigureAwait(false);
            if (data is { Length: > 0 } && IsPicture(data) && (expected.Length == 0 || Sha(data) == expected))
                return Save(model, photo, data, "archive", false);
            if (error.Length == 0)
                error = archiveError.Length > 0 ? archiveError : T("The web archive has no copy of this photo.");
        }
        // The maker changed the photo and the archive has no copy of the old one: the new one, without JoyPro's positions.
        if (changedOriginal != null)
            return Save(model, photo, changedOriginal, "original", true);
        return new PhotoResult(null, string.Empty, false, error.Length > 0 ? error : T("The photo could not be downloaded."));
    }

    // Every missing photo of a model, one after the other; progress = (done, total).
    public async Task<SetResult> DownloadModelAsync(RefDevice model, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        List<DevicePhoto> photos = DevicePhotoDb.UsablePhotos(model);
        int done = 0, downloaded = 0, already = 0, archived = 0;
        List<string> failed = [];
        foreach (DevicePhoto photo in photos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FileOf(photo.Url) != null)
                already++;
            else
            {
                PhotoResult result = await EnsureAsync(model.Id, photo, null, cancellationToken).ConfigureAwait(false);
                if (result.File == null)
                    failed.Add(photo.ViewTitle + ": " + result.Error);
                else
                {
                    downloaded++;
                    if (result.Origin == "archive")
                        archived++;
                }
            }
            progress?.Report((++done, photos.Count));
        }
        return new SetResult(downloaded, already, archived, failed);
    }

    private PhotoResult Save(string model, DevicePhoto photo, byte[] data, string origin, bool changed)
    {
        string sha = Sha(data);
        string slug = DeviceVocabulary.Slug(model.Length > 0 ? model : photo.Model);
        string view = DeviceVocabulary.Slug(photo.View.Length > 0 ? photo.View : "photo");
        string relative = Path.Combine(slug, view + "-" + sha[..12] + Extension(data));
        string file = Path.Combine(Folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        System.IO.File.WriteAllBytes(file, data);
        lock (gate)
        {
            Index()[photo.Url.Trim()] = new StoredPhoto
            {
                Model = slug, Url = photo.Url.Trim(), View = photo.View, Sha256 = sha, File = relative, Origin = origin, Changed = changed, Fetched = DateTime.UtcNow
            };
            SaveIndex();
        }
        return new PhotoResult(file, origin, changed, string.Empty);
    }

    private static readonly object KnownGate = new();
    private static Dictionary<string, (RefDevice Model, DevicePhoto Photo)>? known;
    private static DeviceCatalog? knownCatalog;

    // The catalog photo an address belongs to (null for any other picture).
    public static (RefDevice Model, DevicePhoto Photo)? Known(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        DeviceCatalog catalog;
        try
        {
            catalog = DeviceReferenceService.Catalog;
        }
        catch (Exception)
        {
            return null;
        }
        lock (KnownGate)
        {
            if (known == null || !ReferenceEquals(knownCatalog, catalog))
            {
                Dictionary<string, (RefDevice, DevicePhoto)> map = new(StringComparer.OrdinalIgnoreCase);
                foreach (RefDevice model in catalog.Devices)
                {
                    foreach (DevicePhoto photo in DevicePhotoDb.UsablePhotos(model))
                        map.TryAdd(photo.Url.Trim(), (model, photo));
                }
                known = map;
                knownCatalog = catalog;
            }
            return known.TryGetValue(url.Trim(), out (RefDevice, DevicePhoto) hit) ? hit : null;
        }
    }

    public static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // JPEG, PNG, GIF, WebP, BMP by their first bytes: an error page is not a picture.
    public static bool IsPicture(byte[] data) => Extension(data).Length > 0 && Extension(data) != ".bin";

    private static string Extension(byte[] d)
    {
        if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8)
            return ".jpg";
        if (d.Length > 8 && d[0] == 0x89 && d[1] == (byte)'P' && d[2] == (byte)'N' && d[3] == (byte)'G')
            return ".png";
        if (d.Length > 6 && d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F')
            return ".gif";
        if (d.Length > 12 && d[0] == (byte)'R' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'F' && d[8] == (byte)'W' && d[9] == (byte)'E' && d[10] == (byte)'B' && d[11] == (byte)'P')
            return ".webp";
        if (d.Length > 2 && d[0] == (byte)'B' && d[1] == (byte)'M')
            return ".bmp";
        return ".bin";
    }
}
