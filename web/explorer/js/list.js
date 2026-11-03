// La liste : tri (mis en cache), rendu virtualisé (seules les lignes visibles existent dans le DOM), barre latérale, messages fs.list / fs.list.more / fs.done du programme.
  // ---------------- liste ----------------
  const ROW_H = 26, OVER = 10;   // hauteur d'une ligne (celle de .fr) ; lignes gardées au-delà du bord visible
  const collator = new Intl.Collator('fr', { numeric: true, sensitivity: 'base' });   // bien plus rapide que localeCompare sur des dizaines de milliers de noms
  const cmp = t => { const k = t.sortK, s = t.rev ? -1 : 1;
    const key = { n: it => it.b ?? it.n, m: it => it.m || 0, t: it => it._t ??= typeOf(it), s: it => it.s ?? -1 }[k];
    return (a, b) => { if (!!a.d !== !!b.d) return a.d ? -1 : 1;
      if (t.group) { const ga = groupKeyOf(t, a).r, gb = groupKeyOf(t, b).r; if (ga !== gb) return typeof ga === 'string' ? collator.compare(ga, gb) : ga - gb; }   // les groupes d'abord, dans leur ordre
      const x = key(a), y = key(b);
      return s * (typeof x === 'string' ? collator.compare(x, y) : (x - y)) || collator.compare(a.n, b.n); }; };
  // Les éléments à montrer, triés et filtrés ; le résultat est gardé tant que ni les éléments (t.ver), ni le tri, ni le filtre ne changent : un clic ne retrie jamais.
  function visible(t) {
    const key = t.ver + '|' + t.sortK + '|' + t.rev + '|' + t.filter + '|' + (t.group || '');
    if (t.vkey === key && t.vrows) return t.vrows;
    const f = t.filter.toLowerCase();
    const list = f ? t.items.filter(i => i.n.toLowerCase().includes(f)) : t.items.slice();
    list.sort(cmp(t)); t.vkey = key; return t.vrows = list;
  }
  // Les éléments par nom (pour retrouver la sélection sans parcourir toute la liste).
  function byName(t) { if (t.bnVer !== t.ver || !t.bn) { t.bn = new Map(t.items.map(i => [i.n, i])); t.bnVer = t.ver; } return t.bn; }

  // Une ligne (ou une case, dans les vues en icônes) : views.js, qui connaît la vue, les colonnes masquées et la position de chaque élément.
  const rowHtml = (it, i, t, lay) => cellHtml(it, i, t, lay || ensureLayout(t));

  // Dessine les lignes qui touchent la zone visible (plus quelques-unes de marge), entre deux espaceurs qui gardent la hauteur et la barre de défilement de la liste entière.
  function paintRows(force) {
    const t = T(), box = $('files'); if (!t || !t.rows) return;
    const rows = t.rows;
    if (!rows.length) { const s = t.search; box.innerHTML = `<div class="empty">${t.path === TRASH && !t.loading ? 'La corbeille est vide' : s ? (s.running ? 'Recherche en cours…' : searchIdle(s) ? 'Tape un nom (ex. facture *.pdf) ou choisis un critère.' : 'Aucun résultat') : t.loading ? 'Lecture du dossier…' : t.filter ? 'Aucun résultat' : 'Ce dossier est vide'}</div>`; t.win = null; return; }
    const lay = ensureLayout(t), h = box.clientHeight || 400, st = box.scrollTop;
    const top = st - OVER_PX, bot = st + h + OVER_PX;
    let first = firstFrom(lay.ys, top - lay.rh), last = firstFrom(lay.ys, bot) - 1;
    first = Math.max(0, first); last = Math.min(rows.length - 1, Math.max(first, last));
    if (!force && t.win && t.win[0] === first && t.win[1] === last && t.winKey === t.layKey) return;
    t.win = [first, last]; t.winKey = t.layKey;
    let html = `<div class="lay" style="height:${lay.h}px">`;   // le conteneur garde la hauteur de la liste entière (barre de défilement juste) ; chaque élément est posé à sa place
    for (const hd of lay.hdrs) if (hd.y + HDR_H >= top && hd.y <= bot) html += `<div class="grp" style="top:${hd.y}px">${esc(hd.l)}</div>`;
    for (let i = first; i <= last; i++) html += rowHtml(rows[i], i, t, lay);
    box.innerHTML = html + '</div>';
  }
  let paintQueued = false;
  $('files').addEventListener('scroll', () => { if (paintQueued) return; paintQueued = true; requestAnimationFrame(() => { paintQueued = false; paintRows(); }); });
  if (window.ResizeObserver) new ResizeObserver(() => paintRows()).observe($('files'));
  // Fait défiler la liste pour que la ligne d'indice i soit visible (le clavier, le renommage).
  function ensureVisible(i) {
    const t = T(); if (!t || !t.rows || i < 0 || i >= t.rows.length) return;
    const lay = ensureLayout(t), box = $('files'), top = lay.ys[i], bot = top + lay.rh, st = box.scrollTop, h = box.clientHeight;
    if (top < st) box.scrollTop = top; else if (bot > st + h) box.scrollTop = bot - h;
    paintRows();
  }

  function show(keepScroll) {
    const t = T(); if (!t || t.path == null) return;
    const rows = visible(t); t.rows = rows;
    if (!keepScroll) $('files').scrollTop = 0;
    syncViewUi(t); paintRows(true);
    document.querySelectorAll('#lhead .lh').forEach(b => { b.classList.toggle('on', b.dataset.k === t.sortK); b.classList.toggle('rev', b.dataset.k === t.sortK && t.rev); });
    const dh = document.querySelector('#lhead .lh[data-k="m"]'); if (dh && dh.firstChild && dh.firstChild.nodeType === 3) dh.firstChild.nodeValue = t.path === TRASH ? 'Supprimé le' : 'Modifié le';   // dans la corbeille, la date est celle de la suppression
    $('addr').value = t.path === TRASH ? 'Corbeille' : t.path; $('back').disabled = t.idx <= 0; $('fwd').disabled = t.idx >= t.hist.length - 1; $('up').disabled = parentOf(t.path) == null;
    $('ttl').textContent = baseOf(t.path) + ' — Explorateur'; post({ cmd: 'win.title', t: baseOf(t.path) + ' — Explorateur' });
    const bn = byName(t); let n = 0, bytes = 0;
    for (const name of t.sel) { const it = bn.get(name); if (it) { n++; if (it.s > 0) bytes += it.s; } }
    const sr = t.search;
    $('st1').textContent = sr ? (sr.running ? `Recherche… ${rows.length} trouvé${rows.length > 1 ? 's' : ''}, ${sr.scanned} éléments lus` : `${rows.length} résultat${rows.length > 1 ? 's' : ''}${sr.truncated ? ' (limité à 20 000)' : ''}`)
      : `${rows.length} élément${rows.length > 1 ? 's' : ''}${t.loading ? ' (lecture en cours…)' : ''}`;
    $('st2').textContent = flash && Date.now() < flash.until ? flash.text : n ? `${n} sélectionné${n > 1 ? 's' : ''}${bytes ? ' (' + sizeOf(bytes) + ')' : ''}` : '';
    $('st3').textContent = t.free >= 0 ? `Espace libre : ${sizeOf(t.free)}` : '';
    $('orb').className = t.loading ? 'orb amber pulse' : 'orb green';
    drawSide(); prefetchOpenWith(); syncSearchUi();
    if (window.updatePreview) updatePreview();
  }
  function drawSide() {
    if (!places) return; const t = T();
    const row = (p, extra = '') => `<div class="row-item ${p.path === t.path ? 'sel' : ''}" data-p="${esc(p.path)}"${extra}>${pix(p.ic, 16)}<span class="grow">${esc(p.name)}</span></div>`;
    const local = places.drives.filter(d => d.kind !== 'reseau'), net = places.drives.filter(d => d.kind === 'reseau');
    const servers = (Array.isArray(settings['net.servers']) ? settings['net.servers'] : []).map(s => ({ name: s.replace(/^\\\\/, ''), path: s, ic: 'serveur' }));
    const pins = pinList().map(p => ({ name: baseOf(p), path: p, ic: 'dossier' }));
    const freq = recentList().filter(r => (r.n | 0) >= 3 && !isPinned(r.p)).sort((a, b) => b.n - a.n).slice(0, 4);
    const rec = recentList().filter(r => !isPinned(r.p) && !freq.includes(r.p) && !freq.some(f => f.p === r.p) && r.p !== t.path).sort((a, b) => b.at - a.at).slice(0, 6);
    $('side').innerHTML = (pins.length || freq.length ? `<div class="head">Accès rapide</div>` + pins.map(p => row(p, ' data-pin="1"')).join('') + freq.map(r => row({ name: baseOf(r.p), path: r.p, ic: 'dossier' }, ' data-freq="1"')).join('') : '')
      + (rec.length ? `<div class="head">Récents</div>` + rec.map(r => row({ name: baseOf(r.p), path: r.p, ic: 'dossier' }, ' data-rec="1"')).join('') : '')
      + places.places.map(p => row(p)).join('')
      + `<div class="head">Ce PC</div>` + local.map(d => row(d, d.kind === 'amovible' || d.kind === 'cdrom' ? ' data-eject="1"' : '')).join('')
      + `<div class="row-item ${t.path === '' ? 'sel' : ''}" data-p="">${pix('pc', 16)}<span class="grow">Tous les disques</span></div>`
      + `<div class="row-item ${t.path === TRASH ? 'sel' : ''}" data-p="${TRASH}">${pix('supprimer', 16)}<span class="grow">Corbeille</span></div>`
      + (net.length || servers.length ? `<div class="head">Réseau</div>` + net.map(d => row(d)).join('') + servers.map(s => row(s, ' data-server="1"')).join('') : '');
  }
  $('side').onclick = e => { const r = e.target.closest('[data-p]'); if (r) go(r.dataset.p, 'push'); };
  // Un serveur dont on a lu un partage (ou la liste des partages) reste dans la barre latérale (réglage net.servers, commun à toutes les fenêtres) ; « Oublier ce serveur » le retire.
  function rememberServer(path) {
    const m = /^(\\\\[^\\]+)/.exec(path || ''); if (!m) return;
    const list = Array.isArray(settings['net.servers']) ? settings['net.servers'] : [];
    if (list.some(x => x.toLowerCase() === m[1].toLowerCase())) return;
    setSetting('net.servers', [...list, m[1]].slice(-12));
  }
  function forgetServer(path) { const list = Array.isArray(settings['net.servers']) ? settings['net.servers'] : []; setSetting('net.servers', list.filter(x => x.toLowerCase() !== path.toLowerCase())); }
  function ejectDrive(p) { post({ cmd: 'fs.eject', drive: p, tab: T().id }); say('Éjection de ' + p + '…'); }
  $('side').oncontextmenu = e => {
    const r = e.target.closest('[data-p]'); if (!r) return; e.preventDefault();
    const items = [['Ouvrir', () => go(r.dataset.p, 'push')], ['Ouvrir dans un nouvel onglet', () => addTab(r.dataset.p)]];
    if (r.dataset.eject) items.push(null, ['Éjecter', () => ejectDrive(r.dataset.p), { icon: 'disque' }]);
    if (r.dataset.server) items.push(null, ['Oublier ce serveur', () => forgetServer(r.dataset.p)]);
    if (r.dataset.pin) items.push(null, ['Retirer de l’accès rapide', () => togglePin(r.dataset.p)]);
    if (r.dataset.freq || r.dataset.rec) items.push(null, ['Épingler à l’accès rapide', () => togglePin(r.dataset.p)], ['Retirer des récents', () => forgetRecent(r.dataset.p)]);
    CdxFrame.menu(items, e.clientX, e.clientY, r.textContent.trim());
  };

  handlers.places = m => {
    places = m;
    if (tabs.length) { drawSide(); return; }
    const q = new URLSearchParams(location.search), sess = settings.session;
    // Une fenêtre ouverte sans dossier (restore=1) retrouve les onglets de la dernière session ; sinon le dossier demandé (ou Documents).
    if (q.get('restore') === '1' && sess && Array.isArray(sess.tabs) && sess.tabs.some(p => typeof p === 'string')) { for (const p of sess.tabs) if (typeof p === 'string') addTab(p); selectTab(Math.max(0, Math.min(tabs.length - 1, sess.cur | 0))); }
    else { addTab(q.get('path') || m.home); if (q.get('select')) T().pendingSel = q.get('select'); }
  };
  // Le premier paquet d'un dossier (fs.list) remplace la liste ; les suivants (fs.list.more) s'y ajoutent. L'affichage est rafraîchi au plus toutes les 350 ms pendant la lecture, puis une dernière fois.
  let lastShow = 0;
  handlers['fs.list'] = m => {
    const t = tabs.find(x => x.id === m.tab); if (!t) return;
    // Le dossier affiché a disparu (supprimé, déplacé, disque retiré) : on remonte au dossier parent le plus proche qui existe.
    if (m.error && m.error.startsWith('Dossier introuvable') && t.mode === 'reload' && t.path) { const up = parentOf(t.path); t.loading = false; go(up == null ? '' : up, 'push'); return; }
    if (m.error) { t.loading = false; $('orb').className = 'orb red'; $('st1').textContent = m.error; $('st2').textContent = ''; $('st3').textContent = ''; t.want = t.path; return; }
    // Une relecture qui arrive pendant un renommage ou un glisser ne doit pas détruire le champ de saisie : on la laisse tomber, `dirty` en commande une autre dès que c'est fini.
    if (t.mode === 'reload' && t === T() && t.path === m.path && busy()) { t.dirty = true; t.dropMore = !!m.more; return; }
    const keep = t.mode === 'reload' && t.path === m.path ? new Set(t.sel) : new Set();
    if (t.mode === 'push' && t.path !== m.path) { t.hist.splice(t.idx + 1); t.hist.push(m.path); t.idx = t.hist.length - 1; }
    if (t.mode === 'back') t.idx = Math.max(0, t.idx - 1); if (t.mode === 'fwd') t.idx = Math.min(t.hist.length - 1, t.idx + 1);
    Object.assign(t, { path: m.path, items: m.items, free: m.free, ver: (t.ver | 0) + 1, loading: !!m.more, sel: t.pendingSel ? new Set([t.pendingSel]) : keep, anchor: -1, cursor: -1 }); t.pendingSel = null;
    if (!m.more && t.mode === 'reload') pruneSel(t);
    if (t.mode !== 'reload') { applySavedView(t); saveSession(); bumpRecent(m.path); }   // la vue et le groupe gardés pour ce dossier ; la session et les récents suivent
    if (t.watched !== m.path) { t.watched = m.path; post({ cmd: 'fs.watch', tab: t.id, path: m.path }); }   // le programme prévient quand le dossier change (fs.changed)
    drawTabs(); if (t === T()) { show(t.mode === 'reload'); lastShow = performance.now(); if (t.renameAfter) { const n = t.renameAfter; t.renameAfter = null; rename(n); } }
    if (isUnc(m.path)) rememberServer(m.path);
    log('liste affichée : ' + m.path + ' (' + m.items.length + (m.more ? ', suite en cours' : '') + ')');
  };
  handlers['fs.list.more'] = m => {
    const t = tabs.find(x => x.id === m.tab); if (!t || t.path !== m.path) return;
    if (t.dropMore) { if (!m.more) t.dropMore = false; return; }   // suite d'une liste laissée tomber (voir plus haut)
    for (const it of m.items) t.items.push(it);
    t.ver++; t.loading = !!m.more;
    if (!m.more && t.mode === 'reload') pruneSel(t);
    if (t === T()) { const now = performance.now(); if (!m.more || now - lastShow > 350) { show(true); lastShow = now; } }
    if (!m.more) log('liste complète : ' + m.path + ' (' + t.items.length + ')');
  };
  // Après une relecture, la sélection ne garde que ce qui existe encore.
  function pruneSel(t) { const bn = byName(t); for (const n of [...t.sel]) if (!bn.has(n)) t.sel.delete(n); }
  handlers['fs.done'] = m => {
    const t = tabs.find(x => x.id === m.tab) || T();
    if (m.error) { $('orb').className = 'orb red'; $('st1').textContent = m.error; say(m.error); }   // (say : la relecture qui suit réécrit st1)
    if (m.ejected) say('Disque éjecté : ' + m.ejected + ' (tu peux le retirer)');
    if (m.select) { t.pendingSel = m.select.split(sep).pop(); if (t.wantRename) t.renameAfter = t.pendingSel; }   // « Nouveau » : l'élément créé est sélectionné et son nom s'édite
    t.wantRename = false;
    reload();
  };
  const reload = () => { if (T().search) { runSearch(); return; } go(T().path, 'reload'); };   // pendant une recherche, actualiser la relance
  // Un réglage a changé (ici ou dans une autre fenêtre) : les éléments cachés demandent une relecture de tous les onglets (l'actif tout de suite), les extensions un simple redessin.
  let shownHidden = false;
  function onSettings() {
    const t = T(), hid = settings['show.hidden'] === true;
    if (hid !== shownHidden) { shownHidden = hid; for (const x of tabs) x.stale = true; if (t) { t.stale = false; reload(); } return; }
    refreshViewSettings();   // vues par dossier, colonnes masquées
    if (t && t.rows && !busy()) paintRows(true);   // (pas pendant un renommage ni un glisser : la session et les récents écrivent des réglages en pleine navigation)
    drawSide();   // serveurs du réseau, dossiers épinglés, récents : des réglages aussi
  }
  // Le dossier d'un onglet a changé hors de la fenêtre : on le relit (sélection et défilement gardés), sauf pendant un renommage ou un glisser (on attend la fin) ; un onglet caché se relit quand on y revient.
  const busy = () => !!(typeof drag !== 'undefined' && drag) || !!document.activeElement?.closest?.('#files input');
  handlers['fs.changed'] = m => {
    const t = tabs.find(x => x.id === m.tab); if (!t || t.path !== m.path || t.search) return;   // (pas de relecture pendant une recherche)
    if (t !== T()) { t.stale = true; return; }
    if (busy()) { t.dirty = true; return; }
    reload();
  };
  // la relecture différée d'un dossier « sale » : seulement fenêtre visible (une fenêtre réduite n'a rien à redessiner ; elle relit en revenant)
  let dirtyTimer = null;
  const dirtyTick = () => { const t = T(); if (t && t.dirty && !busy()) { t.dirty = false; reload(); } };
  const watchDirty = on => { clearInterval(dirtyTimer); dirtyTimer = on ? setInterval(dirtyTick, 700) : null; if (on) dirtyTick(); };
  document.addEventListener('visibilitychange', () => watchDirty(!document.hidden));
  watchDirty(!document.hidden);
