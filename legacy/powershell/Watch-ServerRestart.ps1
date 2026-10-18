<#
    Watch-ServerRestart.ps1  --  READ-ONLY

    Live console grid for servers being restarted by hand. One background runspace per server pings it and, when it
    answers, reads over DCOM (WinRM is closed): last boot, whether TiWorker.exe (servicing) is running and the Windows
    reboot flags (CBS RebootPending, WU RebootRequired). The grid redraws every -RefreshSec; q quits.
    Written 2026-10-17 for a 54-server restart round. -StateFile writes the grid as JSON on every redraw so another
    session can read it.

    State column
        NOT RESTARTED   answers, booted before -Since, no servicing running
        INSTALLING      answers, booted before -Since, TiWorker running (updates being applied before shutdown)
        DOWN            no ping: rebooting, or dead (watch the timer)
        BOOTING         ping answers, WMI not ready yet
        BACK            booted after -Since; flagged yellow if a reboot is pending again

    CBS creates RebootPending as a "reboot mark" while the StartComponentCleanup idle task runs (CbsTask deep clean, seen
    17-10 right after the updates) and clears it at the end of each pass. So a CBS reboot only counts when
    PackagesPending has entries; the bare mark shows as "component cleanup running".

    -ListFile is re-read on every redraw: servers added to it (one per line or comma-separated, \\ prefixes fine) join the
    grid without a restart; servers removed from it drop off the display.

    Example
        .\Watch-ServerRestart.ps1 -ComputerName SRV01,SRV02 -Since 2026-10-17T15:00
        Start-Process powershell -ArgumentList '-NoExit','-File','D:\...\Watch-ServerRestart.ps1','-ListFile','D:\...\servers.txt'
#>
[CmdletBinding()]
param(
    [string[]] $ComputerName,
    [string] $ListFile,
    [datetime] $Since = (Get-Date).Date,
    [int] $RefreshSec = 5,
    [string] $StateFile
)
$state = [hashtable]::Synchronized(@{})
$ctl = [hashtable]::Synchronized(@{ Stop = $false })

$worker = {
    param($h, $e, $ctl, $pause)
    $ping = New-Object System.Net.NetworkInformation.Ping; $s = $null; $HKLM = [uint32]2147483650
    $cbs = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing'
    function Get-SubKeys($path) {
        @((Invoke-CimMethod -CimSession $s -Namespace root\default -ClassName StdRegProv -MethodName EnumKey -ErrorAction Stop `
           -Arguments @{ hDefKey = $HKLM; sSubKeyName = $path }).sNames | Where-Object { $_ })
    }
    while (-not $ctl.Stop) {
        $up = $false
        try { $up = ($ping.Send($h, 1000).Status -eq 'Success') } catch {}
        $e.Ping = $up
        if ($up) {
            $e.DownSince = $null
            try {
                if (-not $s) { $s = New-CimSession -ComputerName $h -SessionOption (New-CimSessionOption -Protocol Dcom) -OperationTimeoutSec 10 -ErrorAction Stop }
                $e.LastBoot = (Get-CimInstance -CimSession $s -ClassName Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime
                $e.Ti = [bool](Get-CimInstance -CimSession $s -ClassName Win32_Process -Filter "Name='TiWorker.exe'" -ErrorAction Stop)
                $f = @(); $ck = Get-SubKeys $cbs
                $pkgs = if ($ck -contains 'PackagesPending') { (Get-SubKeys "$cbs\PackagesPending").Count } else { 0 }
                if ($ck -contains 'RebootPending' -and $pkgs) { $f += "CBS $pkgs pkgs" }
                $e.Cleanup = ($ck -contains 'RebootPending' -and -not $pkgs)
                if ((Get-SubKeys 'SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update') -contains 'RebootRequired') { $f += 'WU' }
                $e.Pend = $f -join '+'
                $e.Wmi = 'ok'
            } catch {
                $e.Wmi = $_.Exception.Message.Split("`n")[0]
                if ($s) { Remove-CimSession $s -ErrorAction SilentlyContinue; $s = $null }
            }
        } else {
            if (-not $e.DownSince) { $e.DownSince = Get-Date }
            $e.Wmi = ''
            if ($s) { Remove-CimSession $s -ErrorAction SilentlyContinue; $s = $null }
        }
        for ($i = 0; $i -lt $pause -and -not $ctl.Stop; $i++) { Start-Sleep -Seconds 1 }
    }
}

function Format-Span([timespan] $t) {
    if ($t.TotalHours -ge 1) { '{0}h{1:00}m' -f [int][math]::Floor($t.TotalHours), $t.Minutes } else { '{0}m{1:00}s' -f $t.Minutes, $t.Seconds }
}

function Get-Row([string] $h) {
    $e = $state[$h]; $now = Get-Date; $for = ''; $note = ''
    if ($null -eq $e.Ping -or ($e.Ping -and $e.Wmi -eq '')) { $st = 'checking...'; $c = 'DarkGray' }   # no WMI answer yet since (re)start or since it came back
    elseif (-not $e.Ping)            { $st = 'DOWN';          $c = 'Red'; $for = Format-Span ($now - $e.DownSince) }
    elseif ($e.Wmi -ne 'ok')         { $st = 'BOOTING';       $c = 'Yellow'; $note = $e.Wmi }
    elseif ($e.LastBoot -lt $Since)  { if ($e.Ti) { $st = 'INSTALLING'; $c = 'Cyan' } else { $st = 'NOT RESTARTED'; $c = 'Magenta' } }
    else {
        $for = Format-Span ($now - $e.LastBoot)
        if ($e.Pend)                       { $st = 'BACK'; $note = "reboot pending again ($($e.Pend))"; $c = 'Yellow' }
        else                               { $st = 'BACK'; $c = 'Green' }
        if ($e.Cleanup -and $c -eq 'Green') { $note = 'component cleanup running' }
        elseif ($e.Ti -and $c -eq 'Green') { $note = 'servicing finishing' }
    }
    [pscustomobject]@{ Name = $h; Ping = $(if ($e.Ping) { 'up' } elseif ($null -eq $e.Ping) { '' } else { '--' })
                       State = $st; LastBoot = $(if ($e.LastBoot) { $e.LastBoot.ToString('dd-MM HH:mm') } else { '' })
                       For = $for; TiWorker = $(if ($e.Ti -and $e.Ping) { 'yes' } else { '' }); Note = $note; Color = $c }
}

$pool = [runspacefactory]::CreateRunspacePool(1, 256); $pool.Open()   # one long-lived worker per server
$jobs = New-Object System.Collections.ArrayList
$names = @()
function Sync-Names {
    # powershell -File passes a,b as one string, hence the extra split
    $want = @(@($ComputerName) + @(if ($ListFile -and (Test-Path $ListFile)) { Get-Content $ListFile }) | ForEach-Object { "$_" -split ',' } |
              ForEach-Object { $_.Trim().TrimStart('\') } | Where-Object { $_ } | Sort-Object -Unique)
    foreach ($h in $want) {
        if ($state.ContainsKey($h)) { continue }
        $state[$h] = [hashtable]::Synchronized(@{ Ping = $null; Wmi = ''; LastBoot = $null; Ti = $false; Pend = ''; Cleanup = $false; DownSince = $null })
        $ps = [powershell]::Create().AddScript($worker).AddArgument($h).AddArgument($state[$h]).AddArgument($ctl).AddArgument($RefreshSec)
        $ps.RunspacePool = $pool
        [void]$jobs.Add([pscustomobject]@{ PS = $ps; Handle = $ps.BeginInvoke() })
    }
    $script:names = $want
    try { $Host.UI.RawUI.WindowTitle = "Server restart watch ($($want.Count) servers)" } catch {}
}
Sync-Names
if (-not $names.Count) { throw 'No servers: pass -ComputerName and/or -ListFile' }
try { $Host.UI.RawUI.BufferSize = New-Object Management.Automation.Host.Size(150, 3000); $Host.UI.RawUI.WindowSize = New-Object Management.Automation.Host.Size(150, [math]::Min(60, $names.Count + 8)) } catch {}
Clear-Host
$fmt = '{0,-15} {1,-4} {2,-14} {3,-11} {4,-8} {5,-8} {6}'
$quit = $false; $shown = 0
try {
    while (-not $quit) {
        Sync-Names
        $w = 140; try { $w = [math]::Max(80, $Host.UI.RawUI.WindowSize.Width - 1) } catch {}
        $rows = @($names | ForEach-Object { Get-Row $_ })
        $back = @($rows | Where-Object { $_.State -eq 'BACK' -and $_.Color -eq 'Green' }).Count
        $warn = @($rows | Where-Object { $_.State -eq 'BACK' -and $_.Color -eq 'Yellow' }).Count
        $sum = ($rows | Where-Object { $_.State -ne 'BACK' } | Group-Object State | ForEach-Object { "$($_.Name) $($_.Count)" }) -join '   '
        [Console]::SetCursorPosition(0, 0)
        Write-Host ("Server restart watch   {0}   since {1:dd-MM HH:mm}   refresh {2}s   q = quit" -f (Get-Date -Format 'HH:mm:ss'), $Since, $RefreshSec).PadRight($w) -ForegroundColor White
        Write-Host ("BACK ok $back   BACK flagged $warn   $sum").PadRight($w) -ForegroundColor White
        Write-Host ''.PadRight($w)
        Write-Host ($fmt -f 'Server', 'Ping', 'State', 'LastBoot', 'Up/Down', 'TiWorker', 'Note').PadRight($w) -ForegroundColor Gray
        foreach ($r in $rows) {
            $line = $fmt -f $r.Name, $r.Ping, $r.State, $r.LastBoot, $r.For, $r.TiWorker, $r.Note
            if ($line.Length -gt $w) { $line = $line.Substring(0, $w) }
            Write-Host $line.PadRight($w) -ForegroundColor $r.Color
        }
        for ($i = $rows.Count; $i -le $shown; $i++) { Write-Host ''.PadRight($w) }   # blank rows left by servers removed from -ListFile
        $shown = $rows.Count
        if ($StateFile) {
            try { [pscustomobject]@{ Updated = (Get-Date -Format 's'); Since = $Since.ToString('s'); Rows = @($rows | Select-Object * -ExcludeProperty Color) } |
                  ConvertTo-Json -Depth 3 | Set-Content -Path $StateFile -Encoding UTF8 } catch {}
        }
        $until = (Get-Date).AddSeconds($RefreshSec)
        while ((Get-Date) -lt $until -and -not $quit) {
            try { if ([Console]::KeyAvailable -and [Console]::ReadKey($true).Key -eq 'Q') { $quit = $true } } catch {}
            Start-Sleep -Milliseconds 200
        }
    }
} finally {
    $ctl.Stop = $true
    Write-Host 'Stopping workers...'
    foreach ($j in $jobs) { try { [void]$j.PS.EndInvoke($j.Handle) } catch {}; $j.PS.Dispose() }
    $pool.Close()
}
