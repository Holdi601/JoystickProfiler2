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

  // Phone menu
  var menuBtn = document.querySelector('.menu-btn');
  if (menuBtn) menuBtn.addEventListener('click', function () {
    var open = document.body.classList.toggle('nav-open');
    menuBtn.setAttribute('aria-expanded', open ? 'true' : 'false');
  });

  // Detail level (beginner / advanced / technical)
  var switcher = document.querySelector('.layer-switch');
  if (switcher) {
    var apply = function (level) {
      if (level === 'all') document.body.removeAttribute('data-show'); else document.body.setAttribute('data-show', level);
      switcher.querySelectorAll('button').forEach(function (b) { b.classList.toggle('on', b.getAttribute('data-show') === level); });
    };
    switcher.addEventListener('click', function (e) {
      var level = e.target && e.target.getAttribute && e.target.getAttribute('data-show');
      if (!level) return;
      apply(level);
      store('jp-layer', level);
    });
    var saved = load('jp-layer');
    if (saved) apply(saved);
  }

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
      d.setAttribute('title', 'Tap to enlarge');
      d.addEventListener('click', function () { d.classList.toggle('zoom'); });
    });
  }
  // A game page's support card starts folded on a phone (it is long there; the title and level stay visible).
  if (window.matchMedia && window.matchMedia('(max-width: 700px)').matches) {
    document.querySelectorAll('details.support-card[open]').forEach(function (d) { d.removeAttribute('open'); });
  }
  if (window.mermaid) {
    window.mermaid.initialize({ startOnLoad: true, securityLevel: 'strict', theme: isDark() ? 'dark' : 'default',
      flowchart: { htmlLabels: false, curve: 'basis', nodeSpacing: 24, rankSpacing: 32, padding: 6, diagramPadding: 4 },
      sequence: { useMaxWidth: true, boxMargin: 6, messageMargin: 28 },
      themeVariables: { fontSize: '15px' }, fontFamily: 'Segoe UI, system-ui, sans-serif' });
  }

  // Search: the index is a script (no fetch, so it works without a server), loaded on first use.
  var input = document.getElementById('q');
  var box = document.getElementById('results');
  var loading = false;
  function ensureIndex(then) {
    if (window.JP_SEARCH) { then(); return; }
    if (loading) return;
    loading = true;
    var s = document.createElement('script');
    s.src = root + 'assets/search-index.js';
    s.onload = then;
    document.head.appendChild(s);
  }
  function norm(t) { return (t || '').toLowerCase(); }
  function run() {
    var q = norm(input.value).trim();
    if (q.length < 2) { box.hidden = true; box.innerHTML = ''; return; }
    var words = q.split(/\s+/);
    var hits = [];
    var idx = window.JP_SEARCH || [];
    for (var i = 0; i < idx.length; i++) {
      var e = idx[i], t = norm(e.t), x = norm(e.x), s = norm(e.s), score = 0, ok = true;
      for (var w = 0; w < words.length; w++) {
        var word = words[w];
        if (t.indexOf(word) >= 0) score += t.indexOf(word) === 0 ? 12 : 8;
        else if (s.indexOf(word) >= 0) score += 3;
        else if (x.indexOf(word) >= 0) score += 2;
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
    if (!hits.length) { var n = document.createElement('div'); n.className = 'r-s'; n.style.padding = '8px'; n.textContent = 'Nothing found.'; box.appendChild(n); }
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
