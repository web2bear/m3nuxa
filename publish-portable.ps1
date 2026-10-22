param(
    [ValidateSet('win-x64', 'win-x86', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot 'dist/m3nuxa'
$platform = $RuntimeIdentifier.Substring(4)

foreach ($process in @(Get-Process -Name 'm3nuxa.App', 'm3nuxa.Editor' -ErrorAction SilentlyContinue)) {
    Write-Host "Останавливаю $($process.ProcessName) (PID $($process.Id)) перед публикацией."
    Stop-Process -Id $process.Id -Force
    Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
}

dotnet publish (Join-Path $PSScriptRoot 'src/m3nuxa.Editor/m3nuxa.Editor.csproj') -c Release -p:Platform=$platform -r $RuntimeIdentifier --self-contained true -p:PublishTrimmed=false -p:PublishReadyToRun=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Не удалось опубликовать редактор.' }

dotnet publish (Join-Path $PSScriptRoot 'src/m3nuxa.App/m3nuxa.App.csproj') -c Release -r $RuntimeIdentifier --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Не удалось опубликовать приложение.' }

foreach ($name in @('m3nuxa.App.exe', 'm3nuxa.Editor.exe', 'm3nuxa.Editor.pri')) {
    if (-not (Test-Path -LiteralPath (Join-Path $output $name))) { throw "В публикации отсутствует $name" }
}

foreach ($name in @('m3nuxa.Mcp.exe', 'm3nuxa.Mcp.pdb')) {
    $legacyPath = Join-Path $output $name
    if (Test-Path -LiteralPath $legacyPath) { Remove-Item -LiteralPath $legacyPath -Force }
}

# dotnet publish не удаляет ресурсы, оставшиеся от предыдущей публикации.
$allowedLanguages = @('en-US', 'ru-RU')
$cultures = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($culture in [System.Globalization.CultureInfo]::GetCultures([System.Globalization.CultureTypes]::AllCultures)) {
    if ($culture.Name) { [void]$cultures.Add($culture.Name) }
}
foreach ($directory in Get-ChildItem -LiteralPath $output -Directory) {
    if (-not $cultures.Contains($directory.Name) -or $directory.Name -in $allowedLanguages) { continue }
    $files = @(Get-ChildItem -LiteralPath $directory.FullName -Recurse -File)
    if ($files.Where({ $_.Name -notlike '*.mui' -and $_.Name -notlike '*.resources.dll' }).Count -gt 0) {
        throw "Языковая папка содержит неожиданные файлы: $($directory.FullName)"
    }
    if ([System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($directory.FullName)) -ne [System.IO.Path]::GetFullPath($output)) {
        throw "Языковая папка вне каталога публикации: $($directory.FullName)"
    }
    Remove-Item -LiteralPath $directory.FullName -Recurse -Force
}

Write-Host "Portable-папка готова: $output"
$app = Start-Process -FilePath (Join-Path $output 'm3nuxa.App.exe') -WorkingDirectory $output -PassThru
Write-Host "m3nuxa.App запущен (PID $($app.Id))."
