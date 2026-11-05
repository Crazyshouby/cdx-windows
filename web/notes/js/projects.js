// Vue « Projets » (lot 5) : les fiches de projet du coffre (type: projet) et les tâches ouvertes de toutes les notes, à la place de l'éditeur.
// Une case cochée ici écrit « [x] » dans la note (le programme le fait : n.toggletask) ; « Dossier » ouvre le dépôt dans l'Explorateur, « Dépôts » l'application Dépôts.
  let view = null;                           // null | 'projects' | 'graph'
  let projData = null;                       // { projects, tasks } envoyé par le programme
  let projFilter = ls('projFilter', 'tous'); // tous | actif | en pause | terminé

  function showView(name) {
    view = name;
    $('main').classList.toggle('viewing', !!name);
    $('projects').hidden = name !== 'projects'; if (!graphCol) $('graph').hidden = name !== 'graph';
    document.querySelectorAll('#views .mb').forEach(b => b.classList.toggle('on', b.dataset.v === name || (b.dataset.v === 'graph' && graphOn())));
    if (name === 'projects') { $('st2').textContent = ''; setStatus('Projets et tâches', ''); }
    if (name === 'graph') { $('st2').textContent = ''; setStatus('Graphe des liens', ''); if (typeof graphShown === 'function') graphShown(); }
    if (!name && cur) updateStatus();
  }
  function closeViews() { if (view) showView(null); }
  function openProjects() { if (view === 'projects') { showView(null); return; } showView('projects'); post({ cmd: 'n.projects' }); }
  $('views').onclick = e => { const b = e.target.closest('[data-v]'); if (!b) return; if (b.dataset.v === 'projects') openProjects(); else openGraph(); };
  // l'index a bougé pendant qu'une vue est ouverte : elle se rafraîchit
  function onIndexForViews() { if (view === 'projects') post({ cmd: 'n.projects' }); if (view === 'graph') post({ cmd: 'n.graph' }); }

  const STATUS_CLS = s => s === 'actif' ? 'green' : s === 'en pause' ? 'amber' : s === 'terminé' ? '' : 'amber';
  const today = () => new Date().toISOString().slice(0, 10);

  function renderProjects() {
    const el = $('projects'); if (!projData) { el.innerHTML = '<div class="empty">Lecture du coffre…</div>'; return; }
    const all = projData.projects, list = projFilter === 'tous' ? all : all.filter(p => p.status === projFilter);
    const chips = [['tous', 'Tous'], ['actif', 'Actifs'], ['en pause', 'En pause'], ['terminé', 'Terminés']].map(([k, n]) => `<button class="mb${projFilter === k ? ' on' : ''}" data-pf="${k}">${n} <span class="n">${k === 'tous' ? all.length : all.filter(p => p.status === k).length}</span></button>`).join('');
    const rows = list.map(p => `<div class="fr" data-p="${esc(p.p)}" title="${esc(p.p)}">
      <span class="nm"><b>${esc(p.name)}</b>${p.summary ? `<i>${esc(p.summary)}</i>` : ''}</span>
      <span class="st"><span class="orb ${STATUS_CLS(p.status)}"></span>${esc(p.status || '—')}</span>
      <span>${esc(p.priority || '—')}</span>
      <span title="${p.done} faite(s)">${p.open} ouverte${p.open > 1 ? 's' : ''}</span>
      <span class="${p.due && p.due < today() ? 'late' : ''}">${esc(p.due || '—')}</span>
    </div>`).join('');
    const tasks = projData.tasks.map(t => `<div class="row-item tk1" data-p="${esc(t.p)}" data-line="${t.line}" title="${esc(t.p)} : ligne ${t.line}"><input type="checkbox" data-t="1"><span class="txt">${esc(t.text)}</span><span class="src">${esc(titleOf(t.p))}</span><span class="due${t.due && t.due < today() ? ' late' : ''}">${esc(t.due || '')}</span></div>`).join('');
    el.innerHTML = `<div class="prbar"><div class="mbar" id="prchips">${chips}</div><span class="grow"></span><button class="btn" id="prNew"><span class="pix">${pix('editer', 16)}</span> Nouveau projet…</button><button class="btn" id="prRepos" title="Ouvrir Dépôts : l'état git de tous les projets du PC"><span class="pix">${pix('liste', 16)}</span> Dépôts</button></div>
      <div class="prsplit">
        <div class="col" style="flex:1 1 0"><div class="lhead pr5"><button class="lh">Projet</button><button class="lh">Statut</button><button class="lh">Priorité</button><button class="lh">Tâches</button><button class="lh">Échéance</button></div><div class="list pr" id="prlist">${rows || `<div class="empty">${all.length ? 'Aucun projet avec ce statut.' : 'Aucune fiche de projet.<br>Une note avec « type: projet » en tête en est une ; « Nouveau projet… » en crée une à partir du modèle.'}</div>`}</div></div>
        <div class="col" style="flex:1 1 0"><div class="lhead"><button class="lh">Tâches ouvertes</button><span class="n" style="margin-left:auto;padding-right:8px;color:var(--faint)">${projData.tasks.length}</span></div><div class="list tk" id="tklist">${tasks || '<div class="empty">Aucune tâche ouverte : rien de la forme « - [ ] … ».</div>'}</div></div>
      </div>`;
    $('prchips').onclick = e => { const b = e.target.closest('[data-pf]'); if (b) { projFilter = b.dataset.pf; lsSet('projFilter', projFilter); renderProjects(); } };
    $('prlist').onclick = e => { const r = e.target.closest('[data-p]'); if (r) openNote(r.dataset.p); };
    $('prlist').oncontextmenu = e => {
      const r = e.target.closest('[data-p]'); if (!r) return; e.preventDefault();
      const p = all.find(x => x.p === r.dataset.p); if (!p) return;
      CdxFrame.menu([
        ['Ouvrir la fiche', () => openNote(p.p)],
        ['Ouvrir le dossier du dépôt', () => post({ cmd: 'n.openfolder', path: p.repo }), { disabled: !p.repo }],
        null,
        ['Statut', null, { sub: ['actif', 'en pause', 'terminé'].map(s => [s, () => post({ cmd: 'n.setprop', path: p.p, key: 'statut', value: s }), { check: p.status === s }]) }],
        ['Priorité', null, { sub: ['haute', 'normale', 'basse'].map(s => [s, () => post({ cmd: 'n.setprop', path: p.p, key: 'priorite', value: s }), { check: p.priority === s }]) }],
      ], e.clientX, e.clientY, p.name);
    };
    $('tklist').onclick = e => {
      const r = e.target.closest('.tk1'); if (!r) return;
      if (e.target.closest('input')) { e.preventDefault(); post({ cmd: 'n.toggletask', path: r.dataset.p, line: +r.dataset.line }); }
      else openNote(r.dataset.p, { line: +r.dataset.line });
    };
    $('prNew').onclick = () => createProject();
    $('prRepos').onclick = () => post({ cmd: 'n.repos' });
  }
  handlers['n.projects'] = m => { projData = m; if (view === 'projects') renderProjects(); };

  async function createProject(name) {
    name = name || await ask({ title: 'Nouveau projet', label: 'Nom du projet', value: '', ok: 'Créer', hint: 'La fiche est créée dans « Projets » à partir du modèle (objectif, prochaines actions, notes, journal).' });
    if (!name) return;
    post({ cmd: 'n.newproject', name });
  }
