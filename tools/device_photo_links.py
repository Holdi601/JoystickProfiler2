"""Link health of the device reference photos (maintainer tool, docs/device-reference.md "Dead links").

JoyPro ships only photo addresses (DB/DevicePhotos) with a SHA-256 each; the user's PC downloads them and falls back to
the Wayback Machine snapshot when an address dies or its content changes. This tool keeps that working:

  python tools/device_photo_links.py check [query] [--report <check.json>] [--out <report.json>] [--limit n]
      every photo: alive (same content), changed (other content), dead (no picture); for changed and dead ones whether
      the snapshot (recorded "archive", else the newest capture) still has the recorded content. --report checks only
      the failing photos of an earlier report again (the web archive drops requests under steady load).
  python tools/device_photo_links.py snapshots [query] [--report <check.json>] [--record] [--save-page-now] [--limit n]
      finds a Wayback snapshot with the recorded content for every photo without one (the CDX index of the address,
      then the archive's availability API); --report takes only the photos a check found not alive, also those whose
      recorded snapshot fails; --record writes it into DB/DevicePhotos ("archive"); --save-page-now asks the public
      Save Page Now to capture photos the archive does not have yet (slowly, one request every 15 s; the archive
      decides what it may keep). A photo without a recorded hash is matched against its copy in the private archive
      when there is one, and --record then records that hash too.
  python tools/device_photo_links.py snapshots --all [query] [--record] [--save-page-now] [--hours h] [--spn-gap s]
                                     [--status <file.json>]
      the whole list at once: the CDX index once per folder, matched by the digest of the private archive's copy (no
      capture downloaded), recorded snapshots checked too; Save Page Now first for the photos without any capture,
      then for those whose captures have other bytes, until --hours are over (after "too many requests" it waits
      5, 10, 20 minutes, then stops); --record saves every 20 snapshots, so a stopped run keeps what it found.
  python tools/device_photo_links.py recover [query] [--report <check.json>] [--backup <private archive>] [--apply]
      for every photo whose address AND snapshot fail: the copy in the private archive (tools/backup_device_photos.ps1,
      by SHA-256), a new public address with the same content on the photo's source page (the maker's hosts only; the
      photo's own file name first, Shopify's "?v=" version of it too; at most --tries addresses per photo; --apply
      writes it into the database and into the curated position set of that photo), else the Save Page Now request to
      prepare for the page. The archived files themselves are never copied anywhere and never published. A hash is
      never changed: a photo the maker re-encoded keeps its hash (curated positions and drawings are keyed by it).
  python tools/device_photo_links.py same-pixels [query] [--report <check.json>] [--backup <private archive>] [--apply]
      for every photo whose address sends other bytes than on record: decodes the new file and the recorded one (the
      private archive's copy, else the recorded or newest Wayback snapshot with the recorded SHA-256) and compares the
      pixels; exactly the same picture (size and every pixel) = a shop re-encoded it, and --apply adds the new file's
      SHA-256 to the photo's "sha256Alt" (JoyPro accepts it as the photo on record; "sha256" stays its identity, so
      curated positions and drawings keep working). Other pixels: the maker changed the photo, nothing is recorded.
      Needs Pillow (pip install pillow) for the decoding; the other commands use the standard library only.

Standard library only (same-pixels: Pillow); one request at a time per site; the same request as JoyPro's own download (User-Agent JoyPro2,
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
    """(bytes or None, status or error text). Spaced per site, up to two retries after 429/503; the web archive, which
    drops connections under steady load, also after 502/504 and a timeout or reset (a longer pause each time)."""
    host = urllib.parse.urlparse(url).hostname or ''
    archive = 'archive.org' in host
    for attempt in range(3):
        wait = _last.get(host, 0) + (1.5 if archive else 0.4) - time.time()
        if wait > 0:
            time.sleep(wait)
        _last[host] = time.time()
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': UA}), timeout=timeout) as r:
                return r.read(), r.status
        except urllib.error.HTTPError as e:
            if (e.code in (429, 503) or (archive and e.code in (502, 504))) and attempt < 2:
                time.sleep(10 * (attempt + 1))
                continue
            return None, e.code
        except Exception as e:  # offline, DNS, TLS, timeout
            if archive and attempt < 2:
                time.sleep(10 * (attempt + 1))
                continue
            return None, str(e)
    return None, 'busy'


def is_picture(data):
    return bool(data) and (data[:2] == b'\xff\xd8' or data[:4] == b'\x89PNG' or data[:3] == b'GIF' or (data[:4] == b'RIFF' and data[8:12] == b'WEBP') or data[:2] == b'BM')


def sha(data):
    return hashlib.sha256(data).hexdigest()


def accepted(photo, digest):
    """Whether bytes with this SHA-256 are the photo on record: its sha256 or one of its sha256Alt (the same pixels,
    re-encoded by a shop); a photo without a recorded hash accepts anything."""
    recorded = (photo.get('sha256') or '').lower()
    return not recorded or digest == recorded or digest in [a.lower() for a in photo.get('sha256Alt') or []]


def pixels(data):
    """(size, RGBA bytes) of a picture file (Pillow)."""
    import io
    from PIL import Image
    with Image.open(io.BytesIO(data)) as im:
        im.load()
        return im.size, im.convert('RGBA').tobytes()


def same_pixels(a, b):
    """Whether two picture files decode to exactly the same picture (size and every pixel)."""
    try:
        return pixels(a) == pixels(b)
    except Exception:
        return False


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
            row['state'] = 'alive' if accepted(photo, sha(data)) else 'changed'
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
        if data is not None and is_picture(data) and accepted(photo, sha(data)):
            row['fallback'] = 'ok'
        else:
            row['fallback'] = 'missing' if data is None else 'other content'
            row['archive_status'] = status
    return row


def cdx_digest(file):
    """The CDX index's digest of a file's bytes (base32 SHA-1), for a private copy: the matching captures are tried
    first and the others not at all."""
    import base64
    return base64.b32encode(hashlib.sha1(open(file, 'rb').read()).digest()).decode('ascii')


def cdx_snapshot(url, recorded, digest=''):
    """A Wayback capture of the address whose bytes have the recorded SHA-256 (newest first; with the digest of the
    private copy, only the captures with that digest)."""
    q = 'https://web.archive.org/cdx/search/cdx?' + urllib.parse.urlencode({'url': url, 'output': 'json', 'filter': 'statuscode:200', 'fl': 'timestamp,digest', 'collapse': 'digest'})
    data, _ = get(q)
    if not data:
        return None
    try:
        rows = json.loads(data.decode('utf-8'))[1:]
    except Exception:
        return None
    if digest:
        rows = [r for r in rows if r[1] == digest]
    for ts, _digest in reversed(rows):
        snap = 'https://web.archive.org/web/%sid_/%s' % (ts, url)
        body, _ = get(snap)
        if body is not None and is_picture(body) and (not recorded or sha(body) == recorded):
            return snap
    return None


def available_snapshot(url, recorded):
    """The archive's availability API (archive.org/wayback/available): its closest capture of the address, for when
    the CDX index is busy or has nothing; kept only with the recorded content."""
    data, _ = get('https://archive.org/wayback/available?' + urllib.parse.urlencode({'url': url}))
    try:
        closest = json.loads(data.decode('utf-8'))['archived_snapshots']['closest']
    except Exception:
        return None
    ts = str(closest.get('timestamp', ''))
    if not closest.get('available') or not re.fullmatch(r'\d{14}', ts) or not str(closest.get('status', '200')).startswith('2'):
        return None
    snap = 'https://web.archive.org/web/%sid_/%s' % (ts, url)
    body, _ = get(snap)
    if body is not None and is_picture(body) and (not recorded or sha(body) == recorded):
        return snap
    return None


def find_snapshot(url, recorded, copy=''):
    digest = cdx_digest(copy) if copy and os.path.isfile(copy) else ''
    return cdx_snapshot(url, recorded, digest) or available_snapshot(url, recorded)


def protected_hashes():
    """Photo hashes JoyPro's own data is keyed by: the curated position sets (DB/DeviceLayouts) and the drawings made
    from a photo (DB/DeviceDrawings, by the opaque key of the photo's hash). Such a hash is never re-recorded."""
    out = set()
    for f in glob.glob(os.path.join(DB, 'DeviceLayouts', '*.json')):
        try:
            h = (json.load(open(f, encoding='utf-8-sig')).get('photo') or {}).get('sha256', '')
        except Exception:
            continue
        if h:
            out.add(h.lower())
    keys = set()
    for f in glob.glob(os.path.join(DB, 'DeviceDrawings', '*.json')):
        try:
            k = json.load(open(f, encoding='utf-8-sig')).get('key', '')
        except Exception:
            continue
        if k:
            keys.add(k.lower())
    _, rows = load()
    for _, p in rows:
        h = (p.get('sha256') or '').lower()
        if h and hashlib.sha256(('joypro-drawing:' + h).encode('ascii')).hexdigest()[:16] in keys:
            out.add(h)
    return out


def report_rows(rows, path):
    """Only the photos a check report lists as not alive (dead, changed, archive-only), by model and address; a photo
    whose recorded snapshot works is left out."""
    report = {(r['model'], r['url']): r for r in json.load(open(path, encoding='utf-8'))['photos'] if r['state'] != 'alive'}
    return [(f, p) for f, p in rows if (p['model'], p['url']) in report
            and not (p.get('archive') and report[(p['model'], p['url'])].get('fallback') == 'ok')]


def save_page_now(url):
    data, status = get('https://web.archive.org/save/' + url, timeout=120)
    time.sleep(15)
    return status


# ------------------------------------------------------------------ the whole list (snapshots --all)

def url_key(url):
    """An address as the CDX index matches it: without scheme, "www." and default port; path and query as they are."""
    u = urllib.parse.urlsplit(url.strip())
    host = (u.hostname or '').lower()
    if host.startswith('www.'):
        host = host[4:]
    return host + (u.path or '/') + ('?' + u.query if u.query else '')


def folder_of(url):
    return url.split('?', 1)[0].rsplit('/', 1)[0] + '/'


def cdx_rows(query, cache_dir, timeout=150):
    """The CDX rows (original, timestamp, digest) of a query (successful captures only), cached for a day in cache_dir;
    None when the index did not answer."""
    params = dict(query, output='json', filter='statuscode:200', fl='original,timestamp,digest')
    key = hashlib.sha1(json.dumps(params, sort_keys=True).encode('utf-8')).hexdigest()
    cached = os.path.join(cache_dir, key + '.json')
    if os.path.isfile(cached) and time.time() - os.path.getmtime(cached) < 86400:
        return json.load(open(cached, encoding='utf-8'))
    data, status = get('https://web.archive.org/cdx/search/cdx?' + urllib.parse.urlencode(params), timeout=timeout)
    if data is None:
        return None
    try:
        rows = json.loads(data.decode('utf-8') or '[]')[1:]
    except Exception:
        return None
    json.dump(rows, open(cached, 'w', encoding='utf-8'))
    return rows


def snapshot_of(ts, original):
    return 'https://web.archive.org/web/%sid_/%s' % (ts, original)


def snapshot_time(archive):
    """The capture time of a pinned snapshot (14 digits); '' for none or an unpinned one ("2id_", "2012id_": the
    archive picks the nearest capture, which may have other bytes), so that one gets pinned."""
    m = re.match(r'https://web\.archive\.org/web/(\d{14})(?:id|im)_/', archive or '')
    return m.group(1) if m else ''


def snapshot_address(archive):
    """The address a snapshot is of (after "id_/" or "im_/")."""
    m = re.match(r'https://web\.archive\.org/web/\d*(?:id|im)_/(.+)$', archive or '')
    return m.group(1) if m else ''


def spn_capture(url, gap):
    """Save Page Now (the public, anonymous form): (snapshot address or '', status). Waits at least `gap` seconds
    after the call; after "too many requests" it waits 5, 10, then 20 minutes and gives up ('busy')."""
    for wait in (300, 600, 1200, None):
        started = time.time()
        try:
            req = urllib.request.Request('https://web.archive.org/save/' + url, headers={'User-Agent': UA})
            with urllib.request.urlopen(req, timeout=180) as r:
                final = r.geturl()
                location = r.headers.get('Content-Location', '') or ''
            m = re.search(r'/web/(\d{14})', location) or re.search(r'/web/(\d{14})', final)
            snap = snapshot_of(m.group(1), url) if m else ''
            status = 200
        except urllib.error.HTTPError as e:
            snap, status = '', e.code
        except Exception as e:
            snap, status = '', str(e)
        rest = gap - (time.time() - started)
        if rest > 0:
            time.sleep(rest)
        if status == 429 and wait:
            print('    Save Page Now: too many requests, waiting %d min' % (wait // 60), flush=True)
            time.sleep(wait)
            continue
        return snap, ('busy' if status == 429 else status)
    return '', 'busy'


def snapshots_all(a, files, rows):
    """Every photo of the list gets a Wayback snapshot with its recorded bytes (docs/device-reference.md "Dead links"):
    the CDX index once per folder (a folder with one or two photos: per address), matched by the CDX digest (SHA-1)
    of the private archive's copy with the recorded SHA-256, so the captures need not be downloaded; a photo without a
    private copy compares the downloaded captures with its hash (newest first, at most two). A recorded snapshot is
    kept when the index lists it with the same digest (or when there is no digest to compare). Photos without any
    capture are asked from Save Page Now first, then those whose captures have other bytes (--save-page-now; one
    request every --spn-gap seconds, until --hours are over). --record writes "archive" (and a missing hash from the
    private copy) every 20 snapshots and at the end; --status writes the per-photo result."""
    started = time.time()
    deadline = started + a.hours * 3600 if a.hours else 0
    cache_dir = os.path.join(tempfile.gettempdir(), 'joypro-cdx-cache')
    os.makedirs(cache_dir, exist_ok=True)
    copies = backup_hashes(a.backup)
    backup = backup_index(a.backup)
    states = {}
    pending = [0]

    def flush(force=False):
        if a.record and (force or pending[0] >= 20):
            save(files)
            pending[0] = 0

    def record(p, snap, expected):
        if snap and snap != p.get('archive'):
            print('snapshot', p['model'], snap, flush=True)
            if a.record:
                p['archive'] = snap
                if not (p.get('sha256') or '') and expected:
                    p['sha256'] = expected
                pending[0] += 1
                flush()

    # Which bytes are the photo, and their CDX digest when the private archive has them.
    info = []
    for f, p in rows:
        recorded = (p.get('sha256') or '').lower()
        expected = recorded or copies.get((p['model'], p['url']), '')
        copy = backup.get(expected, '')
        digest = cdx_digest(copy) if copy and os.path.isfile(copy) and sha(open(copy, 'rb').read()) == expected else ''
        info.append((p, expected, digest))

    folders = {}
    for p, _, _ in info:
        if p['url'].startswith(('https://', 'http://')):
            folders.setdefault(folder_of(p['url']), set()).add(p['url'])
    index = {}
    for n, (folder, urls) in enumerate(sorted(folders.items()), 1):
        got = cdx_rows({'url': folder, 'matchType': 'prefix', 'collapse': 'digest'}, cache_dir) if len(urls) > 2 else None
        if got is None:
            for u in sorted(urls):
                one = cdx_rows({'url': u}, cache_dir)
                for original, ts, dg in one or []:
                    index.setdefault(url_key(original), []).append((ts, dg, original))
                if one is None:
                    index.setdefault(url_key(u), None)
        else:
            for original, ts, dg in got:
                index.setdefault(url_key(original), []).append((ts, dg, original))
        if n % 20 == 0:
            print('... CDX %d of %d folders' % (n, len(folders)), file=sys.stderr, flush=True)

    for p, expected, digest in info:
        key = url_key(p['url'])
        caps = index.get(key, [])
        if caps is None:
            states[(p['model'], p['url'])] = 'index-busy'
            continue
        caps = sorted(set(caps), reverse=True)
        recorded_ts = snapshot_time(p.get('archive', ''))
        if recorded_ts:
            same = [c for c in caps if c[0] == recorded_ts]
            former = url_key(snapshot_address(p['archive'])) != key
            # Kept: a snapshot of a former address of the photo, nothing to compare, or not listed with other bytes
            # (the folder query leaves out repeated captures with the same digest).
            if former or not digest or not same or any(c[1] == digest for c in same):
                states[(p['model'], p['url'])] = 'recorded'
                continue
        snap = ''
        if digest:
            match = [c for c in caps if c[1] == digest]
            if match:
                snap = snapshot_of(match[0][0], match[0][2])
        if not snap and caps:
            for ts, _, original in caps[:2]:
                body, _ = get(snapshot_of(ts, original))
                if body is not None and is_picture(body) and (not expected or sha(body) == expected or accepted(p, sha(body))):
                    snap = snapshot_of(ts, original)
                    break
        if snap:
            record(p, snap, expected)
            states[(p['model'], p['url'])] = 'found'
        else:
            states[(p['model'], p['url'])] = 'other-content' if caps else 'no-capture'
    flush(True)

    counts = {}
    for s in states.values():
        counts[s] = counts.get(s, 0) + 1
    print('after the index:', json.dumps(counts), flush=True)

    if a.save_page_now:
        by_key = {(p['model'], p['url']): (p, expected) for p, expected, _ in info}
        queue = [k for k, s in states.items() if s == 'no-capture'] + [k for k, s in states.items() if s == 'other-content']
        done_urls = {}
        for k in queue:
            p, expected = by_key[k]
            if not p['url'].startswith('https://'):
                continue
            if deadline and time.time() > deadline:
                print('time is up: Save Page Now stops here', flush=True)
                break
            if p['url'] in done_urls:
                snap = done_urls[p['url']]
            else:
                snap, status = spn_capture(p['url'], a.spn_gap)
                if status == 'busy':
                    print('Save Page Now keeps refusing (too many requests): stopped', flush=True)
                    break
                if snap:
                    body, _ = get(snap)
                    if body is None or not is_picture(body) or (expected and sha(body) != expected and not accepted(p, sha(body))):
                        print('    captured, other bytes:', p['url'], flush=True)
                        snap = ''
                else:
                    print('    Save Page Now failed (%s): %s' % (status, p['url']), flush=True)
                done_urls[p['url']] = snap
            if snap:
                record(p, snap, expected)
                states[k] = 'created'
        flush(True)

    counts = {}
    for s in states.values():
        counts[s] = counts.get(s, 0) + 1
    missing = sum(1 for p, _, _ in info if not p.get('archive')) if a.record else sum(1 for s in states.values() if s not in ('recorded', 'found', 'created'))
    print('photos: %d; %s; without a snapshot: %d; %.0f min' % (len(info), json.dumps(counts), missing, (time.time() - started) / 60), flush=True)
    if a.status:
        json.dump({'photos': len(info), 'counts': counts, 'withoutSnapshot': missing,
                   'states': [{'model': m, 'url': u, 'state': s} for (m, u), s in sorted(states.items())]},
                  open(a.status, 'w', encoding='utf-8'), indent=1)
    return 0


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


def backup_hashes(folder):
    """(model, url) -> the SHA-256 of the private archive's copy of that address (empty without a private archive)."""
    m = os.path.join(folder or '', 'manifest.json')
    if not folder or not os.path.isfile(m):
        return {}
    return {(e['model'], e['url']): e['sha256'].lower() for e in json.load(open(m, encoding='utf-8-sig'))
            if e.get('model') and e.get('url') and e.get('sha256') and not e.get('changed')}


_pages = {}


def page_candidates(page, url=''):
    """Image addresses on a page, the ones with the photo's file name first (largest sizes first: WordPress size
    suffixes and Shopify's width/height/crop parameters removed; Shopify's "?v=" version kept, since the version of
    the address the page names may still send the recorded bytes)."""
    if page not in _pages:
        data, _ = get(page)
        _pages[page] = data.decode('utf-8', 'replace') if data else ''
    html = _pages[page]
    found = []
    for u in re.findall(r'''(?:https?:)?//[^"'\s()<>,]+?\.(?:jpe?g|png|webp)(?:\?[^"'\s()<>,]*)?''', html.replace('\\/', '/'), re.I):
        u = ('https:' + u) if u.startswith('//') else u
        u = u.replace('&amp;', '&')
        base, _, query = u.partition('?')
        version = re.search(r'(?:^|&)v=(\d+)', query)
        for v in (re.sub(r'-\d{2,4}x\d{2,4}(\.(?:jpe?g|png|webp))$', r'\1', base, flags=re.I), base,
                  base + '?v=' + version.group(1) if version else '', u):
            if v and v not in found:
                found.append(v)
    name = urllib.parse.urlparse(url).path.rsplit('/', 1)[-1].rsplit('.', 1)[0].lower()
    if name:
        found.sort(key=lambda v: name not in urllib.parse.urlparse(v).path.lower())
    return found


def maker_hosts(model):
    """The officialHosts of a catalog model (its vendor file's and its own), as host or host/path prefix."""
    for f in glob.glob(os.path.join(DB, 'Devices', '*.json')):
        data = json.load(open(f, encoding='utf-8-sig'))
        for dev in data.get('devices', []):
            if dev.get('id') == model:
                return [h.strip().lower() for h in (data.get('officialHosts') or []) + (dev.get('officialHosts') or []) if h]
    return []


def host_allowed(url, old_url, hosts):
    u = urllib.parse.urlparse(url)
    if u.scheme != 'https':
        return False
    if u.hostname == urllib.parse.urlparse(old_url).hostname:
        return True
    where = (u.hostname or '') + u.path
    return any(where == h.rstrip('/') or where.startswith(h if '/' in h else h + '/') or
               ('/' not in h and (u.hostname or '').endswith('.' + h)) for h in hosts)


def relink_layouts(model, sha256, old, new):
    """Curated position sets of this photo (by hash) name the new address too (a plain text edit of the file)."""
    for f in glob.glob(os.path.join(DB, 'DeviceLayouts', '*.json')):
        text = open(f, encoding='utf-8-sig').read()
        try:
            data = json.loads(text)
        except Exception:
            continue
        photo = data.get('photo') or {}
        if data.get('model') == model and (photo.get('sha256') or '').lower() == sha256 and photo.get('url') == old:
            text = text.replace('"url": ' + json.dumps(old, ensure_ascii=False), '"url": ' + json.dumps(new, ensure_ascii=False), 1)
            open(f, 'w', encoding='utf-8', newline='\n').write(text)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('command', choices=['check', 'snapshots', 'recover', 'same-pixels'])
    ap.add_argument('query', nargs='?', default='')
    ap.add_argument('--out', default=os.path.join(tempfile.gettempdir(), 'joypro-photo-links.json'))
    ap.add_argument('--limit', type=int, default=0)
    ap.add_argument('--record', action='store_true')
    ap.add_argument('--save-page-now', action='store_true')
    ap.add_argument('--backup', default=os.environ.get('JOYPRO_PHOTO_BACKUP') or default_backup())
    ap.add_argument('--apply', action='store_true')
    ap.add_argument('--tries', type=int, default=40, help='recover: at most this many addresses tried per photo')
    ap.add_argument('--report', default='', help='only the photos this check report found not alive')
    ap.add_argument('--all', action='store_true', help='snapshots: the whole list at once (CDX per folder, recorded snapshots checked too)')
    ap.add_argument('--hours', type=float, default=0, help='snapshots --all: Save Page Now stops after this many hours')
    ap.add_argument('--spn-gap', type=float, default=20, help='snapshots --all: seconds between Save Page Now requests')
    ap.add_argument('--status', default='', help='snapshots --all: the per-photo result as JSON')
    a = ap.parse_args()
    files, rows = load(a.query)
    if a.report:
        rows = report_rows(rows, a.report)
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

    if a.command == 'snapshots' and a.all:
        return snapshots_all(a, files, rows)

    if a.command == 'snapshots':
        found = asked = 0
        copies = backup_hashes(a.backup)
        backup = backup_index(a.backup)
        for f, p in rows:
            if p.get('archive') and not a.report:
                continue
            recorded = (p.get('sha256') or '').lower()
            # Without a recorded hash, the private archive's copy of the address says which content is the photo.
            expected = recorded or copies.get((p['model'], p['url']), '')
            snap = find_snapshot(p['url'], expected, backup.get(expected, '')) if p['url'].startswith(('https://', 'http://')) else None
            if snap and snap == p.get('archive'):
                snap = None
            if snap:
                found += 1
                print('snapshot', p['model'], snap)
                if a.record:
                    p['archive'] = snap
                    if not recorded and expected:
                        p['sha256'] = expected
            elif a.save_page_now and p['url'].startswith('https://'):
                asked += 1
                print('save page now', p['model'], p['url'], save_page_now(p['url']))
        if a.record:
            save(files)
        print('snapshots found: %d, Save Page Now requests: %d' % (found, asked))
        return 0

    if a.command == 'recover':
        backup = backup_index(a.backup)
        copies = backup_hashes(a.backup)
        keyed = protected_hashes()
        changed = False
        for f, p in rows:
            r = probe(p)
            if r['state'] == 'alive' or r.get('fallback') == 'ok':
                continue
            recorded = (p.get('sha256') or '').lower()
            # Without a recorded hash, the private archive's copy of the address says which content is the photo.
            expected = recorded or copies.get((p['model'], p['url']), '')
            local = backup.get(expected)
            print('%s %s (%s, snapshot %s)' % (p['model'], p['url'], r['state'], r.get('fallback')))
            print('    private archive:', local if local and os.path.isfile(local) else 'no copy')
            new = None
            if expected and p.get('page', '').startswith('http'):
                hosts = maker_hosts(p['model'])
                tried = 0
                for u in page_candidates(p['page'], p['url']):
                    if u == p['url'] or not host_allowed(u, p['url'], hosts):
                        continue
                    tried += 1
                    if tried > a.tries:
                        break
                    data, _ = get(u)
                    if data is not None and sha(data) == expected:
                        new = u
                        break
            if new:
                print('    same photo now at:', new)
                if a.apply:
                    relink_layouts(p['model'], expected, p['url'], new)
                    p['url'] = new
                    p.pop('archive', None)
                    if not recorded:
                        p['sha256'] = expected
                    changed = True
            else:
                print('    no new address found; prepare: https://web.archive.org/save/' + (p.get('page') or p['url']))
                if expected in keyed:
                    print('    curated positions or a drawing are keyed by this hash: it stays as recorded')
        if changed:
            save(files)
        return 0

    if a.command == 'same-pixels':
        backup = backup_index(a.backup)
        same = other = unproven = 0
        for f, p in rows:
            recorded = (p.get('sha256') or '').lower()
            if not recorded or not p['url'].startswith('https://'):
                continue
            data, status = get(p['url'])
            if data is None or not is_picture(data):
                continue
            now = sha(data)
            if accepted(p, now):
                continue
            # The recorded picture: the private archive's copy, else a snapshot with the recorded bytes.
            old = None
            local = backup.get(recorded)
            if local and os.path.isfile(local):
                old = open(local, 'rb').read()
            if old is None or sha(old) != recorded:
                body, _ = get(archive_url(p))
                old = body if body is not None and sha(body) == recorded else None
            if old is None:
                unproven += 1
                print('no copy of the recorded picture, not proven:', p['model'], p['url'])
                continue
            if same_pixels(old, data):
                same += 1
                print('same pixels, re-encoded:', p['model'], p['url'], now)
                if a.apply:
                    p['sha256Alt'] = sorted(set([x.lower() for x in p.get('sha256Alt') or []] + [now]))
            else:
                other += 1
                print('other pixels (the maker changed the photo):', p['model'], p['url'])
        if a.apply and same:
            save(files)
        print('same pixels: %d%s, other pixels: %d, not proven: %d' % (same, ' (recorded)' if a.apply else '', other, unproven))
        return 0
    return 2


if __name__ == '__main__':
    sys.exit(main())
