# The JoyPro website and wiki: a static site generated from the source, so it stays in sync with the program.
#
#   python website/build.py                  build into website/dist (pure static files, no server code)
#   python website/build.py --check          also run the completeness gate (exit 1 when it fails)
#   python website/build.py --shots <dir>    take the PNG screenshots of WebsiteScreenshotTests from <dir> (WebP, assets/shots)
#   python website/build.py --engine <exe>   refresh data/support.json from `engine games support --json`
#   python website/build.py --out <dir>      build into another folder (parallel builds, renders)
#
# Needs Python 3.10+, markdown-it-py and Pillow (website/requirements.txt). Mermaid (pinned, checked by SHA-256) is
# downloaded once into website/vendor (git-ignored) and shipped in dist.
#
# Sources (read-only):
#   gui/JoyPro.Gui/Views/**/*.axaml        every button, menu item, check box, switch and radio button with text and tooltip
#   gui/JoyPro.Gui/**/*.cs                 the command behind each control: method, its code comment, the engine calls
#   engine/docs/ENGINE_CLI_WIKI.md         every engine command and global option (the list `engine --help` prints)
#   docs/**/*.md                           the feature docs (reference pages), docs/games/*.md (game reference pages)
#   website/content/**/*.md                the hand-written pages and entry texts (beginner / advanced / technical),
#                                          content/games/*.md the game pages (front matter `support:` = the titles)
#   website/data/loc.json, support.json    code statistics (JoyPro 1 and 2) and the game support matrix
# See website/README.md.
from __future__ import annotations

import argparse
import hashlib
import html
import json
import os
import re
import shutil
import sys
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path

from markdown_it import MarkdownIt

sys.path.insert(0, str(Path(__file__).resolve().parent))
import i18n  # noqa: E402  (website/i18n.py: the languages, the catalogs and the translators' tool)

ROOT = Path(__file__).resolve().parent.parent
SITE = ROOT / "website"
CONTENT = SITE / "content"
DIST = SITE / "dist"
ASSETS = SITE / "assets"
DATA = SITE / "data"
VENDOR = SITE / "vendor"
GUI = ROOT / "gui" / "JoyPro.Gui"
REPO_URL = "https://github.com/Holdi601/JoystickProfiler2"
DISCORD = "https://discord.gg/yK4gQA9xgk"
MERMAID_VERSION = "11.4.1"
MERMAID_URL = f"https://cdn.jsdelivr.net/npm/mermaid@{MERMAID_VERSION}/dist/mermaid.min.js"
MERMAID_SHA256 = "a43bc1afd446f9c4cc66ac5dd45d02e8d65e26fc5344ec0ef787f88d6ddb6f9e"

# ----------------------------------------------------------------------------------------------------------------------
# Site structure
# ----------------------------------------------------------------------------------------------------------------------

# The GUI pages in the order of the bottom bar, plus the parts that are on every page.
# slug, title, screenshot, one-line summary
GUI_PAGES = [
    ("header", "Profile bar", "start", "The bar above every page: the open profile, Open / New, autosave, Back / Forward, Undo / Redo, History, More and Options."),
    ("start", "Start", "start", "Your profile at a glance, next steps, your games (import wizard), your devices, the watcher."),
    ("relations", "Relations", "relations", "Build relations: one function with its action in every game and aircraft."),
    ("binds", "Binds", "binds", "Give each relation its button or axis: record, modifiers, axis curves."),
    ("modifiers", "Modifiers", "modifiers", "Shift buttons: one button changes what other buttons do."),
    ("layout", "Layout", "layout", "Device pictures with your binds, cheat sheets, bind on the picture."),
    ("export", "Export", "export", "Write the profile into the games: the modes, the games, backups and Undo export."),
    ("check", "Check", "check", "Problems that would go wrong in the game, in game words, with one-click fixes."),
    ("hud", "HUD", "hud", "An overlay and stream page that shows your binds and live inputs."),
    ("ffb", "Force Feedback", "ffb", "Force feedback from the game's telemetry for FFB sticks."),
    ("vjoy", "Virtual Joystick", "vjoy", "vJoy / Xbox pad mode for games that read too few devices, virtual functions."),
    ("cockpits", "Cockpits", "cockpits", "The cockpit database: panels, controls and pictures per aircraft."),
    ("statistics", "Statistics", "statistics", "Opt-in: what you fly most, across all games."),
    ("settings", "Settings", "settings", "Appearance, games, privacy, confirmations, components, local AI and more."),
    ("advanced", "Advanced", "advanced", "Expert tools: run engine commands, databases, DCS defaults, backups."),
    ("output", "Output", None, "What JoyPro's engine printed: the details of the last action."),
    ("overlays", "Dialogs and notices", None, "What shows over every page: the game watcher, Move to another button, updates."),
    ("first-start", "First-start setup", None, "Your games: the window of the first start (also Settings > Games > Run setup again)."),
    ("setup-wizard", "Setup wizard", None, "Relations and binds for your watched games from your devices, roles and priorities."),
    ("help", "Help mode and tours", None, "F1 explains any control you click; the welcome, the tours of every page and the quick setup."),
]
GUI_TITLES = {slug: title for slug, title, _, _ in GUI_PAGES}

# The feature docs behind each GUI page (docs/<name>.md), linked from the page.
GUI_DOCS = {
    "header": ["profile-history", "ui/navigation", "gui-cli-parity"],
    "start": ["setup-wizard", "installs", "sync-watcher", "devices"],
    "relations": ["relation-builder", "auto-group", "natural-language-grouping", "suggest-additions", "ai-plans"],
    "binds": ["axis-curves", "ui/live-input", "auto-assign", "layout-with-ai"],
    "modifiers": ["relation-builder"],
    "layout": ["device-reference", "ui/layout-redesign", "devices"],
    "export": ["export", "backups", "reimport"],
    "check": ["ui/check"],
    "hud": ["hud"],
    "ffb": ["ffb-telemetry", "telemetry-recording"],
    "vjoy": ["virtual-joystick", "components"],
    "cockpits": ["cockpits"],
    "statistics": ["usage-statistics"],
    "settings": ["installs", "localization", "ui/themes", "ui/pages", "privacy", "components", "logging", "updates"],
    "advanced": ["gui-cli-parity", "backups"],
    "output": ["logging"],
    "overlays": ["sync-watcher", "updates"],
    "first-start": ["installs"],
    "setup-wizard": ["setup-wizard"],
    "help": ["ui/help-and-tours"],
}

# Which AXAML file belongs to which GUI page (MainWindow.axaml is split by its page sections).
VIEW_PAGES = {
    "CockpitsView.axaml": "cockpits",
    "StatisticsView.axaml": "statistics",
    "VirtualJoystickView.axaml": "vjoy",
    "VirtualFunctionEditorView.axaml": "vjoy",
    "GameSetupWindow.axaml": "first-start",
    "SetupWizardWindow.axaml": "setup-wizard",
    "LayoutWindow.axaml": "layout",
    "HudWindow.axaml": "hud",
    "HudSurface.axaml": "hud",
    "AircraftSelectorPanel.axaml": "relations",
    "BusyProgressPanel.axaml": "start",
    "CatalogBrowserPanel.axaml": "relations",
    "CheckProblemsPanel.axaml": "check",
    "ComponentCard.axaml": "settings",
    "ConfirmationSettingsPanel.axaml": "settings",
    "ListFilterBar.axaml": "relations",
    "ListFilterGamesPanel.axaml": "relations",
    "ListSortBar.axaml": "relations",
    "LocalAiStatusChip.axaml": "settings",
    "MissingReviewPanel.axaml": "relations",
    "SheetStyleEditor.axaml": "layout",
    "DrawingStyleEditor.axaml": "layout",
    "TelemetryRecordingPanel.axaml": "settings",
    "ReplaceDeviceWindow.axaml": "header",
    "ConfirmWindow.axaml": "overlays",
    "HelpLayer.axaml": "help",
}

STAGE_PAGES = {
    "IsStartStage": "start", "IsRelationsStage": "relations", "IsBindsStage": "binds", "IsModifiersStage": "modifiers",
    "IsLayoutStage": "layout", "IsExportStage": "export", "IsHudStage": "hud", "IsFfbTelemetryStage": "ffb",
    "IsVirtualJoystickStage": "vjoy", "IsCockpitsStage": "cockpits", "IsStatisticsStage": "statistics",
    "IsSettingsStage": "settings", "IsAdvancedStage": "advanced",
}

CONTROL_KINDS = ("Button", "ToggleButton", "SplitButton", "DropDownButton", "RepeatButton", "MenuItem", "CheckBox",
                 "RadioButton", "ToggleSwitch")
# What a reader calls each kind of control (the chip next to a control's name on the GUI pages; translated).
KIND_WORDS = {"Button": "Button", "ToggleButton": "Toggle button", "SplitButton": "Button with a menu",
              "DropDownButton": "Drop-down button", "RepeatButton": "Button", "MenuItem": "Menu entry",
              "CheckBox": "Check box", "RadioButton": "Option", "ToggleSwitch": "Switch"}

# Docs that stay out of the public site (the same rules as tools/export_public.ps1).
PRIVATE_DOCS = re.compile(r"^(internal/|licensing-audit\.md$|releases/README\.md$)")
PRIVATE_IMAGES = re.compile(r"^docs/ui/(?!relation-builder-)[^/]*\.png$", re.I)

LAYER_TITLES = {"beginner": "For beginners", "advanced": "Advanced", "technical": "Technical internals"}

# The side menu: what a beginner needs first; the power-user groups below a line (the command line is hidden at the
# Beginner level, site.js / site.css).
NAV_MAIN = ["Start", "Getting started", "How do I", "Games", "Help", "Main flows", "GUI pages"]
NAV_POWER = ["Engine (CLI)", "Reference", "Game reference", "Release notes", "Project", "Developers"]
NAV_OPEN = ("Start", "Getting started", "How do I", "Help")
NAV_EXTRA = {"Help": [("gui/help.html", "F1 help, tours and Easy words")]}

# Test and evaluation logs (testing, auto setup evaluation, device market checks): not on the site.
MAINTAINER_DOCS = re.compile(r"^(testing|auto-setup-eval|device-market-check[^/]*)[.]md$")
# Developer documents: built (links keep working) but listed under Developers, not with the user topics.
DEVELOPER_DOCS = re.compile(r"^(testing|auto-setup-eval|device-market-check[\w-]*|gui-cli-parity|localization|ui/layout-redesign|ui/pages|ui/navigation)\.md$")

# The feature columns of the support matrix (engine games support --json, Cells) as the game pages show them.
GAME_FEATURES = [
    ("import", "Import"), ("export", "Export"), ("keyboard", "Keyboard binds"), ("modifiers", "Modifiers"),
    ("refresh", "Command list built on your PC"), ("watcher", "Game watcher"), ("tuning", "Axis curves and dead zones"),
    ("settings", "Game control settings"), ("ffb", "In-game force feedback settings"), ("telemetry", "Force feedback from telemetry"),
    ("recording", "Telemetry recording"), ("vjoy", "Virtual joystick (vJoy)"), ("xbox", "Virtual Xbox pad"),
    ("functions", "Virtual functions"), ("wizard", "Setup wizard / Auto Assign"), ("cockpits", "Cockpits"),
    ("layout", "Cheat sheets"), ("e2e", "In-game test"),
]
# The categories of the game pages (front matter `category:`), in the order of the games index and the side menu.
GAME_CATEGORIES = [
    ("flight", "Flight simulators"), ("combat", "Combat flight"), ("space", "Space"), ("military", "Military sims"),
    ("arcade", "Arcade, one-device and pad games"), ("other", "Other games"),
]
LEVEL_WORDS = {"native": "native (its own bind files)", "virtual": "virtual joystick (vJoy)", "xbox": "virtual Xbox pad",
               "documented": "documented only"}

# ----------------------------------------------------------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------------------------------------------------------


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig", errors="replace")


def slug(text: str) -> str:
    s = re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")
    return s or "x"


def esc(text: str) -> str:
    return html.escape(text or "", quote=True)


def unescape_xaml(text: str) -> str:
    return html.unescape(text.replace("\\'", "'"))


def front_matter(text: str) -> tuple[dict, str]:
    meta: dict = {}
    if text.startswith("---"):
        end = text.find("\n---", 3)
        if end > 0:
            for line in text[3:end].strip().splitlines():
                if ":" in line:
                    k, v = line.split(":", 1)
                    meta[k.strip()] = v.strip().strip('"')
            text = text[end + 4:].lstrip("\n")
    return meta, text


def github(path: str, line: int | None = None) -> str:
    url = f"{REPO_URL}/blob/main/{path}"
    return url + (f"#L{line}" if line else "")


def rel(from_url: str, to_url: str) -> str:
    """Relative link from one site page (path like 'gui/start.html') to another ('cli/dcs.html#x')."""
    depth = from_url.count("/")
    return "../" * depth + to_url


# Public text rules (tools/export_public.ps1): the site states behaviour only. Sentences of tooltips and code comments
# that give protection, legal or anti-cheat reasons, or that note an internal decision, are left out of the site.
NOT_PUBLIC = re.compile(
    r"(?i)(?<!\bEFS )(encrypt|decrypt)|obfuscat|\bDRM\b|\bEULA\b|copy[- ]?protect|\blegal(ly)?\b(?!\s+state)"
    r"|\bcopyright(ed)?\b(?!\s+(notice|holder))|infring|\bDMCA\b|\blawsuit|anti-?cheat"
    r"|\buser (decision|approved|said|wish)|\buser's decision|\bthe user (approved|said|decided)\b|\btesters?\b|\btester's"
    r"|\b(dev|test|development|author'?s)[- ]PC\b|reverse[- ]?engineer|\bdecompil|\blicensed (with|to|by)\b"
    r"|\b(has|with) no licen[cs]e\b|all rights reserved|licen[cs]e terms|for (themselves|yourself) only"
    r"|[E]:\\Games\\|C:\\Temp\\jp2[-]")  # a game drive and the test kit's run folders


def public_text(text: str) -> str:
    if not text or not NOT_PUBLIC.search(text):
        return text
    text = re.sub(r"\s*\([^()]*\)", lambda m: "" if NOT_PUBLIC.search(m.group(0)) else m.group(0), text)
    parts = re.split(r"(?<=[.;!?])\s+", text)
    return " ".join(p for p in parts if not NOT_PUBLIC.search(p)).strip()


# ----------------------------------------------------------------------------------------------------------------------
# GUI extraction
# ----------------------------------------------------------------------------------------------------------------------

LT = re.compile(r"\{l:T '((?:[^'\\]|\\.)*)'")
TAG = re.compile(r"<(" + "|".join(CONTROL_KINDS) + r")\b((?:\s+[\w.:]+\s*=\s*\"[^\"]*\")*)\s*(/?)>", re.S)
ATTR = re.compile(r"([\w.:]+)\s*=\s*\"([^\"]*)\"")


@dataclass
class VmMember:
    name: str
    file: str
    line: int
    comment: str
    engine: list[str] = field(default_factory=list)
    cls: str = ""  # the class that declares it (partial classes: one name)


@dataclass
class GuiEntry:
    page: str
    kind: str
    label: str
    tooltip: str
    command: str
    binding: str
    click: str
    name: str
    file: str
    line: int
    key: str = ""
    anchor: str = ""
    count: int = 1
    vm: VmMember | None = None
    hand: str = ""
    scope: str = ""  # the class of the control's DataContext (x:DataType / DataTemplate DataType / DataContext binding)

    @property
    def level(self) -> str:
        if self.hand:
            return "full"
        if self.tooltip or (self.vm and self.vm.comment):
            return "basic"
        return "missing"


def text_of(value: str) -> str:
    m = LT.search(value)
    if m:
        return unescape_xaml(m.group(1))
    if value.startswith("{"):
        return ""
    return unescape_xaml(value)


def binding_of(value: str) -> str:
    m = re.match(r"\{Binding\s+(?:Path=)?!?([\w.#]+)", value)
    return m.group(1) if m else ""


def main_window_regions(lines: list[str]) -> list[tuple[int, str]]:
    """(start line, page) for MainWindow.axaml, sorted; line numbers are 1-based."""
    regions = [(1, "header")]
    for i, line in enumerate(lines, 1):
        m = re.search(r"IsVisible=\"\{Binding (Is\w+Stage)\}\"", line)
        if m and m.group(1) in STAGE_PAGES and i > 300:
            regions.append((i, STAGE_PAGES[m.group(1)]))
        if "<!-- Step 1: check -->" in line:
            regions.append((i, "check"))
        if "<!-- Step 2: how (export mode" in line:
            regions.append((i, "export"))
        if "<!-- The check's problems:" in line:
            regions.append((i, "check"))
        if "l:T 'Game config backups'" in line:
            regions.append((i - 4, "export"))
        if "<!-- HUD Designer" in line:
            regions.append((i, "export-end"))
        if "<!-- Game config watcher:" in line:
            regions.append((i, "overlays"))
        if "<!-- Drag to give the engine output" in line:
            regions.append((i, "output"))
        if "<!-- Bottom bar:" in line:
            regions.append((i, "navigation"))
    regions.sort()
    fixed = []
    for start, page in regions:
        fixed.append((start, "export" if page == "export-end" else page))
    return fixed


def page_at(regions: list[tuple[int, str]], line: int) -> str:
    page = regions[0][1]
    for start, p in regions:
        if start <= line:
            page = p
    return page


def extract_gui_controls(prop_types: dict[tuple[str, str], str] | None = None) -> list[GuiEntry]:
    entries: list[GuiEntry] = []
    for path in sorted((GUI / "Views").rglob("*.axaml")):
        text = read(path)
        lines = text.splitlines()
        relpath = path.relative_to(ROOT).as_posix()
        regions = main_window_regions(lines) if path.name == "MainWindow.axaml" else None
        scopes, root_scope = axaml_scopes(text, prop_types or {})
        for m in TAG.finditer(text):
            kind, attrs_text, selfclose = m.group(1), m.group(2), m.group(3)
            attrs = dict(ATTR.findall(attrs_text))
            line = text.count("\n", 0, m.start()) + 1
            label = public_text(text_of(attrs.get("Content", "") or attrs.get("Header", "")))
            if not label and not selfclose:
                # Content as child elements: the first translated text before the closing tag.
                close = text.find("</" + kind + ">", m.end())
                inner = text[m.end(): close if close > 0 else m.end() + 600][:1500]
                lt = LT.search(inner)
                if lt:
                    label = unescape_xaml(lt.group(1))
            if not label:
                label = text_of(attrs.get("OnContent", "")) or text_of(attrs.get("AutomationProperties.Name", ""))
            tooltip = public_text(text_of(attrs.get("ToolTip.Tip", "")))
            command = binding_of(attrs.get("Command", ""))
            scope = scopes.get(m.start(), "")
            if command.startswith("#"):
                # {Binding #RootWindow.DataContext.FooCommand}: the file's root DataContext.
                scope = root_scope
                command = command.split(".")[-1] if ".DataContext." in command else ""
            elif "." in command:
                # {Binding DeviceReference.FooCommand}: the class of the property path.
                scope = path_type(scope, command.split(".")[:-1], prop_types or {})
                command = command.split(".")[-1]
            binding = binding_of(attrs.get("IsChecked", ""))
            click = attrs.get("Click", "")
            name = attrs.get("x:Name", "")
            if regions:
                page = page_at(regions, line)
            else:
                page = VIEW_PAGES.get(path.name, "overlays")
            if page == "navigation":
                # The bottom bar: each page button is documented on its page; Check and Output are pages of their own.
                if name == "Nav_check":
                    page = "check"
                elif name == "EngineOutputQuickToggle":
                    page = "output"
                else:
                    page = "header"
            if not (label or tooltip or command or binding or click):
                continue
            entries.append(GuiEntry(page, kind, label, tooltip, command, binding, click, name, relpath, line, scope=scope))
    # One entry per page and identity (the same command shows in several places of a page: counted once).
    merged: dict[tuple[str, str], GuiEntry] = {}
    for e in entries:
        ident = e.command or (("bind:" + e.binding) if e.binding else "") or (("click:" + e.click) if e.click else "") or e.name or ("label:" + e.label)
        if e.command and e.command in ("RunCommand",):
            ident += ":" + e.label
        e.key = ident
        k = (e.page, ident)
        if k in merged:
            merged[k].count += 1
            if not merged[k].tooltip and e.tooltip:
                merged[k].tooltip = e.tooltip
            if not merged[k].label and e.label:
                merged[k].label = e.label
        else:
            merged[k] = e
    result = list(merged.values())
    used: set[str] = set()
    for e in result:
        base = slug(e.key.replace("Command", "").replace("bind:", "").replace("click:", "").replace("label:", "")) or "control"
        a = base
        n = 2
        while (e.page, a) in used:
            a = f"{base}-{n}"
            n += 1
        used.add((e.page, a))
        e.anchor = a
    return result


# ----------------------------------------------------------------------------------------------------------------------
# Help mode catalog (F1 in the GUI): every control's short text, made from its wiki entry
# ----------------------------------------------------------------------------------------------------------------------

HELP_CATALOG = GUI / "Resources" / "help-controls.json"


def help_sentences(text: str) -> list[str]:
    """The plain sentences of a wiki entry: no code, links as their text, no details in parentheses, no engine lines."""
    t = re.sub(r"```.*?```", "", text or "", flags=re.S)
    t = re.sub(r"^:::.*$", "", t, flags=re.M)
    t = re.sub(r"^\s*[|!#].*$", "", t, flags=re.M)
    # Inline code in pairs, left to right: engine command lines and long code go, short code (file names, keys) stays.
    t = re.sub(r"`([^`]*)`", lambda m: "" if m.group(1).startswith("engine ") or len(m.group(1)) >= 40 else m.group(1), t)
    t = re.sub(r"\[\[[^|\]]*\|([^\]]*)\]\]", r"\1", t)
    t = re.sub(r"\[\[(?:[a-z]+:)?([^\]]*)\]\]", r"\1", t)
    t = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", t)
    t = re.sub(r"\*\*|__|(?<!\w)\*(?!\s)|(?<=\S)\*(?!\w)", "", t)
    t = re.sub(r"^\s*[-*]\s+", "", t, flags=re.M)
    t = t.replace("\n", " ")
    t = re.sub(r"\s*\((?:[^()]|\([^()]*\))*\)", "", t)
    t = re.sub(r"\s+", " ", t).strip()
    t = re.sub(r"\s+([.,:;!?])(?=\s|$)", r"\1", t)
    t = re.sub(r"([:,;])(?=[.!?])", "", t)
    parts = [x.strip() for x in re.split(r"(?<=[.!?])\s+(?=[A-Z\"'0-9])", t) if x.strip()]
    out = []
    for x in parts:
        if re.match(r"(?i)^engine\b|^see\b|^\(?engine:", x) or len(x) < 8:
            continue
        if x[-1] not in ".!?":
            x += "."
        out.append(x[0].upper() + x[1:])
    # A first sentence that only says the default ("Off by default.") goes with the next one.
    while len(out) > 1 and len(out[0]) < 40:
        out[0:2] = [out[0] + " " + out[1]]
    return out


EASY_LINE = re.compile(r"<!--\s*easy:\s*(.*?)\s*-->", re.S)


def easy_line(text: str) -> str:
    """The entry's plain first sentence for F1's help ('<!-- easy: ... -->'): at most 12 words, for a child."""
    m = EASY_LINE.search(text or "")
    return re.sub(r"\s+", " ", m.group(1)).strip() if m else ""


def easy_problems(site: "Site") -> list[str]:
    """Plain first sentences that are too long: every sentence at most 12 words."""
    out = []
    for e in site.gui:
        line = easy_line(e.hand)
        for sentence in re.split(r"(?<=[.!?])\s+", line):
            if len(sentence.split()) > 12:
                out.append(f"{e.page}/{e.key}: easy line over 12 words: {sentence}")
    return out


def help_catalog(site: "Site") -> list[dict]:
    rows = []
    for e in sorted(site.gui, key=lambda e: (e.page, e.key)):
        hand = re.sub(r"<!--.*?-->", "", e.hand or "", flags=re.S)
        s = help_sentences(hand) or help_sentences(e.tooltip) or help_sentences(e.vm.comment if e.vm else "")
        what = s[0] if s else ""
        nxt = s[1] if len(s) > 1 and len(s[1]) <= 170 else ""
        row = {"p": e.page, "k": e.key, "kind": e.kind, "a": e.anchor, "w": what}
        if nxt:
            row["x"] = nxt
        easy = easy_line(e.hand)
        if easy:
            row["e"] = easy
        for name, value in (("c", e.command), ("b", e.binding), ("ck", e.click), ("n", e.name), ("l", e.label), ("t", e.tooltip)):
            if value:
                row[name] = value
        rows.append(row)
    return rows


def help_catalog_text(site: "Site") -> str:
    return json.dumps(help_catalog(site), ensure_ascii=False, indent=0).replace("\n", "").replace("},{", "},\n{") + "\n"


METHOD = re.compile(r"^[ \t]*(?:(?:private|public|internal|protected|static|async|override|partial|virtual|sealed)\s+)+"
                    r"[\w<>\[\]?,. ]+?\s+(\w+)\s*\(", re.M)
ENGINE_ARGS = re.compile(r"[\[{]\s*\"([a-z][a-z0-9-]*)\"\s*,\s*\"([a-z][a-z0-9-]*)\"(?:\s*,\s*\"([a-z][a-z0-9-]*)\")?")


def leading_comment(lines: list[str], index: int) -> str:
    """The // comment lines directly above line 'index' (0-based), skipping attribute lines."""
    out: list[str] = []
    i = index - 1
    while i >= 0:
        s = lines[i].strip()
        if s.startswith("[") and s.endswith("]"):
            i -= 1
            continue
        if s.startswith("//"):
            out.insert(0, s[2:].strip())
            i -= 1
            continue
        break
    return public_text(" ".join(out).strip())


def extract_vm(cli_areas: set[str]) -> tuple[dict[str, VmMember], dict[str, VmMember]]:
    """Commands (FooCommand -> member) and observable properties (Foo -> member) of every view model."""
    commands: dict[str, list[VmMember]] = {}
    props: dict[str, list[VmMember]] = {}
    methods: dict[str, tuple[str, list[str]]] = {}  # name -> (body, direct engine calls)
    sources = [p for p in GUI.rglob("*.cs") if "/obj/" not in p.as_posix() and "/bin/" not in p.as_posix()]
    pending_cmds: list[tuple[str, str, str, int, str, str]] = []
    defs: dict[str, int] = {}
    for path in sources:
        text = read(path)
        lines = text.splitlines()
        relpath = path.relative_to(ROOT).as_posix()
        spans = class_spans(text)
        for m in PROP_DECL.finditer(text):
            cls = class_at(spans, m.start())
            if cls:
                PROP_TYPES[(cls, m.group(2))] = m.group(1)
        found = list(METHOD.finditer(text))
        for k, m in enumerate(found):
            name = m.group(1)
            # The body: up to the next member declaration (robust against braces in strings and char literals).
            end = found[k + 1].start() if k + 1 < len(found) else len(text)
            body = text[m.end():end]
            calls = []
            for a in ENGINE_ARGS.finditer(body):
                if a.group(1) in cli_areas:
                    calls.append(" ".join(x for x in a.groups() if x))
            defs[name] = defs.get(name, 0) + 1
            prev = methods.get(name)
            if prev:
                methods[name] = (prev[0] + body, prev[1] + calls)
            else:
                methods[name] = (body, calls)
            line_no = text.count("\n", 0, m.start()) + 1
            # [RelayCommand] above?
            j = line_no - 2
            is_cmd = False
            while j >= 0 and lines[j].strip().startswith("["):
                if lines[j].strip().startswith("[RelayCommand"):
                    is_cmd = True
                j -= 1
            if is_cmd:
                cmd = (name[:-5] if name.endswith("Async") else name) + "Command"
                pending_cmds.append((cmd, name, relpath, line_no, leading_comment(lines, line_no - 1), class_at(spans, m.start())))
        for m in re.finditer(r"\[ObservableProperty\][^\n]*\n(?:\s*\[[^\n]*\n)*\s*(?:private|public|protected)\s+[\w<>?\[\],. ]+?\s+_?(\w+)\s*[;=]", text):
            field_name = m.group(1)
            prop = field_name[0].upper() + field_name[1:]
            line_no = text.count("\n", 0, m.start()) + 1
            cls = class_at(spans, m.start())
            props.setdefault(prop, []).append(VmMember(prop, relpath, line_no, leading_comment(lines, line_no - 1), cls=cls))
            type_m = re.search(r"(?:private|public|protected)\s+([\w<>.]+)\??\s+_?" + re.escape(field_name) + r"\b", m.group(0))
            if cls and type_m:
                PROP_TYPES[(cls, prop)] = type_m.group(1)

    method_names = set(methods)
    call_re = re.compile(r"\b(\w+)\s*\(")

    def engine_calls(name: str, depth: int, seen: set[str]) -> list[str]:
        if name in seen or name not in methods:
            return []
        seen.add(name)
        body, calls = methods[name]
        out = list(calls)
        if depth > 0:
            for c in dict.fromkeys(call_re.findall(body)):
                # only helpers with one definition (a common name such as Refresh would pull in unrelated calls)
                if c in method_names and c != name and defs.get(c) == 1:
                    out += engine_calls(c, depth - 1, seen)
        return out

    for cmd, method, relpath, line_no, comment, cls in pending_cmds:
        calls = []
        for c in engine_calls(method, 2, set()):
            if c not in calls:
                calls.append(c)
        commands.setdefault(cmd, []).append(VmMember(cmd, relpath, line_no, comment, calls[:12], cls=cls))
    return commands, props


# ----------------------------------------------------------------------------------------------------------------------
# Which class a binding means: several view models and items have a member of the same name (CancelCommand,
# IsExpanded, IsSelected ...); the AXAML scope of the control (x:DataType, a DataTemplate's DataType, a DataContext
# binding) says which one.
# ----------------------------------------------------------------------------------------------------------------------

PROP_DECL = re.compile(r"^[ \t]*public\s+(?:static\s+|override\s+|virtual\s+|new\s+)*([\w.<>]+)\??\s+(\w+)\s*(?:\{|=>)", re.M)
PROP_TYPES: dict[tuple[str, str], str] = {}  # (class, property) -> type name, filled by extract_vm


def skip_string(text: str, i: int) -> int:
    """The index after the C# string or char literal that starts at i (regular, verbatim, interpolated, raw)."""
    n = len(text)
    j = i
    interpolated = verbatim = False
    while j < n and text[j] in "$@":
        interpolated |= text[j] == "$"
        verbatim |= text[j] == "@"
        j += 1
    if text.startswith('"""', j):
        k = text.find('"""', j + 3)
        while k >= 0 and text.startswith('""""', k):
            k += 1
        return n if k < 0 else k + 3
    if j < n and text[j] == "'":
        k = j + 1
        while k < n and text[k] != "'":
            k += 2 if text[k] == "\\" else 1
        return k + 1
    j += 1  # the opening quote
    while j < n:
        c = text[j]
        if verbatim and text.startswith('""', j):
            j += 2
        elif not verbatim and c == "\\":
            j += 2
        elif c == '"':
            return j + 1
        elif interpolated and text.startswith("{{", j):
            j += 2
        elif interpolated and c == "{":
            # An interpolation hole: code up to its closing brace (strings in it included).
            depth = 1
            j += 1
            while j < n and depth:
                if text[j] == '"' or (text[j] in "$@" and text[j + 1:j + 2] in ('"', '$', '@')):
                    j = skip_string(text, j)
                    continue
                depth += {"{": 1, "}": -1}.get(text[j], 0)
                j += 1
        else:
            j += 1
    return n


TYPE_KEYWORD = re.compile(r"\b(?:class|record|struct|interface)\s+(\w+)")


def class_spans(text: str) -> list[tuple[int, int, str]]:
    """(start, end, name) of every class body in a C# file, by brace matching that skips comments, strings and chars."""
    spans: list[tuple[int, int, str]] = []
    stack: list[tuple[int, str]] = []
    pending = ""
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if text.startswith("//", i):
            i = text.find("\n", i)
            i = n if i < 0 else i
            continue
        if text.startswith("/*", i):
            i = text.find("*/", i + 2)
            i = n if i < 0 else i + 2
            continue
        if c in "\"'" or (c in "$@" and text[i + 1:i + 2] in ('"', '$', '@')):
            i = skip_string(text, i)
            continue
        if (c.isalpha() or c == "_") and (i == 0 or not (text[i - 1].isalnum() or text[i - 1] == "_")):
            m = TYPE_KEYWORD.match(text, i)
            if m:
                pending = m.group(1)
                i = m.end()
                continue
            while i < n and (text[i].isalnum() or text[i] == "_"):
                i += 1
            continue
        if c == ";":
            pending = ""
        elif c == "{":
            stack.append((i, pending))
            pending = ""
        elif c == "}" and stack:
            start, name = stack.pop()
            if name:
                spans.append((start, i, name))
        i += 1
    return spans


def class_at(spans: list[tuple[int, int, str]], offset: int) -> str:
    """The innermost class whose body holds the offset."""
    best = ("", -1)
    for start, end, name in spans:
        if start <= offset <= end and start > best[1]:
            best = (name, start)
    return best[0]


def type_name(value: str) -> str:
    """'vm:MainWindowViewModel' / '{x:Type models:Foo}' -> the class name."""
    value = value.strip()
    m = re.match(r"\{x:Type\s+(?:TypeName=)?([\w:.]+)\s*\}", value)
    if m:
        value = m.group(1)
    return value.split(":")[-1].split(".")[-1] if value and not value.startswith("{") else ""


def path_type(scope: str, segments: list[str], prop_types: dict[tuple[str, str], str]) -> str:
    """The class a property path leads to from the scope ('' when a step is unknown)."""
    for s in segments:
        if not scope:
            return ""
        t = prop_types.get((scope, s), "")
        scope = re.sub(r"<.*", "", t).split(".")[-1]
    return scope


AXAML_TAG = re.compile(r"<(/?)([\w:.]+)((?:\s+[\w.:]+\s*=\s*\"[^\"]*\")*)\s*(/?)>", re.S)


def axaml_scopes(text: str, prop_types: dict[tuple[str, str], str]) -> tuple[dict[int, str], str]:
    """The DataContext class at every element of an AXAML file (by the element's start offset), and the root's class."""
    # Comments out, offsets kept.
    clean = re.sub(r"<!--.*?-->", lambda m: " " * len(m.group(0)), text, flags=re.S)
    scopes: dict[int, str] = {}
    stack: list[str] = []
    root = None
    for m in AXAML_TAG.finditer(clean):
        closing, tag, attrs_text, selfclose = m.group(1), m.group(2), m.group(3), m.group(4)
        if closing:
            if stack:
                stack.pop()
            continue
        attrs = dict(ATTR.findall(attrs_text))
        scope = stack[-1] if stack else ""
        if "x:DataType" in attrs:
            scope = type_name(attrs["x:DataType"])
        elif tag.endswith("DataTemplate") and "DataType" in attrs:
            scope = type_name(attrs["DataType"])
        elif "DataContext" in attrs:
            dc = attrs["DataContext"]
            b = re.match(r"\{Binding\s+(?:Path=)?([\w.]+)\s*\}$", dc)
            if b:
                scope = path_type(scope, b.group(1).split("."), prop_types)
            elif dc.startswith("{Binding}"):
                pass
            else:
                scope = ""
        if root is None:
            root = scope
        scopes[m.start()] = scope
        if not selfclose:
            stack.append(scope)
    return scopes, root or ""


def pick_member(members: list[VmMember] | None, scope: str) -> VmMember | None:
    """The member of the scope's class; one of a single class as it is; unknown scope: the last one found (as before)."""
    if not members:
        return None
    if len(members) == 1:
        return members[0]
    for m in members:
        if scope and m.cls == scope:
            return m
    classes = {m.cls for m in members}
    if len(classes) == 1:
        return members[-1]
    return members[-1]


def self_check_scopes() -> list[str]:
    """The scope resolution on a small AXAML and C# sample (build.py has no test runner; --check fails on a problem)."""
    problems: list[str] = []
    cs = ('public sealed partial class MainVm { public ItemVm Child { get; } = new(); [RelayCommand] private void Cancel() { var s = "}"; } }\n'
          'public sealed class ItemVm { [RelayCommand] private void Cancel() { } public bool IsOn { get; set; } }\n'
          'public sealed class RowItem { [RelayCommand] private void Cancel() { /* } */ } }\n')
    spans = class_spans(cs)
    if class_at(spans, cs.index("RelayCommand] private void Cancel() { var")) != "MainVm":
        problems.append("class_at: MainVm's Cancel")
    if class_at(spans, cs.index("/* }")) != "RowItem":
        problems.append("class_at: RowItem's Cancel")
    types = {("MainVm", "Child"): "ItemVm"}
    axaml = ('<UserControl x:DataType="vm:MainVm">\n'
             '  <Button Command="{Binding CancelCommand}" />\n'
             '  <!-- <Border x:DataType="vm:Nothing"> -->\n'
             '  <StackPanel DataContext="{Binding Child}">\n'
             '    <Button Command="{Binding CancelCommand}" />\n'
             '  </StackPanel>\n'
             '  <ItemsControl><ItemsControl.ItemTemplate><DataTemplate x:DataType="models:RowItem">\n'
             '    <Button Command="{Binding CancelCommand}" />\n'
             '    <Button Command="{Binding #Root.DataContext.CancelCommand}" />\n'
             '  </DataTemplate></ItemsControl.ItemTemplate></ItemsControl>\n'
             '  <ContentControl><ContentControl.ContentTemplate><DataTemplate DataType="{x:Type vm:ItemVm}"><CheckBox IsChecked="{Binding IsOn}" /></DataTemplate></ContentControl.ContentTemplate></ContentControl>\n'
             '</UserControl>')
    scopes, root = axaml_scopes(axaml, types)
    buttons = [m.start() for m in re.finditer(r"<Button ", axaml)]
    got = [scopes.get(b, "?") for b in buttons]
    if got != ["MainVm", "ItemVm", "RowItem", "RowItem"] or root != "MainVm":
        problems.append(f"axaml_scopes: {got}, root {root}")
    if scopes.get(axaml.index("<CheckBox"), "?") != "ItemVm":
        problems.append("axaml_scopes: DataTemplate DataType")
    members = [VmMember("CancelCommand", "a.cs", 1, "", cls="MainVm"), VmMember("CancelCommand", "b.cs", 2, "", cls="ItemVm"),
               VmMember("CancelCommand", "c.cs", 3, "", cls="RowItem")]
    for scope in ("MainVm", "ItemVm", "RowItem"):
        if pick_member(members, scope).cls != scope:
            problems.append("pick_member: " + scope)
    return ["scope resolution self-check: " + p for p in problems]


# ----------------------------------------------------------------------------------------------------------------------
# CLI extraction
# ----------------------------------------------------------------------------------------------------------------------


@dataclass
class CliCommand:
    signature: str
    note: str
    area: str
    key: str
    anchor: str = ""
    body: str = ""
    options: list[tuple[str, list[str], str]] = field(default_factory=list)  # flag, values, placeholder
    flag_docs: dict[str, str] = field(default_factory=dict)
    hand: str = ""
    gui: list[GuiEntry] = field(default_factory=list)

    @property
    def what(self) -> str:
        m = re.search(r"What it does:\s*(.+)", self.body)
        return m.group(1).strip() if m else ""

    @property
    def level(self) -> str:
        if self.hand:
            return "full"
        if self.note or (self.what and "See examples" not in self.what):
            return "basic"
        return "missing"


def parse_cli() -> tuple[list[tuple[str, str]], list[CliCommand]]:
    text = read(ROOT / "engine" / "docs" / "ENGINE_CLI_WIKI.md")
    # Global options block
    glob: list[tuple[str, str]] = []
    gm = re.search(r"### Global Options\s*```text\n(.*?)```", text, re.S)
    if gm:
        for line in gm.group(1).splitlines():
            m = re.match(r"\s+(--\S+(?:\s+(?:<[^>]+>|\S+\|\S+))?)\s{2,}(.*)", line)
            if m:
                glob.append((m.group(1), m.group(2).strip()))
    cm = re.search(r"### Commands\s*```text\n(.*?)```", text, re.S)
    commands: list[CliCommand] = []
    for line in (cm.group(1) if cm else "").splitlines():
        line = line.strip()
        if not line.startswith("engine "):
            continue
        note = ""
        nm = re.match(r"(.*?\S)\s{3,}\((.*)\)\s*$", line)
        sig = line
        if nm:
            sig, note = nm.group(1), nm.group(2)
        tokens = sig.split()
        key_tokens = []
        for t in tokens[1:]:
            if t[0] in "[<-|(" or not re.match(r"^[a-z0-9][a-z0-9-]*$", t):
                break
            key_tokens.append(t)
        area = key_tokens[0] if key_tokens else "engine"
        commands.append(CliCommand(sig, note, area, " ".join(key_tokens) or "engine"))
    # Details per command
    sections = re.split(r"^### `(engine [^`]*)`\s*$", text, flags=re.M)
    details: dict[str, str] = {}
    for i in range(1, len(sections) - 1, 2):
        head = sections[i].strip()
        body = re.split(r"^##\s", sections[i + 1], flags=re.M)[0].strip()
        details[re.sub(r"\s+", " ", head)] = body
    used: set[str] = set()
    for c in commands:
        full = re.sub(r"\s+", " ", c.signature + (f"   ({c.note})" if c.note else ""))
        c.body = details.get(full) or details.get(re.sub(r"\s+", " ", c.signature), "")
        if not c.body:
            for head, body in details.items():
                if head.startswith(re.sub(r"\s+", " ", c.signature)):
                    c.body = body
                    break
        a = slug(c.key)
        n = 2
        while a in used:
            a = f"{slug(c.key)}-{n}"
            n += 1
        used.add(a)
        c.anchor = a
        for om in re.finditer(r"(--[a-z0-9][a-z0-9-]*)(?:\s+(<[^>]+>|[A-Za-z0-9.]+(?:\|[A-Za-z0-9.-]+)+))?", c.signature):
            flag, val = om.group(1), om.group(2) or ""
            if any(o[0] == flag for o in c.options):
                continue
            values = val.split("|") if "|" in val and not val.startswith("<") else []
            c.options.append((flag, values, val if val.startswith("<") else ""))
        for fm in re.finditer(r"^- `(--[a-z0-9-]+|<[^>]+>)`: (.+)$", c.body, re.M):
            c.flag_docs[fm.group(1)] = fm.group(2).strip()
    return glob, commands


# ----------------------------------------------------------------------------------------------------------------------
# Markdown rendering with the site's extensions
# ----------------------------------------------------------------------------------------------------------------------


def mark_fallbacks(out: str) -> str:
    """A segment that stayed English on a language page (website/i18n.py put a mark in front of it): its element gets
    lang="en" (and data-en for the style); marks inside other text go."""
    mark = i18n.FALLBACK_MARK
    if mark not in out:
        return out
    out = re.sub(r"<(p|li|td|th|div|h[1-6]|figcaption|span|strong|b|a)((?:\s[^>]*)?)>" + mark, r'<\1\2 lang="en" data-en>', out)
    return out.replace(mark, "")


BLOCK_KINDS = ("layer", "video", "picture", "note", "warn", "tip", "ingame", "steps", "cards", "stats", "hero", "flow")


def split_cells(line: str) -> list[str]:
    """The cells of a markdown table row; a | inside [[...|...]] or escaped as \\| is part of the cell."""
    s = line.strip()
    if s.startswith("|"):
        s = s[1:]
    if s.endswith("|") and not s.endswith("\\|"):
        s = s[:-1]
    cells, cur, depth, i = [], "", 0, 0
    while i < len(s):
        if s.startswith("[[", i):
            depth += 1
            cur += "[["
            i += 2
            continue
        if s.startswith("]]", i) and depth:
            depth -= 1
            cur += "]]"
            i += 2
            continue
        ch = s[i]
        if ch == "\\" and s[i + 1:i + 2] == "|":
            cur += "|"
            i += 2
            continue
        if ch == "|" and not depth:
            cells.append(cur)
            cur = ""
        else:
            cur += ch
        i += 1
    cells.append(cur)
    return cells


def strip_md(text: str) -> str:
    t = re.sub(r"\[\[[^|\]]*\|([^\]]*)\]\]", r"\1", text)
    t = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", t)
    return re.sub(r"[*_`]", "", t).strip()


def cards_html(rendered: str) -> str:
    """:::cards — a list of '[Title](link): what it is' as cards."""
    out = []
    for li in re.findall(r"<li>(.*?)</li>", rendered, re.S):
        li = re.sub(r"</?p>", "", li).strip()
        m = re.match(r'<a href="([^"]*)">(.*?)</a>\s*[:：–—-]?\s*(.*)$', li, re.S)  # ： (the CJK colon) too
        if not m:
            out.append(f'<div class="card">{li}</div>')
            continue
        title = re.sub(r"</?strong>", "", m.group(2))
        out.append(f'<a class="card" href="{m.group(1)}"><b>{title}</b><span>{m.group(3).strip()}</span></a>')
    return '<div class="cards">' + "".join(out) + "</div>\n"


def stats_html(rendered: str) -> str:
    """:::stats — a list of '**number** what' as number tiles."""
    out = []
    for li in re.findall(r"<li>(.*?)</li>", rendered, re.S):
        li = re.sub(r"</?p>", "", li).strip()
        m = re.match(r"<strong>(.*?)</strong>\s*(.*)$", li, re.S)
        out.append(f"<div><b>{m.group(1)}</b>{m.group(2)}</div>" if m else f"<div>{li}</div>")
    return '<div class="stats">' + "".join(out) + "</div>\n"


# ----------------------------------------------------------------------------------------------------------------------
# Diagrams: the caption line and the authoring rules (website/README.md, "Diagrams")
# ----------------------------------------------------------------------------------------------------------------------

CAPTION_LINE = re.compile(r"^[ \t]*%%[ \t]*caption:[ \t]*(.*?)[ \t]*$", re.M)
DIAGRAM_MAX_ROW = 5      # boxes in one row (a left-to-right chain, or one rank of a top-down chart)
DIAGRAM_MAX_OUT = 3      # lines out of one box; more: use a table


def mermaid_caption(src: str) -> tuple[str, str]:
    """('%% caption: ...' lines out of the source, the caption)."""
    caps = CAPTION_LINE.findall(src)
    return CAPTION_LINE.sub("", src).strip("\n") + "\n", " ".join(c for c in caps if c)


EDGE_LABEL_FORMS = [
    (re.compile(r"--\s+[^-|>][^>]*?\s+-->"), " --> "),
    (re.compile(r"-\.\s+[^>]*?\s+\.->"), " -.-> "),
    (re.compile(r"==\s+[^>]*?\s+==>"), " ==> "),
]
EDGE = re.compile(r"\s*<?(?:-{2,}|={2,}|-\.+-)[>ox]?\s*")


def diagram_graph(src: str) -> tuple[str, list[tuple[str, str]], bool, bool]:
    """(direction, edges, has solid lines, has dotted lines) of a Mermaid flowchart; ('', ...) for other kinds."""
    lines = src.strip().splitlines()
    head = lines[0].strip() if lines else ""
    m = re.match(r"(?:flowchart|graph)\s+(TD|TB|LR|RL|BT)\b", head)
    if not m:
        return "", [], False, False
    direction = "LR" if m.group(1) in ("LR", "RL") else "TD"
    edges: list[tuple[str, str]] = []
    solid = dotted = False
    for raw in lines[1:]:
        line = raw.strip()
        if not line or line.startswith("%%") or re.match(r"(subgraph|end\b|classDef|class\s|style\s|linkStyle|direction|click\s)", line):
            continue
        line = re.sub(r'"[^"]*"', '"q"', line)
        for rx, repl in EDGE_LABEL_FORMS:
            line = rx.sub(repl, line)
        line = re.sub(r"(-->|-\.->|==>|---|-\.-|--[ox])\s*\|[^|]*\|", r"\1", line)
        if re.search(r"-\.+-", line):
            dotted = True
        if re.search(r"(?<![.-])-{2,}[>ox-]|={2,}>", line):
            solid = True
        # Node shapes out, the id stays.
        line = re.sub(r"(\w[\w-]*)\s*(\(\(.*?\)\)|\(\[.*?\]\)|\[\[.*?\]\]|\[\(.*?\)\]|\{\{.*?\}\}|\[/.*?[/\\]\]|\[\\.*?[/\\]\]|\[.*?\]|\(.*?\)|\{.*?\}|>.*?\])",
                      r"\1", line)
        line = re.sub(r":::\w+", "", line)
        parts = [p for p in EDGE.split(line)]
        if len(parts) < 2:
            continue
        groups = [[x.strip() for x in p.split("&") if x.strip()] for p in parts]
        for a, b in zip(groups, groups[1:]):
            for x in a:
                for y in b:
                    edges.append((x, y))
    return direction, edges, solid, dotted


def diagram_problems(src: str) -> list[str]:
    """The authoring rules: a caption; at most 5 boxes in a row; at most 3 lines out of one box; a caption that says
    what the dotted lines mean when a chart has solid and dotted lines."""
    body, caption = mermaid_caption(src)
    problems = []
    if not caption:
        problems.append("no caption line ('%% caption: ...' in the diagram: one line that says what the picture shows)")
    direction, edges, solid, dotted = diagram_graph(body)
    if not direction:
        return problems
    first = body.strip().splitlines()[0][:40]
    out: dict[str, set[str]] = {}
    for a, b in edges:
        out.setdefault(a, set()).add(b)
    for node, targets in out.items():
        if len(targets) > DIAGRAM_MAX_OUT:
            problems.append(f"'{node}' has {len(targets)} lines out (at most {DIAGRAM_MAX_OUT}; use a table instead) [{first}]")
    if solid and dotted and caption and "dotted" not in caption.lower():
        problems.append(f"solid and dotted lines, but the caption does not say what dotted means [{first}]")
    # Longest path in boxes (back edges of loops ignored).
    nodes = {x for e in edges for x in e}
    depth: dict[str, int] = {}

    def longest(n: str, stack: set[str]) -> int:
        if n in depth:
            return depth[n]
        stack.add(n)
        best = 1
        for t in out.get(n, ()):
            if t not in stack:
                best = max(best, 1 + longest(t, stack))
        stack.discard(n)
        depth[n] = best
        return best

    if direction == "LR":
        chain = max((longest(n, set()) for n in nodes), default=0)
        if chain > DIAGRAM_MAX_ROW:
            problems.append(f"{chain} boxes in one left-to-right row (at most {DIAGRAM_MAX_ROW}; split it or draw it top-down) [{first}]")
    else:
        preds: dict[str, set[str]] = {}
        for a, b in edges:
            preds.setdefault(b, set()).add(a)
        rank: dict[str, int] = {}

        def rank_of(n: str, stack: set[str]) -> int:
            if n in rank:
                return rank[n]
            stack.add(n)
            r = 0
            for p in preds.get(n, ()):
                if p not in stack:
                    r = max(r, rank_of(p, stack) + 1)
            stack.discard(n)
            rank[n] = r
            return r

        per: dict[int, int] = {}
        for n in nodes:
            r = rank_of(n, set())
            per[r] = per.get(r, 0) + 1
        widest = max(per.values(), default=0)
        if widest > DIAGRAM_MAX_ROW:
            problems.append(f"{widest} boxes side by side in a top-down chart (at most {DIAGRAM_MAX_ROW}) [{first}]")
    return problems


class Renderer:
    def __init__(self, site: "Site"):
        self.site = site
        self.md = MarkdownIt("commonmark", {"html": True, "linkify": False, "typographer": False}).enable("table").enable("strikethrough")
        default_fence = self.md.renderer.rules.get("fence")

        def fence(tokens, idx, options, env):
            tok = tokens[idx]
            if tok.info.strip() == "mermaid":
                env["mermaid"] = True
                src, caption = mermaid_caption(tok.content)
                # The authoring rules are checked on the English source (a translated caption says "dotted" in
                # its own language).
                for problem in (diagram_problems(tok.content) if self.site.lang == "en" else []):
                    self.site.warn(f"{env.get('page_url', '?')}: diagram: {problem}")
                cap = f'<figcaption>{self.md.renderInline(caption)}</figcaption>' if caption else ""
                # Mermaid reads the text of the element: the source is escaped exactly once here (a label's "&gt;"
                # written in the source would come out literally, so the source uses plain > and <).
                return '<figure class="diagram-fig"><div class="diagram"><pre class="mermaid">' + esc(src) + "</pre></div>" + cap + "</figure>\n"
            return default_fence(tokens, idx, options, env)

        self.md.renderer.rules["fence"] = fence

    def render(self, text: str, page_url: str, env: dict, source: str | None = None) -> str:
        env.setdefault("page_url", page_url)
        text = self.expand(text, page_url, env)
        out = self.md.render(text, env)
        out = self.fix_links(out, page_url, source)
        out = re.sub(r"<table>", '<div class="table-wrap"><table>', out)
        out = re.sub(r"</table>", "</table></div>", out)
        return out

    # :::layer / :::video / :::note blocks, {{...}} placeholders, shot: images, [[...]] links
    def expand(self, text: str, page_url: str, env: dict) -> str:
        site = self.site
        lines = text.split("\n")
        out: list[str] = []
        plain: list[str] = []

        def flush():
            # The Markdown between the blocks, translated segment by segment (website/i18n.py) on a language page.
            if plain:
                out.extend(site.translate_run("\n".join(plain)).split("\n"))
                plain.clear()

        i = 0
        while i < len(lines):
            line = lines[i]
            m = re.match(r"^:::(\w+)\s*(.*)$", line)
            if m and m.group(1) in BLOCK_KINDS:
                flush()
                kind, arg = m.group(1), m.group(2).strip()
                depth = 1
                j = i + 1
                inner: list[str] = []
                while j < len(lines):
                    if re.match(r"^:::(\w+)", lines[j]):
                        depth += 1
                    elif lines[j].strip() == ":::":
                        depth -= 1
                        if depth == 0:
                            break
                    inner.append(lines[j])
                    j += 1
                body = "\n".join(inner)
                if kind == "layer":
                    env.setdefault("layers", set()).add(arg)
                    title = site.t(LAYER_TITLES.get(arg, arg.title()))
                    out.append(f'<section class="layer layer-{esc(arg)}" data-layer="{esc(arg)}"><div class="layer-badge">{esc(title)}</div>\n')
                    outer, site.ctx_layer = site.ctx_layer, arg
                    out.append(self.render(body, page_url, env))
                    site.ctx_layer = outer
                    out.append("</section>\n")
                elif kind == "video":
                    out.append(site.video_slot(arg, page_url))
                elif kind == "picture":
                    out.append(site.picture_slot(arg, page_url))
                elif kind == "steps":
                    out.append(f'<div class="steps">\n{self.render(body, page_url, env)}</div>\n')
                elif kind == "cards":
                    out.append(cards_html(self.render(body, page_url, env)))
                elif kind == "stats":
                    out.append(stats_html(self.render(body, page_url, env)))
                elif kind == "hero":
                    out.append(self.hero_html(arg, body, page_url, env))
                elif kind == "flow":
                    out.append(self.flow_html(body, page_url, env))
                elif kind == "ingame":
                    out.append(f'<div class="callout ingame"><div class="callout-title">{esc(site.t("In the game"))}: {esc(site.t(arg))}</div>\n')
                    out.append(self.render(body, page_url, env))
                    out.append("</div>\n")
                else:
                    titles = {"note": "Note", "warn": "Careful", "tip": "Tip"}
                    out.append(f'<div class="callout {kind}"><div class="callout-title">{esc(site.t(arg or titles[kind]))}</div>\n')
                    out.append(self.render(body, page_url, env))
                    out.append("</div>\n")
                out.append("")
                i = j + 1
                continue
            plain.append(line)
            i += 1
        flush()
        text = "\n".join(out)
        text = re.sub(r"\{\{stat:([\w.]+)\}\}", lambda mm: site.stat(mm.group(1)), text)
        text = re.sub(r"\{\{game-card\}\}", lambda mm: site.game_card(env.get("game_support", []), page_url), text)
        text = re.sub(r"\{\{game-index\}\}", lambda mm: site.game_index(page_url), text)
        text = re.sub(r"\{\{device-list\}\}", lambda mm: site.device_list(page_url), text)
        text = re.sub(r"\{\{(gui-table|cli-table):([\w-]+)\}\}", lambda mm: site.placeholder(mm.group(1), mm.group(2), page_url), text)
        text = re.sub(r"!\[([^\]]*)\]\(shot:([\w-]+)\)", lambda mm: site.shot_html(mm.group(2), mm.group(1), page_url), text)
        text = re.sub(r"\[\[(cli|gui|doc|game|page):([^\]|]+)(?:\|([^\]]+))?\]\]", lambda mm: site.wikilink(mm.group(1), mm.group(2).strip(), mm.group(3), page_url), text)
        return text

    def inline(self, text: str, page_url: str, env: dict) -> str:
        """One table cell or line of markdown as HTML without the paragraph around it."""
        out = self.render(text, page_url, env).strip()
        m = re.fullmatch(r"<p>(.*)</p>", out, re.S)
        return m.group(1) if m else out

    def hero_html(self, arg: str, body: str, page_url: str, env: dict) -> str:
        """:::hero shot:<name> — a heading, a paragraph and a list of links as buttons (the first one is the main one)."""
        inner = self.render(body, page_url, env)

        def buttons(m):
            links = re.findall(r'<a href="([^"]*)">(.*?)</a>', m.group(0), re.S)
            return '<div class="btns">' + "".join(
                f'<a class="btn{"" if i == 0 else " ghost"}" href="{h}">{re.sub(r"</?strong>", "", t)}</a>' for i, (h, t) in enumerate(links)) + "</div>"

        inner = re.sub(r"<ul>.*?</ul>", buttons, inner, count=1, flags=re.S)
        pic = ""
        shot = arg.removeprefix("shot:").strip()
        if shot and (shot + "-dark") in self.site.shots:
            pic = f'<div><img src="{rel(page_url, "assets/shots/" + shot + "-dark.webp")}" alt="" loading="lazy"></div>'
        return f'<div class="hero"><div>{inner}</div>{pic}</div>\n'

    def flow_html(self, body: str, page_url: str, env: dict) -> str:
        """:::flow — a markdown table drawn as boxes with arrows: the header row names the columns, each row is one
        line from left to right, an empty first cell continues the box above (one box that leads to several rows).
        On a phone the boxes stack: the first box, then one card per row."""
        rows = [split_cells(line) for line in body.splitlines() if line.strip().startswith("|")]
        rows = [r for r in rows if not all(re.fullmatch(r":?-{2,}:?", c.strip() or "--") for c in r) or not r]
        if len(rows) < 2:
            self.site.warn(f"{page_url}: :::flow needs a header row and at least one row")
            return ""
        head, data = rows[0], rows[1:]
        n = len(head)
        cols = " 30px ".join(["minmax(0, 1fr)"] * n)
        out = [f'<div class="flow" style="grid-template-columns: {cols}">']
        for c, title in enumerate(head):
            out.append(f'<div class="flow-head" style="grid-column: {2 * c + 1}">{self.inline(title, page_url, env)}</div>')
        # The first column: a box spans the rows below it while their first cell is empty.
        spans: list[int] = []
        for i, r in enumerate(data):
            first = (r[0] if r else "").strip()
            if first:
                k = 1
                while i + k < len(data) and not (data[i + k][0] if data[i + k] else "").strip():
                    k += 1
                spans.append(k)
            else:
                spans.append(0)
        for i, r in enumerate(data):
            cells = [(r[c] if c < len(r) else "").strip() for c in range(n)]
            grid_row = i + 2
            lead = spans[i] > 1
            if cells[0] and lead:
                out.append(f'<div class="flow-box flow-lead" data-col="{esc(strip_md(self.site.t(head[0].strip())))}" style="grid-column: 1; grid-row: {grid_row} / span {spans[i]}">'
                           f'<div>{self.inline(cells[0], page_url, env)}</div></div>')
            out.append('<div class="flow-row">')
            for c in range(n):
                if c == 0 and (not cells[0] or lead):
                    continue
                if not cells[c]:
                    continue
                if c > 0:
                    from_lead = c == 1 and (lead or not cells[0])
                    out.append(f'<div class="flow-arrow{" from-lead" if from_lead else ""}" style="grid-column: {2 * c}; grid-row: {grid_row}" aria-hidden="true"></div>')
                out.append(f'<div class="flow-box" data-col="{esc(strip_md(self.site.t(head[c].strip())))}" style="grid-column: {2 * c + 1}; grid-row: {grid_row}">'
                           f'<div>{self.inline(cells[c], page_url, env)}</div></div>')
            out.append("</div>")
        out.append("</div>\n")
        return "".join(out)

    def fix_links(self, out: str, page_url: str, source: str | None) -> str:
        if not source:
            return out
        site = self.site
        base = Path(source).parent.as_posix()

        def fix(m):
            attr, url = m.group(1), m.group(2)
            if re.match(r"^(https?:|mailto:|#|data:)", url) or url.startswith("../" * 9):
                return m.group(0)
            target, _, frag = url.partition("#")
            resolved = os.path.normpath(os.path.join(base, target)).replace("\\", "/") if target else source
            if attr == "src":
                return f'{attr}="{site.doc_image(resolved, page_url)}"'
            mapped = site.doc_url(resolved)
            if mapped:
                return f'{attr}="{rel(page_url, mapped)}{("#" + frag) if frag else ""}"'
            if resolved.startswith("..") or PRIVATE_DOCS.match(resolved.removeprefix("docs/")) and resolved.startswith("docs/"):
                return 'data-private="1"'
            return f'{attr}="{github(resolved)}"'

        return re.sub(r'(href|src)="([^"]+)"', fix, out)


# ----------------------------------------------------------------------------------------------------------------------
# The site
# ----------------------------------------------------------------------------------------------------------------------


@dataclass
class Page:
    url: str
    title: str
    section: str
    html: str = ""
    status: str = "generated"  # full / skeleton / generated
    summary: str = ""
    toc: list[tuple[int, str, str]] = field(default_factory=list)
    mermaid: bool = False
    layers: bool = False
    order: int = 100
    nav: str = ""


class Site:
    def __init__(self, args):
        self.args = args
        self.warnings: list[str] = []
        self.pages: dict[str, Page] = {}
        self.search: list[dict] = []
        self.slots: list[tuple[str, str, str, str]] = []  # planned videos and in-game pictures (kind, id, title, page)
        self.loc = json.loads(read(DATA / "loc.json")) if (DATA / "loc.json").exists() else {}
        self.support = json.loads(read(DATA / "support.json")) if (DATA / "support.json").exists() else {"Rows": []}
        self.videos = json.loads(read(CONTENT / "videos.json")) if (CONTENT / "videos.json").exists() else {}
        self.global_options, self.cli = parse_cli()
        self.cli_areas = {c.area for c in self.cli}
        self.commands, self.props = extract_vm(self.cli_areas)
        self.gui = extract_gui_controls(PROP_TYPES)
        for e in self.gui:
            # A name several classes have: the one of the control's AXAML scope (DataContext / x:DataType).
            if e.command and e.command in self.commands:
                e.vm = pick_member(self.commands[e.command], e.scope)
            elif e.binding and e.binding in self.props:
                e.vm = pick_member(self.props[e.binding], e.scope)
        for problem in self_check_scopes():
            self.warn(problem)
        self.shots = {p.stem for p in (ASSETS / "shots").glob("*.webp")} if (ASSETS / "shots").exists() else set()
        self.doc_map: dict[str, str] = {}
        # The hand-written game pages (content/games/<slug>.md -> games/<slug>.html) and the docs they are built on.
        self.game_pages: dict[str, dict] = {}
        for path in sorted((CONTENT / "games").glob("*.md")) if (CONTENT / "games").exists() else []:
            meta, _ = front_matter(read(path))
            self.game_pages[path.stem] = meta
        self.renderer = Renderer(self)
        self.hand_gui: dict[str, dict[str, str]] = {}
        self.hand_cli: dict[str, str] = {}
        self.load_hand_entries()
        self.link_cli_gui()
        # Languages (website/i18n.py): the language being built ("en" = the English site at the root), the catalogs,
        # and while collecting the segments for the translators, the list they go into.
        self.lang = "en"
        self.cat: i18n.Catalog | None = None
        self.collecting: list[dict] | None = None
        self.untracked = 0  # > 0 while a text outside the scope is rendered (probe_language_keys skips its lookups)
        self.ctx_page = ""
        self.ctx_layer = ""
        self.ctx_heading = ""

    # ---- languages ----
    def tr(self, key: str, kind: str = "p") -> str | None:
        """The translation of one segment, or None (the English stays). While collecting: records it."""
        key = i18n.norm(key)
        if not key:
            return None
        if self.collecting is not None:
            if kind == "h":
                self.ctx_heading = key
            ctx = " · ".join(x for x in (self.ctx_page, self.ctx_layer, "" if kind == "h" else self.ctx_heading) if x)
            self.collecting.append({"en": key, "ctx": ctx, "page": self.ctx_page, "layer": self.ctx_layer})
            return None
        if self.lang == "en" or self.cat is None:
            return None
        return self.cat.lookup(self.lang, key)

    def t(self, text: str) -> str:
        """A text of the site's frame (menu, labels): the translation or the English (never marked)."""
        return self.tr(text, "c") or text

    def h2(self, text: str) -> str:
        """A generated heading in the page's language with the id of the English one."""
        shown = self.t(text)  # also on the English page, so the collector sees the heading
        if self.lang == "en":
            return f"<h2>{esc(text)}</h2>"
        return f"<h2>{i18n.HEAD_ID[0]}{slug(text)}{i18n.HEAD_ID[1]}{esc(shown)}</h2>"

    def translate_run(self, text: str) -> str:
        if self.lang == "en" and self.collecting is None:
            return text
        slugger = None if self.collecting is not None else self.heading_slug
        return i18n.translate_markdown(text, self.renderer.md, self.tr, slugger)

    def heading_slug(self, key: str) -> str:
        """The id the English heading gets (with_toc), so a link to a spot works in every language."""
        inner = self.renderer.md.renderInline(re.sub(r"\[\[[^|\]]*\|([^\]]*)\]\]", r"\1", key))
        return slug(html.unescape(re.sub(r"<[^>]+>", "", inner)))

    def warn(self, text: str):
        self.warnings.append(text)
        print("warning: " + text)

    # ---- data ----
    def stat(self, key: str) -> str:
        if key in self.extra_stats():
            return self.extra_stats()[key]
        node = self.loc
        for part in key.split("."):
            if isinstance(node, dict) and part in node:
                node = node[part]
            else:
                return "?"
        if isinstance(node, int):
            return f"{node:,}"
        if isinstance(node, list) and node and isinstance(node[0], int):
            return f"{node[1]:,}"
        if isinstance(node, str) and re.match(r"^\d{4}-\d\d-\d\d ", node):
            return node[:10]
        return str(node)

    def extra_stats(self) -> dict[str, str]:
        if hasattr(self, "_extra"):
            return self._extra
        rows = self.support.get("Rows", [])
        langs = len(list((GUI / "Localization" / "Strings").glob("*.json")))
        jp1 = self.loc.get("jp1", {})
        jp2 = self.loc.get("jp2", {})

        def ext(d, e, i):
            return d.get("by", {}).get(e, [0, 0, 0, 0])[i]

        smoke = len(list((ROOT / "engine" / "tests" / "smoke").glob("*.ps1")))
        self._extra = {
            "games.titles": str(len(rows)),
            "games.handled": str(sum(1 for r in rows if r.get("InEngine"))),
            "games.native": str(sum(1 for r in rows if r.get("Level") == "native")),
            "games.virtual": str(sum(1 for r in rows if r.get("Level") in ("virtual", "xbox"))),
            "games.documented": str(sum(1 for r in rows if r.get("Level") == "documented")),
            "languages": str(langs + 1),
            "cli.commands": str(len(self.cli)),
            "cli.areas": str(len(self.cli_areas)),
            "cli.globals": str(len(self.global_options)),
            "gui.controls": str(len(self.gui)),
            "gui.commands": str(len(self.commands)),
            "smoke.parts": str(smoke),
            "jp1.cs.code": f"{ext(jp1, '.cs', 2):,}",
            "jp2.cs.code": f"{ext(jp2, '.cs', 2):,}",
            "jp1.cs.files": f"{ext(jp1, '.cs', 0):,}",
            "jp2.cs.files": f"{ext(jp2, '.cs', 0):,}",
            "jp1.xaml.lines": f"{ext(jp1, '.xaml', 1):,}",
            "jp2.axaml.lines": f"{ext(jp2, '.axaml', 1):,}",
            "jp2.md.lines": f"{ext(jp2, '.md', 1):,}",
            "jp1.md.lines": f"{ext(jp1, '.md', 1):,}",
            "jp2.ps1.lines": f"{ext(jp2, '.ps1', 1):,}",
            "jp2.py.lines": f"{ext(jp2, '.py', 1):,}",
            "jp1.projects": str(len(jp1.get("projects", []))),
            "jp2.projects": str(len([p for p in jp2.get("projects", []) if not p.startswith("tools/e2e") and not p.startswith("tools/trailer")])),
            "jp2.mb": f"{jp2.get('bytes', 0) / 1e6:.0f}",
            "ratio.cs": f"{ext(jp2, '.cs', 2) / max(1, ext(jp1, '.cs', 2)):.1f}",
            "ratio.types": f"{jp2.get('types', 0) / max(1, jp1.get('types', 1)):.0f}",
            "ratio.methods": f"{jp2.get('methods', 0) / max(1, jp1.get('methods', 1)):.0f}",
            "jp2.engine.code": f"{jp2.get('cs', {}).get('engine', 0):,}",
            "jp2.gui.code": f"{jp2.get('cs', {}).get('gui/JoyPro.Gui', 0):,}",
            "jp2.guitests.code": f"{jp2.get('cs', {}).get('gui/JoyPro.Gui.Tests', 0):,}",
            "jp2.ffb.code": f"{jp2.get('cs', {}).get('gui/JoyPro.Ffb.Core', 0) + jp2.get('cs', {}).get('gui/JoyPro.Ffb', 0):,}",
        }
        return self._extra

    def load_hand_entries(self):
        """Entry texts in content/gui/<page>.md and content/cli/<area>.md: '<!-- entry: Key -->' blocks."""
        for folder, target in (("gui", self.hand_gui), ("cli", self.hand_cli)):
            for path in sorted((CONTENT / folder).glob("*.md")) if (CONTENT / folder).exists() else []:
                text = read(path)
                parts = re.split(r"^<!-- entry: (.+?) -->\s*$", text, flags=re.M)
                for i in range(1, len(parts) - 1, 2):
                    key, body = parts[i].strip(), parts[i + 1].strip()
                    if folder == "gui":
                        target.setdefault(path.stem, {})[key] = body
                    else:
                        target[key] = body
        for e in self.gui:
            page = self.hand_gui.get(e.page, {})
            e.hand = page.get(e.key) or page.get(e.command) or (page.get("label:" + e.label) if e.label else "") or ""
        for c in self.cli:
            c.hand = self.hand_cli.get(c.key, "")

    def link_cli_gui(self):
        by_key = sorted(self.cli, key=lambda c: -len(c.key))
        for e in self.gui:
            if not e.vm:
                continue
            for call in e.vm.engine:
                for c in by_key:
                    if call == c.key or call.startswith(c.key + " ") or c.key.startswith(call + " ") and len(call.split()) >= 2:
                        if e not in c.gui:
                            c.gui.append(e)
                        break

    def cli_for_call(self, call: str) -> CliCommand | None:
        for c in sorted(self.cli, key=lambda c: -len(c.key)):
            if call == c.key or call.startswith(c.key + " "):
                return c
        for c in self.cli:
            if c.key.startswith(call + " "):
                return c
        # A verb listed inside another command's signature ("backups list | show <id> | restore <id>").
        tokens = call.split()
        if len(tokens) >= 2:
            for c in self.cli:
                if c.area == tokens[0] and re.search(r"\|\s*" + re.escape(tokens[1]) + r"\b", c.signature):
                    return c
        return None

    # ---- urls ----
    def doc_url(self, source: str) -> str | None:
        if source in self.doc_map:
            return self.doc_map[source]
        if source.endswith("ENGINE_CLI_WIKI.md"):
            return "cli/index.html"
        return None

    def doc_image(self, source: str, page_url: str) -> str:
        """Copies a picture the docs use into dist (WebP), unless it is a private screenshot."""
        src = ROOT / source
        if PRIVATE_IMAGES.match(source) or not src.exists():
            return ""
        out_name = "assets/docs/" + slug(Path(source).with_suffix("").as_posix()) + (".svg" if src.suffix == ".svg" else ".webp")
        dest = DIST / out_name
        if not dest.exists():
            dest.parent.mkdir(parents=True, exist_ok=True)
            if src.suffix == ".svg":
                shutil.copy(src, dest)
            else:
                from PIL import Image
                im = Image.open(src)
                if im.width > 1600:
                    im = im.resize((1600, round(im.height * 1600 / im.width)))
                im.save(dest, "WEBP", quality=78, method=6)
        return rel(page_url, out_name)

    def shot_html(self, name: str, alt: str, page_url: str) -> str:
        light, dark = f"{name}-light", f"{name}-dark"
        if light not in self.shots and dark not in self.shots:
            return f'<div class="shot missing">Screenshot "{esc(name)}" is not rendered yet.</div>'
        alt = alt or f"The {name} page"
        out = ['<figure class="shot">']
        if light in self.shots:
            out.append(f'<a href="{rel(page_url, "assets/shots/" + light + ".webp")}"><img class="only-light" loading="lazy" src="{rel(page_url, "assets/shots/" + light + ".webp")}" alt="{esc(alt)}"></a>')
        if dark in self.shots:
            out.append(f'<a href="{rel(page_url, "assets/shots/" + dark + ".webp")}"><img class="only-dark" loading="lazy" src="{rel(page_url, "assets/shots/" + dark + ".webp")}" alt="{esc(alt)}"></a>')
        out.append(f"<figcaption>{esc(alt)}</figcaption></figure>")
        return "".join(out)

    def video_slot(self, arg: str, page_url: str) -> str:
        m = re.match(r"(\S+)\s*(.*)", arg)
        vid, title = (m.group(1), m.group(2).strip().strip('"')) if m else ("video", "")
        yt = self.videos.get(vid, "")
        if yt:
            return (f'<div class="video"><iframe loading="lazy" src="https://www.youtube-nocookie.com/embed/{esc(yt)}" title="{esc(title)}" '
                    f'allow="encrypted-media; picture-in-picture" allowfullscreen></iframe></div>\n')
        # No empty box on the page: the planned videos are listed on the wiki status page.
        self.slots.append(("video", vid, title, page_url))
        return ""

    def wikilink(self, kind: str, target: str, text: str | None, page_url: str) -> str:
        if kind == "cli":
            c = next((c for c in self.cli if c.key == target), None) or self.cli_for_call(target)
            if not c:
                self.warn(f"{page_url}: no engine command '{target}'")
                return f"`engine {target}`"
            return f'<a href="{rel(page_url, "cli/" + c.area + ".html#" + c.anchor)}"><code>{esc(text or "engine " + target)}</code></a>'
        if kind == "gui":
            page, _, key = target.partition("#")
            if not key:
                return f'<a href="{rel(page_url, "gui/" + page + ".html")}">{esc(text or self.t(GUI_TITLES.get(page, page)))}</a>'
            e = next((e for e in self.gui if e.page == page and (e.key == key or e.command == key or e.label == key)), None)
            if not e:
                self.warn(f"{page_url}: no control '{target}'")
                return f"<strong>{esc(text or key)}</strong>"
            return f'<a href="{rel(page_url, "gui/" + page + ".html#" + e.anchor)}"><strong>{esc(text or (self.t(e.label) if e.label else key))}</strong></a>'
        if kind == "doc":
            url = self.doc_map.get("docs/" + target + ".md") or self.doc_map.get(target)
            if not url and MAINTAINER_DOCS.match(target + ".md") and (ROOT / "docs" / (target + ".md")).exists():
                return f'<a href="{github("docs/" + target + ".md")}">{esc(text or target)}</a>'
            if not url:
                self.warn(f"{page_url}: no doc '{target}'")
            return f'<a href="{rel(page_url, url)}">{esc(text or target)}</a>' if url else esc(text or target)
        if kind == "game":
            url = ("games/" + target + ".html") if target in self.game_pages else self.doc_map.get("docs/games/" + target + ".md")
            if not url:
                self.warn(f"{page_url}: no game doc '{target}'")
            return f'<a href="{rel(page_url, url)}">{esc(text or target)}</a>' if url else esc(text or target)
        if kind == "page":
            return f'<a href="{rel(page_url, target)}">{esc(text or target)}</a>'
        return esc(text or target)

    def placeholder(self, kind: str, arg: str, page_url: str) -> str:
        if kind == "gui-table":
            return self.gui_entries_html(arg, page_url)
        if kind == "cli-table":
            return self.cli_area_list(arg, page_url)
        return ""

    # ---- page building ----
    def add(self, page: Page, text_for_search: str = ""):
        self.pages[page.url] = page
        plain = re.sub(r"<[^>]+>", " ", page.html)
        plain = html.unescape(re.sub(r"\s+", " ", plain))
        self.search.append({"t": page.title, "u": page.url, "s": page.section, "x": (text_for_search or plain)[:220]})
        for level, hid, htext in page.toc:
            self.search.append({"t": htext, "u": page.url + "#" + hid, "s": page.title, "x": ""})

    def render_md(self, text: str, url: str, source: str | None = None, env: dict | None = None) -> tuple[str, dict]:
        env = env if env is not None else {}
        out = self.renderer.render(text, url, env, source)
        return out, env

    def with_toc(self, out: str) -> tuple[str, list[tuple[int, str, str]]]:
        toc = []
        used = set()

        def fix(m):
            level, inner = int(m.group(1)), m.group(2)
            # A language page: the English id comes along (website/i18n.py), so links to the spot stay the same.
            forced = re.match(i18n.HEAD_ID[0] + "([^" + i18n.HEAD_ID[1] + "]*)" + i18n.HEAD_ID[1], inner)
            if forced:
                inner = inner[forced.end():]
            fallback = inner.startswith(i18n.FALLBACK_MARK)
            inner = inner.replace(i18n.FALLBACK_MARK, "")
            text = html.unescape(re.sub(r"<[^>]+>", "", inner))
            hid = forced.group(1) if forced and forced.group(1) else slug(text)
            n = 2
            base = hid
            while hid in used:
                hid = f"{base}-{n}"
                n += 1
            used.add(hid)
            if level in (2, 3):
                toc.append((level, hid, text))
            # The docs link to headings the way GitHub names them ("JoyPro's drawings" -> #joypros-drawings): that
            # name is an id here too, so the same link works in the repository and on the site.
            gh = re.sub(r"\s", "-", re.sub(r"[^\w\- ]", "", text.strip().lower()))
            alias = f'<span id="{esc(gh)}" class="alias"></span>' if gh and gh != hid and gh not in used else ""
            if alias:
                used.add(gh)
            en = ' lang="en" data-en' if fallback else ""
            return f'<h{level} id="{hid}"{en}>{alias}<a class="anchor" href="#{hid}">#</a>{inner}</h{level}>'

        out = re.sub(r"<h([2-4])>(.*?)</h\1>", fix, out)
        return out, toc

    def content_page(self, path: Path, url: str, section: str, extra_html: str = "") -> Page:
        meta, text = front_matter(read(path))
        # The texts of single controls and commands ('<!-- entry: key -->' blocks at the end of the file) are shown in
        # the list of every control, not a second time as the page's own text.
        cut = re.search(r"^<!-- entry: .+? -->\s*$", text, re.M)
        if cut:
            text = text[:cut.start()]
        self.ctx_page, self.ctx_layer, self.ctx_heading = url, "", ""
        title = self.t(meta.get("title", path.stem))
        summary = self.t(meta["summary"]) if meta.get("summary") else ""
        env = {"game_support": [x.strip() for x in meta.get("support", "").split(",") if x.strip()]}
        out, env = self.render_md(text, url, None, env)
        out += extra_html
        out, toc = self.with_toc(out)
        status = meta.get("status", "skeleton")
        page = Page(url, title, section, mark_fallbacks(out), status, summary, toc,
                    bool(env.get("mermaid")), bool(env.get("layers")), int(meta.get("order", 100)))
        return page

    def build_docs(self):
        docs = ROOT / "docs"
        for path in sorted(docs.rglob("*.md")):
            r = path.relative_to(docs).as_posix()
            if PRIVATE_DOCS.match(r) or r.startswith("branding/") or r.startswith("cockpits/") or r.startswith("device-reference/"):
                continue
            if MAINTAINER_DOCS.match(r):
                # Test logs and evaluation runs of the maintainer: not on the site (a link to one goes to the repository).
                continue
            if r.startswith("games/"):
                # The matrix stays a game page; the per-game notes are the reference behind the hand-written game pages.
                self.doc_map["docs/" + r] = "games/" + path.stem + ".html" if r == "games/support.md" else "games/ref/" + path.stem + ".html"
            elif r.startswith("ui/"):
                self.doc_map["docs/" + r] = "reference/ui-" + path.stem + ".html"
            elif r.startswith("releases/"):
                self.doc_map["docs/" + r] = "releases/" + path.stem + ".html"
            else:
                self.doc_map["docs/" + r] = "reference/" + path.stem + ".html"
        for source, url in self.doc_map.items():
            text = read(ROOT / source)
            m = re.search(r"^# (.+)$", text, re.M)
            # The title is plain text (link texts, the menu, <title>): Markdown code marks dropped.
            title = re.sub(r"`([^`]*)`", r"\1", m.group(1).strip()) if m else Path(source).stem
            text = re.sub(r"^# .+\n", "", text, count=1, flags=re.M)
            section = "Game reference" if url.startswith("games/ref/") else "Games" if url.startswith("games/") else "Release notes" if url.startswith("releases/") else "Reference"
            if DEVELOPER_DOCS.match(source.removeprefix("docs/")):
                section = "Developers"
            head = ""
            if url.startswith("games/ref/"):
                head = self.game_header(Path(source).name, url)
            out, env = self.render_md(text, url, source)
            out = head + f'<p class="source-note">From <a href="{github(source)}">{esc(source)}</a> in the repository (generated, always the current text).</p>' + out
            out, toc = self.with_toc(out)
            nav = re.sub(r"\s*\([^)]*\)", "", title).strip() or title
            order = 1 if url == "games/support.html" else 0 if url == "games/ref/README.html" else 100
            self.add(Page(url, title, section, out, "generated", "", toc, bool(env.get("mermaid")), order=order, nav=nav))

    def game_header(self, doc: str, url: str) -> str:
        rows = [r for r in self.support.get("Rows", []) if r.get("Doc") == doc]
        if not rows:
            return ""
        cells = []
        for r in rows:
            c = r.get("Cells", {})
            marks = " · ".join(f"{k}: {v}" for k, v in list(c.items())[:8]) if isinstance(c, dict) else ""
            cells.append(f"<tr><td><strong>{esc(r['Title'])}</strong></td><td>{esc(r.get('Level', ''))}</td>"
                         f"<td><code>{esc(r.get('Command', ''))}</code></td><td>{self.renderer.md.renderInline(r.get('BindFile', '') or '')}</td><td>{esc(r.get('TestResult', '') or '')}</td></tr>")
        return ('<div class="game-card"><div class="table-wrap"><table><thead><tr><th>Title</th><th>Support</th><th>Engine command</th>'
                '<th>Bind file</th><th>In-game test</th></tr></thead><tbody>' + "".join(cells) + "</tbody></table></div>"
                f'<p>{self.hand_game_link(doc, url)}Full matrix: <a href="{rel(url, "games/support.html")}">Game support</a>. What an export does in this kind of game: '
                f'<a href="{rel(url, "flows/export.html#in-the-game")}">Export flow, in the game</a>.</p></div>')

    def hand_game_link(self, doc: str, url: str) -> str:
        pages = [(s, m) for s, m in self.game_pages.items() if doc in [d.strip() for d in m.get("docs", "").split(",")]]
        if not pages:
            return ""
        links = ", ".join(f'<a href="{rel(url, "games/" + s + ".html")}">{esc(m.get("title", s))}</a>' for s, m in pages)
        return f"The game page in plain words: {links}. "

    def support_rows(self, keys: list[str]) -> list[dict]:
        rows = []
        for k in keys:
            for r in self.support.get("Rows", []):
                if r in rows:
                    continue
                if k in (r.get("GameId"), (r.get("Command") or "").removeprefix("engine "), r.get("Title")):
                    rows.append(r)
        return rows

    def game_card(self, keys: list[str], url: str) -> str:
        """The support card of a hand-written game page: per title its level, engine command, bind file, input limit
        and the feature marks of the support matrix (data/support.json, from the engine's code)."""
        rows = self.support_rows(keys)
        if not rows:
            self.warn(f"{url}: game card without titles in data/support.json (front matter support: {keys})")
            return ""
        # A note the matrix gives most titles (the generic Xbox pad note) says nothing about this game: left out.
        counts: dict[str, int] = {}
        for r in self.support.get("Rows", []):
            for cell in (r.get("Cells") or {}).values():
                if isinstance(cell, dict) and cell.get("Note"):
                    counts[cell["Note"]] = counts.get(cell["Note"], 0) + 1
        out = ['<div class="game-card">']
        for r in rows:
            c = r.get("Cells", {}) or {}
            cmd = r.get("Command") or ""
            area = cmd.removeprefix("engine ")
            cmd_html = (f'<a href="{rel(url, "cli/" + area + ".html")}"><code>{esc(cmd)}</code></a>' if area in self.cli_areas
                        else (f"<code>{esc(cmd)}</code>" if cmd else esc(self.t("none"))))
            facts = [(self.t("Support"), esc(self.t(LEVEL_WORDS.get(r.get("Level", ""), r.get("Level", ""))))), (self.t("Engine command"), cmd_html)]
            # The texts of the matrix (bind file, input limit, list source, notes) are segments of the page too: the
            # translation keeps the code spans (paths, file names) and the game names.
            if r.get("BindFile"):
                facts.append((self.t("Bind file"), self.renderer.md.renderInline(self.t(public_text(r["BindFile"])))))
            if r.get("InputLimit"):
                facts.append((self.t("Inputs the game reads"), esc(self.t(public_text(r["InputLimit"])))))
            if r.get("ListSource"):
                facts.append((self.t("Command list from"), self.renderer.md.renderInline(self.t(public_text(r["ListSource"])))))
            if r.get("TestResult"):
                facts.append((self.t("In-game test"), esc(self.t("{0} steps passed ({1})").replace("{0}", r["TestResult"]).replace("{1}", r["TestDate"])
                                                         if r.get("TestDate") else r["TestResult"])))
            # A title with the game that covers it ("ACE COMBAT Zero (with ACE COMBAT 8)"): the words around the names.
            tm = re.fullmatch(r"(.+?) \(with (.+)\)", r["Title"])
            title = (self.t("{0} (with {1})").replace("{0}", tm.group(1)).replace("{1}", tm.group(2)) if tm
                     else self.t(r["Title"]) if re.search(r"\([^)]*(?<![A-Za-z])[a-z]{3,}", r["Title"]) else r["Title"])
            out.append(f'<details class="support-card"{" open" if len(rows) <= 2 else ""}><summary><strong>{esc(title)}</strong>'
                       f' <span class="lvl lvl-{esc(r.get("Level", ""))}">{esc(self.t(LEVEL_WORDS.get(r.get("Level", ""), r.get("Level", ""))))}</span></summary>')
            out.append('<dl class="facts">' + "".join(f"<dt>{k}</dt><dd>{v}</dd>" for k, v in facts if v) + "</dl>")
            marks = []
            for key, label in GAME_FEATURES:
                cell = c.get(key) if isinstance(c, dict) else None
                if not cell:
                    continue
                mark = cell.get("Mark", "")
                note = cell.get("Note", "") or ""
                note = "" if counts.get(note, 0) > 8 else note
                note = re.sub(r"\s*Live watcher test.*$", "", note) if key == "watcher" else note
                note = public_text(note)
                cls = {"✓": "yes", "◐": "part", "✗": "no"}.get(mark, "na")
                marks.append(f'<li class="{cls}"><span class="m">{esc(mark)}</span> {esc(self.t(label))}'
                             + (f'<span class="n">{self.renderer.md.renderInline(self.t(note))}</span>' if note and cls != "yes" else "") + "</li>")
            out.append('<ul class="features">' + "".join(marks) + "</ul></details>")
        out.append(f'<p class="muted small">{esc(self.t("From the support matrix, generated from the engine’s code:"))} '
                   f'<a href="{rel(url, "games/support.html")}">{esc(self.t("all games"))}</a>.</p></div>')
        return "".join(out)

    def picture_slot(self, arg: str, page_url: str) -> str:
        """An in-game picture: the screenshot when one is there (shot <id>, or <id>-light / -dark), else a marked slot."""
        m = re.match(r"(\S+)\s*(.*)", arg)
        pid, title = (m.group(1), m.group(2).strip().strip('"')) if m else ("picture", "")
        if pid in self.shots:
            src = rel(page_url, "assets/shots/" + pid + ".webp")
            return f'<figure class="shot"><a href="{src}"><img loading="lazy" src="{src}" alt="{esc(title)}"></a><figcaption>{esc(title)}</figcaption></figure>\n'
        if pid + "-light" in self.shots or pid + "-dark" in self.shots:
            return self.shot_html(pid, title, page_url) + "\n"
        self.slots.append(("picture", pid, title, page_url))
        return ""

    def device_list(self, url: str) -> str:
        """The supported devices from the device catalog (DB/Devices): per maker the models with their kind, the USB ids
        JoyPro knows them by and whether JoyPro has a picture with the buttons marked."""
        db = ROOT / "EngineOnly_Runtime" / "DB"
        pictures = {f.name.split(".")[0] for d in ("DeviceLayouts", "DeviceLayouts2D", "DeviceDrawings") for f in (db / d).glob("*.json")}
        photos: set[str] = set()
        for f in (db / "DevicePhotos").glob("*.json"):
            try:
                photos |= {x.get("model", "") for x in json.loads(read(f)).get("photos", [])}
            except ValueError:
                pass
        kinds = {"stick": "stick", "base": "stick base", "throttle": "throttle", "hotas": "stick and throttle", "pedals": "pedals",
                 "yoke": "yoke", "quadrant": "throttle quadrant", "collective": "collective", "panel": "panel or button box",
                 "controller": "button controller", "mfd": "MFD frame", "gamepad": "gamepad", "other": "other"}
        vendors: list[tuple[str, list[dict]]] = []
        for f in sorted((db / "Devices").glob("*.json")):
            d = json.loads(read(f))
            vendors.append((d.get("vendor") or f.stem, d.get("devices", [])))
        vendors.sort(key=lambda v: v[0].lower())
        total = sum(len(v[1]) for v in vendors)
        with_ids = sum(1 for v in vendors for x in v[1] if (x.get("identify") or {}).get("vidPids"))
        with_pic = sum(1 for v in vendors for x in v[1] if x.get("id") in pictures)
        out = [f'<div class="stats"><div><b>{total}</b>devices in the catalog</div><div><b>{len(vendors)}</b>makers</div>'
               f'<div><b>{with_ids}</b>recognised by their USB id</div><div><b>{with_pic}</b>with a picture of the buttons</div></div>']
        for vendor, items in vendors:
            out.append(f'<h3>{esc(vendor)}</h3><div class="table-wrap"><table class="devices"><thead><tr><th>Model</th><th>Kind</th>'
                       f'<th>USB id (VID:PID)</th><th>Picture</th></tr></thead><tbody>')
            for x in sorted(items, key=lambda x: x.get("name", "").lower()):
                ids = (x.get("identify") or {}).get("vidPids") or []
                shown = " ".join(f"<code>{esc(i)}</code>" for i in ids[:4]) + (f" +{len(ids) - 4}" if len(ids) > 4 else "")
                pic = "buttons marked" if x.get("id") in pictures else ("photo" if x.get("id") in photos else "")
                out.append(f'<tr><td>{esc(public_text(x.get("name", "")))}</td><td>{esc(kinds.get(x.get("kind", ""), x.get("kind", "")))}</td>'
                           f'<td>{shown or "<span class=muted>by name</span>"}</td><td>{esc(pic) or "<span class=muted>-</span>"}</td></tr>')
            out.append("</tbody></table></div>")
        return "\n".join(out)

    def build_games(self):
        for path in sorted((CONTENT / "games").glob("*.md")) if (CONTENT / "games").exists() else []:
            url = "games/" + path.stem + ".html"
            page = self.content_page(path, url, "Games")
            meta = self.game_pages.get(path.stem, {})
            docs = [d.strip() for d in meta.get("docs", "").split(",") if d.strip()]
            links = []
            for d in docs:
                target = self.doc_map.get("docs/games/" + d)
                if target:
                    links.append(f'<a href="{rel(url, target)}">{esc(d)}</a>')
                else:
                    self.warn(f"{url}: no game doc '{d}'")
            if links:
                page.html += f'<p class="source-note">{esc(self.t("All technical notes on this game (generated from the repository):"))} {", ".join(links)}.</p>'
            cats = [c for c, _ in GAME_CATEGORIES]
            cat = meta.get("category", "other")
            page.order = (cats.index(cat) if cat in cats else len(cats)) * 1000 + page.order
            if path.stem == "index":
                page.order = -1
                page.nav = self.t("All games")
            self.add(page, page.summary)

    def game_index(self, url: str) -> str:
        """The cards of every game page, grouped by category, with the support level of its titles."""
        out = []
        for cat, title in GAME_CATEGORIES:
            items = sorted(((int(m.get("order", 100)), m.get("title", s), s, m) for s, m in self.game_pages.items()
                            if s != "index" and m.get("category", "other") == cat))
            if not items:
                continue
            out.append(f'{self.h2(title)}<div class="cards">')
            for _, t, s, m in items:
                rows = self.support_rows([x.strip() for x in m.get("support", "").split(",") if x.strip()])
                order = list(LEVEL_WORDS)
                levels = [self.t(LEVEL_WORDS.get(lv, lv)) for lv in sorted({r.get("Level", "") for r in rows}, key=lambda lv: order.index(lv) if lv in order else 99)]
                names = ", ".join(r["Title"] for r in rows) if len(rows) > 1 else ""
                sub = esc(" · ".join(x for x in [names, "; ".join(levels)] if x))
                out.append(f'<a class="card" href="{rel(url, "games/" + s + ".html")}"><b>{esc(self.t(t))}</b><span>{sub}</span></a>')
            out.append("</div>")
        return "".join(out)

    # ---- GUI pages ----
    def gui_entries_html(self, page_slug: str, page_url: str) -> str:
        entries = [e for e in self.gui if e.page == page_slug]
        if not entries:
            return '<p class="muted">This page has no controls of its own.</p>'
        out = [f'<div class="entries" id="controls">']
        for e in entries:
            raw = e.command.replace("Command", "") or e.binding or e.name
            title = e.label or e.tooltip or re.sub(r"(?<=[a-z0-9])(?=[A-Z])", " ", raw).capitalize()
            title = self.t(title)
            badge = self.t({"full": "written", "basic": "from the source", "missing": "to be expanded"}[e.level])
            out.append(f'<article class="entry level-{e.level}" id="{e.anchor}">')
            out.append(f'<header><h3 class="eh"><a class="anchor" href="#{e.anchor}">#</a>{esc(title)}</h3><span class="kind">{esc(self.t(KIND_WORDS.get(e.kind, e.kind)))}</span>'
                       f'<span class="badge {e.level}">{esc(badge)}</span>' + (f'<span class="kind">{e.count} &times;</span>' if e.count > 1 else "") + "</header>")
            tooltip = self.t(e.tooltip) if e.tooltip else ""
            if tooltip and tooltip != title:
                out.append(f'<p class="tip"><span class="lbl">{esc(self.t("Tooltip"))}</span> {esc(tooltip)}</p>')
            if e.hand:
                out.append(f'<div class="hand">{self.entry_text(e, page_url)}</div>')
            facts = []
            if e.vm and e.vm.comment:
                facts.append(f'<p><span class="lbl">From the code</span> {esc(e.vm.comment)}</p>')
            if e.vm and e.vm.engine:
                links = []
                for call in e.vm.engine:
                    c = self.cli_for_call(call)
                    if c:
                        links.append(f'<a href="{rel(page_url, "cli/" + c.area + ".html#" + c.anchor)}"><code>engine {esc(call)}</code></a>')
                    else:
                        links.append(f"<code>engine {esc(call)}</code>")
                facts.append(f'<p><span class="lbl">Engine</span> {" · ".join(links)}</p>')
            src = []
            src.append(f'<a href="{github(e.file, e.line)}">{esc(Path(e.file).name)}:{e.line}</a>')
            if e.vm:
                src.append(f'<a href="{github(e.vm.file, e.vm.line)}">{esc(e.vm.name)}</a> in {esc(Path(e.vm.file).name)}')
            elif e.command:
                src.append(f"<code>{esc(e.command)}</code>")
            if e.binding:
                src.append(f"setting <code>{esc(e.binding)}</code>")
            facts.append(f'<p class="src"><span class="lbl">Source</span> {" · ".join(src)}</p>')
            if self.lang == "en":
                # The source facts (code comment, engine calls, file and line) are for power users: the English page only.
                out.append('<div class="facts power">' + "".join(facts) + "</div>")
            out.append("</article>")
            self.search.append({"t": title, "u": page_url + "#" + e.anchor, "s": self.t(GUI_TITLES.get(page_slug, page_slug)),
                                "x": (tooltip or (e.vm.comment if e.vm and self.lang == "en" else ""))[:160]})
        out.append("</div>")
        return "".join(out)

    def help_rows(self) -> dict[tuple[str, str], dict]:
        if not hasattr(self, "_help_rows"):
            self._help_rows = {(r["p"], r["k"]): r for r in help_catalog(self)}
        return self._help_rows

    def entry_text(self, e: GuiEntry, page_url: str) -> str:
        """A control's hand-written text. On a language page whose site catalog does not have the text yet: the F1
        help sentences in that language (the app's translations of help-controls.json), the English text folded below."""
        if self.collecting is not None:
            # The F1 texts are translated already (the app's strings); the long entry texts are not in the scope.
            row = self.help_rows().get((e.page, e.key), {})
            for k in ("w", "x", "e"):
                if row.get(k):
                    self.collecting.append({"en": i18n.norm(row[k]), "ctx": self.ctx_page + " · F1", "page": self.ctx_page, "app": True})
            saved, self.collecting = self.collecting, None
            h, _ = self.render_md(e.hand, page_url)
            self.collecting = saved
            return h
        more = self.t("More details (in English)")  # asked first, so the language probe always sees it
        # The long English text is not in the scope (the F1 texts below stand for it): not a segment for the probe.
        self.untracked += 1
        try:
            h, _ = self.render_md(e.hand, page_url)
        finally:
            self.untracked -= 1
        h = mark_fallbacks(h)
        if self.lang == "en" or "data-en" not in h:
            return h
        row = self.help_rows().get((e.page, e.key), {})
        what = self.tr(row.get("w", "")) if row.get("w") else None
        if not what:
            return h
        nxt = self.tr(row["x"]) if row.get("x") else None
        easy = self.tr(row["e"]) if row.get("e") else None
        lead = (f'<p class="easy">{esc(easy)}</p>' if easy else "") + f"<p>{esc(what)}</p>" + (f"<p>{esc(nxt)}</p>" if nxt else "")
        # The whole English text stays on the English page (one link instead of a copy in every language).
        return lead + f'<p class="en-more"><a href="EN:{page_url}#{e.anchor}" hreflang="en">{esc(more)}</a></p>'

    def build_gui_pages(self):
        for slug_, title, shot, summary in GUI_PAGES:
            url = f"gui/{slug_}.html"
            path = CONTENT / "gui" / f"{slug_}.md"
            entries = [e for e in self.gui if e.page == slug_]
            counts = {lvl: sum(1 for e in entries if e.level == lvl) for lvl in ("full", "basic", "missing")}
            docs = [d for d in GUI_DOCS.get(slug_, []) if ("docs/" + d + ".md") in self.doc_map]
            refs = ""
            if docs:
                # The reference pages stay English: marked on a language page.
                en_mark = (f' <span class="en-mark" lang="en" title="{esc(self.t("This page is only in English."))}">EN</span>'
                           if self.lang != "en" else "")
                links = "".join(f'<li><a href="{rel(url, self.doc_map["docs/" + d + ".md"])}">{esc(self.pages[self.doc_map["docs/" + d + ".md"]].title)}</a>{en_mark}</li>'
                                for d in docs if self.doc_map["docs/" + d + ".md"] in self.pages)
                refs = (f'{self.h2("Reference")}<p>{esc(self.t("The feature documentation behind this page, generated from the repository:"))}</p>'
                        f'<ul>{links}</ul>')
            self.ctx_page, self.ctx_layer, self.ctx_heading = url, "", ""
            count_line = self.t("{0} controls, read from the program’s source: {1} written, {2} with the facts from the source "
                                "(tooltip, code comment, engine call), {3} still to be expanded.")
            count_line = count_line.format(len(entries), counts["full"], counts["basic"], counts["missing"])
            gen = refs + (f'{self.h2("Every control on this page")}<p class="muted">{esc(count_line)}</p>'
                          + self.gui_entries_html(slug_, url))
            if path.exists():
                page = self.content_page(path, url, "GUI pages", gen)
            else:
                intro = f"<p class=\"lead\">{esc(summary)}</p>" + (self.shot_html(shot, f"The {title} page", url) if shot else "")
                intro += '<div class="callout warn"><div class="callout-title">To be expanded</div><p>This page is a generated skeleton: the controls below come straight from the source. The beginner / advanced / technical text follows in a later phase.</p></div>'
                out, toc = self.with_toc(intro + gen)
                page = Page(url, title, "GUI pages", out, "skeleton", summary, toc)
            page.title = page.title or title
            page.summary = page.summary or summary
            self.add(page, summary)

    # ---- CLI pages ----
    def cli_area_list(self, area: str, page_url: str) -> str:
        items = [c for c in self.cli if c.area == area]
        return "<ul>" + "".join(f'<li><a href="{rel(page_url, "cli/" + area + ".html#" + c.anchor)}"><code>engine {esc(c.key)}</code></a></li>' for c in items) + "</ul>"

    def build_cli_pages(self):
        areas: dict[str, list[CliCommand]] = {}
        for c in self.cli:
            areas.setdefault(c.area, []).append(c)
        # index
        url = "cli/index.html"
        path = CONTENT / "cli" / "index.md"
        rows = []
        for area, items in sorted(areas.items()):
            rows.append(f'<tr><td><a href="{area}.html"><code>engine {esc(area)}</code></a></td><td>{len(items)}</td>'
                        f'<td>{esc(", ".join(sorted({c.key.split(" ", 1)[1].split(" ")[0] if " " in c.key else c.key for c in items}))[:200])}</td></tr>')
        glob = "".join(f"<tr><td><code>{esc(o)}</code></td><td>{esc(d)}</td></tr>" for o, d in self.global_options)
        gen = (f'<h2>Command areas</h2><p>{len(self.cli)} commands in {len(areas)} areas, read from the engine\'s own command list '
               f'(<code>engine --help</code>, <a href="{github("engine/docs/ENGINE_CLI_WIKI.md")}">ENGINE_CLI_WIKI.md</a>).</p>'
               '<div class="table-wrap"><table><thead><tr><th>Area</th><th>Commands</th><th>Verbs</th></tr></thead><tbody>' + "".join(rows) + "</tbody></table></div>"
               '<h2 id="global-options">Global options</h2><p>These work with every command.</p><div class="table-wrap"><table><thead><tr><th>Option</th><th>What it does</th></tr></thead><tbody>'
               + glob + "</tbody></table></div>")
        if path.exists():
            page = self.content_page(path, url, "Engine (CLI)", gen)
        else:
            out, toc = self.with_toc(gen)
            page = Page(url, "Engine command line", "Engine (CLI)", out, "skeleton", "", toc)
        page.order = 0
        self.add(page)
        for area, items in sorted(areas.items()):
            url = f"cli/{area}.html"
            parts = [f'<p class="lead">{len(items)} commands of <code>engine {esc(area)}</code>. Each with every option and every value it takes, '
                     'what it does, which GUI buttons use it, and an example.</p>']
            parts.append('<div class="cmd-index"><ul>' + "".join(f'<li><a href="#{c.anchor}"><code>{esc(c.key)}</code></a></li>' for c in items) + "</ul></div>")
            # A hand-written text in layers (beginner / advanced / technical) gives the page the detail-level switch.
            self._cli_layers = False
            for c in items:
                parts.append(self.cli_entry_html(c, url))
            out, toc = self.with_toc("".join(parts))
            gamerow = next((r for r in self.support.get("Rows", []) if r.get("Command") == "engine " + area), None)
            title = f"engine {area}" + (f" ({gamerow['Title']})" if gamerow else "")
            page = Page(url, title, "Engine (CLI)", out, "generated", "", [t for t in toc if t[0] == 2][:0])
            page.layers = self._cli_layers
            self.add(page, f"engine {area} commands")

    def cli_ingame(self, c: CliCommand, url: str) -> str:
        """The in-game effect of a game's import, scan or export command, from the game's class in the support matrix."""
        verb = c.key.split(" ")[1] if " " in c.key else ""
        if verb not in ("export", "import", "scan"):
            return ""
        rows = [r for r in self.support.get("Rows", []) if r.get("Command") == "engine " + c.area]
        if not rows:
            return ""
        row = rows[0]
        cats = {x.get("Id"): x for x in self.support.get("Categories", [])}
        note = cats.get(row.get("Category"), {}).get("Note", "")
        title = row.get("Title", c.area)
        if verb in ("import", "scan"):
            text = (f"Reads {esc(title)}'s files only; nothing changes in the game. "
                    + ("A scan only shows what an import would bring and does not change the profile." if verb == "scan" else
                       "The binds come into the profile as relations; JoyPro's own data next to the game file brings back names, groups, modifiers and curves."))
        else:
            text = (f"Writes into {esc(title)}'s control files while the game is closed (refused while it runs), after a backup. "
                    f"{esc(note)} Bind file: {self.renderer.md.renderInline(row.get('BindFile', '') or '')}. ")
        doc = self.doc_map.get("docs/games/" + (row.get("Doc") or ""))
        more = f' <a href="{rel(url, doc)}">{esc(title)} details</a> ·' if doc else ""
        return (f'<p><span class="lbl">In the game</span> {text}{more} <a href="{rel(url, "flows/export.html#in-the-game")}">'
                f'what each mode changes</a></p>')

    def cli_entry_html(self, c: CliCommand, url: str) -> str:
        badge = {"full": "written", "basic": "from the source", "missing": "to be expanded"}[c.level]
        out = [f'<article class="entry cli level-{c.level}" id="{c.anchor}"><header><h3 class="eh"><a class="anchor" href="#{c.anchor}">#</a><code>engine {esc(c.key)}</code></h3>'
               f'<span class="badge {c.level}">{badge}</span></header>']
        out.append(f'<pre class="sig"><code>{esc(c.signature)}</code></pre>')
        if c.note:
            out.append(f'<p>{esc(c.note[0].upper() + c.note[1:])}</p>')
        if c.hand:
            h, env = self.render_md(c.hand, url)
            if env.get("layers"):
                self._cli_layers = True
            # An option name is not broken at its hyphens ("--" at the end of one line, "text" on the next).
            h = re.sub(r"<code>(--[^<\s]{1,40})</code>", r'<code class="opt">\1</code>', h)
            out.append(f'<div class="hand">{h}</div>')
        if c.what and "See examples" not in c.what:
            out.append(f'<p><span class="lbl">What it does</span> {esc(c.what)}</p>')
        if c.options:
            rows = []
            for flag, values, placeholder in c.options:
                doc = c.flag_docs.get(flag, "")
                if "See examples for typical values" in doc:
                    doc = ""
                if values:
                    vals = " ".join(f"<code>{esc(v)}</code>" for v in values)
                elif placeholder:
                    vals = f"<code>{esc(placeholder)}</code>"
                else:
                    vals = '<span class="muted">switch (no value)</span>'
                rows.append(f"<tr><td><code>{esc(flag)}</code></td><td>{vals}</td><td>{esc(doc)}</td></tr>")
            out.append('<div class="table-wrap"><table class="opts"><thead><tr><th>Option</th><th>Values</th><th>Meaning</th></tr></thead><tbody>' + "".join(rows) + "</tbody></table></div>")
            enum_rows = [(f, v) for f, v, _ in c.options if v]
            if enum_rows:
                ex = []
                for flag, values in enum_rows:
                    for v in values:
                        ex.append(f"engine {c.key} {flag} {v}")
                out.append('<details><summary>Every value as a command line</summary><pre><code>' + esc("\n".join(ex)) + "</code></pre></details>")
        ingame = self.cli_ingame(c, url)
        if ingame:
            out.append(ingame)
        if c.gui:
            links = " · ".join(f'<a href="{rel(url, "gui/" + e.page + ".html#" + e.anchor)}">{esc(GUI_TITLES.get(e.page, e.page))}: {esc(e.label or e.command)}</a>' for e in c.gui[:12])
            out.append(f'<p><span class="lbl">Used by the GUI</span> {links}</p>')
        ex = re.search(r"Examples:\s*```powershell\n(.*?)```", c.body, re.S)
        if ex:
            out.append(f'<p class="lbl">Example</p><pre><code>{esc(ex.group(1).strip())}</code></pre>')
        inter = re.search(r"How arguments interact:\n((?:- .+\n?)+)", c.body)
        if inter:
            h, _ = self.render_md(inter.group(1), url)
            out.append(f'<details><summary>How the options interact</summary>{h}</details>')
        out.append("</article>")
        self.search.append({"t": "engine " + c.key, "u": url + "#" + c.anchor, "s": "Engine (CLI)", "x": (c.note or c.what)[:160]})
        return "".join(out)

    # ---- content pages ----
    def build_content(self):
        for path in sorted(CONTENT.rglob("*.md")):
            r = path.relative_to(CONTENT).as_posix()
            if r.startswith("gui/") or r.startswith("cli/") or r.startswith("games/"):
                continue
            url = r[:-3] + ".html"
            section = {"getting-started": "Getting started", "how-do-i": "How do I", "flows": "Main flows", "project": "Project"}.get(r.split("/")[0], "Start")
            if r == "faq.md":
                section = "Help"
            page = self.content_page(path, url, section)
            self.add(page, page.summary)

    # ---- link and version checks ----
    def check_links(self):
        """Every link to a spot on a page (#anchor) lands on an id there, every link to a page goes to a built page, and
        every F1 'More in the wiki' link (help-controls.json: page + anchor) exists."""
        import posixpath
        ids = {u: set(re.findall(r'\bid="([^"]+)"', p.html)) for u, p in self.pages.items()}
        for u, p in self.pages.items():
            for href in set(re.findall(r'href="([^"]+)"', p.html)):
                if re.match(r"^(https?:|mailto:|data:|javascript:)", href):
                    continue
                target, _, frag = href.partition("#")
                tu = posixpath.normpath(posixpath.join(posixpath.dirname(u), target)) if target else u
                if not tu.endswith(".html"):
                    continue
                if tu not in self.pages:
                    self.warn(f"{u}: link to a page that is not built: {href}")
                elif frag and frag not in ids[tu]:
                    self.warn(f"{u}: link to a missing spot: {href}")
        for row in help_catalog(self):
            page = f"gui/{row['p']}.html"
            if page in ids and row.get("a") and row["a"] not in ids[page]:
                self.warn(f"F1 help: {row['k']} links to {page}#{row['a']}, which is not on the page")

    def check_versions(self):
        """A JoyPro version newer than the newest release note is a typo or a version that was never released."""
        notes = [tuple(int(x) for x in m.groups()) for m in (re.match(r"v(\d+)\.(\d+)\.(\d+)$", f.stem) for f in (ROOT / "docs" / "releases").glob("v*.md")) if m]
        newest = max(notes, default=(0, 0, 0))
        files = list(CONTENT.rglob("*.md")) + [f for f in (ROOT / "docs").rglob("*.md") if not PRIVATE_DOCS.match(f.relative_to(ROOT / "docs").as_posix())]
        for f in files:
            text = read(f)
            for m in re.finditer(r"\b2\.(\d+)\.(\d+)\b", text):
                v = (2, int(m.group(1)), int(m.group(2)))
                before = text[max(0, m.start() - 16):m.start()]
                if v > newest and not re.search(r"(DCS|World|Beta|Python|version of [A-Z]\w+)\W*$", before, re.I):
                    self.warn(f"{f.relative_to(ROOT).as_posix()}: version {'.'.join(map(str, v))} is newer than the newest release note "
                              f"(v{'.'.join(map(str, newest))})")

    # ---- completeness ----
    def completeness(self) -> dict:
        gui_levels = {lvl: sum(1 for e in self.gui if e.level == lvl) for lvl in ("full", "basic", "missing")}
        cli_levels = {lvl: sum(1 for c in self.cli if c.level == lvl) for lvl in ("full", "basic", "missing")}
        unbound = sorted(set(self.commands) - {e.command for e in self.gui})
        pages = {"full": [], "skeleton": [], "generated": []}
        for p in self.pages.values():
            pages.setdefault(p.status, []).append(p.url)
        missing_gui_pages = [s for s, *_ in GUI_PAGES if f"gui/{s}.html" not in self.pages]
        return {
            "gui": {"controls": len(self.gui), **gui_levels, "missing_list": [f"{e.page}: {e.label or e.key} ({e.file}:{e.line})" for e in self.gui if e.level == "missing"]},
            "cli": {"commands": len(self.cli), **cli_levels, "missing_list": [c.key for c in self.cli if c.level == "missing"]},
            "vm_commands": len(self.commands),
            "vm_commands_not_in_axaml": len(unbound),
            "pages": {k: len(v) for k, v in pages.items()},
            "pages_full": sorted(pages.get("full", [])),
            "pages_skeleton": sorted(pages.get("skeleton", [])),
            "missing_gui_pages": missing_gui_pages,
            "cli_without_entry": [c.key for c in self.cli if not c.anchor],
        }

    def build_status_page(self, comp: dict):
        url = "project/wiki-status.html"
        g, c = comp["gui"], comp["cli"]
        rows = "".join(f"<li>{esc(x)}</li>" for x in g["missing_list"][:400])
        crow = "".join(f"<li><code>engine {esc(x)}</code></li>" for x in c["missing_list"])
        out = (f'<p class="lead">How complete this wiki is, measured on every build against the source.</p>'
               f'<div class="stats"><div><b>{comp["pages"].get("full", 0)}</b>pages written fully</div><div><b>{comp["pages"].get("skeleton", 0)}</b>skeleton pages</div>'
               f'<div><b>{comp["pages"].get("generated", 0)}</b>pages generated from docs and source</div></div>'
               f'<h2>GUI controls</h2><p>{g["controls"]} controls (buttons, menu items, check boxes, switches, radio buttons) on {len(GUI_PAGES)} GUI pages: '
               f'<b>{g["full"]}</b> written by hand, <b>{g["basic"]}</b> with source facts (tooltip, code comment, engine call), <b>{g["missing"]}</b> without any description yet.</p>'
               f'<details><summary>Controls without a description ({g["missing"]})</summary><ul>{rows}</ul></details>'
               f'<h2>Engine commands</h2><p>{c["commands"]} commands: <b>{c["full"]}</b> written by hand, <b>{c["basic"]}</b> with source facts, <b>{c["missing"]}</b> without a description.</p>'
               f'<details><summary>Commands without a description ({c["missing"]})</summary><ul>{crow}</ul></details>'
               f'<h2>Pages</h2><p>Written fully:</p><ul>' + "".join(f'<li><a href="{rel(url, u)}">{esc(self.pages[u].title)}</a></li>' for u in comp["pages_full"]) + "</ul>"
               f'<p>Skeletons (source facts, text to be expanded):</p><ul>' + "".join(f'<li><a href="{rel(url, u)}">{esc(self.pages[u].title)}</a></li>' for u in comp["pages_skeleton"]) + "</ul>")
        for kind, head in (("video", "Planned videos"), ("picture", "Planned in-game pictures")):
            items = sorted({(u, i, t) for k, i, t, u in self.slots if k == kind})
            if items:
                out += (f'<h2>{head}</h2><p>{len(items)} slots in the page sources (<code>:::{kind} &lt;id&gt;</code>); the pages show '
                        f'nothing until the {"video is on YouTube (content/videos.json)" if kind == "video" else "screenshot is in assets/shots"}.</p><details><summary>The list</summary><ul>'
                        + "".join(f'<li><a href="{rel(url, u)}">{esc(u)}</a>: <code>{esc(i)}</code> {esc(t)}</li>' for u, i, t in items) + "</ul></details>")
        out, toc = self.with_toc(out)
        self.add(Page(url, "Wiki status", "Project", out, "generated", "", toc, order=90))

    # ---- output ----
    def nav_html(self, current: str) -> str:
        groups: dict[str, list[Page]] = {}
        for p in self.pages.values():
            groups.setdefault(p.section, []).append(p)
        gui_order = {f"gui/{s}.html": i for i, (s, *_) in enumerate(GUI_PAGES)}
        out = ['<nav class="sidenav" aria-label="Site">']

        def group(s: str, power: bool):
            items = groups.get(s, [])
            extra = NAV_EXTRA.get(s, [])
            if not items and not extra:
                return
            if s == "GUI pages":
                items.sort(key=lambda p: gui_order.get(p.url, 99))
            elif s == "Release notes":
                # By version (2.2.17 before 2.2.2), the newest first.
                items.sort(key=lambda p: [int(x) for x in re.findall(r"\d+", p.url)], reverse=True)
            else:
                items.sort(key=lambda p: (p.order, p.title.lower()))
            here = any(p.url == current for p in items)
            open_ = " open" if here or (not power and s in NAV_OPEN) else ""
            cls = f' class="nav-{slug(s)}"'
            out.append(f"<details{open_}{cls}><summary>{esc(self.t(s))}</summary><ul>")
            for p in items:
                cur = ' class="current"' if p.url == current else ""
                mark = '<span class="dot skeleton" title="to be expanded"></span>' if p.status == "skeleton" else ""
                if self.lang != "en" and not i18n.in_scope(p.url):
                    # A page that stays English (reference, command line, release notes): marked in the menu.
                    mark += f'<span class="en-mark" lang="en" title="{esc(self.t("This page is only in English."))}">EN</span>'
                out.append(f'<li><a{cur} href="{rel(current, p.url)}" title="{esc(p.title)}">{esc(p.nav or p.title)}</a>{mark}</li>')
            for url, text in extra:
                out.append(f'<li><a href="{rel(current, url)}">{esc(self.t(text))}</a></li>')
            out.append("</ul></details>")

        for s in NAV_MAIN:
            group(s, False)
        out.append(f'<div class="nav-power"><div class="nav-sep">{esc(self.t("For power users"))}</div>')
        for s in NAV_POWER:
            group(s, True)
        out.append("</div></nav>")
        return "".join(out)

    def write(self):
        # Files are replaced in place and stale pages removed one by one (a synced folder may hold a folder open).
        if DIST.exists():
            keep = {DIST / p.url for p in self.pages.values()}
            codes = {l.code for l in i18n.languages()}
            for f in DIST.rglob("*.html"):
                if f not in keep and f.relative_to(DIST).parts[0] not in codes:
                    try:
                        f.unlink()
                    except OSError:
                        pass
        (DIST / "assets").mkdir(parents=True, exist_ok=True)
        for f in ("site.css", "site.js", "logo.svg", "favicon.svg"):
            if (ASSETS / f).exists():
                shutil.copy(ASSETS / f, DIST / "assets" / f)
        if (ASSETS / "shots").exists():
            shutil.copytree(ASSETS / "shots", DIST / "assets" / "shots", dirs_exist_ok=True)
        if any(p.mermaid for p in self.pages.values()):
            (DIST / "assets" / "vendor").mkdir(exist_ok=True)
            shutil.copy(mermaid_file(), DIST / "assets" / "vendor" / "mermaid.min.js")
        (DIST / "assets" / "search-index.js").write_text("window.JP_SEARCH=" + json.dumps(self.search, separators=(",", ":"), ensure_ascii=False) + ";", encoding="utf-8")
        (DIST / "assets" / "i18n.js").write_text(self.i18n_script(), encoding="utf-8")
        for page in self.pages.values():
            dest = DIST / page.url
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_text(self.page_html(page), encoding="utf-8")

    # ---- languages: every page of the scope (website/i18n.py SCOPE) again under /<code>/ ----
    JS_TEXTS = ["Nothing found.", "Tap to enlarge", "Tap to fit", "Language", "This page is only in English.",
                "This page is also in your language:", "Open it", "No thanks"]

    def i18n_script(self) -> str:
        """assets/i18n.js: the languages for the switcher and the few texts site.js shows, in every language."""
        langs = i18n.languages()
        cat = self.cat or i18n.Catalog()
        texts = {}
        for l in langs[1:]:
            row = {}
            for k in self.JS_TEXTS:
                v = cat.lookup(l.code, k)
                if v:
                    row[k] = v
            texts[l.code] = row
        data = {"langs": [[l.code, l.label, l.native] for l in langs], "t": texts}
        return "window.JP_I18N=" + json.dumps(data, ensure_ascii=False, separators=(",", ":")) + ";"

    def build_languages(self, codes: list[str]):
        """Every page of the scope in each language: rendered again with the catalogs (website/i18n.py), the links moved
        under /<code>/, one search index per language."""
        import time
        en_pages, en_search, en_warnings = self.pages, self.search, len(self.warnings)
        self.cat = i18n.Catalog()
        scope = {u for u in en_pages if i18n.in_scope(u)}
        self.scope = scope
        for code in codes:
            started = time.time()
            self.lang = code
            self.pages, self.search = dict(en_pages), []
            self.build_games()
            self.build_content()
            self.build_gui_pages()
            written = set()
            for url in sorted(scope):
                page = self.pages[url]
                dest = DIST / code / url
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_text(self.page_html(page), encoding="utf-8")
                written.add(dest)
            for f in (DIST / code).rglob("*.html"):
                if f not in written:
                    try:
                        f.unlink()
                    except OSError:
                        pass
            index = [dict(e, u=code + "/" + e["u"]) for e in self.search if e["u"].split("#")[0] in scope]
            index += [e for e in en_search if e["u"].split("#")[0] not in scope]
            (DIST / "assets" / f"search-index.{code}.js").write_text(
                "window.JP_SEARCH=" + json.dumps(index, separators=(",", ":"), ensure_ascii=False) + ";", encoding="utf-8")
            print(f"  {code}: {len(written)} pages ({time.time() - started:.1f} s)")
        self.lang, self.pages, self.search = "en", en_pages, en_search
        # A warning a language pass found again (the same as in English) is listed once.
        extra = list(dict.fromkeys(self.warnings[en_warnings:]))
        self.warnings[en_warnings:] = [w for w in extra if w not in self.warnings[:en_warnings]]

    def relocate(self, text: str, url: str, code: str) -> str:
        """The links of a page rendered for its English place, for its place under /<code>/: a page of the scope is
        linked in the same language, every other page and every asset at the root."""
        import posixpath
        base = posixpath.dirname(url)
        up = "../" * (code + "/" + url).count("/")
        scope = getattr(self, "scope", set())

        def fix(m):
            attr, val = m.group(1), m.group(2)
            if val.startswith("EN:"):
                return f'{attr}="{up}{val[3:]}"'
            if re.match(r"^(https?:|mailto:|#|data:|javascript:)", val):
                return m.group(0)
            target, sep, frag = val.partition("#")
            if not target:
                return m.group(0)
            path = posixpath.normpath(posixpath.join(base, target))
            if path.startswith(".."):
                return m.group(0)
            if path in scope:
                path = code + "/" + path
            return f'{attr}="{up}{path}{sep}{frag}"'

        return re.sub(r'\b(href|src)="([^"]*)"', fix, text)

    def page_html(self, page: Page) -> str:
        """A page in the language being built (self.lang): English at the root, the others under /<code>/."""
        r = lambda u: rel(page.url, u)
        t = self.t
        code = self.lang
        toc = ""
        if len(page.toc) > 2:
            toc = f'<aside class="toc"><div class="toc-title">{esc(t("On this page"))}</div><ul>' + "".join(
                f'<li class="l{lvl}"><a href="#{hid}">{esc(x)}</a></li>' for lvl, hid, x in page.toc[:40]) + "</ul></aside>"
        status = ""
        if page.status == "skeleton":
            status = f'<span class="badge missing">{esc(t("to be expanded"))}</span>'
        # The detail level is one choice for the whole site (Beginner on the first visit, then remembered): it picks the
        # layer of a page, and at Beginner hides the command line from the menu and the source facts of the controls.
        layers = ""
        if page.url != "index.html":
            layers = (f'<div class="layer-switch" role="group" aria-label="{esc(t("Detail level"))}"><span>{esc(t("Show:"))}</span>'
                      + "".join(f'<button data-show="{k}">{esc(t(v))}</button>' for k, v in
                                (("beginner", "Beginner"), ("advanced", "Advanced"), ("technical", "Technical"), ("all", "Everything")))
                      + "</div>")
        mermaid = f'<script src="{r("assets/vendor/mermaid.min.js")}"></script>' if page.mermaid else ""
        translated = i18n.in_scope(page.url)
        notice = ""
        main_cls = ""
        if code != "en":
            # Mostly English still (no translation yet): one line says so, the single paragraphs are not marked.
            fb = page.html.count(" data-en")
            blocks = len(re.findall(r"<(?:p|li|td|h[2-4])[\s>]", page.html))
            if blocks and fb > blocks * 0.2:
                main_cls = ' class="mostly-en"'
                text = ("This page is not translated yet. It shows the English text." if fb > blocks * 0.5 else
                        "Parts of this page are not translated yet. They show the English text.")
                notice = f'<div class="callout note i18n-note"><p>{esc(t(text))}</p></div>'
        body = page.html
        if code != "en":
            # The screenshots show the English program: said once, under the page's first picture.
            note = t("The pictures show the app in English.")
            body = re.sub(r'(<figure class="shot">(?:(?!</figure>).)*?)</figcaption></figure>',
                          lambda m: m.group(1) + f' <span class="shot-lang">({esc(note)})</span></figcaption></figure>', body, count=1, flags=re.S)
        # Other languages of this page (search engines), and where the switcher goes.
        alts = ""
        if translated:
            here = (code + "/" if code != "en" else "") + page.url
            up = "../" * here.count("/")
            alts = "".join(f'<link rel="alternate" hreflang="{l.code}" href="{up}{"" if l.code == "en" else l.code + "/"}{page.url}">'
                           for l in i18n.languages())
            alts += f'<link rel="alternate" hreflang="x-default" href="{up}{page.url}">'
        cjk = i18n.font_stack(code)
        font = f'<style>:root{{--font:{cjk}, "Segoe UI", system-ui, sans-serif}}</style>' if cjk else ""
        head = f"""<!doctype html>
<html lang="{code}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{esc(page.title)} · JoyPro</title>
<meta name="description" content="{esc(page.summary or t('JoyPro 2: one profile for all your devices, in every flight and space game.'))}">
<link rel="icon" href="{r('assets/favicon.svg')}" type="image/svg+xml">
<link rel="stylesheet" href="{r('assets/site.css')}">{font}
<!--JP-ALT-->
<script>(function(){{var d=document.documentElement,l='beginner';try{{var p=new URLSearchParams(location.search),q=p.get('theme');var t=q||localStorage.getItem('jp-theme');if(t==='light'||t==='dark')d.dataset.theme=t;l=p.get('show')||localStorage.getItem('jp-layer')||'beginner';}}catch(e){{}}if(l!=='all')d.dataset.show=l;}})();</script>
</head>
<body data-root="{r('')}" data-lang="{code}" data-page="{page.url}" data-i18n="{'1' if translated else ''}">
<header class="topbar">
  <button class="menu-btn" aria-label="{esc(t('Menu'))}" aria-expanded="false">&#9776;</button>
  <a class="brand" href="{r('index.html')}"><img src="{r('assets/logo.svg')}" alt="" width="28" height="28"><span>JoyPro<sup>2</sup></span></a>
  <div class="search"><input type="search" id="q" placeholder="{esc(t('Search buttons, commands, games...'))}" autocomplete="off" aria-label="{esc(t('Search'))}"><div id="results" class="results" hidden></div></div>
  <a class="top-link" href="{DISCORD}">Discord</a>
  <a class="top-link" href="{REPO_URL}">GitHub</a>
  <span class="lang-slot"></span>
  <button class="theme-btn" aria-label="{esc(t('Light or dark'))}">&#9680;</button>
</header>
<div class="layout">
{self.nav_html(page.url)}
<main{main_cls}>
<div class="page-head"><div class="crumb">{esc(t(page.section))}</div><h1>{esc(page.title)} {status}</h1>{layers}</div>
<div class="content">
{notice}{body}
</div>
<footer class="foot">{esc(t("JoyPro is free software:"))} <a href="{REPO_URL}/blob/main/LICENSE">GPL-3.0</a> · <a href="{DISCORD}">Discord</a> · <a href="{REPO_URL}">{esc(t("Source"))}</a> · <a href="{r('project/wiki-status.html')}">{esc(t("Wiki status"))}</a></footer>
</main>
{toc}
</div>
{mermaid}
<script src="{r('assets/i18n.js')}"></script>
<script src="{r('assets/site.js')}"></script>
</body>
</html>
"""
        if code != "en":
            head = self.relocate(head, page.url, code)
            head = head.replace(f'data-root="{r("")}"', f'data-root="{"../" * (code + "/" + page.url).count("/")}"', 1)
        return head.replace("<!--JP-ALT-->", alts, 1)


def collect_segments() -> tuple[list[dict], dict[str, tuple[int, int]]]:
    """For website/i18n.py: every segment of the pages that exist in every language ({en, ctx, page}), in page order,
    and per page the English words to translate and the words the app's strings translate already (F1 texts, button
    names). The texts of the site's frame (menu, labels) count as the page "(site frame)"."""
    args = argparse.Namespace(check=False, shots=None, engine=None, out=None, help_catalog=False, langs="none")
    site = Site(args)
    site.build_docs()
    site.collecting = []
    site.build_games()
    site.build_content()
    site.build_gui_pages()
    site.ctx_page, site.ctx_layer, site.ctx_heading = "(site frame)", "", ""
    for text in Site.JS_TEXTS:
        site.t(text)
    for page in list(site.pages.values()):
        if i18n.in_scope(page.url):
            site.page_html(page)  # the frame: menu, switch, footer
            break
    segs = [s for s in site.collecting if i18n.in_scope(s["page"]) or s["page"] == "(site frame)"]
    app = i18n.Catalog().app.get("de", {})
    # Per page: [words to translate, of them in the beginner layer or outside any layer, words the app translates]
    words: dict[str, list[int]] = {}
    seen: dict[str, set[str]] = {}
    for s in segs:
        if s["en"] in seen.setdefault(s["page"], set()):
            continue
        seen[s["page"]].add(s["en"])
        row = words.setdefault(s["page"], [0, 0, 0])
        n = len(re.sub(r"`[^`]*`|\{\{[^}]*\}\}|\]\([^)]*\)|\[\[[a-z]+:[^|\]]*\|?|<[^>]+>|[*#|]", " ", s["en"]).split())
        if s.get("app") or s["en"] in app:
            row[2] += n
        else:
            row[0] += n
            if s.get("layer", "") in ("", "beginner"):
                row[1] += n
    for s in segs:
        if s.get("app") or s["en"] in app:
            s["app"] = True
    # The guard: build the pages once more as a language page and add every text it asks the catalogs for that the
    # English pass above did not see (a text only a language page shows, such as "More details (in English)", or a
    # heading the English page writes without a lookup). Such a text is counted and listed like any other segment, so
    # i18n.py report can never call a language complete while a page still shows English.
    known = {s["en"] for s in segs}
    for key, page in probe_language_keys(site).items():
        if key in known:
            continue
        known.add(key)
        n = len(key.split())
        is_app = key in app
        row = words.setdefault(page, [0, 0, 0])
        row[2 if is_app else 0] += n
        if not is_app:
            row[1] += n
        segs.append({"en": key, "ctx": page + " · generated", "page": page, "layer": "", "probe": True, "app": is_app})
    return segs, words


class _ProbeCatalog:
    """A catalog that knows no translation and remembers every text a language build asks it for."""

    def __init__(self, site):
        self.site, self.keys = site, {}

    def lookup(self, code: str, key: str):
        key = i18n.norm(key)
        if key and key not in self.keys and not self.site.untracked:
            self.keys[key] = self.site.ctx_page or "(site frame)"
        return None


def probe_language_keys(site) -> dict[str, str]:
    """Every text the pages of the scope ask the catalogs for when they are built as a language page: {text: page}.
    Nothing is written; the English build of the site object is restored afterwards."""
    saved = (site.lang, site.cat, site.collecting, dict(site.pages), list(site.search), len(site.warnings))
    probe = _ProbeCatalog(site)
    site.lang, site.cat, site.collecting = "xx", probe, None
    site.scope = {u for u in site.pages if i18n.in_scope(u)}
    try:
        site.build_games()
        site.build_content()
        site.build_gui_pages()
        keys = {k: p for k, p in probe.keys.items() if i18n.in_scope(p)}
        probe.keys = {}
        for url in sorted(site.scope):
            site.ctx_page = "(site frame)"
            site.page_html(site.pages[url])
        keys.update({k: "(site frame)" for k in probe.keys if k not in keys})
        for text in Site.JS_TEXTS:
            keys.setdefault(i18n.norm(text), "(site frame)")
    finally:
        site.lang, site.cat, site.collecting, site.pages, site.search = saved[:5]
        del site.warnings[saved[5]:]
    return keys


def mermaid_file() -> Path:
    VENDOR.mkdir(exist_ok=True)
    path = VENDOR / f"mermaid-{MERMAID_VERSION}.min.js"
    if not path.exists():
        print(f"Downloading mermaid {MERMAID_VERSION} ...")
        with urllib.request.urlopen(MERMAID_URL, timeout=60) as resp:
            data = resp.read()
        path.write_bytes(data)
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if MERMAID_SHA256 != "PINNED_ON_FIRST_BUILD" and digest != MERMAID_SHA256:
        path.unlink()
        raise SystemExit(f"mermaid.min.js has an unexpected SHA-256 ({digest}); delete website/vendor and check the pin.")
    return path


def convert_shots(src: Path):
    from PIL import Image
    out = ASSETS / "shots"
    out.mkdir(parents=True, exist_ok=True)
    n = 0
    for png in sorted(src.glob("*.png")):
        im = Image.open(png)
        if im.mode in ("RGBA", "LA", "P"):
            # A window rendered without its background (the first-start setup): onto the dark page colour.
            im = im.convert("RGBA")
            bg = Image.new("RGBA", im.size, (32, 32, 32, 255))
            im = Image.alpha_composite(bg, im)
        im = im.convert("RGB")
        if im.width > 1600:
            # The wide renders (the HUD at 1920, the style gallery): a page column is narrower than that.
            im = im.resize((1600, round(im.height * 1600 / im.width)), Image.LANCZOS)
        im.save(out / (png.stem + ".webp"), "WEBP", quality=80, method=6)
        n += 1
    print(f"{n} screenshots converted to {out}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="run the completeness gate")
    ap.add_argument("--shots", help="folder with the PNGs of WebsiteScreenshotTests")
    ap.add_argument("--engine", help="engine.exe: refresh data/support.json")
    ap.add_argument("--out", help="build into this folder instead of website/dist")
    ap.add_argument("--help-catalog", action="store_true", help="write the GUI's help mode texts (gui/JoyPro.Gui/Resources/help-controls.json)")
    ap.add_argument("--langs", default="all", help="the languages to build besides English: all (default), none, or codes (de,ja)")
    args = ap.parse_args()
    global DIST
    if args.out:
        DIST = Path(args.out).resolve()
    if args.shots:
        convert_shots(Path(args.shots))
    if args.engine:
        import subprocess
        import tempfile
        # What JoyPro ships: an empty data folder, so command lists refreshed on this PC do not change a mark.
        with tempfile.TemporaryDirectory(prefix="joypro-support-data-") as data_dir:
            env = dict(os.environ, JOYPRO_DATA_DIR=data_dir)
            res = subprocess.run([args.engine, "games", "support", "--json"], capture_output=True, text=True,
                                 encoding="utf-8", env=env)
        json.loads(res.stdout)
        (DATA / "support.json").write_text(res.stdout, encoding="utf-8")
    site = Site(args)
    catalog = help_catalog_text(site)
    if args.help_catalog:
        HELP_CATALOG.write_text(catalog, encoding="utf-8", newline=chr(10))
        print(f"Help catalog: {HELP_CATALOG} ({len(site.gui)} controls)")
    elif not HELP_CATALOG.exists() or read(HELP_CATALOG) != catalog:
        site.warn("the GUI's help texts are not up to date with the wiki: run python website/build.py --help-catalog")
    for problem in easy_problems(site):
        site.warn(problem)
    site.build_docs()
    site.build_games()
    site.build_content()
    site.build_gui_pages()
    site.build_cli_pages()
    comp = site.completeness()
    comp["warnings"] = site.warnings
    site.build_status_page(comp)
    site.check_links()
    site.check_versions()
    site.write()
    codes = [l.code for l in i18n.languages()[1:]]
    if args.langs != "all":
        wanted = [c.strip() for c in args.langs.split(",") if c.strip() and c.strip() != "none"]
        unknown = [c for c in wanted if c not in codes]
        if unknown:
            raise SystemExit("unknown language: " + ", ".join(unknown))
        codes = wanted
    if codes:
        print(f"Languages: {len(codes)}")
        site.build_languages(codes)
    comp["warnings"] = site.warnings
    summary = {k: v for k, v in comp.items() if not k.endswith("_list") and k not in ("pages_full", "pages_skeleton")}
    summary["gui"] = {k: v for k, v in comp["gui"].items() if k != "missing_list"}
    summary["cli"] = {k: v for k, v in comp["cli"].items() if k != "missing_list"}
    (DIST / "completeness.json").write_text(json.dumps(comp, indent=1), encoding="utf-8")
    size = sum(f.stat().st_size for f in DIST.rglob("*") if f.is_file())
    print(f"Built {len(site.pages)} pages into {DIST} ({size / 1e6:.1f} MB).")
    print("Completeness: " + json.dumps(summary))
    if args.check:
        sys.exit(check(comp))


def check(comp: dict) -> int:
    """The completeness gate: every GUI page and CLI command has its entry, and the number of controls and commands
    without any description, or with only source facts, never grows (website/data/completeness-baseline.json; lower it
    when you write more)."""
    problems = []
    if comp.get("warnings"):
        problems.append(f"{len(comp['warnings'])} build warnings (broken links, unknown commands or controls): " + "; ".join(comp["warnings"][:20]))
    if comp["missing_gui_pages"]:
        problems.append("GUI pages without a wiki page: " + ", ".join(comp["missing_gui_pages"]))
    if comp["cli_without_entry"]:
        problems.append("engine commands without an entry: " + ", ".join(comp["cli_without_entry"]))
    base_path = DATA / "completeness-baseline.json"
    base = json.loads(read(base_path)) if base_path.exists() else {}
    for area in ("gui", "cli"):
        limit = base.get(area + "_missing")
        if limit is not None and comp[area]["missing"] > limit:
            problems.append(f"{area}: {comp[area]['missing']} entries without a description (baseline {limit}); describe the new ones: "
                            + "; ".join(comp[area]["missing_list"][:20]))
        # The number with only source facts (no hand-written text) may not grow either: a text taken out shows here.
        limit = base.get(area + "_basic")
        if limit is not None and comp[area]["basic"] > limit:
            problems.append(f"{area}: {comp[area]['basic']} entries with only source facts (baseline {limit}); a hand-written text was removed or a new "
                            f"entry has none")
    for p in problems:
        print("COMPLETENESS: " + p)
    print("Completeness gate: " + ("FAILED" if problems else "passed") +
          f" (GUI {comp['gui']['full']} written / {comp['gui']['basic']} source facts / {comp['gui']['missing']} missing; "
          f"CLI {comp['cli']['full']} / {comp['cli']['basic']} / {comp['cli']['missing']})")
    return 1 if problems else 0


if __name__ == "__main__":
    main()
