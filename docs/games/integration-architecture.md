# How games plug into the engine

## Data model (all games)
- A relation (`Relation`, `engine/CoreRelation.cs`) has nodes (`RelationItem`): `Game`, `ID`, `AIRCRAFT` (plane → active).
- Games without planes use the game ID as the plane: `AIRCRAFT[gameId] = true`.
- The bind (`Bind`) holds `Joystick` (DirectInput string `"Name {InstanceGUID}"` or `"Keyboard"`), `JAxis`/`JButton` (`JOY_X/Y/Z/RX/RY/RZ/SLIDER1/SLIDER2`, `JOY_BTN1..128`, `JOY_BTN_POV{n}_{U,UR,R,DR,D,DL,L,UL}`, keyboard key names), `AllReformers` (`name§device§key§sw`), `Inverted`, `Slider`, `Deadzone`, `SaturationX/Y`, `Curvature`, `DoubleTap`.

## IL-2 and Star Citizen (pre-registry)
Each has its own ~2500-line class (`EngineIl2.cs`, `EngineStarCitizen.cs`) plus special cases spread over `Program.cs`, `CoreInternalDataManagement.cs`, `EngineDatabase.cs`, `GamePathDiscovery.cs`, `EngineLayoutRenderer.cs` and the GUI view model.

Known inconsistencies (2026-09-30 audit):
- Star Citizen node IDs exist in three forms: engine `category|id`, load corrections `category___id` (JoyPro 1 format), GUI bare `id`. Nodes added in the GUI are skipped on export.
- The GUI uses plane `"Default"` for IL-2 and Star Citizen nodes, the engine uses the game name.
- Exporters ignore `AIRCRAFT` (disabled nodes are exported too).

## Registry games (`engine/Games/`)
New games implement `IGameIntegration` and register in `GameRegistry`. The engine then provides, without further per-game edits:
- CLI: `engine <game> paths|scan|import|export|refresh-db`, `engine games integrations`, `engine db nodes --game <id>`.
- Profile bridge: collecting binds for export and applying imported bindings (merge by node, then by device+input+modifier signature, else create a relation named after the action).
- Mutating-command detection, path discovery, game-name normalization.
- Device directory: DirectInput instance/product GUIDs, VID/PID and HID product strings of connected devices.
