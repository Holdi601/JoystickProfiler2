# VTOL VR (Boundless Dynamics, Steam app 667970) (experimental, not tested in game)

JoyPro has two VTOL VR integrations:

| CLI | Writes | Covers |
|---|---|---|
| `engine vtolvr` | `<install>\SaveData\BYOJ_<seat>.xml` of the **BYOJoystick** mod (`engine/Games/VtolVrIntegration.cs`) | full HOTAS: 433 targets over 9 vehicle seats, axes with invert/deadzone, buttons, hats 1-4, one modifier button, keyboard with `--include-keyboard` |
| `engine vtolvr-native` | `%APPDATA%\Boundless Dynamics, LLC\VTOLVR\SaveData\inputbindings.cfg` (`engine/Games/VtolVrNativeIntegration.cs`) | the game's own hardware input: Rudder, Wheel Brake L, Wheel Brake R |

Catalogs: `EngineOnly_Runtime/DB/VTOLVR` (`actions.tsv`, `seats.tsv`), `DB/VTOLVRNative`, `DB/Functions/VTOLVR.tsv`, `VTOLVRNative.tsv`. Test fixtures: `engine/tests/fixtures/vtolvr` (constructed with the game's and the mod's serializer order, not written by the game).

Native export rules as implemented: only maps of devices VTOL VR has listed (map, calibration or assignment entry) are written; the device element is the n-th axis the device has (unverified); clean and nuke remove the three actions from the other devices' maps; the toe brakes get the `axisRange` of the relation's `vtolnative` tuning (0 Full, 1 Positive); `HARDWARE_RUDDER` and the other Settings > Controls values are written to `gameSettings.cfg` when the profile sets them (`engine ffb set --device * --game VTOLVR`), otherwise a missing/off `HARDWARE_RUDDER` gives a warning. See "Every setting the game offers" below.

Tags: **[OBS]** seen in real files on a test PC · **[OBS-bin]** seen in the installed game's own assemblies and data files (`Assembly-CSharp.dll`, `Rewired_Core.dll`, `Rewired_Windows.dll`; the Rewired `InputManager` and `HardwareJoystickMap` objects in `level0` / `sharedassets0.assets`), looked at for interoperability only, nothing modified · **[SRC]** third-party source or post with URL · **[INF]** inferred · **[UNVERIFIED]** needs one check with the running game. Research 2026-10-01 on VTOL VR **1.13.4f1** (Steam build 25514272, `globalgamemanagers` PlayerSettings; changelog head "v1.13.0fX"). The game has never been started on a test PC (`appmanifest_667970.acf` `LastPlayed 0`, no `%APPDATA%\Boundless Dynamics, LLC`, no `LocalLow\Boundless Dynamics, LLC`).

The maintainer's research notes (Rewired hardware definitions, sample generators) are not part of the repo. At run time JoyPro reads only the user's save files, the installed `level0` (native action list) and, after the user installed it, the BYOJoystick mod.

## Summary
- VTOL VR is VR-only. Pitch/roll, throttle, weapons, switches, MFDs etc. are operated with the VR controllers on the virtual stick, throttle and cockpit; there is **no native HOTAS support** [OBS-bin; SRC developer quoted in https://steamcommunity.com/app/667970/discussions/0/5287797783032612392/ : "VTOL VR will remain VR-only"].
- Native physical-device input = **three axes**: `Rudder`, `Wheel Brake L`, `Wheel Brake R` (Settings › "Hardware Control Rudder" + "Bindings"), read through **Rewired** (Guavaman) with the **Raw Input** source. Stored in `%APPDATA%\Boundless Dynamics, LLC\VTOLVR\SaveData\inputbindings.cfg` (Boundless "ConfigNode" text file whose values are Rewired XML/JSON strings). Global, not per vehicle.
- Full HOTAS/keyboard binding exists only through mods. The current one is **BYOJoystick** (Steam Workshop 3276352374, needs the "VTOL VR Mod Loader"): per-vehicle/per-seat XML files `<install>\SaveData\BYOJ_<vehicle>.xml`, devices by **DirectInput instance GUID**, DirectInput axis/button names — a near-perfect fit for JoyPro (433 actions over 9 vehicle seats).
- Feasibility: native writer = small but safe (3 axes; device identity via product string + VID/PID, not the DirectInput GUID). BYOJ writer = full HOTAS coverage, trivial format, but depends on a third-party mod.

## Input stack [OBS-bin]
| Layer | What VTOL VR uses it for |
|---|---|
| Rewired (`Rewired_Core.dll`, `Rewired_Windows.dll`, `Plugins\x86_64\Rewired_DirectInput.dll`) | the only bindable hardware input: actions `Rudder` (id 0), `Wheel Brake L` (5), `Wheel Brake R` (6); also mouse X/Y/scroll (2/3/4) for the map/mission editors, not user-assignable |
| SteamVR/OpenXR actions (`actions.json`, `bindings_*.json` in the install) | VR controllers (grip, trigger, thumbstick) |
| Unity legacy `Input` / new Input System | hard-coded debug and spectator-camera keys only (`CameraFollowMe`: V, T, `[`, `]`, `,`, `.`, `/`, H, Right-Shift+A/P/D/Tab; `VRHead`: Shift+right mouse = mouse look, Right-Shift+L/R radar debug; `FlightAssist`: Q at spawn disables assist; `VRJoystick` WASD/QE debug stick). Nothing is rebindable or stored. |

Rewired InputManager (`level0`, MonoBehaviour path id 121, `_dontDestroyOnLoad` 1) [OBS-bin]:
- `configVars`: `windowsStandalonePrimaryInputSource = 0` (**RawInput**), `useXInput = true`, `useNativeMouse = true`, `useEnhancedDeviceSupport = true`, `ignoreInputWhenAppNotInFocus = false`, `joystickRefreshRate = 240`, `maxJoysticksPerPlayer = 100`, `autoAssignJoysticks = true`, `logLevel = 7`. So every connected HID joystick/gamepad is assigned to `Player0`; XInput pads come through XInput.
- Players: `System` (9999999) and `Player0` (runtime id 0, `ReInput.players.GetPlayer(0)`), Player0 default joystick map = category 0 / layout 0, enabled.
- Actions (id, name, type, input behaviour, user-assignable, category): `0 Rudder` axis, behaviour 0 "Default", yes, 0 "Default" · `2 Mouse X`, `3 Mouse Y`, `4 Mouse Scroll` axis, behaviour 0, **no**, category 1 "Mouse Input" ("Replace Unity Mouse Inputs") · `5 Wheel Brake L`, `6 Wheel Brake R` axis, behaviour 2 **"ZeroToOneAxis"**, yes, category 0. Descriptive names: "Rudder" (+ "Rudder+"/"Rudder-"), "Wheel Brake" (+ "Brake"). Map categories: 0 "Default" (user-assignable); layouts: "Default" (0) for joystick, keyboard, mouse, custom controller. `RewiredConsts.Action` in Assembly-CSharp lists the same ids.
- **No joystick maps are defined** (joystickMaps list empty; only one mouse map for actions 2-4). Every joystick therefore starts with a blank map that Rewired creates (`JoystickMap.Blank`: `sourceMapId -1`, category 0, layout 0, `hardwareGuid` = the device's Rewired type GUID). Bindings exist only in the user file.
- `UserDataStore_ConfigNode` (MonoBehaviour path id 147): `isEnabled 1, loadDataOnStart 1, loadJoystickAssignments 1, loadKeyboardAssignments 1, loadMouseAssignments 1, playerPrefsKeyPrefix "RewiredSaveData"`. This is Boundless' copy of Rewired's `UserDataStore_PlayerPrefs` that writes a ConfigNode file instead of the registry.

### How the game uses the three actions [OBS-bin `VehicleInputManager`, `VTRewiredExtensions`, `VRJoystick`, `AeroController`]
- Only when `gameSettings.cfg` has `HARDWARE_RUDDER` > 0 ("Hardware Control Rudder: Use hardware (rudder pedals) for rudder. Overrides other settings.", default off). Read in `Start()` of the vehicle, i.e. at spawn.
- Yaw = `player.GetAxis(0)` replaces the stick twist and the thumbstick rudder; the same value drives nosewheel steering (`SetWheelSteer`) and the stick yaw animation is frozen.
- Brakes: `wheelBrakesBound` = an element map exists for action **5** (the code checks action 5 twice and never 6). If bound, left/right wheel brakes = `VTRWGetAxis(5)` / `VTRWGetAxis(6)` and the throttle trigger only moves the airbrake; otherwise both brakes follow the throttle trigger (changelog Alpha v0.0.15: "Added bindings for left and right wheel brakes (Hardware rudder must be enabled. If bound, throttle trigger will only control airbrake)") [OBS-bin, SRC changelog.txt in the install]. With brakes bound the airbrake low-speed limiter is removed (v1.4.6).
- `VTRWGetAxis` for ZeroToOneAxis actions: `v = GetAxis(id)`; if the **first** element map of the action is inverted → `1 − clamp01(|v|)`, else `clamp01(v)`. A full-range toe-brake axis (−1 released … +1 pressed) therefore only uses half of its travel, and inverted it brakes fully at mid travel. Community fix for Thrustmaster TPR: change that binding's `<axisRange>0</axisRange><invert>true</invert>` to `<axisRange>1</axisRange><invert>true</invert>` (Rewired AxisRange 1 = Positive) [SRC https://steamcommunity.com/app/667970/discussions/1/3270185687336160688 , MadDog 2024-03-13, confirmed by users 2024-03 and 2025-05; why it works is [UNVERIFIED]].
- Rewired combines all element maps bound to an action; the UI enforces one binding per action [INF].

### In-game binding UI [OBS-bin `VRGameSettingsWindow.GoToBindings`, `VRRemapperWindow`, `UIInputBinder`]
Settings › "Bindings" ("turn on physical rudder control, click the bindings button, choose your Hotas for each control listed" [SRC https://steamcommunity.com/app/667970/discussions/0/5287797783032612392/?ctp=3 , 2022-04-19]) lists the user-assignable actions (Rudder, Wheel Brake L, Wheel Brake R). Per row: previous/next controller (joysticks of Player0, shown by Rewired name), **Bind** ("Move the Rudder axis.", Rewired `InputMapper`, `actionRange Full`), **Clear**, invert toggle, live axis bar. Bind first deletes the action from every joystick map → one device per action. **Save** (`SaveAndBackToSettings` → `userDataStore.Save()`) writes the file; leaving without Save keeps the change only in memory. Opening the window calls `userDataStore.Load()` (re-reads the file). No calibration, deadzone, curve or button UI. Button presses are accepted by the InputMapper for a full-axis action (Rewired default `allowButtonsOnFullAxisAssignment`) [INF] → a reader may meet `buttonMaps` entries.

### Per vehicle
- One file and one set of actions for all vehicles; no per-vehicle binds [OBS-bin]. `VehicleInputManager` is referenced by the generic flight code, the AH-94 collective (`AH94CollectiveFunctions.vim`) and the multicrew sync (`MultiCrewControlSender`, `MulticrewAirBrakeController`, `ConnectedJoystickSync`) [OBS-bin]; that all six player vehicles (AV-42C, F/A-26B, F-45A, AH-94, T-55, EF-24G) carry it is [INF] (prefabs not decoded).
- Two-seaters: each local player's own hardware rudder is used and synced (v1.4.6 fixes "With two hardware rudder users ...") [SRC changelog].

## Files
| What | Where |
|---|---|
| Install | Steam `steamapps\common\VTOL VR` [OBS] |
| **Native binds** | `%APPDATA%\Boundless Dynamics, LLC\VTOLVR\SaveData\inputbindings.cfg` = `Environment.SpecialFolder.ApplicationData` + `Boundless Dynamics, LLC\VTOLVR\SaveData` [OBS-bin `PilotSaveManager.newSaveDataPath`; SRC MadDog post above]. Roaming, **not** LocalLow. |
| Legacy binds | `<install>\SaveData\inputbindings.cfg`: read only when the Roaming file is missing or unparsable, then copied to Roaming [OBS-bin `LoadConfigNodeFromFile`]. The install has an empty `SaveData` folder [OBS]. |
| Settings | `...\VTOLVR\SaveData\gameSettings.cfg` (`HARDWARE_RUDDER`, `THUMB_RUDDER`, `TWIST_RUDDER_DEADZONE`, `THUMBSTICK_DEADZONE`, ...), `pilots.cfg` [OBS-bin] |
| Steam Cloud | Auto-Cloud rule in `appcache\appinfo.vdf`: root `WinAppDataRoaming`, path `Boundless Dynamics, LLC/VTOLVR/SaveData`, pattern `*.cfg` → `inputbindings.cfg` and `gameSettings.cfg` are cloud-synced [OBS]. Steam should upload a locally newer file at the next launch [INF]; a cloud conflict dialog is possible if another PC changed it [INF]. |
| Log | `%LOCALAPPDATA%\..\LocalLow\Boundless Dynamics, LLC\VTOLVR\Player.log` (target of `Player.log.lnk` in the install) [OBS]. Contains `Initializing Rewired User Data`, `No Rewired saved data found.  Creating file.` and one line per device `ReWired: Controller Connected Event: Name='<name>' ControllerType='Joystick', ControllerID='<n>'` [OBS-bin `SplashSceneController.SetupRudderDebug`]. |
| BYOJoystick mod binds | `<install>\SaveData\BYOJ_<ShortName>.xml` (`PilotSaveManager.saveDataPath` = `<install>\SaveData`) [SRC BYOJ `ConfigManager.cs`, OBS-bin path] |

## inputbindings.cfg format

### ConfigNode container [OBS-bin `ConfigNode.WriteNode/ParseNode/ParseValueLine/SaveToFile`]
```
RewiredSaveData
{
	{<key>} = {<value>}
	...
}
```
- One root node `RewiredSaveData`, values only, no child nodes. Writer: `name + "\n{"`, per value `"\n\t" + key + " = " + value`, then `"\n" + "\n}\n"`, every `"\n\n"` collapsed to `"\n"`; `File.WriteAllLines` adds one `"\r\n"`. So: **LF line ends, tab indent, UTF-8 without BOM, file ends with `}\n\r\n`**. Empty file the game creates on first start: `RewiredSaveData\n{\n}\n\r\n` (22 bytes, the empty file the game creates).
- Values containing CR/LF are stored with `///n`. If key **or** value contains one of `= { } < > "`, **both** are wrapped in `{}`; every Rewired key contains `=`, so every line is `{key} = {value}`. The parser takes the first top-level `{...}` as key and the second as value (brace depth counted, so JSON braces are fine). Lines without `{` are split at `=`, spaces/tabs removed from the key. An empty value is written as `key = ` without braces (would break a key with `=`; does not occur).
- Values live in a `Dictionary<string,string>`: order = insertion order; a re-saved key keeps its place, new keys are appended [INF Mono Dictionary without removals]. The game never removes keys.
- Parse failure (e.g. empty or truncated file) → `LoadFromFile` returns null → at start `OnInitialize` **creates a new empty file, all bindings lost** [OBS-bin]. A writer must never leave a half-written file.

### Keys [OBS-bin `UserDataStore_ConfigNode.Get*PlayerPrefsKey`]
| Key | Value |
|---|---|
| `RewiredSaveData_ControllerAssignments` | JSON (Rewired TinyJson, no whitespace, fields in declaration order, strings not escaped): `{"players":[{"id":<playerId>,"hasKeyboard":true,"hasMouse":true,"joysticks":[{"instanceGuid":"<guid>","hardwareIdentifier":"<HI>","id":<rewiredJoystickId>},...]},...]}` for System (9999999) and Player0 (0); order of the two players [UNVERIFIED] |
| `RewiredSaveData\|playerName=Player0\|dataType=ControllerMap\|controllerMapType=JoystickMap\|categoryId=0\|layoutId=0\|hardwareIdentifier=<HI>\|hardwareGuid=<G>` | Rewired JoystickMap XML (below) |
| `RewiredSaveData\|playerName=Player0\|dataType=ControllerMap_KnownActionIds\|...same tail...` | comma list of all action ids when saved: `0,2,3,4,5,6` [INF from InputManager action order] |
| `...\|controllerMapType=KeyboardMap\|...\|hardwareIdentifier=<kb>` / `MouseMap` (+ KnownActionIds), also `playerName=System` | keyboard/mouse maps of both players [INF: written by SaveAll for every user-assignable map] — keep |
| `RewiredSaveData\|playerName=<p>\|dataType=InputBehavior\|id=<0\|2>` | InputBehavior XML ("Default", "ZeroToOneAxis") — keep |
| `RewiredSaveData\|dataType=CalibrationMap\|controllerType=Joystick\|hardwareIdentifier=<HI>\|hardwareGuid=<G>` | Rewired CalibrationMap XML, one per joystick (below) — keep |

Maps are keyed by **model** (`hardwareIdentifier` + `hardwareGuid`), not by device instance [OBS-bin].

### JoystickMap XML [OBS-bin Rewired `ControllerMap`/`ControllerMapWithAxes`/`ActionElementMap` serializers, `SerializedObject.ToXmlString`, `SerializationTools.WriteXmlElement`]
`XmlWriter.Create(StringWriter)` with default settings: one line, declaration `encoding="utf-16"` (although the file is UTF-8 — parse it from a string, not from bytes), empty elements as `<x />`, ints and enums as decimal numbers, bools `true`/`false`, GUIDs lower-case `D` format. Root attributes in this order: `dataVersion="2" hardwareGuid hardwareName xmlns:xsi xsi:schemaLocation` then the implied `xmlns`. Children: `sourceMapId, categoryId, layoutId, name, hardwareGuid, enabled, buttonMaps, axisMaps`; each `ActionElementMap`: `actionCategoryId, actionId, elementType, elementIdentifierId, axisRange, invert, axisContribution, keyboardKeyCode, modifierKey1, modifierKey2, modifierKey3, enabled`. The element order is confirmed by the community TPR snippet `<axisRange>0</axisRange><invert>true</invert>` [SRC thread above].

Exact example (built with that call sequence; the test fixture holds the full file):
```xml
<?xml version="1.0" encoding="utf-16"?><JoystickMap dataVersion="2" hardwareGuid="00000000-0000-0000-0000-000000000000" hardwareName="MFG Crosswind V2" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:schemaLocation="http://guavaman.com/rewired http://guavaman.com/schemas/rewired/1.1/JoystickMap.xsd" xmlns="http://guavaman.com/rewired"><sourceMapId>-1</sourceMapId><categoryId>0</categoryId><layoutId>0</layoutId><name /><hardwareGuid>00000000-0000-0000-0000-000000000000</hardwareGuid><enabled>true</enabled><buttonMaps /><axisMaps><ActionElementMap><actionCategoryId>0</actionCategoryId><actionId>0</actionId><elementType>0</elementType><elementIdentifierId>2</elementIdentifierId><axisRange>0</axisRange><invert>false</invert><axisContribution>0</axisContribution><keyboardKeyCode>0</keyboardKeyCode><modifierKey1>0</modifierKey1><modifierKey2>0</modifierKey2><modifierKey3>0</modifierKey3><enabled>true</enabled></ActionElementMap><ActionElementMap>...<actionId>5</actionId>...<elementIdentifierId>0</elementIdentifierId>...</ActionElementMap><ActionElementMap>...<actionId>6</actionId>...<elementIdentifierId>1</elementIdentifierId>...</ActionElementMap></axisMaps></JoystickMap>
```
In the file this sits on one line: `\t{RewiredSaveData|playerName=Player0|dataType=ControllerMap|controllerMapType=JoystickMap|categoryId=0|layoutId=0|hardwareIdentifier=WindowsRawInputMFGCrosswindV226160a3816d0-0000-0000-0000-504944564944|hardwareGuid=00000000-0000-0000-0000-000000000000} = {<?xml ...</JoystickMap>}` (full file: `engine/tests/fixtures/vtolvr/native/inputbindings.cfg`).

| Field | Values |
|---|---|
| `actionId` | 0 Rudder, 5 Wheel Brake L, 6 Wheel Brake R |
| `actionCategoryId` | 0 |
| `elementType` | 0 axis (in `axisMaps`), 1 button (in `buttonMaps`) |
| `elementIdentifierId` | element of the device's Rewired hardware definition (next section) |
| `axisRange` | 0 Full, 1 Positive half, 2 Negative half |
| `invert` | the in-game invert toggle |
| `axisContribution` | 0 Positive, 1 Negative (button → axis) |
| `keyboardKeyCode`, `modifierKey1..3` | 0 (keyboard maps only) |
| `sourceMapId` | −1 for a blank (game-created) map |
| `name` | `<name />` in a blank map (`_name` null); a map loaded and saved again may become `<name></name>` [UNVERIFIED] |
| `enabled` (map) | `true` expected (player default map enabled) [INF] |

Not representable natively: deadzone, saturation, curves (only in the CalibrationMap, no UI), buttons/hats for anything useful, modifiers, keyboard.

### Device identification [OBS-bin Rewired_Windows Raw Input source, `MiscTools`, `InputTools`]
- `hardwareIdentifier` = `Regex.Replace("Windows" + "RawInput" + <productName> + <productId decimal> + <productGuid>, "\s*", "")`, where productName = HID product string (`HidD_GetProductString`, `\x1A` removed, "Unknown" if empty; for Bluetooth devices the Bluetooth name), productId = USB PID as decimal, productGuid = `PPPPVVVV-0000-0000-0000-504944564944` (lower-case hex, same as DirectInput's guidProduct). Example: MFG Crosswind V2 (16D0:0A38, product string "MFG Crosswind V2") → `WindowsRawInputMFGCrosswindV226160a3816d0-0000-0000-0000-504944564944`; T.Flight Rudder Pedals (044F:B679, "T-Rudder") → `WindowsRawInputT-Rudder46713b679044f-0000-0000-0000-504944564944` [INF from the code; first real file confirms].
- `hardwareGuid` = Rewired controller-type GUID: `00000000-0000-0000-0000-000000000000` for every device Rewired does not recognize; otherwise the `controllerGuid` of one of the 166 built-in definitions in `sharedassets0.assets` . Flight devices among them: Thrustmaster T.Flight Rudder Pedals `bdd20bc1-5619-4361-91e0-d652ce750d73` (044F:B679, names "T-Rudder"), Saitek/Logitech Pro Flight Rudder Pedals `01665f95-8b2d-42d0-8d54-3bc9b0d16b1d` (06A3:0763/0764/0765), CH Pro Pedals `3049c96e-9ffd-4088-a136-e92a4d92e2b1` (068E:00F2), 3DRudder, Warthog stick/throttle, T.16000M, TWCS, T.Flight HOTAS X/4, X52/X52 Pro/X55/X56, CH Fighterstick/Pro Throttle, Logitech Extreme 3D Pro, VKB Gladiator MkII (231D:0121). Not recognized (→ Unknown): Thrustmaster TPR, MFG Crosswind, VKB T-Rudder, Virpil, Winwing, Logitech G PRO pedals, Slaw, VPforce, MOZA, newer VKB.
- `instanceGuid` (ControllerAssignments only) = first 16 bytes of SHA-1(UTF-8 HID device instance id) as `new Guid(byte[])` — **not** the DirectInput instance GUID. The instance id string is from SetupAPI (`HID\VID_xxxx&PID_xxxx\...`) [INF]. `id` = Rewired joystick id of that session.
- Identical devices (same model) produce the same key → they share one saved map and both drive the bound action [OBS-bin key format, INF behaviour]. Per-instance binds are impossible natively.
- Renaming a device (VKB/Virpil configurators change the HID product string) changes `hardwareIdentifier` → old map orphaned [INF].
- ControllerAssignments is (re)written at every start with joysticks connected and on every connect/disconnect, so after one start the file lists `hardwareIdentifier` for every connected device [OBS-bin `OnInitialize`, `OnControllerConnected`].

### Element ids
Unknown controller (the Unknown Controller definition) [OBS-bin]:
| Rewired element | id | JoyPro |
|---|---|---|
| `Axis 0` … `Axis 31` | 0 … 31 | n-th HID axis of the device, see below |
| `Button 0` … `Button 127` | 32 … 159 | `JOY_BTN<n>` → id 31+n |
| `Hat h Up/Right/Down/Left` | 160 + 4h + {0,1,2,3} | `JOY_BTN_POV<h+1>_U/R/D/L` |
| `Hat h Up Right/Down Right/Down Left/Up Left` | 224 + 4h + {0,1,2,3} | `..._UR/DR/DL/UL` (16 hats) |
- Raw Input numbers the axes in the order of the device's HID value caps (no sorting found in the HID joystick class) [OBS-bin]. For the usual descriptors (X, Y, Z, Rx, Ry, Rz, Slider, Dial in ascending usage) `Axis n` = n-th **present** axis of X, Y, Z, Rx, Ry, Rz, Slider0, Slider1, i.e. DirectInput order with missing axes skipped: MFG Crosswind (X, Y, Rz) → Axis 0 = X, 1 = Y, 2 = Rz [INF, UNVERIFIED — check with one bind]. Devices with more than 8 axes or unusual descriptors may differ (Raw Input sees all HID axes, DirectInput max 8).
- Recognized pedals (TFRP, Saitek, CH): 0 `Left Pedal`, 1 `Right Pedal`, 2 `Slide` (rudder). Typical DirectInput layout X = left toe, Y = right toe, Rz (CH: Z) = rudder [INF].
- Rewired applies the hardware definition's default calibration; the Unknown map's axes carry `axisDeadZone 0.1` (the pedal definitions 0.05) [OBS-bin raw data, meaning INF]. Users cannot change it in VTOL VR.

### CalibrationMap XML (keep untouched) [OBS-bin `CalibrationMap`/`AxisCalibration` serializers]
`<?xml version="1.0" encoding="utf-16"?><CalibrationMap dataVersion="4" xmlns:xsi="..." xsi:schemaLocation="http://guavaman.com/rewired http://guavaman.com/schemas/rewired/1.3/CalibrationMap.xsd" xmlns="http://guavaman.com/rewired"><axes><AxisCalibration><enabled/><deadZone/><calibratedZero/><calibratedMin/><calibratedMax/><invert/><sensitivity/><applyRangeCalibration/><sensitivityType/><sensitivityCurve/></AxisCalibration>...` — one `AxisCalibration` per hardware axis; Rewired rejects a map whose axis count differs ("Calibration map data does not match the number of elements in the hardware!"). The `sensitivityCurve` (AnimationCurve) encoding was not decoded [UNVERIFIED]. Possible later use: edit `<deadZone>` of one axis inside a game-written entry.

## gameSettings.cfg [OBS-bin `GameSettings.SaveGameSettings`]
Same container format, root `GAMESETTINGS`, child node `PLAYAREA`, values `RADIO_MUSIC_PATH`, `WINGMAN_VOICES` and one line per setting written as float with `ToString("G", en-US)`: `\tHARDWARE_RUDDER = 1` (bool settings: the game writes 1 / -1, value > 0 = on). Keys without special characters are not braced. Loaded once at start (cached); written when the player saves settings.

## When the game reads and writes inputbindings.cfg [OBS-bin]
- Start (splash scene, Rewired init): read; if missing/unparsable create `RewiredSaveData { }`; load assignments, maps and calibration for connected joysticks; **write** (ControllerAssignments) if any joystick is connected.
- Joystick connected: load its maps; write assignments. Joystick about to disconnect: its maps and calibration are copied into memory; after disconnect the whole file is written.
- Bindings window opened: file re-read. **Save** in the window: everything written (all players' maps, input behaviours, calibration of all joysticks, assignments).
- Every write saves the whole in-memory node → edits made while VTOL VR runs are lost or mixed. **The game must be closed** (process `VTOLVR`).

## Action catalog
- `actions.tsv`: the three native actions (id = Rewired action name; file stores 0/5/6).
- Refresh (`engine vtolvr-native refresh-db [<path>]`, GUI "DB refresh"): reads the Rewired InputManager from
  `VTOLVR_Data\level0` of the install (or `--path` with the game folder, `VTOLVR_Data` or the file), read-only. Layout
  in 1.13.4: actions = int32 count, then id, name, type, descriptiveName, positive/negative names, int32 behavior id,
  int32 userAssignable, int32 category id; categories = name, descriptiveName, tag, int32 id, int32 userAssignable
  (Nuclear Option's Rewired has 6 action ints and 3 category ints; `RewiredLayout` in `NuclearOptionIntegration.cs`).
  Found by the first action, `Rudder` (id 0). Adds the user-assignable actions the catalog lacks, with the Rewired id
  as fifth column (import and export use it), title = descriptive name, category = that of the known actions of the
  same Rewired category. Run on 1.13.4 (2026-10-10): 6 actions (Rudder, Mouse X/Y/Scroll, Wheel Brake L/R), 3
  user-assignable, 0 new.
- `functions.tsv`: `rudder` ← Rudder, `wheelbrakeleftanalog` ← Wheel Brake L, `wheelbrakerightanalog` ← Wheel Brake R (plane `VTOLVR`).
- Flight stick, throttle, trim, weapons, countermeasures, radar/TGP slew, gear, flaps, hook, NWS, autopilot, MFD, VR recentering: **not bindable natively** (VR controllers on the virtual cockpit; nosewheel steering follows the rudder axis). Available through BYOJoystick (below).

## BYOJoystick mod (optional second target)

### License and source (checked 2026-10-12)
- **No license.** github.com/sicsix/BYOJoystick has no LICENSE file and the GitHub API reports `license: null`; the
  README and the Workshop description name none either. So the code and the mod are "all rights reserved" by the
  author (sicsix): free to use, but nobody may redistribute it or parts of it without permission. That includes its
  target list (433 names taken from the mod's code): **JoyPro does not ship it** (`DB\VTOLVR\actions.tsv`,
  `seats.tsv` are left out of releases).
- **Official binary source: the Steam Workshop only**, item 3276352374 "BYO Joystick" (version 1.3.2, updated
  2024-09-08, 1.435 MB as Steam lists it), for the free "VTOL VR Mod Loader" (Steam app 3018410; the item's
  `consumer_app_id`). The GitHub repository has tags but no release files. The Workshop item is SteamPipe content
  (Steam's `GetPublishedFileDetails` gives no file address), so only the Steam client can download it.

### One-click install (GUI: VTOL VR card > Install BYOJoystick; CLI `engine vtolvr mod status|prepare`)
1. JoyPro shows what the mod is, the official source (Workshop page, source code), the size Steam lists and the license
   situation (`VtolVrMod.LicenseText`), and the Mod Loader and mod status of this PC.
2. Only after the tick "I have read what BYOJoystick is and its license situation, and I want to install it from the
   Steam Workshop" is **Install BYOJoystick** enabled (the tick is used up by one install).
3. `vtolvr mod prepare --accept-license` backs up the user's `<install>\SaveData\BYOJ_*.xml` into the backup store
   (`engine backups list --game VTOLVR`) and returns the Steam addresses to open: `steam://install/3018410` when the
   Mod Loader is missing, `steam://url/CommunityFilePage/3276352374`. The GUI opens them; the user presses Subscribe.
4. Steam downloads the item, checks it against the Workshop's content manifest (Valve's signed SteamPipe manifests; the
   author publishes no checksum or signature of his own) and puts it into
   `<library>\steamapps\workshop\content\3018410\3276352374`, where the Mod Loader loads it. JoyPro writes no mod file.
5. **Check again** (`vtolvr mod status`) finds the item and `BYOJoystick.dll` in it, shows its SHA-256, size and file
   version and how many targets JoyPro reads, then runs `vtolvr refresh-db`.

### Target list from the installed mod
`engine vtolvr refresh-db` (no path, or `--path` with `BYOJoystick.dll` or its folder) reads the targets from the
installed DLL, read only (`engine/Games/ClrMetadata.cs`: PE sections, .NET metadata tables, `#US` strings and the IL of
each method; the DLL is not loaded or run; `VtolVrMod.ReadTargets`): every vehicle manager class has a `ShortName`
property (the seat) and adds its targets with helper calls `<Category><Axis|AxisC|Button>("<target>", ...)`
(`FlightAxisC`, `LightsButton`, ...): the first string argument is the target, the helper gives the category and the
input type (`AxisC` = `axis_centered`). The base manager's `StaticButton("Reset VR Position")` and the `Modifier` belong
to every seat. New targets come in with the mod's category; known ones keep title and category and gain the seats they
lack (`seats.tsv`). The user's `BYOJ_*.xml` files are read as before on top. A newer mod that builds its targets
differently gives a warning and the files are read alone.

Tested with a stand-in only: `engine/tests/fixtures/vtolvr/byoj-fake/FakeByojMod.cs` (JoyPro's own code in the shape
the reader expects, made-up "Fake ..." targets) is compiled by the smoke test; the real mod is not installed on the dev
PC (2026-10-12), so the first real install should be checked against the 433 targets of the development list.
`JOYPRO_VTOLVR_MOD_DIR` points the engine at a folder standing for the Workshop item (tests).

Source: https://github.com/sicsix/BYOJoystick (last commit ffc106e 2024-09-08), Workshop https://steamcommunity.com/sharedfiles/filedetails/?id=3276352374 (v1.3.2, "VTOL VR v1.10.1+", requires "VTOL VR Mod Loader" [SRC https://catalogue.smods.ru/archives/361171]). Whether it still loads in 1.13.4 is [UNVERIFIED].
- Reads devices with SharpDX **DirectInput** (types Joystick, Gamepad, FirstPerson, Flight, Driving, Supplemental); `JoystickId` = `DeviceInstance.InstanceGuid` = JoyPro's DirectInput instance GUID [SRC `BYOJ.cs`].
- One file per vehicle seat: `<install>\SaveData\BYOJ_<ShortName>.xml`, ShortName ∈ `AV42C, FA26B, F45A, AH94Front, AH94Rear, T55Front, T55Rear, EF24GFront, EF24GRear` [SRC `Managers\*.cs`, `ConfigManager.cs`].
- Format: `XmlSerializer(typeof(Bindings))` into a `StreamWriter` (UTF-8 no BOM, 2-space indent) [SRC]; fixture `engine/tests/fixtures/vtolvr/byoj/BYOJ_FA26B.xml` (built with .NET's XmlSerializer; Mono may order `xmlns:xsi`/`xmlns:xsd` differently [UNVERIFIED], irrelevant for parsing):
```xml
<?xml version="1.0" encoding="utf-8"?>
<Bindings xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <KeyboardBindings>
    <KeyboardBinding>
      <Target>Landing Gear Toggle</Target>
      <Key>G</Key>
      <ModifierKey>None</ModifierKey>
    </KeyboardBinding>
  </KeyboardBindings>
  <JoystickBindings>
    <JoystickBinding>
      <Target>Joystick Pitch</Target>
      <JoystickId>8a3c7e20-6b1f-11ef-8001-444553540000</JoystickId>
      <Input>Y</Input>
      <RequiresModifier>false</RequiresModifier>
      <Invert>true</Invert>
      <Deadzone>0.02</Deadzone>
      <POVDirection>None</POVDirection>
    </JoystickBinding>
  </JoystickBindings>
</Bindings>
```
- `Input` (enum `FriendlyOffsetName` over DirectInput `JoystickOffset`): `X Y Z RX RY RZ` = `JOY_X..JOY_RZ`, `S1 S2` = `JOY_SLIDER1/2`, `PoV1..PoV4` (+ `POVDirection` `Up|Right|Down|Left`; the enum has diagonals but they never fire), `B1..B128` = `JOY_BTN1..128` [SRC `JoystickBinding.cs`].
- Axis value = raw/65535 (DirectInput default range, not set by the mod [INF]); `Invert` flips (1 - value). `Deadzone` means different things per target (master source, see "Every setting the game offers"): a cut without rescale only for SOI Slew X/Y and Articulate Tilt; on the stick and throttle/power/collective a multi-crew takeover threshold that does not change the response; unused elsewhere. Axis actions need axis inputs, button actions need buttons/POV (an axis on a button action never fires).
- Modifier: a binding with `<Target>Modifier</Target>` on a joystick button; bindings with `RequiresModifier=true` act only while it is held; button bindings without it are inactive while it is held; axis bindings without it keep working unless the same axis also has a `RequiresModifier` binding. Each modifier press overwrites one shared flag, so one modifier per vehicle file is the safe use [SRC `JoystickBinding.OnModifierChange/OnStateChange`, INF].
- Keyboard: `Key`/`ModifierKey` = UnityEngine `KeyCode` names (`A`, `Alpha1`, `Keypad5`, `F1`, `Space`, `Return`, `LeftShift`, `LeftControl`, `LeftAlt`, ...), one optional modifier key [SRC `KeyboardBinding.cs`].
- Several bindings per target are allowed (lists) [SRC].
- Read at mod start; written only by the mod's desktop UI "Save" (UI "Load" re-reads). Targets unknown to the vehicle are dropped on load (logged) and disappear at the next UI save [SRC `ConfigManager.LoadBindings`].
- Catalog: `DB/VTOLVR/actions.tsv` + `seats.tsv` (433 targets, 16 axes, 6 centred axes, 411 buttons; per-vehicle membership: AV42C 285, FA26B 264, F45A 175, AH94Front 220, AH94Rear 228, T55Front 246, T55Rear 264, EF24GFront 215, EF24GRear 145 incl. `Modifier` and `Reset VR Position`). `DB/Functions/VTOLVR.tsv`: 156 slot mappings (stick/throttle/collective/tilt axes, gear, flaps, hook, launch bar, brake lock, SAS, autopilot, master arm, trigger, weapon cycle, countermeasures, engines, APU, radar, NVG, canopy, jettison, lights, PTT, SOI slew/zoom, trims, keypad, all MFD OSBs). Not mapped (no JoyPro slot): eject, wing fold, seat height, doors, rotor brake/fold, sweep, HUD/HMD/MFD power and brightness, music, radio volume, VR recenter.
- Catalog refresh (`engine vtolvr refresh-db [<path>]`, GUI "DB refresh"): the targets exist only as string literals
  in the mod's compiled `BYOJoystick.dll` (`Managers\<Vehicle>Manager.cs`: `FlightAxisC("Joystick Pitch", ...)`,
  `FlightButton(...)`, one `Create<Category>Controls` method per category); the game's files do not list them
  (checked: `VTOLVR_Data\Managed\*.dll` contain no target name). The refresh reads the installed mod (see "Target list
  from the installed mod" above) and then the user's own `SaveData\BYOJ_<seat>.xml` files (no path: the install's
  SaveData; `--path` with the SaveData folder, a file in it or the VTOL VR folder): the mod drops targets a vehicle does
  not have when it loads a file, so every target there exists in that seat. Targets found only in the files come in
  with title = target, category `Misc`, axis when bound to axes only (else button); `seats.tsv` gets their seats and
  the seats a known target was found in; files of unknown seats are reported and skipped. Run on a test PC
  (2026-10-12): neither the mod nor a `BYOJ_*.xml` is there, so the refresh says to install the mod.
- Older mods: VTOLVRPhysicalInput (evilC/solidshadow1126, last 2022, XML `Mappings` by device name, legacy Mod Loader) and TriquetraInput 2 (muskit, 2024-06, `<install>\SaveData\triquetrainput.xml`, unmaintained) [SRC github.com/solidshadow1126-zz/VTOLVRPhysicalInput, github.com/muskit/TriquetraInput2]. Not worth supporting.

## How JoyPro should export (native)
1. **Find**: `%APPDATA%\Boundless Dynamics, LLC\VTOLVR\SaveData\inputbindings.cfg` (`Environment.SpecialFolder.ApplicationData`). If missing: tell the user to start VTOL VR once with the devices connected (the game creates it and lists the devices); do not create it from scratch in version 1. Never write `<install>\SaveData\inputbindings.cfg`.
2. **Refuse** while process `VTOLVR` runs. Back up to `inputbindings.cfg.jpbak`. Write atomically (temp file + replace).
3. **Parse** with the ConfigNode rules above (keys in insertion order). Parse each XML value from a string (ignore `encoding="utf-16"`).
4. **Device → key**: for each JoyPro device to export, compute `hardwareIdentifier` from HID product string + VID/PID; prefer an exact match in `ControllerAssignments.joysticks[].hardwareIdentifier` (game-written). `hardwareGuid`: take it from an existing `ControllerMap` or `CalibrationMap` key with that identifier; else the Rewired definitions by VID:PID (TFRP/Saitek/CH pedals ...), else `00000000-...`. Skip (warn) devices JoyPro cannot match or whose identifier the file's assignments contradict. Warn when two connected devices share the identifier (shared map).
5. **Element**: Unknown map → axis index per the Raw Input order above (warn [UNVERIFIED] until confirmed); recognized pedals → 0/1/2 by role. Only axes; skip buttons/hats with a warning.
6. **Write** only `ActionElementMap`s with `actionId` 0/5/6 inside `<axisMaps>` (and drop any 0/5/6 entries in `<buttonMaps>`) of `playerName=Player0|...|controllerMapType=JoystickMap|categoryId=0|layoutId=0` values, as text edits that keep the rest of the XML byte-for-byte; new entries use the exact element order and formatting above (`actionCategoryId 0`, `elementType 0`, `axisRange 0`, `invert` from JoyPro `Inverted`, `axisContribution 0`, zeros, `enabled true`). For a device without a map key, append a new value pair with the blank-map XML (`sourceMapId -1`, `<name />`) plus its `ControllerMap_KnownActionIds` = `0,2,3,4,5,6`, using `{key} = {value}` lines. Keep CRLF only at the end (`}\n\r\n`), LF elsewhere, UTF-8 without BOM.
7. **Clean/nuke**: the three actions are all flight controls and the game uses every bound device, so remove 0/5/6 element maps from every other JoystickMap value in the file (connected or not). Never touch other actions, KeyboardMap/MouseMap/InputBehavior/CalibrationMap/ControllerAssignments values or the System player.
8. **One binding per action** (the UI's rule); warn when the profile has several.
9. **Brakes**: write as full axis; JoyPro `Inverted` → `<invert>`; the TPR-style `axisRange 1` is the explicit `vtolnative` option `axisRange` (implemented, with a half-travel warning for Full on devices Rewired does not know). Deadzone/saturation/curvature: not exportable.
10. **HARDWARE_RUDDER**: if `gameSettings.cfg` has `HARDWARE_RUDDER = 0` (or lacks it), warn ("enable Settings › Hardware Control Rudder"); optionally (with consent) change that one line to `= 1` keeping every other byte.
11. **Import**: Player0 JoystickMap values → binds for 0/5/6 (axis maps → JOY axis by the element tables; button maps → warning).

## How JoyPro could export (BYOJoystick)
Write `<install>\SaveData\BYOJ_<ShortName>.xml` for each vehicle seat whose catalog has the target (`seats.tsv`); merge: keep keyboard bindings and bindings of devices JoyPro does not export, replace JoyPro-device bindings of managed targets; refuse while `VTOLVR` runs; DirectInput instance GUID as `JoystickId`; POV diagonals, button-on-axis and axis-on-button rows skipped with a warning; `Deadzone` from the `byoj` tuning by target (takeover threshold or cut, see below); JoyPro modifiers only as one `Modifier` button per file + `RequiresModifier`. Keyboard only with `--include-keyboard` (KeyCode names).

## Every setting the game offers (axes, controls, haptics)
Research notes: private; BYOJoystick master source (`Bindings/JoystickBinding.cs`, `Controls/CJoystick.cs`, `CThrottle.cs`, `CSOI.cs`, `CCollFuncs.cs`, `Managers/*.cs`); the game's `GameSettings` class.

| Where | Setting | File | JoyPro |
|---|---|---|---|
| BYOJ | Invert | `<Invert>` | relation invert |
| BYOJ | Deadzone on Joystick Pitch/Roll/Yaw | `<Deadzone>` | `byoj` model **`takeover`**: not a dead zone (the stick is read with `GetAsFloatCenteredNoDeadzone`). Only in multiplayer multi-crew: the deflection from the centre the HOTAS needs before it takes the stick from the other crew member (`max(0.005, value)`, half of it while held). The mod stores Joystick Yaw's value as the roll threshold and Roll's as the yaw threshold (swapped). |
| BYOJ | Deadzone on Throttle, Power, Collective | `<Deadzone>` | `takeover`: only in multiplayer multi-crew, the change needed before the HOTAS takes the throttle (same rule) |
| BYOJ | Deadzone on SOI Slew X/Y, Articulate Tilt | `<Deadzone>` | `byoj` model **`cut`**: `|v| < value -> 0`, else `v` unchanged (no rescale) |
| BYOJ | Deadzone on every other axis (knobs, Tilt, brakes axis, sweep) | `<Deadzone>` | unused by the mod: JoyPro writes 0 for new binds and keeps the file's value on binds it replaces |
| native | Rudder / Wheel Brake L/R invert | `<invert>` | relation invert |
| native | toe brake axis range (no UI) | `<axisRange>` 0 Full, 1 Positive | `vtolnative` model **`axisRange`** (brakes only). Full on a device Rewired does not know gives a warning: the game reads a brake as 0..1, so a -1..1 pedal brakes only on the second half (inverted: fully at mid travel). 1 = the community TPR fix (with Invert); why it works is unverified. |
| native | only Wheel Brake R bound | | warning: the game takes the brakes from the hardware only when action 5 (L) is bound |
| native | Rewired calibration (dead zone 0.1 on unknown devices, sensitivity Multiplier/Power/Curve, calibrated min/max) | `CalibrationMap` value | **not settable**: no UI, the `sensitivityCurve` encoding is not decoded and Rewired rejects a map whose axis count differs; the survey found no safe file to write. Documented only. |
| Settings > Controls | Hardware Control Rudder, Thumb Control Rudder, Thumbstick Mode, Toggle Grip | `gameSettings.cfg` `HARDWARE_RUDDER` etc., `1` on / `-1` off | game-wide settings (`engine ffb set --device * --game VTOLVR --param HARDWARE_RUDDER=true`) |
| Settings > Controls | Twist Rudder Deadzone, Thumbstick Deadzone (VR controllers, 0..90 %, default 3) | `TWIST_RUDDER_DEADZONE`, `THUMBSTICK_DEADZONE` | game-wide settings, in % |
| Settings > Controls | Flight Control Haptics 0..100 %, Overall Haptics 0..200 % (VR controller vibration; there is no joystick force feedback) | `CONTROL_HAPTICS`, `OVERALL_HAPTICS` | game-wide settings, in % |

`gameSettings.cfg` format (`GameSettings.SaveGameSettings`, OBS-bin): root `GAMESETTINGS`, one `\tKEY = value` line per
setting (`ConfigNodeUtils.WriteObject(float)`), on/off settings stored as 1 / -1 (`SetBoolValue`), floats clamped to
their range on load, a `PLAYAREA` child node, LF lines, `}\n\r\n` at the end. The vtolvr-native export sets the keys in
place, adds a missing key after the last root value, never creates the file (start the game once), backs it up and
writes atomically; the file is Steam Cloud synced. A profile with these settings but no native binds still exports them
(settings only, inputbindings.cfg untouched). The BYOJoystick export does not write `gameSettings.cfg` (it lives in
Roaming, next to inputbindings.cfg).

Axis models (`engine\AxisModels\VtolVr.cs`): `byoj` previews the linear response (the slew's cut when its `slewAxis`
switch is 1; an import sets it for slew binds); its fit from the base gives the base dead zone to both `takeover` and
`cut` (what JoyPro wrote before for centred targets), 0 for sliders. `vtolnative` previews the rudder as linear and a
brake (Slider) through `VTRWGetAxis` (axisRange 1 assumed to map the whole travel to 0..1, inferred from the fix).
Import: BYOJ `<Deadzone>` becomes `takeover`/`cut` of `game:VTOLVR` by target (no longer a relation dead zone); native
`axisRange` becomes `game:VTOLVRNative` tuning.
## Open questions (one real start needed)
1. The real `hardwareIdentifier` of the test-PC devices (product string source, PID decimal, GUID case) and the JSON player order.
2. `Axis n` ↔ DirectInput axis for an unknown device (e.g. MFG Crosswind: expected rudder Rz = Axis 2, toe brakes X/Y = Axis 0/1).
3. Exact blank-map XML the game writes (`<name />`, `enabled`, KnownActionIds string, which Keyboard/Mouse/InputBehavior/Calibration keys appear) — to replace the constructed fixture with a real one.
4. Whether a JoyPro-created map key for a device the game has not saved yet is loaded (expected yes: lookup by key at connect).
5. Brake direction of the test-PC pedals and whether invert or `axisRange 1` gives full travel.
6. Steam Cloud behaviour after an offline edit (upload vs conflict prompt).
7. BYOJoystick compatibility with 1.13.4 (only if JoyPro wants that target).

**What the user should do once** (VR headset needed; VTOL VR has no desktop mode): connect all sticks/pedals, start VTOL VR, in the main menu open **Settings**, switch on **Hardware Control Rudder**, press **Bindings**, for *Rudder* select the MFG Crosswind with the arrows, press **Bind** and push the rudder; do the same for *Wheel Brake L* (left toe) and *Wheel Brake R* (right toe); press **Save**, then press OK in Settings and **quit the game**. Then hand JoyPro these three files: `%APPDATA%\Boundless Dynamics, LLC\VTOLVR\SaveData\inputbindings.cfg`, `...\SaveData\gameSettings.cfg`, `%USERPROFILE%\AppData\LocalLow\Boundless Dynamics, LLC\VTOLVR\Player.log`.

## Pitfalls
- File is in Roaming and cloud-synced; LocalLow only has the log.
- A broken file is silently replaced by an empty one at the next start.
- Device identity is the HID product string + PID + VID GUID, not the DirectInput instance GUID; identical devices share one map.
- `wheelBrakesBound` only checks Wheel Brake **L**: binding only the right brake leaves both brakes on the throttle trigger.
- Rudder/brakes do nothing unless `HARDWARE_RUDDER` is on; the setting is read at vehicle spawn.
- The XML declares UTF-16 inside a UTF-8 file.
