# Arma Reforger (experimental; first started and checked in game 2026-10-05, 1.9.0.13 Steam, settings menu only)

## Two binding mechanisms
1. **Custom joystick preset files** (the native joystick path in stable 1.8): `%USERPROFILE%\Documents\My Games\ArmaReforger\profile\.save\settings\customInputConfigs\*.conf`. The user picks one in Settings > Controllers > "Joystick preset". [SRC official scripts, BohemiaInteractive/Arma-Reforger-Script-Diff: SCR_ControllerPresetsSettingsSubMenu.c, game.c]
   - The gamepad "Controller preset" selection also calls `SetCustomConfigs` with one element, so a joystick preset and a gamepad preset cannot both be active.
2. **User rebind file** `InputUserSettings.conf` in `profile\.save\app1874880_user<id>\settings\` (experimental: `app1890860_...`). Holds only overridden actions. Older builds used `profile\.chimeraUserInput.conf`. Exact current location [UNVERIFIED].
   - 1.9 experimental (2026-09-18) announced a joystick binding tab; in 1.9.0.13 (Steam, seen in game 2026-10-05) Settings > Controls shows only the Keybind and Controller (gamepad) columns, joysticks still go through the "Joystick preset" in Settings > Controllers.

**JoyPro approach:** write and own `customInputConfigs\JoyPro.conf` with only HelicopterContext flight actions and joystick sources. The user selects it once. InputUserSettings.conf is never touched, which satisfies "don't destroy other bindings".

## Format (Enfusion text) [SRC Input Manager wiki, InputManagerDocs.c]
```
ActionManager {
 Actions {
  Action HelicopterCollectiveIncrease {
   InputSource InputSourceSum "{66E300D8F2C50A1B}" {
    Sources {
     InputSourceValue "{66E300D8F2C53F96}" {
      FilterPreset "up"
      Input "joystick0:axis6-"
     }
    }
   }
  }
 }
}
```
- One-space indentation, `Key Value` without `=`/`;`, objects `Class "{16-hex-ID}" {`. IDs must be unique random 16-hex values.
- Combos: `InputSourceCombo "{id}" { Sources { two InputSourceValue } }`. Cross-device combos are not supported.
- Filters: `Filter InputFilterDown "{id}" { }`, `InputFilterHold`, `InputFilterHoldOnce`, and `InputFilterValue` with properties (below).

## Input names
- `keyboard:KC_*` (DIK names with KC_), `mouse:button0`, `mouse:y_rel+`, `gamepad0:a`, `joystick0:button4` (0-based), `joystick0:axis0`, half axes `joystickN:axisA+` / `axisA-`, hats `joystickN:pov_up|pov_down|pov_left|pov_right`. [SRC/OBS]
- Axis numbering axis0..7 = X, Y, Z, Rx, Ry, Rz, Slider0, Slider1 [INF from presets and Klawifiantix/Arma-Reforger-HOTAS-Mapper].
- Second hat and diagonal names [UNVERIFIED].

## Devices and slots (JoystickOrderRule)
Up to 4 joystick slots (`joystick0..3`). Settings > Controllers lists them under "Devices" with a slot combo (0-3).

### How the game picks and orders joysticks (1.9, in game and ArmaReforgerSteam.exe, 2026-10-05)
Behaviour of the installed exe, looked at for interoperability only (nothing modified or injected; code addresses for
maintainers), checked in game where noted:
- **Detection** (`WinInputDevice`, around 0x140c2be20): `RegisterRawInputDevices` with one entry, UsagePage 1 /
  **Usage 4 (Joystick)**, flags 0 (no `RIDEV_DEVNOTIFY`). Gamepads (Usage 5) and multi-axis controllers (Usage 8)
  are never joysticks here. A device is seen when it **first sends a raw input report while the game window is
  focused** (`GetRawInputDeviceInfoW(RIDI_DEVICEINFO)` on that report). VID 057E / PID 2009 (Switch Pro Controller)
  is skipped.
- **Table of 4**: key = (usage page, usage, VID, PID). A new device is inserted sorted by key while there is room; a
  known key is a reconnect. With 4 entries taken, **every further new device overwrites entry 3**. So the first
  devices that send input (devices with axis noise: in this PC's test the VPC throttle, Crosswind pedals, MOZA base)
  stay; entry 3 goes to whichever other device sent last (seen: VPC stick, Rhino, WinWing on different starts). vJoy,
  VKB and the Wooting never appeared in four starts, not even with vJoy sweeping an axis during start and menu.
  Names come from `HidD_GetManufacturerString` + `HidD_GetProductString`: "Gudsen MOZA R5 Base",
  "ATMEL/VIRPIL/200325 LEFT VPC Throttle MT-50 CM".
- **Order rules** (around 0x1411d9930): for device i = 0..3 (only the 4 in the table), the rules are checked from the
  last to the first; `strstr(deviceName, DeviceNameContains)` gives the device the rule's slot unless another device
  holds it ("Joystick slot rule conflict - slot is already used"). Devices without a rule take the first free slot in
  table order. **The rule's slot indexes a 4-entry array without a range check**: slots above 3 are not just
  ignored but read and write past the array, so JoyPro writes 0..3 only. Checked in game: a rule moves a listed
  device (throttle rule 0 -> 3 lists the throttle last); a rule for a device the game did not detect (vJoy, slot 4
  or 1, DirectInput and HID name) changes nothing.
- Consequence: the game uses at most 4 joysticks, and which 4 depends on which ones send input first. Users with
  more than 4 joysticks have to disconnect (or hide, e.g. HidHide) the others while playing Reforger; JoyPro can
  only sort the ones the game detected.

Files (1.9 Steam names; older builds: `EngineUserSettings.conf` / `GameUserSettings.conf`, same content) in
`profile\.save\app1874880_user<steam id>\settings\`: `ReforgerEngineSettings.conf` (root `EngineUserSettings`) holds
```
 InputDeviceUserSettings InputDeviceUserSettings {
  JoystickProfile InputProfileJoystick "{id}" { Axis00 InputProfileKey "{id}" { Transition CurveCubicSplineFloat "{id}" { } } ... Axis09 }
  JoystickSettings JoystickSettings "{id}" {
   DeviceOrderRules {
    JoystickOrderRule "{id}" {
     DeviceNameContains "ATMEL/VIRPIL/200325 LEFT VPC Throttle MT-50 CM"
     DeviceSlotNumber 0
    }
   }
  }
 }
```
(the game writes a rule for a device when its slot combo is used); `ReforgerGameSettings.conf` (root
`GameUserSettings`) holds the settings modules (`SCR_GameplaySettings` ...). The `JoystickProfile` per-axis curves
(`Axis00`-`Axis09`, empty `CurveCubicSplineFloat` = linear) are not managed by JoyPro yet [format of the points
UNVERIFIED: no UI for them was found in Settings]. Identical device names cannot be told apart (substring match).

JoyPro, on export (when a settings folder `profile\.save\app*_user*\settings` with one of the files is next to the
preset):
- first the devices with a chosen slot (`ffb set --device "<device>" --game ArmaReforger --param joystickSlot=<0..3>`,
  -1 = automatic), then each exported device keeps the slot an existing rule gives it (also the game's own rules with
  the long name: either name contains the other), the others get the lowest slot no rule claims. JoyPro writes a rule
  per device with its **HID product string** (contained in the game's name; the DirectInput name can differ, e.g.
  "vJoy Device" vs "vJoy - Virtual Joystick"), edits `DeviceSlotNumber` in place or appends `JoystickOrderRule`
  inside `DeviceOrderRules` (with an id when the existing rules have ids), and uses that slot as `joystickN`.
- warnings: more than 4 devices used or placed; no free slot left; two chosen slots alike; another device's rule on
  a chosen slot; joystickSlot above 3 is refused by `ffb set`.
- it creates `DeviceOrderRules` only inside an existing `JoystickSettings`, and `JoystickSettings` only inside an
  existing `InputDeviceUserSettings`; without that section it writes nothing there (warning) and numbers the used
  devices by VID/PID (the game's table order). Two exported devices with the same name: the second gets no slot.
- every settings folder found (stable and experimental) gets the same rules; backups via GameExportSafety.
- Scan/import maps `joystickN` back through the rules (else the VID/PID order).

## Axis filter (axis model `reforger`, `engine\AxisModels\ArmaReforger.cs`)
Per bind: `multiplier` (default 1) and `useInvert` (the game's description: the invert mouse / gamepad vertically
options are applied) are written as `Filter InputFilterValue "{id}" { Multiplier <m> UseInvert 1 }` on both half-axis
sources when not default; read back on import into the bind's `game:ArmaReforger` tuning. Property names from the stable
1.8 exe strings (OBS) and the 1.9 Advanced keybind dialog (SRC, which also has Offset, not written). Whether a filter on a
half-axis source keeps the preset's behaviour, and how the action combines values above 1, is UNVERIFIED; the preview
is x * multiplier, clamped. The engine's per-axis joystick profile (`JoystickProfile` / `Axis00..09` `DeadZone`,
`Transition`) is not written (format unknown).

## Game settings (`engine ffb set --device * --game ArmaReforger`, `engine\FfbModels\ArmaReforger.cs`)
`ReforgerGameSettings.conf` (1.9 Steam; older builds `GameUserSettings.conf`) in the same settings folders:
- `hotasCollective` -> `SCR_GameplaySettings` `m_eHotasCollective` (0 Default, 1 Manual, 2 Sticky, 3 Direct; the game's
  default for a HOTAS is Direct) [SRC 1.8 `SCR_GameplaySettings.c`].
- `forceFeedbackIntensity` -> `SCR_ControllerSettings` `m_fForceFeedbackIntensity` (0..3, default 1; 1.9 experimental)
  [SRC 1.9 `SCR_ControllerSettings.c`]. The Force Feedback Device choice (1.9) is not written (persistence unknown).
- An existing key is changed in place (numbers only; a non-numeric value is left with a warning); a missing key is
  added as `<key> <value>` inside its module's block when that block exists; a missing block is not created (warning:
  change the setting in the game once). Format UNVERIFIED (enum as number assumed).
- An export without Reforger binds still writes these settings.

## Helicopter actions (HelicopterContext)
HelicopterCollectiveIncrease ("up"), HelicopterCollectiveDecrease ("down"), HelicopterAntiTorqueLeft ("left"), HelicopterAntiTorqueRight ("right"), HelicopterCyclicForward ("forward"), HelicopterCyclicBack ("back"), HelicopterCyclicLeft ("left"), HelicopterCyclicRight ("right"), HelicopterWheelBrake, HelicopterWheelBrakePersistent ("pressed"), HelicopterAutohoverToggle ("click"), HelicopterLightsTaxiToggle, HelicopterLightsLandingToggle ("toggle" + InputFilterDown), HelicopterEngineStart ("hold" + InputFilterHold), HelicopterEngineStop ("click" + InputFilterHoldOnce), HelicopterFire, HelicopterSightDeploy, HelicopterSightZeroing, HelicopterNextWeapon.
Only helicopters exist in vanilla (UH-1H, Mi-8); fixed wing only through mods. Full list: `Configs/System/chimeraInputCommon.conf` inside the game paks (PAC1 format).
1.9.0.13 (Steam, read 2026-10-11): HelicopterContext has the 18 actions above minus HelicopterNextWeapon (gone); AirplaneContext
is empty, and the top-level AirplaneElevators / AirplaneAilerons / AirplaneRudder / AirplaneThrust / AirplaneFlapsToggle
are referenced by no context (dead). The Settings > Controls "Helicopter" category (keyBindingMenu.conf) lists the
same actions minus HelicopterFire, with HelicopterSightZeroing twice (presets "up" / "down").

## DB refresh (`engine reforger refresh-db [<path>] [--dry-run]`; the GUI's DB refresh of registry games runs the same)
Reads the installed game (found by detection or `engine games install set reforger <folder>`) read only; `<path>` can
be the game folder, one `.pak`, a folder of extracted files (`Configs\System\...`, `Language\...`) or the input config
itself.
- **Archives** (`EnfusionPak.cs`): every `*.pak` under `<game>\addons` (16 in 1.9; a later archive replaces a path an
  earlier one has). Format as seen in 1.9 (HEAD version 0x10003, matches the community descriptions, e.g. the
  `enfusion_pak` crate / `pac1` Go package; JoyPro has its own reader): `FORM` + big-endian size + `PAC1`, chunks
  `HEAD` (28 bytes), `DATA` (contents), `FILE` (directory tree: byte type 0 folder / 1 file, byte name length, name; a
  folder has a uint32 child count, a file 24 bytes: uint32 absolute offset, stored size, size, 0, 2 zero bytes, byte
  compression 0 stored / 1 zlib, byte level, uint32 time). The three files below are in `addons\data\data007.pak`
  (1.9); reading them takes about 0.4 s.
- **Actions**: `Configs/System/chimeraInputCommon.conf` (`ActionManager { Actions {...} Contexts { ActionContext ... {
  Actions { Action <name> {...} } ActionRefs { "<name>" } } } }`). Only contexts whose name starts with Helicopter /
  Airplane / Aircraft / Plane count; the category is the context name without "Context" (Helicopter, as the shipped
  rows). Ids are the action names the preset files use. The half axes of the cyclic / collective / pedals stay in
  JoyPro's `Axis.*` composite rows. An action of type Analog / AnalogRelative / Motion with a whole-axis source
  (`axisN`, thumb sticks, triggers, `*_abs`) is an axis; a stepping action of that type with two presets (sight range
  up / down) gets one button row per preset (`<action>.Up`, `<action>.Down`); everything else is one button row.
- **Source column**: new button rows get a 5th column `<action>|<FilterPreset>|<filter class>|<parameters>` copied from
  the game's own default source (the one with the row's preset; keyboard first, then mouse, joystick, gamepad). The
  export writes the joystick source with that preset and filter (e.g. `InputFilterRepeat` with `Multiplier -1` for
  sight range down, `InputFilterPressed` with the game's thresholds for fire), so the button acts like the game's key;
  scan / import map a source back to the row with its preset. A whole-axis row is written as `joystickN:axisA`
  (inverted by a negative `Multiplier`).
- **Titles**: `Configs/System/keyBindingMenu.conf` (`SCR_KeyBindingEntry { m_sActionName m_sDisplayName "#AR-..."
  m_sPreset }`) and `Language/localization.en_us.conf` (`StringTableRuntime { Ids { "AR-..." } Texts { "..." } }`, same
  order; `\"` is a quote, `"..."\` continues on the next line). Without an entry: the action name without the category,
  split into words ("HelicopterFire" -> "Fire"; with a preset "Sight zeroing (down)").
- **User configs** (second source, like WARDOGS' ini): the `*.conf` presets in `customInputConfigs` (or the folder /
  file set with `engine games config set reforger`) and `InputUserSettings.conf` of each settings folder; their
  Helicopter* / Airplane* actions the game files did not bring (mods) are added the same way. On this PC both hold no
  actions (InputUserSettings.conf only has an empty `CustomConfigs`).
- Known rows keep id, title and category (relation names come from titles); the catalog is written only when a row is
  new. Workshop mods (`Documents\My Games\ArmaReforger\addons`) are not read.
- First real run (1.9.0.13, 2026-10-11): 18 flight actions in HelicopterContext and AirplaneContext, 4 new, now in the
  shipped catalog: HelicopterFire ("Fire"; no keybind entry, mouse button 0 / gamepad shoulder with
  `InputFilterPressed`), HelicopterSightZeroing.Up / .Down ("Sight range up / down", `InputFilterRepeat`),
  HelicopterSightDeploy ("Toggle sights", `InputFilterClick`). Not checked in game yet.

## Pitfalls
The game writes InputUserSettings.conf on every rebind and its settings files on changes: export only while it is
closed (JoyPro refuses while ArmaReforgerSteam / ArmaReforger_BE / ArmaReforger runs); restart after changing a preset
file; stable and experimental use separate folders; whether a preset replaces or adds to default keyboard sources is
[UNVERIFIED]. One first start without BattlEye (`ArmaReforgerSteam.exe` directly, Settings only) is needed to see the
real settings format; until then everything in the two settings files is built from the scripts.

Sources: https://community.bistudio.com/wiki/Arma_Reforger:Input_Manager, https://github.com/BohemiaInteractive/Arma-Reforger-Script-Diff, https://github.com/jscrobinson/reforger_hotas_config, https://github.com/Nywed01/arma-reforger-hotas-blank-preset.
