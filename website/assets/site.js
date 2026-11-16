// JoyPro website: theme switch, phone menu, detail-level switch, Mermaid, client-side search (works from file:// too).
(function () {
  var root = document.body.getAttribute('data-root') || '';
  var html = document.documentElement;

  function store(key, value) { try { localStorage.setItem(key, value); } catch (e) { } }
  function load(key) { try { return localStorage.getItem(key); } catch (e) { return null; } }
  function isDark() {
    if (html.dataset.theme) return html.dataset.theme === 'dark';
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
  }

  // Theme
  var themeBtn = document.querySelector('.theme-btn');
  if (themeBtn) themeBtn.addEventListener('click', function () {
    var next = isDark() ? 'light' : 'dark';
    html.dataset.theme = next;
    store('jp-theme', next);
    if (document.querySelector('pre.mermaid')) location.reload();
  });

  // Languages (website/i18n.py): English at the root, the others under <code>/; assets/i18n.js has the list and the
  // few texts this script shows. The choice is remembered in jp-lang; never an automatic redirect.
  var I18N = window.JP_I18N || { langs: [['en', 'English']], t: {} };
  var lang = document.body.getAttribute('data-lang') || 'en';
  var pagePath = document.body.getAttribute('data-page') || 'index.html';
  var translated = document.body.getAttribute('data-i18n') === '1';
  function tr(text, code) { var row = I18N.t[code || lang] || {}; return row[text] || text; }
  function langUrl(code) { return root + (code === 'en' ? '' : code + '/') + (translated ? pagePath : 'index.html'); }
  var slot = document.querySelector('.lang-slot');
  if (slot && I18N.langs.length > 1) {
    var sel = document.createElement('select');
    sel.className = 'lang-select';
    sel.setAttribute('aria-label', tr('Language'));
    // On a phone only the native name fits ("日本語", not "日本語 (Japanese)").
    var narrow = window.matchMedia && window.matchMedia('(max-width: 820px)').matches;
    I18N.langs.forEach(function (l) {
      var o = document.createElement('option');
      o.value = l[0]; o.textContent = narrow && l[2] ? l[2] : l[1];
      o.title = l[1];
      if (l[0] === lang) o.selected = true;
      sel.appendChild(o);
    });
    sel.addEventListener('change', function () {
      store('jp-lang', sel.value);
      if (!translated && lang === 'en') { showEnglishOnly(sel.value); return; }
      location.href = langUrl(sel.value) + (translated ? location.hash : '');
    });
    // On a small phone the search box needs the room: the picker shows only a short code ("DE", "日本") and the
    // select lies invisibly on top of it, so a tap still opens the full list with the native names.
    var face = document.createElement('span');
    face.className = 'lang-face';
    face.setAttribute('aria-hidden', 'true');
    var shortNames = { 'ja': '日本', 'ko': '한국', 'zh-Hans': '简', 'zh-Hant': '繁', 'sr-Cyrl': 'СР', 'sr-Latn': 'SR' };
    face.textContent = (shortNames[lang] || lang.split('-')[0].toUpperCase()) + ' ▾';
    slot.appendChild(face);
    slot.appendChild(sel);
  }
  function note(html1) {
    var head = document.querySelector('.page-head');
    if (!head || document.querySelector('.lang-note')) return null;
    var d = document.createElement('div');
    d.className = 'callout note lang-note';
    d.innerHTML = html1;
    head.parentNode.insertBefore(d, head.nextSibling);
    return d;
  }
  function esc(s) { return String(s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
  // An English-only page (command line, reference, release notes) says so in the reader's language.
  function showEnglishOnly(code) {
    if (!code || code === 'en') return;
    var n = note('<p></p>');
    if (n) {
      n.firstChild.textContent = tr('This page is only in English.', code) + ' ';
      var a = document.createElement('a'); a.href = root + code + '/index.html'; a.textContent = I18N.langs.filter(function (l) { return l[0] === code; }).map(function (l) { return l[1]; })[0] || code;
      n.firstChild.appendChild(a);
    }
  }
  var chosen = load('jp-lang');
  if (lang === 'en' && !translated) showEnglishOnly(chosen);
  // On the English version of a translated page: offer the reader's language once (from the browser), never redirect.
  if (lang === 'en' && translated && !chosen && load('jp-lang-offer') !== 'no') {
    var wanted = navigator.languages || [navigator.language || ''];
    var match = null;
    for (var wi = 0; wi < wanted.length && !match; wi++) {
      var lw = String(wanted[wi]).toLowerCase(), primary = lw.split('-')[0];
      if (primary === 'en') break;
      var code = primary;
      if (primary === 'zh') code = /tw|hk|mo|hant/.test(lw) ? 'zh-Hant' : 'zh-Hans';
      else if (primary === 'sr') code = /latn/.test(lw) ? 'sr-Latn' : 'sr-Cyrl';
      else if (primary === 'no' || primary === 'nn') code = 'nb';
      match = I18N.langs.filter(function (l) { return l[0] === code; })[0] || null;
    }
    if (match && match[0] !== 'en') {
      var n2 = note('<p></p>');
      if (n2) {
        var p = n2.firstChild;
        p.textContent = tr('This page is also in your language:', match[0]) + ' ';
        var go = document.createElement('a'); go.href = langUrl(match[0]) + location.hash; go.textContent = tr('Open it', match[0]) + ' (' + match[1] + ')';
        go.addEventListener('click', function () { store('jp-lang', match[0]); });
        var no = document.createElement('button'); no.className = 'link-btn'; no.textContent = tr('No thanks', match[0]);
        no.addEventListener('click', function () { store('jp-lang-offer', 'no'); n2.remove(); });
        p.appendChild(go); p.appendChild(document.createTextNode(' · ')); p.appendChild(no);
      }
    }
  }
  if (lang !== 'en') store('jp-lang', lang);

  // Phone menu
  var menuBtn = document.querySelector('.menu-btn');
  if (menuBtn) menuBtn.addEventListener('click', function () {
    var open = document.body.classList.toggle('nav-open');
    menuBtn.setAttribute('aria-expanded', open ? 'true' : 'false');
  });

  // Detail level (beginner / advanced / technical / all): one choice for the whole site, Beginner on the first visit
  // (set in the page head before anything is drawn), remembered in jp-layer.
  function level() { return html.getAttribute('data-show') || 'all'; }
  var switcher = document.querySelector('.layer-switch');
  if (switcher) {
    var apply = function (lv) {
      if (lv === 'all') html.removeAttribute('data-show'); else html.setAttribute('data-show', lv);
      switcher.querySelectorAll('button').forEach(function (b) { b.classList.toggle('on', b.getAttribute('data-show') === lv); });
      // "On this page" lists only the headings of the shown level.
      document.querySelectorAll('.toc a[href^="#"]').forEach(function (a) {
        var target = document.getElementById(decodeURIComponent(a.getAttribute('href').slice(1)));
        var layer = target && target.closest ? target.closest('.layer') : null;
        a.parentNode.hidden = !!(layer && lv !== 'all' && !layer.classList.contains('layer-' + lv));
      });
    };
    switcher.addEventListener('click', function (e) {
      var lv = e.target && e.target.getAttribute && e.target.getAttribute('data-show');
      if (!lv) return;
      apply(lv);
      store('jp-layer', lv);
    });
    apply(level());
  }
  // A link to a spot inside a layer the current level hides: show everything, so the reader lands on it.
  function reveal() {
    var id = decodeURIComponent((location.hash || '').slice(1));
    var el = id && document.getElementById(id);
    if (el && el.closest && el.closest('.layer') && el.offsetParent === null && switcher) {
      apply('all');
      el.scrollIntoView();
    }
  }
  reveal();
  window.addEventListener('hashchange', reveal);

  // Diagrams. On a phone a left-to-right flowchart turns top-down, so it fits the width and keeps its text size
  // (wider diagrams are scaled to the column by the CSS instead of scrolling sideways).
  if (window.mermaid && window.matchMedia && window.matchMedia('(max-width: 700px)').matches) {
    document.querySelectorAll('pre.mermaid').forEach(function (pre) {
      var src = pre.textContent;
      var turned = src.replace(/^(\s*(?:flowchart|graph))\s+(LR|RL)(?=\s)/, '$1 TD');
      if (turned !== src) pre.textContent = turned;
    });
    // Fitted to the width a big diagram gets small: a tap shows it at its own size (scrolls sideways), another fits it again.
    document.querySelectorAll('.diagram').forEach(function (d) {
      d.setAttribute('title', tr('Tap to enlarge'));
      d.setAttribute('data-tap', tr('Tap to enlarge'));
      d.setAttribute('data-tap-fit', tr('Tap to fit'));
      d.addEventListener('click', function () { d.classList.toggle('zoom'); });
    });
  }
  // A game page's support card starts folded on a phone (it is long there; the title and level stay visible).
  if (window.matchMedia && window.matchMedia('(max-width: 700px)').matches) {
    document.querySelectorAll('details.support-card[open]').forEach(function (d) { d.removeAttribute('open'); });
  }
  // Mermaid: the 'base' theme coloured from the site's tokens (site.css :root), HTML labels (they wrap and keep the
  // page font), straight lines and room between boxes. Authoring rules: website/README.md, "Diagrams".
  if (window.mermaid) {
    var css = getComputedStyle(html);
    var tok = function (name, fallback) { return (css.getPropertyValue(name) || '').trim() || fallback; };
    var dark = isDark();
    var font = tok('--font', 'Segoe UI, system-ui, sans-serif');
    window.mermaid.initialize({ startOnLoad: true, securityLevel: 'strict', theme: 'base', fontFamily: font,
      themeVariables: {
        darkMode: dark, fontFamily: font, fontSize: '16px', background: tok('--surface', '#fff'),
        primaryColor: tok('--surface-2', '#F6F8FB'), primaryTextColor: tok('--text', '#0F172A'), primaryBorderColor: tok('--accent', '#2563EB'),
        secondaryColor: tok('--surface', '#fff'), secondaryTextColor: tok('--text', '#0F172A'), secondaryBorderColor: tok('--border', '#D5DCE6'),
        tertiaryColor: tok('--surface', '#fff'), tertiaryTextColor: tok('--text', '#0F172A'), tertiaryBorderColor: tok('--border', '#D5DCE6'),
        lineColor: tok('--line', '#475569'), textColor: tok('--text', '#0F172A'), mainBkg: tok('--surface-2', '#F6F8FB'), nodeBorder: tok('--accent', '#2563EB'),
        clusterBkg: tok('--bg', '#EEF2F7'), clusterBorder: tok('--border', '#D5DCE6'), titleColor: tok('--text', '#0F172A'),
        edgeLabelBackground: tok('--surface', '#fff'), nodeTextColor: tok('--text', '#0F172A'),
        actorBkg: tok('--surface-2', '#F6F8FB'), actorBorder: tok('--accent', '#2563EB'), actorTextColor: tok('--text', '#0F172A'),
        actorLineColor: tok('--line', '#475569'), signalColor: tok('--line', '#475569'), signalTextColor: tok('--text', '#0F172A'),
        labelBoxBkgColor: tok('--surface-2', '#F6F8FB'), labelBoxBorderColor: tok('--border', '#D5DCE6'), labelTextColor: tok('--text', '#0F172A'),
        loopTextColor: tok('--text', '#0F172A'), noteBkgColor: tok('--surface', '#fff'), noteTextColor: tok('--text', '#0F172A'),
        noteBorderColor: tok('--accent', '#2563EB'), activationBkgColor: tok('--surface', '#fff'), activationBorderColor: tok('--accent', '#2563EB'),
        sequenceNumberColor: tok('--surface', '#fff')
      },
      flowchart: { htmlLabels: true, curve: 'linear', nodeSpacing: 40, rankSpacing: 56, padding: 10, diagramPadding: 8, useMaxWidth: true },
      sequence: { useMaxWidth: true, boxMargin: 8, messageMargin: 32, mirrorActors: false } });
  }

  // Search: the index is a script (no fetch, so it works without a server), loaded on first use.
  var input = document.getElementById('q');
  // A phone's search box is narrow: the short word instead of the long hint, so it is not cut off.
  if (input && window.matchMedia && window.matchMedia('(max-width: 700px)').matches && input.getAttribute('aria-label')) {
    input.placeholder = input.getAttribute('aria-label');
  }
  var box = document.getElementById('results');
  var loading = false;
  function ensureIndex(then) {
    if (window.JP_SEARCH) { then(); return; }
    if (loading) return;
    loading = true;
    var s = document.createElement('script');
    s.src = root + 'assets/search-index' + (lang === 'en' ? '' : '.' + lang) + '.js';
    s.onload = then;
    document.head.appendChild(s);
  }
  function norm(t) { return (t || '').toLowerCase(); }
  function run() {
    var q = norm(input.value).trim();
    var cjk = /[぀-ヿ㐀-鿿가-힯豈-﫿]/;
    if (q.length < 2 && !(q.length === 1 && cjk.test(q))) { box.hidden = true; box.innerHTML = ''; return; }
    // Japanese, Chinese and Korean write without spaces: a run of their characters is searched as its character
    // pairs (bigrams), each of which must appear; other words as they are.
    var words = [];
    q.split(/\s+/).forEach(function (w) {
      var parts = w.split(/([぀-ヿ㐀-鿿가-힯豈-﫿]+)/);
      parts.forEach(function (p) {
        if (!p) return;
        if (cjk.test(p) && p.length > 2) { for (var k = 0; k + 1 < p.length; k++) words.push(p.substr(k, 2)); }
        else words.push(p);
      });
    });
    var hits = [];
    var idx = window.JP_SEARCH || [];
    // At the Beginner level the command line stays out of the results, unless the search asks for it.
    var noCli = level() === 'beginner' && !/engine|--/.test(q);
    for (var i = 0; i < idx.length; i++) {
      var e = idx[i], t = norm(e.t), x = norm(e.x), s = norm(e.s), score = 0, ok = true;
      if (noCli && e.s === 'Engine (CLI)') continue;
      for (var w = 0; w < words.length; w++) {
        var word = words[w];
        if (t.indexOf(word) >= 0) score += t.indexOf(word) === 0 ? 12 : 8;
        else if (s.indexOf(word) >= 0) score += 3;
        else if (x.indexOf(word) >= 0) score += 2;
        // A character pair typed without the space the text has (Korean "가상의컨트롤러" for "가상의 컨트롤러"): the text
        // without its spaces, made once per entry.
        else if (cjk.test(word) && (e._q || (e._q = (t + '|' + s + '|' + x).replace(/\s+/g, ''))).indexOf(word) >= 0) score += 2;
        else { ok = false; break; }
      }
      if (!ok) continue;
      if (t === q) score += 20;
      if (e.u.indexOf('#') < 0) score += 2;
      hits.push([score, e]);
    }
    hits.sort(function (a, b) { return b[0] - a[0]; });
    box.innerHTML = '';
    hits.slice(0, 25).forEach(function (h) {
      var e = h[1], a = document.createElement('a');
      a.href = root + e.u;
      var t = document.createElement('span'); t.className = 'r-t'; t.textContent = e.t;
      var s = document.createElement('span'); s.className = 'r-s'; s.textContent = '  ' + e.s;
      a.appendChild(t); a.appendChild(s);
      if (e.x) { var x = document.createElement('span'); x.className = 'r-x'; x.textContent = e.x; a.appendChild(x); }
      box.appendChild(a);
    });
    if (!hits.length) { var n = document.createElement('div'); n.className = 'r-s'; n.style.padding = '8px'; n.textContent = tr('Nothing found.'); box.appendChild(n); }
    box.hidden = false;
  }
  if (input) {
    input.addEventListener('focus', function () { ensureIndex(function () { }); });
    input.addEventListener('input', function () { ensureIndex(run); });
    input.addEventListener('keydown', function (e) {
      var links = box.querySelectorAll('a'), cur = box.querySelector('a.sel'), i = Array.prototype.indexOf.call(links, cur);
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        if (cur) cur.classList.remove('sel');
        i = e.key === 'ArrowDown' ? Math.min(links.length - 1, i + 1) : Math.max(0, i - 1);
        if (links[i]) { links[i].classList.add('sel'); links[i].scrollIntoView({ block: 'nearest' }); }
      } else if (e.key === 'Enter') {
        var go = cur || links[0];
        if (go) location.href = go.href;
      } else if (e.key === 'Escape') { box.hidden = true; input.blur(); }
    });
    document.addEventListener('click', function (e) { if (!box.contains(e.target) && e.target !== input) box.hidden = true; });
    document.addEventListener('keydown', function (e) {
      if (e.key === '/' && document.activeElement !== input && !/input|textarea/i.test(document.activeElement.tagName)) { e.preventDefault(); input.focus(); }
    });
  }
})();
