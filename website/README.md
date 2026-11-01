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
| Code statistics (JoyPro 1 and 2) | `data/loc.json` (the maintainer's statistics script) |
| Hand-written pages | `content/**/*.md` |
| Game pages | `content/games/<slug>.md` (front matter `support:` = GameIds of `data/support.json`, `docs:` = files in `docs/games`, `category:`); the per-game docs become the game reference (`games/ref/`) |
| Screenshots | `assets/shots/*.webp` |

## Writing content

Pages in `content/` are Markdown with a front matter (`title`, `status: full|skeleton`, `order`, `summary`) and these
additions:

- `:::layer beginner|advanced|technical` ... `:::` — the three levels of a page (a switch on the page shows one).
- `:::ingame <title>` ... `:::` — what the player notices in the game.
- `:::video <id> "<title>"` — the slot of a topic video; put the YouTube id into `content/videos.json` (`{"<id>": "<youtube id>"}`) to embed it.
- `:::picture <id> "<title>"` — an in-game picture: the screenshot `<id>.webp` (or `<id>-light/-dark.webp`) in
  `assets/shots` when it exists, else a marked slot "In-game picture follows" (one per game page: `ingame-<slug>-controls`).
- `{{game-card}}` (game pages) — the support card of the page's titles from the matrix; `{{game-index}}` — the cards of
  every game page by category.
- `:::note`, `:::tip`, `:::warn` — callouts.
- `![caption](shot:<name>)` — a screenshot in the reader's theme (`<name>-light.webp` / `<name>-dark.webp`).
- `[[cli:dcs export]]`, `[[gui:export#ExportSelectedGamesCommand]]`, `[[doc:export]]`, `[[game:dcs]]`, `[[page:flows/check.html|text]]` — links that are checked at build time.
- `{{stat:jp2.tests}}`, `{{stat:games.handled}}`, `{{stat:cli.commands}}` ... — numbers filled in at build time.
- ` ```mermaid ` — diagrams.

The text of a single control or command goes into `content/gui/<page>.md` or `content/cli/*.md` as a block after
`<!-- entry: <key> -->` (key: the command, e.g. `ExportSelectedGamesCommand`, `bind:<Setting>`, or `dcs export`).
An engine command's text has the three layers (`:::layer beginner` with one example command, `advanced` with every
option in practice and its safety, `technical` with output, exit codes, files and related commands; no headings), and a
CLI page with such a text gets the detail-level switch. The files of `content/cli`: `main-flows.md` (import, export,
check), `profile-games-db.md`, `relations.md`, `binds-devices.md`, `games.md` (every game's import and export).
Facts only from the code; example paths use the demo user (`C:\Users\Pilot\...`).

Public text rules: describe behaviour only; no personal data. `check_public.ps1` runs the checker of
`tools/export_public.ps1` over the built site and these sources; generated texts drop sentences the rules do not allow.

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
