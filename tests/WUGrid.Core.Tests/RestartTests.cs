using System;
using WUGrid.Core;
using WUGrid.Core.Restart;
using Xunit;

namespace WUGrid.Core.Tests
{
    public class RestartGuardTests
    {
        static readonly DateTime Since = new DateTime(2026, 10, 17, 15, 0, 0);
        static readonly DateTime Now = Since.AddHours(1);

        static Observation Up(DateTime lastBoot) => new Observation { Ping = true, Wmi = WmiResult.Ok, LastBoot = lastBoot };
        static Observation NotRestarted() => Up(Since.AddDays(-30));

        static RestartEligibility Check(Observation o) => RestartGuard.Check(o, StateClassifier.Classify(o, Since, Now));

        [Fact]
        public void NoRestartPending_CanStillRestart_AndSaysSo()
        {
            var e = Check(NotRestarted());
            Assert.True(e.Allowed);
            Assert.StartsWith("no restart pending", e.Reason);
        }

        [Fact]
        public void RestartPending_CanRestart_AndCountsAsPending()
        {
            var o = NotRestarted(); o.CbsRebootPending = true; o.CbsPackagesPending = 40;
            var s = StateClassifier.Classify(o, Since, Now);
            Assert.True(RestartGuard.Check(o, s).Allowed);
            Assert.True(RestartGuard.IsPending(s));
            Assert.False(RestartGuard.IsPending(StateClassifier.Classify(NotRestarted(), Since, Now)));
        }

        [Fact]
        public void BackServer_CanRestartAgain_AndSaysSo()
        {
            var e = Check(Up(Since.AddMinutes(20)));
            Assert.True(e.Allowed);
            Assert.Contains("already restarted", e.Reason);
        }

        [Fact]
        public void PendingReboot_IsAReasonToRestart()
        {
            var o = Up(Since.AddMinutes(20));
            o.WuRebootRequired = true;
            var e = Check(o);
            Assert.True(e.Allowed);
            Assert.Contains("completes it", e.Reason);
        }

        [Theory]
        [InlineData("TiWorker.exe")]
        [InlineData("TrustedInstaller.exe")]
        [InlineData("msiexec.exe")]
        [InlineData("wusa.exe")]
        [InlineData("DismHost.exe")]
        public void UpdateWorkRunning_Blocks(string process)
        {
            var o = Up(Since.AddMinutes(20));
            o.BusyProcesses = new[] { process };
            o.TiWorker = process == "TiWorker.exe";
            var e = Check(o);
            Assert.False(e.Allowed);
            Assert.Contains(process, e.Reason);
        }

        [Fact]
        public void InstallingBeforeShutdown_Blocks_ARestartIsAlreadyUnderWay()
        {
            var o = NotRestarted();
            o.TiWorker = true;
            o.BusyProcesses = new[] { "TiWorker.exe" };
            Assert.Contains("under way", Check(o).Reason);
        }

        [Fact]
        public void ComponentCleanup_Blocks()
        {
            var o = Up(Since.AddMinutes(20));
            o.CbsRebootPending = true;
            Assert.Equal("component cleanup running", Check(o).Reason);
        }

        [Theory]
        [InlineData(null, false)]          // no answer yet
        [InlineData(false, false)]         // down
        public void UnreachableServers_Block(bool? ping, bool allowed) =>
            Assert.Equal(allowed, Check(new Observation { Ping = ping, DownSince = Now }).Allowed);

        [Fact]
        public void BootingServer_Blocks() =>
            Assert.False(Check(new Observation { Ping = true, Wmi = WmiResult.Failed, WmiError = "Access is denied.", WmiHResult = unchecked((int)0x80070005) }).Allowed);
    }

    public class AuditLogTests
    {
        [Fact]
        public void Line_IsOneJsonObject_WithEverythingNeededToTraceTheAction()
        {
            var result = new RestartResult("SRV01", RestartOutcome.Requested, "restart, not forced, 60 s warning");
            var options = new RestartOptions { WarningSeconds = 60, Comment = "after \"patch\" round\nwave 1" };
            var line = AuditLog.Line(result, options, new DateTime(2026, 10, 17, 23, 5, 0), @"CONTOSO\admin", "ADMINPC");
            Assert.DoesNotContain("\n", line);
            Assert.StartsWith("{\"time\":\"2026-10-17T23:05:00", line);
            Assert.Contains("\"operator\":\"CONTOSO\\\\admin\"", line);
            Assert.Contains("\"server\":\"SRV01\"", line);
            Assert.Contains("\"force\":false", line);
            Assert.Contains("\"checkOnly\":false", line);
            Assert.Contains("\"warningSeconds\":60", line);
            Assert.Contains("\"reasonCode\":\"0x80020011\"", line);
            Assert.Contains("\"comment\":\"after \\\"patch\\\" round\\nwave 1\"", line);
            Assert.Contains("\"outcome\":\"Requested\"", line);
            Assert.EndsWith("\"code\":\"0x00000000\"}", line);
        }

        [Fact]
        public void Comment_NamesTheOperatorAndIsCappedForTheShutdownApi()
        {
            var c = RestartAction.CommentFor(new RestartOptions { Comment = new string('x', 1000) });
            Assert.StartsWith("Restart requested from WUGrid by ", c);
            Assert.True(c.Length <= 500);
        }
    }
}
