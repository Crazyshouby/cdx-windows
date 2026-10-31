// Le clavier : une table de raccourcis (la même sert à l'aide « Raccourcis clavier », F1), la saisie pour sauter à un élément et les déplacements du curseur.
  // ---------------- clavier ----------------
  // Déplace le curseur de la liste (la ligne « active ») : `target(c)` donne l'indice visé d'après l'indice courant c (-1 : aucun) ; `extend` (Maj) étend la sélection depuis l'ancre.
  function moveCursor(target, extend) {
    const t = T(); if (!t || !t.rows || !t.rows.length) return; const n = t.rows.length;
    let cur = t.cursor ?? -1; if (cur < 0 || cur >= n) cur = t.rows.findIndex(r => t.sel.has(r.n));
    const to = Math.max(0, Math.min(n - 1, target(cur)));
    if (extend) { const a = t.anchor >= 0 ? t.anchor : (cur < 0 ? to : cur); t.sel = new Set(); for (let k = Math.min(a, to); k <= Math.max(a, to); k++) t.sel.add(t.rows[k].n); t.anchor = a; }
    else { t.sel = new Set([t.rows[to].n]); t.anchor = to; }
    t.cursor = to; show(true); ensureVisible(to);
  }
  const pageRows = () => rowsPerPage();   // (views.js : en icônes, une page = des rangées de plusieurs cases)
  const invertSelection = () => { const t = T(); if (!t || !t.rows) return; t.sel = new Set(t.rows.filter(r => !t.sel.has(r.n)).map(r => r.n)); show(true); };
  const selectAll = () => { const t = T(); if (!t || !t.rows) return; t.sel = new Set(t.rows.map(r => r.n)); show(true); };
  const selectNone = () => { const t = T(); if (!t) return; t.sel.clear(); show(true); };
  const showProps = () => { const t = T(), s = selected(); const p = s.length ? pathOf(s[0]) : t.path; if (p) post({ cmd: 'fs.props', path: p }); };
  const copyPath = () => { const s = selected(), t = T(); const paths = s.length ? s.map(pathOf) : (t.path ? [t.path] : []); if (!paths.length) return; post({ cmd: 'fs.copypath', paths }); $('st2').textContent = paths.length > 1 ? 'Chemins copiés' : 'Chemin copié'; };
  const toggleHidden = () => setSetting('show.hidden', settings['show.hidden'] !== true);
  const closeTab = () => { if (tabs.length > 1) { post({ cmd: 'fs.watch', tab: tabs[cur].id, path: '' }); tabs.splice(cur, 1); cur = Math.min(cur, tabs.length - 1); drawTabs(); show(); } };
  const switchTab = d => { if (tabs.length < 2) return; cur = (cur + d + tabs.length) % tabs.length; drawTabs(); show(); if (T().stale) { T().stale = false; reload(); } };
  const focusFind = () => { $('find').focus(); $('find').select(); };

  // La table : { k : touches, t : intitulé, g : groupe de l'aide, run : action }. Plusieurs touches pour une même action : séparées par « | ».
  const KEYS = [
    { g: 'Navigation', k: 'Alt+ArrowLeft', t: 'Dossier précédent', run: () => $('back').click() },
    { g: 'Navigation', k: 'Alt+ArrowRight', t: 'Dossier suivant', run: () => $('fwd').click() },
    { g: 'Navigation', k: 'Alt+ArrowUp|Backspace', t: 'Dossier parent', run: () => $('up').click() },
    { g: 'Navigation', k: 'F5|Ctrl+R', t: 'Actualiser', run: () => reload() },
    { g: 'Navigation', k: 'Alt+D|F4|Ctrl+L', t: "Aller à la barre d'adresse", run: () => $('addr').focus() },
    { g: 'Navigation', k: 'Ctrl+E', t: 'Rechercher dans les sous-dossiers', run: () => startSearch() },
    { g: 'Navigation', k: 'Ctrl+F|F3', t: 'Filtrer le dossier affiché', run: focusFind },
    { g: 'Navigation', k: 'Ctrl+T', t: 'Nouvel onglet', run: () => addTab(T().path) },
    { g: 'Navigation', k: 'Ctrl+W', t: "Fermer l'onglet", run: closeTab },
    { g: 'Navigation', k: 'Ctrl+Tab', t: 'Onglet suivant', run: () => switchTab(1) },
    { g: 'Navigation', k: 'Ctrl+Shift+Tab', t: 'Onglet précédent', run: () => switchTab(-1) },
    { g: 'Navigation', k: 'Ctrl+N', t: 'Nouvelle fenêtre', run: () => post({ cmd: 'fs.newwindow', path: T().path || '' }) },
    { g: 'Sélection', k: 'ArrowDown', t: 'Élément suivant (en icônes : la rangée du dessous)', run: () => moveCursor(c => c + lineStep(), false) },
    { g: 'Sélection', k: 'ArrowUp', t: 'Élément précédent (en icônes : la rangée du dessus)', run: () => moveCursor(c => c < 0 ? Infinity : c - lineStep(), false) },
    { g: 'Sélection', k: 'ArrowRight', t: 'Élément suivant', run: () => moveCursor(c => c + 1, false) },
    { g: 'Sélection', k: 'ArrowLeft', t: 'Élément précédent', run: () => moveCursor(c => c < 0 ? Infinity : c - 1, false) },
    { g: 'Sélection', k: 'Shift+ArrowDown', t: 'Étendre la sélection vers le bas', run: () => moveCursor(c => c + lineStep(), true) },
    { g: 'Sélection', k: 'Shift+ArrowUp', t: 'Étendre la sélection vers le haut', run: () => moveCursor(c => c < 0 ? Infinity : c - lineStep(), true) },
    { g: 'Sélection', k: 'Shift+ArrowRight', t: 'Étendre la sélection au suivant', run: () => moveCursor(c => c + 1, true) },
    { g: 'Sélection', k: 'Shift+ArrowLeft', t: 'Étendre la sélection au précédent', run: () => moveCursor(c => c < 0 ? Infinity : c - 1, true) },
    { g: 'Sélection', k: 'Home', t: 'Premier élément', run: () => moveCursor(() => 0, false) },
    { g: 'Sélection', k: 'End', t: 'Dernier élément', run: () => moveCursor(() => Infinity, false) },
    { g: 'Sélection', k: 'Shift+Home', t: "Sélectionner jusqu'au premier", run: () => moveCursor(() => 0, true) },
    { g: 'Sélection', k: 'Shift+End', t: "Sélectionner jusqu'au dernier", run: () => moveCursor(() => Infinity, true) },
    { g: 'Sélection', k: 'PageDown', t: 'Une page plus bas', run: () => moveCursor(c => c + pageRows(), false) },
    { g: 'Sélection', k: 'PageUp', t: 'Une page plus haut', run: () => moveCursor(c => c - pageRows(), false) },
    { g: 'Sélection', k: 'Shift+PageDown', t: 'Étendre d’une page vers le bas', run: () => moveCursor(c => c + pageRows(), true) },
    { g: 'Sélection', k: 'Shift+PageUp', t: 'Étendre d’une page vers le haut', run: () => moveCursor(c => c - pageRows(), true) },
    { g: 'Sélection', k: 'Ctrl+A', t: 'Tout sélectionner', run: selectAll },
    { g: 'Sélection', k: 'Ctrl+Shift+I', t: 'Inverser la sélection', run: invertSelection },
    { g: 'Sélection', k: 'Escape', t: 'Désélectionner (puis fermer la recherche)', run: () => { const t = T(); if (t.search && t.sel.size === 0) endSearch(true); else selectNone(); } },
    { g: 'Fichiers', k: 'Enter', t: 'Ouvrir (dans la corbeille : restaurer)', run: () => { if (inTrash()) { restoreSel(); return; } const s = selected(); if (s.length === 1) open(s[0]); } },
    { g: 'Fichiers', k: 'Alt+Enter', t: 'Propriétés', run: showProps },
    { g: 'Fichiers', k: 'F2', t: 'Renommer', run: () => rename() },
    { g: 'Fichiers', k: 'Delete', t: 'Supprimer (corbeille) ; dans la corbeille, effacer', run: del },
    { g: 'Fichiers', k: 'Shift+Delete', t: 'Supprimer définitivement', run: delPerm },
    { g: 'Fichiers', k: 'Ctrl+Z', t: 'Annuler', run: undo },
    { g: 'Fichiers', k: 'Ctrl+Y|Ctrl+Shift+Z', t: 'Rétablir', run: redo },
    { g: 'Fichiers', k: 'Ctrl+C', t: 'Copier', run: () => copy(false) },
    { g: 'Fichiers', k: 'Ctrl+X', t: 'Couper', run: () => copy(true) },
    { g: 'Fichiers', k: 'Ctrl+V', t: 'Coller', run: paste },
    { g: 'Fichiers', k: 'Ctrl+Shift+C', t: 'Copier le chemin', run: copyPath },
    { g: 'Fichiers', k: 'Ctrl+Shift+N', t: 'Nouveau dossier', run: newFolder },
    { g: 'Fichiers', k: 'Shift+F10', t: "Menu complet de Windows (Plus d'options)", run: () => { const s = selected(), t = T(); const paths = s.length ? s.map(pathOf) : (t.path && !t.search ? [t.path] : []); if (paths.length) post({ cmd: 'fs.shellmenu', paths, shift: false }); } },
    { g: 'Affichage', k: 'Ctrl+Shift+1', t: 'Grandes icônes', run: () => setView('icons-l') },
    { g: 'Affichage', k: 'Ctrl+Shift+2', t: 'Icônes moyennes', run: () => setView('icons-m') },
    { g: 'Affichage', k: 'Ctrl+Shift+3', t: 'Petites icônes', run: () => setView('icons-s') },
    { g: 'Affichage', k: 'Ctrl+Shift+5', t: 'Liste', run: () => setView('list') },
    { g: 'Affichage', k: 'Ctrl+Shift+6', t: 'Détails', run: () => setView('details') },
    { g: 'Affichage', k: 'Ctrl+H', t: 'Éléments cachés', run: toggleHidden },
    { g: 'Affichage', k: 'Ctrl+J', t: 'Fenêtre des transferts', run: () => post({ cmd: 'fs.showxfers' }) },
    { g: 'Affichage', k: 'Alt+P', t: "Volet d'aperçu", run: () => { if (typeof setPreview === 'function') setPreview(!pvOn); } },
    { g: 'Affichage', k: 'F1|Ctrl+/', t: 'Raccourcis clavier', run: () => keyHelp() },
  ];
  // « ArrowDown », « Ctrl+Shift+I »... : le nom d'un événement clavier, dans l'ordre Ctrl, Alt, Maj.
  const keyName = e => {
    let k = e.key === ' ' ? 'Space' : e.key.length === 1 ? e.key.toUpperCase() : e.key;
    return (e.ctrlKey ? 'Ctrl+' : '') + (e.altKey ? 'Alt+' : '') + (e.shiftKey && !(e.key.length === 1 && !/[A-Za-z]/.test(e.key)) ? 'Shift+' : '') + k;
  };
  const KEYMAP = new Map(); for (const s of KEYS) for (const k of s.k.split('|')) KEYMAP.set(k, s);
  const keyLabel = k => k.split('|').map(x => x.replace(/Arrow(Left|Right|Up|Down)/, (m, d) => ({ Left: '←', Right: '→', Up: '↑', Down: '↓' }[d])).replace('Escape', 'Échap').replace('Delete', 'Suppr').replace('Backspace', 'Retour')).join('  ou  ');

  // L'aide : la table, par groupe.
  function keyHelp() {
    const groups = [...new Set(KEYS.map(s => s.g))];
    $('layer').innerHTML = `<div class="veil"><div class="win active"><div class="tb"><span class="tb-title">Raccourcis clavier</span></div><div class="cb"><div class="kb">${groups.map(g => `<div class="kbg">${esc(g)}</div>` + KEYS.filter(s => s.g === g).map(s => `<span class="kbk">${esc(keyLabel(s.k))}</span><span>${esc(s.t)}</span>`).join('')).join('')}</div><div class="btns"><button class="primary" id="kbClose">Fermer</button></div></div></div></div>`;
    const close = () => { $('layer').innerHTML = ''; $('files').focus(); };
    $('kbClose').onclick = close; $('kbClose').focus();
    $('layer').onkeydown = e => { if (e.key === 'Escape') close(); };
  }

  // La saisie pour sauter : taper « be » va au premier nom qui commence par « be » ; une lettre répétée (« aaa ») passe d'un nom en « a » au suivant ; la saisie s'oublie après une seconde.
  let taBuf = '', taTimer = 0;
  function typeAhead(ch) {
    const t = T(); if (!t || !t.rows || !t.rows.length) return false;
    clearTimeout(taTimer); taTimer = setTimeout(() => { taBuf = ''; }, 1000);
    taBuf += ch.toLocaleLowerCase('fr');
    const cyc = taBuf.length > 1 && [...taBuf].every(c => c === taBuf[0]), needle = cyc ? taBuf[0] : taBuf;
    const n = t.rows.length; let cur = t.cursor ?? -1; if (cur < 0 || cur >= n) cur = t.rows.findIndex(r => t.sel.has(r.n));
    const start = (cyc || needle.length === 1) ? cur + 1 : Math.max(cur, 0);   // un début plus long reste sur l'élément courant s'il correspond encore
    for (let k = 0; k < n; k++) {
      const i = (start + k) % n;
      if (dispName(t.rows[i]).toLocaleLowerCase('fr').startsWith(needle)) { moveCursor(() => i, false); return true; }
    }
    return false;
  }

  document.addEventListener('keydown', e => {
    if (e.target && e.target.matches && e.target.matches('input, textarea, select')) return;   // (les essais envoient l'événement au document, qui n'a pas de matches)
    if ($('layer').querySelector('.veil, .menu') || document.querySelector('.menu')) return;   // une boîte ou un menu est ouvert : il a ses propres touches
    const t = T(); if (!t || !t.rows) return;
    const s = KEYMAP.get(keyName(e));
    if (s) { e.preventDefault(); s.run(); return; }
    if (e.key.length === 1 && !e.ctrlKey && !e.altKey && !e.metaKey && (e.key !== ' ' || taBuf)) { if (typeAhead(e.key)) e.preventDefault(); }
  });
