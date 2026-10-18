<#
    Watch-ServerRestartEvents.ps1  --  READ-ONLY

    Headless follower for Watch-ServerRestart.ps1: reads the grid's -StateFile JSON every -PollSec and emits one line
    per server state change, plus alarms (DOWN >= 15 min, reboot pending again >= 20 min) and "GRID STOPPED" when the state file goes stale (> 90 s). Exits when every row is BACK clean, unless
    -KeepRunning. Written 2026-10-17 during a restart round so a Claude Code background task gets notified on
    each change while the grid runs in its own window.

    Example
        .\Watch-ServerRestart.ps1 -ListFile servers.txt -Since 2026-10-17T15:00 -StateFile state.json   # window 1
        .\Watch-ServerRestartEvents.ps1 -StateFile state.json                                           # background
#>
param([Parameter(Mandatory)] [string] $StateFile,
      [int] $PollSec = 20,
      [switch] $KeepRunning)
$cleanSaid = $false
# Emits one line per server state change read from Watch-ServerRestart's state file, plus alarms; exits when all are BACK clean.
function Emit($s) { [Console]::Out.WriteLine($s); [Console]::Out.Flush() }
function Mins($span) { if ($span -match '^(\d+)h(\d+)m$') { 60 * [int]$Matches[1] + [int]$Matches[2] } elseif ($span -match '^(\d+)m') { [int]$Matches[1] } else { 0 } }
$prev = @{}; $alarmed = @{}; $staleSaid = $false
while ($true) {
    try { $j = Get-Content $StateFile -Raw -ErrorAction Stop | ConvertFrom-Json } catch { Start-Sleep -Seconds $PollSec; continue }
    $age = ((Get-Date) - [datetime]$j.Updated).TotalSeconds
    if ($age -gt 90 -and -not $staleSaid) { Emit "GRID STOPPED: state file not updated for $([int]$age)s (window closed?)"; $staleSaid = $true }
    if ($age -le 90) { $staleSaid = $false }
    $clean = 0
    foreach ($r in $j.Rows) {
        $note = $r.Note -replace '\(.*\)', ''
        if ($r.State -eq 'BACK' -and $r.Note -in 'servicing finishing', 'component cleanup running') { $note = '' }   # routine idle maintenance
        $key = "$($r.State)|$note"
        if ($r.State -eq 'checking...') { continue }
        if ($r.State -eq 'BACK' -and ($r.Note -eq '' -or $r.Note -in 'servicing finishing', 'component cleanup running')) { $clean++ }
        if ($prev.ContainsKey($r.Name) -and $prev[$r.Name] -ne $key) {
            Emit ("{0:HH:mm} {1}: {2} -> {3}{4}" -f (Get-Date), $r.Name, ($prev[$r.Name] -replace '\|$', ''), $r.State, $(if ($r.Note) { " ($($r.Note))" } else { '' }))
        }
        $prev[$r.Name] = $key
        $m = Mins $r.For
        if ($r.State -eq 'DOWN' -and $m -ge 15 -and -not $alarmed["$($r.Name)|down"]) { Emit "ALARM $($r.Name): DOWN for $($r.For)"; $alarmed["$($r.Name)|down"] = 1 }
        if ($r.Note -like 'reboot pending again*' -and $m -ge 20 -and -not $alarmed["$($r.Name)|pend"]) { Emit "ALARM $($r.Name): back $($r.For), $($r.Note)"; $alarmed["$($r.Name)|pend"] = 1 }
    }
    $all = ($clean -eq @($j.Rows).Count)
    if ($all -and -not $cleanSaid) { Emit "ALL $clean BACK CLEAN at $($j.Updated)"; $cleanSaid = $true; if (-not $KeepRunning) { break } }
    if (-not $all) { $cleanSaid = $false }
    Start-Sleep -Seconds $PollSec
}
