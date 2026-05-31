param(
    [string]$TestRoot = "vice_VICII_tests",

    [string]$OutputDirectory = "artifacts\vice-vicii-tests",

    [string[]]$IncludeDirectories = @(
        "banking",
        "border",
        "colorfetchbug",
        "colorsplit",
        "D011Test",
        "dentest",
        "dmadelay",
        "fldscroll",
        "flibug",
        "frodotests",
        "gfxfetch",
        "greydot",
        "rasterirq",
        "sb_sprite_fetch",
        "screenpos",
        "sequencer-bug",
        "spritebug",
        "spritecrunch",
        "spritedma",
        "spriteenable",
        "spritefetchbug",
        "spritepriorities",
        "spritesplit",
        "vicii_timing",
        "videomode"
    ),

    [long]$MaxCycles = 5000000,

    [long]$WarmupCycles = 2500000,

    [int]$MaxTests = 0,

    [ValidateSet("Exact", "BackgroundClass")]
    [string]$CompareMode = "Exact",

    [double]$WarnMismatchRate = 0.001,

    [switch]$UseBasicRun,

    [switch]$Build,

    [switch]$NoDiff,

    [switch]$StopOnD7FF,

    [switch]$NoStopOnD7FF,

    [long]$StopAfterWriteCycles = 0
)

$ErrorActionPreference = "Stop"

function Get-SafeTestId {
    param([string]$RelativePath)

    $id = $RelativePath -replace '(?i)\.prg$', ''
    $id = $id -replace '[\\/]+', '-'
    $id = $id -replace '[^A-Za-z0-9_.-]', '-'
    return $id.Trim('-')
}

function Get-BasicSysAddress {
    param([string]$ProgramPath)

    $bytes = [System.IO.File]::ReadAllBytes($ProgramPath)
    if ($bytes.Length -lt 8) {
        return $null
    }

    $loadAddress = [int]$bytes[0] -bor ([int]$bytes[1] -shl 8)
    if ($loadAddress -ne 0x0801) {
        return $null
    }

    $index = 2
    while ($index -lt $bytes.Length - 5) {
        $nextLine = [int]$bytes[$index] -bor ([int]$bytes[$index + 1] -shl 8)
        if ($nextLine -eq 0) {
            return $null
        }

        $lineEnd = $index + 4
        while ($lineEnd -lt $bytes.Length -and $bytes[$lineEnd] -ne 0) {
            $lineEnd++
        }

        for ($tokenIndex = $index + 4; $tokenIndex -lt $lineEnd; $tokenIndex++) {
            if ($bytes[$tokenIndex] -ne 0x9E) {
                continue
            }

            $digits = New-Object System.Text.StringBuilder
            for ($digitIndex = $tokenIndex + 1; $digitIndex -lt $lineEnd; $digitIndex++) {
                $value = $bytes[$digitIndex]
                if ($value -ge 0x30 -and $value -le 0x39) {
                    [void]$digits.Append([char]$value)
                }
                elseif ($digits.Length -gt 0) {
                    break
                }
            }

            if ($digits.Length -gt 0) {
                return [int]::Parse($digits.ToString(), [System.Globalization.CultureInfo]::InvariantCulture)
            }
        }

        $index = $lineEnd + 1
    }

    return $null
}

function Find-TestCandidates {
    param(
        [string]$RootPath,
        [string[]]$Directories,
        [int]$Limit
    )

    $rootFullPath = (Resolve-Path -LiteralPath $RootPath).Path
    $candidates = New-Object System.Collections.Generic.List[object]

    foreach ($directoryName in $Directories) {
        $directoryPath = Join-Path $rootFullPath $directoryName
        if (-not (Test-Path -LiteralPath $directoryPath)) {
            continue
        }

        Get-ChildItem -LiteralPath $directoryPath -Recurse -File -Filter *.prg |
            Where-Object {
                $_.FullName -notmatch '\\references\\' -and
                $_.Name -notmatch '(?i)ntsc|8562|8564|8565|6572'
            } |
            Sort-Object FullName |
            ForEach-Object {
                $referencePath = Join-Path (Join-Path $_.DirectoryName "references") ($_.Name + ".png")
                if (-not (Test-Path -LiteralPath $referencePath)) {
                    return
                }

                $relativePath = $_.FullName.Substring($rootFullPath.Length).TrimStart('\', '/')
                $sysAddress = Get-BasicSysAddress $_.FullName
                $candidates.Add([pscustomobject]@{
                    Id = Get-SafeTestId $relativePath
                    ProgramPath = $_.FullName
                    ReferencePath = (Resolve-Path -LiteralPath $referencePath).Path
                    RelativePath = $relativePath
                    Directory = $directoryName
                    SysAddress = $sysAddress
                }) | Out-Null

                if ($Limit -gt 0 -and $candidates.Count -ge $Limit) {
                    throw [System.OperationCanceledException]::new("candidate-limit")
                }
            }
    }

    return $candidates
}

function Invoke-GoldenTest {
    param(
        [string]$ExePath,
        [object]$Candidate,
        [string]$TestOutputDirectory,
        [long]$Cycles,
        [long]$Warmup,
        [bool]$StopOnD7FF,
        [long]$StopAfterWriteCycles
    )

    New-Item -ItemType Directory -Force -Path $TestOutputDirectory | Out-Null
    $manifestPath = Join-Path $TestOutputDirectory "manifest.json"
    $manifest = [ordered]@{
        schemaVersion = 1
        name = "VICE VIC-II single test"
        description = "Generated local manifest for imported VICE VIC-II test program."
        tests = @(
            [ordered]@{
                id = $Candidate.Id
                name = $Candidate.RelativePath
                category = "vicii"
                model = "PAL"
                programPath = $Candidate.ProgramPath
                maxCycles = $Cycles
                arguments = [ordered]@{
                    profile = "accuracy"
                    warmupCycles = $Warmup.ToString([System.Globalization.CultureInfo]::InvariantCulture)
                    mountAfterWarmup = "true"
                    writeFrame = "true"
                }
                expectations = [ordered]@{
                    hashes = [ordered]@{}
                    properties = [ordered]@{}
                }
            }
        )
    }

    if ($StopOnD7FF) {
        $manifest.tests[0].arguments["stopOnWriteAddress"] = '$D7FF'
        $manifest.tests[0].arguments["stopOnWriteValue"] = "0"
        $manifest.tests[0].arguments["stopAfterWriteCycles"] = $StopAfterWriteCycles.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    }

    if (!$UseBasicRun -and $null -ne $Candidate.SysAddress) {
        $manifest.tests[0].arguments["startAddress"] = $Candidate.SysAddress.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    }
    else {
        $manifest.tests[0].arguments["command"] = "RUN\r"
    }

    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    & $ExePath --golden-run $manifestPath $TestOutputDirectory | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Golden run failed with exit code $LASTEXITCODE."
    }

    $framePath = Join-Path $TestOutputDirectory ($Candidate.Id + ".ppm")
    if (-not (Test-Path -LiteralPath $framePath)) {
        throw "Golden run did not write expected frame: $framePath"
    }

    return (Resolve-Path -LiteralPath $framePath).Path
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$resolvedRoot = Join-Path $repoRoot $TestRoot
if (-not (Test-Path -LiteralPath $resolvedRoot)) {
    throw "VICE VIC-II test root not found: $resolvedRoot"
}

if ($IncludeDirectories.Count -eq 1 -and $IncludeDirectories[0].Contains(",")) {
    $IncludeDirectories = @($IncludeDirectories[0].Split(",") | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 })
}

$exePath = Join-Path $repoRoot "C64Emulator\bin\x64\Release\C64Emulator.exe"
if ($Build -or -not (Test-Path -LiteralPath $exePath)) {
    dotnet build (Join-Path $repoRoot "C64Emulator.sln") -c Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE."
    }
}

$exePath = (Resolve-Path -LiteralPath $exePath).Path
$outputPath = Join-Path $repoRoot $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$useD7ffStop = [bool]$StopOnD7FF -or -not [bool]$NoStopOnD7FF

try {
    $candidates = @(Find-TestCandidates -RootPath $resolvedRoot -Directories $IncludeDirectories -Limit $MaxTests)
}
catch [System.OperationCanceledException] {
    if ($_.Exception.Message -ne "candidate-limit") {
        throw
    }

    $candidates = @(Find-TestCandidates -RootPath $resolvedRoot -Directories $IncludeDirectories -Limit 0 | Select-Object -First $MaxTests)
}

$summary = New-Object System.Collections.Generic.List[object]
$total = $candidates.Count
Write-Host "Selected $total VICE VIC-II tests from $resolvedRoot"

for ($index = 0; $index -lt $total; $index++) {
    $candidate = $candidates[$index]
    $ordinal = $index + 1
    $testOutputPath = Join-Path $outputPath $candidate.Id
    Write-Host "[$ordinal/$total] $($candidate.RelativePath)"

    $status = "error"
    $message = ""
    $mismatches = -1
    $rate = 1.0
    $framePath = ""
    $reportPath = ""

    try {
        $framePath = Invoke-GoldenTest -ExePath $exePath -Candidate $candidate -TestOutputDirectory $testOutputPath -Cycles $MaxCycles -Warmup $WarmupCycles -StopOnD7FF $useD7ffStop -StopAfterWriteCycles $StopAfterWriteCycles

        $compareArguments = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", (Join-Path $PSScriptRoot "compare-vicii-reference.ps1"),
            "-ReferenceFrame", $candidate.ReferencePath,
            "-EmulatorFrame", $framePath,
            "-OutputDirectory", $testOutputPath,
            "-Name", ($candidate.Id + "-compare"),
            "-AutoAlign",
            "-Mode", $CompareMode
        )
        if ($NoDiff) {
            $compareArguments += "-NoDiff"
        }

        powershell @compareArguments | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Frame comparison failed with exit code $LASTEXITCODE."
        }

        $reportPath = Join-Path $testOutputPath ($candidate.Id + "-compare.json")
        $comparison = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $mismatches = [int]$comparison.NormalizedMismatches
        $rate = [double]$comparison.MismatchRate
        $status = if ($rate -le $WarnMismatchRate) { "ok" } else { "mismatch" }
        $message = "mismatchRate=$('{0:P4}' -f $rate)"
    }
    catch {
        $message = $_.Exception.Message
    }

    $summary.Add([pscustomobject]@{
        id = $candidate.Id
        directory = $candidate.Directory
        relativePath = $candidate.RelativePath
        status = $status
        mismatches = $mismatches
        mismatchRate = $rate
        message = $message
        programPath = $candidate.ProgramPath
        referencePath = $candidate.ReferencePath
        framePath = $framePath
        reportPath = $reportPath
        startMode = if (!$UseBasicRun -and $null -ne $candidate.SysAddress) { "sys" } else { "run" }
    }) | Out-Null
}

$summaryJsonPath = Join-Path $outputPath "summary.json"
$summaryCsvPath = Join-Path $outputPath "summary.csv"
$summaryRows = $summary.ToArray()
$summaryRows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryJsonPath -Encoding UTF8
$summaryRows | Export-Csv -LiteralPath $summaryCsvPath -NoTypeInformation -Encoding UTF8

$okCount = @($summaryRows | Where-Object status -eq "ok").Count
$mismatchCount = @($summaryRows | Where-Object status -eq "mismatch").Count
$errorCount = @($summaryRows | Where-Object status -eq "error").Count

Write-Host "Done. ok=$okCount mismatch=$mismatchCount error=$errorCount"
Write-Host "Summary: $((Resolve-Path -LiteralPath $summaryJsonPath).Path)"
