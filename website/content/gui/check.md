---
title: Check
status: full
summary: Problems found by the check: errors, warnings and notes as cards in game words, with fixes, search, filters and order.
---
**Check** in the bottom bar runs the profile check and shows the results on the Export page under **Problems found
by the check**. What each kind of card means and how the fixes work: [[page:flows/check.html|The Check]].

:::video check-page "The Check page"
:::

![The Check with one card](shot:check)

:::layer beginner
## Using the list

- The line at the top counts the cards: "To look at: 0 errors, 1 warning, 0 notes".
- **Search** finds cards by relation, aircraft, button and command.
- The first box shows one kind (All problems, errors, warnings, notes); the second orders them (By relation, By
  aircraft, By game, By confidence).
- **Expand all** / **Collapse all** open or close every section.
- A card's blue button with a tick is JoyPro's suggestion; **More** has the other fixes; **Open '<relation>'** jumps
  to the relation with the card's commands marked.
:::

:::layer advanced
## Fixing many at once

**Fix all with a rule** applies one rule to every card of a section, **Fix with AI** takes an instruction in plain
words; both show a **Preview** with a tick per change,
**Apply the ticked ones** applies them as one undo step. **Also right after imports and take-overs** runs the sure
fixes automatically after an import.
:::

:::layer technical
## Behind the page

`Views/Controls/CheckProblemsPanel.axaml`, `ViewModels/MainWindowViewModel.CheckCards.cs`,
`MainWindowViewModel.Duplicates.cs`, `Models/CheckIssueModels.cs`. Engine: [[cli:profile validate]],
[[cli:relations conflicts]], [[cli:relations duplicates]], [[cli:export-marks list]].
:::

<!-- entry: ValidateProfileCommand -->
Runs the whole check (`engine profile validate --fail-on-warnings false`, then the conflicts, duplicates and export
marks) and builds the cards. It changes nothing in the profile or the games. The bottom bar's **Check** does the same
and opens the Export page.

<!-- entry: ExpandAllCheckCardsCommand -->
Opens every section and card of the list.

<!-- entry: CollapseAllCheckCardsCommand -->
Closes every section, so only the section titles with their counts show.

<!-- entry: ToggleDuplicateRuleCommand -->
Opens the rule panel: one rule for every card of the section; **Preview** lists each change with a tick before
anything happens.

<!-- entry: ApplyDuplicatePreviewCommand -->
Applies the ticked changes of the preview in one engine command, so one Ctrl+Z takes them all back. In the game:
after the next export each fixed button does one thing.

<!-- entry: click:MoreFixesClicked -->
The other fixes of the card: switch it off on both buttons, move to another button, remove an unknown command, find
similar commands.

<!-- entry: ToggleDuplicateAiCommand -->
Opens the box **Fix with AI** (and closes **Fix all with a rule**): describe in a sentence where the actions of
"actions on in several relations" belong, for example "keep trim functions in the Trim relations, everything else in
the Mode Button relations". JoyPro reads simple sentences itself; the optional local AI only helps with the rest.
Nothing changes until you preview and apply.

<!-- entry: PreviewDuplicateRuleCommand -->
Shows what the chosen rule would change (a rule such as "keep it on the device ..." or "keep it in the relations named
..."), as a list of ticked lines "F-16C 'Trim Up': keep in 'Trim', off in 'Hat Up'" with how sure JoyPro is. Changes
nothing. Engine: [[cli:relations duplicates]] `--rule`.

<!-- entry: PreviewDuplicateInstructionCommand -->
Shows what your sentence would change, with "Understood: ..." and "Not understood: ..." lines so you see how JoyPro
read it. Changes nothing. Engine: [[cli:relations duplicates]] `--instruction ... --llm auto` (the local AI is used
only when it is switched on in Settings > Local AI).

<!-- entry: bind:IsTicked -->
One change of the preview. Untick the lines you do not want; **Apply the ticked ones** applies only the ticked lines.

<!-- entry: TickAllDuplicatePreviewCommand -->
Ticks every line of the preview, or unticks all of them when all are ticked already.

<!-- entry: CancelDuplicatePreviewCommand -->
Closes the preview without changing anything.

<!-- entry: Command -->
A fix of a card (the blue one with the tick is JoyPro's suggestion) or an **Open '<relation>'** button. A fix runs one
engine command, so it is one undo step (Ctrl+Z). **Open** shows the relation with exactly the card's commands and
aircraft marked; **Back to the check** above the relation's name (or **Back**, Alt+Left) returns to the card. In the
game: the fix takes effect at the next export.

On "Same command on two buttons", **Replace '...' on <button> with...** is for the case where one button should do
something else (the [Dec] that should be [Inc]): the relation opens with **Add actions** searching for the command's
name and its aircraft as the filter. Tick the action that belongs there and press **Replace with the ticked**: it takes
the old command's place for those aircraft in one step (one Ctrl+Z).

<!-- entry: ToggleRowsCommand -->
"Show all n" / "Show fewer": the table "aircraft -> command" of a card shows its first rows; this opens the rest or
folds them again.

<!-- entry: bind:IsDetailsOpen -->
Opens the card's details: how sure JoyPro is about the suggestion and why, and where an unknown command came from
(the module version, the import). Built only while open, so a page with many cards stays fast.

<!-- entry: PreviewSureDuplicatesCommand -->
"Fix the sure ones (n of m at 90% or more)": shows a preview of JoyPro's suggested fix for every action on in several
relations that it is at least as sure about as the slider above says. Nothing changes yet; tick the lines you want and
press **Apply the ticked ones** (one undo step). Engine: [[cli:relations duplicates]]
`--rule suggested --min-confidence`.

<!-- entry: bind:AutoFixDuplicatesAfterImport -->
Off by default: when an import or a take-over of game changes puts an action into a second relation, JoyPro applies
its suggested fix at once if it is at least as sure as the slider says; the rest stays on the check for you. The
import's notice says how many were fixed (Ctrl+Z undoes it). Saved in JoyPro's settings.

<!-- entry: click:OpenMenuClicked -->
On a card whose actions are in more than two relations: a menu of those relations. Pick one to open it on the
Relations page with the card's actions marked; **Back** (Alt+Left) returns to the card.

<!-- entry: bind:IsExpanded -->
The arrow at the top right of a card: folds the card to its title or opens it again with the buttons, the sentence,
the table and the fixes. **Expand all** / **Collapse all** do it for every card.

<!-- entry: ShowMoreCommand -->
"Show n more (m not shown yet)": a section with many cards shows one page of them at first, so the page stays fast;
this adds the next page. The search finds every card, shown or not.
