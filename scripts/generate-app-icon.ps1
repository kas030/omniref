[CmdletBinding()]
param(
    [string]$InputPath,
    [string]$NormalizedPngPath,
    [string]$OutputPath,
    [ValidateRange(256, 4096)]
    [int]$CanvasSize = 1024,
    [ValidateRange(0, 1024)]
    [int]$Padding = 32
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$defaultSourcePngPath = Join-Path $PSScriptRoot '..\src\OmniRef.App\Assets\AppIcon.Source.png'
$defaultNormalizedPngPath = Join-Path $PSScriptRoot '..\src\OmniRef.App\Assets\AppIcon.png'
if ([string]::IsNullOrWhiteSpace($InputPath)) {
    $InputPath = $defaultSourcePngPath
}
if ([string]::IsNullOrWhiteSpace($NormalizedPngPath)) {
    $NormalizedPngPath = $defaultNormalizedPngPath
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot '..\src\OmniRef.App\Assets\AppIcon.ico'
}

$InputPath = [System.IO.Path]::GetFullPath($InputPath)
$NormalizedPngPath = [System.IO.Path]::GetFullPath($NormalizedPngPath)
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $InputPath -PathType Leaf)) {
    throw "Input PNG was not found: $InputPath"
}
if (($Padding * 2) -ge $CanvasSize) {
    throw "Padding must leave a positive content area inside the canvas."
}

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

if (-not ('OmniRef.IconTools.LanczosResampler' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;

namespace OmniRef.IconTools
{
    public static class LanczosResampler
    {
        private const double Radius = 3.0;

        public static byte[] ResizeBgra32(
            byte[] source,
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }
            if (source.Length != sourceWidth * sourceHeight * 4)
            {
                throw new ArgumentException("Unexpected BGRA32 buffer length.", "source");
            }

            Contributor[][] horizontal = CreateContributors(sourceWidth, targetWidth);
            Contributor[][] vertical = CreateContributors(sourceHeight, targetHeight);
            double[] intermediate = new double[targetWidth * sourceHeight * 4];

            for (int y = 0; y < sourceHeight; y++)
            {
                int sourceRow = y * sourceWidth * 4;
                int targetRow = y * targetWidth * 4;
                for (int x = 0; x < targetWidth; x++)
                {
                    double blue = 0;
                    double green = 0;
                    double red = 0;
                    double alpha = 0;
                    Contributor[] contributors = horizontal[x];
                    for (int index = 0; index < contributors.Length; index++)
                    {
                        Contributor contributor = contributors[index];
                        int sourceOffset = sourceRow + (contributor.Index * 4);
                        double sourceAlpha = source[sourceOffset + 3] / 255.0;
                        double weight = contributor.Weight;
                        blue += source[sourceOffset] * sourceAlpha * weight;
                        green += source[sourceOffset + 1] * sourceAlpha * weight;
                        red += source[sourceOffset + 2] * sourceAlpha * weight;
                        alpha += sourceAlpha * weight;
                    }

                    int targetOffset = targetRow + (x * 4);
                    intermediate[targetOffset] = blue;
                    intermediate[targetOffset + 1] = green;
                    intermediate[targetOffset + 2] = red;
                    intermediate[targetOffset + 3] = alpha;
                }
            }

            byte[] result = new byte[targetWidth * targetHeight * 4];
            for (int y = 0; y < targetHeight; y++)
            {
                Contributor[] contributors = vertical[y];
                for (int x = 0; x < targetWidth; x++)
                {
                    double blue = 0;
                    double green = 0;
                    double red = 0;
                    double alpha = 0;
                    for (int index = 0; index < contributors.Length; index++)
                    {
                        Contributor contributor = contributors[index];
                        int intermediateOffset =
                            ((contributor.Index * targetWidth) + x) * 4;
                        double weight = contributor.Weight;
                        blue += intermediate[intermediateOffset] * weight;
                        green += intermediate[intermediateOffset + 1] * weight;
                        red += intermediate[intermediateOffset + 2] * weight;
                        alpha += intermediate[intermediateOffset + 3] * weight;
                    }

                    alpha = Clamp(alpha, 0, 1);
                    int targetOffset = ((y * targetWidth) + x) * 4;
                    result[targetOffset + 3] = ToByte(alpha * 255.0);
                    if (alpha > 0.000001)
                    {
                        result[targetOffset] = ToByte(blue / alpha);
                        result[targetOffset + 1] = ToByte(green / alpha);
                        result[targetOffset + 2] = ToByte(red / alpha);
                    }
                }
            }

            return result;
        }

        private static Contributor[][] CreateContributors(int sourceSize, int targetSize)
        {
            double scale = (double)targetSize / sourceSize;
            double filterScale = Math.Min(1.0, scale);
            double support = Radius / filterScale;
            Contributor[][] result = new Contributor[targetSize][];

            for (int target = 0; target < targetSize; target++)
            {
                double center = ((target + 0.5) / scale) - 0.5;
                int first = (int)Math.Ceiling(center - support);
                int last = (int)Math.Floor(center + support);
                Dictionary<int, double> accumulated = new Dictionary<int, double>();
                double totalWeight = 0;

                for (int source = first; source <= last; source++)
                {
                    double distance = (center - source) * filterScale;
                    double weight = Lanczos(distance) * filterScale;
                    if (weight == 0)
                    {
                        continue;
                    }

                    int clampedSource = Math.Max(0, Math.Min(sourceSize - 1, source));
                    double existingWeight;
                    accumulated.TryGetValue(clampedSource, out existingWeight);
                    accumulated[clampedSource] = existingWeight + weight;
                    totalWeight += weight;
                }

                List<Contributor> contributors = new List<Contributor>(accumulated.Count);
                foreach (KeyValuePair<int, double> pair in accumulated)
                {
                    contributors.Add(new Contributor(pair.Key, pair.Value / totalWeight));
                }
                result[target] = contributors.ToArray();
            }

            return result;
        }

        private static double Lanczos(double value)
        {
            value = Math.Abs(value);
            if (value < 0.0000001)
            {
                return 1;
            }
            if (value >= Radius)
            {
                return 0;
            }

            double piValue = Math.PI * value;
            return (Math.Sin(piValue) / piValue) *
                   (Math.Sin(piValue / Radius) / (piValue / Radius));
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static byte ToByte(double value)
        {
            return (byte)Math.Round(
                Clamp(value, 0, 255),
                MidpointRounding.AwayFromZero);
        }

        private struct Contributor
        {
            public Contributor(int index, double weight)
            {
                Index = index;
                Weight = weight;
            }

            public int Index;
            public double Weight;
        }
    }
}
'@
}

function Get-AlphaBounds {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Media.Imaging.BitmapSource]$Source
    )

    $converted = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new(
        $Source,
        [System.Windows.Media.PixelFormats]::Bgra32,
        $null,
        0)
    $stride = $converted.PixelWidth * 4
    $pixels = [byte[]]::new($stride * $converted.PixelHeight)
    $converted.CopyPixels($pixels, $stride, 0)

    $minimumX = $converted.PixelWidth
    $minimumY = $converted.PixelHeight
    $maximumX = -1
    $maximumY = -1
    for ($y = 0; $y -lt $converted.PixelHeight; $y++) {
        $rowOffset = $y * $stride
        for ($x = 0; $x -lt $converted.PixelWidth; $x++) {
            if ($pixels[$rowOffset + ($x * 4) + 3] -eq 0) {
                continue
            }
            if ($x -lt $minimumX) { $minimumX = $x }
            if ($x -gt $maximumX) { $maximumX = $x }
            if ($y -lt $minimumY) { $minimumY = $y }
            if ($y -gt $maximumY) { $maximumY = $y }
        }
    }

    if ($maximumX -lt 0 -or $maximumY -lt 0) {
        throw "The input PNG is fully transparent."
    }

    return [System.Windows.Int32Rect]::new(
        $minimumX,
        $minimumY,
        ($maximumX - $minimumX) + 1,
        ($maximumY - $minimumY) + 1)
}

function ConvertTo-NormalizedBitmap {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Media.Imaging.BitmapSource]$Source,

        [Parameter(Mandatory)]
        [System.Windows.Int32Rect]$AlphaBounds,

        [Parameter(Mandatory)]
        [int]$Size,

        [Parameter(Mandatory)]
        [int]$SafePadding
    )

    $contentSize = $Size - (2 * $SafePadding)
    $scale = [Math]::Min(
        $contentSize / $AlphaBounds.Width,
        $contentSize / $AlphaBounds.Height)
    $targetWidth = [Math]::Max(
        1,
        [int][Math]::Round(
            $AlphaBounds.Width * $scale,
            [MidpointRounding]::AwayFromZero))
    $targetHeight = [Math]::Max(
        1,
        [int][Math]::Round(
            $AlphaBounds.Height * $scale,
            [MidpointRounding]::AwayFromZero))
    $offsetX = [int][Math]::Floor(($Size - $targetWidth) / 2)
    $offsetY = [int][Math]::Floor(($Size - $targetHeight) / 2)

    $cropped = [System.Windows.Media.Imaging.CroppedBitmap]::new(
        $Source,
        $AlphaBounds)
    $visual = [System.Windows.Media.DrawingVisual]::new()
    [System.Windows.Media.RenderOptions]::SetBitmapScalingMode(
        $visual,
        [System.Windows.Media.BitmapScalingMode]::HighQuality)
    $drawingContext = $visual.RenderOpen()
    try {
        $drawingContext.DrawImage(
            $cropped,
            [System.Windows.Rect]::new(
                $offsetX,
                $offsetY,
                $targetWidth,
                $targetHeight))
    }
    finally {
        $drawingContext.Close()
    }

    $normalized = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size,
        $Size,
        96,
        96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $normalized.Render($visual)
    $normalized.Freeze()
    return $normalized
}

function ConvertTo-PngBytes {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Media.Imaging.BitmapSource]$Source
    )

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add(
        [System.Windows.Media.Imaging.BitmapFrame]::Create($Source))
    $memory = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($memory)
        return ,$memory.ToArray()
    }
    finally {
        $memory.Dispose()
    }
}

function ConvertTo-PngIconFrame {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Media.Imaging.BitmapSource]$Source,

        [Parameter(Mandatory)]
        [int]$Size
    )

    $converted = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new(
        $Source,
        [System.Windows.Media.PixelFormats]::Bgra32,
        $null,
        0)
    $sourceStride = $converted.PixelWidth * 4
    $sourcePixels = [byte[]]::new(
        $sourceStride * $converted.PixelHeight)
    $converted.CopyPixels($sourcePixels, $sourceStride, 0)
    $renderedPixels = [OmniRef.IconTools.LanczosResampler]::ResizeBgra32(
        $sourcePixels,
        $converted.PixelWidth,
        $converted.PixelHeight,
        $Size,
        $Size)
    $rendered = [System.Windows.Media.Imaging.BitmapSource]::Create(
        $Size,
        $Size,
        96,
        96,
        [System.Windows.Media.PixelFormats]::Bgra32,
        $null,
        $renderedPixels,
        $Size * 4)
    $rendered.Freeze()
    return ConvertTo-PngBytes -Source $rendered
}

function New-TemporarySiblingPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $directory = [System.IO.Path]::GetDirectoryName($Path)
    if (-not [System.IO.Directory]::Exists($directory)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    return Join-Path `
        $directory `
        ".$([System.IO.Path]::GetFileName($Path)).$([guid]::NewGuid().ToString('N')).tmp"
}

$inputStream = [System.IO.File]::OpenRead($InputPath)
try {
    $decoder = [System.Windows.Media.Imaging.PngBitmapDecoder]::new(
        $inputStream,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    if ($decoder.Frames.Count -ne 1) {
        throw "Expected a single-frame PNG, found $($decoder.Frames.Count) frames."
    }
    $source = $decoder.Frames[0]
    if ($source.CanFreeze) {
        $source.Freeze()
    }
}
finally {
    $inputStream.Dispose()
}

$alphaBounds = Get-AlphaBounds -Source $source
if ([Math]::Max($alphaBounds.Width, $alphaBounds.Height) -lt 256) {
    throw "The visible PNG content must be at least 256 pixels on one axis; found $($alphaBounds.Width)x$($alphaBounds.Height)."
}
$normalized = ConvertTo-NormalizedBitmap `
    -Source $source `
    -AlphaBounds $alphaBounds `
    -Size $CanvasSize `
    -SafePadding $Padding

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    [byte[]]$frame = ConvertTo-PngIconFrame -Source $normalized -Size $size
    $frames.Add($frame)
}

$normalizedTemporaryPath = New-TemporarySiblingPath -Path $NormalizedPngPath
$icoTemporaryPath = New-TemporarySiblingPath -Path $OutputPath
try {
    [byte[]]$normalizedBytes = ConvertTo-PngBytes -Source $normalized
    [System.IO.File]::WriteAllBytes($normalizedTemporaryPath, $normalizedBytes)

    $fileStream = [System.IO.File]::Create($icoTemporaryPath)
    $writer = [System.IO.BinaryWriter]::new($fileStream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$frames.Count)

        [uint32]$offset = 6 + (16 * $frames.Count)
        for ($index = 0; $index -lt $frames.Count; $index++) {
            $size = $sizes[$index]
            $dimension = if ($size -eq 256) { [byte]0 } else { [byte]$size }
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length)
            $writer.Write($offset)
            $offset += [uint32]$frames[$index].Length
        }

        foreach ($frame in $frames) {
            $writer.Write($frame)
        }
    }
    finally {
        $writer.Dispose()
    }

    $pngValidationStream = [System.IO.File]::OpenRead($normalizedTemporaryPath)
    try {
        $pngValidationDecoder = [System.Windows.Media.Imaging.PngBitmapDecoder]::new(
            $pngValidationStream,
            [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        $pngFrame = $pngValidationDecoder.Frames[0]
        if ($pngFrame.PixelWidth -ne $CanvasSize -or
            $pngFrame.PixelHeight -ne $CanvasSize) {
            throw "Normalized PNG validation failed: $($pngFrame.PixelWidth)x$($pngFrame.PixelHeight)."
        }
    }
    finally {
        $pngValidationStream.Dispose()
    }

    $icoValidationStream = [System.IO.File]::OpenRead($icoTemporaryPath)
    try {
        $icoValidationDecoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
            $icoValidationStream,
            [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        $actualSizes = @(
            $icoValidationDecoder.Frames |
                ForEach-Object { $_.PixelWidth } |
                Sort-Object
        )
    }
    finally {
        $icoValidationStream.Dispose()
    }
    if (($actualSizes -join ',') -ne ($sizes -join ',')) {
        throw "ICO validation failed. Expected $($sizes -join ','); found $($actualSizes -join ',')."
    }

    Move-Item `
        -LiteralPath $normalizedTemporaryPath `
        -Destination $NormalizedPngPath `
        -Force
    Move-Item `
        -LiteralPath $icoTemporaryPath `
        -Destination $OutputPath `
        -Force
}
finally {
    foreach ($temporaryPath in @($normalizedTemporaryPath, $icoTemporaryPath)) {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

Write-Host "Normalized $NormalizedPngPath"
Write-Host "Canvas: ${CanvasSize}x${CanvasSize}; padding: $Padding px"
Write-Host "Source alpha bounds: $($alphaBounds.X),$($alphaBounds.Y) $($alphaBounds.Width)x$($alphaBounds.Height)"
Write-Host "Alpha: preserved from source"
Write-Host "Resampling: premultiplied-alpha Lanczos3"
Write-Host "Generated $OutputPath"
Write-Host "Frames: $($sizes -join ', ') px"
