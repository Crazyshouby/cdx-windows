// Les onglets : état, ajout, navigation d'un onglet, barre des onglets ; réordonner en glissant, dupliquer, fermer les autres, rouvrir le dernier fermé, détacher dans
// une nouvelle fenêtre (lâcher l'onglet hors de la fenêtre) ; la session (onglets ouverts, onglet actif) est gardée pour la prochaine fenêtre ouverte sans dossier.
  // ---------------- onglets ----------------
  let tabs = [], cur = 0, nextId = 1, places = null, clip = { paths: [], cut: false };
  const closedTabs = [];                      // chemins des onglets fermés, le dernier à la fin (Ctrl+Maj+T les rouvre)
  const T = () => tabs[cur];
  const newTabState = () => ({ id: String(nextId++), path: null, hist: [], idx: -1, items: [], sel: new Set(), sortK: 'n', rev: false, filter: '', mode: 'push', anchor: -1, free: -1, drives: false, ver: 0, loading: false });
  // Ajoute un onglet (à la fin, ou à la place `at`) et y va.
  function addTab(path, at) { const t = newTabState(); const i = at == null ? tabs.length : Math.max(0, Math.min(tabs.length, at)); tabs.splice(i, 0, t); cur = i; go(path, 'push'); drawTabs(); return t; }
  function go(path, mode) { const t = T(); if (t.search) endSearch(false); t.mode = mode || 'push'; t.want = path; post({ cmd: 'fs.list', path, tab: t.id }); }
  function selectTab(i) { if (i < 0 || i >= tabs.length) return; cur = i; drawTabs(); show(); if (T().stale) { T().stale = false; reload(); } saveSession(); }
  function closeTabAt(i, force) {
    if (i < 0 || i >= tabs.length || (!force && tabs.length <= 1)) return;
    const t = tabs[i]; post({ cmd: 'fs.watch', tab: t.id, path: '' });
    if (t.path) { closedTabs.push(t.path); if (closedTabs.length > 20) closedTabs.shift(); }
    tabs.splice(i, 1);
    if (!tabs.length) { addTab(places ? places.home : ''); return; }
    cur = cur > i ? cur - 1 : Math.min(cur, tabs.length - 1);
    drawTabs(); show(); if (T().stale) { T().stale = false; reload(); } saveSession();
  }
  function moveTab(from, to) { if (from === to || from < 0 || from >= tabs.length) return; to = Math.max(0, Math.min(tabs.length - 1, to)); const [t] = tabs.splice(from, 1); tabs.splice(to, 0, t); cur = to; drawTabs(); saveSession(); }
  const duplicateTab = i => addTab(tabs[i].path ?? '', i + 1);
  function closeOthers(i) { const keep = tabs[i]; if (!keep) return; for (const t of tabs) if (t !== keep) { post({ cmd: 'fs.watch', tab: t.id, path: '' }); if (t.path) closedTabs.push(t.path); } tabs = [keep]; cur = 0; drawTabs(); show(); saveSession(); }
  function closeRight(i) { while (tabs.length > i + 1) closeTabAt(tabs.length - 1, true); }
  function reopenClosed() { const p = closedTabs.pop(); if (p != null) addTab(p); }
  function detachTab(i) { const t = tabs[i]; if (!t || !t.path) return; post({ cmd: 'fs.newwindow', path: t.path }); if (tabs.length > 1) closeTabAt(i); }

  // La session : les chemins des onglets et l'onglet actif (réglage « session »), retrouvés par la prochaine fenêtre ouverte sans dossier (restore=1).
  let sessionTimer = 0;
  function saveSession() {
    clearTimeout(sessionTimer);
    sessionTimer = setTimeout(() => { const paths = tabs.map(t => t.path).filter(p => typeof p === 'string'); if (paths.length) setSetting('session', { tabs: paths, cur: Math.max(0, Math.min(paths.length - 1, cur)) }); }, 400);
  }

  function drawTabs() {
    $('tabs').innerHTML = tabs.map((t, i) => `<button class="tab ${i === cur ? 'on' : ''}" data-i="${i}" title="${esc(t.path ?? '')}">${esc(baseOf(t.path ?? t.want ?? ''))}</button>`).join('') + '<button class="tab" data-i="+" title="Nouvel onglet (Ctrl+T)">+</button>';
  }
  $('tabs').onclick = e => { const b = e.target.closest('[data-i]'); if (tabDragged) { tabDragged = false; return; } if (!b) return; if (b.dataset.i === '+') addTab(T().path ?? ''); else selectTab(+b.dataset.i); };
  $('tabs').onauxclick = e => { const b = e.target.closest('[data-i]'); if (b && e.button === 1 && b.dataset.i !== '+') closeTabAt(+b.dataset.i); };
  const tabMenu = i => [
    ['Nouvel onglet', () => addTab(tabs[i].path ?? '', i + 1), { key: 'Ctrl+T' }],
    ['Dupliquer', () => duplicateTab(i)],
    ['Détacher dans une nouvelle fenêtre', () => detachTab(i), { disabled: !tabs[i].path }], null,
    ['Fermer', () => closeTabAt(i), { key: 'Ctrl+W', disabled: tabs.length <= 1 }],
    ['Fermer les autres', () => closeOthers(i), { disabled: tabs.length <= 1 }],
    ['Fermer ceux de droite', () => closeRight(i), { disabled: i >= tabs.length - 1 }], null,
    ['Rouvrir l’onglet fermé', reopenClosed, { key: 'Ctrl+Maj+T', disabled: !closedTabs.length }],
  ];
  $('tabs').oncontextmenu = e => { const b = e.target.closest('[data-i]'); if (!b || b.dataset.i === '+') return; e.preventDefault(); const i = +b.dataset.i; CdxFrame.menu(tabMenu(i), e.clientX, e.clientY, baseOf(tabs[i].path ?? '')); };

  // Réordonner en glissant un onglet (6 px avant que ce soit un glisser) ; lâché hors de la fenêtre, l'onglet part dans une nouvelle fenêtre.
  let tabDrag = null, tabDragged = false;
  $('tabs').addEventListener('pointerdown', e => { const b = e.target.closest('[data-i]'); if (e.button !== 0 || !b || b.dataset.i === '+') return; tabDrag = { i: +b.dataset.i, x: e.clientX, y: e.clientY, on: false, to: -1, out: false }; });
  document.addEventListener('pointermove', e => {
    if (!tabDrag) return; if (!(e.buttons & 1)) { endTabDrag(false); return; }
    if (!tabDrag.on) { if (Math.hypot(e.clientX - tabDrag.x, e.clientY - tabDrag.y) < 6) return; tabDrag.on = true; document.body.classList.add('tabdrag'); }
    const bs = [...$('tabs').querySelectorAll('[data-i]')].filter(b => b.dataset.i !== '+');
    let to = bs.length - 1; for (let k = 0; k < bs.length; k++) { const r = bs[k].getBoundingClientRect(); if (e.clientX < r.left + r.width / 2) { to = k; break; } }
    tabDrag.to = to; bs.forEach((b, k) => { b.classList.toggle('ins', k === to && k !== tabDrag.i); b.classList.toggle('drag', k === tabDrag.i); });
    tabDrag.out = e.clientX < 0 || e.clientY < 0 || e.clientX >= innerWidth || e.clientY >= innerHeight;
  });
  function endTabDrag(drop) {
    const d = tabDrag; tabDrag = null; document.body.classList.remove('tabdrag');
    $('tabs').querySelectorAll('.ins, .drag').forEach(b => b.classList.remove('ins', 'drag'));
    if (!d || !d.on) return; tabDragged = true;
    if (drop && d.out) { detachTab(d.i); return; }
    if (drop && d.to >= 0 && d.to !== d.i) moveTab(d.i, d.to);
  }
  document.addEventListener('pointerup', () => { if (tabDrag) endTabDrag(true); });
  document.addEventListener('pointercancel', () => { if (tabDrag) endTabDrag(false); });

  // ---------------- accès rapide : dossiers épinglés, fréquents et récents (réglages pins et recent, communs à toutes les fenêtres) ----------------
  const pinList = () => (Array.isArray(settings.pins) ? settings.pins : []).filter(p => typeof p === 'string');
  const isPinned = p => !!p && pinList().some(x => x.toLowerCase() === p.toLowerCase());
  function togglePin(p) { if (!p || p === TRASH) return; const l = pinList(); setSetting('pins', isPinned(p) ? l.filter(x => x.toLowerCase() !== p.toLowerCase()) : [...l, p].slice(-30)); }
  const recentList = () => (Array.isArray(settings.recent) ? settings.recent : []).filter(r => r && typeof r.p === 'string');
  // Un dossier affiché compte une visite de plus ; les 40 plus récents sont gardés.
  function bumpRecent(p) {
    if (!p || p === TRASH || !tabs.length) return;
    const now = Date.now(), l = recentList().filter(r => r.p.toLowerCase() !== p.toLowerCase()); const old = recentList().find(r => r.p.toLowerCase() === p.toLowerCase());
    l.push({ p, n: (old ? old.n | 0 : 0) + 1, at: now });
    setSetting('recent', l.sort((a, b) => b.at - a.at).slice(0, 40));
  }
  function forgetRecent(p) { setSetting('recent', recentList().filter(r => r.p.toLowerCase() !== p.toLowerCase())); }
