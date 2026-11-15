# Starts a built LiteView, checks that its window appears and stays up, then closes it, and saves a screenshot,
# the logs and any Windows error events. CI runs it after build.ps1; it can also be run by hand:
#     tools\smoke.ps1 -Dir dist\LiteView-Enhanced-0.1.0
# It runs a copy of the folder in a temporary directory, so the folder itself (and a real install's settings,
# login and browser profile) is never touched. Results go to -Out (default .\smoke). Exit code 1 = failed.
param(
    [Parameter(Mandatory = $true)] [string]$Dir,
    [string]$Out = 'smoke',
    [int]$Seconds = 30,
    [string[]]$AppArgs = @(),
    [switch]$A11y,             # also save what screen readers see (tools/a11y-dump.ps1) as a11y.txt
    [string]$FakeStreamlink,   # CI: replace Streamlink in the copy with tests/FakeStreamlink (no Twitch needed)
    [string[]]$Expect = @(),   # regular expressions that must each match a line of liteview.log (what the scene must have done)
    [string[]]$A11yExpect = @(),   # with -A11y: regular expressions that must each match a line of a11y.txt
    [hashtable]$Env = @{}   # e.g. @{ LITEVIEW_UI_FIXTURE = '1'; LITEVIEW_UI_FIXTURE_SCENE = 'followed' } (made-up data, docs/TESTING.md)
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Out = (New-Item -ItemType Directory -Force $Out).FullName
$src = (Resolve-Path $Dir).Path
$app = Join-Path ([IO.Path]::GetTempPath()) ('liteview-smoke-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
Copy-Item $src $app -Recurse
if ($FakeStreamlink)
{
    $sl = Get-ChildItem (Join-Path $app 'twitch\streamlink') -Recurse -Filter streamlinkw.exe | Select-Object -First 1
    Copy-Item $FakeStreamlink $sl.FullName -Force
}
$exe = Join-Path $app 'LiteView.exe'
$report = New-Object System.Collections.Generic.List[string]
function Note([string]$s) { $report.Add($s); Write-Host $s }

function Screenshot([string]$name) {
    try {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $b.Size)
        $bmp.Save((Join-Path $Out $name), [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
    } catch { Note "screenshot failed: $($_.Exception.Message)" }
}

function WindowShot([IntPtr]$h, [string]$name) {   # the window's own content, even where other windows cover it
    try {
        Add-Type -AssemblyName System.Drawing
        if (-not ('Smoke.Win' -as [type])) {
            Add-Type -Namespace Smoke -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, int[] r);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
'@
        }
        $r = New-Object int[] 4
        if (-not [Smoke.Win]::GetWindowRect($h, $r)) { Note "window screenshot: no window rectangle"; return }
        $bmp = New-Object System.Drawing.Bitmap ([Math]::Max(1, $r[2] - $r[0])), ([Math]::Max(1, $r[3] - $r[1]))
        $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
        try { [void][Smoke.Win]::PrintWindow($h, $dc, 2) } finally { $g.ReleaseHdc($dc); $g.Dispose() }   # 2 = PW_RENDERFULLCONTENT (WebView2)
        $bmp.Save((Join-Path $Out $name), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    } catch { Note "window screenshot failed: $($_.Exception.Message)" }
}

# LiteView's own window, found by its title among the process's top-level windows. Not Process.MainWindowHandle:
# that is the first visible top-level window, which can be a tooltip that popped up under the CI mouse.
# GetWindowText does not wait on the window, so this works even when the app does not answer.
if (-not ('Smoke.Top' -as [type])) {
    Add-Type -Namespace Smoke -Name Top -MemberDefinition @'
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
[DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr result);
// the window's thread answers a no-op message within ms (SMTO_ABORTIFHUNG): a busy or hung app must not hang this script
public static bool Responds(IntPtr h, uint ms) { IntPtr r; return SendMessageTimeout(h, 0, IntPtr.Zero, IntPtr.Zero, 0x0002, ms, out r) != IntPtr.Zero; }
public static string Title(IntPtr h) { var sb = new System.Text.StringBuilder(512); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }
public static IntPtr Find(int pid, string prefix)
{
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) =>
    {
        uint p; GetWindowThreadProcessId(h, out p);
        if (p == (uint)pid && IsWindowVisible(h) && Title(h).StartsWith(prefix, StringComparison.Ordinal)) { found = h; return false; }
        return true;
    }, IntPtr.Zero);
    return found;
}
'@
}
$main = [IntPtr]::Zero

function Tree([int]$id) {   # the process and everything it started (WebView2, Streamlink, mpv)
    $all = @(Get-CimInstance Win32_Process)
    $ids = New-Object System.Collections.Generic.List[int]; $ids.Add($id)
    for ($i = 0; $i -lt $ids.Count; $i++) { foreach ($c in $all) { if ($c.ParentProcessId -eq $ids[$i] -and -not $ids.Contains([int]$c.ProcessId)) { $ids.Add([int]$c.ProcessId) } } }
    $all | Where-Object { $ids.Contains([int]$_.ProcessId) }
}

$t0 = Get-Date
Note "smoke: $exe $($AppArgs -join ' ') $(($Env.Keys | ForEach-Object { "$_=$($Env[$_])" }) -join ' ')"
$start = @{ FilePath = $exe; WorkingDirectory = $app; PassThru = $true }
if ($AppArgs.Count -gt 0) { $start.ArgumentList = $AppArgs }   # Start-Process refuses an empty list
$saved = @{}
foreach ($k in $Env.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, [string]$Env[$k]) }
try { $p = Start-Process @start }
finally { foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
$null = $p.Handle   # keeps the exit code readable after the process ends
$firstWindow = $null
while (((Get-Date) - $t0).TotalSeconds -lt $Seconds) {
    Start-Sleep -Milliseconds 500
    $p.Refresh()
    if ($p.HasExited) { break }
    # the main window, not an error message box (title "LiteView")
    if ($null -eq $firstWindow) {
        $main = [Smoke.Top]::Find($p.Id, 'LiteView for Twitch')
        if ($main -ne [IntPtr]::Zero) { $firstWindow = ((Get-Date) - $t0).TotalSeconds }
    }
}
$p.Refresh()
Screenshot 'screen.png'

$ok = $true
if ($p.HasExited) {
    $ok = $false
    Note ("FAIL: LiteView exited after {0:N1} s with exit code 0x{1:X8}" -f ($p.ExitTime - $t0).TotalSeconds, $p.ExitCode)
} else {
    if ($null -eq $firstWindow) { $ok = $false; Note "FAIL: still running after $Seconds s without its main window (top window: '$($p.MainWindowTitle)')" }
    else
    {
        Note ("window after {0:N1} s: '{1}'" -f $firstWindow, [Smoke.Top]::Title($main))
        # PrintWindow and screen readers wait for the window's thread: only when it answers
        $responds = [Smoke.Top]::Responds($main, 5000)
        if (-not $responds) { $ok = $false; Note "FAIL: the window does not respond (its thread was busy for more than 5 s)" }
        else { WindowShot $main 'window.png' }
        if ($A11y -and $responds)
        {
            $dump = Join-Path $Out 'a11y.txt'
            $dumper = Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                ('"' + (Join-Path $PSScriptRoot 'a11y-dump.ps1') + '"'), '-ProcessId', $p.Id, '-Window', $main.ToInt64(), '-Out', ('"' + $dump + '"'))
            if (-not $dumper.WaitForExit(60000)) { Stop-Process -Id $dumper.Id -Force -ErrorAction SilentlyContinue; Note "screen reader dump: no answer within 60 s" }
            if (-not (Test-Path $dump)) { Set-Content $dump 'no dump (tools/a11y-dump.ps1 did not finish)' }
            $uia = Select-String -Path $dump -Pattern '^LIST .*\([1-9][0-9]* items\)' -Quiet         # Narrator
            $msaa = Select-String -Path $dump -Pattern '^MSAA LIST .*\([1-9][0-9]* items\)' -Quiet   # NVDA, JAWS
            if (-not ($uia -and $msaa)) { $ok = $false; Note "FAIL: screen readers see no list items (UI Automation: $uia, MSAA: $msaa; a11y.txt)" }
            else { Note "screen readers see the lists (UI Automation and MSAA; a11y.txt)" }
            foreach ($x in $A11yExpect) {
                if (Select-String -Path $dump -Pattern $x -Quiet) { Note "a11y has: $x" } else { $ok = $false; Note "FAIL: a11y.txt has no line matching: $x" }
            }
        }
    }
    foreach ($c in Tree $p.Id) { Note ("  process {0} ({1}): {2:N0} MB" -f $c.Name, $c.ProcessId, ($c.WorkingSetSize / 1MB)) }
    # close it the normal way (runs the save-settings and stop-stream code), then make sure nothing is left
    if ($main -ne [IntPtr]::Zero) { [void][Smoke.Top]::PostMessage($main, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }   # WM_CLOSE to LiteView's window
    else { [void]$p.CloseMainWindow() }
    if ($p.WaitForExit(10000)) { Note "closed normally, exit code $($p.ExitCode)" }
    else { $ok = $false; Note "FAIL: did not close within 10 s of the close request" }
    Start-Sleep -Seconds 2
    $left = @(Tree $p.Id | Where-Object { $_.ProcessId -ne $p.Id })
    foreach ($c in $left) { Note "  left running after close: $($c.Name) ($($c.ProcessId))" }
    foreach ($c in @(Tree $p.Id)) { Stop-Process -Id $c.ProcessId -Force -ErrorAction SilentlyContinue }
}

# Windows' own record of crashes and hangs (a .NET crash before LiteView's log is open only shows up here)
try {
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $t0.AddSeconds(-5) } -ErrorAction Stop |
        Where-Object { $_.ProviderName -in '.NET Runtime', 'Application Error', 'Application Hang', 'SideBySide', 'Windows Error Reporting' })
    $events | Format-List TimeCreated, ProviderName, Id, Message | Out-File (Join-Path $Out 'events.txt') -Width 400
    Note "$($events.Count) Windows error event(s) (events.txt)"
} catch { Note "no Windows error events ($($_.Exception.Message))" }

Get-ChildItem $app -File -Filter '*.log*' | Copy-Item -Destination $Out
$log = Join-Path $app 'liteview.log'
if (-not (Test-Path $log)) { $ok = $false; Note "FAIL: no liteview.log" }
else {
    $errors = @(Select-String -Path $log -Pattern ' ERROR ' -SimpleMatch | Select-Object -First 5)
    if ($errors.Count -gt 0) { $ok = $false; Note "FAIL: errors in liteview.log:"; foreach ($e in $errors) { Note ("  " + $e.Line) } }
    foreach ($x in $Expect) {
        if (Select-String -Path $log -Pattern $x -Quiet) { Note "log has: $x" } else { $ok = $false; Note "FAIL: liteview.log has no line matching: $x" }
    }
}
Get-ChildItem $app -File -Filter '*.ini' | Copy-Item -Destination $Out
Note ("logs: " + ((Get-ChildItem $Out -File | Select-Object -ExpandProperty Name) -join ', '))
Note ($(if ($ok) { 'PASS' } else { 'FAIL' }))
$report | Set-Content (Join-Path $Out 'result.txt') -Encoding utf8
Remove-Item $app -Recurse -Force -ErrorAction SilentlyContinue
if (-not $ok) { exit 1 }
