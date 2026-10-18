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

        /// <param name="afterRecord">0 for everything since <paramref name="since"/>; otherwise only records after this one.</param>
        public static List<EventRecord> ReadEvents(ManagementScope scope, DateTime since, long afterRecord)
        {
            var codes = string.Join(" OR ", VerificationBuilder.EventCodes.Select(c => "EventCode=" + c));
            var wql = "SELECT EventCode, SourceName, TimeGenerated, InsertionStrings, User, RecordNumber FROM Win32_NTLogEvent " +
                      $"WHERE Logfile='System' AND TimeGenerated >= '{ManagementDateTimeConverter.ToDmtfDateTime(since)}' AND ({codes})" +
                      (afterRecord > 0 ? $" AND RecordNumber > {afterRecord}" : "");
            return Wmi.Query(scope, wql, EventTimeout)
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
