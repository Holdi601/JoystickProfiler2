# The website's languages: build-time catalogs in the style of the app's strings (the English text is the key).
#
#   website/i18n/<code>.json     {"English segment": "translation"} per language, the app's codes (de, zh-Hans, sr-Cyrl ...)
#
# A segment is one heading, paragraph, list item, table cell, callout title, diagram label or caption, page title or
# summary, or one text of the site's frame (menu, buttons). Its key is the English Markdown source with the spaces
# collapsed. A translation keeps the Markdown: **bold**, `code`, [[kind:target|text]] (target unchanged, text
# translated), [text](url) (url unchanged), {{stat:...}}, <br>. When the English of a segment changes, its key changes:
# the page shows the English again (marked lang="en") until the new text is translated, never an outdated translation.
#
# Lookup per segment: website/i18n/<lang>.json, then the fallback languages' files (be -> ru, bs/sr-Latn -> hr,
# sr-Cyrl -> sr-Latn, zh-Hant -> zh-Hans), then the app's own strings (gui/JoyPro.Gui/Localization/Strings/<lang>.json:
# every button name and the F1 help texts, so a page names a button exactly as the translated program does), else
# English.
#
# The translators' workflow (the LocTool commands of tools/loc for the app, here for the site):
#   python website/i18n.py report                                   per language: segments, translated, missing
#   python website/i18n.py scope [--md file]                        the translated pages with their English words
#   python website/i18n.py missing --lang de [--out file]           the missing segments as {"English": ""}
#   python website/i18n.py chunks --lang de --dir <dir> [--size 12000]
#                                                                   the missing segments in files of about --size
#                                                                   characters: [{"en", "ctx", "terms"}] (ctx = page,
#                                                                   level and heading; terms = the app's translation of
#                                                                   the button names in the segment)
#   python website/i18n.py validate --in <filled.json> [--todo <chunk.json>] [--lang de]
#                                                                   every segment there, Markdown and placeholders kept
#   python website/i18n.py merge --lang de --in <filled.json> [--keep]
#                                                                   into website/i18n/de.json (only valid ones)
#   python website/i18n.py check                                    every catalog valid (exit 1 on a problem)
#   python website/i18n.py prune --lang de|all                      drops translations of segments the site no longer has
#   python website/i18n.py cyrillic                                 i18n/sr-Cyrl.json from i18n/sr-Latn.json
# A filled chunk is a JSON object {"English": "translation", ...} (the "en" values of the chunk as keys).
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

SITE = Path(__file__).resolve().parent
ROOT = SITE.parent
I18N = SITE / "i18n"
APP_LOC = ROOT / "gui" / "JoyPro.Gui" / "Localization"
CJK = ("ja", "ko", "zh-Hans", "zh-Hant")

# The pages that exist in every language (English stays at the root, the others under /<code>/): the beginner pages,
# the GUI pages, the FAQ, the game pages and the home page. Everything else (engine command line, reference, game
# reference, release notes, wiki status) is English only, with a note in the reader's language.
SCOPE = re.compile(r"^(index\.html|faq\.html|getting-started/[^/]+\.html|how-do-i/[^/]+\.html|flows/[^/]+\.html|"
                   r"games/(?!support\.html|ref/)[^/]+\.html|gui/[^/]+\.html|project/(about|jp1-vs-jp2)\.html)$")


def in_scope(url: str) -> bool:
    return bool(SCOPE.match(url))


# ----------------------------------------------------------------------------------------------------------------------
# Languages (gui/JoyPro.Gui/Localization/Languages.cs)
# ----------------------------------------------------------------------------------------------------------------------

class Language:
    def __init__(self, code: str, english: str, native: str, fallback: str | None):
        self.code, self.english, self.native, self.fallback = code, english, native, fallback

    @property
    def label(self) -> str:
        return self.native if self.native == self.english else f"{self.native} ({self.english})"


def languages() -> list[Language]:
    """English first, then the app's languages in the order of its language picker."""
    text = (APP_LOC / "Languages.cs").read_text(encoding="utf-8-sig")
    out = [Language("en", "English", "English", None)]
    for m in re.finditer(r'new\("([\w-]+)",\s*"([^"]+)",\s*"([^"]+)",\s*PluralRule\.\w+(?:,\s*"([\w-]+)")?\)', text):
        if m.group(1) != "en":
            out.append(Language(m.group(1), m.group(2), m.group(3), m.group(4)))
    return out


def font_stack(code: str) -> str:
    """The CJK fonts of a language first, as the app does (Languages.FallbackFonts): Han characters differ."""
    cjk = {"ja": '"Yu Gothic UI", "Meiryo UI", "Microsoft YaHei UI", "Malgun Gothic"',
           "zh-Hans": '"Microsoft YaHei UI", "Microsoft JhengHei UI", "Yu Gothic UI", "Malgun Gothic"',
           "zh-Hant": '"Microsoft JhengHei UI", "Microsoft YaHei UI", "Yu Gothic UI", "Malgun Gothic"',
           "ko": '"Malgun Gothic", "Yu Gothic UI", "Microsoft YaHei UI"'}
    return cjk.get(code, "")


# ----------------------------------------------------------------------------------------------------------------------
# Catalogs and lookup
# ----------------------------------------------------------------------------------------------------------------------

def norm(text: str) -> str:
    return re.sub(r"\s+", " ", text or "").strip()


def read_json(path: Path) -> dict:
    if not path.exists():
        return {}
    data = json.loads(path.read_text(encoding="utf-8-sig") or "{}")
    return {k: v for k, v in data.items() if not k.startswith("_") and isinstance(v, str)}


def write_json(path: Path, table: dict):
    path.parent.mkdir(parents=True, exist_ok=True)
    ordered = dict(sorted(table.items(), key=lambda kv: kv[0]))
    path.write_text(json.dumps(ordered, ensure_ascii=False, indent=1) + "\n", encoding="utf-8", newline="\n")


class Catalog:
    """Every language's site catalog and app strings, and the lookup with fallbacks."""

    def __init__(self):
        self.langs = languages()
        self.by_code = {l.code: l for l in self.langs}
        self.site: dict[str, dict[str, str]] = {}
        self.app: dict[str, dict[str, str]] = {}
        for l in self.langs[1:]:
            self.site[l.code] = {norm(k): v for k, v in read_json(I18N / f"{l.code}.json").items() if v.strip()}
            self.app[l.code] = {norm(k): v for k, v in read_json(APP_LOC / "Strings" / f"{l.code}.json").items() if v.strip()}

    def chain(self, code: str) -> list[str]:
        out, seen = [], set()
        c = code
        while c and c != "en" and c not in seen:
            seen.add(c)
            out.append(c)
            c = self.by_code[c].fallback if c in self.by_code else None
        return out

    def lookup(self, code: str, key: str) -> str | None:
        key = norm(key)
        if not key or code == "en":
            return None
        for c in self.chain(code):
            v = self.site.get(c, {}).get(key)
            if v:
                return v
        for c in self.chain(code):
            v = self.app.get(c, {}).get(key)
            if v and placeholders_ok(key, v):
                return v
        return None


# ----------------------------------------------------------------------------------------------------------------------
# Placeholder rules (what a translation must keep)
# ----------------------------------------------------------------------------------------------------------------------

def tokens(text: str) -> dict[str, list[str]]:
    t = text or ""
    return {
        "code": sorted(re.findall(r"`[^`]+`", t)),
        "wikilink": sorted(m.group(1) for m in re.finditer(r"\[\[([a-z]+:[^\]|]+)(?:\|[^\]]*)?\]\]", t)),
        "url": sorted(re.findall(r"\]\(([^)\s]+)\)", t)),
        "stat": sorted(re.findall(r"\{\{[^}]+\}\}", t)),
        "format": sorted(re.findall(r"(?<!\{)\{\d+\}(?!\})", t)),
        "html": sorted(x.lower().replace(" ", "") for x in re.findall(r"</?[a-zA-Z][^>]*>", t)),
        "bold": [str(t.count("**"))],
    }


def problems(en: str, tr: str) -> list[str]:
    """What a translation broke: Markdown and placeholders kept, nothing left empty."""
    out = []
    if not tr.strip():
        return ["empty"]
    a, b = tokens(en), tokens(tr)
    for k in a:
        if a[k] != b[k]:
            out.append(f"{k} differs: {a[k]} -> {b[k]}")
    if en.strip().startswith(("- ", "1. ", "#")) != tr.strip().startswith(("- ", "1. ", "#")):
        out.append("list or heading marker added or removed")
    return out


def placeholders_ok(en: str, tr: str) -> bool:
    return not problems(en, tr)


# ----------------------------------------------------------------------------------------------------------------------
# Markdown: the segments of a run of Markdown lines, translated in place
# ----------------------------------------------------------------------------------------------------------------------

PREFIX = re.compile(r"^((?:[ \t]*>)*[ \t]*(?:(?:[-*+]|\d+[.)])[ \t]+)?(?:#{1,6}[ \t]+)?)")
FALLBACK_MARK = "⁣"   # an invisible separator in front of a segment that stayed English (build.py marks it lang="en")
HEAD_ID = ("", "")  # around the English heading id, so anchors stay the same in every language


def translate_markdown(text: str, md, tr, slugger=None) -> str:
    """The run of Markdown lines with every paragraph, list item, heading and table cell passed through tr(segment)
    (it returns the translation, or None to keep the English). Lines that hold no text (code, HTML, blank) stay."""
    lines = text.split("\n")
    try:
        toks = md.parse(text)
    except Exception:
        return text
    edits: dict[int, tuple[int, str]] = {}  # first line -> (end line, new text)
    i = 0
    while i < len(toks):
        t = toks[i]
        if t.type in ("paragraph_open", "heading_open") and t.map and i + 1 < len(toks) and toks[i + 1].type == "inline":
            start, end = t.map
            content = toks[i + 1].content
            key = norm(content)
            if key and not re.fullmatch(r"\{\{[\w:-]+\}\}|<[^>]+>|!\[\]\([^)]*\)", key):
                new = tr(key, "h" if t.type == "heading_open" else "p")
                m = PREFIX.match(lines[start])
                prefix = m.group(1) if m else ""
                if t.type == "heading_open" and not prefix.lstrip(" >-*+0123456789.)").startswith("#"):
                    prefix = prefix + "#" * int(t.tag[1]) + " "
                if t.type == "heading_open" and slugger:
                    hid = slugger(key)
                    body = HEAD_ID[0] + hid + HEAD_ID[1] + (new if new is not None else key)
                elif new is None and re.fullmatch(r"!\[[^\]]*\]\(shot:[\w-]+\)", key):
                    body = key  # a picture: its caption stays English without a mark
                elif new is None:
                    body = FALLBACK_MARK + key
                else:
                    body = new
                if new is None and t.type == "heading_open":
                    body = body.replace(HEAD_ID[1], HEAD_ID[1] + FALLBACK_MARK, 1)
                edits[start] = (end, prefix + body)
            i += 2
            continue
        if t.type == "tr_open" and t.map:
            start, _ = t.map
            line = lines[start]
            cells = split_cells(line)
            out = []
            for c in cells:
                key = norm(c)
                if not key or re.fullmatch(r"[-:| ]+|`[^`]*`|[\d.,%x ]+", key):
                    out.append(c)
                    continue
                new = tr(key)
                out.append(" " + (new.replace("|", "\\|") if new is not None and "[[" not in new else new if new is not None else FALLBACK_MARK + key) + " ")
            edits[start] = (start + 1, "|" + "|".join(out) + "|")
        if t.type == "fence" and t.info.strip() == "mermaid" and t.map:
            start, end = t.map
            body = "\n".join(lines[start + 1:end - 1])
            edits[start] = (end, lines[start] + "\n" + translate_mermaid(body, tr) + "\n" + lines[end - 1])
        i += 1
    if not edits:
        return text
    out_lines: list[str] = []
    i = 0
    while i < len(lines):
        if i in edits:
            end, new = edits[i]
            out_lines.append(new)
            i = max(end, i + 1)
            continue
        out_lines.append(lines[i])
        i += 1
    return "\n".join(out_lines)


def split_cells(line: str) -> list[str]:
    """The cells of a table row (a | inside [[...|...]] or escaped is part of the cell)."""
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
        if s[i] == "\\" and s[i + 1:i + 2] == "|":
            cur += "\\|"
            i += 2
            continue
        if s[i] == "|" and not depth:
            cells.append(cur)
            cur = ""
        else:
            cur += s[i]
        i += 1
    cells.append(cur)
    return cells


def translate_mermaid(src: str, tr) -> str:
    """The quoted labels, |edge labels| and the caption line of a Mermaid diagram."""
    def label(m):
        key = norm(m.group(1))
        if not key or key == "q":
            return m.group(0)
        new = tr(key)
        return '"' + (new if new is not None else m.group(1)).replace('"', "'") + '"'

    out = []
    for line in src.split("\n"):
        cm = re.match(r"^([ \t]*%%[ \t]*caption:[ \t]*)(.*)$", line)
        if cm:
            new = tr(norm(cm.group(2)))
            out.append(cm.group(1) + (new if new is not None else cm.group(2)))
            continue
        if line.strip().startswith("%%"):
            out.append(line)
            continue
        line = re.sub(r'"([^"]*)"', label, line)
        line = re.sub(r"\|([^|\"]+)\|", lambda m: "|" + (tr(norm(m.group(1))) or m.group(1)) + "|", line)
        line = re.sub(r"(subgraph\s+\w+\s*)\[([^\]\"]+)\]", lambda m: m.group(1) + "[" + (tr(norm(m.group(2))) or m.group(2)) + "]", line)
        out.append(line)
    return "\n".join(out)


# ----------------------------------------------------------------------------------------------------------------------
# Serbian Cyrillic from Latin (tools/loc SerbianScript.cs; Markdown, placeholders and names stay Latin)
# ----------------------------------------------------------------------------------------------------------------------

_DIGRAPHS = {"Lj": "Љ", "LJ": "Љ", "lj": "љ", "Nj": "Њ", "NJ": "Њ", "nj": "њ", "Dž": "Џ", "DŽ": "Џ", "dž": "џ"}
_LETTERS = dict(zip("ABVGDĐEŽZIJKLMNOPRSTĆUFHCČŠabvgdđežzijklmnoprstćufhcčš",
                    "АБВГДЂЕЖЗИЈКЛМНОПРСТЋУФХЦЧШабвгдђежзијклмнопрстћуфхцчш"))
_KEEP = set("JoyPro JoyPro2 DCS IL vJoy HidHide ViGEmBus HOTAS Steam GitHub Windows Star Citizen War Thunder MSFS Arma Falcon "
            "BMS Nuclear Option Korea World Microsoft Flight Simulator Elite Dangerous Lua Saved Games Documents Thrustmaster "
            "Warthog Virpil VKB Winwing Logitech Saitek Honeycomb TrackIR Hell Let Loose Vietnam WARDOGS Sturmovik Battle "
            "Stalingrad Great Battles Cliffs Dover Plane X XPlane Ace Combat ACE COMBAT Discord Tobii VR Escape Esc Ctrl Alt "
            "Shift Enter Tab Up Down Left Right Home Space Del F1 OBS Kneeboard OpenKneeboard Easy Force".split())
_PROTECT = re.compile(r"`[^`]*`|\[\[[^\]]*\]\]|\]\([^)]*\)|\{\{[^}]*\}\}|\{\d+\}|<[^>]+>|https?://\S+|[A-Za-z]:\\\S*|\S+\.(?:exe|dll|json|lua|xml|ini|cfg|png|txt|log|md)\b")
_WORD = re.compile(r"[^\W\d_][\w'’-]*")


def _keep_latin(w: str) -> bool:
    if w in _KEEP:
        return True
    inner = any(c.isupper() for c in w[1:]) and not (len(w) >= 2 and w[0] in "LND" and w[1] in "JŽ")
    if inner or (2 <= len(w) <= 6 and w.upper() == w):
        return True
    if any(c.isdigit() or c in "qwxyQWXY" for c in w):
        return True
    return any(p in w.lower() for p in ("th", "ck", "oo", "ee", "ea", "ou", "gh"))


# Words that are Serbian as well as English: converted even when the English segment has them.
_BOTH = set("a i u o s k to on ne je da pa sve mod plan program problem model test start plus port original format panel "
            "data general import export profil tip film hit minus status video audio tim sport ideal normal total motor "
            "centar servis link klik fokus bonus ton gas mini max min demo info auto pad lift trim radio".split())


def to_cyrillic(latin: str, english: str = "") -> str:
    # A word of the English segment (a game, menu or command name the translation kept) stays Latin, single capital
    # letters too (pad layouts A/B/C, keys), except words that are also Serbian.
    names = {w for w in _WORD.findall(english) if w.lower() not in _BOTH and (len(w) > 1 or w.isupper())}

    def word(m):
        w = m.group(0)
        if _keep_latin(w) or w in names:
            return w
        if len(w) == 1 and w.isupper() and w in english and not re.match(r" \w", m.string[m.end():m.end() + 2]):
            return w  # a letter of the English (layout A, H.A.W.X), not the Serbian word "A ..." starting a sentence
        out, i = [], 0
        while i < len(w):
            if w[i:i + 2] in _DIGRAPHS:
                out.append(_DIGRAPHS[w[i:i + 2]])
                i += 2
                continue
            out.append(_LETTERS.get(w[i], w[i]))
            i += 1
        return "".join(out)

    parts, last = [], 0
    for m in _PROTECT.finditer(latin):
        parts.append(_WORD.sub(word, latin[last:m.start()]))
        parts.append(m.group(0))
        last = m.end()
    parts.append(_WORD.sub(word, latin[last:]))
    return "".join(parts)


# ----------------------------------------------------------------------------------------------------------------------
# The translators' tool
# ----------------------------------------------------------------------------------------------------------------------

def inventory() -> tuple[list[dict], dict[str, int]]:
    """Every segment of the translated pages ({en, ctx, page}) in page order, and the English words per page
    (segments the app's strings already translate, such as the F1 texts, counted apart)."""
    sys.path.insert(0, str(SITE))
    import build  # noqa: E402
    return build.collect_segments()


def button_terms(cat: Catalog, code: str, en: str) -> dict[str, str]:
    """The app's translation of every **button name** in a segment (the same words as in the translated program)."""
    out = {}
    for name in re.findall(r"\*\*([^*]+)\*\*", en):
        for c in cat.chain(code):
            v = cat.app.get(c, {}).get(norm(name))
            if v:
                out[name] = v
                break
    return out


def main():
    ap = argparse.ArgumentParser(description="The website's translation catalogs (see the top of website/i18n.py).")
    ap.add_argument("command", choices=["report", "scope", "missing", "chunks", "validate", "merge", "check", "prune", "cyrillic"])
    ap.add_argument("--lang")
    ap.add_argument("--out")
    ap.add_argument("--dir")
    ap.add_argument("--size", type=int, default=12000)
    ap.add_argument("--in", dest="infile")
    ap.add_argument("--todo")
    ap.add_argument("--keep", action="store_true")
    ap.add_argument("--md", help="scope: also write the table as Markdown into this file")
    ap.add_argument("--level", choices=["all", "beginner"], default="all",
                    help="missing/chunks: only the beginner layer and the text outside the layers (translate these first)")
    ap.add_argument("--pages", default="", help="missing/chunks: only these pages (comma-separated paths or prefixes)")
    a = ap.parse_args()
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8")  # the segments are printed as they are (a Windows console is cp1252)
        except (AttributeError, ValueError):
            pass
    cat = Catalog()
    codes = [l.code for l in cat.langs[1:]]

    if a.command in ("report", "missing", "chunks", "scope", "prune"):
        segs, words = inventory()
        keys = list(dict.fromkeys(s["en"] for s in segs))
        own = [k for k in keys if not any(cat.app.get(c, {}).get(k) for c in ("de",))]

    if a.command == "report":
        total_words = sum(len(k.split()) for k in own)
        print(f"{len(keys)} segments on {len(words)} pages; {len(own)} need the site catalog ({total_words} English words), "
              f"{len(keys) - len(own)} come from the app's strings")
        # The language probe (build.collect_segments): texts only a language page asks for. They are counted above like
        # every other segment; listed here so the collector can be taught them.
        probe = sorted({s["en"] for s in segs if s.get("probe") and s["en"] in set(own)})
        if probe:
            print(f"{len(probe)} segments seen only when a page is built in another language (generated or frame texts): "
                  + "; ".join(p[:60] for p in probe[:10]))
        for code in codes:
            done = sum(1 for k in own if cat.lookup(code, k) is not None and k in cat.site.get(code, {}))
            fb = sum(1 for k in own if cat.lookup(code, k) is not None and k not in cat.site.get(code, {}))
            stale = sum(1 for k in cat.site.get(code, {}) if k not in set(keys))
            print(f"{code:8} {done:6} translated, {fb:5} via fallback, {len(own) - done - fb:6} missing, {stale:5} no longer used")
        return 0

    if a.command == "scope":
        rows = sorted(words.items(), key=lambda kv: kv[0])
        lines = ["| Page | English words to translate | of them for beginners (beginner layer and text outside the layers) | "
                 "Words the app translates already (F1, button names) |", "|---|---:|---:|---:|"]
        tw = tb = ta = 0
        for page, (w, b, app) in rows:
            lines.append(f"| {page} | {w} | {b} | {app} |")
            tw, tb, ta = tw + w, tb + b, ta + app
        lines.append(f"| **{len(rows)} pages** | **{tw}** | **{tb}** | **{ta}** |")
        text = "\n".join(lines)
        print(text)
        if a.md:
            Path(a.md).write_text(text + "\n", encoding="utf-8")
        return 0

    if a.command in ("missing", "chunks"):
        if not a.lang:
            ap.error("--lang is required")
        seen, todo = set(), []
        for s in segs:
            k = s["en"]
            if k in seen or k in cat.site.get(a.lang, {}) or cat.lookup(a.lang, k) is not None:
                continue
            if a.level == "beginner" and s.get("layer", "") not in ("", "beginner"):
                continue
            if a.pages and not any(s["page"] == p or s["page"].startswith(p) for p in a.pages.split(",") if p):
                continue
            seen.add(k)
            todo.append(s)
        if a.command == "missing":
            text = json.dumps({s["en"]: "" for s in todo}, ensure_ascii=False, indent=1)
            Path(a.out).write_text(text + "\n", encoding="utf-8") if a.out else print(text)
            print(f"{len(todo)} segments without a {a.lang} translation", file=sys.stderr)
            return 0
        if not a.dir:
            ap.error("--dir is required")
        out = Path(a.dir)
        out.mkdir(parents=True, exist_ok=True)
        index, chars, chunk = 0, 0, []

        def flush():
            nonlocal index, chars, chunk
            if not chunk:
                return
            index += 1
            (out / f"chunk-{index:02}.json").write_text("[\n" + ",\n".join(json.dumps(c, ensure_ascii=False) for c in chunk) + "\n]\n",
                                                       encoding="utf-8", newline="\n")
            chunk, chars = [], 0

        for s in todo:
            item = {"en": s["en"], "ctx": s["ctx"]}
            terms = button_terms(cat, a.lang, s["en"])
            if terms:
                item["terms"] = terms
            chunk.append(item)
            chars += len(s["en"])
            if chars >= a.size:
                flush()
        flush()
        print(f"{len(todo)} segments in {index} chunks in {out}")
        return 0

    if a.command == "validate":
        done = json.loads(Path(a.infile).read_text(encoding="utf-8-sig"))
        found = []
        if a.todo:
            for item in json.loads(Path(a.todo).read_text(encoding="utf-8-sig")):
                if not str(done.get(item["en"], "")).strip():
                    found.append("missing: " + item["en"][:120])
        for k, v in done.items():
            for p in problems(k, str(v)):
                found.append(f"{k[:80]!r}: {p}")
        for p in found:
            print(p)
        print("OK" if not found else f"{len(found)} problems")
        return 0 if not found else 1

    if a.command == "merge":
        if not (a.lang and a.infile):
            ap.error("--lang and --in are required")
        path = I18N / f"{a.lang}.json"
        table = read_json(path)
        incoming = json.loads(Path(a.infile).read_text(encoding="utf-8-sig"))
        added = bad = 0
        for k, v in incoming.items():
            if not isinstance(v, str) or not v.strip():
                continue
            k = norm(k)
            p = problems(k, v)
            if p:
                bad += 1
                print(f"skipped {k[:80]!r}: {'; '.join(p)}", file=sys.stderr)
                continue
            if a.keep and k in table:
                continue
            table[k] = v.strip()
            added += 1
        write_json(path, table)
        print(f"{a.lang}: {added} translations merged, {bad} skipped, {len(table)} in total")
        return 1 if bad else 0

    if a.command == "check":
        n = 0
        for code in codes:
            for k, v in read_json(I18N / f"{code}.json").items():
                for p in problems(k, v):
                    n += 1
                    print(f"{code}.json: {k[:80]!r}: {p}")
        print("no problems" if not n else f"{n} problems")
        return 1 if n else 0

    if a.command == "prune":
        live = set(keys)
        for code in (codes if a.lang in (None, "all") else [a.lang]):
            path = I18N / f"{code}.json"
            table = read_json(path)
            kept = {k: v for k, v in table.items() if norm(k) in live}
            if len(kept) != len(table):
                write_json(path, kept)
            print(f"{code}: {len(table) - len(kept)} dropped, {len(kept)} kept")
        return 0

    if a.command == "cyrillic":
        latin = read_json(I18N / "sr-Latn.json")
        write_json(I18N / "sr-Cyrl.json", {k: to_cyrillic(v, k) for k, v in latin.items()})
        print(f"sr-Cyrl: {len(latin)} segments")
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
