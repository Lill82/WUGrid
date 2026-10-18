using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using WUGrid.Core.Verification;

namespace WUGrid.Core.Probing
{
    /// <summary>
    /// Reads what a verification needs over DCOM, read-only: System log records (Win32_NTLogEvent, filtered by event ID
    /// and time, or by record number to fetch only what is new), services and the build. The Setup log is not a classic
    /// log, so WMI cannot read it; Windows Update's own events 19/20 in the System log carry the install results.
    /// </summary>
    static class VerificationReader
    {
        const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        const string Services = @"SYSTEM\CurrentControlSet\Services";

        /// <summary>A time-filtered query walks the log back to the cut-off: seconds on a busy 200,000-record log.</summary>
        public static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(60);

        /// <summary>"17763.8880", or the bare build where UBR does not exist (older systems).</summary>
        public static string ReadBuild(ManagementClass registry, TimeSpan timeout)
        {
            var build = Wmi.Value(registry, CurrentVersion, "CurrentBuildNumber", dword: false, timeout) as string;
            var ubr = Wmi.Value(registry, CurrentVersion, "UBR", dword: true, timeout);
            return build == null ? null : ubr == null ? build : $"{build}.{Convert.ToUInt32(ubr)}";
        }

        public static List<ServiceRecord> ReadServices(ManagementScope scope, TimeSpan timeout) =>
            Wmi.Query(scope, "SELECT Name, DisplayName, State, StartMode FROM Win32_Service", timeout)
               .Select(mo => new ServiceRecord((string)mo["Name"], (string)mo["DisplayName"], (string)mo["State"], (string)mo["StartMode"]))
               .ToList();

        /// <summary>Which of these services carry DeleteFlag in their registry key: a role or application removal deleted them
        /// while something still held them open, so the Service Control Manager drops them at the next boot. One registry read
        /// per service: call it for the baseline's candidates only, once.</summary>
        public static HashSet<string> ReadMarkedForDeletion(ManagementClass registry, IEnumerable<ServiceRecord> candidates, TimeSpan timeout)
        {
            var marked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in candidates)
            {
                var flag = Wmi.Value(registry, $@"{Services}\{s.Name}", "DeleteFlag", dword: true, timeout);
                if (flag != null && Convert.ToUInt32(flag) != 0) marked.Add(s.Name);
            }
            return marked;
        }

        /// <summary>WMI walks every record back to the start of the range: 124 s for 180 days of a 150 MB log (FIELD-NOTES).</summary>
        public static readonly TimeSpan EarlierBootsTimeout = TimeSpan.FromMinutes(5);

        /// <param name="afterRecord">0 for everything since <paramref name="since"/>; otherwise only records after this one.</param>
        public static List<EventRecord> ReadEvents(ManagementScope scope, DateTime since, long afterRecord) =>
            Records(scope, VerificationBuilder.EventCodes, $"TimeGenerated >= '{ManagementDateTimeConverter.ToDmtfDateTime(since)}'" +
                                                           (afterRecord > 0 ? $" AND RecordNumber > {afterRecord}" : ""), EventTimeout);

        /// <summary>Boots and service failures from <paramref name="from"/> up to the cut-off <paramref name="to"/>: slow on a
        /// large log, so only on request.</summary>
        public static List<EventRecord> ReadEarlierBoots(ManagementScope scope, DateTime from, DateTime to) =>
            Records(scope, VerificationBuilder.EarlierBootCodes, $"TimeGenerated >= '{ManagementDateTimeConverter.ToDmtfDateTime(from)}' " +
                                                                 $"AND TimeGenerated < '{ManagementDateTimeConverter.ToDmtfDateTime(to)}'", EarlierBootsTimeout);

        static List<EventRecord> Records(ManagementScope scope, IEnumerable<int> eventCodes, string where, TimeSpan timeout)
        {
            var codes = string.Join(" OR ", eventCodes.Select(c => "EventCode=" + c));
            var wql = "SELECT EventCode, SourceName, TimeGenerated, InsertionStrings, User, RecordNumber FROM Win32_NTLogEvent " +
                      $"WHERE Logfile='System' AND {where} AND ({codes})";
            return Wmi.Query(scope, wql, timeout)
                      .Select(mo => new EventRecord(
                          (string)mo["SourceName"],
                          Convert.ToInt32(mo["EventCode"]),
                          ManagementDateTimeConverter.ToDateTime((string)mo["TimeGenerated"]),
                          Convert.ToInt64(mo["RecordNumber"]),
                          (string)mo["User"],
                          (mo["InsertionStrings"] as string[]) ?? new string[0]))
                      .ToList();
        }
    }
}
