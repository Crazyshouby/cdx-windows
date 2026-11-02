// Le terminal CDX : des onglets, chacun un arbre de volets ; chaque volet est un shell (une session du programme) affiché par xterm.js.
//   page → programme   ready · term.new {sid,cols,rows,cwd?} · term.in {sid,data} · term.resize {sid,cols,rows} · term.close {sid} · term.copy {text} · term.paste {sid}
//   programme → page   hello {build,cwd?} · out {sid,b64} · exit {sid,code} · paste {sid,text} · open-tab {cwd} · theme (par host.js)
'use strict';
fillPix();
const $ = id => document.getElementById(id);

// ---------- police et couleurs ----------
// CDX Pixel Mono : la police console de la charte à chasse fixe (case de 7 pixels, 14 px). Les repli servent aux symboles qu'elle n'a pas (flèches, braille...) ;
// le moteur WebGL de xterm place chaque caractère dans sa case, un repli plus large ou plus étroit ne décale donc rien.
const FONT = '"CDX Pixel Mono", "Cascadia Mono", Consolas, "Lucida Console", monospace';

// Les seize couleurs ANSI viennent de la charte (cdx-ui.css, « genres de lignes d'une console ») : rouge = problème, ambre = annonce, vert sombre = présence,
// bleu = sujet, rose = action, cyan = lien, gris = voix du serveur. La charte n'a qu'une teinte par sens : les variantes vives reprennent la même quand elle n'en a pas d'autre.
const ANSI = {
  black: '#000000', red: '#ff5a4a', green: '#00a000', yellow: '#faa320', blue: '#6a8aff', magenta: '#ff5cf0', cyan: '#5cf0ff', white: '#c8c8c8',
  brightBlack: '#888888', brightRed: '#ff5a4a', brightGreen: '#00ff00', brightYellow: '#ffcc99', brightBlue: '#6a8aff', brightMagenta: '#ff5cf0', brightCyan: '#5cf0ff', brightWhite: '#ffffff',
};
// Le reste suit l'accent de la suite : texte = encre des champs du thème, curseur = accent, sélection = fond et texte de sélection du thème.
function palette() {
  const cs = getComputedStyle(document.documentElement), v = (n, d) => cs.getPropertyValue(n).trim() || d;
  return {
    ...ANSI,
    foreground: v('--field-ink', '#00ff00'), background: v('--ink', '#000000'),
    cursor: v('--accent', '#d41818'), cursorAccent: '#000000',
    selectionBackground: v('--sel-bg', '#001177'), selectionForeground: v('--sel-fg', '#ff00ff'), selectionInactiveBackground: '#222222',
    scrollbarSliderBackground: '#333333', scrollbarSliderHoverBackground: '#3b3b3b', scrollbarSliderActiveBackground: '#2a2a2a', overviewRulerBorder: '#000000',
  };
}
let windowsPty;   // { backend: 'conpty', buildNumber } dès que le programme a dit la version de Windows
const termOptions = () => ({
  fontFamily: FONT, fontSize: 14, lineHeight: 1,                // lineHeight 1 : la case fait la hauteur de la police (14 px), les caractères de cadre se touchent
  fontWeight: 'normal', fontWeightBold: 'normal',                // pas de gras (CHARTE.md §8) : le « gras » des programmes passe en couleur vive
  cursorStyle: 'block', cursorBlink: false, scrollback: 10000, theme: palette(), windowsPty,
});
const bytes = b64 => { const s = atob(b64), a = new Uint8Array(s.length); for (let i = 0; i < s.length; i++) a[i] = s.charCodeAt(i); return a; };

// ---------- modèle : onglets > arbre de volets > shells ----------
let nextSid = 1, nextTab = 1, active = null;
const tabs = [];            // { id, view, root, focus }
const bySid = new Map();    // sid -> Pane

class Pane {
  constructor(tab) {
    this.sid = nextSid++; this.tab = tab; this.title = ''; this.parent = null; this.started = false;
    this.el = document.createElement('div'); this.el.className = 'pane';
    const box = document.createElement('div'); box.className = 'termbox';
    this.host = document.createElement('div'); this.host.className = 'term';
    box.append(this.host); this.el.append(box);
    bySid.set(this.sid, this);
  }

  // À appeler quand l'élément est dans la page et visible : xterm mesure la case de la police et la place disponible.
  start() {
    const term = this.term = new Terminal(termOptions());
    const fit = this.fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    term.open(this.host);
    try { const gl = new WebglAddon.WebglAddon(); gl.onContextLoss(() => gl.dispose()); term.loadAddon(gl); this.gl = gl; }
    catch (e) { log('WebGL indisponible, affichage sans WebGL : ' + e.message); }
    fit.fit();
    post({ cmd: 'term.new', sid: this.sid, cols: term.cols, rows: term.rows, cwd: this.cwd });
    this.started = true;
    term.onData(d => post({ cmd: 'term.in', sid: this.sid, data: d }));
    term.onResize(({ cols, rows }) => post({ cmd: 'term.resize', sid: this.sid, cols, rows }));
    term.onTitleChange(t => { this.title = t || ''; renderTabs(); });
    term.attachCustomKeyEventHandler(e => onKey(this, e));
    this.el.addEventListener('focusin', () => focusPane(this));
    this.el.addEventListener('contextmenu', e => { e.preventDefault(); paneMenu(this, e.clientX, e.clientY); });
    let queued = false;   // la grille suit la taille du volet (fenêtre, séparateur, onglet qui redevient visible)
    this.ro = new ResizeObserver(() => { if (queued) return; queued = true; requestAnimationFrame(() => { queued = false; try { fit.fit(); } catch (e) { log('fit : ' + e.message); } }); });
    this.ro.observe(this.host);
    term.focus();
  }

  dispose() { try { this.ro?.disconnect(); this.term?.dispose(); } catch (e) { log('fermeture du volet : ' + e.message); } }
}

class Split {
  constructor(dir, a, b) {          // dir : 'h' = côte à côte, 'v' = l'un sous l'autre
    this.dir = dir; this.a = a; this.b = b; this.ratio = 0.5; this.parent = null;
    this.el = document.createElement('div'); this.el.className = 'sp ' + dir;
    this.cellA = document.createElement('div'); this.cellA.className = 'cell';
    this.dv = document.createElement('i'); this.dv.className = 'dv';
    this.cellB = document.createElement('div'); this.cellB.className = 'cell';
    this.el.append(this.cellA, this.dv, this.cellB);
    this.apply();
    this.dv.addEventListener('pointerdown', e => this.drag(e));
  }
  apply() { this.cellA.style.flexGrow = this.ratio; this.cellB.style.flexGrow = 1 - this.ratio; }
  drag(e) {
    if (e.button !== 0) return;
    e.preventDefault(); this.dv.setPointerCapture(e.pointerId);
    const move = ev => {
      const r = this.el.getBoundingClientRect();
      const p = this.dir === 'h' ? (ev.clientX - r.left) / r.width : (ev.clientY - r.top) / r.height;
      this.ratio = Math.min(0.9, Math.max(0.1, p)); this.apply();
    };
    const up = () => { this.dv.removeEventListener('pointermove', move); this.dv.removeEventListener('pointerup', up); this.dv.removeEventListener('pointercancel', up); };
    this.dv.addEventListener('pointermove', move); this.dv.addEventListener('pointerup', up); this.dv.addEventListener('pointercancel', up);
  }
}

const elOf = n => n.el;
function panesOf(n, out = []) { if (n instanceof Pane) out.push(n); else { panesOf(n.a, out); panesOf(n.b, out); } return out; }

// ---------- opérations ----------
function newTab(cwd) {          // cwd : dossier de départ du shell (sinon le dossier personnel)
  const tab = { id: nextTab++, view: document.createElement('div'), root: null, focus: null };
  tab.view.className = 'tabview';
  $('stage').append(tab.view);
  const p = new Pane(tab);
  p.cwd = typeof cwd === 'string' ? cwd : undefined;
  tab.root = tab.focus = p;
  tab.view.append(p.el);
  tabs.push(tab);
  activate(tab, false);
  p.start();
  focusPane(p);
  return tab;
}

function splitPane(pane, dir) {
  const tab = pane.tab, np = new Pane(tab), sp = new Split(dir, pane, np), parent = pane.parent;
  pane.el.replaceWith(sp.el);
  sp.cellA.append(pane.el); sp.cellB.append(np.el);
  sp.parent = parent;
  if (parent) { if (parent.a === pane) parent.a = sp; else parent.b = sp; } else tab.root = sp;
  pane.parent = sp; np.parent = sp;
  np.start();
  focusPane(np);
  return np;
}

// Retire un volet de l'arbre (sa session est déjà finie ou fermée) ; le voisin prend sa place.
function removePane(p) {
  if (!bySid.delete(p.sid)) return;
  const tab = p.tab, parent = p.parent;
  p.dispose();
  if (!parent) { dropTab(tab); return; }
  const sib = parent.a === p ? parent.b : parent.a, gp = parent.parent;
  parent.el.replaceWith(elOf(sib));
  sib.parent = gp;
  if (gp) { if (gp.a === parent) gp.a = sib; else gp.b = sib; } else tab.root = sib;
  if (tab.focus === p) tab.focus = panesOf(sib)[0];
  if (tab === active) { focusPane(tab.focus); tab.focus.term.focus(); }
  renderTabs();
}

function closePane(p) { if (!p || !bySid.has(p.sid)) return; post({ cmd: 'term.close', sid: p.sid }); removePane(p); }

function dropTab(tab) {
  const i = tabs.indexOf(tab); if (i < 0) return;
  for (const p of panesOf(tab.root)) { bySid.delete(p.sid); p.dispose(); }
  tab.view.remove(); tabs.splice(i, 1);
  if (!tabs.length) { active = null; cdx.win.close(); return; }
  if (tab === active) activate(tabs[Math.min(i, tabs.length - 1)]);
  else renderTabs();
}

function closeTab(tab) { for (const p of panesOf(tab.root)) post({ cmd: 'term.close', sid: p.sid }); dropTab(tab); }

function activate(tab, focus = true) {
  active = tab;
  for (const t of tabs) t.view.classList.toggle('on', t === tab);
  renderTabs();
  if (focus && tab.focus) { focusPane(tab.focus); tab.focus.term?.focus(); }   // le focus de la page peut ne pas suivre si la fenêtre n'est pas au premier plan : on le dit au programme aussi
}

function focusPane(p) {
  if (!p) return;
  p.tab.focus = p;
  if (p.tab === active) { renderTabs(); post({ cmd: 'term.focus', sid: p.sid }); }   // le programme sait quel shell reçoit les touches (utile au canal de test)
}

function selectTab(i) { if (tabs[i]) activate(tabs[i]); }
function cycle(d) { selectTab((tabs.indexOf(active) + d + tabs.length) % tabs.length); }

// Le volet voisin dans une direction : celui dont le centre est le plus proche dans cette direction.
function moveFocus(dx, dy) {
  const cur = active.focus, list = panesOf(active.root).filter(p => p !== cur && p.started);
  const c = r => ({ x: r.left + r.width / 2, y: r.top + r.height / 2 });
  const me = c(cur.el.getBoundingClientRect());
  let best = null, bd = Infinity;
  for (const p of list) {
    const o = c(p.el.getBoundingClientRect()), vx = o.x - me.x, vy = o.y - me.y;
    if ((dx && Math.sign(vx) !== dx) || (dy && Math.sign(vy) !== dy)) continue;
    const d = dx ? Math.abs(vx) + 2 * Math.abs(vy) : Math.abs(vy) + 2 * Math.abs(vx);
    if (d < bd) { bd = d; best = p; }
  }
  if (best) { focusPane(best); best.term.focus(); }
}

function splitAuto(p) { const r = p.el.getBoundingClientRect(); return splitPane(p, r.width > r.height * 1.2 ? 'h' : 'v'); }

// ---------- onglets : l'affichage ----------
function tabTitle(t) { return (t.focus && t.focus.title) || 'Terminal'; }
function renderTabs() {
  $('tabs').innerHTML = tabs.map(t => `<button class="tab ${t === active ? 'on' : ''}" data-id="${t.id}" title="${esc(tabTitle(t))}"><span class="tt">${esc(tabTitle(t))}</span><span class="x" data-x="${t.id}" title="Fermer l’onglet">×</span></button>`).join('')
    + '<button class="tab new" data-new title="Nouvel onglet (Ctrl+Maj+T)">+</button>';
  const title = active ? tabTitle(active) : 'Terminal';
  $('ttl').textContent = title; cdx.win.title(title);
}
$('tabs').addEventListener('click', e => {
  if (e.target.closest('[data-new]')) { newTab(); return; }
  const x = e.target.closest('[data-x]');
  if (x) { const t = tabs.find(t => t.id === +x.dataset.x); if (t) closeTab(t); return; }
  const b = e.target.closest('[data-id]'); if (b) { const t = tabs.find(t => t.id === +b.dataset.id); if (t) activate(t); }
});
$('tabs').addEventListener('auxclick', e => {          // clic du milieu : ferme l'onglet
  if (e.button !== 1) return;
  const b = e.target.closest('[data-id]'); if (b) { const t = tabs.find(t => t.id === +b.dataset.id); if (t) closeTab(t); }
});

// ---------- clavier et menu ----------
// Les raccourcis de Windows Terminal que Valentin connaît : Ctrl+Maj+T / W, Ctrl+Tab, Ctrl+Alt+1..9, Alt+Maj+D (séparer), Alt+flèches (changer de volet).
// Écoutés avant xterm.js : un raccourci traité ne part pas vers le shell.
document.addEventListener('keydown', e => {
  if (!active || e.defaultPrevented) return;
  const c = e.code, ctrl = e.ctrlKey, alt = e.altKey, shift = e.shiftKey;
  let done = true;
  if (ctrl && shift && !alt && c === 'KeyT') newTab();
  else if (ctrl && shift && !alt && c === 'KeyW') closePane(active.focus);
  else if (ctrl && !alt && c === 'Tab') cycle(shift ? -1 : 1);
  else if (ctrl && alt && !shift && /^Digit[1-9]$/.test(c)) selectTab(+c.slice(5) - 1);
  else if (alt && shift && !ctrl && c === 'KeyD') splitAuto(active.focus);
  else if (alt && shift && !ctrl && e.key === '+') splitPane(active.focus, 'h');
  else if (alt && !shift && !ctrl && c === 'ArrowLeft') moveFocus(-1, 0);
  else if (alt && !shift && !ctrl && c === 'ArrowRight') moveFocus(1, 0);
  else if (alt && !shift && !ctrl && c === 'ArrowUp') moveFocus(0, -1);
  else if (alt && !shift && !ctrl && c === 'ArrowDown') moveFocus(0, 1);
  else done = false;
  if (done) { e.preventDefault(); e.stopPropagation(); }
}, true);

// Presse-papiers : Ctrl+C copie s'il y a une sélection (sinon c'est l'interruption du programme), Ctrl+Maj+C copie toujours ; Ctrl+V, Ctrl+Maj+V et Maj+Inser collent.
// Le programme lit et écrit le presse-papiers lui-même : la page n'a pas à demander d'autorisation.
function copyOf(p) { const t = p.term.getSelection(); if (t) { post({ cmd: 'term.copy', text: t }); p.term.clearSelection(); } }
const pasteInto = p => post({ cmd: 'term.paste', sid: p.sid });
function onKey(p, e) {
  if (e.type !== 'keydown') return true;
  const k = e.key.toLowerCase();
  if (e.ctrlKey && !e.altKey && k === 'c' && (e.shiftKey || p.term.hasSelection())) { copyOf(p); e.preventDefault(); return false; }
  if ((e.ctrlKey && !e.altKey && k === 'v') || (e.shiftKey && e.key === 'Insert')) { pasteInto(p); e.preventDefault(); return false; }
  return true;
}

// Les réglages de la fenêtre (bouton hachuré de la barre de titre, host.js) : les mêmes actions que le menu d'un volet ; la suite y ajoute le thème.
windowSettings(() => [
  ['Nouvel onglet', () => newTab(), { key: 'Ctrl+Maj+T' }],
  ['Séparer à droite', () => { if (active && active.focus) splitPane(active.focus, 'h'); }, { key: 'Alt+Maj++', disabled: !(active && active.focus) }],
  ['Séparer en bas', () => { if (active && active.focus) splitPane(active.focus, 'v'); }, { disabled: !(active && active.focus) }],
]);

function paneMenu(p, x, y) {
  const back = f => () => { f(); setTimeout(() => (bySid.has(p.sid) ? p : active?.focus)?.term?.focus(), 0); };
  CdxFrame.menu([
    ['Copier', back(() => copyOf(p)), { key: 'Ctrl+C', disabled: !p.term.hasSelection() }],
    ['Coller', back(() => pasteInto(p)), { key: 'Ctrl+V' }],
    null,
    ['Séparer à droite', back(() => splitPane(p, 'h')), { key: 'Alt+Maj++' }],
    ['Séparer en bas', back(() => splitPane(p, 'v'))],
    ['Fermer le volet', back(() => closePane(p)), { key: 'Ctrl+Maj+W' }],
    null,
    ['Nouvel onglet', back(() => newTab()), { key: 'Ctrl+Maj+T' }],
  ], x, y);
}

// ---------- messages du programme ----------
let gotHello; const helloP = new Promise(r => { gotHello = r; });
handlers.hello = m => { windowsPty = { backend: 'conpty', buildNumber: m.build }; for (const p of bySid.values()) if (p.term) p.term.options.windowsPty = windowsPty; gotHello(m); };   // le reflux des lignes au redimensionnement dépend de la version de Windows
handlers.out = m => bySid.get(m.sid)?.term?.write(bytes(m.b64));
handlers.exit = m => { const p = bySid.get(m.sid); if (p) removePane(p); };   // le shell s'est terminé : le volet se ferme avec lui, comme dans Windows Terminal
handlers.paste = m => bySid.get(m.sid)?.term?.paste(m.text);
handlers['open-tab'] = m => { newTab(m.cwd); };   // « Ouvrir dans un terminal » (Explorateur) : un onglet dans ce dossier
cdx.on('theme', () => { const t = palette(); for (const p of bySid.values()) if (p.term) p.term.options.theme = t; });   // host.js a déjà posé le nouvel accent sur <html>
window.addEventListener('focus', () => active?.focus?.term?.focus());

// Pour les pages de test.
window.__tabs = tabs; window.__panes = bySid;
window.__screenText = p => { const b = (p || active.focus).term.buffer.active, o = []; for (let i = 0; i < b.length; i++) o.push(b.getLine(i).translateToString(true)); return o.join('\n').replace(/\s+$/, ''); };
window.__api = { newTab, splitPane, closePane, closeTab, activate, moveFocus, splitAuto };

// ---------- démarrage ----------
function demo() {
  // Hors du programme (page ouverte dans un navigateur) : un exemple de sortie pour voir les couleurs, la police et les volets.
  const E = '\x1b[', fg = [30, 31, 32, 33, 34, 35, 36, 37, 90, 91, 92, 93, 94, 95, 96, 97];
  const text = [
    'Windows PowerShell\r\n',
    'PS C:\\Users\\Valentin> ' + E + '93mdir' + E + '0m\r\n',
    E + '36m    Répertoire : C:\\Users\\Valentin\\Dev' + E + '0m\r\n',
    'Mode                 LastWriteTime         Length Name\r\n',
    'd-----        31/10/2026     16:25                cdx-ui\r\n',
    'd-----        31/10/2026     16:50                cdx-windows\r\n\r\n',
    fg.map(c => E + c + 'm' + String(c).padStart(3) + E + '0m').join(' ') + '\r\n',
    E + '31merreur : exemple' + E + '0m  ' + E + '33mattention : exemple' + E + '0m  ' + E + '92mok : exemple' + E + '0m  ' + E + '3mitalique' + E + '0m  é€ß\r\n',
    '┌────────────────────┐\r\n│ encadré  ░▒▓█ ▀▄ │\r\n├──────┬─────────────┤\r\n└──────┴─────────────┘  ╭─╮ ╰─╯ ━ ┃ ═ ║\r\n',
    'PS C:\\Users\\Valentin> ',
  ].join('');
  const q = new URLSearchParams(location.search).get('demo');
  const first = active.focus; first.term.write(text);
  if (q === 'split') {
    const p2 = splitPane(first, 'h'); p2.term.write(text.split('\r\n').slice(0, 5).join('\r\n') + '\r\n' + E + '95mdeuxième volet' + E + '0m\r\nPS> ');
    const p3 = splitPane(p2, 'v'); p3.term.write('troisième volet\r\nPS> ');
    newTab().focus.term.write('second onglet\r\nPS> ');
    activate(tabs[0]);
  }
}

CdxFrame.init();
Promise.race([
  Promise.all([document.fonts.load('14px "CDX Pixel Mono"'), document.fonts.load('italic 14px "CDX Pixel Mono"')]),
  new Promise(r => setTimeout(r, 2000)),
]).then(async () => {
  if (!hasHost) { newTab(); demo(); return; }
  post({ cmd: 'ready' });
  const m = await Promise.race([helloP, new Promise(r => setTimeout(r, 1500))]);   // le programme dit si un dossier de départ a été demandé
  newTab(m && m.cwd);
});
