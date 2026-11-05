// Le script de start.html, sorti de la page pour que sa politique de sécurité (CSP) n'autorise aucun script en ligne.
  fillPix();
  const $ = id => document.getElementById(id);
  let apps = [], shown = [];

  // Les tuiles « Épinglées » : la liste vient du programme (epingles.json). Clic droit sur une tuile ou une application : ajouter à la barre / retirer de la barre.
  let pins = [];
  const onBar = t => pins.some(p => p.bar && p.target.toLowerCase() === String(t).toLowerCase());
  function renderTiles() {
    const tiles = pins.filter(p => p.tile);
    $('tiles').innerHTML = tiles.map(p => `<button class="btn tile" draggable="true" data-t="${esc(p.target)}">${pinIcon(p, 32)}<span>${esc(p.name)}</span></button>`).join('');
    fillPinIcons($('tiles'), 32);
  }
  $('tiles').onclick = e => { const b = e.target.closest('[data-t]'); if (b) launch(b.dataset.t); };
  const launch = t => { post({ cmd: 'launch', target: t }); post({ cmd: 'hideStart' }); };
  handlers.pins = m => { pins = m.items; renderTiles(); closeCtx(); };

  // Le petit menu du clic droit : une seule ligne, posée au pointeur, qui se ferme au moindre autre geste.
  let ctx = null;
  function closeCtx() { if (ctx) { ctx.remove(); ctx = null; } }
  function openCtx(x, y, label, fn) {
    closeCtx();
    ctx = document.createElement('div'); ctx.className = 'ctxm';
    const row = document.createElement('div'); row.className = 'row-item'; row.textContent = label; row.onclick = () => { closeCtx(); fn(); };
    ctx.appendChild(row); document.body.appendChild(ctx);
    const r = ctx.getBoundingClientRect();
    ctx.style.left = Math.max(4, Math.min(x, innerWidth - r.width - 4)) + 'px'; ctx.style.top = Math.max(4, Math.min(y, innerHeight - r.height - 4)) + 'px';
  }
  $('tiles').oncontextmenu = e => {
    e.preventDefault(); const b = e.target.closest('[data-t]'); if (!b) return;
    const t = b.dataset.t, p = pins.find(q => q.target === t);
    if (onBar(t)) openCtx(e.clientX, e.clientY, 'Retirer de la barre', () => post({ cmd: 'pin.remove', target: t }));
    else openCtx(e.clientX, e.clientY, 'Ajouter à la barre', () => post({ cmd: 'pin.add', name: p.name, target: t }));
  };
  // Glisser une tuile ou une application du menu Démarrer sur la barre : l'épingle à l'endroit lâché (la barre reçoit le glisser, le programme valide).
  document.addEventListener('dragstart', e => {
    const tile = e.target.closest('[data-t]'), row = e.target.closest('#apps [data-i]');
    let it = null;
    if (tile) { const p = pins.find(q => q.target === tile.dataset.t); if (p) it = { k: 'app', name: p.name, t: p.target }; }
    else if (row) { const a = shown[+row.dataset.i]; if (a) it = { k: 'app', name: a.name, t: a.path }; }
    if (!it) { e.preventDefault(); return; }
    closeCtx(); e.dataTransfer.setData('application/x-cdx-pin', JSON.stringify(it)); e.dataTransfer.effectAllowed = 'copy';
    log('glisser commencé : ' + it.name);
  });
  document.addEventListener('mousedown', e => { if (ctx && !ctx.contains(e.target)) closeCtx(); });
  document.addEventListener('contextmenu', e => { if (!e.target.closest('[data-t], #apps')) e.preventDefault(); });

  // Liste des applications : filtrée à chaque frappe ; l'icône d'une ligne n'est demandée que lorsqu'elle devient visible.
  const io = new IntersectionObserver(es => es.forEach(en => { if (en.isIntersecting) { io.unobserve(en.target); wantIcon(en.target.dataset.p, en.target); } }), { root: $('apps') });
  function render() {
    const q = $('q').value.trim().toLowerCase();
    shown = apps.filter(a => !q || a.name.toLowerCase().includes(q)).slice(0, 300);
    $('appsTitle').textContent = q ? `Résultats (${shown.length})` : 'Toutes les applications';
    $('apps').innerHTML = shown.length ? shown.map((a, i) => `<div class="row-item ${q && i === 0 ? 'sel' : ''}" draggable="true" data-i="${i}"><img data-p="${esc(a.path)}" alt=""><span class="grow">${esc(a.name)}</span></div>`).join('') : '<div class="empty">Aucune application</div>';
    $('apps').querySelectorAll('img').forEach(img => io.observe(img));
  }
  $('apps').onclick = e => { const r = e.target.closest('[data-i]'); if (r) launch(shown[+r.dataset.i].path); };
  $('apps').oncontextmenu = e => {
    e.preventDefault(); const r = e.target.closest('[data-i]'); if (!r) return;
    const a = shown[+r.dataset.i];
    if (onBar(a.path)) openCtx(e.clientX, e.clientY, 'Retirer de la barre', () => post({ cmd: 'pin.remove', target: a.path }));
    else openCtx(e.clientX, e.clientY, 'Épingler à la barre', () => post({ cmd: 'pin.add', name: a.name, target: a.path }));
  };
  $('q').oninput = render;
  $('q').onkeydown = e => { if (e.key === 'Enter' && shown.length) launch(shown[0].path); };
  document.onkeydown = e => { if (e.key === 'Escape') post({ cmd: 'hideStart' }); };
  $('x').onclick = () => post({ cmd: 'hideStart' });
  $('lock').onclick = () => { post({ cmd: 'hideStart' }); post({ cmd: 'lock' }); };
  // Veille, redémarrage et arrêt demandent une confirmation (une CDX-boîte de dialogue, Oui est le bouton par défaut).
  function confirmPower(action, title, text, icon) {
    const layer = document.createElement('div'); layer.className = 'veil';
    layer.innerHTML = `<div class="win active"><div class="tb"><span class="tb-title">${title}</span></div><div class="cb"><div class="row" style="gap:14px;padding:10px 4px">${pix(icon, 32)}<div style="line-height:1.5">${text}</div></div><div class="btns"><button class="primary" id="py">Oui</button><button class="btn" id="pn">Non</button></div></div></div>`;
    document.body.appendChild(layer);
    const close = () => layer.remove();
    layer.querySelector('#py').onclick = () => { close(); post({ cmd: 'power', action }); };
    layer.querySelector('#pn').onclick = close;
    layer.querySelector('#py').focus();
    layer.onkeydown = e => { if (e.key === 'Escape') close(); };
  }
  $('sleep').onclick = () => confirmPower('sleep', 'Veille', 'Mettre l’ordinateur en veille ?', 'absent');
  $('restart').onclick = () => confirmPower('restart', 'Redémarrer', 'Redémarrer l’ordinateur ? Les applications ouvertes seront fermées.', 'maj');
  $('shutdown').onclick = () => confirmPower('shutdown', 'Éteindre', 'Éteindre l’ordinateur ? Les applications ouvertes seront fermées.', 'quitter');

  handlers.apps = m => { apps = m.items; render(); log('applications reçues : ' + apps.length); };
  handlers.user = m => { $('user').textContent = m.name; };
  handlers.show = () => { $('q').value = ''; render(); $('apps').scrollTop = 0; $('q').focus(); };
  post({ cmd: 'ready', page: 'start' });
