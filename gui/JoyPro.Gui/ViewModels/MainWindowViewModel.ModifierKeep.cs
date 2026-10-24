using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace JoyPro.Gui.ViewModels;

// Bulk deletes (remove a device, delete or merge relations, clear or unbind binds, replace a device) take away binds,
// never modifiers of other devices. The profile keeps a modifier only while a bind uses it, so a modifier that loses its
// last bind in such a step stays in the Modifier Library as a temporary modifier (kept with the profile by the autosave
// drafts) until it is on a bind again. Only the modifiers of the removed device itself go with it.
public sealed partial class MainWindowViewModel
{
    // The modifiers of the profile as the GUI knows them from the last binds list (name, device, input, inverted).
    private List<PendingModifierDefinition> SnapshotProfileModifiers()
    {
        List<PendingModifierDefinition> snapshot = [];
        foreach (KeyValuePair<string, List<ModifierReformerMatch>> entry in modifierReformerCacheByName)
        {
            if (entry.Value == null || entry.Value.Count == 0 || pendingModifiersByName.ContainsKey(entry.Key))
                continue;
            ModifierReformerMatch best = entry.Value
                .GroupBy(m => (NormalizeDeviceToken(m.Device), NormalizeInputToken(m.Input)))
                .OrderByDescending(g => g.Count())
                .Select(g => g.First())
                .First();
            if (string.IsNullOrWhiteSpace(best.Device) || string.IsNullOrWhiteSpace(best.Input))
                continue;
            snapshot.Add(new PendingModifierDefinition(entry.Key.Trim(), best.Device.Trim(), best.Input.Trim(), ReformerInverted(best.Reformer)));
        }
        return snapshot;
    }

    // "name§device§input§True": the last field is the inverted flag.
    private static bool ReformerInverted(string reformer)
    {
        string[] parts = (reformer ?? string.Empty).Split('§');
        return parts.Length >= 4 && bool.TryParse(parts[3].Trim(), out bool inverted) && inverted;
    }

    // The same device instance: "Name {GUID}" only with the same GUID; names without a GUID by name.
    internal static bool SameDeviceInstanceName(string left, string right)
    {
        string a = Regex.Replace((left ?? string.Empty).Trim(), @"\s+", " ");
        string b = Regex.Replace((right ?? string.Empty).Trim(), @"\s+", " ");
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // After a bulk delete: refreshes binds and modifiers, then keeps every modifier of the snapshot that is gone and was
    // not on the removed device. Returns how many were kept.
    private async Task<int> RefreshAndKeepModifiersAsync(IReadOnlyCollection<PendingModifierDefinition> before, string? removedDevice = null)
    {
        await RefreshBindsAndModifiersAfterChangeAsync();
        return KeepModifiersMissingAfterBulkDelete(before, removedDevice);
    }

    private int KeepModifiersMissingAfterBulkDelete(IReadOnlyCollection<PendingModifierDefinition> before, string? removedDevice)
    {
        if (before.Count == 0)
            return 0;
        HashSet<string> present = ModifierItems
            .Select(m => (m.Name ?? string.Empty).Trim())
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<PendingModifierDefinition> kept = before
            .Where(m => !present.Contains(m.Name))
            .Where(m => string.IsNullOrWhiteSpace(removedDevice) || !SameDeviceInstanceName(m.Device, removedDevice))
            .ToList();
        if (kept.Count == 0)
            return 0;

        foreach (PendingModifierDefinition modifier in kept)
            pendingModifiersByName[modifier.Name] = modifier;
        MergePendingModifiersIntoCollections();
        RefreshModifierValidationState();
        RebuildLayoutEditorModifierOptions();
        ScheduleAutosave();
        string names = string.Join(", ", kept.Select(m => m.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        StatusText = Plural("Kept {0} modifier without binds in the library: {1}.", "Kept {0} modifiers without binds in the library: {1}.",
            kept.Count, kept.Count.ToString(CultureInfo.InvariantCulture), names);
        return kept.Count;
    }
}
