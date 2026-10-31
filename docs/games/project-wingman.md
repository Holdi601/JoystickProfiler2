# Project Wingman (Sector D2, Unreal Engine 4.27)

Status: **integrated, native** (`engine pw`, game id `ProjectWingman`). Several devices at once. No anti-cheat.
Installed on a test PC (Steam 895870, Anniversary Edition 2.1.1208); everything below was observed on the game on
2026-10-12 [OBS] unless marked.

## How the game stores controls
- **Binds:** `%LOCALAPPDATA%\ProjectWingman\Saved\Config\WindowsNoEditor\Input.ini`, section
  `[/Script/Engine.InputSettings]`:
  ```
  ActionMappings=(ActionName="Hide/Unhide UI",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=Joystick_vJoyDevice_8_Button4)
  AxisMappings=(AxisName="Pitch Axis",Scale=1.000000,Key=Joystick_vJoyDevice_8_Axis1)
  ```
  - The game writes the file **the moment a bind changes** in the controls menu (not on exit), with the whole list as
    plain lines, sorted by name and key. A list it has not changed yet is not in the file at all (after the first axis
    bind only `AxisConfig` and `AxisMappings` were there): the pak's defaults apply. Because a plain line replaces the
    defaults, JoyPro writes the defaults of a missing list first (read from
    `<install>\ProjectWingman\Content\Paks\pakchunk0-WindowsNoEditor.pak`, `ProjectWingman/Config/DefaultInput.ini`,
    `+`/`-` lines applied). The `Android_*` keys of the defaults are kept: **the menu shows one slot per mapping**, and
    those keys are the empty slots (without them View Padlock X showed a single slot). The game itself drops them when
    it saves, so a list the game wrote has fewer empty slots after the next start.
  - Nothing is rewritten on exit (Input.ini identical after quitting). The first start writes `Engine.ini`,
    `GameUserSettings.ini` and empty plugin inis next to it, and updates `SaveGames\savegame.sav`.
- **Joystick keys** come from the UE JoystickPlugin 0.9.3 (SDL2, `Joystick_%s_%d_Axis%d`, `..._Button%d`,
  `..._Hat%d_%s`, `..._Ball%d_%s` in the exe):
  - `%s` = the **DirectInput product name without spaces** (`vJoy Device` -> `vJoyDevice`, `VPforce Rhino FFB
    Joystick` -> `VPforceRhinoFFBJoystick`); not the HID string.
  - `%d` (first) = the device's **place in the game's device list** (Controls > Miscellaneous, "DirectInput devices
    (XInput devices ignored)"), which is DirectInput's order **reversed**: with 12 controllers vJoy (DirectInput
    index 3) is `8`. So the number changes when a controller is added or removed; JoyPro computes it from the
    connected devices at export and warns on import when a unique name moved. XInput pads are not in the list.
  - `Axis<n>` counts the device's **present** axes from 0 in X Y Z Rx Ry Rz Slider Slider order (vJoy with X,Y,Z,Rz,
    Sl0,Sl1: Y = `Axis1`, Sl0 = `Axis4`). `Button<n>` counts from 0 (vJoy button 5 = `Button4`).
  - A comma in a name would break an unquoted key: JoyPro quotes such keys.
- **Invert** = the axis slot's Invert box = `Scale=-1.000000`.
- **Hat switches** cannot be bound in the menu (a hat press while a slot listens does nothing). Controls >
  Miscellaneous has "Hat Function 1" / "Hat Function 2" = WEAPON or VIEW (`PovWeapon0/1`, `PovView0/1` booleans in
  savegame.sav).
- **Curves + dead zones** (Controls > CURVES + DEADZONES), one row per axis action, for all devices together:
  `SaveGames\savegame.sav` (GVAS, `/Game/ProjectWingman/Blueprints/Instance/WingmanSaveGame`), FloatProperty
  `<Axis>Curve` and `<Axis>Deadzone` for Pitch, Roll, Thrust, Yaw, ViewX, ViewY. Only present once changed. The menu
  shows CURVE = 2 x the stored value (stored 0.7104 = "1.42", default 1.00) and DEADZONE in percent (0.3876 = "39%").
  Game defaults: pitch/roll 25 %, view 10 %, thrust/yaw 0 %. No checksum. Steam Cloud syncs the SaveGames folder.
- **Menu layout:** one slot per mapping. KEY BINDINGS shows three columns (a fourth is scrolled off to the right),
  AXIS BINDINGS two by two with an Invert box each; keyboard keys come first, the order of the others follows the
  list. The `Android_*` mappings of the defaults show as empty slots, so with the pak's defaults every row has free
  slots; an exported joystick mapping takes the next slot (e.g. Weapon Cycle: X | vJoy | mouse wheel | ...).
- The catalog: 6 axes (Pitch, Roll, Yaw, Thrust, View Padlock X/Y) and 14 buttons from the pak's `DefaultInput.ini`
  (menu rows and the mouse axes left out). "Thrust Axis" is a centred axis (shift/ctrl = +/-): a throttle lever
  accelerates above its centre and brakes below it.
- Steam Input should be off for the game (a Steam Input pad shows as an XInput device). No anti-cheat, no store, no
  account (the "PASSWORD / LOGIN SUCCESSFUL" lines on the menu are decoration).

## What JoyPro does (`engine/Games/ProjectWingmanIntegration.cs`)
- `engine pw refresh-db [pak|install folder|Input.ini]`: the flight actions from the pak (and the user's Input.ini).
  A catalog of the current game ships in `DB\ProjectWingman\actions.tsv`.
- `engine pw scan|import`: every joystick mapping of a catalog action, by name + place; Scale -1 = inverted; the
  curve and dead zone of the axis action come along as the game's tuning (model `projectwingman`: `curve` in menu
  units, `deadZone` 0..1).
- `engine pw export`:
  - add only adds: a bind on an input the game uses for another flight action is not written (listed as a skipped
    multi bind), the game's own form of a bind (Invert) stays; merge also removes the game's mapping on an input the
    profile gives another action and keeps the action's other mappings; merge-overwrite also removes the other
    joystick mappings of every action the profile binds (a move fully moves); clean drops the other joystick mappings
    of the exported devices (a flight-only game); nuke also those of the other connected devices. Only mappings the
    import reads are removed: keyboard, gamepad, mouse, hats, menu rows and devices that are not connected stay;
  - hats are refused with the game's own way (Hat Function), modifiers too (the game has none);
  - savegame.sav: `<Axis>Curve` / `<Axis>Deadzone` edited in place or added before the closing `None`; every other
    byte stays. The tuning model's formula (rescaled dead zone, curve as exponent) is a working hypothesis;
  - Input.ini keeps its other lines, order and line ends; backups as for every export.
- Notes for the user (export output): close the game first; after adding or removing a controller export again.

## Tests
- Smoke: `engine/tests/smoke/projectwingman.ps1` on fixtures of the real shape (`tests/fixtures/projectwingman`, a
  v11 pak and a GVAS save built by the script).
- In game: `tools/e2e/games/ProjectWingmanSettings.ps1` (RunAll `projectwingman`): random vJoy axes/buttons, invert,
  Pitch curve and dead zone exported; KEY BINDINGS / AXIS BINDINGS / CURVES + DEADZONES read by OCR; View Padlock X
  bound in the game; quit; import reads both back. Snapshots of `Saved\Config` and `Saved\SaveGames` are restored.
- Live runs on 2026-10-12: in both complete runs (`C:\Temp\jp2-e2e\runs\20261012-063548-pw-settings`,
  `...-064045-pw-settings2`) the game showed every exported bind on its pages (screenshots: e.g. "VJOYDEVICE 8 BUTTON
  24/18/3", "VJOYDEVICE 8 AXIS 1/2/3/4", Invert boxes, CURVE 0.30 / DEADZONE 23 %), the import after the exit read all
  of them and savegame.sav kept the curve and dead zone (run 2: 19 of 25 checks passed; the failures were the
  script's OCR of the small slot text, fixed afterwards and verified on those screenshots, and the View X bind made in
  the game, whose slot had vanished because the defaults were then written without the `Android_*` placeholders, fixed
  in the export). A full rerun was blocked: from 06:44 an invisible GameInputSvc window held the foreground, and no
  click or key could reach any program (see the report of that night). **Still to do with input possible:** one full
  run of `ProjectWingmanSettings.ps1`.
- A closing note: when the game quits, Windows' GameInputSvc may take the foreground (seen once, 2026-10-12 06:43);
  then injected input fails until someone uses the mouse or keyboard.
