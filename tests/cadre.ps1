# Essai isolé du cadre de fenêtre (kdx-frame.js sur le pont kdx-bridge.js) dans les vraies fenêtres de l'Explorateur et du navigateur : le pont est
# détecté, les boutons de la barre de titre agissent (agrandir, rétablir, fermer), les gestionnaires de pages reçoivent leurs messages.
# Isolé comme bus.ps1 (préfixe de tube, réglages et données de test) ; aucune entrée simulée (les boutons sont cliqués par le code de la page).
#   pwsh -File tests\cadre.ps1 [-Bin <dossier des .exe>]
param([string]$Bin)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
$id = "$PID"
$suite = Join-Path $env:TEMP "kdx-cf-suite-$id"; $data = Join-Path $env:TEMP "kdx-cf-data-$id"
New-Item -ItemType Directory $suite, $data -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
$prefix = "kdxcf$id."
$env:KDX_PIPE_PREFIX = $prefix; $env:KDX_SUITE_DIR = $suite; $env:KDX_DATA_DIR = $data
function Check($what, $ok) { "{0}  {1}" -f ($(if ($ok) { 'OK    ' } else { 'ÉCHEC ' })), $what }
function Ctl([string]$pipe, [string]$line) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
  try { $c.Connect(5000) } catch { return $null }
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
}
function Until([scriptblock]$f, [int]$ms) { $t = [Diagnostics.Stopwatch]::StartNew(); while ($t.ElapsedMilliseconds -lt $ms) { if (& $f) { return $true }; Start-Sleep -Milliseconds 100 }; return [bool](& $f) }
$started = @(); $t0Run = Get-Date
try {
  $ex = Start-Process "$Bin\kdx-explorer.exe" -ArgumentList $env:USERPROFILE -PassThru; $started += $ex
  $br = Start-Process "$Bin\kdx-browser.exe" -PassThru; $started += $br
  Start-Sleep 7
  foreach ($app in @(@{ n = 'Explorateur'; pipe = "${prefix}explorer.ctl"; ev = 'eval'; proc = $ex }, @{ n = 'Navigateur'; pipe = "${prefix}browser.ctl"; ev = 'evalui'; proc = $br })) {
    "$($app.n)"
    $e = $app.ev; $p = $app.pipe
    Check "le pont de la charte est détecté (kdx.host = webview2)" ((Ctl $p "$e window.kdx && kdx.host") -eq '"webview2"')
    Check "KdxFrame est chargé et la barre de titre est branchée (data-act sur les trois boutons)" ((Ctl $p "$e typeof KdxFrame.init + ' ' + document.querySelectorAll('.tb-btn[data-act]').length") -eq '"function 3"')
    Check "la barre se double-clique pour agrandir (data-dblclick = max)" ((Ctl $p "$e document.getElementById('tb').dataset.dblclick") -eq '"max"')
    $w0 = [int](Ctl $p "$e window.outerWidth")
    Ctl $p "$e document.getElementById('bmax').click()" | Out-Null
    $grown = Until { [int](Ctl $p "$e window.outerWidth") -ne $w0 } 4000
    Check "le bouton agrandir agit (la fenêtre change de taille : $w0 px -> $([int](Ctl $p "$e window.outerWidth")))" $grown
    Ctl $p "$e document.getElementById('bmax').click()" | Out-Null
    Check "un second clic la rétablit" (Until { [int](Ctl $p "$e window.outerWidth") -eq $w0 } 4000)
    Check "un gestionnaire de page branché par host.js reçoit ses messages (liste affichée ou onglets présents)" ($(if ($app.n -eq 'Explorateur') { [int](Ctl $p "$e document.querySelectorAll('#files .fr').length") -ge 3 } else { [int](Ctl $p "$e document.querySelectorAll('#tabs .tab').length") -ge 1 }))
    Ctl $p "$e document.getElementById('bx').click()" | Out-Null
    Check "le bouton fermer ferme la fenêtre : le programme s'arrête avec sa dernière fenêtre" (Until { $app.proc.HasExited } 5000)
  }
}
finally {
  $started | ForEach-Object { try { if (-not $_.HasExited) { $_.Kill() } } catch { } }
  Get-Process kdx-browser, kdx-explorer -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $t0Run } | Stop-Process -Force
  Start-Sleep 1
  Remove-Item $suite, $data -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:KDX_PIPE_PREFIX, Env:KDX_SUITE_DIR, Env:KDX_DATA_DIR -ErrorAction SilentlyContinue
}
