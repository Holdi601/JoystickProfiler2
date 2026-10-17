using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.Gui.Services;

// The GUI's command line:
// --review <watch key>  (repeatable; from the tray host) checks that game config at once and shows its changes;
// --tray                (the Run entry of the first tray mode) starts the tray host instead of the window.
public sealed record StartupOptions(bool StartInTray, IReadOnlyList<string> ReviewKeys)
{
    public static StartupOptions Parse(IEnumerable<string>? args)
    {
        bool tray = false;
        List<string> review = [];
        List<string> list = [.. args ?? []];
        for (int i = 0; i < list.Count; i++)
        {
            string name = (list[i] ?? string.Empty).Trim().TrimStart('-', '/');
            if (string.Equals(name, "tray", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "minimized", StringComparison.OrdinalIgnoreCase))
            {
                tray = true;
            }
            else if (string.Equals(name, "review", StringComparison.OrdinalIgnoreCase) && i + 1 < list.Count)
            {
                string key = (list[++i] ?? string.Empty).Trim();
                if (key.Length > 0 && !review.Contains(key, StringComparer.OrdinalIgnoreCase))
                    review.Add(key);
            }
        }
        return new StartupOptions(tray, review);
    }
}
