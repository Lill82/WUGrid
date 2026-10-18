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

namespace WUGrid.Cli
{
    static class Program
    {
        static int Main(string[] args)
        {
            // Redirected output (logs, pipes) gets UTF-8, so localized remote messages survive; the console's code page is left alone
            if (Console.IsOutputRedirected) Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });

            var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            var rest = args.Skip(1).ToList();
            switch (command)
            {
                case "check": return Check(rest);
                case "verify": return Verify(rest);
                case "watch": return WatchCommand.Run(rest);
                case "error": return Error(rest);
                default:
                    Console.WriteLine($"wugrid-cli {Assembly.GetExecutingAssembly().GetName().Version.ToString(3)}");
                    Console.WriteLine();
                    Console.WriteLine("  wugrid-cli watch [SERVER ...] [--list FILE] [--since DATETIME] [--headless] [--state FILE] [--events FILE]");
                    Console.WriteLine("                                     read-only live round: console grid (q quits), or --headless: one");
                    Console.WriteLine("                                     line per change and alarm until all are back clean (watch --help)");
                    Console.WriteLine("  wugrid-cli check SERVER [SERVER ...]   read-only reachability: DNS, ping, WMI over DCOM, admin$");
                    Console.WriteLine("  wugrid-cli verify SERVER [SERVER ...] [--since 2026-10-17T15:00] [--html report.html] [--csv report.csv]");
                    Console.WriteLine("                                     read-only: restarted after the cut-off? update results, who");
                    Console.WriteLine("                                     restarted it, services not running (default cut-off: today 00:00)");
                    Console.WriteLine("  wugrid-cli error CODE [CODE ...]       explain CBS / Windows Update / WMI / Win32 error codes");
                    Console.WriteLine();
                    Console.WriteLine("WUGrid.exe is the live grid with Servers, Reboot and Report windows; --demo plays a simulated round in either.");
                    return command == "" || command == "help" || command == "-h" || command == "--help" ? 0 : 2;
            }
        }

        static int Check(IReadOnlyList<string> args)
        {
            var servers = ServerList.Parse(string.Join(" ", args));
            if (servers.Count == 0) { Console.Error.WriteLine("usage: wugrid-cli check SERVER [SERVER ...]"); return 2; }
            var results = servers.AsParallel().AsOrdered().WithDegreeOfParallelism(Math.Min(16, servers.Count))
                                 .Select(s => AccessCheck.Run(s, TimeSpan.FromSeconds(15))).ToList();
            foreach (var r in results)
            {
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
            string csv = null, html = null;
            var names = new List<string>();
            for (var i = 0; i < args.Count; i++)
            {
                if (args[i] == "--since" && i + 1 < args.Count)
                {
                    if (!DateTime.TryParse(args[++i], out since)) { Console.Error.WriteLine($"--since: not a date/time: {args[i]}"); return 2; }
                }
                else if (args[i] == "--csv" && i + 1 < args.Count) csv = args[++i];
                else if (args[i] == "--html" && i + 1 < args.Count) html = args[++i];
                else names.AddRange(ServerList.Parse(args[i]));
            }
            if (names.Count == 0) { Console.Error.WriteLine("usage: wugrid-cli verify SERVER [SERVER ...] [--since DATETIME] [--html FILE] [--csv FILE]"); return 2; }

            Console.WriteLine($"Cut-off {since:yyyy-MM-dd HH:mm}: a server counts as restarted when it booted after it.");
            var observations = names.Distinct(StringComparer.OrdinalIgnoreCase).AsParallel().AsOrdered().WithDegreeOfParallelism(Math.Min(16, names.Count))
                                    .Select(n =>
                                    {
                                        using (var probe = new ServerProbe(n, TimeSpan.FromSeconds(15), () => since))
                                            return new KeyValuePair<string, Observation>(n, probe.Poll());
                                    })
                                    .ToList();
            var snap = new RoundSnapshot(DateTime.Now, since, observations);
            foreach (var r in snap.Rows) Print(r);

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
                Console.WriteLine($"  {(u.Succeeded ? "Installed" : "FAILED   ")}   {u.Time:dd-MM HH:mm} {u.Title}");
                if (u.Succeeded) continue;
                var e = ErrorCatalog.Lookup(u.ErrorCode)[0];
                Console.WriteLine($"              {ErrorCatalog.Summary(u.ErrorCode)}: {e.Description}");
                if (e.Mitigation != "") Console.WriteLine($"              What to do: {e.Mitigation}");
            }
            foreach (var x in v.Restarts) Console.WriteLine($"  Restart     {RoundReport.RestartLine(x)}");
            if (v.UnexpectedShutdown) Console.WriteLine("  Unexpected shutdown logged (event 6008)");
            if (!v.ServicesChecked) Console.WriteLine("  Services    not checked yet: up less than 5 min");
            foreach (var i in v.StoppedServices) Console.WriteLine($"  Service     {RoundReport.ServiceLine(i)}");
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
