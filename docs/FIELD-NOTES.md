# Field notes — what the real runs showed (16–17 Oct 2026)

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
  It is **not** a reboot request — the grid shows it as `component cleanup running`.
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
