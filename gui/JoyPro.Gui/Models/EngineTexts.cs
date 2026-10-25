namespace JoyPro.Gui.Models;

// The engine's English texts of Auto-Assign, the Setup wizard and Lay out with AI (engine/SetupWizard/SetupPlanner.cs,
// SetupRoles.cs, SetupPlanJson.cs, SetupImportance.cs, SetupPhases.cs, engine/EngineSetupWizard.cs, EngineAutoAssign.cs,
// EngineDeviceKb.cs, EngineLayout.cs, LayoutPhrases.cs) for the localization tool; shown through Services/EngineText.cs,
// which translates a sentence whole or clause by clause.
internal static class EngineTexts
{
    // Why a function is where it is, and why not.
    public static readonly string[] Reasons =
    [
        N("translation on the left stick (space sims)"), N("on a labelled switch ({0}): no free unlabelled input left"), N("the panel label \"{0}\""), N("where the real aircraft has it"),
        N("its own position"), N("a fitting position (preference {0})"), N("a free grip button behind {0}"), N("the best free input left"),
        N("your own control from before"), N("on/off pair on one switch"), N("together with its group on one hat"), N("air to air layer of the mode switch"), N("air to ground layer of the mode switch"), N("your preference"),
        N("moved by you"), N("swapped by you"),
        N("needs an MFD device"), N("no axis on the devices the hands reach for {0}"), N("no button on the devices the hands reach for {0}"),
        N("no fitting axis (flight axes only take their own functions)"), N("no fitting input (grips keep cockpit panel functions off; no panel button left)"),
        N("every fitting input is taken by more important functions of {0} (best: {1} = {2})"),
        N("every fitting input is taken by more important functions of the same aircraft (best: {0} = {1})"),
        N("no hat with the free up direction for its set ({0}): a set goes together on one hat or not at all"),
        N("no hat with the free down direction for its set ({0}): a set goes together on one hat or not at all"),
        N("no hat with the free left direction for its set ({0}): a set goes together on one hat or not at all"),
        N("no hat with the free right direction for its set ({0}): a set goes together on one hat or not at all"),
        N("no hat with the free press direction for its set ({0}): a set goes together on one hat or not at all"), N("every fitting input is taken"),
        N("your own modifier"), N("the pinky lever / button, the classic shift"), N("a grip button JoyPro reserved as shift"),
        N("mode switch up: the air to air layer"), N("mode switch down: the air to ground layer"), N("mode switch up: the air to ground layer"), N("mode switch down: the air to air layer")
    ];

    // The plan's notes and warnings.
    public static readonly string[] Notes =
    [
        N("Only {0} free buttons on the sticks for the {1} most important functions of {2}: JoyPro added {3} modifier(s) although you asked for none."),
        N("Only {0} free buttons on the sticks for the {1} most important functions: JoyPro added {2} modifier(s) although you asked for none."),
        N("Only {0} free buttons on the sticks for the {1} most important functions of {2}: JoyPro added {3} modifier(s) although you asked for at most {4}."),
        N("Only {0} free buttons on the sticks for the {1} most important functions: JoyPro added {2} modifier(s) although you asked for at most {3}."),
        N("Only {0} of the {1} modifiers you allowed were needed."),
        N("Phase layers: not needed, the functions fit without the mode switch."),
        N("{0} display / keypad commands left out: no MFD or keypad device in this setup."),
        N("{0} new relations for {1} aircraft / games"), N("{0} functions placed, {1} not placed, {2} of your binds kept."),
        N("Realism {0} %: grouped by function, one button per function in every aircraft."),
        N("Realism {0} %: aircraft whose real HOTAS has a function elsewhere got their own relation ({1} split relations). Positions from the {2}."),
        N("Phase layers on the mode switch {0} ({1})"), N("up = air to air, down = air to ground"), N("up = air to ground, down = air to air"),
        N("up = air to air"), N("up = air to ground"), N("down = air to ground"), N("down = air to air"),
        N("the centre / the other position is the normal layer. Flight, take-off and landing functions stay on the normal layer."),
        N("Modifier \"{0}\" = {1} {2}"), N("{0} functions on its layer."),
        N("Cockpit entry not found: {0}"), N("Not in the local DCS database (refresh it to include them): {0}"),
        N("The function map could not be used: {0}"), N("Function map: {0} more actions the catalog does not list.")
    ];

    // Device roles, the reasons JoyPro proposed them, and the rules of the plan.
    public static readonly string[] Roles =
    [
        N("Main stick (right hand)"), N("Stick for helicopters"), N("Left stick (space sims)"), N("Throttle (left hand)"), N("Collective (helicopters)"),
        N("Pedals"), N("Panel / button box"), N("Keyboard joystick (extra)"), N("Virtual device (kept as is)"), N("Not used"),
        N("virtual device: binds on it are kept, the plan does not use it"), N("keyboard with joystick mode: an extra, kept for your keyboard binds"),
        N("rudder pedals: yaw / rudder and toe brakes"), N("collective: the helicopters' collective and throttle"), N("throttle: the left hand for aircraft"),
        N("main stick: the right hand in every aircraft"), N("left-hand stick: translation in space sims"), N("also the throttle side (no throttle)"),
        N("panel / button box: system switches where their type and label fit"),
        N("a second left-hand stick: an alternative (one device per hand)"), N("a second right-hand stick: an alternative (one device per hand)"),
        N("a second {0}: an alternative (one device per hand)"), N("choose \"Stick for helicopters\" to fly helicopters with it"),
        N("unknown device: used like a button box"),
        N("Right hand: {0} in every aircraft."), N("Right hand: {0} in every aircraft except helicopters ({1})."), N("Right hand: no main stick chosen."),
        N("Left hand in aircraft: {0} (throttle, airbrake, sensors)."), N("Left hand: no throttle: throttle functions go on the main stick and panels."),
        N("Helicopters: the collective is {0}"), N("the throttle {0} stays with the aircraft."), N("the throttle is not used."),
        N("Helicopters: no collective: the collective goes on the throttle axis of {0}."),
        N("Space sims: translation on the left stick {0} (X = strafe left/right, Y = forward/back, twist = up/down), its buttons for the ship."),
        N("Space sims: no left stick: translation on the throttle's mini-stick, axes and hats ({0})."),
        N("Pedals: yaw / rudder and toe brakes on {0}."), N("Panels: system switches (gear, lights, master arm ...) where their type and label fit: {0}."),
        N("{0}: an extra, kept for your keyboard binds (the plan does not bind it)."), N("{0}: virtual, its binds are kept."), N("Not used: {0}."),
        N("Flight controls"), N("Weapons, countermeasures, sensors"), N("Systems"), N("Views and comms"), N("Display keys"),
        N("Flying"), N("Take-off and landing"), N("Navigation"), N("Air to air"), N("Air to ground"), N("Comms"), N("Views")
    ];

    // Changing the plan by hand.
    public static readonly string[] Edits =
    [
        N("\"{0}\" is your own bind; it stays."), N("That input is not part of the plan's devices."), N("Unknown modifier: {0}"),
        N("That input is a modifier button."), N("Already used for the same aircraft by \"{0}\"; swap them instead."),
        N("Your own binds stay; only the plan's new binds can be swapped."), N("An axis and a button cannot be swapped."),
        N("Both functions need a place to swap; use Move for a function that is not placed."),
        N("The swap would put a function on an input another relation of the same aircraft uses."), N("Its input is used for the same aircraft now; move it instead."),
        N("\"{0}\" and \"{1}\" share {2} in the same aircraft."), N("\"{0}\" on {1} would not work while the mode switch is on \"{2}\" (\"{3}\").")
    ];

    // Auto Assign: devices, warnings, the explanation.
    public static readonly string[] AutoAssign =
    [
        N("grip told apart by the inputs it reports"), N("other grips report the same"), N("pick yours if it is one of them (--grip)"),
        N("several grips fit this base"), N("pick one (--grip)"), N("not in the device catalog"), N("generic layout from its {0} buttons"),
        N("not connected (added from the catalog)"), N("exchange it for the real device once connected"),
        N("Pick the grip of every device with several grip choices (--grip \"<device>=<grip id>\"); those devices are left out until then."),
        N("Devices with the same role as another one are left out (one bind per function): {0}. Use --ignore-device on the other one to use them instead."),
        N("Phase layers: no latched mode switch on your devices (a 2- or 3-position toggle switch on a base or collective)."),
        N("No usable device: connect your devices or add them from the catalog (--add-device)."),
        N("{0} ({1} aircraft)"), N("wanted"), N("before"), N("{0} (with a modifier)"), N("not assigned"), N("{0} (strong match)"), N("used by {0}")
    ];

    // Control names of DeviceVocabulary (engine/DeviceReference/DeviceVocabulary.cs): the parts and their directions
    // (Services/ReadableNames.Vocabulary translates only names of this list).
    public static readonly string[] VocabularyNames =
    [
        N("Button {0}"), N("Hat {0}"), N("Base button {0}"), N("Throttle hat {0}"), N("Thumb button {0}"), N("TMS hat"), N("Selector {0}"),
        N("Trim hat"), N("Trigger"), N("Base toggle {0}"), N("Throttle button {0}"), N("Toggle {0}"), N("CMS hat"), N("DMS hat"), N("Thumb stick"),
        N("Encoder {0}"), N("3-way toggle {0}"), N("Yoke button {0}"), N("Quadrant button {0}"), N("Collective hat {0}"), N("Base encoder {0}"),
        N("Mic switch"), N("Yoke hat {0}"), N("Index button {0}"), N("Pinky button"), N("Weapon release"), N("Quadrant detent {0}"), N("Rotary {0}"),
        N("Key {0}"), N("Pinky lever"), N("Yoke rocker {0}"), N("Gear lever"), N("Base rotary {0}"), N("Collective button {0}"), N("Flaps lever"),
        N("Yoke switch {0}"), N("Quadrant toggle {0}"), N("Flip trigger"), N("Lower index button"), N("Speed brake"), N("Collective switch {0}"),
        N("Dogfight switch"), N("Boat switch"), N("Base selector {0}"), N("China hat"), N("Pinky switch"), N("Quadrant lever {0}"),
        N("Second thumb stick"), N("Cage button"), N("Coolie hat"), N("Brake lever"), N("Twist"), N("Collective ministick"), N("Stick roll"),
        N("Stick pitch"), N("Yoke roll"), N("Yoke pitch"), N("Throttle"), N("Left throttle"), N("Right throttle"), N("Friction"), N("Rudder"),
        N("Left toe brake"), N("Right toe brake"), N("Collective"), N("Throttle twist"), N("Slider"), N("Slider {0}"), N("Wheel {0}"), N("Axis {0}"),
        N("Throttle axis {0}"), N("Stick (roll / pitch)"), N("Yoke (roll / pitch)"), N("Ministick"), N("Throttle ministick")
    ];

    public static readonly string[] VocabularyDirections =
    [
        N("up"), N("down"), N("left"), N("right"), N("press"), N("first stage"), N("second stage"), N("clockwise"), N("counter-clockwise"),
        N("up-right"), N("up-left"), N("down-right"), N("down-left"), N("forward"), N("back"), N("center"), N("mid"), N("push"), N("pull"), N("in"),
        N("out"), N("X"), N("Y"), N("position {0}")
    ];

    // Lay out with AI: reasons and notes.
    public static readonly string[] Layout =
    [
        N("curvature {0}, dead zone {1}"), N("vJoy, the physical devices hidden from the game"), N("the physical devices"),
        N("the {0} commands of \"{1}\" ({2}) get a relation of their own"),
        N("\"{0}\": some of its DCS commands serve {1} and other aircraft at once ({2}); they stay."),
        N("No {0} in your setup for: {1}"), N("No bound axis relation of that kind for: {0}"),
        N("The local AI model was not asked: {0}"), N("The local AI model did not start: {0}"), N("The local AI model did not answer: {0}")
    ];
}
