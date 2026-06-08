param([int]$ProcId = 0, [string]$Out = "tools\SmokeTest\out\xrmtoolbox-verseops-ui2.png")
if ($ProcId -eq 0) { $ProcId = [int](Get-Content "$env:TEMP\xrm-pid.txt") }
$p = Get-Process -Id $ProcId -ErrorAction Stop

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool alt);
}
public class M {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  public const uint LD = 0x0002;
  public const uint LU = 0x0004;
}
"@

[W]::ShowWindow($p.MainWindowHandle, 3) | Out-Null
Start-Sleep -Milliseconds 300
[W]::BringWindowToTop($p.MainWindowHandle) | Out-Null
[W]::SwitchToThisWindow($p.MainWindowHandle, $true)
[W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep 1

Add-Type -AssemblyName System.Windows.Forms

# Click Cancel button on the connection dialog (approx coords from prior screenshot).
[M]::SetCursorPos(940, 721) | Out-Null
Start-Sleep -Milliseconds 200
[M]::mouse_event([M]::LD, 0, 0, 0, [UIntPtr]::Zero)
[M]::mouse_event([M]::LU, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep 2

Add-Type -AssemblyName System.Drawing
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
New-Item -ItemType Directory -Path (Split-Path $Out) -Force | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Saved $Out ($((Get-Item $Out).Length) bytes)"
