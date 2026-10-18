using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using WUGrid.Core.Errors;

namespace WUGrid.Gui
{
    /// <summary>Browse and search the error catalog: type a code (0x800F0840, -2146498496…) or words.</summary>
    sealed class ErrorLookupForm : Form
    {
        const int MaxShown = 500;
        static readonly string[] FamilyOrder = { "CBS", "Windows Update", "Remote access", "Win32", "WMI" };

        readonly float scale;
        readonly TextBox query = new TextBox();
        readonly ListView results = new ListView();
        readonly TextBox details = new TextBox();
        readonly FlowLayoutPanel links = new FlowLayoutPanel();

        public ErrorLookupForm(int? code = null)
        {
            scale = DeviceDpi / 96f;
            Text = L.T("Error codes");
            Icon = AppInfo.Icon;
            Font = new Font("Segoe UI", 9.75f);
            ClientSize = new Size(Px(900), Px(620));
            MinimumSize = new Size(Px(640), Px(420));
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;

            var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = Px(64), Padding = new Padding(Px(10), Px(8), Px(10), Px(4)), ColumnCount = 1 };
            top.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = Palette.MutedText,
                Text = L.F("Code (0x800F0840, 800F0840, -2146498496) or words. {0} CBS, Windows Update, WMI and Win32 codes.", ErrorCatalog.All.Count),
            });
            query.Dock = DockStyle.Fill;
            query.TextChanged += (s, e) => Search();
            top.Controls.Add(query);

            results.Dock = DockStyle.Fill;
            results.View = View.Details;
            results.FullRowSelect = true;
            results.HideSelection = false;
            results.BorderStyle = BorderStyle.None;
            results.Columns.Add(L.T("Code"), Px(100));
            results.Columns.Add(L.T("Name"), Px(320));
            results.Columns.Add(L.T("Family"), Px(120));
            results.Columns.Add(L.T("Description"), Px(600));
            results.SelectedIndexChanged += (s, e) => ShowDetails();
            var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Px(10), Px(4), Px(10), 0) };
            listHost.Controls.Add(results);

            details.Dock = DockStyle.Fill;
            details.Multiline = true;
            details.ReadOnly = true;
            details.ScrollBars = ScrollBars.Vertical;
            details.BackColor = Color.White;
            links.Dock = DockStyle.Bottom;
            links.AutoSize = true;
            var detailHost = new Panel { Dock = DockStyle.Bottom, Height = Px(210), Padding = new Padding(Px(10), Px(6), Px(10), Px(8)) };
            detailHost.Controls.Add(details);
            detailHost.Controls.Add(links);

            Controls.Add(listHost);
            Controls.Add(detailHost);
            Controls.Add(top);

            query.Text = code.HasValue ? ErrorCatalog.Hex(code.Value) : "";
            if (!code.HasValue) Search();
            Shown += (s, e) => { query.Focus(); query.SelectAll(); };
        }

        int Px(int dip) => (int)Math.Round(dip * scale);

        void Search()
        {
            var text = query.Text.Trim();
            var found = ErrorCatalog.TryParse(text, out var code) && (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.Length >= 8)
                ? ErrorCatalog.Lookup(code)
                : ErrorCatalog.All.Where(e => text == "" || e.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
                                                       || e.Description.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
                                                       || e.Hex.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                                  .OrderBy(e => Array.IndexOf(FamilyOrder, e.Family) is var i && i >= 0 ? i : FamilyOrder.Length)
                                  .ThenBy(e => unchecked((uint)e.Code)).Take(MaxShown).ToList();
            results.BeginUpdate();
            results.Items.Clear();
            foreach (var e in found)
                results.Items.Add(new ListViewItem(new[] { e.Hex, e.Name, e.Family, e.Description }) { Tag = e });
            results.EndUpdate();
            if (results.Items.Count > 0) results.Items[0].Selected = true;
            else ShowDetails();
        }

        void ShowDetails()
        {
            links.Controls.Clear();
            var e = results.SelectedItems.Count == 1 ? (ErrorEntry)results.SelectedItems[0].Tag : null;
            if (e == null) { details.Text = results.Items.Count == 0 ? L.T("No match.") : ""; return; }
            details.Text = $"{e.Hex}   {(e.Name == "" ? L.T("(no name)") : e.Name)}   [{e.Family}]{Environment.NewLine}{Environment.NewLine}{e.Description}"
                           + (e.Mitigation == "" ? "" : Environment.NewLine + Environment.NewLine + L.F("What to do: {0}", e.Mitigation));
            foreach (var key in e.Source.Split(','))
            {
                if (!ErrorCatalog.Sources.TryGetValue(key, out var src)) continue;
                if (src.Url == null) { links.Controls.Add(new Label { Text = L.F("Source: {0}", src.Title), AutoSize = true, ForeColor = Palette.MutedText }); continue; }
                var link = new LinkLabel { Text = L.F("Source: {0}", src.Title), AutoSize = true, Tag = src.Url };
                link.LinkClicked += (s, a) => Process.Start(new ProcessStartInfo((string)((Control)s).Tag) { UseShellExecute = true });
                links.Controls.Add(link);
            }
        }
    }
}
