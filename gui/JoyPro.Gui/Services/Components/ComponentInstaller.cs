using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace JoyPro.Gui.Services.Components;

public interface IComponentDownloader
{
    // Writes the file at url to target; reports (bytes so far, total or -1).
    Task DownloadAsync(string url, string target, IProgress<(long Done, long Total)>? progress, CancellationToken cancellationToken);
}

// The result of running an installer with administrator rights.
public sealed record InstallerRun(bool Started, int ExitCode, bool Declined, string Problem)
{
    public static InstallerRun DeclinedByUser { get; } = new(false, 1223, true, "Windows' administrator prompt was declined.");
}

public interface IInstallerRunner
{
    // Starts the installer elevated ("runas": one Windows UAC prompt, never bypassed) and waits for it.
    Task<InstallerRun> RunElevatedAsync(string installer, string arguments, CancellationToken cancellationToken);
}

// The PC side of the component installer. Tests replace every part with fakes (TestEnvironment): no test downloads,
// verifies a real file or starts an installer.
public static class ComponentServices
{
    public static IComponentProbe Probe { get; set; } = OperatingSystem.IsWindows() ? new WindowsComponentProbe() : new NoComponentProbe();

    public static IComponentDownloader Downloader { get; set; } = new HttpComponentDownloader();

    public static ISignatureVerifier SignatureVerifier { get; set; } = OperatingSystem.IsWindows() ? new AuthenticodeVerifier() : new NoSignatureVerifier();

    public static IInstallerRunner Runner { get; set; } = new ElevatedInstallerRunner();

    // Where installers are downloaded to: <data>\Components (%LOCALAPPDATA%\JoyPro2\Components).
    public static Func<string> DownloadFolder { get; set; } = () => AppInfo.EnsureSubfolder("Components");

    private sealed class NoComponentProbe : IComponentProbe
    {
        public ComponentState Probe(ComponentSpec spec) => ComponentState.Missing;
    }

    private sealed class NoSignatureVerifier : ISignatureVerifier
    {
        public SignatureCheck Verify(string file) => SignatureCheck.Invalid(T("Signatures can only be checked on Windows."));
    }
}

public sealed class HttpComponentDownloader : IComponentDownloader
{
    public async Task DownloadAsync(string url, string target, IProgress<(long Done, long Total)>? progress, CancellationToken cancellationToken)
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JoyPro2/" + AppInfo.Version);
        using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? -1;
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream file = new(target, FileMode.Create, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[81920];
        long done = 0;
        long lastReport = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            done += read;
            if (done - lastReport >= 256 * 1024)
            {
                lastReport = done;
                progress?.Report((done, total));
            }
        }
        progress?.Report((done, total));
    }
}

public sealed class ElevatedInstallerRunner : IInstallerRunner
{
    private const int ErrorCancelled = 1223;

    public async Task<InstallerRun> RunElevatedAsync(string installer, string arguments, CancellationToken cancellationToken)
    {
        Process? process;
        try
        {
            process = ExternalProcess.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(installer) ?? string.Empty
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return InstallerRun.DeclinedByUser;
        }
        catch (Win32Exception ex)
        {
            return new InstallerRun(false, ex.NativeErrorCode, false, T("The installer did not start: {0}", ex.Message));
        }
        if (process == null)
            return new InstallerRun(false, -1, false, T("The installer did not start."));
        using (process)
        {
            await process.WaitForExitAsync(cancellationToken);
            return new InstallerRun(true, process.ExitCode, false, string.Empty);
        }
    }
}

public enum ComponentInstallStage
{
    Consent,
    Download,
    Verify,
    Install,
    Check,
    Done
}

public sealed record ComponentProgress(ComponentInstallStage Stage, double Percent, string Text);

public sealed record ComponentInstallResult
{
    public bool Ok { get; init; }

    public bool Cancelled { get; init; }

    public bool RestartRequired { get; init; }

    public ComponentInstallStage FailedAt { get; init; } = ComponentInstallStage.Done;

    public string Message { get; init; } = string.Empty;

    public ComponentState After { get; init; } = ComponentState.Missing;
}

// One click: consent -> download (pinned URL) -> SHA-256 and size -> Authenticode signature with the pinned
// publisher -> the official installer with one UAC prompt -> detect again. The verified file stays locked against
// writes and deletion from the hash check until the installer has ended, so what runs is what was checked.
public static class ComponentInstaller
{
    public static async Task<ComponentInstallResult> InstallAsync(ComponentSpec spec, bool consent, IProgress<ComponentProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (!consent)
        {
            return new ComponentInstallResult
            {
                FailedAt = ComponentInstallStage.Consent,
                Message = "Read what " + spec.Name + " is and its license (" + spec.LicenseName + "), then tick \"I agree\". JoyPro downloads and installs nothing without it."
            };
        }

        string folder = ComponentServices.DownloadFolder();
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, spec.FileName);
        string part = target + ".part";

        // 1. Download (a file already downloaded and still matching the checksum is used again).
        if (!File.Exists(target) || new FileInfo(target).Length != spec.SizeBytes || !string.Equals(Sha256Of(target), spec.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(target);
            progress?.Report(new ComponentProgress(ComponentInstallStage.Download, 0, T("Downloading {0} {1} ({2}) from {3}...", spec.Name, spec.Version, spec.SizeText, new Uri(spec.DownloadUrl).Host)));
            try
            {
                // Synchronous: the caller's own IProgress (the card's) moves it to the UI thread, in order.
                InlineProgress<(long Done, long Total)> bytes = new(p =>
                {
                    long total = p.Total > 0 ? p.Total : spec.SizeBytes;
                    double percent = total > 0 ? Math.Min(100.0, p.Done * 100.0 / total) : 0;
                    progress?.Report(new ComponentProgress(ComponentInstallStage.Download, percent,
                        T("Downloading {0}: {1} of {2}", spec.Name, ComponentSpec.FormatSize(p.Done), ComponentSpec.FormatSize(total))));
                });
                await ComponentServices.Downloader.DownloadAsync(spec.DownloadUrl, part, bytes, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryDelete(part);
                return new ComponentInstallResult { Cancelled = true, FailedAt = ComponentInstallStage.Download, Message = T("Download cancelled; nothing was installed.") };
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                TryDelete(part);
                return new ComponentInstallResult { FailedAt = ComponentInstallStage.Download, Message = "Download of " + spec.Name + " failed: " + ex.Message + " Check the internet connection and try again." };
            }
            File.Move(part, target, true);
        }

        // 2. Verify and keep the file locked against changes while the installer runs.
        progress?.Report(new ComponentProgress(ComponentInstallStage.Verify, 100, T("Checking {0} (SHA-256 and the publisher's signature)...", spec.FileName)));
        FileStream locked;
        try
        {
            locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ComponentInstallResult { FailedAt = ComponentInstallStage.Verify, Message = "The downloaded file cannot be opened: " + ex.Message };
        }
        using (locked)
        {
            string problem = Verify(spec, locked, target);
            if (problem.Length > 0)
            {
                locked.Dispose();
                TryDelete(target);
                return new ComponentInstallResult { FailedAt = ComponentInstallStage.Verify, Message = problem + " The file was deleted and nothing was installed." };
            }

            // 3. The official installer, one UAC prompt.
            progress?.Report(new ComponentProgress(ComponentInstallStage.Install, 100,
                T("Installing {0}: confirm Windows' prompt (\"Do you want to allow this app to make changes?\").{1}", spec.Name, (spec.IsDriver ? " Windows may also ask whether to install the driver from " + spec.SignerOrganization + ": choose Install." : string.Empty))));
            InstallerRun run;
            try
            {
                run = await ComponentServices.Runner.RunElevatedAsync(target, spec.InstallerArguments, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new ComponentInstallResult { Cancelled = true, FailedAt = ComponentInstallStage.Install, Message = T("Stopped waiting for the {0} setup; check its window.", spec.Name) };
            }
            locked.Dispose();
            return Finish(spec, run, target, progress);
        }
    }

    // Size, SHA-256 (read from the locked stream) and the Authenticode signature with the pinned organization.
    private static string Verify(ComponentSpec spec, FileStream stream, string path)
    {
        if (stream.Length != spec.SizeBytes)
            return T("The download of {0} has {1} bytes, expected {2}.", spec.Name, stream.Length, spec.SizeBytes);
        stream.Position = 0;
        string sha;
        using (SHA256 hash = SHA256.Create())
            sha = Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant();
        if (!string.Equals(sha, spec.Sha256, StringComparison.OrdinalIgnoreCase))
            return T("The download of {0} is not the expected file (SHA-256 {1}, expected {2}).", spec.Name, sha, spec.Sha256);
        SignatureCheck signature = ComponentServices.SignatureVerifier.Verify(path);
        if (!signature.Valid)
            return signature.Problem.Length > 0 ? signature.Problem : T("The download of {0} has no valid signature.", spec.Name);
        if (!string.Equals(signature.Organization, spec.SignerOrganization, StringComparison.Ordinal))
            return T("The download of {0} is signed by \"{1}\", expected \"{2}\".", spec.Name, signature.Organization, spec.SignerOrganization);
        return string.Empty;
    }

    private static ComponentInstallResult Finish(ComponentSpec spec, InstallerRun run, string installer, IProgress<ComponentProgress>? progress)
    {
        if (run.Declined)
            return new ComponentInstallResult { Cancelled = true, FailedAt = ComponentInstallStage.Install, Message = T("Windows' administrator prompt was declined: {0} was not installed. Press Install again when you are ready.", spec.Name) };
        if (!run.Started)
            return new ComponentInstallResult { FailedAt = ComponentInstallStage.Install, Message = run.Problem };

        ExitMeaning meaning = Interpret(spec.Kind, run.ExitCode);
        progress?.Report(new ComponentProgress(ComponentInstallStage.Check, 100, T("Checking {0} again...", spec.Name)));
        ComponentState after = ComponentServices.Probe.Probe(spec);
        if (meaning == ExitMeaning.Cancelled)
            return new ComponentInstallResult { Cancelled = true, FailedAt = ComponentInstallStage.Install, After = after, Message = T("{0} setup was cancelled; nothing changed.", spec.Name) };
        if (meaning == ExitMeaning.Busy)
            return new ComponentInstallResult { FailedAt = ComponentInstallStage.Install, After = after, Message = "Another installation is running (Windows Update?). Wait until it is done and press Install again." };
        if (meaning == ExitMeaning.RestartFirst)
            return new ComponentInstallResult { FailedAt = ComponentInstallStage.Install, RestartRequired = true, After = after, Message = spec.Name + " setup needs Windows to restart first. Restart Windows, then press Install again." };
        if (meaning == ExitMeaning.Failed)
            return new ComponentInstallResult { FailedAt = ComponentInstallStage.Install, After = after, Message = spec.Name + " setup ended with code " + run.ExitCode + ". Nothing was changed by JoyPro; the setup's own log is in %TEMP%." };

        TryDelete(installer);
        if (!after.Installed)
        {
            return new ComponentInstallResult
            {
                Ok = false,
                RestartRequired = true,
                FailedAt = ComponentInstallStage.Check,
                After = after,
                Message = T("{0} setup finished, but Windows does not show the driver yet{1}. Restart Windows and press Check again.", spec.Name, (after.Details.Length > 0 ? " (" + after.Details + ")" : string.Empty))
            };
        }
        bool restart = meaning == ExitMeaning.OkRestart || (spec.IsDriver && !after.Running);
        return new ComponentInstallResult
        {
            Ok = true,
            RestartRequired = restart,
            After = after,
            Message = T("{0} {1} is installed.{2}", spec.Name, (after.Version.Length > 0 ? after.Version : spec.Version), (restart ? T(" Restart Windows before you use it: the driver starts after the restart.") : string.Empty))
        };
    }

    private enum ExitMeaning
    {
        Ok,
        OkRestart,
        Cancelled,
        Busy,
        RestartFirst,
        Failed
    }

    private static ExitMeaning Interpret(InstallerKind kind, int code)
    {
        if (code == 0)
            return ExitMeaning.Ok;
        if (kind == InstallerKind.InnoSetup)
        {
            // https://jrsoftware.org/ishelp/topic_setupexitcodes.htm
            return code switch
            {
                2 or 5 => ExitMeaning.Cancelled,
                8 => ExitMeaning.RestartFirst,
                _ => ExitMeaning.Failed
            };
        }
        // msiexec exit codes (passed through by the Advanced Installer EXE).
        return code switch
        {
            3010 or 1641 => ExitMeaning.OkRestart,
            1602 => ExitMeaning.Cancelled,
            1618 => ExitMeaning.Busy,
            _ => ExitMeaning.Failed
        };
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static string Sha256Of(string file)
    {
        try
        {
            using FileStream stream = File.OpenRead(file);
            using SHA256 hash = SHA256.Create();
            return Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
