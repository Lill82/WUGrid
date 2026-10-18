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
    /// and decided again on a fresh read just before the restart is sent. A server with no restart pending can still be
    /// restarted (an admin may want it anyway); it is only told.
    /// </summary>
    public static class RestartGuard
    {
        public static RestartEligibility Check(Observation o, ServerStatus s)
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
            if (o.CbsRebootPending && o.CbsPackagesPending == 0)
                return No("component cleanup running");

            if (IsPending(s)) return Yes("a restart is pending: this one completes it");
            if (s.State == ServerState.NoRestartPending) return Yes("no restart pending: only if you want one anyway");
            if (s.State == ServerState.Back && o.LastBoot.HasValue) return Yes($"already restarted in this round, at {o.LastBoot:HH:mm}");
            return Yes("");
        }

        /// <summary>For "tick all with a restart pending".</summary>
        public static bool IsPending(ServerStatus s) => s.State == ServerState.RestartPending || s.Flag.HasFlag(BackFlag.RebootPending);

        static RestartEligibility Yes(string note) => new RestartEligibility(true, note);
        static RestartEligibility No(string reason) => new RestartEligibility(false, reason);
    }
}
