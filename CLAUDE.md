# WUGrid — agentless Windows Update rounds for servers

.NET Framework 4.8 tool (WinForms GUI + `wugrid-cli` CLI) for sysadmins to drive a patch round across Windows servers —
scan, install, restart in waves, verify — while watching each server live. Agentless over WMI/DCOM, so it works where
WinRM is closed. Grew out of a PowerShell watch grid used on a real 54-server round (`legacy/powershell/`).

Start with `docs/DESIGN.md` (architecture, safety model, milestones) and `docs/FIELD-NOTES.md` (what real runs showed —
read it before changing state logic). Estate-specific context, if present, is in the gitignored `CLAUDE.local.md`.

## Ground rules

- **Observe by default.** Watching and verifying never change a target. Anything that acts (restart, scan trigger,
  install, scheduled task) is an explicitly named action: plan shown first, explicit confirmation, guards re-checked per
  server, written to the audit log. No observation ever triggers an action by itself.
- **Restarts are never forced** (`Win32ShutdownTracker` flags 2, no `EWX_FORCE`) and only go to servers `RestartGuard`
  allows (no update work running); `RestartAction` re-reads each server just before sending. Keep it that way.
- **Never restart a real server while developing or testing** — use check-only, the demo or unit tests; a real restart
  test needs the operator to name the server for that test.
- **Agentless, DCOM only**: `System.Management` over DCOM, registry via `root\default:StdRegProv`, ICMP for up/down.
  No WinRM, nothing installed. Anything an action leaves on a target is named `WUGrid*` and removed when done.
- **No ConfigMgr**: users may not have it. Use only what every Windows Server has (WMI, registry, event logs, the
  Windows Update Agent) — no ConfigMgr classes, services or site database, not even as an optional extra.
- **Locale-neutral**: key logic on event IDs, HRESULTs and state values, never on message text.
- **Cheap towards targets**: one WMI connection per server, reused; poll every few seconds; drop it when ping fails.
- **Public-repo hygiene**: no real hostnames, domains, accounts or site names in tracked files. Real-run material stays
  in gitignored `samples/` and `CLAUDE.local.md`; test fixtures are anonymised.
- **Testing on real servers**: read-only checks are fine; actions only against servers the operator named for that test.
- `StateClassifier` stays pure and every field-notes rule in it has a unit test.

## Build

```
dotnet build WUGrid.sln
dotnet test WUGrid.sln
```

Targets `net48` (C# 12) via `Directory.Build.props`; `Microsoft.NETFramework.ReferenceAssemblies` lets it build without
the targeting pack. The legacy scripts are Windows PowerShell 5.1 — see the 5.1 traps in FIELD-NOTES.

## Layout

- `src/WUGrid.Core` — observation model, `StateClassifier`, `RoundSnapshot` / `IRoundSource`, `RoundTracker`
  (transitions, alarms, all-clean), `LiveRound` (thread per server), `Probing/` (`ServerProbe` incl. baseline and
  verification passes, `VerificationReader`, `AccessCheck`, WMI over DCOM), `Verification/` (records +
  `VerificationBuilder`, pure), `Reports/` (HTML/CSV), `Errors/` (catalog), `Restart/` (guard, action, audit log),
  `ServerList`, `Demo/` (scripted round).
- `src/WUGrid.Cli` — `wugrid-cli.exe` (`watch`: console grid / `--headless` events, `--state` JSON, `--events` JSONL;
  `check`, `verify`, `error`). `Reports/ConsoleGrid` (pure) lays out its frame, `Reports/RoundJson` writes its files. `src/WUGrid.Gui` — `WUGrid.exe`: live grid + event pane,
  Servers…, Reboot…, Report…, Error codes…, server details (double-click); `--demo` plays the simulated round.
- `tools/update_error_catalog.py` — regenerates `Errors/error-codes.microsoft.tsv` from Microsoft Learn (never edit that
  file by hand; WUGrid's own explanations go in `error-codes.wugrid.tsv`).
- `assets/WUGrid.ico` — app icon for both exes and the windows, drawn by `tools/New-AppIcon.ps1` (edit the script, not
  the .ico).
- `site/` — project page (static HTML, deployed by `.github/workflows/pages.yml`). Its pictures and
  `sample-report.html` come from `tools/New-Screenshots.ps1` (`WUGrid.exe --demo --screenshots DIR`: the demo round
  only, windows off-screen, Reboot window in preview that sends nothing). Never put a picture of a real round there.
- `tests/WUGrid.Core.Tests` — xUnit.
- `legacy/powershell/` — the original grid, follower and one-shot check; reference behaviour for M1/M2 parity.
- `docs/` — `DESIGN.md`, `FIELD-NOTES.md`.

## Status

- M0 done; MIT licence (`LICENSE`, Alex Lillo). Public at `github.com/Lill82/WUGrid` since 2026-10-18; CI on GitHub
  Actions; the project page deploys from `site/` through GitHub Pages (`pages.yml`).
- M1 built: live grid (WMI/DCOM probe, thread per server), Servers window with access check, asterisk footnote,
  error catalog, demo, polling interval, `wugrid-cli watch` (console grid, `--headless` events, live list reload,
  state JSON, events JSONL). All-clean waits for each server's checks (`ServerRow.ChecksDone`). Still open: the parity
  run against real servers.
- M2 done: baseline, verification passes, update-failed / service-not-running flags, details window, HTML/CSV report,
  `wugrid-cli verify`.
- M3 first cut: Reboot window (`Restart/`: `RestartGuard`, `RestartAction`, `AuditLog`), never forced, check-only.
  Open: waves, protect list, CLI.
- Decide on event IDs + source + insertion strings (see `VerificationBuilder`); the Setup log is not readable over WMI.
