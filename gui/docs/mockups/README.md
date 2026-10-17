# JoyPro Mockups (Current Set)

Old mockups were removed to avoid confusion.
Only the files below are current.

## Current workflow mockups (base files)

1. `mockup_v3_01_start_workspace.svg`
2. `mockup_v3_02_relations_ai.svg`
3. `mockup_v3_03_binds_and_modifiers.svg`
4. `mockup_v3_04_layout_visual_assign.svg`
5. `mockup_v3_05_export_stage.svg`
6. `mockup_v3_06_settings_db_tools.svg`
7. `mockup_v3_07_visual_assign_mode.svg`
8. `mockup_v3_08_relation_node_search.svg`

## Theme variants

Light mode set:

- `light/mockup_v3_01_start_workspace.svg`
- `light/mockup_v3_02_relations_ai.svg`
- `light/mockup_v3_03_binds_and_modifiers.svg`
- `light/mockup_v3_04_layout_visual_assign.svg`
- `light/mockup_v3_05_export_stage.svg`
- `light/mockup_v3_06_settings_db_tools.svg`
- `light/mockup_v3_07_visual_assign_mode.svg`
- `light/mockup_v3_08_relation_node_search.svg`

Dark mode set:

- `dark/mockup_v3_01_start_workspace.svg`
- `dark/mockup_v3_02_relations_ai.svg`
- `dark/mockup_v3_03_binds_and_modifiers.svg`
- `dark/mockup_v3_04_layout_visual_assign.svg`
- `dark/mockup_v3_05_export_stage.svg`
- `dark/mockup_v3_06_settings_db_tools.svg`
- `dark/mockup_v3_07_visual_assign_mode.svg`
- `dark/mockup_v3_08_relation_node_search.svg`

## Notes

- Import is available from start and also later in the flow.
- If no profile is open, import creates a new profile implicitly.
- Save Profile and Merge Profile are visible during Relations and Binds editing.
- Relations and Binds lists support multi-select filtering by group.
- Relations can belong to multiple groups.
- Binds list supports filtering by device directly in the left filter row.
- Binds stage includes Record capture (button change or axis movement over 30 percent).
- Binds stage separates modifier creation from bind attachment and supports multiple modifiers per bind.
- Relation page keeps `Mass operations` and `AI Grouping Assistant` collapsed by default.
- Binds page keeps `Quick bind actions` and `Modifier Library` collapsed by default.
- Relation stage includes a planned AI assistant concept with human approval.
- `Open Visual Assign` is in the Bind Studio header because it switches the bind-tab view mode.
- Visual assign is a mode inside Bind Studio (`v3_07`), not a separate window.
- Node add and database search flow is shown in `v3_08` (search, select, add, and remove single node).
- Layout editor places physical control ids (button and axis) on image positions.
- Export resolves control ids into game and plane specific action text automatically.
- Export stage includes `Validate profile` before export and a result panel with `Show errors` and `Show warnings` filters.
- Conflict detection stays separate in the `Conflicts detected` panel (`Open conflicts`), not inside profile validation rows.
- Warnings are hidden by default and can be expanded when needed.
- Validation results are shown as a row-based issue list with per-row action buttons.
- Validation actions include old JoyPro parity operations such as `Deactivate plane item`, `Delete bind`, and warning resolution choices.
- Relation list includes an explicit `New Relation` action.
- Nodes in relation show both `Node ID` and `Title`.
- Layout stage includes text styling controls (font, size, color, outline, background, alignment).
- Theme switch is available as a quick header toggle and in Settings under Appearance.
- DCS Defaults and DCS FFB are separate sections in Settings.
- DCS FFB follows legacy parity: per-device `Swap Axis`, `Invert X`, `Invert Y`.
- Settings and DB maintenance are in dedicated UI.
- A bottom workflow bar allows back and forth navigation between stages.
- Both `light/` and `dark/` sets are XML-validated.
