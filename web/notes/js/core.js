// Le socle de la page « Notes » : état, résolution des liens, boîtes de saisie, messages du programme. Chargé en premier ; les fichiers suivants partagent ses noms (scripts classiques).
  fillPix();
  const $ = id => document.getElementById(id);
  const ls = (k, d) => { try { const v = localStorage.getItem('kdx.notes.' + k); return v == null ? d : JSON.parse(v); } catch (e) { return d; } };
  const lsSet = (k, v) => { try { localStorage.setItem('kdx.notes.' + k, JSON.stringify(v)); } catch (e) { /* pas de stockage : tant pis */ } };
  const baseName = p => p.slice(p.lastIndexOf('/') + 1).replace(/\.md$/i, '');
  const folderOf = p => { const i = p.lastIndexOf('/'); return i < 0 ? '' : p.slice(0, i); };

  // ---------- l'index du coffre (envoyé par le programme à chaque changement) ----------
  let hello = null;                         // { name, root, pinnedRoot }
  let notes = [], folders = [], images = [];
  const byPath = new Map();                 // chemin -> note {p, t, m, g, y, s}
  let byName = new Map();                   // nom (minuscules) -> chemins, du moins profond au plus profond
  let imgPaths = new Map(), imgByName = new Map();

  function indexNotes() {
    byPath.clear(); byName = new Map();
    for (const n of notes) byPath.set(n.p.toLowerCase(), n);
    for (const n of [...notes].sort((a, b) => a.p.length - b.p.length || a.p.localeCompare(b.p))) { const k = n.t.toLowerCase(); if (!byName.has(k)) byName.set(k, []); byName.get(k).push(n.p); }
    imgPaths = new Map(); imgByName = new Map();
    for (const p of [...images].sort((a, b) => a.length - b.length)) { imgPaths.set(p.toLowerCase(), p); const k = p.slice(p.lastIndexOf('/') + 1).toLowerCase(); if (!imgByName.has(k)) imgByName.set(k, []); imgByName.get(k).push(p); }
  }
  const noteAt = p => byPath.get(String(p).toLowerCase()) || null;

  // Les mêmes règles que le programme (Vault.Resolve) : « Note » = la note de ce nom la moins profonde ; « Dossier/Note » vise un chemin.
  function resolveLink(target) {
    let t = String(target).replace(/\\/g, '/').trim().replace(/\.md$/i, ''); if (!t) return null;
    if (t.includes('/')) {
      t = t.replace(/^\/+/, '').toLowerCase(); let best = null;
      for (const n of notes) { const k = n.p.slice(0, -3).toLowerCase(); if ((k === t || k.endsWith('/' + t)) && (!best || n.p.length < best.length)) best = n.p; }
      return best;
    }
    const l = byName.get(t.toLowerCase()); return l ? l[0] : null;
  }

  function imageUrl(target) {
    const lo = String(target).replace(/\\/g, '/').replace(/^\/+/, '').toLowerCase(); let rel = imgPaths.get(lo) || null;
    if (!rel && typeof cur === 'string' && cur) { const base = folderOf(cur).toLowerCase(); rel = imgPaths.get((base ? base + '/' : '') + lo) || null; }   // à côté de la note
    if (!rel) { const k = lo.slice(lo.lastIndexOf('/') + 1), l = imgByName.get(k); if (l) rel = l.find(p => p.toLowerCase().endsWith('/' + lo) || p.toLowerCase() === lo) || (lo.includes('/') ? null : l[0]); }
    return rel ? 'https://vault.kdx.notes/' + rel.split('/').map(encodeURIComponent).join('/') : null;
  }
  const mdCtx = { resolve: resolveLink, imageUrl };

  // ---------- boîtes ----------
  const layer = () => $('layer');
  // Demande un texte (nom de note, de dossier). Rend le texte, ou null si on annule.
  function ask({ title, label = '', value = '', ok = 'OK', hint = '' }) {
    return new Promise(resolve => {
      const v = document.createElement('div'); v.className = 'veil';
      v.innerHTML = `<div class="win active"><div class="tb"><span class="tb-title">${esc(title)}</span></div><div class="cb"><div class="dlg-text">${esc(label)}</div><input class="text-input dlg-in" type="text" spellcheck="false" autocomplete="off"><div class="faint">${esc(hint)}</div><div class="btns"><button class="primary" data-y>${esc(ok)}</button><button class="btn" data-n>Annuler</button></div></div></div>`;
      const inp = v.querySelector('input'); inp.value = value;
      const done = r => { v.remove(); resolve(r); };
      v.querySelector('[data-y]').onclick = () => done(inp.value.trim() || null);
      v.querySelector('[data-n]').onclick = () => done(null);
      v.addEventListener('keydown', e => { if (e.key === 'Escape') done(null); else if (e.key === 'Enter') done(inp.value.trim() || null); });
      layer().appendChild(v); inp.focus(); inp.select();
    });
  }
  // Un message d'information avec un texte à copier (la commande du serveur MCP).
  function info({ title, text, code = '', copy = '' }) {
    const v = document.createElement('div'); v.className = 'veil';
    v.innerHTML = `<div class="win active"><div class="tb"><span class="tb-title">${esc(title)}</span></div><div class="cb"><div class="dlg-text">${esc(text)}</div>${code ? `<code class="dlg-code">${esc(code)}</code>` : ''}<div class="btns">${copy ? '<button class="btn" data-c>Copier la commande</button>' : ''}<button class="primary" data-y>Fermer</button></div></div></div>`;
    const done = () => v.remove();
    v.querySelector('[data-y]').onclick = done;
    const c = v.querySelector('[data-c]'); if (c) c.onclick = () => { try { navigator.clipboard.writeText(copy); c.textContent = 'Copiée'; } catch (e) { c.textContent = 'Copie impossible'; } };
    v.addEventListener('keydown', e => { if (e.key === 'Escape') done(); });
    layer().appendChild(v); v.querySelector('[data-y]').focus();
  }

  // ---------- état d'enregistrement, barre d'état ----------
  function setStatus(text, cls) { $('st1').textContent = text || ''; if (cls !== undefined) $('orb').className = 'orb ' + (cls || ''); }
  const say = (text, ms = 3500) => { $('st2').textContent = text; clearTimeout(say.t); if (ms) say.t = setTimeout(() => { $('st2').textContent = ''; }, ms); };

  // ---------- messages du programme ----------
  handlers.hello = m => {
    hello = m; $('ttl').textContent = 'Notes — ' + m.name;
    if (m.reset) { for (const p of [...docs.keys()]) dropDoc(p); tabs = []; cur = null; renderTabs(); showDoc(); }
    if (!m.reset) restoreTabs(!!m.open);
    if (m.open) openNote(m.open);
  };
  handlers.index = m => { notes = m.notes; folders = m.folders; images = m.images || []; indexNotes(); renderTree(); if (typeof refreshPreview === 'function') refreshPreview(); renderTabs(); };
  handlers.theme = m => { document.documentElement.dataset.theme = m.value; };
  handlers['n.error'] = m => {
    if (m.op === 'n.save' && m.code === 'conflit') return conflict(m.path);
    if (m.op === 'n.read') { const d = pending.get(m.path); pending.delete(m.path); if (tabs.includes(m.path) && !docs.has(m.path)) { tabs = tabs.filter(p => p !== m.path); lsSet('tabs', tabs); renderTabs(); } if (d && !d.quiet) say('Note introuvable : ' + m.path); else if (d) return; }
    setStatus(m.message, 'red'); say(m.message, 6000);
    if (m.op === 'n.save' && docs.has(m.path)) { docs.get(m.path).saving = false; }
  };
  handlers['n.mcpinfo'] = m => info({
    title: 'Claude et tes notes',
    text: 'Le serveur MCP « kdx-notes » permet à Claude de lire, chercher, créer et ranger les notes de ce coffre (il ne supprime jamais : ce qu\'il retire va dans le dossier .trash).\n\nPour le déclarer à Claude Code (une seule fois) :',
    code: `claude mcp add --scope user kdx-notes -- "${m.exe}"`, copy: `claude mcp add --scope user kdx-notes -- "${m.exe}"`
  });

