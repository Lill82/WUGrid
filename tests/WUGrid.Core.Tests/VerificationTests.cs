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
                                        double upMinutes = 20, bool tiWorker = false, IEnumerable<EventRecord> earlier = null) =>
            VerificationBuilder.Build(Boot.AddMinutes(upMinutes), Boot, "17763.8880", events, services, baseline, tiWorker, earlier);

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

        // Service Control Manager layouts as read from a real Server 2019 (es-ES) on 18-10, names changed: errors arrive as
        // "%%1053" (Win32 1053, no answer in time), 7009 starts with the timeout, 7023 can carry the display name instead
        static readonly ServiceRecord[] PrintServer =
        {
            new ServiceRecord("PrintSvc", "Print Management Server", "Stopped", "Auto"),
            new ServiceRecord("W32Time", "Windows Time", "Running", "Auto"),
        };

        static EventRecord Scm(int code, DateTime time, long record, params string[] strings) =>
            new EventRecord(ServiceControlManager, code, time, record, "", strings);

        static EventRecord BootedAt(DateTime boot, long record) =>
            new EventRecord(KernelGeneral, 12, boot.AddSeconds(1), record, @"NT AUTHORITY\SYSTEM",
                            "10", "0", "17763", "8641", "0", "0", boot.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));

        static IEnumerable<EventRecord> TimedOutAfter(DateTime boot, long record) => new[]
        {
            Scm(7009, boot.AddMinutes(1.6), record, "30000", "PrintSvc"),
            Scm(7000, boot.AddMinutes(1.6), record + 1, "PrintSvc", "%%1053"),
        };

        static ServiceFailure FailureOf(ServerVerification v) => Assert.Single(v.StoppedServices).Failure;

        [Fact]
        public void TimeoutAtStart_IsTheReason_As7009SaysHowLong()
        {
            var f = FailureOf(Build(TimedOutAfter(Boot, 50).Prepend(BootEvent), PrintServer));
            Assert.Equal(7009, f.Event.Code);
            Assert.Equal(FailureKind.Timeout, f.Kind);
            Assert.Equal("timed out at start: no answer within 30 s (event 7009)", f.Reason);
            Assert.Equal(0, f.EarlierBoots);
            Assert.Equal("", f.Earlier);
            Assert.StartsWith("Too slow to start while the server boots", f.Advice);
        }

        [Fact]
        public void ServiceSpecificError_IsTheServicesOwnCode()
        {
            var f = FailureOf(Build(new[] { BootEvent, Scm(7024, Boot.AddMinutes(1.8), 60, "PrintSvc", "%%17113") }, PrintServer));
            Assert.Equal("stopped with its own error 17113 (event 7024)", f.Reason);
            Assert.Equal("stopped with an error", f.ShortReason);
        }

        [Fact]
        public void EventNamingTheDisplayName_StillMatches() =>
            Assert.Equal("stopped with an error: access denied (event 7023)",
                         FailureOf(Build(new[] { BootEvent, Scm(7023, Boot.AddMinutes(2), 60, "Print Management Server", "%%5") }, PrintServer)).Reason);

        [Fact]
        public void StoppedWithoutAFailureEvent_HasNoReason() =>   // stopped by someone or disabled: event 7040 says who
            Assert.Null(FailureOf(Build(new[] { BootEvent }, PrintServer)));

        [Fact]
        public void FailureBeforeThisBoot_IsNotThisBootsReason() =>
            Assert.Null(FailureOf(Build(new[] { Scm(7034, Boot.AddMinutes(-2), 2, "PrintSvc", "1"), BootEvent }, PrintServer)));

        [Fact]
        public void EarlierBootInTheRound_IsCounted()
        {
            var first = Boot.AddMinutes(-80);
            var events = new[] { BootedAt(first, 1) }.Concat(TimedOutAfter(first, 2)).Append(BootEvent).Concat(TimedOutAfter(Boot, 50));
            var f = FailureOf(Build(events, PrintServer));
            Assert.Equal((1, 1), (f.EarlierBoots, f.EarlierFailed));
            Assert.True(f.EveryBoot);
            Assert.Equal($"also at the boot before ({first:dd-MM HH:mm})", f.Earlier);
            Assert.StartsWith("It failed after every boot before too", f.Advice);
        }

        [Fact]
        public void EarlierBootsReadOnRequest_CountOnlyFailuresSoonAfterEachBoot()
        {
            DateTime b1 = Boot.AddDays(-150), b2 = Boot.AddDays(-70), b3 = Boot.AddDays(-30);
            var earlier = new[] { BootedAt(b1, 1), BootedAt(b2, 3), BootedAt(b3, 5) }
                .Concat(TimedOutAfter(b1, 100))
                .Append(Scm(7034, b2.AddHours(3), 200, "PrintSvc", "1"))   // hours after its boot: not failing at that boot
                .Concat(TimedOutAfter(b3, 300));
            var f = FailureOf(Build(TimedOutAfter(Boot, 50).Prepend(BootEvent), PrintServer, earlier: earlier));
            Assert.Equal((3, 2), (f.EarlierBoots, f.EarlierFailed));
            Assert.Equal($"also at 2 of the 3 boots before since {b1:dd-MM-yyyy}", f.Earlier);
            Assert.Equal("also at 2 of 3 boots before", f.ShortEarlier);
        }

        [Fact]
        public void NoFailureAtTheBootsBefore_PointsAtThisRestart()
        {
            var earlier = new[] { BootedAt(Boot.AddDays(-60), 1), BootedAt(Boot.AddDays(-30), 2) };
            var f = FailureOf(Build(TimedOutAfter(Boot, 50).Prepend(BootEvent), PrintServer, earlier: earlier));
            Assert.Equal("first time in 3 boots", f.ShortEarlier);
            Assert.StartsWith("It did not fail after the boots before", f.Advice);
        }

        [Theory]
        [InlineData(7000, new[] { "PrintSvc", "%%1069" }, FailureKind.Logon, "could not log on")]
        [InlineData(7038, new[] { "PrintSvc", @"CONTOSO\svc-print", "%%1326" }, FailureKind.Logon, "could not log on")]
        [InlineData(7001, new[] { "PrintSvc", "Print Database", "%%1053" }, FailureKind.Dependency, "a service it needs did not start")]
        [InlineData(7031, new[] { "PrintSvc", "1", "300000", "1", "Reiniciar el servicio" }, FailureKind.Ended, "terminated unexpectedly")]
        [InlineData(7022, new[] { "PrintSvc" }, FailureKind.Timeout, "hung at start")]
        [InlineData(7000, new[] { "PrintSvc", "%%2" }, FailureKind.Error, "failed to start")]
        public void EachFailureEvent_ReadsAsItsKind(int code, string[] strings, FailureKind kind, string shortReason)
        {
            var f = FailureOf(Build(new[] { BootEvent, Scm(code, Boot.AddMinutes(2), 60, strings) }, PrintServer));
            Assert.Equal((kind, shortReason), (f.Kind, f.ShortReason));
        }

        [Fact]
        public void LogonFailure_NamesTheAccount() =>
            Assert.Equal(@"could not log on as CONTOSO\svc-print: wrong user name or password (event 7038)",
                         FailureOf(Build(new[] { BootEvent, Scm(7038, Boot.AddMinutes(2), 60, "PrintSvc", @"CONTOSO\svc-print", "%%1326") }, PrintServer)).Reason);

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

        // 43 and 44 as a 25H2 workstation logs them (es-ES): [0] title, [1] update id, [2] revision. 44 version 0 has no title.
        const string NetCu = "2026-10 Actualización acumulativa de .NET Framework 3.5 y 4.8.1 (KB5127690)";

        static EventRecord WuDownloadStarted(int min, string id = Cu, string title = NetCu) =>
            new EventRecord(WindowsUpdateClient, 44, Boot.AddMinutes(min), 300 + min, @"NT AUTHORITY\SYSTEM", title, id, "100");

        static EventRecord WuInstallStarted(int min, string id = Cu, string title = NetCu) =>
            new EventRecord(WindowsUpdateClient, 43, Boot.AddMinutes(min), 400 + min, @"NT AUTHORITY\SYSTEM", title, id, "100");

        [Fact]
        public void DownloadStarted_WithNoResult_IsInFlight_AndDoesNotSettle()
        {
            var v = Build(new[] { WuDownloadStarted(8) }, upMinutes: 60);
            var u = Assert.Single(v.UpdatesInFlight);
            Assert.Equal("KB5127690", u.Label);
            Assert.False(u.Installing);
            Assert.Equal(Boot.AddMinutes(8), u.Time);
            Assert.Empty(v.Updates);
            Assert.False(v.Settled);
        }

        [Fact]
        public void InstallStarted_IsTheLatestStage() =>
            Assert.True(Assert.Single(Build(new[] { WuDownloadStarted(8), WuInstallStarted(40) }).UpdatesInFlight).Installing);

        [Fact]
        public void AResult_EndsTheUpdateInFlight()
        {
            var v = Build(new[] { WuDownloadStarted(1), WuInstallStarted(2), WuInstalled(6) });
            Assert.Empty(v.UpdatesInFlight);
            Assert.True(v.Settled);
            Assert.Empty(Build(new[] { WuInstallStarted(1), WuFailed(2) }).UpdatesInFlight);
        }

        [Fact]
        public void ANewDownload_AfterAResult_IsInFlightAgain() =>
            Assert.Single(Build(new[] { WuFailed(2), WuDownloadStarted(10) }).UpdatesInFlight);

        // FIELD-NOTES 28-10: a cumulative update failed twice overnight (disk full), the server restarted and Windows Update
        // started it again; the row said "update failed" with TiWorker running the retry
        [Fact]
        public void AFailedUpdate_StartedAgain_IsInFlight_NotFailed()
        {
            var v = Build(new[] { WuFailed(2), WuInstallStarted(10, Cu.ToUpperInvariant().Trim('{', '}')) }, tiWorker: true);
            Assert.Empty(v.Failed);
            Assert.True(v.Retrying(Assert.Single(v.Updates)));
            Assert.Single(v.UpdatesInFlight);

            var o = new Observation { Ping = true, Wmi = WmiResult.Ok, LastBoot = Boot, TiWorker = true, Verification = v };
            var s = StateClassifier.Classify(o, Boot.AddMinutes(-19), Boot.AddMinutes(20));
            Assert.True(s.IsClean);
            Assert.Equal(BackActivity.UpdatesInFlight, s.Activity);
            Assert.Equal($"installing KB5127690 (started {Boot.AddMinutes(10):HH:mm}, again after 0x800F0840)", s.Note);

            // Install started but no TiWorker (between its runs, or waiting): not called installing
            o.TiWorker = false;
            Assert.Equal($"update not installed yet: KB5127690 (install started {Boot.AddMinutes(10):HH:mm}, again after 0x800F0840)",
                         StateClassifier.Classify(o, Boot.AddMinutes(-19), Boot.AddMinutes(20)).Note);

            // The retry fails too: flagged again
            var again = Build(new[] { WuFailed(2), WuInstallStarted(10), WuFailed(30) });
            Assert.Single(again.Failed);
            Assert.Empty(again.UpdatesInFlight);
        }

        [Fact]
        public void UntitledDownloadEvent_IsMatchedOnTheUpdateId_WhateverItsBracesAndCase()
        {
            var old44 = new EventRecord(WindowsUpdateClient, 44, Boot.AddMinutes(1), 301, @"NT AUTHORITY\SYSTEM", Cu.Trim('{', '}').ToUpperInvariant(), "200");
            var u = Assert.Single(Build(new[] { old44 }).UpdatesInFlight);
            Assert.Equal(Cu.Trim('{', '}'), u.Label);
            Assert.Empty(Build(new[] { old44, WuInstalled(5) }).UpdatesInFlight);
        }

        [Fact]
        public void OtherUpdates_KeepTheirOwnStage()
        {
            var v = Build(new[] { WuDownloadStarted(1), WuDownloadStarted(1, "{01b92d3a-0000-0000-0000-000000000000}", "Security Intelligence Update (KB2267602)"),
                                  WuInstalled(3, "{01b92d3a-0000-0000-0000-000000000000}", "Security Intelligence Update (KB2267602)") });
            Assert.Equal("KB5127690", Assert.Single(v.UpdatesInFlight).Kb);
        }

        [Fact]
        public void Events43And44_FromOtherSources_AreNotUpdates() =>
            Assert.Empty(Build(new[] { new EventRecord("Microsoft-Windows-Kernel-Power", 44, Boot, 5, "", "1", "2") }).UpdatesInFlight);
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

        static ServiceIssue TimesOutAtEveryBoot() =>
            new ServiceIssue(new ServiceRecord("PrintSvc", "Print Management Server", "Stopped", "Auto"), null,
                             new ServiceFailure(new EventRecord(ServiceControlManager, 7009, Boot.AddMinutes(2), 9, "", "30000", "PrintSvc"),
                                                4, 4, new DateTime(2026, 5, 8, 7, 31, 0)));

        // Field notes 18-10 evening: a service that fails at every boot is its own problem, not the restart's
        [Fact]
        public void StoppedService_ThatFailsAtEveryBoot_SaysSoInTheNote()
        {
            var s = StateClassifier.Classify(Back(Verified(Boot, services: new[] { TimesOutAtEveryBoot() })), Since, Boot.AddMinutes(30));
            Assert.Equal(BackFlag.ServiceStopped, s.Flag);
            Assert.Equal("service not running: Print Management Server (timed out at start; also at all 4 boots before)", s.Note);
        }

        [Fact]
        public void ServiceLine_SaysWhyItDidNotStart_AndHowItFaredBefore() =>
            Assert.Equal("Print Management Server (PrintSvc): Stopped, Auto; timed out at start: no answer within 30 s (event 7009), " +
                         "also at all 4 boots before since 08-05-2026", RoundReport.ServiceLine(TimesOutAtEveryBoot()));

        [Fact]
        public void FlagsCombine()
        {
            var o = Back(Verified(Boot, new[] { FailedCu }));
            o.WuRebootRequired = true;
            var s = StateClassifier.Classify(o, Since, Boot.AddMinutes(30));
            Assert.Equal(BackFlag.RebootPending | BackFlag.UpdateFailed, s.Flag);
            Assert.StartsWith("reboot pending again (WU); update failed", s.Note);
        }

        static ServerVerification WithInFlight(params UpdateInFlight[] inFlight) =>
            new ServerVerification(Boot.AddMinutes(20), Boot, "17763.8880", new UpdateResult[0], new RestartRecord[0], new BootRecord[0],
                                   false, new ServiceIssue[0], true, true, false, updatesInFlight: inFlight);

        static readonly UpdateInFlight Downloading = new UpdateInFlight(Boot.AddMinutes(8), "2026-10 Cumulative Update (KB5127648)", "KB5127648", "{c}", false);

        // An update downloading after the restart installs later and may want another: the row stays clean, the round not done
        [Fact]
        public void UpdateInFlight_KeepsTheRowClean_ButTheRoundNotDone()
        {
            var o = Back(WithInFlight(Downloading));
            var s = StateClassifier.Classify(o, Since, Boot.AddMinutes(30));
            Assert.True(s.IsClean);
            Assert.Equal(BackActivity.UpdatesInFlight, s.Activity);
            Assert.Equal($"update not installed yet: KB5127648 (download started {Boot.AddMinutes(8):HH:mm})", s.Note);

            var snap = new RoundSnapshot(Boot.AddMinutes(30), Since, new[] { new KeyValuePair<string, Observation>("SRV01", o) });
            Assert.False(snap.Rows[0].ChecksDone);
            var tracker = new RoundTracker();
            Assert.DoesNotContain(tracker.Update(snap), e => e.Kind == RoundEventKind.AllClean);
            Assert.False(tracker.AllClean);

            o.Verification = WithInFlight();
            snap = new RoundSnapshot(Boot.AddMinutes(32), Since, new[] { new KeyValuePair<string, Observation>("SRV01", o) });
            Assert.Contains(tracker.Update(snap), e => e.Kind == RoundEventKind.AllClean);
        }

        [Fact]
        public void UpdatesInFlight_OnAFlaggedRow_AreNamedToo()
        {
            var o = Back(WithInFlight(Downloading, new UpdateInFlight(Boot.AddMinutes(9), "x (KB5127690)", "KB5127690", "{d}", true)));
            o.WuRebootRequired = true;
            var s = StateClassifier.Classify(o, Since, Boot.AddMinutes(30));
            Assert.Equal(BackFlag.RebootPending, s.Flag);
            Assert.Equal("reboot pending again (WU); 2 updates not installed yet: KB5127648, KB5127690", s.Note);
        }

        // FIELD-NOTES 22-10: a workstation up since two days before was downloading a feature update and the row said nothing
        [Theory]
        [InlineData(false, false, "NoRestartPending", "update not installed yet: KB5127648 (download started {0})")]
        [InlineData(true, false, "RestartPending", "WU; update not installed yet: KB5127648 (download started {0})")]
        [InlineData(false, true, "Installing", "update not installed yet: KB5127648 (download started {0})")]
        public void UpdateInFlight_BeforeTheRestart_IsNamed(bool wuReboot, bool tiWorker, string state, string note)
        {
            var o = new Observation { Ping = true, Wmi = WmiResult.Ok, LastBoot = Since.AddDays(-2), WuRebootRequired = wuReboot, TiWorker = tiWorker,
                                      UpdatesInFlight = new[] { Downloading } };
            var s = StateClassifier.Classify(o, Since, Boot.AddMinutes(30));
            Assert.Equal(state, s.State.ToString());
            Assert.Equal(string.Format(note, Downloading.Time.ToString("HH:mm")), s.Note);
            var snap = new RoundSnapshot(Boot.AddMinutes(30), Since, new[] { new KeyValuePair<string, Observation>("SRV01", o) });
            Assert.Same(Downloading, Assert.Single(snap.Rows[0].UpdatesInFlight));
            Assert.Contains("\"updatesInFlight\":[{\"update\":\"KB5127648\",\"stage\":\"downloading\"", RoundJson.State(snap, false, null).Replace(" ", ""));
        }

        // TiWorker alone can be servicing finishing or cleanup; with Windows Update's 43 the row names the install
        [Fact]
        public void InstallStarted_WithTiWorker_IsCalledInstalling()
        {
            var installing = new UpdateInFlight(Boot.AddMinutes(9), "x (KB5127690)", "KB5127690", "{d}", true);
            var before = new Observation { Ping = true, Wmi = WmiResult.Ok, LastBoot = Since.AddDays(-2), TiWorker = true, UpdatesInFlight = new[] { installing } };
            var s = StateClassifier.Classify(before, Since, Boot.AddMinutes(30));
            Assert.Equal(ServerState.Installing, s.State);
            Assert.Equal($"installing KB5127690 (started {Boot.AddMinutes(9):HH:mm})", s.Note);

            var flagged = Back(WithInFlight(installing));
            flagged.TiWorker = true;
            flagged.WuRebootRequired = true;
            Assert.Equal($"reboot pending again (WU); installing KB5127690 (started {Boot.AddMinutes(9):HH:mm})",
                         StateClassifier.Classify(flagged, Since, Boot.AddMinutes(30)).Note);

            // Downloading with TiWorker running is something else's servicing: still "not installed yet"
            var downloading = Back(WithInFlight(Downloading));
            downloading.TiWorker = true;
            Assert.StartsWith("update not installed yet: KB5127648", StateClassifier.Classify(downloading, Since, Boot.AddMinutes(30)).Note);
        }

        [Fact]
        public void UpdateInFlight_DoesNotBlockARestart() =>
            Assert.True(Restart.RestartGuard.Check(Back(WithInFlight(Downloading)), StateClassifier.Classify(Back(WithInFlight(Downloading)), Since, Boot.AddMinutes(30))).Allowed);

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
