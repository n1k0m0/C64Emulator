param(
    [Parameter(Mandatory = $true)]
    [string]$ReferenceFrame,

    [Parameter(Mandatory = $true)]
    [string]$EmulatorFrame,

    [string]$OutputDirectory = "artifacts\vice-vicii-compare",

    [string]$Name = "vicii-reference-compare",

    [ValidateSet("Exact", "BackgroundClass")]
    [string]$Mode = "Exact",

    [switch]$AutoAlign,

    [int]$SearchSampleStep = 4,

    [switch]$NoDiff
)

$ErrorActionPreference = "Stop"

$comparerSource = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

public sealed class ViciiImageInfo
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string BorderColor { get; set; }
    public string InnerBackgroundColor { get; set; }
    public int InnerLeft { get; set; }
    public int InnerTop { get; set; }
    public int InnerWidth { get; set; }
    public int InnerHeight { get; set; }
}

public sealed class ViciiMismatchInfo
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Reference { get; set; }
    public string Emulator { get; set; }
}

public sealed class ViciiCompareResult
{
    public string ReferenceFrame { get; set; }
    public string EmulatorFrame { get; set; }
    public string Mode { get; set; }
    public bool AutoAligned { get; set; }
    public int ReferenceLeft { get; set; }
    public int ReferenceTop { get; set; }
    public int EmulatorLeft { get; set; }
    public int EmulatorTop { get; set; }
    public int CompareWidth { get; set; }
    public int CompareHeight { get; set; }
    public int SearchSampleStep { get; set; }
    public int SampleMismatches { get; set; }
    public int NormalizedMismatches { get; set; }
    public double MismatchRate { get; set; }
    public string Diff { get; set; }
    public ViciiImageInfo Reference { get; set; }
    public ViciiImageInfo Emulator { get; set; }
    public List<ViciiMismatchInfo> FirstMismatches { get; set; }
}

public static class ViciiReferenceComparer
{
    private sealed class FrameImage
    {
        public string Path;
        public int Width;
        public int Height;
        public int[] Pixels;
        public int BorderColor;
        public int InnerBackgroundColor;
        public int InnerLeft;
        public int InnerTop;
        public int InnerWidth;
        public int InnerHeight;

        public int Pixel(int x, int y)
        {
            return Pixels[y * Width + x];
        }
    }

    private sealed class Alignment
    {
        public int ReferenceLeft;
        public int ReferenceTop;
        public int EmulatorLeft;
        public int EmulatorTop;
        public int Width;
        public int Height;
        public int SampleMismatches;
    }

    public static ViciiCompareResult Compare(
        string referencePath,
        string emulatorPath,
        string outputDirectory,
        string name,
        string mode,
        bool autoAlign,
        int searchSampleStep,
        bool writeDiff)
    {
        searchSampleStep = Math.Max(1, searchSampleStep);
        FrameImage reference = ReadFrame(referencePath);
        FrameImage emulator = ReadFrame(emulatorPath);
        FillSignature(reference);
        FillSignature(emulator);

        Alignment alignment = autoAlign
            ? FindAlignment(reference, emulator, mode, searchSampleStep)
            : new Alignment
            {
                ReferenceLeft = 0,
                ReferenceTop = 0,
                EmulatorLeft = 0,
                EmulatorTop = 0,
                Width = Math.Min(reference.Width, emulator.Width),
                Height = Math.Min(reference.Height, emulator.Height),
                SampleMismatches = 0
            };

        var result = new ViciiCompareResult();
        result.ReferenceFrame = System.IO.Path.GetFullPath(referencePath);
        result.EmulatorFrame = System.IO.Path.GetFullPath(emulatorPath);
        result.Mode = mode;
        result.AutoAligned = autoAlign;
        result.ReferenceLeft = alignment.ReferenceLeft;
        result.ReferenceTop = alignment.ReferenceTop;
        result.EmulatorLeft = alignment.EmulatorLeft;
        result.EmulatorTop = alignment.EmulatorTop;
        result.CompareWidth = alignment.Width;
        result.CompareHeight = alignment.Height;
        result.SearchSampleStep = searchSampleStep;
        result.SampleMismatches = alignment.SampleMismatches;
        result.Reference = ToInfo(reference);
        result.Emulator = ToInfo(emulator);
        result.FirstMismatches = new List<ViciiMismatchInfo>();

        int mismatches = 0;
        int[] diffPixels = writeDiff ? new int[alignment.Width * alignment.Height] : null;
        for (int y = 0; y < alignment.Height; y++)
        {
            for (int x = 0; x < alignment.Width; x++)
            {
                int referenceValue = GetComparableValue(reference, alignment.ReferenceLeft + x, alignment.ReferenceTop + y, mode);
                int emulatorValue = GetComparableValue(emulator, alignment.EmulatorLeft + x, alignment.EmulatorTop + y, mode);
                bool equal = referenceValue == emulatorValue;
                if (!equal)
                {
                    mismatches++;
                    if (result.FirstMismatches.Count < 20)
                    {
                        result.FirstMismatches.Add(new ViciiMismatchInfo
                        {
                            X = x,
                            Y = y,
                            Reference = FormatValue(referenceValue, mode),
                            Emulator = FormatValue(emulatorValue, mode)
                        });
                    }
                }

                if (diffPixels != null)
                {
                    diffPixels[y * alignment.Width + x] = equal ? 0x000000 : 0xFF0000;
                }
            }
        }

        result.NormalizedMismatches = mismatches;
        result.MismatchRate = alignment.Width == 0 || alignment.Height == 0
            ? 1.0
            : (double)mismatches / (double)(alignment.Width * alignment.Height);

        if (diffPixels != null)
        {
            Directory.CreateDirectory(outputDirectory);
            string diffPath = System.IO.Path.Combine(outputDirectory, name + ".diff.png");
            SaveRgbBitmap(diffPath, alignment.Width, alignment.Height, diffPixels);
            result.Diff = System.IO.Path.GetFullPath(diffPath);
        }
        else
        {
            result.Diff = string.Empty;
        }

        return result;
    }

    private static Alignment FindAlignment(FrameImage reference, FrameImage emulator, string mode, int sampleStep)
    {
        if (reference.Width <= emulator.Width && reference.Height <= emulator.Height)
        {
            Alignment best = FindNeedleInHaystack(reference, emulator, mode, sampleStep);
            return best;
        }

        if (emulator.Width <= reference.Width && emulator.Height <= reference.Height)
        {
            Alignment reverse = FindNeedleInHaystack(emulator, reference, mode, sampleStep);
            return new Alignment
            {
                ReferenceLeft = reverse.EmulatorLeft,
                ReferenceTop = reverse.EmulatorTop,
                EmulatorLeft = reverse.ReferenceLeft,
                EmulatorTop = reverse.ReferenceTop,
                Width = reverse.Width,
                Height = reverse.Height,
                SampleMismatches = reverse.SampleMismatches
            };
        }

        return new Alignment
        {
            ReferenceLeft = 0,
            ReferenceTop = 0,
            EmulatorLeft = 0,
            EmulatorTop = 0,
            Width = Math.Min(reference.Width, emulator.Width),
            Height = Math.Min(reference.Height, emulator.Height),
            SampleMismatches = int.MaxValue
        };
    }

    private static Alignment FindNeedleInHaystack(FrameImage needle, FrameImage haystack, string mode, int sampleStep)
    {
        int bestX = 0;
        int bestY = 0;
        int bestMismatches = int.MaxValue;
        int maxX = haystack.Width - needle.Width;
        int maxY = haystack.Height - needle.Height;

        for (int yOffset = 0; yOffset <= maxY; yOffset++)
        {
            for (int xOffset = 0; xOffset <= maxX; xOffset++)
            {
                int mismatches = 0;
                for (int y = 0; y < needle.Height; y += sampleStep)
                {
                    for (int x = 0; x < needle.Width; x += sampleStep)
                    {
                        int needleValue = GetComparableValue(needle, x, y, mode);
                        int hayValue = GetComparableValue(haystack, xOffset + x, yOffset + y, mode);
                        if (needleValue != hayValue)
                        {
                            mismatches++;
                            if (mismatches >= bestMismatches)
                            {
                                break;
                            }
                        }
                    }

                    if (mismatches >= bestMismatches)
                    {
                        break;
                    }
                }

                if (mismatches < bestMismatches)
                {
                    bestMismatches = mismatches;
                    bestX = xOffset;
                    bestY = yOffset;
                    if (bestMismatches == 0)
                    {
                        return new Alignment
                        {
                            ReferenceLeft = 0,
                            ReferenceTop = 0,
                            EmulatorLeft = bestX,
                            EmulatorTop = bestY,
                            Width = needle.Width,
                            Height = needle.Height,
                            SampleMismatches = bestMismatches
                        };
                    }
                }
            }
        }

        return new Alignment
        {
            ReferenceLeft = 0,
            ReferenceTop = 0,
            EmulatorLeft = bestX,
            EmulatorTop = bestY,
            Width = needle.Width,
            Height = needle.Height,
            SampleMismatches = bestMismatches
        };
    }

    private static int GetComparableValue(FrameImage image, int x, int y, string mode)
    {
        int pixel = image.Pixel(x, y);
        if (string.Equals(mode, "BackgroundClass", StringComparison.OrdinalIgnoreCase))
        {
            return pixel == image.InnerBackgroundColor ? 1 : 0;
        }

        return pixel;
    }

    private static string FormatValue(int value, string mode)
    {
        if (string.Equals(mode, "BackgroundClass", StringComparison.OrdinalIgnoreCase))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        return "#" + value.ToString("X6", CultureInfo.InvariantCulture);
    }

    private static ViciiImageInfo ToInfo(FrameImage image)
    {
        return new ViciiImageInfo
        {
            Width = image.Width,
            Height = image.Height,
            BorderColor = FormatValue(image.BorderColor, "Exact"),
            InnerBackgroundColor = FormatValue(image.InnerBackgroundColor, "Exact"),
            InnerLeft = image.InnerLeft,
            InnerTop = image.InnerTop,
            InnerWidth = image.InnerWidth,
            InnerHeight = image.InnerHeight
        };
    }

    private static void FillSignature(FrameImage image)
    {
        image.BorderColor = image.Pixels.Length == 0 ? 0 : image.Pixels[0];
        int minX = image.Width;
        int minY = image.Height;
        int maxX = -1;
        int maxY = -1;

        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (image.Pixel(x, y) != image.BorderColor)
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (maxX < 0)
        {
            minX = 0;
            minY = 0;
            maxX = image.Width - 1;
            maxY = image.Height - 1;
        }

        image.InnerLeft = minX;
        image.InnerTop = minY;
        image.InnerWidth = maxX - minX + 1;
        image.InnerHeight = maxY - minY + 1;

        var counts = new Dictionary<int, int>();
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int pixel = image.Pixel(x, y);
                int count;
                counts.TryGetValue(pixel, out count);
                counts[pixel] = count + 1;
            }
        }

        int bestPixel = image.BorderColor;
        int bestCount = -1;
        foreach (KeyValuePair<int, int> pair in counts)
        {
            if (pair.Value > bestCount)
            {
                bestCount = pair.Value;
                bestPixel = pair.Key;
            }
        }

        image.InnerBackgroundColor = bestPixel;
    }

    private static FrameImage ReadFrame(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        if (System.IO.Path.GetExtension(fullPath).Equals(".ppm", StringComparison.OrdinalIgnoreCase))
        {
            return ReadPpm(fullPath);
        }

        using (var source = new Bitmap(fullPath))
        using (var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawImageUnscaled(source, 0, 0);
            int[] pixels = ReadBitmapPixels(bitmap);
            return new FrameImage { Path = fullPath, Width = bitmap.Width, Height = bitmap.Height, Pixels = pixels };
        }
    }

    private static FrameImage ReadPpm(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int index = 0;
        string magic = ReadToken(bytes, ref index);
        if (magic != "P6")
        {
            throw new InvalidDataException("Unsupported PPM format '" + magic + "'.");
        }

        int width = int.Parse(ReadToken(bytes, ref index), CultureInfo.InvariantCulture);
        int height = int.Parse(ReadToken(bytes, ref index), CultureInfo.InvariantCulture);
        int maxValue = int.Parse(ReadToken(bytes, ref index), CultureInfo.InvariantCulture);
        if (maxValue != 255)
        {
            throw new InvalidDataException("Unsupported PPM max value '" + maxValue.ToString(CultureInfo.InvariantCulture) + "'.");
        }

        SkipPpmRasterSeparator(bytes, ref index);
        int[] pixels = new int[width * height];
        int pixelIndex = 0;
        for (int offset = index; pixelIndex < pixels.Length; offset += 3)
        {
            if (offset + 2 >= bytes.Length)
            {
                throw new InvalidDataException("PPM pixel data is truncated.");
            }

            pixels[pixelIndex++] = (bytes[offset] << 16) | (bytes[offset + 1] << 8) | bytes[offset + 2];
        }

        return new FrameImage { Path = path, Width = width, Height = height, Pixels = pixels };
    }

    private static string ReadToken(byte[] bytes, ref int index)
    {
        while (index < bytes.Length)
        {
            byte value = bytes[index];
            if (value == 35)
            {
                while (index < bytes.Length && bytes[index] != 10)
                {
                    index++;
                }
                continue;
            }

            if (value > 32)
            {
                break;
            }

            index++;
        }

        int start = index;
        while (index < bytes.Length && bytes[index] > 32)
        {
            index++;
        }

        return System.Text.Encoding.ASCII.GetString(bytes, start, index - start);
    }

    private static void SkipPpmRasterSeparator(byte[] bytes, ref int index)
    {
        if (index < bytes.Length && bytes[index] <= 32)
        {
            byte first = bytes[index++];
            if (first == 13 && index < bytes.Length && bytes[index] == 10)
            {
                index++;
            }
        }
    }

    private static int[] ReadBitmapPixels(Bitmap bitmap)
    {
        Rectangle rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int byteCount = Math.Abs(stride) * bitmap.Height;
            byte[] bytes = new byte[byteCount];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, byteCount);
            int[] pixels = new int[bitmap.Width * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int offset = row + x * 4;
                    int b = bytes[offset];
                    int g = bytes[offset + 1];
                    int r = bytes[offset + 2];
                    pixels[y * bitmap.Width + x] = (r << 16) | (g << 8) | b;
                }
            }

            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void SaveRgbBitmap(string path, int width, int height, int[] pixels)
    {
        using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        {
            Rectangle rect = new Rectangle(0, 0, width, height);
            BitmapData data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                int byteCount = Math.Abs(stride) * height;
                byte[] bytes = new byte[byteCount];
                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int pixel = pixels[y * width + x];
                        int offset = row + x * 4;
                        bytes[offset] = (byte)(pixel & 0xFF);
                        bytes[offset + 1] = (byte)((pixel >> 8) & 0xFF);
                        bytes[offset + 2] = (byte)((pixel >> 16) & 0xFF);
                        bytes[offset + 3] = 0xFF;
                    }
                }

                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, byteCount);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            bitmap.Save(path, ImageFormat.Png);
        }
    }
}
'@

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition $comparerSource -ReferencedAssemblies System.Drawing

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$result = [ViciiReferenceComparer]::Compare(
    (Resolve-Path -LiteralPath $ReferenceFrame).Path,
    (Resolve-Path -LiteralPath $EmulatorFrame).Path,
    (Resolve-Path -LiteralPath $OutputDirectory).Path,
    $Name,
    $Mode,
    [bool]$AutoAlign,
    $SearchSampleStep,
    -not [bool]$NoDiff)

$reportPath = Join-Path $OutputDirectory ($Name + ".json")
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host "Reference: $($result.Reference.Width)x$($result.Reference.Height)"
Write-Host "Emulator:  $($result.Emulator.Width)x$($result.Emulator.Height)"
Write-Host "Aligned reference at $($result.ReferenceLeft),$($result.ReferenceTop); emulator at $($result.EmulatorLeft),$($result.EmulatorTop)"
Write-Host "Compared: $($result.CompareWidth)x$($result.CompareHeight)"
Write-Host "Mismatches: $($result.NormalizedMismatches) ($('{0:P4}' -f $result.MismatchRate))"
Write-Host "Report: $((Resolve-Path -LiteralPath $reportPath).Path)"
if (-not [string]::IsNullOrWhiteSpace($result.Diff)) {
    Write-Host "Diff: $($result.Diff)"
}
