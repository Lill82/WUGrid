using System;
using System.Globalization;
using System.Linq;

namespace WUGrid.Core.Verification
{
    /// <summary>What a Service Control Manager failure says about the service, for the advice.</summary>
    public enum FailureKind { Timeout, Dependency, Logon, Ended, Error }

    /// <summary>
    /// Why a service is not running, from the Service Control Manager's own event at this boot (7009 timed out at start,
    /// 7024 stopped with its own error…), and how it fared after the boots before that the events read cover. Decided on
    /// the event ID and its data fields; the event's text is localized and never read.
    /// </summary>
    public sealed class ServiceFailure
    {
        public ServiceFailure(EventRecord @event, int earlierBoots = 0, int earlierFailed = 0, DateTime? earliestBoot = null)
        {
            Event = @event;
            EarlierBoots = earlierBoots;
            EarlierFailed = earlierFailed;
            EarliestBoot = earliestBoot;
        }

        /// <summary>The event that says why, logged since this boot.</summary>
        public EventRecord Event { get; }

        /// <summary>Boots before this one that the events read cover: the round's, and older ones once read on request.</summary>
        public int EarlierBoots { get; }

        /// <summary>Of those, how many the service failed after (within <see cref="VerificationBuilder.StartWindow"/>).</summary>
        public int EarlierFailed { get; }

        /// <summary>The oldest of those boots.</summary>
        public DateTime? EarliestBoot { get; }

        /// <summary>Failed after every earlier boot read, and at least one was: the service's own problem, not this restart's.</summary>
        public bool EveryBoot => EarlierBoots > 0 && EarlierFailed == EarlierBoots;

        /// <summary>Where each event carries the service: 7009 and 7011 start with the timeout. Some events give the display
        /// name instead of the service name.</summary>
        public static string ServiceIn(EventRecord e) => e.Code == 7009 || e.Code == 7011 ? e.At(1) : e.At(0);

        public FailureKind Kind
        {
            get
            {
                switch (Event.Code)
                {
                    case 7009: case 7011: case 7022: return FailureKind.Timeout;
                    case 7001: return FailureKind.Dependency;
                    case 7038: return FailureKind.Logon;
                    case 7031: case 7034: return FailureKind.Ended;
                    case 7000:
                        switch (Code(Event.At(1)))
                        {
                            case 1053: return FailureKind.Timeout;
                            case 1068: case 1075: return FailureKind.Dependency;
                            case 1069: return FailureKind.Logon;
                            case 1067: return FailureKind.Ended;
                            default: return FailureKind.Error;
                        }
                    case 7023: return Code(Event.At(1)) == 1067 ? FailureKind.Ended : FailureKind.Error;
                    default: return FailureKind.Error;
                }
            }
        }

        /// <summary>"timed out at start: no answer within 30 s (event 7009)".</summary>
        public string Reason
        {
            get
            {
                var e = Event;
                switch (e.Code)
                {
                    case 7009: return $"timed out at start: no answer within {Seconds(e.At(0))} (event 7009)";
                    case 7011: return $"timed out answering the Service Control Manager within {Seconds(e.At(0))} (event 7011)";
                    case 7022: return "hung while starting (event 7022)";
                    case 7000: return (Code(e.At(1)) == 1053 ? "timed out at start" : "failed to start: " + Error(e.At(1))) + " (event 7000)";
                    case 7001: return $"a service it depends on did not start: {e.At(1)} (event 7001)";
                    case 7023: return $"stopped with an error: {Error(e.At(1))} (event 7023)";
                    case 7024: return $"stopped with its own error {e.At(1).TrimStart('%')} (event 7024)";
                    case 7031: case 7034: return $"terminated unexpectedly (event {e.Code})";
                    case 7038: return $"could not log on as {e.At(1)}: {Error(e.At(2))} (event 7038)";
                    default: return $"event {e.Code}";
                }
            }
        }

        /// <summary>"timed out at start": the reason for the grid.</summary>
        public string ShortReason
        {
            get
            {
                switch (Kind)
                {
                    case FailureKind.Timeout: return Event.Code == 7011 ? "timed out" : Event.Code == 7022 ? "hung at start" : "timed out at start";
                    case FailureKind.Dependency: return "a service it needs did not start";
                    case FailureKind.Logon: return "could not log on";
                    case FailureKind.Ended: return "terminated unexpectedly";
                    default: return Event.Code == 7000 ? "failed to start" : "stopped with an error";
                }
            }
        }

        /// <summary>"also at all 4 boots before since 08-05-2026"; "" when no earlier boot was read.</summary>
        public string Earlier
        {
            get
            {
                if (EarlierBoots == 0) return "";
                var since = !EarliestBoot.HasValue ? "" : EarlierBoots == 1 ? $" ({EarliestBoot:dd-MM HH:mm})" : $" since {EarliestBoot:dd-MM-yyyy}";
                if (EveryBoot) return EarlierBoots == 1 ? "also at the boot before" + since : $"also at all {EarlierBoots} boots before{since}";
                if (EarlierFailed > 0) return $"also at {EarlierFailed} of the {EarlierBoots} boots before{since}";
                return EarlierBoots == 1 ? "not at the boot before" + since : $"not at the {EarlierBoots} boots before{since}";
            }
        }

        /// <summary>"also at all 4 boots before", "first time in 3 boots": the history for the grid.</summary>
        public string ShortEarlier =>
            EarlierBoots == 0 ? ""
            : EveryBoot ? (EarlierBoots == 1 ? "also at the boot before" : $"also at all {EarlierBoots} boots before")
            : EarlierFailed > 0 ? $"also at {EarlierFailed} of {EarlierBoots} boots before"
            : $"first time in {EarlierBoots + 1} boots";

        /// <summary>What it means and what to do; "" when there is nothing general to say.</summary>
        public string Advice
        {
            get
            {
                var history = EveryBoot ? "It failed after every boot before too: not caused by this restart."
                            : EarlierBoots > 0 && EarlierFailed == 0 ? "It did not fail after the boots before: look at what this restart changed."
                            : "";
                string what;
                switch (Kind)
                {
                    case FailureKind.Timeout: what = "Too slow to start while the server boots: start it by hand; set to Automatic (Delayed Start) it usually starts on its own."; break;
                    case FailureKind.Dependency: what = "Look at the service it depends on first."; break;
                    case FailureKind.Logon: what = "Its account could not log on: check the account and its password."; break;
                    case FailureKind.Ended: what = "The program ended by itself: its own log says why."; break;
                    default: what = ""; break;
                }
                return string.Join(" ", new[] { history, what }.Where(t => t != ""));
            }
        }

        /// <summary>A "%%1053" data field: the Win32 error, or the service's own code for 7024. Null when the field is not in
        /// that form (some systems log the text).</summary>
        static int? Code(string field) =>
            field.StartsWith("%%") && int.TryParse(field.Substring(2), NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : (int?)null;

        static string Seconds(string ms) =>
            int.TryParse(ms, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? $"{v / 1000.0:0.#} s" : "the time allowed";

        /// <summary>The Win32 errors services fail with, in a few words; others by number, to look up in Error codes….</summary>
        static string Error(string field)
        {
            var code = Code(field);
            if (code == null) return field;
            switch (code.Value)
            {
                case 2: return "file not found";
                case 3: return "path not found";
                case 5: return "access denied";
                case 193: return "not a valid program";
                case 577: return "its signature could not be verified";
                case 1053: return "no answer in time";
                case 1058: return "the service is disabled";
                case 1067: return "the process ended unexpectedly";
                case 1068: return "a service it depends on did not start";
                case 1069: return "logon failure";
                case 1075: return "a service it depends on does not exist";
                case 1326: return "wrong user name or password";
                case 1330: return "password expired";
                case 1331: return "account disabled";
                case 1385: return "logon right not granted";
                case 1909: return "account locked out";
                default: return $"error {code} ({Errors.ErrorCatalog.Hex(unchecked((int)(0x80070000u | (uint)code.Value)))})";
            }
        }
    }
}
