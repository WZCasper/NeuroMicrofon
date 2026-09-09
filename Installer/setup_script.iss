; ============================================================================
; NeuroMicrophone — установщик (Inno Setup 6.x, https://jrsoftware.org/isinfo.php)
; ============================================================================
;
; ВАЖНО — прочитайте перед использованием:
;
; 1) Начиная с Windows 10, ядро ОС проверяет цифровую подпись драйверов
;    (Driver Signature Enforcement). Эта проверка выполняется самой
;    операционной системой и не может быть обойдена никаким скриптом.
;    Чтобы установка драйвера ниже реально сработала, в папку
;    Driver\Package\ нужно заранее положить УЖЕ ПОДПИСАННЫЙ пакет
;    драйвера (файлы .inf/.sys/.cat) — см. Installer/README_DRIVER.md.
;    Если пакета нет, установщик пропустит этот шаг без ошибки — просто
;    установится само приложение.
;
; 2) "Опубликованное" имя INF в хранилище драйверов (вида oemNN.inf)
;    присваивается системой во время установки и отличается от исходного
;    имени файла. Секция [Code] ниже сама перехватывает вывод pnputil
;    во время установки, вытаскивает из него это имя и сохраняет в
;    реестр (HKLM\Software\NeuroMicrophone), чтобы деинсталлятор мог
;    корректно удалить именно тот драйвер, который был установлен —
;    без этого механизма "pnputil /delete-driver" почти наверняка не
;    нашёл бы, что удалять.
;
; 3) Приложение должно быть заранее собрано командой:
;      dotnet publish ..\src\NeuroMicrophone.csproj -c Release -r win-x64 --self-contained false
;    Скрипт ожидает результат publish в ..\src\bin\Release\net8.0-windows\win-x64\publish\
;
; ============================================================================

#define MyAppName "NeuroMicrophone"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "NeuroMicrophone"
#define MyAppExeName "NeuroMicrophone.exe"
#define DriverInfName "NeuroMicCable.inf"

[Setup]
AppId={{B6C1F9C4-6E2C-4B7E-9C7B-3C6E6B4B7B10}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=Output
OutputBaseFilename=NeuroMicrophone_Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
DisableProgramGroupPage=yes
WizardStyle=modern

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
; Собранное приложение (см. примечание 3 выше про dotnet publish).
Source: "..\src\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

; Пакет драйвера — необязателен на этапе сборки установщика: если папка
; пуста, "skipifsourcedoesntexist" не даёт компилятору упасть с ошибкой
; "источник не найден", а приложение просто покажет баннер "драйвер не
; установлен" и предложит кнопку установки внутри самого приложения.
Source: "Driver\Package\*"; DestDir: "{app}\Driver\Package"; \
    Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; \
    Excludes: "*.tmp"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Создать значок на рабочем столе"; GroupDescription: "Дополнительные значки:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RegistryRoot = HKLM;
  RegistryKeyPath = 'Software\NeuroMicrophone';
  RegistryValueName = 'PublishedDriverInfName';

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsAdminInstallMode() then
  begin
    MsgBox('Для установки требуются права администратора (нужны для регистрации ' +
           'виртуального аудиодрайвера). Перезапустите установщик от имени администратора.',
           mbError, MB_OK);
    Result := False;
  end;
end;

// Устанавливает драйвер через pnputil, перенаправляя его вывод во временный
// файл (сам pnputil не имеет режима вывода в переменную), затем разбирает
// этот файл в поисках строки "Published Name" и сохраняет найденное имя
// в реестр — деинсталлятор прочитает его оттуда для корректного удаления.
procedure InstallDriverAndCapturePublishedName();
var
  ResultCode: Integer;
  OutputFile: String;
  Lines: TArrayOfString;
  I: Integer;
  PublishedName: String;
  InfFullPath: String;
begin
  InfFullPath := ExpandConstant('{app}\Driver\Package\{#DriverInfName}');
  if not FileExists(InfFullPath) then
  begin
    Exit; // Пакет драйвера не был предоставлен на этапе сборки — просто пропускаем этот шаг.
  end;

  OutputFile := ExpandConstant('{tmp}\nm_pnputil_output.txt');
  PublishedName := '';

  Exec(ExpandConstant('{cmd}'), ExpandConstant(
    '/c pnputil.exe /add-driver "' + InfFullPath + '" /install > "' + OutputFile + '" 2>&1'),
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if LoadStringsFromFile(OutputFile, Lines) then
  begin
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      if Pos('Published Name', Lines[I]) > 0 then
      begin
        PublishedName := Trim(Copy(Lines[I], Pos(':', Lines[I]) + 1, MaxInt));
        Break;
      end;
    end;
  end;

  if PublishedName <> '' then
  begin
    RegWriteStringValue(RegistryRoot, RegistryKeyPath, RegistryValueName, PublishedName);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    InstallDriverAndCapturePublishedName();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  PublishedName: String;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(RegistryRoot, RegistryKeyPath, RegistryValueName, PublishedName) and (PublishedName <> '') then
    begin
      Exec('pnputil.exe', '/delete-driver "' + PublishedName + '" /uninstall /force', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      RegDeleteValue(RegistryRoot, RegistryKeyPath, RegistryValueName);
    end;
    // Если запись в реестре отсутствует, драйвер либо не был установлен
    // этим установщиком, либо уже был удалён через саму программу
    // (кнопка "Удалить драйвер") — в обоих случаях делать здесь больше нечего.
  end;
end;
