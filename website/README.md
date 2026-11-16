# JoyPro website and wiki

A static site generated from the source, so it always describes the current program. Pure HTML, CSS and JavaScript:
copy `website/dist` to any web server (or open `dist/index.html` from disk). No server-side code.

## Build

```powershell
pip install -r website/requirements.txt          # markdown-it-py, Pillow
python website/build.py                          # -> website/dist
python website/build.py --check                  # also the completeness gate (exit 1 when it fails)
python website/build.py --out <folder>           # into another folder (parallel builds)
powershell -ExecutionPolicy Bypass -File website\check_public.ps1   # public-content rules of tools\export_public.ps1
```

Mermaid (pinned version and SHA-256 in `build.py`) is downloaded once into `website/vendor` and copied into `dist`.
`dist` and `vendor` are not versioned.

## Sources

| What | From |
|---|---|
| Every GUI control (buttons, menu items, check boxes, switches, radio buttons) with text and tooltip | `gui/JoyPro.Gui/Views/**/*.axaml`, split into pages by the page sections of `MainWindow.axaml` |
| What a control does in the background | the command or setting behind it in `gui/JoyPro.Gui/**/*.cs`: its code comment and the engine commands it runs (followed through helper methods) |
| Every engine command and option | `engine/docs/ENGINE_CLI_WIKI.md` (generated from `engine --help`) |
| Reference, game and release pages | `docs/**/*.md` (not `docs/internal`, `docs/licensing-audit.md`) |
| Game support matrix | `data/support.json` (`python website/build.py --engine <engine.exe>` refreshes it from `engine games support --json`) |
| Code statistics (JoyPro 1 and 2) | `data/loc.json` (written by a statistics script outside the site build) |
| Hand-written pages | `content/**/*.md` |
| Game pages | `content/games/<slug>.md` (front matter `support:` = GameIds of `data/support.json`, `docs:` = files in `docs/games`, `category:`); the per-game docs become the game reference (`games/ref/`) |
| Screenshots | `assets/shots/*.webp` |

## Writing content

Pages in `content/` are Markdown with a front matter (`title`, `status: full|skeleton`, `order`, `summary`) and these
additions:

- `:::layer beginner|advanced|technical` ... `:::` — the three levels of a page (a switch on the page shows one).
- `:::ingame <title>` ... `:::` — what the player notices in the game.
- `:::video <id> "<title>"` — a topic video: embedded once its YouTube id is in `content/videos.json`
  (`{"<id>": "<youtube id>"}`); until then the page shows nothing and `project/wiki-status` lists the slot.
- `:::picture <id> "<title>"` — an in-game picture: the screenshot `<id>.webp` (or `<id>-light/-dark.webp`) in
  `assets/shots` when it exists; until then nothing (listed on `project/wiki-status`).
- `:::steps` with a numbered list — step cards in a row with arrows (a column on a phone). Up to 5 steps.
- `:::flow` with a markdown table — boxes and arrows from left to right: the header row names the columns, each row
  is one line, an empty first cell continues the box above (one box that leads to several rows). On a phone the
  boxes stack. Use it instead of a Mermaid fan-out: it never crosses lines and it translates like any table.
- `:::cards` with a list of `[Title](link): what it is` — link cards; `:::stats` with a list of `**number** what` —
  number tiles; `:::hero shot:<name>` — the home page banner (heading, paragraph, a list of links as buttons).
- `{{game-card}}` (game pages) — the support card of the page's titles from the matrix; `{{game-index}}` — the cards of
  every game page by category.
- `:::note`, `:::tip`, `:::warn` — callouts.
- `![caption](shot:<name>)` — a screenshot in the reader's theme (`<name>-light.webp` / `<name>-dark.webp`).
- `[[cli:dcs export]]`, `[[gui:export#ExportSelectedGamesCommand]]`, `[[doc:export]]`, `[[game:dcs]]`, `[[page:flows/check.html|text]]` — links that are checked at build time.
- `{{stat:jp2.tests}}`, `{{stat:games.handled}}`, `{{stat:cli.commands}}` ... — numbers filled in at build time.
- ` ```mermaid ` — diagrams (rules below).
- `{{device-list}}` — the supported devices from `EngineOnly_Runtime/DB/Devices` (the Supported devices page).

### Levels

Every reader starts at **Beginner** (remembered in the browser afterwards; `?show=all|advanced|technical` in the URL
overrides it once). At Beginner, the advanced and technical layers, the command line in the menu and in the search,
and the source facts under the GUI controls are hidden. Write file maps, engine commands and code paths into the
`technical` layer, never outside a layer.

### Diagrams

`build.py --check` fails on a diagram that breaks these rules (`diagram_problems`):

- Every diagram has one caption line: `%% caption: Solid: always. Dotted: only when ...` inside the Mermaid source (it
  is shown under the picture). When a chart has solid and dotted lines, the caption says what dotted means.
- At most **5 boxes in a row** (a left-to-right chain, or one rank of a top-down chart). Longer: split it, or draw it
  top-down. On a phone left-to-right charts turn top-down by themselves.
- At most **3 lines out of one box**. More: use a table, or a `:::flow`.

Also: short labels (a few words; a line break `<br/>` in a box is fine), no text on lines (put it into the box or the
caption), no engine command names outside the technical layer, plain `>` and `<` in labels (never `&gt;`). The site
draws Mermaid with the `base` theme coloured from the site's tokens, HTML labels, straight lines and spacing 40/56
(`site.js`). Look at every diagram you change: `python website/render_diagrams.py <built site> <folder> --pages <page>`
renders each one alone at desktop and phone width, light and dark (headless Edge, no window).

The text of a single control or command goes into `content/gui/<page>.md` or `content/cli/*.md` as a block after
`<!-- entry: <key> -->` (key: the command, e.g. `ExportSelectedGamesCommand`, `bind:<Setting>`, or `dcs export`).
An engine command's text has the three layers (`:::layer beginner` with one example command, `advanced` with every
option in practice and its safety, `technical` with output, exit codes, files and related commands; no headings), and a
CLI page with such a text gets the detail-level switch. The files of `content/cli`: `main-flows.md` (import, export,
check), `profile-games-db.md`, `relations.md`, `binds-devices.md`, `games.md` (every game's import and export).
Facts only from the code; example paths use the demo user (`C:\Users\Pilot\...`).

Public text rules: describe behaviour only; no personal data. `check_public.ps1` runs the checker of
`tools/export_public.ps1` over the built site and these sources; generated texts drop sentences the rules do not allow.

## Languages

The site speaks the app's languages (`gui/JoyPro.Gui/Localization/Languages.cs`: the same codes, names and fallbacks).
English stays at the root; every other language lives under `/<code>/` (`/de/gui/start.html`, `/zh-Hant/faq.html`).
Only the pages of the scope exist in every language (`website/i18n.py` `SCOPE`: home, Getting started, How do I,
flows, FAQ, game pages, GUI pages, About, JoyPro 1 vs 2); the command line, reference, game reference, release notes
and wiki status stay English and say so in the reader's language. F1's **More in the wiki** opens the page in the
GUI's language (`HelpCatalog.WikiUrl`).

- **Catalogs:** `website/i18n/<code>.json`, `{"English segment": "translation"}`, like `Strings/<code>.json`: the
  English text is the key. A segment is a heading, paragraph, list item, table cell, callout title, diagram label or
  caption, page title or summary, or a text of the frame. A translation keeps the Markdown (`**bold**`, `` `code` ``,
  `[[kind:target|text]]` with the same target, `[text](url)` with the same url, `{{stat:...}}`, `<br>`).
- **Lookup:** the language's catalog, then its fallbacks' (be → ru, bs / sr-Latn → hr, sr-Cyrl → sr-Latn, zh-Hant →
  zh-Hans), then the app's `Strings/<code>.json` (button names and the F1 help texts: a GUI page's controls show the
  F1 text in the reader's language with a link to the full English text), else English (marked `lang="en"`; a page
  that is mostly English says so once).
- **Never outdated:** when the English of a segment changes, its key changes and the page shows the new English
  until it is translated again. `python website/i18n.py prune --lang all` drops what no page uses any more.
- **Headings keep their English ids** in every language, so `#anchors` work everywhere.
- **Search:** one index per language (`assets/search-index.<code>.js`, the English pages included); Japanese,
  Chinese and Korean are searched by character pairs. CJK pages use the app's CJK font order.
- **Build:** `python website/build.py` builds every language (`--langs de,ja` some, `--langs none` English only).

The translators' workflow (the LocTool steps of the app; every command prints what it did):

```powershell
python website/i18n.py scope                                   # the pages of the scope with their English words
python website/i18n.py report                                  # per language: translated, via fallback, missing
#   (the inventory also builds the pages once as a language page: a generated or frame text only such a page shows
#    counts as a segment too, so a page can never stay partly English while report says 0 missing)
python website/i18n.py chunks --lang de --dir C:\Temp\loc\de --level beginner   # the missing segments, ~12000 characters per file
#   (each item: "en", "ctx" = page · level · heading, "terms" = how the app translates the button names in it)
#   fill in a JSON object {"English": "translation", ...} per chunk, with gui/JoyPro.Gui/Localization/glossary.json
python website/i18n.py validate --in done-01.json --todo C:\Temp\loc\de\chunk-01.json --lang de
python website/i18n.py merge --lang de --in done-01.json      # only valid translations go in
python website/i18n.py check                                   # every catalog (also part of the release checklist)
python website/i18n.py cyrillic                                # sr-Cyrl from sr-Latn (as the app does)
python website/build.py --langs de                             # look at /de/
```

`--level beginner` takes the beginner layer and the text outside the layers first; `--pages faq.html,how-do-i/`
narrows a run to some pages.

## Screenshots

```powershell
dotnet build gui/JoyPro.Gui.Tests
set JOYPRO_WEBSITE_SHOTS_OUT=C:\Temp\jpw\shots & dotnet test gui/JoyPro.Gui.Tests --no-build --filter WebsiteScreenshotTests
python website/build.py --shots C:\Temp\jpw\shots
```

Every page in light and dark at 1280x800, taller renders of the main pages and the Check, with the design renders'
demo profile (`WebsiteScreenshotTests.RenderWebsiteScreenshots`); the header shows a neutral demo path.
`RenderWebsiteDetailScreenshots` (same switch) renders what the GUI pages describe: every Settings section, the axis
response panel, Follow input, bind on the picture, the Layout page's modes and cheat sheets, the HUD designer, the HUD
over a game, the page for OBS (headless Edge) and a style gallery, the force feedback editor and inspector, the virtual
joystick functions, the Cockpits stick page, the Output panel, a confirmation window, the first-start setup and the five
setup wizard steps. `JOYPRO_WEBSITE_SHOTS_ONLY=design,settings,hud,layout,ffb,vjoy,wizard,setup` renders only some
groups. The test's own folders shown as text (profile, data, logs, sandbox user folders) are replaced by the demo
user's in the picture; whatever still shows this PC's user name or temp folder is listed in `leaks.txt` and fails the
test. Use a short neutral output folder anyway. Wider renders than 1600 px are scaled down to WebP by `--shots`.

To look at the built site: `powershell -ExecutionPolicy Bypass -File website\screenshot_pages.ps1 -Out <folder>`
(headless Edge, desktop and phone width, light and dark).

## Completeness gate

`build.py --check` fails on a build warning (a broken `[[...]]` link, an unknown engine command or control), when a
GUI page or an engine command has no page or entry, or when more controls or commands
are without any description, or with only source facts (no hand-written text), than `data/completeness-baseline.json`
allows (`*_missing`, `*_basic`; lower them when you write more). `project/wiki-status.html` lists the
numbers and what is still missing. Also run by `tools\quality_gate.ps1 -Website`.
