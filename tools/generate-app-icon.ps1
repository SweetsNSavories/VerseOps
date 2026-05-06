# Generates a multi-resolution .ico for VerseOps.App from a programmatic
# rounded-blue-square + dashboard-grid glyph (similar to the Inventory Sentinel
# tile). Run once after edit; output is checked into source.
#   pwsh tools\generate-app-icon.ps1
#
# The Segoe Fluent Icons font ships on Windows 10/11 by default, so the glyph
# is guaranteed to render on any dev machine that builds this project.
Add-Type -AssemblyName System.Drawing

# Sizes the Windows shell expects in a single .ico file (covers Explorer at
# every zoom level + start-menu + jump-list + alt-tab thumbnails).
$sizes = @(16, 24, 32, 48, 64, 128, 256)

$assetsDir = Join-Path $PSScriptRoot '..\VerseOps.App\Assets'
[System.IO.Directory]::CreateDirectory($assetsDir) | Out-Null
$pngs = @()

# Brand blue from the App.xaml token palette (TokenBrandBackground is roughly #0078D4 in the dark theme).
$brand   = [System.Drawing.Color]::FromArgb(255, 0, 120, 212)
$brandHi = [System.Drawing.Color]::FromArgb(255, 16, 110, 190)
$white   = [System.Drawing.Color]::White

function New-IconBitmap {
    param([int]$size)

    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $g.Clear([System.Drawing.Color]::Transparent)

        # Rounded-square background. Corner radius proportional to size; matches the Sentinel tile.
        $radius = [Math]::Max(2, [int]($size * 0.18))
        $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $radius * 2
        $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
        $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
        $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
        $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
        $path.CloseFigure()

        # Vertical brand gradient — a touch of depth so the icon doesn't read as flat at large sizes.
        $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rect, $brand, $brandHi,
            [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
        try   { $g.FillPath($gradient, $path) }
        finally { $gradient.Dispose() }

        # Dashboard-grid glyph (4 tiles: large header bar, two small cells, one wide). White on blue.
        # Layout uses a virtual 100x100 grid mapped into the icon's central 64% region so the glyph
        # has a comfortable padding regardless of size.
        $padding = [int]($size * 0.18)
        $inner = New-Object System.Drawing.Rectangle($padding, $padding, $size - 2*$padding, $size - 2*$padding)
        $unit = $inner.Width / 100.0

        function Tile($x,$y,$w,$h) {
            $rx = [int]($inner.X + $x * $unit)
            $ry = [int]($inner.Y + $y * $unit)
            $rw = [int]($w * $unit)
            $rh = [int]($h * $unit)
            $r  = [Math]::Max(1, [int]($size * 0.04))
            $tilePath = New-Object System.Drawing.Drawing2D.GraphicsPath
            $tileRect = New-Object System.Drawing.Rectangle($rx, $ry, $rw, $rh)
            $td = $r * 2
            if ($td -ge [Math]::Min($rw,$rh)) { $td = [Math]::Max(2, [Math]::Min($rw,$rh) - 2) }
            $tilePath.AddArc($tileRect.X, $tileRect.Y, $td, $td, 180, 90)
            $tilePath.AddArc($tileRect.Right - $td, $tileRect.Y, $td, $td, 270, 90)
            $tilePath.AddArc($tileRect.Right - $td, $tileRect.Bottom - $td, $td, $td, 0, 90)
            $tilePath.AddArc($tileRect.X, $tileRect.Bottom - $td, $td, $td, 90, 90)
            $tilePath.CloseFigure()
            $brush = New-Object System.Drawing.SolidBrush($white)
            try   { $g.FillPath($brush, $tilePath) }
            finally { $brush.Dispose(); $tilePath.Dispose() }
        }

        # Top-left wide tile (toolbar / hero).
        Tile  0   0  46 18
        # Top-right small tile (KPI).
        Tile 54   0  46 18
        # Middle-left tall tile (env grid).
        Tile  0  26  46 74
        # Middle-right two stacked tiles (drawer + detail).
        Tile 54  26  46 32
        Tile 54  66  46 34

    } finally {
        $g.Dispose()
    }
    return $bmp
}

# Render each size and stash the bitmap.
$bitmaps = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap -size $s
    $bitmaps += $bmp
    # Also write a PNG side-car for the title bar / drawer (WPF can use PNGs directly via pack URIs).
    $pngPath = Join-Path $assetsDir ("app-{0}.png" -f $s)
    $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += $pngPath
}

# Stitch all sizes into a single .ico file. Format reference:
#   https://en.wikipedia.org/wiki/ICO_(file_format)
# Each frame is stored as a PNG payload (supported on Windows Vista+) — keeps
# the file small and crisp at every scale.
$icoPath = Join-Path $assetsDir 'app.ico'
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
try {
    # ICONDIR header.
    $bw.Write([uint16]0)              # reserved
    $bw.Write([uint16]1)              # type = 1 (icon)
    $bw.Write([uint16]$bitmaps.Count) # image count

    # Reserve the directory entries (16 bytes each) before the PNG payloads.
    $entryStart = $ms.Position
    $payloadOffset = $entryStart + (16 * $bitmaps.Count)
    $payloads = @()
    foreach ($bmp in $bitmaps) {
        $pngStream = New-Object System.IO.MemoryStream
        $bmp.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
        $payloads += ,$pngStream.ToArray()
        $pngStream.Dispose()
    }

    # Write directory entries.
    for ($i = 0; $i -lt $bitmaps.Count; $i++) {
        $bmp = $bitmaps[$i]
        $size = $bmp.Width
        # 0 means 256 in the ICO spec — use 0 for the 256 frame.
        $w = if ($size -ge 256) { 0 } else { $size }
        $h = if ($size -ge 256) { 0 } else { $size }
        $payloadLen = $payloads[$i].Length

        $bw.Write([byte]$w)            # width
        $bw.Write([byte]$h)            # height
        $bw.Write([byte]0)             # color count (0 for >=256 colors)
        $bw.Write([byte]0)             # reserved
        $bw.Write([uint16]1)           # color planes
        $bw.Write([uint16]32)          # bits per pixel
        $bw.Write([uint32]$payloadLen) # image size
        $bw.Write([uint32]$payloadOffset)

        $payloadOffset += $payloadLen
    }

    # Write PNG payloads.
    foreach ($p in $payloads) {
        $bw.Write($p)
    }

    [System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
} finally {
    $bw.Dispose()
    $ms.Dispose()
    foreach ($bmp in $bitmaps) { $bmp.Dispose() }
}

Write-Host "Wrote $icoPath ($([System.IO.File]::ReadAllBytes($icoPath).Length) bytes)"
foreach ($p in $pngs) { Write-Host "Wrote $p" }
