# Nuclear Option (Shockfront Studios, Steam app 2168680, early access)

**Status:** integrated, experimental: `engine nuclearoption` (aliases `no`, `nuclear`; `engine/Games/NuclearOptionIntegration.cs`) with `paths`, `catalog`, `scan`, `import`, `export` (clean/add/merge/merge-overwrite, `--nuke-untouched-devices`, `--dry-run`) and `refresh-db`. Built against the installed game 0.34.2 (Unity 2022.3.62f2, Rewired) on 2026-10-10: its registry key and its `resources.assets` were read on a test PC; the game itself was not started by JoyPro and no export has been checked in game yet. The research below (2026-10-01, from public sources before the game was installed) is kept; the section "JoyPro integration" lists what the install confirmed and replaces the old blockers.

Tags: **[OBS]** seen on a test PC (installed game 0.34.2, its registry key, binds made in the game by the user) · **[SRC]** public source (URL in the line or in "Sources" at the end) · **[INF]** inferred · **[UNVERIFIED]** needs one check on a real install. Research 2026-10-01; current game version 0.34.2 (2026-08-13) [SRC S17].

## JoyPro integration (2026-10-10)
What the installed game showed [OBS unless marked]:
- **Storage**: `HKCU\Software\Shockfront\NuclearOption`, process `NuclearOption`, install `<SteamLibrary>\steamapps\common\Nuclear Option` (marker `NuclearOption.exe`, Steam app 2168680, in `engine games installs`). Value names end in `_h<hash>` (Unity's djb2-xor over the UTF-8 key, decimal); Rewired values are REG_BINARY = UTF-8 + one 0x00. Example (the vJoy map): `RewiredSaveData|playerName=Player0|dataType=ControllerMap|kv=2|controllerMapType=JoystickMap|categoryId=0|layoutId=0|hardwareGuid=00000000-0000-0000-0000-000000000000|hardwareIdentifier=WindowsRawInputvJoy-VirtualJoystickBEAD1234|duplicate=0_h719594119`, next to it `…dataType=ControllerMap_KnownActionIds…` (comma list of all 61 action ids) and `RewiredSaveData|dataType=CalibrationMap|controllerType=Joystick|hardwareIdentifier=…|hardwareGuid=…`. 10 of the value names are 255+ characters long; `reg export` silently stops at the first of them.
- **Devices**: none of the user's devices is recognized by Rewired (`hardwareGuid` all zero). `hardwareIdentifier` = `WindowsRawInput` + HID product string with all whitespace removed + PID + VID (4 hex digits each, upper case), checked on 8 devices (vJoy, VKB Gunfighter, VPC MT-50 CM throttle, VPC WarBRD stick, VPforce Rhino, WinWing take-off panel, MFG Crosswind, Wooting Two HE). The map's `hardwareName` attribute is the HID product string **untrimmed** (VKB: `" VKB-Sim Gunfighter Modern Combat Pro Twist "`). `duplicate` = index among devices with the same identifier. The Wooting keyboard shows several HID game-controller interfaces with the same VID/PID: JoyPro skips such ambiguous models (read and write) with a warning.
- **Maps**: joystick maps exist only in map category 0; binds are global (all aircraft). The game's default generic map (`sourceMapId` 2), saved for every device the first time the bindings menu was closed: Pitch Axis 0, Roll Axis 1, Yaw Axis 2, Throttle Axis 3, Brake Axis 4, and Fire, Target Select, Target Cancel, Next Weapon, Next Countermeasure, Gear, Countermeasures, Night Vis, Flight Assist, Radar, Eject on Buttons 1–11. `actionCategoryId` is 0 in the default entries and -1 in an entry the user rebound in the game.
- **Element ids** (unrecognized joystick): axes X 0, Y 1, Z 2, RX 3, RY 4, RZ 5 (vJoy RY bound in the game = element 4; the default map uses 0–4); Button n = 31 + n (Button 1 = 32). Sliders: elements 6 and 7 by Rewired's Unknown Controller definition [INF, not seen in a saved map]; JoyPro writes them with a warning. Hats: element ids not seen → JoyPro skips hat binds with a warning (read and write).
- **Action catalog**: the Rewired InputManager in `NuclearOption_Data\resources.assets` holds the action list (int32 count = 61, then per action int32 id, string name, int32 type 0 axis / 1 button, strings descriptiveName / positiveName / negativeName, 6 int32 whose last is the action category; Unity strings = int32 length + bytes + padding to 4) directly followed by the 7 action categories (strings name, descriptiveName, tag, int32, int32 id, int32): 0 Default, 1 Flight Controls, 2 Camera Controls, 3 Gameplay, 4 Debug, 5 Chat, 6 Mission Editor. `engine nuclearoption refresh-db [--path <game folder|NuclearOption_Data|resources.assets>]` finds the list by its first record ("Pitch", id 0) and writes `DB\NuclearOption\actions.tsv` (id = action name, title = descriptive name, category name, fifth column = Rewired id; axis actions also get `<Action>.Positive` / `.Negative` rows titled with the pole names). The shipped catalog was generated that way from 0.34.2 (95 rows). Brake is an axis action (only "Apply Brakes" as pole name).
- **Import** (`scan`/`import`): every Player0 JoystickMap of category 0 whose identifier matches exactly one connected device; axis elements 0–7 → `JOY_X … JOY_SLIDER2` with the map's `invert`, buttons → `JOY_BTN<n>`, a button on an axis action → `<Action>.Positive` (axisContribution 0) / `.Negative` (1). Half-axis binds, hats and ids not in the catalog (mods) are skipped.
- **Export**: refused while `NuclearOption` runs; per device the existing value is edited in place (only the touched `ActionElementMap`s change, buttons go to `<buttonMaps>`, axes to `<axisMaps>`, everything else stays). A device without a saved map gets a new one (`sourceMapId` -1, only JoyPro's binds, `hardwareName` = HID product string) plus its `KnownActionIds` value (all catalog ids), so the game's default binds of that device are replaced. Modes: a conflict is the same action (and pole) on the same device; merge keeps the game's bind, merge-overwrite replaces it, add keeps both. Clean releases every other **Flight Controls** action of the written devices; clean and merge-overwrite also release flight actions on an element JoyPro now uses. Nuke releases the flight actions of every other saved map and gives connected devices without a saved map an empty map. Camera, gameplay, chat, debug and editor binds, keyboard/mouse maps, calibration and every other value stay. Keyboard binds and modifiers are not written.
- **Backup**: before the first write, a snapshot of Player0's controller-map values goes into the backup store (`engine backups list`, `engine backups restore <id>` writes them back and removes map values JoyPro added); a fake-registry file (test hook: a config path ending in `.json` = `{ "value name": "<base64 of the bytes>" }`) is copied to `<file>.jpbak` like other games. JoyPro's metadata of exported binds goes to `<data>\GameMetadata\HKCU_Software_Shockfront_NuclearOption.joypro-meta.json`.
- **Auto Group**: `DB\Functions\NuclearOption.tsv` (from [nuclear-option-functions.tsv](nuclear-option-functions.tsv); every action id exists in the catalog) and the `NuclearOption` entry in `DB\Functions\aircraft.json`.
- Smoke test: `engine/tests/smoke/nuclearoption.ps1` with the fixture `engine/tests/fixtures/nuclearoption` (the real vJoy map of a test PC plus camera/gameplay binds).

- In game (2026-10-10, `tools\e2e\games\NuclearOptionSettings.ps1`, unattended): JoyPro's export of Pitch/Roll/Yaw on random vJoy axes shows in the Control Mapper's vJoy tab with the element ids JoyPro wrote (RZ 5, RY 4, Z 2 in the run), and after the game saved the maps again an import reads the same binds (with JoyPro's relation names from its metadata). The script snapshots and restores the registry key through the registry API: Unity writes floats as REG_DWORD with 8 data bytes, which .NET reads as a long but cannot write (RegSetValueEx).

Still open: binds checked in flight; slider element ids; hat element ids; keyboard element ids; recognized (Rewired-known) devices such as the T.16000M, which use a definition GUID instead of zeros (JoyPro only handles unrecognized devices); the axis sign the game expects for Pitch (the default map binds Pitch without invert).

Background material (not in the repo): the game's input classes as published mods reference them (a pre-0.34 build, about March 2026) [S3], Rewired 1.1.43 joystick definitions [S12] and the mod sources quoted below. At run time JoyPro reads only the user's registry key and the installed `resources.assets`.

## Input system
- Input = **Rewired** (Guavaman) [SRC S2: `Rewired_Core.dll`/`Rewired_Windows.dll` in `NuclearOption_Data\Managed`; S17 0.34 notes; S3: `using Rewired;`, `GameManager.playerInput = ReInput.players.GetPlayer(0)`]. All game code reads actions by **name** on Player 0: `player.GetAxis("Pitch")`, `GetButton("Fire")`, `GetButtonTimedPressUp("Radar", …)` [SRC S3 `PilotPlayerState.cs`].
- Binding UI = Rewired **Control Mapper** (`GameManager.controlMapper`, opened by Settings > Controls > "Open Control Bindings Menu") [SRC S3 `ControlsMenu.EditBindings`, `MainMenu.cs`; wiki S5]. One tab per device ("you have to click on each controller option at the top") [SRC S18].
- Persistence = the stock **`Rewired.Data.UserDataStore_PlayerPrefs`** component: the firstpass DLL of the game contains its class name and its key fragments `RewiredSaveData`, `|playerName=`, `|dataType=ControllerMap`, `|dataType=ControllerMap_KnownActionIds`, `|kv=`, `|hardwareGuid=`, `|hardwareIdentifier=`, `|duplicate=`, `|dataType=CalibrationMap`, `|dataType=InputBehavior`, `ControllerAssignments` [SRC S3 `Assembly-CSharp-firstpass.dll` text strings]. Mods save with `ReInput.userDataStore.Save()` [SRC S4]. The same class in a 2025/26 Rewired build has the same fields [SRC S21], so key version 2 is still current [INF].
- Game update 0.34 (2026-07-26) "Rewired updated to newer version to prepare for future Input System improvements" and shows a one-time "Input Manager System Update" notice [SRC S17]. What the notice says, and whether old binds survived, is [UNVERIFIED]. The current Rewired is 1.1.65 [SRC S14 docs]; the game's exact Rewired version is unknown.
- Exe `NuclearOption.exe`, data `steamapps\common\Nuclear Option\NuclearOption_Data\` [SRC S2 `tasklist /FI "IMAGENAME eq NuclearOption.exe"`, S3 assembly path].

## Storage
| What | Where |
|---|---|
| All binds | Unity PlayerPrefs = registry `HKEY_CURRENT_USER\Software\Shockfront\NuclearOption` [SRC S1, S2 `REG_PATH = r"Software\Shockfront\NuclearOption"`] |
| Controls options | same key: `ThrottleUseNegative`, `ThrottleUseRelative`, `InvertCollective`, `VirtualJoystickEnabled`, `VirtualJoystickInvertPitch`, `ViewInvertPitch`, `ControllerMenuNavigation`, `MenuWeaponSafety`, `UseTrackIR` (ints), `VirtualJoystickSensitivity`, `VirtualJoystickCentering`, `ViewSensitivity`, `ViewSmoothing`, `PressDelay`, `ClickDelay` (floats) [SRC S3 `ControlsMenu.SavePlayerSettings`] |
| Graphics etc. | same key, e.g. `Screenmanager Resolution Width_h182942802` [SRC S1] |
| Mod actions | `BepInEx\plugins\InputFramework\ExtraInput\ExtraInputActions.json` (Extra Input Framework: names and the ids it gave to mod actions) [SRC S8] |

- No file in `AppData\LocalLow`; moving binds to another PC = export/import of that registry key [SRC S1]. Not covered by Steam Cloud [INF: the forum recipe for moving binds is a .reg export].
- Binds are **global**, one set for every aircraft ("Nuclear Option has one set of flight bindings for every aircraft") [SRC S6 README]. The `Throttle` action is also the helicopter collective [SRC S5].

### Value names
Unity appends `_h<hash>` to every PlayerPrefs key: hash = djb2 with XOR (`h = 5381; for each UTF-8 byte b: h = (h * 33) ^ b`, unsigned 32 bit), written in decimal. Checked on all three published Nuclear Option names: `Screenmanager Resolution Height_h2627697771`, `Screenmanager Resolution Width_h182942802` [S1] and the Armory value below [S2] [SRC + own computation]. For non-ASCII keys the byte basis (UTF-8) is [UNVERIFIED].

Rewired keys (`playerPrefsKeyPrefix` = `RewiredSaveData`, `kv=2`) [SRC S11 source of the same component; S2 real example]:
```
RewiredSaveData|playerName=Player0|dataType=ControllerMap|kv=2|controllerMapType=JoystickMap|categoryId=<mapCat>|layoutId=0|hardwareGuid=<typeGuid>|duplicate=<n>                                   recognized joystick
RewiredSaveData|playerName=Player0|dataType=ControllerMap|kv=2|controllerMapType=JoystickMap|categoryId=<mapCat>|layoutId=0|hardwareGuid=00000000-0000-0000-0000-000000000000|hardwareIdentifier=<hwid>|duplicate=<n>   unrecognized joystick
RewiredSaveData|playerName=Player0|dataType=ControllerMap_KnownActionIds|kv=2|…same suffix…                 comma list of all action ids at save time
RewiredSaveData|playerName=Player0|dataType=ControllerMap|kv=2|controllerMapType=KeyboardMap|categoryId=<mapCat>|layoutId=0|hardwareGuid=0000…|hardwareIdentifier=<kb hwid>   (MouseMap likewise; no duplicate part)
RewiredSaveData|dataType=CalibrationMap|controllerType=Joystick|hardwareIdentifier=<hwid>|hardwareGuid=<typeGuid>   per device, also for recognized ones
RewiredSaveData|playerName=Player0|dataType=InputBehavior|id=<n>
RewiredSaveData_ControllerAssignments                                                                          JSON: per player hasKeyboard/hasMouse, joysticks[{instanceGuid, id, hardwareIdentifier}]
```
- Real example: `RewiredSaveData|playerName=Player0|dataType=ControllerMap|kv=2|controllerMapType=JoystickMap|categoryId=0|layoutId=0|hardwareGuid=d74a350e-fe8b-4e9e-bbcd-efff16d34115|duplicate=0_h1237724504` (an XInput gamepad) [SRC S2]. The hash matches the rule above.
- Player name `Player0` [SRC S2]. `SaveAll` saves every player including the System player [SRC S11]; only `Player0` matters [INF].
- `duplicate` = index among the Player's joysticks with the same type GUID (recognized) or the same hardwareIdentifier (unrecognized), in Rewired's joystick order [SRC S11 `GetDuplicateIndex`].
- On load Rewired tries key versions 2, 1, 0 in turn (older keys put `hardwareIdentifier` before `hardwareGuid`) [SRC S11]. Values with old key versions may still exist after updates; readers should parse the `|key=value` pairs, not fixed positions [INF].
- Regedit hides value names of 255+ characters, but they exist (Rewired comment) [SRC S11]; use the registry API.

### Value encoding
- Strings (all Rewired data) are **REG_BINARY = UTF-8 bytes + exactly one 0x00**, although the XML header says `encoding="utf-16"` [SRC S2: "confirmed empirically against a real captured value … round-tripped byte-for-byte"]. Ints are REG_DWORD [INF from S1 "use decimal" advice for `Screenmanager Resolution Width`]; the float encoding is [UNVERIFIED] (JoyPro must not write floats).
- The XML is compact (no line breaks, no indentation), as written by Rewired's `ToXmlString()` [SRC S15, S16 samples].

## JoystickMap XML
Exact example of the format (unrecognized joystick in Liftoff, a Rewired game; Nuclear Option values have the same shape; the field list is confirmed for Nuclear Option by S2) [SRC S15]:
```xml
<?xml version="1.0" encoding="utf-16"?><JoystickMap dataVersion="2" hardwareGuid="00000000-0000-0000-0000-000000000000" hardwareName="RadioMaster TX16S Joystick (connector)" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:schemaLocation="http://guavaman.com/rewired http://guavaman.com/schemas/rewired/1.1/JoystickMap.xsd" xmlns="http://guavaman.com/rewired"><sourceMapId>18</sourceMapId><categoryId>2</categoryId><layoutId>0</layoutId><name></name><hardwareGuid>00000000-0000-0000-0000-000000000000</hardwareGuid><enabled>true</enabled><buttonMaps /><axisMaps><ActionElementMap><actionCategoryId>0</actionCategoryId><actionId>7</actionId><elementType>0</elementType><elementIdentifierId>0</elementIdentifierId><axisRange>0</axisRange><invert>false</invert><axisContribution>0</axisContribution><keyboardKeyCode>0</keyboardKeyCode><modifierKey1>0</modifierKey1><modifierKey2>0</modifierKey2><modifierKey3>0</modifierKey3><enabled>true</enabled></ActionElementMap>…</axisMaps></JoystickMap>
```
- Root attributes `dataVersion`, `hardwareGuid`, `hardwareName` (device name as Rewired reports it), then the namespaces. Children in fixed order: `sourceMapId` (id of the default map it came from, `-1` if none), `categoryId` (= map category of the key), `layoutId`, `name`, `hardwareGuid`, `enabled`, `buttonMaps`, `axisMaps` [SRC S15, S16]. Keep all of them; the newer Rewired in 0.34 may add elements or raise `dataVersion` [UNVERIFIED].
- Empty lists are written self-closing with a space: `<buttonMaps />`, `<axisMaps />` [SRC S15]. A writer that only looks for `</axisMaps>` (as Armory does) fails on them.
- `<buttonMaps>` holds bindings of button elements (incl. hat directions), `<axisMaps>` those of axis elements [SRC S2, S15].
- `ActionElementMap` fields, in this order [SRC S2 (Nuclear Option), S15, S16]:

| Field | Meaning (Rewired enum values [SRC S13]) |
|---|---|
| `actionCategoryId` | action category of the action |
| `actionId` | numeric Rewired action id (see "Action ids") |
| `elementType` | 0 Axis, 1 Button, 100 CompoundElement (S2's comment "2 = CompoundElement" is wrong) |
| `elementIdentifierId` | element of the device (tables below) |
| `axisRange` | 0 Full, 1 Positive half, 2 Negative half (axis elements) |
| `invert` | `true`/`false`, full-axis bindings only |
| `axisContribution` | Pole: 0 Positive, 1 Negative; which pole of an axis action a button (or half axis) drives |
| `keyboardKeyCode` | 0 for joysticks; Unity-style KeyCode for keyboard maps (A=97, Space=32, LeftShift=304, Keypad5=261, PageUp=280 …) |
| `modifierKey1..3` | 0 None, 1 Control, 2 Alt, 3 Shift, 4 Command; **keyboard maps only** [SRC S13 "The first keyboard modifier key"] |
| `enabled` | `true` |

## Device identification
- A joystick map is keyed by Rewired's **hardware type GUID**, not by the DirectInput instance GUID [SRC S11]. Recognized devices have the GUID of their Rewired definition (e.g. Thrustmaster T.16000M `f9166a6e-926e-4a12-8abc-d623ff193e3d`, Warthog stick `ffb9683b-ea6e-4ff6-b0e1-694d33876f72`, Saitek X-56 stick `d8e96a10-f86a-4364-aaa0-5d99fa017573`, XInput gamepad `d74a350e-fe8b-4e9e-bbcd-efff16d34115`) [SRC S12, S2]. Recognition uses VID/PID (`productGUID` = DirectInput product GUID, `PIDVID` in Data1) and product names per input source [SRC S12].
- Rewired's list of recognised joysticks: all 168 definitions of Rewired 1.1.43 with GUID, VID:PID and product names [SRC S12]. Newer Rewired versions add devices; the current supported list (S14) adds e.g. Honeycomb Echo and Yawman Arrow, which are not in this copy. Supported flight hardware: CH, Saitek/Logitech X45/X52/X55/X56, Thrustmaster Warthog/T.16000M/T.Flight/TWCS/pedals, Logitech Extreme 3D Pro, MS SideWinder, VKB **Gladiator MKII only** (231D:0121) [SRC S14]. **VKB Gunfighter/NXT, Virpil/VPC, WinWing, MOZA, VPforce, MFG are not recognized → "Unknown Controller"** [SRC S14 list, INF]. All HOTAS devices on a test PC fall into this group.
- Unrecognized devices: `hardwareGuid` all zero plus `hardwareIdentifier=<string>` in the key; the string "varies depending on the platform, input source in use, and the device" [SRC S14 API]. Linux example: `LinuxLinuxRadioMasterTX16SJoystick(connector)203084f541209-0000-0000-0000-504944564944` (name without spaces + a product GUID ending in ASCII "PIDVID") [SRC S15]. **Windows format [OBS 2026-10-10]: `WindowsRawInput` + HID product string without whitespace + PID + VID (hex, upper case)**, see "JoyPro integration". Candidates for matching: the `hardwareName` attribute (expected: the HID product string under Raw Input [INF]), VID/PID inside `hardwareIdentifier` [INF], `RewiredSaveData_ControllerAssignments` (`instanceGuid` + `hardwareIdentifier` per joystick; only if the game enables assignment saving [UNVERIFIED]), the `CalibrationMap` key (always has `hardwareIdentifier`).
- Rewired's `deviceInstanceGuid` ("may stay constant between sessions") is not used in map keys [SRC S14 API, S11].
- Two identical devices: told apart only by `duplicate=n` (Rewired joystick order) [SRC S11] → cannot be mapped to DirectInput instance GUIDs [INF]; skip like Elite Dangerous.
- Rewired on Windows uses Raw Input or DirectInput (+ XInput, optionally Windows.Gaming.Input); which one Nuclear Option enables is [UNVERIFIED]. Element ids of the Unknown Controller are the same for both (below).

## Element identifiers
### Unknown Controller (all unrecognized joysticks) [SRC S12 `Default/Unknown.asset`]
| Element | elementIdentifierId | Source (DirectInput / Raw Input) | JoyPro |
|---|---|---|---|
| Axis 0 … Axis 7 | 0 … 7 | sourceAxis 1 … 8 | `JOY_X, JOY_Y, JOY_Z, JOY_RX, JOY_RY, JOY_RZ, JOY_SLIDER1, JOY_SLIDER2` [INF, below] |
| Axis 8 … Axis 10 | 8 … 10 | sourceAxis 9 … 11 | not mapped |
| Axis 11 … Axis 31 | 11 … 31 | DirectInput 12 … 32 (DIJOYSTATE2 extra axes); Raw Input: none | not mapped |
| Button 0 … Button 127 | 32 … 159 | button 0 … 127 | `JOY_BTN1` … `JOY_BTN128` (id = 31 + n) |
| Hat h Up/Right/Down/Left (h = 0 … 15) | 160 + 4h + {0,1,2,3} | hat h, direction 0/1/2/3 | `JOY_BTN_POV<h+1>_U/R/D/L` |
| Hat h Up Right/Down Right/Down Left/Up Left | 224 + 4h + {0,1,2,3} | hat h, direction 4/5/6/7 | `JOY_BTN_POV<h+1>_UR/DR/DL/UL` |
- sourceAxis numbering = DirectInput axis order X, Y, Z, Rx, Ry, Rz, Slider0, Slider1: Rewired's own definitions map T.16000M twist to 6 and its throttle wheel to 7, Warthog throttle left/right to 6 (Rz)/3 (Z) and friction to 7, Extreme 3D Pro twist 6 / throttle 7 [SRC S12, INF]. Under Raw Input, which HID usage lands in sourceAxis 8 (second slider, dial, wheel) is [UNVERIFIED].
- Max 128 buttons, 16 hats; diagonals are separate elements.
- Default calibration dead zone 0.1 on every axis of the Unknown Controller [SRC S12; S15 calibration sample].

### Recognized devices
Rewired's element tables: per definition, input source, element id → name, type, DirectInput source (`JOY_*`), `invert` and default dead zone [SRC S12 Rewired 1.1.43]. Example T.16000M (Raw Input): 0 Stick X (`JOY_X`), 1 Stick Y (`JOY_Y`, invert 1), 2 Stick Rotate (`JOY_RZ`), 3 Throttle (`JOY_SLIDER1`, invert 1), 4 Trigger (`JOY_BTN1`) … Element ids are per definition, not generic; some devices map hats to buttons. The game's newer Rewired may have changed a definition; users reported the X-56 stick's "Stick rotate" and "Grip Mini-Stick Y" swapped [SRC S18 595150321338116090]. Treat recognized devices as lower confidence.

## Axes
- Axis action ← axis element: `<axisMaps>`, `elementType 0`, `axisRange 0` (Full), `axisContribution 0`, `invert` per binding [SRC S13, S15]. The Control Mapper shows an invert toggle on full-axis fields [SRC S14 ControlMapper]; NOVR toggles `ActionElementMap.invert` [SRC S4].
- **Direction**: Unknown Controller axes are not inverted by Rewired (raw DirectInput direction, Y forward = −1); recognized definitions often invert (`Stick Y`, throttles: `invert 1` in S12) [SRC S12]. So JoyPro's DirectInput-relative `Inverted` must be XOR-ed with the definition's invert for recognized devices [INF]. Which sign `Pitch` expects (positive = nose up from the virtual-joystick code [INF S3]) and whether the game's default Unknown map binds Y→Pitch with `invert=true` are [UNVERIFIED]; read the default map once.
- **Dead zone, sensitivity**: not part of the binding. Rewired stores them per device axis in the `CalibrationMap` value: `<AxisCalibration>` per axis index with `enabled, deadZone, calibratedZero, calibratedMin, calibratedMax, invert, sensitivity, applyRangeCalibration, sensitivityType (0 Multiplier, 1 Power, 2 Curve), sensitivityCurve` [SRC S15 sample, S13]. Calibration `invert` flips the physical axis for all actions. The Control Mapper of the game contains Rewired's `CalibrationWindow` [SRC S3 firstpass strings]; whether the game shows its Calibrate button is [UNVERIFIED]. No saturation/curvature per binding; `calibratedMin/Max` could emulate saturation [INF].
- **Half axes**: `axisRange 1/2` binds one half of a physical axis; for a button action this makes the half axis a button [SRC S13].
- **Throttle / collective** (`Throttle`, read with `GetAxisRaw`, clamped −1…1) [SRC S3 `PilotPlayerState.PlayerAxisControls`]:
  - `ThrottleUseNegative` (UI "Use Throttle Axis Negative Region", default **on**): throttle = (v + 1) / 2, a full axis gives 0…100 %. Off: only v > 0 counts (negative half = 0 %).
  - `ThrottleUseRelative` (default off): the axis becomes a rate input (|v| > 0.1 → ±1).
  - The game smooths jumps: a change of ≥ 0.5 between frames ramps at 1/s; keys (`Throttle` poles) ramp the same way.
  - `InvertCollective` (UI "Invert Throttle Control when used as Collective", default off): on aircraft with `takeoffDistance == 0` (helicopters) throttle = 1 − throttle.
  - Option labels from S18 4035850678064380470; defaults from S3 `PlayerSettings` and `ControlsMenu.ResetSlidersToggles`.
  - Users report a throttle stuck at 48–75 % until they toggle these options [SRC S18]. JoyPro should write throttle bindings as full axes and leave the options alone (warn when `ThrottleUseNegative` is 0).
- **Custom Axis 1** (VTOL nozzle / tiltrotor / liftfan): clamped to 0…1, so only the positive half of a full axis has an effect [SRC S3]. With `Axis Modifier` held, the throttle input drives Custom Axis 1 instead [SRC S3, S5].
- **Brake**: an axis action ("Brake Axis", poles "Apply Brakes" / "Brake Axis -") that the game reads as on/off (`GetButton("Brake")`) [SRC S5, S3, S7 BrakeAsAxis reads `GetAxisRaw("Brake")`]. The HeliBinds author calls it a button action [SRC S6]; type [UNVERIFIED].

## Buttons, hats
- Button action ← button element: `<buttonMaps>`, `elementType 1`, `axisRange 0`, `invert false`, `axisContribution 0` [SRC S15, S16].
- Button on an axis action (e.g. `Throttle.Negative` = "Decrease Throttle"): same, with `axisContribution 1` for the negative pole [SRC S13 axisContribution].
- Hats are button elements (ids above) [SRC S12].
- Several actions use timed presses: `Next Weapon` (hold toggles turret auto), `Flight Assist` (hold = auto-hover), `Cancel` (hold = deselect all), `Select`, `Switch View`; click/hold times = `ClickDelay`/`PressDelay` [SRC S3, S5].

## Keyboard
- Keyboard maps: `elementType 1`, `elementIdentifierId` = Rewired keyboard element id, `keyboardKeyCode` = KeyboardKeyCode. Known pairs: A…Z = 1…26 / 97…122, Space 54/32, Tab 56/9, LeftShift 117/304, LeftControl 119/306 [SRC S16]. The full element-id table is not public [UNVERIFIED]; v1 should import keyboard binds (by `keyboardKeyCode`) and not write them.
- Default keys (wiki): pitch S/W, roll D/A, yaw E/Q, throttle LeftShift/LeftControl, brakes B, custom axis Up/Down arrows, Fire Space, Target Select Return, Target Cancel Backspace, Next Weapon Tab, Countermeasures X, Next Countermeasure V, Eject Ctrl+Shift+E, Flight Assist F, Gear G, Night Vision N, Radar R, Center View Numpad 5, Switch View L, Pan/Tilt Numpad 4/6/2/8 and mouse, Zoom mouse wheel, Free Look right mouse button, FoV PageUp/PageDown, Pause Escape, Slow Motion `,`, Map M, Chat T [SRC S5].

## Modifiers
- Rewired modifier keys exist only in keyboard maps (Ctrl/Alt/Shift/Command, up to 3) [SRC S13]. Joystick bindings cannot have modifiers → JoyPro binds with `AllReformers` cannot be exported (skip with a warning) [INF].
- The game's only joystick "modifier" is the `Axis Modifier` action (hold to move throttle input to Custom Axis 1) [SRC S3].

## Actions
[nuclear-option-actions.tsv](nuclear-option-actions.tsv): 82 rows = 52 actions (27 Flight Controls, 14 Camera Controls, 9 Gameplay, 2 Mission Editor) + 30 pole rows. Ids are the Rewired **action names** as the game code passes them (`"Pitch"`, `"Night Vis"`, `"Flight Assist"`, `"Cycle Look At"` …) [SRC S3, S7]; titles are the names shown in the bindings menu [SRC S5; "Look At Target" = `Cycle Look At` from S7 `LookAtTargetFix`]. `<Action>.Positive/.Negative` = button on a pole (same convention as `DB\Wardogs\actions.tsv`). Pole signs are inferred from the code [INF], rows marked "(?)" are weak.

| Tab (category) | Actions |
|---|---|
| Flight Controls | Pitch, Roll, Yaw, Throttle (also collective), Brake, Custom Axis 1 (axes); Axis Modifier, Fire, Select (Target Select), Cancel (Target Cancel), Next/Previous Weapon, Weapon Wheel, Link Guns, Turret Control, Countermeasures, Next Countermeasure, Radial Menu (+ Radial Menu Horizontal/Vertical axes), Eject, Toggle Engine, Flight Assist, Gear, Night Vis, Radar, Nav Lights |
| Camera Controls | Center, Switch View, Free Look, Cycle Look At, Pan View, Tilt View, Zoom View, FOV, Move Longitudinal/Lateral/Vertical (free camera; Move Vertical also cockpit elevation since 0.34 [SRC S7, S17]), Spectate Next/Previous Aircraft, Change Spectate Faction |
| Gameplay | Map, Jump Map, Move Map Horizontal/Vertical, Pause, Slow Motion, Open/Submit/Cancel Chat |
| Mission Editor | DeleteUnit, FocusUnit (more editor actions exist, names not public) |
- **Not bindable**: flaps, airbrake, hook, bay doors, jettison, weapon-by-number, wheel brake left/right. The radial menu offers only the functions above (Eject, Gear, Radar, NavLights, FlightAssist, AutoHover, Engine, Nightvis, TurretAuto, SelectWeapon, LinkGuns) [SRC S3 `RadialMenuAction.ActionType`, S5].
- Same actions for jets, VTOLs and helicopters [SRC S6 README]. Helicopter-only bindings exist only via the HeliBinds mod (actions "Helicopter Pitch/Roll/Yaw/Collective/Brake" in the Flight category) [SRC S6].
- Categories: the action category of the flight actions is named `flight` [SRC S6 `TargetCategory = "flight"`], mods also use `Debug` and `Gameplay` [SRC S8, S9]. The mod manager Nuclear-Option-Armory lists **7 action categories and 61 actions** (version unknown, 2026) [SRC S2 controller_engine.py]. Tab names "Flight Controls", "Camera Controls", "Gameplay" [SRC S5].

### Action ids (resolved 2026-10-10: read from resources.assets, see "JoyPro integration")
- The saved XML holds only numeric `actionId`/`actionCategoryId`; names exist only in the game's Rewired Input Manager data [SRC S2]. No public list exists. Ids are sparse and not contiguous: HeliBinds notes that the usual "highest id + 1" recipe of NO mods lands "around 850" [SRC S6 `StableActionId`], i.e. game ids reach roughly 850 [INF]; TargetCamControl starts at 1000 [SRC S9], Extra Input Framework counts down from 1 000 000 [SRC S8], HeliBinds uses 1000–9999 (FNV hash of the name) [SRC S6]. Mod action ids therefore change with the installed mods.
- Ways to resolve name → id on the user's PC (no game process involved):
  1. **Scan the game data** (recommended): Rewired serializes the Input Manager's `actions` list as `_id` (int32), `_name` (string), `_type` (int32, 0 Axis / 1 Button), `_descriptiveName`, `_positiveDescriptiveName`, `_negativeDescriptiveName` (strings), `_behaviorId` (int32), `_userAssignable` (bool), `_categoryId` (int32); `actionCategories` as `_name, _descriptiveName, _tag, _id, _userAssignable`; `mapCategories` as `_name, _descriptiveName, _tag, _id, _userAssignable, _checkConflictsWithAllCategories, _checkConflictsCategoryIds` [SRC S19, same layout in Rewired 1.1.x prefabs and a 2026 scene]. In Unity's binary files a string is int32 length + UTF-8 + padding to 4 bytes, a bool 1 byte + padding [INF, Unity serialization]. Search `NuclearOption_Data\*.assets`, `level*` for the record of a known name (`"Night Vis"` followed by `"Toggle Night Vision"`) and read the whole list. Whether the Input Manager sits in an uncompressed file (not in an Addressables bundle) is [UNVERIFIED].
  2. **From the saved keyboard/mouse maps**: the game saves the default keyboard map with the binds; default keys (wiki list) identify the action ids of everything that has a default key (both poles of the flight axes, Fire, Gear, Radar …) and the actionCategoryId of each map category. Actions without default key (Link Guns, Turret Control, Toggle Engine, Nav Lights, Axis Modifier, Previous Weapon, Weapon Wheel) stay unknown. Good as a cross-check of option 1.
  3. A one-time calibration in the game (user binds known buttons to the unknown actions). Last resort.
- `ControllerMap_KnownActionIds` gives the complete id list of the current version (cross-check that every id found exists) [SRC S11].

### Map categories
- Each map value belongs to one map category (`categoryId` in key and XML); each Control Mapper tab is a "mapping set" = map category + action categories [SRC S14 ControlMapper, S10 Boscali `MappingSet`]. So a device has one JoystickMap value per user-assignable map category, empty ones included (Liftoff saves empty maps) [SRC S15, INF]. An action must be written into the value of the map category that contains its action category; the numbers are [UNVERIFIED] → derive from the scanned `mapCategories`/ControlMapper data or from the saved keyboard maps (which `actionCategoryId`s occur in which map category).

## When the game reads and writes
- Rewired loads all saved maps at start (`loadDataOnStart`) and loads a joystick's maps again when it is connected [SRC S11]. When a saved value exists it **replaces** the default map of that category; new action ids not in `KnownActionIds` get their default bindings added [SRC S11 `AddDefaultMappingsForNewActions`].
- Saves (all players, all maps, calibration, assignments, then `PlayerPrefs.Save()`): when the Control Mapper window is closed, when a joystick is unplugged (that joystick's data), and on controller connect/disconnect (assignments) [SRC S14 ControlMapper, S11]. NOVR and other mods also call `ReInput.userDataStore.Save()` [SRC S4].
- **The game must be closed** for an export: an open Control Mapper or a joystick unplug overwrites the values, and Unity rewrites PlayerPrefs on quit [INF; Armory refuses to write while `NuclearOption.exe` runs, SRC S2].
- No saved value for a device → Rewired uses the game's default map. For unknown joysticks that default binds "x,y,z on every device" (duplicate pitch/roll on pedals) and "throttle to both the slider and the throttle" [SRC S18 5715697457572661391, 4035850678064380470]. Users fix this by clearing each device tab [SRC S18].

## Export plan (research before the install; what JoyPro does is in "JoyPro integration")
1. **Find**: `HKCU\Software\Shockfront\NuclearOption`. Missing → game never started. Refuse while `NuclearOption.exe` runs. Back up every value JoyPro will change (name + raw bytes, e.g. a `.reg` export) before the first write.
2. **Action table**: resolve name → (id, type, actionCategoryId) and map category per the "Action ids" section; refuse to write if any managed action cannot be resolved. Store it per game version (refresh-db).
3. **Device → value**: for each JoyPro device:
   - VID/PID in Rewired's list of recognised joysticks (refresh from the game version if possible) → recognized: key with `hardwareGuid=<guid>|duplicate=0`.
   - else unrecognized: find the existing value whose `hardwareName`/`hardwareIdentifier` matches the device (rule [UNVERIFIED]).
   - Two connected devices with the same Rewired identity → skip with a warning.
   - No existing value: for recognized devices the value name can be computed (hash rule verified) and a map created from scratch; for unrecognized devices tell the user to connect the device, open the bindings menu once and close it (that saves a value with the right `hardwareIdentifier`).
4. **Edit the XML as text** (like S2): parse `ActionElementMap` blocks, keep each block's exact substring, replace/remove/append only the blocks JoyPro owns, keep header, root attributes, `sourceMapId`, `enabled`, unknown elements and every other block byte-for-byte; expand `<buttonMaps />`/`<axisMaps />` when adding. Append new blocks with the full field list in the order above. Write back REG_BINARY UTF-8 + one 0x00 into the same value name.
5. **Encode**: axes `JOY_X…JOY_SLIDER2` → element 0…7 (unknown) or the definition's element (recognized), `axisRange 0`, `invert` = JoyPro `Inverted` (XOR definition invert for recognized); buttons `JOY_BTNn` → 31 + n (n ≤ 128); POV → 160/224 tables (POV 1–16, diagonals allowed); button on an axis pole → `axisContribution 0/1`. Skip with a warning: modifiers, buttons > 128, axes beyond Slider2, an axis on a button action (unless the half-axis option is wanted).
6. **Dead zone** (optional, off by default): set `deadZone` of that axis in the device's `CalibrationMap` value; keep all other fields. Never touch calibration `invert`.
7. **Clean / nuke**: remove `ActionElementMap` blocks whose action is a "Flight Controls" action from [nuclear-option-actions.tsv](nuclear-option-actions.tsv) on the written devices (clean) or on other joystick values of Player0 (nuke) — this also removes the game's default x/y/z axis binds. Keep Camera/Gameplay/Mission-Editor blocks, blocks with unknown or mod action ids, keyboard/mouse maps.
8. **Keyboard**: not in v1 (element-id table incomplete).
9. **Options**: leave `ThrottleUseNegative`, `InvertCollective` etc. alone; warn if `ThrottleUseNegative` = 0 while JoyPro writes a full throttle axis.

### What an exporter must keep untouched
All values except the edited Player0 JoystickMap values (and optionally CalibrationMap): `…_KnownActionIds` (keep; it controls default merging), KeyboardMap/MouseMap, InputBehavior, `RewiredSaveData_ControllerAssignments`, other players, older-key-version values, game settings (`ThrottleUseNegative_h946159981`, `InvertCollective_h1480218291`, `Screenmanager …`, `unity.*`), and every byte of an edited value outside the touched blocks, including bindings of mod actions (ids ≥ 1000) and of non-flight categories.

## Import
Per Player0 JoystickMap value: identify the device (see above; skip disconnected and ambiguous ones), translate `elementIdentifierId` → `JOY_*` (unknown table or definition table), `actionId` → action name (+ `.Positive/.Negative` from `axisContribution` for buttons on axis actions), `invert` → `Inverted`, dead zone from the CalibrationMap axis. Keyboard binds via `keyboardKeyCode` (+ `modifierKey1..3`).

## Open points of the research (1, 2 and 5 answered by the install, see "JoyPro integration"; 6: Brake is an axis action)
1. Windows `hardwareIdentifier` and `hardwareName` of an unrecognized joystick, and how to match them to JoyPro's devices (VID/PID, HID product string, DirectInput name).
2. Numeric action ids, action/map category ids, and whether the action list can be scanned from `NuclearOption_Data`.
3. Raw Input vs DirectInput; what sits in Axis 6/7 for a device with two sliders/dials.
4. Sign conventions: default Unknown-map invert of Pitch/Throttle; pole signs marked "(?)".
5. Root `dataVersion` / extra XML elements after the 0.34 Rewired update; whether 0.34 reset old binds.
6. Whether `Brake` is an axis or a button action; whether the calibration window is reachable.

## Sources
- S1 Steam forum "How To Migrate My Control Bindings?" https://steamcommunity.com/app/2168680/discussions/0/608668448981019398/
- S2 Nuclear-Option-Armory (mod manager, reads/writes the binds) https://github.com/DomesticNukes/Nuclear-Option-Armory — `source/rewired_registry.py`, `source/controller_engine.py`
- S3 the game's input classes of a pre-0.34 build as community mods reference them (class and member names only; reference notes are private) — `PilotPlayerState`, `ControlsMenu`, `PlayerSettings`, `GameManager`, `MainMenu`, `RadialMenuAction`, camera states, `Assembly-CSharp-firstpass.dll`
- S4 NOVR settings panel https://github.com/InfernoSuperNova/novr/blob/990f3db1eed9763bdd774f5e91f63a5d3c77530d/NOVR/VrUi/Native/NativeSettingsPanel.cs
- S5 Wiki controls https://nuclearoption.wiki.gg/wiki/Nuclear_Option_Wiki:Controls
- S6 HeliBinds https://github.com/CarloApri/NO-HeliBinds (README, `HeliBinds/Patches/RewiredAwakePatches.cs`, `HeliBinds/Helpers/StableActionId.cs`)
- S7 Pauel's Random Fixes https://github.com/pauel3312/PauelsRandomFixes (README, `PRF/Fixes/BrakeAsAxis.cs`, `LookAtTargetFix.cs`, `ThrottleInputFix.cs`)
- S8 Extra Input Framework https://github.com/Assassin1076/NuclearOptionInputFramework (`HarmonyPatch.cs`), https://github.com/NiJeTi/NoWingmen/issues/8
- S9 TargetCamControl https://github.com/9138noms/TargetCamControl (`Plugin.cs`)
- S10 Boscali Ocean Training Exercise `src/Mod_Input.cs` https://github.com/MinecrackTyler/BoscaliOceanTrainingExercise ; SplitThrottle https://github.com/9138noms/NuclearOption-SplitThrottle
- S11 Rewired `UserDataStore_PlayerPrefs.cs` (Rewired 1.1.43 copy) https://github.com/RemiFusade2/Froguelike/blob/HEAD/Froguelike/Assets/Rewired/Internal/Scripts/DataStorage/UserDataStore_PlayerPrefs.cs
- S12 Rewired joystick definitions (same copy) https://github.com/RemiFusade2/Froguelike/tree/HEAD/Froguelike/Assets/Rewired/Internal/Data/Controllers/HardwareMaps/Joysticks
- S13 Rewired API: https://guavaman.com/projects/rewired/docs/api-reference/html/T_Rewired_AxisRange.htm , `T_Rewired_Pole.htm`, `T_Rewired_ModifierKey.htm`, `T_Rewired_ControllerElementType.htm`, `T_Rewired_KeyboardKeyCode.htm`, `T_Rewired_AxisSensitivityType.htm`, `P_Rewired_ActionElementMap_modifierKey1.htm`, `P_Rewired_ActionElementMap_axisContribution.htm`
- S14 Rewired docs: https://guavaman.com/projects/rewired/docs/ControllerMaps.html , `ControlMapper.html`, `SupportedControllers.html`, `UserDataStore.html`, API `P_Rewired_ControllerIdentifier_hardwareIdentifier.htm`, `P_Rewired_Joystick_deviceInstanceGuid.htm`
- S15 Liftoff Rewired save https://github.com/ilopezgazpio/gym-liftoff-connector/blob/main/liftoff_configuration/ba2614f0-0c69-4dd6-9dac-68b2b72e1acb_0001.inputsettings
- S16 Risk of Rain 2 save https://github.com/tjosepo/ror2-editor/blob/main/src/sample-save.tsx
- S17 Update 0.34 https://changelog.gg/games/nuclear-option-2168680/updates/2026-07-27-update-0-34-bbd0e223afeaf8de ; history https://nuclearoption.wiki.gg/wiki/Development
- S18 Steam HOTAS threads (app 2168680): 5715697457572661391, 4035850678064380470, 595150321338116090, 6633328470867897818, 5715697457576600526 (https://steamcommunity.com/app/2168680/discussions/0/<id>/)
- S19 Rewired Input Manager YAML: https://github.com/RemiFusade2/Froguelike/blob/HEAD/Froguelike/Assets/Rewired/Examples/SimpleControlRemapping/SimpleControlRemapping.unity , https://github.com/BartMartner/OpenARNF/blob/HEAD/Assets/Prefabs/Rewired/Rewired%20Input%20Manager.prefab
- S20 Controller Tuner mod https://github.com/TheDEMIKus/Nuclear-Option-Controller-Tuner-Mod
- S21 Rewired's `UserDataStore_PlayerPrefs` class in a 2025/26 Rewired build (field list; reference notes are private)
