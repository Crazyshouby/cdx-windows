# cdx-windows : l'environnement CDX pour Windows, en trois programmes

La charte CDX-UI (dépôt `cdx-ui`) appliquée à Windows : la barre des tâches et le menu Démarrer à la place de ceux de Windows, un Explorateur de fichiers et
un navigateur web. Chaque page est du HTML/CSS de la charte servi par WebView2 ; les programmes .NET 8 ne font que le lien avec Windows.

```
pwsh -File build.ps1               # compile les trois programmes dans bin\
pwsh -File lancer.ps1 [thème]      # compile si besoin, lance le shell (un des seize thèmes de cdx-ui/tokens.json : rouge, couchant, ambre, jean, marine, tramway, sarcelle, chocolat, lavande, oxygene, techno, reve-bleu, reve-vert, reve-violet, lagon, turquoise)
pwsh -File restaurer.ps1           # arrête le shell et rend sa barre à Windows
```

| Programme | Rôle | Instance | Données |
|---|---|---|---|
| `bin\cdx-shell.exe` | la barre, le menu Démarrer, le centre rapide, Alt+Tab, la touche Windows, la liaison avec AudioLinker | une | `journal.log` |
| `bin\cdx-explorer.exe [--theme t] [dossier]` | l'Explorateur : une fenêtre par dossier ouvert, s'arrête avec la dernière ; **glisser-déposer** (voir ci-dessous) | une (un second lancement ouvre une fenêtre dans la première) | `journal-explorer.log` |
| `bin\cdx-browser.exe [--theme t] [adresse]` | le navigateur : onglets, groupes d'onglets, pages scindées, plusieurs fenêtres, session retrouvée, extensions (antipub), réglages, import depuis Chrome (voir « Navigateur, lot 4 ») | une (un second lancement ouvre un onglet) | `journal-browser.log`, profil `navigateur\` |
| `bin\cdx-repos.exe [--theme t]` | **Dépôts** : tous les projets git du PC (ce qui reste à enregistrer ou à envoyer sur GitHub), en lecture seule | une (un second lancement passe devant) | `journal-repos.log`, `repos.json` (dossiers explorés, projets masqués) |
| `bin\cdx-agenda.exe [--theme t]` | **Agenda** : calendrier et tâches du compte Google relié à la suite (voir « Agenda et brique Google ») | une (un second lancement passe devant) | `journal-agenda.log` |
| `bin\cdx-mail.exe [--theme t]` | **Courrier** : la boîte Gmail du compte Google relié à la suite (voir « Courrier ») | une (un second lancement passe devant) | `journal-mail.log` |
| `bin\cdx-pilote.exe [--theme t]` | **Pilote** : le chat avec Claude réservé à l'organisation et à la création des projets ; Claude Code en sous-processus, la suite en outils (voir « Pilote ») | une (un second lancement passe devant) | `journal-pilote.log`, `pilote.json`, `pilote\<conversation>.json` |
| `bin\cdx-terminal.exe [--theme t] [dossier]` | **Terminal** : un shell (PowerShell 7 s'il est installé, sinon Windows PowerShell) dans une fenêtre de la charte : cadre CDX, fond noir, texte à la couleur d'accent, aucun coin arrondi. Pseudo-console Windows (ConPTY) côté C#, xterm.js dans la page. **Onglets** (Ctrl+Maj+T / W, Ctrl+Tab, Ctrl+Alt+1..9), **volets** côte à côte ou l'un sous l'autre (Alt+Maj+D, menu clic droit, séparateurs déplaçables, Alt+flèches), un shell par volet ; police pixel à chasse fixe de la charte (KIRC Pixel Mono, case de 7 px), texte dessiné par WebGL. Lancé avec un dossier (ou, s'il tourne déjà, un second lancement) : un onglet dans ce dossier. **Win+T** l'ouvre depuis le shell ; la tuile « Terminal CDX » du menu Démarrer le lance ; « Ouvrir dans un terminal » de l'Explorateur l'utilise (Windows Terminal puis cmd en repli) | une (un second lancement passe devant) | `journal-terminal.log` |

Le shell lance les autres (`cdx:explorer`, `cdx:navigateur`, `cdx:repos`, `cdx:terminal`, Win+E, Win+T) : **un plantage de l'un n'emporte pas les autres** (essai `tests\suite.ps1`).
Toutes les données sont dans `%LOCALAPPDATA%\CdxShell\`.

## Organisation

```
src\Cdx.Host\       la bibliothèque commune : fenêtre hôte WebView2, appels Windows, journal, instance unique et relais, canal de test,
                    boutons latéraux de la souris, aimantation des fenêtres (types internes, visibles des trois programmes seulement)
src\Cdx.Shell\      cdx-shell : Program.cs (barre, Démarrer, centre, Alt+Tab, crochet clavier), AudioLink.cs, Sys.cs
src\Cdx.Explorer\   cdx-explorer : Fs.cs (fichiers), ExplorerForm.cs
src\Cdx.Browser\    cdx-browser : Browser.cs (onglets, favoris, historique, téléchargements)
src\Cdx.Repos\      cdx-repos : Git.cs (recherche et lecture de git, jamais d'écriture), ReposApp.cs
src\Cdx.Pilote\     cdx-pilote : Claude.cs (claude.exe en sous-processus, stream-json), PilotTools.cs (les outils de la suite, tube cdx.pilote.outils), PiloteApp.cs (conversations, accords, page) ; src\Cdx.PiloteMcp\ cdx-pilote-mcp (le relais MCP « cdx-suite ») ; dans Cdx.Host : ClaudeFlux.cs (l'analyseur du flux), PilotPolicy.cs (la politique d'autorisation)
src\Cdx.Terminal\   cdx-terminal : ConPty.cs (la pseudo-console : lancer le shell, faire passer les octets, redimensionner), TerminalApp.cs (fenêtre, une session par volet, messages de la page, presse-papiers)
web\terminal\       la page du terminal (terminal.html, terminal.css, terminal.js : onglets, arbre de volets) ; fonts\ = KIRC Pixel Mono (générée par cdx-ui\gen\gen_font_mono.py, copie à refaire si la police change) ; vendor\ = xterm.js 6.0.0, addon-fit 0.11.0 et addon-webgl 0.19.0 (MIT, copiés tels quels ; exceptions du lint dans charte-lint.allow)
web\cdx\            la charte (copie exacte de cdx-ui/dist : node tools/charte-sync.mjs --write cdx-windows) et host.js, le pont des pages
web\shell\ web\explorer\ web\browser\ web\repos\   les pages de chaque programme (les _test-*.html servent aux auto-tests)
couche1\            le cadre des fenêtres de Windows aux couleurs de la charte (DWM), réversible
tests\              pilote.ps1 (Pilote contre fake_claude.py et fake_gmail.py : 41 vérifications), navigateur.ps1 (52 vérifications), suite.ps1 (lancement, relais, isolation), bus.ps1 (bus et accent, isolé), repos.ps1 (Dépôts : 21 vérifications sur des dépôts jetables), explorer.ps1 (l'Explorateur : liste, réglages, surveillance, sélection, raccourcis, menus, recherche, corbeille, annuler : 134 vérifications), dnd.ps1 (glisser-déposer de l'Explorateur : 31 vérifications, dans la vraie fenêtre isolée)
```

## Essais (aucune entrée simulée)

Les essais `tests\*.ps1` se lancent avec PowerShell 7 (`pwsh`, ou la console PowerShell de Claude Code) ; ils portent une marque d'ordre d'octets UTF-8 depuis le 01/11/2026 pour que
Windows PowerShell 5.1 lise aussi leurs accents (sans elle, « écris » partait en « Ã©cris » et quatorze vérifications de Pilote échouaient sans que la ligne ÉCHEC soit repérable).

- Auto-tests, sans fenêtre visible : `cdx-explorer --selftest-fs`, `--selftest-web` ; `cdx-shell --selftest-sys`, `--selftest-audio`, `--selftest-snap` (19 vérifications de l'aimantation), `--selftest-spell` (24 vérifications du correcteur orthographique) ; `cdx-browser --selftest-browser` ; `cdx-repos --selftest-git` (22 vérifications sur de vrais dépôts jetables) ; `cdx-terminal --selftest-pty` (10 vérifications de la pseudo-console : sortie, code de sortie, UTF-8, redimensionnement, arrêt) ; `tests\terminal.ps1 -Bin <dossier>` (43 vérifications dans la vraie fenêtre isolée : shells, police et WebGL, couleurs de la charte, accent, onglets, volets, raccourcis, redimensionnement, titre, fermeture, dossier de départ, et avec `-ExplorerBin` l'Explorateur qui ouvre un onglet puis relance le terminal).
  Résultats dans `%LOCALAPPDATA%\CdxShell\selftest*.txt`.
- Canal de contrôle : si `%LOCALAPPDATA%\CdxShell\debug.txt` existe au lancement, chaque programme ouvre un tube réservé à l'utilisateur courant
  (`cdx.shell.ctl`, `cdx.explorer.ctl`, `cdx.browser.ctl`, `cdx.repos.ctl`) pour lire son état et exécuter du JavaScript dans ses pages. `tests\navigateur.ps1` et
  `tests\suite.ps1` s'en servent ; supprimer `debug.txt` et relancer ensuite.

## Aimantation des fenêtres (01/11/2026)
Toutes les fenêtres de l'hôte C# (`Cdx.Host/Snap.cs`, `HostForm.WndProc`) : pendant un déplacement ou un redimensionnement, la fenêtre colle aux bords des autres
fenêtres de la suite (`cdx-*.exe`, KIRC, AudioLinker) qui lui font face, et aux bords de la zone de travail de son écran. Règles de la charte (`tokens.json`) : 10 px
d'attraction, 2 px d'écart entre deux fenêtres, aucun au bord de l'écran, à l'échelle de l'écran ; mêmes règles que KIRC (`app.rs`). Windows fait le geste
(`WM_NCLBUTTONDOWN`), l'hôte corrige le rectangle proposé par `WM_MOVING` / `WM_SIZING` ; les voisines sont relevées à l'entrée du geste. Essai : `cdx-shell --selftest-snap`.

## Réglages communs et bus (phase 2)
- **Accent de la suite** : `%APPDATA%\CDX\suite.json` (`{"version":1,"accent":"bleu"}`). Chaque programme le relit à chaud (250 ms) et recolore ses pages ; `cdx-shell.exe --accent bleu` l'écrit.
  Sans argument, les programmes le suivent ; `--theme <nom>` (ou `lancer.ps1 <nom>`) fixe un thème pour ce lancement seulement.
- **Bus** : un tube `\\.\pipe\cdx.<nom>` par programme (`Bus.cs`), messages `{type, from, data}` validés par liste blanche : `notify` (shell), `open-path` et `select` (Explorateur),
  `open-url` (navigateur, http et https seulement), `show` (Dépôts). Lancer `cdx-browser.exe <adresse>` alors qu'il tourne déjà passe par ce bus. Spécification : `cdx-ui/docs/SUITE.md`.
- Essais : `cdx-shell --selftest-bus` (20 vérifications), `tests\bus.ps1` (essai de bout en bout isolé : ni le vrai bus, ni le vrai `suite.json`, ni le vrai profil du navigateur).

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

## Agenda et brique Google (lot 5, 01/11/2026)
`src\Cdx.Host\Google.cs` (**GoogleAuth**) : le compte Google de la suite, relié une fois (OAuth 2.0 « application de bureau », PKCE, redirection sur 127.0.0.1, page de
consentement ouverte dans le navigateur CDX), jeton gardé chiffré DPAPI dans `%APPDATA%\CDX\google-token.bin` (`Dpapi.cs`), rafraîchi tout seul, `ApiAsync` pour les API (401 → rafraîchi
une fois, message d'erreur de Google remonté). L'identifiant client vient de `%APPDATA%\CDX\google-client.json` (le `client_secret_….json` de la console Google Cloud, jamais commité) ;
les portées se cumulent (une nouvelle refait passer par le consentement). `CDX_GOOGLE_API` remplace googleapis.com pour les essais.
`src\Cdx.Agenda\` (**cdx-agenda**, neuvième programme, tuile « Agenda », `cdx:agenda`) : mois, semaine, liste des 31 jours ; événements de tous les agendas (masquables par le menu
Réglages, `agenda.json`), fiche, création (bouton, Ctrl+N, double-clic sur un jour), modification, suppression (agendas en écriture seulement) ; tâches Google par liste (ajouter,
cocher, renommer, échéance, supprimer, nouvelle liste) ; cache `agenda-cache.json` montré hors ligne. Essai `tests\agenda.ps1` (22 vérifications) contre `tests\fake_google.py`
(OAuth, Calendar et Tasks en mémoire) : rien ne touche au vrai compte.

## Pilote (01/11/2026)
`src\Cdx.Pilote\` (**cdx-pilote**, onzième programme, tuile « Pilote », `cdx:pilote`) : un chat avec Claude, réservé à l'organisation et à la création des projets (décisions du 01/11/2026 :
Claude Code en sous-processus plutôt que l'API ; un projet = fiche dans Notes + dossier dans `Dev\` + dépôt git ; lecture libre, irréversible confirmé ; nom « Pilote »).
- **Le cerveau** : `Claude.cs` lance `claude.exe -p` en stream-json dans les deux sens (le canal mis au point pour Tennis), un processus persistant par conversation, repris après un
  redémarrage par `--resume` (la session garde tout son contexte) ; `--permission-prompt-tool stdio` fait passer les demandes d'accord par ce même flux (forme relevée en vrai, voir
  `Cdx.Host\ClaudeFlux.cs`, l'analyseur : texte au fil de l'eau, outils, résultats, demandes d'accord, quota de l'abonnement, fin de tour). Facturé sur l'abonnement de Valentin, aucune clé API.
  `claude.exe` est cherché dans `CDX_CLAUDE_EXE`, puis `~\.local\bin`, puis le PATH ; `CDX_CLAUDE_ARGS` (essais) place des arguments devant.
- **Les outils** : `PilotTools.cs` tient le tube `cdx.pilote.outils` ; `src\Cdx.PiloteMcp\` (**cdx-pilote-mcp**, serveur MCP « cdx-suite ») n'est qu'un relais JSON-RPC vers ce tube, lancé par
  Claude Code d'après le `--mcp-config` que Pilote écrit (`pilote-mcp.json` : cdx-suite et cdx-notes, `--strict-mcp-config`). Outils : `suite_state`, `app_open`, `app_state`, `app_command`,
  `browser_tabs/open/navigate/read/find/click/fill/back/scroll/screenshot`, `mail_search`, `mail_read` (le courrier, lu par Courrier sans rien marquer), `agenda_events`, `agenda_tasks` (par Agenda), `notify`, `project_folder`. Les programmes sont commandés par leur **canal de pilotage** `cdx.<nom>.pilot`
  (`Host.StartPilotPipe`, toujours ouvert, utilisateur courant : `state`, `cmd <json>` et, pour le navigateur, `page.text/find/click/fill/scroll/shot`, des scripts fixes sur l'onglet actif ; Courrier : `search`, `thread` ; Agenda : `events`, `tasks` ;
  jamais `eval`, réservé au canal de test). Un programme absent est lancé.
- **Les accords** : `Cdx.Host\PilotPolicy.cs` décide seul ce qui peut l'être selon le niveau (Réglages > Autonomie : libre, prudent, auto) : lectures et outils de lecture toujours ; écritures dans
  `Dev\` et le coffre, commandes sûres (git status/add/commit, npm install, dotnet build…), outils réversibles de la suite au niveau libre ; le reste (et toujours : git push, suppressions,
  envoi de courrier, `notes_trash`) s'affiche dans le volet **Actions** avec Autoriser / Refuser (ou Refuser… avec un motif que Claude lit). Les questions à choix de Claude (AskUserQuestion)
  s'y répondent aussi. Les outils de lecture sont en plus donnés par `--allowedTools`.
- **La mémoire** (`Memory.cs`, décisions du 01/11/2026) : des notes du coffre, dossier `Pilote\` (`Profil.md`, `Décisions.md`, `Projets.md`, `Journal.md`, créées au premier lancement), injectées bornées
  dans la consigne à chaque lancement de Claude (`--system-prompt-snapshot off` : une session reprise relit la mémoire du jour) ; Claude y écrit lui-même par les outils de Notes (faits durables dès qu'il les
  apprend, une entrée datée Demandé / Fait / En suspens dans le journal à la fin d'un échange utile). Tout se lit et se corrige dans Notes (Réglages > Mémoire de Pilote) ; la mémoire
  automatique de Claude Code est coupée pour Pilote (`CLAUDE_CODE_DISABLE_AUTO_MEMORY`).
- **La page** `web\pilote\pilote.html` : conversations à gauche (clic droit : renommer, supprimer), le fil au centre (Markdown rendu par `web\notes\js\md.js`, texte en direct, appels d'outils
  avec leur état), la saisie (Entrée envoie, Maj+Entrée à la ligne, Échap ou « Arrêter » interrompt), le volet des actions à droite ; ligne d'état : quota de l'abonnement, contexte occupé.
  Réglages : autonomie, modèle, effort (pris au message suivant, `--resume`). Une demande d'accord faite alors que la fenêtre n'a pas le focus est signalée à la barre.
- **Essais** : `cdx-shell --selftest-pilote` (36 vérifications : l'analyseur sur des lignes réelles de Claude Code 2.1.316, la politique) ; `tests\pilote.ps1` (41 vérifications contre
  `tests\fake_claude.py`, un faux `claude -p` qui lance les vrais serveurs MCP et rejoue les scénarios : outils, navigateur piloté, accords automatiques et demandés, refus, question à choix,
  commande d'un programme, création d'un projet dans un coffre jetable, lecture du courrier contre un faux Gmail, mémoire dans le coffre relue après reprise, redémarrage et reprise, arrêt). Rien ne touche au vrai Claude Code ni aux vrais programmes.

## Icônes d'application (01/11/2026)

Décision du 01/11/2026 : une icône pixel par application, d'une même famille, sans retouche des icônes existantes ; dessins approuvés par Valentin.
- **La famille** vit dans la charte (cdx-ui `gen/pixel_icons.py`, dictionnaire `APPS`) : dix pictogrammes 16 × 16, chacun dans les couleurs de son objet (choix de Valentin :
  pas le même rouge partout), avec la lumière de l'icône de KIRC (arête claire en haut et à gauche, ombre en bas et à droite, contour noir). Explorateur : le dossier jaune existant ;
  Navigateur : globe bleu et vert ; Dépôts : classeur gris ; Notes : carnet à spirale blanc ; Terminal : écran noir dans un cadre gris, invite verte ; Agenda : page de calendrier au bandeau
  rouge ; Courrier : enveloppe beige ; Pilote : volant noir au klaxon rouge ; Transferts : flèches verte et bleue ; CDX : le carré rouge de l'accent et son C.
- **Dans les pages** : les mêmes noms dans `icons.js` (`pix('notes')`, `data-ic="pilote"`…) ; les épingles de la barre et les tuiles du Démarrer (`Pins.cs` : une épingle d'une application
  de la suite prend son icône de la famille même si `epingles.json` a été écrit avec un pictogramme générique), les notifications du centre (l'icône du programme qui notifie), le fil de Pilote
  (l'icône du programme visé par l'outil, sinon la nature de l'outil). Alt+Tab et la liste des tâches montrent l'icône de la fenêtre, donc celle de l'exécutable.
- **Les exécutables** : `icons\<nom>.ico` (16, 32, 48 et 256 px, images PNG dans l'ICO, rouge fixe), produits par `python gen/gen_app_icons.py ..\cdx-windows\icons` dans cdx-ui et
  versionnés ici ; `<ApplicationIcon>` dans les dix projets, `SetupIconFile` de l'installeur (`cdx.ico`). `HostForm` prend l'icône de son exécutable pour la fenêtre (Alt+Tab, liste
  des tâches de la barre).
- **Reste** (même famille, à la pièce) : les boutons du navigateur, la mise en forme de Notes, les états des transferts, l'Explorateur.

## Correcteur orthographique et grammatical (01/11/2026)

Décision du 01/11/2026 : un correcteur à nous plutôt que celui de Chromium. Celui de WebView2 s'appuie sur le correcteur de Windows et sa langue suit celle de
l'environnement, pas le texte (bogue WebView2 n° 5294, suivi chez Microsoft) ; surtout, avec nos menus (AreDefaultContextMenusEnabled = false) l'événement
ContextMenuRequested n'est jamais levé (c'est écrit dans sa spécification), donc ses suggestions n'atteignent jamais la page. Vérifié par un programme d'essai le 01/11/2026.
- **Le moteur** (`Cdx.Host/Spell.cs`) : Hunspell en C# pur (paquet WeCantSpell.Hunspell 7.0.1) et le dictionnaire français de Dicollecte 7.7 (`web\dict\fr` : `fr.aff`, `fr.dic`,
  `README_dict_fr.txt`, licence MPL 2.0 ; le dictionnaire de LibreOffice et de Firefox, pris dans le dépôt LibreOffice/dictionaries). Chargé en arrière-plan au premier mot (270 ms),
  partagé par les fenêtres d'un programme. Hunspell connaît toutes les formes conjuguées ; il ne lit pas la phrase (les accords sont de la grammaire, pas de l'orthographe).
  L'hôte ajoute les élisions (« l'école », « qu'il », apostrophe droite ou typographique) et les mots composés par trait d'union dont chaque partie existe. Les mots ajoutés vont dans
  `dictionnaire-perso.txt` du dossier des données (une ligne par mot) ; `CDX_DICT_DIR` désigne un autre dossier de dictionnaire (essais).
- **Le contrat avec les pages** (servi par `HostForm` pour tous les programmes, avant l'application) : `sp.check {id, words}` → `sp.result {id, bad}` ; `sp.suggest {id, word}` →
  `sp.suggestions {id, word, list}` ; `sp.add {word}` → `sp.added {word}` ; `sp.off` quand il n'y a pas de dictionnaire.
- **La page** (`web\cdx\cdx-spell.js`, composant de la charte, canonique dans cdx-ui) : un calque posé sous la zone de texte (même police, même largeur utile, mêmes retours à la ligne,
  texte transparent) porte les mots inconnus soulignés en rouge ondulé ; la zone de texte devient transparente et défile avec lui. Jamais vérifiés : adresses, mots avec chiffres, sigles,
  lettres seules, le mot en cours de frappe, et avec l'option `markdown` le code, les liens `[[…]]` et `[…](…)`, les #étiquettes. Clic droit sur un mot souligné : les suggestions
  (huit au plus), « Ajouter au dictionnaire », « Ignorer ici », puis Annuler / Rétablir / Couper / Copier / Tout sélectionner (ces actions seules ailleurs dans la zone).
  Un mot choisi remplace l'autre par `insertText` (l'historique Annuler est gardé).
- **La correction automatique** (QCM du 01/11/2026 : orthographe à la fin du mot ; seulement si la suggestion est sûre ; Ctrl+Z et un rappel visible) : à l'espace, à la ponctuation
  ou au retour à la ligne, le mot qui précède est vérifié (`sp.suggest`, qui dit aussi s'il est connu) ; s'il est inconnu et qu'une seule candidate est à une lettre d'écart (ou, dès six
  lettres, une seule à deux lettres : « orthografe » → « orthographe »), elle le remplace par `insertText`, le curseur reste où il était. « marchée » (matchée, marchés, marché) n'est pas touché.
  Le mot corrigé reste souligné en pointillé « ok » (`--green`) huit secondes ; clic droit : « Rétablir « … » », « Ajouter « … » au dictionnaire ». Ctrl+Z rend le mot tapé, qui n'est plus
  corrigé dans la fenêtre. Jamais corrigés tout seuls : mots avec apostrophe ou trait d'union, majuscule interne. Réglage « Correction automatique à la fin du mot » (grisé sans le correcteur).
- **La grammaire** (demande du 01/11/2026 : « ajoute ce qu'il faut pour la conjugaison ») : Grammalecte 2.3.0 (Olivier R., GPL 3), le moteur de l'extension Firefox, dans un worker de la
  page (`web\cdx\grammaire-worker.js`, fichiers sous `web\third_party\grammalecte` : moteur, règles, lexique de 4 Mo, conjugaisons ; ni thésaurus ni tests ; le lint de la charte ignore
  `third_party`). Chargé en une à deux secondes à la première zone de texte, hors du fil de la page ; le texte entier (parties masquées en espaces) est vérifié 600 ms après la dernière
  frappe, paragraphe par paragraphe. Options : accords (genre et nombre), conjugaisons, infinitif, impératif, interrogatif, participes passés, modes verbaux, confusions, locutions ;
  pas la typographie ni le style (apostrophes droites, espaces, majuscules… seraient du bruit). Les erreurs sont soulignées en pointillé « attention » (`--warn`) ; clic droit :
  le message de Grammalecte en titre, les suggestions, « Ignorer ici » (règle + mot, le temps de la fenêtre). Grammalecte ne signale qu'une faute à la fois par accord
  (« Les chat mange » : « chat » d'abord ; « mange » une fois « chats » écrit, puisqu'il s'accordait avec le singulier).
- **Où** : l'éditeur de Notes (Réglages > Affichage > Correcteur orthographique, Grammaire), le message de Courrier, la zone de saisie de Pilote et la description d'un événement
  d'Agenda (Réglages > Correcteur orthographique, Correction automatique, Grammaire) ; réglages gardés par programme, en marche par défaut. Les autres programmes n'ont pas de zone
  de texte multiligne (les champs d'une ligne, comme un titre ou un nom, ne sont pas corrigés).
- **Essais** : `cdx-shell --selftest-spell` (24 vérifications : mots, formes fausses, élisions, traits d'union, suggestions, dictionnaire personnel jetable, contrat des pages) et
  `tests/spell.test.html` de cdx-ui (44 vérifications dans Chrome headless contre un faux hôte et un faux worker : découpage, calque, mot du curseur, menus, remplacement, ajout, correction
  automatique, grammaire, « Ignorer ici », arrêt, retrait, sp.off) ; `tests\notes.ps1` vérifie la chaîne entière dans la vraie fenêtre (Hunspell et Grammalecte : soulignement, menu, correction
  automatique, dictionnaire personnel).

## Courrier (lot 5, 01/11/2026)
`src\Cdx.Mail\` (**cdx-mail**, dixième programme, tuile « Courrier », `cdx:courrier`, portée Google `gmail.modify` ajoutée au compte de la suite) : `Gmail.cs` (lecture d'un message de
l'API en forme simple : en-têtes, texte, HTML, pièces jointes ; fabrication d'un message RFC 2822 : UTF-8, pièces jointes en multipart/mixed, en-têtes RFC 2047), `MailApp.cs`
(libellés et compteurs, fils par libellé ou recherche Gmail, fil ouvert marqué lu, archiver / corbeille / spam / non lu / favori, pièces jointes enregistrées dans Téléchargements,
envoi par `/upload/…/messages/send`, relevé toutes les 90 s qui signale un nouveau courrier à la barre, cache `courrier-cache.json`). Page `web\mail\courrier.html` : libellés,
fils, fil ouvert ; **chaque corps HTML est dans un cadre isolé sans script** (sandbox), **images distantes bloquées** par CSP jusqu'au clic « Afficher les images », liens ouverts
dans le navigateur CDX ; écrire (Ctrl+N), répondre (R), répondre à tous (Maj+R), transférer (avec les pièces jointes relues chez Google), pièces jointes par la boîte de Windows.
Essai `tests\courrier.ps1` (29 vérifications) contre `tests\fake_gmail.py` (OAuth, libellés, fils, envoi relu, pièces jointes, courrier injecté).

## Navigateur, lot 4 (01/11/2026)
`src\Cdx.Browser\` : `Browser.cs` (une fenêtre : onglets, groupes, pages scindées), `BrowserApp.cs` (les fenêtres, la session, le bus, le canal de test), `BrowserData.cs`
(favoris, historique, téléchargements, réglages, mots de passe, session, extensions, import). Pages : `web\browser\navigateur.html` (le cadre) et `interne.html` (cdx://newtab,
favoris, historique, telechargements, extensions, motsdepasse, reglages).
- **Session** (`session.json`) : fenêtres (position, agrandie), onglets, onglet actif et groupes, réécrite un instant après chaque changement (plus seulement à la fermeture :
  un plantage ne perd plus les onglets) ; retrouvée au lancement, même lancé avec une adresse (elle s'ajoute). Réglage « Rouvrir la dernière session ». **Ctrl+Maj+T** rouvre
  l'onglet fermé (les dix derniers, par fenêtre). Ancien format (liste d'adresses) relu.
- **Plusieurs fenêtres** : Ctrl+N, menu Réglages, « Ouvrir dans une nouvelle fenêtre » (menu de l'onglet : le contrôle WebView2 change de fenêtre, la page n'est pas rechargée).
  Une adresse venue du bus ou d'un second lancement s'ouvre dans la dernière fenêtre active ; le programme s'arrête avec la dernière fenêtre. Canal de test : `use <n>`.
- **Groupes d'onglets** (menu clic droit d'un onglet) : nom, couleur de la charte (accent, rouge, ambre, vert, gris), onglets contigus, pastille cliquable qui replie le groupe
  (un onglet choisi dans un groupe replié le déplie), renommer, couleur, dégrouper, fermer le groupe ; gardés dans la session.
- **Pages scindées** (Ctrl+\, menu) : deux onglets côte à côte sur deux rectangles (`#vpA`, `#vpB`, barre de volet cdx-split) ; cliquer dans une page la rend active (barre d'adresse) ;
  choisir un autre onglet remplace le volet actif ; fermer l'un réunit.
- **Extensions** (`cdx://extensions`, bouton bouclier) : liste du profil (nom, version, active), activer / désactiver, retirer (dossier supprimé), « Réglages » ouvre la page d'options
  de l'extension dans un onglet, « Ajouter un dossier d'extension… » (non empaquetée, copiée sous `extensions\`), **« Installer uBlock Origin Lite »** : dernière archive Chromium
  publiée sur GitHub (uBOL-home), téléchargée, dépliée sous `extensions\uBOLite`, chargée (vérifié : règles DNR actives dans WebView2, pisteurs bloqués). `extensions\index.json` : id → dossier.
- **Réglages** (`cdx://reglages`, `reglages-navigateur.json`) : moteur de recherche de la barre d'adresse (Google, DuckDuckGo, Qwant, Bing, Brave, Startpage), dossier des
  téléchargements et « demander où enregistrer », session au lancement, enregistrement des mots de passe et remplissage automatique (réglages du moteur), effacer les données des sites
  (cookies, cache, stockage ; avec ou sans l'historique).
- **Import** : favoris de Chrome et d'Edge lus dans leurs profils (`Bookmarks`, nom du profil dans `Local State`), ou tout fichier Bookmarks / export HTML (Netscape) ; mots de passe par
  l'export CSV du navigateur d'origine (Chrome : chrome://password-manager/settings), gardés chiffrés DPAPI dans `motsdepasse.bin` (`cdx://motsdepasse` : liste sans les mots de passe,
  retirer), **remplis dans les pages de connexion** du même site (script posé une fois par adresse, champ mot de passe et champ de texte qui le précède). La synchro Google de Chrome
  n'existe pas dans WebView2 : c'est le remplacement. Les mots de passe de la base chiffrée de Chrome ne sont pas lus directement (chiffrement lié à l'application depuis Chrome 127).
- Essai `tests\navigateur.ps1` (52 vérifications, isolé) : une extension de test en archive avec une règle DNR qui bloque un script servi en local, un fichier Bookmarks et un CSV fabriqués,
  une page de connexion locale remplie.

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
`graph.js` (**graphe** des liens sur un canevas : disposition par forces, survol, glisser, molette, clic = ouvrir ; réglage **« Graphe dans une colonne »** : le graphe à droite de l'éditeur, volet redimensionnable, la note active en accent), `md.js` (moteur Markdown maison, `tests/md.test.mjs`).
**Mise en forme** (menu Réglages > Mise en forme, et raccourcis dans l'éditeur, `doc.js` FORMATS) : gras Ctrl+B, italique Ctrl+I, barré Ctrl+Maj+X, code Ctrl+Maj+C, lien Ctrl+K, titres Ctrl+1 à 6 (touche physique, clavier AZERTY compris), paragraphe Ctrl+0, liste Ctrl+L, numérotée Ctrl+Maj+L, case à cocher Ctrl+Entrée (coche et décoche), citation Ctrl+Maj+Q ; sur plusieurs lignes à la fois, et un second appel retire le préfixe. Essai : `tests\notes.ps1` (88 vérifications, dont Claude qui écrit par le MCP pendant que l'application est ouverte).

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
