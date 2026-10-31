# Essai isolé de l'Explorateur dans sa vraie fenêtre (le socle : liste lue en arrière-plan, réglages persistants) ; il grandira avec les fonctionnalités (liste vivante, vues...).
# Des dossiers jetables dans un dossier temporaire, la page pilotée par le canal de test ; rien n'est lu ni écrit hors de ce dossier et du dossier de données de l'essai.
#   pwsh -File tests\explorer.ps1 [-Bin <dossier des .exe>]
param([string]$Bin)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
$id = "$PID"
$suite = Join-Path $env:TEMP "cdx-ex-suite-$id"; $data = Join-Path $env:TEMP "cdx-ex-data-$id"; $work = Join-Path $env:TEMP "cdx-ex-work-$id"
New-Item -ItemType Directory $suite, $data, $work -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
$prefix = "cdxex$id."
$env:CDX_PIPE_PREFIX = $prefix; $env:CDX_SUITE_DIR = $suite; $env:CDX_DATA_DIR = $data
function Check($what, $ok) { "{0}  {1}" -f ($(if ($ok) { 'OK    ' } else { 'ÉCHEC ' })), $what }
function Ctl([string]$line) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "${prefix}explorer.ctl", [System.IO.Pipes.PipeDirection]::InOut)
  try { $c.Connect(5000) } catch { return $null }
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
}
function Js([string]$code) { if ($code -match 'const') { $code = "(() => { $code })()" }   # un const global resterait déclaré pour les essais suivants
  $r = Ctl "eval $code"; if ($r -and $r.StartsWith('"')) { $r | ConvertFrom-Json } else { $r } }
function Until([scriptblock]$f, [int]$ms) { $t = [Diagnostics.Stopwatch]::StartNew(); while ($t.ElapsedMilliseconds -lt $ms) { if (& $f) { return $true }; Start-Sleep -Milliseconds 150 }; return [bool](& $f) }
function Start-Explorer([string]$dir) { Start-Process "$Bin\cdx-explorer.exe" -ArgumentList "`"$dir`"" -PassThru }

$started = @(); $t0Run = Get-Date
try {
  New-Item -ItemType Directory "$work\petit", "$work\gros" | Out-Null
  foreach ($n in 'a', 'b', 'c') { Set-Content "$work\petit\$n.txt" $n }
  1..20000 | ForEach-Object { [IO.File]::WriteAllText("$work\gros\f$_.txt", 'x') }
  $app = Start-Explorer "$work\petit"; $started += $app
  Check "la fenêtre s'ouvre et lit le dossier en arrière-plan : 3 fichiers" (Until { (Js "T().items.length") -eq '3' } 20000)

  # --- une demande plus récente annule la précédente : jamais de liste périmée ---
  Js "go('$($work.Replace('\', '\\'))\\gros', 'push'); go('$($work.Replace('\', '\\'))\\petit', 'push')" | Out-Null
  Start-Sleep -Seconds 3
  Check "deux dossiers demandés d'affilée : seul le dernier (petit, 3 éléments) est affiché, la liste de 20 000 n'a pas pris sa place" (((Js "T().path") -eq "$work\petit") -and ((Js "T().items.length") -eq '3'))
  Js "go('$($work.Replace('\', '\\'))\\gros', 'push')" | Out-Null
  Check "un grand dossier (20 000 fichiers) finit par s'afficher, sans gel de la page" (Until { (Js "T().items.length") -eq '20000' } 30000)
  Check "pendant ce temps la page répond toujours (la lecture ne bloque pas le fil d'interface)" ((Js "1 + 1") -eq '2')
  Js "go('$($work.Replace('\', '\\'))\\petit', 'push')" | Out-Null
  Until { (Js "T().items.length") -eq '3' } 20000 | Out-Null

  # --- la liste suit le dossier toute seule (surveillance) ---
  Js "T().sel = new Set(['a.txt']); show(true)" | Out-Null
  Set-Content "$work\petit\nouveau.txt" 'n'
  Check "un fichier créé hors de la fenêtre apparaît sans F5" (Until { (Js "T().items.length") -eq '4' -and ((Js "T().rows.map(r => r.n).join(',')") -like '*nouveau.txt*') } 6000)
  Check "la sélection est gardée pendant la relecture (a.txt reste sélectionné)" ((Js "[...T().sel].join(',')") -eq 'a.txt')
  Rename-Item "$work\petit\nouveau.txt" 'renomme.txt'
  Check "un fichier renommé hors de la fenêtre : l'ancien nom disparaît, le nouveau apparaît" (Until { $n = Js "T().rows.map(r => r.n).join(',')"; $n -like '*renomme.txt*' -and $n -notlike '*nouveau.txt*' } 6000)
  Remove-Item "$work\petit\renomme.txt"
  Check "un fichier supprimé hors de la fenêtre disparaît de la liste" (Until { (Js "T().items.length") -eq '3' } 6000)
  1..30 | ForEach-Object { Set-Content "$work\petit\rafale$_.txt" 'r' }
  Check "une rafale de 30 créations est regroupée : la liste complète (33) est là peu après" (Until { (Js "T().items.length") -eq '33' } 8000)
  Get-ChildItem "$work\petit\rafale*.txt" | Remove-Item
  Until { (Js "T().items.length") -eq '3' } 8000 | Out-Null
  # un renommage en cours n'est pas interrompu par une relecture
  Js "document.getElementById('find').value = ''; T().sel = new Set(['b.txt']); show(true); rename('b.txt')" | Out-Null
  Check "le champ de renommage est ouvert" ((Js "!!document.querySelector('#files input')") -eq 'true')
  Set-Content "$work\petit\pendant.txt" 'p'
  Start-Sleep -Milliseconds 1500
  Check "pendant un renommage, une relecture ne détruit pas le champ de saisie" ((Js "!!document.querySelector('#files input')") -eq 'true')
  Js "document.querySelector('#files input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))" | Out-Null
  Check "le renommage terminé, la liste se met à jour (pendant.txt apparaît)" (Until { (Js "T().rows.map(r => r.n).join(',')") -like '*pendant.txt*' } 6000)
  Remove-Item "$work\petit\pendant.txt"
  Until { (Js "T().items.length") -eq '3' } 6000 | Out-Null

  # --- le menu « Réglages » du bouton hachuré de la barre de titre (Affichage, Sélection, couleur d'accent) ---
  Js "document.querySelector('.tb-m').click()" | Out-Null
  Check "le bouton hachuré ouvre le menu « Réglages » : Affichage, Sélection, Couleur d'accent" (Until { (Js "[...document.querySelectorAll('.menu .menu-item')].map(e => e.textContent.trim()).join('|')") -match '^Affichage\|Sélection\|Couleur d.accent$' } 4000)
  Check "le menu porte le titre « Réglages »" ((Js "document.querySelector('.menu .menu-title')?.textContent") -eq 'Réglages')
  Js "[...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.includes('accent')).click()" | Out-Null
  Check "« Couleur d'accent » : les sept couleurs de la suite, la couleur actuelle cochée" (Until { (Js "[...document.querySelectorAll('.menu .menu-item')].filter(e => ['Rouge','Bleu','Vert','Violet','Ambre','Sarcelle','Charbon'].includes(e.textContent.trim())).length") -eq '7' -and (Js "document.querySelectorAll('.menu .chk.on').length") -eq '1' } 4000)
  Js "[...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.trim() === 'Bleu').click()" | Out-Null
  Check "choisir « Bleu » écrit l'accent de la suite (suite.json, isolé pour l'essai)" (Until { (Test-Path "$suite\suite.json") -and ((Get-Content "$suite\suite.json" -Raw) -match '"accent"\s*:\s*"bleu"') } 6000)
  Check "la fenêtre prend la couleur : accent bleu reçu de la suite" (Until { (Js "document.documentElement.dataset.theme") -eq 'bleu' } 6000)
  Check "le menu se ferme après le choix" ((Js "document.querySelectorAll('.menu[data-cdx]').length") -eq '0')
  Js "document.querySelector('.tb-m').click(); [...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.includes('accent')).click(); [...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.trim() === 'Rouge').click()" | Out-Null
  Check "retour au rouge (état d'origine de l'essai)" (Until { (Get-Content "$suite\suite.json" -Raw) -match '"accent"\s*:\s*"rouge"' } 6000)
  Check "les boutons « Sélection » et « Affichage » ont quitté la barre d'outils (déchargée)" ((Js "!document.getElementById('cSel') && !document.getElementById('cView')") -eq 'true')

  # --- éléments cachés et extensions (menu Affichage) ---
  Set-Content "$work\petit\secret.txt" 's'; (Get-Item "$work\petit\secret.txt").Attributes = 'Hidden'
  Start-Sleep -Milliseconds 800
  Check "un fichier caché n'est pas listé par défaut" ((Js "T().rows.map(r => r.n).join(',')") -notlike '*secret.txt*')
  Js "document.querySelector('.tb-m').click(); [...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.trim().startsWith('Affichage')).click()" | Out-Null
  Check "Réglages > Affichage ouvre un sous-menu avec « Éléments cachés » et « Extensions des fichiers »" (Until { (Js "[...document.querySelectorAll('.menu .menu-item')].map(e => e.textContent).join('|')") -match 'Éléments cachés.*Extensions des fichiers' } 4000)
  Js "[...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.includes('Éléments cachés')).click()" | Out-Null
  Check "cocher « Éléments cachés » relit le dossier : secret.txt apparaît, estompé" (Until { (Js "T().rows.map(r => r.n).join(',')") -like '*secret.txt*' -and (Js "!!document.querySelector('#files .fr.hid')") -eq 'true' } 8000)
  Check "le réglage est enregistré (show.hidden)" (Until { (Get-Content "$data\explorer.json" -Raw) -match '"show.hidden":true' } 4000)
  Js "document.querySelector('.tb-m').click(); [...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.trim().startsWith('Affichage')).click()" | Out-Null
  Js "[...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.includes('Extensions')).click()" | Out-Null
  Check "décocher « Extensions des fichiers » : a.txt s'affiche « a »" (Until { (Js "document.querySelector('#files .fr .nm span:last-child').textContent") -eq 'a' } 4000)
  Check "le nom réel ne change pas (a.txt, pour les opérations)" ((Js "T().rows[0].n") -eq 'a.txt')
  Js "rename('a.txt')" | Out-Null
  Check "le renommage d'un fichier sans extension affichée édite « a » (sans .txt)" ((Js "document.querySelector('#files input').value") -eq 'a')
  Js "const i = document.querySelector('#files input'); i.value = 'z'; i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))" | Out-Null
  Check "valider « z » renomme en z.txt (l'extension masquée est conservée)" (Until { Test-Path "$work\petit\z.txt" } 6000)
  Js "document.querySelector('.tb-m').click(); [...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.trim().startsWith('Affichage')).click(); [...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.includes('Extensions')).click()" | Out-Null
  Js "document.querySelector('.tb-m').click(); [...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.trim().startsWith('Affichage')).click(); [...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.includes('cachés')).click()" | Out-Null
  Check "tout remis par défaut : extensions affichées, caché masqué" (Until { (Js "T().rows.map(r => r.n).join(',')") -like '*z.txt*' -and (Js "T().rows.map(r => r.n).join(',')") -notlike '*secret.txt*' } 8000)
  Rename-Item "$work\petit\z.txt" 'a.txt'
  Until { (Js "T().rows.map(r => r.n).join(',')") -like '*a.txt*' } 6000 | Out-Null

  # --- sélection avancée et raccourcis clavier (événements fabriqués) ---
  New-Item -ItemType Directory "$work\touches" | Out-Null
  foreach ($n in 'alpha', 'beta', 'bravo') { Set-Content "$work\touches\$n.txt" $n }
  1..40 | ForEach-Object { Set-Content ("$work\touches\f{0:00}.txt" -f $_) 'f' }
  Js "go('$($work.Replace('\', '\\'))\\touches', 'push')" | Out-Null
  Check "un dossier de 43 fichiers s'ouvre" (Until { (Js "T().items.length") -eq '43' } 8000)
  function Key([string]$k, [string]$mods = '{}') { Js "document.dispatchEvent(new KeyboardEvent('keydown', Object.assign({ key: '$k', bubbles: true, cancelable: true }, $mods)))" | Out-Null }
  function Sel { Js "T().rows.filter(r => T().sel.has(r.n)).map(r => r.n).join(',')" }
  Js "T().sel = new Set(); T().cursor = -1; show(true)" | Out-Null
  Key 'ArrowDown'
  Check "↓ sélectionne le premier élément" ((Sel) -eq 'alpha.txt')
  Key 'ArrowDown'
  Check "↓ encore : le deuxième" ((Sel) -eq 'beta.txt')
  Key 'ArrowUp'
  Check "↑ revient au premier" ((Sel) -eq 'alpha.txt')
  Key 'End'
  Check "Fin : le dernier élément, et la liste a défilé jusqu'à lui" (((Sel) -eq 'f40.txt') -and ([int](Js "document.getElementById('files').scrollTop") -gt 0))
  Key 'Home'
  Check "Début : le premier élément, liste remontée" (((Sel) -eq 'alpha.txt') -and ((Js "document.getElementById('files').scrollTop") -eq '0'))
  Key 'ArrowDown' '{shiftKey:true}'; Key 'ArrowDown' '{shiftKey:true}'; Key 'ArrowDown' '{shiftKey:true}'
  Check "Maj+↓ trois fois étend la sélection : quatre éléments depuis l'ancre" ((Sel) -eq 'alpha.txt,beta.txt,bravo.txt,f01.txt')
  Key 'ArrowUp' '{shiftKey:true}'
  Check "Maj+↑ la rétrécit d'un élément" ((Sel) -eq 'alpha.txt,beta.txt,bravo.txt')
  Js "T().sel = new Set(); T().cursor = -1; T().anchor = -1; show(true)" | Out-Null
  Key 'PageDown'
  $pg = [int](Js "T().cursor")
  Check "Page bas descend d'une page (curseur à l'indice $pg)" ($pg -ge 5 -and $pg -le 40)
  Key 'a' '{ctrlKey:true}'
  Check "Ctrl+A sélectionne tout (43)" ((Js "T().sel.size") -eq '43')
  Key 'Escape'
  Check "Échap désélectionne" ((Js "T().sel.size") -eq '0')
  Js "T().sel = new Set(['alpha.txt']); show(true)" | Out-Null
  Key 'I' '{ctrlKey:true, shiftKey:true}'
  Check "Ctrl+Maj+I inverse la sélection (42 éléments, sans alpha.txt)" (((Js "T().sel.size") -eq '42') -and ((Js "T().sel.has('alpha.txt')") -eq 'false'))
  Js "document.querySelector('.tb-m').click(); [...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.trim().startsWith('Sélection')).click()" | Out-Null
  Check "Réglages > Sélection ouvre un sous-menu (tout, rien, inverser)" (Until { (Js "[...document.querySelectorAll('.menu .menu-item')].map(e => e.textContent).join('|')") -match 'Tout sélectionner.*Ne rien sélectionner.*Inverser' } 4000)
  Js "[...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.includes('Inverser')).click()" | Out-Null
  Check "« Inverser la sélection » du menu : seul alpha.txt est de nouveau sélectionné" ((Sel) -eq 'alpha.txt')
  # saisie pour sauter
  Js "T().sel = new Set(); T().cursor = -1; show(true)" | Out-Null
  Key 'b'
  Check "taper « b » saute à beta.txt" ((Sel) -eq 'beta.txt')
  Key 'r'
  Check "taper « r » aussitôt après (« br ») saute à bravo.txt" ((Sel) -eq 'bravo.txt')
  Start-Sleep -Milliseconds 1300
  Key 'b'
  Check "une seconde plus tard la saisie est oubliée : « b » repart et cycle vers le b suivant (beta.txt)" ((Sel) -eq 'beta.txt')
  Start-Sleep -Milliseconds 1300   # la saisie « b » doit être oubliée, sinon « bf »
  Key 'f'; Key 'f' ; Key 'f'
  Check "taper « fff » cycle d'un nom en « f » au suivant (f03.txt après trois « f »)" ((Sel) -eq 'f03.txt')
  # tout ce qui part vers l'hôte est intercepté : aucune boîte de propriétés, rien dans le presse-papiers de Valentin
  Js "window.__spy = []; window.__real = window.chrome.webview.postMessage; window.chrome.webview.postMessage = o => { window.__spy.push(o); if (!['fs.props', 'fs.copypath'].includes(o.cmd)) window.__real.call(window.chrome.webview, o); }" | Out-Null
  Js "T().sel = new Set(['bravo.txt']); show(true)" | Out-Null
  Key 'Enter' '{altKey:true}'
  Check "Alt+Entrée demande les propriétés du fichier sélectionné" ((Js "JSON.stringify(window.__spy.filter(m => m.cmd === 'fs.props').map(m => m.path))") -like '*touches\\bravo.txt*')
  Key 'C' '{ctrlKey:true, shiftKey:true}'
  Check "Ctrl+Maj+C demande de copier le chemin (le message part avec le chemin complet) et le dit dans la barre d'état" (((Js "JSON.stringify(window.__spy.filter(m => m.cmd === 'fs.copypath').map(m => m.paths))") -like '*touches\\bravo.txt*') -and ((Js "document.getElementById('st2').textContent") -eq 'Chemin copié'))
  Js "window.chrome.webview.postMessage = window.__real" | Out-Null
  Key 'h' '{ctrlKey:true}'
  Check "Ctrl+H coche « Éléments cachés » (réglage show.hidden)" (Until { (Js "settings['show.hidden']") -eq 'true' } 6000)
  Key 'h' '{ctrlKey:true}'
  Check "Ctrl+H une seconde fois le décoche" (Until { (Js "settings['show.hidden']") -eq 'false' } 6000)
  Key 'n' '{ctrlKey:true}'
  Check "Ctrl+N ouvre une nouvelle fenêtre de l'Explorateur" (Until { (Ctl 'state') -like '2 fenêtre*' } 15000)
  Key 'F1'
  Check "F1 ouvre l'aide « Raccourcis clavier » avec toute la table (plus de 30 raccourcis)" ((Js "document.querySelectorAll('#layer .kb .kbk').length") -gt 30)
  Js "T().sel = new Set(['alpha.txt']); show(true)" | Out-Null
  Key 'ArrowDown'
  Check "boîte ouverte : les flèches ne déplacent pas la sélection de la liste en dessous" ((Sel) -eq 'alpha.txt')
  Js "document.getElementById('layer').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))" | Out-Null
  Check "Échap ferme l'aide" ((Js "!document.querySelector('#layer .veil')") -eq 'true')
  # rectangle de sélection, dans un dossier court
  Js "go('$($work.Replace('\', '\\'))\\petit', 'push')" | Out-Null
  Until { (Js "T().path") -eq "$work\petit" -and (Js "T().items.length") -eq '3' } 8000 | Out-Null
  Js "T().sel = new Set(); show(true)" | Out-Null
  Js "const f = document.getElementById('files'), b = f.getBoundingClientRect(), x = b.left + 120, y0 = b.bottom - 16; f.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, buttons: 1, pointerId: 11, clientX: x, clientY: y0 })); document.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, buttons: 1, pointerId: 11, clientX: x + 30, clientY: y0 - 30 })); document.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, buttons: 1, pointerId: 11, clientX: x + 60, clientY: b.top + 6 }));" | Out-Null
  Check "tirer un rectangle depuis l'espace vide dessine le rectangle et sélectionne les lignes touchées (3 fichiers)" (((Js "!!document.querySelector('.rubber')") -eq 'true') -and ((Js "T().sel.size") -eq '3'))
  Js "document.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, button: 0, pointerId: 11 }))" | Out-Null
  Check "relâcher retire le rectangle ; la sélection reste" (((Js "!document.querySelector('.rubber')") -eq 'true') -and ((Js "T().sel.size") -eq '3'))
  Js "const f = document.getElementById('files'), b = f.getBoundingClientRect(); const r = [...f.querySelectorAll('.fr')][0].getBoundingClientRect(); T().sel = new Set(['b.txt']); show(true); f.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, buttons: 1, pointerId: 12, ctrlKey: true, clientX: b.left + 120, clientY: b.bottom - 16 })); document.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, buttons: 1, pointerId: 12, clientX: b.left + 150, clientY: r.top + 4 })); document.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, button: 0, pointerId: 12 }))" | Out-Null
  Check "avec Ctrl, le rectangle s'ajoute à la sélection existante (b.txt et au moins un autre)" ((Js "T().sel.has('b.txt') && T().sel.size >= 2") -eq 'true')

  # --- menu contextuel complet (éléments, fond, sous-menus) ---
  New-Item -ItemType Directory "$work\menus\sousdossier" | Out-Null
  Set-Content "$work\menus\note.txt" 'n'; Set-Content "$work\menus\autre.txt" 'a'
  $wjm = "$work\menus".Replace([string][char]92, [string][char]92 + [string][char]92)
  Js "go('$wjm', 'push')" | Out-Null
  Check "un dossier de menus s'ouvre (3 éléments)" (Until { (Js "T().path") -eq "$work\menus" -and (Js "T().items.length") -eq '3' } 8000)
  Check "Windows a fourni les données du menu (modèles « Nouveau » dont le texte)" (Until { (Js "menuData.news.some(k => k.ext === '.txt')") -eq 'true' } 8000)
  # tout ce qui part vers l'hôte et lancerait quelque chose est intercepté
  Js "window.__spy = []; window.__real = window.__real || window.chrome.webview.postMessage; window.chrome.webview.postMessage = o => { window.__spy.push(o); if (!['fs.terminal', 'fs.openwith', 'fs.openas', 'fs.copypath', 'fs.sendto', 'fs.props'].includes(o.cmd)) window.__real.call(window.chrome.webview, o); }" | Out-Null
  function Menu { Js "[...document.querySelectorAll('.menu')].map(m => [...m.querySelectorAll('.menu-item')].map(e => e.querySelector('span:not(.chk):not(.pix):not(.subarrow)')?.textContent).join('|')).join(' // ')" }
  function MenuTitle { Js "document.querySelector('.menu .menu-title')?.textContent" }
  function MenuClick([string]$text) { $q = $text.Replace("'", [string][char]92 + "'"); Js "const it = [...document.querySelectorAll('.menu .menu-item')].find(e => e.querySelector('span:not(.chk):not(.pix):not(.subarrow)')?.textContent === '$q'); if (it) it.click(); return !!it" }   # l'apostrophe du libellé est protégée dans le JavaScript
  function RightClick([string]$name) { Js "const r = __rowOf('$name'); const b = r.getBoundingClientRect(); r.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: Math.round(b.left + 60), clientY: Math.round(b.top + 8) }))" | Out-Null }
  Js "window.__rowOf = n => [...document.querySelectorAll('#files .fr')].find(r => r.querySelector('.nm span:last-child').textContent === n)" | Out-Null
  Js "T().sel = new Set(); show(true)" | Out-Null
  RightClick 'note.txt'
  Check "un clic droit sur un fichier non sélectionné le sélectionne et ouvre son menu (titre = le nom)" (((Sel) -eq 'note.txt') -and ((MenuTitle) -eq 'note.txt'))
  $m = Menu
  Check "menu d'un fichier : Ouvrir, Ouvrir avec, terminal, Couper, Copier, Copier le chemin, Envoyer vers, Renommer, Supprimer, Propriétés" ($m -match 'Ouvrir\|Ouvrir avec\|Ouvrir dans un terminal\|Couper\|Copier\|Copier le chemin\|Envoyer vers\|Renommer\|Supprimer\|Supprimer définitivement\|Propriétés')
  Check "un fichier n'offre ni « nouvel onglet » ni « nouvelle fenêtre »" ($m -notmatch 'nouvel onglet|nouvelle fenêtre')
  Check "les éléments à sous-menu portent une flèche (Ouvrir avec, Envoyer vers)" ((Js "document.querySelectorAll('.menu .subarrow').length") -eq '2')
  MenuClick 'Ouvrir avec' | Out-Null
  Check "« Ouvrir avec » ouvre un sous-menu avec « Choisir une autre application… »" (Until { (Menu) -match ' // .*Choisir une autre application' } 4000)
  Js "document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))" | Out-Null
  Start-Sleep -Milliseconds 400
  RightClick 'note.txt'; MenuClick 'Ouvrir avec' | Out-Null
  Check "la liste des programmes pour .txt est arrivée (au moins un programme avant « Choisir… »)" (Until { $mm = Menu; $mm -match ' // ' -and ($mm -split ' // ')[1].Split('|').Count -ge 3 } 6000)
  $app1 = Js "[...document.querySelectorAll('.menu')][1].querySelector('.menu-item span').textContent"
  MenuClick $app1 | Out-Null
  Check "choisir un programme (« $app1 ») envoie fs.openwith avec son exécutable et le fichier (intercepté, rien n'est lancé)" ((Js "JSON.stringify(window.__spy.filter(m => m.cmd === 'fs.openwith').map(m => [m.exe.toLowerCase().endsWith('.exe'), m.path.endsWith('note.txt')]))") -like '*[true,true]*')
  Check "le menu se referme après l'action" ((Js "document.querySelectorAll('.menu').length") -eq '0')
  RightClick 'note.txt'; MenuClick 'Ouvrir avec' | Out-Null; Start-Sleep -Milliseconds 400; MenuClick 'Choisir une autre application…' | Out-Null
  Check "« Choisir une autre application… » envoie fs.openas (intercepté)" ((Js "window.__spy.filter(m => m.cmd === 'fs.openas').length") -ge 1)
  RightClick 'note.txt'; MenuClick 'Ouvrir dans un terminal' | Out-Null
  Check "« Ouvrir dans un terminal » sur un fichier ouvre le terminal dans son dossier (intercepté)" ((Js "JSON.stringify(window.__spy.filter(m => m.cmd === 'fs.terminal').map(m => m.path))") -like "*menus*")
  RightClick 'note.txt'; MenuClick 'Copier le chemin' | Out-Null
  Check "« Copier le chemin » envoie fs.copypath avec le chemin complet (intercepté : le presse-papiers de Valentin reste intact)" ((Js "JSON.stringify(window.__spy.filter(m => m.cmd === 'fs.copypath').map(m => m.paths))") -like '*menus\\note.txt*')
  RightClick 'note.txt'; MenuClick 'Envoyer vers' | Out-Null
  Check "« Envoyer vers » ouvre un sous-menu (les destinations de Windows, ou « Aucune destination »)" (Until { (Menu) -match ' // ' } 4000)
  $dest = Js "[...document.querySelectorAll('.menu')][1].querySelector('.menu-item')?.className"
  if ($dest -notmatch 'off') {
    $destName = Js "[...document.querySelectorAll('.menu')][1].querySelector('.menu-item span').textContent"
    MenuClick $destName | Out-Null
    Check "choisir une destination envoie fs.sendto avec l'élément de la liste et le fichier (intercepté)" ((Js "JSON.stringify(window.__spy.filter(m => m.cmd === 'fs.sendto').map(m => [m.item.length > 0, m.paths.length, m.paths[0].endsWith('note.txt')]))") -like '*[true,1,true]*')
  } else { Check "(aucune destination « Envoyer vers » sur ce PC : essai de l'envoi passé)" $true; Js "document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))" | Out-Null }
  RightClick 'sousdossier'
  $m = Menu
  Check "menu d'un dossier : « Ouvrir dans un nouvel onglet » et « …une nouvelle fenêtre » en plus" ($m -match 'Ouvrir dans un nouvel onglet\|Ouvrir dans une nouvelle fenêtre')
  Check "pas d'« Ouvrir avec » pour un dossier" ($m -notmatch 'Ouvrir avec')
  MenuClick 'Ouvrir dans un nouvel onglet' | Out-Null
  Check "« Ouvrir dans un nouvel onglet » ouvre un onglet sur le dossier" (Until { (Js "tabs.length") -eq '2' -and (Js "T().path") -eq "$work\menus\sousdossier" } 6000)
  Js "tabs.splice(1, 1); cur = 0; drawTabs(); show()" | Out-Null
  Js "T().sel = new Set(['note.txt', 'autre.txt']); show(true)" | Out-Null
  RightClick 'note.txt'
  Check "avec deux éléments sélectionnés : le titre dit « 2 éléments », « Ouvrir » et « Renommer » sont grisés" (((MenuTitle) -eq '2 éléments') -and ((Js "[...document.querySelectorAll('.menu-item.off')].map(e => e.querySelector('span:not(.chk):not(.pix)').textContent).sort().join('|')") -eq 'Ouvrir|Renommer'))
  Js "document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))" | Out-Null
  # le fond de la liste
  Js "T().sel = new Set(); show(true); const f = document.getElementById('files'); const b = f.getBoundingClientRect(); f.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: Math.round(b.left + 200), clientY: Math.round(b.bottom - 20) }))" | Out-Null
  $m = Menu
  Check "menu du fond : Nouveau, Coller, Trier par, Afficher, Actualiser, terminal, Propriétés" (($m -match 'Nouveau\|Coller\|Trier par\|Afficher\|Actualiser\|Ouvrir dans un terminal\|Propriétés') -and ((MenuTitle) -eq 'menus'))
  MenuClick 'Trier par' | Out-Null
  Check "« Trier par » : Nom, Date, Type, Taille, Croissant, Décroissant ; Nom est coché" (Until { (Menu) -match ' // Nom\|Date de modification\|Type\|Taille\|Croissant\|Décroissant' } 4000)
  Check "…la case de « Nom » et de « Croissant » est cochée" ((Js "[...document.querySelectorAll('.menu')][1].querySelectorAll('.chk.on').length") -eq '2')
  MenuClick 'Taille' | Out-Null
  Check "choisir « Taille » trie par taille (et le menu se ferme)" (((Js "T().sortK") -eq 's') -and ((Js "document.querySelectorAll('.menu').length") -eq '0'))
  Js "const f = document.getElementById('files'); const b = f.getBoundingClientRect(); f.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: Math.round(b.left + 200), clientY: Math.round(b.bottom - 20) }))" | Out-Null
  MenuClick 'Trier par' | Out-Null; Start-Sleep -Milliseconds 300; MenuClick 'Décroissant' | Out-Null
  Check "« Décroissant » inverse le tri" ((Js "T().rev") -eq 'true')
  Js "T().sortK = 'n'; T().rev = false; show(true)" | Out-Null
  Js "const f = document.getElementById('files'); const b = f.getBoundingClientRect(); f.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: Math.round(b.left + 200), clientY: Math.round(b.bottom - 20) }))" | Out-Null
  MenuClick 'Afficher' | Out-Null
  Check "« Afficher » : Vue, Grouper par, Colonnes, Éléments cachés et Extensions des fichiers" (Until { (Menu) -match ' // Vue\|Grouper par\|Colonnes\|Éléments cachés\|Extensions des fichiers' } 4000)
  Js "document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))" | Out-Null
  # Nouveau
  Js "const f = document.getElementById('files'); const b = f.getBoundingClientRect(); f.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: Math.round(b.left + 200), clientY: Math.round(b.bottom - 20) }))" | Out-Null
  MenuClick 'Nouveau' | Out-Null
  Check "« Nouveau » : Dossier, Raccourci…, puis les modèles de Windows (Document texte)" (Until { (Menu) -match ' // Dossier\|Raccourci…\|Document texte' } 4000)
  MenuClick 'Dossier' | Out-Null
  Check "« Nouveau > Dossier » crée « Nouveau dossier » et ouvre la saisie de son nom" ((Until { Test-Path "$work\menus\Nouveau dossier" } 6000) -and (Until { (Js "document.querySelector('#files input')?.value") -eq 'Nouveau dossier' } 6000))
  Js "document.querySelector('#files input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))" | Out-Null
  Start-Sleep -Milliseconds 500
  Js "const f = document.getElementById('files'); const b = f.getBoundingClientRect(); f.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: Math.round(b.left + 200), clientY: Math.round(b.bottom - 20) }))" | Out-Null
  MenuClick 'Nouveau' | Out-Null; Start-Sleep -Milliseconds 300; MenuClick 'Document texte' | Out-Null
  Check "« Nouveau > Document texte » crée un .txt vide et ouvre la saisie de son nom" ((Until { Test-Path "$work\menus\Nouveau document texte.txt" } 6000) -and (Until { (Js "!!document.querySelector('#files input')") } 6000))
  Js "document.querySelector('#files input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))" | Out-Null
  Start-Sleep -Milliseconds 500
  Js "const f = document.getElementById('files'); const b = f.getBoundingClientRect(); f.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: Math.round(b.left + 200), clientY: Math.round(b.bottom - 20) }))" | Out-Null
  MenuClick 'Nouveau' | Out-Null; Start-Sleep -Milliseconds 300; MenuClick 'Raccourci…' | Out-Null
  Check "« Nouveau > Raccourci… » ouvre une boîte qui demande la cible" (Until { (Js "!!document.querySelector('#layer #askIn')") -eq 'true' } 4000)
  Js "const i = document.getElementById('askIn'); i.value = '$($work.Replace('\', '\\'))\\petit'; document.getElementById('askOk').click()" | Out-Null
  Check "valider crée « petit.lnk » qui pointe vers le dossier cible, et la saisie de son nom s'ouvre" ((Until { Test-Path "$work\menus\petit.lnk" } 8000) -and (Until { (Js "!!document.querySelector('#files input')") -eq 'true' } 6000))
  Js "document.querySelector('#files input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))" | Out-Null
  Js "window.chrome.webview.postMessage = window.__real" | Out-Null

  # --- « Plus d'options… » : le menu de Windows (le message est intercepté : le menu natif attend un vrai choix et bloquerait l'essai) ---
  Js "go('$wjm', 'push')" | Out-Null
  Until { (Js "T().path") -eq "$work\menus" -and (Js "T().items.length") -ge 3 } 8000 | Out-Null
  Js "window.__spy = []; window.__real = window.__real || window.chrome.webview.postMessage; window.chrome.webview.postMessage = o => { window.__spy.push(o); if (o.cmd !== 'fs.shellmenu') window.__real.call(window.chrome.webview, o); }" | Out-Null
  Js "T().sel = new Set(); show(true)" | Out-Null
  RightClick 'note.txt'
  Check "le menu d'un fichier se termine par « Propriétés » puis « Plus d'options… »" ((Menu) -match "Propriétés\|Plus d'options…$")
  MenuClick "Plus d'options…" | Out-Null
  Check "« Plus d'options… » envoie fs.shellmenu avec le chemin du fichier (intercepté)" ((Js "JSON.stringify(window.__spy.filter(m => m.cmd === 'fs.shellmenu').map(m => [m.paths.length, m.paths[0].endsWith('note.txt'), m.shift]))") -like '*[1,true,false]*')
  Js "T().sel = new Set(['note.txt', 'autre.txt']); show(true)" | Out-Null
  Key 'F10' '{shiftKey:true}'
  Check "Maj+F10 envoie fs.shellmenu avec les deux fichiers sélectionnés" ((Js "JSON.stringify(window.__spy.filter(m => m.cmd === 'fs.shellmenu').map(m => m.paths.length))") -like '*2*')
  Js "T().sel = new Set(); show(true); const f = document.getElementById('files'); const b = f.getBoundingClientRect(); f.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: Math.round(b.left + 200), clientY: Math.round(b.bottom - 20) }))" | Out-Null
  Check "le menu du fond propose aussi « Plus d'options… »" ((Menu) -match "Propriétés\|Plus d'options…$")
  MenuClick "Plus d'options…" | Out-Null
  Check "pour le fond, le menu de Windows est demandé pour le dossier courant" ((Js "JSON.stringify(window.__spy.filter(m => m.cmd === 'fs.shellmenu').map(m => m.paths[0]))") -like '*menus*')
  Js "window.chrome.webview.postMessage = window.__real" | Out-Null

  # --- recherche dans les sous-dossiers ---
  New-Item -ItemType Directory "$work\recherche\a\b", "$work\recherche\c" | Out-Null
  Set-Content "$work\recherche\facture-2025.pdf" 'p'; Set-Content "$work\recherche\a\photo.jpg" 'j'; Set-Content "$work\recherche\a\b\Facture vieille.txt" 't'
  Set-Content "$work\recherche\c\musique.mp3" 'm'; Set-Content "$work\recherche\notes.md" 'n'
  (Get-Item "$work\recherche\a\b\Facture vieille.txt").LastWriteTime = (Get-Date).AddDays(-400)
  $wjr = "$work\recherche".Replace([string][char]92, [string][char]92 + [string][char]92)
  Js "go('$wjr', 'push')" | Out-Null
  Check "un dossier à sous-dossiers s'ouvre" (Until { (Js "T().path") -eq "$work\recherche" -and (Js "T().items.length") -eq '4' } 8000)
  Js "document.getElementById('bSearch').click()" | Out-Null
  Check "le bouton « Partout » ouvre la barre de critères (type, date, taille) et vide la liste" (((Js "!document.getElementById('sbar').hidden") -eq 'true') -and ((Js "T().rows.length") -eq '0') -and ((Js "document.getElementById('sKind').options.length") -eq '9'))
  Check "la liste vide invite à taper un nom" ((Js "document.querySelector('#files .empty').textContent") -like 'Tape un nom*')
  Js "const f = document.getElementById('find'); f.value = 'facture'; f.dispatchEvent(new Event('input'))" | Out-Null
  Check "taper « facture » trouve les deux fichiers des sous-dossiers (recherche différée de 300 ms)" (Until { (Js "T().rows.map(r => r.b).sort().join(',')") -eq 'Facture vieille.txt,facture-2025.pdf' } 8000)
  Check "le dossier de chaque résultat s'affiche en grisé après son nom (« a\b » pour la vieille facture)" ((Js "[...document.querySelectorAll('#files .fr .rd')].map(e => e.textContent).sort().join('|')") -eq ".|a\b")
  Check "la barre d'état compte les résultats" (Until { (Js "document.getElementById('st1').textContent") -match '^2 résultats' } 6000)
  Js "const s = document.getElementById('sDate'); s.value = 'year'; s.dispatchEvent(new Event('change'))" | Out-Null
  Check "le critère « 12 derniers mois » écarte la facture de 400 jours" (Until { (Js "T().rows.map(r => r.b).join(',')") -eq 'facture-2025.pdf' } 8000)
  Js "const s = document.getElementById('sDate'); s.value = 'any'; s.dispatchEvent(new Event('change')); const k = document.getElementById('sKind'); k.value = 'image'; k.dispatchEvent(new Event('change')); const f = document.getElementById('find'); f.value = ''; f.dispatchEvent(new Event('input'))" | Out-Null
  Check "le type « Images » sans nom ne laisse que photo.jpg" (Until { (Js "T().rows.map(r => r.b).join(',')") -eq 'photo.jpg' } 8000)
  Js "const k = document.getElementById('sKind'); k.value = 'all'; k.dispatchEvent(new Event('change')); const f = document.getElementById('find'); f.value = 'v*e.t?t'; f.dispatchEvent(new Event('input'))" | Out-Null
  Check "le joker « v*e.t?t » trouve « Facture vieille.txt »... (le nom entier doit correspondre : rien)" (Until { (Js "T().rows.length") -eq '0' -and (Js "document.querySelector('#files .empty')?.textContent") -eq 'Aucun résultat' } 8000)
  Js "const f = document.getElementById('find'); f.value = '*vieille.t?t'; f.dispatchEvent(new Event('input'))" | Out-Null
  Check "« *vieille.t?t » trouve la vieille facture" (Until { (Js "T().rows.map(r => r.b).join(',')") -eq 'Facture vieille.txt' } 8000)
  Js "T().sel = new Set([T().rows[0].n]); show(true)" | Out-Null
  Js "window.__spy = []; window.__real = window.__real || window.chrome.webview.postMessage; window.chrome.webview.postMessage = o => { window.__spy.push(o); if (!['fs.open', 'fs.props', 'fs.copypath'].includes(o.cmd)) window.__real.call(window.chrome.webview, o); }" | Out-Null
  Js "const r = document.querySelector('#files .fr'); r.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 200, clientY: 200 }))" | Out-Null
  $m = Menu
  Check "le menu d'un résultat propose « Ouvrir l'emplacement du dossier » et grise « Renommer »" (($m -match "Ouvrir\|Ouvrir l'emplacement du dossier") -and ((Js "[...document.querySelectorAll('.menu-item.off')].map(e => e.querySelector('span:not(.chk):not(.pix)').textContent).join('|')") -match 'Renommer'))
  MenuClick "Ouvrir l'emplacement du dossier" | Out-Null
  Check "« Ouvrir l'emplacement du dossier » ferme la recherche, ouvre a\b et sélectionne le fichier" (Until { (Js "T().path") -eq "$work\recherche\a\b" -and (Js "[...T().sel].join(',')") -eq 'Facture vieille.txt' -and (Js "!T().search") -eq 'true' } 8000)
  Check "la barre de critères a disparu" ((Js "document.getElementById('sbar').hidden") -eq 'true')
  Js "go('$wjr', 'push')" | Out-Null
  Until { (Js "T().path") -eq "$work\recherche" } 6000 | Out-Null
  Key 'e' '{ctrlKey:true}'
  Check "Ctrl+E ouvre la recherche et met le curseur dans la zone de saisie" (((Js "!!T().search") -eq 'true') -and ((Js "document.activeElement.id") -eq 'find'))
  Js "const f = document.getElementById('find'); f.value = 'photo'; f.dispatchEvent(new Event('input'))" | Out-Null
  Until { (Js "T().rows.length") -eq '1' } 8000 | Out-Null
  Js "document.getElementById('find').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))" | Out-Null
  Check "Échap dans la zone de saisie ferme la recherche et rend le dossier (4 éléments)" (Until { (Js "!T().search") -eq 'true' -and (Js "T().items.length") -eq '4' -and (Js "T().path") -eq "$work\recherche" } 8000)
  # ouvrir un résultat dossier / actualiser / changer de dossier
  Key 'e' '{ctrlKey:true}'
  Js "const k = document.getElementById('sKind'); k.value = 'folder'; k.dispatchEvent(new Event('change'))" | Out-Null
  Check "type « Dossiers » : a, a\b et c" (Until { (Js "T().rows.map(r => r.n).sort().join(',')") -eq 'a,a\b,c' } 8000)
  Js "document.querySelector('#files .fr').dispatchEvent(new MouseEvent('mousedown', { bubbles: true, button: 0, detail: 1 })); const r = [...document.querySelectorAll('#files .fr')].find(x => x.textContent.includes('c')); r.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, button: 0, detail: 2 }))" | Out-Null
  Check "double-cliquer un dossier résultat ouvre ce dossier (la recherche se ferme)" (Until { (Js "!T().search") -eq 'true' -and ((Js "T().path") -like '*recherche\*') } 8000)
  Js "go('$wjr', 'push')" | Out-Null
  Until { (Js "T().path") -eq "$work\recherche" } 6000 | Out-Null
  Js "document.getElementById('bSearch').click(); const f = document.getElementById('find'); f.value = 'notes'; f.dispatchEvent(new Event('input'))" | Out-Null
  Until { (Js "T().rows.length") -eq '1' } 8000 | Out-Null
  Set-Content "$work\recherche\notes-bis.md" 'x'
  Key 'F5'
  Check "F5 pendant la recherche la relance : le nouveau fichier est trouvé (2 résultats)" (Until { (Js "T().rows.length") -eq '2' } 8000)
  Js "document.getElementById('bSearch').click()" | Out-Null
  Check "le bouton « Partout » une seconde fois referme la recherche" (Until { (Js "!T().search") -eq 'true' -and (Js "T().items.length") -ge 4 } 8000)
  Remove-Item "$work\recherche\notes-bis.md"
  # annulation d'une recherche longue : lancer sur un grand dossier puis fermer aussitôt
  1..3000 | ForEach-Object { New-Item -ItemType Directory "$work\recherche\c\gros$_" -Force | Out-Null }
  Js "document.getElementById('bSearch').click(); const f = document.getElementById('find'); f.value = 'gros'; f.dispatchEvent(new Event('input'))" | Out-Null
  Start-Sleep -Milliseconds 500
  Js "document.getElementById('sClose').click()" | Out-Null
  Start-Sleep -Seconds 2
  Check "fermer pendant la recherche : aucun résultat tardif ne revient dans la liste du dossier" (((Js "!T().search") -eq 'true') -and ((Js "T().items.every(i => i.b === undefined)") -eq 'true'))
  Js "window.chrome.webview.postMessage = window.__real" | Out-Null
  Remove-Item "$work\recherche\c\gros*" -Recurse -Force
  Js "go('$($work.Replace('\', '\\'))\\petit', 'push')" | Out-Null
  Until { (Js "T().path") -eq "$work\petit" } 8000 | Out-Null

  # --- corbeille et annuler/rétablir : la vraie corbeille de Windows, mais seulement avec des fichiers créés ici (retirés à la fin) ; "Vider la corbeille" n'est jamais envoyé à l'hôte ---
  $wj = $work.Replace('\', '\\')
  New-Item -ItemType Directory "$work\corb" | Out-Null
  foreach ($n in 't1', 't2', 't3', 't4') { Set-Content "$work\corb\$n.txt" "contenu $n" }
  Js "go('$wj\\corb', 'push')" | Out-Null
  Until { (Js "T().items.length") -eq '4' } 8000 | Out-Null
  function KeyOk([string]$k) { Key $k '{ ctrlKey: true }'; $true }   # Ctrl+<touche>, rend vrai pour s'enchaîner dans un Check
  function Confirm-Dialog { Js "const b = document.getElementById('dy'); if (b) b.click(); return !!b" }
  Js "T().sel = new Set(['t1.txt']); show(true)" | Out-Null
  Key 'Delete'
  Check "Suppr ouvre la boîte de confirmation (rien n'est supprimé avant la réponse)" (((Js "!!document.getElementById('dy')") -eq 'true') -and (Test-Path "$work\corb\t1.txt"))
  Confirm-Dialog | Out-Null
  Check "confirmer met le fichier à la corbeille : il disparaît du dossier et de la liste" ((Until { -not (Test-Path "$work\corb\t1.txt") } 6000) -and (Until { (Js "T().items.length") -eq '3' } 6000))
  Check "le menu du fond propose « Annuler Supprimer »" (Until { (Js "undoState.undo") -eq 'Supprimer' } 4000)
  Js "T().sel = new Set(['t2.txt']); show(true)" | Out-Null
  Key 'Delete' '{ shiftKey: true }'
  Check "Maj+Suppr demande une confirmation « définitive »" ((Js "document.querySelector('#layer .tb-title')?.textContent") -eq 'Supprimer définitivement')
  Js "document.getElementById('dn').click()" | Out-Null
  Check "refuser laisse le fichier en place" ((Test-Path "$work\corb\t2.txt") -and ((Js "!!document.getElementById('dy')") -eq 'false'))
  # Ctrl+Z restaure t1 depuis la corbeille, Ctrl+Y le remet
  Key 'z' '{ ctrlKey: true }'
  Check "Ctrl+Z restaure le fichier supprimé, avec son contenu, et le dit dans la barre d'état" ((Until { Test-Path "$work\corb\t1.txt" } 6000) -and ((Get-Content "$work\corb\t1.txt" -Raw).Trim() -eq 'contenu t1') -and (Until { (Js "document.getElementById('st2').textContent") -eq 'Annulé : Supprimer' } 4000))
  Check "après l'annulation, « Rétablir Supprimer » est proposé et « Annuler Supprimer » n'est plus proposé" ((Until { (Js "undoState.redo") -eq 'Supprimer' } 4000) -and ((Js "undoState.undo") -ne 'Supprimer'))
  Key 'y' '{ ctrlKey: true }'
  Check "Ctrl+Y remet le fichier à la corbeille" ((Until { -not (Test-Path "$work\corb\t1.txt") } 6000) -and ((Js "document.getElementById('st2').textContent") -eq 'Rétabli : Supprimer'))
  # la vue Corbeille
  Js "go('::corbeille', 'push')" | Out-Null
  Check "la vue Corbeille liste l'élément supprimé (nom d'origine, dossier d'origine) et la barre d'adresse dit « Corbeille »" ((Until { (Js "T().items.some(i => i.b === 't1.txt' && i.o === '$wj\\corb\\t1.txt')") -eq 'true' } 8000) -and ((Js "document.getElementById('addr').value") -eq 'Corbeille'))
  Check "la colonne de date s'appelle « Supprimé le » dans la corbeille" ((Js "document.querySelector('#lhead .lh[data-k=m]').firstChild.nodeValue.trim()") -eq 'Supprimé le')
  Js "T().sel = new Set(T().items.filter(i => i.o && i.o.startsWith('$wj\\corb')).map(i => i.n)); show(true)" | Out-Null
  Check "la sélection d'un élément de la corbeille ne vient pas d'un fichier ordinaire (identifiant de « `$I »)" ((Js "selected().length") -eq '1')
  $menu = Js "const it = selected(); return itemMenu(it, T()).filter(x => x).map(x => x[0]).join('|')"
  Check "menu d'un élément de la corbeille : Restaurer et Supprimer définitivement, rien d'autre" ($menu -eq 'Restaurer|Supprimer définitivement')
  Js "window.__spy = []; window.__real = window.__real || window.chrome.webview.postMessage; window.chrome.webview.postMessage = o => { window.__spy.push(o); if (o.cmd !== 'fs.emptybin') window.__real.call(window.chrome.webview, o); }" | Out-Null
  Js "emptyTrash()" | Out-Null
  Check "« Vider la corbeille » demande confirmation et n'envoie rien tant qu'on ne confirme pas" (((Js "!!document.getElementById('dy')") -eq 'true') -and ((Js "window.__spy.filter(m => m.cmd === 'fs.emptybin').length") -eq '0'))
  Js "document.getElementById('dn').click()" | Out-Null
  Check "dans la corbeille, copier, coller, nouveau dossier, renommer et la recherche sont sans effet" ((Js "copy(false); paste(); newFolder(); rename(); startSearch(); window.__spy.filter(m => ['fs.copy','fs.paste','fs.newfolder'].includes(m.cmd)).length + ':' + !!T().search") -eq '0:false')
  Js "window.chrome.webview.postMessage = window.__real" | Out-Null
  Key 'Enter'
  Check "Entrée restaure l'élément sélectionné : il revient dans son dossier et quitte la corbeille" ((Until { Test-Path "$work\corb\t1.txt" } 8000) -and (Until { (Js "T().items.every(i => i.b !== 't1.txt')") -eq 'true' } 8000))
  # purge d'un élément (avec confirmation) : t3 supprimé puis effacé pour de bon depuis la corbeille
  Js "go('$wj\\corb', 'push')" | Out-Null
  Until { (Js "T().path") -eq "$work\corb" } 6000 | Out-Null
  Js "post({ cmd: 'fs.delete', paths: ['$wj\\corb\\t3.txt'], tab: T().id })" | Out-Null
  Until { -not (Test-Path "$work\corb\t3.txt") } 6000 | Out-Null
  Js "go('::corbeille', 'push')" | Out-Null
  Until { (Js "T().items.some(i => i.b === 't3.txt')") -eq 'true' } 8000 | Out-Null
  Js "T().sel = new Set(T().items.filter(i => i.b === 't3.txt').map(i => i.n)); show(true)" | Out-Null
  Key 'Delete'
  Check "Suppr dans la corbeille demande « Effacer définitivement »" ((Js "document.querySelector('#layer .tb-title')?.textContent") -eq 'Effacer définitivement')
  Confirm-Dialog | Out-Null
  Check "confirmer efface l'élément de la corbeille pour de bon (il quitte la liste, ne revient pas dans le dossier)" ((Until { (Js "T().items.every(i => i.b !== 't3.txt')") -eq 'true' } 8000) -and -not (Test-Path "$work\corb\t3.txt"))
  # annuler / rétablir : renommer, nouveau dossier
  Js "go('$wj\\corb', 'push')" | Out-Null
  Until { (Js "T().path") -eq "$work\corb" } 6000 | Out-Null
  Js "post({ cmd: 'fs.rename', path: '$wj\\corb\\t4.txt', name: 't4-renomme.txt', tab: T().id })" | Out-Null
  Check "renommer (message hôte) puis Ctrl+Z : l'ancien nom revient" ((Until { Test-Path "$work\corb\t4-renomme.txt" } 6000) -and (Until { (Js "undoState.undo") -eq 'Renommer' } 4000) -and (KeyOk 'z') -and (Until { Test-Path "$work\corb\t4.txt" } 6000) -and -not (Test-Path "$work\corb\t4-renomme.txt"))
  Key 'y' '{ ctrlKey: true }'
  Check "Ctrl+Y refait le renommage" ((Until { Test-Path "$work\corb\t4-renomme.txt" } 6000) -and -not (Test-Path "$work\corb\t4.txt"))
  Js "post({ cmd: 'fs.newfolder', dir: '$wj\\corb', tab: T().id })" | Out-Null
  Check "nouveau dossier puis Ctrl+Z : le dossier va à la corbeille (annulé), Ctrl+Y le ramène" ((Until { (Get-ChildItem "$work\corb" -Directory).Count -eq 1 } 6000) -and (KeyOk 'z') -and (Until { (Get-ChildItem "$work\corb" -Directory).Count -eq 0 } 6000) -and (KeyOk 'y') -and (Until { (Get-ChildItem "$work\corb" -Directory).Count -eq 1 } 6000))
  # déposer sur la Corbeille de la barre latérale : le plan de glisser-déposer
  Js "T().sel = new Set(['t2.txt']); show(true)" | Out-Null
  Check "la barre latérale a une ligne « Corbeille » pour déposer des fichiers" ((Js "!!document.querySelector('#side [data-p=\""::corbeille\""]')") -eq 'true')
  Check "Ctrl+Z, Ctrl+Y et Maj+Suppr figurent dans l'aide des raccourcis" ((Js "KEYS.filter(s => /Ctrl\+Z|Ctrl\+Y|Shift\+Delete/.test(s.k)).length") -ge 3)
  # ménage : tout ce qui reste de l'essai dans la corbeille est effacé (la vraie corbeille de Valentin ne garde rien de nous)
  Js "post({ cmd: 'fs.delete', paths: ['$wj\\corb\\t4-renomme.txt', '$wj\\corb\\t2.txt', '$wj\\corb\\t1.txt'], tab: T().id })" | Out-Null
  Until { -not (Test-Path "$work\corb\t1.txt") } 6000 | Out-Null
  Js "go('::corbeille', 'push')" | Out-Null
  Until { (Js "T().path") -eq '::corbeille' } 6000 | Out-Null
  Start-Sleep -Milliseconds 800
  Js "post({ cmd: 'fs.purge', ids: T().items.filter(i => i.o && i.o.startsWith('$wj\\')).map(i => i.id), tab: T().id })" | Out-Null
  Check "ménage : plus aucun élément de l'essai dans la corbeille" (Until { (Js "T().items.filter(i => i.o && i.o.startsWith('$wj\\')).length") -eq '0' } 8000)
  Js "go('$wj\\petit', 'push')" | Out-Null
  Until { (Js "T().path") -eq "$work\petit" } 8000 | Out-Null

  # --- le dossier affiché disparaît : on remonte au parent ---
  Js "go('$($work.Replace('\', '\\'))\\gros', 'push')" | Out-Null
  Until { (Js "T().items.length") -eq '20000' } 30000 | Out-Null
  Remove-Item "$work\gros" -Recurse -Force
  Check "le dossier affiché est supprimé : l'Explorateur remonte au dossier parent" (Until { (Js "T().path") -eq $work } 12000)
  Js "go('$($work.Replace('\', '\\'))\\petit', 'push')" | Out-Null
  Until { (Js "T().path") -eq "$work\petit" } 8000 | Out-Null

  # --- réseau et amovibles (lot 5) ---
  Check "chemins réseau : un partage a pour parent le serveur, le serveur a pour parent « Ce PC »" ((Js "[parentOf('\\\\srv\\part\\a'), parentOf('\\\\srv\\part'), parentOf('\\\\srv')].join('|')") -eq '\\srv\part|\\srv|')
  Check "chaque disque de la barre latérale a un type (fixe, amovible, reseau, cdrom)" ((Js "places.drives.length > 0 && places.drives.every(d => ['fixe', 'amovible', 'reseau', 'cdrom'].includes(d.kind))") -eq $true)
  Js "post({ cmd: 'fs.eject', drive: 'C:\\', tab: T().id })" | Out-Null
  Check "éjecter un disque fixe est refusé avec une raison dans la barre d'état" (Until { (Js "document.getElementById('st2').textContent") -like '*amovible*' } 6000)
  Js "setSetting('net.servers', ['\\\\srv-essai'])" | Out-Null
  Check "un serveur retenu apparaît sous « Réseau » dans la barre latérale" (Until { (Js "[...document.querySelectorAll('#side .head')].map(h => h.textContent).join('|')") -like '*Réseau*' -and (Js "document.querySelector('#side [data-server]')?.dataset.p") -eq '\\srv-essai' } 6000)
  Js "forgetServer('\\\\srv-essai')" | Out-Null
  Check "« Oublier ce serveur » le retire" (Until { (Js "!!document.querySelector('#side [data-server]')") -eq $false } 6000)
  Js "go('\\\\srv-inexistant-cdx', 'push')" | Out-Null
  Check "lister les partages d'un serveur qui n'existe pas : un message clair, et le serveur n'est pas retenu" ((Until { (Js "document.getElementById('st1').textContent") -like 'Serveur introuvable*' } 40000) -and ((Js "JSON.stringify(settings['net.servers'] || [])") -eq '[]'))
  Js "go('$($work.Replace('\', '\\'))\\petit', 'push')" | Out-Null
  Until { (Js "T().path") -eq "$work\petit" } 10000 | Out-Null

  # --- vues, regroupement, colonnes (lot 6) ---
  [IO.File]::WriteAllBytes("$work\petit\pixel.png", [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=='))
  Until { (Js "T().items.some(i => i.n === 'pixel.png')") -eq $true } 8000 | Out-Null
  Js "setView('icons-m')" | Out-Null
  Check "vue « Icônes moyennes » : des cases à la place des lignes, l'en-tête des colonnes disparaît, plusieurs cases par rangée" ((Until { (Js "document.querySelectorAll('#files .fr.fc').length") -eq (Js "T().rows.length") } 4000) -and ((Js "document.getElementById('lhead').hidden") -eq $true) -and ([int](Js "T().lay.cols")) -ge 2 -and ((Js "document.getElementById('files').dataset.view") -eq 'icons-m'))
  Check "une image a sa vignette, servie par le programme (x.drive.cdx.shell)" ((Js "document.querySelector('#files .fr.fc[title=`"pixel.png`"] img')?.getAttribute('src') || ''") -like 'https://*.drive.cdx.shell/*pixel.png')
  Check "la vue est gardée pour ce dossier et devient la vue par défaut (réglages)" ((Until { (Js "(settings.views || {})['$($work.Replace('\', '\\').ToLower())\\petit']?.view") -eq 'icons-m' } 4000) -and ((Js "settings['view.default']") -eq 'icons-m'))
  Check "au clavier, ↓ descend d'une rangée entière en icônes" ((Js "lineStep()") -eq (Js "T().lay.cols"))
  Check "le rectangle de sélection couvre toutes les cases quand il englobe la liste" ((Js "rowsInRect(T(), 0, 1e9, 0, 1e9).length") -eq (Js "T().rows.length"))
  Js "setView('details')" | Out-Null
  Check "retour à « Détails » : des lignes, l'en-tête revient" ((Until { (Js "document.querySelectorAll('#files .fr:not(.fc)').length") -eq (Js "T().rows.length") } 4000) -and ((Js "document.getElementById('lhead').hidden") -eq $false))
  Js "setGroup('type')" | Out-Null
  Check "grouper par type : un en-tête par type (texte, image), le groupe des images d'un seul élément" ((Until { (Js "[...document.querySelectorAll('#files .grp')].map(g => g.textContent).join('|')") -eq 'Document texte|Image PNG' } 4000) -and ((Js "T().rows[T().rows.length - 1].n") -eq 'pixel.png'))
  Js "setGroup('date')" | Out-Null
  Check "grouper par date : tout est d'aujourd'hui" (Until { (Js "[...document.querySelectorAll('#files .grp')].map(g => g.textContent).join('|')") -eq "Aujourd’hui" } 4000)
  Js "setGroup('')" | Out-Null
  Check "sans regroupement : plus d'en-tête" (Until { (Js "document.querySelectorAll('#files .grp').length") -eq '0' } 4000)
  Js "toggleCol('t')" | Out-Null
  Check "masquer la colonne « Type » : trois en-têtes, trois cellules par ligne, réglage cols.hidden" ((Until { (Js "document.querySelectorAll('#lhead .lh').length") -eq '3' } 4000) -and ((Js "document.querySelector('#files .fr').children.length") -eq '3') -and ((Js "JSON.stringify(settings['cols.hidden'])") -eq '["t"]'))
  Js "toggleCol('t')" | Out-Null
  Check "la remontrer : quatre en-têtes" (Until { (Js "document.querySelectorAll('#lhead .lh').length") -eq '4' } 4000)
  New-Item -ItemType Directory "$work\autre" -Force | Out-Null; Set-Content "$work\autre\z.txt" 'z'
  Js "setView('icons-s'); go('$($work.Replace('\', '\\'))\\autre', 'push')" | Out-Null
  $okDef = Until { (Js "T().path") -eq "$work\autre" -and (Js "T().view") -eq 'icons-s' } 20000
  Check "un autre dossier prend la vue par défaut (petites icônes, la dernière choisie)" $okDef
  if (-not $okDef) { "    path=" + (Js "T().path") + " view=" + (Js "T().view") + " default=" + (Js "settings['view.default']") + " st1=" + (Js "document.getElementById('st1').textContent"); Get-Content "$data\journal-explorer.log" | Where-Object { $_ -match 'erreur|exception' } | Select-Object -Last 3 | ForEach-Object { "    | $_" } }
  Js "setView('details'); go('$($work.Replace('\', '\\'))\\petit', 'push')" | Out-Null
  Check "retour dans le premier dossier : sa vue à lui (petites icônes) est retrouvée" (Until { (Js "T().path") -eq "$work\petit" -and (Js "T().view") -eq 'icons-s' } 10000)
  Js "setView('details')" | Out-Null

  # --- réglages : aller-retour page → hôte → page, fichier, redémarrage ---
  Js "setSetting('essai.cle', { a: 1, b: [1, 2] })" | Out-Null
  Check "un réglage enregistré revient par l'hôte (message settings) dans la page" (Until { (Js "JSON.stringify(settings['essai.cle'])") -eq '{"a":1,"b":[1,2]}' } 6000)
  Check "il est écrit dans explorer.json (écriture atomique : pas de .tmp)" ((Until { Test-Path "$data\explorer.json" } 4000) -and -not (Test-Path "$data\explorer.json.tmp") -and ((Get-Content "$data\explorer.json" -Raw) -match 'essai.cle'))
  Js "setSetting('Mauvaise Clé', 1)" | Out-Null
  Start-Sleep -Milliseconds 600
  Check "une clé invalide est refusée par l'hôte (rien d'écrit)" (-not ((Get-Content "$data\explorer.json" -Raw) -match 'Mauvaise'))
  $app.Kill(); Start-Sleep -Seconds 1
  $app = Start-Explorer "$work\petit"; $started += $app
  Check "après un redémarrage du programme, le réglage est retrouvé" (Until { (Js "JSON.stringify(settings['essai.cle'])") -eq '{"a":1,"b":[1,2]}' } 20000)
  Check "le programme n'a pas planté" (-not $app.HasExited)
}
finally {
  $started | ForEach-Object { try { if (-not $_.HasExited) { $_.Kill() } } catch { } }
  Get-Process cdx-explorer -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $t0Run } | Stop-Process -Force
  Start-Sleep -Seconds 1
  Remove-Item $suite, $data, $work -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:CDX_PIPE_PREFIX, Env:CDX_SUITE_DIR, Env:CDX_DATA_DIR -ErrorAction SilentlyContinue
}


