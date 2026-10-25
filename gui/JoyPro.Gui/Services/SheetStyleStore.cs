using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Services;

// Where the cheat sheet styles live (docs/ui/layout-redesign.md, "Sheet style"):
//   the global style       <settings>\sheet-style.json (survives a restart; a test's JOYPRO_DATA_DIR keeps its own)
//   the user's presets     <data>\SheetStyles\<name>.json (named; the four built-in presets are not files)
//   a device's own style   inside its sheet ("style" of the callouts, CalloutSheet.Style) or, for a .layout file,
//                          "<layout>.style.json" next to it; both are what the engine reads for that device.
public static class SheetStyleStore
{
    public static string GlobalFile => Path.Combine(AppInfo.SettingsFolder, "sheet-style.json");

    public static string PresetFolder => Path.Combine(AppInfo.DataFolder, "SheetStyles");

    // The built-in presets with their shown names (English; translated where shown).
    public static readonly (string Id, string Title)[] BuiltIn =
    [
        ("joypro", N("JoyPro default")),
        ("print", N("Print")),
        ("high-contrast", N("High contrast")),
        ("dark-sheet", N("Dark sheet"))
    ];

    // A style with every value set: the missing ones from JoyPro's default (a file of an older version, a partial
    // style from the command line).
    public static SheetStyle Complete(SheetStyle? style)
    {
        SheetStyle full = SheetStyle.Preset("joypro");
        if (style == null)
            return full;
        full.Name = style.Name;
        full.Overlay(style);
        if (style.FontFamily.Length == 0)
            full.FontFamily = string.Empty;
        full.Drawing = style.Drawing;
        return full;
    }

    public static SheetStyle LoadGlobal()
    {
        try
        {
            if (File.Exists(GlobalFile) && SheetStyle.Parse(File.ReadAllText(GlobalFile, Encoding.UTF8)) is { } loaded)
                return Complete(loaded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return SheetStyle.Preset("joypro");
    }

    public static void SaveGlobal(SheetStyle style) => Write(GlobalFile, style);

    private static void Write(string file, SheetStyle style)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, style.ToJson(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------ presets

    public static List<SheetStyle> UserPresets()
    {
        List<SheetStyle> presets = [];
        if (!Directory.Exists(PresetFolder))
            return presets;
        foreach (string file in Directory.EnumerateFiles(PresetFolder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (SheetStyle.Parse(File.ReadAllText(file, Encoding.UTF8)) is { } style)
                {
                    if (style.Name.Length == 0)
                        style.Name = Path.GetFileNameWithoutExtension(file);
                    presets.Add(Complete(style));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return presets;
    }

    // The style saved under the name (an existing preset of that name is replaced). Returns the file.
    public static string SavePreset(SheetStyle style, string name)
    {
        string clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
            throw new ArgumentException("A preset needs a name.", nameof(name));
        SheetStyle copy = style.Clone();
        copy.Name = clean;
        string file = PresetFile(clean);
        Directory.CreateDirectory(PresetFolder);
        File.WriteAllText(file, copy.ToJson(), new UTF8Encoding(false));
        return file;
    }

    public static bool DeletePreset(string name)
    {
        string file = PresetFile(name);
        if (!File.Exists(file))
            return false;
        File.Delete(file);
        return true;
    }

    public static bool IsBuiltInName(string name) =>
        BuiltIn.Any(b => string.Equals(b.Id, name?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(b.Title, name?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(T(b.Title), name?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string PresetFile(string name)
    {
        string safe = new((name ?? string.Empty).Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return Path.Combine(PresetFolder, (safe.Length == 0 ? "style" : safe) + ".json");
    }

    // ------------------------------------------------------------------ a .layout file's own style

    public static string SidecarOf(string layoutPath) => layoutPath + ".style.json";

    public static SheetStyle? LoadSidecar(string? layoutPath)
    {
        if (string.IsNullOrWhiteSpace(layoutPath))
            return null;
        try
        {
            string file = SidecarOf(layoutPath);
            return File.Exists(file) && SheetStyle.Parse(File.ReadAllText(file, Encoding.UTF8)) is { } style ? Complete(style) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // null removes it (the device uses the global style again).
    public static void SaveSidecar(string layoutPath, SheetStyle? style)
    {
        if (string.IsNullOrWhiteSpace(layoutPath))
            return;
        string file = SidecarOf(layoutPath);
        if (style == null)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            return;
        }
        Write(file, style);
    }
}
