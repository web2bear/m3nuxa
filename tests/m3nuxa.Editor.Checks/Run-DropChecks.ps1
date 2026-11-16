 $ErrorActionPreference = 'Stop'
 $out = Join-Path $PSScriptRoot 'bin/ui-checks'
 $diagnosticDirectory = Join-Path $env:TEMP ('m3nuxa-drop-diag-' + [guid]::NewGuid().ToString('N'))
 $diagnosticLog = Join-Path $diagnosticDirectory 'run.log'
 New-Item -ItemType Directory -Path $diagnosticDirectory -Force | Out-Null
 Write-Host "Drop diagnostics: $diagnosticDirectory"
 $winappCommand = Get-Command winapp -ErrorAction SilentlyContinue
 if (-not $winappCommand) { throw 'WinApp CLI 0.7.0 or newer is required.' }
 $winappVersionLine = @(& winapp --version 2>$null) | Where-Object { $_ -match '^\s*v?(\d+\.\d+\.\d+)(?:\s|$)' } | Select-Object -Last 1
 if (-not $winappVersionLine -or [version]([regex]::Match($winappVersionLine, '\d+\.\d+\.\d+').Value) -lt [version]'0.7.0') {
     throw 'A released WinApp CLI 0.7.0 or newer is required.'
 }
 $fixture = Join-Path $env:TEMP ('m3nuxa-drop-' + [guid]::NewGuid().ToString('N'))
 $state = Join-Path $fixture 'state.json'
 New-Item -ItemType Directory -Path (Join-Path $fixture 'DropFolder') -Force | Out-Null
 Set-Content -LiteralPath (Join-Path $fixture 'DropFile.cmd') -Value '@exit /b 0'
 Set-Content -LiteralPath (Join-Path $fixture 'Document.txt') -Value 'test'
 $previousState = $env:m3nuxa_DROP_CHECK
 $previousLanguage = $env:m3nuxa_CHECK_LANGUAGE
 $settings = Join-Path $out 'app-language.json'
 $originalSettings = if (Test-Path -LiteralPath $settings) { [IO.File]::ReadAllBytes($settings) } else { $null }
 $env:m3nuxa_DROP_CHECK = $state
 $editor = $null; $explorerUiaWindow = $null; $explorerShellWindow = $null
 $shell = New-Object -ComObject Shell.Application
 Add-Type 'using System; using System.Runtime.InteropServices; public static class DropTestWindows { [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int width, int height, bool repaint); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h); }'
 function Invoke-Winapp([string[]]$Arguments) {
     $localArguments = @($Arguments) + @('--on', 'local')
     $raw = @(& winapp @localArguments 2>&1)
     $exitCode = $LASTEXITCODE
     $rawText = @($raw | ForEach-Object { [string]$_ })
     Add-Content -LiteralPath $diagnosticLog -Value "`n> winapp $($localArguments -join ' ') (exit $exitCode)"
     Add-Content -LiteralPath $diagnosticLog -Value ($rawText -join "`n")
     if ($exitCode -ne 0) { throw "winapp failed: $($Arguments -join ' '). $($rawText -join ' ')" }
     return ($rawText | ConvertFrom-Json)
 }
 function Flatten($items) { foreach ($item in $items) { $item; if ($item.children) { Flatten $item.children } } }
 function Get-FreshExplorerWindow {
     $candidate = $null
     foreach ($window in @($shell.Windows())) {
         try {
             if ([string]::Equals([string]$window.Document.Folder.Self.Path, $fixture, [StringComparison]::OrdinalIgnoreCase)) {
                 $candidate = $window
                 break
             }
         } catch { }
     }
     if (-not $candidate) { return $null }
     $handle = [long]$candidate.HWND
     $windows = @(Invoke-Winapp @('ui','list-windows','--json'))
     $uiaWindow = $windows | Where-Object { [long]$_.hwnd -eq $handle } | Select-Object -First 1
     Add-Content -LiteralPath $diagnosticLog -Value "Exact Explorer path=$($candidate.Document.Folder.Self.Path); COM HWND=$handle; UIA match=$([bool]$uiaWindow); process=$($uiaWindow.processId); title=$($uiaWindow.title)"
     if ($uiaWindow) { return [pscustomobject]@{ ShellWindow = $candidate; UiaWindow = $uiaWindow } }
     return $null
 }
 function Elements([string]$window) {
     $tree = Invoke-Winapp @('ui','inspect','-w',$window,'--depth','15','--json')
     return @(Flatten $tree.windows.elements)
 }
 try {
     dotnet build (Join-Path $PSScriptRoot 'm3nuxa.Editor.Checks.csproj') -c Release -p:Platform=x64 -p:PublishReadyToRun=false -o $out
     if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
     $env:m3nuxa_CHECK_LANGUAGE = 'en-US'
     [IO.File]::WriteAllText($settings, '{"Language":"en-US"}')
     $editor = Start-Process -FilePath (Join-Path $out 'm3nuxa.Editor.Checks.exe') -ArgumentList @('--language', 'en-US') -WorkingDirectory $out -WindowStyle Hidden -PassThru
     for ($i=0; $i -lt 50 -and (!(Test-Path $state) -or $editor.MainWindowHandle -eq 0); $i++) { Start-Sleep -Milliseconds 200; $editor.Refresh() }
     if (!(Test-Path $state) -or $editor.MainWindowHandle -eq 0) { throw 'Test editor did not start.' }
     Start-Process -FilePath explorer.exe -ArgumentList ('"{0}"' -f $fixture) | Out-Null
     for ($i=0; $i -lt 40; $i++) {
         $freshWindow = Get-FreshExplorerWindow
         if ($freshWindow) {
             $explorerShellWindow = $freshWindow.ShellWindow
             $explorerUiaWindow = $freshWindow.UiaWindow
             break
         }
         Start-Sleep -Milliseconds 250
     }
     if (!$explorerShellWindow -or !$explorerUiaWindow) { throw "Explorer fixture window did not start for exact path: $fixture" }
     Add-Type -AssemblyName System.Windows.Forms
     $screen = [System.Windows.Forms.Screen]::FromHandle($editor.MainWindowHandle)
     $work = $screen.WorkingArea
     $half = [int]($work.Width / 2)
     [void][DropTestWindows]::MoveWindow($editor.MainWindowHandle,$work.Left,$work.Top,$half,$work.Height,$true)
     [void][DropTestWindows]::MoveWindow([IntPtr]$explorerUiaWindow.hwnd,($work.Left+$half),$work.Top,($work.Width-$half),$work.Height,$true)
     Start-Sleep -Seconds 1
     # Explorer UIA elements become stale after OLE drops. Cache each source and target coordinate once, before the first drop.
     $explorerElements = Elements "$($explorerUiaWindow.hwnd)"
     $editorElements = Elements "$($editor.MainWindowHandle.ToInt64())"
     $explorerElements | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath (Join-Path $diagnosticDirectory 'explorer-before-drops.json')
     $editorElements | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath (Join-Path $diagnosticDirectory 'editor-before-drops.json')
     $cachedSourcePoints = @{}
     foreach ($name in @('DropFolder','DropFile.cmd','Document.txt')) {
         $visibleNames = @($name)
         $nameWithoutExtension = [IO.Path]::GetFileNameWithoutExtension($name)
         if ($nameWithoutExtension -notin $visibleNames) { $visibleNames += $nameWithoutExtension }
         $sourceMatches = @($explorerElements | Where-Object { $_.type -eq 'ListItem' -and $_.name -in $visibleNames })
         if ($sourceMatches.Count -ne 1) { throw "Expected one Explorer item for $name but found $($sourceMatches.Count). Diagnostics: $diagnosticDirectory" }
         $source = $sourceMatches[0]
         $cachedSourcePoints[$name] = '{0},{1}' -f [int]($source.x+$source.width/2),[int]($source.y+$source.height/2)
     }
     $targetMatches = @($editorElements | Where-Object { $_.type -eq 'TreeItem' -and $_.name -like '*Drop group*' })
     if ($targetMatches.Count -ne 1) { throw "Expected one destination group; found $($targetMatches.Count). Diagnostics: $diagnosticDirectory" }
     $cachedTargetPoint = '{0},{1}' -f [int]($targetMatches[0].x+$targetMatches[0].width/2),[int]($targetMatches[0].y+$targetMatches[0].height/2)
     Add-Content -LiteralPath $diagnosticLog -Value "Cached source points: $($cachedSourcePoints.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" } | Join-String -Separator '; '); cached target group point=$cachedTargetPoint"
     foreach ($name in @('DropFolder','DropFile.cmd','Document.txt')) {
         $freshWindow = Get-FreshExplorerWindow
         if (-not $freshWindow) { throw "Could not reacquire Explorer for exact fixture path: $fixture" }
         $explorerShellWindow = $freshWindow.ShellWindow
         $oldHwnd = $explorerUiaWindow.hwnd
         $explorerUiaWindow = $freshWindow.UiaWindow
         Add-Content -LiteralPath $diagnosticLog -Value "Reacquired Explorer before ${name}: old HWND=$oldHwnd; current HWND=$($explorerUiaWindow.hwnd)"
         $null = Invoke-Winapp @('ui','status','-w',"$($explorerUiaWindow.hwnd)",'--json')
         if (Test-Path -LiteralPath $state) { Copy-Item -LiteralPath $state -Destination (Join-Path $diagnosticDirectory "state-before-$name.json") -Force }
         $from = $cachedSourcePoints[$name]
         $to = $cachedTargetPoint
         [System.Windows.Forms.SendKeys]::SendWait('%')
         [void][DropTestWindows]::BringWindowToTop([IntPtr]$explorerUiaWindow.hwnd)
         [void][DropTestWindows]::SetForegroundWindow([IntPtr]$explorerUiaWindow.hwnd)
         Start-Sleep -Milliseconds 300
         $null = Invoke-Winapp @('ui','drag',$from,$to,'-w',"$($explorerUiaWindow.hwnd)",'--dwell-ms','350','--json')
         Add-Content -LiteralPath $diagnosticLog -Value "$name from=$from to=$to"
         $expected = Join-Path $fixture $name
         $found = $false
         for ($i=0; $i -lt 30; $i++) {
             Start-Sleep -Milliseconds 200
             try { $config = Get-Content -Raw -LiteralPath $state | ConvertFrom-Json } catch { continue }
             if (@($config.Items[0].Children | Where-Object { $_.Steps -and $_.Steps[0].Path -eq $expected }).Count -eq 1) { $found = $true; break }
         }
         if (Test-Path -LiteralPath $state) { Copy-Item -LiteralPath $state -Destination (Join-Path $diagnosticDirectory "state-after-$name.json") -Force }
         if (!$found) { throw "Drop failed: $name was not added to group with its original path." }
         if ($name -eq "Document.txt" -and @($config.Items[0].Children | Where-Object { $_.Steps -and $_.Steps[0].Path -eq $expected -and $_.Steps[0].Kind -eq "OpenItem" }).Count -ne 1) { throw "Document was not assigned OpenItem." }
         Write-Host "PASS: Explorer drop $name into group"
     }
 } finally {
     $env:m3nuxa_DROP_CHECK = $previousState
     $env:m3nuxa_CHECK_LANGUAGE = $previousLanguage
     if ($null -eq $originalSettings) { Remove-Item -LiteralPath $settings -Force -ErrorAction SilentlyContinue }
     else { [IO.File]::WriteAllBytes($settings, $originalSettings) }
     if ($editor -and !$editor.HasExited) { Stop-Process -Id $editor.Id }
     if ($explorerShellWindow) { try { $explorerShellWindow.Quit() } catch { } }
     foreach ($candidate in @($shell.Windows())) {
         try {
             if ([string]::Equals([string]$candidate.Document.Folder.Self.Path, $fixture, [StringComparison]::OrdinalIgnoreCase)) { $candidate.Quit() }
         } catch { }
     }
     Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
 }
