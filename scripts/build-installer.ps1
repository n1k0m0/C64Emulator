param(
    [string]$Configuration = "Release",
    [string]$Version = "",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

function Get-C64VersionInfo([string]$VersionText) {
    $parts = @($VersionText.Split("."))
    while ($parts.Count -lt 4) {
        $parts += "0"
    }

    return ($parts[0..3] -join ".")
}

function Find-InnoSetupCompiler {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $candidates += (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates += (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates += (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    return $null
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$versionFile = Join-Path $repoRoot "VERSION"
if ([string]::IsNullOrWhiteSpace($Version) -and (Test-Path -LiteralPath $versionFile)) {
    $Version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = "0.1.0"
}

$iscc = Find-InnoSetupCompiler
if ([string]::IsNullOrWhiteSpace($iscc)) {
    throw "Inno Setup 6 was not found. Install it with: winget install --id JRSoftware.InnoSetup -e --accept-package-agreements --accept-source-agreements"
}

$publishDir = Join-Path $repoRoot "artifacts\publish\win-x64"
$installerDir = Join-Path $repoRoot "artifacts\installer"
$scriptPath = Join-Path $repoRoot "installer\C64Emulator.iss"

if (-not $SkipPublish) {
    & (Join-Path $repoRoot "scripts\publish-win-x64.ps1") -Configuration $Configuration -Version $Version
}

New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

$versionInfo = Get-C64VersionInfo $Version
$isccArgs = @(
    "/DAppVersion=$Version",
    "/DAppVersionInfo=$versionInfo",
    "/DSourceDir=$publishDir",
    "/DOutputDir=$installerDir",
    $scriptPath
)

& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE"
}

$setupPath = Join-Path $installerDir "C64Emulator-$Version-win-x64-setup.exe"
Write-Host "Installer written to $setupPath"
