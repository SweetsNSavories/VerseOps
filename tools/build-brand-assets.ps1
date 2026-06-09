# Rasterizes the VerseOps SVG brand assets in docs\brand\ into PNGs at the
# sizes used by GitHub, social cards, store listings, and favicons.
#
#   pwsh tools\build-brand-assets.ps1
#
# Output (overwritten on every run):
#   docs\brand\verseops-mark-256.png
#   docs\brand\verseops-mark-512.png
#   docs\brand\verseops-mark-1024.png
#   docs\brand\verseops-mark-32.png        (favicon-sized preview)
#   docs\brand\verseops-logo-1200.png      (README / blog header)
#   docs\brand\verseops-logo-2400.png      (high-DPI / store hero)
#   docs\brand\verseops-logo-dark-1200.png
#   docs\brand\verseops-logo-dark-2400.png
#
# Uses WPF (System.Windows.Media.Imaging) which ships with the .NET Framework
# on every Windows dev box — no ImageMagick / Inkscape dependency.
[CmdletBinding()]
param()

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$brandDir = Resolve-Path (Join-Path $PSScriptRoot '..\docs\brand')

function Render-Svg {
    param(
        [Parameter(Mandatory)] [string] $SvgPath,
        [Parameter(Mandatory)] [int]    $Width,
        [Parameter(Mandatory)] [int]    $Height,
        [Parameter(Mandatory)] [string] $OutPng
    )

    # WPF doesn't render SVG natively, so we host the SVG inside a tiny XAML
    # WebBrowser-via-WPF wrapper... actually the simplest path is to use a
    # System.Windows.Controls.Image bound to a DrawingImage produced by a
    # XAML loader. But XAML's <Image> won't read SVG either. So we render
    # via a transparent WPF window that hosts an Image whose Source is a
    # BitmapImage decoded from a System.Drawing rasterisation. That's
    # circular.
    #
    # Instead, since our SVGs are hand-authored with a fixed, simple set of
    # primitives (rect rx/ry, line, path with two L commands, circle, text),
    # we re-render them programmatically via System.Drawing using the same
    # geometry the SVG defines. This avoids pulling in a third-party SVG
    # renderer (SkiaSharp, Svg.NET) and keeps the script self-contained.
    throw "Render-Svg should not be called directly — use Render-Mark / Render-Lockup."
}

Add-Type -AssemblyName System.Drawing

function New-Mark {
    param([Parameter(Mandatory)][int] $Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.TextRenderingHint  = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
        $g.Clear([System.Drawing.Color]::Transparent)

        $scale = $Size / 1024.0
        function S($v) { [single]($v * $scale) }

        # Rounded brand square. Corner radius = 180 in viewBox units.
        $radius = [single]([Math]::Max(1, [int](S 180)))
        $rect = New-Object System.Drawing.RectangleF 0,0,([single]$Size),([single]$Size)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $radius * 2
        $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
        $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
        $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
        $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
        $path.CloseFigure()

        $brushBrand = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rect,
            [System.Drawing.Color]::FromArgb(255, 0, 120, 212),
            [System.Drawing.Color]::FromArgb(255, 16, 110, 190),
            [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
        try   { $g.FillPath($brushBrand, $path) }
        finally { $brushBrand.Dispose(); $path.Dispose() }

        # The V — two rounded white strokes converging at (512, 760).
        $penV = New-Object System.Drawing.Pen([System.Drawing.Color]::White, (S 116))
        $penV.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $penV.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
        try {
            $g.DrawLine($penV, (S 248), (S 296), (S 512), (S 760))
            $g.DrawLine($penV, (S 776), (S 296), (S 512), (S 760))
        } finally { $penV.Dispose() }

        # Faint translucent vertical link removed: was visually noisy and read
        # as a nail / screw through the V. The single signal dot above the
        # apex carries the "convergence point" idea on its own.

        # Single white signal dot centred above the V apex.
        $brushDot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        try {
            $rr = S 66
            $bx = (S 512) - $rr
            $by = (S 212) - $rr
            $g.FillEllipse($brushDot, $bx, $by, $rr * 2, $rr * 2)
        } finally { $brushDot.Dispose() }
    } finally {
        $g.Dispose()
    }
    return $bmp
}

function New-Lockup {
    param(
        [Parameter(Mandatory)][int]    $Width,
        [Parameter(Mandatory)][int]    $Height,
        [Parameter(Mandatory)][bool]   $Dark
    )

    $bmp = New-Object System.Drawing.Bitmap($Width, $Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
        $g.Clear([System.Drawing.Color]::Transparent)

        # The lockup viewBox is 2400 x 600. Scale everything proportionally.
        $scale = $Width / 2400.0
        function L($v) { [single]($v * $scale) }

        # --- Mark in the left 600 x 600 slot ------------------------------
        $markSize = [int]([Math]::Round(600 * $scale))
        $markBmp = New-Mark -Size $markSize
        try { $g.DrawImage($markBmp, 0, 0) } finally { $markBmp.Dispose() }

        # --- Wordmark -----------------------------------------------------
        # "Verse" (semibold, neutral) + "Ops" (heavy, accent blue).
        # Use Segoe UI Semibold / Black if present, else Segoe UI Bold.
        function PickFont($family, $size, $style) {
            try   { return New-Object System.Drawing.Font($family, $size, $style, [System.Drawing.GraphicsUnit]::Pixel) }
            catch { return New-Object System.Drawing.Font('Segoe UI', $size, $style, [System.Drawing.GraphicsUnit]::Pixel) }
        }

        $fontSize = L 280
        $fontVerse = PickFont 'Segoe UI Semibold' $fontSize ([System.Drawing.FontStyle]::Regular)
        $fontOps   = PickFont 'Segoe UI Black'    $fontSize ([System.Drawing.FontStyle]::Bold)

        $textColor   = if ($Dark) { [System.Drawing.Color]::FromArgb(242, 242, 242) } else { [System.Drawing.Color]::FromArgb(27, 27, 27) }
        $accentColor = if ($Dark) { [System.Drawing.Color]::FromArgb(46, 160, 242) } else { [System.Drawing.Color]::FromArgb(0, 120, 212) }

        $brushText   = New-Object System.Drawing.SolidBrush($textColor)
        $brushAccent = New-Object System.Drawing.SolidBrush($accentColor)

        try {
            # Vertically centre the wordmark on the mark (mark centre y ≈ 300
            # in viewBox units; ascent ≈ 0.78 em, so emTop ≈ centre - 0.5 em + 0.78 em
            # ≈ centre - 0.28 em ≈ 222 — close to (L 222)).
            $emTop = L 142
            $xVerse = L 700
            $g.DrawString('Verse', $fontVerse, $brushText,
                [single]$xVerse, [single]$emTop,
                [System.Drawing.StringFormat]::GenericTypographic)

            # Place "Ops" using actual measured width of "Verse" + a small
            # positive gap so the two halves read as one word but with a
            # clear weight break.
            $verseSize = $g.MeasureString('Verse', $fontVerse, ([int]::MaxValue),
                [System.Drawing.StringFormat]::GenericTypographic)
            $xOps = $xVerse + $verseSize.Width + (L 10)
            $g.DrawString('Ops', $fontOps, $brushAccent,
                [single]$xOps, [single]$emTop,
                [System.Drawing.StringFormat]::GenericTypographic)
        } finally {
            $brushText.Dispose(); $brushAccent.Dispose()
            $fontVerse.Dispose(); $fontOps.Dispose()
        }
    } finally {
        $g.Dispose()
    }
    return $bmp
}

function Save-Png {
    param(
        [Parameter(Mandatory)] [System.Drawing.Bitmap] $Bitmap,
        [Parameter(Mandatory)] [string] $Path
    )
    $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $Bitmap.Dispose()
    Write-Host "  wrote $Path"
}

Write-Host "Rendering VerseOps brand assets into $brandDir"

foreach ($size in 32, 80, 256, 512, 1024) {
    Save-Png -Bitmap (New-Mark -Size $size) `
             -Path   (Join-Path $brandDir "verseops-mark-$size.png")
}

foreach ($w in 1200, 2400) {
    $h = [int]($w / 4)
    Save-Png -Bitmap (New-Lockup -Width $w -Height $h -Dark $false) `
             -Path   (Join-Path $brandDir "verseops-logo-$w.png")
    Save-Png -Bitmap (New-Lockup -Width $w -Height $h -Dark $true) `
             -Path   (Join-Path $brandDir "verseops-logo-dark-$w.png")
}

# XrmToolBox plugin-tile base64 blobs (32 = SmallImage, 80 = BigImage).
# Print them so they can be pasted into VerseOpsPluginFactory.cs metadata.
Write-Host ""
Write-Host "XrmToolBox tile base64 (paste into VerseOpsPluginFactory.cs):"
foreach ($pair in @(@{ Key='SmallImageBase64'; Size=32 }, @{ Key='BigImageBase64'; Size=80 })) {
    $png = Join-Path $brandDir ("verseops-mark-{0}.png" -f $pair.Size)
    $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($png))
    Write-Host ("  {0} ({1}x{1}) length={2}" -f $pair.Key, $pair.Size, $b64.Length)
}

Write-Host "Done."
