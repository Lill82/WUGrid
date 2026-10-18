using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WUGrid.Core.Reports
{
    /// <summary>A stretch of console text in one colour.</summary>
    public readonly struct TextRun
    {
        public TextRun(string text, ConsoleColor color)
        {
            Text = text;
            Color = color;
        }

        public string Text { get; }
        public ConsoleColor Color { get; }
    }

    /// <summary>
    /// The round as console lines, for <c>wugrid-cli watch</c>: a title, the counts, one row per server, the footnote, and
    /// the latest events in the space left. When the servers do not fit, clean BACK servers fold into one line first, then
    /// the rest is cut with a "+N more" line. Every line fits the width and the frame leaves the window's last line empty,
    /// so the console never scrolls. Like the GUI grid it keeps a server's last boot while it is down, and the footnote
    /// stays once it has shown. The caller only writes the lines out.
    /// </summary>
    public sealed class ConsoleGrid
    {
        const string Gap = "  ";
        readonly Dictionary<string, DateTime> lastBoots = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        bool footnote;

        public static ConsoleColor ColorOf(ServerStatus s)
        {
            switch (s.State)
            {
                case ServerState.RestartPending: return ConsoleColor.Magenta;
                case ServerState.NoRestartPending: return ConsoleColor.Gray;
                case ServerState.Installing: return ConsoleColor.Cyan;
                case ServerState.Down: return ConsoleColor.Red;
                case ServerState.Booting: return ConsoleColor.Yellow;
                case ServerState.Back: return s.IsClean ? ConsoleColor.Green : ConsoleColor.DarkYellow;   // the GUI's orange
                default: return ConsoleColor.DarkGray;
            }
        }

        public static ConsoleColor ColorOf(RoundEventKind kind)
        {
            switch (kind)
            {
                case RoundEventKind.Alarm: return ConsoleColor.Red;
                case RoundEventKind.AllClean: return ConsoleColor.Green;
                case RoundEventKind.Action: return ConsoleColor.Cyan;
                case RoundEventKind.Seen: return ConsoleColor.DarkGray;
                default: return ConsoleColor.Gray;
            }
        }

        /// <param name="title">First line (time, cut-off, keys…), written as given.</param>
        /// <param name="events">Oldest first; the newest that fit are shown.</param>
        /// <param name="warning">Shown in red under the counts, e.g. a state file that cannot be written; null for none.</param>
        public IReadOnlyList<IReadOnlyList<TextRun>> Frame(RoundSnapshot snapshot, IReadOnlyList<RoundEvent> events, string title,
                                                           int width, int height, string warning = null)
        {
            var rows = snapshot.Rows;
            foreach (var r in rows)
                if (r.Observation.LastBoot.HasValue) lastBoots[r.Name] = r.Observation.LastBoot.Value;
            foreach (var gone in lastBoots.Keys.Except(rows.Select(r => r.Name), StringComparer.OrdinalIgnoreCase).ToList())
                lastBoots.Remove(gone);
            footnote |= rows.Any(r => r.Status.LikelyTransient);

            var lines = new List<IReadOnlyList<TextRun>>();
            void Add(ConsoleColor color, string text) => lines.Add(new[] { new TextRun(Fit(text, width), color) });

            Add(ConsoleColor.White, title);
            lines.Add(Counts(rows, width));
            if (warning != null) Add(ConsoleColor.Red, warning);
            Add(ConsoleColor.Gray, "");

            var nameWidth = Math.Min(24, Math.Max(6, rows.Select(r => r.Name.Length).DefaultIfEmpty(0).Max()));
            var builds = rows.ToDictionary(r => r.Name, r => RoundReport.ShortBuildChange(r.Observation, "->"), StringComparer.OrdinalIgnoreCase);
            var buildWidth = Math.Min(26, Math.Max(5, builds.Values.Select(b => b.Length).DefaultIfEmpty(0).Max()));
            string Columns(string name, string ping, string state, string boot, string elapsed, string ti, string build, string note) =>
                string.Join(Gap, name.PadRight(nameWidth).Substring(0, nameWidth), ping.PadRight(4), state.PadRight(18), boot.PadRight(11),
                            elapsed.PadRight(7), ti.PadRight(8), build.PadRight(buildWidth), note);
            Add(ConsoleColor.DarkGray, Columns("Server", "Ping", "State", "Last boot", "Up/Down", "TiWorker", "Build", "Note"));

            var foot = footnote ? Wrap(StateClassifier.TransientFootnote, width) : new List<string>();
            var room = Math.Max(0, height - 1 - lines.Count - (foot.Count > 0 ? foot.Count + 1 : 0));
            IReadOnlyList<ServerRow> shown = rows;
            var folded = new List<ServerRow>();
            var cut = 0;
            if (rows.Count > room)
            {
                folded = rows.Where(r => r.Status.IsClean).ToList();
                if (folded.Count > 0)
                {
                    shown = rows.Where(r => !r.Status.IsClean).ToList();
                    room = Math.Max(0, room - 1);
                }
                if (shown.Count > room)
                {
                    var keep = Math.Max(0, room - 1);
                    cut = shown.Count - keep;
                    shown = shown.Take(keep).ToList();
                }
            }

            foreach (var r in shown)
            {
                var o = r.Observation;
                var s = r.Status;
                Add(ColorOf(s), Columns(r.Name,
                                        o.Ping == true ? "up" : o.Ping == false ? "--" : "",
                                        s.Label,
                                        lastBoots.TryGetValue(r.Name, out var boot) ? boot.ToString("dd-MM HH:mm") : "",
                                        s.Elapsed.HasValue ? Durations.Format(s.Elapsed.Value) : "",
                                        o.TiWorker && o.Ping == true ? "yes" : "",
                                        builds[r.Name],
                                        s.Note));
            }
            if (folded.Count > 0 && shown.Count < rows.Count)
                Add(ConsoleColor.Green, Ellipsis($"BACK clean ({folded.Count}): {string.Join(" ", folded.Select(r => r.Name))}", width));
            if (cut > 0) Add(ConsoleColor.DarkGray, $"+{cut} more not shown: enlarge the window");

            if (foot.Count > 0)
            {
                Add(ConsoleColor.Gray, "");
                foreach (var f in foot) Add(ConsoleColor.DarkGray, f);
            }

            var left = height - 1 - lines.Count - 1;
            if (left > 0 && events.Count > 0)
            {
                Add(ConsoleColor.Gray, "");
                foreach (var e in events.Skip(Math.Max(0, events.Count - left))) Add(ColorOf(e.Kind), e.ToString());
            }
            return lines;
        }

        static IReadOnlyList<TextRun> Counts(IReadOnlyList<ServerRow> rows, int width)
        {
            var counts = new (string Label, int Count, ConsoleColor Color)[]
            {
                ("BACK", rows.Count(r => r.Status.IsClean), ConsoleColor.Green),
                ("FLAGGED", rows.Count(r => r.Status.State == ServerState.Back && !r.Status.IsClean), ConsoleColor.DarkYellow),
                ("BOOTING", rows.Count(r => r.Status.State == ServerState.Booting), ConsoleColor.Yellow),
                ("DOWN", rows.Count(r => r.Status.State == ServerState.Down), ConsoleColor.Red),
                ("INSTALLING", rows.Count(r => r.Status.State == ServerState.Installing), ConsoleColor.Cyan),
                ("PENDING", rows.Count(r => r.Status.State == ServerState.RestartPending), ConsoleColor.Magenta),
                ("NOT PENDING", rows.Count(r => r.Status.State == ServerState.NoRestartPending), ConsoleColor.Gray),
                ("checking", rows.Count(r => r.Status.State == ServerState.Checking), ConsoleColor.DarkGray),
            };
            var runs = new List<TextRun>();
            var used = 0;
            foreach (var (label, count, color) in counts)
            {
                var text = Fit((used == 0 ? "" : "   ") + $"{count} {label}", width - used);
                if (text == "") break;
                runs.Add(new TextRun(text, count > 0 ? color : ConsoleColor.DarkGray));
                used += text.Length;
            }
            return runs;
        }

        static string Fit(string text, int width) => text.Length <= width ? text : text.Substring(0, Math.Max(0, width));

        static string Ellipsis(string text, int width) =>
            text.Length <= width ? text : width <= 3 ? Fit(text, width) : text.Substring(0, width - 3) + "...";

        static List<string> Wrap(string text, int width)
        {
            var lines = new List<string>();
            var line = new StringBuilder();
            foreach (var word in text.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) lines.Add(line.ToString());
            return lines.Select(l => Fit(l, width)).ToList();   // a word longer than the width
        }
    }
}
