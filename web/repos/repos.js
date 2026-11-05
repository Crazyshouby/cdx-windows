// Le script de repos.html, sorti de la page pour que sa politique de sécurité (CSP) n'autorise aucun script en ligne.
  fillPix();
  const $ = id => document.getElementById(id);
  const repos = new Map();                 // chemin -> dépôt (ce que le programme a lu)
  let roots = [], hidden = [], gitVersion = undefined, scanning = false, scanId = 0, expected = 0;
  let filter = 'all', query = '', sortKey = 'd', sortRev = false, selected = null;
  const details = new Map();               // chemin -> { commits, files }

  // ---------- ce que git dit, en clair ----------
  // Un dépôt peut avoir plusieurs « états » à la fois. cls : red = illisible, amber = quelque chose n'est pas à l'abri, grey = pour information, green = tout est à jour.
  function flags(r) {
    if (r.error) return [{ k: 'err', cls: 'red', t: 'Illisible', why: 'git n\'arrive pas à lire ce dossier : ' + r.error + '.' }];
    const f = [], dirty = r.changed + r.untracked;
    if (dirty) {
      const parts = [];
      if (r.changed) parts.push(r.changed + ' fichier' + (r.changed > 1 ? 's modifiés' : ' modifié'));
      if (r.untracked) parts.push(r.untracked + ' fichier' + (r.untracked > 1 ? 's nouveaux' : ' nouveau'));
      f.push({ k: 'dirty', cls: 'amber', t: dirty + ' à enregistrer', why: parts.join(' et ') + ' depuis le dernier enregistrement (« commit »). Tant que ce n\'est pas enregistré, git ne les protège pas : ils ne sont pas dans l\'historique.' });
    }
    if (r.ahead) f.push({ k: 'ahead', cls: 'amber', t: r.ahead + ' à envoyer', why: r.ahead + ' enregistrement' + (r.ahead > 1 ? 's existent' : ' existe') + ' sur ce PC mais pas encore sur GitHub (à « pousser », « push »).' });
    if (r.behind) f.push({ k: 'behind', cls: 'grey', t: r.behind + ' à récupérer', why: 'GitHub a ' + r.behind + ' enregistrement' + (r.behind > 1 ? 's' : '') + ' que ce PC n\'a pas (d\'après la dernière synchronisation connue). Se règle avec « récupérer » (« pull »).' });
    if (!r.hasRemote) f.push({ k: 'local', cls: 'amber', t: 'Seulement sur ce PC', why: 'Ce projet n\'a aucune copie en ligne : si le disque tombe en panne, son historique est perdu.' });
    else if (!r.upstream && !r.noCommits && !r.detached) f.push({ k: 'unpub', cls: 'amber', t: 'Branche pas sur GitHub', why: 'La branche « ' + (r.branch || '?') + ' » n\'existe pas encore sur GitHub : ses enregistrements n\'y sont pas.' });
    if (r.noCommits) f.push({ k: 'empty', cls: 'grey', t: 'Vide', why: 'Aucun enregistrement : le projet n\'a pas encore d\'historique.' });
    if (r.detached) f.push({ k: 'det', cls: 'grey', t: 'HEAD détaché', why: 'Le dossier est posé sur un ancien enregistrement, pas sur une branche : ce qu\'on y ajoute risque de se perdre.' });
    if (!f.length) f.push({ k: 'ok', cls: 'green', t: 'À jour', why: 'Rien à enregistrer, rien à envoyer : ce PC et GitHub sont identiques (d\'après la dernière synchronisation connue).' });
    return f;
  }
  const risk = r => flags(r).reduce((n, x) => n + (x.cls === 'red' ? 3 : x.cls === 'amber' ? 1 : 0), 0);

  const FILTERS = [
    ['all', 'Tous', () => true],
    ['dirty', 'À enregistrer', r => !r.error && r.changed + r.untracked > 0],
    ['ahead', 'À envoyer', r => !r.error && r.ahead > 0],
    ['behind', 'À récupérer', r => !r.error && r.behind > 0],
    ['local', 'Seulement sur ce PC', r => !r.error && !r.hasRemote],
    ['err', 'Illisibles', r => !!r.error],
    ['hid', 'Masqués', r => isHidden(r)],
  ];
  const isHidden = r => hidden.some(h => h.toLowerCase() === r.path.toLowerCase());

  // ---------- dates ----------
  function ago(iso) {
    if (!iso) return '—';
    const s = (Date.now() - new Date(iso).getTime()) / 1000;
    if (s < 60) return 'à l\'instant';
    if (s < 3600) return 'il y a ' + Math.floor(s / 60) + ' min';
    if (s < 86400) return 'il y a ' + Math.floor(s / 3600) + ' h';
    const d = Math.floor(s / 86400);
    if (d === 1) return 'hier';
    if (d < 31) return 'il y a ' + d + ' j';
    if (d < 365) return 'il y a ' + Math.floor(d / 30) + ' mois';
    const y = Math.floor(d / 365); return 'il y a ' + y + ' an' + (y > 1 ? 's' : '');
  }
  const full = iso => iso ? new Date(iso).toLocaleString('fr-FR') : '';

  // ---------- liste ----------
  const visible = () => {
    const q = query.trim().toLowerCase(), f = FILTERS.find(x => x[0] === filter)[2];
    const list = [...repos.values()].filter(r => (filter === 'hid' ? isHidden(r) : !isHidden(r)) && f(r) && (!q || r.name.toLowerCase().includes(q) || r.path.toLowerCase().includes(q) || (r.lastSubject || '').toLowerCase().includes(q)));
    const dir = sortRev ? -1 : 1;
    list.sort((a, b) => {
      if (sortKey === 'n') return dir * a.name.localeCompare(b.name, 'fr');
      if (sortKey === 'b') return dir * (a.branch || '').localeCompare(b.branch || '', 'fr') || a.name.localeCompare(b.name, 'fr');
      if (sortKey === 'e') return dir * (risk(b) - risk(a)) || a.name.localeCompare(b.name, 'fr');
      return dir * ((new Date(b.lastDate || 0)) - (new Date(a.lastDate || 0))) || a.name.localeCompare(b.name, 'fr');
    });
    return list;
  };

  let queued = false;
  const render = () => { if (queued) return; queued = true; requestAnimationFrame(() => { queued = false; draw(); }); };

  function draw() {
    // filtres, avec leur compte
    const shown = [...repos.values()].filter(r => !isHidden(r));
    $('chips').innerHTML = FILTERS.filter(([k]) => (k !== 'err' || shown.some(r => r.error) || filter === 'err') && (k !== 'hid' || hidden.length || filter === 'hid'))
      .map(([k, t, fn]) => `<button class="mb ${filter === k ? 'on' : ''}" data-f="${k}">${esc(t)} <span class="n">${(k === 'hid' ? [...repos.values()] : shown).filter(fn).length}</span></button>`).join('');
    document.querySelectorAll('#lhead .lh[data-k]').forEach(b => { b.classList.toggle('on', b.dataset.k === sortKey); b.classList.toggle('rev', b.dataset.k === sortKey && sortRev); });

    const rows = $('rows'), list = visible();
    if (gitVersion === null) rows.innerHTML = '<div class="empty">git n\'est pas installé (ou introuvable) : sans lui, cette application ne peut rien lire.<br>Installe « Git for Windows », puis rouvre Dépôts.</div>';
    else if (!roots.length) rows.innerHTML = '<div class="empty">Aucun dossier à explorer.<br>Clique sur « Dossiers… » pour en choisir un.</div>';
    else if (!list.length) rows.innerHTML = '<div class="empty">' + (scanning && !repos.size ? 'Recherche des projets…' : repos.size ? 'Aucun projet ne correspond.' : 'Aucun projet git dans ces dossiers.') + '</div>';
    else rows.innerHTML = list.map(r => {
      const sub = r.error ? r.path : (r.lastSubject ? esc(r.lastSubject) : r.path);
      return `<div class="fr ${selected === r.path ? 'sel' : ''}" data-p="${esc(r.path)}" title="${esc(r.path)}">
        <div class="nm"><b>${esc(r.name)}</b><i>${esc(sub)}</i></div>
        <div class="stt">${flags(r).map(x => `<span class="st"><span class="orb ${x.cls === 'grey' ? '' : x.cls}"></span>${esc(x.t)}</span>`).join('')}</div>
        <div class="br">${esc(r.detached ? '(détaché)' : r.branch || '—')}</div>
        <div class="dt" title="${esc(full(r.lastDate))}">${ago(r.lastDate)}</div></div>`;
    }).join('');

    // résumé
    const all = [...repos.values()].filter(r => !isHidden(r)), n = k => all.filter(FILTERS.find(x => x[0] === k)[2]).length;
    const orb = $('orb');
    if (scanning) { orb.className = 'orb amber pulse'; $('st1').textContent = expected ? `Lecture des projets… ${repos.size} sur ${expected}` : 'Recherche des projets…'; }
    else {
      const todo = n('dirty') + n('ahead');
      orb.className = 'orb ' + (all.some(r => r.error) ? 'red' : todo || n('local') ? 'amber' : 'green');
      $('st1').textContent = all.length ? `${all.length} projet${all.length > 1 ? 's' : ''} · ${n('dirty')} à enregistrer · ${n('ahead')} à envoyer · ${n('local')} seulement sur ce PC` + (n('err') ? ` · ${n('err')} illisible${n('err') > 1 ? 's' : ''}` : '') : '';
    }
    drawDetail();
  }

  // ---------- détail ----------
  function drawDetail() {
    const box = $('pv'), r = selected && repos.get(selected);
    if (!r) { box.innerHTML = '<div class="empty">Choisis un projet dans la liste pour voir où il en est, en clair.</div>'; return; }
    const d = details.get(r.path);
    let h = `<h2>${esc(r.name)}</h2><div class="path">${esc(r.path)}</div>
      <div class="acts"><button class="btn" data-a="folder"><span class="pix" data-ic="dossier"></span> Ouvrir le dossier</button>${r.web ? `<button class="btn" data-a="web" title="${esc(r.web)}"><span class="pix" data-ic="wifi"></span> Voir sur GitHub</button>` : ''}<button class="btn" data-a="hide" title="${isHidden(r) ? 'Remettre ce projet dans la liste' : 'Ne plus montrer ce projet dans la liste (rien n\'est supprimé)'}">${isHidden(r) ? 'Ne plus masquer' : 'Masquer'}</button></div>`;
    h += `<div class="sec">Où il en est</div>` + flags(r).map(x => `<div class="why"><span class="orb ${x.cls === 'grey' ? '' : x.cls}"></span><span>${esc(x.why)}</span></div>`).join('');
    if (r.branch || r.web) h += `<div class="path">${r.branch ? 'Branche « ' + esc(r.branch) + ' »' : ''}${r.branch && r.web ? ' · ' : ''}${r.web ? 'en ligne : ' + esc(r.webLabel) : ''}</div>`;
    if (!d) h += '<div class="more">Lecture du détail…</div>';
    else {
      if (d.files.length) h += `<div class="sec">Fichiers pas encore enregistrés (${d.files.length}${d.files.length >= 300 ? ' et plus' : ''})</div>` +
        d.files.slice(0, 60).map(f => `<div class="file" title="${esc(f.path)}"><span class="c">${esc(f.code)}</span><span class="p">${esc(f.path)}</span></div>`).join('') +
        (d.files.length > 60 ? `<div class="more">… et ${d.files.length - 60} autres</div>` : '') +
        '<div class="more">M modifié · A ajouté · D supprimé · R renommé · ? nouveau · U en conflit</div>';
      h += `<div class="sec">Derniers enregistrements</div>` + (d.commits.length ? d.commits.map(c => `<div class="cm"><span class="d" title="${esc(full(c.date))}">${ago(c.date)}</span><span class="s" title="${esc(c.subject)}">${esc(c.subject)}</span><span class="a">${esc(c.author)} · ${esc(c.hash)}</span></div>`).join('') : '<div class="more">Aucun.</div>');
    }
    box.innerHTML = h; fillPix(box);
  }

  function select(p) {
    selected = p; render();
    if (p && !details.has(p)) post({ cmd: 'repos.detail', path: p });
  }

  // ---------- messages du programme ----------
  handlers.hello = m => { gitVersion = m.git; roots = m.roots || []; hidden = m.hidden || []; render(); };
  handlers.hidden = m => { hidden = m.hidden || []; if (filter === 'hid' && !hidden.length) filter = 'all';
    const l = visible(); if (selected && !l.some(r => r.path === selected)) { selected = l.length ? l[0].path : null; if (selected && !details.has(selected)) post({ cmd: 'repos.detail', path: selected }); }
    render(); };
  handlers.roots = m => { roots = m.roots || []; if (rootsOpen) drawRoots(); render(); };
  handlers['scan-start'] = m => { scanId = m.id; scanning = true; expected = 0; repos.clear(); details.clear(); render(); };
  handlers['scan-found'] = m => { if (m.id === scanId) { expected = m.count; render(); } };
  handlers.repo = m => { if (m.id !== scanId) return; repos.set(m.item.path, m.item); render(); };
  handlers['scan-end'] = m => {
    if (m.id !== scanId) return;
    scanning = false; if (selected && !repos.has(selected)) selected = null;
    if (!selected) { const l = visible(); if (l.length) selected = l[0].path; }
    if (selected) post({ cmd: 'repos.detail', path: selected });
    render();
  };
  handlers.detail = m => { details.set(m.path, { commits: m.commits || [], files: m.files || [] }); if (m.path === selected) render(); };

  // ---------- fenêtres de dialogue ----------
  let rootsOpen = false;
  function drawRoots() {
    const b = document.getElementById('rootsBody'); if (!b) return;
    b.innerHTML = (roots.length ? roots.map(p => `<div class="rootrow"><span title="${esc(p)}">${esc(p)}</span><button class="btn" data-rm="${esc(p)}">Retirer</button></div>`).join('') : '<div class="empty">Aucun dossier.</div>');
  }
  function rootsDialog() {
    rootsOpen = true;
    const v = document.createElement('div'); v.className = 'veil';
    v.innerHTML = `<div class="win active"><div class="tb"><span class="tb-title">Dossiers explorés</span></div><div class="cb">
      <div class="dlg-text">Dépôts cherche les projets git dans ces dossiers (jusqu'à 4 niveaux plus bas). Retirer un dossier de la liste ne supprime rien.</div>
      <div id="rootsBody"></div>
      <div class="btns"><button class="btn" id="rootAdd">Ajouter un dossier…</button><button class="primary" id="rootClose">Fermer</button></div></div></div>`;
    document.body.appendChild(v);
    const close = () => { rootsOpen = false; v.remove(); };
    v.querySelector('#rootClose').onclick = close;
    v.querySelector('#rootAdd').onclick = () => post({ cmd: 'repos.addroot' });
    v.addEventListener('click', e => { const rm = e.target.closest('[data-rm]'); if (rm) post({ cmd: 'repos.removeroot', path: rm.dataset.rm }); });
    v.addEventListener('keydown', e => { if (e.key === 'Escape') close(); });
    drawRoots(); v.querySelector('#rootClose').focus();
  }

  const HELP = 'Un dépôt (« repo ») est un dossier de projet dont git garde tout l\'historique.\n\n' +
    'Enregistrer (« commit ») : ajouter à l\'historique une photo datée de tes fichiers. Tant que ce n\'est pas fait, les changements ne sont pas protégés.\n\n' +
    'Envoyer (« push ») : copier tes enregistrements sur GitHub, la sauvegarde en ligne.\n\n' +
    'Récupérer (« pull ») : rapatrier ce qui a été envoyé sur GitHub depuis un autre endroit.\n\n' +
    '« Seulement sur ce PC » : le projet n\'a pas de copie en ligne.\n\n' +
    'Cette application ne fait que regarder : elle ne modifie jamais un projet. Les nombres « à envoyer » et « à récupérer » viennent de la dernière synchronisation que git a faite ; rien n\'est vérifié sur Internet.';

  // ---------- gestes ----------
  $('find').addEventListener('input', e => { query = e.target.value; render(); });
  $('bScan').onclick = () => post({ cmd: 'repos.scan' });
  // Les réglages de la fenêtre (bouton hachuré de la barre de titre) : les dossiers où chercher des projets, et l'aide sur les mots de git.
  windowSettings(() => [
    ['Dossiers où chercher des projets…', rootsDialog],
    ['Comprendre git et GitHub…', () => CdxFrame.dialog({ title: 'Comprendre git et GitHub', text: HELP, yes: 'Compris', no: '' })],
  ]);
  $('chips').addEventListener('click', e => { const b = e.target.closest('[data-f]'); if (b) { filter = b.dataset.f; render(); } });
  $('lhead').addEventListener('click', e => { const b = e.target.closest('[data-k]'); if (!b) return; if (sortKey === b.dataset.k) sortRev = !sortRev; else { sortKey = b.dataset.k; sortRev = false; } render(); });
  $('rows').addEventListener('click', e => { const r = e.target.closest('[data-p]'); if (r) select(r.dataset.p); });
  $('rows').addEventListener('dblclick', e => { const r = e.target.closest('[data-p]'); if (r) post({ cmd: 'repos.folder', path: r.dataset.p }); });
  $('rows').addEventListener('keydown', e => {
    const list = visible(); if (!list.length) return;
    const i = list.findIndex(r => r.path === selected);
    if (e.key === 'ArrowDown') { select(list[Math.min(list.length - 1, i + 1)].path); e.preventDefault(); }
    else if (e.key === 'ArrowUp') { select(list[Math.max(0, i - 1)].path); e.preventDefault(); }
    else if (e.key === 'Enter' && selected) post({ cmd: 'repos.folder', path: selected });
  });
  $('pv').addEventListener('click', e => {
    const b = e.target.closest('[data-a]'); if (!b || !selected) return;
    if (b.dataset.a === 'hide') post({ cmd: 'repos.hide', path: selected, hide: !isHidden(repos.get(selected)) });
    else post({ cmd: b.dataset.a === 'web' ? 'repos.web' : 'repos.folder', path: selected });
  });

  CdxFrame.init();
  CdxSplit.init({ pane: $('pv').closest('.col'), side: 'right', key: 'depots-detail', min: 240, max: 800 });   // la barre entre la liste et le détail
  CdxColumns.init({ head: $('lhead'), cols: ['1fr', 250, 110, 150], key: 'depots' });   // colonnes redimensionnables (double-clic sur une poignée : largeurs d'origine)   // barre de titre, boutons et bords : cdx-frame.js (la barre se double-clique pour agrandir)
  draw();

  // Hors du programme (page ouverte dans un navigateur) : des projets d'exemple pour voir la mise en page.
  if (!hasHost) {
    const t = n => new Date(Date.now() - n * 3600e3).toISOString();
    handlers.hello({ git: 'git version 2', roots: ['C:\\Users\\Moi\\Dev'] });
    handlers['scan-start']({ id: 1 });
    [{ path: 'C:\\Dev\\AudioLinker', name: 'AudioLinker', branch: 'main', upstream: 'origin/main', hasRemote: true, web: 'https://github.com/moi/AudioLinker', webLabel: 'github.com/moi/AudioLinker', changed: 0, untracked: 0, ahead: 0, behind: 0, lastDate: t(30), lastSubject: 'Pages découpées', lastAuthor: 'Moi', lastHash: 'abc1234' },
     { path: 'C:\\Dev\\Tennis', name: 'Tennis', branch: 'main', upstream: 'origin/main', hasRemote: true, web: 'https://github.com/moi/Tennis', webLabel: 'github.com/moi/Tennis', changed: 12, untracked: 23, ahead: 2, behind: 0, lastDate: t(5), lastSubject: 'Agents de Valentin', lastAuthor: 'Moi', lastHash: 'def5678' },
     { path: 'C:\\Dev\\cdx-ui', name: 'cdx-ui', branch: 'main', hasRemote: false, changed: 0, untracked: 0, ahead: 0, behind: 0, lastDate: t(200), lastSubject: 'Le lint part dans les applications', lastAuthor: 'Moi', lastHash: '99aa001' },
     { path: 'C:\\Dev\\Vide', name: 'Vide', hasRemote: false, noCommits: true, changed: 0, untracked: 0, ahead: 0, behind: 0 }].forEach(item => handlers.repo({ id: 1, item }));
    handlers['scan-end']({ id: 1, count: 4 });
    handlers.detail({ path: 'C:\\Dev\\Tennis', files: [{ code: 'M', path: 'src/agents.ts' }, { code: '?', path: 'docs/agents.md' }], commits: [{ hash: 'def5678', date: t(5), author: 'Moi', subject: 'Agents de Valentin' }] });
  } else post({ cmd: 'ready' });
