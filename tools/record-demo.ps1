# Records a short ffmpeg demo of the VerseOps WPF shell launching from a
# wiped cache (empty state, no tenant data). Also captures a still frame
# at the end for the README/blog assets.
#
# Outputs (under docs/brand/demo/):
#   verseops-empty-state.mp4
#   verseops-empty-state.gif
#   verseops-empty-state-frame.png
#
# PREREQUISITES — both must be true or the recording is wasted:
#   1. The Windows session must be UNLOCKED. ffmpeg gdigrab captures the
#      lockscreen otherwise (and WPF's DWM-backed compositor stops painting
#      frames while locked, so even PrintWindow comes back blank gray).
#   2. ffmpeg.exe must be on PATH (`winget install Gyan.FFmpeg` works).
#
# Safety: the LOCALAPPDATA\VerseOps cache is wiped at the start so no
# tenant data is loaded. No interactive sign-in is performed. The capture
# always shows the empty-state shell.

$ErrorActionPreference = 'Stop'

# Wipe the tenant-data cache up-front. See screenshot_data_leak rule.
Stop-Process -Name VerseOps.App, ffmpeg -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
Remove-Item "$env:LOCALAPPDATA\VerseOps\*" -Recurse -Force -ErrorAction SilentlyContinue

$exe = Join-Path $PSScriptRoot '..\VerseOps.App\bin\Release\net10.0-windows\VerseOps.App.exe'
if (-not (Test-Path $exe)) { throw "VerseOps.App.exe not found at $exe -- run `dotnet build -c Release` first" }

$outDir = Join-Path $PSScriptRoot '..\docs\brand\demo'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$mp4 = Join-Path $outDir 'verseops-empty-state.mp4'
$gif = Join-Path $outDir 'verseops-empty-state.gif'
$png = Join-Path $outDir 'verseops-empty-state-frame.png'
$pngTitle = Join-Path $outDir 'verseops-empty-state-titlebar.png'

Remove-Item $mp4, $gif, $png, $pngTitle -Force -ErrorAction SilentlyContinue

# 1. Start ffmpeg recording at 12 fps, primary display, 14 s duration.
#    -t 14 lets us include a few seconds of bare desktop + the launch
#    + ~6 s of the shell sitting idle.
$ff = Get-Command ffmpeg.exe -ErrorAction Stop
Write-Host "ffmpeg: $($ff.Source)"

$ffArgs = @(
    '-y',
    '-f', 'gdigrab',
    '-framerate', '12',
    '-offset_x', '0',
    '-offset_y', '0',
    '-video_size', '1920x1080',
    '-i', 'desktop',
    '-t', '14',
    '-c:v', 'libx264',
    '-preset', 'veryfast',
    '-crf', '23',
    '-pix_fmt', 'yuv420p',
    $mp4
)

Write-Host 'starting ffmpeg recording...'
$ffProc = Start-Process -FilePath $ff.Source -ArgumentList $ffArgs -PassThru -WindowStyle Hidden -RedirectStandardError "$outDir\ffmpeg.err.log" -RedirectStandardOutput "$outDir\ffmpeg.out.log"
Start-Sleep -Milliseconds 1500  # give ffmpeg a head-start

# 2. Launch VerseOps.App
Write-Host 'launching VerseOps.App...'
$appProc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 10  # let the shell render + settle

# 3. Wait for ffmpeg to finish its 14 s capture
Write-Host 'waiting for ffmpeg to finish...'
$ffProc.WaitForExit(30000) | Out-Null
if (-not $ffProc.HasExited) { $ffProc.Kill() }

# 4. Capture single-frame stills from the running window.
#    Use the PrintWindow variant (capture-window-printwindow.ps1) — it reads
#    the back-buffer directly so it survives partial obstruction. CopyFromScreen
#    silently captures whatever screen pixels are at the window rect.
Write-Host 'capturing still frames...'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'capture-window-printwindow.ps1') -ProcessName 'VerseOps.App' -OutPath $png | Out-Null

# 5. Close VerseOps.App cleanly
Write-Host 'stopping VerseOps.App...'
try { $appProc.CloseMainWindow() | Out-Null; $appProc.WaitForExit(3000) | Out-Null } catch {}
if (-not $appProc.HasExited) { Stop-Process -Id $appProc.Id -Force -ErrorAction SilentlyContinue }

# 6. Convert mp4 → gif using a palette pass (clean colours, smaller file).
Write-Host 'rendering gif...'
$palette = Join-Path $env:TEMP 'verseops-palette.png'
& $ff.Source -y -i $mp4 -vf 'fps=10,scale=960:-1:flags=lanczos,palettegen=stats_mode=diff' $palette 2>$null
& $ff.Source -y -i $mp4 -i $palette -lavfi 'fps=10,scale=960:-1:flags=lanczos [x]; [x][1:v] paletteuse=dither=bayer:bayer_scale=5' $gif 2>$null
Remove-Item $palette -Force -ErrorAction SilentlyContinue

# 7. Sanity-check the still: if it's a near-uniform colour, the session was
#    locked or WPF didn't paint — flag loudly so the operator notices.
if (Test-Path $png) {
    try {
        Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
        $bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $png))
        try {
            $w = $bmp.Width; $h = $bmp.Height
            $samples = @(
                $bmp.GetPixel(10,10), $bmp.GetPixel([int]($w/2),[int]($h/2)),
                $bmp.GetPixel($w-10,10), $bmp.GetPixel(10,$h-10), $bmp.GetPixel($w-10,$h-10)
            )
            $unique = ($samples | ForEach-Object { '{0:X2}{1:X2}{2:X2}' -f $_.R, $_.G, $_.B } | Sort-Object -Unique).Count
            if ($unique -le 2) {
                Write-Warning "Still frame is near-uniform ($unique unique sampled colours). Session was likely locked or WPF didn't paint. Re-run with the desktop unlocked."
            }
        } finally { $bmp.Dispose() }
    } catch { }
}

Write-Host '--- artifacts ---'
Get-Item $mp4, $gif, $png -ErrorAction SilentlyContinue | Select-Object Name, Length, FullName
