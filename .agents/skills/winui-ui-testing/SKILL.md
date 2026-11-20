---
name: winui-ui-testing
description: "Write and run automated UI tests for Windows desktop apps (WinUI 3, WPF, WinForms, Win32) with the winapp ui harness: one batch script, assertions, input, file pickers, dialogs, screenshots, in the current Windows session. Use when asked for UI tests or to verify a feature end to end."
---

### Scope and execution boundary

`winapp ui` uses Windows UI Automation (UIA), so the AutomationId-based workflow works with any Windows desktop framework, packaged or unpackaged. Skip WinUI-specific binding/dialog advice for other frameworks.

Run UI tests in the current Windows session inside the Hyper-V VM. Discover the installed contract with `winapp run --help` and `winapp ui <verb> --help`.

Real input and screenshots require an unlocked, interactive desktop session. When connected remotely, check that the session can receive input and capture images; a readable UI tree alone does not establish readiness. If the session is unavailable, report the limitation and preserve diagnostic evidence.

### Approach and command discovery

Prefer a single scripted batch over a long series of interactive calls. If you wrote the app, use its XAML/source AutomationIds directly; otherwise inspect its current tree and read source for hidden/lazy flyouts and dialogs. Either way, validate a real window/tree before passing an accessibility audit.

Core verbs: `list-windows`, `inspect`, `search`, `get-property`, `get-value`, `wait-for`; `invoke`, `click`, `set-value`, `focus`, `scroll-into-view`; `send-keys`, `hover`, `drag`, `touch`, `pen`; `screenshot`, `record`. Use each verb's `--help` for selectors and options, rather than guessing from this short reference.

### Step 1: Select the local session

Launch the app in the current Windows session or reuse a known local process. The `winapp run` JSON includes `ProcessId`; use that local PID for subsequent UI commands. Discover windows again after restarting the application.

### Step 2: Write the batch

Create `ui-tests.ps1`, replace the sample AutomationIds/expected values with the app's requirements, and add the relevant examples below **inside the outer `try`**, before its `finally`. The two small command helpers only route and check CLI calls; they do not implement a target manager. Use them for **every** added command so an earlier native failure cannot be hidden by a later success.

```powershell
# ui-tests.ps1
param(
    [int]$AppPid = 0,
    [ValidateNotNullOrEmpty()][string]$WorkflowId = [guid]::NewGuid().ToString('N'),
    [string]$ArtifactDirectory = (Join-Path '.\ui-test-artifacts' ([guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$ScopeArgs = @()
$previousWorkflowId = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = $WorkflowId
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$uiStarted = $false

function Invoke-WinAppChecked {
    param([string[]]$Arguments)
    $global:LASTEXITCODE = $null
    $output = @(& winapp @Arguments)
    $code = $global:LASTEXITCODE
    if ($null -eq $code -or $code -ne 0) {
        throw "winapp $($Arguments -join ' ') failed (exit $code). $($output -join "`n")"
    }
    $output
}

function Invoke-Ui {
    param([string[]]$Arguments, [string]$Window)
    $selector = if ($Window) { @('-w', $Window) } else { @('-a', "$AppPid") }
    Invoke-WinAppChecked (@('ui') + $Arguments + $ScopeArgs + $selector)
}

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try {
        & $Script | Out-Null
        $results.Add(@{ name = $Name; status = 'PASS' })
    } catch {
        $results.Add(@{ name = $Name; status = 'FAIL'; detail = "$_" })
    }
}

function Save-Screenshot {
    param([string]$Name)
    $path = Join-Path $ArtifactDirectory $Name
    if (Test-Path -LiteralPath $path) { throw "Choose a new evidence path: $path" }
    Invoke-Ui @('screenshot', '--output', $path) -Window $hwnd | Out-Null
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Screenshot was not delivered to the local filesystem: $path"
    }
    $screenshots.Add($path)
}

try {
    $ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
    New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
    if ($AppPid -lt 0) { throw 'AppPid must be positive, or zero to launch.' }
    if ($AppPid -eq 0) {
        $launch = Invoke-WinAppChecked (@('run', '.') + $ScopeArgs + @('--detach', '--json')) |
            ConvertFrom-Json
        if ($launch.Error -or -not $launch.ProcessId -or [int]$launch.ProcessId -le 0) {
            throw "No app ProcessId. Discover the local process and rerun with -AppPid. $($launch.Error)"
        }
        $AppPid = [int]$launch.ProcessId
    }

    $uiStarted = $true
    $windows = @(Invoke-Ui @('list-windows', '--json') | ConvertFrom-Json)
    $main = $windows | Where-Object { $_.hwnd -and $_.hwnd -ne '0' -and $_.title -ne 'PopupHost' } |
        Select-Object -First 1
    if (-not $main) { throw 'No app window found in the current session; rediscover after restarting the application.' }
    $hwnd = [string]$main.hwnd

    Test-UI 'Initial screenshot delivered' { Save-Screenshot '01-initial.png' }
    Test-UI 'Home exists' { Invoke-Ui @('wait-for', 'NavHome', '-t', '3000') }
    Test-UI 'Navigate to Settings' {
        Invoke-Ui @('invoke', 'NavSettings')
        Invoke-Ui @('wait-for', 'TxtUserName', '-t', '3000')
    }
    Test-UI 'Save username' {
        Invoke-Ui @('set-value', 'TxtUserName', 'TestUser')
        Invoke-Ui @('invoke', 'BtnSave')
        Invoke-Ui @('wait-for', 'TxtUserName', '--value', 'TestUser', '-t', '2000')
    }
    Test-UI 'Default values' {
        Invoke-Ui @('wait-for', 'CmbTheme', '--value', 'System default', '-t', '2000')
        Invoke-Ui @('wait-for', 'TglLogging', '--value', 'Off', '-t', '2000')
    }
    Test-UI 'App controls have AutomationIds' {
        $inspection = Invoke-Ui @('inspect', '--interactive', '--json') -Window $hwnd | ConvertFrom-Json
        function Get-Elements($nodes) {
            foreach ($node in $nodes) {
                if ($null -ne $node) { $node; Get-Elements $node.children }
            }
        }
        $allElements = @(Get-Elements @($inspection.windows | ForEach-Object { $_.elements }))
        if (-not $allElements.Count) { throw 'Inspection returned no elements; not an accessibility PASS.' }
        $appElements = @($allElements | Where-Object {
            $_.type -match 'Button|TextBox|ComboBox|CheckBox|ToggleSwitch|TabItem|Edit' -and
            $_.name -notmatch 'Minimize|Maximize|Close|System' -and
            $_.className -notmatch 'PickerHost|#32770|CabinetWClass'
        })
        if (-not $appElements.Count) { throw 'No app controls were audited; adjust selectors rather than passing.' }
        $missingId = @($appElements | Where-Object { -not $_.automationId })
        if ($missingId.Count) {
            throw "Missing AutomationIds: $(($missingId | ForEach-Object { "$($_.type) '$($_.name)'" }) -join ', ')"
        }
    }
    Test-UI 'Final screenshot delivered' { Save-Screenshot '02-settings.png' }
} catch {
    $results.Add(@{ name = 'Target setup / test execution'; status = 'FAIL'; detail = "$_" })
} finally {
    try {
        if ($uiStarted) { Invoke-WinAppChecked (@('ui', 'yield') + $ScopeArgs) | Out-Null }
    } catch {
        $results.Add(@{ name = 'Release UI workflow'; status = 'FAIL'; detail = "$_" })
    } finally {
        $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflowId
    }
}

$failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
$report = [ordered]@{
    appPid = $AppPid; workflowId = $WorkflowId
    passed = $results.Count - $failed; failed = $failed
    results = @($results.ToArray()); screenshots = @($screenshots.ToArray())
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath '.\test-results.json' -Encoding utf8
Write-Host "Passed: $($report.passed) | Failed: $failed | Results: .\test-results.json"
if ($failed) { exit 1 }
exit 0
```

Within a test, **throw**, not `exit`; the script fails on CLI errors, empty inspections, and undelivered evidence. Screenshots target the known main HWND so each lands at one exact local path; use `--capture-screen` when popups must be included.

### Step 3: Run, read, and visually verify

```powershell
.\ui-tests.ps1
if ($LASTEXITCODE -ne 0) { throw 'UI batch failed; read test-results.json.' }
$report = Get-Content -LiteralPath '.\test-results.json' -Raw | ConvertFrom-Json
$report.screenshots
```

For an existing local process, use `.\ui-tests.ps1 -AppPid 1234`.

View **each local PNG** listed in `test-results.json` with the image-viewing tool. UIA PASS cannot detect clipping, overlap, incorrect theming, or content bleeding past its container. Fail visual review for unintended scrollbars, unintended ellipses, clipped hero/right-edge controls, overlapping rows, unbalanced whitespace, cramped/vast spacing, incorrect Light/Dark/High Contrast, or missing focus/hover/error states. Capture meaningful states immediately after their interactions, not just at the end.

If fixes are requested, batch-fix failures, rebuild/relaunch with the same target using the template (omit `-AppPid` to launch again), and capture the new PID. Maximum **two** fix-and-rerun cycles; then report remaining failures. Do not declare completion without the structured results **and** visual review.

### Assertions and coverage

Write tests for every requested requirement. Use `wait-for --value` for TextBox/NumberBox values, RichEditBox text, ComboBox selection, toggle/checkbox `On`/`Off`, and TextBlock/label text. `--contains` matches a substring; `--gone` waits for disappearance. Use `-p IsEnabled --value True` only for a specific property. Invoke buttons/nav items, wait for page-specific elements, and test default/error/empty states.

The following examples are additions to the template, not independent shells. `Invoke-Ui` always supplies the chosen scope plus `-a $AppPid`, or scope plus `-w $Window`. For raw reads, pass `--json` and parse it; plain stdout can include advisory messages. Use `scroll-into-view` before asserting a virtualized item below the fold.

```powershell
Test-UI 'Status and enabled state' {
    Invoke-Ui @('wait-for', 'StatusBar', '--value', 'saved', '--contains', '-t', '3000')
    Invoke-Ui @('wait-for', 'BtnSave', '-p', 'IsEnabled', '--value', 'True', '-t', '3000')
    $value = Invoke-Ui @('get-value', 'TxtUserName', '--json') | ConvertFrom-Json
    if ($value.text -ne 'TestUser') { throw 'Wrong username.' }
}
```

### File pickers, flyouts, and ContentDialogs

Pickers run in a separate `PickerHost` process. Discover their HWND in the **same target**; `-w` changes the window selector, never the target. Use a filename that exists in the current Windows session.

```powershell
Test-UI 'Open file picker' {
    Invoke-Ui @('invoke', 'BtnOpenFile')
    Start-Sleep -Seconds 1
    $allWindows = @(Invoke-WinAppChecked (@('ui', 'list-windows', '--json') + $ScopeArgs) | ConvertFrom-Json)
    $pickers = @($allWindows | Where-Object { $_.hwnd -and [string]$_.ownerHwnd -eq $hwnd -and $_.title -match 'Open|Save' })
    if ($pickers.Count -ne 1) { throw 'Expected one picker; inspect the selected target again.' }
    $pickerHwnd = [string]$pickers[0].hwnd
    Invoke-Ui @('inspect', '--interactive', '--json') -Window $pickerHwnd
    Invoke-Ui @('set-value', 'FileNameControlHost', 'test.txt') -Window $pickerHwnd
    Invoke-Ui @('invoke', 'Open') -Window $pickerHwnd
    Invoke-Ui @('wait-for', 'StatusBar', '--value', 'opened', '--contains', '-t', '3000')
}

Test-UI 'Copy context menu' {
    Invoke-Ui @('click', 'LstItems', '--right')
    Invoke-Ui @('wait-for', 'MnuCopy', '-t', '2000')
    Invoke-Ui @('invoke', 'MnuCopy')
    Invoke-Ui @('wait-for', 'StatusText', '--value', 'Copied', '-t', '2000')
}

Test-UI 'Confirm deletion' {
    Invoke-Ui @('invoke', 'BtnDelete')
    Invoke-Ui @('wait-for', 'Primary', '-t', '2000')
    Invoke-Ui @('invoke', 'Primary')
    Invoke-Ui @('wait-for', 'Primary', '--gone', '-t', '3000')
}
```

For Save/Cancel pickers, substitute the appropriate action and assertion. MenuBar headers and flyout items use `invoke` and `wait-for` similarly. ContentDialogs live in the app window; `Primary`, `Secondary`, and `Close` are common selectors, but inspect the actual open dialog because custom AutomationIds are not guaranteed.

### Keyboard, hover, drag, touch, and pen

`send-keys --via send-input` is required for accelerators and reliable per-character input in WinUI/WPF text controls. `post-message` is HWND-targeted but does not produce per-character `KeyDown`. `--target` focuses first; `--verbatim` types literal key names. System shortcuts require the CLI's explicit permission flags; do not use them as a workaround for desktop-readiness errors.

```powershell
Test-UI 'Keyboard save' {
    Invoke-Ui @('send-keys', 'hello world', '--target', 'TxtName', '--via', 'send-input')
    Invoke-Ui @('send-keys', 'ctrl+s', '--via', 'send-input')
    Invoke-Ui @('wait-for', 'StatusBar', '--value', 'saved', '--contains', '-t', '2000')
}
Test-UI 'Tooltip' {
    Invoke-Ui @('hover', 'BtnInfo')
    Invoke-Ui @('wait-for', 'InfoTooltip', '-t', '2000')
}
Test-UI 'Reorder items' {
    Invoke-Ui @('drag', 'ItemA', 'ItemB')
    Invoke-Ui @('wait-for', 'StatusBar', '--value', 'reordered', '--contains', '-t', '2000')
}
Test-UI 'Touch and ink input' {
    Invoke-Ui @('touch', 'LstFeed', '-g', 'swipe', '--direction', 'up', '--distance', '400')
    Invoke-Ui @('pen', 'InkCanvas', '--path', '50,50 120,80 200,60', '--pressure', '0.8')
    Save-Screenshot '03-ink.png'
    # Add the app-specific state assertion; successful injection alone is not functional proof.
}
```

Use `winapp ui drag --help`, `winapp ui touch --help`, and `winapp ui pen --help` for coordinates, long-press/dwell, pinch/stretch, pressure/tilt/eraser support. Read coordinates in the current UI tree.

### Recordings and workflow coordination

```powershell
Test-UI 'Video delivered' {
    $video = Join-Path $ArtifactDirectory 'flow.mp4'
    if (Test-Path -LiteralPath $video) { throw 'Choose a new video evidence path.' }
    Invoke-Ui @('record', '--duration-sec', '6', '--fps', '30', '--output', $video)
    if (-not (Test-Path -LiteralPath $video -PathType Leaf) -or (Get-Item -LiteralPath $video).Length -eq 0) {
        throw "Recording was not delivered to the local filesystem: $video"
    }
}
```

Screenshot and recording **`--output` paths are local paths**. Use fresh output paths and check the exit status and actual local files. `--capture-screen` can include popups outside the app window. For an interrupted recording, preserve `stopReason`, `partialOutput`, `recoveryHint`, and the reported partial/recovery paths; a nonempty partial file is not necessarily playable.

WinApp CLI arbitrates desktop-changing commands automatically. Without a workflow ID, each command releases its turn immediately; with one, the four-second grace covers tight script bursts, not agent reasoning. Use the **same `WINAPP_UI_WORKFLOW_ID`** for cooperating commands, including concurrent recording and interaction; independent flows need distinct IDs. The template accepts `-WorkflowId` for this purpose, sets it in its own process, restores the previous value, and calls `winapp ui yield` in `finally`. Fresh shell tool calls do not inherit changes from an earlier shell: set the same ID on **every** cooperating invocation. A separate concurrent recording process must also check its exit code/delivery, use the same scoped PID, and finish before the test runner yields. Do not start an independent-ID recording that blocks the interactions it is meant to capture. After a pause, inspect again and reopen menus/dialogs another workflow may have changed.

### Persistence

Check the actual application data file in the current Windows session. Use the app's real storage location when verifying that changes persist across restarts.

### Binding and selector gotchas

- `set-value` does not commit default WinUI `x:Bind TwoWay` TextBox bindings until focus changes. Prefer `UpdateSourceTrigger=PropertyChanged` in the app; otherwise `invoke` Save or `focus` another control before checking persistence.
- RichEditBox/RichTextBox generally need `send-keys --via send-input`, not UIA `set-value`.
- Use `$AppPid`, never PowerShell's read-only automatic `$Pid`.
- A picker HWND needs `-w`; rediscover HWNDs and PIDs after an application restart.
