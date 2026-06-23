; Inno Setup script for Clip
; Builds a per-user installer (no UAC prompt) that registers in
; Add/Remove Programs, adds a Start-menu entry, and an optional
; desktop shortcut. The app handles its own data folders under
; %LOCALAPPDATA%\Clip on first run.

#define MyAppName "Clip"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Isaiah Calvo"
#define MyAppURL "https://github.com/IsaiahCalvo/Clip"
#define MyAppExeName "Clip.exe"
#define MyAppHostExeName "Clip.Watcher.exe"
#define MyAppLauncherExeName "Clip.Launcher.exe"
#define MyAppHostArgs "watch"

[Setup]
AppId={{B5A7C821-94E1-4D2F-B0C8-8F0B16B6C0D4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={userappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\publish
OutputBaseFilename=Clip_{#MyAppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\app-icons\clip-tile-light.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
CloseApplications=force
RestartApplications=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "startupentry"; Description: "Launch {#MyAppName} when I sign in to Windows"; GroupDescription: "Startup:"

[Files]
Source: "..\artifacts\publish\Clip-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: filesandordirs; Name: "{app}\Clip.exe.WebView2"
Type: filesandordirs; Name: "{app}\Clip.Watcher.exe.WebView2"
Type: filesandordirs; Name: "{app}\cs"
Type: filesandordirs; Name: "{app}\de"
Type: filesandordirs; Name: "{app}\es"
Type: filesandordirs; Name: "{app}\fr"
Type: filesandordirs; Name: "{app}\it"
Type: filesandordirs; Name: "{app}\ja"
Type: filesandordirs; Name: "{app}\ko"
Type: filesandordirs; Name: "{app}\pl"
Type: filesandordirs; Name: "{app}\pt-BR"
Type: filesandordirs; Name: "{app}\ru"
Type: filesandordirs; Name: "{app}\tr"
Type: filesandordirs; Name: "{app}\zh-Hans"
Type: filesandordirs; Name: "{app}\zh-Hant"
Type: files; Name: "{app}\Clip.Launcher.deps.json"
Type: files; Name: "{app}\Clip.Launcher.dll"
Type: files; Name: "{app}\Clip.Launcher.runtimeconfig.json"
Type: files; Name: "{app}\clretwrc.dll"
Type: files; Name: "{app}\createdump.exe"
Type: files; Name: "{app}\Microsoft.DiaSymReader.Native.amd64.dll"
Type: files; Name: "{app}\Microsoft.VisualBasic*.dll"
Type: files; Name: "{app}\mscordaccore*.dll"
Type: files; Name: "{app}\mscordbi.dll"
Type: files; Name: "{app}\PresentationFramework*.dll"
Type: files; Name: "{app}\PresentationUI.dll"
Type: files; Name: "{app}\ReachFramework.dll"
Type: files; Name: "{app}\System.Design.dll"
Type: files; Name: "{app}\System.Diagnostics.EventLog*.dll"
Type: files; Name: "{app}\System.DirectoryServices.dll"
Type: files; Name: "{app}\System.Drawing.Design.dll"
Type: files; Name: "{app}\System.Linq.Parallel.dll"
Type: files; Name: "{app}\System.Printing.dll"
Type: files; Name: "{app}\System.ServiceModel.Web.dll"
Type: files; Name: "{app}\System.ServiceProcess.dll"
Type: files; Name: "{app}\System.Web*.dll"
Type: files; Name: "{app}\System.Windows.Forms.Design*.dll"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppLauncherExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppLauncherExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Clip"; ValueData: """{app}\{#MyAppHostExeName}"" {#MyAppHostArgs}"; Tasks: startupentry; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppLauncherExeName}"; Description: "Launch {#MyAppName} now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
