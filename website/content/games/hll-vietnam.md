---
title: Hell Let Loose: Vietnam
status: full
order: 30
category: arcade
support: HLLVietnam,doc:HellLetLoose,doc:Squad
docs: hll-vietnam.md, one-device-games.md
summary: Hell Let Loose: Vietnam with JoyPro: fly its helicopters with your HOTAS through a virtual Xbox pad that follows the game's pad layout A, B or C, plus the keyboard pilot keys; and why Hell Let Loose and Squad are documented only.
---
Hell Let Loose: Vietnam is an infantry shooter on Unreal Engine 5 with flyable helicopters, but it reads no joystick
at all: only keyboard, mouse and Xbox pads. Its pad layouts are fixed (you pick A, B or C in the game). So JoyPro flies
it through a **virtual Xbox pad**: your stick, throttle and pedals drive the pad controls that the chosen layout uses
for each helicopter action. JoyPro writes nothing for the pad; it only writes the keyboard pilot keys, when you ask
for them.

:::video game-hll-vietnam "Hell Let Loose: Vietnam: import, relations, export"
:::

{{game-card}}

```mermaid
flowchart LR
  L["GameUserSettings.ini<br/>layout A / B / C"] --> T["Pad table<br/>(follows the layout)"]
  R["Stick, throttle, pedals"] --> F["JoyPro.VJoy forwarder"]
  T --> F
  F --> V["Virtual Xbox 360 pad<br/>(ViGEmBus)"]
  V --> G["Hell Let Loose: Vietnam"]
  H["HidHide"] -.->|hides the real devices<br/>from the game only| G
```

:::layer beginner
## What JoyPro does for Hell Let Loose: Vietnam

- **Helicopter on the pad**: JoyPro knows the helicopter actions of the three pad layouts (collective, cyclic, yaw,
  stability hold, auto-level, supply drop, recon flare, look, seat, medic). Bind your HOTAS to them, and the virtual
  pad moves the stick, trigger or button the game expects.
- **Keyboard pilot keys**: on request, JoyPro reads and writes the game's keyboard keys of the pilot actions.
- **Hell Let Loose** (the original) has nothing to fly, and **Squad** reads one merged device: both are documented
  only (see Advanced).

## Import

1. Open **Start**. If the card asks for a command list, press **Build List**: JoyPro writes its own table of the
   three pad layouts and the pilot keys.
2. Press **Import** on the Hell Let Loose: Vietnam card. Tick **Also take the keyboard keys** if you want the pilot
   keys; there are no joystick binds in the game to import.
3. Press **Import now**.

![The Start page with your games](shot:start)

## Set up the virtual Xbox pad

1. Open the [[gui:vjoy|Virtual Joystick]] page. Under **What this page needs**, install **ViGEmBus** and **HidHide**
   if they are missing.
2. Under **Games**, switch Hell Let Loose: Vietnam to **Xbox pad** (JoyPro suggests it with **Use it**).
3. On the **Relations** page, bind your HOTAS to the helicopter pad actions. For the collective, put one throttle axis
   on both **Collective up** and **Collective down**: JoyPro splits it at the centre onto the two triggers.
4. Press **Start**, then play. **Stop** when you are done.

![The Virtual Joystick page](shot:vjoy)

## Build and check relations

The [[page:flows/relations.html|relations flow]] works as for every game. A relation can hold the helicopter action
of this game and the same function of your flight sims, so "Collective" is one throttle lever everywhere.

## Export

The pad needs no export: **Start** on the Virtual Joystick page builds the pad table from your relations. An export
writes only the keyboard pilot keys, and only when you ticked the keyboard at the import (the choice is kept per game):
**close the game**, open **Export**, press **Export now** on its tile.

:::ingame What you see in Hell Let Loose: Vietnam
The game sees one Xbox controller: its menus show pad buttons (for example "B EXIT" in the main menu). Your stick,
throttle and pedals fly the helicopter as the pad of the layout you picked would. Keyboard pilot keys JoyPro wrote
show in the game's key settings.
:::

:::picture ingame-hll-vietnam-controls "Hell Let Loose: Vietnam: the controls menu with the exported binds"
:::

## What you do yourself in Hell Let Loose: Vietnam

- **Pick the pad layout** (A, B or C) in **Options > Controls**. After a change, press **Update mapping** on the Virtual
  Joystick page (or Stop and Start): the pad table follows the new layout.
- **The game's pad buttons cannot be rebound**, so the layout decides which pad control each action has. JoyPro
  follows it; there is nothing to set in the game for the pad.
- If the game does not react to the pad, press **Stop** and **Start** once the title screen shows: in JoyPro's tests a
  pad created before the game had started did not reach it.

## Common pitfalls

- **Yaw and look move together** in layouts A and B: the game puts yaw and look left / right on the same stick axis
  and decides by context. JoyPro pins one physical axis there and warns about the second.
- **The collective goes the wrong way**: tick *Invert* on the collective relation; it swaps the two triggers.
- **A pilot key was not written**: keyboard keys are written only with the keyboard choice of the import.
:::

:::layer advanced
## Files JoyPro reads and writes

| File | What it is |
|---|---|
| `%LOCALAPPDATA%\HLLVietnam\Saved\Config\Windows\GameUserSettings.ini` | read only: `SelectedGamepadLayout` (`LayoutA`, `LayoutB` or `LayoutC`) |
| `%LOCALAPPDATA%\HLLVietnam\Saved\SaveGames\EnhancedInputUserSettings.sav` | the keyboard keys you changed; JoyPro writes the pilot keys there (with the keyboard option) |
| `...\EnhancedInputUserSettings.sav.joypro-meta.json` | JoyPro's data of the exported keys for the next import |

## The helicopter pad layouts

| Action | A | B | C |
|---|---|---|---|
| Collective up / down | RT / LT | LT / RT | RT / LT |
| Cyclic pitch | left stick Y | right stick Y | left stick Y |
| Cyclic roll | left stick X | right stick X | left stick X |
| Yaw | right stick X | left stick X | LB / RB |
| Stability hold | B | B | B |
| Auto-level toggle | LS click | LS click | LS click |
| Supply drop | D-pad right | D-pad right | D-pad right |
| Recon flare | D-pad up | D-pad up | D-pad right |
| Look left / right, up / down | right stick | left stick | right stick |
| Free look (hold) | RB | LB | - |
| Next seat | Y | Y | Y |
| Medic / medic radial | A / RB | A / RB | A / RB |

## The export modes (keyboard pilot keys)

One key per pilot action. A mouse button on a pilot action always stays; infantry and common keys never change.

| Mode | What happens |
|---|---|
| **Only add** | The game's key stays; a key another pilot action has is skipped and marked "Not written to the game". |
| **Update, keep the game's binds** / **Update, my profile wins** | The profile's key is set; another pilot action on that key loses it (the game then uses its default there). |
| **Replace on my devices** | The other pilot keys you changed in the game go back to the game's defaults. |
| **+ Also clear my other devices** | Nothing to clear: the game stores no joystick binds. |

## What is never touched

- The game's pad layouts: they are fixed and JoyPro writes no pad binds.
- Infantry and vehicle keys, mouse buttons, every other byte of the save file.
- HidHide hides your real devices only from this game, only while the virtual pad runs; **Stop** gives HidHide back
  exactly its previous setup.

## Pad, curves, other settings

- **Invert** on an axis relation reverses that pad axis in JoyPro's forwarder. Curves and dead zones are the game's
  own pad settings.
- An action without a pad key in the layout gets the next free pad control; what does not fit is listed on the
  Virtual Joystick page. [[doc:virtual-joystick|Virtual functions]] work on the pad too.
- **Settings > Games: Hell Let Loose: Vietnam: use the Oodle library of another installed game**: the game's files are
  compressed and it ships no library to read them. With your yes JoyPro uses one that another game on this PC has,
  where it is (never copied), to build the list from the game's own files. Off: JoyPro's own table, which is all the
  pad mode needs. **Ask again** forgets your answer.
- **Watcher**: the keyboard save file is watched after the game closes ([[doc:sync-watcher|the watcher]]).
- **HUD**: while the game runs, the [[gui:hud|HUD]] names the action of each pressed button.

## Hell Let Loose and Squad

| Game | What is known | JoyPro |
|---|---|---|
| **Hell Let Loose** (original) | Its `Input.ini` has only artillery and vehicle machine gun pitch / yaw; nothing flyable. | Not planned. |
| **Squad** | Reads one merged HID device; a beta version adds several HOTAS devices. Binds in `%LOCALAPPDATA%\SquadGame\Saved\Config\...\Input.ini`. | Waiting for the multi-device version; no integration. |
:::

:::layer technical
## Inside the Hell Let Loose: Vietnam integration

Code: `engine\Games\HllVietnamIntegration.cs`, `engine\Games\GvasKeyMappings.cs`; the pad planner and forwarder of
[[doc:virtual-joystick]].

- **Input**: the game's executable has no DirectInput, raw input or GameInput code, only keyboard, mouse and XInput.
  It declares itself a one-device game (0 joysticks, suggested mode `xbox`).
- **Command list**: `DB\HLLVietnam\actions.tsv`, two kinds of rows: `Pad_*` helicopter actions with the default key per
  layout in the 5th column (`A:Gamepad_LeftY|B:Gamepad_RightY|C:Gamepad_LeftY`), and `Driver_*` keyboard pilot keys.
  [[cli:hllv refresh-db]] writes JoyPro's own table, or reads the game's input assets when an Oodle library may be used
  (the game's own first, another game's only after your yes: `--oodle-dll <path>`, loaded where it is, only in the
  refresh process). [[cli:hllv oodle]] names the library it would use.
- **Pad plan**: the planner reads the layout from `GameUserSettings.ini`, pins each action to its key in that layout
  (`Gamepad_LeftY` to `XB_LY`, `Gamepad_FaceButton_Right` to `XB_B` ...); a relation on both collective actions becomes
  `XB_TRIGGERS`; *Invert* adds `~`. [[cli:vjoy mode]] `HLLVietnam xbox`, [[cli:vjoy map]].
- **Keyboard file**: Unreal GVAS with the Enhanced Input tail (profiles, mappings with name, key, hardware class and
  slot). JoyPro rewrites only the `<Action>_Keyboard` mappings of the pilot actions; key names are turned into Unreal
  key names (`Space` to `SpaceBar`, `D1` to `One`).
- **Commands**: [[cli:hllv paths]], [[cli:hllv catalog]], [[cli:hllv scan]], [[cli:hllv import]], [[cli:hllv export]]
  `--include-keyboard --mode clean|add|merge|merge-overwrite --dry-run`.

### Known limits

- A flight with the virtual pad is not confirmed yet (collective on the triggers, cyclic, yaw, stability hold, supply
  drop); the menus were checked.
- Yaw and look share a stick axis in layouts A and B.
- Tests: `engine\tests\smoke\hllv.ps1` (no library loaded without your yes, the fallback table, the game's own library
  first); in the game `tools\e2e\games\HllVietnamXbox.ps1` (menus with the virtual pad; vJoy and HidHide unchanged
  afterwards).
:::
