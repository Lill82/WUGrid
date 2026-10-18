using System;
using System.IO;
using System.Linq;
using WUGrid.Core;
using Xunit;

namespace WUGrid.Core.Tests
{
    // One class: the tests that start and stop the process-wide log must not run side by side
    public class ActivityLogTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), "WUGridLogTests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            ActivityLog.Stop();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        [Fact]
        public void Format_TimeLevelServerText_OnOneLine()
        {
            var line = ActivityLog.Format(new DateTime(2026, 10, 18, 16, 35, 1, 123), LogLevel.Warn, "SRV01", "WMI: RPC server unavailable\r\n  more", null);
            Assert.Equal("2026-10-18 16:35:01.123 WARN  SRV01  WMI: RPC server unavailable more\r\n", line);
            Assert.StartsWith("2026-10-18 16:35:01.123 INFO  -  started", ActivityLog.Format(new DateTime(2026, 10, 18, 16, 35, 1, 123), LogLevel.Info, null, "started", null));
        }

        [Fact]
        public void Format_ExceptionFollowsIndented_WithTypeAndHResult()
        {
            Exception ex;
            try { throw new InvalidOperationException("boom"); }
            catch (Exception e) { ex = e; }
            var lines = ActivityLog.Format(DateTime.Now, LogLevel.Error, "SRV01", "poll failed", ex).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.EndsWith("poll failed [InvalidOperationException 0x80131509]", lines[0]);
            Assert.Equal("    System.InvalidOperationException: boom", lines[1]);
            Assert.Contains(lines.Skip(2), l => l.StartsWith("       ") && l.Contains(nameof(Format_ExceptionFollowsIndented_WithTypeAndHResult)));
        }

        [Fact]
        public void FileNames_CarryProgramAndDate_OtherFilesHaveNone()
        {
            Assert.Equal("wugrid-cli-2026-10-18.log", ActivityLog.FileName("wugrid-cli", new DateTime(2026, 10, 18, 9, 0, 0)));
            Assert.Equal("WUGrid-2026-10-18-4242.log", ActivityLog.FileName("WUGrid", new DateTime(2026, 10, 18), 4242));
            Assert.Equal(new DateTime(2026, 10, 18), ActivityLog.DateOf("WUGrid-2026-10-18-4242.log", "WUGrid"));
            Assert.Null(ActivityLog.DateOf("wugrid-cli-2026-10-18.log", "WUGrid"));   // the CLI's files are not the GUI's
            Assert.Null(ActivityLog.DateOf("WUGrid-notes.log", "WUGrid"));
        }

        [Fact]
        public void Off_WritesNothing()
        {
            ActivityLog.Stop();
            ActivityLog.Info("SRV01", "nobody hears this");
            Assert.False(ActivityLog.Enabled);
            Assert.False(Directory.Exists(dir));
        }

        [Fact]
        public void On_WritesHeaderAndLines_OffWritesAClosingLine()
        {
            var now = new DateTime(2026, 10, 18, 12, 0, 0);
            var log = ActivityLog.Start(dir, "WUGrid", "WUGrid 0.11.0 --log", () => now);
            ActivityLog.Info("SRV01", "ping: answers");
            ActivityLog.Stop();
            ActivityLog.Info("SRV01", "after it was turned off");

            Assert.Equal(Path.Combine(dir, "WUGrid-2026-10-18.log"), log.File);
            var lines = File.ReadAllLines(log.File);
            Assert.Equal(3, lines.Length);
            Assert.Contains("--- WUGrid 0.11.0 --log | ", lines[0]);
            Assert.EndsWith("INFO  SRV01  ping: answers", lines[1]);
            Assert.EndsWith("--- activity log turned off", lines[2]);
        }

        [Fact]
        public void NewDay_NewFile()
        {
            var now = new DateTime(2026, 10, 18, 23, 59, 59);
            ActivityLog.Start(dir, "wugrid-cli", "wugrid-cli watch", () => now);
            now = now.AddSeconds(2);
            ActivityLog.Info(null, "after midnight");
            Assert.Equal(Path.Combine(dir, "wugrid-cli-2026-10-19.log"), ActivityLog.Current.File);
            Assert.Single(File.ReadAllLines(Path.Combine(dir, "wugrid-cli-2026-10-18.log")));
        }

        [Fact]
        public void Start_PrunesThisProgramsFilesOlderThanKeepDays()
        {
            Directory.CreateDirectory(dir);
            var today = new DateTime(2026, 10, 18, 8, 0, 0);
            string Touch(string name) { var p = Path.Combine(dir, name); File.WriteAllText(p, "x"); return p; }
            var old = Touch(ActivityLog.FileName("WUGrid", today.AddDays(-ActivityLog.KeepDays - 1)));
            var oldSecondCopy = Touch(ActivityLog.FileName("WUGrid", today.AddDays(-40), 77));
            var kept = Touch(ActivityLog.FileName("WUGrid", today.AddDays(-ActivityLog.KeepDays)));
            var cli = Touch(ActivityLog.FileName("wugrid-cli", today.AddDays(-90)));
            var other = Touch("WUGrid-notes.log");

            ActivityLog.Start(dir, "WUGrid", "WUGrid", () => today);

            Assert.False(File.Exists(old));
            Assert.False(File.Exists(oldSecondCopy));
            Assert.True(File.Exists(kept));
            Assert.True(File.Exists(cli));
            Assert.True(File.Exists(other));
        }

        [Fact]
        public void SecondCopySameDay_GetsAFileOfItsOwn()
        {
            Directory.CreateDirectory(dir);
            var now = new DateTime(2026, 10, 18, 12, 0, 0);
            using (new FileStream(Path.Combine(dir, "WUGrid-2026-10-18.log"), FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var log = ActivityLog.Start(dir, "WUGrid", "WUGrid", () => now);
                Assert.Null(log.Problem);
                Assert.Equal(ActivityLog.FileName("WUGrid", now, System.Diagnostics.Process.GetCurrentProcess().Id), Path.GetFileName(log.File));
            }
        }

        [Fact]
        public void Unwritable_SaysWhy_NeverThrows()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dir) ?? dir);
            File.WriteAllText(dir, "a file where the folder should be");
            try
            {
                var log = ActivityLog.Start(dir, "WUGrid", "WUGrid");
                ActivityLog.Error("SRV01", "still no exception");
                Assert.NotNull(log.Problem);
                Assert.Contains(dir, log.Problem);
            }
            finally
            {
                ActivityLog.Stop();
                File.Delete(dir);
            }
        }
    }

    public class SettingsTests
    {
        [Fact]
        public void Parse_NameEqualsValue_SkipsCommentsAndJunk()
        {
            var all = Settings.Parse("# WUGrid settings\r\nactivity-log = on\nno equals sign\n=nameless\nOther=a=b\n");
            Assert.Equal(2, all.Count);
            Assert.Equal("on", all["Activity-Log"]);
            Assert.Equal("a=b", all["other"]);
        }

        [Fact]
        public void Set_KeepsOtherValues_GetReadsBack()
        {
            var path = Path.Combine(Path.GetTempPath(), "WUGridSettingsTests-" + Guid.NewGuid().ToString("N"), "settings.txt");
            try
            {
                Assert.Null(Settings.Get("activity-log", path));
                Settings.Set("other", "x", path);
                Settings.Set("activity-log", "on", path);
                Settings.Set("activity-log", "off", path);
                Assert.Equal("off", Settings.Get("activity-log", path));
                Assert.Equal("x", Settings.Get("other", path));
            }
            finally { Directory.Delete(Path.GetDirectoryName(path), true); }
        }

        [Theory]
        [InlineData("on", true)]
        [InlineData("ON", true)]
        [InlineData("1", true)]
        [InlineData("true", true)]
        [InlineData("off", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsOn(string value, bool on) => Assert.Equal(on, Settings.IsOn(value));
    }
}
