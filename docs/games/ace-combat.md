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
- Before the game wrote Input.ini (and before a refresh), the catalog, search and relation editor use JoyPro's own table of the 20 `Flight_*` actions (`IGameBuiltInCatalog`); ACE COMBAT 8 uses its table of the default gamepad layout the same way. A refresh, import or export builds the real list.
- Export:
  - writes into the section of each device;
  - an axis as `X`/`Y:R` (Invert = `:R`), buttons as `ButtonN`, hat directions as `POV_<U|D|L|R><n>`;
  - only one input per action and device;
  - add never removes or overwrites: an action that already has a value on that device keeps it, and a bind on an
    input the game uses for another action is not written (listed as a skipped multi bind);
  - merge writes the profile's value (one slot per action and device; a combination JoyPro does not read stays),
    releases another action on the same input and keeps the action's values on other devices (an axis the game
    averages is reported);
  - merge-overwrite also clears every action the profile binds on the other connected devices (the game would average
    an axis; listed in the steps); clean keeps only the profile's readable values in the sections it writes (and still
    clears a written axis on the other connected devices); nuke clears the readable values of the other connected
    devices. A section of a device that is not connected, menu keys (`UI_*`) and combinations JoyPro does not read
    are never cleared;
  - a device without a section is skipped with "start the game once with this device connected";
  - comments, order, encoding and line ends stay.
- Read: simple values per device (by product GUID). Combinations and half axes are reported, not read.
- Limits: buttons 1-32, hats 1-4, four hat directions, no modifiers (the `&` syntax is per device).

**Observed on the installed game (2026-10-12, Steam build, no anti-cheat, no account screen at the first
start):**
- `Ace7Game.exe` is the only executable (`Game\Content\Paks` holds the data). The first window is a 960x540 splash; the
  title then says "PRESS THE ENTER KEY".
- The game writes `Config\Input.ini` **when it closes** (at start it is an empty file). The first start creates
  `[JoystickSetting]`, a `[JoystickMappingFormat]` block of `HelpComment=` lines (the object/value/mapping syntax and
  the example `Flight_Pitch=Y:R ! Button10`), and one `[Joystick-<product GUID>]` section per connected DirectInput
  model with `ProductName=` (the DirectInput name, quoted when it has outer spaces: `" VKB-Sim Gunfighter Modern Combat
  Pro Twist "`) and **every** `Flight_*` and `UI_*` key, all empty for unlicensed devices. For example 7 sections
  (pedals, WinWing panel, vJoy `BEAD1234-...`, VKB, Rhino, VPC throttle, WarBRD); the Wooting keyboard's game
  controller interfaces got none (XInput-capable devices are left to XInput).
- **The values JoyPro writes survive** the game's rewrite on exit (`Slider2:R`, `Y`, `Button11`, `POV_R1` ... read back
  unchanged), so the game reads them in; a value it rejected would be gone.
- `engine ac7 refresh-db` on the real file: the 20 `Flight_*` actions.
- In-game check `tools/e2e/games/AceCombat7Settings.ps1` (RunAll `ac7`), no input to the game: export random vJoy
  binds, start, title, WM_CLOSE, the rewritten file still holds every value, import reads them back (19/19 on
  2026-10-12).
- **Not checked yet:** whether the controls menu shows stick binds at all, and a
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
- **On the pad by itself (2.7.0):** the game reads no generic joystick, so without a mode chosen it uses the virtual
  Xbox pad (the user's choice wins). The relation's axis curve goes to the pad in JoyPro's forwarder (axis model
  `xboxpad`; the game gets no export).
- **Auto setup from the live test of 2026-11-14** (docs/virtual-joystick.md "Auto setup on the Xbox pad"): the throttle
  splits both triggers (forward RT, back LT) even when Decelerate sits in a toe brake relation; yaw on LB/RB comes from
  the stick twist (else the rudder pedals) through "Axis ends to buttons" with an end zone of 0.35; the D-pad (wingman
  orders) from the comm switch or AI-menu hat; a hat direction bound to Change target drives Y; the "Cobra button" (DCS ASC
  Direct Control) or a button relation named "High-G" holds both triggers (High-G turn) on top of the throttle.
- **Pitch and invert on the pad:** DCS's invert of a relation never reaches the pad. `Pad_Pitch` is reversed by default
  because with the game's default "Pitch Controls" the pad stick pushed up pitches the nose up (checked in game, 2026-11-14):
  a HOTAS pushed forward gives nose down. With "Pitch Controls: Inverted" in the game, untick Invert for ACE COMBAT 8
  in Axis response (Applies to: ACE COMBAT 8 only). The throttle on the triggers follows the lever (forward = RT).
- **Look around (right stick) on a hat:** with no axis on Look Horizontal / Look Vertical (`Pad_CameraYaw`,
  `Pad_CameraPitch`), the pad's auto setup puts the right stick on a free hat of the user's stick ("Hat to axes": a
  direction holds the stick fully out while held). On a VKB Gunfighter MCG Pro that is POV1, the top ministick that
  trims in DCS (docs/virtual-joystick.md "Auto setup on the Xbox pad", rule 7). pad-check reports it as `look`.
- **Roll direction (in-game check 2026-11-14: rolling left/right came out inverted):** JoyPro's chain is straight (DirectInput
  X right -> XInput LX +32767, `XboxPadForwarderTests.SignChain_*`); nothing in JoyPro reverses roll. A reversal comes
  from the game side (its pad roll convention or a setting); the fix is a game-scope invert on the Roll relation for
  ACE COMBAT 8 only (`binds tuning set Roll --scope game:AceCombat8 --inverted true`), never DCS's base.
- **Flares = L3 + R3 pressed together: NOT CONFIRMED.** Not checked in game yet
  (`AceCombat8Integration.PadChords`, `Confirmed = false`). JoyPro maps it from the flare / CMS forward relation as a
  120 ms chord (a macro with "pressed together") and says "not confirmed in game yet"; change it here once it is checked.
- **Setup wizard and Auto Group (2.7.0):** `DB/Functions/AceCombat8.tsv` has every flight control: pitch / roll
  (`Pad_Pitch`, `Pad_Roll`), yaw (the rudder function with `Pad_YawLeft` + `Pad_YawRight`: the twist or the pedals, the
  pad's auto setup presses LB / RB at the axis ends), the throttle (`Pad_ThrottleUp` + `Pad_ThrottleDown`: the centre
  split), gun, missile (weapon release), target (TMS right), change weapon, radar map, camera, flares (CMS forward,
  `Pad_Flares`: the L3 + R3 chord), High-G (the FBW disconnect / Cobra button, `Pad_HighGTurn`) and the wingman D-pad
  from the comm switch (`Pad_WingmanUp/Down/Left/Right` on the D-pad). `Pad_Flares` and `Pad_HighGTurn` have no pad
  key of their own: the chord and the hold read the relation's button. The relations take the game's names (Radar map,
  Change weapon, Fire gun ...), not the DCS slots. ACE COMBAT 7's table has its flight axes, missile and flares too.
- **Not covered:** autopilot, view and the other buttons have no player-mappable gamepad key in the save. They sit on
  fixed pad buttons JoyPro does not know yet. Bind them to the free pad buttons the planner lists, and check in the
  game.
- **Community route** (WEB): vJoy + Joystick Gremlin + HidHide + x360ce. EAC reportedly blocks x360ce's DLL
  injection. JoyPro's ViGEm pad is created outside the game, so nothing is injected.
- The AC8HOTAS mod (UE4SS, `bindings.lua`) gives native multi-device input. JoyPro does not generate it.
- **Menu check with the virtual pad (2026-10-11, `tools\e2e\games\AceCombat8Xbox.ps1`, 20/20):** unattended, menus
  only. JoyPro's xbox mode with vJoy
  standing in for the HOTAS: the planner pins Fire machine gun to A, Fire missile to B and Pitch to the left stick Y;
  XInput shows the pad; the pad's A passes "PRESS THE ENTER KEY" (the menus then show pad glyphs, "Confirm"), opens
  SYSTEM > CONTROLS > CONTROLLER, the stick moves the focus, A opens BUTTON MAPPING, B goes back to the main menu, and
  the game is quit with the pad. vJoy and HidHide unchanged, `Saved\SaveGames` restored.
- **Flares and camera on the pad: not found in the menus.** CONTROLLER > BUTTON MAPPING lists only the nine remappable
  actions (Yaw left LB, Yaw right RB, Accelerate RT, Decelerate LT, Fire machine gun A, Fire missile or weapon B,
  Change weapon X, Change target Y, Switch radar map View); CONTROLLER OPTIONS has Control Type, High-G Turn, Target
  Highlight, Pitch Controls, Camera, Camera Sensitivity, Vibration; FLIGHT STICK shows only the T.Flight HOTAS diagram
  (Deploy flares, Change view, Camera on the licensed stick). The pad buttons of flares and view are shown only in a
  mission (pause menu / training), which needs gameplay: **not checked**.
- **Not checked in flight yet:** weapons in the air; the flare chord (L3 + R3, see above) and the view pad buttons.
  A flight with throttle on the triggers and yaw on LB/RB works.
- Menu facts: a pad created before the game starts may not reach it (as in HLL: Vietnam), so the script creates it once
  the title shows; the title's prompt blinks; a mouse click only selects a menu row; the main menu has ACE COMBAT STORE
  and SYSTEM has AGREEMENTS (never opened); on BUTTON MAPPING the pad's X is "Reset Settings".
