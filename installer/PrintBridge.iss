#ifndef BuildVersion
  #define BuildVersion "1.2.4"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
#ifndef Arch
  #define Arch "x64compatible"
#endif
#ifndef RuntimeName
  #define RuntimeName "win-x64"
#endif
[Setup]
AppId={{0312689D-FCDD-4576-A25A-FADBE4E5D159}
AppName=PrintBridge
AppVersion={#BuildVersion}
AppVerName=PrintBridge {#BuildVersion}
AppPublisher=develmax
AppPublisherURL=https://github.com/develmax/PrintBridge
LanguageDetectionMethod=none
ShowLanguageDialog=yes
DefaultDirName={localappdata}\Programs\PrintBridge
DefaultGroupName=PrintBridge
UninstallDisplayIcon={app}\PrintBridge.exe
SetupIconFile=..\src\PrintBridge.App\Resources\PrintBridge.ico
PrivilegesRequired=lowest
ArchitecturesAllowed={#Arch}
ArchitecturesInstallIn64BitMode={#Arch}
MinVersion=10.0.19045
OutputDir=..\artifacts\installer
OutputBaseFilename=PrintBridge-Setup-{#BuildVersion}-{#RuntimeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#BuildVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
russian.AutoStart=Запускать PrintBridge при входе в Windows
english.AutoStart=Start PrintBridge when signing in to Windows
russian.StartApp=Запустить PrintBridge
english.StartApp=Launch PrintBridge
russian.CloseFailure=PrintBridge занят. Дождитесь завершения операции и повторите установку.
english.CloseFailure=PrintBridge is busy. Wait for the current operation and retry setup.
russian.CleanupFailure=Удаление PrintBridge остановлено. Проверьте состояние очереди в PrintBridge и повторите попытку.
english.CleanupFailure=PrintBridge uninstall stopped. Check the queue in PrintBridge and retry.
english.MsiInstalled=PrintBridge was installed with MSI. Use the MSI package to update it.
russian.MsiInstalled=PrintBridge установлен из MSI. Используйте MSI-пакет для обновления.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutoStart}"; Check: DefaultAutostart

[Files]
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\ThirdParty\*"; DestDir: "{app}\ThirdParty"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\PrintBridge.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\PrintBridge"; Filename: "{app}\PrintBridge.exe"
Name: "{userdesktop}\PrintBridge"; Filename: "{app}\PrintBridge.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\PrintBridge"; ValueType: string; ValueName: "InstallerLanguage"; ValueData: "{code:AppLanguage}"; Flags: createvalueifdoesntexist uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PrintBridge"; ValueData: """{app}\PrintBridge.exe"" --tray"; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "PrintBridge"; Tasks: not autostart; Check: DefaultAutostart; Flags: deletevalue

[Run]
Filename: "{app}\PrintBridge.exe"; Description: "{cm:StartApp}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
function AppLanguage(Param: String): String;
begin
  if ActiveLanguage = 'russian' then Result := 'ru'
  else Result := 'en';
end;

function DefaultAutostart: Boolean;
begin
  Result := not FileExists(ExpandConstant('{app}\PrintBridge.exe'));
end;

var
  WasRunning: Boolean;

function IsAutomaticUpdate: Boolean;
var
  I: Integer;
begin
  Result := True;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/PBUPDATE') = 0 then Exit;
  Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  AppPath, StopArguments: String;
begin
  Result := '';
  AppPath := ExpandConstant('{app}\PrintBridge.exe');
  if FileExists(ExpandConstant('{app}\msi-installation.txt')) then
  begin
    Result := CustomMessage('MsiInstalled');
    Exit;
  end;
  StopArguments := '--stop --quiet';
  if IsAutomaticUpdate then
    StopArguments := '--stop-for-update --quiet';
  if FileExists(AppPath) then
  begin
    if not Exec(AppPath, StopArguments, '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Result := CustomMessage('CloseFailure')
    else if (Code <> 0) and (Code <> 3) then
      Result := CustomMessage('CloseFailure')
    else
      WasRunning := Code = 3;
  end;
end;

procedure DeinitializeSetup;
var
  Code: Integer;
begin
  if WasRunning and not IsAutomaticUpdate then
    ExecAsOriginalUser(ExpandConstant('{app}\PrintBridge.exe'), '--tray', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

function InitializeUninstall: Boolean;
var
  AppPath, Params: String;
  Code: Integer;
begin
  AppPath := ExpandConstant('{app}\PrintBridge.exe');
  Params := '--uninstall';
  if UninstallSilent then
    Params := Params + ' --quiet';
  Result := Exec(AppPath, Params, '', SW_SHOWNORMAL, ewWaitUntilTerminated, Code) and (Code = 0);
  if not Result then
    SuppressibleMsgBox(CustomMessage('CleanupFailure'), mbError, MB_OK, IDOK);
end;
