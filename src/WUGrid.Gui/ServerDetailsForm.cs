using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using WUGrid.Core;
using WUGrid.Core.Errors;
using WUGrid.Core.Reports;

namespace WUGrid.Gui
{
    /// <summary>
    /// Everything known about one server in this round: state, build before and after, update results with their error
    /// codes explained, who restarted it and why, boots, services not running and why they did not start. Follows the
    /// grid while open.
    /// </summary>
    sealed class ServerDetailsForm : Form
    {
        readonly string server;
        readonly Func<string, ServerRow> current;
        readonly RichTextBox text = new RichTextBox();
        readonly Timer timer = new Timer { Interval = 2000 };
        readonly Button lookup = new Button { Text = L.T("Error codes…"), AutoSize = true };
        readonly Button earlierBoots = new Button { Text = L.T("Earlier boots"), AutoSize = true, Visible = false };
        readonly bool canReadEarlier;
        readonly Font body = new Font("Segoe UI", 9.75f);
        readonly Font heading = new Font("Segoe UI Semibold", 10.5f);
        readonly Font title = new Font("Segoe UI Semibold", 13f);
        string shown;
        int firstCode;

        /// <param name="recheck">Asks for a fresh verification (live rounds); null hides the button.</param>
        /// <param name="markExpected">Opens the expected-stops window for this server; null hides the button.</param>
        /// <param name="readEarlierBoots">Reads the server's earlier boots in the background (live rounds); null hides the button.</param>
        public ServerDetailsForm(string server, Func<string, ServerRow> current, Action<string> recheck, Action<string> markExpected = null,
                                 Action<string> readEarlierBoots = null)
        {
            this.server = server;
            this.current = current;
            var scale = DeviceDpi / 96f;
            Text = server;
            Icon = AppInfo.Icon;
            Font = body;
            ClientSize = new Size((int)(820 * scale), (int)(620 * scale));
            MinimumSize = new Size((int)(560 * scale), (int)(380 * scale));
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;

            text.Dock = DockStyle.Fill;
            text.ReadOnly = true;
            text.BorderStyle = BorderStyle.None;
            text.BackColor = Color.White;
            text.DetectUrls = false;
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding((int)(14 * scale), (int)(10 * scale), (int)(10 * scale), 0), BackColor = Color.White };
            host.Controls.Add(text);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = (int)(48 * scale), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding((int)(8 * scale)) };
            var close = new Button { Text = L.T("Close"), AutoSize = true, MinimumSize = new Size((int)(80 * scale), 0) };
            close.Click += (s, e) => Close();   // modeless window (Show, not ShowDialog): a DialogResult alone closes nothing
            bar.Controls.Add(close);
            lookup.Click += (s, e) => { using (var f = new ErrorLookupForm(firstCode == 0 ? (int?)null : firstCode)) f.ShowDialog(this); };
            bar.Controls.Add(lookup);
            if (recheck != null)
            {
                var again = new Button { Text = L.T("Check again"), AutoSize = true };
                again.Click += (s, e) => recheck(server);
                bar.Controls.Add(again);
            }
            if (markExpected != null)
            {
                var expected = new Button { Text = L.T("Expected…"), AutoSize = true };
                expected.Click += (s, e) => markExpected(server);
                bar.Controls.Add(expected);
            }
            canReadEarlier = readEarlierBoots != null;
            earlierBoots.Click += (s, e) =>
            {
                earlierBoots.Enabled = false;   // until the poll shows it reading
                readEarlierBoots?.Invoke(server);
            };
            bar.Controls.Add(earlierBoots);
            CancelButton = close;
            Load += (s, e) => ActiveControl = close;   // not the text box: no caret blinking in the report (only once visible)
            Controls.Add(host);
            Controls.Add(bar);

            timer.Tick += (s, e) => Render();
            Load += (s, e) => { Render(); timer.Start(); };
            FormClosed += (s, e) => timer.Stop();
        }

        void Render()
        {
            var row = current(server);
            // Offered for a service that failed to start, when no read is running or done for this cut-off
            var read = row?.Observation.EarlierBoots;
            earlierBoots.Visible = canReadEarlier && HasFailures(row);
            earlierBoots.Enabled = read == null || read.Error != null;
            var doc = new Doc(heading);
            firstCode = 0;
            if (row == null) doc.Line(L.T("This server is no longer in the list."), body);
            else Describe(doc, row);
            if (doc.Plain == shown) return;   // unchanged: keep the reader's scroll position
            shown = doc.Plain;
            text.Clear();
            foreach (var (t, font, color, indent) in doc.Parts)
            {
                text.SelectionStart = text.TextLength;
                text.SelectionFont = font;
                text.SelectionColor = color;
                text.SelectionIndent = indent;
                text.AppendText(t);
            }
            text.SelectionStart = 0;
        }

        void Describe(Doc d, ServerRow r)
        {
            var o = r.Observation;
            var s = r.Status;
            var v = o.Verification != null && o.LastBoot == o.Verification.Boot ? o.Verification : null;
            var (strong, _) = Palette.For(s);
            d.Text(r.Name + "   ", title, Palette.Text);
            d.Line(s.Label + (s.State == ServerState.Back ? "  " + (s.IsClean ? L.T("clean") : L.T("flagged")) : ""), heading, strong);
            if (s.Note != "") d.Line(s.Note, body, Palette.Text);
            d.Line(L.F("Last boot {0:dd-MM-yyyy HH:mm}", o.LastBoot) +
                   (s.Elapsed.HasValue ? " · " + (s.State == ServerState.Down ? L.F("down {0}", Durations.Format(s.Elapsed.Value)) : L.F("up {0}", Durations.Format(s.Elapsed.Value))) : ""),
                   body, Palette.MutedText);
            var build = RoundReport.BuildChange(r);
            if (build != "") d.Line(L.F("Build {0}", build) + (o.Baseline?.Build != null && o.Build != null && o.Baseline.Build != o.Build ? "  " + L.T("(before → now)") : ""), body, Palette.MutedText);

            if (s.State == ServerState.Booting && o.WmiHResult != 0)
            {
                d.Heading(L.T("Why WMI does not answer"));
                Explain(d, o.WmiHResult);
            }

            if (v == null && s.State == ServerState.Back)
                d.Line("\n" + L.T("Checking update results, restarts and services…"), body, Palette.MutedText);

            if (v == null && o.UpdatesInFlight.Count > 0)
            {
                d.Heading(L.F("Updates not installed yet ({0})", o.UpdatesInFlight.Count));
                InFlight(d, o.UpdatesInFlight);
                d.Line(L.T("Not restarted yet: it installs later, perhaps after the restart, and may ask for another one. Checked every 2 min."),
                       body, Palette.MutedText);
            }

            if (v != null)
            {
                d.Heading(L.F("Updates since the cut-off ({0})", v.Updates.Count));
                if (v.Updates.Count == 0) d.Line(L.T("No Windows Update installs logged."), body, Palette.MutedText);
                foreach (var u in v.Updates)
                {
                    d.Text(u.Succeeded ? "✓ " : "✗ ", heading, u.Succeeded ? Palette.CleanText : Palette.AlarmText);
                    d.Line($"{u.Time:dd-MM HH:mm}  {u.Title}", body, Palette.Text);
                    if (!u.Succeeded)
                    {
                        if (firstCode == 0) firstCode = u.ErrorCode;
                        Explain(d, u.ErrorCode);
                    }
                }
                InFlight(d, v.UpdatesInFlight);
                if (v.UpdatesInFlight.Count > 0)
                    d.Line(L.T("Not done yet: it installs later and may ask for another restart. Checked again every 2 min until it has a result."),
                           body, Palette.MutedText);

                d.Heading(L.F("Restarts ({0}) and boots ({1})", v.Restarts.Count, v.Boots.Count));
                foreach (var x in v.Restarts) d.Line(RoundReport.RestartLine(x), body, Palette.Text);
                if (v.Boots.Count > 0)
                    d.Line(L.F("Booted {0}", string.Join(", ", v.Boots.Select(b => $"{b.Time:HH:mm}" + (b.Build != null ? $" ({b.Build})" : "")))), body, Palette.MutedText);
                if (v.UnexpectedShutdown) d.Line(L.T("An unexpected shutdown was logged (event 6008)."), body, Palette.AlarmText);

                d.Heading(L.T("Services"));
                var known = o.Baseline?.RunningAutoServices.Count ?? 0;
                var flagged = r.UnexpectedStops;
                var expected = r.ExpectedStops;
                if (!v.ServicesChecked) d.Line(L.F("Checked once the server has been up {0} min (delayed-start services).", VerificationLimits.ServicesAfter), body, Palette.MutedText);
                else
                {
                    if (flagged.Count == 0)
                        d.Line(v.ComparedToBaseline
                            ? (expected.Count == 0 ? L.F("Every service that ran before the restart is running ({0} of {1}).", known, known)
                                                   : L.F("Every other service that ran before the restart is running ({0} of {1}).", known - expected.Count, known))
                            : (expected.Count == 0 ? L.T("Every Automatic service is running.") : L.T("Every other Automatic service is running.")), body, Palette.CleanText);
                    else
                    {
                        d.Line(v.ComparedToBaseline ? L.F("Ran before the restart, not running now ({0} of {1}):", flagged.Count, known) : L.T("Automatic, not running (no baseline from before the restart):"), body, Palette.MutedText);
                        foreach (var i in flagged)
                        {
                            d.Line("• " + RoundReport.ServiceLine(i), body, Palette.AlarmText);
                            Advise(d, i);
                        }
                    }
                    if (expected.Count > 0)
                    {
                        d.Line(L.F("Not running, expected ({0}): shown, not flagged.", expected.Count), body, Palette.MutedText);
                        foreach (var x in expected)
                        {
                            d.Line("• " + RoundReport.ExpectedLine(x), body, Palette.MutedText);
                            Advise(d, x.Issue);
                        }
                    }
                    if (HasFailures(r)) EarlierBootsStatus(d, o.EarlierBoots);
                }
                if (v.RemovedServices.Count > 0)
                {
                    d.Line(L.F("Marked for deletion before the restart, removed as planned ({0}):", v.RemovedServices.Count), body, Palette.MutedText);
                    foreach (var removed in v.RemovedServices) d.Line("• " + RoundReport.RemovedLine(removed), body, Palette.MutedText);
                }

                d.Line("", body, Palette.Text);
                d.Line(L.F("Checked {0:HH:mm:ss}", v.CheckedAt) + " · " + (v.Settled ? L.T("settled, no more checks for this boot") : L.T("checked again every 2 min until it settles")), body, Palette.MutedText);
            }
            else if (s.State == ServerState.RestartPending || s.State == ServerState.NoRestartPending || s.State == ServerState.Installing)
            {
                d.Heading(L.T("Before the restart"));
                d.Line(o.Baseline != null
                    ? L.F("Baseline taken {0:HH:mm}: build {1}, {2} running Automatic services to compare after the restart.", o.Baseline.TakenAt, o.Baseline.Build, o.Baseline.RunningAutoServices.Count)
                    : L.T("Baseline not taken yet."), body, Palette.MutedText);
                if (o.Baseline != null && o.Baseline.MarkedForDeletion.Count > 0)
                    d.Line(L.F("Marked for deletion, gone at the next boot and not expected back ({0}): {1}", o.Baseline.MarkedForDeletion.Count,
                               string.Join(", ", o.Baseline.MarkedForDeletion.Select(kv => $"{kv.Value} ({kv.Key})"))), body, Palette.MutedText);
            }
            if (o.VerificationError != null) d.Line(L.F("Last check failed: {0}", o.VerificationError), body, Palette.AlarmText);
        }

        void Advise(Doc d, Core.Verification.ServiceIssue i)
        {
            if (i.Failure != null && i.Failure.Advice != "") d.Line(i.Failure.Advice, body, Palette.MutedText, (int)(20 * DeviceDpi / 96f));
        }

        void EarlierBootsStatus(Doc d, EarlierBootsRead read)
        {
            if (read == null)
            {
                if (canReadEarlier)
                    d.Line(L.F("Earlier boots reads the System log back {0} days for whether these services failed at those boots too: minutes on a large log, in the background.",
                               (int)EarlierBootsRead.Lookback.TotalDays), body, Palette.MutedText);
            }
            else if (read.Reading)
                d.Line(L.F("Reading earlier boots: the System log back to {0:dd-MM-yyyy}, minutes on a large log…", read.From), body, Palette.MutedText);
            else if (read.Error != null)
                d.Line(L.F("Reading earlier boots failed at {0:HH:mm}: {1}", read.Finished, read.Error), body, Palette.AlarmText);
            else
                d.Line(L.F("Earlier boots read at {0:HH:mm}: the System log back to {1:dd-MM-yyyy}.", read.Finished, read.From), body, Palette.MutedText);
        }

        static bool HasFailures(ServerRow r)
        {
            var v = r?.CurrentVerification;
            return v != null && v.StoppedServices.Any(i => i.Failure != null);
        }

        void InFlight(Doc d, System.Collections.Generic.IReadOnlyList<Core.Verification.UpdateInFlight> updates)
        {
            foreach (var u in updates)
            {
                d.Text("… ", heading, Palette.MutedText);
                d.Line($"{u.Time:dd-MM HH:mm}  {(u.Title != "" ? u.Title : u.UpdateId)}  " +
                       (u.Installing ? L.T("(install started, no result yet)") : L.T("(download started, not installed yet)")), body, Palette.Text);
            }
        }

        void Explain(Doc d, int code)
        {
            var indent = (int)(20 * DeviceDpi / 96f);
            foreach (var e in ErrorCatalog.Lookup(code))
            {
                d.Line($"{e.Hex} {e.Name}  [{e.Family}]", heading, Palette.Text, indent);
                d.Line(e.Description, body, Palette.Text, indent);
                if (e.Mitigation != "") d.Line(L.F("What to do: {0}", e.Mitigation), body, Palette.MutedText, indent);
            }
        }

        static class VerificationLimits
        {
            public static readonly int ServicesAfter = (int)Core.Verification.VerificationBuilder.ServicesAfter.TotalMinutes;
        }

        /// <summary>Styled text built first, so an unchanged render can be skipped.</summary>
        sealed class Doc
        {
            public readonly System.Collections.Generic.List<(string, Font, Color, int)> Parts = new System.Collections.Generic.List<(string, Font, Color, int)>();
            readonly System.Text.StringBuilder plain = new System.Text.StringBuilder();
            public string Plain => plain.ToString();

            readonly Font heading;

            public Doc(Font heading) { this.heading = heading; }

            public void Text(string t, Font f, Color c, int indent = 0) { Parts.Add((t, f, c, indent)); plain.Append(t); }
            public void Line(string t, Font f, Color? c = null, int indent = 0) => Text(t + "\n", f, c ?? Palette.Text, indent);
            public void Heading(string t) => Line("\n" + t, heading, Palette.Text);
        }
    }
}
