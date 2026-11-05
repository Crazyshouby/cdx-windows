; Installeur de la suite CDX (Inno Setup 6.1+). À construire avec installer\build.ps1, qui lit la version dans Directory.Build.props :
;   ISCC /DAppVersion=0.1.0 /DBinDir=..\bin CDX.iss
;
; Par utilisateur par défaut (aucun droit d'administrateur : la suite n'en a pas besoin), avec le choix « pour tous les utilisateurs » de l'assistant.
; Chaque programme est un composant : on installe la barre seule, ou seulement l'Explorateur et le Terminal, etc.
; Un essai silencieux ne doit pas arrêter la suite en cours : /NOSTOP=1 (installation comme désinstallation).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef BinDir
  #define BinDir "..\bin"
#endif
#define WebDir "..\web"
#define AppName "CDX"
#define AppUrl "https://github.com/Crazyshouby/cdx-windows"
#define ShellExe "cdx-shell.exe"

[Setup]
; Ne jamais changer ce GUID : c'est par lui qu'une mise à jour retrouve la version qu'elle remplace.
AppId={{7C1D0E52-9B3A-4F6E-8D21-5A0C4B7E9F13}
AppName={#AppName}
SetupIconFile=..\icons\cdx.ico
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Valentin Large
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=Installation de la suite {#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 11 : la barre, la zone de notification et Alt+Tab de la suite sont écrits contre lui.
MinVersion=10.0.22000
UninstallDisplayIcon={app}\{#ShellExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=Output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
; Les programmes en cours sont arrêtés par [Code] (la barre doit rendre celle de Windows avant), pas par le Restart Manager.
CloseApplications=no
RestartApplications=no
#ifdef SignTool
SignTool=signtool
SignedUninstaller=yes
#endif

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Types]
Name: "full"; Description: "Complète (toute la suite)"
Name: "custom"; Description: "Personnalisée"; Flags: iscustom

[Components]
Name: "shell"; Description: "Barre des tâches et menu Démarrer CDX (remplacent ceux de Windows tant que la barre tourne)"; Types: full
Name: "explorer"; Description: "Explorateur de fichiers"; Types: full
Name: "browser"; Description: "Navigateur"; Types: full
Name: "repos"; Description: "Dépôts (vos projets git, en lecture seule)"; Types: full
Name: "notes"; Description: "Notes (coffre Markdown) et son serveur MCP pour Claude"; Types: full
Name: "terminal"; Description: "Terminal"; Types: full
Name: "agenda"; Description: "Agenda (calendrier et tâches du compte Google)"; Types: full
Name: "mail"; Description: "Courrier (boîte Gmail du compte Google)"; Types: full
Name: "youtube"; Description: "YouTube (accueil, recherche, vidéos ; page YouTube cachée avec uBlock Origin Lite)"; Types: full
Name: "audio_linker"; Description: "Audio Linker (le son du PC sur plusieurs sorties à la fois ; demande VB-CABLE)"; Types: full

[Tasks]
Name: "startup"; Description: "Lancer la barre {#AppName} à l'ouverture de session Windows"; Components: shell; Flags: unchecked

[Files]
; --- commun à tous les programmes : la bibliothèque hôte, WebView2, les pages ---
Source: "{#BinDir}\Cdx.Host.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\Cdx.Host.deps.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\Microsoft.Web.WebView2.*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\runtimes\win-x64\native\WebView2Loader.dll"; DestDir: "{app}\runtimes\win-x64\native"; Flags: ignoreversion
Source: "{#WebDir}\*"; DestDir: "{app}\web"; Excludes: "_test-*"; Flags: ignoreversion recursesubdirs createallsubdirs
; --- un programme par composant ---
Source: "{#BinDir}\cdx-shell.*"; DestDir: "{app}"; Excludes: "*.pdb"; Components: shell; Flags: ignoreversion
Source: "{#BinDir}\NAudio.*.dll"; DestDir: "{app}"; Components: shell; Flags: ignoreversion
Source: "{#BinDir}\cdx-explorer.*"; DestDir: "{app}"; Excludes: "*.pdb"; Components: explorer; Flags: ignoreversion
Source: "{#BinDir}\cdx-browser.*"; DestDir: "{app}"; Excludes: "*.pdb"; Components: browser; Flags: ignoreversion
Source: "{#BinDir}\cdx-transfers.*"; DestDir: "{app}"; Excludes: "*.pdb"; Components: explorer browser; Flags: ignoreversion
Source: "{#BinDir}\cdx-repos.*"; DestDir: "{app}"; Excludes: "*.pdb"; Components: repos; Flags: ignoreversion
Source: "{#BinDir}\cdx-notes.*"; DestDir: "{app}"; Excludes: "*.pdb"; Components: notes; Flags: ignoreversion
Source: "{#BinDir}\cdx-notes-mcp.*"; DestDir: "{app}"; Excludes: "*.pdb"; Components: notes; Flags: ignoreversion
Source: "{#BinDir}\Cdx.Vault.dll"; DestDir: "{app}"; Components: notes; Flags: ignoreversion
Source: "{#BinDir}\Cdx.Vault.deps.json"; DestDir: "{app}"; Components: notes; Flags: ignoreversion
Source: "{#BinDir}\cdx-terminal.*"; DestDir: "{app}"; Excludes: "*.pdb"; Components: terminal; Flags: ignoreversion
Source: "{#BinDir}\cdx-agenda.*"; DestDir: "{app}"; Excludes: "*.pdb"; Components: agenda; Flags: ignoreversion
Source: "{#BinDir}\cdx-mail.*"; DestDir: "{app}"; Excludes: "*.pdb"; Components: mail; Flags: ignoreversion
Source: "{#BinDir}\cdx-youtube.*"; DestDir: "{app}"; Excludes: "*.pdb"; Components: youtube; Flags: ignoreversion
; Audio Linker (C++, compilé par build.ps1 dans bin\) : un seul exécutable autonome
Source: "{#BinDir}\cdx-audio_linker.exe"; DestDir: "{app}"; Components: audio_linker; Flags: ignoreversion

[InstallDelete]
; Pilote, retiré de la suite le 03/11/2026 : une mise à jour par-dessus retire ses fichiers, sa page et son raccourci (ses données, elles, restent).
Type: files; Name: "{app}\cdx-pilote.*"
Type: files; Name: "{app}\cdx-pilote-mcp.*"
Type: filesandordirs; Name: "{app}\web\pilote"
Type: files; Name: "{autoprograms}\{#AppName}\Pilote {#AppName}.lnk"

[Icons]
Name: "{autoprograms}\{#AppName}\Barre {#AppName}"; Filename: "{app}\{#ShellExe}"; Components: shell
Name: "{autoprograms}\{#AppName}\Rendre la barre de Windows"; Filename: "{app}\{#ShellExe}"; Parameters: "--restore"; Components: shell
Name: "{autoprograms}\{#AppName}\Explorateur {#AppName}"; Filename: "{app}\cdx-explorer.exe"; Components: explorer
Name: "{autoprograms}\{#AppName}\Navigateur {#AppName}"; Filename: "{app}\cdx-browser.exe"; Components: browser
Name: "{autoprograms}\{#AppName}\Dépôts {#AppName}"; Filename: "{app}\cdx-repos.exe"; Components: repos
Name: "{autoprograms}\{#AppName}\Notes {#AppName}"; Filename: "{app}\cdx-notes.exe"; Components: notes
Name: "{autoprograms}\{#AppName}\Terminal {#AppName}"; Filename: "{app}\cdx-terminal.exe"; Components: terminal
Name: "{autoprograms}\{#AppName}\Agenda {#AppName}"; Filename: "{app}\cdx-agenda.exe"; Components: agenda
Name: "{autoprograms}\{#AppName}\Courrier {#AppName}"; Filename: "{app}\cdx-mail.exe"; Components: mail
Name: "{autoprograms}\{#AppName}\YouTube {#AppName}"; Filename: "{app}\cdx-youtube.exe"; Components: youtube
Name: "{autoprograms}\{#AppName}\Audio Linker"; Filename: "{app}\cdx-audio_linker.exe"; Components: audio_linker

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#ShellExe}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#ShellExe}"; Description: "Lancer la barre {#AppName} maintenant"; Components: shell; Flags: nowait postinstall skipifsilent

[Code]
const
  WebView2ClientKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Bootstrapper = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  DotnetKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  DotnetInstaller = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  PrereqPage: TOutputMsgWizardPage;
  DownloadPage: TDownloadWizardPage;
  NeedWebView2, NeedDotnet: Boolean;

// /NOSTOP=1 : les essais (CI, installation silencieuse de contrôle) ne touchent pas à la suite qui tourne.
function NoStop: Boolean;
begin
  Result := ExpandConstant('{param:NOSTOP|0}') = '1';
end;

function HasWebView2Version(RootKey: Integer; const SubKey: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

// Le runtime Evergreen s'enregistre par machine (vue 32 bits) ou par utilisateur.
function WebView2Installed: Boolean;
begin
  Result := HasWebView2Version(HKLM32, WebView2ClientKey) or HasWebView2Version(HKLM64, WebView2ClientKey) or
            HasWebView2Version(HKCU, WebView2ClientKey);
end;

// Le runtime .NET 8 « Desktop » (WinForms) : une valeur « 8.x.y » sous la clé des versions installées.
function DotnetDesktop8Installed: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM64, DotnetKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 2) = '8.' then Result := True;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if Progress = ProgressMax then Log(Format('Téléchargé %s', [FileName]));
  Result := True;
end;

procedure InitializeWizard;
begin
  NeedWebView2 := not WebView2Installed;
  NeedDotnet := not DotnetDesktop8Installed;
  PrereqPage := CreateOutputMsgPage(wpWelcome, 'Avant de commencer', 'Ce dont la suite CDX a besoin',
    'CDX remplace la barre des tâches et le menu Démarrer de Windows, et ajoute un Explorateur, un navigateur, un terminal, des notes et la vue de vos projets git. Il faut :' + #13#10 + #13#10 +
    '  •  Windows 11, en 64 bits.' + #13#10 + #13#10 +
    '  •  Le runtime Microsoft .NET 8 (Desktop). S''il manque, cet installateur le télécharge et l''installe.' + #13#10 + #13#10 +
    '  •  Le runtime Microsoft Edge WebView2 (affichage des fenêtres). Il est déjà présent sur Windows 11 ; sinon il est téléchargé et installé.' + #13#10 + #13#10 +
    'La barre de Windows revient dès que la barre CDX est arrêtée (menu Démarrer > CDX > « Rendre la barre de Windows »), et à la désinstallation.');
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and (NeedWebView2 or NeedDotnet) then begin
    DownloadPage.Clear;
    if NeedDotnet then DownloadPage.Add(DotnetInstaller, 'windowsdesktop-runtime-8-win-x64.exe', '');
    if NeedWebView2 then DownloadPage.Add(WebView2Bootstrapper, 'MicrosoftEdgeWebview2Setup.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        if DownloadPage.AbortedByUser then
          Result := False
        else begin
          SuppressibleMsgBox('Un prérequis n''a pas pu être téléchargé (' + GetExceptionMessage + ').' + #13#10 +
            'CDX sera installé, mais ses programmes ne démarreront qu''une fois .NET 8 Desktop et WebView2 installés : ' +
            'https://dotnet.microsoft.com/download/dotnet/8.0 et https://developer.microsoft.com/microsoft-edge/webview2/', mbError, MB_OK, IDOK);
          NeedWebView2 := False; NeedDotnet := False;
        end;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

// Arrête les programmes de la suite (la barre d'abord : elle rend ensuite celle de Windows avec --restore), sauf si /NOSTOP=1.
procedure StopSuite(const AppDir: String);
var
  Names: TArrayOfString;
  I, ResultCode: Integer;
  Shell: String;
begin
  if NoStop then exit;
  // Audio Linker d'abord, et proprement (--quit : groupe arrêté, sortie par défaut de Windows rendue) : tué, il laisserait le son sur le câble virtuel.
  if FileExists(AppDir + '\cdx-audio_linker.exe') then
    Exec(AppDir + '\cdx-audio_linker.exe', '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Names := ['cdx-shell.exe', 'cdx-explorer.exe', 'cdx-browser.exe', 'cdx-transfers.exe', 'cdx-repos.exe', 'cdx-notes.exe', 'cdx-notes-mcp.exe', 'cdx-terminal.exe', 'cdx-agenda.exe', 'cdx-mail.exe', 'cdx-pilote.exe', 'cdx-pilote-mcp.exe', 'cdx-youtube.exe'];
  for I := 0 to GetArrayLength(Names) - 1 do
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM ' + Names[I] + ' /F /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Shell := AppDir + '\{#ShellExe}';
  if FileExists(Shell) then
    Exec(Shell, '--restore', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopSuite(ExpandConstant('{app}'));
  Result := '';
end;

procedure RunPrereq(const FileName, Params, Caption: String);
var
  Path: String;
  ResultCode: Integer;
begin
  Path := ExpandConstant('{tmp}\' + FileName);
  if FileExists(Path) then begin
    WizardForm.StatusLabel.Caption := Caption;
    // Ces runtimes s'installent par machine : l'élévation est demandée explicitement.
    if not ShellExec('runas', Path, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Log(Format('%s : échec du lancement (%d)', [FileName, ResultCode]));
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    if NeedDotnet then RunPrereq('windowsdesktop-runtime-8-win-x64.exe', '/install /quiet /norestart', 'Installation de .NET 8 Desktop...');
    if NeedWebView2 then RunPrereq('MicrosoftEdgeWebview2Setup.exe', '/silent /install', 'Installation de WebView2...');
  end;
end;

function InitializeUninstall: Boolean;
begin
  StopSuite(ExpandConstant('{app}'));
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then begin
    RegDeleteValue(HKCU, RunKey, '{#AppName}');
    if not UninstallSilent then begin
      if MsgBox('Supprimer aussi vos réglages (épingles, favoris, historique, journaux, accent) ?' + #13#10 + #13#10 +
                'Vos notes (dossier « Notes CDX » de vos Documents) ne sont jamais supprimées.' + #13#10 +
                'Choisissez Non pour garder les réglages si vous réinstallez CDX plus tard.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then begin
        DelTree(ExpandConstant('{localappdata}\CdxShell'), True, True, True);
        DelTree(ExpandConstant('{userappdata}\{#AppName}'), True, True, True);
      end;
    end;
  end;
end;
