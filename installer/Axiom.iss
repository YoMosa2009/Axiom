; Axiom Windows installer (Inno Setup 6).
;
; Built by scripts\Build-Installer.ps1 from the same clean package folder as the release ZIP:
;   ISCC.exe /DAppVersion=1.10.1 /DSourceDir=<clean package folder> /DOutputDir=<folder> installer\Axiom.iss
;
; Per-user install into %LOCALAPPDATA%\Programs\Axiom: no administrator prompt, and the in-app
; updater can replace files there later without elevation. User data (chats, keys, models) lives
; in %LOCALAPPDATA%\Axiom and is never touched by install, update or uninstall.

#ifndef AppVersion
  #error AppVersion must be passed with /DAppVersion=x.y.z
#endif
#ifndef SourceDir
  #error SourceDir must be passed with /DSourceDir=<clean package folder>
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
; Never change AppId: Windows and winget use it to recognise upgrades and uninstalls.
AppId={{6F3A2C1E-8B7D-4E5A-9C0F-2D4B6A8E1F37}
AppName=Axiom
AppVersion={#AppVersion}
AppVerName=Axiom {#AppVersion}
AppPublisher=Mosa (YoMosa2009)
AppPublisherURL=https://github.com/YoMosa2009/Axiom
AppSupportURL=https://github.com/YoMosa2009/Axiom/issues
AppUpdatesURL=https://github.com/YoMosa2009/Axiom/releases
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={localappdata}\Programs\Axiom
; A fixed folder keeps "delete the whole app folder on uninstall" safe.
DisableDirPage=yes
DefaultGroupName=Axiom
DisableProgramGroupPage=yes
UninstallDisplayName=Axiom
UninstallDisplayIcon={app}\Malx_AI.exe
SetupIconFile=..\Malx_AI\Assets\AxiomLogo.ico
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=Axiom-Setup-v{#AppVersion}-win-x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Closes a running Axiom (it holds a single-instance mutex) before replacing files.
AppMutex=Local\Axiom_SingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Axiom"; Filename: "{app}\Malx_AI.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Axiom"; Filename: "{app}\Malx_AI.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Malx_AI.exe"; Description: "Launch Axiom"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; In-app updates add files the installer never recorded; remove the whole (fixed) app folder.
; User data in %LOCALAPPDATA%\Axiom is a different folder and stays.
Type: filesandordirs; Name: "{app}"
