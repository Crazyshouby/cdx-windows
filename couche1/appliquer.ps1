# CDX-UI pour Windows, couche 1 : cadre des fenêtres + fond d'écran. Réversible : voir restaurer.ps1.
#   pwsh -File appliquer.ps1 [-Theme rouge|bleu|vert|violet|ambre|sarcelle|charbon] [-SansFond]
# Ne touche à aucun réglage système hors du fond d'écran (sauvegardé dans sauvegarde.json avant tout changement).
param(
    [ValidateSet('rouge', 'bleu', 'vert', 'violet', 'ambre', 'sarcelle', 'charbon')][string]$Theme = 'rouge',
    [switch]$SansFond
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$exe = Join-Path $here 'cdx-frame.exe'
$bak = Join-Path $here 'sauvegarde.json'

function Get-Val($path, $name) {
    $p = Get-ItemProperty -Path $path -Name $name -ErrorAction SilentlyContinue
    if ($p) { $p.$name } else { $null }
}

# 1. Sauvegarde de l'état d'origine (une seule fois : un second passage ne doit pas écraser l'original)
if (-not (Test-Path $bak)) {
    [ordered]@{
        date           = (Get-Date).ToString('s')
        wallpaper      = Get-Val 'HKCU:\Control Panel\Desktop' 'WallPaper'
        wallpaperStyle = Get-Val 'HKCU:\Control Panel\Desktop' 'WallpaperStyle'
        tileWallpaper  = Get-Val 'HKCU:\Control Panel\Desktop' 'TileWallpaper'
        backgroundType = Get-Val 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers' 'BackgroundType'
    } | ConvertTo-Json | Set-Content -Path $bak -Encoding utf8
    Write-Host "Sauvegarde écrite : $bak"
}

# 2. Le programme de cadre (compilé avec le csc de .NET Framework, présent sur tout Windows)
$src = Join-Path $here 'cdx-frame.cs'
if (-not (Test-Path $exe) -or (Get-Item $src).LastWriteTime -gt (Get-Item $exe).LastWriteTime) {
    Get-Process cdx-frame -ErrorAction SilentlyContinue | Stop-Process -Force
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc /nologo /codepage:65001 /target:exe /optimize+ "/out:$exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll $src
    if ($LASTEXITCODE -ne 0) { throw 'Compilation de cdx-frame.cs échouée' }
    Write-Host 'cdx-frame.exe compilé'
}

# 3. Le fond d'écran : fond.html rendu à la taille de l'écran principal par un Chrome (ou Edge) headless séparé
if (-not $SansFond) {
    Add-Type -AssemblyName System.Windows.Forms
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $browser = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $browser) { throw 'Ni Chrome ni Edge trouvés pour dessiner le fond : relancer avec -SansFond' }
    $png = Join-Path $here "fond-$Theme.png"
    $page = ([System.Uri](Join-Path $here 'fond.html')).AbsoluteUri + "?theme=$Theme"
    $prof = Join-Path $env:TEMP 'cdx-fond-profil'
    & $browser --headless=new --no-first-run "--user-data-dir=$prof" --hide-scrollbars --force-device-scale-factor=1 "--window-size=$($b.Width),$($b.Height)" "--screenshot=$png" $page 2>&1 | Out-Null
    if (-not (Test-Path $png)) { throw 'Le rendu du fond a échoué' }
    Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name WallpaperStyle -Value '10'   # remplir
    Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name TileWallpaper -Value '0'
    Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public class CdxWp { [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(int a, int b, string c, int d); }'
    [void][CdxWp]::SystemParametersInfo(20, 0, $png, 3)   # SPI_SETDESKWALLPAPER, mise à jour du profil + diffusion
    Write-Host "Fond d'écran posé : $png ($($b.Width) x $($b.Height))"
}

# 4. Le cadre des fenêtres : (re)lance le programme résident avec le thème demandé
Get-Process cdx-frame -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
Start-Process -FilePath $exe -ArgumentList $Theme -WindowStyle Hidden
Start-Sleep -Seconds 3
Write-Host "Cadre CDX actif (thème $Theme). Icône dans la zone de notification ; restaurer.ps1 pour tout défaire."
