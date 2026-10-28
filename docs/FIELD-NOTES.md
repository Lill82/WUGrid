# Field notes — what the real runs showed (16–18 Oct 2026)

Context: 54 servers had several months of cumulative updates (LCUs) installed but never committed. They were restarted
by hand on 17-10 (15:09–17:47, then SRV-A at 20:10) while the legacy grid watched: 44, then 1, then 3, then SRV-A.
Server names below are placeholders; the real logs are kept private.

## A normal restart, step by step

SRV-A (Server 2019, 110 days up, 551 CBS packages pending), from an ad-hoc polling loop:

```
20:11:43 INSTALLING (pre-shutdown) | TiWorker, CBS 551 pkgs pending   <- restart clicked; TiWorker works before shutdown (~3 min)
20:14:21 DOWN
20:14:52 BACK boot 20:14 | TiWorker, CBS 551 pkgs pending
20:16:38 BACK boot 20:16 | TiWorker, CBS 1 pkgs pending               <- 2nd reboot by TrustedInstaller
           ... servicing finishing, then clean                        ("CBS 1 pkgs" there = the @($null) bug below)
```

Typical follower sequence: `INSTALLING -> DOWN -> BOOTING (Acceso denegado / El servidor RPC no está disponible) ->
BACK -> DOWN -> BACK (servicing finishing) -> BACK (component cleanup running) -> BACK`.

## Things that look alarming but are routine

- **Chained reboots.** ~15 of 44 servers did 1–2 extra reboots started by TrustedInstaller (stacked LCUs). A server
  going `BACK -> DOWN` again a minute later is normal. Server 2016 is slow: SRV-G serviced 15:12 → 16:16.
- **`BOOTING` with "Acceso denegado." or "El servidor RPC no está disponible."** for a minute or two during boot:
  RPC/DCOM up before the security subsystem is. Transient. (Messages are Spanish — the OS locale there is es-ES.)
- **CBS `RebootPending` with an empty `PackagesPending`.** That is the "reboot mark" the `StartComponentCleanup` idle
  task (CbsTask deep clean) holds while it runs; it clears at the end of each pass (SRV-H: deep clean > 40 min).
  It is **not** a reboot request — the grid shows it as `component cleanup running`. But the mark can also stay after
  update work, with no TiWorker running and Windows Update's `RebootRequired` set (SRV-P, Server 2016, 28-10: a
  cumulative update failed for lack of disk space at 02:15, install retried after a restart at 14:31). The Reboot
  window refused it as "component cleanup running"; since 0.14.1 the mark does not block a restart when Windows
  Update asks for one (a cleanup really running still blocks through TiWorker).
- **PendingFileRenameOperations (PFR)** lingers on many servers right after boot; on its own it meant nothing here.

## Failures that showed green in the legacy grid (WUGrid M2 flags them)

- **Stacked LCUs fail to commit: `0x800F0840` (CBS_E_SESSION_CORRUPT).** When several months of LCUs were staged on top
  of each other, only the oldest committed at reboot; the rest failed and were discarded. Machine comes back clean, but
  at an old patch level. Seen on SRV-B (16-10, stayed at June) and SRV-A (17-10: June CU KB5094123 committed,
  build 17763.8644 → 8880; July KB5099538 + August KB5120238 failed). Signatures, all since the boot:
  - System log, `Microsoft-Windows-WindowsUpdateClient` **event 20** "error 0x800F0840: <CU title>" (event 19 = OK);
  - Setup log **event 3** "failed to be changed to the Installed state … 0x800f0840" (event 2 = OK);
  - `\\host\admin$\Logs\CBS\CBS.log`: "Stale uno package session found … CBS_E_SESSION_CORRUPT", "Failed to resume
    session";
  - UBR (`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\UBR`) moved less than expected;
  - CBS `Packages\Package_for_RollupFix~…~<build>` `CurrentState` 112 = installed, 80 = superseded, 32 = absent.
- **Application services stopped/disabled by the owner before the restart.** SRV-A: the application's database
  service was stopped at 20:09 and set **Disabled** at 20:10:17 (System event 7040, by an admin account) so it would not
  auto-start; the owner re-enabled it at 20:40. The grid watches no application services. A service baseline taken
  before the restart and compared after is what caught it (`Win32_Service` State/StartMode).
- **Who/why restarted**: System event 1074 (User32) has the initiating user/process and reason; not read today.

## What the first verification run showed (17-10, evening)

- **A third green-but-failed server.** The first `wugrid-cli verify` against a Server 2019 test server that every check had
  shown green since 15:19 found Windows Update event 20: `0x800f0840` on the September cumulative update (KB5122876),
  two minutes after its second boot. The .NET update in the same batch installed (event 19). Both boots came up on
  build 17763.8880 (Kernel-General event 12 carries build and UBR), so the server never reached September.
- **Who restarted it is in event 1074**: the first restart came from the admin's workstation (`wininit.exe (<IP>)`,
  reason `0x800000ff`, no title), the second from `TrustedInstaller.exe` (reason `0x80020003`, planned OS upgrade).
- **BITS changes its own start type every 6–10 minutes** (System event 7040 by SYSTEM, auto ↔ on demand), so 7040 alone
  is noise. Judge services on `Win32_Service` (its `StartMode` / `State` values are not localized) and use 7040 only to
  say who changed a service that is found not running.
- **The Setup log is not reachable through `Win32_NTLogEvent`** (it is not a classic log). Windows Update's own events
  19/20 in the System log carry the install results.
- **Cost**: the System log had 218,000 records (84 MB). A query filtered by event ID and time took 3.3 s for 1.5 days and
  1.7 s for 8 hours; by record number 1.0 s; `Win32_Service` 0.6 s. Fine every couple of minutes, not every poll.

## The first restart round WUGrid sent (18-10)

Five Server 2019 servers, each RESTART PENDING with 18–71 CBS packages, restarted from the Reboot window in two batches
(three at 12:56, two at 13:08) with the 60 s warning, not forced; each was read again and passed the guard just before
its call. All five came back. The grid, a headless `wugrid-cli watch` and the audit log agree on the sequence. The
servers also hosted a WSUS downstream that was being removed in the same maintenance, which matters below.

SRV-M, from the headless watch (polled every 5 s):

```
13:08:29 restart requested (audit log)                                    <- 60 s warning starts
13:09:32 RESTART PENDING (CBS 71 pkgs) -> INSTALLING                      <- warning over: TiWorker works before shutdown
13:09:50 INSTALLING -> BOOTING (El servidor RPC no está disponible. *)    <- WMI gone while the server shuts down
13:10:06 BOOTING (Acceso denegado. *) -> DOWN                             <- ping gone
13:11:46 DOWN -> BOOTING (El servidor RPC no está disponible. *)          <- ping back, WMI not yet
13:13:19 BOOTING -> BACK (servicing finishing)
13:17:32 BACK -> BACK (service not running: Base de datos interna de Windows)   <- services pass at +5 min
```

- **Timings of a restart sent over WMI with the 60 s warning.** Request → INSTALLING about 65 s; INSTALLING 20–50 s;
  shutting down with WMI refusing 15–50 s; request → DOWN 1.5–4 min; DOWN 1.5–3.5 min; BOOTING with "RPC no disponible"
  1–2 min; request → BACK 5–7.5 min. No chained reboots this time: the pending packages were not stacked LCUs. The build
  (UBR) did not change on any of the five — it had already moved when the packages were installed — so the Build column
  only says something when the LCU itself was still pending at the cut-off.
- **Event 1074 for a restart sent over WMI**: user = the operator, process `wmiprvse.exe (<server>)`, reason `0x80020011`
  (Operating System: Hotfix, planned), comment "Restart requested from WUGrid by DOMAIN\operator on WORKSTATION". The
  details window and the report show it, and the server's own log names who did it.
- **The services pass found something real on all five**: Windows Internal Database stopped after the restart (SCM event
  7024, service-specific error 17113: its `master.mdf` was gone) and WSUS, whose database it holds, failed to start with
  it (events 7000/7009). Two print-management services timed out at start too, but they do not depend on it: they do so
  at every boot (see "Services that time out at every boot"). The operator knew why the database was gone — the WSUS
  removal — so those flags were right but expected:
  - a server can be flagged for a reason the operator already knows; an **"expected" acknowledgement** per service or
    per server lets the round go clean (added in 0.7.0: **Expected…**, and the `expected.txt` list file);
  - the **cause sits in SCM events 7000/7009/7022/7023/7024/7031/7034 since boot**, which WUGrid does not read yet;
    reading them for the flagged services would put the reason in the details window instead of a query by hand;
  - the 2-minute re-check works: a management agent's service flagged at the +5 min pass had started by the next one;
  - once a boot has settled (15 min) the flags stay until **Check again** in the details window;
  - one server came back with its antivirus service stopped (it ran before the restart) — nothing to do with WSUS, and
    exactly what the baseline comparison exists for;
  - a service that a role or application removal deleted while it still ran carries `DeleteFlag` in its registry key and
    goes at the next boot: since 0.6.0 the baseline leaves such services out and reports them as removed after the
    restart, so the before and after counts reconcile. Here the services were still registered as Automatic after the
    restart, so the operator's knowledge was the only signal.
- **Start the watch before sending restarts.** The headless watch here started two minutes after the first batch, found
  two servers already shutting down and had no baseline for them, so it fell back to "Automatic but not running". It
  flagged the same services this time, but that check also flags Automatic services that were already stopped before
  the restart. The GUI, open since before the restart, had a baseline for all five.
- **`watch --headless` ends only when every server is back clean**; with a flag that sticks it runs until Ctrl+C. Fine
  at a terminal; an unattended run wants "exit once every server has settled, status 1 if any is flagged" (open).

## Restarts refused while users are signed in (18-10, afternoon)

A second batch of five restarts from the Reboot window (0.7.0) at 14:16: two were sent; for the other three
`Win32ShutdownTracker` returned **1191**, `ERROR_SHUTDOWN_USERS_LOGGED_ON` (0x800704A7). Each failed the same way on two
or three more tries, including one right after a Check only that had said "would restart" for all three.

- **Windows refuses a restart that is not forced while any user is signed in, active or disconnected.** WUGrid's call
  arrives as a network logon with no session of its own, so every interactive session is "other users"; only a forced
  restart would sign them out, and WUGrid never forces. The morning's five went because nobody was signed in.
- What blocked them: on one server the domain's Administrator account had been signed in over Remote Desktop for a
  month, and that session was reconnected from the admin workstation between two tries — closing the window would only
  have disconnected it again. The other two had disconnected sessions idle for 2, 38 and 130 days (two accounts).
- Check only could not see it: the guard looked only at update work, and the refusal comes from the shutdown call.
  **Since 0.8.0 the check just before the restart lists who is signed in** and skips the server, naming the user, the
  session and since when. A session counts when one of its processes (`Win32_Process`, sessions above 0) belongs to a
  user account, decided on the owner SID (S-1-5-21-…) so it holds in any language; the logon screen's own processes
  (csrss, winlogon, LogonUI, dwm, fontdrvhost) are not asked about. The sign-in time is the oldest of the processes
  started at sign-in (sihost, taskhostw, rdpclip, explorer): explorer alone would mislead, as in the month-old session it
  had started that afternoon. The poll does not read
  sessions (an owner lookup per session is too much every few seconds), so the Reboot window can still tick such a
  server.
- The fix is on the server: the user signs out (Start → Sign out; closing Remote Desktop only disconnects), or, once the
  session's owner agrees, `logoff <ID> /server:<name>` ends it, closing what is open without saving.
  `quser /server:<name>` shows the sessions with their state and idle time, which WMI does not give.
- WUGrid counts any user signed in, the operator's own account too; whether Windows lets the caller's own sessions
  through is untested.

## Services that time out at every boot (18-10, evening)

After the afternoon's restarts the operator had to start two print-management services by hand (a print server and its
terminal service for the printers) on several servers. Checked on two of them by hand, reading the System log:

- **Not caused by the restart or the updates.** Both services time out at start — event 7009 ("waiting for the service
  to connect", 30000 ms) then 7000 — at nearly every boot the log holds: five of six boots since March on one server,
  all four since April of the year before on the other. ADWS (Active Directory Web Services) does the same at every
  boot on both, and was running again by the time WUGrid checked. The print services depend on their own database
  (Firebird) and web server, not on Windows Internal Database.
- 7009 means the process did not even connect to the Service Control Manager within 30 s; started later by hand it
  runs. Too slow to start while the server is busy booting. **The fix belongs to the server, not to WUGrid**:
  Automatic (Delayed Start) for those services (`sc.exe \\<server> config <service> start= delayed-auto`), a longer
  `ServicesPipeTimeout` for every service (needs a restart), or the vendor.
- **What WUGrid does about it (0.9.0), for any service**: for one not running, it reads the Service Control Manager's
  failure events (7000, 7001, 7009, 7011, 7022, 7023, 7024, 7031, 7034, 7038) at this boot and the boots before, and
  says "timed out at start; also at all 4 boots before". Every boot means the service's own problem — start it by hand
  now, fix it outside the round; a first failure points at the restart. Keyed on event IDs and data fields, so it holds
  in any language: the error comes as a raw field (`%%1053`), and the service by name or, in some events (7023 here),
  by display name. It stays flagged rather than becoming an expected stop: a stopped print service matters.
- **Reading months of log is the expensive part.** On one of these servers (System log 150 MB, 396,500 records since
  March) a `Win32_NTLogEvent` query for boots and these failures over 180 days took **124 s**: WMI walks every record in
  the range. So the boots after the cut-off come free with each verification pass, and older ones are read only when
  asked (Earlier boots in the details window, `verify --earlier-boots`), in the background on a connection of their own.
- Seen on the same two servers afterwards with 0.9.0: Windows Internal Database "stopped with its own error 17113
  (event 7024), also at the boot before", WSUS timed out at start (7009) on one and crashed later (7031) on the other —
  both marked expected by then, which keeps the reason visible without flagging.
- Windows Internal Database (above) fails the same way at every boot until the feature itself is removed: its data
  files are gone, its service is still installed and Automatic.

## Updates installed on a server already BACK (18-10, evening)

Two hours after its restart in the round, SRV-L ran Windows Modules Installer again (18:06–18:12, two runs, each
ending with the service's ~2 min idle stop) and came out with CBS 71 packages pending. The operator saw TiWorker on the
server while the grid's row said only `BACK · PENDING — reboot pending again (CBS 71 pkgs)`: the RebootPending mark
appears early in the install, the row becomes flagged, and the classifier returned on the flag before the TiWorker note
(`servicing finishing` was only written for clean rows). Since 0.9.1 a flagged row adds `TiWorker running` to its note.
The Reboot window already refused it meanwhile (busy processes are checked on every row, not only INSTALLING).

## Downloads that install later (22-10, read on the operator's own workstation)

A server can look finished while Windows Update is still downloading: the update installs afterwards, logs 19/20 and
may set RebootRequired again. The System log carries the steps as `WindowsUpdateClient` events, read on a Windows 11
25H2 (es-ES) workstation:

- **44 download started, 43 install started, then 19/20.** Each carries `[0]` title, `[1]` update ID, `[2]` revision; the
  manifest's version 0 of 44 (older builds) has no title (`[0]` update ID, `[1]` revision).
- **Download and install can be days apart**: two October updates logged 44 on 13-10 at 21:46, 43 on 16-10 at 14:18 and 19
  at 15:28; Edge WebView2 updates went 43 → 19 in about 30 s. There is no "download finished" event, so 44 alone means
  "downloading or downloaded, not installed yet". Since 0.13.0 that holds back "done" until the update has a result.
- **The first one seen live was on a machine not restarted yet**: a workstation up for two days logged 44 twice for the
  Windows 11 25H2 feature update (16:38 and 16:47, title with no KB) and the 0.13.0 row said nothing, because events
  were read only once a server was back. Since 0.13.1 a server not restarted yet reads Windows Update's events too.
- **BITS is not a usable signal here**: it switches itself to Automatic (Delayed Start) while it has any job, from any
  application (its 7040s every few minutes above), and the job list is only reachable locally. From Server 2019 Windows
  Update downloads through Delivery Optimization anyway. Event 44 comes from Windows Update itself, so it should appear
  whatever downloads the files — not yet seen on a server.

## PowerShell 5.1 traps hit while writing these

- `@($null).Count` is **1**. `StdRegProv.EnumKey` returns `sNames = $null` for an empty key, so
  `@((...).sNames).Count` reports 1 package pending forever. Filter: `@($x.sNames | Where-Object { $_ }).Count`
  (both legacy tools do; the ad-hoc SRV-A loop didn't, hence "CBS 1 pkgs pending" there).
- `powershell -File script.ps1 -ComputerName a,b` passes `"a,b"` as **one string** → split on commas yourself.
- Remote error messages arrive localized (es-ES) and through the console code page — `está` showed as `est�` in
  captured logs. Match on HRESULTs/IDs, not message text.
- `Get-CimInstance` returns `LastBootUpTime` as local `DateTime`.
- One CIM session per server reused across polls; drop it when ping fails or a call throws, or the next call hangs on
  a dead session.

## Environment it ran in

Admin workstation (Windows 11, Windows PowerShell 5.1, not elevated — remote calls only), an account that is an
administrator on the targets, a mix of Windows Server versions on VMware and physical hardware, ICMP allowed, WinRM
closed, DCOM open.
