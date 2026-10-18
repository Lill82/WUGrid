using System.IO;
using System.Linq;
using WUGrid.Core.Errors;
using Xunit;

namespace WUGrid.Core.Tests
{
    public class ErrorCatalogTests
    {
        [Fact]
        public void BothListsLoad_AndEveryEntryIsUsable()
        {
            Assert.True(ErrorCatalog.All.Count > 400);
            Assert.All(ErrorCatalog.All, e =>
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Description), e.Hex);
                Assert.All(e.Source.Split(','), s => Assert.True(ErrorCatalog.Sources.ContainsKey(s), $"{e.Hex}: unknown source {s}"));
            });
        }

        [Fact]
        public void FieldNotesCode_IsExplained() =>
            Assert.Equal("CBS_E_SESSION_CORRUPT", ErrorCatalog.Lookup(unchecked((int)0x800F0840))[0].Name);

        [Fact]
        public void WindowsUpdateCode_HasDescriptionAndMitigationFromMicrosoft()
        {
            var e = Assert.Single(ErrorCatalog.Lookup(unchecked((int)0x80240022)));
            Assert.Equal("WU_E_ALL_UPDATES_FAILED", e.Name);
            Assert.Equal("Windows Update", e.Family);
            Assert.NotEqual("", e.Mitigation);
        }

        [Fact]
        public void AccessDenied_ShowsWUGridsRemoteAccessReadingFirst()
        {
            var all = ErrorCatalog.Lookup(unchecked((int)0x80070005));
            Assert.Equal("Remote access", all[0].Family);
            Assert.True(all.Count >= 2);
        }

        [Theory]
        [InlineData("0x800F0840")]
        [InlineData("0X800f0840")]
        [InlineData("800F0840")]
        [InlineData("-2146498496")]
        [InlineData("2148468800")]
        public void TryParse_AcceptsHexSignedAndUnsigned(string text)
        {
            Assert.True(ErrorCatalog.TryParse(text, out var code));
            Assert.Equal(unchecked((int)0x800F0840), code);
        }

        [Theory]
        [InlineData("")]
        [InlineData("corrupt")]
        [InlineData("0x1234567890")]
        public void TryParse_RejectsNonCodes(string text) => Assert.False(ErrorCatalog.TryParse(text, out _));

        [Fact]
        public void UnknownCbsCode_PointsToCbsLog()
        {
            var e = Assert.Single(ErrorCatalog.Lookup(unchecked((int)0x800F0FFF)));
            Assert.Equal("CBS", e.Family);
            Assert.Contains("CBS.log", e.Mitigation);
        }

        [Fact]
        public void UnknownWin32Code_UsesWindowsOwnMessage()
        {
            var e = Assert.Single(ErrorCatalog.Lookup(unchecked((int)0x80070008)));   // ERROR_NOT_ENOUGH_MEMORY, not in the lists
            Assert.Equal("windows", e.Source);
            Assert.NotEqual("", e.Description);
        }

        [Fact]
        public void Parse_RejectsMalformedLines() =>
            Assert.Throws<System.FormatException>(() => ErrorCatalog.Parse(new StringReader("code\tname\n0x1\tonly-two")).ToList());
    }

    public class ServerListTests
    {
        [Fact]
        public void Parse_SplitsOnCommasSpacesAndLines_AndDropsDuplicatesAndPrefixes() =>
            Assert.Equal(new[] { "SRV01", "SRV02", "SRV03", "srv04" },
                         ServerList.Parse("SRV01, \\\\SRV02;SRV03\r\n srv04  srv01\n# comment"));

        [Fact]
        public void LoadAndSave_RoundTrip()
        {
            var path = Path.Combine(Path.GetTempPath(), "wugrid-test-" + System.Guid.NewGuid() + ".txt");
            try
            {
                ServerList.Save(path, new[] { "B01", "A01" });
                Assert.Equal(new[] { "A01", "B01" }, ServerList.Load(path));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Load_MissingFileIsEmpty() => Assert.Empty(ServerList.Load(@"C:\nonexistent\wugrid.txt"));
    }
}
