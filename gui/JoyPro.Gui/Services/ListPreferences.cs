using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Services;

// What the list pages remember besides ui-settings.json (a file of its own, so it never races the main settings):
// the filter ticks of the Relations and Binds pages, and the questions the user said not to ask again
// ("Remove this action?"), which Settings > Confirmations switches back on. <settings>\list-preferences.json.
public sealed class ListPreferences
{
    public Dictionary<string, ListFilterState> Filters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Question key -> false: do not ask (the answer is yes).
    public Dictionary<string, bool> Confirmations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string DefaultPath => Path.Combine(AppInfo.SettingsFolder, "list-preferences.json");

    public static ListPreferences Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                ListPreferences? loaded = JsonSerializer.Deserialize<ListPreferences>(File.ReadAllText(path), Options);
                if (loaded != null)
                {
                    loaded.Filters = new Dictionary<string, ListFilterState>(loaded.Filters ?? [], StringComparer.OrdinalIgnoreCase);
                    loaded.Confirmations = new Dictionary<string, bool>(loaded.Confirmations ?? [], StringComparer.OrdinalIgnoreCase);
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // A broken file: start with the defaults.
        }
        return new ListPreferences();
    }

    public void Save(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception)
        {
            // Not remembered this time.
        }
    }

    public bool ShouldAsk(string key) => !Confirmations.TryGetValue(key, out bool ask) || ask;
}
