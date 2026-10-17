using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Services.Components;
using Microsoft.Win32;

namespace JoyPro.Gui.Services;

public sealed class RequirementStatus
{
    public string Name { get; init; } = string.Empty;
    public bool Ok { get; init; }
    public string Details { get; init; } = string.Empty;
    // How to install it: a bundled installer (relative to the engine folder) or a Microsoft download.
    public string BundledInstaller { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string InstallerArguments { get; init; } = string.Empty;

    public string Display => (Ok ? T("OK      ") : "MISSING ") + Name + (Details.Length > 0 ? " - " + Details : string.Empty);
}

// What the engine needs on the PC: .NET Framework 4.8 (DirectInput is part of Windows; the Lua runtime for DCS comes
// from the DCS install; the GUI ships self-contained). A missing framework is installed on request from Microsoft's
// download after its Authenticode signature (Microsoft) was verified. Download, signature check and the elevated run go
// through ComponentServices (docs/components.md), which the tests replace.
[SupportedOSPlatform("windows")]
public static class RequirementsChecker
{
    public const string MicrosoftOrganization = "Microsoft Corporation";

    public static List<RequirementStatus> Check(string engineFolder)
    {
        List<RequirementStatus> list = [];

        int release = 0;
        using (RegistryKey? key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
        {
            if (key?.GetValue("Release") is int value)
                release = value;
        }
        list.Add(new RequirementStatus
        {
            Name = ".NET Framework 4.8",
            Ok = release >= 528040,
            Details = release >= 528040 ? "release " + release : "needed by the JoyPro engine",
            DownloadUrl = "https://go.microsoft.com/fwlink/?linkid=2088631",
            InstallerArguments = "/passive /norestart"
        });

        bool engine = File.Exists(Path.Combine(engineFolder, "engine.exe"));
        list.Add(new RequirementStatus
        {
            Name = "JoyPro engine files",
            Ok = engine,
            Details = engine ? engineFolder : "engine.exe is missing in " + engineFolder + "; reinstall JoyPro2"
        });
        return list;
    }

    // Runs one installer and waits for it; returns an error text or empty on success.
    public static async Task<string> InstallAsync(RequirementStatus requirement, string engineFolder, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        string installer;
        string arguments = requirement.InstallerArguments;
        if (requirement.BundledInstaller.Length > 0)
        {
            string msi = Path.Combine(engineFolder, requirement.BundledInstaller);
            if (!File.Exists(msi))
                return T("The bundled installer is missing: {0}", msi);
            installer = "msiexec.exe";
            arguments = "/i \"" + msi + "\" /passive /norestart";
        }
        else
        {
            progress?.Report(T("Downloading {0}...", requirement.Name));
            string folder = ComponentServices.DownloadFolder();
            Directory.CreateDirectory(folder);
            installer = Path.Combine(folder, Path.GetFileName(new Uri(requirement.DownloadUrl).AbsolutePath) is { Length: > 4 } name && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name
                : SafeName(requirement.Name) + ".exe");
            try
            {
                await ComponentServices.Downloader.DownloadAsync(requirement.DownloadUrl, installer, null, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                return T("Download of {0} failed: {1}", requirement.Name, ex.Message);
            }

            string signature = VerifyMicrosoftSignature(installer);
            if (signature.Length > 0)
            {
                TryDelete(installer);
                return signature;
            }
        }

        progress?.Report(T("Installing {0} (confirm the Windows prompt)...", requirement.Name));
        InstallerRun run = await ComponentServices.Runner.RunElevatedAsync(installer, arguments, cancellationToken);
        if (run.Declined)
            return T("Windows' administrator prompt was declined: {0} was not installed.", requirement.Name);
        if (!run.Started)
            return run.Problem.Length > 0 ? run.Problem : T("The installer did not start.");
        // 3010 = success, restart required.
        return run.ExitCode == 0 || run.ExitCode == 3010 ? string.Empty : T("{0} setup ended with code {1}.", requirement.Name, run.ExitCode);
    }

    private static string SafeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Replace(' ', '_');
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Valid Authenticode signature (WinVerifyTrust) by Microsoft Corporation, or the reason why not.
    public static string VerifyMicrosoftSignature(string file)
    {
        SignatureCheck check = ComponentServices.SignatureVerifier.Verify(file);
        if (!check.Valid)
            return T("The downloaded file {0} has no valid signature; it was not run.{1}", Path.GetFileName(file), (check.Problem.Length > 0 ? " (" + check.Problem + ")" : string.Empty));
        if (string.Equals(check.Organization, MicrosoftOrganization, StringComparison.Ordinal))
            return string.Empty;
        return T("The downloaded file is not signed by Microsoft ({0}); it was not run.", check.Subject);
    }
}
