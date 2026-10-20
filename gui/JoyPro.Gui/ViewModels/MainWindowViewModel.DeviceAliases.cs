using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Device aliases (docs/devices.md "Device names"): on the Layout page, next to the device's icon, the user gives the
// shown device a name of his own ("Left panel"); it is kept in the profile (engine devices alias) and shown wherever a
// device name is shown (DisplayText.ShortDevice), with the real name in the tooltip. Settings > Devices switches
// between the aliases and the real names. Exports always use the real device.
public sealed partial class MainWindowViewModel
{
    // The alias box on the Layout page (the shown device's alias).
    [ObservableProperty]
    private string workspaceDeviceAlias = string.Empty;

    [ObservableProperty]
    private string deviceAliasStatus = string.Empty;

    // Settings > Devices: "Device names: show the alias" (on) or "show the device's real name" (off).
    [ObservableProperty]
    private bool showDeviceAliases = true;

    // The other choice of the setting (two radio buttons).
    public bool ShowDeviceRealNames
    {
        get => !ShowDeviceAliases;
        set => ShowDeviceAliases = !value;
    }

    // The device's real name next to the alias box ("alias of VPC Panel #2").
    public string WorkspaceDeviceRealName => DeviceAliases.WithoutGuid(IconDevice());

    public bool HasWorkspaceDeviceAlias => DeviceAliases.AliasOf(IconDevice()).Length > 0;

    partial void OnShowDeviceAliasesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDeviceRealNames));
        DeviceAliases.ShowAliases = value;
        OnDeviceNamesChanged();
        if (!loadingUiSettings)
            SaveUiSettings();
    }

    private void RefreshWorkspaceDeviceAlias()
    {
        WorkspaceDeviceAlias = DeviceAliases.AliasOf(IconDevice());
        OnPropertyChanged(nameof(WorkspaceDeviceRealName));
        OnPropertyChanged(nameof(HasWorkspaceDeviceAlias));
    }

    // The profile's aliases (after opening a profile and after devices moved to a new ID).
    internal async Task LoadDeviceAliasesAsync()
    {
        if (!HasProfile)
            return;
        EngineCommandResult? result = await RunEngineAsync(["devices", "alias", "list"], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Reading the device names..."), forceText: false, background: true);
        if (result == null || result.ExitCode != 0)
            return;
        if (DeviceAliases.TryLoad(ExtractJsonPayload(result.StandardOutput)))
        {
            DeviceAliases.IsConnected = device => ConnectedDevices.Contains(device, StringComparer.OrdinalIgnoreCase) ||
                                                  DeviceCards.Any(c => c.Connected && string.Equals(c.Device, device, StringComparison.OrdinalIgnoreCase));
            OnDeviceNamesChanged();
        }
    }

    [RelayCommand]
    private Task SaveDeviceAliasAsync() => SetDeviceAliasAsync(IconDevice(), WorkspaceDeviceAlias);

    [RelayCommand]
    private Task ClearDeviceAliasAsync() => SetDeviceAliasAsync(IconDevice(), string.Empty);

    // Sets (or with an empty alias takes back) the alias of a device in the profile.
    internal async Task<bool> SetDeviceAliasAsync(string device, string? alias)
    {
        string target = (device ?? string.Empty).Trim();
        string value = (alias ?? string.Empty).Trim();
        if (target.Length == 0)
        {
            DeviceAliasStatus = T("Pick a device first.");
            return false;
        }
        if (value == DeviceAliases.AliasOf(target) && value.Length > 0)
            return true;
        if (!HasProfile)
        {
            DeviceAliasStatus = T("Open or create a profile first: the device names are kept in it.");
            return false;
        }
        // Taking an alias back clears it where it is kept (the same device under its older ID).
        string key = value.Length == 0 ? DeviceAliases.StoredKeyOf(target) : target;
        List<string> args = value.Length == 0 ? ["devices", "alias", "clear", key] : ["devices", "alias", "set", target, value];
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true,
            busyLabel: N("Saving the device name..."), forceText: false,
            historyNote: value.Length == 0 ? N("Device name taken back") : N("Device named"));
        if (result == null || result.ExitCode != 0)
        {
            DeviceAliasStatus = T("The device name could not be saved.");
            return false;
        }
        DeviceAliases.Set(key, value.Length == 0 ? null : value);
        DeviceAliasStatus = value.Length == 0
            ? T("{0} shows its real name again.", DeviceAliases.WithoutGuid(target))
            : T("{0} is shown as {1}.", DeviceAliases.WithoutGuid(target), value);
        OnDeviceNamesChanged();
        return true;
    }

    // An alias changed or Settings switched between aliases and real names: every list that shows device names follows.
    private void OnDeviceNamesChanged()
    {
        foreach (DeviceCardItem card in DeviceCards)
            card.RefreshName();
        foreach (LayoutSuggestionItem suggestion in LayoutSuggestions)
            suggestion.RefreshName();
        foreach (HudWidget widget in HudWidgets.Where(w => w.Device != null))
            widget.Device!.RefreshName();
        foreach (RelationListItem relation in allRelations)
            relation.RefreshDeviceName();
        if (Binds.Count > 0 || bindRows.Count > 0)
            RebuildVisibleBinds();
        if (allRelations.Count > 0)
            RebuildVisibleRelations();
        RefreshWorkspaceDeviceAlias();
        OnPropertyChanged(nameof(LayoutWorkspaceDeviceTitle));
    }
}
