; OpenUninstaller installer (Inno Setup 6).
; Build with scripts/build-installer.ps1, which publishes first and passes MyAppVersion.
; The maintenance service is deliberately NOT installed here: it needs an explicit elevated
; install from Settings or `oucli service install --yes`.

#ifndef MyAppVersion
  #define MyAppVersion "0.6.0"
#endif

#ifndef MyAppSource
  #define MyAppSource "..\artifacts\publish\win-x64"
#endif

[Setup]
AppId={{8E1C2E8A-6F0B-4B37-9A5E-2C7D1B5A9C41}
AppName=OpenUninstaller
AppVersion={#MyAppVersion}
AppVerName=OpenUninstaller {#MyAppVersion}
AppPublisher=OpenUninstaller contributors
AppPublisherURL=https://github.com/
DefaultDirName={localappdata}\Programs\OpenUninstaller
DefaultGroupName=OpenUninstaller
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts\release
OutputBaseFilename=OpenUninstaller-{#MyAppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\OpenUninstaller.exe
SetupIconFile=..\src\OpenUninstaller.App\Assets\app.ico
LicenseFile=..\LICENSE
InfoAfterFile=..\docs\INSTALLER.md

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#MyAppSource}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\OpenUninstaller"; Filename: "{app}\OpenUninstaller.exe"
Name: "{group}\OpenUninstaller CLI"; Filename: "{app}\cli\oucli.exe"
Name: "{group}\User guide"; Filename: "{app}\docs\USER-GUIDE.md"
Name: "{autodesktop}\OpenUninstaller"; Filename: "{app}\OpenUninstaller.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\OpenUninstaller.exe"; Description: "Launch OpenUninstaller"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The app never installs the maintenance service on its own, so the uninstaller does not remove it.
; Remove it from Settings or with `oucli service remove --yes` before uninstalling if it was installed.

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
