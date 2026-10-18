using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
        string key = width + "|" + source;
        Task<Bitmap?> task = Decoded.GetOrAdd(key, _ => LoadUncachedAsync(source, width));
        // A picture that could not be loaded is not remembered as missing: the next look tries again (a busy server
        // answers "too many requests" for a while).
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted || t.IsCanceled || t.Result == null)
                Decoded.TryRemove(new System.Collections.Generic.KeyValuePair<string, Task<Bitmap?>>(key, task));
        }, TaskScheduler.Default);
        return task;
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
        byte[]? data = await DownloadAsync(uri).ConfigureAwait(false);
        if (data == null || data.Length == 0 || data.Length > MaxBytes)
            return null;
        await File.WriteAllBytesAsync(file, data).ConfigureAwait(false);
        return file;
    }

    // One download at a time per site, a little apart, and again after "too many requests" / "unavailable" (429, 503:
    // Wikimedia answers so when a program asks for several pictures at once) as long as the server's Retry-After says,
    // else after 2, 4, 8 seconds. Only an image answer counts (an error page is not a picture).
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Sites = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> LastRequest = new(StringComparer.OrdinalIgnoreCase);

    // Waits between tries and between requests to one site (tests make them short).
    public static TimeSpan RequestSpacing { get; set; } = TimeSpan.FromMilliseconds(400);
    public static TimeSpan FirstRetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    public const int MaxTries = 4;

    // The test's HTTP handler (null: the real one).
    public static HttpMessageHandler? TestHandler
    {
        set => testClient = value == null ? null : CreateClient(value);
    }

    private static HttpClient? testClient;

    public static async Task<byte[]?> DownloadAsync(Uri uri)
    {
        HttpClient http = testClient ?? Http;
        SemaphoreSlim gate = Sites.GetOrAdd(uri.Host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            TimeSpan delay = FirstRetryDelay;
            for (int attempt = 1; attempt <= MaxTries; attempt++)
            {
                if (LastRequest.TryGetValue(uri.Host, out DateTime last))
                {
                    TimeSpan wait = last + RequestSpacing - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait).ConfigureAwait(false);
                }
                LastRequest[uri.Host] = DateTime.UtcNow;
                using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                int code = (int)response.StatusCode;
                if ((code == 429 || code == 503) && attempt < MaxTries)
                {
                    TimeSpan retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? delay;
                    retry = retry < TimeSpan.Zero ? delay : retry > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : retry;
                    await Task.Delay(retry).ConfigureAwait(false);
                    delay += delay;
                    continue;
                }
                if (!response.IsSuccessStatusCode || (response.Content.Headers.ContentLength ?? 0) > MaxBytes)
                    return null;
                string? type = response.Content.Headers.ContentType?.MediaType;
                if (type != null && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && type != "application/octet-stream")
                    return null;
                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    // Who asks (Wikimedia's robot policy wants a program's name, version and a way to reach its makers).
    public static string UserAgent => "JoyPro2/" + AppInfo.Version + " (+https://github.com/" + AppInfo.RepositoryOwner + "/" + AppInfo.RepositoryName + ")";

    private static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        HttpClient client = handler == null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent);
        return client;
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
    }
}
