# Essai de la suite par les canaux de contrôle (aucune entrée simulée) : le shell lance l'Explorateur et le navigateur comme programmes à part, la
# seconde ouverture est relayée à la première instance, et l'arrêt brutal de l'un ou de l'autre n'emporte pas le shell.
# Prérequis : %LOCALAPPDATA%\CdxShell\debug.txt, puis (re)lancer bin\cdx-shell.exe (il ouvre alors « CdxShellCtl »). Le test ferme ce qu'il ouvre ;
# supprimer debug.txt et relancer le shell ensuite. Attention : il manipule le shell en cours.
$ErrorActionPreference = 'Continue'
function Ctl([string]$pipe, [string]$line) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
  try { $c.Connect(5000) } catch { return $null }
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine($line); $res = $r.ReadLine(); $c.Dispose(); $res
}
function Check($what, $ok) { "{0}  {1}" -f ($(if ($ok) { 'OK    ' } else { 'ÉCHEC ' })), $what }
function Count($n) { @(Get-Process $n -ErrorAction SilentlyContinue).Count }
$dir = Split-Path $PSScriptRoot
$enc = { param($p) [Uri]::EscapeDataString($p) }
# Garde-fou : ce test tue les navigateurs et Explorateurs CDX en cours (étape 4). Il refuse de partir s'il en existe déjà qu'il n'a pas lancés :
# le 01/11/2026, un essai lancé sans y penser a fermé le navigateur de l'utilisateur et ses onglets.
$deja = @(Get-Process cdx-browser, cdx-explorer -ErrorAction SilentlyContinue)
if ($deja.Count) { throw "suite.ps1 : fermez d'abord $($deja.ProcessName -join ', ') (pid $($deja.Id -join ', ')) : ce test les tuerait." }

"1. le shell répond"
Check "le shell tient son canal de contrôle (debug.txt présent au lancement)" ((Ctl 'cdx.shell.ctl' 'audio') -ne $null)
Check "la barre exécute du JavaScript" ((Ctl 'cdx.shell.ctl' 'evalbar 1+1') -eq '2')

"2. l'Explorateur est un programme à part"
Ctl 'cdx.shell.ctl' ('launch cdx:explorer?' + (& $enc $dir)) | Out-Null; Start-Sleep 5
Check "cdx-explorer.exe tourne" ((Count 'cdx-explorer') -eq 1)
Check "il affiche une fenêtre" ((Ctl 'cdx.explorer.ctl' 'state') -eq '1 fenêtre(s)')
$rows = Ctl 'cdx.explorer.ctl' 'eval document.querySelectorAll("#files .fr").length'
Check "la page liste le contenu du dossier demandé (éléments : $rows)" ([int]$rows -ge 3)
Ctl 'cdx.shell.ctl' ('launch cdx:explorer?' + (& $enc (Join-Path $dir 'src'))) | Out-Null; Start-Sleep 4
Check "seconde ouverture : toujours un seul programme" ((Count 'cdx-explorer') -eq 1)
Check "seconde ouverture : une deuxième fenêtre dans le même programme" ((Ctl 'cdx.explorer.ctl' 'state') -eq '2 fenêtre(s)')

"3. le navigateur est un programme à part"
Ctl 'cdx.shell.ctl' 'launch cdx:navigateur' | Out-Null; Start-Sleep 7
Check "cdx-browser.exe tourne" ((Count 'cdx-browser') -eq 1)
Check "il répond sur son canal" ((Ctl 'cdx.browser.ctl' 'state') -like '*tabs*')
Ctl 'cdx.shell.ctl' 'launch cdx:navigateur?https%3A%2F%2Fexample.org%2F' | Out-Null; Start-Sleep 5
Check "seconde ouverture : toujours un seul programme" ((Count 'cdx-browser') -eq 1)
$st = (Ctl 'cdx.browser.ctl' 'state') | ConvertFrom-Json
Check "seconde ouverture : l'adresse arrive dans un onglet du même programme" (@($st.tabs | Where-Object { $_.Url -like 'https://example.org*' }).Count -ge 1)

"4. isolation : un arrêt brutal n'emporte pas le shell"
$shellPid = (Get-Process cdx-shell).Id
Get-Process cdx-browser | Stop-Process -Force; Start-Sleep 2
Check "navigateur tué : le shell vit toujours" ((Get-Process cdx-shell -ErrorAction SilentlyContinue).Id -eq $shellPid)
Check "navigateur tué : la barre répond encore" ((Ctl 'cdx.shell.ctl' 'evalbar 2+2') -eq '4')
Check "navigateur tué : l'Explorateur vit toujours" ((Count 'cdx-explorer') -eq 1)
Get-Process cdx-explorer | Stop-Process -Force; Start-Sleep 2
Check "Explorateur tué : le shell vit toujours et la barre répond" (((Get-Process cdx-shell -ErrorAction SilentlyContinue).Id -eq $shellPid) -and ((Ctl 'cdx.shell.ctl' 'evalbar 3+3') -eq '6'))
Ctl 'cdx.shell.ctl' ('launch cdx:explorer?' + (& $enc $dir)) | Out-Null; Start-Sleep 5
Check "après la mort de l'Explorateur, le shell sait le relancer" ((Count 'cdx-explorer') -eq 1)
Get-Process cdx-explorer -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process cdx-browser -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Content "$env:LOCALAPPDATA\CdxShell\journal.log" | Select-Object -Last 6

