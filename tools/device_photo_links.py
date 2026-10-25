"""Link health of the device reference photos (maintainer tool, docs/device-reference.md "Dead links").

JoyPro ships only photo addresses (DB/DevicePhotos) with a SHA-256 each; the user's PC downloads them and falls back to
the Wayback Machine snapshot when an address dies or its content changes. This tool keeps that working:

  python tools/device_photo_links.py check [query] [--out <report.json>] [--limit n]
      every photo: alive (same content), changed (other content), dead (no picture); for changed and dead ones whether
      the snapshot (recorded "archive", else the newest capture) still has the recorded content.
  python tools/device_photo_links.py snapshots [query] [--record] [--save-page-now] [--limit n]
      finds a Wayback snapshot with the recorded content for every photo without one (the CDX index of the address);
      --record writes it into DB/DevicePhotos ("archive"); --save-page-now asks the public Save Page Now to capture
      photos the archive does not have yet (slowly, one request every 15 s; the archive decides what it may keep).
  python tools/device_photo_links.py recover [query] [--backup <private archive>] [--apply]
      for every photo whose address AND snapshot fail: the copy in the private archive (tools/backup_device_photos.ps1,
      by SHA-256), a new public address on the photo's source page with the same content (--apply writes it into the
      database), else the Save Page Now request to prepare for the page. The archived files themselves are never
      copied anywhere and never published.

Standard library only; one request at a time per site; the same request as JoyPro's own download (User-Agent JoyPro2,
no Accept header) so the bytes and the hash match. The report goes to --out (default: the system temp folder).
"""
import argparse
import glob
import hashlib
import json
import os
import re
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DB = os.path.join(REPO, 'EngineOnly_Runtime', 'DB')
PHOTOS = os.path.join(DB, 'DevicePhotos')
UA = 'JoyPro2'
_last = {}


def get(url, timeout=40):
    """(bytes or None, status or error text). Spaced per site, one retry after 429/503."""
    host = urllib.parse.urlparse(url).hostname or ''
    for attempt in range(3):
        wait = _last.get(host, 0) + (1.5 if 'archive.org' in host else 0.4) - time.time()
        if wait > 0:
            time.sleep(wait)
        _last[host] = time.time()
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': UA}), timeout=timeout) as r:
                return r.read(), r.status
        except urllib.error.HTTPError as e:
            if e.code in (429, 503) and attempt < 2:
                time.sleep(10 * (attempt + 1))
                continue
            return None, e.code
        except Exception as e:  # offline, DNS, TLS, timeout
            return None, str(e)
    return None, 'busy'


def is_picture(data):
    return bool(data) and (data[:2] == b'\xff\xd8' or data[:4] == b'\x89PNG' or data[:3] == b'GIF' or (data[:4] == b'RIFF' and data[8:12] == b'WEBP') or data[:2] == b'BM')


def sha(data):
    return hashlib.sha256(data).hexdigest()


def archive_url(photo):
    a = photo.get('archive', '')
    return a if a.startswith('https://web.archive.org/web/') else 'https://web.archive.org/web/2id_/' + photo['url']


def load(query=''):
    files = {}
    for f in sorted(glob.glob(os.path.join(PHOTOS, '*.json'))):
        if os.path.basename(f).startswith('_'):
            continue
        files[f] = json.load(open(f, encoding='utf-8-sig'))
    q = query.lower()
    rows = [(f, p) for f, d in files.items() for p in d['photos'] if not q or q in p['model'].lower() or q in p['url'].lower()]
    return files, rows


def dump(v, ind=0):
    pad = '  ' * ind
    if isinstance(v, dict):
        return '{\n' + ',\n'.join(pad + '  ' + json.dumps(k, ensure_ascii=False) + ': ' + dump(x, ind + 1) for k, x in v.items()) + '\n' + pad + '}'
    if isinstance(v, list):
        if all(not isinstance(x, (dict, list)) for x in v):
            return '[' + ', '.join(json.dumps(x, ensure_ascii=False) for x in v) + ']'
        return '[\n' + ',\n'.join(pad + '  ' + dump(x, ind + 1) for x in v) + '\n' + pad + ']'
    return json.dumps(v, ensure_ascii=False)


def save(files):
    for f, data in files.items():
        open(f, 'w', encoding='utf-8', newline='\n').write(dump(data) + '\n')


def probe(photo):
    """The state of one photo: alive / changed / dead, and the fallback's state."""
    recorded = (photo.get('sha256') or '').lower()
    row = {'model': photo['model'], 'url': photo['url'], 'view': photo.get('view', '')}
    if photo['url'].startswith('https://'):
        data, status = get(photo['url'])
        if data is not None and is_picture(data):
            row['state'] = 'alive' if not recorded or sha(data) == recorded else 'changed'
            if not recorded:
                row['sha256'] = sha(data)
        else:
            row['state'] = 'dead'
            row['status'] = status
    else:
        row['state'] = 'archive-only'
    if row['state'] != 'alive':
        a = archive_url(photo)
        data, status = get(a)
        row['archive'] = a
        if data is not None and is_picture(data) and (not recorded or sha(data) == recorded):
            row['fallback'] = 'ok'
        else:
            row['fallback'] = 'missing' if data is None else 'other content'
    return row


def cdx_snapshot(url, recorded):
    """A Wayback capture of the address whose bytes have the recorded SHA-256 (newest first)."""
    q = 'https://web.archive.org/cdx/search/cdx?' + urllib.parse.urlencode({'url': url, 'output': 'json', 'filter': 'statuscode:200', 'fl': 'timestamp,digest', 'collapse': 'digest'})
    data, _ = get(q)
    if not data:
        return None
    try:
        rows = json.loads(data.decode('utf-8'))[1:]
    except Exception:
        return None
    for ts, _digest in reversed(rows):
        snap = 'https://web.archive.org/web/%sid_/%s' % (ts, url)
        body, _ = get(snap)
        if body is not None and is_picture(body) and (not recorded or sha(body) == recorded):
            return snap
    return None


def save_page_now(url):
    data, status = get('https://web.archive.org/save/' + url, timeout=120)
    time.sleep(15)
    return status


def default_backup():
    """The JoyPro2-private/device-photos folder next to the repository or one of its parent folders."""
    folder = REPO
    while True:
        candidate = os.path.join(folder, 'JoyPro2-private', 'device-photos')
        if os.path.isdir(candidate):
            return candidate
        parent = os.path.dirname(folder)
        if parent == folder:
            return ''
        folder = parent


def backup_index(folder):
    m = os.path.join(folder, 'manifest.json')
    if not os.path.isfile(m):
        return {}
    out = {}
    for e in json.load(open(m, encoding='utf-8-sig')):
        if e.get('sha256') and e.get('file'):
            out[e['sha256']] = os.path.join(folder, e['file'])
    return out


def page_candidates(page):
    """Image addresses on a page (largest sizes first: WordPress size suffixes removed)."""
    data, _ = get(page)
    if not data:
        return []
    html = data.decode('utf-8', 'replace')
    found = []
    for u in re.findall(r'https?://[^"\'\s()<>]+?\.(?:jpe?g|png|webp)', html, re.I):
        u = u.replace('\\/', '/')
        for v in (re.sub(r'-\d{2,4}x\d{2,4}(\.(?:jpe?g|png|webp))$', r'\1', u, flags=re.I), u):
            if v not in found:
                found.append(v)
    return found


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('command', choices=['check', 'snapshots', 'recover'])
    ap.add_argument('query', nargs='?', default='')
    ap.add_argument('--out', default=os.path.join(tempfile.gettempdir(), 'joypro-photo-links.json'))
    ap.add_argument('--limit', type=int, default=0)
    ap.add_argument('--record', action='store_true')
    ap.add_argument('--save-page-now', action='store_true')
    ap.add_argument('--backup', default=os.environ.get('JOYPRO_PHOTO_BACKUP') or default_backup())
    ap.add_argument('--apply', action='store_true')
    a = ap.parse_args()
    files, rows = load(a.query)
    if a.limit:
        rows = rows[:a.limit]

    if a.command == 'check':
        report = []
        for i, (_, p) in enumerate(rows, 1):
            r = probe(p)
            report.append(r)
            if r['state'] != 'alive':
                print('%-14s %-10s %s %s' % (r['state'], r.get('fallback', ''), p['model'], p['url']))
            if i % 100 == 0:
                print('... %d of %d' % (i, len(rows)), file=sys.stderr)
        counts = {}
        for r in report:
            k = r['state'] + ('' if r['state'] == 'alive' else ' / fallback ' + r.get('fallback', '?'))
            counts[k] = counts.get(k, 0) + 1
        json.dump({'checked': len(report), 'counts': counts, 'photos': report}, open(a.out, 'w', encoding='utf-8'), indent=1)
        print(json.dumps(counts, indent=1))
        print('report:', a.out)
        return 0 if all(r['state'] == 'alive' or r.get('fallback') == 'ok' for r in report) else 1

    if a.command == 'snapshots':
        found = asked = 0
        for f, p in rows:
            if p.get('archive'):
                continue
            snap = cdx_snapshot(p['url'], (p.get('sha256') or '').lower()) if p['url'].startswith('https://') else None
            if snap:
                found += 1
                print('snapshot', p['model'], snap)
                if a.record:
                    p['archive'] = snap
            elif a.save_page_now and p['url'].startswith('https://'):
                asked += 1
                print('save page now', p['model'], p['url'], save_page_now(p['url']))
        if a.record:
            save(files)
        print('snapshots found: %d, Save Page Now requests: %d' % (found, asked))
        return 0

    if a.command == 'recover':
        backup = backup_index(a.backup)
        changed = False
        for f, p in rows:
            r = probe(p)
            if r['state'] == 'alive' or r.get('fallback') == 'ok':
                continue
            recorded = (p.get('sha256') or '').lower()
            local = backup.get(recorded)
            print('%s %s (%s, snapshot %s)' % (p['model'], p['url'], r['state'], r.get('fallback')))
            print('    private archive:', local if local and os.path.isfile(local) else 'no copy')
            new = None
            if recorded and p.get('page', '').startswith('http'):
                for u in page_candidates(p['page']):
                    data, _ = get(u)
                    if data is not None and sha(data) == recorded:
                        new = u
                        break
            if new:
                print('    same photo now at:', new)
                if a.apply:
                    p['url'] = new
                    p.pop('archive', None)
                    changed = True
            else:
                print('    no new address found; prepare: https://web.archive.org/save/' + (p.get('page') or p['url']))
        if changed:
            save(files)
        return 0
    return 2


if __name__ == '__main__':
    sys.exit(main())
