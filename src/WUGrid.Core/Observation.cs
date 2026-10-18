using System;

namespace WUGrid.Core
{
    public enum WmiResult { NotYet, Ok, Failed }

    /// <summary>
    /// Raw facts from the latest poll of one server, no interpretation. The probe fills one per poll;
    /// <see cref="StateClassifier"/> turns it into a <see cref="ServerStatus"/>.
    /// </summary>
    public sealed class Observation
    {
        /// <summary>Null until the first ping has been sent.</summary>
        public bool? Ping { get; set; }

        /// <summary>When ping first failed in the current outage.</summary>
        public DateTime? DownSince { get; set; }

        /// <summary>Ping failed because the name does not resolve (a typo, or a server removed from DNS).</summary>
        public bool NameUnresolved { get; set; }

        /// <summary>NotYet = no WMI answer since the worker started or since the server came back.</summary>
        public WmiResult Wmi { get; set; }

        /// <summary>First line of the WMI error. Localized on the target: display only, never match on it.</summary>
        public string WmiError { get; set; }

        public int WmiHResult { get; set; }

        /// <summary>Local time, as converted from Win32_OperatingSystem.LastBootUpTime.</summary>
        public DateTime? LastBoot { get; set; }

        /// <summary>TiWorker.exe is running: servicing before shutdown, or finishing after boot.</summary>
        public bool TiWorker { get; set; }

        /// <summary>Update / install processes running now (TiWorker.exe, TrustedInstaller.exe, msiexec.exe…): a restart
        /// would cut them off. Empty when none, or not read.</summary>
        public string[] BusyProcesses { get; set; } = new string[0];

        /// <summary>CBS RebootPending key exists.</summary>
        public bool CbsRebootPending { get; set; }

        /// <summary>Number of entries under CBS PackagesPending.</summary>
        public int CbsPackagesPending { get; set; }

        /// <summary>WindowsUpdate\Auto Update\RebootRequired key exists.</summary>
        public bool WuRebootRequired { get; set; }

        /// <summary>"17763.8880" (build.UBR) as last read, or null.</summary>
        public string Build { get; set; }

        /// <summary>What the server looked like before its restart, if it was seen then.</summary>
        public Verification.Baseline Baseline { get; set; }

        /// <summary>Latest verification; applies to the boot in <see cref="Verification.ServerVerification.Boot"/>.</summary>
        public Verification.ServerVerification Verification { get; set; }

        /// <summary>Why the last verification pass failed, or null.</summary>
        public string VerificationError { get; set; }
    }
}
