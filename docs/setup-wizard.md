# Setup wizard

The Setup wizard sets up your controls for the games you watch in one go. It looks at your connected devices, gives
each one a role, makes relations for the important functions of your games' aircraft, binds them to your devices and
shows you how it did it. You review everything before your profile changes. It runs without AI: the same choices always
give the same plan.

- GUI: the Start page's auto setup card (below), Start page > `Your games` > **Set up my devices automatically...**, or Binds >
  **Set up my devices automatically...**. User guide:
  [GUI wiki](../gui/docs/GUI_USER_WIKI.md#setup-wizard).
- CLI: `engine setup devices | aircraft | plan | apply | render` (below; [CLI wiki](../engine/docs/ENGINE_CLI_WIKI.md)).
- Code: `engine/SetupWizard/*.cs` (the planner, plain C# 7.3, compiled into the engine and linked into the GUI),
  `engine/EngineSetupWizard.cs` (devices, catalogs, profile, apply, sheets, the CLI),
  `gui/JoyPro.Gui/ViewModels/SetupWizardViewModel.cs`, `gui/JoyPro.Gui/Views/SetupWizardWindow.axaml`.

![Devices and roles](ui/setup-wizard-2-devices.png)

## Start page

Once the installed games are known, JoyPro reads per game whether the user already has binds there
(`engine setup games`, [CLI wiki](../engine/docs/ENGINE_CLI_WIKI.md); the game's control files are only read, keyboard
and mouse binds do not count). The Start page's auto setup card then says:

- **Set up your devices for X, Y, Z**: the games where nothing is assigned yet (no binds of their own in the game and none
  in the profile). **Set it all up for me** (the blue button) runs it by itself; **Set up step by step...** opens the wizard with exactly these games ticked.
- **Your own binds in A stay**: games with binds of the user's own in the game. **Import, then fill the gaps** starts the
  import of that game; its result offers **Now fill the gaps...**, which opens the wizard for that game. The plan keeps
  every bind the profile has ("Your profile first"), so only free controls get functions.
- **Already in your profile: B**: **Fill the gaps...** opens the wizard for these games.
- **Where is what?** opens the layout window (below).

The look must never be noticed (`MainWindowViewModel.AutoSetupStart.cs`): it starts only while the Start page shows and
once the app has had no engine call for a moment, runs in an engine process of its own in Windows' background mode (not
in the queue of the app's engine calls, no busy state, the engine server keeps running), and stops when the Start page is
left. Its answer is kept until the installed games, the profile (its file or its lists), the connected devices change,
or an import, an export (Refresh), the wizard's Apply or a game's changed control files (game watcher) throw it away.
`--status-only` leaves the control files of a game the profile already has binds for unread (their status is "profile"
either way; DCS reads every aircraft's files), and the connected devices are read once per run instead of once per game.
The quick path asks at once, at normal priority.

## Quick path

For someone who just wants binds: **Done, and set everything up for me** at the end of the first start's game setup
(`GameSetupWindow`), or **Set it all up for me** on the Start page's card. JoyPro asks which games have nothing
assigned (`setup games`; else the games already in the profile), opens the wizard with them ticked and runs every step by
itself with the defaults: detected devices with their proposed roles, every aircraft equal, no modifier wanted, your last
choice of **Where should functions go?** (the middle, 50 %, at first), phase layers automatic (`SetupWizardViewModel.RunQuickAsync`). It stops at the review with a box in plain words
(`QuickSummary`, no JoyPro terms): which devices, which games, how many functions are on the controls, and every device
JoyPro does not know by its exact model ("It is set up like a usual joystick: moving it steers, button 1 fires, the first
hat trims" / "like a usual throttle: its first lever is the throttle"). The buttons say **Use these buttons** (Apply) and
**Write to my game**, which exports exactly these games right away (`MainWindowViewModel.QuickExportAsync`: the Export
page with only them ticked; the usual questions before a first export still come).

A device not in `DB/Devices` (neither its USB id nor its HID product name, exactly or as part of the name) gets a generic
layout from what Windows reports (`EngineDeviceKb.GenericModel`): its kind from the name (stick, throttle, pedals,
collective, panel; "URSA MINOR", "fighter" and "sidestick" count as sticks) or its capabilities, stick X / Y = roll /
pitch, Z = twist when there is no RZ, button 1 = trigger, hat 1 = trim hat; a throttle's lever is Z, else its first other
axis (levers on RX / RY). `SetupRoles.Rules` adds the plain line for each such device.

The named test of this path: IL-2 Great Battles only, a WinWing URSA MINOR stick and throttle
([auto-setup-eval.md](auto-setup-eval.md#novice-case) and `QuickSetupPersonaTests`).

## Layout window

**Where is what?** (Start page card, Layout page, the wizard's review, the tray menu): a window of its own, not owned by
the main window, so it can stay on a second monitor. Pick a game and an aircraft; every device of the setup shows with
JoyPro's drawing and its tables and lines (the Layout page's callout sheets, `CalloutSheetCanvas`) and the function on
each control. **Device** shows all devices or one alone (bigger, readable from a distance).

- Source: before Apply the wizard's plan, else the profile's binds on the connected devices (`engine setup preview
  --plan | --from-profile --live-data`: per device its drawing, callout sheet and row texts).
- **Light up what I press** (on by default): a pressed button, a moved axis or hat lights its row and line up and its
  function is named in big letters at the top. The window reads the devices itself (`IJoystickCaptureService.MonitorAsync`)
  only while it is shown and not minimized; after 1.5 s without input it reads them every 250 ms instead of every 15 ms.
- **Follow the game** (off by default): DCS's hook (UDP 1992, one listener shared with the HUD) names the aircraft; every
  10 s the running game is looked up like the HUD does. Without that the manual choice stays.
- Kept in `ui-settings.json` (`layoutWindow`): game, aircraft, device, both options, position, size, maximized, the
  monitor, and whether it was open when JoyPro closed (then it opens again with the next start; a monitor that is gone
  puts it on the main window's monitor).
- The tray's **Where is what?** starts JoyPro with `--layout-window` (a running JoyPro gets it like a review key).

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
   - *Mode switch (phase layers)*: Automatic, Always or Off ([auto-assign.md](auto-assign.md#phase-layers)).
   - *Where should functions go?*: a slider from *Same button for the same job in every aircraft* (0 %) to *Where the
     real aircraft has it* (100 %); internally and on the command line it is the realism (`--placement`, old name
     `--realism`). The choice is kept in the settings and used by every later setup (also the quick setup and the Start
     page, which use it without asking; the default is the middle, 50 %).

   - *Do you use head tracking or VR?* ([below](#head-tracking)): shown when a chosen game is a flight sim that supports
     it.

   The choices of this step are kept in the settings, and the next wizard starts with them.
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
   - **Where should functions go?** below 50 %: one relation per function for all aircraft (same function, same button everywhere), split
     only where aircraft classes use different devices.
   - **Where should functions go?** from 50 %: aircraft whose real HOTAS has the function elsewhere get their own relation (by the side
     the catalog gives for each aircraft and, with the cockpit database, the exact position). After placing, parts of
     one function that ended on the same input become one relation again; a function in one piece keeps its plain name.
2. **Importance** of a relation = category weight x frequency weight x priority x flight phase weight for the roles of
   its aircraft x time criticality ([auto-assign.md](auto-assign.md#flight-phases)).
   - Category (from the function map's categories, `relations functions`): flight controls 8 (flight, engine axes),
     weapons / countermeasures / HOTAS switches / sensors 4, systems (nav, lights, aircraft systems, engine buttons) 2,
     views and comms 1, display keys 0.5.
   - Frequency (catalog per aircraft): constant 5, frequent 3, occasional 2, rare 1, last 0.1. A row's `occasional`
     (or none) takes the function's own frequency; `last` stays: a control that only joins the shared relations (Fate
     of the Galaxy's S-foils and forward / reverse thrust) and gets an input only when nothing else wants it.
   - Priority: row 1 weighs 1, each row below 0.6 of the one above, unlisted aircraft half of the last row; without a
     list every aircraft weighs 1. A relation counts its highest aircraft plus a little (0.15 each, up to 5) for the
     others. DCS input layers (Default, NS430, voice) count like the average aircraft.
3. **Score** of a relation on an input = importance x (comfort + realism x 4 x real match): the pull to the real position
   grows evenly with the slider. At every position a helicopter's engine levers never share a relation with a jet's
   throttle, a DCS input layer's throttle never with an aircraft's, and a second trigger stage that drops bombs never with
   one that fires the gun.
   - Comfort: the function's position preference (`slot_controls.tsv`, exact position +0.3), its label on a panel
     (+1.5, a different label -0.6, the label's direction +-0.8), reach (grip > base > panel, more for frequent
     functions), a modifier layer (minus, more with `As few as possible`), a latched switch for a non-pair (minus), your
     own previous control (+4).
   - Real match 0..1, weighted by aircraft priority: the cockpit database's position (exact 1, same hat 0.5, same hand
     0.2); without an entry the knowledge map's position on the side this aircraft has the function (0.8 / 0.5 / 0.25).
4. **Constraints.** Axis on axis, button on button. Only the devices the hands reach for the relation's aircraft
   classes. Trigger, weapon release, pinky, detents and flight axes only take functions made for them (or the real
   aircraft's function there with the slider from 50 %). Cockpit panel functions stay off the grips. An input serves several
   relations only when they have no aircraft in common.
5. **Optimizer.** Kept binds first, then your own previous controls, then rounds of on/off pairs on toggle switches,
   hats as groups (TMS, DMS, CMS, trim ...) and single functions by importance: with the slider from 50 % first only real
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
the setup); kept binds count 100 %, unplaced functions 0 %. Also placed / total and the essential ones. An essential
counts as placed when the aircraft can do it another way (only the count; placement and the score are unchanged): a
function dropped as redundant (the gear toggle next to gear up and gear down), an axis whose function is placed in
another form (the wheel brake axes next to the "Wheel brakes" button, the second engine's throttle next to IL-2's one
lever), and an axis named N next to two placed buttons named "N: ..." (the trim axis "Elevator trim" next to "Elevator
trim: nose up / down"). The quick path's calm line ("All important buttons are set ...") reads this count.

## Cockpit database

The slider **Where should functions go?** uses the cockpit database (another part of JoyPro, `engine cockpits get <aircraft> [<variant>]
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
the priority list with the slider at 1 % or more), then the cockpit database itself (`DB\Cockpits`, read with the engine's
`CockpitCatalog`: every variant that names a game aircraft gives it its positions; a game-wide entry goes to the game's
key, first variant only). An aircraft a file gave keeps that entry. Without an entry the knowledge map is used (the
plan's notes name the sources). Code: `engine/SetupWizard/SetupCockpits.cs` (`ICockpitPositions`,
`CockpitPositionTable.AddJson` / `AddCatalog`).

## Head tracking

For the sims that support head tracking or VR (DCS, IL-2 Great Battles / Korea / Cliffs of Dover / 1946, Falcon BMS,
MSFS 2020 / 2024, FSX, Prepar3D, X-Plane 11 / 12, War Thunder, Arma, Star Citizen, Elite Dangerous, VTOL VR) the wizard
asks *Do you use head tracking or VR?* with two answers:

- **Yes – I use head tracking, face tracking, Tobii or VR, or I plan to (no view buttons on the HOTAS).** No input is
  spent on looking around in those sims: no snap views, no look hat, no look axes. Only the view recenter (the TrackIR /
  VR centre) is weighted up so it lands on a reachable, low-priority button.
- **No – put looking around on a hat of my stick or throttle.** Looking around ranks right after the fight essentials
  (gun, weapon release, countermeasures, brakes, trim, target) and before gear, flaps, airbrake and comms: the four
  snap views go together on a grip hat without a modifier, or the look axes on a thumb stick (a gamepad's right
  stick). A device without a free hat gets them on the first shift layer instead of none. From the middle of *Where
  should functions go?* a function on its real HOTAS position (the Warthog's DMS in the F-16) keeps it.

The quick path asks the same question as one yes / no in its window and makes the plan again with the answer. The
answer is kept in the settings (`autoPlacement.wizardHeadTracking` in ui-settings.json: `yes`, `no`, empty = not asked
yet, planned like no) and used by every later setup; **Settings > Looking around** changes it and offers **Set up
again** for the installed sims it concerns. On the command line: `engine setup plan --head-tracking yes|no`.

The answer belongs to the **profile** too (`Pr0file.HeadTracking`): `engine setup apply` keeps the answer its plan was
made with, a change in Settings writes it into the open profile, and `engine profile head-tracking [yes|no|clear]`
shows or sets it; `engine setup plan` without `--head-tracking` uses the profile's answer. With *yes*
**[Auto Group](auto-group.md)** makes no look-around relation (snap views, look axes) for the aircraft of those sims
("Left out for head tracking: N"), and the **suggestions** (Suggest, Suggest missing, Fill all) propose no look-around
command of those sims, neither into a look relation nor into another one. The recenter, zoom, padlock and the camera
views stay. Look relations the profile already has are left as they are.

Sims whose camera JoyPro does not manage (MSFS, X-Plane, Falcon BMS, Arma: no look command in their lists) keep one
grip hat free for the game's own look-around when the answer is no and the hands have two or more four-way hats.

## Devices without published numbers, gamepads

- A catalog device whose DirectInput numbers are not published (no `input` in `DB/Devices`) gets the usual order:
  button 1 = the trigger, hat 1 = the first hat, stick X / Y = roll / pitch, twist RZ, pedals X / Y = toe brakes and RZ =
  rudder, a throttle's main lever Z, then the catalog's order (a connected device only numbers it reports). The device
  list says "Button numbers not published for ...: JoyPro assumed the usual order"; the review shows every input.
- An Xbox controller (`microsoft-xbox-controller`) is the main stick when no joystick, HOTAS or yoke is connected: left
  stick pitch and roll, triggers rudder, the bumpers trigger and weapon release. Without head tracking the right stick
  looks around and the throttle goes on the game's increase / decrease buttons on the D-pad up / down (DCS "Throttle
  Smoothly" where the module has it); a game without such buttons keeps the throttle on the right stick. Gear and the
  trim may sit behind the shift button (Menu) on a pad.
- Two sticks that fit either hand and no throttle (two T.16000M): the second one is the left stick.

## How good the plans are

`engine setup eval` plans 40 device setups for every wizard game and the main DCS families and scores them
([auto-setup-eval.md](auto-setup-eval.md)); the smoke test keeps its thresholds.

## CLI

All print JSON without `--text`. Device and role options are shared by `devices` and `plan`.

- `engine setup devices [--grip <device>=<grip id>]... [--role <device|product|VID:PID>=<role>]... [--add-device <catalog id>]...`:
  devices with `Role`, `ProposedRole`, `Reason`, `Inputs`, grips, and the `Rules` in words.
- `engine setup aircraft --games <g1,g2>`: the aircraft for the priority list (`Key`, `Game`, `Class`).
- `engine setup plan --games <g1,g2> [--priorities "A>B|C>D"] [--priority "B|C"]... [--modifiers 0..8]
  [--prefer-fewer|--use-all-modifiers] [--placement same-button|real-aircraft|0..1|0..100%] (old name --realism) [--seed N] [--replace-existing] [--phase-layers on|off|auto] [--role ...] [--grip ...]
  [--add-device ...] [--cockpit-file <json>]... [--only-aircraft <a1,a2>] [--out-plan <plan.json>]`: `>` separates rows, `|` (or `=`) aircraft
  of one row. Read-only. The plan file has `Devices`, `Rules`, `Inputs` (of the devices with a role that binds),
  `Modifiers` (name, input, `Forced`, reason), `Relations` (name, function, category, nodes to add, aircraft,
  `Overlap` = the aircraft it occupies, device, input, control, label, modifier, score, `RealMatch`, reason, `Kept`,
  `Existing`, `Removed`), `NotPlaced` (with the reason), `Quality`, `Notes`, `Warnings`.
- `engine setup check --plan <plan.json> [--modifiers N]`: the rules every plan keeps (C1-C10 of the
  [combination matrix](auto-assign.md#combination-matrix)) and its measures; prints JSON, or text with `--text`.
- `engine setup apply --plan <plan.json> [--dry-run] [--force]`: creates or extends the relations (group `Setup`), sets
  the binds and modifiers. Refused when two relations with an aircraft in common share an input (the review's edits are
  checked the same way) unless `--force`. A command that another relation got in the meantime stays there. One
  profile save = one history version "Setup wizard: N relations, M binds".
- `engine setup render --plan <plan.json> --out-dir <folder> [--aircraft <name>] [--light] [--width <px>]`: a callout
  sheet (JoyPro's drawing, tables per control group, the layout code of the Layout page) per device with a role that
  binds; with `--aircraft` the function names of that aircraft, else the relation names.

## Example: a large pit

Fake device set `engine/tests/fixtures/setupwizard/devices.tsv`: VKB Gunfighter with MCG Pro, VPforce Rhino (grip not
picked), Virpil WarBRD with Alpha-L, VPC MT-50CM throttle, MFG Crosswind V2, WinWing F/A-18 takeoff panel, vJoy,
Wooting. Games DCS and Star Citizen, priorities F-16C_50 > FA-18C_hornet = A-10C II > StarCitizen, 1 modifier as few as
possible, **Where should functions go?** at 30 %:

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
- `gui/JoyPro.Gui.Tests/QuickSetupPersonaTests.cs`: the quick path of the novice case with the real engine on
  `engine/tests/fixtures/setupwizard/ursa-minor.tsv` (first start, quick path, IL-2 functions on the right devices, Use
  these binds into a temp profile, Write to my game = the IL-2 export, faked) and `ursa-unknown.tsv` (the same devices
  with a USB id and name JoyPro does not know: set up generically and said so).
- `engine/tests/smoke/autosetup.ps1`: a part of `setup eval` with thresholds, including the novice case.


## More setup commands

- `engine setup games [--games <ids>] [--path <game>=<folder>]... [--status-only]`: per game `empty`, `game` or `profile` (Start page).
- `engine setup preview (--plan <plan.json> | --from-profile) [--game <id>] [--aircraft <key>] [--out-dir <folder>]
  [--live-data]`: the layout window's data.
- `engine setup eval ...`: the evaluation ([auto-setup-eval.md](auto-setup-eval.md)).
