# Several inputs per relation (experimental, 2.6.5)

A relation can have more than one bind: the **main bind** (as always) and **extra binds**, each with its own device,
input, modifiers and axis tuning. Two devices can then do the same functions, for example the same grip on two bases
(a tester's two Virpil Constellation ALPHA Left grips, one on a VPC MongoosT-50CM2 base, one on a MOZA AB9). The
alternative, a copy relation per device, was not chosen: the actions, aircraft and names stay in one relation.

The feature is behind **Settings > Experimental > Several inputs per relation (experimental)**, off by default
(`ui-settings.json` `"multiBind"`). The GUI then passes the global option `--multi-bind on` to every engine call
(`JOYPRO_MULTI_BIND=1` does the same for hand-typed commands). With it off everything behaves as before: the engine
reads no extra bind, exports, checks and pages use the main binds only, and a profile without extra binds is written
byte for byte as before.

## Data model

- `Relation.ExtraBinds` (`List<Bind>`, `[OptionalField]`, engine/CoreRelation.cs). `relation.bind` and
  `InternalDataManagement.AllBinds[name]` stay the main bind; every extra bind has `Rl` = its relation. Inputs are
  numbered as the pages show them: 1 = the main bind, 2.. = the extra binds.
- Serialization (engine/CoreProfileFormatter.cs): profiles are written with a surrogate for `Relation` that writes the
  same members in the same order with the same types as BinaryFormatter's default, and leaves `ExtraBinds` out while it
  is empty. A profile without extra binds therefore has the bytes it had before 2.6.5 (checked old vs new engine on a
  copy of a real 5 MB profile: load + save and every export identical). Reading uses BinaryFormatter's default (no
  surrogate): older profiles and JoyPro 1 files read as always; an older JoyPro 2 reading a new profile skips the
  unknown member (version tolerant serialization) and keeps the main bind (the extra binds are lost when that version
  saves).
- `Relation.Copy`, the engine server's profile copy (ObjectGraphCloner), the history (versions are the saved bytes),
  the relations/binds JSON documents (`relations export/import`, `binds export/import`, `EngineBindDto.ExtraBinds`) and
  the device renames (devices exchange / auto-remap / the load correction's device remap) carry the extra binds; the
  modifiers list (`AllModifiers`) includes theirs.
- The history describes an extra bind as an item of its own, "<relation> (input 2)".

## Command line

    engine binds extra list <relation>
    engine binds extra add <relation> --device <d> --input <i> [--format dcs|il2] [--modifier <m>]... [--copy-tuning true|false] [--inverted ..] [--slider ..] [--deadzone ..] [--saturation-x ..] [--saturation-y ..] [--curvature ..]
    engine binds extra set <relation> <n> [same options] [--clear-modifiers]
    engine binds extra remove <relation> <n>|--all
    engine binds extra move <relation> <n> --to <m>            (--to 1: it becomes the main bind, the main bind takes its place)
    engine binds extra move <relation> <n> --to-relation <name>
    engine binds tuning show|set|clear <relation> --bind <n> ...  (scoped axis tuning of input n)
    engine relations join-inputs [--game <id>] [--relation <name>] [--apply]
    engine devices copy-to <device A> <device B> [--mode auto|same|layout] [--map OLD=NEW]... [--grip <device>=<grip id>]... [--merge skip|add] [--dry-run]

Changing commands need `--multi-bind on`; `list` and `remove` work always, so extra binds of a profile can be seen and
taken out with the setting off. `add` on an axis copies the main bind's tuning (every scope) unless `--copy-tuning
false`; a relation without a bind gets the input as its main bind. `binds list` and `relations show` list the extra
binds ("+ input 2: device=... | input=... | modifiers(n)=..."; JSON: `ExtraBinds` of the main bind).

## Checks

`relations conflicts` and `profile validate` look at every input in use (`MultiBind.WithActiveExtras`): a relation never
conflicts with its own extra bind (the conflict key holds the relation once), but another relation on the same device,
input and modifiers with an action of the same aircraft is "one input does two things" as before. `relations
duplicates` (the same action in two relations) is about relations and is unchanged: an extra bind is not a second
relation.

## Export per game

Where the game's file keeps several inputs per action, every bind is written (main first). Where it keeps one, the
main bind is written and the export warns per relation ("<game> keeps one ...: relation 'X' was exported with its main
input only; N extra input(s) not written"). With the setting off, an export names the extra binds it left out once
("N extra input(s) of M relation(s) were not exported to <game>: the experimental setting ... is off"). Nothing is
dropped without a word. Registry games go through `GameProfileBridge.CollectBindings` / `ExportedBinds`; DCS, IL-2
Great Battles and Star Citizen have their own exporters with the same rules. The export rules per game class (clean,
nuke, keyboard) are unchanged: the extra binds are binds like any other for them.

| Game | Support | Evidence (what the file holds; what the exporter does with two bindings of one action) |
|---|---|---|
| DCS | yes | one `added` list per command in each device's `.diff.lua`; a key of another device goes to that device's file, two keys of one device are merged into one list (EngineDcsExport.cs AddEntry) |
| IL-2 Great Battles | yes (axis: one curve) | one line per action and input in global.actions; the response curve is per action id, the export warns when the inputs have other tuning |
| IL-2 Korea | yes | the JSON `actions` keep a list of inputs per action (Il2KoreaIntegration) |
| FSX / FSX SE / P3D v4-v6 | yes | one block per input in each device map, no limit per event (FsxIntegration) |
| Arma 2 / 2 OA / 3 | yes | `key<Action>[]={codes}` list (ArmaProfileIntegration) |
| Arma Reforger | yes | a list of sources per action (ArmaReforgerIntegration) |
| X4 | yes | several `<id>` entries per control (X4Integration) |
| VTOL VR (BYOJoystick mod) | yes | "several bindings per target are allowed" (docs/games/vtol-vr.md) |
| MSFS 2020 / 2024 | limited | one profile per device with Primary and Secondary: two per action and device; other devices are fine; a third input on one device is skipped with a warning |
| Elite Dangerous | limited | buttons: Primary + Secondary (two per action over all devices); axis: one binding; the export warns when the slots are full |
| War Thunder | limited | shortcuts: repeated blocks (the menu shows three); axis: one per action, a second is skipped with a warning |
| Falcon BMS | limited | buttons: any number of buttons with the same callback; axis function: one physical axis (warning) |
| X-Plane 11 / 12 | limited | buttons: one command on many buttons; two axes with the same use: written, the game ignores a shared use (warning) |
| IL-2 1946 / Cliffs of Dover / Blitz | limited | lines per input; 1946 keeps one axis and two other inputs per command, CoD one axis per command (warning) |
| Ace Combat 7 | limited | one value per action per device model: different models fine, two devices of one model warn |
| Nuclear Option / Tiny Combat Arena | limited | Rewired maps per device model: two devices of one model, the second is skipped |
| Project Wingman | limited | mappings without limit (menu shows 3/4); one curve and dead zone per axis action |
| Star Citizen | main only | one rebind per input type and action; all joysticks share the type `js` |
| Battlefield 6 / 2042 | main only | the game reads one joystick: extra binds on another device would push the main device out |
| G-Rebels | main only | one key per action and device channel; a second one would silently overwrite |
| HLL Vietnam | main only | one keyboard key per pilot action |
| VTOL VR (native) | main only | the game's UI keeps one binding per action |
| WARDOGS | main only | the game uses one `ActionBindings` per action |
| Ace Combat 8 | main only | the virtual Xbox pad gets one control per action |

"Limited" games get every bind; the game's own export checks its limit and warns as it does for two relations on one
action. The table is `MultiBind.SupportOf` (engine/EngineMultiBind.cs). Virtual joystick mode gives every extra bind
its own vJoy slot (EngineVJoy.CollectUsedInputs); the virtual Xbox pad uses the main bind.

## Import and re-import

- Re-import of JoyPro's own export (setting on): the metadata names the relation, so an extra bind comes back as an
  extra bind of that relation with its modifiers and tuning, not as a second relation: DCS (`.jp`, `JPK`), IL-2 (`//JP:`
  comments, a seventh field = the input number, only for extra binds), registry games (`<config>.joypro-meta.json`:
  `"Inputs"` with the input numbers, only when there is an extra bind). Files without extra binds stay byte for byte as
  before; an older JoyPro ignores the new field.
- A game file with the same action on two inputs that JoyPro did not export: the import makes a relation per input as
  before and, with the setting on, adds a note "N function(s) are bound on several inputs as separate relations (...):
  keep each as one relation with several inputs? engine relations join-inputs --game <id> --apply". `relations
  join-inputs` finds relations with exactly the same actions and aircraft on other inputs and keeps the first (a name
  without copy suffix, else the shortest) with the others' inputs as extra binds (preview without `--apply`).
- With the setting off every import is as before.

## Copy to another device

`devices copy-to A B` gives every bind on device A (main and extra binds) the matching input of device B as one more
input of the same relation; A keeps its binds. The inputs are matched exactly as `devices replace` matches them: the
same input ids for the same product (or `--mode same`), else by layout from the device catalog (`--mode layout`,
`--grip` picks the grip of a base), `--map OLD=NEW` for your own choices. An input of B another relation uses is left
out with a warning unless `--merge add`. The preview (`--dry-run`) changes nothing; the copy is one profile version
(Ctrl+Z undoes it). GUI: Binds page > More inputs > Copy every bind of a device to another device (Preview, Copy).

### The same grip on two bases (tester report)

What the device catalog (EngineOnly_Runtime/DB/Devices) knows:

- Virpil ALPHA grips (virpil-alpha-l, virpil-alpha-l-v2, virpil-alpha-prime-l and the right-hand ones): "Grip numbering
  = what the grip reports on any Virpil base (VPC Configurator factory default); the base adds JOY_X/JOY_Y. Fits
  WarBRD, WarBRD-D and MT-50CM2/CM3 bases (same grip numbering; the PID reflects the base)." The classic and the
  Configurator 2.0 layouts number some buttons differently.
- VPC MongoosT-50CM2 base (virpil-mongoost-50cm2-base): names "VPC Stick MT-50CM2", "L-VPC Stick MT-50CM2", ...; no
  VID:PID known ("depends on base + grip + hand + firmware ... let the user pick the grip").
- MOZA AB9 base (moza-ab9): 346E:1000 (community source), axes JOY_X roll / JOY_Y pitch, no buttons of its own; "grips
  plug in on a 5-pin connector, third-party grips via adapter". The catalog's AB9 grips are MOZA's (MH16, MHG, MA3X).
  It has no data on how a Virpil grip's buttons are numbered through the AB9 or an adapter.

What `devices copy-to` does with them (checked with fake devices "L-VPC Stick MT-50CM2" 3344:8130 and "MOZA AB9 Base"
346E:1000, binds on JOY_X, JOY_Y, BTN 1, 2, 8, 13):

- Without a grip picked: JoyPro guesses a MongoosT-50 grip for the Virpil base and the MOZA MHG for the AB9 (warning
  "the grip is not chosen"). The parts do not match, so the buttons fall back to the same input id; the axes map by
  part (roll -> roll, pitch -> pitch). Result: the same ids, but with a misleading part name in the preview.
- With the grip picked for both devices (`--grip "<A>=virpil-alpha-l-v2" --grip "<B>=virpil-alpha-l-v2"`, or on the
  Layout page): both layouts are the same grip, so the mode is "same": every input keeps its id and the preview names
  the parts ("Flip trigger UP", "Face hat 1 up").
- Either way JoyPro maps BTN n on the Virpil base to BTN n on the AB9. Whether the AB9 (with the adapter) reports the
  ALPHA's buttons with the same numbers is not in any data JoyPro has: press each control once (Record or the HUD) and
  fix differences with `--map OLD=NEW` or by changing the copied input on the Binds page.

## GUI

- Settings > Experimental: the switch.
- Binds page: a card shows "+1" (the other inputs in its tooltip); the selected relation's **More inputs
  (experimental)** panel lists input 2.. with **Make main** and **Remove**, **Record another input**, **Add the input
  picked by hand** (the device and input of Pick the input by hand) and **Copy every bind of a device to another device**
  (First device, Second device, Preview, Copy).
- Relations page: the card and the relation editor show "+1" next to the bind chip (a click opens the bind).
- Overlay/HUD names (engine overlay map), the Layout page's pictures and cheat sheets (EngineLayoutRenderer) and the
  layout suggestion's used inputs include the extra binds while the setting is on.
- Not covered yet: the HUD designer's live "which relation is on this input" uses the overlay map (covered), but the
  Binds page's follow input, Visual assign and the device reference page look up the main binds only; the axis-response
  section of the Binds page edits the main bind (`binds tuning --bind <n>` tunes an extra bind on the command line).

## Tests

- engine/tests/smoke/multibind.ps1: off/on, add/set/move/remove, tuning `--bind`, history undo/redo, conflicts and
  validate, DCS export off/on and the `.jp` re-import, Star Citizen's warning, join-inputs, copy-to (preview changes
  nothing, one undo), a profile whose extra binds were removed has no `ExtraBinds` member.
- gui/JoyPro.Gui.Tests/MultiBindTests.cs: off by default nothing shows and no flag; on, the cards, the relation editor
  and the panel, every command's arguments, the copy preview and apply, the setting survives a restart; renders with
  `JOYPRO_MULTIBIND_RENDER_OUT`.
- Old vs new engine with the setting off and on on a copy of a real profile: every export (DCS add/merge/clean on a copy
  of Saved Games, IL-2, 33 registry games) and the saved profiles byte for byte the same, except timestamps.
