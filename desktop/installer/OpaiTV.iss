; OpaiTV 桌面版 Inno Setup 安装脚本
; 编译: "C:\Users\admin\AppData\Local\Programs\Inno Setup 6\ISCC.exe" desktop\installer\OpaiTV.iss

#define MyAppName "OpaiTV"
#ifndef MyAppVersion
  #define MyAppVersion "1.3.15"
#endif
#define MyAppPublisher "OpaiTV"
#define MyAppExeName "OpaiTV.exe"

[Setup]
AppId={{8F3A2C71-4D9E-4B6A-9E21-7C5B0A1F2D34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\dist
OutputBaseFilename=OpaiTV-Setup-{#MyAppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "..\publish\win-x64-self-contained\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: not IsUpdateInstall
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; Check: IsUpdateInstall

[Code]
function IsUpdateInstall: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;
