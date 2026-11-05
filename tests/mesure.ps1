# Mesure de référence des programmes « outils » de la suite (audit du 03/11/2026, lot 3) : pour chacun, le temps d'ouverture (lancement → page chargée)
# et, une fois tous ouverts, la mémoire des processus WebView2 (msedgewebview2.exe) qu'ils ont créés. À lancer avant et après chaque changement de
# performances pour savoir ce qu'il rapporte vraiment ; les chiffres dépendent de la machine, seuls les écarts comptent.
# Isolé comme les autres essais : préfixe de tube, réglages, données et coffre de notes jetables ; aucun programme en cours n'est touché.
#   pwsh -File tests\mesure.ps1 [-Bin <dossier des .exe>]
param([string]$Bin)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
$id = "$PID"
$suite = Join-Path $env:TEMP "cdx-ms-suite-$id"; $data = Join-Path $env:TEMP "cdx-ms-data-$id"; $vault = Join-Path $env:TEMP "cdx-ms-coffre-$id"; $work = Join-Path $env:TEMP "cdx-ms-work-$id"
New-Item -ItemType Directory $suite, $data, $vault, $work -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
Set-Content "$vault\Bienvenue.md" "# Bienvenue`n" -Encoding utf8
$prefix = "cdxms$id."
$env:CDX_PIPE_PREFIX = $prefix; $env:CDX_SUITE_DIR = $suite; $env:CDX_DATA_DIR = $data; $env:CDX_NOTES_DIR = $vault; $env:CDX_NO_FOCUS = '1'

function Ctl([string]$app, [string]$line) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "$prefix$app.ctl", [System.IO.Pipes.PipeDirection]::InOut)
  try { $c.Connect(300) } catch { return $null }
  try { $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c); $w.WriteLine($line); $r.ReadLine() } finally { $c.Dispose() }
}
# Les processus nés d'un processus donné (WebView2 lance son navigateur, puis ses moteurs de rendu, GPU, utilitaires).
function Tree([int[]]$roots) {
  $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name, WorkingSetSize
  $set = New-Object System.Collections.Generic.HashSet[int]; foreach ($r in $roots) { [void]$set.Add($r) }
  do { $n = $set.Count; foreach ($p in $all) { if ($set.Contains([int]$p.ParentProcessId)) { [void]$set.Add([int]$p.ProcessId) } } } while ($set.Count -ne $n)
  $all | Where-Object { $set.Contains([int]$_.ProcessId) }
}

$apps = @(
  @{ name = 'repos'; exe = 'cdx-repos' }, @{ name = 'agenda'; exe = 'cdx-agenda' }, @{ name = 'mail'; exe = 'cdx-mail' }, @{ name = 'notes'; exe = 'cdx-notes' },
  @{ name = 'transfers'; exe = 'cdx-transfers' }, @{ name = 'terminal'; exe = 'cdx-terminal' }, @{ name = 'explorer'; exe = 'cdx-explorer'; args = "`"$work`"" }
)
$started = @(); $rows = @()
try {
  foreach ($a in $apps) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = if ($a.args) { Start-Process "$Bin\$($a.exe).exe" -ArgumentList $a.args -PassThru } else { Start-Process "$Bin\$($a.exe).exe" -PassThru }
    $started += $p
    $ready = $false
    while ($sw.ElapsedMilliseconds -lt 30000) { if ((Ctl $a.name 'eval document.readyState') -eq '"complete"') { $ready = $true; break }; Start-Sleep -Milliseconds 50 }
    $rows += [pscustomobject]@{ programme = $a.name; ouverture_ms = if ($ready) { $sw.ElapsedMilliseconds } else { 'délai' } }
  }
  Start-Sleep 3   # les processus WebView2 finissent de démarrer
  $tree = Tree ($started | ForEach-Object Id)
  $wv = @($tree | Where-Object Name -eq 'msedgewebview2.exe')
  $rows | Format-Table -AutoSize | Out-String | Write-Output
  "processus msedgewebview2 : {0}, mémoire (somme des working sets) : {1:N0} Mo" -f $wv.Count, (($wv | Measure-Object WorkingSetSize -Sum).Sum / 1MB)
  "programmes de la suite (sans WebView2) : {0:N0} Mo" -f ((@($tree | Where-Object Name -ne 'msedgewebview2.exe') | Measure-Object WorkingSetSize -Sum).Sum / 1MB)
  "dossiers de données WebView2 : " + ((Get-ChildItem $data -Directory -Filter 'webview*' | ForEach-Object Name) -join ', ')
}
finally {
  $all = Tree ($started | ForEach-Object Id)
  foreach ($p in $all) { try { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue } catch { } }
  Start-Sleep 2
  Remove-Item $suite, $data, $vault, $work -Recurse -Force -ErrorAction SilentlyContinue
}
