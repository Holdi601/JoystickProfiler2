# Axis curve manager

Every axis relation has one set of axis values that all games get, the **base**: invert, slider, dead zone,
saturation X/Y and curvature (one value) or an 11-point user curve. These are DCS's filter parameters and what JoyPro
has always stored on the bind. On top of the base a relation can hold **tuning per scope**:

| Scope | Example | Used for |
|---|---|---|
| `plane:<game>:<plane>` | `plane:DCS:F-16C_50` | one aircraft of one game |
| `game:<game>` | `game:IL2Korea` | every aircraft of a game |
| `family:<family>` | `family:il2` | games with the same response model and files (il2 = Great Battles + Korea, vtolvr, arma2 = Arma 2 + OA, xplane = X-Plane 11 + 12, msfs = MSFS 2020 + 2024) |
| `base` | | every game without its own tuning |

A game looks for the most specific scope: plane, then game, then family, then base. When it falls back to the base and
its response model has parameters of its own (IL-2 Korea's exponent), JoyPro **fits** them to the base curve: the
model's parameters that reproduce the base curve best (least squares on 20 points of the stick travel). So one curve
set once is carried to every game as closely as each game can express it, and any game or aircraft can still get its
own values.

Storage: `Bind.TuningOverrides` (`[OptionalField] Dictionary<string, string>`), scope -> `AxisTuning.Format()`
(`inv=0;sl=0;dz=0.05;sx=1;sy=1;cv=0.2;n.sensitivity=22`). Framework types only, so JoyPro 1 still opens the profile
(binary formatter version tolerance skips the unknown member).

## Response models (`engine/AxisTuning.cs`)
The file is plain math with no engine dependencies; the GUI links it for the live preview.

- **DCS** (`dcs`), the formula of DCS's axis-tune widget (`bin\lua-gui_axes_tune.dll`; the widget only returns 0
  without a GUI, so JoyPro computes the curve itself):
  - centred axis: u = |x|; u <= dead zone -> 0; u >= saturation X -> saturation Y; else t = (u - dz) / (sx - dz),
    curvature k != 0: t = expm1(10 k t) / expm1(10 k); output = sy * t with the sign of x (invert flips it);
  - user curve (11 values): natural cubic spline through the points at 0, 0.1, .. 1 instead of the curvature
    (output not scaled by saturation Y);
  - slider: u = (x + 1) / 2, output = 2 f(u) - 1.
- **IL-2 Series: Korea** (`il2korea`), fitted to the tables the game compiles (see `docs/games/il2-korea.md`): dead
  zones, quadratic Bezier exponent, centre offset; matches the game to 1e-6 except the flat side of a centre offset
  (interpolated from measurements, < 0.005).
- **IL-2 Great Battles** (`il2`), fitted to 12 tables the game compiled into `current.map`
  (`tools\e2e\research\Il2GbCurves.ps1`): Korea's curve with exponent = 200 x `sensitivityBallance`; the dead
  zones (`centerDeadZone`, `sideDeadZone`, fractions of the half travel) snap to a 0.02 grid, towards the centre on the
  positive side and away from it on the negative side; error below 1e-4 (0.001 with all three set). The game shows
  0.5 balance and 0.25 dead zone as 100 %. Its "fit" of the base is what JoyPro has always written for centred axes:
  balance = curvature / 2, centre dead zone = dead zone / 4. Korea and Great Battles read each other's keys
  (`family:il2` may hold either; Korea's offset = 100 x `pitchShift`, its one-sided Low/High = 100 x low/high).
  - Pitch `pitchShift` (`global.pitch`): the output at the stick centre, Korea's curve with offset. Matches the game to
    1e-5 with a shift and a balance; with a shift and no balance, or with dead zones on the long side, the game's table
    deviates up to 0.008 / 0.019 (its points snap to round tenths; not modelled).
  - One-sided axes (Slider; `highValuesDeadZone`, `lowValuesDeadZone`, shares of the whole travel): linear over the
    whole travel between the two dead zones, no curve, no grid (error 1e-5). The base's slider becomes low = dead zone,
    high = 1 - saturation X.
- **War Thunder** (`warthunder`, `engine\AxisModels\WarThunder.cs`): the Axis setup dialog's own preview formula
  (`axiscontrols.nut`): `v = x*kMul + kAdd` (after invert), sign taken before clamping to -1..1, dead zone
  `(|v| - innerDeadzone) / (1 - innerDeadzone)`, `y = sign * |v|^(1 + nonlinearity)`; then spread over
  rangeMin..rangeMax (inferred; a one-sided axis shows its 0..1 as -1..1). Also relative control (sensitivity, step) and
  keep value, which do not change the curve. Fit of the base: multiplier = 1 / saturation X, dead zone x multiplier
  (exact for the linear part), nonlinearity 0..3 by least squares; sliders get dead zone 0 and multiplier/correction
  that map the base's travel onto the whole range. Unverified in game (no machine.blk on this PC yet).
- **Microsoft Flight Simulator 2020/2024** (`msfs`, `engine\AxisModels\Msfs.cs`): the device's Sensitivity page
  (sensitivity -/+, neutral, dead zone, extremity dead zone, reactivity; one set per physical axis) and per action no
  sensitivity curve (flag 1024) and axis override (flag 4096 + `<Axis/>`), in the file's units. The curve was measured in
  MSFS 2020 (2026-10-10, exact to 3e-4 on 84 points): the axis is evaluated mirrored (Sensitivity - on the positive stick
  half, zero point at -neutral), dead zone in stick units from that point, then v(1+s)/(1-s+2sv) with s = sensitivity/100
  (at most 0.99), the extremity dead zone caps the output at 1 - e; flag 1024 keeps the dead zone (not rescaled). Fit from
  the base: dead zone, extremity = 1 - saturation Y, one sensitivity for both halves (least squares); a slider base gives
  a linear page. Details: `docs/games/msfs.md`. `AxisModel.SharedPerAxisKeys`
  marks the per-device-axis keys: `binds tuning set` warns when another relation on the same device axis differs.
- **Falcon BMS** (`bms`, `engine\AxisModels\FalconBMS.cs`): the launcher's dead zone and saturation steps (0-3) per
  physical axis, invert, one-sided axes without dead zone, and User.cfg's `g_nAxisExp_AXIS_*` / `g_nAxisSat_AXIS_*` for
  pitch, roll, yaw and cursor X/Y. Preview from the documented ints (DirectInput dead zone/saturation of the half
  travel) and the BMS forum's description of the curve (linear-to-cubic blend, less travel to full output): UNVERIFIED
  in game (`tools\e2e\games\BmsSettings.ps1` prepared). The fit from the base takes the nearest steps only, no exponent.
  Game settings: `engine\FfbModels\FalconBMS.cs` (see `docs/games/falcon-bms.md`).
- **Star Citizen** (`starcitizen`, `engine\AxisModels\StarCitizen.cs`): dead zone and saturation per device axis
  (`deviceoptions`), then per joystick slot and option group invert, exponent (1..3) or a 9-point curve (10..90 %,
  ends fixed), sensitivity; linear between dead zone and saturation, curve points joined by straight lines. Documented
  behaviour, not measured (unverified). Fit from the base: best exponent, else the curve; sliders fit to neutral. See
  `docs/games/starcitizen.md`.
- **Elite Dangerous** (`elite`, `engine\AxisModels\EliteDangerous.cs`): per axis element invert and dead zone (0..1,
  file units; `platformDeadzone` = the game's `DefaultPlatformDeadzone` symbol); Slider on the throttle = Throttle Axis
  Range forward only. Invert is written XOR the element's stock direction (docs/games/elite-dangerous.md).
- **X-Plane 11/12** (`xplane`, `engine\AxisModels\XPlane.cs`): X-Plane's per-axis response curve, stops (x, y) in 0..1
  with LINEAR (exact), CATMULL_ROM or CUBIC_AVG interpolation (all three measured in X-Plane 12.4), x = deflection magnitude on
  centred axes and travel on one-sided ones. The stops are explicit control points (`AxisTuning.ControlPoints`,
  Native `cpN`/`cpx<i>`/`cpy<i>`; imported curves, `--curve-points`) or come from dead zone (flat first stop),
  saturation, curvature or the 11-point user curve (11 equally spaced stops). The fit of the base samples the DCS
  curve into LINEAR stops (adaptive, error below 0.001). Also `interp`, `rangeType`/`rangePt0..2` (throttle/prop/mixture
  fixed ranges). Details: `docs/games/xplane.md`.
- **Arma 3** (`arma3`, `engine\AxisModels\Arma3.cs`), read from arma3_x64.exe (see `docs/games/arma3.md`): per device
  and physical axis, DirectInput dead zone `deadZone` (0..0.1), then per half s = `sensitivityPos` / `sensitivityNeg`:
  s >= 0: s v^(1/3) + (1 - s) v, s < 0: |s| v^3 + (1 - |s|) v, times `steepness` (1.0/1.2/1.5/1.9/2.4/3.0), clamped
  to 1. No fit: the base is not written to Arma 3.
- **Arma 2 / OA** (`arma2`, `engine\AxisModels\Arma2.cs`), from ArmA2OA.exe: v^(10^-p) per half after the fixed 7 %
  dead zone, p = `sensitivity` (the Customize slider as shown in game, file value 10^p, both halves). No fit.
- **Arma Reforger** (`reforger`, `engine\AxisModels\ArmaReforger.cs`): the per-bind `InputFilterValue` `multiplier`
  (and `useInvert`); preview x * multiplier (not verified in the game).
- **WARDOGS** (`wardogs`, `engine\AxisModels\Wardogs.cs`): per axis Sensitivity (0.1..2, 1 = unchanged) and DeadZone
  (0..0.5) in the HOTAS page's units, invert. Formula **unverified** (packed code, no preview in the game): invert,
  rescaled dead zone, times sensitivity, clipped at 1. Fit: base dead zone (not for sliders), sensitivity = slope from
  saturation; no curvature. See `docs/games/wardogs.md`.
- **VTOL VR BYOJoystick** (`byoj`, `engine\AxisModels\VtolVr.cs`): invert and the mod's one `Deadzone` number, which is
  the multi-crew takeover threshold on stick/throttles (`takeover`, no effect on the response) and a cut without rescale
  on SOI slew / Articulate Tilt (`cut`); linear otherwise. **VTOL VR native** (`vtolnative`): invert and the toe brakes'
  Rewired `axisRange`. See `docs/games/vtol-vr.md`.
- **Generic** (`generic`): invert and a linear dead zone, for games whose response JoyPro does not model yet.

Arma's curves are per device axis, not per action: every relation exported to the same physical axis must agree; the
export writes the first relation's values (by name) and warns about the others.

Split games (2026-10-11): MSFS and X-Plane are one game per version now (`MSFS2020`/`MSFS2024`, `XPlane11`/`XPlane12`). A
tuning stored for the single game (`game:MSFS`, `game:XPlane`) is moved to the new family (`family:msfs`, `family:xplane`)
when the profile is loaded, so both versions keep using it; `plane:MSFS:<p>` goes to each version's plane scope
(`engine\Games\LegacyGameIds.cs`).

Split family `arma`: until 2026-10-05 `family:arma` held Arma 3, Arma 2 and OA together (generic model: only invert
reached the files). Now Arma 3 has no family and Arma 2 / OA have `family:arma2`; a stored `family:arma` tuning is not
migrated but still read as the last scope for all three games (after the game and the new family), so its invert keeps
working; it has none of the new parameters, so it writes no curve. The GUI no longer offers it.

## Games
- DCS: `plane:DCS:<plane>` -> `game:DCS` -> base, written per aircraft as the diff.lua `filter`. The import keeps a
  second aircraft's different filter as its plane tuning (it used to be dropped).
- IL-2 Korea: `general.pitch` (pitch) and `shared.responses` (other axes); imported as `game:IL2Korea` tuning.
- IL-2 Great Battles: `current.responses` (one row per action, all aircraft), from `game:IL2Game` -> `family:il2` ->
  base. The file has sections `type4Devices`, `type2Devices` (one-sided: actionId, highValuesDeadZone,
  lowValuesDeadZone) and `type1Devices` (centred: actionId, centerDeadZone, sensitivityBallance, sideDeadZone); the
  pitch file `global.pitch` has `type3Devices` (actionId, centerDeadZone, pitchShift, sensitivityBallance,
  sideDeadZone). Survey (`tools\e2e\research\Il2GbAxesSurvey.ps1`, every curve action of `[axiscurves]` in each section,
  compiled by the game): the game applies a row to its action whatever the section (the sections only tell its editor
  which form to show); type2 and type4 compile the same; a type2/type4 row wins over a type1 row; a `global.pitch` row
  wins over `current.responses`; a type3 row in `current.responses` does nothing. JoyPro writes a slider as a type2 row
  (type4 when the action already has one), pitch with a shift to `global.pitch` (only created then), everything else as
  type1, and each action in one section only: the game's own `type2` row `rpc_all_engines_throttles,0,0` used to stay
  in the file and override any throttle setting JoyPro wrote in type1; it is now left out when JoyPro writes the
  throttle (old-vs-new: that line is the only difference, same compiled result). Import reads all sections; a
  type2/type4 row is a slider. The import keeps a row in the base when the base reproduces it (as before) and as `game:IL2Game` tuning when
  it does not (both dead zones set, values beyond the base's range); re-importing a reproducible row drops that tuning.
  Settings > Input devices (UI survey 2026-10-05: Control Mode, Tank Turret Control, mouse Sensitivity/Acceleration/
  Tank aim amplifier/Invert Y, Joystick Noise filter 0..1, Force Feedback Shaking/Feedback power in %, Enable) is
  saved on ACCEPT to `data\input\current.devices`, one URL query line (`ffEnabled=1&ffShaking=1&mouseAcceleration=0&
  ffForce=1&mouseSensitivity=4&mouseInvertYAxis=0&joystickFilter=0.13`), not to `startup.cfg` (unchanged by ACCEPT);
  `current.map` then compiles `use joy(1000.00,0.13);`. `engine ffb set --device * --game IL2Game` (enabled, force,
  amplitude = Shaking, joystickFilter; 0..1) edits that line in place and still writes `startup.cfg` [force_feedback];
  it does not create `current.devices` (the mouse keys have no known defaults) and warns instead. The game also
  rewrites `global.actions` on that ACCEPT. Checked in the game (2026-10-05): with `current.devices` from JoyPro
  (Shaking 0.6, power 0.7, filter 0.25) and `startup.cfg` at 1.0, the recompiled `current.map` had `use joy(1000.00,0.25)`
  and `[force_feedback]` force 0.7 / amplitude 0.6, and the page showed 0.25 / 60 % / 70 %: the game reads
  `current.devices`. An export without IL-2 binds still writes these settings.
- War Thunder: `game:WarThunder` -> base (fitted), written to machine.blk `axes{}` as the game's diff against its base
  preset (`docs/games/war-thunder.md`); import fills every attribute with the value the game uses.
- MSFS 2020 / 2024: `game:MSFS2020` / `game:MSFS2024` -> `family:msfs` -> base (fitted), written to the device's Sensitivity page of the bound axis or, with `axisOverride`, to the action's own `<Axis/>` (`docs/games/msfs.md`).
- Star Citizen: `game:StarCitizen` -> base, written to `actionmaps.xml` (option group of the action on its slot,
  `deviceoptions` of the physical axis) for the axis rebinds JoyPro writes; a relation without own settings leaves the
  game's values. Import stores the file's values as `game:StarCitizen` tuning. Several relations on one group or axis:
  the first by name wins (warning). Game-wide settings (attributes.xml): `engine ffb set --device * --game
  StarCitizen`. Details: `docs/games/starcitizen.md`.
- Elite Dangerous: `game:EliteDangerous` -> base; `<Inverted>`/`<Deadzone>` per axis element, `<ThrottleRange>` from the
  throttle's Slider, game-wide options with `engine ffb set --device * --game EliteDangerous`.
- X4: no per-axis values in the game; the relations' invert (XOR X4's HOTAS default) and the throttle's Slider become
  the game-wide `invert_*` / `bidirectionalthrottle` params, dead zone and the rest via `engine ffb set --device * --game X4`
  (docs/games/x4.md "Axis settings").
- X-Plane: `axis_response_curve<axis>` lines of the exported Joysticks.prf, imported as `game:XPlane12` / `game:XPlane11` tuning (`family:xplane` for both);
  control sensitivity, X-Plane 11 stability augmentation, flap detents and X-Plane 12's control-loaded flag are game settings
  (`engine ffb set --game XPlane12|XPlane11`). Null zone and centre are calibration and never written.
- Arma 3 / Arma 2 / OA: `JoysticksList` arrays of the profile (per device axis), imported as `game:<game>` tuning;
  device and game settings (mode, analog click, Arma 3's RTD options) through `engine ffb set --game Arma3|Arma2|Arma2OA`.
  Reforger: the axis filter in JoyPro's preset; order rules and game settings in the game's settings files
  (`docs/games/arma-reforger.md`). An export without binds of these games still writes their stored settings.
- `binds tuning set --param` looks the key up in the scope's model first (several models have a `sensitivity` with
  different ranges) and also takes a model's own switches (`useInvert=1`).
- Registry games: the bridge passes the resolved tuning (`GameInputBinding.Tuning`); imported axis values (dead zones
- WARDOGS: `game:Wardogs` (no family), `Sensitivity`/`DeadZone` of each axis line of `GameUserSettings.ini`; game-wide
  Enable HOTAS / Self-Centering Collective via `engine ffb set --device * --game Wardogs` (no force feedback).
- VTOL VR: `game:VTOLVR` (BYOJ) / `game:VTOLVRNative` -> `family:vtolvr`; Settings > Controls (`gameSettings.cfg`,
  rudder options, VR dead zones and haptics) via `engine ffb set --device * --game VTOLVR`, written by the native export.
- An export of a profile without binds for a game with game-wide settings (`IGameWideSettings`) writes only those
  settings and leaves the binds in the game file alone.- Registry games: the bridge passes the resolved tuning (`GameInputBinding.Tuning`); imported axis values (dead zones
  etc.) become that game's tuning instead of the base, so one game's settings no longer leak into DCS.

## Detent mode (one throttle for all aircraft)
A throttle with an afterburner detent used to need one relation per afterburner start (each with its own curve). In
detent mode one throttle relation serves every aircraft: per aircraft JoyPro bends the relation's curve so that the
device's physical detent is exactly where that aircraft's afterburner starts (`engine\AxisDetents.cs`, math only, linked
into the GUI; `engine\EngineAxisDetents*.cs`).

- **Gate data**: `DB\Detents\throttle-gates.json` (shipped, JoyPro's own): per game and aircraft (DCS: the Config\Input
  folder name, aliases for _WSO/_RIO seats, `_easy` falls back to the aircraft; `*` = every aircraft of a game) the
  afterburner start on the game's throttle axis (0 idle .. 1 full), an idle cut-off region on the axis (`idleGate`),
  `noAfterburner`, source and confidence. Read from the game files (static analysis, 2026-10-17): F-14 / F-4E 0.70,
  F-15E 0.75 (cut-off 0.10), M-2000C 0.90 (cut-off 0.15) and the Mirage F1 0.59 are the defaults of the modules' own
  "afterburner detent in axis percent" options (high); F-16C 0.775, F/A-18C 0.77, MiG-29A (full fidelity) 0.675,
  F-5E 0.80 and JF-17 0.833 are the MIL marks of their Controls Indicator scripts (medium: assumes the lever follows the
  axis 1:1; if DCS stretches the axis over idle..max instead, 0.75 / 0.744 / 0.612, see the notes). FC3 jets, AJS 37
  and MiG-21 have no number in their files (their old curves in the profile give one). MiG-19P / F-100D switch the
  afterburner by a button. IL-2 (both) has no afterburner. Falcon BMS calibrates its own AB and idle detents in its
  setup (stored in `joystick.cal`), X-Plane per aircraft in the .acf (`acf/_go_to_burner_50_above_thro`); MSFS's
  aircraft files are not read.
  DCS also has hidden filter fields `hardwareDetent`, `hardwareDetentAB`, `hardwareDetentMax` (`Scripts\Input\
  AxesTuneDialog.lua`, hidden in public builds); JoyPro does not use them.
- **Calibration** per device axis (`Pr0file.AxisCalibrations`, `"<device>|<axis>"` -> `idle=0.02;det=0.79;full=..;cut=..;
  src=wizard;at=<date>`, raw axis positions 0..1 as DirectInput reports them): idle (on the idle detent if the device
  has one), the afterburner detent, optionally full and the cut-off behind the idle detent. The direction (value rising
  or falling towards full) comes from idle vs. detent. The Binds page's wizard reads the axis live (capture service);
  `binds detent calibrate` stores it; every throttle relation on that axis uses it.
- **Settings** of a relation: `Bind.TuningOverrides["detent"]` (`on=1;noab=full;unknown=linear;margin=0.01;
  t.<game>:<plane>=ab:0.78|ab:0.78/idle:0.1|full|linear`), so copies, re-import metadata and the history carry it like the
  tuning scopes (`binds tuning show` does not list it as a scope). Per aircraft: its own value (`t.`), else the gate data,
  else `unknown` (default: the relation's curve). Aircraft without afterburner get full power at the detent (`noab=full`)
  or the relation's curve (`linear`).
- **The curve**: the base curve gives the game throttle g(u) along the raw travel; a piecewise linear bend W maps
  g(detent) to the afterburner start minus the margin (resting in the detent stays just below the afterburner, 1 % by
  default), g(full) to 1, and with a calibrated cut-off g(idle) to the aircraft's idle gate (else flat zero in the
  cut-off). The relation's own curve shape is kept inside each segment. Game conventions (DCS thrust: -1 = full) and the
  relation's invert need not be known: the base's output at idle and full tells which way the game throttle rises.
- **Order**: an aircraft's or game's own tuning scope wins, then detent mode, then the base (`BindTuning.Resolve`).
- **Per game** (`DetentMath.Fit`, max. difference to the wanted curve in game throttle reported): DCS (per aircraft)
  dead zone + saturation when the curve is one straight line (full at the detent: exact), else the 11-point user curve
  (least squares, exact at the detent and on flat stretches; typically below 1.5 %). X-Plane: curve points (exact).
  Any other model: its own parameters searched on their grid (IL-2 full at the detent = a high dead zone: exact; an
  afterburner bend cannot be expressed there). Games with one curve for all aircraft get what most of the relation's
  aircraft of that game need (others listed). Above 2 % difference a warning, above 6 % the game keeps its normal curve
  (warning in `binds detent show`, the GUI and the export output).
- **Merge** (`binds detent merge`): finds per device axis the throttle relations (name or DCS thrust axes 2004-2006),
  reads the device's detent from a relation that gives full power there and stays full (else from the gate data), keeps
  the relation with most aircraft that does not end at the detent (its bind and curve), adds the others' aircraft and
  removes them. Per aircraft its old throttle at the detent (+ margin) becomes its own afterburner start
  (`--ab-source game`: the gate data where it has one); a full-at-detent aircraft with gate data gets the gate data;
  aircraft without afterburner that flew past the detent keep the relation's curve. The merge is one history version of
  its own (`profile history undo`; the GUI's "Undo merge" restores the version it was made on).
- **GUI**: Binds page, axis relation, "Detent mode": on/off, no-afterburner rule, "Calibrate detent..." (idle, detent,
  full and cut-off with Confirm/Skip/Back on the live axis), "Merge my throttle relations..." (plan, Merge, Undo merge),
  and the aircraft groups ("F-16C, F/A-18C: afterburner at 77 %") with a travel/throttle preview (detent line, afterburner
  band), the source, per game the method and error, and the afterburner start editable per group.
- Verified: unit tests `gui\JoyPro.Gui.Tests\DetentModeTests.cs` (math per model, panel, wizard, merge), smoke
  `engine\tests\smoke\detent.ps1` (gate data, merge/undo/redo, calibration checks, own values, DCS/X-Plane/IL-2 fits,
  DCS export and DCS's own Lua loading it), the user's March 2025 profile merged on a copy and exported into a copy of
  Saved Games\DCS (every written file loads in DCS's luae; per aircraft the throttle at the detent equals the old
  curve's), journey scenario `detent` (`UserJourneyTests`). Not flown in game yet.

## Adding a game's model
- Axis model: `engine\AxisModels\<Game>.cs`, a static method marked `[GameAxisModel("<game id>")]` that returns the
  `AxisModel` (C# 7.3, math only; start the file with `#if NET` / `#nullable disable` / `#endif`). The engine compiles
  the folder by wildcard and the GUI links it for the preview.
- Game settings (force feedback and other game-wide control options, device `*`): `engine\FfbModels\<Game>.cs`, a
  static method marked `[GameFfbModel]` that returns the `FfbModel` (engine only).
- Smoke tests: `engine\tests\smoke\<game>.ps1`, dot-sourced by `engine_smoke_test.ps1` (Run-Engine, Assert-*,
  `$tempDir`, `$engineDir`).
- What each game offers and what JoyPro lacks: the author's private per-game survey notes (research 2026-10-05).

## CLI
`engine binds tuning models | show | set | clear | preview` and `engine binds detent show | set | calibrate |
uncalibrate | gates | merge` (see the CLI wiki).

## Verification
- Smoke tests: scope storage, Korea import/export of the response files, inheritance fit, merge keeps a response
  changed in the game, DCS per-aircraft filters, IL-2 GB import/export of `current.responses` and family conversion.
- Old-vs-new: the IL-2 GB import and all export modes write the same files as before the `il2` model.
- `engine\tests\smoke\arma.ps1`: Arma 3 arrays/settings/full-axis throttle, conflict warning, import round trip,
  settings-only export; Arma 2 OA sensitivity, family split, device switch; Reforger filter, order rules, game settings
  and the refusals. In-game checks prepared (not run): `tools\e2e\games\Arma3Settings.ps1`, `Arma2Settings.ps1`.
- WARDOGS / VTOL VR: `engine\tests\smoke\wardogs.ps1`, `vtolvr.ps1` (import round trips, own values, fit, game-wide settings, settings-only export). In game: `tools\e2e\games\WardogsSettings.ps1` (prepared, not run yet).
- In game: `tools\e2e\games\IL2KoreaCurves.ps1` exports random settings, starts Korea and compares the table it
  compiled with JoyPro's preview on all 101 points (max difference 4e-6 on the first run).
- In game: `tools\e2e\games\IL2GameCurves.ps1` does the same for Great Battles (roll in GB units, yaw as
  `family:il2` in Korea units, a one-sided throttle as a type2 row, pitch with a shift in `global.pitch`): 11/11, max
  difference 0.0004 (2026-10-05).
- In game: `tools\e2e\games\DCSCurves.ps1` (isolated `-w JoyProE2E`) exports random Su-25T plane tunings: pitch
  (dead zone, saturation X/Y, curvature), roll (11-point user curve, inverted), thrust (slider). DCS's Axis Tune panel
  shows the same numbers and Slider/Invert/User Curve boxes, and its live In/Out readout follows JoyPro's preview at
  10 vJoy positions per axis (first run 21/21). The readout is whole percent cut off rather than rounded, a slider
  shows 0..100, and Out is the response before Invert.
- X-Plane: `engine\tests\smoke\xplane.ps1` (import of every mode and a fixed range, byte-identical round trip, export
  on the right index, fit of the base, merge keeps a curve changed in the game, calibration unchanged, control
  settings). In game (prepared, not run yet): `tools\e2e\games\XPlaneCurves.ps1` compares X-Plane's processed axis
  values (Web API `sim/joystick/joy_mapped_axis_value`) with JoyPro's preview during a vJoy sweep.
- Calibration scripts: `tools\e2e\research\KoreaCurves.ps1`, `Il2GbCurves.ps1`.
- MSFS: smoke `engine\tests\smoke\msfs.ps1` (import/export of the Sensitivity page, overrides, flags, conflicts,
  merge). In game (prepared, not run yet): `tools\e2e\games\MsfsCurves.ps1` sweeps vJoy and reads ELEVATOR / AILERON /
  RUDDER POSITION and GENERAL ENG THROTTLE LEVER POSITION through `tools\e2e\MsfsSimVars` (read-only SimConnect with
  the sim's own `SimConnect.dll`), compares with the preview and saves `measurements.csv` for fitting.
