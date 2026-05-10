param(
    [string]$Manifest = "docs\golden-manifest.sample.json",
    [string]$OutputDirectory = "artifacts\golden-reference",
    [switch]$Accept,
    [string]$AcceptedManifest = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "C64Emulator\C64Emulator.csproj"
$manifestPath = if ([System.IO.Path]::IsPathRooted($Manifest)) { $Manifest } else { Join-Path $repoRoot $Manifest }
$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }

dotnet build (Join-Path $repoRoot "C64Emulator.sln") -p:Platform=x64

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$exe = Join-Path $repoRoot "C64Emulator\bin\x64\Debug\C64Emulator.exe"

function Invoke-C64Emulator {
    param([string[]]$Arguments)

    $process = Start-Process -FilePath $exe -ArgumentList $Arguments -NoNewWindow -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        exit $process.ExitCode
    }
}

Invoke-C64Emulator @("--golden-run", $manifestPath, $outputPath)

if ($Accept) {
    $resultPath = Join-Path $outputPath "golden-results.json"
    $targetManifest = $AcceptedManifest
    if ([string]::IsNullOrWhiteSpace($targetManifest)) {
        $targetManifest = $manifestPath
    } elseif (-not [System.IO.Path]::IsPathRooted($targetManifest)) {
        $targetManifest = Join-Path $repoRoot $targetManifest
    }

    Invoke-C64Emulator @("--golden-accept", $manifestPath, $resultPath, $targetManifest)
}
