using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// A connected device for the device reference: what Windows reports about it (engine hud devices) and the model the
// engine found.
public sealed class ReferenceDeviceItem
{
    public string Device { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public bool Generic { get; init; }
    public DeviceProbe Probe { get; init; } = new();

    public override string ToString() => DisplayName;
}

// The device reference of the layout pages (docs/device-reference.md): DeviceReference is the DeviceReferenceViewModel a
// page puts into a DeviceReferenceView. A device picked in the layout suggestions (or in DeviceReferenceDevices) is shown
// at once with its picture, markers and the binds of the profile.
public sealed partial class MainWindowViewModel
{
    private DeviceReferenceViewModel? deviceReference;
    private bool referenceDevicesLoaded;

    public DeviceReferenceViewModel DeviceReference => deviceReference ??= CreateDeviceReference();

    // Connected devices (filled by LoadDeviceReferenceDevicesCommand).
    public ObservableCollection<ReferenceDeviceItem> DeviceReferenceDevices { get; } = [];

    // Catalog models matching DeviceReferenceSearch (to show a model that is not connected).
    public ObservableCollection<RefDevice> DeviceReferenceModels { get; } = [];

    [ObservableProperty] private string deviceReferenceSearch = string.Empty;
    [ObservableProperty] private ReferenceDeviceItem? selectedDeviceReferenceDevice;
    [ObservableProperty] private RefDevice? selectedDeviceReferenceModel;

    private DeviceReferenceViewModel CreateDeviceReference()
    {
        DeviceReferenceService.EnginePath = EnginePath ?? string.Empty;
        DeviceReferenceService.ApplySettings();
        DeviceReferenceViewModel reference = new() { CaptureService = joystickCaptureService };
        ObserveDeviceReference(reference);
        return reference;
    }

    partial void OnDeviceReferenceSearchChanged(string value) => RefreshDeviceReferenceModels();

    public void RefreshDeviceReferenceModels()
    {
        DeviceReferenceService.EnginePath = EnginePath ?? string.Empty;
        DeviceReferenceModels.Clear();
        foreach (RefDevice model in DeviceReferenceService.Catalog.Search(DeviceReferenceSearch).Where(m => m.Kind != "other").OrderBy(m => m.Vendor).ThenBy(m => m.Name).Take(400))
            DeviceReferenceModels.Add(model);
    }

    partial void OnSelectedDeviceReferenceModelChanged(RefDevice? value)
    {
        if (value != null)
            _ = ShowDeviceReferenceModelAsync(value.Id);
    }

    partial void OnSelectedDeviceReferenceDeviceChanged(ReferenceDeviceItem? value)
    {
        if (value != null)
            _ = ShowDeviceReferenceForAsync(value.Device);
    }

    public async Task ShowDeviceReferenceModelAsync(string modelId)
    {
        await DeviceReference.ShowModelAsync(modelId);
        DeviceReference.SetBindings(new Dictionary<string, string>());
    }

    // Reads the connected devices (USB id, name, buttons, hats, axes) through the engine.
    [RelayCommand]
    private async Task LoadDeviceReferenceDevicesAsync()
    {
        DeviceReferenceService.EnginePath = EnginePath ?? string.Empty;
        EngineCommandResult? result = await RunEngineAsync(HudDevicesArguments(), includeProfile: false, mutatesProfile: false, busyLabel: N("Reading your devices..."), forceText: false);
        referenceDevicesLoaded = true;
        DeviceReferenceDevices.Clear();
        if (result == null || result.ExitCode != 0)
            return;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            if (!doc.RootElement.TryGetProperty("Devices", out JsonElement devices) || devices.ValueKind != JsonValueKind.Array)
                return;
            foreach (JsonElement item in devices.EnumerateArray())
            {
                List<string> axes = [];
                if (item.TryGetProperty("Axes", out JsonElement axisList) && axisList.ValueKind == JsonValueKind.Array)
                    axes.AddRange(axisList.EnumerateArray().Select(a => ReadJsonString(a, "Input")).Where(a => a.Length > 0));
                string device = ReadJsonString(item, "Device");
                DeviceReferenceDevices.Add(new ReferenceDeviceItem
                {
                    Device = device,
                    DisplayName = DeviceProbe.RemoveGuid(device),
                    ModelId = ReadJsonString(item, "ModelId"),
                    Generic = item.TryGetProperty("Generic", out JsonElement generic) && generic.ValueKind == JsonValueKind.True,
                    Probe = new DeviceProbe
                    {
                        VidPid = ReadJsonString(item, "VidPid"),
                        ProductName = ReadJsonString(item, "Name"),
                        InstanceName = device,
                        Buttons = ReadJsonInt(item, "Buttons"),
                        Hats = ReadJsonInt(item, "Hats"),
                        Axes = axes
                    }
                });
            }
        }
        catch (JsonException)
        {
        }
    }

    // Shows the reference of a connected device (JoyPro device name with GUID, or its name without it) with the binds
    // of the open profile.
    public async Task ShowDeviceReferenceForAsync(string device)
    {
        string wanted = (device ?? string.Empty).Trim();
        if (wanted.Length == 0)
            return;
        if (!referenceDevicesLoaded)
            await LoadDeviceReferenceDevicesAsync();
        ReferenceDeviceItem? item = DeviceReferenceDevices.FirstOrDefault(d => string.Equals(d.Device, wanted, StringComparison.OrdinalIgnoreCase))
                                    ?? DeviceReferenceDevices.FirstOrDefault(d => string.Equals(d.DisplayName, DeviceProbe.RemoveGuid(wanted), StringComparison.OrdinalIgnoreCase));
        DeviceProbe probe = item?.Probe ?? new DeviceProbe { InstanceName = wanted };
        // What the user said the device is (a grip for a base, a model, or a kind of device for an unknown one; kept
        // from an earlier answer) wins over what the engine found.
        string? grip = DeviceReferenceService.ChoiceOf(item?.Device ?? wanted);
        if (grip != null && (grip.StartsWith(DeviceReferenceService.ClassChoicePrefix, StringComparison.OrdinalIgnoreCase) || DeviceReferenceService.Catalog.Find(grip) != null))
            await DeviceReference.ShowDeviceAsync(probe, item?.Device ?? wanted, grip, pickedGrip: true);
        else
            await DeviceReference.ShowDeviceAsync(probe, item?.Device ?? wanted, item != null && !item.Generic ? item.ModelId : null);
        DeviceReference.SetBindings(DeviceReferenceBindings(item?.Device ?? wanted));
        RebuildWorkspacePictures();
        RebuildReferenceLegend();
        NotifyWorkspacePicture();
    }

    // What each input of a device does in the open profile: relation names (with modifiers) by input.
    public Dictionary<string, string> DeviceReferenceBindings(string device)
    {
        string name = DeviceProbe.RemoveGuid(device);
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, BindListItem> group in allBinds
                     .Where(b => b.HasBind && b.Input.Length > 0 &&
                                 (string.Equals(b.Device, device, StringComparison.OrdinalIgnoreCase) || string.Equals(DeviceProbe.RemoveGuid(b.Device), name, StringComparison.OrdinalIgnoreCase) ||
                                  SameDeviceName(b.Device, device)))
                     .GroupBy(b => b.Input.Trim().ToUpperInvariant()))
        {
            map[group.Key] = string.Join(" / ", group.Select(b => b.RelationName + (b.ModifiersSummary.Length > 0 && b.ModifiersSummary is not ("-" or "<none>") ? " (+" + b.ModifiersSummary + ")" : string.Empty)).Distinct());
        }
        return map;
    }
}
