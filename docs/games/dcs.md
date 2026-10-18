# DCS World

CLI: `engine dcs scan | import | export`, `engine dcs refresh id | clean` (DB and clean profiles), `engine dcs verify-clean`,
`engine dcs strip-defaults`, `engine dcs device-luas`. Code:
`engine\EngineDcsImport.cs`, `engine\EngineDcsExport.cs`, `engine\EngineDcsRefresh.cs`, `engine\DcsDeviceNames.cs`, `engine\EngineDcsRefresh.Verify.cs`, `engine\EngineDcsDefaultsStrip.cs`.

## Files
- `Saved Games\DCS\Config\Input\<profile>\joystick\<device>.diff.lua`: the user's changes against the defaults
  (`keyDiffs`/`axisDiffs` with `added`/`removed`, `ffDiffs` for force feedback). `<profile>` is the input profile
  name a module declares in its `entry.lua` (`InputProfiles`), evaluated with DCS's own Lua (see refresh). Keyboard
  files are in `keyboard\Keyboard.diff.lua`, modifiers in `<profile>\modifiers.lua`.
- `<device>.jp`: JoyPro's copy of the written file, with relation names and groups (re-import metadata).
- Files are written without a BOM: DCS's Lua 5.1 cannot load one.

## Device names
DCS names a joystick `<DirectInput instance name> {<instance GUID>}`, with the instance name exactly as DirectInput
reports it, spaces included. Example: VKB reports `" VKB-Sim Gunfighter Modern Combat Pro Twist "`, so DCS reads
`" VKB-Sim Gunfighter Modern Combat Pro Twist  {1A2B3C4D-0A3F-11f0-8011-444553540000}.diff.lua"`. The GUID is upper
case except its third group.

JoyPro's device names trim the instance name (`VKB-Sim Gunfighter Modern Combat Pro Twist {...}`). `DcsDeviceNames`
converts between both:
- Export: the file is named after the connected device's raw DirectInput name; for a device that is not connected,
  after an existing file of that device in the folder; else the JoyPro name. Files an older JoyPro wrote under the
  trimmed name (DCS ignores them) are read when no DCS-named file exists yet and removed after the write (backed up).
- Import: a file name is turned into the JoyPro name (spaces at the ends of the instance name trimmed).

The device *type* (DCS: `Input.Utils.getDeviceTemplateName`) is the name without ` {GUID}`. It selects the device's
default file (below).

## Default binds and clean profiles
For each aircraft DCS loads the device's defaults from the profile's `joystick` folder: `<device type>.lua` when it
exists, else `default.lua`; then the module-independent `Config\Input\Aircrafts\Default\joystick\` files. A template
diff `<device type>.diff.lua` in the install applies while the user has no diff of that device. `default.lua` itself
asks `defaultDeviceAssignmentFor("pitch" | "rudder" | "left_wheel_brake" | ...)`, which answers per device type from
`Scripts\Input\DefaultAssignments.lua` (MFG Crosswind V2: rudder JOY_RZ, wheel brakes JOY_X/JOY_Y inverted; panels such
as WINWING F18 TAKEOFF PANEL or F16 MFD: no axes) and from the user's `Config\Input\wizard.lua`. So the same aircraft
has different defaults per device type (A-10C II: generic stick 17 default binds, Warthog stick 33, Warthog throttle
38, WINWING F18 takeoff panel 12).

JoyPro takes over whole devices (see [README](README.md), rule 5), so it removes the defaults of every device it
writes. The removals come from clean profiles in the game-data folder, built on the user's PC from the user's DCS:
- `CleanProfile\DCS\<profile>.cf`: the generic stick (`default.lua`).
- `CleanProfile\DCS\<profile>\<device type>.cf`: a device type whose defaults differ from the generic ones in that
  aircraft, or that has a default file in the aircraft's folder.
- `CleanProfile\DCS\device-types.txt`: the device types the clean profiles were built for. A listed type without a
  `.cf` of its own has exactly the generic stick's defaults there.
- `KeyboardCleanProfile\DCS\<profile>.cf`: the keyboard. Refreshed 2026-10-10 by the headless Lua refresh (118 profiles; the March 2025 set differed in 70: common keys such as F9/\ missing, MiG-21 Num+/Num- with the old signs, MiG-29/Su-25 commands moved to other device ids, 4 new profiles). Check: a clean export with `--keep-keyboard-defaults false` into a temporary Saved Games for all 118 profiles, every written file loads in DCS's luae.exe.

The export picks `<profile>\<device type>.cf` for the device (also for a device that is not connected, whose JoyPro
name has no spaces at the ends), else `<profile>.cf`. It warns when a written device's type has defaults of its own
(DefaultAssignments.lua) that the clean profiles were not built for (lists from before 2026-10-17: update the DCS list
once).

### Refresh (`engine dcs refresh clean --headless-lua true`)
Runs DCS's own input code in `bin\luae.exe` (no game window): `Input.Data` loads every input profile, registers a
generic fake stick and one fake stick per device type (`Input.addFakeJoystick("<type>")`, which DCS names
`"<type> {FAKE-JOYSTICK-nnnn}"`), calls `InputData.clearProfile` for every profile and `saveChanges`. The diffs DCS
writes are the clean profiles above. The device types:
- every type with a default file of its own in some aircraft (install and Saved Games mods);
- every type `Scripts\Input\DefaultAssignments.lua` names (evaluated by Lua in the same run);
- every joystick connected now (luae.exe sees the PC's DirectInput devices) and every device the user's
  `Config\Input\*\joystick\*.diff.lua` names (the user's devices that are not connected; read only).
Everything is written into a temporary Saved Games folder; the user's `Config\Input` is never changed.
`--plane`/`--planes` limit the aircraft. A file the strip option (below) patched is read from its `.nocombo.bak`, so
the clean profiles always hold DCS's real defaults (checked: FA-18C and F-15C from a stripped copy of the install are
byte-identical to those from the untouched install). Before the refresh JoyPro re-applies the switched-on install
options. The full refresh on a test PC (85 profiles, 136 device types) takes about 25 minutes and writes 5071 clean
profiles (9 MB; before: 1145).

### Root cause of "DCS defaults stay on my device" (fixed 2026-10-17)
Checked with `engine dcs verify-clean` on a copy of the user's Saved Games after a GUI-default export (`merge`), with
the clean profiles of 2026-10-13:
1. The written devices kept every DCS default in the aircraft the profile does not bind (C-130J, CH-47F, F-100D, ...)
   and in exported aircraft where that device has no bind (a device bound only in some aircraft): only `clean` mode
   touched other aircraft, and never the exported ones. Now every mode removes the DCS defaults of the written
   devices in every aircraft (`--clean-all-aircraft`, default true; outside clean mode the files keep their binds).
2. The refresh built device-type clean profiles only for types with a default file of their own. Types that
   `DefaultAssignments.lua` gives other axes (MFG Crosswind V2, the WINWING / F16 MFD panels, ...) got the generic
   stick's removals: F-15C "Wheel Brake Left" stayed on the MFG's JOY_X, and the panels got removals of axes they never
   had. Now these types and all of the user's own devices get their own clean profiles.
Result on the same copy: 0 DCS defaults left on the written devices (also the disconnected ones, checked with fake
sticks), in every aircraft; left only in the input layers JoyPro does not touch ("General" = Config\Input\Aircrafts\
Default, Combined Arms = ground_unit) and on the devices JoyPro does not write (devices outside the profile, e.g. the
extra HID interfaces of a Wooting keyboard), which nuke or the strip option below cover. Old-vs-new export diff (same
profile, same DB): 1616 files identical, 313 new (other aircraft), 91 MFG / WINWING files with the exact removals of
their type instead of the generic ones; no added bind or force feedback setting changed; all 932 written files load in
DCS's luae.exe, none has a BOM. Not covered: a device the user ran DCS's axis wizard for (`wizard.lua`, per device
instance) keeps the wizard's assignment as its default.

### Checking (`engine dcs verify-clean`)
`engine dcs verify-clean [--path <Saved Games\DCS>] [--planes ...] [--devices ...] [--include-disconnected true]`:
DCS's own Lua loads every input profile with a temporary copy of the folder's `Config\Input` (diffs, modifiers,
wizard.lua, disabled.lua) and lists every bind still active on a joystick that its `.diff.lua` does not add itself:
DCS defaults that stay. Connected joysticks are checked as DCS names them; with `--include-disconnected` the devices
that only have files get a fake stick of their type with a copy of their files. "Defaults Left On JoyPro Devices"
counts those on files JoyPro wrote (a `.jp` next to them). About 90 s for all aircraft.

## Default binds in the DCS install (Settings > DCS: default binds)
Two optional changes to the DCS installation itself, for all devices (also those JoyPro does not write), both off by
default: JoyPro's export already removes the defaults of the devices it writes (above), and patched files fail the
integrity check of multiplayer servers that enforce it. The GUI shows that warning and asks before each change.
- **Strip** (`engine dcs strip-defaults [apply|restore|status|auto] [--dry-run] [--restore] [--skip-ui-layer]`): every
  `*.lua` in a folder named `joystick` under `<DCS>\Mods`, `<DCS>\Config` and `Saved Games\DCS*\Mods` (not
  `*.diff.lua`; only files with `keyCommands`, `axisCommands` or `external_profile`) is wrapped in a function, and the
  table it returns gets `combos = nil` on every keyCommands/axisCommands entry. The first line is the marker
  `-- [[DCS_STRIP_JOYSTICK_DEFAULTS v1]]`, the original stays as `<file>.nocombo.bak` (the community script's names:
  either one can undo the other's work; JoyPro's restore covers files that script patched). Original bytes, line ends
  and a UTF-8 BOM stay. After patching, DCS's own loader (`Input.Data.loadDeviceProfileFromFile` in luae.exe) reads
  every patched file and its original: same commands, no combos left; a file that fails is put back at once. On a copy
  of a test PC's install: 218 files, all compared, 2525 default binds switched off; restore byte-identical (8231
  files). `--skip-ui-layer` keeps `Config\Input\UiLayer`. The user's `Saved Games\DCS\Config\Input` is never touched.
- **Device files** (`engine dcs device-luas [list|remove|restore]`): the device-specific default files of the
  joystick folders (`<device type>.lua` and template diffs `<device type>.diff.lua`, not `default.lua`) move into
  `<data>\DcsDefaults\DeviceLuas` (with `manifest.json`), so DCS uses `default.lua` for every device (JoyPro 1's way).
  Files another Lua file of the module names stay, and so does the UI layer (its VR controller files make the menus
  usable in VR). On a test PC's install: 554 files. After a move or restore the clean profiles no longer match what
  DCS loads; the GUI rebuilds them and the export warns until then. `engine dcs defaults restore` still puts back what
  an older JoyPro moved into `Saved Games\DCS\BACKUP_JOYSTICK_DEFAULTS`.
- The switches are kept in `<data>\DcsDefaults\state.json`. A DCS update or repair brings original files back; with a
  switch on, JoyPro does it again at its start (`strip-defaults auto`), before every DCS list refresh and on "Apply
  now". `status` reports what is patched, what DCS brought back (`Needs Reapply`) and the DCS version. All commands
  refuse while DCS or its updater runs; without write access to the DCS folder they say so.

## Export modes
- `merge-overwrite` (the engine's default when `--mode` is left out), `merge` (the GUI's default, "Update, keep the
  game's binds"), `add`: the profile's binds are merged into the existing files (conflicts per input). In all three the
  DCS defaults of every device the export writes are removed (rule 5: JoyPro takes over whole devices), in every
  installed aircraft (the other aircraft's files keep their binds); the GUI's mode texts say so.
- `clean`: the written devices get only JoyPro's binds. They also lose their binds and DCS defaults in every other
  installed aircraft (every profile with a clean profile; input layers such as UiLayer, Camera and Default are not
  touched).
- `--clean-all-aircraft false`, or a plane filter, limits the removals to the aircraft the profile binds (GUI: "DCS: my
  devices lose the DCS default binds in every other aircraft too").
- `--nuke-untouched-devices true`: in the exported aircraft, devices the profile does not use there lose all binds
  and their DCS defaults.
- Force feedback settings (`ffDiffs`) of a file that is emptied stay.

## Verification
- Engine smoke (`engine\tests\engine_smoke_test.ps1`): default removals, kept removals, nuke, clean of the other
  aircraft and `--clean-all-aircraft false`, device type clean profiles, DCS device names with spaces (export into
  the DCS-named file, legacy trimmed files removed, import back to the JoyPro name), no BOM. `smoke\dcsdefaults.ps1`:
  strip / restore / status / auto / `--skip-ui-layer` / dry run / a file the community script patched / device files
  on a synthetic DCS folder, each back byte for byte.
- Headless check 2026-10-10 (`luae.exe`, temporary Saved Games): the fake stick of a device type gives the same
  diff as the real connected device (WINWING F18 TAKEOFF PANEL); the generic clean profiles of A-10C II and FA-18C
  are identical to the previous ones. 2026-10-17: the new refresh keeps all 1145 earlier clean profiles byte for byte.
- In game: `tools\e2e\games\DCS.ps1` and `DCSCurves.ps1` (binds and axis tuning in the controls menu).
