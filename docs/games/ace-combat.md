# ACE COMBAT on PC

| Title | PC | Input | Devices | Binds | JoyPro |
|---|---|---|---|---|---|
| ACE COMBAT 7: Skies Unknown (2019, Steam 502500) | installed here (2026-10-12) | DirectInput sticks + XInput | **any number**, one section per device model | `%LOCALAPPDATA%\BANDAI NAMCO Entertainment\ACE COMBAT 7\Config\Input.ini` (text) | **native**, `engine ac7` (experimental: file and the game's rewrite checked live, menu/flight not yet) |
| ACE COMBAT 8: Wings of Theve (Oct 2026, Steam 2288340) | installed here, played | XInput 1.4 + Thrustmaster SDK for licensed sticks; no DirectInput | **0** generic joysticks | `%LOCALAPPDATA%\BANDAI NAMCO Entertainment\ACE COMBAT 8\Saved\SaveGames\System.sav` (GVAS + checksum) | **virtual Xbox pad**, `engine ac8` + `engine vjoy mode AceCombat8 xbox` (nothing written) |
| ACE COMBAT Zero (PC port, bundled with AC8) | `ACE COMBAT 8\ACZero\acz.exe` | unknown (own `.arc` engine) | - | - | not researched; try the virtual Xbox pad |
| ACE COMBAT Assault Horizon Enhanced Edition (2013) | delisted | XInput only (WEB) | - | unknown | virtual Xbox pad if needed; not integrated |

AC 04, AC 5 and Infinity never came to PC.

## ACE COMBAT 7 (native)
**Input.ini** (first from published samples; checked on the installed game on 2026-10-12 [OBS], see below):
```
[JoystickSetting]
EnableJoystick=True
EnableDeviceJoystick=True
EnableDeviceFlight=True
EnableDevice1stPerson=True

[Joystick-0402044F-0000-0000-0000-504944564944]
ProductName=Joystick - HOTAS Warthog
Flight_Pitch=Y:R
Flight_Roll=X
Flight_Gun=Button1
Flight_HatSwitchUp=POV_U1
...
```
- **Section name:** "Joystick-" + the DirectInput product GUID (PID high word + VID, then `504944564944` = "PIDVID").
  Two devices of one model share a section; the game cannot tell them apart.
- **Objects:** `X Y Z Rx Ry Rz Slider1 Slider2`, `Button1`-`Button32`, `POV_U/D/L/R1`-`4`.
- **Options:** `:P` positive half, `:N` negative half, `:R` reversed, `:C` −1..1 as 0..1.
- **Combinations:** `&` (and), `!` (unless), `+`, `>`, `<`. An empty value is unbound.
- **Actions:**
  - axes: `Flight_Pitch/Roll/Yaw/Throttle/CameraPitch/CameraYaw`;
  - buttons: `Flight_Gun/Missile/Target/Weapon/Radar/Flare/View/AccelerationDeceleration/AutoPilot/Pause` and
    `Flight_HatSwitchUp/Down/Left/Right`;
  - menu keys `UI_*` (not in JoyPro's catalog).
- **The game averages one action over all devices** (throttle at 100 % + slider at −100 % = 50 %).
- No anti-cheat. Steam Input must be off for the game.
- Flight-stick binds are not remappable in the game; editing Input.ini is the usual way.

**What JoyPro does** (`engine/Games/AceCombat7Integration.cs`):
- `engine ac7 refresh-db`: the `Flight_*` keys from the user's Input.ini, with JoyPro's own titles.
- Export:
  - writes into the section of each device;
  - an axis as `X`/`Y:R` (Invert = `:R`), buttons as `ButtonN`, hat directions as `POV_<U|D|L|R><n>`;
  - only one input per action and device;
  - merge-overwrite / clean: the axis JoyPro writes is cleared on the other device sections, because the game would
    average it (listed in the steps); merge keeps every value the game has;
  - a device without a section is skipped with "start the game once with this device connected";
  - comments, order, encoding and line ends stay.
- Read: simple values per device (by product GUID). Combinations and half axes are reported, not read.
- Limits: buttons 1-32, hats 1-4, four hat directions, no modifiers (the `&` syntax is per device).

**Observed on the installed game (2026-10-12, Steam build, no anti-cheat, no EULA or account screen at the first
start):**
- `Ace7Game.exe` is the only executable (`Game\Content\Paks` holds the data). The first window is a 960x540 splash; the
  title then says "PRESS THE ENTER KEY".
- The game writes `Config\Input.ini` **when it closes** (at start it is an empty file). The first start creates
  `[JoystickSetting]`, a `[JoystickMappingFormat]` block of `HelpComment=` lines (the object/value/mapping syntax and
  the example `Flight_Pitch=Y:R ! Button10`), and one `[Joystick-<product GUID>]` section per connected DirectInput
  model with `ProductName=` (the DirectInput name, quoted when it has outer spaces: `" VKB-Sim Gunfighter Modern Combat
  Pro Twist "`) and **every** `Flight_*` and `UI_*` key, all empty for unlicensed devices. On the dev PC: 7 sections
  (pedals, WinWing panel, vJoy `BEAD1234-...`, VKB, Rhino, VPC throttle, WarBRD); the Wooting keyboard's game
  controller interfaces got none (XInput-capable devices are left to XInput).
- **The values JoyPro writes survive** the game's rewrite on exit (`Slider2:R`, `Y`, `Button11`, `POV_R1` ... read back
  unchanged), so the game reads them in; a value it rejected would be gone.
- `engine ac7 refresh-db` on the real file: the 20 `Flight_*` actions.
- In-game check `tools/e2e/games/AceCombat7Settings.ps1` (RunAll `ac7`), no input to the game: export random vJoy
  binds, start, title, WM_CLOSE, the rewritten file still holds every value, import reads them back (19/19 on
  2026-10-12, `C:\Temp\jp2-e2e\runs\20261012-070310-ac7-settings2`).
- **Still open (needs a session where the kit can click):** whether the controls menu shows stick binds at all, and a
  flight: `:R` on pitch for the user's stick, `:C` for a throttle lever.

## ACE COMBAT 8 (virtual Xbox pad)
- **Input** [SRC, `AceCombat8.exe`]:
  - the exe imports only `XINPUT1_4.dll` (no `dinput8`, no GameInput);
  - flight sticks come through Thrustmaster's SDK (`tm_api_lib_x64.dll`, `FlightStickVendor`, `FlightStickInputIndex`);
  - Bandai lists only the licensed T.Flight HOTAS NEO AC8 Edition and the HORI AC8 stick. WEB: Bandai's "Flight Stick
    Support Update" of 2026-09-30 says some AC7 options are missing.
- **Settings** [OBS]: `System.sav` (GVAS, `/Script/Live.LiveSystemSaveGame`) holds:
  - keyboard keys as `EOptionsKeys::...`;
  - nine gamepad keys as FKey names (`YawLeftKey_Controller` = `Gamepad_LeftShoulder`, `ThrottleUpKey_Controller` =
    `Gamepad_RightTriggerAxis` ...);
  - `ControlType` (Standard/Expert), the flight-stick type, and **a `Checksum`**.
- Easy Anti-Cheat (`start_protected_game.exe`). The Steam Cloud syncs `System.sav`.
- **What JoyPro does** (`engine/Games/AceCombat8Integration.cs`):
  - `engine ac8 refresh-db` reads the gamepad keys from `System.sav` (read only). The catalog holds one `Pad_*` action
    per control with that key in the 5th column. The sticks (pitch/roll left stick, camera right stick) come from
    JoyPro's own table.
  - `engine vjoy mode AceCombat8 xbox`: the planner pins each HOTAS input to the pad control the game uses. A throttle
    relation on `Pad_ThrottleUp` + `Pad_ThrottleDown` becomes the centre-split trigger axis (`XB_TRIGGERS`).
  - The export writes nothing (checksum, EAC) and says so. After changing a gamepad key in the game: `engine ac8
    refresh-db`, then `engine vjoy map`.
- **Not covered:** flares, autopilot, view and the other buttons have no player-mappable gamepad key in the save. They
  sit on fixed pad buttons JoyPro does not know yet. Bind them to the free pad buttons the planner lists, and check in
  the game.
- **Community route** (WEB): vJoy + Joystick Gremlin + HidHide + x360ce. EAC reportedly blocks x360ce's DLL
  injection. JoyPro's ViGEm pad is created outside the game, so nothing is injected.
- The AC8HOTAS mod (UE4SS, `bindings.lua`) gives native multi-device input. It is game modding under EAC (offline
  only), so JoyPro does not generate it.
- **Menu check with the virtual pad (2026-10-11, `tools\e2e\games\AceCombat8Xbox.ps1`, 20/20, run
  `C:\Temp\jp2-e2e\runs\20261011-221446-release\ac8-xbox`):** unattended, menus only (EAC). JoyPro's xbox mode with vJoy
  standing in for the HOTAS: the planner pins Fire machine gun to A, Fire missile to B and Pitch to the left stick Y;
  XInput shows the pad; the pad's A passes "PRESS THE ENTER KEY" (the menus then show pad glyphs, "Confirm"), opens
  SYSTEM > CONTROLS > CONTROLLER, the stick moves the focus, A opens BUTTON MAPPING, B goes back to the main menu, and
  the game is quit with the pad. vJoy and HidHide unchanged, `Saved\SaveGames` restored.
- **Flares and camera on the pad: not found in the menus.** CONTROLLER > BUTTON MAPPING lists only the nine remappable
  actions (Yaw left LB, Yaw right RB, Accelerate RT, Decelerate LT, Fire machine gun A, Fire missile or weapon B,
  Change weapon X, Change target Y, Switch radar map View); CONTROLLER OPTIONS has Control Type, High-G Turn, Target
  Highlight, Pitch Controls, Camera, Camera Sensitivity, Vibration; FLIGHT STICK shows only the T.Flight HOTAS diagram
  (Deploy flares, Change view, Camera on the licensed stick). The pad buttons of flares and view are shown only in a
  mission (pause menu / training), which needs gameplay: **not checked** (EAC, menus-only rule).
- **Still open (needs a flight):** throttle on the triggers, yaw on LB/RB, weapons in the air; the flare and view pad
  buttons.
- Menu facts: a pad created before the game starts may not reach it (as in HLL: Vietnam), so the script creates it once
  the title shows; the title's prompt blinks; a mouse click only selects a menu row; the main menu has ACE COMBAT STORE
  and SYSTEM has AGREEMENTS (never opened); on BUTTON MAPPING the pad's X is "Reset Settings".
