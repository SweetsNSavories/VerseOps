[CmdletBinding()]
param([string]$ProcessName = 'XrmToolBox')

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$ErrorActionPreference = 'Stop'

$proc = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition `
    ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $proc.Id
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
foreach ($win in $wins) {
    Write-Host ("===== WINDOW '{0}' (class={1}) rect={2}" -f `
        $win.Current.Name, $win.Current.ClassName, $win.Current.BoundingRectangle)

    $textCond = New-Object System.Windows.Automation.PropertyCondition `
        ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), `
        ([System.Windows.Automation.ControlType]::Text)
    $btnCond  = New-Object System.Windows.Automation.PropertyCondition `
        ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), `
        ([System.Windows.Automation.ControlType]::Button)
    $treeCond = New-Object System.Windows.Automation.PropertyCondition `
        ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), `
        ([System.Windows.Automation.ControlType]::Tree)
    $treeItemCond = New-Object System.Windows.Automation.PropertyCondition `
        ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), `
        ([System.Windows.Automation.ControlType]::TreeItem)
    $editCond = New-Object System.Windows.Automation.PropertyCondition `
        ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), `
        ([System.Windows.Automation.ControlType]::Edit)

    $texts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)
    foreach ($t in $texts) {
        $nm = $t.Current.Name
        if ($nm) { Write-Host ("  TEXT : {0}" -f $nm) }
    }

    $btns = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    foreach ($b in $btns) {
        Write-Host ("  BTN  : '{0}' id='{1}' rect={2}" -f $b.Current.Name, $b.Current.AutomationId, $b.Current.BoundingRectangle)
    }

    $trees = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $treeCond)
    foreach ($tr in $trees) {
        Write-Host ("  TREE : id='{0}' rect={1}" -f $tr.Current.AutomationId, $tr.Current.BoundingRectangle)
    }

    $items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $treeItemCond)
    foreach ($i in $items | Select-Object -First 30) {
        Write-Host ("  ITEM : '{0}'" -f $i.Current.Name)
    }

    $edits = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
    foreach ($e in $edits) {
        Write-Host ("  EDIT : id='{0}' rect={1}" -f $e.Current.AutomationId, $e.Current.BoundingRectangle)
    }
}
