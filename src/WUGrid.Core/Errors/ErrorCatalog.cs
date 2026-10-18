using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WUGrid.Core.Errors
{
    public sealed class ErrorEntry
    {
        public ErrorEntry(int code, string name, string family, string description, string mitigation, string source)
        {
            Code = code;
            Name = name;
            Family = family;
            Description = description;
            Mitigation = mitigation;
            Source = source;
        }

        public int Code { get; }
        public string Name { get; }

        /// <summary>CBS, Windows Update, WMI, Win32, Remote access, Other.</summary>
        public string Family { get; }

        public string Description { get; }
        public string Mitigation { get; }

        /// <summary>Comma-separated keys of <see cref="ErrorCatalog.Sources"/>.</summary>
        public string Source { get; }

        public string Hex => ErrorCatalog.Hex(Code);
    }

    /// <summary>
    /// CBS, Windows Update and WMI error codes with real explanations. Two embedded lists: error-codes.microsoft.tsv,
    /// generated from Microsoft Learn by tools/update_error_catalog.py, and the hand-written error-codes.wugrid.tsv for
    /// the codes WUGrid runs into while probing. Codes not in either fall back to Windows' own message (Win32 codes,
    /// in the workstation's language) or to what the facility says about them.
    /// </summary>
    public static class ErrorCatalog
    {
        public static readonly IReadOnlyDictionary<string, (string Title, string Url)> Sources = new Dictionary<string, (string, string)>
        {
            ["wu-reference"] = ("Microsoft Learn: Windows Update error code list by component",
                                "https://learn.microsoft.com/windows/deployment/update/windows-update-error-reference"),
            ["wu-common"] = ("Microsoft Learn: Common Windows Update errors",
                             "https://learn.microsoft.com/troubleshoot/windows-client/deployment/common-windows-update-errors"),
            ["fix-corruptions"] = ("Microsoft Learn: Fix Windows Update corruptions and installation failures",
                                   "https://learn.microsoft.com/troubleshoot/windows-server/deployment/fix-windows-update-errors"),
            ["netfx35"] = ("Microsoft Learn: .NET Framework 3.5 deployment errors and resolution steps",
                           "https://learn.microsoft.com/windows-hardware/manufacture/desktop/net-framework-35-deployment-errors-and-resolution-steps"),
            ["wmi"] = ("Microsoft Learn: WMI Error Constants", "https://learn.microsoft.com/windows/win32/wmisdk/wmi-error-constants"),
            ["wugrid"] = ("WUGrid field notes", "https://github.com/Lill82/WUGrid/blob/main/docs/FIELD-NOTES.md"),
            ["windows"] = ("Windows system message on this computer", null),
            ["facility"] = ("Derived from the code's facility", null),
        };

        static readonly Lazy<IReadOnlyList<ErrorEntry>> all = new Lazy<IReadOnlyList<ErrorEntry>>(Load);
        static readonly Lazy<ILookup<int, ErrorEntry>> byCode = new Lazy<ILookup<int, ErrorEntry>>(() => all.Value.ToLookup(e => e.Code));

        public static IReadOnlyList<ErrorEntry> All => all.Value;

        /// <summary>Catalog entries for the code (WUGrid's own first), or one fallback entry; never empty.</summary>
        public static IReadOnlyList<ErrorEntry> Lookup(int code)
        {
            var found = byCode.Value[code].OrderBy(e => e.Source == "wugrid" ? 0 : 1).ToList();
            return found.Count > 0 ? found : new List<ErrorEntry> { Fallback(code) };
        }

        /// <summary>"0x800F0840 CBS_E_SESSION_CORRUPT" — for one-line displays.</summary>
        public static string Summary(int code)
        {
            var e = Lookup(code)[0];
            return e.Name == "" ? Hex(code) : $"{Hex(code)} {e.Name}";
        }

        public static string Hex(int code) => "0x" + code.ToString("X8");

        /// <summary>Accepts 0x800F0840, 800F0840, -2146498496 and 2148468800.</summary>
        public static bool TryParse(string text, out int code)
        {
            code = 0;
            var t = (text ?? "").Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            else if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var dec) && t.Length >= 9)
            {
                if (dec < int.MinValue || dec > uint.MaxValue) return false;
                code = unchecked((int)(uint)(dec & 0xFFFFFFFF));
                return true;
            }
            if (t.Length == 0 || t.Length > 8 || !uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return false;
            code = unchecked((int)hex);
            return true;
        }

        static ErrorEntry Fallback(int code)
        {
            var u = unchecked((uint)code);
            var facility = (u >> 16) & 0x1FFF;
            switch (facility)
            {
                case 7:   // FACILITY_WIN32: Windows has the text, in the workstation's language
                    var text = new Win32Exception(code & 0xFFFF).Message;
                    return new ErrorEntry(code, "", "Win32", text, "", "windows");
                case 0x24:
                    return new ErrorEntry(code, "", "Windows Update", "A Windows Update Agent error that Microsoft does not document.",
                                          "Search WindowsUpdate.log (Get-WindowsUpdateLog) on the server for the code.", "facility");
                case 0xF:
                    return new ErrorEntry(code, "", "CBS", "A Component-Based Servicing (or setup API) error that Microsoft does not document.",
                                          @"Search CBS.log (\\<server>\admin$\Logs\CBS\CBS.log) for the code: the lines before it name the package and step.", "facility");
                case 4 when (u & 0xF000) == 0x1000:
                    return new ErrorEntry(code, "", "WMI", "A WMI error that is not in Microsoft's list.", "", "facility");
                default:
                    return new ErrorEntry(code, "", "Other", "Not in the catalog.", "", "facility");
            }
        }

        static IReadOnlyList<ErrorEntry> Load()
        {
            var asm = typeof(ErrorCatalog).Assembly;
            var list = new List<ErrorEntry>();
            foreach (var resource in new[] { "WUGrid.Errors.error-codes.wugrid.tsv", "WUGrid.Errors.error-codes.microsoft.tsv" })
                using (var stream = asm.GetManifestResourceStream(resource))
                using (var reader = new StreamReader(stream ?? throw new InvalidOperationException("Missing resource " + resource)))
                    list.AddRange(Parse(reader));
            return list;
        }

        internal static IEnumerable<ErrorEntry> Parse(TextReader reader)
        {
            string line;
            var header = true;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("#") || line.Trim() == "") continue;
                if (header) { header = false; continue; }
                var f = line.Split('\t');
                if (f.Length < 6 || !TryParse(f[0], out var code)) throw new FormatException("Bad catalog line: " + line);
                yield return new ErrorEntry(code, f[1], f[2], f[3], f[4], f[5]);
            }
        }
    }
}
