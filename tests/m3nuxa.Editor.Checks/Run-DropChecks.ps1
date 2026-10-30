 $ErrorActionPreference = 'Stop'
 $out = Join-Path $PSScriptRoot 'bin/ui-checks'
 $fixture = Join-Path $env:TEMP ('m3nuxa-drop-' + [guid]::NewGuid().ToString('N'))
 $state = Join-Path $fixture 'state.json'
 New-Item -ItemType Directory -Path (Join-Path $fixture 'DropFolder') -Force | Out-Null
 Set-Content -LiteralPath (Join-Path $fixture 'DropFile.cmd') -Value '@exit /b 0'
 Set-Content -LiteralPath (Join-Path $fixture 'Document.txt') -Value 'test'
 $env:m3nuxa_DROP_CHECK = $state
 $editor = $null; $explorerWindow = $null
 Add-Type 'using System; using System.Runtime.InteropServices; public static class DropTestWindows { [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int width, int height, bool repaint); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h); }'
 function Invoke-Winapp([string[]]$Arguments) {
     $raw = & winapp @Arguments
     if ($LASTEXITCODE -ne 0) { throw "winapp failed: $($Arguments -join ' ')" }
     return ($raw | ConvertFrom-Json)
 }
 function Flatten($items) { foreach ($item in $items) { $item; if ($item.children) { Flatten $item.children } } }
 function Elements([string]$window) {
     $tree = Invoke-Winapp @('ui','inspect','-w',$window,'--depth','15','--json')
     return @(Flatten $tree.windows.elements)
 }
 try {
     dotnet build (Join-Path $PSScriptRoot 'm3nuxa.Editor.Checks.csproj') -c Release -p:Platform=x64 -p:PublishReadyToRun=false -o $out
     if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
     $editor = Start-Process (Join-Path $out 'm3nuxa.Editor.Checks.exe') -PassThru
     for ($i=0; $i -lt 50 -and (!(Test-Path $state) -or $editor.MainWindowHandle -eq 0); $i++) { Start-Sleep -Milliseconds 200; $editor.Refresh() }
     if (!(Test-Path $state) -or $editor.MainWindowHandle -eq 0) { throw 'Test editor did not start.' }
     Start-Process explorer.exe $fixture
     for ($i=0; $i -lt 40; $i++) {
         $explorerWindow = @(Invoke-Winapp @('ui','list-windows','--json') | Where-Object { $_.processName -eq 'explorer' -and $_.title -like "*$fixture*" }) | Select-Object -First 1
         if ($explorerWindow) { break }; Start-Sleep -Milliseconds 250
     }
     if (!$explorerWindow) { throw 'Explorer fixture window did not start.' }
     Add-Type -AssemblyName System.Windows.Forms
     $screen = [System.Windows.Forms.Screen]::FromHandle($editor.MainWindowHandle)
     $work = $screen.WorkingArea
     $half = [int]($work.Width / 2)
     [void][DropTestWindows]::MoveWindow($editor.MainWindowHandle,$work.Left,$work.Top,$half,$work.Height,$true)
     [void][DropTestWindows]::MoveWindow([IntPtr]$explorerWindow.hwnd,($work.Left+$half),$work.Top,($work.Width-$half),$work.Height,$true)
     Start-Sleep -Seconds 1
     foreach ($name in @('DropFolder','DropFile.cmd','Document.txt')) {
         $source = Elements "$($explorerWindow.hwnd)" | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq $name } | Select-Object -First 1
         $target = Elements "$($editor.MainWindowHandle.ToInt64())" | Where-Object { $_.type -eq 'TreeItem' -and $_.name -like '*Drop group*' } | Select-Object -First 1
         if (!$source -or !$target) { throw "Cannot find source $name or destination group." }
         $from = '{0},{1}' -f [int]($source.x+$source.width/2),[int]($source.y+$source.height/2)
         $to = '{0},{1}' -f [int]($target.x+$target.width/2),[int]($target.y+$target.height/2)
         [System.Windows.Forms.SendKeys]::SendWait('%')
         [void][DropTestWindows]::BringWindowToTop([IntPtr]$explorerWindow.hwnd)
         [void][DropTestWindows]::SetForegroundWindow([IntPtr]$explorerWindow.hwnd)
         Start-Sleep -Milliseconds 300
         $null = Invoke-Winapp @('ui','drag',$from,$to,'-w',"$($explorerWindow.hwnd)",'--dwell-ms','350','--json')
         $expected = Join-Path $fixture $name
         $found = $false
         for ($i=0; $i -lt 30; $i++) {
             Start-Sleep -Milliseconds 200
             try { $config = Get-Content -Raw -LiteralPath $state | ConvertFrom-Json } catch { continue }
             if (@($config.Items[0].Children | Where-Object { $_.Action.Path -eq $expected }).Count -eq 1) { $found = $true; break }
         }
         if (!$found) { throw "Drop failed: $name was not added to group with its original path." }
        if ($name -eq "Document.txt" -and @($config.Items[0].Children | Where-Object { $_.Action.Path -eq $expected -and $_.Action.Kind -eq "OpenItem" }).Count -ne 1) { throw "Document was not assigned OpenItem." }
         Write-Host "PASS: Explorer drop $name into group"
     }
 } finally {
     Remove-Item Env:m3nuxa_DROP_CHECK -ErrorAction SilentlyContinue
     if ($editor -and !$editor.HasExited) { Stop-Process -Id $editor.Id }
     if ($explorerWindow) { $shell = New-Object -ComObject Shell.Application; foreach ($window in $shell.Windows()) { if ($window.HWND -eq $explorerWindow.hwnd) { $window.Quit() } } }
     Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
 }
