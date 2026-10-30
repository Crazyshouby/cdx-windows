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
