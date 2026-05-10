param(
    [string]$Configuration = "Release",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$artifactsDir = Join-Path $repoRoot "artifacts"
$publishDir = Join-Path $artifactsDir "publish\win-x64"
$projectPath = Join-Path $repoRoot "C64Emulator\C64Emulator.csproj"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $versionFile = Join-Path $repoRoot "VERSION"
    if (Test-Path -LiteralPath $versionFile) {
        $Version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = "0.1.0"
}

$resolvedArtifactsDir = [System.IO.Path]::GetFullPath($artifactsDir)
$resolvedPublishDir = [System.IO.Path]::GetFullPath($publishDir)
if (-not $resolvedPublishDir.StartsWith($resolvedArtifactsDir, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear publish directory outside artifacts: $resolvedPublishDir"
}

if (Test-Path -LiteralPath $resolvedPublishDir) {
    Remove-Item -LiteralPath $resolvedPublishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $resolvedPublishDir -Force | Out-Null

dotnet publish $projectPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:Platform=x64 `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -o $resolvedPublishDir

Get-ChildItem -LiteralPath $resolvedPublishDir -Recurse -File |
    Where-Object { $_.Extension -in ".bin", ".prg", ".d64" } |
    Remove-Item -Force

Write-Host "Published C64 Emulator $Version to $resolvedPublishDir"
