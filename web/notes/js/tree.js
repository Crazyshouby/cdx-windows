// L'arborescence du coffre : dossiers repliables, notes, filtre, menus, glisser-déposer pour ranger.
  const collapsed = new Set(ls('collapsed', []));
  const saveCollapsed = () => lsSet('collapsed', [...collapsed]);
  const fold = s => String(s).toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '');
  const cleanName = s => s.replace(/[\\:*?"<>|\u0000-\u001f]/g, ' ').replace(/\s+/g, ' ').trim().replace(/[. ]+$/, '');

  function buildTree() {
    const root = { name: '', path: '', folders: new Map(), notes: [] };
    const node = path => { let n = root; if (!path) return n; let acc = ''; for (const seg of path.split('/')) { acc = acc ? acc + '/' + seg : seg; if (!n.folders.has(seg)) n.folders.set(seg, { name: seg, path: acc, folders: new Map(), notes: [] }); n = n.folders.get(seg); } return n; };
    for (const f of folders) node(f);
    for (const n of notes) node(folderOf(n.p)).notes.push(n);
    return root;
  }
  const countIn = n => n.notes.length + [...n.folders.values()].reduce((s, f) => s + countIn(f), 0);

  const matches = (n, q) => {
    if (q.startsWith('#')) { const t = fold(q.slice(1)); return t.length > 0 && n.g.some(x => fold(x).includes(t)); }
    const f = fold(q); return fold(n.t).includes(f) || fold(n.p).includes(f);
  };

  function renderTree() {
    const q = $('filter').value.trim(); const el = $('tree'); const out = [];
    const note = (n, depth, withFolder) => `<div class="row-item nt${n.p === cur ? ' sel' : ''}" data-p="${esc(n.p)}" draggable="true" title="${esc(n.p)}" style="padding-left:${8 + depth * 14}px"><span class="tw"></span><span class="grow">${esc(n.t)}</span>${withFolder && folderOf(n.p) ? `<span class="sub">${esc(folderOf(n.p))}</span>` : (n.g.length ? `<span class="tgs">#${esc(n.g[0])}${n.g.length > 1 ? ' +' + (n.g.length - 1) : ''}</span>` : '')}</div>`;
    if (q) {
      const l = notes.filter(n => matches(n, q)).sort((a, b) => (fold(a.t).startsWith(fold(q.replace(/^#/, ''))) ? 0 : 1) - (fold(b.t).startsWith(fold(q.replace(/^#/, ''))) ? 0 : 1) || b.m - a.m).slice(0, 300);
      el.innerHTML = l.length ? l.map(n => note(n, 0, true)).join('') : '<div class="empty">Aucune note ne correspond.</div>';
    } else {
      const root = buildTree();
      out.push(`<div class="row-item root" data-f="" title="Racine du coffre"><span class="tw">⌂</span><span class="grow">${esc(hello ? hello.name : 'Coffre')}</span><span class="n">${notes.length}</span></div>`);
      const walk = (n, depth) => {
        for (const f of [...n.folders.values()].sort((a, b) => a.name.localeCompare(b.name, 'fr', { sensitivity: 'base' }))) {
          const open = !collapsed.has(f.path);
          out.push(`<div class="row-item fld" data-f="${esc(f.path)}" draggable="true" style="padding-left:${8 + depth * 14}px"><span class="tw">${open ? '▾' : '▸'}</span><span class="grow">${esc(f.name)}</span><span class="n">${countIn(f)}</span></div>`);
          if (open) walk(f, depth + 1);
        }
        for (const x of [...n.notes].sort((a, b) => a.t.localeCompare(b.t, 'fr', { sensitivity: 'base' }))) out.push(note(x, depth, false));
      };
      walk(root, 0);
      el.innerHTML = out.join('') + (notes.length === 0 ? '<div class="empty">Le coffre est vide.<br>Crée une note avec « Nouvelle note ».</div>' : '');
    }
  }
  $('filter').oninput = renderTree;
  $('filter').onkeydown = e => { if (e.key === 'Escape') { $('filter').value = ''; renderTree(); } };

  // Montre la note active dans l'arborescence (dossiers dépliés, ligne visible).
  function revealInTree(path) {
    if ($('filter').value) return;
    let ch = false; let f = folderOf(path);
    while (f) { if (collapsed.delete(f)) ch = true; f = folderOf(f); }
    if (ch) { saveCollapsed(); renderTree(); }
    const r = [...$('tree').querySelectorAll('.nt')].find(x => x.dataset.p === path); if (r) r.scrollIntoView({ block: 'nearest' });
  }

  // ---------- actions ----------
  async function newNote(folder) {
    const name = await ask({ title: 'Nouvelle note', label: 'Nom de la note' + (folder ? ` (dans « ${folder} »)` : ''), value: 'Sans titre', ok: 'Créer', hint: 'Un « / » dans le nom crée des sous-dossiers.' });
    if (!name) return;
    const parts = name.split('/').map(cleanName).filter(Boolean); if (!parts.length) return;
    const path = (folder ? folder + '/' : '') + parts.join('/') + '.md';
    post({ cmd: 'n.create', path, text: '# ' + parts[parts.length - 1] + '\n\n' });
  }
  async function newFolder(folder) {
    const name = await ask({ title: 'Nouveau dossier', label: 'Nom du dossier' + (folder ? ` (dans « ${folder} »)` : ''), value: '', ok: 'Créer' });
    if (!name) return;
    const parts = name.split('/').map(cleanName).filter(Boolean); if (!parts.length) return;
    post({ cmd: 'n.folder', path: (folder ? folder + '/' : '') + parts.join('/') });
  }
  async function renameNote(path) {
    const cur0 = baseName(path);
    const name = await ask({ title: 'Renommer la note', label: 'Nouveau nom', value: cur0, ok: 'Renommer', hint: 'Les liens [[…]] qui la visent sont mis à jour dans tout le coffre.' });
    const clean = name && cleanName(name); if (!clean || clean === cur0) return;
    post({ cmd: 'n.move', path, to: (folderOf(path) ? folderOf(path) + '/' : '') + clean + '.md' });
  }
  async function renameFolder(path) {
    const name = await ask({ title: 'Renommer le dossier', label: 'Nouveau nom', value: path.slice(path.lastIndexOf('/') + 1), ok: 'Renommer' });
    const clean = name && cleanName(name); if (!clean || clean === path.slice(path.lastIndexOf('/') + 1)) return;
    post({ cmd: 'n.movefolder', path, to: (folderOf(path) ? folderOf(path) + '/' : '') + clean });
  }
  async function trashNote(path) {
    if (await CdxFrame.dialog({ title: 'Retirer la note', text: `Retirer « ${baseName(path)} » ?\nElle est déplacée dans le dossier .trash du coffre : rien n'est supprimé.`, icon: 'supprimer', yes: 'Retirer', no: 'Annuler' })) post({ cmd: 'n.trash', path });
  }
  async function trashFolder(path) {
    if (await CdxFrame.dialog({ title: 'Retirer le dossier', text: `Retirer le dossier « ${path} » et ses notes ?\nLes notes sont déplacées dans .trash : rien n'est supprimé.`, icon: 'supprimer', yes: 'Retirer', no: 'Annuler' })) post({ cmd: 'n.trashfolder', path });
  }

  // ---------- souris ----------
  $('tree').onclick = e => {
    const f = e.target.closest('.fld'), n = e.target.closest('.nt');
    if (f) { const p = f.dataset.f; if (collapsed.has(p)) collapsed.delete(p); else collapsed.add(p); saveCollapsed(); renderTree(); }
    else if (n) openNote(n.dataset.p);
  };
  $('tree').onauxclick = e => { /* clic molette : rien (les onglets se ferment au clic molette, pas l'arborescence) */ };
  $('tree').oncontextmenu = e => {
    e.preventDefault();
    const f = e.target.closest('.fld, .root'), n = e.target.closest('.nt');
    if (n) {
      const p = n.dataset.p;
      CdxFrame.menu([['Ouvrir', () => openNote(p)], null, ['Renommer…', () => renameNote(p)], ['Retirer…', () => trashNote(p), { icon: 'supprimer' }]], e.clientX, e.clientY, baseName(p));
    } else {
      const p = f ? f.dataset.f : '';
      const items = [['Nouvelle note ici…', () => newNote(p)], ['Nouveau dossier ici…', () => newFolder(p)]];
      if (f && p) items.push(null, ['Renommer le dossier…', () => renameFolder(p)], ['Retirer le dossier…', () => trashFolder(p), { icon: 'supprimer' }]);
      CdxFrame.menu(items, e.clientX, e.clientY, p ? p.slice(p.lastIndexOf('/') + 1) : (hello ? hello.name : 'Coffre'));
    }
  };

  // ---------- glisser-déposer : ranger une note ou un dossier dans un dossier ----------
  const NOTE_KIND = 'application/x-cdx-note';
  let dragging = null;
  const clearOver = () => $('tree').querySelectorAll('.dnd-over').forEach(x => x.classList.remove('dnd-over'));
  $('tree').addEventListener('dragstart', e => {
    const f = e.target.closest('.fld'), n = e.target.closest('.nt');
    if (!f && !n) { e.preventDefault(); return; }
    dragging = n ? { k: 'note', p: n.dataset.p } : { k: 'folder', p: f.dataset.f };
    e.dataTransfer.setData(NOTE_KIND, JSON.stringify(dragging)); e.dataTransfer.effectAllowed = 'move';
    (n || f).classList.add('drag');
  });
  $('tree').addEventListener('dragend', () => { dragging = null; clearOver(); $('tree').querySelectorAll('.drag').forEach(x => x.classList.remove('drag')); });
  // un dossier ne se range pas dans lui-même ni dans ses descendants
  // (ni dans le dossier où il est déjà : rien à faire)
  const canDrop = target => !!dragging && folderOf(dragging.p) !== target && !(dragging.k === 'folder' && (target === dragging.p || target.startsWith(dragging.p + '/')));
  $('tree').addEventListener('dragover', e => {
    if (![...e.dataTransfer.types].includes(NOTE_KIND)) return;
    const f = e.target.closest('.fld, .root'); const target = f ? f.dataset.f : '';
    if (!canDrop(target)) { clearOver(); return; }
    e.preventDefault(); e.dataTransfer.dropEffect = 'move'; clearOver(); if (f) f.classList.add('dnd-over');
  });
  $('tree').addEventListener('dragleave', e => { if (!e.relatedTarget || !$('tree').contains(e.relatedTarget)) clearOver(); });
  $('tree').addEventListener('drop', e => {
    if (![...e.dataTransfer.types].includes(NOTE_KIND)) return;
    e.preventDefault(); const f = e.target.closest('.fld, .root'); const target = f ? f.dataset.f : ''; clearOver();
    let d = dragging; if (!d) { try { d = JSON.parse(e.dataTransfer.getData(NOTE_KIND)); } catch (x) { return; } }
    if (!d || !d.p) return; dragging = d; if (!canDrop(target)) return;
    const name = d.p.slice(d.p.lastIndexOf('/') + 1);
    post({ cmd: d.k === 'note' ? 'n.move' : 'n.movefolder', path: d.p, to: (target ? target + '/' : '') + name });
    if (target && collapsed.delete(target)) saveCollapsed();
  });

  // ---------- réponses du programme ----------
  handlers['n.created'] = m => openNote(m.path);
  handlers['n.folder'] = m => { collapsed.delete(m.path); say('Dossier créé : ' + m.path); };
  handlers['n.moved'] = m => { renamedDoc(m.from, m.to); say(m.updated ? `Déplacée ; ${m.updated} note(s) dont les liens ont été mis à jour.` : 'Déplacée.'); };
  handlers['n.foldermoved'] = m => { for (const mv of m.moves) renamedDoc(mv.from, mv.to); say(m.updated ? `Dossier déplacé ; liens mis à jour dans ${m.updated} note(s).` : 'Dossier déplacé.'); };
  handlers['n.trashed'] = m => { closeDoc(m.path, true); say('Retirée : ' + m.trash); };
  handlers['n.foldertrashed'] = m => { for (const p of m.notes) closeDoc(p, true); say(`Dossier retiré : ${m.notes.length} note(s) dans .trash.`); };
