# Couche 1 : le cadre des fenêtres et le fond d'écran

Premier pas de `CHARTE-WINDOWS.md` §4. Aucun outil tiers, aucun réglage système modifié hors du fond d'écran.

| Fichier | Rôle |
|---|---|
| `cdx-frame.cs` | programme résident (icône dans la zone de notification) : pose sur chaque fenêtre la bordure et le titre à l'accent (active) ou gris (inactive), le texte du titre, les coins droits, le cadre sombre |
| `fond.html` | le fond d'écran de `windows-ui.html`, rendu à la taille de l'écran |
| `appliquer.ps1` | sauvegarde le fond actuel, compile `cdx-frame.exe`, pose le fond, lance le programme |
| `restaurer.ps1` | arrête le programme, rend leur cadre aux fenêtres ouvertes, remet le fond d'avant |

```
pwsh -File appliquer.ps1 -Theme rouge      # rouge bleu vert violet ambre sarcelle charbon ; -SansFond pour garder le fond
pwsh -File restaurer.ps1
cdx-frame.exe --status                     # échantillonne les pixels réels de la bordure et du titre de chaque fenêtre
```

Le programme ne démarre pas avec la session : relancer `appliquer.ps1 -SansFond` après un redémarrage.

## Mesuré (30/10/2026, Windows 11 26H2 build 26300)

Fenêtre `winver` : active = bordure `#cc0000`, titre `#880000` ; inactive = `#555555`, `#333333` ; le rouge passe à la fenêtre
qui reprend le focus. Les attributs de couleur ne se relisent pas (`DwmGetWindowAttribute` renvoie `E_INVALIDARG`) :
`--status` lit donc les pixels de l'écran.

## Limites connues

- La barre de titre est **unie** (pas de dégradé), les boutons de titre restent ceux de Windows : seule l'injection dans `dwm.exe`
  ou le thème classique (couche 2) peut aller plus loin.
- Les fenêtres qui dessinent leur propre barre de titre (Windows Terminal, Chrome, Tauri) gardent leur titre ; leur **bordure**
  est colorée. Celles à effet Mica (Explorateur, Paramètres) gardent leur titre, la couleur de titre n'y change rien.
- La couleur d'accent du système (Paramètres) n'est pas touchée.
