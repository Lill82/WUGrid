using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace WUGrid.Core.Verification
{
    /// <summary>
    /// Turns what was read from a server (System log records since the cut-off, services, build) into a
    /// <see cref="ServerVerification"/>. Pure, so every event layout it relies on is pinned by a test. Decides on event
    /// IDs, sources and data fields, never on message text (servers can be in any language).
    /// </summary>
    public static class VerificationBuilder
    {
        public const string WindowsUpdateClient = "Microsoft-Windows-WindowsUpdateClient";
        public const string User32 = "User32";
        public const string KernelGeneral = "Microsoft-Windows-Kernel-General";
        public const string EventLog = "EventLog";
        public const string ServiceControlManager = "Service Control Manager";

        /// <summary>The System log events a verification reads. Event 20 is shared by several sources: filter by source too.</summary>
        public static readonly int[] EventCodes = { 19, 20, 1074, 12, 6008, 7040 };

        /// <summary>Delayed-start services start about two minutes after boot; wait longer than that before judging.</summary>
        public static readonly TimeSpan ServicesAfter = TimeSpan.FromMinutes(5);

        /// <summary>No more checks for a boot once it has been up this long with servicing finished.</summary>
        public static readonly TimeSpan SettledAfter = TimeSpan.FromMinutes(15);

        // Services that start on a trigger or stop by themselves, so "ran before, not now" means nothing; per-user
        // services (Name_1a2b3c) get a new suffix every logon
        static readonly Regex Transient = new Regex(
            @"^(gupdate|gupdatem|edgeupdate|edgeupdatem|MicrosoftEdgeElevationService|sppsvc|RemoteRegistry|MapsBroker|CDPSvc|" +
            @"tiledatamodelsvc|WbioSrvc|clr_optimization.*|ShellHWDetection|BITS|wuauserv|UsoSvc|dmwappushservice|GoogleUpdater.*|" +
            @"WaaSMedicSvc|DoSvc|InstallService|StateRepository|TrustedInstaller|AGMService|AGSService|AdobeARMservice|" +
            @"SysmonLog|VSS|swprv|TabletInputService|WSearch|DiagTrack|SensrSvc|SCardSvr|ScDeviceEnum|WinHttpAutoProxySvc)$|_[0-9a-f]{4,}$",
            RegexOptions.IgnoreCase);

        static readonly Regex Kb = new Regex(@"\bKB\s?(\d{6,8})\b", RegexOptions.IgnoreCase);

        public static bool IsTransientService(string name) => Transient.IsMatch(name ?? "");

        /// <summary>The services a baseline expects to run again after the restart: Automatic, running, not transient.</summary>
        public static IEnumerable<ServiceRecord> BaselineCandidates(IEnumerable<ServiceRecord> services) =>
            services.Where(s => s.StartMode == "Auto" && s.State == "Running" && !IsTransientService(s.Name))
                    .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First());

        /// <summary>The baseline: build plus the Automatic services running right now, less those marked for deletion.</summary>
        /// <param name="markedForDeletion">Names of services whose registry key carries DeleteFlag (they go at the next boot);
        /// null when not read.</param>
        public static Baseline BuildBaseline(DateTime now, string build, IEnumerable<ServiceRecord> services, IEnumerable<string> markedForDeletion = null)
        {
            var marked = new HashSet<string>(markedForDeletion ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var candidates = BaselineCandidates(services).ToList();
            return new Baseline(now, build,
                candidates.Where(s => !marked.Contains(s.Name)).ToDictionary(s => s.Name, s => s.DisplayName, StringComparer.OrdinalIgnoreCase),
                candidates.Where(s => marked.Contains(s.Name)).ToDictionary(s => s.Name, s => s.DisplayName, StringComparer.OrdinalIgnoreCase));
        }

        /// <param name="events">System log records since the cut-off (any order; others are ignored).</param>
        /// <param name="services">Current services, or null when not read this time.</param>
        /// <param name="tiWorker">Servicing still running: not settled yet.</param>
        public static ServerVerification Build(DateTime now, DateTime boot, string build, IEnumerable<EventRecord> events,
                                               IReadOnlyList<ServiceRecord> services, Baseline baseline, bool tiWorker)
        {
            var list = events.OrderBy(e => e.Time).ThenBy(e => e.RecordNumber).ToList();
            var up = now - boot;

            var servicesChecked = services != null && up >= ServicesAfter;
            var stopped = servicesChecked ? StoppedServices(services, baseline, list) : new List<ServiceIssue>();
            var removed = servicesChecked && baseline != null ? RemovedServices(services, baseline) : new List<ServiceRecord>();

            return new ServerVerification(
                now, boot, build,
                Updates(list),
                list.Where(e => e.Code == 1074 && e.Source == User32).Select(Restart).ToList(),
                list.Where(e => e.Code == 12 && e.Source == KernelGeneral).Select(BootOf).ToList(),
                list.Any(e => e.Code == 6008 && e.Source == EventLog),
                stopped, servicesChecked, servicesChecked && baseline != null,
                settled: up >= SettledAfter && !tiWorker,
                removedServices: removed);
        }

        /// <summary>Last result per update (a failure followed by a success is a success).</summary>
        public static IReadOnlyList<UpdateResult> Updates(IEnumerable<EventRecord> events)
        {
            var results = new List<UpdateResult>();
            foreach (var e in events.Where(e => e.Source == WindowsUpdateClient && (e.Code == 19 || e.Code == 20)))
            {
                // 19: [0] title, [1] update id    20: [0] error code, [1] title, [2] update id
                var failed = e.Code == 20;
                var title = failed ? e.At(1) : e.At(0);
                var id = failed ? e.At(2) : e.At(1);
                var code = failed && Errors.ErrorCatalog.TryParse(e.At(0), out var c) ? c : 0;
                results.Add(new UpdateResult(e.Time, title, KbOf(title), id, !failed, code));
            }
            return results.GroupBy(u => u.UpdateId != "" ? u.UpdateId : u.Title, StringComparer.OrdinalIgnoreCase)
                          .Select(g => g.OrderBy(u => u.Time).Last())
                          .OrderBy(u => u.Time)
                          .ToList();
        }

        public static string KbOf(string title)
        {
            var m = Kb.Match(title ?? "");
            return m.Success ? "KB" + m.Groups[1].Value : "";
        }

        // 1074: [0] process (machine), [1] computer, [2] reason text, [3] reason code, [4] type, [5] comment, [6] user
        static RestartRecord Restart(EventRecord e) =>
            new RestartRecord(e.Time, e.At(0), e.At(6) != "" ? e.At(6) : e.User, e.At(3), e.At(2), e.At(5));

        // Kernel-General 12: [0] major, [1] minor, [2] build, [3] UBR (QFE), ..., [6] boot time (UTC, ISO 8601)
        static BootRecord BootOf(EventRecord e)
        {
            var time = DateTime.TryParse(e.At(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) && t.Kind == DateTimeKind.Utc
                ? t.ToLocalTime() : e.Time;
            var build = int.TryParse(e.At(2), out var b) && int.TryParse(e.At(3), out var ubr) && b > 0 ? $"{b}.{ubr}" : null;
            return new BootRecord(time, build);
        }

        static List<ServiceIssue> StoppedServices(IReadOnlyList<ServiceRecord> services, Baseline baseline, List<EventRecord> events)
        {
            var now = services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            IEnumerable<ServiceRecord> expected = baseline != null
                // Ran before the restart: should run now, whatever its start type has become
                ? baseline.RunningAutoServices.Select(kv => now.TryGetValue(kv.Key, out var s) ? s : new ServiceRecord(kv.Key, kv.Value, "Missing", ""))
                // No baseline: the classic check, Automatic but not running
                : services.Where(s => s.StartMode == "Auto");
            // 7040: [0] display name, [1] old start type, [2] new start type (both localized), [3] service name
            var changes = events.Where(e => e.Code == 7040 && e.Source == ServiceControlManager).ToList();
            return expected.Where(s => s.State != "Running" && !IsTransientService(s.Name))
                           .Select(s => new ServiceIssue(s, changes.LastOrDefault(e => string.Equals(e.At(3), s.Name, StringComparison.OrdinalIgnoreCase))))
                           .OrderBy(i => i.Service.Name, StringComparer.OrdinalIgnoreCase)
                           .ToList();
        }

        /// <summary>Services the baseline found marked for deletion and that are not running now: gone, or still registered
        /// until their last handle closes. One running again (reinstalled) is simply running and is not listed.</summary>
        static List<ServiceRecord> RemovedServices(IReadOnlyList<ServiceRecord> services, Baseline baseline)
        {
            var now = services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            return baseline.MarkedForDeletion
                           .Select(kv => now.TryGetValue(kv.Key, out var s) ? s : new ServiceRecord(kv.Key, kv.Value, "Missing", ""))
                           .Where(s => s.State != "Running")
                           .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                           .ToList();
        }
    }
}
