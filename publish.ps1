# Собирает Pult.exe (один файл) и PultAdmin.exe (NativeAOT) в .\publish.
# Без ключа -Install только собирает и проверяет. С -Install ещё и кладёт оба файла в %LOCALAPPDATA%\Programs\Pult.
# С -Installer после сборки делает publish\Установка Lumen.exe (Inno Setup, installer\Pult.iss) и publish\Lumen.rar (установщик + «Как установить.txt», WinRAR).
param([switch]$Install, [switch]$Installer)
$ErrorActionPreference = 'Stop'
$dn = 'C:\Program Files\dotnet\dotnet.exe'
$root = $PSScriptRoot
$pub = Join-Path $root 'publish'
$out = Join-Path $env:LOCALAPPDATA 'Programs\Pult'

function Run([string]$what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "${what}: код выхода $LASTEXITCODE" }
}

Run 'тесты' { & $dn test (Join-Path $root 'Pult.sln') -c Release }

Remove-Item -Recurse -Force $pub -ErrorAction SilentlyContinue

# Помощник администратора: NativeAOT. Ключи single-file/self-contained не нужны (AOT их подразумевает, а вместе с PublishSingleFile сборка падает).
# Компоновщику нужен vswhere.exe из Visual Studio\Installer — добавляем папку в PATH только для этого процесса.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$env:PATH = "$vsInstaller;$env:PATH"
Run 'публикация Pult.Admin (NativeAOT)' {
    & $dn publish (Join-Path $root 'src\Pult.Admin') -c Release -r win-x64 -o (Join-Path $pub 'admin')
}

$admExe = Join-Path $pub 'admin\PultAdmin.exe'
if (-not (Test-Path $admExe)) { throw "не появился файл $admExe" }
$admHash = (Get-FileHash -Algorithm SHA256 $admExe).Hash.ToLowerInvariant()
"SHA256 PultAdmin.exe (вшивается в Pult.exe): $admHash"

# Приложение: один самодостаточный файл. SHA256 помощника вшивается в Pult.exe: только такой PultAdmin.exe Lumen согласится поставить с окном «Да».
Run 'публикация Pult.App' {
    & $dn publish (Join-Path $root 'src\Pult.App') -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:ExpectedAdminSha256=$admHash -o (Join-Path $pub 'app')
}

$app = Join-Path $pub 'app\Pult.exe'
$adm = Join-Path $pub 'admin\PultAdmin.exe'
foreach ($f in $app, $adm) { if (-not (Test-Path $f)) { throw "не появился файл $f" } }
$admSize = (Get-Item $adm).Length
if ($admSize -lt 1MB) { throw "PultAdmin.exe всего $admSize байт — это не NativeAOT (обычная оболочка .NET без защиты от подмены окружения)" }
"Pult.exe: {0:N1} МБ; PultAdmin.exe: {1:N1} МБ" -f ((Get-Item $app).Length / 1MB), ($admSize / 1MB)

if ($Installer) {
    $iscc = @((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')) |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'не найден ISCC.exe (Inno Setup 6)' }
    $rar = Join-Path $env:ProgramFiles 'WinRAR\Rar.exe'
    if (-not (Test-Path $rar)) { throw "не найден $rar (WinRAR)" }
    # Версия — одна, из Directory.Build.props.
    $ver = ([xml](Get-Content -Raw -Encoding UTF8 (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
    if ($ver -notmatch '^\d+\.\d+\.\d+$') { throw "версия '$ver' в Directory.Build.props должна быть вида 1.2.3" }
    Run 'установщик (Inno Setup)' { & $iscc "/DAppVersion=$ver" "/DSrcApp=$app" "/DSrcAdmin=$adm" (Join-Path $root 'installer\Pult.iss') }
    $setup = Join-Path $pub 'Установка Lumen.exe'
    if (-not (Test-Path $setup)) { throw "не появился файл $setup" }
    $howTo = Join-Path $root 'installer\Как установить.txt'
    $archive = Join-Path $pub 'Lumen.rar'
    Remove-Item $archive -ErrorAction SilentlyContinue   # «rar a» дописывает в старый архив — собираем заново
    Run 'архив (WinRAR)' { & $rar a -ep -m5 -idq $archive $setup $howTo }
    if (-not (Test-Path $archive)) { throw "не появился файл $archive" }
    "Установщик: {0:N1} МБ; архив: {1:N1} МБ (версия $ver)" -f ((Get-Item $setup).Length / 1MB), ((Get-Item $archive).Length / 1MB)
}

if (-not $Install) { "Сборка готова: $pub (без -Install ничего не устанавливалось)"; return }

Get-Process Pult -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null; $_.WaitForExit(5000) | Out-Null }
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item $app, $adm $out -Force
"Готово: $out"
