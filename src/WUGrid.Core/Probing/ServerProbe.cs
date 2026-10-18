using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using WUGrid.Core.Verification;

namespace WUGrid.Core.Probing
{
    /// <summary>
    /// Polls one server, read-only: a ping, then — when it answers — last boot, TiWorker.exe and the CBS / Windows Update
    /// reboot keys over DCOM. Keeps one WMI connection between polls and drops it as soon as ping fails or a call throws
    /// (a dead DCOM connection hangs the next call). Not thread-safe: one per worker.
    ///
    /// With a cut-off it also verifies: a baseline (build, running Automatic services) the first time the server is seen
    /// not yet restarted, and once it is back a verification pass (update results, restarts, boots, services) on the first
    /// poll of each boot and every <see cref="VerifyEvery"/> until the boot settles. Event queries are the expensive part,
    /// so after the first pass of a boot only records newer than the last one seen are fetched. Earlier boots (the log
    /// before the cut-off) are read only on request, in the background over a connection of their own.
    /// </summary>
    public sealed class ServerProbe : IDisposable
    {
        const string Cbs = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing";
        const string WuAuto = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update";

        /// <summary>Time between verification passes while a restarted server settles.</summary>
        public static readonly TimeSpan VerifyEvery = TimeSpan.FromMinutes(2);

        readonly string host;
        readonly TimeSpan timeout;
        readonly Func<DateTime> since;
        readonly Ping ping = new Ping();
        ManagementScope scope;
        ManagementClass registry;
        DateTime? downSince;
        WmiResult last = WmiResult.NotYet;

        Baseline baseline;
        ServerVerification verification;
        string build, verifyError;
        readonly List<EventRecord> events = new List<EventRecord>();
        DateTime eventsSince, eventsBoot, lastPass = DateTime.MinValue;
        long lastRecord;
        volatile bool verifyRequested;
        volatile Earlier earlier;

        /// <summary>The read of earlier boots: its status, and once done its records. Replaced whole, never changed.</summary>
        sealed class Earlier
        {
            public Earlier(EarlierBootsRead status, List<EventRecord> events)
            {
                Status = status;
                Events = events;
            }

            public EarlierBootsRead Status { get; }
            public List<EventRecord> Events { get; }
        }

        /// <param name="since">Round cut-off; null polls only (no baseline, no verification).</param>
        public ServerProbe(string host, TimeSpan timeout, Func<DateTime> since = null)
        {
            this.host = host;
            this.timeout = timeout;
            this.since = since;
        }

        /// <summary>Verify again on the next poll, even if the boot has settled.</summary>
        public void RequestVerify() => verifyRequested = true;

        /// <summary>Read earlier boots in the background (minutes on a large log); the next poll after it ends applies them.
        /// <paramref name="done"/> is called when it ends. Ignored while a read is running or without a cut-off.</summary>
        public void RequestEarlierBoots(Action done = null)
        {
            if (since == null || earlier?.Status.Reading == true) return;
            var status = EarlierStatus();
            earlier = new Earlier(status, null);
            Task.Run(() =>
            {
                ReadEarlier(status);
                done?.Invoke();
            });
        }

        /// <summary>The same, in the calling thread (the CLI); the next poll applies it.</summary>
        public void ReadEarlierBoots()
        {
            if (since != null) ReadEarlier(EarlierStatus());
        }

        EarlierBootsRead EarlierStatus()
        {
            var to = since();
            return new EarlierBootsRead(to - EarlierBootsRead.Lookback, to);
        }

        void ReadEarlier(EarlierBootsRead status)
        {
            earlier = new Earlier(status, null);
            try
            {
                var events = VerificationReader.ReadEarlierBoots(Wmi.Connect(host, timeout), status.From, status.To);
                earlier = new Earlier(status.Done(null), events);
            }
            catch (Exception ex)
            {
                earlier = new Earlier(status.Done($"{Wmi.FirstLine(ex.Message)} ({Errors.ErrorCatalog.Hex(Wmi.HResultOf(ex))})"), null);
            }
            verifyRequested = true;
        }

        /// <param name="interim">Told "answers ping, no WMI yet" before the first connection after start or after an
        /// outage, which can take a while. Not after a failed attempt: that error stays shown until WMI answers.</param>
        public Observation Poll(Action<Observation> interim = null)
        {
            // A read for another cut-off does not cover the gap up to this one: ask again
            if (since != null && earlier != null && !earlier.Status.Reading && earlier.Status.To != since()) earlier = null;
            var o = Read(interim);
            o.Build = build;
            o.Baseline = baseline;
            o.Verification = verification;
            o.VerificationError = verifyError;
            o.EarlierBoots = earlier?.Status;
            return o;
        }

        Observation Read(Action<Observation> interim)
        {
            var o = new Observation { Ping = Pings(out var unresolved), NameUnresolved = unresolved };
            if (o.Ping == false)
            {
                downSince = downSince ?? DateTime.Now;
                o.DownSince = downSince;
                last = WmiResult.NotYet;
                Drop();
                return o;
            }
            downSince = null;
            try
            {
                if (scope == null)
                {
                    if (last == WmiResult.NotYet) interim?.Invoke(new Observation { Ping = true, Build = build, Baseline = baseline, Verification = verification });
                    scope = Wmi.Connect(host, timeout);
                    registry = Wmi.Registry(scope);
                }
                ReadState(scope, registry, timeout, o);
            }
            catch (Exception ex)
            {
                o.Wmi = WmiResult.Failed;
                o.WmiError = Wmi.FirstLine(ex.Message);
                o.WmiHResult = Wmi.HResultOf(ex);
                Drop();
            }
            last = o.Wmi;
            if (o.Wmi == WmiResult.Ok && since != null)
            {
                // A failed check never fails the poll: the grid keeps working, the details show why
                try { Verify(o.LastBoot.Value, o.TiWorker); }
                catch (Exception ex)
                {
                    verifyError = $"{Wmi.FirstLine(ex.Message)} ({Errors.ErrorCatalog.Hex(Wmi.HResultOf(ex))})";
                    lastPass = DateTime.Now;
                }
            }
            return o;
        }

        /// <summary>Processes that mean update or install work is under way; one query reads them all.</summary>
        public static readonly string[] BusyProcessNames =
            { "TiWorker.exe", "TrustedInstaller.exe", "wusa.exe", "dism.exe", "DismHost.exe", "msiexec.exe", "SetupHost.exe" };

        /// <summary>The per-poll state: last boot, busy processes, CBS / Windows Update reboot keys. Shared with the
        /// restart action, which re-reads it over its own connection just before acting.</summary>
        internal static void ReadState(ManagementScope scope, ManagementClass registry, TimeSpan timeout, Observation o)
        {
            o.LastBoot = Wmi.Query(scope, "SELECT LastBootUpTime FROM Win32_OperatingSystem", timeout)
                            .Select(mo => ManagementDateTimeConverter.ToDateTime((string)mo["LastBootUpTime"]))
                            .First();
            var names = string.Join(" OR ", BusyProcessNames.Select(n => $"Name='{n}'"));
            o.BusyProcesses = Wmi.Query(scope, $"SELECT Name FROM Win32_Process WHERE {names}", timeout)
                                 .Select(mo => (string)mo["Name"])
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(n => Array.FindIndex(BusyProcessNames, b => string.Equals(b, n, StringComparison.OrdinalIgnoreCase)))
                                 .ToArray();
            o.TiWorker = o.BusyProcesses.Contains("TiWorker.exe", StringComparer.OrdinalIgnoreCase);
            var cbs = Wmi.SubKeys(registry, Cbs, timeout);
            o.CbsRebootPending = cbs.Contains("RebootPending");
            o.CbsPackagesPending = cbs.Contains("PackagesPending") ? Wmi.SubKeys(registry, Cbs + @"\PackagesPending", timeout).Length : 0;
            o.WuRebootRequired = Wmi.SubKeys(registry, WuAuto, timeout).Contains("RebootRequired");
            o.Wmi = WmiResult.Ok;
        }

        void Verify(DateTime boot, bool tiWorker)
        {
            var cut = since();
            var now = DateTime.Now;
            if (boot < cut)
            {
                if (baseline == null)
                {
                    build = VerificationReader.ReadBuild(registry, timeout);
                    var before = VerificationReader.ReadServices(scope, timeout);
                    // A service a role or application removal deleted while it still ran goes at the next boot: not expected back
                    var marked = VerificationReader.ReadMarkedForDeletion(registry, VerificationBuilder.BaselineCandidates(before), timeout);
                    baseline = VerificationBuilder.BuildBaseline(now, build, before, marked);
                }
                return;
            }

            var due = verifyRequested || verification == null || verification.Boot != boot
                      || (!verification.Settled && now - lastPass >= VerifyEvery);
            if (!due) return;
            verifyRequested = false;
            lastPass = now;

            // A new boot or cut-off reads the log again from the cut-off (also covers a log cleared in between)
            if (cut != eventsSince || boot != eventsBoot)
            {
                events.Clear();
                lastRecord = 0;
                eventsSince = cut;
                eventsBoot = boot;
            }
            var fresh = VerificationReader.ReadEvents(scope, cut, lastRecord);
            events.AddRange(fresh);
            if (fresh.Count > 0) lastRecord = Math.Max(lastRecord, fresh.Max(e => e.RecordNumber));
            build = VerificationReader.ReadBuild(registry, timeout);
            var services = now - boot >= VerificationBuilder.ServicesAfter ? VerificationReader.ReadServices(scope, timeout) : null;
            var read = earlier;
            var older = read != null && read.Status.To == cut ? read.Events : null;
            verification = VerificationBuilder.Build(now, boot, build, events, services, baseline, tiWorker, older);
            verifyError = null;
        }

        bool Pings(out bool unresolved)
        {
            unresolved = false;
            try { return ping.Send(host, 1000)?.Status == IPStatus.Success; }
            catch (PingException ex)
            {
                unresolved = ex.InnerException is System.Net.Sockets.SocketException;
                return false;
            }
        }

        void Drop()
        {
            registry?.Dispose();
            registry = null;
            scope = null;
        }

        public void Dispose()
        {
            Drop();
            ping.Dispose();
        }
    }
}
