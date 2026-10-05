; Установщик «Корпоративная почта» (Inno Setup 6, https://jrsoftware.org/isinfo.php)
; Сборка: scripts\publish.ps1 (сначала публикует приложение в publish\win-x64, затем вызывает ISCC).

#define AppName "Корпоративная почта"
#define AppExe "MailClient.exe"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif

[Setup]
AppId={{7B0E5D6C-2F4A-4E0B-9C0D-6A1E2B3C4D5E}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=MailClient
DefaultDirName={autopf}\MailClient
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\publish
OutputBaseFilename=MailClient-Setup-{#AppVersion}
SetupIconFile=..\src\MailClient.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription=Установщик «{#AppName}»
#ifdef SignTool
; CI passes /DSignTool=1 and /Ssigntool=… when a code-signing certificate is configured.
SignTool=signtool
SignedUninstaller=yes
#endif
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Администратор ставит для всех пользователей, обычный пользователь — для себя.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать значок на рабочем столе"; GroupDescription: "Дополнительно:"
Name: "mailto"; Description: "Зарегистрировать как почтовую программу (ссылки mailto:)"; GroupDescription: "Дополнительно:"
Name: "autostart"; Description: "Запускать при входе в Windows"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; Excludes: "*.pdb,*.xml"
; Необязательные настройки организации (адрес сервера и т. п.), см. deploy\organization.example.json
Source: "..\deploy\organization.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Регистрация обработчика mailto: (выбор программы по умолчанию — в «Параметры Windows → Приложения по умолчанию»).
Root: HKA; Subkey: "Software\Classes\MailClient.mailto"; ValueType: string; ValueData: "URL:MailTo Protocol"; Flags: uninsdeletekey; Tasks: mailto
Root: HKA; Subkey: "Software\Classes\MailClient.mailto"; ValueName: "URL Protocol"; ValueType: string; ValueData: ""; Tasks: mailto
Root: HKA; Subkey: "Software\Classes\MailClient.mailto\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExe}"",0"; Tasks: mailto
Root: HKA; Subkey: "Software\Classes\MailClient.mailto\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: mailto
Root: HKA; Subkey: "Software\MailClient\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: mailto
Root: HKA; Subkey: "Software\MailClient\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Почтовый клиент для Microsoft Exchange"; Tasks: mailto
Root: HKA; Subkey: "Software\MailClient\Capabilities\URLAssociations"; ValueType: string; ValueName: "mailto"; ValueData: "MailClient.mailto"; Tasks: mailto
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "MailClient"; ValueData: "Software\MailClient\Capabilities"; Flags: uninsdeletevalue; Tasks: mailto
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MailClient"; ValueData: """{app}\{#AppExe}"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "Запустить {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
// Microsoft Edge WebView2 Runtime нужен для отображения HTML-писем (в Windows 11 и обновлённой Windows 10 уже есть).
function IsWebView2Installed(): Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not IsWebView2Installed()) and (not WizardSilent()) then
    MsgBox('Не найден компонент Microsoft Edge WebView2 Runtime.' + #13#10 + #13#10 +
           'Без него письма будут отображаться в упрощённом текстовом виде. ' +
           'Установите WebView2 Runtime (Evergreen) с сайта Microsoft или через средства развёртывания организации.',
           mbInformation, MB_OK);
end;
