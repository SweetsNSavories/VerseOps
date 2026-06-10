param(
    [Parameter(Mandatory=$true)][string]$ProcessName,
    [Parameter(Mandatory=$true)][string]$OutPath
)

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
public class WinCap {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    // Get-Process.MainWindowHandle on WinForms apps often returns the bogus
    // .NET-BroadcastEventWindow handle (empty title, ~0x0 rect). Enumerate top
    // level windows for the PID and pick the largest visible non-empty-title one.
    public static IntPtr FindMainWindow(uint pid) {
        IntPtr best = IntPtr.Zero;
        long bestArea = -1;
        EnumWindows((h, l) => {
            uint wpid; GetWindowThreadProcessId(h, out wpid);
            if (wpid != pid) return true;
            if (!IsWindowVisible(h)) return true;
            int tlen = GetWindowTextLength(h);
            if (tlen < 4) return true;
            RECT r; if (!GetWindowRect(h, out r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static string Title(IntPtr h) {
        int n = GetWindowTextLength(h);
        if (n <= 0) return string.Empty;
        var sb = new StringBuilder(n + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static int Shoot(IntPtr h, string outPath) {
        if (h == IntPtr.Zero) return -1;
        ShowWindow(h, 9); // SW_RESTORE
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

$procs = Get-Process -Name $ProcessName -ErrorAction Stop
if (-not $procs) { Write-Error "No process named $ProcessName"; exit 1 }
$hwnd = [IntPtr]::Zero
$pid2 = 0
foreach ($p in $procs) {
    $h = [WinCap]::FindMainWindow([uint32]$p.Id)
    if ($h -ne [IntPtr]::Zero) { $hwnd = $h; $pid2 = $p.Id; break }
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Error "No visible main window for $ProcessName"; exit 1 }
$title = [WinCap]::Title($hwnd)
Write-Host "Capturing pid=$pid2 hwnd=$($hwnd.ToInt64()) title='$title'"
$rc = [WinCap]::Shoot($hwnd, $OutPath)
Write-Host "rc=$rc -> $OutPath"
exit $rc
