#define AppName "Vellum"
#ifndef AppVersion
  #define AppVersion "1.0.72"
#endif
#define AppExeName "Vellum.exe"
#define BootstrapperExeName "Bootstrapper\Vellum.Bootstrapper.exe"
#define FwRuleTCP "Vellum - BitTorrent TCP 4240-4249"
#define FwRuleUDP "Vellum - BitTorrent UDP 4240-4249"
#define LegacyFwRuleTCP "Vellum - BitTorrent TCP 6881"
#define LegacyFwRuleUDP "Vellum - BitTorrent UDP 6881"
; Rules left behind by the previous product name.
#define LegacyBrandFwRuleTCP "Nexora - BitTorrent TCP 4240-4249"
#define LegacyBrandFwRuleUDP "Nexora - BitTorrent UDP 4240-4249"
#define LegacyBrandFwRuleTCP6881 "Nexora - BitTorrent TCP 6881"
#define LegacyBrandFwRuleUDP6881 "Nexora - BitTorrent UDP 6881"

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
#ifndef InstallerDir
  #define InstallerDir "..\artifacts\installer"
#endif

[Setup]
AppId={{A41A2A56-96F5-4BB4-98E9-73EA14A6E929}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={localappdata}\Programs\{#AppName}
; The product was renamed, so never reuse the old install folder.
UsePreviousAppDir=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#BootstrapperExeName}
SetupIconFile=..\src\Vellum.App\Assets\VELLUM_icon_light.ico
OutputDir={#InstallerDir}
OutputBaseFilename=Vellum-Setup-{#AppVersion}-win-x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=yes

[Languages]
Name: "english";  MessagesFile: "compiler:Default.isl"
Name: "russian";  MessagesFile: "compiler:Languages\Russian.isl"
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[CustomMessages]
english.VellumGameOperation=Vellum is downloading or installing a game. Wait for it to finish (or pause it) before running this installer.
russian.VellumGameOperation=Vellum сейчас скачивает или устанавливает игру. Дождитесь завершения (или поставьте на паузу), прежде чем запускать этот установщик.
ukrainian.VellumGameOperation=Vellum зараз завантажує або встановлює гру. Дочекайтеся завершення (або поставте на паузу), перш ніж запускати цей інсталятор.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; A setup replaces the launcher outside of the update system. Old launcher
; journals/installed hashes refer to the previous build and must not roll it back.
Type: filesandordirs; Name: "{userappdata}\Vellum\updates\launcher"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#BootstrapperExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#BootstrapperExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; Firewall rules require administrator rights, while the app itself installs per-user
; (PrivilegesRequired=lowest). Only this helper is elevated, via the shell "runas"
; verb. If UAC is declined the launcher still works using outgoing peers/UPnP.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""$ErrorActionPreference = 'SilentlyContinue'; Remove-NetFirewallRule -DisplayName '{#LegacyBrandFwRuleTCP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#LegacyBrandFwRuleUDP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#LegacyBrandFwRuleTCP6881}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#LegacyBrandFwRuleUDP6881}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#LegacyFwRuleTCP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#LegacyFwRuleUDP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#FwRuleTCP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#FwRuleUDP}' | Out-Null; New-NetFirewallRule -DisplayName '{#FwRuleTCP}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort '4240-4249' -Profile Any | Out-Null; New-NetFirewallRule -DisplayName '{#FwRuleUDP}' -Direction Inbound -Action Allow -Protocol UDP -LocalPort '4240-4249' -Profile Any | Out-Null"""; \
  Flags: runhidden shellexec waituntilterminated skipifsilent; Verb: runas; \
  StatusMsg: "Configuring firewall…"

; Launch app after install through the mandatory bootstrapper entry point
Filename: "{app}\{#BootstrapperExeName}"; Description: "Launch {#AppName}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Removing firewall rules also needs elevation; best effort (declining UAC leaves
; harmless named rules behind).
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""$ErrorActionPreference = 'SilentlyContinue'; Remove-NetFirewallRule -DisplayName '{#FwRuleTCP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#FwRuleUDP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#LegacyFwRuleTCP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#LegacyFwRuleUDP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#LegacyBrandFwRuleTCP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#LegacyBrandFwRuleUDP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#LegacyBrandFwRuleTCP6881}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#LegacyBrandFwRuleUDP6881}' | Out-Null"""; \
  Flags: runhidden shellexec waituntilterminated; Verb: runas; RunOnceId: "FwCleanup"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ErrorCode: Integer;
begin
  // The launcher keeps this mutex alive while a game is downloading/installing/
  // updating. Killing it then would abort the extraction and corrupt game files.
  if CheckForMutexes('Local\Vellum.GameOperation') then
  begin
    Result := ExpandConstant('{cm:VellumGameOperation}');
    Exit;
  end;

  // Ask the running launcher to close, then force it only if it is still alive
  // (for example when it minimized to the tray instead of exiting).
  Exec('taskkill.exe', '/im Vellum.Bootstrapper.exe', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
  if CheckForMutexes('Local\Vellum.Launcher.SingleInstance') then
  begin
    Exec('taskkill.exe', '/im Vellum.exe', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
    Sleep(2000);
    if CheckForMutexes('Local\Vellum.Launcher.SingleInstance') then
      Exec('taskkill.exe', '/im Vellum.exe /f', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
  end;

  // Older builds are still called Nexora. Refuse to interrupt them mid-operation
  // and close them before the renamed files are written.
  if CheckForMutexes('Local\Nexora.GameOperation') then
  begin
    Result := ExpandConstant('{cm:VellumGameOperation}');
    Exit;
  end;

  if CheckForMutexes('Local\Nexora.Launcher.SingleInstance') then
  begin
    Exec('taskkill.exe', '/im Nexora.exe', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
    Sleep(1500);
    if CheckForMutexes('Local\Nexora.Launcher.SingleInstance') then
      Exec('taskkill.exe', '/im Nexora.exe /f', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
  end;

  Result := '';
end;
