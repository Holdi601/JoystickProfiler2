# Falcon BMS 4.38, Falcon 4.0 / Allied Force, Tiny Combat Arena

Tags: **[OBS]** seen in real files on a test PC · **[OBS-bin]** seen in the installed binaries (BMS exe/PDB type info, the launcher 2.4.2.6 assemblies), looked at for interoperability only, nothing modified · **[SRC]** official docs/source · **[INF]** inferred. Research 2026-09-30; BMS had never been run on a test PC, so the generated files below are known from the binaries, not from user files.

## JoyPro integration (`engine bms`, experimental)
- Config path = `<BMS>\User\Config` (found via `HKLM\SOFTWARE\WOW6432Node\Benchmark Sims\*\baseDir` and `X:\Falcon BMS*`, newest first). JoyPro edits only the launcher's `Setup.v100.* {GUID}.xml` files; the launcher builds key file, axismapping.dat, joystick.cal and DeviceSorting.txt from them on its next Launch. The legacy key-file workflow is not written.
- Devices are matched by the DirectInput instance GUID in the file name. The launcher writes a device file only after something of that device is assigned in it, so for a connected device of the profile without a file JoyPro creates `Setup.v100.<instance name> {GUID}.xml`: from the launcher's stock file `Launcher\Stock\Setup.v100.<product> {Stock}.xml` when one exists for that model, else empty in the launcher's layout (8 axes, 4 hats x 8 directions x 2 callbacks, 128 buttons x 4 slots). A device that is neither connected nor in a file is skipped; files of devices that are not connected are read with the name from the file.
- Catalog `DB\FalconBMS\actions.tsv` (`engine bms refresh-db [--path <User\Config or key file>]`): visible callbacks (display flag `1`) of `BMS - Full.key` sections 2.x-5.x, 6.01-6.05, 6.09, plus the launcher's axis functions without FOV and Camera_Distance.
- Buttons: press slot (0); the shift layer is the press+shift slot (1) and becomes a JoyPro modifier `BMS Shift` on the button that holds `SimHotasPinkyShift` (from the export's binds first, else from the files). A modifier on any other button is skipped. Release slots are never imported; clean mode releases cataloged callbacks in them. A new callback gets `Invoke` = Default.
- Hats: `<pov>` string 0 (normal) and 1 (shifted). Top-level lists and the `profileDefaultF16` copy are written alike.
- Axes: `AxisName` on the physical axis, `AssgnDate` = now for a new claim, older claims of the same function are released (unset date `1998-12-12T12:00:00`). Invert, dead zone and saturation are the launcher's steps from the relation's `bms` tuning (see "Every axis, control and force feedback setting" below). Merge mode never moves a function the user assigned elsewhere.
- Ownership: `User\Config\JoyPro.joypro.json`. Files keep declaration, whitespace, CRLF and BOM state; only changed files are written (`.jpbak` backup first, read-only attribute restored).
- Refused while `Falcon BMS` or `FalconBMS_Alternative_Launcher` runs (`Launcher.exe` is a generic process name and not checked).
- DB update check: `Falcon BMS.exe` product version + hash of `BMS - Full.key`; new callbacks are named.
- Tested with synthetic device files in the stock layout (smoke test) and the installed 4.38.1 key file. Not yet tested: a launcher-generated file of a real device and a real Launch after an export.

### Every axis, control and force feedback setting (axis model `bms`, settings model `FalconBMS`)
Research notes: private. Only files the launcher keeps on Launch are written: the device XMLs, `Falcon BMS User.cfg` **above** the launcher's marker line, `Feedback.ini` and byte 388 of the pilot file. `axismapping.dat`, `joystick.cal`, `DeviceSorting.txt` and the override block of User.cfg are rebuilt by every Launch and never written. All writes: refused while BMS or its launcher runs, `.jpbak` + versioned backup (`GameExportSafety`), read-only attribute restored, only changed files.

| Setting | Where JoyPro sets it | File | Evidence |
|---|---|---|---|
| Invert | relation (base or `game:FalconBMS`) | device XML `<Invert>` | launcher `AxAssgn` |
| Dead zone, launcher step 0-3 | `bms` param `deadzoneStep` | device XML `<Deadzone>` None/Small/Medium/Large → axismapping.dat 0/100/500/1000 | launcher `GetAxDeadZoneByte` |
| Saturation, launcher step 0-3 | `bms` param `saturationStep` | device XML `<Saturation>` → axismapping.dat -1/9500/9000/8500 | launcher `GetAxSaturationByte` |
| One-sided axis | `slider` (set on import for the functions below) | no `<Deadzone>`: always None | launcher hides the dropdown (`AxisAssignWindow`) |
| Exponential curve 0-100 (pitch, roll, yaw, cursor X/Y) | `bms` param `axisExp` | User.cfg `set g_nAxisExp_AXIS_PITCH n` (also `_ROLL`, `_YAW`, `_CURSOR_X`, `_CURSOR_Y`) | TM 4.38.1 p.32-33 |
| Exponential saturation 0-99 (same axes) | `bms` param `axisSat` | User.cfg `set g_nAxisSat_AXIS_* n` | TM p.32-33 |
| Anti-jitter deadband 0-0.20 (default 0.05) | `ffb set --device * --game FalconBMS --param flightControlsDeadband=` | User.cfg `g_fFlightControlsDeadband` | TM p.25 |
| Force feedback dead band (default 100) | `ffbDeadBand` | User.cfg `g_nForceFeedbackDeadBand` | TM p.25 |
| Analog idle cutoff | `analogIdleCutoff` | User.cfg `g_bUseAnalogIdleCutoff` | TM p.16 |
| Click at the AB detent | `abDetentClick` | User.cfg `g_bAfterburnerDetentClick` | TM p.25 |
| Virtual rudder / Depr-Ret as brake | `virtualRudder`, `deprRetAsBrake` | User.cfg `g_bUseVirtualRudder`, `g_bUseDeprRetAsBrake` | TM p.16, p.30-31 |
| Enable Force Feedback | `ffbEnabled` | `<callsign>.pop` byte 388 (468-byte file only) | PDB `PlayerOptionsClass::enableFFB` at 388; neighbours 384/389/390/410 are exactly the bytes the launcher's `SavePop` writes, which keeps 388 |
| Effect gains (DirectInput 0..10000) | `gunGain` (Effect0), `bounceGain` (1), `autoCenterGain` (2), `runwayGain` (3+4), `bombDropGain` (5+6), `stallGain` (7+8) | `Feedback.ini` `[EffectN] gain=` | shipped file comments |
| Auto-centre spring coefficient | `autoCenterStrength` | `Feedback.ini` `[Effect2] conditionPositive/NegativeCoefficient` | shipped file |
| Throttle AB / IDLE detent (% of travel from idle) | per device: `ffb set --device "<throttle>" --game FalconBMS --param abDetent=80 --param idleDetent=5` | device XML `<detentPosition><AB>/<IDLE>` (0..65536, first child of `<JoyAssgn>`) → joystick.cal | launcher `DetentPosition`, `SaveJoystickCal`, stock XMLs |

- One-sided functions (no dead zone): Throttle, Throttle_Right, Toe_Brake(_Right), HUD/HMS/FLIR brightness, Reticle_Depression, the volumes (COMM 1/2, MSL, Threat, Intercom, AI vs IVC), FOV, Camera_Distance, the HSI/altimeter knobs. A stored dead zone there (old setups) acts around the middle of the travel; the export resets it to None with a warning.
- User.cfg: an existing `set` line above the marker is changed in place (all copies); a new one goes right before the marker as `set g_x v // JoyPro`, in the launcher's normal form (it collapses spaces and drops lines containing `// LAUNCHER OVERRIDE` / `// SETUP OVERRIDE`). The launcher itself only writes g_nButtonsPerDevice, g_nHotasPinkyShiftMagnitude, g_nNumOfPOVs/g_nPOV*, g_bExportRTTTextures and the VR cfg's lines into its block, none of JoyPro's keys. CRLF, UTF-8 without BOM (BOM kept if present). A curve value of 0 adds no line. Merge mode keeps a curve value the user changed in User.cfg (warning); the game-wide settings are always written.
- The curve lines are written only for values a tuning sets (import or `binds tuning set`); a tuning inherited from the base (fit) sets the launcher steps (nearest to the base dead zone / saturation; a slider's saturation is converted from whole to half travel) but **no** exponent: the forum calls BMS's curve "very aggressive", so DCS curvature is not converted until the formula is measured.
- Import: steps, invert and one-sided flag from the device XML, `axisExp`/`axisSat` from User.cfg (lines above the marker) of the five curve axes; stored as the relation's `game:FalconBMS` tuning (not the base DCS uses). Game-wide settings are not imported (as for the other games).
- An export without BMS binds still writes the game settings and detents (`IGameSettingsExport`).
- Preview (`binds tuning preview --game FalconBMS`): u = |x| (after invert); 0 inside the dead zone (int/10000 of the half travel), 1 from the saturation (int/10000; None = 1), linear between, as DirectInput applies `DIPROP_DEADZONE`/`DIPROP_SATURATION` (also on one-sided axes, around the centre of the physical travel); then `axisSat`: v = min(1, v / (1 - s/100)) ("percentage to reduce the physical stick travel required to reach 100 %"), then `axisExp`: v = (1 - e) v + e v³ with e = exp/100 ("blend from fully linear (1) to a cubic curve (99)"), both quoted from BMS forum moderator airtex2019. **UNVERIFIED**: whether BMS reads 9500 as full output at 95 % (file ints, DirectInput meaning) or 99 % (User Manual / launcher preview "1 %"); the exp formula (the same moderator also called it "very aggressive, 2-3 recommended", which a linear-cubic blend is not); the order of DirectInput and cfg shaping; what `g_fFlightControlsDeadband` does (not in the preview).
- Not implemented (open): Enable Custom Axis Shaping (pop byte 409) with `axiscurves.cal` (header fields `dwObj`/`dwHow`, axis order and how BMS applies the points are unverified; no BMS-written sample exists); raw non-step dead zone ints (only possible with "Launch without any Setup override"); the in-sim step sizes `g_nDeadzone*`/`g_nSaturation*`; Roll-linked NWS (the launcher rewrites pop byte 410 from its own checkbox on every Launch); undocumented cfg values (`g_nFFEffectAutoCenter`, `g_bFFCenterFix`, `g_nThrottleMode`, smoothing).
- In game (prepared, not run): `tools\e2e\games\BmsSettings.ps1` exports random steps/invert for the three trim axes, a pitch curve, detents and FFB settings, presses Launch through UI Automation, checks what the launcher generated (step ints in axismapping.dat, detents in joystick.cal, JoyPro's User.cfg lines still above the block, pop byte 388) and then sweeps vJoy: the trims through shared memory (`FalconSharedMemoryArea` FlightData.TrimPitch/Roll/Yaw, offset counted from `Tools\SharedMem\FlightData.h`; BMS exports no stick position, so pitch's exponent is captured from the `g_bDisplayAxisValues` overlay by screenshot/OCR for a manual fit). Snapshot/restore of User\Config, theater.lst, the test pilot and the launcher's side folders.
### Telemetry recording (`gui/JoyPro.Ffb.Core/Telemetry/Bms/BmsTelemetry.cs`)
- Source: BMS's shared memory, its official export interface for cockpit builders and tools (`Tools\SharedMem\FlightData.h`), not the game's process memory. `FalconSharedMemoryArea` (FlightData, v118), `FalconSharedMemoryArea2` (FlightData2, v23) and `FalconSharedMemoryAreaString` (StringData) are opened with read rights only; nothing is ever written. Until BMS has created them the feed retries every 2 s ("BMS has not opened its shared memory (it does so when it starts)"); then it polls at 20 Hz on a timer, copies both structs into reused buffers and publishes a frame only when they changed. The string area is re-read only when FlightData2.StringAreaTime changes.
- Session = in 3D: FlightData.hsiBits `Flying` (0x80000000). Entering 3D raises session start, leaving it (or the feed stopping) session end; in the UI no frames are published.
- Keys `bms.*` in the game's own units (ft, ft/s, kt, rad, lbs, %), grouped by category: position/velocity, attitude, air data, engine (and engine 2), fuel, gear and surfaces, lights (the bit words plus every named bit as 0/1), instruments (HSI/ADI/altimeter/RALT), trim, countermeasures/ECM, RWR (count + per active contact symbol/bearing/activity/launch/selected/lethality/new/jamming), radio/navigation/IFF, electrics/hydraulics, head offsets, RTT export rectangles, session (versions, ACD index, time of day, MP pilot states) and derived `bms.calc.*` (true heading 0..360, ground track, ground speed, altitude, vertical speed ft/min, total fuel, AGL while RALT is valid, bullseye bearing/range). FlightData2 members newer than the running BMS's FLIGHTDATA2_VERSION are left out.
- Events: `bms.ded` / `bms.pfl` (5 lines joined by `\n`, at most 4 per second), `bms.pilots` (MP list, `callsign|state` per line), objects snapshot with the bullseye and the steerpoints/markpoints/pre-planned threats of the string area (lat/lon linearised around the ownship, u/v exact in sim feet converted to m). Session facts: aircraft (StringData AcName, else "ACD n"), map (ThrName), pilot (the callsign when the pilot list holds one name), `bms.flight` (flight callsign from VoiceHelpers), game version.
- A test re-derives every offset from the installed `FlightData.h` (skipped without BMS), so a BMS update that moves a member fails the build's tests.
- Not available in shared memory: other aircraft, ground units and weapons (only RWR bearings, no positions), throttle/stick positions, flaps as 0..1.

- Smoke test: `engine\tests\smoke\bms.ps1` with fixtures from the stock files (`fixtures\bms\settings`: stock User.cfg + a launcher block, stock Feedback.ini, the launcher's new-pilot .pop).

## Falcon BMS 4.38.1 (`C:\Falcon BMS 4.38`)

### Who owns which file
- 4.38 ships the "Alternative Launcher" (`Launcher\FalconBMS_Alternative_Launcher.exe`, .NET WPF). `Hotas\README.TXT`: "PLEASE USE THE NEW LAUNCHER EXPERIENCE TO DEFINE KEYBOARD AND HOTAS MAPPINGS." [OBS]
- **Launcher-managed (default):** the truth is one `User\Config\Setup.v100.<SanitizedProductName> {<INSTANCE-GUID>}.xml` per device plus the keyboard lines of `BMS - Auto.key`. On every Launch the launcher regenerates `axismapping.dat`, `joystick.cal`, `DeviceSorting.txt`, the override block of `Falcon BMS User.cfg`, `BMS - Auto.key` / `BMS - Auto-F15ABCD.key` and the key-file name in `<callsign>.pop` (unless "Launch without any Setup override" is ticked). [OBS-bin]
- **Legacy:** hand-edited key files / in-sim Setup (writes `axismapping.dat` + `joystick.cal` on OK; saving key files there drops all comments). [SRC TM 10.1.1, 10.7.5]

### Files (`<BMS>\User\Config\`)
| File | Format | Role |
|---|---|---|
| `*.key` | text | callbacks → keyboard keys, DX buttons, POV hats |
| `<name>-F15ABCD.key` | text | separate profile for the F-15 |
| `DeviceSorting.txt` | text | DirectInput device order = device index |
| `axismapping.dat` | binary, 504 B | device/axis per in-game axis, deadzone, saturation |
| `joystick.cal` | binary, 720 B | per-axis used/reversed, throttle detents |
| `<Callsign>.pop` | binary, 468 B | pilot options incl. active key file (`char[32]` at +336) |
| `Falcon BMS.cfg` / `Falcon BMS User.cfg` | `set g_x value` | User.cfg overrides; launcher appends its override block |
| `Setup.v100.* {GUID}.xml` | XML (XmlSerializer, UTF-8 no BOM) | launcher's per-device store |
| `DeviceSuppress.txt` | `{GUID}` per line | devices the launcher ignores |

### Key-file grammar [OBS stock files; OBS-bin launcher regexes; SRC TM 10.5]
Stock files: ASCII, CRLF, no BOM (archive files contain CP1252 bytes; launcher writes Auto.key as UTF-8 LF). Read as Latin-1, keep line terminators. Classify: comment `^\s*#`; DX/POV line when the 4th token is `-2` (button) / `-3` (POV); else keyboard line.

- **Keyboard (9 fields):** `AFGearToggle 119 0 0x22 0 0 0 1 "GEAR: LG Handle - Toggle"` = callback, soundId (-1 none), 0, scancode hex (`0XFFFFFFFF` unbound; `0X`/`0x` mixed), modifiers (Shift 1, Ctrl 2, Alt 4), combo key, combo modifiers, display flag (`1` visible, `-1` headline, `-0` locked — keep the text, not an int, `-2` hidden), `"description"`.
- **DX button:** `SimTriggerFirstDetent 0 -1 -2 0 0x0 -1` = callback, buttonId, invoke (-1 default, -2 down, -4 up, 8 UI), `-2`, pressRelease (0 press, 0x42 release), `0x0`, [soundId], ["desc"].
- **POV:** `AFElevatorTrimUp 0 -1 -3 0 0x0 0` = callback, pov (0/1 = hats 1/2 unshifted, 2/3 shifted), -1, `-3`, direction 0-7 clockwise from up, `0x0`, [soundId]. Unmapped hat directions pan the view; map `SimDoNothing` to block.
- Same DX id twice → only the last line fires. Shift layer: `SimHotasPinkyShift` on id and id+offset; one shift layer for all devices.

### Numbering
- Button id = `deviceIndex * g_nButtonsPerDevice + (button - 1)`; deviceIndex = 0-based line in DeviceSorting.txt (keyboard not counted); default 32 per device, shifted = id + `g_nHotasPinkyShiftMagnitude` (stock 256). The launcher writes `g_nButtonsPerDevice 128` and shift magnitude `N*128` (N devices). [SRC TM 10.8.4; OBS-bin]
- POV devices: `g_nNumOfPOVs`, `g_nPOV1DeviceID`, `g_nPOV1ID`, `g_nPOV2DeviceID`, `g_nPOV2ID`; device ID = DeviceSorting position + 2 (0 mouse, 1 keyboard). [SRC TM 10.8.3; OBS-bin enum device_t]

### DeviceSorting.txt
`{PRODUCT-GUID} "Sanitized Product Name"` per line, e.g. `{0402044F-0000-0000-0000-504944564944} "Joystick - HOTAS Warthog"` (PIDVID product GUID, not the instance GUID). The launcher rewrites it in DirectInput enumeration order minus DeviceSuppress. Name sanitizer: `[^A-Za-z0-9\~\`\[\]\{\}\-_\=\'\x20]` removed.

### axismapping.dat (504 B, little-endian) [OBS-bin PDB]
```
+0  int32 FlightControlDevice (device_t of the Pitch device; 1 = keyboard when unassigned)
+4  GUID  FlightControllerGUID (instance GUID, Guid.ToByteArray layout)
+20 int32 totalDeviceCount
+24 DeviceAxis[30] x 16 B: { int32 Device (-1 none); int32 Axis (0 X,1 Y,2 Z,3 RX,4 RY,5 RZ,6 SLIDER0,7 SLIDER1); int32 Deadzone; int32 Saturation }
order: Pitch, Bank, Yaw, Throttle, Throttle2, BrakeLeft, BrakeRight, FOV, PitchTrim, YawTrim, BankTrim, AntElev, RngKnob, CursorX, CursorY,
       Comm1Vol, Comm2Vol, MSLVol, ThreatVol, InterComVol, AIvsIVCVol, HudBrt, HudFlirBrt, HmsBrt, RetDepr, Zoom, HsiCourse, HsiHeading, Altimeter, IlsVol
```
Deadzone None/Small/Medium/Large = 0/100/500/1000; saturation = -1/9500/9000/8500; unassigned record `FF FF FF FF FF FF FF FF 64 00 00 00 FF FF FF FF`. Invert lives in joystick.cal. Wrong sizes are rejected by BMS.

### joystick.cal (30 × 24 B) [OBS-bin]
`{ int32 center; int32 cutoff; int32 ioVal; int32 smoothingFactor; float engrValue; bool isUsed; bool isReversed; pad[2] }` in `GameAxis_t` order (Pitch, Roll, Yaw, Throttle, Throttle2, TrimPitch, TrimYaw, TrimRoll, BrakeLeft, BrakeRight, FOV, AntElev, CursorX, CursorY, RangeKnob, CommVol1, CommVol2, MSLVol, ThreatVol, HUDBrightness, RetDepr, Zoom, IntercomVol, HMSBrt, AIvsIVCVol, HUDFlirBrightness, HSICourse, HSIHeading, Altimeter, ILSVol) — not the axismapping order. Launcher default: center 0, cutoff 15000.

### Launcher XML `Setup.v100.<name> {<INSTANCE-GUID-UPPER>}.xml` [OBS stock; OBS-bin]
Root `<JoyAssgn>`; children in order: `<detentPosition><AB/><IDLE/>` (0..65536), `<axis>` 8 × `<AxAssgn>` for X,Y,Z,Rx,Ry,Rz,Slider0,Slider1 (`<AxisName>` Roll/Pitch/Yaw/Throttle/Throttle_Right/Toe_Brake/Toe_Brake_Right/Trim_*/…, `<AssgnDate>` — newest claim wins, `<Invert>`, `<Saturation>`/`<Deadzone>` None|Small|Medium|Large), `<pov>` 4 × 8 directions × (unshifted, shifted) callbacks, `<dx>` 32 or 128 × 4 slots (press, press+shift, release, release+shift; empty = `SimDoNothing`; `<Invoke>` Default|Down|Up|UI). 2.4.x adds `<profileDefaultF16>` (wins over top-level dx/pov when it has 128 entries) and `<profileF15ABCD>`.

### Active key file, version
- Active key file: `.pop` `char[32]` at offset 336 (callsign from `HKLM\SOFTWARE\WOW6432Node\Benchmark Sims\Falcon BMS 4.38\PilotCallsign`). Chat `.joy` reloads key/button/hat maps in 3D.
- Version: registry `...\Falcon BMS 4.38` (`baseDir`, `curUpdate`, `curPatch`), `Bin\x64\Falcon BMS.exe` FileVersion, `Falcon BMS.cfg` line 2.

### Flight vs. UI callbacks
Section headers of `BMS - Full.key` (`SimDoNothing ... -1 "======== 2.04 MANUAL TRIM PANEL ========"`): flight/HOTAS = 2.04, 2.19, 5.11, 6.03, 6.09 (plus cockpit panels 2.01-5.10, 6.01, 6.02, 6.04, 6.05 optionally); never touch 1.x UI, 6.06-6.08, 7.x views, 8.x radio comms. Full list: `Docs\01 Input Devices\02 Key File Editor\Complete Callback List.txt`.

### Exporter recommendation
1. Refuse while `Falcon BMS`, `FalconBMS_Alternative_Launcher` or `Launcher` runs; back up; restore read-only attributes.
2. Launcher-managed (any `Setup.v100.*.xml`, or the pop's key file is `BMS - Auto`): edit only the device XMLs (`<dx>`/`<pov>` slots, the `profileDefaultF16` copy, `<axis>` entries with a newer `AssgnDate`); never the generated files.
3. Legacy: edit only DX/POV lines of flight callbacks (e.g. a `# ==== JoyPro BEGIN/END ====` block) and the affected records of `axismapping.dat` / `joystick.cal` after checking sizes 504/720.

## Falcon 4.0 (Steam) and Allied Force
Not integrated: small user base, an older key-file variant without a launcher store, and an export would have to rewrite key-file DX lines by device index. Use BMS.

- Falcon 4.0: `config\keystrokes.key`, `config\laptop.key`; keyboard lines use the same 9 fields; DX lines are an older variant (`SimTrigger 0 -1 -2 0 0 0 "Fire gun"`, field 3 = 2D-cockpit id, decimal 0, no POV lines); `config\Viper.pop` (212 B, key file name at 0xA0); no axismapping/DeviceSorting (primary stick + joystick.cal). [OBS]
- Allied Force (not installed): same `.key` family plus `axismapping.dat` + `joystick.cal`; struct sizes may differ from BMS. [SRC TM 10.5, forum]

## Tiny Combat Arena
Integrated since 2026-10-11 (`engine tca`): see [tiny-combat-arena.md](tiny-combat-arena.md). The notes below are the
research before the install.

- Unity game by Why485/MicroProse; logs in `%USERPROFILE%\AppData\LocalLow\TinyCombat\Arena\`; input via Rewired (DirectInput/RawInput/XInput, rebinding screen for HOTAS axes). [SRC Steam discussions, itch.io devlog]
- Where bindings are stored is undocumented. [INF] With Rewired's stock `UserDataStore_PlayerPrefs` they would be XML controller maps in `HKCU\Software\TinyCombat\Arena` (REG_BINARY UTF-8, keys like `UserRemapping_v3&...&controllerMapType=JoystickMap&hardwareIdentifier=...`). Needs one real binding on an install to verify before JoyPro writes anything.
