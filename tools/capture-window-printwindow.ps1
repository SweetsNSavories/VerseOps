param(
    [Parameter(Mandatory=$true)][string]$ProcessName,
    [Parameter(Mandatory=$true)][string]$OutPath
)

# PrintWindow-based capture. Unlike capture-window.ps1 (CopyFromScreen),
# this reads the window's actual back-buffer via PrintWindow with
# PW_RENDERFULLCONTENT (0x2). Works even when the window is obscured,
# minimised(*), or the user session is locked — as long as the process
# is alive and has an HWND.
#
# (*) WPF windows in WindowState=Minimized may render at 0x0; the script
#     calls ShowWindowAsync(SW_RESTORE) first to coax the layout pass.

Add-Type -ReferencedAssemblies @('System.Drawing') -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public class PrintWinCap {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr hdc);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    public const uint PW_RENDERFULLCONTENT = 0x00000002;
    public const int SW_RESTORE = 9;

    public static int Shoot(IntPtr hwnd, string outPath) {
        if (hwnd == IntPtr.Zero) return -1;
        ShowWindowAsync(hwnd, SW_RESTORE);
        System.Threading.Thread.Sleep(200);

        RECT r;
        if (!GetWindowRect(hwnd, out r)) return -2;
        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        if (w < 50 || h < 50) return -3;

        using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
            using (Graphics g = Graphics.FromImage(bmp)) {
                IntPtr hdc = g.GetHdc();
                try {
                    bool ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                    if (!ok) return -4;
                } finally { g.ReleaseHdc(hdc); }
            }
            bmp.Save(outPath, ImageFormat.Png);
        }
        return 0;
    }
}
"@

$p = Get-Process -Name $ProcessName -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Error "No window found for $ProcessName"; exit 1 }
Write-Host "Capturing pid=$($p.Id) hwnd=$($p.MainWindowHandle.ToInt64()) title='$($p.MainWindowTitle)'"
$rc = [PrintWinCap]::Shoot($p.MainWindowHandle, $OutPath)
Write-Host "rc=$rc -> $OutPath"
exit $rc
