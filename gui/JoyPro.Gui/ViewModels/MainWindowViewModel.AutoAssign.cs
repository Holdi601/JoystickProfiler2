using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using System.Text.RegularExpressions;
using JoyPro.Gui.Localization;

namespace JoyPro.Gui.ViewModels;

// "Auto Assign": binds the relations to the best physical controls of the user's devices (engine relations
// auto-assign). The panel shows the recognised devices, lets the user pick the grip of bases that take several grips
// (with the manufacturer's pictures), leave devices out and add devices from the catalog that are not connected.
public sealed partial class MainWindowViewModel
{
    public IReadOnlyList<int> AutoAssignModifierChoices { get; } = [0, 1, 2];

    public ObservableCollection<AutoAssignDeviceItem> AutoAssignDevices { get; } = [];

    public ObservableCollection<AutoAssignCatalogItem> AutoAssignCatalogResults { get; } = [];

    public ObservableCollection<AutoAssignCatalogItem> AutoAssignAddedDevices { get; } = [];

    [ObservableProperty]
    private bool isAutoAssignPanelOpen;

    [ObservableProperty]
    private int autoAssignMaxModifiers = 2;

    [ObservableProperty]
    private bool autoAssignOverwrite;

    [ObservableProperty]
    private bool autoAssignIncludeMissing;

    [ObservableProperty]
    private bool autoAssignKeepMyLayout = true;

    [ObservableProperty]
    private string autoAssignCatalogQuery = string.Empty;

    [ObservableProperty]
    private AutoAssignCatalogItem? selectedAutoAssignCatalogItem;

    [ObservableProperty]
    private string autoAssignPreview = string.Empty;

    // Only the relations selected in the list (--relation): the others keep their binds and only occupy their controls.
    [ObservableProperty]
    private bool autoAssignOnlySelected;

    // The layout of one catalog device (engine devices kb show): what JoyPro knows about each input.
    public ObservableCollection<AutoAssignLayoutRow> AutoAssignLayoutRows { get; } = [];

    [ObservableProperty]
    private string autoAssignLayoutTitle = string.Empty;

    [ObservableProperty]
    private string autoAssignLayoutDetails = string.Empty;

    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? autoAssignLayoutImage;

    public bool HasAutoAssignLayout => AutoAssignLayoutTitle.Length > 0;

    partial void OnAutoAssignLayoutTitleChanged(string value) => OnPropertyChanged(nameof(HasAutoAssignLayout));

    [RelayCommand]
    private async Task ToggleAutoAssignPanelAsync()
    {
        IsAutoAssignPanelOpen = !IsAutoAssignPanelOpen;
        if (IsAutoAssignPanelOpen && !IsBindsStage)
            CurrentStage = UiStage.Binds;
        if (IsAutoAssignPanelOpen && AutoAssignDevices.Count == 0)
            await DetectAutoAssignDevicesAsync();
    }

    [RelayCommand]
    private async Task DetectAutoAssignDevicesAsync()
    {
        List<string> args = ["devices", "kb", "identify"];
        AddAutoAssignDeviceChoices(args, includeIgnored: false);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: N("Detecting devices..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;

        Dictionary<string, string> grips = AutoAssignDevices.Where(d => d.SelectedGrip != null).ToDictionary(d => d.Device, d => d.SelectedGrip!.Id);
        HashSet<string> ignored = AutoAssignDevices.Where(d => d.IsIgnored).Select(d => d.Device).ToHashSet();
        AutoAssignDevices.Clear();
        foreach (AutoAssignDeviceItem device in ParseAutoAssignDevices(result.StandardOutput))
        {
            if (grips.TryGetValue(device.Device, out string? gripId))
                device.SelectedGrip = device.FindGrip(gripId);
            device.IsIgnored = ignored.Contains(device.Device);
            AutoAssignDevices.Add(device);
        }
        StatusText = T("Auto Assign: {0} devices{1}", AutoAssignDevices.Count, (AutoAssignDevices.Any(d => d.NeedsGrip && d.SelectedGrip == null) ? T("; pick the grip of the marked devices.") : "."));
        _ = LoadAutoAssignPicturesAsync(AutoAssignDevices.SelectMany(d => d.Grips).ToList());
    }

    [RelayCommand]
    private async Task SearchAutoAssignCatalogAsync()
    {
        List<string> args = ["devices", "kb", "list"];
        if (!string.IsNullOrWhiteSpace(AutoAssignCatalogQuery))
            args.Add(AutoAssignCatalogQuery.Trim());
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: N("Searching the device catalog..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        AutoAssignCatalogResults.Clear();
        foreach (AutoAssignCatalogItem item in ParseAutoAssignCatalog(result.StandardOutput).Where(i => i.Kind != "base"))
            AutoAssignCatalogResults.Add(item);
        SelectedAutoAssignCatalogItem = AutoAssignCatalogResults.FirstOrDefault();
    }

    [RelayCommand]
    private void AddAutoAssignCatalogDevice()
    {
        AutoAssignCatalogItem? item = SelectedAutoAssignCatalogItem;
        if (item != null && AutoAssignAddedDevices.All(a => a.Id != item.Id))
            AutoAssignAddedDevices.Add(item);
    }

    [RelayCommand]
    private void RemoveAutoAssignAddedDevice(AutoAssignCatalogItem? item)
    {
        if (item != null)
            AutoAssignAddedDevices.Remove(item);
    }

    [RelayCommand]
    private async Task PreviewAutoAssignAsync()
    {
        List<string>? args = AutoAssignArguments(dryRun: true);
        if (args == null)
            return;
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false, busyLabel: N("Previewing Auto Assign..."), forceText: false);
        if (result != null)
            AutoAssignPreview = result.ExitCode == 0 ? FormatAutoAssignResult(result.StandardOutput) : result.StandardError + result.StandardOutput;
    }

    // Why the selected relations land where they do (--explain): their slot, previous control and the best candidates
    // with their score and what occupies them. A preview: nothing changes.
    [RelayCommand]
    private async Task ExplainAutoAssignAsync()
    {
        List<string> relations = AutoAssignSelectedRelations();
        if (relations.Count == 0)
        {
            StatusText = T("Auto Assign: select the relations to explain in the list first.");
            return;
        }
        List<string>? args = AutoAssignArguments(dryRun: true);
        if (args == null)
            return;
        foreach (string relation in relations)
            args.AddRange(["--explain", relation]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false, busyLabel: N("Explaining Auto Assign..."), forceText: false);
        if (result != null)
            AutoAssignPreview = result.ExitCode == 0 ? FormatAutoAssignResult(result.StandardOutput) : result.StandardError + result.StandardOutput;
    }

    [RelayCommand]
    private async Task ApplyAutoAssignAsync()
    {
        List<string>? args = AutoAssignArguments(dryRun: false);
        if (args == null)
            return;
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Assigning controls..."), forceText: false);
        if (result == null)
            return;
        AutoAssignPreview = result.ExitCode == 0 ? FormatAutoAssignResult(result.StandardOutput) : result.StandardError + result.StandardOutput;
        if (result.ExitCode == 0)
        {
            await RefreshRelationsAsync();
            await RefreshBindsAsync();
        }
    }

    // The Auto Assign result (engine JSON) in plain words: the devices, the counts, where each relation goes ("Hat 1 down
    // (TMS hat down)"), what stays unassigned and why; never an input or control id. Text that is no result stays as it is.
    internal static string FormatAutoAssignResult(string output)
    {
        string text = (output ?? string.Empty).Trim();
        if (!text.StartsWith('{'))
            return output ?? string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("Assignments", out JsonElement assignments) || assignments.ValueKind != JsonValueKind.Array)
                return output ?? string.Empty;
            System.Text.StringBuilder sb = new();
            sb.AppendLine(T(ReadJsonBool(root, "DryRun") ? "Auto Assign preview (nothing changed yet)" : "Auto Assign"));
            if (root.TryGetProperty("Devices", out JsonElement devices) && devices.ValueKind == JsonValueKind.Array && devices.GetArrayLength() > 0)
            {
                sb.AppendLine(T("Devices:"));
                foreach (JsonElement device in devices.EnumerateArray())
                {
                    string model = ReadJsonString(device, "Model");
                    string alternative = ReadJsonString(device, "AlternativeTo");
                    string note = ReadJsonString(device, "Note");
                    sb.AppendLine("  - " + ReadableNames.Device(ReadJsonString(device, "Device")) + (ReadJsonBool(device, "Connected") ? string.Empty : " " + T("(not connected)")) + " => " +
                                  (model.Length > 0 ? model : T("grip needed")) + (ReadJsonBool(device, "Generic") ? " " + T("(generic)") : string.Empty) +
                                  (alternative.Length > 0 ? " - " + T("left out, same role as {0}", ReadableNames.Device(alternative)) : string.Empty) +
                                  (note.Length > 0 ? " - " + EngineText.Tr(note) : string.Empty));
                }
            }
            int styleKept = root.TryGetProperty("StyleKept", out JsonElement style) && style.TryGetInt32(out int kept) ? kept : 0;
            int keptExisting = root.TryGetProperty("KeptExisting", out JsonElement existing) && existing.TryGetInt32(out int count) ? count : 0;
            sb.AppendLine(T("Kept existing binds: {0}", keptExisting));
            sb.AppendLine(T("Assigned: {0}", assignments.GetArrayLength()) + (styleKept > 0 ? " " + T("({0} on the same control as before)", styleKept) : string.Empty));
            List<string> modifiers = ReadJsonStrings(root, "ModifiersUsed");
            sb.AppendLine(T("Modifiers: {0}", modifiers.Count == 0 ? T("none") : EngineText.Tr(string.Join("; ", modifiers))));
            if (assignments.GetArrayLength() > 0)
            {
                sb.AppendLine(T("Assignments:"));
                foreach (JsonElement a in assignments.EnumerateArray())
                {
                    // Composed in the app's language from its parts (the engine's "Where" is English).
                    string where = string.Empty;
                    {
                        string control = ReadJsonString(a, "Control");
                        Match labelled = Regex.Match(control, "^(?<c>[^ ]*) \"(?<l>.*)\"$");
                        where = ReadableNames.Device(ReadJsonString(a, "Device")) + " " +
                                (labelled.Success ? ReadableNames.Where(ReadJsonString(a, "Input"), labelled.Groups["c"].Value, labelled.Groups["l"].Value) : ReadableNames.Where(ReadJsonString(a, "Input"), control)) +
                                (ReadJsonString(a, "Modifier").Length > 0 ? " + " + ReadJsonString(a, "Modifier") : string.Empty);
                    }
                    sb.AppendLine("  - " + ReadJsonString(a, "Relation") + " => " + where);
                }
            }
            List<string> unassigned = ReadJsonStrings(root, "Unassigned");
            if (unassigned.Count > 0)
            {
                sb.AppendLine(T("Not assigned (no fitting free input):"));
                foreach (string name in unassigned)
                    sb.AppendLine("  - " + name);
            }
            List<string> explanations = ReadJsonStrings(root, "Explanations");
            if (explanations.Count > 0)
            {
                sb.AppendLine(T("Explain:"));
                foreach (string line in explanations)
                    sb.AppendLine("  " + EngineText.Tr(line));
            }
            foreach ((string key, string title) in new[] { ("Warnings", T("Warnings:")), ("Errors", T("Errors:")) })
            {
                List<string> lines = ReadJsonStrings(root, key);
                if (lines.Count == 0)
                    continue;
                sb.AppendLine(title);
                foreach (string line in lines.Distinct())
                    sb.AppendLine("  - " + EngineText.Tr(line));
            }
            return sb.ToString().TrimEnd();
        }
        catch (JsonException)
        {
            return output ?? string.Empty;
        }
    }

    // The relations selected in the relation list (several with Ctrl/Shift), else the bind selected on the Binds page,
    // else the relation shown.
    private List<string> AutoAssignSelectedRelations()
    {
        List<string> names = relationMassSelectedNames
            .Where(name => allRelations.Any(r => string.Equals(r.Name, name, System.StringComparison.OrdinalIgnoreCase)))
            .OrderBy(name => name, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0 && IsBindsStage && SelectedBind != null && SelectedBind.RelationName.Length > 0)
            names.Add(SelectedBind.RelationName);
        if (names.Count == 0 && SelectedRelation != null)
            names.Add(SelectedRelation.Name);
        return names;
    }

    // null: "only the selected relations" without a selection (nothing is run).
    private List<string>? AutoAssignArguments(bool dryRun)
    {
        List<string> args = ["relations", "auto-assign"];
        AddAutoAssignDeviceChoices(args, includeIgnored: true);
        if (AutoAssignOnlySelected)
        {
            List<string> relations = AutoAssignSelectedRelations();
            if (relations.Count == 0)
            {
                StatusText = T("Auto Assign: select the relations to assign in the list, or untick \"Only the selected relations\".");
                return null;
            }
            foreach (string relation in relations)
                args.AddRange(["--relation", relation]);
        }
        args.Add("--max-modifiers");
        args.Add(AutoAssignMaxModifiers.ToString(System.Globalization.CultureInfo.InvariantCulture));
        args.Add("--phase-layers");
        args.Add(PhaseLayerChoice.ToArgument(AutoAssignPhaseLayers));
        if (AutoAssignOverwrite)
        {
            args.Add("--overwrite");
            if (AutoAssignIncludeMissing)
                args.Add("--include-missing");
        }
        if (!AutoAssignKeepMyLayout)
            args.Add("--no-style");
        if (dryRun)
            args.Add("--dry-run");
        return args;
    }

    private void AddAutoAssignDeviceChoices(List<string> args, bool includeIgnored)
    {
        foreach (AutoAssignDeviceItem device in AutoAssignDevices)
        {
            if (device.SelectedGrip != null)
            {
                args.Add("--grip");
                args.Add(device.Device + "=" + device.SelectedGrip.Id);
            }
            if (includeIgnored && device.IsIgnored)
            {
                args.Add("--ignore-device");
                args.Add(device.Device);
            }
        }
        foreach (AutoAssignCatalogItem added in AutoAssignAddedDevices)
        {
            args.Add("--add-device");
            args.Add(added.Id);
        }
    }

    // The layout of a detected device (its grip when one is picked) or of the catalog device picked in the search.
    [RelayCommand]
    private async Task ShowAutoAssignDeviceLayoutAsync(AutoAssignDeviceItem? device)
    {
        string id = device?.SelectedGrip?.Id ?? device?.ModelId ?? string.Empty;
        if (id.Length == 0)
        {
            StatusText = device == null ? T("Pick a device first.")
                : device.NeedsGrip ? T("Pick the grip of {0} first.", device.Device)
                : T("{0} is not in the device catalog (generic layout).", device.Device);
            return;
        }
        await ShowAutoAssignLayoutAsync(id);
    }

    [RelayCommand]
    private async Task ShowAutoAssignCatalogLayoutAsync()
    {
        if (SelectedAutoAssignCatalogItem == null)
        {
            StatusText = T("Search the catalog and pick a device first.");
            return;
        }
        await ShowAutoAssignLayoutAsync(SelectedAutoAssignCatalogItem.Id);
    }

    [RelayCommand]
    private void CloseAutoAssignLayout()
    {
        AutoAssignLayoutRows.Clear();
        AutoAssignLayoutTitle = string.Empty;
        AutoAssignLayoutDetails = string.Empty;
        AutoAssignLayoutImage = null;
    }

    private async Task ShowAutoAssignLayoutAsync(string id)
    {
        EngineCommandResult? result = await RunEngineAsync(["devices", "kb", "show", id], includeProfile: false, mutatesProfile: false,
            busyLabel: N("Loading the device layout..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        CloseAutoAssignLayout();
        string image;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            string vidPids = root.TryGetProperty("VidPids", out JsonElement ids) && ids.ValueKind == JsonValueKind.Array
                ? string.Join(", ", ids.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()))
                : string.Empty;
            string hand = ReadJsonString(root, "Hand");
            string baseId = ReadJsonString(root, "Base");
            List<string> details = [ReadJsonString(root, "Vendor"), ReadJsonString(root, "Kind")];
            if (hand.Length > 0 && hand != "none")
                details.Add(hand + " hand");
            details.Add(T("layout known: {0}", ReadJsonString(root, "Confidence")));
            if (baseId.Length > 0)
                details.Add(T("grip of {0}", baseId));
            if (vidPids.Length > 0)
                details.Add(T("VID:PID {0}", vidPids));
            AutoAssignLayoutDetails = string.Join(" | ", details.Where(d => d.Length > 0));
            foreach (string list in new[] { "Axes", "Controls" })
            {
                if (!root.TryGetProperty(list, out JsonElement controls) || controls.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement control in controls.EnumerateArray())
                {
                    AutoAssignLayoutRows.Add(new AutoAssignLayoutRow(ReadJsonString(control, "Input"), ReadJsonString(control, "Control"), ReadJsonString(control, "Label"),
                        list == "Axes" ? "axis" : ReadJsonString(control, "Type"), ReadJsonString(control, "Finger")));
                }
            }
            image = ReadJsonString(root, "Image");
            AutoAssignLayoutTitle = ReadJsonString(root, "Name") + " (" + ReadJsonString(root, "Id") + ")";
        }
        catch (JsonException)
        {
            StatusText = T("The device layout could not be read.");
            return;
        }
        StatusText = T("{0}: {1} inputs.", AutoAssignLayoutTitle, AutoAssignLayoutRows.Count);
        if (image.Length > 0)
        {
            string title = AutoAssignLayoutTitle;
            Avalonia.Media.Imaging.Bitmap? picture = await DeviceImageCache.LoadAsync(image);
            if (picture != null && AutoAssignLayoutTitle == title)
                AutoAssignLayoutImage = picture;
        }
    }

    private static async Task LoadAutoAssignPicturesAsync(List<AutoAssignGripItem> grips)
    {
        foreach (AutoAssignGripItem grip in grips.Where(g => g.Image == null && g.ImageUrl.Length > 0))
        {
            Avalonia.Media.Imaging.Bitmap? image = await DeviceImageCache.LoadAsync(grip.ImageUrl);
            if (image != null)
                await Dispatcher.UIThread.InvokeAsync(() => grip.Image = image);
        }
    }

    private static List<AutoAssignDeviceItem> ParseAutoAssignDevices(string output)
    {
        List<AutoAssignDeviceItem> devices = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return devices;
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                List<AutoAssignGripItem> grips = [];
                if (item.TryGetProperty("GripChoices", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement grip in list.EnumerateArray())
                        grips.Add(new AutoAssignGripItem(ReadJsonString(grip, "Id"), ReadJsonString(grip, "Name"), ReadJsonString(grip, "Image")));
                }
                string device = ReadJsonString(item, "Device");
                if (device.Length == 0)
                    continue;
                devices.Add(new AutoAssignDeviceItem(device, ReadJsonString(item, "Model"), ReadJsonString(item, "Kind"), ReadJsonBool(item, "Connected"),
                    ReadJsonBool(item, "Generic"), ReadJsonString(item, "Note"), ReadJsonString(item, "AlternativeTo"), grips, ReadJsonString(item, "ModelId")));
            }
        }
        catch (JsonException)
        {
        }
        return devices;
    }

    private static List<AutoAssignCatalogItem> ParseAutoAssignCatalog(string output)
    {
        List<AutoAssignCatalogItem> items = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return items;
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                string id = ReadJsonString(item, "Id");
                if (id.Length > 0)
                    items.Add(new AutoAssignCatalogItem(id, ReadJsonString(item, "Name"), ReadJsonString(item, "Kind"), ReadJsonString(item, "Base"), ReadJsonString(item, "Confidence")));
            }
        }
        catch (JsonException)
        {
        }
        return items;
    }
}
