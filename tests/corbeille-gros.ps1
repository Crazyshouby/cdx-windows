# Essai isolé : mettre à la corbeille un GROS dossier ne doit ni figer la fenêtre ni faire ramer la souris de Windows (03/11/2026 : `Mindcraft` et `MinecraftBot`, 1,2 Go chacun,
# figeaient l'Explorateur des dizaines de secondes, et son crochet souris global, posé sur le même fil, freinait chaque mouvement de souris du système).
# Un dossier jetable de -Fichiers fichiers dans un dossier temporaire, la page pilotée par le canal de test ; l'élément mis dans la vraie corbeille est retiré à la fin ($I + $R).
#   pwsh -File tests\corbeille-gros.ps1 [-Bin <dossier des .exe>] [-Fichiers 30000]
param([string]$Bin, [int]$Fichiers = 30000)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
$id = "$PID"
$suite = Join-Path $env:TEMP "cdx-cg-suite-$id"; $data = Join-Path $env:TEMP "cdx-cg-data-$id"; $work = Join-Path $env:TEMP "cdx-cg-work-$id"
New-Item -ItemType Directory $suite, $data, $work -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
$prefix = "cdxcg$id."
$env:CDX_PIPE_PREFIX = $prefix; $env:CDX_SUITE_DIR = $suite; $env:CDX_DATA_DIR = $data
function Check($what, $ok) { "{0}  {1}" -f ($(if ($ok) { 'OK    ' } else { 'ÉCHEC ' })), $what }
function Ctl([string]$line) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "${prefix}explorer.ctl", [System.IO.Pipes.PipeDirection]::InOut)
  try { $c.Connect(5000) } catch { return $null }
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
}
function Js([string]$code) { $r = Ctl "eval $code"; if ($r -and $r.StartsWith('"')) { $r | ConvertFrom-Json } else { $r } }
function Until([scriptblock]$f, [int]$ms) { $t = [Diagnostics.Stopwatch]::StartNew(); while ($t.ElapsedMilliseconds -lt $ms) { if (& $f) { return $true }; Start-Sleep -Milliseconds 150 }; return [bool](& $f) }

$started = @()
$sid = ([Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
$corbeille = Join-Path ([IO.Path]::GetPathRoot($work)) "`$Recycle.Bin\$sid"
try {
  # un dossier « de développement » : beaucoup de petits fichiers sur plusieurs niveaux
  $gros = "$work\projet\gros"
  for ($d = 0; $d -lt 100; $d++) {
    $sub = "$gros\mod$d\src"; [IO.Directory]::CreateDirectory($sub) | Out-Null
    for ($i = 0; $i -lt [Math]::Ceiling($Fichiers / 100); $i++) { [IO.File]::WriteAllText("$sub\f$i.js", 'x') }
  }
  Set-Content "$work\projet\garde.txt" 'g'
  $app = Start-Process "$Bin\cdx-explorer.exe" -ArgumentList "`"$work\projet`"" -PassThru; $started += $app
  Check "la fenêtre s'ouvre sur le dossier (2 éléments)" (Until { (Js "T().items.length") -eq '2' } 20000)
  Check "le crochet souris est posé sur un fil à lui" (Until { (Get-Content "$data\journal-explorer.log" -Raw -ErrorAction SilentlyContinue) -match 'crochet souris installé \(fil dédié\)' } 5000)

  # Suppr sur « gros », confirmation « Oui »
  Js "T().sel = new Set(['gros']); show(true)" | Out-Null
  Js "document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true, cancelable: true }))" | Out-Null
  Check "Suppr ouvre la boîte de confirmation" (Until { (Js "!!document.getElementById('dy')") -eq 'true' } 3000)
  $sw = [Diagnostics.Stopwatch]::StartNew()
  Js "document.getElementById('dy').click()" | Out-Null
  $worst = $sw.ElapsedMilliseconds; $n = 1   # l'appel qui confirme compte comme un sondage : s'il ne revient qu'à la fin, la fenêtre était figée
  while ((Test-Path $gros) -and $sw.ElapsedMilliseconds -lt 180000) {
    $t = [Diagnostics.Stopwatch]::StartNew(); $r = Js "1 + 1"; $ms = $t.ElapsedMilliseconds
    if ($r -ne '2') { $ms = 99999 }
    if ($ms -gt $worst) { $worst = $ms }; $n++
    Start-Sleep -Milliseconds 100
  }
  $total = $sw.ElapsedMilliseconds
  "        (mise à la corbeille : $total ms ; $n sondages de la page, le plus lent $worst ms)"
  Check "le dossier a quitté son emplacement" (-not (Test-Path $gros))
  Check "pendant toute la mise à la corbeille, la page a répondu en moins de 500 ms à chaque sondage" ($n -gt 0 -and $worst -lt 500)
  Check "la liste se met à jour : seul garde.txt reste" (Until { (Js "T().items.map(i => i.n).join(',')") -eq 'garde.txt' } 10000)
  Check "pas de message d'erreur dans la barre d'état" ((Js "document.getElementById('orb').className") -notmatch 'red')
  Check "« Annuler Supprimer » est proposé" (Until { (Js "undoState.undo") -eq 'Supprimer' } 5000)
  Check "aucune tâche en échec dans le journal" ((Get-Content "$data\journal-explorer.log" -Raw -ErrorAction SilentlyContinue) -notmatch 'tâche \d+ \(delete\) :')
  Check "le programme n'a pas planté" (-not $app.HasExited)
}
finally {
  $started | ForEach-Object { try { if (-not $_.HasExited) { $_.Kill() } } catch { } }   # seulement ce que l'essai a lancé
  Start-Sleep -Seconds 1
  # retirer de la vraie corbeille ce que l'essai y a mis (et rien d'autre : l'origine doit être dans $work)
  Get-ChildItem -LiteralPath $corbeille -Force -Filter '$I*' -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      $b = [IO.File]::ReadAllBytes($_.FullName); $len = [BitConverter]::ToInt32($b, 24)
      $orig = [Text.Encoding]::Unicode.GetString($b, 28, $len * 2).TrimEnd([char]0)
      if ($orig.StartsWith($work, [StringComparison]::OrdinalIgnoreCase)) {
        $r = Join-Path $corbeille ('$R' + $_.Name.Substring(2))
        if (Test-Path -LiteralPath $r) { Remove-Item -LiteralPath $r -Recurse -Force }
        Remove-Item -LiteralPath $_.FullName -Force
        "        (retiré de la corbeille : $orig)"
      }
    } catch { }
  }
  Remove-Item $suite, $data, $work -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:CDX_PIPE_PREFIX, Env:CDX_SUITE_DIR, Env:CDX_DATA_DIR -ErrorAction SilentlyContinue
}
