; Установщик Lumen (Inno Setup 6). Собирается из publish.ps1 -Installer:
;   ISCC.exe /DAppVersion=<версия из Directory.Build.props> /DSrcApp=<Pult.exe> /DSrcAdmin=<PultAdmin.exe> installer\Pult.iss
; Результат: publish\Установка Lumen.exe
;
; Внутренние имена остаются прежними (Pult.exe, PultAdmin.exe, папки Pult, AppId): так Lumen обновляет прежнюю установку «Пульт» поверх.
; ВАЖНО: папка установки — ровно %LOCALAPPDATA%\Programs\Pult (как у publish.ps1 -Install). Lumen и помощник администратора
; обязаны лежать в разных папках: помощник ставится в C:\Program Files\Pult, а если Pult.exe окажется там — Lumen покажет
; «установлен не туда» (StartupChecks.IsInHelperFolder), а помощник откажется ставиться (Installer.InstallPathProblem).
; Поэтому: права обычные (PrivilegesRequired=lowest, переопределить нельзя), страницы выбора папки нет.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SrcApp
  #define SrcApp "..\publish\app\Pult.exe"
#endif
#ifndef SrcAdmin
  #define SrcAdmin "..\publish\admin\PultAdmin.exe"
#endif

[Setup]
AppId={{A82152EF-DC66-4F7B-95EE-4B508EC37E43}
AppName=Lumen
AppVersion={#AppVersion}
AppVerName=Lumen {#AppVersion}
VersionInfoVersion={#AppVersion}.0
DefaultDirName={localappdata}\Programs\Pult
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish
OutputBaseFilename=Установка Lumen
SetupIconFile=..\src\Pult.App\Assets\pult.ico
UninstallDisplayIcon={app}\Pult.exe
UninstallDisplayName=Lumen
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Если Lumen сейчас открыт, установщик спросит и закроет его сам (Pult.exe), иначе файлы не заменить.
CloseApplications=yes
CloseApplicationsFilter=Pult.exe
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык «Lumen» на рабочем столе"; GroupDescription: "Ярлыки:"

[Files]
Source: "{#SrcApp}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcAdmin}"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Прежние ярлыки программы, когда она называлась «Пульт» (обновление поверх старой установки).
Type: files; Name: "{userdesktop}\Пульт.lnk"
Type: files; Name: "{userdesktop}\Пульт запуска.lnk"
Type: files; Name: "{userprograms}\Пульт.lnk"
Type: files; Name: "{userprograms}\Пульт запуска.lnk"

[Icons]
Name: "{autoprograms}\Lumen"; Filename: "{app}\Pult.exe"
Name: "{autodesktop}\Lumen"; Filename: "{app}\Pult.exe"; Tasks: desktopicon

[Registry]
; Автозапуск Lumen (галочка «Запускать вместе с Windows» в самой программе пишет это значение): при удалении убираем его, ничего не создавая.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Pult"; ValueType: none; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\Pult.exe"; Description: "Запустить Lumen"; Flags: nowait postinstall skipifsilent

[Code]
// Помощник администратора (C:\Program Files\Pult, задачи «Pult Admin» и «Pult System», C:\ProgramData\Pult) ставит и держит сама программа, не этот установщик.
// При удалении Lumen спрашиваем, убрать ли и его. Запускаем ТОЛЬКО копию из Program Files (папка защищена от записи обычными программами),
// а не ту, что рядом с Pult.exe: её мог бы подменить любой процесс пользователя, а запуск идёт с правами администратора.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Helper: String;
  Code: Integer;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Helper := ExpandConstant('{commonpf64}\Pult\PultAdmin.exe');
  if not FileExists(Helper) then Exit;
  if SuppressibleMsgBox(
       'Вместе с Lumen можно удалить и «помощника администратора» — маленькую программу, которая выполняет действия с правами администратора.' + #13#10 + #13#10 +
       'Удалить и помощника? Windows спросит разрешение (понадобится пароль или права администратора). Если ответить «Нет», Lumen удалится, а помощник останется.',
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    ShellExec('runas', Helper, '--uninstall', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;
