param(
    [int]$TraceCycles = 20000,
    [int]$TraceSampleInterval = 63,
    [int]$RegressionCycles = 500000
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot "artifacts\phase4"
$exe = Join-Path $repoRoot "C64Emulator\bin\x64\Release\C64Emulator.exe"

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

dotnet build (Join-Path $repoRoot "C64Emulator.sln") -c Release -p:Platform=x64

& $exe --accuracy-tests (Join-Path $artifacts "accuracy_tests.log")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $exe --trace-cycles $TraceCycles (Join-Path $artifacts "trace_cycles.csv") $TraceSampleInterval
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $exe --regression-run "-" $RegressionCycles (Join-Path $artifacts "regression_boot.log") (Join-Path $artifacts "regression_boot.ppm")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Phase 4 checks completed. Logs: $artifacts"
