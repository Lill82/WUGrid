using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using WUGrid.Core;
using WUGrid.Core.Demo;
using WUGrid.Core.Errors;
using WUGrid.Core.Probing;
using WUGrid.Core.Reports;
using WUGrid.Core.Restart;

namespace WUGrid.Gui
{
    sealed partial class MainForm : Form
    {
        const int MaxEvents = 2000;

        readonly IRoundSource source;
        readonly DemoRound demo;
        readonly LiveRound live;
        readonly string listPath;
        readonly Dictionary<string, AccessCheckResult> checks = new Dictionary<string, AccessCheckResult>(StringComparer.OrdinalIgnoreCase);
        readonly float scale;
        readonly Timer timer = new Timer();
        readonly Dictionary<string, DataGridViewRow> rows = new Dictionary<string, DataGridViewRow>(StringComparer.OrdinalIgnoreCase);
        RoundTracker tracker = new RoundTracker();
        RoundSnapshot lastSnapshot;
        Font boldEvent;

        readonly DataGridView grid = new BufferedGrid();
        readonly Label footnote = new Label();
        readonly ListView events = new ListView();
        readonly FlowLayoutPanel chips = new FlowLayoutPanel();
        readonly ToolTip chipTips = new ToolTip();
        readonly Label clock = new Label();
        readonly SplitContainer split = new SplitContainer();
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel();

        /// <param name="listPath">Where a live round's server list is saved when it changes.</param>
        public MainForm(IRoundSource source, string listPath = null)
        {
            this.source = source;
            this.listPath = listPath;
            demo = source as DemoRound;
            live = source as LiveRound;
            scale = DeviceDpi / 96f;
            timer.Interval = demo != null ? 500 : 1000;

            Text = $"WUGrid {AppInfo.Version}" + (demo != null ? " — demo round" : "");
            Icon = AppInfo.Icon;
            Font = new Font("Segoe UI", 9.75f);
            ClientSize = new Size(Px(1180), Px(760));
            MinimumSize = new Size(Px(800), Px(480));
            StartPosition = FormStartPosition.CenterScreen;

            BuildGrid();
            BuildEvents();
            footnote.Dock = DockStyle.Bottom;
            footnote.Height = Px(48);   // two lines
            footnote.Padding = new Padding(Px(10), Px(5), Px(10), Px(3));
            footnote.ForeColor = Palette.MutedText;
            footnote.BackColor = Palette.Header;
            footnote.Text = StateClassifier.TransientFootnote;
            footnote.Visible = false;
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.Panel1.Controls.Add(grid);
            split.Panel1.Controls.Add(footnote);
            split.Panel2.Controls.Add(events);
            split.SplitterWidth = Px(6);

            var strip = new StatusStrip { SizingGrip = true, ShowItemToolTips = true };
            status.Spring = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            var credit = new ToolStripStatusLabel("Vibed by Claude && Lill")   // && = a literal & in tool strip text
            {
                IsLink = true,
                LinkBehavior = LinkBehavior.HoverUnderline,
                ToolTipText = AppInfo.RepoUrl,
            };
            credit.Click += (s, e) => AppInfo.OpenRepo();
            strip.Items.AddRange(new ToolStripItem[] { status, credit });

            // Docking runs from the last control added: tool strip outermost, then the header, then the split fills the rest
            Controls.AddRange(new Control[] { split, BuildHeader(), BuildToolbar(), strip });

            timer.Tick += (s, e) => ShowRound(source.Snapshot());
            Load += (s, e) =>
            {
                split.SplitterDistance = Math.Max(Px(200), split.Height - Px(190));
                ShowRound(source.Snapshot());
                timer.Start();
            };
            Shown += (s, e) => { if (live != null && live.Servers.Count == 0) EditServers(); };
            FormClosed += (s, e) => timer.Stop();
        }

        int Px(int dip) => (int)Math.Round(dip * scale);

        Control BuildHeader()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = Px(52), Padding = new Padding(Px(10), Px(10), Px(12), Px(8)) };
            chips.Dock = DockStyle.Fill;
            chips.WrapContents = false;
            clock.Dock = DockStyle.Right;
            clock.Width = Px(300);
            clock.TextAlign = ContentAlignment.MiddleRight;
            clock.Font = new Font("Segoe UI", 10.5f);
            clock.ForeColor = Palette.Text;
            header.Controls.Add(chips);
            header.Controls.Add(clock);
            return header;
        }

        Control BuildToolbar()
        {
            var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(Px(6), Px(2), 0, Px(2)) };
            if (demo != null)
            {
                var pause = new ToolStripButton("Pause");
                var restart = new ToolStripButton("Restart demo");
                var speed = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = Px(70) };
                var speeds = new[] { 10, 30, 60, 120 };
                foreach (var x in speeds) speed.Items.Add($"{x}×");
                speed.SelectedIndex = Array.IndexOf(speeds, (int)demo.Speed) is var i && i >= 0 ? i : 1;
                pause.Click += (s, e) => { demo.Paused = !demo.Paused; pause.Text = demo.Paused ? "Resume" : "Pause"; };
                restart.Click += (s, e) => { demo.Restart(); ResetView(); ShowRound(demo.Snapshot()); };
                speed.SelectedIndexChanged += (s, e) => demo.Speed = speeds[speed.SelectedIndex];
                bar.Items.AddRange(new ToolStripItem[] { pause, restart, new ToolStripSeparator(), new ToolStripLabel("Speed"), speed });
            }
            else if (live != null)
            {
                var servers = new ToolStripButton("Servers…") { ToolTipText = "Add or remove servers; each one added is checked once (read-only)" };
                servers.Click += (s, e) => EditServers();
                var reboot = new ToolStripButton("Reboot…") { ToolTipText = "Pick servers to restart (never forced, only when no update work is running)" };
                reboot.Font = new Font(reboot.Font, FontStyle.Bold);
                reboot.Click += (s, e) => Reboot();
                var picker = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "dd-MM-yyyy HH:mm", Width = Px(140), Value = live.Since };
                picker.ValueChanged += (s, e) => live.Since = picker.Value;
                var poll = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = Px(64) };
                var seconds = new[] { 2, 5, 10, 15, 30, 60 };
                foreach (var x in seconds) poll.Items.Add($"{x} s");
                poll.SelectedIndex = Array.IndexOf(seconds, (int)live.Interval.TotalSeconds) is var p && p >= 0 ? p : 1;
                poll.SelectedIndexChanged += (s, e) => live.Interval = TimeSpan.FromSeconds(seconds[poll.SelectedIndex]);
                const string pollTip = "How often each server is pinged and read over WMI";
                bar.Items.AddRange(new ToolStripItem[]
                {
                    servers, reboot, new ToolStripSeparator(),
                    new ToolStripLabel("Restarted after") { ToolTipText = "Round cut-off: a server counts as restarted when it booted after this" },
                    new ToolStripControlHost(picker), new ToolStripSeparator(),
                    new ToolStripLabel("Poll every") { ToolTipText = pollTip }, poll,
                });
                poll.ToolTipText = pollTip;
            }
            var errors = new ToolStripButton("Error codes…") { ToolTipText = "Explain a CBS, Windows Update, WMI or Win32 error code" };
            errors.Click += (s, e) => ShowErrorCodes(null);
            var report = new ToolStripButton("Report…") { ToolTipText = "Save the round as an HTML page (opened after saving) or a CSV file" };
            report.Click += (s, e) => SaveReport();
            bar.Items.AddRange(new ToolStripItem[] { new ToolStripSeparator(), report, errors });
            var note = demo != null ? "Round time runs faster than real time" : "Read-only, except restarts you send with Reboot…";
            bar.Items.Add(new ToolStripLabel(note) { ForeColor = Palette.MutedText, Alignment = ToolStripItemAlignment.Right });
            return bar;
        }

        void EditServers()
        {
            using (var dialog = new ServersForm(live.Servers, checks))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                live.SetServers(dialog.Servers);
                if (listPath != null)
                {
                    try { ServerList.Save(listPath, dialog.Servers); }
                    catch (Exception ex) { MessageBox.Show(this, $"Could not save the server list to {listPath}:\n{ex.Message}", "WUGrid", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                }
                ShowRound(live.Snapshot());
            }
        }

        void Reboot()
        {
            using (var dialog = new RestartForm(() => lastSnapshot, Restarted)) dialog.ShowDialog(this);
        }

        // Every restart result goes to the event pane; results can arrive after the Reboot window has closed
        void Restarted(RestartResult r, RestartOptions options)
        {
            if (InvokeRequired) { BeginInvoke((Action)(() => Restarted(r, options))); return; }
            var prefix = options.CheckOnly ? "CHECK ONLY: " : "RESTART ";
            string text;
            switch (r.Outcome)
            {
                case RestartOutcome.Requested: text = $"RESTART requested by {RestartAction.Operator} ({r.Detail})"; break;
                case RestartOutcome.WouldRestart: text = $"CHECK ONLY: would restart ({r.Detail})"; break;
                case RestartOutcome.Skipped: text = prefix + "skipped: " + r.Detail; break;
                default: text = prefix + "failed: " + r.Detail + (r.Code != 0 ? $" ({ErrorCatalog.Summary(r.Code)})" : ""); break;
            }
            AddEvent(new RoundEvent(DateTime.Now, RoundEventKind.Action, r.Server, text));
            if (r.Outcome == RestartOutcome.Requested) live.RequestVerify(r.Server);   // poll it now rather than at the next interval
        }

        void ShowErrorCodes(int? code)
        {
            using (var dialog = new ErrorLookupForm(code)) dialog.ShowDialog(this);
        }

        void ShowDetails(string name) =>
            new ServerDetailsForm(name, n => lastSnapshot?.Rows.FirstOrDefault(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase)),
                                  live == null ? (Action<string>)null : live.RequestVerify).Show(this);

        void SaveReport()
        {
            var snap = source.Snapshot();
            using (var dialog = new SaveFileDialog
            {
                Title = "Save round report",
                Filter = "HTML page (*.html)|*.html|CSV for Excel (*.csv)|*.csv",
                FileName = $"wugrid-round-{snap.Now:yyyyMMdd-HHmm}.html",
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var csv = dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
                    File.WriteAllText(dialog.FileName, csv ? RoundReport.Csv(snap) : RoundReport.Html(snap), new UTF8Encoding(true));
                    if (!csv) Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not save the report:\n{ex.Message}", "WUGrid", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        void BuildGrid()
        {
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.BorderStyle = BorderStyle.None;
            grid.BackgroundColor = Color.White;
            grid.GridColor = Palette.GridLine;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.RowTemplate.Height = Px(28);
            grid.DefaultCellStyle.ForeColor = Palette.Text;
            grid.DefaultCellStyle.Padding = new Padding(Px(6), 0, Px(6), 0);

            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = Px(32);
            var head = grid.ColumnHeadersDefaultCellStyle;
            head.BackColor = head.SelectionBackColor = Palette.Header;
            head.ForeColor = head.SelectionForeColor = Palette.MutedText;
            head.Font = new Font("Segoe UI Semibold", 9f);
            head.Padding = new Padding(Px(6), 0, Px(6), 0);

            AddColumn("Server", "Server", 14);
            AddColumn("Ping", "Ping", 6);
            AddColumn("State", "State", 14);
            AddColumn("LastBoot", "Last boot", 12);
            AddColumn("Elapsed", "Up / Down", 9);
            AddColumn("TiWorker", "TiWorker", 8);
            AddColumn("Build", "Build", 14);
            AddColumn("Note", "Note", 31);
            grid.Columns["Server"].DefaultCellStyle.Font = new Font("Segoe UI Semibold", 9.75f);
            var state = grid.Columns["State"].DefaultCellStyle;
            state.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            state.Alignment = DataGridViewContentAlignment.MiddleCenter;

            // Double-click a row for the server's details: update results, restarts, services, errors explained
            grid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0) ShowDetails((string)grid.Rows[e.RowIndex].Cells["Server"].Value);
            };
        }

        void AddColumn(string name, string header, float weight)
        {
            var i = grid.Columns.Add(name, header);
            grid.Columns[i].FillWeight = weight;
            grid.Columns[i].MinimumWidth = Px(40);
            grid.Columns[i].SortMode = DataGridViewColumnSortMode.Automatic;
        }

        void BuildEvents()
        {
            events.Dock = DockStyle.Fill;
            events.View = View.Details;
            events.FullRowSelect = true;
            events.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            events.BorderStyle = BorderStyle.None;
            events.Columns.Add("Time", Px(80));
            events.Columns.Add("Server", Px(110));
            events.Columns.Add("Event", Px(600));
            events.Resize += (s, e) => events.Columns[2].Width = Math.Max(Px(200), events.ClientSize.Width - Px(190));
        }

        void ShowRound(RoundSnapshot snap)
        {
            lastSnapshot = snap;
            foreach (var r in snap.Rows) UpdateRow(r);
            foreach (var gone in rows.Keys.Except(snap.Rows.Select(r => r.Name), StringComparer.OrdinalIgnoreCase).ToList())
            {
                grid.Rows.Remove(rows[gone]);
                rows.Remove(gone);
            }
            // Stays once shown: the event pane keeps the asterisked lines, and a footnote that comes and goes jolts the grid
            if (!footnote.Visible && snap.Rows.Any(r => r.Status.LikelyTransient)) footnote.Visible = true;
            UpdateHeader(snap);
            // First states are what the grid already shows
            foreach (var e in tracker.Update(snap)) if (e.Kind != RoundEventKind.Seen) AddEvent(e);
            status.Text = demo != null ? "Demo round: simulated, no servers are contacted."
                        : snap.Rows.Count == 0 ? "No servers yet: click Servers… to add some."
                        : $"{snap.Rows.Count} servers, polled every {live.Interval.TotalSeconds:0} s over ICMP and DCOM/WMI. Double-click a row for its update results, restarts and services.";
        }

        void UpdateRow(ServerRow r)
        {
            if (!rows.TryGetValue(r.Name, out var row))
            {
                row = grid.Rows[grid.Rows.Add()];
                row.Cells["Server"].Value = r.Name;
                rows[r.Name] = row;
            }
            var o = r.Observation;
            var s = r.Status;
            Set(row, "Ping", o.Ping == true ? "up" : o.Ping == false ? "--" : "");
            Set(row, "State", s.Label);
            if (o.LastBoot.HasValue) Set(row, "LastBoot", o.LastBoot.Value.ToString("dd-MM HH:mm"));   // keep the last known while down
            Set(row, "Elapsed", s.Elapsed.HasValue ? Durations.Format(s.Elapsed.Value) : "");
            Set(row, "TiWorker", o.TiWorker && o.Ping == true ? "yes" : "");
            Set(row, "Build", RoundReport.ShortBuildChange(o));
            Set(row, "Note", s.Note);

            // An error on the row (WMI while booting, or a failed update): its explanation goes in the Note tooltip
            var failed = o.Verification != null && o.Verification.Boot == o.LastBoot ? o.Verification.Failed.FirstOrDefault() : null;
            var code = s.State == ServerState.Booting ? o.WmiHResult : failed?.ErrorCode ?? 0;
            if (!(row.Tag is int known) || known != code)
            {
                row.Tag = code;
                row.Cells["Note"].ToolTipText = code == 0 ? "" : Explain(code);
            }

            var (strong, tint) = Palette.For(s);
            if (row.DefaultCellStyle.BackColor != tint)
            {
                row.DefaultCellStyle.BackColor = tint;
                row.DefaultCellStyle.SelectionBackColor = Palette.Darker(tint);
                row.DefaultCellStyle.SelectionForeColor = Palette.Text;
                var stateCell = row.Cells["State"].Style;   // cell style, or the row's text colour would win over the column's
                stateCell.ForeColor = stateCell.SelectionForeColor = Color.White;
                stateCell.BackColor = strong;
                stateCell.SelectionBackColor = Palette.Darker(strong, 0.15f);
                row.DefaultCellStyle.ForeColor = s.State == ServerState.Checking ? Palette.MutedText : Palette.Text;
            }
        }

        static string Explain(int code)
        {
            var e = ErrorCatalog.Lookup(code)[0];
            return $"{ErrorCatalog.Summary(code)}\n{e.Description}" + (e.Mitigation == "" ? "" : $"\nWhat to do: {e.Mitigation}");
        }

        static void Set(DataGridViewRow row, string column, string value)
        {
            var cell = row.Cells[column];
            if (!Equals(cell.Value as string ?? "", value)) cell.Value = value;
        }

        void UpdateHeader(RoundSnapshot snap)
        {
            // Short chip names so eight fit next to the clock; the tooltip says what each counts
            var counts = new (string Label, int Count, Color Strong, string Tip)[]
            {
                ("BACK", snap.Rows.Count(r => r.Status.IsClean), Palette.BackClean.Strong, "Restarted after the cut-off, nothing pending, checks clean"),
                ("FLAGGED", snap.Rows.Count(r => r.Status.State == ServerState.Back && !r.Status.IsClean), Palette.BackFlagged.Strong,
                    "Restarted, but a restart is pending again, an update failed or a service is not running"),
                ("BOOTING", snap.Rows.Count(r => r.Status.State == ServerState.Booting), Palette.Booting.Strong, "Answers ping, WMI not ready yet"),
                ("DOWN", snap.Rows.Count(r => r.Status.State == ServerState.Down), Palette.Down.Strong, "No ping"),
                ("INSTALLING", snap.Rows.Count(r => r.Status.State == ServerState.Installing), Palette.Installing.Strong,
                    "Not restarted yet, TiWorker running: updates being installed, a restart may be under way"),
                ("PENDING", snap.Rows.Count(r => r.Status.State == ServerState.RestartPending), Palette.RestartPending.Strong,
                    "Not restarted yet, a restart is pending (CBS packages or Windows Update)"),
                ("NOT PENDING", snap.Rows.Count(r => r.Status.State == ServerState.NoRestartPending), Palette.NoRestartPending.Strong,
                    "Not restarted yet, no restart pending (it can still be restarted)"),
                ("checking", snap.Rows.Count(r => r.Status.State == ServerState.Checking), Palette.Checking.Strong, "No answer yet"),
            };
            if (chips.Controls.Count == 0)
                foreach (var c in counts)
                {
                    var chip = new Label
                    {
                        AutoSize = true,
                        Font = new Font("Segoe UI Semibold", 10f),
                        Padding = new Padding(Px(7), Px(5), Px(7), Px(5)),
                        Margin = new Padding(0, 0, Px(6), 0),
                    };
                    chipTips.SetToolTip(chip, c.Tip);
                    chips.Controls.Add(chip);
                }
            for (var i = 0; i < counts.Length; i++)
            {
                var chip = (Label)chips.Controls[i];
                var (label, count, strong, _) = counts[i];
                chip.Text = $"{count}  {label}";
                chip.BackColor = count > 0 ? strong : Palette.Header;
                chip.ForeColor = count > 0 ? Color.White : Palette.MutedText;
            }
            clock.Text = $"{snap.Now:HH:mm:ss}    since {snap.Since:dd-MM HH:mm}    {snap.Rows.Count} servers";
        }

        void AddEvent(RoundEvent e)
        {
            var item = new ListViewItem(new[] { e.Time.ToString("HH:mm:ss"), e.Server ?? "", e.Text });
            if (e.Kind != RoundEventKind.Transition)
            {
                item.ForeColor = e.Kind == RoundEventKind.Alarm ? Palette.AlarmText : e.Kind == RoundEventKind.Action ? Palette.ActionText : Palette.CleanText;
                item.Font = boldEvent ?? (boldEvent = new Font(events.Font, FontStyle.Bold));
            }
            events.Items.Insert(0, item);   // newest on top
            if (events.Items.Count > MaxEvents) events.Items.RemoveAt(events.Items.Count - 1);
        }

        void ResetView()
        {
            tracker = new RoundTracker();
            footnote.Visible = false;
            events.Items.Clear();
            grid.Rows.Clear();
            rows.Clear();
        }
    }

    sealed class BufferedGrid : DataGridView
    {
        public BufferedGrid() { DoubleBuffered = true; }
    }
}
