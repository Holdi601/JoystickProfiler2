"""After a model's catalog entry changed its parts: drops what the model's curated photo sets name but the model no longer
has (markers, candidates, unplaced, parts) and the callout sheet when one of its tables names such a part (arrange it
again with tools/device_positions.py sheet <model>).

    python tools/device_drawings_prune.py <model> [<model> ...]
"""
import json
import os
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DB = os.path.join(REPO, "EngineOnly_Runtime", "DB")
LAYOUTS = os.path.join(DB, "DeviceLayouts")


def engine():
    for c in (os.environ.get("JOYPRO_ENGINE"), os.path.join(REPO, "engine", "bin", "x64", "Debug", "engine.exe")):
        if c and os.path.isfile(c):
            return c
    sys.exit("engine.exe not found")


def parts_of(model):
    env = dict(os.environ)
    env["JOYPRO_DEVICE_KB"] = os.path.join(DB, "Devices")
    out = subprocess.run([engine(), "devices", "reference", "show", model], capture_output=True, text=True, encoding="utf-8", env=env).stdout
    data = json.loads(out[out.index("{"):])
    return {m["Id"] for m in data["Markers"]}


def main():
    for model in sys.argv[1:]:
        parts = parts_of(model)
        for name in sorted(os.listdir(LAYOUTS)):
            if not name.startswith(model + ".") or not name.endswith(".json"):
                continue
            path = os.path.join(LAYOUTS, name)
            with open(path, encoding="utf-8-sig") as handle:
                data = json.load(handle)
            if data.get("model") != model or (data.get("photo") or {}).get("kind") == "lineart":
                continue
            dropped = []
            for key in ("markers", "candidates", "unplaced", "parts"):
                block = data.get(key) or {}
                for part in [p for p in block if p not in parts]:
                    block.pop(part)
                    dropped.append(key + ":" + part)
            sheet = data.get("callouts")
            if sheet and any(p not in parts or p not in data.get("markers", {}) for t in sheet.get("tables", []) for p in t.get("parts", []) if t.get("spot")):
                data.pop("callouts")
                dropped.append("callouts")
            if dropped:
                with open(path, "w", encoding="utf-8", newline="\n") as handle:
                    json.dump(data, handle, ensure_ascii=False, indent=1)
                    handle.write("\n")
                print("%s: dropped %s" % (name, ", ".join(dropped)))


if __name__ == "__main__":
    main()
