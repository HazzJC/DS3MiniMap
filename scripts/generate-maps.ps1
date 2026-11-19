[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$Output,
    [string]$ReviewDir,
    [switch]$Force,
    [switch]$OpenPreviews
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'

if (-not (Test-Path $dotnet)) {
    throw 'The local SDK is missing. Run scripts/bootstrap.ps1 once before generating maps.'
}

if (-not $GameDir) {
    $candidates = [Collections.Generic.List[string]]::new()
    $candidates.Add('C:\Program Files (x86)\Steam\steamapps\common\DARK SOULS III\Game')
    $libraryFile = 'C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf'
    if (Test-Path $libraryFile) {
        foreach ($line in Get-Content $libraryFile) {
            if ($line -match '"path"\s+"([^"]+)"') {
                $root = $Matches[1] -replace '\\\\', '\'
                $candidates.Add((Join-Path $root 'steamapps\common\DARK SOULS III\Game'))
            }
        }
    }
    $GameDir = $candidates | Where-Object { Test-Path (Join-Path $_ 'DarkSoulsIII.exe') } | Select-Object -First 1
}
if (-not $GameDir) { throw 'Dark Souls III was not found. Pass -GameDir with the directory containing DarkSoulsIII.exe.' }

if (-not $Output) { $Output = Join-Path $env:LOCALAPPDATA 'DS3Minimap\maps\1.15.2\base.dsmaps' }
if ($ReviewDir) {
    if (-not [IO.Path]::IsPathRooted($ReviewDir)) { $ReviewDir = Join-Path $repo $ReviewDir }
    $previewDir = [IO.Path]::GetFullPath($ReviewDir)
} else {
    $previewDir = Join-Path (Split-Path -Parent $Output) 'diagnostics'
}

Push-Location $repo
try {
    & $dotnet build 'src/DS3Minimap.MapBaker/DS3Minimap.MapBaker.csproj' -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Map generator build failed. Run scripts/bootstrap.ps1 to restore dependencies.' }
    $arguments = @('artifacts/bin/Release/net10.0/win-x64/DS3Minimap.MapBaker.dll', '--game-dir', $GameDir, '--output', $Output, '--preview-dir', $previewDir)
    if ($Force) { $arguments += '--force' }
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Map generation or validation failed with exit code $LASTEXITCODE. See $previewDir\validation-report.json." }
    Write-Host "Map package: $Output" -ForegroundColor Green
    Write-Host "Visual validation: $previewDir\index.html" -ForegroundColor Green
    if ($OpenPreviews) { Start-Process (Join-Path $previewDir 'index.html') }
} finally {
    Pop-Location
}
