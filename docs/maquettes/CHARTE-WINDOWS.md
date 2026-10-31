# CDX-UI pour Windows

La charte CDX-UI (`CHARTE.md`) appliquée à **tout ce que Windows affiche** : bureau, barre des tâches, menu Démarrer,
Explorateur, Paramètres, gestionnaire des tâches, centre rapide, Alt+Tab, dialogues, verrouillage, écran d'arrêt.
Les dessins sont dans `windows-ui.html` (douze écrans de 1600 × 900 ; flèches gauche et droite pour passer de l'un à
l'autre, sélecteur d'accent en haut) ; des aperçus PNG sont dans `apercus/`.

Rien ici ne change les règles de `CHARTE.md` : relief éclairé en haut à gauche, gris neutres, aucun arrondi, une
seule couleur d'accent, 14 px sans gras. Ce fichier dit **quoi mettre à la place de quoi**.

## 1. Ce que devient chaque élément de Windows

| Windows | Dans CDX-UI | Composant |
|---|---|---|
| Fenêtre d'application | cadre à quatre lignes + barre de titre 21 px ; fermer à gauche, agrandir et réduire à droite | `.win > .tb + .cb` |
| Fenêtre active / inactive | accent / gris | `.win.active` |
| Barre des tâches | panneau en relief, 36 px, en bas | `.taskbar` (extension) |
| Bouton Démarrer | bouton en relief, icône de quatre carrés rouges | `.tk.start` |
| Application ouverte | bouton en relief ; **active** = enfoncé + barre d'accent de 4 px à gauche ; **attention** = texte ambre | `.tk`, `.tk.on`, `.tk.attn` |
| Zone de notification, horloge | afficheur noir creusé, texte vert | `.tray` (extension) |
| Menu Démarrer, centre rapide, calendrier | fenêtres CDX ancrées au-dessus de la barre | `.win` |
| Bureau | icônes pixel 32 px (2 × 16), libellé blanc à ombre noire ; sélection = gris `#3f3f3f` cerné de `#777` | `.di` |
| Fond d'écran | rampe d'accent assombrie (haut `--a-dark` 45 %, bas 80 %), lueur `--a-mid` en bas, trame de lignes à 1 px | `.wall` |
| Explorateur | onglets CDX, barre de boutons carrés, arbre et liste à filets, en-têtes triables, afficheur d'état | `.tabs`, `.mb`, `.list`, `.lhead`, `.status` |
| Paramètres | liste de rubriques à gauche, groupes gravés à droite, un seul bouton par défaut | `.list`, `fieldset.grp`, `.form` |
| Gestionnaire des tâches | liste à jauges intégrées ; courbes = oscilloscopes noirs à traits verts en escalier | `.gauge`, `.scope` |
| Interrupteur (« activé / désactivé ») | case à cocher | `.switch` |
| Bouton radio | plot octogonal + carré d'accent | `.radio` (extension) |
| Barre de progression | rigole noire creusée, barre d'accent ; ambre = attention | `.gauge` (extension) |
| Notification | boîte noire, cadre vert (fait) ou rouge (problème) | `.toast.ok` |
| Infobulle | boîte noire, filet `#888`, ombre dure | `.tip` (extension) |
| Menu contextuel | mini-cadre, titre au dégradé de la barre de titre | `.menu` |
| Contrôle de compte (UAC) | fenêtre active + bandeau ambre sur noir | `.banner` |
| Boîtes d'erreur et de copie | fenêtre inactive ; icône d'erreur 32 px | `.win` |
| Écran de verrouillage | horloge en police pixel × 8 (112 px), fenêtre de connexion active | `.big` |
| Écran d'arrêt | plein écran `--a-mid`, texte blanc, QR en blanc pur | `.bsod` |
| Alt+Tab | fond assombri, vignettes = petites fenêtres ; la sélectionnée est active | `.win` |

## 2. Extensions à la charte

Cinq composants que Windows impose et que CDX n'a pas. Ils sont dans le `<style>` de `windows-ui.html` (section
« Extensions Windows »), à reporter dans `cdx-ui.css` si le projet se réalise.

- **Barre des tâches** — 36 px, `--panel`, filet noir en haut, relief clair `#666` / `#444` ; boutons de 26 px,
  épinglés carrés de 30 px, séparateurs gravés (filet sombre + filet clair).
- **Jauge** — 14 px (22 px pour une progression de copie) ; barre `--bar`, ambre dégradé pour « attention ». Le rouge
  n'est jamais utilisé : il est déjà l'accent.
- **Bouton radio** — 14 × 14, chanfrein de 2 px, carré de 6 × 6 en `--bar` cerné de noir quand il est choisi.
- **Infobulle** — fond `#000`, filet `#888`, ombre `2px 3px 0 rgba(0,0,0,.5)`, texte blanc.
- **Barre de menus** (`.mb`) — boutons carrés de 22 px au filet noir partagé, comme les en-têtes de colonne.

Tailles de texte : 14 px partout ; 28 px et 112 px seulement pour l'horloge de verrouillage et le résultat de la
calculatrice (multiples entiers, jamais de taille intermédiaire).

## 3. Choix propres à Windows

- **Pas de transparence, pas de flou** (Mica, Acrylic) : tout est plein et plat. L'accent change **tout**, fond
  d'écran compris, via `<html data-theme>` (sept accents, voir `themes.css`).
- **Un seul bouton par défaut** par dialogue (UAC : « Oui » ; erreur : « Réessayer »).
- **Aucune animation** sauf le voyant `pulse` (occupé).
- **Icônes** : 40 icônes 16 × 16 (les 19 de KIRC + 21 ajoutées pour le système : `dossier`, `fichier`, `image`,
  `disque`, `pc`, `wifi`, `volume`, `recherche`, `demarrer`, `musique`, `video`, `telecharger`, `bouclier`, `horloge`,
  `utilisateur`, `palette`, `apps`, `maj`, `avertissement`, `erreur`, `cadenas`, `processeur`). Dessin dans
  `outils/pixel_icons.py`, génération par `outils/gen_assets.py`. Sur le bureau, elles s'affichent à 32 px.
- **Fenêtre enroulée** (`.shaded`) : titre seul, c'est l'état « réduit sur place » de CDX.


## 4. Appliquer le design à Windows : étude (30/10/2026)

Poste visé : Windows 11 26H2, build 26300.9711. **Rien de ce qui suit n'a été appliqué** ; les affirmations sur les
outils viennent de leurs fiches (sources en bas), sauf mention « testé ».

**Le constat.** Windows 11 n'a aucun réglage officiel pour refaire le cadre des fenêtres. Mais ses surfaces ne se
dessinent pas toutes de la même façon, et chacune a sa porte d'entrée :

| Surface | Comment elle est dessinée | Porte d'entrée |
|---|---|---|
| Fenêtres Win32 (Explorateur, dialogues, Exécuter, regedit, mmc, propriétés…) | thème visuel `uxtheme`, ou GDI si le thème est coupé | **thème classique** : biseaux et couleurs système réglables |
| Cadre et barre de titre de toutes les fenêtres | DWM | attributs DWM (couleur de bordure, de titre, coins droits) |
| Barre des tâches, Démarrer, centre rapide, Alt+Tab | XAML dans `explorer.exe` | remplacer par un shell maison, ou styler en XAML |
| Paramètres, gestionnaire des tâches, Bloc-notes récent, applications du Store | WinUI / XAML | rien à faire de propre : mode sombre + accent seulement, ou application maison |
| Chrome, Discord, VS Code | leur propre moteur | thèmes CSS de chaque application |
| Connexion, UAC, écran d'arrêt | processus système | non modifiables |

**Le meilleur levier : le thème classique, parce qu'il ressemble déjà à CDX.** Le thème classique (Win9x/2000) dessine
les contrôles en relief : arête claire en haut à gauche, sombre en bas à droite. Les cinq couleurs de relief se règlent
(`HKCU\Control Panel\Colors`) et se rangent exactement sur nos jetons :

| Couleur système Windows | Jeton CDX |
|---|---|
| `ButtonHilight` | `#666666` (arête claire extérieure) |
| `ButtonLight` | `#444444` (arête claire intérieure) |
| `ButtonFace` | `#333333` (`--btn`, `--panel`) |
| `ButtonShadow` | `#222222` |
| `ButtonDkShadow` | `#111111` |
| `Window` / `WindowText` | `#000000` / `#00ff00` (champs et listes : noir, texte vert) |
| `ActiveTitle` → `GradientActiveTitle` | `--a-title-from` → `--a-title-to` (titre à dégradé, actif) |
| `InactiveTitle` → `GradientInactiveTitle` | `#0c0c0c` → `#8f8f8f` (titre inactif) |
| `Hilight` / `HilightText` | `#3f3f3f` / `#ffffff` (sélection grise) |

Les mesures aussi se règlent (`WindowMetrics`) : hauteur de titre 21 px, boutons de titre 14 px, bordure épaisse de
5 px, barres de défilement de 15 px. C'est la valeur 21 / 14 / 5 / 15 de `CHARTE.md` §4.

Le mod Windhawk **Classic Theme** active ce thème ; il cite lui-même « 26H2 build 9444 et plus » : Microsoft a cassé
les boutons de titre et les barres de défilement du thème classique dans ces builds, un second mod (*Fix for Classic
theme metrics*) le corrige, avec une reconnexion à chaque changement de thème. **Réserves** : la barre des tâches
Windows 11 et le gestionnaire des tâches moderne sont incompatibles avec le thème classique (d'où la barre et le
gestionnaire maison) ; les mods qui s'injectent dans `winlogon.exe`, `dwm.exe` ou `userinit.exe` peuvent empêcher
l'ouverture de session s'ils se trompent.

**Police.** Dans les fenêtres Win32, le texte passe par GDI, qui ne sait pas lire les polices en couches du kit
(glyphes de base vides). `outils/gen_font_gdi.py` produit les variantes à contours simples (`polices/kirc-pixel-gdi.ttf`,
`kirc-pixel-log-gdi.ttf`). **Testé** : à 14 px, lissage coupé (GDI+), chaque pixel tombe sur un pixel d'écran, sans
flou. Reste à les brancher sur les polices du système (`NONCLIENTMETRICS`, mod *Explorer Font Changer*) et à vérifier
l'effet sur toutes les applications. Réserve de licence inchangée (`CHARTE.md` §10) : usage personnel, pas de diffusion.

**Les trois couches à bâtir, dans cet ordre.**

1. **Couleurs et contour, sans installer d'outil.** Mode sombre, couleur d'accent, couleurs système ci-dessus, puis un
   petit programme (ou les mods Windhawk *Auto Custom Titlebar Colors* et *Custom Window Corner Radius* à 0) qui pose
   sur chaque fenêtre la couleur de bordure et de titre (`DWMWA_BORDER_COLOR`, `DWMWA_CAPTION_COLOR`, coins droits,
   Windows 11 22H2 et plus). Tout se défait en un clic. Résultat : cadre accent / gris, coins droits ; pas de relief,
   boutons de titre à la Windows.
2. **Thème classique + police pixel.** Le relief CDX sur tout le Win32. À essayer d'abord **dans une machine virtuelle**
   (Hyper-V, Windows 11 Pro l'a), jamais en premier sur le poste principal.
3. **Shell maison.** La barre des tâches, Démarrer, le centre rapide, Alt+Tab. Trois candidats, à trancher :
   **Seelen UI** (libre, AGPL-3.0, thèmes en CSS/JSON, barre, lanceur, panneaux de volume et de luminosité, Alt+Tab,
   gestionnaire de fenêtres ; des utilisateurs signalent des plantages au démarrage) ; **Zebar + GlazeWM** (déjà utilisés
   dans `maquette-windows.html`, widgets en HTML : la charte s'y applique telle quelle) ; **une application Tauri
   maison** reprenant le CSS de la charte. Points durs de la voie maison : héberger les icônes de la zone de
   notification, récupérer les notifications, la liste des fenêtres.

**Ce qui restera de Windows** : écran de connexion, UAC, écran d'arrêt, Paramètres (sauf accent et mode sombre).

Sources : [Windhawk — Classic Theme](https://github.com/ramensoftware/windhawk-mods) (`classic-theme-enable`,
`classic-theme-metrics-fix`, `win10-taskbar-on-win11-24h2`, `auto-custom-titlebar-colors`, `custom-corner-radius`,
`caption-buttons-customizer`), [Seelen UI](https://github.com/eythaann/Seelen-UI),
[Tacky Borders](https://github.com/lukeyou05/tacky-borders), [SecureUxTheme](https://github.com/namazso/SecureUxTheme)
(déconseillé : issues récentes de sessions cassées), [DwmSetWindowAttribute](https://learn.microsoft.com/windows/apps/desktop/modernize/apply-rounded-corners).

## 5. Aller jusqu'au bout : une solution pour chaque écran (étude complémentaire, 30/10/2026)

Principe : **garder le natif quand un outil le rend fidèle, le remplacer par une application maison quand il ne le peut
pas.** Une application maison (Tauri / WebView2 avec la charte en CSS) est fidèle à 100 % par construction : c'est la
solution de repli de toutes les lignes. « Natif » = outil existant, fidélité estimée d'après leurs fiches, **non
mesurée**.

| Écran | Voie native (outil, fidélité) | Voie maison (100 %) |
|---|---|---|
| 1 Bureau | icônes et fond : thème classique + couleurs (~85 %) | fenêtre-bureau maison sous les fenêtres (type Zebar / Rainmeter), icônes et gadgets en HTML |
| 2 Démarrer | *Start Menu Styler* : coins droits, couleurs, polices, dégradés (~60 %), la disposition reste celle de Windows | lanceur maison ouvert par la touche Windows (dossiers Menu Démarrer + AppsFolder) |
| 3 Explorateur | thème classique + mods `classic-explorer-*` : barre de menus, arbre, barre d'état en Win32 relief (~80 %) ; *File Explorer Styler* pour la barre de commandes XAML | explorateur maison (le plus long : menus contextuels du shell, miniatures, glisser-déposer) |
| 4 Paramètres | *Settings Styler* (cible `SystemSettings.exe`) : propriétés XAML au choix (coins, bordures, dégradés, polices) (~60 %) | panneau maison pour la dizaine de réglages utilisés, liens `ms-settings:` pour le reste |
| 5 Tâches | gestionnaire classique Win7 (incompatible avec le moderne) (~80 %) | application maison (compteurs PDH / `sysinfo`), courbes en oscilloscope |
| 6 Centre rapide, notifications, calendrier | *Notification Center Styler* (~60 %) | panneaux maison : volume (Core Audio), Wi-Fi (WLAN API), luminosité (WMI) ; les notifications des autres applications sont le point dur |
| 7 Alt+Tab | mods `classic-window-switcher`, `radial-window-switcher` | sélecteur maison (crochet clavier + vignettes DWM) ; Seelen UI en a déjà un |
| 8 Dialogues | thème classique : Exécuter, propriétés, boîtes de message (~90 %) ; **contrôle de compte** : il s'affiche sur le bureau sécurisé, hors de portée de tout outil ; on peut le ramener sur le bureau normal (`PromptOnSecureDesktop=0`), mais cela retire une protection de sécurité | — |
| 9 Verrouillage / connexion | *Lock Screen Clock Styler* (horloge de l'écran de verrouillage, XAML) ; `uxtheme-hook` cible `logonui.exe` (~30 %) ; un fournisseur d'identification ne change que la tuile de mot de passe | **économiseur d'écran maison** (`.scr`) : plein écran entièrement libre, avec « demander le mot de passe à la reprise » ; le vrai mot de passe reste celui de Windows |
| 10 Applications | Windows Terminal : couleurs, police, effet rétro dans `settings.json` (~85 %) ; Bloc-notes : mod `modern-notepad-to-classic-editor` | Bloc-notes et calculatrice maison, triviaux |
| 11 Arrêt | **impossible** : l'écran est dessiné par le noyau | rapport d'arrêt maison affiché au redémarrage suivant (lecture de l'événement 41 / 1001) |
| 12 Éléments | — | — |

**Trois techniques qui expliquent ces chiffres.**

- **Injection XAML** (*Taskbar / Start Menu / Notification Center / Settings / File Explorer Styler*, tous de m417z) :
  un mod se branche sur l'arbre visuel d'une application XAML et réécrit les propriétés d'éléments choisis par type et
  nom (`CornerRadius=0`, `BorderThickness`, `Background:=<LinearGradientBrush…>`, polices…). Limite : on **modifie**
  l'existant, on n'ajoute pas d'éléments, donc pas de biseau en quatre lignes (au mieux une bordure à dégradé).
- **Thème classique** : voir §4. Le plus fidèle au relief CDX, mais uniquement pour le Win32.
- **Injection dans `dwm.exe`** (*Custom Window Corner Radius*, *Caption Buttons Customizer* ; en grand : OpenGlass,
  DWMBlurGlass) : permet de **redessiner le cadre et les boutons de titre de toutes les fenêtres**, XAML comprises.
  Contre : ces outils accrochent des fonctions privées de DWM, propres à chaque build ; OpenGlass déclare ne pas
  prendre en charge les builds Insider. À réserver à la fin, une fois le reste en place.

**Ce qui est vraiment hors de portée** : le texte de l'écran d'arrêt, le bureau sécurisé du contrôle de compte, la
saisie du mot de passe (LogonUI).

**Une voie d'extrême recours, non vérifiée** : un compositeur maison qui affiche chaque fenêtre de Windows (miniatures
DWM ou Windows Graphics Capture) dans un cadre CDX dessiné par nous, et réexpédie la souris et le clavier. Ce serait la
fidélité totale pour toutes les applications, mais c'est un gestionnaire de fenêtres complet à écrire. À ne tenter que
si les voies ci-dessus ne suffisent pas.

**Prudence commune** : Windhawk (winget : `RamenSoftware.Windhawk`, version 1.7.3) injecte du code dans des processus
système ; certains mods le font dans tous les processus (`@include *`), ce qui peut déranger un jeu protégé par un
anti-triche. À vérifier jeu par jeu, et à essayer d'abord en machine virtuelle.

Sources : [windhawk-mods](https://github.com/ramensoftware/windhawk-mods) (mods lus dans le dépôt : `windows-11-*-styler`,
`uxtheme-hook`, `lock-screen-clock-styler`, `classic-theme-enable`, `win11-old-taskmgr`, `caption-buttons-customizer`),
[OpenGlass](https://github.com/ALTaleX531/OpenGlass), [DWMBlurGlass](https://github.com/Maplespe/DWMBlurGlass),
[Credential providers et Custom Logon](https://learn.microsoft.com/windows/configuration/custom-logon),
[XAML Diagnostics](https://learn.microsoft.com/windows/win32/api/xamlom/nf-xamlom-initializexamldiagnosticsex).
