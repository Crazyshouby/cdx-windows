// Pont entre les pages et le programme .NET qui les héberge (kdx-shell, kdx-explorer ou kdx-browser).
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

const handlers = {};
window.__handlers = handlers;   // pour les pages de test
if (hasHost) window.chrome.webview.addEventListener('message', e => { const m = e.data; if (m.type === 'icon') gotIcon((m.size || 16) + '|' + m.path, m.png); else if (handlers[m.type]) handlers[m.type](m); });
// L'accent de la suite a changé (%APPDATA%\KDX\suite.json) : le programme le pousse à toutes ses pages.
handlers.theme = m => { if (m.value) document.documentElement.dataset.theme = m.value; };
const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
