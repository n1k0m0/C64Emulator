param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [int]$BenchmarkCycles = 2000000
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root "C64Emulator.sln"
$exe = Join-Path $root "C64Emulator\bin\$Platform\$Configuration\C64Emulator.exe"
$logDir = Join-Path $root "artifacts\phase1"

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

dotnet build $solution -c $Configuration -p:Platform=$Platform

& $exe --check-roms
if ($LASTEXITCODE -ne 0) {
    throw "ROM check failed."
}

& $exe --self-test-cpu (Join-Path $logDir "cpu_self_test.log")
if ($LASTEXITCODE -ne 0) {
    throw "CPU self-test failed."
}

& $exe --benchmark $BenchmarkCycles (Join-Path $logDir "benchmark.log")
if ($LASTEXITCODE -ne 0) {
    throw "Benchmark failed."
}

Write-Host "Phase 1 checks completed. Logs: $logDir"
