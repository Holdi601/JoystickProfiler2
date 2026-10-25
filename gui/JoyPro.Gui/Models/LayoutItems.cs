using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// "Lay out with AI" (docs/layout-with-ai.md): a preference JoyPro understood, in the user's language.
public sealed class LayoutPreferenceItem(string key, string text, string words, bool isNew, bool fromAi)
{
    public string Key { get; } = key;
    public string Text { get; } = text;
    public string Words { get; } = words;
    public bool IsNew { get; } = isNew;
    public bool FromAi { get; } = fromAi;
    public string Badge => IsNew ? (FromAi ? T("new, read by the local AI") : T("new")) : T("saved");
}

// One change of the preview (a bind, a split relation, an axis curve, force feedback, the virtual joystick), ticked to apply.
public sealed partial class LayoutChangeItem(string id, string kind, string title, string scope, string before, string after, string reason) : ObservableObject
{
    public string Id { get; } = id;
    public string Kind { get; } = kind;
    public string Title { get; } = title;
    public string Scope { get; } = scope;
    public string Before { get; } = before;
    public string After { get; } = after;
    public string Reason { get; } = reason;
    public string BeforeText => Before.Length == 0 ? T("(not bound)") : Before;
    public bool HasScope => Scope.Length > 0;
    public bool HasReason => Reason.Length > 0;

    [ObservableProperty]
    private bool isSelected = true;
}

// The changes of one kind, with a tick for all of them.
public sealed partial class LayoutChangeGroupItem : ObservableObject
{
    public LayoutChangeGroupItem(string kind, string title, IEnumerable<LayoutChangeItem> changes)
    {
        Kind = kind;
        Title = title;
        foreach (LayoutChangeItem change in changes)
        {
            change.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LayoutChangeItem.IsSelected) && !updating)
                    OnPropertyChanged(nameof(AllSelected));
            };
            Changes.Add(change);
        }
    }

    private bool updating;

    public string Kind { get; }
    public string Title { get; }
    public ObservableCollection<LayoutChangeItem> Changes { get; } = [];
    public string Header => T("{0} ({1})", Title, Changes.Count);

    [ObservableProperty]
    private bool isExpanded;

    // true: all ticked, false: none, null: some.
    public bool? AllSelected
    {
        get => Changes.All(c => c.IsSelected) ? true : Changes.Any(c => c.IsSelected) ? null : false;
        set
        {
            bool tick = value != false;
            updating = true;
            foreach (LayoutChangeItem change in Changes)
                change.IsSelected = tick;
            updating = false;
            OnPropertyChanged();
        }
    }
}

// The engine's English texts of Lay out with AI (engine/SetupWizard/LayoutPhrases.cs, engine/EngineLayout.cs) for the
// localization tool; shown through T().
internal static class LayoutTexts
{
    public static readonly string[] Templates =
    [
        N("Every bind is laid out again (your previous controls still count)."),
        N("Every button does what the real aircraft's button at that place does."),
        N("Left hand: the left-hand stick in every game (the throttle is left out)."),
        N("Left hand: the throttle in aircraft and helicopters, the left-hand stick in space games."),
        N("Only the devices you named; the others are left out."),
        N("The same function is on the same button in every aircraft."),
        N("Your binds stay; only unbound relations get a place."),
        N("{0} first: its functions get the best places."),
        N("{0}: linear {1} (no curve)."),
        N("{0}: more direct curves for {1} (curvature {2})."),
        N("{0}: softer curves for {1} (curvature +{2})."),
        N("{0}: yaw on the left-hand stick's twist axis instead of the pedals."),
        N("{0}: yaw / rudder on the twist axis instead of the pedals."),
        N("Every game sees your devices directly (no virtual joystick)."),
        N("Every game: a virtual Xbox pad (ViGEmBus)."),
        N("Every game: the virtual joystick (vJoy) with your devices hidden."),
        N("{0}: the games see your devices directly (no virtual joystick)."),
        N("{0}: a virtual Xbox pad (ViGEmBus)."),
        N("{0}: the virtual joystick (vJoy) with your devices hidden."),
        N("{0}: {1} never on the {2} of the {3}."),
        N("{0}: {1} on the {2} of the {3}."),
        N("{0}: {1} never on the {2}."),
        N("{0}: {1} on the {2}."),
        N("Space games: translation and the ship's buttons on the left-hand stick."),
        N("{0}: the left-hand stick."),
        N("No modifier buttons."),
        N("As few modifier buttons as possible."),
        N("At most {0} modifier button(s)."),
        N("No mode switch (no phase layers)."),
        N("A latched switch becomes a mode switch: up = air to air, down = air to ground."),
        N("No pedals: rudder / yaw on the stick's twist axis."),
        N("No collective: the helicopters' collective on the throttle."),
        N("{0}: no force feedback shake."),
        N("{0}: force feedback off."),
        N("{0}: more force feedback shake (x{1})."),
        N("{0}: less force feedback shake (x{1})."),
        N("{0}: stronger force feedback forces (x{1})."),
        N("{0}: softer force feedback forces (x{1})."),
        N("{0}: smaller dead zone for {1}."),
        N("{0}: bigger dead zone for {1}.")
    ];

    // Inputs and the Lay out values in plain words (DeviceVocabulary.InputName, EngineLayout's OldText / NewText, the
    // planner's reasons), shown through Loc.Translate (Services/ReadableNames.cs).
    public static readonly string[] Readable =
    [
        N("Hat {0} up"), N("Hat {0} down"), N("Hat {0} left"), N("Hat {0} right"), N("Hat {0} up-right"), N("Hat {0} up-left"), N("Hat {0} down-right"), N("Hat {0} down-left"),
        N("X axis"), N("Y axis"), N("Z axis"), N("X rotation"), N("Y rotation"), N("Z rotation"),
        N("linear"), N("inverted"), N("default"), N("dead zone {0} %"), N("curve {0}"), N("travel {0} %"), N("forces {0} %"), N("shake {0} %"),
        N("your devices directly"), N("a virtual Xbox pad"), N("the virtual joystick (vJoy)"),
        N("no hat with the free {0} direction for its set ({1}): a set goes together on one hat or not at all")
    ];

    // Words the templates take as values.
    public static readonly string[] Words =
    [
        N("Every aircraft"), N("Helicopters"), N("Spaceships"), N("Fixed wing"), N("{0} in {1}"),
        N("trim"), N("countermeasures"), N("TMS"), N("DMS / sensor select"), N("landing gear"), N("flaps"), N("airbrake"), N("weapon release"), N("gun trigger"),
        N("radar"), N("zoom"), N("lights"), N("yaw / rudder"), N("strafe"), N("wheel brakes"), N("slew / TDC"), N("comms"), N("kneeboard"), N("autopilot"),
        N("master arm"), N("afterburner"), N("shields"), N("power management"), N("boost"), N("pitch"), N("roll"), N("yaw"), N("throttle"), N("collective"),
        N("twist axis"), N("mini-stick"), N("hat"), N("slider / wheel"), N("pinky lever"), N("panel"), N("base buttons"), N("switch"), N("trigger"),
        N("left-hand stick"), N("pedals"), N("main stick"), N("device")
    ];

    // Notes of the engine.
    public static readonly string[] Notes =
    [
        N("No left-hand stick found."),
        N("No left-hand stick found: space games use the throttle."),
        N("No left-hand stick found: the throttle is the left hand everywhere."),
        N("No throttle found: the left-hand stick is the left hand everywhere."),
        N("No modifier needed: every function fits without one."),
        N("Nothing to change: your layout already fits these preferences."),
        N("No force feedback device in your setup: the force feedback wishes wait until one is connected."),
        N("The virtual joystick is set per game, not per aircraft class: say the game (\"vJoy for Star Citizen\").")
    ];
}
