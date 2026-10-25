using System.Collections.Generic;

namespace JoyPro.Gui.Models;

// The choices of Auto Assign, the Setup wizard and Lay out with AI (ui-settings.json "autoPlacement"): every one of them
// survives a restart.
public sealed class AutoPlacementSettings
{
    public int AutoAssignMaxModifiers { get; set; } = 2;
    public bool AutoAssignKeepMyLayout { get; set; } = true;
    public bool AutoAssignOverwrite { get; set; }
    public bool AutoAssignIncludeMissing { get; set; }
    public bool AutoAssignOnlySelected { get; set; }
    public string AutoAssignPhaseLayers { get; set; } = "auto";

    public int WizardModifiers { get; set; }
    public bool WizardPreferFewer { get; set; } = true;
    public double WizardRealism { get; set; } = 50;
    public string WizardPhaseLayers { get; set; } = "auto";

    public bool LayoutAddMissing { get; set; }
    public string LayoutRequest { get; set; } = string.Empty;
}

// Phase layers on a latched mode switch (docs/auto-assign.md#phase-layers): the English choice shown in lists (translated
// by the item template) and the engine's --phase-layers value.
public static class PhaseLayerChoice
{
    public const string Automatic = "Automatic";
    public const string Always = "Always";
    public const string Off = "Off";

    public static readonly IReadOnlyList<string> All = [N("Automatic"), N("Always"), N("Off")];

    public static string ToArgument(string? choice) => choice switch
    {
        Always => "on",
        Off => "off",
        _ => "auto"
    };

    public static string FromArgument(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "on" => Always,
        "off" => Off,
        _ => Automatic
    };
}
