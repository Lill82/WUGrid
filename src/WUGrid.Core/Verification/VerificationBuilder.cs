using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace WUGrid.Core.Verification
{
    /// <summary>
    /// Turns what was read from a server (System log records since the cut-off, services, build, and on request the boots
    /// and service failures before it) into a <see cref="ServerVerification"/>. Pure, so every event layout it relies on is pinned by a test. Decides on event
    /// IDs, sources and data fields, never on message text (servers can be in any language).
    /// </summary>
    public static class VerificationBuilder
    {
        public const string WindowsUpdateClient = "Microsoft-Windows-WindowsUpdateClient";
        public const string User32 = "User32";
        public const string KernelGeneral = "Microsoft-Windows-Kernel-General";
        public const string EventLog = "EventLog";
        public const string ServiceControlManager = "Service Control Manager";

        /// <summary>Service Control Manager events that say why a service is not running: 7000 failed to start, 7001 a service
        /// it depends on failed, 7009 / 7011 timed out, 7022 hung starting, 7023 / 7024 stopped with an error, 7031 / 7034
        /// terminated unexpectedly, 7038 could not log on.</summary>
        public static readonly int[] ServiceFailureCodes = { 7000, 7001, 7009, 7011, 7022, 7023, 7024, 7031, 7034, 7038 };

        /// <summary>The System log events a verification reads. Event 20 is shared by several sources: filter by source too.</summary>
        public static readonly int[] EventCodes = new[] { 19, 20, 43, 44, 1074, 12, 6008, 7040 }.Concat(ServiceFailureCodes).ToArray();

        /// <summary>Windows Update's own steps: result (19/20), install and download started (43/44). Read before the restart
        /// too, for <see cref="UpdatesInFlight"/>.</summary>
        public static readonly int[] UpdateCodes = { 19, 20, 43, 44 };

        /// <summary>What the read of earlier boots looks for: the boots, and the services' failures after them.</summary>
        public static readonly int[] EarlierBootCodes = new[] { 12 }.Concat(ServiceFailureCodes).ToArray();

        /// <summary>A service failure this soon after an earlier boot counts as failing at that boot.</summary>
        public static readonly TimeSpan StartWindow = TimeSpan.FromMinutes(30);

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
        /// <param name="earlier">Boots and service failures from before the cut-off, read on request; null when not read.</param>
        public static ServerVerification Build(DateTime now, DateTime boot, string build, IEnumerable<EventRecord> events,
                                               IReadOnlyList<ServiceRecord> services, Baseline baseline, bool tiWorker,
                                               IEnumerable<EventRecord> earlier = null)
        {
            var list = events.OrderBy(e => e.Time).ThenBy(e => e.RecordNumber).ToList();
            var up = now - boot;

            var servicesChecked = services != null && up >= ServicesAfter;
            var stopped = servicesChecked ? StoppedServices(services, baseline, list, boot, earlier) : new List<ServiceIssue>();
            var removed = servicesChecked && baseline != null ? RemovedServices(services, baseline) : new List<ServiceRecord>();
            var inFlight = UpdatesInFlight(list);

            return new ServerVerification(
                now, boot, build,
                Updates(list),
                list.Where(e => e.Code == 1074 && e.Source == User32).Select(Restart).ToList(),
                list.Where(e => e.Code == 12 && e.Source == KernelGeneral).Select(BootOf).ToList(),
                list.Any(e => e.Code == 6008 && e.Source == EventLog),
                stopped, servicesChecked, servicesChecked && baseline != null,
                settled: up >= SettledAfter && !tiWorker && inFlight.Count == 0,
                removedServices: removed, updatesInFlight: inFlight);
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

        /// <summary>Updates whose latest Windows Update event is "download started" (44) or "install started" (43); a result
        /// (19/20) for the same update ends it. Matched on the update ID, whatever its braces and case.</summary>
        public static IReadOnlyList<UpdateInFlight> UpdatesInFlight(IEnumerable<EventRecord> events)
        {
            // 43, 44: [0] title, [1] update id; 44 version 0 (older builds) has no title: [0] update id, [1] revision
            var steps = events.Where(e => e.Source == WindowsUpdateClient && (e.Code == 19 || e.Code == 20 || e.Code == 43 || e.Code == 44))
                              .Select(e =>
                              {
                                  var untitled = e.Code == 44 && Guid.TryParse(e.At(0), out _);
                                  var title = untitled ? "" : e.Code == 20 ? e.At(1) : e.At(0);
                                  var id = untitled ? e.At(0) : e.Code == 20 ? e.At(2) : e.At(1);
                                  return (Event: e, Title: title, Id: Guid.TryParse(id, out var g) ? g.ToString() : id);
                              });
            return steps.GroupBy(x => x.Id != "" ? x.Id : x.Title, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.OrderBy(x => x.Event.Time).ThenBy(x => x.Event.RecordNumber).ToList())
                        .Where(g => g[g.Count - 1].Event.Code == 43 || g[g.Count - 1].Event.Code == 44)
                        .Select(g =>
                        {
                            var last = g[g.Count - 1];
                            var title = g.Select(x => x.Title).LastOrDefault(t => t != "") ?? "";
                            return new UpdateInFlight(last.Event.Time, title, KbOf(title), last.Id, last.Event.Code == 43);
                        })
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

        static List<ServiceIssue> StoppedServices(IReadOnlyList<ServiceRecord> services, Baseline baseline, List<EventRecord> events,
                                                  DateTime boot, IEnumerable<EventRecord> earlier)
        {
            var now = services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            IEnumerable<ServiceRecord> expected = baseline != null
                // Ran before the restart: should run now, whatever its start type has become
                ? baseline.RunningAutoServices.Select(kv => now.TryGetValue(kv.Key, out var s) ? s : new ServiceRecord(kv.Key, kv.Value, "Missing", ""))
                // No baseline: the classic check, Automatic but not running
                : services.Where(s => s.StartMode == "Auto");
            // 7040: [0] display name, [1] old start type, [2] new start type (both localized), [3] service name
            var changes = events.Where(e => e.Code == 7040 && e.Source == ServiceControlManager).ToList();

            var all = (earlier ?? Enumerable.Empty<EventRecord>()).Concat(events).ToList();
            var failures = all.Where(e => e.Source == ServiceControlManager && ServiceFailureCodes.Contains(e.Code))
                              .OrderBy(e => e.Time).ThenBy(e => e.RecordNumber).ToList();
            var earlierBoots = all.Where(e => e.Code == 12 && e.Source == KernelGeneral).Select(e => BootOf(e).Time)
                                  .Where(t => t < boot - TimeSpan.FromMinutes(1)).Distinct().OrderBy(t => t).ToList();

            return expected.Where(s => s.State != "Running" && !IsTransientService(s.Name))
                           .Select(s => new ServiceIssue(s, changes.LastOrDefault(e => string.Equals(e.At(3), s.Name, StringComparison.OrdinalIgnoreCase)),
                                                         FailureOf(s, boot, failures, earlierBoots)))
                           .OrderBy(i => i.Service.Name, StringComparer.OrdinalIgnoreCase)
                           .ToList();
        }

        /// <summary>Why the service is not running at this boot, and whether it failed after the boots before; null when the
        /// Service Control Manager logged no failure for it since the boot (stopped by someone, disabled…).</summary>
        static ServiceFailure FailureOf(ServiceRecord s, DateTime boot, List<EventRecord> failures, List<DateTime> earlierBoots)
        {
            var mine = failures.Where(e => Names(s, ServiceFailure.ServiceIn(e))).ToList();
            var now = mine.Where(e => e.Time >= boot).ToList();
            if (now.Count == 0) return null;
            var failed = 0;
            for (var i = 0; i < earlierBoots.Count; i++)
            {
                var from = earlierBoots[i];
                var next = i + 1 < earlierBoots.Count ? earlierBoots[i + 1] : boot;
                var to = from + StartWindow < next ? from + StartWindow : next;
                if (mine.Any(e => e.Time >= from && e.Time < to)) failed++;
            }
            return new ServiceFailure(Primary(now), earlierBoots.Count, failed, earlierBoots.Count > 0 ? earlierBoots[0] : (DateTime?)null);
        }

        static bool Names(ServiceRecord s, string name) =>
            string.Equals(name, s.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(name, s.DisplayName, StringComparison.OrdinalIgnoreCase);

        /// <summary>The latest failure, except that a 7000 "no answer in time" gives way to the 7009 logged with it, which says
        /// how long the service had.</summary>
        static EventRecord Primary(List<EventRecord> failures)
        {
            var last = failures[failures.Count - 1];
            return last.Code != 7000 ? last : failures.LastOrDefault(e => e.Code != 7000 && last.Time - e.Time <= TimeSpan.FromSeconds(10)) ?? last;
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
