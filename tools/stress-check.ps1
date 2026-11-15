# Reads the "stress:" lines of a chat stress run (LiteView.exe --chat-stress=N, or the CI fixture scene "stress")
# from liteview.log, prints them as a table with CPU per interval, and checks them against a budget:
#   - the generator kept up (messages per second at least 80 % of the rate),
#   - private memory stays under -MaxPrivateMB,
#   - over the last -SteadySamples samples (the steady state) private memory, handles, GDI and USER objects do not
#     grow by more than their budget (a leak grows without end; a working cache levels off),
#   - the window's thread answers within -MaxLagMs (it draws the chat, so a long wait is a visible freeze),
#   - optionally, enough emotes were decoded and none refused (-MinDecoded, -MaxRefused).
# The numbers in the budget come from the first CI runs (docs/PERFORMANCE_BASELINE.md). Exit code 1 = over budget.
#     tools\stress-check.ps1 -Log smoke\fixture-stress\liteview.log -Rate 300
param(
    [Parameter(Mandatory = $true)] [string]$Log,
    [int]$Rate = 300,
    [int]$MinSamples = 10,
    [int]$SteadySamples = 4,
    [int]$MaxPrivateMB = 600,
    [int]$MaxGrowthMB = 60,
    [int]$MaxHandleGrowth = 150,
    [int]$MaxGdiGrowth = 50,
    [int]$MaxUserGrowth = 50,
    [int]$MaxLagMs = 750,
    [int]$MinDecoded = 0,   # emotes decoded at the end (CI: the fixture's made-up 7TV emotes really drew)
    [int]$MaxRefused = -1,  # images refused by the size/format checks (-1: not checked)
    [string]$Out
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$re = 'stress: (\d+) messages \((\d+)/s\), working set (\d+) MB, private (\d+) MB, threads (\d+), handles (\d+), GDI (\d+), USER (\d+), CPU ([0-9.]+) s, UI lag max (\d+) ms, emotes: (.*)$'
$rows = New-Object System.Collections.Generic.List[object]
foreach ($line in Get-Content $Log) {
    $m = [regex]::Match($line, $re)
    if (-not $m.Success) { if ($line -match 'stress phase: (.*)$') { $rows.Add([pscustomobject]@{ Phase = $Matches[1] }) }; continue }
    $g = $m.Groups
    $rows.Add([pscustomobject]@{
        Phase = $null; Messages = [long]$g[1].Value; PerSecond = [int]$g[2].Value; WorkingMB = [int]$g[3].Value; PrivateMB = [int]$g[4].Value
        Threads = [int]$g[5].Value; Handles = [int]$g[6].Value; Gdi = [int]$g[7].Value; User = [int]$g[8].Value; CpuS = [double]::Parse($g[9].Value, [Globalization.CultureInfo]::InvariantCulture)
        LagMs = [int]$g[10].Value; Emotes = $g[11].Value.Trim() })
}
$samples = @($rows | Where-Object { $null -eq $_.Phase })

$report = New-Object System.Collections.Generic.List[string]
function Note([string]$s) { $report.Add($s); Write-Host $s }
$ok = $true
function Fail([string]$s) { $script:ok = $false; Note "FAIL: $s" }

Note ("{0,4} {1,9} {2,6} {3,8} {4,8} {5,7} {6,7} {7,5} {8,5} {9,6} {10,7}  {11}" -f 's', 'messages', 'per s', 'work MB', 'priv MB', 'threads', 'handles', 'GDI', 'USER', 'CPU %', 'lag ms', 'emotes')
$t = 0; $prevCpu = $null
foreach ($r in $rows) {
    if ($null -ne $r.Phase) { Note "     -- $($r.Phase)"; continue }
    $t += 10
    $cpu = if ($null -eq $prevCpu) { '' } else { [string][int][Math]::Round(($r.CpuS - $prevCpu) * 10) }   # % of one core over the 10 s
    $prevCpu = $r.CpuS
    Note ("{0,4} {1,9} {2,6} {3,8} {4,8} {5,7} {6,7} {7,5} {8,5} {9,6} {10,7}  {11}" -f $t, $r.Messages, $r.PerSecond, $r.WorkingMB, $r.PrivateMB, $r.Threads, $r.Handles, $r.Gdi, $r.User, $cpu, $r.LagMs, $r.Emotes)
}

if ($samples.Count -lt $MinSamples) { Fail "only $($samples.Count) samples (need $MinSamples): the run was too short or the stress mode did not start" }
else {
    $run = @($samples | Select-Object -Skip 1)   # the first interval includes the start
    $slow = @($run | Where-Object { $_.PerSecond -lt $Rate * 0.8 })
    if ($slow.Count -gt 0) { Fail "the generator fell behind: $(($slow | ForEach-Object { $_.PerSecond }) -join ', ') messages/s (rate $Rate)" }
    $peak = ($samples | Measure-Object PrivateMB -Maximum).Maximum
    if ($peak -gt $MaxPrivateMB) { Fail "private memory peaked at $peak MB (budget $MaxPrivateMB MB)" } else { Note "private memory peak: $peak MB (budget $MaxPrivateMB MB)" }
    $steady = @($samples | Select-Object -Last $SteadySamples)
    $first = $steady[0]; $last = $steady[-1]
    foreach ($k in @(@('PrivateMB', $MaxGrowthMB, 'MB'), @('Handles', $MaxHandleGrowth, 'handles'), @('Gdi', $MaxGdiGrowth, 'GDI objects'), @('User', $MaxUserGrowth, 'USER objects'))) {
        $grow = $last.($k[0]) - $first.($k[0])
        if ($grow -gt $k[1]) { Fail "$($k[0]) grew by $grow $($k[2]) over the last $SteadySamples samples (budget $($k[1]))" }
        else { Note "$($k[0]) over the last $SteadySamples samples: $($first.($k[0])) -> $($last.($k[0])) (budget +$($k[1]))" }
    }
    $lag = ($run | Measure-Object LagMs -Maximum).Maximum
    $em = [regex]::Match($samples[-1].Emotes, '(\d+) decoded.* (\d+) refused')
    if ($MinDecoded -gt 0 -or $MaxRefused -ge 0) {
        if (-not $em.Success) { Fail "no emote figures in the last sample: $($samples[-1].Emotes)" }
        else {
            $dec = [int]$em.Groups[1].Value; $ref = [int]$em.Groups[2].Value
            if ($dec -lt $MinDecoded) { Fail "only $dec emotes decoded (need $MinDecoded)" } else { Note "emotes decoded: $dec" }
            if ($MaxRefused -ge 0 -and $ref -gt $MaxRefused) { Fail "$ref images refused (budget $MaxRefused)" }
        }
    }
    if ($lag -gt $MaxLagMs) { Fail "the window's thread waited up to $lag ms (budget $MaxLagMs ms)" } else { Note "longest UI wait: $lag ms (budget $MaxLagMs ms)" }
}
Note ($(if ($ok) { 'PASS' } else { 'FAIL' }))
if ($Out) { $report | Set-Content $Out -Encoding utf8 }
if (-not $ok) { exit 1 }
