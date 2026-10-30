// Le menu contextuel de la liste.
  // ---------------- menu contextuel ----------------
  // Le menu « Sélection » : les mêmes actions que le clavier.
  $('cSel').onclick = () => {
    const r = $('cSel').getBoundingClientRect();
    KdxFrame.menu([
      ['Tout sélectionner', selectAll, { key: 'Ctrl+A' }],
      ['Ne rien sélectionner', selectNone, { key: 'Échap' }],
      ['Inverser la sélection', invertSelection, { key: 'Ctrl+Maj+I' }],
    ], r.left, r.bottom + 2, 'Sélection');
  };

  // Le menu « Affichage » : ce que montre la liste (réglages partagés par toutes les fenêtres).
  $('cView').onclick = () => {
    const r = $('cView').getBoundingClientRect(), on = k => settings[k] === true;
    KdxFrame.menu([
      ['Éléments cachés', () => setSetting('show.hidden', !on('show.hidden')), { key: on('show.hidden') ? 'oui' : 'non' }],
      ['Extensions des fichiers', () => setSetting('show.ext', !showExt()), { key: showExt() ? 'oui' : 'non' }],
    ], r.left, r.bottom + 2, 'Affichage');
  };

  document.addEventListener('mousedown', e => { if (!e.target.closest('.menu')) $('layer').querySelector('.menu')?.remove(); });
  $('files').oncontextmenu = e => {
    e.preventDefault(); const t = T(), r = e.target.closest('[data-i]'), s = selected();
    const on = r && s.length, one = s.length === 1;
    const items = on ? [[one && s[0].d ? 'Ouvrir' : 'Ouvrir', 'dossier', 'Entrée', () => open(s[0]), !one], ['Couper', '', 'Ctrl+X', () => copy(true)], ['Copier', '', 'Ctrl+C', () => copy(false)], ['Renommer', 'editer', 'F2', () => rename(), !one], null, ['Supprimer', 'supprimer', 'Suppr', del], ['Propriétés', '', '', () => post({ cmd: 'fs.props', path: pathOf(s[0]) }), !one]]
      : [['Nouveau dossier', 'dossier', '', newFolder, !t.path], ['Coller', '', 'Ctrl+V', paste, !t.path], null, ['Actualiser', 'maj', 'F5', reload], ['Propriétés', '', '', () => post({ cmd: 'fs.props', path: t.path }), !t.path]];
    const title = on ? (one ? s[0].n : `${s.length} éléments`) : baseOf(t.path);
    const m = document.createElement('div'); m.className = 'menu'; m.style.cssText = `left:${Math.min(e.clientX, innerWidth - 270)}px;top:${Math.min(e.clientY, innerHeight - 250)}px;min-width:250px`;
    m.innerHTML = `<div class="menu-title">${esc(title)}</div>` + items.map((x, i) => x ? `<div class="menu-item ${x[4] ? 'off' : ''}" data-m="${i}">${x[1] ? pix(x[1], 16) : '<span style="width:16px"></span>'}<span>${x[0]}</span><span class="k">${x[2]}</span></div>` : '<div class="menu-sep"></div>').join('');
    m.onclick = ev => { const it = ev.target.closest('[data-m]'); if (!it) return; const x = items[+it.dataset.m]; if (x[4]) return; m.remove(); x[3](); };
    $('layer').appendChild(m);
  };

