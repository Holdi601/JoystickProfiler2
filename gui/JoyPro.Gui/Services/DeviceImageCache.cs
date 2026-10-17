using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace JoyPro.Gui.Services;

// Pictures for the GUI: product pictures of the device catalog (manufacturer image URLs from DB/Devices), downloaded
// once and kept in %LOCALAPPDATA%\JoyPro2\DeviceImages, and local files (game covers and DCS aircraft pictures from
// engine media). Decoded pictures are kept in memory per size. A picture that cannot be loaded is simply not shown.
public static class DeviceImageCache
{
    private const long MaxBytes = 8L * 1024 * 1024;
    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Decoded = new(StringComparer.OrdinalIgnoreCase);

    // Pictures from the internet (manufacturers' product pictures, docs/device-reference.md): off = none is downloaded or
    // shown, only local files. The GUI sets it from the user's setting; the tests switch it off.
    public static bool InternetPicturesEnabled { get; set; } = true;

    // Never a picture from the internet, also not from the download cache, whatever the setting says (the tests: no test
    // downloads anything, and renders do not depend on what this PC has cached).
    public static bool InternetPicturesBlocked { get; set; }

    // Called with the address before a picture is downloaded or taken from the download cache (tests count them).
    public static Action<string>? Downloads { get; set; }

    // Where a downloaded picture is kept (%LOCALAPPDATA%\JoyPro2\DeviceImages, or JOYPRO_DEVICE_IMAGE_CACHE).
    public static string CacheFolder()
    {
        string? overrideFolder = Environment.GetEnvironmentVariable("JOYPRO_DEVICE_IMAGE_CACHE");
        return !string.IsNullOrWhiteSpace(overrideFolder)
            ? overrideFolder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro2", "DeviceImages");
    }

    // Whether an internet picture may be loaded: a maker's device photo only with the user's consent for that device
    // (DevicePhotoConsent; asked on the Layout page and the Start page's device cards). Replaceable for tests.
    public static Func<string, bool> MayLoad { get; set; } = DevicePhotoConsent.AllowsUrl;

    private static bool IsInternet(string source) =>
        !Path.IsPathFullyQualified(source) && Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public static Task<Bitmap?> LoadAsync(string source, int width = 240)
    {
        if (string.IsNullOrWhiteSpace(source))
            return Task.FromResult<Bitmap?>(null);
        // Not allowed (yet): nothing is loaded and nothing is remembered, so a later "yes" loads it.
        if (IsInternet(source) && (!InternetPicturesEnabled || InternetPicturesBlocked || !MayLoad(source)))
            return Task.FromResult<Bitmap?>(null);
        return Decoded.GetOrAdd(width + "|" + source, _ => LoadUncachedAsync(source, width));
    }

    private static async Task<Bitmap?> LoadUncachedAsync(string source, int width)
    {
        try
        {
            string? file = await LocalFileAsync(source).ConfigureAwait(false);
            if (file == null)
                return null;
            await using FileStream stream = File.OpenRead(file);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The cached file of an https picture without downloading it (null when not cached or pictures are off).
    public static string? CachedFile(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;
        if (Path.IsPathFullyQualified(source))
            return File.Exists(source) ? source : null;
        if (!InternetPicturesEnabled || InternetPicturesBlocked || !MayLoad(source))
            return null;
        string file = Path.Combine(CacheFolder(), Hash(source) + ".img");
        return File.Exists(file) ? file : null;
    }

    // A local picture as it is; an https picture from the download cache (downloaded when missing).
    public static async Task<string?> LocalFileAsync(string source)
    {
        if (Path.IsPathFullyQualified(source))
            return File.Exists(source) ? source : null;
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || !InternetPicturesEnabled || InternetPicturesBlocked || !MayLoad(source))
            return null;
        Downloads?.Invoke(source);
        string folder = CacheFolder();
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, Hash(source) + ".img");
        if (File.Exists(file))
            return file;
        using HttpResponseMessage response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || (response.Content.Headers.ContentLength ?? 0) > MaxBytes)
            return null;
        byte[] data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (data.Length == 0 || data.Length > MaxBytes)
            return null;
        await File.WriteAllBytesAsync(file, data).ConfigureAwait(false);
        return file;
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.TryParseAdd("JoyPro2");
        return client;
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
    }
}
