Hand-written texts of the engine commands behind the Relations page, Auto Group, Auto Assign, the local AI model, the Setup wizard, Lay out with AI and the Cockpits page; build.py puts each block on the command's entry of the CLI reference.

<!-- entry: relations list -->
:::layer beginner
Lists the relations of the profile, one line each with its type, bind, modifiers, games, aircraft count and groups.
Add a word to show only the relations that contain it. The GUI's Relations page loads its list with it, and **Refresh
relations** there reads it again. It changes nothing.

```powershell
engine relations list Gear --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `[filter]`: all words after `list` form one text. A relation is listed when the text is part of (case ignored) its
  name, a group, its bind (device, input, `device::input`, alias, modifiers) or one of its actions (game, id or
  aircraft).
- `--titles` adds to each relation the titles of its actions (JSON field; the GUI uses it for the relation search and
  runs `relations list --titles`).
- The relations come sorted by name. Read-only: neither the profile nor a game changes.
:::

:::layer technical
- Output: JSON (the relations document with every relation, its actions, aircraft and bind), or `--text`: `Relations:
  <n>` and one line per relation `name | type=axis|button | bind=<device>::<input> | mods=<n> | games=... | planes=<n> |
  groups=...`. `--out <file>` writes it to a file.
- Exit code `0`; the filter never fails, it only lists fewer relations.
- Related: [[cli:relations show]] (one relation in detail), [[cli:relations search]] (actions of the games, not of the
  profile), [[cli:relations group list]].
:::

<!-- entry: relations show -->
:::layer beginner
Shows one relation in detail: type, groups, games, its bind with modifiers and axis settings, and every action with its
aircraft. **Show selected in output** on the Relations page prints it into the Output panel, and the GUI reads it
whenever it opens a relation. It changes nothing.

```powershell
engine relations show "Gear Toggle" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The name may be the exact name (case ignored) or a part of it that only one relation has. Several matches are
  refused with the list of names.
- The actions carry the titles the profile stored for them (per aircraft).
- Read-only.
:::

:::layer technical
- Output: JSON of the relation, or `--text` (`Relation:`, `Type:`, `Groups:`, `Games:`, `Planes Covered:`, `Bind:`,
  `Bind Reformers:`, inversion, slider, curve, dead zone and saturation, then `Nodes: <n>` with each action).
- Exit code `1` with `relations show requires a relation name.`, `No relation matched query: ...` or `Ambiguous
  relation query '...'. Matches: ...`.
- The commands that change one relation ([[cli:relations group add]], [[cli:relations item remove]],
  [[cli:relations item move]]) answer with this same view of the changed relation.
:::

<!-- entry: relations group list -->
:::layer beginner
Lists the groups of one relation, or every group of the profile with the relations that use it. Groups only sort and
filter your relations in JoyPro; no game sees them. On the Relations page, **List Groups (Selected or All)** under
Groups prints it into the Output panel.

```powershell
engine relations group list --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- With a relation name (exact, or a part only one relation has): that relation's groups, sorted, each once.
- Without: every group (case ignored) with its relations and their count.
- More than one name is refused. Read-only.
:::

:::layer technical
- Output: JSON (`Scope` `relation` or `global`, `Groups`, and for `global` the `Entries` with `Relations` and
  `RelationCount`), or `--text`.
- Exit code `1` with `relations group list accepts at most one relation name.` or the name messages of
  [[cli:relations show]].
- Related: [[cli:relations group add]], [[cli:relations group remove]], [[cli:relations group rename]].
:::

<!-- entry: relations group add -->
:::layer beginner
Adds one or more groups to a relation, for example "Startup" or "Carrier". Use it to sort and filter relations; nothing
changes in any game. On the Relations page, type the groups (comma-separated) under **Groups** and press **Add**.

```powershell
engine relations group add "Gear Toggle" Startup Carrier --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
- Each word after the relation name is a group; a word may also hold several groups separated by `,` or `;`.
- A group the relation has already (case ignored) is not added twice.
- Options starting with `--` are refused here. One profile version (one undo step).
:::

:::layer technical
- Output: the changed relation as [[cli:relations show]] prints it; with `--profile` the profile is saved
  (`Auto-saved profile: <path>`).
- Exit code `1` with `relations group add requires at least one group name.`, `Unknown relations group option(s): ...`
  or the name messages.
- Related: [[cli:relations group remove]], [[cli:relations group rename]], [[cli:relations group list]].
:::

<!-- entry: relations group remove -->
:::layer beginner
Takes groups off a relation, or all of them with `--all`. The relation, its actions and its bind stay. On the
Relations page, **Remove Selected** under Groups removes the picked groups and **Remove All** every group of the open
relation.

```powershell
engine relations group remove "Gear Toggle" Startup --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
- Groups are matched with case ignored; `,` and `;` separate several in one word.
- `--all` clears every group of the relation.
- A group the relation does not have is ignored. One profile version (one undo step).
:::

:::layer technical
- Output: the changed relation as [[cli:relations show]] prints it; the profile is saved with `--profile`.
- Exit code `1` with `relations group remove requires at least one group name or --all.` or the name messages.
- Related: [[cli:relations group add]], [[cli:relations group rename]].
:::

<!-- entry: relations group rename -->
:::layer beginner
Renames a group in every relation that has it, for example "Startup" to "Cold Start". On the Relations page, **Rename**
under Groups runs it with the old and the new name.

```powershell
engine relations group rename Startup "Cold Start" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Exactly two names: the old group (case ignored) and the new one. Both must be non-empty and differ.
- A relation that has the new group already keeps it once.
- One profile version (one undo step) for all relations.
:::

:::layer technical
- Output: JSON (`OldName`, `NewName`, `UpdatedRelations`, `UpdatedCount`), or `--text`; the profile is saved with
  `--profile`.
- Exit code `1` with `relations group rename requires: <old-group> <new-group>.`, `relations group rename requires
  different old/new names.` or `Group not found: <name>`.
- Related: [[cli:relations group list]].
:::

<!-- entry: relations item add -->
:::layer beginner
Puts one or more game actions into an existing relation, so they go on the relation's button or axis with the next
export. Use it to give a bind more games or aircraft, for example the F-16's gear handle next to the F/A-18's.

```powershell
engine relations item add "Gear Toggle" --node "DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--node game:id[:plane1|plane2]`, repeatable: the game (empty = DCS), the action id and the aircraft (`|`, `,` or `;`
  between them). An action the relation has already gets the named aircraft switched on.
- `--id <id>` with `--game <game>` (default DCS) and `--planes <list>` is the same as one `--node`.
- DCS without aircraft: the aircraft where the id has its most common meaning are switched on; aircraft where the same
  id means something else are left out (a note on the error stream says how many). Other games without aircraft: the
  action is for the whole game (`Default` stands for the game too).
- MSFS 2024: an action the command list has only for some aircraft (an add-on's own) comes in for those aircraft
  alone unless the node names aircraft.
- The action must fit: an id its game's command list does not have (checked when JoyPro has that list) and an axis in
  a button relation or a button in an axis relation are refused, nothing is added. `--force` adds it anyway (the
  suggestions then flag it under "Looks wrong").
- One profile version (one undo step). The game files change only with the next export.
:::

:::layer technical
- Output: the changed relation as [[cli:relations show]] prints it; the profile is saved with `--profile`.
- Exit code `1` with `relations item add requires at least one target via --node or --id.`, `Invalid --node value
  '...'. Use --node <game:id[:plane1|plane2]>.`, `Unknown relations item argument(s): ...`, `<game>:<id> is not an
  action of <game>'s command list ...`, `<game>:<id> is a button; '<relation>' is an axis relation.` (or the other way
  round; both end with `Nothing was added (--force adds it anyway).`), `No relation items were changed.` or the name
  messages.
- Related: [[cli:relations item remove]], [[cli:relations item enable]], [[cli:relations suggest apply]] (adds with a
  kind check), [[cli:relations search]] (find the id).
:::

<!-- entry: relations item remove -->
:::layer beginner
Takes an action out of a relation, or only some of its aircraft. Use it when an action does not belong to the
relation's button. **Remove from the relation** on an action card of the Relations page runs it (with the card's
switched-on aircraft). In the game: after the next export the action is no longer written on that relation's button.

```powershell
engine relations item remove "Gear Toggle" --node "DCS:d68pnilunilcdnilvdnilvpnilvunil:F-16C_50" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
- `--node game:id[:plane1|plane2]`, repeatable. Without aircraft the whole action leaves the relation; with aircraft
  only those leave it, and the action goes once no aircraft is left.
- `--id <id>` with `--game <game>` (default DCS) and `--planes <list>` is the same as one `--node`.
- `--all` takes every action out; the relation stays, empty, with its bind.
- An old game id of a game split into versions (MSFS, X-Plane) matches the actions of every version.
- One profile version (one undo step).
:::

:::layer technical
- Output: the changed relation as [[cli:relations show]] prints it; the profile is saved with `--profile`.
- Exit code `1` with `relations item remove requires --all or at least one target via --node or --id.`, `Invalid
  --node value '...'`, `No relation items were changed.` (nothing matched) or the name messages.
- Related: [[cli:relations item add]], [[cli:relations aircraft set]] (switch aircraft off and keep them listed),
  [[cli:relations item move-to-input]].
:::

<!-- entry: relations item enable -->
:::layer beginner
Switches aircraft of an action in a relation on again. The action stays in the relation either way; switched-off
aircraft simply do not get it on the relation's button at the next export.

```powershell
engine relations item enable "Gear Toggle" --node "DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--node game:id[:plane1|plane2]` (repeatable) or `--id <id> --game <game> --planes <list>`; at least one is needed.
- With aircraft: exactly those are switched on, also aircraft the action did not list yet. Without aircraft: every
  aircraft the action lists.
- Games without aircraft keep the action under the game's name; `Default` in the spec means the same.
- An old game id of a game split into versions (MSFS, X-Plane) matches the actions of every version.
- One profile version (one undo step). Nothing changes in the game until the next export.
:::

:::layer technical
- Output: the changed relation as [[cli:relations show]] prints it; the profile is saved with `--profile`.
- Exit code `1` with `relations item enable requires at least one target via --node or --id.`, `Invalid --node value
  '...'`, `No relation items were changed.` (nothing matched, or everything was on already) or the name messages.
- Related: [[cli:relations item disable]], [[cli:relations aircraft set]] (on and off in one step, with the meaning
  check of [[cli:relations aircraft]]).
:::

<!-- entry: relations item disable -->
:::layer beginner
Switches aircraft of an action in a relation off without removing it, for example where the same DCS id does
something else. The aircraft stay listed, so suggestions do not bring them back.

```powershell
engine relations item disable "Gear Toggle" --node "DCS:d3007pnilu3007cd16vd1vpnilvu0:Mi-24P" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--node game:id[:plane1|plane2]` (repeatable) or `--id <id> --game <game> --planes <list>`; at least one is needed.
- With aircraft: those are switched off (an aircraft the action did not list is added as off). Without aircraft:
  every aircraft of the action, so the action no longer reaches any game.
- One profile version (one undo step). In the game: at the next export the switched-off aircraft do not get this
  action on the relation's input.
:::

:::layer technical
- Output: the changed relation as [[cli:relations show]] prints it; the profile is saved with `--profile`.
- Exit code `1` with `relations item disable requires at least one target via --node or --id.`, `Invalid --node value
  '...'`, `No relation items were changed.` or the name messages.
- Related: [[cli:relations item enable]], [[cli:relations item remove]], [[cli:relations check-aircraft]].
:::

<!-- entry: relations item move -->
:::layer beginner
Changes the order of the actions inside a relation. The order decides which action a game gets first when two actions
of one game share the bind. **Move up** (Alt+Up), **Move down** (Alt+Down), **Move to the top** and **Move to the
bottom** in an action card's menu on the Relations page run it.

```powershell
engine relations item move "Gear Toggle" --node "DCS:d68pnilunilcdnilvdnilvpnilvunil" --to 1 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
- `--node game:id` names the action (aircraft in the spec are ignored; the first matching action moves).
- Exactly one of `--to <position>` (1 = first, as [[cli:relations show]] numbers them; a larger number means last),
  `--up` or `--down`.
- An action already in place changes nothing and writes no profile version. Otherwise one version (one undo step).
:::

:::layer technical
- Output: the relation as [[cli:relations show]] prints it; the profile is saved with `--profile` when the order
  changed.
- Exit code `1` with `relations item move needs one of --to <position>, --up and --down.`, `Not in '<relation>':
  game:id` or `--to takes a position from 1 (the first action) to <n>.`
:::

<!-- entry: relations item move-to-input -->
:::layer beginner
Moves one action to another button or axis while the rest of the relation stays where it is. JoyPro puts it into the
relation that already uses the new input, or makes a new relation there named after the action. **Move to another
button...** on an action card or a bind opens the GUI's panel; its preview and **Move** run this command.

```powershell
engine relations item move-to-input "Master Caution" --node "DCS:d3001pnilu3001cd12vd1vpnilvu0:F-16C_50" --device "Throttle - HOTAS Warthog" --input JOY_BTN22 --dry-run --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--node game:id[:planes]` (exactly one): the action. DCS: only the named aircraft move (all switched-on aircraft
  without a list); other games: the whole action.
- `--device` and `--input` (both needed): the new place. The device is written the way the profile spells it.
- `--modifier <name>` (repeatable) puts it behind modifiers; `--keep-modifiers` keeps the modifiers of the old bind.
- `--name <relation>`: the name of a new relation (default: the action's title; `(2)`, `(3)` ... when taken).
- `--if-taken`: what happens when a relation on that input already has another action of the game for these aircraft
  (one aircraft has one action per relation):

| Value | Result |
|---|---|
| `ask` (default) | nothing changes; the command fails with the choices (with `--dry-run` it reports them) |
| `swap` | the action that is there goes to the old relation of the moved one |
| `replace` | the action that is there leaves the input and is not bound any more |
| `keep-both` | the moved action gets a relation of its own on the same input; both fire together |

- A new relation copies the old bind's settings (an axis keeps its tuning) and groups. A relation left without actions
  is removed with its bind.
- `--dry-run` only reports where it would go. Otherwise one profile version (one undo step).
:::

:::layer technical
- Output: JSON (`Action`, `From`, `To`, `Joined`, `Created`, `FromRemoved`, `Taken`, `TakenRelation`, `Displaced`,
  `BothFire`, `Message` ...), or `--text` (the message, `From:`, `To: ... (joined|new)`).
- Exit code `1` with `relations item move-to-input needs exactly one --node ...`, `... needs --device <device> and
  --input <button or axis>.`, `--if-taken takes ask, swap, replace or keep-both.`, `Relation '...' does not hold
  game:id.`, `'...' is on <device> <input> already.` or the taken-input message of `ask`.
- Related: [[cli:relations item remove]], [[cli:relations show]].
:::

<!-- entry: relations mass delete -->
:::layer beginner
Deletes many relations at once, picked by name, by a search text or all of them. Each relation leaves the profile
with its bind. The Relations page's **Delete** (one open relation, or several ticked ones, after a question) and the
bulk operation `delete` run it.

```powershell
engine relations mass delete --query Temporary --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Selection (combined): `--relation <name>` (repeatable), `--relations <list>` (`,`, `|` or `;` between names), each
  name exact or a part only one relation has; `--query <text>` (the same match as [[cli:relations list]]); `--all`.
- Nothing selected is refused. The game files do not change until the next export.
- One profile version (one undo step): `engine profile history undo` ([[cli:profile history restore]]) or Ctrl+Z in
  the GUI brings them back.
:::

:::layer technical
- Output: JSON (`Action`, `SelectedRelations`, `SelectedCount`, `DeletedRelations`), or `--text`; the profile is saved
  with `--profile`.
- Exit code `1` with `relations mass requires at least one selected relation via --relation/--relations, --query, or
  --all.`, `No relations were deleted.` or an unclear name (`Ambiguous relation query ...`).
- Related: [[cli:relations mass duplicate]], [[cli:relations mass merge]], [[cli:relations mass export]].
:::

<!-- entry: relations mass duplicate -->
:::layer beginner
Copies relations: each copy has the same actions and, unless you say `--no-bind`, the same bind. Use it to put a
variant on another button. The Relations page's **Duplicate** and **Duplicate without bind** run it for the open
relation.

```powershell
engine relations mass duplicate --relation "Trim Pitch" --no-bind --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Selection as in [[cli:relations mass delete]]: `--relation`, `--relations`, `--query`, `--all`.
- Names: `<name> (copy)`, then `<name> (copy 2)` ..., never an existing name.
- `--no-bind`: the copy has no input and no modifiers.
- The copy has the same actions switched on, so [[cli:relations duplicates]] (the Check) lists them as on in two
  relations until one side changes.
- One profile version (one undo step).
:::

:::layer technical
- Output: JSON (`Action`, `SelectedRelations`, `CreatedRelations`), or `--text`; the profile is saved with
  `--profile`.
- Exit code `1` with `No relations were duplicated.` or the selection messages of [[cli:relations mass delete]].
:::

<!-- entry: relations mass merge -->
:::layer beginner
Joins several relations into one new relation with all their actions, for example "Trim Pitch" and "Trim Roll" into
"Trim". The old relations are removed. On the Relations page, **Bulk changes** with `merge` (the new name in
**Value**) and **Run Mass Operation** run it.

```powershell
engine relations mass merge "Trim" --relations "Trim Pitch,Trim Roll" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The new name: the first word after `merge` or `--name <name>`; a name that exists already gets `__1`, `__2` ...
- Selection as in [[cli:relations mass delete]]: `--relation` (repeatable), `--relations <list>`, `--query <text>`,
  `--all`.
- All selected relations must be of one kind (all axes or all buttons).
- The actions are copied with their aircraft; an action in several of them becomes one with the switched-on aircraft of
  all.
- The bind: kept when every selected relation had the same input with the same modifiers; otherwise the new relation
  has no bind. The groups of the old relations are not carried over.
- One profile version (one undo step). The game files change only with the next export.
:::

:::layer technical
- Output: JSON (`Action` `merge`, `SelectedRelations`, `SelectedCount`, `DeletedRelations`, `CreatedRelations`), or
  `--text` (`Relation Mass Operation` with the lists); the profile is saved with `--profile`.
- Exit code `1` with `relations mass merge requires a target name via positional <name> or --name.`, `relations mass
  merge requires selected relations to be either all axis or all button.`, `Unknown relations mass merge argument(s):
  ...` or the selection messages of [[cli:relations mass delete]].
- Related: [[cli:relations mass split]], [[cli:relations merge-copies]] (joins copies of one relation).
:::

<!-- entry: relations mass split -->
:::layer beginner
Moves some aircraft out of relations into new relations of their own, for example the carrier aircraft into
"Startup_Carrier". Use it when those aircraft should get the function on another input. On the Relations page,
**Bulk changes** with `split` (the postfix in **Value**, the aircraft in **Planes**) runs it.

```powershell
engine relations mass split Carrier --plane "DCS:FA-18C_hornet" --query Startup --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The postfix: the first word after `split` or `--postfix <text>`; each new relation is named `<old name>_<postfix>`
  (`__1`, `__2` ... when taken).
- `--plane <aircraft>` (repeatable) or `--planes <list>`: the aircraft that move; `game:aircraft` works too (the part
  after the colon counts). At least one is needed.
- Selection as in [[cli:relations mass delete]]; all selected relations must be of one kind.
- Per selected relation one new relation with a copy of every action: the given aircraft are switched on in the copy
  and off in the original; all other aircraft are off in the copy. A relation without those aircraft still gets a new
  relation, with every action switched off.
- The new relations keep the groups and have no bind yet. One profile version (one undo step).
:::

:::layer technical
- Output: JSON (`Action` `split`, `SelectedRelations`, `SelectedCount`, `CreatedRelations`), or `--text`; the profile
  is saved with `--profile`.
- Exit code `1` with `relations mass split requires a postfix via positional <postfix> or --postfix.`, `relations mass
  split requires at least one plane via --plane or --planes.`, `relations mass split requires selected relations to be
  either all axis or all button.`, `No split relations were created.` or the selection messages.
- Related: [[cli:relations mass merge]], [[cli:binds set]] (bind the new relations), [[cli:relations aircraft set]].
:::

<!-- entry: relations mass add-postfix -->
:::layer beginner
Appends the same text to the names of many relations, for example `_ALT`. The Relations page's bulk operation
`add-postfix` (**Run Mass Operation**, text in **Value**) runs it.

```powershell
engine relations mass add-postfix " (Carrier)" --query Hook --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The text is the first word after `add-postfix` (or `--value <text>`). Selection as in
  [[cli:relations mass delete]].
- A new name that exists already gets `__1`, `__2` ... The bind and the dismissed suggestions go with the name.
- One profile version (one undo step). Sister commands: [[cli:relations mass add-prefix]],
  [[cli:relations mass remove-part]]; one relation: [[cli:relations rename]].
:::

:::layer technical
- Output: JSON (`Action` `add-postfix`, `RenamedRelations` with `OldName` / `NewName`), or `--text`; the profile is
  saved with `--profile`.
- Exit code `1` with `relations mass add-postfix requires a value via positional <value> or --value.`, `No relation
  names were changed.` or the selection messages.
:::

<!-- entry: relations mass add-prefix -->
:::layer beginner
Puts the same text in front of the names of many relations, for example `A10_`. On the Relations page, **Bulk
changes** with `add-prefix` (the text in **Value**) and **Run Mass Operation** run it.

```powershell
engine relations mass add-prefix "A10_" --query "A-10" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The text is the first word after `add-prefix` (or `--value <text>`); `prefix` works as the action name too. Spaces at
  its ends are dropped.
- Selection as in [[cli:relations mass delete]]: `--relation`, `--relations`, `--query`, `--all`.
- A new name that exists already gets `__1`, `__2` ... The bind and the dismissed suggestions go with the name.
- One profile version (one undo step). No game sees relation names, so nothing changes in the games.
:::

:::layer technical
- Output: JSON (`Action` `add-prefix`, `RenamedRelations` with `OldName` / `NewName`), or `--text`; the profile is saved
  with `--profile`.
- Exit code `1` with `relations mass add-prefix requires a value via positional <value> or --value.`, `No relation
  names were changed.` or the selection messages.
- Related: [[cli:relations mass add-postfix]], [[cli:relations mass remove-part]], [[cli:relations rename]].
:::

<!-- entry: relations mass remove-part -->
:::layer beginner
Removes a piece of text from the names of many relations, for example a `TEMP_` you added before. On the Relations
page, **Bulk changes** with `remove-part` (the text in **Value**) runs it.

```powershell
engine relations mass remove-part "TEMP_" --query "TEMP_" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The text is the first word after `remove-part` (or `--value <text>`); `remove-name-part` works as the action name
  too. Every place the text occurs (case matters) is removed.
- A name that would be empty keeps its old name; a new name that exists already gets `__1`, `__2` ...
- Selection as in [[cli:relations mass delete]]. The bind and the dismissed suggestions go with the name.
- One profile version (one undo step).
:::

:::layer technical
- Output: JSON (`Action` `remove-part`, `RenamedRelations`), or `--text`; the profile is saved with `--profile`.
- Exit code `1` with `relations mass remove-part requires a value via positional <value> or --value.`, `No relation
  names were changed.` or the selection messages.
- Related: [[cli:relations mass add-prefix]], [[cli:relations mass add-postfix]].
:::

<!-- entry: relations mass export -->
:::layer beginner
Saves some relations (with their binds) to a file, to keep them or to give them to someone. On the Relations page,
**Bulk changes** with `export` (the file in **Value**) runs it. The profile does not change.

```powershell
engine relations mass export "C:\Users\Pilot\Documents\JoyPro\startup.json" --query Startup --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The file: the first word after `export` (or `--path <file>`); a missing folder is created and an existing file is
  overwritten.
- Format: JSON when the file ends in `.json` or with `--format json`; otherwise the older binary relation format `.rl`
  (`--format rl`).
- Selection as in [[cli:relations mass delete]].
- The JSON file is the relations document of [[cli:relations export]], with only the selected relations. Note that
  [[cli:relations import]] replaces all relations of a profile with the file's: it refuses such a partial file unless
  `--replace` is given, and then only the file's relations are left.
- Read-only for the profile.
:::

:::layer technical
- Output: JSON (`Action` `export`, `SelectedRelations`, `SelectedCount`, `OutputPath`, `ExportFormat`), or `--text`
  (`Output Path:`, `Export Format:`).
- Exit code `1` with `relations mass export requires a target path (.rl or .json).`, `Invalid --format value: ... Use
  rl or json.`, `relations mass export failed: ...` or the selection messages.
- Related: [[cli:relations export]] (every relation), [[cli:relations list]].
:::

<!-- entry: relations export -->
:::layer beginner
Saves all relations of the profile with their binds to one JSON file, as a backup or to edit them outside JoyPro. On
the Relations page, **More** > **Export Relations JSON...** runs it. The profile does not change.

```powershell
engine relations export "C:\Users\Pilot\Documents\JoyPro\relations.json" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
- The file is the first word after `export`; without it the global `--out <file>` is used. An existing file is
  overwritten; a missing folder is created.
- Words after the file filter the relations as in [[cli:relations list]] (only those are written). Such a partial file
  needs `--replace` on [[cli:relations import]], which then removes every relation the file does not have.
- Leave out `--text`: with it the file gets the readable text view instead of JSON, and [[cli:relations import]]
  refuses it ("is not a JSON file ... for reading only").
- Read-only for the profile.
:::

:::layer technical
- Output: `Wrote file: <path>`. The file: the relations document (`FormatVersion`, `GeneratedUtc`, `Relations` with
  name, kind, groups, function, actions with aircraft, and the bind with modifiers and axis settings).
- Exit code `1` with `relations export requires a target file path.`; a file that cannot be written ends with
  `engine error: <file> could not be written: ...` (no crash report).
- Related: [[cli:relations import]], [[cli:relations edit]] (the same document in an editor),
  [[cli:relations mass export]] (some relations), [[cli:binds export]].
:::

<!-- entry: relations import -->
:::layer beginner
Reads a relations JSON file (from [[cli:relations export]]) into the profile. The file's relations and binds replace
the profile's. On the Relations page, **More** > **Import Relations JSON...** runs it after asking first ("Replace all
relations and binds?").

```powershell
engine relations import "C:\Users\Pilot\Documents\JoyPro\relations.json" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
- The file's relations become all relations of the profile: a relation that is not in the file is gone. A file that
  lacks some of the profile's relations (a filtered [[cli:relations export]], [[cli:relations mass export]]) is refused
  and the refusal names them; `--replace` imports it anyway and lists what it removed (`Removed (not in the file): ...`).
  The GUI asks first and then passes `--replace`.
- Other options are refused (`relations import takes the file and --replace`).
- The binds come from the file; the profile's modifier list and groups are rebuilt from them. A name that is twice in
  the file gets a `1` appended.
- The titles the profile stored for an action are taken over when the action is in the file too.
- Old game ids of games split into versions (MSFS, X-Plane) are taken over as the new ones.
- One profile version (one undo step): `engine profile history undo` ([[cli:profile history restore]]) brings the
  old relations back. The games change only with the next export.
:::

:::layer technical
- Output: `Imported relations from: <file>` and `relations=<n>, binds=<n>, modifiers=<n>`; the profile is saved with
  `--profile`.
- Exit code `1` with `relations import requires a source file path.`, `File not found: <file>`, `<file> is not a JSON
  file. ...` (a `--text` export), `<file> is not a relations file of engine relations export ...` (another JSON file,
  for example a binds export) or the refusal of a partial file; the profile stays as it was in each case.
- Related: [[cli:relations export]], [[cli:relations edit]], [[cli:binds import]], [[cli:profile merge]] (adds another
  profile instead of replacing).
:::

<!-- entry: relations rename -->
:::layer beginner
Gives one relation a new name; its bind and actions stay. On the Relations page, typing a new name into the open
relation's name box and pressing Enter (or leaving the box) runs it.

```powershell
engine relations rename "Gear Toggle" "Landing Gear" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Exactly two words: the relation (exact name, or a part only one relation has) and the new name.
- A new name another relation has (case ignored) is refused; a change of case only is allowed.
- The bind and the dismissed suggestions go with the name. No game sees relation names.
- One profile version (one undo step). Many relations at once: [[cli:relations mass add-prefix]],
  [[cli:relations mass add-postfix]], [[cli:relations mass remove-part]].
:::

:::layer technical
- Output: JSON (`Relation`, `Name`), or `--text` (`Renamed: <old> -> <new>`); the profile is saved with `--profile`.
- Exit code `1` with `relations rename <relation> <new name>` (not two words, or an empty name), `A relation named
  '...' exists already.`, `Could not rename '...'.` or the name messages.
:::

<!-- entry: relations edit -->
:::layer beginner
Opens all relations of the profile as one JSON file in a text editor and reads the file back when the editor closes.
It is for experts who want to change many things by hand. The Advanced page's **Relations Edit** runs it.

```powershell
engine --editor "code --wait {file}" relations edit --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
- The editor: the global option `--editor` (`{file}` stands for the file; without it the file is added at the end),
  run through `cmd /c`; without `--editor`, Notepad. The engine waits until that process ends, so an editor command
  that returns at once reads the file back unchanged.
- On return the file replaces the profile's relations and their binds (a relation removed from the file is gone);
  the titles the profile stored for the actions are taken over.
- Nothing changes in the games until the next export. One profile version (one undo step).
:::

:::layer technical
- Files: `%TEMP%\joypro-engine-relations-<UTC time>.json` (the relations document of [[cli:relations export]]).
- Output: `Imported relations from: <file>` and the summary line; the profile is saved with `--profile`.
- Exit code `1` when the file cannot be read back as a relations document.
- Related: [[cli:relations export]], [[cli:relations import]], [[cli:binds edit]].
:::

<!-- entry: relations suggest apply -->
:::layer beginner
Carries out your decisions on the suggestions for one relation: add an action, take it from another relation, dismiss
it for good, or take it out again. On the Relations page the add, add-all, move-here and dismiss buttons of the
suggestions, **Restore** of a dismissed one, and the review of **Suggest missing for every relation** run it.

```powershell
engine relations suggest apply "Speed Brake Retract" --add "DCS:d3031pnilunilcd16vd1vpnilvunil:F-16C_50" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
The relation is given by its exact name (or `--relation <name>`). Each option is repeatable and takes
`game:id:plane1|plane2`:

| Option | Effect |
|---|---|
| `--add` | the action is added switched on for these aircraft |
| `--take` | it is switched off in every other relation that has it, then added here |
| `--never` | it is kept in the relation switched off, so it is never suggested again |
| `--remove` | it leaves the relation, or a dismissed suggestion is restored |
| `--move <spec>=>Relation` | it leaves this relation and goes into that existing relation |

- An action of the other kind (a button into an axis relation) is refused with a message; the others are still applied.
- One profile version (one undo step) for all decisions.
:::

:::layer technical
- Output: JSON (`Relation`, `Added`, `Rejected`, `Removed`, `Moved`, `Taken`, `TakenFrom`, `Errors`), or `--text`
  (`Added:`, `Never:`, `Removed:`, `Moved:`, `Taken:` and `Error:` lines). The profile is saved with `--profile`.
- Exit code `1` only when nothing could be applied (`Relation not found: ...`, `Not a node (game:id:plane1|plane2):
  ...`, `Move needs game:id:planes=>existing relation: ...`); partial errors are listed with exit code `0`.
- Related: [[cli:relations suggest]] (what is suggested), [[cli:relations search]].
:::

<!-- entry: relations browse -->
:::layer beginner
The catalog of every game's actions: without options the list of games with their action and aircraft counts, with
`--game` the whole action list of that game (DCS: of one aircraft) by its categories. The Relations page's **Open
catalog** shows it. It changes nothing.

```powershell
engine relations browse --game DCS --plane F-16C_50 --button --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--game <game>` and `--plane <aircraft>` (several with `|` or `,`) pick the list; `--axis` or `--button` keep only
  that kind (not both).
- With `--profile` each action says which of your relations use it (the GUI's **Only free** filter).
- `relations catalog` is the same command. Extra words are refused.
:::

:::layer technical
- Output: JSON (`Games` with `Actions` and `Planes`, or `Game`, `Planes`, `Total`, `Categories` with `Count` and
  `InRelations`, `Results`), or `--text` (`Games: <n>` or `Catalog: <game> | <n> actions` with `[category]` blocks).
- Exit code `1` with `Use --axis or --button, not both.` or an error of the game lists.
- Related: [[cli:relations search]] (find by words), [[cli:relations suggest apply]].
:::

<!-- entry: relations assistant -->
:::layer beginner
The JoyPro assistant of the Relations page: you type a sentence and it changes relations at once, for example
"Throttle ALL: all throttle axes" or "add the collectives to Throttle". It works without AI for simple sentences; with
the optional local AI model it understands more. The **JoyPro assistant** box on the Relations page runs it.

```powershell
engine relations assistant "add the collectives to Throttle" --llm off --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--llm auto` (default): the local model when it is switched on and ready (Settings > Local AI), else the offline
  rules; `on`: the model or an error; `off`: offline rules only.
- Offline it understands "<name>: <what>" (create and fill), "add <what> [to <relation>]", "remove <what> [from
  <relation>]" and "rename <relation> to <name>"; anything else says that it needs the model.
- With the model, the model calls JoyPro's own tools (list, show, search, suggest, create, rename, add, remove) one
  JSON call at a time; JoyPro checks every id, skips actions of the wrong kind and never deletes a relation.
- A question is answered from JoyPro's knowledge of the games and changes nothing.
- `--relation <name>`: the open relation, used when the sentence names none. `--max-steps <n>` (default 16, at
  most 40) limits the model's tool calls. `--show-prompt` prints what the model would get and changes nothing.
- A turn that changes something is one profile version (the GUI's **Undo this**).
:::

:::layer technical
- Output: JSON (method, reply, changes, the model's process and notes), or `--text`. The profile is saved with
  `--profile` only when something changed.
- Exit code `1` with `--max-steps takes a number from 1.`, `Invalid --llm value: ...`, `Unknown relations assistant
  argument(s): ...`, or when the turn failed without a change (with `--llm on` and no usable model: the model's
  problem, for example `The local AI model is off ...`).
- The model runs in its own `llama-server` process for the turn ([[cli:llm]], [[cli:llm start]]).
- Related: [[cli:relations chat]] (one relation), [[cli:relations from-text]].
:::

<!-- entry: relations chat -->
:::layer beginner
Changes one relation from a sentence: "add flare release for all jets", "remove the helicopters", "also War Thunder",
"only DCS". It shows what it would add and remove; `--apply` carries it out. It works without AI; with the optional
local model switched on, the model picks from the same lists.

```powershell
engine relations chat "Flare Release" "add flare release for all jets" --llm off --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The relation: the first word (exact name, or a part only one relation has) or `--relation <name>`; the rest is the
  message.
- What it reads: remove ("remove", "delete", "drop" ...), keep only ("only", "just") or add (otherwise), with the
  functions and aircraft the message names. The candidates come from the offline rules of Group by description, the
  relation's own suggestions and a word search over every game.
- `--llm off`: offline rules only; `auto`: the local model only when the rules found nothing or did not understand
  words; `on`: always the model (fails when it is not usable). Without `--llm`: `on` when the local AI is switched on
  in Settings, else `off`.
- The model may only pick numbered lines of these lists; every other id is rejected and listed. A failed model answer
  falls back to the offline rules with a warning.
- `--show-prompt` adds what the model would get (messages and grammar) to the output.
- Without `--apply` nothing changes. With `--apply` the adds and removes run as one profile version (one undo step).
:::

:::layer technical
- Output: JSON (relation, message, intent, `Add` and `Remove` with spec, title and other relations that have it,
  `Rejected`, `Applied`, `Added`, `Removed`, the model's data, warnings, errors), or `--text` (`Understood:`,
  `Method:`, `Add: <n>` with `+ game:id:planes | title` lines, `Remove: <n>`, `Applied: ...`).
- With `--apply` and `--profile` the profile is saved.
- Exit code `1` with `relations chat <relation> "<message>" [--llm off|auto|on] [--show-prompt] [--apply]`, `Invalid
  --llm value: ...`, `Unknown relations chat argument(s): ...`, `Say what to change, ...`, the model's problem with
  `--llm on`, or the name messages.
- Related: [[cli:relations assistant]] (any relation), [[cli:relations suggest apply]] (what `--apply` uses),
  [[cli:llm]].
:::

<!-- entry: relations plan ask -->
:::layer beginner
Turns a request in your words into a plan of relation steps, for example "fill the F-4E Phantom II into my relations"
or "append _sc to every relation that contains Star Citizen". Nothing changes yet: you see every step and pick the ones
to run with [[cli:relations plan apply]]. The Relations page's Manage with AI and **Edit with AI** (one relation) run it.

```powershell
engine relations plan ask "fill the F-4E Phantom II into my relations" --llm off --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Steps: create, delete, rename, add, remove, aircraft, groups, tuning, fill (a preview of what fits) and check
  (unticks aircraft where an action does something else). JoyPro checks every step against the profile and the
  command lists.
- `--llm auto` (default): JoyPro's offline rules when one fits the request exactly, else the local model; `off`: the
  offline rules only (a request they do not cover fails with a hint); `on`: always the model.
- `--relation <name>`: the plan is for that relation (Edit with AI); an unknown name fails.
- A question ("what does Landing gear down do in X-Plane 12") gets JoyPro's answer and no steps.
- Changes nothing, also with `--profile`.
:::

:::layer technical
- Output: JSON (`Request`, `Scope`, `Method`, `Reply`, `Ops`, `Steps`, `Llm`, `Warnings`, `Errors`), or `--text`. The
  `Ops` are what [[cli:relations plan apply]] takes.
- Exit code `1` with `Relation not found: ...`, `Invalid --llm value: ...`, `Unknown relations plan ask argument(s):
  ...`, or when the plan has errors and no step (`Say what to do, ...`, no offline rule and no usable model).
- Reference: [[doc:ai-plans]]. Related: [[cli:relations assistant]], [[cli:llm]].
:::

<!-- entry: relations plan apply -->
:::layer beginner
Runs the steps of a plan from [[cli:relations plan ask]]: all of them, or only the ones you ticked. They run together
as one version of the profile, so one undo takes them back. The Relations page runs it when you apply a Manage with AI
or **Edit with AI** plan.

```powershell
engine relations plan apply --file "C:\Users\Pilot\Documents\JoyPro\plan.json" --only 0,2 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The plan: `--file <plan.json>`, or the JSON as text after `apply`.
- `--only 0,2,3`: only these steps, numbered from 0 as in the plan.
- `--relation <name>`: the relation an Edit with AI plan was made for; a step that would change another relation gets
  an error instead.
- `--dry-run`: reports what the steps would do; the profile is not saved.
- A step that fails is reported with its error; the other steps still run. Steps left out by `--only` are listed as
  skipped.
:::

:::layer technical
- Output: JSON of the result (what each step did, `DryRun`, warnings, errors), or `--text`. With `--profile` the profile
  is saved unless `--dry-run`.
- Exit code `1` with `The plan has no steps.`, `--only takes step numbers from 0, e.g. --only 0,2.`, a parse error of
  the plan, `Relation not found: ...` or `Unknown relations plan apply argument(s): ...`.
- Related: [[cli:relations plan ask]], [[cli:profile history]] (undo).
:::

<!-- entry: relations ai-matrix -->
:::layer beginner
A test for maintainers: asks every AI path of the Relations page (the JoyPro assistant, Manage and Edit with AI, Expand
with AI, Group by description, the relation chat and what the local model is shown) typical requests for every game
JoyPro knows, and judges each answer. Nothing is saved.

```powershell
engine relations ai-matrix --games "DCS|XPlane12" --examples 80 --text
```
:::

:::layer advanced
- Each answer is judged: only actions of the game the request names, readable (no raw ids), an answer at all; a game
  without that function must not get another game's actions. The function map decides which game has a function.
- `--games g1|g2`: only these games; `--paths p1|p2`: only these paths (for example `assistant`, `plan`, `describe`,
  `chat`, `llm`); `--examples N`: show each answer cut to N characters (`0` = none).
- Works on the loaded profile in memory; every cell is taken back at once, nothing is saved.
:::

:::layer technical
- Output: JSON (counts per path, the cells with their verdict, warnings), or `--text`. Exit code `0` after the run;
  `1` with `--examples takes a number of characters (0 = no answers).` or an extra argument.
- Related: [[cli:relations phrase-check]], [[cli:relations assistant]], [[cli:relations plan ask]].
:::

<!-- entry: relations phrase-check -->
:::layer beginner
A test for maintainers: checks that what players type (names, synonyms, slang, everyday sentences, German) finds the
right function on every entry point, offline and without the AI model. The phrases come from a JSON file, one set per
function.

```powershell
engine relations phrase-check --file engine\tests\fixtures\phrases\phrases.json --entries search --text
```
:::

:::layer advanced
- Entry points: `search` (the add-actions search), `catalog` (the filter of one game's list), `assistant` (the JoyPro
  assistant's lookup), `plan` (Manage with AI "make a relation for ..."), `describe` (Group by description);
  `--entries` picks some.
- `--functions id1|id2`: only these functions; `--kinds phrases|descriptions|de`: only these phrase kinds;
  `--failures N`: how many failures are listed (default 40).
- Nothing changes: the profile is not touched, plan and describe run as dry runs.
:::

:::layer technical
- Output: JSON (per entry point the counts and failures, the games per function, warnings, errors), or `--text`.
- Exit code `0`; `1` when the run has errors, without `--file`, with an unknown entry point (`Unknown entry point(s):
  ...`) or `--failures takes a number.`
- Related: [[cli:relations search]], [[cli:relations ai-matrix]].
:::

<!-- entry: relations action-info -->
:::layer beginner
A report for maintainers: per game, how many actions have a readable name, how many have an explanation and how many
functions have a clear recommended action. It shows where names and texts are still missing. It changes nothing.

```powershell
engine relations action-info --coverage --game DCS --text
```
:::

:::layer advanced
- `--coverage` is needed (it is the only report). `--game <id>`: one game.
- Readable: the game's own name, or one JoyPro makes from an id or event name. Explained: by the cockpit guide, the
  game's own description, JoyPro's text or the general text of the function.
- Up to 6 examples per game of unexplained abbreviations and up to 4 of "word plus axis" names; `--all-examples` lists
  all of them.
- Read-only; no profile is needed.
:::

:::layer technical
- Output: JSON (`Games` with the counts and examples, `Warnings`), or `--text` (one line per game with percentages,
  then `abbreviation:` and `word+axis:` examples).
- Exit code `1` with the usage line `relations action-info --coverage [--game g] [--all-examples]` without
  `--coverage` or with another argument.
- Related: [[cli:relations functions]], [[cli:relations pair]].
:::

<!-- entry: relations pair -->
:::layer beginner
Tells how two action titles of one game relate: `twin` (the same thing), `opposite` (two directions of one control,
such as up and down) or `different`. JoyPro uses this judgement for its recommended-action hints; the command shows it
for two titles you give. It changes nothing.

```powershell
engine relations pair "Landing Gear Up" "Landing Gear Down" --game DCS --text
```
:::

:::layer advanced
- Exactly two titles; `--game <id>` lets the game's own prefixes and tags be cleaned from the titles first.
- `opposite`: each title names one direction of the same pair of direction words. `twin`: the same words once filler
  words are gone and the directions do not oppose. Everything else is `different`.
- Read-only; no profile is needed.
:::

:::layer technical
- Output: JSON (`A`, `B`, `Game`, `Relation`), or `--text` (`<A> / <B>: <relation>`).
- Exit code `1` with `relations pair "<title A>" "<title B>" [--game <game>]` when there are not exactly two titles.
- Related: [[cli:relations action-info]], [[cli:relations suggest]].
:::

<!-- entry: relations functions -->
:::layer beginner
Lists the functions of JoyPro's function map ("Gear Up", "Pitch", "CMS Forward" ...) with their synonyms and how many
actions of the games on this PC each one covers. It is what the search, the suggestions and the AI layer use to
understand words. It changes nothing.

```powershell
engine relations functions --query "speed brake" --text
```
:::

:::layer advanced
- `--category <id>`: only the functions of that category. `--query "<words>"`: only the functions the words resolve to
  (as the search reads them).
- `--coverage`: per game how many actions are mapped to a function (by the function catalog or by the map's rules),
  how many functions and aircraft, and up to 25 unmapped titles.
- `--dump <file.tsv>`: writes one line per action (game, aircraft, id, axis/button, title, category, class, functions
  with direction, weight and source); a missing folder is created.
- Read-only for the profile.
:::

:::layer technical
- Output: JSON (`Version`, `Source`, `Totals`, `Categories`, `Functions` with name, kind, synonyms, game terms,
  directions, slots, related functions, action counts per game, `Coverage`, timings, `FromCache`, `Warnings`), or
  `--text` (a totals line, the functions by category, `Coverage:`).
- Exit code `1` when the map is empty (`The function map is empty.` or the reading problem) or with an unknown
  argument.
- Files: `DB\Functions\function-map.json`; the mapped index is cached in `%LOCALAPPDATA%\JoyPro2\Cache\function-map.bin`
  until the game lists change.
- Related: [[cli:relations resolve]], [[cli:relations function]] (one relation's function).
:::

<!-- entry: relations resolve -->
:::layer beginner
Turns words into functions and the matching actions of every game, for example "cut the throttle" or "pitch". It shows
which function JoyPro understood and which action of each game does it. It changes nothing.

```powershell
engine relations resolve "pitch" --game DCS --plane F-16C_50 --axis --text
```
:::

:::layer advanced
- `--game g1|g2` and `--plane p1|p2` (also with `,`, repeatable): only these games and aircraft. `--axis` or
  `--button` keep only that kind (not both).
- `--per-game N`: at most N actions per game (default 25, `0` = all).
- A direction in the words ("up", "down" ...) keeps only buttons of that direction (toggles stay). A function's main
  axis comes first when the words name one function.
- DCS: one row per id and meaning; an aircraft whose title of the id means something else is a row of its own, with
  the variants listed.
- Read-only; no profile is needed.
:::

:::layer technical
- Output: JSON (`Query`, `Functions` with id, direction, term, score, related or described, `DidYouMean`, `Extra`,
  `Games` with `Actions`: id, title, aircraft, function, weight, source, why, meaning variants; `Total`), or `--text`
  (`Query:`, `Functions:`, `Actions: <n> in <n> games` and one line per action).
- Exit code `1` with `--per-game takes a number (0 = all).`, `Use --axis or --button, not both.`, `Unknown relations
  resolve argument(s): ...` or the usage line without words. Words that resolve to nothing give `Functions: (none)`
  with exit code `0`.
- Related: [[cli:relations search]] (the add-actions search), [[cli:relations functions]].
:::

<!-- entry: relations function -->
:::layer beginner
Shows or sets the function a relation stands for, for example "Gear Up" or "CMS Forward". The function steers the
suggestions, Suggest missing and Auto Assign. The function chip on the Relations page shows it, and picking a function
there runs `--set`.

```powershell
engine relations function "Left Hat Up" --set target_management_system_up --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Without an option: the function and how JoyPro knows it: set by you, cleared by you, named after it, covered (at
  least 80 % of its known actions are that function), mixed (a button position with a different function per
  aircraft) or unknown.
- `--set <function>`: a catalog function by id or name; an axis function only on an axis relation and a button one
  only on a button relation.
- `--none`: no function (a button position: each aircraft its own; no function-based suggestions).
- `--auto`: back to automatic. Use one of the three at a time; setting what is already set writes no version.
- `relations function --list [--axis|--button] [--query <text>]` lists the catalog functions `--set` can pick (the
  GUI's picker), sorted by name: `--axis` / `--button` keep only axis or button functions, `--query` only those whose
  name or id contains every word.
:::

:::layer technical
- Output: JSON of the relation's function result (or of the choices with `--list`), or `--text` (`--list`: one line
  `id<TAB>name` per function, `(axis)` for axes). The profile is saved with `--profile` after a change.
- Exit code `1` with `Use one of --set, --none and --auto.`, `Unknown function: ... (relations function --list shows
  them).`, the axis/button mismatch message or the name messages.
- Data: `DB\Functions\slots.json`. Related: [[cli:relations suggest]], [[cli:relations auto-assign]].
:::

<!-- entry: relations aircraft -->
:::layer beginner
Shows, for each action of a relation, every aircraft that has its id: switched on, off or not in the action, with the
title it has there and whether that is the same function. Use it to see where a DCS id does something else. The
Relations page reads it for the aircraft list under each action and for the titles of other games' actions. It changes
nothing.

```powershell
engine relations aircraft "Rudder" --node "DCS:a2003cdnil" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The relation: exact name or a part only one relation has (also `--relation <name>`). `--node game:id`: only that
  action; an aircraft part (`game:id:F-16C_50` or `game:id:a|b`) is read like every other `--node` and only names the
  action, the report still lists every aircraft.
- Per aircraft the match to the action's meaning (the most common meaning of its switched-on aircraft): `same`,
  `near` (the same words in another order, another category or one extra word), `differs`, `differs (same
  function)` when the function map reads it as the same function anyway, or `unknown` (the id is not in that
  aircraft's list any more).
- Games with one list (IL-2, Star Citizen, X-Plane ...): one line for the game. MSFS 2020 and 2024: every aircraft of
  JoyPro's aircraft list, all ticked when the action is for all aircraft.
- Read-only. `--on`, `--off` and `--only` change something only with [[cli:relations aircraft set]].
:::

:::layer technical
- Output: JSON (`Relation`, `Nodes` with game, id, title, category, explanation, `Badge` such as `3 of 12 aircraft`,
  counts of switched-on aircraft that differ or are near, `Aircraft` with plane, title, state `on` / `off` / `absent`,
  match and note), or `--text` (one line per action, then `[x]` on, `[-]` off, `[ ]` absent per aircraft, and `MIXED:`
  when switched-on aircraft mean something else).
- Exit code `1` with the usage line (no relation), `--node needs game:id.` or the name messages.
- Related: [[cli:relations check-aircraft]] (all relations), [[cli:relations meanings]].
:::

<!-- entry: relations aircraft set -->
:::layer beginner
Ticks or unticks aircraft of one action in a relation, in one step. Use it when a DCS command should not apply in
some aircraft. On the Relations page, **Apply** under an action's aircraft list runs it, and so do **Only where it is
'...'** of the aircraft check and the MSFS 2024 "only in some aircraft" fix.

```powershell
engine relations aircraft set "Rudder" --node "DCS:a2003cdnil" --off "AH-64D_BLK_II|Mi-24P" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--node game:id` (needed): the action in the relation.
- `--on p1|p2`: switched on, also aircraft the action did not list yet. `--off p3|p4`: switched off and kept listed,
  so suggestions do not bring them back. `--only p1|p2`: exactly these on, every other aircraft of the action (and of
  the game's aircraft list) off.
- Games with one aircraft list per game (MSFS): all ticked means all aircraft; none ticked switches the action off.
- In the game: unticked aircraft do not get this action on the relation's button at the next export.
- One profile version (one undo step).
:::

:::layer technical
- Output: JSON (`Relation`, `Changed`, `Nodes` with every aircraft: `on` / `off`, its title and whether the id means
  the same there), or `--text` (`Changed: <n>` and one `[x]` / `[-]` line per aircraft).
- Exit code `1` with `relations aircraft set needs --node game:id.`, `relations aircraft set needs --on, --off or
  --only.`, `game:id is not in '<relation>'.` or the name messages.
- Related: [[cli:relations aircraft]] (the same report without a change), [[cli:relations check-aircraft]].
:::

<!-- entry: relations check-aircraft -->
:::layer beginner
Finds DCS actions that are switched on for aircraft where the same command id means something else (the "Rudder" id
that is nosewheel steering in another module). `--apply` switches those aircraft off. The Relations page shows the
open relation's findings as a question above its actions; **Check all relations** lists all, and **Switch them all
off** runs `--apply`.

```powershell
engine relations check-aircraft --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Only DCS actions are checked; an action of the other kind (a button in an axis relation) is counted, not listed.
- `--relation <name>` (or the name as words) checks one relation.
- Aircraft where the function map reads the other meaning as the same function are listed but not proposed;
  `--include-same-function` proposes them too.
- `--apply` switches the proposed aircraft off (one profile version, one undo step); one tick in the action's aircraft
  list brings one back.
:::

:::layer technical
- Output: JSON (`Relations`, `Nodes`, `Found` with relation, action, meanings and `Drop`, `AircraftToDrop`,
  `AircraftSameFunction`, `Dropped`), or `--text`. The profile is saved with `--profile` after `--apply`.
- Exit code `0`; `1` only for an unknown relation name.
- Related: [[cli:relations aircraft set]], [[cli:relations aircraft]].
:::

<!-- entry: relations meanings -->
:::layer beginner
A statistic of the game lists: per game, how many command ids several aircraft share and how many of those mean
something else in some aircraft, with the ids that have the most meanings. It explains why JoyPro checks the meaning
of an id per aircraft. It changes nothing.

```powershell
engine relations meanings --game DCS --top 10 --text
```
:::

:::layer advanced
- `--game <id>`: one game. `--top N`: the number of example ids (default 20).
- Each example lists up to 6 meanings (title and category) with up to 6 aircraft each; the first is the most common
  meaning.
- The rule for same, near and different is the one of [[cli:relations aircraft]]. Read-only; no profile is needed.
:::

:::layer technical
- Output: JSON (`Games` with lists, ids, shared ids, ids with other meanings, aircraft entries left out, ids with near
  variants; `Examples`), or `--text` (one line per game, then `<game>:<id> (<n> meanings)` with the titles and
  aircraft).
- Exit code `1` with `--top needs a number.` or `Unknown relations meanings argument(s): ...`.
- Related: [[cli:relations check-aircraft]], [[cli:relations aircraft]].
:::

<!-- entry: relations duplicates -->
:::layer beginner
Lists actions that are switched on in more than one relation for the same aircraft, so the game gets the same command
on two inputs, each with a suggestion where to keep it and how sure JoyPro is. Its options fix one card, many cards by
a rule, or many by a sentence. The Check's cards "on in several relations" come from it, and their fixes, **Fix all
with a rule** and **Fix with AI** run it.

```powershell
engine relations duplicates --rule suggested --min-confidence 80 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
The actions a fix is about: `--keys "game|id|aircraft;..."` or `--node game:id --plane p1|p2`.

| Option | Effect |
|---|---|
| `--keep <relation>` | stays on only in this relation, off in the others |
| `--off` | off in every relation |
| `--ignore` | all stay; recorded as intended and not listed again until one more relation gets the action (then it is listed with `ShownAgain` and `NowAlsoIn`); `--keep-both` is the old name |
| `--forget-kept` | the ignored ones of `--keys` are listed again; without keys every ignored warning (also the conflicts') |
| `--list-kept` | lists only the ignored ones (the app's "Ignored warnings") |
| `--rule <rule>` | a preview of one rule for all cards; `--apply` applies it |
| `--instruction "<words>"` | a preview from a sentence; `--apply` applies it |
| `--apply-suggested` | `--rule suggested --apply` |
| `--changes "key=>relation;key=>-"` | the ticked lines of a preview exactly as shown (`-` = off in all) |

- Rules: `suggested` (JoyPro's suggestion, from `--min-confidence` 0-100), `name` (the one relation whose function or
  name fits), `first` (the relation that had it first in the profile history), `device:<stick|throttle|panel|pedals|
  collective>`, `relation:<words>` (the one relation whose name has these words).
- `--instruction` reads simple sentences with JoyPro's phrase rules; `--llm auto` (default) or `on` asks the local
  model only for the parts the rules did not understand, `off` never.
- The suggestion comes from the profile history first, else from each relation's function and name.
- `--include-kept` lists the ignored ones too (`Kept`). An ignored one stays hidden while these relations or fewer of
  them have the action. Each fix is one profile version (one undo step); a fix also forgets ignored ones whose action
  is on in one relation only now.
:::

:::layer technical
- Output: JSON (`Count`, `Duplicates`, `Groups`, `Changes`, `Understood`, `NotUnderstood`, `SwitchedOff`, `Resolved`,
  `Warnings`), or `--text`. The profile is saved with `--profile` only after a fix.
- Exit code `1` with `relations duplicates: --keep, --off and --ignore exclude each other.`, `relations
  duplicates: --rule and --instruction exclude each other.`, `Unknown rule '...'`, `--min-confidence needs a number
  from 0 to 100.`, `... needs the actions: --keys ...` or `Those actions are on in one relation only (nothing to
  resolve).`
- Related: [[cli:relations conflicts]], [[cli:profile validate]], [[cli:relations merge-copies]].
:::

<!-- entry: relations merge-copies -->
:::layer beginner
Merges relations an import copied ("Gear", "Gear__1", "Gear__2" ...) that have the same bind back into one relation.
Such copies came from devices Windows gave new IDs over time. The Check's cards for copied relations offer **Merge into
one** and **Merge all copied relations**; the Check finds them with `--dry-run`.

```powershell
engine relations merge-copies --dry-run --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- A copy family is `<name>` and `<name>__N` with the same device, input, kind and modifiers. The same device under
  another Windows ID counts as the same device unless two of those IDs are connected at once.
- The merged relation gets every action and aircraft of the copies and their groups; each copy's axis tuning becomes
  the tuning of its aircraft. The copies are removed.
- `--name <relation>` merges only the family of that name (or of one of its copies). `--dry-run` only lists.
- One profile version (one undo step); nothing to merge writes no version.
:::

:::layer technical
- Output: JSON (`Count`, `CopiesRemoved`, relations and binds before and after, `Families` with the relation, copies,
  device, input and aircraft), or `--text`. The profile is saved with `--profile` after a merge.
- Exit code `0`; `1` for an unknown argument.
- Related: [[cli:relations duplicates]].
:::

<!-- entry: relations auto-group -->
:::layer beginner
Puts the same function of every DCS aircraft and every other supported game into one relation, so one button does it
everywhere (for example "CMS forward" of the F-16, the Hornet, BMS and Star Citizen). It uses JoyPro's function catalog
and your own habits, without AI. Relations > **Auto-build relations from all my actions...** > **By function (Auto
Group)**: **Preview** runs it with `--dry-run`, **Apply** without.

```powershell
engine relations auto-group --preset all --dry-run --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
| `--preset` | Relations |
|---|---|
| `all` (default) | one relation per function for all aircraft |
| `wing` | split into fixed wing and helicopters |
| `era` | split by wing type and era; spaceships form their own group |
| `purist` | only the basic flight controls shared; every other function one relation per aircraft |

- `--no-substitutes`: an aircraft without the function stays without it (default: the slot takes your habit from at
  least two own decisions, else the catalog's substitute at the same position).
- `--planes p1|p2|Game`: only these aircraft or games (a game id like `StarCitizen` counts).
- `--no-map`: only the catalog, not the function map's actions it does not list.
- `--replace`: removes the relations of earlier runs (group `AutoGroup`, with their binds) first; your own relations
  are never removed.
- Existing relations of a function keep their binds and only get missing aircraft. A command you use in another
  relation stays there ("Kept"); commands you switched off stay off; an aircraft never gets two actions on in relations
  on the same input.
- New relations get the group `AutoGroup`. One profile version (one undo step); `--dry-run` changes nothing.
:::

:::layer technical
- Output: JSON (counts of created, extended and removed relations, actions added, the kept ones by reason, aircraft and
  commands missing in the database, `Relations`), or `--text`. The profile is saved with `--profile` unless
  `--dry-run`.
- Exit code `1` with `Invalid --preset value: ...`, `Unknown relations auto-group argument(s): ...` or `Function
  catalog file missing: ... (update JoyPro).`
- Data: `DB\Functions` (`slots.json`, `aircraft.json`, `<Game>.tsv`). Reference: [[doc:auto-group]]. Related:
  [[cli:relations from-text]] (only what a sentence names), [[cli:relations auto-assign]] (put them on buttons).
:::

<!-- entry: relations from-text -->
:::layer beginner
Makes relations from a description in your words, for example "speed brake in and out on all jets" or "CMS forward for
all DCS jets except the A-10". Offline rules read the sentence; the optional local AI model helps with unusual
wording. Relations > **Auto-build relations from all my actions...** > **By description**: **Preview** runs it with
`--dry-run`, **Create** makes the ticked relations.

```powershell
engine relations from-text "speed brake in and out on all jets" --dry-run --llm off --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- One relation per function and direction, named after it and the aircraft ("Speed Brake Out (jets)"), with a
  confidence and an explanation per aircraft. `--name <name>` names it (with several relations: `<name> - <function>`).
- `--games g1|g2` and `--planes p1|p2` limit the aircraft; `--substitutes` also takes the catalog's stand-ins.
- `--select <relation>` (repeatable) creates only those relations; `--skip-node game:id:planes` (repeatable) leaves
  single commands out; `--min-confidence 0..1` (default 0.5) drops weaker ones.
- `--llm auto` (default when the model is switched on, else `off`) asks the model only when the rules found no
  function, used catalog words or left words not understood; `on` always asks; `off` never. The model only picks from
  JoyPro's lists; JoyPro checks every id.
- Like Auto Group: an existing relation of that name gets the missing aircraft, a command used in another relation
  stays there, switched-off commands stay off; new relations get the group `FromText`.
- `--show-prompt [--prompt-model <model>|local]` prints the model's prompt and grammar without running a model.
- `relations from-text --check` tests `DB\Functions\language.json` against the catalog.
- `--dry-run` only shows. Otherwise one profile version (one undo step).
:::

:::layer technical
- Output: JSON (proposals with their actions, confidence and explanations, understood and not understood words, the
  model's part), or `--text`; the result is printed before a failure. The profile is saved with `--profile` unless
  `--dry-run` or `--check`.
- Exit code `1` with `No function recognised in "..."`, `No aircraft or game matches "..."`, `Invalid
  --min-confidence value: ...`, `Invalid --llm value: ...`, with `--llm on` and no usable model, or (`--check`) when the
  vocabulary has errors.
- Reference: [[doc:natural-language-grouping]]. Related: [[cli:relations auto-group]], [[cli:llm]].
:::

<!-- entry: relations auto-assign -->
:::layer beginner
Binds your unbound relations to fitting controls of your devices: TMS on the TMS hat, gear on the gear lever, trim on
the trim hat. It works for any set of devices and runs without AI, from JoyPro's device catalog and function catalog.
Binds > **Put relations on my buttons automatically...**: **Preview** runs it with `--dry-run`, **Explain Selected**
with `--explain`, **Apply** without.

```powershell
engine relations auto-assign --max-modifiers 1 --dry-run --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Devices: connected devices are matched by VID:PID and name; `--grip "<device or VID:PID>=<grip id>"` picks the grip
  of a base; `--add-device <catalog id>` adds a device that is not connected (on a placeholder device);
  `--ignore-device <device>` leaves one out. One device per hand: a second stick of the same hand is left out.
- `--relation <name>` (repeatable): only these relations.
- `--max-modifiers 0|1|2` (default 2): shift layers at most, only when the busiest aircraft has more grip functions
  than grip buttons, and only for games that read modifier binds.
- `--phase-layers auto|on|off` (default `auto`): a latched switch as air-to-air / air-to-ground mode switch.
- `--overwrite`: lays out the bound relations again (keyboard, virtual devices and unplugged devices stay);
  `--include-missing` also moves binds of devices that are not connected.
- `--no-style`: ignores your previous controls (default: a relation goes back on its old control first).
- `--explain <relation>`: the best candidates with score and what occupies them.
- Bound relations keep their binds. One input serves several relations only when they share no aircraft.
- One profile version (one undo step); `--dry-run` changes nothing.
:::

:::layer technical
- Output: JSON (devices as recognised, `Assignments`, `Unassigned`, modifiers, explanations), or `--text`
  (`Assigned: <n>`, `relation => device input + modifier (control)`, `Not Assigned (no fitting free input):`). The
  profile is saved with `--profile` unless `--dry-run`.
- Exit code `1` with `--grip expects "<device or VID:PID>=<grip id>".`, `--max-modifiers takes 0, 1 or 2.`,
  `--phase-layers takes on, off or auto.` or `No usable device: connect your devices or add them from the catalog
  (--add-device).`
- Data: `DB\Devices\*.json`, `DB\Functions\slot_controls.tsv`. Reference: [[doc:auto-assign]]. Related:
  [[cli:devices kb]], [[cli:setup plan]], [[cli:arrange ask]].
:::

<!-- entry: llm -->
:::layer beginner
Shows the state of the optional local AI model: switched on or off, ready or not, the chosen model, the downloaded
runtime, this PC's hardware and which model size JoyPro recommends for it. `engine llm recommend` prints only the
recommendation. Settings > Local AI shows this status (**Refresh** reads it again), and every AI action checks it first.

```powershell
engine llm status --text
```
:::

:::layer advanced
- Without options the hardware is detected (RAM, graphics cards with video memory, cores, AVX2).
- `--ram-gb`, `--vram-gb`, `--gpu <name>`, `--gpu-vendor nvidia|amd|intel`, `--cores`, `--avx2 true|false` replace the
  detected values, to see what JoyPro would recommend for another PC.
- The recommendation: about 8 GB video memory: the large model on the graphics card; about 4 GB: the medium one; else
  on the processor by memory and AVX2 (medium, small, tiny), or none below about 4 GB of memory.
- Which commands can use the model: [[cli:relations from-text]], [[cli:relations assistant]], [[cli:relations chat]],
  [[cli:relations duplicates]] `--instruction`, [[cli:arrange ask]] and `engine relations plan` (Manage with AI). Auto
  Group, Auto Assign, the Setup wizard, the suggestions and the cockpit database never use it.
- Read-only; no profile is needed.
:::

:::layer technical
- Output: JSON (`Enabled`, `Ready`, `SelectedModel`, `Hardware`, `Recommendation`, the runtime, your own llama.cpp, the
  models with size, licence, installed, recommended), or `--text`. `recommend`: the recommendation alone.
- Exit code `1` with `Invalid --ram-gb value: ...` (and the same for the other values) or an unknown argument.
- Files: `%LOCALAPPDATA%\JoyPro2\Llm\settings.json`, the model list `DB\Llm\models.json`.
- Related: [[cli:llm download]], [[cli:llm use-local]], [[cli:llm enable]], [[cli:llm start]], [[cli:llm server]].
:::

<!-- entry: llm download -->
:::layer beginner
Downloads a local AI model and the llama.cpp runtime it runs in, checks both, and switches the model on. Nothing
downloads without `--accept-license`. Settings > Local AI > **Download and switch on** (after ticking **I accept the
size and the licenses above**) runs it.

```powershell
engine llm download --accept-license --text
```
:::

:::layer advanced
- `<model>` (or `--model <id>`): a model id of `DB\Llm\models.json`; without it, the chosen or recommended model.
- `--accept-license`: without it the command only names the download (size, source, licence) and fails.
- `--runtime auto|cpu|vulkan|cuda`: the llama.cpp build (`auto`: Vulkan with a graphics card, else CPU); an explicit
  choice is remembered. `--no-runtime` downloads the model only.
- The model comes from its pinned source and is checked by SHA-256; a model already downloaded and verified is not
  fetched again. A stopped download keeps its part and the next download resumes it.
- After success the model is chosen and the local AI is switched on. No profile is touched.
:::

:::layer technical
- Files: `%LOCALAPPDATA%\JoyPro2\Llm\models\<file>` with `<file>.sha256`, the runtime in `Llm\runtime`, the progress in
  `Llm\download-progress.json` (the GUI follows it), `Llm\settings.json`.
- Output: JSON (`Model`, `Runtime`, `Consent`, `Messages`, `Path`), or `--text`.
- Exit code `1` with `Downloading needs your consent. ... Run again with --accept-license.`, `Unknown model '...'`,
  `Unknown runtime '...'`, `Download failed: ...` or the cancel message.
- [[cli:llm cancel]] stops a running download. Related: [[cli:llm remove]], [[cli:llm]].
:::

<!-- entry: llm remove -->
:::layer beginner
Deletes a downloaded local AI model from this PC to free the space. Settings > Local AI > **Remove model** runs it for
the chosen model.

```powershell
engine llm remove qwen2.5-1.5b-instruct-q4_k_m --text
```
:::

:::layer advanced
- `<model>`: that model; without a model id every downloaded model of the list is removed.
- The model file, a partial download and its checksum file go. A removed model that was chosen is no longer chosen.
- `--all` also deletes the llama.cpp runtime and switches the local AI off.
- Your own llama.cpp and GGUF files ([[cli:llm use-local]]) are never deleted.
:::

:::layer technical
- Output: JSON or `--text` with `Removed <model>.`, `Removed the llama.cpp runtime.` or `Nothing to remove.`
- Exit code `1` with `Unknown model '...'.` or more than one model id.
- Files: `%LOCALAPPDATA%\JoyPro2\Llm\models`, `Llm\runtime`, `Llm\settings.json`.
:::

<!-- entry: llm enable -->
:::layer beginner
Switches the optional local AI model on, optionally choosing which downloaded model to use; `engine llm disable`
switches it off. Settings > Local AI > **Use the local AI model** runs it.

```powershell
engine llm enable qwen2.5-1.5b-instruct-q4_k_m --text
```
:::

:::layer advanced
- `<model>`: a model id of `DB\Llm\models.json`; without it the chosen model stays. `local`: your own llama.cpp and
  GGUF set with [[cli:llm use-local]] (refused when none is set). `llm disable` takes no model: it switches the
  model off and keeps the chosen one.
- It only changes the setting; nothing is downloaded or started. Whether the model is usable (downloaded, runtime
  there) shows [[cli:llm]].
- Switched on, these use it: [[cli:relations from-text]], [[cli:relations assistant]], [[cli:relations chat]],
  [[cli:relations duplicates]] `--instruction`, [[cli:arrange ask]] and [[cli:relations plan ask]]. Off, they use
  JoyPro's offline rules only.
- No profile is touched.
:::

:::layer technical
- Output: the settings as JSON, or `--text` (`Local AI model enabled (<model>).` / `... disabled ...`).
- Exit code `1` with `No own llama.cpp / GGUF set (engine llm use-local --server <llama-server.exe> --model
  <file.gguf>).`, `Unknown model '...'. Models: ...` or `llm disable takes no model: ...`.
- File: `%LOCALAPPDATA%\JoyPro2\Llm\settings.json`. Related: [[cli:llm download]], [[cli:llm start]].
:::

<!-- entry: llm cancel -->
:::layer beginner
Stops a running model download. The part already downloaded is kept, and the next [[cli:llm download]] resumes it.

```powershell
engine llm cancel --text
```
:::

:::layer advanced
- It leaves a stop request that the running download checks for while it reads data; the download then ends with its
  cancel message.
- Without a running download it does nothing harmful: the next download clears the request before it starts.
- The GUI's stop button of a download leaves the same kind of request on its own.
:::

:::layer technical
- Output: `{"Cancel":true}`, or `--text` (`Download cancel requested.`); exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\Llm\download.cancel`.
- Related: [[cli:llm download]], [[cli:llm]].
:::

<!-- entry: llm find-local -->
:::layer beginner
Looks for a llama.cpp server (`llama-server.exe`) and GGUF model files that are already on this PC, and names the pair
that suits the hardware. It only reads. Settings > Local AI > **Find on this PC** runs it and fills in the paths.

```powershell
engine llm find-local --text
```
:::

:::layer advanced
- Searched by default: `llama-server.exe` on the PATH, folders like `llama.cpp`, `AI`, `LLM`, `models` and `tools`
  on the fixed drives, your `Downloads`, the Hugging Face cache, LM Studio's model folders and JoyPro's own model
  folder. `--root <folder>` (repeatable) searches only these folders.
- At most 7 folder levels deep and about 25 seconds; Ollama's own server and blobs are skipped.
- Only language models count (no vision projectors, vocab-only or embedding files, from 50 MB; of a split model the
  first part).
- Recommended: the largest model that fits completely in the graphics card's memory, else the smallest that fits the
  memory; a GPU build of the server when there is a graphics card.
- Changes nothing; use [[cli:llm use-local]] to take the pair.
:::

:::layer technical
- Output: JSON (`Searched`, `Servers` with backend, `Models` with size, `FitsGpu`, `FitsRam`, `BestServer`,
  `BestModel`, `Reason`, `Seconds`), or `--text`.
- Exit code `0`; `1` for an unknown argument.
:::

<!-- entry: llm use-local -->
:::layer beginner
Makes JoyPro use a llama.cpp server and GGUF model you already have instead of a download, and switches the local AI
on. Nothing is downloaded. Settings > Local AI > **Use these files** runs it with the two paths; **Stop using them**
runs `--clear`.

```powershell
engine llm use-local --server "C:\Users\Pilot\llama.cpp\llama-server.exe" --model "C:\Users\Pilot\models\Qwen3-8B-Q4_K_M.gguf" --text
```
:::

:::layer advanced
- `--server`: `llama-server.exe` (or its folder); `--model`: the `.gguf` file. A missing one is taken from the search
  of [[cli:llm find-local]].
- `--gpu-layers auto|all|<n>` (`auto`: llama.cpp decides), `--context <n>` (512 or more), `--template server|chatml`
  (`server`: the GGUF's own chat template).
- `--clear` forgets the two paths; when they were in use the local AI is switched off. Your files stay where they are.
:::

:::layer technical
- Output: JSON or `--text` (`Model: local`, the runtime path and the message `Using your llama.cpp (...) with ...;
  the local AI model is on. Nothing was downloaded.`).
- Exit code `1` with `llama-server.exe not found: ...`, `GGUF model not found: ...`, `No llama-server.exe found
  (searched ...)`, `Invalid --gpu-layers value: ...`, `Invalid --context value: ... (512 or more).` or `Invalid
  --template value: ...`.
- File: `%LOCALAPPDATA%\JoyPro2\Llm\settings.json`. Related: [[cli:llm enable]] (`local`), [[cli:llm]].
:::

<!-- entry: llm start -->
:::layer beginner
Loads the chosen local AI model now, so the next AI request answers without the loading wait. Settings > Local AI >
**Start now** runs it. Started from JoyPro, the model stays loaded until the idle stop; from a command line it ends
again when the command ends.

```powershell
engine llm start --text
```
:::

:::layer advanced
- Needs a usable model: the local AI switched on and a downloaded model (with its runtime) or your own llama.cpp and
  GGUF.
- The model runs in its own `llama-server` process on this PC. [[cli:llm server]] shows it, `engine llm stop` ends it
  and frees its memory.
- No profile is touched.
:::

:::layer technical
- Output: JSON (`Started`, `Model`, `ModelId`, `Pid`, `LoadMs`, `StaysLoaded`), or `--text` (`Started the local AI
  model <name> (process <pid>, <s> s); ...`).
- Exit code `1` with `The local AI model cannot start: <reason>` (for example the model is off or not downloaded) or
  `The local AI model (<name>) did not start: ...`.
:::

<!-- entry: llm server -->
:::layer beginner
Tells whether JoyPro's local AI model is loaded right now, which model and how much memory it uses.
`engine llm stop` ends it and frees that memory.

```powershell
engine llm server --text
```
:::

:::layer advanced
- Started from JoyPro, the model stays loaded between requests until the idle stop; from a command line every request
  starts and ends its own model process, so `server` usually says `Not running.` there.
- `stop` ends only the process JoyPro recorded as its model server (not another program's llama.cpp) and forgets it.
- No profile is touched.
:::

:::layer technical
- Output of `server`: JSON (`Running`, and when running `Pid`, `Model`, `LastUsedUtc`, `IdleMinutes`, `Busy`,
  `WorkingSetBytes`), or `--text` (`Running: <model> (process <pid>)` / `Not running.`). Output of `stop`: JSON
  (`Stopped`, `Pid`), or `--text` (`Stopped the local AI model (process <pid>).` / `The local AI model is not
  running.`). Exit code `0`; `1` with an extra argument.
- File: `%LOCALAPPDATA%\JoyPro2\Llm\server.json` (the running server; removed by `stop`).
- Related: [[cli:llm start]], [[cli:llm]].
:::

<!-- entry: llm fake-server -->
:::layer beginner
A test helper, not for players: a small stand-in for the llama.cpp server that JoyPro's automatic tests use, so the
AI paths and the model download can be tested without a real model or the internet. It listens on this PC only and
ends by itself.

```powershell
engine llm fake-server --port 0 --port-file "C:\Users\Pilot\AppData\Local\Temp\fake-port.txt" --lifetime 60
```
:::

:::layer advanced
- `--port N` (default `0` = a free port); `--port-file <file>` receives the port it got.
- `--response <file>`: the answer to every completion request; answers separated by a line `#next` are given one per
  request, the last one repeated. Without it the answer is `{}`.
- `--serve <folder>`: files of that folder are downloadable (with resume), for the downloader tests.
- `--log <file>`: every request is logged. `--lifetime <s>` (default 300): it ends after that many seconds at the
  latest.
- Only the loopback address; nothing of the profile or settings is touched.
:::

:::layer technical
- Answers `GET /health`, `POST /completion`, `POST /v1/chat/completions` and `GET /files/<name>`.
- Exit code `0` when the lifetime is over; `1` with `Invalid --port value: ...`, `Invalid --lifetime value: ...` or an
  unknown argument.
- The engine starts it itself instead of llama.cpp when the variable `JOYPRO_LLM_FAKE_RESPONSE` names a response file.
:::

<!-- entry: setup devices -->
:::layer beginner
Step 2 of the Setup wizard: recognises your connected devices with JoyPro's device catalog and proposes a role for
each one (main stick, throttle, pedals, panel ...), with the rules the plan will follow in words. The wizard runs it
when it reaches the devices step and on **Detect again**. It changes nothing.

```powershell
engine setup devices --role "VPC MongoosT-50CM3 Throttle=throttle" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--role "<device or VID:PID>=<role>"` (repeatable) sets a role: `main-stick`, `heli-stick`, `left-stick`,
  `throttle`, `collective`, `pedals`, `panel`, `keyboard-joystick`, `virtual` or `unused`.
- `--grip "<device or VID:PID>=<grip id>"` picks the grip of a base that takes several grips.
- `--add-device <catalog id>` (repeatable) adds a device that is not connected.
- With `--profile` the device your profile uses most is proposed as the main stick.
- Read-only. The same device options work with [[cli:setup plan]] and [[cli:arrange ask]].
:::

:::layer technical
- Output: JSON (`Devices` with role, proposed role, reason, model, kind, hand, grips and input count, `Rules`, `Roles`
  with titles, `Warnings`), or `--text`.
- Exit code `1` with `--grip expects "<device or VID:PID>=<grip id>".`, `--role expects "<device or VID:PID>=<role>"
  (...)` or an unknown argument.
- Reference: [[doc:setup-wizard]]. Related: [[cli:devices kb]].
:::

<!-- entry: setup aircraft -->
:::layer beginner
Step 3 of the Setup wizard: lists the aircraft of the chosen games for the priority list (DCS: the modules of your
DCS database; other games count as one entry). The wizard runs it when it reaches the priorities step. It changes
nothing.

```powershell
engine setup aircraft --games DCS,StarCitizen --text
```
:::

:::layer advanced
- `--games <g1,g2>` (needed; `,`, `|` or `;` between ids): the game ids, for example `DCS`, `StarCitizen`, `IL2Game`.
- Each entry has its key, game, type, class (fixed wing, helicopter, spaceship ...) and era.
- Read-only; no profile is needed.
:::

:::layer technical
- Output: JSON (`Aircraft` with `Key`, `Game`, `Type`, `Class`, `ClassTitle`, `Era`, and `Warnings`), or `--text`.
- Exit code `1` with `setup aircraft needs --games <game ids>.` or an unknown argument.
- Related: [[cli:setup plan]] (`--priorities` takes these keys).
:::

<!-- entry: setup plan -->
:::layer beginner
Step 4 of the Setup wizard: makes the whole plan for your games and devices: which relations to make, which input each
gets, which modifiers, and a quality score per aircraft. It runs without AI; the same choices always give the same
plan. The wizard runs it at step 4 and on **Make the plan again**. It changes nothing; [[cli:setup apply]] writes it.

```powershell
engine setup plan --games DCS --priorities "F-16C_50>FA-18C_hornet|A-10C_2" --modifiers 1 --out-plan "C:\Users\Pilot\Documents\JoyPro\plan.json" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--games <g1,g2>` (needed): the games to set up.
- `--priorities "A>B|C>D"`: `>` separates rows, `|` (or `=`) aircraft of one row; `--priority "B|C"` (repeatable) adds
  one row. Aircraft not listed count least.
- `--modifiers 0..8` (default 0) with `--prefer-fewer` (default: a modifier only when the essential functions do not
  fit) or `--use-all-modifiers`.
- `--realism 0..1` or `0..100%` (default 0.5): from 50 % aircraft whose real HOTAS has a function elsewhere get their
  own relation; `--cockpit-file <json>` (repeatable) adds cockpit layouts from [[cli:cockpits]] `get --json`.
- `--phase-layers auto|on|off`, `--seed <n>` (default 1), `--replace-existing` (lays your bound relations out again).
- The device options of [[cli:setup devices]]: `--role`, `--grip`, `--add-device`.
- `--out-plan <file>` (or `--plan-file`) writes the plan file for [[cli:setup check]], [[cli:setup render]] and
  [[cli:setup apply]].
- Your profile first: commands in your relations stay there, bound relations keep their inputs. Read-only.
:::

:::layer technical
- Output: the plan as JSON, or `--text` (a summary); the plan file is written indented.
- Exit code `1` with `setup plan needs --games <game ids> ...`, `No device to plan with: connect your devices (or give
  them a role with --role).`, `--modifiers takes 0 to 8.`, `--prefer-fewer and --use-all-modifiers exclude each
  other.`, `--realism takes 0..1 (or 0..100 %).` or `--phase-layers takes on, off or auto.`
- Reference: [[doc:setup-wizard]].
:::

<!-- entry: setup check -->
:::layer beginner
Checks a saved Setup wizard plan against the rules every plan keeps: no input does two things in one aircraft, few
modifiers, trigger and pinky only for their own functions, MFD keys in order, slew not on the TMS hat, and more. It
changes nothing.

```powershell
engine setup check --plan "C:\Users\Pilot\Documents\JoyPro\plan.json" --text
```
:::

:::layer advanced
- `--plan <file>` (needed): a plan of [[cli:setup plan]] `--out-plan`.
- `--modifiers N` (default 2): how many shift modifiers are allowed; a plan that asked for more allows its own number.
- The rules, C1 to C11: no input serves two relations of one aircraft (also across phase layers); at most the allowed
  modifiers and two phase layers; trigger, weapon release and pinky only take their own functions; MFD keys only MFD
  functions, in OSB order; printed panel labels only the functions they name; slew on slew controls and TMS on its
  hat; CMS forward dispenses and left / right change the program; one device per hand; phase layers only functions of
  their phase; modifier layers only for games that handle them; directional sets together on one hat.
- Also measured: relations placed and not placed, essential functions of the top aircraft placed, time-critical
  functions on the grips.
- Read-only.
:::

:::layer technical
- Output: JSON (`Checks` with `Id`, `Title`, `Passed`, `Problems`, the counts, `Passed`), or `--text` (`Checks: all
  passed` or `<n> failed`, `PASS` / `FAIL` per rule with up to 8 problems, a `Placed:` line).
- Exit code `0` when every rule passes, `1` when a rule fails (the report is printed first), or with `setup check
  needs --plan <plan.json> (from engine setup plan --out-plan).`, `The plan could not be read: ...` or `Unknown setup
  check argument(s): ...`; a non-number for `--modifiers` ends with `engine error: Invalid value for option
  --modifiers ...` (no crash report).
- Reference: [[doc:setup-wizard]]. Related: [[cli:setup apply]], [[cli:setup render]].
:::

<!-- entry: setup render -->
:::layer beginner
Draws a callout sheet per device of a saved Setup wizard plan: the device picture with every planned bind labelled.
With `--aircraft` only that aircraft's binds, labelled with its own function names. The wizard's review draws its
sheets with it.

```powershell
engine setup render --plan "C:\Users\Pilot\Documents\JoyPro\plan.json" --out-dir "C:\Users\Pilot\Documents\JoyPro\sheets" --aircraft F-16C_50 --text
```
:::

:::layer advanced
- `--plan <file>` and `--out-dir <folder>` are needed; the folder is created, and files of the same name are
  overwritten.
- `--aircraft <name>`: only binds that cover that aircraft. `--width <px>`: picture width (default 1800, at most
  6000; the wizard uses 1600). `--light`: light sheets instead of dark.
- The drawing options of the device pictures work here too: `--drawing-style lines|cel`, `--drawing-preset`,
  `--drawing-ink`, `--drawing-paper`, `--drawing-accent` (colours as `#RRGGBB`); otherwise the drawing style of
  Settings.
- Devices with a role that takes binds are drawn; a device that is not in the device catalog gets no sheet and a
  warning. With several modifiers each label names its modifier.
- The profile is not touched.
:::

:::layer technical
- Files: `<out-dir>\<nn>-<device>.png`, or `<nn>-<device>-<aircraft>.png` with `--aircraft`.
- Output: JSON (`Sheets` with `Device` and `File`, `Warnings`), or `--text` (`Setup Sheets: <n>` with `<device> =>
  <file>` lines and `Warning:` lines).
- Exit code `1` with `setup render needs --plan <plan.json> and --out-dir <folder>.`, `The plan could not be read:
  ...`, `Unknown setup render argument(s): ...`; a non-number for `--width` ends with `engine error: Invalid value for
  option --width ...` (no crash report). A sheet that cannot be drawn is a warning, not an error.
- Reference: [[doc:setup-wizard]]. Related: [[cli:layout render]], [[cli:setup check]].
:::

<!-- entry: setup apply -->
:::layer beginner
Writes a plan of the Setup wizard into the profile in one step: it creates or extends the relations, sets their binds
and adds the modifiers. The wizard's **Apply to my profile** runs it with the plan as you edited it in the review. In
the game: the binds arrive with the next export.

```powershell
engine setup apply --plan "C:\Users\Pilot\Documents\JoyPro\plan.json" --dry-run --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--plan <file>` (needed): a plan file of [[cli:setup plan]] `--out-plan`.
- New relations get the group `Setup`. A command another relation got in the meantime stays there and is listed as
  skipped.
- Refused when two relations with an aircraft in common share an input, unless `--force`.
- `--dry-run` counts without changing the profile.
- One profile version "Setup wizard: N relations, M binds" (Undo takes all of it back).
:::

:::layer technical
- Output: JSON (`DryRun`, `Created`, `Extended`, `NodesAdded`, `Bound`, `ModifiersAdded`, `Skipped`, `Problems`), or
  `--text`. The profile is saved with `--profile` unless `--dry-run`.
- Exit code `1` with `setup apply needs --plan <plan.json> (from engine setup plan --out-plan).`, `The plan could not
  be read: ...` or `The plan puts functions of the same aircraft on one input: ... Fix them in the review (or apply
  with --force).`
- Related: [[cli:setup check]], [[cli:setup render]], [[cli:profile history restore]].
:::

<!-- entry: arrange ask -->
:::layer beginner
Lay out with AI, step one: you say in words how you like your controls ("throttle and left-hand stick, trim on the
hat, curves softer for helis") and JoyPro prepares every change to binds, axis curves, force feedback and the virtual
joystick as a preview file. Phrase rules read the words; the optional local AI model only helps with sentences they do
not understand. Binds > **Lay out with AI...** > **Preview** runs it. It changes nothing.

```powershell
engine arrange ask "throttle and left-hand stick, trim on the hat, curves softer for helis" --llm off --proposal "C:\Users\Pilot\Documents\JoyPro\layout.json" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The words: every argument that is not an option. Without words the preferences kept in the profile are used.
- `--llm auto` (default) or `on`: the local model rewrites the clauses the rules did not understand into phrases they
  know (only when there are such clauses); `off`: never.
- `--games <g1,g2>`: default the games of your relations that have a function catalog.
- `--add-missing` also makes relations for functions you do not have yet; `--forget-saved` ignores the kept
  preferences.
- `--proposal <file>`: where the preview is written (default `%TEMP%\JoyPro2\layout-ai\proposal.json`).
- The device options of [[cli:setup devices]]: `--role`, `--grip`, `--add-device`.
- Only relations a preference concerns, relations on a device whose role changed, and unbound relations move; the rules
  of Auto Assign hold.
:::

:::layer technical
- Output: the preview as JSON (understood preferences, not understood clauses, the changes with their ids and before /
  after values, warnings), or `--text`; the same preview goes to the proposal file. The profile is not saved.
- Exit code `1` with `arrange ask needs your preferences in words, ...` (no words and no kept preferences), `Invalid
  --llm value: ...` or a device option message.
- Reference: [[doc:layout-with-ai]]. Related: [[cli:arrange apply]], [[cli:arrange prefs]].
:::

<!-- entry: arrange apply -->
:::layer beginner
Lay out with AI, step two: applies the preview file of `arrange ask` in one step and keeps your preferences in the
profile for next time. Binds > **Lay out with AI...** > **Apply** runs it with the changes you unticked as `--skip`;
**Undo** takes it back.

```powershell
engine arrange apply --proposal "C:\Users\Pilot\Documents\JoyPro\layout.json" --skip "vjoy:StarCitizen" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--proposal <file>` (needed): the file [[cli:arrange ask]] wrote.
- `--skip <change id>` (repeatable): `bind:<relation>`, `split:<relation>`, `tuning:<relation>|<scope>`,
  `ffb:<device>|<scope>`, `vjoy:<game>`. Skipping a split also skips the new relation's bind.
- Refused when the layout puts functions of the same aircraft on one input.
- `--dry-run` counts without a change. Otherwise one profile version "Lay out with AI: N binds, ..." (one undo step).
:::

:::layer technical
- Output: JSON of the result, or `--text` (`Binds Set:`, `Relations Created:`, `Relations Split:`, `Axis Curves Set:`,
  `Force Feedback Set:`, `Virtual Joystick Set:`, skipped items and warnings). The profile is saved with `--profile`
  unless `--dry-run`.
- Exit code `1` with `arrange apply needs --proposal <proposal.json> (from engine arrange ask).`, `The preview could
  not be read: ...`, `That file is no preview of engine arrange ask.` or `The layout puts functions of the same
  aircraft on one input: ...`.
- Related: [[cli:arrange prefs]], [[cli:profile history restore]].
:::

<!-- entry: arrange prefs -->
:::layer beginner
Shows the layout preferences Lay out with AI keeps in the profile, or forgets one or all of them. When the Lay out with
AI panel opens, the GUI reads them; the cross on a preference and **Forget all** run `remove` and `clear`.

```powershell
engine arrange prefs show --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `show` (default): the kept preferences with their keys.
- `remove <key>`: forgets one; `clear`: forgets all. Each is one profile version (one undo step).
- Forgetting changes no bind; the next [[cli:arrange ask]] simply does not use it.
:::

:::layer technical
- Output: JSON (the preferences), or `--text` (`Saved preferences: <n>` and `- <description> | <key>` lines). The
  profile is saved with `--profile` after `remove` or `clear`.
- Exit code `1` with `arrange prefs remove <key>`, `No saved preference with the key ...` or `arrange prefs [show |
  clear | remove <key>]`.
:::

<!-- entry: cockpits -->
:::layer beginner
JoyPro's cockpit database: real and fictional cockpits with their stick and throttle controls, what each does per mode,
the panels and the sources. Use it to see how the real aircraft uses each button. The Cockpits page runs `get --json`
for **Use this layout**, `compare` for **Compare with my setup** and `pictures build --game all` for **Build pictures
from my games**; the Setup wizard reads `get --json` of your priority aircraft.

```powershell
engine cockpits get f-16 f-16c-block-50 --text
```
:::

:::layer advanced
| Verb | What it does |
|---|---|
| `list [query]` (default) | aircraft and variants; filters `--game`, `--era`, `--role`, `--nation`, `--category fixed-wing|helicopter|space|fiction|real` |
| `search <text> [--limit n]` | aircraft and controls by words (default 50 each) |
| `get <aircraft> [<variant>]` | one variant: devices, controls, functions per mode, panels, sources; the JSON is the `joypro-cockpit-layout` format |
| `compare <aircraft> [<variant>] [--game G]` | with `--profile`: which functions of the variant your relations hold, with relation and input |
| `render <aircraft> [<variant>] --png <file>` | the grip sheets as PNG (`--device`, `--style dark|light`, `--print-width`, `--drawing-only`) |
| `render <aircraft> [<variant>] --page <page> [--png <file>] [--pdf <file>]` | the Cockpits page's manual pages (`all`, `overview`, `stick`, `throttle`, a device, `panels`, `panel:<id>`, `photos`, `photo`); several pages with `--png` become `<file>.<page>.png`; `--style` (default light), `--scale 0.1..4`, `--highlight <words>`, `--pictures picture|drawing|none`, `--photos <folder>|none` |
| `photos [query] [--coverage] [--verified]`, `photos --needed [--game G]`, `coverage`, `drawings` | photo links, the variants that still need photos, what the database covers, the drawings |
| `guide <aircraft> [<variant>] [--lang <code>] [--search <words>] [--no-pictures]`, `guide --coverage [--game G]` | the cockpit guide as a tree (areas, panels, controls with what each does and the game's commands); per variant what it explains |
| `check [--structure]` | checks the database, drawings and guide; `--structure` lists only what is wrong, not what is still missing |
| `pictures build|status|check` | the cockpit pictures made on this PC from your installed games |

- Aircraft and variants are found by id, name or alias; a game's own aircraft name (`F-16C_50`) works too.
- `pictures build [--game <id>|all] [--aircraft <a,b>] [--force]` renders the cockpits of the installed games (DCS,
  X-Plane 12, MSFS 2020, IL-2 Great Battles, Nuclear Option, War Thunder, Star Citizen) into JoyPro's data folder, never
  into a game folder; DCS needs `luae.exe` in its folder. A game that is not installed is skipped with a note.
- Nothing here changes the profile or a game.
:::

:::layer technical
- Output: JSON, or `--text`; `--json` is accepted and ignored (JSON is the default).
- Exit code `1` with `No aircraft '...' in the cockpit database (cockpits list).`, `No variant '...' of ...`,
  `cockpits search needs a text ...`, `cockpits render needs --png <file>.`, `--style is dark or light.` or `cockpits
  pictures build --game takes ...`; `check` ends with `1` when it finds problems. `pictures build` shows progress and
  can be cancelled (exit code `3`).
- Data: `DB\Cockpits\*.json` with `drawings`, `guide` and `photo-labels`; pictures in
  `%LOCALAPPDATA%\JoyPro2\GameData\CockpitPictures\<game>`.
- Reference: [[doc:cockpits]]. Related: [[cli:setup plan]] (`--cockpit-file`).
:::

<!-- entry: relations join-inputs -->
:::layer beginner
Experimental: relations that do exactly the same (the same actions with the same aircraft on) on other inputs become
one relation with several inputs. An import names them when the setting is on ("keep each as one relation with
several inputs?").

```powershell
engine --multi-bind on relations join-inputs --game DCS --apply --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Without `--apply` a preview. The relation kept is one without a copy suffix, else the shortest name; the others'
  inputs become its extra inputs, their groups join it, and they are removed (one undo step).
:::
