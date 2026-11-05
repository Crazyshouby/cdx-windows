## Audio Linker VERSION

Diffuse le son de Windows sur plusieurs sorties à la fois, avec une interface en plusieurs fenêtres dans le style de CDX. Détail dans le [changelog](https://github.com/Crazyshouby/cdx-windows/blob/main/src/cdx-audio_linker/CHANGELOG.md).

### Installer

1. Télécharger **`cdx-audio_linker-Setup-VERSION.exe`** et le lancer (aucun droit administrateur nécessaire, installation pour votre compte).
2. Installer [VB-CABLE](https://vb-audio.com/Cable/) (gratuit) : c'est le câble audio virtuel qui sert à capturer le son du système. Le mode « Sans câble » des Options (expérimental) permet d'essayer sans.
3. Le runtime WebView2 est installé automatiquement s'il manque (déjà présent sur Windows 11).

Windows 10 (version 2004) ou Windows 11, 64 bits. `cdx-audio_linker-VERSION-portable.exe` est l'exécutable seul, sans installation.

### Vérifier le téléchargement

Les empreintes SHA-256 sont dans `cdx-audio_linker-Setup-VERSION.exe.sha256` : `sha256sum -c cdx-audio_linker-Setup-VERSION.exe.sha256`, ou `Get-FileHash` sous PowerShell.

