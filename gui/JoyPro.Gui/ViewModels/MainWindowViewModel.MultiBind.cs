using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Experimental: several inputs per relation (docs/multi-bind.md). Settings > Experimental switches it on (ui-settings.json
// "multiBind", off by default); then every engine call gets --multi-bind on, the Binds page shows a relation's other
// inputs (card badge, "More inputs" panel: record or pick another input, make one the main input, remove one) and
// "Copy to another device" puts device A's binds on device B too (engine devices copy-to, preview first, one undo step).
// Off: nothing of this shows and the engine gets nothing, so everything is as before.
public sealed partial class MainWindowViewModel
{
    public const string ExperimentalSettingsSectionId = "experimental";

    [ObservableProperty]
    private bool multiBindEnabled;

    // The selected relation's other inputs (input 2..).
    public ObservableCollection<ExtraBindRow> SelectedExtraBinds { get; } = [];

    public bool HasSelectedExtraBinds => SelectedExtraBinds.Count > 0;

    public bool ShowMultiBindPanel => MultiBindEnabled && SelectedBind != null;

    // Copy to another device: device A (one the profile uses) and device B (any known device).
    public ObservableCollection<BindDeviceChoice> CopyDeviceChoices { get; } = [];

    [ObservableProperty]
    private BindDeviceChoice? copyFromDevice;

    [ObservableProperty]
    private BindDeviceChoice? copyToDevice;

    [ObservableProperty]
    private string copyDevicePreview = string.Empty;

    // The copy expander is open (opened by a preview, so its result shows).
    [ObservableProperty]
    private bool isCopyDeviceOpen;

    // The preview was made for exactly these two devices: Copy applies it.
    private string copyDevicePreviewKey = string.Empty;

    public bool HasCopyDevicePreview => CopyDevicePreview.Length > 0;

    public bool CanApplyCopyDevice => HasCopyDevicePreview && copyDevicePreviewKey == CopyKey();

    partial void OnMultiBindEnabledChanged(bool value)
    {
        SaveUiSettings();
        OnPropertyChanged(nameof(ShowMultiBindPanel));
        if (loadingUiSettings)
            return;
        foreach (BindListItem bind in allBinds)
            bind.ShowExtraBinds = value;
        SyncRelationBindsFromBinds();
        RefreshExtraBindRows();
        // The cards show or hide their "+1": the list is built again from the same binds.
        RebuildVisibleBinds();
        if (value)
            RebuildCopyDeviceChoices();
    }

    partial void OnCopyFromDeviceChanged(BindDeviceChoice? value) => OnPropertyChanged(nameof(CanApplyCopyDevice));

    partial void OnCopyToDeviceChanged(BindDeviceChoice? value) => OnPropertyChanged(nameof(CanApplyCopyDevice));

    partial void OnCopyDevicePreviewChanged(string value)
    {
        OnPropertyChanged(nameof(HasCopyDevicePreview));
        OnPropertyChanged(nameof(CanApplyCopyDevice));
    }

    private string CopyKey() => (CopyFromDevice?.Device ?? string.Empty) + "\n" + (CopyToDevice?.Device ?? string.Empty);

    // The "ExtraBinds" of a bind in the engine's binds list (null or missing: none).
    private static IReadOnlyList<ExtraBindRow> ParseExtraBinds(JsonElement bindEl, string relation, bool isAxis)
    {
        if (!bindEl.TryGetProperty("ExtraBinds", out JsonElement extrasEl) || extrasEl.ValueKind != JsonValueKind.Array)
            return [];
        List<ExtraBindRow> rows = [];
        int number = 2;
        foreach (JsonElement extra in extrasEl.EnumerateArray())
        {
            if (extra.ValueKind != JsonValueKind.Object)
                continue;
            string input = GetJsonString(extra, isAxis ? "JAxis" : "JButton");
            if (string.IsNullOrWhiteSpace(input))
                input = GetJsonString(extra, isAxis ? "JButton" : "JAxis");
            List<string> modifiers = [];
            if (extra.TryGetProperty("AllReformers", out JsonElement reformers) && reformers.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement reformer in reformers.EnumerateArray())
                {
                    string name = ModifierNameFromReformer(reformer.ValueKind == JsonValueKind.String ? reformer.GetString() ?? string.Empty : string.Empty);
                    if (name.Length > 0 && !modifiers.Contains(name, StringComparer.OrdinalIgnoreCase))
                        modifiers.Add(name);
                }
            }
            rows.Add(new ExtraBindRow
            {
                Relation = relation,
                Number = number++,
                Device = GetJsonString(extra, "Joystick").Trim(),
                Input = input.Trim(),
                IsAxis = isAxis,
                Modifiers = modifiers
            });
        }
        return rows;
    }

    private void RefreshExtraBindRows()
    {
        IReadOnlyList<ExtraBindRow> rows = MultiBindEnabled && SelectedBind != null ? SelectedBind.ExtraBinds : [];
        if (!rows.Select(r => r.Number + "\n" + r.Label).SequenceEqual(SelectedExtraBinds.Select(r => r.Number + "\n" + r.Label)))
        {
            SelectedExtraBinds.Clear();
            foreach (ExtraBindRow row in rows)
                SelectedExtraBinds.Add(row);
        }
        OnPropertyChanged(nameof(HasSelectedExtraBinds));
        OnPropertyChanged(nameof(ShowMultiBindPanel));
    }

    // Device A: the devices the binds use; device B: those and every device the picker knows (connected ones too).
    private void RebuildCopyDeviceChoices()
    {
        string from = CopyFromDevice?.Device ?? string.Empty;
        string to = CopyToDevice?.Device ?? string.Empty;
        List<BindDeviceChoice> choices = [];
        foreach (string device in allBinds.Where(b => b.HasBind).Select(b => b.Device)
                     .Concat(allBinds.SelectMany(b => b.ExtraBinds).Select(e => e.Device))
                     .Concat(BindDeviceChoices.Where(c => !c.IsKeyboard).Select(c => c.Device)))
        {
            if (string.IsNullOrWhiteSpace(device) || device.Equals("<unbound>", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("Keyboard", StringComparison.OrdinalIgnoreCase) || choices.Any(c => c.Device.Equals(device, StringComparison.OrdinalIgnoreCase)))
                continue;
            choices.Add(new BindDeviceChoice(device, DisplayText.ShortDevice(device)));
        }
        CopyDeviceChoices.Clear();
        foreach (BindDeviceChoice choice in choices.OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase))
            CopyDeviceChoices.Add(choice);
        CopyFromDevice = CopyDeviceChoices.FirstOrDefault(c => c.Device.Equals(from, StringComparison.OrdinalIgnoreCase));
        CopyToDevice = CopyDeviceChoices.FirstOrDefault(c => c.Device.Equals(to, StringComparison.OrdinalIgnoreCase));
    }

    private string SelectedRelationForExtra() => (SelectedBind?.RelationName ?? SelectedRelation?.Name ?? string.Empty).Trim();

    // binds extra add for the selected relation; true when the engine saved it.
    private async Task<bool> AddExtraBindAsync(string relation, string device, string input)
    {
        if (relation.Length == 0 || string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(input))
        {
            StatusText = T("Pick a device and an input first.");
            return false;
        }
        EngineCommandResult? result = await RunEngineAsync(["binds", "extra", "add", relation, "--device", device.Trim(), "--input", input.Trim()],
            includeProfile: true, mutatesProfile: true, busyLabel: N("Adding another input..."));
        if (result?.ExitCode != 0)
            return false;
        await RefreshBindsAndModifiersAfterChangeAsync();
        StatusText = T("Added another input to '{0}'.", relation);
        return true;
    }

    // Record another input: the next button or axis becomes one more input of the selected relation.
    [RelayCommand]
    private async Task RecordExtraInputAsync()
    {
        string relation = SelectedRelationForExtra();
        if (relation.Length == 0 || !MultiBindEnabled)
            return;
        if (IsRecordingInput)
        {
            StatusText = T("Input recording is already running.");
            return;
        }
        JoystickCaptureService.CaptureOptions options = new()
        {
            AxisMode = IsSelectedTargetAxisMode(),
            IgnoreAxesOf = AxisIgnorePredicate(),
            TimeoutMs = ParseOrDefault(RecordTimeoutMs, 6000, 500, 120000),
            WarmupMs = ParseOrDefault(RecordWarmupMs, 300, 0, 5000),
            AxisThresholdPercent = ParseOrDefault(RecordAxisThresholdPercent, 30, 1, 100),
            RecordReleaseOfHeldButtons = RecordReleaseOfHeldButtons
        };
        recordInputCts?.Dispose();
        recordInputCts = new CancellationTokenSource();
        try
        {
            IsRecordingInput = true;
            RecordStatusText = T("Recording another input... press the next control.");
            JoystickCaptureService.CaptureResult? capture = await joystickCaptureService.CaptureNextAsync(options, recordInputCts.Token);
            if (capture == null)
            {
                RecordStatusText = T("Record timed out.");
                return;
            }
            if (await AddExtraBindAsync(relation, capture.Device, capture.Input))
                RecordStatusText = T("Added another input to '{0}'.", relation);
        }
        catch (OperationCanceledException)
        {
            RecordStatusText = T("Recording canceled.");
        }
        finally
        {
            IsRecordingInput = false;
            recordInputCts?.Dispose();
            recordInputCts = null;
        }
    }

    // The device and input picked by hand (the picker above) as another input.
    [RelayCommand]
    private async Task AddPickedExtraInputAsync()
    {
        string relation = SelectedRelationForExtra();
        if (relation.Length == 0 || !MultiBindEnabled)
            return;
        await AddExtraBindAsync(relation, BindDeviceInput, BindInputValue);
    }

    [RelayCommand]
    private async Task RemoveExtraBindAsync(ExtraBindRow? row)
    {
        if (row == null)
            return;
        EngineCommandResult? result = await RunEngineAsync(["binds", "extra", "remove", row.Relation, row.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            includeProfile: true, mutatesProfile: true, busyLabel: N("Removing an input..."));
        if (result?.ExitCode != 0)
            return;
        await RefreshBindsAndModifiersAfterChangeAsync();
        StatusText = T("Removed input {0} of '{1}'.", row.Number, row.Relation);
    }

    [RelayCommand]
    private async Task MakeExtraBindMainAsync(ExtraBindRow? row)
    {
        if (row == null || !MultiBindEnabled)
            return;
        EngineCommandResult? result = await RunEngineAsync(["binds", "extra", "move", row.Relation, row.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--to", "1"],
            includeProfile: true, mutatesProfile: true, busyLabel: N("Changing the main input..."));
        if (result?.ExitCode != 0)
            return;
        await RefreshBindsAndModifiersAfterChangeAsync();
        await ShowBindAsync(row.Relation);
        StatusText = T("Input {0} of '{1}' is the main input now.", row.Number, row.Relation);
    }

    [RelayCommand]
    private void RefreshCopyDevices() => RebuildCopyDeviceChoices();

    // Preview: what would be copied (engine devices copy-to --dry-run); nothing changes.
    [RelayCommand]
    private async Task PreviewCopyToDeviceAsync()
    {
        if (CopyFromDevice == null || CopyToDevice == null)
        {
            StatusText = T("Pick both devices first.");
            return;
        }
        EngineCommandResult? result = await RunEngineAsync(["devices", "copy-to", CopyFromDevice.Device, CopyToDevice.Device, "--dry-run"],
            includeProfile: true, mutatesProfile: false, busyLabel: N("Comparing the two devices..."), forceText: true);
        if (result?.ExitCode != 0)
        {
            CopyDevicePreview = string.Empty;
            return;
        }
        copyDevicePreviewKey = CopyKey();
        IsCopyDeviceOpen = true;
        CopyDevicePreview = JoyPro.Gui.Localization.Loc.TranslateLines((result.StandardOutput ?? string.Empty).Trim());
    }

    // Copy: one engine call, one profile version (Ctrl+Z undoes it).
    [RelayCommand]
    private async Task ApplyCopyToDeviceAsync()
    {
        if (!CanApplyCopyDevice || CopyFromDevice == null || CopyToDevice == null)
            return;
        string from = CopyFromDevice.Device, to = CopyToDevice.Device;
        EngineCommandResult? result = await RunEngineAsync(["devices", "copy-to", from, to],
            includeProfile: true, mutatesProfile: true, busyLabel: N("Copying binds to another device..."), historyNote: N("Copy to another device"));
        if (result?.ExitCode != 0)
            return;
        CopyDevicePreview = string.Empty;
        copyDevicePreviewKey = string.Empty;
        await RefreshBindsAndModifiersAfterChangeAsync();
        StatusText = T("Copied the binds of {0} to {1}. Ctrl+Z undoes it.", DisplayText.ShortDevice(from), DisplayText.ShortDevice(to));
    }
}
