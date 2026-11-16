param(
    [Parameter(Mandatory)] [string]$Directory,
    [string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Directory)
foreach ($file in @('m3nuxa.App.exe', 'm3nuxa.Editor.exe', 'm3nuxa.App.pri', 'm3nuxa.Editor.pri',
        'm3nuxa.App.deps.json', 'm3nuxa.Editor.deps.json', 'm3nuxa.App.runtimeconfig.json', 'm3nuxa.Editor.runtimeconfig.json',
        'icon-packs/Boxicons/LICENSE.txt', 'icon-packs/Boxicons/bx-folder.png', 'icon-packs/Fluent/LICENSE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $file) -PathType Leaf)) { throw "Missing distribution file: $file" }
}
foreach ($name in @('configs', 'logs', 'backups', 'm3nuxa.App.config', 'config.json', 'active-config.txt', 'icon-picker-folder.txt')) {
    if (Test-Path -LiteralPath (Join-Path $root $name)) { throw "User data must not be included in a release: $name" }
}
$allowedJson = @('m3nuxa.App.deps.json', 'm3nuxa.Editor.deps.json', 'm3nuxa.App.runtimeconfig.json', 'm3nuxa.Editor.runtimeconfig.json',
    'workloads.json', 'workloads.365.json', 'workloads.j32.json', 'workloads.lnl.json', 'workloads.qnn.json', 'workloads.stx.json')
foreach ($file in Get-ChildItem -LiteralPath $root -File -Force) {
    if ($file.Extension -eq '.config' -or ($file.Extension -eq '.json' -and $file.Name -notin $allowedJson)) {
        throw "Unexpected configuration file in release: $($file.Name)"
    }
}
foreach ($folder in Get-ChildItem -LiteralPath $root -Directory) {
    try { $culture = [Globalization.CultureInfo]::GetCultureInfo([string]$folder.Name) }
    catch { if ($_.Exception.InnerException -is [Globalization.CultureNotFoundException]) { continue }; throw }
    if ($culture.Name -notin @('en-US', 'ru-RU')) { throw "Unexpected language directory: $($folder.Name)" }
}
if ($ExpectedVersion) {
    $version = $ExpectedVersion -replace '^v', ''
    foreach ($name in @('m3nuxa.App.exe', 'm3nuxa.Editor.exe')) {
        $actual = (Get-Item -LiteralPath (Join-Path $root $name)).VersionInfo.ProductVersion.Split('+')[0]
        if ($actual -ne $version) { throw "$name version $actual does not match tag $ExpectedVersion" }
    }
}
Write-Host "Portable contents verified: $root"
