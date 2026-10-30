# Essai isolé de l'Explorateur dans sa vraie fenêtre (le socle : liste lue en arrière-plan, réglages persistants) ; il grandira avec les fonctionnalités (liste vivante, vues...).
# Des dossiers jetables dans un dossier temporaire, la page pilotée par le canal de test ; rien n'est lu ni écrit hors de ce dossier et du dossier de données de l'essai.
#   pwsh -File tests\explorer.ps1 [-Bin <dossier des .exe>]
param([string]$Bin)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
$id = "$PID"
$suite = Join-Path $env:TEMP "kdx-ex-suite-$id"; $data = Join-Path $env:TEMP "kdx-ex-data-$id"; $work = Join-Path $env:TEMP "kdx-ex-work-$id"
New-Item -ItemType Directory $suite, $data, $work -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
$prefix = "kdxex$id."
$env:KDX_PIPE_PREFIX = $prefix; $env:KDX_SUITE_DIR = $suite; $env:KDX_DATA_DIR = $data
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
function Start-Explorer([string]$dir) { Start-Process "$Bin\kdx-explorer.exe" -ArgumentList "`"$dir`"" -PassThru }

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

  # --- éléments cachés et extensions (menu Affichage) ---
  Set-Content "$work\petit\secret.txt" 's'; (Get-Item "$work\petit\secret.txt").Attributes = 'Hidden'
  Start-Sleep -Milliseconds 800
  Check "un fichier caché n'est pas listé par défaut" ((Js "T().rows.map(r => r.n).join(',')") -notlike '*secret.txt*')
  Js "document.getElementById('cView').click()" | Out-Null
  Check "le bouton Affichage ouvre un menu avec « Éléments cachés » et « Extensions des fichiers »" (Until { (Js "[...document.querySelectorAll('.menu .menu-item')].map(e => e.textContent).join('|')") -match 'Éléments cachés.*Extensions des fichiers' } 4000)
  Js "[...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.includes('Éléments cachés')).click()" | Out-Null
  Check "cocher « Éléments cachés » relit le dossier : secret.txt apparaît, estompé" (Until { (Js "T().rows.map(r => r.n).join(',')") -like '*secret.txt*' -and (Js "!!document.querySelector('#files .fr.hid')") -eq 'true' } 8000)
  Check "le réglage est enregistré (show.hidden)" (Until { (Get-Content "$data\explorer.json" -Raw) -match '"show.hidden":true' } 4000)
  Js "document.getElementById('cView').click()" | Out-Null
  Js "[...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.includes('Extensions')).click()" | Out-Null
  Check "décocher « Extensions des fichiers » : a.txt s'affiche « a »" (Until { (Js "document.querySelector('#files .fr .nm span:last-child').textContent") -eq 'a' } 4000)
  Check "le nom réel ne change pas (a.txt, pour les opérations)" ((Js "T().rows[0].n") -eq 'a.txt')
  Js "rename('a.txt')" | Out-Null
  Check "le renommage d'un fichier sans extension affichée édite « a » (sans .txt)" ((Js "document.querySelector('#files input').value") -eq 'a')
  Js "const i = document.querySelector('#files input'); i.value = 'z'; i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))" | Out-Null
  Check "valider « z » renomme en z.txt (l'extension masquée est conservée)" (Until { Test-Path "$work\petit\z.txt" } 6000)
  Js "document.getElementById('cView').click(); [...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.includes('Extensions')).click()" | Out-Null
  Js "document.getElementById('cView').click(); [...document.querySelectorAll('.menu .menu-item')].find(e => e.textContent.includes('cachés')).click()" | Out-Null
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
  Js "document.getElementById('cSel').click()" | Out-Null
  Check "le bouton Sélection ouvre un menu (tout, rien, inverser)" (Until { (Js "[...document.querySelectorAll('.menu .menu-item')].map(e => e.textContent).join('|')") -match 'Tout sélectionner.*Ne rien sélectionner.*Inverser' } 4000)
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

  # --- le dossier affiché disparaît : on remonte au parent ---
  Js "go('$($work.Replace('\', '\\'))\\gros', 'push')" | Out-Null
  Until { (Js "T().items.length") -eq '20000' } 30000 | Out-Null
  Remove-Item "$work\gros" -Recurse -Force
  Check "le dossier affiché est supprimé : l'Explorateur remonte au dossier parent" (Until { (Js "T().path") -eq $work } 12000)
  Js "go('$($work.Replace('\', '\\'))\\petit', 'push')" | Out-Null
  Until { (Js "T().path") -eq "$work\petit" } 8000 | Out-Null

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
  Get-Process kdx-explorer -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $t0Run } | Stop-Process -Force
  Start-Sleep -Seconds 1
  Remove-Item $suite, $data, $work -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:KDX_PIPE_PREFIX, Env:KDX_SUITE_DIR, Env:KDX_DATA_DIR -ErrorAction SilentlyContinue
}
