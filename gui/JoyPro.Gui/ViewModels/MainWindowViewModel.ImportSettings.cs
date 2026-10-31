using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The import options are remembered (export-settings.json, "import"; docs/reimport.md "Remembered import options"):
// the Start page import (add to the profile or start over, the keyboard per game) and every option of the expert import
// (include defaults stays off until the user ticks it, the axis values, *_easy aircraft, the aircraft and device lists
// and the ignored devices). They come back at the next start. The DCS device options (only connected, same name as one)
// are kept in ui-settings.json, the MSFS ones with the export settings.
public sealed partial class MainWindowViewModel
{
    private static readonly HashSet<string> RememberedImportProperties = new(StringComparer.Ordinal)
    {
        nameof(ImportWizardAddToProfile), nameof(ImportMerge), nameof(ImportIncludeDefaults), nameof(ImportKeyboardCommands),
        nameof(ImportStarCitizenIncludeKeyboard), nameof(ImportIgnoreEasyPlanes), nameof(ImportInverted), nameof(ImportSlider),
        nameof(ImportCurvature), nameof(ImportDeadzone), nameof(ImportSaturationX), nameof(ImportSaturationY),
        nameof(ImportAllDcsPlanes), nameof(ImportUseSelectedDevices), nameof(ImportPlanesCsv), nameof(ImportDevicesCsv),
        nameof(ImportIgnoreDevicesCsv), nameof(RegistryGameIncludeKeyboard), nameof(ImportWizardKeyboard), nameof(SelectedRegistryGame),
        nameof(VJoyTunnelDevice),
    };

    private bool importSettingsApplying;

    // Called by InitializeExportSettings once the remembered export settings are loaded.
    private void InitializeImportSettings(bool persisted)
    {
        PropertyChanged += OnImportSettingChanged;
        WatchImportSelection(ImportPlaneOptions, () => exportSettings.Import.SelectedPlanes);
        WatchImportSelection(ImportDeviceOptions, () => exportSettings.Import.SelectedDevices);
        // The ignored devices are per PC, or the open profile's own (MainWindowViewModel.ExportSettings.cs).
        WatchImportSelection(ImportIgnoreDeviceOptions, ActiveIgnoredDevices);
        if (!persisted)
            return;
        ApplyRememberedImportSettings();
    }

    private void ApplyRememberedImportSettings()
    {
        ImportSettings s = exportSettings.Import;
        importSettingsApplying = true;
        try
        {
            ImportWizardAddToProfile = s.AddToProfile;
            ImportMerge = s.Merge;
            ImportIncludeDefaults = s.IncludeDefaults;
            ImportKeyboardCommands = s.DcsKeyboard;
            ImportStarCitizenIncludeKeyboard = s.StarCitizenKeyboard;
            ImportIgnoreEasyPlanes = s.IgnoreEasyPlanes;
            ImportInverted = s.Inverted;
            ImportSlider = s.Slider;
            ImportCurvature = s.Curvature;
            ImportDeadzone = s.Deadzone;
            ImportSaturationX = s.SaturationX;
            ImportSaturationY = s.SaturationY;
            ImportAllDcsPlanes = s.AllDcsPlanes;
            ImportUseSelectedDevices = s.UseSelectedDevices;
            ImportPlanesCsv = s.PlanesCsv;
            ImportDevicesCsv = s.DevicesCsv;
            ImportIgnoreDevicesCsv = s.IgnoreDevicesCsv;
            VJoyTunnelDevice = s.VJoyTunnelDevice;
            if (SelectedRegistryGame != null)
                RegistryGameIncludeKeyboard = RememberedRegistryKeyboard(SelectedRegistryGame.GameId);
            ApplyRememberedSelection(ImportPlaneOptions, s.SelectedPlanes);
            ApplyRememberedSelection(ImportDeviceOptions, s.SelectedDevices);
            ApplyRememberedSelection(ImportIgnoreDeviceOptions, ActiveIgnoredDevices());
        }
        finally
        {
            importSettingsApplying = false;
        }
    }

    // The keyboard choice of a game JoyPro reads through its game list (off until the user ticks it for that game).
    private bool RememberedRegistryKeyboard(string gameId) =>
        !string.IsNullOrWhiteSpace(gameId) && exportSettings.Import.RegistryKeyboard.TryGetValue(gameId, out bool keyboard) && keyboard;

    private bool CanRememberImportSettings => CanRememberExportSettings && !importSettingsApplying;

    private void OnImportSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == null || !RememberedImportProperties.Contains(e.PropertyName) || importSettingsApplying)
            return;
        ImportSettings s = exportSettings.Import;
        switch (e.PropertyName)
        {
            case nameof(SelectedRegistryGame):
                // The keyboard box of the expert form follows the game picked there.
                if (SelectedRegistryGame != null && exportSettingsReady)
                {
                    importSettingsApplying = true;
                    try
                    {
                        RegistryGameIncludeKeyboard = RememberedRegistryKeyboard(SelectedRegistryGame.GameId);
                    }
                    finally
                    {
                        importSettingsApplying = false;
                    }
                }
                return;
            case nameof(ImportWizardKeyboard):
                // The Start page's keyboard box is the game's own choice right away (also when the user goes back).
                if (ImportWizardGame == null)
                    return;
                switch (ImportWizardGame.Kind)
                {
                    case "dcs":
                        ImportKeyboardCommands = ImportWizardKeyboard;
                        break;
                    case "starcitizen":
                        ImportStarCitizenIncludeKeyboard = ImportWizardKeyboard;
                        break;
                    case "il2":
                        return;
                    default:
                        s.RegistryKeyboard[ImportWizardGame.GameId] = ImportWizardKeyboard;
                        break;
                }
                break;
            case nameof(ImportIncludeDefaults):
                // A tick or untick by the user stays over JoyPro's default (on since 2.5.0).
                s.IncludeDefaultsChosen = true;
                break;
            case nameof(RegistryGameIncludeKeyboard):
                // The Start page sets it for the game it imports (already remembered above); the expert form for its game.
                if (ImportWizardStep == 2 || SelectedRegistryGame == null)
                    return;
                s.RegistryKeyboard[SelectedRegistryGame.GameId] = RegistryGameIncludeKeyboard;
                break;
        }
        SaveImportSettings();
    }

    private void SaveImportSettings()
    {
        if (!CanRememberImportSettings)
            return;
        ImportSettings s = exportSettings.Import;
        s.AddToProfile = ImportWizardAddToProfile;
        s.Merge = ImportMerge;
        s.IncludeDefaults = ImportIncludeDefaults;
        s.DcsKeyboard = ImportKeyboardCommands;
        s.StarCitizenKeyboard = ImportStarCitizenIncludeKeyboard;
        s.IgnoreEasyPlanes = ImportIgnoreEasyPlanes;
        s.Inverted = ImportInverted;
        s.Slider = ImportSlider;
        s.Curvature = ImportCurvature;
        s.Deadzone = ImportDeadzone;
        s.SaturationX = ImportSaturationX;
        s.SaturationY = ImportSaturationY;
        s.AllDcsPlanes = ImportAllDcsPlanes;
        s.UseSelectedDevices = ImportUseSelectedDevices;
        s.PlanesCsv = ImportPlanesCsv?.Trim() ?? string.Empty;
        s.DevicesCsv = ImportDevicesCsv?.Trim() ?? string.Empty;
        if (OwnPathsTicks() is { } own)
            own.IgnoreDevicesCsv = ImportIgnoreDevicesCsv?.Trim() ?? string.Empty;
        else
            s.IgnoreDevicesCsv = ImportIgnoreDevicesCsv?.Trim() ?? string.Empty;
        s.VJoyTunnelDevice = VJoyTunnelDevice;
        ExportSettingsStore.Save(ExportSettingsPath, exportSettings);
    }

    // A list of aircraft or devices: a tick the user sets is remembered (names not shown now stay remembered), and an
    // entry the list gets again comes back ticked.
    private void WatchImportSelection(ObservableCollection<SelectableTagItem> list, Func<List<string>> remembered)
    {
        void Watch(SelectableTagItem item)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(SelectableTagItem.IsSelected) || importSettingsApplying || !list.Contains(item))
                    return;
                List<string> names = remembered();
                names.RemoveAll(n => string.Equals(n, item.Name, StringComparison.OrdinalIgnoreCase));
                if (item.IsSelected)
                    names.Add(item.Name);
                SaveImportSettings();
            };
            if (exportSettingsReady && !item.IsSelected && remembered().Contains(item.Name, StringComparer.OrdinalIgnoreCase))
            {
                importSettingsApplying = true;
                try
                {
                    item.IsSelected = true;
                }
                finally
                {
                    importSettingsApplying = false;
                }
            }
        }

        foreach (SelectableTagItem item in list)
            Watch(item);
        list.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Replace)
                foreach (SelectableTagItem item in e.NewItems?.OfType<SelectableTagItem>() ?? [])
                    Watch(item);
        };
    }

    private static void ApplyRememberedSelection(IEnumerable<SelectableTagItem> list, List<string> remembered)
    {
        HashSet<string> names = new(remembered, StringComparer.OrdinalIgnoreCase);
        foreach (SelectableTagItem item in list)
            if (names.Contains(item.Name))
                item.IsSelected = true;
    }
}
