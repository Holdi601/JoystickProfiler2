# FSX, FSX: Steam Edition and Prepar3D v4 / v5 / v6

> **Untested on a real install.** None of these sims is installed on the dev PC and the author has none of them. The
> integration was written blind from public sources (below) and is covered only by a synthetic fixture built from the
> documented format (`engine/tests/fixtures/fsx`, `engine/tests/smoke/fsx.ps1`). After a first export, open the sim's
> Controls page and check the assignments; please report differences.

Evidence tags: **[FILE]** control files posted by users (AVSIM, Steam discussions of FSX-SE, FSDeveloper),
**[TOOL]** a complete `Standard.XML` written by a public tool (ControllerBuddy's FSX configuration script), **[DOC]** the
Prepar3D SDK (SimObject API page, add-on packages, Event IDs) and the FSX/ESP SDK Event IDs, **[INF]** inferred.
Microsoft never documented the file; what is known comes from users (FSDeveloper moderator: "trial-and-error").

CLI: `engine fsx|fsxse|p3d4|p3d5|p3d6 paths|catalog|scan|import|export|refresh-db`; game ids `FSX`, `FSXSE`, `P3D4`,
`P3D5`, `P3D6` (one integration class, one game each because each has its own files).

## Files

| Sim | Control file | Active file | Tag |
|---|---|---|---|
| FSX (boxed) | `%APPDATA%\Microsoft\FSX\Controls\Standard.XML` | `fsx.cfg` `[CONTROLS] Controls_Current=Standard` | FILE |
| FSX: Steam Edition | `%APPDATA%\Microsoft\FSX-SE\Controls\` next to boxed FSX; `%APPDATA%\Microsoft\FSX\Controls\` when installed alone | `fsx_se.cfg` (some guides: `fsx.cfg`) | FILE (folder), INF (cfg name) |
| Prepar3D v4 / v5 / v6 | `%APPDATA%\Lockheed Martin\Prepar3D vN\Controls\Standard.xml` | `Prepar3D.cfg` `[CONTROLS]` | DOC, FILE |

`engine <sim> paths` lists the active file first (`Controls_Current` + `.xml`), then the other `*.xml` of the folder.
Prepar3D's `Standard.xmlx` in the same folder holds device presets (FILE: forum) and FSX's install folder has a template
`Standard.xml`; refresh-db reads both, export never touches them. The sims rewrite the file on exit: export only while
`fsx.exe` / `Prepar3D.exe` is closed (JoyPro refuses while it runs). Header: `<?xml version="1.0"
encoding="Windows-1252"?>`, root `<SimBase.Document Type="Controls" version="1,0" id="Standard">` (TOOL).

JoyPro edits the file **as text**: it replaces, removes or adds whole `<Axis>`, `<Entry>`, `<POV>` blocks (and whole
maps for new devices); every other byte (formatting, comments, unknown elements, other maps) stays.

## Format

```xml
<SimControls.Map>
    <Name>Saitek Pro Flight Rudder Pedals{3B771DE0-2119-11E6-8001-444553540000}</Name>
    <Axis>
        <AxName>RzAxis</AxName>
        <Index>5</Index>
        <AxEvent>AXIS_RUDDER_SET</AxEvent>
        <AxScale>64</AxScale>
        <AxNull>36</AxNull>
    </Axis>
    <Entry>
        <Index>0</Index>
        <Down>BRAKES</Down>
        <DownRpt>1</DownRpt>
    </Entry>
    <POV>
        <Index>0</Index>
        <Down>PAN_VIEW</Down>
        <DownRpt>1</DownRpt>
    </POV>
</SimControls.Map>
```
(shape from FILE/TOOL samples; values here are illustrative)

| Element | Meaning | Tag |
|---|---|---|
| `<Name>` | `<DirectInput instance name>{<instance GUID, upper case>}`; the space before `{` belongs to the name (CH devices) | FILE, TOOL |
| `... SLEW {GUID}` maps | the slew mode's controls: JoyPro leaves them alone | FILE, TOOL |
| `KEYBOARD_MAIN` | keyboard: `<Entry><Key>CTRL+SHIFT+X</Key><Down>EVENT</Down></Entry>` | TOOL |
| `Mouse Yoke`, `Mouse Look`, `<name>.<SimObject>` (P3D) | not managed | TOOL, DOC |
| `<Axis>` | `<Index>` 0..7 = X, Y, Z, Rx, Ry, Rz, Slider, second slider (`AxName` `XAxis` .. `Slider`, `NULL` for 7) | FILE, TOOL |
| `<AxScale>` | sensitivity slider, 0..127; **negative = reversed axis** | FILE, INF |
| `<AxNull>` | null zone slider 0..127; left out at 0 | FILE |
| `<Entry>` | 0-based button `<Index>`, `<Down>`/`<Up>` events, `<DownRpt>1` = repeat while held | FILE, TOOL |
| `<POV>` | a **whole hat**; the sim passes the direction as the event value (normally `PAN_VIEW`) | FILE, FORUM |

Event ids are the "String Name" column of the SDK lists (`GEAR_TOGGLE`, `AXIS_AILERONS_SET`; not always `KEY_` minus the
prefix, e.g. `KNEEBOARD_VIEW`). `*_EX1` events are MSFS-only.

### Devices

Each DirectInput device has its own map, identified by **instance GUID plus name** (FILE). The GUID can change after
re-plugging (FILE, Steam user); JoyPro matches a map by GUID, else by the name when exactly one connected device has
it (with a warning), and writes a new map `<instance name>{<GUID>}` for a device the file does not have. Two identical
devices are told apart by their GUIDs. No device limit is documented; not a one-device game (`MaxJoysticks` 16, a
nominal value).

### JoyPro mapping

- Axes: `JOY_X` .. `JOY_SLIDER2` ↔ `<Index>` 0..7; invert ↔ the sign of `<AxScale>`.
- Buttons: `JOY_BTN<n>` ↔ `<Entry><Index>n-1`. JoyPro adds `<DownRpt>1` for events that repeat (trim, brakes,
  increments, panning), as the default file does.
- Hats: a hat is assigned as a whole; JoyPro shows it on the up direction (`JOY_BTN_POV1_U`) and exports only
  `PAN_VIEW` to a hat (other events on a hat direction are refused with a warning).
- Keys (with `--include-keyboard`): letters, digits, F-keys, `Num_0..9`, `VK_*` names, modifiers `CTRL`, `SHIFT`, `ALT`;
  chords of two plain keys (`SHIFT+Num_6+Num_5`) are not JoyPro inputs and stay.
- No joystick modifiers in this format.

## Axis settings (model `fsx`)

`scale` (0..127) and `nullZone` (0..127) per device axis, as on the Calibration page. Import stores them as
`game:<sim>` tuning; export writes them into the axis block; in merge mode values set in the sim stay when JoyPro's are
the plain default (127 / 0). The base fit maps dead zone → null zone and saturation Y → sensitivity; the curve has no
equivalent. The preview's formula is an assumption (FSDeveloper: hand-edited values above 127 widen the range).

## Catalog

`engine <sim> refresh-db [<file or folder>]`: JoyPro's own event list (`engine/Games/FsxEvents.cs`: about 150 ids with
JoyPro's titles and categories; ids are the public SDK names, no SDK text) plus every event the user's control files,
FSX's install template and Prepar3D's `Standard.xmlx` name. `AXIS_*` events are axes.

## Export rules

Flight-only: clean takes over the devices it writes (all blocks of their main maps), nuke clears the main maps of the
other connected devices (the maps stay, empty); slew, mouse and keyboard maps are untouched (keyboard only with
`--include-keyboard`). Merge keeps the sim's event on an input, merge-overwrite replaces it, add never removes. Backup
before the first write; JoyPro's inputs are remembered in `Standard.XML.joypro.json`.

## Open points

Everything needs a real install. In particular: the P3D v4+ header and whether its `<Name>` still carries the GUID,
the exact bytes the sims write (BOM), `AxName` for the second slider, and whether the sim keeps a new map JoyPro adds
for a device it has not seen yet (it may rebuild it from `Standard.xmlx`).
