[CmdletBinding()]
param(
    [string]$Version = '1.2.0',
    [string]$InnoSetupCompiler,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
$staging = Join-Path $repo 'artifacts\installer-staging'
$output = Join-Path $repo 'artifacts\installer'

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $File $($Arguments -join ' ')" }
}

if (-not (Test-Path $dotnet)) { throw 'The local .NET SDK is missing. Run scripts/bootstrap.ps1 once before packaging.' }
if (-not $InnoSetupCompiler) {
    $candidates = @(
        (Join-Path $repo '.tools\inno\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
    ) | Where-Object { $_ -and (Test-Path $_) }
    $InnoSetupCompiler = $candidates | Select-Object -First 1
}
if (-not $InnoSetupCompiler) {
    throw 'Inno Setup 6 is required only to build the installer. Install it from https://jrsoftware.org/isdl.php or pass -InnoSetupCompiler. End users need only the generated Setup.exe.'
}

$resolvedStaging = [IO.Path]::GetFullPath($staging)
$expectedArtifacts = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedStaging.StartsWith($expectedArtifacts, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to clear a staging directory outside artifacts.' }
if (Test-Path $resolvedStaging) { Remove-Item -LiteralPath $resolvedStaging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging, $output | Out-Null

Push-Location $repo
try {
    $env:NUGET_PACKAGES = Join-Path $repo '.cache\nuget'
    $env:DOTNET_ROOT = Join-Path $repo '.tools\dotnet'
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
    Invoke-Checked $dotnet @('restore', 'DS3Minimap.slnx', '--locked-mode')
    Invoke-Checked $dotnet @('build', 'DS3Minimap.slnx', '--no-restore', '-c', 'Release')
    if (-not $SkipTests) { Invoke-Checked $dotnet @('test', 'DS3Minimap.slnx', '--no-build', '-c', 'Release') }
    Invoke-Checked $dotnet @('publish', 'src/DS3Minimap.App/DS3Minimap.App.csproj', '--no-restore', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', (Join-Path $staging 'app'))
    Invoke-Checked $dotnet @('publish', 'src/DS3Minimap.MapBaker/DS3Minimap.MapBaker.csproj', '--no-restore', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', (Join-Path $staging 'baker'))
    Invoke-Checked $InnoSetupCompiler @("/DMyAppVersion=$Version", (Join-Path $repo 'installer\DS3Minimap.iss'))
    $installer = Join-Path $output "DS3Minimap-Setup-$Version-win-x64.exe"
    if (-not (Test-Path $installer)) { throw 'Inno Setup completed without producing the installer.' }
    Write-Host "Installer: $installer" -ForegroundColor Green
} finally {
    Pop-Location
}
