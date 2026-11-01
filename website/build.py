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
}

# Which AXAML file belongs to which GUI page (MainWindow.axaml is split by its page sections).
VIEW_PAGES = {
    "CockpitsView.axaml": "cockpits",
    "StatisticsView.axaml": "statistics",
    "VirtualJoystickView.axaml": "vjoy",
    "VirtualFunctionEditorView.axaml": "vjoy",
    "GameSetupWindow.axaml": "first-start",
    "SetupWizardWindow.axaml": "setup-wizard",
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
}

STAGE_PAGES = {
    "IsStartStage": "start", "IsRelationsStage": "relations", "IsBindsStage": "binds", "IsModifiersStage": "modifiers",
    "IsLayoutStage": "layout", "IsExportStage": "export", "IsHudStage": "hud", "IsFfbTelemetryStage": "ffb",
    "IsVirtualJoystickStage": "vjoy", "IsCockpitsStage": "cockpits", "IsStatisticsStage": "statistics",
    "IsSettingsStage": "settings", "IsAdvancedStage": "advanced",
}

CONTROL_KINDS = ("Button", "ToggleButton", "SplitButton", "DropDownButton", "RepeatButton", "MenuItem", "CheckBox",
                 "RadioButton", "ToggleSwitch")

# Docs that stay out of the public site (the same rules as tools/export_public.ps1).
PRIVATE_DOCS = re.compile(r"^(internal/|licensing-audit\.md$|releases/README\.md$)")
PRIVATE_IMAGES = re.compile(r"^docs/ui/(?!relation-builder-)[^/]*\.png$", re.I)

LAYER_TITLES = {"beginner": "For beginners", "advanced": "Advanced", "technical": "Technical internals"}

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
    r"|\buser (decision|approved|said|wish)|\bthe user approved\b|\btester\b|\bdev[- ]PC\b")


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


class Renderer:
    def __init__(self, site: "Site"):
        self.site = site
        self.md = MarkdownIt("commonmark", {"html": True, "linkify": False, "typographer": False}).enable("table").enable("strikethrough")
        default_fence = self.md.renderer.rules.get("fence")

        def fence(tokens, idx, options, env):
            tok = tokens[idx]
            if tok.info.strip() == "mermaid":
                env["mermaid"] = True
                return '<div class="diagram"><pre class="mermaid">' + esc(tok.content) + "</pre></div>\n"
            return default_fence(tokens, idx, options, env)

        self.md.renderer.rules["fence"] = fence

    def render(self, text: str, page_url: str, env: dict, source: str | None = None) -> str:
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
        i = 0
        while i < len(lines):
            line = lines[i]
            m = re.match(r"^:::(\w+)\s*(.*)$", line)
            if m and m.group(1) in ("layer", "video", "picture", "note", "warn", "tip", "ingame"):
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
                    title = LAYER_TITLES.get(arg, arg.title())
                    out.append(f'<section class="layer layer-{esc(arg)}" data-layer="{esc(arg)}"><div class="layer-badge">{esc(title)}</div>\n')
                    out.append(self.render(body, page_url, env))
                    out.append("</section>\n")
                elif kind == "video":
                    out.append(site.video_slot(arg, page_url))
                elif kind == "picture":
                    out.append(site.picture_slot(arg, page_url))
                elif kind == "ingame":
                    out.append(f'<div class="callout ingame"><div class="callout-title">In the game: {esc(arg)}</div>\n')
                    out.append(self.render(body, page_url, env))
                    out.append("</div>\n")
                else:
                    titles = {"note": "Note", "warn": "Careful", "tip": "Tip"}
                    out.append(f'<div class="callout {kind}"><div class="callout-title">{esc(arg or titles[kind])}</div>\n')
                    out.append(self.render(body, page_url, env))
                    out.append("</div>\n")
                out.append("")
                i = j + 1
                continue
            out.append(line)
            i += 1
        text = "\n".join(out)
        text = re.sub(r"\{\{stat:([\w.]+)\}\}", lambda mm: site.stat(mm.group(1)), text)
        text = re.sub(r"\{\{game-card\}\}", lambda mm: site.game_card(env.get("game_support", []), page_url), text)
        text = re.sub(r"\{\{game-index\}\}", lambda mm: site.game_index(page_url), text)
        text = re.sub(r"\{\{(gui-table|cli-table):([\w-]+)\}\}", lambda mm: site.placeholder(mm.group(1), mm.group(2), page_url), text)
        text = re.sub(r"!\[([^\]]*)\]\(shot:([\w-]+)\)", lambda mm: site.shot_html(mm.group(2), mm.group(1), page_url), text)
        text = re.sub(r"\[\[(cli|gui|doc|game|page):([^\]|]+)(?:\|([^\]]+))?\]\]", lambda mm: site.wikilink(mm.group(1), mm.group(2).strip(), mm.group(3), page_url), text)
        return text

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
        return (f'<div class="video-slot" data-video="{esc(vid)}"><div class="video-icon">&#9654;</div><div><strong>Video: {esc(title or vid)}</strong>'
                f'<br>Coming later: one YouTube video per topic, recorded with the English interface, with subtitles in every language JoyPro speaks.'
                f'</div></div>\n')

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
                return f'<a href="{rel(page_url, "gui/" + page + ".html")}">{esc(text or GUI_TITLES.get(page, page))}</a>'
            e = next((e for e in self.gui if e.page == page and (e.key == key or e.command == key or e.label == key)), None)
            if not e:
                self.warn(f"{page_url}: no control '{target}'")
                return f"<strong>{esc(text or key)}</strong>"
            return f'<a href="{rel(page_url, "gui/" + page + ".html#" + e.anchor)}"><strong>{esc(text or e.label or key)}</strong></a>'
        if kind == "doc":
            url = self.doc_map.get("docs/" + target + ".md") or self.doc_map.get(target)
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
            text = html.unescape(re.sub(r"<[^>]+>", "", inner))
            hid = slug(text)
            n = 2
            base = hid
            while hid in used:
                hid = f"{base}-{n}"
                n += 1
            used.add(hid)
            if level in (2, 3):
                toc.append((level, hid, text))
            return f'<h{level} id="{hid}"><a class="anchor" href="#{hid}">#</a>{inner}</h{level}>'

        out = re.sub(r"<h([2-4])>(.*?)</h\1>", fix, out)
        return out, toc

    def content_page(self, path: Path, url: str, section: str, extra_html: str = "") -> Page:
        meta, text = front_matter(read(path))
        env = {"game_support": [x.strip() for x in meta.get("support", "").split(",") if x.strip()]}
        out, env = self.render_md(text, url, None, env)
        out += extra_html
        out, toc = self.with_toc(out)
        status = meta.get("status", "skeleton")
        page = Page(url, meta.get("title", path.stem), section, out, status, meta.get("summary", ""), toc,
                    bool(env.get("mermaid")), bool(env.get("layers")), int(meta.get("order", 100)))
        return page

    def build_docs(self):
        docs = ROOT / "docs"
        for path in sorted(docs.rglob("*.md")):
            r = path.relative_to(docs).as_posix()
            if PRIVATE_DOCS.match(r) or r.startswith("branding/") or r.startswith("cockpits/") or r.startswith("device-reference/"):
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
            title = m.group(1).strip() if m else Path(source).stem
            text = re.sub(r"^# .+\n", "", text, count=1, flags=re.M)
            section = "Game reference" if url.startswith("games/ref/") else "Games" if url.startswith("games/") else "Release notes" if url.startswith("releases/") else "Reference"
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
                        else (f"<code>{esc(cmd)}</code>" if cmd else "none"))
            facts = [("Support", esc(LEVEL_WORDS.get(r.get("Level", ""), r.get("Level", "")))), ("Engine command", cmd_html)]
            if r.get("BindFile"):
                facts.append(("Bind file", self.renderer.md.renderInline(public_text(r["BindFile"]))))
            if r.get("InputLimit"):
                facts.append(("Inputs the game reads", esc(public_text(r["InputLimit"]))))
            if r.get("ListSource"):
                facts.append(("Command list from", self.renderer.md.renderInline(public_text(r["ListSource"]))))
            if r.get("TestResult"):
                facts.append(("In-game test", esc(r["TestResult"] + (" steps passed (" + r["TestDate"] + ")" if r.get("TestDate") else ""))))
            out.append(f'<details class="support-card"{" open" if len(rows) <= 2 else ""}><summary><strong>{esc(r["Title"])}</strong>'
                       f' <span class="lvl lvl-{esc(r.get("Level", ""))}">{esc(LEVEL_WORDS.get(r.get("Level", ""), r.get("Level", "")))}</span></summary>')
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
                marks.append(f'<li class="{cls}"><span class="m">{esc(mark)}</span> {esc(label)}'
                             + (f'<span class="n">{self.renderer.md.renderInline(note)}</span>' if note and cls != "yes" else "") + "</li>")
            out.append('<ul class="features">' + "".join(marks) + "</ul></details>")
        out.append(f'<p class="muted small">From the support matrix, generated from the engine\'s code: '
                   f'<a href="{rel(url, "games/support.html")}">all games</a>.</p></div>')
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
        return (f'<div class="picture-slot" data-picture="{esc(pid)}"><div class="picture-icon">&#128247;</div><div><strong>In-game picture follows: {esc(title or pid)}</strong>'
                f'<br>A screenshot from the game itself, taken during JoyPro\'s in-game test runs, comes here later.</div></div>\n')

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
                page.html += f'<p class="source-note">All technical notes on this game (generated from the repository): {", ".join(links)}.</p>'
            cats = [c for c, _ in GAME_CATEGORIES]
            cat = meta.get("category", "other")
            page.order = (cats.index(cat) if cat in cats else len(cats)) * 1000 + page.order
            if path.stem == "index":
                page.order = -1
                page.nav = "All games"
            self.add(page, page.summary)

    def game_index(self, url: str) -> str:
        """The cards of every game page, grouped by category, with the support level of its titles."""
        out = []
        for cat, title in GAME_CATEGORIES:
            items = sorted(((int(m.get("order", 100)), m.get("title", s), s, m) for s, m in self.game_pages.items()
                            if s != "index" and m.get("category", "other") == cat))
            if not items:
                continue
            out.append(f'<h2>{esc(title)}</h2><div class="cards">')
            for _, t, s, m in items:
                rows = self.support_rows([x.strip() for x in m.get("support", "").split(",") if x.strip()])
                order = list(LEVEL_WORDS)
                levels = [LEVEL_WORDS.get(lv, lv) for lv in sorted({r.get("Level", "") for r in rows}, key=lambda lv: order.index(lv) if lv in order else 99)]
                names = ", ".join(r["Title"] for r in rows) if len(rows) > 1 else ""
                sub = esc(" · ".join(x for x in [names, "; ".join(levels)] if x))
                out.append(f'<a class="card" href="{rel(url, "games/" + s + ".html")}"><b>{esc(t)}</b><span>{sub}</span></a>')
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
            badge = {"full": "written", "basic": "from the source", "missing": "to be expanded"}[e.level]
            out.append(f'<article class="entry level-{e.level}" id="{e.anchor}">')
            out.append(f'<header><h3 class="eh"><a class="anchor" href="#{e.anchor}">#</a>{esc(title)}</h3><span class="kind">{esc(e.kind)}</span>'
                       f'<span class="badge {e.level}">{badge}</span>' + (f'<span class="kind">{e.count} places</span>' if e.count > 1 else "") + "</header>")
            if e.tooltip and e.tooltip != title:
                out.append(f'<p class="tip"><span class="lbl">Tooltip</span> {esc(e.tooltip)}</p>')
            if e.hand:
                h, _ = self.render_md(e.hand, page_url)
                out.append(f'<div class="hand">{h}</div>')
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
            out.append('<div class="facts">' + "".join(facts) + "</div>")
            out.append("</article>")
            self.search.append({"t": title, "u": page_url + "#" + e.anchor, "s": GUI_TITLES.get(page_slug, page_slug) + " page",
                                "x": (e.tooltip or (e.vm.comment if e.vm else ""))[:160]})
        out.append("</div>")
        return "".join(out)

    def build_gui_pages(self):
        for slug_, title, shot, summary in GUI_PAGES:
            url = f"gui/{slug_}.html"
            path = CONTENT / "gui" / f"{slug_}.md"
            entries = [e for e in self.gui if e.page == slug_]
            counts = {lvl: sum(1 for e in entries if e.level == lvl) for lvl in ("full", "basic", "missing")}
            docs = [d for d in GUI_DOCS.get(slug_, []) if ("docs/" + d + ".md") in self.doc_map]
            refs = ""
            if docs:
                links = "".join(f'<li><a href="{rel(url, self.doc_map["docs/" + d + ".md"])}">{esc(self.pages[self.doc_map["docs/" + d + ".md"]].title)}</a></li>'
                                for d in docs if self.doc_map["docs/" + d + ".md"] in self.pages)
                refs = f'<h2>Reference</h2><p>The feature documentation behind this page, generated from the repository:</p><ul>{links}</ul>'
            gen = refs + (f'<h2>Every control on this page</h2><p class="muted">{len(entries)} controls, read from the program\'s source: '
                   f'{counts["full"]} written, {counts["basic"]} with the facts from the source (tooltip, code comment, engine call), '
                   f'{counts["missing"]} still to be expanded.</p>' + self.gui_entries_html(slug_, url))
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
            section = {"getting-started": "Getting started", "flows": "Main flows", "project": "Project"}.get(r.split("/")[0], "Start")
            if r == "faq.md":
                section = "Help"
            page = self.content_page(path, url, section)
            self.add(page, page.summary)

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
        out, toc = self.with_toc(out)
        self.add(Page(url, "Wiki status", "Project", out, "generated", "", toc, order=90))

    # ---- output ----
    def nav_html(self, current: str) -> str:
        sections = ["Start", "Getting started", "Main flows", "GUI pages", "Games", "Engine (CLI)", "Reference", "Game reference", "Help", "Project", "Release notes"]
        groups: dict[str, list[Page]] = {}
        for p in self.pages.values():
            groups.setdefault(p.section, []).append(p)
        gui_order = {f"gui/{s}.html": i for i, (s, *_) in enumerate(GUI_PAGES)}
        out = ['<nav class="sidenav" aria-label="Site">']
        for s in sections:
            items = groups.get(s, [])
            if not items:
                continue
            if s == "GUI pages":
                items.sort(key=lambda p: gui_order.get(p.url, 99))
            elif s == "Release notes":
                items.sort(key=lambda p: p.url, reverse=True)
            else:
                items.sort(key=lambda p: (p.order, p.title.lower()))
            open_ = " open" if any(p.url == current for p in items) or s in ("Start", "Getting started", "Main flows") else ""
            out.append(f"<details{open_}><summary>{esc(s)}</summary><ul>")
            for p in items:
                cls = ' class="current"' if p.url == current else ""
                mark = '<span class="dot skeleton" title="to be expanded"></span>' if p.status == "skeleton" else ""
                out.append(f'<li><a{cls} href="{rel(current, p.url)}" title="{esc(p.title)}">{esc(p.nav or p.title)}</a>{mark}</li>')
            out.append("</ul></details>")
        out.append("</nav>")
        return "".join(out)

    def write(self):
        # Files are replaced in place and stale pages removed one by one (a synced folder may hold a folder open).
        if DIST.exists():
            keep = {DIST / p.url for p in self.pages.values()}
            for f in DIST.rglob("*.html"):
                if f not in keep:
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
        for page in self.pages.values():
            dest = DIST / page.url
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_text(self.page_html(page), encoding="utf-8")

    def page_html(self, page: Page) -> str:
        r = lambda u: rel(page.url, u)
        toc = ""
        if len(page.toc) > 2:
            toc = '<aside class="toc"><div class="toc-title">On this page</div><ul>' + "".join(
                f'<li class="l{lvl}"><a href="#{hid}">{esc(t)}</a></li>' for lvl, hid, t in page.toc[:40]) + "</ul></aside>"
        status = ""
        if page.status == "skeleton":
            status = '<span class="badge missing">to be expanded</span>'
        layers = ""
        if page.layers:
            layers = ('<div class="layer-switch" role="group" aria-label="Detail level"><span>Show:</span>'
                      '<button data-show="all" class="on">Everything</button><button data-show="beginner">Beginner</button>'
                      '<button data-show="advanced">Advanced</button><button data-show="technical">Technical</button></div>')
        mermaid = f'<script src="{r("assets/vendor/mermaid.min.js")}"></script>' if page.mermaid else ""
        return f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{esc(page.title)} · JoyPro</title>
<meta name="description" content="{esc(page.summary or 'JoyPro 2: one profile for all your devices, in every flight and space game.')}">
<link rel="icon" href="{r('assets/favicon.svg')}" type="image/svg+xml">
<link rel="stylesheet" href="{r('assets/site.css')}">
<script>(function(){{try{{var q=new URLSearchParams(location.search).get('theme');var t=q||localStorage.getItem('jp-theme');if(t==='light'||t==='dark')document.documentElement.dataset.theme=t;}}catch(e){{}}}})();</script>
</head>
<body data-root="{r('')}">
<header class="topbar">
  <button class="menu-btn" aria-label="Menu" aria-expanded="false">&#9776;</button>
  <a class="brand" href="{r('index.html')}"><img src="{r('assets/logo.svg')}" alt="" width="28" height="28"><span>JoyPro<sup>2</sup></span></a>
  <div class="search"><input type="search" id="q" placeholder="Search buttons, commands, games..." autocomplete="off" aria-label="Search"><div id="results" class="results" hidden></div></div>
  <a class="top-link" href="{DISCORD}">Discord</a>
  <a class="top-link" href="{REPO_URL}">GitHub</a>
  <button class="theme-btn" aria-label="Light or dark">&#9680;</button>
</header>
<div class="layout">
{self.nav_html(page.url)}
<main>
<div class="page-head"><div class="crumb">{esc(page.section)}</div><h1>{esc(page.title)} {status}</h1>{layers}</div>
<div class="content">
{page.html}
</div>
<footer class="foot">JoyPro is free software under the <a href="{REPO_URL}/blob/main/LICENSE">GPL-3.0</a> · <a href="{DISCORD}">Discord</a> · <a href="{REPO_URL}">Source</a> · Generated from the source; <a href="{r('project/wiki-status.html')}">wiki status</a></footer>
</main>
{toc}
</div>
{mermaid}
<script src="{r('assets/site.js')}"></script>
</body>
</html>
"""


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
    site.build_docs()
    site.build_games()
    site.build_content()
    site.build_gui_pages()
    site.build_cli_pages()
    comp = site.completeness()
    comp["warnings"] = site.warnings
    site.build_status_page(comp)
    site.write()
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
