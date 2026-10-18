using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WUGrid.Core;
using WUGrid.Core.Verification;
using Xunit;

namespace WUGrid.Core.Tests
{
    public class ExpectedListTests
    {
        [Fact]
        public void Parse_ReadsBareEveryServerAndServerEntries_WithSlashSlashComments()
        {
            var list = ExpectedList.Parse(
                "// header line\n" +
                "MSSQL$MICROSOFT##WID // WSUS removal\n" +          // a # inside a real service name is not a comment
                "*\\WsusService\r\n" +
                " SRV01\\Bonjour Service  // retired — by CONTOSO\\admin, 18-10 13:40\n" +
                "\n" +
                "srv01\\bonjour service // duplicate, dropped\n" +
                "\\\n");
            Assert.Equal(3, list.Count);
            Assert.Null(list[0].Server);
            Assert.Equal("MSSQL$MICROSOFT##WID", list[0].Service);
            Assert.Equal("WSUS removal", list[0].Comment);
            Assert.Null(list[1].Server);
            Assert.Equal("WsusService", list[1].Service);
            Assert.Equal("SRV01", list[2].Server);
            Assert.Equal("Bonjour Service", list[2].Service);
            Assert.StartsWith("retired", list[2].Comment);
        }

        [Fact]
        public void Matches_OneServerOrEveryServer_CaseInsensitive()
        {
            var any = new ExpectedStop("*", "WsusService");
            var one = new ExpectedStop("SRV01", "WsusService");
            Assert.True(any.Matches("srv09", "wsusservice"));
            Assert.True(one.Matches("srv01", "WsusService"));
            Assert.False(one.Matches("SRV02", "WsusService"));
            Assert.False(any.Matches("SRV01", "W32Time"));
            Assert.Equal("every server", any.ServerLabel);
        }

        [Fact]
        public void Find_PrefersTheServerSpecificEntry()
        {
            var entries = new[] { new ExpectedStop(null, "Wid", "everywhere"), new ExpectedStop("SRV01", "Wid", "here") };
            Assert.Equal("here", ExpectedList.Find(entries, "SRV01", "Wid").Comment);
            Assert.Equal("everywhere", ExpectedList.Find(entries, "SRV02", "Wid").Comment);
            Assert.Null(ExpectedList.Find(entries, "SRV02", "W32Time"));
            Assert.Equal(new[] { "Wid" }, ExpectedList.ServicesFor(entries, "SRV02"));
        }

        [Fact]
        public void SaveAndLoad_RoundTrip()
        {
            var path = Path.Combine(Path.GetTempPath(), "wugrid-test-" + Guid.NewGuid() + ".txt");
            try
            {
                ExpectedList.Save(path, new[] { new ExpectedStop(null, "MSSQL$MICROSOFT##WID", "WSUS removal — by CONTOSO\\admin, 18-10 13:40"), new ExpectedStop("SRV01", "KNM_PM") });
                var back = ExpectedList.Load(path);
                Assert.Equal(2, back.Count);
                Assert.Null(back[0].Server);
                Assert.Equal("MSSQL$MICROSOFT##WID", back[0].Service);
                Assert.Equal("WSUS removal — by CONTOSO\\admin, 18-10 13:40", back[0].Comment);
                Assert.Equal("SRV01", back[1].Server);
                Assert.Equal("", back[1].Comment);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Load_MissingFileIsEmpty() => Assert.Empty(ExpectedList.Load(@"C:\nonexistent\wugrid-expected.txt"));

        [Fact]
        public void Stamp_SaysWhyWhoAndWhen()
        {
            var when = new DateTime(2026, 10, 18, 13, 40, 0);
            Assert.Equal("WSUS removal — by CONTOSO\\admin, 18-10 13:40", ExpectedList.Stamp(" WSUS removal ", "CONTOSO\\admin", when));
            Assert.Equal("by CONTOSO\\admin, 18-10 13:40", ExpectedList.Stamp("", "CONTOSO\\admin", when));
        }
    }

    public class ExpectedStopClassificationTests
    {
        static readonly DateTime Since = new DateTime(2026, 10, 18, 12, 0, 0);
        static readonly DateTime Now = Since.AddMinutes(60);

        static readonly ServiceRecord Wid = new ServiceRecord("MSSQL$MICROSOFT##WID", "Windows Internal Database", "Stopped", "Auto");
        static readonly ServiceRecord AppDb = new ServiceRecord("AppDb", "Application Database", "Stopped", "Disabled");

        /// <summary>Back and checked, with these services not running.</summary>
        static Observation Back(params ServiceRecord[] stopped)
        {
            var o = new Observation { Ping = true, Wmi = WmiResult.Ok, LastBoot = Since.AddMinutes(20) };
            o.Verification = new ServerVerification(Now, o.LastBoot.Value, "17763.8644", new UpdateResult[0], new RestartRecord[0], new BootRecord[0], false,
                                                    stopped.Select(s => new ServiceIssue(s, null)).ToList(), true, true, true);
            return o;
        }

        [Fact]
        public void ExpectedStops_AreNotedNotFlagged()
        {
            var s = StateClassifier.Classify(Back(Wid, AppDb), Since, Now, new[] { "mssql$microsoft##wid", "AppDb" });
            Assert.True(s.IsClean);
            Assert.Equal("2 expected stops", s.Note);
        }

        [Fact]
        public void OneExpectedOneNot_FlagsTheOtherOnly()
        {
            var s = StateClassifier.Classify(Back(Wid, AppDb), Since, Now, new[] { "MSSQL$MICROSOFT##WID" });
            Assert.Equal(BackFlag.ServiceStopped, s.Flag);
            Assert.Equal("service not running: Application Database; expected stop: Windows Internal Database", s.Note);
        }

        [Fact]
        public void ExpectedNote_FollowsTheRoutineNote()
        {
            var o = Back(Wid); o.TiWorker = true;
            var s = StateClassifier.Classify(o, Since, Now, new[] { "MSSQL$MICROSOFT##WID" });
            Assert.True(s.IsClean);
            Assert.Equal(BackActivity.ServicingFinishing, s.Activity);
            Assert.Equal("servicing finishing; expected stop: Windows Internal Database", s.Note);
        }

        [Fact]
        public void WithoutExpectedStops_NothingChanges()
        {
            var s = StateClassifier.Classify(Back(Wid), Since, Now);
            Assert.Equal(BackFlag.ServiceStopped, s.Flag);
            Assert.Equal("service not running: Windows Internal Database", s.Note);
        }

        [Fact]
        public void Snapshot_SplitsEachServersStops_ByTheEntriesThatApply()
        {
            var entries = new[] { new ExpectedStop(null, "MSSQL$MICROSOFT##WID", "WSUS removal"), new ExpectedStop("SRV01", "AppDb", "retired") };
            var snap = new RoundSnapshot(Now, Since, new Dictionary<string, Observation> { ["SRV01"] = Back(Wid, AppDb), ["SRV02"] = Back(Wid, AppDb) }, entries);

            var srv01 = snap.Rows[0];
            Assert.True(srv01.Status.IsClean);
            Assert.Empty(srv01.UnexpectedStops);
            Assert.Equal(new[] { "MSSQL$MICROSOFT##WID", "AppDb" }, srv01.ExpectedStops.Select(x => x.Issue.Service.Name));   // the verification's order
            Assert.Null(srv01.ExpectedStops[0].Entry.Server);    // matched by the every-server entry
            Assert.Equal("SRV01", srv01.ExpectedStops[1].Entry.Server);

            var srv02 = snap.Rows[1];
            Assert.Equal(BackFlag.ServiceStopped, srv02.Status.Flag);
            Assert.Equal("AppDb", Assert.Single(srv02.UnexpectedStops).Service.Name);
            Assert.Equal("WSUS removal", Assert.Single(srv02.ExpectedStops).Entry.Comment);

            // Only expected stops left: the round is all clean
            var tracker = new RoundTracker();
            tracker.Update(new RoundSnapshot(Now, Since, new Dictionary<string, Observation> { ["SRV01"] = Back(Wid, AppDb) }, entries));
            Assert.True(tracker.AllClean);
        }
    }
}
