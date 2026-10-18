using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WUGrid.Core;
using WUGrid.Core.Errors;
using WUGrid.Core.Restart;

namespace WUGrid.Gui
{
    /// <summary>
    /// Pick servers to restart. Only servers whose last poll shows no update work can be ticked; the list follows the grid
    /// while open, and a server that starts update work is unticked. Restarts are never forced, are confirmed first, are
    /// checked again on each server just before they are sent, and go to the audit log.
    /// </summary>
    sealed class RestartForm : Form
    {
        static readonly SemaphoreSlim Throttle = new SemaphoreSlim(8);
        static readonly int[] Warnings = { 0, 30, 60, 120, 300 };

        readonly Func<RoundSnapshot> snapshot;
        readonly Action<RestartResult, RestartOptions> report;
        readonly bool preview;
        readonly AuditLog audit = new AuditLog(AuditLog.DefaultPath);
        readonly float scale;
        readonly ListView list = new ListView();
        readonly ComboBox warning = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly TextBox comment = new TextBox();
        readonly Button checkOnly = new Button { Text = "Check only", AutoSize = true };
        readonly Button restart = new Button { Text = "Restart…", AutoSize = true };
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 2000 };
        readonly Dictionary<string, RestartEligibility> eligible = new Dictionary<string, RestartEligibility>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int running;

        /// <param name="report">Told every result, on the UI thread (the main window logs it in the event pane).</param>
        /// <param name="preview">Shows the list and the reasons but never sends or records anything (the screenshots).</param>
        /// <param name="tick">A server to tick on opening (the grid's right-click), if it can be restarted now.</param>
        public RestartForm(Func<RoundSnapshot> snapshot, Action<RestartResult, RestartOptions> report, bool preview = false, string tick = null)
        {
            this.snapshot = snapshot;
            this.report = report;
            this.preview = preview;
            scale = DeviceDpi / 96f;
            Text = "Reboot servers";
            Icon = AppInfo.Icon;
            Font = new Font("Segoe UI", 9.75f);
            ClientSize = new Size(Px(980), Px(600));
            MinimumSize = new Size(Px(720), Px(440));
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;

            var hint = new Label
            {
                Dock = DockStyle.Top, Height = Px(44), Padding = new Padding(Px(12), Px(10), Px(12), 0), ForeColor = Palette.MutedText,
                Text = "Tick the servers to restart. Greyed ones cannot be restarted now without cutting off update work; the reason is shown.",
            };

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.CheckBoxes = true;
            list.FullRowSelect = true;
            list.BorderStyle = BorderStyle.None;
            list.Columns.Add("Server", Px(125));
            list.Columns.Add("State", Px(155));
            list.Columns.Add("Can restart now?", Px(420));
            list.Columns.Add("Result", Px(240));
            list.ItemCheck += (s, e) =>
            {
                // Greyed servers cannot be ticked
                var name = list.Items[e.Index].Text;
                if (e.NewValue == CheckState.Checked && !(eligible.TryGetValue(name, out var el) && el.Allowed)) e.NewValue = CheckState.Unchecked;
            };
            list.ItemChecked += (s, e) => UpdateButtons();
            var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Px(10), 0, Px(10), 0) };
            listHost.Controls.Add(list);

            Controls.Add(listHost);
            Controls.Add(BuildOptions());
            Controls.Add(hint);
            Controls.Add(BuildButtons());

            timer.Tick += (s, e) => RefreshList();
            Load += (s, e) =>
            {
                RefreshList();
                if (tick != null)
                {
                    Tick(name => string.Equals(name, tick, StringComparison.OrdinalIgnoreCase));
                    list.Items[tick]?.EnsureVisible();
                }
                timer.Start();
            };
            FormClosed += (s, e) => timer.Stop();
        }

        int Px(int dip) => (int)Math.Round(dip * scale);

        Control BuildOptions()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = Px(96), Padding = new Padding(Px(12), Px(8), Px(12), 0), ColumnCount = 4 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var w in Warnings) warning.Items.Add(w == 0 ? "none" : w < 120 ? $"{w} s" : $"{w / 60} min");
            warning.SelectedIndex = Array.IndexOf(Warnings, 60);
            warning.Width = Px(80);
            comment.Dock = DockStyle.Fill;
            panel.Controls.Add(new Label { Text = "Warning before restart", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            panel.Controls.Add(warning, 1, 0);
            panel.Controls.Add(new Label { Text = "Comment", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(Px(16), 0, 0, 0) }, 2, 0);
            panel.Controls.Add(comment, 3, 0);
            var note = new Label
            {
                AutoSize = true, ForeColor = Palette.MutedText, MaximumSize = new Size(Px(940), 0),
                Text = "Not forced: running programs can delay or cancel the restart, and Windows refuses it while a user is signed in, even " +
                       "disconnected. Until it runs it can be aborted on the server with shutdown /a. Each server is checked again just before: one " +
                       $"that started update work or has a user signed in is skipped. Everything is recorded in {(preview ? @"%APPDATA%\WUGrid\audit.jsonl" : audit.Path)}.",
            };
            panel.Controls.Add(note, 0, 1);
            panel.SetColumnSpan(note, 4);
            return panel;
        }

        Control BuildButtons()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = Px(52), Padding = new Padding(Px(8), Px(8), Px(8), Px(6)), ColumnCount = 7 };
            for (var i = 0; i < 7; i++) bar.ColumnStyles.Add(new ColumnStyle(i == 3 ? SizeType.Percent : SizeType.AutoSize, 100));
            var withPending = new Button { Text = "Tick all with a restart pending", AutoSize = true };
            var all = new Button { Text = "Tick all possible", AutoSize = true };
            var none = new Button { Text = "Clear", AutoSize = true };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(Px(80), 0) };
            withPending.Click += (s, e) => Tick(name => pending.Contains(name));
            all.Click += (s, e) => Tick(name => true);
            none.Click += (s, e) => { foreach (ListViewItem i in list.Items) i.Checked = false; };
            checkOnly.Click += (s, e) => Run(checkOnlyRun: true);
            restart.Click += (s, e) => Run(checkOnlyRun: false);
            restart.Font = new Font(Font, FontStyle.Bold);
            CancelButton = close;
            bar.Controls.AddRange(new Control[] { withPending, all, none, new Label(), checkOnly, restart, close });
            return bar;
        }

        // Only servers that can be restarted now, and not one already sent a restart from this window
        void Tick(Func<string, bool> which)
        {
            foreach (ListViewItem i in list.Items)
                i.Checked = which(i.Text) && eligible.TryGetValue(i.Text, out var el) && el.Allowed && !requested.Contains(i.Text);
        }

        void RefreshList()
        {
            var snap = snapshot();
            if (snap == null) return;
            list.BeginUpdate();
            foreach (var row in snap.Rows)
            {
                var item = list.Items[row.Name] ?? list.Items.Add(new ListViewItem(new[] { row.Name, "", "", "" }) { Name = row.Name, UseItemStyleForSubItems = false });
                var el = RestartGuard.Check(row.Observation, row.Status);
                eligible[row.Name] = el;
                item.SubItems[1].Text = row.Status.Label;
                if (RestartGuard.IsPending(row.Status)) pending.Add(row.Name); else pending.Remove(row.Name);
                item.SubItems[2].Text = el.Allowed ? (el.Reason == "" ? "yes" : "yes — " + el.Reason) : "no — " + el.Reason;
                var colour = el.Allowed ? Palette.Text : Palette.MutedText;
                item.ForeColor = item.SubItems[1].ForeColor = colour;
                item.SubItems[2].ForeColor = el.Allowed ? Palette.CleanText : Palette.MutedText;
                if (!el.Allowed && item.Checked)
                {
                    item.Checked = false;
                    item.SubItems[3].Text = "unticked: " + el.Reason;
                }
            }
            foreach (var gone in list.Items.Cast<ListViewItem>().Where(i => snap.Rows.All(r => !string.Equals(r.Name, i.Text, StringComparison.OrdinalIgnoreCase))).ToList())
                list.Items.Remove(gone);
            list.EndUpdate();
            UpdateButtons();
        }

        void UpdateButtons()
        {
            var n = list.CheckedItems.Count;
            restart.Text = n == 1 ? "Restart 1 server…" : $"Restart {n} servers…";
            restart.Enabled = checkOnly.Enabled = n > 0 && running == 0;
        }

        void Run(bool checkOnlyRun)
        {
            var names = list.CheckedItems.Cast<ListViewItem>().Select(i => i.Text).ToList();
            if (names.Count == 0 || preview) return;
            var options = new RestartOptions { WarningSeconds = Warnings[warning.SelectedIndex], Comment = comment.Text, CheckOnly = checkOnlyRun };
            if (!checkOnlyRun)
            {
                var text = $"Restart {(names.Count == 1 ? "this server" : $"these {names.Count} servers")} now?\n\n{string.Join(", ", names)}\n\n" +
                           $"Not forced; {(options.WarningSeconds > 0 ? $"{options.WarningSeconds} s warning" : "no warning")}. " +
                           "Each server is checked again just before; one that started update work or has a user signed in is skipped.";
                if (MessageBox.Show(this, text, "Reboot servers", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
            }
            var since = snapshot()?.Since ?? DateTime.Today;
            foreach (var name in names) Result(name, checkOnlyRun ? "checking…" : "sending…", Palette.MutedText);
            running += names.Count;
            UpdateButtons();
            foreach (var name in names)
                Task.Run(async () =>
                {
                    await Throttle.WaitAsync();
                    RestartResult result;
                    try { result = RestartAction.Execute(name, since, options); }
                    finally { Throttle.Release(); }
                    try { audit.Restart(result, options, DateTime.Now); }
                    catch (Exception ex)
                    {
                        ActivityLog.Error(name, $"audit log not written ({audit.Path})", ex);
                        result = new RestartResult(name, result.Outcome, result.Detail + $" (audit log not written: {ex.Message})", result.Code);
                    }
                    if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)(() => Done(result, options)));
                    else report(result, options);
                });
        }

        void Done(RestartResult r, RestartOptions options)
        {
            running--;
            switch (r.Outcome)
            {
                case RestartOutcome.Requested:
                    requested.Add(r.Server);
                    Result(r.Server, $"requested {DateTime.Now:HH:mm:ss}: {r.Detail}", Palette.CleanText);
                    var item = list.Items[r.Server];
                    if (item != null) item.Checked = false;   // not twice
                    break;
                case RestartOutcome.WouldRestart: Result(r.Server, "would restart: " + r.Detail, Palette.CleanText); break;
                case RestartOutcome.Skipped: Result(r.Server, "skipped: " + r.Detail, Palette.AlarmText); break;
                default: Result(r.Server, $"failed: {r.Detail}" + (r.Code != 0 ? $" ({ErrorCatalog.Summary(r.Code)})" : ""), Palette.AlarmText); break;
            }
            report(r, options);
            UpdateButtons();
        }

        void Result(string server, string text, Color colour)
        {
            var item = list.Items[server];
            if (item == null) return;
            item.SubItems[3].Text = text;
            item.SubItems[3].ForeColor = colour;
        }
    }
}
