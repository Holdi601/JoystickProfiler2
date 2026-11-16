using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.Gui.Services;

// The GUI's command line:
// --review <watch key>  (repeatable; from the tray host) checks that game config at once and shows its changes;
// --tray                (the Run entry of the first tray mode) starts the tray host instead of the window;
// --layout-window       (the tray's "Where is what?") opens the layout window; it travels with the review keys as
//                       LayoutWindowKey, so a JoyPro that already runs opens it too;
// --welcome / --tour    (the "JoyPro 2 - Tour" entry next to the exe) opens the welcome with its tours, also when it was
//                       answered before (docs/ui/help-and-tours.md); a second start hands it to the running JoyPro.
public sealed record StartupOptions(bool StartInTray, IReadOnlyList<string> ReviewKeys)
{
    public const string LayoutWindowKey = "joypro:layout-window";

    // The line a second start leaves in the request file for "--welcome" (no watch key looks like it).
    public const string WelcomeRequest = ":welcome";

    public bool OpenLayoutWindow => ReviewKeys.Contains(LayoutWindowKey, StringComparer.OrdinalIgnoreCase);

    public bool ShowWelcome { get; init; }

    public static StartupOptions Parse(IEnumerable<string>? args)
    {
        bool tray = false;
        bool welcome = false;
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
            else if (string.Equals(name, "welcome", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "tour", StringComparison.OrdinalIgnoreCase))
            {
                welcome = true;
            }
            else if (string.Equals(name, "layout-window", StringComparison.OrdinalIgnoreCase))
            {
                if (!review.Contains(LayoutWindowKey, StringComparer.OrdinalIgnoreCase))
                    review.Add(LayoutWindowKey);
            }
            else if (string.Equals(name, "review", StringComparison.OrdinalIgnoreCase) && i + 1 < list.Count)
            {
                string key = (list[++i] ?? string.Empty).Trim();
                if (key.Length > 0 && !review.Contains(key, StringComparer.OrdinalIgnoreCase))
                    review.Add(key);
            }
        }
        return new StartupOptions(tray, review) { ShowWelcome = welcome };
    }
}
