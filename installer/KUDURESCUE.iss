; KUDURESCUE installer (Inno Setup 6)
; Installs for the current Windows user only, so installing never asks for administrator rights.
; (Scanning a drive does: KUDURESCUE asks Windows for them when a scan starts.)

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\KUDURESCUE"
#endif

#define MyAppName "KUDURESCUE"
#define MyAppExe "KUDURESCUE.exe"
#define MyAppUrl "https://github.com/azeembukhariprivate/kudurescue"

[Setup]
AppId={{4C8B2E17-9A3F-4D61-B0E5-7F2A6C1D9E83}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Azeem Bukhari
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=KUDURESCUE-{#MyAppVersion}-setup
SetupIconFile=..\src\KUDURESCUE\Assets\KUDURESCUE.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany=Azeem Bukhari
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nTip: if you're recovering deleted files, install KUDURESCUE on a different drive from the one the files were on, so the installation can't overwrite them.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "self-test.log"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Open KUDURESCUE now"; Flags: nowait postinstall skipifsilent

[Registry]
; Remove saved settings when uninstalling.
Root: HKCU; Subkey: "Software\KUDURESCUE"; Flags: uninsdeletekey dontcreatekey
