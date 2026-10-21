using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.Engine
{
    // engine layout image <device> [--set <layout file>|--clear]: the layout file the profile uses for a device
    // (Pr0file.JoystickFileImages: the user's own layout, a JoyPro 1 layout, a picture he chose). Without an option it
    // prints the mapping; --clear forgets it (the Layout page's "Reset to default" shows JoyPro's picture again), --set
    // puts one back (its undo). The device matches with or without its GUID.
    internal static partial class Program
    {
        private static int RunLayoutImage(List<string> args, string outPath, bool textOutput)
        {
            string set = PopOptionValue(args, "--set");
            bool clear = PopFlag(args, "--clear");
            if (args.Count != 1 || (set != null && clear))
                return Fail("layout image <device> [--set <layout file>|--clear]");
            string device = args[0].Trim();
            if (InternalDataManagement.JoystickFileImages == null)
                InternalDataManagement.JoystickFileImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> map = InternalDataManagement.JoystickFileImages;
            string bare = DeviceReference.DeviceProbe.RemoveGuid(device);
            List<string> keys = map.Keys.Where(k => string.Equals(k, device, StringComparison.OrdinalIgnoreCase) ||
                                                    string.Equals(DeviceReference.DeviceProbe.RemoveGuid(k), bare, StringComparison.OrdinalIgnoreCase)).ToList();
            string previous = keys.Count > 0 ? map[keys[0]] : string.Empty;
            if (clear)
            {
                foreach (string key in keys)
                    map.Remove(key);
                CommandChangedNothing = keys.Count == 0;
            }
            else if (set != null)
            {
                foreach (string key in keys)
                    map.Remove(key);
                map[device] = set.Trim();
                CommandChangedNothing = string.Equals(previous, set.Trim(), StringComparison.OrdinalIgnoreCase) && keys.Count == 1 && keys[0] == device;
            }
            string current = map.Where(p => string.Equals(DeviceReference.DeviceProbe.RemoveGuid(p.Key), bare, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).FirstOrDefault() ?? string.Empty;
            EmitPayload(textOutput
                ? device + ": " + (current.Length > 0 ? current : "(no layout file: JoyPro's picture)") + (previous != current ? " (was: " + (previous.Length > 0 ? previous : "none") + ")" : string.Empty)
                : EngineSerialization.ToJson(new { Device = device, Previous = previous, Current = current }), outPath);
            return 0;
        }
    }
}
