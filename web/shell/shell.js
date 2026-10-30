// Ce que seules les pages du shell (barre, menu Démarrer) utilisent, en plus du pont /kdx/host.js.
// Applications épinglées (barre et menu Démarrer). ic = icône pixel de la charte ; target = ce que Windows doit lancer.
const PINS = [
  { name: 'Explorateur', ic: 'dossier', target: 'kdx:explorer' },
  { name: 'Paramètres', ic: 'reglages', target: 'ms-settings:' },
  { name: 'Terminal', ic: 'console', target: 'wt.exe' },
  { name: 'Chrome', ic: 'serveur', target: 'chrome.exe' },
  { name: 'Navigateur KDX', ic: 'kirc', target: 'kdx:navigateur' },
  { name: 'Calculatrice', ic: 'apps', target: 'calc.exe' },
  { name: 'Bloc-notes', ic: 'journal', target: 'notepad.exe' },
  { name: 'Images', ic: 'image', target: 'shell:My Pictures' },
];

// Hors du shell (page ouverte dans un navigateur), des données d'exemple permettent de voir la mise en page.
if (!hasHost) window.addEventListener('load', () => {
  const f = (h, t, exe, active, min) => ({ h, title: t, exe, active, min });
  if (handlers.tasks) handlers.tasks({ items: [f('1', 'Documents — Explorateur', '', true, false), f('2', 'Paramètres', '', false, false), f('3', 'Terminal', '', false, false), f('4', 'notes-kdx.txt — Bloc-notes', '', false, true)] });
  if (handlers.stats) handlers.stats({ cpu: 12, ram: '5,1' });
  if (handlers.apps) handlers.apps({ items: ['7-Zip', 'Bloc-notes', 'Calculatrice', 'Chrome', 'Discord', 'Explorateur', 'Godot', 'OBS Studio', 'Paramètres', 'Terminal'].map(n => ({ name: n, path: n })) });
  if (handlers.start && location.search.includes('open')) handlers.start({ open: true });
  if (handlers.show) handlers.show();
});
