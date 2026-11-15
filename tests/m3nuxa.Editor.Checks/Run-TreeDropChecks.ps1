param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'bin/ui-checks'
if (!$NoBuild) {
    dotnet build (Join-Path $PSScriptRoot 'm3nuxa.Editor.Checks.csproj') -c Release -p:Platform=x64 -p:PublishReadyToRun=false -o $out
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
$previousCheck = $env:m3nuxa_TREE_DROP_CHECK
$previousResult = $env:m3nuxa_CHECK_RESULT
$result = Join-Path $env:TEMP ('m3nuxa-tree-drop-' + [guid]::NewGuid().ToString('N') + '.txt')
$process = $null
try {
    $env:m3nuxa_TREE_DROP_CHECK = '1'
    $env:m3nuxa_CHECK_RESULT = $result
    $process = Start-Process -FilePath (Join-Path $out 'm3nuxa.Editor.Checks.exe') -WorkingDirectory $out -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(20000)) { throw 'Tree drop checks timed out.' }
    if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
    if ($process.ExitCode -ne 0) { throw 'Tree drop checks failed.' }
} finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id }
    $env:m3nuxa_TREE_DROP_CHECK = $previousCheck
    $env:m3nuxa_CHECK_RESULT = $previousResult
    Remove-Item -LiteralPath $result -Force -ErrorAction SilentlyContinue
}
