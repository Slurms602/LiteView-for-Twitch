# Lists what screen readers see in a running LiteView (UI Automation, the interface Narrator / NVDA use):
# each List and its first ListItems with their names. Used by tools/smoke.ps1 -A11y; Windows PowerShell 5.1
# (UI Automation is part of .NET Framework).   usage: powershell -File tools\a11y-dump.ps1 -ProcessId <pid> -Out a11y.txt
param([Parameter(Mandatory = $true)] [int]$ProcessId, [string]$Out = 'a11y.txt', [int]$MaxItems = 8)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$p = Get-Process -Id $ProcessId
$root = $A::FromHandle($p.MainWindowHandle)
$lines = New-Object System.Collections.Generic.List[string]
$listCond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::List)
foreach ($list in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCond))
{
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    $lines.Add("LIST '" + $list.Current.Name + "' (" + $items.Count + " items)")
    $i = 0
    foreach ($it in $items) { if ($i++ -ge $MaxItems) { break }; $lines.Add("  - " + $it.Current.Name) }
}
if ($lines.Count -eq 0) { $lines.Add('no lists found') }
# the raw tree (what is there at all), a few levels deep
$walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
function Walk($e, [int]$depth)
{
    if ($depth -gt 7 -or $script:count -gt 300) { return }
    $script:count++
    $c = $e.Current
    $lines.Add(('  ' * $depth) + $c.ControlType.ProgrammaticName + " '" + $c.Name + "' [" + $c.ClassName + "] " + $c.FrameworkId)
    $ch = $walker.GetFirstChild($e)
    while ($ch -ne $null) { Walk $ch ($depth + 1); $ch = $walker.GetNextSibling($ch) }
}
$script:count = 0
$lines.Add('--- raw tree')
Walk $root 0
$lines | Set-Content $Out -Encoding utf8
$lines | ForEach-Object { Write-Host $_ }
