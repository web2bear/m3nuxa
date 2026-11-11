param(
    [Parameter(Mandatory)][long]$WindowHandle,
    [Parameter(Mandatory)][string]$Group,
    [Parameter(Mandatory)][string]$Child,
    [Parameter(Mandatory)][string]$EmptyGroup
)

# Run against a Russian-language test panel already open at its root. Clicks affect the desktop.
$ErrorActionPreference = 'Stop'
function Invoke-Check([string[]]$Arguments) {
    $output = & winapp @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "winapp failed: $output" }
    return $output
}
function Assert-Visible([string]$Expected) {
    $tree = Invoke-Check @('ui', 'inspect', '-w', "$WindowHandle", '-d', '6')
    if (-not $tree.Contains($Expected)) { throw "Expected '$Expected' in panel: $tree" }
}
Invoke-Check @('ui', 'click', $Group, '-w', "$WindowHandle") | Out-Null
Assert-Visible $Child
Invoke-Check @('ui', 'click', 'На уровень вверх', '-w', "$WindowHandle") | Out-Null
Assert-Visible $EmptyGroup
Invoke-Check @('ui', 'click', $EmptyGroup, '-w', "$WindowHandle") | Out-Null
Assert-Visible 'Здесь пока нет пунктов'
Invoke-Check @('ui', 'click', 'Домой', '-w', "$WindowHandle") | Out-Null
Assert-Visible $Group
Write-Output 'PASS: group opens, Up returns, empty group opens, Home returns.'
