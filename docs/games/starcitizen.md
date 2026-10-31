# Star Citizen

CLI: `engine starcitizen scan | import | export | list-devices` (alias `sc`), `engine db refresh-starcitizen`. Research
notes (SC 4.10, 2026-10-05) are private. Tags as in the [README](README.md).

## Files
- `LIVE\user\client\0\Profiles\default\actionmaps.xml`: rebinds, joystick slots (`<options type="joystick"
  instance="1..8" Product="...">`), the slots' option groups and the device options. The game rewrites it on exit, so
  JoyPro refuses to export while `StarCitizen` runs. [OBS]
- `Product` is the DirectInput product name and product GUID, two spaces apart: `vJoy Device  {BEAD1234-0000-0000-0000-504944564944}`
  (PID, VID, "PIDVID"). A slot or `deviceoptions` block written with another name (the instance GUID JoyPro uses for
  devices) is not recognized: at start the game rewrites the slot with its product and drops the slot's option groups;
  the device options are ignored. JoyPro writes the connected device's product name (in-game test 2026-10-10).
- Menu display (Options): saturation shows 0.01 higher than the file value (0.87 shows 0.88, 0.95 shows 0.96); the game
  keeps the file values. ESP assist strength: the slider ends at 1.00 (1.05 and 1.21 show as 1.00, the file keeps them),
  so JoyPro offers 0..1. [OBS 2026-10-10]
- Joystick sensitivity curves (`nonlinearity_curve`) are drawn as a smooth spline through the points, not straight
  segments. [OBS 2026-10-10, curve editor]
- `...\Profiles\default\attributes.xml`: game-wide settings, `<Attr name="..." value="..."/>`, only changed values,
  sorted by name, CRLF. Defaults with their cvars: `Data.p4k` `Data\Libs\Config\Profiles\default\attributes.xml`
  (Version 35). [SRC, OBS]
- `actionmaps.jp` (JoyPro): the written file plus relation names as comments, read back on import.
- `actionmaps.xml.joypro.json` (JoyPro): the axis settings JoyPro wrote (merge keeps values changed in the game).
- `actionmaps.xml.jpbak`, `attributes.xml.jpbak`: copy before the first write of an export run, plus the versioned
  backup store (`engine backups`).

The export edits `actionmaps.xml` in place: only ship rebinds and the axis settings of the axis rebinds JoyPro writes
change; other action maps, mouse/gamepad rebinds, activation modes, modifiers, other option groups and device options
stay byte-identical.

## Catalog (`engine db refresh-starcitizen`)
Source: the game's `Data\Libs\Config\defaultProfile.xml` (CryXmlB in `Data.p4k`, read through the zip index) or an
extracted copy (`--source`). `DB\StarCitizen\AllBinds.xml` (and the identical `actionmaps.xml`):
- `<action name="v_pitch"><!--@ui_CIPitch//true--><optionGroup name="flight_move_pitch"/></action>`: the line older
  JoyPro builds read is unchanged; the option group (the action's `optionGroup` attribute in defaultProfile.xml)
  follows the comment as a child element.
- `<optiontree type="joystick" ...>` (copied from defaultProfile.xml, before the action maps): parents, defaults
  (`flight_view exponent="2.5"`, `mgv_move`/`mining_throttle invert="1"`) and which groups show a curve
  (groups without `UIShowCurve` only offer Invert: zoom, turret limiter). Without a tree (old catalog) JoyPro uses
  these few defaults built in.
- SC 4.10: 76 actions have an option group; the joystick tree has 55 groups.
- `<defaultJoystick input="y"/>` (child element, from defaultProfile.xml `joystick="y"`): the game's default joystick
  input of the action (" ", "" and the 0/1 flags are none). SC 4.x: 67 actions, e.g. `v_pitch` y, `v_yaw` x, `v_roll`
  rotz, `v_strafe_up` button9, `v_afterburner` button8, `v_weapon_toggle_launch_missile` button1. Debug aid:
  `JOYPRO_SC_DUMP_DIR=<folder>` makes the refresh save every CryXmlB (binary XML) source it converted as text.

### Default joystick binds (import, clean, nuke)
The defaults act on js1 until the action has a js rebind. An import takes every active default of a ship action (no js
rebind in the file; an empty rebind switches the default off) as a bind of the js1 device, so the profile holds what
the game does (since 2.4.3). An export writes no rebind for a bind that equals the action's default on js1 (only real
differences), so import + clean + nuke leaves actionmaps.xml byte-identical. Clean (with a device written to js1) and
nuke (js1 not used) switch off the defaults the profile does not have (the user removed them in JoyPro): an empty
rebind `<rebind input="js1_ "/>`, as the game's menu writes a cleared bind, and only when the device on js1 is
connected. An empty rebind already in the file stays where it is.

Export modes (since 2.5.0, [export](../export.md#export-modes)); ship actions only, the on-foot, vehicle and menu maps
stay byte-identical:
- Add changes no game bind: an action whose joystick slot holds a rebind or an active default keeps it, and a bind on
  an input another ship action uses (a rebind or an active js1 default) is not written and listed as a skipped multi
  bind.
- Merge and merge-overwrite put the profile's input into the action's joystick slot (Star Citizen has one per action
  and input type) and release the game's bind on an input the profile gives another ship action: its rebind goes, or
  becomes `js1_ ` when the action has a default (so the default does not come back); an active default on that input
  gets `js1_ `. Merge-overwrite also takes a bound action's keyboard rebind away when the export includes the keyboard.
- Clean and nuke release only slots of connected devices; a slot whose device is not connected keeps its rebinds.
An export of a profile without Star Citizen binds leaves actionmaps.xml as it is. [OBS 2026-10-10: a clean + nuke
export with vJoy on js4 wrote 29 empty js1 rebinds; the game kept all 29 when it rewrote actionmaps.xml on exit]

Joystick slots (`--prefer-local-order`, the default): a connected device the file's `<options type="joystick">` names
keeps that slot (the game's own record, matched by product as the import does); other devices fill the free slots in
DirectInput order. Before 2.4.3 the DirectInput order came first, so an export could move every bind of a device the
game had on js1 to another slot.

## Axis settings (axis model `starcitizen`, `engine\AxisModels\StarCitizen.cs`)
Two stages, as the files and menus show them:

| Setting | Where | JoyPro |
|---|---|---|
| Dead zone, saturation | per device (Product) and physical axis: `<deviceoptions name="<Product>"><option input="x" deadzone="0.05" saturation="0.95"/></deviceoptions>` | `deadzone`, `saturationX` (device axis) |
| Invert | per joystick slot and option group: `<flight_move_pitch invert="1"/>` | `invert` |
| Exponent | same: `exponent="1.5"` | `exponent` (1..3, unverified range) |
| Curve | same: `<flight_move_roll><nonlinearity_curve><point in="0.1" out="0.05"/>...</nonlinearity_curve></flight_move_roll>` | `userCurve`: the points at 10..90 % (ends fixed at 0 and 1) |
| Sensitivity | same: `sensitivity="0.8"` (INF) | `sensitivityScale` (0.01..2, unverified) |

- Exponent and curve exclude each other (the game's menu deletes one when the other is set): writing a curve removes
  the group's `exponent`, writing an exponent removes its curve.
- Preview (UNVERIFIED, documented behaviour, not measured): u = |x|; u <= dead zone -> 0; u >= saturation -> 1; else
  linear between them; then the curve (straight lines through (0,0), the 9 points, (1,1)) or u^exponent; times the
  sensitivity (capped at 1); sign of x, flipped by invert. There is no table the game writes back (unlike IL-2), so the
  in-game check compares the menu values (`tools\e2e\games\StarCitizenSettings.ps1`).
- No one-sided axes: absolute/relative throttle modes are separate actions. A base marked Slider fits to neutral.
- Fit from the base (relations without Star Citizen tuning): dead zone and saturation X go to the device axis; the
  curvature (and saturation Y) becomes the best exponent 1..3 (least squares, 20 points), or the 11-point curve when no
  exponent comes within 0.01 RMS (negative curvature); a user curve passes through.

### Import
Each joystick axis rebind gets the values the game uses: its action's option group on its slot (the user's leaf, else
the nearest parent the user set, else the game's default at that level), and the device options of its axis. When they
differ from what the relation already gives Star Citizen they become the relation's `game:StarCitizen` tuning; the
base keeps JoyPro's defaults (the import used to reset them). A relation bound to several actions (merged by device
and input) keeps the first action's values and warns about different ones.

### Export
For every axis rebind JoyPro writes (js slots only) and whose relation has axis settings (a `game:StarCitizen` tuning
or a non-neutral base; a neutral relation leaves the game's values, e.g. free look's default exponent 2.5):
- the option group of the action on the slot: an attribute the group already has gets the wanted value; a missing one
  is added only when the inherited value differs (e.g. `exponent="1"` under `flight_view`'s 2.5); invert-only groups
  get only invert;
- the device options of the axis (`deviceoptions name=` is the slot's Product exactly as in the file); a new block goes
  before the first `<options>` [INF: placement in a 4.x file not observed].
- Several relations on one option group of a slot, or on one physical axis, with different values: warning, the first
  relation by name wins.
- Modes: merge and add keep a group or axis whose values were changed in the game (not what JoyPro wrote last time,
  `actionmaps.xml.joypro.json`), with a warning; merge-overwrite and clean set the profile's values.
- A slot that changes device: its option groups move with the device to the device's new slot, and the slot gets the
  groups its new device had elsewhere in the file (they used to be dropped). A device that gets no slot loses the
  groups of its old slot (warning).

## Game-wide settings (`engine ffb set --device * --game StarCitizen --param <Attr name>=<value>`)
Settings model `StarCitizen` (`engine\FfbModels\StarCitizen.cs`), written in place into `attributes.xml` on export
(an existing line gets the value, a new one goes to its sorted place; every other byte stays; JoyPro does not create
the file). Keys are the game's Attr names:

| Key | Menu | Default |
|---|---|---|
| `Vibration` | Vibration (`i_forcefeedback`, the only force feedback setting) | 1 |
| `FlightThrustInvertToggle` | Flight - HOTAS thrust invert toggle | 1 |
| `FlightRelativeThrottleTrapTime` / `...Threshold` | Pilot - relative throttle - axis crossing trap time (s) / threshold | 0.3 / 0.25 |
| `FlightCoreDisabledSensitivityRotation` / `...Translation` | IFCS core disabled sensitivity modifiers | 2 / 2 |
| `IFCS_Setting_EspEnabled`, `PilotEspStrength`, `PilotEspDampeningAmount`, `PilotEspDampeningIncludeRoll` | ESP default on, strength, dampening, dampening includes roll | 1, 1, 0.15, 1 |
| `TurretsEspDefaultOn`, `TurretsEspStrength`, `TurretsEspDampening` | Turret E.S.P. | 1, 1, 1 |
| `IFCS_Setting_StickyThrottleEnabled`, `..._SpeedLimiterOn`, `..._CoupledEnabled`, `..._GSafeEnabled`, `..._GSafeDisabledByBoost`, `..._AutoPrecisionModeEnabled`, `..._SpacebrakeEnablesBoost` | flight defaults | 0, 1, 1, 1, 1, 1, 1 |
| `InvertYController` | Invert flight controller (gamepad, view pitch) | 0 |

Ranges are not in the game's readable files: the model's ranges are guesses
(unverified). Mouse, VJoy, head tracking and on-foot settings are out of scope.

## Verification
- Smoke: `engine\tests\smoke\starcitizen.ps1` with fixtures in `engine\tests\smoke\fixtures\starcitizen`: DB refresh
  keeps option groups and the joystick tree; import of option groups/curves/device options into tuning; import +
  export keeps the file byte-identical; export of invert/exponent/curve/sensitivity/dead zone/saturation incl.
  exponent/curve exclusivity and inherited defaults; conflicts (group, physical axis); merge keeps an in-game change;
  slot change moves groups; attributes.xml in place.
- In game: `tools\e2e\games\StarCitizenSettings.ps1` (unattended; RSI launcher LAUNCH GAME, main menu > Options; OCR;
  snapshot/restore; needs the launcher's login). 54/54 on 2026-10-10 incl. the cleared js1 defaults.

## Open (needs the in-game check)
1. Does the game keep `deviceoptions` where JoyPro puts it (before `<options>`) and accept `deadzone`/`saturation`?
2. Curve interpolation (straight lines vs spline), exponent range, whether joysticks use `sensitivity`.
3. Do user-file parent groups (e.g. `<flight_move exponent=.../>`) apply to their children as JoyPro assumes?
4. Does `Vibration` drive DirectInput force feedback sticks?
