# Défait la couche 1 : arrête kdx-frame, rend leur cadre d'origine aux fenêtres ouvertes, remet le fond d'écran d'avant.
#   pwsh -File restaurer.ps1
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$exe = Join-Path $here 'kdx-frame.exe'
$bak = Join-Path $here 'sauvegarde.json'

Get-Process kdx-frame -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $exe) { & $exe --reset; Write-Host "Cadre d'origine rendu aux fenêtres ouvertes" }
Remove-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name KdxFrame -ErrorAction SilentlyContinue

if (Test-Path $bak) {
    $s = Get-Content $bak -Raw | ConvertFrom-Json
    if ($null -ne $s.wallpaperStyle) { Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name WallpaperStyle -Value ([string]$s.wallpaperStyle) }
    if ($null -ne $s.tileWallpaper) { Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name TileWallpaper -Value ([string]$s.tileWallpaper) }
    Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public class KdxWp { [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(int a, int b, string c, int d); }'
    # un chemin vide = fond uni (la couleur de Control Panel\Colors\Background, noire chez Valentin)
    [void][KdxWp]::SystemParametersInfo(20, 0, [string]$s.wallpaper, 3)
    if ($null -ne $s.backgroundType) {
        Set-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers' -Name BackgroundType -Value ([int]$s.backgroundType)
    }
    Write-Host "Fond d'écran d'origine remis (chemin : '$($s.wallpaper)', type : $($s.backgroundType))"
} else {
    Write-Host 'Pas de sauvegarde.json : fond d''écran inchangé'
}
