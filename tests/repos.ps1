# Essai isolé de « Dépôts » (kdx-repos.exe) dans sa vraie fenêtre : des dépôts jetables (propre, modifié, en avance sur un remote local, vide) dans un dossier
# temporaire désigné par un repos.json de test ; la page les affiche, le détail se remplit, les filtres et la recherche agissent, un second lancement
# passe la main à la première instance. Aucune entrée simulée (la page est pilotée par le canal de test) et aucun vrai dépôt n'est lu.
#   pwsh -File tests\repos.ps1 [-Bin <dossier des .exe>]
param([string]$Bin)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
$id = "$PID"
$suite = Join-Path $env:TEMP "kdx-rp-suite-$id"; $data = Join-Path $env:TEMP "kdx-rp-data-$id"; $work = Join-Path $env:TEMP "kdx-rp-work-$id"
New-Item -ItemType Directory $suite, $data, $work -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
$prefix = "kdxrp$id."
$env:KDX_PIPE_PREFIX = $prefix; $env:KDX_SUITE_DIR = $suite; $env:KDX_DATA_DIR = $data
function Check($what, $ok) { "{0}  {1}" -f ($(if ($ok) { 'OK    ' } else { 'ÉCHEC ' })), $what }
function Ctl([string]$line) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "${prefix}repos.ctl", [System.IO.Pipes.PipeDirection]::InOut)
  try { $c.Connect(5000) } catch { return $null }
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
}
function Js([string]$code) { $r = Ctl "eval $code"; if ($r -and $r.StartsWith('"')) { $r | ConvertFrom-Json } else { $r } }
function JsInt([string]$code) { $r = Js $code; $o = 0; if ([int]::TryParse("$r", [ref]$o)) { $o } else { -1 } }   # -1 tant que la page n'a pas répondu
function Until([scriptblock]$f, [int]$ms) { $t = [Diagnostics.Stopwatch]::StartNew(); while ($t.ElapsedMilliseconds -lt $ms) { if (& $f) { return $true }; Start-Sleep -Milliseconds 150 }; return [bool](& $f) }
function G([string]$dir, [string[]]$a) { & git -c user.name=Essai -c user.email=essai@example.invalid -c commit.gpgsign=false -C $dir @a 2>&1 | Out-Null }

$started = @(); $t0Run = Get-Date
try {
  # --- des dépôts jetables ---
  $bare = "$work\origine.git"; New-Item -ItemType Directory $bare | Out-Null; G $bare @('init', '--bare', '-b', 'main')
  $propre = "$work\projets\propre"; $modif = "$work\projets\modifie"; $avance = "$work\projets\enavance"; $vide = "$work\projets\vide"
  foreach ($d in $propre, $modif, $avance, $vide) { New-Item -ItemType Directory $d -Force | Out-Null; G $d @('init', '-b', 'main') }
  foreach ($d in $propre, $modif, $avance) { Set-Content "$d\a.txt" 'un'; G $d @('add', '.'); G $d @('commit', '-m', "premier de $(Split-Path $d -Leaf)") }
  G $avance @('remote', 'add', 'origin', $bare); G $avance @('push', '-u', 'origin', 'main'); Set-Content "$avance\b.txt" 'deux'; G $avance @('add', '.'); G $avance @('commit', '-m', 'deuxième')
  Set-Content "$modif\a.txt" 'changé'; Set-Content "$modif\neuf.txt" 'neuf'
  $before = @(Get-ChildItem "$work\projets" -Recurse -Force -File | Where-Object { $_.FullName -notmatch '\\\.git\\' } | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })
  @{ roots = @("$work\projets") } | ConvertTo-Json | Set-Content "$data\repos.json" -Encoding UTF8

  $app = Start-Process "$Bin\kdx-repos.exe" -PassThru; $started += $app
  Check "la fenêtre s'ouvre et le canal de test répond" (Until { (Ctl 'state') -like 'fenêtre ouverte*' } 15000)
  Check "la recherche se termine : 4 projets lus" (Until { (JsInt "repos.size") -eq 4 -and (Js "scanning") -eq $false } 20000)
  Check "git est reconnu par la page (version)" ((Js "String(gitVersion)") -like 'git version*')
  Check "la liste affiche 4 lignes" ((JsInt "document.querySelectorAll('#rows .fr').length") -eq 4)
  $txt = Js "document.getElementById('rows').innerText"
  Check "« modifie » : 2 changements à enregistrer (1 modifié, 1 nouveau)" ($txt -match '2 à enregistrer')
  Check "« enavance » : 1 à envoyer" ($txt -match '1 à envoyer')
  Check "« propre » et « vide » : seulement sur ce PC (pas de remote)" ((Js "[...repos.values()].filter(r => !r.hasRemote).length") -eq 3)
  Check "le résumé en bas compte les projets" ((Js "document.getElementById('st1').textContent") -match '4 projets · 1 à enregistrer · 1 à envoyer · 3 seulement sur ce PC')

  # --- détail, filtres, recherche ---
  Js "document.querySelector('#rows .fr[data-p*=modifie]').click()" | Out-Null
  Check "un clic sur « modifie » remplit le détail : fichier modifié et fichier neuf" (Until { $d = Js "document.getElementById('pv').innerText"; $d -match 'a\.txt' -and $d -match 'neuf\.txt' -and $d -match 'premier de modifie' } 8000)
  Js "document.querySelector('#chips [data-f=dirty]').click()" | Out-Null
  Check "le filtre « À enregistrer » ne laisse que « modifie »" (Until { (JsInt "document.querySelectorAll('#rows .fr').length") -eq 1 } 3000)
  Js "document.querySelector('#chips [data-f=all]').click(); const f=document.getElementById('find'); f.value='vide'; f.dispatchEvent(new Event('input'))" | Out-Null
  Check "la recherche « vide » ne laisse que « vide »" (Until { (JsInt "document.querySelectorAll('#rows .fr').length") -eq 1 -and (Js "document.querySelector('#rows .fr b').textContent") -eq 'vide' } 3000)

  # --- colonnes redimensionnables (kdx-columns.js) ---
  Js "document.getElementById('find').value=''; document.getElementById('find').dispatchEvent(new Event('input')); document.querySelector('#chips [data-f=all]').click()" | Out-Null
  Check "l'en-tête a une poignée sur chaque colonne sauf la dernière" ((JsInt "document.querySelectorAll('#lhead .kdx-colrz').length") -eq 3)
  $w0 = Js "JSON.stringify([...document.querySelectorAll('#lhead > .lh')].map(b => Math.round(b.getBoundingClientRect().width)))"
  Js "(() => { const h = document.querySelector('#lhead .kdx-colrz'), x = h.getBoundingClientRect().left; for (const [t, d] of [['pointerdown', 0], ['pointermove', -60], ['pointerup', -60]]) h.dispatchEvent(new PointerEvent(t, { bubbles: true, button: 0, pointerId: 5, clientX: x + d })); })()" | Out-Null
  $w1 = Js "JSON.stringify([...document.querySelectorAll('#lhead > .lh')].map(b => Math.round(b.getBoundingClientRect().width)))"
  $a = $w0 | ConvertFrom-Json; $b = $w1 | ConvertFrom-Json
  Check "tirer le bord de « Projet » de 60 px à gauche le rétrécit de 60 et élargit la colonne suivante de 60 ($w0 -> $w1)" (($a[0] - $b[0]) -eq 60 -and ($b[1] - $a[1]) -eq 60 -and $a[2] -eq $b[2])
  Check "les lignes de la liste suivent les colonnes de l'en-tête" ((Js "(() => { const r = document.querySelector('#rows .fr'), h = document.querySelectorAll('#lhead > .lh'); return [0, 1, 2, 3].every(i => Math.abs(r.children[i].getBoundingClientRect().width - h[i].getBoundingClientRect().width) <= 1); })()") -eq 'true')
  Check "tirer un bord ne change pas le tri" ((Js "document.querySelector('#lhead .lh.on').dataset.k") -eq 'd')
  Js "document.querySelector('#lhead .kdx-colrz').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))" | Out-Null
  Check "un double-clic sur une poignée rend les largeurs d'origine" ((Js "JSON.stringify([...document.querySelectorAll('#lhead > .lh')].map(b => Math.round(b.getBoundingClientRect().width)))") -eq $w0)

  # --- la barre entre la liste et le détail (kdx-split.js) ---
  $d0 = JsInt "Math.round(document.querySelector('.col.pv').getBoundingClientRect().width)"
  Check "la barre entre la liste et le détail existe (détail de 380 px)" (((Js "document.querySelector('.col.pv').previousElementSibling.classList.contains('kdx-split')") -eq 'true') -and $d0 -eq 380)
  Js "(() => { const s = document.querySelector('.col.pv').previousElementSibling.firstElementChild, x = s.getBoundingClientRect().left; for (const [t, d] of [['pointerdown', 0], ['pointermove', -50], ['pointerup', -50]]) s.dispatchEvent(new PointerEvent(t, { bubbles: true, button: 0, pointerId: 5, clientX: x + d })); })()" | Out-Null
  Check "tirer la barre de 50 px à gauche élargit le détail (380 -> 430)" ((JsInt "Math.round(document.querySelector('.col.pv').getBoundingClientRect().width)") -eq 430)
  Js "document.querySelector('.col.pv').previousElementSibling.firstElementChild.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))" | Out-Null
  Check "un double-clic sur la barre rend la largeur d'origine (380)" ((JsInt "Math.round(document.querySelector('.col.pv').getBoundingClientRect().width)") -eq 380)

  # --- masquer un projet (réglage de l'application, enregistré dans repos.json) ---
  Js "document.getElementById('find').value=''; document.getElementById('find').dispatchEvent(new Event('input')); document.querySelector('#rows .fr[data-p*=vide]').click()" | Out-Null
  Js "document.querySelector('#pv [data-a=hide]').click()" | Out-Null
  Check "« Masquer » retire « vide » de la liste (3 lignes) et crée la puce « Masqués »" (Until { (JsInt "document.querySelectorAll('#rows .fr').length") -eq 3 -and (Js "!!document.querySelector('#chips [data-f=hid]')") -eq 'true' } 5000)
  Check "le masquage est enregistré dans repos.json" (Until { (Get-Content "$data\repos.json" -Raw) -match 'vide' } 3000)
  Check "le résumé ne compte plus le projet masqué (3 projets)" ((Js "document.getElementById('st1').textContent") -match '^3 projets')
  Js "document.querySelector('#chips [data-f=hid]').click()" | Out-Null
  Check "la puce « Masqués » montre « vide » seul" (Until { (JsInt "document.querySelectorAll('#rows .fr').length") -eq 1 -and (Js "document.querySelector('#rows .fr b').textContent") -eq 'vide' } 3000)
  Js "document.querySelector('#rows .fr').click(); document.querySelector('#pv [data-a=hide]').click()" | Out-Null
  Check "« Ne plus masquer » le remet dans la liste (4 lignes, plus de puce)" (Until { (JsInt "document.querySelectorAll('#rows .fr').length") -eq 4 -and (Js "!document.querySelector('#chips [data-f=hid]')") -eq 'true' } 5000)

  # --- lecture seule : rien n'a changé dans les dépôts ---
  $after = @(Get-ChildItem "$work\projets" -Recurse -Force -File | Where-Object { $_.FullName -notmatch '\\\.git\\' } | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })
  Check "lecture seule : aucun fichier de projet modifié, ajouté ou supprimé" (-not (Compare-Object $before $after))
  Check "lecture seule : aucun index.lock laissé" (-not (Get-ChildItem "$work\projets" -Recurse -Force -Filter index.lock -ErrorAction SilentlyContinue))

  # --- instance unique : un second lancement passe la main et s'arrête ---
  $second = Start-Process "$Bin\kdx-repos.exe" -PassThru
  Check "un second lancement s'arrête de lui-même (instance unique)" (Until { $second.HasExited } 10000)
  Check "la première instance tourne toujours" (-not $app.HasExited)

  # --- fermeture ---
  Js "document.getElementById('bx').click()" | Out-Null
  Check "le bouton fermer ferme le programme" (Until { $app.HasExited } 5000)
}
finally {
  $started | ForEach-Object { try { if (-not $_.HasExited) { $_.Kill() } } catch { } }
  Get-Process kdx-repos -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $t0Run } | Stop-Process -Force
  Start-Sleep -Seconds 1
  Get-ChildItem $work -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Attributes = 'Normal' } catch { } }
  Remove-Item $suite, $data, $work -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:KDX_PIPE_PREFIX, Env:KDX_SUITE_DIR, Env:KDX_DATA_DIR -ErrorAction SilentlyContinue
}
