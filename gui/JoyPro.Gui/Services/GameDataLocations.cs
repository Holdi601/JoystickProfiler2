using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JoyPro.Gui.Services;

// Where the engine keeps the game lists built on this PC (the engine's GameDataPaths, docs/installs.md "Game data"):
// <data>\GameData (data = JOYPRO_DATA_DIR like the engine, else %LOCALAPPDATA%\JoyPro) holds DB\<Game>, CleanProfile
// and KeyboardCleanProfile; the engine folder holds JoyPro's own data (DB\Functions, DB\Devices, DB\Llm, ...). A game
// folder is read from <data>\GameData when it exists there, else from the engine folder (a development build's copy).
// The GUI only reads: the engine writes and migrates.
public static class GameDataLocations
{
    public const string FolderName = "GameData";

    private static readonly HashSet<string> ShippedDbFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Functions", "Devices", "Llm", "DevicePhotos", "DeviceLayouts", "DeviceDrawings", "Cockpits", "Detents"
    };

    public static string DataRoot()
    {
        string? root = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        return string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro")
            : root;
    }

    // The game-data folder of a data folder (dataRoot empty: DataRoot()).
    public static string GameDataRoot(string? dataRoot = null) =>
        Path.Combine(string.IsNullOrWhiteSpace(dataRoot) ? DataRoot() : dataRoot, FolderName);

    public static bool IsShippedDbFolder(string folder) => ShippedDbFolders.Contains((folder ?? string.Empty).Trim());

    // DB\<folder> to read: the game-data folder's when it exists, else the engine folder's (JoyPro's own DB folders
    // always come from the engine folder). Empty when neither is known. dataRoot: the data folder to look in (empty:
    // DataRoot()).
    public static string DbFolder(string? engineDirectory, string folder, string? dataRoot = null)
    {
        string program = string.IsNullOrWhiteSpace(engineDirectory) ? string.Empty : Path.Combine(engineDirectory, "DB", folder);
        if (IsShippedDbFolder(folder))
            return program;
        string user = Path.Combine(GameDataRoot(dataRoot), "DB", folder);
        if (Directory.Exists(user) || program.Length == 0)
            return user;
        return Directory.Exists(program) ? program : user;
    }

    // Every game DB folder of both places (one path per folder name, the game-data folder's first).
    public static IReadOnlyList<string> GameDbFolders(string? engineDirectory, string? dataRoot = null)
    {
        Dictionary<string, string> folders = new(StringComparer.OrdinalIgnoreCase);
        List<string> roots = [Path.Combine(GameDataRoot(dataRoot), "DB")];
        if (!string.IsNullOrWhiteSpace(engineDirectory))
            roots.Add(Path.Combine(engineDirectory, "DB"));
        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            try
            {
                foreach (string path in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(path);
                    if (!IsShippedDbFolder(name) && !folders.ContainsKey(name))
                        folders[name] = path;
                }
            }
            catch (Exception)
            {
                // An unreadable folder lists nothing.
            }
        }
        return folders.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(f => f.Value).ToList();
    }
}
