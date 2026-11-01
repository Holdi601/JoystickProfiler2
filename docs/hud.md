# HUD

The HUD puts your sticks, throttles and pedals on screen, like a streamer's controller overlay:

- **Picture:** the device's official product picture (background cut away) tilts and twists with the stick.
- **Stick element:** a dot in a ring (or a square, a crosshair, a radar, a big dot) shows the stick position; a tick shows twist.
- **Levers and axes:** throttle levers, brakes and every axis move as bars.
- **Buttons:** held buttons light up with their names from the device catalog ("Trigger second detent"). Devices with up to 32 buttons also show a button grid. Hats light their direction.
- **Aircraft stick and throttle** (2.6.0): the stick and throttle of the aircraft you fly, as the game draws them, moving with yours; the control of a pressed button lights up on them.
- **What the button does:** the function of each pressed button in the running game, for example "Gear Toggle" with "Landing Gear Lever - UP/DOWN" below it.
- **Header:** the game or DCS aircraft with its picture.

Open it with **HUD** in the bottom bar. **Start HUD** (also on the Start page) starts it: on your screen over the game,
and/or for your stream (see "Streamer mode").

## Designer (HUD page)

- **Preview:** the preview shows live input while the page is open, over a DCS aircraft's main menu picture.
  - Drag a widget to move it, and click it to edit it on the right.
  - **Preview: My screen / The stream** shows the widgets ticked for your screen or for the stream.
  - **Arrange automatically** puts the widgets along the bottom of the screen: throttle, collective and left-hand devices on the left, pedals in the middle, the right-hand stick on the right. A new HUD is arranged this way once the pictures are in.
- **Where the HUD shows** (2.6.0, independent of each other): *On my screen (over the game)*, *Page for OBS (Browser
  Source, transparent)* with its address, *Copy* and the port, *Stream window (Window Capture)* with its background
  (transparent or a chroma key colour), and *Updates per second at most* (15, 30 or 60; 30 by default).
- **Widgets:** two ticks per widget: shown on your screen (monitor) and shown in the stream (OBS).
  - Every connected device has a widget. Sticks, throttles and pedals start shown; panels, keyboards and others start hidden.
  - The game header, the binds ticker and the aircraft widget are widgets too. The aircraft widget starts shown on a new HUD
    and unticked on a HUD made with an older JoyPro.
  - **Reload devices** reads the devices again. **Reset places** puts every widget back to its default place.
- **Per widget:**
  - shown on my screen, shown in the stream, size, **Style of this widget** (*The HUD's style*, or any preset or own style);
  - **Show:** name, kind (stick, throttle), picture, tilt with the stick, buttons light up, held buttons by name, axes
    and levers, axis names, axis values, stick position (gimbal), hats. The header: its picture.
  - **Picture:**
    - *Photo* (the manufacturer's picture) is also what *Auto* uses.
    - *Layout* is your JoyPro layout picture. The pressed buttons light up at the positions stored in the layout. JoyPro 1 layouts put these positions on the label boxes.
  - **Grip on this base:** for a base that takes several grips, such as VPforce Rhino or Virpil WarBRD. The grip decides the picture and the button names.
  - **Stick axes** (2.5.0): where the stick element (gimbal dot, twist and the tilt of the picture) gets **X**, **Y** and **Twist** from. *Automatic* (the default) takes the device's own axis; a grip that is a device of its own without X/Y (a VKB grip on a VPforce Rhino base: buttons from the grip, axes from the base) takes them from the base it sits on. Only a real stick-like device that is no base itself gets a base: a **virtual device** (vJoy, also the one JoyPro's forwarder tunnels into; ViGEm; found by VID:PID `1234:BEAD` or its name) never is a base and never sits on one (it only mirrors real devices), and a **base** (kind *base*, or one that takes grips like the VPforce Rhino: it has its own X and Y) never sits on another. The base is a real stick-like device with its own X and Y, fewer buttons than the grip and no other hand; the same alias (Layout page) wins, then a base (by its kind or its grip choices), then the fewest buttons. Example (2.6.0, the user's set): VKB Gunfighter grip (right, 128 buttons), VPforce Rhino (64 buttons, takes grips), L-VPC WarBRD (left, 31 buttons), vJoy (32 buttons): the VKB sits on the Rhino; the Rhino, the WarBRD and vJoy sit on nothing (`HudStickBaseTests`). The aircraft widget's *Automatic* stick and throttle skip virtual devices as well. Each box can also name any device's axis (*Rhino · X*). An axis that is missing, has not reported yet or is stuck at an end of its travel since the first report (a grip's X/Y without sensors) is centred, never a corner. The line below the boxes says what *Automatic* uses. Kept in `hud.json` per widget (`AxisX`, `AxisY`, `AxisTwist`: empty = Automatic, else `<device>|<input>`).
  - **The aircraft widget:** stick, throttle, throttle travel bar, stick position, controls light up, held controls by
    name; **Aircraft** (*Automatic (the aircraft you fly)* or any picture set on this PC) and **Moves with** (*Automatic*
    or the stick and the throttle you pick). The line below says what it shows and where the pictures come from.
- **Look:** see "Styles".
- **What the button does:** lines, seconds, font size, device and button line, only while a game runs.

Everything is kept in `%APPDATA%\JoyPro2\hud.json` and is there again after a restart: places, sizes, what each widget
shows on screen and in the stream, the HUD's style with every option, own styles, each widget's style, the outputs,
the port, the stream window's background and the update rate. Nothing is saved before `hud.json` was read (the HUD page,
Start HUD on the Start page or the HUD page, the devices read for the HUD): the HUD page's controls exist from the
window's start and may write a value back, which once saved the never-read defaults over the file (2.6.0: the page for
OBS and the stream window came up off). A `hud.json` that cannot be read is kept as `hud.json.unreadable-<time>`
before JoyPro writes its own (`HudSettingsRestartTests`).

## Styles

- **Style:** 40 presets, from the 2.5.0 styles (*Glass*, *Neon*, *Minimal*) over clean ones (*Frosted light*,
  *Streamer clean*, *Streamer bold*, *Compact*, *Large text*, *Pastel*, *Outline only*), cockpit displays (*Military
  green HUD*, *Amber MFD*, *Airliner PFD*, *Glass cockpit*, *Night vision*, *Red cockpit lighting*, *Vector
  wireframe*, *Retro terminal*, *Warbird*, *Hologram*), games and genres (*Space sim orange*, *Holo cyan*, *Cyberpunk*,
  *Synthwave*, *Neon pink*, *Racing*, *Tactical tan*, *Navy and gold*, *Stealth*, *Ghost*), colour themes (*Arctic*,
  *Desert*, *Ocean*, *Sunset*, *Royal*, *Carbon*) to readability (*High contrast*, *High contrast light*,
  *Monochrome*, *Monochrome light*). **›** steps to the next one. The list (`Models/HudStylePresets.cs`) is checked to
  use every shape and at least five fonts.
- **Accent colours** (one click), **Accent**, **Pressed** (held buttons and lit markers; the accent when not set),
  **Opacity**.
- **Design options per element type:**
  - *Cards:* background (with its opacity: transparent = no card), border colour, border opacity, border width,
    corners, shadow (none, soft shadow, glow).
  - *Text:* font (JoyPro's font, monospace, Bahnschrift, Segoe UI, Consolas, Arial, Verdana, Trebuchet MS, Georgia,
    Courier New, Impact), text size, letter spacing, text colour, captions colour, bold names.
  - *Stick element:* shape (ring, square, crosshair, radar, dot), size, glow.
  - *Bars and levers:* shape of the axis bars and of the levers (rounded, square, thin, segmented, outline), bar
    height, lever width, the colour of the empty part.
  - *Buttons and chips:* button shape (rounded, circle, square), chip shape (pill, square, underline).
- A changed option makes the style "own design (changed)" until it is saved; **Undo changes** goes back to the style
  before the changes (the one picked, saved or imported last, or the HUD's design at start). The preview's
  **My screen** / **The stream** choice is kept in `hud.json` (`PreviewStream`).
- **Your styles:** **Save style** keeps the design under a name of your own (not a preset's name; the same name
  replaces it). **Export style...** writes it as a style file (`*.joypro-hudstyle.json`, `"format": "joypro-hud-style"`)
  to share; **Import style...** reads one (a taken name gets a number) and makes it your style and the HUD's.
  **Delete** removes an own style (widgets that used it go back to the HUD's style).
- Every change shows at once in the preview, on screen and on the stream page (the page gets the same design as CSS
  values).

## Aircraft stick and throttle

When JoyPro knows the aircraft (the widget's own pick, else **DCS aircraft**, else what the **DCS hook** reported), the
aircraft widget shows that aircraft's stick and throttle:

1. **The game's own grips:** the stick and throttle pictures of the aircraft's cockpit picture set
   (`<data>\GameData\CockpitPictures`, made by *Cockpits > Build pictures from my games* or a picture pack,
   [cockpits.md](cockpits.md)). They are rendered from the game's 3D model. JoyPro cuts them to the grip and makes them
   480 px high once, so it never scales a 1600 px picture per frame.
2. **Else JoyPro's line drawing of the real grip** (`DB\Cockpits\line-drawings`), drawn into a picture once.
3. **Else** the widget says what is missing. The device widgets stay the fallback: your device's own picture with its
   lit markers.

- **Moving:** the stick picture tilts (up to 16° of roll, 12° of pitch, 12° of twist) with your stick's element
  (*Moves with: Automatic* takes the right-hand stick, else any stick, base, HOTAS or yoke with X and Y). The throttle
  picture slides forward (46 px over the travel) and tips a little with the first lever of your throttle (*Automatic*:
  the throttle, collective or quadrant). A travel bar and the stick element can show next to them. The pictures are
  pre-rendered views moved by the deflection; there is no 3D render.
- **Lit controls:** the pressed button's control vocabulary id from the device catalog ([devices.md](devices.md);
  `engine hud devices` gives it per button as `Controls`) names the aircraft's control: a Warthog's `stick.hat.tms.up`
  lights the A-10C's `stick.hat.tms`. The place on the picture comes from the model's named parts matched to the
  catalog's controls (`CockpitPictureSet.MatchGrip`), on line drawings from their part positions. Held controls show by
  name under the grips ("TMS (Target Management Switch)"). A device without control ids in the catalog moves the
  grips but lights nothing.
- The picture set of an aircraft is found like the Cockpits page finds it; the DCS unit type ("A-10C_2") through the
  pictures' `index.json`.

## Streamer mode

The player's own HUD and the stream are independent: untick *On my screen* to play without a HUD while the stream
shows it, or show different widgets on each.

### Page for OBS (Browser Source, recommended)

1. Tick **Page for OBS (Browser Source, transparent)** and press **Start HUD**.
2. In OBS: *Sources* > **+** > **Browser**, *URL* `http://127.0.0.1:8765/hud` (**Copy** next to the address), *Width*
   1920, *Height* 1080. Nothing else is needed: the page's background is transparent, no custom CSS.
3. Optional: *Shutdown source when not visible* (OBS stops the page in scenes without it).

- The page draws the widgets ticked for the stream with the same styles as the app (the design as CSS values, the
  stick element and bars in the same shapes) and the same places on a 1920 x 1080 stage, scaled to the source's size.
- It is served by JoyPro on **127.0.0.1 only** (no other computer can open it; a request with another host name is
  refused). `GET /hud` is the page with the HUD as it is at that moment, `/hud/events` the live updates
  (Server-Sent Events: `layout` when widgets, places, styles or pictures change, `state` with the live input),
  `/hud/img/<id>` the pictures as PNG with transparency, `/hud/state` the layout and the state as JSON. Code:
  `Services/HudStreamServer.cs`, `Services/HudStreamHub.cs`, `Resources/hud-stream.html`.
- Port taken? Pick another one under **Port** (the status line says so).

### Stream window (Window Capture)

- Tick **Stream window (Window Capture)**. A normal window (not on top, it never takes the focus when it opens) shows
  the whole HUD scaled to its size; it may sit behind the game.
- Background **Transparent (per-pixel alpha)**: in OBS add *Window Capture* with *Capture Method: Windows 10 (1903 and
  up)* (Windows Graphics Capture; it also captures a window behind others, not a minimised one). Whether the
  transparency reaches OBS depends on the OBS version and the graphics driver; when the background shows black, use
  the Browser Source or a chroma key colour.
- Background **green, magenta, blue** or your own colour: add a *Chroma Key* filter with that colour in OBS.

### Live check in OBS (tests)

An automated check of the page in OBS that never touches the screen: no screen capture, no mouse or keyboard, no
JoyPro window.

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\obs_hud_live_check.ps1 [-Obs] [-UserHud | -HudJson <file>] [-Out <folder>]

- Without `-Obs` (the dry run): the opt-in test `HudObsLiveTests` starts the real `HudStreamServer` by Start HUD of a
  view model in a sandbox (`JOYPRO_DATA_DIR` in `<Out>\page\data`) with the user's four sticks and a fake device
  stream moving every axis, a demo HUD or a copied `hud.json`, and checks `GET /hud`, `/hud/state` and the live channel
  `/hud/events` (the layout, then changing states). Port 18765 (`-Port`), not JoyPro's 8765. The test alone:
  `JOYPRO_HUD_LIVE=<folder>` [`JOYPRO_HUD_LIVE_HUDJSON`, `JOYPRO_HUD_LIVE_PORT`, `JOYPRO_HUD_LIVE_SECONDS` = keep the
  page up that long, a file `stop` in the folder ends it].
- With `-Obs`: OBS Studio runs from a private copy of its program folder in portable mode (`<Out>\obs-portable`, copied
  once per OBS version), minimised to the tray, with a copy of the user's `%APPDATA%\obs-studio` as its configuration.
  Only that copy is changed: OBS 31+'s `[Locations]` point into it, a scene collection *JoyProHudTest* with a Browser
  Source on the page (1920 x 1080), obs-websocket on (random password, port 44655). The Browser Source's picture is
  taken three times through obs-websocket (`GetSourceScreenshot`) and checked: 1920 x 1080, the HUD drawn, the
  background transparent, the picture moving. Then OBS is closed.
- Safety: it refuses to run while OBS runs (also checked again right before OBS starts). The user's
  `%APPDATA%\obs-studio` is mirrored before (`<Out>\obs-backup`) and restored exactly after (robocopy /MIR), then
  compared file by file (size + SHA-256) with the state before; a difference is a failed check.
- Results: `<Out>\obs-live-check.md` (all checks), `<Out>\page\live-check.md`, `<Out>\obs-browser-source-1..3.png`.

## On screen

- The HUD window stays on top and never takes the focus from the game. It is not in the task bar or Alt+Tab, and it lets clicks through ("Click-through", applied when the HUD starts).
- It is only as large as the visible widgets, placed where the designer puts them (scaled from 1920 x 1080 to the primary screen).
- It works over windowed and borderless games. Exclusive full screen hides it (a Windows limit).

## Performance

Everything here runs while you play and must cost the game nothing:

- **Updates at most 30 a second** (*Updates per second at most*): the input poll reports about 66 times a second per
  moving device; the HUD keeps only the newest report of each device (`Services/HudInputPump.cs`) and applies them
  together, then moves the aircraft widget and sends the stream page one state. A button press waits for the same
  update.
- **Drawn only when something changed:** properties change only when their value does; the stick element and the bars
  are drawn directly by small controls (`Views/Controls/HudElements.cs`), with pens made when a colour changes, not per
  frame. A window that shows nothing new is not drawn.
- **Nothing allocated per report:** axis reports, the stick element's sources (worked out when the devices or a choice
  change), the aircraft widget's moves and the axis values' texts (from tables) make no new objects; the stream page's
  state is written into one reused buffer.
- **No work without a viewer:** without a connected page the stream server only listens (nothing is built); each page
  has one sleeping thread at below normal priority that wakes when there is something new (a keep-alive every
  15 s). The stream window draws only while it is open and something changes.
- **Pictures prepared once:** device pictures are cut out once, the aircraft's grips are cut and made 480 px high once,
  their PNGs for the page are made once.
- **Measured:** the HUD soak ([testing.md](testing.md#speed)) drives a fake device stream for minutes with the HUD
  drawn and a page connected and checks CPU, allocations, memory growth and the update rate against budgets.
- A hidden game is not looked for.

## Which game and aircraft

- The game is found by its process: DCS, IL-2, Star Citizen and every registry game (War Thunder, MSFS, X-Plane, Falcon BMS, ...). Only that game's binds are shown.
- DCS also needs the aircraft. Either pick it under **DCS aircraft**, or install the **DCS hook**: a small file `Saved Games\DCS\Scripts\JoyProOverlay.lua` plus one line in `Export.lua`. DCS then tells JoyPro the aircraft at every mission start and change (UDP to 127.0.0.1:1992).
  - The line is wrapped in `pcall`, so a missing hook never breaks Export.lua. The hook keeps the export functions of other scripts (SRS, Tacview, ...) and calls them.
  - Install and remove back up Export.lua first (restorable on the Export page). Remove takes the line out again, and the file is then byte for byte as before. Close DCS before installing or removing.
  - DCS names the unit, not the seat, so JoyPro picks the front seat's binds (AH-64D: pilot, F-14: pilot). For the back seat, pick it under **DCS aircraft**.

## Input

- **Devices and buttons:** buttons, hats and axes of every connected DirectInput device are read in the background, also while the game has the focus. Devices are matched by their GUID.
- **Modifiers:** a bind with modifiers shows when its modifiers are held; switch modifiers toggle on each press. The bind with the most matching modifiers wins.
- **Axis roles:** what each axis is comes from the device catalog (`DB/Devices`), for example roll, pitch and twist of a VKB grip (twist is `JOY_Z` there), or the left and right levers of a throttle. Devices that are not in the catalog use X/Y as roll and pitch and Rz as twist.

## CLI

- `engine hud devices [--grip <device>=<catalog id>]...`: the connected devices with model, kind, hand, product picture, axes with their role, buttons, hats, button names, each button's control vocabulary id (`Controls`, for the aircraft widget) and the layout picture with button positions (from the profile's layout for the device, else the best layout template; never generated; `Own` = the user's own layout, not a shipped template).
- `engine overlay map [--game <id>] [--plane <name>] [--unit <DCS unit type>]`: the bound buttons of that game and aircraft, with modifiers and titles.
- `engine overlay dcs-hook status|install|remove [--path <Saved Games\DCS>] [--dry-run]`.
