param(
    [string]$ViceRoot = "GTK3VICE-3.10-win64",
    [string]$OutputDirectory = "artifacts\golden-fixtures"
)

$ErrorActionPreference = "Stop"

function Resolve-Tool {
    param(
        [string]$ToolName,
        [string]$ViceRootPath
    )

    $repoRoot = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $repoRoot (Join-Path $ViceRootPath ("bin\" + $ToolName))),
        (Join-Path $ViceRootPath ("bin\" + $ToolName)),
        $ToolName
    )

    foreach ($candidate in $candidates) {
        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($command -ne $null) {
            return $command.Source
        }
    }

    throw "Could not find $ToolName. Pass -ViceRoot or put VICE tools on PATH."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$petcat = Resolve-Tool "petcat.exe" $ViceRoot
$c1541 = Resolve-Tool "c1541.exe" $ViceRoot

$basicPath = Join-Path $outputPath "aa.bas"
$relocatedPrg = Join-Path $outputPath "aa-0801.prg"
$absolutePrg = Join-Path $outputPath "aa-absolute-2000.prg"
$d64Path = Join-Path $outputPath "load-relocate.d64"
$metadataPath = Join-Path $outputPath "fixtures.json"

@(
    "10 POKE49152,66:POKE49153,65:POKE49154,83:POKE49155,73:POKE49156,67:END"
) | Set-Content -Path $basicPath -Encoding ASCII

& $petcat -w2 -l 0801 -o $relocatedPrg -- $basicPath

$programBytes = [System.IO.File]::ReadAllBytes($relocatedPrg)
$programBytes[0] = 0x00
$programBytes[1] = 0x20
[System.IO.File]::WriteAllBytes($absolutePrg, $programBytes)

Remove-Item -LiteralPath $d64Path -ErrorAction SilentlyContinue
& $c1541 -format "GOLDEN,01" d64 $d64Path -write $absolutePrg aa -list

$metadata = [ordered]@{
    fixtures = @(
        [ordered]@{
            id = "load-aa-relocatable-d64"
            d64 = $d64Path
            prg = $absolutePrg
            description = "D64 contains BASIC program AA with file load address `$2000; LOAD`"AA`",8 must relocate it to BASIC start `$0801."
        }
    )
    generatedUtc = [DateTime]::UtcNow.ToString("O")
    petcat = $petcat
    c1541 = $c1541
}

$metadata | ConvertTo-Json -Depth 5 | Set-Content -Path $metadataPath -Encoding UTF8

Write-Host "Golden fixtures written:"
Write-Host "  D64:      $d64Path"
Write-Host "  PRG:      $absolutePrg"
Write-Host "  Metadata: $metadataPath"
