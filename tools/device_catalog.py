"""Adding devices and photo references to the catalog (docs/devices.md, docs/device-reference.md "Growing the catalog").

A catalog draft is a JSON file in the vendor-file format with only new devices (and, optionally, hosts to add):
    { "vendor": "VKB", "file": "vkb.json", "officialHosts": ["vkbsimcontrollers.com"], "devices": [ { ...entry... } ] }
A photo draft lists photo references for new or existing models:
    { "photos": [ { "model": "<id>", "url": "https://<official host>/...", "view": "front", "page": "<product page>",
                    "confidence": "high" } ] }
A fix draft changes fields of existing entries (names, labels; with a source):
    { "fixes": [ { "model": "<id>", "set": { "name": "..." }, "control": "<control id or empty>", "source": "https://..." } ] }

    python tools/device_catalog.py check  <draft.json> ...   checks drafts against the catalog (ids, vocabulary, inputs,
                                                              hosts) and builds a trial catalog with them through the engine
    python tools/device_catalog.py merge  <draft.json> ...   adds the devices to EngineOnly_Runtime/DB/Devices/<file>
    python tools/device_catalog.py photos <photos.json> ...  adds photo references (engine photo-add: official hosts only)
    python tools/device_catalog.py fix    <fixes.json> ...   applies fixes
    python tools/device_catalog.py ids    [query]            lists the catalog's ids, kinds, names, USB ids, photo yes/no
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DB = os.path.join(REPO, "EngineOnly_Runtime", "DB")
DEVICES = os.path.join(DB, "Devices")
KINDS = {"stick", "throttle", "hotas", "base", "pedals", "collective", "yoke", "quadrant", "panel", "mfd", "buttonbox", "controller", "other"}
HANDS = {"right", "left", "either", "none"}
CONFIDENCES = {"high", "medium", "low"}
TYPES = {"momentary", "latched", "rotary", "analog"}
FINGERS = {"index", "middle", "thumb", "pinky", "palm", "hand", "ring"}
VIEWS = re.compile(r"^(main|front|side|left|right|back|top|base|three-quarter|sheet|gallery-\d+)$")
INPUT = re.compile(r"^JOY_(X|Y|Z|RX|RY|RZ|SLIDER1|SLIDER2|BTN\d+|BTN_POV[1-4]_(U|UR|R|DR|D|DL|L|UL))$")
AXES = re.compile(r"^JOY_(X|Y|Z|RX|RY|RZ|SLIDER1|SLIDER2)$")
VIDPID = re.compile(r"^[0-9A-F]{4}:[0-9A-F]{4}$")
CONTROL = re.compile(r"^(stick|throttle|collective|pedals|yoke|quadrant|panel|mfd|wheel|controller|base|other)\.[a-z0-9_.\-]+$")


def fail(message):
    print("error: " + message, file=sys.stderr)
    sys.exit(2)


def load(path):
    with open(path, encoding="utf-8-sig") as handle:
        return json.load(handle)


def catalog_files():
    return sorted(f for f in os.listdir(DEVICES) if f.endswith(".json"))


def catalog():
    models = {}
    for name in catalog_files():
        data = load(os.path.join(DEVICES, name))
        for device in data.get("devices", []):
            models[device["id"]] = (name, data, device)
    return models


def host_ok(url, hosts):
    match = re.match(r"^https://([^/]+)/", url or "")
    if not match:
        return False
    host = match.group(1).lower()
    return any(host == h or host.endswith("." + h) for h in hosts)


def engine_path():
    for candidate in (os.path.join(REPO, "engine", "bin", "x64", "Debug", "engine.exe"), os.path.join(REPO, "engine", "bin", "x64", "Release", "engine.exe")):
        if os.path.isfile(candidate):
            return candidate
    fail("engine.exe not found; build engine/engine.csproj")


def run_engine(devices_folder, *command):
    env = dict(os.environ)
    env["JOYPRO_DEVICE_KB"] = devices_folder
    result = subprocess.run([engine_path()] + list(command), capture_output=True, text=True, encoding="utf-8", env=env)
    return result.returncode, (result.stdout + result.stderr)


def check_device(device, problems, known_ids, hosts):
    did = device.get("id", "")
    where = "device '" + did + "'"
    if not re.match(r"^[a-z0-9][a-z0-9\-]*$", did):
        problems.append(where + ": id must be a lower-case slug")
    if did in known_ids:
        problems.append(where + ": id already in the catalog")
    for field in ("name", "kind", "sources", "confidence"):
        if not device.get(field):
            problems.append(where + ": no " + field)
    if device.get("kind") not in KINDS:
        problems.append(where + ": kind '" + str(device.get("kind")) + "'")
    if device.get("hand", "none") not in HANDS:
        problems.append(where + ": hand '" + str(device.get("hand")) + "'")
    if device.get("confidence") not in CONFIDENCES:
        problems.append(where + ": confidence '" + str(device.get("confidence")) + "'")
    for vp in device.get("identify", {}).get("vidPids", []):
        if not VIDPID.match(vp):
            problems.append(where + ": VID:PID '" + vp + "' (upper-case hex, VVVV:PPPP)")
    for url in [device.get("image", "")] + [p.get("url", "") for p in device.get("photos", [])]:
        if url and not host_ok(url, hosts):
            problems.append(where + ": photo not on an official host: " + url)
    inputs = {}
    for group in ("axes", "controls"):
        for item in device.get(group, []):
            control, inp = item.get("control", ""), item.get("input", "")
            if not CONTROL.match(control):
                problems.append(where + ": control '" + control + "'")
            if inp:
                if not INPUT.match(inp):
                    problems.append(where + ": input '" + inp + "'")
                elif group == "axes" and not AXES.match(inp):
                    problems.append(where + ": axis input '" + inp + "' is not an axis")
                if inp in inputs and inputs[inp] != control and group == "controls":
                    problems.append(where + ": input " + inp + " on two controls (" + inputs[inp] + ", " + control + ")")
                inputs[inp] = control
            if item.get("type") and item["type"] not in TYPES:
                problems.append(where + ": type '" + item["type"] + "' on " + control)
            if item.get("finger") and item["finger"] not in FINGERS:
                problems.append(where + ": finger '" + item["finger"] + "' on " + control)


def trial_catalog(drafts, photo_drafts):
    """A copy of DB/Devices and DB/DevicePhotos with the drafts merged in (the engine reads it via JOYPRO_DEVICE_KB)."""
    root = tempfile.mkdtemp(prefix="joypro-catalog-")
    shutil.copytree(DEVICES, os.path.join(root, "Devices"))
    shutil.copytree(os.path.join(DB, "DevicePhotos"), os.path.join(root, "DevicePhotos"))
    for index, draft in enumerate(drafts):
        target = os.path.join(root, "Devices", draft.get("file") or ("draft-%d.json" % index))
        if os.path.isfile(target):
            data = load(target)
            data["devices"].extend(draft["devices"])
            data["officialHosts"] = sorted(set(data.get("officialHosts", [])) | set(draft.get("officialHosts", [])))
        else:
            data = {"vendor": draft.get("vendor", ""), "officialHosts": draft.get("officialHosts", []), "devices": draft["devices"]}
        with open(target, "w", encoding="utf-8") as handle:
            json.dump(data, handle, indent=1, ensure_ascii=False)
    for index, photos in enumerate(photo_drafts):
        with open(os.path.join(root, "DevicePhotos", "zz-draft-%d.json" % index), "w", encoding="utf-8") as handle:
            json.dump(photos, handle, indent=1, ensure_ascii=False)
    return root


def cmd_check(args):
    known = catalog()
    drafts, photo_drafts, problems = [], [], []
    hosts_by_file = {name: set(load(os.path.join(DEVICES, name)).get("officialHosts", [])) for name in catalog_files()}
    new_ids = {}
    for path in args.drafts:
        data = load(path)
        if "photos" in data:
            photo_drafts.append(data)
            continue
        if "fixes" in data:
            for fix in data["fixes"]:
                if fix.get("model") not in known:
                    problems.append(path + ": fix for unknown model " + str(fix.get("model")))
                if not fix.get("source"):
                    problems.append(path + ": fix for " + str(fix.get("model")) + " without a source")
            continue
        if not data.get("file"):
            problems.append(path + ": no 'file' (the vendor file the devices go into)")
        hosts = set(data.get("officialHosts", [])) | hosts_by_file.get(data.get("file", ""), set())
        for device in data.get("devices", []):
            check_device(device, problems, set(known), hosts)
            if device.get("id") in new_ids:
                problems.append("device '" + device["id"] + "' in two drafts")
            new_ids[device.get("id")] = device
        drafts.append(data)
    all_ids = set(known) | set(new_ids)
    for device in new_ids.values():
        for field in ("base",):
            if device.get(field) and device[field] not in all_ids:
                problems.append("device '" + device["id"] + "': base '" + device[field] + "' unknown")
        for other in device.get("fitsBases", []):
            if other not in all_ids:
                problems.append("device '" + device["id"] + "': fitsBases '" + other + "' unknown")
    for photos in photo_drafts:
        for photo in photos.get("photos", []):
            if photo.get("model") not in all_ids:
                problems.append("photo for unknown model " + str(photo.get("model")))
            if not VIEWS.match(photo.get("view", "")):
                problems.append("photo of " + str(photo.get("model")) + ": view '" + str(photo.get("view")) + "'")
    root = trial_catalog(drafts, photo_drafts)
    try:
        code, out = run_engine(os.path.join(root, "Devices"), "devices", "reference", "photos", "--coverage", "--text")
        for line in out.splitlines():
            if "not an official host" in line or "unknown model" in line or "could not be read" in line:
                problems.append("engine: " + line.strip())
        if new_ids:
            for did in sorted(new_ids):
                code, out = run_engine(os.path.join(root, "Devices"), "devices", "reference", "check", did, "--text")
                for line in out.splitlines():
                    if line.strip().startswith("- ") and did in line:
                        problems.append("engine: " + line.strip()[2:])
                # JoyPro's drawing of the model gets a clean callout sheet (the engine smoke checks every model).
                code, out = run_engine(os.path.join(root, "Devices"), "layout", "callouts", "--check-all", did, "--text")
                if code != 0:
                    problems.append("engine callouts " + did + ": " + " | ".join(l.strip() for l in out.splitlines() if l.strip())[:400])
        code, out = run_engine(os.path.join(root, "Devices"), "devices", "reference", "photos", "--coverage", "--text")
        print(out.strip())
    finally:
        shutil.rmtree(root, ignore_errors=True)
    print("Drafts: {} device(s), {} photo reference(s)".format(len(new_ids), sum(len(p.get("photos", [])) for p in photo_drafts)))
    print("Problems: {}".format(len(problems)))
    for problem in problems:
        print("  - " + problem)
    sys.exit(1 if problems else 0)


def insert_devices(path, devices, hosts):
    """Appends devices to a vendor file without reformatting what is there."""
    if not os.path.isfile(path):
        data = {"vendor": devices[0].get("vendor", ""), "officialHosts": sorted(hosts), "devices": devices}
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(data, indent=1, ensure_ascii=False) + "\n")
        return
    with open(path, encoding="utf-8-sig") as handle:
        text = handle.read().replace("\r\n", "\n")
    data = json.loads(text)
    missing = sorted(set(hosts) - set(data.get("officialHosts", [])))
    match = re.search(r'\n( +)"devices": \[\n( +)\{', text)
    if not match:
        fail(path + ": cannot find the devices array")
    indent = len(match.group(2)) - len(match.group(1))
    close = text.rstrip().rfind("]")
    head = text[:close].rstrip()
    block = ",\n".join("\n".join(match.group(2) + line for line in json.dumps(d, indent=indent, ensure_ascii=False).split("\n")) for d in devices)
    text = head + ",\n" + block + "\n" + match.group(1) + text[close:].rstrip() + "\n"
    if missing:
        hosts_match = re.search(r'"officialHosts": \[(.*?)\]', text, re.S)
        current = json.loads("[" + hosts_match.group(1) + "]")
        inner = hosts_match.group(1)
        pad = re.search(r"\n( +)\"", inner)
        item_indent = pad.group(1) if pad else match.group(2)
        new_inner = ",\n".join(item_indent + json.dumps(h) for h in current + missing)
        text = text[:hosts_match.start(1)] + "\n" + new_inner + "\n" + match.group(1) + text[hosts_match.end(1):]
    json.loads(text)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def cmd_merge(args):
    for path in args.drafts:
        data = load(path)
        if not data.get("devices"):
            continue
        target = os.path.join(DEVICES, data["file"])
        if not os.path.isfile(target):
            for device in data["devices"]:
                device.setdefault("vendor", data.get("vendor", ""))
        insert_devices(target, data["devices"], data.get("officialHosts", []))
        print("{}: {} device(s) -> {}".format(path, len(data["devices"]), os.path.relpath(target, REPO)))


def cmd_photos(args):
    known = catalog()
    # Photos whose content hash is on record (curated positions belong to them) keep their entry as it is.
    recorded = set()
    folder = os.path.join(DB, "DevicePhotos")
    for name in os.listdir(folder):
        if name.endswith(".json"):
            for item in load(os.path.join(folder, name)).get("photos", []):
                if item.get("sha256"):
                    recorded.add((item.get("model"), item.get("url")))
    added = 0
    for path in args.drafts:
        for photo in load(path).get("photos", []):
            if photo["model"] not in known:
                print("skipped (unknown model): " + photo["model"])
                continue
            if (photo["model"], photo["url"]) in recorded and not photo.get("sha256"):
                continue
            command = ["devices", "reference", "photo-add", "--model", photo["model"], "--url", photo["url"], "--view", photo.get("view", "main")]
            for key in ("page", "confidence", "sha256"):
                if photo.get(key):
                    command += ["--" + key, photo[key]]
            if photo.get("shows"):
                command += ["--shows", ",".join(photo["shows"])]
            code, out = run_engine(DEVICES, *command)
            if code != 0:
                print("refused: " + photo["model"] + " " + photo["url"] + ": " + out.strip()[:300])
            else:
                added += 1
    print("Photo references added or updated: {}".format(added))


def cmd_fix(args):
    for path in args.drafts:
        for fix in load(path).get("fixes", []):
            known = catalog()
            if fix["model"] not in known:
                print("skipped (unknown model): " + fix["model"])
                continue
            name, _, _ = known[fix["model"]]
            file = os.path.join(DEVICES, name)
            with open(file, encoding="utf-8-sig") as handle:
                text = handle.read().replace("\r\n", "\n")
            data = json.loads(text)
            device = next(d for d in data["devices"] if d["id"] == fix["model"])
            target = device
            if fix.get("control"):
                target = next((c for group in ("axes", "controls") for c in device.get(group, []) if c.get("control") == fix["control"]), None)
                if target is None:
                    print("skipped (no control {} on {})".format(fix["control"], fix["model"]))
                    continue
            # Field by field on the text, so the file keeps its layout.
            start = text.find('"id": ' + json.dumps(fix["model"]))
            for key, value in fix["set"].items():
                old = target.get(key)
                if old is None:
                    print("skipped (no field {} to change on {}{})".format(key, fix["model"], " " + fix.get("control", "") if fix.get("control") else ""))
                    continue
                anchor = start
                if fix.get("control"):
                    anchor = text.find('"control": ' + json.dumps(fix["control"]), start)
                    block_start = text.rfind("{", 0, anchor)
                    anchor = block_start
                old_text = '"' + key + '": ' + json.dumps(old, ensure_ascii=False)
                pos = text.find(old_text, anchor)
                if pos < 0:
                    print("skipped (field text not found) " + fix["model"] + " " + key)
                    continue
                text = text[:pos] + '"' + key + '": ' + json.dumps(value, ensure_ascii=False) + text[pos + len(old_text):]
                target[key] = value
                print("{}{}: {} = {}".format(fix["model"], " " + fix["control"] if fix.get("control") else "", key, value))
            json.loads(text)
            with open(file, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(text)


def cmd_ids(args):
    for did, (name, data, device) in sorted(catalog().items()):
        line = "{} | {} | {} | {} | {} | photo={}".format(did, name, device.get("kind"), device.get("name"),
                                                          ",".join(device.get("identify", {}).get("vidPids", [])), "y" if device.get("image") or device.get("photos") else "n")
        if not args.query or args.query.lower() in line.lower():
            print(line)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("check", "merge", "photos", "fix"):
        p = sub.add_parser(name)
        p.add_argument("drafts", nargs="+")
    p = sub.add_parser("ids")
    p.add_argument("query", nargs="?")
    args = parser.parse_args()
    {"check": cmd_check, "merge": cmd_merge, "photos": cmd_photos, "fix": cmd_fix, "ids": cmd_ids}[args.command](args)


if __name__ == "__main__":
    main()
