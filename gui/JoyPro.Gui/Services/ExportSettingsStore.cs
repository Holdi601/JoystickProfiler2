using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace JoyPro.Gui.Services;

// The Export page's last choices (docs/export.md "Remembered settings"), in <settings>\export-settings.json: how JoyPro
// writes into the games and the options per game for every profile alike; the paths the user chose; and per profile
// the games and DCS aircraft the user ticked or unticked themselves (only those: the other ticks follow the game, as
// on a fresh start). A broken or missing file gives JoyPro's defaults.
public sealed class ExportSettings
{
    public string Mode { get; set; } = "merge";
    public bool NukeUntouchedDevices { get; set; }
    public bool DcsKeepKeyboardDefaults { get; set; } = true;
    public bool DcsCleanAllAircraft { get; set; } = true;
    public bool StarCitizenKeepKeyboardDefaults { get; set; } = true;
    public bool StarCitizenPreferLocalOrder { get; set; } = true;
    public bool RegistryDryRun { get; set; }

    // MSFS import: also the devices that are not connected (off: MSFS keeps the binds of every device it ever saw).
    public bool ImportIncludeDisconnected { get; set; }

    // MSFS import: also the sim's default presets whose use the sim's records do not show (off: only the ones they name).
    public bool ImportIncludeSimDefaults { get; set; }

    // MSFS 2024 export: write the binds into the user's own active profiles (on), and import files for every device
    // and category (off: only where the user's profile could not be written).
    public bool MsfsWriteUserProfiles { get; set; } = true;
    public bool MsfsAlwaysImportFiles { get; set; }

    // The import choices of the Start page's import and the expert import (docs/reimport.md "Remembered import options").
    public ImportSettings Import { get; set; } = new();

    // The check list's filters (Show Errors / Show Warnings).
    public bool ValidationShowErrors { get; set; } = true;
    public bool ValidationShowWarnings { get; set; }

    // "dcs", "il2", "starCitizen", "starCitizenSource" -> the path the user had.
    public Dictionary<string, string> Paths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Registry game id -> its file or folder (the Export page's options; empty = detected).
    public Dictionary<string, string> RegistryPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Full profile path (lower case) -> the user's own ticks.
    public Dictionary<string, ProfileExportTicks> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

// The import options the user chose last, for every profile alike; the defaults are JoyPro's (include defaults off).
public sealed class ImportSettings
{
    // Start page import: add to the profile (on) or start over from the game.
    public bool AddToProfile { get; set; } = true;
    // Expert import: merge into the profile.
    public bool Merge { get; set; }
    public bool IncludeDefaults { get; set; }
    public bool DcsKeyboard { get; set; } = true;
    public bool StarCitizenKeyboard { get; set; }
    // The games JoyPro reads through its game list: game id -> take the keyboard keys.
    public Dictionary<string, bool> RegistryKeyboard { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IgnoreEasyPlanes { get; set; }
    public bool Inverted { get; set; }
    public bool Slider { get; set; }
    public bool Curvature { get; set; }
    public bool Deadzone { get; set; }
    public bool SaturationX { get; set; }
    public bool SaturationY { get; set; }
    public bool AllDcsPlanes { get; set; } = true;
    public bool UseSelectedDevices { get; set; }
    public string PlanesCsv { get; set; } = string.Empty;
    public string DevicesCsv { get; set; } = string.Empty;
    public string IgnoreDevicesCsv { get; set; } = string.Empty;
    // The ticked aircraft, devices and ignored devices of the lists (kept also while one is not shown).
    public List<string> SelectedPlanes { get; set; } = [];
    public List<string> SelectedDevices { get; set; } = [];
    public List<string> IgnoredDevices { get; set; } = [];
}

public sealed class ProfileExportTicks
{
    // Game id -> ticked for "Export ticked games" (only where the user changed JoyPro's tick).
    public Dictionary<string, bool> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // DCS aircraft -> ticked (only where the user changed it; aircraft start ticked).
    public Dictionary<string, bool> Planes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime LastUsed { get; set; }
}

public static class ExportSettingsStore
{
    // Profiles kept (the most recently used ones).
    public const int MaxProfiles = 50;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string DefaultPath => Path.Combine(AppInfo.SettingsFolder, "export-settings.json");

    public static ExportSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new ExportSettings();
            ExportSettings? settings = JsonSerializer.Deserialize<ExportSettings>(File.ReadAllText(path), Json);
            if (settings == null)
                return new ExportSettings();
            // The dictionaries from the file compare case-insensitively like new ones.
            settings.Paths = new Dictionary<string, string>(settings.Paths ?? [], StringComparer.OrdinalIgnoreCase);
            settings.RegistryPaths = new Dictionary<string, string>(settings.RegistryPaths ?? [], StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ProfileExportTicks> profiles = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string key, ProfileExportTicks ticks) in settings.Profiles ?? [])
            {
                if (ticks == null)
                    continue;
                profiles[key] = new ProfileExportTicks
                {
                    Games = new Dictionary<string, bool>(ticks.Games ?? [], StringComparer.OrdinalIgnoreCase),
                    Planes = new Dictionary<string, bool>(ticks.Planes ?? [], StringComparer.OrdinalIgnoreCase),
                    LastUsed = ticks.LastUsed
                };
            }
            settings.Profiles = profiles;
            ImportSettings import = settings.Import ?? new ImportSettings();
            import.RegistryKeyboard = new Dictionary<string, bool>(import.RegistryKeyboard ?? [], StringComparer.OrdinalIgnoreCase);
            import.PlanesCsv ??= string.Empty;
            import.DevicesCsv ??= string.Empty;
            import.IgnoreDevicesCsv ??= string.Empty;
            import.SelectedPlanes ??= [];
            import.SelectedDevices ??= [];
            import.IgnoredDevices ??= [];
            settings.Import = import;
            if (string.IsNullOrWhiteSpace(settings.Mode))
                settings.Mode = "merge";
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new ExportSettings();
        }
    }

    public static void Save(string path, ExportSettings settings)
    {
        try
        {
            foreach (string old in settings.Profiles.OrderByDescending(p => p.Value.LastUsed).Skip(MaxProfiles).Select(p => p.Key).ToList())
                settings.Profiles.Remove(old);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The choices just are not remembered this time.
        }
    }

    public static string ProfileKey(string profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath))
            return string.Empty;
        try
        {
            return Path.GetFullPath(profilePath.Trim()).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return profilePath.Trim().ToLowerInvariant();
        }
    }

    // The ticks of a profile (made on first use).
    public static ProfileExportTicks For(ExportSettings settings, string profilePath)
    {
        string key = ProfileKey(profilePath);
        if (!settings.Profiles.TryGetValue(key, out ProfileExportTicks? ticks))
        {
            ticks = new ProfileExportTicks();
            if (key.Length > 0)
                settings.Profiles[key] = ticks;
        }
        return ticks;
    }

    // A tick the user set: kept when it differs from JoyPro's own tick, forgotten when it is the same again.
    public static void SetTick(Dictionary<string, bool> ticks, string key, bool value, bool defaultValue)
    {
        if (value == defaultValue)
            ticks.Remove(key);
        else
            ticks[key] = value;
    }
}
