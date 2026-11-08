; OpenUninstaller installer (Inno Setup 6.3 or later). Keep this file UTF-8 with BOM: the Turkish
; messages below need it.
; Build with scripts/build-installer.ps1, which publishes first and passes MyAppVersion.
; The maintenance service is deliberately NOT installed here: it needs an explicit elevated
; install from Settings or `oucli service install --yes`.
;
; Upgrades: every release keeps the same AppId, so a newer setup run over an existing installation
; updates it in place. It reuses the previous folder, install mode (per-user or all users),
; language and shortcut choices, closes a running OpenUninstaller, and refuses to replace a newer
; version.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef MyAppSource
  #define MyAppSource "..\artifacts\publish\win-x64"
#endif

#define MyAppRepository "https://github.com/eco-null/OpenUninstaller"
#define MaintenanceService "OpenUninstallerMaintenance"

[Setup]
AppId={{8E1C2E8A-6F0B-4B37-9A5E-2C7D1B5A9C41}
AppName=OpenUninstaller
AppVersion={#MyAppVersion}
AppVerName=OpenUninstaller {#MyAppVersion}
AppPublisher=OpenUninstaller contributors
AppPublisherURL={#MyAppRepository}
AppSupportURL={#MyAppRepository}/issues
AppUpdatesURL={#MyAppRepository}/releases
AppCopyright=Copyright (c) 2026 OpenUninstaller contributors. MIT licensed.
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName=OpenUninstaller
VersionInfoDescription=OpenUninstaller Setup
VersionInfoCompany=OpenUninstaller contributors
UninstallDisplayName=OpenUninstaller
UninstallDisplayIcon={app}\OpenUninstaller.exe
; {autopf} is %LOCALAPPDATA%\Programs for per-user installs and Program Files for /ALLUSERS.
DefaultDirName={autopf}\OpenUninstaller
DefaultGroupName=OpenUninstaller
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousAppDir=yes
UsePreviousPrivileges=yes
UsePreviousTasks=yes
UsePreviousLanguage=yes
ShowLanguageDialog=auto
CloseApplications=yes
; The [Run] entry relaunches the app after setup, so Restart Manager must not start a second copy.
RestartApplications=no
ChangesEnvironment=yes
SetupMutex=OpenUninstallerSetup,Global\OpenUninstallerSetup
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\release
OutputBaseFilename=OpenUninstaller-{#MyAppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\OpenUninstaller.App\Assets\app.ico
LicenseFile=..\LICENSE
InfoAfterFile=..\docs\INSTALLER.md

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[CustomMessages]
english.ShortcutsGroup=Shortcuts:
english.DesktopIcon=Create a desktop shortcut
english.IntegrationGroup=Command line:
english.AddToPath=Add the oucli command-line tool to PATH
english.CliShortcut=OpenUninstaller CLI
english.UserGuide=User guide
english.LaunchApp=Launch OpenUninstaller
english.NewerInstalled=OpenUninstaller %1 is already installed, which is newer than this setup (%2).%n%nUninstall the newer version first if you want to go back to %2.
english.ReadyUpdate=Setup will update OpenUninstaller %1 to %2 in its existing folder. Your settings, backups and traces are kept.
english.ReadyRepair=Setup will reinstall OpenUninstaller %1 in its existing folder. Your settings, backups and traces are kept.
english.UpdateButton=&Update
english.ServiceWillUpdate=The maintenance service will be updated too; Windows asks for administrator approval after the files are copied.
english.ServiceUpdateFailed=The maintenance service still runs its previous version.%n%nUpdate it later from Settings (run OpenUninstaller as administrator) or run "oucli service update --yes" from an elevated prompt.
english.LegacyServiceStop=The maintenance service runs from the installation folder and must be stopped before its files can be replaced. Approve the administrator prompt, or stop the service ("net stop {#MaintenanceService}" from an elevated prompt) and try again.
english.ServiceStillInstalled=The OpenUninstaller maintenance service is still installed and will keep running after the application is removed.%n%nTo remove it, cancel and run "oucli service remove --yes" from an elevated prompt, or remove it from Settings.%n%nUninstall the application anyway?
turkish.ShortcutsGroup=Kısayollar:
turkish.DesktopIcon=Masaüstü kısayolu oluştur
turkish.IntegrationGroup=Komut satırı:
turkish.AddToPath=oucli komut satırı aracını PATH'e ekle
turkish.CliShortcut=OpenUninstaller CLI
turkish.UserGuide=Kullanım kılavuzu
turkish.LaunchApp=OpenUninstaller'ı başlat
turkish.NewerInstalled=Bu bilgisayarda daha yeni bir sürüm (OpenUninstaller %1) kurulu; bu kurulum %2 sürümünü içeriyor.%n%n%2 sürümüne dönmek istiyorsanız önce yeni sürümü kaldırın.
turkish.ReadyUpdate=Kurulum, OpenUninstaller %1 sürümünü mevcut klasöründe %2 sürümüne güncelleyecek. Ayarlarınız, yedekleriniz ve izleriniz korunur.
turkish.ReadyRepair=Kurulum, OpenUninstaller %1 sürümünü mevcut klasörüne yeniden kuracak. Ayarlarınız, yedekleriniz ve izleriniz korunur.
turkish.UpdateButton=&Güncelle
turkish.ServiceWillUpdate=Bakım hizmeti de güncellenecek; dosyalar kopyalandıktan sonra Windows yönetici onayı isteyecek.
turkish.ServiceUpdateFailed=Bakım hizmeti hâlâ önceki sürümüyle çalışıyor.%n%nDaha sonra Ayarlar'dan (OpenUninstaller'ı yönetici olarak çalıştırın) ya da yönetici komut isteminde "oucli service update --yes" komutuyla güncelleyebilirsiniz.
turkish.LegacyServiceStop=Bakım hizmeti kurulum klasöründen çalışıyor; dosyaları değiştirilmeden önce durdurulması gerekiyor. Yönetici onayını verin ya da hizmeti durdurup ("net stop {#MaintenanceService}", yönetici komut isteminde) yeniden deneyin.
turkish.ServiceStillInstalled=OpenUninstaller bakım hizmeti hâlâ kurulu ve uygulama kaldırıldıktan sonra da çalışmaya devam edecek.%n%nKaldırmak için iptal edip yönetici komut isteminde "oucli service remove --yes" komutunu çalıştırın ya da Ayarlar'dan kaldırın.%n%nUygulama yine de kaldırılsın mı?

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:ShortcutsGroup}"
Name: "addtopath"; Description: "{cm:AddToPath}"; GroupDescription: "{cm:IntegrationGroup}"; Flags: unchecked

[InstallDelete]
; Folders owned entirely by the package are replaced on upgrade so files dropped from a release do
; not linger. The service folder is left alone: an install made before 1.0.10 may still run it.
Type: filesandordirs; Name: "{app}\docs"; Check: IsUpgrade
Type: filesandordirs; Name: "{app}\cli"; Check: IsUpgrade

[Files]
Source: "{#MyAppSource}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\OpenUninstaller"; Filename: "{app}\OpenUninstaller.exe"
Name: "{group}\{cm:CliShortcut}"; Filename: "{app}\cli\oucli.exe"
Name: "{group}\{cm:UserGuide}"; Filename: "{app}\docs\USER-GUIDE.md"
Name: "{autodesktop}\OpenUninstaller"; Filename: "{app}\OpenUninstaller.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\OpenUninstaller.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
const
  UserEnvironmentKey = 'Environment';
  SystemEnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
  MaintenanceServiceKey = 'SYSTEM\CurrentControlSet\Services\{#MaintenanceService}';

var
  PreviousVersion: String;

function InstalledVersion(): String;
begin
  // Inno Setup records the installed version under the AppId in HKCU (per-user) or HKLM (all
  // users); UsePreviousPrivileges makes HKA point at the hive of the existing installation.
  if not RegQueryStringValue(HKA,
    ExpandConstant('Software\Microsoft\Windows\CurrentVersion\Uninstall\{#emit SetupSetting("AppId")}_is1'),
    'DisplayVersion', Result) then
    Result := '';
end;

function IsUpgrade(): Boolean;
begin
  Result := PreviousVersion <> '';
end;

function MaintenanceServiceInstalled(): Boolean;
begin
  Result := RegKeyExists(HKLM, MaintenanceServiceKey);
end;

{ Maintenance service. It runs as LocalSystem from its own copy under Program Files, which only an
  administrator can replace, so setup hands the update to the elevated `oucli service update`. }

function MaintenanceServiceBinary(): String;
var
  Quote: Integer;
begin
  Result := '';
  if not RegQueryStringValue(HKLM, MaintenanceServiceKey, 'ImagePath', Result) then
    Exit;
  Result := Trim(Result);
  if Copy(Result, 1, 1) = '"' then
  begin
    Delete(Result, 1, 1);
    Quote := Pos('"', Result);
    if Quote > 0 then
      Result := Copy(Result, 1, Quote - 1);
  end;
end;

function IsInsideFolder(Path, Folder: String): Boolean;
begin
  Folder := AddBackslash(Folder);
  Result := (Path <> '') and (Uppercase(Copy(Path, 1, Length(Folder))) = Uppercase(Folder));
end;

function MaintenanceServiceNeedsUpdate(): Boolean;
var
  Binary, Version: String;
begin
  Result := False;
  if not MaintenanceServiceInstalled() then
    Exit;
  Binary := MaintenanceServiceBinary();
  if not GetVersionNumbersString(Binary, Version) then
    Version := '';
  Result := (Version <> '{#MyAppVersion}.0')
    or not IsInsideFolder(Binary, ExpandConstant('{commonpf64}\OpenUninstaller\Maintenance'));
end;

function RunElevated(FileName, Params: String): Boolean;
var
  ErrorCode: Integer;
begin
  // A UAC prompt in a silent per-user install would block an unattended deployment.
  if WizardSilent() and not IsAdmin() then
  begin
    Log('Skipped elevated ' + FileName + ' ' + Params + ' in silent non-administrative mode.');
    Result := False;
    Exit;
  end;
  Result := ShellExec('runas', FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
  if not Result then
    Log('Elevated ' + FileName + ' did not run: ' + SysErrorMessage(ErrorCode));
end;

procedure UpdateMaintenanceService();
begin
  if not MaintenanceServiceNeedsUpdate() then
    Exit;
  RunElevated(ExpandConstant('{app}\cli\oucli.exe'), 'service update --yes');
  if MaintenanceServiceNeedsUpdate() then
    SuppressibleMsgBox(CustomMessage('ServiceUpdateFailed'), mbInformation, MB_OK, IDOK);
end;

{ PATH entry for cli\oucli.exe: per-user installs change the user PATH, all-users installs the
  machine PATH. Only the exact folder this setup added is ever removed. }

function EnvironmentRoot(): Integer;
begin
  if IsAdminInstallMode() then
    Result := HKLM
  else
    Result := HKCU;
end;

function EnvironmentKey(): String;
begin
  if IsAdminInstallMode() then
    Result := SystemEnvironmentKey
  else
    Result := UserEnvironmentKey;
end;

function CliFolder(): String;
begin
  Result := ExpandConstant('{app}\cli');
end;

procedure AddCliToPath();
var
  Paths: String;
begin
  if not RegQueryStringValue(EnvironmentRoot(), EnvironmentKey(), 'Path', Paths) then
    Paths := '';
  if Pos(';' + Uppercase(CliFolder()) + ';', ';' + Uppercase(Paths) + ';') > 0 then
    Exit;
  // Keep the existing trailing separator style so RemoveCliFromPath restores the value exactly.
  if Paths = '' then
    Paths := CliFolder()
  else if Copy(Paths, Length(Paths), 1) = ';' then
    Paths := Paths + CliFolder() + ';'
  else
    Paths := Paths + ';' + CliFolder();
  if not RegWriteExpandStringValue(EnvironmentRoot(), EnvironmentKey(), 'Path', Paths) then
    Log('Could not add ' + CliFolder() + ' to PATH.');
end;

procedure RemoveCliFromPath();
var
  Paths: String;
  Position: Integer;
begin
  if not RegQueryStringValue(EnvironmentRoot(), EnvironmentKey(), 'Path', Paths) then
    Exit;
  Paths := ';' + Paths + ';';
  Position := Pos(';' + Uppercase(CliFolder()) + ';', Uppercase(Paths));
  if Position = 0 then
    Exit;
  Delete(Paths, Position, Length(CliFolder()) + 1);
  Paths := Copy(Paths, 2, Length(Paths) - 2);
  if not RegWriteExpandStringValue(EnvironmentRoot(), EnvironmentKey(), 'Path', Paths) then
    Log('Could not remove ' + CliFolder() + ' from PATH.');
end;

{ Setup }

function InitializeSetup(): Boolean;
var
  Installed, Packaged: Int64;
begin
  Result := True;
  PreviousVersion := InstalledVersion();
  if IsUpgrade() and StrToVersion(PreviousVersion, Installed)
    and StrToVersion('{#MyAppVersion}', Packaged)
    and (ComparePackedVersion(Packaged, Installed) < 0) then
  begin
    SuppressibleMsgBox(FmtMessage(CustomMessage('NewerInstalled'), [PreviousVersion, '{#MyAppVersion}']),
      mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // The licence was accepted for the installed version; an update only needs the summary.
  Result := (PageID = wpLicense) and IsUpgrade();
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Summary: String;
begin
  if (CurPageID = wpReady) and IsUpgrade() then
  begin
    if PreviousVersion = '{#MyAppVersion}' then
      Summary := FmtMessage(CustomMessage('ReadyRepair'), [PreviousVersion])
    else
      Summary := FmtMessage(CustomMessage('ReadyUpdate'), [PreviousVersion, '{#MyAppVersion}']);
    if MaintenanceServiceNeedsUpdate() then
      Summary := Summary + #13#10#13#10 + CustomMessage('ServiceWillUpdate');
    WizardForm.ReadyLabel.Caption := Summary;
    WizardForm.NextButton.Caption := CustomMessage('UpdateButton');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  // Services registered before 1.0.10 run from the installation folder and lock the files that
  // are about to be replaced; Restart Manager cannot stop a LocalSystem service.
  if MaintenanceServiceInstalled()
    and IsInsideFolder(MaintenanceServiceBinary(), WizardDirValue()) then
  begin
    if not RunElevated(ExpandConstant('{sys}\net.exe'), 'stop {#MaintenanceService}') then
      Result := CustomMessage('LegacyServiceStop');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Re-applied on every install so clearing the task during an update removes the entry.
    if WizardIsTaskSelected('addtopath') then
      AddCliToPath()
    else
      RemoveCliFromPath();
    UpdateMaintenanceService();
  end;
end;

{ Uninstall }

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if MaintenanceServiceInstalled() then
    Result := SuppressibleMsgBox(CustomMessage('ServiceStillInstalled'),
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDYES) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveCliFromPath();
end;
