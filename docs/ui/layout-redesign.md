# Layout pages

The Layout page and "Bind on the picture" (Bind Studio) are built around one picture canvas, `DeviceMarkerCanvas`.

- It shows the **device reference** of a device (`docs/device-reference.md`): your photo, else the maker's photo, else
  JoyPro's drawing, with a marker on every part and a legend.
- It also shows the device's **layout file**: a `.layout` with label markers, tables and lines, which the cheat sheets
  use.
- The device reference view (`DeviceReferenceView`) draws its picture with the same control, so there is only one
  canvas.

## Pages

### Layout page (bottom bar: Layout)

A switch in the header picks the section.

#### Device pictures

Three columns.

**Your devices** (left)

- A card per device: name without the GUID, model, and "Your layout · 47 markers" or "Reference picture".
- Refresh.
- "Use your own picture": search a picture, a file or an address, Use picture.
- "Where JoyPro looks": catalog, JoyPro 1 folders, sources, layout file paths.
- When the page opens, the first device (or the picked one) is shown at once.

**The picture** (middle): a toolbar, the canvas and a status bar.

- Modes:
  - **View**: what every input does.
  - **Edit**:
    - drag markers (snapping, undo/redo);
    - on the reference: save/reset markers, your photo (button or drop a file on the picture), maker photos on/off,
      the marker assistant, device definitions;
    - on the layout file: place/add/remove markers, tables and lines.
  - **Bind**: click an input, pick a relation, bind or free it in the profile.
- A floating label on the picture shows:
  - the device;
  - the picture choice: your photo / photo from <maker> / JoyPro drawing / "Layout file: <name>";
  - the grip choice for a base that several grips share;
  - the maker's credit under a vendor photo.
- The marker assistant's panel takes the label's place while it runs: Start, Axis, Skip, Undo, Stop, Finish, Close.
  A click on the picture places the part it asks for.
- The status bar shows what the reference is ("Thrustmaster · Stick · USB 044F:0402 · all 25 inputs known") or its
  message, and key hints.
- Empty state (no device): "Pick a device on the left".

**Tables and lines** (a reference picture with known positions; details:
[device-reference.md](../device-reference.md#tables-and-lines-callout-sheets))

- The default look: a table per control group around the picture, orthogonal lines to the parts, the bound relation
  per row and the modifier bind in the second column (JoyPro 1's cheat sheets). **Tables** on the toolbar switches to
  the markers with labels; the marker assistant always uses the markers.
- The preview box next to it: **Relation names**, or one game and aircraft: the rows show that game's own menu titles
  and the sheet gets JoyPro 1's GAME / PLANE band.
- View and Bind: a click on a row picks its input (side panel, Bind). Right-click a row: **Change...** / **Clear**.
- Any mode: a relation dragged from **Drag a relation onto a row** (side panel) and dropped on a row binds it there.
- Edit: drag a table (pinned), its right edge (width), a line's segment (bend), a spot (moves the part), the round
  handle of the selected table to a spot (adds its line; places a part without a position). Del removes the selected
  line. Side panel: **Modifier column**, **Row + / −**, **Pinned**, **Re-route line**, **Remove line**, **Split into
  single tables** (a key grid or a shared table), **Group again**, **Auto arrange**, **Reset**, **Modifier column
  shows**. Undo / Redo and Save as for markers. A relation dropped on a key's cell of a grid binds that key.

![Tables, edit mode](layout-tables-edit-after-1920-light.png)

**Side panel** (right)

- The selected input: key cap, physical name, kind, DirectInput name, modifier.
- A part with several inputs (a hat's directions and press) lists them to pick one.
- "Does": the bound command.
- Edit mode: the layout file's marker fields, or the reference's "Picture and markers" card.
- Bind mode: search, relations of the right kind (axis or button), Bind / Free.
- Below: the legend with a filter and "7 of 25 inputs bound".
  - Reference: badge, input, part and command, editable. Your texts are kept per model; an empty command shows the
    profile's bind.
  - Layout file: the markers with what they do, plus "Tables, lines and label style".

#### Cheat sheets

The printable pictures, as before (devices, games, aircraft, what to make, look, output folder, preview gallery).

- A device needs a layout file: the user's own (JoyPro 1, his picture); else the device's sheet of tables and lines
  (the picture last shown for it on the Device pictures section, saved before the export); else the picked one.
- **One page with all devices** (per aircraft and for the overview; `--combine`). Sheets are printed 3508 px wide (A4
  landscape at 300 dpi). The page colour of the sheet style decides the grey or the white page (`--style`).

#### Sheet style

One style for the look of every cheat sheet, edited in the **Sheet style** panel (`Views/Controls/SheetStyleEditor`,
`ViewModels/SheetStyleViewModel.cs`, `MainWindowViewModel.SheetStyle.cs`): on Device pictures (right column) and in the
cheat sheet options. Values: font (installed fonts, `FontPickerField`), size (16 = the tables' own size, 24 = 1.5 times;
on a layout picture the labels' point size), weight; the labels' text, halo and box colour; page, table, table border,
header row (background and text), table text, leader lines; a marker colour per kind of input (button, axis, hat,
slider); modifier colours as a list (name + colour, add/remove; markers of binds with the modifier and a bar over its
column); JoyPro's drawing of the device (`DrawingStyleEditor`). All colours are `ColorField`s (`#AARRGGBB`; fully
transparent halo or box = none).

- Presets: **JoyPro default** (JoyPro 1's grey page, white tables), **Print** (white page), **High contrast**, **Dark
  sheet**; **Save as preset** keeps the style under a name (`<data>\SheetStyles\<name>.json`), Delete removes it.
- The global style is kept in `<settings>\sheet-style.json` (survives a restart).
- **This device uses its own style** (Device pictures): the shown device's sheet gets a copy to change. It is stored
  where the engine reads it for that device: inside its sheet (`"style"` of the callouts, saved at once, not part of the
  sheet's undo) or `<layout>.style.json` next to a .layout file. "Share layout..." carries it with the sheet, "Import
  layout..." brings it back. Off removes it again.
- The editor draws with the shown device's style (`LayoutSheetStyle`: `CalloutSheetCanvas.SheetStyle`,
  `DeviceMarkerCanvas.SheetStyle`, the layout file's tables and lines, the label font, size and colour); "Create cheat
  sheets" passes the global style to `layout render` (`--font --font-size --font-weight --text-color --outline-color
  --label-background --page-background --table-background --table-border --header-background --header-text --table-text
  --line-color --marker-color-button|axis|hat|slider --modifier-colors`, or `--sheet-style <preset|file>`). The shared
  `engine/DeviceReference/SheetStyle.cs` resolves the palette for both, so screen and print match.
- Otherwise JoyPro asks the engine (`layout suggest --device`). For a device the catalog knows, the engine makes a
  layout on JoyPro's drawing with a marker on every input. So cheat sheets do not need the shipped `LayoutTemplates`
  pictures.

### Bind Studio > Visual assign ("Bind on the picture")

The same picture of the same device (the Layout page follows), in Bind mode.

- Toolbar: device, picture choice, "Edit markers" (opens the Layout page in Edit mode) and Close.
- Right: the selected input with its state (green "Bound: Pitch" chip or grey "Free"), its inputs (a hat's
  directions), **With modifiers** (only when the profile has modifiers: one row per combination "—", "Shift A",
  "Shift B", "Shift A + Shift B" with the relation bound there; clicking a row picks the combination Bind here uses),
  the preview "Pitch → device · input", **Bind here** / **Free**, "Or pick another relation" (search, list), all
  inputs (green dot + relation = bound, hollow dot + "free").
- **Bind here** binds, in this order: the command parameter, the relation picked in the side list, else the relation
  picked on the left of Bind Studio (the bind list). The left relation is preselected in the list when it fits the
  input's kind; an axis relation on a button (or the reverse) is refused with the reason. Every step is written to the
  session log as `[VisualAssign]` lines (`GuiTrace` in tests). After binding: the same refresh as Set Bind / Record
  (`RefreshBindsAndModifiersAfterChangeAsync`: binds, the bound counts of Bind Studio and Start, modifiers), the status
  line and the side panel say "Bound Pitch to <device> <input>.", the input's marker gets a green ring for 4 s
  (`WorkspaceFlashMarker` -> canvas `FlashMarker`).
- In Bind mode the canvas colors the inputs: a bound part's dot is green (its label has a green border), a free one is a
  hollow grey dashed dot; a layout file's bound badges are green.
- **Drag and drop**: a relation dragged from the bind list (left) or from "Or pick another relation" onto an input of
  the picture is bound there (the marker under the pointer, or the nearest within 28 px, gets an accent ring while
  dragging; the drop selects it and runs Bind here with the dragged relation: `DeviceMarkerCanvas.RelationDroppedCommand`,
  `DropRelationAt`). The marker and its table update as after Bind here.
- A device that is not listed is looked up with `layout suggest --device`.
- (Before: Bind here passed no relation and used only the side list's pick, which is empty unless the input was bound,
  so it did nothing; the message went to a status the panel did not show.)

### Which picture first

The order is described in [device-reference.md](../device-reference.md#2-picture-sources).

1. The user's own layout file. That is:
   - a JoyPro 1 layout (`legacy-layout-dir`, also those taken over with **Use my old JoyPro layouts...**);
   - a picture the user chose (`user-picture`);
   - a layout saved in this session.
2. Your photo.
3. The maker's reference photos, one per view.
4. JoyPro's drawing, when it is recognizable.
5. The class outline.

- Layout files the engine generated (its drawings, placeholders, shipped templates) are not a picture of the strip.
  They only feed the cheat sheets.
- The picture strip (a button per picture) replaces the old picture combo box on the floating label.
- `LayoutSuggestionItem.IsOwnLayout` and the session's saved layouts decide whether the layout file comes first.

### Over the picture

- **"Which one is it?"**: a base whose grip is not known, a USB id several products share, or an unknown device.
  - Candidate tiles with photos; for an unknown device, a catalog search and the kinds of device.
  - It is hidden while the marker assistant runs.
- **No positions on this picture yet**: offers the marker assistant and *Import positions...* when the shown picture
  knows no part's position.
- **Use my old JoyPro layouts...** in the device column: a folder pick (it starts in a JoyPro 1 folder found on the PC),
  and a line saying what was taken over.
- The Layout page lists one entry per physical device: interfaces without inputs, and a keyboard's key interfaces
  next to its gamepad interface, are left out.

### Keys

- Layout page (outside text boxes):
  - V / E / B: the modes.
  - N: place marker (layout file only).
  - Del: remove the selected marker (layout file only).
  - Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z): undo / redo marker moves and edits. Reference part moves count too.
  - Ctrl+S: saves the shown picture's markers.
- On the canvas: + / - zoom, 0 or F fit, Esc clears the selection, arrows nudge in Edit mode (Shift: 10 px).
- Mouse:
  - The wheel zooms at the pointer.
  - Dragging the background, the middle button or Space + drag pans.
  - Alt drags without snapping.
  - Dropping a picture file on the canvas makes it your photo of the device.

## The canvas contract

`gui/JoyPro.Gui/Views/Controls/DeviceMarkerCanvas.cs` (`Border x:Name="DeviceReferenceHost"` on the Layout page,
`VisualAssignCanvas` in Bind Studio, `Canvas` inside `DeviceReferenceView`).

### Properties

| Property | Meaning |
|---|---|
| `Source` (IImage) | a photo or layout picture |
| `Schematic` (DeviceSchematic) | JoyPro's vector drawing, drawn when there is no `Source` |
| `SourceWidth`, `SourceHeight` | the picture's size = the marker coordinate space (picture pixels); 0 = the picture's or drawing's size |
| `Markers` (IEnumerable of IDeviceMarker) | collection and item changes redraw |
| `SelectedMarker` (object, two-way) | set by a click; set from outside it scrolls into view |
| `Mode` | `View`, `EditMarkers` (dragging), `Bind` |
| `Zoom` (two-way) | 0.05 to 4; the picture stays fitted until the user zooms or pans |
| `ShowCommands`, `ShowBadges` | the bound command next to markers (not in Edit mode); the numbered badges of parts |
| `SnapEnabled`, `ShowGrid`, `GridSize` | snapping to the other markers' edges / centres, the grid |
| `Overlay`, `ShowOverlay` | extra content in picture coordinates (the layout file's tables and lines) |
| `ShowChrome`, `ShowBackdrop`, `PictureBackground`, `HintText` | zoom bar and hint; the dotted canvas and shadow; the drawing's paper; the hint text |

### Events (also as commands)

| Event | Command | Argument | When |
|---|---|---|---|
| `MarkerClicked` | `MarkerClickedCommand` | IDeviceMarker | a click on a marker |
| `MarkerMoved` | `MarkerMovedCommand` | `DeviceMarkerMove(Marker, FromX, FromY, ToX, ToY)` | end of a drag or an arrow nudge (Edit mode); the canvas never moves a marker itself |
| `CanvasClicked` | `CanvasClickedCommand` | `DeviceCanvasClick(X, Y, IsRightButton)` | a click on the picture outside the markers (the selection is cleared first) |
| `PictureFileDropped` | `PictureFileDroppedCommand` | the file path | a picture file dropped on the canvas |

`PictureRect()`, `ToCanvasPoint()`, `ToViewPoint()`, `FitToView()`, `ZoomIn()` and `ZoomOut()` are public.

### Markers

`IDeviceMarker` (`gui/JoyPro.Gui/Models/IDeviceMarker.cs`) has these members:

| Member | Meaning |
|---|---|
| `Input` | the DirectInput name |
| `Kind` | button, axis, pov or slider |
| `Modifier` | the modifier |
| `X`, `Y` | in picture pixels |
| `Command` | what the input does |
| `IsSelected` | drawn as selected |

Two kinds of markers implement it:

- **Label markers** (`LayoutEditorMarkerItem`, the layout file). X/Y is the label's top-left corner.
  - Drawn as a pill: kind dot, short label, modifier, command.
  - With `PlainLabels` (the user's JoyPro 1 layout): the bound command as plain text in `LabelFontFamily`,
    `LabelFontSize` (picture pixels) and `LabelForeground`, the layout's own text style, like JoyPro 1's cheat sheets.
    Unbound inputs show nothing in View mode, and a small tag in Edit and Bind mode.
- **Part markers** (`IDevicePartMarker`: `Radius`, `Badge`, `Label`, `IsHighlighted`, `Details`). X/Y is the part's
  centre.
  - On a photo: a small dot. On JoyPro's drawing: no dot (the part is drawn), only a ring when selected or hovered.
  - Next to it a compact white label with the bound command, joined by a leader line. Unbound parts get a small tag
    with the short name.
  - Labels are placed together so that none covers another label or part: right, left, above, below, then further
    out. Where none fits, the part's number is drawn at the part.
  - Amber while held or while the assistant asks for it.
  - `ReferenceCanvasMarker` wraps a reference part (`ReferenceMarker`, 0..1 of the picture) for the picture's pixel
    size. Only parts whose position is known on the shown picture (`IsShown`) become canvas markers.

### View model side

- `DeviceReferenceViewModel` (`DeviceReferenceViewModel.Canvas.cs`) gives the canvas its members:
  - `CanvasPhoto` / `CanvasSchematic`, `PictureWidth` / `PictureHeight`, `CanvasMarkers`, `SelectedCanvasMarker`
    (kept in sync with `SelectedMarker`), `CanvasMode`;
  - `CanvasMarkerMovedCommand` (→ `MoveMarker`), `CanvasClickedCommand` (→ the marker assistant's `PlaceAt`),
    `DropPhotoCommand` (→ your photo);
  - `Clear()`.
- `MainWindowViewModel.LayoutReference.cs` feeds the Layout page and Bind Studio from one place:
  - `Workspace*` (`WorkspaceImage`, `WorkspaceSchematic`, `WorkspaceWidth/Height`, `WorkspaceMarkers`,
    `WorkspaceSelectedMarker`);
  - `LayoutShowsLayoutFile` / `LayoutShowsReference`, `WorkspacePictures` / `SelectedWorkspacePicture`,
    `ReferenceLegendItems`, `SelectedPartRows`;
  - the selected input `WorkspaceInput*`, `ShowWorkspaceDeviceAsync(device)`, `ClearLayoutWorkspace()`.
- `MainWindowViewModel.LayoutWorkspace.cs`:
  - modes;
  - `LayoutCanvasMarkerMovedCommand` and `LayoutCanvasClickedCommand`, for the layout file and the reference;
  - snapshot undo/redo of either picture's markers;
  - Bind and Free (`BindSelectedLayoutMarkerCommand`, `FreeSelectedLayoutMarkerCommand`) on the selected layout
    marker or reference input, through `AssignInputAsync`.

## The callout canvas contract

`gui/JoyPro.Gui/Views/Controls/CalloutSheetCanvas.cs` (`LayoutCalloutCanvas` on the Layout page, shown instead of
`DeviceMarkerCanvas` while `LayoutShowsCallouts`).

| Property / event | Meaning |
|---|---|
| `Callouts` (CalloutSheetViewModel) | the sheet (`CalloutSheet`, shared engine code), the row texts (`TextOf`), the edits with undo/redo |
| `Source` / `Schematic` | the photo or JoyPro's drawing, drawn at the sheet's picture rectangle |
| `Mode` | View / EditMarkers / Bind (`DeviceCanvasMode`) |
| `HeaderGame`, `HeaderPlane` | the GAME / PLANE band of a preview |
| `SheetStyle` | the cheat sheet style (palette, font, size, weight, modifier bars); null = the theme's base palette |
| `ShowGrid` | the snap grid in Edit mode |
| `RowClicked` (input, modifier) | a row was clicked |
| `RowChangeRequested` (input, modifier) | right-click > Change... |

A drag of text `joypro-relation:<name>` (`CalloutSheetCanvas.RelationDragFormat`) dropped on a row calls
`CalloutSheetViewModel.DropRelation`, which raises `BindRequested`; `MainWindowViewModel.LayoutCallouts.cs` binds it
through `AssignInputAsync` with the modifiers of the cell's column (`CalloutSheet.ComboOfColumn`; see
[device-reference.md](../device-reference.md), "Modifier combinations"). The canvas heads each modifier column with its
combination and reports the column (`HitRow(..., out int column)`).

`CalloutSheetViewModel`: `Show(drawing, positions, width, height, saved)`, `UpdatePositions`, `MoveTable`,
`ResizeTable`, `SetModifierColumn`, `ChangeRows`, `BendLine`, `DeleteLine`, `RerouteLine`, `AddLine`, `MoveSpot`,
`SetPinned`, `SplitGroup`, `Regroup`, `AutoArrange`, `ResetLayout`, `Undo` / `Redo`, `SelectRow` / `SelectInput`, `DropRelation`, `ClearRow`;
events `Changed`, `BindRequested`, `PartPlaced` (a part got a spot: the reference marker moves there).

## Checking the look

`LayoutRedesignRenderTests` renders the pages offscreen with a fake engine. Its devices are:

- **Stick:** the Warthog stick of the catalog, plus a VKB layout file.
- **Throttle:** the Warthog throttle, with no layout file.
- **Home Cockpit Box:** an unknown button box.

The pages are:

- editor (reference, Edit), view, bind (the markers with labels, Tables off);
- tables (View), tables-edit (a selected table with its handles), tables-preview (DCS F-16C_50 menu titles);
- layoutfile (the stick's layout file, Edit);
- assistant (the unknown box, assistant waiting for a click);
- visualbind (Bind Studio), cheatsheets, empty.

Each page is rendered at 1920x1080 and 1280x800, dark and light.

No picture is downloaded in tests (`DeviceImageCache.InternetPicturesBlocked`), so the reference shows JoyPro's
drawing. The test needs `JOYPRO_UI_DESIGN_OUT` and must run alone. `JOYPRO_UI_LAYOUT_PICTURE` is a layout preview PNG
(`engine layout template-inspect --source <your own .layout file>` writes one to `%LOCALAPPDATA%\JoyPro2\LayoutPreview`);
without it the test draws a stand-in.

```
set JOYPRO_UI_DESIGN_OUT=<repo>\docs\ui
set JOYPRO_UI_DESIGN_TAG=after
set JOYPRO_UI_LAYOUT_PICTURE=<preview png>
dotnet test gui/JoyPro.Gui.Tests --filter LayoutRedesignRenderTests
```

Files: `docs/ui/layout-<page>-<tag>-<width>-<theme>.png`.

- The `before` pictures (editor, bind, visualbind, cheatsheets) are the GUI before the redesign.
- The light renders show smeared bold text on some buttons and titles. The earlier design pass pictures have it too
  (offscreen renderer); it has not been checked in a shown window.
- The reference view's pictures: `JOYPRO_DEVICE_REFERENCE_PREVIEW=<repo>\docs\device-reference` with
  `--filter DeviceReferenceTests`.
