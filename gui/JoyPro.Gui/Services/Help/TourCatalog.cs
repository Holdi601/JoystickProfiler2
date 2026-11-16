using System;
using System.Collections.Generic;
using System.Linq;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Services.Help;

// One step of a tour (docs/ui/help-and-tours.md). Target: what the step points at:
//   nav:<page id>      the page's button in the bottom bar (found by name, so the user's order and size do not matter)
//   name:<x:Name>      a named control       cmd:<Command>   the first shown button with that view model command
//   tab:<English tab>  a tab by its header   section:<id>    a Settings section            page   the open page
//   none / empty       no element: the card stands in the middle.
//   a|b                the first of the alternatives that is shown.
// Stage: the page the step opens first. WaitStage: the step waits until the user opened that page (its button is the
// target). WaitClick: the step waits for a click on the target. Essential: shown with "only the essentials" too.
// Text and Easy are English (shown through T()); "{0}" in them is the game the tour is about.
public sealed record TourStep(string Title, string Text, string Target = "", UiStage? Stage = null, UiStage? WaitStage = null,
    bool WaitClick = false, bool Essential = true)
{
    public bool Waits => WaitStage != null || WaitClick;

    // A second paragraph (English, shown through T()); not shown with easy words.
    public string Extra { get; init; } = string.Empty;

    // Easy words (Settings > Tours): the card's text for a child, at most 12 words per sentence, no JoyPro words
    // without their meaning. Empty: the normal text.
    public string Easy { get; init; } = string.Empty;

    // The title with easy words (empty: the normal title).
    public string EasyTitle { get; init; } = string.Empty;

    // A button on the card that does the step for the user (TourActions), shown when it applies.
    public string Action { get; init; } = string.Empty;

    // A small drawn picture on the card (TourPictures): what an axis, the pretend pad or Export is.
    public string Picture { get; init; } = string.Empty;

    // The tab the step opens on its page (English header).
    public string OpenTab { get; init; } = string.Empty;

    // The step is left out when this is true (TourConditions), e.g. the helper driver is already installed.
    public string SkipIf { get; init; } = string.Empty;

    // A step in two parts: once ThenWhen holds (TourConditions: the target was clicked, the license box is ticked), the
    // card shows Then / ThenEasy and points at ThenTarget (empty: the same target). The step goes on only with Next.
    public string ThenWhen { get; init; } = string.Empty;

    public string Then { get; init; } = string.Empty;

    public string ThenEasy { get; init; } = string.Empty;

    public string ThenTarget { get; init; } = string.Empty;

    // The step waits until this holds (TourConditions.BindSelected), then goes on by itself; Next does it for the user.
    public string WaitFor { get; init; } = string.Empty;

    // Done when the step opens (TourPrepare), so its control is there: a game control picked for Record input.
    public string Prepare { get; init; } = string.Empty;

    // Next or Skip while SkipIf does not hold yet: the card first says what will not work (WarnSkip / WarnSkipEasy); the
    // second press goes on.
    public string WarnSkip { get; init; } = string.Empty;

    public string WarnSkipEasy { get; init; } = string.Empty;

    // The card while the quick setup window is open (TourActions.QuickSetup): the step waits for that window.
    public string Waiting { get; init; } = string.Empty;

    public string WaitingEasy { get; init; } = string.Empty;
}

public static class TourActions
{
    // "Open my JoyPro 1 profile": shown when Documents\JoyPro\last.pr0file is there.
    public const string OpenJoyPro1Profile = "open-jp1";

    // "Set it all up for me": the quick setup (the pad tour's buttons).
    public const string QuickSetup = "quick-setup";
}

// When a step is left out (TourStep.SkipIf).
public static class TourConditions
{
    // ViGEmBus, the pretend pad's driver, is installed.
    public const string ViGEmInstalled = "vigem-installed";

    // The tour's games are all pad games (nothing to export).
    public const string PadGamesOnly = "pad-only";

    // The quick tour was not started right after "Write to my game" (no success to tell).
    public const string NotJustWritten = "not-just-written";

    // The step's control was clicked (ThenWhen).
    public const string Clicked = "clicked";

    // The license box of ViGEmBus is ticked (ThenWhen).
    public const string ViGEmConsent = "vigem-consent";

    // A game control is picked in the Binds list (WaitFor).
    public const string BindSelected = "bind-selected";

    // The tour runs from its first card (not joined in the middle by the first start's quick path).
    public const string FromTheStart = "from-start";
}

// What a step does when it opens (TourStep.Prepare).
public static class TourPrepare
{
    // Picks the first game control of the Binds list when none is picked, so Record input shows.
    public const string SelectBind = "select-bind";
}

// The small drawn pictures of the cards (Views/Controls/TourPicture.cs).
public static class TourPictures
{
    public const string Axis = "axis";
    public const string Pad = "pad";
    public const string Export = "export";
    public const string AltTab = "alttab";
}

// A tour. Big: the newcomer's tour with bigger highlights and text (easy words make every tour big).
public sealed record TourDefinition(string Id, string Title, string Description, IReadOnlyList<TourStep> Steps, bool Big = false, string PageSlug = "")
{
    public IReadOnlyList<TourStep> StepsFor(bool everyFeature) => everyFeature ? Steps : Steps.Where(s => s.Essential).ToList();

    // The welcome's answer with easy words (empty: the normal ones).
    public string EasyTitle { get; init; } = string.Empty;

    public string EasyDescription { get; init; } = string.Empty;
}

// The tours of the welcome (the paths for different players) and one tour per page.
public static class TourCatalog
{
    public const string Pad = "pad";
    public const string Dcs = "dcs";
    public const string Quick = "quick";
    public const string Newcomer = "new";
    public const string JoyPro1 = "jp1";
    public const string NoTour = "none";

    // Games that only take an Xbox pad (or are played with one): the pad path. Study sims: the DCS path.
    public static readonly IReadOnlyCollection<string> PadGames = new HashSet<string>(["AceCombat7", "AceCombat8", "HLLVietnam"], StringComparer.OrdinalIgnoreCase);
    public static readonly IReadOnlyCollection<string> StudySims = new HashSet<string>(["DCS", "FalconBMS"], StringComparer.OrdinalIgnoreCase);

    // The path the welcome suggests for the installed games: DCS (or another study sim) -> the DCS path; only pad games
    // -> the pad path; anything else -> the quick path. The user can pick another.
    public static string SuggestPath(IEnumerable<string> installedGameIds, bool joyPro1Profile)
    {
        // Someone with JoyPro 1's profile on the PC wants to see where its functions are now (and to open it).
        return joyPro1Profile ? JoyPro1 : SuggestPath(installedGameIds);
    }

    // With what the Start page knows: only a gamepad and only pad games -> no tour (the controller already works); pad
    // games without a flight stick -> never the flight-stick way; a study sim that has no buttons set yet -> the quick
    // way (the DCS tour starts with reading the binds the game has).
    public static string SuggestPath(IEnumerable<string> installedGameIds, bool joyPro1Profile, bool? stickConnected, bool gamepadOnly, IReadOnlyCollection<string> gamesWithoutBinds)
    {
        if (joyPro1Profile)
            return JoyPro1;
        List<string> games = installedGameIds.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        List<string> sims = games.Where(g => StudySims.Contains(g)).ToList();
        if (sims.Count > 0)
            return sims.All(g => gamesWithoutBinds.Contains(g, StringComparer.OrdinalIgnoreCase)) ? Quick : Dcs;
        if (games.Count > 0 && games.All(g => PadGames.Contains(g)))
            return gamepadOnly ? NoTour : stickConnected == false ? Quick : Pad;
        return Quick;
    }

    public static string SuggestPath(IEnumerable<string> installedGameIds)
    {
        List<string> games = installedGameIds.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (games.Any(g => StudySims.Contains(g)))
            return Dcs;
        if (games.Count > 0 && games.All(g => PadGames.Contains(g)))
            return Pad;
        return Quick;
    }

    // Every path tour ends with help: F1 explains anything.
    private static readonly TourStep HelpStep = new(N("Help is always there"),
        N("Stuck? Press the F1 key, then click any button. JoyPro explains it instead of pressing it. Press F1 again to stop."), "name:HelpButton")
    {
        Easy = N("Stuck? Press the F1 key. Then click a button. JoyPro tells you what it does.")
    };

    public static readonly IReadOnlyList<TourDefinition> Paths =
    [
        new(Pad, N("Play ACE COMBAT with my flight stick"),
            N("For games that only know an Xbox controller. JoyPro turns your stick and throttle into a pretend controller."),
        [
            new(N("Your stick as an Xbox controller"),
                N("ACE COMBAT only knows an Xbox controller. JoyPro turns your stick and throttle into a pretend controller. This takes a few short steps."))
            {
                Easy = N("This game only knows an Xbox controller. JoyPro turns your stick into a pretend one. Press Next."),
                Picture = TourPictures.Pad
            },
            // The buttons first (the quick setup): the pretend controller needs them. Its "Set up the pretend controller"
            // brings the user back here; while its window is open, this card waits (Waiting) and never runs ahead.
            new(N("Let JoyPro set up your buttons"),
                N("Click Set it all up for me. JoyPro puts the game's controls on your stick. Then press Use these buttons, then Set up the pretend controller."),
                "cmd:QuickSetupCommand", UiStage.Start, WaitClick: true)
            {
                Easy = N("Click Set it all up for me. A new window opens. Then click the blue buttons there."),
                Action = TourActions.QuickSetup,
                Waiting = N("The setup window is open. Click the blue buttons there: first Use these buttons, then Set up the pretend controller. This card waits for you."),
                WaitingEasy = N("Look at the new window. Click the blue buttons there. I wait here.")
            },
            // The bar button's name follows the player (Pretend controller for pad-only players, else Virtual Joystick):
            // the card points at the glowing button instead of naming it.
            new(N("Open the controller page"), N("Click the glowing button in the bar at the bottom."), "nav:vjoy", WaitStage: UiStage.VirtualJoystick)
            {
                Easy = N("Click the glowing button at the bottom.")
            },
            // Where the first start's quick path joins the pad steps (PadFinishIndex): the why, which the tour's first
            // card says when the tour runs from its start (left out then).
            new(N("Why this page"),
                N("Your game only knows an Xbox controller. JoyPro turns your stick into a pretend one. The next steps get it ready."),
                Stage: UiStage.VirtualJoystick)
            {
                Easy = N("Your game only knows an Xbox controller. JoyPro turns your stick into a pretend one."),
                Picture = TourPictures.Pad,
                SkipIf = TourConditions.FromTheStart
            },
            // The install button stays grey until the license box is ticked: the box glows first, then the button.
            new(N("Install the helper"),
                N("The pretend controller needs a small free helper, ViGEmBus. First tick the box below the license. Then click Install ViGEmBus. Windows asks for permission: click Yes."),
                "name:VirtualJoystickViGEmNeeded>ComponentConsentBox|name:VirtualJoystickViGEmNeeded|name:VirtualJoystickViGEmStatus", UiStage.VirtualJoystick)
            {
                Easy = N("Your stick needs a small helper. Get a grown-up. Tick the box. Then click Install ViGEmBus."),
                SkipIf = TourConditions.ViGEmInstalled,
                ThenWhen = TourConditions.ViGEmConsent,
                Then = N("Good. Now click Install ViGEmBus. Windows asks for permission: click Yes. This card goes on by itself when the helper is there."),
                ThenEasy = N("Good! Now click Install ViGEmBus. The grown-up clicks Yes."),
                ThenTarget = "name:VirtualJoystickViGEmNeeded>ComponentInstallButton|name:VirtualJoystickViGEmNeeded|name:VirtualJoystickViGEmStatus",
                WarnSkip = N("Without ViGEmBus the pretend controller cannot work: your game will not see your stick. Install it first. Or click Next again to go on without it."),
                WarnSkipEasy = N("Without the helper, the game cannot see your stick. Click Next again to go on anyway.")
            },
            new(N("Nothing to change here"),
                N("Your game already uses the pretend controller. You do not need to change anything here."),
                "name:VirtualJoystickGamesList", UiStage.VirtualJoystick, Essential: false)
            {
                Easy = N("Your game already uses the pretend controller. Nothing to do here.")
            },
            // After the click on Test the card stays and says to move the stick; only Next goes on.
            new(N("Try it"),
                N("Click Test. Then move your stick. The controller picture below moves with it."),
                "cmd:TestVirtualJoystickCommand", UiStage.VirtualJoystick, WaitClick: true)
            {
                Easy = N("Click Test. Now move your stick. The picture moves too!"),
                OpenTab = "Live test",
                ThenWhen = TourConditions.Clicked,
                Then = N("Now move your stick. Does the picture move? Then click Next."),
                ThenEasy = N("Now move your stick. Does the picture move? Then click Next.")
            },
            // Pad games see a pretend controller that starts after their title screen (VirtualXboxPadTimingNote): one
            // order, said the same way everywhere, with the way back from a full-screen game (Alt+Tab).
            new(N("When to press Start"),
                N("First start the game. At its title screen, hold Alt and press Tab: that brings you back here. Click Start. Then press Alt+Tab again to go back to the game."),
                "cmd:StartVirtualJoystickCommand", UiStage.VirtualJoystick)
            {
                Easy = N("Start the game. At the title screen, hold Alt and press Tab. Click Start. Press Alt and Tab again to go back."),
                Picture = TourPictures.AltTab
            },
            new(N("Which button does what"),
                N("This list shows which stick button becomes which controller button."), "tab:Mapping", UiStage.VirtualJoystick, Essential: false)
            {
                Easy = N("Here you see which stick button is which controller button.")
            },
            HelpStep,
            new(N("Done!"),
                N("Every time you play, start the game first. At its title screen, press Alt+Tab and click Start here. Have fun!"))
            {
                Easy = N("Start the game. At the title screen, press Alt+Tab. Click Start here. Have fun!")
            }
        ])
        {
            EasyDescription = N("For games that only know an Xbox controller.")
        },
        new(Dcs, N("DCS World or Falcon BMS"),
            N("Your buttons for every aircraft in one place: read from DCS, organised, written back and checked."),
        [
            new(N("The DCS way"),
                N("This tour shows how JoyPro works with DCS. Your binds for every aircraft live in one place."))
            {
                Easy = N("JoyPro keeps your DCS buttons for every plane in one place.")
            },
            new(N("Your profile"),
                N("This is your profile, one file with your binds for every game and aircraft. JoyPro saves it by itself. History keeps every version."),
                "name:ProfileHeaderGrid")
            {
                Easy = N("This is your profile. It keeps your buttons for every plane. JoyPro saves it for you.")
            },
            new(N("Read your DCS binds"),
                N("Import reads the binds you already have in DCS, for every aircraft. Your DCS files are only read, never changed."),
                "cmd:StartImportWizardCommand", UiStage.Start)
            {
                Easy = N("Import reads the buttons you already have in DCS. DCS does not change."),
                // (a DCS beginner has none: the quick way sets them up)
                Extra = N("No buttons in DCS yet? Then end this tour and click Set it all up for me on the Start page.")
            },
            new(N("Open Relations"), N("Click Relations in the bar at the bottom."), "nav:relations", WaitStage: UiStage.Relations)
            {
                Easy = N("Click Relations at the bottom.")
            },
            new(N("One function, all aircraft"),
                N("A relation is one function in all your aircraft. Landing gear up in the F-16 and the A-10 is one relation. It gets one button."),
                "name:RelationsListBox", UiStage.Relations)
            {
                Easy = N("A relation is one job, like landing gear up. It is the same in every plane. It gets one button.")
            },
            new(N("The commands inside"),
                N("On the right you see the DCS command of each aircraft in this relation. Add more aircraft with the search."),
                "name:RelationNodeList", UiStage.Relations, Essential: false)
            {
                Easy = N("On the right you see the same job in each plane.")
            },
            new(N("Open Binds"), N("Click Binds in the bar at the bottom."), "nav:binds", WaitStage: UiStage.Binds)
            {
                Easy = N("Click Binds at the bottom.")
            },
            new(N("Axis curves"),
                N("An axis is a part you move smoothly, like the stick. Here you set how soft or sharp it feels. JoyPro writes it into DCS's own axis settings."),
                "name:AxisResponseExpander", UiStage.Binds)
            {
                Easy = N("An axis is a part you move, like the stick. Here you set how soft it feels."),
                Picture = TourPictures.Axis
            },
            new(N("Detent"),
                N("Detent mode: one throttle for all aircraft. The afterburner notch sits where your throttle has it."),
                "name:DetentModeExpander", UiStage.Binds, Essential: false)
            {
                Easy = N("Some throttles have a bump for extra power. JoyPro puts it right in every plane.")
            },
            new(N("Modifiers"),
                N("Modifiers are shift buttons. Hold one, and other buttons do a second job. DCS shows them as button combinations."),
                "nav:modifiers", Essential: false)
            {
                Easy = N("Hold a shift button. Then the other buttons do something else.")
            },
            new(N("Open Export"), N("Click Export in the bar at the bottom."), "nav:export", WaitStage: UiStage.Export)
            {
                Easy = N("Click Export at the bottom.")
            },
            new(N("Write into DCS"),
                N("Export writes your binds into DCS's control files. JoyPro keeps a backup each time. Undo export puts the old files back."),
                "cmd:ExportSelectedGamesCommand", UiStage.Export)
            {
                Easy = N("Export sends your buttons into DCS. You can always undo it."),
                Picture = TourPictures.Export
            },
            new(N("Check"),
                N("Check finds problems before you fly, like two jobs on one button. Click Check in the bar at the bottom."),
                "nav:check", WaitClick: true)
            {
                Easy = N("Check finds mistakes. Click Check at the bottom.")
            },
            HelpStep
        ])
        {
            EasyDescription = N("For DCS: all your planes in one place.")
        },
        new(Quick, N("Just make my stick work"),
            N("JoyPro sets it all up for you. You only look and click."),
        [
            // Right after "Write to my game": the success first, on the Start page (not on the Export page and its warnings).
            new(N("It worked!"),
                N("It worked! {0} now has your buttons. JoyPro kept a backup of the old ones. You can start the game now."),
                "none", UiStage.Start)
            {
                Easy = N("It worked! {0} has your buttons now. You can play now!"),
                SkipIf = TourConditions.NotJustWritten
            },
            new(N("Change a button later"),
                N("Want another button for something? Click Binds in the bar at the bottom. Everything you just set up is there."),
                "nav:binds", WaitStage: UiStage.Binds)
            {
                Easy = N("Want to change a button? Click Binds at the bottom.")
            },
            // Record input shows only once a game control is picked: the list first, then the button.
            new(N("Pick a game control"),
                N("Click a game control in this list."),
                "name:BindsListBox", UiStage.Binds)
            {
                Easy = N("Click a game control in this list."),
                WaitFor = TourConditions.BindSelected
            },
            new(N("Pick and press"),
                N("Now click Record input. Then press a button on your stick."),
                "cmd:RecordNextInputCommand", UiStage.Binds)
            {
                Easy = N("Now click Record input. Then press a button on your stick."),
                Prepare = TourPrepare.SelectBind
            },
            new(N("Send it to the game"),
                N("Changed something? Send it to the game again. Click Export at the bottom, then Export ticked games."),
                "nav:export", WaitStage: UiStage.Export)
            {
                Easy = N("Changed something? Click Export at the bottom. Then click Export ticked games."),
                SkipIf = TourConditions.PadGamesOnly
            },
            HelpStep,
            new(N("Done!"), N("Your buttons are ready. Start {0} and have fun!"), "none", UiStage.Start)
            {
                Easy = N("All done! Start {0} and have fun!")
            }
        ])
        {
            EasyDescription = N("JoyPro does it for you. You only click.")
        },
        new(Newcomer, N("Show me around"),
            N("A calm tour of JoyPro, step by step."),
        [
            new(N("Welcome"),
                N("JoyPro puts game controls on your stick, throttle and pedals. This tour has five short parts. Take your time and press Next."))
            {
                Easy = N("JoyPro puts game controls on your stick. This tour has five short parts. Take your time.")
            },
            new(N("1. Your profile"),
                N("This is your profile. It keeps all your settings for every game. JoyPro saves it for you, so you never press Save."),
                "name:ProfileHeaderGrid")
            {
                Easy = N("This is your profile. It keeps all your settings. JoyPro saves it for you.")
            },
            new(N("Your games and devices"),
                N("The Start page shows the games and devices JoyPro found on your PC."), "page", UiStage.Start)
            {
                Easy = N("Here you see your games and your sticks.")
            },
            new(N("Reading a game"),
                N("A game may already have buttons set. JoyPro can read them. That is called import. The game is only read."),
                "cmd:StartImportWizardCommand", UiStage.Start, Essential: false)
            {
                Easy = N("JoyPro can read the buttons a game already has. The game does not change.")
            },
            new(N("2. Relations"), N("Now click Relations, down in the bar at the bottom."), "nav:relations", WaitStage: UiStage.Relations)
            {
                Easy = N("Click Relations at the bottom.")
            },
            new(N("What a relation is"),
                N("A relation is one job, like \"landing gear up\". It is the same job in every plane. Give it one button, and it works in every plane."),
                "name:RelationsListBox", UiStage.Relations)
            {
                Easy = N("A relation is one job, like landing gear up. Give it one button. That button works in every plane.")
            },
            new(N("3. Binds"), N("Click Binds in the bar at the bottom."), "nav:binds", WaitStage: UiStage.Binds)
            {
                Easy = N("Click Binds at the bottom.")
            },
            new(N("What a bind is"),
                N("A bind is the stick button for a job. Click a job in this list."),
                "name:BindsListBox", UiStage.Binds)
            {
                Easy = N("A bind is the stick button for a job. Click a job in this list."),
                WaitFor = TourConditions.BindSelected
            },
            // Record input shows only once a job is picked (the step picks the first one when none is).
            new(N("Record a button"), N("Now click Record input. Then press a button on your stick. JoyPro waits for it."),
                "cmd:RecordNextInputCommand", UiStage.Binds)
            {
                Easy = N("Now click Record input. Then press a button on your stick."),
                Prepare = TourPrepare.SelectBind
            },
            new(N("Shift buttons"),
                N("A modifier works like the Shift key. Hold it, and other buttons do a second job."), "nav:modifiers", Essential: false)
            {
                Easy = N("Hold a shift button. Then the other buttons do something else.")
            },
            new(N("Pictures of your devices"),
                N("Layout shows your devices with what every button does. You can print it."), "nav:layout", Essential: false)
            {
                Easy = N("Here you see a picture of your stick with every button.")
            },
            new(N("4. Export"), N("Click Export in the bar at the bottom."), "nav:export", WaitStage: UiStage.Export)
            {
                Easy = N("Click Export at the bottom.")
            },
            new(N("What export does"),
                N("Export sends your buttons into the game. Before that, the game does not change. JoyPro keeps a backup, so you can always undo it."),
                "cmd:ExportSelectedGamesCommand", UiStage.Export)
            {
                Easy = N("Export sends your buttons into the game. You can always undo it."),
                Picture = TourPictures.Export
            },
            new(N("5. Check"),
                N("Check finds mistakes, like two jobs on one button. Click Check in the bar at the bottom."), "nav:check", WaitClick: true)
            {
                Easy = N("Check finds mistakes. Click Check at the bottom.")
            },
            HelpStep
        ], Big: true)
        {
            EasyDescription = N("A slow tour, step by step.")
        },
        new(JoyPro1, N("I used JoyPro 1"),
            N("Where each JoyPro 1 function sits now."),
        [
            new(N("Same ideas, new places"),
                N("JoyPro 2 keeps JoyPro 1's ideas: relations and binds. Only the places changed. Here is where each old function is now.")),
            new(N("Load, save, new"),
                N("JoyPro 1's Load, Save and New: the profile bar. Saving happens by itself now, and History keeps every version."), "name:ProfileHeaderGrid"),
            // JoyPro 2 opens JoyPro 1's profiles as they are; the card offers the one JoyPro 1 opened last when it is there.
            new(N("Your JoyPro 1 profile"),
                N("JoyPro 2 opens JoyPro 1 profiles as they are: click Open profile... and pick yours (JoyPro 1 kept them in Documents\\JoyPro)."),
                "name:OpenProfileButton") { Action = TourActions.OpenJoyPro1Profile },
            new(N("Import from a game"),
                N("JoyPro 1's import: on each game's card on the Start page, now for many more games."), "cmd:StartImportWizardCommand", UiStage.Start),
            new(N("Relations"),
                N("The relation list and editor: the Relations page, now with search by meaning and automatic grouping."), "name:RelationsListBox", UiStage.Relations),
            new(N("Binds and curves"),
                N("Setting binds, curves and dead zones: the Binds page."), "name:BindsListBox", UiStage.Binds),
            new(N("Modifiers"), N("Modifiers have their own page now."), "nav:modifiers", Essential: false),
            new(N("Export"),
                N("Export with its modes and backups: the Export page. Undo export restores the game's files."), "name:ExportModeList", UiStage.Export),
            new(N("Validation"), N("JoyPro 1's duplicate and validation check: the Check button."), "nav:check"),
            new(N("Overview"), N("JoyPro 1's overview and cheat sheets: the Layout page."), "nav:layout", Essential: false),
            new(N("Game paths"), N("Game paths: Settings > Games."), "section:games", UiStage.Settings, Essential: false),
            HelpStep
        ])
    ];

    // The first pad step on the Virtual Joystick page: where the quick setup's "Set up the pretend controller" goes on.
    public static int PadFinishIndex(IReadOnlyList<TourStep> steps)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Stage == UiStage.VirtualJoystick && steps[i].WaitStage == null)
                return i;
        }
        return 0;
    }

    // The page tours ("Tour of this page"): the page in plain words first, then its main parts.
    private static readonly (string Page, UiStage Stage, TourStep[] Steps)[] PageSteps =
    [
        ("start", UiStage.Start,
        [
            new(N("Your games"), N("Each card is one game: read its binds, write yours into it, or update its list of functions."), "cmd:StartImportWizardCommand"),
            new(N("Set up automatically"), N("This button puts the functions of your games on your devices for you."), "cmd:QuickSetupCommand|cmd:OpenSetupWizardCommand")
        ]),
        ("relations", UiStage.Relations,
        [
            new(N("The list"), N("All your relations. Click one to see its game commands on the right."), "name:RelationsListBox"),
            new(N("Search"), N("Type what you look for, in your own words."), "name:RelationSearchBox"),
            new(N("The commands"), N("The game commands of the picked relation, per game and aircraft."), "name:RelationNodeList", Essential: false)
        ]),
        ("binds", UiStage.Binds,
        [
            new(N("The list"), N("Every relation with its button. \"not bound\" means it has none yet."), "name:BindsListBox"),
            new(N("Record"), N("Press Record input, then the button on your stick."), "cmd:RecordNextInputCommand") { Prepare = TourPrepare.SelectBind },
            new(N("Axis curves"), N("For an axis: how soft or sharp it feels, per game and aircraft."), "name:AxisResponseExpander", Essential: false),
            new(N("Follow input"), N("Press any button on your devices and the list jumps to what it does."), "name:FollowInputToggle", Essential: false)
        ]),
        ("modifiers", UiStage.Modifiers, []),
        ("layout", UiStage.Layout,
        [
            new(N("Your device"), N("The picture of your device with your binds. Click a marker to see or change that button."), "name:LayoutCanvas")
        ]),
        ("export", UiStage.Export,
        [
            new(N("How to write"), N("How the export treats what is already in the game. JoyPro keeps a backup, so Undo export puts it back."), "name:ExportModeList"),
            new(N("Write now"), N("Writes your binds into the ticked games. A backup is made first."), "cmd:ExportSelectedGamesCommand")
        ]),
        ("hud", UiStage.Hud,
        [
            new(N("The designer"), N("Drag and size the parts of your HUD here."), "name:HudDesignerSurface")
        ]),
        ("ffb", UiStage.FfbTelemetry, []),
        ("vjoy", UiStage.VirtualJoystick,
        [
            new(N("Per game"), N("For each game: your real devices, one virtual joystick, or a pretend Xbox controller."), "name:VirtualJoystickGamesList"),
            new(N("Live test"), N("Click Test, then move your devices and watch the virtual device move."), "cmd:TestVirtualJoystickCommand", Essential: false) { OpenTab = "Live test" },
            new(N("Start"), N("Xbox controller: start the game, wait for its title screen, then click Start. Virtual joystick: click Start before the game."), "cmd:StartVirtualJoystickCommand")
        ]),
        ("cockpits", UiStage.Cockpits, []),
        ("statistics", UiStage.Statistics, []),
        ("settings", UiStage.Settings,
        [
            new(N("Sections"), N("Every part of the settings. Open one by its title; the search finds a setting by name."), "name:SettingsSectionsHost"),
            new(N("Tours"), N("Every tour, to start again any time."), "section:tours", Essential: false)
        ]),
        ("advanced", UiStage.Advanced, [])
    ];

    public static IReadOnlyList<string> PageTourPages => PageSteps.Select(p => p.Page).ToList();

    public static TourDefinition? PageTour(string page)
    {
        foreach ((string slug, UiStage stage, TourStep[] steps) in PageSteps)
        {
            if (!string.Equals(slug, page, StringComparison.OrdinalIgnoreCase))
                continue;
            HelpPageText text = HelpCatalog.Page(slug);
            List<TourStep> list = [new(text.Title, text.What, "page", stage) { Extra = text.Fits, Easy = text.Easy }];
            list.AddRange(steps.Select(s => s with { Stage = s.Stage ?? stage }));
            list.Add(new TourStep(N("What comes next"), text.Next, "page", stage));
            return new TourDefinition("page:" + slug, text.Title, text.What, list, PageSlug: slug);
        }
        return null;
    }

    public static TourDefinition? Find(string id)
    {
        if (id.StartsWith("page:", StringComparison.Ordinal))
            return PageTour(id[5..]);
        return Paths.FirstOrDefault(t => t.Id == id);
    }
}
