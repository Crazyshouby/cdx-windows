// Démarrage : réglages de la fenêtre (bouton hachuré), raccourcis, boutons, ouverture.
  // Les réglages de la fenêtre, derrière le bouton hachuré de la barre de titre (host.js : windowSettings) ; la suite y ajoute la couleur d'accent.
  windowSettings(() => [
    ['Affichage', null, { sub: () => [
      ['Édition seule', () => setMode('edit'), { check: mode === 'edit' }],
      ['Édition et aperçu', () => setMode('split'), { check: mode === 'split' }],
      ['Aperçu seul', () => setMode('view'), { check: mode === 'view' }],
      null,
      ['Retour à la ligne', () => setWrap(!wrap), { check: wrap }],
      null,
      ['Volet d’information (plan, liens, étiquettes)', () => setInfo(!infoOpen), { check: infoOpen }],
    ] }],
    ['Projets et tâches', () => openProjects(), { check: view === 'projects' }],
    ['Graphe des liens', () => openGraph(), { check: view === 'graph' }],
    ['Coffre', null, { sub: () => [
      ['Ouvrir le dossier dans l\u2019Explorateur', () => post({ cmd: 'n.reveal' })],
      ['Changer de dossier de notes\u2026', () => post({ cmd: 'n.choosevault' }), { disabled: !!(hello && hello.pinnedRoot) }],
      ['Relire le dossier', () => post({ cmd: 'n.refresh' })],
    ] }],
    ['Claude et tes notes\u2026', () => post({ cmd: 'n.mcpinfo' })],
  ]);

  $('bNew').onclick = () => newNote(currentFolder());
  $('bFolder').onclick = () => newFolder(currentFolder());
  // Le dossier « courant » : celui de la note ouverte (les nouvelles notes se rangent à côté).
  const currentFolder = () => (cur ? folderOf(cur) : '');
  $('modes').onclick = e => { const b = e.target.closest('[data-m]'); if (b) setMode(b.dataset.m); };

  document.addEventListener('keydown', e => {
    const ctrl = e.ctrlKey || e.metaKey; if (!ctrl || e.altKey) return;
    const k = e.key.toLowerCase();
    if (k === 's') { e.preventDefault(); const d = docs.get(cur); if (d) save(d); }
    else if (k === 'n' && !e.shiftKey) { e.preventDefault(); newNote(currentFolder()); }
    else if (k === 'w') { e.preventDefault(); if (cur) closeDoc(cur); }
    else if (k === 'tab') { e.preventDefault(); cycleTab(e.shiftKey ? -1 : 1); }
    else if (k === 'e' && !e.shiftKey) { e.preventDefault(); setMode(mode === 'split' ? 'edit' : mode === 'edit' ? 'view' : 'split'); }
    else if (k === 'f' && e.shiftKey) { e.preventDefault(); $('filter').focus(); $('filter').select(); }
  });
  // Avant de fermer la fenêtre : ce qui n'est pas encore enregistré part tout de suite.
  addEventListener('beforeunload', () => { for (const d of docs.values()) if (d.dirty && !d.saving) save(d); });
  // Le programme demande d'ouvrir une note (bus : open-note).
  handlers['n.open'] = m => openNote(m.path);

  CdxFrame.init();
  CdxSplit.init({ pane: $('side'), side: 'left', key: 'notes-arbre', min: 180, max: 520 });
  showDoc(); renderTabs(); renderTree();

  // Hors du programme (page ouverte dans un navigateur) : un coffre d'exemple pour voir la mise en page.
  if (!hasHost) {
    const t = n => Date.now() - n * 3600e3;
    handlers.hello({ name: 'Notes CDX', root: 'C:\\Users\\Moi\\Documents\\Notes CDX', pinnedRoot: false });
    handlers.index({ notes: [
      { p: 'Accueil.md', t: 'Accueil', m: t(2), g: [], y: null, s: null }, { p: 'Projets/Exemple de projet.md', t: 'Exemple de projet', m: t(5), g: ['exemple'], y: 'projet', s: 'actif' },
      { p: 'Projets/CDX Notes.md', t: 'CDX Notes', m: t(1), g: ['cdx', 'projet'], y: 'projet', s: 'actif' }, { p: 'Idées/Piste.md', t: 'Piste', m: t(30), g: ['idée'], y: null, s: null }], folders: ['Idées', 'Projets', 'Archives'], images: [] });
    const demo = '---\ntype: projet\nstatut: actif\ntags: [exemple]\n---\n# Exemple de projet\n\nUne note avec un [[Accueil|lien]], un lien [[Inconnue]] et #une-etiquette.\n\n## Prochaines actions\n\n- [ ] Lire [[Piste]]\n- [x] Ouvrir l\u2019application\n\n| Mot | Sens |\n|---|---|\n| **gras** | `code` |\n\n> [!note] Encart\n> Du texte.\n';
    handlers['n.note']({ path: 'Projets/Exemple de projet.md', text: demo, hash: 'demo', backlinks: [] });
  } else post({ cmd: 'ready' });
