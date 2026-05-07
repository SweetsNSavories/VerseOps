param(
    [Parameter(Mandatory=$true)][string]$ProcessName,
    [Parameter(Mandatory=$true)][string]$OutPath
)

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public class WinCap {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    public static int Shoot(IntPtr h, string outPath) {
        if (h == IntPtr.Zero) return -1;
        ShowWindow(h, 5);
        SetForegroundWindow(h);
        System.Threading.Thread.Sleep(900);
        RECT r;
        if (!GetWindowRect(h, out r)) return -2;
        int w = r.Right - r.Left;
        int ht = r.Bottom - r.Top;
        if (w < 100 || ht < 100) return -3;
        Bitmap bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb);
        try {
            Graphics g = Graphics.FromImage(bmp);
            try {
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, ht), CopyPixelOperation.SourceCopy);
                bmp.Save(outPath, ImageFormat.Png);
            } finally { g.Dispose(); }
        } finally { bmp.Dispose(); }
        return 0;
    }
}
"@

$p = Get-Process -Name $ProcessName -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Error "No window found for $ProcessName"; exit 1 }
Write-Host "Capturing pid=$($p.Id) hwnd=$($p.MainWindowHandle.ToInt64()) title='$($p.MainWindowTitle)'"
$rc = [WinCap]::Shoot($p.MainWindowHandle, $OutPath)
Write-Host "rc=$rc → $OutPath"
exit $rc
