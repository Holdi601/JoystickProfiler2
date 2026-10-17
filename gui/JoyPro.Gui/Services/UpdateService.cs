using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JoyPro.Gui.Services;

// A version as JoyPro's releases are tagged: v2.1.0, 2.1, 2.1.0-beta.2 (+build metadata is ignored). Compared as in
// Semantic Versioning: a pre-release comes before its release (2.1.0-beta.2 < 2.1.0), numeric parts compare as numbers.
public readonly record struct SemVersion(int Major, int Minor, int Patch, string PreRelease) : IComparable<SemVersion>
{
    public static SemVersion? Parse(string? text)
    {
        string raw = (text ?? string.Empty).Trim().TrimStart('v', 'V');
        int plus = raw.IndexOf('+');
        if (plus >= 0)
            raw = raw[..plus];
        string pre = string.Empty;
        int dash = raw.IndexOf('-');
        if (dash >= 0)
        {
            pre = raw[(dash + 1)..];
            raw = raw[..dash];
        }
        string[] parts = raw.Split('.');
        if (parts.Length is < 1 or > 4 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit)))
            return null;
        int Part(int i) => i < parts.Length && int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
        return new SemVersion(Part(0), Part(1), Part(2), pre);
    }

    public bool IsPreRelease => PreRelease.Length > 0;

    public int CompareTo(SemVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c == 0)
            c = Minor.CompareTo(other.Minor);
        if (c == 0)
            c = Patch.CompareTo(other.Patch);
        if (c != 0)
            return c;
        if (PreRelease.Length == 0 && other.PreRelease.Length == 0)
            return 0;
        // A release comes after its pre-releases.
        if (PreRelease.Length == 0)
            return 1;
        if (other.PreRelease.Length == 0)
            return -1;
        string[] a = PreRelease.Split('.');
        string[] b = other.PreRelease.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool aNum = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out int an);
            bool bNum = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out int bn);
            int part = aNum && bNum ? an.CompareTo(bn) : aNum ? -1 : bNum ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (part != 0)
                return Math.Sign(part);
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => Major + "." + Minor + "." + Patch + (PreRelease.Length > 0 ? "-" + PreRelease : string.Empty);
}

public sealed class ReleaseInfo
{
    public string Tag { get; init; } = string.Empty;
    public SemVersion Version { get; init; }
    public string Notes { get; init; } = string.Empty;
    public string PageUrl { get; init; } = string.Empty;
    public string PackageUrl { get; init; } = string.Empty;
    public string PackageName { get; init; } = string.Empty;
    public long PackageSize { get; init; }
    public string ChecksumUrl { get; init; } = string.Empty;
    public DateTimeOffset? Published { get; init; }

    public bool HasPackage => PackageUrl.Length > 0 && ChecksumUrl.Length > 0;
}

public enum UpdateCheckOutcome
{
    // A newer release with its package and checksum: JoyPro can install it.
    NewerAvailable,
    // A newer release, but without the package or its checksum: only the release page helps.
    NewerWithoutPackage,
    UpToDate,
    // The repository has no published release (or is private).
    NoRelease,
    // Offline, GitHub not reachable, rate limit, a broken answer.
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseInfo? Release, string Message)
{
    public bool IsNewer => Outcome is UpdateCheckOutcome.NewerAvailable or UpdateCheckOutcome.NewerWithoutPackage;
}

// The downloaded package does not match its checksum (or is no JoyPro package): nothing was installed.
public sealed class UpdateVerificationException(string message) : Exception(message);

// How the updater reads its feed and downloads: https from GitHub, or a local file / file:// address (tests and a
// local test feed, JOYPRO_UPDATE_FEED).
public interface IUpdateTransport
{
    Task<(HttpStatusCode Status, string Body)> GetStringAsync(string url, CancellationToken cancellationToken);

    Task DownloadAsync(string url, string targetFile, IProgress<double>? progress, CancellationToken cancellationToken);
}

public sealed class DefaultUpdateTransport : IUpdateTransport
{
    private static readonly HttpClient Http = CreateClient();

    public static DefaultUpdateTransport Instance { get; } = new();

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JoyPro2/" + AppInfo.Version);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    // A file:// address or a plain path: the local file.
    internal static string? LocalPath(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return uri.IsFile ? uri.LocalPath : null;
        return Path.IsPathRooted(url) ? url : null;
    }

    public async Task<(HttpStatusCode Status, string Body)> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        if (LocalPath(url) is { } file)
            return File.Exists(file) ? (HttpStatusCode.OK, await File.ReadAllTextAsync(file, cancellationToken)) : (HttpStatusCode.NotFound, string.Empty);
        using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task DownloadAsync(string url, string targetFile, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (LocalPath(url) is { } file)
        {
            await using FileStream source = File.OpenRead(file);
            await using FileStream target = File.Create(targetFile);
            await CopyAsync(source, target, source.Length, progress, cancellationToken);
            return;
        }
        using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream download = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream output = File.Create(targetFile);
        await CopyAsync(download, output, response.Content.Headers.ContentLength ?? -1, progress, cancellationToken);
    }

    private static async Task CopyAsync(Stream source, Stream target, long length, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            done += read;
            if (length > 0)
                progress?.Report(Math.Min(1, done / (double)length));
        }
        progress?.Report(1);
    }
}

// Self-updater (docs/updates.md): the latest GitHub release of the public repository carries
// "JoyPro2-<version>-win-x64.zip" and "<zip>.sha256" (tools/publish_release.ps1). The package is downloaded, checked
// against its checksum, unpacked into <data>\Updates and copied over the install by a script that runs after JoyPro2
// exits and starts it again; DB files are only replaced by newer ones, so refreshed DBs survive an update.
public sealed class UpdateService
{
    // A feed of its own instead of GitHub (a releases/latest JSON as a file path, file:// or http(s) address): tests and
    // checking a release before it is published.
    public const string FeedVariable = "JOYPRO_UPDATE_FEED";

    private readonly IUpdateTransport transport;

    public UpdateService(string feedUrl, SemVersion currentVersion, IUpdateTransport? transport = null, string? updatesFolder = null)
    {
        FeedUrl = feedUrl;
        CurrentVersion = currentVersion;
        this.transport = transport ?? DefaultUpdateTransport.Instance;
        UpdatesFolder = updatesFolder ?? AppInfo.EnsureSubfolder("Updates");
    }

    public string FeedUrl { get; }

    public SemVersion CurrentVersion { get; }

    public string UpdatesFolder { get; }

    public static SemVersion AppVersion => SemVersion.Parse(AppInfo.Version) ?? new SemVersion(0, 0, 0, string.Empty);

    // "owner/repo" (Settings > About), or JoyPro's own repository.
    public static string NormalizeRepository(string? repository)
    {
        string text = (repository ?? string.Empty).Trim().Trim('/');
        const string prefix = "https://github.com/";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            text = text[prefix.Length..];
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            text = text[..^4];
        string[] parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[0] + "/" + parts[1] : AppInfo.RepositoryOwner + "/" + AppInfo.RepositoryName;
    }

    public static string FeedUrlFor(string? repository)
    {
        string? feed = Environment.GetEnvironmentVariable(FeedVariable);
        if (!string.IsNullOrWhiteSpace(feed))
            return feed.Trim();
        return "https://api.github.com/repos/" + NormalizeRepository(repository) + "/releases/latest";
    }

    public static string ReleasesPageFor(string? repository) => "https://github.com/" + NormalizeRepository(repository) + "/releases";

    // Kept for callers that only need the numbers (System.Version, no pre-release).
    public static Version? ParseVersion(string text)
    {
        SemVersion? version = SemVersion.Parse(text);
        return version == null ? null : new Version(version.Value.Major, version.Value.Minor, version.Value.Patch);
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        HttpStatusCode status;
        string body;
        try
        {
            (status, body) = await transport.GetStringAsync(FeedUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, null, T("Could not reach the update server (no internet?): {0}", ex.Message));
        }

        if (status == HttpStatusCode.NotFound)
            return new UpdateCheckResult(UpdateCheckOutcome.NoRelease, null, T("No published release found (none yet, or the repository is private)."));
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, null, T("GitHub refused the check for now (too many checks from this network); try again later."));
        if ((int)status < 200 || (int)status > 299)
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, null, T("The update server answered {0}.", (int)status));

        ReleaseInfo? release;
        try
        {
            release = ParseRelease(body);
        }
        catch (JsonException)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, null, T("The update server's answer could not be read."));
        }
        if (release == null)
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, null, T("The latest release has no version in its tag."));

        if (release.Version <= CurrentVersion)
            return new UpdateCheckResult(UpdateCheckOutcome.UpToDate, release, T("JoyPro2 {0} is up to date (latest release: {1}).", CurrentVersion, release.Version));
        if (!release.HasPackage)
            return new UpdateCheckResult(UpdateCheckOutcome.NewerWithoutPackage, release,
                T("Version {0} is available, but its package or checksum is missing; download it from the release page.", release.Version));
        return new UpdateCheckResult(UpdateCheckOutcome.NewerAvailable, release, T("Version {0} is available (you have {1}).", release.Version, CurrentVersion));
    }

    // GitHub's release JSON (releases/latest): tag, notes, page, and the package and checksum assets. A draft or a
    // pre-release is no update for a released JoyPro.
    internal static ReleaseInfo? ParseRelease(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("not an object");
        string tag = Str(root, "tag_name");
        SemVersion? version = SemVersion.Parse(tag);
        if (version == null)
            return null;
        string package = string.Empty, packageName = string.Empty, checksum = string.Empty;
        long size = 0;
        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name = Str(asset, "name");
                string url = Str(asset, "browser_download_url");
                if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                {
                    package = url;
                    packageName = name;
                    size = asset.TryGetProperty("size", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                }
                else if (name.EndsWith("-win-x64.zip.sha256", StringComparison.OrdinalIgnoreCase))
                {
                    checksum = url;
                }
            }
        }
        DateTimeOffset? published = DateTimeOffset.TryParse(Str(root, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset when) ? when : null;
        return new ReleaseInfo
        {
            Tag = tag,
            Version = version.Value,
            Notes = Str(root, "body").Replace("\r\n", "\n").Trim(),
            PageUrl = Str(root, "html_url"),
            PackageUrl = package,
            PackageName = packageName,
            PackageSize = size,
            ChecksumUrl = checksum,
            Published = published
        };
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    // The first word of a .sha256 file ("<hash>  <file name>"), as 64 hex digits; empty when it is none.
    internal static string ReadChecksum(string text)
    {
        string first = (text ?? string.Empty).Trim().Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return first.Length == 64 && first.All(Uri.IsHexDigit) ? first : string.Empty;
    }

    // Downloads, verifies and unpacks the release; returns the folder that holds JoyPro.Gui.exe. A package that does
    // not match its checksum is deleted and nothing is unpacked (UpdateVerificationException).
    public async Task<string> StageAsync(ReleaseInfo release, IProgress<string>? progress, IProgress<double>? downloadProgress = null, CancellationToken cancellationToken = default)
    {
        if (!release.HasPackage)
            throw new UpdateVerificationException(T("The release has no package or no checksum; download it from the release page."));
        string folder = Path.Combine(UpdatesFolder, release.Version.ToString());
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string zipPath = Path.Combine(folder, "package.zip");

        progress?.Report(T("Downloading JoyPro2 {0}...", release.Version));
        await transport.DownloadAsync(release.PackageUrl, zipPath, downloadProgress, cancellationToken);

        progress?.Report(T("Checking the download..."));
        (HttpStatusCode status, string checksumText) = await transport.GetStringAsync(release.ChecksumUrl, cancellationToken);
        string expected = (int)status is >= 200 and <= 299 ? ReadChecksum(checksumText) : string.Empty;
        string actual;
        await using (FileStream file = File.OpenRead(zipPath))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        if (expected.Length == 0 || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(folder);
            throw new UpdateVerificationException(expected.Length == 0
                ? T("The checksum of the package could not be read; the update was not installed.")
                : T("The downloaded package does not match its checksum (it is damaged or not the published one); the update was not installed."));
        }

        progress?.Report(T("Unpacking..."));
        string extracted = Path.Combine(folder, "files");
        try
        {
            ZipFile.ExtractToDirectory(zipPath, extracted);
        }
        catch (InvalidDataException)
        {
            TryDelete(folder);
            throw new UpdateVerificationException(T("The package is no readable zip file; the update was not installed."));
        }
        string? root = Directory.GetFiles(extracted, "JoyPro.Gui.exe", SearchOption.AllDirectories).Select(Path.GetDirectoryName).OrderBy(p => p!.Length).FirstOrDefault();
        if (root == null)
        {
            TryDelete(folder);
            throw new UpdateVerificationException(T("The package does not contain JoyPro.Gui.exe; the update was not installed."));
        }
        File.Delete(zipPath);
        return root;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // The copy script beside the staged files; LaunchInstaller starts it and the caller then closes JoyPro2.
    public static string WriteInstallerScript(string stagedFolder)
    {
        string script = Path.Combine(Path.GetDirectoryName(stagedFolder.TrimEnd('\\', '/'))!, "install-update.ps1");
        File.WriteAllText(script, InstallerScript, new UTF8Encoding(true));
        return script;
    }

    // The script waits for JoyPro2 (processId) to exit, copies the staged files over the install (DB only newer files),
    // logs to <data>\Updates\update.log and starts restartExe (none: no restart, for tests).
    public static ProcessStartInfo BuildInstallerStart(string script, string stagedFolder, string installFolder, int processId, string restartExe, string log)
    {
        string install = installFolder.TrimEnd('\\', '/');
        ProcessStartInfo start = new()
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\" -ProcessId " + processId.ToString(CultureInfo.InvariantCulture) +
                        " -Source \"" + stagedFolder.TrimEnd('\\', '/') + "\" -Target \"" + install + "\" -Restart \"" + restartExe + "\" -Log \"" + log + "\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (!CanWrite(install))
            start.Verb = "runas";
        return start;
    }

    public static void LaunchInstaller(string stagedFolder)
    {
        string install = AppInfo.InstallFolder.TrimEnd('\\', '/');
        string script = WriteInstallerScript(stagedFolder);
        string log = Path.Combine(AppInfo.EnsureSubfolder("Updates"), "update.log");
        ExternalProcess.Start(BuildInstallerStart(script, stagedFolder, install, Environment.ProcessId, Path.Combine(install, "JoyPro.Gui.exe"), log));
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            string probe = Path.Combine(folder, ".joypro-write-test");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal const string InstallerScript = """
        param([int]$ProcessId, [string]$Source, [string]$Target, [string]$Restart, [string]$Log)
        function Write-Log($message) { Add-Content -Path $Log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $message) }
        try {
            Write-Log "Waiting for JoyPro2 ($ProcessId) to close."
            try { Wait-Process -Id $ProcessId -Timeout 120 -ErrorAction Stop } catch { }
            # The tray host (tray\JoyPro.Tray.exe) would lock its files; the restarted GUI starts it again.
            Get-Process -Name 'engine','JoyPro.Tray' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Target, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force
            $sourceDb = Join-Path $Source 'engine\DB'
            robocopy $Source $Target /E /XD $sourceDb /R:5 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "copying the program files failed (robocopy $LASTEXITCODE)" }
            if (Test-Path $sourceDb) {
                # /XO: a DB file the user refreshed and that is newer than the release copy stays.
                robocopy $sourceDb (Join-Path $Target 'engine\DB') /E /XO /R:5 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
                if ($LASTEXITCODE -ge 8) { throw "copying the DB failed (robocopy $LASTEXITCODE)" }
            }
            Write-Log "Update installed from $Source."
        }
        catch {
            Write-Log ("Update failed: " + $_)
        }
        if ($Restart) { Start-Process -FilePath $Restart } else { Write-Log "No restart asked." }
        """;
}
