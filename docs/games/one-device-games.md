# One-device and XInput-only games (virtual-only class)

Some games take only **one** joystick (Battlefield) or only **Xbox controllers / XInput** (Hell Let Loose: Vietnam, ACE
COMBAT 8). A HOTAS of several devices then works only through JoyPro's virtual mode (docs/virtual-joystick.md):

- **vJoy target** (DirectInput, one virtual joystick): for games that read one DirectInput device. JoyPro merges the
  inputs the game's relations use onto one vJoy device, exports the binds to it, and HidHide hides the real devices
  from the game.
- **Xbox pad target** (XInput, one virtual Xbox 360 pad through ViGEmBus): for games that only read XInput. It has two
  sticks, two triggers, 14 buttons and the D-pad. JoyPro follows the game's own gamepad layout where it knows it,
  gives the rest in order, and lists what does not fit.

The engine marks these games with `IGameDeviceLimit` (`engine/Games/GameIntegration.cs`):

| Field | What it does |
|---|---|
| `MaxJoysticks` | Joysticks the game reads; 0 = XInput only |
| `SuggestedMode` | `virtual` or `xbox` |
| `DeviceLimitNote` | One sentence for the user |
| `AvoidVJoyAxes` | vJoy axes the game must not see |

The marking is used in four places:
- `engine vjoy status` shows "one-device game: <mode> suggested" for such a game in native mode.
- `engine vjoy mode <game> native` warns.
- The Virtual Joystick page shows the suggestion with a "Use it" button.
- An export in native mode warns when the binds use more joysticks than the game reads.

Research of 2026-10-11 (read-only on the user's PC and the web). Tags: OBS = seen on this PC, SRC = strings in the
game's exe, WEB = web source, INF = inferred.

## Survey
| Game | Installed here | Input API | Devices | Bind file + format | Anti-cheat | JoyPro strategy | Implemented |
|---|---|---|---|---|---|---|---|
| Hell Let Loose: Vietnam | yes | XInput 1.4 + RawInput, no DirectInput (SRC) | 0 joysticks | `%LOCALAPPDATA%\HLLVietnam\Saved\SaveGames\EnhancedInputUserSettings.sav` (GVAS; pad layouts fixed) | EAC | Xbox pad, the game's layout A/B/C ([hll-vietnam.md](hll-vietnam.md)) | yes (`engine hllv`) |
| Battlefield 6 | yes | dinput8 + GameInput + HID + XInput (SRC) | 1; a stick with Rx/Ry counts as an Xbox pad (WEB) | `Documents\Battlefield 6\settings\steam\PROFSAVEbf6mp_profile` + `PROFSAVE_profile`, `GstKeyBinding.<cat>.<Concept>.<slot>.<prop> <int>` (OBS) | EA Javelin | vJoy without Rx/Ry ([battlefield.md](battlefield.md)) | yes (`engine bf6`), joystick writes after the live survey |
| Battlefield 2042 | no (stub folder) | as BF6 | 1 | `Documents\Battlefield 2042\settings\PROFSAVE_profile` (OBS, older copy) | EA anti-cheat | vJoy without Rx/Ry | yes (`engine bf2042`), same survey (`-Game bf2042`) |
| Battlefield V | yes (EA app) | DirectInput (SRC) | 1 (`.device` always 0) | `Documents\Battlefield V\settings\PROFSAVE_profile_synced` (OBS) | EA anti-cheat | vJoy | no: server-synced profile; own enum (none = 24/64) |
| Battlefield 1 / 4 / Hardline / 3 / 2 | no | DirectInput | 1 | `Documents\Battlefield <n>\settings\PROFSAVE_profile`; BF2 `Controls.con` | EAAC (BF1), PunkBuster/FairFight | vJoy | no ([battlefield.md](battlefield.md)) |
| Rising Storm 2: Vietnam | no | XInput only (`XboxTypeS_*`), no joystick (WEB) | 0 | `Documents\My Games\Rising Storm 2\ROGame\Config\ROInput.ini` (UE3 `Bindings=`) | EAC | Xbox pad + pad binds in ROInput.ini ([rising-storm-2.md](rising-storm-2.md)) | no (not installed) |
| ACE COMBAT 7 | yes (2026-10-12) | DirectInput + XInput (OBS: a section per DirectInput model) | any number, one section per model; the game averages an action over devices | `%LOCALAPPDATA%\BANDAI NAMCO Entertainment\ACE COMBAT 7\Config\Input.ini` (text) | none | **native** ([ace-combat.md](ace-combat.md)) | yes (`engine ac7`, from published samples) |
| ACE COMBAT 8 | yes, played | XInput 1.4 + Thrustmaster SDK, no DirectInput (SRC) | 0 generic sticks | `...\ACE COMBAT 8\Saved\SaveGames\System.sav` (GVAS + checksum) | EAC | Xbox pad on the game's pad keys (read from System.sav) | yes (`engine ac8`, nothing written) |
| ACE COMBAT Assault Horizon EE / Zero | no / bundled with AC8 | XInput (WEB) / unknown | - | unknown | - | Xbox pad | no |
| STAR WARS: Squadrons | no | DirectInput + XInput | 3-4 by name, unreliable (WEB) | `Documents\STAR WARS Squadrons Steam\settings\ProfileOptions_profile_synced` (Frostbite text + `deviceid`) | EAC | vJoy, one device ([star-wars-squadrons.md](star-wars-squadrons.md)) | no (not installed) |
| Flyout | no | Unity, joysticks merged (INF) | effectively 1 (WEB) | unknown (LocalLow / PlayerPrefs, INF) | none | vJoy, one device ([flyout.md](flyout.md)) | no |
| Kerbal Space Program 1 | no | Unity Input Manager | several (PRIMARY/SECONDARY per axis) | `<install>\settings.cfg` (ConfigNode text) | none | native candidate; vJoy against index shifts ([kerbal-space-program.md](kerbal-space-program.md)) | no (needs a real file) |
| Kerbal Space Program 2 | no | Unity Input System | broken (WEB) | `LocalLow\Intercept Games\...\InputBindings.json` | none | Xbox pad or a mod | no |
| G-Rebels | yes, started 2026-10-11 | XInput + RawInput (EnhancedRawInput plugin: 24 axes, 96 buttons) (SRC) | several, each switched on in the game | `%LOCALAPPDATA%\G_Rebels\Saved\SaveGames\SaveOptions.sav` (GVAS) | none | native (a channel per device in the game); vJoy against shared keys ([g-rebels.md](g-rebels.md)) | yes (`engine grebels`) |
| No Man's Sky | no | Steam Input / XInput, no flight stick (WEB) | 0 | keyboard `Binaries\SETTINGS\TKGAMESETTINGS.MXML`; pad = Steam Input | none | Xbox pad ([no-mans-sky.md](no-mans-sky.md)) | no |
| Tiny Combat Arena | yes, in game 2026-10-11 | Unity + Rewired (SRC) | several (Rewired Control Mapper) | registry `HKCU\Software\TinyCombat\Arena` (Unity PlayerPrefs, OBS) | none | **native** like Nuclear Option ([tiny-combat-arena.md](tiny-combat-arena.md)) | yes (`engine tca`) |
| H.A.W.X / H.A.W.X 2 | H.A.W.X 2 yes, not started (needs Ubisoft Connect + login) | DirectInput + XInput | **1** (WEB) | profile through Ubisoft Connect save games (SRC); `Data_PC\Settings\settings.ini` dead zone | none (Ubisoft Connect) | vJoy, bind in game ([hawx.md](hawx.md)) | no (binary) |
| Project Wingman | yes | UE4 JoystickPlugin (SDL2, DirectInput, XInput ignored) (SRC/OBS) | several (OBS: all 12 listed) | `%LOCALAPPDATA%\ProjectWingman\Saved\Config\WindowsNoEditor\Input.ini` (`Joystick_<DI name>_<place in the reversed DI list>_Axis<n>/_Button<n>`), curves in `savegame.sav` | none | **native** ([project-wingman.md](project-wingman.md)) | yes (`engine pw`) |
| Hell Let Loose (original) | yes | no (SRC) | - | `Input.ini` | EAC | nothing flyable: excluded | - |
| Squad | yes | HID via RawInput + GameInput (SRC) | one merged device; v10.6 beta adds multi-device HOTAS (WEB) | `%LOCALAPPDATA%\SquadGame\Saved\Config\...\Input.ini` | EAC | wait for 10.6 | - |
| GTA V | - | - | - | - | - | not researched | - |

FSX: not on this PC as a Steam version, left out.

### What needs the user (live)
- **Battlefield 6** (and 2042 when installed): the joystick survey, about 5 minutes. See [battlefield.md](battlefield.md#live-survey-finishes-the-integration).
- **ACE COMBAT 8:** one flight with the virtual Xbox pad (ViGEmBus installed); find the pad buttons of flares and view.
- Tiny Combat Arena and G-Rebels were started and bound by the e2e kit on 2026-10-11 (see their pages).
- **KSP 1** (when installed): one real file with known binds, read-only. (Project Wingman and ACE COMBAT 7: done
  2026-10-12; AC7's controls menu and a flight are still open.)
- **H.A.W.X 2:** install Ubisoft Connect, log in, start the game once ([hawx.md](hawx.md)).

### Checked and not one-device (2026-10-11)

These read several joysticks at once, so they need no virtual mode (the device count is also the `MaxJoysticks`
property of their integration, to be unified with the one-device marker):

| Game | Joysticks at once | How a device is named in the file | Source |
|---|---|---|---|
| IL-2 Sturmovik: 1946 | 8 (4.13+ code; 4 in 4.12 docs) | index only: `JoystickDevice0..7` (DirectInput order) | [il2-1946.md](il2-1946.md) |
| Cliffs of Dover Blitz / original | 16 since Blitz 5.026 (8 before) / 8 | Blitz `<name>-<id>`, original `Joystick`, `Joystick1` .. | [il2-cliffs-of-dover.md](il2-cliffs-of-dover.md) |
| FSX, FSX-SE, Prepar3D v4-v6 | no documented limit (one map per device) | `<instance name>{<instance GUID>}` | [fsx-p3d.md](fsx-p3d.md) |

## Template: adding a one-device game

1. **Detection**: Steam app id / registry key, process names (for the watcher, auto start and HidHide's block list),
   the settings path(s), Steam Cloud (`steam_autocloud.vdf`) or server-synced variants.
2. **Input capability**: DirectInput yes/no, how many devices, the limits (buttons, hats, axes), whether a device with
   Rx/Ry is taken for a gamepad; XInput yes/no; GameInput.
3. **Target**: vJoy (DirectInput binds writable), Xbox pad (XInput only; use the game's gamepad layout), or document only.
   Implement `IGameDeviceLimit` (MaxJoysticks, SuggestedMode, DeviceLimitNote, AvoidVJoyAxes) so status, GUI and export
   suggest the virtual mode.
4. **Catalog** (`DB\<Game>\actions.tsv`, built by `engine <game> refresh-db` from the game's own files): the flight
   actions; for an Xbox game the 5th column holds the default gamepad key as an Unreal FKey name
   (`Gamepad_LeftX`, `Gamepad_FaceButton_Bottom`, `Gamepad_LeftStick_Up` ...), which pins the action to that control
   (`XboxControls.FromUnrealKey`).
5. **Writer** (only when the file is understood): keep unknown lines, the game's type codes and unbound markers, write
   only while the game is closed, back up first (`GameExportSafety`), write every profile variant, check after one launch
   that the game or a cloud copy did not undo it. Games that store gamepad binds implement `IGameGamepadBinds` and get
   their binds on `EngineVJoy.XboxDeviceName` with `XB_*` inputs in Xbox mode.
6. **Virtual mode**: `engine vjoy mode <game> virtual|xbox`; the planner places the inputs and lists what does not fit;
   HidHide hides the real devices from the game only.
7. **Anti-cheat note** per game (Javelin, EAC, BattlEye): JoyPro only uses signed virtual drivers (vJoy, ViGEmBus) and
   HidHide, no injection.
8. **Re-import**: the virtual binds map back to the physical inputs through the stored table.
