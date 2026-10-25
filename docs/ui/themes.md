# Appearance: themes, text size, UI scale, fonts

Settings > Appearance (`ViewModels/MainWindowViewModel.Appearance.cs`, `Services/ThemeService.cs`).

## Colours

- Light, Dark, System, or a theme of the user's own. The choice is kept in `ui-settings.json` (`themeMode`, and
  `customTheme`: the theme's file name).
- Every colour of the GUI is a resource token ({DynamicResource JP.TextBrush}, ...). The Light and Dark values are in
  `Styles/Design.axaml` (and the status brushes in `Styles/Icons.axaml`); `ThemeService.Tokens` lists every token with its
  group and name for the editor: pages, text, accent, buttons (normal, under the mouse, pressed, switched off), status,
  chips and key caps, the header banner, the bottom bar, the engine output, charts and the markers on device pictures.
  Fluent's expanders, list selection shades and accent shades are derived from the tokens.
- No colour is written into the XAML (`ThemeTests.Xaml_HasNoHardCodedColours`): allowed are only the token
  definitions, the logo (`Branding*.axaml`), Fluent's base palette in `App.axaml` and the in-game HUD overlay.
  Drawn controls read the theme when they draw (`ThemeService.Brush`: axis curves, marker colours).
- A theme of the user's own is a JSON file in `<data>\Themes` (`%LOCALAPPDATA%\JoyPro2\Themes`):
  `{"format": "joypro-theme", "version": 1, "name": ..., "base": "Light"|"Dark", "colors": {token: "#AARRGGBB"},
  "fonts": {"ui", "mono", "weight", "lineSpacing", "headingRatio"}, "textScale": 1.2}`. A colour the file lacks comes
  from its base. Import checks the file: no `format`, no name or broken JSON refuses it; unknown tokens and values
  that are no colour are left out with a note. Export writes the file for sharing.
- The editor: start from Light or Dark, a colour picker per token (`Views/Controls/ColorField.cs`: spectrum, hue,
  opacity, hex text, a few swatches), a live preview of a small main page (`Views/Controls/ThemePreviewHost.cs`: the
  edited colours and fonts as local resources), Reset colours, Save, Use this theme, Duplicate, Delete.
- Contrast check: `ThemeService.ContrastPairs` (text on its backgrounds, semi-transparent backgrounds composited on what
  they stand on): 4.5:1 for text, 3:1 for the accent and chart curves. "Fix the contrast" makes the text colours lighter
  or darker (keeping their hue) until all pairs pass; a text colour on several backgrounds gets one colour that reads on
  all of them; when no text colour can, the backgrounds move.

## Text size and UI scale

- Both work on top of Windows' display scaling (80 % to 200 %, kept in `ui-settings.json`: `textScale`, `uiScale`).
- Text size: every font size is a resource (`JP.FontSize.<n>`, JoyPro's size times the text size; headings from 15 up
  also times the theme's heading ratio; Fluent's `ControlContentThemeFontSize`). Ctrl+Plus / Ctrl+Minus / Ctrl+0.
  A theme may bring a text size; the user's own wins (Ctrl+0 / Reset goes back to the theme's).
- UI scale: the whole window through a layout transform (`UiScaleHost` in `MainWindow.axaml`); it lays out again.
- Pages scroll as a whole when they need more height than the window has (`Views/Controls/PageFit.cs`: a page gets at
  least JoyPro's page height times the text size), the profile header's actions wrap, the relation editor's search
  filters go below the search on a narrow pane (`Views/Controls/SearchRow.cs`).

## Fonts

- `JP.UiFont`: the theme's font first, then JoyPro's bundled stack (Bahnschrift, Segoe UI, the CJK font of the
  language, symbols), so a letter the chosen font lacks comes from the next font that has it. `JP.MonoFont`: key caps and
  input names. `JP.UiFontWeight`, `JP.LineSpacing`. A font of a theme that is not installed on this PC is left out
  (JoyPro's font stands in; the editor says so). No font name is written into the XAML
  (`ThemeTests.Xaml_HasNoHardCodedFontFamilies`).

## Tests

`ThemeTests` (save/load/import/export, broken files, persistence and the user's text size winning, contrast fix, text
size resources, no colour or font in the XAML), `ThemeContrastTests` (also a custom theme), `TextScaleLayoutTests`
(main pages at 150 % / 200 % text with 150 % Windows scaling, a serif and a wide font), `AppearanceRenderTests`
(`JOYPRO_APPEARANCE_OUT`), gate I10 (text size and font runs).
