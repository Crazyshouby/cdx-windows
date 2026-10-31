# Construit l'installeur : installer\Output\CDX-Setup-<version>.exe et son SHA-256. La version est lue dans Directory.Build.props, le seul endroit où elle vit.
#
#   .\installer\build.ps1                    # prend les programmes de bin\ (compilés par build.ps1 à la racine)
#   .\installer\build.ps1 -Bin X -Sign       # signe avec signtool (voir plus bas)
#
# -Sign demande signtool.exe dans le PATH et la variable d'environnement CDX_SIGN_ARGS : les arguments entre « signtool sign » et le fichier
# (certificat, horodatage...), par exemple '/f cert.pfx /p secret /tr http://timestamp.digicert.com /td sha256 /fd sha256'. Les exécutables
# eux-mêmes sont à signer avant.
param(
    [string]$Bin = (Join-Path $PSScriptRoot '..\bin'),
    [switch]$Sign
)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
if ($props -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') { throw 'Version introuvable dans Directory.Build.props' }
$version = $Matches[1]

$bin = (Resolve-Path $Bin).Path
foreach ($exe in 'cdx-shell', 'cdx-explorer', 'cdx-browser', 'cdx-transfers', 'cdx-repos', 'cdx-notes', 'cdx-notes-mcp', 'cdx-terminal') {
    if (-not (Test-Path (Join-Path $bin "$exe.exe"))) { throw "$exe.exe manque dans $bin : compiler d'abord (pwsh -File build.ps1)" }
}

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 introuvable (https://jrsoftware.org/isinfo.php, ou winget install JRSoftware.InnoSetup)' }

$isccArgs = @('/Q', "/DAppVersion=$version", "/DBinDir=$bin")
if ($Sign) {
    if (-not $env:CDX_SIGN_ARGS) { throw 'CDX_SIGN_ARGS est vide' }
    $isccArgs += '/DSignTool'
    # sign.cmd lance signtool avec CDX_SIGN_ARGS ; $q est l'échappement d'Inno pour un guillemet, $f le fichier.
    $isccArgs += "/Ssigntool=`$q$(Join-Path $PSScriptRoot 'sign.cmd')`$q `$f"
}
$isccArgs += (Join-Path $PSScriptRoot 'CDX.iss')

Write-Host "Version $version, programmes dans $bin"
& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) { throw "ISCC a échoué ($LASTEXITCODE)" }

$setup = Join-Path $PSScriptRoot "Output\CDX-Setup-$version.exe"
Write-Host "OK : $setup ($([math]::Round((Get-Item $setup).Length / 1KB)) Ko)"
& (Join-Path $PSScriptRoot 'checksum.ps1') -Setup $setup
