using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Services;

// Analog keyboards (Wooting, Razer Huntsman V2 Analog / V3 Pro, SteelSeries Apex Pro, Keychron HE, ...) report their
// keys as game-controller axes too: typing moves them. Follow input and axis recording leave the axes of such devices
// (and of every device the catalog counts as a keyboard) alone unless the user opted the device in (Settings > Devices).
// Their buttons still count: they are the keyboard binds of the profile.
public static class KeyboardLikeDevices
{
    private static readonly Regex NamePattern = new(
        @"keyboard|wooting|huntsman|apex ?pro|keychron|drunkdeer|lekker|\bnuphy\b|\banalog key",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly ConcurrentDictionary<string, bool> Cache = new(StringComparer.OrdinalIgnoreCase);

    // The catalog to ask (tests set their own; null = the shipped catalog when it is loaded already).
    internal static Func<DeviceCatalog?> CatalogSource { get; set; } = () => DeviceReferenceService.LoadedCatalog;

    // The device ("Wooting Two HE (ARM) {GUID}") is an analog keyboard or a keyboard the catalog knows.
    public static bool IsKeyboardLike(string? device)
    {
        string name = DeviceMarkerText.DeviceName(device);
        if (name.Length == 0 || string.Equals(name, "Keyboard", StringComparison.OrdinalIgnoreCase))
            return false;
        if (NamePattern.IsMatch(name))
            return true;
        if (Cache.TryGetValue(name, out bool known))
            return known;
        DeviceCatalog? catalog;
        try
        {
            catalog = CatalogSource();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            catalog = null;
        }
        if (catalog == null)
            return false;
        bool keyboard = catalog.Devices.Any(d =>
            d.Names.Any(n => string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase)) &&
            (DeviceClasses.Of(d) == "keyboard" || d.Kind is "keyboard" or "mouse"));
        Cache[name] = keyboard;
        return keyboard;
    }

    internal static void ClearCache() => Cache.Clear();
}
