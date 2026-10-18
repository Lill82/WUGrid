using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WUGrid.Core
{
    /// <summary>Small choices kept between runs, one <c>name=value</c> per line in <see cref="DefaultPath"/>.</summary>
    public static class Settings
    {
        public static string DefaultPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WUGrid", "settings.txt");

        /// <summary>Null when not set, or when the file cannot be read.</summary>
        public static string Get(string name, string path = null)
        {
            try
            {
                path = path ?? DefaultPath;
                return File.Exists(path) && Parse(File.ReadAllText(path)).TryGetValue(name, out var v) ? v : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        /// <summary>Sets one value, keeping the others. Throws when the file cannot be written.</summary>
        public static void Set(string name, string value, string path = null)
        {
            path = path ?? DefaultPath;
            var all = File.Exists(path) ? Parse(File.ReadAllText(path)) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            all[name] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, new[] { "# WUGrid settings" }.Concat(all.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        }

        internal static Dictionary<string, string> Parse(string text)
        {
            var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                var eq = line.IndexOf('=');
                if (line.StartsWith("#") || eq <= 0) continue;
                all[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return all;
        }

        public static bool IsOn(string value) =>
            value != null && (value.Equals("on", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }
}
