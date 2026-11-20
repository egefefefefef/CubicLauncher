#ifndef PublishDir
  #error PublishDir must be set to the self-contained publish directory
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{3AD90840-7225-4FF5-87D8-79C707C6A0F8}
AppName=VoxelNest Launcher
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\VoxelNest
DefaultGroupName=VoxelNest Launcher
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=VoxelNest-Setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\VoxelNest.exe

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\VoxelNest Launcher"; Filename: "{app}\VoxelNest.exe"
Name: "{userdesktop}\VoxelNest Launcher"; Filename: "{app}\VoxelNest.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VoxelNest.exe"; Description: "Start VoxelNest Launcher"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "app.voxelnest.launcher"; Flags: dontcreatekey uninsdeletevalue
