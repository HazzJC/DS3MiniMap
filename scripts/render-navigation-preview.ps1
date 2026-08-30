[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InputSvg,
    [Parameter(Mandatory)]
    [string]$OutputPng,
    [ValidateRange(512, 4096)]
    [int]$Size = 1400
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $InputSvg)) {
    throw "Navigation SVG was not found: $InputSvg"
}

$svg = Get-Content -LiteralPath $InputSvg -Raw
$viewBox = [regex]::Match($svg, '<svg[^>]*viewBox="(?<x>-?[\d.]+) (?<z>-?[\d.]+) (?<width>[\d.]+) (?<height>[\d.]+)"')
if (-not $viewBox.Success) { throw 'The navigation SVG has no readable viewBox.' }

$minimumX = [single]::Parse($viewBox.Groups['x'].Value, [Globalization.CultureInfo]::InvariantCulture)
$minimumZ = [single]::Parse($viewBox.Groups['z'].Value, [Globalization.CultureInfo]::InvariantCulture)
$width = [single]::Parse($viewBox.Groups['width'].Value, [Globalization.CultureInfo]::InvariantCulture)
$height = [single]::Parse($viewBox.Groups['height'].Value, [Globalization.CultureInfo]::InvariantCulture)
$padding = [int]($Size * 0.055)
$scale = [Math]::Min(($Size - 2 * $padding) / $width, ($Size - 2 * $padding) / $height)
$offsetX = ($Size - $width * $scale) / 2
$offsetY = ($Size - $height * $scale) / 2

$bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.Clear([Drawing.Color]::FromArgb(18, 20, 22))

    $groups = [regex]::Matches($svg, '(?s)<g id="[^"]+" fill="(?<colour>#[0-9a-fA-F]{6})"[^>]*>(?<content>.*?)</g>')
    foreach ($group in $groups) {
        $colour = [Drawing.ColorTranslator]::FromHtml($group.Groups['colour'].Value)
        $fill = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(180, $colour))
        $outline = [Drawing.Pen]::new([Drawing.Color]::FromArgb(185, 22, 19, 15), [Math]::Max(1.0, $scale / 150.0))
        try {
            foreach ($polygon in [regex]::Matches($group.Groups['content'].Value, '<polygon points="(?<points>[^"]+)"')) {
                $points = [regex]::Matches($polygon.Groups['points'].Value, '(?<x>-?[\d.]+),(?<z>-?[\d.]+)') | ForEach-Object {
                    $x = [single]::Parse($_.Groups['x'].Value, [Globalization.CultureInfo]::InvariantCulture)
                    $z = [single]::Parse($_.Groups['z'].Value, [Globalization.CultureInfo]::InvariantCulture)
                    [Drawing.PointF]::new($offsetX + ($x - $minimumX) * $scale, $offsetY + ($z - $minimumZ) * $scale)
                }
                if ($points.Count -ge 3) {
                    $graphics.FillPolygon($fill, [Drawing.PointF[]]$points)
                    $graphics.DrawPolygon($outline, [Drawing.PointF[]]$points)
                }
            }
        } finally {
            $fill.Dispose()
            $outline.Dispose()
        }
    }

    $frame = [Drawing.Pen]::new([Drawing.Color]::FromArgb(215, 163, 133, 80), 4)
    try { $graphics.DrawRectangle($frame, 2, 2, $Size - 5, $Size - 5) } finally { $frame.Dispose() }
    $directory = Split-Path -Parent $OutputPng
    if ($directory) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
    $bitmap.Save($OutputPng, [Drawing.Imaging.ImageFormat]::Png)
} finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Host "Wrote $OutputPng"
