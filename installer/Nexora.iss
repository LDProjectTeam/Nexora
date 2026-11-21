#define AppName "Nexora"
#ifndef AppVersion
  #define AppVersion "1.0.67"
#endif
#define AppExeName "Nexora.exe"
#define FwRuleTCP "Nexora - BitTorrent TCP 4240-4249"
#define FwRuleUDP "Nexora - BitTorrent UDP 4240-4249"
#define LegacyFwRuleTCP "Nexora - BitTorrent TCP 6881"
#define LegacyFwRuleUDP "Nexora - BitTorrent UDP 6881"

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
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\src\Nexora.App\Assets\NEXORA_icon_light.ico
OutputDir={#InstallerDir}
OutputBaseFilename=Nexora-Setup-{#AppVersion}-win-x64
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
english.NexoraGameOperation=Nexora is downloading or installing a game. Wait for it to finish (or pause it) before running this installer.
russian.NexoraGameOperation=Nexora сейчас скачивает или устанавливает игру. Дождитесь завершения (или поставьте на паузу), прежде чем запускать этот установщик.
ukrainian.NexoraGameOperation=Nexora зараз завантажує або встановлює гру. Дочекайтеся завершення (або поставте на паузу), перш ніж запускати цей інсталятор.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; Firewall rules require administrator rights, while the app itself installs per-user
; (PrivilegesRequired=lowest). Only this helper is elevated, via the shell "runas"
; verb. If UAC is declined the launcher still works using outgoing peers/UPnP.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""$ErrorActionPreference = 'SilentlyContinue'; Remove-NetFirewallRule -DisplayName '{#LegacyFwRuleTCP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#LegacyFwRuleUDP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#FwRuleTCP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#FwRuleUDP}' | Out-Null; New-NetFirewallRule -DisplayName '{#FwRuleTCP}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort '4240-4249' -Profile Any | Out-Null; New-NetFirewallRule -DisplayName '{#FwRuleUDP}' -Direction Inbound -Action Allow -Protocol UDP -LocalPort '4240-4249' -Profile Any | Out-Null"""; \
  Flags: runhidden shellexec waituntilterminated skipifsilent; Verb: runas; \
  StatusMsg: "Configuring firewall…"

; Launch app after install
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Removing firewall rules also needs elevation; best effort (declining UAC leaves
; harmless named rules behind).
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""$ErrorActionPreference = 'SilentlyContinue'; Remove-NetFirewallRule -DisplayName '{#FwRuleTCP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#FwRuleUDP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#LegacyFwRuleTCP}' | Out-Null; Remove-NetFirewallRule -DisplayName '{#LegacyFwRuleUDP}' | Out-Null"""; \
  Flags: runhidden shellexec waituntilterminated; Verb: runas; RunOnceId: "FwCleanup"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ErrorCode: Integer;
begin
  // The launcher keeps this mutex alive while a game is downloading/installing/
  // updating. Killing it then would abort the extraction and corrupt game files.
  if CheckForMutexes('Local\Nexora.GameOperation') then
  begin
    Result := ExpandConstant('{cm:NexoraGameOperation}');
    Exit;
  end;

  // Ask the running launcher to close, then force it only if it is still alive
  // (for example when it minimized to the tray instead of exiting).
  if CheckForMutexes('Local\Nexora.Launcher.SingleInstance') then
  begin
    Exec('taskkill.exe', '/im Nexora.exe', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
    Sleep(2000);
    if CheckForMutexes('Local\Nexora.Launcher.SingleInstance') then
      Exec('taskkill.exe', '/im Nexora.exe /f', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
  end;

  Result := '';
end;
