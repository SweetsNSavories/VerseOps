[CmdletBinding()]
param([Parameter(Mandatory=$true)][int]$ProcessId)

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class W32 {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    public delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumChildProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern int  GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L; public int T; public int R; public int B; }
    public static List<IntPtr> TopLevel(int pid) {
        var found = new List<IntPtr>();
        EnumWindows((h, l) => {
            int p; GetWindowThreadProcessId(h, out p);
            if (p == pid) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static List<IntPtr> Children(IntPtr parent) {
        var found = new List<IntPtr>();
        EnumChildWindows(parent, (h, l) => { found.Add(h); return true; }, IntPtr.Zero);
        return found;
    }
    public static string Text(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }
    public static string Class(IntPtr h) { var sb = new StringBuilder(128); GetClassName(h, sb, 128); return sb.ToString(); }
}
"@

$tops = [W32]::TopLevel($ProcessId)
foreach ($h in $tops) {
    $r = New-Object W32+RECT
    [W32]::GetWindowRect($h, [ref]$r) | Out-Null
    $vis = [W32]::IsWindowVisible($h)
    Write-Host ("WIN  hwnd={0} vis={1} class='{2}' text='{3}' rect=({4},{5})-({6},{7})" -f `
        $h.ToInt64(), $vis, [W32]::Class($h), [W32]::Text($h), $r.L, $r.T, $r.R, $r.B)
    foreach ($c in [W32]::Children($h)) {
        $cr = New-Object W32+RECT; [W32]::GetWindowRect($c, [ref]$cr) | Out-Null
        Write-Host ("  child hwnd={0} class='{1}' text='{2}' rect=({3},{4})-({5},{6})" -f `
            $c.ToInt64(), [W32]::Class($c), [W32]::Text($c), $cr.L, $cr.T, $cr.R, $cr.B)
    }
}
