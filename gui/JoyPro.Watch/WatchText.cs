using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace JoyPro.Watch;

// The tray host's texts in the user's language (docs/localization.md). The GUI writes the translations of these texts
// for the language it shows to <settings>\tray-strings.json (TrayStrings.cs); the tray reads that small file at start,
// so it carries no string tables of its own. Without the file: English.
public static class WatchText
{
    public const string FileName = "tray-strings.json";

    private static Dictionary<string, string> strings = new(StringComparer.Ordinal);

    public static string T(string text) => strings.TryGetValue(text, out string? t) && t.Length > 0 ? t : text;

    public static string T(string format, params object[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, T(format), args);
        }
        catch (FormatException)
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
    }

    public static void Load(string settingsFolder)
    {
        try
        {
            string file = Path.Combine(settingsFolder, FileName);
            if (!File.Exists(file))
                return;
            Dictionary<string, string>? read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
            if (read != null)
                strings = new Dictionary<string, string>(read, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // English then.
        }
    }

    // Tests.
    public static void Reset() => strings = new Dictionary<string, string>(StringComparer.Ordinal);
}
