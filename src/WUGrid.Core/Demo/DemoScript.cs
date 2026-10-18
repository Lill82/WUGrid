using System;
using System.Collections.Generic;
using System.Linq;
using WUGrid.Core.Verification;

namespace WUGrid.Core.Demo
{
    /// <summary>One server's timeline in the demo: phases that start at a given minute after the cut-off.</summary>
    sealed class DemoScript
    {
        enum Kind { Unknown, Up, Down, Booting }

        sealed class Phase
        {
            public double At;
            public Kind Kind;
            public bool TiWorker, CbsMark, Wu;
            public int Packages;
            public string Error;
            public int HResult;
            public double? BootAt;   // null = still on the boot from before the round
        }

        public static readonly (string Text, int HResult) AccessDenied = ("Access is denied.", unchecked((int)0x80070005));
        public static readonly (string Text, int HResult) RpcUnavailable = ("The RPC server is unavailable.", unchecked((int)0x800706BA));

        const string Cu = "2026-09 Cumulative Update for Windows Server 2019 for x64-based Systems (KB5900001)";
        const string CuId = "{6f1e0c2a-0000-4d00-9000-000000000001}";

        readonly List<Phase> phases = new List<Phase>();
        readonly double daysUp;
        string buildAfter = "17763.9010";
        int updateError;
        bool hasUpdate = true;   // also: packages pending before the restart, so it shows RESTART PENDING
        (string Name, string Display, double Until)? serviceDown;
        (string Name, string Display)? serviceRemoved;

        /// <param name="daysUp">Uptime before the round, for the last boot shown before the restart.</param>
        /// <param name="firstAnswer">Minute the first poll answers; "checking..." until then.</param>
        public DemoScript(string name, double daysUp, double firstAnswer)
        {
            Name = name;
            this.daysUp = daysUp;
            phases.Add(new Phase { At = 0, Kind = Kind.Unknown });
            phases.Add(new Phase { At = firstAnswer, Kind = Kind.Up });
        }

        public string Name { get; }

        public DemoScript Installing(double at) => Add(new Phase { At = at, Kind = Kind.Up, TiWorker = true });

        public DemoScript Down(double at) => Add(new Phase { At = at, Kind = Kind.Down });

        public DemoScript Booting(double at, (string Text, int HResult) error) =>
            Add(new Phase { At = at, Kind = Kind.Booting, Error = error.Text, HResult = error.HResult });

        public DemoScript Back(double at, bool tiWorker = false, bool cleanup = false, int packages = 0, bool wu = false) =>
            Add(new Phase { At = at, Kind = Kind.Up, TiWorker = tiWorker, CbsMark = cleanup || packages > 0, Packages = packages, Wu = wu });

        /// <summary>The cumulative update fails: Windows Update event 20 with this code, and the build stays old.</summary>
        public DemoScript FailsUpdate(int errorCode)
        {
            updateError = errorCode;
            buildAfter = "17763.8880";
            return this;
        }

        public DemoScript NoUpdates()
        {
            hasUpdate = false;
            buildAfter = "17763.8644";
            return this;
        }

        /// <summary>The owner stopped and disabled an application service before the restart and re-enables it at <paramref name="until"/>.</summary>
        public DemoScript ServiceDown(string name, string display, double until)
        {
            serviceDown = (name, display, until);
            return this;
        }

        /// <summary>An application removal deleted a running service before the round (marked for deletion): gone after the restart, as planned.</summary>
        public DemoScript ServiceRemoved(string name, string display)
        {
            serviceRemoved = (name, display);
            return this;
        }

        DemoScript Add(Phase p)
        {
            var prev = phases[phases.Count - 1];
            if (p.At <= prev.At) throw new ArgumentException($"{Name}: phases must be in time order ({p.At} after {prev.At})");
            // The server booted shortly before it first answers after an outage
            if (p.Kind != Kind.Down) p.BootAt = prev.Kind == Kind.Down ? p.At - 0.3 : prev.BootAt;
            phases.Add(p);
            return this;
        }

        public Observation At(double minute, DateTime since)
        {
            var p = phases[0];
            foreach (var ph in phases)
            {
                if (ph.At > minute) break;
                p = ph;
            }
            switch (p.Kind)
            {
                case Kind.Unknown:
                    return new Observation();
                case Kind.Down:
                    return new Observation { Ping = false, DownSince = since.AddMinutes(p.At), Baseline = Baseline(since) };
                case Kind.Booting:
                    return new Observation { Ping = true, Wmi = WmiResult.Failed, WmiError = p.Error, WmiHResult = p.HResult, Baseline = Baseline(since) };
                default:
                    var restarted = p.BootAt.HasValue;
                    return new Observation
                    {
                        Ping = true,
                        Wmi = WmiResult.Ok,
                        LastBoot = restarted ? since.AddMinutes(p.BootAt.Value) : since.AddDays(-daysUp),
                        TiWorker = p.TiWorker,
                        BusyProcesses = p.TiWorker ? new[] { "TiWorker.exe", "TrustedInstaller.exe" } : new string[0],   // servicing runs both
                        CbsRebootPending = p.CbsMark || (!restarted && hasUpdate),
                        CbsPackagesPending = restarted || !hasUpdate ? p.Packages : 40,
                        WuRebootRequired = p.Wu,
                        Build = restarted ? buildAfter : "17763.8644",
                        Baseline = Baseline(since),
                        Verification = restarted ? Verify(minute, since, p) : null,
                    };
            }
        }

        Baseline Baseline(DateTime since)
        {
            var services = new Dictionary<string, string> { ["W32Time"] = "Windows Time", ["Winmgmt"] = "Windows Management Instrumentation" };
            if (serviceDown.HasValue) services[serviceDown.Value.Name] = serviceDown.Value.Display;
            var marked = new Dictionary<string, string>();
            if (serviceRemoved.HasValue) marked[serviceRemoved.Value.Name] = serviceRemoved.Value.Display;
            return new Baseline(since.AddMinutes(-1), "17763.8644", services, marked);
        }

        // What a verification pass would have found at this minute: update result two minutes after the boot, every
        // restart so far (the first by the admin, later ones chained by TrustedInstaller), the boots, the service
        ServerVerification Verify(double minute, DateTime since, Phase p)
        {
            var boot = since.AddMinutes(p.BootAt.Value);
            var up = minute - p.BootAt.Value;
            var updates = new List<UpdateResult>();
            if (hasUpdate && up >= 2)
                updates.Add(new UpdateResult(boot.AddMinutes(2), Cu, "KB5900001", CuId, updateError == 0, updateError));

            var downs = phases.Where(x => x.Kind == Kind.Down && x.At <= minute).Select(x => x.At).ToList();
            var restarts = downs.Select((at, i) => i == 0
                ? new RestartRecord(since.AddMinutes(at - 0.2), @"C:\Windows\System32\wininit.exe (10.0.0.50)", @"CONTOSO\admin", "0x80020003", "Operating System: Upgrade (Planned)", "Patch round")
                : new RestartRecord(since.AddMinutes(at - 0.2), $@"C:\Windows\servicing\TrustedInstaller.exe ({Name})", @"NT AUTHORITY\SYSTEM", "0x80020003", "Operating System: Upgrade (Planned)", ""))
                .ToList();
            var boots = phases.Where(x => x.BootAt.HasValue && x.BootAt.Value <= minute).Select(x => x.BootAt.Value).Distinct()
                              .Select(b => new BootRecord(since.AddMinutes(b), buildAfter)).ToList();

            var checkedServices = up >= VerificationBuilder.ServicesAfter.TotalMinutes;
            var stopped = new List<ServiceIssue>();
            if (checkedServices && serviceDown.HasValue && minute < serviceDown.Value.Until)
            {
                var s = serviceDown.Value;
                var change = new EventRecord(VerificationBuilder.ServiceControlManager, 7040, since.AddMinutes(downs.FirstOrDefault() - 1.5), 0,
                                             @"CONTOSO\appowner", s.Display, "auto start", "disabled", s.Name);
                stopped.Add(new ServiceIssue(new ServiceRecord(s.Name, s.Display, "Stopped", "Disabled"), change));
            }
            var removed = new List<ServiceRecord>();
            if (checkedServices && serviceRemoved.HasValue)
                removed.Add(new ServiceRecord(serviceRemoved.Value.Name, serviceRemoved.Value.Display, "Missing", ""));
            return new ServerVerification(since.AddMinutes(minute), boot, buildAfter, updates, restarts, boots, false, stopped,
                                          checkedServices, true, up >= VerificationBuilder.SettledAfter.TotalMinutes && !p.TiWorker, removed);
        }
    }
}
