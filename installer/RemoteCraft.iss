; Inno Setup script for RemoteCraft.
; Build with installer\build-installer.ps1 (publishes the app and relay first, then compiles this script).

#define AppName "RemoteCraft"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExe "RemoteCraft.exe"

[Setup]
AppId={{9E3B6A41-7C25-4D8F-B1E0-5A2C8D73F964}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=RemoteCraft
VersionInfoVersion={#AppVersion}
; Per-user install by default (no admin prompt); the user can choose "all users" in the wizard.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=RemoteCraft-Setup-{#AppVersion}
SetupIconFile=..\src\RemoteCraft\Assets\RemoteCraft.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Close a running copy before files are replaced (same mutex name as Program.cs, default profile).
AppMutex=RemoteCraft-instance-
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "Start {#AppName} with Windows (in the tray, so this desk stays reachable)"; GroupDescription: "Startup:"
Name: "firewall"; Description: "Allow {#AppName} through Windows Firewall (direct and LAN connections)"; GroupDescription: "Network:"; Check: IsAdminInstallMode

[Files]
; Self-contained publish output: the target PC does not need .NET installed.
; Includes RemoteCraft.Relay.exe, the optional relay server for connections across the internet.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same value name the app's own Settings > "Start with Windows" uses.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExe}"" --tray"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes"; Flags: runhidden; Tasks: firewall
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden; RunOnceId: "DelFirewall"; Check: IsAdminInstallMode

; Settings, desk id and recent sessions (%APPDATA%\RemoteCraft) are left in place on uninstall.
