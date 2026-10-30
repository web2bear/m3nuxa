param([string]$Browser = 'C:\Program Files\Google\Chrome\Application\chrome.exe')
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $Browser)) { throw "Browser not found: $Browser" }
Add-Type -AssemblyName System.Windows.Forms
Add-Type 'using System;using System.Runtime.InteropServices;public static class BrowserProbeWindows { [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int ht,bool repaint); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h); }'
$root = Join-Path $env:TEMP ('m3nuxa-browser-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$profile = Join-Path $root 'profile'
$result = Join-Path $root 'state.json'
$env:m3nuxa_DROP_CHECK = $result
$env:m3nuxa_OLE_PROBE = Join-Path $root 'ole.log'
$probe = $null; $browserProcess = $null
function Invoke-Winapp([string[]]$arguments) {
    $out = & winapp @arguments
    if ($LASTEXITCODE -ne 0) { throw "winapp failed: $($arguments -join ' ')" }
    $converted = $out | ConvertFrom-Json
    if ($converted -is [array]) { foreach ($item in $converted) { Write-Output $item } }
    else { Write-Output $converted }
}
function Flatten($nodes) { foreach ($node in $nodes) { $node; if ($node.children) { Flatten $node.children } } }
function Elements([string]$hwnd) { $tree = Invoke-Winapp @('ui','inspect','-w',$hwnd,'--depth','25','--json'); @(Flatten $tree.windows.elements) }
function Find-Node($hwnd, $name) { @(Elements "$hwnd" | Where-Object { $_.name -eq $name -and !$_.isOffscreen } | Select-Object -First 1)[0] }
function Activate-Window($hwnd) {
    [System.Windows.Forms.SendKeys]::SendWait('%')
    [void][BrowserProbeWindows]::BringWindowToTop([IntPtr]$hwnd)
    [void][BrowserProbeWindows]::SetForegroundWindow([IntPtr]$hwnd)
    Start-Sleep -Milliseconds 300
}
function Drop-Item($source, $target, $browserHwnd) {
    if (!$source -or !$target) { throw 'Source or destination element missing' }
    $from = '{0},{1}' -f [int]($source.x + [Math]::Min(60, $source.width / 2)), [int]($source.y + $source.height / 2)
    $to = '{0},{1}' -f [int]($target.x + $target.width / 2), [int]($target.y + $target.height / 2)
    Activate-Window $browserHwnd
    $null = Invoke-Winapp @('ui','drag',$from,$to,'-w',"$browserHwnd",'--dwell-ms','400','--json')
    Start-Sleep -Seconds 2
    Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
}
try {
    $probe = Start-Process (Join-Path $PSScriptRoot 'bin/ole-checks/m3nuxa.Editor.Checks.exe') -PassThru -WindowStyle Hidden
    $fixture = [Uri]::new((Join-Path $PSScriptRoot 'BrowserDragFixture.html')).AbsoluteUri
    $browserProcess = Start-Process -FilePath $Browser -ArgumentList @("--user-data-dir=$profile",'--no-first-run','--disable-sync','--disable-features=Translate','--force-renderer-accessibility',"--app=$fixture") -PassThru -WindowStyle Hidden
    for ($i=0; $i -lt 50; $i++) {
        $windows = @(Invoke-Winapp @('ui','list-windows','--json'))
        $targetWindow = $windows | Where-Object { $_.processId -eq $probe.Id -and $_.title } | Select-Object -First 1
        $browserWindow = $windows | Where-Object { $_.title -like '*m3nuxa browser drag fixture*' } | Select-Object -First 1
        if ($targetWindow -and $browserWindow) { break }
        Start-Sleep -Milliseconds 250
    }
    if (!$targetWindow -or !$browserWindow) { throw 'Browser or OLE probe did not open' }
    $screen = [System.Windows.Forms.Screen]::FromHandle([IntPtr]$targetWindow.hwnd)
    if ($screen.Primary) { Write-Host 'Only one screen available to the probe; using its working area.' }
    $work = $screen.WorkingArea; $half = [int]($work.Width / 2)
    [void][BrowserProbeWindows]::MoveWindow([IntPtr]$targetWindow.hwnd,$work.Left,$work.Top,$half,$work.Height,$true)
    [void][BrowserProbeWindows]::MoveWindow([IntPtr]$browserWindow.hwnd,($work.Left+$half),$work.Top,($work.Width-$half),$work.Height,$true)
    Start-Sleep -Seconds 1
    $target = @(Elements "$($targetWindow.hwnd)" | Where-Object { $_.type -eq 'TreeItem' -and $_.name -like '*Drop group*' } | Select-Object -First 1)[0]
    $link = Find-Node $browserWindow.hwnd 'Drag test link'
    $received = Drop-Item $link $target $browserWindow.hwnd
    if (@($received.Items[0].Children | Where-Object { $_.Action.Kind -eq 'OpenItem' -and $_.Action.Path -eq 'https://example.com/m3nuxa-drag-test' }).Count -ne 1) { throw 'Dropped link was not added to group as OpenItem' }
    Write-Host 'PASS: link added to group'
    Activate-Window $browserWindow.hwnd
    $null = Invoke-Winapp @('ui','click','Select sample text','-w',"$($browserWindow.hwnd)",'--json')
    $sample = Find-Node $browserWindow.hwnd 'm3nuxa selected text fixture'
    $received = Drop-Item $sample $target $browserWindow.hwnd
    if (@($received.Items[0].Children | Where-Object { $_.Action.Kind -eq 'InsertText' -and $_.Action.Text -eq 'm3nuxa selected text fixture' }).Count -ne 1) { throw 'Dropped text was not added to group as InsertText' }
    Write-Host 'PASS: text added to group'
} finally {
    Remove-Item Env:m3nuxa_DROP_CHECK,Env:m3nuxa_OLE_PROBE -ErrorAction SilentlyContinue
    if ($probe -and !$probe.HasExited) { Stop-Process -Id $probe.Id -ErrorAction SilentlyContinue }
    if ($browserProcess -and !$browserProcess.HasExited) { Stop-Process -Id $browserProcess.Id -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
