// Les notes ouvertes : onglets, éditeur, aperçu, enregistrement automatique, changements venus d'ailleurs (Claude, un autre éditeur).
  const docs = new Map();                   // chemin -> { path, text, hash, eol, dirty, saving, sent, timer, sel, scroll }
  let tabs = ls('tabs', []);                // chemins ouverts, dans l'ordre
  let cur = null;                           // la note active
  const pending = new Map();                // chemin -> options, le temps que le programme envoie la note
  let mode = ls('mode', 'split');           // edit | split | view
  let wrap = ls('wrap', true);
  const ed = $('ed'), pv = $('pv');
  const titleOf = p => { const n = noteAt(p); return n ? n.t : baseName(p); };

  // ---------- ouvrir, activer, fermer ----------
  function openNote(path, opts = {}) {
    if (!path) return;
    const n = noteAt(path); const p = n ? n.p : path;
    if (docs.has(p)) { activate(p, opts); return; }
    pending.set(p, Object.assign({ activate: true }, opts)); post({ cmd: 'n.read', path: p });
  }

  function restoreTabs(noActivate) {
    const saved = ls('cur', null); tabs = tabs.filter(p => typeof p === 'string');
    for (const p of tabs) { pending.set(p, { activate: !noActivate && (p === saved || (!saved && p === tabs[0])), quiet: true }); post({ cmd: 'n.read', path: p }); }
    if (!tabs.length) showDoc();
  }

  function makeDoc(m) {
    const d = { path: m.path, text: String(m.text).replace(/\r\n/g, '\n'), hash: m.hash, eol: m.text.includes('\r\n') ? '\r\n' : '\n', dirty: false, saving: false, sent: '', timer: 0, sel: [0, 0], scroll: 0, again: false };
    docs.set(m.path, d); return d;
  }

  handlers['n.note'] = m => {
    const opts = pending.get(m.path) || {}; pending.delete(m.path);
    const had = docs.get(m.path);
    if (had && opts.reload) { adopt(had, m); return; }
    if (had) return;
    makeDoc(m); if (!tabs.includes(m.path)) tabs.push(m.path);
    if (opts.activate || !cur) activate(m.path, opts); else { renderTabs(); lsSet('tabs', tabs); }
  };

  function activate(path, opts = {}) {
    const d = docs.get(path); if (!d) return;
    const old = docs.get(cur); if (old) { old.sel = [ed.selectionStart, ed.selectionEnd]; old.scroll = ed.scrollTop; }
    cur = path; closeNotice(); showDoc();
    ed.value = d.text; ed.setSelectionRange(Math.min(d.sel[0], d.text.length), Math.min(d.sel[1], d.text.length)); ed.scrollTop = d.scroll;
    renderPreview(); renderTabs(); renderTree(); revealInTree(path); updateStatus();
    lsSet('tabs', tabs); lsSet('cur', path);
    if (typeof afterActivate === 'function') afterActivate(opts);   // nav.js : volet d'information, vues fermées
    if (opts.heading) gotoHeading(opts.heading);
    if (opts.line && typeof gotoLine === 'function') gotoLine(opts.line);
    if (!opts.quiet && mode !== 'view') ed.focus();
  }

  function showDoc() {
    if (typeof graphOn === 'function' && graphOn()) gDraw();   // le graphe en colonne marque la note active
    ed.disabled = !cur; document.body.classList.toggle('nowrap', !wrap);
    const w = $('pane'); w.classList.remove('mode-edit', 'mode-split', 'mode-view'); w.classList.add('mode-' + mode);
    document.querySelectorAll('#modes .mb').forEach(b => b.classList.toggle('on', b.dataset.m === mode));
    if (!cur) { ed.value = ''; pv.innerHTML = welcome(); $('st3').textContent = ''; setStatus('Aucune note ouverte', ''); if (typeof renderInfo === 'function') renderInfo(); }
  }

  function welcome() {
    const rec = [...notes].sort((a, b) => b.m - a.m).slice(0, 8);
    return '<h1>Notes</h1>' + (rec.length ? '<p>Récemment modifiées :</p><ul>' + rec.map(n => `<li><a class="wl" data-path="${esc(n.p)}">${esc(n.t)}</a> <span class="faint">${esc(folderOf(n.p))}</span></li>`).join('') + '</ul>' : '<p>Le coffre est vide : crée une note avec « Nouvelle note ».</p>');
  }

  function closeDoc(path, force) {
    const d = docs.get(path); if (!d) return;
    if (!force && d.dirty) { d.closeAfter = true; save(d); return; }
    dropDoc(path);
  }
  function dropDoc(path) {
    const d = docs.get(path); if (d) clearTimeout(d.timer);
    docs.delete(path); pending.delete(path); post({ cmd: 'n.close', path });
    const i = tabs.indexOf(path); if (i >= 0) tabs.splice(i, 1);
    if (cur === path) { cur = null; const next = tabs[Math.min(i, tabs.length - 1)]; if (next) activate(next, { quiet: true }); else { showDoc(); renderTabs(); renderTree(); } }
    else renderTabs();
    lsSet('tabs', tabs); lsSet('cur', cur);
  }

  // La note a changé de chemin (renommée, déplacée, ou son dossier l'a été) : l'onglet suit.
  function renamedDoc(from, to) {
    const d = docs.get(from); if (!d) return;
    docs.delete(from); d.path = to; docs.set(to, d);
    tabs = tabs.map(p => p === from ? to : p); if (cur === from) cur = to;
    renderTabs(); renderTree(); lsSet('tabs', tabs); lsSet('cur', cur); updateStatus();
  }

  // ---------- onglets ----------
  function renderTabs() {
    $('tabs').innerHTML = tabs.map(p => { const d = docs.get(p); return `<button class="tab${p === cur ? ' on' : ''}" data-p="${esc(p)}" title="${esc(p)}"><span class="tt">${esc(titleOf(p))}</span>${d && d.dirty ? '<span class="dirty">●</span>' : ''}<i class="x" data-x>×</i></button>`; }).join('');
  }
  $('tabs').onclick = e => { const t = e.target.closest('.tab'); if (!t) return; if (e.target.closest('[data-x]')) closeDoc(t.dataset.p); else if (t.dataset.p !== cur) activate(t.dataset.p); };
  $('tabs').onauxclick = e => { const t = e.target.closest('.tab'); if (t && e.button === 1) closeDoc(t.dataset.p); };
  function cycleTab(d) { if (tabs.length < 2) return; activate(tabs[(tabs.indexOf(cur) + d + tabs.length) % tabs.length]); }

  // ---------- éditeur ----------
  let previewTimer = 0;
  function renderPreview() {
    const d = docs.get(cur); if (!d) return;
    const top = pv.scrollTop;
    pv.innerHTML = Md.render(d.text, mdCtx).html; pv.scrollTop = top;
  }
  const refreshPreview = () => { if (cur) renderPreview(); else if (!ed.value) pv.innerHTML = welcome(); };
  function schedulePreview() { clearTimeout(previewTimer); previewTimer = setTimeout(() => { renderPreview(); updateStatus(); if (typeof renderInfo === 'function') renderInfo(); }, 150); }

  function updateStatus() {
    const d = docs.get(cur); if (!d) return;
    const words = (d.text.match(/[\p{L}\p{N}_'’-]+/gu) || []).length;
    $('st3').textContent = `${words} mot${words > 1 ? 's' : ''} · ${d.text.split('\n').length} lignes`;
    $('st2').textContent = d.path;
    setStatus(d.saving ? 'Enregistrement…' : d.dirty ? 'Modification…' : 'Enregistré', d.saving || d.dirty ? 'amber' : 'green');
  }

  function markDirty(d) {
    d.dirty = true; clearTimeout(d.timer); d.timer = setTimeout(() => save(d), 900);
    renderTabs(); updateStatus();
  }
  ed.addEventListener('input', () => { const d = docs.get(cur); if (!d) return; d.text = ed.value; markDirty(d); schedulePreview(); });

  // ---------- enregistrement ----------
  function save(d) {
    clearTimeout(d.timer);
    if (!d.dirty) { if (d.closeAfter) dropDoc(d.path); return; }
    if (d.saving) { d.again = true; return; }
    d.saving = true; d.sent = d.text; updateStatus();
    post({ cmd: 'n.save', path: d.path, text: d.eol === '\r\n' ? d.text.replace(/\n/g, '\r\n') : d.text, hash: d.hash });
  }
  handlers['n.saved'] = m => {
    const d = docs.get(m.path); if (!d) return;
    d.hash = m.hash; d.saving = false; d.dirty = d.text !== d.sent;
    if (d.dirty) { d.again = false; save(d); } else if (d.closeAfter) { dropDoc(d.path); return; }
    renderTabs(); if (m.path === cur) updateStatus();
  };

  // ---------- changements venus d'ailleurs ----------
  function adopt(d, m) {
    d.text = String(m.text).replace(/\r\n/g, '\n'); d.eol = m.text.includes('\r\n') ? '\r\n' : '\n'; d.hash = m.hash; d.dirty = false; d.saving = false; clearTimeout(d.timer);
    if (d.path === cur) { const s = ed.selectionStart, t = ed.scrollTop; ed.value = d.text; ed.setSelectionRange(Math.min(s, d.text.length), Math.min(s, d.text.length)); ed.scrollTop = t; renderPreview(); updateStatus(); }
    renderTabs(); closeNotice();
  }
  handlers['n.changed'] = m => {
    const d = docs.get(m.path); if (!d) return;
    const incoming = String(m.text).replace(/\r\n/g, '\n');
    if (incoming === d.text) { d.hash = m.hash; return; }   // c'est ce qu'on vient d'écrire
    if (!d.dirty) { adopt(d, m); say('« ' + titleOf(m.path) + ' » a été mise à jour depuis le disque.'); return; }
    if (m.path === cur) notice(`« ${titleOf(m.path)} » a été modifiée ailleurs pendant que tu écrivais.`, [['Garder ma version', () => { closeNotice(); post({ cmd: 'n.force', path: d.path, text: d.eol === '\r\n' ? d.text.replace(/\n/g, '\r\n') : d.text }); }], ['Recharger', () => adopt(d, m)]]);
    else d.conflict = m;
  };
  handlers['n.gone'] = m => {
    const d = docs.get(m.path); if (!d) return;
    if (!d.dirty) { dropDoc(m.path); say('« ' + baseName(m.path) + ' » n\'existe plus : onglet fermé.'); return; }
    if (m.path === cur) notice(`« ${titleOf(m.path)} » a été supprimée ou déplacée, mais tu as des modifications non enregistrées.`, [['Recréer la note', () => { closeNotice(); post({ cmd: 'n.create', path: d.path, text: d.eol === '\r\n' ? d.text.replace(/\n/g, '\r\n') : d.text }); }], ['Fermer sans enregistrer', () => dropDoc(d.path)]]);
  };
  function conflict(path) {
    const d = docs.get(path); if (!d) return; d.saving = false;
    if (path === cur) notice(`« ${titleOf(path)} » a été modifiée ailleurs : ton enregistrement automatique est suspendu.`, [['Garder ma version', () => { closeNotice(); post({ cmd: 'n.force', path, text: d.eol === '\r\n' ? d.text.replace(/\n/g, '\r\n') : d.text }); }], ['Recharger (perdre mes changements)', () => { closeNotice(); pending.set(path, { reload: true, activate: false }); post({ cmd: 'n.read', path }); }]]);
  }
  function notice(text, buttons) {
    const n = $('notice'); n.hidden = false; n.innerHTML = `<span>${esc(text)}</span>` + buttons.map((b, i) => `<button class="btn" data-i="${i}">${esc(b[0])}</button>`).join('');
    n.onclick = e => { const b = e.target.closest('[data-i]'); if (b) buttons[+b.dataset.i][1](); };
    setStatus('Conflit à régler', 'red');
  }
  function closeNotice() { const n = $('notice'); n.hidden = true; n.innerHTML = ''; }

  // ---------- aperçu : liens, étiquettes, cases à cocher ----------
  function gotoHeading(h) {
    const id = 'h-' + Md.slug(h); const el = pv.querySelector('#' + CSS.escape(id));
    if (el) { el.scrollIntoView({ block: 'start' }); const line = +el.dataset.line; if (mode !== 'view' && Number.isFinite(line)) { const off = ed.value.split('\n').slice(0, line).join('\n').length + (line ? 1 : 0); ed.setSelectionRange(off, off); } }
  }
  async function createFromLink(target) {
    const name = target.split('/').map(cleanName).filter(Boolean).join('/'); if (!name) return;
    if (await CdxFrame.dialog({ title: 'Créer la note', text: `La note « ${name} » n'existe pas encore.\nLa créer à la racine du coffre ?`, icon: 'editer', yes: 'Créer', no: 'Annuler' })) post({ cmd: 'n.create', path: name + '.md', text: '# ' + name.split('/').pop() + '\n\n' });
  }
  function toggleTask(line) {
    const d = docs.get(cur); if (!d) return;
    const lines = d.text.split('\n'); if (!(line >= 0 && line < lines.length) || !/\[( |x|X)\]/.test(lines[line])) return;
    lines[line] = lines[line].replace(/\[( |x|X)\]/, (m, c) => c === ' ' ? '[x]' : '[ ]');
    d.text = lines.join('\n'); ed.value = d.text; markDirty(d); renderPreview();
  }
  pv.addEventListener('click', e => {
    const cb = e.target.closest('input.task'); if (cb) { e.preventDefault(); toggleTask(+cb.dataset.line); return; }
    const a = e.target.closest('a'); if (!a) return;
    if (a.classList.contains('wl')) { if (a.dataset.path) openNote(a.dataset.path, { heading: a.dataset.heading }); else createFromLink(a.dataset.target); }
    else if (a.classList.contains('ext')) post({ cmd: 'n.openext', url: a.dataset.ext });
    else if (a.classList.contains('tg')) { $('filter').value = '#' + a.dataset.tag; renderTree(); }
  });
  // Ctrl+clic sur un [[lien]] dans l'éditeur : l'ouvrir.
  ed.addEventListener('click', e => {
    if (!(e.ctrlKey || e.metaKey)) return;
    const t = ed.value, i = ed.selectionStart; const ls0 = t.lastIndexOf('\n', i - 1) + 1; let le = t.indexOf('\n', i); if (le < 0) le = t.length;
    const line = t.slice(ls0, le), col = i - ls0; const re = /\[\[([^\[\]|#^]+?)(?:#([^\[\]|]*))?(?:\|[^\[\]]*)?\]\]/g; let m;
    while ((m = re.exec(line))) if (col >= m.index && col <= m.index + m[0].length) { const p = resolveLink(m[1].trim()); if (p) openNote(p, { heading: m[2] }); else createFromLink(m[1].trim()); return; }
  });
  // l'aperçu suit l'éditeur (défilement proportionnel)
  ed.addEventListener('scroll', () => { if (mode !== 'split') return; const r = ed.scrollTop / Math.max(1, ed.scrollHeight - ed.clientHeight); pv.scrollTop = r * Math.max(0, pv.scrollHeight - pv.clientHeight); });

  // ---------- aides d'écriture ----------
  // execCommand garde l'historique « Annuler » de la zone de texte, ce que setRangeText ne fait pas.
  function replaceRange(start, end, text, selStart, selEnd) {
    ed.focus(); ed.setSelectionRange(start, end); document.execCommand('insertText', false, text);
    if (selStart !== undefined) ed.setSelectionRange(selStart, selEnd ?? selStart);
  }
  function wrapSel(a, b) {
    const s = ed.selectionStart, e = ed.selectionEnd, t = ed.value.slice(s, e);
    if (t.startsWith(a) && t.endsWith(b) && t.length >= a.length + b.length) replaceRange(s, e, t.slice(a.length, t.length - b.length), s, e - a.length - b.length);
    else replaceRange(s, e, a + t + b, s + a.length, s + a.length + t.length);
  }
  function indentSel(back) {
    const t = ed.value, s = ed.selectionStart, e = ed.selectionEnd;
    if (s === e && !back) { replaceRange(s, e, '  '); return; }
    const a = t.lastIndexOf('\n', s - 1) + 1; let z = t.indexOf('\n', e); if (z < 0) z = t.length;
    const lines = t.slice(a, z).split('\n').map(l => back ? l.replace(/^( {1,2}|\t)/, '') : '  ' + l);
    const out = lines.join('\n'); replaceRange(a, z, out, a, a + out.length);
  }
  // Entrée dans une liste ou une citation : l'élément suivant (et une ligne vide termine la liste).
  function continueList() {
    const t = ed.value, s = ed.selectionStart; if (s !== ed.selectionEnd) return false;
    const a = t.lastIndexOf('\n', s - 1) + 1; const line = t.slice(a, s);
    const m = /^(\s*)((?:[-*+] \[[ xX]\] )|(?:[-*+] )|(?:\d+[.)] )|(?:> ?))(.*)$/.exec(line); if (!m) return false;
    if (m[3].trim() === '') { replaceRange(a, s, '', a); return true; }   // élément vide : on sort de la liste
    let marker = m[2]; const n = /^(\d+)([.)] )$/.exec(marker); if (n) marker = (+n[1] + 1) + n[2]; else marker = marker.replace(/\[[xX]\]/, '[ ]');
    replaceRange(s, s, '\n' + m[1] + marker); return true;
  }
  // ---------- mise en forme par raccourci (et menu Réglages > Mise en forme) ----------
  // Une ligne : son retrait, son préfixe (titre, case, puce, numéro, citation) et le reste.
  const PREFIX = /^(\s*)(#{1,6} |[-*+] \[[ xX]\] |[-*+] |\d+[.)] |> )?/;
  const kindOf = p => !p ? '' : p[0] === '#' ? 'h' : p[0] === '>' ? 'quote' : /\[/.test(p) ? 'task' : /^\d/.test(p) ? 'ol' : 'ul';
  // Pose un préfixe sur les lignes de la sélection ('h' niveau 1..6 ou 0 = paragraphe, 'ul', 'ol', 'task', 'quote'), ou le retire si toutes l'ont déjà ;
  // une case déjà là se coche / se décoche. Les lignes vides d'une sélection de plusieurs lignes sont laissées telles quelles.
  function setPrefix(kind, level = 1) {
    const t = ed.value, s = ed.selectionStart, e = ed.selectionEnd;
    const a = t.lastIndexOf('\n', s - 1) + 1; let z = t.indexOf('\n', e > s && t[e - 1] === '\n' ? e - 1 : e); if (z < 0) z = t.length;
    const lines = t.slice(a, z).split('\n'); const parts = lines.map(l => { const m = PREFIX.exec(l); return { ind: m[1], p: m[2] || '', body: l.slice(m[0].length) }; });
    const used = parts.filter(p => lines.length === 1 || p.body.trim() || p.p);
    const all = used.length > 0 && used.every(p => kindOf(p.p) === kind && (kind !== 'h' || p.p.length - 1 === level));
    const allChecked = used.every(p => /\[[xX]\]/.test(p.p));
    let n = 0;
    const out = parts.map(p => {
      if (!used.includes(p)) return p.ind + p.p + p.body;
      if (kind === 'task' && kindOf(p.p) === 'task') return p.ind + (allChecked ? p.p.replace(/\[[xX]\]/, '[ ]') : p.p.replace(/\[ \]/, '[x]')) + p.body;
      if (all || (kind === 'h' && level === 0)) return p.ind + p.body;
      const np = kind === 'h' ? '#'.repeat(level) + ' ' : kind === 'ul' ? '- ' : kind === 'ol' ? (++n) + '. ' : kind === 'task' ? '- [ ] ' : '> ';
      return p.ind + np + p.body;
    }).join('\n');
    if (out === t.slice(a, z)) return;
    if (s === e) replaceRange(a, z, out, Math.max(a, s + out.length - (z - a)));
    else replaceRange(a, z, out, a, a + out.length);
  }
  // Les actions de mise en forme : [touche (e.key en minuscules, ou le code physique DigitN : Ctrl+1 vaut aussi sur un clavier AZERTY), Maj, libellé, action, raccourci].
  const FORMATS = [
    ['b', false, 'Gras', () => wrapSel('**', '**'), 'Ctrl+B'], ['i', false, 'Italique', () => wrapSel('*', '*'), 'Ctrl+I'],
    ['x', true, 'Barré', () => wrapSel('~~', '~~'), 'Ctrl+Maj+X'], ['c', true, 'Code', () => wrapSel('`', '`'), 'Ctrl+Maj+C'],
    ['k', false, 'Lien [[…]]', () => wrapSel('[[', ']]'), 'Ctrl+K'], null,
    ['Digit1', false, 'Titre 1', () => setPrefix('h', 1), 'Ctrl+1'], ['Digit2', false, 'Titre 2', () => setPrefix('h', 2), 'Ctrl+2'], ['Digit3', false, 'Titre 3', () => setPrefix('h', 3), 'Ctrl+3'],
    ['Digit4', false, 'Titre 4', () => setPrefix('h', 4), 'Ctrl+4'], ['Digit5', false, 'Titre 5', () => setPrefix('h', 5), 'Ctrl+5'], ['Digit6', false, 'Titre 6', () => setPrefix('h', 6), 'Ctrl+6'],
    ['Digit0', false, 'Paragraphe (retirer le préfixe)', () => setPrefix('h', 0), 'Ctrl+0'], null,
    ['l', false, 'Liste à puces', () => setPrefix('ul'), 'Ctrl+L'], ['l', true, 'Liste numérotée', () => setPrefix('ol'), 'Ctrl+Maj+L'],
    ['enter', false, 'Case à cocher (cocher, décocher)', () => setPrefix('task'), 'Ctrl+Entrée'], ['q', true, 'Citation', () => setPrefix('quote'), 'Ctrl+Maj+Q'],
  ];
  function formatKey(e) { const k = e.key.toLowerCase(); const f = FORMATS.find(f => f && f[1] === e.shiftKey && (f[0].startsWith('Digit') ? e.code === f[0] : f[0] === k)); return f ? f[3] : null; }
  // Le même choix pour le menu : [libellé, action, { key }]. La sélection de la zone de texte survit au menu ; en aperçu seul, on passe en double.
  const formatMenu = () => FORMATS.map(f => f ? [f[2], () => { if (mode === 'view') setMode('split'); f[3](); }, { key: f[4] }] : null);
  ed.addEventListener('keydown', e => {
    if (e.key === 'Tab' && !e.ctrlKey) { e.preventDefault(); indentSel(e.shiftKey); }
    else if (e.key === 'Enter' && !e.shiftKey && !e.ctrlKey && !e.altKey && !e.isComposing) { if (continueList()) e.preventDefault(); }
    else if ((e.ctrlKey || e.metaKey) && !e.altKey) { const f = formatKey(e); if (f) { e.preventDefault(); f(); } }
  });

  function setMode(m) { mode = m; lsSet('mode', m); showDoc(); if (cur) { renderPreview(); if (m !== 'view') ed.focus(); } }
  function setWrap(on) { wrap = on; lsSet('wrap', on); showDoc(); }

