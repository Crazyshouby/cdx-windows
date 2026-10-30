// La liste : tri (mis en cache), rendu virtualisé (seules les lignes visibles existent dans le DOM), barre latérale, messages fs.list / fs.list.more / fs.done du programme.
  // ---------------- liste ----------------
  const ROW_H = 26, OVER = 10;   // hauteur d'une ligne (celle de .fr) ; lignes gardées au-delà du bord visible
  const collator = new Intl.Collator('fr', { numeric: true, sensitivity: 'base' });   // bien plus rapide que localeCompare sur des dizaines de milliers de noms
  const cmp = t => { const k = t.sortK, s = t.rev ? -1 : 1;
    const key = { n: it => it.n, m: it => it.m || 0, t: it => it._t ??= typeOf(it), s: it => it.s ?? -1 }[k];
    return (a, b) => { if (!!a.d !== !!b.d) return a.d ? -1 : 1; const x = key(a), y = key(b);
      return s * (typeof x === 'string' ? collator.compare(x, y) : (x - y)) || collator.compare(a.n, b.n); }; };
  // Les éléments à montrer, triés et filtrés ; le résultat est gardé tant que ni les éléments (t.ver), ni le tri, ni le filtre ne changent : un clic ne retrie jamais.
  function visible(t) {
    const key = t.ver + '|' + t.sortK + '|' + t.rev + '|' + t.filter;
    if (t.vkey === key && t.vrows) return t.vrows;
    const f = t.filter.toLowerCase();
    const list = f ? t.items.filter(i => i.n.toLowerCase().includes(f)) : t.items.slice();
    list.sort(cmp(t)); t.vkey = key; return t.vrows = list;
  }
  // Les éléments par nom (pour retrouver la sélection sans parcourir toute la liste).
  function byName(t) { if (t.bnVer !== t.ver || !t.bn) { t.bn = new Map(t.items.map(i => [i.n, i])); t.bnVer = t.ver; } return t.bn; }

  const rowHtml = (it, i, t) => `<div class="fr ${t.sel.has(it.n) ? 'sel' : ''} ${it.h ? 'hid' : ''} ${clip.cut && clip.paths.includes(joinP(t.path, it.n)) ? 'cut' : ''}" data-i="${i}"><span class="nm">${pix(iconOf(it), 16)}<span>${esc(dispName(it))}</span></span><span class="f">${dateOf(it.m)}</span><span class="f">${typeOf(it)}</span><span class="f r">${it.drive ? sizeOf(it.free) + ' libres' : sizeOf(it.s)}</span></div>`;

  // Dessine les lignes qui touchent la zone visible (plus quelques-unes de marge), entre deux espaceurs qui gardent la hauteur et la barre de défilement de la liste entière.
  function paintRows(force) {
    const t = T(), box = $('files'); if (!t || !t.rows) return;
    const rows = t.rows;
    if (!rows.length) { box.innerHTML = `<div class="empty">${t.loading ? 'Lecture du dossier…' : t.filter ? 'Aucun résultat' : 'Ce dossier est vide'}</div>`; t.win = null; return; }
    const h = box.clientHeight || 400, st = box.scrollTop;
    const first = Math.max(0, Math.floor(st / ROW_H) - OVER), last = Math.min(rows.length - 1, Math.ceil((st + h) / ROW_H) + OVER);
    if (!force && t.win && t.win[0] === first && t.win[1] === last) return;
    t.win = [first, last];
    let html = first ? `<div style="height:${first * ROW_H}px"></div>` : '';
    for (let i = first; i <= last; i++) html += rowHtml(rows[i], i, t);
    if (last < rows.length - 1) html += `<div style="height:${(rows.length - 1 - last) * ROW_H}px"></div>`;
    box.innerHTML = html;
  }
  let paintQueued = false;
  $('files').addEventListener('scroll', () => { if (paintQueued) return; paintQueued = true; requestAnimationFrame(() => { paintQueued = false; paintRows(); }); });
  if (window.ResizeObserver) new ResizeObserver(() => paintRows()).observe($('files'));
  // Fait défiler la liste pour que la ligne d'indice i soit visible (le clavier, le renommage).
  function ensureVisible(i) {
    const box = $('files'), top = i * ROW_H, bot = top + ROW_H, st = box.scrollTop, h = box.clientHeight;
    if (top < st) box.scrollTop = top; else if (bot > st + h) box.scrollTop = bot - h;
    paintRows();
  }

  function show(keepScroll) {
    const t = T(); if (!t || t.path == null) return;
    const rows = visible(t); t.rows = rows;
    if (!keepScroll) $('files').scrollTop = 0;
    paintRows(true);
    document.querySelectorAll('#lhead .lh').forEach(b => { b.classList.toggle('on', b.dataset.k === t.sortK); b.classList.toggle('rev', b.dataset.k === t.sortK && t.rev); });
    $('addr').value = t.path; $('back').disabled = t.idx <= 0; $('fwd').disabled = t.idx >= t.hist.length - 1; $('up').disabled = parentOf(t.path) == null;
    $('ttl').textContent = baseOf(t.path) + ' — Explorateur'; post({ cmd: 'win.title', t: baseOf(t.path) + ' — Explorateur' });
    const bn = byName(t); let n = 0, bytes = 0;
    for (const name of t.sel) { const it = bn.get(name); if (it) { n++; if (it.s > 0) bytes += it.s; } }
    $('st1').textContent = `${rows.length} élément${rows.length > 1 ? 's' : ''}${t.loading ? ' (lecture en cours…)' : ''}`;
    $('st2').textContent = n ? `${n} sélectionné${n > 1 ? 's' : ''}${bytes ? ' (' + sizeOf(bytes) + ')' : ''}` : '';
    $('st3').textContent = t.free >= 0 ? `Espace libre : ${sizeOf(t.free)}` : '';
    $('orb').className = t.loading ? 'orb amber pulse' : 'orb green';
    drawSide(); prefetchOpenWith();
    if (window.updatePreview) updatePreview();
  }
  function drawSide() {
    if (!places) return; const t = T();
    const row = p => `<div class="row-item ${p.path === t.path ? 'sel' : ''}" data-p="${esc(p.path)}">${pix(p.ic, 16)}<span class="grow">${esc(p.name)}</span></div>`;
    $('side').innerHTML = places.places.map(row).join('') + `<div class="head">Ce PC</div>` + places.drives.map(row).join('') + `<div class="row-item ${t.path === '' ? 'sel' : ''}" data-p="">${pix('pc', 16)}<span class="grow">Tous les disques</span></div>`;
  }
  $('side').onclick = e => { const r = e.target.closest('[data-p]'); if (r) go(r.dataset.p, 'push'); };

  handlers.places = m => { places = m; if (!tabs.length) { const q = new URLSearchParams(location.search); addTab(q.get('path') || m.home); if (q.get('select')) T().pendingSel = q.get('select'); } else drawSide(); };
  // Le premier paquet d'un dossier (fs.list) remplace la liste ; les suivants (fs.list.more) s'y ajoutent. L'affichage est rafraîchi au plus toutes les 350 ms pendant la lecture, puis une dernière fois.
  let lastShow = 0;
  handlers['fs.list'] = m => {
    const t = tabs.find(x => x.id === m.tab); if (!t) return;
    // Le dossier affiché a disparu (supprimé, déplacé, disque retiré) : on remonte au dossier parent le plus proche qui existe.
    if (m.error && m.error.startsWith('Dossier introuvable') && t.mode === 'reload' && t.path) { const up = parentOf(t.path); t.loading = false; go(up == null ? '' : up, 'push'); return; }
    if (m.error) { t.loading = false; $('orb').className = 'orb red'; $('st1').textContent = m.error; $('st2').textContent = ''; $('st3').textContent = ''; t.want = t.path; return; }
    const keep = t.mode === 'reload' && t.path === m.path ? new Set(t.sel) : new Set();
    if (t.mode === 'push' && t.path !== m.path) { t.hist.splice(t.idx + 1); t.hist.push(m.path); t.idx = t.hist.length - 1; }
    if (t.mode === 'back') t.idx = Math.max(0, t.idx - 1); if (t.mode === 'fwd') t.idx = Math.min(t.hist.length - 1, t.idx + 1);
    Object.assign(t, { path: m.path, items: m.items, free: m.free, ver: (t.ver | 0) + 1, loading: !!m.more, sel: t.pendingSel ? new Set([t.pendingSel]) : keep, anchor: -1, cursor: -1 }); t.pendingSel = null;
    if (!m.more && t.mode === 'reload') pruneSel(t);
    if (t.watched !== m.path) { t.watched = m.path; post({ cmd: 'fs.watch', tab: t.id, path: m.path }); }   // le programme prévient quand le dossier change (fs.changed)
    drawTabs(); if (t === T()) { show(t.mode === 'reload'); lastShow = performance.now(); if (t.renameAfter) { const n = t.renameAfter; t.renameAfter = null; rename(n); } }
    log('liste affichée : ' + m.path + ' (' + m.items.length + (m.more ? ', suite en cours' : '') + ')');
  };
  handlers['fs.list.more'] = m => {
    const t = tabs.find(x => x.id === m.tab); if (!t || t.path !== m.path) return;
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
    if (m.error) { $('orb').className = 'orb red'; $('st1').textContent = m.error; }
    if (m.select) { t.pendingSel = m.select.split(sep).pop(); if (t.wantRename) t.renameAfter = t.pendingSel; }   // « Nouveau » : l'élément créé est sélectionné et son nom s'édite
    t.wantRename = false;
    reload();
  };
  const reload = () => go(T().path, 'reload');
  // Un réglage a changé (ici ou dans une autre fenêtre) : les éléments cachés demandent une relecture de tous les onglets (l'actif tout de suite), les extensions un simple redessin.
  let shownHidden = false;
  function onSettings() {
    const t = T(), hid = settings['show.hidden'] === true;
    if (hid !== shownHidden) { shownHidden = hid; for (const x of tabs) x.stale = true; if (t) { t.stale = false; reload(); } return; }
    if (t && t.rows) paintRows(true);
  }
  // Le dossier d'un onglet a changé hors de la fenêtre : on le relit (sélection et défilement gardés), sauf pendant un renommage ou un glisser (on attend la fin) ; un onglet caché se relit quand on y revient.
  const busy = () => !!(typeof drag !== 'undefined' && drag) || !!document.activeElement?.closest?.('#files input');
  handlers['fs.changed'] = m => {
    const t = tabs.find(x => x.id === m.tab); if (!t || t.path !== m.path) return;
    if (t !== T()) { t.stale = true; return; }
    if (busy()) { t.dirty = true; return; }
    reload();
  };
  setInterval(() => { const t = T(); if (t && t.dirty && !busy()) { t.dirty = false; reload(); } }, 700);
