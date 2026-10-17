using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JoyPro.Gui.Models;

// The installation folder for what the user picked, the same way the engine resolves it (GameInstalls.ResolveInstallFolder):
// the folder itself when one of the game's markers is in it; else a subfolder with one ("game" first: the standalone
// IL-2 Korea keeps the game in <launcher folder>\game); else a parent folder (up to four levels, or its "game"
// subfolder) with one. A file (the game's or the launcher's .exe) counts as its folder.
public static class GameInstallFolder
{
    public static string Resolve(IEnumerable<string>? markers, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        List<string> list = (markers ?? []).ToList();
        string folder = path.Trim().Trim('"');
        try
        {
            folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder));
            if (File.Exists(folder))
                folder = Path.GetDirectoryName(folder) ?? folder;
            folder = folder.TrimEnd('\\', '/');
            if (folder.EndsWith(':'))
                folder += "\\";
            if (list.Count == 0 || !Directory.Exists(folder) || FoundMarker(list, folder) != null)
                return folder;
            string? child = Directory.GetDirectories(folder)
                .OrderBy(d => string.Equals(Path.GetFileName(d), "game", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(d => FoundMarker(list, d) != null);
            if (child != null)
                return child;
            DirectoryInfo? parent = Directory.GetParent(folder);
            for (int level = 0; level < 4 && parent != null; level++, parent = parent.Parent)
            {
                if (FoundMarker(list, parent.FullName) != null)
                    return parent.FullName;
                string game = Path.Combine(parent.FullName, "game");
                if (FoundMarker(list, game) != null)
                    return game;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // No access: what was picked stays the folder.
        }
        return folder;
    }

    public static string? FoundMarker(IEnumerable<string> markers, string folder)
    {
        try
        {
            return markers.FirstOrDefault(m => File.Exists(Path.Combine(folder, m)) || Directory.Exists(Path.Combine(folder, m)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
