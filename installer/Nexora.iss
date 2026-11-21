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

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; Firewall rules need admin. We run PowerShell elevated via ShellExecute "runas".
; The -WindowStyle Hidden keeps it invisible; errors are suppressed so install never fails.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""netsh advfirewall firewall delete rule name='{#LegacyFwRuleTCP}' | Out-Null; netsh advfirewall firewall delete rule name='{#LegacyFwRuleUDP}' | Out-Null; netsh advfirewall firewall delete rule name='{#FwRuleTCP}' | Out-Null; netsh advfirewall firewall add rule name='{#FwRuleTCP}' dir=in action=allow protocol=TCP localport=4240-4249 profile=any | Out-Null; netsh advfirewall firewall delete rule name='{#FwRuleUDP}' | Out-Null; netsh advfirewall firewall add rule name='{#FwRuleUDP}' dir=in action=allow protocol=UDP localport=4240-4249 profile=any | Out-Null"""; \
  Flags: runhidden runascurrentuser; \
  StatusMsg: "Configuring firewall…"

; Launch app after install
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Remove firewall rules on uninstall (best-effort, ignore errors)
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""netsh advfirewall firewall delete rule name='{#FwRuleTCP}' | Out-Null; netsh advfirewall firewall delete rule name='{#FwRuleUDP}' | Out-Null; netsh advfirewall firewall delete rule name='{#LegacyFwRuleTCP}' | Out-Null; netsh advfirewall firewall delete rule name='{#LegacyFwRuleUDP}' | Out-Null"""; \
  Flags: runhidden runascurrentuser; RunOnceId: "FwCleanup"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ErrorCode: Integer;
begin
  // Terminate any running Nexora process so files can be replaced without lock errors
  Exec('taskkill.exe', '/im Nexora.exe /f', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
  Result := '';
end;
