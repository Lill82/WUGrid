using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WUGrid.Core.Probing;

namespace WUGrid.Core
{
    /// <summary>
    /// Watches real servers, read-only: one long-running worker thread per server, each polling every
    /// <see cref="Interval"/> with its own <see cref="ServerProbe"/>. Servers can be added and removed while it runs.
    /// </summary>
    public sealed class LiveRound : IRoundSource
    {
        readonly object gate = new object();
        readonly Dictionary<string, Watcher> watchers = new Dictionary<string, Watcher>(StringComparer.OrdinalIgnoreCase);
        long intervalTicks;
        volatile IReadOnlyList<ExpectedStop> expected = new List<ExpectedStop>();

        public LiveRound(DateTime since, TimeSpan interval)
        {
            Since = since;
            Interval = interval;
        }

        /// <summary>Round cut-off: a server counts as restarted when it booted after this.</summary>
        public DateTime Since { get; set; }

        /// <summary>Time between polls of each server. Changing it applies to the running workers from their next wait.</summary>
        public TimeSpan Interval
        {
            get => TimeSpan.FromTicks(Interlocked.Read(ref intervalTicks));
            set => Interlocked.Exchange(ref intervalTicks, value.Ticks);
        }

        public IReadOnlyList<string> Servers
        {
            get { lock (gate) return watchers.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(); }
        }

        public IReadOnlyList<ExpectedStop> Expected
        {
            get => expected;
            set => expected = value ?? new List<ExpectedStop>();
        }

        /// <summary>Starts workers for new names and stops the ones no longer listed.</summary>
        public void SetServers(IEnumerable<string> names)
        {
            var want = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            lock (gate)
            {
                foreach (var gone in watchers.Keys.Where(k => !want.Contains(k)).ToList())
                {
                    watchers[gone].Stop();
                    watchers.Remove(gone);
                }
                foreach (var name in want.Where(n => !watchers.ContainsKey(n)))
                    watchers[name] = new Watcher(name, () => Interval, () => Since);
            }
        }

        /// <summary>Re-check the server's update results, restarts and services on its next poll, and poll it now.</summary>
        public void RequestVerify(string name)
        {
            lock (gate)
                if (watchers.TryGetValue(name, out var w)) w.RequestVerify();
        }

        public RoundSnapshot Snapshot()
        {
            List<KeyValuePair<string, Observation>> observations;
            lock (gate) observations = watchers.Select(kv => new KeyValuePair<string, Observation>(kv.Key, kv.Value.Latest)).ToList();
            return new RoundSnapshot(DateTime.Now, Since, observations, expected);
        }

        public void Dispose()
        {
            lock (gate)
            {
                foreach (var w in watchers.Values) w.Stop();
                watchers.Clear();
            }
        }

        sealed class Watcher
        {
            static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
            readonly ServerProbe probe;
            volatile bool stop, wake;
            volatile Observation latest = new Observation();

            public Watcher(string name, Func<TimeSpan> interval, Func<DateTime> since)
            {
                probe = new ServerProbe(name, CallTimeout, since);
                new Thread(() => Run(interval)) { IsBackground = true, Name = "WUGrid " + name }.Start();
            }

            public Observation Latest => latest;

            /// <summary>A worker blocked in a DCOM call finishes that call, then exits.</summary>
            public void Stop() => stop = true;

            public void RequestVerify()
            {
                probe.RequestVerify();
                wake = true;
            }

            void Run(Func<TimeSpan> interval)
            {
                using (probe)
                    while (!stop)
                    {
                        wake = false;   // before the poll, so a request made during it still cuts the wait short
                        var o = probe.Poll(interim => { if (!stop) latest = interim; });
                        if (stop) break;
                        latest = o;
                        // Re-read every slice, so a shorter interval takes effect without waiting out the old one
                        for (var slept = 0; slept < interval().TotalMilliseconds && !stop && !wake; slept += 250) Thread.Sleep(250);
                    }
            }
        }
    }
}
