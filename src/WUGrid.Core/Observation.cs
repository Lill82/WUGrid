using System;
using System.Collections.Generic;

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

        /// <summary>Users signed in, one per session, active or disconnected: Windows refuses a restart that is not forced
        /// while there are any. Read only just before a restart, not by the poll; empty when none, or not read.</summary>
        public UserSession[] SignedIn { get; set; } = new UserSession[0];

        /// <summary>Before the restart: updates downloading or installing since the cut-off (Windows Update 44/43, no 19/20
        /// yet), read every <see cref="Probing.ServerProbe.VerifyEvery"/>. Empty once the server is back: its verification
        /// carries them then.</summary>
        public IReadOnlyList<Verification.UpdateInFlight> UpdatesInFlight { get; set; } = new Verification.UpdateInFlight[0];

        /// <summary>"17763.8880" (build.UBR) as last read, or null.</summary>
        public string Build { get; set; }

        /// <summary>What the server looked like before its restart, if it was seen then.</summary>
        public Verification.Baseline Baseline { get; set; }

        /// <summary>Latest verification; applies to the boot in <see cref="Verification.ServerVerification.Boot"/>.</summary>
        public Verification.ServerVerification Verification { get; set; }

        /// <summary>Why the last verification pass failed, or null.</summary>
        public string VerificationError { get; set; }

        /// <summary>The read of earlier boots, when asked for (details window, verify --earlier-boots); null otherwise.</summary>
        public EarlierBootsRead EarlierBoots { get; set; }
    }

    /// <summary>
    /// A read of the System log before the cut-off, for the boots there and the services' failures after them: whether a
    /// service that failed to start now failed at those boots too. WMI walks every record in the range, two minutes for
    /// 150 MB, so it runs only when asked for, in the background.
    /// </summary>
    public sealed class EarlierBootsRead
    {
        /// <summary>How far back before the cut-off it reads.</summary>
        public static readonly TimeSpan Lookback = TimeSpan.FromDays(180);

        public EarlierBootsRead(DateTime from, DateTime to, DateTime? finished = null, string error = null)
        {
            From = from;
            To = to;
            Finished = finished;
            Error = error;
        }

        /// <summary>The oldest record asked for.</summary>
        public DateTime From { get; }

        /// <summary>The cut-off it was read for: records from then on come with every verification pass.</summary>
        public DateTime To { get; }

        /// <summary>When it ended, well or not; null while reading.</summary>
        public DateTime? Finished { get; }

        /// <summary>Why it failed, or null.</summary>
        public string Error { get; }

        public bool Reading => Finished == null;

        public EarlierBootsRead Done(string error) => new EarlierBootsRead(From, To, DateTime.Now, error);
    }

    /// <summary>A session on the server with a user signed in.</summary>
    public sealed class UserSession
    {
        public UserSession(int id, string user, DateTime? since)
        {
            Id = id;
            User = user ?? "";
            Since = since;
        }

        /// <summary>Windows session ID, the one quser and logoff take.</summary>
        public int Id { get; }

        /// <summary>DOMAIN\user as the server names it, or the SID when it could not.</summary>
        public string User { get; }

        /// <summary>When the session's sign-in processes started: about when the user signed in.</summary>
        public DateTime? Since { get; }
    }
}
