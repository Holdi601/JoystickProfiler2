# War Thunder (experimental, not tested on a real install)

## Files
- Current controls: `%USERPROFILE%\Documents\My Games\WarThunder\Saves\<gaijinUserId>\production\machine.blk` (`Saves\last\production\` when not logged in). Same path for the Gaijin launcher and Steam (app 236390). [OBS]
- No PC cloud sync of controls. [forum 2026]
- Official presets live inside `gui.vromfs.bin` (`config/hotkeys/hotkey.*.blk`). The in-game Controls > Import/Export uses any path.
- The game saves the profile periodically (60 s debounce) and on exit: edit only while the game is closed.

## BLK text format [SRC Dagor BLK spec]
`name:type=value` with types `t` text, `i` int, `r` real, `b` bool (yes/no), `c`, `p2`/`p3`/`p4`, `ip2`/`ip3`, `m`, `i64`. `//` and `/* */` comments. **Block names repeat**: parse into an ordered list. User files are text with CRLF.

```
version:i=16
content{
  machineId:t="..."
  controls{
    version:i=5
    basePresetPaths{ default:t="wt/config/hotkeys/hotkey.keyboard_shooter_ver2.blk" }
    hotkeys{ ... }
    axes{ ... }
    params{ ... }
    deviceMapping{ ... }
  }
  settings{ ... } gui_options{ ... } custom{} config{ ... }
}
```

### Hotkeys [SRC controlspreset.nut]
```
hotkeys{
  ID_AIR_BRAKE{
    joyButton:i=57
    joyButton:i=61
  }
  ID_AIR_BRAKE{
    joyButton:i=55
  }
  ID_GEAR{
    keyboardKey:i=34
  }
  ID_AAM{}
```
- One `ID_X{...}` block = one shortcut; all inputs inside form a combo (modifiers). Repeated blocks = alternatives (UI max 3).
- Parameters: `keyboardKey` (DIK codes), `mouseButton` (0-4), `joyButton`, `gesture`, optional `activationType`.
- The file is a **diff against `basePresetPaths.default`**: the first occurrence of an ID replaces the base preset's whole list for that ID; `ID_X{}` = explicitly unbound.
- Axis helper shortcuts: `<axis>_rangeMax`, `<axis>_rangeMin`, `<axis>_rangeSet`, `<axis>` (enable).
- `controls.version < 5` = legacy, ignored.

### Axes
```
axes{
  throttle{
    axisId:i=12
    innerDeadzone:r=0.05
    inverse:b=yes
    relSens:r=0.01
    relative:b=no
  }
```
Attributes merge per attribute onto the base preset. Defaults: `axisId -1`, `mouseAxisId -1`, `innerDeadzone 0.05` (not "deadzone"), `rangeMin -1` (0 for throttle, helicopter_collective, ...), `rangeMax 1`, `inverse false`, `nonlinearity 0` (0-3), `kAdd 0`, `kMul 1`, `relSens 1`, `relStep 0`, `relative false`, `keepDisabledValue false`.

Loading [SRC controlsPreset.nut]: the base preset (`basePresetPaths.default`, a legacy `wt/` prefix is dropped; none = `hotkey.empty_ver1`) is loaded with its own base chain, each of its axes reset to the code default and extended with the preset's attributes; then each attribute in the file replaces the base's. A preset with `controls.version < 5` loads nothing. On save the game drops every attribute equal to the base preset's (`diffControls`). So a missing line means "the base preset's value", e.g. `hotkey.keyboard_ver1` (most user files): ailerons/elevator/rudder `innerDeadzone 0`, `nonlinearity 1`; throttle `innerDeadzone 0.02`, `relative yes`; official device presets often `kMul 1.1`.

### Axis setup dialog [SRC axiscontrols.nut, shortcutsGroupAxis.nut, controlsConsts.nut]
| Dialog | File | Shown as |
|---|---|---|
| Dead zone (slider 0..100 step 5) | `innerDeadzone:r` 0..0.5 (`MAX_DEADZONE`) | value (0.00..0.50) |
| Nonlinearity (slider 10..40) | `nonlinearity:r` 0..3 | value + 1 (1.0..4.0) |
| Multiplier (slider 20..200 step 5) | `kMul:r` | value (0.20..2.00); presets use 0.02 for tank turrets |
| Correction (slider -50..50) | `kAdd:r` -1..1 | value x 50 |
| Invert Axis | `inverse:b` | |
| Relative control (hidden for roll/pitch) | `relative:b` | |
| Relative control sensitivity (1..10) | `relSens:r` = (slider/10)^2 | sqrt(value) x 100 % |
| Relative control step (0..50) | `relStep:r` = slider/100 | value x 100 |
| Keep value for disabled axis | `keepDisabledValue:b` | |
| (none) | `rangeMin:r`, `rangeMax:r` | file only |

The dialog's live preview (`onAxisInputTimer`): `v = raw/32000; v = inverse ? -v : v; v = v*kMul + kAdd; s = sign(v); v = clamp(v,-1,1); v = |v| < dz ? 0 : (|v|-dz)/(1-dz); y = s*|v|^(1+nonlinearity)`. That the in-flight value then spans rangeMin..rangeMax (throttle 0..1, so the dead zone of a throttle sits at half throttle) is inferred, not in the scripts.

### Game-wide control options [SRC optionsExt.nut, shortcutsGroupAircraft/Helicopter.nut; keys OBS in 4 user machine.blk]
| Controls page | machine.blk | File value |
|---|---|---|
| Roll / Pitch / Yaw sensitivity (aircraft) | `settings{aileronsMultiplier, elevatorMultiplier, rudderMultiplier}` | % / 100 (0.79/0.64/0.43 in all 4 user files) |
| Roll / Pitch / Yaw sensitivity (helicopter) | `settings{helicopterCyclicRollMultiplier, helicopterCyclicPitchMultiplier, helicopterPedalsMultiplier}` | % / 100 |
| Invert Y axis (aircraft / helicopter), Invert throttle | `settings{invertY, invertYHelicopter, invertT}:b` | |
| Force Feedback Multiplier (0..100) | `settings{forceGain:r}` | UI / 50 (`set_option_gain(value / 50.0)`) |
| Force Feedback (restart needed) | `gui_options{OPTIONS_MODE_GAMEPLAY{USEROPT_JOYFX:b}}` | |
| Autotrim | `gui_options{OPTIONS_MODE_GAMEPLAY{USEROPT_AUTOTRIM:b}}` | |
| Hold throttle for WEP | `controls{params{holdThrottleForWEP:b}}` (joystick params, diffed against the base preset's) | |

There is no per-device or per-effect force feedback and no axis noise filter for joysticks.

### Devices [SRC Dagor joy_device.cpp, joy_enumdev.cpp]
```
deviceMapping{
  joystick{
    connected:b=no
    devId:t="0738:2215"
    axesOffset:i=7
    name:t="Saitek Pro Flight X-55 Rhino Stick"
    buttonsOffset:i=28
    buttonsCount:i=21
    axesCount:i=5
  }
```
- `devId` = `"%04X:%04X"` VID:PID from DirectInput `guidProduct`; `name` = `tszInstanceName`.
- `joyButton` = buttonsOffset + local index. Local 0..N-1 = DirectInput buttons, then 4 per POV hat: `N + 4*hat + {0 up, 1 right, 2 down, 3 left}`.
- `axisId` = axesOffset + local index, local order = DirectInput `EnumObjects` order (X/Y/Z/Rx/Ry/Rz/Slider0/Slider1), then 2 virtual axes per hat.
- `buttonsCount = buttons + 4×POV`, `axesCount = axes + 2×POV`.
- On load the game matches entries **by devId only** and shifts ranges to real offsets. Identical devIds collapse into one.
- A device the game has never seen has no entry; its indices are unknown until the game lists it.

## Flight entries
Aircraft (`shortcutsgroupaircraft.nut`, ~136 shortcuts + 37 axes: throttle, ailerons, elevator, rudder, trim_*, mixture, prop_pitch, radiator, brake_left/right, flaps, ...) and helicopter (`shortcutsgrouphelicopter.nut`, ~95 shortcuts + 22 axes `helicopter_*`, `*_HELICOPTER`). Everything with `gm_`, `ship_`, `submarine_`, `human_` belongs to other units. In `params{}`, `settings{}` and `gui_options{}` JoyPro writes only the single keys of the game-wide options above (each line set in place, a missing block created at the end of its parent, every other line kept); `config{}` and everything else is never written.

Where the action list lives in the installed game: `gui.vromfs.bin` (`scripts/controls/shortcutslist/shortcutsgroupaircraft.nut`, `...helicopter.nut`); names in `lang.vromfs.bin` (`lang/controls.csv`). See "Catalog" below for how JoyPro reads them on the user's PC, and how to turn that off.

### Device limits [SRC Dagor dag_hiJoyData.h, joy_device.cpp]
- Axes are added in `EnumObjects` order by `guidType` (X/Y/Z/Rx/Ry/Rz; duplicates count twice), sliders by offset or next free slider (max 2); axes with an unknown GUID and offset are skipped.
- At most `512 - 16 = 496` buttons and 4 hats per device are counted, but only the first 128 buttons are read (`DIJOYSTATE2`).
- `engine games devices` prints what JoyPro sees per device (VID:PID, axes in enumeration order, buttons, hats).

## Cloud sync [OBS .game_logs 2026-10-10; aces.exe online_storage]
- `Saves\<uid>\production\machine.blk` (controls, settings{}, gui_options{}) and `global.blk` are synced with the Gaijin server: machine.blk per account and machine (`machine.id`), global.blk per account. `Saves\last` is a copy from before the login; `Saves\lastlogin.blk` names the account (`uid:i64=`), which JoyPro picks first.
- At login the game compares the file's first line `version:i=N` with the server's (`online_storage::process_gblk_meta_with_type`): server higher: the server copy replaces the file; equal version and same content: nothing; equal version, other content: the file is uploaded (version + 1); server lower: the file is uploaded. The game raises the version on each local save. The game's own log (`.game_logs\*.clog`) records the decision ("ver is newer local ver", "is differ local ver", "force save local settings").
- JoyPro's export keeps the version (equal version, new content: the file wins; seen in game). Restoring an older copy lowers it, and then the server's newer copy comes back at the next login. `engine backups restore` therefore gives a restored machine.blk/global.blk the version above the file it replaces (`WarThunderCloudVersion`); copy a `.jpbak` back by hand only together with such a version.
- In-game check: `tools\e2e\games\WarThunderSettings.ps1` (unattended): binds (vJoy axes 2 and 7), the Axis setup dialogs of pitch and throttle and the Controls page values against what JoyPro wrote.

## JoyPro implementation (`engine warthunder ...`)
- Files: `machine.blk` (`content{controls{}}`) or an exported controls preset (`controls{}` at the top); text BLK only.
- Written: `hotkeys{}` blocks of cataloged IDs; `axisId` and the axis settings of cataloged axes; the game-wide options the profile sets. Every other line keeps its text; `deviceMapping{}` is only read.
- Axis settings: response model `warthunder` (`engine\AxisModels\WarThunder.cs`, file units, preview = the dialog's formula above), scope `game:WarThunder` (or `plane:`). Import reads every attribute of the dialog with the value the game uses (file, else base preset from `DB\WarThunder\base-presets.tsv`, else code default) into the relation's `game:WarThunder` tuning. Export writes only the attributes the tuning holds plus `inverse`: a line that already has the value stays, a value equal to the base preset's drops the line (the game's diff style; `innerDeadzone` 0 is written when the base differs, e.g. 0.02), anything else is written; the dead zone is clamped to 0.5 and nonlinearity to 0..3. A relation without War Thunder tuning gets its base (DCS) values fitted: multiplier = 1 / saturation X, dead zone = dead zone x multiplier (exact for the linear part), nonlinearity 0..3 closest to the curve/saturation Y; a slider gets dead zone 0 and multiplier/correction mapping the travel between dead zone and saturation X onto the whole range. Relative control, keep value and range stay as the file has them unless set.
- Base presets: `DB\WarThunder\base-presets.tsv` holds the effective axis values of all 72 official presets (chains resolved), generated by the maintainer from the game's official preset files (`config/hotkeys/hotkey.*.blk` in `aces.vromfs.bin`); a development copy only, not shipped and not in the public repository. A base preset that is not in the table gets a warning; missing attributes are then the code defaults, which may differ from the game. Regenerate the table after a game update that changes presets.
- Game-wide options: `engine ffb set --device * --game WarThunder --param <key>=<value>` (`engine\FfbModels\WarThunder.cs`: `joyFX`, `forceGain` 0..100, `aileronsMultiplier`/`elevatorMultiplier`/`rudderMultiplier`/`helicopterCyclicRollMultiplier`/`helicopterCyclicPitchMultiplier`/`helicopterPedalsMultiplier` in %, `invertY`, `invertYHelicopter`, `invertT`, `holdThrottleForWEP`, `autotrim`). Written to machine.blk only (not to an exported preset), also by an export without War Thunder binds; import takes the file's values into the profile. The game takes them from machine.blk like the controls (see Cloud sync).
- Device match: `devId` = VID:PID of the JoyPro device. Skipped with a reason when War Thunder has not listed the device yet, when several connected devices share the VID:PID, or when `buttonsCount`/`axesCount` differ from the DirectInput counts (then all indices would be off).
- Shortcuts: the last `joyButton` of a block is the main input, other `joyButton`/`keyboardKey` entries become modifiers. Hat diagonals, mouse buttons and axis modifiers are not supported.
- Export modes (only cataloged aircraft/helicopter IDs; tank, ship and menu shortcuts and axes are never touched; a bind of another cataloged ID on the same input = "conflict"):
  - `add` writes a shortcut only when no other cataloged ID uses that input (else it is skipped and listed as a skipped multi-bind); it never removes a block or replaces a bound axis.
  - `merge` writes the profile's shortcuts beside the ID's other shortcuts and removes only conflicts (an axis of another cataloged ID on the profile's axis gets `axisId -1`).
  - `merge-overwrite` = merge + the profile's IDs lose their other shortcuts on connected devices War Thunder lists (keyboard only with `--include-keyboard`; mouse and devices that are not connected stay).
  - `clean` = merge-overwrite + releases the other cataloged shortcuts/axes of the devices the export writes; `--nuke-untouched-devices` releases those of connected devices without binds in the export.
  - No ownership file any more: JoyPro's earlier shortcuts count like the game's (an old `machine.blk.joypro.json` is ignored).
  - An empty `ID_X{}` marker is replaced when JoyPro binds the ID. An ID missing from the file gets a warning, because JoyPro's shortcut then replaces the base preset's default.
- Release packages ship neither `actions.tsv` nor `base-presets.tsv` (no game data, THIRD_PARTY_NOTICES.md 1.4). Without the base
  preset table an export writes every tuned axis attribute explicitly (it never drops a line as "equal to the base"),
  so the file is right whatever the base preset is; import uses the code defaults for attributes the file does not set.
- Catalog: `DB\WarThunder\actions.tsv`, built on the user's PC by `engine warthunder refresh-db`:
  - **Default: the full list from the installed game's archives** (the user approved reading them on 2026-10-12;
    before, this needed an opt-in). Fresh build: 290 actions, the same as the development list.
  - **`--no-game-archives`** (or `--read-game-archives false`; GUI: Settings > Games, untick "War Thunder: read the full
    command list from the game's archives"): no game file is read; JoyPro's own list, the War Thunder action ids of its
    function list (`DB\Functions\WarThunder.tsv`) with JoyPro's descriptions (116 actions); ids without `ID_` are axes.
    `--read-game-archives` is still accepted.
    The GUI stores the choice under a new key (`ReadWarThunderArchives`, default on), so the old opt-in default of
    earlier builds is not taken as the user turning it off.
  - `engine\Games\WarThunderVromfs.cs` reads `scripts/controls/shortcutslist/shortcutsgroupaircraft.nut` and `...helicopter.nut` from `gui.vromfs.bin`, and `lang/controls.csv` (titles) from `lang.vromfs.bin`.
  - The archives are Dagor Engine vromfs, the format of Gaijin's own open-source engine (BSD-3, `prog/engine/ioSys/vromfsLoad.cpp`): a `VRFx` header, then a zstd body whose first and last 16 bytes are masked as that source does it, then a name table and a data table. Read only, on the user's PC: the two scripts and `controls.csv` are unpacked into a temporary folder that is deleted after the refresh; only the command list (ids and titles) stays, in JoyPro's game-data folder; nothing is shipped and no game file is changed.
  - `--path <game folder, or a folder with the control scripts as plain files>` also works, including older script names with the `sl_` prefix.
  - Refresh 2026-10-10 on the installed game: 290 actions, the same as the shipped catalog.
- Before writing: the game must be closed (`aces`/`aces_BE` running = refused), the file is backed up once to `.jpbak`.
