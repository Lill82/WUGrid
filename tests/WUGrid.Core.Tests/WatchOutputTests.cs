using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using WUGrid.Core;
using WUGrid.Core.Reports;
using WUGrid.Core.Verification;
using Xunit;

namespace WUGrid.Core.Tests
{
    public class RoundJsonTests
    {
        static readonly DateTime Since = new DateTime(2026, 10, 17, 15, 0, 0);
        static readonly Baseline Before = new Baseline(Since.AddMinutes(-1), "17763.8644", new Dictionary<string, string>());

        static RoundSnapshot Round()
        {
            var failed = new Observation { Ping = true, Wmi = WmiResult.Ok, LastBoot = Since.AddMinutes(10), Build = "17763.8880", Baseline = Before };
            failed.Verification = new ServerVerification(
                Since.AddMinutes(20), failed.LastBoot.Value, "17763.8880",
                new[] { new UpdateResult(Since.AddMinutes(12), "2026-09 Cumulative Update (KB5900001)", "KB5900001", "{id}", false, unchecked((int)0x800F0840)) },
                new RestartRecord[0], new BootRecord[0], false,
                new[] { new ServiceIssue(new ServiceRecord("AppDbSvc", "App \"Db\"", "Stopped", "Disabled"), null) }, true, true, true);
            var booting = new Observation { Ping = true, Wmi = WmiResult.Failed, WmiError = "Access is denied.", WmiHResult = unchecked((int)0x80070005) };
            var pending = new Observation { Ping = true, Wmi = WmiResult.Ok, LastBoot = Since.AddDays(-90), CbsRebootPending = true, CbsPackagesPending = 40, WuRebootRequired = true };
            return new RoundSnapshot(Since.AddMinutes(30), Since, new Dictionary<string, Observation>
            {
                ["SRV03"] = pending,
                ["SRV01"] = failed,
                ["SRV02"] = booting,
                ["SRV04"] = new Observation(),
            });
        }

        static Dictionary<string, object> Parse(string json)
        {
            Assert.DoesNotContain("\n", json);
            return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
        }

        static IEnumerable<Dictionary<string, object>> Objects(object array) => ((IEnumerable)array).Cast<Dictionary<string, object>>();

        [Fact]
        public void State_IsOneJsonObject_WithEveryServer()
        {
            var state = Parse(RoundJson.State(Round(), false, TimeSpan.FromSeconds(5)));
            Assert.StartsWith("2026-10-17T15:30:00", (string)state["updated"]);
            Assert.Equal(5, state["intervalSeconds"]);
            Assert.Equal(4, state["servers"]);
            Assert.Equal(false, state["allClean"]);
            var counts = (Dictionary<string, object>)state["counts"];
            Assert.Equal(1, counts["flagged"]);
            Assert.Equal(1, counts["restartPending"]);
            Assert.Equal(new[] { "SRV01", "SRV02", "SRV03", "SRV04" }, Objects(state["rows"]).Select(r => (string)r["server"]));
        }

        [Fact]
        public void State_Rows_CarryStatesFlagsAndCodes_ByTheirStableNames()
        {
            var rows = Objects(Parse(RoundJson.State(Round(), false, null))["rows"]).ToDictionary(r => (string)r["server"]);

            var failed = rows["SRV01"];
            Assert.Equal("Back", failed["state"]);
            Assert.Equal(new object[] { "UpdateFailed", "ServiceStopped" }, (object[])failed["flags"]);
            Assert.Equal(false, failed["clean"]);
            Assert.Equal(true, failed["checksDone"]);
            Assert.Equal(1200, failed["elapsedSeconds"]);
            Assert.Equal("17763.8644", failed["buildBefore"]);
            var update = Objects(failed["updatesFailed"]).Single();
            Assert.Equal(("KB5900001", "0x800F0840"), ((string)update["update"], (string)update["code"]));
            Assert.Equal(new object[] { "AppDbSvc" }, (object[])failed["servicesNotRunning"]);

            Assert.Equal("Booting", rows["SRV02"]["state"]);
            Assert.Equal("0x80070005", rows["SRV02"]["wmiError"]);
            Assert.Equal(true, rows["SRV02"]["likelyTransient"]);

            Assert.Equal("RestartPending", rows["SRV03"]["state"]);
            Assert.Equal(new object[] { "CBS 40 pkgs", "WU" }, (object[])rows["SRV03"]["restartPending"]);
            Assert.Null(rows["SRV03"]["wmiError"]);

            Assert.Equal("Checking", rows["SRV04"]["state"]);
            Assert.Null(rows["SRV04"]["ping"]);
            Assert.Null(rows["SRV04"]["lastBoot"]);
        }

        [Fact]
        public void Event_IsOneLine_WithBothSidesOfATransition()
        {
            var t = new RoundTracker();
            var seen = t.Update(new RoundSnapshot(Since, Since, new[] { new KeyValuePair<string, Observation>("SRV01", new Observation { Ping = true, Wmi = WmiResult.Ok, LastBoot = Since.AddDays(-1), TiWorker = true }) })).Single();
            var down = t.Update(new RoundSnapshot(Since.AddMinutes(3), Since, new[] { new KeyValuePair<string, Observation>("SRV01", new Observation { Ping = false, DownSince = Since.AddMinutes(3) }) })).Single();

            var first = Parse(RoundJson.Event(seen));
            Assert.Equal(("Seen", "SRV01", "Installing"), ((string)first["kind"], (string)first["server"], (string)first["state"]));
            Assert.False(first.ContainsKey("from"));

            var change = Parse(RoundJson.Event(down));
            Assert.Equal("Transition", change["kind"]);
            Assert.Equal("INSTALLING -> DOWN", change["text"]);
            Assert.Equal(("INSTALLING", "DOWN"), ((string)change["from"], (string)change["label"]));
            Assert.StartsWith("2026-10-17T15:03:00", (string)change["time"]);
        }

        [Fact]
        public void Event_RoundWide_HasNoServer()
        {
            var e = Parse(RoundJson.Event(new RoundEvent(Since, RoundEventKind.AllClean, null, "ALL 3 BACK CLEAN")));
            Assert.Null(e["server"]);
            Assert.False(e.ContainsKey("state"));
        }
    }

    public class ConsoleGridTests
    {
        static readonly DateTime Since = new DateTime(2026, 10, 17, 15, 0, 0);

        static Observation Back() => new Observation { Ping = true, Wmi = WmiResult.Ok, LastBoot = Since.AddMinutes(5) };
        static Observation Down() => new Observation { Ping = false, DownSince = Since.AddMinutes(10) };

        static RoundSnapshot Snap(IEnumerable<(string Name, Observation O)> rows) =>
            new RoundSnapshot(Since.AddMinutes(20), Since, rows.Select(r => new KeyValuePair<string, Observation>(r.Name, r.O)));

        static string Text(IReadOnlyList<TextRun> line) => string.Concat(line.Select(r => r.Text));

        [Fact]
        public void Frame_FitsTheWindow_AndLeavesTheLastLineFree()
        {
            var snap = Snap(Enumerable.Range(1, 8).Select(i => ($"VERYLONGSERVERNAME{i:00}", Back())));
            var events = Enumerable.Range(0, 100).Select(i => new RoundEvent(Since, RoundEventKind.Transition, "SRV01", new string('x', 300))).ToList();
            var frame = new ConsoleGrid().Frame(snap, events, new string('t', 200), 60, 20);
            Assert.True(frame.Count <= 19);
            Assert.All(frame, l => Assert.True(Text(l).Length <= 60));
        }

        [Fact]
        public void Frame_ShowsEveryServer_WhenTheyFit()
        {
            var frame = new ConsoleGrid().Frame(Snap(new[] { ("SRV01", Back()), ("SRV02", Down()) }), new RoundEvent[0], "title", 120, 30);
            var lines = frame.Select(Text).ToList();
            Assert.Contains(lines, l => l.StartsWith("SRV01 ") && l.Contains("BACK"));
            Assert.Contains(lines, l => l.StartsWith("SRV02 ") && l.Contains("DOWN") && l.Contains("10m00s"));
            Assert.Equal(ConsoleColor.Red, frame.Single(l => Text(l).StartsWith("SRV02")).Single().Color);
        }

        [Fact]
        public void Frame_FoldsCleanServersFirst_WhenTheyDoNotFit()
        {
            var rows = Enumerable.Range(1, 30).Select(i => ($"SRV{i:00}", Back())).Concat(new[] { ("SRV99", Down()) });
            var lines = new ConsoleGrid().Frame(Snap(rows), new RoundEvent[0], "title", 120, 15).Select(Text).ToList();
            Assert.Contains(lines, l => l.StartsWith("SRV99 ") && l.Contains("DOWN"));
            Assert.Contains(lines, l => l.StartsWith("BACK clean (30): SRV01 SRV02"));
            Assert.DoesNotContain(lines, l => l.StartsWith("SRV01 "));
        }

        [Fact]
        public void Frame_CutsWhatStillDoesNotFit_AndSaysHowMany()
        {
            var rows = Enumerable.Range(1, 30).Select(i => ($"SRV{i:00}", Down()));
            var lines = new ConsoleGrid().Frame(Snap(rows), new RoundEvent[0], "title", 120, 15).Select(Text).ToList();
            var shown = lines.Count(l => l.StartsWith("SRV"));
            Assert.Contains($"+{30 - shown} more not shown: enlarge the window", lines);
            Assert.True(lines.Count <= 14);
        }

        [Fact]
        public void Frame_KeepsTheLastBootWhileDown()
        {
            var grid = new ConsoleGrid();
            grid.Frame(Snap(new[] { ("SRV01", Back()) }), new RoundEvent[0], "title", 120, 30);
            var lines = grid.Frame(Snap(new[] { ("SRV01", Down()) }), new RoundEvent[0], "title", 120, 30).Select(Text);
            Assert.Contains(lines, l => l.StartsWith("SRV01 ") && l.Contains("DOWN") && l.Contains("17-10 15:05"));
        }

        [Fact]
        public void Frame_ShowsTheNewestEvents_InTheSpaceLeft()
        {
            var events = Enumerable.Range(0, 50).Select(i => new RoundEvent(Since.AddSeconds(i), RoundEventKind.Transition, "SRV01", $"event {i}")).ToList();
            var lines = new ConsoleGrid().Frame(Snap(new[] { ("SRV01", Back()) }), events, "title", 120, 15).Select(Text).ToList();
            Assert.EndsWith("event 49", lines.Last());
            Assert.DoesNotContain(lines, l => l.EndsWith("event 0"));
        }
    }
}
