# JoyPro UX Blueprint (Current)

## Product promise

"JoyPro helps pilots manage controls across games and planes without forcing them to understand internal engine structures."

## User types

1. Beginner
   - wants to import and fly fast
   - needs guidance and explanations
2. Intermediate
   - wants stable profile maintenance
   - needs filters, validation, and safe export
3. Power user
   - wants mass operations and exact control
   - needs full parity and raw command access

## Stage-based UX

1. Start stage
   - New, Open, Import
   - import auto-creates profile if none is open
2. Relation stage
   - add, edit, delete, group, filter, mass operations
   - includes import entry and group filter on the relation list
   - relation can be in multiple groups
   - group filter is multi-select
   - mass operations panel is collapsible and collapsed by default
   - AI grouping panel is collapsible and collapsed by default
3. Bind stage
  - set, clear, unbind, and modify binds
  - modifier manager integrated here
  - modifier library and bind attachments are separate actions
  - create modifier adds it to library only
  - bind can attach multiple modifiers with order and mode control
   - group filter on relation list
   - device filter on relation list
   - quick bind actions panel is collapsible and collapsed by default
   - modifier library panel is collapsible and collapsed by default
  - record mode for quick capture:
    - next changed button assigns
    - next axis movement over 30 percent assigns
   - optional visual assign entry opens Layout Studio side flow
   - axis behavior values remain fully editable, including single or multi-point curvature
4. Layout stage
   - visual assign mode compatible with legacy workflow
   - optional stage for users who do not need visual assignment
   - user places physical control ids (button and axis) on image points
   - export resolves control ids into game and plane specific action labels
   - text styling controls for labels:
     - font family, size, weight
     - text color, outline, background
     - alignment and padding
5. Export stage
   - mode selection and conflict policy preview
6. Settings and maintenance stage
   - refresh db, defaults tools, path discovery, diagnostics
   - appearance controls including light and dark theme
   - DCS Defaults and DCS FFB are separate sections
   - DCS FFB uses per-device toggles: Swap Axis, Invert X, Invert Y

## AI grouping helper concept

Placement:
- inside Relation stage

Flow:
1. user selects ungrouped nodes
2. assistant suggests grouped relations
3. user reviews suggestion diff
4. user applies selected suggestions

Guard rails:
- no auto-apply without user confirmation
- always show before/after preview
- write actions to profile only after explicit apply

## Required visibility rules

1. Current profile always visible
2. Current game context always visible
3. Validation status always visible
4. Last action result always visible
5. Save Profile always visible during editing
6. Merge Profile available before export from Relations and Binds
7. Bottom workflow bar supports back and forth movement between stages

## Success metrics

1. Beginners reach first successful import in under 3 minutes
2. Users complete export with no unreviewed conflicts
3. Fewer support questions about relation vs bind vs modifier
4. High completion rate for profile maintenance tasks
