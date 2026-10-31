// Navigation (lot 4) : le volet d'information (plan de la note, liens sortants et rétroliens, étiquettes du coffre), la recherche dans le texte,
// le sélecteur rapide (Ctrl+P) et l'autocomplétion des [[liens]] dans l'éditeur. Chargé après doc.js, qui appelle afterActivate() et renderInfo().
  // ---------- volet d'information ----------
  let infoTab = ls('infoTab', 'plan');      // plan | liens | tags
  let infoOpen = ls('infoOpen', true);
  let backlinks = [];                        // de la note active, envoyés par le programme (n.backlinks)
  const infoPane = $('info');   // (« info » est la boîte d'information de core.js)

  // Les titres de la note, hors blocs de code, avec leur ligne (0 = première).
  function headingsOf(text) {
    const out = []; let fence = false;
    String(text).split('\n').forEach((l, i) => {
      if (/^(```|~~~)/.test(l)) { fence = !fence; return; }
      if (fence) return;
      const m = /^(#{1,6})\s+(.+?)\s*#*\s*$/.exec(l); if (m) out.push({ level: m[1].length, text: m[2], line: i });
    });
    return out;
  }
  // Les [[liens]] de la note (une fois chacun), résolus comme le programme les résout.
  function linksOf(text) {
    const re = /\[\[([^\[\]|#^]+?)(?:#([^\[\]|]*))?(?:\|([^\[\]]*))?\]\]/g; const out = new Map(); let m;
    while ((m = re.exec(String(text)))) { const t = m[1].trim(); if (t && !out.has(t.toLowerCase())) out.set(t.toLowerCase(), { target: t, path: resolveLink(t) }); }
    return [...out.values()];
  }
  function tagCounts() { const c = new Map(); for (const n of notes) for (const g of n.g || []) c.set(g, (c.get(g) || 0) + 1); return [...c].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0], 'fr')); }

  function renderInfo() {
    document.body.classList.toggle('noinfo', !infoOpen);
    if (!infoOpen) return;
    infoPane.querySelectorAll('.mb').forEach(b => b.classList.toggle('on', b.dataset.i === infoTab));
    const box = $('infobox'); const d = docs.get(cur);
    if (infoTab === 'plan') {
      const hs = d ? headingsOf(d.text) : [];
      box.innerHTML = hs.length ? hs.map(h => `<div class="row-item hd" data-line="${h.line}" data-h="${esc(h.text)}" style="padding-left:${8 + (h.level - 1) * 12}px"><span class="grow">${esc(h.text)}</span></div>`).join('')
        : `<div class="empty">${d ? 'Aucun titre dans cette note.' : 'Aucune note ouverte.'}</div>`;
    } else if (infoTab === 'liens') {
      if (!d) { box.innerHTML = '<div class="empty">Aucune note ouverte.</div>'; return; }
      const out = linksOf(d.text);
      box.innerHTML = `<div class="sec">Liens sortants <span class="n">${out.length}</span></div>`
        + (out.length ? out.map(l => `<div class="row-item lk${l.path ? '' : ' miss'}" data-path="${esc(l.path || '')}" data-target="${esc(l.target)}" title="${esc(l.path || 'Note à créer')}"><span class="grow">${esc(l.path ? titleOf(l.path) : l.target)}</span>${l.path ? '' : '<span class="n">à créer</span>'}</div>`).join('') : '<div class="empty">Aucun lien.</div>')
        + `<div class="sec">Rétroliens <span class="n">${backlinks.length}</span></div>`
        + (backlinks.length ? backlinks.map(b => `<div class="row-item bl" data-path="${esc(b.p)}" data-line="${b.line}" title="${esc(b.p)} : ligne ${b.line}"><span class="grow"><span class="t">${esc(b.t)}</span><span class="sub">${esc(b.ctx)}</span></span></div>`).join('') : '<div class="empty">Aucune note ne mène ici.</div>');
    } else {
      const tc = tagCounts();
      box.innerHTML = tc.length ? tc.map(([g, n]) => `<div class="row-item tgr" data-tag="${esc(g)}"><span class="grow">#${esc(g)}</span><span class="n">${n}</span></div>`).join('') : '<div class="empty">Aucune étiquette dans le coffre.</div>';
    }
  }
  infoPane.querySelector('.mbar').onclick = e => { const b = e.target.closest('[data-i]'); if (b) { infoTab = b.dataset.i; lsSet('infoTab', infoTab); renderInfo(); } };
  $('infobox').onclick = e => {
    const h = e.target.closest('.hd'); if (h) { gotoHeading(h.dataset.h); return; }
    const l = e.target.closest('.lk'); if (l) { if (l.dataset.path) openNote(l.dataset.path); else createFromLink(l.dataset.target); return; }
    const b = e.target.closest('.bl'); if (b) { openNote(b.dataset.path, { line: +b.dataset.line }); return; }
    const t = e.target.closest('.tgr'); if (t) { $('filter').value = '#' + t.dataset.tag; renderTree(); }
  };
  function setInfo(on) { infoOpen = on; lsSet('infoOpen', on); renderInfo(); }
  handlers['n.backlinks'] = m => { if (m.path === cur) { backlinks = m.backlinks || []; if (infoTab === 'liens') renderInfo(); } };

  // Place le curseur de l'éditeur au début d'une ligne (1 = première) et la montre.
  function gotoLine(line) {
    const lines = ed.value.split('\n'); const i = Math.max(0, Math.min(lines.length - 1, line - 1));
    const off = lines.slice(0, i).join('\n').length + (i ? 1 : 0);
    if (mode !== 'view') { ed.focus(); ed.setSelectionRange(off, off + lines[i].length); const lh = parseFloat(getComputedStyle(ed).lineHeight) || 16; ed.scrollTop = Math.max(0, i * lh - ed.clientHeight / 3); }
  }

  // Appelé par doc.js quand la note active change : le volet se met à jour, les vues se ferment, les rétroliens sont redemandés.
  function afterActivate() {
    backlinks = []; closeViews(); renderInfo();
    if (cur) post({ cmd: 'n.backlinks', path: cur });
  }
  // L'index a changé (une note écrite, par toi ou par Claude) : étiquettes, rétroliens et vues suivent.
  { const idx = handlers.index; handlers.index = m => { idx(m); if (cur) post({ cmd: 'n.backlinks', path: cur }); if (infoTab === 'tags') renderInfo(); if (typeof onIndexForViews === 'function') onIndexForViews(); }; }

  // ---------- recherche dans le texte ----------
  // Le champ de filtre : taper filtre par nom ; Entrée (ou « ? » devant) cherche dans le texte de toutes les notes (termes, "phrase", tag:, in:, type:).
  let searchQ = '', lastHits = null;
  function runSearch(q) {
    q = String(q == null ? $('filter').value : q).replace(/^\?/, '').trim();
    if (!q) { searchQ = ''; lastHits = null; $('filter').value = ''; renderTree(); return; }
    searchQ = q; $('filter').value = '?' + q; post({ cmd: 'n.search', q });
  }
  const markTerms = (s, q) => {
    const terms = (q.match(/"([^"]+)"|(\S+)/g) || []).map(t => t.replace(/^"|"$/g, '')).filter(t => !/^(tag|in|type):/i.test(t)).map(t => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
    let h = esc(s); if (!terms.length) return h;
    return h.replace(new RegExp('(' + terms.join('|') + ')', 'gi'), '<mark>$1</mark>');
  };
  function renderResults() {
    const el = $('tree'); if (!lastHits) return;
    el.innerHTML = `<div class="row-item root" title="Résultats de la recherche"><span class="tw">?</span><span class="grow">${esc(searchQ)}</span><span class="n">${lastHits.length}</span></div>`
      + (lastHits.length ? lastHits.map(h => `<div class="row-item hit nt" data-p="${esc(h.p)}" data-line="${h.line}" title="${esc(h.p)}"><span class="tw"></span><span class="grow"><span class="t">${esc(h.t)}</span><span class="sub">${markTerms(h.s, searchQ)}</span></span></div>`).join('') : '<div class="empty">Rien ne correspond.</div>');
  }
  handlers['n.results'] = m => { if (m.q !== searchQ) return; lastHits = m.hits; renderResults(); };
  { const rt = renderTree; renderTree = function () { if ($('filter').value.startsWith('?') && lastHits) renderResults(); else rt(); }; }
  $('filter').oninput = () => { if (!$('filter').value.startsWith('?')) { lastHits = null; searchQ = ''; } renderTree(); };
  $('filter').onkeydown = e => {
    if (e.key === 'Escape') { $('filter').value = ''; lastHits = null; searchQ = ''; renderTree(); }
    else if (e.key === 'Enter') { e.preventDefault(); runSearch(); }
  };
  // un résultat de recherche ouvre la note à la ligne trouvée
  $('tree').addEventListener('click', e => { const h = e.target.closest('.hit'); if (h && h.dataset.line) openNote(h.dataset.p, { line: +h.dataset.line }); }, true);

  // ---------- sélecteur rapide : Ctrl+P (ou Ctrl+O) ----------
  function openSwitcher() {
    if (document.querySelector('.veil.swv')) return;
    const v = document.createElement('div'); v.className = 'veil swv';
    v.innerHTML = `<div class="win active sw"><div class="tb"><span class="tb-title">Aller à une note</span></div><div class="cb"><input class="text-input dlg-in" type="text" spellcheck="false" autocomplete="off" placeholder="Nom de la note…"><div class="list" id="swl"></div><div class="hint">↑ ↓ pour choisir, Entrée pour ouvrir — ou pour créer la note si aucune ne porte ce nom, Échap pour fermer.</div></div></div>`;
    const inp = v.querySelector('input'), list = v.querySelector('#swl'); let items = [], sel = 0;
    const fill = () => {
      const q = fold(inp.value.trim()); const words = q.split(/\s+/).filter(Boolean);
      items = (q ? notes.filter(n => { const s = fold(n.t) + ' ' + fold(n.p); return words.every(w => s.includes(w)); }) : [...notes].sort((a, b) => b.m - a.m))
        .sort((a, b) => (q ? (fold(a.t).startsWith(q) ? 0 : 1) - (fold(b.t).startsWith(q) ? 0 : 1) || b.m - a.m : 0)).slice(0, 40);
      sel = 0; paint();
    };
    const paint = () => { list.innerHTML = items.length ? items.map((n, i) => `<div class="row-item${i === sel ? ' sel' : ''}" data-i="${i}"><span class="grow">${esc(n.t)}</span><span class="sub">${esc(folderOf(n.p))}</span></div>`).join('') : `<div class="empty">Aucune note ne s'appelle ainsi.<br>Entrée crée « ${esc(inp.value.trim())} ».</div>`; const s = list.querySelector('.sel'); if (s) s.scrollIntoView({ block: 'nearest' }); };
    const done = () => v.remove();
    const go = () => { if (items.length) { openNote(items[sel].p); done(); } else { const name = inp.value.trim(); const parts = name.split('/').map(cleanName).filter(Boolean); if (!parts.length) return; done(); post({ cmd: 'n.create', path: parts.join('/') + '.md', text: '# ' + parts[parts.length - 1] + '\n\n' }); } };
    inp.oninput = fill;
    v.addEventListener('keydown', e => {
      if (e.key === 'Escape') { e.preventDefault(); done(); }
      else if (e.key === 'ArrowDown') { e.preventDefault(); if (items.length) { sel = (sel + 1) % items.length; paint(); } }
      else if (e.key === 'ArrowUp') { e.preventDefault(); if (items.length) { sel = (sel - 1 + items.length) % items.length; paint(); } }
      else if (e.key === 'Enter') { e.preventDefault(); go(); }
    });
    list.onclick = e => { const r = e.target.closest('[data-i]'); if (r) { sel = +r.dataset.i; go(); } };
    layer().appendChild(v); fill(); inp.focus();
  }
  $('bSwitch').onclick = openSwitcher;
  document.addEventListener('keydown', e => {
    if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey) return;
    const k = e.key.toLowerCase();
    if (k === 'p' || k === 'o') { e.preventDefault(); openSwitcher(); }
  });

  // ---------- autocomplétion des [[liens]] dans l'éditeur ----------
  const ac = $('ac'); let acItems = [], acSel = 0, acStart = -1;
  const acClose = () => { ac.hidden = true; acItems = []; acStart = -1; };
  // Le nom à écrire dans le lien : le titre, ou le chemin quand deux notes portent le même titre.
  const linkName = n => { const l = byName.get(n.t.toLowerCase()); return l && l.length > 1 ? n.p.slice(0, -3) : n.t; };
  // Où est le curseur, en pixels dans l'éditeur : un double de la zone de texte, avec le même texte jusqu'au curseur, dont on mesure la fin.
  function caretXY() {
    const cs = getComputedStyle(ed); const m = document.createElement('div');
    for (const p of ['fontFamily', 'fontSize', 'lineHeight', 'letterSpacing', 'padding', 'border', 'boxSizing', 'whiteSpace', 'overflowWrap', 'tabSize']) m.style[p] = cs[p];
    m.style.position = 'absolute'; m.style.visibility = 'hidden'; m.style.width = ed.clientWidth + 'px'; m.style.height = 'auto'; m.style.whiteSpace = cs.whiteSpace === 'pre' ? 'pre' : 'pre-wrap'; m.style.overflowWrap = 'anywhere';
    m.textContent = ed.value.slice(0, ed.selectionStart); const s = document.createElement('span'); s.textContent = '​'; m.appendChild(s);
    document.body.appendChild(m); const x = s.offsetLeft, y = s.offsetTop; m.remove();
    return { x: ed.offsetLeft + x - ed.scrollLeft, y: ed.offsetTop + y - ed.scrollTop + (parseFloat(cs.lineHeight) || 16) };
  }
  function acUpdate() {
    const d = docs.get(cur); if (!d || mode === 'view') return acClose();
    const v = ed.value, i = ed.selectionStart; if (ed.selectionEnd !== i) return acClose();
    const s = v.lastIndexOf('[[', i - 1); if (s < 0) return acClose();
    const q = v.slice(s + 2, i); if (q.includes(']]') || q.includes('\n') || /[|#]/.test(q)) return acClose();
    const f = fold(q);
    acItems = notes.filter(n => n.p !== cur && (fold(n.t).includes(f) || fold(n.p).includes(f))).sort((a, b) => (fold(a.t).startsWith(f) ? 0 : 1) - (fold(b.t).startsWith(f) ? 0 : 1) || b.m - a.m).slice(0, 10);
    if (!acItems.length) return acClose();
    acStart = s; acSel = 0; acPaint();
    const c = caretXY(); ac.style.left = Math.min(c.x, Math.max(0, $('pane').clientWidth - 220)) + 'px'; ac.style.top = c.y + 'px'; ac.hidden = false;
  }
  function acPaint() { ac.innerHTML = acItems.map((n, i) => `<div class="it${i === acSel ? ' on' : ''}" data-i="${i}">${esc(n.t)}${folderOf(n.p) ? `<span class="sub">${esc(folderOf(n.p))}</span>` : ''}</div>`).join(''); }
  function acPick(i) {
    const n = acItems[i]; if (!n || acStart < 0) return;
    const end = ed.selectionStart; const after = ed.value.slice(end, end + 2) === ']]' ? 2 : 0;
    replaceRange(acStart, end + after, '[[' + linkName(n) + ']]'); acClose();
    const d = docs.get(cur); if (d) { d.text = ed.value; markDirty(d); schedulePreview(); }
  }
  ed.addEventListener('input', acUpdate);
  ed.addEventListener('click', acClose);
  ed.addEventListener('blur', () => setTimeout(acClose, 150));
  ed.addEventListener('keydown', e => {
    if (ac.hidden) return;
    if (e.key === 'ArrowDown') { acSel = (acSel + 1) % acItems.length; acPaint(); }
    else if (e.key === 'ArrowUp') { acSel = (acSel - 1 + acItems.length) % acItems.length; acPaint(); }
    else if (e.key === 'Enter' || e.key === 'Tab') { acPick(acSel); }
    else if (e.key === 'Escape') { acClose(); }
    else return;
    e.preventDefault(); e.stopImmediatePropagation();
  }, true);
  ac.onmousedown = e => { e.preventDefault(); const it = e.target.closest('[data-i]'); if (it) acPick(+it.dataset.i); };

  CdxSplit.init({ pane: infoPane, side: 'right', key: 'notes-info', min: 180, max: 480 });
