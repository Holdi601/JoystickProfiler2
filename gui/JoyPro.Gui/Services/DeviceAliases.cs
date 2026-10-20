using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace JoyPro.Gui.Services;

// Device aliases (docs/devices.md "Device names"): a name of the user's own for a device ("Left panel"), kept in the
// profile (engine devices alias). Every place that shows a device name asks DisplayText.ShortDevice, which shows the
// alias while Settings > "Device names" says so (ShowAliases); the real name stays in the tooltip (DisplayText.DeviceTip).
// Display only: the games always get the real device.
public static class DeviceAliases
{
    private static readonly object Gate = new();
    private static Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);

    // Settings > Device names: the alias (default) or the device's real name.
    public static bool ShowAliases { get; set; } = true;

    // The devices connected now (an alias kept under a device's older instance GUID is only taken while that older
    // device is not connected itself).
    public static Func<string, bool>? IsConnected { get; set; }

    public static event Action? Changed;

    public static IReadOnlyDictionary<string, string> All
    {
        get
        {
            lock (Gate)
                return new Dictionary<string, string>(aliases, StringComparer.OrdinalIgnoreCase);
        }
    }

    // The profile's aliases from "engine devices alias list" (JSON); false (and nothing changed) for other output.
    public static bool TryLoad(string json)
    {
        Dictionary<string, string> loaded = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Aliases", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                return false;
            foreach (JsonElement item in list.EnumerateArray())
            {
                string device = item.TryGetProperty("Device", out JsonElement d) ? d.GetString() ?? string.Empty : string.Empty;
                string alias = item.TryGetProperty("Alias", out JsonElement a) ? a.GetString() ?? string.Empty : string.Empty;
                if (device.Trim().Length > 0 && alias.Trim().Length > 0)
                    loaded[device.Trim()] = alias.Trim();
            }
        }
        catch (JsonException)
        {
            return false;
        }
        Replace(loaded);
        return true;
    }

    public static void Replace(IReadOnlyDictionary<string, string> values)
    {
        lock (Gate)
            aliases = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke();
    }

    public static void Set(string device, string? alias)
    {
        string key = (device ?? string.Empty).Trim();
        if (key.Length == 0)
            return;
        string value = (alias ?? string.Empty).Trim();
        lock (Gate)
        {
            if (value.Length == 0)
            {
                string? found = KeyOf(key);
                if (found != null)
                    aliases.Remove(found);
                aliases.Remove(key);
            }
            else
            {
                aliases[key] = value;
            }
        }
        Changed?.Invoke();
    }

    // The alias of a device (whether shown or not), empty when it has none.
    public static string AliasOf(string? device)
    {
        string key = (device ?? string.Empty).Trim();
        if (key.Length == 0)
            return string.Empty;
        lock (Gate)
        {
            string? found = KeyOf(key);
            return found == null ? string.Empty : aliases[found];
        }
    }

    // The device the alias of a device is kept under (itself, or the same device under its older instance GUID).
    public static string StoredKeyOf(string? device)
    {
        string key = (device ?? string.Empty).Trim();
        lock (Gate)
            return key.Length == 0 ? string.Empty : KeyOf(key) ?? key;
    }

    // The alias to show for a device: empty when it has none or Settings shows the real names.
    public static string Shown(string? device) => ShowAliases ? AliasOf(device) : string.Empty;

    // The exact device, else the one alias of that name kept under another instance GUID (a device Windows gave a new
    // GUID; with several devices of one name only while the other one is not connected).
    private static string? KeyOf(string key)
    {
        if (aliases.ContainsKey(key))
            return aliases.Keys.First(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        string name = WithoutGuid(key);
        List<string> same = aliases.Keys.Where(k => string.Equals(WithoutGuid(k), name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (same.Count != 1)
            return null;
        string other = same[0];
        bool hasGuid = key.EndsWith('}') && key.Contains('{');
        if (!hasGuid || !other.Contains('{'))
            return other;
        Func<string, bool>? connected = IsConnected;
        return connected != null && !connected(other) ? other : null;
    }

    public static string WithoutGuid(string device)
    {
        string text = (device ?? string.Empty).Trim();
        int open = text.LastIndexOf(" {", StringComparison.Ordinal);
        return open > 0 && text.EndsWith('}') ? text[..open].Trim() : text;
    }
}
