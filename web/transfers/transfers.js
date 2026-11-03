// Le script de transfers.html, sorti de la page pour que sa politique de sécurité (CSP) n'autorise aucun script en ligne.
  fillPix();
  const $ = id => document.getElementById(id);
  const items = new Map();            // clé -> transfert (ce que le programme a annoncé)
  let filter = 'all';
  const KIND = { copy: ['Copie', 'dossier'], move: ['Déplacement', 'dossier'], delete: ['Suppression', 'supprimer'], download: ['Téléchargement', 'telecharger'], torrent: ['Torrent', 'wifi'] };
  const KIND_FILTERS = { files: ['Fichiers', k => ['copy', 'move', 'delete'].includes(k)], download: ['Téléchargements', k => k === 'download'], torrent: ['Torrents', k => k === 'torrent'] };

  // ---------- mise en forme ----------
  const pad = n => String(n).padStart(2, '0');
  const size = b => { if (b == null || b < 0) return ''; const u = ['o', 'Ko', 'Mo', 'Go', 'To']; let i = 0, v = b; while (v >= 1024 && i < 4) { v /= 1024; i++; } return (i === 0 || v >= 10 ? Math.round(v) : v.toFixed(1).replace('.', ',')) + ' ' + u[i]; };
  const dur = s => { s = Math.max(0, Math.round(s)); if (s < 60) return s + ' s'; if (s < 3600) return Math.floor(s / 60) + ' min ' + pad(s % 60) + ' s'; return Math.floor(s / 3600) + ' h ' + pad(Math.floor(s % 3600 / 60)) + ' min'; };
  const ago = ms => { const s = (Date.now() - ms) / 1000; if (s < 60) return 'à l\'instant'; if (s < 3600) return 'il y a ' + Math.floor(s / 60) + ' min'; if (s < 86400) return 'il y a ' + Math.floor(s / 3600) + ' h'; const d = Math.floor(s / 86400); return d === 1 ? 'hier' : 'il y a ' + d + ' j'; };
  const live = e => e.state === 'running' || e.state === 'paused' || e.state === 'queued';
  const bytes = e => e.kind !== 'delete';   // une suppression compte des éléments, pas des octets
  const pct = e => e.total > 0 ? Math.min(100, Math.floor(e.done * 100 / e.total)) : 0;

  const STATE = { queued: 'En attente', running: null, paused: 'En pause', done: 'Terminé', failed: 'Échec', canceled: 'Annulé' };
  function stateText(e) {
    if (e.state === 'running') return e.total > 0 ? pct(e) + ' %' : 'En cours';
    if (e.state === 'paused') return (e.total > 0 ? pct(e) + ' % · ' : '') + 'En pause';
    if (live(e) || !e.ended) return STATE[e.state];
    return STATE[e.state] + ' · ' + ago(e.ended);
  }
  function line2(e) {
    const route = e.kind === 'delete' ? (e.from || '') : (e.from || e.to ? (e.from || '') + (e.from && e.to ? ' → ' : '') + (e.to || '') : '');
    const n = e.items > 1 && e.kind !== 'delete' ? e.items + ' éléments' : '';
    const cur = e.state === 'running' && e.current && e.current !== e.title ? e.current : '';
    return [route, n, cur].filter(Boolean).join(' · ');
  }
  function line3(e) {
    if (e.state === 'failed' || (e.state === 'canceled' && e.error)) return e.error || (e.state === 'failed' ? 'Le transfert a échoué.' : '');
    if (live(e)) {
      const parts = [];
      if (bytes(e)) parts.push(e.total > 0 ? size(e.done) + ' sur ' + size(e.total) : size(e.done));
      else if (e.total > 0) parts.push(e.done + ' sur ' + e.total + ' éléments');
      if (e.state === 'running' && e.speed > 0 && bytes(e)) parts.push(size(e.speed) + '/s');
      if (e.state === 'running' && e.speed > 0 && e.total > e.done && bytes(e)) parts.push('reste ' + dur((e.total - e.done) / e.speed));
      if (e.state === 'queued') parts.push('démarre après les précédents');
      return parts.join(' · ');
    }
    if (e.state === 'done') {
      const parts = [];
      if (bytes(e) && e.total > 0) parts.push(size(e.total));
      if (e.started && e.ended && e.ended - e.started >= 2000) parts.push('en ' + dur((e.ended - e.started) / 1000));
      return parts.join(' · ');
    }
    return '';
  }
  function actions(e) {
    const b = (a, t, tip) => `<button class="btn" data-a="${a}"${tip ? ' title="' + tip + '"' : ''}>${t}</button>`;
    if (e.state === 'running') return (e.canPause ? b('pause', 'Pause') : '') + b('cancel', 'Annuler');
    if (e.state === 'paused') return b('resume', 'Reprendre') + b('cancel', 'Annuler');
    if (e.state === 'queued') return b('cancel', 'Annuler');
    const o = [];
    if (e.state === 'done' && e.hasPath) o.push(b('open', 'Ouvrir'));
    if (e.hasPath && e.state !== 'canceled') o.push(b('reveal', 'Afficher', 'Afficher dans le dossier'));
    if (e.canRetry) o.push(b('retry', 'Réessayer'));
    o.push(b('remove', 'Retirer', 'Retirer de la liste'));
    return o.join('');
  }

  // ---------- liste ----------
  const order = list => list.sort((a, b) => (live(b) - live(a)) || (live(a) ? a.started - b.started : (b.ended || 0) - (a.ended || 0)));
  const matches = e => filter === 'all' || (filter === 'live' && live(e)) || (filter === 'done' && !live(e)) || (KIND_FILTERS[filter] && KIND_FILTERS[filter][1](e.kind));

  function chips() {
    const all = [...items.values()];
    const f = [['all', 'Tous', all.length], ['live', 'En cours', all.filter(live).length], ['done', 'Terminés', all.filter(e => !live(e)).length]];
    const kinds = Object.entries(KIND_FILTERS).filter(([k, v]) => (all.some(e => v[1](e.kind)) && all.some(e => !v[1](e.kind))) || filter === k);   // un genre n'a son bouton que s'il existe et que la liste n'en est pas faite que de lui
    $('chips').innerHTML = f.map(([k, t, n]) => `<button class="mb ${filter === k ? 'on' : ''}" data-f="${k}">${t} <span class="n">${n}</span></button>`).join('')
      + (kinds.length ? '<span class="sep"></span>' + kinds.map(([k, v]) => `<button class="mb ${filter === k ? 'on' : ''}" data-f="${k}">${v[0]} <span class="n">${all.filter(e => v[1](e.kind)).length}</span></button>`).join('') : '');
  }

  const els = new Map();              // clé -> ligne affichée
  function rowHtml(e) {
    const [kn, ic] = KIND[e.kind] || ['Transfert', 'fichier'];
    return `<div class="l1"><span class="pix" data-ic="${ic}"></span><span class="ttl"></span><span class="kd">${kn}</span><span class="stt"></span></div><div class="l2"></div><div class="bar"><i></i></div><div class="l3row"><div class="l3"></div><div class="acts"></div></div>`;
  }
  function patch(el, e) {
    el.className = 'tx ' + e.state;
    const set = (sel, t) => { const n = el.querySelector(sel); if (n.textContent !== t) n.textContent = t; };
    set('.ttl', e.title || '—'); el.querySelector('.ttl').title = e.title || '';
    set('.stt', stateText(e)); set('.l2', line2(e));
    const l3 = el.querySelector('.l3'); const t3 = line3(e); if (l3.textContent !== t3) l3.textContent = t3;
    l3.classList.toggle('err', e.state === 'failed');
    const bar = el.querySelector('.bar'); bar.classList.toggle('unk', e.state === 'running' && !(e.total > 0));
    el.querySelector('.bar i').style.width = (e.state === 'done' ? 100 : pct(e)) + '%';
    bar.style.display = e.state === 'canceled' ? 'none' : '';
    const sig = e.state + '|' + e.canPause + '|' + e.canRetry + '|' + e.hasPath, acts = el.querySelector('.acts');
    if (acts.dataset.sig !== sig) { acts.dataset.sig = sig; acts.innerHTML = actions(e); }
  }

  let queued = false;
  const render = () => { if (queued) return; queued = true; requestAnimationFrame(() => { queued = false; draw(); }); };

  function draw() {
    chips();
    const rows = $('rows'), list = order([...items.values()].filter(matches));
    const keep = new Set(list.map(e => e.key));
    for (const [k, el] of els) if (!keep.has(k)) { el.remove(); els.delete(k); }
    const empty = rows.querySelector('.empty');
    if (!list.length) {
      if (!empty) rows.innerHTML = '<div class="empty"></div>';
      rows.querySelector('.empty').innerHTML = items.size ? 'Rien dans cette liste.' : 'Aucun transfert.<br>Les copies, les déplacements et les téléchargements apparaissent ici.';
    } else if (empty) empty.remove();
    let prev = null;
    for (const e of list) {
      let el = els.get(e.key);
      if (!el) { el = document.createElement('div'); el.dataset.key = e.key; el.innerHTML = rowHtml(e); fillPix(el); els.set(e.key, el); }
      patch(el, e);
      const want = prev ? prev.nextSibling : rows.firstChild;   // l'ordre ne bouge que si besoin : pas de scintillement
      if (el !== want) rows.insertBefore(el, want);
      prev = el;
    }
    const act = [...items.values()].filter(e => e.state === 'running'), n = [...items.values()].filter(live).length, sp = act.reduce((s, e) => s + (bytes(e) ? e.speed || 0 : 0), 0);
    $('orb').className = 'orb ' + (act.length ? 'amber pulse' : [...items.values()].some(e => e.state === 'failed' && !e.hist) ? 'red' : 'green');
    $('st1').textContent = n ? n + ' en cours' + (sp > 0 ? ' · ' + size(sp) + '/s' : '') : 'Rien en cours';
  }

  // ---------- clics ----------
  $('chips').addEventListener('click', e => { const b = e.target.closest('[data-f]'); if (b) { filter = b.dataset.f; render(); } });
  // Les réglages de la fenêtre (bouton hachuré de la barre de titre) : vider la liste de ce qui est terminé (réglable ici dès qu'il y en aura d'autres : durée de l'historique…).
  const clearDone = () => { for (const [k, e] of items) if (!live(e)) items.delete(k); post({ cmd: 'xf.clear' }); render(); };
  windowSettings(() => [['Effacer les terminés', clearDone, { disabled: ![...items.values()].some(e => !live(e)) }]]);
  $('rows').addEventListener('click', ev => {
    const b = ev.target.closest('[data-a]'), row = ev.target.closest('.tx'); if (!b || !row) return;
    const key = row.dataset.key, a = b.dataset.a;
    if (a === 'open') post({ cmd: 'xf.open', key });
    else if (a === 'reveal') post({ cmd: 'xf.reveal', key });
    else if (a === 'remove') { items.delete(key); post({ cmd: 'xf.remove', key }); render(); }
    else if (a === 'retry') { post({ cmd: 'xf.ctl', key, action: 'retry' }); items.delete(key); render(); }
    else post({ cmd: 'xf.ctl', key, action: a });
  });

  // ---------- messages du programme ----------
  handlers['xf.hello'] = m => { items.clear(); for (const e of m.items || []) items.set(e.key, e); render(); };
  handlers['xf.items'] = m => { for (const e of m.items || []) items.set(e.key, e); render(); };
  handlers['xf.removed'] = m => { for (const k of m.keys || []) items.delete(k); render(); };

  CdxFrame.init();
  draw();
  setInterval(() => { for (const e of items.values()) if (!live(e)) { const el = els.get(e.key); if (el) patch(el, e); } }, 30000);   // « il y a 5 min » se met à jour

  // Hors du programme (page ouverte dans un navigateur) : quelques transferts d'exemple pour voir la mise en page.
  if (!hasHost) {
    const now = Date.now();
    handlers['xf.hello']({ items: [
      { key: 'a', kind: 'copy', title: '3 éléments', state: 'running', from: 'Downloads', to: 'Archives', current: 'film.mkv', done: 1.4e9, total: 4.2e9, speed: 82e6, items: 3, canPause: true, started: now - 20000 },
      { key: 'b', kind: 'download', title: 'installeur-1.2.exe', state: 'running', from: 'github.com', to: 'Téléchargements', done: 30e6, total: 120e6, speed: 6e6, hasPath: true, canPause: true, started: now - 9000 },
      { key: 'c', kind: 'move', title: 'Photos 2025', state: 'queued', from: 'Bureau', to: 'D:', items: 1, started: now - 5000 },
      { key: 'd', kind: 'copy', title: 'sauvegarde.zip', state: 'failed', from: 'C:', to: 'E:', error: 'sauvegarde.zip : Le disque est plein.', canRetry: true, started: now - 600000, ended: now - 590000 },
      { key: 'e', kind: 'download', title: 'rapport.pdf', state: 'done', from: 'exemple.org', to: 'Téléchargements', done: 2e6, total: 2e6, hasPath: true, started: now - 3600000, ended: now - 3599000 },
    ] });
  } else post({ cmd: 'ready' });
