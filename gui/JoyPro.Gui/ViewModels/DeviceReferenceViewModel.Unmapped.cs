using System;
using System.Collections.Generic;
using System.Linq;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Nothing bound may disappear from the picture (docs/device-reference.md "Unmapped and bound inputs"):
//   - every hat has a press row: when the catalog does not say which button presses it, the row is there, empty ("not
//     mapped"), and the user maps it by pressing the hat or by picking a bound input (MapPress); the mapping is saved as
//     the user's definition of the device and goes into a correction like any other change;
//   - an input the profile binds that the model has no part for (a grip's unlisted button, a driver's extra button) is a
//     stand-in part without a spot: it shows under "Other controls" with its bind.
public sealed partial class DeviceReferenceViewModel
{
    // Stand-in parts for bound inputs the model does not list.
    public const string BoundPrefix = "other.bound.";

    // The label of a hat's press that has no input yet (added by JoyPro, not part of the user's definition until mapped).
    public const string UnmappedPressLabel = "Press (not mapped)";

    // Every hat of the model gets its press control (without input when the catalog does not know it).
    private static void AddHatPresses(RefDevice model)
    {
        List<string> hats = model.Controls.Select(c => DeviceVocabulary.ElementOf(c.Control))
            .Where(e => e.Length > 0 && DeviceVocabulary.ElementType(e) == "hat").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string hat in hats)
        {
            bool hasPress = model.Controls.Any(c => string.Equals(DeviceVocabulary.ElementOf(c.Control, out string d), hat, StringComparison.OrdinalIgnoreCase) &&
                                                    (d is "press" or "push" || d.Length == 0 && DeviceVocabulary.InputKind(c.Input) == "button"));
            if (!hasPress)
                model.Controls.Add(new RefControl { Control = hat + ".press", Input = string.Empty, Label = UnmappedPressLabel, Type = "momentary" });
        }
    }

    // Bound inputs the model has no part for: stand-in parts (no spot: "Other controls"). Returns whether any was added.
    private bool AddBoundStandIns()
    {
        if (Model == null)
            return false;
        HashSet<string> known = new(Model.AllControls.Select(c => c.Input).Where(i => i.Length > 0), StringComparer.OrdinalIgnoreCase);
        bool added = false;
        foreach (string input in bindings.Keys.Where(i => i.StartsWith("JOY_", StringComparison.OrdinalIgnoreCase) && !known.Contains(i))
                     .OrderBy(DeviceVocabulary.NaturalKey, StringComparer.Ordinal))
        {
            string kind = DeviceVocabulary.InputKind(input);
            RefControl control = new()
            {
                Input = input.ToUpperInvariant(),
                Control = BoundPrefix + input.ToLowerInvariant().Replace("joy_", string.Empty),
                Label = T("{0} (bound, not on the picture)", DeviceMarkerText.ShortLabel(input)),
                Type = kind == "axis" ? "analog" : "momentary"
            };
            if (kind == "axis")
                Model.Axes.Add(control);
            else
                Model.Controls.Add(control);
            added = true;
        }
        return added;
    }

    // The stand-ins: inputs the user can map to an unmapped press.
    public IReadOnlyList<string> BoundStandIns => Model == null ? [] :
        Model.AllControls.Where(c => c.Control.StartsWith(BoundPrefix, StringComparison.OrdinalIgnoreCase) && DeviceVocabulary.InputKind(c.Input) == "button").Select(c => c.Input).ToList();

    // Whether a part is a hat whose press is not mapped.
    public bool HasUnmappedPress(string part) => Model != null && Model.Controls.Any(c => c.Input.Length == 0 &&
        string.Equals(c.Control, part + ".press", StringComparison.OrdinalIgnoreCase));

    // Maps a hat's press to an input (pressed on the device or picked from the bound stand-ins): saved as the user's
    // definition of the device (a correction to send like any other).
    public bool MapPress(string part, string input)
    {
        if (Model == null || string.IsNullOrWhiteSpace(part) || string.IsNullOrWhiteSpace(input))
            return false;
        RefControl? press = Model.Controls.FirstOrDefault(c => c.Input.Length == 0 && string.Equals(c.Control, part + ".press", StringComparison.OrdinalIgnoreCase));
        if (press == null)
            return false;
        string wanted = input.Trim().ToUpperInvariant();
        RefControl? taken = Model.AllControls.FirstOrDefault(c => string.Equals(c.Input, wanted, StringComparison.OrdinalIgnoreCase) && !c.Control.StartsWith(BoundPrefix, StringComparison.OrdinalIgnoreCase));
        if (taken != null)
        {
            Status = T("{0} is already '{1}' of this device.", DeviceMarkerText.ShortLabel(wanted), taken.Label.Length > 0 ? taken.Label : taken.Control);
            return false;
        }
        Model.Controls.RemoveAll(c => c.Control.StartsWith(BoundPrefix, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Input, wanted, StringComparison.OrdinalIgnoreCase));
        Model.Axes.RemoveAll(c => c.Control.StartsWith(BoundPrefix, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Input, wanted, StringComparison.OrdinalIgnoreCase));
        press.Input = wanted;
        press.Label = "Press"; // nonloc: catalog data
        RefDevice? definition = CurrentDefinition();
        if (definition != null)
        {
            try
            {
                DeviceReferenceService.SaveUserDefinition(definition);
            }
            catch (Exception ex)
            {
                Status = T("Could not save your definition: {0}", ex.Message);
            }
        }
        RedrawKeepingPlaced();
        SetBindings(new Dictionary<string, string>(bindings, StringComparer.OrdinalIgnoreCase));
        Status = T("{0} is the press of '{1}' now (saved as your definition of the device; send it as a correction to have it in JoyPro).",
            DeviceMarkerText.ShortLabel(wanted), Markers.FirstOrDefault(m => string.Equals(m.Id, part, StringComparison.OrdinalIgnoreCase))?.Name ?? part);
        return true;
    }

    // What JoyPro added for the screen only (stand-ins, unmapped presses) is not part of the user's definition.
    private static void StripScreenOnly(RefDevice definition)
    {
        definition.Controls.RemoveAll(c => c.Control.StartsWith(BoundPrefix, StringComparison.OrdinalIgnoreCase) || c.Input.Length == 0 && c.Label == UnmappedPressLabel);
        definition.Axes.RemoveAll(c => c.Control.StartsWith(BoundPrefix, StringComparison.OrdinalIgnoreCase));
    }
}
