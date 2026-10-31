# cdx-windows : l'environnement CDX pour Windows, en trois programmes

La charte CDX-UI (dépôt `cdx-ui`) appliquée à Windows : la barre des tâches et le menu Démarrer à la place de ceux de Windows, un Explorateur de fichiers et
un navigateur web. Chaque page est du HTML/CSS de la charte servi par WebView2 ; les programmes .NET 8 ne font que le lien avec Windows.

```
pwsh -File build.ps1               # compile les trois programmes dans bin\
pwsh -File lancer.ps1 [thème]      # compile si besoin, lance le shell (rouge bleu vert violet ambre sarcelle charbon)
pwsh -File restaurer.ps1           # arrête le shell et rend sa barre à Windows
```

| Programme | Rôle | Instance | Données |
|---|---|---|---|
| `bin\cdx-shell.exe` | la barre, le menu Démarrer, le centre rapide, Alt+Tab, la touche Windows, la liaison avec AudioLinker | une | `journal.log` |
| `bin\cdx-explorer.exe [--theme t] [dossier]` | l'Explorateur : une fenêtre par dossier ouvert, s'arrête avec la dernière ; **glisser-déposer** (voir ci-dessous) | une (un second lancement ouvre une fenêtre dans la première) | `journal-explorer.log` |
| `bin\cdx-browser.exe [--theme t] [adresse]` | le navigateur à onglets | une (un second lancement ouvre un onglet) | `journal-browser.log`, profil `navigateur\` |
| `bin\cdx-repos.exe [--theme t]` | **Dépôts** : tous les projets git du PC (ce qui reste à enregistrer ou à envoyer sur GitHub), en lecture seule | une (un second lancement passe devant) | `journal-repos.log`, `repos.json` (dossiers explorés, projets masqués) |
| `bin\cdx-terminal.exe [--theme t] [dossier]` | **Terminal** : un shell (PowerShell 7 s'il est installé, sinon Windows PowerShell) dans une fenêtre de la charte : cadre CDX, fond noir, texte à la couleur d'accent, aucun coin arrondi. Pseudo-console Windows (ConPTY) côté C#, xterm.js dans la page. **Onglets** (Ctrl+Maj+T / W, Ctrl+Tab, Ctrl+Alt+1..9), **volets** côte à côte ou l'un sous l'autre (Alt+Maj+D, menu clic droit, séparateurs déplaçables, Alt+flèches), un shell par volet ; police pixel à chasse fixe de la charte (KIRC Pixel Mono, case de 7 px), texte dessiné par WebGL. Lancé avec un dossier (ou, s'il tourne déjà, un second lancement) : un onglet dans ce dossier. **Win+T** l'ouvre depuis le shell ; la tuile « Terminal CDX » du menu Démarrer le lance ; « Ouvrir dans un terminal » de l'Explorateur l'utilise (Windows Terminal puis cmd en repli) | une (un second lancement passe devant) | `journal-terminal.log` |

Le shell lance les autres (`cdx:explorer`, `cdx:navigateur`, `cdx:repos`, `cdx:terminal`, Win+E, Win+T) : **un plantage de l'un n'emporte pas les autres** (essai `tests\suite.ps1`).
Toutes les données sont dans `%LOCALAPPDATA%\CdxShell\`.

## Organisation

```
src\Cdx.Host\       la bibliothèque commune : fenêtre hôte WebView2, appels Windows, journal, instance unique et relais, canal de test,
                    boutons latéraux de la souris (types internes, visibles des trois programmes seulement)
src\Cdx.Shell\      cdx-shell : Program.cs (barre, Démarrer, centre, Alt+Tab, crochet clavier), AudioLink.cs, Sys.cs
src\Cdx.Explorer\   cdx-explorer : Fs.cs (fichiers), ExplorerForm.cs
src\Cdx.Browser\    cdx-browser : Browser.cs (onglets, favoris, historique, téléchargements)
src\Cdx.Repos\      cdx-repos : Git.cs (recherche et lecture de git, jamais d'écriture), ReposApp.cs
src\Cdx.Terminal\   cdx-terminal : ConPty.cs (la pseudo-console : lancer le shell, faire passer les octets, redimensionner), TerminalApp.cs (fenêtre, une session par volet, messages de la page, presse-papiers)
web\terminal\       la page du terminal (terminal.html, terminal.css, terminal.js : onglets, arbre de volets) ; fonts\ = KIRC Pixel Mono (générée par cdx-ui\gen\gen_font_mono.py, copie à refaire si la police change) ; vendor\ = xterm.js 6.0.0, addon-fit 0.11.0 et addon-webgl 0.19.0 (MIT, copiés tels quels ; exceptions du lint dans charte-lint.allow)
web\cdx\            la charte (copie exacte de cdx-ui/dist : node tools/charte-sync.mjs --write cdx-windows) et host.js, le pont des pages
web\shell\ web\explorer\ web\browser\ web\repos\   les pages de chaque programme (les _test-*.html servent aux auto-tests)
couche1\            le cadre des fenêtres de Windows aux couleurs de la charte (DWM), réversible
tests\              navigateur.ps1 (22 vérifications), suite.ps1 (lancement, relais, isolation), bus.ps1 (bus et accent, isolé), repos.ps1 (Dépôts : 21 vérifications sur des dépôts jetables), explorer.ps1 (l'Explorateur : liste, réglages, surveillance, sélection, raccourcis, menus, recherche, corbeille, annuler : 134 vérifications), dnd.ps1 (glisser-déposer de l'Explorateur : 31 vérifications, dans la vraie fenêtre isolée)
```

## Essais (aucune entrée simulée)

- Auto-tests, sans fenêtre visible : `cdx-explorer --selftest-fs`, `--selftest-web` ; `cdx-shell --selftest-sys`, `--selftest-audio` ; `cdx-browser --selftest-browser` ; `cdx-repos --selftest-git` (22 vérifications sur de vrais dépôts jetables) ; `cdx-terminal --selftest-pty` (10 vérifications de la pseudo-console : sortie, code de sortie, UTF-8, redimensionnement, arrêt) ; `tests\terminal.ps1 -Bin <dossier>` (43 vérifications dans la vraie fenêtre isolée : shells, police et WebGL, couleurs de la charte, accent, onglets, volets, raccourcis, redimensionnement, titre, fermeture, dossier de départ, et avec `-ExplorerBin` l'Explorateur qui ouvre un onglet puis relance le terminal).
  Résultats dans `%LOCALAPPDATA%\CdxShell\selftest*.txt`.
- Canal de contrôle : si `%LOCALAPPDATA%\CdxShell\debug.txt` existe au lancement, chaque programme ouvre un tube réservé à l'utilisateur courant
  (`cdx.shell.ctl`, `cdx.explorer.ctl`, `cdx.browser.ctl`, `cdx.repos.ctl`) pour lire son état et exécuter du JavaScript dans ses pages. `tests\navigateur.ps1` et
  `tests\suite.ps1` s'en servent ; supprimer `debug.txt` et relancer ensuite.

## Réglages communs et bus (phase 2)
- **Accent de la suite** : `%APPDATA%\CDX\suite.json` (`{"version":1,"accent":"bleu"}`). Chaque programme le relit à chaud (250 ms) et recolore ses pages ; `cdx-shell.exe --accent bleu` l'écrit.
  Sans argument, les programmes le suivent ; `--theme <nom>` (ou `lancer.ps1 <nom>`) fixe un thème pour ce lancement seulement.
- **Bus** : un tube `\\.\pipe\cdx.<nom>` par programme (`Bus.cs`), messages `{type, from, data}` validés par liste blanche : `notify` (shell), `open-path` et `select` (Explorateur),
  `open-url` (navigateur, http et https seulement), `show` (Dépôts). Lancer `cdx-browser.exe <adresse>` alors qu'il tourne déjà passe par ce bus. Spécification : `cdx-ui/docs/SUITE.md`.
- Essais : `cdx-shell --selftest-bus` (19 vérifications), `tests\bus.ps1` (essai de bout en bout isolé : ni le vrai bus, ni le vrai `suite.json`, ni le vrai profil du navigateur).

## Socle de l'Explorateur (lot 0 du plan « l'Explorateur se suffit à lui-même »)
- **Page en modules** : `web/explorer/explorer.html` (balisage), `explorer.css`, `js/*.js` (core, tabs, list, nav, select, commands, menu, keys, dnd, boot) + `preview.js`, scripts classiques qui partagent leurs noms.
- **Réglages** (`Settings.cs`) : `explorer.json` dans le dossier de données, une clé à la fois (`fs.settings.get` / `fs.settings.set`, clés `[a-z0-9._-]`, 256 Ko au plus, écriture atomique), renvoyés à toutes les fenêtres ;
  côté page `settings` et `setSetting(clé, valeur)`.
- **Tâches de fond** (`Jobs.cs`) : file d'attente (copier, déplacer, supprimer, chercher), avancement, vitesse lissée, pause, reprise, annulation ; messages `fs.job` à toutes les fenêtres (utilisée par le lot 4).
- **La liste suit le dossier** (`Watch.cs`) : un `FileSystemWatcher` par onglet prévient la page (`fs.changed`, au plus tous les 250 ms), qui relit en gardant sélection et défilement (pas pendant un renommage ni un glisser) ; un dossier supprimé fait remonter au parent.
- **Éléments cachés et extensions** : menu « Affichage » (réglages `show.hidden`, `show.ext`, partagés par toutes les fenêtres) ; un élément caché est estompé ; sans extension affichée le renommage garde l'extension.
- **Menu contextuel complet** (`js/menu.js`, `ShellData.cs`) : sur un élément — Ouvrir, **Ouvrir avec ▸** (les programmes que Windows connaît pour l'extension : liste récente, classes, identifiants de programme ; « Choisir une autre application… » = la boîte de Windows), nouvel onglet / nouvelle fenêtre (dossiers), **terminal** (Windows Terminal sinon cmd), Couper, Copier, **Copier le chemin**, **Envoyer vers ▸** (dossier SendTo : dossiers, raccourcis vers un programme ou un dossier, disques amovibles ; une destination dossier copie, avec la boîte « Éléments existants »), Renommer, Supprimer, Propriétés ; sur le fond — **Nouveau ▸** (Dossier, Raccourci… (boîte de saisie), Document texte, Document Markdown, puis les modèles ShellNew du registre), Coller, **Trier par ▸**, **Afficher ▸** (cases cochées), Actualiser, terminal, Propriétés. Un clic droit sur un élément non sélectionné le sélectionne ; le nouvel élément créé s'ouvre en édition de nom. Sous-menus : option `sub` de `CdxFrame.menu` (charte).
- **« Plus d'options… »** (`ShellMenu.cs`) : le vrai menu contextuel de Windows (tout ce que d'autres logiciels y ajoutent : 7-Zip, Git, partage...) pour les éléments sélectionnés (même dossier), au pointeur ; Maj + clic droit ou Maj+F10 pour le menu étendu. Windows le construit (`IContextMenu` via l'`IShellFolder` du parent) et exécute lui-même la commande ; les sous-menus dessinés par leur logiciel reçoivent leurs messages par `ExplorerForm.WndProc`. Le menu est construit pour de bon dans l'auto-test (`ShellMenu.Probe`) mais **jamais affiché ni cliqué** par un essai.
- **Recherche dans les sous-dossiers** (`Search.cs`, `js/search.js`) : Ctrl+E ou le bouton « Partout » ouvre une barre de critères — mots du nom (tous doivent s'y trouver ; `*` et `?` sont des jokers), type (dossiers, documents, images, audio, vidéos, archives, code, programmes), date de modification (aujourd'hui… 12 mois), taille (< 1 Mo … > 1 Go). Le programme parcourt en arrière-plan (liens non suivis, cachés selon le réglage, 20 000 résultats au plus) et envoie par paquets ; le dossier de chaque résultat est en grisé après son nom ; clic droit > « Ouvrir l'emplacement du dossier » ; Échap ou « Fermer » revient au dossier ; F5 relance. Sans dossier (« Ce PC »), tous les disques fixes.
- **Sélection** : rectangle tiré depuis un espace vide (Ctrl : s'ajoute ; la liste défile près des bords), menu « Sélection » (tout, rien, inverser), saisie pour sauter (« be » → premier nom en « be », « aaa » cycle, oubli après 1 s), curseur au clavier (↑↓, Maj pour étendre, Début, Fin, Page haut/bas).
- **Raccourcis** : une table (`js/keys.js`) sert à la fois au clavier et à l'aide **F1** : Alt+Entrée propriétés, Ctrl+Maj+C copier le chemin, Ctrl+E/F/F3 rechercher, Alt+D/F4/Ctrl+L adresse, Ctrl+N nouvelle fenêtre, Ctrl+Tab onglets, Ctrl+H éléments cachés, Ctrl+Maj+I inverser, Alt+P aperçu, etc. Aucun raccourci n'agit quand une boîte ou un menu est ouvert.
- **Très grands dossiers** : lecture par paquets de 4000 (`fs.list` puis `fs.list.more`), liste virtualisée (32 lignes dans le DOM pour 100 000 éléments), tri et filtre en cache ; `tests\perf.ps1` (100 000 fichiers : premières lignes 45 ms, lecture complète 0,5 s).
- **Liste en arrière-plan** : `fs.list` ne bloque plus le fil d'interface ; une nouvelle demande pour le même onglet annule la précédente.
- **Corbeille** (`Recycle.cs`) : la vraie corbeille de Windows, lue directement (`$Recycle.Bin\<SID>\$I…` + `$R…`, versions 1 et 2), sans boîte de dialogue. Ligne « Corbeille » dans la barre latérale (lieu `::corbeille`, colonne « Supprimé le », nom d'origine et dossier d'origine) ; **Restaurer** (Entrée ou menu : jamais d'écrasement, le dossier d'origine est recréé), **Supprimer définitivement** (Suppr dans la corbeille, avec confirmation), **Vider la corbeille** (menu du fond, avec confirmation). **Maj+Suppr** sur un fichier ordinaire = suppression définitive après confirmation. Déposer des fichiers sur « Corbeille » les y met. Les identifiants envoyés par la page ne sont acceptés que s'ils désignent un « $I » du dossier de l'utilisateur.
- **Annuler / rétablir** (`Undo.cs`) : une pile de 50 gestes par fenêtre — renommer, nouveau dossier/fichier/raccourci, déplacer, copier, supprimer (vers la corbeille), restaurer. **Ctrl+Z** annule, **Ctrl+Y** (ou Ctrl+Maj+Z) rétablit ; le menu du fond dit « Annuler Renommer ». Une annulation ne s'écrase jamais sur un nom pris (message, geste retiré) ; une suppression définitive n'est pas annulable.

## L'Explorateur, lots 5 à 8 (31/10/2026)
- **Réseau et amovibles** (`Fs.cs`) : chemins `\\serveur\partage` (le partage a pour parent le serveur, le serveur « Ce PC »), la racine d'un serveur liste ses partages (`NetShareEnum`), les disques ont un type (fixe, amovible, réseau, DVD) ; section « Réseau » de la barre latérale (lecteurs réseau + serveurs retenus, réglage `net.servers`, « Oublier ce serveur ») ; **Éjecter** une clé ou un DVD (verrouillage, démontage, éjection ; refusé si un fichier y est ouvert).
- **Vues** (`js/views.js`) : Détails, Liste, petites / moyennes / grandes icônes avec **vignettes** des images (servies par `x.drive.cdx.shell`), **regroupement** par type, date ou taille (en-têtes), **colonnes** masquables (Modifié le, Type, Taille) ; vue et groupe gardés **par dossier** (réglage `views`), la dernière vue choisie devient celle des dossiers jamais réglés (`view.default`) ; Ctrl+Maj+1/2/3/5/6. La mise en page est calculée (position de chaque élément) : la liste reste virtualisée, le rectangle de sélection et les flèches suivent la vue.
- **Onglets** (`js/tabs.js`) : réordonner en glissant, dupliquer, fermer les autres / ceux de droite, rouvrir le dernier fermé (Ctrl+Maj+T), détacher dans une nouvelle fenêtre (lâcher l'onglet hors de la fenêtre), Ctrl+1…9 ; la **session** (onglets, onglet actif) est gardée (réglage `session`) et retrouvée par une fenêtre ouverte sans dossier (`&restore=1`, seulement si aucune autre n'est ouverte). **Accès rapide** : dossiers épinglés (`pins`, Ctrl+Maj+P, menus), fréquents et récents (`recent`) en tête de la barre latérale.
- **Aperçus étendus** (`preview.js`, `Fs.OfficePreview`) : Markdown rendu (moteur des Notes, `md.js`), code coloré (commentaires, chaînes, nombres, mots-clés, balises ; familles JS/TS, C#, Python, Rust, PowerShell, Go, Java/Kotlin, C/C++, SQL, shell, batch, HTML/XML, YAML/TOML/INI), **documents Office** : texte des paragraphes d'un `.docx`, cellules de la première feuille d'un `.xlsx`, textes des diapositives d'un `.pptx` (lus dans le zip, sans Office).
- Essais : `tests\explorer.ps1` 177 vérifications, `--selftest-fs` 133.

## Glisser-déposer de l'Explorateur
- **Dans la fenêtre** : une ligne (ou la sélection) se dépose sur un dossier de la liste, un lieu de la barre latérale ou un onglet. Comme dans Windows : sur le même disque on déplace, d'un disque à l'autre on copie ;
  **Ctrl** copie, **Maj** déplace, **Échap** annule. Une ligne fantôme suit le pointeur et dit ce qui va se passer (« Déplacer vers « X » »), la cible s'éclaire, la liste défile près de ses bords.
  Refusés : un dossier sur lui-même ou dans l'un de ses sous-dossiers, et déplacer dans le dossier d'origine (ne fait rien).
  **Un nom déjà pris** dans la destination ouvre une boîte de la fenêtre (« Garder les deux », « Remplacer », « Ignorer », « Annuler ») : rien n'est touché avant la réponse. Les boîtes de conflit de Windows
  ne sont pas utilisées pour les glissés : sans fenêtre parente, l'une d'elles est restée cachée derrière les autres et a bloqué l'Explorateur (30/10/2026).
- **Depuis une autre application ou une autre fenêtre de l'Explorateur** : les fichiers se déposent sur la liste (le dossier courant), un dossier, un lieu ou un onglet ; comme dans Windows, déplacés sur le même disque,
  copiés d'un disque à l'autre ; Ctrl copie, Maj déplace.
  Les chemins viennent de WebView2 (`postMessageWithAdditionalObjects`, `HostForm.DroppedPaths`).
- **Vers une autre application** : quand le pointeur quitte la fenêtre avec des éléments saisis, la page envoie `fs.dragout` et Windows prend le relais (`DoDragDrop`, fichiers réels) ; la liste est relue ensuite.
- **Essais** : `cdx-explorer --selftest-fs` (déplacer, copier, refus, noms uniques) et `tests\dnd.ps1` (la page pilotée par des événements de pointeur fabriqués, jamais la vraie souris ; conflits de noms compris). **Pas essayés automatiquement** :
  le glisser depuis et vers une autre application (il demande un vrai glisser de Windows) ; à essayer à la main.

## Synergies (phase 4)
- **Notifications** : `notify` sur le bus du shell → une cloche et le nombre de notifications non vues dans la barre (place fixe), la liste dans le centre rapide (50 au plus, « Effacer »).
  Émetteurs : KIRC (messages privés, mentions), cdx-browser (téléchargement terminé ou échoué), AudioLinker (erreurs et avertissements).
- **Liens** : un lien cliqué dans KIRC s'ouvre dans cdx-browser (lancé au besoin d'après `apps.json`, sinon navigateur de Windows).
- **Fichiers** : le double-clic sur une page HTML, un PDF, une image, un son ou une vidéo dans l'Explorateur les ouvre dans le navigateur (`open-file`) ; « Afficher dans le dossier » d'un téléchargement ouvre l'Explorateur sur le fichier (`select`).
- **Son** : le groupe d'AudioLinker (volume, coupure, setups) est dans la barre (voir ci-dessous).
- Essai de bout en bout isolé : `tests\bus.ps1` (sections 6 et 7).

## Liaison avec AudioLinker
Le shell se connecte au tube `\\.\pipe\cdx.audiolinker` (voir AudioLinker `src/CdxLink.cpp`) et affiche dans la barre le groupe (voyant, coupure, volume, setups) tant
qu'AudioLinker tourne sur la machine.

## Notes (`cdx-notes`, lots 1 à 6)
Le coffre (`Cdx.Vault`, dossier `Documents\Notes CDX` par défaut) est partagé avec le serveur MCP `cdx-notes-mcp` de Claude ; l'application voit tout changement du dossier. Page `web/notes/` :
`core.js` (index, liens, boîtes), `tree.js` (arborescence, filtre, glisser-déposer), `doc.js` (onglets, éditeur, aperçu, enregistrement, conflits), `nav.js` (**volet d'information** : plan de la note, liens sortants et rétroliens avec ligne et contexte, étiquettes du coffre ; **recherche dans le texte** par Entrée dans le filtre ou `?` devant,
avec `"phrase"`, `tag:`, `in:`, `type:` ; **sélecteur Ctrl+P** qui crée la note si elle n'existe pas ; **autocomplétion des `[[liens]]`** dans l'éditeur), `projects.js` (**vue Projets** : fiches `type: projet` filtrées par statut, tâches ouvertes de tout le coffre cochables depuis la vue, nouveau projet depuis le modèle, menu statut/priorité, dossier du dépôt dans l'Explorateur, Dépôts),
`graph.js` (**graphe** des liens sur un canevas : disposition par forces, survol, glisser, molette, clic = ouvrir), `md.js` (moteur Markdown maison, `tests/md.test.mjs`). Essai : `tests\notes.ps1` (72 vérifications, dont Claude qui écrit par le MCP pendant que l'application est ouverte).

## Installeur (phase 5)
`pwsh -File installer\build.ps1 [-Bin <dossier des .exe>] [-Sign]` construit `installer\Output\CDX-Setup-<version>.exe` (Inno Setup 6, français) et son `.sha256`. La version vit dans
`Directory.Build.props` (les huit exécutables la portent). Par utilisateur par défaut (`%LOCALAPPDATA%\Programs\CDX`, aucun droit d'administrateur), ou pour tous ; **un composant par programme**
(barre, Explorateur, navigateur, Dépôts, Notes + MCP, Terminal) ; tâche « lancer la barre à l'ouverture de session » (clé `Run`, décochée par défaut) ; raccourcis dans Démarrer > CDX, dont
« Rendre la barre de Windows » (`cdx-shell.exe --restore`). Prérequis téléchargés et installés s'ils manquent : .NET 8 Desktop et WebView2. Avant d'installer ou de désinstaller, les programmes
en cours sont arrêtés et la barre de Windows rendue ; `/NOSTOP=1` (essais) n'y touche pas. La désinstallation propose de supprimer les réglages, jamais le coffre de notes.
Signature : comme AudioLinker (`-Sign` + `CDX_SIGN_ARGS`, `installer\sign.cmd`), aucun certificat pour l'instant (SmartScreen avertit). La CI construit l'installeur, l'installe en silence,
réinstalle par-dessus, désinstalle, et le publie en artefact.

## Pas encore
Icône propre de la suite (les fenêtres et l'installeur ont celle par défaut), signature, autostart par défaut (la tâche existe, décochée).
Les anciens fichiers sous `KIRC\charte\windows\` ne sont pas supprimés.
