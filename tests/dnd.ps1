# Essai isolé du glisser-déposer de l'Explorateur dans sa vraie fenêtre : des éléments jetables dans un dossier temporaire ; la page est pilotée par des événements de pointeur
# fabriqués par le canal de test (jamais de vraie souris, rien sur la session de Valentin). Déplacer sur un dossier, copier avec Ctrl, sur un lieu de la barre latérale et sur un onglet,
# refus d'un dossier sur lui-même, Échap qui annule, la ligne fantôme et les cibles éclairées, et la sortie de la fenêtre qui confie les éléments à Windows (message fs.dragout).
#   pwsh -File tests\dnd.ps1 [-Bin <dossier des .exe>]
param([string]$Bin)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
$id = "$PID"
$suite = Join-Path $env:TEMP "kdx-dd-suite-$id"; $data = Join-Path $env:TEMP "kdx-dd-data-$id"; $work = Join-Path $env:TEMP "kdx-dd-work-$id"
New-Item -ItemType Directory $suite, $data, $work -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
$prefix = "kdxdd$id."
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

# (press : pointerdown d'abord, puis mousedown qui redessine la liste, comme un vrai pointeur ; pas de commentaire // dans le JavaScript : il est mis sur une seule ligne.)
# Les gestes, en JavaScript : sélectionner une ligne par son nom (mousedown), puis pointerdown / pointermove / pointerup à des coordonnées prises sur des éléments réels.
$helpers = @'
window.__t = {
  row: n => [...document.querySelectorAll('#files .fr')].find(r => r.querySelector('.nm span:last-child').textContent === n),
  mid: el => { const r = el.getBoundingClientRect(); return [Math.round(r.left + r.width / 2), Math.round(r.top + r.height / 2)]; },
  names: () => [...document.querySelectorAll('#files .fr .nm span:last-child')].map(e => e.textContent).join(','),
  press: (n, extra) => { const r = __t.row(n), [x, y] = __t.mid(r); r.dispatchEvent(new PointerEvent('pointerdown', Object.assign({ bubbles: true, button: 0, buttons: 1, pointerId: 9, clientX: x, clientY: y }, extra || {}))); r.dispatchEvent(new MouseEvent('mousedown', Object.assign({ bubbles: true, button: 0, detail: 1, clientX: x, clientY: y }, extra || {}))); },
  move: (x, y, extra) => document.dispatchEvent(new PointerEvent('pointermove', Object.assign({ bubbles: true, buttons: 1, pointerId: 9, clientX: x, clientY: y }, extra || {}))),
  up: (x, y, extra) => document.dispatchEvent(new PointerEvent('pointerup', Object.assign({ bubbles: true, button: 0, pointerId: 9, clientX: x, clientY: y }, extra || {}))),
};
'@
$started = @(); $t0Run = Get-Date
try {
  New-Item -ItemType Directory "$work\dossierA", "$work\dossierB", "$work\dossierA\profond" | Out-Null
  foreach ($n in 'a', 'b', 'c', 'd', 'e') { Set-Content "$work\$n.txt" $n }
  $app = Start-Process "$Bin\kdx-explorer.exe" -ArgumentList "`"$work`"" -PassThru; $started += $app
  Check "la fenêtre s'ouvre sur le dossier de l'essai" (Until { (Js "document.getElementById('addr').value") -eq $work } 20000)
  Check "la liste affiche les 5 fichiers et les 2 dossiers" (Until { (Js "document.querySelectorAll('#files .fr').length") -eq '7' } 8000)
  Js ($helpers -replace "`r?`n", ' ') | Out-Null
  Check "les gestes de l'essai sont installés" ((Js "typeof __t") -eq 'object')

  # --- glisser a.txt sur dossierA : déplacé ---
  Js "__t.press('a.txt'); const [x, y] = __t.mid(__t.row('dossierA')); __t.move(x - 40, y); __t.move(x, y)" | Out-Null
  Check "pendant le glisser : la ligne fantôme suit, la cible est éclairée, le curseur annonce un déplacement" (((Js "!!document.querySelector('.dnd-ghost') && document.querySelector('.dnd-over .nm span:last-child').textContent === 'dossierA' && document.body.classList.contains('mv')") -eq 'true'))
  Check "la ligne fantôme dit ce qui va se passer" ((Js "document.querySelector('.dnd-ghost').textContent") -like '*a.txt*Déplacer vers*dossierA*')
  Js "const [x, y] = __t.mid(__t.row('dossierA')); __t.up(x, y)" | Out-Null
  Check "déposé : a.txt est dans dossierA et plus dans le dossier d'origine" (Until { (Test-Path "$work\dossierA\a.txt") -and -not (Test-Path "$work\a.txt") } 6000)
  Check "la liste est relue (a.txt a disparu) et le fantôme aussi" (Until { (Js "__t.names()") -notlike '*a.txt*' -and (Js "!document.querySelector('.dnd-ghost')") -eq 'true' } 6000)

  # --- Ctrl : copier ---
  Js "__t.press('b.txt'); const [x, y] = __t.mid(__t.row('dossierB')); __t.move(x - 30, y, { ctrlKey: true }); __t.move(x, y, { ctrlKey: true })" | Out-Null
  Check "avec Ctrl, le curseur annonce une copie" ((Js "document.body.classList.contains('cp') && document.querySelector('.dnd-ghost').textContent.includes('Copier vers')") -eq 'true')
  Js "const [x, y] = __t.mid(__t.row('dossierB')); __t.up(x, y, { ctrlKey: true })" | Out-Null
  Check "Ctrl : b.txt est copié dans dossierB et reste à sa place" (Until { (Test-Path "$work\dossierB\b.txt") -and (Test-Path "$work\b.txt") } 6000)

  # --- plusieurs éléments à la fois (Ctrl-clic) ---
  Js "__t.press('c.txt'); __t.up(0, 0); __t.press('d.txt', { ctrlKey: true }); const [x, y] = __t.mid(__t.row('dossierB')); __t.move(x - 30, y); __t.move(x, y)" | Out-Null
  Check "deux éléments saisis : la ligne fantôme annonce « 2 éléments »" ((Js "document.querySelector('.dnd-ghost').textContent") -like '*2 éléments*')
  Js "const [x, y] = __t.mid(__t.row('dossierB')); __t.up(x, y)" | Out-Null
  Check "déposés ensemble : c.txt et d.txt sont dans dossierB et ont quitté le dossier d'origine" (Until { (Test-Path "$work\dossierB\c.txt") -and (Test-Path "$work\dossierB\d.txt") -and -not (Test-Path "$work\c.txt") -and -not (Test-Path "$work\d.txt") } 8000)

  # --- refus et annulation ---
  Js "__t.press('dossierA'); const [x, y] = __t.mid(__t.row('dossierA')); __t.move(x - 20, y); __t.move(x, y)" | Out-Null
  Check "un dossier sur lui-même : pas de cible éclairée, curseur « interdit »" ((Js "!document.querySelector('.dnd-over') && document.body.classList.contains('no')") -eq 'true')
  Js "const [x, y] = __t.mid(__t.row('dossierA')); __t.up(x, y)" | Out-Null
  Start-Sleep -Milliseconds 600
  Check "rien ne bouge : dossierA est toujours là, avec son contenu" ((Test-Path "$work\dossierA\a.txt") -and (Test-Path "$work\dossierA\profond"))
  Js "__t.press('b.txt'); const [x, y] = __t.mid(__t.row('dossierB')); __t.move(x - 30, y); __t.move(x, y); document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))" | Out-Null
  Check "Échap annule : plus de fantôme ni de curseur de glisser" ((Js "!document.querySelector('.dnd-ghost') && !document.body.classList.contains('dnd')") -eq 'true')
  Js "const [x, y] = __t.mid(__t.row('dossierB')); __t.up(x, y)" | Out-Null
  Start-Sleep -Milliseconds 600
  Check "et rien n'est déposé après l'annulation (dossierB n'a pas reçu un second b.txt)" (-not (Test-Path "$work\dossierB\b (2).txt"))

  # --- un nom déjà pris : la page demande (boîte « Éléments existants »), jamais une boîte de Windows (cachée, elle bloquait tout) ---
  Js "__t.press('b.txt'); const [x, y] = __t.mid(__t.row('dossierB')); __t.move(x - 30, y); __t.move(x, y); __t.up(x, y)" | Out-Null
  Check "b.txt existe déjà dans dossierB : la boîte « Éléments existants » s'ouvre dans la page, avec le nom" (Until { (Js "!!document.querySelector('#layer .veil') && document.querySelector('#layer .veil').textContent.includes('b.txt') && document.querySelector('#layer .veil').textContent.includes('existe déjà')") -eq 'true' } 6000)
  Check "elle offre quatre réponses : garder les deux, remplacer, ignorer, annuler" ((Js "document.querySelectorAll('#layer [data-c]').length") -eq '4')
  Js "document.querySelectorAll('#layer [data-c]')[3].click()" | Out-Null
  Start-Sleep -Milliseconds 600
  Check "Annuler : la boîte se ferme et rien n'est copié" ((Js "!document.querySelector('#layer .veil')") -eq 'true' -and -not (Test-Path "$work\dossierB\b (2).txt"))
  Js "__t.press('b.txt'); const [x, y] = __t.mid(__t.row('dossierB')); __t.move(x - 30, y); __t.move(x, y); __t.up(x, y)" | Out-Null
  Check "de nouveau la boîte" (Until { (Js "!!document.querySelector('#layer .veil')") -eq 'true' } 6000)
  Js "document.querySelectorAll('#layer [data-c]')[0].click()" | Out-Null
  Check "Garder les deux (déplacement) : « b (2).txt » arrive dans dossierB, l'ancien b.txt de dossierB reste" (Until { (Test-Path "$work\dossierB\b (2).txt") -and (Test-Path "$work\dossierB\b.txt") -and -not (Test-Path "$work\b.txt") } 6000)
  Set-Content "$work\b.txt" 'b'; Js "reload()" | Out-Null   # b.txt resert aux essais suivants
  Check "la liste est relue (b.txt est de retour)" (Until { (Js "__t.names()") -like '*b.txt*' } 6000)

  # --- un clic simple ne déclenche aucun glisser ---
  Js "__t.press('b.txt'); __t.move(__t.mid(__t.row('b.txt'))[0] + 3, __t.mid(__t.row('b.txt'))[1]); __t.up(0, 0)" | Out-Null
  Check "un petit mouvement (moins de 6 px) n'est pas un glisser" ((Js "!document.querySelector('.dnd-ghost')") -eq 'true')

  # --- barre latérale : déposer sur un lieu ---
  $sideName = Js "[...document.querySelectorAll('#side .row-item')].filter(r => r.dataset.p).map(r => r.dataset.p + '|' + r.textContent.trim()).join(';')"
  Check "la barre latérale offre des lieux où déposer" ($sideName -match '\|')

  # --- onglet : déposer sur un autre onglet ---
  Js "[...document.querySelectorAll('#tabs [data-i]')].pop().click()" | Out-Null
  Check "un second onglet s'ouvre" (Until { (Js "document.querySelectorAll('#tabs [data-i]').length") -eq '3' } 5000)
  $wj = $work.Replace([string][char]92, [string][char]92 + [string][char]92)   # les barres obliques inverses doublées, pour un texte JavaScript
  Js "go('$wj' + String.fromCharCode(92) + 'dossierB', 'push')" | Out-Null
  Check "le second onglet est sur dossierB" (Until { (Js "tabs[1].path") -eq (Join-Path $work 'dossierB') } 6000)
  Js "[...document.querySelectorAll('#tabs [data-i]')][0].click()" | Out-Null
  Check "retour sur le premier onglet" (Until { (Js "[...document.querySelectorAll('#tabs [data-i]')][0].classList.contains('on')") -eq 'true' -and (Js "__t.names()") -like '*e.txt*' } 6000)
  Js "__t.press('e.txt'); const [x, y] = __t.mid([...document.querySelectorAll('#tabs [data-i]')][1]); __t.move(x, y + 30); __t.move(x, y)" | Out-Null
  Check "au-dessus d'un onglet : il est éclairé comme cible" ((Js "!!document.querySelector('#tabs .dnd-over')") -eq 'true')
  Js "const [x, y] = __t.mid([...document.querySelectorAll('#tabs [data-i]')][1]); __t.up(x, y)" | Out-Null
  Start-Sleep -Milliseconds 800
  Check "déposé sur l'onglet : e.txt est dans dossierB (le dossier de cet onglet)" (Until { (Test-Path "$work\dossierB\e.txt") -and -not (Test-Path "$work\e.txt") } 6000)

  # --- la sortie de la fenêtre confie les éléments à Windows : le message fs.dragout part. Il est intercepté ici : la boucle de glisser-déposer de Windows attend un vrai bouton de souris
  #     enfoncé (elle bloquerait l'essai) ; ce côté-là ne s'essaie qu'à la main.
  Js "window.__spy = []; window.__real = window.chrome.webview.postMessage; window.chrome.webview.postMessage = o => { window.__spy.push(o); if (o.cmd !== 'fs.dragout') window.__real.call(window.chrome.webview, o); }" | Out-Null
  Js "__t.press('b.txt'); const [x, y] = __t.mid(__t.row('dossierB')); __t.move(x - 30, y); __t.move(-5, 40)" | Out-Null
  Check "sortir de la fenêtre : le glisser interne s'arrête (plus de fantôme)" ((Js "!document.querySelector('.dnd-ghost') && !document.body.classList.contains('dnd')") -eq 'true')
  Check "et le message fs.dragout part avec b.txt" ((Js "JSON.stringify(window.__spy.filter(m => m.cmd === 'fs.dragout').map(m => m.paths))") -like '*b.txt*')
  Js "window.chrome.webview.postMessage = window.__real" | Out-Null
  Check "le programme n'a pas planté et la page répond" ((-not $app.HasExited) -and ((Js "document.getElementById('addr').value") -eq $work))
}
finally {
  $started | ForEach-Object { try { if (-not $_.HasExited) { $_.Kill() } } catch { } }
  Get-Process kdx-explorer -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $t0Run } | Stop-Process -Force
  Start-Sleep -Seconds 1
  Remove-Item $suite, $data, $work -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:KDX_PIPE_PREFIX, Env:KDX_SUITE_DIR, Env:KDX_DATA_DIR -ErrorAction SilentlyContinue
}
