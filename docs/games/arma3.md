# Arma 3 (experimental, not tested on a real install; the game was never started on this PC)

## Files
- Default profile: `Documents\Arma 3\<Name>.Arma3Profile`. Other profiles: `Documents\Arma 3 - Other Profiles\<Name>\<Name>.Arma3Profile`. [SRC https://community.bistudio.com/wiki/Profile]
- Folder and file names are URL-encoded (`Mission%20Builder`); decode with `Uri.UnescapeDataString`. [OBS]
- `-profiles=` moves the root; Documents may be redirected (OneDrive). Resolve the known folder, allow a manual path.
- Never touch `<Name>.vars.Arma3Profile` (binarized) or `.3den.Arma3Profile`.
- The main profile is plain text starting with `version=2;`. A rapified file starts with `00 72 61 50` ("\0raP"): abort.
- The game saves the profile on exit and on settings changes: edit only while the game is closed.

## Format
One top-level line per action, values are signed 32-bit decimals. The game writes a line for every known action (including mods).
```
keyHeliCyclicForward[]={17,200,1048579,196873};
keyHeliCollectiveRaiseCont[]={196874,196878};
keyHeliTrimLeft[]={-1659699125};
```
Joysticks:
```
class JoysticksList
{
	class Joystick1
	{
		guid="8F1BD570-02DF-11EA-8002-444553540000";
		name="Mad Catz F.L.Y.5 Stick";
		offset=256;
		isXInput=0;
		sensitivity[]={0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0};
		deadZone[]={0.07,0.07,0,0,0,0.07,0,0};
		steepness[]={1,1,1,1,1,1,1,1};
		mode="Custom";
		...
```
- `guid` = DirectInput **instance** GUID, uppercase, no braces. [OBS]
- `offset` = 256 × N, assigned when the game first sees the device; entries stay after unplugging. A changed GUID creates a new entry with a new offset. [OBS/forum]
- `mode="Custom"` = enabled, `"Disabled"` = disabled (the exe hashes the string case-insensitively; anything but "Disabled" counts as on). `isXInput=1` = gamepad (XInput codes, schemes).
- Axis tuning (`sensitivity[]` 16 values, `deadZone[]` 8, `steepness[]` 8) is per device and physical axis, not per action. No invert flag: invert by binding the opposite half axis.
- Save order of an entry: guid, name, offset, isXInput, sensitivity[], deadZone[], steepness[], mode, analogClickHigh, analogClickLow (Florian sample); floats are 32-bit with up to 8 significant digits (`0.40000001`, `0.050000001`, `0.07`).

## Response settings (per device and physical axis) [OBS-exe: behaviour of the installed arma3_x64.exe, 2026-10-05; addresses for maintainers]
- Load (0x14103f2ff): `sensitivity[]` values clamped to -1..1; fewer than 8 values are copied to both halves; missing
  values 0. `deadZone[]` not clamped; missing entries default to {0.07, 0.07, 0, 0.07, 0.07, 0.07, 0, 0} (X, Y, Z, RX,
  RY, RZ, S1, S2). `steepness[]` default 1.0.
- Dead zone: a virtual method (0x1410414e0, vtable 0x141c701b0) sets `DIPROP_DEADZONE = (int)(deadZone[a] * 10000)`
  (truncated) per axis; DirectInput reports the centre inside it and scales the rest [INF: DirectInput's documented
  behaviour]. Axis range -100..100, value / 100.
- Value of a bound half axis (0x141078160): v = clamp(+x, 0, 1) for locals 0..7, clamp(-x, 0, 1) for 8..15; locals
  16..23 / 24..31 (both halves on one action) v = (pos - neg + 1) / 2 or its reverse.
- Curve (0x1410715e0): s = `sensitivity[local]` (local < 16, else `[a]`); s >= 0: f = s * v^(1/3) + (1 - s) * v
  (powf with 1/3); s < 0: f = |s| * v^3 + (1 - |s|) * v; output = clamp(`steepness[a]` * f, 0, 1). So steepness
  multiplies and saturates; it is not an exponent.
- Customize dialog (ui_f.pbo config, decoded): Sensitivity slider (IDC 1900) with the pictures convex / linear /
  concave above it (left = -1 = cubic, right = +1 = cube root); Dead Zone slider (1901, "max 10%"); Steepness combo
  (2100) `values[]={"1.0","1.2","1.5","1.9","2.4","3.0"}`.
- Whether the slider writes the same value to both halves, and the per-action `DefaultAnalogCurveDefs` (Gamma) of some
  actions on top of this, are not verified (the preview shows the device curve only).

JoyPro (axis model `arma3`, `engine\AxisModels\Arma3.cs`): parameters `sensitivityPos` ([a]), `sensitivityNeg` ([a+8]),
`deadZone` (0..0.1), `steepness` (snapped to the six choices, with a warning), `slider` (full-axis throttle: the curve
over the whole lever with `sensitivityPos`), `invert`. Scope `game:Arma3` (or a plane scope); there is no family and no
fit of the base curve: the base is not written to Arma 3, only values set for Arma 3 or imported. Export writes the
values of every exported relation into its device's arrays (one curve per physical axis: when two relations disagree the
first one by name is written and a warning names both); a missing array is created after its predecessor in the save
order with the engine defaults for the other axes, and only when a value differs from the default. Import puts the
arrays of each bound axis into the bind's `game:Arma3` tuning; import + export is byte-identical.

## Device and game settings (`engine ffb set --game Arma3`, `engine\FfbModels\Arma3.cs`)
- Per device (`--device "<name> {GUID}"`): `enabled` (mode Custom / Disabled), `analogClickHigh` / `analogClickLow`
  (axis used as a button: pressed above / released below; defaults 0.4 / 0.2; out-of-range values are reset by the game
  on load; refused when low > high). A disabled device that gets binds is switched to Custom by the export (step
  message) unless `enabled=0` is set.
- Game-wide (`--device *`): `RTDEnabled` (helicopter advanced flight model), `RTDAutoTrim`, `RTDFullHUD`,
  `RTDRoughLanding`, `RTDWindDynamic`, `RTDStressDamage`, `vibrations` (gamepad rumble): top-level `key=0|1;` lines,
  changed in place or added after the other RTD lines. The label pairing (e.g. Show gauges = RTDFullHUD) is inferred.
- An export without Arma 3 binds still writes these settings (and touches no key line).
- Arma 3 has no joystick force feedback option (only "%1 Vibration enabled/disabled" hints; not found as a setting).

## Throttle
"Thrust (Analog)" / "Speedbrake (Analog)" (`HeliThrottlePos` / `HeliThrottleNeg`) need both halves of the lever on the
action (game tooltip; T65252). JoyPro writes `Axis.PlaneThrottle` / `Axis.PlaneAirbrake` as a full axis: negative half
(lever forward) first, inverted: positive half first. Before 2026-10-05 only one half was written.

## Input codes [SRC https://community.bistudio.com/wiki/Arma_3:_Modded_Keybinding]
```
INPUT_DEVICE_SOLO_KB 0x00000000  keyboard (DIK)
INPUT_DEVICE_SOLO_MB 0x00010000  mouse button (+0x80 click)
INPUT_DEVICE_SOLO_JB 0x00020000  stick button
INPUT_DEVICE_SOLO_JA 0x00030000  stick axis
INPUT_DEVICE_SOLO_JP 0x00040000  stick POV
INPUT_DEVICE_SOLO_XI 0x00050000  XInput
INPUT_DEVICE_SOLO_TR 0x00080000  TrackIR
INPUT_DEVICE_SOLO_MA 0x00100000  mouse axis
INPUT_DOUBLE_TAP_OFFSET 0x100
```
With `off` = the device's `offset` (256, 512, ...):
- Keyboard: DIK code (17 = W, 200 = Up); double tap `0x100 + DIK`.
- Joystick button: `0x20000 + off + button` (0-based). [INF]
- Joystick axis: `0x30000 + off + a`, a = 0..7 positive half, a+8 negative half; order X, Y, Z, RotX, RotY, RotZ, Slider1, Slider2. [INF, axis order SRC Joystick_Sensivity_Adjusting] Examples for offset 256: HeliCyclicForward 0x30109 (Y−), HeliCyclicBack 0x30101 (Y+), AirBankRight 0x30100 (X+), HeliRudderLeft 0x3010D (Rz−).
- POV: `0x40000 + off + dir`, dir 0..7 clockwise from up. Only one hat per device seems encodable. [INF]
- Combos: `(modifierKey << 24) | (comboType << 20) | (mainType << 16) | (CC << 8) | DD`. Current profiles write LCtrl+W as `0x1D130011` (= 487784465); RCtrl produces negative decimals. Old profiles use `0x1D000000 + DIK`: import both, write the new form. Keyboard modifier + joystick button would be `0x1D140000 | off | btn` [INF, no real sample]. Joystick-button modifiers are unconfirmed. [UNVERIFIED]

## Flight actions (profile key = "key" + name) [SRC https://community.bistudio.com/wiki/inputAction/actions]
- Helicopter: HeliCyclicForward, HeliCyclicBack, HeliCyclicLeft, HeliCyclicRight, HeliCollectiveRaise, HeliCollectiveLower, HeliRudderLeft, HeliRudderRight, HeliLeft, HeliRight, AutoHover, AutoHoverCancel, LandGear, LandGearUp, HeliCollectiveRaiseCont, HeliCollectiveLowerCont, HeliWheelsBrake, HelicopterTrimOn, HelicopterTrimOff, HeliTrimLeft, HeliTrimRight, HeliTrimForward, HeliTrimBackward, HeliTrimRudderLeft, HeliTrimRudderRight, HeliRopeAction, HeliSlingLoadManager.
- Plane: HeliForward, HeliBack, AirBankLeft, AirBankRight, HeliFastForward, HeliUp, HeliDown, HeliThrottlePos, HeliThrottleNeg, AirPlaneBrake, vtolVectoring (written as `keyVTOLVectoring`, match case-insensitively), vtolVectoringCancel, FlapsDown, FlapsUp (+ the shared rudder/gear actions).
- Related: LaunchCM, NextCM, heliManualFire, Eject, EngineToggle.
- UAVs use the same actions. Heli and plane share HeliRudder*, HeliLeft/Right and LandGear*.

Analog notes (T65252): throttle needs both halves of an axis on HeliThrottlePos (analogue); collective needs one half on raise and the other on lower.

Catalog (`DB\Arma3\actions.tsv`, built on the user's PC by `engine arma3 refresh-db`, no first start needed since
2026-10-12; `engine/Games/ArmaGameData.cs`, read only): the action names from the game config (`Dta\bin.pbo` >
`config.bin`, class `CfgDefaultKeysPresets >> Default >> Mappings`, 387 entries in 2.20) and their English menu names
from `Dta\languagecore_f.pbo`'s `stringtable.xml` (`str_usract_` + the action in snake case; a few actions have other
keys and keep a name made from the id). Kept: the flight actions above, the ones of JoyPro's function list and LaunchCM,
NextCM, Eject, EngineToggle; the analogue halves of JoyPro's axis relations (`HeliCollective*Cont`,
`HeliThrottlePos/Neg`) are no buttons. The player profile, when there is one, adds actions of mods and those the default
preset does not list (Eject). Fresh build on the dev PC: 51 actions (= the development list; before: 37).

## Safe write
Rewrite only lines matching `^key<FlightAction>\[\]=\{...\};$`. In each: remove codes that belong to devices JoyPro manages (device type JB/JA/JP with that device's offset, and combos whose main input is on those devices), keep keyboard, mouse, TrackIR and XInput codes, append the new codes. Map devices via the instance GUID in `JoysticksList`; if a device is missing, warn and ask the user to start Arma once with it; a disabled device that gets binds is enabled. The JoysticksList arrays and properties and the top-level game options are edited only as described above; every other byte stays.

In-game check (prepared, not run): `tools\e2e\games\Arma3Settings.ps1` (stages prime / prepare / launch / check / restore; test profile `JoyProE2E`; main menu > Options > Controls only, arma3_x64.exe without BattlEye).

Samples: https://github.com/Florian-DELRIEU/Arma (Florian.Arma3Profile), https://github.com/ademirt/Arma3, https://github.com/teef22/Arma3_profile_stub.
