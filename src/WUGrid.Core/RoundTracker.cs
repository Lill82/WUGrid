using System;
using System.Collections.Generic;
using System.Linq;

namespace WUGrid.Core
{
    /// <summary>Seen = a server's first state (at start, or when it joins the list). Action = something WUGrid sent to a
    /// server (a restart), logged by whoever sent it.</summary>
    public enum RoundEventKind { Seen, Transition, Alarm, AllClean, Action }

    public sealed class RoundEvent
    {
        public RoundEvent(DateTime time, RoundEventKind kind, string server, string text, ServerStatus status = null, ServerStatus previous = null)
        {
            Time = time;
            Kind = kind;
            Server = server;
            Text = text;
            Status = status;
            Previous = previous;
        }

        public DateTime Time { get; }
        public RoundEventKind Kind { get; }

        /// <summary>Null for round-wide events.</summary>
        public string Server { get; }

        public string Text { get; }

        /// <summary>The server's status at the event (after the change, for a transition); null for round-wide events and
        /// actions.</summary>
        public ServerStatus Status { get; }

        /// <summary>Before the change, for a transition; otherwise null.</summary>
        public ServerStatus Previous { get; }

        public override string ToString() => Server == null ? $"{Time:HH:mm:ss} {Text}" : $"{Time:HH:mm:ss} {Server}: {Text}";
    }

    /// <summary>
    /// Turns successive snapshots into the round's event stream: each server's first state, one line per state change, an
    /// alarm when something has gone on too long, and one line when every server is back clean with its checks done.
    /// Replaces the legacy follower.
    /// </summary>
    public sealed class RoundTracker
    {
        readonly Dictionary<string, ServerStatus> last = new Dictionary<string, ServerStatus>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> alarmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public TimeSpan DownAlarm { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>Measured from the boot: a server can show pending flags for a while right after it comes back.</summary>
        public TimeSpan RebootPendingAlarm { get; set; } = TimeSpan.FromMinutes(20);

        /// <summary>As of the last update: every server is back clean with its checks done (<see cref="ServerRow.ChecksDone"/>).</summary>
        public bool AllClean { get; private set; }

        public IReadOnlyList<RoundEvent> Update(RoundSnapshot snapshot)
        {
            var events = new List<RoundEvent>();
            var now = snapshot.Now;
            foreach (var row in snapshot.Rows)
            {
                var s = row.Status;
                if (s.State == ServerState.Checking) continue;   // no answer yet: neither a change nor a baseline

                // Routine notes (servicing finishing, component cleanup) and changing error text are not steps
                if (!last.TryGetValue(row.Name, out var prev))
                    events.Add(new RoundEvent(now, RoundEventKind.Seen, row.Name, $"first seen {Describe(s)}", s));
                else if (prev.State != s.State || prev.Flag != s.Flag)
                    events.Add(new RoundEvent(now, RoundEventKind.Transition, row.Name, $"{Describe(prev)} -> {Describe(s)}", s, prev));
                last[row.Name] = s;
                if (s.IsClean && row.ChecksDone) done.Add(row.Name);
                else done.Remove(row.Name);

                Alarm(events, now, row.Name, "down", s.State == ServerState.Down && s.Elapsed >= DownAlarm,
                      s, () => $"DOWN for {Durations.Format(s.Elapsed.Value)}");
                Alarm(events, now, row.Name, "pending", s.Flag == BackFlag.RebootPending && s.Elapsed >= RebootPendingAlarm,
                      s, () => $"back {Durations.Format(s.Elapsed.Value)}, {s.Note}");
            }

            // Servers dropped from the list are forgotten; if they come back they start fresh
            var current = new HashSet<string>(snapshot.Rows.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var gone in last.Keys.Where(k => !current.Contains(k)).ToList())
            {
                last.Remove(gone);
                done.Remove(gone);
                alarmed.RemoveWhere(a => a.StartsWith(gone + "|", StringComparison.OrdinalIgnoreCase));
            }

            // A server briefly back to "checking..." (WMI reconnecting) counts with its last known state, so the
            // all-clean line is not said twice. A green row counts only once its checks are in (update results, services
            // after 5 min up): the round is not done while a failure can still turn up
            var allClean = snapshot.Rows.Count > 0 && snapshot.Rows.All(r => done.Contains(r.Name));
            if (allClean && !AllClean)
                events.Add(new RoundEvent(now, RoundEventKind.AllClean, null, $"ALL {snapshot.Rows.Count} BACK CLEAN"));
            AllClean = allClean;
            return events;
        }

        // Once per episode: the alarm re-arms when the condition clears, so a second outage alarms again
        void Alarm(List<RoundEvent> events, DateTime now, string server, string kind, bool on, ServerStatus s, Func<string> text)
        {
            var key = server + "|" + kind;
            if (!on) { alarmed.Remove(key); return; }
            if (alarmed.Add(key)) events.Add(new RoundEvent(now, RoundEventKind.Alarm, server, "ALARM " + text(), s));
        }

        static string Describe(ServerStatus s) => s.Note == "" ? s.State.ToLabel() : $"{s.State.ToLabel()} ({s.Note})";
    }
}
