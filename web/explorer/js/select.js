// La sélection à la souris (clic, Ctrl, Maj, double-clic) et l'ouverture.
  // ---------------- sélection et ouverture ----------------
  const pathOf = it => it.p || joinP(T().path, it.n);
  const selected = () => { const t = T(); return (t.rows || []).filter(i => t.sel.has(i.n)); };
  function open(it) { if (it.d) { go(pathOf(it), 'push'); $('find').value = ''; T().filter = ''; } else post({ cmd: 'fs.open', path: pathOf(it) }); }
  $('files').onmousedown = e => {
    if (e.target.closest('input')) return;
    const r = e.target.closest('[data-i]'), t = T();
    if (!r) { if (e.button === 0) { t.sel.clear(); show(true); } return; }
    const i = +r.dataset.i, it = t.rows[i];
    // Le double-clic se lit ici et non dans « dblclick » : chaque clic redessine la liste, le deuxième tombe donc sur un élément neuf
    // et le navigateur ne produit plus d'événement dblclick.
    if (e.button === 0 && e.detail >= 2 && !e.ctrlKey && !e.shiftKey) { open(it); return; }
    if (e.button === 2 && t.sel.has(it.n)) return;
    if (e.shiftKey && t.anchor >= 0) { t.sel.clear(); for (let k = Math.min(i, t.anchor); k <= Math.max(i, t.anchor); k++) t.sel.add(t.rows[k].n); }
    else if (e.ctrlKey) { t.sel.has(it.n) ? t.sel.delete(it.n) : t.sel.add(it.n); t.anchor = i; }
    else { t.sel = new Set([it.n]); t.anchor = i; }
    show(true);
  };

