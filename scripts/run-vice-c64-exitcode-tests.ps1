param(
    [string]$TestRoot = "vice_testprogs",

    [string]$TestList = "testbench\x64sc-testlist.txt",

    [string]$OutputDirectory = "artifacts\vice-c64-exitcode-tests",

    [string[]]$IncludeGroups = @(
        "CPU",
        "CIA",
        "C64",
        "general",
        "interrupts"
    ),

    [int]$MaxTests = 0,

    [long]$WarmupCycles = 2500000,

    [switch]$AllGroups,

    [switch]$IncludeExpectedErrors,

    [switch]$IncludeMedia,

    [switch]$UseBasicRun,

    [string]$EmulatorPath = "",

    [switch]$Build
)

$ErrorActionPreference = "Stop"

function ConvertTo-NormalizedList {
    param([string[]]$Values)

    $items = New-Object System.Collections.Generic.List[string]
    foreach ($value in $Values) {
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }

        foreach ($part in $value.Split(",")) {
            $trimmed = $part.Trim()
            if ($trimmed.Length -gt 0) {
                $items.Add($trimmed) | Out-Null
            }
        }
    }

    return $items.ToArray()
}

function Get-SafeTestId {
    param(
        [int]$LineNumber,
        [string]$RelativePath,
        [string]$ProgramName
    )

    $id = ("{0:D4}-{1}-{2}" -f $LineNumber, $RelativePath, $ProgramName)
    $id = $id -replace '[\\/]+', '-'
    $id = $id -replace '[^A-Za-z0-9_.-]', '-'
    return $id.Trim('-')
}

function Get-GroupName {
    param([string]$TestDirectory)

    $normalized = ($TestDirectory -replace '\\', '/').Trim()
    while ($normalized.StartsWith("../", [System.StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring(3)
    }

    while ($normalized.StartsWith("./", [System.StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring(2)
    }

    $parts = $normalized.Split("/") | Where-Object { $_.Length -gt 0 }
    if ($parts.Count -eq 0) {
        return "unknown"
    }

    return $parts[0]
}

function Get-PrgLaunchInfo {
    param([string]$ProgramPath)

    $bytes = [System.IO.File]::ReadAllBytes($ProgramPath)
    if ($bytes.Length -lt 2) {
        return [pscustomobject]@{
            LoadAddress = $null
            BasicSysAddress = $null
        }
    }

    $loadAddress = [int]$bytes[0] -bor ([int]$bytes[1] -shl 8)
    $sysAddress = $null
    if ($loadAddress -eq 0x0801 -and $bytes.Length -ge 8) {
        $index = 2
        while ($index -lt $bytes.Length - 5) {
            $nextLine = [int]$bytes[$index] -bor ([int]$bytes[$index + 1] -shl 8)
            if ($nextLine -eq 0) {
                break
            }

            $lineEnd = $index + 4
            while ($lineEnd -lt $bytes.Length -and $bytes[$lineEnd] -ne 0) {
                $lineEnd++
            }

            for ($tokenIndex = $index + 4; $tokenIndex -lt $lineEnd; $tokenIndex++) {
                if ($bytes[$tokenIndex] -ne 0x9E) {
                    continue
                }

                $digitIndex = $tokenIndex + 1
                while ($digitIndex -lt $lineEnd -and $bytes[$digitIndex] -eq 0x20) {
                    $digitIndex++
                }

                if ($digitIndex -ge $lineEnd -or $bytes[$digitIndex] -lt 0x30 -or $bytes[$digitIndex] -gt 0x39) {
                    continue
                }

                $digits = New-Object System.Text.StringBuilder
                for (; $digitIndex -lt $lineEnd; $digitIndex++) {
                    $value = $bytes[$digitIndex]
                    if ($value -ge 0x30 -and $value -le 0x39) {
                        [void]$digits.Append([char]$value)
                    }
                    elseif ($digits.Length -gt 0) {
                        break
                    }
                }

                if ($digits.Length -gt 0) {
                    $sysAddress = [int]::Parse($digits.ToString(), [System.Globalization.CultureInfo]::InvariantCulture)
                    break
                }
            }

            if ($null -ne $sysAddress) {
                break
            }

            $index = $lineEnd + 1
        }
    }

    return [pscustomobject]@{
        LoadAddress = $loadAddress
        BasicSysAddress = $sysAddress
    }
}

function Resolve-TestPath {
    param(
        [string]$BaseDirectory,
        [string]$RelativePath
    )

    $platformPath = $RelativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar
    return [System.IO.Path]::GetFullPath((Join-Path $BaseDirectory $platformPath))
}

function Get-MediaOptionPath {
    param(
        [string[]]$Options,
        [string]$TestDirectory
    )

    foreach ($option in $Options) {
        if ($option -match '^mount(?:d64|g64|crt):(.+)$') {
            return Resolve-TestPath -BaseDirectory $TestDirectory -RelativePath $Matches[1]
        }
    }

    return $null
}

function Read-TestList {
    param(
        [string]$TestListPath,
        [string[]]$Groups,
        [bool]$UseAllGroups,
        [bool]$AllowExpectedErrors,
        [bool]$AllowMedia,
        [int]$Limit
    )

    $testListFullPath = (Resolve-Path -LiteralPath $TestListPath).Path
    $testListDirectory = Split-Path -Parent $testListFullPath
    $selected = New-Object System.Collections.Generic.List[object]
    $skipped = New-Object System.Collections.Generic.List[object]
    $groupLookup = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($group in $Groups) {
        [void]$groupLookup.Add($group)
    }

    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $testListFullPath) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith("#", [System.StringComparison]::Ordinal)) {
            continue
        }

        $columns = $line.Split(",")
        if ($columns.Count -lt 4) {
            continue
        }

        $testDirectoryText = $columns[0].Trim()
        $programName = $columns[1].Trim()
        $testType = $columns[2].Trim()
        $timeoutText = $columns[3].Trim()
        $options = @()
        if ($columns.Count -gt 4) {
            $options = @($columns[4..($columns.Count - 1)] | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 })
        }

        $group = Get-GroupName $testDirectoryText
        $skipReason = $null
        if ($testType -ne "exitcode") {
            $skipReason = "not-exitcode"
        }
        elseif (-not $UseAllGroups -and -not $groupLookup.Contains($group)) {
            $skipReason = "group-filter"
        }
        elseif (($options | Where-Object { $_ -eq "expect:error" }).Count -gt 0 -and -not $AllowExpectedErrors) {
            $skipReason = "expected-error"
        }
        elseif (($options | Where-Object { $_ -match '^vicii-(?:ntsc|ntscold|drean)$' }).Count -gt 0) {
            $skipReason = "non-pal-vicii"
        }
        elseif (($options | Where-Object { $_ -match '^mount(?:d64|g64|crt):' }).Count -gt 0 -and -not $AllowMedia) {
            $skipReason = "media-option"
        }

        $testDirectory = Resolve-TestPath -BaseDirectory $testListDirectory -RelativePath $testDirectoryText
        $programPath = $null
        if (-not [string]::IsNullOrWhiteSpace($programName)) {
            $programPath = Join-Path $testDirectory $programName
            if ($null -eq $skipReason -and -not (Test-Path -LiteralPath $programPath)) {
                $skipReason = "missing-program"
            }
        }

        $mediaPath = Get-MediaOptionPath -Options $options -TestDirectory $testDirectory
        if ($null -ne $mediaPath -and $null -eq $skipReason -and -not (Test-Path -LiteralPath $mediaPath)) {
            $skipReason = "missing-media"
        }

        if ($null -ne $skipReason) {
            $skipped.Add([pscustomobject]@{
                lineNumber = $lineNumber
                group = $group
                type = $testType
                testDirectory = $testDirectoryText
                programName = $programName
                options = ($options -join ",")
                reason = $skipReason
            }) | Out-Null
            continue
        }

        $maxCycles = [long]::Parse($timeoutText, [System.Globalization.CultureInfo]::InvariantCulture)
        $relativeName = (($testDirectoryText.TrimEnd("/", "\") + "/" + $programName) -replace '^[.][/]', '')
        $expectsError = ($options | Where-Object { $_ -eq "expect:error" }).Count -gt 0
        $expectsTimeout = ($options | Where-Object { $_ -eq "expect:timeout" }).Count -gt 0
        $selected.Add([pscustomobject]@{
            id = Get-SafeTestId -LineNumber $lineNumber -RelativePath $relativeName -ProgramName ($options -join "-")
            lineNumber = $lineNumber
            group = $group
            testDirectory = $testDirectoryText
            programName = $programName
            programPath = if ($null -eq $programPath) { "" } else { (Resolve-Path -LiteralPath $programPath).Path }
            mediaPath = if ($null -eq $mediaPath) { "" } else { (Resolve-Path -LiteralPath $mediaPath).Path }
            maxCycles = $maxCycles
            options = $options
            expectsTimeout = $expectsTimeout
            expectedValue = if ($expectsError) { "FF" } elseif ($expectsTimeout) { "" } else { "00" }
        }) | Out-Null

        if ($Limit -gt 0 -and $selected.Count -ge $Limit) {
            break
        }
    }

    return [pscustomobject]@{
        Selected = $selected.ToArray()
        Skipped = $skipped.ToArray()
    }
}

function New-GoldenManifest {
    param(
        [object[]]$Tests,
        [long]$Warmup,
        [bool]$PreferBasicRun
    )

    $manifestTests = New-Object System.Collections.Generic.List[object]
    foreach ($test in $Tests) {
        $arguments = [ordered]@{
            profile = "accuracy"
            warmupCycles = $Warmup.ToString([System.Globalization.CultureInfo]::InvariantCulture)
            mountAfterWarmup = "true"
            stopOnWriteAddress = '$D7FF'
            stopAfterWriteCycles = "0"
        }

        if (-not [string]::IsNullOrWhiteSpace($test.programPath)) {
            $launch = Get-PrgLaunchInfo $test.programPath
            if (-not $PreferBasicRun -and $null -ne $launch.BasicSysAddress) {
                $arguments["startAddress"] = $launch.BasicSysAddress.ToString([System.Globalization.CultureInfo]::InvariantCulture)
            }
            elseif ($null -ne $launch.LoadAddress -and $launch.LoadAddress -ne 0x0801) {
                $arguments["startAddress"] = $launch.LoadAddress.ToString([System.Globalization.CultureInfo]::InvariantCulture)
            }
            else {
                $arguments["command"] = "RUN\r"
            }
        }

        $expectedExitReason = if ($test.expectsTimeout) { "cycles" } else { "stopOnWrite" }
        $expectedProperties = [ordered]@{
            stopOnWriteMatched = if ($test.expectsTimeout) { "false" } else { "true" }
        }

        if (-not $test.expectsTimeout) {
            $expectedProperties["stopOnWriteValue"] = $test.expectedValue
        }

        $manifestTests.Add([ordered]@{
            id = $test.id
            name = (($test.testDirectory.TrimEnd("/", "\") + "/" + $test.programName).TrimEnd("/"))
            category = $test.group
            model = "PAL"
            programPath = $test.programPath
            mediaPath = $test.mediaPath
            maxCycles = $test.maxCycles
            tags = @($test.group, "vice", "exitcode")
            arguments = $arguments
            metadata = [ordered]@{
                sourceLine = $test.lineNumber.ToString([System.Globalization.CultureInfo]::InvariantCulture)
                options = ($test.options -join ",")
            }
            expectations = [ordered]@{
                exitReason = $expectedExitReason
                hashes = [ordered]@{}
                properties = $expectedProperties
            }
        }) | Out-Null
    }

    return [ordered]@{
        schemaVersion = 1
        name = "VICE C64 exitcode tests"
        description = "Generated from VICE x64sc testbench exitcode entries."
        tests = $manifestTests.ToArray()
    }
}

function Convert-GoldenResultsToSummary {
    param(
        [object]$RunResult,
        [hashtable]$SelectedById
    )

    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($result in $RunResult.results) {
        $selected = $SelectedById[$result.id]
        $properties = $result.actualProperties
        $stopValue = ""
        $stopMatched = ""
        $stopCycle = ""
        $stopPc = ""
        if ($null -ne $properties) {
            $stopValue = $properties.stopOnWriteValue
            $stopMatched = $properties.stopOnWriteMatched
            $stopCycle = $properties.stopOnWriteCycle
            $stopPc = $properties.stopOnWritePc
        }

        $rows.Add([pscustomobject]@{
            id = $result.id
            group = $result.category
            relativePath = (($selected.testDirectory.TrimEnd("/", "\") + "/" + $selected.programName).TrimEnd("/"))
            outcome = $result.outcome
            expectedValue = $selected.expectedValue
            d7ffValue = $stopValue
            d7ffMatched = $stopMatched
            exitReason = $result.exitReason
            stopPc = $stopPc
            stopCycle = $stopCycle
            durationMs = $result.durationMilliseconds
            maxCycles = $selected.maxCycles
            message = $result.message
            programPath = $selected.programPath
            mediaPath = $selected.mediaPath
            options = ($selected.options -join ",")
        }) | Out-Null
    }

    return $rows.ToArray()
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$resolvedTestRoot = Join-Path $repoRoot $TestRoot
if (-not (Test-Path -LiteralPath $resolvedTestRoot)) {
    throw "VICE test root not found: $resolvedTestRoot"
}

$resolvedTestList = if ([System.IO.Path]::IsPathRooted($TestList)) {
    $TestList
}
else {
    Join-Path $resolvedTestRoot $TestList
}

if (-not (Test-Path -LiteralPath $resolvedTestList)) {
    throw "VICE test list not found: $resolvedTestList"
}

$IncludeGroups = ConvertTo-NormalizedList $IncludeGroups
$selection = Read-TestList `
    -TestListPath $resolvedTestList `
    -Groups $IncludeGroups `
    -UseAllGroups ([bool]$AllGroups) `
    -AllowExpectedErrors ([bool]$IncludeExpectedErrors) `
    -AllowMedia ([bool]$IncludeMedia) `
    -Limit $MaxTests

$selectedTests = @($selection.Selected)
$skippedTests = @($selection.Skipped)
if ($selectedTests.Count -eq 0) {
    throw "No VICE exitcode tests selected."
}

$exePath = if ([string]::IsNullOrWhiteSpace($EmulatorPath)) {
    Join-Path $repoRoot "C64Emulator\bin\x64\Release\C64Emulator.exe"
}
else {
    $EmulatorPath
}

if ([System.IO.Path]::IsPathRooted($exePath)) {
    $exePath = $exePath
}
else {
    $exePath = Join-Path $repoRoot $exePath
}

if ([string]::IsNullOrWhiteSpace($EmulatorPath)) {
    if ($Build -or -not (Test-Path -LiteralPath $exePath)) {
        dotnet build (Join-Path $repoRoot "C64Emulator.sln") -c Release -p:Platform=x64
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE."
        }
    }
}

$exePath = (Resolve-Path -LiteralPath $exePath).Path
$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
}
else {
    Join-Path $repoRoot $OutputDirectory
}

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$manifestPath = Join-Path $outputPath "manifest.json"
$resultPath = Join-Path $outputPath "golden-results.json"
$summaryJsonPath = Join-Path $outputPath "summary.json"
$summaryCsvPath = Join-Path $outputPath "summary.csv"
$skippedJsonPath = Join-Path $outputPath "skipped.json"
$skippedCsvPath = Join-Path $outputPath "skipped.csv"

$manifest = New-GoldenManifest -Tests $selectedTests -Warmup $WarmupCycles -PreferBasicRun ([bool]$UseBasicRun)
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
$skippedTests | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $skippedJsonPath -Encoding UTF8
$skippedTests | Export-Csv -LiteralPath $skippedCsvPath -NoTypeInformation -Encoding UTF8

Write-Host "Selected $($selectedTests.Count) VICE C64 exitcode tests from $resolvedTestList"
Write-Host "Skipped before run: $($skippedTests.Count)"
Write-Host "Manifest: $((Resolve-Path -LiteralPath $manifestPath).Path)"

& $exePath --golden-run $manifestPath $outputPath | Out-Host
$emulatorExitCode = $LASTEXITCODE
if (-not (Test-Path -LiteralPath $resultPath)) {
    throw "Golden result file was not written: $resultPath"
}

$runResult = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
$selectedById = @{}
foreach ($test in $selectedTests) {
    $selectedById[$test.id] = $test
}

$summary = @(Convert-GoldenResultsToSummary -RunResult $runResult -SelectedById $selectedById)
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryJsonPath -Encoding UTF8
$summary | Export-Csv -LiteralPath $summaryCsvPath -NoTypeInformation -Encoding UTF8

$passed = @($summary | Where-Object { $_.outcome -eq "Passed" }).Count
$failed = @($summary | Where-Object { $_.outcome -eq "Failed" }).Count
$errors = @($summary | Where-Object { $_.outcome -eq "Error" }).Count
$missingWrite = @($summary | Where-Object { $_.d7ffMatched -ne "true" }).Count

Write-Host "Done. passed=$passed failed=$failed error=$errors missingD7FF=$missingWrite emulatorExitCode=$emulatorExitCode"
Write-Host "Summary JSON: $((Resolve-Path -LiteralPath $summaryJsonPath).Path)"
Write-Host "Summary CSV:  $((Resolve-Path -LiteralPath $summaryCsvPath).Path)"

if ($failed -gt 0 -or $errors -gt 0) {
    $summary |
        Where-Object { $_.outcome -ne "Passed" } |
        Select-Object -First 20 group, relativePath, outcome, expectedValue, d7ffValue, exitReason, message |
        Format-Table -AutoSize
}

exit $emulatorExitCode
