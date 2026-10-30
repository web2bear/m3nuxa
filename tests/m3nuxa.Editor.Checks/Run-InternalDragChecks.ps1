param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'bin/ui-checks'
$state = Join-Path $env:TEMP ('m3nuxa-internal-' + [guid]::NewGuid().ToString('N') + '.json')
$editor = $null
$previousState = $env:m3nuxa_DROP_CHECK
Add-Type -AssemblyName System.Windows.Forms
Add-Type 'using System; using System.Runtime.InteropServices; public static class InternalDropFocus { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd); }'
function Invoke-Winapp([string[]]$arguments) {
    $output = & winapp @arguments
    if ($LASTEXITCODE -ne 0) { throw "winapp failed: $($arguments -join ' ')" }
    $output | ConvertFrom-Json
}
function Flatten($nodes) {
    foreach ($node in $nodes) { $node; if ($node.children) { Flatten $node.children } }
}
try {
    if (!$NoBuild) {
        dotnet build (Join-Path $PSScriptRoot 'm3nuxa.Editor.Checks.csproj') -c Release -p:Platform=x64 -p:PublishReadyToRun=false -o $out
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    $env:m3nuxa_DROP_CHECK = $state
    $editor = Start-Process (Join-Path $out 'm3nuxa.Editor.Checks.exe') -PassThru
    for ($i=0; $i -lt 60 -and (!(Test-Path $state) -or $editor.MainWindowHandle -eq 0); $i++) { Start-Sleep -Milliseconds 200; $editor.Refresh() }
    if (!(Test-Path $state) -or $editor.MainWindowHandle -eq 0) { throw 'Test editor did not start.' }
    $null = Invoke-Winapp @('ui','invoke','Создать пункт меню','-a',"$($editor.Id)",'--json')
    $null = Invoke-Winapp @('ui','invoke','Действие','-a',"$($editor.Id)",'--json')
    Start-Sleep -Milliseconds 500
    $tree = Invoke-Winapp @('ui','inspect','-a',"$($editor.Id)",'--depth','15','--json')
    $rows = @(Flatten $tree.windows.elements | Where-Object { $_.type -eq 'TreeItem' })
    if ($rows.Count -ne 2) { throw "Expected two root rows; found $($rows.Count)." }
    $from = '{0},{1}' -f [int]($rows[1].x + 85),[int]($rows[1].y + $rows[1].height / 2)
    $to = '{0},{1}' -f [int]($rows[0].x + 85),[int]($rows[0].y + $rows[0].height / 2)
    [System.Windows.Forms.SendKeys]::SendWait('%')
    [void][InternalDropFocus]::SetForegroundWindow($editor.MainWindowHandle)
    $null = Invoke-Winapp @('ui','drag',$from,$to,'-a',"$($editor.Id)",'--dwell-ms','800','--json')
    $found = $false
    for ($i=0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 200
        try { $model = Get-Content -Raw -LiteralPath $state | ConvertFrom-Json } catch { continue }
        if ($model.Items.Count -eq 1 -and $model.Items[0].Kind -eq 'Group' -and $model.Items[0].Children.Count -eq 1 -and $model.Items[0].Children[0].Kind -eq 'Action') { $found = $true; break }
    }
    if (!$found) { throw 'Real internal drag did not move the action into the group.' }
    Write-Host 'PASS: internal TreeView drag moved action into group'
} finally {
    $env:m3nuxa_DROP_CHECK = $previousState
    if ($editor -and !$editor.HasExited) { Stop-Process -Id $editor.Id }
    Remove-Item -LiteralPath $state -Force -ErrorAction SilentlyContinue
}
