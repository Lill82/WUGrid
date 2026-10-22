using System;
using System.Collections.Generic;
using System.Linq;
using WUGrid.Core.Verification;

namespace WUGrid.Core
{
    /// <summary>A service not running that the operator marked as expected, with the entry that says so.</summary>
    public sealed class ExpectedServiceStop
    {
        public ExpectedServiceStop(ServiceIssue issue, ExpectedStop entry)
        {
            Issue = issue;
            Entry = entry;
        }

        public ServiceIssue Issue { get; }
        public ExpectedStop Entry { get; }
    }

    public sealed class ServerRow
    {
        /// <param name="expected">The round's expected stops; the ones that apply to this server's stopped services are split off.</param>
        public ServerRow(string name, Observation observation, ServerStatus status, IReadOnlyList<ExpectedStop> expected = null)
        {
            Name = name;
            Observation = observation;
            Status = status;
            var stops = CurrentVerification?.StoppedServices ?? new List<ServiceIssue>();
            var entries = expected ?? new List<ExpectedStop>();
            var split = stops.Select(i => new ExpectedServiceStop(i, ExpectedList.Find(entries, name, i.Service.Name))).ToList();
            ExpectedStops = split.Where(x => x.Entry != null).ToList();
            UnexpectedStops = split.Where(x => x.Entry == null).Select(x => x.Issue).ToList();
        }

        public string Name { get; }
        public Observation Observation { get; }
        public ServerStatus Status { get; }

        /// <summary>The verification when it belongs to the server's current boot; null otherwise.</summary>
        public ServerVerification CurrentVerification
        {
            get
            {
                var v = Observation.Verification;
                return v != null && v.Boot == Observation.LastBoot ? v : null;
            }
        }

        /// <summary>Services not running that flag the server.</summary>
        public IReadOnlyList<ServiceIssue> UnexpectedStops { get; }

        /// <summary>Services not running that the operator marked as expected: shown, not flagged.</summary>
        public IReadOnlyList<ExpectedServiceStop> ExpectedStops { get; }

        /// <summary>The checks for the current boot are in: update results read, and services checked once it has been up
        /// <see cref="Verification.VerificationBuilder.ServicesAfter"/>. Until then a clean BACK can still hide a failure.
        /// Not while an update is downloading or installing either: it installs later and may want another restart.</summary>
        public bool ChecksDone
        {
            get
            {
                var v = Observation.Verification;
                return v != null && v.Boot == Observation.LastBoot && v.ServicesChecked && v.UpdatesInFlight.Count == 0;
            }
        }
    }

    /// <summary>The whole round at one instant, every server classified, sorted by name.</summary>
    public sealed class RoundSnapshot
    {
        /// <param name="expected">Services the operator expects not to run after the restart (<see cref="ExpectedList"/>).</param>
        public RoundSnapshot(DateTime now, DateTime since, IEnumerable<KeyValuePair<string, Observation>> observations,
                             IReadOnlyList<ExpectedStop> expected = null)
        {
            Now = now;
            Since = since;
            Expected = expected ?? new List<ExpectedStop>();
            Rows = observations
                .Select(kv => new ServerRow(kv.Key, kv.Value, StateClassifier.Classify(kv.Value, since, now, ExpectedList.ServicesFor(Expected, kv.Key)), Expected))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public DateTime Now { get; }
        public DateTime Since { get; }
        public IReadOnlyList<ServerRow> Rows { get; }

        /// <summary>The expected stops the rows were classified with.</summary>
        public IReadOnlyList<ExpectedStop> Expected { get; }
    }

    /// <summary>Where the grid gets its rounds from: live probing, or the demo.</summary>
    public interface IRoundSource : IDisposable
    {
        /// <summary>The round as it stands now. Called on every refresh.</summary>
        RoundSnapshot Snapshot();

        /// <summary>Services the operator expects not to run after the restart: noted, not flagged. Applies from the next snapshot.</summary>
        IReadOnlyList<ExpectedStop> Expected { get; set; }
    }
}
