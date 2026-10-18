# Localization

JoyPro's GUI speaks the Windows display language when JoyPro has it, otherwise English. Settings > Language picks
another one; the choice is kept in `ui-settings.json` (`"language"`, empty = the Windows language). The engine's
command line output stays English (it is a tool interface); engine texts the GUI shows can be translated at the GUI.

Languages: English (the source), the EU languages (Bulgarian, Croatian, Czech, Danish, Dutch, Estonian, Finnish, French,
German, Greek, Hungarian, Irish, Italian, Latvian, Lithuanian, Maltese, Polish, Portuguese, Romanian, Slovak, Slovenian,
Spanish, Swedish), Norwegian (Bokmål), Icelandic, Albanian, Bosnian, Serbian (Latin and Cyrillic), Macedonian, Russian,
Ukrainian, Belarusian, Turkish, Japanese, Korean, Chinese (Simplified and Traditional). The list and the plural rules:
`gui/JoyPro.Gui/Localization/Languages.cs`. Montenegrin Windows gets Serbian (Latin), Norwegian Nynorsk gets Bokmål.

## How it works (gettext style)

The English text is the key. A text without a translation stays English, so a new English text works at once and is
translated later.

- XAML (`xmlns:l="using:JoyPro.Gui.Localization"`):
  - `Text="{l:T 'Bind here'}"`
  - with values: `Text="{l:T 'Start: {0}', Arg0={Binding SourceLabel}}"` (Arg0..Arg3)
  - with a count: `Text="{l:T '{0} bind', Plural='{0} binds', Count={Binding N}}"`
  - a `'` in the text is written `\'`. These texts switch live when the language changes.
- C# (the whole GUI project has `using static JoyPro.Gui.Localization.Loc`):
  - `T("Bind here")`, `T("Exported {0} binds to {1}.", count, game)` (string.Format placeholders, never string
    concatenation or `$"..."` for a user text: the translation needs the whole sentence)
  - `Plural("{0} relation", "{0} relations", count)` (args optional; `{0}` is the count by default)
  - `Loc.Translate(engineLine)` / `Loc.TranslateLines(text)`: an English text from elsewhere (an engine status line):
    its translation, or the translation of a pattern it matches (`"Exported 12 binds."` <- `"Exported {0} binds."`).
- English values shown in a list (filter choices such as "All"): `ItemTemplate` with `{Binding Converter={x:Static l:TConverter.Instance}}`; the value stays English.
- Texts kept in fields (the status line, the record state): `Loc.Retranslate(text, previousLanguage)` on a language change finds the text in the previous language's strings and shows it in the new one.
- A language change raises `Loc.LanguageChanged`; the main view model then refreshes all its bindings, so getters that
  use `T()` follow. Texts already written (the status line) change with their next update.
- Strings: `gui/JoyPro.Gui/Localization/Strings/<code>.json`, embedded in the GUI: `{"English": "translation"}`; a
  plural text is a list of the language's forms (`{"{0} bind": ["{0} Belegung", "{0} Belegungen"]}`). Missing texts
  fall back over the language's fallback (Belarusian -> Russian, Bosnian/Serbian Latin -> Croatian, Serbian Cyrillic ->
  Serbian Latin, Traditional -> Simplified Chinese) to English. A broken translation (wrong placeholders) falls back to
  English instead of throwing.
- Glossary: `gui/JoyPro.Gui/Localization/glossary-en.json` (terms, meaning, style rules) and `glossary.json` (the terms
  in every language). Every translation uses these words.

- Static tables (made before the language is known) mark their texts with `N("...")` (gettext's N_: stays English,
  collected by the tool) and translate where shown: `T(value)` (settings sections, setup steps, export modes,
  progress lines `busyLabel: N("Saving profile...")`).
- Not translated: log lines, ids and keys, JSON field names, file and folder names, key names of the capture service,
  license texts, font names. A line with `// nonloc` is left out by the tool.
- Engine texts: the status line (`Failed: ...`), the busy panel's step and the profile history messages go through
  `Loc.Translate`.
- Fonts: the UI font is `{DynamicResource JP.UiFont}`: Bahnschrift, Segoe UI, then the CJK UI font of the language
  shown first (Yu Gothic UI / Microsoft YaHei UI / Microsoft JhengHei UI / Malgun Gothic), then Segoe UI Symbol and
  Segoe UI Emoji (arrows and media signs such as ↻ ⇕ ⏮ ⏺ that no text font has), `Languages.FontStack`; the HUD uses
  it too. The callout sheets and manual pages keep their own first font and add `Languages.FallbackFonts`
  (`Views/Controls/SheetFonts.cs`). Bahnschrift has every letter of the 38 languages (Latin Extended, Greek,
  Cyrillic); `FontCoverageTests` checks every character of every strings file and every language's own name against
  these chains (and the default font of the drawn canvases through Avalonia's match), so a new text with a character
  no font has fails the test. `JOYPRO_FONT_REPORT=<file>` lists which font draws how many characters per language.
- Tray host: the GUI writes the tray's texts in its language to `<settings>\tray-strings.json`
  (`Services/TrayStrings.cs`); `JoyPro.Watch.WatchText` reads them (English without the file).
- Layout: icon buttons wrap a long translation to a second line instead of cutting it (Styles/IconStyles.axaml).
  Render check: `LocalizationRenderTests` (`JOYPRO_LOC_OUT`, `JOYPRO_LOC_LANGS`, `JOYPRO_LOC_PAGES`).

## The tool (tools/loc/JoyPro.LocTool)

    dotnet build tools/loc/JoyPro.LocTool
    dotnet tools/loc/JoyPro.LocTool/bin/Debug/net9.0/JoyPro.LocTool.dll <command>

| Command | What it does |
| --- | --- |
| `extract [--out f]` | the string inventory: every text in .axaml and .cs that looks user-visible (wrapped or not), as JSON |
| `unwrapped [--file x] [--out f]` | the texts not yet going through `{l:T}` / `T()`, with file and line |
| `report [--wrapped]` | per language: how many inventory texts have no translation |
| `missing --lang de [--wrapped]` | the untranslated texts of a language as `{"English": ""}` |
| `chunks --lang de --dir d [--size n]` | the same in files of about n characters with a context hint, for translating |
| `validate --lang de --in f [--todo chunk]` | a filled-in file: every text there, placeholders kept |
| `merge --lang de --in f` | adds a filled-in file to `Strings/de.json` (keeps the English text's leading/trailing spaces) |
| `check` | placeholders of every translation in every language |
| `cyrillic` | makes `sr-Cyrl.json` from `sr-Latn.json` (transliteration; names, ids and placeholders stay) |
| `wrap-axaml [--file x] [--dry]` | literal XAML texts to `{l:T '...'}` (and `StringFormat` bindings to `Arg0`) |
| `wrap-cs [--file x] [--dry] [--left f]` | user texts in C# to `T("...", args)`; concatenations and `$"..."` become format texts; ids, keys, JSON fields, logs and static tables stay (listed in `--left` with the reason). Review the diff and run the tests |
| `merge --keep` | as merge, but a translation already in the file stays |

The inventory converts concatenations and interpolations to the format text the localization would use
(`"Exported " + n + " binds"` -> `"Exported {0} binds"`), so the translation is there when the code is changed to
`T("Exported {0} binds", n)`. Unused keys are harmless.

Adding texts: write them in English with `{l:T}` / `T()`. Then `missing --lang de --wrapped` (the test
`LocalizationTests.German_HasEveryLocalizedText` fails until German has them), translate with the glossary, `merge`;
for all languages `chunks` per language, translate, `validate`, `merge`, then `cyrillic`.

## Tests

`gui/JoyPro.Gui.Tests/LocalizationTests.cs`: system language mapping, plural rules (CLDR), format/fallback, engine
patterns, the extractor, German has every wrapped text, all strings files valid, the XAML texts switch live.
