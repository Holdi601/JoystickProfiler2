using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Services;
using JoyPro.Logging;

namespace JoyPro.Gui.ViewModels;

// JoyPro's version (the corner of the window, Settings > About) and the updates (docs/updates.md): at start (when
// "Check on startup" is on) and with "Check for updates" JoyPro asks GitHub Releases of its public repository for the
// latest release. A newer one is offered in a dialog over the page: Update now (download, checksum, install, restart),
// Later, or Skip this version (not offered at start again; a manual check still shows it).
public sealed partial class MainWindowViewModel
{
    public string AppVersionText => T("JoyPro2 {0}", AppInfo.Version);

    // "Built 2026-10-17 from commit a6a72cc, .NET 9.0.x".
    public string AppBuildText => AppInfo.BuildDescription;

    public string AppLicenseText => T("Free software under the GNU GPL 3.0 or later (LICENSE.txt in the JoyPro folder). Third-party parts: THIRD_PARTY_NOTICES.md and the licenses folder.");

    public string ThirdPartyNoticesText => ThirdPartyNotices.Text;

    // The corner of the window: the version, or the version with the update on offer.
    public string VersionCornerText => AvailableUpdateVersion.Length > 0 ? T("v{0}  ·  update {1}", AppInfo.Version, AvailableUpdateVersion) : "v" + AppInfo.Version;

    public string VersionCornerTip => AppVersionText + ". " + AppBuildText + "." +
                                      (AvailableUpdateVersion.Length > 0 ? " " + T("Version {0} is available.", AvailableUpdateVersion) : string.Empty) +
                                      " " + T("Click: About JoyPro and updates.");

    // Raised when JoyPro2 must close (an update is about to be installed).
    public event Action? ShutdownRequested;

    [ObservableProperty]
    private string updateStatus = T("Not checked yet.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionCornerText))]
    [NotifyPropertyChangedFor(nameof(VersionCornerTip))]
    private string availableUpdateVersion = string.Empty;

    [ObservableProperty]
    private bool updateAvailable;

    [ObservableProperty]
    private bool checkUpdatesOnStartup = true;

    partial void OnCheckUpdatesOnStartupChanged(bool value) => SaveUiSettings();

    // Where the updates come from: "owner/repo" of the public repository on GitHub (empty: JoyPro's own).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateSourceText))]
    private string updateRepository = string.Empty;

    partial void OnUpdateRepositoryChanged(string value) => SaveUiSettings();

    public string UpdateSourceText => Environment.GetEnvironmentVariable(UpdateService.FeedVariable) is { Length: > 0 } feed
        ? T("Updates come from {0} (test feed: {1}).", UpdateService.ReleasesPageFor(UpdateRepository), feed)
        : T("Updates come from {0}.", UpdateService.ReleasesPageFor(UpdateRepository));

    // The version the user said "Skip this version" to (its tag); not offered at start again.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkippedUpdate))]
    [NotifyPropertyChangedFor(nameof(SkippedUpdateText))]
    private string skippedUpdateVersion = string.Empty;

    partial void OnSkippedUpdateVersionChanged(string value) => SaveUiSettings();

    public bool HasSkippedUpdate => SkippedUpdateVersion.Length > 0;

    public string SkippedUpdateText => T("You skipped version {0}: JoyPro does not offer it at start (Check for updates still shows it).", SkippedUpdateVersion);

    private ReleaseInfo? availableRelease;

    // Tests: replaces the update source (default: UpdateService for UpdateRepository / JOYPRO_UPDATE_FEED).
    internal Func<UpdateService>? UpdateServiceFactory { get; set; }

    // Tests: replaces starting the copy script (gets the staged folder); JoyPro then asks to close as after a real start.
    internal Action<string>? UpdateInstallerOverride { get; set; }

    private UpdateService CreateUpdateService() =>
        UpdateServiceFactory?.Invoke() ?? new UpdateService(UpdateService.FeedUrlFor(UpdateRepository), UpdateService.AppVersion);

    // ---- The dialog over the page ----

    [ObservableProperty]
    private bool updatePromptVisible;

    [ObservableProperty]
    private string updatePromptTitle = string.Empty;

    [ObservableProperty]
    private string updatePromptMessage = string.Empty;

    // The release notes ("What's new").
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdatePromptNotes))]
    private string updatePromptNotes = string.Empty;

    public bool HasUpdatePromptNotes => UpdatePromptNotes.Length > 0;

    // A newer version on offer: Update now / Later / Skip this version; otherwise only OK.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatePromptIsInfo))]
    private bool updatePromptOffersUpdate;

    public bool UpdatePromptIsInfo => !UpdatePromptOffersUpdate && !UpdatePromptBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatePromptIsInfo))]
    [NotifyPropertyChangedFor(nameof(UpdatePromptCanAnswer))]
    [NotifyPropertyChangedFor(nameof(UpdatePromptProgressUnknown))]
    private bool updatePromptBusy;

    public bool UpdatePromptCanAnswer => !UpdatePromptBusy;

    // ok, info, warn, error: the symbol of the dialog.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatePromptIsOk))]
    [NotifyPropertyChangedFor(nameof(UpdatePromptIsWarn))]
    [NotifyPropertyChangedFor(nameof(UpdatePromptIsError))]
    [NotifyPropertyChangedFor(nameof(UpdatePromptIsNews))]
    [NotifyPropertyChangedFor(nameof(UpdatePromptIsInfoKind))]
    private string updatePromptKind = "info";

    public bool UpdatePromptIsOk => UpdatePromptKind == "ok";

    public bool UpdatePromptIsWarn => UpdatePromptKind == "warn";

    public bool UpdatePromptIsError => UpdatePromptKind == "error";

    public bool UpdatePromptIsNews => UpdatePromptKind == "news";

    public bool UpdatePromptIsInfoKind => UpdatePromptKind == "info";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatePromptProgressUnknown))]
    private double updatePromptProgress;

    // Busy before the download reports how far it is (the check): a moving bar.
    public bool UpdatePromptProgressUnknown => UpdatePromptBusy && UpdatePromptProgress <= 0;

    [ObservableProperty]
    private bool updatePromptShowsProgress;

    [ObservableProperty]
    private bool updatePromptCanOpenPage;

    private bool updateCheckRunning;

    [RelayCommand]
    private Task CheckForUpdatesAsync() => CheckForUpdatesCoreAsync(manual: true);

    // At start only a newer, not skipped version opens the dialog; "Check for updates" always says what it found.
    internal async Task CheckForUpdatesCoreAsync(bool manual)
    {
        if (updateCheckRunning)
            return;
        updateCheckRunning = true;
        try
        {
            UpdateStatus = T("Checking for updates...");
            if (manual)
                ShowUpdatePrompt(T("Checking for updates..."), T("JoyPro asks GitHub for the latest release."), string.Empty, "info", offers: false, busy: true);
            UpdateCheckResult result = await CreateUpdateService().CheckAsync();
            JoyProLog.Info("Update", "Update check (" + (manual ? "manual" : "at start") + "): " + result.Outcome + ": " + result.Message);
            bool skipped = result.Release != null && SkippedUpdateVersion.Length > 0 &&
                           string.Equals(SkippedUpdateVersion, result.Release.Tag, StringComparison.OrdinalIgnoreCase);
            availableRelease = result.Outcome == UpdateCheckOutcome.NewerAvailable ? result.Release : null;
            UpdateAvailable = availableRelease != null;
            // A skipped version is not advertised in the corner after the check at start.
            AvailableUpdateVersion = result.IsNewer && result.Release != null && (manual || !skipped) ? result.Release.Version.ToString() : string.Empty;
            UpdateStatus = result.Message + (skipped ? " " + T("You skipped this version.") : string.Empty);
            if (!manual && (!result.IsNewer || skipped))
                return;
            ShowCheckResult(result, skipped);
        }
        finally
        {
            updateCheckRunning = false;
        }
    }

    private void ShowCheckResult(UpdateCheckResult result, bool skipped)
    {
        ReleaseInfo? release = result.Release;
        switch (result.Outcome)
        {
            case UpdateCheckOutcome.NewerAvailable:
                ShowUpdatePrompt(T("JoyPro2 {0} is available", release!.Version),
                    T("You have {0}. Update now? JoyPro downloads the new version{1}, checks it, closes, installs it and starts again. Your profiles and settings stay.", AppInfo.Version, SizeText(release)) +
                    (skipped ? " " + T("(You skipped this version before.)") : string.Empty),
                    release.Notes, "news", offers: true, busy: false);
                break;
            case UpdateCheckOutcome.NewerWithoutPackage:
                ShowUpdatePrompt(T("JoyPro2 {0} is available", release!.Version), result.Message, release.Notes, "warn", offers: false, busy: false);
                break;
            case UpdateCheckOutcome.UpToDate:
                ShowUpdatePrompt(T("JoyPro2 is up to date"),
                    release != null && release.Version < UpdateService.AppVersion
                        ? T("You have {0}, newer than the latest release.", AppInfo.Version)
                        : T("You have {0}, the latest version.", AppInfo.Version),
                    string.Empty, "ok", offers: false, busy: false);
                break;
            case UpdateCheckOutcome.NoRelease:
                ShowUpdatePrompt(T("No release found"), result.Message + " " + UpdateSourceText, string.Empty, "info", offers: false, busy: false);
                break;
            default:
                ShowUpdatePrompt(T("Could not check for updates"), result.Message + " " + T("JoyPro works on as it is; try again later."), string.Empty, "error", offers: false, busy: false);
                break;
        }
        UpdatePromptCanOpenPage = release != null && result.IsNewer;
    }

    internal static string SizeText(ReleaseInfo release)
    {
        if (release.PackageSize <= 0)
            return string.Empty;
        double mb = release.PackageSize / (1024.0 * 1024.0);
        return mb >= 1
            ? " " + T("({0} MB)", mb.ToString("0", System.Globalization.CultureInfo.CurrentCulture))
            : " " + T("({0} KB)", Math.Max(1, release.PackageSize / 1024).ToString(System.Globalization.CultureInfo.CurrentCulture));
    }

    private void ShowUpdatePrompt(string title, string message, string notes, string kind, bool offers, bool busy)
    {
        UpdatePromptTitle = title;
        UpdatePromptMessage = message;
        UpdatePromptNotes = notes.Length > 4000 ? notes[..4000].TrimEnd() + "\n..." : notes;
        UpdatePromptKind = kind;
        UpdatePromptOffersUpdate = offers;
        UpdatePromptBusy = busy;
        UpdatePromptShowsProgress = false;
        UpdatePromptProgress = 0;
        UpdatePromptCanOpenPage = false;
        UpdatePromptVisible = true;
    }

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        ReleaseInfo? release = availableRelease;
        if (release == null)
        {
            UpdateStatus = T("No update to install.");
            UpdatePromptVisible = false;
            return;
        }
        UpdatePromptOffersUpdate = false;
        UpdatePromptCanOpenPage = false;
        UpdatePromptBusy = true;
        UpdatePromptShowsProgress = true;
        UpdatePromptProgress = 0;
        UpdatePromptKind = "news";
        try
        {
            InlineProgress<string> progress = new(text =>
            {
                UpdateStatus = text;
                UpdatePromptMessage = text;
            });
            InlineProgress<double> download = new(value => UpdatePromptProgress = Math.Round(value * 100, 1));
            string staged = await CreateUpdateService().StageAsync(release, progress, download);
            JoyProLog.Info("Update", "Update " + release.Version + " staged in " + staged + "; installing");
            UpdateStatus = T("Installing {0}; JoyPro2 restarts in a moment.", release.Version);
            UpdatePromptTitle = T("Installing JoyPro2 {0}", release.Version);
            UpdatePromptMessage = T("The download is complete and checked. JoyPro closes now, installs the new version and starts again.");
            UpdatePromptProgress = 100;
            if (UpdateInstallerOverride != null)
                UpdateInstallerOverride(staged);
            else
                UpdateService.LaunchInstaller(staged);
            ShutdownRequested?.Invoke();
        }
        catch (Exception ex)
        {
            JoyProLog.Error("Update", "The update to " + release.Version + " failed", ex);
            UpdateStatus = T("The update failed: {0}", ex.Message);
            UpdatePromptBusy = false;
            UpdatePromptShowsProgress = false;
            UpdatePromptOffersUpdate = false;
            UpdatePromptKind = "error";
            UpdatePromptTitle = T("The update did not work");
            UpdatePromptNotes = string.Empty;
            UpdatePromptMessage = (ex is UpdateVerificationException ? ex.Message : T("The download failed: {0}", ex.Message)) + " " +
                                  T("JoyPro {0} stays as it is. Try again later, or download the release from its page.", AppInfo.Version);
            UpdatePromptCanOpenPage = true;
        }
    }

    // The Settings button: the same dialog, straight to installing.
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (availableRelease == null)
        {
            UpdateStatus = T("No update to install.");
            return;
        }
        ShowCheckResult(new UpdateCheckResult(UpdateCheckOutcome.NewerAvailable, availableRelease, string.Empty), skipped: false);
        await UpdateNowAsync();
    }

    [RelayCommand]
    private void UpdateLater()
    {
        UpdatePromptVisible = false;
        if (availableRelease != null)
            StatusText = T("JoyPro2 {0} is available; Settings > About JoyPro and updates installs it.", availableRelease.Version);
    }

    [RelayCommand]
    private void SkipUpdateVersion()
    {
        if (availableRelease != null)
        {
            SkippedUpdateVersion = availableRelease.Tag;
            StatusText = T("JoyPro does not offer {0} at start any more.", availableRelease.Version);
        }
        UpdatePromptVisible = false;
    }

    [RelayCommand]
    private void CloseUpdatePrompt()
    {
        if (!UpdatePromptBusy)
            UpdatePromptVisible = false;
    }

    [RelayCommand]
    private void ClearSkippedUpdate() => SkippedUpdateVersion = string.Empty;

    [RelayCommand]
    private void OpenReleasePage()
    {
        string url = availableRelease?.PageUrl is { Length: > 0 } page ? page : UpdateService.ReleasesPageFor(UpdateRepository);
        OpenExternal(url);
    }

    [RelayCommand]
    private void OpenProjectPage() => OpenExternal("https://github.com/" + UpdateService.NormalizeRepository(UpdateRepository));

    // The version in the corner: Settings > About JoyPro and updates.
    [RelayCommand]
    private void OpenAbout() => OpenSettingsSection("about");

    // Reports at once on the caller's thread (the download runs on the UI thread's context), so a late report can never
    // overwrite the state that follows it.
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
