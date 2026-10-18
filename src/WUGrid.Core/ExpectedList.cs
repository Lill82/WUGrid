using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WUGrid.Core
{
    /// <summary>
    /// A service the operator expects not to run after the restart — a role being removed, an application retired — on one
    /// server or on every server. The server is not flagged for it; the stop is still shown, with the reason, wherever
    /// services are. Nothing is ever sent to a server for it.
    /// </summary>
    public sealed class ExpectedStop
    {
        public ExpectedStop(string server, string service, string comment = "")
        {
            Server = string.IsNullOrWhiteSpace(server) || server.Trim() == "*" ? null : server.Trim();
            Service = (service ?? "").Trim();
            Comment = (comment ?? "").Trim();
        }

        /// <summary>Null: every server in the round.</summary>
        public string Server { get; }

        /// <summary>The service name (not the display name), as Win32_Service.Name has it.</summary>
        public string Service { get; }

        /// <summary>Why, and who said so; free text.</summary>
        public string Comment { get; }

        public string ServerLabel => Server ?? "every server";

        public bool AppliesTo(string server) => Server == null || string.Equals(Server, server, StringComparison.OrdinalIgnoreCase);

        public bool Matches(string server, string service) => AppliesTo(server) && string.Equals(Service, service, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The expected-stops list and its file, one entry per line: <c>SERVICE</c> or <c>SERVER\SERVICE</c> (<c>*\SERVICE</c> for
    /// every server), with <c>//</c> starting a comment. A service name can hold almost anything — <c>MSSQL$MICROSOFT##WID</c>
    /// is real, so <c>#</c> cannot mark comments here — but never a slash or a backslash, and a host name never holds a slash
    /// either, so these two separators are safe.
    /// </summary>
    public static class ExpectedList
    {
        public const string Header = @"// WUGrid expected service stops: one per line, SERVICE or SERVER\SERVICE (*\SERVICE = every server), then // why";

        /// <summary>The GUI's list, kept between runs; also what <c>wugrid-cli watch</c> and <c>verify</c> read when given no --expected.</summary>
        public static string DefaultPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WUGrid", "expected.txt");

        public static IReadOnlyList<ExpectedStop> Parse(string text)
        {
            var entries = new List<ExpectedStop>();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw;
                var comment = "";
                var slashes = line.IndexOf("//", StringComparison.Ordinal);
                if (slashes >= 0)
                {
                    comment = line.Substring(slashes + 2).Trim();
                    line = line.Substring(0, slashes);
                }
                line = line.Trim();
                if (line == "") continue;
                var sep = line.IndexOf('\\');
                var entry = sep < 0 ? new ExpectedStop(null, line, comment) : new ExpectedStop(line.Substring(0, sep), line.Substring(sep + 1), comment);
                if (entry.Service == "" || entries.Any(e => Same(e, entry))) continue;
                entries.Add(entry);
            }
            return entries;
        }

        /// <summary>Same server (or both every server) and same service, whatever the comment.</summary>
        public static bool Same(ExpectedStop a, ExpectedStop b) =>
            string.Equals(a.Server, b.Server, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Service, b.Service, StringComparison.OrdinalIgnoreCase);

        /// <summary>Missing file = empty list.</summary>
        public static IReadOnlyList<ExpectedStop> Load(string path) => File.Exists(path) ? Parse(File.ReadAllText(path)) : new List<ExpectedStop>();

        public static void Save(string path, IEnumerable<ExpectedStop> entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, new[] { Header }.Concat(entries.Select(Format)));
        }

        public static string Format(ExpectedStop e) => $"{e.Server ?? "*"}\\{e.Service}" + (e.Comment != "" ? "  // " + e.Comment : "");

        /// <summary>The service names expected to be stopped on this server.</summary>
        public static IReadOnlyCollection<string> ServicesFor(IEnumerable<ExpectedStop> entries, string server) =>
            new HashSet<string>(entries.Where(e => e.AppliesTo(server)).Select(e => e.Service), StringComparer.OrdinalIgnoreCase);

        /// <summary>The entry that says this service's stop is expected on this server, if any; a server-specific entry wins.</summary>
        public static ExpectedStop Find(IEnumerable<ExpectedStop> entries, string server, string service) =>
            entries.Where(e => e.Matches(server, service)).OrderBy(e => e.Server == null ? 1 : 0).FirstOrDefault();

        /// <summary>"WSUS removal — by CONTOSO\admin, 18-10 13:40": the comment WUGrid writes for an entry added in the GUI.</summary>
        public static string Stamp(string why, string operatorName, DateTime now) =>
            (string.IsNullOrWhiteSpace(why) ? "" : why.Trim() + " — ") + $"by {operatorName}, {now:dd-MM HH:mm}";
    }
}
