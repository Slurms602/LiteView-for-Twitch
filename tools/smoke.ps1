# Starts a built LiteView, checks that its window appears and stays up, then closes it, and saves a screenshot,
# the logs and any Windows error events. CI runs it after build.ps1; it can also be run by hand:
#     tools\smoke.ps1 -Dir dist\LiteView-Enhanced-0.1.0
# It runs a copy of the folder in a temporary directory, so the folder itself (and a real install's settings,
# login and browser profile) is never touched. Results go to -Out (default .\smoke). Exit code 1 = failed.
param(
    [Parameter(Mandatory = $true)] [string]$Dir,
    [string]$Out = 'smoke',
    [int]$Seconds = 30,
    [string[]]$AppArgs = @()
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Out = (New-Item -ItemType Directory -Force $Out).FullName
$src = (Resolve-Path $Dir).Path
$app = Join-Path ([IO.Path]::GetTempPath()) ('liteview-smoke-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
Copy-Item $src $app -Recurse
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

function Tree([int]$id) {   # the process and everything it started (WebView2, Streamlink, mpv)
    $all = @(Get-CimInstance Win32_Process)
    $ids = New-Object System.Collections.Generic.List[int]; $ids.Add($id)
    for ($i = 0; $i -lt $ids.Count; $i++) { foreach ($c in $all) { if ($c.ParentProcessId -eq $ids[$i] -and -not $ids.Contains([int]$c.ProcessId)) { $ids.Add([int]$c.ProcessId) } } }
    $all | Where-Object { $ids.Contains([int]$_.ProcessId) }
}

$t0 = Get-Date
Note "smoke: $exe $($AppArgs -join ' ')"
$start = @{ FilePath = $exe; WorkingDirectory = $app; PassThru = $true }
if ($AppArgs.Count -gt 0) { $start.ArgumentList = $AppArgs }   # Start-Process refuses an empty list
$p = Start-Process @start
$null = $p.Handle   # keeps the exit code readable after the process ends
$firstWindow = $null
while (((Get-Date) - $t0).TotalSeconds -lt $Seconds) {
    Start-Sleep -Milliseconds 500
    $p.Refresh()
    if ($p.HasExited) { break }
    if ($null -eq $firstWindow -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $firstWindow = ((Get-Date) - $t0).TotalSeconds }
}
$p.Refresh()
Screenshot 'screen.png'

$ok = $true
if ($p.HasExited) {
    $ok = $false
    Note ("FAIL: LiteView exited after {0:N1} s with exit code 0x{1:X8}" -f ($p.ExitTime - $t0).TotalSeconds, $p.ExitCode)
} else {
    if ($null -eq $firstWindow) { $ok = $false; Note "FAIL: still running after $Seconds s but has no window" }
    else { Note ("window after {0:N1} s: '{1}'" -f $firstWindow, $p.MainWindowTitle) }
    foreach ($c in Tree $p.Id) { Note ("  process {0} ({1}): {2:N0} MB" -f $c.Name, $c.ProcessId, ($c.WorkingSetSize / 1MB)) }
    # close it the normal way (runs the save-settings and stop-stream code), then make sure nothing is left
    [void]$p.CloseMainWindow()
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
Get-ChildItem $app -File -Filter '*.ini' | Copy-Item -Destination $Out
Note ("logs: " + ((Get-ChildItem $Out -File | Select-Object -ExpandProperty Name) -join ', '))
Note ($(if ($ok) { 'PASS' } else { 'FAIL' }))
$report | Set-Content (Join-Path $Out 'result.txt') -Encoding utf8
Remove-Item $app -Recurse -Force -ErrorAction SilentlyContinue
if (-not $ok) { exit 1 }
