$ErrorActionPreference = 'Stop'

dotnet build (Join-Path $PSScriptRoot 'm3nuxa.Editor.Checks.csproj') -c Release -p:Platform=x64 -p:PublishReadyToRun=false
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать проверки редактора.' }

$checkDirectory = Join-Path $PSScriptRoot 'bin/x64/Release/net10.0-windows10.0.19041.0/win-x64'
$checkProcess = Start-Process -FilePath (Join-Path $checkDirectory 'm3nuxa.Editor.Checks.exe') -WorkingDirectory $checkDirectory -WindowStyle Hidden -PassThru
if (-not $checkProcess.WaitForExit(20000)) {
    Stop-Process -Id $checkProcess.Id
    throw 'Проверки редактора не завершились за 20 секунд.'
}
$result = Join-Path $checkDirectory 'result.txt'
if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
if ($checkProcess.ExitCode -ne 0) { throw "Проверки редактора завершились с кодом $($checkProcess.ExitCode)." }
