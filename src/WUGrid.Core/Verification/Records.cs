using System;
using System.Collections.Generic;

namespace WUGrid.Core.Verification
{
    /// <summary>One event log record as read over WMI (Win32_NTLogEvent): the data, not the localized message.</summary>
    public sealed class EventRecord
    {
        public EventRecord(string source, int code, DateTime time, long recordNumber, string user, params string[] strings)
        {
            Source = source ?? "";
            Code = code;
            Time = time;
            RecordNumber = recordNumber;
            User = user ?? "";
            Strings = strings ?? new string[0];
        }

        public string Source { get; }
        public int Code { get; }

        /// <summary>Local time.</summary>
        public DateTime Time { get; }

        public long RecordNumber { get; }
        public string User { get; }

        /// <summary>Insertion strings: the event's data fields, in the order the event defines.</summary>
        public IReadOnlyList<string> Strings { get; }

        public string At(int i) => i < Strings.Count ? Strings[i] ?? "" : "";
    }

    /// <summary>A service as Win32_Service reports it. State and StartMode values are not localized (Running, Auto…).</summary>
    public sealed class ServiceRecord
    {
        public ServiceRecord(string name, string displayName, string state, string startMode)
        {
            Name = name;
            DisplayName = displayName ?? name;
            State = state ?? "";
            StartMode = startMode ?? "";
        }

        public string Name { get; }
        public string DisplayName { get; }
        public string State { get; }
        public string StartMode { get; }
    }

    /// <summary>Taken once, the first time a server is seen not yet restarted: what "before" looked like.</summary>
    public sealed class Baseline
    {
        public Baseline(DateTime takenAt, string build, IReadOnlyDictionary<string, string> runningAutoServices,
                        IReadOnlyDictionary<string, string> markedForDeletion = null)
        {
            TakenAt = takenAt;
            Build = build;
            RunningAutoServices = runningAutoServices;
            MarkedForDeletion = markedForDeletion ?? new Dictionary<string, string>();
        }

        public DateTime TakenAt { get; }

        /// <summary>"17763.8644" (build.UBR), or null when unknown.</summary>
        public string Build { get; }

        /// <summary>Automatic services that were running and are expected to run again after the restart: name → display name.</summary>
        public IReadOnlyDictionary<string, string> RunningAutoServices { get; }

        /// <summary>Automatic services that were running but marked for deletion (DeleteFlag in their registry key: a role or
        /// application removal deleted them while something still held them open). The Service Control Manager removes them
        /// at the next boot, so they are not expected back and are not in <see cref="RunningAutoServices"/>.</summary>
        public IReadOnlyDictionary<string, string> MarkedForDeletion { get; }
    }

    public sealed class UpdateResult
    {
        public UpdateResult(DateTime time, string title, string kb, string updateId, bool succeeded, int errorCode)
        {
            Time = time;
            Title = title ?? "";
            Kb = kb ?? "";
            UpdateId = updateId ?? "";
            Succeeded = succeeded;
            ErrorCode = errorCode;
        }

        public DateTime Time { get; }

        /// <summary>As Windows Update named it, in the server's language.</summary>
        public string Title { get; }

        /// <summary>"KB5122876", or "" when the title has none.</summary>
        public string Kb { get; }

        public string UpdateId { get; }
        public bool Succeeded { get; }
        public int ErrorCode { get; }

        public string Label => Kb != "" ? Kb : Title;
    }

    /// <summary>An update Windows Update started downloading (event 44) or installing (event 43) since the cut-off and has
    /// logged no result for yet (19/20). It installs later and may ask for another restart, so the round is not done.</summary>
    public sealed class UpdateInFlight
    {
        public UpdateInFlight(DateTime time, string title, string kb, string updateId, bool installing)
        {
            Time = time;
            Title = title ?? "";
            Kb = kb ?? "";
            UpdateId = updateId ?? "";
            Installing = installing;
        }

        /// <summary>When its latest event (43 or 44) was logged.</summary>
        public DateTime Time { get; }

        /// <summary>As Windows Update named it, in the server's language; "" when only an older 44 (no title) was seen.</summary>
        public string Title { get; }

        public string Kb { get; }
        public string UpdateId { get; }

        /// <summary>Install started (43); otherwise download started (44), which can also mean downloaded and waiting for its
        /// install time: the System log has no "download finished" event.</summary>
        public bool Installing { get; }

        public string Label => Kb != "" ? Kb : Title != "" ? Title : UpdateId;
    }

    /// <summary>A shutdown or restart someone or something asked for (System event 1074).</summary>
    public sealed class RestartRecord
    {
        public RestartRecord(DateTime time, string process, string user, string reasonCode, string reason, string comment)
        {
            Time = time;
            Process = process ?? "";
            User = user ?? "";
            ReasonCode = reasonCode ?? "";
            Reason = reason ?? "";
            Comment = comment ?? "";
        }

        public DateTime Time { get; }

        /// <summary>"C:\Windows\servicing\TrustedInstaller.exe (SRV01)", or "wininit.exe (10.0.0.5)" for a remote restart.</summary>
        public string Process { get; }

        public string User { get; }
        public string ReasonCode { get; }

        /// <summary>Localized on the server: display only.</summary>
        public string Reason { get; }

        public string Comment { get; }
    }

    /// <summary>A boot (Kernel-General event 12) with the build it came up on.</summary>
    public sealed class BootRecord
    {
        public BootRecord(DateTime time, string build)
        {
            Time = time;
            Build = build;
        }

        public DateTime Time { get; }

        /// <summary>"17763.8880", or null when the event does not carry it.</summary>
        public string Build { get; }
    }

    /// <summary>A service that ran before the restart (or should be running) and does not now.</summary>
    public sealed class ServiceIssue
    {
        public ServiceIssue(ServiceRecord service, EventRecord lastStartModeChange, ServiceFailure failure = null)
        {
            Service = service;
            LastStartModeChange = lastStartModeChange;
            Failure = failure;
        }

        public ServiceRecord Service { get; }

        /// <summary>The latest System event 7040 for this service since the cut-off, if any: who changed its start type.</summary>
        public EventRecord LastStartModeChange { get; }

        /// <summary>Why it is not running, when the Service Control Manager logged a failure for it at this boot.</summary>
        public ServiceFailure Failure { get; }
    }

    /// <summary>What a server's restart round came to, for its current boot.</summary>
    public sealed class ServerVerification
    {
        public ServerVerification(DateTime checkedAt, DateTime boot, string build, IReadOnlyList<UpdateResult> updates,
                                  IReadOnlyList<RestartRecord> restarts, IReadOnlyList<BootRecord> boots, bool unexpectedShutdown,
                                  IReadOnlyList<ServiceIssue> stoppedServices, bool servicesChecked, bool comparedToBaseline, bool settled,
                                  IReadOnlyList<ServiceRecord> removedServices = null, IReadOnlyList<UpdateInFlight> updatesInFlight = null)
        {
            CheckedAt = checkedAt;
            Boot = boot;
            Build = build;
            Updates = updates;
            Restarts = restarts;
            Boots = boots;
            UnexpectedShutdown = unexpectedShutdown;
            StoppedServices = stoppedServices;
            ServicesChecked = servicesChecked;
            ComparedToBaseline = comparedToBaseline;
            Settled = settled;
            RemovedServices = removedServices ?? new List<ServiceRecord>();
            UpdatesInFlight = updatesInFlight ?? new List<UpdateInFlight>();
        }

        public DateTime CheckedAt { get; }

        /// <summary>The boot this applies to (the server's LastBootUpTime when checked).</summary>
        public DateTime Boot { get; }

        public string Build { get; }

        /// <summary>Last result per update since the cut-off.</summary>
        public IReadOnlyList<UpdateResult> Updates { get; }

        /// <summary>Updates started (download or install) since the cut-off with no result yet: the boot does not settle and the
        /// round is not done while there are any.</summary>
        public IReadOnlyList<UpdateInFlight> UpdatesInFlight { get; }

        public IReadOnlyList<RestartRecord> Restarts { get; }
        public IReadOnlyList<BootRecord> Boots { get; }

        /// <summary>System event 6008 since the cut-off: a shutdown that was not clean.</summary>
        public bool UnexpectedShutdown { get; }

        public IReadOnlyList<ServiceIssue> StoppedServices { get; }

        /// <summary>Services the baseline found marked for deletion that did not come back: gone, or still registered but not
        /// running until their last handle closes. Removed as planned, so not in <see cref="StoppedServices"/>; the before
        /// and after counts reconcile through this list.</summary>
        public IReadOnlyList<ServiceRecord> RemovedServices { get; }

        /// <summary>False until the server has been up long enough for delayed-start services.</summary>
        public bool ServicesChecked { get; }

        /// <summary>Services were compared with a baseline taken before the restart (otherwise: Automatic but not running).</summary>
        public bool ComparedToBaseline { get; }

        /// <summary>Up long enough, servicing finished and no update in flight: no more checks needed for this boot.</summary>
        public bool Settled { get; }

        /// <summary>Failed and not started again since. Windows Update retries a failed update after the restart: while the
        /// retry runs it is in <see cref="UpdatesInFlight"/>, not here (FIELD-NOTES 28-10).</summary>
        public IEnumerable<UpdateResult> Failed
        {
            get { foreach (var u in Updates) if (!u.Succeeded && !Retrying(u)) yield return u; }
        }

        /// <summary>A failed update Windows Update has started downloading or installing again since.</summary>
        public bool Retrying(UpdateResult u) => !u.Succeeded && RetryOf(u) != null;

        /// <summary>The update in flight that is a retry of <paramref name="u"/>, or null.</summary>
        public UpdateInFlight RetryOf(UpdateResult u)
        {
            foreach (var i in UpdatesInFlight)
                if (VerificationBuilder.SameUpdate(i.UpdateId, i.Title, u.UpdateId, u.Title)) return i;
            return null;
        }

        /// <summary>The failure an update in flight is a retry of, or null.</summary>
        public UpdateResult FailureBefore(UpdateInFlight i)
        {
            foreach (var u in Updates)
                if (!u.Succeeded && VerificationBuilder.SameUpdate(i.UpdateId, i.Title, u.UpdateId, u.Title)) return u;
            return null;
        }
    }
}
