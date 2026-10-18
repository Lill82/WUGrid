using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WUGrid.Core;
using WUGrid.Core.Errors;
using WUGrid.Core.Probing;

namespace WUGrid.Gui
{
    /// <summary>
    /// Add and remove the servers the grid watches. Every server added gets a one-shot, read-only reachability check
    /// (DNS, ping, WMI over DCOM, admin$); results are kept in the shared cache so reopening the window shows them.
    /// </summary>
    sealed class ServersForm : Form
    {
        static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);
        static readonly SemaphoreSlim Throttle = new SemaphoreSlim(16);

        readonly Dictionary<string, AccessCheckResult> cache;
        readonly float scale;
        readonly TextBox input = new TextBox();
        readonly ListView list = new ListView();
        readonly TextBox details = new TextBox();
        readonly Label summary = new Label();

        public ServersForm(IEnumerable<string> servers, Dictionary<string, AccessCheckResult> cache)
        {
            this.cache = cache;
            scale = DeviceDpi / 96f;
            Text = "Servers";
            Icon = AppInfo.Icon;
            Font = new Font("Segoe UI", 9.75f);
            ClientSize = new Size(Px(980), Px(600));
            MinimumSize = new Size(Px(700), Px(420));
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;

            Controls.Add(BuildList());
            Controls.Add(BuildDetails());
            Controls.Add(BuildInput());
            Controls.Add(BuildButtons());

            foreach (var name in servers) AddRow(name);
            UpdateSummary();
            Shown += (s, e) => input.Focus();
        }

        /// <summary>The list as it stands, in display order.</summary>
        public IReadOnlyList<string> Servers => list.Items.Cast<ListViewItem>().Select(i => i.Text).ToList();

        int Px(int dip) => (int)Math.Round(dip * scale);

        Control BuildInput()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, Height = Px(68), Padding = new Padding(Px(10), Px(8), Px(10), Px(4)), ColumnCount = 3 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var hint = new Label { Text = "Add servers: names separated by commas, spaces or lines (paste a list here). Each one is checked once, read-only.", AutoSize = true, ForeColor = Palette.MutedText };
            panel.Controls.Add(hint, 0, 0);
            panel.SetColumnSpan(hint, 3);
            input.Dock = DockStyle.Fill;
            input.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddFromInput(); } };
            var add = new Button { Text = "Add", AutoSize = true, Margin = new Padding(Px(6), 0, 0, 0) };
            add.Click += (s, e) => AddFromInput();
            var import = new Button { Text = "Import…", AutoSize = true, Margin = new Padding(Px(6), 0, 0, 0) };
            import.Click += (s, e) => Import();
            panel.Controls.Add(input, 0, 1);
            panel.Controls.Add(add, 1, 1);
            panel.Controls.Add(import, 2, 1);
            return panel;
        }

        Control BuildList()
        {
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.BorderStyle = BorderStyle.None;
            list.Columns.Add("Server", Px(150));
            list.Columns.Add("DNS", Px(130));
            list.Columns.Add("Ping", Px(90));
            list.Columns.Add("DCOM / WMI", Px(380));
            list.Columns.Add("admin$", Px(190));
            list.SelectedIndexChanged += (s, e) => ShowDetails();
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Px(10), Px(4), Px(10), 0) };
            host.Controls.Add(list);
            return host;
        }

        Control BuildDetails()
        {
            details.Dock = DockStyle.Fill;
            details.Multiline = true;
            details.ReadOnly = true;
            details.ScrollBars = ScrollBars.Vertical;
            details.BackColor = Color.White;
            details.Font = new Font("Segoe UI", 9f);
            var host = new Panel { Dock = DockStyle.Bottom, Height = Px(150), Padding = new Padding(Px(10), Px(6), Px(10), 0) };
            host.Controls.Add(details);
            return host;
        }

        Control BuildButtons()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = Px(50), Padding = new Padding(Px(8), Px(8), Px(8), Px(6)), ColumnCount = 5 };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var remove = new Button { Text = "Remove", AutoSize = true };
            var recheck = new Button { Text = "Check again", AutoSize = true };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(Px(80), 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(Px(80), 0) };
            remove.Click += (s, e) => RemoveSelected();
            recheck.Click += (s, e) => Recheck();
            summary.AutoSize = true;
            summary.Anchor = AnchorStyles.Left;
            summary.ForeColor = Palette.MutedText;
            summary.Margin = new Padding(Px(12), 0, 0, 0);
            CancelButton = cancel;   // no AcceptButton: Enter in the input box adds servers
            bar.Controls.AddRange(new Control[] { remove, recheck, summary, ok, cancel });
            return bar;
        }

        void AddFromInput()
        {
            foreach (var name in ServerList.Parse(input.Text)) AddRow(name, check: true);
            input.Clear();
            UpdateSummary();
        }

        void Import()
        {
            using (var dialog = new OpenFileDialog { Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*", Title = "Import server list" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (var name in ServerList.Load(dialog.FileName)) AddRow(name, check: true);
                UpdateSummary();
            }
        }

        void AddRow(string name, bool check = false)
        {
            if (list.Items.Cast<ListViewItem>().Any(i => string.Equals(i.Text, name, StringComparison.OrdinalIgnoreCase))) return;
            var item = new ListViewItem(new[] { name, "", "", "", "" }) { UseItemStyleForSubItems = false, Name = name };
            list.Items.Add(item);
            AccessCheckResult known;
            lock (cache) cache.TryGetValue(name, out known);
            if (known != null && !check) Render(item, known);
            else if (check || known == null) StartCheck(name);
        }

        void RemoveSelected()
        {
            foreach (ListViewItem item in list.SelectedItems) list.Items.Remove(item);
            details.Clear();
            UpdateSummary();
        }

        void Recheck()
        {
            var items = list.SelectedItems.Count > 0 ? list.SelectedItems.Cast<ListViewItem>() : list.Items.Cast<ListViewItem>();
            foreach (var item in items.ToList()) StartCheck(item.Text);
        }

        void StartCheck(string name)
        {
            var pending = new AccessCheckResult(name);
            var row = list.Items[name];
            if (row != null) Render(row, pending);
            Task.Run(async () =>
            {
                await Throttle.WaitAsync();
                try
                {
                    var result = AccessCheck.Run(name, CheckTimeout, progress => Post(name, progress));
                    lock (cache) cache[name] = result;
                }
                finally { Throttle.Release(); }
            });
        }

        // Checks finish on worker threads, possibly after the window has closed
        void Post(string name, AccessCheckResult r)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    var row = list.Items[name];
                    if (row == null) return;
                    Render(row, r);
                    if (row.Selected) ShowDetails();
                    UpdateSummary();
                }));
            }
            catch (InvalidOperationException) { }   // closed in between
        }

        void Render(ListViewItem row, AccessCheckResult r)
        {
            Cell(row.SubItems[1], r.Dns);
            Cell(row.SubItems[2], r.Ping);
            Cell(row.SubItems[3], r.Wmi);
            Cell(row.SubItems[4], r.AdminShare);
            row.Tag = r;
        }

        static void Cell(ListViewItem.ListViewSubItem cell, CheckOutcome c)
        {
            switch (c.State)
            {
                case CheckStatus.Ok: cell.Text = "✓ " + c.Detail; cell.ForeColor = Palette.CleanText; break;
                case CheckStatus.Failed: cell.Text = "✗ " + c.Detail; cell.ForeColor = Palette.AlarmText; break;
                case CheckStatus.Skipped: cell.Text = "–"; cell.ForeColor = Palette.MutedText; break;
                default: cell.Text = "checking…"; cell.ForeColor = Palette.MutedText; break;
            }
        }

        void ShowDetails()
        {
            var r = list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as AccessCheckResult : null;
            if (r == null) { details.Clear(); return; }
            var sb = new StringBuilder();
            sb.Append(r.Server).Append(r.Done ? $"   checked {r.CheckedAt:HH:mm:ss}" : "   checking…").AppendLine();
            Describe(sb, "DNS", r.Dns);
            Describe(sb, "Ping", r.Ping);
            Describe(sb, "DCOM / WMI", r.Wmi);
            Describe(sb, "admin$", r.AdminShare);
            if (r.Ping.State == CheckStatus.Failed && r.Wmi.State == CheckStatus.Ok)
                sb.AppendLine("Ping is blocked but WMI answers: the grid uses ping for up/down, so this server will show as DOWN.");
            details.Text = sb.ToString();
        }

        static void Describe(StringBuilder sb, string label, CheckOutcome c)
        {
            sb.Append($"{label}: ").Append(c.State == CheckStatus.Pending ? "checking…" : c.State.ToString().ToLowerInvariant());
            if (c.Detail != "") sb.Append(" — ").Append(c.Detail);
            sb.AppendLine();
            if (c.State != CheckStatus.Failed || c.HResult == 0) return;
            foreach (var e in ErrorCatalog.Lookup(c.HResult))
            {
                sb.Append("    ").Append(e.Hex).Append(e.Name == "" ? "" : " " + e.Name).Append(": ").AppendLine(e.Description);
                if (e.Mitigation != "") sb.Append("    What to do: ").AppendLine(e.Mitigation);
            }
        }

        void UpdateSummary()
        {
            var results = list.Items.Cast<ListViewItem>().Select(i => i.Tag as AccessCheckResult).ToList();
            var checking = results.Count(r => r == null || !r.Done);
            var problems = results.Count(r => r != null && r.Done && !r.AllOk);
            summary.Text = $"{list.Items.Count} servers" + (checking > 0 ? $" · {checking} checking" : "") + (problems > 0 ? $" · {problems} with problems" : "");
        }
    }
}
