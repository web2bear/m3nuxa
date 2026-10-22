$ErrorActionPreference = 'Stop'
dotnet build (Join-Path $PSScriptRoot 'm3nuxa.Launcher.Checks.csproj') -c Release -p:Platform=x64 -r win-x64
if ($LASTEXITCODE -ne 0) { throw 'Launcher checks build failed.' }
$checkDirectory = Join-Path $PSScriptRoot 'bin/x64/Release/net10.0-windows10.0.19041.0/win-x64'
$process = Start-Process -FilePath (Join-Path $checkDirectory 'm3nuxa.Launcher.Checks.exe') -WorkingDirectory $checkDirectory -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(20000)) { throw 'Launcher checks timed out.' }
Get-Content (Join-Path $checkDirectory 'result.txt')
if ($process.ExitCode -ne 0) { throw 'Launcher checks failed.' }
