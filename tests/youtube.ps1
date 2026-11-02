# Essai de « YouTube » (cdx-youtube.exe --essai) : le parcours automatique du programme lui-même (accueil, recherche, suite, fiche, suggestion, retour à
# l'accueil, et sans compte la fenêtre de connexion affichée puis recachée), sur le vrai YouTube. Aucune entrée simulée ; résultat lu dans journal-youtube.log.
#   pwsh -File tests\youtube.ps1 [-Bin <dossier des .exe>]            avec les vraies données (session Google, uBOL) : cdx-youtube ne doit pas tourner
#   pwsh -File tests\youtube.ps1 -Isole [-Bin …]                       profil neuf et anonyme dans un dossier temporaire (uBOL installé, consentement UE refusé)
#   options transmises : -Requete "…" -Video <id> -Extraction fetch
param([string]$Bin, [switch]$Isole, [string]$Requete, [string]$Video, [string]$Extraction)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
if (-not $Bin) { $Bin = "$root\bin" }
$data = Join-Path $env:LOCALAPPDATA 'CdxShell'
if ($Isole) {
  $id = "$PID"; $data = Join-Path $env:TEMP "cdx-yt-data-$id"; $suite = Join-Path $env:TEMP "cdx-yt-suite-$id"
  New-Item -ItemType Directory $data, $suite -Force | Out-Null
  $env:CDX_DATA_DIR = $data; $env:CDX_SUITE_DIR = $suite; $env:CDX_PIPE_PREFIX = "cdxyt$id."
}
$journal = Join-Path $data 'journal-youtube.log'
$avant = if (Test-Path $journal) { (Get-Content $journal).Count } else { 0 }
$a = @('--essai')
if ($Requete) { $a += '--requete', ('"' + $Requete + '"') }
if ($Video) { $a += '--video', $Video }
if ($Extraction) { $a += '--extraction', $Extraction }
$p = Start-Process "$Bin\cdx-youtube.exe" -ArgumentList ($a -join ' ') -PassThru -Wait
Get-Content $journal -Encoding utf8 | Select-Object -Skip $avant
"code de sortie : $($p.ExitCode)$(if ($p.ExitCode -eq 4) { ' (cdx-youtube tourne déjà : le fermer avant l''essai)' })"
if ($Isole) { "données de l'essai : $data (à supprimer à la main)" }
exit $p.ExitCode
