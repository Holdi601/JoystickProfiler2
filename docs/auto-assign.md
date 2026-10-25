# Auto Assign

Auto Assign binds your relations to the physical controls of your devices. It works for any setup: one stick or a
full pit, mixed vendors, panels, pedals, a second stick. It runs without AI. A device catalog says what every input of
a known device physically is (trigger, TMS hat, gear lever ...), and the function catalog says where each function
belongs in a real cockpit.

- CLI: `engine relations auto-assign [--grip <device>=<grip id>]... [--add-device <catalog id>]... [--ignore-device <device>]... [--relation <name>]... [--max-modifiers 0|1|2] [--phase-layers on|off|auto] [--overwrite [--include-missing]] [--no-style] [--explain <relation>]... [--dry-run]`
- Devices: `engine devices kb identify` shows how your devices were recognised; `engine devices kb list [query]` and
  `engine devices kb show <id>` browse the catalog.

Run it with `--dry-run` first: it prints every assignment and changes nothing.

## Your devices

- **Recognised devices** are matched by USB VID:PID and product name against `DB/Devices` (about 320 sim devices:
  VKB, Virpil, Thrustmaster, WinWing, Logitech/Saitek, VPforce, MOZA, CH, Honeycomb, Turtle Beach, Brunner,
  RealSimulator, pedals, DIY boards ...).
- **Bases with exchangeable grips** (Gunfighter, Rhino, WarBRD ...) report the same USB id for every grip. If the
  product name does not name the grip, JoyPro lists the grips with their pictures and you pick one:
  `--grip "VPforce Rhino FFB Joystick=vpforce-rhino-tm-f16-grip"`.
- **Unknown devices** get a generic layout from what Windows reports (axes, buttons, hats). Their kind is guessed from
  the name; keyboards, wheels and gamepads are left out.
- **Devices that are not connected** can be added from the catalog with `--add-device <id>`. Their binds use a
  placeholder device; exchange it for the real device once it is connected.
- **One device per hand.** A relation has one bind, so a second right-hand stick (for example a Rhino next to a VKB) is
  left out as an alternative. The device your profile already uses wins, then devices you added, known layouts, a
  throttle over a left-hand stick. Use `--ignore-device` on the other one to switch.

## How functions are placed

1. **Your own layout first.** With `--overwrite`, every relation goes back on the control it had before (same
   product, same input, same shift state), also after Windows gave the device a new id. `--no-style` ignores it.
2. **Strong matches.** A function goes to its own position: TMS up on the TMS hat, pickle on the weapon release, gear
   on the gear lever, ANTI SKID on the switch labelled ANTI SKID. Labels also decide the direction (LAUNCH BAR EXTEND).
   A label names a function only with its whole meaning: words that change it (WARN, SILENCE, HORN, TEST, LIGHT,
   BRT / DIM, ALT, EMERG) must be the function's own too, so "LDG GEAR WARN SILENCE" is no place for the gear and
   "ALT RDR" (the radar altimeter) none for the radar (`SetupPhases.LabelNames`). Cockpit labels printed on a base
   (all capitals, like the Warthog throttle's "APU START" or "EAC ARM") take other functions only once the unlabelled
   inputs are used up (a strong penalty; the review says "on a labelled switch (APU START): no free unlabelled input
   left"), and never a function the label contradicts or a time-critical / weapon function (MASTER ARM, weapon release,
   countermeasures ...). Descriptions ("left base 1") and mode positions ("MODE 2") are no such labels.
3. **Groups stay together.** The four directions and the press of TMS, DMS, CMS, trim, slew, sensor select, weapon
   select and comm go on one hat, each on its own direction (TMS up on up, press on press). A directional set goes on
   one hat or is not placed: a direction never lands on another hat or a plain button as a last resort, and the plan
   says why ("no hat with the free down direction for its set (TMS)"). Members already in place (your own binds)
   decide the hat for the others; a set the profile has only one member of still goes on its direction. Slew and TDC X/Y go on one thumb stick. On/off pairs (gear, master arm, hook,
   launch bar ...) go on both positions of one toggle switch.
4. **Everything else** goes where it fits best. The function placed first is the one with the fewest good options.
   Frequent functions go on the grips, rare ones on the base or panels.

## Flight phases

Auto Assign ranks every function the way an experienced pilot would. The Setup wizard's planner and Lay out with AI
([layout-with-ai.md](layout-with-ai.md)) use the same ranking. The ranking has two parts, both kept in
`DB/Functions/slot_controls.tsv` as the columns `phase` and `crit` (written by `tools/slot_phases.py`):

- **Flight phases.** Each function belongs to one or more phases: `pilot` (flying the aircraft), `tol` (take-off and
  landing), `nav`, `a2a`, `a2g`, `systems`, `comms` and `view`. How much each phase matters depends on the aircraft's
  role (`aircraft.json`). A fighter's air-to-air weapons come before its air-to-ground ones. An attack aircraft and an
  attack helicopter rank air to ground first. A transport puts navigation and systems before weapons. Spaceships rank
  combat first. A function shared by several aircraft takes the average over them, weighted by their priority.
- **Time criticality.** How fast a function must be reached: `instant` (trigger, CMS, TMS, flight axes), `seconds`
  (gear, flaps, trim, sensor select), `minutes` (lights, autopilot and radar modes) and `idle` (start-up).

Together they rank a function's importance (frequency x phase weight for the role x criticality 1.5 / 1.2 / 0.85 / 0.6).
Reach follows from them:

- Time-critical functions get a bonus on the grips and pay more behind a shift button.
- Functions that can wait go to the base and the panels when there is room.

## Special devices

- **MFDs** (catalog kind `mfd`, controls `mfd.*`) take only MFD functions, each on its own OSB position: the left MFD's
  T1 on T1. When an aircraft has more displays than you have MFDs, a free MFD takes the same OSB of another display. An
  aircraft without MFD functions gets nothing on an MFD.
- **Printed labels.** A panel control with a printed label (LDG GEAR UP, MASTER ARM SAFE, "1 T-ILS" on a keypad) takes
  only the functions the label names. A label is matched to a function in three ways:
  - by the function's label keywords: whole words, `^1` (the label starts with 1) or `=A` (the label is exactly A);
  - by the position word, so that LDG GEAR CENTER is never gear up and HOOK DOWN gets tail hook down;
  - by how much of the label the keywords explain, so JETT SAFE takes "Jettison safe" before "Jettison".

  Generic labels (B1, SW1 UP, Key 01, "5-position rotary") count as unlabelled.
- **Cockpit replicas** are panels where at least half the controls, and at least four, carry printed labels (WinWing
  take-off panel, UFC, ICP, combat ready panel ...). Their unlabelled controls take nothing except a function whose own
  position they are. Generic button boxes take anything.
- **Audit.** The [combination matrix](#combination-matrix) report lists, per replica panel of the catalog, the printed
  labels that no function names. Those controls stay free until the function catalog has a keyword for them.

## Phase layers

A latched switch on the throttle or collective base can become a **mode switch**, like the master mode of a real
fighter. It can also be a mode selector or the yoke's mode switch.

- **Positions.** Up is the air-to-air layer and down is the air-to-ground layer. The centre of a 3-position switch is
  the normal layer. With only one position, the layer goes to the phase whose functions matter less.
- **What goes on a layer.** Each layer takes only functions of its phase (weapon select and radar lock on A2A; DMS,
  laser and targeting pod on A2G). Flying, take-off, landing and every function used in both phases stay on the normal
  layer.
- **Sharing an input.** One input can hold an A2A function on the up layer and an A2G function on the normal layer.
  That works because the normal function is not needed while the switch is on air to air. A function needed in every
  phase never shares its input with a layer.
- **Choice of switch.** The best switch is one labelled MODE, otherwise a 2-position switch on the throttle or
  collective base, otherwise one on a stick base, otherwise an unlabelled panel switch. Switches with a fixed role (boat,
  dogfight, speed brake, pinky) and labelled cockpit switches are never used.
- **In DCS** each position is a switch modifier (`sw = true`).
- **Budget.** The layers come on top of the shift modifiers (no hand holds them), with at most two.
- **The option.**
  - `--phase-layers auto` (default): only when the busiest aircraft has more grip functions than grip buttons and some
    of them belong to one combat phase. The Setup wizard keeps the layers only when they place more essential
    functions, or more functions of one phase, than the plan without them.
  - `on`: always, when the setup has such a switch.
  - `off`: never.
  - The GUI choice ("Mode switch: Automatic / Always / Off") is in the Auto Assign panel and in the Setup wizard's
    priorities step, and it is kept in the settings.

## Modifiers per game

A modifier layer only takes relations whose games read modifier binds (`engine games support`, column Modifiers):

- **Yes**: DCS, IL-2, Star Citizen, War Thunder, Elite ...
- **Partial**: BMS, MSFS ... These take shift layers, but no phase layers.
- **No**: X-Plane, Arma ... These never go behind a modifier. A relation that has a command of such a game stays on the
  normal layer.

Rules that always hold:

- Trigger, weapon release, pinky and detents only take functions made for them.
- Flight axes and self-centring thumb sticks only take the axes made for them (no RPM on a ministick).
- MFD keys only go on MFD keys, and cockpit panel functions stay off the grips.
- **Inputs are shared between aircraft.** One input serves several relations as long as they have no aircraft in
  common, for example the left throttle of the F/A-18 and of the MiG-29. DCS input layers (UI, NS430, voice) count for
  every DCS aircraft. Every other game has one input configuration for the whole game.
- **At most two modifier layers**, and only if the busiest aircraft has more grip functions than grip buttons. The
  shift button your profile already uses comes first, then the pinky lever or button. Phase layers on a mode switch
  come on top (see above).
- **Slew is not TMS.** Slew directions never go on a TMS hat. TMS, DMS, CMS and sensor-select hats never go on a thumb
  stick unless their own position list names it (TMS on a stick's mini-stick when the grip has no TMS hat).

## What it changes

- **Bound relations** keep their binds; only unbound relations are assigned. `--relation` limits it to some relations.
- **`--overwrite`** re-lays out the binds on the devices of this setup. Binds on the keyboard, virtual devices (vJoy)
  and devices that are not connected stay. `--include-missing` also moves the binds of devices that are not connected
  (a replaced device).
- **"Not Assigned"** lists functions with no fitting free input (for example MFD keys without an MFD).
- **`--explain <relation>`** shows where a relation is best placed, its previous control and the best candidates with
  their score and the relations of the same aircraft that already use them.
- **Plain words.** The app shows every input and control in plain words ("Hat 1 down (TMS hat down)", "Base button
  25", the printed label), never an input or control id; the CLI's `--text` output keeps the ids for scripts.

## Devices without published button numbers

For many configurable devices (Virpil, WinWing, MOZA grips, Rhino grips) the button numbers depend on the firmware
profile and are not published; their catalog entries have `confidence: low`. Auto Assign still uses the inputs Windows
reports, and your previous binds tell it where things are. A guided "press the control" teach mode is planned.

## Combination matrix

`gui/JoyPro.Gui.Tests/AutoAssignMatrixTests.cs` runs every combination twice. First `engine setup plan` makes a plan,
which `engine setup check` (`SetupPlanChecks`) checks. Then `engine relations auto-assign --overwrite --no-style` lays
out the same relations again, and its assignments are checked as well.

The combinations use device sets made of real catalog devices (`engine/tests/fixtures/autoassign-matrix/*.tsv`, fake
devices through `JOYPRO_FAKE_DEVICES`):

- a stick alone;
- a HOTAS in one piece (also with X-Plane, which has no modifier binds);
- small stick + throttle;
- medium stick + throttle + pedals (also with phase layers on);
- a pit with take-off panel and two MFDs (also with UFC and combat ready panel);
- two sticks for space;
- a collective;
- yoke + quadrant;
- a small set with a mode switch;
- unknown devices;
- the author's pit.

The checks:

- C1 no input for two relations of one aircraft (the normal and phase layers included);
- C2 at most 2 shift modifiers and 2 phase layers;
- C3 trigger / weapon release / pinky only take their own functions;
- C4 MFD keys only take MFD functions, in OSB order;
- C5 printed panel labels only take the functions they name; a base's cockpit label never takes a function it
  contradicts or a time-critical / weapon function;
- C6 slew is not TMS;
- C7 CMS forward dispenses, left / right change the program;
- C8 one device per hand;
- C9 phase layers only take their phase;
- C10 no modifier layer for a game without modifier binds;
- C11 a directional set (TMS, DMS, CMS, trim, 4/8-way + press) sits together on the matching directions of one hat,
  or is not placed at all. A direction never lands on another hat or a plain button as a last resort; the plan lists the
  set as not placed with the reason.

Measures: placed functions, essential functions of the top aircraft, time-critical functions on the grips, modifiers and
phase layers. The same plan is made twice and must be identical.

- Always: three sets, plus the same plan twice.
- Full run: `set JOYPRO_AUTOASSIGN_MATRIX=<folder> & dotnet test gui/JoyPro.Gui.Tests --filter AutoAssignMatrixTests`.
  It writes `report.md` (a table per combination, the notes, and the panel label audit) and each plan with its checks.
- The engine smoke test runs four sets of it (`engine/tests/smoke/autoassignmatrix.ps1`).

## Where the data comes from

- `DB/Devices/*.json`: the device catalog, one file per vendor (format: [devices.md](devices.md)).
- `DB/Functions/slot_controls.tsv`: for every function slot of the Auto Group catalog, its side (stick, throttle,
  panel), the controls it belongs on in order of preference, latched pairs (`gear:in`), panel label keywords, how
  often it is used, the hat or 2D group it belongs to (`tms:up`, `tdc_axes:x`), its flight phases and its time
  criticality. `tools/slot_phases.py` writes the phases, the criticality, the keypad labels and the MFDs' OSB order; run
  it again after the catalog generator rewrote the file.

To set up the relations and binds for the games you watch in one go (device roles, aircraft priorities, modifiers, realism, review), use the [Setup wizard](setup-wizard.md).
