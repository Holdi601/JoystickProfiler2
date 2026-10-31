# Virtual joystick mode (vJoy + HidHide)

Per game you choose: the game uses your **physical devices** (native, as always) or one **virtual joystick**
(virtual). In virtual mode JoyPro puts every input that the game's relations use - axes, buttons, hats and the buttons
of their modifiers, from all your sticks, throttles and panels - onto one vJoy device (more only when one is not
enough: a vJoy device has at most 8 axes, 128 buttons and 4 hats; up to 16 devices). The export writes the game's
binds to the vJoy device, `JoyPro.VJoy.exe` copies the physical inputs to it live, and HidHide hides the physical
devices from that game, and only from that game.

Why: some games take only a few joysticks (Arma Reforger: the first four that send input), pick up the wrong ones or
renumber them when a device is added. With virtual mode the game sees one device whose inputs never move.

Needs: [vJoy](https://github.com/jshafer817/vJoy) 2.1.9 and [HidHide](https://github.com/nefarius/HidHide) 1.5
(both free). JoyPro configures both itself; each change asks Windows for administrator rights once. Not installed:
the page's "What this page needs" (and Settings > Components) installs the official, pinned and checked installers on
the user's click (vJoy 2.1.9.1, HidHide 1.5.230, ViGEmBus 1.22.0; docs/components.md).

## How it fits together

| Part | Where | What it does |
|---|---|---|
| Mapping table | profile (`Pr0file.VirtualJoystick`, JSON) | physical device + input -> vJoy device + input; never reshuffled |
| `engine vjoy ...` | `engine/EngineVJoy.cs`, `engine/VJoy/VJoyMapping.cs` | per-game mode, table, status, vJoy setup, HidHide session |
| Export/import scope | `EngineVJoy.GameScope` around every game command | the game's binds point at the vJoy slots while the game is exported, imported, scanned or synced |
| HidHide session | `engine/VJoy/HidHideSession.cs` (also linked into the GUI) | plan, journal, apply, verify, restore, crash recovery |
| Forwarder | `gui/JoyPro.VJoy` (`<GUI>\vjoy\JoyPro.VJoy.exe`) | DirectInput (non-exclusive) -> vJoyInterface.dll, live |
| GUI page | `Virtual Joystick` (bottom bar), `MainWindowViewModel.VirtualJoystick.cs` | toggles, status, table, Start/Test/Stop, live view |
| Virtual functions | `engine/VJoy/VirtualFunctions.cs`, `VirtualFunctionRuntime.cs` (shared), `EngineVJoyFunctions.cs`, GUI `MainWindowViewModel.VirtualFunctions.cs`, `VirtualFunctionEditorViewModel.cs` | functions between the physical inputs and the virtual outputs, evaluated by the forwarder every pass |
| Game input limits | `engine/VJoy/GameInputLimits.cs` | what a game reads of one joystick; warnings above it |

### The mapping table

- Inputs: every relation with an active node of a game in virtual mode and a bind on a game controller (keyboard and
  mouse stay as they are), plus the modifier buttons of those binds. vJoy devices are never a source.
- A hat is one slot (its eight directions go along: `JOY_BTN_POV1_UR` on the stick becomes `JOY_BTN_POV2_UR` on vJoy if
  the stick's hat 1 sits on vJoy hat 2).
- New inputs get a slot in a fixed order (device name, then axes, buttons, hats by number). An axis keeps its own axis
  when it is free (X stays X), else the first free one; buttons and hats take the next free number. A physical device
  stays on the vJoy device it already uses where it fits.
- Existing slots never move. A slot no relation uses any more stays (`--prune` drops it, `--reset` starts over), so the
  game's binds are the same from run to run. One table serves all virtual games: a physical input has the same vJoy
  input in every game.
- `engine vjoy settings --first-device N --last-device M` limits the vJoy devices JoyPro uses (default 1-16). JoyPro's
  in-game test kit (tools/e2e) uses vJoy device 1; virtual mode and the kit do not run at the same time.

### Export, import, sync

While a game in virtual mode is exported, imported, scanned or synced (`engine dcs|il2|sc|<game> export|import|scan`,
`engine sync status|diff|apply|accept --game <game>`), every bind on a mapped physical input (and every modifier on a
mapped button) points at its vJoy slot; afterwards the profile gets its physical devices back. Binds an import or a
sync brings in on vJoy slots are turned back into the physical inputs. So the game file holds vJoy binds, the profile
always holds your real devices. If the table needs a new slot during an export, it is given and saved. If a needed vJoy
device is missing (not configured, or DirectInput does not list it), the export stops with a message instead of writing
binds nobody can use.

The vJoy device's identity in the games is its DirectInput name (`vJoy Device {GUID}`); vJoy numbers its devices by
HID collection (`&col01` = the first configured device). Games that tell devices apart only by name (for example
WARDOGS, Arma Reforger) cannot tell several vJoy devices apart: keep such games within one vJoy device.

### vJoy devices in imports (2.5.0)

vJoy devices are imported and exported like any other device: a user may feed one on purpose (VoiceAttack, his own
feeder) and bind it in the game; no default ignore list leaves them out. Only the vJoy device JoyPro tunnels into is
JoyPro's own: Settings "vJoy device JoyPro tunnels into" (instance 1-16, default none) passes `--vjoy-tunnel <n>` to
every import and scan (`engine dcs|il2|starcitizen import|scan`, `engine <game> import|scan`). The engine finds that instance's device the way the virtual mode does (the
HID collection `&col0n` against the configured vJoy devices, `EngineVJoy.ScanDevices`) and adds its `vJoy Device
{GUID}` to the ignore list (registry games: its binds are dropped from the read); every other vJoy instance stays a
normal device. When that vJoy device is not connected nothing is left out and the log says so. Checked by
`engine\tests\smoke\vjoytunnel.ps1` (two fake vJoy devices: without the option both are imported, `--vjoy-tunnel 1`
leaves out only device 1, `2` only device 2, `17` is refused).

### Virtual Xbox pad (mode "xbox", ViGEmBus)

For games that only read Xbox controllers (XInput) or only one device, a game can use a **virtual Xbox 360 pad**
instead of vJoy: `engine vjoy mode <game> xbox` (GUI: "Xbox pad"). It needs the free ViGEmBus driver by Nefarius
(BSD-3-Clause). JoyPro detects it (`engine vjoy status`: installed, running, version) and explains it; the GUI installs
the official ViGEmBus 1.22.0 on the user's click and consent (docs/components.md). The engine never installs drivers,
and no test touches the driver.

- **Per game its own table** (`VJoySettings.XboxSlots[game]`, in the profile, stable like the vJoy table), because an
  XInput game takes one pad and has its own layout.
- **The game's layout first**: when the game's catalog knows the default gamepad key of an action (5th column of
  `DB\<Game>\actions.tsv`, an Unreal FKey name such as `Gamepad_LeftX`, `Gamepad_LeftTriggerAxis`,
  `Gamepad_FaceButton_Bottom`, `Gamepad_LeftStick_Up`), the physical input of that relation is pinned to that control.
  Two axes on one control: the one moved last wins (a note says so). An input whose kind does not fit the control
  (a button on an action that sits on a stick axis) gets a free control and a note.
- **The rest in order**: axes -> left stick X/Y, right stick X/Y, left/right trigger; buttons -> A B X Y LB RB Back
  Start LS RS, then the D-pad directions, then the triggers (pressed = fully pulled); a hat -> the D-pad (diagonals
  included). Guide is never given (Windows opens the Game Bar on it). A button can also push a stick half
  (`XB_LX+`, `XB_LY-` ...) when the game's layout wants that. **What does not fit is listed per game** (status, plan,
  map, GUI mapping tab).
- **One axis on both triggers** (`XB_TRIGGERS`): when one physical axis drives two actions of a relation that the layout
  puts on LT and RT (a collective on "ascend" and "descend"), the forwarder splits it at the axis centre: above it RT,
  below it LT, both up at the centre.
- **Reversed axes**: *Invert* on the relation's bind reverses the pad axis (target suffix `~`, e.g. `XB_RX~`,
  `XB_TRIGGERS~`).
- **Layout chosen in the game**: a game can give its gamepad layout itself (`IGameGamepadLayout`): Hell Let Loose:
  Vietnam reads the layout A/B/C picked in the game's settings, and `engine vjoy map` moves the pins when it changes.
- **Forwarding**: `engine vjoy forwarder-config --xbox-game <game>` writes that game's table (the GUI passes the game that
  started; without it the first Xbox game). `JoyPro.VJoy run --xbox-sink vigem` creates the pad for the run (neutral at
  start and end, disconnected afterwards); `--xbox-sink dry` (default, tests, the GUI's Test button) writes nothing.
  The report is computed from the last state of every physical device: two buttons on one control combine, a pressed
  button on a stick half wins over the axis on that stick, triggers take the larger value; stick Y follows XInput (up
  = positive), so a physical stick pushed forward gives a positive Y. No allocation per pass.
- **Export**: games that can store gamepad binds (`IGameGamepadBinds`) get their binds on the virtual pad
  (`Xbox 360 Controller (JoyPro virtual pad)` with `XB_*` inputs). Games with fixed gamepad layouts (Hell Let Loose:
  Vietnam) keep their layout; JoyPro's table follows it, so nothing has to be written.
- **HidHide**: as for vJoy - the physical devices of the Xbox games' tables are hidden from those games only. (An
  XInput-only game ignores DirectInput devices anyway; hiding keeps a real Xbox pad or a stick that pretends to be one
  out of the way.)

Games of this class and how to add more: docs/games/one-device-games.md.

### One-device games: the suggestion

Games that read one joystick or only Xbox pads declare it (`IGameDeviceLimit`): Battlefield 6/2042 (one joystick,
`virtual`), Hell Let Loose: Vietnam and ACE COMBAT 8 (no joystick, `xbox`).
- `engine vjoy status` shows "one-device game: <mode> suggested" while such a game is native, and the status JSON has
  `SuggestedMode`, `MaxJoysticks` and `DeviceLimitNote` per game.
- `engine vjoy mode <game> native` warns.
- The GUI's Games list shows the suggestion with a **Use it** button.
- An export in native mode warns when the binds use more joysticks than the game reads.

**Axes a game must not see:** a game can rule out vJoy axes (`AvoidVJoyAxes`). Battlefield takes a joystick with Rx/Ry
for an Xbox pad and then hides its joystick page. While such a game is in virtual mode:
- the planner gives no new input those axes;
- a kept slot on one of them is flagged (move it with `engine vjoy map --reset`);
- `configure-vjoy` sets the vJoy device up without them, e.g. `vJoyConfig 1 -f -a x y z rz sl0 sl1 ...`. This is the
  one case where a device loses something: an avoided axis the table does not use.

### vJoy configuration

`engine vjoy status|plan` show per vJoy device what the table needs and what it has. `engine vjoy configure-vjoy`
runs `vJoyConfig <n> -f -a x y z rx ry rz sl0 sl1 -b <buttons> -p <hats>` for each device that lacks something - one
administrator prompt for all (several devices go through one elevated `engine vjoy elevated-run`). The new
configuration is the union of what the device has and what the table needs (all eight axes as soon as one is used,
buttons in steps of 32, continuous hats), so a device never gets smaller. Afterwards export the virtual games again:
games may list a reconfigured vJoy device anew, and adding a second vJoy device can change the first one's DirectInput
identity (it gets a HID collection number).

**vJoy's maximum.** A vJoy device offers up to 8 axes, 128 buttons and 4 hats (16 devices). JoyPro plans for that
maximum: the table and the virtual functions use buttons 1-128; what the configured device lacks shows as "needs"
(status, plan, the page). `engine vjoy configure-vjoy --max [--device N] [--dry-run]` (GUI: "Set up vJoy for maximum"
on the Overview, shown while a device has less, asks first) gives every vJoy device JoyPro uses
`vJoyConfig <n> -f -a x y z rx ry rz sl0 sl1 -b 128 -p 4` (force feedback kept; an axis a virtual game must not see,
Battlefield's Rx/Ry, stays out unless the table uses it). **Windows must restart** before a vJoyConfig change takes
effect (vJoy asks for it; on the development PC every change needed a restart); JoyPro never restarts Windows and the
result says so. `engine vjoy status` reports `AtMaximum` per vJoy device. Tests only ever run the dry run.

### What a game reads of one joystick

JoyPro sets vJoy up for 128 buttons, but a game may read less: DirectInput's `DIJOYSTATE` has 32 buttons,
`DIJOYSTATE2` 128; some games read one hat or none. `engine/VJoy/GameInputLimits.cs` keeps what is known per game, with
the confidence (verified = JoyPro's game code enforces it or it was read from the game's files/binary, documented =
the game's own docs or help, reported = players/forums) and the source; `engine vjoy limits [--game g]` lists it, the
status JSON has `InputLimit` and `LimitWarnings` per game. Where a game's table slots or virtual function outputs lie
above what it reads, the plan, `engine vjoy status`, `functions list|add` and the game's line on the page warn:
"Arma 2: Operation Arrowhead reads only the first 32 buttons of a joystick: the virtual function outputs on button 40 on
vJoy 1 will not work there." Unknown limits never warn. Lower button numbers come first everywhere (slots and
function outputs take the lowest free one), so such games keep working while the table is small.

| Game | Buttons | Axes | Hats | Confidence | Note |
|---|---|---|---|---|---|
| DCS World | 128 | 8 | 4 | documented | |
| IL-2 Great Battles | 128 | 8 | 4 | documented | 128 since 5.204 |
| IL-2 Cliffs of Dover / Blitz | 128 | 8 | 1 | documented | 128 since 5.025; one hat per device in the file format |
| IL-2 1946 | 32 | 8 | 4 | reported | 4 hats shared by all devices |
| Falcon BMS | 128 | 8 | 2 | documented | 128 with `g_nButtonsPerDevice 128` (4.37 launcher), else 32; two 8-way hats in all |
| FSX / Prepar3D | 32 | 8 | ? | reported | |
| MSFS 2020/2024 | ? | 8 | 1 (4-way) | documented | more than 32 in the sim, SimConnect add-ons see 0-31; JoyPro writes one hat |
| X-Plane 11/12 | 160 | 25 | 1 | verified | further hats become buttons |
| ACE COMBAT 7 | 32 | 8 | 4 (4-way) | documented | the game's Input.ini help |
| Arma 2 / OA | 32 | 8 | 1 | verified | DIJOYSTATE, reverse engineered |
| X4: Foundations | 64 | 8 | 1 | verified | from the game binary |
| G-Rebels | 96 | 24 | 0 | documented | Unreal's Raw Input plugin |
| VTOL VR (BYOJoystick) | 128 | 8 | 4 (4-way) | verified | |
| ACE COMBAT 8, Hell Let Loose: Vietnam | - | - | - | documented | XInput only: virtual Xbox pad |

Not known (no warning): Star Citizen (sources conflict), War Thunder, Arma 3, Arma Reforger, Battlefield 6/2042 (the
enum has 36 buttons, players see about 18), Elite Dangerous (players report 32, Joy_41 worked on the development PC),
IL-2 Korea, WARDOGS, the Rewired games (Nuclear Option, Tiny Combat Arena).

### HidHide

HidHide's configuration is global: a cloak switch, hidden devices (device instance paths) and a program list, which is
an allow list (only listed programs see hidden devices) or, in inverse mode, a block list (only listed programs lose
them). JoyPro:

- uses the **block list** with the executables of the virtual-mode games, so only those games stop seeing the physical
  devices; JoyPro's forwarder, JoyPro, SimHub, Discord and every other program keep seeing them;
- keeps an **allow list** the user already uses (inverse off, cloaking on, own entries): then JoyPro's forwarder and
  JoyPro are allowed and every other program loses the hidden devices while the session runs (the page says so);
- **parks** the user's own entries when cloaking was off (dormant): they are taken off the lists for the session and
  put back exactly afterwards, so switching cloaking on does not wake up an old setup (for example a hidden vJoy or a
  blocked MSFS);
- never hides a vJoy device the games need (a hidden one is shown for the session and hidden again afterwards), and
  takes a trusted program (the forwarder, JoyPro) off a block list for the session;
- hides the physical devices by the instance path HidHide itself reports (`HidHideCLI --dev-gaming`), falling back to
  one derived from the HID path;
- finds the game executables in the game's installation (marker files and process names, a few levels deep), plus the
  ones set with `engine vjoy exe <game> --add <path>` and the running game's executable when the GUI starts for it.

Reading HidHide: one `HidHideCLI` query per call (`--cloak-state`, `--inv-state`, `--app-list`, `--dev-list`; combined
queries and `--dev-gaming` can hang without administrator rights), each with a time limit (`JOYPRO_HIDHIDE_TIMEOUT_MS`,
default 8 s); a call that runs over is ended with its child processes, so no HidHideCLI is ever left running (a hung
one makes every other HidHideCLI call fail with "Access is denied"). If another HidHideCLI is already running, JoyPro
does not start one and says so. A part that cannot be read (time limit, "Error code 0x0005 ... Access is denied" - the
hidden-device list needs administrator rights) is **unknown**, never "off" or "empty": `engine vjoy status` shows it
as unknown, a dry run says its plan is partial and which parts are missing, and a real session or restore stops with
nothing changed. A journal whose "previous" lacks the inverse mode never has the inverse mode set back.

Changing HidHide is one elevated step (one UAC prompt; never bypassed): `apply-hidhide`, `restore-hidhide` and
`recover` start the engine elevated (`engine vjoy elevated-hidhide`, the request passed on its command line, not in a
file), and that process does everything: reads HidHide completely, resolves the device instance paths (`--dev-gaming`),
writes the journal, runs all changes in one `HidHideCLI` call, reads back, compares and writes its result file for the
caller. Nothing that needs the rights runs outside it. Before the change JoyPro writes a journal (`<data>\VirtualJoystick\hidhide-session.txt`, `<data>` = `JOYPRO_DATA_DIR` or
`%LOCALAPPDATA%\JoyPro2`) with the state before, what it adds, what it parks and the owner process (the GUI's pid and
start time). After the call it reads HidHide back and compares. Restore undoes exactly the journal's changes against
the configuration found then (entries the user added meanwhile stay), cloaking off first, and moves the journal to
`history\`. Order on apply: lists first, cloaking last; on restore: cloaking off first - so nothing is hidden while the
lists are half done.

Crash safety: a journal whose owner process is gone is **stale**. The GUI checks the journal when it starts and
restores a stale session at once (`engine vjoy recover`, one prompt); `apply-hidhide` refuses while a stale session
waits. Closing JoyPro stops the forwarder and restores HidHide through an engine process that outlives the window. If
the prompt is declined, the next start does it. By hand: `engine vjoy restore-hidhide`, or in the HidHide client untick
"Enable device hiding".

## Virtual functions

Per game in virtual mode (vJoy or Xbox pad) the user adds functions that the forwarder evaluates every pass between
the physical inputs and the virtual outputs - Joystick Gremlin-like features inside JoyPro's virtual mode.

| Type id | Name | Inputs | Outputs | Settings |
|---|---|---|---|---|
| `bands` | ≡ Axis bands | in (axis) | band1..bandN (buttons, each hold / enter / leave / both) | edges (N-1, 0..1), hysteresis, pulse length |
| `cycle` | ↻ Button cycle | next, prev (opt.), reset (opt.) | step1..stepN | pulse or hold (selector), wrap or stay, pulse length |
| `toggle` | ◐ Toggle (latch) | in | out (latched) | on or off at start |
| `switch` | ⇅ Switch to on/off pulses | in (maintained switch) | on, off (pulses) | pulse length, pulse the position at start |
| `presses` | ⋯ Short / long / double press | in | short, long, double (all optional) | long after, double within, long = pulse or hold |
| `shift` | ⇧ Shift layer | shift, in1..inN (any kind) | out1..outN (shifted), base1..baseN (unshifted; default: the input's own slot) | hold or toggle |
| `merge` | ⋈ Merge two axes | a, b (axes) | out | difference / sum / average / larger / smaller, invert A/B |
| `split` | ⋔ Split an axis | in | low, high (axes) | split point |
| `curve` | ∿ Axis curve | in | out | JoyPro's axis tuning (dead zone, saturation X/Y, curvature, invert, slider; `AxisModels.EvaluateDcs`) |
| `axis-buttons` | ⇔ Axis ends to buttons | in | low, high (hold / enter / leave / both) | end zone, hysteresis, pulse length |
| `axes-hat` | ✛ Axes to hat | x, y | hat | deflection, 4 or 8 ways |
| `buttons-axis` | ± Buttons to axis | up, down, centre (opt.) | out (axis) | speed (travel/s), start, spring back speed |
| `encoder` | ⟳ Axis to encoder pulses | in | up, down | step, pulse, gap, queue |
| `turbo` | » Turbo (repeat) | in | out | pressed for, released for, while held or press on/press off |
| `macro` | ▶ Macro | trigger | out1..outN, keys | steps (after ms: tap/press/release an output or key, hold ms), once or repeat while held |
| `hat-buttons` | ✣ Hat to buttons | hat | up, right, down, left, 4 diagonals (opt.) | 4 ways (a diagonal presses both neighbours) or 8 |
| `buttons-hat` | ⊕ Buttons to hat (8-way from 4 switches) | up, right, down, left | hat | diagonal settle time |

Inputs are physical inputs (`JOY_X` .. `JOY_SLIDER2`, `JOY_BTN<n>`, `POV<n>`, a hat direction `JOY_BTN_POV<n>_<dir>`);
outputs are vJoy inputs (`JOY_BTN<n>`, an axis, `POV<n>` on a vJoy device), controls of the virtual Xbox pad (`XB_A`,
`XB_LT`, `XB_LX`, `XB_DPAD`, ...) or keys (`KEY_A`, `KEY_LCTRL`, `KEY_F1`, ... - **only** with the function's
`Keyboard` switch on; keys go out through SendInput with scan codes and only with a real sink, never in a dry run or
the GUI's Test).

**Storage and planning.** Functions live in the profile with the game's virtual-mode settings
(`VJoySettings.Functions[game]`, ids `f1`, `f2`, ...). Their vJoy outputs are **reserved** in the table: the planner never
gives them to a physical input, they count in what the vJoy device must offer ("+ N function output(s)"), and an output
that collides with a slot or another function is flagged. New functions get free outputs (`functions add`, or an output
set to `auto`): the lowest free button/axis/hat on devices first..last (vJoy's maximum assumed; an output above what the
device has now says "Set up vJoy for maximum", one above what the game reads says so too); on the Xbox pad free
controls the game's pad table does not use. A shift layer's unshifted output defaults to the input's own slot, so the
layer takes the input away from it while shifted. A function's output is bound in relations like any vJoy input
(device `vJoy Device {GUID}`, input `JOY_BTN<n>`; the editor's "Bind relation" runs `engine binds set`); exports leave
those binds as they are. The physical devices a function reads are hidden by HidHide like the table's.

**Evaluation.** `engine/VJoy/VirtualFunctions.cs` (model, catalog, tokens, resolution) and
`engine/VJoy/VirtualFunctionRuntime.cs` (evaluation, simulator) are plain C# 7.3, compiled into the engine, JoyPro.VJoy
and the GUI: the CLI test, the editor's preview and the forwarder run the same code. `forwarder-config` writes the
functions resolved (source index, kind, index; vJoy device index) - of the game that started (`--game`, the GUI's auto
start) or of every vJoy game and the Xbox game (Start by hand) - and adds their devices to the sources. The forwarder
copies each changed device state into the runtime, calls `Tick` **every pass** (also without a change: pulses, turbo,
macros, long presses and the incremental axis need the clock) and merges the outputs: every vJoy button, axis and hat a
function owns is replaced by what the functions wrote (two functions on one button combine), the Xbox report likewise.
Timing resolution is one pass (at 144 Hz 6 ms): a pulse lasts its length rounded up to passes, a macro press is held for
at least one pass. A device that disappears releases the functions' buttons; Stop releases every key and output.
Nothing is allocated per pass.

**CLI.** `engine vjoy functions types|list|add|set|remove|test` (`--json` by default, `--text`), see
`engine/docs/ENGINE_CLI_WIKI.md`:
```
engine vjoy functions add --game DCS --type bands --name "Throttle detents" --input "in=VPC Throttle {GUID}|JOY_Z" --edges 0.05,0.95 --output band3=auto:enter
engine vjoy functions set --game DCS --id f1 --param hysteresis=0.02 | --spec @function.json
engine vjoy functions test --game DCS --id f1 [--stream "0:in=0.5; 100:in=0.97"] [--rate 240] [--duration 600]
engine vjoy functions remove --game DCS --id f1
```
`test` simulates (default stream per type: an axis sweep, presses, ...) and lists every output change; nothing goes to
vJoy, the pad or the keyboard.

**GUI.** Virtual Joystick page, tab **Functions**: the types with icon and one line each, "Add function" (game, type),
the list (outputs, state, live state), and the editor: name, on/off, keyboard output; a node view in the style of the
force feedback editor (inputs -> function -> outputs, lit while an output fires); the axis band editor (the axis travel
as a bar with the bands and their outputs, draggable edges, the hysteresis zones, the input marker); inputs (device,
input), outputs (target, vJoy device, band mode, "Bind relation"), settings (sliders/choices), macro steps, the curve
view; **Try it** runs the function in the editor (sliders, press buttons, hat directions; a timer only while it is on),
and while Test or Start runs the forwarder's `fn` lines show the real state (LIVE). Save stores it through the engine.

**Performance** (`JoyPro.VJoy bench`, release build, development PC, 4 synthetic sources changing every pass, 50
functions of all types): 144 Hz: 9.7 us per pass on average (p99 29 us), 0.47 % of one core (table only: 0.31 %);
240 Hz: 8.5 us average (p99 31 us), 0.31 % of one core; 0 bytes allocated in the loop. The GUI test
`LoopBenchmarkWithFiftyFunctions` measures the same pass without the timer (about 4 us per pass).

## The forwarder (JoyPro.VJoy.exe)

```
JoyPro.VJoy run --config <file> [--sink vjoy|dry] [--xbox-sink vigem|dry] [--rate Hz] [--mode event|poll] [--parent-pid N] [--protocol] [--monitor] [--duration s] [--vjoy-dll path]
JoyPro.VJoy devices [--json]      (DirectInput game controllers with instance GUID and HID path; read-only)
JoyPro.VJoy refresh-rate          (the fastest display's refresh rate, at least 60)
JoyPro.VJoy bench [--functions 50] [--rate Hz] [--seconds 10] [--sources 4]   (the loop with virtual functions and synthetic sources; no device, vJoy or key)
```

- Reads every mapped device through DirectInput in non-exclusive background mode with JoyPro's data format (the first
  176 bytes of DIJOYSTATE2: X Y Z Rx Ry Rz Slider Slider by GUID, 4 POVs, 128 buttons - the numbering of JoyPro's and
  DCS's binds). `DIPROP_RANGE` is set to vJoy's axis range, so axis values go over 1:1; JoyPro's curves, dead zones and
  inversions stay in the game's settings.
- Wakes on DirectInput's change events (a button goes out at once) and on a high-resolution waitable timer at the rate
  (default: the fastest display's refresh rate, minimum 60 Hz; period = whole milliseconds rounded down, so 144 Hz polls
  every 6 ms). Event wake-ups closer together than half a period wait for the next tick: a device that reports 1000
  times a second costs at most two passes per period. `--mode poll` uses the timer only.
- Writes a vJoy device only when its position changed (`UpdateVJD` with the whole JOYSTICK_POSITION_V2). Neutral at
  start (axes centred, buttons up, hats centred) and at the end.
- No allocation per pass: native buffers, preallocated handles, structs; the stats line reports the loop thread's
  allocated bytes (stays at the startup value). Measured on the development PC (3 real devices incl. a VPforce Rhino,
  144 Hz, release build): ~250 passes/s, 1.5-1.7 % of one CPU core, ~34 MB working set, handles flat; poll mode ~1.4 %.
- A device that disappears releases its buttons and centres its hats (axes keep their last value); it is opened again
  when Windows reports a device change (WM_DEVICECHANGE), else once a minute (opening a missing device costs
  DirectInput a device scan).
- Ends on `stop` (stdin), when stdin closes (`--protocol`), when the parent process exits (`--parent-pid`), on Ctrl+C or
  after `--duration`. The default sink is `dry` (nothing goes to vJoy); only `--sink vjoy` uses the vJoy devices, and
  it refuses a vJoy device another feeder holds. `JOYPRO_VJOY_DRY=1` forces dry.
- Protocol lines: `ready ...`, `source <i> connected|missing|lost <device>`, `stats passes= hz= wakes= changes= writes=
  sources= loop-alloc= cpu-ms= ws-kb= handles=` (1/s), `out <id> <8 axes> <128 buttons hex> <4 hats>` (15/s while the
  live view is on), `fn <id> <first input 0..1000> <state> <outputs on, bit mask>` per virtual function (15/s while the
  live view is on), `error ...`, `exit <reason>`. The ready line says `functions=<n>` (and `keys=on` when a function may
  type).

## GUI: Virtual Joystick page

- **Overview**: what it is and how to use it in plain words; status of vJoy (version, devices), HidHide (state) and
  JoyPro's session; `Set up vJoy for the table` (only when needed), `Restore HidHide` (when a session is active); "What
  this page needs": the vJoy, HidHide and ViGEmBus cards of docs/components.md (status, license, one-click install); per
  game a Native/Virtual switch (the games of the profile and every game in virtual mode); options "Hide the real
  devices" (HidHide on/off) and "Start and stop by itself when a game set to Virtual starts and ends".
- **Mapping**: the table (physical input, virtual input, the relations that use it, state), `Update mapping`.
- **Functions**: the virtual functions (see "Virtual functions"). Overview also has "Set up vJoy for maximum" and, per
  game, what it reads of a joystick with the limit warnings.
- **Live test**: bars for the eight axes, pressed buttons and hats of each vJoy device as the forwarder writes them, the
  forwarder's stats and log.
- `Start`: updates the table, writes the forwarding table, hides the physical devices (one prompt), starts
  `JoyPro.VJoy --sink vjoy`. `Test`: the same without HidHide and with the dry sink (nothing changes). `Stop` and an
  unexpected end of the forwarder restore HidHide. Press Start before you start the game: games often look for devices
  only once at start.
- Auto start: only while it is on and a game is in virtual mode, JoyPro looks for the games' processes every 3 s and
  starts (with the running game's executable added to HidHide's list) and stops by itself.

## CLI

See `engine/docs/ENGINE_CLI_WIKI.md` (`engine vjoy ...`). `--dry-run` everywhere that changes something.
`JOYPRO_VJOY_FAKE=<folder>` replaces vJoy and HidHide by files in that folder: `vjoy.txt` (`installed=<version>`,
`device=<n> axes=x,y,... buttons=<n> povs=<n> discrete=<n> ffb=yes|no`), `hidhide-state.txt` (HidHideCLI's own
format), `hidhide-gaming.json` (`--dev-gaming`), `hidhide-missing` (file: not installed), `hidhide-exit.txt` (exit code
of the next apply, `-1223` = prompt declined, `-1460` = hung), `hidhide-deny.txt` / `hidhide-hang.txt` (parts
`cloak`, `inverse`, `devices`, `apps` that are denied without administrator rights / never answer); the elevated step
runs as a normal child process then. The calls JoyPro would run go to `hidhide-calls.txt` and `vjoyconfig-calls.txt`.
`JOYPRO_HIDHIDE_CLI=<exe>` uses another HidHideCLI (the smoke test uses a script that hangs and denies).
In `vjoy.txt`, `vigem=<version>` fakes an installed and running ViGEmBus (missing, empty or `vigem=no`: not installed).
`JOYPRO_HLLV_SETTINGS=<ini>` replaces Hell Let Loose: Vietnam's `GameUserSettings.ini` (gamepad layout), and
`JOYPRO_OODLE_DLL=none` keeps its catalog refresh from using any Oodle library (another game's is used only with the
user's consent, `refresh-db --oodle-dll`; docs/games/hll-vietnam.md "Oodle").

## Tests

- `engine/tests/smoke/vjoy.ps1` (part of the engine smoke test): table rules and stability, capacity over two vJoy
  devices, vjoy configure (dry run, one call, never again when it fits), DCS export to the vJoy device incl. modifier and
  hat, profile keeps the physical devices, import back to physical, missing vJoy stops the export, HidHide dry run,
  declined prompt, block list with parked entries and vJoy shown, exact restore, stale owner and recover, the user's
  allow list, denied/hanging HidHide reads (unknown, never "off"; nothing changed; session kept), a journal with an unknown inverse mode, the real CLI path with a stand-in that hangs and denies (time limit, ended with its children), the forwarder table, a dry JoyPro.VJoy run (no allocation in the loop), back to native.
- `gui/JoyPro.Gui.Tests/VirtualJoystickTests.cs`: the HidHide state machine (enable/disable, crash of the owner, crash
  while applying, declined prompt, allow list, inverse switch, a second game, journal round trip, unreadable parts, partial dry run, unknown previous inverse mode) and the page against a
  fake engine that runs the same state machine (status, toggle, Test, Start/Stop, forwarder ending by itself, recovery at
  start, auto start, shutdown). `VirtualJoystickForwardingTests.cs`: the forwarding table (axes, buttons, hats, discrete
  hats, release, no allocation, JOYSTICK_POSITION_V2 layout).
- Virtual functions: `engine/tests/smoke/vjoy.ps1` section 7 (add with free outputs, reservation against the planner,
  test with the default and a given stream (pulse length), keys only with keyboard output, forwarder-config, a dry
  JoyPro.VJoy run with a function (`fn` lines, no allocation), per-game limit warnings, `limits`, `configure-vjoy --max`
  as a dry run only, remove). `gui/JoyPro.Gui.Tests/VirtualFunctionRuntimeTests.cs`: every function type with simulated
  input streams (band edges with hysteresis, enter/leave pulses, cycle wrap/back/reset/hold, toggle and switch,
  short/long/double timing, shift layers with frozen axes and latch, merge/split math, the curve, axes to hat, the
  incremental axis rate and spring, encoder pulses, turbo, macro timeline and keys, hat helpers), the merge into the
  vJoy positions (owned outputs, a shift layer over a slot) and the Xbox report, the forwarder table's JSON, no
  allocation per `Tick`, a loop benchmark. `VirtualFunctionsGuiTests.cs`: the Functions tab against a fake engine (list,
  add, edit, preview, save spec, live lines, bind, remove, set up for maximum, limits) and offscreen renders of the
  editors (`%TEMP%\JoyPro2.Gui.Tests\vjoy-functions`; the whole tab with `JOYPRO_VJOY_FUNCTIONS_PAGE_PREVIEW=1`).
- Nothing in the tests touches the real HidHide, vJoyConfig or a vJoy device: the GUI tests never start JoyPro.VJoy and
  point every engine at a fake (`JOYPRO_VJOY_FAKE`).

## Limits and notes

- Force feedback is not forwarded (vJoy's FFB would need a feedback loop back to the physical stick).
- Virtual functions read physical inputs only (not another function's output); started by hand, the functions of every
  virtual game run at once (a shift layer of one game also acts while another runs); auto start runs only the started
  game's. The node view is read-only (the rows next to it edit).
- Not verified on hardware yet: the functions in a running game, keys from SendInput in a game, the vJoy maximum setup
  (needs a Windows restart; never run by the tests).
- Games that identify devices only by name or VID/PID cannot tell several vJoy devices apart.
- HidHide blocks by executable path: a game started through another executable (a launcher that stays the game) must
  be listed with `engine vjoy exe <game> --add`. Microsoft Store games live in protected folders JoyPro cannot list.
- Anti-cheat systems normally accept vJoy and HidHide (signed drivers), but a game may refuse virtual devices.
- 4-way (discrete) vJoy hats are supported by the table, but JoyPro configures continuous hats; the discrete layout is
  not verified on a device yet.
