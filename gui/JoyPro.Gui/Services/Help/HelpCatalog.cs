using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Services.Help;

// One control of the GUI with its help text (docs/ui/help-and-tours.md). Made from the wiki entry of the control
// (website/content/gui/<page>.md, "<!-- entry: key -->") by `python website/build.py --help-catalog` into
// Resources/help-controls.json; the build's --check fails while the file is older than the wiki. What and Next are
// English: the help popup shows T(What) and T(Next).
public sealed record HelpEntry(string Page, string Key, string Kind, string Anchor, string What, string Next, string Command,
    string Binding, string Click, string Name, string Label, string Tooltip)
{
    // The plain first sentence (the wiki entry's "<!-- easy: ... -->"): at most 12 words, for a child. Empty: none.
    public string Easy { get; init; } = string.Empty;
}

// A page of the wiki (one per GUI page): what it is for in the bigger picture and what comes after it. Easy: what it is
// in at most 12 plain words (the help popup's first line, the page tour with easy words).
public sealed record HelpPageText(string Slug, string Title, string What, string Fits, string Next, string Easy = "");

// The help texts of F1's help mode: every control's entry, the pages, the plain words for the kinds of controls and the
// few JoyPro words the popup explains.
public static class HelpCatalog
{
    public const string WikiBase = "https://holdi601.github.io/JoystickProfiler2/";

    private static IReadOnlyList<HelpEntry>? entries;

    public static IReadOnlyList<HelpEntry> Entries => entries ??= Load();

    // The wiki page in the language of the GUI: the site keeps each language under /<code>/ with the GUI's codes (de,
    // zh-Hans, sr-Cyrl ...); English (and a code the GUI does not know) is at the root.
    public static string WikiUrl(string page, string? anchor = null) => WikiUrl(page, anchor, JoyPro.Gui.Localization.Loc.Current.Code);

    public static string WikiUrl(string page, string? anchor, string? language) =>
        WikiBase + WikiLanguagePrefix(language) + "gui/" + (string.IsNullOrWhiteSpace(page) ? "start" : page) + ".html" +
        (string.IsNullOrWhiteSpace(anchor) ? string.Empty : "#" + anchor);

    // "<code>/" of a GUI language other than English, else empty.
    internal static string WikiLanguagePrefix(string? language) =>
        JoyPro.Gui.Localization.Languages.Find(language) is { } known && !ReferenceEquals(known, JoyPro.Gui.Localization.Languages.English) ? known.Code + "/" : string.Empty;

    private static List<HelpEntry> Load()
    {
        List<HelpEntry> list = [];
        try
        {
            using Stream? stream = typeof(HelpCatalog).Assembly.GetManifestResourceStream("JoyPro.Gui.HelpControls.json");
            if (stream == null)
                return list;
            using JsonDocument doc = JsonDocument.Parse(stream);
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                list.Add(new HelpEntry(S(row, "p"), S(row, "k"), S(row, "kind"), S(row, "a"), S(row, "w"), S(row, "x"), S(row, "c"),
                    S(row, "b"), S(row, "ck"), S(row, "n"), S(row, "l"), S(row, "t")) { Easy = S(row, "e") });
            }
        }
        catch (JsonException)
        {
        }
        return list;

        static string S(JsonElement row, string name) =>
            row.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }

    // ---- pages ----

    public static readonly IReadOnlyList<HelpPageText> Pages =
    [
        new("header", N("Profile bar"),
            N("The bar at the top: which profile is open, and the buttons for the whole profile."),
            N("Your profile is one file with all your settings for every game. JoyPro saves it by itself after every change."),
            N("Pick a page in the bar at the bottom to work on your profile."),
            N("The bar at the top. It shows your open profile.")),
        new("start", N("Start"),
            N("The Start page shows your games, your devices and what to do next."),
            N("Everything begins here: JoyPro finds your games and devices. From here you read binds from a game or let JoyPro set up your devices."),
            N("After that, look at Relations and Binds, or go straight to Export to write everything into your games."),
            N("Your games and your sticks. Start here.")),
        new("relations", N("Relations"),
            N("A relation is one thing you want to do, like \"Gear up\", in all your games and aircraft at once."),
            N("Relations are the heart of JoyPro: you give a relation one button, and that button does the same job in every game and aircraft the relation covers."),
            N("Next, give each relation its button on the Binds page."),
            N("Jobs, like landing gear up. One job works in every plane.")),
        new("binds", N("Binds"),
            N("A bind is the button or axis on your stick that a relation uses."),
            N("A relation says what happens, the bind says where you press it. JoyPro keeps both in your profile."),
            N("To change a button: click a game control in the list, then Record input. When everything has a button, Export writes it into your games."),
            N("Which stick button does which job. Click a job to change its button.")),
        new("modifiers", N("Modifiers"),
            N("A modifier is a shift button: hold it, and other buttons do a second job."),
            N("Modifiers give you more functions than your stick has buttons. They belong to your binds."),
            N("Use a modifier on the Binds page, then export as usual."),
            N("Shift buttons. Hold one, and other buttons do more.")),
        new("layout", N("Layout"),
            N("Pictures of your devices with what every button does, and cheat sheets to print."),
            N("The Layout page only shows your binds; changing a bind on the picture changes the same bind as the Binds page."),
            N("Print or save a cheat sheet, or bind a button right on the picture."),
            N("A picture of your stick with every button.")),
        new("export", N("Export"),
            N("Export writes your binds into the games' own control files."),
            N("This is the step that changes your games. Until you export, your games keep their old controls. JoyPro keeps a backup every time."),
            N("Start the game and fly. If something is wrong, Undo export puts the old files back."),
            N("Send your buttons into the game.")),
        new("check", N("Check"),
            N("Check looks for problems before they hit you in the game, for example two jobs on one button."),
            N("Check reads your profile only; it changes nothing until you press a fix."),
            N("Fix what it found with one click, then export."),
            N("Finds mistakes, like two jobs on one button.")),
        new("hud", N("HUD"),
            N("The HUD shows your sticks and what each button does on top of the game, or for your stream."),
            N("The HUD only shows; it never changes your binds or your games."),
            N("Switch the HUD on and start your game."),
            N("Shows your buttons on top of the game.")),
        new("ffb", N("Force Feedback"),
            N("Force feedback lets a force feedback stick push back, from what the game reports while you fly."),
            N("This works next to your binds: it does not change them."),
            N("Pick your stick and game, then fly and feel it."),
            N("Makes a force feedback stick push back.")),
        new("vjoy", N("Virtual Joystick"),
            N("A virtual joystick or a pretend Xbox controller that JoyPro makes for games that see too few devices or only know a controller."),
            N("Your real devices are sent through one virtual device, so the game sees what it understands."),
            N("Xbox controller: start the game, wait for its title screen, then press Start. Virtual joystick: press Start before the game."),
            N("A pretend controller for games that need one.")),
        new("cockpits", N("Cockpits"),
            N("A book of real cockpits: which switch sits where on the real stick and throttle."),
            N("The cockpit book helps to put your buttons where the real ones are. It changes nothing by itself."),
            N("Use a layout from here in the setup to copy the real aircraft."),
            N("Pictures of real cockpits and their buttons.")),
        new("statistics", N("Statistics"),
            N("What you play and fly most, read from your games' logs on this PC."),
            N("JoyPro uses this to put your most-used aircraft first when it sets up buttons."),
            N("Nothing to do here; it fills by itself while you play."),
            N("What you play most.")),
        new("settings", N("Settings"),
            N("Settings for JoyPro itself: games, looks, language, pages, tours and more."),
            N("Settings change how JoyPro works, not your binds."),
            N("Every setting is saved at once and stays after a restart."),
            N("Settings for JoyPro itself.")),
        new("advanced", N("Advanced"),
            N("Tools for experts: type JoyPro's commands yourself."),
            N("Everything here is also somewhere else in JoyPro with buttons; this page is the expert way."),
            N("Read the answer in the Output panel."),
            N("Tools for experts.")),
        new("output", N("Output"),
            N("The Output panel shows what JoyPro's engine wrote while it worked: the details of the last action."),
            N("It only shows; nothing you read here changes anything."),
            N("Close it again with the same button."),
            N("What JoyPro did, in detail.")),
        new("overlays", N("Notices"),
            N("A notice or question that JoyPro shows over the page."),
            N("JoyPro asks before it does something big, and tells you when a game changed its own files."),
            N("Answer it, and JoyPro goes on."),
            N("A message from JoyPro. Answer it to go on.")),
        new("first-start", N("Your games"),
            N("The first start: which games you have and where they are."),
            N("JoyPro needs to know your games before it can read or write their controls."),
            N("After this, JoyPro builds the list of each game's functions."),
            N("Which games you have.")),
        new("help", N("Help and tours"),
            N("Help is always there: F1 explains any button, and the tours show you the way."),
            N("When you are stuck, help shows you what a button does before you press it."),
            N("Pick a tour, or press F1 and click a button."),
            N("Help for every button, and the tours.")),
        new("setup-wizard", N("Setup wizard"),
            N("The setup wizard puts the functions of your games on your devices for you."),
            N("It makes relations and binds in one step, like you would by hand."),
            N("Look at the result, then export it into your games."),
            N("JoyPro puts game controls on your stick for you."))
    ];

    public static HelpPageText Page(string slug) =>
        Pages.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase)) ?? Pages[1];

    public static string PageOf(UiStage stage) => stage switch
    {
        UiStage.Start => "start",
        UiStage.Relations => "relations",
        UiStage.Binds => "binds",
        UiStage.Modifiers => "modifiers",
        UiStage.Layout => "layout",
        UiStage.Export => "export",
        UiStage.Settings => "settings",
        UiStage.Advanced => "advanced",
        UiStage.Hud => "hud",
        UiStage.FfbTelemetry => "ffb",
        UiStage.VirtualJoystick => "vjoy",
        UiStage.Cockpits => "cockpits",
        UiStage.Statistics => "statistics",
        _ => "start"
    };

    // ---- the kinds of controls, in plain words ----

    public static string KindName(string kind) => kind switch
    {
        "Button" => N("Button"),
        "PageButton" => N("Page button"),
        "ToggleButton" => N("On/off button"),
        "CheckBox" => N("Tick box"),
        "RadioButton" => N("Choice"),
        "ToggleSwitch" => N("Switch"),
        "MenuItem" => N("Menu entry"),
        "TextBox" => N("Text field"),
        "ComboBox" => N("Drop-down list"),
        "Slider" => N("Slider"),
        "NumberBox" => N("Number field"),
        "ListItem" => N("List row"),
        "Tab" => N("Tab"),
        "Expander" => N("Section"),
        "Chip" => N("Label"),
        "Drawing" => N("Picture"),
        "Page" => N("Page"),
        _ => N("Control")
    };

    public static string KindWhat(string kind) => kind switch
    {
        "TextBox" => N("Type here."),
        "ComboBox" => N("Click it and pick one entry from the list."),
        "Slider" => N("Drag it left or right to change the value."),
        "NumberBox" => N("Type a number, or use the mouse wheel over it."),
        "ListItem" => N("Click a row to pick it; the details of the picked row show next to the list."),
        "Tab" => N("Shows another part of this page."),
        "Expander" => N("Click the title to open or close this part."),
        "Chip" => N("A small label that tells you a state. It shows; it does not do anything."),
        "Drawing" => N("A picture you can click on or drag in."),
        "CheckBox" => N("Tick it to switch this on, untick it to switch it off."),
        "ToggleSwitch" => N("Switch it on or off."),
        "RadioButton" => N("Pick one of these choices."),
        _ => N("Click it to do what its name says.")
    };

    // ---- rows of the lists: what one row is ----

    // The type of a list row's item (its DataContext) -> what one row is. Rows not named here fall back to the list's
    // own label.
    public static readonly IReadOnlyDictionary<string, string> RowTexts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["RelationListItem"] = N("One relation: one function, like \"Gear up\", in all your games and aircraft. Click it to see its game commands on the right."),
        ["BindListItem"] = N("One relation with its button. Click it, then Record input and press a button on your stick."),
        ["ModifierListItem"] = N("One modifier: a shift button. Click it to see where it is used."),
        ["GameCardItem"] = N("One of your games: read binds from it, write binds into it, or update its list of functions."),
        ["DeviceCardItem"] = N("One of your devices (stick, throttle, pedals ...). JoyPro shows what it knows about it."),
        ["ExportGameItem"] = N("One game to write into. Tick it to include it in the export."),
        ["ValidationIssueItem"] = N("One problem the check found, with a fix button."),
        ["RelationNodeItem"] = N("One game command inside the relation: the game, the aircraft and the command's name."),
        ["GameBackupItem"] = N("One backup of a game's control files, made before an export. Restore puts it back."),
        ["HistoryVersionItem"] = N("One saved version of your profile. You can compare it or go back to it."),
        ["NavPageItem"] = N("One page of the bottom bar: show or hide it, or drag it to another place."),
        ["SettingsSectionItem"] = N("One part of the settings. Click it to jump there."),
        ["GameInstallItem"] = N("One game JoyPro knows: whether it is installed and where."),
        ["ComponentItem"] = N("One extra part JoyPro can install for you, like a driver."),
        ["SearchMatchItem"] = N("One search result. Click it to use it."),
        ["CatalogItem"] = N("One game command from the game's list of functions."),
        ["DbNodeSearchResultItem"] = N("One game command found by your search. Add it to the relation."),
        ["SelectableTagItem"] = N("A filter. Click it to show only what matches."),
        ["LayoutSuggestionItem"] = N("A suggestion where a function could go on your device."),
        ["TourListItem"] = N("A tour. Start it to be shown around, step by step.")
    };

    // ---- JoyPro's own words, explained in one plain sentence ----

    public static readonly IReadOnlyList<(string Word, string[] Matches, string Explanation)> Words =
    [
        (N("Relation"), ["relation"], N("one job, like \"landing gear up\", in all your games and aircraft at once.")),
        (N("Bind"), ["bind"], N("the button or axis on your stick that does a job.")),
        (N("Profile"), ["profile"], N("the file with all your relations and binds for every game.")),
        (N("Export"), ["export"], N("write your binds into the games' own control files.")),
        (N("Import"), ["import"], N("read the binds a game already has into JoyPro.")),
        (N("Modifier"), ["modifier"], N("a button you hold so other buttons do a second job.")),
        (N("Axis"), ["axis", "axes"], N("a part you move smoothly, like the stick or the throttle, not just press.")),
        (N("Curve"), ["curve"], N("how strongly the game reacts to a small or a big move of an axis.")),
        (N("Dead zone"), ["dead zone"], N("a small area around the middle where moving the axis does nothing.")),
        (N("Command list"), ["command list"], N("the list of every function a game has, read from the game itself.")),
        (N("Detent"), ["detent"], N("a notch on a throttle, like the afterburner gate.")),
        (N("Cheat sheet"), ["cheat sheet"], N("a picture of your device with what every button does, to print.")),
        (N("Force feedback"), ["force feedback", "ffb"], N("a stick with motors that pushes back.")),
        (N("Telemetry"), ["telemetry"], N("live data the game sends while you play, like speed.")),
        (N("vJoy"), ["vjoy", "virtual joystick"], N("a joystick that exists only in software; JoyPro sends your real devices through it.")),
        (N("Xbox pad"), ["xbox pad", "pretend controller", "pretend pad", "virtual xbox"], N("a pretend Xbox controller that JoyPro makes from your stick, for games that only know a controller.")),
        (N("Driver"), ["driver"], N("a small helper program that lets Windows talk to a device.")),
        (N("HidHide"), ["hidhide"], N("a helper that hides your real devices from a game, so it only sees the virtual one.")),
        (N("HUD"), ["hud"], N("an overlay on top of the game that shows your buttons.")),
        (N("Engine"), ["engine"], N("the part of JoyPro that reads and writes the game files; the window asks it to work.")),
        (N("Check"), ["the check", "check finds", "check found"], N("JoyPro's look for problems, like two jobs on one button."))
    ];

    // The words of a text (English, the catalog's) that the popup explains, at most two, the first ones found.
    public static IReadOnlyList<(string Word, string Explanation)> WordsIn(string englishText, int max = 2)
    {
        List<(string, string)> found = [];
        if (string.IsNullOrWhiteSpace(englishText))
            return found;
        string text = englishText.ToLowerInvariant();
        List<(int At, string Word, string Explanation)> hits = [];
        foreach ((string word, string[] matches, string explanation) in Words)
        {
            int at = matches.Select(m => IndexOfWord(text, m)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
            if (at >= 0)
                hits.Add((at, word, explanation));
        }
        foreach ((int _, string word, string explanation) in hits.OrderBy(h => h.At).Take(max))
            found.Add((word, explanation));
        return found;
    }

    private static int IndexOfWord(string text, string word)
    {
        int at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
        {
            bool startOk = at == 0 || !char.IsLetter(text[at - 1]);
            if (startOk)
                return at;
            at += word.Length;
        }
        return -1;
    }
}
