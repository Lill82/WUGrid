using System;
using System.Collections.Generic;
using System.Linq;
using WUGrid.Core;
using WUGrid.Core.Verification;
using Xunit;

namespace WUGrid.Core.Tests
{
    public class RoundTrackerTests
    {
        static readonly DateTime Since = new DateTime(2026, 10, 17, 15, 0, 0);

        static Observation NotRestarted() => new Observation { Ping = true, Wmi = WmiResult.Ok, LastBoot = Since.AddDays(-30) };
        static Observation Installing() { var o = NotRestarted(); o.TiWorker = true; return o; }
        static Observation Down(double atMin) => new Observation { Ping = false, DownSince = Since.AddMinutes(atMin) };
        static Observation Checking() => new Observation { Ping = true };
        static Observation Back(double bootMin) => new Observation { Ping = true, Wmi = WmiResult.Ok, LastBoot = Since.AddMinutes(bootMin) };

        /// <summary>Back, with the checks for this boot in: update results read and (by default) services checked.</summary>
        static Observation Checked(double bootMin, bool services = true)
        {
            var o = Back(bootMin);
            o.Verification = new ServerVerification(o.LastBoot.Value.AddMinutes(6), o.LastBoot.Value, "17763.8880", new UpdateResult[0],
                                                    new RestartRecord[0], new BootRecord[0], false, new ServiceIssue[0], services, true, false);
            return o;
        }

        static RoundSnapshot Snap(double atMin, params (string Name, Observation O)[] rows) =>
            new RoundSnapshot(Since.AddMinutes(atMin), Since, rows.Select(r => new KeyValuePair<string, Observation>(r.Name, r.O)));

        [Fact]
        public void FirstSighting_IsSeen_NotATransition()
        {
            var e = Assert.Single(new RoundTracker().Update(Snap(0, ("SRV01", NotRestarted()))));
            Assert.Equal(RoundEventKind.Seen, e.Kind);
            Assert.Equal("first seen NO RESTART PENDING", e.Text);
            Assert.Equal(ServerState.NoRestartPending, e.Status.State);
            Assert.Null(e.Previous);
        }

        [Fact]
        public void StateChange_IsOneLine()
        {
            var t = new RoundTracker();
            t.Update(Snap(0, ("SRV01", Installing())));
            var e = Assert.Single(t.Update(Snap(3, ("SRV01", Down(3)))));
            Assert.Equal(RoundEventKind.Transition, e.Kind);
            Assert.Equal("SRV01", e.Server);
            Assert.Equal("INSTALLING -> DOWN", e.Text);
            Assert.Equal(ServerState.Installing, e.Previous.State);
            Assert.Equal(ServerState.Down, e.Status.State);
        }

        [Fact]
        public void RoutineNotes_AreNotSteps()
        {
            var t = new RoundTracker();
            t.Update(Snap(10, ("SRV01", Back(5))));
            var finishing = Back(5); finishing.TiWorker = true;
            var cleanup = Back(5); cleanup.CbsRebootPending = true;
            Assert.Empty(t.Update(Snap(11, ("SRV01", finishing))));
            Assert.Empty(t.Update(Snap(12, ("SRV01", cleanup))));
        }

        [Fact]
        public void RebootPendingAgain_IsAStep()
        {
            var t = new RoundTracker();
            t.Update(Snap(10, ("SRV01", Back(5))));
            var pending = Back(5); pending.WuRebootRequired = true;
            Assert.Equal("BACK -> BACK (reboot pending again (WU))", Assert.Single(t.Update(Snap(11, ("SRV01", pending)))).Text);
        }

        [Fact]
        public void Checking_IsSkipped()
        {
            var t = new RoundTracker();
            Assert.Empty(t.Update(Snap(9, ("SRV01", Checking()))));   // not even seen yet
            t.Update(Snap(10, ("SRV01", Back(5))));
            Assert.Empty(t.Update(Snap(11, ("SRV01", Checking()))));
            Assert.Empty(t.Update(Snap(12, ("SRV01", Back(5)))));
        }

        [Fact]
        public void DownAlarm_FiresOncePerOutage()
        {
            var t = new RoundTracker();
            t.Update(Snap(0, ("SRV01", Down(0))));
            Assert.Empty(t.Update(Snap(14.9, ("SRV01", Down(0)))));
            var alarm = Assert.Single(t.Update(Snap(15, ("SRV01", Down(0)))));
            Assert.Equal(RoundEventKind.Alarm, alarm.Kind);
            Assert.Equal("ALARM DOWN for 15m00s", alarm.Text);
            Assert.Equal(ServerState.Down, alarm.Status.State);
            Assert.Empty(t.Update(Snap(20, ("SRV01", Down(0)))));

            // Back, then a second long outage alarms again
            t.Update(Snap(21, ("SRV01", Back(20.5))));
            t.Update(Snap(30, ("SRV01", Down(30))));
            Assert.Contains(t.Update(Snap(45, ("SRV01", Down(30)))), e => e.Kind == RoundEventKind.Alarm);
        }

        [Fact]
        public void RebootPendingAlarm_CountsFromTheBoot()
        {
            var t = new RoundTracker();
            var pending = Back(5); pending.CbsRebootPending = true; pending.CbsPackagesPending = 3;
            t.Update(Snap(6, ("SRV01", pending)));
            Assert.Empty(t.Update(Snap(24.9, ("SRV01", pending))));
            var alarm = Assert.Single(t.Update(Snap(25, ("SRV01", pending))));
            Assert.Equal("ALARM back 20m00s, reboot pending again (CBS 3 pkgs)", alarm.Text);
        }

        [Fact]
        public void AllClean_IsSaidOnceAndRearms()
        {
            var t = new RoundTracker();
            t.Update(Snap(0, ("SRV01", Checked(1)), ("SRV02", Installing())));
            var events = t.Update(Snap(10, ("SRV01", Checked(1)), ("SRV02", Checked(4))));
            Assert.Contains(events, e => e.Kind == RoundEventKind.AllClean && e.Text == "ALL 2 BACK CLEAN" && e.Server == null);
            Assert.True(t.AllClean);
            Assert.DoesNotContain(t.Update(Snap(11, ("SRV01", Checked(1)), ("SRV02", Checked(4)))), e => e.Kind == RoundEventKind.AllClean);

            t.Update(Snap(12, ("SRV01", Checked(1)), ("SRV02", Down(12))));
            Assert.False(t.AllClean);
            Assert.Contains(t.Update(Snap(20, ("SRV01", Checked(1)), ("SRV02", Checked(13)))), e => e.Kind == RoundEventKind.AllClean);
        }

        // A green row is not done until its checks are in: an update failure or a stopped service can still turn up
        [Fact]
        public void AllClean_WaitsForTheChecks()
        {
            var t = new RoundTracker();
            t.Update(Snap(0, ("SRV01", Checked(1)), ("SRV02", Installing())));
            Assert.DoesNotContain(t.Update(Snap(5, ("SRV01", Checked(1)), ("SRV02", Back(4)))), e => e.Kind == RoundEventKind.AllClean);
            Assert.DoesNotContain(t.Update(Snap(6, ("SRV01", Checked(1)), ("SRV02", Checked(4, services: false)))), e => e.Kind == RoundEventKind.AllClean);
            Assert.False(t.AllClean);
            Assert.Contains(t.Update(Snap(9, ("SRV01", Checked(1)), ("SRV02", Checked(4)))), e => e.Kind == RoundEventKind.AllClean);
        }

        [Fact]
        public void AllClean_IgnoresAVerificationOfAnEarlierBoot()
        {
            var t = new RoundTracker();
            var rebooted = Checked(1);
            rebooted.LastBoot = Since.AddMinutes(8);   // chained reboot: the checks were for the first boot
            t.Update(Snap(10, ("SRV01", rebooted)));
            Assert.False(t.AllClean);
        }

        [Fact]
        public void ServerRemovedAndReadded_StartsFresh()
        {
            var t = new RoundTracker();
            t.Update(Snap(0, ("SRV01", Installing()), ("SRV02", Installing())));
            t.Update(Snap(1, ("SRV01", Installing())));
            var e = Assert.Single(t.Update(Snap(2, ("SRV01", Installing()), ("SRV02", Down(2)))));
            Assert.Equal(("SRV02", RoundEventKind.Seen), (e.Server, e.Kind));
        }
    }
}
