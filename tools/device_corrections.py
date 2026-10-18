"""Merges users' device layout corrections (joypro-device-correction, sent from the Layout page with "Send correction
to JoyPro", see docs/device-reference.md "Correcting JoyPro") into JoyPro's data.

    python tools/device_corrections.py show  <correction.json> ...        what a correction changes, against the shipped data
    python tools/device_corrections.py merge <correction.json> ... [--controls]

merge:
  - positions on JoyPro's own drawing (picture kind "lineart" with a drawing of tools/device_drawings): written into the
    drawing's spec ("positions": overrides by part id, "unplaced" for removed parts), then rebuild it with
    tools/device_drawings.py build <model>;
  - positions on a maker's photo: into the curated set of that photo (DB/DeviceLayouts, by its content hash);
  - --controls: added / changed controls go into the model's catalog entry (DB/Devices); without it they are only listed.
Review every correction before merging (the user can be wrong too); the commit message names the issue.
"""
import argparse
import glob
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DB = os.path.join(REPO, "EngineOnly_Runtime", "DB")
LAYOUTS = os.path.join(DB, "DeviceLayouts")
SPECS = os.path.join(REPO, "tools", "device_drawings")
FORMAT = "joypro-device-correction"


def read(path):
    with open(path, encoding="utf-8-sig") as handle:
        data = json.load(handle)
    if data.get("format") != FORMAT:
        sys.exit("%s: not a JoyPro device correction" % path)
    return data


def write_json(path, data):
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(data, handle, ensure_ascii=False, indent=1)
        handle.write("\n")


def target_set(corr):
    """The shipped position set of the corrected picture: (path, data) or (None, None)."""
    key = (corr.get("picture") or {}).get("key", "").lower()
    for path in sorted(glob.glob(os.path.join(LAYOUTS, corr["model"] + ".*.json"))):
        with open(path, encoding="utf-8-sig") as handle:
            data = json.load(handle)
        if data.get("model") == corr["model"] and (data.get("photo") or {}).get("sha256", "").lower() == key:
            return path, data
    return None, None


def spec_of(corr, set_data):
    view = (set_data.get("photo") or {}).get("view", "main") if set_data else (corr.get("picture") or {}).get("view", "main")
    path = os.path.join(SPECS, "%s.%s.json" % (corr["model"], view or "main"))
    return path if os.path.isfile(path) else None


def describe(corr):
    path, data = target_set(corr)
    shipped = (data or {}).get("markers", {})
    print("%s (%s), VID:PID %s, picture %s %s -> %s" % (corr.get("modelName", ""), corr["model"], corr.get("vidPid", "-"),
                                                       corr["picture"].get("kind"), corr["picture"].get("key", "")[:16],
                                                       os.path.basename(path) if path else "no shipped set"))
    for part, xy in sorted(corr.get("positions", {}).items()):
        old = shipped.get(part)
        print("  position %-34s %s%s" % (part, xy, "  (was %s)" % old if old else "  (new)"))
    for part in corr.get("removed", []):
        print("  removed  %s" % part)
    for c in corr.get("controls", []):
        print("  control  %-14s %-40s %s  [%s]" % (c["input"], c["control"], c.get("label", ""), c.get("change", "")))
    return path, data


def merge_controls(corr):
    path = None
    for f in glob.glob(os.path.join(DB, "Devices", "*.json")):
        with open(f, encoding="utf-8") as handle:
            text = handle.read()
        if '"id": "%s"' % corr["model"] in text:
            path = f
            break
    if path is None:
        print("  model not in the catalog: controls not merged")
        return
    with open(path, encoding="utf-8") as handle:
        catalog = json.load(handle)
    device = next(d for d in catalog["devices"] if d["id"] == corr["model"])
    for c in corr.get("controls", []):
        key = "axes" if c.get("type") == "analog" else "controls"
        items = device.setdefault(key, [])
        hit = next((x for x in device.get("axes", []) + device.get("controls", []) if x.get("input") == c["input"]), None)
        entry = {"input": c["input"], "control": c["control"]}
        if c.get("label"):
            entry["label"] = c["label"]
        if c.get("type") and c["type"] != "analog":
            entry["type"] = c["type"]
        if hit:
            hit.update(entry)
        else:
            items.append(entry)
    write_json(path, catalog)
    print("  controls merged into %s (reformat check: git diff)" % os.path.basename(path))


def cmd_show(args):
    for f in args.files:
        describe(read(f))


def cmd_merge(args):
    for f in args.files:
        corr = read(f)
        path, data = describe(corr)
        spec = spec_of(corr, data) if corr["picture"].get("kind") == "lineart" else None
        if spec:
            with open(spec, encoding="utf-8") as handle:
                s = json.load(handle)
            s.setdefault("positions", {}).update(corr.get("positions", {}))
            for part in corr.get("removed", []):
                s.setdefault("unplaced", {})[part] = "removed by a user correction"
            write_json(spec, s)
            print("  -> %s (rebuild: python tools/device_drawings.py build %s)" % (os.path.relpath(spec, REPO), corr["model"]))
        elif path:
            data.setdefault("markers", {}).update(corr.get("positions", {}))
            for part in corr.get("removed", []):
                data["markers"].pop(part, None)
                data.setdefault("unplaced", {})[part] = "removed by a user correction"
            for part in corr.get("positions", {}):
                (data.get("unplaced") or {}).pop(part, None)
                (data.get("candidates") or {}).pop(part, None)
            data.pop("callouts", None)
            write_json(path, data)
            print("  -> %s (arrange its sheet: python tools/device_positions.py sheet %s)" % (os.path.relpath(path, REPO), corr["model"]))
        else:
            print("  no shipped picture with this key: positions not merged (a user photo cannot be shipped)")
        if args.controls and corr.get("controls"):
            merge_controls(corr)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("show")
    s.add_argument("files", nargs="+")
    s.set_defaults(func=cmd_show)
    m = sub.add_parser("merge")
    m.add_argument("files", nargs="+")
    m.add_argument("--controls", action="store_true")
    m.set_defaults(func=cmd_merge)
    args = ap.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
