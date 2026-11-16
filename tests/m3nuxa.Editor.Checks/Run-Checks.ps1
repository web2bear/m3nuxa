param([ValidateSet('en-US', 'ru-RU')][string]$Language = 'en-US')

$ErrorActionPreference = 'Stop'
$checkDirectory = Join-Path $PSScriptRoot 'bin/ui-checks'
$winappCommand = Get-Command winapp -ErrorAction SilentlyContinue
if (-not $winappCommand) { throw 'Для UI-проверки редактора требуется WinApp CLI 0.7.0 или новее.' }
$winappVersionLine = @(& winapp --version 2>$null) | Where-Object { $_ -match '^\s*v?(\d+\.\d+\.\d+)(?:\s|$)' } | Select-Object -Last 1
if (-not $winappVersionLine -or [version]([regex]::Match($winappVersionLine, '\d+\.\d+\.\d+').Value) -lt [version]'0.7.0') {
    throw 'Для UI-проверки редактора требуется released WinApp CLI 0.7.0 или новее.'
}

dotnet build (Join-Path $PSScriptRoot 'm3nuxa.Editor.Checks.csproj') -c Release -p:Platform=x64 -p:PublishReadyToRun=false -o $checkDirectory
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать проверки редактора.' }

$testTray = $null
$checkProcess = $null
$settingsPath = Join-Path $checkDirectory 'app-language.json'
$resultPath = Join-Path $checkDirectory "result-$Language.txt"
$originalSettings = if (Test-Path -LiteralPath $settingsPath) { [IO.File]::ReadAllBytes($settingsPath) } else { $null }
$previousLanguage = $env:m3nuxa_CHECK_LANGUAGE
$previousResultPath = $env:m3nuxa_CHECK_RESULT
if (-not (Get-Process -Name 'm3nuxa.App' -ErrorAction SilentlyContinue)) {
    $trayDirectory = Join-Path $PSScriptRoot 'bin/test-tray'
    dotnet publish (Join-Path $PSScriptRoot '../../src/m3nuxa.App/m3nuxa.App.csproj') -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishReadyToRun=false -o $trayDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать тестовое приложение в трее.' }
    $testTray = Start-Process -FilePath (Join-Path $trayDirectory 'm3nuxa.App.exe') -WorkingDirectory $trayDirectory -WindowStyle Hidden -PassThru
}
try {
$env:m3nuxa_CHECK_LANGUAGE = $Language
$env:m3nuxa_CHECK_RESULT = $resultPath
Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
[IO.File]::WriteAllText($settingsPath, (@{ Language = $Language } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$checkProcess = Start-Process -FilePath (Join-Path $checkDirectory 'm3nuxa.Editor.Checks.exe') -ArgumentList @('--language', $Language) -WorkingDirectory $checkDirectory -WindowStyle Hidden -PassThru
winapp ui wait-for ConfigurationSelector -a $checkProcess.Id -t 5000 --on local
if ($LASTEXITCODE -ne 0) { throw 'Тестовое окно не готово к вводу.' }
if (-not $checkProcess.WaitForExit(20000)) {
    Stop-Process -Id $checkProcess.Id
    throw 'Проверки редактора не завершились за 20 секунд.'
}
if (Test-Path -LiteralPath $resultPath) { Get-Content -LiteralPath $resultPath }
if ($checkProcess.ExitCode -ne 0) { throw "Проверки редактора завершились с кодом $($checkProcess.ExitCode)." }
} finally {
    if ($checkProcess -and -not $checkProcess.HasExited) { Stop-Process -Id $checkProcess.Id }
    if ($testTray -and -not $testTray.HasExited) { Stop-Process -Id $testTray.Id }
    if ($null -eq $originalSettings) { Remove-Item -LiteralPath $settingsPath -Force -ErrorAction SilentlyContinue }
    else { [IO.File]::WriteAllBytes($settingsPath, $originalSettings) }
    $env:m3nuxa_CHECK_LANGUAGE = $previousLanguage
    $env:m3nuxa_CHECK_RESULT = $previousResultPath
}
