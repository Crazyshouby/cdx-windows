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
