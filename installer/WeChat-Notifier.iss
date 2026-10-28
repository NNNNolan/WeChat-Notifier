#define AppName "WeChat-Notifier"
#define AppId "{{9C6DF541-BDA2-47D6-8C75-AF3ED0BFA7D3}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={localappdata}\Programs\WeChat-Notifier
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#ArchitectureIdentifier}
ArchitecturesInstallIn64BitMode={#ArchitectureIdentifier}
SetupIconFile=..\src\WeChat-Notifier.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\WeChat-Notifier.exe
OutputDir="{#OutputDir}"
OutputBaseFilename=WeChat-Notifier-Setup-{#AppArchitecture}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\打开设置"; Filename: "{app}\WeChat-Notifier.exe"; Parameters: "--settings"; WorkingDir: "{app}"
Name: "{group}\卸载 WeChat-Notifier"; Filename: "{uninstallexe}"
Name: "{autodesktop}\WeChat-Notifier"; Filename: "{app}\WeChat-Notifier.exe"; Parameters: "--settings"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\WeChat-Notifier.exe"; Parameters: "--settings"; WorkingDir: "{app}"; Description: "启动 WeChat-Notifier"; Flags: postinstall nowait skipifsilent
