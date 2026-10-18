# WUGrid design

## Goal

A public, agentless tool for Windows sysadmins to drive a patch round across servers end to end — **scan → install →
restart → verify**, in waves — while watching every server live. .NET Framework 4.8, WinForms GUI plus a `wugrid-cli`
CLI. Everything goes over WMI/DCOM and ICMP, so it works where WinRM is closed, and nothing is installed on the targets.

It grows out of a PowerShell watch grid (`legacy/powershell/`) used live on a 54-server restart round. That run taught
which signals are routine and which are real failures; `docs/FIELD-NOTES.md` records them and the state logic here is
built on them.

## Principles

1. **Observe by default.** Watching and verifying are read-only. Acting is always a separate, named step.
2. **Plan, confirm, act, audit.** Every action shows what it will do to which servers, needs explicit confirmation,
   re-checks guards right before touching each server, and is written to an append-only audit log.
3. **Nothing acts on its own.** No observation ever triggers an action (no auto-retry, no auto-reboot).
4. **Agentless and tidy.** Anything an action leaves on a target (a scheduled task, a registry key) is named `WUGrid*`,
   documented, and removed when the action completes.
5. **Cheap towards targets.** One WMI connection per server, reused; a poll every few seconds; drop the connection when
   ping fails or a call throws (a dead DCOM connection hangs the next call).
6. **Locale-neutral.** Decide on event IDs, HRESULTs and state values; message text is display-only.

## Architecture

```
WUGrid.sln
  src/WUGrid.Core     net48 library: probing, classification, monitoring, actions, audit
  src/WUGrid.Cli      wugrid-cli.exe — scripting, headless/background runs, CI-friendly output
  src/WUGrid.Gui      WUGrid.exe     — WinForms live grid, wave planner, action confirmations
  tests/WUGrid.Core.Tests          — xUnit; fixtures are anonymised real-run sequences
```

Core layers:

| Layer | Responsibility |
|---|---|
| `WmiConnection` | `System.Management` scope over DCOM (`Impersonate`, `PacketPrivacy`, privileges on for shutdown). Connect runs with a hard timeout because `ManagementScope.Connect` can block for tens of seconds on an unreachable RPC endpoint. |
| `ServerProbe` | One poll → one `Observation` (raw facts only). |
| `StateClassifier` | Pure `Observation` + cut-off + now → `ServerStatus`. All field-notes rules live here and are unit-tested. |
| `ServerWatcher` / `RoundMonitor` | One long-running worker per server; the monitor owns the list (live reload), snapshots, transitions and alarms. GUI and CLI both host the same monitor in-process, so state stays typed (the legacy follower parsed formatted strings back out of JSON). |
| Actions | `RestartAction`, update providers (`IUpdateProvider`), each with guards and audit. |
| Outputs | State snapshot JSON, transitions/alarms log (JSONL), end-of-round CSV/HTML report. |

## What is observed

Per poll, over DCOM: ping; `Win32_OperatingSystem.LastBootUpTime`; `TiWorker.exe` in `Win32_Process`; registry via
`StdRegProv` — CBS `RebootPending` + `PackagesPending` count, WU `RebootRequired`.

| State | Meaning |
|---|---|
| `checking...` | no WMI answer yet since the worker started or the server came back |
| `RESTART PENDING` | answers, booted before the cut-off, a restart is pending: CBS packages waiting (with the RebootPending mark) or WU `RebootRequired` |
| `NO RESTART PENDING` | answers, booted before the cut-off, nothing pending (a bare CBS mark is component cleanup and PendingFileRenameOperations is noise — neither counts). Can still be restarted, and does not count as done until it is BACK |
| `INSTALLING` | answers, booted before the cut-off, `TiWorker.exe` running |
| `DOWN` | no ping (with time down) |
| `BOOTING` | ping answers, WMI not ready yet |
| `BACK` / `BACK · PENDING` | booted after the cut-off. Clean, or flagged: a restart pending again (shown in the state as `BACK · PENDING`), an update failed, a service not running. Routine notes: `servicing finishing`, `component cleanup running` |

`Access is denied` (`0x80070005`) and `The RPC server is unavailable` (`0x800706BA`) while BOOTING carry an asterisk and a
footnote: they show for a minute or two on every boot and are usually not a problem. Decided on the HRESULT, never on the
(localized) text.

### Server list and access check

Servers are added and removed in the GUI (or given on the command line / in a list file, saved to
`%APPDATA%\WUGrid\servers.txt`). Each server added gets one read-only check, each step time-limited: DNS, ping, WMI over
DCOM (OS query plus a `StdRegProv` read — what the grid needs), and listing `admin$`. `wugrid-cli check` runs the same.

### Watching from the console

`wugrid-cli watch` hosts the same `LiveRound` and `RoundTracker` as the GUI, read-only. Servers: names given, a
`--list` file (re-read when its time stamp changes; a file still being saved is tried again next refresh), or the GUI's
saved list. The console grid (`ConsoleGrid`, pure) redraws every second and never scrolls: when the servers do not fit,
clean BACK servers fold into one line, then the rest is cut with a "+N more" line; the latest events fill the space
left. `--headless`, or redirected output, prints the event stream instead and stops once the round is all clean.

The tracker's events: **Seen** (a server's first state — at start, or when it joins the list; the GUI's event pane
skips these), **Transition**, **Alarm** (DOWN 15 min, pending again 20 min after the boot), **AllClean**. All-clean
waits until every server is BACK clean *with its checks done* for its current boot — update results read and services
checked (5 min up) — so a green row that still hides a failure does not end a headless run.

- `--state FILE`: the round as one JSON object, rewritten on every refresh (written aside, then swapped in): time,
  cut-off, counts, `allClean`, and per server the state and flags by enum name, label, note, last boot, elapsed,
  pending restart, busy processes, builds, failed updates with codes, services not running.
- `--events FILE`: append-only JSON lines: time, kind, server, text, and the state after (and before, for a
  transition).

### Error catalog

Every error code WUGrid shows can be explained. `Errors/error-codes.microsoft.tsv` is generated from Microsoft Learn
(CC BY 4.0) by `tools/update_error_catalog.py`: all documented Windows Update codes, the WMI constants, and the CBS / Win32
codes on the Windows Update troubleshooting pages. Microsoft publishes no complete CBS list, so CBS coverage is the
documented codes. `Errors/error-codes.wugrid.tsv` is hand-written for the codes the probe and checks run into (remote
access, `0x800F0840` from the field notes). Unknown Win32 codes fall back to Windows' own message; unknown CBS / WU codes
to what their facility says and where to look (CBS.log, WindowsUpdate.log). M2's failure detection (WU events 19/20,
Setup event 3) will show its codes through the same catalog.

### Verification (M2)

`ServerProbe` collects, read-only, and `VerificationBuilder` (pure, unit-tested against real event layouts) interprets:

- **Baseline**, once, the first time a server is seen not yet restarted: build.UBR (registry) and its running Automatic
  services. A server first seen already restarted has no baseline; services then fall back to "Automatic, not running".
- **Verification passes** once it is BACK: on the first poll of each boot, then every 2 min until the boot settles (up 15
  min, TiWorker gone), then no more until the next boot or a "Check again". Each pass reads the System log since the
  cut-off — the first pass of a boot by time, later ones only records after the last seen (`RecordNumber`) — plus the
  build and, once up 5 min (delayed-start services), the services.
- From the System log, decided on event ID + source + data fields, never message text:
  `WindowsUpdateClient` 19/20 (install result per update; last result wins; event 20's first field is the HRESULT),
  `User32` 1074 (who restarted, process, reason code, comment), `Kernel-General` 12 (each boot and the build.UBR it came
  up on), `EventLog` 6008 (unexpected shutdown), `Service Control Manager` 7040 (who changed a service's start type —
  only used to explain a service found not running; BITS flips its own start type every few minutes).
- The Setup log (servicing events 2/3) is not a classic log, so `Win32_NTLogEvent` cannot read it over DCOM. Updates
  installed through Windows Update/WSUS report through events 19/20 anyway.
- New BACK flags: **update failed** (an update whose last result is a failure, with the code explained) and **service
  not running**. Flags combine; a verification only counts for the boot it was made on.
- Cost on a 200,000-record System log: first pass ~2–4 s, incremental ~1 s, services ~0.6 s.

Outputs: Build column (before → now), details window per server (double-click), **Report…** (HTML page with a summary
and a "needs attention" section, or CSV in the culture's list separator for Excel), `wugrid-cli verify` (one-shot, same
checks, `--html` / `--csv`).

## Actions

### Restart

Built (GUI **Reboot…**). `Win32_OperatingSystem.Win32ShutdownTracker` over DCOM, **never forced**: flags 2 (reboot
without `EWX_FORCE`), reason `0x80020011` (Operating System: Hotfix, planned), a comment naming the operator and
workstation (lands in System event 1074), and a warning period (0–300 s, default 60) during which logged-on users are told
and the restart can be aborted on the server with `shutdown /a`. The connection enables the caller's privileges
(SeRemoteShutdownPrivilege); observing never does.

- `RestartGuard` (pure) decides who can be ticked: reachable with WMI answering, not INSTALLING (a restart already under
  way), no update/install process running (`TiWorker.exe`, `TrustedInstaller.exe`, `wusa.exe`, `dism.exe`,
  `DismHost.exe`, `msiexec.exe`, `SetupHost.exe` — read every poll in the same `Win32_Process` query), no component
  cleanup. A pending reboot or an earlier restart in the round is shown, not blocking.
- `RestartAction` reads the server again over its own connection right before sending and applies the guard to that read;
  a server that started update work is skipped. Check-only mode stops before the call.
- Confirmation lists the servers; results go to the event pane and to `%APPDATA%\WUGrid\audit.jsonl` (append-only JSON
  lines: time, operator, workstation, server, options, outcome, code).

### Updates — providers

The Windows Update Agent API can **search** from a remote caller, but Microsoft blocks `Download`/`Install` from a
remote caller by design; installing has to run locally on the target. Agentless routes, to settle in the M4 spike:

| Route | Notes |
|---|---|
| **WUA via a one-shot SYSTEM scheduled task**, created over WMI | The PSWindowsUpdate `Invoke-WUJob` approach. Creation options: `root\Microsoft\Windows\TaskScheduler` (`PS_ScheduledTask`, Server 2012+), or `Win32_Process.Create` → `schtasks.exe` (all versions; `/TR` caps at 261 chars, so the script is staged in a `WUGrid` registry key via `StdRegProv` and the task runs a short bootstrap). Progress and results are written back to that registry key and read with `StdRegProv` — no SMB, no WinRM. Removes task and key at the end. |
| **`MSFT_WUOperations`** (`root\Microsoft\Windows\WindowsUpdate`, Server 2019+) | Scan/install as WMI methods. Must confirm whether remote DCOM calls are allowed. |

Both rely only on what every Windows Server has and work whether updates come from Microsoft Update or WSUS. No
ConfigMgr or other management agent is assumed.

### Safety model

- **Waves**: ordered server groups. The next wave starts when the previous one is all BACK clean, or on the operator's
  go; a failure stops the round.
- **Guards**, re-checked per server immediately before acting: reachable; not already restarting or installing; no
  other WUGrid action in flight; not on the protect list (servers WUGrid must never act on); optional checks such as
  interactive sessions.
- **Confirmation**: GUI dialog listing servers and action; CLI prints the plan and requires typed confirmation, or an
  explicit `--yes` together with a server-count limit for unattended runs. `--what-if` everywhere.
- **Audit**: JSONL, append-only — time, operator, workstation, run id, action, target, parameters, outcome.

## Milestones

| | Scope | Done when |
|---|---|---|
| **M0** Foundation — done | Solution, build, tests; anonymised field notes and fixtures; licence; CI (GitHub Actions, windows runner); first commit | builds from a clean clone; no estate names in tracked files |
| **M1** Watch (read-only) — built | Probe, monitor, live list reload, transitions + alarms log, state JSON; `wugrid-cli watch` (console grid, `--headless` events); GUI grid. Open: the parity run | parity with the legacy grid against real servers |
| **M2** Verify (read-only) — done | Patch-level outcome, services baseline, who restarted / boot count, end-of-round report | catches the two green-but-wrong cases in the field notes (it caught a real third one on its first run) |
| **M3** Restart — first cut done | Reboot window: guards, re-check, never forced, confirmation, audit, check-only. Open: waves, protect list, CLI | a test server restarted and verified end to end |
| **M4** Updates | Spike the routes above; WUA provider; scan → approve → install → restart (M3) → verify (M2) | a test wave patched end to end |
| **M5** Release | Docs, release zip, versioning, (code signing) | public release |

The legacy round's candidate list maps as: patch-level outcome, services baseline, who restarted, end-of-round report →
M2; one tool instead of two → M1; de-estate-ify → M0/M1.

## Open questions

- Minimum target OS for actions: Server 2008 R2 has PowerShell 2.0 and no `PS_ScheduledTask`; 2012 R2+ is simpler.
- Credentials: current user only (Kerberos), or also alternate credentials per run.
