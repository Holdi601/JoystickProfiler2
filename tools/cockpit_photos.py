"""Verified cockpit photos and JoyPro's labels on them (maintainer tool, docs/cockpits.md "Photos of cockpits no game has").

For cockpits no game has good pictures of, JoyPro ships photo addresses with a SHA-256 each (the aircraft's "photos" in
DB/Cockpits/*.json) and its own labels as data (DB/Cockpits/photo-labels/<aircraft>.<sha12>.json). The user's PC
downloads the photo ("Download photos" on the Cockpits page), checks the hash and falls back to the Wayback snapshot;
the labels are drawn on top. Like the device reference photos (tools/device_photo_links.py).

  py -3 tools/cockpit_photos.py needed [--game G]
      the variants without good pictures from a game (engine cockpits photos --needed)
  py -3 tools/cockpit_photos.py search <words> [--limit n]
      Wikimedia Commons files for the words (File namespace), with size and licence
  py -3 tools/cockpit_photos.py record <aircraft> <photo address or Commons file page> [--view cockpit] [--variant id]
                               [--caption text] [--credit text] [--license id] [--source commons|maker|press|museum]
                               [--page url] [--width 1920] [--archive <folder>]
      downloads the photo once (a Commons file: the original, or its 1920 px thumbnail when it is over 7.5 MB or 6000 px;
      credit and licence from the file's page), checks that it is a JPEG or PNG without an EXIF rotation, records url,
      sha256, width, height (and the rest) in the aircraft's photo entry (the same address is updated, else added) and
      keeps the bytes in the private archive (sha256\\<sha256>.<ext>; JOYPRO_COCKPIT_PHOTO_ARCHIVE, default
      %LOCALAPPDATA%\\JoyPro2\\cockpit-photo-archive)
  py -3 tools/cockpit_photos.py panels <aircraft> [variant]
      the catalog panels and grips a label may lead to ("panel")
  py -3 tools/cockpit_photos.py label <aircraft> <sha12> list | title <text> |
                               add <n> <x> <y> <text> [--panel id] [--note text] | move <n> <x> <y> | remove <n>
      JoyPro's labels of a photo (x, y: 0..1 of the photo, from the top left)
  py -3 tools/cockpit_photos.py preview <aircraft> [variant] <sha12> [--out file.png] [--dark] [--archive <folder>]
      the Cockpits page's photo page as a PNG (engine cockpits render --page photo:<sha12> --photos <archive>)
  py -3 tools/cockpit_photos.py check [query] [--out report.json]
      every verified photo: alive (same content), changed, dead; for the others whether the snapshot has the content
  py -3 tools/cockpit_photos.py snapshots [query] [--record] [--save-page-now] [--all]
      a Wayback snapshot with the recorded content for every verified photo without one (CDX, matched by the digest
      of the private archive's copy, then the captures compared, then the availability API); --all checks the
      recorded snapshots too (by the index's digest, else downloaded) and replaces one with other bytes; --record
      writes it as "archive"; --save-page-now asks Save Page Now for the ones the archive lacks (one request every
      20 s, waiting 5 and 10 minutes after "too many requests")

Standard library only. One request at a time per site, spaced; a user agent that names JoyPro and its repository.
"""
import argparse
import glob
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
COCKPITS = os.environ.get('JOYPRO_COCKPITS_DB') or os.path.join(REPO, 'EngineOnly_Runtime', 'DB', 'Cockpits')
LABELS = os.path.join(COCKPITS, 'photo-labels')
ARCHIVE = os.environ.get('JOYPRO_COCKPIT_PHOTO_ARCHIVE') or os.path.join(
    os.environ.get('LOCALAPPDATA', os.path.expanduser('~')), 'JoyPro2', 'cockpit-photo-archive')
UA = 'JoyPro2-maintainer/2.5 (+https://github.com/Holdi601/JoystickProfiler2; cockpit photo curation)'
COMMONS_API = 'https://commons.wikimedia.org/w/api.php'
LABEL_FORMAT = 'joypro-cockpit-photo-labels'
_last = {}


def get(url, timeout=60, tries=4):
    """(bytes or None, status or error). Spaced per site (Wikimedia 1 s, the web archive 2 s, others 1 s); after 429/503
    the server's Retry-After (else 10, 30, 60 s); the web archive also after 502/504 and a timeout."""
    host = urllib.parse.urlparse(url).hostname or ''
    archive = 'archive.org' in host
    for attempt in range(tries):
        wait = _last.get(host, 0) + (2.0 if archive else 1.0) - time.time()
        if wait > 0:
            time.sleep(wait)
        _last[host] = time.time()
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': UA}), timeout=timeout) as r:
                return r.read(), r.status
        except urllib.error.HTTPError as e:
            if (e.code in (429, 503) or (archive and e.code in (502, 504))) and attempt < tries - 1:
                retry = e.headers.get('Retry-After', '') if e.headers else ''
                time.sleep(int(retry) if retry.isdigit() and int(retry) < 300 else (10, 30, 60)[min(attempt, 2)])
                continue
            return None, e.code
        except Exception as e:  # offline, DNS, TLS, timeout
            if archive and attempt < tries - 1:
                time.sleep(10 * (attempt + 1))
                continue
            return None, str(e)
    return None, 'busy'


def is_picture(data):
    return bool(data) and (data[:2] == b'\xff\xd8' or data[:4] == b'\x89PNG')


def ext_of(data):
    return '.png' if data[:4] == b'\x89PNG' else '.jpg'


def sha(data):
    return hashlib.sha256(data).hexdigest()


def image_size(data):
    """(width, height) of a JPEG or PNG."""
    if data[:4] == b'\x89PNG':
        return int.from_bytes(data[16:20], 'big'), int.from_bytes(data[20:24], 'big')
    i = 2
    while i + 9 < len(data):
        while i < len(data) and data[i] == 0xFF:
            i += 1
        marker = data[i]
        length = int.from_bytes(data[i + 1:i + 3], 'big')
        if 0xC0 <= marker <= 0xCF and marker not in (0xC4, 0xC8, 0xCC):
            return int.from_bytes(data[i + 6:i + 8], 'big'), int.from_bytes(data[i + 4:i + 6], 'big')
        i += 1 + length
    return 0, 0


def exif_orientation(data):
    """The EXIF orientation of a JPEG (1 = as stored); JoyPro draws the pixels as stored, so a rotated photo would put
    the labels in the wrong place."""
    if data[:2] != b'\xff\xd8':
        return 1
    i = 2
    while i + 4 < len(data) and data[i] == 0xFF:
        marker = data[i + 1]
        length = int.from_bytes(data[i + 2:i + 4], 'big')
        if marker == 0xE1 and data[i + 4:i + 10] == b'Exif\x00\x00':
            t = data[i + 10:i + 2 + length]
            big = t[:2] == b'MM'
            order = 'big' if big else 'little'
            ifd = int.from_bytes(t[4:8], order)
            count = int.from_bytes(t[ifd:ifd + 2], order)
            for k in range(count):
                e = ifd + 2 + 12 * k
                if int.from_bytes(t[e:e + 2], order) == 0x0112:
                    return int.from_bytes(t[e + 8:e + 10], order)
            return 1
        if marker == 0xDA:
            break
        i += 2 + length
    return 1


def dump(path, data):
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
        f.write('\n')


class Lock:
    """A lock file next to the database, so that several helpers can record photos at the same time."""

    def __init__(self):
        self.path = os.path.join(COCKPITS, '.photos.lock')

    def __enter__(self):
        for _ in range(600):
            try:
                self.fd = os.open(self.path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
                return self
            except FileExistsError:
                if time.time() - os.path.getmtime(self.path) > 120:
                    os.remove(self.path)
                time.sleep(0.2)
        raise SystemExit('the database is locked: ' + self.path)

    def __exit__(self, *a):
        os.close(self.fd)
        # A sync tool or a virus scanner may hold the new file for a moment.
        for attempt in range(50):
            try:
                os.remove(self.path)
                return
            except PermissionError:
                time.sleep(0.2)
        os.remove(self.path)


def db_files():
    return sorted(f for f in glob.glob(os.path.join(COCKPITS, '*.json')))


def find_aircraft(aircraft_id):
    """(file, data, aircraft entry)."""
    for f in db_files():
        data = json.load(open(f, encoding='utf-8'))
        for a in data.get('aircraft', []):
            if a.get('id') == aircraft_id:
                return f, data, a
    raise SystemExit('unknown aircraft: ' + aircraft_id)


def verified_photos(query=''):
    out = []
    for f in db_files():
        data = json.load(open(f, encoding='utf-8'))
        for a in data.get('aircraft', []):
            for p in a.get('photos', []) or []:
                if p.get('sha256') and (not query or query in a['id'] or query in p.get('url', '')):
                    out.append((f, data, a, p))
    return out


# ------------------------------------------------------------------ Wikimedia Commons

def commons_title(url):
    """File:Name.jpg of a Commons file page or upload address; None for any other address."""
    u = urllib.parse.urlparse(url)
    if u.hostname in ('commons.wikimedia.org', 'en.wikipedia.org') and '/wiki/' in u.path:
        name = urllib.parse.unquote(u.path.split('/wiki/', 1)[1])
        return name if name.startswith('File:') else 'File:' + name
    if u.hostname == 'upload.wikimedia.org' and '/commons/' in u.path:
        parts = u.path.split('/')
        name = parts[-1] if '/thumb/' not in u.path else parts[-2]
        return 'File:' + urllib.parse.unquote(name)
    return None


def strip_html(text):
    text = re.sub(r'<[^>]+>', '', text or '')
    return re.sub(r'\s+', ' ', text.replace('&amp;', '&').replace('&#039;', "'").replace('&quot;', '"')).strip()


def licence_id(short):
    s = (short or '').strip().lower()
    if s in ('public domain', 'pd', 'pdm-owner', 'pd-usgov', 'pd-usgov-military') or s.startswith('pd') or 'public domain' in s:
        return 'public-domain'
    if s.startswith('cc0'):
        return 'cc0'
    m = re.match(r'cc[ -](by(?:-sa|-nc|-nd)*)[ -]?([\d.]+)?', s)
    if m:
        return 'cc-' + m.group(1) + ('-' + m.group(2) if m.group(2) else '')
    if s.startswith('gfdl'):
        return 'gfdl-1.2' if '1.2' in s else 'gfdl'
    return s.replace(' ', '-')


def commons_info(title, width=1920):
    q = COMMONS_API + '?' + urllib.parse.urlencode({
        'action': 'query', 'titles': title, 'prop': 'imageinfo', 'iiprop': 'url|size|extmetadata|mime',
        'iiurlwidth': str(width), 'format': 'json', 'formatversion': '2'})
    data, status = get(q)
    if not data:
        raise SystemExit('Commons API: ' + str(status))
    page = json.loads(data.decode('utf-8'))['query']['pages'][0]
    if 'imageinfo' not in page:
        raise SystemExit('no such Commons file: ' + title)
    info = page['imageinfo'][0]
    meta = info.get('extmetadata', {})
    return {
        'original': info['url'], 'width': info['width'], 'height': info['height'], 'mime': info.get('mime', ''), 'size': info.get('size', 0),
        'thumb': info.get('thumburl', ''), 'thumbwidth': info.get('thumbwidth', 0), 'thumbheight': info.get('thumbheight', 0),
        'page': info.get('descriptionurl', 'https://commons.wikimedia.org/wiki/' + urllib.parse.quote(title.replace(' ', '_'))),
        'credit': strip_html(meta.get('Artist', {}).get('value', '')) or strip_html(meta.get('Credit', {}).get('value', '')),
        'license': licence_id(meta.get('LicenseShortName', {}).get('value', '')),
        'description': strip_html(meta.get('ImageDescription', {}).get('value', ''))[:200],
    }


def cmd_search(a):
    q = COMMONS_API + '?' + urllib.parse.urlencode({
        'action': 'query', 'generator': 'search', 'gsrnamespace': '6', 'gsrsearch': a.words, 'gsrlimit': str(a.limit),
        'prop': 'imageinfo', 'iiprop': 'size|extmetadata|mime', 'format': 'json', 'formatversion': '2'})
    data, status = get(q)
    if not data:
        raise SystemExit('Commons API: ' + str(status))
    pages = json.loads(data.decode('utf-8')).get('query', {}).get('pages', [])
    for p in sorted(pages, key=lambda p: p.get('index', 0)):
        info = (p.get('imageinfo') or [{}])[0]
        meta = info.get('extmetadata', {})
        print('%-6s %5sx%-5s %-14s https://commons.wikimedia.org/wiki/%s' % (
            info.get('mime', '?').split('/')[-1], info.get('width', '?'), info.get('height', '?'),
            licence_id(meta.get('LicenseShortName', {}).get('value', ''))[:14], urllib.parse.quote(p['title'].replace(' ', '_'), safe=':/')))
    return 0


# ------------------------------------------------------------------ record

def cmd_record(a):
    url, page, credit, licence, source = a.url, a.page, a.credit, a.license, a.source
    title = commons_title(url)
    if title:
        info = commons_info(title, a.width)
        if info['mime'] not in ('image/jpeg', 'image/png'):
            raise SystemExit('not a JPEG or PNG on Commons: ' + info['mime'])
        # The original file (the same bytes everywhere, and in the web archive). Wikimedia's thumbnails are made by each
        # data centre and differ in their bytes from place to place: only for a file too big to download (JoyPro takes
        # at most 8 MB) or to draw (more than 6000 px), or with --thumb.
        big = info['size'] > 7500000 or max(info['width'], info['height']) > 6000
        url = info['thumb'] if (big or a.thumb) and info['thumb'] else info['original']
        # The thumbnail's canonical address (the API adds a tracking query and may name another host for the same file).
        url = url.split('?', 1)[0].replace('https://thumb.wikimedia.org/', 'https://upload.wikimedia.org/')
        page = page or info['page']
        credit = credit or info['credit']
        licence = licence or info['license']
        source = source or 'commons'
        if not a.caption and info['description']:
            print('Commons description:', info['description'])
    if not url.startswith('https://'):
        raise SystemExit('an https address is needed: ' + url)
    data, status = get(url)
    if data is None or not is_picture(data):
        raise SystemExit('no JPEG or PNG at %s (%s)' % (url, status))
    if exif_orientation(data) not in (0, 1):
        raise SystemExit('the photo has an EXIF rotation (%d): pick another file or its rotated version' % exif_orientation(data))
    digest = sha(data)
    width, height = image_size(data)
    os.makedirs(os.path.join(a.archive, 'sha256'), exist_ok=True)
    stored = os.path.join(a.archive, 'sha256', digest + ext_of(data))
    if not os.path.exists(stored):
        open(stored, 'wb').write(data)
    with Lock():
        f, db, aircraft = find_aircraft(a.aircraft)
        if a.variant and a.variant not in [v['id'] for v in aircraft.get('variants', [])]:
            raise SystemExit('unknown variant of %s: %s' % (a.aircraft, a.variant))
        photos = aircraft.setdefault('photos', [])
        # The same address, the address given by --replace, or the same Commons file (a link recorded before).
        entry = next((p for p in photos if p.get('url') == url or (a.replace and p.get('url') == a.replace) or
                      (title and commons_title(p.get('url', '')) == title)), None)
        if entry is None:
            entry = {}
            photos.append(entry)
        if entry.get('url') != url or entry.get('sha256') not in (None, digest):
            # Another file: its snapshot and alternative hashes were of the old one.
            entry.pop('archive', None)
            entry.pop('sha256Alt', None)
        entry['url'] = url
        # Given options win; else what the entry already says (curated earlier), else what Commons says.
        given = {'credit': a.credit, 'license': a.license, 'page': a.page, 'caption': a.caption}
        for key, value in (('page', page), ('view', a.view), ('credit', credit), ('license', licence), ('variant', a.variant),
                           ('caption', a.caption), ('confidence', a.confidence)):
            if value and (given.get(key) or not entry.get(key) or key in ('view', 'variant', 'confidence')):
                entry[key] = value
            elif key in ('page', 'view', 'credit', 'license'):
                entry.setdefault(key, '')
        entry['sha256'] = digest
        entry['width'] = width
        entry['height'] = height
        entry['source'] = source or 'maker'
        dump(f, db)
    print('recorded %s %s %dx%d sha256 %s (%s)' % (a.aircraft, url, width, height, digest, stored))
    print('labels: py -3 tools/cockpit_photos.py label %s %s add 1 0.5 0.1 "Overhead panel" --panel <id>' % (a.aircraft, digest[:12]))
    return 0


# ------------------------------------------------------------------ labels

def photo_of(aircraft, key):
    _, _, a = find_aircraft(aircraft)
    for p in a.get('photos', []) or []:
        if p.get('sha256') and (p['sha256'].startswith(key.lower()) or p.get('url') == key):
            return a, p
    raise SystemExit('no verified photo %s of %s (record it first)' % (key, aircraft))


def label_file(aircraft, photo):
    return os.path.join(LABELS, '%s.%s.json' % (aircraft, photo['sha256'][:12]))


def cmd_label(a):
    aircraft, photo = photo_of(a.aircraft, a.key)
    path = label_file(a.aircraft, photo)
    data = json.load(open(path, encoding='utf-8')) if os.path.exists(path) else {
        'format': LABEL_FORMAT, 'version': 1, 'aircraft': a.aircraft,
        'photo': {'url': photo['url'], 'sha256': photo['sha256']}, 'title': '',
        'source': "JoyPro's labels, placed on the photo by looking at it (%s)" % time.strftime('%Y-%m-%d'), 'labels': []}
    labels = data['labels']
    op = a.op
    if op == 'list':
        print('%s (%s) %s' % (os.path.basename(path), data.get('title', ''), photo['url']))
        for l in sorted(labels, key=lambda l: l['n']):
            print('  %2d  %.3f %.3f  %-40s %s %s' % (l['n'], l['x'], l['y'], l['text'], l.get('panel', ''), ('- ' + l['note']) if l.get('note') else ''))
        return 0
    if op == 'title':
        data['title'] = ' '.join(a.args)
    elif op == 'add':
        if len(a.args) < 4:
            raise SystemExit('add <n> <x> <y> <text>')
        n, x, y, text = int(a.args[0]), float(a.args[1]), float(a.args[2]), ' '.join(a.args[3:])
        if not (0 <= x <= 1 and 0 <= y <= 1):
            raise SystemExit('x and y are 0..1 of the photo')
        labels[:] = [l for l in labels if l['n'] != n]
        label = {'n': n, 'text': text, 'x': round(x, 4), 'y': round(y, 4)}
        if a.panel:
            label['panel'] = a.panel
        if a.note:
            label['note'] = a.note
        labels.append(label)
    elif op == 'move':
        n, x, y = int(a.args[0]), float(a.args[1]), float(a.args[2])
        for l in labels:
            if l['n'] == n:
                l['x'], l['y'] = round(x, 4), round(y, 4)
    elif op == 'remove':
        labels[:] = [l for l in labels if l['n'] != int(a.args[0])]
    labels.sort(key=lambda l: l['n'])
    os.makedirs(LABELS, exist_ok=True)
    dump(path, data)
    print('%s: %d labels' % (os.path.relpath(path, REPO), len(labels)))
    return 0


# ------------------------------------------------------------------ engine

def engine_path():
    env = os.environ.get('JOYPRO_ENGINE')
    if env:
        return env
    for config in ('Debug', 'Release'):
        path = os.path.join(REPO, 'engine', 'bin', 'x64', config, 'engine.exe')
        if os.path.exists(path):
            return path
    raise SystemExit('engine.exe not found: build the engine first or set JOYPRO_ENGINE')


def run_engine(args):
    env = dict(os.environ)
    env['JOYPRO_COCKPITS_DB'] = COCKPITS
    result = subprocess.run([engine_path(), '--status', 'false', '--benchmark', 'false'] + args,
                            capture_output=True, text=True, encoding='utf-8', errors='replace', env=env)
    return result.returncode, result.stdout + result.stderr


def cmd_needed(a):
    code, out = run_engine(['cockpits', 'photos', '--needed', '--text'] + (['--game', a.game] if a.game else []))
    print(out)
    return code


def cmd_panels(a):
    code, out = run_engine(['cockpits', 'get', a.aircraft] + ([a.variant] if a.variant else []) + ['--json'])
    start = out.find('{')
    if code != 0 or start < 0:
        print(out)
        return 1
    v = json.loads(out[start:])
    print('grips:', ', '.join(d.get('Device', '') for d in v.get('Devices', [])))
    for p in v.get('Panels', []):
        print('  %-36s %-18s %s' % (p.get('Id', ''), p.get('Area', ''), p.get('Name', '')))
    return 0


def cmd_preview(a):
    args = a.args
    key = args[-1]
    variant = args[0] if len(args) > 1 else None
    out = a.out or os.path.join(tempfile.gettempdir(), 'joypro-photo-%s-%s.png' % (a.aircraft, key[:12]))
    code, text = run_engine(['cockpits', 'render', a.aircraft] + ([variant] if variant else []) +
                            ['--page', 'photo:' + key[:12].lower(), '--photos', a.archive, '--png', out, '--pictures', 'none'] +
                            (['--style', 'dark'] if a.dark else []))
    print(text.strip() or out)
    return code


# ------------------------------------------------------------------ link health and snapshots

def accepted(photo, digest):
    return digest == photo['sha256'] or digest in (photo.get('sha256Alt') or [])


def archive_url(photo):
    a = photo.get('archive', '')
    return a if a.startswith('https://web.archive.org/web/') else 'https://web.archive.org/web/2id_/' + photo['url']


def cmd_check(a):
    report = []
    for _, _, ac, p in verified_photos(a.query):
        data, status = get(p['url'])
        row = {'aircraft': ac['id'], 'url': p['url']}
        if data is not None and is_picture(data):
            row['state'] = 'alive' if accepted(p, sha(data)) else 'changed'
        else:
            row['state'], row['status'] = 'dead', status
        if row['state'] != 'alive':
            body, _ = get(archive_url(p))
            row['fallback'] = 'ok' if body is not None and is_picture(body) and accepted(p, sha(body)) else 'missing'
        report.append(row)
        if row['state'] != 'alive':
            print('%-8s %-8s %s %s' % (row['state'], row.get('fallback', ''), ac['id'], p['url']))
    counts = {}
    for r in report:
        k = r['state'] + ('' if r['state'] == 'alive' else ' / fallback ' + r.get('fallback', '?'))
        counts[k] = counts.get(k, 0) + 1
    out = a.out or os.path.join(tempfile.gettempdir(), 'joypro-cockpit-photo-links.json')
    json.dump({'checked': len(report), 'counts': counts, 'photos': report}, open(out, 'w', encoding='utf-8'), indent=1)
    print(json.dumps(counts, indent=1))
    print('report:', out)
    return 0 if all(r['state'] == 'alive' or r.get('fallback') == 'ok' for r in report) else 1


def private_copy(digest):
    """The private archive's copy of a photo (sha256/<sha256>.<ext>) when its bytes have that hash, else ''."""
    for ext in ('.jpg', '.png', '.jpeg'):
        f = os.path.join(ARCHIVE, 'sha256', digest + ext)
        if os.path.isfile(f) and sha(open(f, 'rb').read()) == digest:
            return f
    return ''


def cdx_digest(file):
    """The CDX index's digest (base32 SHA-1) of a file's bytes: a capture with it has exactly these bytes."""
    import base64
    return base64.b32encode(hashlib.sha1(open(file, 'rb').read()).digest()).decode('ascii')


def captures(url):
    """The address's successful captures, newest first: [(timestamp, digest)]; None when the index did not answer."""
    q = 'https://web.archive.org/cdx/search/cdx?' + urllib.parse.urlencode({'url': url, 'output': 'json', 'filter': 'statuscode:200', 'fl': 'timestamp,digest'})
    data, _ = get(q)
    if data is None:
        return None
    try:
        return [tuple(r) for r in reversed(json.loads(data.decode('utf-8') or '[]')[1:])]
    except Exception:
        return None


def find_snapshot(url, recorded, digest=''):
    """A capture of the address with the recorded bytes: by the CDX digest of the private copy (nothing downloaded),
    else the captures downloaded and compared (newest first), else the availability API's closest one."""
    rows = captures(url) or []
    if digest:
        for ts, d in rows:
            if d == digest:
                return 'https://web.archive.org/web/%sid_/%s' % (ts, url)
    tried = set()
    for ts, d in rows:
        if d in tried or len(tried) >= 4:
            continue
        tried.add(d)
        snap = 'https://web.archive.org/web/%sid_/%s' % (ts, url)
        body, _ = get(snap)
        if body is not None and is_picture(body) and sha(body) == recorded:
            return snap
    data, _ = get('https://archive.org/wayback/available?' + urllib.parse.urlencode({'url': url}))
    try:
        closest = json.loads(data.decode('utf-8'))['archived_snapshots']['closest']
        ts = str(closest.get('timestamp', ''))
        if closest.get('available') and re.fullmatch(r'\d{14}', ts):
            snap = 'https://web.archive.org/web/%sid_/%s' % (ts, url)
            body, _ = get(snap)
            if body is not None and is_picture(body) and sha(body) == recorded:
                return snap
    except Exception:
        pass
    return None


def recorded_ok(photo, digest):
    """Whether the photo's recorded snapshot has its recorded bytes: the index lists that capture with the private
    copy's digest, else the snapshot downloaded and compared."""
    m = re.match(r'https://web\.archive\.org/web/(\d{4,14})id_/(.+)$', photo.get('archive', ''))
    if not m:
        return False
    if digest:
        rows = captures(m.group(2))
        if rows and any(ts == m.group(1) and d == digest for ts, d in rows):
            return True
    body, _ = get(photo['archive'])
    return body is not None and is_picture(body) and accepted(photo, sha(body))


def save_page_now(url):
    """Asks the public Save Page Now to capture an address: (snapshot or '', status). One request every 20 s; after
    "too many requests" it waits 5, then 10 minutes."""
    for wait in (300, 600, None):
        snap, status = '', 0
        try:
            req = urllib.request.Request('https://web.archive.org/save/' + url, headers={'User-Agent': UA})
            with urllib.request.urlopen(req, timeout=180) as r:
                m = re.search(r'/web/(\d{14})', r.headers.get('Content-Location', '') or '') or re.search(r'/web/(\d{14})', r.geturl())
                snap, status = ('https://web.archive.org/web/%sid_/%s' % (m.group(1), url)) if m else '', r.status
        except urllib.error.HTTPError as e:
            status = e.code
        except Exception as e:
            status = str(e)
        time.sleep(20)
        if status == 429 and wait:
            time.sleep(wait)
            continue
        return snap, status
    return '', 'busy'


def cmd_snapshots(a):
    """Every verified photo gets a snapshot with its recorded bytes; --all checks the recorded ones too (a recorded
    snapshot with other bytes is replaced)."""
    found = asked = kept = 0
    todo = []
    for f, _, ac, p in verified_photos(a.query):
        copy = private_copy(p['sha256'])
        digest = cdx_digest(copy) if copy else ''
        if p.get('archive'):
            if not a.all:
                continue
            if recorded_ok(p, digest):
                kept += 1
                print('snapshot ok', ac['id'], p['archive'])
                continue
            print('recorded snapshot has other bytes', ac['id'], p['archive'])
        todo.append((ac['id'], p['url'], p['sha256'], digest))
    results = {}
    missing = []
    for aircraft, url, recorded, digest in todo:
        snap = find_snapshot(url, recorded, digest)
        if not snap and a.save_page_now:
            asked += 1
            snap, status = save_page_now(url)
            print('save page now', aircraft, url, status)
            if snap:
                body, _ = get(snap)
                if body is None or not is_picture(body) or sha(body) != recorded:
                    snap = find_snapshot(url, recorded, digest)
        if snap:
            found += 1
            results[(aircraft, url)] = snap
            print('snapshot', aircraft, snap)
        else:
            missing.append((aircraft, url))
            print('no snapshot yet', aircraft, url)
    if a.record and results:
        with Lock():
            for f in db_files():
                db = json.load(open(f, encoding='utf-8'))
                changed = False
                for ac in db.get('aircraft', []):
                    for p in ac.get('photos', []) or []:
                        snap = results.get((ac['id'], p.get('url')))
                        if snap and p.get('archive') != snap:
                            p['archive'] = snap
                            changed = True
                if changed:
                    dump(f, db)
    print('recorded snapshots checked and kept: %d; snapshots found: %d of %d, Save Page Now requests: %d; without a snapshot: %d'
          % (kept, found, len(todo), asked, len(missing)))
    return 0 if not missing else 1


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='command', required=True)
    s = sub.add_parser('needed')
    s.add_argument('--game', default='')
    s = sub.add_parser('search')
    s.add_argument('words')
    s.add_argument('--limit', type=int, default=20)
    s = sub.add_parser('record')
    s.add_argument('aircraft')
    s.add_argument('url')
    for opt in ('--variant', '--caption', '--credit', '--license', '--source', '--page', '--replace'):
        s.add_argument(opt, default='')
    s.add_argument('--view', default='cockpit')
    s.add_argument('--confidence', default='high')
    s.add_argument('--width', type=int, default=1920, help='the thumbnail width for a file too big to use as it is')
    s.add_argument('--thumb', action='store_true', help='the thumbnail even when the original is small enough')
    s.add_argument('--archive', default=ARCHIVE)
    s = sub.add_parser('panels')
    s.add_argument('aircraft')
    s.add_argument('variant', nargs='?')
    s = sub.add_parser('label')
    s.add_argument('aircraft')
    s.add_argument('key')
    s.add_argument('op', choices=['list', 'title', 'add', 'move', 'remove'])
    s.add_argument('args', nargs='*')
    s.add_argument('--panel', default='')
    s.add_argument('--note', default='')
    s = sub.add_parser('preview')
    s.add_argument('aircraft')
    s.add_argument('args', nargs='+')
    s.add_argument('--out', default='')
    s.add_argument('--dark', action='store_true')
    s.add_argument('--archive', default=ARCHIVE)
    s = sub.add_parser('check')
    s.add_argument('query', nargs='?', default='')
    s.add_argument('--out', default='')
    s = sub.add_parser('snapshots')
    s.add_argument('query', nargs='?', default='')
    s.add_argument('--record', action='store_true')
    s.add_argument('--save-page-now', action='store_true')
    s.add_argument('--all', action='store_true', help='check the recorded snapshots too')
    a = ap.parse_args()
    return {'needed': cmd_needed, 'search': cmd_search, 'record': cmd_record, 'panels': cmd_panels, 'label': cmd_label,
            'preview': cmd_preview, 'check': cmd_check, 'snapshots': cmd_snapshots}[a.command](a)


if __name__ == '__main__':
    sys.exit(main())
