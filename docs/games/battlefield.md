# Battlefield (Frostbite): one-device games

## Summary
- **Battlefield 6** and **Battlefield 2042** read **one** DirectInput joystick, and their binds name no device. A HOTAS
  of several devices only works through JoyPro's virtual joystick: `engine vjoy mode Battlefield6 virtual`
  (docs/virtual-joystick.md).
  - The forwarder puts the whole HOTAS on one vJoy device.
  - HidHide hides the real devices from `bf6.exe` only.
- The export writes the binds into the game's text profile.
- **Joystick binds are written only after one live session with the user.** The axis/button numbering of these two
  games is not confirmed yet; see [Live survey](#live-survey-finishes-the-integration). Until then JoyPro writes
  keyboard keys (with `--include-keyboard`) and reads joystick binds with a warning.
- **Anti-cheat:** EA Javelin (kernel level). JoyPro only writes the profile while the game is closed and uses the
  signed vJoy/HidHide drivers. It never touches the game process. Some players report that the game still sees
  through HidHide; unplugging the real devices is the sure way (see below).
- Battlefield V, 1, 4, 3, Hardline: same family, documented below. Not integrated.

CLI: `engine bf6 refresh-db|scan|import|export`, `engine bf2042 ...`. Both are experimental.

## Files
| Game | Profile (text, LF, sorted `GstKeyBinding` lines) | Process | Steam |
|---|---|---|---|
| Battlefield 6 | `Documents\Battlefield 6\settings\steam\PROFSAVEbf6mp_profile` (multiplayer) and `PROFSAVE_profile` (campaign); EA app: `settings\` without `steam` | `bf6.exe` | 2807960 |
| Battlefield 2042 | `Documents\Battlefield 2042\settings\PROFSAVE_profile` | `BF2042.exe` | 1517290 |

- The `PROFSAVE` files without `_profile` are binary `FBCHUNKS` and hold no binds. JoyPro never touches them. [OBS]
- An export writes every `*_profile` of the folder that holds binds, so campaign and multiplayer match.
- The folder is synced by Steam Cloud (`steam_autocloud.vdf`). After an export, start the game once and check the
  binds there.

## Bind lines [OBS]
```
GstKeyBinding.version 2000007
GstKeyBinding.<category>.<Concept>.<slot>.axis <int>
GstKeyBinding.<category>.<Concept>.<slot>.button <int>
GstKeyBinding.<category>.<Concept>.<slot>.negate 0|1
GstKeyBinding.<category>.<Concept>.<slot>.type <int>
```
- **Categories:** `infantry`, `vehicle`, `transport`, `gunner`, `helicopter`, `jet`, `default`. JoyPro's catalog
  takes `jet` and `helicopter`.
- **`type` (BF6 and 2042):** 0 mouse, 1 pad, 2 keyboard (`button` = DIK scan code, 255 = none, `axis` 0), 3 joystick.
- **"Nothing bound":** each enum's count. Axis 56 in BF6 (IDA_Axis0..7) and 27 in 2042 (IDA_Axis0..3); button 60.
- **`negate`:** 1 = this slot drives the action in the negative direction. Examples: S = DIK 31 with negate 1 on
  MoveFB, Left arrow = 203 with negate 1 on Roll.
- **Slots:**
  - The game's default profile already holds two empty joystick slots per axis action, one with negate 0 and one with
    negate 1. Examples: `jet.ConceptPitch.4/.5`, `jet.ConceptMoveFB.3/.4`.
  - Button actions have no joystick slot until something is bound. JoyPro then adds slot n+1 with the four lines in
    their sorted place.

### Enums (strings in `bf6.exe`, in enum order) [SRC]
| Codes | Names |
|---|---|
| IDA 0-8 | Axis0X, Axis0Y, Axis0Z, Axis0XPos, Axis0YPos, Axis0ZPos, Axis0XNeg, Axis0YNeg, Axis0ZNeg |
| IDA 9-14 | Axis1X, Axis1Y, Axis1XPos, Axis1YPos, Axis1XNeg, Axis1YNeg |
| IDA 15-26 | Axis2, Axis3 (same pattern) |
| IDA 27-55 | BF6 only: Axis4-6 (with Z), Axis7X/Y. 56 = IDA_NumAxes |
| IDB 0-17 | Lup, Ldown, Lleft, Lright, Rup, Rdown, Rleft, Rright, Rtopleft, Rtopright, Lthumb, Rthumb, start, alt, Ltrigger, Rtrigger, Ltrigger2, Rtrigger2 |
| IDB 18-35 | XButton1..18 |
| IDB 36-43 | PosZAxis, NegZAxis, XRotationPos/Neg, YRotationPos/Neg, ZRotationPos/Neg |
| IDB 44-59 | Pov1North, South, West, East .. Pov4East. 60 = none |

These counts match the "nothing bound" markers (56 and 60). Mouse: axis 0/1 = X/Y, wheel 16. Pad: left stick 0/1,
right stick 9/10. [OBS]

### Joystick numbering: provisional
A community editor for Battlefield 4 decoded BF4's joystick binds (WEB: Realmware BF4 Settings Editor). JoyPro moves
that scheme onto the newer enum:
- X / Y → Axis1X / Axis1Y (halves 11/13 and 12/14).
- Z, Rx, Ry, Rz: only analog halves, as button codes 36-43.
- Sliders: trigger codes 14 (+) / 15 (−).
- DirectInput button n → code n−1.
- Hat n → 44 + 4(n−1) + North/South/West/East.

The game's own default `transport.ConceptMoveFB.2 axis 1 type 3` (Axis0Y) does not fit this scheme. That is why
JoyPro waits for the live survey.

How JoyPro writes an axis: as **two halves**, the way the game's default profile lays out its slots.
- The + half goes into the negate-0 joystick slot, the − half into the negate-1 slot.
- *Invert* swaps the two halves.
- Read does the reverse. Two halves of one axis become one bind; halves in swapped slots read as inverted.

## Limits and quirks
- **One joystick.** Several devices in the profile → the export writes the device with the most binds and lists the
  others ("use the virtual joystick").
- **Rx/Ry:** players report that the game takes a joystick with Rx or Ry for an Xbox pad and hides its joystick page.
  VKB users fixed this by switching off the mini-stick's Rx/Ry. [WEB]
  - Battlefield therefore declares `AvoidVJoyAxes = rx, ry` (`IGameDeviceLimit`).
  - In virtual mode the planner gives no input vJoy Rx/Ry.
  - `engine vjoy configure-vjoy` configures the vJoy device without them, e.g. `vJoyConfig 1 -f -a x y z rz sl0 sl1`.
  - A slot that already sits on Rx/Ry is flagged (`engine vjoy map --reset`).
- **Buttons:** players see about 18 buttons. Above that: unconfirmed.
- **Hats:** four directions only; diagonals are skipped.
- **No modifiers.**
- **Keyboard keys:** written only for button actions (axis actions have keyboard pairs the game manages).

## Automated survey (`BattlefieldJoystickSurvey.ps1 -Auto`, BattlefieldSurveyAuto.ps1)
**Needs a reboot window:** on the dev PC vJoyConfig answers with "You must restart your computer to apply these
changes" (2026-10-11): the running vJoy driver keeps its layout until a reboot, and restoring the layout is another
vJoyConfig call with the same question. The script therefore refuses to touch vJoy unless it is started with
`-Auto -AllowVJoyReconfigure`, which is only for a run planned with the user around a reboot (the first attempt was
stopped at that dialog; nothing else had changed, and the user restored vJoy device 1 through UAC).

Unattended except for one Windows administrator prompt at the start:
- read-only check that Steam Input is off for the game (`localconfig.vdf` UseSteamControllerConfig of app 2807960,
  `config.vdf` generic gamepad support); on: stop, nothing changed;
- the profile files are copied; one elevated helper (`BattlefieldAdminSession.ps1`) configures vJoy device 1 without
  Rx/Ry (its own button and hat counts) and starts JoyPro's HidHide session (`engine vjoy apply-hidhide` with a survey
  profile in which Battlefield 6 is in virtual mode and every physical game controller has a relation, so all of them are
  hidden from `bf6.exe` only); it restores both (`restore-hidhide`, the recorded vJoy axes/buttons/hats) when the survey
  ends, writes done.flag, or after 90 minutes;
- after an idle wait the game starts; the script passes START, the intro (SKIP) and the welcome popup (DISMISS, never
  PLAY BREAKTHROUGH), opens the gear > CONTROLLER > EDIT SETTINGS > JOYSTICK: Joystick EDIT > Aircraft EDIT
  ("AIRCRAFT PILOT CONTROL MAPPING");
- capture test: a row's box is clicked while vJoy rests; if the game binds anything, another device is still visible to
  it (HidHide seen through): stop;
- one vJoy input per aircraft row (Pitch Down, Roll Left, Throttle Up, Yaw Right, Freelook Up/Left, Fire, Airplane Zoom,
  Switch Weapon, Primary Weapon, Flick Look Forward/Backward/Left/Right), each followed by Esc (the game writes the
  profile when the page is left) and a diff against the copy before that step;
- `joystick-map.tsv` -> `EngineOnly_Runtime\TOOLS\Conversions\Battlefield6JoystickMap.tsv`, engine build, a JoyPro
  export of random binds on vJoy and a second start that reads them on the aircraft page; quit (QUIT CLIENT > QUIT),
  profiles restored (with the current time, so Steam Cloud takes them as the newest), HidHide and vJoy compared with
  the state before.

Menu facts (Season 4, 1.4.3, 2026-10-11): the main menu advertises the battle pass with a BUY NOW tile (the e2e guard
ignores exactly those lines, the script never goes near STORE); a mapping row shows UNBOUND or the bind ("AXIS 1 X -");
**a click on a row's box binds the first input the game sees**: with the user's devices connected the VPforce Rhino's
noise was bound at once ("AXIS 1 X -" on Throttle Up), so the survey needs only vJoy visible. Binding one row of an
axis pair (Throttle Up) filled both rows ("AXIS 1 X -" / "AXIS 1 X +") and stored **one whole axis** in one slot:
`jet.ConceptMoveFB.3.axis 9` (IDA Axis1X) with `negate 1`.

## Live survey (finishes the integration)
`tools\e2e\games\BattlefieldJoystickSurvey.ps1` (needs the game, vJoy and the user, about 5 minutes). It sends no
key or mouse input to the game. It only reads the left mouse button state and moves vJoy.
1. **vJoy:** device 1 with X, Y, Z, Rz, Slider, Slider and **no Rx/Ry**, e.g. as administrator:
   `"C:\Program Files\vJoy\x64\vJoyConfig.exe" 1 -f -a x y z rz sl0 sl1 -b 32 -p 4`. The script checks this and
   prints the command.
   - The e2e kit uses device 1 with all eight axes. Configure it back afterwards if needed.
   - JoyPro's virtual joystick must be stopped; the script drives vJoy itself.
2. **Real devices:** unplug the sticks, throttle and pedals for the session, so the game's one joystick is vJoy.
   Hiding them with HidHide also works, but some players report BF6 sees through it.
3. Run `powershell -ExecutionPolicy Bypass -File tools\e2e\games\BattlefieldJoystickSurvey.ps1`. Use `-Game bf2042`
   for 2042; `-Plan` only prints the steps.
   - The script saves the profile files and prints the 22 steps.
   - Start the game. Open Settings > Controls > the joystick page (BF6: under Controller) and press Enter in the console.
4. **Per step** the script says the row, by voice and in the console. Examples: "Jet, pitch", "Helicopter, roll",
   "Land vehicle, steering", "Jet, fire", "Jet, change camera".
   - Click the joystick field of that row. One second after the click, vJoy moves exactly one input:
     - an axis to one end;
     - a whole sweep (2 steps);
     - button 1, 2, 18 or 19;
     - a hat-1 direction.
   - "Either row" means either of the two direction rows (for example pitch up or pitch down).
   - Button 19 may be ignored by the game: press Escape and go on.
5. Apply or save in the game if it asks, and quit the game. The script waits for the exit, then:
   - compares the profile with the saved copy;
   - writes `joystick-map.tsv` into the run folder and copies it to
     `EngineOnly_Runtime\TOOLS\Conversions\Battlefield6JoystickMap.tsv` (`Battlefield2042JoystickMap.tsv`), which
     JoyPro treats as confirmed;
   - puts the original profile files back (`-KeepBinds` keeps the survey binds).
6. Send the run folder (`C:\Temp\jp2-e2e\runs\<stamp>-bf-survey`) to the developer.
   - The table's notes show which slots the game filled per step. If the game writes a half axis as a whole axis, or
     fills both halves at once, the writer is adjusted to that.
   - Afterwards one export + in-game check confirms the integration.

`-Analyze -Before <copy> -After <file>` repeats the evaluation on two copies of the profile without the game.
The engine reads the table from `<engine>\TOOLS\Conversions\<GameId>JoystickMap.tsv`. `JOYPRO_BATTLEFIELD_MAP=<tsv>`
points elsewhere (tests). The table is JoyPro's own measured numbering, not game data. It ships like the X4 and Elite
key tables: release packaging (`tools\ReleaseContent.psm1`) strips the game DB folders but keeps `TOOLS\Conversions`.
Lines look like `JOY_Z+<TAB>button<TAB>36`. Without the file the provisional table applies: read only, no joystick
writes.

## Other titles (not integrated)
| Title | Joystick | File | Notes |
|---|---|---|---|
| Battlefield V | DirectInput, 1 device; binds have `.device` (always 0) | `Documents\Battlefield V\settings\PROFSAVE_profile_synced` (+ `_backup`) | Types 0 kb, 1 mouse, 2 pad, 3 joystick; none = axis 24 / button 64. Installed here (EA app). EA anti-cheat since 2024. The `_synced` file is synced with EA's servers, so a local edit may be undone [INF]. Same writer with another enum table, after its own survey. |
| Battlefield 1 | DirectInput, 1 device | `Documents\Battlefield 1\settings\PROFSAVE_profile` | Same scheme as BFV (community sources, WEB). EA anti-cheat since 2024. |
| Battlefield 4 / Hardline / 3 | One device; joystick and pad share type 2 | `Documents\Battlefield 4\settings\PROFSAVE_profile` | BF4: X/Y = axis 6/7, Z/Rx/Ry/Rz as button halves 36-43, buttons 0-35, hats 44-59 (Realmware editor). |
| Battlefield 2 / 2142 | DirectInput, device index | `Documents\Battlefield 2\Profiles\000N\Controls.con` | `ControlMap.addAxisToAxisMapping c_PIPitch IDFGameController_0 IDAxis_1 1 0`. Legacy. |

Research: real profiles on the dev PC (read-only), text strings in `bf6.exe`, community sources and the BF4 editor (WEB; see
the survey in docs/games/one-device-games.md).
