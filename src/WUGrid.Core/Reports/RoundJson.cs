using System;
using System.Linq;
using WUGrid.Core.Errors;

namespace WUGrid.Core.Reports
{
    /// <summary>
    /// The round for other tools: the state snapshot (one JSON object, rewritten on every refresh) and the events log (one
    /// JSON object per line). States and flags are the enum names, which never change; labels and notes are display text.
    /// </summary>
    public static class RoundJson
    {
        /// <param name="allClean">Every server back clean with its checks done (<see cref="RoundTracker.AllClean"/>).</param>
        /// <param name="interval">Time between polls of each server; null for the demo.</param>
        public static string State(RoundSnapshot snapshot, bool allClean, TimeSpan? interval)
        {
            var rows = snapshot.Rows;
            int Count(Func<ServerRow, bool> which) => rows.Count(which);
            var counts = new JsonObject()
                .Num("back", Count(r => r.Status.IsClean))
                .Num("flagged", Count(r => r.Status.State == ServerState.Back && !r.Status.IsClean))
                .Num("booting", Count(r => r.Status.State == ServerState.Booting))
                .Num("down", Count(r => r.Status.State == ServerState.Down))
                .Num("installing", Count(r => r.Status.State == ServerState.Installing))
                .Num("restartPending", Count(r => r.Status.State == ServerState.RestartPending))
                .Num("noRestartPending", Count(r => r.Status.State == ServerState.NoRestartPending))
                .Num("checking", Count(r => r.Status.State == ServerState.Checking));
            return new JsonObject()
                .Time("updated", snapshot.Now)
                .Time("since", snapshot.Since)
                .Num("intervalSeconds", interval.HasValue ? (long)interval.Value.TotalSeconds : (long?)null)
                .Num("servers", rows.Count)
                .Bool("allClean", allClean)
                .Raw("counts", counts.ToString())
                .Raw("expectedStops", Json.Array(snapshot.Expected.Select(e =>
                    new JsonObject().Str("server", e.Server).Str("service", e.Service).Str("comment", e.Comment).ToString())))
                .Raw("rows", Json.Array(rows.Select(Row)))
                .ToString();
        }

        static string Row(ServerRow r)
        {
            var o = r.Observation;
            var s = r.Status;
            var v = o.Verification != null && o.Verification.Boot == o.LastBoot ? o.Verification : null;
            var flags = Enum.GetValues(typeof(BackFlag)).Cast<BackFlag>().Where(f => f != BackFlag.None && s.Flag.HasFlag(f));
            return new JsonObject()
                .Str("server", r.Name)
                .Bool("ping", o.Ping)
                .Str("state", s.State.ToString())
                .Str("label", s.Label)
                .Raw("flags", Json.Strings(flags.Select(f => f.ToString())))
                .Str("note", s.Note)
                .Bool("likelyTransient", s.LikelyTransient)
                .Bool("clean", s.IsClean)
                .Bool("checksDone", r.ChecksDone)
                .Str("verificationError", o.VerificationError)
                .Time("lastBoot", o.LastBoot)
                .Num("elapsedSeconds", s.Elapsed.HasValue ? (long)s.Elapsed.Value.TotalSeconds : (long?)null)
                .Raw("restartPending", Json.Strings(StateClassifier.PendingRestart(o)))
                .Raw("busy", Json.Strings(o.Ping == true ? o.BusyProcesses : new string[0]))
                .Str("wmiError", s.State == ServerState.Booting && o.WmiHResult != 0 ? ErrorCatalog.Hex(o.WmiHResult) : null)
                .Str("buildBefore", o.Baseline?.Build)
                .Str("build", o.Build)
                .Raw("updatesFailed", Json.Array((v?.Failed ?? Enumerable.Empty<Verification.UpdateResult>())
                    .Select(u => new JsonObject().Str("update", u.Label).Str("code", ErrorCatalog.Hex(u.ErrorCode)).ToString())))
                .Raw("updatesInFlight", Json.Array(r.UpdatesInFlight
                    .Select(u => new JsonObject().Str("update", u.Label).Str("stage", u.Installing ? "installing" : "downloading")
                                                 .Time("since", u.Time).ToString())))
                .Raw("servicesNotRunning", Json.Strings(r.UnexpectedStops.Select(i => i.Service.Name)))
                .Raw("servicesExpectedStopped", Json.Strings(r.ExpectedStops.Select(x => x.Issue.Service.Name)))
                .Num("servicesExpected", o.Baseline != null ? (long?)o.Baseline.RunningAutoServices.Count : null)
                .Raw("servicesMarkedForDeletion", Json.Strings(o.Baseline?.MarkedForDeletion.Keys ?? Enumerable.Empty<string>()))
                .Raw("servicesRemoved", Json.Strings(v?.RemovedServices.Select(s => s.Name) ?? Enumerable.Empty<string>()))
                .Raw("serviceFailures", Json.Array(r.UnexpectedStops.Concat(r.ExpectedStops.Select(x => x.Issue)).Where(i => i.Failure != null)
                    .Select(i => new JsonObject().Str("service", i.Service.Name).Num("event", i.Failure.Event.Code).Str("reason", i.Failure.ShortReason)
                                                 .Num("earlierBoots", i.Failure.EarlierBoots).Num("earlierFailed", i.Failure.EarlierFailed).ToString())))
                .ToString();
        }

        /// <summary>One line of the events log.</summary>
        public static string Event(RoundEvent e)
        {
            var j = new JsonObject()
                .Time("time", e.Time)
                .Str("kind", e.Kind.ToString())
                .Str("server", e.Server)
                .Str("text", e.Text);
            if (e.Status != null) j.Str("state", e.Status.State.ToString()).Str("label", e.Status.Label).Str("note", e.Status.Note);
            if (e.Previous != null) j.Str("from", e.Previous.Label);
            return j.ToString();
        }
    }
}
