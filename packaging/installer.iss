; Inno Setup 6 安装脚本: 小熊模拟器 (Xiaoxiong Emulator)
; 采用用户级安装 (PrivilegesRequired=lowest)，默认落入 {localappdata}\Programs\XBear，
; WHPX 加速调用宿主虚拟化无需进程提权，用户数据统一落盘于 %LOCALAPPDATA%\XBear，卸载时不破坏实例数据。

#ifndef MyAppVersion
#define MyAppVersion "0.1.0"
#endif

#ifndef MyAppBuildId
#define MyAppBuildId "0.1.0+unknown"
#endif

#ifndef StagingDir
#define StagingDir "staging"
#endif

#ifndef OutputDir
#define OutputDir "dist"
#endif

[Setup]
AppId={{E8C7A39B-841F-4B18-A785-3F5F72A99E01}}
AppName=小熊模拟器
AppVersion={#MyAppVersion}
AppVerName=小熊模拟器 {#MyAppVersion}
AppPublisher=Xiaoxiong Emulator
DefaultDirName={localappdata}\Programs\XBear
DefaultGroupName=小熊模拟器
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=XBear-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\app\XBear.App.exe
SetupIconFile=..\src\XBear.App\Assets\app.ico
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式:"; Flags: unchecked

[Files]
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\小熊模拟器"; Filename: "{app}\app\XBear.App.exe"; IconFilename: "{app}\app\Assets\app.ico"
Name: "{group}\卸载小熊模拟器"; Filename: "{uninstallexe}"
Name: "{autodesktop}\小熊模拟器"; Filename: "{app}\app\XBear.App.exe"; IconFilename: "{app}\app\Assets\app.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\app\XBear.App.exe"; Description: "立即启动小熊模拟器"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 仅清理安装目录本身，用户实例配置与数据位于 %LOCALAPPDATA%\XBear，卸载时默认予以保留
Type: filesandordirs; Name: "{app}\app"
Type: filesandordirs; Name: "{app}\spec"
Type: filesandordirs; Name: "{app}\qemu"
Type: filesandordirs; Name: "{app}\platform-tools"
Type: dirifempty; Name: "{app}"
