// Aides communes des pages de kdx-windows, au-dessus du pont de la suite (kdx-bridge.js) : post, log, icônes, gestionnaires de messages.
// Les pages sont servies par WebView2 sous https://kdx.shell/ (le dossier web/) : /kdx/kdx-ui.css, /kdx/fonts.css, /kdx/icons.js.
{ const t = new URLSearchParams(location.search).get('theme'); if (t) document.documentElement.dataset.theme = t; }
const hasHost =!!(window.chrome && window.chrome.webview);
const post = o => { if (window.__spy) window.__spy.push(o); if (hasHost) window.chrome.webview.postMessage(o); };   // __spy : réservé aux pages de test
const log = m => post({ cmd: 'log', msg: String(m) });
window.addEventListener('error', e => log('erreur JS : ' + e.message + ' (' + e.filename + ':' + e.lineno + ')'));
window.addEventListener('unhandledrejection', e => log('promesse rejetée : ' + e.reason));

// Icônes d'application : le programme renvoie un PNG par chemin et par taille (16 ou 32) ; on les garde en mémoire.
const iconCache = new Map(), iconWaiting = new Map();
const FALLBACK = 'data:image/svg+xml;utf8,' + encodeURIComponent(pix('fichier', 16));   // icône de repli : la feuille de la charte
function wantIcon(path, img, size = 16) {
  img.src = FALLBACK;
  if (!path) return;
  const key = size + '|' + path;
  if (iconCache.has(key)) { const p = iconCache.get(key); if (p) img.src = 'data:image/png;base64,' + p; return; }
  if (!iconWaiting.has(key)) { iconWaiting.set(key, []); post({ cmd: 'icon', path, size }); }
  iconWaiting.get(key).push(img);
}
function gotIcon(key, png) {
  iconCache.set(key, png);
  for (const img of iconWaiting.get(key) || []) if (png) img.src = 'data:image/png;base64,' + png;   // sans PNG, le repli reste
  iconWaiting.delete(key);
}

// Un gestionnaire posé dans `handlers` (handlers.tasks = m => ...) est branché sur le pont de la suite (kdx-bridge.js, chargé avant) et reçoit le message entier.
const bound = new Set(), table = {};
const handlers = new Proxy(table, { set(o, type, fn) { o[type] = fn; if (!bound.has(type)) { bound.add(type); kdx.on(type, (value, m) => { if (table[type]) table[type](m); }); } return true; } });
window.__handlers = handlers;   // pour les pages de test
kdx.on('icon', (value, m) => gotIcon((m.size || 16) + '|' + m.path, m.png));
// L'accent de la suite a changé (%APPDATA%\KDX\suite.json) : le programme le pousse à toutes ses pages.
kdx.on('theme', v => { if (v) document.documentElement.dataset.theme = v; });
const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

// Le bouton hachuré de la barre de titre (.tb-m avec data-act="menu") ouvre le menu « Réglages » de la fenêtre. Chaque page y met ses propres réglages avec
//   windowSettings(() => [ ['Libellé', action, { check, key, sub, disabled }], null, … ])      (même forme que KdxFrame.menu ; la fonction est appelée à chaque ouverture)
// et la suite y ajoute les siens, communs à tous les programmes (couleur d'accent). Sans réglage propre, le menu ne montre que les réglages communs.
const ACCENTS = [['rouge', 'Rouge'], ['bleu', 'Bleu'], ['vert', 'Vert'], ['violet', 'Violet'], ['ambre', 'Ambre'], ['sarcelle', 'Sarcelle'], ['charbon', 'Charbon']];
let settingsBuilder = () => [];
const windowSettings = build => { settingsBuilder = build; };
const commonSettings = () => {
  const cur = document.documentElement.dataset.theme || 'rouge';
  return [['Couleur d’accent', null, { sub: ACCENTS.map(([k, n]) => [n, () => post({ cmd: 'suite.accent', value: k }), { check: cur === k }]) }]];
};
document.addEventListener('kdx:menu', e => {
  const r = (e.target.closest('.tb-btn') || e.target).getBoundingClientRect();
  let own = []; try { own = settingsBuilder() || []; } catch (err) { log('réglages : ' + err.message); }
  KdxFrame.menu([...own, ...(own.length ? [null] : []), ...commonSettings()], r.left, r.bottom + 2, 'Réglages');
  // Une fenêtre dont la page est recouverte par d'autres vues natives (le navigateur) masque la vue le temps du menu : « win.overlay » l'annonce, l'hôte qui n'en a pas l'usage l'ignore.
  post({ cmd: 'win.overlay', on: true });
  const watch = new MutationObserver(() => { if (!document.querySelector('.menu[data-kdx]')) { watch.disconnect(); post({ cmd: 'win.overlay', on: false }); } });
  watch.observe(document.body, { childList: true });
});
