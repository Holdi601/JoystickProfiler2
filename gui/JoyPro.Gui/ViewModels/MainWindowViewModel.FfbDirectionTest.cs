using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Ffb.Core.Host;
using JoyPro.Gui.Services;
using JoyPro.Logging;

namespace JoyPro.Gui.ViewModels;

// One answer button of the direction test (label, symbol, the answer it gives).
public sealed record FfbWizardChoiceItem(FfbWizardAnswer Answer, string Label, string Glyph);

// The "Direction test" of the telemetry force feedback page: FfbDirectionWizard (gui/JoyPro.Ffb.Core) drives a
// "JoyPro.Ffb test" process of its own; every step is announced and waits for "Ready", the answers set the device's
// invert X / Y. Only while JoyPro.Ffb does not run for a game (the test needs the stick to itself).
public sealed partial class MainWindowViewModel
{
    private FfbDirectionWizard? ffbWizard;
    private IFfbTestOutput? ffbWizardOutput;

    // Test seam: the output the wizard drives (the tests pass a fake; the app starts JoyPro.Ffb test).
    internal Func<string, IFfbTestOutput>? FfbTestOutputFactory { get; set; }

    internal Func<TimeSpan, System.Threading.CancellationToken, Task>? FfbWizardDelay { get; set; }

    public ObservableCollection<FfbWizardChoiceItem> FfbWizardChoices { get; } = [];

    [ObservableProperty]
    private bool ffbWizardOpen;

    [ObservableProperty]
    private string ffbWizardTitle = string.Empty;

    [ObservableProperty]
    private string ffbWizardText = string.Empty;

    [ObservableProperty]
    private string ffbWizardGlyph = string.Empty;

    [ObservableProperty]
    private bool ffbWizardCanReady;

    [ObservableProperty]
    private bool ffbWizardBusy;

    [ObservableProperty]
    private bool ffbWizardFinished;

    [ObservableProperty]
    private string ffbWizardReadyLabel = "Ready";

    // "Start" (intro) and "Ready" are two buttons with fixed text: a button whose text changes kept the width of the
    // old text and cut "Ready" to "Read" (hardware test, 2026-10-11).
    [ObservableProperty]
    private bool ffbWizardIsIntro;

    [ObservableProperty]
    private bool ffbWizardCanRetry;

    [ObservableProperty]
    private bool ffbWizardCanStartOver;

    private FfbWizardStep? ffbWizardLoggedStep;

    internal FfbDirectionWizard? FfbWizard => ffbWizard;

    private IFfbTestOutput CreateFfbTestOutput(string device) =>
        new FfbTestProcessOutput(FfbHostLauncher, FfbTelemetryExePath, device, FfbTelemetryGuiPid);

    [RelayCommand]
    private void StartFfbDirectionTest()
    {
        if (ffbHost != null)
        {
            FfbTelemetryMessage = T("The direction test needs the stick to itself: close the game (JoyPro's force feedback for it stops then) and start the test again.");
            return;
        }
        CloseFfbWizardOutput();
        string device = SelectedFfbTelemetryDevice?.InstanceGuid ?? string.Empty;
        ffbWizardOutput = (FfbTestOutputFactory ?? CreateFfbTestOutput)(device);
        ffbWizard = new FfbDirectionWizard(ffbWizardOutput, FfbTelemetryInvertX, FfbTelemetryInvertY, FfbTelemetryIsVpforce, FfbWizardDelay);
        ffbWizard.Changed += () => FfbTelemetryDispatch(UpdateFfbWizard);
        FfbWizardOpen = true;
        UpdateFfbWizard();
    }

    [RelayCommand]
    private async Task FfbWizardReadyAsync()
    {
        if (ffbWizard is not { WaitingForReady: true } wizard)
            return;
        JoyProLog.Info("FfbWizard", "Ready clicked in " + wizard.Step);
        await wizard.ReadyAsync();
        FinishFfbWizardIfDone();
    }

    [RelayCommand]
    private async Task FfbWizardAnswerAsync(FfbWizardChoiceItem? choice)
    {
        if (ffbWizard == null || choice == null)
            return;
        JoyProLog.Info("FfbWizard", "Answer in " + ffbWizard.Step + ": " + choice.Answer +
                                    (ffbWizard.TrimMeasured is { } moved ? " (measured " + moved.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")" : string.Empty));
        await ffbWizard.AnswerAsync(choice.Answer);
        FinishFfbWizardIfDone();
    }

    // Repeats the push / spring / trim step whose question is shown.
    [RelayCommand]
    private async Task FfbWizardRetryAsync()
    {
        if (ffbWizard is not { CanRetry: true } wizard)
            return;
        JoyProLog.Info("FfbWizard", "Retry in " + wizard.Step);
        await wizard.RetryAsync();
    }

    // Back to the first step (forces stop; after the end of a test a fresh test process is started).
    [RelayCommand]
    private async Task FfbWizardStartOverAsync()
    {
        JoyProLog.Info("FfbWizard", "Start over" + (ffbWizard != null ? " from " + ffbWizard.Step : string.Empty));
        if (ffbWizard == null || ffbWizard.Finished || ffbWizardOutput == null)
        {
            StartFfbDirectionTest();
            return;
        }
        await ffbWizard.StartOverAsync();
    }

    [RelayCommand]
    private async Task FfbWizardCancelAsync()
    {
        JoyProLog.Info("FfbWizard", "Cancel" + (ffbWizard != null ? " in " + ffbWizard.Step : string.Empty));
        if (ffbWizard != null)
            await ffbWizard.CancelAsync();
        CloseFfbWizardOutput();
    }

    [RelayCommand]
    private async Task FfbWizardCloseAsync()
    {
        JoyProLog.Info("FfbWizard", "Close" + (ffbWizard != null ? " in " + ffbWizard.Step : string.Empty));
        if (ffbWizard is { Finished: false })
            await ffbWizard.CancelAsync();
        CloseFfbWizardOutput();
        ffbWizard = null;
        FfbWizardOpen = false;
    }

    // Done: the found directions become the device's (and the global) invert X / Y; the stick is released.
    private void FinishFfbWizardIfDone()
    {
        if (ffbWizard is not { Finished: true } wizard)
            return;
        if (wizard.Step == FfbWizardStep.Done)
        {
            FfbTelemetryInvertX = wizard.InvertX;
            FfbTelemetryInvertY = wizard.InvertY;
            FfbTelemetryMessage = T("Direction test done: invert X {0}, invert Y {1}{2}.", (wizard.InvertX ? "on" : "off"), (wizard.InvertY ? "on" : "off"), (SelectedFfbTelemetryDevice != null ? T(" for {0}", SelectedFfbTelemetryDevice.Name) : string.Empty));
        }
        CloseFfbWizardOutput();
    }

    private void CloseFfbWizardOutput()
    {
        IFfbTestOutput? output = ffbWizardOutput;
        ffbWizardOutput = null;
        if (output is IDisposable disposable)
        {
            // Quits the test process (it stops every force and releases the stick on its way out).
            _ = Task.Run(disposable.Dispose);
        }
    }

    private void UpdateFfbWizard()
    {
        FfbDirectionWizard? wizard = ffbWizard;
        if (wizard == null)
            return;
        if (wizard.Step != ffbWizardLoggedStep)
        {
            ffbWizardLoggedStep = wizard.Step;
            JoyProLog.Info("FfbWizard", "Step " + wizard.Step + ": " + wizard.Title +
                                        (wizard.Finished ? " - " + wizard.Text : string.Empty));
        }
        FfbWizardTitle = wizard.Title;
        FfbWizardText = wizard.Text;
        FfbWizardGlyph = FfbWizardGlyphFor(wizard.Icon);
        FfbWizardCanReady = wizard.WaitingForReady;
        FfbWizardIsIntro = wizard.Step == FfbWizardStep.Intro;
        FfbWizardReadyLabel = FfbWizardIsIntro ? T("Start") : T("Ready");
        FfbWizardCanRetry = wizard.CanRetry;
        FfbWizardCanStartOver = wizard.CanStartOver;
        FfbWizardBusy = wizard.Busy;
        FfbWizardFinished = wizard.Finished;
        FfbWizardChoices.Clear();
        foreach (FfbWizardChoice choice in wizard.Choices)
            FfbWizardChoices.Add(new FfbWizardChoiceItem(choice.Answer, choice.Label, FfbWizardGlyphFor(choice.Icon)));
    }

    internal static string FfbWizardGlyphFor(string icon) => icon switch
    {
        FfbWizardIcons.Right => "→",
        FfbWizardIcons.Left => "←",
        FfbWizardIcons.Forward => "↑",
        FfbWizardIcons.Back => "↓",
        FfbWizardIcons.Nothing => "∅",
        FfbWizardIcons.Spring => "↺",
        FfbWizardIcons.Trim => "⇕",
        FfbWizardIcons.Yes => "✓",
        FfbWizardIcons.No => "✗",
        FfbWizardIcons.Done => "✔",
        FfbWizardIcons.Warning => "⚠",
        FfbWizardIcons.Ready => "▶",
        _ => "ℹ"
    };
}
