# WUGrid

Agentless Windows Update rounds for servers: scan, install, restart in waves and verify — while watching every server
live. A WinForms GUI and a `wugrid-cli` CLI on .NET Framework 4.8. Everything goes over WMI/DCOM and ICMP, so it works
where WinRM is closed, and nothing is installed on the targets.

> **Status: early development.** The live grid watches and verifies real servers, read-only, and can restart the ones you
> pick (never forced). Installing updates is not built. See [`docs/DESIGN.md`](docs/DESIGN.md) for the plan.

## Principles

- **Observe by default.** Watching and verifying never change a target.
- **Plan, confirm, act, audit.** Restarts and installs are explicit actions, shown before they run, confirmed, re-checked
  per server and logged.
- **Nothing acts on its own.** No observation ever triggers an action.

## Use

```
WUGrid.exe                                  live grid; servers from %APPDATA%\WUGrid\servers.txt
WUGrid.exe SRV01 SRV02 --since 2026-10-17T15:00
WUGrid.exe --list C:\rounds\servers.txt     use (and save back to) that list
WUGrid.exe --demo                           simulated 75-minute round of 14 made-up servers, nothing contacted

wugrid-cli watch SRV01 SRV02 --since 2026-10-17T15:00
                                            the live round as a console grid; q quits
wugrid-cli watch --list servers.txt --headless --state state.json --events events.jsonl
                                            one line per change and alarm; stops once all are back clean
wugrid-cli check SRV01 SRV02                reachability check from the command line
wugrid-cli verify SRV01 SRV02 --since 2026-10-17T15:00 --html round.html
                                            one-shot verification: update results, restarts, services
wugrid-cli error 0x800F0840                 explain an error code
```

- **Verification**: once a server is back, WUGrid reads its System log (since the cut-off) and services and flags what a
  green restart can hide: an **update that failed** (Windows Update event 20, error code explained — e.g. stacked
  cumulative updates failing with `0x800F0840`) and a **service not running** that ran before the restart (with who
  changed its start type, and why it did not start: the Service Control Manager's own events — timed out at start, a
  service it depends on, its account could not log on, its own error code — and whether it failed the same way at the
  boots before, which says it is the service's own problem rather than the restart's; boots from before the cut-off are
  read on request, **Earlier boots** in the details window or `verify --earlier-boots`, as months of log take minutes
  to read over WMI). A service marked for deletion before the restart (a role or application removal that could
  not finish while it ran) is expected to go, and is reported as removed, not as stopped, so the counts reconcile.
  A stop you know about (a role being removed) can be marked **expected** (Expected… in the details window or the
  toolbar, or a line in `%APPDATA%\WUGrid\expected.txt`): it stays visible with your reason but no longer flags the
  server, so a round whose only remaining "problem" is a planned removal goes clean. Double-click a row for the details:
  updates, who restarted it and why (event 1074), each boot and the build it came up on, services. The Build column
  shows the build before → now.
- **`wugrid-cli watch`** is the same live round in a console, read-only: same probe, states, alarms and checks as the
  GUI. Servers come from the command line, from `--list FILE` (re-read whenever the file changes) or, with neither,
  from the GUI's saved list. The grid redraws every second; when the servers do not fit the window, clean BACK servers
  fold into one line first. `--headless` (also used whenever the output is redirected) prints one line per first state,
  change and alarm, and stops once every server is back clean with its checks done (`--keep-running` to carry on): what
  a background task follows. `--state FILE` rewrites the round as JSON on every refresh, `--events FILE` appends each
  event as a JSON line. Exit code 0 when every server ended back clean, 1 otherwise. `--demo` plays the simulated round.
- **Report…** saves the round as an HTML page (summary + what needs attention, to mail or attach to a change) or CSV.
- **Reboot…** restarts the servers you tick — the only thing WUGrid changes on a server:
  - only servers whose last poll shows no update work can be ticked (not while `TiWorker.exe`, `TrustedInstaller.exe`,
    `msiexec.exe`, `wusa.exe`, `dism.exe`, `SetupHost.exe` run, during component cleanup, or while installing before a
    shutdown already under way), and each is checked again just before the restart is sent;
  - **never forced** (`Win32ShutdownTracker`, reboot without force): running programs can delay or cancel it, and
    Windows refuses it while a user is signed in, even in a disconnected session. The check just before sending lists
    who is signed in and skips that server, naming the user, the session and since when (**Check only** shows it too);
  - an optional warning period (default 60 s) during which the restart can be aborted on the server with `shutdown /a`;
  - confirmed before sending; **Check only** runs everything except the restart call;
  - every request and result is appended to `%APPDATA%\WUGrid\audit.jsonl` (who, from where, which server, options,
    outcome) and logged in the event pane; the comment lands in System event 1074 on the server.

- **Servers…** adds and removes servers (type or paste names, or import a text file). Every server added gets a one-shot,
  read-only check: name resolves, ping, WMI over DCOM (OS query + registry read) and admin$. Failures come with the
  error code explained.
- **Restarted after** is the round's cut-off: a server counts as restarted when it booted after it.
- **States**, before → during → after the restart:

  | State | Meaning |
  |---|---|
  | `RESTART PENDING` | not restarted yet; a restart is pending (CBS packages waiting, or Windows Update's RebootRequired) |
  | `NO RESTART PENDING` | not restarted yet; nothing pending. It can still be restarted and is not done until it is back |
  | `INSTALLING` | not restarted yet; `TiWorker.exe` installing updates — a restart may be under way |
  | `DOWN` / `BOOTING` | no ping / ping but WMI not ready yet |
  | `BACK` | restarted, nothing pending, checks clean |
  | `BACK · PENDING` | restarted, but a restart is pending again (a chained reboot coming, or another one needed) |
  | `BACK` (orange) | restarted, but an update failed or a service is not running — the note says which |
- **Poll every** sets how often each server is pinged and read over WMI (2–60 s, default 5 s); it applies at once.
- **Error codes…** searches the catalog: 438 CBS, Windows Update, WMI and Win32 codes with descriptions and fixes from
  Microsoft Learn, plus WUGrid's own notes for the codes it runs into. Double-click a row showing an error to open it
  there; hover the note for a summary.
- An asterisk (`Access is denied. *`, `The RPC server is unavailable. *`) marks errors that are usually temporary while a
  server restarts; the footnote under the grid explains it. Decided on the error code, so it works in any language.

Requirements: Windows with .NET Framework 4.8 on the admin workstation; ICMP, DCOM/RPC (and SMB for the admin$ check) to
the servers; an account that is an administrator on them (restarting also needs the remote shutdown right, which
administrators have). Nothing is installed on the servers.

## Download

The latest release on [GitHub Releases](https://github.com/Lill82/WUGrid/releases/latest) is a zip: unzip it anywhere and
run `WUGrid.exe` or `wugrid-cli.exe`, nothing to install. The binaries are not signed yet: unblock the zip first
(Properties → Unblock), or Windows SmartScreen asks before they start.

## Build

Requires the .NET SDK 8 or later (the .NET Framework 4.8 reference assemblies come from NuGet).

```
dotnet build WUGrid.sln
dotnet test WUGrid.sln
```

## Docs

| Path | What it is |
|---|---|
| [`docs/DESIGN.md`](docs/DESIGN.md) | Architecture, safety model, update providers, milestones. |
| [`docs/FIELD-NOTES.md`](docs/FIELD-NOTES.md) | What a real 54-server round showed: state sequences, false alarms, failure signatures. |
| [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) | Licence of the Microsoft Learn error descriptions (CC BY 4.0). |
| `tools/update_error_catalog.py` | Regenerates the Microsoft part of the error catalog. |
| `tools/New-AppIcon.ps1` | Draws the app icon (`assets/WUGrid.ico`). |
| `tools/Publish-Build.ps1` | Tests, then builds both exes into `builds\<version>\` (version from `Directory.Build.props`). |
| `site/` | The project page (GitHub Pages, `.github/workflows/pages.yml`). |
| `tools/New-Screenshots.ps1` | Regenerates the page's pictures from the demo round (`WUGrid.exe --demo --screenshots`, Edge headless, a console window). |

## Licence

MIT, see [`LICENSE`](LICENSE). The Microsoft Learn error descriptions in
`src/WUGrid.Core/Errors/error-codes.microsoft.tsv` stay under CC BY 4.0, see
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

## Legacy PowerShell tools (read-only)

Written for and used on a real 54-server restart round (16–17 Oct 2026). Windows PowerShell 5.1, ICMP + DCOM/RPC to the
targets, admin rights on them.

| Script | What it does |
|---|---|
| `Watch-ServerRestart.ps1` | Live console grid. One runspace per server; redraws every `-RefreshSec`; `q` quits. Optional `-StateFile` JSON on every redraw, `-ListFile` re-read live. |
| `Watch-ServerRestartEvents.ps1` | Headless follower of the grid's `-StateFile`: one line per state change + alarms; exits when all are BACK clean. |
| `Get-ServerRebootState.ps1` | One-shot post-restart check (table/CSV): last boot, CBS/WU/PFR reboot flags, stopped Automatic services. |

```powershell
# Grid in its own window, servers from a file you can edit while it runs
Start-Process powershell -ArgumentList '-NoExit','-ExecutionPolicy','Bypass','-File','.\legacy\powershell\Watch-ServerRestart.ps1',
    '-ListFile','C:\path\servers.txt','-Since','2026-10-17T15:00','-StateFile','C:\path\state.json'

# Follower (e.g. as a background task): prints changes and alarms
.\legacy\powershell\Watch-ServerRestartEvents.ps1 -StateFile C:\path\state.json

# Verification once they're back
.\legacy\powershell\Get-ServerRebootState.ps1 -ComputerName (Get-Content C:\path\servers.txt) -Out C:\path\check.csv
```

`-Since` is the cut-off: a server counts as restarted when its last boot is after it.

| State | Meaning |
|---|---|
| `checking...` | no WMI answer yet since the worker started or the server came back |
| `NOT RESTARTED` | answers, booted before `-Since`, no servicing running |
| `INSTALLING` | answers, booted before `-Since`, `TiWorker.exe` running (updates applied before shutdown) |
| `DOWN` | no ping (timer shows for how long) |
| `BOOTING` | ping answers, WMI not ready yet (note shows the WMI error) |
| `BACK` | booted after `-Since`. Green = clean; yellow = reboot pending again (CBS packages / WU). Notes: `servicing finishing` (TiWorker), `component cleanup running` (bare CBS mark) |
