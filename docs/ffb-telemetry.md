# Telemetry force feedback (JoyPro.Ffb)

(Recording the telemetry of a session, Tacview export: [telemetry-recording.md](telemetry-recording.md). It uses the
same sources and runs in the same process while force feedback runs.)

JoyPro reads live telemetry from the game you fly and turns it into force feedback on a DirectInput force feedback
stick (for example the VPforce Rhino): stall buffet, G-force stick weight, gear and flap rumble, gun bursts, runway
rumble, a centring spring that stiffens with airspeed and follows the trim (the game's, and JoyPro's own stick trim
on buttons or a hat of any device). What the stick does is a node graph you can
edit; the built-in graph covers the effects above. Phase 1 supports DCS World, IL-2 Great Battles / IL-2 Korea and War
Thunder; MSFS and X-Plane are researched below.

This is JoyPro's own implementation. No code of VPforce TelemFFB or any other project was used; the protocol facts
below come from the games' own documentation and the public sources listed at the end. The graph design (telemetry
nodes with min/max normalisation, group nodes, Bezier curve nodes, output nodes with values at 0 % and 100 % input,
"strongest wins" per actuator, gating "only while the game runs") comes from the user's own Forza haptics app
(`ForzaHaptics.Tester`: SignalGraph, BlueprintEditor, GameWatch).

## Where things are

| Part | Path |
|---|---|
| Graph engine, telemetry sources, host, settings model | `gui/JoyPro.Ffb.Core` (net9 library) |
| The process (DirectInput output, dry sink, simulator) | `gui/JoyPro.Ffb` (`JoyPro.Ffb.exe`, net9.0-windows) |
| Settings and game setup on the command line | `engine ffb telemetry ...` (`engine/EngineFfbTelemetry.cs`) |
| GUI page (settings, setup buttons, node editor, telemetry inspector) | `gui/JoyPro.Gui` (Force feedback (telemetry)) |
| Settings file | `<data>\Ffb\Telemetry\telemetry-ffb.json` |
| Graph files | `<data>\Ffb\Telemetry\Graphs\*.jpffb.json` |
| Last status of the process | `<data>\Ffb\Telemetry\status.json` |

`<data>` is `JOYPRO_DATA_DIR`, else `%LOCALAPPDATA%\JoyPro` (the engine's data folder).

## How it runs

```
game ──UDP/HTTP──> source (DCS lines | IL-2 packets | War Thunder JSON)
                     └─> normalised aircraft model "ac.*" (+ derived: gear moving, firing, ground roll ...)
                           └─> graph (telemetry → group/curve/maths → effect outputs)
                                 └─> mixer (strongest per slot, effect switches, strength, hard force cap)
                                       └─> DirectInput device (or the dry sink in tests)
```

- **Only while a game runs.** The GUI checks for a supported game process every 5 s, and only while the feature is
  switched on; then it starts `JoyPro.Ffb run --game <id> --game-pid <pid> --parent-pid <gui> --protocol`. The process
  follows the game through its process handle (a kernel wait, no polling) and ends when the game, or JoyPro, ends.
- **Only while telemetry arrives.** Without fresh telemetry (menus, pause, the hook not installed) the force loop sends
  ONE STOP ALL, releases the device and then leaves it alone (the Forza app's rule: re-zeroing every tick would fight
  other programs). Exception: with "JoyPro provides the centring spring" (setting `centringSpring`, default on) and the
  device already JoyPro's, only the springs are kept (the last ones, with their trim, plus a plain spring on an axis
  without one), refreshed about once a second; everything else stops. Springs are passive, and a stick whose own
  spring is switched off must not go slack while the game pauses. The status line says why: `idle - waiting for
  DCS.exe`, `idle - no telemetry on UDP 41992`, `idle - the multiplayer server does not allow ownship export`,
  `active - F-16C_50`, `stopped after an error: ...`.
- **The force loop** runs at 50 Hz (setting `tickRateHz`) on a high-resolution waitable timer while forces are sent
  (no busy wait, no raised system timer resolution). Without telemetry it blocks on an event the next telemetry frame
  sets: no CPU at all.
- **Every force is finite; springs are not restarted.** Constant and periodic effects are downloaded with a duration
  of 0.3 s and restarted every 0.15 s; between restarts only changed parameters go to the device (a USB HID stick
  takes about 1000 reports a second; this keeps JoyPro far below that). Periodic effects restart at the phase they
  reached, so the waveform is not reset. If the process hangs or is killed, the device ends them on its own within
  0.3 s. Condition effects (spring, damper, friction) only resist the hand, so they run with an infinite duration,
  are started once and only get their changed parameters (`SetParameters(DIEP_TYPESPECIFICPARAMS)`, no DIEP_START):
  a restarted spring drops its force for a moment and jitters. A killed process loses its exclusive acquisition and
  DirectInput stops its effects (verified on the Rhino for the forces; to re-check for springs, see below).
- **STOP ALL** on: telemetry timeout (`timeoutSeconds`, default 0.5 s), game exit, JoyPro exit, stdin closed, "stop",
  Ctrl+C / console close / shutdown (PosixSignalRegistration), process exit and unhandled exceptions, and any error in
  a tick (which also switches the output off until it is enabled again).
- **Hard force cap** (`maxForce`, default 60 %): per axis the effects are scaled together so that even their worst case
  (sum of peak forces) stays within the cap. The user's strength (global × game × aircraft) applies before the cap.
- **Exclusive device access** only while forces are sent: the device is acquired (exclusive, background) on the first
  update and released with the STOP ALL. The game's own force feedback must be switched off for that stick (DCS: the
  aircraft's FFB option / Axis Tune "FFB"; IL-2: Settings > Input devices > force feedback off; War Thunder: force
  feedback off), otherwise the game holds the device and JoyPro reports `DirectInput Acquire failed`.

### Process protocol (GUI ↔ JoyPro.Ffb)

`JoyPro.Ffb run ... --protocol` writes JSON lines to stdout and reads commands from stdin
(`gui/JoyPro.Ffb.Core/Host/HostProtocol.cs`):

```
{"type":"status","state":"active","reason":"active - F-16C_50","game":"DCS","aircraft":"F-16C_50","graph":"Flight (built-in)","frames":1234,"endpoint":"UDP 41992","device":"VPforce Rhino FFB Joystick"}
{"type":"telemetry","time":12.3,"values":{"ac.ias":154.2,...}}     5 Hz, only after "watch on"
{"type":"nodes","values":{"n1a2b3c4d5e6":0.42,...}}                 5 Hz, only after "watch on"
{"type":"exit","reason":"game exited"}
stdin: "watch on" | "watch off" | "reload" | "stop"     (end of stdin = stop)
```

Other commands: `JoyPro.Ffb devices [--json]` (read-only list of force feedback devices, their force axes and
effects: nothing is acquired or sent), `JoyPro.Ffb preset` (the built-in graph as JSON), `JoyPro.Ffb simulate`
(the synthetic test flight, see Tests), `JoyPro.Ffb test [--device {guid}] [--parent-pid N]` (the direction test's
process, below). `JOYPRO_FFB_DRY=1` forces the dry sink.

`JoyPro.Ffb test` reads one command per stdin line and answers one JSON line each (`{"type":"ok"}`,
`{"type":"position","valid":true,"x":..,"y":..}`, `{"type":"error","message":..}`): `invert X Y` (0/1),
`push X|Y magnitude`, `spring k cx cy`, `conditions combined|separate|restart` (spring layout, see "Condition
effects"), `pos`, `stop`, `quit`. Forces are clamped to 25 %, a push ends by itself after 4 s and a spring after 30 s
without a new command; end of stdin, `quit`, the GUI exiting (`--parent-pid`), Ctrl+C and process exit all release the
device.

**Logs** (`<data>\Logs`, docs/logging.md): `JoyPro.Ffb test` writes every stdin command and its answer (incl. every
`pos` reading) and every device call with its values and HRESULT (category `FfbTest`): acquire / autocenter / STOP ALL
/ actuators on, each effect created or updated (type, axes, magnitude or centre / coefficient / saturation, and the
device values after the invert, e.g. `Spring X+Y X: offset 0, ...; Y: offset -3500, ...`), stops and the release
sequence. `JoyPro.Ffb run` logs the same device calls at debug level, at most one line a second per kind. The GUI log
has the direction test's steps, the user's clicks and answers (category `FfbWizard`) and, at debug level, each test
command with its answer.

### Resources (measured 2026-10-11, Debug build, DCS stream at 60 Hz, every effect on)

| | Value |
|---|---|
| CPU while forces run | ~1.5 % of one core |
| CPU without telemetry (game in a menu) | 0 (no measurable CPU time in 10 s) |
| Private memory | ~17 MB, flat over 60 s (live managed heap < 1 MB) |
| Working set | ~43 MB |
| Threads / handles | 11-16 / ~310, flat |
| GUI side | `Process.GetProcessesByName` every 5 s while the feature is on (a few ms), nothing while it is off |

The process allocates about 1 MB/s while forces run (telemetry frames are small immutable dictionaries). The runtime
config sets a 64 MB heap hard limit, which makes the GC collect every few seconds; with the default budget the heap
grew to ~58 MB between collections (`System.GC.Gen0Size` is not read by .NET 9).

## The graph

Values flow through the graph as numbers, normalised to 0..1 by convention (signed -1..1 where a direction matters,
e.g. trim). Node kinds (`NodeKind`):

- **Constant** (with Active switch: a test value without the game), **Telemetry** (any key of the current game, with
  min/max normalisation, absolute value, invert; a missing key reads 0 and "valid" 0 - also when inverted),
  **Group** (any number of wires: maximum, minimum, average, clamped sum), **Curve** (multi-segment cubic Bezier).
- Maths (add, multiply, scale/offset, clamp, abs, min, max, select) and stateful nodes (low pass, slew limit,
  derivative, counter delta to events, envelope, threshold with hysteresis), vectors.
- **Outputs** (effects): constant force (axis, direction, bipolar), periodic (sine/square/triangle/saw; strength and/or
  frequency mapped from the input with values at 0 % and 100 %, optional frequency input, pulsed bursts with pulse
  rate and duty cycle), spring (stiffness from the input, centre offset input e.g. trim, saturation), damper, friction.
  Every output takes any number of wires ("in", merged by its input mode), has an effect name (for the switches), a
  slot 0..3 and "silent at zero". Outputs of the same type on the same axis and slot compete: the strongest wins (as
  several outputs on one actuator in the Forza app); different slots add up (and are capped together).
- **Direction:** **Invert** (flips the sign of a value; a switch turns it off), the constant force's "Invert direction"
  (saved as `negative`) and the spring's "Invert centre" (trim moves the other way). These change what one effect
  does; the device-wide direction is the device's invert X / Y (see "Direction, invert and the direction test").
- **Input:** **Device button / hat** (`held` 0/1, `pressed` one event per press; any device: the stick itself when the
  device is empty, otherwise "Name {instance guid}" as the "press the button now" capture names it; input `JOY_BTN5`
  or `JOY_BTN_POV1_U`, a straight hat direction also counts on its diagonals), **Stick position** (the force feedback
  stick's x / y, -1..1, right and towards the pilot positive, plus `valid`), **Trim** (see "Trim").

Buttons of other devices (a throttle hat, a button box) are read with DirectInput in the background, non-exclusive
(`gui/JoyPro.Ffb/DirectInputInputs.cs`): opened when a node first asks, polled once per tick on the force loop, looked
for again every 3 s while missing. The stick's own buttons and position use a second, non-exclusive instance of the
same device, so they also work while JoyPro's forces are off. Axes are read with a range of -10000..10000.

Graphs are JSON (`*.jpffb.json`, format `joypro-ffb-graph`), per game and optionally per aircraft (settings `graph`).
Loading a graph never switches output on.

### Built-in graph (`FfbPresets.Flight()`)

| Effect name | Input (normalised model) | Output |
|---|---|---|
| `buffet` | AoA 10..20 deg through an ease-in curve, max with the stall warning, × airborne (IAS 20..35 m/s) | periodic sine, both axes, slot 0, 5..45 %, 9..14 Hz |
| `gforce` | G 1.5..9, low pass 0.15 s | constant force pushing forward (Y), 0..35 % |
| `gear` | gear moving | periodic triangle 16 Hz, 15 %, slot 1 |
| `flaps` | flaps moving | periodic sine 22 Hz, 10 %, slot 1 (gear wins while both move) |
| `speedbrake` | speed brake out × airspeed 50..250 m/s | periodic sine 12 Hz, 3..20 %, slot 1 |
| `runway` | ground roll 1..60 m/s | periodic triangle 8..24 Hz, 4..22 %, slot 2 |
| `shake` | IL-2 cockpit shake amplitude (its own frequency) | periodic sine, 0..30 %, slot 2 |
| `guns` | guns firing | square 25 Hz pulsed 12 times a second (45 % duty), 35 %, Y, slot 3 |
| `hits` | hit taken (IL-2) | sideways jolt, 30 % |
| `spring` | base stiffness 0.2 + airspeed 30..250 m/s × 0.5; centre = game trim × 0.5 (War Thunder) + JoyPro's stick trim | spring per axis, up to 50 %, saturation 50 % |

The built-in graph also has the stick trim: a Trim node with six button nodes (`Trim: to stick position`,
`Trim: nose down (hat up)`, `Trim: nose up (hat down)`, `Trim: left`, `Trim: right`, `Trim: reset`), unassigned until
the user picks the buttons (editor: select the Trim node, "Assign trim buttons"). Its x / y are added to the game's
trim and go into the roll and pitch springs' centre.

## Trim

`TrimNode` (`gui/JoyPro.Ffb.Core/Graph/Nodes.Input.cs`) keeps the springs' centre:

- **To stick position** ("here"): the centre jumps to where the stick is (its own position, read non-exclusively).
  While the button is held the centre follows the stick, so the spring lets go (a helicopter's force trim release);
  when it is let go the centre stays. Without a position (device not readable) the press is ignored.
- **Four trim buttons / a hat** (up, down, left, right): one step per press (`Step`, default 2 % of the travel), held
  longer than `RepeatDelay` (0.35 s) it repeats at `RepeatRate` (10 steps a second; 0 = no repeat). Up = nose down =
  the centre moves forward (away from the pilot, negative Y); `InvertX` / `InvertY` turn the buttons around.
- **Reset**: back to the middle.
- `Limit` (80 %) caps the centre; `MoveRate` (1.5 travel/s; 0 = at once) limits how fast the centre moves after a reset
  or a big step, so the stick does not jump.
- The trim survives a telemetry gap or pause (the graph's Reset keeps it; a button held across the gap does not fire
  again); a new graph (aircraft change) or a restart starts from the middle. While the telemetry pauses, the held
  springs keep the trimmed centre (with "JoyPro provides the centring spring").

Trim only moves the stick when the device obeys DirectInput springs: on the VPforce Rhino the Configurator's own spring
must be off (see "Hardware findings").

## Direction, invert and the direction test

JoyPro's convention: a positive force pushes to the positive end of the axis, right on X and towards the pilot on Y;
positions use the same (DirectInput: right and pulled back are positive). How a device maps that is its invert X / Y,
saved per device (`devices` in the config, by instance guid; a device without an entry uses the global
`invertX`/`invertY`). The backend flips forces and the spring centre with it.

The page's **Direction test** (`FfbDirectionWizard`, `gui/JoyPro.Ffb.Core/Host/FfbDirectionWizard.cs`, run through
`JoyPro.Ffb test`) does it with the user, one announced step at a time (each waits for "Ready", Cancel stops every
force at any time):

1. Push RIGHT for 2 s (20 %, inverts off: the raw direction) -> "Which way did the stick move?" right / left / it did
   not move. Left = invert X.
2. Push FORWARD for 2 s -> forward / back / did not move. Back = invert Y (the Rhino's case).
   "Did not move" keeps that axis's invert and says what to check.
3. Centring spring (with the found inverts, 25 % at most): "move the stick and let go: does it spring back?"
4. Trim: the spring's centre moves 35 % forward for 2.5 s; JoyPro reads the stick position before and after and shows
   the measured movement -> "did the stick move forward and stay there?"
   If 3 or 4 fail: on a VPforce stick the Configurator hint (below), otherwise a general note about the device's own
   spring.
At the end the found inverts are saved for the device (and globally). The test refuses to start while JoyPro.Ffb runs
for a game (it needs the stick to itself). "Repeat this step" (shown with every question) stops the forces and
announces the same push / spring / trim check again; "Start over" stops everything and goes back to the first step
(after the end of a test it starts a fresh test process). "Start" and "Ready" are separate buttons with fixed texts:
one button whose text changed from "Start" to "Ready" showed "Read" in the first hardware test.

## Hardware findings (VPforce Rhino, 2026-10-11, positions read with winmm, hands off)

- Direction: X matches JoyPro's convention; Y is reversed (a negative Y force pushed back towards the pilot).
  `invertY=true` fixes it (verified: -Y with invertY pushed forward). Other sticks may differ, hence the per-device
  invert and the direction test; single effects can be turned around in the graph (Invert node / switches).
- The VPforce Configurator's Effects tab, box at the top left, checkbox **"Spring"** (+ "Apply Settings") is a
  firmware spring. With it ON, a DirectInput spring (centre 0.5, coefficient 1, saturation 0.3) did nothing at all
  (Y stayed at -0.014 for 14 s), with or without DIPROP_AUTOCENTER off; constant forces and periodic effects still
  act. With it OFF, JoyPro's spring with a trimmed centre moved the stick to the centre within 0.6 s - and after
  JoyPro released the device the stick was slack, because the firmware spring is the user's own setting (not caused
  by JoyPro; an earlier note blaming the release was confounded by this setting).
- The spring's centre offset follows the force invert on this device (without invertY a positive centre went forward),
  so the backend applies the same sign to the centre.
- So: DIPROP_AUTOCENTER does not control this firmware spring; JoyPro cannot (and does not try to) change VPforce
  firmware settings. For trim and centring from JoyPro: untick "Spring" in the Configurator; JoyPro then centres the
  stick itself while it owns it ("JoyPro provides the centring spring"). Keep it ticked to use the stick's own spring
  (no trim from JoyPro; then switch the JoyPro option off, so JoyPro sends no springs). The page and the direction
  test show this hint for VPforce devices (VID FFFF PID 2055, or "VPforce" in the name).

- Re-test 2 (Configurator spring off, `JoyPro.Ffb test`, single-axis springs): `spring 0.8 0 0` centred X (from
  -0.92 to -0.04); `spring 0.8 0 -0.35` (Y centre 35 % forward) did NOT move Y, but moved X to -0.16 - the Y offset
  acted on X. The bytes JoyPro sends name the right axis (checked by `DirectInputLayoutTests`: Y effect = offset 4, its
  DICONDITION has the -3500 offset), so the device (or the PID driver) applies the condition block of a single-axis
  Y effect to X - plausibly firmware that stores conditions by parameter block (block 0 = X) regardless of the
  effect's axis. Hence the combined layout below, as games send a 2-axis spring.

## Condition effects (spring, damper, friction): layout on the device

`DirectInputLayout` (`gui/JoyPro.Ffb.Core/Output/DirectInputLayout.cs`) writes the DIEFFECT bytes and is tested
without a device. Axes are named by their offset in JoyPro's data format (DIEFF_OBJECTOFFSETS): X = 0, Y = 4.

- **combined** (default): one effect per condition type and slot for both axes: cAxes 2, rgdwAxes {0, 4},
  rglDirection {0, 0}, two DICONDITIONs, X's first, Y's second (an axis without that effect gets an all-zero block).
  Infinite, started once, changes only with DIEP_TYPESPECIFICPARAMS.
- **separate**: one single-axis effect per axis (cAxes 1, direction 1), infinite, changes without restart (the
  version of the first re-test).
- **restart**: one single-axis effect per axis with a 0.3 s duration, restarted with DIEP_DURATION |
  DIEP_TYPESPECIFICPARAMS | DIEP_START (the very first version, which moved Y to a trimmed centre in the morning test).

For a hardware A/B test: `JoyPro.Ffb test` command `conditions combined|separate|restart`, or
`JoyPro.Ffb run ... --conditions separate`. The log shows the exact blocks sent for each layout.

## Acquire and release (how JoyPro hands the device back)

DirectInput, as captured on USB by the Wine developers (Wine's `dinput/joystick_hid.c` follows it): an exclusive
`Acquire` sends a PID "Device Reset" (the device's startup state: its default centring on, if it has one) and, when
DIPROP_AUTOCENTER is off, "Stop All Effects" (that is what "autocenter off" means for a PID device); an exclusive
`Unacquire` sends "Device Reset" again. DISFFC_RESET = Device Reset, DISFFC_STOPALL = Stop All Effects,
DISFFC_SETACTUATORSON/OFF = Enable/Disable Actuators, DISFFC_PAUSE/CONTINUE = Pause/Continue. DIPROP_AUTOCENTER can
only change while the device is not acquired.

JoyPro (`DirectInputFfbBackend`):
- Acquire: DIPROP_AUTOCENTER off (before Acquire), Acquire (exclusive, background), STOP ALL, ACTUATORS ON. No RESET
  after Acquire: on PID devices a reset brings the default centring back, which would fight JoyPro's spring.
- Release (telemetry gone without the centring option, game exit, JoyPro exit, errors): STOP ALL, stop and unload
  every effect, RESET while still acquired (the device's startup state), Unacquire (DirectInput resets it again),
  DIPROP_AUTOCENTER on (the default for the next program). This is the standard way to hand a device back; whether the
  stick then centres depends on the device's own default spring (on the Rhino: the Configurator's "Spring").
- While JoyPro owns the device and "JoyPro provides the centring spring" is on, it never leaves the stick slack: the
  graph's springs, a plain spring (stiffness `centringStiffness`, default 30 %, saturation 50 %) on every axis without
  one, and the held springs during telemetry pauses. Switched off, JoyPro sends no springs at all.

## Normalised aircraft model ("ac.*")

Every source fills the same keys (SI units), so one graph works in every game. A key the game does not send stays
absent (the node reads 0). Raw game keys (`dcs.*`, `il2.*`, `wt.*`) are available too.

| Key | Unit | DCS | IL-2 | War Thunder |
|---|---|---|---|---|
| ac.ias / ac.tas / ac.mach | m/s, - | LoGetIndicatedAirSpeed / TrueAirSpeed / MachNumber | EAS | "IAS, km/h" / "TAS, km/h" / "M" |
| ac.aoa / ac.aos | deg | LoGetAngleOfAttack / AngleOfSideSlip (rad) | AOA / AOS (rad) | "AoA, deg" / "AoS, deg" |
| ac.g | G | LoGetAccelerationUnits().y | \|ACCELERATION\| / g | "Ny" |
| ac.vs, ac.agl, ac.alt | m/s, m | LoGetVerticalVelocity, AGL, ASL | VERTICAL_SPEED, AGL | "Vy, m/s", "H, m" |
| ac.gear / ac.flaps / ac.speedbrake | 0..1 | LoGetMechInfo gear/flaps/speedbrakes .value | LGEARS_STATE (mean), FLAPS, AIR_BRAKES | "gear, %", "flaps, %", "airbrake, %" |
| ac.gear_moving, ac.flaps_moving, ac.speedbrake_moving | 0/1 | derived: changed within 0.3 s (a jump of more than half the travel in one frame is a new aircraft, not movement) | | |
| ac.on_ground | 0/1 | gear strut rods compressed | LGEARS_PRESS > 0 | radio altimeter < 3 m, gear down, Vy ≈ 0 (heuristic) |
| ac.ground_speed / ac.ground_roll | m/s | horizontal velocity / derived (0 in the air) | EAS | IAS |
| ac.ammo / ac.rounds_fired / ac.firing | rounds, 0/1 | LoGetPayloadInfo().Cannon.shells counting down | GUN_FIRE events (one per bullet) | "ammo_counterN" (only cockpits with a round counter) |
| ac.stall_warning | 0/1 | LoGetMCPState().StallSignalization | - | - |
| ac.hits / ac.hit | count, pulse | - | HIT + DAMAGE events | - |
| ac.damage | count | LoGetMCPState() failures | - | - |
| ac.trim_pitch | -1..1 | - (not exported) | - | "trimmer" (cockpits that show it; range to verify) |
| ac.shake_amp / ac.shake_freq | 0..1, Hz | - | COCKPIT_SHAKE | - |
| ac.rpm | 0..1 | LoGetEngineInfo().RPM | - | - |

## Games

### DCS World (implemented)

- **What:** the export API (`Scripts\Export.lua` of the install documents it; `API\Sim_ControlAPI.md`): IAS/TAS (m/s),
  AoA/AoS (rad), acceleration in G (y = normal load), vertical speed, AGL/ASL, angular velocity, `LoGetMechInfo()`
  (gear/flaps/speed brake value and status, gear strut `rod` per wheel = weight on wheels, control surface positions),
  `LoGetPayloadInfo().Cannon.shells`, `LoGetMCPState()` (StallSignalization and failure flags), `LoGetEngineInfo()`.
  Trim is not exported generically (only per-module cockpit/draw arguments).
- **Rate:** the hook runs every simulation frame (`onSimulationFrame`) and sends at most 100 lines per second of model
  time; nothing while paused (model time stands still), so a pause silences the stick through the timeout.
- **How it is enabled:** `engine ffb telemetry setup dcs` writes `Saved Games\DCS*\Scripts\Hooks\JoyProFfb.lua`
  (every DCS Saved Games folder, or `--path`). DCS loads Hooks scripts itself, each in its own Lua environment, and
  the export functions are available there as `Export.Lo*`. So `Export.lua` is not changed at all and other tools
  (SRS, Tacview, DCS-BIOS, TelemFFB, SimShaker) keep working exactly as before. The hook only reads, sends UDP to
  127.0.0.1:41992 (setting `port`), never waits, and is wrapped in `pcall`. Idempotent; refuses to overwrite a file it
  did not write; backed up (`engine backups`); `--remove` deletes it. DCS must be closed (it loads hooks at start).
- **Line protocol:** `plane <unit type>` (aircraft change, every 5 s), `exit` (mission end), `noexport` (multiplayer
  server forbids ownship export), and `t=12.34;ias=154.2;aoa=0.081;g=3.42;...` (`DcsTelemetryProtocol`).
  Hook version 2 (for telemetry recording) keeps these keys and adds: position and attitude every frame
  (`lat`/`lon`/`alt_msl`, `heading`/`true_pitch`/`true_bank` from `LoGetSelfData()`, `pos_x/y/z`, velocity
  `vx/vy/vz`, and the ADI `pitch`/`bank`/`yaw` the protocol always listed); a `slow t=...;...` line 10 times a second
  (engine temperatures, fuel flow and quantity in kg, hydraulics, control surfaces, wheel brakes, canopy, hook, stores,
  chaff/flares, the `LoGetMCPState()` flags one by one, radar altimeter, magnetic heading, ILS deviations, wind, ACS
  requirements, targets and the first locked target; route and wingmen once a second) that the source adds to the
  following frames; `start`; `info key=text` session facts (map, mission, mission date/time, pilot, unit, group,
  coalition, game version); `objs`/`obj`/`objend` (the other objects once a second, only when the server allows
  object export); `ev <name> <text>` (onGameEvent, pause/resume, navigation mode changes) and `msg` (chat). The Lua
  source is `engine\DcsHooks\JoyProFfb.lua` (embedded in the engine). An installed version-1 hook shows as "outdated"
  in the setup status; Install (or `engine ffb telemetry setup dcs`) replaces it, DCS reads it at its next start.
  `DcsRecordingTelemetryTests` runs the hook in DCS's own `bin\luae.exe` (Lua 5.1) with stubbed DCS tables and parses
  what it sends (skipped without a DCS install; `JOYPRO_DCS_LUAE`, `JOYPRO_DCS_HOOK_FILE` to test an installed copy).
- **Multiplayer:** the ownship functions only work when the server allows player export
  (`LoIsOwnshipExportAllowed()`, server setting "Allow player export"); otherwise the hook says `noexport` and the
  status line explains it.

### IL-2 Great Battles and IL-2 Korea (implemented)

- **What:** the "telemetrydevice" UDP output (`<game>\Doc\UDP_Output\Il2_TeleOutput`, rev. 1.21; packet id
  0x54000101): indicators ENG_RPM/MP/SHAKE, LGEARS_STATE, LGEARS_PRESS (gear pressure = weight on wheels), EAS (m/s),
  AOA and AOS (rad), ACCELERATION (vector incl. gravity), COCKPIT_SHAKE (Hz, amplitude), AGL, FLAPS, AIR_BRAKES,
  VERTICAL_SPEED; events SET_FOCUS (aircraft name), GUN_FIRE (one per bullet), HIT, DAMAGE, EXPLOSION, DROP_BOMB,
  ROCKET_LAUNCH, server and client data. The separate "motiondevice" output (packet id 0x494C0100: Euler angles, body
  rotation rates and accelerations) and the "ffbdevice" output are parsed too but not needed by the built-in graph.
- **Rate:** 50 Hz / `decimation` (setup writes decimation 1 = 50 Hz when it creates the section).
- **How it is enabled:** `engine ffb telemetry setup il2` (or `il2korea`) edits `<game>\data\startup.cfg`:
  `[KEY = telemetrydevice]` takes several targets (`addr`, `addr1`, ... as "IP:port"), so JoyPro adds ONE line
  `addrN = "127.0.0.1:41994"` after the other tools' addresses and never changes them (no fan-out needed). Without a
  section it adds one (`addr = "127.0.0.1:41994"`, decimation 1, enable true, port 41994); a disabled output is
  switched on and the old value remembered. What was changed is recorded in `<data>\Ffb\Telemetry\il2-setup.json`, so
  `--remove` restores the file byte for byte. Backed up; idempotent; the game must be closed. The motion device is
  not touched (it has one target per the documentation; other tools use it).
- **Multiplayer:** the telemetry output is used online by other tools; no official statement found.

### War Thunder (implemented)

- **What:** the game's local web server, on by default while the game runs: `http://127.0.0.1:8111/state` (flight
  model: "valid", "IAS, km/h", "TAS, km/h", "M", "AoA, deg", "AoS, deg", "Ny", "Vy, m/s", "H, m", "Wx, deg/s",
  "gear, %", "flaps, %", "airbrake, %", control surfaces, engine values per engine) and `/indicators` (cockpit gauges:
  "type" = vehicle, "speed", "g_meter", "gears", "trimmer", "radio_altitude", "ammo_counterN", ... - only what the
  cockpit has). Outside a battle both say `{"valid": false}`. No stall warning, no damage, no weight on wheels, no
  generic gun field.
- **Rate:** JoyPro polls /state at 20 Hz (setting `pollHz`, 1..60) and /indicators every second request, only while
  aces.exe runs and the feature is on; if the server does not answer it retries every 2 s. The game's own web page
  polls every 500 ms; community tools poll every 250 ms; no rate limit is documented.
- **How it is enabled:** nothing to set up. Settings `url` must stay on this PC.
- **Recording extras** (`WarThunderTelemetrySource.RecordExtras`, off for force feedback; the session recorder
  switches it on through `WarThunderTelemetryFeed.RecordExtras`): while /state is valid, one extra request per poll
  period after /state: `/map_info.json` every 10 s (map_min/map_max in m, grid_steps, grid_zero = top left corner,
  map_generation, valid), `/map_obj.json` at 2 Hz (type, icon, color, x/y 0..1 of the map picture with y down, dx/dy,
  airfields sx/sy/ex/ey), `/hudmsg?lastEvt=&lastDmg=` at 1 Hz (events/damage with id, msg, time), `/mission.json`
  every 5 s (objectives with primary, status, text). Field names checked in the game's own page (webUi.vromfs.bin;
  the page polls all of them every 500 ms). Frame keys added: `wt.map.x_m` (east), `wt.map.y_m` (north),
  `wt.map.heading_deg`, `wt.att.roll_deg` / `wt.att.pitch_deg` (artificial horizon gauge, sign decided from the
  turn direction and flight path + AoA, default inverted). `/gamechat` and `/map.img` are never requested.

### MSFS 2020 / 2024 (telemetry recording source; no force feedback effects yet)

Implemented for the telemetry recording (`gui/JoyPro.Ffb.Core/Telemetry/Msfs`, docs/telemetry-recording.md): a
read-only native SimConnect client that loads the user's own `SimConnect.dll` with NativeLibrary (never shipped),
213 FLOAT64 simvars as `msfs.<name>` keys (98 tagged/changed-only at sim-frame rate, interval adapted to about 20 Hz;
115 once per second), 8 strings (TITLE, ATC ID, ...), system events and a 1 Hz traffic snapshot. Not verified
against a running sim yet. The research notes below still apply.

SimConnect (`Microsoft.FlightSimulator.SimConnect.dll` from the SDK + native `SimConnect.dll`, x64):
`SimConnect_RequestDataOnSimObject` with `SIMCONNECT_PERIOD_SIM_FRAME` (every sim frame) or VISUAL_FRAME/SECOND,
optional `SIMCONNECT_DATA_REQUEST_FLAG_CHANGED`. Useful simvars: AIRSPEED INDICATED/TRUE (knots), INCIDENCE ALPHA/BETA
(rad), STALL ALPHA, STALL WARNING (bool), G FORCE, ACCELERATION BODY X/Y/Z (ft/s²), ROTATION VELOCITY BODY X/Y/Z,
GEAR POSITION:n / GEAR TOTAL PCT EXTENDED, GEAR IS ON GROUND:n, CONTACT POINT COMPRESSION:n, SIM ON GROUND, GROUND
VELOCITY (knots), SURFACE TYPE, FLAPS HANDLE PERCENT, TRAILING EDGE FLAPS LEFT PERCENT, SPOILERS LEFT POSITION,
ELEVATOR TRIM POSITION (rad) / ELEVATOR TRIM PCT, AILERON/RUDDER TRIM PCT, DESIGN SPEED VS0/VS1 (to normalise stall
per aircraft). Simvar names are the same in 2020 and 2024; facility structures differ (load the SimConnect DLL that
matches the running exe if facilities are ever needed). Enabling: nothing in the sim (SimConnect is always on locally).

### X-Plane 11 / 12 (telemetry source done, no force feedback graph yet)

The source and its UDP feed exist for telemetry recording (`gui/JoyPro.Ffb.Core/Telemetry/XPlane`, keys `xp.*`,
see docs/telemetry-recording.md); the force feedback process does not use it yet (no `ac.*` deriver for X-Plane).


UDP on port 49000 ("Exchanging Data with X-Plane.rtfd" in the install's Instructions folder): request
`"RREF\0"` + `int freq` + `int index` + `char path[400]` (413 bytes; freq 0 unsubscribes; array elements as
`path[i]`); X-Plane answers to the sender's address with `"RREF"` + 1 byte + n × (int index, float value), at most 183
pairs per packet. Datarefs: sim/flightmodel/position/indicated_airspeed (kias), true_airspeed (m/s),
sim/flightmodel2/misc/AoA_angle_degrees, sim/flightmodel/position/alpha/beta (deg), sim/flightmodel/forces/g_nrml,
sim/cockpit2/annunciators/stall_warning (+ stall_warning_ratio), sim/flightmodel2/gear/deploy_ratio[10],
sim/flightmodel2/gear/on_ground[10], tire_vertical_deflection_mtr[10], sim/cockpit2/controls/flap_system_deploy_ratio,
sim/cockpit2/controls/elevator_trim / aileron_trim / rudder_trim (-1..1), sim/flightmodel2/controls/speedbrake_ratio,
sim/flightmodel/position/groundspeed, sim/weapons/firing[25], sim/weapons/bul_rounds[25], sim/time/paused. The
X-Plane 12 Web API (REST/WebSocket on 8086) updates at ~10 Hz: too slow for force feedback. In X-Plane 12, Settings >
Network can block incoming UDP.

## Ports

JoyPro: DCS 41992 (overlay hook: 1992), IL-2 41994, War Thunder 8111 (the game's). Avoided: TelemFFB 34380 (DCS),
34385 (IL-2), 34386 (IL-2 Korea), 34390 (X-Plane); IL-2 defaults 4321/4322; SimShaker 29373; Tacview 42674/42675;
DCS-BIOS 5010/7778; SRS 5002-5003, 5068-5069, 7080/7082, 9040, 9084-9087; X-Plane 49000/49707/8086. All ports are
settings.

## Settings and command line

```
engine ffb telemetry status [--text]
engine ffb telemetry effects
engine ffb telemetry config show
engine ffb telemetry config set --param enabled=true --param max-force=0.6 --param device={AD5168C0-...}
engine ffb telemetry config set --game DCS --param graph=my-dcs.jpffb.json --param strength=0.8
engine ffb telemetry config set --game DCS --plane F-16C_50 --param graph=viper.jpffb.json
engine ffb telemetry config set --game IL2Game --effect buffet --param enabled=false
engine ffb telemetry config clear --game DCS --plane F-16C_50
engine ffb telemetry config set --device {AD5168C0-...} --param invert-y=true
engine ffb telemetry config clear --device {AD5168C0-...}
engine ffb telemetry setup dcs|il2|il2korea|warthunder [--remove] [--dry-run] [--path <folder>]
```

Global keys: enabled, device (DirectInput instance GUID; empty = the first device with force axes, so vJoy is not
picked), device-name, strength (0..1), max-force (0..1, default 0.6), invert-x, invert-y (devices without their own
entry), tick-rate (10..200), timeout (0.1..5 s), centring-spring (true = JoyPro provides the centring spring, default),
centring-stiffness (0..1, default 0.3). Per device (`--device {guid}`): invert-x, invert-y. Per game: enabled, port,
graph, strength (0..2), url, poll-hz. Per aircraft (DCS unit type, IL-2 focus object, War Thunder vehicle): graph,
strength. Per effect: enabled, strength (0..2). The GUI saves the direction for the picked device and globally.
A running JoyPro.Ffb takes a new invert on its next start (the effects' directions are set when they are created).

## Tests (no hardware movement)

- `dotnet test gui/JoyPro.Ffb.Core.Tests`: graph, nodes, persistence, runtime safety rules, IL-2 packets, and
  `TelemetryFlightTests`: the synthetic flight (`SyntheticFlight`: cruise, pull to 22 deg AoA, gear down, guns,
  landing roll) as DCS lines, IL-2 packets and War Thunder JSON through the real sources, the normalised model, the
  built-in graph and the mixer: buffet only above 10 deg AoA and growing with it; gear rumble only while the gear
  moves; pulsed gun bursts only while firing; runway rumble only on the ground, weaker as it slows; IL-2 hit jolt;
  switched-off effects stay off; the cap holds at every update with several effects at once; one STOP ALL on
  telemetry timeout and nothing until telemetry returns; the host over loopback UDP follows a game process and stops
  when it is killed; War Thunder polling of a local HTTP stub, `valid: false` gives no frames, no requests after stop;
  an idle runtime does ~1 loop check per second instead of 100. `TrimInvertTests`: input names, hat diagonals, device
  matching, Invert node and switches, button nodes, stick position, trim (steps, repeat rate, limit, here/follow,
  reset, invert, move rate, kept over a reset), the built-in graph's trim wiring, centring modes (plain spring, off,
  springs held with their trim during a pause, STOP ALL on disable), config round trip. `DirectionWizardTests`: the
  test's state machine with a fake output (inverts from the answers, forces always stopped, cancel in every step,
  output errors, the VPforce / general hints).
- `dotnet test gui/JoyPro.Gui.Tests` (`FfbTrimDirectionTests`): the page's direction test with a fake output, the
  VPforce hint, centring and per-device invert settings to the engine, the test process protocol against a fake
  process, the editor's palette, labels, "press the button now" and "Assign trim buttons".
- `engine/tests/smoke/ffbtelemetry.ps1` (part of `engine/tests/engine_smoke_test.ps1`, ~2 min): the CLI settings and
  setup on temp copies (DCS hook with an existing Export.lua that must stay byte-identical, a foreign file that must not
  be overwritten, port change -> update; IL-2 startup.cfg with and without another tool's telemetry section, CRLF kept,
  motion device untouched, remove restores byte for byte), then `JoyPro.Ffb run --sink dry` against
  `JoyPro.Ffb simulate` for DCS, IL-2 and War Thunder at once, a telemetry gap, a game process that is killed, and the
  60 s resource run (budget: < 5 % of a core active, < 0.5 % idle, private memory after a 21 s warm-up: median of 43-61 s less than 3 MB above the median of 21-39 s and under 40 MB, handles < +25,
  threads < +5, compared the same way; effect lengths with a tolerance from the measured lateness of the run), and the telemetry gap with "JoyPro provides the centring spring" (only springs, on both axes, no
  STOP ALL until the end).

## Hardware re-test of this change (manual, on the real Rhino; not part of the automated tests)

1. VPforce Configurator: note the settings; untick Effects > "Spring", Apply. Emergency stop within reach.
2. JoyPro page: Refresh devices, pick the Rhino; the VPforce hint shows. Start the **Direction test**: Start -> Ready:
   the stick goes RIGHT -> answer; Ready: FORWARD -> answer "Back" expected while invert Y is not set yet (saved
   inverts: X off, Y on). Spring step: move the stick, let go: it springs back. Trim step: the stick moves forward
   about a third and stays; the measured value is shown (negative = forward). Cancel once mid-push: the force stops
   at once.
3. Same with the Configurator "Spring" ticked: the spring and trim steps should fail and show the hint.
4. Editor: select "Stick trim", "Assign trim buttons": press the trim-here button, the hat up/down/left/right and a
   reset button (a throttle hat works too). Save the graph and pick it for DCS.
5. `JoyPro.Ffb run --game DCS --no-game-check` + `JoyPro.Ffb simulate --game DCS --port 41992 --seconds 30` (or DCS):
   hat up moves the centre forward in steps, holding it repeats; hold trim-here, move the stick, let go: it stays
   there; reset returns it slowly. The spring must not jitter or pulse (no restarts).
6. Let the simulator end (telemetry stops): the stick keeps its trimmed spring (held), no slack. Then end JoyPro.Ffb
   (Ctrl+C): released; with the Configurator spring off the stick is slack now (expected), with it on it centres.
7. Kill JoyPro.Ffb with Task Manager while a spring and a force run: the force must stop at once; check that the spring
   also ends (the exclusive acquisition is lost). If the spring stays, report it (it would need a watchdog).

### Re-test 3: the Y spring centre (after "the Y offset acted on X")

Configurator spring off, hands off, positions with winmm. `JoyPro.Ffb test --device {rhino}` and type:
1. `invert 0 1`, `spring 0.8 0 0` (wait 2 s, `pos`), `spring 0.8 0 -0.35` (wait 2 s, `pos`): with the combined layout
   Y should move forward (by about a third) and X stay centred; `spring 0.8 0.3 0`: X moves right, Y stays.
2. If Y still does not move: `stop`, `conditions separate`, repeat 1; then `conditions restart`, repeat 1. Note which
   layout moves the right axis; the log (`ffb-<date>.log`, category FfbTest) has every block sent.
3. GUI: the direction test's buttons read "Start" / "Ready" in full (100 % and 150 % scaling); "Repeat this step" repeats
   the push / spring / trim; "Start over" goes back to the beginning. The GUI log lists the steps and answers.

## First hardware test (needs the user)

Nothing in this phase has sent a force to a real device; the DirectInput sign convention and the behaviour of the
Rhino on a killed process are unverified. Proposed procedure:

1. Close the game. In VPforce Configurator note the current settings; keep its emergency stop / the Rhino's power
   switch within reach. The abort key for the test is closing JoyPro.Ffb (Ctrl+C in its console, or
   `Stop-Process -Name JoyPro.Ffb`), which sends STOP ALL.
2. `JoyPro.Ffb devices` and set `device` to the Rhino's instance GUID; set `max-force=0.15`, `strength=0.5`.
3. Hands OFF the stick: `JoyPro.Ffb run --game DCS --no-game-check` and `JoyPro.Ffb simulate --game DCS --port 41992
   --seconds 30`. Watch: buffet at 3-5 s, gear rumble at 6-7 s, gun bursts at 7-8 s, runway at 8-10 s, nothing after
   the simulator ends (STOP ALL after 0.5 s). Check the G weight pushes FORWARD; if not, set `invert-y=true`
   (and `invert-x` for the roll spring/jolt).
4. Kill the process with Task Manager while forces run: the stick must go slack within ~0.3 s (finite effect
   durations). If not, report it - the backend would need infinite durations plus a watchdog instead.
5. Hands ON the stick, repeat 3 with `max-force=0.3`, then 0.6.
6. In DCS (game FFB off for the Rhino): install the hook, fly; check the status line and the inspector. Then IL-2 and
   War Thunder the same way.

### Result of the first hardware test (2026-10-11, VPforce Rhino, synthetic DCS flight, cap 0.3, strength 1)
- Vibration and rumble effects (buffet, gear, guns, runway) were felt by the user; the host reported no DirectInput
  error. A process killed with Stop-Process mid-run: the stick was slack at once (finite effect durations work).
- The first two runs (cap 0.15, strength 0.5) moved nothing: the Rhino rested off centre without its own spring
  (motors apparently inactive at that moment); measured with winmm joyGetPosEx (a read-only position logger).
- Not felt yet: a steady pull (G weight) or the trim spring, so the force direction (`invert-x`/`invert-y`) is still
  to be checked in the game.

## Open points

- Live verification of all three games (hook loads in DCS hooks state, IL-2 keeps the added lines when it rewrites
  startup.cfg, War Thunder "trimmer" range and sign, the radio-altimeter on-ground heuristic).
- Re-test on the Rhino (see "Hardware re-test of this change"): infinite springs after a killed process, trim and the
  held springs. DIPROP_AUTOCENTER is switched off while JoyPro owns the device and on again on release (it does not
  control the VPforce Configurator's spring).
- DCS trim and stall warnings for modules that do not use LoGetMCPState; per-module draw arguments could add them.
- MSFS (SimConnect) and X-Plane (RREF) sources; per-aircraft AoA thresholds (e.g. from MSFS STALL ALPHA).

## Sources

- DCS: `<DCS>\Scripts\Export.lua` (function list and units), `<DCS>\API\Sim_ControlAPI.md` (Hooks, `Export.Lo*`, export
  permissions); https://wiki.hoggitworld.com/view/DCS_Export_Script
- IL-2: `<IL-2 Korea>\Doc\UDP_Output\Il2_TeleOutput.odt` (rev. 1.21), `Il2_MotionOutput.odt`, `IL2_ffbOutput.doc`;
  https://forum.il2sturmovik.com/topic/72958-key-telemetrydevice-what-are-the-packet_id-outputs/ ,
  https://forum.il2sturmovik.com/topic/44048-motion-support-for-il-2/ ,
  https://forum.il2sturmovik.com/topic/91259-vpforce-startupcfg-query/
- War Thunder: the game's own local page `http://127.0.0.1:8111` (served by the running game: `/state`, `/indicators`),
  https://forum.warthunder.com/t/weapon-and-ammunition-info-in-localhost/345655 , sample
  answers in public repositories (lucasvmx/WarTelemetry, Ktpls/wtutility; key names only).
- MSFS: https://docs.flightsimulator.com/msfs2024/flighting/programming-apis/simvars/aircraft-simvars/aircraft-flight-model-variables/index.md ,
  .../aircraft-brake-and-landing-gear-variables/index.md , .../aircraft-control-variables/index.md ,
  https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/API_Reference/Events_And_Data/SimConnect_RequestDataOnSimObject.htm ,
  https://devsupport.flightsimulator.com/t/2020-2024-compatible-microsoft-flightsimulator-simconnect-dll/11844
- X-Plane: `<X-Plane 12>\Instructions\Exchanging Data with X-Plane.rtfd`, `Resources\plugins\DataRefs.txt`;
  https://developer.x-plane.com/article/x-plane-web-api/
- Ports of other tools: https://github.com/walmis/VPforce-TelemFFB (README; port numbers only),
  https://github.com/ciribob/DCS-SimpleRadioStandalone (port constants), Tacview and DCS-BIOS documentation.
- DirectInput: the DirectX SDK documentation of IDirectInputDevice8::CreateEffect, DIEFFECT, DIPERIODIC, DICONDITION,
  SetCooperativeLevel, SendForceFeedbackCommand (https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ee416842(v=vs.85)),
  "Device Properties" (DIPROP_AUTOCENTER, properties only change while unacquired:
  https://msdn.microsoft.com/en-us/library/ee416595(v=vs.85)), "Creating the DirectInput Force-Feedback Device"
  (https://learn.microsoft.com/en-us/previous-versions/ee418821(v=vs.85)).
- What DirectInput sends on Acquire / Unacquire / DISFFC_* (USB captures of Windows, as implemented by Wine; read for
  the behaviour only): Wine `dlls/dinput/joystick_hid.c` (https://github.com/wine-mirror/wine) and the merge request
  "dinput: implement DIPROP_AUTOCENTER" (https://gitlab.winehq.org/wine/wine/-/merge_requests/4830). USB HID PID 1.0
  "Device Control" usages (Enable/Disable Actuators, Stop All Effects, Device Reset, Device Pause/Continue).
