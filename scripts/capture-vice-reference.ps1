param(
    [string]$VicePath = "",
    [string]$MediaPath = "",
    [int]$Cycles = 10000000,
    [string]$OutputDirectory = "artifacts\vice-reference",
    [string]$Name = "vice-reference",
    [switch]$AllowBlankScreenshot
)

$ErrorActionPreference = "Stop"

function Resolve-X64Sc {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    if (-not [string]::IsNullOrWhiteSpace($env:VICE_X64SC)) {
        return (Resolve-Path -LiteralPath $env:VICE_X64SC).Path
    }

    $command = Get-Command "x64sc.exe" -ErrorAction SilentlyContinue
    if ($command -ne $null) {
        return $command.Source
    }

    $command = Get-Command "x64sc" -ErrorAction SilentlyContinue
    if ($command -ne $null) {
        return $command.Source
    }

    $commonRoots = @(
        "$env:ProgramFiles\GTK3VICE",
        "$env:ProgramFiles\WinVICE",
        "${env:ProgramFiles(x86)}\GTK3VICE",
        "${env:ProgramFiles(x86)}\WinVICE"
    )

    foreach ($root in $commonRoots) {
        if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path -LiteralPath $root)) {
            continue
        }

        $candidate = Get-ChildItem -Path $root -Recurse -Filter "x64sc.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($candidate -ne $null) {
            return $candidate.FullName
        }
    }

    throw "Could not find x64sc. Pass -VicePath or set VICE_X64SC."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$x64sc = Resolve-X64Sc $VicePath
$screenshotPath = Join-Path $outputPath ($Name + ".png")
$logPath = Join-Path $outputPath ($Name + ".log")
$metadataPath = Join-Path $outputPath ($Name + ".json")

$arguments = @(
    "-default",
    "-silent",
    "-windowxpos", "100",
    "-windowypos", "100",
    "-windowwidth", "720",
    "-windowheight", "643",
    "-limitcycles", $Cycles.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    "-exitscreenshot", $screenshotPath,
    "-logtofile",
    "-logfile", $logPath
)

if (-not [string]::IsNullOrWhiteSpace($MediaPath)) {
    $resolvedMedia = if ([System.IO.Path]::IsPathRooted($MediaPath)) { $MediaPath } else { Join-Path $repoRoot $MediaPath }
    $arguments += @("-autostart", $resolvedMedia)
}

$process = Start-Process -FilePath $x64sc -ArgumentList $arguments -NoNewWindow -Wait -PassThru
if ($process.ExitCode -ne 0) {
    $logText = if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Raw } else { "" }
    $hasExpectedCycleLimitExit = $process.ExitCode -eq 1 -and
        $logText -match "cycle limit reached" -and
        (Test-Path -LiteralPath $screenshotPath)

    if (-not $hasExpectedCycleLimitExit) {
        exit $process.ExitCode
    }
}

$hash = if (Test-Path -LiteralPath $screenshotPath) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $screenshotPath).Hash
} else {
    ""
}

$screenshotStats = [ordered]@{
    width = 0
    height = 0
    uniqueSampleColors = 0
    nonBlackSamplePixels = 0
}

if (Test-Path -LiteralPath $screenshotPath) {
    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::new((Resolve-Path -LiteralPath $screenshotPath).Path)
    try {
        $uniqueColors = New-Object 'System.Collections.Generic.HashSet[int]'
        $stepX = [Math]::Max(1, [int]($bitmap.Width / 160))
        $stepY = [Math]::Max(1, [int]($bitmap.Height / 120))
        $nonBlack = 0
        for ($y = 0; $y -lt $bitmap.Height; $y += $stepY) {
            for ($x = 0; $x -lt $bitmap.Width; $x += $stepX) {
                $argb = $bitmap.GetPixel($x, $y).ToArgb()
                [void]$uniqueColors.Add($argb)
                if (($argb -band 0x00FFFFFF) -ne 0) {
                    $nonBlack++
                }
            }
        }

        $screenshotStats.width = $bitmap.Width
        $screenshotStats.height = $bitmap.Height
        $screenshotStats.uniqueSampleColors = $uniqueColors.Count
        $screenshotStats.nonBlackSamplePixels = $nonBlack
    }
    finally {
        $bitmap.Dispose()
    }
}

if (-not $AllowBlankScreenshot -and $screenshotStats.nonBlackSamplePixels -eq 0) {
    Write-Error "VICE screenshot was blank/black. Increase -Cycles or pass -AllowBlankScreenshot for diagnostics."
    exit 1
}

$metadata = [ordered]@{
    emulator = "VICE x64sc"
    executable = $x64sc
    cycles = $Cycles
    media = $MediaPath
    screenshot = $screenshotPath
    screenshotSha256 = $hash
    screenshotStats = $screenshotStats
    log = $logPath
    capturedUtc = [DateTime]::UtcNow.ToString("O")
}

$metadata | ConvertTo-Json -Depth 4 | Set-Content -Path $metadataPath -Encoding UTF8
Write-Host "VICE reference written:"
Write-Host "  Screenshot: $screenshotPath"
Write-Host "  SHA256:     $hash"
Write-Host "  Metadata:   $metadataPath"
