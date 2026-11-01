---
title: Relations
status: full
summary: The Relations page: your relations on the left, the open relation in the middle, Add actions / Suggested / Catalog / JoyPro assistant on the right.
---
The Relations page builds and organizes relations: one function with its action in every game and aircraft. The
walk-through with diagrams and the in-game meaning is on [[page:flows/relations.html|Build relations]].

:::video relations-page "The Relations page"
:::

![The Relations page with an open relation](shot:relations)

:::layer beginner
## Three areas

- **Left: your relations.** A search box, filters (groups, games, devices, bound or not, axis or button) and one card
  per relation with its button ("Throttle - HOTAS Warthog · BTN 22 · Shift") or "not bound".
- **Middle: the open relation.** Its name (click to rename), Axis or Button, its input, and **Its actions**: one card
  per game action.
- **Right: tools.** **Add actions** (search every game), **Suggested for this relation**, **Catalog** (browse a
  game's actions by category) and the **JoyPro assistant** (type a sentence).

**New** starts a relation; **Auto-build relations from all my actions** builds them for the whole profile.
:::

:::layer advanced
## Tips

- Ctrl/Shift+click several cards for **Bulk changes** (groups, duplicate, delete) and **Groups** (add, remove,
  rename).
- **Follow input**: press a button on your stick and its relation opens.
- A DCS action card opens its aircraft ("82 of 84 aircraft"); untick aircraft where the command should not apply.
  **Only same meaning** keeps only aircraft where the id means the same.
- **Suggest missing (n)** on the open relation lists what other games and aircraft lack; **More > Suggest missing
  for every relation...** does all relations at once.
- The function chip says which function JoyPro thinks the relation is; change it if the guess is wrong (it steers the
  suggestions).
:::

:::layer technical
## Behind the page

`MainWindowViewModel.RelationBuilder.cs` (editor and right panels), `MainWindowViewModel.RelationAircraft.cs`,
`MainWindowViewModel.SuggestMissing.cs`, `MainWindowViewModel.Catalog.cs`, `MainWindowViewModel.AutoGroup.cs`,
`ListFilterViewModel.cs`. The quick commands run in the engine server (`engine serve`). Engine:
[[cli:relations list]], [[cli:relations search]], [[cli:relations add]], [[cli:relations suggest]],
[[cli:relations aircraft]], [[cli:relations item move]], [[cli:relations mass]], [[cli:relations auto-group]],
[[cli:relations from-text]], [[cli:relations assistant]]. Reference: [[doc:relation-builder|Relations page (reference)]].
:::

<!-- entry: StartNewRelationCommand -->
Starts a new relation (Ctrl+N): type a name, then tick its actions under **Add actions**. Left empty, the relation is
named after its first action. Nothing changes in any game until you bind and export it.

<!-- entry: ToggleOrganizeCommand -->
Opens the whole-profile tools in place of the editor: **By function (Auto Group)** puts the same function of every
aircraft and game into one relation; **By description** groups only what a sentence names. Both show a preview
first. In the game: after binding and export, one button does that function in every aircraft.

<!-- entry: PreviewAutoGroupCommand -->
Shows what Auto Group would build or change (relations, their actions, what moves) without changing the profile.
Engine: `engine relations auto-group` (preview).

<!-- entry: ApplyAutoGroupCommand -->
Applies the Auto Group preview as one change (one undo step).

<!-- entry: AddCheckedBuilderResultsCommand -->
Creates the relation with the ticked search rows ("Create relation with n actions"), or adds them to the open
relation ("Add selected (n)"). A DCS row adds only its ticked aircraft. Axes and buttons together are refused with a
hint. Engine: `engine relations add` / `engine relations suggest apply --add`.

<!-- entry: AddAllBuilderSuggestionsCommand -->
Adds every shown suggestion that no other relation uses (one engine call, one undo step).

<!-- entry: DismissBuilderSuggestionCommand -->
Dismisses the suggestion for good: it is kept in the relation switched off, so it is never suggested again.
**Restore** in "Dismissed for this relation" takes it back.

<!-- entry: MoveBuilderSuggestionHereCommand -->
The suggested action is already in another relation; this moves it here (it leaves the other relation). In the game:
after the next export the action is on this relation's button instead.

<!-- entry: OpenCatalogCommand -->
Opens the catalog: every action of a game (DCS: of an aircraft) by its categories, with ticks and **Only free**
(actions no relation uses yet). Engine: `engine relations browse`.

<!-- entry: AskAssistantCommand -->
Sends your sentence to the JoyPro assistant. It changes relations at once and answers with what it did, the ids it
rejected and **Undo this**. Works without the optional AI model (offline rules); with the model it calls the same
tools. Engine: `engine relations assistant`.

<!-- entry: UndoAssistantTurnCommand -->
Takes the assistant's whole last answer back (it was one engine command, so one undo snapshot).

<!-- entry: ApplyNodeAircraftCommand -->
Saves the ticked aircraft of this action (one undo step). In the game: aircraft you unticked do not get this action
on the relation's button at the next export.

<!-- entry: RemoveNodeCommand -->
Takes this action out of the relation (asks first unless switched off in Settings > Confirmations). In the game: the
action is no longer written on this relation's button.

<!-- entry: DuplicateSelectedRelationCommand -->
Makes a copy of the open relation with its bind. **Duplicate without bind** copies it without the input, for example
to put a variant on another button.

<!-- entry: DeleteSelectedRelationCommand -->
Deletes the open relation (Ctrl+Z brings it back). In the game: at the next export its binds are no longer written
(with Replace they are removed from your devices).

<!-- entry: SuggestMissingCommand -->
"Suggest missing (n)": lists the games and aircraft this relation lacks, from the function catalog, your style and
the same DCS id where it means the same.

<!-- entry: BindSelectedRelationCommand -->
Opens the Binds page with this relation selected, ready to record its button or axis.

<!-- entry: bind:Item.Row.IsChecked -->
Catalog: ticks one action of the game or aircraft. **Add** at the bottom of the catalog puts the ticked actions into
the open relation (or a new one). A **main** chip marks the one action to take for the function (its main control,
not a variant); a used action shows which relations have it already.

<!-- entry: ClearCatalogChecksCommand -->
Catalog: unticks every ticked action; nothing is added.

<!-- entry: ClearGroupsCommand -->
Filter bar: clears the group filter, so relations of every group show again.

<!-- entry: ClearDevicesCommand -->
Filter bar: clears the device filter, so relations on every device (and unbound ones) show again.

<!-- entry: Remove -->
A filter chip above the list ("Group: Weapons", "Device: Warthog"): a click removes just that filter.

<!-- entry: ClearGamesCommand -->
Filter bar: clears the game and aircraft filter, so relations of every game show again.

<!-- entry: TickAllMissingReviewCommand -->
Suggest missing for every relation: ticks every suggestion in the review, ready for **Add**.

<!-- entry: ClearMissingReviewTicksCommand -->
Suggest missing for every relation: unticks every suggestion; nothing is added.

<!-- entry: StartAutoGroupCommand -->
Shown on an empty Relations page: opens Auto Group (**By function**), which builds relations for the whole profile
from your imported actions, with a preview before anything changes.

<!-- entry: GoStartCommand -->
Shown on an empty Relations page: opens the [[gui:start|Start]] page to import the binds of a game you already
play; the import makes relations from them.

<!-- entry: RunRelationMassOperationCommand -->
Runs the chosen bulk operation on the ticked relations (`engine relations mass`): delete, duplicate, merge (into the
name in Value), split (the listed aircraft move into a new relation), add-prefix, add-postfix, remove-part (text from
the names) or export (to a file). One undo step. In the game: deleted or merged relations change what the next
export writes.

<!-- entry: AddRelationGroupsCommand -->
Adds the typed groups (comma-separated) to the ticked relations, or the open one (`engine relations group add`).
Groups only sort and filter; the games never see them.

<!-- entry: RemoveRelationGroupsCommand -->
Removes the groups selected in the list from the ticked relations (`engine relations group remove`).

<!-- entry: RemoveAllRelationGroupsCommand -->
Removes every group from the ticked relations (`engine relations group remove`).

<!-- entry: ClearGroupRemoveSelectionCommand -->
Unselects the groups picked for **Remove Selected**.

<!-- entry: RenameGroupCommand -->
Renames a group in every relation that has it (`engine relations group rename`).

<!-- entry: ListSelectedRelationGroupsCommand -->
Prints the groups of the open relation (or of every relation) into the Output panel (`engine relations group list`).
Changes nothing.

<!-- entry: StopLlmServerCommand -->
The chip of the optional local AI model while it runs: a click stops it at once and frees its memory. It starts again
by itself when an AI feature needs it (Settings > Local AI).

<!-- entry: RefreshRelationsCommand -->
Reads the profile's relations again (`engine relations list`) and rebuilds the list, for example after a change made
outside this window.

<!-- entry: ShowSelectedRelationCommand -->
Prints the open relation with its actions and their aircraft into the Output panel (`engine relations show`,
`engine relations aircraft`). Changes nothing.

<!-- entry: OpenImportWorkspaceCommand -->
Opens the [[gui:start|Start]] page, where the import of a game's binds is set up and run.

<!-- entry: SaveProfileCommand -->
Saves the profile now (Ctrl+S). JoyPro saves by itself after every change; this writes pending edits at once
(`engine profile save`).

<!-- entry: MergeProfileCommand -->
Adds the profile picked under **Add another profile...** in the profile bar to the open one: relations, binds and
modifiers (`engine profile merge`). Asks first and keeps a copy of your profile next to it.

<!-- entry: ShowOrganizeByFunctionCommand -->
Shows **By function (Auto Group)**: the same function of every aircraft and game into one relation, by a preset,
with a preview before **Apply**.

<!-- entry: ShowOrganizeByDescriptionCommand -->
Shows **By description**: type what to group in your words ("speed brake in and out on all jets") and preview the
relation it makes.

<!-- entry: bind:AutoGroupIncludeSubstitutes -->
Auto Group (on by default): when an aircraft lacks a function, the slot is filled with the closest substitute action
(for example another control that does the same job there). Off: such aircraft stay without that action
(`--no-substitutes`).

<!-- entry: PreviewDescribeGroupCommand -->
Shows the relation your description would make: which actions of which games and aircraft, without changing the
profile (`engine relations from-text`; the optional local AI helps with other words and languages).

<!-- entry: ReviewNewDuplicatesCommand -->
After an import put actions into a second relation: runs the [[gui:check|Check]] and opens its cards (duplicates,
conflicts, merge suggestions) on the Export page.

<!-- entry: DismissDuplicateImportNoticeCommand -->
Hides the notice about new duplicates for now; the Check still finds them.

<!-- entry: ToggleRelationCheckExpandedCommand -->
Shows or hides the list of aircraft the open relation's aircraft check is about (aircraft where an action's id may
mean something else).

<!-- entry: LimitMsfsAircraftOnlyBindCommand -->
MSFS 2024 import: this bind existed only in some aircraft's profiles in the sim but came in for all aircraft; this
limits it to those aircraft. Ctrl+Z undoes it. In the game: after the export the other aircraft no longer get it.

<!-- entry: SetBuilderFunctionCommand -->
Sets the open relation's function (the function chip) to the picked one (`engine relations function`). The function
steers the suggestions; change it when JoyPro guessed wrong.

<!-- entry: MoveRelationNodeUpCommand -->
Moves the action card one place up in the relation (`engine relations item move`). The order decides which action a
game gets first when two of one game share the bind.

<!-- entry: MoveRelationNodeDownCommand -->
Moves the action card one place down in the relation (`engine relations item move`).

<!-- entry: MoveRelationNodeToTopCommand -->
Moves the action card to the top of the relation (`engine relations item move`).

<!-- entry: MoveRelationNodeToBottomCommand -->
Moves the action card to the bottom of the relation (`engine relations item move`).

<!-- entry: AddBuilderSearchPlaneCommand -->
Add actions: limits the search to the picked aircraft (a chip appears; several can be picked) and searches again.

<!-- entry: ToggleBuilderGameExpandedCommand -->
Add actions: shows all results of this game ("Show n more"), or folds them back to the best ones.

<!-- entry: ClearBuilderGameFilterCommand -->
Suggested for this relation: clears the game chips, so suggestions of every game show again.

<!-- entry: UseAssistantExampleCommand -->
JoyPro assistant: puts the example sentence into the input box; edit it or send it as it is.

<!-- entry: SelectAllCommand -->
Aircraft list of an action: ticks every aircraft the list shows (with a filter typed, only those). The ticks count
when you press **Apply** on the action card, or when you add the row from the search or the catalog.

<!-- entry: SelectNoneCommand -->
Aircraft list of an action: unticks every aircraft the list shows (with a filter typed, only those). Nothing is saved
before **Apply** on the action card, or before you add the row.

<!-- entry: OnlySameMeaningCommand -->
Aircraft list: ticks the aircraft where this id means the same as the row (also the nearly same ones) and unticks
those where it means something else. It acts on the whole list, also on aircraft a typed filter hides.

<!-- entry: OnlySameFunctionCommand -->
Aircraft list, shown when some aircraft have another title for the same function: ticks every aircraft where this id
is this function, also under another title (a helicopter's cyclic pitch for pitch), and unticks the rest.

<!-- entry: OnlySameTitleCommand -->
Aircraft list: ticks only the aircraft whose title of this id is exactly the row's (also in another category). Nearly
same titles and aircraft where the id means something else are unticked.

<!-- entry: InvertCommand -->
Aircraft list: swaps the ticks of the aircraft the list shows (with a filter typed, only those).

<!-- entry: bind:IsChecked -->
A tick in one of the page's lists: an aircraft of an action, a catalog category ("every action of this category
shown"), a search row, a suggestion, or a group, game or device in the filters. A filter tick applies at once; the
other ticks change nothing until you press the button below them (**Apply**, **Add selected**, **Add ticked**).

<!-- entry: bind:CatalogOnlyFree -->
Catalog: shows only the actions no relation uses yet, so you see what is still free in this game or aircraft.

<!-- entry: ExpandAllCatalogCommand -->
Catalog: opens every category, so all actions of the game or aircraft show at once.

<!-- entry: CollapseAllCatalogCommand -->
Catalog: closes every category; only the category names with their counts stay.

<!-- entry: ToggleCatalogCategoryCommand -->
Catalog: opens or closes this category (a click on its name does the same).

<!-- entry: ToggleExpandedCommand -->
The aircraft line of a DCS action ("4 of 37 aircraft · ..."): opens or closes the list of every aircraft with this id,
each with its own title, to tick the ones the action is for. In the catalog, the search and on the action cards.

<!-- entry: AddCheckedCatalogCommand -->
Catalog: **Add selected (n)** puts every ticked action into the open relation (a DCS action with its ticked aircraft);
without an open relation it reads **Create relation with n actions** and makes a new one. Axes and buttons together
are refused with a hint. Ctrl+Z undoes it. Engine: [[cli:relations add]] / [[cli:relations suggest apply]].

<!-- entry: label:Groups -->
Filter bar: **Groups** lists your groups with a count each; tick one or more to show only the relations of these
groups (any of them). The **Groups** button under the list adds, removes and renames groups instead.

<!-- entry: label: -->
Filter bar: **Games** lists the games of your relations with a count each. Tick a game for every relation with an
action of it, or open it (>) to tick only some of its aircraft.

<!-- entry: label:Devices -->
Filter bar: **Devices** lists the devices your relations are bound to; tick one or more to show only the relations
bound to them.

<!-- entry: SetBoundCommand -->
Filter switches **All** / **Bound** / **Not bound** (each with its count): every relation, only those on an input of a
device, or only those not bound yet. A click on the active switch goes back to all.

<!-- entry: SetKindCommand -->
Filter switches **Axes** / **Buttons** (each with its count): only axis or only button relations. A second click on
the active switch shows both again.

<!-- entry: ClearAllCommand -->
**Clear all** next to the filter chips: switches every filter off and empties the search box, so every relation shows
again.

<!-- entry: label:All -->
Games filter, inside an opened game: ticks every aircraft shown (with a filter typed, only those), so the relations
with an action for them show.

<!-- entry: label:None -->
Games filter, inside an opened game: unticks every aircraft shown.

<!-- entry: SortButton -->
**Sort** (the label names the levels, e.g. "Sort: Device, Input"): the tiered sort of the list. Each level is a key
and a direction; rows equal on one level go by the next. Relations sort by name, device, input, kind, games, aircraft
count, group, bound or modifiers; the catalog by game, aircraft, category, title, kind or date. Remembered per list.

<!-- entry: ResetCommand -->
Sort: **Name only** (in the catalog **Own order**) removes every level, back to the list's own order.

<!-- entry: ToggleDirectionCommand -->
Sort: turns this level around (A to Z or Z to A, low to high or high to low, oldest or newest first).

<!-- entry: MoveUpCommand -->
Sort: moves this level one place up, so it counts before the level above it.

<!-- entry: MoveDownCommand -->
Sort: moves this level one place down.

<!-- entry: RemoveCommand -->
Sort: removes this level.

<!-- entry: AddLevelCommand -->
Sort: **Add level** adds a level with the first key not used yet (pick another key in its box); it sorts the rows the
levels above leave equal.

<!-- entry: SuggestMissingForAllCommand -->
**Suggest missing for every relation...** (More menu) and **Check again** in its review: checks every relation for the
games and aircraft it lacks (medium or high confidence, no AI) and lists the actions no other relation uses. Nothing
changes until you add them. Engine: [[cli:relations suggest]] `--all --missing`.

<!-- entry: label:Open -->
Suggest missing for every relation: opens this relation with its own suggestions, also the ones other relations use
(to move them here).

<!-- entry: bind:IsExpanded -->
Suggest missing for every relation: shows or hides this relation's suggestions (the first three relations start open).

<!-- entry: AddTickedMissingReviewCommand -->
**Add ticked (n)**: adds the ticked actions to their relations, one engine call per relation, so Ctrl+Z takes them
back one relation at a time. Engine: [[cli:relations suggest apply]] `--add`. In the game: after the next export the
relation's input also does these actions.

<!-- entry: ShowManageWithAiCommand -->
**Manage with AI** (the wand next to the list title): opens the plan panel for the whole profile. Say what to do with
your relations (rename, delete, create, groups, aircraft); you see every step before anything changes. Click again to
close it.

<!-- entry: bind:FollowInputOnBinds -->
**Follow input**: press a button or move an axis a long way and the list jumps to its relation; press it again for the
next relation on it (without modifiers first). Not while recording or typing. On by default; the same setting as on
the Binds page and in Settings > Binds, kept across restarts.

<!-- entry: DeleteSelectedRelationsCommand -->
With several relations selected (Ctrl/Shift+click): deletes them and their binds after a question (Del). One undo
step: Ctrl+Z brings them back. Engine: [[cli:relations mass delete]]. In the game: at the next export their binds are
no longer written.

<!-- entry: click:ClearRelationSelectionClicked -->
**Clear** (also More > **Clear selection**): unselects every relation in the list and closes the open one. Nothing
changes in the profile.

<!-- entry: DuplicateRelationCommand -->
Right-click on a relation card: copies it with its actions, aircraft, groups, tuning and bind as a new relation named
"(copy)" and opens the copy. Ctrl+Z undoes it. Engine: [[cli:relations mass duplicate]].

<!-- entry: DuplicateRelationWithoutBindCommand -->
Right-click on a relation card: copies it without its bind (`--no-bind`), the same actions for another input, and
opens the copy; bind it on the Binds page. Ctrl+Z undoes it.

<!-- entry: DeleteRelationCommand -->
Right-click on a relation card: deletes that relation and its bind after a question. Ctrl+Z brings it back. In the
game: at the next export its binds are no longer written.

<!-- entry: label:Bulk changes -->
Opens the bulk tools under the list: pick what to **Do** (delete, duplicate, merge, split, add-prefix, add-postfix,
remove-part, export), a **Value**, and **On** (the selected relations, all, or a query), then **Run Mass Operation**.

<!-- entry: bind:IsSelected -->
A tick or chip that picks an item: a group under **Remove Groups**, a step or action of an AI plan or a By description
preview, or a game chip over the search results or suggestions. A game chip filters at once; the other ticks only
decide what **Remove Selected**, **Apply selected** or **Create** use.

<!-- entry: ShowEditWithAiCommand -->
**Edit with AI** (with an open relation): opens the plan panel for this relation only. Say what to change (its
actions, aircraft ticks, name, groups, axis tuning); you see before and after first, and nothing outside this relation
changes.

<!-- entry: label:More -->
Opens the **More** menu: refresh the relations, show the open one in the Output panel, clear the selection, the next
relation with suggestions, suggest missing for every relation, import, Relations JSON in and out, save and merge the
profile.

<!-- entry: NextRelationWithSuggestionsCommand -->
More menu: opens the next relation (by name, after the open one) that has suggestions of medium or high confidence;
use it again to walk through them. Engine: [[cli:relations suggest]] `--all`.

<!-- entry: click:ImportRelationsJsonClicked -->
More menu: pick a relations JSON file (for example one saved with **Export Relations JSON...**). JoyPro asks first:
the file's relations and binds replace all relations and binds of the open profile, and relations that are not in the
file are removed. Ctrl+Z brings the previous profile back. Engine: [[cli:relations import]] `--replace`.

<!-- entry: click:ExportRelationsJsonClicked -->
More menu: saves every relation of the profile, with its actions, groups and bind, to a JSON file you pick (proposed
name relations.json). The profile is not changed. Engine: [[cli:relations export]].

<!-- entry: CloseAiPlanCommand -->
**Close** on the AI plan panel: back to the relation editor. Nothing is applied.

<!-- entry: AskAiPlanCommand -->
**Make a plan**: turns your request into steps, each with before and after; steps that cannot run are unticked with
the reason. Simple requests work with offline rules, free sentences with the optional local AI model. Nothing changes
yet. Engine: [[cli:relations plan ask]].

<!-- entry: ApplyAiPlanCommand -->
**Apply selected**: runs the ticked steps in one engine call, so one Ctrl+Z undoes all of them; steps that failed are
named. Engine: [[cli:relations plan apply]]. In the game: renamed, deleted or changed relations change what the next
export writes.

<!-- entry: DiscardAiPlanCommand -->
**Discard**: forgets the plan and its steps; nothing changes.

<!-- entry: CloseRelationToolsCommand -->
**Close and back to my relation**: closes Auto-build relations (By function or By description); the open relation's
editor comes back. A preview you did not apply changes nothing.

<!-- entry: bind:AutoGroupReplace -->
Auto Group: first removes the relations tagged AutoGroup by an earlier run, then builds again (`--replace`). Your own
relations are never removed. Off: earlier Auto Group relations stay.

<!-- entry: CreateDescribeGroupCommand -->
By description: **Create** makes the ticked relations of the preview; unticked actions are left out. It uses the same
answer as the preview (the local AI only when the preview used it). Engine: [[cli:relations from-text]] `--select`.

<!-- entry: OnlyWhereSameMeaningCommand -->
Aircraft check: **Only where it is '...'** (several actions: **Only where the meaning is the same**) switches the
relation off in the aircraft where its command does something else, except those ticked **use here**. They stay
listed, one tick brings one back; Ctrl+Z undoes it, one step per action. Engine: [[cli:relations aircraft set]]
`--off`. In the game: those aircraft no longer get the action on this input.

<!-- entry: KeepRelationCheckForAllCommand -->
Aircraft check: **Keep for all** keeps the relation in every listed aircraft and remembers that, so JoyPro does not
ask about them again (kept across restarts). Nothing changes in the profile.

<!-- entry: DismissAircraftCheckCommand -->
Aircraft check: **Ask me later** hides the question for this relation until JoyPro starts again. Nothing changes.

<!-- entry: bind:UseHere -->
Aircraft check: **use here** keeps the relation in this aircraft too when you press **Only where ...**; the aircraft
is then remembered and not asked about again.

<!-- entry: ToggleProfileCheckCommand -->
**Check all relations (n actions)**: runs the aircraft check over the whole profile and opens (or closes) the list of
every relation with aircraft where its command does something else. Engine: [[cli:relations check-aircraft]].

<!-- entry: RepairAircraftCommand -->
Profile check: **Switch them all off** switches the flagged aircraft off in every listed action. They stay listed, one
tick brings one back; Ctrl+Z undoes it (one step, or one per action when you kept some aircraft). Engine:
[[cli:relations check-aircraft]] `--apply`. In the game: those aircraft no longer get these actions.

<!-- entry: OpenAircraftCheckRelationCommand -->
Profile check: opens this relation in the editor.

<!-- entry: LimitAllMsfsAircraftOnlyBindsCommand -->
MSFS 2024 import notice: **Limit all** limits every listed relation to the aircraft whose profiles in the sim had it.
Each relation is one undo step, so Ctrl+Z undoes one at a time. In the game: after the export the other aircraft no
longer get these binds.

<!-- entry: DismissMsfsAircraftOnlyBindsCommand -->
MSFS 2024 import notice: **Keep for all aircraft** hides the notice; the relations stay for all aircraft as imported.

<!-- entry: OpenBuilderFunctionPickerCommand -->
The function chip of the open relation: opens the picker with the catalog functions of the relation's kind, a search
box, **None** and (after you set one) **Automatic**. The function is what the relation stands for in every aircraft
and game; suggestions, AI fill and Auto Assign use it. Engine: [[cli:relations function]] `--list`.

<!-- entry: ClearBuilderFunctionCommand -->
Function picker: **None** makes the relation a button position, like in the real cockpit: each aircraft uses it for
its own function, and nothing is suggested by function (`--none`). Ctrl+Z undoes it.

<!-- entry: AutoBuilderFunctionCommand -->
Function picker (after you set a function): **Automatic** lets JoyPro decide again from the name and the actions; it
picks a function only when (nearly) all actions are it (`--auto`). Ctrl+Z undoes it.

<!-- entry: ExpandWithAiCommand -->
**Expand with AI**: opens the JoyPro assistant on the right, working on this relation ("Working on: ..."). Say in your
words what else belongs in it, or what does not; the assistant then changes it with your answer, and **Undo this**
in its answer takes it back. The button itself changes nothing.

<!-- entry: RelationDuplicateButton -->
**Duplicate** on the open relation: a menu with **Duplicate** (with its bind) and **Duplicate without bind**. Both
make a new relation named "(copy)" and open it; Ctrl+Z undoes it.

<!-- entry: DuplicateSelectedRelationWithoutBindCommand -->
Copies the open relation with its actions, aircraft, groups and tuning but without its bind, for the same actions on
another input; bind the copy on the Binds page. Ctrl+Z undoes it. Engine: [[cli:relations mass duplicate]]
`--no-bind`.

<!-- entry: CloseNewRelationFormCommand -->
**Cancel** on the new relation form: closes it without making a relation.

<!-- entry: OpenMoveActionForNodeCommand -->
Right-click on an action card: moves only this action (with its aircraft) to another input: press the new button or
pick device and input. It joins the relation that already uses that input, else gets a relation of its own; the other
actions stay. One undo step. Engine: [[cli:relations item move-to-input]]. In the game: the action is on the new
input.

<!-- entry: ToggleNodeAircraftCommand -->
The chevron on an action card (actions with an aircraft list, such as DCS): shows or hides the action's aircraft with
a tick each. A click on the card's header row does the same.

<!-- entry: ResetNodeAircraftCommand -->
Action card: **Reset** puts the aircraft ticks back to what the relation has; nothing is saved.

<!-- entry: RestoreBuilderDismissedCommand -->
Dismissed for this relation: **Restore** takes the dismissed suggestion out of the relation, where it was kept
switched off, so it may be suggested again. Ctrl+Z undoes it. Engine: [[cli:relations suggest apply]] `--remove`.

<!-- entry: ShowBuilderFindCommand -->
Right panel: **Add actions** (Ctrl+Shift+F) shows the search over every game and aircraft at once; tick what belongs
and add it with one click. An action whose title has every word you typed comes first (brackets and punctuation do not
matter: "tacan channel (xx0x) - inc" finds "TACAN Channel (xx0x) - [Inc]"); what only means the same comes after it.

<!-- entry: ShowBuilderSuggestedCommand -->
Right panel: **Suggested for this relation (n)** shows what means the same in your other games and aircraft, from
JoyPro's function catalog, each with its reason. Only with an open relation.

<!-- entry: ShowBuilderAssistantCommand -->
Right panel: **JoyPro assistant**. Say in a sentence what you want; it creates and fills relations directly, one Undo
per answer. Simple requests work offline; free sentences need the optional local AI model.

<!-- entry: SetBuilderKindCommand -->
Add actions: **All** / **Axes** / **Buttons** limit the search to one kind. A relation holds only one kind, so once
the open relation has actions the switches are locked to its kind.

<!-- entry: OpenBuilderPlanePickerCommand -->
Add actions: the aircraft button opens the aircraft JoyPro knows, with a box to type in (e.g. F-16); pick one to
search only its actions.

<!-- entry: RemoveBuilderSearchPlaneCommand -->
Add actions: removes this aircraft chip from the search and searches again. Engine: [[cli:relations search]].

<!-- entry: UseBuilderDidYouMeanCommand -->
Add actions: **Did you mean: ...?** puts the proposed words into the search box and searches for them.

<!-- entry: ExpandAllBuilderGamesCommand -->
Add actions: **Expand all** shows every action of every game for this search, not only the best rows; ticks stay.
Engine: [[cli:relations search]].

<!-- entry: CollapseAllBuilderGamesCommand -->
Add actions: **Collapse all** shows only the best rows of each game again; ticks stay.

<!-- entry: bind:BuilderGroupByFunction -->
Add actions: **By function** groups the result rows by the function they are (throttle, collective ...) instead of by
game.

<!-- entry: CheckAllShownBuilderResultsCommand -->
Add actions: **Tick all shown** ticks every action in the list; narrow the list with the game chips first. Actions
already in the open relation are not ticked: they are listed greyed at the bottom under **Already in this relation**,
without a tick box, and the count above the list says how many ("+ 1 already in this relation").

<!-- entry: OpenOtherRelationsTagCommand -->
Add actions and Suggested for this relation: the tag under an action that is switched on in other relations. **in 'Name'** opens that relation;
**in 3 relations** lists them under the action, and a name opens that relation. An action in no other relation has no
tag. The search stays; **Back to '...'** above the relation's name (or the Back arrow at the top, Alt+Left) returns to
the relation and search you came from.

<!-- entry: OpenRelationFromSearchCommand -->
Add actions: a relation name in the list under an action's tag opens that relation; **Back to '...'** returns.

<!-- entry: ReturnToCheckCommand -->
**Back to the check** (when the relation was opened from a Check card) returns to the Check list at that card; **Back
to '...'** (when it was opened from an action's tag) returns to the relation and search you came from. The same as the
Back arrow at the top (Alt+Left). After a change in the relation the check runs again, so a fixed card is gone.

<!-- entry: CancelBuilderReplaceCommand -->
Add actions, when a Check card's **Replace ... with...** opened it: **Cancel** ends replacing; the button adds again
instead of replacing.

<!-- entry: ClearBuilderChecksCommand -->
Add actions: **Clear ticks** unticks every row of the search; nothing is added.

<!-- entry: AddTickedBuilderSuggestionsCommand -->
Suggested for this relation: **Add ticked (n)** adds the ticked suggestions to the open relation in one engine call.
Ctrl+Z undoes it. Engine: [[cli:relations suggest apply]] `--add`. In the game: after the next export the relation's
input also does these actions.

<!-- entry: bind:BuilderShowWeakMatches -->
Suggested for this relation: **Also weak matches (n)** also shows actions whose title only looks similar (weaker
evidence). Off by default.

<!-- entry: MoveMisfitCommand -->
Looks wrong in this relation: **Move to '...'** moves the action into the relation of its function. Ctrl+Z undoes it.
Engine: [[cli:relations suggest apply]] `--move`.

<!-- entry: RemoveMisfitCommand -->
Looks wrong in this relation: **Remove** takes the action out of this relation. Ctrl+Z undoes it. In the game: it is
no longer written on this relation's input at the next export.

<!-- entry: ToggleAlternativesCommand -->
A suggestion: **+n other actions in this game** shows the game's other actions for the same thing under it, each with
how it differs; **Hide the other actions** folds them away.

<!-- entry: AddBuilderSuggestionCommand -->
Suggested for this relation: **Add** puts this one suggestion into the open relation. Ctrl+Z undoes it.

<!-- entry: ClearAssistantContextCommand -->
JoyPro assistant: the x next to "Working on: ..." stops working on that relation; your next sentence is about the
whole profile.

<!-- entry: OpenAssistantRelationCommand -->
JoyPro assistant: a relation name in an answer opens that relation in the editor.
