using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    // Alias names for devices (JoyPro 1's joystick aliases, docs/devices.md "Device names"): a name of the user's own
    // ("Left panel") for a device, kept in the profile (Pr0file.JoystickAliases, keyed by the device name with its
    // instance GUID). Display only: exports to the games always use the real device.
    //   devices alias list | set <device> <alias> | clear <device>
    // A device that came back under a new instance GUID keeps its alias: devices auto-remap/exchange/replace move it.
    // Older profiles kept the name of the layout template there ("layout suggest --apply" wrote it); such names are no
    // aliases and are left out.
    internal static class EngineDeviceAliases
    {
        public sealed class AliasEntry
        {
            public string Device { get; set; } = string.Empty;
            public string Alias { get; set; } = string.Empty;
        }

        public sealed class AliasListResult
        {
            public List<AliasEntry> Aliases { get; set; } = new List<AliasEntry>();
        }

        public sealed class AliasChangeResult
        {
            public string Action { get; set; } = string.Empty;
            public string Device { get; set; } = string.Empty;
            public string Alias { get; set; } = string.Empty;
            public string PreviousAlias { get; set; } = string.Empty;
            public bool Changed { get; set; }
        }

        private static Dictionary<string, string> Aliases()
        {
            if (InternalDataManagement.JoystickAliases == null)
                InternalDataManagement.JoystickAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return InternalDataManagement.JoystickAliases;
        }

        // The user's alias of a device, empty when it has none. A name without its instance GUID finds the alias of
        // the one device of that name.
        public static string Lookup(string device)
        {
            string key = ResolveKey(device);
            return key.Length == 0 ? string.Empty : Aliases()[key].Trim();
        }

        private static string ResolveKey(string device)
        {
            string key = (device ?? string.Empty).Trim();
            if (key.Length == 0)
                return string.Empty;
            Dictionary<string, string> aliases = Aliases();
            string exactKey = aliases.Keys.FirstOrDefault(k => string.Equals(k.Trim(), key, StringComparison.OrdinalIgnoreCase));
            if (exactKey != null)
                return IsUserAlias(exactKey, aliases[exactKey]) ? exactKey : string.Empty;
            if (key.Contains("{"))
                return string.Empty;
            List<string> same = aliases
                .Where(e => string.Equals(DeviceModelIdentity.RemoveGuid(e.Key), key, StringComparison.OrdinalIgnoreCase) && IsUserAlias(e.Key, e.Value))
                .Select(e => e.Key)
                .ToList();
            return same.Count == 1 ? same[0] : string.Empty;
        }

        public static AliasListResult List()
        {
            AliasListResult result = new AliasListResult();
            foreach (KeyValuePair<string, string> entry in Aliases().OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || !IsUserAlias(entry.Key, entry.Value))
                    continue;
                result.Aliases.Add(new AliasEntry { Device = entry.Key.Trim(), Alias = entry.Value.Trim() });
            }
            return result;
        }

        public static AliasChangeResult Set(string device, string alias)
        {
            string key = (device ?? string.Empty).Trim();
            string value = Clean(alias);
            if (value.Length == 0)
                return Clear(key);
            AliasChangeResult result = new AliasChangeResult { Action = "set", Device = key, Alias = value, PreviousAlias = Lookup(key) };
            Dictionary<string, string> aliases = Aliases();
            string existing = ResolveKey(key);
            if (existing.Length > 0 && !string.Equals(existing, key, StringComparison.Ordinal))
                aliases.Remove(existing);
            result.Changed = !string.Equals(result.PreviousAlias, value, StringComparison.Ordinal);
            aliases[key] = value;
            return result;
        }

        public static AliasChangeResult Clear(string device)
        {
            string key = (device ?? string.Empty).Trim();
            AliasChangeResult result = new AliasChangeResult { Action = "clear", Device = key, PreviousAlias = Lookup(key) };
            Dictionary<string, string> aliases = Aliases();
            string existing = ResolveKey(key);
            if (existing.Length > 0)
                aliases.Remove(existing);
            // A layout template's name kept under the device goes as well (no alias).
            string stale = aliases.Keys.FirstOrDefault(k => string.Equals(k.Trim(), key, StringComparison.OrdinalIgnoreCase));
            if (stale != null)
                aliases.Remove(stale);
            result.Changed = existing.Length > 0 || stale != null;
            return result;
        }
        // One line, no control characters, at most 60 characters.
        public static string Clean(string alias)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in (alias ?? string.Empty).Trim())
                sb.Append(char.IsControl(c) ? ' ' : c);
            string text = sb.ToString().Trim();
            return text.Length > 60 ? text.Substring(0, 60).Trim() : text;
        }

        // An alias the user gave (not the device's own name, not a layout template's name an older JoyPro kept there).
        public static bool IsUserAlias(string device, string alias)
        {
            string value = (alias ?? string.Empty).Trim();
            if (value.Length == 0)
                return false;
            if (string.Equals(value, DeviceModelIdentity.RemoveGuid(device), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, (device ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
            return !IsLayoutTemplateName(device, value);
        }

        private static bool IsLayoutTemplateName(string device, string value)
        {
            if (string.Equals(value, "Picture Missing", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("JoyPro drawing of ", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("Placeholder for ", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(" (auto markers)", StringComparison.OrdinalIgnoreCase))
                return true;
            // The layout the device has: its file name or the catalog's name for it.
            if (InternalDataManagement.JoystickFileImages == null ||
                !InternalDataManagement.JoystickFileImages.TryGetValue((device ?? string.Empty).Trim(), out string layout) ||
                string.IsNullOrWhiteSpace(layout))
                return false;
            string file = Path.GetFileNameWithoutExtension(layout.Trim());
            if (string.Equals(value, file, StringComparison.OrdinalIgnoreCase))
                return true;
            return EngineLayoutSuggestion.CatalogNamesOfLayoutFile(Path.GetFileName(layout.Trim()))
                .Any(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
        }

        public static string ToText(AliasListResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Device Aliases");
            if (result.Aliases.Count == 0)
                sb.AppendLine("  (none)");
            foreach (AliasEntry entry in result.Aliases)
                sb.AppendLine("  - " + entry.Device + " = " + entry.Alias);
            return sb.ToString().TrimEnd();
        }

        public static string ToText(AliasChangeResult result)
        {
            if (result.Action == "set")
                return "Device alias: " + result.Device + " = " + result.Alias + (result.Changed ? string.Empty : " (unchanged)");
            return result.Changed ? "Device alias cleared: " + result.Device : "Device has no alias: " + result.Device;
        }
    }

    internal static partial class Program
    {
        // devices alias list | set <device> <alias> | clear <device>
        private static int RunDevicesAlias(List<string> args, string outPath, bool textOutput)
        {
            string action = args.Count == 0 ? "list" : (args[0] ?? string.Empty).Trim().ToLowerInvariant();
            List<string> rest = args.Skip(1).ToList();
            switch (action)
            {
                case "list":
                {
                    if (rest.Count > 0)
                        return Fail("Unknown devices alias list argument(s): " + string.Join(" ", rest));
                    EngineDeviceAliases.AliasListResult list = EngineDeviceAliases.List();
                    EmitPayload(textOutput ? EngineDeviceAliases.ToText(list) : EngineSerialization.ToJson(list), outPath);
                    return 0;
                }
                case "set":
                case "clear":
                case "remove":
                {
                    string device = PopOptionValue(rest, "--device");
                    string alias = PopOptionValue(rest, "--alias");
                    if (string.IsNullOrWhiteSpace(device) && rest.Count > 0)
                    {
                        device = rest[0];
                        rest.RemoveAt(0);
                    }
                    if (action == "set" && alias == null && rest.Count > 0)
                    {
                        alias = string.Join(" ", rest);
                        rest.Clear();
                    }
                    if (rest.Count > 0)
                        return Fail("Unknown devices alias " + action + " argument(s): " + string.Join(" ", rest));
                    if (string.IsNullOrWhiteSpace(device))
                        return Fail("devices alias " + action + " requires <device>" + (action == "set" ? " <alias>." : "."));
                    if (action == "set" && string.IsNullOrWhiteSpace(EngineDeviceAliases.Clean(alias)))
                        return Fail("devices alias set requires an alias (devices alias clear <device> removes one).");
                    string resolved = ResolveAliasDevice(device.Trim(), out string error);
                    if (resolved.Length == 0)
                        return Fail(error);
                    EngineDeviceAliases.AliasChangeResult result = action == "set"
                        ? EngineDeviceAliases.Set(resolved, alias)
                        : EngineDeviceAliases.Clear(resolved);
                    if (!result.Changed)
                        CommandChangedNothing = true;
                    EmitPayload(textOutput ? EngineDeviceAliases.ToText(result) : EngineSerialization.ToJson(result), outPath);
                    return 0;
                }
                default:
                    return Fail("Unknown devices alias action: " + action + " (list, set, clear).");
            }
        }

        // The device as given when it is a full device name (with its instance GUID) or one the profile knows; else
        // the profile or connected device it names.
        private static string ResolveAliasDevice(string token, out string error)
        {
            error = string.Empty;
            List<DeviceCandidate> profile = BuildProfileDeviceCandidates();
            DeviceCandidate exact = profile.FirstOrDefault(c => string.Equals(c.Name, token, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact.Name;
            if (token.Contains("{") && token.EndsWith("}", StringComparison.Ordinal))
                return token;
            List<string> warnings = new List<string>();
            List<DeviceCandidate> all = BuildDeviceMutationCandidates(profile, warnings);
            if (all.Count == 0)
                return token;
            if (TryResolveDeviceToken(token, all, false, "devices alias", out string resolved, out error))
                return resolved;
            return string.Empty;
        }
    }
}
