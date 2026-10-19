using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The Export page remembers its last settings (Services/ExportSettingsStore.cs, export-settings.json): the export
// mode, the options per game and the paths for every profile; the games and DCS aircraft the user ticked or unticked
// per profile. They come back at the next start and when the profile is opened again. A remembered path is only used
// while it still exists.
public sealed partial class MainWindowViewModel
{
    private static readonly HashSet<string> RememberedExportProperties = new(StringComparer.Ordinal)
    {
        nameof(ExportMode), nameof(ExportNukeUntouchedDevices), nameof(ExportKeepKeyboardDefaults), nameof(ExportDcsCleanAllAircraft),
        nameof(ExportStarCitizenKeepKeyboardDefaults), nameof(ExportStarCitizenPreferLocalOrder), nameof(RegistryGameDryRun),
        nameof(ImportIncludeDisconnected), nameof(ImportIncludeSimDefaults), nameof(ValidationShowErrors), nameof(ValidationShowWarnings), nameof(ExportDcsPath), nameof(ExportIl2Path), nameof(ExportStarCitizenPath), nameof(StarCitizenSourcePath), nameof(RegistryGamePath),
    };

    private ExportSettings exportSettings = new();
    private string ExportSettingsPath { get; } = ExportSettingsStore.DefaultPath;
    // Off until the remembered settings are applied (the constructor's defaults are no user choice), and while JoyPro
    // itself changes a value (a dry-run preview, "Export" on a card).
    private bool exportSettingsReady;
    private int exportSettingsSuppressed;
    // The profile the export tiles belong to (their ticks are not carried over to another profile).
    private string exportTilesProfile = string.Empty;

    // After the paths are prefilled (constructor): the remembered choices on top.
    private void InitializeExportSettings(bool persisted)
    {
        PropertyChanged += OnExportSettingChanged;
        if (!persisted)
            return;
        exportSettings = ExportSettingsStore.Load(ExportSettingsPath);
        ApplyRememberedExportSettings();
        exportSettingsReady = true;
    }

    private void ApplyRememberedExportSettings()
    {
        ExportSettings s = exportSettings;
        if (ExportModeOptions.Any(o => string.Equals(o.Key, s.Mode, StringComparison.OrdinalIgnoreCase)))
            ExportMode = s.Mode;
        ExportNukeUntouchedDevices = s.NukeUntouchedDevices;
        ExportKeepKeyboardDefaults = s.DcsKeepKeyboardDefaults;
        ExportDcsCleanAllAircraft = s.DcsCleanAllAircraft;
        ExportStarCitizenKeepKeyboardDefaults = s.StarCitizenKeepKeyboardDefaults;
        ExportStarCitizenPreferLocalOrder = s.StarCitizenPreferLocalOrder;
        RegistryGameDryRun = s.RegistryDryRun;
        ImportIncludeDisconnected = s.ImportIncludeDisconnected;
        ImportIncludeSimDefaults = s.ImportIncludeSimDefaults;
        ValidationShowErrors = s.ValidationShowErrors;
        ValidationShowWarnings = s.ValidationShowWarnings;
        if (RememberedFolder("dcs") is { } dcs)
            ExportDcsPath = dcs;
        if (RememberedFolder("il2") is { } il2)
            ExportIl2Path = il2;
        if (RememberedFolder("starCitizen") is { } sc)
            ExportStarCitizenPath = sc;
        if (s.Paths.TryGetValue("starCitizenSource", out string? source) && source.Length > 0 && (File.Exists(source) || Directory.Exists(source)))
            StarCitizenSourcePath = source;
    }

    private string? RememberedFolder(string key) =>
        exportSettings.Paths.TryGetValue(key, out string? path) && path.Length > 0 && Directory.Exists(path) ? path : null;

    private void OnExportSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == null || !RememberedExportProperties.Contains(e.PropertyName))
            return;
        if (e.PropertyName == nameof(RegistryGamePath))
        {
            RememberRegistryGamePath();
            return;
        }
        SaveExportSettings();
    }

    private bool CanRememberExportSettings => exportSettingsReady && exportSettingsSuppressed == 0 && persistUiFiles;

    private void SaveExportSettings()
    {
        if (!CanRememberExportSettings)
            return;
        ExportSettings s = exportSettings;
        s.Mode = string.IsNullOrWhiteSpace(ExportMode) ? "merge" : ExportMode.Trim();
        s.NukeUntouchedDevices = ExportNukeUntouchedDevices;
        s.DcsKeepKeyboardDefaults = ExportKeepKeyboardDefaults;
        s.DcsCleanAllAircraft = ExportDcsCleanAllAircraft;
        s.StarCitizenKeepKeyboardDefaults = ExportStarCitizenKeepKeyboardDefaults;
        s.StarCitizenPreferLocalOrder = ExportStarCitizenPreferLocalOrder;
        s.RegistryDryRun = RegistryGameDryRun;
        s.ImportIncludeDisconnected = ImportIncludeDisconnected;
        s.ImportIncludeSimDefaults = ImportIncludeSimDefaults;
        s.ValidationShowErrors = ValidationShowErrors;
        s.ValidationShowWarnings = ValidationShowWarnings;
        void Path(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                s.Paths.Remove(key);
            else
                s.Paths[key] = value.Trim();
        }
        Path("dcs", ExportDcsPath);
        Path("il2", ExportIl2Path);
        Path("starCitizen", ExportStarCitizenPath);
        Path("starCitizenSource", StarCitizenSourcePath);
        ExportSettingsStore.Save(ExportSettingsPath, s);
    }

    // The path box of a registry game is per game: remembered for the selected one, put back when it is picked again.
    private void RememberRegistryGamePath()
    {
        if (!CanRememberExportSettings || SelectedRegistryGame == null || restoringRegistryGamePath)
            return;
        if (string.IsNullOrWhiteSpace(RegistryGamePath))
            exportSettings.RegistryPaths.Remove(SelectedRegistryGame.GameId);
        else
            exportSettings.RegistryPaths[SelectedRegistryGame.GameId] = RegistryGamePath.Trim();
        ExportSettingsStore.Save(ExportSettingsPath, exportSettings);
    }

    private bool restoringRegistryGamePath;

    // Called after OnSelectedRegistryGameChanged cleared the path box.
    private void RestoreRegistryGamePath(RegistryGameItem? game)
    {
        if (game == null || !exportSettings.RegistryPaths.TryGetValue(game.GameId, out string? path) || path.Length == 0 || !(File.Exists(path) || Directory.Exists(path)))
            return;
        restoringRegistryGamePath = true;
        try
        {
            RegistryGamePath = path;
        }
        finally
        {
            restoringRegistryGamePath = false;
        }
    }

    // JoyPro changes an option for a moment (a dry-run preview, "Export" on a card): not the user's choice.
    private IDisposable SuppressExportSettings()
    {
        exportSettingsSuppressed++;
        return new Releaser(() => exportSettingsSuppressed--);
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private Action? release = release;

        public void Dispose()
        {
            release?.Invoke();
            release = null;
        }
    }

    // ---- The ticks of the tiles, per profile ----

    private ProfileExportTicks? RememberedTicks(bool create)
    {
        if (!persistUiFiles || !HasProfile)
            return null;
        string key = ExportSettingsStore.ProfileKey(ProfilePath);
        if (!create && !exportSettings.Profiles.ContainsKey(key))
            return null;
        return ExportSettingsStore.For(exportSettings, ProfilePath);
    }

    // The ticks a refresh of the tiles starts from: the user's own ticks of this profile (remembered), then the ticks
    // changed on the tiles shown now when they belong to the same profile.
    private (Dictionary<string, bool> Games, Dictionary<string, bool> Planes, bool SameProfile) ExportTicksForRefresh()
    {
        bool sameProfile = string.Equals(exportTilesProfile, ProfilePath, StringComparison.OrdinalIgnoreCase);
        Dictionary<string, bool> games = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, bool> planes = new(StringComparer.OrdinalIgnoreCase);
        if (RememberedTicks(create: false) is { } remembered)
        {
            foreach ((string game, bool tick) in remembered.Games)
                games[game] = tick;
            foreach ((string plane, bool tick) in remembered.Planes)
                planes[plane] = tick;
        }
        if (sameProfile)
        {
            foreach (ExportGameItem game in ExportGames.Where(g => g.IsSelected != g.DefaultSelected))
                games[game.GameId] = game.IsSelected;
            foreach (ExportPlaneItem plane in ExportGames.SelectMany(g => g.Planes))
                planes[plane.Plane] = plane.IsSelected;
        }
        return (games, planes, sameProfile);
    }

    // The new tiles: a tick the user changes from now on is remembered for the profile.
    private void WatchExportTiles(IEnumerable<ExportGameItem> games)
    {
        exportTilesProfile = ProfilePath;
        foreach (ExportGameItem game in games)
        {
            game.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ExportGameItem.IsSelected))
                    RememberGameTick(game);
            };
            foreach (ExportPlaneItem plane in game.Planes)
            {
                plane.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ExportPlaneItem.IsSelected))
                        RememberPlaneTick(plane);
                };
            }
        }
    }

    private void RememberGameTick(ExportGameItem game)
    {
        if (!CanRememberExportSettings || !ExportGames.Contains(game) || RememberedTicks(create: true) is not { } ticks)
            return;
        ExportSettingsStore.SetTick(ticks.Games, game.GameId, game.IsSelected, game.DefaultSelected);
        ticks.LastUsed = DateTime.UtcNow;
        ExportSettingsStore.Save(ExportSettingsPath, exportSettings);
    }

    private void RememberPlaneTick(ExportPlaneItem plane)
    {
        if (!CanRememberExportSettings || RememberedTicks(create: true) is not { } ticks)
            return;
        ExportSettingsStore.SetTick(ticks.Planes, plane.Plane, plane.IsSelected, defaultValue: true);
        ticks.LastUsed = DateTime.UtcNow;
        ExportSettingsStore.Save(ExportSettingsPath, exportSettings);
    }

    // For the status line and the tests: what the Export page remembers for the open profile.
    internal string RememberedExportSummary()
    {
        ProfileExportTicks? ticks = RememberedTicks(create: false);
        return T("mode {0}, {1}", exportSettings.Mode, (ticks == null ? T("no own ticks") :
            T("{0} game tick(s) [{1}], {2} aircraft tick(s)", ticks.Games.Count, string.Join(", ", ticks.Games.Select(t => t.Key + "=" + t.Value)), ticks.Planes.Count)));
    }
}
