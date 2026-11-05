// Vue « Graphe » (lot 6) : les notes et leurs liens sur un canevas, disposés par forces (les notes liées se rapprochent, les autres s'écartent).
// Pixels nets, couleurs de la charte : la note active en accent, les autres en vert, les liens en gris ; survol = la note et ses voisines en blanc.
  let gNodes = [], gEdges = [], gById = new Map(), gAdj = new Map();
  let gZoom = 1, gPanX = 0, gPanY = 0, gHover = null, gDrag = null, gRunning = 0, gAlpha = 0, gLast = 0;
  const gc = $('gcanvas');
  window.__graph = { get nodes() { return gNodes; }, get edges() { return gEdges; } };

  // « Graphe dans une colonne » : le graphe vit dans la colonne #gcol à droite de l'éditeur (volet redimensionnable) au lieu de prendre sa place.
  let graphCol = !!ls('graphCol', false);
  const graphOn = () => graphCol ? !$('gcol').hidden : view === 'graph';
  function placeGraph() { const g = $('graph'); if (graphCol) { $('gcol').appendChild(g); g.hidden = false; } else { $('main').appendChild(g); g.hidden = view !== 'graph'; } }
  function openGraph() {
    if (graphCol) { const on = $('gcol').hidden; $('gcol').hidden = !on; $('bGraph').classList.toggle('on', on); if (on) { post({ cmd: 'n.graph' }); graphShown(); } return; }
    if (view === 'graph') { showView(null); return; } showView('graph'); post({ cmd: 'n.graph' });
  }
  function setGraphCol(on) {
    const was = graphOn(); if (view === 'graph') showView(null);
    graphCol = on; lsSet('graphCol', on); $('gcol').hidden = !(on && was); placeGraph();
    if (was) { if (on) { $('bGraph').classList.add('on'); graphShown(); } else openGraph(); }
  }
  function graphShown() { gResize(); gDraw(); }
  const gColors = () => { const cs = getComputedStyle(document.documentElement), v = (n, d) => cs.getPropertyValue(n).trim() || d; return { ink: v('--ink', '#000'), node: v('--field-ink', '#0f0'), cur: v('--accent', '#d41818'), edge: '#444444', text: v('--text', '#fff'), faint: v('--faint', '#888'), proj: v('--a-spark', '#ff8a74') }; };

  handlers['n.graph'] = m => {
    const old = new Map(gNodes.map(n => [n.id, n]));
    gNodes = m.nodes.map((n, i) => { const o = old.get(n.id); const a = i / Math.max(1, m.nodes.length) * Math.PI * 2, r = 40 + Math.sqrt(m.nodes.length) * 18; return Object.assign({ x: o ? o.x : Math.cos(a) * r, y: o ? o.y : Math.sin(a) * r, vx: 0, vy: 0, deg: 0 }, n); });
    gById = new Map(gNodes.map(n => [n.id, n]));
    gEdges = m.edges.filter(e => gById.has(e.from) && gById.has(e.to));
    gAdj = new Map(); for (const n of gNodes) gAdj.set(n.id, new Set());
    for (const e of gEdges) { gAdj.get(e.from).add(e.to); gAdj.get(e.to).add(e.from); gById.get(e.from).deg++; gById.get(e.to).deg++; }
    $('ginfo').textContent = `${gNodes.length} note${gNodes.length > 1 ? 's' : ''} · ${gEdges.length} lien${gEdges.length > 1 ? 's' : ''}`;
    if (graphOn()) { gResize(); gAlpha = 1; gStart(); }
  };

  // ---------- disposition par forces ----------
  function gStep() {
    const k = 60;   // longueur de repos d'un lien
    for (let i = 0; i < gNodes.length; i++) {
      const a = gNodes[i]; let fx = -a.x * 0.01, fy = -a.y * 0.01;   // rappel vers le centre
      for (let j = 0; j < gNodes.length; j++) {
        if (i === j) continue; const b = gNodes[j]; let dx = a.x - b.x, dy = a.y - b.y; let d2 = dx * dx + dy * dy; if (d2 < 1) { dx = Math.random() - .5; dy = Math.random() - .5; d2 = 1; }
        const f = (k * k) / d2 * 0.9; const d = Math.sqrt(d2); fx += dx / d * f; fy += dy / d * f;   // répulsion
      }
      a.fx = fx; a.fy = fy;
    }
    for (const e of gEdges) {
      const a = gById.get(e.from), b = gById.get(e.to); const dx = b.x - a.x, dy = b.y - a.y, d = Math.max(1, Math.sqrt(dx * dx + dy * dy)); const f = (d - k) * 0.04;   // ressort
      a.fx += dx / d * f; a.fy += dy / d * f; b.fx -= dx / d * f; b.fy -= dy / d * f;
    }
    for (const n of gNodes) {
      if (n === gDrag) continue;
      n.vx = (n.vx + n.fx * gAlpha) * 0.6; n.vy = (n.vy + n.fy * gAlpha) * 0.6; n.x += n.vx; n.y += n.vy;
    }
    gAlpha *= 0.985;
  }
  function gStart() { if (gRunning) return; gRunning = requestAnimationFrame(gFrame); }
  function gFrame() { gRunning = 0; if (!graphOn()) return; for (let i = 0; i < 2; i++) gStep(); gDraw(); if (gAlpha > 0.01) gStart(); }
  // Pour les essais (pas d'images animées en headless) : fait avancer la disposition d'un coup.
  function graphSettle(n = 300) { for (let i = 0; i < n; i++) gStep(); gDraw(); }

  // ---------- dessin ----------
  function gResize() { const r = gc.getBoundingClientRect(); const w = Math.max(10, Math.floor(r.width)), h = Math.max(10, Math.floor(r.height)); if (gc.width !== w || gc.height !== h) { gc.width = w; gc.height = h; } }
  const toScreen = n => ({ x: Math.round(gc.width / 2 + (n.x + gPanX) * gZoom), y: Math.round(gc.height / 2 + (n.y + gPanY) * gZoom) });
  const nodeSize = n => Math.min(11, 4 + n.deg);
  function gDraw() {
    if (gc.width === 0) gResize();
    const ctx = gc.getContext('2d'); const c = gColors(); ctx.imageSmoothingEnabled = false;
    ctx.fillStyle = c.ink; ctx.fillRect(0, 0, gc.width, gc.height);
    const hi = gHover ? new Set([gHover.id, ...gAdj.get(gHover.id)]) : null;
    ctx.lineWidth = 1;
    for (const e of gEdges) {
      const a = toScreen(gById.get(e.from)), b = toScreen(gById.get(e.to));
      ctx.strokeStyle = hi && (e.from === gHover.id || e.to === gHover.id) ? c.text : c.edge; ctx.beginPath(); ctx.moveTo(a.x + .5, a.y + .5); ctx.lineTo(b.x + .5, b.y + .5); ctx.stroke();
    }
    ctx.font = '14px "CDX Pixel", sans-serif'; ctx.textBaseline = 'top';
    const labelsAll = gNodes.length <= 80 || gZoom >= 1.5;
    for (const n of gNodes) {
      const p = toScreen(n), s = nodeSize(n);
      const dim = hi && !hi.has(n.id);
      ctx.fillStyle = '#000'; ctx.fillRect(p.x - s / 2 - 1, p.y - s / 2 - 1, s + 2, s + 2);
      ctx.fillStyle = dim ? c.edge : n.id === cur ? c.cur : hi && hi.has(n.id) ? c.text : n.type === 'projet' ? c.proj : c.node;
      ctx.fillRect(p.x - Math.floor(s / 2), p.y - Math.floor(s / 2), s, s);
      if ((labelsAll && !dim) || (hi && hi.has(n.id)) || n.id === cur) { ctx.fillStyle = n.id === cur ? c.cur : dim ? c.faint : hi && hi.has(n.id) ? c.text : c.faint; ctx.fillText(n.title, p.x + s / 2 + 4, p.y - 7); }
    }
  }
  function gFit() {
    if (!gNodes.length) return;
    let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity; for (const n of gNodes) { x0 = Math.min(x0, n.x); y0 = Math.min(y0, n.y); x1 = Math.max(x1, n.x); y1 = Math.max(y1, n.y); }
    const w = Math.max(50, x1 - x0 + 160), h = Math.max(50, y1 - y0 + 80);
    gZoom = Math.max(0.2, Math.min(2.5, Math.min(gc.width / w, gc.height / h))); gPanX = -(x0 + x1) / 2; gPanY = -(y0 + y1) / 2; gDraw();
  }
  $('gFit').onclick = gFit;
  new ResizeObserver(() => { if (graphOn()) { gResize(); gDraw(); } }).observe(gc);

  // ---------- souris ----------
  const gAt = (mx, my) => { let best = null, bd = 100; for (const n of gNodes) { const p = toScreen(n); const d = (p.x - mx) ** 2 + (p.y - my) ** 2; if (d < bd) { bd = d; best = n; } } return best; };
  const gMouse = e => { const r = gc.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; };
  let gPanStart = null;
  gc.addEventListener('mousedown', e => { const m = gMouse(e); const n = gAt(m.x, m.y); if (n) { gDrag = n; gDrag.moved = false; } else gPanStart = { x: m.x, y: m.y, px: gPanX, py: gPanY }; });
  gc.addEventListener('mousemove', e => {
    const m = gMouse(e);
    if (gDrag) { gDrag.x = (m.x - gc.width / 2) / gZoom - gPanX; gDrag.y = (m.y - gc.height / 2) / gZoom - gPanY; gDrag.moved = true; gAlpha = Math.max(gAlpha, 0.3); gStart(); return; }
    if (gPanStart) { gPanX = gPanStart.px + (m.x - gPanStart.x) / gZoom; gPanY = gPanStart.py + (m.y - gPanStart.y) / gZoom; gDraw(); return; }
    const n = gAt(m.x, m.y); if (n !== gHover) { gHover = n; gc.style.cursor = n ? 'pointer' : 'default'; gc.title = n ? n.id : ''; gDraw(); }
  });
  const gUp = () => { if (gDrag && !gDrag.moved) graphOpenNode(gDrag.id); gDrag = null; gPanStart = null; };
  gc.addEventListener('mouseup', gUp); gc.addEventListener('mouseleave', () => { gDrag = null; gPanStart = null; if (gHover) { gHover = null; gDraw(); } });
  gc.addEventListener('wheel', e => { e.preventDefault(); const m = gMouse(e); const z = Math.max(0.2, Math.min(4, gZoom * (e.deltaY < 0 ? 1.15 : 1 / 1.15))); const wx = (m.x - gc.width / 2) / gZoom - gPanX, wy = (m.y - gc.height / 2) / gZoom - gPanY; gZoom = z; gPanX = (m.x - gc.width / 2) / z - wx; gPanY = (m.y - gc.height / 2) / z - wy; gDraw(); }, { passive: false });
  function graphOpenNode(id) { if (gById.has(id)) openNote(id); }
