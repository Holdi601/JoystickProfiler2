# JoyPro GUI Feature Parity Map (CLI -> UI)

> The current, checked map with a novice rating per command is [docs/gui-cli-parity.md](../../docs/gui-cli-parity.md).
> This file is the original plan of the GUI surfaces.

This document maps current CLI command families to planned GUI surfaces.
Goal: every CLI capability has a discoverable place in the UI.

## 1) Workspace

GUI Surface: `Workspace Center`

- `engine summary`
- `engine profile summary`
- `engine profile load`
- `engine profile save`
- `engine profile save-last`
- `engine profile validate`
- `engine profile merge`

Flow rule:
- If user runs Import without an open profile, GUI creates a new profile implicitly before import.
- `Save Profile` is always visible in the header while editing.
- `Merge Profile` is available in Relations and Binds stages (not only in workspace).
- quick header theme toggle is always available (light and dark)

## 2) Import / Scan

GUI Surface: `Import Wizard`

- `engine dcs scan`
- `engine dcs import`
- `engine il2 scan`
- `engine il2 import`

Import entry points:
- available at start screen
- available later from toolbar and stage navigation

## 3) Export

GUI Surface: `Export Center`

- `engine dcs export`
- `engine il2 export`

Export Center includes mode selector:
- clean
- add
- merge
- merge-overwrite

Export Center includes options:
- keep keyboard defaults
- nuke untouched devices
- conflict policy guidance

## 4) DCS Tools

GUI Surface: `DCS Maintenance`

- `engine dcs defaults cut`
- `engine dcs defaults restore`
- `engine dcs refresh clean`
- `engine dcs refresh id`
- `engine dcs refresh preview-clicks`
- `engine dcs ffb list`
- `engine dcs ffb set`
- `engine dcs ffb clear`

DCS Maintenance UX structure:
- `DCS Defaults` and `DCS Force Feedback` are separate cards
- Force Feedback card uses legacy-style per-device toggles:
  - Swap Axis
  - Invert X
  - Invert Y

## 5) Database

GUI Surface: `Database Center`

- `engine db summary`
- `engine db list`
- `engine db check`
- `engine db games`
- `engine db planes`
- `engine db fingerprint`

## 6) Game Discovery

GUI Surface: `Games & Paths`

- `engine games paths`

## 7) Layout

GUI Surface: `Layout Studio`

- `engine layout render`

Layout Studio includes:
- visual assignment mode (legacy JoyPro behavior)
- save/load layout mappings
- per-plane and overview rendering
- label styling controls: font family, size, weight, text color, outline, background, alignment
- control-id placement workflow: set button and axis positions on image
- export resolves control ids into plane and game specific action labels

## 8) Device Management

GUI Surface: `Device Center`

- `engine devices list`
- `engine devices inputs`
- `engine devices exchange`
- `engine devices replace`
- `engine devices auto-remap`
- `engine devices remove`

## 9) Relation Management

GUI Surface: `Relation Studio`

- `engine relations list`
- `engine relations show`
- `engine relations add`
- `engine relations group list`
- `engine relations group add`
- `engine relations group remove`
- `engine relations group rename`
- `engine relations item add`
- `engine relations item remove`
- `engine relations item enable`
- `engine relations item disable`
- `engine relations mass delete`
- `engine relations mass duplicate`
- `engine relations mass merge`
- `engine relations mass split`
- `engine relations mass add-prefix`
- `engine relations mass add-postfix`
- `engine relations mass remove-part`
- `engine relations mass export`
- `engine relations export`
- `engine relations import`
- `engine relations edit`

Relation Studio UX commitments:
- left list can be filtered by group (multi-select)
- each relation can belong to multiple groups
- import action is available directly in this stage
- profile save and profile merge are available without leaving this stage
- mass operations panel is collapsible and collapsed by default
- AI grouping panel is collapsible and collapsed by default

## 10) Bind Management

GUI Surface: `Bind Studio`

- `engine binds list`
- `engine binds show`
- `engine binds set`
- `engine binds clear`
- `engine binds modifier list`
- `engine binds modifier add`
- `engine binds modifier remove`
- `engine binds modifier rename`
- `engine binds unbind`
- `engine binds export`
- `engine binds import`
- `engine binds edit`

Bind Studio UX commitments:
- left list can be filtered by group (multi-select)
- left list can be filtered by device
- record capture mode is available:
  - next changed button assigns immediately
  - next axis motion over 30 percent assigns immediately
- optional Visual Assign entry opens Layout Studio side flow and returns selected input
- axis behavior values are directly editable, including single and multi-point curvature modes
- profile save and profile merge are available without leaving this stage
- modifier model is explicit:
  - `Create modifier` adds a profile-wide modifier to the modifier library
  - attaching modifiers to a bind is a separate action
  - multiple modifiers per bind are supported (order and mode controls)
- quick bind actions panel is collapsible and collapsed by default
- modifier library panel is collapsible and collapsed by default

## 11) Corrections / Validation

GUI Surface: `Corrections & Validation`

Global options represented in UI settings:
- `--load-corrections`
- `--auto-add-db-items`
- `--remap-missing-devices`
- `--confirm-device-remap`
- `--correction-mode`
- `--correction-auto-threshold`
- `--confirm-corrections`
- `--correction-report`

Appearance options represented in settings:
- light theme
- dark theme
- system theme

## 12) Diagnostics / Advanced

GUI Surface: `Advanced Console`

Global options represented in Advanced Console:
- `--out`
- `--editor`
- `--text`
- `--status`
- `--benchmark`

Advanced Console always supports raw command execution for parity fallback.

`--status true` (with its `[engine-progress]` lines, `--progress`) is always on for the commands the GUI runs: the busy
area (Start page, and a compact row above the status line on the other pages) shows them live with Cancel where the
engine offers it (`[engine-cancel]`, `JOYPRO_CANCEL_FILE`). The "Status Updates" option only decides whether the
status lines also stay in the engine output pane.

## 13) Stage Navigation

GUI Surface: `Bottom Workflow Bar`

- visible in editing stages
- user can click to move back and forth between Relations and Binds
- Layout stage is optional side stage (for visual assign and image export)

---

## Beginner UX policy

- Default users see guided workflows first.
- Advanced users can open raw command mode instantly.
- Every dangerous action has preview + confirmation + clear outcome text.
