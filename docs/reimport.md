# Re-import: JoyPro's own data next to the game files

A game file only holds what the game needs (device, input, maybe a curve). So an export also keeps JoyPro's own data
of every exported bind next to it, and an import into a new or empty profile gets the relations back as they were
(like JoyPro 1 did with its DCS `.jp` files and IL-2 comments). Checked 2026-10-05 with the March 2025 reference profile
(524 relations, 16 groups, 9 modifiers): exported, imported into an empty profile, compared field by field.

| Game | Where JoyPro keeps its data | What comes back |
|---|---|---|
| DCS | `<plane>\joystick\<device>.jp` beside each `.diff.lua` (`["JPK"]` relation name, `["JGRP"]` groups in their order, the axis filter); `modifiers.lua` per plane (modifier names, device, key, switch) | 439/439 exported relations: name, groups (order), bind, modifiers with names, invert, slider, dead zone, saturation, curvature / 11-point curve. Keyboard binds only with `--keep-keyboard-defaults false` (keyboard binds are exported on request only). |
| IL-2 Great Battles | `data\input\jp.actions`: `//JP:name;groups;alias;modifiers;base;scopes` per action (escaped with `%XX`; the first three fields are the old form JoyPro 1 also wrote) | 41/41: name, groups (order), alias, modifiers with names and switch, the exact tuning (base and every scope), hats. |
| Registry games (X-Plane, Elite, X4, Arma, Reforger, WARDOGS, BMS, MSFS, War Thunder, VTOL VR, ...) | `<config file>.joypro-meta.json` (`GameMetadataStore`): per action + device + input + modifiers the relation name, groups, alias, modifiers, base tuning and every scope, and how the file read back after the export | name, groups, alias, modifiers, base tuning and every scope (smoke tests for Arma 3, Arma 2 OA, Reforger, VTOL VR). |
| Star Citizen | `actionmaps.jp` (comments with the relation name) | names; groups are `GENERATED`, `StarCitizen`. |

Rules:

- **JoyPro's data is the base, the game's current state wins.** DCS: the `.diff.lua` filter (what DCS uses) wins over
  the `.jp` filter, so a curve changed in DCS after the export is kept. IL-2: the stored tuning is applied first and a
  response changed in IL-2 since then becomes the bind's IL-2 tuning. Registry games: when the file no longer reads
  as it did right after the export, its values become the game's tuning on top of the stored one.
- **The import options (`--import-inverted`, `--import-curvature`, ...) are only about values set in the game.** A
  bind JoyPro exported itself gets its filter back without them (before 2026-10-05 a default import dropped them: a
  re-export then removed every curve from DCS; on the user's real DCS files 1121 filters were lost, now all are kept).
- A relation exported without groups comes back without groups (DCS); binds JoyPro did not export get `DCS` / `IL2`
  / `GENERATED` + game as before, and names built from the game's action.
- Not in any game file, so not restorable from one: relations without a bind, DCS nodes without an active aircraft
  (not exported, with a warning; JoyPro 1 did not export them either, a new DCS node without planes now gets every
  aircraft that has the id, as in JoyPro 1), and the nodes of other games of a relation (import those games with
  `--merge`). Profile-level data (layouts, pictures, plane aliases) lives in the `.pr0file` only.
- IL-2 hats: IL-2 numbers hats from 0 (`pov0` = first hat), JoyPro like DCS from 1 (`JOY_BTN_POV1_*`). The export
  wrote `POV1` as `pov1` (the second hat) and the import read `pov1` as buttons 80+; both fixed 2026-10-05.
