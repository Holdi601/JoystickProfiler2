Hand-written texts of the engine commands behind binds, modifiers, axis curves, detent mode, devices, device pictures
and cheat sheets, force feedback and the virtual joystick. Each block below belongs to the command named in its marker.

<!-- entry: binds list -->
:::layer beginner
Lists the binds of the profile: for each relation its device, input, modifiers and axis settings. An optional text
filters the list. The Binds page runs it to build its cards (**Refresh binds** under More, and after every change).

```powershell
engine binds list "Warthog" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The filter (all words after `list`, joined) matches, case-insensitive and as a part of the text: the relation name,
  the device, the button or axis, the device alias, `device::input` and the modifiers.
- Sorted by relation name. Relations without a bind are not listed ([[cli:binds show]] tells you more).
- Read-only.
:::

:::layer technical
- Output: the binds document as JSON (`FormatVersion`, `GeneratedUtc`, `Binds`), or a text list with `--text`; exit
  code `0`.
- `binds list` without an action is the same as `binds`.
- Related: [[cli:binds show]], [[cli:binds export]] (the same document into a file), [[cli:binds edit]].
:::

<!-- entry: binds show -->
:::layer beginner
Shows one bind in full: device, input, modifiers, axis settings and the relation with its actions. **Show selected in
output** under More on the Binds page runs it and prints the answer into the Output panel.

```powershell
engine binds show "Gear Up/Down" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The selector is a relation name, a bound input (`JOY_BTN22`) or `device::input`; a part of a relation name works
  when only one bound relation contains it.
- Read-only.
:::

:::layer technical
- Output: `Bind` and `Relation` as JSON, or text; exit code `0`.
- Failures (exit `1`): `binds show requires a bind name or relation name.`, `No bind matched query: ...`,
  `Ambiguous bind query '...'. Matches: ...`, `Bind not found for relation: ...` (the relation has no bind).
- Related: [[cli:binds list]], [[cli:binds set]].
:::

<!-- entry: binds set -->
:::layer beginner
Puts a relation on a button or axis, or changes its modifiers and axis settings. It creates the bind when the relation
has none. The Binds page runs it for **Record input** and **Set Bind**, the Layout page for **Bind to this input**, and
the virtual function editor for **Bind relation**.

```powershell
engine binds set "Gear Up/Down" --device "Throttle - HOTAS Warthog" --input JOY_BTN22 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
| Option | Effect on the bind |
|---|---|
| `--device <name>` | the device the input belongs to |
| `--input <token>` | the input; an axis relation stores it as its axis, a button relation as its button (the other field is emptied) |
| `--format dcs\|il2` | how `--input` is written: `dcs` (default, `JOY_BTN1`) or `il2` (`joy0_b0`, converted to the DCS form); needs `--input` |
| `--modifier <name>` (also `--mod`, `--reformer`; repeatable) | replaces the bind's whole modifier list |
| `--clear-modifiers` | empties the modifier list (ignored when `--modifier` is given) |
| `--clear-input` | empties the input, the bind and its settings stay |
| `--inverted`, `--slider true\|false` | axis invert and one-sided axis |
| `--curvature <csv>`, `--deadzone`, `--saturation-x`, `--saturation-y <n>` | the bind's own (base) axis curve |
| `--double-tap true\|false` | the double-tap flag (kept in the profile) |

- The axis options are refused on a button relation. At least one option is needed.
- Only the profile changes; the game gets the bind at the next export.
:::

:::layer technical
- Output: the bind after the change, as [[cli:binds show]] prints it; exit code `0`.
- Failures (exit `1`): `binds set requires at least one change option (e.g. --device, --input, --curvature,
  --inverted).`, `--format requires --input.`, `Invalid --deadzone value '...'`, `Axis tuning options (...) can only
  be set on axis relations.`, `No relation/bind matched query: ...`, `Unknown binds set argument(s): ...`.
- With `--profile` the profile is saved afterwards as a new history version (one undo step).
- Per-game and per-aircraft curves: [[cli:binds tuning set]]. Related: [[cli:binds clear]], [[cli:binds modifier add]].
:::

<!-- entry: binds clear -->
:::layer beginner
Takes the bind off a relation: by default the whole bind (input, modifiers, axis settings), with `--input-only` just
the input. The relation and its actions stay. **Clear Bind** on the Binds page runs the full clear, **Clear Input** the
`--input-only` form; the Layout page's free-marker button also uses it.

```powershell
engine binds clear "Gear Up/Down" --input-only --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Default: the bind is removed from the relation. `--input-only`: the button and axis are emptied, modifiers and the
  curve stay on the relation for the next input.
- A relation without a bind is no error; the result says `HadBind: false`.
- After the next export the relation is on no button in the game.
:::

:::layer technical
- Output: `Relation`, `InputOnly`, `HadBind`, `ClearedInput`, `RemovedBind` as JSON or text; exit code `0`.
- Failures (exit `1`): `binds clear requires: <bind-or-relation> [--input-only].`, `Unknown binds clear
  argument(s): ...`, `Relation not found: ...`.
- One undo step with `--profile`. Related: [[cli:binds unbind]] (free an input in every relation), [[cli:binds set]].
:::

<!-- entry: binds unbind -->
:::layer beginner
Frees one physical input in the whole profile: every relation bound to it loses that input. Use it to answer "nothing
may be on this button". **Unbind** under "Unbind an input everywhere (advanced)" on the Binds page runs it; **Unbind
selected input** in the header does it for the selected bind's input.

```powershell
engine binds unbind "Throttle - HOTAS Warthog::JOY_BTN22" --all --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The input: positional `device::input` or `input`, or `--input` (with `--device`). `--format il2` converts an IL-2
  token (`joy0_b0`).
- `--device <name>` limits it to binds on that device (exact name, any case); without it the input matches on any
  device.
- `--game <id>` limits it to relations with an action of that game: any game id (`DCS`, `IL2Game`, `StarCitizen`,
  `MSFS2024`, ...), any case; a node without a game counts as DCS.
- Without `--all` it refuses when more than one relation matches; with `--all` every match is freed (the GUI's
  **In all relations** tick; **Unbind selected input** always uses `--all` with the bind's device).
- Only the input goes: the binds keep their device, modifiers and curve.
:::

:::layer technical
- Output: `Input`, `NormalizedInput`, `Format`, `DeviceFilter`, `GameFilter`, `UpdatedRelations`; exit code `0`.
- Failures (exit `1`): `binds unbind requires --input <value> or positional <device::input|input>.`, `No bind matched
  input '...'.`, `Ambiguous bind match for input '...'. Matches: .... Use --all or narrow with --device/--game.`
- One undo step with `--profile`. Related: [[cli:binds clear]].
:::

<!-- entry: binds edit -->
:::layer beginner
Opens every bind of the profile as one JSON file in a text editor and reads it back when you close the editor. For
many changes at once. **Binds Edit** on the Advanced page runs it.

```powershell
engine binds edit --editor "code --wait {file}" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
- The file is the binds document of [[cli:binds export]], written to `%TEMP%\joypro-engine-binds-<time>.json`.
- The editor: the global `--editor` command (`{file}` stands for the file, else the path is appended; it runs through
  `cmd.exe /c`), else Notepad. The engine waits until the editor process ends, so use an editor's wait flag.
- On close the file is applied like [[cli:binds import]]: binds to the relations of the same name.
:::

:::layer technical
- Output: plain text `Imported binds from: <file>` and the profile summary; exit code `0`, `1` when the file is gone.
- With `--profile` the profile is saved afterwards as one history version.
- Related: [[cli:relations edit]] (the same for relations), [[cli:binds export]], [[cli:binds import]].
:::

<!-- entry: binds modifier list -->
:::layer beginner
Lists modifiers (shift buttons held together with a bind's button): without a selector every modifier name with the
relations that use it, with a selector those of one bind. The Binds and Modifiers pages run it when they refresh their
modifier lists.

```powershell
engine binds modifier list --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Global: `Modifiers` (names, sorted) and `Entries` (name, relations, `BindCount`).
- One bind: `Modifiers` (names) and `Reformers` (the stored form with the modifier's device and input).
- Read-only.
:::

:::layer technical
- Output: JSON with `Scope` (`global` or `bind`), or text; exit code `0`.
- Failures (exit `1`): `binds modifier list accepts at most one bind/relation selector.`, `Bind not found for
  relation: ...`.
- Related: [[cli:binds modifier add]], [[cli:binds modifier remove]], [[cli:binds modifier rename]].
:::

<!-- entry: binds modifier add -->
:::layer beginner
Adds one or more modifiers to a bind, so its button only does the relation while they are held. **Attach Selected To
Bind** on the Binds page and **Create Modifier** (with a relation picked) run it.

```powershell
engine binds modifier add "Gear Up/Down" LShift --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Modifiers the bind already has (any case) are not added twice.
- The relation needs a bind ([[cli:binds set]] first).
- The game gets the modifier combination at the next export (DCS shows it in its modifier list).
:::

:::layer technical
- Output: the bind after the change, as [[cli:binds show]] prints it; exit code `0`.
- Failures (exit `1`): `binds modifier add requires at least one modifier.`, `Bind not found for relation: ...`,
  `Unknown binds modifier option(s): ...`.
- One undo step with `--profile`. Related: [[cli:binds modifier remove]], [[cli:binds modifier list]].
:::

<!-- entry: binds modifier remove -->
:::layer beginner
Takes modifiers off a bind, or all of them with `--all`; the bind and its input stay. **Detach Selected From Bind** and
**Clear Modifiers** (`--all`) on the Binds page run it, and **Delete Selected** on the Modifiers page runs it per
relation.

```powershell
engine binds modifier remove "Gear Up/Down" --all --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- A name removes every stored form of that modifier on the bind (any device and input).
- After the next export the button works without holding the shift button; binds that only differed by the modifier
  now share one button (the Check shows it).
:::

:::layer technical
- Output: the bind after the change; exit code `0`.
- Failures (exit `1`): `binds modifier remove requires at least one modifier or --all.`, `Bind not found for relation:
  ...`.
- One undo step with `--profile`. Related: [[cli:binds modifier add]].
:::

<!-- entry: binds modifier rename -->
:::layer beginner
Gives a modifier a new name in every bind that uses it, or only in one bind when you name it. **Rename** (From / To) on
the Modifiers page runs it.

```powershell
engine binds modifier rename LAlt "Pinky Shift" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Only the name changes; the modifier's device and input stay. A bind that ends up with the same modifier twice keeps
  one.
- The new name shows in the game after the next export (DCS's modifier list).
:::

:::layer technical
- Output: `Scope`, `Relation`, `OldName`, `NewName`, `UpdatedRelations`, `UpdatedCount`; exit code `0`.
- Failures (exit `1`): `binds modifier rename requires different old/new names.`, `Modifier not found: ...`,
  `Bind not found for relation: ...`.
- One undo step with `--profile`. Related: [[cli:binds modifier list]].
:::

<!-- entry: binds tuning show -->
:::layer beginner
Shows an axis relation's curves: the base curve every game gets and each scope with its own curve (an aircraft, a game,
a game family). With `--game` (and `--plane`) it also says which curve that game uses. The axis response panel on the
Binds page runs it when it loads a scope (**Reload**).

```powershell
engine binds tuning show "Pitch" --game DCS --plane F-16C_50 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The resolved curve: the most specific stored scope (aircraft, game, family), else the base; for a game whose curve
  differs from DCS's it is fitted to the base curve; in detent mode the detent curve.
- Values are written as `inv=0;sl=0;dz=0.05;sx=1;sy=1;cv=0.2` plus the game's own parameters.
- Read-only. Axis relations only.
:::

:::layer technical
- Output: JSON `Relation`, `Scopes`, `Game`, `Plane`, `Resolved` (`Model`, `Source`, `Fitted`, `Tuning`, `Note`), or
  text; exit code `0`.
- Failures (exit `1`): `Axis tuning is only for axis relations; '...' is a button relation.`, `No relation/bind
  matched query: ...`.
- Reference: [[doc:axis-curves]]. Related: [[cli:binds tuning set]], [[cli:binds tuning clear]],
  [[cli:binds tuning preview]], [[cli:binds tuning models]].
:::

<!-- entry: binds tuning set -->
:::layer beginner
Stores an axis curve for one scope: the base (every game), a game, an aircraft of a game or a game family. **Save for
this scope** in the axis response panel on the Binds page runs it.

```powershell
engine binds tuning set "Pitch" --scope plane:DCS:F-16C_50 --deadzone 0.03 --curvature 0.2 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
| Option | Values |
|---|---|
| `--scope` (required) | `base`, `game:<game>`, `plane:<game>:<plane>`, `family:<family>` |
| `--inverted`, `--slider` | `true` / `false` |
| `--deadzone`, `--saturation-x`, `--saturation-y` | `0..1` |
| `--curvature` | one value `-1..1`, or 11 user-curve points `0..1` |
| `--param <name>=<value>` | a parameter of the game's own curve model, checked against its range ([[cli:binds tuning models]]) |
| `--curve-points x:y,...` \| `none` | a curve with free stops in `0..1`, x increasing, at least two points |

- A scope without values of its own starts from what it uses now (game and aircraft scopes) or from the base.
- Curve points stay until dead zone, saturation or curvature change without new points.
- The base scope writes the bind's own values: invert, slider, dead zone, saturation and curvature. `--param` and
  `--curve-points` are not stored there: the result shows only the stored values and a line "Not set: ... do not apply
  to the base scope"; put them on a game, aircraft or family scope.
- Games that keep some values once per device axis (MSFS) get a warning when another relation on the same axis has
  other values.
- The next export writes the curve into the game's axis settings for that aircraft, game or family.
:::

:::layer technical
- Output: `Relation` and `Message` (`Set <scope>: <values>`, for the base scope a `Not set: ...` line when `--param` or
  `--curve-points` were given, plus warnings); exit code `0`.
- Failures (exit `1`): `binds tuning set requires --scope (...)`, `Invalid scope '...'`, `Relation '...' has no bind;
  bind an axis first.`, `--deadzone must be 0..1.`, `Unknown model parameter '...'. See: engine binds tuning
  models.`, `--curve-points: x must increase from point to point.`
- Stored per bind in `TuningOverrides` (scope -> values). One undo step with `--profile`.
- Related: [[cli:binds tuning show]], [[cli:binds tuning clear]], [[cli:binds set]] (base values only).
:::

<!-- entry: binds tuning clear -->
:::layer beginner
Removes a scope's own curve, so that game, aircraft or family uses the next wider curve again (in the end the base).
**Inherit (clear)** in the axis response panel runs it.

```powershell
engine binds tuning clear "Pitch" --scope plane:DCS:F-16C_50 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Scopes: `game:<game>`, `plane:<game>:<plane>`, `family:<family>`. The base cannot be cleared; set its values with
  [[cli:binds tuning set]].
- A scope without own values is no error (`No tuning for <scope>.`).
- The next export writes the inherited curve into the game.
:::

:::layer technical
- Output: `Relation` and `Message` (`Cleared <scope>.`); exit code `0`.
- Failures (exit `1`): `The base tuning cannot be cleared; set its values instead.`, `Invalid scope '...'`,
  `Relation '...' has no bind; bind an axis first.`
- One undo step with `--profile`. Related: [[cli:binds tuning show]].
:::

<!-- entry: binds tuning models -->
:::layer beginner
Lists the axis response models JoyPro knows (how a game turns your stick position into what it uses) with their
parameters. The parameter keys are what `binds tuning set --param` takes.

```powershell
engine binds tuning models --text
```
:::

:::layer advanced
- `dcs`: dead zone, saturation X/Y, curvature (exponential) or an 11-point user curve, slider, invert. Also `il2`,
  `il2korea`, and `generic` for games without a model of their own (invert, dead zone, saturation).
- The games' own models: Arma 2 / Arma 2 OA, Arma 3, Arma Reforger, Elite Dangerous, Falcon BMS, FSX / Prepar3D,
  IL-2 1946, IL-2 Cliffs of Dover (also Blitz), MSFS 2020 / 2024, Project Wingman, Star Citizen, VTOL VR, Wardogs,
  War Thunder, X-Plane 11 / 12.
- Per parameter: key, label, kind (number or switch) and the range of a number. A model whose game takes a curve with
  free stops also shows the `--curve-points` line.
- Read-only; no profile needed. With `binds tuning` alone it lists the models too.
:::

:::layer technical
- Output: JSON `Models` (`Id`, `Title`, `Description`, `CurvePoints`, `Parameters` with `Key`, `Label`, `Kind`, `Min`,
  `Max`, `Step`, `Default`), or text; exit code `0`.
- Reference: [[doc:axis-curves]]. Related: [[cli:binds tuning set]], [[cli:binds tuning preview]].
:::

<!-- entry: binds tuning preview -->
:::layer beginner
Prints the response curve a game (and an aircraft) applies to an axis relation: each stick position from -1 to 1 and
what the game makes of it. Use it to compare scopes before an export.

```powershell
engine binds tuning preview "Pitch" --game DCS --plane F-16C_50 --points 11 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--game` (required) and `--plane`: the curve [[cli:binds tuning show]] resolves for them (the aircraft, game or family
  scope, else the base; fitted to the base curve for a game with another model; the detent curve in detent mode).
- `--points <n>`: 2..1001 samples spread evenly from -1 to 1 (default 21).
- Read-only. Axis relations only.
:::

:::layer technical
- Output: JSON `Relation`, `Game`, `Plane`, `Model`, `Source`, `Fitted`, `Tuning`, `Points` (`[x, y]` pairs), or text
  (`Model: ...`, `Tuning: ...`, then lines like `+0.500 -> +0.353553`); exit code `0`.
- Failures (exit `1`): `binds tuning preview requires --game.`, `--points must be 2..1001.`, `Axis tuning is only for
  axis relations; '...' is a button relation.`, `binds tuning preview requires a relation or bind name.`
- Reference: [[doc:axis-curves]]. Related: [[cli:binds tuning models]].
:::

<!-- entry: binds detent show -->
:::layer beginner
Shows a throttle relation's detent mode: on or off, the device axis calibration, the aircraft grouped by where their
afterburner starts, and per game the curve it gets. **Reload** in the Detent mode panel on the Binds page runs it.

```powershell
engine binds detent show "Throttle" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Settings: on/off, aircraft without afterburner (`full power at the detent` or linear), unknown aircraft, margin.
- Per aircraft: the afterburner start and where it comes from (JoyPro's gate data, your own value), its confidence;
  per game: the fit method, its error and the curve.
- Warnings: no device axis bound, the axis not calibrated, the relation not a one-sided axis.
- Read-only. Axis relations only.
:::

:::layer technical
- Output: JSON (`Settings`, `Calibration`, `GatesFile`, `Groups`, `Warnings`), or text; exit code `0`.
- Failures (exit `1`): `Detent mode is only for axis relations; '...' is a button relation.`
- Reference: [[doc:axis-curves]]. Related: [[cli:binds detent set]], [[cli:binds detent calibrate]],
  [[cli:binds detent gates]], [[cli:binds detent merge]].
:::

<!-- entry: binds detent set -->
:::layer beginner
Switches detent mode on or off for a throttle relation and sets its options. In detent mode one throttle relation serves
every aircraft: the curve is bent per aircraft so your device's detent is where that aircraft's afterburner starts. The
**Detent mode on** tick, the second tick (aircraft without afterburner) and a group's **Set** / **Game data** run it.

```powershell
engine binds detent set "Throttle" --on --no-ab full --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--on` / `--off` (not both).
- `--no-ab full|linear`: aircraft without afterburner get full power at the detent, or the relation's curve.
- `--unknown full|linear`: the same for aircraft without gate data.
- `--margin <0..0.1>`: share of the game's throttle.
- `--target <game>:<plane>=ab:<0..1>[/idle:<0..1>]|full|linear|default` (repeatable): your own afterburner start
  for an aircraft; `default` goes back to the gate data.
- It needs a bound device axis. Without a calibration it says so: it takes effect after
  [[cli:binds detent calibrate]].
:::

:::layer technical
- Output: the detent view of [[cli:binds detent show]] with a `Message`; exit code `0`.
- Failures (exit `1`): `Relation '...' has no device axis bound; bind the throttle axis first.`, `Use --on or --off,
  not both.`, `--margin must be 0..0.1 (share of the game's throttle).`, `--target takes <game>:<plane>=...`
- Stored on the bind. One undo step with `--profile`.
:::

<!-- entry: binds detent calibrate -->
:::layer beginner
Stores where your throttle axis is at idle and at the afterburner detent (and optionally at full and behind idle), as
raw axis positions. **Save calibration** at the end of the **Calibrate detent...** wizard runs it.

```powershell
engine binds detent calibrate "Throttle" --idle 0.02 --detent 0.78 --full 0.99 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--idle`, `--detent` (required), `--full`, `--cutoff` (`0..1` or `none`): raw positions of the bound device axis.
- `--source` (default `manual`; the GUI passes `wizard`) and the date are stored with it.
- Checks: the detent at least 2 % of the travel away from idle, full beyond the detent, the cut-off behind idle.
- Stored per device axis, so every throttle relation on that axis uses it.
:::

:::layer technical
- Output: the detent view with `Calibrated <device axis>: idle at ..., detent at ... of the travel.`; exit code `0`.
- Failures (exit `1`): `--idle must be the raw axis position 0..1 at idle.`, `--detent must be ...`, `The detent must
  be at least 2 % of the travel away from idle.`, `Full must be beyond the detent.`, `The cut-off must be behind
  idle.`
- One undo step with `--profile`. Related: [[cli:binds detent uncalibrate]], [[cli:binds detent set]].
:::

<!-- entry: binds detent uncalibrate -->
:::layer beginner
Removes the stored detent calibration of the throttle axis a relation is bound to. Detent mode then has nothing to bend
the curve to until you calibrate the axis again.

```powershell
engine binds detent uncalibrate "Throttle" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The calibration belongs to the device axis, not to the relation: every throttle relation on that axis loses it.
- Detent mode itself, its options and your per-aircraft targets stay.
- An axis without a calibration is no error (`No calibration stored.`).
- It needs a bound device axis. Axis relations only.
:::

:::layer technical
- Output: the detent view of [[cli:binds detent show]] with `Calibration of <device axis> removed.`; exit code `0`.
- Failures (exit `1`): `Relation '...' has no device axis bound; bind the throttle axis first.`, `Detent mode is only
  for axis relations; '...' is a button relation.`, `Unknown binds detent uncalibrate argument(s): ...`
- One undo step with `--profile`. Related: [[cli:binds detent calibrate]].
:::

<!-- entry: binds detent gates -->
:::layer beginner
Lists JoyPro's throttle gate data: per aircraft where its afterburner starts (and where idle is) on the game's throttle
axis, with source and confidence. Detent mode uses it for every aircraft you did not set yourself.

```powershell
engine binds detent gates --game DCS --text
```
:::

:::layer advanced
- `--game <id>`: only that game (its id, e.g. `DCS`). `--plane <name>`: only that aircraft (its id or one of its
  aliases). Without them: every aircraft of the data.
- Per aircraft: `no afterburner`, `afterburner start unknown` or `afterburner at <n> %`, the idle gate when known, the
  confidence and the source.
- Read-only; no profile needed. Alias: `binds detent data`.
:::

:::layer technical
- Output: JSON `File`, `Aircraft` (`Game`, `Plane`, `Title`, `NoAfterburner`, `Afterburner`, `IdleGate`, `Confidence`,
  `Source`, `Notes`, `GameOption`), or text `Throttle gates (<file>): <n>` with a line per aircraft; exit code `0`.
- Failures (exit `1`): `Unknown binds detent gates argument(s): ...`
- The data: `DB\Detents\throttle-gates.json`. Related: [[cli:binds detent set]] (`--target` overrides an aircraft),
  [[cli:binds detent show]].
:::

<!-- entry: binds detent merge -->
:::layer beginner
Joins your old per-aircraft throttle relations (one per afterburner position) into one relation in detent mode.
Without `--apply` it only shows the plan. **Merge my throttle relations...** shows the plan, **Merge** applies it.

```powershell
engine binds detent merge --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Without `--relations`: every device axis with two or more bound throttle relations (by name, or DCS thrust axes).
- `--relations a,b,...` (at least two, one device axis) and `--into <relation>` (one of them) choose by hand; else the
  relation with most aircraft that does not give full power at the detent stays.
- The device's detent: the calibration, else read from a relation that gives full power there, else from the gate
  data. Each aircraft's afterburner start comes from its old curve (`--ab-source profile`, default) or from the gate
  data where it has a value (`--ab-source game`).
- `--apply`: the kept relation gets the other relations' aircraft and groups, detent mode on, each aircraft's own
  afterburner point; the others are removed.
:::

:::layer technical
- Output: `Applied`, `AbSource`, `Proposals` (device, sources, calibration, targets, notes, problems); exit code `0`.
- Failures (exit `1`): `Nothing to merge: no device axis has two or more throttle relations.`, `Cannot merge: ...`
  (a problem of a plan), `--into needs --relations.`, `binds detent merge needs at least two relations.`
- `--apply` saves one history version named "Detent mode: merged ..."; undo with [[cli:profile history]].
:::

<!-- entry: binds export -->
:::layer beginner
Writes the profile's binds to a JSON file: per relation the device, the input, modifiers and the axis settings. Use it
to keep a copy or to edit binds by hand. **Export Binds JSON...** in the menu of the binds list runs it.

```powershell
engine binds export "C:\Users\Pilot\Documents\JoyPro\binds.json" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
- The path is the first word; without it the global `--out` is used. A missing folder is created.
- Words after the path filter the binds: only those whose relation name, device, input, device alias or modifiers
  contain them. Such a partial file needs `--replace` on [[cli:binds import]], which then unbinds every relation the
  file has no bind for.
- With `--text` the file gets the readable text form instead of JSON; [[cli:binds import]] reads only the JSON and
  refuses the text form ("for reading only").
- The file is overwritten; the profile and the games are not touched.
:::

:::layer technical
- Output: the file, JSON `FormatVersion`, `GeneratedUtc`, `Binds` (`RelationName`, `IsAxis`, `Joystick`, `JAxis`,
  `JButton`, `Inverted`, `Slider`, `Deadzone`, `Curvature`, `SaturationX`, `SaturationY`, `AllReformers`,
  `AliasJoystick`, `DoubleTap`, `TuningOverrides`, ...); exit code `0`.
- Failures (exit `1`): `binds export requires a target file path.`
- Related: [[cli:binds import]], [[cli:relations export]] (relations with their binds).
:::

<!-- entry: binds import -->
:::layer beginner
Reads a binds file of [[cli:binds export]] into the profile. The file's binds replace all binds of the profile.
**Import Binds JSON...** in the menu of the binds list runs it after asking first ("Replace all binds?").

```powershell
engine binds import "C:\Users\Pilot\Documents\JoyPro\binds.json" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
- Each bind goes to the relation of its `RelationName`. A name the profile does not have becomes a new relation
  without game actions, holding that bind.
- Every bind that is not in the file is removed; those relations stay, unbound. A file that lacks the binds of some
  relations (a filtered export) is refused and the refusal names them; `--replace` imports it anyway and lists them
  (`Unbound (not in the file): ...`). The GUI asks first and then passes `--replace`. The modifier list is rebuilt
  from the imported binds.
- Only binds change: the relations' actions, groups and aircraft stay as they are.
- Safety: with `--profile` the import is one history version; undo with [[cli:profile history]]. The games get the
  binds at the next export.
:::

:::layer technical
- Output: always text, `Imported binds from: <path>` and the profile summary; exit code `0`.
- Failures (exit `1`): `binds import requires a source file path.`, `File not found: ...`, `<file> is not a JSON
  file. A binds export with --text is for reading only; ...`, `<file> is not a binds file of engine binds export ...`,
  the refusal of a partial file, `Unknown binds import argument(s): ...`; the profile stays as it was.
- Related: [[cli:binds export]], [[cli:relations import]].
:::

<!-- entry: devices list -->
:::layer beginner
Lists device names: the connected game controllers and the devices your profile has binds for, and which are only in
one list. **List Devices** in Settings > Devices and **Refresh Options** on the Binds page run it.

```powershell
engine devices list --connected --profile-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- No flag (or both): both lists. `--connected`: only the connected devices; `--profile-devices` (also
  `--profile-only`, `--in-profile`): only the profile's.
- The names are as JoyPro writes them into binds, with the instance GUID.
- Read-only.
:::

:::layer technical
- Output: `Connected`, `Profile`, `OnlyConnected`, `OnlyProfile`, `Warnings`; exit code `0`.
- Related: [[cli:games devices]] (VID:PID and inputs), [[cli:devices exchange]], [[cli:devices remove]].
:::

<!-- entry: devices inputs -->
:::layer beginner
Lists every input name JoyPro knows: joystick axes, buttons and hat directions, and the keyboard keys in DirectInput,
DCS and IL-2 spelling. **Input Catalog** in Settings > Devices and the input list on the Binds page use it.

```powershell
engine devices inputs --text
```
:::

:::layer advanced
- `--include-off` adds the `_OFF` form of each joystick input.
- Read-only; no profile needed.
:::

:::layer technical
- Output: `JoystickAxes`, `JoystickButtons`, `JoystickPovButtons`, `JoystickOffVariants`, `KeyboardDirectInput`,
  `KeyboardDcs`, `KeyboardIl2`, `Warnings`; exit code `0`.
- Aliases: `devices catalog`, `devices input-catalog`.
:::

<!-- entry: devices exchange -->
:::layer beginner
Moves everything of one device in the profile to another device with the same inputs: binds, modifiers and the
device's settings. Use it when a stick got a new Windows ID or you replaced it with the same model. **Exchange
Device** in Settings > Devices runs it (it asks first and keeps a copy of the profile).

```powershell
engine devices exchange "LEFT VPC Stick WarBRD" "L-VPC Stick WarBRD" --confirm false --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Old and new device: positional, or `--old` (`--from`, `--device`) and `--new` (`--to`, `--target`).
- The old device must be one the profile uses. A name that does not match exactly is resolved to the closest device;
  `--confirm true` (default) asks on the console, `--confirm false` takes it and says so on stderr.
- A new device given with its instance GUID is taken as written (also when it is not connected).
- Input names stay unchanged. Also moved: bind backups and aliases, modifiers on the device, the device's alias,
  layout file, force feedback settings and activity entries.
- For other models, input by input with a preview: [[cli:devices replace]].
:::

:::layer technical
- Output: the counts per kind and `UpdatedRelations`; exit code `0`.
- Failures (exit `1`): `devices exchange requires <old-device> <new-device> (or --old/--new).`, `No profile-referenced
  devices are available to exchange.`, `devices exchange source and target resolve to the same device: '...'.`,
  `No profile references were updated for device '...'.`
- One undo step with `--profile`. The game gets the new device at the next export.
:::

<!-- entry: devices remove -->
:::layer beginner
Removes one device from the profile: every bind on it, its modifiers and its settings. The relations stay, unbound.
**Remove Device Refs** in Settings > Devices runs it (it asks first and keeps a copy of the profile).

```powershell
engine devices remove "Throttle - HOTAS Warthog" --confirm false --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- One device per run (positional or `--device`). With its GUID only that instance matches: a second unit of the same
  model keeps its binds.
- Removed: the binds whose device it is, modifiers of that device on other binds and in the modifier list, its alias,
  layout file, force feedback settings and activity entries.
- `--confirm` as for [[cli:devices exchange]].
- After the next export the device has no binds from JoyPro.
:::

:::layer technical
- Output: the counts per kind, `UpdatedRelations`, `ModifiersWithoutBinds`; exit code `0`.
- Failures (exit `1`): `devices remove requires <device-name> or --device <name>.`, `devices remove accepts one target
  device at a time.`, `No profile references were removed for device '...'.`
- Aliases: `devices purge`, `devices clear`. One undo step with `--profile`.
:::

<!-- entry: devices kb -->
:::layer beginner
JoyPro's device catalog for Auto Assign: search it, show one model's controls, or identify your connected devices
against it. Auto Assign on the Binds page runs `identify` when it opens, `list` for **Search Catalog** and `show` for
**Layout**.

```powershell
engine devices kb list "Orion" --text
```
:::

:::layer advanced
- `list [words]`: catalog entries whose id, name or vendor contain every word (as one quoted argument or several);
  `list vkb gladiator` finds the entries with both words.
- `show <id>`: one model with its axes and controls (input, control, label).
- `identify [--grip <device>=<grip id>]... [--add-device <id>]...`: the connected devices identified against the
  catalog; `--grip` picks the grip of a base that takes several, `--add-device` adds a catalog device that is not
  connected.
- Read-only; no profile needed.
:::

:::layer technical
- Output: JSON (list: id, name, vendor, kind, hand, base, image, confidence, VID:PIDs; show: the model; identify: the
  devices), or text; exit code `0`.
- Failures (exit `1`): `devices kb show requires a device id.`, `Device '...' is not in the device catalog.`, `Unknown
  devices kb action: ...`.
- The catalog: `DB\Devices` in JoyPro's program folder. Reference: [[doc:auto-assign]]. Related:
  [[cli:relations auto-assign]].
:::

<!-- entry: devices replace -->
:::layer beginner
Moves every bind and modifier of one device to another device, input by input, also to a different model: the same
input ids for the same product, else by the device's parts (trigger to trigger, hat 2 to hat 2). The **Replace
device** window runs it with `--dry-run` for the
preview and without for **Move binds**; it opens from **Replace device...**, a device card's **Replace...** or
**Replace the selected bind's device...** in the binds list's menu.

```powershell
engine devices replace "Joystick - HOTAS Warthog" "VKB Gladiator NXT EVO" --dry-run --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Old device: one the profile has binds on (its full name, or the name without GUID when only one device has it).
  New device: a connected device, a device of the profile, or any name with an instance GUID. Also `--old` / `--from`
  and `--new` / `--to`.
- `--mode auto` (default): `same` when it is the same product (same name, USB id or catalog model), else `layout` (the
  parts of the device catalog). `same` or `layout` forces one (GUI: **Same input ids (1:1)**, **By layout (device
  catalog)**).
- `--map OLD=NEW` (repeatable): your choice for one input; `NEW = none` leaves it on the old device. An axis goes only to
  an axis, a button only to a button or hat direction.
- `--grip <device or VID:PID>=<grip id>` (repeatable): which grip a base carries.
- `--merge` when the new device already has binds: `add` keeps both, `keep` lets the new device's binds win (the old
  bind stays on the old device), `replace` lets the old device's binds win (the new device's binds on those inputs are
  removed); the word may be written in any case (`ADD` = `add`). A real run with such overlaps and no `--merge` is
  refused.
- Inputs that do not fit stay on the old device. When nothing is left on it, its alias, force feedback settings and
  activity move along (its picture and USB id only for the same product).
- `--dry-run` shows the plan and saves nothing.
:::

:::layer technical
- Output: JSON plan (`OldDevice`, `NewDevice`, `Mode`, `ModeReason`, `OldModel`, `NewModel`, connected flags, bind
  counts, `NeedsMergeChoice`, `Mappings` with old and new input, part, method, relations and modifiers, `NewInputs`,
  `Steps`, `Warnings`), or text `Device Replace (preview)` / `(applied)`; exit code `0`.
- Failures (exit `1`): `The profile has no binds on '...'. Devices with binds: ...`, `'...' is neither connected nor in
  the profile. Connected: ...`, `The old and the new device are the same device: ...`, `... already has <n> bind(s).
  Choose how to merge: ...`, `No input of ... fits ...; nothing was moved. Map inputs yourself with --map OLD=NEW.`,
  `--mode must be auto, same or layout.`, `--merge must be add, keep or replace.`
- One undo step with `--profile` (not for a dry run). Reference: [[doc:devices]]. Related: [[cli:devices exchange]]
  (same inputs, everything at once), [[cli:devices auto-remap]].
:::

<!-- entry: devices auto-remap -->
:::layer beginner
Gives a device that came back under a new Windows ID (new PC, Windows reinstalled, other USB port) its binds again: a
profile device that is not connected gets the connected device of the same product. JoyPro runs it after opening a
profile when **Remap Missing Devices** is on, and shows what moved with Undo.

```powershell
engine devices auto-remap --dry-run --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- A pair: the same USB id JoyPro remembered for the old device, or the same name (and the same USB id when one is
  known); the connected device must not be used in the profile yet.
- Several connected devices of one kind: the only one that has every input the profile uses; else the device is
  skipped with the candidates (pick one with [[cli:devices replace]]). Two old devices for one connected device are
  skipped too.
- A pair is moved like [[cli:devices exchange]] (binds, modifiers, the device's settings), and the new device's USB id
  is remembered.
- `--dry-run` lists the pairs and changes nothing. Alias: `devices remap`.
:::

:::layer technical
- Output: JSON `DryRun`, `Applied`, `Remapped` (`OldDevice`, `NewDevice`, `Reason`, `Binds`, `Modifiers`), `Skipped`
  (`OldDevice`, `Reason`, `Candidates`), `Missing`, `Warnings`, or text `Device Auto Remap`; exit code `0`.
- Failures (exit `1`): `Unknown devices auto-remap argument(s): ...`
- The profile is saved (one undo step) only when something moved.
:::

<!-- entry: devices alias list -->
:::layer beginner
A name of your own for a device ("Left throttle"): list, set or clear it. It is for display only; exports always use
the real device. **Name it** beside the box "Your name for this device" runs `set`, the clear button next to it
`clear`.

```powershell
engine devices alias set "LEFT VPC Throttle" "Left throttle" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `list` (default): every device with an alias. A stored name that is only the device's own name, or the name of a
  layout template an older JoyPro kept there, is no alias and left out.
- `set <device> <alias>` (or `--device`, `--alias`; the remaining words form the alias): one line, at most 60
  characters. `clear <device>` (also `remove`) takes it back.
- The device: a full name with instance GUID, a device of the profile, or the connected device a name resolves to. A
  name without GUID finds the alias of the only device of that name.
- A device that comes back under a new ID keeps its alias: [[cli:devices auto-remap]], [[cli:devices exchange]] and
  [[cli:devices replace]] move it.
:::

:::layer technical
- Output: JSON `Aliases` (`Device`, `Alias`), or for `set` / `clear` `Action`, `Device`, `Alias`, `PreviousAlias`,
  `Changed`; text `Device alias: <device> = <alias>`; exit code `0`.
- Failures (exit `1`): `devices alias set requires <device> <alias>.`, `devices alias set requires an alias (devices
  alias clear <device> removes one).`, `Unknown devices alias action: ... (list, set, clear).`
- Stored in the profile; `set` and `clear` are one undo step (none when nothing changed). Alias: `devices aliases`.
:::

<!-- entry: devices reference -->
:::layer beginner
JoyPro's device reference catalog: search its models, identify a device from its USB id and name, or draw JoyPro's
schematic of a model with numbered markers where its parts are.

```powershell
engine devices reference identify --vidpid 044F:0402 --name "Joystick - HOTAS Warthog" --text
```
:::

:::layer advanced
- `list [words]` (default): the models with kind, shape, known inputs, VID:PIDs and origin (`catalog`, or `user` for
  your own definitions).
- `identify --vidpid <VID:PID> --name <HID name> [--instance <name>] [--buttons <n>] [--hats <n>] [--axes X,Y,RZ]`:
  the matching model, method and reason, its base, the grips to choose from, and, when it is not sure, the question to
  ask with ranked candidates.
- `show <id> [--base <id>] [--svg <file>] [--light] [--no-badges]`: the schematic's markers (number, name, type,
  region, position, inputs); `--svg` writes it (dark unless `--light`).
- `photos [words] [--coverage]`: the reference photos per model and the curated position sets; `--coverage` only the
  numbers.
- `drawings [words]`: JoyPro's line drawings and how many curated positions each carries; `drawings --model <id>
  [--view <v>] --svg <file> | --png <file> [--light] [--drawing-style lines|cel] [--drawing-preset <p>]` draws one.
- `completeness [words] [--missing] [--list <file>]`: whether the best shipped picture of a model places every input.
- `detect <model> [--photo <file|view|sha>] [--photos <folder>] [--max <n>] [--eval]` (experimental): where the
  buttons, hats, switches and knobs of a device photo probably are, each with a score; no input is assigned.
- Checks of the catalog for its maintainers: `check [words]`, `layout-check`, `drawings --check`. `photo-add` adds a
  photo reference (an address on the maker's hosts or an allowed site) to `DB\DevicePhotos`; `mesh-render` renders a
  game's stick or throttle grip into buffers a drawing is made from.
- Files are written only by `--svg`, `--png`, `photo-add` and `mesh-render`. The profile is not used.
:::

:::layer technical
- Output: JSON or text per action; exit code `0`, `1` when `check`, `layout-check` or `drawings --check` find
  problems.
- Failures (exit `1`): `devices reference identify needs --vidpid and/or --name.`, `devices reference show needs one
  model id.`, `Device '...' is not in the device catalog.`, `JoyPro has no line drawing of '...'.`, `Unknown devices
  reference action: ...`
- The catalog: `DB\Devices` in JoyPro's program folder plus your own definitions in `%LOCALAPPDATA%\JoyPro2\Devices`.
  Alias: `devices ref`. Reference: [[doc:device-reference]]. Related: [[cli:devices kb]], [[cli:layout identify]],
  [[cli:layout callouts]].
:::

<!-- entry: layout render -->
:::layer beginner
Draws the cheat sheets: a picture of each device with every bind written at its button, as PNG files, one overview per
device and one per aircraft. **Create cheat sheets** on the Layout page (Cheat sheets) runs it.

```powershell
engine layout render --path "C:\Users\Pilot\Documents\JoyPro\Cheat sheets" --game DCS --plane F-16C_50 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Each device needs a layout: the profile's layout file for it ([[cli:layout image]], [[cli:layout suggest]]) or
  `--layout-map <device>=<file>`. A JoyPro 1 `.layout`, a device layout `.json` with tables and lines, or a page with
  several views. `--skip-missing-layouts true` (default) skips a device without one with a warning; `false` fails.
- `--device(s)`, `--game(s)`, `--plane(s)` limit what is drawn.
- `--include-overview` and `--include-planes` (both default `true`; not both `false`).
- Look: `--draw-markers`, `--draw-legend-table` (default `false`), `--marker-size` (10), `--legend-width` (460),
  `--legend-max-rows` (20), `--legend-font-size` (11), symbols (default circle, diamond, triangle, bar),
  `--sheet-style` and the colour and font options, `--style dark|light`, `--print-width` (default 3508, at most 12000),
  `--combine true` (one `All devices.png` per folder), `--drawing-*` (default: the GUI's drawing settings).
- Writes only pictures; the profile and the games are not touched.
:::

:::layer technical
- Files: `<path>\<device>.png` (overview) and `<path>\<game>\<plane>\<device>.png`. `--path` default: the profile's
  cheat sheet folder, else `LayoutExport`.
- Output: the render result (rendered devices, written files, warnings), or text; exit code `0`, `1` on
  `layout render failed: ...` (for example `layout render produced no images. Ensure joystick layout mappings exist
  ...`).
- Related: [[cli:layout titles]], [[cli:layout multiview]], [[cli:layout callouts]].
:::

<!-- entry: layout titles -->
:::layer beginner
Lists, for every game and aircraft a device's binds are active in, the game's own menu title of each bound input, and
the device's main modifier. The Layout page reads it for the preview box of **Tables** (one game and aircraft with the
game's menu titles).

```powershell
engine layout titles --device "Joystick - HOTAS Warthog" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--device` (required): the device name as in the binds.
- Inputs with a modifier are listed as `Modifier+JOY_BTN5`.
- Read-only; needs the profile.
:::

:::layer technical
- Output: JSON `Device`, `Modifier`, `Targets` (game, plane, titles), or text; exit code `0`.
- Failures (exit `1`): `layout titles needs --device <JoyPro device name>.`
:::

<!-- entry: layout image -->
:::layer beginner
Shows or changes the layout file the profile uses for a device (your own layout, a JoyPro 1 layout, a picture you
chose). **Reset to JoyPro's default...** on the Layout page clears it, **Undo the reset** puts it back.

```powershell
engine layout image "Joystick - HOTAS Warthog" --clear --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- No option: prints the mapping. `--clear`: forgets it, JoyPro's picture shows again. `--set <file>`: sets it.
- The device matches with or without its GUID.
- Only the profile changes.
:::

:::layer technical
- Output: `Device`, `Previous`, `Current`, or text `<device>: <file> (was: ...)`; exit code `0`.
- Failures (exit `1`): the usage `layout image <device> [--set <layout file>|--clear]`.
- One undo step with `--profile` when it changed something. Related: [[cli:layout render]].
:::

<!-- entry: layout multiview -->
:::layer beginner
Draws a page with several views of one device (front and side): each view as its own picture and the whole page as one
picture. The Layout page runs it to draw the views of **Several views**.

```powershell
engine layout multiview "C:\Users\Pilot\Documents\JoyPro\Warthog views.json" --png "C:\Users\Pilot\Documents\JoyPro\Warthog.png" --device "Joystick - HOTAS Warthog" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
- `--views <folder>`: each view as `view-<n>.png`. `--png <file>`: the page.
- `--device`: whose binds are written; `--game` with `--plane`: one aircraft (else relation names).
- `--light`, `--print-width` (default 3508), the sheet style options; a view's own style wins.
- Writes only pictures.
:::

:::layer technical
- Output: always JSON: `Model`, `Page`, `Width`, `Height`, `Views` (index, file, size), `Png`, `Warnings`; exit code `0`.
- Failures (exit `1`): `Not a page with several views: ...`, the usage line.
- Related: [[cli:layout render]] (draws such a page as the device's cheat sheet).
:::

<!-- entry: layout suggest -->
:::layer beginner
Finds a picture with button positions for each device: a fitting layout template, else JoyPro's drawing of the model,
else a generated layout. With `--apply true` it stores the choice in the profile for the cheat sheets. **Suggest** and
**Find my devices** on the Layout page run it, **Use picture** with your own picture (`--backdrop`).

```powershell
engine layout suggest --connected --apply true --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Devices: `--device(s)`; else `--connected` (the connected game controllers); `--profile-devices` adds the profile's.
  None: it fails.
- Templates: the device layout catalog (`--catalog`) and your layout folders (`--legacy-layout-dir`,
  `--legacy-output-dir`; found by themselves when not given). A template only fits the same product (VID:PID, a known
  alias or the same model words of the same vendor). Below a score of 45 it uses a picture of the product or a
  generated layout instead.
- `--backdrop <file or image URL>`: your picture of the device with markers from its real inputs (one device only).
- `--apply true` (default `false`) stores layouts with a score from `--apply-min-score` (default 55).
:::

:::layer technical
- Output: the suggestions (device, layout file, score, vendor, model, VID:PID, search link), counts, warnings; exit code
  `0`, `1` on `layout suggest failed: ...` (`No devices available to suggest layouts for. ...`, `--backdrop needs
  exactly one device (--device <name>).`).
- Generated layouts go into JoyPro's `AutoLayouts` folder. One undo step with `--profile` when `--apply` stored one.
- Related: [[cli:layout identify]], [[cli:layout image]], [[cli:layout render]].
:::

<!-- entry: layout identify -->
:::layer beginner
Says which product a device is (vendor, model, kind) from its USB VID:PID and HID name, with a picture search link.
**Search a picture** on the Layout page runs it when the suggestion has no link yet.

```powershell
engine layout identify --connected --text
```
:::

:::layer advanced
- `--device(s)`: these devices; without any, or with `--connected`, every connected game controller.
- Read-only; no profile needed.
:::

:::layer technical
- Output: `Devices` (identities) and `Warnings`, or text; exit code `0`.
- Related: [[cli:devices reference]] (the device catalog's identification), [[cli:layout suggest]].
:::

<!-- entry: layout template-inspect -->
:::layer beginner
Reads a layout file (JoyPro 1 `.layout`) or a picture for the layout-file editor: size, font, colour and every marker
with its position, plus its tables and lines. The Layout page runs it when it opens a layout file in the editor.

```powershell
engine layout template-inspect --source "C:\Users\Pilot\Documents\JoyPro\Layouts\Warthog.layout" --text
```
:::

:::layer advanced
- `--source` (also `--template`, or the first word): the file.
- `--max-markers` (default 2000; the GUI passes 10000; at most 20000, `0` or less means 5000) limits the marker list;
  more gives a warning.
- Tables and lines come from `<file>.tables.json` and `<file>.connectors.json` beside it.
- Does not change the profile.
:::

:::layer technical
- Output: source, size, device name, font, text colour, preview picture path, `Markers` (token, type, modifier, x, y),
  `Tables`, `Connectors`; exit code `0`.
- Failures (exit `1`): `layout template-inspect requires --source <path.layout-or-image>.`, `layout
  template-inspect failed: Layout source not found: ...`.
- Related: [[cli:layout template-save]].
:::

<!-- entry: layout template-save -->
:::layer beginner
Saves a layout file from a source layout or picture with the markers, tables and lines you give it. The layout-file
editor on the Layout page runs it for **Save**, and before **Create cheat sheets** when the editor has a file open.

```powershell
engine layout template-save --source "C:\Users\Pilot\Pictures\warthog.png" --out "C:\Users\Pilot\Documents\JoyPro\Layouts\Warthog.layout" --markers-json "C:\Users\Pilot\Documents\JoyPro\markers.json"
```
:::

:::layer advanced
- `--source` (also `--template`, or the first word) and `--out` (also `--output`, `--path`, the global `--out`, or the
  second word); `.layout` is added when missing.
- Markers, tables, lines: `--markers-json <file>` or `--markers <json>`, `--tables-json` / `--tables`,
  `--connectors-json` / `--connectors`.
- `--replace-all true` (default): the file gets exactly the given markers; `false`: the source's markers stay, given
  ones are added or moved.
- Writes the `.layout` plus `<out>.tables.json` and `<out>.connectors.json`; the profile is not touched.
:::

:::layer technical
- Output: source, output path, marker, table and connector counts, warnings; exit code `0`.
- Failures (exit `1`): `layout template-save requires --source ...`, `... requires --out <path.layout>.`, `markers
  json file not found: ...`, `layout template-save invalid markers payload: ...`, `layout template-save failed: ...`.
- Related: [[cli:layout template-inspect]], [[cli:layout render]].
:::

<!-- entry: layout callouts -->
:::layer beginner
Arranges the tables around a device picture with right-angled leader lines, for a model of the device catalog or a
saved device layout, checks the sheet, and can draw it as a PNG with your binds in the tables.

```powershell
engine layout callouts --model thrustmaster-warthog-stick --device "Joystick - HOTAS Warthog" --game DCS --plane F-16C_50 --png "C:\Users\Pilot\Documents\JoyPro\Warthog.png" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Source: `--model <catalog id>` (or the first word) takes JoyPro's line drawing of the model with its positions and
  its shipped sheet when there is one, else (or with `--schematic`) JoyPro's schematic drawing; `--picture <file>` puts
  your own picture behind it (without positions). `--layout <device layout .json>` takes a saved layout with its
  tables.
- `--arrange` arranges the tables anew even when the layout has some.
- Table text: `--device <name>` writes that device's binds (relation names; with `--game` and `--plane`, which go
  together, that aircraft's binds); `--sample` fills sample text; without either the tables stay empty.
- `--png <file>` draws the sheet: `--light`, `--print-width` (default 3508, at most 12000), `--sheet-style` and the font
  and colour options as for [[cli:layout render]] (a layout's own style wins), `--drawing-style lines|cel`,
  `--drawing-preset`, `--drawing-ink`, `--drawing-paper`, `--drawing-accent` (default: the GUI's drawing settings).
- `--save <file.json>` writes the layout with the arranged sheet.
- `--check-all [words]` arranges and checks the sheet of every catalog model with inputs.
- Writes only the files you name; the profile and the games are not touched.
:::

:::layer technical
- Output: JSON `Model`, `Check`, `Warnings`, `Sheet`, or text `Layout Callouts: <model> | picture ... | sheet ...`,
  `Check: ...`, `Result: ok|problems`, a line per table, problems and warnings. Exit code `0`, or `1` when the check finds
  problems (the picture and the file are written all the same).
- `--check-all`: exit code `1` when a sheet has overlapping tables, slanted lines, something outside the canvas, a line
  through a table or a placed part without a line.
- Failures (exit `1`): `layout callouts needs --model <catalog id> or --layout <device layout .json>.`, `layout callouts
  needs --game and --plane together.`, `Unknown device model: ...`, `Device layout not found: ...`, `layout callouts
  failed: ...`
- Reference: [[doc:device-reference]]. Related: [[cli:layout render]], [[cli:devices reference]].
:::

<!-- entry: ffb models -->
:::layer beginner
Lists the games whose force feedback and other game-wide control settings JoyPro manages, with every setting, its
range and default. **Reload** in Settings > Force feedback settings of the games runs it to build the rows.

```powershell
engine ffb models --text
```
:::

:::layer advanced
- Per game: title, description, settings (key, label, on/off or range, default, help).
- A setting marked game-wide is set with `--device *` ([[cli:ffb set]]); the others per device.
- Read-only; no profile needed.
:::

:::layer technical
- Output: `Models` as JSON, or text; exit code `0`.
- Related: [[cli:ffb show]], [[cli:ffb set]], [[cli:ffb clear]].
:::

<!-- entry: ffb show -->
:::layer beginner
Lists the force feedback settings stored in the profile; with `--device` and `--game` (and `--plane`) also what that
device gets in that game. **Reload** in Settings > Force feedback settings of the games runs it.

```powershell
engine ffb show --device "VPforce Rhino FFB Joystick" --game DCS --plane F-16C_50 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--device` filters the stored entries (key `<device>|<scope>`).
- The resolved values: "(game file values)" when nothing is stored, so the game keeps its own.
- Read-only.
:::

:::layer technical
- Output: `Entries`, `Game`, `Plane`, `Resolved`, or text; exit code `0`.
- Related: [[cli:ffb set]], [[cli:ffb models]].
:::

<!-- entry: ffb set -->
:::layer beginner
Stores force feedback settings of a game for a device (or game-wide), optionally for one aircraft. The next export
writes them into the game. **Apply** in Settings > Force feedback settings of the games runs it.

```powershell
engine ffb set --device * --game IL2Game --param force=0.8 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--device <name>` or `*` (game-wide settings need `*`, per-device settings a device name) and `--game` are
  required; `--plane` makes it an aircraft's own value.
- `--param <name>=<value>` (repeatable): checked against the game's settings and ranges; on/off settings take
  `true`/`false` or `1`/`0`.
- Values you do not store stay as the game has them.
:::

:::layer technical
- Output: `Key`, `Values`, `Message` (`Set <key>: ...`), or `Force feedback: ...`; exit code `0`.
- Failures (exit `1`): `ffb set requires --device <device or *> and --game <game>.`, `JoyPro manages no force feedback
  settings for '...'. See: engine ffb models.`, `ffb set needs at least one --param <name>=<value>.`, `Unknown ...
  force feedback setting '...'`, `... is a game-wide setting: use --device *.`
- Stored in the profile; one undo step with `--profile`. Related: [[cli:ffb clear]], [[cli:dcs ffb set]] (DCS's swap
  and invert per device).
:::

<!-- entry: ffb clear -->
:::layer beginner
Removes stored force feedback settings: a whole scope, or single settings with `--param`. The game keeps its own
values for them at the next export. **Clear** and a row's **Unset** in Settings > Force feedback settings of the games
run it.

```powershell
engine ffb clear --device "VPforce Rhino FFB Joystick" --game DCS --plane F-16C_50 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--device <name>|*` and `--game` required, `--plane` for an aircraft scope.
- Without `--param`: every value of that scope; `--param <name>` (repeatable): only those.
:::

:::layer technical
- Output: `Key`, `Values`, `Message` (`Cleared <key>.` when nothing is left); exit code `0`.
- Failures (exit `1`): as [[cli:ffb set]].
- One undo step with `--profile`.
:::

<!-- entry: ffb telemetry status -->
:::layer beginner
Shows JoyPro's own telemetry force feedback: its settings, per game whether the telemetry export is set up and the game
runs, the JoyPro.Ffb process and its last status. **Reload settings** on the Force Feedback page runs it.

```powershell
engine ffb telemetry status --text
```
:::

:::layer advanced
- Games: DCS, IL-2 Great Battles, IL-2 Korea, War Thunder; each with its endpoint (UDP port, War Thunder's HTTP
  address) and whether setup is needed.
- Read-only; no profile needed.
:::

:::layer technical
- Output: `ConfigPath`, `Config`, `Games`, `Effects`, `Process`, `LastStatus`, `Warnings`, or text; exit code `0`.
- Reads `%LOCALAPPDATA%\JoyPro2\Ffb\Telemetry\telemetry-ffb.json` and `status.json` there.
- Reference: [[doc:ffb-telemetry]]. Related: [[cli:ffb telemetry setup]], [[cli:ffb telemetry config set]].
:::

<!-- entry: ffb telemetry effects -->
:::layer beginner
Lists the effect names of JoyPro's built-in telemetry force feedback graph: the names the per-game and per-aircraft
effect switches use.

```powershell
engine ffb telemetry effects --text
```
:::

:::layer advanced
- `buffet` (stall / AoA buffet), `gforce` (G-force stick weight), `gear` and `flaps` (rumble while they move),
  `speedbrake` (speed brake buffet), `runway` (runway rumble), `guns` (gun fire bursts), `hits` (hits taken), `shake`
  (cockpit shake, IL-2), `spring` (centring spring: airspeed stiffening and trim).
- Switch one on or off, or change its strength, with `ffb telemetry config set --game <game> [--plane <aircraft>]
  --effect <name>`.
- Read-only; no profile needed.
:::

:::layer technical
- Output: JSON `Effects` (`Name`, `Title`), or text `<name> - <title>` per line; exit code `0`.
- Reference: [[doc:ffb-telemetry]]. Related: [[cli:ffb telemetry config set]].
:::

<!-- entry: ffb telemetry config show -->
:::layer beginner
Shows the telemetry force feedback settings, with every value you did not set filled in with its default.

```powershell
engine ffb telemetry config show --text
```
:::

:::layer advanced
- Global: `enabled` (default off), `device`, `deviceName`, `strength` (1), `maxForce` (the hard force cap, 0.6),
  `invertX`, `invertY`, `tickRateHz` (50), `timeoutSeconds` (0.5), `centringSpring` (on), `centringStiffness` (0.3).
- `devices`: per force feedback device (instance GUID) its own directions.
- `games`: per game `enabled` (on), `port` (0 = the game's default), `graph`, `strength` (1), `url` (War Thunder,
  `http://127.0.0.1:8111/`), `pollHz` (20), `effects`, and `aircraft` with their own graph, strength and effects.
- A file that cannot be read gives a warning, and the defaults apply. Read-only; not part of the profile.
:::

:::layer technical
- Output: JSON `ConfigPath`, `Config`, `Warnings`; with `--text` the settings themselves as indented JSON; exit code
  `0`.
- The file: `%LOCALAPPDATA%\JoyPro2\Ffb\Telemetry\telemetry-ffb.json`. Related: [[cli:ffb telemetry config set]],
  [[cli:ffb telemetry config clear]].
:::

<!-- entry: ffb telemetry config set -->
:::layer beginner
Changes telemetry force feedback settings: the global ones, a game's, an aircraft's, an effect's, or a device's
directions. The Force Feedback page saves each change with it a moment after you make it and then tells a running
JoyPro.Ffb to reload.

```powershell
engine ffb telemetry config set --game DCS --effect buffet --param strength=0.7 --text
```
:::

:::layer advanced
| Scope | Options | `--param` keys |
|---|---|---|
| global | none | `enabled`, `device`, `device-name`, `strength` (0..1), `max-force` (0..1, the hard force cap), `invert-x`, `invert-y`, `tick-rate` (10..200 Hz), `timeout` (0.1..5 s), `centring-spring`, `centring-stiffness` (0..1) |
| game | `--game DCS\|IL2Game\|IL2Korea\|WarThunder` | `enabled`, `port` (0..65535), `graph`, `strength` (0..2), `url`, `poll-hz` (1..60) |
| aircraft | `--game` `--plane <aircraft>` | `graph`, `strength` (0..2) |
| effect | `--game` [`--plane`] `--effect <name>` | `enabled`, `strength` (0..2) |
| device | `--device {guid}` alone | `invert-x`, `invert-y` |

- `--param <key>=<value>` is repeatable. Switches take `true`/`false`, `1`/`0`, `on`/`off`, `yes`/`no`; a number
  outside its range is refused.
- `url` must be an http address on this PC. `graph` is a `*.jpffb.json` file name (another name gives a warning). An
  effect name outside [[cli:ffb telemetry effects]] gives a warning: it only acts on graphs that use that name.
- Game names also: `dcs`, `il2` / `il-2` / `il2gb`, `il2korea` / `korea`, `wt` / `warthunder`.
- When one value is refused nothing is written. Not part of the profile.
:::

:::layer technical
- Output: JSON `ConfigPath`, `Changes`, `Warnings`, `Config`, or text with a line `* <scope>: <field> = <value>` per
  change; exit code `0`.
- Failures (exit `1`): `config set needs at least one --param <key>=<value>.`, `--param needs <key>=<value>: ...`,
  `Unknown <scope> setting '...' (...)`, `<key> must be <min>..<max>.`, `--plane and --effect need --game.`, `--device
  cannot be combined with --game, --plane or --effect.`, `url must be an http address on this PC (e.g.
  http://127.0.0.1:8111/).`
- Writes `%LOCALAPPDATA%\JoyPro2\Ffb\Telemetry\telemetry-ffb.json`. Reference: [[doc:ffb-telemetry]]. Related:
  [[cli:ffb telemetry config show]], [[cli:ffb telemetry config clear]].
:::

<!-- entry: ffb telemetry config clear -->
:::layer beginner
Resets part of the telemetry force feedback settings to the defaults: a game, an aircraft, an effect, a device, or
everything. **Remove** next to an aircraft on the Force Feedback page runs it for that aircraft.

```powershell
engine ffb telemetry config clear --game DCS --plane F-16C_50 --text
```
:::

:::layer advanced
- No option: every setting back to the defaults. `--game`: that game. `--game --plane`: the aircraft's own graph and
  strength. `--game [--plane] --effect`: the effect back to on, strength 1.
- `--device {guid}`: removes that device's entry (the global directions apply again).
- `--plane` and `--effect` need `--game`; `--param` is refused.
- Not part of the profile.
:::

:::layer technical
- Output: `ConfigPath`, `Changes`, `Warnings`, `Config`, or text; exit code `0`.
- Failures (exit `1`): `config clear takes no --param; it resets the whole scope.`, `config clear --plane/--effect
  needs --game.`, `--device needs the device's instance guid ({...}): ...`.
- Writes `telemetry-ffb.json`. Related: [[cli:ffb telemetry config show]].
:::

<!-- entry: ffb telemetry setup -->
:::layer beginner
Makes a game send its telemetry to JoyPro, or takes that back with `--remove`. **Install**, **Remove** and **Dry run**
under Telemetry export on the Force Feedback page run it. Close the game first.

```powershell
engine ffb telemetry setup dcs --dry-run --text
```
:::

:::layer advanced
- `dcs`: writes `Scripts\Hooks\JoyProFfb.lua` in every `Saved Games\DCS*` folder (or `--path`); `Export.lua` is not
  touched. A file of that name not written by JoyPro is not replaced (and not deleted by `--remove`).
- `il2` / `il2korea`: one `addrN` line in `[KEY = telemetrydevice]` of `data\startup.cfg` (the section is made when
  missing); JoyPro remembers what it changed so `--remove` takes exactly that back.
- `warthunder`: nothing to set up.
- `--dry-run` lists the changes and writes nothing.
- Safety: refused while DCS or IL-2 runs; changed files are backed up first (undo: [[cli:backups list]]).
:::

:::layer technical
- Output: `Game`, `Action`, `DryRun`, `Installed`, `Detail`, `Files`, `Changes`, `Warnings`, `Errors`; exit code `0`,
  `1` on errors (`No DCS Saved Games folder found (use --path <Saved Games\DCS>).`, `... does not exist (start the
  game once).`, `Close <game> first: ...`).
- Reference: [[doc:ffb-telemetry]]. Related: [[cli:ffb telemetry status]].
:::

<!-- entry: vjoy status -->
:::layer beginner
Shows the virtual joystick state: vJoy and HidHide installed, the vJoy devices, HidHide's state and JoyPro's session,
each game's mode (native, vJoy, Xbox pad) and the mapping table. **Refresh** on the Virtual Joystick page's Overview
runs it.

```powershell
engine vjoy status --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Per game: mode, relations, executables, suggested mode for one-device games, input limits and warnings, functions,
  modes.
- Per vJoy device: what the table needs and what it has.
- Read-only and without administrator rights: HidHide's hidden-device list usually cannot be read then and shows as
  unknown, never as empty.
:::

:::layer technical
- Output: the status as JSON, or text; exit code `0`.
- Reference: [[doc:virtual-joystick]]. Related: [[cli:vjoy mode]], [[cli:vjoy map]], [[cli:vjoy limits]].
:::

<!-- entry: vjoy map -->
:::layer beginner
Gives every input the virtual-mode games use a fixed place on the vJoy device (or the Xbox pad) and saves this table in
the profile. **Update mapping** on the Mapping tab runs it, and **Start** / **Test** run it first.

```powershell
engine vjoy map --dry-run --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Places never move: new inputs get a free place (an axis keeps its own axis where free, buttons and hats the next
  number).
- `--reset` plans the table anew; `--prune` drops places no game uses any more; `--dry-run` saves nothing.
- Per vJoy device it says what it needs and whether it fits; a device that lacks something needs
  [[cli:vjoy configure-vjoy]].
- Only the profile changes; the games get the vJoy inputs at their next export.
:::

:::layer technical
- Output: `Message`, `VirtualGames`, `XboxGames`, `Slots`, `Added`, `Removed`, `Unused`, `Devices`, `Xbox`, `Errors`,
  `Warnings`; exit code `0`, `1` when the plan has errors (nothing is saved then).
- One undo step with `--profile`. Related: [[cli:vjoy plan]] (the same, never saved).
:::

<!-- entry: vjoy forwarder-config -->
:::layer beginner
Writes the table for JoyPro.VJoy.exe, the process that copies your inputs to the virtual joystick: the devices, the
places and the resolved virtual functions. **Start** and **Test** on the Virtual Joystick page run it.

```powershell
engine vjoy forwarder-config --out "C:\Users\Pilot\Documents\JoyPro\forwarder.json" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Only the places a vJoy game uses go into it; devices by DirectInput instance GUID.
- `--xbox-game <game>`: the Xbox pad table of that game (else the first Xbox game); it must use the Xbox pad.
- `--game <game>`: only that game's virtual functions (the GUI passes the game that started); else those of every
  virtual game.
- Changes nothing in the profile.
:::

:::layer technical
- Without `--out` the table itself is printed as JSON (also with `--text`). With `--out`: `Message`, `Path`, counts,
  `Warnings` (function problems).
- Exit code `0`, `1` when the plan has errors, the Xbox game does not use the pad or the game is unknown.
- Related: [[cli:vjoy map]], [[cli:vjoy functions list]].
:::

<!-- entry: vjoy functions add -->
:::layer beginner
Adds a virtual function to a game: axis bands, toggles, short/long press, shift layers, macros and more, running
between your devices and the virtual joystick. **Add function** on the Functions tab runs it with the game and type.

```powershell
engine vjoy functions add --game DCS --type bands --input "in=VPC Throttle|JOY_Z" --count 3 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--type` ([[cli:vjoy functions types]]) or `--spec <json>|@<file>` (a whole function); `--name`.
- `--input role=<device>|<input>` (repeatable; `role=fn:<id>|<output>` reads another function's output; `role=`
  clears). `--output role=<target>[@<vJoy device>][:hold|enter|leave|both]`.
- `--param key=value`, `--edges 0.1,0.9`, `--count 1..32`, `--tuning`, `--keyboard on|off`, `--modes a,b` (none: every
  mode), `--switch-modes`.
- Outputs left empty get free vJoy buttons, axes or hats (or Xbox controls), reserved in the table; `--no-auto-outputs`
  leaves them empty. Loops between functions are refused. `--dry-run` saves nothing.
- The function works only while the game is in virtual mode.
:::

:::layer technical
- Output: `Message`, `Item` (outputs, problems, warnings), `Notes`; exit code `0`, `1` with the reason (`functions add
  needs --type <type> ... or --spec <json|@file>.`, `Unknown function type: ...`, `--count takes 1..32.`, a loop).
- Ids are `f1`, `f2` ...; stored in the profile. Related: [[cli:vjoy functions set]], [[cli:vjoy functions test]].
:::

<!-- entry: vjoy functions set -->
:::layer beginner
Changes a virtual function. **Save** in the function editor runs it with the whole function (`--spec`), **On / off**
with `--enabled`, and wiring an input to another function in the graph with `--input`.

```powershell
engine vjoy functions set --game DCS --id f1 --enabled off --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--id` required; the same options as [[cli:vjoy functions add]]; `--spec` replaces the function, its id stays.
- `--enabled on|off`; `--auto-outputs` fills empty outputs with free places.
- Loops are refused and nothing is saved. A running virtual joystick takes the change after Stop and Start.
:::

:::layer technical
- Output: as `functions add`; exit code `0`, `1` with the reason (`functions set needs --id <function id>.`, `No
  function ... for ...`).
- Related: [[cli:vjoy functions list]], [[cli:vjoy functions remove]].
:::

<!-- entry: vjoy modes -->
:::layer beginner
Manages a game's modes (layers) of its virtual functions: list, add, set (rename, parent), remove, and the mode at start.
**Add mode**, **Rename**, **×** and **At start** on the Functions tab run it.

```powershell
engine vjoy modes add --game DCS --name Landing --parent Combat --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `list|add|set|remove|start --game <game>` (required) and `--name <mode>`.
- `add`: `--parent` makes it inherit the parent's functions unless it has its own on that input.
- `set`: `--parent` (empty: none; loops refused), `--rename` (renamed everywhere: children, functions, start mode).
- `remove`: children inherit from its parent; functions drop the mode.
- `start`: the mode the forwarder starts in. `--dry-run` saves nothing.
:::

:::layer technical
- Output: `Message`, `Game`, `Start`, `Modes` (name, parent, start, lineage, functions); exit code `0`, `1` with the
  reason (`--game <game> is needed.`, `The mode "..." exists already for ...`, `No mode "..." for ...`).
- Stored in the profile.
:::

<!-- entry: vjoy import-gremlin -->
:::layer beginner
Turns a Joystick Gremlin profile (`.xml`, R13 or R14) into virtual functions and modes of one game. **Import a Gremlin
profile...** on the Functions tab runs it as a dry run for the report, **Import** runs it for real.

```powershell
engine vjoy import-gremlin "C:\Users\Pilot\Documents\Gremlin\dcs.xml" --game DCS --dry-run --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The file: positional or `--file`; `--game` required.
- Devices by GUID or name; remaps, curves, keys, macros, mode changes, merged axes and containers become functions.
  The report lists what was imported, what JoyPro cannot do and what differs.
- `--replace` removes the game's functions and modes first; else they are added.
- `--vjoy-device 1..16`: the vJoy device of the outputs (default: the tunnel device, when set).
- `--dry-run` saves nothing.
:::

:::layer technical
- Output: `Message`, `Format`, `Devices`, `Modes`, `StartMode`, `Functions`, `NotImported`, `Notes`, `Warnings`; exit
  code `0`, `1` with the reason (`The Gremlin profile was not found: ...`, a loop: `... Nothing was imported.`).
- Stored in the profile. Related: [[cli:vjoy functions list]], [[cli:vjoy mode]].
:::

<!-- entry: vjoy configure-vjoy -->
:::layer beginner
Sets the vJoy devices up with the axes, buttons and hats the mapping table needs, or with vJoy's maximum (`--max`).
**Set up vJoy for the table** and **Set up vJoy for maximum** (asks first) on the Overview run it. Windows asks once for
administrator rights, and Windows must restart afterwards.

```powershell
engine vjoy configure-vjoy --max --dry-run --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- What changes: the vJoy driver's device configuration. It runs vJoy's own `vJoyConfig.exe` as `vJoyConfig <n> -f -a
  <axes> -b <buttons> -p <hats>` per device that lacks something (`--max`: 8 axes, 128 buttons, 4 hats for every device
  JoyPro uses, or `--device N`); force feedback is kept.
- Buttons and hats are never reduced. An axis a virtual-mode game must not see (Battlefield's Rx/Ry) is left out
  unless the table uses it.
- Rights: one UAC prompt for the whole run (one device: vJoyConfig itself elevated; several: one elevated engine
  helper). Declined: nothing changes.
- Windows must restart before the new setup works; JoyPro never restarts Windows. Then export the virtual-mode games
  again. There is no undo command.
- `--dry-run` lists the vJoyConfig calls and changes nothing. The profile is not changed.
:::

:::layer technical
- Output: `Message`, `DryRun`, `Maximum`, `Cancelled`, `RestartNeeded`, `Commands`, `Warnings`.
- Exit codes: `0`; `1` (vJoy not installed, plan errors, a device does not read back as wanted); `2` the prompt was
  declined (`The Windows administrator prompt was declined; vJoy was not changed.`).
- Related: [[cli:vjoy map]], [[cli:vjoy status]].
:::

<!-- entry: vjoy apply-hidhide -->
:::layer beginner
Hides the physical devices of the mapping table from the virtual-mode games only, with HidHide, so those games see just
the virtual joystick. **Start** on the Virtual Joystick page runs it when "Hide the physical devices" is on. Windows
asks once for administrator rights.

```powershell
engine vjoy apply-hidhide --dry-run --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- What changes (HidHide's global configuration): the connected physical devices the table and functions use go on
  HidHide's hidden-device list; the virtual-mode games' executables (found in the installation, set with
  [[cli:vjoy exe]], plus `--exe`) go on its program list as a block list; device hiding (cloaking) is switched on last.
  vJoy devices stay visible; the forwarder and `--trusted` programs keep seeing the devices.
- An allow list you already use stays one (then only the forwarder and JoyPro are added); your own inactive entries
  are parked for the session and put back afterwards.
- Journal first: `%LOCALAPPDATA%\JoyPro2\VirtualJoystick\hidhide-session.txt` with the state before, every change and
  the owner process (`--owner-pid`, the GUI's).
- Rights: one UAC prompt; the engine starts itself elevated for the whole step. Declined: nothing changes.
- Refused when no game is in virtual mode, or while an older session waits to be restored ([[cli:vjoy recover]]).
- `--dry-run`: the plan without administrator rights (partial when HidHide cannot be read).
- Undo: [[cli:vjoy restore-hidhide]]. vJoy's driver configuration is not touched.
:::

:::layer technical
- Output: HidHide's result (message, phase, strategy, changes) and warnings (`Not connected, not hidden: ...`).
- Exit codes: `0`; `1` refused or failed (`No game uses virtual mode ...; HidHide was not changed.`); `2` the prompt was
  declined.
- Reference: [[doc:virtual-joystick]].
:::

<!-- entry: vjoy restore-hidhide -->
:::layer beginner
Undoes exactly JoyPro's HidHide changes and puts back the setup HidHide had before. **Stop** and **Restore HidHide** on
the Virtual Joystick page run it; JoyPro also runs it when the forwarder ends by itself. Windows asks once for
administrator rights.

```powershell
engine vjoy restore-hidhide --dry-run --text
```
:::

:::layer advanced
- It reads the journal `%LOCALAPPDATA%\JoyPro2\VirtualJoystick\hidhide-session.txt` and takes back only what it lists,
  against the configuration found now (entries you added meanwhile stay): cloaking off first (when JoyPro switched it
  on), then the hidden devices and programs JoyPro added go, parked entries come back, the inverse mode is set back.
  The journal moves to `history\`.
- No journal: nothing to do (exit `0`).
- `--dry-run`: lists from the journal what it would take back, without administrator rights.
- `--force` is accepted as for [[cli:vjoy recover]], but changes nothing here: restore-hidhide restores even while the
  session's owner (the GUI) runs.
:::

:::layer technical
- Output: HidHide's result and warnings.
- Exit codes: `0`; `1` failed; `2` the prompt was declined (`The Windows administrator prompt was declined; HidHide
  was not changed.`).
- Related: [[cli:vjoy recover]] (a session whose owner is gone), [[cli:vjoy apply-hidhide]], [[cli:vjoy status]].
:::

<!-- entry: vjoy mode -->
:::layer beginner
Switches one game between your physical devices (`native`), the virtual joystick (`virtual`, vJoy) and the virtual Xbox
360 pad (`xbox`, ViGEmBus). A game's mode buttons on the Virtual Joystick page run it. Export the game again afterwards.

```powershell
engine vjoy mode DCS virtual --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `<game>`: a game id or name JoyPro knows. Mode: `native`, `virtual` (also `vjoy`) or `xbox`.
- `virtual`: every input the game's relations use (axes, buttons, hats, modifier buttons) gets a fixed place on the
  vJoy device in the mapping table ([[cli:vjoy map]]); export, import and sync of the game then work on the vJoy
  inputs.
- `xbox`: for games that read only Xbox controllers or one device. The game's gamepad layout puts actions on their pad
  controls first, the rest fills the sticks, triggers, 14 buttons and the D-pad; what does not fit is listed.
- `native` takes the game out of virtual mode; its places stay in the table until [[cli:vjoy map]] `--prune`.
- A one-device game set to another mode than the suggested one gets a warning with the suggested mode.
- `--dry-run` saves nothing. Only the profile changes: vJoy, HidHide and the game's files are untouched until Start
  and the next export.
:::

:::layer technical
- Output: the plan as for [[cli:vjoy map]], with the message `<game> now uses the virtual joystick (vJoy).` (or the
  Xbox pad, or the physical devices); exit code `0`, `1` when the plan has errors (nothing is saved then).
- Failures (exit `1`): `engine vjoy mode <game> native|virtual|xbox`, `Unknown game: ...`, `The mode is native, virtual
  (vJoy) or xbox (virtual Xbox 360 pad).`, `Unknown vjoy mode argument(s): ...`
- Stored in the profile; one undo step with `--profile`. Reference: [[doc:virtual-joystick]]. Related:
  [[cli:vjoy status]], [[cli:vjoy limits]].
:::

<!-- entry: vjoy plan -->
:::layer beginner
Shows the mapping table the virtual-mode games need and the vJoy configuration it takes, without saving anything.

```powershell
engine vjoy plan --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The same plan as [[cli:vjoy map]] with `--dry-run`: `--reset` plans the table anew, `--prune` drops places no game
  uses any more.
- Per vJoy device: its places and function outputs, the axes, buttons and hats they need, what the device has, and
  `fits` or the `vJoyConfig` arguments it would need ([[cli:vjoy configure-vjoy]]).
- Read-only (`--dry-run` is accepted and changes nothing).
:::

:::layer technical
- Output: as [[cli:vjoy map]] with the message `Plan (nothing saved).`; exit code `0`, `1` when the plan has errors
  (for example `No free vJoy ... left on devices 1-16 for ...`) or with `Unknown vjoy plan argument(s): ...`.
- Related: [[cli:vjoy status]], [[cli:vjoy settings]] (the devices JoyPro may use).
:::

<!-- entry: vjoy settings -->
:::layer beginner
Sets which vJoy devices JoyPro may use, whether the virtual joystick starts and stops by itself with a game in virtual
mode, and whether HidHide hides the physical devices. The ticks "Hide the real devices from the games set to Virtual
(HidHide)" and "Start and stop by itself when a game set to Virtual starts and ends" on the Virtual Joystick page run
it.

```powershell
engine vjoy settings --first-device 2 --last-device 3 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--first-device N`, `--last-device N`: 1..16 (default 1 to 16), the last not below the first. The mapping table and
  the function outputs use only these devices.
- `--auto-start on|off` (default off), `--hide-devices on|off` (default on); `true`/`false`, `yes`/`no`, `1`/`0` work
  too.
- Without options it prints the current settings. `--dry-run` saves nothing.
:::

:::layer technical
- Output: `vJoy devices <first>-<last>, auto start on|off, hide physical devices on|off.`; JSON also `FirstDevice`,
  `LastDevice`, `AutoStart`, `HideDevices`; exit code `0`.
- Failures (exit `1`): `--first-device takes 1..16.`, `--last-device takes 1..16.`, `--last-device must not be below
  --first-device.`, `--auto-start takes on or off.`
- Stored in the profile; one undo step with `--profile`. Related: [[cli:vjoy map]], [[cli:vjoy apply-hidhide]].
:::

<!-- entry: vjoy exe -->
:::layer beginner
Lists the executables of a game that HidHide blocks in virtual mode, so that game sees only the virtual joystick: the
ones found in the game's installation plus your own. Add the game's program when JoyPro does not find it.

```powershell
engine vjoy exe DCS --add "D:\DCS World\bin-mt\DCS.exe" --text
```
:::

:::layer advanced
- `<game>` is required. `--add <path>` (repeatable): an existing file, kept with its full path. `--remove <path>`
  (repeatable): removes one of yours.
- Found by JoyPro: the installation's marker executables and the game's process names found a few folder levels deep
  in the installation folder.
- Yours are kept outside the profile and used at the next [[cli:vjoy apply-hidhide]].
:::

:::layer technical
- Output: JSON `Game`, `Exes`, `Extra` (yours), `Warnings`, or text with `(set by you)` after your own; exit code `0`.
- Failures (exit `1`): `engine vjoy exe <game> [--add <path>] [--remove <path>]`, `Unknown game: ...`, `Executable not
  found: ...`
- Your list: `%LOCALAPPDATA%\JoyPro2\VirtualJoystick\game-exes.json`. Reference: [[doc:virtual-joystick]].
:::

<!-- entry: vjoy functions types -->
:::layer beginner
Lists the kinds of virtual function you can add, each with its inputs, outputs and parameters with their defaults.

```powershell
engine vjoy functions types --text
```
:::

:::layer advanced
- Type ids for `--type`: `bands`, `cycle`, `toggle`, `switch`, `presses`, `shift`, `merge`, `split`, `curve`,
  `axis-buttons`, `axes-hat`, `buttons-axis`, `encoder`, `turbo`, `macro`, `hat-buttons`, `buttons-hat`, `remap`,
  `mode`.
- Per input and output role: its kind, whether it is optional, and `<n>` for numbered roles.
- Read-only; no profile needed.
:::

:::layer technical
- Output: JSON `Types`, or text (`<icon> <id> - <name>: <summary>` with `inputs:`, `outputs:`, `params:` lines); exit
  code `0`, `1` with `Unknown vjoy functions types argument(s): ...`
- Related: [[cli:vjoy functions add]].
:::

<!-- entry: vjoy functions list -->
:::layer beginner
Lists the virtual functions of every game, or of one, with their inputs, outputs, problems and warnings.

```powershell
engine vjoy functions list --game DCS --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--game <game>`: only that game. `vjoy functions` without an action lists too.
- Each output shows the bind device and input the game gets; problems (an input or output missing) and warnings about
  the game's input limits are listed per function.
- JSON also lists the physical devices with their axes, buttons and hats (the editor's pickers) and, with `--game`,
  the game's input limits ([[cli:vjoy limits]]).
- Read-only.
:::

:::layer technical
- Output: JSON `Game`, `Functions`, `InputDevices`, `Limits`, `Warnings`, or text `Virtual functions of <game>: <n>`
  with `in`, `out`, `problem` and `warning` lines; exit code `0`.
- Failures (exit `1`): `Unknown game: ...`, `Unknown vjoy functions list argument(s): ...`
- Related: [[cli:vjoy functions set]], [[cli:vjoy forwarder-config]].
:::

<!-- entry: vjoy functions remove -->
:::layer beginner
Removes a virtual function from a game; its virtual outputs are free again. **Remove** in the function list runs it
after asking.

```powershell
engine vjoy functions remove --game DCS --id f1 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--game` and `--id` are required.
- Inputs of other functions that read this function's output are cleared (the message names them).
- Binds in your relations on its outputs stay; remove them there if you no longer need them.
- `--dry-run` saves nothing.
:::

:::layer technical
- Output: `Removed the function <id> "<name>" of <game>; its virtual outputs are free again.` (JSON `Message`, `Id`,
  `Game`); exit code `0`.
- Failures (exit `1`): `--game <game> is needed.`, `functions remove needs --id <function id>.`, `No function ... for
  ...`
- One undo step with `--profile`. Related: [[cli:vjoy functions list]].
:::

<!-- entry: vjoy functions test -->
:::layer beginner
Simulates a virtual function with the forwarder's own evaluation and lists every change of its outputs over time.
Nothing goes to vJoy, the pad or the keyboard.

```powershell
engine vjoy functions test --game DCS --id f1 --stream "0:in=0; 500:in=0.5; 1000:in=1" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The function: `--game` with `--id` (from the profile), or `--spec <json>|@<file>` (a function that is not saved).
- `--stream`: items `<ms>:<role>=<value>` separated by `;` or `,`; a button takes `1`/`0`, `on`, `down` or `press`.
  Without it a stream that shows the type's behaviour is used (for axis types a sweep up and back down over two
  seconds).
- `--rate` 10..2000 Hz (default 240), `--duration` 1..600000 ms (default: the stream's, 2000 ms for most types).
- Key presses of the function are listed as simulated, not sent. Read-only.
:::

:::layer technical
- Output: JSON `Function`, `Type`, `Rate`, `Duration`, `Stream`, `Events`, `Keys`, `Problems`, or text lines `<ms> ms
  <output> (<target>) -> <value>`; exit code `0`.
- Failures (exit `1`): `functions test needs --game <game> --id <function id>, or --spec <json|@file>.`, `--rate takes
  10..2000 Hz.`, `--duration takes milliseconds (1..600000).`, `A stream item is <time ms>:<role>=<value>, e.g.
  0:in=0.2; got "..."`
- Related: [[cli:vjoy functions add]], [[cli:vjoy functions set]].
:::

<!-- entry: vjoy limits -->
:::layer beginner
Lists what each game reads of one joystick: how many buttons, axes and hats, with confidence and source. vJoy offers
up to 128 buttons, 8 axes and 4 hats per device, on up to 16 devices.

```powershell
engine vjoy limits --game DCS --text
```
:::

:::layer advanced
- `--game <game>`: one game; without it every game.
- Per game: buttons, axes, hats (and four-way hats), the interface it reads, the confidence (`unknown` when JoyPro
  does not know the limits), a note and the source.
- Read-only; no profile needed.
:::

:::layer technical
- Output: JSON `VJoyMaximum` (`Buttons`, `Axes`, `Hats`, `Devices`) and `Games` (`Game`, `Name`, `Known`, `Buttons`,
  `Axes`, `Hats`, `FourWayHats`, `Interface`, `Confidence`, `Describe`, `Note`, `Source`), or text; exit code `0`.
- Failures (exit `1`): `Unknown game: ...`
- Related: [[cli:vjoy mode]] (one-device games), [[cli:vjoy status]].
:::

<!-- entry: vjoy recover -->
:::layer beginner
Puts HidHide back as it was when a virtual joystick session did not end cleanly (JoyPro crashed or was closed hard) and
its owner, the GUI, is gone. The GUI runs it at start when it finds such a session. Windows asks once for
administrator rights.

```powershell
engine vjoy recover --dry-run --text
```
:::

:::layer advanced
- No session journal: nothing to do (exit `0`).
- The owner process named in the journal still runs: nothing changes, the message says so (exit `0`); `--force`
  restores anyway.
- Otherwise the same restore as [[cli:vjoy restore-hidhide]]: exactly JoyPro's changes are taken back.
- `--dry-run`: lists from the journal what it would take back, without administrator rights.
:::

:::layer technical
- Output: HidHide's result and warnings, e.g. `The HidHide session is still in use by process <n>; nothing was changed
  (use --force to restore anyway).`
- Exit codes: `0`; `1` failed; `2` the administrator prompt was declined.
- The journal: `%LOCALAPPDATA%\JoyPro2\VirtualJoystick\hidhide-session.txt`. Reference: [[doc:virtual-joystick]].
:::
