// Le glisser-déposer : dans la fenêtre, depuis et vers d'autres applications, conflits de noms.
  // ---------------- glisser-déposer ----------------
  // Une ligne (ou la sélection) se dépose sur un dossier de la liste, un lieu de la barre latérale ou un onglet. Comme dans Windows : sur le même disque on déplace, d'un disque
  // à l'autre on copie ; Ctrl copie, Maj déplace ; Échap annule. Sortir de la fenêtre avec les éléments les confie à Windows (glisser vers une autre application). Des fichiers venus
  // d'une autre application ou d'une autre fenêtre se déposent par-dessus (comme dans Windows : déplacés sur le même disque, copiés sinon ; Ctrl copie, Maj déplace), sur un dossier ou sur la liste (le dossier courant).
  const driveOf = p => (p || '').slice(0, 2).toUpperCase();
  const norm = p => (p || '').replace(/\\$/, '').toLowerCase();
  const within = (p, dir) => norm(dir) === norm(p) || norm(dir).startsWith(norm(p) + sep);   // dir est p ou se trouve dedans
  let dcand = null, drag = null, extOver = null;

  // Ce qui reçoit un dépôt sous le pointeur : un dossier de la liste, un lieu de la barre latérale ou un onglet ; `skip` : les éléments saisis (on ne dépose pas sur soi-même).
  function dropTarget(x, y, skip) {
    const el = document.elementFromPoint(x, y), t = T(); if (!el || !t) return null;
    let n;
    if ((n = el.closest('#files .fr[data-i]'))) { const it = t.rows && t.rows[+n.dataset.i]; return it && it.d && !it.drive && !(skip && skip.has(it.n)) ? { el: n, path: pathOf(it), name: it.n } : null; }
    if ((n = el.closest('#side [data-p]'))) return n.dataset.p ? { el: n, path: n.dataset.p, name: n.textContent.trim() } : null;
    if ((n = el.closest('#tabs [data-i]')) && n.dataset.i !== '+') { const tt = tabs[+n.dataset.i]; return tt && tt.path ? { el: n, path: tt.path, name: baseOf(tt.path) } : null; }
    return null;
  }
  // Que se passerait-il ? null : refusé (un dossier sur lui-même ou dans l'un de ses sous-dossiers, ou déplacer dans le dossier d'origine) ; sinon { copy }.
  function plan(target, paths, ev) {
    if (!target || !paths.length || paths.some(p => within(p, target.path))) return null;
    const copy = ev.ctrlKey || (!ev.shiftKey && paths.some(p => driveOf(p) !== driveOf(target.path)));
    if (!copy && paths.every(p => parentOf(p) != null && norm(parentOf(p)) === norm(target.path))) return null;
    return { copy };
  }
  const clearOver = () => document.querySelectorAll('.dnd-over').forEach(e => e.classList.remove('dnd-over'));
  function setOver(el) { clearOver(); if (el) el.classList.add('dnd-over'); }

  $('files').addEventListener('pointerdown', e => {
    dcand = null;
    if (e.button !== 0 || e.target.closest('input')) return;
    const t = T(); if (!t || !t.path || !e.target.closest('[data-i]')) return;
    dcand = { x: e.clientX, y: e.clientY, id: e.pointerId };
  });
  function startDrag() {
    const items = selected().filter(i => !i.drive); if (!items.length) return false;
    const ghost = document.createElement('div'); ghost.className = 'dnd-ghost';
    drag = { paths: items.map(pathOf), names: new Set(items.map(i => i.n)), ghost, id: dcand.id, target: null, plan: null, one: items.length === 1 ? items[0] : null };
    $('layer').appendChild(ghost);
    document.querySelectorAll('#files .fr.sel').forEach(r => r.classList.add('dnd-src'));
    document.body.classList.add('dnd');
    try { $('files').setPointerCapture(dcand.id); } catch { /* la page de démonstration n'a pas de pointeur réel */ }
    return true;
  }
  function endDrag() {
    if (drag) { drag.ghost.remove(); try { $('files').releasePointerCapture(drag.id); } catch { /* idem */ } }
    drag = null; dcand = null; clearOver();
    document.body.classList.remove('dnd', 'cp', 'mv', 'no');
    document.querySelectorAll('.dnd-src').forEach(r => r.classList.remove('dnd-src'));
  }
  function moveDrag(e) {
    const g = drag.ghost;
    if (e.clientX < 0 || e.clientY < 0 || e.clientX >= innerWidth || e.clientY >= innerHeight) {   // le pointeur a quitté la fenêtre : Windows prend le relais
      const paths = drag.paths; endDrag(); post({ cmd: 'fs.dragout', paths }); return;
    }
    const tg = dropTarget(e.clientX, e.clientY, drag.names), pl = plan(tg, drag.paths, e);
    drag.target = tg; drag.plan = pl;
    setOver(pl ? tg.el : null);
    const what = drag.one ? drag.one.n : drag.paths.length + ' éléments';
    g.innerHTML = pix(drag.one ? iconOf(drag.one) : 'dossier', 16) + `<span class="t">${esc(what)}${pl ? `<i>${pl.copy ? 'Copier' : 'Déplacer'} vers « ${esc(tg.name)} »</i>` : ''}</span>`;
    g.style.left = Math.min(e.clientX + 14, innerWidth - g.offsetWidth - 4) + 'px'; g.style.top = Math.min(e.clientY + 12, innerHeight - g.offsetHeight - 4) + 'px';
    document.body.classList.toggle('cp', !!pl && pl.copy); document.body.classList.toggle('mv', !!pl && !pl.copy); document.body.classList.toggle('no', !pl);
    const fr = $('files').getBoundingClientRect();   // près du bord de la liste : elle défile
    if (e.clientX >= fr.left && e.clientX <= fr.right) { if (e.clientY < fr.top + 28 && e.clientY >= fr.top) $('files').scrollTop -= 14; else if (e.clientY > fr.bottom - 28 && e.clientY <= fr.bottom) $('files').scrollTop += 14; }
  }
  document.addEventListener('pointermove', e => {
    if (!drag) {
      if (!dcand) return;
      if (!(e.buttons & 1)) { dcand = null; return; }
      if (Math.hypot(e.clientX - dcand.x, e.clientY - dcand.y) < 6 || !startDrag()) return;
    }
    moveDrag(e);
  });
  document.addEventListener('pointerup', e => {
    if (!drag) { dcand = null; return; }
    const tg = drag.target, pl = drag.plan, paths = drag.paths; endDrag();
    if (tg && pl) post({ cmd: 'fs.drop', paths, dir: tg.path, move: !pl.copy, tab: T().id });
  });
  document.addEventListener('pointercancel', () => { if (drag) endDrag(); });
  document.addEventListener('keydown', e => { if (drag && e.key === 'Escape') { e.stopPropagation(); endDrag(); } }, true);
  window.addEventListener('blur', () => { if (drag) endDrag(); });
  handlers['fs.dragend'] = () => reload();
  // Des noms déjà pris dans la destination : le programme n'a rien fait et demande. La réponse repart en fs.drop avec la consigne (replace, keep, skip).
  handlers['fs.conflict'] = m => {
    const n = m.names.length, where = baseOf(m.dir);
    const what = n === 1 ? `« ${esc(m.names[0])} » existe déjà dans « ${esc(where)} ».` : `${n} éléments existent déjà dans « ${esc(where)} » (${esc(m.names.slice(0, 3).join(', '))}${n > 3 ? '…' : ''}).`;
    $('layer').innerHTML = `<div class="veil"><div class="win active"><div class="tb"><span class="tb-title">Éléments existants</span></div><div class="cb"><div class="row" style="gap:14px;padding:10px 4px">${pix('avertissement', 32)}<div style="line-height:1.5">${what}<br>Que faire ?</div></div>
      <div class="btns"><button class="primary" data-c="keep">Garder les deux</button><button class="btn" data-c="replace">Remplacer</button><button class="btn" data-c="skip">Ignorer</button><button class="btn" data-c="cancel">Annuler</button></div></div></div></div>`;
    const close = () => { $('layer').innerHTML = ''; $('files').focus(); };
    $('layer').onclick = e => { const b = e.target.closest('[data-c]'); if (!b) return; close(); if (b.dataset.c !== 'cancel') post({ cmd: 'fs.drop', paths: m.paths, dir: m.dir, move: m.move, tab: m.tab, conflict: b.dataset.c }); };
    $('layer').onkeydown = e => { if (e.key === 'Escape') close(); };
    $('layer').querySelector('[data-c="keep"]').focus();
  };

  // Des fichiers glissés depuis une autre application : WebView2 les présente comme un glisser « Files » ; leurs chemins sont rendus par postMessageWithAdditionalObjects.
  const hasFiles = e => e.dataTransfer && [...e.dataTransfer.types].includes('Files');
  function extTarget(e) { const t = dropTarget(e.clientX, e.clientY, null); if (t) return t; const cur = T(); return cur && cur.path ? { el: $('files'), path: cur.path, name: baseOf(cur.path) } : null; }
  document.addEventListener('dragover', e => {
    if (!hasFiles(e)) return;
    const tg = extTarget(e); if (!tg) { e.dataTransfer.dropEffect = 'none'; return; }
    e.preventDefault(); e.dataTransfer.dropEffect = e.shiftKey ? 'move' : 'copy'; setOver(tg.el === $('files') ? null : tg.el);
  });
  document.addEventListener('dragleave', e => { if (!e.relatedTarget) clearOver(); });
  document.addEventListener('drop', e => {
    if (!hasFiles(e)) return;
    e.preventDefault(); clearOver();
    const tg = extTarget(e); if (!tg || !hasHost || !e.dataTransfer.files.length) return;
    window.chrome.webview.postMessageWithAdditionalObjects({ cmd: 'fs.dropfiles', dir: tg.path, shift: e.shiftKey, ctrl: e.ctrlKey, tab: T().id }, e.dataTransfer.files);
  });

