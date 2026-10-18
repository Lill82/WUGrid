using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WUGrid.Core.Localization;
using Xunit;

namespace WUGrid.Core.Tests
{
    public class TranslationsTests
    {
        [Fact]
        public void Parse_KeysOnEnglish_AndUnescapes()
        {
            var t = Translations.Parse("es", "# comment\n\nServers…\tServidores…\r\nCould not open {0}:\\n{1}\tNo se pudo abrir {0}:\\n{1}\n");
            Assert.Equal("Servidores…", t.Get("Servers…"));
            Assert.Equal("No se pudo abrir C:\\x:\nboom", t.Format("Could not open {0}:\n{1}", "C:\\x", "boom"));
            Assert.Equal("Not translated", t.Get("Not translated"));   // falls back to the English
        }

        [Fact]
        public void Parse_RefusesALineWithoutTab_AndAKeyTwice()
        {
            Assert.Throws<FormatException>(() => Translations.Parse("es", "Servers"));
            Assert.Throws<FormatException>(() => Translations.Parse("es", "A\tB\nA\tC"));
        }

        [Fact]
        public void EscapeAndUnescape_RoundTrip()
        {
            const string text = "a\\b\tc\nd";
            Assert.Equal(@"a\\b\tc\nd", Translations.Escape(text));
            Assert.Equal(text, Translations.Unescape(Translations.Escape(text)));
        }

        [Theory]
        [InlineData("ES", "ca", "es")]
        [InlineData(" en ", "ca", "en")]
        [InlineData("fr", "ca", "ca")]
        [InlineData(null, "ca", "ca")]
        [InlineData("xx", null, null)]
        public void Resolve_TakesAKnownCode_ElseTheFallback(string requested, string fallback, string expected) =>
            Assert.Equal(expected, Translations.Resolve(requested, fallback));

        [Fact]
        public void Placeholders_KeepTheirFormat() =>
            Assert.Equal(new[] { "{0}", "{1:HH:mm}" }, Translations.Placeholders("{1:HH:mm} at {0}, {{literal}} {0}"));

        public static IEnumerable<object[]> Languages() =>
            Translations.Languages.Where(l => l.Code != "en").Select(l => new object[] { l.Code });

        /// <summary>Every L.T / L.F / L.In text in the GUI is in each list, with the same placeholders, and nothing else is.</summary>
        [Theory]
        [MemberData(nameof(Languages))]
        public void GuiText_IsTranslatedCompletely(string language)
        {
            var gui = Path.Combine(RepoRoot(), "src", "WUGrid.Gui");
            var used = GuiKeys(gui);
            Assert.True(used.Count > 100, $"only {used.Count} texts found in the GUI source");
            var t = Translations.Parse(language, File.ReadAllText(Path.Combine(gui, $"Strings.{language}.tsv")));

            var missing = used.Where(k => !t.Entries.ContainsKey(k)).ToList();
            Assert.True(missing.Count == 0, $"{language}: not translated:\n" + string.Join("\n", missing.Select(Translations.Escape)));
            var unused = t.Entries.Keys.Where(k => !used.Contains(k)).ToList();
            Assert.True(unused.Count == 0, $"{language}: no longer in the GUI:\n" + string.Join("\n", unused.Select(Translations.Escape)));
            Assert.All(t.Entries, kv =>
            {
                Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key);
                Assert.True(Translations.Placeholders(kv.Key).SequenceEqual(Translations.Placeholders(kv.Value)), $"{language}: placeholders differ in \"{kv.Value}\"");
            });
        }

        static readonly Regex Call = new Regex(@"\bL\.(?:T|F|In)\((?:\w+,\s*)?""((?:[^""\\]|\\.)*)""");

        static HashSet<string> GuiKeys(string dir) =>
            new HashSet<string>(Directory.GetFiles(dir, "*.cs")
                .SelectMany(f => Call.Matches(File.ReadAllText(f)).Cast<Match>())
                .Select(m => Regex.Replace(m.Groups[1].Value, @"\\(.)", c => c.Groups[1].Value == "n" ? "\n" : c.Groups[1].Value)), StringComparer.Ordinal);

        static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "WUGrid.sln"))) return dir.FullName;
            throw new DirectoryNotFoundException("WUGrid.sln not found above " + AppDomain.CurrentDomain.BaseDirectory);
        }
    }
}
