// Le cadre de la fenêtre (charte), les volets, les colonnes et le démarrage (lieux, page de démonstration hors du programme).
  // ---------------- cadre de la fenêtre ----------------
  KdxFrame.init();
  KdxSplit.init({ pane: $('side'), side: 'left', key: 'explorateur-dossiers', min: 140, max: 520 });    // la barre entre les dossiers et le contenu
  KdxSplit.init({ pane: $('pv'), side: 'right', key: 'explorateur-apercu', min: 200, max: 800 });       // la barre entre le contenu et l'aperçu
  KdxColumns.init({ head: $('lhead'), cols: ['1fr', 170, 170, 90], key: 'explorateur' });   // colonnes redimensionnables (double-clic sur une poignée : largeurs d'origine)   // barre de titre, boutons et bords : kdx-frame.js (la barre se double-clique pour agrandir)

  // ---------------- démarrage ----------------
  if (hasHost) { post({ cmd: 'fs.settings.get' }); post({ cmd: 'fs.places' }); post({ cmd: 'fs.menudata' }); }
  else { // hors du shell : données d'exemple pour voir la mise en page
    handlers.places({ places: [{ name: 'Accueil', path: 'C:\\Users\\Valentin', ic: 'utilisateur' }, { name: 'Bureau', path: 'C:\\Users\\Valentin\\Desktop', ic: 'pc' }, { name: 'Documents', path: 'C:\\Users\\Valentin\\Documents', ic: 'dossier' }, { name: 'Images', path: 'C:\\Users\\Valentin\\Pictures', ic: 'image' }, { name: 'Musique', path: 'x', ic: 'musique' }, { name: 'Vidéos', path: 'y', ic: 'video' }, { name: 'Téléchargements', path: 'z', ic: 'telecharger' }], drives: [{ name: 'Windows (C:)', path: 'C:\\', ic: 'disque' }, { name: 'Données (D:)', path: 'D:\\', ic: 'disque' }], home: 'C:\\Users\\Valentin\\Documents' });
    const now = Date.now(); const d = (n, dd = true, s = -1, ago = 0) => ({ n, d: dd, s, m: now - ago * 3600e3 });
    handlers['fs.list']({ tab: '1', path: 'C:\\Users\\Valentin\\Documents', free: 412e9, items: [d('KIRC', true, -1, 4), d('Charte graphique', true, -1, 20), d('Factures 2026', true, -1, 600), d('capture-kdx.png', false, 2.3e6, 30), d('CV-2026.pdf', false, 214e3, 400), d('notes-kdx.txt', false, 4e3, 70), d('demo-kirc.mp4', false, 48e6, 120), d('ambiance.mp3', false, 6.1e6, 900), d('budget.xlsx', false, 38e3, 300)] });
    T().sel = new Set(['capture-kdx.png']); show();
  }
