$ErrorActionPreference = 'Stop'

dotnet build (Join-Path $PSScriptRoot 'm3nuxa.Editor.Checks.csproj') -c Release -p:Platform=x64 -p:PublishReadyToRun=false
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать проверки редактора.' }

$checkDirectory = Join-Path $PSScriptRoot 'bin/x64/Release/net10.0-windows10.0.19041.0/win-x64'
if (-not (Get-Process -Name 'm3nuxa.App' -ErrorAction SilentlyContinue)) {
    dotnet publish (Join-Path $PSScriptRoot '../../src/m3nuxa.App/m3nuxa.App.csproj') -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishReadyToRun=false -o $checkDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать тестовое приложение в трее.' }
    $testTray = Start-Process -FilePath (Join-Path $checkDirectory 'm3nuxa.App.exe') -WorkingDirectory $checkDirectory -WindowStyle Hidden -PassThru
}
try {
$checkProcess = Start-Process -FilePath (Join-Path $checkDirectory 'm3nuxa.Editor.Checks.exe') -WorkingDirectory $checkDirectory -WindowStyle Hidden -PassThru
if (-not $checkProcess.WaitForExit(20000)) {
    Stop-Process -Id $checkProcess.Id
    throw 'Проверки редактора не завершились за 20 секунд.'
}
$result = Join-Path $checkDirectory 'result.txt'
if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
if ($checkProcess.ExitCode -ne 0) { throw "Проверки редактора завершились с кодом $($checkProcess.ExitCode)." }
} finally {
    if ($testTray -and -not $testTray.HasExited) { Stop-Process -Id $testTray.Id }
}
