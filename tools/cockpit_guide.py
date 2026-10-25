"""JoyPro's cockpit guide tool (docs/cockpits.md, "The cockpit guide").

The guide explains every panel and control of a cockpit in plain words: EngineOnly_Runtime/DB/Cockpits/guide/*.json
(format joypro-cockpit-guide), translations next to them (<id>.<lang>.json, format joypro-cockpit-guide-text). The
same rules as the GUI's CockpitGuide.Check (gui/JoyPro.Gui.Tests CockpitGuideTests).

    py -3 tools/cockpit_guide.py check [guide.json ...]          the rules (all guides when no file is given)
    py -3 tools/cockpit_guide.py coverage <guide id> [--pictures <pictures.json or set folder>] [--seat N] [--all]
                                                                 which game controls of a picture set the guide explains;
                                                                 always: the catalog items and grip controls of its variants
                                                                 that no guide control names
    py -3 tools/cockpit_guide.py stats [--lang de]               areas, panels, controls and characters per guide
    py -3 tools/cockpit_guide.py report --pictures <root> [--game DCS]   coverage of every set under a pictures root
                                                                 (<root>/<game>-<set>/pictures.json or <root>/<game>/<set>/pictures.json)

Matching (also CockpitGuide.cs): a control's "match" keys are tried against a picture callout in this order of forms:
"cmd:<glob>" (any command id or name of the callout), "label:<text>" (the callout's label, letters and digits only,
any case), "item:<panel>:<n>" (an item of the catalog's panel), "control:<id>" (a grip control of the catalog),
otherwise a glob of the callout id ("PNT-BTN-MFD-L-*"; * and ? wildcards, any case). The first control in guide order
that matches a callout gets it.
"""

import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GUIDE = os.environ.get("JOYPRO_COCKPIT_GUIDE") or os.path.join(REPO, "EngineOnly_Runtime", "DB", "Cockpits", "guide")

FORMAT = "joypro-cockpit-guide"
TEXT_FORMAT = "joypro-cockpit-guide-text"
WHERE = ["glareshield", "front-left", "front-center", "front-right", "left-console", "right-console", "center-pedestal",
         "overhead", "left-side", "right-side", "floor", "rear", "seat", "helmet", "hotas", "other"]
TYPES = ["switch2", "switch3", "switch", "knob", "selector", "button", "lever", "handle", "wheel", "guard", "gauge",
         "display", "light", "indicator", "keypad", "rocker", "hat", "slider", "encoder", "pedal", "trigger", "axis",
         "screen", "cover", "other"]
ID = re.compile(r"^[a-z0-9][a-z0-9._-]*$")
LIMITS = {"name": (2, 90), "text": (40, 800), "context": (30, 800), "area-text": (15, 500), "position": (1, 40),
          "position-text": (8, 400), "intro": (40, 1500)}
BANNED = re.compile(r"\b(TODO|TBD|lorem|ipsum|FIXME|XXX)\b|\?\?|!", re.I)


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _files(folder, fmt):
    if not os.path.isdir(folder):
        return []
    out = []
    for f in sorted(os.listdir(folder)):
        if not f.endswith(".json") or f.startswith("_"):
            continue
        path = os.path.join(folder, f)
        try:
            if load(path).get("format") == fmt:
                out.append(path)
        except Exception:
            if fmt == FORMAT:
                out.append(path)
    return out


def guide_files(folder=GUIDE):
    """The guide and part files (format joypro-cockpit-guide; any file name, part files often <id>.part2.json)."""
    return _files(folder, FORMAT)


def text_files(folder=GUIDE):
    """The translations (format joypro-cockpit-guide-text, <id>.<lang>.json)."""
    return _files(folder, TEXT_FORMAT)


def all_guides(folder=GUIDE):
    """id -> guide, part files (partOf) merged into their guide."""
    guides, parts = {}, []
    for f in guide_files(folder):
        g = load(f)
        g["_file"] = os.path.basename(f)
        if g.get("partOf"):
            parts.append(g)
        else:
            guides[g.get("id", "")] = g
    for p in parts:
        host = guides.get(p["partOf"])
        if host is None:
            guides.setdefault("?" + p["_file"], p)
            continue
        merge_areas(host.setdefault("areas", []), p.get("areas", []))
        host.setdefault("_parts", []).append(p["_file"])
        for s in p.get("sources", []):
            if s.get("id") not in {x.get("id") for x in host.setdefault("sources", [])}:
                host["sources"].append(s)
    return guides


def merge_areas(target, extra):
    for a in extra:
        mine = next((x for x in target if x.get("id") == a.get("id")), None)
        if mine is None:
            target.append(json.loads(json.dumps(a)))
            continue
        for p in a.get("panels", []):
            pm = next((x for x in mine.setdefault("panels", []) if x.get("id") == p.get("id")), None)
            if pm is None:
                mine["panels"].append(json.loads(json.dumps(p)))
            else:
                ids = {c.get("id") for c in p.get("controls", [])}
                pm["controls"] = [c for c in pm.get("controls", []) if c.get("id") not in ids] + json.loads(json.dumps(p.get("controls", [])))
                for k in ("name", "text", "context", "db", "src", "page"):
                    if p.get(k):
                        pm[k] = p[k]


def resolve(guides, gid, seen=None):
    """The guide with its parents (inherits): the parent's areas, panels and controls, this guide's replace by id."""
    seen = seen or set()
    g = guides.get(gid)
    if g is None or gid in seen:
        return None
    seen.add(gid)
    if not g.get("inherits"):
        return g
    parent = resolve(guides, g["inherits"], seen)
    if parent is None:
        return g
    r = json.loads(json.dumps(parent))
    for k, v in g.items():
        if k not in ("areas", "inherits", "remove", "sources"):
            r[k] = v
    remove = set(g.get("remove", []))
    for a in r.get("areas", []):
        a["panels"] = [p for p in a.get("panels", []) if p.get("id") not in remove]
        for p in a["panels"]:
            p["controls"] = [c for c in p.get("controls", []) if p.get("id") + "/" + c.get("id") not in remove]
    merge_areas(r.setdefault("areas", []), g.get("areas", []))
    src = {s.get("id"): s for s in parent.get("sources", [])}
    for s in g.get("sources", []):
        src[s.get("id")] = s
    r["sources"] = list(src.values())
    r["inherits"] = g["inherits"]
    return r


def controls_of(g):
    for a in g.get("areas", []):
        for p in a.get("panels", []):
            for c in p.get("controls", []):
                yield a, p, c


def length(problems, where, what, value, key):
    lo, hi = LIMITS[key]
    value = (value or "").strip()
    if not value:
        problems.append(f"{where}: no {what}")
        return
    if len(value) < lo:
        problems.append(f"{where}: {what} too short ({len(value)} < {lo}): {value!r}")
    if len(value) > hi:
        problems.append(f"{where}: {what} too long ({len(value)} > {hi})")
    if BANNED.search(value):
        problems.append(f"{where}: {what} has a placeholder or an exclamation mark: {value[:80]!r}")


def check_guide(g, guides):
    problems = []
    gid = g.get("id", "")
    name = g.get("_file", gid)
    if g.get("format") != FORMAT:
        problems.append(f"{name}: format is not {FORMAT}")
    if not g.get("partOf"):
        if not ID.match(gid or ""):
            problems.append(f"{name}: id {gid!r} is not lower case letters, digits, dots and dashes")
        if not g.get("variants") and not g.get("common"):
            problems.append(f"{name}: names no variants (and is not common)")
        if g.get("common") and not g.get("games"):
            problems.append(f"{name}: a common guide must name its games")
        if g.get("inherits") and g["inherits"] not in guides:
            problems.append(f"{name}: inherits {g['inherits']!r}, which does not exist")
        if not g.get("sources"):
            problems.append(f"{name}: no sources")
        if g.get("intro"):
            length(problems, name, "intro", g.get("intro"), "intro")
    elif g["partOf"] not in guides:
        problems.append(f"{name}: partOf {g['partOf']!r} does not exist")
    src_ids = {s.get("id") for s in g.get("sources", [])}
    for s in g.get("sources", []):
        if not s.get("title"):
            problems.append(f"{name}: a source without a title")
    area_ids, panel_ids = set(), set()
    for a in g.get("areas", []):
        aw = f"{name} area {a.get('id')}"
        if not ID.match(a.get("id") or ""):
            problems.append(f"{aw}: bad id")
        if a.get("id") in area_ids:
            problems.append(f"{aw}: area id twice")
        area_ids.add(a.get("id"))
        if a.get("seat") not in (None, 1, 2, 3, 4):
            problems.append(f"{aw}: seat must be 1, 2, 3 or 4")
        if a.get("where") and a["where"] not in WHERE:
            problems.append(f"{aw}: where {a['where']!r} is not one of {WHERE}")
        length(problems, aw, "name", a.get("name"), "name")
        if not g.get("inherits") or a.get("text"):
            length(problems, aw, "text", a.get("text"), "area-text")
        for p in a.get("panels", []):
            pw = f"{name} panel {p.get('id')}"
            if not ID.match(p.get("id") or ""):
                problems.append(f"{pw}: bad id")
            if p.get("id") in panel_ids:
                problems.append(f"{pw}: panel id twice")
            panel_ids.add(p.get("id"))
            length(problems, pw, "name", p.get("name"), "name")
            length(problems, pw, "text", p.get("text"), "text")
            length(problems, pw, "context", p.get("context"), "context")
            if p.get("src") and p["src"] not in src_ids and not g.get("partOf"):
                problems.append(f"{pw}: src {p['src']!r} is not a source id")
            cids = set()
            if not p.get("controls"):
                problems.append(f"{pw}: no controls")
            for c in p.get("controls", []):
                cw = f"{pw}/{c.get('id')}"
                if not ID.match(c.get("id") or ""):
                    problems.append(f"{cw}: bad id")
                if c.get("id") in cids:
                    problems.append(f"{cw}: control id twice in the panel")
                cids.add(c.get("id"))
                length(problems, cw, "name", c.get("name"), "name")
                length(problems, cw, "text", c.get("text"), "text")
                length(problems, cw, "context", c.get("context"), "context")
                if c.get("type") not in TYPES:
                    problems.append(f"{cw}: type {c.get('type')!r} is not one of {TYPES}")
                m = c.get("match", [])
                if not isinstance(m, list) or any(not isinstance(k, str) or not k.strip() for k in m):
                    problems.append(f"{cw}: match must be a list of keys")
                pos = c.get("positions", [])
                names = [x.get("name", "") for x in pos]
                if len(set(n.lower() for n in names)) != len(names):
                    problems.append(f"{cw}: a position is named twice")
                for x in pos:
                    length(problems, cw, "position name", x.get("name"), "position")
                    length(problems, cw + " " + x.get("name", ""), "position text", x.get("text"), "position-text")
                if c.get("type") == "switch2" and len(pos) not in (0, 2):
                    problems.append(f"{cw}: a switch2 has 2 positions, not {len(pos)}")
                if c.get("type") == "switch3" and len(pos) not in (0, 3):
                    problems.append(f"{cw}: a switch3 has 3 positions, not {len(pos)}")
                if c.get("type") in ("switch2", "switch3") and not pos:
                    problems.append(f"{cw}: a {c['type']} needs its positions")
                if len(pos) > 16:
                    problems.append(f"{cw}: more than 16 positions")
                if c.get("src") and c["src"] not in src_ids and not g.get("partOf"):
                    problems.append(f"{cw}: src {c['src']!r} is not a source id")
                if c.get("text") and c.get("context") and c["text"].strip() == c["context"].strip():
                    problems.append(f"{cw}: text and context are the same")
    return problems


def check_all(files=None):
    guides = all_guides()
    problems = []
    targets = [g for g in guides.values()] if not files else [dict(load(f), _file=os.path.basename(f)) for f in files]
    for g in targets:
        problems += check_guide(g, guides)
    # Duplicate texts within a resolved guide (inheriting guides share their parent's on purpose).
    for gid in guides:
        if gid.startswith("?"):
            problems.append(f"{guides[gid]['_file']}: partOf names a guide that does not exist")
            continue
        r = resolve(guides, gid)
        # Within one seat (the same control of the pilot's and the WSO's cockpit may share its words).
        seen = {}
        for a, p, c in controls_of(r):
            seat = a.get("seat") or 0
            for key in ("text", "context"):
                t = re.sub(r"\s+", " ", (c.get(key) or "").strip().lower())
                if len(t) < 60:
                    continue
                for (k, s2), first in seen.items():
                    if k == (key, t) and (s2 == seat or s2 == 0 or seat == 0) and first != (p.get("id"), c.get("id")):
                        problems.append(f"{gid}: {p.get('id')}/{c.get('id')} has the same {key} as {first[0]}/{first[1]}")
                        break
                seen.setdefault(((key, t), seat), (p.get("id"), c.get("id")))
        if r.get("variants"):
            for v in r["variants"]:
                if not ID.match(v):
                    problems.append(f"{gid}: variant id {v!r}")
    for f in text_files():
        t = load(f)
        base = os.path.basename(f)
        if t.get("format") != TEXT_FORMAT:
            problems.append(f"{base}: format is not {TEXT_FORMAT}")
            continue
        g = guides.get(t.get("guide", ""))
        if g is None:
            problems.append(f"{base}: guide {t.get('guide')!r} does not exist")
            continue
        r = resolve(guides, t["guide"])
        panels = {p.get("id"): p for a in r.get("areas", []) for p in a.get("panels", [])}
        areas = {a.get("id") for a in r.get("areas", [])}
        for aid in t.get("areas", {}):
            if aid not in areas:
                problems.append(f"{base}: area {aid!r} is not in the guide")
        for pid in t.get("panels", {}):
            if pid not in panels:
                problems.append(f"{base}: panel {pid!r} is not in the guide")
        for key, entry in t.get("controls", {}).items():
            pid, _, cid = key.partition("/")
            p = panels.get(pid)
            c = next((x for x in (p or {}).get("controls", []) if x.get("id") == cid), None)
            if c is None:
                problems.append(f"{base}: control {key!r} is not in the guide")
                continue
            names = {x.get("name") for x in c.get("positions", [])}
            for pos in entry.get("positions", {}):
                if pos not in names:
                    problems.append(f"{base}: {key}: position {pos!r} is not a position of the control")
            for k in ("text", "context"):
                if k in entry and not (entry[k] or "").strip():
                    problems.append(f"{base}: {key}: empty {k}")
    return problems


# ---------------------------------------------------------------------------------------------------------- matching

def glob_re(pattern):
    return re.compile("^" + re.escape(pattern).replace(r"\*", ".*").replace(r"\?", ".") + "$", re.I)


def norm(s):
    return re.sub(r"[^a-z0-9]", "", (s or "").lower())


class Matcher:
    def __init__(self, guides_in_order):
        self.entries = []  # (guide id, panel id, control id, kind, value, area seat)
        for g in guides_in_order:
            for a, p, c in controls_of(g):
                seat = a.get("seat")
                for k in c.get("match", []):
                    if k.startswith("cmd:"):
                        self.entries.append((g["id"], p["id"], c["id"], "cmd", glob_re(k[4:]), seat))
                    elif k.startswith("label:"):
                        self.entries.append((g["id"], p["id"], c["id"], "label", norm(k[6:]), seat))
                    elif k.startswith("item:") or k.startswith("control:"):
                        continue
                    else:
                        self.entries.append((g["id"], p["id"], c["id"], "id", glob_re(k), seat))

    def match(self, callout, seat=1):
        cid = callout.get("id", "")
        label = norm(callout.get("label", ""))
        cmds = [x.get("id", "") for x in callout.get("commands") or []] + [x.get("name", "") for x in callout.get("commands") or []]
        for gid, pid, ctl, kind, value, area_seat in self.entries:
            if area_seat and area_seat != seat:
                continue
            if kind == "id" and value.match(cid):
                return gid, pid, ctl
            if kind == "label" and value and value == label:
                return gid, pid, ctl
            if kind == "cmd" and any(value.match(x) for x in cmds if x):
                return gid, pid, ctl
        return None


def picture_callouts(path, seat=None):
    if not path:
        return {}, []
    if os.path.isdir(path):
        path = os.path.join(path, "pictures.json")
    d = load(path)
    shot = d.get("source") == "screenshot"
    out = []
    for v in d.get("views", []):
        if v.get("kind") == "overview" and not shot:
            continue
        if seat and v.get("seat", 1) != seat:
            continue
        for c in v.get("callouts", []):
            out.append((v, c))
    return d, out


def coverage(gid, pictures, seat=None, show_all=False, guides=None, quiet=False):
    guides = guides or all_guides()
    r = resolve(guides, gid)
    if r is None:
        sys.exit(f"no guide {gid!r}")
    commons = [resolve(guides, k) for k, g in guides.items() if g.get("common") and k != gid]
    d, callouts = picture_callouts(pictures, seat)
    game = d.get("game", "")
    order = [r] + [c for c in commons if game in c.get("games", [])]
    m = Matcher(order)
    hit, miss = 0, []
    used = set()
    for v, c in callouts:
        x = m.match(c, v.get("seat", 1))
        if x:
            hit += 1
            used.add(x)
        else:
            miss.append((v, c))
    unused = [(p["id"], c["id"]) for a, p, c in controls_of(r)
              if (r["id"], p["id"], c["id"]) not in used and not any(k.startswith(("item:", "control:")) for k in c.get("match", []))]
    if not quiet:
        if pictures:
            print(f"{gid} on {d.get('game')}/{d.get('plane')}: {hit} of {len(callouts)} game controls explained, {len(miss)} not")
        else:
            print(f"{gid}: catalog keys only (no picture set given)")
        for v, c in miss if show_all else miss[:400]:
            print(f"  MISSING {c.get('id')} | {c.get('label')} | {v.get('id')}")
        if unused and pictures:
            print(f"  guide controls that match no game control here ({len(unused)}): " + ", ".join(p + "/" + c for p, c in unused[:80]))
        for v, keys in catalog_keys(r.get("variants", [])).items():
            seat_areas = [a for a in r.get("areas", []) if not a.get("seat") or a.get("seat") == keys["seat"]]
            used_keys = {k for a in seat_areas for p in a.get("panels", []) for c in p.get("controls", []) for k in c.get("match", [])}
            missing = [k for k in keys["controls"] if k not in used_keys]
            if missing:
                print(f"  catalog grip controls of {v} (seat {keys['seat']}) no guide control names: " + ", ".join(missing))
            missing = [k for k in keys["items"] if k not in used_keys]
            if missing:
                print(f"  catalog panel items of {v} (seat {keys['seat']}) no guide control names ({len(missing)}): " + ", ".join(missing[:60]))
    return hit, len(callouts)


def catalog_keys(variant_ids):
    """variant id -> its control: and item: keys and its seat (2 for a seat of another variant), from the catalog."""
    folder = os.path.dirname(GUIDE)
    variants = {}
    for f in os.listdir(folder):
        if f.endswith(".json"):
            for a in load(os.path.join(folder, f)).get("aircraft", []):
                for v in a.get("variants", []):
                    variants[v["id"]] = v

    def resolved(v, depth=0):
        if v.get("inherits") in variants and depth < 8:
            p = resolved(variants[v["inherits"]], depth + 1)
            r = dict(p)
            for k, x in v.items():
                if k == "controls":
                    ids = {c["id"] for c in x}
                    r["controls"] = [c for c in p.get("controls", []) if c["id"] not in ids] + x
                elif x not in (None, "", [], {}):
                    r[k] = x
            return r
        return v
    out = {}
    for vid in variant_ids:
        if vid not in variants:
            continue
        v = resolved(variants[vid])
        seat = 2 if v.get("seatOf") or re.search("cpg|rio|wso|operator|gunner|copilot|co-pilot|rear|instructor", v.get("seat", ""), re.I) else 1
        out[vid] = {"seat": seat,
                    "controls": ["control:" + c["id"] for c in v.get("controls", []) if c.get("functions")],
                    "items": ["item:" + (p.get("id") or "") + ":" + str(i.get("n")) for p in v.get("panels", []) or [] for i in p.get("items", []) or []]}
    return out


def stats(lang=None):
    guides = all_guides()
    total = [0, 0, 0, 0]
    for gid, g in sorted(guides.items()):
        areas = len(g.get("areas", []))
        panels = sum(len(a.get("panels", [])) for a in g.get("areas", []))
        ctl = list(controls_of(g))
        chars = sum(len(c.get("text", "")) + len(c.get("context", "")) + sum(len(p.get("text", "")) for p in c.get("positions", [])) for a, p, c in ctl)
        chars += sum(len(p.get("text", "")) + len(p.get("context", "")) for a in g.get("areas", []) for p in a.get("panels", []))
        print(f"{gid:40s} areas {areas:3d} panels {panels:4d} controls {len(ctl):5d} chars {chars:8d}  {','.join(g.get('variants', [])) or ('common ' + ','.join(g.get('games', [])))}")
        total = [total[0] + areas, total[1] + panels, total[2] + len(ctl), total[3] + chars]
    print(f"{'ALL':40s} areas {total[0]:3d} panels {total[1]:4d} controls {total[2]:5d} chars {total[3]:8d}")
    langs = {}
    for f in text_files():
        t = load(f)
        g = guides.get(t.get("guide"))
        missing = 0
        if g is not None:
            missing += sum(1 for a in g.get("areas", []) if a.get("id") not in t.get("areas", {}))
            missing += sum(1 for a in g.get("areas", []) for p in a.get("panels", []) if p.get("id") not in t.get("panels", {}))
            for a, p, c in controls_of(g):
                e = t.get("controls", {}).get(p.get("id") + "/" + c.get("id"))
                if e is None or not e.get("text"):
                    missing += 1
        langs.setdefault(t.get("language", "?"), []).append(f"{t.get('guide')}" + (f" ({missing} entries missing)" if missing else ""))
    for k, v in sorted(langs.items()):
        print(f"translations {k}: {len(v)} guides: {', '.join(sorted(v))}")


def report(root, game=None):
    guides = all_guides()
    by_set = {}
    for g in guides.values():
        for s in g.get("sets", []):
            by_set[s.replace("\\", "/").lower()] = g["id"]
    rows = []
    for entry in sorted(os.listdir(root)):
        folder = os.path.join(root, entry)
        cands = [(entry.split("-", 1)[0], entry.split("-", 1)[-1], folder)] if os.path.exists(os.path.join(folder, "pictures.json")) else \
            [(entry, s, os.path.join(folder, s)) for s in sorted(os.listdir(folder)) if os.path.exists(os.path.join(folder, s, "pictures.json"))] if os.path.isdir(folder) else []
        for g, s, f in cands:
            if game and g != game:
                continue
            gid = by_set.get((g + "/" + s).lower())
            if gid is None:
                commons = [k for k, x in guides.items() if x.get("common") and g in x.get("games", [])]
                if not commons:
                    rows.append((g, s, None, 0, 0))
                    continue
                gid = commons[0]
            hit, n = coverage(gid, f, guides=guides, quiet=True)
            rows.append((g, s, gid, hit, n))
    for g, s, gid, hit, n in rows:
        print(f"{g:14s} {s:40s} {gid or '-':34s} {hit:5d}/{n:5d}")
    hits = sum(r[3] for r in rows)
    ns = sum(r[4] for r in rows)
    print(f"ALL {hits}/{ns} game controls explained in {sum(1 for r in rows if r[2])} of {len(rows)} sets")


def main(argv):
    if not argv or argv[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    cmd, rest = argv[0], argv[1:]
    if cmd == "check":
        problems = check_all(rest or None)
        for p in problems:
            print(p)
        print(f"{len(problems)} problem(s)")
        return 1 if problems else 0
    if cmd == "coverage":
        gid = rest[0]
        pics = rest[rest.index("--pictures") + 1] if "--pictures" in rest else None
        seat = int(rest[rest.index("--seat") + 1]) if "--seat" in rest else None
        coverage(gid, pics, seat, "--all" in rest)
        return 0
    if cmd == "stats":
        stats(rest[rest.index("--lang") + 1] if "--lang" in rest else None)
        return 0
    if cmd == "report":
        report(rest[rest.index("--pictures") + 1], rest[rest.index("--game") + 1] if "--game" in rest else None)
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
