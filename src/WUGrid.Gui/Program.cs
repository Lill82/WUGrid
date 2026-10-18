using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using WUGrid.Core;
using WUGrid.Core.Demo;

namespace WUGrid.Gui
{
    static class Program
    {
        /// <summary>
        /// WUGrid.exe                       live grid, servers from the saved list (%APPDATA%\WUGrid\servers.txt)
        /// WUGrid.exe SRV01 SRV02           live grid with these servers
        /// WUGrid.exe --list servers.txt    live grid with that list file (changes are saved back to it)
        /// WUGrid.exe --since 2026-10-17T15:00   round cut-off (default: today 00:00)
        /// WUGrid.exe --demo                simulated round
        /// WUGrid.exe --demo --screenshots DIR   saves the project page's screenshots of the demo, then exits
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var demo = false;
            string screenshots = null;
            var since = DateTime.Today;
            var listPath = ServerList.DefaultPath;
            var names = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.Equals("--demo", StringComparison.OrdinalIgnoreCase)) demo = true;
                else if (a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) screenshots = Path.GetFullPath(args[++i]);
                else if (a.Equals("--list", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) listPath = Path.GetFullPath(args[++i]);
                else if (a.Equals("--since", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (!DateTime.TryParse(args[++i], CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out since))
                    {
                        MessageBox.Show($"--since: not a date/time: {args[i]}", "WUGrid", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
                else names.AddRange(ServerList.Parse(a));
            }

            if (demo)
            {
                using (var round = new DemoRound())
                {
                    var form = new MainForm(round);
                    if (screenshots != null) form.TakeScreenshotsWhenShown(screenshots);
                    Application.Run(form);
                }
                return;
            }
            if (screenshots != null)
            {
                MessageBox.Show("--screenshots works with --demo only: it never shows real servers.", "WUGrid", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            using (var round = new LiveRound(since, TimeSpan.FromSeconds(5)))
            {
                round.SetServers(names.Count > 0 ? names : ServerList.Load(listPath));
                try { round.Expected = ExpectedList.Load(ExpectedList.DefaultPath); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }   // the window says so when it cannot save
                // Names on the command line are a one-off: they are not written over the saved list
                Application.Run(new MainForm(round, names.Count > 0 ? null : listPath));
            }
        }
    }
}
