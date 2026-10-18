using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WUGrid.Core;
using WUGrid.Core.Demo;
using WUGrid.Core.Reports;

namespace WUGrid.Gui
{
    /// <summary>
    /// <c>WUGrid.exe --demo --screenshots DIR</c>: plays the demo round to set points, opens the windows the project page
    /// shows, saves each as a PNG, writes the end-of-round report as HTML, and exits. Demo only, so no server is contacted;
    /// the windows sit off-screen, and the Reboot window opens in preview, which sends and records nothing.
    /// tools/New-Screenshots.ps1 runs it.
    /// </summary>
    sealed partial class MainForm
    {
        static readonly Point OffScreen = new Point(-12000, -12000);

        public void TakeScreenshotsWhenShown(string dir)
        {
            StartPosition = FormStartPosition.Manual;
            Location = OffScreen;
            Shown += (s, e) => BeginInvoke((Action)(() =>
            {
                try { TakeScreenshots(dir); }
                catch (Exception ex) { MessageBox.Show($"Screenshots failed:\n{ex}", "WUGrid", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                Close();
            }));
        }

        void TakeScreenshots(string dir)
        {
            timer.Stop();
            demo.Paused = true;
            Directory.CreateDirectory(dir);
            ResetView();

            // Early: half the round installing, down or booting
            var early = TimeSpan.FromMinutes(12);
            Play(TimeSpan.Zero, early);
            SaveWindow(this, Path.Combine(dir, "grid-early.png"));

            // Mid-round: every kind of state at once, a server booting with the asterisked error, alarms in the event pane
            var mid = TimeSpan.FromMinutes(26.5);
            Play(early, mid);
            SaveWindow(this, Path.Combine(dir, "grid.png"));

            using (var reboot = new RestartForm(() => lastSnapshot, (r, o) => { }, preview: true))
            {
                Open(reboot);
                Find<Button>(reboot, b => b.Text == "Tick all with a restart pending")?.PerformClick();
                SaveWindow(reboot, Path.Combine(dir, "reboot.png"));
            }

            // Later: SQL01 is back at the old build with its cumulative update failed
            Play(mid, TimeSpan.FromMinutes(40));
            using (var details = new ServerDetailsForm("SQL01", n => lastSnapshot?.Rows.FirstOrDefault(r => r.Name == n), null))
            {
                Open(details);
                SaveWindow(details, Path.Combine(dir, "details.png"));
            }
            using (var errors = new ErrorLookupForm())
            {
                Open(errors);
                var query = Find<TextBox>(errors, t => !t.ReadOnly);
                query.Text = "corrupt";
                query.Select(query.Text.Length, 0);
                var results = Find<ListView>(errors, l => true);
                var hit = results.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Text == "0x800F0840");
                if (hit != null)
                {
                    results.SelectedItems.Clear();
                    hit.Selected = true;
                    hit.EnsureVisible();
                }
                SaveWindow(errors, Path.Combine(dir, "errors.png"));
            }

            File.WriteAllText(Path.Combine(dir, "report.html"), RoundReport.Html(demo.At(DemoRound.Length), "WUGrid round report (demo round)"),
                              new UTF8Encoding(true));
        }

        // Feeds the grid and the tracker every 10 s of round time, as the timer would have
        void Play(TimeSpan from, TimeSpan to)
        {
            for (var t = from; t < to; t += TimeSpan.FromSeconds(10)) ShowRound(demo.At(t));
            ShowRound(demo.At(to));
            demo.Seek(to);
            grid.ClearSelection();
        }

        void Open(Form f)
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = OffScreen;
            f.Show(this);
            Settle(f);
        }

        static void Settle(Form f)
        {
            for (var i = 0; i < 5; i++)
            {
                Application.DoEvents();
                Thread.Sleep(60);
            }
            f.Refresh();
        }

        static T Find<T>(Control root, Func<T, bool> which) where T : Control =>
            root.Controls.Cast<Control>().Select(c => c as T is T t && which(t) ? t : Find(c, which)).FirstOrDefault(c => c != null);

        /// <summary>The window as the compositor shows it, title bar included, cropped to its visible frame, square corners.</summary>
        static void SaveWindow(Form f, string path)
        {
            var noRound = DwmDoNotRound;
            DwmSetWindowAttribute(f.Handle, DwmwaWindowCornerPreference, ref noRound, sizeof(int));
            Settle(f);
            GetWindowRect(f.Handle, out var window);
            var frame = DwmGetWindowAttribute(f.Handle, DwmwaExtendedFrameBounds, out var bounds, Marshal.SizeOf(typeof(Rect))) == 0 ? bounds : window;
            using (var full = new Bitmap(window.Right - window.Left, window.Bottom - window.Top, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(full))
                {
                    var hdc = g.GetHdc();
                    try { PrintWindow(f.Handle, hdc, PwRenderFullContent); }
                    finally { g.ReleaseHdc(hdc); }
                }
                var crop = new Rectangle(frame.Left - window.Left, frame.Top - window.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
                using (var shot = full.Clone(crop, PixelFormat.Format24bppRgb)) shot.Save(path, ImageFormat.Png);
            }
        }

        const uint PwRenderFullContent = 2;
        const int DwmwaExtendedFrameBounds = 9;
        const int DwmwaWindowCornerPreference = 33;
        const int DwmDoNotRound = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
