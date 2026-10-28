using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using WUGrid.Core;
using WUGrid.Core.Errors;
using WUGrid.Core.Probing;
using WUGrid.Core.Reports;
using WUGrid.Core.Verification;

namespace WUGrid.Cli
{
    static class Program
    {
        static int Main(string[] args)
        {
            // Redirected output (logs, pipes) gets UTF-8, so localized remote messages survive; the console's code page is left alone
            if (Console.IsOutputRedirected) Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });

            ActivityLog.CatchUnhandled();
            var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            var rest = args.Skip(1).ToList();
            switch (command)
            {
                case "check": return Logged(rest, Check);
                case "verify": return Logged(rest, Verify);
                case "watch": return Logged(rest, WatchCommand.Run);
                case "error": return Error(rest);
                default:
                    Console.WriteLine($"wugrid-cli {Assembly.GetExecutingAssembly().GetName().Version.ToString(3)}");
                    Console.WriteLine();
                    Console.WriteLine("  wugrid-cli watch [SERVER ...] [--list FILE] [--since DATETIME] [--expected FILE] [--headless] [--state FILE] [--events FILE]");
                    Console.WriteLine("                                     read-only live round: console grid (q quits), or --headless: one");
                    Console.WriteLine("                                     line per change and alarm until all are back clean (watch --help)");
                    Console.WriteLine("  wugrid-cli check SERVER [SERVER ...]   read-only reachability: DNS, ping, WMI over DCOM, admin$");
                    Console.WriteLine("  wugrid-cli verify SERVER [SERVER ...] [--since 2026-10-17T15:00] [--expected FILE] [--earlier-boots] [--html report.html] [--csv report.csv]");
                    Console.WriteLine("                                     read-only: restarted after the cut-off? update results, who");
                    Console.WriteLine("                                     restarted it, services not running and why (default cut-off: today 00:00);");
                    Console.WriteLine("                                     --earlier-boots: for a service that failed to start, read the System log");
                    Console.WriteLine($"                                     back {(int)EarlierBootsRead.Lookback.TotalDays} days: did it fail at those boots too? (minutes on a large log)");
                    Console.WriteLine("                                     --expected: stops the operator expects ([SERVER\\]SERVICE per line, // why;");
                    Console.WriteLine("                                     default %APPDATA%\\WUGrid\\expected.txt), shown, not flagged");
                    Console.WriteLine("  wugrid-cli error CODE [CODE ...]       explain CBS / Windows Update / WMI / Win32 error codes");
                    Console.WriteLine();
                    Console.WriteLine("  watch, check and verify also take --log [--log-dir DIR]: keep an activity log of what they do and every");
                    Console.WriteLine($"  error, for troubleshooting (default {ActivityLog.DefaultDirectory.Replace(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%")}, kept {ActivityLog.KeepDays} days;");
                    Console.WriteLine("  on without --log when turned on in WUGrid.exe; --no-log turns it off for the run).");
                    Console.WriteLine();
                    Console.WriteLine("WUGrid.exe is the live grid with Servers, Reboot and Report windows; --demo plays a simulated round in either.");
                    return command == "" || command == "help" || command == "-h" || command == "--help" ? 0 : 2;
            }
        }

        static int Logged(List<string> args, Func<IReadOnlyList<string>, int> run)
        {
            if (!LogOption(args, out var problem)) { Console.Error.WriteLine(problem); return 2; }
            if (ActivityLog.Current?.Problem is string p) Console.Error.WriteLine(p);
            var code = 2;
            try { return code = run(args); }
            finally { ActivityLog.Info(null, $"--- wugrid-cli done, exit code {code}"); }
        }

        /// <summary>Takes --log, --log-dir DIR and --no-log out of the arguments and starts the activity log when asked for,
        /// or when the saved setting (WUGrid.exe's status bar) has it on.</summary>
        static bool LogOption(List<string> args, out string problem)
        {
            problem = null;
            var on = Settings.IsOn(Settings.Get(ActivityLog.SettingName));
            var dir = ActivityLog.DefaultDirectory;
            for (var i = 0; i < args.Count; i++)
            {
                var a = args[i].ToLowerInvariant();
                if (a == "--log") on = true;
                else if (a == "--no-log") on = false;
                else if (a == "--log-dir")
                {
                    if (i + 1 >= args.Count) { problem = "--log-dir: give a folder"; return false; }
                    try { dir = Path.GetFullPath(args[i + 1]); }
                    catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { problem = $"--log-dir: {ex.Message}"; return false; }
                    args.RemoveAt(i + 1);
                    on = true;
                }
                else continue;
                args.RemoveAt(i--);
            }
            if (on) ActivityLog.Start(dir, "wugrid-cli", $"wugrid-cli {Assembly.GetExecutingAssembly().GetName().Version.ToString(3)} {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}");
            return true;
        }

        static int Check(IReadOnlyList<string> args)
        {
            var servers = ServerList.Parse(string.Join(" ", args));
            if (servers.Count == 0) { Console.Error.WriteLine("usage: wugrid-cli check SERVER [SERVER ...]"); return 2; }
            var results = servers.AsParallel().AsOrdered().WithDegreeOfParallelism(Math.Min(16, servers.Count))
                                 .Select(s => AccessCheck.Run(s, TimeSpan.FromSeconds(15))).ToList();
            foreach (var r in results)
            {
                ActivityLog.Info(r.Server, "check: " + string.Join(", ", new[] { ("DNS", r.Dns), ("ping", r.Ping), ("WMI", r.Wmi), ("admin$", r.AdminShare) }
                    .Select(c => $"{c.Item1} {c.Item2.State}" + (c.Item2.State == CheckStatus.Failed ? $" ({c.Item2.Detail}, {ErrorCatalog.Hex(c.Item2.HResult)})" : ""))));
                Console.WriteLine(r.Server);
                Line("DNS", r.Dns);
                Line("Ping", r.Ping);
                Line("WMI", r.Wmi);
                Line("admin$", r.AdminShare);
            }
            return results.All(r => r.AllOk) ? 0 : 1;
        }

        static int Verify(IReadOnlyList<string> args)
        {
            var since = DateTime.Today;
            string csv = null, html = null, expectedPath = null;
            var earlierBoots = false;
            var names = new List<string>();
            for (var i = 0; i < args.Count; i++)
            {
                if (args[i] == "--since" && i + 1 < args.Count)
                {
                    if (!DateTime.TryParse(args[++i], out since)) { Console.Error.WriteLine($"--since: not a date/time: {args[i]}"); return 2; }
                }
                else if (args[i] == "--csv" && i + 1 < args.Count) csv = args[++i];
                else if (args[i] == "--html" && i + 1 < args.Count) html = args[++i];
                else if (args[i] == "--expected" && i + 1 < args.Count) expectedPath = args[++i];
                else if (args[i] == "--earlier-boots") earlierBoots = true;
                else names.AddRange(ServerList.Parse(args[i]));
            }
            if (names.Count == 0) { Console.Error.WriteLine("usage: wugrid-cli verify SERVER [SERVER ...] [--since DATETIME] [--expected FILE] [--earlier-boots] [--html FILE] [--csv FILE]"); return 2; }
            if (expectedPath != null && !File.Exists(expectedPath)) { Console.Error.WriteLine($"--expected: no such file: {expectedPath}"); return 2; }
            if (expectedPath == null && File.Exists(ExpectedList.DefaultPath)) expectedPath = ExpectedList.DefaultPath;
            var expected = expectedPath != null ? ExpectedList.Load(expectedPath) : ExpectedList.Parse("");

            Console.WriteLine($"Cut-off {since:yyyy-MM-dd HH:mm}: a server counts as restarted when it booted after it." +
                              (expected.Count > 0 ? $" {expected.Count} expected stop{(expected.Count == 1 ? "" : "s")} from {expectedPath}." : ""));
            var observations = names.Distinct(StringComparer.OrdinalIgnoreCase).AsParallel().AsOrdered().WithDegreeOfParallelism(Math.Min(16, names.Count))
                                    .Select(n =>
                                    {
                                        using (var probe = new ServerProbe(n, TimeSpan.FromSeconds(15), () => since))
                                        {
                                            var o = probe.Poll();
                                            // Only where a service failed to start: the read walks months of the log
                                            if (earlierBoots && (o.Verification?.StoppedServices.Any(i => i.Failure != null) ?? false))
                                            {
                                                probe.ReadEarlierBoots();
                                                o = probe.Poll();
                                            }
                                            return new KeyValuePair<string, Observation>(n, o);
                                        }
                                    })
                                    .ToList();
            var snap = new RoundSnapshot(DateTime.Now, since, observations, expected);
            foreach (var r in snap.Rows)
            {
                ActivityLog.Info(r.Name, $"verify: {r.Status.Label}" + (r.Status.Note != "" ? $" ({r.Status.Note})" : "") + (r.Status.IsClean ? ", clean" : ""));
                Print(r);
            }

            if (csv != null) File.WriteAllText(csv, RoundReport.Csv(snap), new UTF8Encoding(true));
            if (html != null) File.WriteAllText(html, RoundReport.Html(snap), new UTF8Encoding(true));
            return snap.Rows.All(r => r.Status.IsClean) ? 0 : 1;
        }

        static void Print(ServerRow r)
        {
            var o = r.Observation;
            var s = r.Status;
            var flag = s.State == ServerState.Back ? (s.IsClean ? " (clean)" : " (flagged)") : "";
            Console.WriteLine();
            Console.WriteLine($"{r.Name}  {s.Label}{flag}" + (s.Note != "" ? $"  {s.Note}" : ""));
            if (o.LastBoot.HasValue) Console.WriteLine($"  Last boot   {o.LastBoot:yyyy-MM-dd HH:mm}");
            var build = RoundReport.BuildChange(r);
            if (build != "") Console.WriteLine($"  Build       {build}");
            if (s.State == ServerState.Booting && o.WmiHResult != 0) Console.WriteLine($"  {ErrorCatalog.Summary(o.WmiHResult)}: {ErrorCatalog.Lookup(o.WmiHResult)[0].Description}");
            var v = o.Verification != null && o.Verification.Boot == o.LastBoot ? o.Verification : null;
            if (v == null) return;
            foreach (var u in v.Updates)
            {
                Console.WriteLine($"  {(u.Succeeded ? "Installed" : v.Retrying(u) ? "Retrying " : "FAILED   ")}   {u.Time:dd-MM HH:mm} {u.Title}");
                if (u.Succeeded) continue;
                var e = ErrorCatalog.Lookup(u.ErrorCode)[0];
                Console.WriteLine($"              {ErrorCatalog.Summary(u.ErrorCode)}: {e.Description}");
                if (e.Mitigation != "") Console.WriteLine($"              What to do: {e.Mitigation}");
            }
            foreach (var x in v.Restarts) Console.WriteLine($"  Restart     {RoundReport.RestartLine(x)}");
            if (v.UnexpectedShutdown) Console.WriteLine("  Unexpected shutdown logged (event 6008)");
            if (!v.ServicesChecked) Console.WriteLine("  Services    not checked yet: up less than 5 min");
            foreach (var i in r.UnexpectedStops) Service("Service", RoundReport.ServiceLine(i), i);
            foreach (var x in r.ExpectedStops) Service("Expected", RoundReport.ExpectedLine(x), x.Issue);
            var read = o.EarlierBoots;
            if (read != null)
                Console.WriteLine("  Earlier     " + (read.Error != null ? $"reading earlier boots failed: {read.Error}" : $"System log read back to {read.From:yyyy-MM-dd}"));
            foreach (var removed in v.RemovedServices) Console.WriteLine($"  Removed     {RoundReport.RemovedLine(removed)} (marked for deletion before the restart)");
        }

        static void Service(string label, string line, ServiceIssue issue)
        {
            Console.WriteLine($"  {label,-12}{line}");
            if (issue.Failure != null && issue.Failure.Advice != "") Console.WriteLine($"              {issue.Failure.Advice}");
        }

        static void Line(string label, CheckOutcome c)
        {
            var state = c.State == CheckStatus.Ok ? "ok" : c.State == CheckStatus.Failed ? "FAILED" : c.State.ToString().ToLowerInvariant();
            var code = c.State == CheckStatus.Failed && c.HResult != 0 ? $" ({ErrorCatalog.Summary(c.HResult)})" : "";
            Console.WriteLine($"  {label,-7}{state,-8}{c.Detail}{code}");
            if (c.State == CheckStatus.Failed && c.HResult != 0)
                Console.WriteLine($"  {"",-15}{ErrorCatalog.Lookup(c.HResult)[0].Description}");
        }

        static int Error(IReadOnlyList<string> args)
        {
            if (args.Count == 0) { Console.Error.WriteLine("usage: wugrid-cli error CODE [CODE ...]   (0x800F0840, 800F0840 or -2146498496)"); return 2; }
            var status = 0;
            foreach (var text in args)
            {
                if (!ErrorCatalog.TryParse(text, out var code)) { Console.Error.WriteLine($"not an error code: {text}"); status = 2; continue; }
                foreach (var e in ErrorCatalog.Lookup(code))
                {
                    Console.WriteLine($"{e.Hex}  {(e.Name == "" ? "(no name)" : e.Name)}  [{e.Family}]");
                    Console.WriteLine($"  {e.Description}");
                    if (e.Mitigation != "") Console.WriteLine($"  What to do: {e.Mitigation}");
                    Console.WriteLine($"  Source: {string.Join("; ", e.Source.Split(',').Select(s => ErrorCatalog.Sources.TryGetValue(s, out var src) ? src.Title : s))}");
                    Console.WriteLine();
                }
            }
            return status;
        }
    }
}
