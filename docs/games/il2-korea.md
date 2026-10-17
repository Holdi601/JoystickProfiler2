# IL-2 Series: Korea

Steam app 247970, folder `steamapps\common\IL2Series`, game exe `bin\game\IL2Series.exe` (process `IL2Series`).
Standalone (il2-series.com, `Korea_IL2Series_Installer.exe`, Inno Setup 6.4.3, read from its setup header 2026-10-17 **[OBS]**): AppId `{222bc1b4-be6e-4451-9291-ef5aeb7c64a2}` (uninstall key `..._is1`, shown as "Korea. IL2 Series"), default folder `{commonpf64}\IL2Series` (`C:\Program Files\IL2Series`) with `Launcher.exe`, `cache`, `temp`; the launcher downloads the game into `<folder>\game` (`game\bin\game\IL2Series.exe`, `game\data\input`, `game\data\startup.cfg`). JoyPro takes `<folder>\game` as the installation (docs/installs.md); `--path` of scan/import/export/refresh-db may also be the folder, the launcher, the exe or `data\input`. No own registry keys besides the uninstall entry. Program Files may be write-protected: export then says so instead of failing half-way. Same engine family as IL-2 Great Battles, but a new key mapping format. All facts below are **[OBS]** from the installed game on the dev PC (build 1.004b, 2026-10-04) unless tagged.

## Files (`<install>\data\input`)
- `general.actions`: the key mapping profile "General" (Settings > Key mapping > Current profile). Other profiles from the Profile Manager are further `*.actions` files **[INF]**. JSON, UTF-8 without BOM, LF, 2-space indent, `"key": value`, empty list `[]` **[INF]**:
  ```json
  {
    "devices": [ { "deviceId": 2, "model": "vJoy Device", "ident": "1234_bead", "name": "" } ],
    "modifiers": [ "key_lcontrol", "key_lmenu", "key_lshift", "key_lwin", "key_rcontrol", "key_rmenu", "key_rshift", "key_rwin" ],
    "actions": { "gear": [ "key_g", "dev2_b16" ], "rpc_pitch": [ "key_down/key_up", "key_lmenu+mouse_axis_y", "-dev2_axis_s" ] }
  }
  ```
  `devices` lists the device numbers the profile uses (model and `ident` = `vid_pid` in lower-case hex). Every action holds a list of inputs; the file lists the actions that have inputs.
- `known.devices.json`: every DirectInput device the game has seen, keyed by the instance GUID in IL-2 byte order (last 8 bytes reversed: `{89D63500-B53F-11F1-8002-444553540000}` -> `89d63500-b53f-11f1-0000545345440280`). `deviceId` = the device number ("joy N" in the game), `-1` = none. Other fields: `model`, `ident`, `preset` (built-in device preset, e.g. `mfg crosswind rudder v23`), `isCustomPreset`, `autoLoadPreset`, `isNew`, `isGamepad`, `created`/`updated` (`yyyy.MM.dd HH:mm:ss`), `lastAttachedId` (enumeration order).
- `current.map`: compiled by the game from the two files (`action("gear","State","joy3_b16");`, `joy` index = enumeration order, not the device number). JoyPro never writes it; the game rebuilds it on start and on Accept.
- `joys.ffp`, `joysffmap.cfg`: force feedback per enumeration index.
- The game writes the files when the key mapping is accepted (main ACCEPT), not on exit.

## Inputs
- Keyboard `key_<dinput name>` (same names as IL-2 GB, `TOOLS\Conversions\IL2DX.keyboardconversion`), combinations with `+` (modifiers first), button pairs for axes `a/b`, mouse `mouse_b0`, `mouse_axis_x`.
- Joystick: `dev<N>_b<button, 0-based>`, `dev<N>_axis_<x|y|z|w|s|t|p|q>` (X, Y, Z, RX, RY, RZ, slider 1, slider 2 like IL-2 GB), `-` before the token = inverted axis, hats `dev<N>_pov<hat, 0-based>_<angle 0..315>`.
- The in-game list shows buttons as `b16`, axes as `Axis S` (with `-` when inverted), and the device by name. A device without a number makes the capture dialog ask "ASSIGN DEVICE NUMBER" first.
- An action row with a joystick axis has a curve icon that opens the response editor for that action.

## Axis response (in-game check and calibration 2026-10-05)
- Pitch: `general.pitch` = the pitch curve profile "General (Common pitch curve)", which the editor assigns to aircraft ("Aircraft using this curve": all six by default). Other axes: `shared.responses`. Both files are written on the editor's ACCEPT and use the same JSON: `{"curves":{"<action id>":{"deadZone0":3,"deadZone1":0,"sensitivity":22,"offset":39,"harmonization":1,"normalized":1}}}` (`offset` only for pitch). The game reads every action from either file (a `rpc_roll` entry in `general.pitch` was compiled too).
- Editor fields: Exponent (sensitivity), Dead zone: Center (deadZone0), Dead zone: Edges (deadZone1), Center offset (offset, pitch only), Built-in Response Curve and Control Surface Loading (harmonization/normalized; they do not change the response table). The file stores whole numbers; the editor shows other percentages (Center 11 % was stored as 3, offset +53 % as 39), so JoyPro works in file units.
- The game compiles the response into a 101-point table (input -1..1 in 0.02 steps) per action in `current.map` `[axiscurves]` on start and on ACCEPT. `tools\e2e\research\KoreaCurves.ps1` writes settings, starts the game and reads the tables back (17 + 11 configurations).
- Response model fitted against those tables (error < 1e-6 unless noted). With s = sensitivity/100, d = deadZone0/100, e = deadZone1/100, o = offset/100, per stick side (sign of x):
  - |x| <= d: output o; |x| >= 1 - e: output +-1.
  - In between, a quadratic Bezier from P0 = (d, o) to P2 = (1 - e, +-1) with the control point on the chord's perpendicular through its midpoint, moved towards the x axis (flatter around the centre) by D = s/2 * w (w = 1 - d - e) when the side's height |+-1 - o| >= 1 (exact for every tested d, e and the steep side of an offset).
  - On the flat side of an offset (height h < 1) D = s/2 * f(h) with f measured: f(0.4) = 0.300, f(0.61) = 0.545, f(0.8) = 0.767, f(0.9) = 0.883, f(1) = 1 (no closed form found; JoyPro interpolates).
  - s = 0 gives the straight chord: dead zones rescale linearly, the offset moves the centre output to o.

## Every setting the game offers for axes and force feedback (UI survey 2026-10-05)
Found by opening every page, dialog and drop-down in Settings (vJoy axes on pitch and throttle, values changed and
accepted, the files compared; the user's files restored afterwards).
- Key mapping, curve icon of a joystick axis (only joystick axes have one):
  - Pitch ("PITCH CURVE"): pitch profile drop-down "General (Common pitch curve)" with "Aircraft using this curve"
    (F-80C-10, F-86A-5, F-51D, MiG-15bis, Yak-9P, IL-10); Built-in Response Curve = `harmonization` 0 Exponent Only,
    1 Built-in Static Curve, 2 Built-in Adaptive Curve; Control Surface Loading = `normalized` 0 Proportional Stick
    Travel, 1 Full Stick Travel; Exponent, Dead zone: Center, Dead zone: Edges, Center offset. No option of either
    drop-down adds or removes a slider; they change the low/high speed preview only (Proportional caps the high
    speed curve). Shown percentages: dead zones = file value x 4 (6 -> 24 %, 3 -> 12 %), exponent = file value,
    offset 12 -> +16 %.
  - Throttle ("THROTTLE CONTROL"): Exponent (shown with a sign, +25 %), Dead zone: Low, Dead zone: High; no drop-downs.
    Same file fields (`deadZone0` = Low, `deadZone1` = High), but the response runs over the whole travel: the
    compiled table is 2 f(u) - 1 with u = (x + 1) / 2 and f the centred curve with offset 0 (error 6e-7).
- Key mapping, command list of an action: per input an invert toggle (`-` before the token) and, for joystick axes,
  a ◆ button: Relative Control (Disabled/Enabled, "you control the axis movement speed rather than the axis
  position") with Dead Zone (default 10 %) and Sensitivity (default 5). Enabled is stored as `*` before the token
  (`*dev2_axis_z`); `current.map` then binds it as `Event` instead of `State`. Dead zone and sensitivity go to the
  top level of `general.actions`: `"additive": {"<action>": {"deadzone": 0.17, "sensitivity": 0.11}}`, both the shown
  value / 100 (dialog: Dead Zone 0..80 %, Sensitivity up to 20; a stored 0.22 shows as 20). The game compiles them into
  the `[additive]` section of `current.map` (`deadzone = 0.17; sensitivity = 0.11;`). Found with input.dll's format
  strings and a test write (an `additive` object in `shared.responses` or in a curve entry is ignored), then confirmed
  in the dialog in both directions.
- Settings > INPUT DEVICES, stored in `data\NSData\UserData\UserSettings.json` section `input` (not `startup.cfg`,
  which the game left unchanged): Control Mode (`plane_control_mode`: Classic, Stabilized, Mouse control), Axis
  Noise Filter (`joyFilter`, %), mouse Sensitivity / Invert Y / Exclusive Input (`mouseSensitivity`,
  `mouseInvertYAxis`, `exclusive`), Force Feedback (`ffEnabled`), Shaking Power (`ffAmplitude`), Feedback Power
  (`ffForce`), Low-Speed Stick Force (`ffForceFactorMin`), High-Speed Stick Force (`ffForceFactorMax`, above 100 %
  possible: 178 % seen), Force-Neutral Rebalancing (`ffPositionFactor`), Terrain Contact (`ffContactFactor`); also
  `plane_control_harmonization` / `plane_control_normalized` (global defaults, unchanged by the pitch editor).
- Which axis gets which editor (`tools\e2e\research\KoreaAxesSurvey.ps1`: all 46 axis commands of the page on vJoy
  with sensitivity -30, dead zones 10/5; compiled tables classified, then every editor opened and read):
  - `rpc_pitch`, `rpc_roll`, `rpc_yaw`: Built-in Response Curve and Control Surface Loading drop-downs, Exponent,
    Dead zone: Center/Edges; pitch also the profile drop-down and Center offset.
  - Other centred axes (trims, cursor, zoom, head pitch/yaw, gunsight, visor, radar range): Exponent, Dead zone:
    Center/Edges.
  - One-sided axes (`rpc_all_engines_throttles`, `_props_pitch`, `_mixtures`, `_water_radiators_shutters`,
    `_oil_radiators_shutters`, `_in_cowls_shutters`, `rpc_wheel_brakes`, `_left`, `_right`, `rpc_yaw_left`,
    `rpc_yaw_right`): Exponent, Dead zone: Low/High; table over the whole travel.
  - Camera move/rotate axes, head roll and displacement, turret view: no editor and no table.
  - A negative exponent works on every kind (-30: concave, matches the model with s < 0).
- Device List (device numbers), Device Filter and the profile drop-down of Key mapping (input profile "General",
  Profile Manager) hold no response settings.

## JoyPro integration (`engine il2korea`, experimental)
- Config path: `<install>\data\input\general.actions` (install from Steam or the folder set by the user), then other `*.actions` there.
- Device numbers: a device JoyPro writes keeps its number; a connected device without one gets the lowest free number from 1 up in `known.devices.json` (a new entry when the game has never seen it) and an entry under `devices`. This is what the game's "assign device number" dialog does. A device without a number that is not connected is skipped.
- Modes (flight-only game): clean releases every joystick input of the devices the export writes and keeps keyboard, mouse and button pairs; nuke also releases the devices the export does not use; merge removes nothing and skips an action that already has an input of the same device; merge-overwrite replaces it and removes JoyPro's own old inputs (ownership in `general.actions.joypro.json`); add appends.
- Catalog `DB\IL2Korea\actions.tsv` (the maintainer's development list; not shipped and not in the public repository): every action of the game's Key mapping page with the English title the page shows (378, 2026-10-05), in page order (groups, `subgrp_*` subgroups, actions; localization keys `inp:<id>`). JoyPro itself never reads the game's packed `.gtp` archives or the running game; a release builds the list from the user's files (below). Not in the catalog: the `_p`/`_n` halves of axes (the page shows them inside the axis row and `general.actions` writes them as `key_a/key_b` on the axis), the commands after the page's `hidden` group, and five fuel valve/pump actions the page lists but the game does not show. Commands an older catalog had (from `general.actions`) stay. Cross-checked in the game: `tools\e2e\research\KoreaCatalogOcr.ps1` scrolls Key mapping > All and OCRs every row, `korea_catalog_compare.py` matched all shown rows. `engine il2korea refresh-db [<general.actions or input folder>]` adds the ids of a key mapping file that the catalog lacks.
  On a fresh install (no shipped list, docs/games/README.md) this is the whole list: 318 actions on the dev PC. Titles
  (2026-10-12): Korea's own texts are only in its encrypted `.gtp` archives, which JoyPro does not read; so a title
  comes from IL-2 Great Battles where the id is the same (its built list, else the key mapping files the installed
  IL-2 GB writes, `data\input\*.actions`, comment after `//`; a comment the game left as `inp<id>` counts as none), else
  JoyPro's own wording for the cryptic ids (`rc_*` rangefinder, `bc_*` pilot body, `mc_*` mouse control, `rb_*`
  turret, `vc_*` vehicle, `gui_*` menu, `ext_action<n>` "Extra action n", `cockpit_input_command_<n>`), else the id in
  words. The 60 page actions without an input are not in `general.actions` and need a game update of JoyPro's own
  wording to come in.
- Axis settings per relation (`engine binds tuning set <relation> --scope game:IL2Korea`, model `il2korea`): sensitivity
  (exponent, -100..100), deadZone0/deadZone1, offset (pitch), harmonization (0-2) and normalized (0-1) for pitch, roll
  and yaw, relative (1 = Relative Control, the `*` token prefix), relativeDeadZone (0..80 %) and relativeSensitivity
  (1..20) as the dialog shows them (written to `"additive"` only when set; import reads them back), slider (one-sided
  axis; the import sets it for the throttle-like axes, only the preview uses it). Axes without an editor (camera
  movement) get no response entry.
- Force feedback and the axis noise filter (`engine ffb set --device * --game IL2Korea --param ffForce=80 ...`):
  written in place to `UserSettings.json` "input" (ffEnabled, ffAmplitude, ffForce, ffForceFactorMin/Max,
  ffPositionFactor, ffContactFactor, joyFilter; % as the game shows them). JoyPro's earlier keys (enabled, force,
  amplitude in startup.cfg, 0..1) are taken over.
- In game (`tools\e2e\games\IL2KoreaSettings.ps1`, random values per run): Settings > Input devices shows every force
  feedback value and the noise filter JoyPro wrote; a one-sided throttle with a signed exponent compiles to JoyPro's
  preview (1e-6) and its editor shows exponent and Low/High (x 4); Relative Control compiles as an `Event` binding
  and its dead zone and sensitivity into `[additive]`; the pitch editor shows the built-in curve and loading JoyPro
  set. First passing run 22/22, with Relative Control's values 23/23.
- Tested: smoke test on synthetic files; in-game e2e (`tools\e2e\games\IL2Korea.ps1`): random vJoy binds exported with vJoy unnumbered, the game shows them under Key mapping > Basic with "joy 2 vJoy Device" in use and no device-number prompt, a hat bound in the game is written as `dev2_pov0_<angle>` and imported, mode matrix on copies.
