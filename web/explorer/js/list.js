// La liste : tri, rendu, barre latérale, messages fs.list / fs.done du programme.
  // ---------------- liste ----------------
  const cmp = t => { const k = t.sortK, s = t.rev ? -1 : 1;
    const key = { n: it => it.n, m: it => it.m || 0, t: it => typeOf(it), s: it => it.s ?? -1 }[k];
    return (a, b) => { if (!!a.d !== !!b.d) return a.d ? -1 : 1; const x = key(a), y = key(b);
      return s * (typeof x === 'string' ? x.localeCompare(y, 'fr', { numeric: true, sensitivity: 'base' }) : (x - y)) || a.n.localeCompare(b.n, 'fr', { numeric: true, sensitivity: 'base' }); }; };
  const visible = t => { const f = t.filter.toLowerCase(); return t.items.filter(i => !f || i.n.toLowerCase().includes(f)).sort(cmp(t)); };

  function show(keepScroll) {
    const t = T(); if (!t || t.path == null) return;
    const rows = visible(t); t.rows = rows;
    const top = $('files').scrollTop;
    $('files').innerHTML = rows.length ? rows.map((it, i) => `<div class="fr ${t.sel.has(it.n) ? 'sel' : ''} ${clip.cut && clip.paths.includes(joinP(t.path, it.n)) ? 'cut' : ''}" data-i="${i}"><span class="nm">${pix(iconOf(it), 16)}<span>${esc(it.n)}</span></span><span class="f">${dateOf(it.m)}</span><span class="f">${typeOf(it)}</span><span class="f r">${it.drive ? sizeOf(it.free) + ' libres' : sizeOf(it.s)}</span></div>`).join('')
      : `<div class="empty">${t.filter ? 'Aucun résultat' : 'Ce dossier est vide'}</div>`;
    if (keepScroll) $('files').scrollTop = top;
    document.querySelectorAll('#lhead .lh').forEach(b => { b.classList.toggle('on', b.dataset.k === t.sortK); b.classList.toggle('rev', b.dataset.k === t.sortK && t.rev); });
    $('addr').value = t.path; $('back').disabled = t.idx <= 0; $('fwd').disabled = t.idx >= t.hist.length - 1; $('up').disabled = parentOf(t.path) == null;
    $('ttl').textContent = baseOf(t.path) + ' — Explorateur'; post({ cmd: 'win.title', t: baseOf(t.path) + ' — Explorateur' });
    const selItems = rows.filter(i => t.sel.has(i.n)), bytes = selItems.reduce((a, i) => a + (i.s > 0 ? i.s : 0), 0);
    $('st1').textContent = `${rows.length} élément${rows.length > 1 ? 's' : ''}`;
    $('st2').textContent = selItems.length ? `${selItems.length} sélectionné${selItems.length > 1 ? 's' : ''}${bytes ? ' (' + sizeOf(bytes) + ')' : ''}` : '';
    $('st3').textContent = t.free >= 0 ? `Espace libre : ${sizeOf(t.free)}` : '';
    $('orb').className = 'orb green';
    drawSide();
    if (window.updatePreview) updatePreview();
  }
  function drawSide() {
    if (!places) return; const t = T();
    const row = p => `<div class="row-item ${p.path === t.path ? 'sel' : ''}" data-p="${esc(p.path)}">${pix(p.ic, 16)}<span class="grow">${esc(p.name)}</span></div>`;
    $('side').innerHTML = places.places.map(row).join('') + `<div class="head">Ce PC</div>` + places.drives.map(row).join('') + `<div class="row-item ${t.path === '' ? 'sel' : ''}" data-p="">${pix('pc', 16)}<span class="grow">Tous les disques</span></div>`;
  }
  $('side').onclick = e => { const r = e.target.closest('[data-p]'); if (r) go(r.dataset.p, 'push'); };

  handlers.places = m => { places = m; if (!tabs.length) { const q = new URLSearchParams(location.search); addTab(q.get('path') || m.home); if (q.get('select')) T().pendingSel = q.get('select'); } else drawSide(); };
  handlers['fs.list'] = m => {
    const t = tabs.find(x => x.id === m.tab); if (!t) return;
    if (m.error) { $('orb').className = 'orb red'; $('st1').textContent = m.error; $('st2').textContent = ''; $('st3').textContent = ''; t.want = t.path; return; }
    const keep = t.mode === 'reload' && t.path === m.path ? new Set([...t.sel].filter(n => m.items.some(i => i.n === n))) : new Set();
    if (t.mode === 'push' && t.path !== m.path) { t.hist.splice(t.idx + 1); t.hist.push(m.path); t.idx = t.hist.length - 1; }
    if (t.mode === 'back') t.idx = Math.max(0, t.idx - 1); if (t.mode === 'fwd') t.idx = Math.min(t.hist.length - 1, t.idx + 1);
    Object.assign(t, { path: m.path, items: m.items, free: m.free, sel: t.pendingSel ? new Set([t.pendingSel]) : keep, anchor: -1 }); t.pendingSel = null;
    drawTabs(); if (t === T()) { show(t.mode === 'reload'); if (t.renameAfter) { const n = t.renameAfter; t.renameAfter = null; rename(n); } }
    log('liste affichée : ' + m.path + ' (' + m.items.length + ')');
  };
  handlers['fs.done'] = m => {
    const t = tabs.find(x => x.id === m.tab) || T();
    if (m.error) { $('orb').className = 'orb red'; $('st1').textContent = m.error; }
    if (m.select) t.pendingSel = m.select.split(sep).pop();
    reload();
  };
  const reload = () => go(T().path, 'reload');

