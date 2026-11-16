param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'bin/ui-checks'
$diagnosticDirectory = Join-Path $env:TEMP ('m3nuxa-internal-drag-diag-' + [guid]::NewGuid().ToString('N'))
$diagnosticLog = Join-Path $diagnosticDirectory 'run.log'
New-Item -ItemType Directory -Path $diagnosticDirectory -Force | Out-Null
$diagnosticLog | Set-Content -LiteralPath (Join-Path $diagnosticDirectory 'path.txt')
Write-Host "Internal drag diagnostics: $diagnosticDirectory"
$winappCommand = Get-Command winapp -ErrorAction SilentlyContinue
if (-not $winappCommand) { throw 'WinApp CLI 0.7.0 or newer is required.' }
$winappVersionLine = @(& winapp --version 2>$null) | Where-Object { $_ -match '^\s*v?(\d+\.\d+\.\d+)(?:\s|$)' } | Select-Object -Last 1
if (-not $winappVersionLine -or [version]([regex]::Match($winappVersionLine, '\d+\.\d+\.\d+').Value) -lt [version]'0.7.0') {
    throw 'A released WinApp CLI 0.7.0 or newer is required.'
}
$state = Join-Path $env:TEMP ('m3nuxa-internal-' + [guid]::NewGuid().ToString('N') + '.json')
$editor = $null
$previousState = $env:m3nuxa_DROP_CHECK
$previousLanguage = $env:m3nuxa_CHECK_LANGUAGE
$settings = Join-Path $out 'app-language.json'
$originalSettings = if (Test-Path -LiteralPath $settings) { [IO.File]::ReadAllBytes($settings) } else { $null }
Add-Type -AssemblyName System.Windows.Forms
Add-Type 'using System; using System.Runtime.InteropServices; public static class InternalDropFocus { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd); }'
function Invoke-Winapp([string[]]$arguments) {
    $localArguments = @($arguments) + @('--on', 'local')
    $output = & winapp @localArguments
    $exitCode = $LASTEXITCODE
    Add-Content -LiteralPath $diagnosticLog -Value "`n> winapp $($localArguments -join ' ') (exit $exitCode)"
    Add-Content -LiteralPath $diagnosticLog -Value ($output -join "`n")
    if ($exitCode -ne 0) { throw "winapp failed: $($arguments -join ' ')" }
    $output | ConvertFrom-Json
}
function Save-DragEvidence([string]$name, $tree, [string]$from, [string]$to) {
    $tree | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath (Join-Path $diagnosticDirectory "$name-tree.json")
    Add-Content -LiteralPath $diagnosticLog -Value "$name from=$from to=$to"
    if (Test-Path -LiteralPath $state) {
        Copy-Item -LiteralPath $state -Destination (Join-Path $diagnosticDirectory "$name-state-after.json") -Force
    }
}
function Flatten($nodes) {
    foreach ($node in $nodes) { $node; if ($node.children) { Flatten $node.children } }
}
try {
    if (!$NoBuild) {
        dotnet build (Join-Path $PSScriptRoot 'm3nuxa.Editor.Checks.csproj') -c Release -p:Platform=x64 -p:PublishReadyToRun=false -o $out
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    $env:m3nuxa_CHECK_LANGUAGE = 'ru-RU'
    [IO.File]::WriteAllText($settings, '{"Language":"ru-RU"}')
    $env:m3nuxa_DROP_CHECK = $state
    $editor = Start-Process -FilePath (Join-Path $out 'm3nuxa.Editor.Checks.exe') -ArgumentList @('--language', 'ru-RU') -WorkingDirectory $out -WindowStyle Hidden -PassThru
    Add-Content -LiteralPath $diagnosticLog -Value "Editor PID=$($editor.Id); state=$state"
    for ($i=0; $i -lt 60 -and (!(Test-Path $state) -or $editor.MainWindowHandle -eq 0); $i++) { Start-Sleep -Milliseconds 200; $editor.Refresh() }
    if (!(Test-Path $state) -or $editor.MainWindowHandle -eq 0) { throw 'Test editor did not start.' }
    [System.Windows.Forms.SendKeys]::SendWait('%')
    [void][InternalDropFocus]::SetForegroundWindow($editor.MainWindowHandle)
    $null = Invoke-Winapp @('ui','click','Default','--right','-a',"$($editor.Id)",'--json')
    $null = Invoke-Winapp @('ui','invoke','Добавить','-a',"$($editor.Id)",'--json')
    $null = Invoke-Winapp @('ui','invoke','Сниппет','-a',"$($editor.Id)",'--json')
    Start-Sleep -Milliseconds 500
    # Drag an unselected action; selecting the group first must not change the source.
    $null = Invoke-Winapp @('ui','click','Drop group','-a',"$($editor.Id)",'--json')
    Start-Sleep -Milliseconds 300
    $tree = Invoke-Winapp @('ui','inspect','-a',"$($editor.Id)",'--depth','15','--json')
    $rows = @(Flatten $tree.windows.elements | Where-Object { $_.type -eq 'TreeItem' })
    if ($rows.Count -ne 3) { throw "Expected configuration root, group and action; found $($rows.Count)." }
    $rows = @($rows | Select-Object -Skip 1)
    $from = '{0},{1}' -f [int]($rows[0].x + 85),[int]($rows[0].y + $rows[0].height / 2)
    $to = '{0},{1}' -f [int]($rows[1].x + 85),[int]($rows[1].y + $rows[1].height / 2)
    Save-DragEvidence 'action-to-group' $tree $from $to
    $null = Invoke-Winapp @('ui','status','-a',"$($editor.Id)",'--json')
    $null = Invoke-Winapp @('ui','screenshot','-a',"$($editor.Id)",'--output',(Join-Path $diagnosticDirectory 'action-to-group-before.png'),'--json')
    [System.Windows.Forms.SendKeys]::SendWait('%')
    [void][InternalDropFocus]::SetForegroundWindow($editor.MainWindowHandle)
    $null = Invoke-Winapp @('ui','drag',$from,$to,'-a',"$($editor.Id)",'--hold-ms','300','--dwell-ms','1000','--json')
    if (Test-Path -LiteralPath $state) { Copy-Item -LiteralPath $state -Destination (Join-Path $diagnosticDirectory 'action-to-group-state-immediate.json') -Force }
    $found = $false
    for ($i=0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 200
        try { $model = Get-Content -Raw -LiteralPath $state | ConvertFrom-Json } catch { continue }
        if ($model.Items.Count -eq 1 -and $model.Items[0].Kind -eq 'Group' -and $model.Items[0].Children.Count -eq 1 -and $model.Items[0].Children[0].Kind -eq 'Action') { $found = $true; break }
    }
    if (Test-Path -LiteralPath $state) { Copy-Item -LiteralPath $state -Destination (Join-Path $diagnosticDirectory 'action-to-group-state-after.json') -Force }
    $null = Invoke-Winapp @('ui','screenshot','-a',"$($editor.Id)",'--output',(Join-Path $diagnosticDirectory 'action-to-group-after.png'),'--json')
    if (!$found) { throw "Real internal drag did not move the action into the group. Diagnostics: $diagnosticDirectory" }
    Write-Host 'PASS: internal TreeView drag moved action into group'
    $tree = Invoke-Winapp @('ui','inspect','-a',"$($editor.Id)",'--depth','15','--json')
    $rows = @(Flatten $tree.windows.elements | Where-Object { $_.type -eq 'TreeItem' })
    if ($rows.Count -ne 3) { throw "Expected configuration root, group and child; found $($rows.Count)." }
    $rows = @($rows | Select-Object -Skip 1)
    $from = '{0},{1}' -f [int]($rows[1].x + 100),[int]($rows[1].y + $rows[1].height / 2)
    $to = '{0},{1}' -f [int]($rows[0].x + 30),[int]($rows[1].y + $rows[1].height + 50)
    Save-DragEvidence 'child-to-root' $tree $from $to
    $null = Invoke-Winapp @('ui','drag',$from,$to,'-a',"$($editor.Id)",'--hold-ms','300','--dwell-ms','1000','--json')
    if (Test-Path -LiteralPath $state) { Copy-Item -LiteralPath $state -Destination (Join-Path $diagnosticDirectory 'child-to-root-state-immediate.json') -Force }
    $found = $false
    for ($i=0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 200
        try { $model = Get-Content -Raw -LiteralPath $state | ConvertFrom-Json } catch { continue }
        if ($model.Items.Count -eq 2 -and $model.Items[0].Children.Count -eq 0 -and $model.Items[1].Kind -eq 'Action') { $found = $true; break }
    }
    if (Test-Path -LiteralPath $state) { Copy-Item -LiteralPath $state -Destination (Join-Path $diagnosticDirectory 'child-to-root-state-after.json') -Force }
    if (!$found) { throw "Real internal drag did not move the child to the root. Diagnostics: $diagnosticDirectory" }
    Write-Host 'PASS: internal TreeView drag moved child to root'
} finally {
    $env:m3nuxa_DROP_CHECK = $previousState
    $env:m3nuxa_CHECK_LANGUAGE = $previousLanguage
    if ($null -eq $originalSettings) { Remove-Item -LiteralPath $settings -Force -ErrorAction SilentlyContinue }
    else { [IO.File]::WriteAllBytes($settings, $originalSettings) }
    if ($editor -and !$editor.HasExited) { Stop-Process -Id $editor.Id }
    Remove-Item -LiteralPath $state -Force -ErrorAction SilentlyContinue
}
