param(
    [ValidateSet('App', 'Editor', 'Both', 'None')]
    [string]$Component = 'Both'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'dist/debug'

# Editor discovers App processes by name; stop portable instances as well.
foreach ($process in @(Get-Process -Name 'm3nuxa.App', 'm3nuxa.Editor' -ErrorAction SilentlyContinue)) {
    Stop-Process -Id $process.Id -Force
    Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
}

foreach ($project in @('m3nuxa.Editor', 'm3nuxa.App')) {
    dotnet publish (Join-Path $root "src/$project/$project.csproj") -c Debug -p:Platform=x64 -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:DebugType=portable -o $output
    if ($LASTEXITCODE -ne 0) { throw "Debug publish failed: $project" }
}

foreach ($name in @('m3nuxa.App.exe', 'm3nuxa.App.pdb', 'm3nuxa.App.pri', 'm3nuxa.Editor.exe', 'm3nuxa.Editor.pdb', 'm3nuxa.Editor.pri')) {
    if (-not (Test-Path -LiteralPath (Join-Path $output $name))) { throw "Missing debug artifact: $name" }
}

Write-Host "Debug output: $output"
if ($Component -ne 'None') {
    Start-Process -FilePath (Join-Path $output 'm3nuxa.App.exe') -WorkingDirectory $output -WindowStyle Hidden
}
if ($Component -in @('Editor', 'Both')) {
    Start-Process -FilePath (Join-Path $output 'm3nuxa.Editor.exe') -WorkingDirectory $output -WindowStyle Hidden
    & (Join-Path $PSScriptRoot 'Move-TestWindow.ps1')
}
