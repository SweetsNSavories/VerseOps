param([int]$ProcId = 0)
if ($ProcId -eq 0) { $ProcId = [int](Get-Content "$env:TEMP\xrm-pid.txt") }
$p = Get-Process -Id $ProcId -ErrorAction Stop
Write-Host "PID=$ProcId HWND=$($p.MainWindowHandle) title='$($p.MainWindowTitle)'"

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder t, int c);
}
"@

[W]::ShowWindow($p.MainWindowHandle, 3) | Out-Null   # SW_MAXIMIZE
Start-Sleep -Milliseconds 500
[W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep 2

$fg = [W]::GetForegroundWindow()
$sb = New-Object System.Text.StringBuilder 256
[W]::GetWindowText($fg, $sb, 256) | Out-Null
Write-Host "Foreground HWND=$fg title='$($sb.ToString())'"

Add-Type -AssemblyName System.Drawing,System.Windows.Forms
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$out = "tools\SmokeTest\out\xrmtoolbox-running.png"
New-Item -ItemType Directory -Path (Split-Path $out) -Force | Out-Null
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Saved $out ($((Get-Item $out).Length) bytes)"
