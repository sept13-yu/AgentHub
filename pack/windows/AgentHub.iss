#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\..\dist\win-x64"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "..\..\dist"
#endif

#define MyAppName "AgentHub"
#define MyAppExeName "AgentHub.exe"

[Setup]
AppId={{B1A98275-3EEA-485E-A3AE-51E5AB8B9859}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=AgentHub
DefaultDirName={localappdata}\Programs\AgentHub
DefaultGroupName=AgentHub
OutputDir={#MyOutputDir}
OutputBaseFilename=AgentHub-Setup-{#MyAppVersion}-win-x64
SetupIconFile={#MySourceDir}\assets\agenthub.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Local\AgentHub.SingleInstance
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes
UsedUserAreasWarning=no

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\AgentHub"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\AgentHub"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 AgentHub"; Flags: nowait postinstall skipifsilent

[Code]
const
  WebView2ClientId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

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

function Unquote(Value: String): String;
begin
  Result := Value;
  if (Length(Result) >= 2) and (Ord(Result[1]) = 34) and
     (Ord(Result[Length(Result)]) = 34) then
    Result := Copy(Result, 2, Length(Result) - 2);
end;

function RunValueExe(Value: String): String;
var
  ClosingQuote: Integer;
begin
  Value := Trim(Value);
  if (Length(Value) > 0) and (Ord(Value[1]) = 34) then
  begin
    ClosingQuote := Pos(Chr(34), Copy(Value, 2, Length(Value)));
    if ClosingQuote > 0 then Result := Copy(Value, 2, ClosingQuote - 1)
    else Result := Unquote(Value);
  end
  else
  begin
    ClosingQuote := Pos(' ', Value);
    if ClosingQuote > 0 then Result := Copy(Value, 1, ClosingQuote - 1)
    else Result := Value;
  end;
end;

function IsAgentHubRunValue(Value: String): Boolean;
begin
  Result := CompareText(ExtractFileName(RunValueExe(Value)), '{#MyAppExeName}') = 0;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Current: String;
begin
  if (CurStep = ssPostInstall) and
     RegQueryStringValue(HKCU, RunKey, 'AgentHub', Current) and
     IsAgentHubRunValue(Current) then
    RegWriteStringValue(HKCU, RunKey, 'AgentHub', AddQuotes(ExpandConstant('{app}\{#MyAppExeName}')));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Current: String;
begin
  if (CurUninstallStep = usUninstall) and
     RegQueryStringValue(HKCU, RunKey, 'AgentHub', Current) and
     (CompareText(RunValueExe(Current), ExpandConstant('{app}\{#MyAppExeName}')) = 0) then
    RegDeleteValue(HKCU, RunKey, 'AgentHub');
end;
