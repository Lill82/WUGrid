using System;
using System.Collections.Generic;
using System.Linq;
using WUGrid.Core;
using WUGrid.Core.Demo;
using Xunit;

namespace WUGrid.Core.Tests
{
    public class DemoRoundTests
    {
        [Fact]
        public void StartsWithEveryServerChecking() =>
            Assert.All(new DemoRound().At(TimeSpan.Zero).Rows, r => Assert.Equal(ServerState.Checking, r.Status.State));

        [Fact]
        public void EndsWithEveryServerRestarted_AndOnlyTheFailedUpdateFlagged()
        {
            var demo = new DemoRound();
            var end = demo.At(DemoRound.Length);
            Assert.All(end.Rows, r => Assert.True(r.Observation.LastBoot > demo.Since, r.Name));
            var flagged = Assert.Single(end.Rows, r => !r.Status.IsClean);
            Assert.Equal("SQL01", flagged.Name);
            Assert.Equal(BackFlag.UpdateFailed, flagged.Status.Flag);
            Assert.Equal("17763.8880", flagged.Observation.Build);
        }

        [Fact]
        public void ReplayRaisesTheScriptedAlarmsAndFlags()
        {
            var demo = new DemoRound();
            var tracker = new RoundTracker();
            var events = new List<RoundEvent>();
            for (var t = TimeSpan.Zero; t <= DemoRound.Length; t += TimeSpan.FromSeconds(10))
                events.AddRange(tracker.Update(demo.At(t)));

            var alarms = events.Where(e => e.Kind == RoundEventKind.Alarm).Select(e => $"{e.Server} {e.Text}").ToList();
            Assert.Contains(alarms, a => a.StartsWith("RDS01 ALARM DOWN for 15m"));
            Assert.Contains(alarms, a => a.StartsWith("SQL02 ALARM back 20m") && a.Contains("reboot pending again (CBS 3 pkgs)"));
            Assert.Equal(2, alarms.Count);

            var lines = events.Select(e => $"{e.Server} {e.Text}").ToList();
            Assert.Contains(lines, l => l.StartsWith("SQL01") && l.Contains("update failed: KB5900001 (0x800F0840 CBS_E_SESSION_CORRUPT)"));
            Assert.Contains(lines, l => l.StartsWith("APP02") && l.EndsWith("(service not running: Application Database Service)"));
            Assert.Contains(lines, l => l.StartsWith("APP02 BACK (service not running"));   // re-enabled by its owner
            Assert.Contains(lines, l => l.StartsWith("PRINT01") &&
                                        l.Contains("service not running: Print Management Server (timed out at start; also at all 4 boots before)"));
            Assert.DoesNotContain(events, e => e.Kind == RoundEventKind.AllClean);
        }
    }
}
