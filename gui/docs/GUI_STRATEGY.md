# JoyPro GUI Strategy (Current)

## 1. Main goal

Build a beginner-friendly GUI with full command parity.
No capability that exists in CLI should be missing in UI.

## 2. Core workflow (requested)

1. Start: `New`, `Open`, `Import`
2. Relations stage
3. Binds stage (modifier manager embedded here)
4. Layout visual assign stage (legacy-compatible)
5. Export stage
6. Settings and maintenance stage

Important rule:
- Import is available at start and later.
- If no profile is open, import creates a new profile implicitly.
- Save Profile is always visible while editing.
- Merge Profile is available in Relations and Binds before export.

## 3. UX policy

1. Guided first
   Beginners see step-by-step workflow and explanations.
2. Advanced always available
   Raw command console remains available for full parity fallback.
3. No hidden destructive actions
   Preview and confirmation before risky operations.
4. Clear domain language
   Relation, node, bind, modifier, profile explained in-place.
5. Fast stage switching
   Bottom workflow bar lets users move back and forth between Relations and Binds.
6. Explicit ownership model
   Relations can have multiple groups; modifiers are profile-wide objects that are attached to binds explicitly.
7. Global appearance control
   Theme can be switched from the header quick toggle or in Settings.
8. Progressive disclosure for rare actions
   Mass operations, AI grouping, quick bind actions, and modifier library default to collapsed panels.

## 4. Feature parity policy

Parity mapping is maintained in:
- `FEATURE_PARITY_UI_MAP.md`

It maps command families to screens:
- Workspace
- Import Wizard
- Relation Studio
- Bind Studio
- Layout Studio
- Export Center
- Settings and Maintenance
- Advanced Console

## 5. AI assistant concept for relation grouping

Planned assistant in Relation Studio:

- Input: selected ungrouped nodes
- Output: suggested relation groups and names
- Safety: suggestions only, never auto-apply silently
- User control: approve/reject each suggestion before changes are written

This can be implemented as a separate service adapter later (local or hosted LLM), while the review UI stays deterministic.

## 6. Binding interaction model

Bind capture supports two equivalent entry paths:
- Record mode
  - next changed joystick button becomes the bind
  - next axis motion over 30 percent becomes the bind
- Visual assign mode
  - opens optional Layout Studio side flow
  - selected control returns to Bind Studio

Modifier handling in Bind Studio:
- `Create modifier` creates a reusable profile-level modifier in the modifier library
- attach and detach to bind are separate actions
- a bind can have multiple modifiers
- bind modifier evaluation supports mode and ordering controls
- list pane supports device filter in addition to group and search filters

Axis parameters stay directly editable and support:
- single curvature value
- multi-point curvature curve
- deadzone, saturation, invert, slider, and behavior values

## 7. Relation grouping model

Relations support multi-group membership:
- one relation can be in multiple groups
- group filter is multi-select
- filter matches if relation has any selected group

## 8. Layout styling model

Layout Visual Assign exposes text styling controls:
- font family, font size, font weight
- text color, outline color and thickness
- label background color and padding
- alignment and case options

Layout mapping behavior:
- user sets control id positions on the image (button and axis points)
- layout file stores control ids and coordinates
- export resolves control ids into game and plane specific action labels

## 9. DCS maintenance model

DCS maintenance separation:
- DCS Defaults tools are separate from DCS FFB tools
- DCS FFB editor follows legacy behavior with per-device toggles:
  - Swap Axis
  - Invert X
  - Invert Y

## 10. Technology choice

Recommended stack: Avalonia + MVVM + Engine CLI backend.

Reason:
- modern desktop UX,
- strong separation between GUI and engine,
- keeps existing engine logic intact.

## 11. Current mockup set

See:
- `docs/mockups/README.md`

Only files listed there are current.
