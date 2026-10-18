using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using static WUGrid.Core.Demo.DemoScript;

namespace WUGrid.Core.Demo
{
    /// <summary>
    /// A simulated restart round for trying the grid without touching any server. The scripts replay patterns from a
    /// real 54-server round (docs/FIELD-NOTES.md): servicing before shutdown, access denied / RPC unavailable while
    /// booting, chained TrustedInstaller reboots, component cleanup, a server down too long, one that needs a second
    /// restart, owners who restart late, a cumulative update that fails, a service its owner left disabled. Server names
    /// are made up. Runs on its own clock, faster than real time.
    /// </summary>
    public sealed class DemoRound : IRoundSource
    {
        public static readonly TimeSpan Length = TimeSpan.FromMinutes(75);

        readonly IReadOnlyList<DemoScript> scripts = Scenario();
        readonly Stopwatch clock = Stopwatch.StartNew();
        TimeSpan played, lastReal;

        public DemoRound(double speed = 30)
        {
            Speed = speed;
            Since = DateTime.Today.AddHours(15);
        }

        public DateTime Since { get; }

        /// <summary>Simulated seconds per real second.</summary>
        public double Speed { get; set; }

        public bool Paused { get; set; }

        public RoundSnapshot Snapshot()
        {
            var real = clock.Elapsed;
            if (!Paused) played += TimeSpan.FromTicks((long)((real - lastReal).Ticks * Speed));
            lastReal = real;
            return At(played);
        }

        public void Restart() => Seek(TimeSpan.Zero);

        /// <summary>Carry on from <paramref name="elapsed"/> after the cut-off.</summary>
        public void Seek(TimeSpan elapsed)
        {
            played = elapsed;
            lastReal = clock.Elapsed;
        }

        /// <summary>The round as it stands <paramref name="elapsed"/> after the cut-off.</summary>
        public RoundSnapshot At(TimeSpan elapsed) =>
            new RoundSnapshot(Since + elapsed, Since,
                              scripts.Select(s => new KeyValuePair<string, Observation>(s.Name, s.At(elapsed.TotalMinutes, Since))));

        public void Dispose() { }

        // Minutes after the cut-off. Each phase lasts until the next one starts.
        static IReadOnlyList<DemoScript> Scenario() => new List<DemoScript>
        {
            // Quick Server 2022 boxes: servicing before shutdown, a few minutes down, servicing finishes, clean
            new DemoScript("WEB01", 96, 0.2).Installing(2).Down(4.5).Booting(6, AccessDenied).Back(6.8, tiWorker: true).Back(9),
            new DemoScript("WEB02", 96, 0.3).Installing(3).Down(5.5).Booting(7, RpcUnavailable).Back(7.6, tiWorker: true).Back(10),
            new DemoScript("MON01", 52, 0.25).Installing(7).Down(9.5).Booting(11, AccessDenied).Back(11.5, tiWorker: true).Back(13),
            new DemoScript("RDS02", 75, 0.35).Installing(10).Down(12).Booting(13, RpcUnavailable).Back(14, tiWorker: true).Back(16),
            new DemoScript("BACKUP01", 88, 0.3).Installing(1).Down(4).Booting(5.5, AccessDenied).Booting(6.2, RpcUnavailable)
                .Back(6.8, tiWorker: true).Back(9, cleanup: true).Back(20),

            // Stacked cumulative updates: comes back with packages pending, TrustedInstaller reboots it again
            new DemoScript("APP01", 110, 0.4).Installing(5).Down(8).Booting(9.5, AccessDenied).Back(10, tiWorker: true, packages: 12)
                .Down(12).Booting(13.2, RpcUnavailable).Back(13.8, tiWorker: true).Back(16, cleanup: true).Back(30),

            // Long component cleanup after the restart; its owner disabled the application's database service before the
            // restart and only re-enables it at minute 48
            new DemoScript("APP02", 110, 0.2).Installing(6).Down(9).Booting(10.5, AccessDenied).Back(11, tiWorker: true)
                .Back(14, cleanup: true).Back(40).ServiceDown("AppDbSvc", "Application Database Service", until: 48),

            // Windows Update wants one more reboot; the owner gives it one
            new DemoScript("APP03", 64, 0.3).Installing(16).Down(18).Booting(19, RpcUnavailable).Back(19.5, tiWorker: true, wu: true)
                .Back(21, wu: true).Down(26).Booting(27.5, AccessDenied).Back(28),

            // Comes back still wanting a reboot: alarm after 20 min up, then a second restart clears it
            new DemoScript("SQL02", 140, 0.25).Installing(8).Down(11).Booting(12.5, RpcUnavailable).Back(13, tiWorker: true, packages: 3)
                .Back(15, packages: 3).Down(55).Booting(56.5, AccessDenied).Back(57, tiWorker: true).Back(60),

            // Server 2016: servicing takes most of an hour, with a chained reboot
            new DemoScript("FILE01", 200, 0.4).Installing(3).Down(22).Booting(24, AccessDenied).Back(25, tiWorker: true, packages: 5)
                .Down(33).Booting(35, RpcUnavailable).Back(36, tiWorker: true).Back(45),

            // Down long enough to raise the alarm, then back
            new DemoScript("RDS01", 75, 0.2).Installing(4).Down(7).Booting(26, AccessDenied).Back(27, tiWorker: true).Back(30),

            // Nothing pending, restarted anyway by its admin: straight from NO RESTART PENDING to DOWN
            new DemoScript("TEST01", 30, 0.15).Down(15).Booting(16, AccessDenied).Back(16.5).NoUpdates(),

            // Restarted late, when their owners were ready. SQL01's stacked cumulative updates fail to commit: it comes back
            // green-looking at the old build, and only the update result says so (docs/FIELD-NOTES.md)
            new DemoScript("SQL01", 140, 0.35).Installing(25).Down(29).Booting(31, AccessDenied).Back(31.5, tiWorker: true).Back(35)
                .FailsUpdate(unchecked((int)0x800F0840)),
            new DemoScript("PRINT01", 180, 0.3).Installing(62).Down(64).Booting(65, RpcUnavailable).Back(65.5, tiWorker: true).Back(68),
        };
    }
}
