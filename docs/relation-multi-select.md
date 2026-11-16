# Several actions at once (2.7.0)

The relation editor and the Relations list work on several actions or relations at once. Every operation is one
engine call, so one profile version and one Ctrl+Z; the moves and the paste are versions of their own (never merged
with the edit just before them, `EngineProfileHistory.NoCoalesce`).

## Relation editor (Relations page, Its actions)

- Ctrl+click / Shift+click picks several action cards, Ctrl+A (or **Select all** in the bar) all of them. A click with
  Ctrl or Shift only selects (it does not open the action's aircraft). The bar "n actions selected" has **Copy**,
  **Move to...**, **Remove**, **Select all** and **Clear**.
- The right-click menu of an action has **Move to another relation...**, **Copy** and **Remove from the relation**: for
  that action, or for the selection when the action is part of it.
- **Copy** (Ctrl+C) remembers the relation and the actions (`game:id`). In another relation of the same kind (axis or
  button) the header shows **Paste n action(s)** (Ctrl+V): `relations item copy-to <source> --node ... --to <target>`.
  Pasting into a relation of the other kind is refused in the GUI with a note.
- **Move to...** opens a window: **New relation** (a name, suggested "<source> (2)"; a taken name is refused) or
  **Existing relation (same kind)** (a searchable list of the relations of the same kind, without the source):
  `relations item move-to <source> --node ... (--new <name> | --to <target>)`. The source stays open, also when it is
  left without actions.
- **Remove** (Del) asks once: `relations item remove <relation> --node game:id ...` (the whole action, every aircraft).

## Relations list

With several relations selected the bar has **Move actions to...** (next to Delete): every action of them goes to a new
relation or an existing one of the same kind, and the emptied relations are deleted with their binds:
`relations mass move-actions --relations <csv> --delete-empty (--new <name> | --to <target>)`. Relations of both kinds
together are refused with a note.

## Engine (EngineRelationItemTransfer.cs)

    engine relations item copy-to <relation> (--node game:id ... | --all) --to <relation> [--force]
    engine relations item move-to <relation> (--node game:id ... | --all) (--to <relation> | --new <name>) [--delete-empty] [--force]
    engine relations mass move-actions (--to <relation> | --new <name>) [--relation <n>] [--relations <csv>] [--query <q>] [--all] [--delete-empty] [--force]

- An action keeps its aircraft and which of them are switched off (`RelationItem.Copy`); an action the target already
  has gains the aircraft it lacked (`Relation.IncludeRelationItem`).
- The target must be of the same kind unless `--force`; the source itself as target is refused. A new relation
  (`--new`) has the source's kind, no bind and no groups.
- When the target has another action of the same game for an aircraft, both stay and the output warns (one aircraft
  should have one action per relation; the Check page lists it).

## Tests

- engine/tests/smoke/relationtransfer.ps1: copy-to keeps the switched-off aircraft, the source keeps its actions, one
  undo takes the copy back; the other kind and the source as target are refused; move-to an existing and a new relation
  (kind, no bind, taken name refused, `--delete-empty`); mass move-actions with `--delete-empty`, mixed kinds refused.
- gui/JoyPro.Gui.Tests/RelationMultiSelectTests.cs: remove, copy/paste (other kind refused), move to a new and an
  existing relation (only the same kind listed, search, taken name), the Relations list's move, the list's selection
  reaching the view model and the bar; renders with `JOYPRO_MULTISELECT_RENDER_OUT`.
