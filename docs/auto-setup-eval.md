# Auto setup evaluation

`engine setup eval` is the test bench of the [Setup wizard](setup-wizard.md)'s planner: it plans many real device
setups for every game the wizard knows and the main DCS aircraft families and scores every plan automatically. It runs
offline in the engine process, reads only JoyPro's own catalogs and writes nothing but its report.

- Code: `engine/EngineSetupEval.cs` (setups, operations, hand rules, report), CLI [setup eval](../engine/docs/ENGINE_CLI_WIKI.md).
- Smoke test: `engine/tests/smoke/autosetup.ps1` runs a part of it with thresholds (popular setups and 6 random ones,
  6 DCS families, 7 games): a planner change that loses operations or breaks hand rules fails the smoke test.
- Full run: `engine setup eval --out-dir <folder> [--baseline <earlier results.json>] --text` (about 30 minutes with
  Auto Group / Fill all; `--no-pipeline` about 4 minutes).

## Setups

- **20 popular setups** (`EngineSetupEval.PopularSetups`), each a list of device catalog ids (`DB/Devices`):
  Thrustmaster HOTAS Warthog + TFRP, T.16000M FCS (stick, TWCS, TFRP), VKB Gladiator NXT EVO + STECS + T-Rudder,
  Virpil Alpha + MongoosT-50CM3 + ACE pedals, WinWing Orion 2 F-16EX, WinWing Orion 2 F/A-18 with take-off panel and UFC,
  Logitech X56, X52 Pro, TCA Airbus sidestick + quadrant, Honeycomb Alpha + Bravo + pedals, an Xbox controller alone, a
  Logitech Extreme 3D Pro alone, two VKB Gladiator NXT (space), two identical T.16000M + TFRP, Warthog + two Cougar MFDs,
  T.16000M + TWCS + a 32-button box, VKB + Hawk-60 collective + Crosswind, T.Flight HOTAS 4, VPforce Rhino with TM F-16
  grip + Warthog throttle, Logitech yoke + quadrant + pedals.
- **Random setups** (`--random N`, default 20): a **new seed every run** (time, process, counter), so every run tests
  other devices; the seed is printed, written into `report.md` and `results.json`, and `--seed N` repeats a run. A setup
  takes a real shape - stick + throttle + pedals, stick only, HOTAS, yoke + quadrant, dual sticks, gamepad, stick +
  throttle + panels / MFDs / button boxes, helicopter (stick + collective) - and for every device first a vendor, then a
  device of it, from the whole catalog (`EngineSetupEvalRandom.cs`). The popular setups stay fixed.
- **The regression set** (`engine/tests/fixtures/setup-eval-regression.tsv`, see
  [below](#random-setups-and-the-regression-set)): random setups of earlier runs that failed a core gate or bound
  something destructive, re-run in every eval.
- A device is given as Windows would report it: a fake device file (`JOYPRO_FAKE_DEVICES`, VID:PID, HID name, the axes,
  buttons and hats of its catalog entry; a grip by its base, picked like the wizard's grip choice). A catalog device
  without a USB id is added from the catalog, as the wizard's "add a device" does.

## In-flight essentials

The headline of every row (setup x game / aircraft): **in-flight essentials reached %** - the share of its game class's
essentials a pilot can work in flight - and the share of rows where **every time-critical essential** is there
(`EngineSetupEvalEssentials.cs`; `report.md` "In-flight essentials", `results.json` per row `Essentials`,
`EssentialsPct`, `CriticalComplete`). An essential counts as reached only when it is

- **where a hand is in flight**: on the stick / yoke grip or the throttle / collective side (its grip, base and levers), or
  on a printed lever / switch of its own (the Bravo's gear lever) - for the time-critical ones **without a modifier**
  (the gear may sit behind one: a deliberate two-finger action is safer than a grip button one slip away);
- on a **fitting input**: an axis function on an axis, a button function on a button, not on a lever detent or an axis
  zone, on a latched switch only as an on / off pair or what its label names, never on the opposite direction of a hat;
- **not destructive** (none of the row's destructive bindings).

Pitch, roll, yaw, throttle, gun, weapon release, countermeasures, wheel brakes and look take the hard gate's verdict
(below); gear, flaps, trim, airbrake, target and PTT are judged by the rules above. An essential the game has no command
for (for the scored aircraft) is n/a. Time-critical = everything except the four flight axes.

| Class (rows) | Essentials, in the order that must fit first on a small device |
|---|---|
| Jet (DCS jets, Falcon BMS, War Thunder, VTOL VR, Nuclear Option, WARDOGS, IL-2 Korea, Arma fixed wing) | pitch, roll, throttle, gun, yaw, wheel brakes, weapon release, countermeasures, trim, target (TMS up / radar lock), look, gear, flaps, airbrake, PTT |
| Prop (DCS warbirds, IL-2 Great Battles) | pitch, roll, throttle, gun, yaw, wheel brakes, weapon release, trim, look, gear, flaps, PTT |
| Helicopter (DCS helicopters, Arma rotary) | pitch, roll, throttle (collective), gun, yaw, wheel brakes, weapon release, countermeasures, trim, target, look, gear, PTT |
| Airliner / GA (MSFS 2020 / 2024, X-Plane 11 / 12) | pitch, roll, throttle, yaw, wheel brakes, trim, look, gear, flaps, airbrake (spoilers) |
| Space (Star Citizen, Elite Dangerous, X4) | pitch, roll, throttle, gun, yaw, secondary weapon, countermeasures, target, look |
| ACE COMBAT 8 (virtual Xbox pad) | pitch, roll, throttle, gun, yaw, missile, flares, target |

Looking around ranks right after the fight essentials and the trim (without head tracking it is a
fairly high priority on the HOTAS). With the wizard's head-tracking answer *yes* (`--head-tracking yes`) looking around
is n/a in the sims that support head tracking, and any look button bound there fails.

The order is also the priority on a small device (a gamepad, a stick alone): what must fit first. The planner follows it
(`SetupPlanner.EssentialRank`): a hat set of an earlier essential takes a hat from a later one or from a function that is
no essential (the trim takes an Extreme 3D's only hat from the TMS its catalog name suggests, the snap views take the DMS
hat), passes repeat until nothing moves; gear up / down and flaps up / down stay together on one switch (a base toggle,
a grip's latched two-way switch such as the Warthog's boat switch) or on two buttons of one device; a gear or flaps
toggle and TDC slew buttons give their input back where both directions or the slew axes are placed; the only member of
a set the aircraft has (ACE COMBAT's target switch) takes any free button. The hard gates' small-device waiver uses the
same order (gun, wheel brakes, release, countermeasures, gear, flaps, trim, airbrake, target, look). The reference-profile
suggest hit rate (relations suggest against a filled reference profile) is only a non-regression guard for suggestions, not a
quality headline.

## Where functions go

The setup wizard's slider *Where should functions go?* runs from **Same button for the same job in every aircraft** (0)
to **Where the real aircraft has it** (1) (`SetupOptions.Realism`, CLI `--placement same-button|real-aircraft|0..1`, old
name `--realism`; the eval plans at `--placement`, default 0.5). `--placement-sweep 0,0.25,0.5,0.75,1` plans the popular
setups' typical DCS hangar (20 modules in one plan) at each position (`EngineSetupEvalPlacement.cs`, `placement.md`,
`placement.json`) and measures per position:

- hard gates, destructive bindings, in-flight essentials;
- **cockpit-position fidelity %**: how close the functions sit to the real aircraft's HOTAS (the cockpit database
  `DB\Cockpits`; the mean real match of the functions whose real position is known), per aircraft too;
- **cross-aircraft consistency %**: per function bound for two or more modules, the share of them that have it on the
  one input most of them use.

Checks (the eval fails otherwise; the smoke test runs the sweep on three popular setups): fidelity never falls and
consistency never rises (1 point tolerance) as the slider moves to the real aircraft, fidelity at 1 is at least 5 points
above fidelity at 0 (`PlacementMinEffect`: an effect clearly larger than the tolerance), no destructive binding at any
position, and the gun and the throttle never fail at one position when they pass at another. With three positions or
more, every step (the last one, to 1, too) changes fidelity or consistency by at least `PlacementMinStep` (0.5
points), no step carries more than
`PlacementMaxStepShare` (60 %) of the fidelity gain - a slider, not a switch at 0.5 - and no position's hard-gate pass
rate is more than `PlacementMaxGateDrop` (2 points) below the best position's. The gun, the throttle, the weapon release and
the countermeasures never fail at one position while they pass at another (round 8, L30), and the sweep also plans the
F-16 and the A-10C alone on the run's random and regression setups (`--setups popular,random`), not only on the popular ones.
The sweep also plans the other games alone on the popular and random setups at every position (`SweepGames`: X4, Elite
Dangerous, Star Citizen, IL-2, MSFS 2024, Falcon BMS, War Thunder, Arma 3 as helicopter and plane; review 7 M12 / L41):
pitch, roll, yaw, throttle, the gun, the release, the countermeasures and the switches gate (nothing on a switch printed
for another function) must hold at every position where they hold at one. The space games' cockpit entries leave the
stick's left / right and twist to roll and yaw (X4's and Star Citizen's own presets steer yaw with the stick's X; a yoke
or a stick without twist then had no roll), and Elite's preset spot for the hardpoints (the Warthog's LDG GEAR WARN SIL,
SYS or RESET on other throttles) is not copied.

The real positions take over function by function as the slider moves (`SetupPlanner.RealThreshold`): from 0.25 the
HOTAS sets used often (TMS, DMS, CMS, trim, comm), from 0.5 the buttons used often, from 0.75 their axes and the
occasional HOTAS functions and axes, at 1 the occasional cockpit buttons (switches of the real cockpit, not of its
HOTAS) and every other function; the continuous pull (`RealismWeight`) grows with it. Snap views, countermeasures,
gear and flaps are planned per aircraft at every position (one shared set for 20 modules found no input free in all of
them). Real positions never push the time-critical essentials off the grips (round 7, `RescueGripEssentials`): a
weapon release or wheel brake button left on a base or behind a shift takes a grip button back from a function that is
no in-flight essential or from a later one (countermeasures and after: the grip order gun, wheel brakes, release,
countermeasures), and a stick with three grip push buttons or fewer besides the trigger (TCA sidestick, T.16000M)
gives none of them to a JoyPro shift.

The real-position match uses the device catalog's **HOTAS equivalence** (`"hotas"` on a control in `DB/Devices`: which
real F-16C / A-10C II HOTAS position a generic control stands in for, taken from DCS World's own default profile of
that device - a T.16000M's only hat is the trim hat, the TWCS rocker is the antenna elevation) and the per-aircraft
position equivalence `DB/Functions/hotas-equivalence.json` (the F/A-18's sensor control switch sits where the F-16's
TMS sits, from ED's HOTAS Warthog default profile of each module). A device without such data only matches "same
hand", which is why the slider changes little there; the wizard says so under the slider. The gun stays on the trigger
at every position (the trigger relations are never split by real position).

## Random setups and the regression set

- Every run draws new random setups (above). A random setup that fails a **core gate** (pitch, roll, yaw, throttle, fire,
  wheel brakes) or produces a **destructive binding** in any stage is saved to the regression set with its seed, shape,
  device list and what it failed (`gate:target`), one setup per new kind of failure (gate and game; DCS counts as one), so
  the set stays small and varied. It is written only when the file is named with `--regression-file <file>`; a plain run
  lists the setups to add.
- Every eval re-runs the regression set (`--setups` without `regression` leaves it out; `--no-regression` too). Its rows
  are reported by themselves ("Regression set") and do not count in the popular / random rates. What a saved setup failed
  and now passes is marked fixed in the file; **a fixed failure that comes back fails the eval** ("was fixed and fails
  again"), and so does any destructive binding of a regression setup.
- The file started with the 20 random setups of seed 2026 that every run before round 4 used.
- The smoke test draws 6 fresh random setups every time and runs a copy of the regression set; when a fresh seed fails,
  the output names the seed and the setup's devices (`random-03 (seed 581245564, hotas: ...)`): repeat it with
  `--seed`, fix it, and add it with `--regression-file engine/tests/fixtures/setup-eval-regression.tsv`.
- Options: `--seed N`, `--regression-file <file>`, `--no-regression`, `--min-regression-hard-pass <pct>`.

## Targets

Every game the Setup wizard plans (a function table of its own, what `setup games` marks "Wizard"; since review 6 all 36):
MSFS 2024 and 2020, X-Plane 12 and 11, IL-2 Great Battles and Korea, Falcon BMS, War Thunder, VTOL VR (BYOJoystick and
native), Nuclear Option, WARDOGS, Star Citizen, Elite Dangerous, X4, Arma 3, Arma 2, Arma 2 OA, Arma Reforger, FSX and FSX
Steam Edition, Prepar3D v4 / v5 / v6, IL-2 1946, Cliffs of Dover and its Blitz edition, Tiny Combat Arena, G-Rebels,
Project Wingman, ACE COMBAT 7, Battlefield 6 and 2042, HLL Vietnam (its virtual Xbox pad's helicopter axes) and ACE
COMBAT 8 (on the virtual pad, below). Near-duplicates count as one game in the means (FSX / FSX SE, the three Prepar3D
versions, Cliffs of Dover / Blitz, the two Battlefields, MSFS, X-Plane, Arma 2 / OA). Arma 3 flies planes and helicopters with
one configuration: it is scored twice (`Arma3` as a helicopter, `Arma3:plane` as a plane, half the weight each), each row
without the other class's own actions (the catalog's category Helicopter / Plane), so a missing plane throttle or
collective fails its row (review 7 M11 / L40). Its throttle and collective share one lever (both actions on it). **Wizard flight axes**: on the
typical stick + throttle popular setups (Warthog, T.16000M, VKB + STECS, Virpil, Orion 2 F-16, X56, X52 Pro, T.16000M +
button box, T.Flight HOTAS 4, Rhino + Warthog throttle) every wizard game's row must pass pitch, roll and throttle (or its
collective / thrust); any miss fails the eval (`FlightAxesFailures`, the smoke runs it on three setups). DCS per family, with the family's aircraft
as the owned modules (`setup plan --only-aircraft`) and the first one scored: F-16C, F/A-18C, A-10C II, F-14, F-15E,
F-4E, AV-8B, M-2000C, FC3 (F-15C first), AH-64D, Ka-50 III, UH-1H, Mi-8, P-51D, Spitfire, Bf 109 K-4; and one row with
every DCS module at once (F-16C first).

The planner runs with the wizard's defaults: no modifier wanted, as few as possible, **Where should functions go?** in the middle (50 %), phase layers
automatic, the scored aircraft as the only priority.

## Operations (coverage)

An operation counts for a target only when its game has a command of it for the scored aircraft (the knowledge map
`DB/Functions`); it is done when one of its alternatives is bound completely. Coverage = done / counted operations (a
partly done one, gear up without gear down, counts half).

Flight axes (pitch, roll, yaw), throttle or collective, propeller pitch / RPM and mixture (fixed wing, where the game has
them), gun / primary fire, weapon release, weapon select, landing gear,
flaps, airbrake, wheel brakes (toe brakes or a wheel brake button), trim (pitch both ways, or force trim), view / zoom /
look around (zoom axis, snap views on a hat, look axes), comms, countermeasures, sensor / cursor
slew, target lock / designate, master modes; space: strafe left/right and up/down, boost, power distribution, shields,
quantum / jump drive. Master arm is reported but not scored (a cockpit switch in most aircraft). The alternatives (function
ids) are listed in every report.

## Hand rules (ergonomics)

1. One device per hand.
2. Trim on a hat (or a yoke's trim rocker) of a hand device.
3. Trigger and weapon release on the right-hand device.
4. Countermeasures on the left-hand device, or on the stick's own CMS hat (F-16, A-10C grips).
5. Comms on the left-hand device (or a stick's mic hat).
6. Axes on fitting axes: pitch / roll on a stick's or yoke's axes, yaw on pedals or a twist, throttle on a lever,
   slider or a left stick's Y, toe brakes on pedals.
7. Time-critical functions (`instant` in `slot_controls.tsv`) on a hand device, without a modifier.
8. No axis function on a button.

9. Lever detents only for the lever's own functions (idle / cut-off, reverse, afterburner, autothrottle / TOGA, engine
   start and stop).

Ergonomics = rules kept / rules that apply. Score = 0.75 x coverage + 0.25 x ergonomics. The means are weighted: MSFS
2020 / 2024, X-Plane 11 / 12 and Arma 2 / 2 OA count half each (one game in two versions).

## Hard gates

Coverage counts a binding anywhere; the hard gates (`engine/EngineSetupEvalGates.cs`) count only what a pilot can use
right away. A row passes when every gate that applies is met and no destructive binding is found; the report has the
pass rate per stage, gate and setup and every failure with its reason. `n/a` is said with its reason (the hardware has
no such control, the game has no such command).

- **pitch / roll** on the stick's (yoke's) own pitch / roll axis.
- **yaw** on the rudder pedals when there are pedals (aircraft), else a twist, rocker or thumb stick X.
- **throttle** on a lever (a slider only when there is no lever; a rotary knob, thumb wheel or thumb stick never while a
  lever exists); every engine throttle of a twin on such a lever (no left engine on the lever and the right one on a
  spring rocker); a left stick's Y and a gamepad count. A setup with no throttle at all fails in every flying game (it is
  no "n/a": the game needs one).
- **fire**: the gun on the trigger, not behind a modifier or a mode layer. Where the trigger's first stage is a detent
  before the gun (the catalog says "first detent", "half action", "sight recorder": F-16, A-10C, F-14, Mirage ...), only
  the second stage counts; in combat games without a gun command in the catalog the gate fails ("catalog").
- **release** (weapon release) and **countermeasures** on a grip, not behind a modifier. Countermeasures count only with a
  command that dispenses - CMS forward or aft; CMS left / right / ECM select programs or jammers (the Mi-8's UV-26 SERIYA /
  ZALP) - and are n/a for warbirds and airliners (the Bf 109's flare pistol is a signal flare, now a catalog substitute).
- **wheel_brakes**: both toe brakes on brake axes (one brake lever for both counts) or the game's wheel brake button,
  unshifted. One toe brake alone or a toe brake axis command on a button does not count.
- **gear** and **flaps** bound.
- **look**: all snap views on one hat of a grip without a modifier (looking around is time-critical), or both look axes
  on axes that spring back to the centre; zoom alone does not count. Snap views behind one modifier, all on one hat, count
  ("slow") on hands' devices with fewer than two four-way grip hats, or when every grip hat holds an earlier essential
  (trim, target, countermeasures) or, from the middle of the slider on, its real aircraft function (the Warthog in the
  A-10C). In a sim whose camera JoyPro does not manage (MSFS, X-Plane, Falcon BMS, Arma: no look command in their lists)
  a grip hat must be left free for the game's own look-around (n/a on a device with one hat). With `--head-tracking yes`
  the gate is n/a in the head-tracking sims and fails when a look button is bound there (the padlock and Star Citizen's
  look-ahead count as look buttons, L24) or when the recenter is not bound without a word: where no input is left for it the
  plan must say "Head tracking: no input is left for the view recenter ...: use the recenter key of your tracking software"
  (round 8, review 6 M1). Without head tracking, one grip hat held by an earlier essential and no button a shift may use is
  a hardware limit only when the plan says so ("Looking around: no free hat and no button for a shift ..."). The small-device waiver
  applies to the look gate only once both shift layers JoyPro may add are used: no look-around while a layer is free
  fails.
- **kinds**: no axis command on a button and no button command on an axis (the wheel brake button is a button command,
  also where it is IL-2's `rpc_wheel_brakes`); a centred rate (TDC / cursor slew, strafe sideways or up / down, head
  look) only on a centring axis (a stick, thumb stick, twist, rocker), never on a lever, slider, rotary or wheel that
  stays where it is left (an axis the catalog types `centring` - the T.Flight / TWCS rudder rocker - centres); a
  throttle never on a spring-centred thumb stick, twist or centring axis (a gamepad and a left stick's Y excepted); one
  thumb stick never in both modes (L19: trim on the VKB A1 hat and look axes on its analog mode - the planner keeps the
  device's default mode: a POV hat mode, else the analog axes).
- **switches**: a latched switch holds only an on / off pair or what its label names; a printed function label on a base,
  a panel **or a grip** holds only what it names (the Warthog throttle's SPEED BRAKE is the speed brake's, never the
  gear's; a label that only names the control - BOAT SWITCH, CHINA HAT, COOLIE SWITCH - is no function label); nothing
  on a position of a mode / profile selector (catalog type `selector`: the X56 MODE wheel M1 / M2 / S1, the X52 MODE
  1 / 2 / 3, the STECS 5-position mode switch, the TCA quadrant's ENG MODE knob): one position is always held; an axis
  printed "Flaps" holds only flaps (the axis labels reach the planner since round 7), a printed gear lever only the gear;
  afterburner / TOGA / WEP only on a detent known to be at the lever's top end (L20: never the Honeycomb Bravo's or the
  Logitech quadrant's reverse / below-idle detent, never a numbered TCA detent); canopy, tail hook and refuel probe only
  on a latched switch whose label names them.
- **modifiers**: a JoyPro shift button is a momentary push button without a printed function label - never a lever
  detent, an axis zone button (the TCA sidestick's "thrust reverser" zone), a latched switch or a selector position; a
  mode switch for phase layers is no switch labelled for a function (ENG OPER L, ENG 1 MASTER, L GEN, ATEM ARMED, RUD
  TRIM) and no guarded switch; a shift is reachable while flying (L22): never on the stick's base while the left hand
  holds a throttle, collective or left stick (the planner takes the pinky, a throttle grip button, the stick's index
  button only on a stick with more than three grip buttons, a stick base button only without a throttle). The user's own
  modifiers are not judged.
- **Destructive bindings** (zero tolerance; the engine-switch kind is the eval's own judgement of the engine-switch rule,
  `EvalEngineSwitch`, not the planner's `EngineSwitchMisuse` - round 8, L28): engine start / stop, jettison, eject, canopy, gear on a lever detent (engine
  start / stop on an idle / off detent is fine), on a switch whose printed label names another function, on a grip
  hat or on a mode selector position; an engine master / starter / magneto **toggle** or a **held** starter on a latched
  switch ("on a latched switch": MSFS Engine Master 1 Toggle, X-Plane engage starter); a one-way engine start / stop /
  cut-off on an engine-unfit switch position (L18, review 5 B5; round 8 B5b: a stop only where the printed label names
  the stop - STOP, OFF, CUT-OFF, SHUTDOWN, MOTOR - and no start, never "L ENG AUTO IGNITION" and never an unlabelled panel
  switch, as the canopy; a start never on OFF / STOP; the centre of a 3-way switch, a position labelled for
  another system - the MOZA MTP's "BAT center" -, ENG CRANK as a stop or for the other engine, a position of a switch
  whose other positions carry unrelated functions - the gear on the STECS STEM SW1 up / down); and any two opposite
  functions on one input (engine start and stop, light on and off) unless both are the game's one toggle command.

Thresholds: `--min-hard-pass <pct>`, `--min-popular-hard-pass <pct>`, `--max-destructive N`. The quick path must be
lossless for the essentials (round 8, review 6 B9 / B6, L27 / L34): an essential gate (pitch, roll, yaw, throttle, gun,
weapon release, countermeasures, wheel brakes) that passed for a setup and target in the setup alone and fails in the
all-games plan, or passed in the all-games plan and fails after Auto Group + Fill all, fails the run (zero tolerance,
`--max-all-games-lost N`, `--max-pipeline-lost N`; the losses are listed in `report.md` / `results.json` `StageLosses`;
the MSFS throttle on a slider after Auto Group is a known limitation, listed apart). `--pipeline-zero-gates
<gates>` fails any popular row of the all-games plan or of the pipeline that fails one of those gates;
`--min-all-games-hard-pass <pct>` holds the all-games plan's rate. `setup eval --help` lists every option. Per-gate non-regression:
`--gate-baseline <json>` compares the popular setups' rows that pass each gate (`gate:look`) and reach each in-flight
essential (`essential:ptt`), and the destructive bindings, with a stored baseline (`--write-gate-baseline <json>`
writes this run's counts; the popular setups are fixed, so the counts repeat exactly): a drop in look, PTT or flaps
fails even when the averages hold. The smoke uses `engine/tests/fixtures/setup-eval-gate-baseline.json` and hard-gate
thresholds at the measured value minus 2 (all rows 80 %: the fresh random setups swing that rate).

Devices reported with a USB id and name JoyPro does not know (`ursa-unknown`) are judged on the real device: every
binding of the generic layout is looked up in the device's catalog entry, so the thumb wheel is a thumb wheel and button
1 a base button, whatever the generic layout called them.

VTOL VR's native input has rudder and toe brakes only: without pedals the row is left out (`n/a`) instead of scoring
0 %. ACE COMBAT 8 (the virtual Xbox pad, `--games AceCombat8`) has no setup table: after Auto Group and Fill all it is
scored by `engine vjoy pad-check` (throttle split on the triggers, yaw, the flare chord, the menu D-pad) plus the pad's
own pitch and roll from the stick axes, the gun on the trigger and missiles on a grip.

## After Auto Group and Fill all

The setup is only the start: users then run **Auto Group** (`relations auto-group`) and **Suggest missing** / **Fill
all** (`relations suggest --missing`, every suggestion taken). So per setup the harness also makes one profile the way a
user who owns every game and DCS family of the run would: the setup for all games at once (wizard defaults, the
families' aircraft as owned modules), Auto Group (preset all, with substitutes, the owned aircraft and games), then
every suggestion of medium confidence and up taken for every relation. The profile lives in memory only. Each target is
scored again from the profile's binds on the setup's devices with the same operations and hand rules, plus:

9. **No wrong-kind matches**: no command of the scored aircraft in a relation whose function is another, unrelated one
   (a brightness knob in a slew relation, a wheel brake in a throttle relation, decelerate in a wheel brake relation).

What a relation is: the function its commands are by the knowledge map (`DB/Functions/function-map.json`), each command
voting once for every function it has with weight 0.5 or more, taken before Fill all (suggestions taken later do not
change it); the function its catalog slot names wins when one of its commands is it. A command fits when one of its
functions is that function, shares a command with it (the F-16's "Nosewheel Steering - Lasing" button is both), is
related to it, or is linked to the relation's catalog slot too. Otherwise it is **another function of the same kind**
(same category of the map: reported, not counted) or a **wrong kind** (another category: counted). Catalog substitutes
(status `substitute` in `DB/Functions`: on purpose the closest function where the aircraft has none of its own, e.g. the
UH-1H's search light on the CMS switch) are listed apart and not counted.

The report lists every wrong suggestion Fill all took (relation, its function, the command, its function, the
aircraft, the suggestion's own reason), every wrong-kind command per target, the same-kind ones and the substitutes.
Thresholds: `--min-pipeline-coverage`, `--max-wrong N`.

## Novice case

Named and reported on its own (`persona.md`, `persona.json`): someone new to sims, IL-2 Great Battles is the only game,
a WinWing URSA MINOR stick with the matching throttle, who takes the quick path at the first start
([setup-wizard.md](setup-wizard.md#quick-path)). Setups: Fighter stick R + Combat Throttle, Airline stick R + 32 Airline
Throttle, Space stick R + Combat Throttle, and the Fighter set reported with a USB id and name JoyPro does not know
(`ursa-unknown`: the generic path). Each is planned with the wizard's defaults for IL-2 alone and again after Auto Group
and Fill all. `persona.md` has per row the novice's operations (pitch, roll, yaw, throttle, prop pitch, mixture, guns,
bombs / rockets, gear, flaps, wheel brakes, trim, looking around, comms: yes / partly / no / n/a), the hard gates, what
the setup tells the user about the devices, and every binding. The run fails when a row misses a hard gate or has a
destructive binding (the unknown set judged on the real device), or when the unknown devices are not set up generically
and named in plain words. No gate is exempted by a setup's id (round 8, L31): a one-hat device without a button for a shift
is a said hardware limit by the look gate's rule, and the gates that need the trigger's and the grip buttons' positions are a
known limit (persona table: "known limit (generic layout: the capture step)") only for a setup whose devices report a USB
vendor no catalog device has. The GUI side of it:
`gui/JoyPro.Gui.Tests/QuickSetupPersonaTests.cs`.

## ACE COMBAT 8 player (pad persona)

`engine setup grip-sweep [--games DCS,IL2Game,StarCitizen] [--only-aircraft F-16C_50] [--only <catalog ids>]` (review 7
B12 / L44): every catalog stick or grip with a numbered trigger is planned alone (catalog only, no connected device); the
gun of every game must land on a trigger without a modifier, else the exit code is 1 and the grip is named. The autosetup
smoke part runs it.

`engine setup eval-pad [--out-dir <folder>]` (`EngineSetupEvalPad.cs`, `pad-persona.md`, `pad-persona.json`): someone who
flies only ACE COMBAT 8 with a HOTAS and takes the pad path. Setups: Thrustmaster T.Flight HOTAS 4 (`ac8-tflight`) and VKB
Gunfighter MCG Pro + Virpil MongoosT-50CM throttle + MFG Crosswind pedals (`ac8-vkb-virpil`). The Setup wizard alone (its
defaults, the game alone, no Auto Group, no Fill all) plans and applies into a fresh profile; the row is scored with the
pad gates (pitch and roll on the stick axes, gun on the trigger, missile on a grip, then `engine vjoy pad-check`: pitch,
roll, gun, missile, target, the throttle split on both triggers, yaw on LB / RB, the flare chord, the High-G hold, the
wingman D-pad). Every gate must pass (no threshold); the bindings list where each pad action landed. Run by the
autosetup smoke part; the GUI side: `QuickSetupPersonaTests.AceCombat8TFlightHotas4_QuickPath_PutsTheFlightControlsOnThePad`.

## The report

`report.md` (method, summary per kind, target and setup, the operations missing most often with their most common
reason, the rules broken most often, the worst rows, the setups with the roles JoyPro proposed, every row),
`results.json` (every row with gaps, rule problems and the bindings of the scored aircraft), `rows.csv`,
`setups/<setup>.md` (per target what is bound where), `devices-*.tsv`. "Grip inputs no plan uses" (round 8, L29): per setup the
grip hats (all four directions) and grip buttons of the hands' devices that no setup-alone plan binds. Two identical devices
are told apart by role and instance in every bind list (L32). With `--baseline` the summaries show the earlier
numbers next to the new ones.

## Results 2026-11-14

Before = v2.6.5 (0b97f872) with the same harness, after = this branch; both runs in
`<eval folder>/2026-11-14` (coverage % / ergonomics % / score):

| Rows | before | after |
|---|---|---|
| Setup alone, all 1440 rows | 74.7 / 79.2 / 75.8 | 88.1 / 86.9 / 87.8 |
| Popular setups | 83.1 / 78.8 / 82.0 | 91.7 / 86.8 / 90.5 |
| Random setups | 66.2 / 79.6 / 69.5 | 84.5 / 87.0 / 85.1 |
| DCS families | 71.6 / 77.3 / 73.0 | 86.2 / 83.5 / 85.5 |
| Other games | 77.4 / 80.9 / 78.3 | 89.8 / 89.9 / 89.8 |
| After Auto Group + Fill all | 78.2 / 73.5 / 77.0 | 87.2 / 79.8 / 85.3 |
| Wrong suggestions Fill all took | 5860 | 261 |
| Wrong-kind commands in relations | 1716 | 557 |
| Novice case | failed | passed (Fighter, Space, unknown: 93.3 % coverage; Airline: 80 %) |

Still open: wheel brakes in IL-2 without pedals (its brake command is an axis command, no button bind is exported),
view / zoom and mixture on small devices (the Airline set), combined catalog slots that Auto Group fills across
aircraft (an AH-64 zoom in a radar range relation, AMPCD keys in MFD relations), VTOL VR native (yaw and brakes only).

## Fix round 1 (2026-11-14, after review 1)

`<eval folder>/2026-11-14\after-fix1` (README with every remaining failure and its reason). Before = the
review's state with the hard-gate harness:

| Rows | hard gates passed % before -> after | coverage % | destructive bindings |
|---|---|---|---|
| Setup alone, all 1427 rows | 26.2 -> 63.3 | 88.1 -> 89.8 | 815 -> 0 |
| Popular setups | 30.1 -> 73.1 | 92.1 -> 94.0 | 506 -> 0 |
| Random setups | 22.3 -> 53.6 | 84.1 -> 85.6 | 309 -> 0 |
| After Auto Group + Fill all | 4.2 -> 11.7 | 85.9 -> 85.9 | 1995 -> 347 |
| Novice case (4 variants, both stages) | 0 of 8 -> 8 of 8 | 90.0 -> 96.9 | 2 -> 0 |

Planner and catalog changes of this round:

- The gun on the trigger: where the trigger's first stage is a camera / laser / sight-recorder detent (the catalog's
  text says so), a single-stage trigger gets the second stage, and no other function of an aircraft with a gun.
- Gun, weapon release, flare / chaff, the wheel brake button and the comm switches / PTT never behind a shift button or
  mode layer; no time-critical function behind shift. Flare / chaff without a free hat: a plain grip button.
- Wheel brake button: DCS (77 modules, from each module's command list), IL-2 Great Battles and Korea
  (`rpc_wheel_brakes`, a key or button on that axis action is the game's own default), Falcon BMS (`SimWheelBrakes`).
  War Thunder has brake axes only. One brake lever brakes both wheels.
- Twin engines: one lever = the both-engines throttle (DCS "Throttle Both / Thrust" rows added), two levers = left and
  right on levers; no engine on a knob, wheel, thumb stick or slider while a lever exists. IL-2 keeps its all-engines axis.
- Rudder on the pedals when there are pedals (A-10C II rudder and toe brakes are pedal functions now).
- Lever detents only for the lever's own functions; a printed label (PTT, MIC, TRIM, ENG OPER L / R) keeps its control
  for its functions; engine start / stop, jettison, eject, canopy never on a grip hat; nothing irreversible on the
  buttons of a device JoyPro does not know.
- Unknown devices: a throttle with Rx / Ry and no X / Y has its levers there; a device of a known maker with the same
  axes as the maker's catalog devices of its kind takes over their axis roles, trigger numbers and every button they
  agree on. Catalog pedals or sticks with partly numbered axes get the usual numbers for the rest.
- Looking around: DCS view hat (View left / right / up / down) and absolute camera axes for every module; snap views
  count for the forced shift layer. Main's learned `look_*` slots are the same as `view_snap_*` / `view_look_*`, and
  `wheelbrake_left/right_button` the same as `wheelbrakes_button`: one set each.
- Star Citizen fires with `v_weapon_preset_attack`, IL-2's trigger fires all guns and its weapon release drops bombs,
  the Ka-50 III collective is the collective, the G940 pedals have their toe brakes.

## Fix round 2 (2026-11-14)

`<eval folder>/2026-11-14\after-fix2` (README with the open items). Before = round 1:

| | before | after |
|---|---|---|
| Setup alone, hard gates passed (all / popular / random) | 63.3 / 73.1 / 53.6 % | 70.0 / 82.1 / 58.0 % |
| After Auto Group + Fill all, hard gates passed | 11.7 % | 40.5 % |
| All games at once before Auto Group (new stage) | - | 35.4 % |
| Destructive bindings after Auto Group + Fill all | 347 | 21 |
| Wrong suggestions Fill all took | 5133 | 236 |
| Reference profile suggest hit rate (any / medium+) | 90.7 / 90.6 % | 90.7 / 90.6 % |

- Suggest: your style counts once per simulator family and only for the relation's own kind of function (else a low
  lead); Fill all never adds the opposite direction of the relation's function.
- Planner: snap views as a whole set, one set per game; no role discount for the fly-and-fight functions; trim weighted
  up; every jettison function off grip hats.
- Eval: the all-games plan before Auto Group is its own stage; a command's own kind decides wrong-kind; the jettison
  selector is not destructive; a documented grip priority (gun, brake button, release, flares, trim, gear, flaps, look)
  waives the lower gates of a device whose every grip button holds an earlier one.

## Fix round 3 (2026-11-14, after review 2)

`<eval folder>/2026-11-14\after-fix3` (README with the per-setup table and open items). Before =
round 2. The gates are stricter (see below), so before and after are not fully comparable:

| | before | after |
|---|---|---|
| Setup alone, hard gates passed (all / popular / random) | 70.0 / 82.1 / 58.0 % | 71.6 / 83.7 / 59.4 % |
| All games at once before Auto Group | 35.4 % | 45.0 % |
| After Auto Group + Fill all | 40.5 % | 47.0 % |
| Typical DCS player, 20 modules in one plan (new stage) | - | 49.6 % (popular stick + throttle setups 100 %) |
| ACE COMBAT 8 pad alone (new stage) | - | 67.5 % |
| Destructive bindings, setup alone / typical | 0 | 0 / 0 |
| Destructive bindings after Auto Group + Fill all | 21 | 57 (new detection: lever detent 41, one input 16) |
| Wrong suggestions Fill all took | 236 | 233 |

- Planner: nothing irreversible (eject, engine stop, jettison, mixture cutoff, canopy) on any grip or on a plain base
  button; a base input only when its label names the function. Slider zones and lever detents are marked in the
  catalog: zone buttons get nothing, detents only lever functions, an engine function only as the one-way cutoff of
  the detent's own lever (OFF/CUTOFF, never IDLE, never a toggle). Latched switches only take two-position functions;
  every labelled input is matched by its label (ENG 1/2 MASTER, ENG FIRE, TOGA, EJECT ...). The gun stays on the
  trigger with many modules; a second-stage trigger takes release, not another gun. Trim never behind the shift;
  rotary encoders only stepping functions; throttle never on a ministick or twist; DCS rotary RPM levers are no
  throttle.
- Catalog: VKB STECS throttle axis (Z) and ministick verified; Gladiator Mk.II pedal-port axes; vendor-same-shape
  devices borrow the buttons all agree on; slot name typos (Cursor, Airbrake, Parking brake).
- Eval: stages Typical (`--no-typical`, `--min-typical-hard-pass`) and Pad alone (`--no-pad-alone`,
  `--min-pad-hard-pass`; pad-check checks deduped into the pad gates); `--min-popular-hard-pass`,
  `--popular-zero-gates`, `--min-pipeline-hard-pass`, `--max-pipeline-destructive`; new `switches` gate (latched
  switch misuse, label conflicts); brake button must be on a grip when grips exist; the fire gate names what else is
  on the trigger; jettison is waived only when every command is a selector; destructive counted by kind; shift
  positions in the setup reports; a persona with a truly unknown maker (report-only until the capture step).

## Fix round 4 (2026-11-14)

`<eval folder>/2026-11-14/after-fix4` (README with the per-seed tables and the open items). Fresh seeds
every run; popular setups fixed:

| | round 3 | round 4 |
|---|---|---|
| In-flight essentials, setup alone (popular / random): reached, all time-critical | - | 91.5 % / 86.3 %, 47.4 % / 33.3 % |
| Hard gates setup alone, popular | 83.7 % | 90.0 % |
| Hard gates setup alone, random (seeds 504512613 / 575196760 / 251450249) | 59.4 % (seed 2026) | 70.8 / 54.1 / 63.2 % |
| Regression set (27 setups incl. the old seed-2026 rows) | - | 54.8 % |
| After Auto Group + Fill all, popular | - | 62.5 % |
| Destructive bindings, every stage | 57 (after Auto Group + Fill all) | 0 |
| Placement sweep 0 -> 1: fidelity / consistency | - | 21.0 -> 21.8 % / 99.9 -> 80.7 % |

- Planner: labelled latched switches take what they name; gear and flaps may use the grips or a pinky / boat switch when
  no base button is free; the wheel brake button goes when toe brakes are placed; a trigger-less yoke's first button is
  the gun; hats never get the opposite direction; lights are on / off pairs; GA panels by label (battery, alternator,
  avionics, magnetos, lights, AP mode buttons); rudder on the throttle's thumb stick without pedals or twist; at every
  slider position helicopter / input-layer throttles and bomb-dropping trigger stages keep their own relations.
- Auto Group and Fill all keep the setup's rules for a bound input (`EngineBindGuard`).
- Eval: fresh seeds and the regression set, in-flight essentials, the placement sweep; the slider is renamed "Where
  should functions go?" in the GUI (38 languages) and `--placement` on the command line.

## What the evaluation changed in the planner (2.7.0)

From Auto Group / Fill all and the novice case:

- Suggest missing: a word of the relation's name of another kind than its commands is no lead ("Mode - A2A - CAS" is
  close air support, not the flight control system's CAS; "UFC COMM1" is no HOTAS comm switch; "Radar Frequency Down"
  no radio channel), and a name that reads as functions of different kinds keeps the one that takes most of its words
  ("Radar Prev Mode F1" is a radar mode, not F1 = cockpit view).
- Function map: Star Citizen's "ATC request" is no longer catalogued as the F/A-18's ATC (autothrottle) button; an
  ATC / docking request links to the docking request; "Armament master switch" is no master power switch.
- Toe brakes only on brake axes (pedals, a brake lever), never on a slider or knob; a wheel brake button for MSFS and
  X-Plane, dropped when toe brakes are placed.
- IL-2: snap views (forward, back, left, right) and the look axes for looking around without head tracking.
- Devices JoyPro does not know: "URSA MINOR", "fighter" and "sidestick" names count as sticks, a stick's Z is its twist
  when it has no RZ, a throttle's lever is Z or its first other axis; the setup says in plain words that it does not
  know the device.

From the setup plans alone:

- One relation per game command (Elite's yaw axis was listed as rudder and space yaw, Arma's throttle as four
  throttles); a latched pair on one toggle command keeps both positions.
- Functions that meet only through a DCS input layer (Default ...) may share an input when they do the same thing (the
  layer's throttle and the aircraft's throttle on one lever).
- A function its aircraft already has another way gives its input back (the F/A-18's third throttle axis, an inverted
  toe brake next to the normal one, a gear toggle next to gear up and down on a switch).
- A modifier when essential hat sets (CMS, TMS ...) find no free hat, said in the plan's notes.
- Yokes and quadrants use the stick's and the throttle's positions; their buttons can be the shift button.
- Pitch, roll and yaw never on a lever, slider, wheel or knob that stays where it is left.
- Countermeasures prefer the throttle's hats when the stick has no CMS hat of its own.
- Panel functions (gear, flaps) may use the grips when there is no panel and almost no base button.
- Catalog devices whose button numbers are not published get the usual order (button 1 = trigger, hat 1 = the first
  hat), and the wizard says so.
- An Xbox controller flies when no joystick is connected; the second of two identical sticks becomes the left stick.

## Fix round 5 (2026-11-14, after review 3)

`<eval folder>/2026-11-14/after-fix5` (README with every seed and stage). The gates are stricter (look on a
grip without a modifier or on centring axes, a missing throttle fails, labelled grip controls, selector positions and
modifier inputs are gated), so hard-gate rates are not comparable with round 4:

| | round 4 | round 5 |
|---|---|---|
| In-flight essentials, setup alone, popular: reached / all time-critical | 91.5 / 47.4 % | 90.6 / 50.5 % |
| Same, typical DCS hangar, popular | 85.5 / 28.2 % | 85.2 / 32.0 % |
| Same, after Auto Group + Fill all, popular | 88.1 / 42.8 % | 88.1 / 44.7 % |
| Random setups, setup alone (three fresh seeds each round) | 74.0-86.3 % reached | 85.0-85.3 % reached |
| Destructive bindings, every stage and seed | 0 | 0 |
| Placement sweep 0 -> 1: fidelity / consistency | 21.0 -> 21.8 % / 99.9 -> 80.7 % | 25.5 -> 34.4 % / 99.9 -> 77.1 % |

- Review 3: the Warthog's SPEED BRAKE is the speed brake (printed grip labels own their control), JoyPro's shift only on
  momentary unlabelled buttons (not the TCA's reverser zone), nothing on mode selector positions (X56, X52, STECS, TCA
  ENG MODE: catalog type `selector`), no labelled switch as the mode switch, centred rates on centring axes, the TCA
  catalog from the manuals, lever detents only for the aircraft whose throttle the lever carries.
- Planner: the in-flight essentials take hats in their documented order; gear / flaps pairs stay together; redundant
  slew buttons and gear / flaps toggles give their input back; a lone set member takes any button; a set never splits
  over two hats (C11); the gun keeps the trigger at every slider position; from 50 % on each aircraft's snap views find
  their own free hat.
- HOTAS equivalence in the device catalogs and `DB/Functions/hotas-equivalence.json` (see
  [Where functions go](#where-functions-go)).
- Plain relation names for one-game relations (the game's or JoyPro's own action names).
- Still open: fidelity at position 1 below 50 %, small devices miss flaps / PTT / look by the priority, the pad's
  throttle on its right stick, the B7 capture step.

## Fix round 6 (2026-11-15, after review 4)

`<eval folder>/2026-11-14/after-fix6` (README with every seed, the head-tracking run, the sweep and the
open items). The look gate, the waiver, the destructive check and the essential order changed (see above), so hard-gate
rates are not fully comparable with round 5:

| | round 5 | round 6 |
|---|---|---|
| In-flight essentials, setup alone, popular: reached / all time-critical | 90.6 / 50.5 % | 92.3 / 56.3 % |
| Same, typical DCS hangar, popular | 85.2 / 32.0 % | 88.7 / 38.8 % |
| Hard gates setup alone, popular / typical hangar | 75.8 / 65.0 % | 94.5 / 89.0 % |
| Random setups, setup alone (three fresh seeds each round) | 85.0-85.3 % reached | 82.8-87.5 % reached |
| Popular rows reaching look / PTT / flaps / gear | 395 / 317 / 247 / 320 | 635 / 327 / 249 / 291 |
| Destructive bindings, every stage, seed and slider position | 0 | 0 (new kind: toggles / held starters on latched switches) |
| Placement sweep 0 -> 0.25 -> 0.5 -> 0.75 -> 1: fidelity | 25.5 / 26.7 / 33.7 / 34.2 / 34.4 % | 25.6 / 28.2 / 31.7 / 34.5 / 34.8 % |

- Review 4 B4 / M2: the wizard asks *Do you use head tracking or VR?* ([setup-wizard.md](setup-wizard.md#head-tracking));
  without it looking around ranks right after the fight essentials and the trim, a set that finds no unshifted hat goes
  whole behind Shift 1, the gamepad looks around with its right stick and moves its throttle to the D-pad (where the
  game has throttle buttons), sims whose camera JoyPro does not manage keep a hat free, and from the middle of the slider
  a real HOTAS position (the Warthog's DMS) keeps its function.
- M1 / M3: no engine master / starter / magneto toggle and no held starter on a latched switch or an OFF detent.
- M4: the slider takes the real positions over function by function; snap views, countermeasures, gear and flaps
  are planned per aircraft at every position.
- #4 and minors: no guarded or labelled mode switch; gear off the stick grip, triggers and latched grip switches;
  printed gear levers and flaps levers only for their own function; X56 base toggles are momentary.
- Harness: L10-L16 (above), per-gate non-regression baseline, smoke thresholds at the measured value minus 2.

## Fix round 7 (2026-11-15, after review 5)

`<eval folder>/2026-11-14/after-fix7` (README with every seed, the sweep with single aircraft, the
pipeline, head tracking and the open items). New harness checks L18-L24, so rates are not fully comparable:

| | round 6 | round 7 |
|---|---|---|
| Hard gates setup alone, popular / typical hangar | 94.5 / 89.0 % | 97.4 / 90.8 % |
| In-flight essentials, setup alone, popular: reached / all time-critical | 92.3 / 56.3 % | 92.3 / 56.1 % |
| Random setups, setup alone, the seeds of round 6 (hard gates) | 66.8 / 49.2 / 73.1 % | 69.6 / 50.8 / 77.1 % |
| Review 5's seed 196571044 (random, hard gates) | 65.1 % | 74.9 % |
| Regression set, setup alone / typical | 43.3 / 32.9 % | 48.6 / 39.5 % |
| After Auto Group + Fill all, popular (hard gates) | not measured | 86.0 % |
| Placement sweep: release / wheel brake fails at 0.75 / 1 | +13 / +19 rows | 0 / 0 |
| Destructive bindings (with the new kind L18) | 0 (B5 invisible) | 0 |

- B5: no one-way engine start / stop on an engine-unfit switch position (above, L18), planner and bind guard.
- One mode per dual-mode thumb stick (L19), afterburner / TOGA / WEP only on a top-end detent (L20), single aircraft in
  the sweep (L21), reachable shifts (L22), the pipeline measured (L23), head tracking drops padlock / look-ahead and
  keeps the recenter (L24); the head-tracking answer lives in the profile and Auto Group / suggestions follow it.
- ACE COMBAT 8 on a single-hat HOTAS: look left / right on the spring rocker (catalog axis type `centring`), the pad's
  free-hat look knows the devices' real hats, pad-check reports a hardware limit as n/a.
- Open: the Mi-8 gun after Fill all, MSFS throttle after Auto Group, the TCA's countermeasures in the 20-module hangar,
  L25.

## Fix round 8 (2026-11-15 / 16, after review 6)

`<eval folder>/2026-11-14/after-fix8` (README with every seed, the sweep, head tracking, the pipeline and the open items).
The harness is stricter (losses on the quick path fail, its own engine-switch judge, CMS dispense only, every quick-path
game is a target), so all-target rates are not comparable with round 7; like for like (review 6's targets and setups):

| | review 6 | round 8 |
|---|---|---|
| Hard gates, popular: setup alone / typical hangar / ACE COMBAT 8 alone | 97.4 / 90.8 / 90.0 % | 98.3 / 93.0 / 100.0 % |
| All games in one plan (the quick path), popular | 75.5 % (throttle fails 130) | 94.2 % |
| After Auto Group + Fill all, popular | 87.1 % (Mi-8 gun lost 20 / 20) | 92.5 % (gun never lost) |
| Random seeds 121059320 / 549908568, setup alone | 62.0 / 60.4 % | 63.4 / 67.3 % |
| Countermeasures missing where they apply (random) | 6.0 / 6.3 % | 3.9 / 3.9 % |
| Head tracking, popular hangar: recenter silently missing | 60 rows | 0 (60 rows tell the user to use the tracker's key) |
| Destructive bindings (own judge; with the planner's B5b rule off: 47) | 0 | 0 |

- B9: setup apply no longer switches off an aircraft the catalog files under the relation's own function (the Mi-8 pilot's
  FIRE button and the door gun share one id). B8: the flare rescue may use a hat direction and outranks every essential but
  the gun, the release and the wheel brake button. B6: IL-2's and every other game's throttles are demands of their own in
  a plan for several games. B5b / M9: an engine stop only on a position labelled for the stop.
- M1 recenter (shifted non-essential pair, else a plan note), M2 / Star Citizen gamepad throttle on the right stick's Y,
  M3 ACE COMBAT 8 boost / brake / yaw on a free hat, M4 the gun's own grip rescue, M5 no exemption by setup id, M7 printed
  labels off the grips hold only what they name, M8 no fixed-wing throttle on a collective knob; minors m1 m2 m3 m5 m11 m13 m14.
- Harness L26 L27 L28 L29 L30 L31 L32 L33 L34 (above). Open: B7 / B10, the 3-button yoke countermeasures
  (PTT label vs flares: owner decision), the MSFS throttle on a left stick's twist after Auto Group, L25.

## Fix round 8, data part (2026-11-16, after review 6)

`<eval folder>/2026-11-14/after-data8` (README with seeds 253409032, 680415417, 547390266 and the open items).

- B7 / L26: every wizard game's function table has its flight axes (FSX, P3D, IL-2 1946, Cliffs of Dover, Tiny
  Combat Arena, G-Rebels, Project Wingman, Battlefield, HLL Vietnam); the eval scores all 36 wizard games and fails when
  a typical stick + throttle setup misses pitch, roll or throttle in any of them (1008 of 1008, main's tables 708).
- B10: lower-case catalog descriptions are no printed labels (`SetupPhases.DescribesControlOnly`, docs/devices.md):
  CH / Virpil / WarBRD / Foxtrot / Top Gun / TQS hats are used, the CH Flightstick Pro carries its weapon release.
  Random seed 253409032: hard gates 66.9 -> 75.0 %, regression set 44.5 -> 48.4 %; popular rows unchanged.


## Fix round 8b (2026-11-16, owner decisions, merged with the data part)

`<eval folder>/2026-11-14/after-fix8b` (README: seeds 443495670 / 392840616, failing rows only).

- Countermeasures on a small yoke: a printed label keeps its button; the flares go behind a shift (counted reachable,
  degraded) or the plan says in 38 languages that no grip button is left. A throttle / RPM / mixture / condition never
  on a spring-centred axis (planner, bind guard, relation suggestions). The release may take a wheel-brake button,
  a wheel brake never the flares. Snap views move to a free grip hat at every slider position (no head tracking).
- Gear never on the press of a thumb stick / slew on any layer (the switches gate flags it). Plan / eval / eval-pad
  start from an empty profile unless `--profile` is given.
- Foxtrot alone: a set that evicts and then cannot be placed puts the evicted back (CMS left / right had evicted the
  trim from the POV); the trim keeps the POV. The rest of a split CMS set is no essential of its own (the CH
  Flightstick Pro POV takes the whole trim, no CMS left / right mixed in); an essential set may move a placed group
  of no essential (the Extreme 3D keeps its trim in Falcon BMS). A blanket "never two sets on one hat" rule was tried
  and dropped: it cost trim / PTT on popular setups.
- Axes-only look games (Project Wingman, Battlefield): no centring axis left -> the plan names a free grip hat for Hat
  to axes or the game's view hat (gate: ok, degraded); none free -> a hardware-limit note (gate: n/a). Look fails in
  those games: 51 -> 0 rows (popular + 20 random).
- Fresh seed 443495670, all 36 games: popular 98.8 % hard gates, random 84.0 %, regression set 52.7 %, typical hangar
  66.4 %, all thresholds pass; head tracking (seed 392840616) passes. Open: the all-games plan loses the TCA's wheel
  brake button (22 rows, the release / flares outrank it on its four head buttons - identical before this part), the
  placement sweep's "0.50 -> 0.75 changes nothing" check (identical before this part).

## Fix round 9 (2026-11-16, review 7 and owner decisions)

`<eval folder>/2026-11-14/after-fix9` (summaries and seeds only).

- Wheel brake: combat aircraft keep gun, release and flares on the head buttons and the wheel brake falls back to a
  throttle-hand button (TCA quadrant lever end); civil sims and unarmed aircraft are a demand of their own and keep a
  grip button; in a hangar of more than three modules the wheel brake is planned per module.
- B12: a trigger's "detent" label is no lever detent; `engine setup grip-sweep` (in the autosetup smoke) checks the gun
  on the trigger of every catalog grip with a numbered trigger (175 of 175).
- B13: gun, release and flares beat every non-survival function for grip inputs (flares take a combat aircraft's
  wheel-brake button, release / wheel brake may take a hat direction, grip priority fire > release > CMS > wheel brakes,
  a small stick's index / thumb button is never a shift - the pinky still is); flares behind a shift get a plan note.
- B14: toggle detection reads the game's own action title; a title toggle never goes on a latched position.
- Look: pan left / right / up / down + recenter + zoom, with and without head tracking (with: recenter and zoom
  only). The tables' `view_snap_*` are the games' held pan commands; new gate `zoom`; recenter / zoom / throttle with no
  input left get plan notes (38 languages) and the gates go degraded / n/a only with the note.
- M10: no throttle on any spring-return axis, a left stick's Y included (its slider / wheel or the throttle buttons);
  M14: the throttle hand's momentary buttons count as in hand for the flares, CMS behind a shift passes only when no
  unshifted hand button held a lesser function; M17: head tracking yes keeps look in games the tracker does not reach.
- Slider: the fight's occasional cockpit buttons go real from 0.75, so every step of the sweep moves something.
- Harness: `--zero-gates <gates>` fails on any row (L35 / L36; fire, release, countermeasures currently fail 206
  regression / typical rows - not yet in the smoke); look gate checks recenter; throttle n/a only with the plan's note.
- Open: M13 (all-games yaw loss on CH Combatstick + RS-16), the regression rows behind `--zero-gates`, L42 / L45 beyond
  the notes checked here.
