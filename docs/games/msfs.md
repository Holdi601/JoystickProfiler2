# Microsoft Flight Simulator 2020 / 2024 (experimental; 2020 verified in game 2026-10-10 by `tools\e2e\games\MsfsCurves.ps1`: binds, Sensitivity page, buttons and axis curves in a flight; 2024 verified in the sim 2026-10-10 by `tools\e2e\games\Msfs2024Settings.ps1`: presets as a Community2024 package, random vJoy axes and buttons and the per-action curve override show in Controls)

## Two games: MSFS 2020 and MSFS 2024
JoyPro handles the two sims as two games side by side (both can be installed; a test PC has both on Steam):

| | MSFS 2020 (`MSFS2020`) | MSFS 2024 (`MSFS2024`) |
|---|---|---|
| CLI | `engine msfs2020` (`fs2020`) | `engine msfs2024` (`fs2024`; old name `msfs`) |
| Install | Steam app 1250410 (`FlightSimulator.exe`), Store `Microsoft.FlightSimulator_8wekyb3d8bbwe`, `%APPDATA%\Microsoft Flight Simulator` | Steam app 2537590 (`FlightSimulator2024.exe`), Store `Microsoft.Limitless_8wekyb3d8bbwe`, `%APPDATA%\Microsoft Flight Simulator 2024` |
| Export target (`paths`) | the Steam `remote` folders of app 1250410 (`inputprofile_*`) | JoyPro's preset package `Community2024\joypro-input-profiles`, then the Steam `remote` folders of app 2537590 |
| Process (export refused while it runs) | `FlightSimulator` | `FlightSimulator2024` |
| Catalog | `DB\MSFS2020\actions.tsv` | `DB\MSFS2024\actions.tsv` |
| Aircraft list | `DB\MSFS2020\aircraft.tsv` (44 aircraft on a test PC) | `DB\MSFS2024\aircraft.tsv` (81) |
| `refresh-db` reads | MSFS 2020's presets (`<install>\Input`, its Official packages) and locPak | MSFS 2024's presets (`fs-input-profiles`, `asobo-input-profiles-pc`) and locPak |

Both use the same code (`engine\Games\MsfsIntegration.cs`, one instance per sim) and file reader, so either game
still reads and writes a profile file or folder of the other sim's format when it is given with `--path`. The axis
model is `msfs` for both (tuning scopes `game:MSFS2020`, `game:MSFS2024`, and `family:msfs` for both). Both catalogs
started as the shared catalog of the single MSFS game (built from both sims' data); each refresh only adds its own
sim's actions. The aircraft lists come from the same discovery (both sims are read: a streamed 2024 aircraft takes its
title from the same 2020 package), each game keeps the aircraft of its sim.

Profiles and settings saved before the split (game `MSFS`) are migrated when they are loaded
(`engine\Games\LegacyGameIds.cs`): a node `MSFS:<action>[:<aircraft>]` becomes an `MSFS2024` and an `MSFS2020` node with
the same aircraft; `game:MSFS` tuning becomes `family:msfs`. A folder set for "Microsoft Flight Simulator" in Settings >
Games goes to the sim it belongs to (`FlightSimulator.exe`, `1250410` or the 2020 Store package -> 2020, else 2024),
"I do not have MSFS" hides both. `engine msfs ...` and `--node MSFS:<action>` still work (MSFS 2024, and a node per
sim). Ownership data and backups stay in `%LOCALAPPDATA%\JoyPro\Ownership\MSFS` / `Backups\MSFS` (kept per file
path). The watcher's records (`<data>\Sync\MSFS.json`) are not carried over; the next export records each sim again.

## Files
- **2020 Steam:** `<Steam>\userdata\<id3>\1250410\remote\inputprofile_<10 digits>` (XML, no extension). `profile_00` (binary) maps device instance GUIDs to the inputprofile files. Steam Cloud folder.
- **2020 MS Store:** `%LOCALAPPDATA%\Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\SystemAppData\wgs\...` containers; blobs are renamed on every save. **Read only** (imported, see below).
- **2024 Steam:** `<Steam>\userdata\<id3>\2537590\remote\inputprofile_*`. **2024 Store:** `Microsoft.Limitless_8wekyb3d8bbwe\SystemAppData\wgs\...`. Profiles are also held server-side.
- **2024 supported route:** Settings → Controls → cogwheel → Export/Import (same controller and same category only). [SRC MS support]
- **2024 JoyPro route: Community package** (`engine\Games\MsfsCommunityPackage.cs`). MSFS 2024 loads device presets
  from packages; Asobo ships the presets of known sticks that way (`<install>\Packages\asobo-input-profiles-pc`,
  `fs-input-profiles`). Each preset is plain XML under `InputProfiles\Categories\<Airplane|Glider|Helicopter|LighterThanAir|Other|Transversal>\`
  with root `<DefaulftInput Primary="1" PlatformAvailability="PC">` and `<Device DeviceName GUID ProductID="C215" CompositeID HWVer>`.
  The sim matches a preset to a device by the USB product ID (hex). A preset lists only the bound actions. The
  package also has `layout.json` (every file with size and FILETIME date) and `manifest.json`
  (`package_order_hint: INPUT`). The exe confirms the folder rules (`InputProfiles/Categories/<Category>`). [OBS, SRC SDK "Input Profiles"]
  - JoyPro writes `<packages>\Community2024\joypro-input-profiles\InputProfiles\Categories\Airplane\JoyPro_<device>_<guid8>_2024_Planes.xml`
    per device, then layout.json and manifest.json (`builder: Microsoft Flight Simulator 2024`). `<packages>` is
    `InstalledPackagesPath` in `%APPDATA%\Microsoft Flight Simulator 2024\UserCfg.opt` (Store: `...\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\UserCfg.opt`).
    Community2024 is the folder for packages built for 2024: the sim's session report
    (`AsoboReport-RunningSession.txt`) counts a package there under `communityfolderfs24`, and the same package in
    `Community` under `communityfolderfs20`. The config path for `msfs export|import|scan` is the Community(2024)
    folder or the package folder (`engine msfs2024 paths` lists it).
  - **General binds (since 2026-10-19):** a device with binds of the general contexts (cameras, views, active pause, ...;
    the sim's "General" page) also gets `...\Categories\Transversal\JoyPro_<device>_<guid8>_2024_Transversal.xml`,
    the folder Asobo's own presets use for them (`asobo-input-profiles-pc\InputProfiles\Categories\Transversal\*_2024_Transversal.xml`,
    no `<AircraftInfo>`, contexts such as `COCKPIT_CAMERA`, `EXTERNAL_CAMERA`, `MODES`, `INGAME_UI`, `VIEW_MODE_SWITCHES`).
    The exe names the category folders `Airplane`, `Glider`, `Helicopter`, `LighterThanAir`, `Other`, `Transversal`
    ("... must be either ..."). General actions go only into the Transversal preset, flight actions only into the
    airplane preset; each keeps its context (the action's catalog category).
  - **Encryption (since 2026-10-19):** the package matches the EFS encryption of the Community folder it is in. A Microsoft
    Store install on another drive keeps its data encrypted (EFS), and Windows refuses a plain file there: "The APPX
    file can not be accessed because it is not encrypted as expected." (409, an EFS refusal; report on 2.2.1, MSFS 2024 Store). JoyPro
    2.1.1 to 2.2.1 removed the mark from the package before every export (meant for the 6000 case below), which caused
    it. Now the export never removes the EFS mark of the package; when the Community folder is encrypted it first repairs the package
    (`MsfsCommunityPackage.MatchFolderEncryption` / `SafeFileIo.MatchFolderEncryption`): `File.Encrypt` (EFS) on every folder
    and file that lost the mark; a file Windows does not EFS-encrypt is read, deleted and written again (it takes the
    folder's mark); a file that cannot even be read is moved to `<data>\Backups\MSFS\package-<time>` (deleted when
    that fails) and the export makes it again (a warning names the device presets that were reset; the binds come from
    the JoyPro profile; earlier copies are in JoyPro's backups). New preset files are written into the folder and take
    its mark. Everything done is logged (`MSFS` log area). Not verified on a real Store install on a second drive (no
    such PC); the simulated EFS states are covered by `SafeFileIoTests`, real EFS by the opt-in `JOYPRO_TEST_REAL_EFS=1`.
  - **When Windows cannot EFS-encrypt a folder (2.2.5 report, MSFS 2024 Store):** `File.Encrypt` (EFS) failed on every
    package folder with "The request is not supported.", so each export deleted the unreadable presets and wrote them
    again into folders without the EFS mark: plain again, unreadable again (409) for the sim and JoyPro (the export
    then found "no MSFS profile of device ..."). The preset files made while the folders still had the EFS mark stayed
    readable, so a new file or folder made in an EFS-encrypted folder takes its EFS mark there. Now such a folder is
    made again (`SafeFileIo.RebuildFolder`): a new folder beside it (checked: it must carry the EFS mark, else nothing
    changes), EFS-encrypted files moved over (they keep their EFS mark), readable plain ones written into it, unreadable
    ones moved out of the way as before, then the new folder takes the old name. Files in a folder without the EFS
    mark are no longer deleted and written again (it would not help). What still fails is a warning that says to delete
    the package folder with the sim closed and export again (EFS: the folder made then takes the Community folder's mark).
    The step "encrypted (EFS) again" counts only what worked. Test: `SafeFileIoTests.MatchFolderEncryption_FoldersWindowsCannotEncrypt_AreMadeAgain`.
  - **Import of an own preset Windows cannot read (EFS, 409):** the import first tries `File.Encrypt` (EFS) on it; else it
    reads JoyPro's newest readable copy (`<data>\Backups\MSFS\<file>.<time>.jpbak`, made by an export before it writes
    the file, or `<data>\Backups\MSFS\package-<time>\<file>`) with a warning: the sim does not use the file either (no
    EFS mark), the binds come from the copy, export once to write it again. Without a copy the warning says that its
    binds were not imported and to import with "Merge into profile" (not `--replace`), then export. The device is not
    taken as "no own profile" when the copy was read (no default preset over it). Test fault:
    `JOYPRO_TEST_FAULT_ENCRYPTION=read:409:<file name>`; test `MsfsProfileImportTests.Store2024_PresetsTheSimsRecordsDisagreeOn_AreNotImported_UnreadableOwnPresetFromJoyProsCopy`.
  - Product ID: the one the sim noted for the device in profile_00 (read only), else the USB PID. The sim sees vJoy
    as product ID 0 (not 0xBEAD); real sticks match their USB PID (WINWING 0xBE04, VKB 0x0125).
  - Axis settings go into each action as a per-action override (flag 4096 + `<Axis/>`): the sim ignores a preset's
    `<Axes>` block. The device page (Hardware Settings) stays per device.
  - No `DeviceConfig.xml`: an entry without a display name renamed the device to "Unknown Device" in the sim.
  - A device needs its product ID, so it must be connected for the first export. The preset is created once and
    reused. New actions go into their catalog context.
  - **When the sim uses the preset (in-sim check 2026-10-10):** the sim keeps one record per device and aircraft in
    profile_00 and fills it with the device's default preset when it creates it (first time that device is
    configured for that aircraft). The vJoy test preset showed exactly the exported random binds for an aircraft the
    sim had not configured yet (Cessna 185: flaps down button 7, rudder RAXIS Y, ailerons LAXIS Y, elevator RAXIS X,
    nav lights button 13). For an aircraft already configured with that device the old record stays (empty or the
    previous preset). Only Controls > Reset to Defaults (End) re-applies the defaults, and it resets the presets of
    every device and aircraft ("Your custom profiles will not be deleted"), so JoyPro does not do it.
  - JoyPro never edits profile_00: the file ends with a checksum, and a changed file made the sim discard the whole
    profile (new profile_00, "Connect to Xbox Network" with account linking on the next start). The original file
    put back fixed it.
- **2024 `profile_00`** (Steam `remote`): a binary tree. A node is an 8-byte name hash, a uint32 size (header included)
  and a payload: a container holds a uint32 count and child nodes, a leaf holds a value (strings NUL-terminated).
  2020 uses the same format. The tree holds:
  - a device table (instance GUID string, type);
  - per device, the category scope (AIRPLANE) and the aircraft scope (`asobo-aircraft-c172sp-as1000`);
  - per device and aircraft, three preset slots (general = transversal, category, aircraft), each with the preset
    file name (`keyboard_2024_planes.xml`; empty = none). [OBS 2026-10-10]
  - field hashes (2026-10-19, `engine\Games\MsfsSimProfile.cs`): instance GUID `e494a07827809546` (string; keyboard
    and mouse a number), type `c5e29153ab70ce33` (1 joystick, 15 keyboard/mouse), product ID `9c6a107054980963`,
    composite ID `419c4be1028db14a`, preset file `e5374a443ee0986e`, category `dd0e07ae6ad0a4fc`, aircraft
    `6ef48b11820c125d`; slots `ae7f5b37a56a781c` (general), `8b57821791389404` (category), `6bc4c835749736a7`
    (aircraft) inside a record that carries the device fields itself. The file ends with 5 bytes after the tree.
  - more fields (a tester's Store profile_00, 2026-10-19): slot flag `00d10becc027d54f` (0 none, 1 shipped preset, 3
    own profile: the preset field then holds `inputprofile_<n>` and the name field its name), display name
    `877ddf5aa523fd04` (the preset's name, "TT:GENERIC.NONE" for none; often empty, also for the keyboard on the test
    PC's Steam copy, so it is not used), aircraft path `a8df1050ac9f14e5`. Under the root container `a532ec6925941962`:
    `a46c8bf66a7db8ce` (per device a general entry), `4f1fd58aaa70b44e` (per device and category), `7af6b80fdb235d3f`
    (per device and aircraft) all with empty preset fields on both PCs, and `a9281cff465213e7` (the records, one per
    device and aircraft configuration). Old device instance GUIDs keep their records.
- **2024 in-game observations** (Steam, 2026-10-10, menus only):
  - The Steam `remote` folder holds only `profile_00`, `localprofile` and `datausage` (binary), and no `inputprofile_*`, until a preset exists. No XML profiles under `%APPDATA%\Microsoft Flight Simulator 2024`.
  - A device without a preset (vJoy) shows no preset selector and no preset gear. Its device gear only offers Rename / Deactivate / Hardware Settings, so there is nothing to export until one bind creates a preset.
  - The help page says: preset management is the gear next to the preset selection; binding = select the action, click an empty slot (shows "Scanning..."), then move the axis.
  - The scan takes the first input of *any* device. A device that keeps sending (a VPforce Rhino FFB stick: the Control listener shows SLIDER X/RX changing with nobody touching it) makes the slot show "WRONG DEVICE", and the bind never happens.
  - Hardware Settings (device gear) = device-level curves per axis (Sensitivity +/-, Neutral, Dead zone, Extremity Dead Zone, shown as 0.00) with a live dot on the curve: a menu-only way to check curves without a flight.
  - Per-action settings (row gear): input repetition, delayed input, set on release, inverted axis, current axis value, input value override, device input curve override, tweak action curve.
- Default device profiles: `<install>\Input\*.xml`.
- Cloud (Steam, Xbox, 2024 server) can revert local edits. New `inputprofile_*` files are not picked up (profile_00 references specific files): only edit existing ones.

## Import of your own profiles (since 2026-10-17)
A user report (MSFS 2024, Microsoft Store) failed with "The JoyPro MSFS 2024 package has no presets yet": import read
only JoyPro's own preset package, and a fresh install had no command list. Now import and scan (`engine msfs2024|msfs2020
import|scan`, GUI import wizard) read what the user set up in the sim (`engine\Games\MsfsUserProfiles.cs`):
- **Steam:** every `<Steam>\userdata\<id>\<app>\remote\inputprofile_*` (2020: 1250410, 2024: 2537590).
- **Microsoft Store / Xbox app:** `%LOCALAPPDATA%\Packages\<family>\SystemAppData\wgs\<user>\` (2020
  `Microsoft.FlightSimulator_8wekyb3d8bbwe`, 2024 `Microsoft.Limitless_8wekyb3d8bbwe`). The Xbox "connected storage":
  `containers.index` (uint32 version, int32 count, uint32, string package name, FILETIME, uint32, string, uint64; per
  container: name, name again, quoted hex id, byte file number, uint32, folder GUID, FILETIME, 16 bytes; strings are
  int32 length + UTF-16LE) lists the containers; `<folder GUID>\container.<n>` lists the blobs (uint32 4, int32 count;
  per blob 64 UTF-16 characters of name, file GUID, the GUID again); a blob is the file named by its GUID (32 hex
  digits). Format as the Store-save tools document it [SRC XGP-save-extractor]; not checked against a real MSFS Store
  install (none on a test PC). Every listed blob whose content is an input profile is read (plain XML, UTF-16 with BOM,
  or zlib/gzip-compressed, decompressed into `<data>\Cache\MSFS\wgs`); an index of another layout makes JoyPro check
  every GUID-named file instead (warning). The engine is a .NET Framework program: paths over 260 characters are not
  read (a real blob path is about 205 characters plus the user name).
- **JoyPro's package** (`Community2024\joypro-input-profiles`) is read besides; its absence is no error.
- Per device and category one profile: `--game-profile <name>`, else the most recently saved one (a warning names the
  others). JoyPro's presets and the user's profiles are chosen separately. Keyboard/mouse/gamepad profiles (`GUID="{0}"`)
  are counted, not imported; binds of every managed context are imported, flight and general ones (see "Managed
  contexts"; only menu, world map and developer binds are counted and left out); axis tuning (device Sensitivity page,
  per-action override, invert, no-curve flag) as before. Flight actions are read first, so an input bound to a flight
  and a camera action (throttle axis and cockpit zoom) gives a relation named after the flight action; an axis the
  file inverts for one of them and not the other becomes two relations (one relation has one invert).
- An action the user binds that the command list lacks is added to it (`actions.tsv`) with its context as category
  (also actions without `KEY_`, such as `DRONE_ZOOM_IN`); the title is the sim's menu name (en-US.locPak) when the
  sim is installed, else one from the event name (`KEY_COCKPIT_CAMERA_ZOOM_AXIS` -> "Cockpit Camera Zoom Axis"). An
  action the list has in a flight context that a profile of this sim binds in a general one (the shared list has
  `KEY_ACTIVE_PAUSE_TOGGLE` in PLANE from MSFS 2020; MSFS 2024 binds it in `INGAME_UI`) gets the general context, so the
  export writes it into the Transversal preset.
- **Devices that are not connected (since 2026-10-19):** MSFS keeps a profile of every device it ever saw (Store saves,
  Steam remote files) while the sim lists only the connected ones; a 2.2.1 import brought relations of four devices the
  tester no longer had. Import and scan now take the profiles of connected devices (the profile's instance GUID among
  the connected DirectInput devices) and name the others in a step ("Profiles of N device(s) that are not connected were
  left out ..."). `--include-disconnected` (GUI: "Also import devices that are not connected" on the import wizard and
  the Other Games form, off by default, remembered with the export settings) imports them too. With no device
  connected at all everything is imported (with a note), so an import on another PC still works. The watcher's
  automatic import uses the default. MSFS only: the other games keep one bind file without a per-device history, so
  they do not have this problem and keep their import as it was.
- **Devices on the sim's default preset (since 2026-10-19):** MSFS saves a device's profile only once the user changes
  something; an untouched device runs on the preset the sim ships for it. A 2.2.3 import (MSFS 2024) brought only the
  four devices the tester had customized, not his T-Pendular-Rudder (brakes L-Axis Y / X, rudder L-Axis Z, sidebar
  "T-Pendular-Rudder presets config"). Now import and scan (`engine\Games\MsfsSimDefaults.cs`) take, for every
  connected device and category without an own profile, the default preset the sim uses: the shipped presets
  (`MsfsGameData.PresetFiles`: 2024 `fs-input-profiles`, `asobo-input-profiles-pc`, `InputProfiles\Categories\<Category>\*.xml`)
  matched like the sim does, by the hex `ProductID` of `<Device>` (the one the sim noted for the device in profile_00,
  else the USB PID; the sim's own `InputProfiles\DeviceConfig.xml` also lists devices by `ProductId` only; the
  `GUID` and `DeviceName` of a shipped preset are Asobo's, not the user's), the preset with `Primary="1"` (`Primary="0"`
  are the alternatives of the preset selector). Categories: Airplane and Transversal (General). Order: the user's own
  profile of that category, then JoyPro's package preset, then the shipped default. Presets without a device GUID
  (keyboard, mouse, gamepad) are left out; when several primary presets share the product ID with different
  `CompositeID` (VelocityOne Flight: yoke and quadrant) none is taken and a step says so. Each device gets a step
  ("T-Pendular-Rudder: no own MSFS profile, imported the sim's default preset 'T-Pendular-Rudder 2024 Planes'
  (Airplanes)"). The preset's `<Axes>` page is not imported as tuning (the sim ignores it); invert flags are. Not with
  `--path <file>` or `--game-profile`. With no saved profile at all the import no longer fails when defaults were
  found (otherwise a step adds that none of the connected devices has a default preset). Read only: the shipped presets are never written; export goes to JoyPro's package as before (the defaults
  are written there like any other bind). Checked 2026-10-19 on the Steam install of a test PC: 100 airplane and 78
  transversal presets; none of the connected devices there (VKB 231D:0125, Virpil 3344:80CB, WinWing 4098:BE04,
  MFG, VPforce, vJoy) has one, a TPR (044F:B68F) and a Logitech Extreme 3D (C215, also a Transversal preset) given as
  test devices got theirs. MSFS 2020: same code over its presets (no categories); its shipped presets were not checked
  on a real install (none on a test PC), so a 2020 preset must also carry the device's name. Test:
  `MsfsProfileImportTests.Steam2024_DevicesOnTheSimsDefaultPreset_AreImported` (fixture `engine\tests\fixtures\msfs\shipped-presets`).
- **Only presets the sim really uses (since 2026-10-19, after 2.2.4):** a 2.2.4 import (MSFS 2024 Microsoft Store) gave a tester relations
  for his two Thrustmaster MFD Cougar panels from their shipped presets, while the sim shows no binds for them.
  Evidence (MSFS 2024 install, read only): the MFD presets look like the TPR's: `MFD_COUGAR_Left_2024_Planes.xml`
  (`ProductID="B351"`, also a Transversal one) and `MFD_COUGAR_Right_2024_Planes.xml` (`B352`, no Transversal one;
  Left and Right have their own product IDs), `Primary="1"` like `T-Pendular-Rudder_2024_Planes.xml` (`B68F`), and
  `DeviceConfig.xml` has the same kind of entry for all three (`ProductId` + `TextureFolder`, no flag). So the shipped
  files cannot tell why the sim applies one and not the other. What the sim applies is in profile_00: per device and
  aircraft the three preset slots (general, category with its category name, aircraft) each hold the file name of the
  preset the sim applies, in lower case (`keyboard_2024_planes.xml`, `mouse_2024_transversal.xml`), or a 0 byte; a
  flag beside it is 1 when a preset is set. On the test PC's Steam profile_00 the keyboard and mouse slots name their
  presets in 52 airplane / 61 transversal records, the slots of the 13 joysticks (none with a shipped preset) are all
  empty.
  **2.2.5 report (same tester, Store):** the MFDs still came in, because one record named their presets. His
  profile_00 (read only, copy): the current T-Pendular-Rudder (044F:B68F) has 4 records (Bonanza G36, C172 cargo,
  C172 passengers, A320neo), the category slot of every one names `t-pendular-rudder_2024_planes.xml` with the display
  name "T-Pendular-Rudder 2024 Planes", general slot none ("TT:GENERIC.NONE": no Transversal preset), aircraft slot
  none. F16 MFD 1 (B351) and MFD 2 (B352) have 4 records each: in 3 the general (MFD 1) and category slots carry flag 1
  with an empty file (a state seen nowhere else, also not on the test PC), only the A320neo record names
  `mfd_cougar_left_2024_transversal.xml` / `mfd_cougar_left_2024_planes.xml` (MFD 2: `mfd_cougar_right_2024_planes.xml`)
  without a display name, its aircraft slot "TT:GENERIC.NONE". The per-device entries outside the records are empty
  for all three (also for the keyboard on the test PC, which runs on its preset), and "TT:GENERIC.NONE" in an
  aircraft slot also sits beside the VMAX throttle's own profile that the sim uses, so neither marks "none". The
  sim does not use the MFD presets (no binds in Controls), so a preset counts only when the records agree.
  Rule (`MsfsSimProfile.cs` records, `MsfsSimDefaults.ForConnectedDevices` / `Votes`):
  - per device and category the records vote: General = the general slot of every record; Airplanes = of the records
    with category AIRPLANE the aircraft slot when it names a file, else the category slot (2020: the first slot that
    names one);
  - all name the same shipped preset: imported (also an alternative the user picked in the sim);
  - none names one: no import, and when the device has a shipped preset a step says so ("F16 MFD 1: the sim has a
    default preset 'MFD COUGAR Left 2024 Planes' (Airplanes) but does not use it ...");
  - some do, some do not (or different ones): not known, not imported, a step: "F16 MFD 1: the sim's records of this
    device (one per aircraft) name its default preset 'MFD COUGAR Left 2024 Planes' (Airplanes) in only 1 of 4, ...";
    `--include-sim-defaults` takes the one most records name ("(--include-sim-defaults: the sim's records name it
    for 1 of 4 aircraft)"). On his copy: the rudder's preset is imported, the MFDs get nothing.
  - no records of the device or none of that category (no profile_00 found, a format JoyPro cannot read): unknown, not imported, a step names the
    preset and the opt-in: `--include-sim-defaults` (GUI: "Also import default presets the sim may not use" beside
    "Also import devices that are not connected", import wizard and Other Games page, remembered in
    `export-settings.json`), which takes the Primary preset of the product ID as before. The opt-in never overrides
    records that all name no preset.
  - profile_00 sources: `<Steam>\userdata\<id>\<app>\remote\profile_00` and every blob of the Store save folder whose
    content (plain or zlib/gzip-compressed) is the same tree (a step says which were read). The Store blob was not
    checked against a real Store install (none on a test PC): the tester's TPR keeps its preset by default only if his
    Store profile_00 is found and names it; otherwise the opt-in brings it back.
  Tests: `MsfsProfileImportTests.Steam2024_DevicesOnTheSimsDefaultPreset_AreImported` (rudder and throttle named,
  MFD slots empty), `..._WithoutTheSimsRecords_DefaultPresetsOnlyOnRequest_StoreRecordsAreRead` (profile_00 built
  like the sim's, also as a compressed Store blob) and `Store2024_PresetsTheSimsRecordsDisagreeOn_AreNotImported_...`
  (the 2.2.5 records in minimal form: 4 aircraft, rudder named in all, MFD in 1).
- **Skipped devices:** the profiles of devices that are not connected are left out before JoyPro picks one profile per
  device, so "Device 'VPC CM3 Throttle' has 2 MSFS profiles; imported the most recently saved one" no longer appears
  for a device that was not imported (2.2.5 report).
- A tester's case as a test (`MsfsProfileImportTests.Steam2024_GeneralBindsAreImported_...`): VPC VMAX Prime Throttle
  with flaps / throttle 1 / throttle 2 axes (airplanes) and cockpit view zoom axis, reset / toggle cockpit view, toggle
  external view and active pause (General), plus an old MT-50CM3 stick that is not connected.
- Nothing found: the error names every folder searched (package, Steam remote folders, the Store save folder with the
  number of saved files that were no input profile) and suggests Controls > cogwheel > Export + import of that file.
- Export is unchanged: 2024 writes JoyPro's package (the Store version loads it from its own Community2024 folder,
  `InstalledPackagesPath` of `...\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\UserCfg.opt`), 2020 the Steam remote
  folder; an export to a Store save folder is refused ("read only").
- Config watcher (`engine sync`): besides the export target it watches `inputprofile_*` of the Steam remote folders and
  `containers.index` of the Store save folder (any container save changes it; an empty diff is accepted silently), and
  `sync diff` reads the game as import does.
- Store installs of the sim itself: `<drive>\XboxGames\<Microsoft Flight Simulator[ 2024]>\Content` (also the folders
  of `<drive>\.GamingRoot`) count as install roots for the catalog refresh.
- **Command list on demand:** a fresh install ships no game lists. A scan, import or export of a registry game without
  `actions.tsv` builds it first as refresh-db would (progress shown; a dry run keeps it in memory); MSFS also takes the
  user's own profiles when the sim's presets are not readable. Star Citizen's import/export builds its list the same
  way. Tests: `JOYPRO_TEST_MSFS_HOME` (stand-in `Local`, `Roaming`, `Steam` folders), `JOYPRO_NO_CATALOG_BUILD=1`;
  `engine\tests\smoke\msfsuserprofiles.ps1`, `gui\JoyPro.Gui.Tests\MsfsProfileImportTests.cs`, journey scenario
  `msfs-store-import`.
- **Steam installs (2026-10-17):** 2020 keeps one `inputprofile_*` per device; a 2024 remote folder without a saved
  custom preset holds only `profile_00`, `localprofile`, `datausage`. Where MSFS 2024 writes a custom preset made in Controls was not
  checked in the sim (the docs above expect `inputprofile_*` in the remote folder; the 2024 exe has no literal
  `inputprofile_` string, so this stays an assumption).

## Format (XML fragment: several top-level elements, read with ConformanceLevel.Fragment)
```
<?xml version="1.0" encoding="UTF-8"?>
<Version Num="1238"/>
<FriendlyName>Saitek Pro Flight X-55 Rhino Stick</FriendlyName>
<Device DeviceName="Saitek Pro Flight X-55 Rhino Stick" GUID="{738A3930-CD8E-11EA-8004-444553540000}" ProductID="8725">
	<Axes>
		<Axis AxisName="X" AxisSensitivy="0" AxisDeadZone="0"/>
	</Axes>
	<Context ContextName="PLANE">
		<Action ActionName="KEY_AP_MASTER" Flag="2">
			<Primary>
				<KEY Information="Joystick Button 2">1</KEY>
			</Primary>
		</Action>
		<Action ActionName="KEY_AXIS_ELEVATOR_SET" Flag="4">
			<Primary>
				<KEY Information="Joystick L-Axis Y ">1042</KEY>
```
2024 adds `CompositeID`, `HWVer`, `<AircraftInfo CategoryName="AIRPLANE"/>`, `ValueEvent`, `Delay` and per-action `<Axis .../>` overrides (need flag 4096). Axis attributes (misspelled): `AxisSensitivy`, `AxisSensitivyMinus`, `AxisNeutral`, `AxisDeadZone`, `AxisOutDeadZone`, `AxisResponseRate` (-1 default), range -100..100 [INF].

Keyboard combo: `<Primary><KEY Information="Ctrl">162</KEY><KEY Information="Up">38</KEY></Primary>`, alternative in `<Secondary>`.

### Flags [SRC Input Configuration XML Properties]
1 ANALOG, 2 DIGITAL, 4 AXIS, 8 NO_MOD, 16/32/64 MOD_CTRL/SHIFT/ALT, 128 MOD_INV (invert), 256 MOD_UP, 512 MOD_VALUE, 1024 NO_SENS_CURVE, 2048 RESET_ONCONTEXT_DISABLED, 4096 AXIS_OVERRIDE, 8192 MOD_PRESSED, 16384 DELAYED_EXCLUSIVE, 32768 NO_KEYBOARD_LAYOUT. Examples: 132 inverted axis, 8194 digital once, 4100 axis with override.

### KEY codes [OBS]
| Input | Code |
|---|---|
| Joystick Button N | N-1 |
| Pov Up/Right/Down/Left | 256/257/258/259 |
| Slider X / Y | 514 / 530 |
| R-Axis X / Y / Z | 770 / 786 / 802 |
| L-Axis X / Y / Z | 1026 / 1042 / 1058 |
| half axis L-X− / L-X+ | 1024 / 1025 |
| Keyboard | Windows VK codes (Shift 160, Ctrl 162) |

Scheme [INF]: base 0x100 POV, 0x200 slider, 0x300 R-axis, 0x400 L-axis; +0x10 per axis; low nibble 0 negative half, 1 positive half, 2 full axis. `Information` strings of axes end with a space. POV diagonals and a second POV are unknown. The docs say not to invent these values.

## Devices
`GUID` = DirectInput instance GUID (machine specific), `ProductID` = decimal PID, `CompositeID` for multi-part devices. Keyboard/mouse `GUID="{0}"`. One profile file per device and profile.

## Flight contexts [SRC Input_Profiles]
AIRCRAFT, BASIC_CONTROL(_HELICOPTER/_LIGHTER_THAN_AIR), INSTRUMENTS_CONTROL, PLANE, HELICOPTER, GLIDER, LIGHTER_THAN_AIR, RUDDER_AXIS, SURFACES(_HELICOPTER/_LIGHTER_THAN_AIR), THROTTLE; 2024 also AIRCRAFT_PC, SURFACES_GYRO, AIRCRAFT_ACTIVE_INTERACTION, HELICOPTER_ACTIVE_INTERACTION. Everything else (cameras, MENU, MODES, DRONE, SLEW, DEBUG, ATC) is general.

## Managed contexts (since 2026-10-19)
Until 2.2.1 JoyPro read and wrote only the flight contexts; a tester's cockpit view zoom axis, view toggles and active
pause (the sim's "General" page) were not imported. Now JoyPro manages every context except the sim's own navigation
and developer ones (`MsfsIntegration.IsManagedContext`):
- **Left alone:** `MENU`, `MENU_*` (menu navigation), `WORLDMAP_*` (world map), `DEBUG`, `DEVMODE*`, `EDITOR_*`
  (developer mode and its editors), `MISSION_FLOW_TOOLS`. They are UI navigation and debug binds, not the user's flight
  setup; import counts them ("bind(s) in menu, world map and developer contexts left out"), export never changes them
  (except `clean`/nuke, which take over the whole device as before).
- **General (managed):** everything else outside the flight contexts, e.g. `ALWAYS`, `ATC`, `COCKPIT_CAMERA`,
  `COCKPIT_GLOBAL_CAMERA(_TRANSLATE)`, `COCKPIT_INTERACTIONS(_LOCK)`, `CURSOR(_MODE_SWITCHES)`, `DRONE(_GLOBAL)`, `EFB(_CORE)`,
  `EXTERNAL_CAMERA`, `FIXED_CAMERA`, `HANGAR_CAMERA`, `INGAME_UI`, `INSTRUMENTS_CAMERA`, `MODES`, `MODE_PAUSE`,
  `PC_MODE_SWITCHES`, `PHOTO_MODE_*`, `PLAYER_CHARACTER_*`, `SIM_SHORTCUT`, `SLEW`, `SMART_CAMERA`, `TOOLBAR`,
  `USER_CAMERA`, `VIEW_MODE_SWITCHES` (the contexts of Asobo's 2024 Transversal presets, checked 2026-10-19; real 2020
  profiles bind `KEY_COCKPIT_CAMERA_ZOOM_AXIS` in `COCKPIT_GLOBAL_CAMERA`). A new context of a later sim is managed too.
- Export writes a general action into its context: 2020 / Steam profiles in place (the context the action has in the
  folder's profiles, else its catalog category), 2024 package presets into the device's Transversal preset (see above).
  Ownership, merge rules and backups are the same as for flight actions.

## Action list
2024 profiles list every action of their contexts (unbound ones as empty `<Action/>`). 2020 profiles only hold bound actions: seed with the SDK Event IDs (`KEY_` + name).

## JoyPro implementation (`engine msfs2020 ...`, `engine msfs2024 ...`)
- Config path: one profile file or a folder (Steam `remote` folder of 2020/2024, or a folder of 2024 exports). `engine msfs2020 paths` lists the 2020 Steam folders, `engine msfs2024 paths` the 2024 package and Steam folders; both also list the Microsoft Store save folder (`...\SystemAppData\wgs`) when there is one. Store profiles are imported, never written (see "Import of your own profiles").
- 2024 workflow: run `engine msfs2024 export <Community folder>` (GUI: the discovered package path), then start the sim. The old route still works for an exported file: Controls > cogwheel > Export, `engine msfs2024 export <file>`, then Import in the sim. 2020 Steam: edit the `remote` folder with the sim closed.
- Device = the `GUID` attribute (DirectInput instance GUID), so no connected device is needed; keyboard/mouse profiles (`GUID="{0}"`) are skipped.
- One profile per device (and 2024 category) is used: the one picked with `--game-profile <FriendlyName>`, or the only one. Devices with several profiles are skipped with a warning listing their names.
- Written: only actions in managed contexts (flight and general, see above) and in the catalog; only changed `<Action>` elements are re-rendered. Axis binds set flag 4 and 128 (invert) and the axis settings below; a released 2024 action becomes an empty element again, a released 2020 action is removed.
- KEY codes: buttons N-1, first hat up/right/down/left 256-259 (no diagonals), full axes X/Y/Z 1026/1042/1058, Rx/Ry/Rz 770/786/802, sliders 514/530. The last KEY of a combination is the main input, other buttons of the same device are modifiers; axes take no modifiers. Half axes are not read.
- New actions: 2024 files already list all actions of their category (an action no profile of the device holds, e.g. a General bind of a device without a Transversal profile, is reported once and skipped); in 2020 files the action is added to the context it has in the other profiles of the folder, else (general actions) its catalog context, else PLANE (with a warning). JoyPro's 2024 presets: general actions into the Transversal preset, flight actions into the airplane preset.
- Primary/Secondary: at most two input combinations per action; a third is reported and skipped.
- Ownership (`merge-overwrite` replaces only JoyPro's combinations) and backups live in `%LOCALAPPDATA%\JoyPro\Ownership\MSFS` and `...\Backups\MSFS`, never in the Steam-synced folder. `clean` only changes profiles of devices that JoyPro binds in the export. The backups copy the bytes (`SafeFileIo`) into JoyPro's own folder: a Store-version export failed with "The specified file could not be encrypted." (2.1.0 report) when `File.Copy` tried to keep a preset's encryption. JoyPro's package keeps the encryption of its Community folder (2.1.1 to 2.2.1 removed it, which broke Store installs on another drive with error 409; see "Encryption" above and [backups](../backups.md)).
- Catalog: `DB\MSFS2020\actions.tsv` and `DB\MSFS2024\actions.tsv` (each started as the same list: flight-context actions of MSFS 2024 airplane + helicopter exports and MSFS 2020 profiles). Input actions belong to contexts (AIRCRAFT, HELICOPTER, GLIDER, ...), not to an aircraft: a 2024 export lists every action of its contexts whatever aircraft is loaded, so the catalog is not tied to the test aircraft (Cessna 185). Axis actions are recognized by name (`AXIS`, `*_SET` of throttle/mixture/prop/surfaces).
- Refresh from the game: `engine msfs2020 refresh-db` / `engine msfs2024 refresh-db` (no path; GUI: Games > DB refresh without a source) reads the sims' own data, read only (`engine\Games\MsfsGameData.cs`): the device presets Asobo ships (MSFS 2024 `<install>\Packages\fs-input-profiles` and `asobo-input-profiles-pc`, `InputProfiles\**\*.xml`; MSFS 2020 `<install>\Input`; Store installs under the `InstalledPackagesPath` of UserCfg.opt, `Official*\*`) and adds every action of a managed context they bind, with its context (since 2026-10-19 also the general ones of the Transversal presets and the user's General profiles: cameras, views, pause, ...; an action the list has in a flight context that this sim binds in a general one moves to it; general events only the locPak names stay out, their context is unknown); new actions get the Controls menu name from `fs-base\en-US.locPak` (`INPUT.KEY_...`), known ones keep their title (import names relations after it) unless it is the raw event name (below). `--path <folder>` reads exported profiles instead.
  **Readable titles (since 2026-10-12):** the locPak writes the menu names in capitals; the catalog takes them in title
  case (`ELEVATOR AXIS` -> `Elevator Axis`, `THROTTLE 1 AXIS (-100 to 100%)` -> `Throttle 1 Axis (-100 to 100%)`; the
  panel abbreviations stay: `AP`, `NAV1`, `ADF`, `VOR`, `TACAN` ...). A known action still titled by its raw event
  name (`AXIS ELEVATOR SET`, the lists built before) is renamed too; every other known title stays. Without a locPak
  name (no locPak found, or an event it does not list) the title comes from the event name: `KEY_AXIS_ELEVATOR_SET` ->
  `Elevator Axis`, `KEY_THROTTLE1_AXIS_SET_EX1` -> `Throttle 1 Axis` (`MsfsGameData.Readable` / `TitleFromEventName`).
  The ids do not change, so relations, binds and exports are untouched; only relations made by a later import are named
  after the new title. The lists already on a PC get the readable titles with the next `refresh-db` (GUI: Games > DB
  refresh); nothing is renamed on load. Where the locPak is: Steam `<MSFS 2024 install>\Packages\fs-base\en-US.locPak`
  (2.2 MB, 2026-10), MSFS 2020 `%APPDATA%\Microsoft Flight Simulator\Packages\Official\Steam\fs-base\en-US.locPak` (the
  `InstalledPackagesPath` of UserCfg.opt). Fresh build on a test PC (2026-10-12, temp data folder): MSFS 2024 1715
  menu names + 52 from the event name, MSFS 2020 1555 + 201. Every search hit carries the event id next to the title
  (`Id`); the relation editor finds the primary flight axes first (docs/relation-builder.md "Primary axes"). **Since 2026-10-12 the refresh also adds every flight event the
  locPak names but no preset binds** (most aircraft systems: autopilot, radios, lights, fuel, engines, G1000 and GPS
  keys, breakers, ...), so a fresh install does not depend on the user's exported profiles. The locPak names each event
  but not its context, so JoyPro's own rules decide (`MsfsGameData.IsFlightEvent` / `GuessContext`): not flight are
  cameras, views, menus, UI, developer mode, debug, photo mode, xCloud, slew, EFB, toolbar, ATC, cursor, VR, replay,
  multiplayer, the on-foot character, failures and blockages, recording; the context is LIGHTER_THAN_AIR (airship,
  balloon, burner), HELICOPTER (heli, rotor, collective, cyclic, auto hover), GLIDER (water ballast, tow plane), PLANE
  (the plane-only systems of the presets: liquid dropping, grapple hook, skydive lights, G limiter, ...), else AIRCRAFT.
  Fresh build on a test PC (2026-10-12): MSFS 2024 1739 actions (presets 360 + 1379 named), MSFS 2020 1580; of the
  development list (from real exports) 1343 / 1195 ids are rebuilt, and the guessed context matches it for 91 % of the
  locPak-only events (most others are MSFS 2020 INSTRUMENTS_CONTROL rows of the shared development list). Check 2026-10-10 (2024 build of 2026-10-04): the 720 presets of both sims bind 1197 actions; every flight-context one was already in the catalog except 4 MSFS 2020 PLANE actions (back to fly, flashlight, pushback, UI window mode), now added. The locPak names 2736 KEY_ events; most of the rest are camera, menu, debug, VR and SimConnect-only events (e.g. `KEY_AXIS_THROTTLE_SET`; the 2024 Controls menu offers `KEY_THROTTLE_AXIS_SET_EX1`).

## Aircraft lists (`DB\MSFS2020\aircraft.tsv`, `DB\MSFS2024\aircraft.tsv`, `engine\Games\MsfsAircraft.cs`)
`engine msfs2020|msfs2024 refresh-db` (with or without `--path`; GUI: Games > DB refresh) also lists the installed aircraft of both
sims, read only, and merges the aircraft of its sim (column `sims`) into its `DB\<MSFS2020|MSFS2024>\aircraft.tsv` (aircraft found before stay, like the DCS planes; the file
ships with JoyPro as DCS's plane list does, and is written only when it changes, never on `--dry-run`).
- **Identity (key) = the aircraft's package name** without the `fs20-`/`fs24-` prefix of the 2024 content list, e.g.
  `asobo-aircraft-c172sp-as1000`. [OBS 2026-10-10/11] MSFS 2024's profile_00 keeps its device x aircraft input records
  under exactly these names (`asobo-aircraft-c172sp-as1000`, `microsoft-aircraft-c185f-skywagon` for the streamed
  packages `fs24-asobo-aircraft-c172sp-as1000` / `fs24-microsoft-aircraft-c185f-skywagon`; read only from a copy). An
  MSFS 2020 package has the same name (`Official\Steam\asobo-aircraft-c172sp-as1000`), so one key covers both sims.
  One package can hold several SimObjects (floats, skis): they share the key (the 2024 record is per package too).
  [INF] For `fs20-` packages that 2024 runs, and for Community packages, the record name is assumed to follow the same rule.
- **Sources:**
  - Package folders with `manifest.json` `content_type` `AIRCRAFT` (liveries are `LIVERY` and are left out, as are
    packages without an aircraft SimObject such as `*-gliders-common`): `<InstalledPackagesPath>\Official*\<store>\*`,
    `Community`, `Community2024` (UserCfg.opt of 2020 and 2024, Steam and Store) and `<install>\Packages`. Title = the
    manifest `title` (what the content manager shows), manufacturer = manifest `manufacturer` (else the cfg's
    `icao_manufacturer`). `aircraft.cfg`: `SimObjects\<cat>\<obj>\aircraft.cfg` (2020) and the 2024 modular files
    (`...\common\config\aircraft.cfg`, `...\presets\<creator>\<preset>\config\aircraft.cfg`); a cfg with `[VARIATION]
    base_container` is a livery. `[GENERAL] category` (`airplane`/`Helicopter`; MSFS 2020 lists its gliders as
    airplane) and `icao_type_designator`; the user-selectable `[FLTSIM.N] title`s (`isUserSelectable=0` = AI traffic)
    are the variants (the sim's TITLE). Packed packages: the `aircraft.cfg` paths in `layout.json` (category from the
    SimObjects folder: `Rotorcraft` = helicopter).
  - **MSFS 2024 streams its aircraft:** `Official2024\Steam` is empty and `StreamedPackages\<package>` holds only
    cached `.fsarchive` parts, so there is no readable aircraft.cfg. The owned packages are listed in the
    content list `<UserCfg folder>\<id>\Content.xml` (`<Package name="fs24-asobo-aircraft-c152" active="Activated"/>`,
    also `UserDisabled` / `SystemDisabled`). JoyPro takes the activated `-aircraft-` packages that are no livery, no AI
    traffic (`passiveaircraft`), no base or shared package (`fs-base-*`, `*-common`, `simattachmentlib`, `trainings`,
    `efb-app`). Their title comes from the same package of MSFS 2020 when it is installed, else it is derived from the
    package name (`C185F Skywagon`; column `title source` = `package name`) and the category stays empty. Without a
    content list the `StreamedPackages` names are used.
- **Format** (tab separated, `#` comments): `key, title, category, sims (2020,2024), icao type, manufacturer,
  packages (|), variants (|), title source (manifest | aircraft.cfg | package name)`.
- **Check 2026-10-11** (this PC, real refresh into a temp DB copy): 91 aircraft, 44 from MSFS 2020 (534 Official\Steam
  packages, 55 of type AIRCRAFT: commons and liveries left out) and 81 from the MSFS 2024 content list (61 aircraft
  packages there are switched off); 34 are in both. 47 of the 2024-only titles are derived from the package name.
- **Planes in JoyPro:** `engine db planes --game MSFS` lists `MSFS` (= all aircraft) and every key; the GUI's relation
  node plane picker shows them with title, category and sims. `--node MSFS2024:<action>:<key>` (`MSFS2020:` for 2020) (or `relations item
  add|enable|disable ... --node MSFS2024:<action>:<key>`) limits a relation to aircraft. A node without aircraft or with
  `MSFS` active is for all aircraft (unchanged for existing profiles). Auto Group and Auto Assign keep MSFS as one
  aircraft (`MSFS`, one input configuration for the whole game), because the export below cannot separate aircraft.

### Export of relations limited to aircraft
MSFS has per-aircraft input settings, but JoyPro cannot write them:
- **MSFS 2020:** one input profile per device (`inputprofile_*`), used for every aircraft. [SRC docs above, OBS]
- **MSFS 2024:** the sim keeps a record per device and aircraft in profile_00 with three preset slots (general /
  category / aircraft), and the exe knows `General`, `CategoryAircraft`, `CommonAircraft` and
  `SpecificAircraftInputPresetForDevice` presets [OBS: profile_00 copy, FlightSimulator2024.exe strings]. JoyPro never
  writes profile_00 (checksum: a changed file made the sim discard the whole profile). Aircraft-specific default
  presets come only with the aircraft's own package: the SDK puts them in an `inputs` folder beside
  `attachments/common/liveries/presets` of the aircraft's SimObject (`SimObjects\Airplanes\<aircraft>\inputs`), and the
  exe allows input profile asset groups only under `InputProfiles`, `SimObjects` or `SimAttachments` [SRC SDK "Aircraft
  Input Profile Package Setup", OBS exe]. Official aircraft are streamed, so JoyPro cannot add files to
  them. [INF, not tried] A Community package that overlays `SimObjects\<cat>\<aircraft>\inputs` of another package
  might be picked up, but it was not tested in the sim and could also break the aircraft's SimObject; JoyPro does not do it.
- **So the export (both sims) writes one set of binds per device for every aircraft:** the relations for all aircraft,
  plus, with `engine msfs2024 export --aircraft <key>` (GUI: Other Games > Export Aircraft), the relations limited to that
  aircraft. Without `--aircraft` a relation limited to aircraft is skipped with a warning that says why; an unknown key
  is reported. The 2024 preset then applies as before: to every aircraft the sim has not configured for that device yet.
  The game config watcher (`engine sync`) compares the game files with the relations for all aircraft (as the export
  without `--aircraft`).
- Verified: the export rules on synthetic 2020 profiles (smoke test). Not verified in the sim: anything per aircraft
  (no per-aircraft export exists to verify); the record names of `fs20-` and Community aircraft in profile_00.

## Axis settings (model `msfs`, `engine\AxisModels\Msfs.cs`)
The game's Settings > Controls > device > **Sensitivity** page (one set per physical axis of the device and profile) and
the per-action options, in the file's units (the UI's percent; the UI code `KeyCalibrationComponents.js/.html` of the
2020 install has the ranges):

| JoyPro key (`binds tuning set --scope game:MSFS2024 --param ...`) | File | Range |
|---|---|---|
| `sensitivityPlus` / `sensitivityMinus` (positive / negative half) | `AxisSensitivy` / `AxisSensitivyMinus` | -100..100 (UI slider -100..100 %; the SDK's "0..100" / "-100..0" is wrong: MS's own profiles have -50/-50) |
| `neutral` | `AxisNeutral` | -100..100 |
| `axisDeadZone` | `AxisDeadZone` | 0..100 |
| `extremityDeadZone` | `AxisOutDeadZone` | 0..100 (the UI slider also allows negative values) |
| `reactivity` | `AxisResponseRate` | -1 (off, default) or 100..2000 (the UI shows 0..100 %; the mapping is not known) |
| `noSensitivityCurve` (0/1) | `Flag` bit 1024 | the action gets the raw axis |
| `axisOverride` (0/1) | `Flag` bit 4096 + `<Axis .../>` in `<Action>` before `<Primary>` | the values for this action only (MSFS 2024) |
| Invert | `Flag` bit 128 | |

- **Import** puts the values of the bound axis (the per-action `<Axis/>` when flag 4096 is set, else the device's line)
  into the relation's `game:MSFS2020` / `game:MSFS2024` tuning; only values that differ from the defaults, so an untouched page leaves none.
- **Export** writes only what the tuning sets: a key it does not have keeps the file's value (a relation without MSFS
  values gets dead zone, extremity dead zone and sensitivity fitted from its base curve; neutral and reactivity stay).
  Lines are edited in place (other attributes and lines keep their bytes); a version-1238 line (`AxisSensitivy`,
  `AxisDeadZone` only) gets a missing attribute only when its value is not the default; a missing axis line is added in
  the sim's order (X, Y, Z, rX, rY, rZ, SliderX, SliderY) with the attributes the file's other lines use.
- **One page per device axis:** relations on the same device axis share it. Different values are reported by
  `binds tuning set` and by the export, which writes the first relation's values. `axisOverride=1` gives an action its
  own values (2024 profiles; in a 2020 profile the export warns and uses the device page). Without the key the export
  keeps the action's current override state and writes JoyPro's values where the game reads them for that action.
- A released axis (or an action that becomes digital) loses flags 4/128/4096 and its per-action `<Axis/>` (it used to
  stay behind); flag 1024 and the other bits stay unless the tuning sets `noSensitivityCurve`.
- The export warns when a changed device page also drives actions JoyPro does not manage (camera zoom etc.). Merge keeps
  a page changed in the game (not what JoyPro wrote or imported).
- **Preview, measured in MSFS 2020** (2026-10-10, `tools\e2e\games\MsfsCurves.ps1`: vJoy sweeps, SimConnect ELEVATOR
  POSITION / throttle lever; two runs with random pages, 84 points, max error 3e-4). With x the stick position and
  n = -neutral/100: the half x >= n uses **Sensitivity -**, the other half Sensitivity + (the sim works on the mirrored
  axis); a = |x - n|, h = the half's length (1 - n or 1 + n), d = dead zone/100: output 0 for a <= d, else
  v = (a - d)/(h - d) and y = (1 - e) * v(1+s)/(1-s+2sv) with s = sensitivity/100 clamped to +-0.99 (-100 = a step at the end
  of the travel) and e = extremity dead zone/100 (a cap on the output, not an early saturation). Flag 1024: the raw axis,
  but |x| <= d still gives 0 (not rescaled). Invert flips the result; THROTTLE_AXIS_SET_EX1 gets the value negated (full
  throttle at the stick's minimum). Reactivity is smoothing over time (not in the preview); its page slider shows
  (rate - 100)/19 %, -1 shows 100 %.
- **No force feedback:** MSFS has none of its own (no DirectInput effects in either exe; FFB only through third-party
  telemetry apps); assistance options (assisted yoke, auto-rudder) are not in the input profiles. No `FfbModels` entry.

Sources: https://docs.flightsimulator.com/msfs2024/html/5_Content_Configuration/Input/Input_Configuration_XML_Properties.htm, https://flightsimulator.zendesk.com/hc/en-us/articles/21862909046428, https://github.com/dmenne/FS2020Control, https://github.com/Z1ni/XGP-save-extractor.
