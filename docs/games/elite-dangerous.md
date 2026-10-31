# Elite Dangerous (live 4.x client: Odyssey and Horizons-live)

Tags: **[OBS]** seen in real files on a test PC · **[OBS-bin]** strings in `EliteDangerous64.exe` 2026.09.03 · **[SRC]** official files shipped with the game (`ControlSchemes\Help.txt`, `DeviceButtonMaps\Readme.txt`) · **[SRC-3p]** third-party tools/forums (EDRefCard, marwatk/elite-dangerous-bindings, Steam/Frontier threads) · **[INF]** inferred · **[UNVERIFIED]** needs a check with the game. Research 2026-10-01; the game was not started.

Integrated as `engine elite` (experimental; Ship preset, edited in place as described in "How JoyPro should export"; half-axis binds and modifiers stay on clean). Research helpers: `C:\Temp\jp2-elite\elite_catalog.py` (catalog), `elite_user_binds.py` (bind summary).

## Install and files
- Install found: Steam app 359320 at `<SteamLibrary>\steamapps\common\Elite Dangerous` (via `HKLM\...\Uninstall\Steam App 359320\InstallLocation`), product folder `Products\elite-dangerous-odyssey-64`, `VersionInfo.txt` 2026.09.03.332841. The game last ran on 2025-08-20 (`Logs\netLog.*`). [OBS]
- The user folder is the same for every store (`DeviceButtonMaps\Readme.txt`: "on windows this can be found here %localappdata%\Frontier Developments\Elite Dangerous\Options\Bindings") [SRC]; Frontier-launcher and Epic installs have the same `Products\<product>` layout [INF].

| Path | Content |
|---|---|
| `%LOCALAPPDATA%\Frontier Developments\Elite Dangerous\Options\Bindings\<Preset>.<Major>.<Minor>.binds` | user presets; exe format `%s.%u.%u.%s` [OBS-bin]; test PC: `Custom.4.2.binds` (66 351 B) [OBS] |
| `...\Bindings\StartPreset.4.start` | active preset per control category (below) [OBS] |
| `...\Bindings\StartPreset.start` | one-line form, used by the legacy 3.8 client for `<Preset>.3.0.binds` [INF; 3.0 file names: SRC-3p marwatk]; the 4.x exe still has the name because the other option folders use it (`Options\Audio\StartPreset.start` = `Custom`, `Graphics` = `High`) [OBS] |
| `...\Bindings\BindingLoadingErrors.log` | written when a preset fails to load ("There where errors when loading preset file:", "Missing devices:") [OBS-bin]; absent on a test PC = last load was clean [OBS] |
| `...\Bindings\DeviceButtonMaps\<MappingName>.buttonMap` | display names for buttons only (`<Root><Joy_1>B1</Joy_1></Root>`) [SRC Readme] |
| `<product>\ControlSchemes\*.binds` | 30 stock presets (`KeyboardMouseOnly`, `SaitekX56`, `ThrustMasterHOTASWarthog`, `GenericJoystick`, `Empty`, ...); `PresetName` = file stem, `SortOrder="0"`, no version attributes except `1.8` on two files, 26 of 30 with UTF-8 BOM [OBS] |
| `<product>\ControlSchemes\DeviceMappings.xml` | symbolic device names → VID/PID (below) [OBS] |
| `<product>\ControlSchemes\Help.txt` | key, mouse, pad and joystick key names [SRC] |

`DeviceDefaults.xml` in the product folder is the graphics-card table, not input. The binding definitions (categories, defaults) are not in loose files, and JoyPro does not read the game's packed data (`Win64\00..0f\<sha1>`, no documented format); the element names come from the stock `.binds` presets and the `.binds` the game writes for the user. [OBS]

### StartPreset.4.start
Four preset names, LF-separated, no trailing newline. Test PC: `Custom`, `Custom`, `KeyboardMouseOnly`, `KeyboardMouseOnly` (49 B). [OBS]
Line order = **General, Ship, SRV, OnFoot**: the exe has the binding flags `HiddenInGeneralControls`, `HiddenInShipControls`, `HiddenInSRVControls`, `HiddenInOnFootControls` and telemetry `&GeneralScheme= &ShipScheme= &SRVScheme= &OnFootScheme=` in that order [OBS-bin]; Odyssey's controls menu has one preset drop-down each for General, Ship, SRV and On Foot [SRC-3p]. Each category takes its elements from its own preset file, so on a test PC SRV and on-foot use the stock `KeyboardMouseOnly` scheme and the SRV/on-foot elements in `Custom.4.2.binds` are inactive (they equal the stock values). [INF]

### Versions and when the game writes
- Root attributes `MajorVersion`/`MinorVersion` match the file name. 4.x = live client (Odyssey and Horizons-live), 3.0 = legacy 3.8 client. [OBS, SRC-3p]
- On a version bump the game writes a new versioned file and keeps the old one: `Options\Player\Custom.4.1.misc` (2025-08-19) and `Custom.4.2.misc` (2025-08-20) coexist. Which `.binds` version it loads when only an older one exists is [UNVERIFIED] (expected: newest ≤ its own).
- `Custom.4.2.binds` and `StartPreset.4.start` have the same timestamp (2025-08-14 00:28:34) and were not touched by the sessions of 2025-08-18..20 → the game writes them only when the player saves changes in Options > Controls, not on start or exit. [OBS] Changes in the controls menu are saved into the preset `Custom` [INF; exe string "Custom preset is not in a writable directory", OBS-bin]. The exe also has `.backup` and `Error=AccessDenied/SaveNotFound` strings; when a `.backup` copy is made is [UNVERIFIED].
- The game reads the presets at start; edits made while it runs are lost the next time the player saves controls. [INF]

## Format [OBS Custom.4.2.binds]
UTF-8 without BOM, `<?xml version="1.0" encoding="UTF-8" ?>`, CRLF, tab indentation, ` />` with a space, attributes `Device` then `Key`, floats with 8 decimals (`0.04700000`), file ends with CRLF. The game normalises stock values on save (`<ToggleOn />` → `<ToggleOn Value="0" />`, `0.4` → `0.40000001`).

```xml
<Root PresetName="Custom" MajorVersion="4" MinorVersion="2">
	<KeyboardLayout>en-GB</KeyboardLayout>                         <!-- text -->
	<ThrottleRange Value="" />                                      <!-- value-only setting, not bindable -->
	<PitchAxisRaw>                                                  <!-- axis element -->
		<Binding Device="25BB00D8" Key="Joy_YAxis" />
		<Inverted Value="1" />
		<Deadzone Value="0.04700000" />
	</PitchAxisRaw>
	<CycleNextTarget>                                               <!-- button element -->
		<Primary Device="Keyboard" Key="Key_G" />
		<Secondary Device="231D0125" Key="Joy_POV2Right">
			<Modifier Device="231D0125" Key="Pos_Joy_UAxis" />
		</Secondary>
	</CycleNextTarget>
	<ShipSpotLightToggle>
		<Primary Device="Keyboard" Key="Key_Insert" />
		<Secondary Device="4098BE04" Key="Joy_18">
			<Hold Value="1" />
		</Secondary>
	</ShipSpotLightToggle>
	<ToggleCargoScoop>
		<Primary Device="Keyboard" Key="Key_Home" />
		<Secondary Device="4098BE04" Key="Pos_Joy_RYAxis" />
		<ToggleOn Value="1" />                                      <!-- only on elements with toggle/hold mode -->
	</ToggleCargoScoop>
```
- Unbound slot: `Device="{NoDevice}" Key=""`. Devices: `Keyboard`, `Mouse`, `GamePad` (XInput, `Pad_*` keys), a joystick id (below). Stock presets also use placeholders `GenericJoystick` and `XB360 Pad`. [OBS]
- Axis elements have one `<Binding>` plus `<Inverted Value="0|1"/>` and `<Deadzone Value="0..1"/>`; the exe logs `Failed to find tag "Binding"/"Inverted"/"Deadzone" for binding:` when one is missing. No saturation or curve per axis, and no global joystick curve either: the only curve settings of the game are the mouse's (`MouseLinearity`, `FSSMouseLinearity`, `BuggyTurretMouseLinearity`). [OBS, OBS-bin; survey docs/games/survey/elite.md]
- Button elements have `<Primary>` and `<Secondary>` (two slots each, any device in either slot). A slot may contain up to 3 `<Modifier Device Key/>` ("has too many modifiers, maximum is 3, ignoring the extras") and `<Hold Value="1"/>`. Modifiers can be keys, buttons or axis halves of any device. [OBS, OBS-bin]
- `<ToggleOn Value>` sits after `<Secondary>` and belongs to the element, not the slot. [OBS]
- Some elements named `*Button*` are axes (`IncreaseSpeedButtonPartial`); decide by shape. [OBS]
- Duplicate elements: "There are multiple entries of binding "%s" Only the first will be used" [OBS-bin]; the game itself writes `<MouseGUI>` twice [OBS].
- Unknown attributes `DeviceIndex`, `Group`, `Activator`, `IgnoreMissingDevices`, `SortOrder` exist in the exe next to the parser strings [OBS-bin]; only `DeviceIndex` is documented by third parties.

### Input names [SRC Help.txt, OBS]
| Elite key | Meaning | JoyPro bind |
|---|---|---|
| `Joy_1` .. `Joy_N` | button, 1-based; Help.txt lists 1-32, a test PC has `Joy_41` | `JOY_BTN<n>` [INF] |
| `Joy_POV<h>Up/Right/Down/Left` | hat 1-4, four directions, no diagonals | `JOY_BTN_POV<h>_U/R/D/L`; diagonals cannot be written |
| `Joy_XAxis Joy_YAxis Joy_ZAxis Joy_RXAxis Joy_RYAxis Joy_RZAxis` | DirectInput axes | `JOY_X .. JOY_RZ` |
| `Joy_UAxis`, `Joy_VAxis` | sliders 0 and 1 [INF] | `JOY_SLIDER1`, `JOY_SLIDER2` [INF] |
| `Pos_Joy_<axis>` / `Neg_Joy_<axis>` | half axis used as a button or modifier (also `Pos_Mouse_ZAxis`) | no JoyPro equivalent: keep, never create |
| `Key_*` | keyboard (DirectInput key names, e.g. `Key_LeftShift`, `Key_Numpad_0`) | keyboard binds |
| `Mouse_1..4`, `Mouse_X/Y/ZAxis` | mouse | not managed |

## Axis and game settings (JoyPro, 2026-10-05)
Model `elite` (`engine\AxisModels\EliteDangerous.cs`), game settings `EliteDangerous` (`engine\FfbModels\EliteDangerous.cs`),
smoke test `engine\tests\smoke\elite.ps1`, in-game check `tools\e2e\games\EliteSettings.ps1` (prepared, not run yet).

### Invert direction
A JoyPro relation keeps DCS's direction: no invert = stick forward is nose down, throttle forward is more thrust (with
a device that needs no invert in DCS). Elite's `<Inverted>` is relative to its own axis convention, which is the other
way round for some elements. **Rule: file `Inverted` = relation invert XOR the element's natural invert; the import
reads relation invert = file XOR natural invert.** Natural invert = 1 for `PitchAxisRaw`, `PitchAxisAlternate`,
`PitchAxis_Landing`, `ThrottleAxis`, `AheadThrust`, `AheadThrust_Landing`, `VerticalThrustRaw`,
`VerticalThrustAlternate`, `VerticalThrust_Landing`, `HeadLookPitchAxisRaw`, `CamTranslateYAxis`,
`ExplorationFSSRadioTuningX_Raw`; 0 for every other axis (`EliteDangerousIntegration.NaturalInvert`).

Evidence (read-only, 2026-10-05):
- The 14 DirectInput joystick presets in `ControlSchemes` (GenericJoystick, BlackWidow, LogitechG940, SaitekAV8R03,
  SaitekFLY5, SaitekX52, SaitekX52Pro, SaitekX55, SaitekX56, T16000MHOTAS, TFlightHotasOne, ThrustMasterHOTAS4,
  ThrustMasterHOTASWarthog, ThrustMasterTFlightHOTASX), `<Inverted>` per element over all presets that bind it:
  `PitchAxisRaw` 1 in 14/14, `ThrottleAxis` 1 in 14/14, `VerticalThrustRaw` 1 in 3/3, `HeadLookPitchAxisRaw` 1 in 4/4,
  `CamTranslateYAxis` 1 in 5/5, `ExplorationFSSRadioTuningX_Raw` 1 in 6/7; `RollAxisRaw` 0 in 14/14,
  `YawAxisRaw` 0 in 13/14 (the exception is the G940 *pedals*, a device that reports its rudder reversed),
  `LateralThrustRaw` 0 in 5/5, `HeadLookYawAxis` 0 in 4/4, the multicrew/FSS/DSS camera axes 0. [SRC]
  `RadarRangeAxis` is 2:2 (X52/X52Pro 0, T16000M/Warthog 1): no convention, 0. The `*Alternate`/`*_Landing` copies and
  `AheadThrust` are not bound by any stock preset; they follow their main element (`AheadThrust` like `ThrottleAxis`
  and the SRV's `DriveSpeedAxis`, 1 in 13/14) [INF].
- Pads: XInput `GamePad` presets have pitch 0 (XInput's Y is up-positive), JoyPro does not write pads.
- DCS for the same devices: ED's default Su-25T diffs for the Warthog stick, T.16000M, WarBRD and WinWing bases have
  pitch `invert = false`, and the Warthog throttle's thrust axes have no filter (no invert); Elite's Warthog preset has
  pitch 1 and throttle 1. So the same physical device is "not inverted" in DCS and "inverted" in Elite on these two
  elements. [SRC `Mods\aircraft\Su-25T\Input\su-25T\joystick\*.diff.lua`]
- The user's own files agree: DCS F-16 `CLSE Joystick VPC V.F.X F14` pitch `invert = false` ↔ Elite `25BB00D8`
  pitch `Inverted 1`; VPC throttles report the lever reversed, so DCS needs `invert = true` (ED's own VPC MT-50 / VPC
  Throttle diffs, the user's MT-50 thrust) ↔ the user's Elite throttle on the VPC WarBRD (`334480CB` Z) has
  `Inverted 0`. Importing the user's `Custom.4.2.binds` with the rule gives that throttle relation invert = true, the
  same as its DCS bind. [OBS]
- Device dependency: the table holds the direction for a device that DCS uses without invert. A device that reports an
  axis reversed (VPC levers, the G940 pedals) needs invert in DCS too, and the XOR carries that over, so no
  per-device table is needed. Profiles imported from Elite before this change hold the file's raw value as invert;
  re-import them once.
- Not yet flown: the nose direction in flight is part of the prepared in-game check. [UNVERIFIED in flight]

### Dead zone, throttle range
- `<Deadzone>` is the relation's dead zone in file units 0..1 (8 decimals; stock values 0.02-0.4, the user's 0.047).
  The UI scale of the slider is [UNVERIFIED]. The preview assumes a linear rescale outside the dead zone [UNVERIFIED].
- `DefaultPlatformDeadzone` (stock pad presets): imported as the tuning value `platformDeadzone = 1` (dead zone 0),
  written back as the symbol; a wanted dead zone of 0 keeps a symbol that is there (as before).
- `<ThrottleRange Value>` (Ship preset, game-wide): `Bindings_ThrottleForewardOnly` = forward only, `""` = full range
  with reverse. The import marks the `ThrottleAxis` relation as one-sided (Slider) when it is forward only; the export
  sets it from the relation that holds `ThrottleAxis` (warning when several throttle relations disagree). Only
  `ThrottleAxis` uses it; SRV (`BuggyThrottleRange`, SRV preset) and free camera ranges are not managed.

### Game settings (`engine ffb set --device * --game EliteDangerous`)
Value elements of the Ship preset, edited in place in the game's format, only when set in JoyPro (an export without
Elite binds writes them too; elements missing from the file are reported, never added):

| JoyPro key | Element | Values |
|---|---|---|
| `yawToRollMode` | `YawToRollMode` | 0 `Bindings_YawIntoRollNone`, 1 `..Time`, 2 `..LowRoll` [OBS-bin symbols] |
| `yawToRollModeFaOff`, `yawToRollModeLanding` | `YawToRollMode_FAOff`, `YawToRollMode_Landing` | 0 `""` (same as normal), 1 None, 2 Time, 3 Low roll |
| `yawToRollSensitivity` | `YawToRollSensitivity` | 0..1, float32 with 8 decimals (0.4 → `0.40000001`) |
| `throttleIncrement` | `ThrottleIncrement` | menu index 0 Continuous, 1 "1 in 10", 2 "1 in 8", 3 "1 in 6", 4 "1 in 4" → stored step 0 / 0.1 / 0.125 / 0.16666667 / 0.25 [INF, check in game] |
| `headlookAccumulate` | `HeadlookMode` | `Bindings_HeadlookModeAccumulate` / `Bindings_HeadlookModeDirect` |
| `headlookIncrement` | `HeadlookIncrement` | 0 Continuous, 1 "1 in 4", 2 "1 in 3", 3 "1 in 2" → 0 / 0.25 / 0.33333334 / 0.5 [INF] |
| `headlookSensitivity` | `HeadlookSensitivity` | 0..1 [range INF] |
| `headlookSmoothing`, `headlookResetOnToggle` | `HeadlookSmoothing`, `HeadlookResetOnToggle` | 0/1 |

Not managed: mouse settings, SRV/on-foot settings (other presets), `EnableRumbleTrigger` and `ControllerRumbleEnabled`
(gamepad only), `FlightMotionControlSensitivity` (meaning unknown). Elite has no force feedback for joysticks.

## Device identification
- Id = `%04X%04X` = USB VID then PID, upper-case hex (`231D0125` = VID 231D PID 0125) [OBS-bin format string, OBS]. The game logs its devices at start: `Can't find input device mapping. Device details: Device name: L-VPC Stick WarBRD / VID: 3344, PID: 80CB` [OBS netLog].
- Devices listed in `DeviceMappings.xml` are written by their **symbolic name** instead (`<T16000M><PID>B10A</PID><VID>044F</VID></T16000M>`, `<Alternative>` entries, e.g. `ThrustMasterWarthogJoystick`, `SaitekX56Throttle`, `VPCThrottle` = 3344:0197, `VPCPanel` = 3344:0259). [OBS file; SRC-3p marwatk: "either the 8-digit hex form or one of the game's named IDs"] An exporter must check this table first (VID/PID case-insensitive, Alternatives included).
- Identical devices (same VID/PID): an extra attribute `DeviceIndex` on the slot and on `<Modifier>` [OBS-bin name; SRC-3p EDRefCard `xmlBinding.get('DeviceIndex', 0)`, marwatk "identical devices, such as dual sticks, are handled with device indices"]. How the index is assigned (enumeration/USB port order) is undocumented and users report swaps after changing USB ports [SRC-3p Steam 2022] → JoyPro cannot map an index to a DirectInput instance GUID. **Limitation:** skip export to a VID/PID that more than one connected device has. Absent attribute = index 0. [INF]
- **A device that is referenced but not connected makes the game ignore the whole preset** (falls back, `BindingLoadingErrors.log`: `Missing devices: ...`) [SRC-3p Steam 2019, community guides; exe has "Missing devices: " and "Failed to set controls preset."; `IgnoreMissingDevices` exists but is undocumented → UNVERIFIED for the 2026 build].

| Id | Device (DirectInput OEMName) | Connected | Joystick binds |
|---|---|---|---|
| `231D0125` | VKB-Sim Gunfighter Modern Combat Pro Twist | yes | 19 (yaw axis, fire, targeting, power, countermeasures; 8 hat binds with half-axis modifiers) |
| `25BB00D8` | CLSE Joystick VPC V.F.X F14 | **no** | 2 (roll, pitch inverted, deadzone 0.047) |
| `334480CB` | L-VPC Stick WarBRD | yes | 19 (throttle, lateral/vertical thrust, FA, boost, FSD, supercruise; 8 UI) |
| `4098BE04` | WINWING F18 TAKEOFF PANEL | yes | 10 (gear, lights (Hold), cargo scoop, panels, night vision with 2 modifiers) |
| `03EB2054` | LEFT VPC Throttle MT-50 CM | yes | 1 (sensor range axis) |

## Action catalog
`C:\Temp\jp2-elite\actions.tsv` (built by `elite_catalog.py` from the game-written `Custom.4.2.binds`, which contains every element the game knows): **514 elements, 422 bindable (70 axes, 352 buttons), 197 flight=yes**, the rest are value settings. [OBS]

| Category | n | flight | Examples |
|---|---|---|---|
| ship_flight | 38 | yes | `YawAxisRaw`, `RollAxisRaw`, `PitchAxisRaw`, `LateralThrustRaw`, `VerticalThrustRaw`, `AheadThrust`, `ThrottleAxis`, `*Alternate`, `ForwardKey`/`BackwardKey`, `SetSpeed*`, `ToggleReverseThrottleInput`, `YawToRollButton` |
| ship_flight_landing | 18 | yes | `*_Landing` overrides |
| ship_drive | 6 | yes | `ToggleFlightAssist`, `UseBoostJuice`, `HyperSuperCombination`, `Supercruise`, `Hyperspace`, `DisableRotationCorrectToggle` |
| ship_targeting | 14 | yes | `SelectTarget`, `Cycle*Target`, `SelectHighestThreat`, `TargetWingman0-2`, `WingNavLock`, `Cycle*Subsystem`, `TargetNextRouteSystem` |
| ship_weapons | 5 | yes | `PrimaryFire`, `SecondaryFire`, `CycleFireGroup*`, `DeployHardpointToggle` |
| ship_defence | 6 | yes | `DeployHeatSink`, `FireChaffLauncher`, `ChargeECM`, `UseShieldCell`, `TriggerFieldNeutraliser`, `ToggleButtonUpInput` (silent running) |
| ship_power | 4 | yes | `Increase*Power`, `ResetPowerDistribution` |
| ship_systems | 12 | yes | `LandingGearToggle`, `ShipSpotLightToggle`, `NightVisionToggle`, `ToggleCargoScoop`, `EjectAllCargo`, `Radar*`, `OrbitLinesToggle`, colours, `TriggerColonisationModule` |
| ship_modes | 10 | yes | `UIFocus`, `Focus*Panel`, `QuickCommsPanel`, `GalaxyMapOpen`, `SystemMapOpen`, `OpenCodexGoToDiscovery`, `PlayerHUDModeToggle` |
| ship_headlook | 8 | yes | `HeadLook*` |
| ship_fighter_orders | 8 | yes | `Order*`, `OpenOrders` |
| ship_multicrew | 16 | yes | `MultiCrew*` |
| ship_fss / ship_dss | 19 / 13 | yes | `ExplorationFSS*`; `ExplorationSAA*`, `SAAThirdPerson*` |
| galaxy_map | 20 | yes | `Cam*`, `GalaxyMapHome` (probably General category, shared with SRV/on foot [INF]) |
| general_ui | 13 | no | `UI_*`, `CycleNext/PreviousPanel/Page`, `Pause`, `FriendsMenu` |
| ship_mouse, misc | 2, 7 | no | `MouseReset`, `BlockMouseDecay`; `HMDReset`, `MicrophoneMute`, `GalnetAudio_*`, `ShowPGScoreSummaryInput` |
| camera, camera_store, holo_me | 47, 6, 4 | no | camera suite, `PhotoCameraToggle`, `Store*`, `CommanderCreator_*` |
| colonisation_placement | 25 | no | `PlaceSettlement`, `*PlacementCam*` |
| srv | 50 | no | `Buggy*`, `*_Buggy`, `Steer*`, `DriveSpeedAxis`, `RecallDismissShip` |
| on_foot | 71 | no | `Humanoid*`, `*_Humanoid` |

- Fighters (SLF) use the ship elements; there are no separate fighter flight elements. [INF]
- Odyssey-only (4.0+): all `Humanoid*`/`*_Humanoid` and `MouseHumanoid*` (absent from EDRefCard's archived Horizons 3.5 defaults, present in its 4.0a defaults) [SRC-3p]. Later 4.x additions, absent even from EDRefCard's "ODY patch 8" defaults: settlement placement (colonisation, 2025), `TriggerColonisationModule`, `TriggerFieldNeutraliser`, `NightVisionToggle`, `GalnetAudio_*`, `VanityCameraTen`, `HumanoidPing`, `HumanoidEmoteSlot2-8`, `EnableAimAssistOnFoot`, `EnableMenuGroupsOnFoot` [SRC-3p; those archived files are gamepad presets and incomplete, so the dating is INF]. The legacy 3.8 client has none of these.
- No longer written by the game (only in stock presets): `ToggleVanityCamera(_Buggy)`, `FocusDistanceInc/Dec`, `GunsightSystem`, `StorePitchCameraUp/Down`, `StoreYawCameraLeft/Right`, and 10 Odyssey-alpha `Humanoid*Button` elements (`HumanoidPulseScanButton`, ...). [OBS]
- Titles are curated English labels [INF]; the game's localisation was not decoded.

### DB refresh (`engine elite refresh-db [<path>] [--dry-run]`, GUI: Other Games, Elite Dangerous, Refresh DB)
- Sources: without a path the `.binds` files the game wrote in `Options\Bindings` (they list every element the game knows) and the stock presets in `<install>\Products\*\ControlSchemes`; with a path that `.binds` file, or the `.binds` files of the folder, its `ControlSchemes` subfolder and `Products\*\ControlSchemes`. All read only.
- An element is an axis when it has `<Binding>`, a button when it has `<Primary>`/`<Secondary>`; value settings are skipped.
- Categories come from the same name rules as the catalog (`EliteDangerousIntegration.CatalogCategory`; rebuilding the catalog from scratch with the 2026-09-03 build gives the shipped categories for all 201 actions). SRV, on-foot, settlement placement, camera suite, UI, mouse and misc elements and names no rule knows are not added.
- Only missing elements are added; known actions keep id, title and category. A new element gets a title made from its name in the catalog's style (`ExplorationFSSZoomIn` → `FSS: Zoom In`, `PitchAxis_Landing` → `Pitch Axis (Landing)`). The catalog is written only when something is new.
- Run against the installed game on 2026-10-10 (build 2026.09.03, `Custom.4.2.binds` of 2025-08-14 plus 30 stock presets): 440 bindable elements, 201 ship flight elements, nothing new. The stock presets alone have 393 elements (189 flight); the game-written file is the more complete source.

## Missing elements and element order
- Missing elements are accepted: the stock presets lack 91-155 of the 514 elements and still work; the game then uses internal defaults (`BuggyCycleFireGroupNext` = `Key_N` in the game-written file although `KeyboardMouseOnly.binds` has no such element). [OBS + INF]
- Order does not matter: the game writes a different order than its own stock presets (37 moved blocks vs `KeyboardMouseOnly.binds`), and the parser looks bindings up by name (duplicate warning). [OBS + INF]
- An element with a missing child tag is logged to `BindingLoadingErrors.log`; whether the preset still loads is [UNVERIFIED]. Never write partial elements.

## How JoyPro should export
1. **Refuse** while `EliteDangerous64.exe` runs (the controls menu would overwrite the edit on the next save). `EDLaunch.exe` does not touch binds. Back up to `.jpbak` before the first write.
2. **Which file:** read `StartPreset.4.start`. Ship elements go into the preset of line 2 (Ship), `galaxy_map` elements into line 1 (General); which category each element really belongs to is not readable from loose files [INF], so if lines 1 and 2 name different presets, write the Ship file only and skip `galaxy_map` with a warning. Edit `Options\Bindings\<Preset>.4.<minor>.binds` with the highest minor version (after a game update the game may save a newer `Custom.4.y.binds`; the older file is then dead). If that line names a stock preset (a file only in `ControlSchemes`), do not write: tell the user to change one control in Options > Controls (Ship) and save, so the game creates `Custom.4.x.binds` and points the line to it. Copying a stock preset into `Custom` is possible but [UNVERIFIED] (stock files lack ~100 elements and have a BOM). Never write to the install folder, never edit `StartPreset.4.start`, never write `Custom.3.0.binds` (legacy client) unless that client is the target.
3. **Edit in place**, text-level: replace only the attributes/children of the touched slots; keep every other byte (declaration, CRLF, tabs, no BOM, duplicate `MouseGUI`, value settings, `KeyboardLayout`, unknown attributes and elements from future versions). For duplicates edit the first element only. A cataloged element missing from the file: skip with a warning (adding it is [UNVERIFIED]).
4. **Devices:** device id = `DeviceMappings.xml` name for the VID/PID if listed, else `%04X%04X`. Skip (warn) devices that are not connected now, devices whose VID/PID is shared by two connected devices (no `DeviceIndex` mapping), and binds a slot cannot express (hat diagonals, saturation/curves, more than 3 modifiers). Warn when the file already references a disconnected device: the game will ignore the whole preset.
5. **Axes:** set `<Binding Device Key>`, `<Inverted Value>` (relation invert XOR the element's natural invert, see "Invert direction"), `<Deadzone Value>` (8 decimals), and `<ThrottleRange>` from the throttle relation's Slider. One axis per element; the `*Alternate` and `*_Landing` axes are separate elements.
6. **Buttons:** write into the slot that already holds this JoyPro device/key or a JoyPro-owned bind; else the first `{NoDevice}` slot (Primary, then Secondary). Never overwrite a keyboard or mouse slot or a slot of a device JoyPro does not write; if both slots are taken, skip with a warning. Write `<Modifier>` children for JoyPro modifiers (max 3) and keep existing `<Hold>`. Leave `<ToggleOn>` as it is.
7. **Clean** (`--mode clean`): on the devices the export writes, set slots of **flight=yes** elements that hold a joystick bind of that device and are not part of the profile to `Device="{NoDevice}" Key=""` (remove their `<Modifier>`/`<Hold>` children), axes to `{NoDevice}` with `Inverted 0` / `Deadzone 0.00000000`. **Nuke** (`--nuke-untouched-devices`): the same for flight=yes slots on joystick devices the export does not use. Both never touch flight=no elements (UI, camera, SRV, on foot, colonisation), keyboard/mouse/GamePad slots, or slots of other devices. Since 2.4.3 both also leave what the import cannot read: slots of a device that is not connected (or with a modifier on one) and half-axis binds/modifiers, so import + clean + nuke leaves the preset byte-identical. The binds of a disconnected device stay (the scan warns about it: connect the device, exchange it in JoyPro or clear it in the game).
8. **Keyboard** binds only with `--include-keyboard`, only for flight=yes elements, using `Key_*` names; never clear keyboard slots in clean/nuke.
8a. **Modes** (since 2.5.0, [export](../export.md#export-modes)): add writes only into a free slot and leaves out a bind whose input (device, key, modifiers) another flight element uses (listed as a skipped multi bind); merge releases the slot of another flight element on an input the profile uses (conflict) and writes into a free slot first, so a moved action keeps its old input while a slot is free; merge-overwrite also releases the other slots of every element the profile binds. All of them only release slots of connected joysticks (keyboard only with `--include-keyboard`), never half-axis binds, and add never nukes.
9. **Never touch SRV or on-foot elements** (`srv`, `on_foot` categories), even though their names (`SteeringAxis`, `HumanoidJumpButton`, ...) sit in the same file.
10. **Import:** joystick slots of flight=yes elements become binds; half-axis keys (`Pos_/Neg_Joy_*`) and placeholder devices (`GenericJoystick`, `GamePad`, `XB360 Pad`) are skipped with a warning; modifiers on half axes (the VKB hat binds on a test PC) cannot be represented.
11. Test plan before enabling writes: export to a copy of `Custom.4.2.binds`, compare byte-for-byte outside the edited slots, then one real start of the game with all referenced devices connected and `BindingLoadingErrors.log` absent.

Blockers for a safe exporter: none in the format. Open points: `DeviceIndex` assignment (identical devices are skipped) and the missing-device rule for the 2026 build. New elements of later builds come in with `engine elite refresh-db` once the game has written them to a preset.
