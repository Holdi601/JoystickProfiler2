using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Logging;

namespace JoyPro.Gui.ViewModels;

// Self-updater, crash notice, problem reporter and the requirements check, all started from the window's Opened event.
public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    private bool hasCrashReports;

    [ObservableProperty]
    private string crashReportNotice = string.Empty;

    [ObservableProperty]
    private string problemDescription = string.Empty;

    [ObservableProperty]
    private bool problemIncludeProfile;

    [ObservableProperty]
    private string problemReportStatus = string.Empty;

    public ObservableCollection<string> RequirementItems { get; } = [];

    [ObservableProperty]
    private bool hasMissingRequirements;

    // Only a missing component with an installer offers "Install Missing" (an engine that does not start has none).
    [ObservableProperty]
    private bool hasInstallableRequirements;

    [ObservableProperty]
    private string requirementsStatus = string.Empty;

    private List<RequirementStatus> requirements = [];

    // Recent engine commands and output for crash and problem reports.
    public string RecentActivityText
    {
        get
        {
            string[] lines = (OutputText ?? string.Empty).Split('\n');
            return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 80)));
        }
    }

    private bool startupChecksStarted;

    // Once per run: the window is opened again after "Close to tray" with the same view model.
    public async Task RunStartupChecksAsync()
    {
        if (startupChecksStarted)
            return;
        startupChecksStarted = true;
        CrashReporter.ContextProvider = () => RecentActivityText;
        CheckRequirements();
        await CheckEngineStartsAsync();
        await RefreshGameInstallsAsync();
        ShowFirstStartSetupIfNeeded();

        JoyProLog.Info("Startup", "Requirements: " + string.Join(" | ", RequirementItems) + (HasMissingRequirements ? " (something is missing)" : string.Empty));
        List<string> crashes = CrashReporter.UnseenReports();
        if (crashes.Count > 0)
            JoyProLog.Info("Startup", "Unseen crash reports: " + string.Join(", ", crashes.Select(Path.GetFileName)));
        HasCrashReports = crashes.Count > 0;
        CrashReportNotice = crashes.Count == 0
            ? string.Empty
            : T("{0} ({1}). Please report it so it can be fixed.", (crashes.Count == 1 ? T("JoyPro2 ran into an error last time") : T("JoyPro2 ran into {0} errors since the last start", crashes.Count)), Path.GetFileName(crashes[0]));

        await CheckDbUpdatesAsync();
        // A DCS update brings DCS's default-bind files back: switch them off again when the user switched that on.
        await AutoApplyDcsDefaultsAsync();
        // The Start page's connected devices (with their pictures).
        await LoadDeviceCardsAsync();
        await StartGameConfigWatcherAsync();
        if (CheckUpdatesOnStartup)
            await CheckForUpdatesCoreAsync(manual: false);
    }

    [RelayCommand]
    private void ReportLastCrash()
    {
        ProblemDescription = T("JoyPro2 ran into an error. What I was doing:\n");
        CurrentStage = UiStage.Settings;
        ProblemReportStatus = T("Describe what you were doing, then press 'Create Report'. The crash details are added automatically.");
    }

    [RelayCommand]
    private void DismissCrashReports()
    {
        CrashReporter.MarkSeen();
        HasCrashReports = false;
    }

    // ---- Problem report and background logs (docs/logging.md) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblemReport))]
    private string lastProblemReportPath = string.Empty;

    private string lastProblemIssueUrl = string.Empty;

    public bool HasProblemReport => LastProblemReportPath.Length > 0;

    // How many days the background logs are kept (shared by the GUI, the engine and JoyPro.Ffb: <Logs>\log-settings.txt).
    [ObservableProperty]
    private decimal logRetentionDays = JoyProLogSettings.Read(AppInfo.LogFolder).RetentionDays;

    public string LogFolderText => T("Logs: {0}", AppInfo.LogFolder);

    partial void OnLogRetentionDaysChanged(decimal value)
    {
        int days = JoyProLog.ClampDays((int)Math.Round(value));
        try
        {
            JoyProLogSettings settings = JoyProLogSettings.Read(AppInfo.LogFolder);
            if (settings.RetentionDays == days)
                return;
            settings.RetentionDays = days;
            settings.Write(AppInfo.LogFolder);
            JoyProLog.Info("Settings", "Logs are kept " + days.ToString(System.Globalization.CultureInfo.InvariantCulture) + " day(s) from now on");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = T("The log setting could not be saved: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CreateProblemReportAsync()
    {
        ProblemReportStatus = T("Collecting information...");
        JoyProLog.Info("Report", "Creating a problem report (profile " + (ProblemIncludeProfile ? "attached" : "not attached") + ")");
        string diagnostics = await BuildDiagnosticsAsync();
        try
        {
            ProblemReport report = ProblemReporter.Create(ProblemDescription, diagnostics, ProfilePath, ProblemIncludeProfile, CrashReporter.AllReports(),
                logFolder: AppInfo.LogFolder, extraFiles: [SessionLogPath]);
            CrashReporter.MarkSeen();
            HasCrashReports = false;
            LastProblemReportPath = report.ZipPath;
            lastProblemIssueUrl = report.IssueUrl;
            ProblemReportStatus = T("Done. The report is saved as {0} in {1} (Explorer shows it). Nothing was sent. To send it: press 'Open GitHub Issue Page', and drag the zip into the issue text box (or attach it to your message to the developer). You can open the zip first to see what is in it.", Path.GetFileName(report.ZipPath), Path.GetDirectoryName(report.ZipPath));
            OpenExternal("explorer.exe", "/select,\"" + report.ZipPath + "\"");
        }
        catch (Exception ex)
        {
            JoyProLog.Error("Report", "The problem report could not be created", ex);
            ProblemReportStatus = T("The report could not be created: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenProblemIssuePage()
    {
        if (lastProblemIssueUrl.Length > 0)
            OpenExternal(lastProblemIssueUrl);
    }

    [RelayCommand]
    private void ShowProblemReport()
    {
        if (LastProblemReportPath.Length > 0)
            OpenExternal("explorer.exe", "/select,\"" + LastProblemReportPath + "\"");
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        string folder = AppInfo.LogFolder;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        OpenExternal(folder);
    }

    private async Task<string> BuildDiagnosticsAsync()
    {
        StringBuilder sb = new();
        sb.AppendLine(T("Install folder: {0}", AppInfo.InstallFolder));
        sb.AppendLine(T("Log folder: {0} (keep {1} days)", AppInfo.LogFolder, JoyProLogSettings.Read(AppInfo.LogFolder).RetentionDays));
        sb.AppendLine(T("Current page: {0}", CurrentStage));
        sb.AppendLine(T("Engine: {0}", ResolveEnginePath()));
        EngineCommandResult? version = await RunEngineAsync(["version"], includeProfile: false, mutatesProfile: false, busyLabel: N("Collecting diagnostics..."));
        sb.AppendLine(T("Engine version: {0}", (version?.StandardOutput.Trim() ?? "(not available)")));
        EngineCommandResult? devices = await RunEngineAsync(["games", "devices"], includeProfile: false, mutatesProfile: false, busyLabel: N("Collecting diagnostics..."));
        sb.AppendLine();
        sb.AppendLine(devices?.StandardOutput.Trim() ?? T("(device list not available)"));
        sb.AppendLine();
        sb.AppendLine(T("Requirements:"));
        foreach (string item in RequirementItems)
            sb.AppendLine("  " + item);
        if (DbUpdateMessages.Count > 0)
        {
            sb.AppendLine(T("DB notices:"));
            foreach (string message in DbUpdateMessages)
                sb.AppendLine("  " + message);
        }
        sb.AppendLine();
        sb.AppendLine(T("Profile: {0}{1}", (string.IsNullOrWhiteSpace(ProfilePath) ? "(none)" : Path.GetFileName(ProfilePath)), (ProblemIncludeProfile ? " (attached)" : " (not attached)")));
        sb.AppendLine();
        sb.AppendLine(T("Recent engine activity:"));
        sb.AppendLine(RecentActivityText);
        return sb.ToString();
    }

    // "Check Again": the components and then whether the engine really starts, as at program start.
    [RelayCommand]
    private async Task CheckRequirementsAsync()
    {
        CheckRequirements();
        await CheckEngineStartsAsync();
    }

    private void CheckRequirements()
    {
        string engine = ResolveEnginePath();
        string engineFolder = string.IsNullOrWhiteSpace(engine) ? AppInfo.InstallFolder : Path.GetDirectoryName(engine) ?? AppInfo.InstallFolder;
        requirements = RequirementsChecker.Check(engineFolder);
        ShowRequirements();
        RequirementsStatus = HasMissingRequirements
            ? T("Some components the JoyPro engine needs are missing. 'Install Missing' installs them (Windows asks for permission).")
            : T("All required components are installed.");
    }

    private void ShowRequirements()
    {
        RequirementItems.Clear();
        foreach (RequirementStatus item in requirements)
            RequirementItems.Add(item.Display);
        HasMissingRequirements = requirements.Any(r => !r.Ok);
        HasInstallableRequirements = requirements.Any(r => !r.Ok && (r.DownloadUrl.Length > 0 || r.BundledInstaller.Length > 0));
    }

    // The engine is a .NET Framework 4.8 program: if it does not start, nothing else works.
    private async Task CheckEngineStartsAsync()
    {
        if (requirements.Any(r => !r.Ok))
            return;
        EngineCommandResult? result = await RunEngineAsync(["version"], includeProfile: false, mutatesProfile: false, busyLabel: N("Checking the engine..."));
        bool ok = result != null && result.ExitCode == 0;
        requirements.Add(new RequirementStatus
        {
            Name = "JoyPro engine starts",
            Ok = ok,
            Details = ok ? "version " + result!.StandardOutput.Trim() : (result?.StandardError.Trim() is { Length: > 0 } error ? error : "the engine did not run; check antivirus blocking or reinstall JoyPro2")
        });
        ShowRequirements();
        if (!ok)
            RequirementsStatus = T("The JoyPro engine does not start. Details below; 'Report a Problem' in Settings helps if it stays like this.");
    }

    [RelayCommand]
    private async Task InstallMissingRequirementsAsync()
    {
        string engine = ResolveEnginePath();
        string engineFolder = string.IsNullOrWhiteSpace(engine) ? AppInfo.InstallFolder : Path.GetDirectoryName(engine) ?? AppInfo.InstallFolder;
        Progress<string> progress = new(text => RequirementsStatus = text);
        List<string> errors = [];
        foreach (RequirementStatus requirement in requirements.Where(r => !r.Ok && (r.DownloadUrl.Length > 0 || r.BundledInstaller.Length > 0)).ToList())
        {
            string error = await RequirementsChecker.InstallAsync(requirement, engineFolder, progress);
            if (error.Length > 0)
                errors.Add(error);
        }
        CheckRequirements();
        await CheckEngineStartsAsync();
        if (errors.Count > 0)
            RequirementsStatus = string.Join(" ", errors);
    }

    private void OpenExternal(string target, string? arguments = null)
    {
        try
        {
            ExternalProcess.Start(new ProcessStartInfo { FileName = target, Arguments = arguments ?? string.Empty, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = T("Could not open {0}: {1}", target, ex.Message);
        }
    }
}
