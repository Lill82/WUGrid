using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using WUGrid.Core;
using WUGrid.Core.Demo;
using WUGrid.Core.Reports;

namespace WUGrid.Cli
{
    /// <summary>
    /// <c>wugrid-cli watch</c>: the live round in the console, read-only, with the GUI's probe, classifier and tracker. A grid
    /// redrawn every second (q quits), or headless — also whenever output is redirected — one line per first state, change
    /// and alarm, stopping once every server is back clean with its checks done: what a background task follows.
    /// </summary>
    static class WatchCommand
    {
        const int MaxEvents = 500;
        static readonly Encoding Utf8 = new UTF8Encoding(false);

        public const string Usage =
            "wugrid-cli watch [SERVER ...] [options]     read-only: nothing is changed on the servers\n" +
            "\n" +
            "  SERVER ...           server names (commas, spaces or semicolons between them)\n" +
            "  --list FILE          also watch the servers in FILE, re-read whenever it changes\n" +
            "                       (no servers and no --list: the GUI's list, %APPDATA%\\WUGrid\\servers.txt)\n" +
            "  --since DATETIME     round cut-off: a server counts as restarted when it booted after it\n" +
            "                       (default: today 00:00)\n" +
            "  --expected FILE      services the operator expects not to run after the restart, one per line:\n" +
            "                       SERVICE or SERVER\\SERVICE (*\\SERVICE = every server), // why. Shown, not\n" +
            "                       flagged; re-read whenever it changes (default: the GUI's list,\n" +
            "                       %APPDATA%\\WUGrid\\expected.txt, when it exists)\n" +
            "  --interval SECONDS   time between polls of each server (default 5)\n" +
            "  --headless           no grid: one line per first state, change and alarm; stops once every\n" +
            "                       server is back clean with its checks done (also when output is redirected)\n" +
            "  --keep-running       with --headless: keep watching after that\n" +
            "  --state FILE         write the round as JSON on every refresh, for other tools\n" +
            "  --events FILE        append every event to FILE, one JSON object per line\n" +
            "  --demo               play the simulated round instead; no server is contacted\n" +
            "  --log                keep an activity log: what changed and every error, for troubleshooting\n" +
            "                       (%APPDATA%\\WUGrid\\logs, kept 30 days; on without --log when turned on in\n" +
            "                       WUGrid.exe, --no-log turns it off); --log-dir DIR puts it elsewhere\n" +
            "\n" +
            "Exit code: 0 when every server is back clean with its checks done, 1 otherwise, 2 for usage errors.";

        public static int Run(IReadOnlyList<string> args)
        {
            var since = DateTime.Today;
            var interval = TimeSpan.FromSeconds(5);
            string listPath = null, statePath = null, eventsPath = null, expectedPath = null;
            bool headless = false, keepRunning = false, demo = false;
            var names = new List<string>();
            for (var i = 0; i < args.Count; i++)
            {
                var a = args[i].ToLowerInvariant();
                var hasValue = i + 1 < args.Count;
                if (a == "--help" || a == "-h") { Console.WriteLine(Usage); return 0; }
                if (a == "--since" && hasValue)
                {
                    if (!DateTime.TryParse(args[++i], out since)) return Fail($"--since: not a date/time: {args[i]}");
                }
                else if (a == "--interval" && hasValue)
                {
                    if (!int.TryParse(args[++i], out var s) || s < 1 || s > 600) return Fail($"--interval: seconds from 1 to 600, not {args[i]}");
                    interval = TimeSpan.FromSeconds(s);
                }
                else if (a == "--list" && hasValue) listPath = args[++i];
                else if (a == "--state" && hasValue) statePath = args[++i];
                else if (a == "--events" && hasValue) eventsPath = args[++i];
                else if (a == "--expected" && hasValue) expectedPath = args[++i];
                else if (a == "--headless") headless = true;
                else if (a == "--keep-running") keepRunning = true;
                else if (a == "--demo") demo = true;
                else if (a.StartsWith("--")) return Fail($"unknown option or missing value: {args[i]}");
                else names.AddRange(ServerList.Parse(args[i]));
            }

            if (demo && (names.Count > 0 || listPath != null)) return Fail("--demo plays a simulated round: give it no servers");
            var defaultList = !demo && names.Count == 0 && listPath == null;
            if (defaultList) listPath = ServerList.DefaultPath;
            try
            {
                if (listPath != null) listPath = Path.GetFullPath(listPath);
                if (expectedPath != null) expectedPath = Path.GetFullPath(expectedPath);
                foreach (var p in new[] { statePath, eventsPath }.Where(p => p != null))
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(p)));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return Fail(ex.Message);
            }
            if (listPath != null && !File.Exists(listPath))
                return Fail(defaultList ? $"no servers given, and the GUI has no saved list ({listPath})" : $"--list: no such file: {listPath}");
            if (expectedPath != null && !File.Exists(expectedPath)) return Fail($"--expected: no such file: {expectedPath}");
            if (expectedPath == null && File.Exists(ExpectedList.DefaultPath)) expectedPath = ExpectedList.DefaultPath;

            var servers = names.Concat(listPath != null ? ServerList.Load(listPath) : new string[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!demo && servers.Count == 0) return Fail($"no servers: {listPath} lists none");
            headless |= Console.IsOutputRedirected;

            DemoRound demoRound = null;
            LiveRound live = null;
            IRoundSource source;
            if (demo) source = demoRound = new DemoRound();
            else
            {
                source = live = new LiveRound(since, interval);
                live.SetServers(servers);
            }

            var stop = new ManualResetEventSlim();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
            var tracker = new RoundTracker();
            var recent = new List<RoundEvent>();
            var grid = new ConsoleGrid();
            var listStamp = listPath != null ? Stamp(listPath) : DateTime.MinValue;
            var expectedStamp = DateTime.MinValue;
            if (expectedPath != null) ReloadExpected(source, expectedPath, ref expectedStamp);
            string warning = null;
            int drawn = 0, lastWidth = 0, lastHeight = 0;
            var tick = TimeSpan.FromMilliseconds(demo ? 500 : 1000);

            string oldTitle = null;
            if (headless) Console.WriteLine(Intro(servers.Count, listPath, since, interval, demo, expectedPath));
            else Try(() =>
            {
                oldTitle = Console.Title;
                Console.Title = demo ? "WUGrid watch (demo round)" : "WUGrid watch";
                Console.Clear();
                Console.CursorVisible = false;
            });
            try
            {
                while (true)
                {
                    if (live != null && listPath != null) listStamp = Reload(live, names, listPath, listStamp);
                    var reread = expectedPath != null && ReloadExpected(source, expectedPath, ref expectedStamp);
                    var snap = source.Snapshot();
                    var events = tracker.Update(snap).ToList();
                    if (reread) events.Insert(0, new RoundEvent(snap.Now, RoundEventKind.Expected, null, $"expected stops re-read: {source.Expected.Count} ({expectedPath})"));
                    var problem = Save(statePath, eventsPath, snap, events, tracker.AllClean, live?.Interval);
                    foreach (var e in events) ActivityLog.Info(e.Server, e.Text);
                    if (problem != null && problem != warning) ActivityLog.Warn(null, problem);

                    if (headless)
                    {
                        foreach (var e in events) Console.WriteLine(e);
                        if (problem != null && problem != warning) Console.Error.WriteLine(problem);
                        warning = problem;
                        if (tracker.AllClean && !keepRunning) break;
                        if (demoRound != null && snap.Now - demoRound.Since >= DemoRound.Length) { Console.WriteLine("Demo round over."); break; }
                        if (stop.Wait(tick)) break;
                        continue;
                    }

                    warning = problem;
                    recent.AddRange(events);
                    if (recent.Count > MaxEvents) recent.RemoveRange(0, recent.Count - MaxEvents);
                    var width = Math.Max(20, Console.WindowWidth - 1);
                    var height = Math.Max(5, Console.WindowHeight);
                    if (width != lastWidth || height != lastHeight)
                    {
                        Try(() => { Console.Clear(); Console.CursorVisible = false; });
                        (lastWidth, lastHeight, drawn) = (width, height, 0);
                    }
                    var title = $"WUGrid watch   {snap.Now:HH:mm:ss}   since {snap.Since:dd-MM HH:mm}   {Servers(snap.Rows.Count)}   " +
                                (demo ? $"demo, {demoRound.Speed:0}x real time" : $"poll {interval.TotalSeconds:0} s") + "   read-only   q quits";
                    drawn = Draw(grid.Frame(snap, recent, title, width, height, warning), width, drawn);
                    if (WaitOrQuit(stop, tick)) break;
                }
            }
            finally
            {
                if (!headless)
                    Try(() =>
                    {
                        Console.ResetColor();
                        Console.SetCursorPosition(0, drawn);
                        Console.CursorVisible = true;
                        if (oldTitle != null) Console.Title = oldTitle;
                    });
                source.Dispose();   // workers stuck in a DCOM call are background threads: they do not hold up the exit
            }
            return tracker.AllClean ? 0 : 1;
        }

        static string Intro(int count, string listPath, DateTime since, TimeSpan interval, bool demo, string expectedPath) =>
            (demo
                ? $"Demo round: simulated, no servers are contacted ({DemoRound.Length.TotalMinutes:0} min of round time). Ctrl+C stops."
                : $"Watching {Servers(count)}" + (listPath != null ? $" ({listPath}, re-read when it changes)" : "") +
                  $", cut-off {since:yyyy-MM-dd HH:mm}, polled every {interval.TotalSeconds:0} s. Read-only. Ctrl+C stops.") +
            (expectedPath != null ? $" Expected stops from {expectedPath}, re-read when it changes." : "");

        static string Servers(int n) => n == 1 ? "1 server" : $"{n} servers";

        /// <summary>Re-reads the list file when it has changed; a file still being saved is tried again next time.</summary>
        static DateTime Reload(LiveRound live, IEnumerable<string> names, string listPath, DateTime known)
        {
            var stamp = Stamp(listPath);
            if (stamp == known) return known;
            try
            {
                live.SetServers(names.Concat(ServerList.Load(listPath)));
                return stamp;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ActivityLog.Warn(null, $"server list not re-read from {listPath}; trying again", ex);
                return known;
            }
        }

        static DateTime Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

        /// <summary>Re-reads the expected-stops file when it has changed (also the first time); true when it was read.</summary>
        static bool ReloadExpected(IRoundSource source, string path, ref DateTime known)
        {
            var stamp = Stamp(path);
            if (stamp == known) return false;
            try
            {
                source.Expected = ExpectedList.Load(path);
                known = stamp;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ActivityLog.Warn(null, $"expected stops not re-read from {path}; trying again", ex);
                return false;
            }
        }

        /// <returns>Why a file could not be written, or null.</returns>
        static string Save(string statePath, string eventsPath, RoundSnapshot snap, IReadOnlyList<RoundEvent> events, bool allClean, TimeSpan? interval)
        {
            string problem = null;
            if (eventsPath != null && events.Count > 0)
            {
                try { File.AppendAllText(eventsPath, string.Concat(events.Select(e => RoundJson.Event(e) + "\n")), Utf8); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { problem = $"Cannot write {eventsPath}: {ex.Message}"; }
            }
            if (statePath != null)
            {
                try { WriteState(statePath, RoundJson.State(snap, allClean, interval)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { problem = $"Cannot write {statePath}: {ex.Message}"; }
            }
            return problem;
        }

        // Written aside and swapped in, so a reader never sees half a file; a reader holding it open gets a plain overwrite
        static void WriteState(string path, string json)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, Utf8);
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (IOException)
            {
                File.WriteAllText(path, json, Utf8);
                File.Delete(tmp);
            }
        }

        /// <returns>The number of lines now on screen.</returns>
        static int Draw(IReadOnlyList<IReadOnlyList<TextRun>> lines, int width, int drawn)
        {
            Console.SetCursorPosition(0, 0);
            foreach (var line in lines)
            {
                var used = 0;
                foreach (var run in line)
                {
                    Console.ForegroundColor = run.Color;
                    Console.Write(run.Text);
                    used += run.Text.Length;
                }
                Console.WriteLine(new string(' ', Math.Max(0, width - used)));
            }
            for (var i = lines.Count; i < drawn; i++) Console.WriteLine(new string(' ', width));   // left by a longer frame
            Console.ResetColor();
            return lines.Count;
        }

        static bool WaitOrQuit(ManualResetEventSlim stop, TimeSpan tick)
        {
            var until = DateTime.UtcNow + tick;
            while (DateTime.UtcNow < until)
            {
                if (stop.Wait(100)) return true;
                if (Console.IsInputRedirected) continue;
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    if (key == ConsoleKey.Q || key == ConsoleKey.Escape) return true;
                }
            }
            return false;
        }

        // Some hosts refuse cursor and clear calls; the grid still draws
        static void Try(Action a)
        {
            try { a(); }
            catch (IOException) { }
            catch (ArgumentOutOfRangeException) { }
        }

        static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            Console.Error.WriteLine("usage: wugrid-cli watch [SERVER ...] [--list FILE] [--since DATETIME] [--expected FILE] [--interval SECONDS]");
            Console.Error.WriteLine("                        [--headless [--keep-running]] [--state FILE] [--events FILE] [--demo]");
            Console.Error.WriteLine("       wugrid-cli watch --help");
            return 2;
        }
    }
}
