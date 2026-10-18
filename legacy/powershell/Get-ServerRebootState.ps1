<#
    Get-ServerRebootState.ps1  --  READ-ONLY

    Post-restart check for servers restarted by hand (written 2026-10-17 after a restart round). Per server, over DCOM
    (WinRM is closed): last boot, Windows reboot flags in the registry (CBS RebootPending, WU RebootRequired,
    PendingFileRenameOperations) and Automatic services that are not running.

    Example
        .\Get-ServerRebootState.ps1 -ComputerName SRV01,SRV02
        .\Get-ServerRebootState.ps1 -ComputerName (Get-Content list.txt) -Out .\server-restart-check-YYYY-MM-DD.csv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $ComputerName,
    [string] $Out,
    [int] $Throttle = 20,
    [int] $TimeoutSec = 30
)
$ComputerName = $ComputerName -split ',' | ForEach-Object { $_.Trim().TrimStart('\') } | Where-Object { $_ } | Sort-Object -Unique

$probe = {
    param($h, $t)
    $r = [ordered]@{ Name = $h; Reach = ''; LastBoot = ''; UpMin = ''; RegPending = ''; StoppedAuto = ''; Err = '' }
    try {
        $s = New-CimSession -ComputerName $h -SessionOption (New-CimSessionOption -Protocol Dcom) -OperationTimeoutSec $t -ErrorAction Stop
        $os = Get-CimInstance -CimSession $s -ClassName Win32_OperatingSystem -ErrorAction Stop
    } catch { $r.Reach = 'NO'; $r.Err = $_.Exception.Message.Split("`n")[0]; return [pscustomobject]$r }
    $r.Reach = 'ok'
    $r.LastBoot = $os.LastBootUpTime.ToString('yyyy-MM-dd HH:mm')
    $r.UpMin = [int]((Get-Date) - $os.LastBootUpTime).TotalMinutes
    try {
        $HKLM = [uint32]2147483650; $f = @()
        # A bare CBS RebootPending is the reboot mark StartComponentCleanup holds while it runs; it only means a reboot
        # when PackagesPending has entries (see Watch-ServerRestart.ps1)
        $cbs = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing'
        $k = Invoke-CimMethod -CimSession $s -Namespace root\default -ClassName StdRegProv -MethodName EnumKey -ErrorAction Stop `
             -Arguments @{ hDefKey = $HKLM; sSubKeyName = $cbs }
        if ($k.sNames -contains 'RebootPending') {
            $pp = Invoke-CimMethod -CimSession $s -Namespace root\default -ClassName StdRegProv -MethodName EnumKey -ErrorAction Stop `
                  -Arguments @{ hDefKey = $HKLM; sSubKeyName = "$cbs\PackagesPending" }
            $n = @($pp.sNames | Where-Object { $_ }).Count
            $f += $(if ($n) { "CBS($n pkgs)" } else { 'cleanup' })
        }
        $k = Invoke-CimMethod -CimSession $s -Namespace root\default -ClassName StdRegProv -MethodName EnumKey -ErrorAction Stop `
             -Arguments @{ hDefKey = $HKLM; sSubKeyName = 'SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update' }
        if ($k.sNames -contains 'RebootRequired') { $f += 'WU' }
        $v = Invoke-CimMethod -CimSession $s -Namespace root\default -ClassName StdRegProv -MethodName GetMultiStringValue -ErrorAction Stop `
             -Arguments @{ hDefKey = $HKLM; sSubKeyName = 'SYSTEM\CurrentControlSet\Control\Session Manager'; sValueName = 'PendingFileRenameOperations' }
        if ($v.ReturnValue -eq 0 -and @($v.sValue | Where-Object { $_ }).Count) { $f += 'PFR' }
        $r.RegPending = if ($f) { $f -join '+' } else { 'no' }
    } catch { $r.RegPending = 'err' }
    try {
        # Trigger-start and self-stopping services that are routinely Auto + Stopped on these builds
        $skip = '^(gupdate|edgeupdate|MicrosoftEdgeElevationService|sppsvc|RemoteRegistry|MapsBroker|CDPSvc|tiledatamodelsvc|' +
                'WbioSrvc|clr_optimization.*|ShellHWDetection|BITS|wuauserv|UsoSvc|dmwappushservice|GoogleUpdater.*|' +
                'OneSyncSvc_.*|CDPUserSvc_.*|WaaSMedicSvc|DoSvc|InstallService|StateRepository|TrustedInstaller|' +
                'AGMService|AGSService|AdobeARMservice|SysmonLog|VSS|swprv)$'
        $r.StoppedAuto = (Get-CimInstance -CimSession $s -ClassName Win32_Service -Filter "StartMode='Auto' AND State<>'Running'" -ErrorAction Stop |
                          Where-Object { $_.Name -notmatch $skip } | ForEach-Object { $_.Name } | Sort-Object) -join ','
    } catch { $r.StoppedAuto = 'err' }
    Remove-CimSession $s
    [pscustomobject]$r
}

$pool = [runspacefactory]::CreateRunspacePool(1, $Throttle); $pool.Open()
$jobs = foreach ($h in $ComputerName) {
    $ps = [powershell]::Create().AddScript($probe).AddArgument($h).AddArgument($TimeoutSec); $ps.RunspacePool = $pool
    [pscustomobject]@{ PS = $ps; Handle = $ps.BeginInvoke() }
}
$rows = foreach ($j in $jobs) { $j.PS.EndInvoke($j.Handle); $j.PS.Dispose() }
$pool.Close()
$rows = $rows | Sort-Object Name

if ($Out) { $rows | Export-Csv -Path $Out -NoTypeInformation -Encoding UTF8 }
$rows
