using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.Gui.Localization;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// Replace a device (docs/devices.md "Replace a device"), as JoyPro 1's "replace entire joystick device":
//   - after a profile opens: devices that came back with a new ID (new PC, Windows reinstall, other USB port) get their
//     binds by themselves (engine devices auto-remap); a notice lists what moved, with Undo;
//   - the Replace device window: old device (also one that is not connected) -> new device, preview of every input
//     (same ids for the same product, else by layout from the device catalog), changeable per input, inputs that do
//     not fit, how to merge when the new device has binds; Apply is one change in the history (Undo).
public partial class MainWindowViewModel
{
    // ---- after opening a profile ---------------------------------------------------------------------------------

    public ObservableCollection<DeviceRemapNoticeItem> DeviceRemapNoticeItems { get; } = [];

    [ObservableProperty]
    private string deviceRemapNoticeTitle = string.Empty;

    [ObservableProperty]
    private string deviceRemapSkippedText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceRemapSkipped))]
    private bool hasDeviceRemapNotice;

    [ObservableProperty]
    private bool canUndoDeviceRemap;

    public bool HasDeviceRemapSkipped => HasDeviceRemapNotice && DeviceRemapSkippedText.Length > 0;

    partial void OnDeviceRemapSkippedTextChanged(string value) => OnPropertyChanged(nameof(HasDeviceRemapSkipped));

    // The history name of the last remap or replace, so Undo takes back exactly that change.
    private string deviceRemapHistoryNote = string.Empty;

    // Devices that came back with a new ID get their binds (same product only; several of a kind are left to Replace
    // device). Nothing is written when nothing fits.
    internal async Task RunDeviceAutoRemapAsync()
    {
        if (!HasProfile || !UseLoadCorrections || !RemapMissingDevices)
            return;
        const string note = "Devices with a new ID got their binds";
        EngineCommandResult? result = await RunEngineAsync(["devices", "auto-remap"], includeProfile: true, mutatesProfile: true,
            busyLabel: N("Looking for devices with a new ID..."), forceText: false, historyNote: note);
        if (result == null || result.ExitCode != 0)
            return;
        List<DeviceRemapNoticeItem> remapped = [];
        List<string> skipped = [];
        bool applied = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            JsonElement root = doc.RootElement;
            applied = ReadJsonBool(root, "Applied");
            if (root.TryGetProperty("Remapped", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                    remapped.Add(new DeviceRemapNoticeItem(ReadJsonString(item, "OldDevice"), ReadJsonString(item, "NewDevice"), ReadJsonInt(item, "Binds"), ReadJsonInt(item, "Modifiers"), ReadJsonString(item, "Reason")));
            }
            if (root.TryGetProperty("Skipped", out JsonElement skips) && skips.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in skips.EnumerateArray())
                    skipped.Add(DeviceRemapNoticeItem.ShortName(ReadJsonString(item, "OldDevice")));
            }
        }
        catch (JsonException)
        {
            return;
        }
        if (!applied || remapped.Count == 0)
            return;
        DeviceRemapNoticeItems.Clear();
        foreach (DeviceRemapNoticeItem item in remapped)
            DeviceRemapNoticeItems.Add(item);
        DeviceRemapNoticeTitle = Plural("{0} device came back with a new ID (new PC, Windows reinstall or another USB port): its binds moved to it.",
            "{0} devices came back with a new ID (new PC, Windows reinstall or another USB port): their binds moved to them.", remapped.Count);
        List<string> distinctSkipped = skipped.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        DeviceRemapSkippedText = distinctSkipped.Count == 0 ? string.Empty
            : T("Not moved, more than one fits: {0}. Pick the right one with Replace device.", string.Join(", ", distinctSkipped));
        deviceRemapHistoryNote = note;
        HasDeviceRemapNotice = true;
        RefreshHistoryState();
        CanUndoDeviceRemap = true;
        StatusText = DeviceRemapNoticeTitle;
        await RefreshBindsAndModifiersAfterChangeAsync();
    }

    [RelayCommand]
    private void DismissDeviceRemapNotice()
    {
        HasDeviceRemapNotice = false;
        CanUndoDeviceRemap = false;
        DeviceRemapNoticeItems.Clear();
    }

    // Undo takes back the remap (or the replace) when it is still the last change; later changes stay, so then the
    // History panel is the way back.
    [RelayCommand]
    private async Task UndoDeviceRemapAsync()
    {
        RefreshHistoryState();
        string undo = historyLog?.UndoSubject?.Description ?? string.Empty;
        if (deviceRemapHistoryNote.Length == 0 || !undo.StartsWith(deviceRemapHistoryNote, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = T("Other changes came after it: open History and restore the version before \"{0}\".", deviceRemapHistoryNote);
            IsHistoryPanelOpen = true;
            return;
        }
        await UndoLastChangeCommand.ExecuteAsync(null);
        DismissDeviceRemapNotice();
        await LoadDeviceCardsAsync();
        StatusText = T("Undone: {0}.", deviceRemapHistoryNote);
    }

    // ---- the Replace device window -------------------------------------------------------------------------------

    public event Action? ReplaceDeviceDialogRequested;

    // Devices that have binds in the profile, with how many (connected or not).
    public ObservableCollection<string> ReplaceOldChoices { get; } = [];

    // Connected devices JoyPro uses (keyboards, mice and the old device itself left out).
    public ObservableCollection<string> ReplaceNewChoices { get; } = [];

    public ObservableCollection<DeviceReplaceRow> ReplaceRows { get; } = [];

    [ObservableProperty]
    private string? replaceOldDevice;

    [ObservableProperty]
    private string? replaceNewDevice;

    // auto, same, layout
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReplaceModeAuto), nameof(ReplaceModeSame), nameof(ReplaceModeLayout))]
    private string replaceMode = "auto";

    // add, keep, replace
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReplaceMergeAdd), nameof(ReplaceMergeKeep), nameof(ReplaceMergeReplace))]
    private string replaceMerge = "add";

    [ObservableProperty]
    private string replaceSummary = T("Pick the old device and the device that gets its binds.");

    [ObservableProperty]
    private string replaceModels = string.Empty;

    [ObservableProperty]
    private string replaceCounts = string.Empty;

    [ObservableProperty]
    private string replaceWarnings = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReplaceUnmapped))]
    private string replaceUnmappedText = string.Empty;

    public bool HasReplaceUnmapped => ReplaceHasPreview && ReplaceUnmappedText.Length > 0;

    [ObservableProperty]
    private bool replaceNeedsMerge;

    [ObservableProperty]
    private string replaceMergeText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyReplace), nameof(HasReplaceUnmapped))]
    private bool replaceHasPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyReplace))]
    private bool isReplacePreviewing;

    [ObservableProperty]
    private string replaceStatus = string.Empty;

    public bool CanApplyReplace => ReplaceHasPreview && !IsReplacePreviewing && ReplaceRows.Any(r => r.IsMapped);

    public bool HasReplaceWarnings => ReplaceWarnings.Length > 0;

    partial void OnReplaceWarningsChanged(string value) => OnPropertyChanged(nameof(HasReplaceWarnings));

    public bool ReplaceModeAuto { get => ReplaceMode == "auto"; set { if (value) ReplaceMode = "auto"; } }
    public bool ReplaceModeSame { get => ReplaceMode == "same"; set { if (value) ReplaceMode = "same"; } }
    public bool ReplaceModeLayout { get => ReplaceMode == "layout"; set { if (value) ReplaceMode = "layout"; } }
    public bool ReplaceMergeAdd { get => ReplaceMerge == "add"; set { if (value) ReplaceMerge = "add"; } }
    public bool ReplaceMergeKeep { get => ReplaceMerge == "keep"; set { if (value) ReplaceMerge = "keep"; } }
    public bool ReplaceMergeReplace { get => ReplaceMerge == "replace"; set { if (value) ReplaceMerge = "replace"; } }

    private bool suppressReplacePreview;
    private int replacePreviewVersion;

    // The window, with the card's device as the old device (or empty to pick one).
    [RelayCommand]
    private async Task OpenReplaceDeviceAsync(object? source)
    {
        string? old = source switch
        {
            DeviceCardItem card => ProfileNameOf(card),
            string device when device.Length > 0 => device,
            _ => null
        };
        await PrepareReplaceDeviceAsync(old, null);
        ReplaceDeviceDialogRequested?.Invoke();
    }

    // Fills the window's lists (and the preview when both devices are known), without showing it.
    internal async Task PrepareReplaceDeviceAsync(string? oldDevice, string? newDevice)
    {
        if (!HasProfile)
        {
            ReplaceSummary = T("Open a profile first.");
            return;
        }
        if (lastHudDevices.Count == 0)
            await LoadDeviceCardsAsync();
        suppressReplacePreview = true;
        try
        {
            ReplaceOldChoices.Clear();
            foreach (string device in allBinds.Where(b => b.Device.Length > 0 && !IsKeyboardLike(b.Device)).Select(b => b.Device)
                         .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => IsConnectedDevice(d) ? 1 : 0).ThenBy(d => d, StringComparer.OrdinalIgnoreCase))
                ReplaceOldChoices.Add(device);
            string? pickOld = oldDevice == null ? null : ReplaceOldChoices.FirstOrDefault(d => SameDevice(d, oldDevice)) ?? oldDevice;
            if (pickOld != null && !ReplaceOldChoices.Contains(pickOld))
                ReplaceOldChoices.Insert(0, pickOld);
            ReplaceOldDevice = pickOld ?? ReplaceOldChoices.FirstOrDefault(d => !IsConnectedDevice(d));
            RebuildReplaceNewChoices();
            ReplaceNewDevice = newDevice == null ? SuggestReplaceTarget(ReplaceOldDevice) : ReplaceNewChoices.FirstOrDefault(d => SameDevice(d, newDevice)) ?? newDevice;
            ReplaceMode = "auto";
            ReplaceMerge = "add";
            ReplaceRows.Clear();
            ReplaceHasPreview = false;
            ReplaceStatus = string.Empty;
        }
        finally
        {
            suppressReplacePreview = false;
        }
        await PreviewReplaceDeviceAsync();
    }

    private void RebuildReplaceNewChoices()
    {
        string? keep = ReplaceNewDevice;
        ReplaceNewChoices.Clear();
        IEnumerable<string> connected = lastHudDevices.Count > 0 ? lastHudDevices.Select(d => d.Device) : ConnectedDevices;
        foreach (string device in connected.Where(d => d.Length > 0 && !IsKeyboardLike(d) && !SameDevice(d, ReplaceOldDevice))
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            ReplaceNewChoices.Add(device);
        if (keep != null && ReplaceNewChoices.Any(d => SameDevice(d, keep)))
            ReplaceNewDevice = ReplaceNewChoices.First(d => SameDevice(d, keep));
    }

    // The same product first (same name without the ID), else nothing (the user picks).
    private string? SuggestReplaceTarget(string? oldDevice)
    {
        if (string.IsNullOrWhiteSpace(oldDevice))
            return null;
        string name = DeviceRemapNoticeItem.ShortName(oldDevice);
        return ReplaceNewChoices.FirstOrDefault(d => string.Equals(DeviceRemapNoticeItem.ShortName(d), name, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsConnectedDevice(string device) =>
        lastHudDevices.Any(d => SameDevice(d.Device, device)) || ConnectedDevices.Any(d => SameDevice(d, device));

    private static bool IsKeyboardLike(string device) =>
        string.Equals(device.Trim(), "Keyboard", StringComparison.OrdinalIgnoreCase) || string.Equals(device.Trim(), "Mouse", StringComparison.OrdinalIgnoreCase);

    partial void OnReplaceOldDeviceChanged(string? value)
    {
        if (suppressReplacePreview)
            return;
        suppressReplacePreview = true;
        try
        {
            RebuildReplaceNewChoices();
            if (ReplaceNewDevice == null || SameDevice(ReplaceNewDevice, value))
                ReplaceNewDevice = SuggestReplaceTarget(value);
        }
        finally
        {
            suppressReplacePreview = false;
        }
        _ = PreviewReplaceDeviceAsync();
    }

    partial void OnReplaceNewDeviceChanged(string? value)
    {
        if (!suppressReplacePreview)
            _ = PreviewReplaceDeviceAsync();
    }

    partial void OnReplaceModeChanged(string value)
    {
        if (!suppressReplacePreview)
            _ = PreviewReplaceDeviceAsync();
    }

    private List<string> ReplaceArgs(bool dryRun, bool withChoices)
    {
        List<string> args = ["devices", "replace", ReplaceOldDevice!.Trim(), ReplaceNewDevice!.Trim(), "--mode", ReplaceMode];
        foreach (KeyValuePair<string, string> grip in HudGrips())
        {
            args.Add("--grip");
            args.Add(grip.Key + "=" + grip.Value);
        }
        if (withChoices)
        {
            foreach (DeviceReplaceRow row in ReplaceRows.Where(r => r.ChangedByUser))
            {
                args.Add("--map");
                args.Add(row.OldInput + "=" + (row.IsMapped ? row.ChosenInput : "none"));
            }
        }
        if (dryRun)
            args.Add("--dry-run");
        return args;
    }

    [RelayCommand]
    private async Task PreviewReplaceDeviceAsync()
    {
        int version = ++replacePreviewVersion;
        if (string.IsNullOrWhiteSpace(ReplaceOldDevice) || string.IsNullOrWhiteSpace(ReplaceNewDevice))
        {
            ReplaceRows.Clear();
            ReplaceHasPreview = false;
            ReplaceSummary = string.IsNullOrWhiteSpace(ReplaceOldDevice) ? T("Pick the old device (the one whose binds move).") : T("Pick the device that gets the binds.");
            ReplaceModels = ReplaceCounts = ReplaceWarnings = string.Empty;
            ReplaceNeedsMerge = false;
            return;
        }
        IsReplacePreviewing = true;
        try
        {
            EngineCommandResult? result = await RunEngineAsync(ReplaceArgs(dryRun: true, withChoices: false), includeProfile: true, mutatesProfile: false,
                busyLabel: T("Previewing the device replacement..."), forceText: false);
            if (version != replacePreviewVersion)
                return;
            ReplaceRows.Clear();
            if (result == null || result.ExitCode != 0)
            {
                ReplaceHasPreview = false;
                ReplaceSummary = T("No preview: {0}", Loc.Translate(FirstLine(result?.StandardError) + FirstLine(result == null || result.StandardError.Length > 0 ? string.Empty : result.StandardOutput)));
                return;
            }
            ApplyReplacePlan(result.StandardOutput);
        }
        finally
        {
            if (version == replacePreviewVersion)
                IsReplacePreviewing = false;
        }
    }

    internal void ApplyReplacePlan(string output)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            List<string> newInputs = ReadJsonStrings(root, "NewInputs");
            if (root.TryGetProperty("Mappings", out JsonElement mappings) && mappings.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement m in mappings.EnumerateArray())
                {
                    bool axis = ReadJsonBool(m, "Axis");
                    List<string> choices = [DeviceReplaceRow.LeaveOut, .. newInputs.Where(i => IsAxisToken(i) == axis)];
                    string mapped = ReadJsonString(m, "NewInput");
                    if (mapped.Length > 0 && !choices.Contains(mapped, StringComparer.OrdinalIgnoreCase))
                        choices.Add(mapped);
                    ReplaceRows.Add(new DeviceReplaceRow(ReadJsonString(m, "OldInput"), ReadJsonString(m, "OldPart"), mapped, ReadJsonString(m, "NewPart"),
                        ReadJsonString(m, "Method"), ReadJsonString(m, "Note"), axis, ReadJsonStrings(m, "Relations"), ReadJsonStrings(m, "Modifiers"),
                        ReadJsonStrings(m, "TargetRelations"), choices));
                }
            }
            foreach (DeviceReplaceRow row in ReplaceRows)
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DeviceReplaceRow.ChosenInput))
                        UpdateReplaceCounts();
                };
            string mode = ReadJsonString(root, "Mode");
            ReplaceSummary = mode == "same"
                ? T("Same input ids: {0}.", Loc.Translate(ReadJsonString(root, "ModeReason")))
                : T("Mapped by layout: {0}.", Loc.Translate(ReadJsonString(root, "ModeReason")));
            ReplaceModels = ReadJsonBool(root, "OldConnected")
                ? T("{0} ({1})  ->  {2} ({3})", DeviceRemapNoticeItem.ShortName(ReadJsonString(root, "OldDevice")), ReadJsonString(root, "OldModel"),
                    DeviceRemapNoticeItem.ShortName(ReadJsonString(root, "NewDevice")), ReadJsonString(root, "NewModel"))
                : T("{0} ({1}, not connected)  ->  {2} ({3})", DeviceRemapNoticeItem.ShortName(ReadJsonString(root, "OldDevice")), ReadJsonString(root, "OldModel"),
                    DeviceRemapNoticeItem.ShortName(ReadJsonString(root, "NewDevice")), ReadJsonString(root, "NewModel"));
            ReplaceWarnings = string.Join(Environment.NewLine, ReadJsonStrings(root, "Warnings").Select(Loc.Translate));
            int targetBinds = ReadJsonInt(root, "TargetBinds");
            ReplaceNeedsMerge = ReadJsonBool(root, "NeedsMergeChoice");
            ReplaceMergeText = ReplaceNeedsMerge
                ? Plural("{1} already has {0} bind. Where both use the same input:", "{1} already has {0} binds. Where both use the same input:", targetBinds,
                    targetBinds, DeviceRemapNoticeItem.ShortName(ReplaceNewDevice ?? string.Empty))
                : string.Empty;
            ReplaceHasPreview = true;
            UpdateReplaceCounts();
        }
        catch (JsonException)
        {
            ReplaceHasPreview = false;
            ReplaceSummary = T("No preview (the engine's answer could not be read).");
        }
    }

    private void UpdateReplaceCounts()
    {
        int mapped = ReplaceRows.Count(r => r.IsMapped);
        int binds = ReplaceRows.Where(r => r.IsMapped).Sum(r => r.Relations.Count);
        int staying = ReplaceRows.Where(r => !r.IsMapped).Sum(r => r.Relations.Count);
        int overlaps = ReplaceRows.Count(r => r.HasOverlap);
        List<string> counts = [T("{0} of {1} inputs fit, {2} bind(s) move.", mapped, ReplaceRows.Count, binds)];
        if (staying > 0)
            counts.Add(T("{0} stay on the old device (no matching input).", staying));
        if (overlaps > 0)
            counts.Add(T("The new device already uses {0} of these inputs.", overlaps));
        ReplaceCounts = string.Join(" ", counts);
        // The inputs that do not fit, named up front (the list below can be long).
        List<DeviceReplaceRow> left = ReplaceRows.Where(r => !r.IsMapped).ToList();
        ReplaceUnmappedText = left.Count == 0 ? string.Empty
            : T("Stay on the old device: {0}. Pick an input for them below if the new device has one.",
                left.Count > 8 ? T("{0} and {1} more", UnmappedList(left.Take(8)), left.Count - 8) : UnmappedList(left));
        OnPropertyChanged(nameof(CanApplyReplace));
    }

    private static string UnmappedList(IEnumerable<DeviceReplaceRow> rows) =>
        string.Join(", ", rows.Select(r => r.OldInput + (r.Relations.Count > 0 ? " (" + r.Relations[0] + ")" : string.Empty)));

    private static bool IsAxisToken(string input) => input.StartsWith("JOY_", StringComparison.OrdinalIgnoreCase) && !input.StartsWith("JOY_BTN", StringComparison.OrdinalIgnoreCase);

    private static List<string> ReadJsonStrings(JsonElement item, string name)
    {
        List<string> list = [];
        if (item.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array)
            list.AddRange(array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0));
        return list;
    }

    // Moves the binds (one change in the history). True when it was applied (the window closes).
    internal async Task<bool> ApplyReplaceDeviceAsync()
    {
        if (!CanApplyReplace || string.IsNullOrWhiteSpace(ReplaceOldDevice) || string.IsNullOrWhiteSpace(ReplaceNewDevice))
            return false;
        string oldName = DeviceRemapNoticeItem.ShortName(ReplaceOldDevice);
        string newName = DeviceRemapNoticeItem.ShortName(ReplaceNewDevice);
        List<string> args = ReplaceArgs(dryRun: false, withChoices: true);
        if (ReplaceNeedsMerge)
        {
            args.Add("--merge");
            args.Add(ReplaceMerge);
        }
        string note = T("Replaced {0} with {1}", oldName, newName);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: T("Moving the binds of {0}...", oldName),
            forceText: false, historyNote: note);
        if (result == null || result.ExitCode != 0)
        {
            ReplaceStatus = T("Not replaced: {0}", Loc.Translate(FirstLine(result?.StandardError) + (result != null && result.StandardError.Length == 0 ? FirstLine(result.StandardOutput) : string.Empty)));
            return false;
        }
        int moved = 0, kept = 0, removed = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            moved = ReadJsonInt(doc.RootElement, "MovedBinds");
            kept = ReadJsonInt(doc.RootElement, "KeptOnOld");
            removed = ReadJsonInt(doc.RootElement, "RemovedTargetBinds");
        }
        catch (JsonException)
        {
        }
        DeviceRemapNoticeItems.Clear();
        List<string> done = [T("{0} bind(s) of {1} moved to {2}.", moved, oldName, newName)];
        if (kept > 0)
            done.Add(T("{0} stay on {1} (no matching input).", kept, oldName));
        if (removed > 0)
            done.Add(T("{0} bind(s) of {1} replaced.", removed, newName));
        DeviceRemapNoticeTitle = string.Join(" ", done);
        DeviceRemapSkippedText = string.Empty;
        deviceRemapHistoryNote = note;
        HasDeviceRemapNotice = true;
        CanUndoDeviceRemap = true;
        RefreshHistoryState();
        StatusText = DeviceRemapNoticeTitle;
        await RefreshBindsAndModifiersAfterChangeAsync();
        await LoadDeviceCardsAsync();
        return true;
    }
}
