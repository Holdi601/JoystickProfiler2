using System.Linq;
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
    // The answer to "Do you use head tracking or VR?" (yes, no; empty = not asked yet): the sims that support it get no
    // buttons for looking around with yes (docs/setup-wizard.md#head-tracking).
    public string WizardHeadTracking { get; set; } = string.Empty;


    public bool LayoutAddMissing { get; set; }
    public string LayoutRequest { get; set; } = string.Empty;
}

// The head-tracking answer: "yes", "no" or empty (not asked yet), and the sims it concerns (the engine's list:
// SetupPlanner.HeadTrackingGames).
public static class HeadTrackingChoice
{
    public const string Yes = "yes";
    public const string No = "no";

    public static readonly IReadOnlyList<string> Games =
    [
        "DCS", "IL2Game", "IL2Korea", "IL2CoD", "IL2CoDBlitz", "IL21946", "FalconBMS", "MSFS2020", "MSFS2024", "FSX", "FSXSE", "P3D4", "P3D5", "P3D6",
        "XPlane11", "XPlane12", "WarThunder", "Arma2", "Arma2OA", "Arma3", "ArmaReforger", "StarCitizen", "EliteDangerous", "VTOLVR", "VTOLVRNative", "FateOfTheGalaxy"
    ];

    public static bool Concerns(string? game) => Games.Contains(game ?? string.Empty, System.StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "yes" or "true" => Yes,
        "no" or "false" => No,
        _ => string.Empty
    };
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
