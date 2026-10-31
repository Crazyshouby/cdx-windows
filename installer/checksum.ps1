# Écrit <installeur>.sha256 (SHA-256, marqueur binaire, fin de ligne Unix : `sha256sum -c` l'accepte partout). Appelé par build.ps1, et de nouveau
# par la CI après la signature de l'installeur : l'empreinte publiée doit être celle du fichier publié.
param(
    [Parameter(Mandatory = $true)][string]$Setup
)
$ErrorActionPreference = 'Stop'
$Setup = (Resolve-Path $Setup).Path
$hash = (Get-FileHash $Setup -Algorithm SHA256).Hash.ToLower()
[IO.File]::WriteAllText("$Setup.sha256", "$hash *$(Split-Path $Setup -Leaf)`n")
Write-Host "SHA-256 : $hash"
