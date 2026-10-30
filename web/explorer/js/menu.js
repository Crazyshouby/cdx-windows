// Les menus : « Sélection », « Affichage » (barre d'outils) et le menu contextuel de la liste (éléments et fond), avec leurs sous-menus (Ouvrir avec, Envoyer vers, Nouveau, Trier par, Afficher).
  // ---------------- menus ----------------
  // Ce que Windows fournit au menu (destinations « Envoyer vers », modèles « Nouveau ») est demandé une fois au démarrage ; les programmes de « Ouvrir avec » le sont par extension et gardés.
  let menuData = { sendto: [], news: [] };
  const openWithCache = {};
  handlers.menudata = m => { menuData = { sendto: m.sendto || [], news: m.news || [] }; };
  handlers.openwith = m => { openWithCache[m.ext] = m.items || []; };
  const askOpenWith = ext => { if (ext && ext !== '.' && openWithCache[ext] === undefined) { openWithCache[ext] = null; post({ cmd: 'fs.openwith.list', ext }); } };
  // Quand un seul fichier est sélectionné, on prépare dès maintenant « Ouvrir avec » pour son extension (le menu s'ouvre alors sans attendre).
  function prefetchOpenWith() { const t = T(); if (!t || t.sel.size !== 1) return; const it = byName(t).get([...t.sel][0]); if (it && !it.d && !it.drive) askOpenWith('.' + extOf(it.b ?? it.n)); }

  const viewItems = () => [
    ['Éléments cachés', () => setSetting('show.hidden', settings['show.hidden'] !== true), { check: settings['show.hidden'] === true, key: 'Ctrl+H' }],
    ['Extensions des fichiers', () => setSetting('show.ext', !showExt()), { check: showExt() }],
  ];
  const SORTS = [['n', 'Nom'], ['m', 'Date de modification'], ['t', 'Type'], ['s', 'Taille']];
  const sortItems = () => { const t = T(); return [
    ...SORTS.map(([k, name]) => [name, () => { t.sortK = k; show(true); }, { check: t.sortK === k }]), null,
    ['Croissant', () => { t.rev = false; show(true); }, { check: !t.rev }], ['Décroissant', () => { t.rev = true; show(true); }, { check: t.rev }],
  ]; };

  // « Nouveau » : un dossier, un raccourci, puis les modèles que Windows connaît ; le nouvel élément est sélectionné et son nom s'édite aussitôt.
  const newFile = ext => { const t = T(); if (!t.path) return; t.wantRename = true; post({ cmd: 'fs.new', dir: t.path, ext, tab: t.id }); };
  async function newShortcut() {
    const t = T(); if (!t.path) return;
    const target = await askText({ title: 'Nouveau raccourci', text: 'Vers quoi ? (un chemin ou une adresse web)', ok: 'Créer' });
    if (!target) return;
    const base = target.replace(/[\\/]+$/, '').split(/[\\/]/).pop().replace(/^https?:$/, '') || 'Raccourci';
    t.wantRename = true; post({ cmd: 'fs.newshortcut', dir: t.path, name: base.replace(/[<>:"|?*]/g, '').slice(0, 60) || 'Raccourci', target, tab: t.id });
  }
  const newItems = () => [
    ['Dossier', newFolder, { icon: 'dossier', key: 'Ctrl+Maj+N' }],
    ['Raccourci…', newShortcut],
    ...(menuData.news.length ? [null, ...menuData.news.map(k => [k.name, () => newFile(k.ext)])] : []),
  ];

  const openWithItems = (it, ext) => {
    const path = pathOf(it); askOpenWith(ext);
    const list = openWithCache[ext];
    const apps = list === undefined || list === null ? [['Recherche des programmes…', null, { disabled: true }]]
      : list.length ? list.map(a => [a.name, () => post({ cmd: 'fs.openwith', exe: a.exe, path })]) : [['Aucun programme connu', null, { disabled: true }]];
    return [...apps, null, ['Choisir une autre application…', () => post({ cmd: 'fs.openas', path })]];
  };
  const sendToItems = paths => {
    const t = T();
    return menuData.sendto.length ? menuData.sendto.map(d => [d.name, () => post({ cmd: 'fs.sendto', item: d.path, paths, tab: t.id })]) : [['Aucune destination', null, { disabled: true }]];
  };

  // Le vrai menu de Windows pour ces éléments (tout ce que d'autres logiciels y ajoutent) : il s'ouvre sous le pointeur ; Windows exécute lui-même la commande choisie.
  const moreOptions = paths => post({ cmd: 'fs.shellmenu', paths, shift: lastShift });
  let lastShift = false; document.addEventListener('contextmenu', e => { lastShift = e.shiftKey; }, true);   // Maj + clic droit : le menu étendu de Windows

  // Le menu d'un ou plusieurs éléments sélectionnés.
  function itemMenu(s, t) {
    const one = s.length === 1, it = s[0], paths = s.map(pathOf), ext = one && !it.d && !it.drive ? '.' + extOf(it.b ?? it.n) : null;
    const out = [['Ouvrir', () => open(it), { icon: 'dossier', key: 'Entrée', disabled: !one }]];
    if (t.search && one) out.push(["Ouvrir l'emplacement du dossier", () => showInFolder(it)]);
    if (ext) out.push(['Ouvrir avec', null, { sub: () => openWithItems(it, ext) }]);
    if (one && it.d) out.push(['Ouvrir dans un nouvel onglet', () => addTab(pathOf(it))], ['Ouvrir dans une nouvelle fenêtre', () => post({ cmd: 'fs.newwindow', path: pathOf(it) })]);
    if (one) out.push(['Ouvrir dans un terminal', () => post({ cmd: 'fs.terminal', path: it.d ? pathOf(it) : (parentOf(pathOf(it)) || t.path) })]);
    out.push(null,
      ['Couper', () => copy(true), { key: 'Ctrl+X' }], ['Copier', () => copy(false), { key: 'Ctrl+C' }], ['Copier le chemin', copyPath, { key: 'Ctrl+Maj+C' }], null,
      ['Envoyer vers', null, { sub: () => sendToItems(paths) }], null,
      ['Renommer', () => rename(), { icon: 'editer', key: 'F2', disabled: !one || !!t.search }], ['Supprimer', del, { icon: 'supprimer', key: 'Suppr' }], null,
      ['Propriétés', showProps, { key: 'Alt+Entrée' }],
      ["Plus d'options…", () => moreOptions(paths), { key: 'Maj+F10' }]);
    return out;
  }
  // Le menu du fond de la liste (aucun élément visé).
  function backgroundMenu(t) {
    const no = !t.path || !!t.search;   // pendant une recherche la liste montre des résultats, pas un dossier : rien à y créer ni coller
    return [
      ['Nouveau', null, { sub: newItems, disabled: no }],
      ['Coller', paste, { key: 'Ctrl+V', disabled: no }], null,
      ['Trier par', null, { sub: sortItems }],
      ['Afficher', null, { sub: viewItems }],
      ['Actualiser', reload, { icon: 'maj', key: 'F5' }], null,
      ['Ouvrir dans un terminal', () => post({ cmd: 'fs.terminal', path: t.path }), { disabled: no }],
      ['Propriétés', showProps, { key: 'Alt+Entrée', disabled: no }],
      ["Plus d'options…", () => moreOptions([t.path]), { disabled: no }],
    ];
  }

  $('files').oncontextmenu = e => {
    e.preventDefault(); const t = T(), r = e.target.closest('[data-i]');
    if (r && !t.sel.has(t.rows[+r.dataset.i].n)) { t.sel = new Set([t.rows[+r.dataset.i].n]); t.anchor = +r.dataset.i; t.cursor = +r.dataset.i; show(true); }   // clic droit sur un élément non sélectionné : il le devient
    const s = selected(), on = r && s.length;
    const title = on ? (s.length === 1 ? dispName(s[0]) : `${s.length} éléments`) : baseOf(t.path);
    KdxFrame.menu(on ? itemMenu(s, t) : backgroundMenu(t), e.clientX, e.clientY, title);
  };

  // Le menu « Sélection » : les mêmes actions que le clavier.
  $('cSel').onclick = () => {
    const r = $('cSel').getBoundingClientRect();
    KdxFrame.menu([
      ['Tout sélectionner', selectAll, { key: 'Ctrl+A' }],
      ['Ne rien sélectionner', selectNone, { key: 'Échap' }],
      ['Inverser la sélection', invertSelection, { key: 'Ctrl+Maj+I' }],
    ], r.left, r.bottom + 2, 'Sélection');
  };

  // Le menu « Affichage » : ce que montre la liste (réglages partagés par toutes les fenêtres) et le tri.
  $('cView').onclick = () => {
    const r = $('cView').getBoundingClientRect();
    KdxFrame.menu([...viewItems(), null, ['Trier par', null, { sub: sortItems }]], r.left, r.bottom + 2, 'Affichage');
  };
