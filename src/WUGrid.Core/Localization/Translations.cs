using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace WUGrid.Core.Localization
{
    /// <summary>
    /// One language's text for a window, keyed by the English text itself: the code reads as English and a missing
    /// translation falls back to it. Loaded from a list with one <c>english&lt;TAB&gt;translation</c> per line
    /// (<c>#</c> comments; <c>\n</c>, <c>\t</c> and <c>\\</c> escaped). Placeholders are composite-format ones,
    /// <c>{0}</c>, <c>{1:HH:mm}</c>, and a translation keeps the same ones.
    /// </summary>
    public sealed class Translations
    {
        /// <summary>The languages WUGrid's windows come in, English first; each named in its own language.</summary>
        public static readonly IReadOnlyList<(string Code, string Name)> Languages = new[] { ("en", "English"), ("es", "Español"), ("ca", "Català") };

        public static readonly Translations English = new Translations("en", new Dictionary<string, string>());

        readonly Dictionary<string, string> map;

        Translations(string language, Dictionary<string, string> map)
        {
            Language = language;
            this.map = map;
        }

        public string Language { get; }

        public IReadOnlyDictionary<string, string> Entries => map;

        public string Get(string english) => english != null && map.TryGetValue(english, out var t) ? t : english;

        public string Format(string english, params object[] args) => string.Format(CultureInfo.CurrentCulture, Get(english), args);

        /// <summary>Throws <see cref="FormatException"/> on a line without a tab or a key given twice.</summary>
        public static Translations Parse(string language, string list)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var n = 0;
            foreach (var raw in (list ?? "").Split('\n'))
            {
                n++;
                var line = raw.TrimEnd('\r');
                if (line.Trim() == "" || line.StartsWith("#")) continue;
                var tab = line.IndexOf('\t');
                if (tab <= 0) throw new FormatException($"{language} line {n}: no tab between the English text and its translation");
                var key = Unescape(line.Substring(0, tab));
                if (map.ContainsKey(key)) throw new FormatException($"{language} line {n}: \"{key}\" is there twice");
                map[key] = Unescape(line.Substring(tab + 1));
            }
            return new Translations(language, map);
        }

        /// <summary>The language to show: a known code asked for (setting or <c>--lang</c>, any case), otherwise
        /// <paramref name="fallback"/>.</summary>
        public static string Resolve(string requested, string fallback)
        {
            var code = requested?.Trim();
            return code != null && Languages.Any(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ? code.ToLowerInvariant() : fallback;
        }

        static readonly Regex Placeholder = new Regex(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}");

        /// <summary>"{0} … {1:HH:mm}" → {"{0}", "{1:HH:mm}"}, sorted: what a translation must keep.</summary>
        public static IReadOnlyList<string> Placeholders(string text) =>
            Placeholder.Matches(text ?? "").Cast<Match>().Select(m => m.Value).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();

        public static string Escape(string text) => (text ?? "").Replace("\\", @"\\").Replace("\t", @"\t").Replace("\r", "").Replace("\n", @"\n");

        public static string Unescape(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\\' || i + 1 == text.Length) { sb.Append(text[i]); continue; }
                var c = text[++i];
                sb.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c);
            }
            return sb.ToString();
        }
    }
}
