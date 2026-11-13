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
if ($lines.Count -eq 0) { $lines.Add('no UI Automation lists found') }
# MSAA (IAccessible), what NVDA and JAWS read for WinForms windows
Add-Type -ReferencedAssemblies Accessibility -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using Accessibility;
public static class Msaa
{
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr h, uint id, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    public static List<string> Lists(IntPtr top, int max)
    {
        var lines = new List<string>();
        EnumChildWindows(top, (h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71"); object o;
            if (AccessibleObjectFromWindow(h, 0xFFFFFFFC, ref iid, out o) != 0) return true;
            var a = o as IAccessible;
            if (a == null) return true;
            object role = a.get_accRole(0);
            if (!(role is int) || (int)role != 0x21) return true;   // ROLE_SYSTEM_LIST
            lines.Add("MSAA LIST '" + a.get_accName(0) + "' (" + a.accChildCount + " items)");
            for (int i = 1; i <= Math.Min(max, a.accChildCount); i++) lines.Add("  - " + a.get_accName(i));
            return true;
        }, IntPtr.Zero);
        return lines;
    }
}
'@
$msaa = [Msaa]::Lists($p.MainWindowHandle, $MaxItems)
if ($msaa.Count -eq 0) { $lines.Add('no MSAA lists found') } else { foreach ($l in $msaa) { $lines.Add($l) } }
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
