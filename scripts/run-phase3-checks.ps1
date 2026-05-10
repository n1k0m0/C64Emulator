param(
    [int]$BenchmarkCycles = 500000
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot "artifacts\phase3"
$exe = Join-Path $repoRoot "C64Emulator\bin\x64\Release\C64Emulator.exe"

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

dotnet build (Join-Path $repoRoot "C64Emulator.sln") -c Release -p:Platform=x64

& $exe --check-roms | Tee-Object -FilePath (Join-Path $artifacts "rom_check.log")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $exe --self-test-cpu (Join-Path $artifacts "cpu_self_test.log")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $exe --accuracy-tests (Join-Path $artifacts "accuracy_tests.log")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $exe --benchmark $BenchmarkCycles (Join-Path $artifacts "benchmark.log")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Phase 3 checks completed. Logs: $artifacts"
