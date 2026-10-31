# Compile (si besoin) et lance le shell CDX : barre des tâches et menu Démarrer de la charte, à la place de ceux de Windows.
#   pwsh -File lancer.ps1 [rouge|bleu|vert|violet|ambre|sarcelle|charbon]
# Sans argument, le shell suit l'accent de la suite (%APPDATA%\CDX\suite.json) ; un thème donné ici le fixe pour ce lancement.
param([ValidateSet('', 'rouge', 'bleu', 'vert', 'violet', 'ambre', 'sarcelle', 'charbon')][string]$Theme = '')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$exe = Join-Path $PSScriptRoot 'bin\cdx-shell.exe'
$newest = Get-ChildItem src -Recurse -Include *.cs, *.csproj | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not (Test-Path $exe) -or $newest.LastWriteTime -gt (Get-Item $exe).LastWriteTime) {
    # Seul le shell est arrêté : l'Explorateur et le navigateur en cours (peut-être avec une session ouverte) gardent leurs fichiers ; fermez-les avant de recompiler si build.ps1 le demande.
    Get-Process cdx-shell -ErrorAction SilentlyContinue | Stop-Process -Force
    & "$PSScriptRoot\build.ps1"
}
Get-Process cdx-shell -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
if ($Theme) { Start-Process $exe -ArgumentList $Theme } else { Start-Process $exe }
Write-Host "Shell CDX lancé ($(if ($Theme) { "thème $Theme" } else { "accent de la suite" })). Journaux : $env:LOCALAPPDATA\CdxShell\journal*.log ; restaurer.ps1 pour tout rendre à Windows."
