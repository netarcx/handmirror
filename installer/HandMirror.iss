; Inno Setup script for Hand Mirror

#define MyAppName "Hand Mirror"
; Version can be overridden from the build: ISCC /DMyAppVersion=1.2.3
#ifndef MyAppVersion
  #define MyAppVersion "1.0.2"
#endif
#define MyAppExeName "HandMirror.exe"
#define MyAppId "{8F8E5B6E-6A3F-4A1D-9C2A-3E5F1B2A0C7D}"

[Setup]
AppId={{#MyAppId}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\HandMirror
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=.
OutputBaseFilename=HandMirrorSetup
Compression=lzma2/max
SolidCompression=yes
SetupIconFile=..\icon.ico
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Upgrade in place: a fixed AppId means a newer version installs over the old one.
; We kill the running instance ourselves in PrepareToInstall (below), so disable
; Inno's own restart-manager handling to avoid a redundant "files in use" prompt.
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: checkedonce

[Files]
Source: "..\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish-selfcontained\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\icon.ico"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\icon.ico"; Tasks: desktopicon

[Registry]
; Remove the "start with Windows" entry the app writes, so uninstall leaves no
; orphaned startup item pointing at a deleted exe.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "HandMirror"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Best-effort: stop any running instance before uninstall
Filename: "{cmd}"; Parameters: "/C taskkill /F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillHandMirror"

[Code]
// Kill any running instance before copying files so an upgrade can overwrite the
// in-use HandMirror.exe. Runs after the user confirms but before installation.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName}', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

