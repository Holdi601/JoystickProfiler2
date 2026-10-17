# WARDOGS

Unreal Engine 5 game by Bulkhead. Joysticks are handled by the game's own "WD Joystick" plugin: "Per-device joystick/HOTAS input via DirectInput8. Creates unique FKey names per physical device" (from its `.uplugin`). The whole plugin is flight-only (`EWDFlightAxis`, `EWDFlightAction`).

## Files
- Joystick bindings: `%LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini`, section `[/Script/WDJoystick.WDJoystickSettings]`.
- HOTAS master switch: `bUseHOTASHelicopters` in `[/Script/WDGame.WDUserSettings]` of the same file = the in-game "Enable HOTAS" toggle (verified in game 2026-10-04). With it off the HOTAS menu lists no binds and the game ignores them, so JoyPro's export turns it on when it writes binds.
- Keyboard bindings: `[/Script/BHEnhancedInput.BHEnhancedInputPlayerBinds]` and `Saved\SaveGames\EnhancedInputUserSettings.sav` (GVAS). Not needed for joysticks.
- JoyPro does not read the install (exe and paks are packed and encrypted); the user's ini files are the source.

File: ASCII, CRLF, no BOM, first line `;METADATA=(Diff=true, UseCommands=true)`. The game rewrites the whole file on save/exit, so edit only while `WardogsClient-Win64-Shipping.exe` is not running.

## Section grammar
```
<Axis>=(DeviceIdentifier="<id>",_aaaajcpgpllhacnlfdnkg=<axis>,bInvert=<True|False>,_aaaalddpkcdlnfnaagdkl=<useButtons>,
        Positive=<Btn>,Negative=<Btn>,_aaaaoabiaaaacohedinbn=<float>,_aaabgcjobaejlnkbppedn=<bool>,
        _aaaaombggppmmijkohdph=<Src>,_aaaaochdddlogkjceogmo=<Src>,Sensitivity=<float>,DeadZone=<float>)
<Btn> = (DeviceIdentifier="<id>",_aaaaljahlggohoapfcfef=<button>,_aaaaidddockpolojoodpb=<hat>,_aaaamdcclmobefllhjkdm=<Dir>)
<Src> = (DeviceIdentifier="<id>",_aaaajcpgpllhacnlfdnkg=<axis>,_aaaajlbldnobdplelbhhg=<float>,_aaaakbblnlfadfmofnkml=<bool>,
         _aaaaljahlggohoapfcfef=<button>,_aaaaidddockpolojoodpb=<hat>,_aaaamdcclmobefllhjkdm=<Dir>)
ActionBindings=(Action=<EWDFlightAction>,Binding=(DeviceIdentifier="<id>",_aaaaljahlggohoapfcfef=<button>))
```
- Axis lines, always all six, in this order: Pitch, Throttle, Roll, Yaw, LookPitch, LookYaw. Throttle is the helicopter collective.
- `ActionBindings=` repeats once per bound action (array elements, no `+` prefix). Array elements omit fields equal to the defaults (button -1, hat -1, direction Up).
- Floats are written with 6 decimals.

| Field | Meaning | Confidence |
|---|---|---|
| `DeviceIdentifier` | `"VID:PID:<DirectInput instance name>"`, `""` = unbound | high (in game 2026-10-04) |
| field after `DeviceIdentifier` in an axis (`_aaaajcpgpllhacnlfdnkg`) | DIJOYSTATE2 slot: 0 X, 1 Y, 2 Z, 3 Rx, 4 Ry, 5 Rz, 6/7 slider 0/1; -1 none | high |
| `bInvert` | invert axis | high |
| field after `bInvert` (`_aaaalddpkcdlnfnaagdkl`) | drive the axis from the Positive/Negative buttons | high |
| `_aaaaljahlggohoapfcfef` | 0-based button index, -1 none (the game stored vJoy button 16 as 15). The menu shows action buttons 0-based but axis-half buttons 1-based | high |
| `_aaaaidddockpolojoodpb` | 0-based POV index, -1 none | medium (never seen set) |
| `_aaaamdcclmobefllhjkdm` | hat direction enum, `Up` = value 0 = placeholder | medium |
| `Sensitivity`, `DeadZone` | the HOTAS page's Sensitivity and Deadzone sliders (defaults 1.0 / 0.05, same units as the menu); managed by JoyPro (`wardogs` axis model) | high |
| `_aaaaoabiaaaacohedinbn`, `_aaabgcjobaejlnkbppedn`, the two `<Src>` structs | unknown; preserve | low |

Defaults of a pristine file: Roll=axis 0, Pitch=1, Throttle=2, Yaw=5, LookPitch/LookYaw=-1.

Actions seen (full enum not recoverable): Fire, Flares, CycleLockState, DeploySupplyCrate, Reload, Horn, TacticalMap, ToggleCameraMode, VoipToggleTalk, Ping.

## Every setting the game offers for axes (Settings > GAMEPAD > HOTAS)
Research notes (menu screenshots of the e2e runs, game-written ini): private. JoyPro manages all of it:

| Menu | File | JoyPro |
|---|---|---|
| Enable HOTAS | `[/Script/WDGame.WDUserSettings]` `bUseHOTASHelicopters` | game-wide setting `enableHotas` (`engine ffb set --device * --game Wardogs --param enableHotas=true`); without it an export with binds still turns it on |
| Self-Centering Collective | `[/Script/WDGame.WDUserSettings]` `bCollectiveSelfCentering` (key matched by name; the in-game check `tools\e2e\games\WardogsSettings.ps1` confirms it) | game-wide setting `selfCenteringCollective` |
| Invert Roll/Pitch/Yaw/Collective (+ Invert Look Yaw/Pitch) | `bInvert` of the axis line | Invert of the relation (`wardogs` model) |
| Roll/Pitch/Yaw/Collective Sensitivity | `Sensitivity=` of the axis line | `wardogs` model `sensitivity`, 0.1..2.0, step 0.01, default 1 |
| Roll/Pitch/Yaw/Collective Deadzone | `DeadZone=` of the axis line | `wardogs` model `deadZone`, 0..0.5, step 0.01, default 0.05 |
| (no row) look axes' Sensitivity/DeadZone | same fields on `LookPitch`/`LookYaw` | written like the others when a look axis is bound; whether the game applies them is unknown |
| bindings, Free Look, Actions | see above | binds; "Garage / Vendor" and "Free Look Reset" are not in the catalog: their `Action=` names were never seen in a file (bind them in the game once, then `engine wardogs refresh-db`) |

There is no curve, saturation, smoothing or force feedback row. The game-wide settings are written in place and only
when the file already has the key; an export of a profile without WARDOGS binds writes only these settings.

### Axis model `wardogs` (`engine\AxisModels\Wardogs.cs`)
- Import: each bound axis line's `Sensitivity`/`DeadZone` (and invert) become the relation's `game:Wardogs` tuning
  (`n.sensitivity`, `n.deadZone`), not the base, so DCS keeps its own values. A re-export writes the file back unchanged.
- Export: both values are always written (a dead zone of 0 too; older versions wrote the dead zone only when > 0 and
  never the sensitivity), rounded to the menu's two decimals and clamped to the ranges above. Curvature and user curves
  cannot be expressed (the export warns).
- Fit from the base (no WARDOGS tuning): dead zone = the base dead zone (both cut and rescale); sensitivity = the slope
  between dead zone and saturation X (`sy (1 - dz) / (sx - dz)`, 1 with no saturation); a slider's dead zone (DCS: at the
  start of the travel) is not carried over because WARDOGS treats the collective as a centred axis (it would become a
  flat spot at mid collective).
- Preview formula: **not known** (the code is packed, the menu has no preview). The only first-party statement is the
  menu text "1.0 = unchanged; lower is gentler, higher is sharper". JoyPro previews the most likely form, unverified:
  invert, `|x| <= dz -> 0`, else `sign(x) min(1, sens (|x| - dz) / (1 - dz))`. Open: rescaled or plain cut dead zone,
  whether sensitivity > 1 is clipped at full deflection, whether the collective's dead zone sits at mid travel.
- Slider ranges 0.1..2.0 and 0..0.5 come from the knob positions in the screenshots (1.00 at 47 % of the track,
  0.05 at 11 %); `WardogsSettings.ps1 -ProbeRange` records the real ends.
## Obfuscated field names
The `_aaaa...` tokens are hashes of the property names: the same logical field has the same token in every struct, and they were stable across the 2026-09-13 and 2026-09-30 builds. If the studio changes the salt, all tokens change at once. So never hardcode them: identify fields by position relative to the readable anchors (`DeviceIdentifier`, `bInvert`, `Positive`, ...) and abort when the shape (field count, value types) changes.

## Device identifier
`%04X:%04X:%s`:
- VID/PID from the DirectInput `guidProduct.Data1` (low word VID, high word PID).
- Name = the **DirectInput instance name** (`tszInstanceName`), checked in game on 2026-10-04: binding a vJoy button in the HOTAS menu wrote `1234:BEAD:vJoy Device` (the HID product string is "vJoy - Virtual Joystick"), and the menu showed `FFFF:2055:Rhino FFB Joystick` (HID name, written by older JoyPro versions) as NOT CONNECTED until it was `FFFF:2055:VPforce Rhino FFB Joystick`. JoyPro's export repairs identifiers whose VID:PID belongs to one connected device but whose name differs.
- Never trim: `231D:0125: VKB-Sim Gunfighter Modern Combat Pro Twist ` has a leading and a trailing space.
- Identical devices collide (4 Wooting interfaces all report `31E3:1231:Wooting Two HE (ARM)`).

## Safe write
- Change only lines inside the section; keep all unknown fields and all ActionBindings of actions JoyPro does not manage.
- One bind per action: the menu shows (and the game uses) one line per action, although it keeps extra lines on save. Replace the ActionBindings line of a managed action (clean / merge-overwrite); merge and add keep a bound action; append new ones after the last ActionBindings line (or after LookYaw); delete the line to unassign.
- Unbind an axis with `DeviceIdentifier=""`.
- If the section is missing, do not synthesize it; ask the user to open the HOTAS menu once.
- Keep names ASCII (a non-ASCII name makes UE save the file as UTF-16).

## Open questions (need an in-game check)
1. Full action list: bind every action in the HOTAS menu once and read the ini.
2. Hat encoding: bind a hat direction as an action and as LookPitch Positive.
3. Meaning of the unknown fields: change one HOTAS option at a time and diff. Sensitivity, DeadZone, invert and Self-Centering Collective are mapped (see above); `_aaaaoabiaaaacohedinbn` (always 1.0), `_aaabgcjobaejlnkbppedn` (always False) and the two `<Src>` structs have no menu row.
5. In-game check of the settings: `tools\e2e\games\WardogsSettings.ps1` (random values, HOTAS page OCR, snapshot/restore; not run yet).
4. ~~Whether `bUseHOTASHelicopters=True` is required~~: yes (see Files).

Sources: thewardogsguide.com/wardogs-controls-hotas, allthings.how WARDOGS HOTAS guide; real files on the dev PC (current file plus three `.bak` versions).
