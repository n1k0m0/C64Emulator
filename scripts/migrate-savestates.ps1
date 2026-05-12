param(
    [string]$EmulatorExe = "C64Emulator\bin\x64\Release\C64Emulator.exe",
    [string]$SaveDirectory = "",
    [string]$LogPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$exePath = Resolve-Path -LiteralPath (Join-Path $repoRoot $EmulatorExe)

if ([string]::IsNullOrWhiteSpace($SaveDirectory)) {
    $SaveDirectory = Join-Path $env:APPDATA "C64Emulator\saves"
}

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $repoRoot "artifacts\savestate-migration.log"
}

$resolvedSaveDirectory = [System.IO.Path]::GetFullPath($SaveDirectory)
$resolvedLogPath = [System.IO.Path]::GetFullPath($LogPath)
$logDirectory = [System.IO.Path]::GetDirectoryName($resolvedLogPath)
if ($logDirectory) {
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
}

& $exePath --migrate-savestates $resolvedSaveDirectory $resolvedLogPath
if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
    throw "Savestate migration failed with exit code $LASTEXITCODE"
}

Write-Host "Savestate migration completed."
Write-Host "Save directory: $resolvedSaveDirectory"
Write-Host "Log: $resolvedLogPath"
