using System;
using WUGrid.Core;
using Xunit;

namespace WUGrid.Core.Tests
{
    public class StateClassifierTests
    {
        static readonly DateTime Since = new DateTime(2026, 10, 17, 15, 0, 0);
        static readonly DateTime Now = new DateTime(2026, 10, 17, 16, 0, 0);

        static Observation Up(DateTime lastBoot) =>
            new Observation { Ping = true, Wmi = WmiResult.Ok, LastBoot = lastBoot };

        static Observation Back() => Up(Since.AddMinutes(20));

        static ServerStatus Classify(Observation o) => StateClassifier.Classify(o, Since, Now);

        [Fact]
        public void NoPingYet_IsChecking() =>
            Assert.Equal(ServerState.Checking, Classify(new Observation()).State);

        [Fact]
        public void PingUpButNoWmiAnswerYet_IsChecking() =>
            Assert.Equal(ServerState.Checking, Classify(new Observation { Ping = true }).State);

        [Fact]
        public void NoPing_IsDownWithTimeDown()
        {
            var s = Classify(new Observation { Ping = false, DownSince = Now.AddMinutes(-7) });
            Assert.Equal(ServerState.Down, s.State);
            Assert.Equal(TimeSpan.FromMinutes(7), s.Elapsed);
        }

        [Fact]
        public void NameNotResolving_IsDownAndSaysSo() =>
            Assert.Equal("name does not resolve", Classify(new Observation { Ping = false, DownSince = Now, NameUnresolved = true }).Note);

        [Fact]
        public void WmiFailing_IsBootingWithTheError()
        {
            var s = Classify(new Observation { Ping = true, Wmi = WmiResult.Failed, WmiError = "Acceso denegado." });
            Assert.Equal(ServerState.Booting, s.State);
            Assert.Equal("Acceso denegado.", s.Note);
        }

        [Theory]
        [InlineData(unchecked((int)0x80070005), "Acceso denegado.")]
        [InlineData(unchecked((int)0x800706BA), "El servidor RPC no está disponible.")]
        public void AccessDeniedAndRpcUnavailable_GetTheAsteriskInAnyLanguage(int hresult, string localized)
        {
            var s = Classify(new Observation { Ping = true, Wmi = WmiResult.Failed, WmiError = localized, WmiHResult = hresult });
            Assert.Equal(localized + " *", s.Note);
            Assert.True(s.LikelyTransient);
        }

        [Fact]
        public void OtherWmiErrors_HaveNoAsterisk()
        {
            var s = Classify(new Observation { Ping = true, Wmi = WmiResult.Failed, WmiError = "Invalid namespace", WmiHResult = unchecked((int)0x8004100E) });
            Assert.Equal("Invalid namespace", s.Note);
            Assert.False(s.LikelyTransient);
        }

        [Fact]
        public void BootedBeforeSince_WithTiWorker_IsInstalling()
        {
            var o = Up(Since.AddDays(-110)); o.TiWorker = true;
            Assert.Equal(ServerState.Installing, Classify(o).State);
        }

        [Fact]
        public void BootedBeforeSince_NothingPending_IsNoRestartPending()
        {
            var s = Classify(Up(Since.AddDays(-110)));
            Assert.Equal(ServerState.NoRestartPending, s.State);
            Assert.Equal("NO RESTART PENDING", s.Label);
            Assert.False(s.IsClean);   // not done for the round: an admin may restart it anyway
        }

        [Fact]
        public void BootedBeforeSince_PackagesWaiting_IsRestartPending()
        {
            var o = Up(Since.AddDays(-110)); o.CbsRebootPending = true; o.CbsPackagesPending = 551; o.WuRebootRequired = true;
            var s = Classify(o);
            Assert.Equal(ServerState.RestartPending, s.State);
            Assert.Equal("CBS 551 pkgs+WU", s.Note);
        }

        [Fact]
        public void BootedBeforeSince_BareCleanupMark_IsNotARestartPending()
        {
            var o = Up(Since.AddDays(-110)); o.CbsRebootPending = true;
            var s = Classify(o);
            Assert.Equal(ServerState.NoRestartPending, s.State);
            Assert.Equal("component cleanup running", s.Note);
        }

        [Fact]
        public void Back_WithARestartPendingAgain_IsLabelledSo()
        {
            var o = Back(); o.WuRebootRequired = true;
            Assert.Equal("BACK · PENDING", Classify(o).Label);
            Assert.Equal("BACK", Classify(Back()).Label);
        }

        [Fact]
        public void BootedAfterSince_IsBackCleanWithUptime()
        {
            var s = Classify(Back());
            Assert.Equal(ServerState.Back, s.State);
            Assert.True(s.IsClean);
            Assert.Equal(TimeSpan.FromMinutes(40), s.Elapsed);
            Assert.Equal("", s.Note);
        }

        [Fact]
        public void BareCbsRebootMark_IsComponentCleanupNotAReboot()
        {
            var o = Back(); o.CbsRebootPending = true;
            var s = Classify(o);
            Assert.True(s.IsClean);
            Assert.Equal(BackActivity.ComponentCleanup, s.Activity);
            Assert.Equal("component cleanup running", s.Note);
        }

        [Fact]
        public void CbsRebootMarkWithPackages_IsRebootPending()
        {
            var o = Back(); o.CbsRebootPending = true; o.CbsPackagesPending = 473;
            var s = Classify(o);
            Assert.Equal(BackFlag.RebootPending, s.Flag);
            Assert.Equal("reboot pending again (CBS 473 pkgs)", s.Note);
        }

        [Fact]
        public void PackagesWithoutRebootMark_AreNotFlagged()
        {
            var o = Back(); o.CbsPackagesPending = 3;
            Assert.True(Classify(o).IsClean);
        }

        [Fact]
        public void CbsAndWu_AreBothNamed()
        {
            var o = Back(); o.CbsRebootPending = true; o.CbsPackagesPending = 2; o.WuRebootRequired = true;
            Assert.Equal("reboot pending again (CBS 2 pkgs+WU)", Classify(o).Note);
        }

        [Fact]
        public void TiWorkerAfterBoot_IsServicingFinishing()
        {
            var o = Back(); o.TiWorker = true;
            var s = Classify(o);
            Assert.True(s.IsClean);
            Assert.Equal("servicing finishing", s.Note);
        }

        [Fact]
        public void ComponentCleanup_WinsOverServicingNote()
        {
            var o = Back(); o.TiWorker = true; o.CbsRebootPending = true;
            Assert.Equal(BackActivity.ComponentCleanup, Classify(o).Activity);
        }

        [Fact]
        public void BootInTheFutureFromClockSkew_ShowsZeroUptime() =>
            Assert.Equal(TimeSpan.Zero, Classify(Up(Now.AddSeconds(30))).Elapsed);

        [Theory]
        [InlineData(0, 3, 7, "3m07s")]
        [InlineData(0, 59, 59, "59m59s")]
        [InlineData(1, 0, 0, "1h00m")]
        [InlineData(26, 5, 0, "26h05m")]
        public void Durations_FormatLikeTheGrid(int h, int m, int s, string expected) =>
            Assert.Equal(expected, Durations.Format(new TimeSpan(h, m, s)));
    }
}
