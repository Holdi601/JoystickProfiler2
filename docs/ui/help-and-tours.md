# Help mode, tours and the welcome

JoyPro explains itself over every page: F1's **help mode** explains any control you click instead of running it, the
**tours** point at the real controls one step at a time, the **welcome** asks once what the user wants to do and starts
the fitting path; its quick answer leads into JoyPro's one quick path (the setup run by itself). Wiki page:
`website/content/gui/help.md` (gui/help.html).

Code:

| Part | Files |
|---|---|
| The layer over the window (help catcher, outlines, dims, help popup, tour card, welcome card) | `gui/JoyPro.Gui/Views/Controls/HelpLayer.axaml` (+ `.axaml.cs`), placed in `Views/MainWindow.axaml` inside `UiScaleHost` over `RootLayoutGrid` |
| Keys (F1, Esc, Enter, Space) | `Views/MainWindow.Help.cs` |
| Help mode | `ViewModels/MainWindowViewModel.Help.cs`, `Services/Help/HelpResolver.cs`, `Services/Help/HelpCatalog.cs` |
| Welcome and tours | `ViewModels/MainWindowViewModel.Tours.cs`, `Services/Help/TourCatalog.cs` |
| Quick path (shared with the first start and the Start card) | `ViewModels/MainWindowViewModel.AutoSetupStart.cs` (`StartQuickSetupAsync`), `SetupWizardViewModel.RunQuickAsync` |
| Start options | `Services/StartupOptions.cs` (`--welcome`, `--tour`), `AppShell.cs`, `Program.cs` |
| Start entry | `gui/JoyPro.Gui/Resources/JoyPro 2 - Tour.cmd` |
| Control texts | `gui/JoyPro.Gui/Resources/help-controls.json`, made by `website/build.py --help-catalog` |
| Tour pictures (axis, pretend controller, Export) | `Views/Controls/TourPicture.cs` |
| Tests | `gui/JoyPro.Gui.Tests/HelpModeTests.cs`, `ToursTests.cs`, `QuickSetupTests.cs`, `NoviceTourTests.cs`, `NoviceRenderTests.cs` |

The banner at the top (next to Light / Dark / System) has **Tour of this page** (`PageTourButton`, `StartPageTourCommand`) and **Help** (`HelpButton`, a
menu: **Help mode (F1)** `ToggleHelpModeCommand`, **Tour of this page**, **Tours...** `OpenWelcomeCommand`, **Open the
wiki** `OpenPageWikiCommand`). Settings has the section **Tours** (`SectionId` `tours`): **Show the welcome**, **Help
mode (F1)**, **Ask again at the next start** (`ResetToursCommand`), **Only the essentials** / **Every feature**, **Easy words**
(`ToursEveryFeature`), and the list of every tour (`TourList`) with **Start tour** (`StartTourByIdCommand`).

## Help mode

F1 (no modifier keys) or **Help > Help mode (F1)** switches `IsHelpModeOn` on; F1, Esc or **End help mode** in the
banner switch it off. F1 does nothing while the welcome is open; opening the welcome or a tour switches help mode off.

While it is on, the transparent `HelpCatcher` (help cursor) covers the content, so a click never reaches a control:

1. `HelpLayer.TargetAt` hit-tests the content below the point and `HelpResolver.FindTarget` walks up to the closest
   help target: `Button` (also toggle buttons, check boxes, radio buttons), `MenuItem`, `TextBox`, `ComboBox`,
   `Slider`, `NumericUpDown`, `ListBoxItem`, `TabItem`, `TreeViewItem`, `Expander`, `AutoCompleteBox`, a `Border` with
   the class `chip`, or one of the custom drawn controls (`DeviceMarkerCanvas`, `AxisCurveView`, ...). A part of a
   control's template is lifted to the control (the arrow of a drop-down list is the list); a scroll bar is no target.
2. The page: the bars at the top and bottom (`ProfileHeaderGrid`, `BottomBar`, `BrandBanner`) are the profile bar's
   (`header`), the Output panel is `output`, everything else is the open page (`HelpCatalog.PageOf(CurrentStage)`).
3. `HelpResolver.Resolve` finds the control's catalog entry, in this order: the name of its view model command (the
   command object is compared with the `ICommand` properties of the DataContexts above it), its `x:Name`, its shown
   label and tooltip together, its label, its tooltip. Labels and tooltips are compared in the language shown (the
   catalog's English label goes through `T()` first). Entries of the same page win.
4. Without an entry the text comes from the control itself: the list row's kind (`HelpCatalog.RowTexts`, by the row's
   item type), the drawing (`DrawingTexts`), else its tooltip or placeholder plus what that kind of control does
   (`HelpCatalog.KindWhat`). A click on an empty place shows the page (`HelpCatalog.Pages`).
5. The popup (`HelpTopic`): the kind in plain words (a bottom-bar button is a **Page button**), the title, then the
   **lead**: the entry's plain sentence (`HelpTopic.Plain`, at most 12 words, the wiki's `<!-- easy: ... -->`), else
   what it does (sentence 1 of the entry). A button with a `MenuFlyout` lists its entries. **More details** /
   **Fewer details** (`ToggleHelpDetailsCommand`, `IsHelpDetailsOpen`: open by default, closed with easy words) shows or
   hides the rest: what it does in full, **Why it matters** (the page's place in the bigger picture), **What happens
   next** (sentence 2 of the entry, else the page's next step) and **Words explained** (at most two JoyPro words of the
   text, `HelpCatalog.Words`). **More in the wiki** (`https://holdi601.github.io/JoystickProfiler2/gui/<page>.html#<anchor>`,
   only https URLs are opened). A click on an empty place leads with the page's plain line (`HelpPageText.Easy`).

The help buttons at the top (**Help**, **Tour of this page**, **End help mode**) explain themselves with the help's own
page ("Help and tours"), not the profile bar's. A shared control (a component card on the Virtual Joystick page) is
explained with the page it is on. The Virtual Joystick page's tabs have their own lines (`HelpResolver.NamedTexts`:
"Shows the pretend controller moving with your stick ..."), not "Shows another part of this page".

The hover outline shows which control a click would explain; the mouse wheel still scrolls the page under the catcher.
With the keyboard, Enter or Space on the focused control shows its help instead of pressing it. The cards are placed
next to their control (right, left, below, above, whatever fits first) and always inside the window; they are placed
again after every layout change (scroll, resize, UI scale, page change).

### The catalog: the wiki is the source

Every control's text is its wiki entry (`<!-- entry: KEY -->` in `website/content/gui/<page>.md`, keys: the command
name, `bind:<IsChecked property>`, `click:<handler>`, the `x:Name`, or `label:<text>`).
`python website/build.py --help-catalog` writes `gui/JoyPro.Gui/Resources/help-controls.json` (embedded as
`JoyPro.Gui.HelpControls.json`): one row per control with page, key, kind, wiki anchor, command, binding, click handler,
name, label, tooltip, `w` (what it does) and `x` (what happens next). `help_sentences` makes plain sentences of the
entry (no code, links as their text, no details in parentheses, no engine lines); a first sentence under 40 characters
is joined with the next; the second sentence is only taken up to 170 characters. A control without a hand-written entry
gets its tooltip or its command's code comment.

So write each entry with a self-contained first sentence (what the control does) and a second sentence with the
consequence. A control a novice clicks also gets a plain line right below its `entry` line:
`<!-- easy: Click it, then press a button on your stick. -->` (catalog field `e`, at most 12 words a sentence, no
address where a language would have to pick formal or informal). `website/build.py --check` warns on an easy line over
12 words; `NoviceTourTests` lists the controls that must have one. `python website/build.py --check` warns, and so fails the gate, while `help-controls.json` differs from
what the wiki makes: run `--help-catalog` after editing an entry and commit both.

## The welcome

`ShouldShowWelcome`: the UI settings are kept (`persistUiFiles`), the welcome was never answered
(`tours.welcomeDone` is false), and the run is not a test or sandbox run (`UserFolders.IsSandboxed`, i.e.
`JOYPRO_TEST_SANDBOX`) and not started with `JOYPRO_NO_WELCOME=1`. So everyone who opens a version with the welcome for
the first time sees it once, also users coming from an older 2.x (their `ui-settings.json` has no `tours`). It is
answered by **Start** (with any answer, also **No tour**) or **Not now**; both save `welcomeDone`, `welcomeVersion`,
`choice` and `everyFeature`.

Order at start (`MainWindowViewModel.AppServices.cs`): after the startup detection, when the first-start game setup
shows, the welcome waits until that window is closed (Done or Later, `MarkGameSetupDone` -> `OnGameSetupClosedForWelcome`);
otherwise it shows at once. When no welcome shows, a tour that was running at the last close goes on at its step.

`JoyPro.Gui.exe --welcome` (or `--tour`) opens the welcome on the window's first `Opened`, also when it was answered.
A second start with `--welcome` finds the running JoyPro (single-instance guard), leaves `:welcome`
(`StartupOptions.WelcomeRequest`) in its forwarded request list and signals it; the running window comes to the front
and opens the welcome (`AppShell`). `Resources/JoyPro 2 - Tour.cmd` is copied next to `JoyPro.Gui.exe` on build and
publish (`JoyPro.Gui.csproj`) and runs `start "" "%~dp0JoyPro.Gui.exe" --welcome`; `tools/publish_release.ps1` fails
when the package misses it.

### The paths and the suggestion

| Id | Answer | What starts |
|---|---|---|
| `pad` | Play ACE COMBAT with my flight stick | tour: the pretend controller (picture), **Set it all up for me** (the quick setup, `cmd:QuickSetupCommand`; the card's own button does it too, `TourActions.QuickSetup`; Next does the step; while the setup window is open the card waits: "Finish the setup window first", see below), Virtual Joystick page, install the helper ViGEmBus (left out when it is installed, `TourConditions.ViGEmInstalled`; the license box glows first, then **Install ViGEmBus**; Next without the helper first says what will not work), "Nothing to change here" (extra), Try it (opens the Live test tab, `OpenTab`, waits for **Test**, then stays: "Now move your stick ... Then click Next"), when to press Start (start the game; at its title screen Alt+Tab back here, Start, Alt+Tab back; picture `alttab`), Mapping tab (extra), help, "Done!" |
| `dcs` | DCS World or Falcon BMS | tour: profile, Import, Relations (list, commands), Binds (axis curves with the axis picture, detent mode), Modifiers, Export (picture), Check |
| `quick` | Just make my stick work | the quick setup; its "Write to my game" then starts the quick tour on the Start page: "It worked! <game> now has your buttons" (only right after the write, `TourConditions.NotJustWritten`), Binds, pick a game control in the list (`WaitFor: BindSelected`), Record input (`Prepare: SelectBind`), Export again (left out for pad games, `TourConditions.PadGamesOnly`), help, "Done! Start <game>" (on the Start page) |
| `new` | Show me around | big tour (bigger text and outline): profile, Start page, import, relations, binds, record, modifiers, layout, export (picture), check |
| `jp1` | I used JoyPro 1 | tour: where JoyPro 1's load/save/new, import, relations, binds and curves, modifiers, export, validation, overview and game paths are now; a step on **Open profile...** (JoyPro 2 opens JoyPro 1 profiles as they are) whose card offers **Open my JoyPro 1 profile** when `Documents\JoyPro\last.pr0file` is there (`TourStep.Action`; JoyPro opens a copy, `profiles\From JoyPro 1.pr0file`, so JoyPro 1's file stays as it is) |
| `none` | No tour | nothing |

Every path tour ends with the step "Help is always there" on the Help button (the pad and quick tours then with
"Done!"). **Short** (only the essentials) leaves out the steps marked `Essential: false`. A step's `SkipIf` leaves it
out while its condition holds (also when it becomes true while the step shows: the helper installed meanwhile).
`{0}` in a step's text is the tour's game (`TourGameName`: the games of the last quick setup, else the installed
ones).

`TourCatalog.SuggestPath(installed games, JoyPro 1 profile)` pre-selects the answer and marks it **suggested**: JoyPro 1's
last profile (`Documents\JoyPro\last.pr0file`) on the PC -> `jp1`; DCS or Falcon BMS
installed -> `dcs`; at least one game and only pad games (`AceCombat7`, `AceCombat8`, `HLLVietnam`) -> `pad`; anything
else, also no game found -> `quick`. The installed games are the engine's detected, not hidden installs (the Start
page's game cards as a fallback); the line under the answers names up to four of them.

## Tours

A tour is a `TourDefinition` of `TourStep`s. A step names its target:

| Target | Finds |
|---|---|
| `nav:<page>` | the page's button of the bottom bar (`Nav_<page>`, `nav:output` = `EngineOutputQuickToggle`) |
| `name:<x:Name>` | the first shown control with that name |
| `cmd:<Command>` | the first shown button bound to that view model command |
| `tab:<English header>` | the first shown tab whose header is the translated text |
| `section:<id>` | the Settings section (the step opens it) |
| `a\|b` | the first of the alternatives that is shown |
| `page` | the open page (`PagesHost`) |
| empty / `none` | nothing: the whole window is dimmed and the card stands in the middle |

`x>y` (`name:VirtualJoystickViGEmNeeded>ComponentConsentBox`) is the control named `y` inside the control `x` finds (the
license box of the ViGEmBus card, not of the first component card on the page).

Targets are found by name, command or header, never by position, so window size, UI scale, theme and the user's order
and hidden pages of the bottom bar do not matter. `Stage` opens a page before the step; `WaitStage` waits until the user
opens that page (its bottom-bar button is the target; **Next** opens the page itself, also a hidden one); `WaitClick`
waits for a click on the target button (**Next** goes on without it). The layer dims the window around a hole over the
target, so the target stays clickable, outlines it and places the card beside it. A target not on screen yet is looked
up again on the next layout.

**Back**, **Next** (**Finish** on the last step, the one big blue button), **Skip** (next step without doing it) and
**End tour** (both small text links) are on the card. A waiting step says so in a coloured box: "Waiting for you to
click the glowing button. Or press Next ...". The tour's control glows (`TourOutline` has a blue shadow) and the rest of
the window is dimmed with `JP.TourDimBrush` (darker than a question's dim, so it shows on a dark page). A step's
`OpenTab` opens that tab of its page; `Picture` draws a small explainer (`TourPictures`: `axis`, `pad`, `export`,
`alttab`: the keys Alt + Tab and the game and JoyPro's window with arrows both ways) with a
caption above the text. A tour ends finished (last step) or ended (**End tour**); only a finished tour counts as seen. **Skip** is hidden on the
last step (it would do what **Finish** does). A tour joined in the middle (the pad steps after the first start's quick
path) counts its steps from there ("Step 1 of 6", not "Step 4 of 9"), and Back stops there. The pad steps begin there
with "Why this page" ("Your game only knows an Xbox controller. JoyPro turns your stick into a pretend one."), a step
left out (`TourConditions.FromTheStart`) when the tour runs from its first card, which says the same. The step that
opens the page points at the glowing bar button instead of naming it (it is "Pretend controller" for a pad-only player,
`VirtualJoystickNavTitle`, else "Virtual Joystick").

### Steps that wait or come in two parts

- **The setup window** (`TourStep.Waiting` / `WaitingEasy`, the pad tour's `TourActions.QuickSetup` step): once the quick
  setup is opened from the card, its Next, or the page's own button (a click on the glowing target), the card stays on
  its step (`TourWaitsForSetup`): title "Finish the setup window first", no glow (target `none`), Next is **Show the
  setup window** (brings the window to the front). It never runs ahead (a page change does not move it either).
  There is no **Skip** while it waits (Skip would run ahead; the key does what Next does).
  **Set up the pretend controller** in the window goes on to the pad steps (`ContinueWithPadSteps`); the window closed
  (`OnSetupWizardWindowClosed`, from `MainWindow`) with the buttons applied does the same, without them the step shows
  again.
- **Two parts** (`ThenWhen`, `Then`, `ThenEasy`, `ThenTarget`): once the condition holds, the card shows its second text
  and points at `ThenTarget`. `TourConditions.Clicked`: Test was clicked, the card says to move the stick and waits for
  Next. `TourConditions.ViGEmConsent`: the ViGEmBus license box is ticked (here or in the list of components), the card
  says "now click Install ViGEmBus" and the button glows.
- **A pick in a list** (`WaitFor: TourConditions.BindSelected`): the Binds list glows; a pick goes on by itself, Next
  picks the first row. **Record input** shows only with a row picked, so its step has `Prepare: TourPrepare.SelectBind`
  (it picks the first row when none is).
- **Warn before skipping** (`WarnSkip` / `WarnSkipEasy`): the helper step without the helper: the first Next or Skip shows
  what will not work ("your game will not see your stick"), the second goes on.

Every card has **Easier words** (**Normal words** while easy words are on, `ToggleEasyWordsCommand`), so the switch is
there in the moment a card is too hard: the first start's quick path skips the welcome.

Page tours: `TourCatalog.PageTour(page)` builds "Tour of the <page> page" from the page's `HelpCatalog.Pages` text (what
it is, where it fits), the page's own steps and "What comes next". **Tour of this page** always shows every step; the
list in Settings > Tours uses the section's own **Only the essentials** / **Every feature**, **Easy words** choice (every feature by
default).

### What is remembered

`%APPDATA%\JoyPro2\ui-settings.json`, `"tours"` (`ToursSettings`, camelCase like the rest of the file):

| Key | Meaning |
|---|---|
| `welcomeDone` | the welcome was answered (it does not show by itself again) |
| `welcomeVersion` | the JoyPro version that answered it |
| `choice` | the answer: `pad`, `dcs`, `quick`, `new`, `jp1` or `none` |
| `everyFeature` | the welcome's essentials / every feature choice |
| `seen` | the ids of the tours run to their last step (`page:<slug>` for page tours) |
| `activeTour`, `activeStep`, `activeEveryFeature` | the tour in progress and its step: it goes on after a restart |

Every step change saves at once. **Ask again at the next start** resets all of it.

## Easy words

One switch for anyone who wants it simple (children included): Settings > Tours (`EasyWordsToggle`), the welcome
(`WelcomeEasyWords`), the first-start window (`GameSetupEasyWords`, next to its blue button: its quick path skips the
welcome) and **Easier words** on every tour card, `EasyWords`, kept in `ui-settings.json` (`"tours"` > `"easyWords"`; **Ask again at the next
start** keeps it). It turns on:

- the big card style for every tour (`TourIsBig`), bigger welcome texts;
- each step's `Easy` text (and `EasyTitle`), the welcome's `EasyDescription`s and the short suggestion line. They are
  their own English texts, so a language can write them in the informal address (German "du"); the normal texts keep
  the formal one ("Sie");
- F1's help with its details closed (the plain sentence alone, **More details** opens the rest).

The rules, checked by `NoviceTourTests`: every step of the pad, quick, new and DCS tours has an easy text, at most 12
words a sentence and 4 sentences, no HidHide / vJoy / mapping / driver / engine; normal card sentences at most 15
words; every page has a plain line; the controls a novice clicks lead with a plain sentence; German easy texts never
say "Sie", normal ones never "du" (also the second parts, the waiting cards and the skip warnings); a text shown in both
modes and every card title is neutral (infinitive: "Jetzt den Stick bewegen."); UI buttons are "Schaltfläche" in
German, "Taste" is a button on the stick.

One name for the virtual Xbox controller in every novice text: EN "pretend controller" (the button **Set up the pretend
controller**, the Virtual Joystick page's title for a player whose games are all on it), DE "Ersatz-Controller"
(**Ersatz-Controller einrichten**); each language keeps the one word its files already used. "Xbox pad" stays only as
the name of the mode in the Games list.

## The quick answer

There is one quick path in JoyPro (docs/auto-setup-eval.md): **Done, and set everything up for me** of the first-start game
setup, the Start card's **Set it all up for me**, and the welcome's `quick` answer all call `StartQuickSetupAsync`
(Settings > Tours starts the quick tour itself): the games with nothing assigned (else the ones in the profile) go into a setup
wizard in `QuickMode`, which runs by itself (`RunQuickAsync`: devices, aircraft, plan, review) up to its plain-words
review (`QuickSummary`, `QuickNotes` for devices JoyPro does not know). **Use these buttons** applies the plan, **Write to
my game** exports the games (`QuickExportAsync`); once something was written the setup window closes and the quick tour
starts. Games that only know an Xbox controller (`TourCatalog.PadGames`) get **Set up the pretend controller** instead
(`SetupWizardViewModel.PadGamesOnly`): the window closes and the pad tour goes on at its first Virtual Joystick step
(`ContinueWithPadSteps`).

The review in plain words: function names without ids (`SetupReviewRow.PlainName`: "TrimRudderLeft" -> "Trim rudder
left", "Gear TOGGLE (GearIn)" -> "Gear toggle") and in the language shown (`Services/FunctionNames.cs`: a fixed list of
the pad games' actions, the plain flight slot names IL-2 and the other flight games get, and the PlayStation face buttons
Square / Cross / Circle / Triangle; translation keys `Function name: <name>`, so "Look" never takes the HUD's
"Aussehen"; a name the list does not know stays as the game calls it), no step strip (Priorities and Plan mean nothing
there), the summary "JoyPro put 16 game controls for <game> on your <devices>" (no "Done!" before anything is done), a
calm status line ("All important buttons are set. Some rare extras had no room (17). That is OK.", or "A few
important ones had no room: Change something shows them." when the plan's quality says an essential one is missing), the list full width, the picture big on request (**Show the picture
big**), the reasons, move / swap / remove and what did not fit behind **Change something**, and after **Use these
buttons** that button greyed out so the next one is the only blue one.

After **Write to my game** the window closes and the Start page shows the quick tour's "It worked!" card (not the
Export page with its mode warnings). On the Start page the setup card's one blue button is **Set it all up for me**;
the full wizard is the grey **Set up step by step...**, and while the card offers the setup no next step is blue and
"No relations yet / Auto Group" is not offered.

One setup at a time: a quick setup asked for while one runs only brings its window to the front; the window shows the
newest wizard. The first start's **Done, and set everything up for me** answers the welcome with `quick`, so the welcome
does not open next to the setup.

When it stops before the review (no game, no stick, no plan) `QuickStuck` is set and the quick panel offers **Try again**
(`QuickTryAgainCommand`, runs it again) and **Set it up step by step** (`QuickStepByStepCommand`, leaves the quick mode
and shows the first step): never a dead end.

## Tests

- `HelpModeTests`: every wiki control is in the catalog with a text; every interactive control on every page gets a
  help text; a click in help mode lands on the layer and shows the control's text without running it; a menu button
  lists its entries, fields and rows explain themselves; renders of the help popup, tour steps and the welcome in light
  and dark at 1280x800 and 1920x1080 (`JOYPRO_HELP_RENDER_OUT=<folder>`).
- `ToursTests`: the welcome once per user (also for 2.6 settings), never on sandbox runs, `--welcome` always, the path
  suggested from a fake list of installed games, essentials vs every feature, a tour waiting for the page click and
  the target click, Back / Next / Skip / End, progress and seen state over a restart, and the targets found with another
  bar order, a hidden page, three window sizes and UI scale 1.5.
- `QuickSetupTests`: with a fake engine (IL-2 + WinWing URSA MINOR): the welcome suggests the quick answer and leads
  into the quick path up to its review (plain words, "Use these buttons", "Write to my game"), then apply; a stick JoyPro
  does not know still reaches the review; no stick: Try again, or step by step. `QuickSetupPersonaTests` runs the same
  path with the real engine.
- `NoviceTourTests` (second review): the pad card waits for the setup window until it is applied or closed (M1); the
  install step points at the license box, then at Install, and Next first warns (B1); the Test card stays after the
  click (M5); "It worked!" first on the Start page (M4); the list step before Record input in the quick and new-user
  tours and the Record step picking a row (M3); F1 of Record input says to pick a row; the quick review's calm status
  line (M7); the function names in German and in every language (M9). `VirtualJoystickTests.TitleScreenGame_StartBeforeTheGame_SaysTooEarly`:
  Start before ACE COMBAT 8 runs says "Too early" with Alt+Tab.
- Third review: `QuickWriteQuestionTests` walks **Write to my game** with an explicit recording confirm handler
  (empty game: no question; stick binds, imported or written before: one plain question; **Keep the old ones too**
  writes with "Only add" once; Cancel writes nothing and says how to go on; the Export page keeps "Remove binds?"; a
  MainWindow that attaches and lets go never takes the test's handler away, MainWindow restores the handler it found).
  `ThirdNoviceReviewTests`: IL-2's names in German and every language, the Binds list's names, the ViGEmBus card in
  German, the skip warning gone once the box is ticked, no Skip while the card waits for the setup window, the "Why
  this page" card where the first start joins the pad steps, the bar button "Pretend controller", the quick review's
  plain inputs. `QuickSetupPersonaTests` asserts the IL-2 calm line and that the walk asked nothing.
- `NoviceRenderTests.SecondReviewScreens` (`JOYPRO_NOVICE_RENDER_OUT`, `JOYPRO_NOVICE_RENDER_ONLY=en-normal`): those
  screens and the first-start window with Easy words, offscreen, with a log.
- `website/build.py --check`: the catalog is up to date with the wiki.
