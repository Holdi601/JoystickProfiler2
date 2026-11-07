using System.Collections.Generic;

namespace JoyPro.Gui.Models;

public sealed class BindListItem
{
    public string RelationName { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string TypeDisplay { get; set; } = string.Empty;

    public bool HasBind { get; set; }

    public string BindState { get; set; } = string.Empty;

    public string Device { get; set; } = string.Empty;

    public string Input { get; set; } = string.Empty;

    public string ModifiersSummary { get; set; } = string.Empty;

    public bool IsAxis { get; set; }

    // Stored bind behavior; null when the bind list came from text output that lacks these values.
    public bool? Inverted { get; set; }

    public bool? Slider { get; set; }

    public bool? DoubleTap { get; set; }

    public double? Deadzone { get; set; }

    public double? SaturationX { get; set; }

    public double? SaturationY { get; set; }

    public string CurvatureCsv { get; set; } = string.Empty;

    // ---- For the bind list's cards ----

    public bool IsUnbound => !HasBind;

    public bool IsAxisType => IsAxis || TypeDisplay.Equals("axis", System.StringComparison.OrdinalIgnoreCase);

    public string TypeLabel => IsAxisType ? T("Axis") : T("Button");

    public string DeviceShort => DisplayText.ShortDevice(Device);

    public string InputLabel => DisplayText.Input(Input);

    public bool HasInput => InputLabel.Length > 0;

    public IReadOnlyList<string> ModifierList => DisplayText.List(ModifiersSummary);

    public bool HasModifiers => ModifierList.Count > 0;

    // "BTN 8" on the key cap (the device and the modifiers are chips of their own).
    public string InputShort => HasInput ? DeviceMarkerText.ShortLabel(InputLabel) : string.Empty;

    // "WarBRD · BTN 8 · Shift A".
    public string BindLabel => HasBind ? DisplayText.BindLabel(Device, Input, ModifierList) : string.Empty;

    public string BindTooltip => HasBind ? RelationName + "\n" + DisplayText.BindTooltip(Device, Input, ModifierList) : T("{0}: not bound yet", RelationName);
    // The device's icon in the row's device chip (set when the list is built; null = none known).
    public Avalonia.Media.IImage? DeviceIcon { get; set; }

    public bool HasDeviceIcon => DeviceIcon != null && HasBind;

    // What the Binds page's search found in the relation's actions (set by the list's filter before the row shows).
    public SearchMatchInfo SearchHits { get; set; } = new();

    // Experimental multi-bind (docs/multi-bind.md): the relation's other inputs (engine binds list "ExtraBinds"), input 2..
    public IReadOnlyList<ExtraBindRow> ExtraBinds { get; set; } = [];

    // Shown only while Settings > Experimental "Several inputs per relation" is on (set when the list is built).
    public bool ShowExtraBinds { get; set; }

    public bool HasExtraBinds => ShowExtraBinds && ExtraBinds.Count > 0;

    // "+1" on the card, the other inputs in its tooltip.
    public string ExtraBindsBadge => HasExtraBinds ? "+" + ExtraBinds.Count : string.Empty; // nonloc

    public string ExtraBindsTooltip => HasExtraBinds ? T("Also on: {0}", string.Join(", ", System.Linq.Enumerable.Select(ExtraBinds, e => e.Label))) : string.Empty;
}

// One extra input of a relation (experimental multi-bind): its number as the engine counts them (2..).
public sealed class ExtraBindRow
{
    public string Relation { get; set; } = string.Empty;

    public int Number { get; set; }

    public string Device { get; set; } = string.Empty;

    public string Input { get; set; } = string.Empty;

    public bool IsAxis { get; set; }

    public IReadOnlyList<string> Modifiers { get; set; } = [];

    public string NumberText => T("Input {0}", Number);

    // "WarBRD · BTN 8 · Shift A".
    public string Label => DisplayText.BindLabel(Device, Input, Modifiers);

    public string Tooltip => DisplayText.BindTooltip(Device, Input, Modifiers);

    public string InputShort => DeviceMarkerText.ShortLabel(DisplayText.Input(Input));
}
