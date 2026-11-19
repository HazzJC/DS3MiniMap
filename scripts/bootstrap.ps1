[CmdletBinding()]
param(
    [string]$GameDir,
    [switch]$NoMapBake,
    [switch]$SkipTests,
    [switch]$ForceDependencies,
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $repo '.tools'
$dotnetDir = Join-Path $tools 'dotnet'
$dotnet = Join-Path $dotnetDir 'dotnet.exe'
$deps = Join-Path $repo '.deps'
$smithbox = Join-Path $deps 'smithbox'
$cache = Join-Path $repo '.cache'
$sdkVersion = '10.0.302'
$smithboxCommit = 'b48478ed2a0462bccfe4a18552731a80b5013307'

New-Item -ItemType Directory -Force -Path $tools, $deps, $cache | Out-Null

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $File $($Arguments -join ' ')" }
}

function Install-LocalDotnet {
    if ((Test-Path $dotnet) -and -not $ForceDependencies) {
        $installed = (& $dotnet --version 2>$null)
        if ($installed -eq $sdkVersion) { return }
    }
    if ($Offline) { throw ".NET SDK $sdkVersion is not cached. Re-run without -Offline." }
    if (Test-Path $dotnetDir) {
        $resolved = [IO.Path]::GetFullPath($dotnetDir)
        $expectedParent = [IO.Path]::GetFullPath($tools) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to replace SDK outside .tools.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    $installer = Join-Path $cache 'dotnet-install.ps1'
    Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & $installer -Version $sdkVersion -Architecture x64 -InstallDir $dotnetDir -NoPath
    if (-not (Test-Path $dotnet)) { throw 'The local .NET installation did not produce dotnet.exe.' }
    if ((& $dotnet --version) -ne $sdkVersion) { throw 'The downloaded .NET SDK version did not match the pin.' }
}

function Install-Smithbox {
    $smithboxSparsePaths = @(
        'src/Andre/SoulsFormats',
        'src/Havok/HKX2',
        'src/Smithbox.Data/Assets/UXM Dictionaries',
        'src/Smithbox.Data/Assets/PARAM/DS3/Defs',
        'src/Smithbox.Data/Assets/PARAM/DS3/Param Row Names/English',
        'LICENSE'
    )
    $requiredSmithboxFiles = @(
        'src/Andre/SoulsFormats/SoulsFormats/Andre.SoulsFormats.csproj',
        'src/Havok/HKX2/Havok.HKX2.csproj',
        'src/Smithbox.Data/Assets/PARAM/DS3/Defs/ItemLotParam.xml',
        'src/Smithbox.Data/Assets/PARAM/DS3/Defs/BonfireWarpParam.xml',
        'src/Smithbox.Data/Assets/PARAM/DS3/Param Row Names/English/ItemLotParam.json',
        'src/Smithbox.Data/Assets/PARAM/DS3/Param Row Names/English/BonfireWarpParam.json'
    )
    if ((Test-Path (Join-Path $smithbox '.git')) -and -not $ForceDependencies) {
        $actual = (& git -C $smithbox rev-parse HEAD).Trim()
        if ($actual -eq $smithboxCommit) {
            $missing = $requiredSmithboxFiles | Where-Object { -not (Test-Path (Join-Path $smithbox $_)) }
            if (-not $missing) { return }
            if ($Offline) { throw "Pinned Smithbox checkout is incomplete in offline mode: $($missing -join ', ')" }
            Invoke-Checked git (@('-C', $smithbox, 'sparse-checkout', 'set') + $smithboxSparsePaths)
            $missing = $requiredSmithboxFiles | Where-Object { -not (Test-Path (Join-Path $smithbox $_)) }
            if ($missing) { throw "Pinned Smithbox checkout is incomplete: $($missing -join ', ')" }
            return
        }
    }
    if ($Offline) { throw "Smithbox $smithboxCommit is not cached. Re-run without -Offline." }
    if (Test-Path $smithbox) {
        $resolved = [IO.Path]::GetFullPath($smithbox)
        $expectedParent = [IO.Path]::GetFullPath($deps) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to replace dependency outside .deps.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    Invoke-Checked git @('clone', '--filter=blob:none', '--no-checkout', 'https://github.com/vawser/Smithbox.git', $smithbox)
    Invoke-Checked git @('-C', $smithbox, 'sparse-checkout', 'init', '--cone')
    Invoke-Checked git (@('-C', $smithbox, 'sparse-checkout', 'set') + $smithboxSparsePaths)
    Invoke-Checked git @('-C', $smithbox, 'checkout', '--detach', $smithboxCommit)
    $actual = (& git -C $smithbox rev-parse HEAD).Trim()
    if ($actual -ne $smithboxCommit) { throw "Smithbox pin verification failed: $actual" }
}

function Find-GameDirectory {
    if ($GameDir) { return [IO.Path]::GetFullPath($GameDir) }
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
    return $candidates | Where-Object { Test-Path (Join-Path $_ 'DarkSoulsIII.exe') } | Select-Object -First 1
}

function Test-GameComplete([string]$Path) {
    $required = @('DarkSoulsIII.exe','Data0.bhd','Data0.bdt','Data1.bhd','Data1.bdt','Data2.bhd','Data2.bdt','Data3.bhd','Data3.bdt','Data4.bhd','Data4.bdt','Data5.bhd','Data5.bdt')
    foreach ($name in $required) {
        $file = Join-Path $Path $name
        if (-not (Test-Path $file) -or (Get-Item $file).Length -eq 0) { return $false }
    }
    return $true
}

Install-LocalDotnet
Install-Smithbox
$env:DOTNET_ROOT = $dotnetDir
$env:PATH = "$dotnetDir;$env:PATH"
$env:NUGET_PACKAGES = Join-Path $cache 'nuget'

Push-Location $repo
try {
    if ($Offline) {
        $requiredAssets = @(
            'artifacts/obj/DS3Minimap.App/project.assets.json',
            'artifacts/obj/DS3Minimap.Core/project.assets.json',
            'artifacts/obj/DS3Minimap.MapBaker/project.assets.json',
            'artifacts/obj/DS3Minimap.Memory/project.assets.json',
            'artifacts/obj/DS3Minimap.Rendering/project.assets.json',
            'artifacts/obj/DS3Minimap.Tests/project.assets.json'
        )
        if ($requiredAssets | Where-Object { -not (Test-Path (Join-Path $repo $_)) }) {
            throw 'Offline restore assets are missing. Run bootstrap once without -Offline.'
        }
        Write-Host 'Offline mode: using the existing locked restore assets; no network sources will be contacted.'
    } else {
        Invoke-Checked $dotnet @('restore', 'DS3Minimap.slnx', '--use-lock-file')
    }
    Invoke-Checked $dotnet @('build', 'DS3Minimap.slnx', '--no-restore', '-c', 'Release')
    if (-not $SkipTests) { Invoke-Checked $dotnet @('test', 'DS3Minimap.slnx', '--no-build', '-c', 'Release') }
    Invoke-Checked $dotnet @('publish', 'src/DS3Minimap.App/DS3Minimap.App.csproj', '--no-restore', '-c', 'Release', '-o', 'artifacts/publish/win-x64')

    $detectedGame = Find-GameDirectory
    if (-not $NoMapBake) {
        if (-not $detectedGame) {
            Write-Warning 'DS3 was not found. Build succeeded; map baking was skipped.'
        } elseif (-not (Test-GameComplete $detectedGame)) {
            Write-Warning 'Steam download incomplete. Build succeeded; rerun this command after Steam finishes.'
        } else {
            Invoke-Checked $dotnet @('artifacts/bin/Release/net10.0/win-x64/DS3Minimap.MapBaker.dll', '--game-dir', $detectedGame)
        }
    }
    Write-Host 'DS3 Minimap bootstrap completed successfully.' -ForegroundColor Green
} finally {
    Pop-Location
}
