# Compile les programmes de la suite dans bin\ : ceux en C# (Cdx.sln), puis Audio Linker (C++, src\cdx-audio_linker : CMake + MSVC), dont
# l'exécutable est posé à côté des autres. Sans Visual Studio Build Tools, Audio Linker est sauté (avertissement) et le reste est compilé quand même.
#   pwsh -File build.ps1 [-SansAudioLinker]
param([switch]$SansAudioLinker)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet build Cdx.sln -c Release -o bin 2>&1 | Select-String ' error |Erreur\(s\)|Avertissement\(s\)'
if ($LASTEXITCODE -ne 0) { throw 'Compilation échouée' }

if (-not $SansAudioLinker) {
    $al = Join-Path $PSScriptRoot 'src\cdx-audio_linker'
    if (-not (Test-Path 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat')) {
        Write-Warning 'Audio Linker non compilé : Visual Studio Build Tools 2022 introuvable (voir src\cdx-audio_linker\README.md)'
    } else {
        # build.cmd compile dans build-dev\ (et lance ses essais) ; un exemplaire qui tourne depuis bin\ n'est pas touché tant qu'on ne le copie pas
        & cmd /c "`"$al\build.cmd`" tests" | Select-String 'error|FAILED|tests passed|tests failed'
        if ($LASTEXITCODE -ne 0) { throw 'Compilation ou essais d''Audio Linker échoués' }
        try { Copy-Item (Join-Path $al 'build-dev\cdx-audio_linker.exe') (Join-Path $PSScriptRoot 'bin\cdx-audio_linker.exe') -Force }
        catch { Write-Warning "bin\cdx-audio_linker.exe n'a pas été remplacé (il tourne ?) : $($_.Exception.Message)" }
    }
}
