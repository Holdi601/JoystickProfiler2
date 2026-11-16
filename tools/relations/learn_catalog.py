"""Feed a reworked profile into the function catalog (DB/Functions): Auto Group, relations suggest and the setup
planner then group a new user's actions the same way (the learned rules are listed in docs/suggest-additions.md).

Input: two relation exports of the profile (engine --profile <p> relations export <file.json>), before and after the
rework. Every action that is switched on after and was not before becomes a catalog row of its relation's slot:
<Game>.tsv "plane  slot  action  new  hotas  frequency  control". A relation that is no slot yet becomes one in
slots.json (id from its name, kind from its type and group). Rows the catalog has already are left alone.

Left out: aircraft Auto Group ignores (function-map.json autoGroupIgnore), and --skip relation|game|id-prefix entries
(decisions the owner still has to test in the game; relation|game|id-prefix[|plane]).

python tools/relations/learn_catalog.py --before old.json --after new.json --functions EngineOnly_Runtime/DB/Functions
    [--source "profile style 2026-11-04"] [--skip "NS430__*|MSFS2024|KEY_G1000_PFD"]... [--dry-run]
"""
import argparse
import collections
import fnmatch
import json
import os
import re


def load_items(path):
    data = json.load(open(path, encoding='utf-8-sig'))
    rels = {}
    for r in data['Relations']:
        on = set()
        for n in r['Nodes']:
            for plane, active in (n.get('Aircraft') or {}).items():
                if active:
                    on.add((n['Game'], n['Id'], plane))
        rels[r['Name']] = (r, on)
    return rels


def slug(name, taken):
    base = re.sub(r'[^a-z0-9]+', '_', name.lower()).strip('_') or 'slot'
    candidate = base
    n = 2
    while candidate in taken:
        candidate = base + '_' + str(n)
        n += 1
    return candidate


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--before', required=True)
    ap.add_argument('--after', required=True)
    ap.add_argument('--functions', required=True)
    ap.add_argument('--source', default='profile style')
    ap.add_argument('--skip', action='append', default=[], help='relation-pattern|game|id-prefix[|plane] ("*" = any)')
    ap.add_argument('--dry-run', action='store_true')
    a = ap.parse_args()

    folder = a.functions
    fmap = json.load(open(os.path.join(folder, 'function-map.json'), encoding='utf-8-sig'))
    ignore = set(p.lower() for p in fmap.get('autoGroupIgnore', []))
    slots_path = os.path.join(folder, 'slots.json')
    slots_raw = open(slots_path, encoding='utf-8-sig').read()
    slots = json.loads(slots_raw)
    by_name = {s['name'].lower(): s for s in slots}
    taken = set(s['id'] for s in slots)
    skips = [s.split('|') for s in a.skip]

    before = load_items(a.before)
    after = load_items(a.after)

    # The catalog rows there are: game -> set of (plane, slot, action) lower case.
    existing = collections.defaultdict(set)
    files = {}
    for f in os.listdir(folder):
        if f.endswith('.tsv') and not f.endswith('.rejected.tsv') and f != 'slot_controls.tsv':
            game = f[:-4]
            files[game] = os.path.join(folder, f)
            for line in open(files[game], encoding='utf-8-sig'):
                parts = line.rstrip('\r\n').split('\t')
                if len(parts) >= 4 and not line.startswith('#'):
                    existing[game].add((parts[0].lower(), parts[1].lower(), parts[2].lower()))

    new_slots = []
    new_rows = collections.defaultdict(list)
    skipped = collections.Counter()
    for name, (rel, on) in sorted(after.items()):
        added = on - (before[name][1] if name in before else set())
        if not added:
            continue
        slot = by_name.get(name.lower())
        if slot is None:
            groups = [g.lower() for g in (rel.get('Groups') or [])]
            axis = bool(rel.get('IsAxis'))
            kind = 'axis' if axis else 'lights' if 'lights' in groups else 'starcitizen' if name.startswith('SPACE_') else 'cockpit'
            view = name.lower().startswith(('look', 'view'))
            slot = {'id': slug(name, taken), 'name': name, 'kind': kind, 'axis': axis, 'basic': False, 'scope': 'generic',
                    'hotas': 'none' if view or axis else 'panel', 'frequency': 'occasional', 'placement': '', 'planes': 0}
            taken.add(slot['id'])
            by_name[name.lower()] = slot
            new_slots.append(slot)
        for game, action, plane in sorted(added):
            if plane.lower() in ignore:
                skipped['ignored aircraft'] += 1
                continue
            if any(fnmatch.fnmatch(name, s[0]) and (s[1] in ('*', game)) and action.startswith(s[2] if s[2] != '*' else '') and (len(s) < 4 or s[3] in ('*', plane)) for s in skips):
                skipped['to test in the game first'] += 1
                continue
            key = (plane.lower(), slot['id'].lower(), action.lower())
            if key in existing[game]:
                skipped['already in the catalog'] += 1
                continue
            existing[game].add(key)
            new_rows[game].append('\t'.join([plane, slot['id'], action, 'new', slot['hotas'], slot['frequency'], a.source]))

    for slot in new_slots:
        slot['planes'] = len(set(r.split('\t')[0] for rows in new_rows.values() for r in rows if r.split('\t')[1] == slot['id']))
    print('new slots: %d, new rows: %d (%s)' % (len(new_slots), sum(len(v) for v in new_rows.values()),
                                               ', '.join('%s %d' % (g, len(v)) for g, v in sorted(new_rows.items()))))
    print('left out: ' + ', '.join('%s %d' % kv for kv in sorted(skipped.items())))
    if a.dry_run:
        return
    if new_slots:
        nl = '\r\n' if '\r\n' in slots_raw else '\n'
        text = json.dumps(slots + new_slots, indent=1, ensure_ascii=False)
        open(slots_path, 'w', encoding='utf-8', newline='').write(text.replace('\n', nl) + (nl if slots_raw.endswith(('\n', '\r\n')) else ''))
    for game, rows in new_rows.items():
        path = files.get(game) or os.path.join(folder, game + '.tsv')
        if os.path.exists(path):
            raw = open(path, encoding='utf-8-sig', newline='').read()
            nl = '\r\n' if '\r\n' in raw else '\n'
            if raw and not raw.endswith(('\n', '\r\n')):
                raw += nl
            open(path, 'w', encoding='utf-8', newline='').write(raw + nl.join(rows) + nl)
        else:
            nl = '\r\n'
            header = '# plane\tslot\taction\tstatus\thotas\tfrequency\tcontrol'
            open(path, 'w', encoding='utf-8', newline='').write(header + nl + nl.join(rows) + nl)


if __name__ == '__main__':
    main()
