using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace WUGrid.Core.Probing
{
    /// <summary>A process in an interactive session (SessionId above 0), as Win32_Process lists it.</summary>
    sealed class SessionProcess
    {
        public SessionProcess(string handle, string name, int sessionId, DateTime? created)
        {
            Handle = handle;
            Name = name ?? "";
            SessionId = sessionId;
            Created = created;
        }

        public string Handle { get; }
        public string Name { get; }
        public int SessionId { get; }
        public DateTime? Created { get; }
    }

    /// <summary>
    /// Who is signed in on a server, read-only: Windows refuses a restart that is not forced while any user has a session
    /// there, active or disconnected (ERROR_SHUTDOWN_USERS_LOGGED_ON, 1191). One Win32_Process query lists the processes
    /// in sessions above 0; a session counts when one of them belongs to a user account, decided on the owner's SID so it
    /// holds in any language. The logon screen's own processes are never asked about, so a session nobody signed in to
    /// costs no calls and a signed-in one usually two. Too costly for every poll: the restart reads it just before acting.
    /// </summary>
    static class SessionReader
    {
        /// <summary>What a session runs before anyone signs in: never a user's.</summary>
        static readonly string[] LogonScreen = { "csrss.exe", "winlogon.exe", "LogonUI.exe", "dwm.exe", "fontdrvhost.exe" };

        /// <summary>Started as the user at sign-in (taskhostex / taskhost on 2012 R2 / 2008 R2): asked first, and the
        /// oldest of them dates the sign-in. Explorer comes last, as it can restart long after.</summary>
        static readonly string[] SignInProcesses = { "sihost.exe", "taskhostw.exe", "rdpclip.exe", "taskhostex.exe", "taskhost.exe", "explorer.exe" };

        /// <summary>Owner lookups per session before deciding nobody is signed in there (helpers running as SYSTEM).</summary>
        internal const int MaxAsks = 5;

        public static UserSession[] Read(ManagementScope scope, TimeSpan timeout)
        {
            var processes = Wmi.Query(scope, "SELECT Handle, Name, SessionId, CreationDate FROM Win32_Process WHERE SessionId > 0", timeout)
                               .Select(mo => new SessionProcess((string)mo["Handle"], (string)mo["Name"], Convert.ToInt32(mo["SessionId"]),
                                                                mo["CreationDate"] is string created ? ManagementDateTimeConverter.ToDateTime(created) : (DateTime?)null))
                               .ToList();
            return Pick(processes,
                        p => Ask(scope, p.Handle, "GetOwnerSid", r => (string)r["Sid"], timeout),
                        p => Ask(scope, p.Handle, "GetOwner", r => string.IsNullOrEmpty((string)r["Domain"]) ? (string)r["User"] : $@"{r["Domain"]}\{r["User"]}", timeout));
        }

        /// <summary>The sessions with a user signed in, by session ID. <paramref name="ownerSid"/> and
        /// <paramref name="ownerName"/> ask the server about one process; null when it has exited since the list.</summary>
        internal static UserSession[] Pick(IEnumerable<SessionProcess> processes, Func<SessionProcess, string> ownerSid, Func<SessionProcess, string> ownerName)
        {
            var found = new List<UserSession>();
            foreach (var session in processes.Where(p => p.SessionId > 0).GroupBy(p => p.SessionId).OrderBy(g => g.Key))
            {
                var candidates = session.Where(p => !LogonScreen.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                                        .OrderBy(p => Rank(p.Name)).ThenBy(p => p.Created ?? DateTime.MaxValue)
                                        .Take(MaxAsks);
                foreach (var p in candidates)
                {
                    var sid = ownerSid(p);
                    if (!IsUserSid(sid)) continue;
                    var signIn = session.Where(q => Rank(q.Name) < SignInProcesses.Length).Select(q => q.Created).Min();
                    found.Add(new UserSession(session.Key, ownerName(p) ?? sid, signIn ?? p.Created));
                    break;
                }
            }
            return found.ToArray();
        }

        /// <summary>A domain or local account (S-1-5-21-…) or an Entra ID one (S-1-12-1-…). Not SYSTEM, LOCAL SERVICE,
        /// NETWORK SERVICE, the window manager (S-1-5-90-…) or the font driver host (S-1-5-96-…).</summary>
        internal static bool IsUserSid(string sid) =>
            sid != null && (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) || sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase));

        static int Rank(string name)
        {
            var i = Array.FindIndex(SignInProcesses, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? SignInProcesses.Length : i;
        }

        /// <summary>Calls a Win32_Process method that takes no parameters; null when it fails or the process has exited.</summary>
        static string Ask(ManagementScope scope, string handle, string method, Func<ManagementBaseObject, string> read, TimeSpan timeout)
        {
            try
            {
                using (var process = new ManagementObject(scope, new ManagementPath($"Win32_Process.Handle=\"{handle}\""), null))
                using (var result = process.InvokeMethod(method, null, new InvokeMethodOptions(null, timeout)))
                    return result != null && Convert.ToUInt32(result["ReturnValue"]) == 0 ? read(result) : null;
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotFound)
            {
                return null;
            }
        }
    }
}
