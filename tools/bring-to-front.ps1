param([Parameter(Mandatory)][long]$Hwnd)
$code = @'
using System;
using System.Runtime.InteropServices;
public class F {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
}
'@
Add-Type -TypeDefinition $code -ErrorAction Stop
$h = [IntPtr]::new($Hwnd)
[F]::ShowWindowAsync($h, 9) | Out-Null   # SW_RESTORE
[F]::BringWindowToTop($h) | Out-Null
[F]::SetForegroundWindow($h) | Out-Null
"brought 0x$($Hwnd.ToString('X')) to foreground"
