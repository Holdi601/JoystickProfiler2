"""Curating marker positions on the makers' reference photos (docs/device-reference.md, "Curating positions").

JoyPro ships the positions of every control on each maker's photo it references (EngineOnly_Runtime/DB/DeviceLayouts,
keyed by the photo's SHA-256). The photos themselves are never shipped; this tool reads them from the maintainer's
private backup (tools/backup_device_photos.ps1) and writes only positions into the repository. Pictures it renders
(grids, overlays) go to an output folder outside the repository.

    python tools/device_positions.py info   <model>                  parts (with their inputs) and photos of a model
    python tools/device_positions.py grid   <model> <view> [--crop x0 y0 x1 y1]   the photo with a 0..1 coordinate grid
    python tools/device_positions.py draft  <model> <view>           a draft file to fill in (or the existing set as a draft)
    python tools/device_positions.py build  <draft.txt> [...]        draft(s) -> DB/DeviceLayouts/<model>.<view>.json (+ photo hash)
    python tools/device_positions.py render <model> <view> | <set.json>    the photo with the markers, to check them
    python tools/device_positions.py check                           every curated set: photo hash, parts, inputs
    python tools/device_positions.py status [query]                  models with photos and how far they are curated

Options: --photos <backup folder> (default: the JoyPro2-private/device-photos folder next to the repository, or
JOYPRO_PHOTO_BACKUP), --out <folder> (default: JOYPRO_CURATE_OUT, else %TEMP%/joypro-curation), --engine <engine.exe>.

Draft format (one line per part of the model; '#' header lines):
    # model: thrustmaster-t16000m
    # view: main
    # retag: front              (optional: a better view name for a gallery-<n> photo; written to DB/DevicePhotos)
    # source: how the positions were made
    # mapping: where the input mapping (button numbers, hat directions) comes from
    # mapping-confidence: high  (default for every part)
    # confidence: medium        (the set as a whole)
    # note: free text
    stick.hat.tms 0.620 0.190 high                     placed: x y (0..1 of the photo), position confidence
    stick.trigger 0.300 0.410 medium map=low | note    map= overrides the mapping confidence for this part
    stick.button.thumb.3 - | hidden behind the head    not placed, with the reason
"""
import argparse
import hashlib
import json
import os
import subprocess
import sys
import tempfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DB = os.path.join(REPO, "EngineOnly_Runtime", "DB")
LAYOUTS = os.path.join(DB, "DeviceLayouts")
CONFIDENCES = ("high", "medium", "low")

_engine_cache = {}


def fail(message):
    print("error: " + message, file=sys.stderr)
    sys.exit(2)


def find_photos(arg):
    candidates = [arg, os.environ.get("JOYPRO_PHOTO_BACKUP")]
    folder = REPO
    for _ in range(8):
        folder = os.path.dirname(folder)
        candidates.append(os.path.join(folder, "JoyPro2-private", "device-photos"))
    for candidate in candidates:
        if candidate and os.path.isfile(os.path.join(candidate, "manifest.json")):
            return os.path.abspath(candidate)
    fail("the private photo backup (manifest.json) was not found; pass --photos or set JOYPRO_PHOTO_BACKUP")


def out_folder(arg):
    folder = os.path.abspath(arg or os.environ.get("JOYPRO_CURATE_OUT") or os.path.join(tempfile.gettempdir(), "joypro-curation"))
    if os.path.normcase(folder).startswith(os.path.normcase(REPO)):
        fail("the output folder must be outside the repository (renders show the makers' photos): " + folder)
    os.makedirs(folder, exist_ok=True)
    return folder


def engine_path(arg):
    for candidate in (arg, os.path.join(REPO, "engine", "bin", "x64", "Debug", "engine.exe"), os.path.join(REPO, "engine", "bin", "x64", "Release", "engine.exe")):
        if candidate and os.path.isfile(candidate):
            return candidate
    fail("engine.exe not found; build engine/engine.csproj or pass --engine")


def engine(args, *command):
    key = tuple(command)
    if key in _engine_cache:
        return _engine_cache[key]
    env = dict(os.environ)
    env["JOYPRO_DEVICE_KB"] = os.path.join(DB, "Devices")
    result = subprocess.run([engine_path(args.engine)] + list(command), capture_output=True, text=True, encoding="utf-8", env=env)
    if result.returncode != 0:
        fail("engine " + " ".join(command) + " failed: " + (result.stdout + result.stderr).strip()[:2000])
    _engine_cache[key] = result.stdout
    return result.stdout


def schematic(args, model):
    return json.loads(engine(args, "devices", "reference", "show", model))


def photo_refs(args, model):
    for item in json.loads(engine(args, "devices", "reference", "photos", model)):
        if item["Id"] == model:
            return item["Photos"]
    fail("model '" + model + "' is not in the catalog")


def manifest(photos):
    with open(os.path.join(photos, "manifest.json"), encoding="utf-8-sig") as handle:
        return json.load(handle)


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def find_photo(args, model, view):
    """The backed-up photo of a model by view tag (or by the start of its hash): manifest entry and local file."""
    photos = find_photos(args.photos)
    entries = [e for e in manifest(photos) if e["model"] == model]
    match = [e for e in entries if e["view"] == view] or [e for e in entries if e["sha256"].startswith(view)]
    if not match:
        fail("no backed-up photo '" + view + "' of " + model + " (have: " + ", ".join(e["view"] for e in entries) + ")")
    entry = match[0]
    path = os.path.join(photos, entry["file"].replace("\\", os.sep))
    if sha256_of(path) != entry["sha256"]:
        fail("the backed-up file does not match its manifest hash: " + path)
    return entry, path


def load_image(path):
    from PIL import Image
    image = Image.open(path)
    image.load()
    return image.convert("RGB")


def font(size):
    from PIL import ImageFont
    for name in ("arial.ttf", "segoeui.ttf", "DejaVuSans.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default()


def halo_text(draw, xy, text, fnt, fill=(255, 255, 255), halo=(0, 0, 0)):
    x, y = xy
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            if dx or dy:
                draw.text((x + dx, y + dy), text, font=fnt, fill=halo)
    draw.text((x, y), text, font=fnt, fill=fill)


# ------------------------------------------------------------------------------------------------ info / grid

def cmd_info(args):
    sch = schematic(args, args.model)
    print(sch["Name"] + " (" + sch["ModelId"] + ") | " + sch["Kind"] + " | " + str(len(sch["Markers"])) + " parts")
    for m in sch["Markers"]:
        inputs = ", ".join(i["Input"] + ("=" + i["Control"].split(".")[-1] if i["Control"] else "") for i in m["Inputs"])
        print("  {:<34} {:<9} {:<40} {}".format(m["Id"], m["Type"], m["Name"][:40], inputs))
    photos = find_photos(args.photos)
    refs = {p["Url"]: p for p in photo_refs(args, args.model)}
    print("Photos:")
    for e in [e for e in manifest(photos) if e["model"] == args.model]:
        from PIL import Image
        with Image.open(os.path.join(photos, e["file"].replace("\\", os.sep))) as im:
            size = "{}x{}".format(*im.size)
        ref = refs.get(e["url"])
        state = "referenced" if ref else "NOT referenced any more"
        if ref and ref.get("Sha256"):
            state += ", hash on record" + ("" if ref["Sha256"] == e["sha256"] else " (DIFFERENT)")
        curated = set_file_of(args.model, e["sha256"])
        print("  {:<14} {:<10} {}  {}  {}{}".format(e["view"], size, e["sha256"][:12], state, e["url"], " | curated" if curated else ""))


def cmd_grid(args):
    from PIL import ImageDraw
    entry, path = find_photo(args, args.model, args.view)
    image = load_image(path)
    w, h = image.size
    x0, y0, x1, y1 = args.crop if args.crop else (0.0, 0.0, 1.0, 1.0)
    crop = image.crop((int(x0 * w), int(y0 * h), int(round(x1 * w)), int(round(y1 * h))))
    scale = max(1.0, 1100.0 / max(crop.size))
    if scale != 1.0:
        crop = crop.resize((int(crop.size[0] * scale), int(crop.size[1] * scale)))
    draw = ImageDraw.Draw(crop, "RGBA")
    span = max(x1 - x0, y1 - y0)
    step = 0.05 if span > 0.6 else 0.02 if span > 0.25 else 0.01
    fnt = font(13)

    def px(v, lo, hi, size):
        return (v - lo) / (hi - lo) * size

    k = 0
    while k * step <= 1.0 + 1e-9:
        v = round(k * step, 4)
        major = abs((v / (step * 2)) - round(v / (step * 2))) < 1e-6
        color = (255, 0, 255, 170) if major else (0, 200, 255, 110)
        if x0 - 1e-9 <= v <= x1 + 1e-9:
            gx = px(v, x0, x1, crop.size[0])
            draw.line([(gx, 0), (gx, crop.size[1])], fill=color, width=1)
            if major:
                halo_text(draw, (gx + 2, 2), "{:g}".format(v), fnt, fill=(255, 220, 0))
        if y0 - 1e-9 <= v <= y1 + 1e-9:
            gy = px(v, y0, y1, crop.size[1])
            draw.line([(0, gy), (crop.size[0], gy)], fill=color, width=1)
            if major:
                halo_text(draw, (2, gy + 1), "{:g}".format(v), fnt, fill=(255, 220, 0))
        k += 1
    name = "{}.{}.grid{}.png".format(args.model, entry["view"], "" if not args.crop else "-{:g}-{:g}-{:g}-{:g}".format(x0, y0, x1, y1))
    target = os.path.join(out_folder(args.out), name)
    crop.save(target)
    print(target + "  ({}x{} px photo, grid step {:g})".format(w, h, step))


# ------------------------------------------------------------------------------------------------ drafts

def parse_draft(path):
    header, lines = {}, []
    with open(path, encoding="utf-8") as handle:
        for number, raw in enumerate(handle, 1):
            line = raw.strip()
            if not line:
                continue
            if line.startswith("#"):
                if ":" in line:
                    key, value = line[1:].split(":", 1)
                    key = key.strip().lower()
                    if key and " " not in key:
                        # A repeated header line (several '# note:') continues the text.
                        header[key] = (header[key] + " " + value.strip()) if key in header else value.strip()
                continue
            text, _, note = line.partition("|")
            tokens = text.split()
            lines.append((number, tokens, note.strip()))
    return header, lines


def draft_from_set(data, sch):
    out = ["# model: " + data["model"], "# view: " + data["photo"].get("view", ""), "# sha256: " + data["photo"].get("sha256", "")]
    for key in ("source", "mapping", "confidence", "note"):
        value = data.get("mappingSource" if key == "mapping" else key, "")
        if value:
            out.append("# {}: {}".format(key, value))
    parts = data.get("parts", {})
    mapping = [p.get("mapping") for p in parts.values() if p.get("mapping")]
    default_map = max(set(mapping), key=mapping.count) if mapping else "medium"
    out.append("# mapping-confidence: " + default_map)
    for m in sch["Markers"]:
        pid = m["Id"]
        info = parts.get(pid, {})
        note = info.get("note", "")
        extra = "" if info.get("mapping", default_map) == default_map else " map=" + info["mapping"]
        if pid in data.get("markers", {}):
            x, y = data["markers"][pid]
            out.append("{} {:.3f} {:.3f} {}{}{}".format(pid, x, y, info.get("confidence", "medium"), extra, " | " + note if note else ""))
        elif pid in data.get("candidates", {}):
            x, y = data["candidates"][pid]
            reason = data.get("unplaced", {}).get(pid, "")
            low = " low" if info.get("mapping", default_map) != "low" else " medium"
            out.append("{} {:.3f} {:.3f}{}{} | {}".format(pid, x, y, low, extra, reason))
        else:
            reason = data.get("unplaced", {}).get(pid, "not placed yet")
            out.append("{} -{} | {}".format(pid, extra, reason))
    return "\n".join(out) + "\n"


def cmd_draft(args):
    sch = schematic(args, args.model)
    entry, _ = find_photo(args, args.model, args.view)
    existing = set_file_of(args.model, entry["sha256"])
    if existing:
        with open(existing, encoding="utf-8-sig") as handle:
            text = draft_from_set(json.load(handle), sch)
    else:
        rows = ["# model: " + args.model, "# view: " + entry["view"], "# sha256: " + entry["sha256"], "# source: JoyPro curated on the maker's photo",
                "# mapping: <where the button numbers come from>", "# mapping-confidence: medium", "# confidence: medium",
                "# Parts (x y confidence | note, or - | reason):"]
        for m in sch["Markers"]:
            inputs = " ".join(i["Input"].replace("JOY_", "") for i in m["Inputs"])
            rows.append("{} - | not placed yet   [{}: {} | {}]".format(m["Id"], m["Type"], m["Name"], inputs))
        text = "\n".join(rows) + "\n"
    target = os.path.join(out_folder(args.out), "{}.{}.txt".format(args.model, entry["view"]))
    with open(target, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    print(target)


def build_set(args, draft):
    header, lines = parse_draft(draft)
    model, view = header.get("model", ""), header.get("view", "")
    if not model or not view:
        fail(draft + ": the header needs '# model:' and '# view:'")
    sch = schematic(args, model)
    by_id = {m["Id"]: m for m in sch["Markers"]}
    # The photo: by its hash when the draft names it ("# sha256:"), else the photo of an existing set of this model
    # with the same view tag (gallery numbers change when a shop gallery changes), else by the view tag.
    sha = header.get("sha256", "")
    if not sha:
        for _, existing in load_sets():
            if existing.get("model") == model and existing.get("photo", {}).get("view") in (header.get("retag"), view):
                sha = existing["photo"]["sha256"]
                break
    entry, path = find_photo(args, model, sha or view)
    # One photo content can sit at several addresses (a shop's main image and its gallery copy): keep the address of
    # an existing set, else one whose hash is on record.
    same = [e for e in manifest(find_photos(args.photos)) if e["model"] == model and e["sha256"] == entry["sha256"]]
    if len(same) > 1:
        refs_by_url = {p["Url"]: p for p in photo_refs(args, model)}
        known_url = next((d["photo"]["url"] for _, d in load_sets() if d.get("model") == model and d["photo"].get("sha256") == entry["sha256"]), None)
        preferred = [e for e in same if e["url"] == known_url] or [e for e in same if refs_by_url.get(e["url"], {}).get("Sha256")] or \
            [e for e in same if e["view"] == "main"] or same
        entry = preferred[0]
    refs = {p["Url"]: p for p in photo_refs(args, model)}
    ref = refs.get(entry["url"])
    if ref is None:
        fail(draft + ": the photo is not referenced by " + model + " any more: " + entry["url"])
    default_map = header.get("mapping-confidence", "medium")
    markers, parts, unplaced, candidates = {}, {}, {}, {}
    problems = []
    for number, tokens, note in lines:
        pid = tokens[0]
        part = by_id.get(pid)
        if part is None:
            problems.append("line {}: '{}' is no part of {}".format(number, pid, model))
            continue
        mapping = default_map
        rest = []
        for token in tokens[1:]:
            if token.startswith("map="):
                mapping = token[4:]
            else:
                rest.append(token)
        if mapping not in CONFIDENCES:
            problems.append("line {}: mapping confidence '{}'".format(number, mapping))
        info = {"type": part["Type"], "name": part["Name"],
                "inputs": [{"input": i["Input"], "control": i["Control"]} for i in part["Inputs"]],
                "mapping": mapping}
        if rest and rest[0] == "-":
            unplaced[pid] = note or "not placed"
        elif len(rest) >= 3:
            x, y, confidence = float(rest[0]), float(rest[1]), rest[2]
            if not (0 <= x <= 1 and 0 <= y <= 1):
                problems.append("line {}: position outside the photo".format(number))
            if confidence not in CONFIDENCES:
                problems.append("line {}: confidence '{}'".format(number, confidence))
            if confidence == "low" or mapping == "low":
                # Not certain enough to show: kept as a candidate for a later check (marker assistant, real device).
                candidates[pid] = [round(x, 4), round(y, 4)]
                unplaced[pid] = note or ("position uncertain" if confidence == "low" else "input mapping uncertain")
            else:
                markers[pid] = [round(x, 4), round(y, 4)]
                info["confidence"] = confidence
                if note:
                    info["note"] = note
        else:
            problems.append("line {}: expected '<part> <x> <y> <confidence>' or '<part> - | reason'".format(number))
        parts[pid] = info
    for pid, part in by_id.items():
        if pid not in parts:
            unplaced[pid] = "not reviewed"
            parts[pid] = {"type": part["Type"], "name": part["Name"],
                          "inputs": [{"input": i["Input"], "control": i["Control"]} for i in part["Inputs"]], "mapping": default_map}
    if problems:
        fail(draft + ":\n  " + "\n  ".join(problems))
    view_tag = header.get("retag", "") or ("main" if ref["View"] == "main" else ref["View"])
    data = {
        "format": "joypro-device-layout",
        "version": 1,
        "model": model,
        "photo": {"kind": "vendor", "url": entry["url"], "sha256": entry["sha256"], "view": view_tag},
        "markers": {k: markers[k] for k in sorted(markers)},
        "parts": {k: parts[k] for k in by_id if k in parts},
        "unplaced": {k: unplaced[k] for k in by_id if k in unplaced},
        "candidates": {k: candidates[k] for k in sorted(candidates)},
        "source": header.get("source", "JoyPro curated on the maker's photo"),
        "mappingSource": header.get("mapping", ""),
        "confidence": header.get("confidence", "medium"),
    }
    if header.get("note"):
        data["note"] = header["note"]
    return data, entry, ref


def write_set(data, target):
    """Indented JSON with one line per marker and per input (the files are read by people too)."""
    def dump(value):
        return json.dumps(value, ensure_ascii=False)
    out = ["{"]
    keys = list(data)
    for index, key in enumerate(keys):
        value = data[key]
        comma = "," if index < len(keys) - 1 else ""
        if key in ("markers", "candidates"):
            items = ['    {}: [{}, {}]'.format(dump(k), v[0], v[1]) for k, v in value.items()]
            out.append('  {}: {{'.format(dump(key)) + ("\n" + ",\n".join(items) + "\n  " if items else "") + "}" + comma)
        elif key == "parts":
            rows = []
            for pid, info in value.items():
                inner = []
                for field, field_value in info.items():
                    if field == "inputs":
                        inputs = ",\n".join("        " + dump(i) for i in field_value)
                        inner.append('      "inputs": [' + ("\n" + inputs + "\n      " if inputs else "") + "]")
                    else:
                        inner.append("      {}: {}".format(dump(field), dump(field_value)))
                rows.append("    {}: {{\n{}\n    }}".format(dump(pid), ",\n".join(inner)))
            out.append('  "parts": {' + ("\n" + ",\n".join(rows) + "\n  " if rows else "") + "}" + comma)
        elif key == "unplaced":
            items = ["    {}: {}".format(dump(k), dump(v)) for k, v in value.items()]
            out.append('  "unplaced": {' + ("\n" + ",\n".join(items) + "\n  " if items else "") + "}" + comma)
        elif key == "callouts":
            # The sheet: its fields on one line each, one line per table and per leader line.
            inner = []
            for field, field_value in value.items():
                if field in ("tables", "lines"):
                    rows = ",\n".join("      " + dump(item) for item in field_value)
                    inner.append('    {}: ['.format(dump(field)) + ("\n" + rows + "\n    " if rows else "") + "]")
                else:
                    inner.append("    {}: {}".format(dump(field), dump(field_value)))
            out.append('  "callouts": {\n' + ",\n".join(inner) + "\n  }" + comma)
        else:
            out.append("  {}: {}{}".format(dump(key), dump(value), comma))
    out.append("}")
    with open(target, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(out) + "\n")


def cmd_build(args):
    for draft in args.drafts:
        data, entry, ref = build_set(args, draft)
        if not data["markers"]:
            # A set that shows nothing would count as curated; its candidates stay in the draft.
            print("{}: nothing placed on {}, no set written ({} candidate(s) in the draft)".format(data["model"], entry["view"], len(data["candidates"])))
            continue
        os.makedirs(LAYOUTS, exist_ok=True)
        target = set_file_of(data["model"], entry["sha256"]) or os.path.join(LAYOUTS, "{}.{}.json".format(data["model"], data["photo"]["view"]))
        if not set_file_of(data["model"], entry["sha256"]) and os.path.isfile(target):
            # Another photo of the model has a set under the same view tag (a replaced or second photo): keep it.
            target = os.path.join(LAYOUTS, "{}.{}-{}.json".format(data["model"], data["photo"]["view"], entry["sha256"][:8]))
        if os.path.isfile(target):
            with open(target, encoding="utf-8-sig") as handle:
                old = json.load(handle)
            if old.get("callouts"):
                # The sheet stays (with its pinned tables); "sheet" arranges it again for the new positions.
                data["callouts"] = old["callouts"]
                print("  the set has a callout sheet: run 'sheet' again")
        write_set(data, target)
        placed = len(data["markers"])
        total = len(data["parts"])
        print("{}: {} of {} parts placed -> {}".format(data["model"], placed, total, os.path.relpath(target, REPO)))
        if not args.no_hash:
            shows = sorted(set(data["markers"]))
            command = ["devices", "reference", "photo-add", "--model", data["model"], "--url", entry["url"],
                       "--view", data["photo"]["view"], "--sha256", entry["sha256"],
                       "--confidence", ref.get("Confidence") or "medium"]
            if ref.get("Page"):
                command += ["--page", ref["Page"]]
            if shows:
                command += ["--shows", ",".join(shows)]
            _engine_cache.clear()
            engine(args, *command)
            print("  photo hash recorded in DB/DevicePhotos")


# ------------------------------------------------------------------------------------------------ render

COLORS = {"high": (40, 200, 90), "medium": (255, 170, 0), "low": (230, 50, 50)}


def cmd_render(args):
    from PIL import Image, ImageDraw
    if args.target.lower().endswith(".json"):
        set_file = args.target
    elif args.target.lower().endswith(".txt"):
        data, entry, _ = build_set(args, args.target)
        set_file = None
    else:
        if not args.view:
            fail("render needs <model> <view>, a set .json or a draft .txt")
        entry, _ = find_photo(args, args.target, args.view)
        set_file = set_file_of(args.target, entry["sha256"])
        if not set_file:
            fail("no curated set for that photo yet")
    if set_file:
        with open(set_file, encoding="utf-8-sig") as handle:
            data = json.load(handle)
    model = data["model"]
    photos = find_photos(args.photos)
    same = [e for e in manifest(photos) if e["model"] == model and e["sha256"] == data["photo"]["sha256"]]
    entry = next((e for e in same if e["url"] == data["photo"].get("url")), same[0] if same else None)
    if entry is None:
        fail("the photo with hash " + data["photo"]["sha256"][:12] + " is not in the backup")
    image = load_image(os.path.join(photos, entry["file"].replace("\\", os.sep)))
    scale = 1000.0 / max(image.size)
    image = image.resize((int(image.size[0] * scale), int(image.size[1] * scale)))
    w, h = image.size
    legend_w = 560
    sch = schematic(args, model)
    order = [m["Id"] for m in sch["Markers"]]
    canvas = Image.new("RGB", (w + legend_w, max(h, 120 + 19 * (len(order) + 4))), (24, 24, 28))
    canvas.paste(image, (0, 0))
    draw = ImageDraw.Draw(canvas, "RGBA")
    small, label = font(12), font(13)
    parts = data.get("parts", {})
    for number, pid in enumerate(order, 1):
        if pid in data.get("candidates", {}):
            x, y = data["candidates"][pid]
            cx, cy = x * w, y * h
            draw.rectangle([cx - 5, cy - 5, cx + 5, cy + 5], outline=(0, 0, 0), width=3)
            draw.rectangle([cx - 5, cy - 5, cx + 5, cy + 5], outline=(170, 170, 255), width=1)
            halo_text(draw, (cx + 7, cy - 8), str(number) + "?", small, fill=(170, 170, 255))
        if pid not in data["markers"]:
            continue
        x, y = data["markers"][pid]
        cx, cy = x * w, y * h
        color = COLORS.get(parts.get(pid, {}).get("confidence", "medium"), (255, 255, 255))
        draw.ellipse([cx - 6, cy - 6, cx + 6, cy + 6], outline=(0, 0, 0), width=4)
        draw.ellipse([cx - 6, cy - 6, cx + 6, cy + 6], outline=color + (255,), width=2)
        draw.ellipse([cx - 1.5, cy - 1.5, cx + 1.5, cy + 1.5], fill=color + (255,))
        halo_text(draw, (cx + 8, cy - 8), str(number), label, fill=color)
    y = 8
    title = "{} | {} | {}".format(model, data["photo"].get("view", ""), data["photo"]["sha256"][:12])
    halo_text(draw, (w + 10, y), title, label)
    y += 22
    halo_text(draw, (w + 10, y), "{} of {} placed (green high, amber medium; blue squares: candidates, not shown)".format(len(data["markers"]), len(order)), small, fill=(200, 200, 200))
    y += 22
    for number, pid in enumerate(order, 1):
        info = parts.get(pid, {})
        placed = pid in data["markers"]
        color = COLORS.get(info.get("confidence", ""), (130, 130, 130)) if placed else \
            (170, 170, 255) if pid in data.get("candidates", {}) else (120, 120, 120)
        inputs = " ".join(i["input"].replace("JOY_", "") for i in info.get("inputs", []))
        text = "{:>2}. {} [{}] {}".format(number, pid, info.get("type", ""), inputs if placed else "- " + data.get("unplaced", {}).get(pid, ""))
        halo_text(draw, (w + 10, y), text[:84], small, fill=color)
        y += 19
    target = os.path.join(out_folder(args.out), "{}.{}.overlay.png".format(model, entry["view"]))
    canvas.save(target)
    print(target)


# ------------------------------------------------------------------------------------------------ check / status

def photo_png(args, data, folder):
    """A PNG copy of the set's photo from the private backup (the engine reads PNG, not WebP); outside the repository."""
    photos = find_photos(args.photos)
    same = [e for e in manifest(photos) if e["model"] == data["model"] and e["sha256"] == data["photo"]["sha256"]]
    if not same:
        fail("the photo with hash " + data["photo"]["sha256"][:12] + " is not in the backup")
    target = os.path.join(folder, "photo-" + data["photo"]["sha256"][:16] + ".png")
    if not os.path.isfile(target):
        from PIL import Image
        image = Image.open(os.path.join(photos, same[0]["file"].replace("\\", os.sep)))
        image.load()
        image.convert("RGBA" if "A" in image.getbands() or image.mode == "P" else "RGB").save(target)
    return target


def sheet_quality(check):
    """'good', 'fair' or 'poor' from the engine's check of a sheet."""
    if not (check.get("Overlaps", 0) == 0 and check.get("NonOrthogonal", 0) == 0 and check.get("Outside", 0) == 0 and
            check.get("LinesThroughTables", 0) == 0 and check.get("Linked", 0) == check.get("Positioned", 0)):
        return "poor"
    if check.get("Crossings", 0) > 4 or check.get("Shared", 0) > 0:
        return "fair" if check.get("Crossings", 0) <= 12 else "poor"
    return "good"


def cmd_sheet(args):
    """Arranges the callout sheet (tables + leader lines) of curated sets with the engine and stores it in the set."""
    out = out_folder(args.out)
    folder = os.path.join(out, "sheets")
    os.makedirs(folder, exist_ok=True)
    rows = []
    for name, data in load_sets():
        if args.query and args.query.lower() not in name.lower():
            continue
        if not data.get("markers"):
            continue
        path = os.path.join(LAYOUTS, name)
        picture = photo_png(args, data, folder)
        render = os.path.join(folder, name.replace(".json", ".callouts.png"))
        env = dict(os.environ)
        env["JOYPRO_DEVICE_KB"] = os.path.join(DB, "Devices")
        # Titles follow the catalog (a fixed name reaches the sheet); pinned places and widths stay.
        trial = os.path.join(folder, "sheet-trial.json")
        with open(trial, "w", encoding="utf-8") as handle:
            json.dump(without_titles(data), handle, ensure_ascii=False)
        command = [engine_path(args.engine), "layout", "callouts", "--layout", trial, "--arrange", "--picture", picture]
        if not args.no_png:
            command += ["--png", render, "--sample", "--print-width", "0"]
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", env=env)
        if result.returncode != 0:
            print("{}: engine failed: {}".format(name, (result.stdout + result.stderr).strip()[:400]))
            continue
        payload = json.loads(result.stdout[result.stdout.index("{"):])
        check, sheet = payload["Check"], payload["Sheet"]
        quality = sheet_quality(check)
        data["callouts"] = sheet
        write_set(data, path)
        pinned = sum(1 for t in sheet.get("tables", []) if t.get("pinned"))
        rows.append((name, quality, check))
        print("{:<62} {:<5} tables {:>2} lines {:>2} crossings {:>2} shared {:>2} other {:>2}{}{}".format(
            name, quality, check.get("Tables", 0), check.get("Lines", 0), check.get("Crossings", 0), check.get("Shared", 0),
            check.get("Other", 0), " pinned " + str(pinned) if pinned else "", "" if args.no_png else "  " + render))
    counts = {q: sum(1 for r in rows if r[1] == q) for q in ("good", "fair", "poor")}
    print("Sheets: {} (good {}, fair {}, poor {})".format(len(rows), counts["good"], counts["fair"], counts["poor"]))


def without_titles(data):
    copy = json.loads(json.dumps(data))
    for t in (copy.get("callouts") or {}).get("tables", []):
        t.pop("title", None)
    return copy


def arrange(args, data, picture, scratch):
    """The engine's arrangement of a set (its pinned tables kept) without storing it: (check, sheet)."""
    with open(scratch, "w", encoding="utf-8") as handle:
        json.dump(without_titles(data), handle, ensure_ascii=False)
    env = dict(os.environ)
    env["JOYPRO_DEVICE_KB"] = os.path.join(DB, "Devices")
    result = subprocess.run([engine_path(args.engine), "layout", "callouts", "--layout", scratch, "--arrange", "--picture", picture],
                            capture_output=True, text=True, encoding="utf-8", env=env)
    if result.returncode != 0:
        return None, None
    payload = json.loads(result.stdout[result.stdout.index("{"):])
    return payload["Check"], payload["Sheet"]


def sheet_cost(check):
    bad = check.get("Overlaps", 0) + check.get("NonOrthogonal", 0) + check.get("Outside", 0) + check.get("LinesThroughTables", 0) + \
        (check.get("Positioned", 0) - check.get("Linked", 0))
    return bad * 1000 + check.get("Crossings", 0) * 3 + check.get("Shared", 0) * 2


def cmd_rebalance(args):
    """Hand fixes by rule for fair and poor sheets: a column table whose spot lies on the other half of the device is
    pinned on the other column at its spot's height, in a free slot; a move stays only when the sheet gets better."""
    out = out_folder(args.out)
    folder = os.path.join(out, "sheets")
    os.makedirs(folder, exist_ok=True)
    scratch = os.path.join(folder, "rebalance-trial.json")
    for name, data in load_sets():
        if args.query and args.query.lower() not in name.lower():
            continue
        sheet = data.get("callouts")
        if not sheet or not data.get("markers"):
            continue
        picture = photo_png(args, data, folder)
        check, sheet = arrange(args, data, picture, scratch)
        if check is None or sheet_quality(check) == "good":
            continue
        start = (sheet_quality(check), check.get("Crossings", 0))
        best = sheet_cost(check)
        rh = sheet["rowHeight"]
        title = round(rh * 0.72)
        gap = round(rh * 0.3)
        px, py, pw, ph = sheet["picture"]

        def height(t):
            visible = [r for r in t.get("rows", []) if r.get("input") not in t.get("hidden", [])]
            if any("cell" in r for r in visible):
                # A key grid: each grid row as high as its fullest cell (a switch's up and down are two lines).
                lines = {}
                for r in visible:
                    row, _, sub = r.get("cell", [0, 0, 0])
                    lines[row] = max(lines.get(row, 1), sub + 1)
                return title + sum(lines.values()) * rh
            cols = max(1, t.get("columns", 1))
            return title + -(-len(visible) // cols) * rh

        spots = [px + t["spot"][0] * pw for t in sheet["tables"] if t.get("spot") and t.get("side") in ("left", "right")]
        if len(spots) < 4:
            continue
        mid = (min(spots) + max(spots)) / 2
        moved = 0
        for _ in range(12):
            tables = sheet["tables"]
            columns = {side: [t for t in tables if t.get("side") == side] for side in ("left", "right")}
            if not columns["left"] or not columns["right"]:
                break
            x_of = {"left": min(t["x"] for t in columns["left"]), "right": min(t["x"] for t in columns["right"])}
            # Tables nearest to the other column first (a spot near the middle can as well go to the other side).
            candidates = []
            for side, other in (("right", "left"), ("left", "right")):
                for t in columns[side]:
                    if t.get("pinned") or not t.get("spot"):
                        continue
                    sx = px + t["spot"][0] * pw
                    distance = (mid - sx) if side == "right" else (sx - mid)
                    candidates.append((distance, t, other))
            candidates = sorted(candidates, key=lambda c: -c[0])[:10]
            improved = False
            for _, t, other in sorted(candidates, key=lambda c: -c[0]):
                sy = py + t["spot"][1] * ph
                want = sy - title - rh / 2
                h = height(t)
                taken = [(o["y"], o["y"] + height(o)) for o in columns[other]]
                y = None
                for delta in sorted(range(-int(ph), int(ph), 2), key=abs):
                    top = want + delta
                    if top < 0:
                        continue
                    if all(top + h + gap <= a or top >= b + gap for a, b in taken):
                        y = top
                        break
                if y is None:
                    continue
                trial = json.loads(json.dumps(data))
                trial["callouts"] = json.loads(json.dumps(sheet))
                for tt in trial["callouts"]["tables"]:
                    if tt["id"] == t["id"]:
                        tt["x"], tt["y"], tt["side"], tt["pinned"] = x_of[other], round(y, 1), other, True
                c2, s2 = arrange(args, trial, picture, scratch)
                if os.environ.get("JOYPRO_REBALANCE_DEBUG"):
                    print("   try {} -> {} y {} cost {} (best {}) {}".format(t["id"], other, round(y), None if c2 is None else sheet_cost(c2), best,
                                                                         c2 and {k: c2.get(k) for k in ("Overlaps", "Crossings", "Shared", "LinesThroughTables", "Outside")}))
                if c2 is not None and sheet_cost(c2) < best:
                    best, check, sheet = sheet_cost(c2), c2, s2
                    data["callouts"] = sheet
                    moved += 1
                    improved = True
                    break
            if not improved:
                break
        if moved:
            write_set(data, os.path.join(LAYOUTS, name))
        print("{:<62} {} {:>3} crossings -> {} {:>3} crossings, {} table(s) pinned".format(
            name, start[0], start[1], sheet_quality(check), check.get("Crossings", 0), moved))


def cmd_refresh(args):
    """Rebuilds sets from themselves after catalog changes (part names, inputs), positions and sheets kept."""
    out = out_folder(args.out)
    for name, data in load_sets():
        if args.query and args.query.lower() not in name.lower():
            continue
        if not data.get("parts"):
            continue
        sch = schematic(args, data["model"])
        draft = os.path.join(out, "refresh-" + name.replace(".json", ".txt"))
        with open(draft, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(draft_from_set(data, sch))
        fresh, _, _ = build_set(args, draft)
        if data.get("callouts"):
            fresh["callouts"] = data["callouts"]
        fresh["photo"]["view"] = data["photo"].get("view", fresh["photo"]["view"])
        if json.dumps(fresh, sort_keys=True) != json.dumps(data, sort_keys=True):
            write_set(fresh, os.path.join(LAYOUTS, name))
            print("refreshed " + name)


def cmd_pin(args):
    """Pins a table of a set's sheet at a place (sheet coordinates of its top-left corner); 'sheet' keeps it there."""
    entry, _ = find_photo(args, args.model, args.view)
    path = set_file_of(args.model, entry["sha256"])
    if not path:
        fail("no curated set for that photo")
    with open(path, encoding="utf-8-sig") as handle:
        data = json.load(handle)
    sheet = data.get("callouts")
    if not sheet:
        fail("the set has no sheet yet: run 'sheet' first")
    table = next((t for t in sheet.get("tables", []) if t.get("id") == args.table), None)
    if table is None:
        fail("no table '" + args.table + "' (have: " + ", ".join(t.get("id", "") for t in sheet.get("tables", [])) + ")")
    if args.unpin:
        table.pop("pinned", None)
    else:
        table["x"], table["y"], table["pinned"] = args.x, args.y, True
        if args.side:
            table["side"] = args.side
    write_set(data, path)
    print("{}: {} {}".format(os.path.basename(path), args.table, "unpinned" if args.unpin else "pinned at {} {}".format(args.x, args.y)))


def set_file_of(model, sha):
    """The curated set of a model for one photo content (any file name), or None."""
    for name, data in load_sets():
        if data.get("model") == model and data.get("photo", {}).get("sha256") == sha:
            return os.path.join(LAYOUTS, name)
    return None


def load_sets():
    sets = []
    if os.path.isdir(LAYOUTS):
        for name in sorted(os.listdir(LAYOUTS)):
            if name.endswith(".json"):
                with open(os.path.join(LAYOUTS, name), encoding="utf-8-sig") as handle:
                    data = json.load(handle)
                # Sets on JoyPro's own drawings (tools/device_drawings.py) have no photo; that tool keeps them.
                if (data.get("photo") or {}).get("kind") == "lineart":
                    continue
                sets.append((name, data))
    return sets


def cmd_check(args):
    problems = []
    photos = None
    try:
        photos = find_photos(args.photos)
        known = {(e["model"], e["sha256"]) for e in manifest(photos)}
    except SystemExit:
        known = None
    sets = load_sets()
    for name, data in sets:
        model = data.get("model", "")
        sch = schematic(args, model)
        by_id = {m["Id"]: m for m in sch["Markers"]}
        refs = {p["Url"]: p for p in photo_refs(args, model)}
        photo = data.get("photo", {})
        ref = refs.get(photo.get("url", ""))
        if ref is None:
            problems.append(name + ": the photo is not referenced by " + model)
        elif ref.get("Sha256") != photo.get("sha256"):
            problems.append(name + ": DB/DevicePhotos has no or another hash for the photo")
        if known is not None and (model, photo.get("sha256")) not in known:
            problems.append(name + ": the photo hash is not in the private backup")
        for pid, xy in data.get("markers", {}).items():
            if pid not in by_id:
                problems.append(name + ": '" + pid + "' is no part of " + model)
            elif not (0 <= xy[0] <= 1 and 0 <= xy[1] <= 1):
                problems.append(name + ": '" + pid + "' outside the photo")
        for pid, info in data.get("parts", {}).items():
            part = by_id.get(pid)
            if part is None:
                problems.append(name + ": parts lists '" + pid + "', no part of " + model)
                continue
            own = {(i["Input"], i["Control"]) for i in part["Inputs"]}
            for item in info.get("inputs", []):
                if (item.get("input"), item.get("control")) not in own:
                    problems.append(name + ": " + pid + " lists input " + str(item.get("input")) + " that the catalog does not give it")
        for pid in data.get("unplaced", {}):
            if pid in data.get("markers", {}):
                problems.append(name + ": '" + pid + "' is placed and unplaced")
    print("Curated sets: {}, positions: {}".format(len(sets), sum(len(d.get("markers", {})) for _, d in sets)))
    print("Problems: {}".format(len(problems)))
    for problem in problems:
        print("  - " + problem)
    sys.exit(1 if problems else 0)


def cmd_status(args):
    photos = find_photos(args.photos)
    sets = {(d["model"], d["photo"]["sha256"]): d for _, d in load_sets()}
    rows = {}
    for e in manifest(photos):
        if args.query and args.query.lower() not in e["model"]:
            continue
        rows.setdefault(e["model"], []).append(e)
    for model, entries in sorted(rows.items()):
        done = [sets[(model, e["sha256"])] for e in entries if (model, e["sha256"]) in sets]
        text = ", ".join("{} {}/{}".format(d["photo"]["view"], len(d["markers"]), len(d.get("parts", d["markers"]))) for d in done)
        print("{:<46} {:>2} photo(s)  {}".format(model, len(entries), text or "-"))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--photos")
    common.add_argument("--out")
    common.add_argument("--engine")
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("info", parents=[common])
    p.add_argument("model")
    p = sub.add_parser("grid", parents=[common])
    p.add_argument("model")
    p.add_argument("view")
    p.add_argument("--crop", type=float, nargs=4)
    p = sub.add_parser("draft", parents=[common])
    p.add_argument("model")
    p.add_argument("view")
    p = sub.add_parser("build", parents=[common])
    p.add_argument("drafts", nargs="+")
    p.add_argument("--no-hash", action="store_true")
    p = sub.add_parser("render", parents=[common])
    p.add_argument("target")
    p.add_argument("view", nargs="?")
    sub.add_parser("check", parents=[common])
    p = sub.add_parser("status", parents=[common])
    p.add_argument("query", nargs="?")
    p = sub.add_parser("sheet", parents=[common])
    p.add_argument("query", nargs="?")
    p.add_argument("--no-png", action="store_true")
    p = sub.add_parser("rebalance", parents=[common])
    p.add_argument("query", nargs="?")
    p = sub.add_parser("refresh", parents=[common])
    p.add_argument("query", nargs="?")
    p = sub.add_parser("pin", parents=[common])
    p.add_argument("model")
    p.add_argument("view")
    p.add_argument("table")
    p.add_argument("x", type=float, nargs="?", default=0)
    p.add_argument("y", type=float, nargs="?", default=0)
    p.add_argument("--side")
    p.add_argument("--unpin", action="store_true")
    args = parser.parse_args()
    {"info": cmd_info, "grid": cmd_grid, "draft": cmd_draft, "build": cmd_build, "render": cmd_render,
     "check": cmd_check, "status": cmd_status, "sheet": cmd_sheet, "pin": cmd_pin, "rebalance": cmd_rebalance, "refresh": cmd_refresh}[args.command](args)


if __name__ == "__main__":
    main()
