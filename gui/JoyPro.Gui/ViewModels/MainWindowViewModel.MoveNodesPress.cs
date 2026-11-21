using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Logging;

namespace JoyPro.Gui.ViewModels;

// Move to... by pressing the target (2.7.1 tester report: "it would be really useful just to be able to press the button
// you want them to move to rather than typing them out"): the Binds page's record (same capture, same options) reads the
// next button / hat (or axis for axis actions); the relations of the same kind bound to it fill the Existing relation
// list (the one without modifier first) and the first is picked. Typing in the search shows every relation again.
public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveNodesRecordLabel))]
    private bool isMoveNodesRecording;

    // What the press found (or that nothing of this kind is on that input).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoveNodesPressText))]
    private string moveNodesPressText = string.Empty;

    public bool HasMoveNodesPressText => MoveNodesPressText.Length > 0;

    public string MoveNodesRecordLabel => IsMoveNodesRecording
        ? (moveNodesAreAxis ? T("Move the new axis now.") : T("Press the new button now."))
        : (moveNodesAreAxis ? T("Move the axis they go to") : T("Press the button they go to"));

    private CancellationTokenSource? moveNodesRecordCts;

    // The relations on the pressed input: the Existing relation list shows only these (null: all, by the search).
    private List<string>? moveNodesPressed;

    private bool applyingMoveNodesPress;

    partial void OnIsMoveNodesOpenChanged(bool value)
    {
        if (!value)
            moveNodesRecordCts?.Cancel();
    }

    // Opening the panel: no press yet.
    private void ResetMoveNodesPress()
    {
        moveNodesRecordCts?.Cancel();
        moveNodesPressed = null;
        MoveNodesPressText = string.Empty;
        OnPropertyChanged(nameof(MoveNodesRecordLabel));
    }

    [RelayCommand]
    private async Task RecordMoveNodesTargetAsync()
    {
        if (IsMoveNodesRecording)
        {
            moveNodesRecordCts?.Cancel();
            return;
        }
        int timeoutMs = ParseOrDefault(RecordTimeoutMs, 6000, 500, 120000);
        JoystickCaptureService.CaptureOptions options = new()
        {
            AxisMode = moveNodesAreAxis,
            IgnoreAxesOf = AxisIgnorePredicate(),
            TimeoutMs = timeoutMs,
            WarmupMs = ParseOrDefault(RecordWarmupMs, 300, 0, 5000),
            AxisThresholdPercent = ParseOrDefault(RecordAxisThresholdPercent, 30, 1, 100),
            RecordReleaseOfHeldButtons = RecordReleaseOfHeldButtons
        };
        moveNodesRecordCts?.Dispose();
        moveNodesRecordCts = new CancellationTokenSource();
        try
        {
            IsMoveNodesRecording = true;
            MoveNodesPressText = string.Empty;
            JoystickCaptureService.CaptureResult? capture = await joystickCaptureService.CaptureNextAsync(options, moveNodesRecordCts.Token);
            if (capture == null)
            {
                MoveNodesPressText = T("No button or hat pressed within {0} s.", (timeoutMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture));
                return;
            }
            JoyProLog.Info("MoveNodes", "Captured " + capture.Device + " :: " + capture.Input);
            ApplyMoveNodesPress(capture.Device, capture.Input);
        }
        catch (OperationCanceledException)
        {
            if (IsMoveNodesOpen)
                MoveNodesPressText = T("Recording canceled.");
        }
        finally
        {
            IsMoveNodesRecording = false;
            moveNodesRecordCts?.Dispose();
            moveNodesRecordCts = null;
        }
    }

    // The relations of the moved actions' kind bound to that input (not the sources) become the list; the first is picked.
    internal void ApplyMoveNodesPress(string device, string input)
    {
        string label = DeviceMarkerText.ShortLabel(input);
        string deviceName = DisplayText.ShortDevice(device);
        List<string> found = RelationsOnInput(device, input)
            .Select(r => r.Relation)
            .Where(r => allRelations.Any(a => a.IsAxis == moveNodesAreAxis && string.Equals(a.Name, r, StringComparison.OrdinalIgnoreCase)))
            .Where(r => !moveNodesSourceRelations.Contains(r, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (found.Count == 0)
        {
            MoveNodesPressText = T("{0} on {1}: no relation of this kind is bound there. Pick one below or move them to a new relation.", label, deviceName);
            return;
        }
        applyingMoveNodesPress = true;
        try
        {
            IsMoveNodesToExisting = true;
            MoveNodesSearch = string.Empty;
            moveNodesPressed = found;
            RebuildMoveNodesTargets();
            SelectedMoveNodesTarget = MoveNodesTargets.FirstOrDefault(t => string.Equals(t, found[0], StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            applyingMoveNodesPress = false;
        }
        MoveNodesStatus = string.Empty;
        MoveNodesPressText = T("{0} on {1}: {2}", label, deviceName, string.Join(", ", found));
    }

    // Typing in the search: every relation again.
    private void ForgetMoveNodesPressOnSearch()
    {
        if (applyingMoveNodesPress || moveNodesPressed == null)
            return;
        moveNodesPressed = null;
        MoveNodesPressText = string.Empty;
    }
}
