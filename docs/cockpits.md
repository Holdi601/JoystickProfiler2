# Cockpit database

JoyPro's reference of real and fictional cockpits, shown like the cockpit chapter of a flight manual: pick an aircraft,
its version (block, tranche, lot, mark, ship) and seat, and read

- the **overview** of the cockpit with its numbered areas (HUD, front dash, consoles, throttles, stick ...);
- a page per **grip** (stick and throttle, cyclic and collective, yoke and quadrant, side-stick, hand controllers): JoyPro's
  drawing with numbered callouts and the **HOTAS table**: a row per control, direction, press (short, long, hold ...) and
  condition (on the ground / in the air), a column per **mode or sensor of interest** (TAD, TGP, HUD ...; NAV, A-A, A-G;
  SCM, NAV, quantum), colour-coded per control;
- a page per **panel** with JoyPro's drawing, numbered callouts and the numbered list (switch positions, what each does);
- where every item comes from (manual and page) and links to real photos.

The **Cockpits** page in the bottom bar shows it; `engine cockpits ...` gives the same data to scripts and to the setup
wizard ("Use this layout").

![F-16C Block 50 stick and throttle](cockpits/f-16-dark.png)

## What is in it

- `EngineOnly_Runtime/DB/Cockpits/*.json`: the aircraft (format `joypro-cockpits`), one file per family of types
  (`jets-us.json`, `jets-europe.json`, `jets-russia.json`, `warbirds.json`, `helicopters.json`, `civil.json`,
  `space-games.json`, `fiction.json`, `spacecraft.json` ...).
- `EngineOnly_Runtime/DB/Cockpits/drawings/*.json`: JoyPro's own schematic vector drawings of the grips (format
  `joypro-cockpit-drawing`), and the generic drawings every aircraft without its own drawing gets.
- `EngineOnly_Runtime/DB/Cockpits/line-drawings/*.json`: JoyPro's own drawings of real cockpits (format
  `joypro-device-drawing`, [device-reference.md](device-reference.md#joypros-drawings)): `<variant>.overview.json`,
  `<variant>.panel.<panel id>.json`, `<variant>.<device>.json` (a grip), and the older `<aircraft>.<device>.json`. Each
  carries the callout positions as `parts` (item number or control id -> [x, y], 0..1 of the drawing). Read one at a time
  when a page needs it.
- Photos are **addresses only** (`photos` of an aircraft), shown after the user's consent, never in the repository or the
  release (see [Photos](#photos)).

It is JoyPro's own work and ships with JoyPro (`DB\Cockpits` is one of the shipped DB folders, `tools/ReleaseContent.psm1`;
pictures inside it are refused).

## Format

```json
{ "format": "joypro-cockpits", "version": 1,
  "aircraft": [ {
    "id": "f-16", "name": "F-16 Fighting Falcon", "aliases": ["Viper"],
    "category": "fixed-wing", "fiction": false, "franchise": "",
    "maker": "General Dynamics / Lockheed Martin", "nations": ["US"], "role": "multirole", "era": "modern",
    "summary": "One or two sentences.",
    "photos": [ { "url": "https://upload.wikimedia.org/...jpg", "page": "https://commons.wikimedia.org/wiki/File:...",
                  "view": "cockpit", "credit": "U.S. Air Force photo", "license": "public-domain",
                  "variant": "f-16c-block-50", "caption": "...", "confidence": "high" } ],
    "links": [ { "title": "...", "url": "https://..." } ],
    "variants": [ {
      "id": "f-16c-block-50", "name": "F-16C Block 50/52", "aliases": ["Block 52"], "years": "1991-",
      "inherits": "", "removeControls": [], "seat": "",
      "games": [ { "game": "DCS", "plane": "F-16C_50" }, { "game": "FalconBMS" } ],
      "drawings": { "stick": "f16-sidestick", "throttle": "f16-throttle" },
      "controls": [ {
        "id": "stick.hat.tms", "device": "stick", "name": "TMS (Target Management Switch)", "type": "hat4",
        "where": "4-way hat at the top right of the grip head, forward thumb position", "part": "",
        "functions": [ { "dir": "up", "text": "Designate / lock target", "fn": "target_management_system_up", "note": "" } ]
      } ],
      "panels": [ { "name": "ICP and DED", "area": "glareshield", "text": "..." } ],
      "cockpit": "A paragraph about the cockpit around the pilot.",
      "notes": "What differs from the other variants, what is uncertain.",
      "sources": [ { "title": "DCS F-16C Early Access Guide", "url": "https://...", "kind": "game-manual", "covers": "HOTAS" } ],
      "confidence": "high"
    } ]
  } ] }
```

### Version 2: the manual pages

Version 2 adds, per variant (all optional for the format; `cockpits check` asks every listed variant for an overview and
numbered panels):

```json
{ "id": "a-10c-ii", "seatOf": "",
  "sources": [ { "id": "m1", "title": "DCS A-10C II Flight Manual (EN)", "kind": "game-manual",
                 "file": "Mods/aircraft/A-10C_2/Doc/DCS A-10C II Flight Manual EN.pdf", "covers": "cockpit, HOTAS, panels" } ],
  "modes": [ { "id": "tad", "name": "TAD", "kind": "soi" }, { "id": "tgp", "name": "TGP", "kind": "soi" }, { "id": "hud", "name": "HUD", "kind": "soi" } ],
  "defaultMode": "hud",
  "controls": [ { "id": "stick.hat.dms", "device": "stick", "name": "Data Management Switch (DMS)", "type": "hat4",
                  "where": "...", "n": 2, "src": "m1", "page": "108",
                  "functions": [
                    { "dir": "fwd", "press": "short", "by": { "tad": "TAD scale decrease", "tgp": "Zoom increase", "hud": "Steerpoint increment" },
                      "fnBy": { "hud": "..." }, "src": "m1", "page": "109" },
                    { "dir": "left", "press": "long", "text": "HMD on/off" } ] } ],
  "overview": { "drawing": "a-10c-ii.overview", "text": "...",
                "areas": [ { "n": 1, "name": "Heads Up Display (HUD)", "area": "glareshield", "x": 0.46, "y": 0.26,
                             "panels": ["hud-area"], "src": "m1", "page": "105" } ] },
  "panels": [ { "id": "left-front-dash", "name": "Left Front Dash", "area": "front-left", "text": "...",
                "drawing": "a-10c-ii.panel.left-front-dash", "src": "m1", "page": "113", "confidence": "high",
                "items": [ { "n": 1, "name": "Left engine fire T-handle", "type": "handle", "positions": ["IN", "PULLED"],
                             "text": "...", "x": 0.76, "y": 0.05, "fn": "" } ] } ] }
```

| Field | |
|---|---|
| `modes` | the HOTAS table's columns: `id`, `name`, `kind` (`soi` sensor of interest, `master` master mode, `mode` flight or game mode, `condition`, `seat`). Without modes the table has one "Function" column |
| `defaultMode` | the column the one-column views, the search and the setup wizard read (default: the first mode) |
| function `press` | `short`, `long`, `press`, `hold`, `double`, `release`, or empty; a row is one direction, press and condition |
| function `when` | a condition the row needs: `ground` / `air` (weight on wheels), a master mode, "gear down" ... |
| function `by`, `fnBy` | the row's cell per mode (text, and the JoyPro function id where it maps); `""` = no function in that mode. With modes, every row has `text` (the same in every mode) or a cell for every mode. Equal neighbouring cells are drawn as one |
| control `n` | its callout number on the grip's page (the manual figure's number); else numbered in list order |
| `overview` | the cockpit's numbered areas (1..N), where each callout points on the overview drawing (`x`, `y` 0..1), the panels in each area; `drawing` names JoyPro's drawing of the cockpit (none: JoyPro's plan) |
| panel `id`, `items` | panels have an id (the overview names them) and numbered items 1..N: `name`, `type` (`switch2`, `switch3`, `knob`, `selector`, `button`, `lever`, `handle`, `wheel`, `guard`, `gauge`, `display`, `light`, `indicator`, `keypad`, `rocker`, `hat`, `slider`, `encoder`, `pedal`, `other`), `positions`, `text`, `x`, `y` (where its callout points on the panel's drawing) |
| `seatOf` | a seat (CPG, WSO, RIO, operator) of another variant: the Cockpits page lists it under that version's seats |
| `src`, `page` | on controls, rows, areas, panels and items: the source id (`sources[].id`) and the page |
| source `file` | a document that comes with a game, relative to its folder, where it has no web address; `kind` may be `game-files` (the game's own control lists) |

An inheriting variant takes its parent's modes, overview and panels unless it gives its own.

### Aircraft

| Field | |
|---|---|
| `id` | lower case with dashes, unique (`f-16`, `eurofighter-typhoon`, `x-wing`) |
| `category` | `fixed-wing`, `helicopter` or `space` |
| `fiction` | `true` for craft of films, series and games (Star Wars, Battlestar Galactica, Star Citizen, Elite ...): no drawing of the design, see [Licensing](#licensing) |
| `franchise` | the work a fictional craft is from |
| `nations` | ISO-like short names (`US`, `UK`, `FR`, `DE`, `RU`, `SU` (Soviet Union), `CN`, `SE`, `IT`, `ES`, `JP` ...) |
| `role` | `fighter`, `attack`, `multirole`, `interceptor`, `bomber`, `trainer`, `aerobatic`, `transport`, `airliner`, `ga` (general aviation), `business`, `helicopter-attack`, `helicopter-utility`, `helicopter-transport`, `helicopter-light`, `starfighter`, `freighter`, `explorer`, `capital`, `spacecraft` |
| `era` | `ww1`, `interwar`, `ww2`, `korea`, `cold-war-early` (to ~1965), `cold-war-late` (to ~1991), `modern`, `future` |
| `hidden` | a template other variants inherit from (a game's default control scheme); not listed |

### Variants

A variant is what has its own controls: a version (F-16A / F-16C), a block (Block 50), a tranche (Typhoon Tranche 1),
a lot (F/A-18C Lot 20), a mark (Spitfire Mk IX), a seat (AH-64D pilot / CPG) or a ship.

- `inherits`: the id of another variant (any aircraft). The variant starts from that one's data; its own fields replace
  the parent's, its controls replace the parent's control with the same id and add new ones, `removeControls` drops
  controls, `drawings` are merged, `sources` are joined.
- `games`: where it is flown. `game` is JoyPro's game id (`DCS`, `FalconBMS`, `IL2Game`, `IL2Korea`, `MSFS2020`,
  `MSFS2024`, `XPlane11`, `XPlane12`, `WarThunder`, `StarCitizen`, `EliteDangerous`, `X4`, `VTOLVR`, `NuclearOption`,
  `ArmaReforger`, `Arma3`, `FSX`, `P3D5` ...), `plane` the game's own aircraft name where JoyPro's function tables know it
  (`DB/Functions/<game>.tsv`, column `plane`: `F-16C_50`, `FA-18C_hornet`, `AH-64D_BLK_II_PLT`); empty for games whose
  tables are per game.
- `drawings`: device → drawing id. A device without one gets the generic drawing of its kind.
- `confidence`: `high` (the controls come from the aircraft's or the game's manual), `medium` (consistent secondary
  sources), `low` (partly inferred; `notes` says what).
- `sources`: at least one. `kind`: `manual` (flight manual, NATOPS, Dash-1 public excerpts), `game-manual` (DCS, BMS,
  IL-2, MSFS ...), `wikipedia`, `reference` (books, museums, official pages), `tutorial` (videos, community guides),
  `film` (the work itself).

### Controls

| Field | |
|---|---|
| `id` | the control vocabulary's part id where there is one ([devices.md](devices.md#control-vocabulary)): `stick.trigger`, `stick.weapon_release`, `stick.hat.trim\|tms\|dms\|cms\|N`, `stick.button.index_lower`, `stick.pinky.lever\|button`, `stick.gimbal`, `throttle.hat.mic`, `throttle.speedbrake`, `throttle.boat`, `throttle.china`, `throttle.coolie`, `throttle.dogfight`, `throttle.cage`, `throttle.ministick`, `throttle.axis.main\|left\|right`, `collective.hat.N`, `yoke.hat.N` ...; else `<device>.<kind>.<name>` (`throttle.knob.man_rng`, `throttle.wheel.ant_elev`) |
| `device` | `stick`, `throttle`, `cyclic`, `collective`, `yoke`, `sidestick`, `quadrant`, `rhc`, `thc` (spacecraft hand controllers), `pedals`, `panel` |
| `type` | `hat4`, `hat8`, `hat2`, `rocker`, `button`, `trigger`, `lever`, `paddle`, `switch2`, `switch3`, `slider`, `wheel`, `knob`, `encoder`, `selector`, `ministick`, `axis`, `gimbal`, `twist`, `detent`, `guard` |
| `where` | position and role in words: "4-way hat at the top right, under the thumb" |
| `part` | the drawing's part it is drawn on when that differs from `id` (generic drawings) |
| `functions` | one per direction: `dir` (`up down left right press fwd aft center in out cw ccw stage1 stage2 x y pitch roll yaw axis long double hold pull push on off posN upleft upright downleft downright`, or empty for the only action), `text` (short, at most ~45 characters, shown in the table), `fn` (the JoyPro function id from `DB/Functions/slots.json`, empty when JoyPro has none), `note` |

The stick side (stick, cyclic, yoke, side-stick, RHC) and the throttle side (throttle, collective, quadrant, THC) must
each have at least one control: a fixed-wing aircraft has a stick and a throttle, a helicopter a cyclic and a
collective, an airliner a yoke or side-stick and a quadrant.

### Panels

`area`: `glareshield`, `front-left`, `front-center`, `front-right`, `left-console`, `right-console`, `center-pedestal`,
`overhead`, `left-side`, `right-side`, `floor`, `rear`, `helmet`, `seat`. The page draws a plan of the cockpit from them
(JoyPro's own boxes: glareshield, main panel, consoles, pedestal, overhead) with the panel names.

## Drawings

```json
{ "format": "joypro-cockpit-drawing", "version": 1, "id": "f16-sidestick", "kind": "stick",
  "name": "F-16 side-stick grip", "view": "the grip from behind and left, as the pilot sees it",
  "origin": "JoyPro's own vector drawing ... not traced from a photo.", "generic": false,
  "width": 800, "height": 1000,
  "shapes": [ { "kind": "path", "role": "body", "path": "M284,320 C282,250 ... Z" },
              { "kind": "rect", "role": "bodyAlt", "x": 110, "y": 770, "w": 620, "h": 180, "r": 28 },
              { "kind": "ellipse", "role": "shadow", "x": 420, "y": 968, "w": 330, "h": 20 } ],
  "parts": [ { "id": "stick.hat.tms", "type": "hat4", "x": 546, "y": 282, "r": 32, "angle": 0 } ] }
```

- `shapes`: the silhouette in drawing units (the same primitives as the device drawings, `DeviceSchematic`): `path`
  (absolute `M L Q C Z` with `x,y` pairs), `rect` (x, y, w, h, r), `ellipse` (centre x, y, radii w, h), `line` (x, y to
  w, h), `text`. `role` picks the theme colours: `shadow`, `body`, `bodyAlt`, `face`, `screen`, `control`, `mark`, `hot`,
  `accent`, `line`, `label`.
- `parts`: the controls, drawn by JoyPro in one consistent style (`CockpitSchematic.Glyph`): `hat4`, `hat8`, `hat2`,
  `rocker`, `button`, `release` (red), `trigger`, `lever`, `paddle`, `switch2`, `switch3`, `slider`, `wheel`, `knob`,
  `encoder`, `selector`, `ministick`, `gimbal`, `axis`, `twist`, `detent`, `guard`, `label`. `r` is the size, `angle`
  turns it (degrees). A control of a variant is drawn where the part with its id (or its `part`) is; the callout table's
  line goes there.
- `aircraft`, `variants`, `except`, `device` (optional): where the drawing is used without the variants naming it. A
  device of a variant gets, in this order: the drawing the variant names (`drawings`), a drawing that lists the variant,
  one that lists the variant it inherits from, one that lists its aircraft (unless the variant is in `except`), else
  the generic drawing of its kind. `device` defaults to the drawing's `kind`. Fictional craft never get a non-generic
  drawing.
- `generic: true`: a neutral drawing of a kind of device (`generic-stick`, `generic-throttle`, `generic-sidestick`,
  `generic-yoke`, `generic-quadrant`, `generic-cyclic`, `generic-collective`, `generic-space-stick`,
  `generic-space-throttle`, `generic-pedals`, `generic-panel`). On a generic drawing a control whose part the drawing
  does not have takes the next free part of a fitting kind (a hat on a hat, a lever on a lever), so every aircraft is
  shown with its controls in their listed order.

How a schematic drawing (`drawings/`) is made: the shape of the real grip and the positions of its controls are
reconstructed from the public descriptions (the aircraft's manual excerpts, the game manuals' HOTAS chapters, several
photos looked at for proportions), then drawn freely as a clean silhouette in JoyPro's style with its own coordinates.
No photo is traced, no manual figure copied. `engine cockpits render --drawing <id> --png <file>` shows a drawing with
every part's id for checking.

### Line drawings of real grips

`line-drawings/<aircraft>.<device>.json` (`aircraft`, `device`: stick, throttle, cyclic, collective, yoke) holds
JoyPro's own line drawing of a real grip, in the same format as the device drawings. With a `parts` object (part id ->
[x, y], 0..1 of the drawing) placing at least half of a sheet's controls, the Cockpits page draws that sheet on the
line drawing; without it, the drawing is shown under "JoyPro's drawings" above the photo links (no download, no
consent). Fictional craft never get one. It is painted in the user's drawing style (Settings > Drawings).

The picture order of a sheet: JoyPro's line drawing with positions, else JoyPro's schematic drawing of the grip, else
the generic drawing of its kind; the real photos are links shown after the user's consent.

## Callout sheets

The Cockpits page shows the manual pages (above); the callout sheets remain for `engine cockpits render` without
`--page` and for the setup wizard's pictures. Every device of a variant is one sheet: the drawing, a table per control (title "N  Name", one row per direction with
the direction's icon and the function's text), thin orthogonal lines from the control to its table. It is the Layout
page's callout layout (`CalloutLayouter`, [device-reference.md](device-reference.md#tables-and-lines-callout-sheets)),
so the tables never overlap and the lines do not cross tables. Controls the drawing has no part for are listed under
"Other controls" without a line. Numbers run across the devices of a variant (stick 1-10, throttle 11-18).

## The Cockpits page

- **Picker** (left): search over names, aliases, variants, makers, franchises and game aircraft names; filters for the
  game, the era, the role, the nation and the kind (fixed-wing, helicopter, space, fiction); the variant (version /
  block / tranche) list of the picked aircraft. A version with several seats gets a **Seat** choice.
- **The manual** (`CockpitManualPages`, `ManualPageView`): tabs for the **Cockpit overview** and each grip (Stick,
  Throttle, Cyclic, Collective ...), a **Panels** list with one page per panel, previous / next. The overview's areas
  lead to their panel (or grip) page with a click. A table cell shows the JoyPro function it maps to as a tooltip.
- **Search this cockpit**: words over every control, row, cell, area and item of the variant; the hits are listed (page
  and text) and highlighted on the pages.
- **Export page (PNG)** / **All pages (PDF)**: the pages in the user's drawing style (Settings > Drawings) to
  `Documents\JoyPro2\Cockpits`. `engine cockpits render <aircraft> <variant> --page all|overview|stick|throttle|panels|panel:<id> --png <file> | --pdf <file>`
  makes the same pages from the command line.
- Below the pages: the notes, the photo links and the sources.
- **Photos**: the aircraft's photo links. JoyPro asks before it loads any ("JoyPro has photos of the <aircraft>
  cockpit on <site>. Download them and show them here?" Yes / No / Always for cockpits / Never); see
  [privacy.md](privacy.md#cockpit-photos).
- **Compare with my setup**: with a profile open, each function gets a mark in the HOTAS tables when a relation of the
  profile holds it for that game and aircraft (`engine cockpits compare`), with the relation's name and its input.
- **Use this layout**: hands the variant to the setup wizard (below).

## For the setup wizard and scripts (API)

- `engine cockpits get <aircraft> [<variant>] [--json]` returns the resolved variant as `joypro-cockpit-layout`:
  `Aircraft`, `Variant`, `Games`, `Devices[]` (`Device`, `Title`, `Drawing`, `Generic`, `Controls[]` with `Id`, `Name`,
  `Type`, `Where`, `Part`, `Placed`, `X`, `Y` (0..1 on the drawing) and `Functions[]` with `Dir`, `Text`, `Fn`, `FnName`,
  `Vocabulary` (the control vocabulary id of that direction, e.g. `stick.hat.tms.up`, `stick.axis.pitch`) and `Actions[]`
  (`Game`, `Plane`, `Action`: the game's own command id from `DB/Functions/<game>.tsv`)), `Panels`, `Sources`, `Photos`,
  `Links`. The aircraft and the variant may be given by id, name or alias; a variant id alone also works
  (`engine cockpits get f-16c-block-50`).
- In the GUI: `CockpitsViewModel.UseLayoutRequested` (event, `CockpitLayoutRequest` with `AircraftId`, `VariantId`,
  `Games` and the CLI arguments) and `MainWindowViewModel.PendingCockpitLayout` (the last request, until the wizard takes
  it with `TakePendingCockpitLayout()`).
- `cockpits get --json` also gives `Modes`, `DefaultMode`, the `Overview` (areas), the `Panels` with their `Items`, and
  per function `Press`, `When`, `By` and `FnBy`. A function's `Text` and `Fn` are the default mode's cell: the setup
  wizard places a function where the default mode has it (`SetupCockpits`).
- `engine cockpits list [query] [--game G] [--era E] [--role R] [--nation N] [--category fixed-wing|helicopter|space|fiction|real]`,
  `search <text>` (aircraft and controls / functions: "TMS", "china hat"), `compare <aircraft> [<variant>] [--game G]`
  (with `--profile`), `render <aircraft> [<variant>] --png <file> [--device d] [--style dark|light] [--print-width px]`
  (the callout sheets), `render ... --page <page> --png <file> | --pdf <file> [--scale s] [--highlight words]` (the manual
  pages), `photos [query] [--coverage]`, `coverage` (also the manual pages per game), `drawings`,
  `check [--structure]` (`--structure`: only what is wrong, not what is still missing).

## Pictures from your games

**Build pictures from my games** on the Cockpits page (or `engine cockpits pictures build`) draws the cockpits of the
games installed on this PC from the games' own files. The pictures are made on this PC only. They are kept with the game
data (`<data>\GameData\CockpitPictures\<game>\<aircraft>`) and are never part of JoyPro or its repository. Wherever a variant's
game has pictures, the manual pages show them in place of JoyPro's drawings:

- **Cockpit overview**: the whole cockpit from behind the pilot's head. Its numbered callouts lead to the panels and
  the grips.
- **Stick / Throttle**: the game's grip on its own. The HOTAS table is unchanged. A control has a callout where the model
  names a part for it (A-10C: `STICK-BTN-TMS`, `TRTL-R-HAT-SLEW`); controls the model has no part for are listed under
  the picture.
- **One page per panel** (the game's own panels): a frontal picture with every clickable control numbered, and a
  numbered list with the game's own name for the control (DCS: the hint text of `clickabledata.lua`; X-Plane: the
  manipulator's tooltip). For DCS the list also gives the bindable commands of the same device and command number
  (JoyPro's DCS command list of the aircraft); for X-Plane it gives the command or dataref.
- **Picture from the game / JoyPro's drawing of it**: either the render itself, or JoyPro's line drawing of it (outlines,
  details and the printed labels traced from the render, plus flat cel colours). The drawing is shown in the user's
  drawing style (Settings > Drawings), like the device drawings.

Where the callouts come from: they are computed from the model, never placed by hand. In DCS the clickable connector
of each element (`PNT_...`/`PTR-...` in the cockpit EDM, named in `clickabledata.lua`) gives the place; the panel comes from the
command list's category ("Left Console; ENG & JET START Control Panel"), else the connector's panel token. In X-Plane
the manipulator geometry of the cockpit objects gives the place. The grips are found by the animation arguments
`mainpanel_init.lua` gives them (DCS), by the VR hand connectors (`STICK_HANDLE`, `THROTTLE_HANDLE`) or by node names, and by the
yoke and throttle datarefs (X-Plane).

| Game | What is read | State (2026-10-17) |
|---|---|---|
| DCS World | `Mods\aircraft\*`: entry.lua (aircraft, cockpit folder), mainpanel_init.lua (model, grip arguments), clickabledata.lua, Views.lua (seats, eye points), the cockpit EDM and its textures (DDS in zip archives) | 48 cockpits on the maintainer's PC (about 1,900 pictures, 15,000 callouts). Not complete: F-14BU (no clickable data yet) and SA342 (no cockpit model in the files); some C-130J, OH-58D and F-14 textures are not found (flat colour) |
| X-Plane 12 | `Aircraft\**\*.acf` (objects, pilot's eye) and the OBJ8 objects with their textures | 24 aircraft (every one in Aircraft) |
| MSFS 2024 | models are in encrypted archives | not possible from the files |
| MSFS 2020 | official packages hold plain glTF interiors (parts named by function) | not yet: planned, and these models would also serve the MSFS 2024 aircraft |
| IL-2, Nuclear Option, Star Citizen | proprietary or packed formats | not yet |

The Lua files are read with DCS's own interpreter (`bin\luae.exe`) and small harness scripts that only print; nothing in
a game folder is written. A build skips an aircraft whose pictures are newer than its model and were made for the same
game version (`--force` makes them again). It takes about two seconds per picture; a big DCS module has 50 to 80 pictures.

`engine cockpits pictures build [--game DCS|XPlane12|all] [--aircraft <name>,...] [--force] [--out <folder>]`,
`engine cockpits pictures status`, `engine cockpits render ... --pictures picture|drawing|none`. The format:
`pictures.json` (`joypro-cockpit-pictures`): per view `id`, `kind` (overview, device, panel), `title`, `area`, `seat`,
`image`, `drawing` (a `joypro-device-drawing`), `width`, `height`, and `callouts` (`n`, `id`, `label`, `x`, `y` 0..1 of
the picture, `kind`, `device`, `commands`); `index.json` names the set of each of the game's aircraft names. The code is
in `engine/CockpitPictures` (EDM, OBJ8, DDS/TGA/PNG readers, a software renderer, the line tracer, the view planner).

## Photos

Real photos are only linked: `photos[].url` (the picture) and `page` (where it is published, with its author and
license). Preferred: Wikimedia Commons (most US military photos are public domain; others CC licences) and official
pages (museums, manufacturers, air forces). For fictional craft the links point to the work's official or fan
encyclopedia pages.

- The GUI downloads a photo only after the user's yes for that aircraft (`CockpitPhotoConsent`, built on
  `DevicePhotoConsent`: the answers are kept with the device photo answers under the key `cockpit:<aircraft id>`;
  "Always for cockpits" / "Never for cockpits" under `cockpit:*`; the global "Never" of the device photos also stops
  cockpit photos). It is cached in `%LOCALAPPDATA%\JoyPro2\DeviceImages` (`DeviceImageCache`) and shown with its credit
  and license. A Wikimedia Commons file is loaded as Wikimedia's 960 px thumbnail (what Wikimedia asks programs to
  use), the original only when the thumbnail fails.
- The engine never downloads a photo.
- The maintainer's private backup: `tools\backup_cockpit_photos.ps1 -Target <folder outside the repository>` (default
  `..\JoyPro2-private\cockpit-photos`) downloads every linked photo with a manifest (aircraft, view, address, page,
  credit, license, date, SHA-256, size). Neither the build nor the release reads it.

## Licensing

- Real aircraft: the schematic drawings (`drawings/`) are JoyPro's own reconstructions of the grip's shape and its
  control layout (functional facts: which switch is where and what it does), drawn in JoyPro's style. They are not
  traced from photos and do not copy a manual's figure. The line drawings (`line-drawings/`) are JoyPro's own drawings
  of the cockpits, panels and grips. No manual page, figure or photo is in the repository or the release.
- Fictional craft (films, series, games): **no drawing of their design**. They get the generic stick and throttle with
  their function list only, plus links. A drawing of a franchise's cockpit would be a derivative of a copyrighted
  design ([licensing-audit.md](licensing-audit.md), P7).
- Names of aircraft, franchises and makers are used to say what is described (nominative use); no logos.
- Text is JoyPro's own wording. Facts come from the listed sources; no source text is copied beyond short control
  labels.

## Growing the database

`tools/cockpit_db.py` (Python 3, no packages):

| Command | |
|---|---|
| `functions [query]` | JoyPro's function ids (`DB/Functions/slots.json`) |
| `seed <plane> [--game G]` | the HOTAS rows JoyPro's function tables already have for a game aircraft (DCS: physical control text per function) |
| `ids [query]` | aircraft and variant ids already in the database |
| `check [draft.json ...]` | the database plus the drafts through `engine cockpits check` (only the drafts' problems are printed) |
| `check --patch <patch.json> ... [--all] [--structure]` | the database with patches applied (a temporary copy), the patched variants' problems |
| `patch <patch.json> ...` | applies patches (`{"format": "joypro-cockpit-patch", "variants": {"<id>": {fields}}, "aircraft": {...}, "newVariants": {"<aircraft>": [...]}, "newAircraft": {"<file.json>": [...]}}`; a field given replaces the variant's field, `null` removes it) |
| `pages <aircraft> [variant] [--page ...] [--out dir] [--dark] [--patch p.json]` | the manual pages as PNG for a look |
| `merge <draft.json> --into <file.json> [--replace]` | adds the drafts' aircraft (or new variants of an aircraft already in the file) |
| `render <aircraft> [variant] [--out <folder>] [--light]`, `render --drawing <id>` | the sheets as a PNG for a look |
| `get <aircraft> [variant]` | a variant with its controls, ids, types and where each is drawn |
| `photos-merge <photos.json> ...` | adds verified photo links (`{"format": "joypro-cockpit-photos", "photos": {"<aircraft>": [ ... ]}}`); an address already listed is skipped |
| `coverage`, `drawings` | numbers (also every control no drawing shows); drawings with their parts |

The procedure per batch (also the brief for research helpers):

1. Take a list of types; check `ids` so nothing is added twice. One aircraft entry per type family (all F-16 versions
   in `f-16`), one variant per version that has its own controls.
2. For each variant find the HOTAS description: the game manual's HOTAS chapter (DCS, BMS, IL-2, MSFS), the public
   flight-manual excerpts, the manufacturer's or air force's pages, then tutorials. For a DCS module run `seed` first: it
   lists the function ids JoyPro already maps to that module's stick and throttle, with the physical control in words.
3. Write the controls: vocabulary ids, device, type, where, and one function per direction with a short text and the
   function id (`functions <word>` finds it; empty when there is none, never a guess).
4. Panels briefly, the cockpit paragraph, notes (differences to the other variants; what is uncertain), sources with
   working https addresses, confidence.
5. `check <draft>` until it reports no problem, then `merge`. `render` a few for a look.
6. Drawings are made separately (see [Drawings](#drawings)); a new drawing's part ids are the controls' ids of the
   variants that use it. `check` reports a part a variant names that the drawing lacks.

## Coverage (2026-10-13, `engine cockpits coverage`)

Every listed variant (462, plus 9 hidden game templates) has its manual pages: an overview with numbered areas, the
grips with their HOTAS tables and numbered panels; `cockpits check` reports no problem.

| Game | Variants | HOTAS rows | Variants with mode columns / columns | Panels | Numbered items | JoyPro's drawings |
|---|---|---|---|---|---|---|
| DCS (every module on the maintainer's PC, all versions and seats) | 81 | 2654 | 30 / 104 | 1223 | 9981 | 505 |
| Falcon BMS (F-16 blocks, Hornets, A-10) | 17 | 993 | 16 / 50 | 413 | 2413 | 211 |
| IL-2 Great Battles / IL-2 Korea | 66 / 2 | 974 / 44 | – | 364 / 32 | 2968 / 287 | 149 / 26 |
| War Thunder (type level) | 155 | 4309 | 43 / 125 | 1532 | 12507 | 539 |
| MSFS 2020 / 2024 | 51 / 54 | 1214 / 1323 | 5 / 15, 9 / 23 | 288 / 295 | 2561 / 2837 | 2 / 2 |
| X-Plane 11 / 12, FSX, Prepar3D | 14 / 18, 12, 14 | 335 / 437, 266, 340 | – | 71 / 93, 59, 70 | 579 / 808, 492, 592 | – |
| Star Citizen / Elite Dangerous / X4 | 42 / 32 / 21 | 2184 / 2112 / 588 | 42 / 294, 32 / 160, 21 / 84 | 276 / 224 / 105 | 1351 / 1027 / 318 | none (fiction) |
| VTOL VR / Nuclear Option / WARDOGS / Tiny Combat Arena | 9 / 13 / 6 / 1 | 147 / 253 / 90 / 15 | 7 / 19, – | 104 / 37 / 13 / 4 | 560 / 137 / 50 / 10 | none (game designs) |
| Arma 2 / 2 OA / 3 / Reforger | 12 / 7 / 3 / 3 | 317 / 263 / 67 / 76 | 2 / 6, 2 / 7, 1 / 2, 2 / 4 | 102 / 119 / 23 / 34 | 745 / 881 / 174 / 327 | 39 / 104 / 8 / 21 |
| Real types outside the games (spacecraft, F-35 ...) | 26 | 306 | – | 108 | 402 | – |
| Fiction (films and series, Star Wars: Squadrons, No Man's Sky) | 44 | 402 | 8 / 32 | 139 | 400 | none |
| **All** | **462** | **13075** | **170 / 775** | **3495** | **24486** | **778** |

(Variants count in every game they are flown in.)

### Before version 2

| | Aircraft | Variants |
|---|---|---|
| Fixed-wing (jets, warbirds, WWI, airliners, business and light aircraft, transports) | 121 | 235 |
| Helicopters (military and civil) | 21 | 46 |
| Real spacecraft (Space Shuttle, Apollo CM/LM, Soyuz, Gemini, Mercury, X-15, Crew Dragon, Buran) | 8 | 13 |
| Fiction: film and series craft (Star Wars, BSG, Babylon 5, Firefly, The Expanse, Interstellar, Aliens, Avatar, Airwolf, Blue Thunder, Firefox, Darkstar, Halo) and game ships (Star Citizen, Elite Dangerous, X4, VTOL VR, Nuclear Option, WARDOGS, Tiny Combat Arena) | 70 | 152 |
| **Listed in all** (plus 6 hidden game templates) | **220** | **446** |

| Game | Aircraft | Variants |
|---|---|---|
| DCS (every flyable module; menu-only slots excepted) | 44 | 77 |
| Falcon BMS (F-16 blocks, Hornets, A-10) | 3 | 17 |
| IL-2 Great Battles / IL-2 Korea | 41 / 2 | 66 / 2 |
| War Thunder (type level) | 74 | 151 |
| MSFS 2020 / 2024 | 38 / 40 | 51 / 54 |
| X-Plane 11 / 12 | 12 / 13 | 14 / 18 |
| FSX / Prepar3D | 11 / 10 | 12 / 14 |
| Star Citizen / Elite Dangerous / X4 | 12 / 7 / 6 | 42 / 32 / 21 |
| VTOL VR / Nuclear Option / WARDOGS / Tiny Combat Arena | 6 / 13 / 3 / 1 | 9 / 13 / 6 / 1 |
| Arma 2 / 2 OA / 3 / Reforger | 10 / 7 / 3 / 2 | 12 / 7 / 3 / 3 |

- Drawings: 280 (269 of real grips, 11 generic); 284 variants show JoyPro's drawing of the real grip; every control of
  every variant is drawn (no "Other controls"). Real devices still on a generic drawing: 24 (back seats with their own
  grips, A-4, Harrier GR.3 / Sea Harrier, AH-64A, A-10A, B727, ATR 72, Baron, three WWI throttles).
- Functions: 9978 rows, 8283 with a JoyPro function id. Confidence: 61 variants high, 181 medium, 204 low (types without
  public HOTAS documentation; the variant's notes say what is inferred).
- Photos: 477 links for 165 aircraft (real aircraft: Wikimedia Commons with credit and license, a few makers' pages;
  fiction: page links only).

## Tests

- `dotnet test gui/JoyPro.Gui.Tests --filter CockpitDatabaseTests`: the setup wizard reads the database and the answer of `cockpits get`; Commons photos load as thumbnails; every variant has stick-side and throttle-side
  controls, every function id exists, every variant has a source and a confidence, every drawing's paths are valid and
  its parts inside, fictional craft use only generic drawings and photos are https links; every variant's sheets build
  without overlapping tables; the Cockpits page renders offscreen with a few aircraft (drawings only, no photo is
  downloaded), and so do the manual pages of a few variants (`JOYPRO_COCKPITS_PREVIEW` names a folder to keep them); a
  synthetic version-2 variant checks the modes, the rows with short / long presses and ground / air, the merged cells,
  the overview's links, the numbered panels, the seats and the problems `check` reports.
- `engine\tests\smoke\cockpits.ps1`: `cockpits check`, `list` with filters, `search`, `get` (JSON for the wizard,
  vocabulary and game actions), `render` (PNG), `compare` on a profile, `photos --coverage`.
