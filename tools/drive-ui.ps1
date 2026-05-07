# tools/drive-ui.ps1
#
# Drives the VerseOps WPF window via UI Automation:
#   list                       -- dump every Button + Expander reachable from the
#                                 main window with Name, AutomationId, BoundingRect.
#   click   -Name <text>       -- invoke the first Button whose Name contains <text>
#                                 (case-insensitive).
#   expand  -Index <n>         -- expand the n-th DataGrid row chevron (the per-row
#                                 expand toggle that opens the RowDetailsTemplate).
#   close-drawer               -- press Escape.
#
# All commands target the first window owned by VerseOps.App.

[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [ValidateSet('list', 'click', 'expand', 'close-drawer', 'screenshot')]
    [string] $Command,

    [string] $Name,
    [int]    $Index,
    [string] $Out
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-MainWindow {
    $proc = Get-Process -Name 'VerseOps.App' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { throw 'VerseOps.App is not running' }
    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq 0) { throw 'no main window handle yet' }
    return [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
}

function Find-AllByControlType {
    param([System.Windows.Automation.AutomationElement] $root, $controlType)
    $cond = New-Object System.Windows.Automation.PropertyCondition `
        ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), $controlType
    return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

switch ($Command) {

    'list' {
        $win = Get-MainWindow
        $btns = Find-AllByControlType $win ([System.Windows.Automation.ControlType]::Button)
        Write-Host "=== Buttons ($($btns.Count)) ==="
        foreach ($b in $btns) {
            $info = $b.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::BoundingRectangleProperty)
            Write-Host ("  '{0}' id='{1}' rect={2}" -f $b.Current.Name, $b.Current.AutomationId, $info)
        }
        $exps = Find-AllByControlType $win ([System.Windows.Automation.ControlType]::Custom)
        Write-Host "=== Custom (first 20) ==="
        $i = 0
        foreach ($c in $exps) {
            if ($i -ge 20) { break }
            $i++
            Write-Host ("  '{0}' id='{1}' class='{2}'" -f $c.Current.Name, $c.Current.AutomationId, $c.Current.ClassName)
        }
        $tabs = Find-AllByControlType $win ([System.Windows.Automation.ControlType]::DataItem)
        Write-Host "=== DataItems ($($tabs.Count) -- first 5) ==="
        $i = 0
        foreach ($t in $tabs) {
            if ($i -ge 5) { break }
            $i++
            Write-Host ("  '{0}' id='{1}'" -f $t.Current.Name, $t.Current.AutomationId)
        }
    }

    'click' {
        if (-not $Name) { throw 'click requires -Name' }
        $win = Get-MainWindow
        $btns = Find-AllByControlType $win ([System.Windows.Automation.ControlType]::Button)
        $match = $null
        foreach ($b in $btns) {
            if ($b.Current.Name -and $b.Current.Name.ToLower().Contains($Name.ToLower())) { $match = $b; break }
        }
        if (-not $match) { throw "no button name contains '$Name'" }
        Write-Host "clicking: '$($match.Current.Name)' rect=$($match.Current.BoundingRectangle)"
        $invoke = $match.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $invoke.Invoke()
        Start-Sleep -Milliseconds 600
    }

    'expand' {
        if (-not $Index) { $Index = 0 }
        $win = Get-MainWindow
        # DataGrid rows expose IsExpandable via the per-row expand toggle button
        # (the chevron). Find Buttons whose name is empty AND that have a small
        # square bounding rect at the very left of a row.
        $btns = Find-AllByControlType $win ([System.Windows.Automation.ControlType]::Button)
        $candidates = @()
        foreach ($b in $btns) {
            $r = $b.Current.BoundingRectangle
            if ($r.Width -ge 18 -and $r.Width -le 50 -and $r.Height -ge 18 -and $r.Height -le 50 -and $r.X -lt 80 -and $r.Y -gt 280) {
                $candidates += $b
            }
        }
        Write-Host "found $($candidates.Count) row chevron candidates"
        if ($Index -ge $candidates.Count) { throw "Index $Index out of range" }
        $sorted = $candidates | Sort-Object { $_.Current.BoundingRectangle.Y }
        $target = $sorted[$Index]
        Write-Host "clicking chevron index=$Index rect=$($target.Current.BoundingRectangle)"
        $invoke = $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $invoke.Invoke()
        Start-Sleep -Milliseconds 1500
    }

    'close-drawer' {
        # The drawer overlay is dismissed by clicking the dimming backdrop
        # (the transparent button bound to CloseDrawerCommand that fills
        # the whole window outside the drawer panel). SendKeys ESC was
        # unreliable here, so we synthesize a real mouse click at a
        # safe spot on the left-hand side.
        Add-Type -Namespace W -Name U -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT rect);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, System.UIntPtr dwExtraInfo);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }
"@
        $hwnd = [System.IntPtr](Get-Process -Name 'VerseOps.App' | Select-Object -First 1).MainWindowHandle
        [W.U]::ShowWindow($hwnd, 9) | Out-Null  # SW_RESTORE
        [W.U]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 300
        $rect = New-Object W.U+RECT
        [W.U]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
        # Click at (200, 800) inside the window, well outside drawer (which is on the right)
        $cx = $rect.Left + 200
        $cy = $rect.Top + 800
        [W.U]::SetCursorPos($cx, $cy) | Out-Null
        Start-Sleep -Milliseconds 100
        [W.U]::mouse_event(0x0002, 0, 0, 0, [System.UIntPtr]::Zero) | Out-Null  # LEFTDOWN
        [W.U]::mouse_event(0x0004, 0, 0, 0, [System.UIntPtr]::Zero) | Out-Null  # LEFTUP
        Write-Host "clicked overlay at ($cx,$cy)"
        Start-Sleep -Milliseconds 800
    }

    'screenshot' {
        if (-not $Out) { throw 'screenshot requires -Out' }
        & "$PSScriptRoot\capture-window.ps1" -ProcessName VerseOps.App -Out $Out
    }
}
