; AI Terminals: one-click, offline, per-user Windows installer.
; Build from the exact Windows publish outputs. No PowerShell installer,
; GitHub login, administrator rights or download required on the user's PC.
;
; Files are installed in the same folders as the existing script installer.
; User metadata (folder assignments and language selection), cached session
; data, and CLI history are never included in this installer.
;
; A new installer can display a Windows SmartScreen reputation warning until
; this release is signed and its publisher reputation is established.

#define AppVersion "1.1.1"

[Setup]
AppId={{64A5013C-F387-4D38-A961-5018688BA1D7}
AppName=AI Terminals
AppVerName=AI Terminals {#AppVersion}
AppVersion={#AppVersion}
AppPublisher=AI Terminals Contributors
AppPublisherURL=https://github.com/simracemanagerinfo/ai-terminals
AppSupportURL=https://github.com/simracemanagerinfo/ai-terminals/issues
DefaultDirName={localappdata}\PowerShellCustomization
DisableDirPage=yes
DefaultGroupName=AI Terminals
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0
WizardStyle=modern
OutputDir=..\out\installer
OutputBaseFilename=AI-Terminals-Setup
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
UninstallDisplayName=AI Terminals
SetupLogging=no
VersionInfoVersion=1.1.1.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Types]
Name: "full"; Description: "Claude Terminal + Codex Terminal"
Name: "claude"; Description: "Claude Terminal only"
Name: "codex"; Description: "Codex Terminal only"
Name: "custom"; Description: "{cm:CustomInstallation}"; Flags: iscustom

[Components]
Name: "claude"; Description: "Claude Terminal"; Types: full claude custom
Name: "codex"; Description: "Codex Terminal"; Types: full codex custom

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\out\ClaudeTerminal\*"; DestDir: "{app}\ClaudeTerminal"; Components: claude; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\out\CodexTerminal\*"; DestDir: "{app}\CodexTerminal"; Components: codex; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\AI Terminals\Claude Terminal"; Filename: "{app}\ClaudeTerminal\ClaudeTerminal.exe"; WorkingDir: "{app}\ClaudeTerminal"; Components: claude
Name: "{autoprograms}\AI Terminals\Codex Terminal"; Filename: "{app}\CodexTerminal\CodexTerminal.exe"; WorkingDir: "{app}\CodexTerminal"; Components: codex
Name: "{autodesktop}\Claude Terminal"; Filename: "{app}\ClaudeTerminal\ClaudeTerminal.exe"; Components: claude; Tasks: desktopicon
Name: "{autodesktop}\Codex Terminal"; Filename: "{app}\CodexTerminal\CodexTerminal.exe"; Components: codex; Tasks: desktopicon
