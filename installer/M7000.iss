; Установщик M7000 UI (Inno Setup 6.1+).
; Собирается в GitHub Actions (.github/workflows/release.yml):
;   iscc /DAppVersion=26.10.3.2359 /DSourceDir=..\publish installer\M7000.iss
; SourceDir — результат dotnet publish (self-contained .NET, Windows App SDK — из системы).

#ifndef AppVersion
  #define AppVersion "0.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

#define AppName "M7000 UI"
#define AppExe "M7000Tray.exe"
; Windows App Runtime 2.0 — без него приложение не запускается. Ставим, только если его нет.
#define RuntimeUrl "https://aka.ms/windowsappsdk/2.0/latest/windowsappruntimeinstall-x64.exe"

[Setup]
AppId={{6B0E8F2C-3D51-4C47-9E7A-2F1B5C8D4A90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Igor «InoY» Zviagintsev
AppPublisherURL=https://inoy.dev
AppSupportURL=https://github.com/inoyakaigor/m7000ui
VersionInfoVersion={#AppVersion}
; По умолчанию — для текущего пользователя, без прав администратора; в диалоге можно выбрать «для всех».
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
; Папку показываем всегда, в том числе при обновлении.
DisableDirPage=no
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=M7000UI-Setup-{#AppVersion}
SetupIconFile=..\M7000Tray\Assets\M7000.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
; Запущенное приложение держит файлы — установщик закроет его сам (Restart Manager), мьютекс — запасной вариант.
CloseApplications=yes
RestartApplications=no
AppMutex=M7000Tray.SingleInstance

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.RuntimeDownloading=Загрузка Windows App Runtime 2.0…
english.RuntimeDownloading=Downloading Windows App Runtime 2.0…
russian.RuntimeFailed=Не удалось установить Windows App Runtime 2.0 (код %1). Без него M7000 UI не запустится.%n%nУстановите его вручную: %2
english.RuntimeFailed=Could not install Windows App Runtime 2.0 (code %1). M7000 UI will not start without it.%n%nInstall it manually: %2

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Приложение живёт в трее — закрыть перед удалением файлов.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "StopApp"

[Registry]
; Автозапуск приложение прописывает само (значение M7000 в Run). При удалении — убрать, чтобы не осталась битая ссылка.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "M7000"; Flags: uninsdeletevalue

[Code]
function RuntimeInstalled: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "if (Get-AppxPackage -Name ''Microsoft.WindowsAppRuntime.2'' | Where-Object Architecture -eq ''X64'') { exit 0 } else { exit 1 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// Вызывается и в обычном, и в тихом режиме — перед копированием файлов.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Installer: String;
begin
  Result := '';
  if RuntimeInstalled then Exit;

  WizardForm.PreparingLabel.Caption := CustomMessage('RuntimeDownloading');
  try
    DownloadTemporaryFile('{#RuntimeUrl}', 'WindowsAppRuntimeInstall-x64.exe', '', nil);
  except
    Result := FmtMessage(CustomMessage('RuntimeFailed'), [GetExceptionMessage, '{#RuntimeUrl}']);
    Exit;
  end;

  Installer := ExpandConstant('{tmp}\WindowsAppRuntimeInstall-x64.exe');
  if not Exec(Installer, '--quiet', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    Result := FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(ResultCode), '{#RuntimeUrl}']);
end;
