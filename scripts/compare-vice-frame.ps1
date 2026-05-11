param(
    [Parameter(Mandatory = $true)]
    [string]$ViceFrame,

    [Parameter(Mandatory = $true)]
    [string]$EmulatorFrame,

    [string]$OutputDirectory = "artifacts\vice-compare",

    [string]$Name = "vice-frame-compare"
)

$ErrorActionPreference = "Stop"

function Read-Token {
    param(
        [byte[]]$Bytes,
        [ref]$Index
    )

    while ($Index.Value -lt $Bytes.Length) {
        $b = $Bytes[$Index.Value]
        if ($b -eq 35) {
            while ($Index.Value -lt $Bytes.Length -and $Bytes[$Index.Value] -ne 10) {
                $Index.Value++
            }
            continue
        }

        if ($b -gt 32) {
            break
        }

        $Index.Value++
    }

    $start = $Index.Value
    while ($Index.Value -lt $Bytes.Length -and $Bytes[$Index.Value] -gt 32) {
        $Index.Value++
    }

    return [System.Text.Encoding]::ASCII.GetString($Bytes, $start, $Index.Value - $start)
}

function Skip-Ppm-Whitespace {
    param(
        [byte[]]$Bytes,
        [ref]$Index
    )

    while ($Index.Value -lt $Bytes.Length) {
        $b = $Bytes[$Index.Value]
        if ($b -eq 35) {
            while ($Index.Value -lt $Bytes.Length -and $Bytes[$Index.Value] -ne 10) {
                $Index.Value++
            }
            continue
        }

        if ($b -gt 32) {
            break
        }

        $Index.Value++
    }
}

function Skip-Ppm-RasterSeparator {
    param(
        [byte[]]$Bytes,
        [ref]$Index
    )

    if ($Index.Value -lt $Bytes.Length -and ($Bytes[$Index.Value] -eq 13 -or $Bytes[$Index.Value] -eq 10 -or $Bytes[$Index.Value] -eq 9 -or $Bytes[$Index.Value] -eq 32)) {
        $first = $Bytes[$Index.Value]
        $Index.Value++
        if ($first -eq 13 -and $Index.Value -lt $Bytes.Length -and $Bytes[$Index.Value] -eq 10) {
            $Index.Value++
        }
    }
}

function Read-FrameBitmap {
    param([string]$Path)

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    if ([System.IO.Path]::GetExtension($resolvedPath).Equals(".ppm", [System.StringComparison]::OrdinalIgnoreCase)) {
        $bytes = [System.IO.File]::ReadAllBytes($resolvedPath)
        $index = 0
        $magic = Read-Token -Bytes $bytes -Index ([ref]$index)
        if ($magic -ne "P6") {
            throw "Unsupported PPM format '$magic'. Only binary P6 PPM frames are supported."
        }

        $width = [int](Read-Token -Bytes $bytes -Index ([ref]$index))
        $height = [int](Read-Token -Bytes $bytes -Index ([ref]$index))
        $maxValue = [int](Read-Token -Bytes $bytes -Index ([ref]$index))
        if ($maxValue -ne 255) {
            throw "Unsupported PPM max value '$maxValue'."
        }

        Skip-Ppm-RasterSeparator -Bytes $bytes -Index ([ref]$index)
        $expectedBytes = $width * $height * 3
        if ($bytes.Length - $index -lt $expectedBytes) {
            throw "PPM pixel data is truncated."
        }

        $bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $offset = $index
        for ($y = 0; $y -lt $height; $y++) {
            for ($x = 0; $x -lt $width; $x++) {
                $r = $bytes[$offset]
                $g = $bytes[$offset + 1]
                $b = $bytes[$offset + 2]
                $offset += 3
                $bitmap.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($r, $g, $b))
            }
        }

        return $bitmap
    }

    return [System.Drawing.Bitmap]::new($resolvedPath)
}

function Get-RgbKey {
    param([System.Drawing.Color]$Color)
    return "{0},{1},{2}" -f $Color.R, $Color.G, $Color.B
}

function Get-FrameSignature {
    param([System.Drawing.Bitmap]$Bitmap)

    $borderColor = $Bitmap.GetPixel(0, 0)
    $borderKey = Get-RgbKey $borderColor
    $minX = $Bitmap.Width
    $minY = $Bitmap.Height
    $maxX = -1
    $maxY = -1
    $colorCounts = @{}

    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            $key = Get-RgbKey ($Bitmap.GetPixel($x, $y))
            if (-not $colorCounts.ContainsKey($key)) {
                $colorCounts[$key] = 0
            }
            $colorCounts[$key]++

            if ($key -ne $borderKey) {
                if ($x -lt $minX) { $minX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }

    if ($maxX -lt 0) {
        throw "Could not find a non-border display area."
    }

    $innerCounts = @{}
    for ($y = $minY; $y -le $maxY; $y++) {
        for ($x = $minX; $x -le $maxX; $x++) {
            $key = Get-RgbKey ($Bitmap.GetPixel($x, $y))
            if (-not $innerCounts.ContainsKey($key)) {
                $innerCounts[$key] = 0
            }
            $innerCounts[$key]++
        }
    }

    $backgroundKey = ($innerCounts.GetEnumerator() | Sort-Object -Property Value -Descending | Select-Object -First 1).Key

    return [ordered]@{
        width = $Bitmap.Width
        height = $Bitmap.Height
        borderColor = $borderKey
        innerBackgroundColor = $backgroundKey
        innerBox = [ordered]@{
            left = $minX
            top = $minY
            rightExclusive = $maxX + 1
            bottomExclusive = $maxY + 1
            width = $maxX - $minX + 1
            height = $maxY - $minY + 1
        }
        colorCounts = [ordered]@{}
    }
}

function Fill-ColorCounts {
    param(
        [System.Collections.Specialized.OrderedDictionary]$Signature,
        [System.Drawing.Bitmap]$Bitmap
    )

    $counts = @{}
    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            $key = Get-RgbKey ($Bitmap.GetPixel($x, $y))
            if (-not $counts.ContainsKey($key)) {
                $counts[$key] = 0
            }
            $counts[$key]++
        }
    }

    foreach ($entry in ($counts.GetEnumerator() | Sort-Object -Property Value -Descending)) {
        $Signature.colorCounts[$entry.Key] = $entry.Value
    }
}

function Get-NormalizedClass {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        [int]$X,
        [int]$Y,
        [string]$BackgroundKey
    )

    $key = Get-RgbKey ($Bitmap.GetPixel($X, $Y))
    if ($key -eq $BackgroundKey) {
        return 1
    }

    return 0
}

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$viceBitmap = Read-FrameBitmap -Path $ViceFrame
$emulatorBitmap = Read-FrameBitmap -Path $EmulatorFrame

try {
    $viceSignature = Get-FrameSignature -Bitmap $viceBitmap
    $emulatorSignature = Get-FrameSignature -Bitmap $emulatorBitmap
    Fill-ColorCounts -Signature $viceSignature -Bitmap $viceBitmap
    Fill-ColorCounts -Signature $emulatorSignature -Bitmap $emulatorBitmap

    $viceBox = $viceSignature.innerBox
    $emulatorBox = $emulatorSignature.innerBox
    $dimensionsMatch = $viceBox.width -eq $emulatorBox.width -and $viceBox.height -eq $emulatorBox.height
    $mismatchCount = 0
    $firstMismatches = @()

    if ($dimensionsMatch) {
        $diffBitmap = [System.Drawing.Bitmap]::new($viceBox.width, $viceBox.height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        for ($y = 0; $y -lt $viceBox.height; $y++) {
            for ($x = 0; $x -lt $viceBox.width; $x++) {
                $viceClass = Get-NormalizedClass -Bitmap $viceBitmap -X ($viceBox.left + $x) -Y ($viceBox.top + $y) -BackgroundKey $viceSignature.innerBackgroundColor
                $emulatorClass = Get-NormalizedClass -Bitmap $emulatorBitmap -X ($emulatorBox.left + $x) -Y ($emulatorBox.top + $y) -BackgroundKey $emulatorSignature.innerBackgroundColor
                if ($viceClass -eq $emulatorClass) {
                    $diffBitmap.SetPixel($x, $y, [System.Drawing.Color]::Black)
                }
                else {
                    $mismatchCount++
                    if ($firstMismatches.Count -lt 20) {
                        $firstMismatches += [ordered]@{
                            x = $x
                            y = $y
                            viceClass = $viceClass
                            emulatorClass = $emulatorClass
                        }
                    }
                    $diffBitmap.SetPixel($x, $y, [System.Drawing.Color]::Red)
                }
            }
        }

        $diffPath = Join-Path $OutputDirectory ($Name + ".diff.png")
        $diffBitmap.Save($diffPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $diffBitmap.Dispose()
    }
    else {
        $diffPath = ""
        $mismatchCount = -1
        $firstMismatches += [ordered]@{
            x = -1
            y = -1
            viceClass = $viceBox.width.ToString() + "x" + $viceBox.height.ToString()
            emulatorClass = $emulatorBox.width.ToString() + "x" + $emulatorBox.height.ToString()
        }
    }

    $report = [ordered]@{
        viceFrame = (Resolve-Path -LiteralPath $ViceFrame).Path
        emulatorFrame = (Resolve-Path -LiteralPath $EmulatorFrame).Path
        normalizedInnerDimensionsMatch = $dimensionsMatch
        normalizedMismatches = $mismatchCount
        firstMismatches = $firstMismatches
        diff = $diffPath
        vice = $viceSignature
        emulator = $emulatorSignature
        comparedUtc = [DateTime]::UtcNow.ToString("O")
    }

    $reportPath = Join-Path $OutputDirectory ($Name + ".json")
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding UTF8

    Write-Host "VICE inner: $($viceBox.width)x$($viceBox.height) at $($viceBox.left),$($viceBox.top)"
    Write-Host "EMU  inner: $($emulatorBox.width)x$($emulatorBox.height) at $($emulatorBox.left),$($emulatorBox.top)"
    if (-not $dimensionsMatch) {
        Write-Host "Normalized dimensions differ; pixel diff skipped."
    }
    Write-Host "Normalized mismatches: $mismatchCount"
    Write-Host "Report: $((Resolve-Path -LiteralPath $reportPath).Path)"
    if ($diffPath -ne "") {
        Write-Host "Diff: $((Resolve-Path -LiteralPath $diffPath).Path)"
    }
}
finally {
    $viceBitmap.Dispose()
    $emulatorBitmap.Dispose()
}
