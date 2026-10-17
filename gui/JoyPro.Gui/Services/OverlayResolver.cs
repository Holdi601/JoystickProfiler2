using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Gui.Services;

// What a pressed button does (engine overlay map): the binds of the running game / aircraft, matched by device and
// input, with the modifiers held (or switched on) at that moment. A bind with more matching modifiers wins.
public sealed class OverlayResolver
{
    public sealed record Modifier(string Name, string Device, string Input, bool Switch);

    public sealed record Entry(string Relation, string Title, string Device, string Input, IReadOnlyList<Modifier> Modifiers);

    private readonly List<Entry> entries;
    private readonly HashSet<string> held = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> switchedOn = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> switchInputs = new(StringComparer.OrdinalIgnoreCase);

    public OverlayResolver(IEnumerable<Entry> entries)
    {
        this.entries = entries.ToList();
        foreach (Modifier modifier in this.entries.SelectMany(e => e.Modifiers).Where(m => m.Switch))
            switchInputs.Add(Key(modifier.Device, modifier.Input));
    }

    public int Count => entries.Count;

    // Returns the bind of a press (null for releases and unbound inputs); always tracks what is held.
    public Entry? Handle(string device, string input, bool down)
    {
        string key = Key(device, input);
        if (!down)
        {
            held.Remove(key);
            return null;
        }
        held.Add(key);
        if (switchInputs.Contains(key) && !switchedOn.Remove(key))
            switchedOn.Add(key);

        Entry? best = null;
        int bestModifiers = -1;
        foreach (Entry entry in entries)
        {
            if (!string.Equals(entry.Input, input, StringComparison.OrdinalIgnoreCase) || !SameDevice(entry.Device, device))
                continue;
            if (!entry.Modifiers.All(IsActive))
                continue;
            if (entry.Modifiers.Count > bestModifiers)
            {
                best = entry;
                bestModifiers = entry.Modifiers.Count;
            }
        }
        return best;
    }

    private bool IsActive(Modifier modifier)
    {
        string key = Key(modifier.Device, modifier.Input);
        return modifier.Switch ? switchedOn.Contains(key) : held.Any(h => SameKey(h, key));
    }

    private static bool SameKey(string a, string b)
    {
        int ia = a.LastIndexOf('|');
        int ib = b.LastIndexOf('|');
        return string.Equals(a[(ia + 1)..], b[(ib + 1)..], StringComparison.OrdinalIgnoreCase) && SameDevice(a[..ia], b[..ib]);
    }

    private static string Key(string device, string input) => device.Trim() + "|" + input.Trim();

    // DirectInput names a device "Name {GUID}" like the profile does; the GUID decides, the name is the fallback.
    public static bool SameDevice(string a, string b)
    {
        string ga = Guid(a);
        string gb = Guid(b);
        if (ga.Length > 0 && gb.Length > 0)
            return string.Equals(ga, gb, StringComparison.OrdinalIgnoreCase);
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string Guid(string device)
    {
        Match match = Regex.Match(device ?? string.Empty, @"\{([0-9A-Fa-f-]{36})\}");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}
