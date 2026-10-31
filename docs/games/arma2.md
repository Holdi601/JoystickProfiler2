# Arma 2 and Arma 2: Operation Arrowhead (experimental, not tested in game)

CLI: `engine arma2oa` (Operation Arrowhead, Combined Operations, BAF, PMC, ACR, DayZ mod) and `engine arma2` (Arma 2 standalone). Code: `engine/Games/Arma2Integration.cs` on top of the Arma profile code shared with Arma 3 (`ArmaProfileIntegration.cs`). Catalogs: `EngineOnly_Runtime/DB/Arma2OA`, `DB/Arma2`, `DB/Functions/Arma2OA.tsv`, `Arma2.tsv`. Test fixtures: `engine/tests/fixtures/arma2` (synthetic profiles with the smoke-test devices).

Research date 2026-10-01. Tags: **[OBS]** seen in the installed files, **[OBS-exe]** behaviour of the installed game executable, looked at for interoperability only, nothing modified (code addresses below, for maintainers), **[SRC]** documentation / forum / third-party file with URL, **[INF]** inferred, **[UNVERIFIED]** needs one real game start.

Versions inspected [OBS]:
- Arma 2 (standalone): `arma2.exe` FileVersion 1.11.0.148854, `Dta\bin.pbo` → `config.bin`, `Dta\product.bin`.
- Arma 2 OA: `ArmA2OA.exe` 1.64.0.144629 ("ArmA 2 OA 1.64.144629"), `Expansion\dta\bin.pbo` (revision 112555), `Expansion\dta\product.bin`.
- The research scripts were throw-away tools and are not in the repo. At run time JoyPro reads the game config (`bin.pbo`, the PBO / raP formats Bohemia's own modding tools handle) and the user's profile.

## 1. Which game uses which file

| Executable | Products | Profile file |
|---|---|---|
| `arma2.exe` | Arma 2 standalone | `<Name>.ArmA2Profile` |
| `ArmA2OA.exe` / `ArmA2OA_BE.exe` | OA, Combined Operations (`_runA2CO.cmd` = OA app 33930 with `-mod=<A2 dir>;EXPANSION;ca`), BAF, PMC, ACR, DayZ mod | `<Name>.ArmA2OAProfile` |

- `product.bin` (raP) of both games [OBS]: `profilePathDefault="ArmA 2"`, `profilePathCommon="ArmA 2 Other Profiles"`; `extensionProfile="ArmA2Profile"` (A2) / `"ArmA2OAProfile"` (OA); OA also `defaultMods="ca;expansion;baf;pmc"`.
- Default profile: `Documents\ArmA 2\<Name>.<ext>`; other profiles: `Documents\ArmA 2 Other Profiles\<Name>\<Name>.<ext>` (no dash, unlike "Arma 3 - Other Profiles"). [OBS product.bin, SRC https://community.bistudio.com/wiki/Profile, SRC forum quote "c:\Users\username\My Documents\ArmA 2 Other Profiles\profilename\profilename.ArmA2OAProfile" https://forums.bohemia.net/forums/topic/106686-arma-2-combined-ops-keeps-dropping-joystick-mappings/]
- **A2 and OA share the same folders.** One player name can have both `X.ArmA2Profile` and `X.ArmA2OAProfile` with independent binds and independent joystick offsets. Treat them as two games. The folder also holds `ArmA2.cfg` / `ArmA2OA.cfg` (video settings) [SRC https://forums.bohemia.net/forums/topic/134536-arma-2-oa-profile-path/] - never touch.
- `-profiles=<dir>` moves profiles to `<dir>\Users\<Name>\` (relative paths are relative to the game folder); `-name=<Name>` picks the profile. [SRC https://community.bistudio.com/wiki/Arma_2:_Startup_Parameters]
- `<Name>.vars.<ext>` exists (string `.vars.` in OA exe) [OBS]; binarized scripting variables [SRC Profile wiki]. Never touch.
- Player names with spaces/special characters: Arma 3 percent-encodes folder/file names; for Arma 2 this is **[UNVERIFIED]**. Decoding with `Uri.UnescapeDataString` is harmless either way; enumerate by extension, do not build the file name from the display name.
- Binds live in the main profile, no separate keys file. [OBS sample + OBS-exe save routine]
- Process names for "game is running": `arma2`, `arma2oa`, `arma2oa_be` (ignore `arma2oaserver`). [OBS install]
- Steam app ids: OA 33930 (+33933 BAF/PMC depot), A2 33910 [OBS `*_install.vdf`, `steam_appid.txt`].

## 2. File format

Plain text ParamFile, same family as Arma 3. Real sample `Mych.ArmA2OAProfile` [SRC https://github.com/themych/Arma-2-config]: ASCII, CRLF, no BOM, starts with `\tversion=2;` (leading TAB - the parser must accept leading whitespace). [OBS sample]

Key lines (top level, one per action, signed 32-bit decimal):
```
keyHeliCyclicLeft[]={203,31};
keyFire[]={487653376};
keyLandGear[]={-1660944350};
keyToggleWeapons[]={65540,131330};
keyHeliThrottlePos[]={};
```
- The exe writes `key<Action>` for **every** action of its table on each save, empty ones as `{}` (OA 229 actions, A2 220). [OBS-exe OA save loop 0x6F3E07, sample]
- Order: the game updates its in-memory ParamFile, so existing lines keep their position and new entries are appended. Mych has `keyOpticsMode`, `keyZeroingUp`, `keyLaunchCM`, `keyNextCM`, ... after `class ControllerSchemes` (profile created by an older version). Do not assume a block. [OBS sample, INF]
- Load rules [OBS-exe OA 0x6F657B]: name = `"key" + action`; missing line → the action keeps its `CfgDefaultKeysMapping` default; `{}` → unbound; each element read as int; `-1` ignored; values `0x200..0x3FF` are legacy "Right Ctrl + key" and become `0x9D000000 + (v - 0x200)`.
- Action names: profile uses the exe spelling (`keyHeliCyclicLeft`), the config uses lower camel case (`heliCyclicLeft`); match case-insensitively.
- Unknown top-level entries (e.g. a key line for an action the exe does not know, A3 lines): kept by the ParamFile and rewritten unchanged. [INF]
- Other bind-related top-level entries [OBS sample/OBS-exe]: `revMouse`, `mouseSensitivityX/Y`, `floatingZoneArea`, `mouseSmoothing`, `joystickSensitivity[]` (16 floats), `class JoysticksList`, `customScheme`, `trackIR`, `freeTrack`, `controller="Default"`, `sensitivity="Medium"`, `vibrations`, `class ControllerSchemes {}`.

## 3. Devices: `class JoysticksList`

```
class JoysticksList
{
	class Joystick1
	{
		guid="D6F3FED0-E2B9-11E5-8001-444553540000";
		name="Controller (Wireless Gamepad F710)";
		offset=256;
		isXInput=1;
		mode="Custom";
	};
};
```
[OBS Mych sample; second real example with `mode="Disable"`: SRC forum 106686 above]

Fields, from the OA load (0x6DA02B) and save (0x6DA749) routines; A2 1.11 has the same field set (0x6DF04C) [OBS-exe]:
- `guid` = DirectInput **instance** GUID (`DIDEVICEINSTANCE.guidInstance`), formatted `%08X-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X` (uppercase, no braces). `name` = `tszProductName`. [OBS-exe 0x6D9F11 / caller 0x6DA551]
- `offset` = device id inside codes. New device: `offset = max(existing offsets) + 256` → 256, 512, 768, ...; entries are kept, so a device keeps its offset. A device with a new instance GUID (other USB port, Windows reinstall) is a new entry with a new offset. [OBS-exe 0x6D9F11; SRC forum 106686 "plugged into a different USB slot and it was assigned a different offset"]
- `isXInput` 0/1.
- `mode` enum: 0 `"Custom"` (DirectInput binds active), 1 `"Scheme"` (XInput controller scheme), 2 `"Disable"` (note: not "Disabled"). New XInput devices start as `"Disable"`, DirectInput devices as `"Custom"`. Codes of a device in mode 2 are ignored. [OBS-exe OA 0xBB41BA, A2 0x471C93, 0x6D9FE4]
- `sensitivity[]`: written only when non-empty, i.e. after the user pressed OK in Controls > Controllers > Customize. 16 floats, index = axis (0..7) for the positive half and axis+8 for the negative half; the dialog writes the slider value to both; untouched axes 1.0. Internally the game stores 1/value and applies `pow(v, 1/value)`, for both halves with the value of index a (see 6). [OBS-exe dialog 0x8B3411, load 0x6DA1EE, use 0x6F0AAE] JoyPro writes it (see 6).
- No per-device `deadZone[]` or `steepness[]` (Arma 3 only). [OBS-exe: not referenced]
- Class names `Joystick1..N` are renumbered on each save (list order); identify devices by `guid` only. [OBS-exe save loop]
- **When the game first sees a device it appends every joystick default of `CfgDefaultKeysMapping` (offset 0 in the config) with the new device's offset** to the user binds - for every device, pedals and throttles included. Flight actions affected: HeliCyclicLeft X-, HeliCyclicRight X+, HeliForward Y-, HeliBack Y+, LookUp/LookRightUp/.../LookLeftUp POV 0..7, ToggleWeapons btn 2, LockTargets/VehLockTargets btn 1, PrevAction btn 6, NextAction btn 4, Action btn 5, ZoomIn btn 3; plus Car*/Seagull* axes. [OBS-exe OA 0x6F6A26, A2 0x73FCA8; SRC forum 106686 "Arma thinks the flight pedals have buttons and maps all sorts of things"]. The Customize dialog's "Default" re-applies them, "Unmap" removes all codes of that device. [OBS-exe 0x8B21F9 / 0x8B20F3, OBS config `RscDisplayCustomizeController`]
- Game reads only the first 32 buttons (`DIJOYSTATE`, 0x50 bytes), 8 axes and the first POV hat. Axis range is set to -100..+100 (`DIPROP_RANGE`) and a fixed 7 % DirectInput deadzone (`DIPROP_DEADZONE` 700) on the main axes. [OBS-exe 0x6D8199, 0x6DA3E0; stringtable has stick buttons #1..#32 only]

## 4. Input codes

| Input | Code | Example |
|---|---|---|
| Keyboard | DIK | 17 = W, 200 = Up |
| Keyboard double tap | `0x100 + DIK` | 274 = 2x E |
| Mouse button hold / click / double | `0x10000 + b` (b 0..7) / `+0x80` / `+0x100` | 65536 LMB, 65665 RMB click |
| Joystick button | `0x20000 + offset + b`, b = 0..31 | 131330 = dev@256 button 2 (3rd button) |
| Joystick axis half | `0x30000 + offset + a` (positive), `+ a + 8` (negative) | 196873 = dev@256 Y- |
| Joystick axis full (OA only, rarely stored) | `0x30000 + offset + a + 16` (max at +), `+ a + 24` (max at -) | |
| Joystick POV (first hat) | `0x40000 + offset + d`, d = 0 N, 1 NE, 2 E, 3 SE, 4 S, 5 SW, 6 W, 7 NW | 262400 = dev@256 POV N |
| XInput (scheme mode only) | `0x50000 + n` | |
| TrackIR / FreeTrack | `0x80000 + n` (n 0..11) | 524291 |
| Mouse axis | `0x100000 + n` (0 left, 1 right, 2 up, 3 down, 4 wheel up, 5 wheel down) | 1048579 |
| Combo key + key | `(modDIK << 24) + DIK` (double tap: `+0x100`) | 486539283 = LCtrl+R; RCtrl+G = 0x9D000022 = -1660944350 |
| Combo key + mouse button | `(modDIK << 24) | 0x110000 | b` (+0x80 click) | 487653376 = LCtrl+LMB |

Axis order a = 0 X, 1 Y, 2 Z, 3 RotX, 4 RotY, 5 RotZ, 6 Slider1, 7 Slider2 (stringtable `STR_INPUT_DEVICE_STICK_*_POS` then `_NEG`) [OBS]. Offset = `(code & 0xFFFF) & 0xFF00`, local index = `code & 0xFF` [OBS-exe 0x6F09E6, 0x8B2184]. Device type values and examples [OBS config `CfgDefaultKeysMapping`, sample, OBS-exe].

Details:
- Pushing a stick forward / left reports the negative half (default `heliForward` = Y-, `heliCyclicLeft` = X-) [OBS config]. Most throttle levers report the negative half when pushed forward [INF, same as Arma 3].
- Combos: the modifier is always a keyboard key (high byte); the main input can only be a keyboard key (no type bits) or a mouse button (`0x11` type byte). The config converter rejects anything else ("Wrong entry inside CfgDefaultKeysMapping"), and the runtime combo check only knows these two. **Joystick buttons with modifiers do not exist in Arma 2/OA**; Arma 3's `0x1D13xxxx` style is not used. [OBS-exe OA 0x6F3998, A2 0x73FDCC, sample]. RCtrl (157) and other modifiers ≥ 0x80 give negative numbers.
- Full axis (OA 1.62+ only): when one action contains both halves of the same axis of the same device, the game merges them at evaluation into one full-range input: the **first** of the two codes decides the direction (value 1 at that half's end). Changelog: "[88947] New: Full joystick axis can be mapped by mapping both positive and negative part of the same axis (map negative first to use the axis inverted)" [OBS `changelog.txt`, `ARMA2OA_Update_162_readme.txt`]; mechanism: mask `& 0xFFFFFFE7`, earlier code + 0x10 [OBS-exe 0x6F1597]; evaluation local 0-7 = clamp(v,0,1), 8-15 = clamp(-v,0,1), 16-23 = (pos-neg+1)/2, 24-31 = (1-(pos-neg))/2 [OBS-exe 0x6F0AF7]. The profile stores the two half codes, not the merged code [OBS-exe: merge happens on a copy at evaluation; key-name switch has names only for 0x30000..0x3000F]. Same rule in Arma 3 (BI: "map both, - and +, part of the axes on the throttle action", https://feedback.bistudio.com/T65252).
- Arma 2 1.11 has no merge: axis locals 0-7 and 8-15 only [OBS-exe 0x741AF4]. Both halves on one action would act as |v|. Use one half.
- Code lookups go by offset; codes whose offset has no `JoysticksList` entry are ignored [OBS-exe device search in 0x6F097A].

## 5. Flight actions (both games share them for helicopters and planes)

Full lists with English/German titles and default codes: [arma2-actions-OA.tsv](arma2-actions-OA.tsv) (229), [arma2-actions-A2.tsv](arma2-actions-A2.tsv) (220). A2 lacks OpticsMode, ZeroingUp/Down, LaunchCM, NextCM and four Buld* actions [OBS exe action tables].

What the vehicle code reads [OBS-exe OA `HelicopterAuto` 0x58B87D, `AirplaneAuto` 0x5573EA; A2 `HelicopterAuto` 0x6BD202]:
- Pitch: HeliForward "Nose down" / HeliBack "Nose up". Roll: HeliCyclicLeft "Bank left" / HeliCyclicRight "Bank right". Yaw: HeliRudderLeft/Right "Left/Right pedal". HeliLeft/HeliRight "Left/Right turn" = arcade yaw+bank mix (default mouse X) [SRC https://forums.bohemia.net/forums/topic/72302-joystick-setup-for-helicopters/].
- Thrust / collective, two sources, the one changed most recently (by more than 0.1) wins:
  - digital/rate: `HeliUp - HeliDown` ("Increase/Decrease thrust"),
  - analogue: helicopter `HeliThrottlePos - HeliThrottleNeg` absolute collective; plane thrust = clamp(HeliThrottlePos,0,1), airbrake = clamp(HeliThrottleNeg,0,1) ("Thrust (Analogue)" / "Brake (Analogue)").
- HeliFastForward "Fast forward", AutoHover / AutoHoverCancel, LandGear / LandGearUp (default both RCtrl+G; allowed by `UserActionsConflictGroups.Exceptions.landGear` → one button on both = toggle) [OBS config], FlapsDown/FlapsUp (also VTOL nozzle control in auto-hover: "Manual vectoring control in autohover mode with analogue throttle using Flaps actions" [OBS `ARMA2OA_Patch_1_59_readme.txt`]).
- Weapons: DefaultAction "Fire", ToggleWeapons, SwitchWeapon, HeliManualFire "Toggle manual fire", Fire "Command Fire", LockTarget "Lock or Zoom", VehLockTargets "Next target", LaunchCM / NextCM "Counterrmeasure (mode)" (OA only, sic).
- Not present (Arma 3 / TKOH only): HeliCyclicForward/Back, HeliCollective*, AirBank*, trims, HeliWheelsBrake, AirPlaneBrake, vtolVectoring, rope/sling, EngineToggle (engine and many vehicle functions are in the scroll action menu: Action / PrevAction / NextAction). [OBS exe table]
- Per-action response curves come from config `DefaultAnalogCurveDefs` (e.g. HeliCyclicLeft `deadZone=0; curve[]={"Gamma",2,1.5}`), not from the profile [OBS config the derapified input classes of `Expansion\dta\bin.pbo`].

Catalog: `DB/Arma2OA/actions.tsv`, `DB/Arma2/actions.tsv`; function map: `DB/Functions/Arma2OA.tsv`, `DB/Functions/Arma2.tsv`.
Built on the user's PC by `engine arma2oa|arma2 refresh-db` without a first start (2026-10-12; `engine/Games/ArmaGameData.cs`,
read only): the action names from the game config, class `CfgDefaultKeysMapping` of `config.bin` in `Expansion\dta\bin.pbo`
(OA) / `Dta\bin.pbo` (Arma 2), and their English menu names from `languagecore.pbo`'s `stringtable.xml` (key
`str_usract_` + the action in snake case, e.g. `heliCyclicLeft` -> `str_usract_heli_cyclic_left` "Bank left"). Kept: the
flight actions (`Heli*`, `Flaps*`, `LandGear*`, `AutoHover*`, `LaunchCM`, `NextCM`), the actions of JoyPro's function
list, and JoyPro's choice of weapons, view and radio actions a HOTAS carries (look, optics, zoom toggles, push to talk,
eject, get out); the analogue halves of JoyPro's axis relations (`*Cont`, `HeliThrottlePos/Neg`) are no buttons. A
profile, when there is one, adds what mods define. Fresh build on a test PC: OA 68, Arma 2 66 actions (= the
development lists; before: 20 / 19 without a first start).

JoyPro axis relations (composites):
| Id | Write | Notes |
|---|---|---|
| Axis.Pitch | HeliForward ← neg half, HeliBack ← pos half | heli + plane |
| Axis.Roll | HeliCyclicLeft ← neg, HeliCyclicRight ← pos | |
| Axis.Rudder | HeliRudderLeft ← neg, HeliRudderRight ← pos | |
| Axis.Turn | HeliLeft ← neg, HeliRight ← pos | arcade, optional |
| Axis.ThrustRate | HeliUp ← neg (lever forward), HeliDown ← pos | rate collective / throttle steps |
| Axis.Throttle | OA: HeliThrottlePos ← `{neg, pos}` (two codes, negative first); inverted `{pos, neg}`. A2: HeliThrottlePos ← neg only (inverted: pos) | plane throttle **and** absolute heli collective |
| Axis.Airbrake | like Axis.Throttle on HeliThrottleNeg | in helicopters it subtracts from the collective |
| Axis.ThrottleBrake (A2 only) | HeliThrottlePos ← neg, HeliThrottleNeg ← pos | classic A2 one-lever setup |
| Axis.LookHorizontal / Axis.LookVertical / Axis.Zoom | LookLeftCont/LookRightCont, LookUpCont/LookDownCont, ZoomContIn/ZoomContOut | |

Import of a full axis: if an action holds both halves of the same device axis, the first one decides: first negative → not inverted, first positive → inverted (the Arma 3 integration's `sameAction ? !decoded.NegativeHalf` rule gives exactly this when it keeps the first code per axis). Also accept stored locals 16..31 (full axis) on import; never write them. [INF from OBS-exe]

## 6. Sensitivity, deadzone, curves
- Per device `sensitivity[16]` in `JoysticksList` (see 3): the Customize slider p (-1..1) is stored as s = 10^p
  (checked in game, OA, 2026-10-05: the bar shows log10 s, 0.2512 at -0.60 and 0.6310 at -0.20; the earlier reading
  of OA 0x8ad93f as 10^(-p) had the sign wrong); the game applies v^(1/s) = v^(10^-p) per half axis (0x6f0aa9-0x6f0ae2) and reads `sensitivity[a]`
  for **both** halves (the second pow at 0x6f0ad5 indexes `[a]` again, it only checks that the array has a+9 values;
  re-read 2026-10-05). A full axis (OA) is (f(pos) - f(neg) + 1) / 2, i.e. the same curve over the whole lever.
- Global top-level `joystickSensitivity[]` (16 floats; Mych: 0.5 ×6, 1, 1, 0.5 ×6, 1, 1) - legacy/global, read and written by the exe; its use was not traced [OBS sample, OBS-exe 0x6F3D40]. JoyPro leaves it untouched.
- Deadzone: fixed 7 % DirectInput deadzone (`DIPROP_DEADZONE` 700 on X, Y, Z, RX, RY, RZ; none on the sliders,
  0x6da49c re-read), not configurable; per-action curves from config. `controller`, `sensitivity` ("Low/Medium/High" = `curveLow/Medium/High`), `customScheme`, `class ControllerSchemes` belong to the XInput scheme system. [OBS config `ControllerSchemes`, OBS-exe]

JoyPro (axis model `arma2`, `engine\AxisModels\Arma2.cs`, games Arma2 and Arma2OA): parameter `sensitivity` = the
slider p as the dialog shows it (right = more sensitive); preview v^(10^-p) after the 7 % dead zone (assumes a main axis; DirectInput's rescaling is INF). Scopes
`game:Arma2` / `game:Arma2OA` or `family:arma2` (both games); no fit of the base curve (only values set for the games or
imported are written). Export writes 10^p to `[a]` and `[a+8]` of the device's array (one curve per physical axis, the
first relation by name wins with a warning); a missing array is created after `isXInput` with 1 for the other axes, only
when p != 0. Import reads p = log10(`[a]`) into the bind's game tuning. The old shared scope `family:arma` (Arma 2, OA
and Arma 3 together, generic model) is still read after the new scopes and only carries invert.
Which slider direction the UI calls "more sensitive" is still open (the in-game check below records it).

Device switch (`engine ffb set --device "<name> {GUID}" --game Arma2|Arma2OA --param enabled=0|1`): mode `"Custom"`
or `"Disable"`. A device in mode Disable or Scheme that gets binds is switched to Custom by the export (with a step
message) instead of being refused, unless `enabled=0` is set.

In-game check (prepared, not run): `tools\e2e\games\Arma2Settings.ps1 -Game arma2oa|arma2` (test profiles
`JoyProE2E_OA` / `JoyProE2E_A2`, main menu > Options > Controls only, ArmA2OA.exe / arma2.exe without BattlEye).

## 7. What a writer must do / keep
1. Edit only while the game is closed; refuse if `arma2`/`arma2oa`/`arma2oa_be` runs. Abort on a raP file (`00 72 61 50`).
2. Rewrite only lines `^\s*key<CatalogAction>\[\]=\{...\};$` of cataloged actions; keep every other byte (CRLF, leading TAB, order, floats like `0.55000001`). If a cataloged key line is missing, append it after the last key line (a missing line means "game default", so writing it fixes the binds).
3. Keep keyboard, mouse, TrackIR, XInput codes and combos unless keyboard export is requested. Joystick codes = mod byte 0, type byte 0x02/0x03/0x04. Only cataloged lines change; helicopter and plane actions count as different vehicles (the same code on both is no conflict), weapons/common/view entries as both. `add`: a bind whose code another action of the same vehicle already has is skipped (listed as a skipped multi-bind); nothing is removed. `merge`: adds the profile's codes and drops a code of another action of the same vehicle that the profile puts elsewhere (conflict); the action's old codes stay. `merge-overwrite`: merge + an action the profile binds loses its other joystick codes of connected joysticks (no XInput). Clean mode: also drops the codes of the exported offsets in cataloged lines (this also removes the game's auto-added defaults); nuke: those of other offsets of connected joysticks (a joystick that is not connected keeps its codes). Codes with unknown offsets: leave. No ownership file any more.
4. Never add `JoysticksList` entries or change `joystickSensitivity[]`, `ControllerSchemes`, mouse/TrackIR settings, difficulty, Identity. Device not in the list → skip with "start Arma 2 / OA once with the device connected (Options > Controls > Controllers)". Inside an existing entry only `sensitivity[]` (see 6) and `mode` (a device that gets binds is set to `"Custom"`) are changed.
5. Reject: button index ≥ 32, POV other than the first hat, axis index ≥ 8, joystick combos/modifiers, keyboard combos with a non-keyboard modifier.
6. Keyboard export: DIK, `0x100 + DIK` double tap, `(mod << 24) + DIK` combos (write signed).

## 8. Differences to Arma 3

| | Arma 3 | Arma 2 / OA |
|---|---|---|
| Folder | `Documents\Arma 3`, `Arma 3 - Other Profiles` | `Documents\ArmA 2`, `ArmA 2 Other Profiles` (shared by A2 + OA) |
| File | `.Arma3Profile` | `.ArmA2Profile` (arma2.exe), `.ArmA2OAProfile` (OA/CO/DayZ) |
| Device fields | guid, name, offset, isXInput, sensitivity[16], deadZone[8], steepness[8], mode | guid, name, offset, isXInput, sensitivity[16] only after Customize, mode |
| Disabled mode string | "Disabled" (per arma3.md) | `"Disable"`; also `"Scheme"` |
| Global joystick setting | - | `joystickSensitivity[16]` |
| Combos | `0x1D130011` style, joystick main possible | only `(mod<<24)+DIK` and `(mod<<24)|0x110000|mouse`; no joystick combos |
| Buttons | up to 256 | 32 |
| Full axis on one action | yes (T65252) | OA 1.62+: yes, order = direction; A2 1.11: no |
| Pitch / roll actions | heli: HeliCyclicForward/Back/Left/Right; plane: HeliForward/Back, AirBankLeft/Right | HeliForward/Back and HeliCyclicLeft/Right for both |
| Collective | HeliCollectiveRaiseCont / LowerCont halves | HeliThrottlePos absolute (or HeliUp/HeliDown rate) |
| Missing | - | trims, wheel brake, AirPlaneBrake, vtol, sling, EngineToggle; CM only in OA |
| Deadzone | per device deadZone[] | fixed 7 % |

Same as Arma 3: key line syntax, device types and offsets (`0x20000/0x30000/0x40000 + offset + index`), axis order and half encoding, POV 8 directions, instance GUID format, new devices get `max offset + 256`.

## 9. Open points that need one real start ([UNVERIFIED] until then)
- Exact first-start file (default profile name = Windows user name?, encoding of non-ASCII names, whether `sensitivity[]` really stays absent).
- That the bind dialog stores a full axis as the two half codes in the order they were moved, and that the plane and the helicopter get full range forward = max.
- That a stick button cannot be combined with Ctrl in the bind dialog.
- Whether the profile is written on leaving the Controls dialog or only on exit.
- A2 1.11: helicopter collective with HeliThrottlePos on one half (rear half dead) and the Axis.ThrottleBrake variant.

Exact one-time test for the user (OA; A2 optional, same steps):
1. Connect the HOTAS/pedals, start "Arma 2: Operation Arrowhead" from Steam (no mods), let it create the profile, go to Options > Controls > Controllers: check every device is listed, press Enable where it says disabled.
2. Options > Controls, show "Aircraft controls": select "Thrust (Analogue)", push the throttle fully forward, then fully back (two entries must appear); OK.
3. Select "Gear down", hold Left Ctrl and press stick button 1 (note whether it is accepted); OK.
4. Controllers > select the stick > Customize > move one slider > OK.
5. Exit the game via the main menu, then hand `Documents\ArmA 2\<name>.ArmA2OAProfile` over for analysis (and the `.ArmA2Profile` if A2 was started too).
