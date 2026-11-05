// Le script de navigateur.html, sorti de la page pour que sa politique de sécurité (CSP) n'autorise aucun script en ligne.
  fillPix();
  const $ = id => document.getElementById(id);
  let st = { active: 0, split: 0, left: 0, items: [], groups: [], closed: 0, windows: 1 }, hover = '';
  const cur = () => st.items.find(t => t.id === st.active);
  const groupOf = t => st.groups.find(g => g.id === t.group);
  const COLORS = [['accent', 'Couleur d’accent'], ['rouge', 'Rouge'], ['ambre', 'Ambre'], ['vert', 'Vert'], ['gris', 'Gris']];

  // Les pages web sont des vues natives posées sur la page : un menu ou une boîte passerait dessous. Le programme les masque le temps de l'ouverture (win.overlay).
  function overlayWhile(open) { post({ cmd: 'win.overlay', on: true }); const watch = new MutationObserver(() => { if (!open()) { watch.disconnect(); post({ cmd: 'win.overlay', on: false }); } }); watch.observe(document.body, { childList: true }); }
  function menu(items, x, y, title) { CdxFrame.menu(items, x, y, title); overlayWhile(() => document.querySelector('.menu[data-cdx]')); }
  // Une question avec une zone de saisie (nom d'un groupe) : rend le texte, ou null.
  function ask(title, placeholder, value = '') {
    return new Promise(resolve => {
      const v = document.createElement('div'); v.className = 'veil';
      v.innerHTML = `<div class="win active"><div class="tb"><span class="tb-title">${esc(title)}</span></div><div class="cb"><input class="text-input" id="askIn" placeholder="${esc(placeholder)}" spellcheck="false" autocomplete="off" maxlength="40"><div class="btns"><button class="primary" id="askOk">OK</button><button class="btn" id="askNo">Annuler</button></div></div></div>`;
      document.body.appendChild(v); const inp = v.querySelector('#askIn'); inp.value = value; inp.focus(); inp.select();
      const done = r => { v.remove(); resolve(r); };
      v.querySelector('#askOk').onclick = () => done(inp.value.trim()); v.querySelector('#askNo').onclick = () => done(null);
      v.onkeydown = e => { e.stopPropagation(); if (e.key === 'Escape') done(null); else if (e.key === 'Enter') done(inp.value.trim()); };
      overlayWhile(() => v.isConnected);
    });
  }

  function renderTabs() {
    let html = '', lastGroup = 0;
    for (const t of st.items) {
      const g = groupOf(t);
      if (g && g.id !== lastGroup) { const n = st.items.filter(x => x.group === g.id).length; html += `<span class="grp c-${g.color} ${g.collapsed ? 'collapsed' : ''}" data-g="${g.id}" title="${esc(g.name || 'Groupe')} · ${n} onglet${n > 1 ? 's' : ''} (clic : replier ou déplier)"><i></i>${esc(g.name || '')}<span class="n">${g.collapsed || !g.name ? n : ''}</span></span>`; }
      lastGroup = g ? g.id : 0;
      if (g && g.collapsed) continue;
      const other = st.split && (t.id === st.split || t.id === st.left) && t.id !== st.active;
      html += `<button class="tab ${t.id === st.active ? 'on' : ''} ${g ? 'g c-' + g.color : ''} ${other ? 'sp' : ''}" data-id="${t.id}" title="${esc(t.title)}">${t.favicon ? `<img src="data:image/png;base64,${t.favicon}" alt="">` : `<span class="pix">${pix(t.internalPage ? 'messages' : 'serveur', 16)}</span>`}<span class="tt">${esc(t.loading ? 'Chargement… ' + t.title : t.title)}</span><span class="x" data-x="${t.id}" title="Fermer (Ctrl+W)">×</span></button>`;
    }
    $('tabs').innerHTML = html + '<button class="tab new" data-new title="Nouvel onglet (Ctrl+T)">+</button>';
  }
  function renderBar() {
    const t = cur(); if (!t) return;
    $('back').disabled = !t.canBack; $('fwd').disabled = !t.canFwd;
    $('reload').innerHTML = `<span class="pix">${pix(t.loading ? 'erreur' : 'maj', 16)}</span>`; $('reload').title = t.loading ? 'Arrêter' : 'Actualiser (F5)';
    if (document.activeElement !== $('addr')) $('addr').value = t.url;
    $('star').classList.toggle('on', !!t.starred); $('star').disabled = !t.url;
    $('ttl').textContent = t.title + ' — Navigateur';
    $('orb').className = 'orb ' + (t.loading ? 'amber pulse' : 'green');
    $('st').textContent = hover || (t.loading ? 'Chargement…' : 'Terminé');
    $('zoom').textContent = t.zoom !== 100 ? `Zoom ${t.zoom} %` : '';
    const split = !!st.split; if ($('vpB').hidden === split) { $('vpB').hidden = !split; layout(); }
  }
  handlers.tabs = m => { st = m; renderTabs(); renderBar(); };
  handlers.status = m => { hover = m.text; const t = cur(); if (t) $('st').textContent = hover || (t.loading ? 'Chargement…' : 'Terminé'); };
  handlers.downloads = m => { $('dlb').textContent = m.active ? ' ' + m.active : ''; };
  handlers.focusAddr = () => { $('addr').focus(); $('addr').select(); };
  handlers.nav = m => post({ cmd: m.dir === 'back' ? 'br.back' : 'br.fwd' });   // boutons latéraux de la souris (crochet du shell)

  $('tabs').onclick = e => {
    const x = e.target.closest('[data-x]'); if (x) { post({ cmd: 'br.close', id: +x.dataset.x }); return; }
    if (e.target.closest('[data-new]')) { post({ cmd: 'br.newtab' }); return; }
    const g = e.target.closest('[data-g]'); if (g) { const grp = st.groups.find(k => k.id === +g.dataset.g); post({ cmd: 'br.group', group: +g.dataset.g, what: 'collapse', on: !(grp && grp.collapsed) }); return; }
    const t = e.target.closest('[data-id]'); if (t) post({ cmd: 'br.select', id: +t.dataset.id });
  };
  $('tabs').onauxclick = e => { const t = e.target.closest('[data-id]'); if (e.button === 1 && t) post({ cmd: 'br.close', id: +t.dataset.id }); };
  // ---- menus contextuels : un onglet, la pastille d'un groupe, le fond de la barre d'onglets ----
  async function newGroupWith(id) { const name = await ask('Nouveau groupe', 'Nom du groupe (facultatif)'); if (name !== null) post({ cmd: 'br.group.new', ids: [id], name, color: 'accent' }); }
  function tabMenu(t, x, y) {
    const g = groupOf(t); const others = st.groups.filter(k => k.id !== t.group);
    const groupItems = [['Nouveau groupe…', () => newGroupWith(t.id)], ...(others.length ? [null, ...others.map(k => [k.name || 'Groupe sans nom', () => post({ cmd: 'br.group.add', id: t.id, group: k.id })])] : [])];
    menu([
      ['Nouvel onglet', () => post({ cmd: 'br.newtab' }), { key: 'Ctrl+T' }],
      ['Dupliquer l’onglet', () => post({ cmd: 'br.duplicate', id: t.id })],
      ['Ouvrir dans une nouvelle fenêtre', () => post({ cmd: 'br.towindow', id: t.id }), { disabled: st.items.length < 2 }],
      null,
      ['Ajouter à un groupe', null, { sub: groupItems }],
      ...(g ? [['Retirer du groupe', () => post({ cmd: 'br.group.leave', id: t.id })]] : []),
      null,
      ...(st.split ? [['Réunir les pages', () => post({ cmd: 'br.unsplit' }), { key: 'Ctrl+\\' }]] : [['Afficher à côté (pages scindées)', () => post({ cmd: 'br.split', id: t.id }), { key: 'Ctrl+\\', disabled: t.id === st.active && st.items.length < 2 }]]),
      null,
      ['Rouvrir l’onglet fermé', () => post({ cmd: 'br.reopen' }), { key: 'Ctrl+Maj+T', disabled: !st.closed }],
      ['Fermer les autres onglets', () => post({ cmd: 'br.closeothers', id: t.id }), { disabled: st.items.length < 2 }],
      ['Fermer l’onglet', () => post({ cmd: 'br.close', id: t.id }), { key: 'Ctrl+W' }],
    ], x, y);
  }
  async function groupMenu(g, x, y) {
    menu([
      [g.collapsed ? 'Déplier' : 'Replier', () => post({ cmd: 'br.group', group: g.id, what: 'collapse', on: !g.collapsed })],
      ['Renommer…', async () => { const name = await ask('Nom du groupe', 'Nom', g.name); if (name !== null) post({ cmd: 'br.group', group: g.id, what: 'rename', name }); }],
      ['Couleur', null, { sub: COLORS.map(([c, n]) => [n, () => post({ cmd: 'br.group', group: g.id, what: 'color', color: c }), { check: g.color === c }]) }],
      null,
      ['Dégrouper', () => post({ cmd: 'br.group', group: g.id, what: 'ungroup' })],
      ['Fermer le groupe', () => post({ cmd: 'br.group', group: g.id, what: 'close' })],
    ], x, y, g.name || 'Groupe');
  }
  $('tabs').oncontextmenu = e => {
    e.preventDefault();
    const gEl = e.target.closest('[data-g]'); if (gEl) { const g = st.groups.find(k => k.id === +gEl.dataset.g); if (g) groupMenu(g, e.clientX, e.clientY); return; }
    const tEl = e.target.closest('[data-id]'); if (tEl) { const t = st.items.find(k => k.id === +tEl.dataset.id); if (t) tabMenu(t, e.clientX, e.clientY); return; }
    menu([['Nouvel onglet', () => post({ cmd: 'br.newtab' }), { key: 'Ctrl+T' }], ['Rouvrir l’onglet fermé', () => post({ cmd: 'br.reopen' }), { key: 'Ctrl+Maj+T', disabled: !st.closed }], null, ['Nouvelle fenêtre', () => post({ cmd: 'br.newwindow' }), { key: 'Ctrl+N' }]], e.clientX, e.clientY);
  };
  $('back').onclick = () => post({ cmd: 'br.back' }); $('fwd').onclick = () => post({ cmd: 'br.fwd' });
  $('reload').onclick = () => post({ cmd: cur() && cur().loading ? 'br.stop' : 'br.reload' });
  $('star').onclick = () => post({ cmd: 'br.star' });
  $('favs').onclick = () => post({ cmd: 'br.internal', p: 'favoris' }); $('hist').onclick = () => post({ cmd: 'br.internal', p: 'historique' }); $('dl').onclick = () => post({ cmd: 'br.internal', p: 'telechargements' }); $('ext').onclick = () => post({ cmd: 'br.internal', p: 'extensions' });
  // Les réglages de la fenêtre (bouton hachuré de la barre de titre) : les pages internes du navigateur, les fenêtres, la fenêtre des transferts.
  windowSettings(() => [
    ['Favoris', () => post({ cmd: 'br.internal', p: 'favoris' })],
    ['Historique', () => post({ cmd: 'br.internal', p: 'historique' }), { key: 'Ctrl+H' }],
    ['Téléchargements', () => post({ cmd: 'br.internal', p: 'telechargements' }), { key: 'Ctrl+J' }],
    ['Extensions', () => post({ cmd: 'br.internal', p: 'extensions' })],
    ['Mots de passe', () => post({ cmd: 'br.internal', p: 'motsdepasse' })],
    ['Réglages du navigateur', () => post({ cmd: 'br.internal', p: 'reglages' })],
    null,
    ['Nouvelle fenêtre', () => post({ cmd: 'br.newwindow' }), { key: 'Ctrl+N' }],
    [st.split ? 'Réunir les pages' : 'Pages scindées (deux pages côte à côte)', () => post({ cmd: st.split ? 'br.unsplit' : 'br.split' }), { key: 'Ctrl+\\' }],
    ['Rouvrir l’onglet fermé', () => post({ cmd: 'br.reopen' }), { key: 'Ctrl+Maj+T', disabled: !st.closed }],
    null,
    ['Fenêtre des transferts', () => post({ cmd: 'br.xfers' })],
  ]);
  $('zoom').onclick = () => post({ cmd: 'br.zoom', d: 0 });
  $('addr').onkeydown = e => { e.stopPropagation(); if (e.key === 'Enter') { post({ cmd: 'br.go', url: $('addr').value }); } else if (e.key === 'Escape') { const t = cur(); $('addr').value = t ? t.url : ''; $('addr').blur(); post({ cmd: 'br.focus' }); } };
  $('addr').onfocus = () => $('addr').select();
  $('addr').onblur = () => renderBar();

  // ---- recherche dans la page ----
  let lastTerm = '';
  const doFind = (dir, first) => post({ cmd: 'br.find', text: $('find').value, dir, first: first ? 1 : 0 });
  handlers.openFind = () => { $('findbar').hidden = false; $('find').focus(); $('find').select(); post({ cmd: 'br.findOpen' }); };
  handlers.closeFind = () => { $('findbar').hidden = true; $('fcount').textContent = ''; };
  handlers.find = m => { $('fcount').textContent = m.count ? `${m.index + 1} / ${m.count}` : ($('find').value ? 'Aucun résultat' : ''); };
  $('find').oninput = () => { lastTerm = $('find').value; doFind(1, true); };
  $('find').onkeydown = e => { e.stopPropagation(); if (e.key === 'Enter') doFind(e.shiftKey ? -1 : 1, false); else if (e.key === 'Escape') post({ cmd: 'br.findClose' }); };
  $('fnext').onclick = () => doFind(1, false); $('fprev').onclick = () => doFind(-1, false); $('fclose').onclick = () => post({ cmd: 'br.findClose' });

  // ---- raccourcis (quand c'est le cadre qui a le focus ; la page web les envoie par le programme) ----
  document.addEventListener('keydown', e => {
    const inField = e.target.matches('input');
    if (e.key === 'Escape' && !$('findbar').hidden) { post({ cmd: 'br.findClose' }); return; }
    if (inField && e.ctrlKey && [65, 67, 86, 88, 90, 89].includes(e.keyCode)) return;   // couper, copier, coller restent à la zone de saisie
    if ((e.ctrlKey || e.altKey || (e.keyCode >= 112 && e.keyCode <= 123)) && e.keyCode !== 17 && e.keyCode !== 18 && e.keyCode !== 16) { e.preventDefault(); post({ cmd: 'br.shortcut', vk: e.keyCode }); }
  });

  // ---- cadre de la fenêtre ----
  CdxFrame.init();   // barre de titre, boutons et bords : cdx-frame.js (la barre se double-clique pour agrandir)
  CdxSplit.init({ pane: $('vpB'), side: 'right', key: 'navigateur-scinde', min: 200, max: 4000, minOther: 200 });   // la barre entre les deux pages scindées

  // ---- les rectangles des pages : le programme y pose les WebView2 des onglets (le second seulement quand les pages sont scindées) ----
  const rect = el => { const r = el.getBoundingClientRect(); return { x: Math.round(r.left) + 1, y: Math.round(r.top) + 1, w: Math.round(r.width) - 2, h: Math.round(r.height) - 2 }; };
  const layout = () => { const a = rect($('vpA')); const m = { cmd: 'br.layout', ...a }; if (!$('vpB').hidden) { const b = rect($('vpB')); m.x2 = b.x; m.y2 = b.y; m.w2 = b.w; m.h2 = b.h; } post(m); };
  new ResizeObserver(layout).observe($('vpA')); new ResizeObserver(layout).observe($('vpB')); addEventListener('resize', layout); addEventListener('load', layout);

  if (!hasHost) handlers.tabs({ active: 2, split: 0, left: 2, closed: 1, windows: 1, groups: [{ id: 1, name: 'Travail', color: 'vert', collapsed: false }], items: [{ id: 1, title: 'Nouvel onglet', url: '', favicon: '', canBack: false, canFwd: false, zoom: 100, internalPage: true, group: 0 }, { id: 2, title: 'Documentation WebView2', url: 'https://learn.microsoft.com/fr-fr/microsoft-edge/webview2/', favicon: '', loading: false, canBack: true, canFwd: false, zoom: 125, starred: true, group: 1 }, { id: 3, title: 'Chargement…', url: 'https://exemple.fr', loading: true, canBack: false, canFwd: false, zoom: 100, group: 1 }] });
  post({ cmd: 'br.ready' });
