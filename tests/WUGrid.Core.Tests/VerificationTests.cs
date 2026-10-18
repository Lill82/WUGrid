using System;
using System.Collections.Generic;
using System.Linq;
using WUGrid.Core;
using WUGrid.Core.Reports;
using WUGrid.Core.Verification;
using Xunit;
using static WUGrid.Core.Verification.VerificationBuilder;

namespace WUGrid.Core.Tests
{
    /// <summary>Event layouts as read from a real Server 2019 (es-ES) on 17-10, names changed.</summary>
    public class VerificationBuilderTests
    {
        static readonly DateTime Boot = new DateTime(2026, 10, 17, 15, 19, 30);
        const string Cu = "{f13370af-5bb6-4641-af01-9632ec1bc566}";

        static EventRecord WuFailed(int min, string id = Cu) => new EventRecord(WindowsUpdateClient, 20, Boot.AddMinutes(min), 100 + min, @"NT AUTHORITY\SYSTEM",
            "0x800f0840", "2026-09 Cumulative Update for Windows Server 2019 for x64-based Systems (KB5122876)", id, "200", "{3da21691-e39d-4da6-8a4b-b43877bcb1b7}");

        static EventRecord WuInstalled(int min, string id = Cu, string title = "2026-09 Cumulative Update for Windows Server 2019 for x64-based Systems (KB5122876)") =>
            new EventRecord(WindowsUpdateClient, 19, Boot.AddMinutes(min), 200 + min, @"NT AUTHORITY\SYSTEM", title, id, "200", "{3da21691-e39d-4da6-8a4b-b43877bcb1b7}");

        static readonly EventRecord RemoteRestart = new EventRecord(User32, 1074, Boot.AddMinutes(-9), 1, @"CONTOSO\admin",
            "wininit.exe (10.0.0.50)", "SRV01", "No se encontraron títulos para este motivo", "0x800000ff", "Reinicio", "", @"CONTOSO\admin");

        static readonly EventRecord ChainedRestart = new EventRecord(User32, 1074, Boot.AddMinutes(-0.2), 2, @"NT AUTHORITY\SYSTEM",
            @"C:\Windows\servicing\TrustedInstaller.exe (SRV01)", "SRV01", "Sistema operativo: actualización (planeada)", "0x80020003", "Reinicio", "", @"NT AUTHORITY\SYSTEM");

        static readonly EventRecord BootEvent = new EventRecord(KernelGeneral, 12, Boot.AddSeconds(10), 3, @"NT AUTHORITY\SYSTEM",
            "10", "0", "17763", "8880", "0", "0", Boot.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));

        // Same event ID 20 from other sources: must not read as an update failure
        static readonly EventRecord KernelBoot20 = new EventRecord("Microsoft-Windows-Kernel-Boot", 20, Boot, 4, @"NT AUTHORITY\SYSTEM", "true", "true", "44", "2");

        static ServerVerification Build(IEnumerable<EventRecord> events, IReadOnlyList<ServiceRecord> services = null, Baseline baseline = null,
                                        double upMinutes = 20, bool tiWorker = false) =>
            VerificationBuilder.Build(Boot.AddMinutes(upMinutes), Boot, "17763.8880", events, services, baseline, tiWorker);

        [Fact]
        public void FailedCumulativeUpdate_IsReadFromEvent20()
        {
            var u = Assert.Single(Build(new[] { WuFailed(2), KernelBoot20 }).Updates);
            Assert.False(u.Succeeded);
            Assert.Equal("KB5122876", u.Kb);
            Assert.Equal(unchecked((int)0x800F0840), u.ErrorCode);
        }

        [Fact]
        public void LaterSuccess_OverridesEarlierFailure_ForTheSameUpdate()
        {
            var u = Assert.Single(Build(new[] { WuInstalled(30), WuFailed(2) }).Updates);
            Assert.True(u.Succeeded);
        }

        [Fact]
        public void DifferentUpdates_KeepTheirOwnResults()
        {
            var v = Build(new[] { WuFailed(2), WuInstalled(2, "{01b92d3a-0000-0000-0000-000000000000}", "2026-09 Actualización acumulativa de .NET Framework (KB5126144)") });
            Assert.Equal(2, v.Updates.Count);
            Assert.Equal("KB5122876", Assert.Single(v.Failed).Kb);
        }

        [Fact]
        public void Restarts_SayWhoAndWhy_InTimeOrder()
        {
            var v = Build(new[] { ChainedRestart, RemoteRestart });
            Assert.Equal(2, v.Restarts.Count);
            Assert.Equal(@"CONTOSO\admin", v.Restarts[0].User);
            Assert.Equal("wininit.exe (10.0.0.50)", v.Restarts[0].Process);
            Assert.Equal("0x80020003", v.Restarts[1].ReasonCode);
        }

        [Fact]
        public void BootEvents_CarryTheBuildTheServerCameUpOn()
        {
            var b = Assert.Single(Build(new[] { BootEvent }).Boots);
            Assert.Equal("17763.8880", b.Build);
            Assert.Equal(Boot, b.Time);
        }

        [Fact]
        public void UnexpectedShutdown_IsNoticed() =>
            Assert.True(Build(new[] { new EventRecord(EventLog, 6008, Boot, 9, "") }).UnexpectedShutdown);

        static readonly Baseline Before = new Baseline(Boot.AddHours(-1), "17763.8644",
            new Dictionary<string, string> { ["AppDb"] = "Application Database", ["W32Time"] = "Windows Time", ["Gone"] = "Removed Service" });

        static readonly ServiceRecord[] After =
        {
            new ServiceRecord("AppDb", "Application Database", "Stopped", "Disabled"),
            new ServiceRecord("W32Time", "Windows Time", "Running", "Auto"),
            new ServiceRecord("sppsvc", "Software Protection", "Stopped", "Auto"),          // stops by itself
            new ServiceRecord("CDPUserSvc_1a2b3", "Connected Devices", "Stopped", "Auto"),  // per-user, new suffix each logon
        };

        [Fact]
        public void ServiceThatRanBefore_AndNotNow_IsAnIssue_WithWhoChangedIt()
        {
            var change = new EventRecord(ServiceControlManager, 7040, Boot.AddMinutes(-10), 7, @"CONTOSO\appowner",
                                         "Application Database", "inicio automático", "deshabilitado", "AppDb");
            var v = Build(new[] { change }, After, Before);
            Assert.True(v.ComparedToBaseline);
            Assert.Equal(new[] { "AppDb", "Gone" }, v.StoppedServices.Select(i => i.Service.Name));
            Assert.Equal(@"CONTOSO\appowner", v.StoppedServices[0].LastStartModeChange.User);
            Assert.Equal("Missing", v.StoppedServices[1].Service.State);
        }

        [Fact]
        public void WithoutBaseline_AutomaticServicesNotRunningAreIssues_ButNotTransientOnes()
        {
            var services = new[] { new ServiceRecord("AppDb", "Application Database", "Stopped", "Auto") }.Concat(After.Skip(1)).ToList();
            var v = Build(new EventRecord[0], services);
            Assert.False(v.ComparedToBaseline);
            Assert.Equal("AppDb", Assert.Single(v.StoppedServices).Service.Name);
        }

        [Fact]
        public void Services_WaitForDelayedStart()
        {
            var v = Build(new EventRecord[0], After, Before, upMinutes: 3);
            Assert.False(v.ServicesChecked);
            Assert.Empty(v.StoppedServices);
        }

        [Fact]
        public void Baseline_KeepsRunningAutomaticServicesOnly()
        {
            var b = BuildBaseline(Boot, "17763.8644", new[]
            {
                new ServiceRecord("AppDb", "Application Database", "Running", "Auto"),
                new ServiceRecord("Spooler", "Print Spooler", "Running", "Manual"),
                new ServiceRecord("Fax", "Fax", "Stopped", "Auto"),
                new ServiceRecord("sppsvc", "Software Protection", "Running", "Auto"),
            });
            Assert.Equal(new[] { "AppDb" }, b.RunningAutoServices.Keys);
        }

        [Fact]
        public void Baseline_LeavesOutServicesMarkedForDeletion_AndRemembersThem()
        {
            var b = BuildBaseline(Boot, "17763.8644", new[]
            {
                new ServiceRecord("AppDb", "Application Database", "Running", "Auto"),
                new ServiceRecord("WsusService", "WSUS Service", "Running", "Auto"),
                new ServiceRecord("OldAgent", "Old Agent", "Stopped", "Auto"),
            }, markedForDeletion: new[] { "WsusService", "OldAgent" });
            Assert.Equal(new[] { "AppDb" }, b.RunningAutoServices.Keys);
            Assert.Equal(new[] { "WsusService" }, b.MarkedForDeletion.Keys);   // OldAgent was not running: nothing to expect either way
        }

        [Fact]
        public void ServiceMarkedForDeletion_NotBackAfterTheRestart_IsRemovedNotStopped()
        {
            var before = new Baseline(Boot.AddHours(-1), "17763.8644",
                new Dictionary<string, string> { ["W32Time"] = "Windows Time" },
                new Dictionary<string, string> { ["WsusService"] = "WSUS Service", ["Wid"] = "Windows Internal Database" });
            var after = new[]
            {
                new ServiceRecord("W32Time", "Windows Time", "Running", "Auto"),
                new ServiceRecord("Wid", "Windows Internal Database", "Stopped", "Auto"),   // registered until its last handle closes
            };
            var v = Build(new EventRecord[0], after, before);
            Assert.Empty(v.StoppedServices);
            Assert.Equal(new[] { "Wid", "WsusService" }, v.RemovedServices.Select(s => s.Name));
            Assert.Equal("Stopped", v.RemovedServices[0].State);
            Assert.Equal("Missing", v.RemovedServices[1].State);
        }

        [Fact]
        public void ServiceMarkedForDeletion_RunningAgain_IsNeitherRemovedNorStopped()
        {
            var before = new Baseline(Boot.AddHours(-1), "17763.8644", new Dictionary<string, string>(),
                                      new Dictionary<string, string> { ["WsusService"] = "WSUS Service" });
            var v = Build(new EventRecord[0], new[] { new ServiceRecord("WsusService", "WSUS Service", "Running", "Auto") }, before);
            Assert.Empty(v.StoppedServices);
            Assert.Empty(v.RemovedServices);
        }

        [Theory]
        [InlineData(20, false, true)]
        [InlineData(20, true, false)]
        [InlineData(10, false, false)]
        public void Settled_WhenUpLongEnoughAndServicingDone(double up, bool tiWorker, bool settled) =>
            Assert.Equal(settled, Build(new EventRecord[0], upMinutes: up, tiWorker: tiWorker).Settled);
    }

    public class VerificationFlagTests
    {
        static readonly DateTime Since = new DateTime(2026, 10, 17, 15, 0, 0);
        static readonly DateTime Boot = Since.AddMinutes(19);

        static Observation Back(ServerVerification v) =>
            new Observation { Ping = true, Wmi = WmiResult.Ok, LastBoot = Boot, Build = "17763.8880", Verification = v };

        static ServerVerification Verified(DateTime boot, IReadOnlyList<UpdateResult> updates = null, IReadOnlyList<ServiceIssue> services = null) =>
            new ServerVerification(boot.AddMinutes(20), boot, "17763.8880", updates ?? new UpdateResult[0], new RestartRecord[0], new BootRecord[0],
                                   false, services ?? new ServiceIssue[0], true, true, true);

        static readonly UpdateResult FailedCu = new UpdateResult(Boot.AddMinutes(2), "2026-09 Cumulative Update (KB5122876)", "KB5122876", "{a}", false, unchecked((int)0x800F0840));

        [Fact]
        public void FailedUpdate_FlagsBack_WithTheCodeNamed()
        {
            var s = StateClassifier.Classify(Back(Verified(Boot, new[] { FailedCu })), Since, Boot.AddMinutes(30));
            Assert.Equal(BackFlag.UpdateFailed, s.Flag);
            Assert.Equal("update failed: KB5122876 (0x800F0840 CBS_E_SESSION_CORRUPT)", s.Note);
        }

        [Fact]
        public void StoppedService_FlagsBack()
        {
            var issue = new ServiceIssue(new ServiceRecord("AppDb", "Application Database", "Stopped", "Disabled"), null);
            var s = StateClassifier.Classify(Back(Verified(Boot, services: new[] { issue })), Since, Boot.AddMinutes(30));
            Assert.Equal(BackFlag.ServiceStopped, s.Flag);
            Assert.Equal("service not running: Application Database", s.Note);
        }

        [Fact]
        public void FlagsCombine()
        {
            var o = Back(Verified(Boot, new[] { FailedCu }));
            o.WuRebootRequired = true;
            var s = StateClassifier.Classify(o, Since, Boot.AddMinutes(30));
            Assert.Equal(BackFlag.RebootPending | BackFlag.UpdateFailed, s.Flag);
            Assert.StartsWith("reboot pending again (WU); update failed", s.Note);
        }

        [Fact]
        public void VerificationOfAnEarlierBoot_DoesNotCount()
        {
            var s = StateClassifier.Classify(Back(Verified(Boot.AddMinutes(-5), new[] { FailedCu })), Since, Boot.AddMinutes(30));
            Assert.True(s.IsClean);
        }

        [Fact]
        public void Report_HasOneRowPerServer_WithFailuresAndBuild()
        {
            var o = Back(Verified(Boot, new[] { FailedCu }));
            o.Baseline = new Baseline(Since.AddMinutes(-30), "17763.8644", new Dictionary<string, string>());
            var snap = new RoundSnapshot(Boot.AddMinutes(30), Since, new[] { new KeyValuePair<string, Observation>("SRV01", o) });
            var lines = RoundReport.Csv(snap, ",").TrimEnd().Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("Server,State,Note", lines[0]);
            Assert.Contains("17763.8644,17763.8880", lines[1]);
            Assert.Contains("KB5122876:0x800F0840", lines[1]);
            Assert.Contains("update failed: KB5122876 (0x800F0840 CBS_E_SESSION_CORRUPT)", lines[1]);
            Assert.Contains("CBS_E_SESSION_CORRUPT", RoundReport.Html(snap));
        }

        [Fact]
        public void Csv_QuotesValuesContainingTheSeparator()
        {
            var o = Back(Verified(Boot, new[] { FailedCu, new UpdateResult(Boot, "x (KB1234567)", "KB1234567", "{b}", false, 1) }));
            var snap = new RoundSnapshot(Boot.AddMinutes(30), Since, new[] { new KeyValuePair<string, Observation>("SRV01", o) });
            Assert.Contains("\"2 updates failed: KB5122876, KB1234567\"", RoundReport.Csv(snap, ","));
        }
    }
}
