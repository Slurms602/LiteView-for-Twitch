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
$lines | Set-Content $Out -Encoding utf8
$lines | ForEach-Object { Write-Host $_ }
