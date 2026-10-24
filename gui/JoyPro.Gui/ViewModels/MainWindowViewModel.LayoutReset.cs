using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// "Reset to default" (docs/device-reference.md "Reset to default"): per device on the Layout page (its Model... menu
// and the Edit panel "Picture and markers"), and "Reset all my layout changes" in Settings > Drawings. Asks first; the
// changed files go into a backup (DeviceLayoutReset), the profile's layout file of the device is forgotten (engine
// layout image --clear); "Undo reset" puts everything back.
public sealed partial class MainWindowViewModel
{
    private DeviceLayoutReset.Backup? lastLayoutReset;

    [ObservableProperty]
    private bool canUndoLayoutReset;

    [ObservableProperty]
    private string layoutResetStatus = string.Empty;

    public string UndoLayoutResetLabel => lastLayoutReset == null ? T("Undo reset")
        : lastLayoutReset.Device.Length > 0 ? T("Undo the reset of {0}", DeviceProbe.RemoveGuid(lastLayoutReset.Device)) : T("Undo the reset of all layouts");

    [RelayCommand]
    private async Task ResetDeviceLayoutAsync(LayoutSuggestionItem? item)
    {
        item ??= SelectedLayoutSuggestion;
        string device = (item?.Device ?? string.Empty).Trim();
        if (item == null || device.Length == 0)
            return;
        if (!ReferenceEquals(SelectedLayoutSuggestion, item))
        {
            SelectedLayoutSuggestion = item;
            await ShowDeviceReferenceForAsync(device);
        }
        string model = deviceReference?.Model is { } shown && deviceReference.IsGeneric == false ? shown.Id : string.Empty;
        List<string> files = DeviceLayoutReset.FilesOf(model);
        string image = await LayoutImageOfAsync(device, null);
        bool own = item.IsOwnLayout || image.Length > 0;
        if (files.Count == 0 && !own && DeviceReferenceService.CalloutFileOf(device) == null)
        {
            LayoutResetStatus = T("{0} already shows JoyPro's default picture.", item.DisplayName);
            StatusText = LayoutResetStatus;
            return;
        }
        string what = T("Your marker positions, tables and lines, renamed parts, your photo and your own definition of this model ({0} files)", files.Count) +
                      (own ? T(", and the layout file the profile uses for it (the file itself stays)") : string.Empty) + ".";
        if (!await ConfirmAsync(T("Reset {0} to JoyPro's default?", item.DisplayName),
                T("JoyPro's own picture and positions come back. {0} Undo reset puts everything back.", what), T("Reset"), isDanger: true))
            return;
        DeviceLayoutReset.Backup backup = DeviceLayoutReset.Reset(files, [device], device, model);
        if (own)
        {
            string previous = await LayoutImageOfAsync(device, "--clear");
            if (previous.Length > 0)
                backup.LayoutImages[device] = previous;
        }
        await AfterLayoutResetAsync(backup, [device]);
        LayoutResetStatus = T("{0} shows JoyPro's default picture again. Undo reset puts your changes back.", item.DisplayName);
        StatusText = LayoutResetStatus;
    }

    [RelayCommand]
    private async Task ResetAllLayoutChangesAsync()
    {
        List<string> files = DeviceLayoutReset.AllFiles();
        List<string> devices = LayoutSuggestions.Select(s => (s.Device ?? string.Empty).Trim()).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Dictionary<string, string> images = [];
        foreach (string device in devices)
        {
            string image = await LayoutImageOfAsync(device, null);
            if (image.Length > 0)
                images[device] = image;
        }
        if (files.Count == 0 && images.Count == 0)
        {
            LayoutResetStatus = T("Your devices already show JoyPro's default pictures.");
            StatusText = LayoutResetStatus;
            return;
        }
        if (!await ConfirmAsync(T("Reset all your layout changes?"),
                T("Every device shows JoyPro's own picture and positions again: your marker positions, tables and lines, renamed parts, photos and own device definitions ({0} files) and the layout files the profile uses ({1}; the files themselves stay). Undo reset puts everything back.",
                    files.Count, images.Count), T("Reset all"), isDanger: true))
            return;
        DeviceLayoutReset.Backup backup = DeviceLayoutReset.Reset(files, devices);
        foreach (KeyValuePair<string, string> image in images)
        {
            string previous = await LayoutImageOfAsync(image.Key, "--clear");
            backup.LayoutImages[image.Key] = previous.Length > 0 ? previous : image.Value;
        }
        await AfterLayoutResetAsync(backup, devices);
        LayoutResetStatus = T("All your devices show JoyPro's default pictures again. Undo reset puts your changes back.");
        StatusText = LayoutResetStatus;
    }

    [RelayCommand]
    private async Task UndoLayoutResetAsync()
    {
        DeviceLayoutReset.Backup? backup = lastLayoutReset;
        if (backup == null)
            return;
        DeviceLayoutReset.Restore(backup);
        foreach (KeyValuePair<string, string> image in backup.LayoutImages.Where(i => i.Value.Length > 0))
            await LayoutImageOfAsync(image.Key, "--set", image.Value);
        lastLayoutReset = null;
        CanUndoLayoutReset = false;
        OnPropertyChanged(nameof(UndoLayoutResetLabel));
        await RefreshAfterLayoutChangeAsync(backup.Device.Length > 0 ? [backup.Device] : backup.CalloutPointers.Keys.ToList());
        // The cheat sheets use the user's sheets again (showing the picture pointed them at the one shown).
        foreach (KeyValuePair<string, string> pointer in backup.CalloutPointers)
            DeviceReferenceService.SetCalloutFileOf(pointer.Key, pointer.Value);
        LayoutResetStatus = T("Your layout changes are back.");
        StatusText = LayoutResetStatus;
    }

    private async Task AfterLayoutResetAsync(DeviceLayoutReset.Backup backup, List<string> devices)
    {
        lastLayoutReset = backup;
        CanUndoLayoutReset = true;
        OnPropertyChanged(nameof(UndoLayoutResetLabel));
        foreach (string device in devices)
        {
            layoutTemplateByDevice.Remove(device);
            savedLayoutDevices.Remove(device);
        }
        await RefreshAfterLayoutChangeAsync(devices);
    }

    // The Layout page again: the suggestions (own layouts), the shown device's picture, its sheet.
    private async Task RefreshAfterLayoutChangeAsync(List<string> devices)
    {
        string current = (SelectedLayoutSuggestion?.Device ?? string.Empty).Trim();
        calloutModel = string.Empty;
        calloutPictureKey = string.Empty;
        if (HasLayoutSuggestions)
            await SuggestLayoutsCommand.ExecuteAsync(null);
        LayoutSuggestionItem? again = LayoutSuggestions.FirstOrDefault(s => string.Equals((s.Device ?? string.Empty).Trim(), current, StringComparison.OrdinalIgnoreCase));
        if (again != null && !ReferenceEquals(SelectedLayoutSuggestion, again))
            SelectedLayoutSuggestion = again;
        if (current.Length > 0)
        {
            if (again != null)
                LayoutShowsLayoutFile = PrefersLayoutFile(again);
            await ShowDeviceReferenceForAsync(current);
            RefreshLayoutCallouts();
        }
        if (lastHudDevices.Count > 0)
            _ = RebuildDeviceCardsAsync();
    }

    private bool HasLayoutSuggestions => LayoutSuggestions.Count > 0;

    // engine layout image <device> [--clear|--set <file>]: the previous layout file (the current one without an option).
    private async Task<string> LayoutImageOfAsync(string device, string? option, string? value = null, string? busyLabel = null)
    {
        List<string> args = ["layout", "image", device];
        if (option != null)
            args.Add(option);
        if (value != null)
            args.Add(value);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: option != null,
            busyLabel: busyLabel ?? N("Resetting the device's picture..."), forceText: false, background: option == null);
        if (result == null || result.ExitCode != 0)
            return string.Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            return ReadJsonString(doc.RootElement, option == null ? "Current" : "Previous");
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
