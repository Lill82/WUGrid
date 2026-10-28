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
        /// <param name="expectedStops">Names of services the operator expects not to run on this server (<see cref="ExpectedList"/>):
        /// a stop among them is noted, not flagged.</param>
        public static ServerStatus Classify(Observation o, DateTime since, DateTime now, IReadOnlyCollection<string> expectedStops = null)
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
                // An update still downloading before the restart installs later, maybe after it (FIELD-NOTES 22-10)
                var coming = InFlight(o.UpdatesInFlight, o.TiWorker);
                if (o.TiWorker) return new ServerStatus(ServerState.Installing, note: coming ?? "");
                if (pending.Count > 0) return new ServerStatus(ServerState.RestartPending, note: Join(string.Join("+", pending), coming));
                return new ServerStatus(ServerState.NoRestartPending, note: Join(o.CbsRebootPending ? "component cleanup running" : "", coming));
            }

            var up = Positive(now - o.LastBoot.Value);

            var flags = BackFlag.None;
            var notes = new List<string>();
            string expectedNote = null;
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
                    // A stop the operator marked as expected (a role being removed) is noted, not flagged
                    var unexpected = v.StoppedServices.Where(i => !IsExpected(i.Service.Name, expectedStops)).ToList();
                    var expected = v.StoppedServices.Where(i => IsExpected(i.Service.Name, expectedStops)).ToList();
                    if (unexpected.Count > 0)
                    {
                        flags |= BackFlag.ServiceStopped;
                        notes.Add(unexpected.Count == 1 ? $"service not running: {unexpected[0].Service.DisplayName}{Why(unexpected[0].Failure)}"
                                                        : $"{unexpected.Count} services not running");
                    }
                    if (expected.Count > 0)
                        expectedNote = expected.Count == 1 ? $"expected stop: {expected[0].Service.DisplayName}" : $"{expected.Count} expected stops";
                }
            }

            // A flagged row still says TiWorker is running: updates installed after the restart stage packages, so the row
            // goes BACK · PENDING while TiWorker works (FIELD-NOTES, 18-10 evening)
            // An update downloading or installing after the restart installs later and may want another restart: not a
            // problem, but the round is not done until it has a result
            var inFlight = v != null && v.Boot == o.LastBoot.Value ? InFlight(v.UpdatesInFlight, o.TiWorker, v) : null;
            if (flags != BackFlag.None && o.TiWorker && inFlight?.StartsWith("installing ") != true) notes.Add("TiWorker running");
            if (flags != BackFlag.None && inFlight != null) notes.Add(inFlight);
            if (flags != BackFlag.None)
                return new ServerStatus(ServerState.Back, flags, elapsed: up, note: Join(string.Join("; ", notes), expectedNote));

            if (inFlight != null)
                return new ServerStatus(ServerState.Back, activity: BackActivity.UpdatesInFlight, elapsed: up, note: Join(inFlight, expectedNote));
            if (o.CbsRebootPending)
                return new ServerStatus(ServerState.Back, activity: BackActivity.ComponentCleanup, elapsed: up, note: Join("component cleanup running", expectedNote));
            if (o.TiWorker)
                return new ServerStatus(ServerState.Back, activity: BackActivity.ServicingFinishing, elapsed: up, note: Join("servicing finishing", expectedNote));
            return new ServerStatus(ServerState.Back, elapsed: up, note: expectedNote ?? "");
        }

        static bool IsExpected(string service, IReadOnlyCollection<string> expected) =>
            expected != null && expected.Contains(service, StringComparer.OrdinalIgnoreCase);

        /// <summary>" (timed out at start; also at all 4 boots before)": a service that fails at every boot is its own problem,
        /// one that fails for the first time points at the restart (FIELD-NOTES, 18-10 evening).</summary>
        static string Why(Verification.ServiceFailure f) =>
            f == null ? "" : $" ({f.ShortReason}{(f.ShortEarlier != "" ? "; " + f.ShortEarlier : "")})";

        /// <summary>"update not installed yet: KB5127648 (download started 13:05)", or null when there is none. Install started
        /// and TiWorker running: "installing KB5127648 (started 13:05)". A retry of an update that failed says so:
        /// "(…, again after 0x80070070)" (FIELD-NOTES 28-10).</summary>
        static string InFlight(IReadOnlyList<Verification.UpdateInFlight> updates, bool tiWorker, Verification.ServerVerification v = null)
        {
            if (updates.Count == 0) return null;
            if (updates.Count > 1) return $"{updates.Count} updates not installed yet: {string.Join(", ", updates.Select(u => u.Label))}";
            var u = updates[0];
            var before = v?.FailureBefore(u);
            var again = before != null ? $", again after {Errors.ErrorCatalog.Hex(before.ErrorCode)}" : "";
            // TiWorker alone can be servicing finishing or cleanup; with Windows Update's 43 it is this install
            if (u.Installing && tiWorker) return $"installing {u.Label} (started {u.Time:HH:mm}{again})";
            return $"update not installed yet: {u.Label} ({(u.Installing ? "install" : "download")} started {u.Time:HH:mm}{again})";
        }

        static string Join(string note, string extra) => extra == null ? note : note == "" ? extra : note + "; " + extra;

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
