---
title: Help mode and tours
status: full
summary: F1 explains any control you click, the welcome asks once what you want to do, the tours point at the real buttons, and the quick answer leads into the quick setup that gets your buttons working.
---
JoyPro explains itself in four ways. **Help mode** (F1) turns every click into a question: click a button, field, list
row or tab and a small window next to it says what it does, where it fits and what happens next, without running it.
**Tours** point at the real buttons one step at a time and wait for your click; every page has its own **Tour of this
page**. The **welcome** asks once what you want to do and starts the way that fits you. Its quick answer, "Just make
my stick work", starts JoyPro's quick setup: the same one as **Done, and set everything up for me** of the first start
and **Set it all up for me** on the Start page. **Easy words** makes all of it as simple as it gets: big cards and the
shortest sentences, for children and for anyone who wants it simple.

:::steps
1. **Your games** window<br>at the very first start
2. **The welcome**<br>What do you want to do?
3. **A tour or the quick setup**<br>whatever fits your answer
:::

**Done, and set everything up for me** in the **Your games** window skips the welcome: the quick setup starts at once.
Without the **Your games** window (you update from an older JoyPro 2), the welcome comes first.

:::layer beginner
## Help mode: click anything to learn what it does

Press **F1** (or **Help** at the top right > **Help mode (F1)**). The mouse pointer turns into a question mark and a
line at the top says "Help mode: click anything to see what it does. F1 or Esc ends it."

- **Click a control**: it does **not** run. A small window next to it shows:
  - first one plain sentence of what it does ("Click it, then press a button on your stick."),
  - **More details** / **Fewer details**: the longer explanation, **Why it matters** (what this part of JoyPro is for
    in the bigger picture), **What happens next** and **Words explained** (up to two JoyPro words of the text in one
    plain sentence each: "Relation: one job, like 'landing gear up', in all your games and aircraft at once."). Open
    by default; with **Easy words** it starts closed,
  - **More in the wiki**: the control's entry on this website.
- **A button with a menu** (Help, More ...) lists its entries, each with a short line.
- **An empty place** of a page explains the page.
- The outline under the mouse shows which control a click would explain. The mouse wheel still scrolls the page.
- With the keyboard: move to a control with Tab and press **Enter** or **Space** to see its help.
- **F1**, **Esc** or **End help mode** in the line at the top ends help mode; clicks run the buttons again.

## The welcome

The first time you open this version of JoyPro (also when you come from an older JoyPro 2), it asks once: **What do
you want to do?** When the "Your games" window of the [[page:gui/first-start.html|first start]] shows, the welcome comes
right after it. JoyPro picks the answer that fits the games it found and marks it **suggested**; pick another if it fits
you better.

| Answer | What happens |
|---|---|
| **Play ACE COMBAT with my flight stick** | A tour that turns your stick and throttle into a pretend Xbox controller: **Set it all up for me** first (the quick setup; the card waits while its window is open, and its **Set up the pretend controller** brings you back), then the Virtual Joystick page: the free helper ViGEmBus when it is missing (first tick the box below the license, then **Install ViGEmBus**; Windows asks once for permission), **Test** on the Live test tab (the card stays until you moved your stick and pressed **Next**), and when to press **Start**: start the game; at its title screen hold Alt and press Tab to come back, click **Start**, Alt+Tab back to the game. |
| **DCS World or Falcon BMS** | A tour of the DCS way: your profile, reading your DCS binds, relations across all aircraft, axis curves, detent mode, export and the check. |
| **Just make my stick work** | The quick setup (below), then "It worked!" on the Start page and a short tour: where to change a button later (pick a game control in the list, then **Record input**), how to send it to the game again, "Done! Start your game". |
| **Show me around** | The five ideas behind JoyPro (profile, relations, binds, export, check), one at a time, with big highlights; it waits for your clicks. |
| **I used JoyPro 1** | Where each JoyPro 1 function sits now: load and save, import, relations, binds and curves, export, the validation. JoyPro 2 opens your JoyPro 1 profile as it is: the tour offers **Open my JoyPro 1 profile** when it finds it (a copy is opened; JoyPro 1's file stays). Suggested when that profile is on the PC. |
| **No tour** | Nothing starts. |

**How long a tour?** **Short** shows the steps you need; **Long, with every extra** also shows the extras (for example
the detent step of the DCS tour). **Easy words** (also in Settings > **Tours**) makes every card big and its text as
short as it gets; German then says "du". Press **Start**, or **Not now** to close the welcome. Either way
it does not come back by itself; **Help > Tours...**, Settings > **Tours** > **Show the welcome** and the **JoyPro 2 -
Tour** entry next to JoyPro open it again any time.

## Tours

A tour dims the window and makes the real control it talks about glow. A card next to it says what to do; four
cards also have a small picture: what a joystick axis is, what the pretend controller is, what Export does, and how
Alt+Tab brings you back from a full-screen game.

- Many steps **wait for you**: "Click Relations in the bar at the bottom." and "Waiting for you to click the glowing
  button." The step goes on by itself when you click it. On a step that
  asks you to open a page, **Next** opens it for you, even a page you hid in the bottom bar; on a step that waits for
  another click, **Next** goes on without it.
- **Next** is the big blue button. **Back** goes one step back; the small links **Skip** (leaves a step out) and
  **End tour** (stops). The last step's **Finish** ends it.
- Close JoyPro in the middle of a tour and it goes on at the same step at the next start.
- **Tour of this page** (at the top right, next to **Help**, and in the **Help** menu) shows the open page: what it is for, its main
  parts and what comes next.
- Settings > **Tours** lists every tour with **Start tour**; a tour you ran to its end is marked **seen**.

## The quick setup: "Just make my stick work"

For anyone who wants working buttons and nothing else, the welcome's quick answer opens JoyPro's quick setup (the
[[page:gui/setup-wizard.html|setup wizard]] running by itself):

1. JoyPro finds your games and your devices and makes the setup on its own. A device JoyPro does not know is named in
   plain words and set up like a usual device of its kind.
2. The review shows the result as a plain list: which button does what ("Button 4: Reload guns"). **Show the picture
   big** shows your device with every button; **Change something** opens the tools to move, swap or leave out a
   function.
3. You press **Use these buttons**, then **Write to my game** (only one blue button at a time). The game's old controls
   are kept as a backup. The window closes and a short tour shows where to change a button later.
4. For a game that only knows an Xbox controller (ACE COMBAT, Hell Let Loose: Vietnam) the second button is **Set up the pretend controller** instead: the Virtual Joystick page shows the last steps (ViGEmBus, **Test**, **Start** at the title
   screen). Those games need no export.

When something is in the way (no game found, no stick found), the window says what to do in plain words, with **Try
again** and **Set it up step by step** (the setup wizard with all its steps).
:::

:::layer advanced
## Details

- **Which answer is suggested**: DCS or Falcon BMS installed: **DCS World or Falcon BMS**. Only
  Xbox-controller games installed (ACE COMBAT 7, ACE COMBAT 8, Hell Let Loose: Vietnam): **Play ACE COMBAT with my flight
  stick**. Anything else, or no game found yet: **Just make my stick work**. The line above the answers names the games
  it was made from.
- **The first start's Done, and set everything up for me** answers the welcome with the quick way: the setup opens, the
  welcome does not open next to it.
- **Once per user**: the welcome counts as answered after **Start** or **Not now**, also for **No tour**. Settings >
  Tours > **Ask again at the next start** brings it back at the next start and marks every tour unseen again.
- **Never on test runs**: the welcome does not open when JoyPro runs in its test sandbox or with the environment
  variable `JOYPRO_NO_WELCOME=1`.
- **The JoyPro 2 - Tour entry**: `JoyPro 2 - Tour.cmd` next to `JoyPro.Gui.exe` starts `JoyPro.Gui.exe --welcome` (also
  `--tour`): the welcome opens even when it was answered before. When JoyPro is already running, the running one
  comes to the front and opens the welcome.
- **Essentials or every feature**: the welcome's choice is kept for the path tours; Settings > Tours has its own choice
  for the tours started from its list. **Tour of this page** always shows every step.
- **Tours find their controls by name**, not by position: your window size, UI scale, theme and the order of the
  bottom bar do not matter. A control that is not on screen: the card stands in the middle of the dimmed window.
- **F1** does nothing while the welcome is open. Starting a tour or the welcome ends help mode.
- **The quick setup** is the one of the first start and the Start page: it runs the setup wizard's steps by itself
  ([[cli:setup devices]], [[cli:setup aircraft]], [[cli:setup plan]]) up to its review; **Use these buttons** applies the
  plan ([[cli:setup apply]]) and **Write to my game** exports the games like the Export page (with its backup).

:::ingame The quick setup writes into the game
The quick setup changes a game only when you press **Write to my game**: it exports the games, like **Export** on the
Export page. The game's old controls are kept as a backup; **Undo export** on the Export
page puts them back. Help mode and the tours never change your profile or your games by themselves; a tour step only
shows the button, you press it.
:::
:::

:::layer technical
## Behind the help

- Layer: `Views/Controls/HelpLayer.axaml` (+ `.axaml.cs`) over the window's content, inside the UI scale; keys F1, Esc,
  Enter and Space: `Views/MainWindow.Help.cs`. View model: `MainWindowViewModel.Help.cs`, `.Tours.cs`;
  the quick answer calls `StartQuickSetupAsync` (`.AutoSetupStart.cs`). The control under a click and its text: `Services/Help/HelpResolver.cs`; the page texts and the
  explained words: `Services/Help/HelpCatalog.cs`; the tours: `Services/Help/TourCatalog.cs`.
- The per-control texts are the entries of this wiki: `python website/build.py --help-catalog` writes the first two
  sentences of every entry into `gui/JoyPro.Gui/Resources/help-controls.json` (embedded in the GUI); `--check` fails
  while that file is older than the wiki. A control is matched by its command, its name, its label or its tooltip.
- What the welcome and the tours remember: `ui-settings.json`, `"tours"` (answered, the path picked, essentials or
  every feature, the tours seen, the tour in progress and its step).
- Start options: `Services/StartupOptions.cs` (`--welcome`, `--tour`), the second start's hand-over:
  `AppShell.cs`. Reference: [[doc:ui/help-and-tours|Help mode and tours]].
:::

<!-- entry: CloseHelpPopupCommand -->
<!-- easy: Closes this little window. -->
Closes this help window; help mode stays on. Click the next control to read its help, or press F1 or Esc to end help
mode.

<!-- entry: OpenHelpWikiCommand -->
<!-- easy: Opens the JoyPro website in your browser. -->
**More in the wiki** opens this control's entry on the JoyPro wiki in your browser. There you find the details, the
pictures and how the control works together with the rest of JoyPro.

<!-- entry: EndHelpModeCommand -->
<!-- easy: Stops the help. Buttons work normally again. -->
**End help mode** switches help mode off, so clicks run the buttons again. F1 or Esc does the same.

<!-- entry: TourBackCommand -->
<!-- easy: Goes back one step. -->
**Back** goes to the step before in this tour. It is greyed out on the first step.

<!-- entry: TourNextCommand -->
<!-- easy: Goes to the next step. -->
**Next** goes to the next step of the tour, also when the step waits for your click. On a step that asks you to open
a page it opens the page for you, even one you hid in the bottom bar; on the last step the button says **Finish** and
ends the tour.

<!-- entry: TourSkipCommand -->
<!-- easy: Leaves this step out. -->
**Skip** leaves this step out and goes to the next one without doing it. On the last step it ends the tour.

<!-- entry: OpenJoyPro1ProfileCommand -->
**Open my JoyPro 1 profile** (on the card of the "I used JoyPro 1" tour, when JoyPro finds the profile JoyPro 1 used
last, `last.pr0file` in your Documents folder `JoyPro`): opens it in JoyPro 2, which reads JoyPro 1 profiles as they
are. JoyPro opens a copy (`From JoyPro 1` in its profiles folder), so JoyPro 1's own file stays as it is; a second click
opens that copy again.

<!-- entry: EndTourCommand -->
<!-- easy: Stops the tour. -->
**End tour** stops the tour where it is and takes the dimming away. Start it again any time with **Help > Tours...**
or Settings > **Tours**.

<!-- entry: ToggleEasyWordsCommand -->
<!-- easy: Switches to big text and very short sentences. -->
**Easier words** (on every tour card, **Normal words** while they are on) switches easy words on or off right where a card
is hard to read: big cards, the shortest texts, and help that leads with one plain sentence. The same switch as in
Settings > Tours; it stays after a restart.

<!-- entry: bind:IsSelected -->
<!-- easy: Pick the answer that fits you best. -->
One answer to "What do you want to do?": pick the one that fits you, then press **Start**. The answer marked
**suggested** is the one JoyPro picked from the games it found on this PC; **No tour** starts nothing.

<!-- entry: bind:WelcomeEssentials -->
<!-- easy: A short tour with only the important steps. -->
**Only the essentials**: the tour you pick shows only the steps you need, the shortest way. JoyPro keeps this choice
for later.

<!-- entry: bind:WelcomeEveryFeature -->
<!-- easy: A long tour that shows everything. -->
**Every feature**: the tour you pick shows every step, also the extras.

<!-- entry: StartWelcomeChoiceCommand -->
<!-- easy: Starts what you picked. -->
**Start** begins the way you picked: its tour, the quick setup, or nothing for **No tour**. The welcome then counts as
answered and does not open by itself again.

<!-- entry: SkipWelcomeCommand -->
<!-- easy: Closes the welcome. Nothing starts. -->
**Not now** closes the welcome without starting anything. It does not come back by itself; **Help > Tours...**,
Settings > **Tours** or the **JoyPro 2 - Tour** entry next to JoyPro opens it again.

<!-- entry: TourDoActionCommand -->
<!-- easy: JoyPro does this step for you. -->
The card's own button that does the step for you: on the pad tour, **Set it all up for me** starts the quick setup, and the tour goes on behind its window.

<!-- entry: ToggleHelpDetailsCommand -->
<!-- easy: Shows or hides the longer text. -->
**More details** in the help window shows the longer explanation: what the control does in full, why it matters, what happens next and the JoyPro words in it. With easy words it starts closed.

<!-- entry: bind:EasyWords -->
<!-- easy: Big text and short sentences in tours and help. -->
**Easy words**: big cards and the shortest texts in the tours, and the help leads with one plain sentence (German says
"du"). The same switch as in Settings > Tours; it stays after a restart.
