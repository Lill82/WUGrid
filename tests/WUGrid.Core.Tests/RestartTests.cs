using System;
using System.Collections.Generic;
using System.Linq;
using WUGrid.Core;
using WUGrid.Core.Probing;
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

        // Field notes 28-10: the mark stayed after a failed cumulative update, nothing running, Windows Update asking
        [Fact]
        public void CleanupMark_WithWindowsUpdateAskingForTheRestart_DoesNotBlock()
        {
            var o = Up(Since.AddMinutes(20));
            o.CbsRebootPending = true;
            o.WuRebootRequired = true;
            var e = Check(o);
            Assert.True(e.Allowed);
            Assert.Contains("completes it", e.Reason);
        }

        [Fact]
        public void CleanupMark_WithWindowsUpdateAsking_StillBlocksWhileTiWorkerRuns()
        {
            var o = Up(Since.AddMinutes(20));
            o.CbsRebootPending = true;
            o.WuRebootRequired = true;
            o.TiWorker = true;
            o.BusyProcesses = new[] { "TiWorker.exe" };
            Assert.False(Check(o).Allowed);
        }

        // Field notes 18-10: a restart that is not forced came back 1191 (ERROR_SHUTDOWN_USERS_LOGGED_ON) from three servers
        [Fact]
        public void SignedInUser_Blocks_AndSaysWhoInWhichSessionSinceWhen()
        {
            var o = NotRestarted(); o.CbsRebootPending = true; o.CbsPackagesPending = 40;
            o.SignedIn = new[] { new UserSession(6, @"CONTOSO\admin", new DateTime(2026, 9, 16, 8, 46, 20)) };
            var e = Check(o);
            Assert.False(e.Allowed);
            Assert.StartsWith(@"signed in: CONTOSO\admin (session 6, since 16-09-2026 ", e.Reason);
            Assert.Contains("sign out", e.Reason);
        }

        [Fact]
        public void SignedInUser_WithForce_IsAllowed_AndSaysWhoIsSignedOut()
        {
            var o = NotRestarted(); o.CbsRebootPending = true; o.CbsPackagesPending = 40;
            o.SignedIn = new[] { new UserSession(6, @"CONTOSO\admin", new DateTime(2026, 9, 16, 8, 46, 20)) };
            var e = RestartGuard.Check(o, StateClassifier.Classify(o, Since, Now), forceIfSignedIn: true);
            Assert.True(e.Allowed);
            Assert.StartsWith(@"forced, signs out CONTOSO\admin (session 6, ", e.Reason);
            Assert.Contains("unsaved work is lost; a restart is pending", e.Reason);
        }

        [Fact]
        public void Force_DoesNotLiftTheUpdateWorkGuard()
        {
            var o = Up(Since.AddMinutes(20));
            o.BusyProcesses = new[] { "msiexec.exe" };
            o.SignedIn = new[] { new UserSession(2, @"CONTOSO\admin", null) };
            Assert.False(RestartGuard.Check(o, StateClassifier.Classify(o, Since, Now), forceIfSignedIn: true).Allowed);
        }

        [Fact]
        public void Force_WithNobodySignedIn_ChangesNothing()
        {
            var o = NotRestarted();
            var s = StateClassifier.Classify(o, Since, Now);
            Assert.Equal(RestartGuard.Check(o, s).Reason, RestartGuard.Check(o, s, forceIfSignedIn: true).Reason);
        }

        [Fact]
        public void ManySignedIn_NamesTheFirstThree_AndCountsTheRest()
        {
            var o = NotRestarted();
            o.SignedIn = Enumerable.Range(1, 5).Select(i => new UserSession(i, $@"CONTOSO\user{i}", null)).ToArray();
            var reason = Check(o).Reason;
            Assert.Contains(@"CONTOSO\user3 (session 3)", reason);
            Assert.DoesNotContain("user4", reason);
            Assert.Contains("and 2 more", reason);
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

    public class SessionReaderTests
    {
        const string User = "S-1-5-21-1004336348-1177238915-682003330-500";
        static readonly DateTime SignIn = new DateTime(2026, 9, 16, 8, 46, 20);

        static SessionProcess P(int session, string name, DateTime? created = null) => new SessionProcess($"{name}/{session}", name, session, created);

        static IEnumerable<SessionProcess> LogonScreen(int session) =>
            new[] { "csrss.exe", "winlogon.exe", "LogonUI.exe", "dwm.exe", "fontdrvhost.exe" }.Select(n => P(session, n));

        [Fact]
        public void LogonScreen_IsNobodySignedIn_WithoutAskingTheServer()
        {
            var asked = 0;
            Assert.Empty(SessionReader.Pick(LogonScreen(1), p => { asked++; return "S-1-5-18"; }, p => null));
            Assert.Equal(0, asked);
        }

        [Fact]
        public void RemoteDesktopSession_IsFoundWithOneLookup_AndDatedFromTheSignIn_NotFromAnExplorerRestart()
        {
            var processes = LogonScreen(6).Concat(new[]
            {
                P(6, "explorer.exe", new DateTime(2026, 10, 18, 14, 20, 20)),
                P(6, "rdpclip.exe", SignIn),
                P(6, "sihost.exe", SignIn),
                P(6, "notepad.exe", SignIn.AddDays(3)),
            });
            var asked = new List<string>();
            var s = Assert.Single(SessionReader.Pick(processes, p => { asked.Add(p.Name); return User; }, p => @"CONTOSO\Administrator"));
            Assert.Equal(6, s.Id);
            Assert.Equal(@"CONTOSO\Administrator", s.User);
            Assert.Equal(SignIn, s.Since);
            Assert.Equal(new[] { "sihost.exe" }, asked);
        }

        [Fact]
        public void SystemHelperInTheConsoleSession_IsNotAUser() =>
            Assert.Empty(SessionReader.Pick(LogonScreen(1).Append(P(1, "vm3dservice.exe")), p => "S-1-5-18", p => null));

        [Fact]
        public void ProcessThatExited_IsSkippedForTheNext_AndTheSidStandsInForAName()
        {
            var processes = new[] { P(2, "sihost.exe", SignIn), P(2, "cmd.exe", SignIn.AddMinutes(1)) };
            var s = Assert.Single(SessionReader.Pick(processes, p => p.Name == "sihost.exe" ? null : User, p => null));
            Assert.Equal(User, s.User);
            Assert.Equal(SignIn, s.Since);
        }

        [Fact]
        public void Lookups_AreCappedPerSession()
        {
            var asked = 0;
            Assert.Empty(SessionReader.Pick(Enumerable.Range(0, 20).Select(i => P(1, $"helper{i}.exe")), p => { asked++; return "S-1-5-18"; }, p => null));
            Assert.Equal(SessionReader.MaxAsks, asked);
        }

        [Fact]
        public void EverySignedInSession_IsListed_BySessionId()
        {
            var processes = new[] { P(4, "sihost.exe", SignIn), P(3, "sihost.exe", SignIn), P(1, "winlogon.exe") };
            Assert.Equal(new[] { 3, 4 }, SessionReader.Pick(processes, p => User, p => "user" + p.SessionId).Select(s => s.Id));
        }

        [Theory]
        [InlineData("S-1-5-21-1004336348-1177238915-682003330-500", true)]   // domain or local account
        [InlineData("S-1-12-1-1234567890-1234567890-1234567890-1234567890", true)]   // Entra ID
        [InlineData("S-1-5-18", false)]       // SYSTEM
        [InlineData("S-1-5-19", false)]       // LOCAL SERVICE
        [InlineData("S-1-5-20", false)]       // NETWORK SERVICE
        [InlineData("S-1-5-90-0-1", false)]   // DWM-1, the window manager
        [InlineData("S-1-5-96-0-1", false)]   // UMFD-1, the font driver host
        [InlineData(null, false)]
        public void UserSids_AreAccounts_NotServicesOrTheDesktopsOwnHelpers(string sid, bool user) =>
            Assert.Equal(user, SessionReader.IsUserSid(sid));
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
            Assert.Contains("\"forceIfSignedIn\":false", line);
            Assert.Contains("\"checkOnly\":false", line);
            Assert.Contains("\"warningSeconds\":60", line);
            Assert.Contains("\"reasonCode\":\"0x80020011\"", line);
            Assert.Contains("\"comment\":\"after \\\"patch\\\" round\\nwave 1\"", line);
            Assert.Contains("\"outcome\":\"Requested\"", line);
            Assert.EndsWith("\"code\":\"0x00000000\"}", line);
        }

        [Fact]
        public void Line_SaysWhenTheRestartWasForced()
        {
            var result = new RestartResult("SRV01", RestartOutcome.Requested, "restart, FORCED, 60 s warning", forced: true);
            var line = AuditLog.Line(result, new RestartOptions { ForceIfSignedIn = true }, new DateTime(2026, 10, 28, 9, 0, 0), @"CONTOSO\admin", "ADMINPC");
            Assert.Contains("\"force\":true", line);
            Assert.Contains("\"forceIfSignedIn\":true", line);
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
