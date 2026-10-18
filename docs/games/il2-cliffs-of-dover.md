# IL-2 Sturmovik: Cliffs of Dover and Cliffs of Dover Blitz

> **Partly verified (2026-10-12).** Both games are installed on a test PC. Checked: Blitz 5.050 starts to its main menu
> without any prompt and on its first start creates `Documents\1C SoftClub\il-2 sturmovik cliffs of dover` with copies
> of the install's `conf.ini`, `confs.ini` and `confUser.ini` (GAME); the install's default `confUser.ini` of both games
> (file shape of the fixtures); Blitz's device naming, seen in its installed binaries (CODE, see Devices). **Not yet checked in
> the game:** binds made on Blitz's controls page and the game's rewrite of an export (`tools/e2e/games/IL2CoDBlitz.ps1`
> is written but has not run: Windows' foreground lock blocked all input that night). The original game (63950) does
> not start on a test PC: `Launcher.exe` exits after about 4 s with code 0, no window, no log, no user folder, also
> when started directly; its integration is checked against the install's default file and a user's own Steam Cloud
> file from 2015 only.

Evidence tags: **[GAME]** seen on the installed games; **[CODE]** seen in Blitz's installed binaries, looked at for
interoperability only, nothing modified or copied; **[FILE]** complete `confUser.ini` files posted by players (Steam discussions
of both apps, the ControllerBuddy profile for Blitz) and the user's own Steam Cloud copy of the original's file;
**[DOC]** Team Fusion patch notes and the TF Beginner's Guide, **[FORUM]** forum statements (Team Fusion developers,
ATAG), **[INF]** inferred.

CLI: `engine il2codblitz ...` (aliases `codblitz`, `blitz`, game id `IL2CoDBlitz`) and `engine il2cod ...` (aliases
`cod`, `cliffsofdover`, game id `IL2CoD`) with `paths|catalog|scan|import|export|refresh-db`.

## Files

| What | Where | Tag |
|---|---|---|
| Blitz install | `steamapps\common\IL-2 Sturmovik Cliffs of Dover Blitz`, `Launcher64.exe` | FILE (Steam app info) |
| Original install | `steamapps\common\IL-2 Sturmovik Cliffs of Dover`, `Launcher.exe` | FILE (Steam app info) |
| Binds and axis settings | `Documents\1C SoftClub\il-2 sturmovik cliffs of dover\confUser.ini` — **the same file for Blitz and the original** (Blitz: GAME, and CODE: `maddox.dll` builds `<Personal>\1C SoftClub\il-2 sturmovik cliffs of dover` unless a folder is passed on the command line; the original: DOC, not seen running) | GAME, DOC |
| Pre-Blitz Team Fusion mod | `Documents\1C SoftClub\il-2 sturmovik cliffs of dover - MOD\confUser.ini` (listed for `il2cod`) | DOC |
| Defaults | `<install>\confUser.ini`, copied into the user folder on the first start (read by refresh-db; JoyPro's shipped catalogs were refreshed from both installs) | GAME |
| Other files (not touched) | `conf.ini` (graphics, `[rts_joystick] FF`), `user.ini`, `bldconf.ini`; "Save As" profiles in the same folder | FORUM |

The game writes `confUser.ini` when it closes (not when closed through the launcher) and only after "Apply" (FORUM):
export only while the game is closed (JoyPro refuses while `Launcher64` / `Launcher` runs; note that the original's
process name `Launcher` is generic). **Steam Cloud** keeps copies (`userdata\<id>\63950\remote`) and has been reported to
bring old binds back: turn Cloud off for the game if an export does not stick (FORUM). Unknown sections (`[ChatWindow]`,
the mission builder's `[HotKey builder]` ...) are kept byte for byte.

## Bind lines

Sections JoyPro manages: `[HotKey pilotMove]` (axes), `pilot`, `common`, `turret`, `view`, `viewSet`, `viewPan`,
`viewSnap`, `timeSpeed`, `orders`, `BOB`, `vehicle` (FILE). Line: `<trigger> [<trigger> ...]=<command>` — all triggers
held together, the last one fires, so the others become JoyPro modifiers (keys or joystick buttons) (FILE).

| Input | Line key | Example (FILE) |
|---|---|---|
| Button | `<device>+Key<b>` (b 0-based; 128 buttons since patch 5.025) | `vJoy_Device-5A479A63+Key0=trigger1` |
| Axis | `<device>+AXE_<X|Y|Z|RX|RY|RZ|U|V>=<1|-1><command>` (sign = inversion) | `...+AXE_Z=-1throttle` |
| Hat | `Pov<angle> <device>+Pov` (the first hat) | `Pov90 Controller_(XBOX_One_For_Windows)-5AD3D7F+Pov=cste_lights` |
| Key | `[Control|Shift|Alt|<key> ...] <key>` | `Control NumPad-9=site_alt_plus` |
| Chord | several triggers | `Joystick+Key12 Joystick+Key18=autorudder_off` |

Lines without a device (`Pov0=PanUp`) are defaults the game could not resolve; JoyPro leaves them alone.

### Devices

- **Blitz (4.5+):** `<product name>-<id>` (FILE: `T.Flight_Hotas_4-AA417BA7`, `_VKB-Sim_Gladiator_-ABD81779`,
  `Controller_(XBOX_One_For_Windows)-5AD3D7F`). Seen in Blitz's installed binaries (CODE, 2026-10-12): the name is
  DirectInput's `tszProductName` with every blank and `+` as `_` (leading/trailing blanks kept); the id is **not** part of
  the instance GUID but a hash of the device's interface path (`IDirectInputDevice8::GetProperty(DIPROP_GUIDANDPATH)`,
  e.g. `\\?\hid#vid_1234&pid_bead#...`, converted to ANSI): `h = ((h << 8) | byte) ^ T[h >> 24]` over the bytes, `T` =
  the MSB-first CRC table of polynomial `0x23788D5E` with every entry masked to 31 bits, printed `%X` (no zero padding).
  That is why the id changes with the USB port (FORUM, TF developer). JoyPro computes the same id from the path
  (`engine games devices` shows the path), matches a file name to a connected device by name and id, else by name alone when
  only one connected device has that name (the device on another port), and an export writes the current id (a line
  with an older id of the device is renamed, with a warning).
- **Original game / TF 4.x:** `Joystick` (first game controller), `Joystick1`, `Joystick2` ... in DirectInput order
  (FILE). `engine il2cod` writes this form.
- Several devices at once: up to 16 since Blitz 5.026, 8 before (DOC) — not a one-device game (`MaxJoysticks` 16 / 8).

## Axis settings (`[Config pilotMove]`, model `il2cod`)

Per **command**, not per device axis (FILE): `aileron=0.15 0.12 0.02`, `rudder=1 0.25 0`, `throttle=0 1 0 0 0`,
`pitch=0 0`. Three values on aileron / elevator / rudder: sensitivity (0 linear .. 1 most curved), dead zone at the
centre, dead zone at the ends; two values on the other axes: dead zone at the low and the high end; five on the
throttles (meaning unknown; JoyPro keeps them as imported) (FORUM explanation of the sliders, INF for the order). Import
stores them as `game:IL2CoDBlitz` / `game:IL2CoD` tuning; export writes the line of each exported axis command in the
line's own format, keeps the game's digits when the value did not change (`0.997872340425532`), and in merge mode keeps
values set in the game when JoyPro's are all zero. The base fit maps dead zone → centre (sliders: low end), 1 − saturation
X → end, a positive curvature → sensitivity. The preview's formula (linear-to-cubic blend) is an assumption.

## Catalog

`engine il2codblitz|il2cod refresh-db [<confUser.ini or folder>]`: JoyPro's own command list (the ids the files use,
JoyPro's titles; `pilotMove.aileron`, `pilot.trigger0` ...) plus every command of the user's file and the install's
default `confUser.ini`. Mission builder sections are not taken.

## Export rules

Flight game (also vehicles, but JoyPro manages the listed sections only): clean takes over the devices it writes,
nuke clears the other connected devices; keyboard only with `--include-keyboard`; merge keeps the game's bind on an
input, merge-overwrite replaces it, add never removes. Backup before the first write; JoyPro's inputs are remembered in
`confUser.ini.joypro.json`.

## Live checks still needed

1. Blitz's controls page: a vJoy button, chord, hat and axis bound in the game; the written names should be
   `vJoy_Device-14B51D1+...` on a test PC (the hash of vJoy's path `\\?\hid#hidclass#1&2d595ca7&1&0000#{...}`) — run
   `tools/e2e/games/IL2CoDBlitz.ps1` (calibrate its Options > Controls steps first).
2. A second hat (`+Pov1`?) and the throttle's five values.
3. Whether Blitz still reads the old `Joystick`/`Joystick<n>` names its own default `confUser.ini` ships with
   (JoyPro reads them by DirectInput order for both games).
4. The original game (63950) does not start on a test PC (see the top of this page).

## Other findings

- Buttons on axis commands: `Joystick2+Key4=1underc` in `[HotKey pilotMove]` (FILE: the user's own 2015 file);
  JoyPro reads and writes them as button binds of the axis command (no `[Config pilotMove]` line for them).
- An export renames a line that names an older Blitz id of a connected device (same product name, other USB port) to
  the current id, with a warning.
