# Arrête le shell KDX et rend à Windows sa barre des tâches (visible, état d'origine).
#   pwsh -File restaurer.ps1
# Marche aussi après un plantage du shell : l'état d'origine est dans %LOCALAPPDATA%\KdxShell\etat-barre.txt.
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\kdx-shell.exe'
Get-Process kdx-shell -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
if (Test-Path $exe) {
    & $exe --restore
    Write-Host 'Barre des tâches de Windows rendue.'
} else {
    # Sans l'exécutable : la barre est seulement masquée par ShowWindow ; la rendre suffit si l'état n'a pas été changé.
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public class KdxTb { [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n); }'
    [void][KdxTb]::ShowWindow([KdxTb]::FindWindow('Shell_TrayWnd', $null), 5)
    Write-Host "kdx-shell.exe absent : barre rendue visible. Si elle reste en masquage automatique, la régler dans Paramètres > Personnalisation > Barre des tâches."
}
