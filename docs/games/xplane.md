# X-Plane 11 / 12 (experimental; X-Plane 12.4.3 checked in game, X-Plane 11 from its installed files)

JoyPro handles X-Plane 11 and X-Plane 12 as two games side by side (game ids `XPlane11` / `XPlane12`, CLI
`engine xplane11` / `engine xplane12`; the old `engine xplane` is X-Plane 12): each has its own installation,
`Output\preferences` folder, catalog and settings model, and both can be installed at the same time. See "Two
versions" below.

## Files
- Install roots: X-Plane's installer writes every install folder of a version to `%LOCALAPPDATA%\x-plane_install_<version>.txt`
  (one path per line, slashes mixed: `D:\SteamLibrary/steamapps/common/X-Plane 12/` [OBS, X-Plane 12.4.3 on Steam]).
  A version installed by Steam and never started has no such file [OBS: X-Plane 11 on a test PC]; its Steam folder
  (`steamapps\common\X-Plane 11` / `X-Plane 12`, app 269950 / 2014780) is the install then.
- Preferences inside the install: `Output\preferences\` (created on the first start):
  - `X-Plane Joystick Settings.prf`, `X-Plane Keys.prf`: current/global.
  - `control profiles\<Name> - Joysticks.prf`, `<Name> - Keys.prf` (XP 11.10+, XP12). Profiles cover all devices (not one file per device). The global file seems to mirror the active profile [INF].
  - Not user preferences: `Resources\joystick configs\<VID>-<PID> <OS> <name>.joy` (679 files in X-Plane 12) are
    Laminar's default device configurations (one per known device, with pictures), which X-Plane applies to a new
    device. X-Plane 12.4.3 keeps the user's assignments of all devices in the single `X-Plane Joystick Settings.prf`
    (8562 lines on a test PC, 6 device slots) [OBS], the same file and keys as X-Plane 11 [SRC: the strings
    `Joystick Settings.prf`, `- Joysticks.prf`, `_joy_AXIS_use`, `_joy_BUTN_use`, `_joy_unique_id`, `_joy_damp`,
    `axis_response_curve` in both X-Plane.exe].
  - `Miscellaneous.prf`: aircraft → profile (`P acf_profile Boeing___737-800X B738`, spaces become `___`).
- Rewritten when the settings window closes and on exit: edit only while X-Plane is closed.

## Joysticks.prf (line based)
```
I
10 Version
_joy_unique_id2 VID:1103PID:45322
_joy_location1 \\?\hid#vid_045e&pid_028e&ig_00#...
_joy_AXIS_use50 2
_joy_rev_axis53 0
_joy_BUTN_use320 sim/operation/pause_toggle
_joy_BUTN_use323 sim/flight_controls/landing_gear_toggle
axis_response_curve52 INTERP_MODE CATMULL_ROM | 0.000000 0.000000  | 0.500000 0.500000  | 1.000000 1.000000 
```
- 20 device slots: `_joy_unique_id<slot> VID:<dec>PID:<dec>` (decimal!), `_joy_location<slot>` (HID path), `_joy_lru_time<slot>`.
- Global indices: axis = slot×25 + a (500 entries), button = slot×160 + b (3200 entries). [INF]
- The first hat becomes 8 virtual buttons after the device's buttons, clockwise from up. [OBS/INF]
- `_joy_AXIS_use<i>` = use code, `_joy_rev_axis<i>` = 0/1, `_joy_BUTN_use<i>` = command path (`sim/none/none` = unassigned), optional `_joy_BUTN_use_desc<i>`.
- Custom-command axes (use 52): `_joy_AXIS_cmnd<axis*4+slot>`.
- Response curves (XP 11.30+, "Add/Edit Response Curve" per axis): general curves with any number of stops, not only
  dead zones. Format (X-Plane 12 exe format strings):
  `axis_response_curve<axis> [FIXED_RANGE_TYPE <type> FIXED_PT_0 <int> FIXED_PT_1 <int> FIXED_PT_2 <int> ; ]INTERP_MODE <mode> | x y  | x y  ...`
  with `%f` numbers, two spaces before each `|` and a trailing space. x, y in 0..1 (x = deflection magnitude on
  centred axes [INF], travel on one-sided axes). Modes `LINEAR`, `CATMULL_ROM`, `CUBIC_AVG`. Fixed ranges (throttle,
  prop and mixture curves): `beta_reverse_range`, `feather_range`, `mixture_cutoff_range`, `helo_thro_detent_range`,
  `fadec_thro_range`, `fadec_thro_with_reverse_range`, `feathershutoff_range` (units of `FIXED_PT_*` UNVERIFIED). The
  game writes the lines at the end of the file. A curve overrides the Control Sensitivity of its axis (manual).
- Control settings per axis **use** code `u`: `_joy_prat<u>` = Control Sensitivity "Control Response" slider 0..1
  (pitch 1, roll 2, yaw 3; XP12 also copilot 68/69/70), `_joy_damp<u>` = Stability Augmentation 0..1 (X-Plane 11
  only), `_axis_is_required<u>` (unknown). Per hardware axis: `_joy_ffb_axis<i>` = control-loaded for trim
  (X-Plane 12, .joy tag `ffb`), `_joy_axis_delta_sec<i>` (unknown). Per file: `_discretize_flap_axis` (flap detents).
- Never write calibration keys (`_joy_center`, `_joy_null`, `_joy_*_raw_dat`, `_joy_axis_is_calibrated_override`,
  `_joy_axis_wants_relaxed_calibration`), `_eq_pfc_*`, or slots JoyPro does not manage. The null zone is calibration.
- Joystick buttons have no modifier/combo concept (only keyboard combos in Keys.prf).

Axis order on Windows may follow HID report order rather than DirectInput X-first [INF/UNVERIFIED]: validate against existing assignments.

## Axis use codes [SRC Joystick Datarefs]
0 none, 1 pitch, 2 roll, 3 yaw, 4 throttle, 5 collective, 6/7 left/right toe brake, 8 prop, 9 mixture, 10 carb heat, 11 flaps, 12 thrust vector, 13 wing sweep, 14 speedbrakes, 15 displacement, 16 reverse, 17/18/19 elevator/aileron/rudder trim, 20–23 throttle 1–4, 24–27 prop 1–4, 28–31 mixture 1–4, 32–35 reverse 1–4, 36 gear, 37 nosewheel tiller, 38 backup throttle, 39 cowl flaps, 41–43 view, 44–46 camera, 47–48 gun/bomb, 49–51 VR, 52 custom command(s), 53–56 throttle 5–8, 57–64 cowl flaps 1–8, 65/66 throttle vertical/horizontal (40 is unused).
X-Plane 12 adds, numbered from the `joy_use_*` string table in X-Plane.exe (it lists 0..66 in exactly the documented
order, then these 14; the per-use `_joy_prat`/`_axis_is_required` arrays in user files have 67 (XP11), 74, 79 and
81 entries, which matches these groups): 67 throttle 9, 68/69/70 copilot pitch/roll/yaw, 71/72 copilot left/right
toe brake, 73 copilot tiller, 74/75 `rudd_lft`/`rudd_rgt`, 76/77 `circ_lr`/`circ_ud`, 78 wheel brakes (both),
79/80 viewpoint left-right/up-down. JoyPro catalogs 67-73 and 78; 74-77 (meaning unverified) and 79/80 (view) are not
cataloged. The numbering is still to be confirmed in the game (`joystick_axis_assignments` after picking "Copilot
pitch" in the UI, see `tools\e2e\games\XPlaneCurves.ps1`).

## Commands (DB)
`<XP>\Resources\plugins\Commands.txt`: `<path><spaces><description>` (~2900 in XP12). Plugin/aircraft commands exist only at runtime (XP 12.1.4+ Web API `GET http://localhost:8086/api/v2/commands`).
Flight prefixes: `sim/flight_controls, engines, autopilot, fuel, electrical, lights, systems, radios, instruments, GPS, FMS, magnetos, starters, ignition, igniters, bleed_air, pressurization, ice, annunciator, transponder, audio_panel, weapons, fadec, oxy`. Not flight: `sim/view, operation, general, map, replay, VR, developer, ground_ops`.

## Keys.prf
```
I
1005 Version
F4 ALT sim/operation/quit
Space SHIFT sim/operation/screenshot
F1 <NONE> sim/engines/throttle_down
```
Modifiers `<NONE>`, SHIFT, CTRL, ALT, joined with `+`.

## Two versions
| | X-Plane 11 (`XPlane11`) | X-Plane 12 (`XPlane12`) |
|---|---|---|
| CLI | `engine xplane11` (`xp11`) | `engine xplane12` (`xp12`; old names `xplane`, `xp`) |
| Install | override, else `x-plane_install_11.txt` + Steam app 269950 | override, else `x-plane_install_12.txt` + Steam app 2014780 |
| Joystick file | `X-Plane Joystick Settings.prf`, same format | same |
| Axis use codes | 0..66 (string table of the X-Plane 11 exe ends with `joy_use_thro_hrz`) | 0..80 (adds throttle 9, copilot axes, wheel brakes, ...) |
| Catalog | `DB\XPlane11\actions.tsv`: axes 1..66 + the flight groups of X-Plane 11's `Commands.txt` (2066 actions, refreshed 2026-10-11 from the installed X-Plane 11, build 6916183) | `DB\XPlane12\actions.tsv` (X-Plane 12.4 `Commands.txt`) |
| Settings (`engine ffb set --game ...`) | control response, stability augmentation (`_joy_damp`), flap detents | control response incl. copilot, flap detents, control-loaded axes (`_joy_ffb_axis`) |
| Function map (Auto Group, from-text) | `DB\Functions\XPlane11.tsv` (the X-Plane 12 rows whose command X-Plane 11 has) | `DB\Functions\XPlane12.tsv` |

Both use the same code (`engine\Games\XPlaneIntegration.cs`, one instance per version) and the axis model `xplane`
(tuning scopes `game:XPlane11`, `game:XPlane12`, and `family:xplane` for both). The process is `X-Plane.exe` for both
versions, so an export is refused while either runs.

Profiles and settings saved before the split (game `XPlane`) are migrated when they are loaded
(`engine\Games\LegacyGameIds.cs`): a node `XPlane:<id>` becomes an `XPlane12` node and, when X-Plane 11's catalog has
the action, an `XPlane11` node (X-Plane 12-only axes such as `axis/copilot_pitch` stay X-Plane 12 only); `game:XPlane`
tuning becomes `family:xplane`; `ffb` settings of `XPlane` go to both versions (each keeps the settings its model
knows). A folder set for "X-Plane" in Settings > Games goes to the version it belongs to (listed in an installer file,
else the folder name), "I do not have X-Plane" hides both. `--node XPlane:<id>` still works and adds a node per
version. The watcher's records (`<data>\Sync\XPlane.json`) are not carried over; the next export records the files
of each version again.

## JoyPro implementation (`engine xplane12 ...`, `engine xplane11 ...`)
- Files: `X-Plane Joystick Settings.prf` and `control profiles\<Name> - Joysticks.prf` of the version's installs (the folder set in Settings > Games, else every install listed in `%LOCALAPPDATA%\x-plane_install_<version>.txt` and the version's Steam folder); `--path` for any other file. Keys.prf is never touched. A version that was never started has no preferences yet: start it once with the devices connected.
- Written: `_joy_AXIS_use<i>`/`_joy_rev_axis<i>` for the flight axis codes above (not view, camera, VR or custom-command axes), their `axis_response_curve<i>`, `_joy_BUTN_use<i>` for cataloged commands, and the control settings below. A changed button loses its stale `_joy_BUTN_use_desc<i>` line; every other line keeps its text.
- Device slots: `_joy_unique_id` VID:PID; when identical devices exist, the DirectInput HID interface path (`DIPROP_GUIDANDPATH`) must equal `_joy_location`. Slots of devices that are not connected are skipped.
- Local axis index = DirectInput `EnumObjects` order, which follows the HID report order like X-Plane [INF]. Buttons: device buttons, then the first hat as 8 buttons clockwise from up (diagonals included); other hats are not mapped.
- No modifiers and no keyboard binds.
- Export modes (slots of devices that are not connected are never touched):
  - `add` writes free inputs only; a profile bind on an input with another use or command is not written and listed under "Binds Not Written, Button Taken In The Game". An axis use the game has on another axis stays there (warning).
  - `merge` replaces the other use or command on the profile's input (the conflict) and keeps everything else, also a moved command's old button. An axis use moves (X-Plane ignores a use two axes share).
  - `merge-overwrite` also releases the other axes and buttons of connected devices that hold a use or command the profile binds (with their curves).
  - `clean` also releases every other flight axis/command on connected devices.
  - `<file>.joypro.json` records the inputs JoyPro wrote or took over on import (`A<index>` axes, `B<index>` buttons) and its curves; it no longer decides which binds an export removes.
- Axis response (axis model `xplane`, `engine\AxisModels\XPlane.cs`; tuning scopes `game:XPlane12` / `game:XPlane11` -> `family:xplane` -> base):
  - Import reads each flight axis's curve into the relation's `game:XPlane12` (or `game:XPlane11`) tuning: the exact stops (`n.cpN`,
    `n.cpx<i>`, `n.cpy<i>`), `interp` (0 LINEAR, 1 CATMULL_ROM, 2 CUBIC_AVG), `rangeType` (1..7 in the order above) and
    `rangePt0..2`, reverse, one-sided (from the use code), and the curve resampled as the 11-point user curve for the
    GUI editor. An imported curve is owned by JoyPro (`curve:<axis>` with a hash of the line in `<file>.joypro.json`).
  - Export writes `axis_response_curve<axis>` for every axis it writes: the explicit stops, else the tuning's dead zone
    (flat first stop), saturation (last stops), curvature or 11-point curve (11 equally spaced stops) with the chosen
    mode. A bind without X-Plane tuning gets its base curve as LINEAR stops (adaptive, within 0.001 of the DCS curve).
    A linear tuning without a fixed range writes no line. An unchanged curve keeps its bytes.
  - A curve made in X-Plane (not imported, or changed in the game since JoyPro wrote it) stays when JoyPro's tuning is
    linear and in merge and add mode (warning); merge-overwrite replaces it (warning); clean replaces or removes it.
    Clean, nuke and merge-overwrite remove the curves of the axes they clear; a curve of an axis that stays assigned stays.
  - Fixed ranges are written only on their kind of axis (beta/helicopter/FADEC: throttles, feather: props, cutoff:
    mixtures); otherwise a warning.
  - `engine binds tuning set ... --scope game:XPlane12 --curve-points 0:0,0.4:0.3,1:1` sets any stops (`none` drops
    them); changing dead zone, saturation or curvature replaces explicit stops (the fields define the curve then).
  - Preview, measured in X-Plane 12.4.3 (`tools\e2e\games\XPlaneCurves.ps1`, sim/joystick/joy_mapped_axis_value over
    40 positions, within 0.0001): LINEAR straight lines; CATMULL_ROM = uniform Catmull-Rom per segment, t linear in x,
    tangent (y[i+1]-y[i-1])/2 in units of t (so the slope jumps at stops with unequal neighbours), end stops repeated;
    CUBIC_AVG = cubic Hermite in x, slope at a stop = mean of the adjacent slopes, slope 0 at the first stop (0 at the
    last stop by symmetry, not measured); output clamped to 0..1. Not modelled: X-Plane's calibration/null zone and
    the Control Sensitivity of pitch/roll/yaw axes without a curve (law UNVERIFIED; it does not act on
    joy_mapped_axis_value: copilot pitch with `_joy_prat68` 0.39 measured exactly linear there, so it applies later).
  - Natural direction (measured, reverse off, raw 0 / 0.5 / 1): X-Plane reverses throttle (4), throttle 1-4 (20-23),
    throttle 8 (56; 53-55 assumed the same) and throttle 9 (67) by itself (1 / 0.56 / 0), like DCS's thrust axes, so
    `_joy_rev_axis` = the relation's invert unchanged. Not reversed: collective 5, prop 8, mixture 9, carb heat 10,
    flaps 11, wing sweep 13, speedbrakes 14, displacement 15, reverse 16/32, gear 36, backup throttle 38, cowl flaps 39,
    throttle vertical/horizontal 65/66, wheel brakes 78. Reverse and the natural flip cancel on a reversed throttle
    (X-Plane = curve(raw)); whether they act before or after the curve cannot be told apart from that.
  - X-Plane ignores a use that two connected axes share ("Multiple axes assigned to yaw", the value stays 0): the
    export warns when another connected device's axis has the use of an exported axis (nuke untouched devices or clean
    clears it).
- Control settings (`engine ffb set --game XPlane12|XPlane11`, one model per version in `engine\FfbModels\XPlane.cs`), written into the exported
  file: device `*`: `pitchResponse`/`rollResponse`/`yawResponse` (`_joy_prat1..3`), `copilot*Response`
  (`_joy_prat68..70`, X-Plane 12), `pitchStability`/`rollStability`/`yawStability` (`_joy_damp1..3`, X-Plane 11), `discretizeFlaps`
  (`_discretize_flap_axis`); per device: `controlLoaded` (X-Plane 12, `_joy_ffb_axis<i>` on the pitch/roll/yaw axes JoyPro
  exports for that device). Each version's model has only its own settings (`ffb set` refuses the others). Only lines the file already has are changed (an XP12 file has no `_joy_damp`, an XP11
  file no copilot or `_joy_ffb_axis` lines): a warning otherwise. Not imported (the file keeps them until set).
  X-Plane has no DirectInput force feedback of its own (FFB only via plugins).
- Catalog: `DB\XPlane12\actions.tsv` / `DB\XPlane11\actions.tsv` = the version's axis table + flight command groups of its `Resources\plugins\Commands.txt` (`engine xplane12 refresh-db` / `engine xplane11 refresh-db`, optionally `--path <X-Plane folder>`; `db check-updates` compares each version's Commands.txt). Plugin and aircraft commands are not cataloged, so JoyPro leaves them alone.
- Before writing: X-Plane must be closed; the file is backed up once to `.jpbak`.

Sources: https://developer.x-plane.com/article/joystick-datarefs/, https://developer.x-plane.com/article/creating-joystick-configuration-joy-files/, X-Plane 11/12 manuals, samples from github.com/daleclack/Xe-Project and github.com/WeisAir/weisair.
