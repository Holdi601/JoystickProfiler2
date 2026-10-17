# Setup wizard

The Setup wizard sets up your controls for the games you watch in one go. It looks at your connected devices, gives
each one a role, makes relations for the important functions of your games' aircraft, binds them to your devices and
shows you how it did it. You review everything before your profile changes. It runs without AI: the same choices always
give the same plan.

- GUI: Start page > `Your games` > **Set up my devices automatically...**, or Binds > **Set up my devices automatically...**. User guide:
  [GUI wiki](../gui/docs/GUI_USER_WIKI.md#setup-wizard).
- CLI: `engine setup devices | aircraft | plan | apply | render` (below; [CLI wiki](../engine/docs/ENGINE_CLI_WIKI.md)).
- Code: `engine/SetupWizard/*.cs` (the planner, plain C# 7.3, compiled into the engine and linked into the GUI),
  `engine/EngineSetupWizard.cs` (devices, catalogs, profile, apply, sheets, the CLI),
  `gui/JoyPro.Gui/ViewModels/SetupWizardViewModel.cs`, `gui/JoyPro.Gui/Views/SetupWizardWindow.axaml`.

![Devices and roles](ui/setup-wizard-2-devices.png)

## The five steps

1. **Games.** The games ticked for watching on the Start page (`Watch for changes`, `watchedGames` in
   ui-settings.json) are ticked. Change them here for this setup; the watching itself stays as it is.
2. **Devices and roles.** JoyPro recognises your devices with the device catalog (model, kind, hand, grip; see
   [device-reference.md](device-reference.md#1-identification)) and proposes a role for each one. Change a role in its
   list; pick the grip of a base that takes several grips. The box on the right says in words what the plan will do.
3. **Priorities.**
   - *Your aircraft*: the aircraft of your games that the local game lists know (DCS: the modules of your DCS
     database; other games count as one aircraft). Put them into the priority list: `Add as next priority` makes a new
     row, `Add as equal` puts it into the selected row. Rows can be dragged up and down, aircraft can be dragged onto a
     row; `Up`, `Down`, `Equal with above`, `Own row`, `Remove` do the same with buttons. Aircraft in one row count the
     same; aircraft you do not list count least.
   - *Modifiers*: none, 1, 2, 3 or 4, and `As few as possible`.
   - *Like the real aircraft?*: the realism slider, 0 to 100 %.
4. **Plan.** JoyPro makes the plan (`engine setup plan`): how many functions it placed, what it could not place, the
   modifiers it made, the rules it followed and a quality score per aircraft (100 % = every function on its best
   place).
5. **Review.** Per device JoyPro's drawing with a table per control group showing what is bound where (`engine setup
   render`), for one aircraft or all of them (relation names), and the list of the device's inputs. Select a function
   to move it to a free input, swap it with another one or take it out of the plan; select a function under `Not
   placed` to put it on a free input. `Apply to my profile` writes the relations, binds and modifiers in one step: one
   version in the profile history named "Setup wizard: N relations, M binds" (Undo takes all of it back). `Export to my
   games` opens the Export page.

## Roles

| Role | Proposed for | Used by |
|---|---|---|
| `main-stick` | the right-hand stick (or HOTAS, yoke) the profile uses most, else the best known one | the right hand in every aircraft |
| `heli-stick` | never proposed; choose it for a second stick (e.g. a force feedback base) | the right hand in helicopters |
| `left-stick` | a left-hand stick (catalog hand, or "L-" / "LEFT" in its name) | the left hand in space sims; the throttle side when there is no throttle |
| `throttle` | a throttle or quadrant | the left hand in aircraft (and in helicopters without a collective) |
| `collective` | a collective | the left hand in helicopters |
| `pedals` | rudder pedals | yaw / rudder and toe brakes |
| `panel` | panels, MFDs, button boxes, DIY boards, unknown devices | system switches where their type and label fit |
| `keyboard-joystick` | Wooting and other keyboards with a joystick mode | nothing: an extra, kept for your keyboard binds |
| `virtual` | vJoy, ViGEm | nothing: its binds stay |
| `unused` | a second stick, throttle or pedals of the same hand (one device per hand), wheels, gamepads | nothing |

The hands per aircraft class:

- **Right hand**: the main stick; helicopters use the `heli-stick` when there is one.
- **Left hand**: helicopters the collective, else the throttle; spaceships the left stick, else the throttle;
  aircraft the throttle, else the left stick.
- **Collective present**: the helicopters' collective (ThrottleLeftNonAB / ThrottleRightNonAB, the collective's buttons)
  goes on the collective, the aircraft's throttle on the throttle. Relations are split where the two classes use
  different devices ("[Helicopters]" / "[Fixed wing]").
- **Left stick present**: the spaceships' translation goes on it: X = strafe left/right, Y = forward/back, twist =
  up/down (a mini-stick Y or slider when there is no twist). Its hats and buttons count as the left hand (throttle
  side), never as the main stick (no pitch on the left stick).
- **No left stick**: translation on the throttle's mini-stick and main axis (the knowledge map's positions).
- **Pedals**: rudder / yaw and the toe brakes.
- **Panels**: a function goes on a panel switch whose printed label names it ("LDG GEAR UP", "ANTI SKID ON"); on/off
  pairs (gear up/down, master arm on/off, lights on/off ...) go on both positions of one toggle switch.

## How the plan is made

1. **Relations.** One relation per function of the knowledge map (`DB/Functions`: the catalog rows per aircraft and
   game, plus the function map's actions the catalog does not list, as Auto Group uses them) for the chosen games'
   aircraft. Display keys and keypads only when the setup has such a device.
   - **Your profile first**: a command that is already in one of your relations stays there; a relation with a bind on
     a device of this setup keeps it (it occupies its input for its aircraft). An unbound relation of yours is placed
     like a new one. `--replace-existing` lays your bound relations out again (each on its old control when possible).
     A command you switched off stays off.
   - **Realism below 50 %**: one relation per function for all aircraft (same function, same button everywhere), split
     only where aircraft classes use different devices.
   - **Realism from 50 %**: aircraft whose real HOTAS has the function elsewhere get their own relation (by the side
     the catalog gives for each aircraft and, with the cockpit database, the exact position). After placing, parts of
     one function that ended on the same input become one relation again; a function in one piece keeps its plain name.
2. **Importance** of a relation = category weight x frequency weight x priority.
   - Category (from the function map's categories, `relations functions`): flight controls 8 (flight, engine axes),
     weapons / countermeasures / HOTAS switches / sensors 4, systems (nav, lights, aircraft systems, engine buttons) 2,
     views and comms 1, display keys 0.5.
   - Frequency (catalog per aircraft): constant 5, frequent 3, occasional 2, rare 1.
   - Priority: row 1 weighs 1, each row below 0.6 of the one above, unlisted aircraft half of the last row; without a
     list every aircraft weighs 1. A relation counts its highest aircraft plus a little (0.15 each, up to 5) for the
     others. DCS input layers (Default, NS430, voice) count like the average aircraft.
3. **Score** of a relation on an input = importance x (comfort + realism x 2 x real match).
   - Comfort: the function's position preference (`slot_controls.tsv`, exact position +0.3), its label on a panel
     (+1.5, a different label -0.6, the label's direction +-0.8), reach (grip > base > panel, more for frequent
     functions), a modifier layer (minus, more with `As few as possible`), a latched switch for a non-pair (minus), your
     own previous control (+4).
   - Real match 0..1, weighted by aircraft priority: the cockpit database's position (exact 1, same hat 0.5, same hand
     0.2); without an entry the knowledge map's position on the side this aircraft has the function (0.8 / 0.5 / 0.25).
4. **Constraints.** Axis on axis, button on button. Only the devices the hands reach for the relation's aircraft
   classes. Trigger, weapon release, pinky, detents and flight axes only take functions made for them (or the real
   aircraft's function there at realism from 50 %). Cockpit panel functions stay off the grips. An input serves several
   relations only when they have no aircraft in common.
5. **Optimizer.** Kept binds first, then your own previous controls, then rounds of on/off pairs on toggle switches,
   hats as groups (TMS, DMS, CMS, trim ...) and single functions by importance: at realism from 50 % first only real
   positions, then only strong matches (labels, exact positions, the left stick's translation), then everything.
   Then local search: move a function to a better free input, swap two, let an unplaced function take the input of a
   less valuable one that can move elsewhere; passes in a shuffled order with a fixed seed (`--seed`), so the same
   choices give the same plan.

### Modifier budget

- The modifier buttons: your profile's own modifiers first (most used), then the pinky lever / button, the lower index
  button, the throttle's pinky, other grip buttons; on the main stick before the throttle. Layers copy the grip buttons.
- `As few as possible` (`--prefer-fewer`, default): no modifier while the essential functions (flight controls,
  weapons, countermeasures, sensors used constantly or often) of the top priority row fit; up to your number when they
  do not.
- Otherwise (`--use-all-modifiers`) your number of layers is offered; a layer nobody uses is not made.
- **Too few inputs**: when the essential functions of the top aircraft still do not fit and the hands' grip buttons
  are fewer than those functions, JoyPro adds modifiers beyond your choice (up to 2) and says so: "Only 6 free buttons
  on the sticks for the 14 most important functions of F-16C_50: JoyPro added 1 modifier although you asked for none."

### Quality per aircraft

For each aircraft: the importance-weighted average of (score on its input / best score it could get on any input of
the setup); kept binds count 100 %, unplaced functions 0 %. Also placed / total and the essential ones.

## Cockpit database

The realism slider uses the cockpit database (another part of JoyPro, `engine cockpits get <aircraft> [<variant>]
--json`) when it is there: per control position of the real HOTAS the function ids there. The planner reads (keys in
any case; a list of such entries or `{ "Cockpit": {...} }` work too):

```json
{ "aircraft": "F-16C_50", "variant": "", "game": "DCS",
  "positions": [ { "control": "stick.hat.tms.up", "functions": ["target_management_system_up"] } ] }
```

`control` is the device vocabulary ([devices.md](devices.md#control-vocabulary)); `functions` are the slot ids of
`DB/Functions/slots.json` (`function` for one id; `controls` / `hotas` for `positions`, `position` / `id` for
`control` are accepted). The answer of `engine cockpits get <aircraft> --json` itself (format
`joypro-cockpit-layout`, [cockpits.md](cockpits.md#for-the-setup-wizard-and-scripts-api)) is read as well: its
`Games[].Plane` (else the game) get `Devices[].Controls[].Functions[]` (`Vocabulary` -> `Fn`). `cockpits get` takes the
game's own aircraft name (`F-16C_50`, `FA-18C_hornet`) as well as the database's ids.

Sources, in this order: `--cockpit-file <json>` (the GUI saves the answer of `engine cockpits get` for each aircraft of
the priority list at realism 1 % or more), then the cockpit database itself (`DB\Cockpits`, read with the engine's
`CockpitCatalog`: every variant that names a game aircraft gives it its positions; a game-wide entry goes to the game's
key, first variant only). An aircraft a file gave keeps that entry. Without an entry the knowledge map is used (the
plan's notes name the sources). Code: `engine/SetupWizard/SetupCockpits.cs` (`ICockpitPositions`,
`CockpitPositionTable.AddJson` / `AddCatalog`).

## CLI

All print JSON without `--text`. Device and role options are shared by `devices` and `plan`.

- `engine setup devices [--grip <device>=<grip id>]... [--role <device|product|VID:PID>=<role>]... [--add-device <catalog id>]...`:
  devices with `Role`, `ProposedRole`, `Reason`, `Inputs`, grips, and the `Rules` in words.
- `engine setup aircraft --games <g1,g2>`: the aircraft for the priority list (`Key`, `Game`, `Class`).
- `engine setup plan --games <g1,g2> [--priorities "A>B|C>D"] [--priority "B|C"]... [--modifiers 0..8]
  [--prefer-fewer|--use-all-modifiers] [--realism 0..1|0..100%] [--seed N] [--replace-existing] [--role ...] [--grip ...]
  [--add-device ...] [--cockpit-file <json>]... [--out-plan <plan.json>]`: `>` separates rows, `|` (or `=`) aircraft
  of one row. Read-only. The plan file has `Devices`, `Rules`, `Inputs` (of the devices with a role that binds),
  `Modifiers` (name, input, `Forced`, reason), `Relations` (name, function, category, nodes to add, aircraft,
  `Overlap` = the aircraft it occupies, device, input, control, label, modifier, score, `RealMatch`, reason, `Kept`,
  `Existing`, `Removed`), `NotPlaced` (with the reason), `Quality`, `Notes`, `Warnings`.
- `engine setup apply --plan <plan.json> [--dry-run] [--force]`: creates or extends the relations (group `Setup`), sets
  the binds and modifiers. Refused when two relations with an aircraft in common share an input (the review's edits are
  checked the same way) unless `--force`. A command that another relation got in the meantime stays there. One
  profile save = one history version "Setup wizard: N relations, M binds".
- `engine setup render --plan <plan.json> --out-dir <folder> [--aircraft <name>] [--light] [--width <px>]`: a callout
  sheet (JoyPro's drawing, tables per control group, the layout code of the Layout page) per device with a role that
  binds; with `--aircraft` the function names of that aircraft, else the relation names.

## Example: a pit like the author's

Fake device set `engine/tests/fixtures/setupwizard/devices.tsv`: VKB Gunfighter with MCG Pro, VPforce Rhino (grip not
picked), Virpil WarBRD with Alpha-L, VPC MT-50CM throttle, MFG Crosswind V2, WinWing F/A-18 takeoff panel, vJoy,
Wooting. Games DCS and Star Citizen, priorities F-16C_50 > FA-18C_hornet = A-10C II > StarCitizen, 1 modifier as few as
possible, realism 30 %:

- Roles: VKB main stick, Alpha-L left stick, MT-50 throttle, Crosswind pedals, takeoff panel panel, Wooting keyboard
  joystick, vJoy virtual, Rhino not used (a second right-hand stick; choose "Stick for helicopters" to fly helicopters
  with it).
- About 360 relations; about 310 functions placed, no modifier needed (the essential functions fit), quality around
  85 % for the F-16, F/A-18 and A-10C II.
- Star Citizen: strafe left/right, forward/back, up/down on the Alpha-L's X, Y and twist; shields, missiles and targets
  on its hats; pitch and roll on the VKB.
- The F-16: TMS, DMS and CMS on the VKB's hats, trigger and weapon release on their own, trim on the thumb stick,
  TDC on the throttle's mini-stick, gear, flaps, anti skid, launch bar and the landing / taxi light on the takeoff
  panel's labelled switches.
- Helicopters: no collective, so their collective is on the throttle.

## Tests

- `gui/JoyPro.Gui.Tests/SetupWizardPlannerTests.cs`: roles (the user-like set, collective, left-handed, the user's
  choice), collective and space translation rules, modifier budget (few buttons force a modifier and say why, enough
  buttons need none, the profile's own modifier first), priority weighting (the higher aircraft gets the better input,
  equal rows), realism 0 vs 1 with a fake cockpit database entry, labelled panel switches and pairs, kept binds,
  determinism and the plan file, the review's move / swap / remove with conflicts.
- `gui/JoyPro.Gui.Tests/SetupWizardRenderTests.cs`: the wizard view model through every step with a fake engine (the
  arguments it gives the engine, a move in the review, Apply), and offscreen renders of every step with the real engine
  on the fixture's devices (`JOYPRO_SETUP_WIZARD_PREVIEW=<folder>`; the pictures in `docs/ui/setup-wizard-*.png`).
- `engine/tests/smoke/setupwizard.ps1` (engine smoke test): the fake device set end to end: roles, a role choice, the
  aircraft, a plan (left-stick translation, panel labels, no collective, determinism), a cockpit entry at realism
  100 %, apply with its history version, a refused conflicting plan, the sheets.
