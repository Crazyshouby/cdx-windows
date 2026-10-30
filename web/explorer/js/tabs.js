// Les onglets : état, ajout, navigation d'un onglet, barre des onglets.
  // ---------------- onglets ----------------
  let tabs = [], cur = 0, nextId = 1, places = null, clip = { paths: [], cut: false };
  const T = () => tabs[cur];
  function addTab(path) { tabs.push({ id: String(nextId++), path: null, hist: [], idx: -1, items: [], sel: new Set(), sortK: 'n', rev: false, filter: '', mode: 'push', anchor: -1, free: -1, drives: false, ver: 0, loading: false }); cur = tabs.length - 1; go(path, 'push'); drawTabs(); }
  function go(path, mode) { const t = T(); t.mode = mode || 'push'; t.want = path; post({ cmd: 'fs.list', path, tab: t.id }); }
  function drawTabs() {
    $('tabs').innerHTML = tabs.map((t, i) => `<button class="tab ${i === cur ? 'on' : ''}" data-i="${i}">${esc(baseOf(t.path ?? t.want ?? ''))}</button>`).join('') + '<button class="tab" data-i="+">+</button>';
  }
  $('tabs').onclick = e => { const b = e.target.closest('[data-i]'); if (!b) return; if (b.dataset.i === '+') addTab(T().path ?? ''); else { cur = +b.dataset.i; drawTabs(); show(); } };
  $('tabs').onauxclick = e => { const b = e.target.closest('[data-i]'); if (b && e.button === 1 && b.dataset.i !== '+' && tabs.length > 1) { tabs.splice(+b.dataset.i, 1); cur = Math.min(cur, tabs.length - 1); drawTabs(); show(); } };

