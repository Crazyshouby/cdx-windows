# kdx-windows : l'environnement KDX pour Windows, en trois programmes

La charte KDX-UI (dépôt `kdx-ui`) appliquée à Windows : la barre des tâches et le menu Démarrer à la place de ceux de Windows, un Explorateur de fichiers et
un navigateur web. Chaque page est du HTML/CSS de la charte servi par WebView2 ; les programmes .NET 8 ne font que le lien avec Windows.

```
pwsh -File build.ps1               # compile les trois programmes dans bin\
pwsh -File lancer.ps1 [thème]      # compile si besoin, lance le shell (rouge bleu vert violet ambre sarcelle charbon)
pwsh -File restaurer.ps1           # arrête le shell et rend sa barre à Windows
```

| Programme | Rôle | Instance | Données |
|---|---|---|---|
| `bin\kdx-shell.exe` | la barre, le menu Démarrer, le centre rapide, Alt+Tab, la touche Windows, la liaison avec AudioLinker | une | `journal.log` |
| `bin\kdx-explorer.exe [--theme t] [dossier]` | l'Explorateur : une fenêtre par dossier ouvert, s'arrête avec la dernière | une (un second lancement ouvre une fenêtre dans la première) | `journal-explorer.log` |
| `bin\kdx-browser.exe [--theme t] [adresse]` | le navigateur à onglets | une (un second lancement ouvre un onglet) | `journal-browser.log`, profil `navigateur\` |

Le shell lance les deux autres (`kdx:explorer`, `kdx:navigateur`, Win+E) : **un plantage de l'un n'emporte pas les autres** (essai `tests\suite.ps1`).
Toutes les données sont dans `%LOCALAPPDATA%\KdxShell\`.

## Organisation

```
src\Kdx.Host\       la bibliothèque commune : fenêtre hôte WebView2, appels Windows, journal, instance unique et relais, canal de test,
                    boutons latéraux de la souris (types internes, visibles des trois programmes seulement)
src\Kdx.Shell\      kdx-shell : Program.cs (barre, Démarrer, centre, Alt+Tab, crochet clavier), AudioLink.cs, Sys.cs
src\Kdx.Explorer\   kdx-explorer : Fs.cs (fichiers), ExplorerForm.cs
src\Kdx.Browser\    kdx-browser : Browser.cs (onglets, favoris, historique, téléchargements)
web\kdx\            la charte (copie exacte de kdx-ui/dist : node tools/charte-sync.mjs --write kdx-windows) et host.js, le pont des pages
web\shell\ web\explorer\ web\browser\   les pages de chaque programme (les _test-*.html servent aux auto-tests)
couche1\            le cadre des fenêtres de Windows aux couleurs de la charte (DWM), réversible
tests\              navigateur.ps1 (22 vérifications), suite.ps1 (lancement, relais, isolation), bus.ps1 (bus et accent, isolé)
```

## Essais (aucune entrée simulée)

- Auto-tests, sans fenêtre visible : `kdx-explorer --selftest-fs`, `--selftest-web` ; `kdx-shell --selftest-sys`, `--selftest-audio` ; `kdx-browser --selftest-browser`.
  Résultats dans `%LOCALAPPDATA%\KdxShell\selftest*.txt`.
- Canal de contrôle : si `%LOCALAPPDATA%\KdxShell\debug.txt` existe au lancement, chaque programme ouvre un tube réservé à l'utilisateur courant
  (`kdx.shell.ctl`, `kdx.explorer.ctl`, `kdx.browser.ctl`) pour lire son état et exécuter du JavaScript dans ses pages. `tests\navigateur.ps1` et
  `tests\suite.ps1` s'en servent ; supprimer `debug.txt` et relancer ensuite.

## Réglages communs et bus (phase 2)
- **Accent de la suite** : `%APPDATA%\KDX\suite.json` (`{"version":1,"accent":"bleu"}`). Chaque programme le relit à chaud (250 ms) et recolore ses pages ; `kdx-shell.exe --accent bleu` l'écrit.
  Sans argument, les programmes le suivent ; `--theme <nom>` (ou `lancer.ps1 <nom>`) fixe un thème pour ce lancement seulement.
- **Bus** : un tube `\\.\pipe\kdx.<nom>` par programme (`Bus.cs`), messages `{type, from, data}` validés par liste blanche : `notify` (shell), `open-path` et `select` (Explorateur),
  `open-url` (navigateur, http et https seulement). Lancer `kdx-browser.exe <adresse>` alors qu'il tourne déjà passe par ce bus. Spécification : `kdx-ui/docs/SUITE.md`.
- Essais : `kdx-shell --selftest-bus` (19 vérifications), `tests\bus.ps1` (essai de bout en bout isolé : ni le vrai bus, ni le vrai `suite.json`, ni le vrai profil du navigateur).

## Synergies (phase 4)
- **Notifications** : `notify` sur le bus du shell → une cloche et le nombre de notifications non vues dans la barre (place fixe), la liste dans le centre rapide (50 au plus, « Effacer »).
  Émetteurs : KIRC (messages privés, mentions), kdx-browser (téléchargement terminé ou échoué), AudioLinker (erreurs et avertissements).
- **Liens** : un lien cliqué dans KIRC s'ouvre dans kdx-browser (lancé au besoin d'après `apps.json`, sinon navigateur de Windows).
- **Fichiers** : le double-clic sur une page HTML, un PDF, une image, un son ou une vidéo dans l'Explorateur les ouvre dans le navigateur (`open-file`) ; « Afficher dans le dossier » d'un téléchargement ouvre l'Explorateur sur le fichier (`select`).
- **Son** : le groupe d'AudioLinker (volume, coupure, setups) est dans la barre (voir ci-dessous).
- Essai de bout en bout isolé : `tests\bus.ps1` (sections 6 et 7).

## Liaison avec AudioLinker
Le shell se connecte au tube `\\.\pipe\kdx.audiolinker` (voir AudioLinker `src/KdxLink.cpp`) et affiche dans la barre le groupe (voyant, coupure, volume, setups) tant
qu'AudioLinker tourne sur la machine.

## Pas encore
Interface des notifications dans le centre rapide, sélecteur d'accent, installeur (phases 4 et 5 du plan de la suite), signature.
Les anciens fichiers sous `KIRC\charte\windows\` ne sont pas supprimés.
