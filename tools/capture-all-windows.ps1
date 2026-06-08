[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ProcessName,
    [Parameter(Mandatory=$true)][string]$OutPath
)

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public class RegionCap {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    public static int Shoot(IntPtr h, string outPath) {
        if (h == IntPtr.Zero) return -1;
        ShowWindow(h, 5);
        SetForegroundWindow(h);
        System.Threading.Thread.Sleep(500);
        RECT r;
        if (!GetWindowRect(h, out r)) return -2;
        int w = r.Right - r.Left;
        int ht = r.Bottom - r.Top;
        if (w <= 0 || ht <= 0) return -3;
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

# Walk top-level windows for the process and shoot every one.
$proc = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition `
    ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $proc.Id
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
$i = 0
foreach ($w in $wins) {
    $hwnd = [IntPtr]($w.Current.NativeWindowHandle)
    $name = $w.Current.Name
    Write-Host "Shooting '$name' hwnd=$($hwnd.ToInt64())"
    $stem = if ($name) { ($name -replace '[^a-zA-Z0-9]+','_').Substring(0, [Math]::Min(40, $name.Length)) } else { "untitled" }
    $path = $OutPath -replace '\.png$', ('_{0}_{1}.png' -f $i, $stem)
    $rc = [RegionCap]::Shoot($hwnd, $path)
    Write-Host " -> rc=$rc -> $path"
    $i++
}
