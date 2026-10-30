// La sélection à la souris (clic, Ctrl, Maj, double-clic) et l'ouverture.
  // ---------------- sélection et ouverture ----------------
  const pathOf = it => it.p || joinP(T().path, it.n);
  // Les éléments sélectionnés (ceux que le filtre cache ne comptent pas : on n'agit jamais sur ce qu'on ne voit pas).
  const selected = () => {
    const t = T(); if (!t || !t.items) return [];
    const bn = byName(t), f = t.filter.toLowerCase(), out = [];
    for (const n of t.sel) { const it = bn.get(n); if (it && (!f || it.n.toLowerCase().includes(f))) out.push(it); }
    return out;
  };
  function open(it) { if (it.d) { go(pathOf(it), 'push'); $('find').value = ''; T().filter = ''; } else post({ cmd: 'fs.open', path: pathOf(it) }); }
  $('files').onmousedown = e => {
    if (e.target.closest('input')) return;
    const r = e.target.closest('[data-i]'), t = T();
    if (!r) { if (e.button === 0 && !e.ctrlKey) { t.sel.clear(); show(true); } return; }   // Ctrl + espace vide : la sélection reste (le rectangle l'enrichira)
    const i = +r.dataset.i, it = t.rows[i]; t.cursor = i;
    // Le double-clic se lit ici et non dans « dblclick » : chaque clic redessine la liste, le deuxième tombe donc sur un élément neuf
    // et le navigateur ne produit plus d'événement dblclick.
    if (e.button === 0 && e.detail >= 2 && !e.ctrlKey && !e.shiftKey) { open(it); return; }
    if (e.button === 2 && t.sel.has(it.n)) return;
    if (e.shiftKey && t.anchor >= 0) { t.sel.clear(); for (let k = Math.min(i, t.anchor); k <= Math.max(i, t.anchor); k++) t.sel.add(t.rows[k].n); }
    else if (e.ctrlKey) { t.sel.has(it.n) ? t.sel.delete(it.n) : t.sel.add(it.n); t.anchor = i; }
    else { t.sel = new Set([it.n]); t.anchor = i; }
    show(true);
  };


  // Le rectangle de sélection : tirer depuis un espace vide de la liste sélectionne les lignes qu'il touche (Ctrl : en plus de la sélection actuelle). Il se dessine au-dessus de la liste
  // (la liste, elle, est redessinée à chaque pas) ; près d'un bord elle défile.
  let rb = null;
  $('files').addEventListener('pointerdown', e => {
    rb = null;
    if (e.button !== 0 || e.target.closest('input, [data-i]')) return;
    const t = T(); if (!t || !t.rows || !t.rows.length) return;
    const box = $('files').getBoundingClientRect();
    rb = { x: e.clientX, y: e.clientY - box.top + $('files').scrollTop, id: e.pointerId, base: e.ctrlKey ? new Set(t.sel) : new Set(), on: false, el: null };
  });
  document.addEventListener('pointermove', e => {
    if (!rb) return;
    if (!(e.buttons & 1)) { endRubber(); return; }
    const box = $('files').getBoundingClientRect(), t = T();
    if (!rb.on) {
      if (Math.hypot(e.clientX - rb.x, e.clientY - box.top - rb.y + $('files').scrollTop) < 5) return;
      rb.on = true; rb.el = document.createElement('div'); rb.el.className = 'rubber'; $('layer').appendChild(rb.el);
      try { $('files').setPointerCapture(rb.id); } catch { /* pas de pointeur réel (essais) */ }
    }
    if (e.clientY < box.top + 24) $('files').scrollTop -= 16; else if (e.clientY > box.bottom - 24) $('files').scrollTop += 16;
    const y1 = e.clientY - box.top + $('files').scrollTop, a = Math.min(rb.y, y1), z = Math.max(rb.y, y1);
    const x0 = Math.min(rb.x, e.clientX), x1 = Math.max(rb.x, e.clientX);
    const top = Math.max(box.top, Math.min(rb.y - $('files').scrollTop + box.top, e.clientY)), bot = Math.min(box.bottom, Math.max(rb.y - $('files').scrollTop + box.top, e.clientY));
    Object.assign(rb.el.style, { left: Math.max(box.left, x0) + 'px', top: top + 'px', width: Math.max(0, Math.min(box.right, x1) - Math.max(box.left, x0)) + 'px', height: Math.max(0, bot - top) + 'px' });
    const i0 = Math.max(0, Math.floor(a / ROW_H)), i1 = Math.min(t.rows.length - 1, Math.floor(z / ROW_H));
    const sel = new Set(rb.base); for (let i = i0; i <= i1; i++) sel.add(t.rows[i].n);
    t.sel = sel; t.anchor = i0; t.cursor = i1; show(true);
  });
  function endRubber() { if (rb && rb.el) { rb.el.remove(); try { $('files').releasePointerCapture(rb.id); } catch { /* idem */ } } rb = null; }
  document.addEventListener('pointerup', endRubber); document.addEventListener('pointercancel', endRubber);
