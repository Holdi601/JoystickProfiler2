# HUD

The HUD puts your sticks, throttles and pedals on screen, like a streamer's controller overlay:

- **Picture:** the device's official product picture (background cut away) tilts and twists with the stick.
- **Gimbal:** a dot in a ring shows the stick position, and a tick on the ring shows twist.
- **Levers and axes:** throttle levers, brakes and every axis move as bars.
- **Buttons:** held buttons light up with their names from the device catalog ("Trigger second detent"). Devices with up to 32 buttons also show a button grid. Hats light their direction.
- **What the button does:** the function of each pressed button in the running game, for example "Gear Toggle" with "Landing Gear Lever - UP/DOWN" below it.
- **Header:** the game or DCS aircraft with its picture.

Open it with **HUD** in the bottom bar. **Start HUD** (also on the Start page) shows it over the game.

## Designer (HUD page)

- **Preview:** the preview shows live input while the page is open, over a DCS aircraft's main menu picture.
  - Drag a widget to move it, and click it to edit it on the right.
  - **Arrange automatically** puts the widgets along the bottom of the screen: throttle, collective and left-hand devices on the left, pedals in the middle, the right-hand stick on the right. A new HUD is arranged this way once the pictures are in.
- **Widgets:** tick the widgets to show.
  - Every connected device has a widget. Sticks, throttles and pedals start shown; panels, keyboards and others start hidden.
  - **Reload devices** reads the devices again. **Reset places** puts every widget back to its default place.
- **Per widget:**
  - size;
  - name, picture, tilt with the stick, buttons, axes and levers, gimbal, hats.
  - **Picture:**
    - *Photo* (the manufacturer's picture) is also what *Auto* uses.
    - *Layout* is your JoyPro layout picture. The pressed buttons light up at the positions stored in the layout. JoyPro 1 layouts put these positions on the label boxes.
  - **Grip on this base:** for a base that takes several grips, such as VPforce Rhino or Virpil WarBRD. The grip decides the picture and the button names.
- **Look:**
  - Style: *Glass* (frosted dark cards), *Neon* (glowing outlines) or *Minimal*.
  - Accent colour and opacity.
- **What the button does:** lines, seconds, font size, device and button line, only while a game runs.
- **Stream window (for OBS):** instead of the transparent on-screen HUD, a normal window shows the whole HUD on a plain background (default `#00FF00`). In OBS, add it as *Window Capture* and add a *Chroma Key* filter with that colour.

The layout is saved in `%APPDATA%\JoyPro2\hud.json`.

## On screen

- The HUD window stays on top and never takes the focus from the game. It is not in the task bar or Alt+Tab, and it lets clicks through ("Click-through", applied when the HUD starts).
- It is only as large as the visible widgets, placed where the designer puts them (scaled from 1920 x 1080 to the primary screen).
- It works over windowed and borderless games. Exclusive full screen hides it (a Windows limit).

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

- `engine hud devices [--grip <device>=<catalog id>]...`: the connected devices with model, kind, hand, product picture, axes with their role, buttons, hats, button names and the layout picture with button positions (from the profile's layout for the device, else the best layout template; never generated; `Own` = the user's own layout, not a shipped template).
- `engine overlay map [--game <id>] [--plane <name>] [--unit <DCS unit type>]`: the bound buttons of that game and aircraft, with modifiers and titles.
- `engine overlay dcs-hook status|install|remove [--path <Saved Games\DCS>] [--dry-run]`.
