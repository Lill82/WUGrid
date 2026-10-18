using System;
using System.Collections.Generic;
using System.Linq;

namespace WUGrid.Core
{
    /// <summary>
    /// Turns an <see cref="Observation"/> into the round state. Pure: same inputs, same answer. The rules come from real
    /// runs (docs/FIELD-NOTES.md) — read those before changing anything here.
    /// </summary>
    public static class StateClassifier
    {
        public const int AccessDenied = unchecked((int)0x80070005);        // E_ACCESSDENIED
        public const int RpcServerUnavailable = unchecked((int)0x800706BA); // RPC_S_SERVER_UNAVAILABLE

        public const string TransientFootnote =
            "* Usually temporary while the server restarts: RPC answers before Windows security and WMI are ready. " +
            "If it lasts more than a few minutes on a server that is up, check the firewall (RPC/DCOM) and that your account " +
            "is an administrator there.";

        /// <summary>Errors seen for a minute or two on every boot (FIELD-NOTES), decided on the HRESULT so it works in any
        /// language.</summary>
        public static bool IsTransientWhileRestarting(int hresult) => hresult == AccessDenied || hresult == RpcServerUnavailable;

        /// <param name="since">Round cut-off: a server counts as restarted when it booted after this.</param>
        public static ServerStatus Classify(Observation o, DateTime since, DateTime now)
        {
            if (o.Ping == null || (o.Ping == true && o.Wmi == WmiResult.NotYet))
                return new ServerStatus(ServerState.Checking);
            if (o.Ping == false)
                return new ServerStatus(ServerState.Down, elapsed: Positive(now - (o.DownSince ?? now)),
                                        note: o.NameUnresolved ? "name does not resolve" : "");
            if (o.Wmi == WmiResult.Failed)
            {
                var transient = IsTransientWhileRestarting(o.WmiHResult);
                return new ServerStatus(ServerState.Booting, note: transient ? $"{o.WmiError} *" : o.WmiError, likelyTransient: transient);
            }
            if (o.LastBoot == null)
                return new ServerStatus(ServerState.Checking);

            var pending = PendingRestart(o);
            if (o.LastBoot.Value < since)
            {
                if (o.TiWorker) return new ServerStatus(ServerState.Installing);
                if (pending.Count > 0) return new ServerStatus(ServerState.RestartPending, note: string.Join("+", pending));
                return new ServerStatus(ServerState.NoRestartPending, note: o.CbsRebootPending ? "component cleanup running" : "");
            }

            var up = Positive(now - o.LastBoot.Value);

            var flags = BackFlag.None;
            var notes = new List<string>();
            if (pending.Count > 0)
            {
                flags |= BackFlag.RebootPending;
                notes.Add($"reboot pending again ({string.Join("+", pending)})");
            }

            // Verification only counts for the boot it was made on: a newer boot waits for its own
            var v = o.Verification;
            if (v != null && v.Boot == o.LastBoot.Value)
            {
                var failed = v.Failed.ToList();
                if (failed.Count > 0)
                {
                    flags |= BackFlag.UpdateFailed;
                    notes.Add(failed.Count == 1
                        ? $"update failed: {failed[0].Label} ({Errors.ErrorCatalog.Summary(failed[0].ErrorCode)})"
                        : $"{failed.Count} updates failed: {string.Join(", ", failed.Select(u => u.Label))}");
                }
                if (v.StoppedServices.Count > 0)
                {
                    flags |= BackFlag.ServiceStopped;
                    var names = v.StoppedServices.Select(i => i.Service.DisplayName).ToList();
                    notes.Add(names.Count == 1 ? $"service not running: {names[0]}" : $"{names.Count} services not running");
                }
            }

            if (flags != BackFlag.None)
                return new ServerStatus(ServerState.Back, flags, elapsed: up, note: string.Join("; ", notes));

            if (o.CbsRebootPending)
                return new ServerStatus(ServerState.Back, activity: BackActivity.ComponentCleanup, elapsed: up, note: "component cleanup running");
            if (o.TiWorker)
                return new ServerStatus(ServerState.Back, activity: BackActivity.ServicingFinishing, elapsed: up, note: "servicing finishing");
            return new ServerStatus(ServerState.Back, elapsed: up);
        }

        /// <summary>
        /// What says a restart is pending: CBS packages waiting (with the RebootPending mark) and Windows Update's
        /// RebootRequired. Not a bare CBS RebootPending — that is the mark the StartComponentCleanup idle task holds while it
        /// runs — and not PendingFileRenameOperations, which lingers after almost every boot (FIELD-NOTES).
        /// </summary>
        public static List<string> PendingRestart(Observation o)
        {
            var pending = new List<string>();
            if (o.CbsRebootPending && o.CbsPackagesPending > 0) pending.Add($"CBS {o.CbsPackagesPending} pkgs");
            if (o.WuRebootRequired) pending.Add("WU");
            return pending;
        }

        // Clock skew between workstation and target can make a fresh boot look like it is in the future
        static TimeSpan Positive(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
    }
}
