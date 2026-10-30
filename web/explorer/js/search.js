// La recherche dans les sous-dossiers (Ctrl+E ou le bouton « Partout ») : une barre de critères (type, date, taille) sous la barre d'outils, les résultats dans la liste (le dossier de chaque
// résultat est écrit en grisé après son nom), le programme parcourt en arrière-plan et envoie les résultats par paquets (fs.found). Fermer la recherche (✕, Échap) revient au dossier.
  // ---------------- recherche ----------------
  const S_KINDS = [['all', 'Tous les types'], ['folder', 'Dossiers'], ['document', 'Documents'], ['image', 'Images'], ['audio', 'Audio'], ['video', 'Vidéos'], ['archive', 'Archives'], ['code', 'Code'], ['app', 'Programmes']];
  const S_DATES = [['any', 'Modifié : à tout moment'], ['today', "Aujourd'hui"], ['week', '7 derniers jours'], ['month', '30 derniers jours'], ['year', '12 derniers mois']];
  const S_SIZES = [['any', 'Toutes les tailles'], ['small', 'Moins de 1 Mo'], ['medium', '1 à 100 Mo'], ['large', 'Plus de 100 Mo'], ['huge', 'Plus de 1 Go']];
  const fillSel = (id, list) => { $(id).innerHTML = list.map(([v, l]) => `<option value="${v}">${esc(l)}</option>`).join(''); };
  fillSel('sKind', S_KINDS); fillSel('sDate', S_DATES); fillSel('sSize', S_SIZES);
  let searchSeq = 0, searchTimer = 0, lastFoundShow = 0;

  const searchIdle = s => !s.q.trim() && s.kind === 'all' && s.date === 'any' && s.size === 'any';

  // Montre ou cache la barre de critères selon l'onglet courant (appelée à chaque dessin de la liste).
  function syncSearchUi() {
    const t = T(), s = t && t.search;
    $('sbar').hidden = !s; $('bSearch').classList.toggle('on', !!s);
    $('find').placeholder = s ? 'Nom (ex. facture *.pdf)…' : 'Rechercher ici…';
    if (!s) return;
    if (document.activeElement !== $('find')) $('find').value = s.q;
    $('sKind').value = s.kind; $('sDate').value = s.date; $('sSize').value = s.size;
    $('sInfo').textContent = 'Dans « ' + (s.root ? baseOf(s.root) : 'tous les disques') + ' » et ses sous-dossiers';
  }

  function startSearch() {
    const t = T(); if (!t || t.path == null) return;
    if (t.search) { $('find').focus(); $('find').select(); return; }
    t.search = { root: t.path, q: '', kind: 'all', date: 'any', size: 'any', sid: 0, running: false, scanned: 0, truncated: false };
    t.filter = ''; t.items = []; t.ver++; t.sel = new Set(); t.cursor = -1; t.loading = false;
    post({ cmd: 'fs.watch', tab: t.id, path: '' }); t.watched = null;   // pas de surveillance du dossier pendant la recherche : la liste montre des résultats
    show(); $('find').value = ''; $('find').focus();
  }

  function runSearch() {
    const t = T(); if (!t || !t.search) return; const s = t.search;
    clearTimeout(searchTimer);
    s.sid = ++searchSeq; s.running = false; s.scanned = 0; s.truncated = false;
    t.items = []; t.ver++; t.sel = new Set(); t.cursor = -1;
    if (searchIdle(s)) { t.loading = false; post({ cmd: 'fs.search.stop', tab: t.id }); show(); return; }
    s.running = true; t.loading = true;
    post({ cmd: 'fs.search', tab: t.id, sid: s.sid, root: s.root, q: s.q, kind: s.kind, date: s.date, size: s.size });
    show();
  }
  const runSoon = () => { clearTimeout(searchTimer); searchTimer = setTimeout(runSearch, 300); };

  // Ferme la recherche : le programme l'arrête, et (`back`) la liste du dossier revient.
  function endSearch(back) {
    const t = T(); if (!t || !t.search) return;
    clearTimeout(searchTimer); post({ cmd: 'fs.search.stop', tab: t.id });
    t.search = null; t.loading = false; t.filter = ''; $('find').value = '';
    $('sbar').hidden = true; $('bSearch').classList.remove('on'); $('find').placeholder = 'Rechercher ici…';
    if (back) go(t.path, 'reload');
  }

  handlers['fs.found'] = m => {
    const t = tabs.find(x => x.id === m.tab); if (!t || !t.search || t.search.sid !== m.sid) return;
    const s = t.search;
    if (m.error) { s.running = false; t.loading = false; if (t === T()) { $('orb').className = 'orb red'; $('st1').textContent = m.error; } return; }
    for (const it of m.items) t.items.push(it);
    s.scanned = m.scanned; s.truncated = !!m.truncated; s.running = !m.done; t.loading = !m.done; t.ver++;
    if (t === T()) { const now = performance.now(); if (m.done || now - lastFoundShow > 300) { show(true); lastFoundShow = now; } }
  };

  // Un résultat : « Ouvrir l'emplacement du dossier » ferme la recherche et montre le fichier dans son dossier.
  function showInFolder(it) { const p = pathOf(it), dir = parentOf(p); if (dir == null) return; endSearch(false); const t = T(); t.pendingSel = it.b ?? it.n; go(dir, 'push'); }

  $('bSearch').onclick = () => { if (T().search) endSearch(true); else startSearch(); };
  $('sClose').onclick = () => endSearch(true);
  $('sKind').onchange = () => { T().search.kind = $('sKind').value; runSearch(); };
  $('sDate').onchange = () => { T().search.date = $('sDate').value; runSearch(); };
  $('sSize').onchange = () => { T().search.size = $('sSize').value; runSearch(); };
