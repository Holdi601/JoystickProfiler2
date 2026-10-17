using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JoyPro.Watch;

// Folders and names the GUI and the tray host share.
public static class JoyProPaths
{
    public const string GuiExeName = "JoyPro.Gui.exe";
    public const string TrayExeName = "JoyPro.Tray.exe";
    public const string TrayFolderName = "tray";

    // The GUI's settings (ui-settings.json): %APPDATA%\JoyPro2, or <JOYPRO_DATA_DIR>\settings (tests).
    public static string SettingsFolder
    {
        get
        {
            string? overrideDir = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
            return string.IsNullOrWhiteSpace(overrideDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JoyPro2")
                : Path.Combine(overrideDir, "settings");
        }
    }

    public static string UiSettingsFile(string settingsFolder) => Path.Combine(settingsFolder, "ui-settings.json");
}

// The names of one JoyPro2 installation's processes (per user session and settings folder, so a test run with its
// own JOYPRO_DATA_DIR never meets the user's JoyPro2).
public sealed class InstanceNames
{
    public InstanceNames(string settingsFolder)
    {
        SettingsFolder = settingsFolder;
        Gui = GuiNameFor(settingsFolder);
        Tray = "JoyPro2.Tray." + Gui["JoyPro2.Gui.".Length..];
    }

    public static string GuiNameFor(string settingsFolder)
    {
        string key = (settingsFolder ?? string.Empty).Trim().TrimEnd('\\', '/').ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return "JoyPro2.Gui." + Convert.ToHexString(hash, 0, 8);
    }

    public string SettingsFolder { get; }

    // SingleInstanceGuard name of the GUI (mutex "...Running", event "...Activate").
    public string Gui { get; }

    // SingleInstanceGuard name of the tray host.
    public string Tray { get; }

    // Set by the GUI after it started (its pid is in GuiPidFile): the tray host hands the watching over.
    public string GuiStartedEvent => @"Local\" + Gui + ".Started";

    public string GuiPidFile => Path.Combine(SettingsFolder, "gui.pid");

    // A second GUI start with --review leaves its game list here for the running GUI.
    public string GuiRequestFile => Path.Combine(SettingsFolder, "gui-request.txt");

    public static InstanceNames Current => new(JoyProPaths.SettingsFolder);
}

// The few ui-settings.json fields the tray host reads (and "watchGameConfigs", the one it writes). The GUI owns the
// file; the tray host changes only that field and keeps everything else as it is.
// CloseToTray: the tray host goes on watching after the GUI closed (otherwise it ends with the GUI).
// WatchChoices: the per-game "Watch for changes" ticks of the Start page ("watchedGames").
public sealed record TraySettings(bool WatchGameConfigs, bool StartWithWindows, bool CloseToTray, string LastProfilePath)
{
    public GameWatchChoices WatchChoices { get; init; } = GameWatchChoices.Empty;
}

public interface ITraySettingsStore
{
    TraySettings Load();

    void SaveWatchGameConfigs(bool value);
}

public sealed class UiSettingsFileStore : ITraySettingsStore
{
    private readonly string file;

    public UiSettingsFileStore(string file)
    {
        this.file = file;
    }

    public TraySettings Load()
    {
        JsonObject? root = Read();
        return new TraySettings(
            Bool(root, "watchGameConfigs", true),
            Bool(root, "startWithWindows", false),
            Bool(root, "closeToTray", false),
            Text(root, "lastProfilePath"))
        {
            WatchChoices = Choices(root)
        };
    }

    // "watchedGames": { "DCS": false, ... } (a value that is no boolean is no choice).
    private static GameWatchChoices Choices(JsonObject? root)
    {
        if (Find(root, GameWatchChoices.SettingsKey) is not JsonObject games)
            return GameWatchChoices.Empty;
        List<KeyValuePair<string, bool>> choices = [];
        foreach (var pair in games)
        {
            if (pair.Value is JsonValue value && value.TryGetValue(out bool watched))
                choices.Add(new KeyValuePair<string, bool>(pair.Key, watched));
        }
        return new GameWatchChoices(choices);
    }

    public void SaveWatchGameConfigs(bool value)
    {
        JsonObject root = Read() ?? [];
        string? existing = null;
        foreach (var pair in root)
        {
            if (string.Equals(pair.Key, "watchGameConfigs", StringComparison.OrdinalIgnoreCase))
                existing = pair.Key;
        }
        root[existing ?? "watchGameConfigs"] = value;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, file, overwrite: true);
    }

    private JsonObject? Read()
    {
        try
        {
            return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static JsonNode? Find(JsonObject? root, string name)
    {
        if (root == null)
            return null;
        foreach (var pair in root)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }
        return null;
    }

    private static bool Bool(JsonObject? root, string name, bool fallback) =>
        Find(root, name) is JsonValue value && value.TryGetValue(out bool result) ? result : fallback;

    private static string Text(JsonObject? root, string name) =>
        Find(root, name) is JsonValue value && value.TryGetValue(out string? result) ? result ?? string.Empty : string.Empty;
}
