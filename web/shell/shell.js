// Ce que seules les pages du shell (barre, menu Démarrer) utilisent, en plus du pont /host/host.js.
// Les applications épinglées viennent du programme (message « pins », fichier epingles.json) : bar = affichée dans la barre, tile = tuile du menu Démarrer.
// Une icône : ic = icône pixel de la charte (repli), path = fichier dont on extrait l'icône réelle quand la cible en est un.
function pinIcon(p, size) {
  return p.path ? `<img data-p="${esc(p.path)}" data-size="${size}" alt="">` : pix(p.ic || 'apps', size);
}
function fillPinIcons(root, size) { root.querySelectorAll('img[data-p]').forEach(img => wantIcon(img.dataset.p, img, size)); }

// Hors du shell (page ouverte dans un navigateur), des données d'exemple permettent de voir la mise en page.
if (!hasHost) window.addEventListener('load', () => {
  const f = (h, t, exe, active, min, count = 1, g = null) => ({ h, title: t, exe, active, min, count, g });
  let rank = 0;
  const pin = (name, ic, bar, tile) => ({ name, ic, target: 'cdx:' + name, bar, tile, barIndex: bar ? rank++ : -1 });
  if (handlers.pins) handlers.pins({ items: [pin('Explorateur', 'explorateur', true, true), pin('Paramètres', 'reglages', true, true), pin('Terminal', 'console', true, true), pin('Chrome', 'serveur', true, true), pin('Navigateur CDX', 'navigateur', true, true), pin('Dépôts', 'liste', false, true), pin('Calculatrice', 'apps', false, true), pin('Bloc-notes', 'journal', false, true), pin('Images', 'image', false, true)] });
  if (handlers.tasks) handlers.tasks({ items: [f('1', 'Documents — Explorateur', '', true, false), f('2', 'Paramètres', '', false, false), f('3', 'Terminal', '', false, false), f('4', 'Bloc-notes', '', false, true, 3, 'c:\\windows\\notepad.exe')] });
  if (handlers.stats) handlers.stats({ cpu: 12, ram: '5,1' });
  if (handlers.apps) handlers.apps({ items: ['7-Zip', 'Bloc-notes', 'Calculatrice', 'Chrome', 'Discord', 'Explorateur', 'Godot', 'OBS Studio', 'Paramètres', 'Terminal'].map(n => ({ name: n, path: n })) });
  if (handlers.start && location.search.includes('open')) handlers.start({ open: true });
  if (handlers.show) handlers.show();
});
