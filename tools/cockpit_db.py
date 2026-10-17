"""JoyPro's cockpit database tool (docs/cockpits.md, "Growing the database").

Research batches write drafts in the cockpit format (one JSON file per batch, outside the repository or under
DB/Cockpits). This tool checks them with the real engine, merges them into EngineOnly_Runtime/DB/Cockpits, renders the
sheets for a look and prints what researchers need (function ids, the DCS seed rows, the ids already taken).

    py -3 tools/cockpit_db.py functions [query]            JoyPro function ids (DB/Functions/slots.json)
    py -3 tools/cockpit_db.py seed <plane> [--game DCS]    the HOTAS rows JoyPro's function tables have for an aircraft
    py -3 tools/cockpit_db.py ids [query]                  aircraft and variant ids in the database
    py -3 tools/cockpit_db.py check [draft.json ...]       the database (plus the drafts) through "engine cockpits check"
    py -3 tools/cockpit_db.py check --patch <patch.json> ... [--all] [--structure]   the database with the patches applied
    py -3 tools/cockpit_db.py patch <patch.json> ...        applies patches (format joypro-cockpit-patch) to the database
    py -3 tools/cockpit_db.py pages <aircraft> [variant] [--page all|overview|stick|throttle|panels|panel:<id>] [--out dir]
                                                            [--dark] [--patch p.json]   the Cockpits page's manual pages (PNG)
    py -3 tools/cockpit_db.py merge <draft.json> --into <file.json> [--replace] [--only id,id] [--skip id,id]
    py -3 tools/cockpit_db.py render <aircraft> [variant] [--out <folder>] [--light] [--drawing <id>]
    py -3 tools/cockpit_db.py photos-merge <photos.json> ...   adds photo links {"photos": {"<aircraft>": [...]}}
    py -3 tools/cockpit_db.py get <aircraft> [variant]      a variant with its controls (engine cockpits get --text)
    py -3 tools/cockpit_db.py coverage                     the coverage numbers (engine cockpits coverage)
    py -3 tools/cockpit_db.py drawings                     the drawings and their parts

The engine is engine/bin/x64/Debug/engine.exe of this repository (or JOYPRO_ENGINE). It reads the database from the
repository (JOYPRO_COCKPITS_DB), so no rebuild is needed after a merge. Nothing here downloads anything.
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DB = os.path.join(REPO, "EngineOnly_Runtime", "DB")
# JOYPRO_COCKPIT_SOURCE: another copy of DB/Cockpits to read (a snapshot for research helpers), never written by check.
COCKPITS = os.environ.get("JOYPRO_COCKPIT_SOURCE") or os.path.join(DB, "Cockpits")
FUNCTIONS = os.path.join(DB, "Functions")


def engine_path():
    env = os.environ.get("JOYPRO_ENGINE")
    if env:
        return env
    for config in ("Debug", "Release"):
        path = os.path.join(REPO, "engine", "bin", "x64", config, "engine.exe")
        if os.path.exists(path):
            return path
    sys.exit("engine.exe not found: build the engine first (dotnet build gui/JoyPro.Gui/JoyPro.Gui.csproj) or set JOYPRO_ENGINE")


def run_engine(args, cockpits=COCKPITS):
    env = dict(os.environ)
    env["JOYPRO_COCKPITS_DB"] = cockpits
    env["JOYPRO_FUNCTIONS_DB"] = FUNCTIONS
    env.setdefault("JOYPRO_DATA_DIR", os.path.join(tempfile.gettempdir(), "joypro-cockpit-db"))
    result = subprocess.run([engine_path(), "--status", "false", "--benchmark", "false"] + args,
                            capture_output=True, text=True, encoding="utf-8", errors="replace", env=env)
    return result.returncode, result.stdout + result.stderr


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def save(path, data):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
        f.write("\n")


def cmd_functions(args):
    query = " ".join(args).lower()
    slots = load(os.path.join(FUNCTIONS, "slots.json"))
    for slot in slots:
        line = "%s\t%s\t%s%s" % (slot["id"], slot.get("name", ""), slot.get("hotas", ""), "\taxis" if slot.get("axis") else "")
        if not query or all(t in line.lower() for t in query.split()):
            print(line)


def cmd_seed(args):
    game = "DCS"
    if "--game" in args:
        i = args.index("--game")
        game = args[i + 1]
        del args[i:i + 2]
    plane = " ".join(args) if args else game
    path = os.path.join(FUNCTIONS, game + ".tsv")
    if not os.path.exists(path):
        sys.exit("No function table for " + game)
    hotas = {"stick", "throttle", "cyclic", "collective", "throttle_quadrant", "pedals"}
    rows = 0
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.startswith("#"):
                continue
            cols = line.rstrip("\n").split("\t")
            if len(cols) < 7 or cols[0].lower() != plane.lower():
                continue
            if cols[4] in hotas:
                print("%s\t%s\t%s\t%s" % (cols[1], cols[3], cols[4], cols[6]))
                rows += 1
    if rows == 0:
        planes = set()
        with open(path, encoding="utf-8") as f:
            for line in f:
                if not line.startswith("#"):
                    planes.add(line.split("\t")[0])
        print("No HOTAS rows for '%s'. Aircraft in %s: %s" % (plane, game, ", ".join(sorted(planes))))


def cockpit_files():
    return sorted(os.path.join(COCKPITS, n) for n in os.listdir(COCKPITS) if n.endswith(".json"))


def cmd_ids(args):
    query = " ".join(args).lower()
    for path in cockpit_files():
        for a in load(path).get("aircraft", []):
            text = a["id"] + " " + a.get("name", "") + " " + " ".join(v["id"] for v in a.get("variants", []))
            if query and query not in text.lower():
                continue
            print("%s\t%s\t%s" % (os.path.basename(path), a["id"], a.get("name", "")))
            for v in a.get("variants", []):
                print("\t\t%s\t%s" % (v["id"], v.get("name", "")))


def cmd_check(args):
    work = tempfile.mkdtemp(prefix="joypro-cockpits-")
    try:
        shutil.copytree(os.path.join(COCKPITS, "drawings"), os.path.join(work, "drawings"))
        if os.path.isdir(os.path.join(COCKPITS, "line-drawings")):
            shutil.copytree(os.path.join(COCKPITS, "line-drawings"), os.path.join(work, "line-drawings"))
        drafts = [os.path.abspath(a) for a in args]
        names = set()
        files = {}
        owner = {}
        for path in cockpit_files():
            if os.path.abspath(path) not in drafts:
                files[os.path.basename(path)] = load(path)
                for a in files[os.path.basename(path)].get("aircraft", []):
                    owner[a["id"]] = a
        for draft in drafts:
            name = "draft-" + os.path.basename(draft)
            names.add(name)
            data = load(draft)
            own = []
            for a in data.get("aircraft", []):
                if a["id"] in owner:
                    # New variants of an aircraft already in the database: checked as merge would add them.
                    owner[a["id"]].setdefault("variants", []).extend(a.get("variants", []))
                    owner[a["id"]].setdefault("photos", []).extend(a.get("photos", []))
                else:
                    own.append(a)
            data["aircraft"] = own
            files[name] = data
        for name, data in files.items():
            save(os.path.join(work, name), data)
        code, out = run_engine(["cockpits", "check", "--text"], cockpits=work)
        lines = out.splitlines()
        if drafts:
            ids = set()
            for draft in drafts:
                for a in load(draft).get("aircraft", []):
                    ids.add(a["id"])
            shown = [l for l in lines if l.startswith("Cockpit Check") or l.startswith("Problems")
                     or any(("(" + n + ")") in l for n in names) or any(l.strip().startswith("- " + i + "/") or l.strip().startswith("- " + i + " ") for i in ids)]
            print("\n".join(shown))
        else:
            print(out)
        return code
    finally:
        shutil.rmtree(work, ignore_errors=True)


def cmd_merge(args):
    if "--into" not in args:
        sys.exit("merge <draft.json> --into <file.json> [--replace]")
    only = skip = None
    for flag in ("--only", "--skip"):
        if flag in args:
            k = args.index(flag)
            ids = set(args[k + 1].split(","))
            if flag == "--only":
                only = ids
            else:
                skip = ids
            del args[k:k + 2]
    i = args.index("--into")
    target = args[i + 1]
    replace = "--replace" in args
    drafts = [a for j, a in enumerate(args) if j not in (i, i + 1) and a != "--replace"]
    target_path = target if os.path.isabs(target) else os.path.join(COCKPITS, target)
    data = load(target_path) if os.path.exists(target_path) else {"format": "joypro-cockpits", "version": 1, "aircraft": []}
    taken = {}
    for path in cockpit_files():
        if os.path.abspath(path) == os.path.abspath(target_path):
            continue
        for a in load(path).get("aircraft", []):
            taken[a["id"]] = os.path.basename(path)
    for draft in drafts:
        for a in load(draft).get("aircraft", []):
            if (only is not None and a["id"] not in only) or (skip is not None and a["id"] in skip):
                continue
            if a["id"] in taken:
                sys.exit("%s is already in %s" % (a["id"], taken[a["id"]]))
            existing = [k for k, x in enumerate(data["aircraft"]) if x["id"] == a["id"]]
            if existing and not replace:
                # Variants of an aircraft already in the file are added to it.
                old = data["aircraft"][existing[0]]
                have = {v["id"] for v in old.get("variants", [])}
                for v in a.get("variants", []):
                    if v["id"] in have:
                        sys.exit("variant %s of %s is already in %s (use --replace)" % (v["id"], a["id"], target))
                    old.setdefault("variants", []).append(v)
                for key in ("photos", "links"):
                    old.setdefault(key, []).extend(a.get(key, []))
                print("added %d variant(s) to %s" % (len(a.get("variants", [])), a["id"]))
            elif existing:
                data["aircraft"][existing[0]] = a
                print("replaced %s" % a["id"])
            else:
                data["aircraft"].append(a)
                print("added %s" % a["id"])
    save(target_path, data)
    print("wrote " + target_path)


def cmd_photos_merge(args):
    """Adds photo links from drafts {"format": "joypro-cockpit-photos", "photos": {"<aircraft id>": [ {...} ]}} to the
    aircraft in the database (an address already listed for the aircraft is skipped)."""
    keys = ("url", "page", "view", "credit", "license", "variant", "caption", "confidence")
    files = {p: load(p) for p in cockpit_files()}
    added = skipped = 0
    for draft in args:
        for aircraft, photos in load(draft).get("photos", {}).items():
            target = None
            for path, data in files.items():
                for a in data.get("aircraft", []):
                    if a["id"] == aircraft:
                        target = a
            if target is None:
                print("unknown aircraft " + aircraft)
                continue
            have = {(p.get("url", ""), p.get("page", "")) for p in target.get("photos", [])}
            variants = {v["id"] for v in target.get("variants", [])}
            for p in photos:
                p = {k: p[k] for k in keys if p.get(k)}
                if p.get("variant") and p["variant"] not in variants:
                    p.pop("variant")
                if (p.get("url", ""), p.get("page", "")) in have:
                    skipped += 1
                    continue
                target.setdefault("photos", []).append(p)
                have.add((p.get("url", ""), p.get("page", "")))
                added += 1
    for path, data in files.items():
        save(path, data)
    print("photos added %d, skipped %d" % (added, skipped))


def cmd_render(args):
    out = os.path.join(tempfile.gettempdir(), "joypro-cockpit-renders")
    style = "dark"
    drawing = None
    if "--out" in args:
        i = args.index("--out")
        out = args[i + 1]
        del args[i:i + 2]
    if "--light" in args:
        args.remove("--light")
        style = "light"
    if "--drawing" in args:
        i = args.index("--drawing")
        drawing = args[i + 1]
        del args[i:i + 2]
    os.makedirs(out, exist_ok=True)
    name = drawing if drawing else "-".join(args)
    png = os.path.join(os.path.abspath(out), name + "-" + style + ".png")
    extra = ["--drawing", drawing] if drawing else args
    code, text = run_engine(["cockpits", "render"] + extra + ["--png", png, "--style", style, "--print-width", "0", "--text"])
    print(text)
    return code


PATCH_FORMAT = "joypro-cockpit-patch"


def apply_patches(files, patches):
    """Applies patches to the loaded database files ({name: data}); returns the variant and aircraft ids touched.

    A patch: {"format": "joypro-cockpit-patch",
              "variants": {"<variant id>": {"modes": [...], "overview": {...}, "controls": [...], ...}},   fields set
              "aircraft": {"<aircraft id>": {"summary": "...", ...}},                                     fields set
              "newVariants": {"<aircraft id>": [ {variant}, ... ]},                                       appended
              "newAircraft": {"<file.json>": [ {aircraft}, ... ]}}                                         appended
    A field set to null is removed."""
    touched = set()
    variants = {}
    aircraft = {}
    for data in files.values():
        for a in data.get("aircraft", []):
            aircraft[a["id"]] = (a, data)
            for v in a.get("variants", []):
                variants[v["id"]] = v
    for patch in patches:
        if patch.get("format") != PATCH_FORMAT:
            sys.exit("not a cockpit patch (format %s)" % PATCH_FORMAT)
        for name, items in (patch.get("newAircraft") or {}).items():
            target = files.setdefault(name, {"format": "joypro-cockpits", "version": 2, "aircraft": []})
            for a in items:
                if a["id"] in aircraft:
                    sys.exit("aircraft %s is already in the database" % a["id"])
                target["aircraft"].append(a)
                aircraft[a["id"]] = (a, target)
                for v in a.get("variants", []):
                    variants[v["id"]] = v
                    touched.add(v["id"])
                touched.add(a["id"])
        for aid, items in (patch.get("newVariants") or {}).items():
            if aid not in aircraft:
                sys.exit("unknown aircraft " + aid)
            have = {v["id"] for v in aircraft[aid][0].get("variants", [])}
            for v in items:
                if v["id"] in variants or v["id"] in have:
                    sys.exit("variant %s is already in the database" % v["id"])
                aircraft[aid][0].setdefault("variants", []).append(v)
                variants[v["id"]] = v
                touched.add(v["id"])
        for vid, fields in (patch.get("variants") or {}).items():
            if vid not in variants:
                sys.exit("unknown variant " + vid)
            for key, value in fields.items():
                if value is None:
                    variants[vid].pop(key, None)
                else:
                    variants[vid][key] = value
            touched.add(vid)
        for aid, fields in (patch.get("aircraft") or {}).items():
            if aid not in aircraft:
                sys.exit("unknown aircraft " + aid)
            for key, value in fields.items():
                if key == "variants":
                    continue
                if value is None:
                    aircraft[aid][0].pop(key, None)
                else:
                    aircraft[aid][0][key] = value
            touched.add(aid)
    for data in files.values():
        if any(k in v for a in data.get("aircraft", []) for v in a.get("variants", []) for k in ("modes", "overview")) or \
                any("items" in p for a in data.get("aircraft", []) for v in a.get("variants", []) for p in v.get("panels", [])):
            data["version"] = 2
    return touched


def cmd_patch(args):
    """patch <patch.json> ... : applies the patches to the database files (check them first: check --patch)."""
    files = {os.path.basename(p): load(p) for p in cockpit_files()}
    touched = apply_patches(files, [load(p) for p in args])
    for name, data in files.items():
        save(os.path.join(COCKPITS, name), data)
    print("patched %d variant(s) / aircraft: %s" % (len(touched), ", ".join(sorted(touched))))


def cmd_check_patch(args):
    """check --patch <patch.json> ... [--all] [--structure]: the database with the patches applied, through the engine;
    prints the problems of the patched variants (all with --all)."""
    show_all = "--all" in args
    structure = "--structure" in args
    paths = [a for a in args if a not in ("--patch", "--all", "--structure")]
    work = tempfile.mkdtemp(prefix="joypro-cockpits-")
    try:
        shutil.copytree(os.path.join(COCKPITS, "drawings"), os.path.join(work, "drawings"))
        if os.path.isdir(os.path.join(COCKPITS, "line-drawings")):
            shutil.copytree(os.path.join(COCKPITS, "line-drawings"), os.path.join(work, "line-drawings"))
        files = {os.path.basename(p): load(p) for p in cockpit_files()}
        touched = apply_patches(files, [load(p) for p in paths])
        for name, data in files.items():
            save(os.path.join(work, name), data)
        code, out = run_engine(["cockpits", "check", "--text"] + (["--structure"] if structure else []), cockpits=work)
        lines = out.splitlines()
        shown = [l for l in lines if show_all or l.startswith("Cockpit Check") or
                 any(("/" + t + ":") in l or l.strip().startswith("- " + t + "/") or l.strip().startswith("- " + t + " ") for t in touched)]
        print("\n".join(shown))
        print("Problems of the patched variants: %d" % sum(1 for l in shown if l.strip().startswith("- ")))
        return 0 if not any(l.strip().startswith("- ") for l in shown) else 1
    finally:
        shutil.rmtree(work, ignore_errors=True)


def cmd_pages(args):
    """pages <aircraft> [variant] [--page all|overview|stick|throttle|panels|panel:<id>] [--out folder] [--dark] [--patch p.json]
    renders the Cockpits page's manual pages as PNG (the patches applied to a temporary copy first)."""
    out = os.path.join(tempfile.gettempdir(), "joypro-cockpit-pages")
    page = "all"
    style = "light"
    patches = []
    rest = []
    i = 0
    while i < len(args):
        a = args[i]
        if a == "--out":
            out = args[i + 1]
            i += 2
        elif a == "--page":
            page = args[i + 1]
            i += 2
        elif a == "--patch":
            patches.append(args[i + 1])
            i += 2
        elif a == "--dark":
            style = "dark"
            i += 1
        else:
            rest.append(a)
            i += 1
    os.makedirs(out, exist_ok=True)
    cockpits = COCKPITS
    work = None
    if patches:
        work = tempfile.mkdtemp(prefix="joypro-cockpits-")
        shutil.copytree(os.path.join(COCKPITS, "drawings"), os.path.join(work, "drawings"))
        if os.path.isdir(os.path.join(COCKPITS, "line-drawings")):
            shutil.copytree(os.path.join(COCKPITS, "line-drawings"), os.path.join(work, "line-drawings"))
        files = {os.path.basename(p): load(p) for p in cockpit_files()}
        apply_patches(files, [load(p) for p in patches])
        for name, data in files.items():
            save(os.path.join(work, name), data)
        cockpits = work
    try:
        png = os.path.join(os.path.abspath(out), "-".join(rest) + ".png")
        code, text = run_engine(["cockpits", "render"] + rest + ["--page", page, "--png", png, "--style", style, "--text"], cockpits=cockpits)
        print(text)
        return code
    finally:
        if work:
            shutil.rmtree(work, ignore_errors=True)


def cmd_simple(action):
    code, text = run_engine(["cockpits", action, "--text"])
    print(text)
    return code


def main():
    if len(sys.argv) < 2 or sys.argv[1] in ("-h", "--help", "help"):
        print(__doc__)
        return 0
    cmd, args = sys.argv[1], sys.argv[2:]
    if cmd == "functions":
        return cmd_functions(args)
    if cmd == "seed":
        return cmd_seed(args)
    if cmd == "ids":
        return cmd_ids(args)
    if cmd == "check" and "--patch" in args:
        return cmd_check_patch(args)
    if cmd == "check":
        return cmd_check(args)
    if cmd == "patch":
        return cmd_patch(args)
    if cmd == "pages":
        return cmd_pages(args)
    if cmd == "merge":
        return cmd_merge(args)
    if cmd == "render":
        return cmd_render(args)
    if cmd in ("coverage", "drawings"):
        return cmd_simple(cmd)
    if cmd == "photos-merge":
        return cmd_photos_merge(args)
    if cmd == "get":
        code, text = run_engine(["cockpits", "get"] + args + ["--text"])
        print(text)
        return code
    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.exit(main() or 0)
