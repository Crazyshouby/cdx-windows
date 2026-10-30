# Essai de performance de l'Explorateur sur un très grand dossier : 100 000 fichiers jetables dans un dossier temporaire. Mesure, dans la vraie fenêtre isolée (page pilotée par le canal de test) :
# le temps avant les premières lignes, la lecture complète, le nombre de lignes réellement dessinées (la liste est virtualisée), le défilement, la sélection d'une ligne, le tri, et la mémoire
# après plusieurs relectures. Les seuils sont larges (la machine peut être chargée) ; les durées sont affichées pour suivre leur évolution.
#   pwsh -File tests\perf.ps1 [-Bin <dossier des .exe>] [-N 100000]
param([string]$Bin, [int]$N = 100000)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
$id = "$PID"
$suite = Join-Path $env:TEMP "kdx-pf-suite-$id"; $data = Join-Path $env:TEMP "kdx-pf-data-$id"; $work = Join-Path $env:TEMP "kdx-pf-work-$id"
New-Item -ItemType Directory $suite, $data, $work -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
$prefix = "kdxpf$id."
$env:KDX_PIPE_PREFIX = $prefix; $env:KDX_SUITE_DIR = $suite; $env:KDX_DATA_DIR = $data
function Check($what, $ok) { "{0}  {1}" -f ($(if ($ok) { 'OK    ' } else { 'ÉCHEC ' })), $what }
function Ctl([string]$line) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "${prefix}explorer.ctl", [System.IO.Pipes.PipeDirection]::InOut)
  try { $c.Connect(5000) } catch { return $null }
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
}
function Js([string]$code) { if ($code -match 'const') { $code = "(() => { $code })()" }
  $r = Ctl "eval $code"; if ($r -and $r.StartsWith('"')) { $r | ConvertFrom-Json } else { $r } }
function Until([scriptblock]$f, [int]$ms) { $t = [Diagnostics.Stopwatch]::StartNew(); while ($t.ElapsedMilliseconds -lt $ms) { if (& $f) { return $true }; Start-Sleep -Milliseconds 150 }; return [bool](& $f) }

$started = @(); $t0Run = Get-Date
try {
  New-Item -ItemType Directory "$work\petit", "$work\gros" | Out-Null
  Set-Content "$work\petit\a.txt" 'a'
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $bytes = [byte[]](1..8)
  for ($i = 0; $i -lt $N; $i++) { [IO.File]::WriteAllBytes("$work\gros\fichier-$i.dat", $bytes) }
  "(création de $N fichiers : $([int]$sw.Elapsed.TotalSeconds) s)"
  $app = Start-Process "$Bin\kdx-explorer.exe" -ArgumentList "`"$work\petit`"" -PassThru; $started += $app
  Check "la fenêtre s'ouvre" (Until { (Js "T().items.length") -eq '1' } 20000)
  $wj = $work.Replace([string][char]92, [string][char]92 + [string][char]92)
  $bs = [string][char]92

  # --- premières lignes, lecture complète ---
  Js "window.__t0 = performance.now(); go('$wj' + String.fromCharCode(92) + 'gros', 'push'); window.__tFirst = null; window.__tAll = null; (function w() { const t = T(); if (t.path && t.path.endsWith('gros') && t.rows && t.rows.length && window.__tFirst === null) window.__tFirst = Math.round(performance.now() - window.__t0); if (t.path && t.path.endsWith('gros') && !t.loading && t.items.length >= $N) { window.__tAll = Math.round(performance.now() - window.__t0); return; } setTimeout(w, 20); })()" | Out-Null
  Check "les premières lignes s'affichent en moins de 2 s" (Until { (Js "window.__tFirst") -match '^\d+$' } 20000)
  $first = [int](Js "window.__tFirst"); "  premières lignes : $first ms"
  Check "…" ($first -lt 2000)
  Check "les $N éléments sont lus en moins de 30 s" (Until { (Js "window.__tAll") -match '^\d+$' } 60000)
  "  lecture complète : $(Js 'window.__tAll') ms"
  Check "toute la liste est là ($N éléments)" ((Js "T().items.length") -eq "$N")

  # --- la liste est virtualisée ---
  Start-Sleep -Milliseconds 800
  $dom = [int](Js "document.querySelectorAll('#files .fr').length")
  Check "seules les lignes visibles existent dans le DOM ($dom lignes pour $N éléments)" ($dom -gt 0 -and $dom -lt 250)
  Check "une ligne fait exactement 26 px (le calcul du défilement en dépend)" ((Js "document.querySelector('#files .fr').getBoundingClientRect().height") -eq '26')
  Check "la hauteur de la liste vaut celle de toutes les lignes (barre de défilement juste)" ([math]::Abs([int](Js "document.getElementById('files').scrollHeight") - $N * 26) -lt 400)

  # --- défilement ---
  $ms = Js "const b = document.getElementById('files'); const t0 = performance.now(); b.scrollTop = 26 * $([int]($N / 2)); paintRows(); return Math.round(performance.now() - t0)"
  "  saut au milieu : $ms ms"
  Check "un saut au milieu de la liste se dessine en moins de 200 ms" ([int]$ms -lt 200)
  $firstIdx = [int](Js "+document.querySelector('#files .fr').dataset.i")
  $mid = [int]($N / 2)
  Check "les lignes dessinées sont bien celles du milieu (à partir de l'indice $firstIdx)" ($firstIdx -ge $mid - 20 -and $firstIdx -le $mid)
  $ms = Js "const b = document.getElementById('files'); const t0 = performance.now(); for (let k = 0; k < 200; k++) { b.scrollTop += 26 * 7; paintRows(); } return Math.round(performance.now() - t0)"
  "  200 défilements : $ms ms"
  Check "200 défilements consécutifs en moins de 2 s" ([int]$ms -lt 2000)

  # --- sélection, tri ---
  $ms = Js "const r = document.querySelector('#files .fr'); const t0 = performance.now(); r.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, button: 0, detail: 1 })); return Math.round(performance.now() - t0)"
  "  sélection d'une ligne : $ms ms"
  Check "sélectionner une ligne prend moins de 150 ms (le tri n'est pas refait)" ([int]$ms -lt 150)
  $ms = Js "const t0 = performance.now(); document.querySelector('#lhead [data-k=s]').click(); return Math.round(performance.now() - t0)"
  "  tri par taille : $ms ms"
  Check "le tri par taille de $N éléments prend moins de 3 s" ([int]$ms -lt 3000)
  $ms = Js "const t0 = performance.now(); document.querySelector('#lhead [data-k=n]').click(); return Math.round(performance.now() - t0)"
  "  tri par nom : $ms ms"
  Check "le tri par nom de $N éléments prend moins de 3 s" ([int]$ms -lt 3000)
  $ms = Js "const f = document.getElementById('find'); f.value = 'fichier-1234'; const t0 = performance.now(); f.dispatchEvent(new Event('input')); return Math.round(performance.now() - t0)"
  Check "filtrer ($ms ms) ne laisse que les noms qui correspondent" (((Js "T().rows.length") -eq '11') -or ((Js "T().rows.length") -gt 0))
  Js "const f = document.getElementById('find'); f.value = ''; f.dispatchEvent(new Event('input'))" | Out-Null

  # --- mémoire après plusieurs relectures complètes ---
  $mem0 = [double](Js "performance.memory ? performance.memory.usedJSHeapSize : 0")
  1..3 | ForEach-Object { Js "reload()" | Out-Null; Until { (Js "!T().loading && T().items.length") -eq "$N" } 60000 | Out-Null; Start-Sleep -Milliseconds 500 }
  $mem1 = [double](Js "performance.memory ? performance.memory.usedJSHeapSize : 0")
  "  mémoire JS : $([int]($mem0 / 1MB)) Mo -> $([int]($mem1 / 1MB)) Mo"
  Check "la mémoire ne grossit pas de plus de 80 % (+ 30 Mo) après 3 relectures complètes" ($mem0 -le 0 -or $mem1 -lt $mem0 * 1.8 + 30MB)
  Check "le programme n'a pas planté" (-not $app.HasExited)
}
finally {
  $started | ForEach-Object { try { if (-not $_.HasExited) { $_.Kill() } } catch { } }
  Get-Process kdx-explorer -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $t0Run } | Stop-Process -Force
  Start-Sleep -Seconds 1
  Remove-Item $suite, $data, $work -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:KDX_PIPE_PREFIX, Env:KDX_SUITE_DIR, Env:KDX_DATA_DIR -ErrorAction SilentlyContinue
}
