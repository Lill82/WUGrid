using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using WUGrid.Core;
using WUGrid.Core.Restart;

namespace WUGrid.Gui
{
    /// <summary>
    /// Mark services the operator expects not to run after the restart — a role being removed, an application retired — so
    /// the server is not flagged for them. The stop stays visible with the reason and who said so. Nothing is sent to any
    /// server. The list is saved to %APPDATA%\WUGrid\expected.txt, which wugrid-cli watch and verify read too.
    /// </summary>
    sealed class ExpectedForm : Form
    {
        readonly float scale;
        readonly ListView stops = new ListView();
        readonly CheckBox everyServer = new CheckBox { Text = "On every server", AutoSize = true };
        readonly TextBox why = new TextBox();
        readonly Button mark = new Button { Text = "Mark ticked as expected", AutoSize = true };
        readonly ListView list = new ListView();
        readonly Label summary = new Label();
        readonly List<ExpectedStop> entries;

        /// <param name="server">Pre-ticks this server's stopped services; null lists the round's without ticking.</param>
        public ExpectedForm(RoundSnapshot snapshot, IEnumerable<ExpectedStop> current, string server = null)
        {
            entries = current.ToList();
            scale = DeviceDpi / 96f;
            Text = "Expected service stops";
            Icon = AppInfo.Icon;
            Font = new Font("Segoe UI", 9.75f);
            ClientSize = new Size(Px(980), Px(640));
            MinimumSize = new Size(Px(760), Px(500));
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;

            Controls.Add(BuildEntries());
            Controls.Add(BuildStops(snapshot, server));
            Controls.Add(BuildButtons());
            RefreshEntries();
            Shown += (s, e) => why.Focus();
        }

        /// <summary>The list as it stands.</summary>
        public IReadOnlyList<ExpectedStop> Entries => entries;

        int Px(int dip) => (int)Math.Round(dip * scale);

        Control BuildStops(RoundSnapshot snapshot, string server)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, Height = Px(310), Padding = new Padding(Px(10), Px(8), Px(10), Px(4)), ColumnCount = 4, RowCount = 3 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var hint = new Label
            {
                AutoSize = true, ForeColor = Palette.MutedText, MaximumSize = new Size(Px(940), 0),
                Text = "Services not running right now. Tick the ones you expect to stay stopped (a role being removed, an application retired), say why " +
                       "and mark them: the server is no longer flagged for them and the stop stays visible with your reason. Nothing is sent to any server.",
            };
            panel.Controls.Add(hint, 0, 0);
            panel.SetColumnSpan(hint, 4);

            stops.Dock = DockStyle.Fill;
            stops.View = View.Details;
            stops.CheckBoxes = true;
            stops.FullRowSelect = true;
            stops.HideSelection = false;
            stops.BorderStyle = BorderStyle.FixedSingle;
            stops.Margin = new Padding(0, Px(6), 0, Px(6));
            stops.Columns.Add("Server", Px(130));
            stops.Columns.Add("Service", Px(220));
            stops.Columns.Add("Display name", Px(300));
            stops.Columns.Add("State", Px(130));
            stops.Columns.Add("Expected?", Px(100));
            foreach (var row in snapshot.Rows)
            {
                var v = row.CurrentVerification;
                if (v == null) continue;
                foreach (var i in v.StoppedServices)
                {
                    var known = ExpectedList.Find(entries, row.Name, i.Service.Name) != null;
                    stops.Items.Add(new ListViewItem(new[] { row.Name, i.Service.Name, i.Service.DisplayName, $"{i.Service.State}, {i.Service.StartMode}", "" })
                    {
                        Tag = new[] { row.Name, i.Service.Name },
                        Checked = !known && server != null && string.Equals(row.Name, server, StringComparison.OrdinalIgnoreCase),
                    });
                }
            }
            if (stops.Items.Count == 0)
                stops.Items.Add(new ListViewItem(new[] { "", "no service is stopped in the round right now", "", "", "" }) { ForeColor = Palette.MutedText });
            panel.Controls.Add(stops, 0, 1);
            panel.SetColumnSpan(stops, 4);

            everyServer.Anchor = AnchorStyles.Left;
            everyServer.Margin = new Padding(0, Px(4), Px(12), 0);
            why.Dock = DockStyle.Fill;
            why.Margin = new Padding(Px(6), Px(2), Px(6), 0);
            why.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; MarkTicked(); } };
            mark.Click += (s, e) => MarkTicked();
            panel.Controls.Add(everyServer, 0, 2);
            panel.Controls.Add(new Label { Text = "Why", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, Px(4), 0, 0) }, 1, 2);
            panel.Controls.Add(why, 2, 2);
            panel.Controls.Add(mark, 3, 2);
            return panel;
        }

        Control BuildEntries()
        {
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.BorderStyle = BorderStyle.None;
            list.Columns.Add("Server", Px(130));
            list.Columns.Add("Service", Px(220));
            list.Columns.Add("Why", Px(580));
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
            var title = new Label { Dock = DockStyle.Top, Height = Px(26), Text = "Expected stops in force (select and Remove, or Delete, to take one back)", ForeColor = Palette.MutedText };
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Px(10), Px(4), Px(10), 0) };
            host.Controls.Add(list);
            host.Controls.Add(title);
            return host;
        }

        Control BuildButtons()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = Px(50), Padding = new Padding(Px(8), Px(8), Px(8), Px(6)), ColumnCount = 4 };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var remove = new Button { Text = "Remove", AutoSize = true };
            remove.Click += (s, e) => RemoveSelected();
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(Px(80), 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(Px(80), 0) };
            summary.AutoSize = true;
            summary.Anchor = AnchorStyles.Left;
            summary.ForeColor = Palette.MutedText;
            summary.Margin = new Padding(Px(12), 0, 0, 0);
            CancelButton = cancel;   // no AcceptButton: Enter in the Why box marks the ticked services
            bar.Controls.AddRange(new Control[] { remove, summary, ok, cancel });
            return bar;
        }

        void MarkTicked()
        {
            var ticked = stops.CheckedItems.Cast<ListViewItem>().Where(i => i.Tag is string[]).ToList();
            if (ticked.Count == 0) return;
            var comment = ExpectedList.Stamp(why.Text, RestartAction.Operator, DateTime.Now);
            var added = everyServer.Checked
                ? ticked.Select(i => ((string[])i.Tag)[1]).Distinct(StringComparer.OrdinalIgnoreCase).Select(svc => new ExpectedStop(null, svc, comment))
                : ticked.Select(i => new ExpectedStop(((string[])i.Tag)[0], ((string[])i.Tag)[1], comment));
            foreach (var e in added.ToList())
                if (!entries.Any(x => ExpectedList.Same(x, e))) entries.Add(e);
            foreach (var i in ticked) i.Checked = false;
            why.Clear();
            RefreshEntries();
        }

        void RemoveSelected()
        {
            foreach (var item in list.SelectedItems.Cast<ListViewItem>().ToList()) entries.Remove((ExpectedStop)item.Tag);
            RefreshEntries();
        }

        void RefreshEntries()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var e in entries.OrderBy(e => e.Server ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Service, StringComparer.OrdinalIgnoreCase))
                list.Items.Add(new ListViewItem(new[] { e.ServerLabel, e.Service, e.Comment }) { Tag = e });
            list.EndUpdate();
            foreach (ListViewItem i in stops.Items)
            {
                if (!(i.Tag is string[] key)) continue;
                var known = ExpectedList.Find(entries, key[0], key[1]) != null;
                i.SubItems[4].Text = known ? "yes" : "";
                i.ForeColor = known ? Palette.MutedText : Palette.Text;
            }
            summary.Text = entries.Count == 1 ? "1 expected stop" : $"{entries.Count} expected stops";
        }
    }
}
