using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The Cockpits page in the bottom bar (docs/cockpits.md): the cockpit database as a reference. Its own view model
// (CockpitsViewModel); the main window only switches to it, runs "engine cockpits compare" with the open profile and
// keeps the last "Use this layout" for the setup wizard.
public sealed partial class MainWindowViewModel
{
    private CockpitsViewModel? cockpits;

    public CockpitsViewModel Cockpits
    {
        get
        {
            if (cockpits == null)
            {
                cockpits = new CockpitsViewModel { CompareRunner = RunCockpitCompareAsync, PictureBuilder = RunCockpitPicturesAsync };
                cockpits.UseLayoutRequested += (_, request) =>
                {
                    PendingCockpitLayout = request;
                    StatusText = T("Cockpit layout picked: {0} {1} (engine {2}).", request.AircraftName, request.VariantName, string.Join(" ", request.EngineArguments));
                    CockpitLayoutRequested?.Invoke(this, request);
                };
            }
            return cockpits;
        }
    }

    public bool IsCockpitsStage => CurrentStage == UiStage.Cockpits;

    // The last "Use this layout" of the Cockpits page, until the setup wizard takes it (TakePendingCockpitLayout).
    public CockpitLayoutRequest? PendingCockpitLayout { get; private set; }

    // Raised on "Use this layout"; the setup wizard listens.
    public event EventHandler<CockpitLayoutRequest>? CockpitLayoutRequested;

    public CockpitLayoutRequest? TakePendingCockpitLayout()
    {
        CockpitLayoutRequest? request = PendingCockpitLayout;
        PendingCockpitLayout = null;
        return request;
    }

    [RelayCommand]
    private void GoCockpits() => CurrentStage = UiStage.Cockpits;

    // Called when the stage changes (OnCurrentStageChanged).
    private void OnCockpitsStageChanged(bool shown)
    {
        OnPropertyChanged(nameof(IsCockpitsStage));
        if (shown)
            Cockpits.EnsureLoaded();
    }

    // "Build pictures from my games": the engine renders the cockpits of the installed games (with progress, cancelable).
    private async Task<string?> RunCockpitPicturesAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["cockpits", "pictures", "build", "--game", "all"], includeProfile: false, mutatesProfile: false,
            busyLabel: T("Making cockpit pictures from your games..."), forceText: true);
        if (result == null)
            return null;
        if (result.ExitCode != 0 && result.ExitCode != 3)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError) ? "exit code " + result.ExitCode : result.StandardError.Trim());
        return result.StandardOutput;
    }

    private async Task<string?> RunCockpitCompareAsync(string aircraft, string variant)
    {
        if (string.IsNullOrWhiteSpace(ProfilePath))
            return null;
        EngineCommandResult? result = await RunEngineAsync(["cockpits", "compare", aircraft, variant], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Comparing the cockpit with your profile..."), forceText: false, background: true);
        if (result == null)
            return null;
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError) ? "exit code " + result.ExitCode : result.StandardError.Trim());
        return result.StandardOutput;
    }
}
