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
        nameof(ImportIncludeDisconnected), nameof(ImportIncludeSimDefaults), nameof(MsfsWriteUserProfiles), nameof(MsfsAlwaysImportFiles), nameof(ValidationShowErrors), nameof(ValidationShowWarnings), nameof(ExportDcsPath), nameof(ExportIl2Path), nameof(ExportStarCitizenPath), nameof(StarCitizenSourcePath), nameof(RegistryGamePath),
        nameof(ImportDcsPath), nameof(ImportIl2Path), nameof(ImportStarCitizenPath), nameof(DcsSavedGamesPath),
    };

    // The choices of how JoyPro writes: changed by the user, they are kept over JoyPro's later defaults (ModeChosenByUser).
    private static readonly HashSet<string> ExportModeChoiceProperties = new(StringComparer.Ordinal)
    {
        nameof(ExportMode), nameof(ExportNukeUntouchedDevices), nameof(ExportDcsCleanAllAircraft),
    };

    // The game paths are per PC (export-settings.json "paths", app-wide); a profile may keep its own (PathOverrides).
    // Property -> key. A path the user typed or picked is a hand path: JoyPro's detection never replaces it.
    private static readonly Dictionary<string, string> RememberedPathKeys = new(StringComparer.Ordinal)
    {
        [nameof(ExportDcsPath)] = "dcs", [nameof(ExportIl2Path)] = "il2", [nameof(ExportStarCitizenPath)] = "starCitizen",
        [nameof(StarCitizenSourcePath)] = "starCitizenSource", [nameof(ImportDcsPath)] = "dcsImport", [nameof(ImportIl2Path)] = "il2Import",
        [nameof(ImportStarCitizenPath)] = "starCitizenImport", [nameof(DcsSavedGamesPath)] = "dcsSavedGames",
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
        {
            InitializeImportSettings(persisted: false);
            return;
        }
        exportSettings = ExportSettingsStore.Load(ExportSettingsPath);
        ApplyRememberedExportSettings();
        exportSettingsReady = true;
        // The import options are kept in the same file (MainWindowViewModel.ImportSettings.cs).
        InitializeImportSettings(persisted: true);
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
        MsfsWriteUserProfiles = s.MsfsWriteUserProfiles;
        MsfsAlwaysImportFiles = s.MsfsAlwaysImportFiles;
        ValidationShowErrors = s.ValidationShowErrors;
        ValidationShowWarnings = s.ValidationShowWarnings;
        ApplyRememberedPaths(s.Paths);
    }

    private void ApplyRememberedPaths(Dictionary<string, string> paths)
    {
        string? Folder(string key) => paths.TryGetValue(key, out string? path) && path.Length > 0 && Directory.Exists(path) ? path : null;
        if (Folder("dcsSavedGames") is { } savedGames)
            DcsSavedGamesPath = savedGames;
        if (Folder("dcs") is { } dcs)
            ExportDcsPath = dcs;
        if (Folder("dcsImport") is { } dcsImport)
            ImportDcsPath = dcsImport;
        if (Folder("il2") is { } il2)
            ExportIl2Path = il2;
        if (Folder("il2Import") is { } il2Import)
            ImportIl2Path = il2Import;
        if (Folder("starCitizen") is { } sc)
            ExportStarCitizenPath = sc;
        if (Folder("starCitizenImport") is { } scImport)
            ImportStarCitizenPath = scImport;
        if (paths.TryGetValue("starCitizenSource", out string? source) && source.Length > 0 && (File.Exists(source) || Directory.Exists(source)))
            StarCitizenSourcePath = source;
    }

    // A path the user set by hand (typed or picked): JoyPro's detection leaves it alone.
    internal bool IsHandPath(string propertyName) =>
        RememberedPathKeys.TryGetValue(propertyName, out string? key) && exportSettings.HandPaths.Contains(key, StringComparer.OrdinalIgnoreCase);

    // JoyPro itself sets a path it detected (not a hand path).
    private bool adoptingDetectedPaths;

    private void OnExportSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == null || !RememberedExportProperties.Contains(e.PropertyName))
            return;
        if (e.PropertyName == nameof(RegistryGamePath))
        {
            RememberRegistryGamePath();
            return;
        }
        if (CanRememberExportSettings && !adoptingDetectedPaths)
        {
            if (ExportModeChoiceProperties.Contains(e.PropertyName))
                exportSettings.ModeChosenByUser = true;
            if (RememberedPathKeys.TryGetValue(e.PropertyName, out string? key) && !exportSettings.HandPaths.Contains(key, StringComparer.OrdinalIgnoreCase))
                exportSettings.HandPaths.Add(key);
        }
        SaveExportSettings();
    }

    private bool CanRememberExportSettings => exportSettingsReady && exportSettingsSuppressed == 0 && persistUiFiles;

    private void SaveExportSettings()
    {
        if (!CanRememberExportSettings)
            return;
        ExportSettings s = exportSettings;
        s.Mode = EffectiveExportMode;
        s.NukeUntouchedDevices = ExportNukeUntouchedDevices;
        s.DcsKeepKeyboardDefaults = ExportKeepKeyboardDefaults;
        s.DcsCleanAllAircraft = ExportDcsCleanAllAircraft;
        s.StarCitizenKeepKeyboardDefaults = ExportStarCitizenKeepKeyboardDefaults;
        s.StarCitizenPreferLocalOrder = ExportStarCitizenPreferLocalOrder;
        s.RegistryDryRun = RegistryGameDryRun;
        s.ImportIncludeDisconnected = ImportIncludeDisconnected;
        s.ImportIncludeSimDefaults = ImportIncludeSimDefaults;
        s.MsfsWriteUserProfiles = MsfsWriteUserProfiles;
        s.MsfsAlwaysImportFiles = MsfsAlwaysImportFiles;
        s.ValidationShowErrors = ValidationShowErrors;
        s.ValidationShowWarnings = ValidationShowWarnings;
        // The profile's own paths while it keeps them, else the PC's.
        Dictionary<string, string> paths = OwnPathsTicks()?.PathOverrides ?? s.Paths;
        foreach ((string property, string key) in RememberedPathKeys)
        {
            string value = PathValue(property);
            if (string.IsNullOrWhiteSpace(value))
                paths.Remove(key);
            else
                paths[key] = value.Trim();
        }
        ExportSettingsStore.Save(ExportSettingsPath, s);
    }

    private string PathValue(string property) => (property switch
    {
        nameof(ExportDcsPath) => ExportDcsPath,
        nameof(ExportIl2Path) => ExportIl2Path,
        nameof(ExportStarCitizenPath) => ExportStarCitizenPath,
        nameof(StarCitizenSourcePath) => StarCitizenSourcePath,
        nameof(ImportDcsPath) => ImportDcsPath,
        nameof(ImportIl2Path) => ImportIl2Path,
        nameof(ImportStarCitizenPath) => ImportStarCitizenPath,
        nameof(DcsSavedGamesPath) => DcsSavedGamesPath,
        _ => string.Empty
    }) ?? string.Empty;

    // ---- Paths and ignored devices: the PC's, or the open profile's own ----

    // The ticks of the open profile when it keeps its own paths and ignored devices.
    private ProfileExportTicks? OwnPathsTicks() => RememberedTicks(create: false) is { HasOwnPaths: true } ticks ? ticks : null;

    // "Own paths and ignored devices for this profile" (Export page, Options and paths): off = the PC's (every profile).
    public bool ProfileOwnPaths
    {
        get => OwnPathsTicks() != null;
        set
        {
            if (value == ProfileOwnPaths || !HasProfile)
                return;
            if (value)
            {
                if (RememberedTicks(create: true) is not { } ticks)
                    return;
                ticks.PathOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach ((string property, string key) in RememberedPathKeys)
                {
                    string path = PathValue(property).Trim();
                    if (path.Length > 0)
                        ticks.PathOverrides[key] = path;
                }
                ticks.IgnoreDevicesCsv = ImportIgnoreDevicesCsv?.Trim() ?? string.Empty;
                ticks.IgnoredDevices = ImportIgnoreDeviceOptions.Where(o => o.IsSelected).Select(o => o.Name).ToList();
                ticks.LastUsed = DateTime.UtcNow;
                ExportSettingsStore.Save(ExportSettingsPath, exportSettings);
            }
            else
            {
                ProfileExportTicks ticks = OwnPathsTicks()!;
                ticks.PathOverrides = null;
                ticks.IgnoreDevicesCsv = null;
                ticks.IgnoredDevices = null;
                ExportSettingsStore.Save(ExportSettingsPath, exportSettings);
                ApplyPathsOfProfile();
            }
            OnPropertyChanged();
        }
    }

    // The ignored devices remembered for the open profile (its own) or for the PC.
    private List<string> ActiveIgnoredDevices()
    {
        ProfileExportTicks? own = OwnPathsTicks();
        if (own != null)
            return own.IgnoredDevices ??= [];
        return exportSettings.Import.IgnoredDevices;
    }

    // A profile was opened: its own paths and ignored devices, else the PC's.
    private void ApplyPathsOfProfile()
    {
        if (!exportSettingsReady)
            return;
        ProfileExportTicks? own = OwnPathsTicks();
        using (SuppressExportSettings())
        {
            importSettingsApplying = true;
            try
            {
                ApplyRememberedPaths(own?.PathOverrides ?? exportSettings.Paths);
                ImportIgnoreDevicesCsv = own != null ? own.IgnoreDevicesCsv ?? string.Empty : exportSettings.Import.IgnoreDevicesCsv;
                HashSet<string> ignored = new(own != null ? own.IgnoredDevices ?? [] : exportSettings.Import.IgnoredDevices, StringComparer.OrdinalIgnoreCase);
                foreach (SelectableTagItem item in ImportIgnoreDeviceOptions)
                    item.IsSelected = ignored.Contains(item.Name);
            }
            finally
            {
                importSettingsApplying = false;
            }
        }
        OnPropertyChanged(nameof(ProfileOwnPaths));
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
