using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The layout window (LayoutWindowViewModel, docs/setup-wizard.md#layout-window): opened from the Start page's auto setup,
// the Layout page, the setup wizard's review (with its plan) and the tray ("Where is what?"). One window; a new request
// gives it the new source. "Follow the game": DCS's hook (UDP 1992, shared with the HUD) names the aircraft, and every
// 10 s JoyPro looks which game runs (as the HUD does) - only while the window is open and the option is on.
public sealed partial class MainWindowViewModel
{
    private LayoutWindowSettings layoutWindowSettings = new();
    private CancellationTokenSource? dcsListenerCts;
    private DispatcherTimer? layoutFollowTimer;

    public LayoutWindowViewModel? LayoutWindow { get; private set; }

    // The main window opens (or brings up) the layout window when this is raised.
    public event Action? LayoutWindowRequested;

    internal LayoutWindowSettings LayoutWindowSettings => layoutWindowSettings;

    // Open when JoyPro closed: the window opens again with the main window.
    public bool ReopenLayoutWindow => layoutWindowSettings.WasOpen;

    [RelayCommand]
    private void OpenLayoutPreview()
    {
        // A setup plan that is not applied yet shows what the setup would do; otherwise the profile.
        string? plan = SetupWizard != null && !SetupWizard.Applied ? SetupWizard.WritePreviewPlan() : null;
        OpenLayoutPreviewFor(plan);
    }

    // The Layout page and the tray: the profile's binds.
    [RelayCommand]
    private void OpenLayoutWindow() => OpenLayoutPreviewFor(null);

    public void OpenLayoutPreviewFor(string? planFile)
    {
        if (LayoutWindow == null)
        {
            LayoutWindow = new LayoutWindowViewModel(RunSetupWizardEngineAsync, planFile, GameDisplayName, layoutWindowSettings, SaveLayoutWindowSettings,
                LayoutSheetLightStyle, joystickCaptureService);
            LayoutWindow.FollowGameChanged += (_, on) => UpdateLayoutFollow();
            OnPropertyChanged(nameof(LayoutWindow));
        }
        else
        {
            LayoutWindow.UseSource(planFile, LayoutSheetLightStyle);
        }
        LayoutWindowRequested?.Invoke();
        _ = LayoutWindow.LoadAsync();
        UpdateLayoutFollow();
    }

    // The window opened or closed (it calls this): kept for the next start, and the game watch follows.
    internal void LayoutWindowOpenChanged(bool open)
    {
        layoutWindowSettings.WasOpen = open;
        if (!open)
            LayoutWindow?.StopLive();
        SaveLayoutWindowSettings();
        UpdateLayoutFollow();
    }

    internal void SaveLayoutWindowSettings()
    {
        if (!loadingUiSettings)
            SaveUiSettings();
    }

    private string GameDisplayName(string gameId)
    {
        return GameCards.FirstOrDefault(c => string.Equals(c.GameId, gameId, StringComparison.OrdinalIgnoreCase))?.Name ?? gameId;
    }

    // ---- follow the game ----

    private bool LayoutFollows => LayoutWindow != null && layoutWindowSettings.WasOpen && layoutWindowSettings.FollowGame;

    private void UpdateLayoutFollow()
    {
        UpdateDcsListener();
        if (LayoutFollows)
        {
            if (layoutFollowTimer == null)
            {
                layoutFollowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                layoutFollowTimer.Tick += (_, _) => CheckRunningGameForLayout();
                layoutFollowTimer.Start();
            }
            CheckRunningGameForLayout();
        }
        else if (layoutFollowTimer != null)
        {
            layoutFollowTimer.Stop();
            layoutFollowTimer = null;
        }
    }

    private void CheckRunningGameForLayout()
    {
        if (!LayoutFollows)
            return;
        string game = DetectRunningGame();
        if (game.Length == 0)
            return;
        LayoutWindow!.OnRunningGame(game, game == "DCS" ? overlayUnit : game);
    }

    // DCS's hook (UDP 1992): one listener for the HUD and the layout window, while either wants it.
    private void UpdateDcsListener()
    {
        bool wanted = IsOverlayRunning || LayoutFollows;
        if (wanted && dcsListenerCts == null)
        {
            dcsListenerCts = new CancellationTokenSource();
            CancellationToken token = dcsListenerCts.Token;
            _ = Task.Run(() => ListenForDcsAsync(token), CancellationToken.None);
        }
        else if (!wanted && dcsListenerCts != null)
        {
            dcsListenerCts.Cancel();
            dcsListenerCts.Dispose();
            dcsListenerCts = null;
        }
    }
}
