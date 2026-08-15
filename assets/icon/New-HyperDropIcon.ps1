#Requires -Version 7

<#
.SYNOPSIS
    Generates the HyperDrop application icon.

.DESCRIPTION
    This script is the single source of truth for the icon artwork. The geometry is declared once,
    below, then rendered with WPF into a multi-resolution Windows .ico, written out verbatim as an
    .svg for the vector original, and exported as a PNG for the README.

    Re-run it after changing the artwork:

        pwsh -File assets/icon/New-HyperDropIcon.ps1

    Frames at 64 pixels and below are stored as 32-bit BGRA bitmaps and the two large frames as PNG,
    which is what Windows shells and System.Drawing.Icon both expect.
#>

[CmdletBinding()]
param(
    [string] $IcoPath = (Join-Path $PSScriptRoot '..\..\src\HyperDrop.App\Assets\HyperDrop.ico'),
    [string] $SvgPath = (Join-Path $PSScriptRoot 'hyperdrop.svg'),
    [string] $PngPath = (Join-Path $PSScriptRoot 'hyperdrop-256.png')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

# --- Artwork -----------------------------------------------------------------------------------
# Everything is authored on a 256 x 256 canvas and scaled down per frame.

$Canvas = 256.0
$TileRadius = 58.0

# A droplet falling into the guest. The two subpaths are combined with the even-odd fill rule so
# the arrow is a hole punched through the droplet and the tile gradient shows through it.
$DropletOutline = 'M 128,34 C 152,68 182,102 182,140 A 54,54 0 1 1 74,140 C 74,102 104,68 128,34 Z'
$ArrowCutout = 'M 117,88 L 139,88 L 139,142 L 156,142 L 128,178 L 100,142 L 117,142 Z'

# Below this the arrow collapses into a smudge, so the smallest frames get the plain droplet. Every
# frame keeps the same silhouette, which is what the eye actually recognises at that scale.
$ArrowMinimumSize = 32

# The surface it lands on.
$LandingRect = @{ X = 56.0; Y = 210.0; Width = 144.0; Height = 18.0; Radius = 9.0 }

$TileStops = @(
    @{ Offset = 0.0; Color = '#3BA9F5' }
    @{ Offset = 0.5; Color = '#1276C7' }
    @{ Offset = 1.0; Color = '#0A4E92' }
)

$GlossOpacity = 0.22
$GlossEnd = 0.45
$RimOpacity = 0.16
$RimThickness = 3.0
$RimInset = 4.0
$LandingOpacity = 0.9

$Sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$PngFrameSizes = @(128, 256)

# --- Rendering ---------------------------------------------------------------------------------

function New-Color {
    param([string] $Hex, [double] $Opacity = 1.0)

    $color = [System.Windows.Media.ColorConverter]::ConvertFromString($Hex)
    [System.Windows.Media.Color]::FromArgb([byte][math]::Round($Opacity * 255), $color.R, $color.G, $color.B)
}

function New-VerticalGradient {
    param([hashtable[]] $Stops)

    $brush = [System.Windows.Media.LinearGradientBrush]::new()
    $brush.StartPoint = [System.Windows.Point]::new(0, 0)
    $brush.EndPoint = [System.Windows.Point]::new(0, 1)

    foreach ($stop in $Stops) {
        $brush.GradientStops.Add(
            [System.Windows.Media.GradientStop]::new((New-Color $stop.Color $stop.Opacity), $stop.Offset))
    }

    $brush.Freeze()
    $brush
}

function New-IconVisual {
    param([int] $Size)

    $tile = [System.Windows.Media.LinearGradientBrush]::new()
    $tile.StartPoint = [System.Windows.Point]::new(0, 0)
    $tile.EndPoint = [System.Windows.Point]::new(1, 1)
    foreach ($stop in $TileStops) {
        $tile.GradientStops.Add(
            [System.Windows.Media.GradientStop]::new((New-Color $stop.Color), $stop.Offset))
    }
    $tile.Freeze()

    $gloss = New-VerticalGradient @(
        @{ Offset = 0.0; Color = '#FFFFFF'; Opacity = $GlossOpacity }
        @{ Offset = $GlossEnd; Color = '#FFFFFF'; Opacity = 0.0 }
    )

    $white = [System.Windows.Media.SolidColorBrush]::new((New-Color '#FFFFFF'))
    $white.Freeze()

    $landing = [System.Windows.Media.SolidColorBrush]::new((New-Color '#FFFFFF' $LandingOpacity))
    $landing.Freeze()

    $rim = [System.Windows.Media.Pen]::new(
        [System.Windows.Media.SolidColorBrush]::new((New-Color '#FFFFFF' $RimOpacity)), $RimThickness)
    $rim.Freeze()

    $tileRect = [System.Windows.Rect]::new(0, 0, $Canvas, $Canvas)
    $rimRect = [System.Windows.Rect]::new(
        $RimInset, $RimInset, $Canvas - (2 * $RimInset), $Canvas - (2 * $RimInset))

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $context.PushTransform([System.Windows.Media.ScaleTransform]::new($Size / $Canvas, $Size / $Canvas))

        $context.DrawRoundedRectangle($tile, $null, $tileRect, $TileRadius, $TileRadius)
        $context.DrawRoundedRectangle($gloss, $null, $tileRect, $TileRadius, $TileRadius)
        $context.DrawRoundedRectangle($null, $rim, $rimRect, $TileRadius - $RimInset, $TileRadius - $RimInset)

        $droplet = if ($Size -ge $ArrowMinimumSize) {
            [System.Windows.Media.Geometry]::Parse("F0 $DropletOutline $ArrowCutout")
        }
        else {
            [System.Windows.Media.Geometry]::Parse($DropletOutline)
        }

        $context.DrawGeometry($white, $null, $droplet)

        $context.DrawRoundedRectangle(
            $landing,
            $null,
            [System.Windows.Rect]::new(
                $LandingRect.X, $LandingRect.Y, $LandingRect.Width, $LandingRect.Height),
            $LandingRect.Radius,
            $LandingRect.Radius)

        $context.Pop()
    }
    finally {
        $context.Close()
    }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $bitmap.Freeze()
    $bitmap
}

function ConvertTo-PngBytes {
    param([System.Windows.Media.Imaging.BitmapSource] $Bitmap)

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))

    $stream = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        # The leading comma keeps PowerShell from unrolling the array into the output stream.
        , $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function ConvertTo-IcoBitmapBytes {
    param([System.Windows.Media.Imaging.BitmapSource] $Bitmap)

    # Icon bitmaps carry straight alpha, so undo WPF's premultiplication first.
    $straight = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new()
    $straight.BeginInit()
    $straight.Source = $Bitmap
    $straight.DestinationFormat = [System.Windows.Media.PixelFormats]::Bgra32
    $straight.EndInit()

    $size = $Bitmap.PixelWidth
    $stride = $size * 4
    $pixels = [byte[]]::new($stride * $size)
    $straight.CopyPixels($pixels, $stride, 0)

    $maskStride = [int][math]::Floor(($size + 31) / 32) * 4
    $maskBytes = $maskStride * $size

    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint32]40)               # biSize
        $writer.Write([int32]$size)             # biWidth
        $writer.Write([int32]($size * 2))       # biHeight: colour data plus the AND mask
        $writer.Write([uint16]1)                # biPlanes
        $writer.Write([uint16]32)               # biBitCount
        $writer.Write([uint32]0)                # biCompression: BI_RGB
        $writer.Write([uint32]($pixels.Length + $maskBytes))
        $writer.Write([int32]0)                 # biXPelsPerMeter
        $writer.Write([int32]0)                 # biYPelsPerMeter
        $writer.Write([uint32]0)                # biClrUsed
        $writer.Write([uint32]0)                # biClrImportant

        # Bitmap rows run bottom-up.
        for ($row = $size - 1; $row -ge 0; $row--) {
            $writer.Write($pixels, $row * $stride, $stride)
        }

        # An all-zero AND mask defers entirely to the alpha channel.
        $writer.Write([byte[]]::new($maskBytes))

        $writer.Flush()
        , $stream.ToArray()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Write-Ico {
    param([string] $Path, [hashtable[]] $Frames)

    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)                # reserved
        $writer.Write([uint16]1)                # type: icon
        $writer.Write([uint16]$Frames.Count)

        $offset = 6 + (16 * $Frames.Count)
        foreach ($frame in $Frames) {
            $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)              # palette entries
            $writer.Write([byte]0)              # reserved
            $writer.Write([uint16]1)            # colour planes
            $writer.Write([uint16]32)           # bits per pixel
            $writer.Write([uint32]$frame.Data.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Data.Length
        }

        foreach ($frame in $Frames) {
            $writer.Write([byte[]]$frame.Data)
        }

        $writer.Flush()
        [System.IO.File]::WriteAllBytes($Path, $stream.ToArray())
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Write-Svg {
    param([string] $Path)

    # The -f operator formats with the current culture, which on a German or French machine turns
    # 0.5 into "0,5" and silently corrupts the gradient. String interpolation elsewhere in this
    # function is already invariant, so only this needs pinning.
    $tileStopMarkup = ($TileStops | ForEach-Object {
        '      <stop offset="{0}" stop-color="{1}" />' -f
            $_.Offset.ToString([cultureinfo]::InvariantCulture), $_.Color
    }) -join "`n"

    $rimSize = $Canvas - (2 * $RimInset)
    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 $Canvas $Canvas">
  <title>HyperDrop</title>
  <defs>
    <linearGradient id="tile" x1="0" y1="0" x2="1" y2="1">
$tileStopMarkup
    </linearGradient>
    <linearGradient id="gloss" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity="$GlossOpacity" />
      <stop offset="$GlossEnd" stop-color="#FFFFFF" stop-opacity="0" />
    </linearGradient>
  </defs>
  <rect width="$Canvas" height="$Canvas" rx="$TileRadius" fill="url(#tile)" />
  <rect width="$Canvas" height="$Canvas" rx="$TileRadius" fill="url(#gloss)" />
  <rect x="$RimInset" y="$RimInset" width="$rimSize" height="$rimSize" rx="$($TileRadius - $RimInset)"
        fill="none" stroke="#FFFFFF" stroke-opacity="$RimOpacity" stroke-width="$RimThickness" />
  <path fill="#FFFFFF" fill-rule="evenodd" d="$DropletOutline $ArrowCutout" />
  <rect x="$($LandingRect.X)" y="$($LandingRect.Y)" width="$($LandingRect.Width)" height="$($LandingRect.Height)"
        rx="$($LandingRect.Radius)" fill="#FFFFFF" fill-opacity="$LandingOpacity" />
</svg>
"@

    [System.IO.File]::WriteAllText($Path, $svg, [System.Text.UTF8Encoding]::new($false))
}

# --- Output ------------------------------------------------------------------------------------

foreach ($path in @($IcoPath, $SvgPath, $PngPath)) {
    $directory = Split-Path -Path $path -Parent
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
}

$frames = foreach ($size in $Sizes) {
    $bitmap = New-IconVisual -Size $size
    $data = if ($PngFrameSizes -contains $size) {
        [byte[]](ConvertTo-PngBytes -Bitmap $bitmap)
    }
    else {
        [byte[]](ConvertTo-IcoBitmapBytes -Bitmap $bitmap)
    }

    if ($size -eq 256) {
        [System.IO.File]::WriteAllBytes(
            [System.IO.Path]::GetFullPath($PngPath), [byte[]](ConvertTo-PngBytes -Bitmap $bitmap))
    }

    @{ Size = $size; Data = $data }
}

Write-Ico -Path ([System.IO.Path]::GetFullPath($IcoPath)) -Frames $frames
Write-Svg -Path ([System.IO.Path]::GetFullPath($SvgPath))

$icoInfo = Get-Item -LiteralPath $IcoPath
Write-Host "Wrote $($icoInfo.FullName) ($($frames.Count) frames, $($icoInfo.Length) bytes)"
Write-Host "Wrote $([System.IO.Path]::GetFullPath($SvgPath))"
Write-Host "Wrote $([System.IO.Path]::GetFullPath($PngPath))"
