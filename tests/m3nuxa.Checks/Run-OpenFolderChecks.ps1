$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$root = Join-Path $env:TEMP ('m3nuxa-open-folder-' + [guid]::NewGuid().ToString('N'))
$folder = Join-Path $root 'folder with spaces'
$shell = New-Object -ComObject Shell.Application
$window = $null
try {
    New-Item -ItemType Directory -Path $folder | Out-Null
    dotnet run --project (Join-Path $PSScriptRoot 'm3nuxa.Checks.csproj') -- --open-folder $folder
    if ($LASTEXITCODE -ne 0) { throw 'ActionExecutor failed.' }
    for ($i = 0; $i -lt 80 -and !$window; $i++) {
        Start-Sleep -Milliseconds 250
        foreach ($candidate in @($shell.Windows())) {
            try {
                if ($candidate.Document.Folder.Self.Path -eq $folder) { $window = $candidate; break }
            } catch { }
        }
    }
    if (!$window) { throw "Explorer did not open the expected folder: $folder" }
    $hwnd = [long]$window.HWND
    & (Join-Path $PSScriptRoot '../../tools/Move-TestWindow.ps1') -ProcessName explorer -WindowHandle $hwnd
    $screen = [System.Windows.Forms.Screen]::FromHandle([IntPtr]$hwnd)
    $hasSecond = @([System.Windows.Forms.Screen]::AllScreens | Where-Object { !$_.Primary }).Count -gt 0
    if ($hasSecond -and $screen.Primary) { throw 'Explorer test window is not on the second monitor.' }
    Write-Host "PASS: ActionExecutor opened the expected folder in Explorer; screen=$($screen.DeviceName), primary=$($screen.Primary)"
} finally {
    if ($window) { $window.Quit() }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
