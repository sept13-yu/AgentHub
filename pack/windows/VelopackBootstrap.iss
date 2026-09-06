#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef MyVpkSetup
  #define MyVpkSetup "..\..\dist\Releases\AgentHub-win-Setup.exe"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "..\..\dist"
#endif
#ifndef MyIconFile
  #define MyIconFile "..\..\src\AgentHub\assets\agenthub.ico"
#endif

#define MyAppName "AgentHub"

[Setup]
AppId={{B1A98275-3EEA-485E-A3AE-51E5AB8B9859}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=AgentHub
; 默认 C 盘用户目录，只有一块盘也能装；向导里可以改到别的盘。
DefaultDirName={localappdata}\Programs\AgentHub
DisableDirPage=no
AlwaysShowDirOnReadyPage=yes
DisableProgramGroupPage=yes
OutputDir={#MyOutputDir}
OutputBaseFilename=AgentHub-Setup-{#MyAppVersion}-win-x64
SetupIconFile={#MyIconFile}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Local\AgentHub.SingleInstance
CloseApplications=yes
Uninstallable=no
CreateUninstallRegKey=no
UsedUserAreasWarning=no

[Messages]
WelcomeLabel1=安装 [name]
WelcomeLabel2=程序文件默认装在 C 盘用户目录，下一步可以改路径。配置和缓存仍在本机用户目录。
SelectDestDir=选择安装位置
SelectDirDesc=程序文件会装到下面这个目录（可以改）。
SelectDirLabel3=安装目录：

[Files]
Source: "{#MyVpkSetup}"; DestDir: "{tmp}"; DestName: "AgentHub-win-Setup.exe"; Flags: deleteafterinstall

[Run]
Filename: "{tmp}\AgentHub-win-Setup.exe"; Parameters: "--silent --installto ""{app}"""; StatusMsg: "正在安装 AgentHub…"; Flags: waituntilterminated

[Code]
const
  WebView2ClientId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function HasWebView2Pv(RootKey: Integer; KeyPath: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, KeyPath, 'pv', Version)
    and (Version <> '') and (Version <> '0.0.0.0');
end;

function HasWebView2: Boolean;
var
  Id: String;
begin
  Id := WebView2ClientId;
  Result :=
    HasWebView2Pv(HKLM32, 'Software\Microsoft\EdgeUpdate\Clients\' + Id) or
    HasWebView2Pv(HKLM64, 'Software\Microsoft\EdgeUpdate\Clients\' + Id) or
    HasWebView2Pv(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + Id) or
    HasWebView2Pv(HKLM32, 'Software\Microsoft\EdgeUpdate\ClientState\' + Id) or
    HasWebView2Pv(HKLM64, 'Software\Microsoft\EdgeUpdate\ClientState\' + Id) or
    HasWebView2Pv(HKCU, 'Software\Microsoft\EdgeUpdate\ClientState\' + Id) or
    DirExists(ExpandConstant('{pf32}\Microsoft\EdgeWebView\Application')) or
    DirExists(ExpandConstant('{localappdata}\Microsoft\EdgeWebView\Application'));
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not HasWebView2 then
    Result := MsgBox(
      '没检测到 Microsoft Edge WebView2 Runtime。若你确定已经装过，选「是」继续安装即可。',
      mbConfirmation, MB_YESNO) = IDYES;
end;
