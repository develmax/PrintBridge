#ifndef BuildVersion
  #define BuildVersion "1.2.8"
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
LanguageDetectionMethod=uilanguage
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
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "serbian"; MessagesFile: "Languages\SerbianCyrillic.isl"
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

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

english.AppLanguage=en
arabic.AppLanguage=ar
arabic.AutoStart=تشغيل PrintBridge عند تسجيل الدخول إلى Windows
arabic.StartApp=تشغيل PrintBridge
arabic.CloseFailure=PrintBridge مشغول. انتظر انتهاء العملية الحالية ثم أعد محاولة التثبيت.
arabic.CleanupFailure=توقفت عملية إلغاء تثبيت PrintBridge. تحقق من حالة قائمة الانتظار في PrintBridge ثم أعد المحاولة.
arabic.MsiInstalled=تم تثبيت PrintBridge باستخدام MSI. استخدم حزمة MSI لتحديثه.
russian.AppLanguage=ru
german.AppLanguage=de
german.AutoStart=Starten Sie PrintBridge, wenn Sie sich bei Windows anmelden
german.StartApp=Starten Sie PrintBridge
german.CloseFailure=PrintBridge ist beschäftigt. Warten Sie auf den aktuellen Vorgang und wiederholen Sie die Einrichtung.
german.CleanupFailure=Die Deinstallation von PrintBridge wurde gestoppt. Überprüfen Sie die Warteschlange in PrintBridge und versuchen Sie es erneut.
german.MsiInstalled=PrintBridge wurde mit MSI installiert. Verwenden Sie das MSI-Paket, um es zu aktualisieren.
spanish.AppLanguage=es
spanish.AutoStart=Inicie PrintBridge al iniciar sesión en Windows
spanish.StartApp=Inicie PrintBridge
spanish.CloseFailure=PrintBridge está ocupado. Espere la operación actual y vuelva a intentar la configuración.
spanish.CleanupFailure=La desinstalación de PrintBridge se detuvo. Verifique la cola en PrintBridge y vuelva a intentarlo.
spanish.MsiInstalled=PrintBridge se instaló con MSI. Utilice el paquete MSI para actualizarlo.
french.AppLanguage=fr
french.AutoStart=Démarrez PrintBridge lors de la connexion à Windows
french.StartApp=Lancer PrintBridge
french.CloseFailure=PrintBridge est occupé. Attendez l'opération en cours et réessayez la configuration.
french.CleanupFailure=La désinstallation de PrintBridge s'est arrêtée. Vérifiez la file d'attente dans PrintBridge et réessayez.
french.MsiInstalled=PrintBridge a été installé avec MSI. Utilisez le package MSI pour le mettre à jour.
italian.AppLanguage=it
italian.AutoStart=Avvia PrintBridge quando accedi a Windows
italian.StartApp=Avvia PrintBridge
italian.CloseFailure=PrintBridge è occupato. Attendere l'operazione corrente e riprovare la configurazione.
italian.CleanupFailure=La disinstallazione di PrintBridge è stata interrotta. Controllare la coda in PrintBridge e riprovare.
italian.MsiInstalled=PrintBridge è stato installato con MSI. Utilizza il pacchetto MSI per aggiornarlo.
dutch.AppLanguage=nl
dutch.AutoStart=Start PrintBridge wanneer u zich aanmeldt bij Windows
dutch.StartApp=Start PrintBridge
dutch.CloseFailure=PrintBridge is bezet. Wacht op de huidige bewerking en probeer de installatie opnieuw.
dutch.CleanupFailure=Het verwijderen van PrintBridge is gestopt. Controleer de wachtrij in PrintBridge en probeer het opnieuw.
dutch.MsiInstalled=PrintBridge is geïnstalleerd met MSI. Gebruik het MSI-pakket om het bij te werken.
polish.AppLanguage=pl
polish.AutoStart=Uruchom PrintBridge podczas logowania się do systemu Windows
polish.StartApp=Uruchom PrintBridge
polish.CloseFailure=PrintBridge jest zajęty. Poczekaj na bieżącą operację i ponów próbę konfiguracji.
polish.CleanupFailure=Dezinstalacja programu PrintBridge została zatrzymana. Sprawdź kolejkę w PrintBridge i spróbuj ponownie.
polish.MsiInstalled=PrintBridge został zainstalowany z MSI. Użyj pakietu MSI, aby go zaktualizować.
brazilianportuguese.AppLanguage=pt-BR
brazilianportuguese.AutoStart=Inicie o PrintBridge ao fazer login no Windows
brazilianportuguese.StartApp=Inicie o PrintBridge
brazilianportuguese.CloseFailure=O PrintBridge está ocupado. Aguarde a operação atual e tente novamente a configuração.
brazilianportuguese.CleanupFailure=A desinstalação do PrintBridge foi interrompida. Verifique a fila no PrintBridge e tente novamente.
brazilianportuguese.MsiInstalled=PrintBridge foi instalado com MSI. Use o pacote MSI para atualizá-lo.
turkish.AppLanguage=tr
turkish.AutoStart=Windows'ta oturum açarken PrintBridge'i başlatın
turkish.StartApp=PrintBridge'i başlatın
turkish.CloseFailure=PrintBridge meşgul. Geçerli işlemi bekleyin ve kurulumu yeniden deneyin.
turkish.CleanupFailure=PrintBridge kaldırma işlemi durduruldu. PrintBridge'deki kuyruğu kontrol edin ve yeniden deneyin.
turkish.MsiInstalled=PrintBridge MSI ile kuruldu. Güncellemek için MSI paketini kullanın.
serbian.AppLanguage=sr
serbian.AutoStart=Покрените PrintBridge када се пријављујете на Windows
serbian.StartApp=Покрените PrintBridge
serbian.CloseFailure=PrintBridge је заузет. Сачекајте тренутну операцију и покушајте поново са подешавањем.
serbian.CleanupFailure=PrintBridge деинсталација је заустављена. Проверите ред у PrintBridge-у и покушајте поново.
serbian.MsiInstalled=PrintBridge је инсталиран са MSI-јем. Користите MSI пакет да га ажурирате.
ukrainian.AppLanguage=uk
ukrainian.AutoStart=Запустіть PrintBridge під час входу в Windows
ukrainian.StartApp=Запустіть PrintBridge
ukrainian.CloseFailure=PrintBridge зайнятий. Зачекайте на виконання поточної операції та повторіть налаштування.
ukrainian.CleanupFailure=Видалення PrintBridge зупинено. Перевірте чергу в PrintBridge і повторіть спробу.
ukrainian.MsiInstalled=PrintBridge було встановлено з MSI. Використовуйте пакет MSI, щоб оновити його.
chinesesimplified.AppLanguage=zh-Hans
chinesesimplified.AutoStart=登录 Windows 时启动 PrintBridge
chinesesimplified.StartApp=启动 PrintBridge
chinesesimplified.CloseFailure=PrintBridge 正忙。等待当前操作并重试设置。
chinesesimplified.CleanupFailure=PrintBridge 卸载已停止。检查 PrintBridge 中的队列并重试。
chinesesimplified.MsiInstalled=PrintBridge 与 MSI 一起安装。使用 MSI 包进行更新。
japanese.AppLanguage=ja
japanese.AutoStart=Windows へのサインイン時に PrintBridge を起動する
japanese.StartApp=PrintBridgeを起動する
japanese.CloseFailure=PrintBridge はビジー状態です。現在の操作を待って、セットアップを再試行してください。
japanese.CleanupFailure=PrintBridge のアンインストールが停止しました。 PrintBridge のキューを確認して、再試行してください。
japanese.MsiInstalled=PrintBridge は MSI とともにインストールされました。 MSI パッケージを使用して更新します。
korean.AppLanguage=ko
korean.AutoStart=Windows에 로그인할 때 PrintBridge 시작
korean.StartApp=PrintBridge 실행
korean.CloseFailure=PrintBridge가 사용 중입니다. 현재 작업을 기다렸다가 설정을 다시 시도하세요.
korean.CleanupFailure=PrintBridge 제거가 중지되었습니다. PrintBridge의 대기열을 확인하고 다시 시도하십시오.
korean.MsiInstalled=PrintBridge는 MSI와 함께 설치되었습니다. MSI 패키지를 사용하여 업데이트하세요.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutoStart}"; Check: DefaultAutostart

[Files]
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\ThirdParty\*"; DestDir: "{app}\ThirdParty"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\PrintBridge.exe"; DestDir: "{app}"; Flags: ignoreversion
#ifdef SignedRelease
Source: "{#PublishDir}\PrintBridge.exe.sig"; DestDir: "{app}"; Flags: ignoreversion
#endif

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
  Result := CustomMessage('AppLanguage');
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
