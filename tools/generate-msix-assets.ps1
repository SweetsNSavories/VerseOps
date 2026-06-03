# Generate MSIX visual assets (Square/Wide tile PNGs, Store logo, splash) from
# the existing app.ico. Run once when the icon changes; outputs are committed to
# VerseOps.App/Assets/MsixLogos/ so CI MSIX packaging is deterministic.
[CmdletBinding()]
param(
    [string] $IconPath,
    [string] $OutputDir
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot can be empty when invoked via `powershell.exe -File` from a
# non-PowerShell shell, so resolve defaults relative to MyInvocation if needed.
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $IconPath)  { $IconPath  = Join-Path $root '..\VerseOps.App\Assets\app.ico' }
if (-not $OutputDir) { $OutputDir = Join-Path $root '..\VerseOps.App\Assets\MsixLogos' }

Add-Type -AssemblyName System.Drawing

$IconPath = (Resolve-Path $IconPath).Path
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path

Write-Host "Source : $IconPath"
Write-Host "Output : $OutputDir"

$icon = [System.Drawing.Icon]::new($IconPath)
$source = $icon.ToBitmap()

$sizes = [ordered]@{
    'Square44x44Logo.png'   = @(44, 44)
    'Square71x71Logo.png'   = @(71, 71)
    'Square150x150Logo.png' = @(150, 150)
    'Square310x310Logo.png' = @(310, 310)
    'Wide310x150Logo.png'   = @(310, 150)
    'StoreLogo.png'         = @(50, 50)
    'SplashScreen.png'      = @(620, 300)
}

foreach ($name in $sizes.Keys) {
    $w, $h = $sizes[$name]
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $square = [Math]::Min($w, $h)
    $x = ($w - $square) / 2
    $y = ($h - $square) / 2
    $g.DrawImage($source, $x, $y, $square, $square)

    $g.Dispose()
    $outPath = Join-Path $OutputDir $name
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ("  -> {0,-24} {1}x{2}" -f $name, $w, $h)
}

$source.Dispose()
$icon.Dispose()
Write-Host "Done."
