# The Check: "Problems found by the check"

The Export page's Check (the bottom bar's "Check" goes there) lists what would go wrong in the game, in words a
first-time PC user understands. Since 2.5.0 every card says what is wrong in game words (the aircraft, the buttons as
"device · button", the commands as the game calls them), why it matters in the game, and what to click. No card shows an
id, "node", "relation item key" or another internal word (one exception: the details of an unknown command name its game
id for a support question).

Code: `gui/JoyPro.Gui/Views/Controls/CheckProblemsPanel.axaml`, `ViewModels/MainWindowViewModel.CheckCards.cs` (the
cards), `MainWindowViewModel.Duplicates.cs` (sections, search, order, fixes for many), `Models/CheckIssueModels.cs`.
Engine: `relations duplicates` and `relations conflicts` ([CLI wiki](../../engine/docs/ENGINE_CLI_WIKI.md),
[relation builder](../relation-builder.md#the-checks-errors)).

## Sections

Errors first (red, with their own icon), then warnings (amber), then notes:

| Section | Kind | What it is |
|---|---|---|
| One button does two things | error | an input is bound in two relations that both have a command of the same aircraft on: one press runs both |
| One button runs two commands | error | one relation has two commands of the same aircraft (or game) on |
| Other errors | error | the rest of the check's errors (a modifier button that is also a normal button, ...) |
| Same command on two buttons | warning | one command of an aircraft is on in two relations: the game gets it on two buttons |
| Copied relations | warning | an import made copies of a relation on the same button |
| Not written to the game | warning | "Only add" left a bind out: the game already uses the button for something else; or the game has no free place for another input of the command (Elite: Primary and Secondary in use) |
| Notes | note | harmless, worth a look |

The counts at the top count cards ("To look at: 9 errors, 167 warnings, 0 notes"). The search finds cards by relation,
aircraft, button and command; the kind filter shows one kind; the order ("By relation", "By aircraft", "By game", "By
confidence") also groups the cards under a header (the relation, the aircraft) where a group has several cards.
"Ignored warnings (n)" next to them (only while there are some) opens the list of the warnings you ignored (see
[Ignored warnings](#ignored-warnings)).

## A card

1. **Title**: "Same command on two buttons", "One button does two things", "Not written to DCS World", ...
2. **The buttons**: "LEFT VPC Throttle MT-50 CM · BTN 9 ('A10 - DMS Down') ↔ VKB-Sim Gunfighter · BTN 16 ('Sensor Select
   Aft')" - device · button, the relation in brackets.
3. **One sentence** what happens in the game: "In F-16C Block 50/52, both buttons do 'Display Management Switch - Down'.
   Usually only one of them is wanted."
4. **Aircraft -> command**: a table, the first 3 rows and "Show all 7". Only aircraft that are ticked in both relations
   are listed; an unticked aircraft never makes a card.
5. **Caused by**: the profile history's entry that brought it, when the history tells ("Caused by: Manage with AI (366
   relation changes) on 21.10.2026"). A history entry whose change names other relations only is not taken (the
   history thins out old versions).
6. **The fixes**: one recommended fix as the accent button with a tick (JoyPro's suggestion: "Keep only on VKB-Sim
   Gunfighter · BTN 16"), the other fixes ("Keep only on ...", "Ignore"), and "More" with a down arrow (the tick and
   the arrow are drawn icons since 2.6.0: the ✓ and ▾ characters came from a fallback font whose taller line made
   those buttons higher than their neighbours) (switch it off on both buttons, remove an
   unknown command, find similar). Every fix is one undo step.
7. **Open '<relation>' (<button>)**: one button per relation. The relation opens with exactly the card's commands marked
   (tinted) and scrolled into view, their aircraft list open with the card's aircraft marked. **Back to the check**
   above the relation's name (since 2.6.0; it stays while that relation is open) and Back (header, Alt+Left) return to
   the Check at the card; after a change in the relation the check runs again.
8. **Details** (collapsed): how sure JoyPro is and why ("96% sure: keep it only on ...", "It was only in 'Trim Right'
   before"), where an unknown command came from.

Fixes per kind:

| Card | Recommended | Others | More |
|---|---|---|---|
| Same command on two buttons | the suggestion (keep it on one button, or switch it off) | Keep only on X, Ignore, Replace 'command' on X with... (one per button; under More when the card has several commands) | Switch it off on both buttons, unknown commands |
| One button does two things | - | Keep only 'X' on this button (the aircraft is switched off in the other relations), Ignore | Move 'X' to another button, Switch off all of them, unknown commands |
| One button runs two commands | - | Keep only 'A' (the other commands of the relation are switched off for these aircraft), Ignore | Move one to another button, Switch all of them off, unknown commands |
| Not written to the game | Export with 'Update' to replace it (not for "no free place") | Move to another button, Keep as is | - |
| Copied relations | Merge into one | - | Merge all copied relations |

## Ignored warnings

"Ignore" (2.6.1; "Keep both" before, which was the wrong word for three or more buttons) is on every card of "Same
command on two buttons", "One button does two things" and "One button runs two commands". The binds stay as they are;
the card goes away and JoyPro remembers it in the profile (`KeptDuplicates`, one undo step). It stays hidden while the
same buttons (relations, commands) or fewer of them have it. When the command lands on one more button (one more
relation on the button, one more command in the relation) the card comes back on its own, marked **New** and with a
line "Shown again: now also on MOZA AB9 FFB Base · BTN 3 ('Trim Nose Down')" ("...now also runs 'Gear Up'" for the
errors), so you can work through the warnings once and only see what is new afterwards. Ignoring it again hides it
with the new set.

"Ignored warnings (n)" (only while there are some) opens the list of the hidden ones in the cards' words: the title,
the buttons with their relations, the sentence (aircraft and command), the table where a card has several commands.
Each has **Show again** (the card comes back, one undo step); **Show all again** brings every ignored warning back
(one undo step). The Check's search filters the list too, and finds the list itself by what people call it ("ignore
list", "hidden warnings", "dismissed", "muted", "keep both"): it opens.

Old entries clean up after themselves: when a fix of the check runs, an ignored warning whose command is on one
button only now (the button runs one command) is forgotten, so it comes back as a new warning if it ever happens
again. The check itself never changes the profile. Engine: `relations duplicates --ignore | --list-kept |
--forget-kept [--keys]`, `relations conflicts --ignore | --list-kept | --forget-kept [--keys]` (`ShownAgain`,
`NowAlsoIn`, `engine/EngineIgnoredWarnings.cs`).

"Replace 'X' on <button> with..." (2.6.0) is for a button that should do something else, e.g. the F-4E's "TACAN
Channel (xx0x) - [Dec]" on both COMM 2 CW and CCW where CW should be [Inc]: the relation opens (the card's command
marked), **Add actions** searches for the command's name without its direction ("TACAN Channel (xx0x)") with the card's
aircraft as the filter, and a banner says what is replaced (Cancel ends it). The Add button reads "Replace with the
ticked (n)": the ticked action goes in and the old command leaves for those aircraft in one engine call (`relations
suggest apply <relation> --add ... --remove game:id:planes`, one Ctrl+Z). Back to the check then shows the check again.

## Unknown commands

A command the aircraft's current list of the game no longer has (an old module version, a mod that is not installed)
shows the name the relation stored, marked "unknown command 'CAGE-SEAM button'". "More" offers "Remove the unknown
command" (takes it out of every relation, one undo step) and "Find similar to '...'" (opens the relation with the stored
name in the action search). The details name the game, the aircraft, the relations and the game id.

## Actions with no aircraft ticked

When a profile is opened, an action of a relation with no aircraft ticked is taken out of the relation (one history step
of its own, "Removed 12 actions with no aircraft ticked from 9 relations", and a line in the log). Its aircraft go onto
the relation's dismissed suggestions (`Pr0file.DismissedSuggestions`), so a suggestion the user dismissed before 2.5.0
(an action kept switched off) does not come back. Unticking every aircraft for a moment changes nothing until the
profile is opened again; an action with one aircraft ticked and every relation - also one without a bind, the user's
markers - always stays. Undo brings the actions back.

## Speed

A page of cards per section (5 of the rich cards, 25 of the light ones), "Show more" for the next; the light card
shows its relations as one "Open a relation" menu when there are more than two; "More" and the details are made
only when opened. `CheckResponsivenessTests` (3000 errors, 900 duplicate cards) keeps every step under 400 ms.

## Tests

`DuplicateActionViewModelTests`, `DuplicateRealEngineTests`, `DcsDeviceCopiesTests`, `ExportDefaultsTests`,
`NavigationHistoryTests`, `CheckResponsivenessTests`; `CheckTextGate` (in `CheckCardsRenderTests.cs`) fails on an id or
an internal word in a card's texts. Pictures of every card kind on a copy of a real profile, 1280 and 2267 wide, Light
and Dark: `JOYPRO_CHECK_CARDS_PROFILE=<copy> [JOYPRO_CHECK_CARDS_HISTORY=<its history folder>]
JOYPRO_CHECK_CARDS_OUT=<folder> dotnet test --filter CheckCardsRenderTests`. Engine: `engine\tests\smoke\checkconflicts.ps1`.
