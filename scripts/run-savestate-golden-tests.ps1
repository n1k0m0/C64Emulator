param(
    [string]$SaveRoot = "",

    [string]$BaselinePath = "artifacts\savestate-golden-baseline.json",

    [string]$OutputDirectory = "artifacts\savestate-golden-tests",

    [string]$EmulatorPath = "",

    [int]$Frames = 1,

    [int]$MaxSaves = 0,

    [switch]$Accept,

    [switch]$Build
)

$ErrorActionPreference = "Stop"

function Resolve-RepoPath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repoRoot $Path
}

function Get-SafeId {
    param(
        [int]$Index,
        [string]$RelativePath
    )

    $id = ("{0:D3}-{1}" -f $Index, $RelativePath)
    $id = $id -replace '[\\/]+', '-'
    $id = $id -replace '[^A-Za-z0-9_.-]', '-'
    return $id.Trim('-')
}

function Get-RelativeSavePath {
    param(
        [string]$RootPath,
        [string]$SavePath
    )

    return $SavePath.Substring($RootPath.Length).TrimStart('\', '/')
}

function ConvertTo-QuotedArgument {
    param([string]$Value)

    return '"' + ($Value -replace '"', '\"') + '"'
}

function Invoke-SavestateRender {
    param(
        [string]$ExePath,
        [string]$SavePath,
        [int]$FrameCount,
        [string]$FramePath,
        [string]$LogPath
    )

    $arguments = @(
        "--render-savestate",
        (ConvertTo-QuotedArgument $SavePath),
        $FrameCount.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        (ConvertTo-QuotedArgument $FramePath),
        (ConvertTo-QuotedArgument $LogPath)
    ) -join " "
    $process = Start-Process -FilePath $ExePath -ArgumentList $arguments -NoNewWindow -Wait -PassThru
    $outputText = ""
    if (Test-Path -LiteralPath $LogPath) {
        $outputText = Get-Content -LiteralPath $LogPath -Raw
    }

    $rendered = (Test-Path -LiteralPath $FramePath) -and $process.ExitCode -eq 0
    $hash = ""
    if ($rendered) {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $FramePath).Hash
    }

    return [pscustomobject]@{
        Rendered = $rendered
        Hash = $hash
        Output = $outputText
    }
}

function Read-Baseline {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Baseline not found: $Path. Run with -Accept to create it."
    }

    $baseline = Get-Content -LiteralPath $Path | ConvertFrom-Json
    $lookup = New-Object 'System.Collections.Generic.Dictionary[string, object]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $baseline.entries) {
        $lookup[$entry.relativePath] = $entry
    }

    return [pscustomobject]@{
        Data = $baseline
        Entries = $lookup
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SaveRoot)) {
    $SaveRoot = Join-Path $env:APPDATA "C64Emulator\saves"
}

$resolvedSaveRoot = Resolve-RepoPath $SaveRoot
$resolvedBaselinePath = Resolve-RepoPath $BaselinePath
$resolvedOutputDirectory = Resolve-RepoPath $OutputDirectory

if ([string]::IsNullOrWhiteSpace($EmulatorPath)) {
    $EmulatorPath = Join-Path $repoRoot "C64Emulator\bin\x64\Release\C64Emulator.exe"
}
else {
    $EmulatorPath = Resolve-RepoPath $EmulatorPath
}

if ($Build) {
    dotnet build (Join-Path $repoRoot "C64Emulator.sln") -c Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path -LiteralPath $resolvedSaveRoot)) {
    throw "Savestate root not found: $resolvedSaveRoot"
}

if (-not (Test-Path -LiteralPath $EmulatorPath)) {
    throw "Emulator executable not found: $EmulatorPath"
}

New-Item -ItemType Directory -Force -Path $resolvedOutputDirectory | Out-Null

$baselineInfo = $null
if (-not $Accept) {
    $baselineInfo = Read-Baseline $resolvedBaselinePath
}

$saves = @(Get-ChildItem -LiteralPath $resolvedSaveRoot -Recurse -File -Filter *.c64sav | Sort-Object FullName)
if ($MaxSaves -gt 0) {
    $saves = @($saves | Select-Object -First $MaxSaves)
}

$rows = New-Object System.Collections.Generic.List[object]
$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

Write-Host ("Selected {0} savestates from {1}" -f $saves.Count, $resolvedSaveRoot)
for ($index = 0; $index -lt $saves.Count; $index++) {
    $save = $saves[$index]
    $relativePath = Get-RelativeSavePath $resolvedSaveRoot $save.FullName
    [void]$seen.Add($relativePath)

    $id = Get-SafeId ($index + 1) $relativePath
    $testOutputDirectory = Join-Path $resolvedOutputDirectory $id
    New-Item -ItemType Directory -Force -Path $testOutputDirectory | Out-Null

    $framePath = Join-Path $testOutputDirectory "frame.ppm"
    $logPath = Join-Path $testOutputDirectory "render.log"
    $started = Get-Date
    $render = Invoke-SavestateRender $EmulatorPath $save.FullName $Frames $framePath $logPath
    $seconds = [Math]::Round(((Get-Date) - $started).TotalSeconds, 3)

    $expectedHash = ""
    $status = "ok"
    if (-not $render.Rendered) {
        $status = "render-error"
    }
    elseif (-not $Accept) {
        if (-not $baselineInfo.Entries.ContainsKey($relativePath)) {
            $status = "new"
        }
        else {
            $expectedHash = $baselineInfo.Entries[$relativePath].hash
            if (-not [string]::Equals($expectedHash, $render.Hash, [System.StringComparison]::OrdinalIgnoreCase)) {
                $status = "mismatch"
            }
        }
    }

    $rows.Add([pscustomobject]@{
        index = $index + 1
        relativePath = $relativePath
        status = $status
        hash = $render.Hash
        expectedHash = $expectedHash
        framePath = $framePath
        logPath = $logPath
        seconds = $seconds
        message = $render.Output
    }) | Out-Null

    Write-Host ("[{0}/{1}] {2} {3}" -f ($index + 1), $saves.Count, $status, $relativePath)
}

if (-not $Accept) {
    foreach ($entry in $baselineInfo.Data.entries) {
        if ($seen.Contains($entry.relativePath)) {
            continue
        }

        $rows.Add([pscustomobject]@{
            index = 0
            relativePath = $entry.relativePath
            status = "missing"
            hash = ""
            expectedHash = $entry.hash
            framePath = ""
            logPath = ""
            seconds = 0
            message = "Baseline entry has no matching local savestate."
        }) | Out-Null
    }
}

$summaryJson = Join-Path $resolvedOutputDirectory "summary.json"
$summaryCsv = Join-Path $resolvedOutputDirectory "summary.csv"
$rows | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryJson -Encoding UTF8
$rows | Export-Csv -LiteralPath $summaryCsv -NoTypeInformation -Encoding UTF8

if ($Accept) {
    $baselineDirectory = Split-Path -Parent $resolvedBaselinePath
    if (-not [string]::IsNullOrWhiteSpace($baselineDirectory)) {
        New-Item -ItemType Directory -Force -Path $baselineDirectory | Out-Null
    }

    $entries = @(
        $rows |
            Where-Object { $_.status -eq "ok" } |
            Sort-Object relativePath |
            ForEach-Object {
                [ordered]@{
                    relativePath = $_.relativePath
                    hash = $_.hash
                }
            }
    )

    $accepted = [ordered]@{
        schemaVersion = 1
        name = "C64Emulator local savestate golden baseline"
        generatedAt = (Get-Date).ToString("O")
        saveRoot = $resolvedSaveRoot
        frames = $Frames
        entries = $entries
    }

    $accepted | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resolvedBaselinePath -Encoding UTF8
    Write-Host "Accepted baseline: $resolvedBaselinePath"
}

$groups = $rows | Group-Object status | Sort-Object Name
$groups | Select-Object Name, Count | Format-Table -AutoSize
Write-Host "Summary JSON: $summaryJson"
Write-Host "Summary CSV:  $summaryCsv"

$failed = @($rows | Where-Object { $_.status -ne "ok" })
if ($failed.Count -gt 0) {
    exit 1
}
