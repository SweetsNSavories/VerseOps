param([int]$RootPid)
$code = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class W {
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public static List<string> ForPids(uint[] pids) {
    var hs = new HashSet<uint>(pids); var r = new List<string>();
    EnumWindows((h,l)=>{ uint p; GetWindowThreadProcessId(h, out p); if (hs.Contains(p)) {
        var t = new StringBuilder(256); GetWindowText(h, t, 256);
        var c = new StringBuilder(256); GetClassName(h, c, 256);
        r.Add(string.Format("pid={0} hwnd=0x{1:X} vis={2} class='{3}' title='{4}'", p, h.ToInt64(), IsWindowVisible(h), c, t));
    } return true; }, IntPtr.Zero);
    return r;
  }
}
'@
Add-Type -TypeDefinition $code -ErrorAction Stop
$me = Get-Process -Id $RootPid -ErrorAction SilentlyContinue
if (-not $me) { 'root pid gone'; exit 0 }
$kids = Get-CimInstance Win32_Process -Filter "ParentProcessId=$RootPid" | Select-Object ProcessId, Name, CommandLine
"--- Root: PID=$($me.Id) Name=$($me.ProcessName) WS=$([math]::Round($me.WS/1MB,1))MB ---"
"--- Children ---"
$kids | Format-Table -AutoSize | Out-String | Write-Host
$ids = New-Object 'System.Collections.Generic.List[uint32]'
$ids.Add([uint32]$RootPid)
foreach ($k in $kids) { $ids.Add([uint32]$k.ProcessId) }
"--- All top-level windows for these PIDs ---"
[W]::ForPids($ids.ToArray()) | ForEach-Object { $_ }
