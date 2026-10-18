# Hell Let Loose: Vietnam (virtual Xbox pad game)

## Summary
- **Hell Let Loose: Vietnam (UE 5.5):** has helicopters but no joystick input at all. Its exe has no DirectInput,
  RawInput/HID or GameInput code, only keyboard/mouse and **XInput gamepads**.
- JoyPro therefore flies it through the **virtual Xbox 360 pad** (`engine vjoy mode HLLVietnam xbox`, needs ViGEmBus;
  see docs/virtual-joystick.md). The forwarder reads your HOTAS, feeds the virtual pad, and HidHide hides the real
  devices from the game.
- The game's gamepad layouts are **fixed**. You pick one of three (A, B, C) in Options > Controls, and the gamepad
  keys cannot be rebound. So JoyPro writes nothing for the gamepad. It puts each physical input on the pad control the
  selected layout uses for that action.
- **Keyboard pilot keys** can still be written, and only with `--include-keyboard`.
- **Hell Let Loose (original):** nothing flyable. Its `Input.ini` only has artillery and vehicle-MG pitch/yaw, so JoyPro
  does not support it.

## How it works
1. `engine hllv refresh-db` builds `DB\HLLVietnam\actions.tsv` from the installed game. See the catalog section below.
   Rows are of two kinds:
   - `Pad_*` actions: helicopter gamepad actions. The 5th column holds the default key per layout, e.g.
     `A:Gamepad_LeftY|B:Gamepad_RightY|C:Gamepad_LeftY`.
   - `Driver_*` actions: keyboard pilot keys.
2. Bind your HOTAS to the `Pad_*` actions in JoyPro (relations) as for any game.
3. `engine vjoy mode HLLVietnam xbox` plans the virtual pad:
   - The planner reads `SelectedGamepadLayout=LayoutA|B|C` from
     `%LOCALAPPDATA%\HLLVietnam\Saved\Config\Windows\GameUserSettings.ini`. For tests, `JOYPRO_HLLV_SETTINGS` points
     at another ini.
   - Each action is pinned to its key in that layout (`XboxControls.FromUnrealKey`: `Gamepad_LeftY` → `XB_LY`,
     `Gamepad_FaceButton_Right` → `XB_B`, ...).
   - **Collective:** a relation that holds both `Pad_Helicopter_Ascend` and `Pad_Helicopter_Descend` on one axis becomes
     `XB_TRIGGERS`. Above the axis centre the forwarder pulls RT, below it LT (in layout A, RT ascends). The relation's
     *Invert* swaps the two.
   - *Invert* on any axis relation reverses that pad axis (target suffix `~`).
   - An action without a gamepad key (for example a keyboard action in Xbox mode) gets the next free pad control.
   - What does not fit is listed.
   - Switching the layout in the game and running `engine vjoy map` again moves the pins.
4. `engine hllv export` writes the keyboard keys (with `--include-keyboard`). For the `Pad_*` binds it only says that
   nothing is written for them.

## Default helicopter gamepad layouts (build of 2026-10)
These come from `IMC_Gamepad_ControlScheme_1` (A), `_B` (B), `_C` (C) and the helicopter seat contexts.

| Action (catalog id) | A | B | C |
|---|---|---|---|
| Collective up `Pad_Helicopter_Ascend` | RT | LT | RT |
| Collective down `Pad_Helicopter_Descend` | LT | RT | LT |
| Cyclic pitch `Pad_Helicopter_PitchForward` | left stick Y | right stick Y | left stick Y |
| Cyclic roll `Pad_Helicopter_RollRight` | left stick X | right stick X | left stick X |
| Yaw `Pad_Helicopter_YawRight_Gamepad` | right stick X | left stick X | - |
| Yaw left / right `Pad_Helicopter_YawRotateLeft/Right` | - | - | LB / RB |
| Stability hold `Pad_HelicopterStabilityHold` | B | B | B |
| Auto-level toggle `Pad_HelicopterStabilityToggle` | LS click | LS click | LS click |
| Supply drop `Pad_Helicopter_SupplyDrop` | D-pad right | D-pad right | D-pad right |
| Recon flare `Pad_Helicopter_FireFlare` | D-pad up | D-pad up | D-pad right |
| Look left/right `Pad_DriverLookRightRate` | right stick X | left stick X | right stick X |
| Look up/down `Pad_DriverLookUpRate` | right stick Y | left stick Y | right stick Y |
| Free look (hold) `Pad_EnableSeatFreeLook` | RB | LB | - |
| Next seat `Pad_SwitchSeat_0` | Y | Y | Y |
| Medic `Pad_Helicopter_MedicGamepad` | A | A | A |
| Medic radial `Pad_Helicopter_MedicRadial` | RB | RB | RB |

In A and B, yaw and look left/right share a stick axis. The game decides by context. The planner pins only one physical
axis there and warns about the second one.

## Catalog refresh (`engine hllv refresh-db`)
JoyPro does not ship extracted game data. In Xbox-pad mode only the pad keys of the three layouts matter, and JoyPro
knows them: the catalog is **JoyPro's own table** (the layouts above and the keyboard pilot keys, JoyPro's wording,
action ids and Unreal key names only), unless an Oodle library may be used to read the game's files (see "Oodle").

### Oodle (user decision 2026-10-12: like the device photos, with the user's consent)
The game's input assets are Oodle-compressed. Until 2026-10-11 the refresh silently decompressed them with an
`oo2core_*_win64.dll` it found next to *another* installed game (on a test PC: Battlefield 6). Now:

1. **The game's own library** (an `oo2core_*_win64.dll` inside Hell Let Loose: Vietnam's folder) is used without a
   question. The build of 2026-10 ships none (Oodle is linked into the game's executable; checked 2026-10-12: no
   `oo2core*` or `*oodle*` file in the install).
2. **Another installed game's library only with the user's yes.** `engine hllv oodle` names it (the other game's folder
   and the file, found in the same Steam library). When the GUI builds or updates the HLL Vietnam list it asks once:
   JoyPro found an Oodle library in another game installed on this PC (game, file); it can use it on this PC only to
   read Hell Let Loose: Vietnam's files; the library belongs to its maker (RAD Game Tools / Epic Games) and is licensed
   with that other game; by agreeing the user confirms the use is private and for themselves only; JoyPro never copies
   or ships it. Yes / No; the answer is remembered for that file (UI settings), Settings > Games changes it ("use the
   Oodle library of another installed game", "Ask again"). Without a window to ask in nothing is assumed.
3. With the yes the GUI passes `refresh-db --oodle-dll <path>`: the engine checks the name (`oo2core_<n>_win64.dll`),
   loads it **from where it is** (never copied), only in the refresh process, and logs it (steps and `JoyProLog`).
   Without it (no yes, no file, or `JOYPRO_OODLE_DLL=none` in tests) nothing is loaded.
4. **Fallback: JoyPro's own table** (the pad keys of the three layouts and the pilot keys). In Xbox-pad mode only the
   pad keys matter, so the fallback is complete for that mode. Fresh build on a test PC without the yes: 28 actions,
   the same rows the game files gave with the borrowed library (the two medic pad actions were added to the table).

Tests: `smoke\hllv.ps1` (a synthetic Steam library with a stand-in library: no load without `--oodle-dll`, a load
attempt with it, the fallback either way, the game's own library first) and the GUI test
`HllvOodle_NoLoadWithoutConsent_LoadAfterYes_AndOwnLibraryNeedsNoQuestion`.

The reader (with the game's own library or the allowed one):

1. Open `<install>\HLLVietnam\Content\Paks\HLLVietnam-Windows.utoc/.ucas` (IoStore TOC version 8) with
   `UnrealIoStore`. It reads the directory index, decompresses the Oodle blocks and reads the Zen packages with
   unversioned properties.
2. Take the Enhanced Input assets under `/Content/_WFL/Input/`:
   - the mapping contexts `IMC_Gamepad_ControlScheme_1/_B/_C` and `IMC_Helicopter*`;
   - their `FEnhancedActionKeyMapping` entries: Action, Key and PlayerMappableKeySettings name;
   - the input actions' `ValueType` (axis or button).
3. Build the rows:
   - `<Action>_Keyboard` mapping names become `Driver_*` keyboard rows;
   - gamepad keys of helicopter actions (plus driver look, free look, seat switch) become `Pad_*` rows, with the key
     per layout. Keys from the helicopter contexts apply to every layout (`*:`).
4. **Oodle:** the game compresses with Oodle Kraken. JoyPro does not ship Oodle; it uses the game's own
   `oo2core_*_win64.dll`, or another game's only with the user's yes (see above), loaded only in the engine process and
   only for the refresh.
5. Without one, JoyPro writes its built-in table of the layouts above (build of 2026-10) and says so in the steps.

Measured on a test PC on 2026-10-11 with the borrowed DLL: 313 mappings and 28 actions in 0.26 s.

## Keyboard file
`%LOCALAPPDATA%\HLLVietnam\Saved\SaveGames\EnhancedInputUserSettings.sav` (UE GVAS). Only changed mappings are stored.

```
"GVAS" | i32 SaveGameVersion=3 | i32 UE4Ver=522 | i32 UE5Ver=1013
u16 5,u16 5,u16 4 | u32 CL=0 | FString "UE5"
i32 CustomVersionFormat=3 | i32 N | N x (GUID16, i32)
FString "/Script/EnhancedInput.EnhancedInputUserSettings"
UObject: tagged properties (CurrentProfileIdentifier ...), "None", i32 HasObjectGuid=0
EnhancedInput tail: i32 Version=1 ; i32 NumProfiles ; per profile:
  FString ProfileKey ; FString "/Script/EnhancedInput.EnhancedPlayerMappableKeyProfile" ; FString object name ;
  i32 NumMappings ; per mapping:
    FString MappingName, FString CurrentKey (FKey name), FString HW.InputClassName, FString HW.DeviceIdentifier,
    u8 + i32 (0,0), u8 Slot
FString "ObjectEnd"
```

What JoyPro writes:
- Only the `<Action>_Keyboard` mappings of the `Driver_*` pilot actions, and only with `--include-keyboard`. The
  mapping list is rewritten on its own; every other byte stays identical (`GvasKeyMappings`).
- Key names are translated to Unreal FKey names (`Space` → `SpaceBar`, `D1` → `One`, ...).
- `merge`, `merge-overwrite` and `clean` work as in the other games. Ownership is kept in
  `EnhancedInputUserSettings.sav.joypro.json`.

The game only lists keyboard and mouse keys in its key selector, and gamepad keys are not player-mappable. That is why
JoyPro does not write gamepad binds (`IGameGamepadBinds` is not implemented).

## Menu check with the virtual pad (2026-10-11)
Menus only: JoyPro's xbox mode with vJoy standing in for the HOTAS (relations on
`Pad_*` actions, `vjoy mode HLLVietnam xbox`, `vjoy map`, `forwarder-config`, `JoyPro.VJoy run --xbox-sink vigem`).
Checked: the planner pins layout A (Medic = A, Stability hold = B, Supply drop = D-pad right, cyclic pitch = left
stick Y), XInput shows the pad and the forwarded stick, the pad's A passes "PRESS ANY BUTTON TO CONTINUE", B opens QUIT
GAME, D-pad right moves the focus from ACCEPT to DISMISS (the orange focus box), A on DISMISS closes it, and B, A quit
the game. 19/19 on 2026-10-11 (run `C:\Temp\jp2-e2e\runs\20261011-214410-release\hllv-xbox`). vJoy and HidHide were
unchanged afterwards, the save folders restored.

Facts: a pad created **before** the game starts never reached the game in the first run (Steam Input or the game's
startup probably takes pads present at launch); the script creates the pad once the title shows, which works. With a
pad the main menu shows "B EXIT"; the main menu has "BROWSE ALL AVAILABLE DLC" (a store link: the script never presses
A there).

## Live check with the user (open)
- Layout A with the virtual pad: collective on the triggers, cyclic, yaw, stability (B), supply drop.
- Whether the right-stick yaw/look overlap in layout A disturbs flying.

Research: real files and IoStore containers on a test PC (read-only), worldeka HLLV flight stick guide,
hllvtools.com/helicopter-controls.
