// Les vues (lot 6) : détails, liste, icônes (petites, moyennes, grandes, avec vignettes d'images), regroupement (type, date, taille) et choix des colonnes.
// La mise en page est calculée ici (position de chaque élément et des en-têtes de groupe) ; list.js ne dessine que ce qui est visible. Vue et groupe sont gardés par
// dossier (réglage « views »), la dernière vue choisie devient celle des dossiers jamais réglés (« view.default ») ; les colonnes masquées sont « cols.hidden ».
  // ---------------- vues ----------------
  const VIEWS = {
    details: { name: 'Détails', rowH: 26 }, list: { name: 'Liste', rowH: 22 },
    'icons-s': { name: 'Petites icônes', w: 104, h: 76, icon: 32 }, 'icons-m': { name: 'Icônes moyennes', w: 128, h: 118, icon: 64 }, 'icons-l': { name: 'Grandes icônes', w: 200, h: 190, icon: 128 },
  };
  const VIEW_ORDER = ['icons-l', 'icons-m', 'icons-s', 'list', 'details'];
  const GROUPS = [['', 'Aucun'], ['type', 'Type'], ['date', 'Date de modification'], ['size', 'Taille']];
  const COLS_ALL = [['m', 'Modifié le', 170], ['t', 'Type', 170], ['s', 'Taille', 90]];
  const HDR_H = 24, OVER_PX = 300;
  let viewsMap = {}, lastColsKey = null, colApi = null;

  const viewOf = t => (t && VIEWS[t.view]) ? t.view : 'details';
  const isIcons = t => viewOf(t).startsWith('icons');
  const defaultView = () => (VIEWS[settings['view.default']] ? settings['view.default'] : 'details');
  const folderKey = t => (t.path || '').toLowerCase();
  const hiddenCols = () => new Set(Array.isArray(settings['cols.hidden']) ? settings['cols.hidden'] : []);
  const colsKey = () => [...hiddenCols()].sort().join(',');
  const visibleCols = () => COLS_ALL.filter(c => !hiddenCols().has(c[0]));

  // Quand un onglet arrive dans un dossier : sa vue et son groupe sont ceux gardés pour ce dossier, sinon la vue par défaut.
  function applySavedView(t) {
    const s = viewsMap[folderKey(t)] || {};
    t.view = VIEWS[s.view] ? s.view : defaultView(); t.group = GROUPS.some(g => g[0] === s.group) ? s.group : '';
  }
  function rememberView(t) {
    const k = folderKey(t); if (!k) return;
    const m = Object.assign({}, viewsMap); m[k] = { view: viewOf(t), group: t.group || '', at: Date.now() };
    const keys = Object.keys(m); if (keys.length > 200) for (const old of keys.sort((a, b) => (m[a].at || 0) - (m[b].at || 0)).slice(0, keys.length - 200)) delete m[old];
    viewsMap = m; setSetting('views', m);
  }
  function setView(v, t = T()) { if (!t || !VIEWS[v]) return; t.view = v; rememberView(t); if (defaultView() !== v) setSetting('view.default', v); show(true); }
  function setGroup(g, t = T()) { if (!t) return; t.group = GROUPS.some(x => x[0] === g) ? g : ''; t.vkey = null; rememberView(t); show(true); }
  function toggleCol(k) { const h = hiddenCols(); h.has(k) ? h.delete(k) : h.add(k); setSetting('cols.hidden', [...h]); }
  function refreshViewSettings() {
    viewsMap = settings.views && typeof settings.views === 'object' ? settings.views : {};
    if (colsKey() !== lastColsKey) initColumns();
  }

  // L'en-tête des colonnes : « Nom » puis les colonnes non masquées, redimensionnables (cdx-columns.js, largeurs gardées par jeu de colonnes).
  function initColumns() {
    const cols = visibleCols(); lastColsKey = colsKey();
    const t = T();
    $('lhead').innerHTML = `<button class="lh" data-k="n">Nom</button>` + cols.map(c => `<button class="lh" data-k="${c[0]}">${c[1]}</button>`).join('');
    colApi = CdxColumns.init({ head: $('lhead'), cols: ['1fr', ...cols.map(c => c[2])], key: 'explorateur' + (cols.length === COLS_ALL.length ? '' : '-' + cols.map(c => c[0]).join('')) });
    if (cols.length === 0) $('lhead').parentElement.style.setProperty('--cdx-cols', 'minmax(80px, 1fr)');
    if (t && t.rows) { t.layKey = null; show(true); }
  }

  // ---------------- regroupement ----------------
  // Le groupe d'un élément : un rang (pour l'ordre des groupes) et un libellé (l'en-tête).
  const DAY = 86400e3;
  function groupKeyOf(t, it) {
    if (t.group === 'type') return it.d ? { r: '\u0000', l: 'Dossiers' } : { r: typeOf(it), l: typeOf(it) };
    if (t.group === 'date') {
      if (!it.m) return { r: 6, l: 'Sans date' };
      const now = new Date(), d = new Date(it.m), sod = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
      if (it.m >= sod) return { r: 0, l: 'Aujourd’hui' }; if (it.m >= sod - DAY) return { r: 1, l: 'Hier' };
      if (it.m >= sod - 6 * DAY) return { r: 2, l: 'Cette semaine' };
      if (d.getFullYear() === now.getFullYear() && d.getMonth() === now.getMonth()) return { r: 3, l: 'Ce mois-ci' };
      if (d.getFullYear() === now.getFullYear()) return { r: 4, l: 'Cette année' };
      return { r: 5, l: 'Plus ancien' };
    }
    if (t.group === 'size') {
      if (it.d) return { r: 0, l: 'Dossiers' }; const s = it.s || 0;
      if (s <= 0) return { r: 1, l: 'Vide' }; if (s < 16384) return { r: 2, l: 'Minuscule (moins de 16 Ko)' }; if (s < 1048576) return { r: 3, l: 'Petit (moins de 1 Mo)' };
      if (s < 134217728) return { r: 4, l: 'Moyen (moins de 128 Mo)' }; if (s < 1073741824) return { r: 5, l: 'Grand (moins de 1 Go)' }; return { r: 6, l: 'Énorme (plus de 1 Go)' };
    }
    return { r: 0, l: null };
  }

  // ---------------- mise en page ----------------
  // La position de chaque élément (et des en-têtes de groupe) pour la vue de l'onglet et la largeur de la liste ; gardée tant que rien de cela ne change.
  function ensureLayout(t) {
    const box = $('files'), W = Math.max(100, box.clientWidth || 600);
    const key = (t.vkey || '') + '|' + viewOf(t) + '|' + (t.group || '') + '|' + W + '|' + colsKey();
    if (t.layKey === key && t.lay) return t.lay;
    const rows = t.rows || [], n = rows.length, v = VIEWS[viewOf(t)], ic = !!v.w;
    const cols = ic ? Math.max(1, Math.floor((W - 6) / v.w)) : 1, rh = ic ? v.h : v.rowH, cw = ic ? v.w : W;
    const ys = new Float64Array(n), xs = ic ? new Float64Array(n) : null, hdrs = []; let y = 0, col = 0, last = null;
    for (let i = 0; i < n; i++) {
      const g = t.group ? groupKeyOf(t, rows[i]).l : null;
      if (g !== null && g !== last) { if (col) { y += rh; col = 0; } hdrs.push({ i, y, l: g }); y += HDR_H; last = g; }
      ys[i] = y;
      if (xs) { xs[i] = col * cw; if (++col >= cols) { col = 0; y += rh; } } else y += rh;
    }
    if (col) y += rh;
    t.lay = { ys, xs, hdrs, cols, rh, cw, h: y, ic, icon: v.icon || 16 }; t.layKey = key; return t.lay;
  }
  // Premier indice dont la position est >= y (les positions croissent avec l'indice).
  function firstFrom(ys, y) { let lo = 0, hi = ys.length; while (lo < hi) { const m = (lo + hi) >> 1; if (ys[m] < y) lo = m + 1; else hi = m; } return lo; }
  // Les indices des éléments qui touchent un rectangle (coordonnées du contenu de la liste) : le rectangle de sélection.
  function rowsInRect(t, x0, x1, y0, y1) {
    const lay = ensureLayout(t), out = []; const n = t.rows.length;
    for (let i = Math.max(0, firstFrom(lay.ys, y0 - lay.rh)); i < n && lay.ys[i] <= y1; i++) {
      const x = lay.xs ? lay.xs[i] : 0, w = lay.ic ? lay.cw : 1e9;
      if (lay.ys[i] + lay.rh > y0 && x < x1 && x + w > x0) out.push(i);
    }
    return out;
  }
  const lineStep = () => { const t = T(); const lay = t && t.rows ? ensureLayout(t) : null; return lay && lay.ic ? lay.cols : 1; };
  const rowsPerPage = () => { const t = T(); if (!t || !t.rows) return 1; const lay = ensureLayout(t); return Math.max(1, Math.floor($('files').clientHeight / lay.rh) - 1) * (lay.ic ? lay.cols : 1); };

  // ---------------- une ligne ou une case ----------------
  const IMG_EXT = new Set('png jpg jpeg gif bmp webp svg ico avif'.split(' '));
  function cellHtml(it, i, t, lay) {
    const cls = `fr ${t.sel.has(it.n) ? 'sel' : ''} ${it.h ? 'hid' : ''} ${clip.cut && clip.paths.includes(pathOf(it)) ? 'cut' : ''}`;
    const name = `<span>${esc(dispName(it))}${it.r !== undefined ? `<i class="rd">${esc(it.r || '.')}</i>` : ''}</span>`;
    if (lay.ic) {
      const url = !it.d && !it.drive && IMG_EXT.has(extOf(it.b ?? it.n)) && typeof fileUrl === 'function' ? fileUrl(pathOf(it)) : null;
      return `<div class="${cls} fc" data-i="${i}" style="left:${lay.xs[i]}px;top:${lay.ys[i]}px;width:${lay.cw}px;height:${lay.rh}px" title="${esc(it.n)}"><div class="ic" style="width:${lay.icon}px;height:${lay.icon}px">${pix(iconOf(it), lay.icon)}${url ? `<img src="${url}" loading="lazy" alt="" onerror="this.remove()">` : ''}</div><span class="nm">${name}</span></div>`;
    }
    if (viewOf(t) === 'list') return `<div class="${cls} li" data-i="${i}" style="top:${lay.ys[i]}px;height:${lay.rh}px"><span class="nm">${pix(iconOf(it), 16)}${name}</span></div>`;
    const hid = hiddenCols();
    return `<div class="${cls}" data-i="${i}" style="top:${lay.ys[i]}px;height:${lay.rh}px"><span class="nm">${pix(iconOf(it), 16)}${name}</span>`
      + (hid.has('m') ? '' : `<span class="f">${dateOf(it.m)}</span>`) + (hid.has('t') ? '' : `<span class="f">${typeOf(it)}</span>`)
      + (hid.has('s') ? '' : `<span class="f r">${it.drive ? sizeOf(it.free) + ' libres' : sizeOf(it.s)}</span>`) + '</div>';
  }
  // Ce qui, dans la page, dépend de la vue : l'en-tête des colonnes n'a de sens qu'en « Détails ».
  function syncViewUi(t) { const v = viewOf(t); $('files').dataset.view = v; $('lhead').hidden = v !== 'details'; }

  // ---------------- menus ----------------
  const viewMenuItems = () => { const t = T(); return VIEW_ORDER.map((v, i) => [VIEWS[v].name, () => setView(v), { check: viewOf(t) === v, key: 'Ctrl+Maj+' + [1, 2, 3, 5, 6][i] }]); };
  const groupMenuItems = () => { const t = T(); return GROUPS.map(([k, name]) => [name, () => setGroup(k), { check: (t.group || '') === k }]); };
  const colMenuItems = () => { const h = hiddenCols(); return COLS_ALL.map(([k, name]) => [name, () => toggleCol(k), { check: !h.has(k) }]); };
