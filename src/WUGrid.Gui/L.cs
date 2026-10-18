using System.IO;
using WUGrid.Core;
using WUGrid.Core.Localization;

namespace WUGrid.Gui
{
    /// <summary>
    /// The windows' text in the chosen language: <c>L.T("Servers…")</c>, <c>L.F("Reboot {0}…", name)</c>. Spanish and
    /// Catalan come from the embedded Strings.es.tsv / Strings.ca.tsv, keyed by the English text; a test keeps them
    /// complete. What Core writes (states, notes, events, reasons, the error catalog) stays English: it also goes to the
    /// CLI, the reports and the logs.
    /// </summary>
    static class L
    {
        /// <summary>In settings.txt: en, es or ca; not set = <see cref="DefaultLanguage"/>.</summary>
        public const string SettingName = "language";

        public const string DefaultLanguage = "ca";

        static Translations current = Translations.English;

        public static string Language => current.Language;

        /// <summary>Picks the language for this run: <paramref name="requested"/> (--lang), else the setting, else Catalan.</summary>
        public static void Start(string requested)
        {
            current = Load(Translations.Resolve(requested, null) ?? Translations.Resolve(Settings.Get(SettingName), DefaultLanguage));
        }

        public static string T(string english) => current.Get(english);

        public static string F(string english, params object[] args) => current.Format(english, args);

        /// <summary>Another language's text, for the message that says the change applies at the next start.</summary>
        public static string In(string language, string english) => Load(language).Get(english);

        static Translations Load(string language)
        {
            if (language == "en") return Translations.English;
            using (var stream = typeof(L).Assembly.GetManifestResourceStream($"WUGrid.Strings.{language}.tsv"))
            {
                if (stream == null) return Translations.English;
                using (var reader = new StreamReader(stream)) return Translations.Parse(language, reader.ReadToEnd());
            }
        }
    }
}
