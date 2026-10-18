using System;
using System.Collections.Generic;
using System.Linq;

namespace WUGrid.Core
{
    public sealed class ServerRow
    {
        public ServerRow(string name, Observation observation, ServerStatus status)
        {
            Name = name;
            Observation = observation;
            Status = status;
        }

        public string Name { get; }
        public Observation Observation { get; }
        public ServerStatus Status { get; }

        /// <summary>The checks for the current boot are in: update results read, and services checked once it has been up
        /// <see cref="Verification.VerificationBuilder.ServicesAfter"/>. Until then a clean BACK can still hide a failure.</summary>
        public bool ChecksDone
        {
            get
            {
                var v = Observation.Verification;
                return v != null && v.Boot == Observation.LastBoot && v.ServicesChecked;
            }
        }
    }

    /// <summary>The whole round at one instant, every server classified, sorted by name.</summary>
    public sealed class RoundSnapshot
    {
        public RoundSnapshot(DateTime now, DateTime since, IEnumerable<KeyValuePair<string, Observation>> observations)
        {
            Now = now;
            Since = since;
            Rows = observations
                .Select(kv => new ServerRow(kv.Key, kv.Value, StateClassifier.Classify(kv.Value, since, now)))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public DateTime Now { get; }
        public DateTime Since { get; }
        public IReadOnlyList<ServerRow> Rows { get; }
    }

    /// <summary>Where the grid gets its rounds from: live probing, or the demo.</summary>
    public interface IRoundSource : IDisposable
    {
        /// <summary>The round as it stands now. Called on every refresh.</summary>
        RoundSnapshot Snapshot();
    }
}
