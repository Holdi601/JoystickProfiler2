# G-Rebels (Reakktor Studios / Senatis, Unreal Engine 5.8, Early Access since 2026-07-20)

A cyberpunk flight-combat game, the spiritual successor of G-Police: you fly the "Skyblade" VTOL over megacities.
Steam app 2445980, process `G_Rebels-Win64-Shipping`.

Status: **integrated, experimental: `engine grebels`** (`engine/Games/GRebelsIntegration.cs`, `GRebelsSaveOptions.cs`):
`paths`, `catalog`, `scan`, `import`, `export` (merge-overwrite / merge / add / clean, `--nuke-untouched-devices`,
`--dry-run`). Started for the first time by the e2e kit on 2026-10-11 (build 26/0911.0.64; no EULA, no store, no
anti-cheat); the save layout below is from that start and from binds made in the game with vJoy.

Tags: **[OBS]** seen on the dev PC · **[SRC]** the game's files · **[WEB]** public sources.

## Input in the game [OBS]
- OPTIONS > INPUT > **DEVICES** lists every HID joystick (vendor + product line) with a dropdown **DEACTIVATED / 0 - CHANNEL
  ... 5 - CHANNEL**. Everything starts DEACTIVATED. A device with a channel gets a tab **JST<channel>** under
  OPTIONS > INPUT > **CONTROLS** (next to KEYBOARD, GAMEPAD and JMNU).
- A JST tab has:
  - AXIS: "Joystick Axis X, Y, Z, RX, RY, RZ, Slider, Dial", each with a role dropdown **YAW, THRUST, ROLL, PITCH, SIDE
    STRAFE, UP / DOWN, NONE** and a wrench icon (invert, sensitivity);
  - POVs: "JST 0 POV 1..4" with the same role dropdown;
  - BUTTONS: the actions (Primary Fire, Secondary Fire, Boost, UP, ...) with a bind box; a click on the box and a
    button press binds it ("Enhanced Raw Input Controller 0 Button 5" for vJoy button 5).
- Input itself: the EnhancedRawInput plugin (raw input, `GenericUSBController`/`RawInput_Controller<n>` keys) and
  XInput [SRC]. The FAQ suggests vJoy for pedals [WEB].

## Files [OBS]
`%LOCALAPPDATA%\G_Rebels\Saved\SaveGames\`
- **`SaveOptions.sav`**: GVAS (UE 5.8) of the Blueprint `SaveOptions_C`, UE 5.4+ property tags (name, type tree,
  size, flags; a bool's value is the tag flag 0x10). 4.7 KB after the first start, 63 KB after a JST tab was used.
  - `Joysticks`: `S_Controller_Save { DeviceID (the HID path, saved with one NUL too many), Enabled, MultiID (channel) }`.
  - `AxisOptions`: `S_Input_Axis_Save { Key (FKey: "MouseX", "Gamepad_LeftY", "RawInput_Controller0_AxisY"...), Offset,
    Multiplier, Role ("E_Input_Axis_Roles::NewEnumerator<n>", n = 0 Yaw, 1 Thrust, 2 Roll, 3 Pitch, 4 Side strafe,
    5 Up/down, 6 None), Inverted, DeadZone }` - one entry per axis of every channel the game has seen.
  - No checksum: an in-place edit of a fixed-size value is safe.
- **`EnhancedInputUserSettings.sav`** (appears with the first button bind): UE Enhanced Input player-mapped keys, the
  same layout JoyPro already writes for Hell Let Loose: Vietnam (`GvasKeyMappings`). Names are `<ACTION>JST<channel>`
  ("PRIMARYFIREJST0"), keys `RawInput_Controller<channel>_Button<n>` (n = the DirectInput button number, 1-based).
- `Saved\Config\Windows\GameUserSettings.ini`: graphics etc.; the old note about `Input.ini` RawInputSettings (VID/PID)
  is from the shipped engine config, not used for binds.

## What JoyPro does
- **Axes:** a relation on `Axis_Pitch` (Roll, Yaw, Thrust, SideStrafe, UpDown) with a device's axis sets that axis'
  role (and Invert) on the device's channel. The value is changed in place; entries are never added, so the game must
  have saved the channel once: switch the device on in DEVICES and change one axis role on its JST tab (opening the tab saves nothing; the first role change saves all eight axis rows of the channel). merge-overwrite gives another
  axis of the channel with the same role the role None; clean gives every other role of a written channel None; nuke
  does it on the enabled channels JoyPro does not write; merge keeps a role the game already set.
- **Buttons:** `<ACTION>JST<channel>` = `RawInput_Controller<channel>_Button<n>` in EnhancedInputUserSettings.sav (it
  must exist: bind one button in the game once). The catalog's button rows carry the mapping base name in the 5th
  column; so far only **Primary Fire** (`PRIMARYFIRE`) is confirmed. Other actions follow when their names are seen.
- **Devices:** matched by HID path (SaveOptions' `DeviceID`) to the channel; a device without a channel is skipped
  with "switch it on in OPTIONS > INPUT > DEVICES". Hats (POV rows), dead zone and sensitivity are not written yet.
- Catalog: `DB\GRebels\actions.tsv`, JoyPro's own table (the game's paks are compressed/encrypted and are not read).
  A release ships no game DB folder, so `engine grebels refresh-db` (GUI: Build List / Update) writes the table from
  the code into the game-data folder (2026-10-12; before, a fresh install had no G-Rebels list); rows already there keep
  their entry.
- Backups: both files go through `GameExportSafety` before a write.

## In-game check (`tools\e2e\games\GRebelsSettings.ps1`)
Two starts: the first gives vJoy channel 0 and sets Joystick Axis X to UP / DOWN on JST0 (the game saves the axis rows), the second checks the
export (random roles for Pitch/Roll/Yaw on three vJoy axes, Primary Fire on a random button) on JST0; the import after
exit reads them back; the SaveGames folder is restored at the end, so the user's device setup comes back.

## The vJoy route
The virtual joystick (docs/virtual-joystick.md) puts the whole HOTAS on one vJoy device, which then needs one channel
in the game; this avoids the shared-key overlap of several raw-input devices.
