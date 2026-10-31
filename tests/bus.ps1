# Essai de bout en bout du bus et des réglages communs, ISOLÉ : l'Explorateur et le navigateur sont lancés avec un préfixe de tube, un dossier de
# réglages et un dossier de données de test (CDX_PIPE_PREFIX, CDX_SUITE_DIR, CDX_DATA_DIR) : ni le vrai bus, ni le vrai suite.json, ni le vrai profil du
# navigateur ne sont touchés, et les programmes en cours ne sont pas dérangés. Aucune entrée simulée.
#   pwsh -File tests\bus.ps1 [-Bin <dossier des .exe>]      (défaut : ..\bin-next, sinon ..\bin)
param([string]$Bin)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = if (Test-Path "$root\bin-next\cdx-browser.exe") { "$root\bin-next" } else { "$root\bin" } }
$id = "$PID"
$suite = Join-Path $env:TEMP "cdx-it-suite-$id"; $data = Join-Path $env:TEMP "cdx-it-data-$id"
New-Item -ItemType Directory $suite, $data -Force | Out-Null
New-Item "$data\debug.txt" -ItemType File | Out-Null
$prefix = "cdxit$id."
$env:CDX_PIPE_PREFIX = $prefix; $env:CDX_SUITE_DIR = $suite; $env:CDX_DATA_DIR = $data
function Check($what, $ok) { "{0}  {1}" -f ($(if ($ok) { 'OK    ' } else { 'ÉCHEC ' })), $what }
function Ctl([string]$pipe, [string]$line) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
  try { $c.Connect(5000) } catch { return $null }
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
}
function BusSend([string]$app, [string]$json) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', "$prefix$app", [System.IO.Pipes.PipeDirection]::Out)
  try { $c.Connect(3000); $b = [Text.Encoding]::UTF8.GetBytes($json + "`n"); $c.Write($b, 0, $b.Length); $c.Flush(); $true } catch { $false } finally { $c.Dispose() }
}
function Until([scriptblock]$f, [int]$ms) { $t = [Diagnostics.Stopwatch]::StartNew(); while ($t.ElapsedMilliseconds -lt $ms) { if (& $f) { return $true }; Start-Sleep -Milliseconds 100 }; return [bool](& $f) }
$started = @(); $t0Run = Get-Date
try {
  '{"version":1,"accent":"rouge"}' | Set-Content "$suite\suite.json" -Encoding ascii
  $started += Start-Process "$Bin\cdx-explorer.exe" -ArgumentList $env:USERPROFILE -PassThru
  $started += Start-Process "$Bin\cdx-browser.exe" -PassThru
  Start-Sleep 7

  "1. réglages communs : l'accent se propage en moins d'une seconde"
  Check "l'Explorateur démarre sur l'accent de suite.json (rouge)" ((Ctl "${prefix}explorer.ctl" 'eval document.documentElement.dataset.theme') -eq '"rouge"')
  Check "le navigateur aussi" ((Ctl "${prefix}browser.ctl" 'evalui document.documentElement.dataset.theme') -eq '"rouge"')
  $sw = [Diagnostics.Stopwatch]::StartNew()
  '{"version":1,"accent":"bleu"}' | Set-Content "$suite\suite.json.tmp" -Encoding ascii; Move-Item "$suite\suite.json.tmp" "$suite\suite.json" -Force
  $okE = Until { (Ctl "${prefix}explorer.ctl" 'eval document.documentElement.dataset.theme') -eq '"bleu"' } 3000
  $tE = $sw.ElapsedMilliseconds
  $okB = Until { (Ctl "${prefix}browser.ctl" 'evalui document.documentElement.dataset.theme') -eq '"bleu"' } 3000
  Check "l'Explorateur passe au bleu (en $tE ms, y compris l'aller-retour du test)" ($okE -and $tE -lt 1500)
  Check "le navigateur passe au bleu" $okB
  'pas du json {{{' | Set-Content "$suite\suite.json" -Encoding ascii; Start-Sleep 1
  Check "fichier abîmé : l'accent ne change pas, rien ne plante" ((Ctl "${prefix}explorer.ctl" 'eval document.documentElement.dataset.theme') -eq '"bleu"')
  '{"version":1,"accent":"ambre"}' | Set-Content "$suite\suite.json" -Encoding ascii
  Check "retour à un fichier valide : ambre" (Until { (Ctl "${prefix}explorer.ctl" 'eval document.documentElement.dataset.theme') -eq '"ambre"' } 3000)

  "2. bus : Explorateur"
  $n0 = (Ctl "${prefix}explorer.ctl" 'state')
  Check "open-path vers un dossier : une nouvelle fenêtre" ((BusSend 'explorer' '{"type":"open-path","from":"essai","data":{"path":"C:\\Windows"}}') -and (Until { (Ctl "${prefix}explorer.ctl" 'state') -eq '2 fenêtre(s)' } 5000))
  Check "select : la fenêtre s'ouvre sur le dossier parent, élément à sélectionner transmis" ((BusSend 'explorer' '{"type":"select","from":"essai","data":{"path":"C:\\Windows\\win.ini"}}') -and (Until { (Ctl "${prefix}explorer.ctl" 'state') -eq '3 fenêtre(s)' } 5000))
  BusSend 'explorer' '{"type":"open-path","from":"essai","data":{"path":"..\\relatif"}}' | Out-Null
  BusSend 'explorer' '{"type":"open-path","from":"essai","data":{"path":"C:\\n\\existe\\pas\\du\\tout"}}' | Out-Null
  BusSend 'explorer' '{"type":"ouvre-ce-programme","from":"essai","data":{"path":"calc.exe"}}' | Out-Null
  BusSend 'explorer' 'pas du json' | Out-Null
  Start-Sleep 2
  Check "chemin relatif, chemin inexistant, type inconnu, ligne illisible : refusés, toujours 3 fenêtres" ((Ctl "${prefix}explorer.ctl" 'state') -eq '3 fenêtre(s)')
  Check "le refus est noté dans le journal" ((Get-Content "$data\journal-explorer.log" -Raw) -match 'refusé')

  "3. bus : navigateur"
  Check "open-url https : un onglet de plus" ((BusSend 'browser' '{"type":"open-url","from":"essai","data":{"url":"https://example.org/"}}') -and (Until { (Ctl "${prefix}browser.ctl" 'state') -match 'example.org' } 8000))
  $tabs = { @(((Ctl "${prefix}browser.ctl" 'state') | ConvertFrom-Json).tabs).Count }
  $t0 = & $tabs
  BusSend 'browser' '{"type":"open-url","from":"essai","data":{"url":"javascript:alert(1)"}}' | Out-Null
  BusSend 'browser' '{"type":"open-url","from":"essai","data":{"url":"file:///C:/Windows/win.ini"}}' | Out-Null
  BusSend 'browser' '{"type":"open-url","from":"essai","data":{"url":"ftp://example.org/x"}}' | Out-Null
  BusSend 'browser' ('{"type":"open-url","from":"essai","data":{"url":"https://example.org/' + ('a' * 2100) + '"}}') | Out-Null
  BusSend 'browser' '{"type":"notify","from":"essai","data":{"title":"pas pour moi"}}' | Out-Null
  Start-Sleep 2
  Check "javascript:, file:, ftp:, adresse de plus de 2000 caractères, type d'un autre programme : refusés, aucun onglet de plus" ((& $tabs) -eq $t0)

  "4. seconde instance : relais par le bus"
  $p = Start-Process "$Bin\cdx-browser.exe" -ArgumentList 'https://example.com/' -PassThru; Start-Sleep 6
  Check "la seconde instance s'arrête et l'adresse arrive dans le premier navigateur" ($p.HasExited -and ((Ctl "${prefix}browser.ctl" 'state') -match 'example.com'))
  $p = Start-Process "$Bin\cdx-explorer.exe" -ArgumentList $env:TEMP -PassThru; Start-Sleep 5
  Check "idem pour l'Explorateur (4 fenêtres)" ($p.HasExited -and (Ctl "${prefix}explorer.ctl" 'state') -eq '4 fenêtre(s)')

  "6. synergies : fichiers locaux dans le navigateur"
  $page = Join-Path $env:TEMP "cdx-it-page-$id.html"; "<!doctype html><title>Page locale CDX</title><p>bonjour</p>" | Set-Content $page -Encoding utf8
  $exe = Join-Path $env:TEMP "cdx-it-$id.exe"; "x" | Set-Content $exe
  $t0 = & $tabs
  Check "open-file (page HTML locale) : un onglet file:///, titre de la page" ((BusSend 'browser' ('{"type":"open-file","from":"essai","data":{"path":"' + ($page -replace '\\', '\\\\') + '"}}')) -and (Until { (Ctl "${prefix}browser.ctl" 'state') -match 'Page locale CDX' } 8000))
  $t1 = & $tabs
  BusSend 'browser' ('{"type":"open-file","from":"essai","data":{"path":"' + ($exe -replace '\\', '\\\\') + '"}}') | Out-Null
  BusSend 'browser' '{"type":"open-file","from":"essai","data":{"path":"..\\relatif.html"}}' | Out-Null
  BusSend 'browser' '{"type":"open-file","from":"essai","data":{"path":"C:\\n\\existe\\pas.html"}}' | Out-Null
  Start-Sleep 2
  Check ".exe, chemin relatif, fichier inexistant : refusés" ((& $tabs) -eq $t1)
  Ctl "${prefix}explorer.ctl" ('eval post({cmd:"fs.open", path:' + ($page | ConvertTo-Json) + '})') | Out-Null
  Check "double-clic sur une page HTML dans l'Explorateur : elle s'ouvre dans le navigateur (un onglet de plus)" (Until { (& $tabs) -eq $t1 + 1 } 8000)
  Remove-Item $page, $exe -Force -ErrorAction SilentlyContinue

  "7. synergies : le destinataire absent est lancé (apps.json)"
  Get-Process cdx-browser -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $t0Run } | Stop-Process -Force; Start-Sleep 2
  $page2 = Join-Path $env:TEMP "cdx-it-page2-$id.html"; "<!doctype html><title>Page lancee par l'Explorateur</title>" | Set-Content $page2 -Encoding utf8
  Check "navigateur arrêté : plus de processus" (-not (Get-Process cdx-browser -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $t0Run }))
  Ctl "${prefix}explorer.ctl" ('eval post({cmd:"fs.open", path:' + ($page2 | ConvertTo-Json) + '})') | Out-Null
  Check "double-clic sur la page : l'Explorateur lance cdx-browser.exe (chemin lu dans apps.json) et la page s'y ouvre" (Until { (Ctl "${prefix}browser.ctl" 'state') -match 'lancee par' } 15000)
  Remove-Item $page2 -Force -ErrorAction SilentlyContinue

  "5. sécurité du tube"
  # les droits du tube (utilisateur courant seulement) sont vérifiés par cdx-shell --selftest-bus, sur un tube créé de la même façon
  # le refus des connexions distantes (PIPE_REJECT_REMOTE_CLIENTS, par défaut avec .NET) ne se teste pas depuis la même machine : par le nom d'hôte, Windows reste en local
}
finally {
  $started | ForEach-Object { try { if (-not $_.HasExited) { $_.Kill() } } catch { } }
  Get-Process cdx-browser, cdx-explorer -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $t0Run } | Stop-Process -Force
  Get-Process cdx-explorer, cdx-browser -ErrorAction SilentlyContinue | Where-Object { $_.Id -in $started.Id } | Stop-Process -Force
  Start-Sleep 1
  Remove-Item $suite, $data -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:CDX_PIPE_PREFIX, Env:CDX_SUITE_DIR, Env:CDX_DATA_DIR -ErrorAction SilentlyContinue
}

