using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Services;
using JoyPro.Gui.Services.Components;

namespace JoyPro.Gui.Models;

// Shared by all component cards: one installer at a time (Windows runs one setup at a time anyway).
public sealed class ComponentInstallGate
{
    public ComponentItem? Running { get; set; }
}

// One optional component on a Components card (Settings, Virtual Joystick page): what it is, why JoyPro needs it,
// status, license with an "I agree" tick, one-click install with progress, and links to the project and to Windows'
// apps settings (repair, uninstall). docs/components.md.
public sealed partial class ComponentItem : ObservableObject
{
    public const string AppsSettingsUri = "ms-settings:appsfeatures";

    private readonly ComponentInstallGate gate;
    private readonly Action<ComponentItem, ComponentInstallResult>? onInstalled;
    private CancellationTokenSource? cancel;

    public ComponentItem(ComponentSpec spec, ComponentInstallGate gate, Action<ComponentItem, ComponentInstallResult>? onInstalled = null)
    {
        Spec = spec;
        this.gate = gate;
        this.onInstalled = onInstalled;
    }

    public ComponentSpec Spec { get; }

    public string Id => Spec.Id;

    public string Name => Spec.Name;

    public string WhatItIs => Spec.WhatItIs;

    public string WhyJoyProNeedsIt => Spec.WhyJoyProNeedsIt;

    public string UsedBy => T("Used by: {0}", Spec.UsedBy);

    public string SourceText => T("Version {0}, {1}, the official installer from {2} ({3}).", Spec.Version, Spec.SizeText, Spec.ProjectUrl.Replace("https://", string.Empty, StringComparison.Ordinal), Spec.Author);

    public string VerificationText => T("Before it runs, JoyPro checks the file's SHA-256 checksum and its digital signature by \"{0}\". Not part of JoyPro: downloaded only when you click Install.", Spec.SignerOrganization);

    public string LicenseHeader => T("License: {0} (read it before you install)", Spec.LicenseName);

    public string LicenseText => Spec.LicenseText;

    public string ConsentText => T("I have read what {0} does and its license, and I want JoyPro to download and install it.", Spec.Name);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk), nameof(IsMissing), nameof(IsOutdated), nameof(CanOffer), nameof(InstallButtonText), nameof(CanInstall), nameof(StatusText))]
    private ComponentState state = ComponentState.Missing;

    [ObservableProperty]
    private bool checkedOnce;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool consent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(IsIdle))]
    private bool isBusy;

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private string message = string.Empty;

    [ObservableProperty]
    private bool restartRequired;

    public bool IsIdle => !IsBusy;

    public bool IsOk => State.Installed && (!Spec.IsDriver || State.Running);

    public bool IsMissing => !State.Installed;

    // Older than the version JoyPro offers.
    public bool IsOutdated => State.Installed && ComponentVersion.Compare(State.Version, Spec.Version) < 0;

    // Install (missing) or update (older); never a downgrade or a reinstall over the same version.
    public bool CanOffer => !State.Installed || IsOutdated;

    public bool CanInstall => CanOffer && Consent && !IsBusy;

    public string InstallButtonText => IsOutdated ? T("Update to {0}", Spec.Version) : T("Install {0}", Spec.Name);

    public string StatusText
    {
        get
        {
            if (!CheckedOnce)
                return T("Not checked yet.");
            ComponentState s = State;
            if (!s.Installed)
                return T("Not installed{0}.", (s.Details.Length > 0 ? " (" + s.Details + ")" : string.Empty));
            string version = s.Version.Length > 0 ? " " + s.Version : string.Empty;
            if (Spec.IsDriver && !s.Running)
                return T("Installed{0}, but the driver is not running: restart Windows.", version);
            if (IsOutdated)
                return T("Installed{0}; JoyPro offers {1}.", version, Spec.Version);
            if (ComponentVersion.Compare(s.Version, Spec.Version) > 0)
                return T("Installed{0} (newer than the version JoyPro was tested with, {1}).", version, Spec.Version);
            return T("Installed{0} and running.", version);
        }
    }

    partial void OnStateChanged(ComponentState value) => OnPropertyChanged(nameof(StatusText));

    partial void OnCheckedOnceChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    public void Refresh()
    {
        State = ComponentServices.Probe.Probe(Spec);
        CheckedOnce = true;
        OnPropertyChanged(nameof(IsOutdated));
        OnPropertyChanged(nameof(StatusText));
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsBusy)
            return;
        if (!CanOffer)
        {
            Message = T("{0} is already installed. To repair or remove it, use \"Windows apps\".", Spec.Name);
            return;
        }
        if (gate.Running != null && gate.Running != this)
        {
            Message = T("{0} is being installed; wait until it is done.", gate.Running.Name);
            return;
        }
        gate.Running = this;
        IsBusy = true;
        RestartRequired = false;
        ProgressPercent = 0;
        cancel = new CancellationTokenSource();
        try
        {
            Progress<ComponentProgress> progress = new(p =>
            {
                ProgressPercent = p.Percent;
                Message = p.Text;
            });
            ComponentInstallResult result = await ComponentInstaller.InstallAsync(Spec, Consent, progress, cancel.Token);
            State = result.After.Installed || result.Ok ? result.After : ComponentServices.Probe.Probe(Spec);
            CheckedOnce = true;
            OnPropertyChanged(nameof(IsOutdated));
            OnPropertyChanged(nameof(StatusText));
            RestartRequired = result.RestartRequired;
            Message = result.Message;
            if (result.Ok)
                Consent = false;
            onInstalled?.Invoke(this, result);
        }
        finally
        {
            cancel.Dispose();
            cancel = null;
            IsBusy = false;
            gate.Running = null;
        }
    }

    // Stops a download (an installer that already runs is not killed; its own window has Cancel).
    [RelayCommand]
    private void Cancel() => cancel?.Cancel();

    [RelayCommand]
    private void OpenProject() => Open(Spec.ReleasePageUrl);

    [RelayCommand]
    private void OpenLicense() => Open(Spec.LicenseUrl);

    // Repair or uninstall: Windows' own apps settings (each component's uninstaller is listed there).
    [RelayCommand]
    private void OpenAppsSettings() => Open(AppsSettingsUri);

    private void Open(string target)
    {
        try
        {
            ExternalProcess.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Message = T("Open {0} yourself: {1}", target, ex.Message);
        }
    }
}
