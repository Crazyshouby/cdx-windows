# Essai isolé du cadre de fenêtre (cdx-frame.js sur le pont cdx-bridge.js) dans les vraies fenêtres de l'Explorateur et du navigateur : le pont est
# détecté, les boutons de la barre de titre agissent (agrandir, rétablir, fermer), les gestionnaires de pages reçoivent leurs messages.
# Isolé comme bus.ps1 (préfixe de tube, réglages et données de test) ; aucune entrée simulée (les boutons sont cliqués par le code de la page).
#   pwsh -File tests\cadre.ps1 [-Bin <dossier des .exe>]
param([string]$Bin)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
$id = "$PID"
$suite = Join-Path $env:TEMP "cdx-cf-suite-$id"; $data = Join-Path $env:TEMP "cdx-cf-data-$id"
New-Item -ItemType Directory $suite, $data -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
$prefix = "cdxcf$id."
$env:CDX_PIPE_PREFIX = $prefix; $env:CDX_SUITE_DIR = $suite; $env:CDX_DATA_DIR = $data
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
  $ex = Start-Process "$Bin\cdx-explorer.exe" -ArgumentList $env:USERPROFILE -PassThru; $started += $ex
  $br = Start-Process "$Bin\cdx-browser.exe" -PassThru; $started += $br
  Start-Sleep 7
  foreach ($app in @(@{ n = 'Explorateur'; pipe = "${prefix}explorer.ctl"; ev = 'eval'; proc = $ex }, @{ n = 'Navigateur'; pipe = "${prefix}browser.ctl"; ev = 'evalui'; proc = $br })) {
    "$($app.n)"
    $e = $app.ev; $p = $app.pipe
    Check "le pont de la charte est détecté (cdx.host = webview2)" ((Ctl $p "$e window.cdx && cdx.host") -eq '"webview2"')
    Check "CdxFrame est chargé et la barre de titre est branchée (data-act sur les trois boutons)" ((Ctl $p "$e typeof CdxFrame.init + ' ' + document.querySelectorAll('.tb-btn[data-act]').length") -eq '"function 3"')
    Check "la barre se double-clique pour agrandir (data-dblclick = max)" ((Ctl $p "$e document.getElementById('tb').dataset.dblclick") -eq '"max"')
    if ($app.n -eq 'Explorateur') {
      # colonnes redimensionnables (cdx-columns.js) : poignées, bord tiré, lignes alignées, retour aux largeurs d'origine
      Check "les colonnes ont des poignées (3) et les lignes suivent l'en-tête" ((Ctl $p "$e document.querySelectorAll('#lhead .cdx-colrz').length") -eq '3')
      $c0 = Ctl $p "$e JSON.stringify([...document.querySelectorAll('#lhead > .lh')].map(b => Math.round(b.getBoundingClientRect().width)))"
      Ctl $p "$e (() => { const h = document.querySelectorAll('#lhead .cdx-colrz')[1], x = h.getBoundingClientRect().left; for (const [t, d] of [['pointerdown', 0], ['pointermove', 40], ['pointerup', 40]]) h.dispatchEvent(new PointerEvent(t, { bubbles: true, button: 0, pointerId: 5, clientX: x + d })); })()" | Out-Null
      $c1 = Ctl $p "$e JSON.stringify([...document.querySelectorAll('#lhead > .lh')].map(b => Math.round(b.getBoundingClientRect().width)))"
      $x0 = ($c0 | ConvertFrom-Json | ConvertFrom-Json); $x1 = ($c1 | ConvertFrom-Json | ConvertFrom-Json)
      Check "tirer le bord de « Modifié le » de 40 px : elle gagne 40, « Type » perd 40 ($x0 -> $x1)" (($x1[1] - $x0[1]) -eq 40 -and ($x0[2] - $x1[2]) -eq 40 -and $x0[0] -eq $x1[0])
      Ctl $p "$e document.querySelector('#lhead .cdx-colrz').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))" | Out-Null
      Check "un double-clic sur une poignée rend les largeurs d'origine" ((Ctl $p "$e JSON.stringify([...document.querySelectorAll('#lhead > .lh')].map(b => Math.round(b.getBoundingClientRect().width)))") -eq $c0)
      # la barre entre les dossiers et le contenu (cdx-split.js) ; celle de l'aperçu est cachée avec lui
      Check "la barre entre les dossiers (250 px) et le contenu existe ; celle de l'aperçu est cachée avec l'aperçu" (((Ctl $p "$e document.getElementById('side').nextElementSibling.classList.contains('cdx-split') && Math.round(document.getElementById('side').getBoundingClientRect().width)") -eq '250') -and ((Ctl $p "$e document.getElementById('pv').previousElementSibling.hidden") -eq 'true'))
      Ctl $p "$e (() => { const s = document.getElementById('side').nextElementSibling.firstElementChild, x = s.getBoundingClientRect().left; for (const [t, d] of [['pointerdown', 0], ['pointermove', 60], ['pointerup', 60]]) s.dispatchEvent(new PointerEvent(t, { bubbles: true, button: 0, pointerId: 5, clientX: x + d })); })()" | Out-Null
      Check "tirer la barre de 60 px à droite élargit la liste des dossiers (250 -> 310)" ((Ctl $p "$e Math.round(document.getElementById('side').getBoundingClientRect().width)") -eq '310')
      Check "la liste des fichiers garde sa place : les lignes suivent toujours l'en-tête" ((Ctl $p "$e (() => { const r = document.querySelector('#files .fr'), h = document.querySelectorAll('#lhead > .lh'); return !r || [1, 2, 3].every(i => Math.abs(r.children[i].getBoundingClientRect().width - h[i].getBoundingClientRect().width) <= 1); })()") -eq 'true')
      Ctl $p "$e document.getElementById('side').nextElementSibling.firstElementChild.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))" | Out-Null
      Check "un double-clic sur la barre rend la largeur d'origine (250)" ((Ctl $p "$e Math.round(document.getElementById('side').getBoundingClientRect().width)") -eq '250')
    }
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
  Get-Process cdx-browser, cdx-explorer -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $t0Run } | Stop-Process -Force
  Start-Sleep 1
  Remove-Item $suite, $data -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:CDX_PIPE_PREFIX, Env:CDX_SUITE_DIR, Env:CDX_DATA_DIR -ErrorAction SilentlyContinue
}
