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

    // The Binds page's peek at the selected relation's actions is open.
    public bool BindPeekOpen { get; set; } = true;

    // List -> its sort levels ("device:asc", "input:asc", "name:desc"); none = by name.
    public Dictionary<string, List<string>> Sorts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Aircraft kept in a relation's action although its command does something else there ("Keep for all", "use here"):
    // relation|game|id|plane; the "same id, different meaning" check does not ask about them again.
    public List<string> KeptAircraft { get; set; } = [];

    // The games whose import showed the watcher hint once ("changes come in through Watch for changes").
    public List<string> WatcherHintShownGames { get; set; } = [];

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
                    loaded.KeptAircraft ??= [];
                    loaded.WatcherHintShownGames ??= [];
                    loaded.Sorts = new Dictionary<string, List<string>>(loaded.Sorts ?? [], StringComparer.OrdinalIgnoreCase);
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
