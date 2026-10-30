# Compile les programmes de la suite (kdx-shell, kdx-explorer, kdx-browser, kdx-repos) dans bin\.
#   pwsh -File build.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet build Kdx.sln -c Release -o bin 2>&1 | Select-String ' error |Erreur\(s\)|Avertissement\(s\)'
if ($LASTEXITCODE -ne 0) { throw 'Compilation échouée' }
