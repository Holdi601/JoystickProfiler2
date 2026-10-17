using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Logging;

namespace JoyPro.Gui.ViewModels;

// Live input on the pages that show binds and pictures (docs/ui/live-input.md):
// - Binds page, "Follow input": pressing a button (or moving an axis a long way) selects the relation bound to it;
//   pressing it again steps through all relations on that input, the ones without modifiers first. Not while Record
//   runs, a text box has the focus or the picture (Visual Assign) is open.
// - Visual Assign and the Layout page's device pictures: the pressed input's marker, its row in the lists and its
//   line light up while it is held; an axis shows a small live bar.
// One DirectInput monitor feeds both while one of them is on screen; it stops when the page is left, the window is
// minimized or the feature is switched off.
public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? liveInputCts;
    private readonly FollowInputTracker followTracker = new();
    private (string Device, string Input, int Index)? lastFollow;
    private CancellationTokenSource? followToastCts;
    private DispatcherTimer? liveRowsTimer;
    private readonly HashSet<string> liveRowInputs = new(StringComparer.OrdinalIgnoreCase);

    // The live state of the device on the picture (Visual Assign / Layout page); the canvases draw it.
    public LiveInputState WorkspaceLiveInput { get; } = new();

    // Settings > Binds and the Binds page's toggle: on by default.
    [ObservableProperty]
    private bool followInputOnBinds = true;

    // Record: releasing a button that rests pressed counts (off: such buttons never count).
    [ObservableProperty]
    private bool recordReleaseOfHeldButtons;

    // The window says when a text box has the keyboard focus (typing must not jump the list).
    [ObservableProperty]
    private bool isTextInputFocused;

    // "BTN 8 on L-VPC Stick WarBRD: Gear (1/3)", shown for a moment over the bind list.
    [ObservableProperty]
    private string followInputToast = string.Empty;

    public bool HasFollowInputToast => FollowInputToast.Length > 0;

    // The monitor runs (tests).
    internal bool IsLiveInputMonitoring => liveInputCts != null;

    partial void OnFollowInputToastChanged(string value) => OnPropertyChanged(nameof(HasFollowInputToast));

    partial void OnFollowInputOnBindsChanged(bool value)
    {
        if (!loadingUiSettings)
            SaveUiSettings();
        UpdateLiveInputMonitor();
    }

    partial void OnRecordReleaseOfHeldButtonsChanged(bool value)
    {
        if (!loadingUiSettings)
            SaveUiSettings();
    }

    [RelayCommand]
    private void ToggleFollowInput() => FollowInputOnBinds = !FollowInputOnBinds;

    // Visual Assign is open on the Binds page, or the Layout page shows a device picture.
    private bool WorkspacePictureOnScreen => (IsBindsStage && OpenVisualAssignMode) || (IsLayoutStage && IsDevicePicturesSection);

    private bool FollowInputWanted => IsBindsStage && FollowInputOnBinds;

    internal void UpdateLiveInputMonitor()
    {
        bool wanted = !mainWindowMinimized && (FollowInputWanted || WorkspacePictureOnScreen);
        if (!WorkspacePictureOnScreen)
            ClearWorkspaceLiveInput();
        if (!wanted)
        {
            if (liveInputCts == null)
                return;
            liveInputCts.Cancel();
            liveInputCts.Dispose();
            liveInputCts = null;
            followTracker.Reset();
            RestartAxisTuningMonitorIf(shared: true);
            return;
        }
        if (liveInputCts != null)
            return;
        liveInputCts = new CancellationTokenSource();
        CancellationToken token = liveInputCts.Token;
        _ = RunLiveInputMonitorAsync(token);
        RestartAxisTuningMonitorIf(shared: false);
    }

    // The curve's live position switches between its own monitor and this one (never two DirectInput pollers).
    private void RestartAxisTuningMonitorIf(bool shared)
    {
        if (axisTuningMonitorCts == null || axisTuningSharesLiveMonitor != shared)
            return;
        StopAxisTuningMonitor();
        UpdateAxisTuningMonitor();
    }

    private async Task RunLiveInputMonitorAsync(CancellationToken token)
    {
        try
        {
            await joystickCaptureService.MonitorAsync(
                e =>
                {
                    if (!token.IsCancellationRequested)
                        Dispatcher.UIThread.Post(() => OnLiveInput(e));
                },
                s =>
                {
                    if (!token.IsCancellationRequested)
                        Dispatcher.UIThread.Post(() => OnLiveAxes(s));
                },
                token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            JoyProLog.Warn("LiveInput", "Live input monitor stopped: " + ex.Message);
        }
    }

    internal void OnLiveInput(JoystickInputEvent input)
    {
        if (liveInputCts == null)
            return;
        if (WorkspacePictureOnScreen && IsWorkspaceDevice(input.Device) && WorkspaceLiveInput.SetButton(input.Input, input.Down))
            RefreshLiveRows();
        if (!CanFollowInput())
            return;
        string? follow = followTracker.OnButton(input.Device, input.Input, input.Down, input.FirstReport);
        if (follow != null)
            FollowInput(input.Device, follow);
    }

    internal void OnLiveAxes(JoystickAxisSnapshot snapshot)
    {
        if (liveInputCts == null)
            return;
        if (axisTuningSharesLiveMonitor)
            OnAxisTuningAxes(snapshot);
        if (WorkspacePictureOnScreen && IsWorkspaceDevice(snapshot.Device))
        {
            if (WorkspaceLiveInput.SetAxes(snapshot.Axes, snapshot.FirstReport).Count > 0)
                RefreshLiveRows();
        }
        if (!CanFollowInput())
        {
            // Keeps the axis anchors where the axes are, so the first move after typing is not one big jump.
            followTracker.OnAxes(snapshot.Device, snapshot.Axes, firstReport: true);
            return;
        }
        string? follow = followTracker.OnAxes(snapshot.Device, snapshot.Axes, snapshot.FirstReport);
        if (follow != null)
            FollowInput(snapshot.Device, follow);
    }

    private bool CanFollowInput() =>
        FollowInputWanted && !OpenVisualAssignMode && !IsRecordingInput && !IsTextInputFocused;

    // ---- Follow input: select the relation(s) bound to the input ----

    // The relations bound to the input on that device: without modifiers first, then with, each in list order.
    internal List<(string Relation, bool Shifted)> RelationsOnInput(string device, string input)
    {
        string token = (input ?? string.Empty).Trim();
        List<BindListItem> matches = allBinds
            .Where(b => b.HasBind && string.Equals((b.Input ?? string.Empty).Trim(), token, StringComparison.OrdinalIgnoreCase) &&
                        OverlayResolver.SameDevice(b.Device, device))
            .ToList();
        // A profile made on another PC (or before Windows gave the device a new GUID): the same device name.
        if (matches.Count == 0)
        {
            matches = allBinds
                .Where(b => b.HasBind && string.Equals((b.Input ?? string.Empty).Trim(), token, StringComparison.OrdinalIgnoreCase) &&
                            SameDeviceName(b.Device, device))
                .ToList();
        }
        List<string> order = Binds.Select(b => b.RelationName).ToList();
        int Position(string relation)
        {
            int index = order.FindIndex(r => string.Equals(r, relation, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? int.MaxValue : index;
        }
        return matches
            .GroupBy(b => b.RelationName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Relation: g.Key, Shifted: g.All(b => b.HasModifiers)))
            .OrderBy(r => r.Shifted)
            .ThenBy(r => Position(r.Relation))
            .ThenBy(r => r.Relation, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal void FollowInput(string device, string input)
    {
        List<(string Relation, bool Shifted)> relations = RelationsOnInput(device, input);
        string label = DeviceMarkerText.ShortLabel(input);
        string deviceName = DisplayText.ShortDevice(device);
        if (relations.Count == 0)
        {
            lastFollow = (device, input, -1);
            ShowFollowToast(T("{0} on {1}: nothing bound", label, deviceName));
            return;
        }
        int index = lastFollow is { } last && OverlayResolver.SameDevice(last.Device, device) &&
                    string.Equals(last.Input, input, StringComparison.OrdinalIgnoreCase)
            ? (last.Index + 1) % relations.Count
            : 0;
        lastFollow = (device, input, index);
        (string relation, bool shifted) = relations[index];
        BindListItem? row = FindBindRow(relation);
        if (row == null && (BindSearchText.Length > 0 || BindDeviceFilterText.Length > 0))
        {
            // Hidden by the search: show the whole list again.
            BindSearchText = string.Empty;
            BindDeviceFilterText = string.Empty;
            row = FindBindRow(relation);
        }
        if (row != null && !ReferenceEquals(SelectedBind, row))
            SelectedBind = row;
        string what = relation;
        if (shifted)
            what = T("{0} (with modifier)", what);
        if (relations.Count > 1)
            what = T("{0} ({1}/{2})", what, index + 1, relations.Count);
        if (row == null)
            what = T("{0} - hidden by the group filter", what);
        ShowFollowToast(T("{0} on {1}: {2}", label, deviceName, what));
    }

    private BindListItem? FindBindRow(string relation) =>
        Binds.FirstOrDefault(b => string.Equals(b.RelationName, relation, StringComparison.OrdinalIgnoreCase));

    private void ShowFollowToast(string text)
    {
        FollowInputToast = text;
        followToastCts?.Cancel();
        followToastCts = new CancellationTokenSource();
        CancellationToken token = followToastCts.Token;
        _ = HideFollowToastAsync(token);
    }

    private async Task HideFollowToastAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(2500, token);
            FollowInputToast = string.Empty;
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- The device on the picture: markers, rows and lines light up ----

    private string WorkspaceLiveDevice()
    {
        if (IsBindsStage && OpenVisualAssignMode && !string.IsNullOrWhiteSpace(VisualAssignDevice))
            return VisualAssignDevice.Trim();
        string reference = deviceReference?.DeviceName ?? string.Empty;
        return LayoutShowsLayoutFile || reference.Length == 0 ? ResolveLayoutEditorContextDevice() : reference;
    }

    internal bool IsWorkspaceDevice(string device)
    {
        string shown = WorkspaceLiveDevice();
        return shown.Length > 0 && (OverlayResolver.SameDevice(shown, device) || SameDeviceName(shown, device));
    }

    private void ClearWorkspaceLiveInput()
    {
        WorkspaceLiveInput.Clear();
        RefreshLiveRows();
    }

    // Sets IsLive on the rows and lines of the shown picture whose input is in use.
    internal void RefreshLiveRows()
    {
        HashSet<string> active = new(WorkspaceLiveInput.Pressed.Concat(WorkspaceLiveInput.ActiveAxes()), StringComparer.OrdinalIgnoreCase);
        foreach (ReferenceLegendRow row in ReferenceLegendItems.Concat(SelectedPartRows))
            row.IsLive = active.Contains(row.Input);
        foreach (LayoutEditorMarkerItem marker in LayoutEditorMarkers)
            marker.IsLive = active.Contains(marker.Token ?? string.Empty);
        foreach (LayoutEditorConnectorItem connector in LayoutEditorConnectors)
            connector.IsLive = active.Contains(connector.TargetToken ?? string.Empty);
        liveRowInputs.Clear();
        liveRowInputs.UnionWith(active);
        // An axis stays lit a moment after it stopped: a slow timer puts its rows out (only while one is lit).
        bool axisLit = WorkspaceLiveInput.ActiveAxes().Any();
        if (axisLit && liveRowsTimer == null)
        {
            liveRowsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            liveRowsTimer.Tick += (_, _) =>
            {
                if (WorkspaceLiveInput.ExpiredAxes(liveRowInputs).Count > 0 || !WorkspaceLiveInput.ActiveAxes().Any())
                    RefreshLiveRows();
            };
            liveRowsTimer.Start();
        }
        else if (!axisLit && liveRowsTimer != null)
        {
            liveRowsTimer.Stop();
            liveRowsTimer = null;
        }
    }
}
