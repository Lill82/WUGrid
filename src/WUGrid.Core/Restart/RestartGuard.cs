using System;
using System.Linq;

namespace WUGrid.Core.Restart
{
    public sealed class RestartEligibility
    {
        public RestartEligibility(bool allowed, string reason)
        {
            Allowed = allowed;
            Reason = reason ?? "";
        }

        public bool Allowed { get; }

        /// <summary>Why not, or (when allowed) anything worth knowing first.</summary>
        public string Reason { get; }
    }

    /// <summary>
    /// Whether a server can be restarted now without cutting off update work. Pure: decided on what the last poll saw,
    /// and decided again on a fresh read just before the restart is sent — only that read lists who is signed in, which
    /// blocks a restart that is not forced, unless the operator chose to force it there. A server with no restart pending
    /// can still be restarted (an admin may want it anyway); it is only told.
    /// </summary>
    public static class RestartGuard
    {
        /// <param name="forceIfSignedIn">Users signed in do not block: the restart is forced and ends their sessions. Update
        /// work still does.</param>
        public static RestartEligibility Check(Observation o, ServerStatus s, bool forceIfSignedIn = false)
        {
            switch (s.State)
            {
                case ServerState.Checking: return No("no answer yet");
                case ServerState.Down: return No(o.NameUnresolved ? "name does not resolve" : "down: no ping");
                case ServerState.Booting: return No("booting: WMI not answering yet");
                case ServerState.Installing: return No("installing updates (TiWorker running): a restart may already be under way");
            }
            if (o.BusyProcesses.Length > 0)
                return No($"update work running: {string.Join(", ", o.BusyProcesses)}");
            // The cleanup's mark, unless Windows Update asks for the restart: the mark then outlives the work (a cleanup
            // that is really running shows TiWorker, which blocks above)
            if (o.CbsRebootPending && o.CbsPackagesPending == 0 && !o.WuRebootRequired)
                return No("component cleanup running");
            if (o.SignedIn.Length > 0 && !forceIfSignedIn)
                return No($"signed in: {SignedIn(o.SignedIn)}. Windows refuses a restart that is not forced until they sign out " +
                          "(a disconnected session counts too)");

            var force = o.SignedIn.Length > 0 ? $"forced, signs out {SignedIn(o.SignedIn)}: unsaved work is lost" : "";
            var note = Note(o, s);
            return Yes(force != "" && note != "" ? force + "; " + note : force + note);
        }

        static string Note(Observation o, ServerStatus s)
        {
            if (IsPending(s)) return "a restart is pending: this one completes it";
            if (s.State == ServerState.NoRestartPending) return "no restart pending: only if you want one anyway";
            if (s.State == ServerState.Back && o.LastBoot.HasValue) return $"already restarted in this round, at {o.LastBoot:HH:mm}";
            return "";
        }

        /// <summary>For "tick all with a restart pending".</summary>
        public static bool IsPending(ServerStatus s) => s.State == ServerState.RestartPending || s.Flag.HasFlag(BackFlag.RebootPending);

        /// <summary>"CONTOSO\admin (session 2, since 16-09-2026 08:46)", the first three and how many more.</summary>
        static string SignedIn(UserSession[] sessions) =>
            string.Join(", ", sessions.Take(3).Select(u => $"{u.User} (session {u.Id}{(u.Since.HasValue ? $", since {u.Since:dd-MM-yyyy HH:mm}" : "")})")) +
            (sessions.Length > 3 ? $" and {sessions.Length - 3} more" : "");

        static RestartEligibility Yes(string note) => new RestartEligibility(true, note);
        static RestartEligibility No(string reason) => new RestartEligibility(false, reason);
    }
}
