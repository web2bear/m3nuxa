param([ValidateSet('en-US', 'ru-RU')][string]$Language = 'en-US')

$ErrorActionPreference = 'Stop'
$checkDirectory = Join-Path $PSScriptRoot 'bin/ui-checks'

dotnet build (Join-Path $PSScriptRoot 'm3nuxa.Editor.Checks.csproj') -c Release -p:Platform=x64 -p:PublishReadyToRun=false -o $checkDirectory
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать проверки редактора.' }

$testTray = $null
$checkProcess = $null
$settingsPath = Join-Path $checkDirectory 'settings.json'
$originalSettings = if (Test-Path -LiteralPath $settingsPath) { [IO.File]::ReadAllBytes($settingsPath) } else { $null }
$previousLanguage = $env:m3nuxa_CHECK_LANGUAGE
if (-not (Get-Process -Name 'm3nuxa.App' -ErrorAction SilentlyContinue)) {
    $trayDirectory = Join-Path $PSScriptRoot 'bin/test-tray'
    dotnet publish (Join-Path $PSScriptRoot '../../src/m3nuxa.App/m3nuxa.App.csproj') -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishReadyToRun=false -o $trayDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать тестовое приложение в трее.' }
    $testTray = Start-Process -FilePath (Join-Path $trayDirectory 'm3nuxa.App.exe') -WorkingDirectory $trayDirectory -WindowStyle Hidden -PassThru
}
try {
$env:m3nuxa_CHECK_LANGUAGE = $Language
[IO.File]::WriteAllText($settingsPath, (@{ Language = $Language } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$checkProcess = Start-Process -FilePath (Join-Path $checkDirectory 'm3nuxa.Editor.Checks.exe') -WorkingDirectory $checkDirectory -WindowStyle Hidden -PassThru
winapp ui wait-for CreateFolder -a $checkProcess.Id -t 5000
if ($LASTEXITCODE -ne 0) { throw 'Тестовое окно не готово к вводу.' }
if (-not $checkProcess.WaitForExit(20000)) {
    Stop-Process -Id $checkProcess.Id
    throw 'Проверки редактора не завершились за 20 секунд.'
}
$result = Join-Path $checkDirectory 'result.txt'
if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
if ($checkProcess.ExitCode -ne 0) { throw "Проверки редактора завершились с кодом $($checkProcess.ExitCode)." }
} finally {
    if ($checkProcess -and -not $checkProcess.HasExited) { Stop-Process -Id $checkProcess.Id }
    if ($testTray -and -not $testTray.HasExited) { Stop-Process -Id $testTray.Id }
    if ($null -eq $originalSettings) { Remove-Item -LiteralPath $settingsPath -Force -ErrorAction SilentlyContinue }
    else { [IO.File]::WriteAllBytes($settingsPath, $originalSettings) }
    $env:m3nuxa_CHECK_LANGUAGE = $previousLanguage
}
