# tools/open-verseops-tile.ps1
#
# Find the "VerseOps" tile in XrmToolBox's tool chooser and invoke it,
# so a screenshot can verify the loaded plugin UI without manual clicking.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process -Name 'XrmToolBox' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw 'XrmToolBox is not running' }

# Wait for hwnd
$hwnd = 0
for ($i = 0; $i -lt 40; $i++) {
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0) { $hwnd = $proc.MainWindowHandle; break }
    Start-Sleep -Milliseconds 250
}
if ($hwnd -eq 0) { throw 'no main window handle' }

$root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$hwnd)

# The tool chooser has a search box (Edit control) at the top — typing
# "VerseOps" filters tiles. Then we double-click the first matching tile.

$editCond = New-Object System.Windows.Automation.PropertyCondition `
    ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), `
    ([System.Windows.Automation.ControlType]::Edit)
$edits = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
Write-Host "Edits found: $($edits.Count)"
$searchBox = $null
foreach ($e in $edits) {
    $rect = $e.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::BoundingRectangleProperty)
    Write-Host "  Edit name='$($e.Current.Name)' id='$($e.Current.AutomationId)' rect=$rect"
    if ($null -eq $searchBox -and $rect.Width -gt 200) { $searchBox = $e }
}
if (-not $searchBox) { throw 'no search edit found' }

$valuePattern = $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$valuePattern.SetValue('VerseOps')
Start-Sleep -Milliseconds 400

# Now find the VerseOps tile. Tiles appear to be ListItem or Custom controls
# with name "VerseOps". Scan all descendants for one with name containing "VerseOps".
$allCond = New-Object System.Windows.Automation.PropertyCondition `
    ([System.Windows.Automation.AutomationElement]::IsControlElementProperty), $true
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $allCond)
Write-Host "Descendants: $($all.Count)"
$verseTile = $null
foreach ($el in $all) {
    $nm = $el.Current.Name
    if ($nm -and $nm.Contains('VerseOps')) {
        $ct = $el.Current.ControlType.LocalizedControlType
        $rect = $el.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::BoundingRectangleProperty)
        Write-Host "  match name='$nm' type=$ct rect=$rect"
        if ($null -eq $verseTile -and $ct -ne 'edit') { $verseTile = $el }
    }
}
if (-not $verseTile) { throw 'no VerseOps tile found' }

# Try Invoke pattern; fallback to double-click via mouse.
$rect = $verseTile.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::BoundingRectangleProperty)
$cx = [int]($rect.X + $rect.Width / 2)
$cy = [int]($rect.Y + $rect.Height / 2)
Write-Host "Tile center: ($cx,$cy)"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Mouse {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
  public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
  public const uint MOUSEEVENTF_LEFTUP = 0x0004;
  public static void DoubleClickAt(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(60);
    mouse_event(MOUSEEVENTF_LEFTDOWN, 0,0,0,IntPtr.Zero); mouse_event(MOUSEEVENTF_LEFTUP,0,0,0,IntPtr.Zero);
    System.Threading.Thread.Sleep(60);
    mouse_event(MOUSEEVENTF_LEFTDOWN, 0,0,0,IntPtr.Zero); mouse_event(MOUSEEVENTF_LEFTUP,0,0,0,IntPtr.Zero);
  }
}
'@

[Mouse]::DoubleClickAt($cx, $cy)
Start-Sleep -Milliseconds 1500
Write-Host "Done."
