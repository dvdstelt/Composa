; Inno Setup script for the Composa installer. Built by scripts/package/windows.sh, which passes
; every value that changes per build; nothing here is edited at release time.
;
;   AppVersion    the MinVer version, e.g. 0.4.0 or 0.4.1-alpha.0.3
;   FileVersion   the same as four numbers, which is all a Windows version resource can hold
;   Architecture  x64compatible or arm64
;   SourceDir     the published build
;   OutputDir     where the installer is written
;   OutputName    the installer's file name without .exe

#ifndef AppVersion
  #error Build this through scripts/package/windows.sh, which supplies the version and paths.
#endif

#define AppName "Composa"
#define AppExe "composa.exe"
#define AppUrl "https://github.com/dvdstelt/Composa"

[Setup]
; The AppId identifies the installation to Windows across upgrades and uninstalls. Never change it,
; or a new version installs next to the old one instead of replacing it.
AppId={{3D9BED7E-2C94-450E-A609-E183B1E42ACA}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Dennis van der Stelt
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#FileVersion}
VersionInfoProductTextVersion={#AppVersion}

; Per user by default, so installing needs no administrator rights; the dialog lets someone who has
; them install for every account instead. {auto*} paths and HKA follow that choice.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed={#Architecture}
ArchitecturesInstallIn64BitMode={#Architecture}
MinVersion=10.0

SourceDir={#SourceDir}
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
SetupIconFile={#SourcePath}\..\icons\composa.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ChangesAssociations=yes
; Composa running during an upgrade holds its own files open.
CloseApplications=yes

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Composa owns .cmps, its own project format, so it becomes the default for it.
Root: HKA; Subkey: "Software\Classes\.cmps"; ValueType: string; ValueName: ""; ValueData: "Composa.Project"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.cmps\OpenWithProgids"; ValueType: string; ValueName: "Composa.Project"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Composa.Project"; ValueType: string; ValueName: ""; ValueData: "Composa project"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Composa.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\Composa.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

; Images are offered under "Open with" but never taken over: someone installing an editor has not
; asked for it to replace their photo viewer.
Root: HKA; Subkey: "Software\Classes\Composa.Image"; ValueType: string; ValueName: ""; ValueData: "Image"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Composa.Image\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\Composa.Image\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""
#define OpenWith(Extension) 'Root: HKA; Subkey: "Software\Classes\' + Extension + '\OpenWithProgids"; ValueType: string; ValueName: "Composa.Image"; ValueData: ""; Flags: uninsdeletevalue'
#emit OpenWith(".png")
#emit OpenWith(".jpg")
#emit OpenWith(".jpeg")
#emit OpenWith(".webp")
#emit OpenWith(".bmp")
#emit OpenWith(".gif")
#emit OpenWith(".tif")
#emit OpenWith(".tiff")
#emit OpenWith(".heic")
#emit OpenWith(".heif")
#emit OpenWith(".avif")
#emit OpenWith(".psd")

; How Windows names the program in "Open with" and the default apps settings.
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
