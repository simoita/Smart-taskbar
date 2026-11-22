; Nebula Taskbar Studio - per-user installer.
; Built by scripts\package.ps1; the app version is passed with /DMyAppVersion=.

#define MyAppName "Nebula Taskbar Studio"
#define MyAppShortName "Nebula"
#define MyAppPublisher "Nebula"
#define MyAppExeName "Nebula.exe"
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

#ifndef MyAppVersion
  #define MyAppVersion "1.5.0"
#endif

[Setup]
AppId={{8F3C1D2A-6B47-4E5A-9C3D-2A7F5B1E9C64}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\Nebula
DefaultGroupName={#MyAppShortName}
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.18362
OutputDir=..\artifacts\setup
OutputBaseFilename=Nebula-Setup-{#MyAppVersion}-x64
SetupIconFile=..\src\Nebula\Assets\nebula.ico
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
SetupLogging=yes
AllowNoIcons=yes

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "desktopicon"; Description: "Crea un collegamento sul Desktop"; GroupDescription: "Collegamenti:"; Flags: unchecked
Name: "startup"; Description: "Avvia Nebula all'accesso di Windows"; GroupDescription: "Avvio:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#MyAppShortName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppName}"
Name: "{userdesktop}\{#MyAppShortName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppName}"; Tasks: desktopicon

[Registry]
; Windows Search and Win+R resolve the installed copy.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\Nebula.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\Nebula.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Nebula"; ValueData: """{app}\{#MyAppExeName}"" --startup"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Avvia {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The app restores the native taskbar, tab groups and system icons itself.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--exit"; Flags: runhidden waituntilterminated; RunOnceId: "ExitNebula"
Filename: "{app}\{#MyAppExeName}"; Parameters: "--restore-taskbar"; Flags: runhidden waituntilterminated; RunOnceId: "RestoreTaskbar"
Filename: "{app}\{#MyAppExeName}"; Parameters: "--restore-icons"; Flags: runhidden waituntilterminated; RunOnceId: "RestoreSystemIcons"

[UninstallDelete]
; Remove files added by the in-app updater that the installer does not track.
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{app}.new"
Type: filesandordirs; Name: "{app}.old"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  // Ask the installed copy to clean up before its files are replaced.
  if FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
    Exec(ExpandConstant('{app}\{#MyAppExeName}'), '--exit', '', SW_HIDE,
         ewWaitUntilTerminated, ResultCode);

  // The user may be running a portable copy from another folder. It holds the
  // same single-instance mutex, so the version this Setup installs would exit
  // immediately. Close every Nebula copy, give the watchdog time to restore the
  // native bar, then stop the watchdog too: replacing its exe while it runs is
  // what used to leave a half-installed folder.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // ping (not timeout) because timeout refuses redirected console input
  // inside the hidden installer process.
  Exec(ExpandConstant('{sys}\cmd.exe'), '/c ping -n 3 127.0.0.1 >nul', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Nebula.Watchdog.exe', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Remove registrations made by the app at runtime even when the optional
    // installer tasks were not selected.
    Exec(ExpandConstant('{sys}\reg.exe'),
      'delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v Nebula /f',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\reg.exe'),
      'delete "HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\Nebula.exe" /f',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
