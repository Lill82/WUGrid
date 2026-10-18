using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WUGrid.Core
{
    /// <summary>Server names as people type or paste them, and the list file the GUI keeps between runs.</summary>
    public static class ServerList
    {
        static readonly char[] Separators = { ',', ';', ' ', '\t', '\r', '\n' };

        /// <summary>The GUI's list, kept between runs; also what <c>wugrid-cli watch</c> watches when given no servers.</summary>
        public static string DefaultPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WUGrid", "servers.txt");

        /// <summary>Splits on commas, semicolons, spaces and lines; # comments out the rest of its line; drops \\ prefixes,
        /// blanks and duplicates.</summary>
        public static IReadOnlyList<string> Parse(string text) =>
            (text ?? "").Split('\n')
                        .Select(line => line.IndexOf('#') is var i && i >= 0 ? line.Substring(0, i) : line)
                        .SelectMany(line => line.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
                        .Select(n => n.Trim().TrimStart('\\'))
                        .Where(n => n != "")
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

        /// <summary>One name per line (or any of the separators). Missing file = empty list.</summary>
        public static IReadOnlyList<string> Load(string path) =>
            File.Exists(path) ? Parse(File.ReadAllText(path)) : new List<string>();

        public static void Save(string path, IEnumerable<string> names)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, new[] { "# WUGrid server list: one name per line" }
                                     .Concat(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)));
        }
    }
}
