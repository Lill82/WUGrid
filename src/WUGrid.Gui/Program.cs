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
        /// WUGrid.exe --log [--log-dir DIR]  keep an activity log this run (default %APPDATA%\WUGrid\logs; the status bar
        ///                                  turns it on and off and remembers the choice)
        /// WUGrid.exe --lang es             windows in Spanish this run (en, es, ca; default: the status bar's choice,
        ///                                  else Catalan)
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ActivityLog.CatchUnhandled();
            Application.ThreadException += (s, e) => Unexpected(e.Exception);
            commandLine = string.Join(" ", args);
            // Before anything is shown; the project page's screenshots are in the --lang asked for, English without one
            var lang = Array.FindIndex(args, a => a.Equals("--lang", StringComparison.OrdinalIgnoreCase)) is var li && li >= 0 && li + 1 < args.Length ? args[li + 1] : null;
            L.Start(lang ?? (Array.Exists(args, a => a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase)) ? "en" : null));

            var demo = false;
            var log = Settings.IsOn(Settings.Get(ActivityLog.SettingName));
            string screenshots = null;
            var since = DateTime.Today;
            var listPath = ServerList.DefaultPath;
            var names = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.Equals("--demo", StringComparison.OrdinalIgnoreCase)) demo = true;
                else if (a.Equals("--lang", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) i++;   // read above
                else if (a.Equals("--log", StringComparison.OrdinalIgnoreCase)) log = true;
                else if (a.Equals("--log-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    LogDirectory = Path.GetFullPath(args[++i]);
                    log = true;
                }
                else if (a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) screenshots = Path.GetFullPath(args[++i]);
                else if (a.Equals("--list", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) listPath = Path.GetFullPath(args[++i]);
                else if (a.Equals("--since", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (!DateTime.TryParse(args[++i], CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out since))
                    {
                        MessageBox.Show(L.F("--since: not a date/time: {0}", args[i]), "WUGrid", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
                else names.AddRange(ServerList.Parse(a));
            }
            if (log) StartLog();

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
                MessageBox.Show(L.T("--screenshots works with --demo only: it never shows real servers."), "WUGrid", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            using (var round = new LiveRound(since, TimeSpan.FromSeconds(5)))
            {
                round.SetServers(names.Count > 0 ? names : ServerList.Load(listPath));
                try { round.Expected = ExpectedList.Load(ExpectedList.DefaultPath); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    ActivityLog.Warn(null, $"expected stops not read from {ExpectedList.DefaultPath}", ex);   // the window says so when it cannot save
                }
                // Names on the command line are a one-off: they are not written over the saved list
                Application.Run(new MainForm(round, names.Count > 0 ? null : listPath));
            }
            ActivityLog.Info(null, "--- WUGrid closed");
        }

        static string commandLine;

        /// <summary>Where the activity log goes: --log-dir, or %APPDATA%\WUGrid\logs.</summary>
        public static string LogDirectory { get; private set; } = ActivityLog.DefaultDirectory;

        public static void StartLog() =>
            ActivityLog.Start(LogDirectory, "WUGrid", $"WUGrid {AppInfo.Version}" + (commandLine != "" ? $" {commandLine}" : ""));

        // An exception on the UI thread: say so and keep the window open, rather than the bare .NET dialog
        static void Unexpected(Exception ex)
        {
            ActivityLog.Error(null, "unexpected error in the window", ex);
            var log = ActivityLog.Current;
            MessageBox.Show(L.F("Something went wrong in WUGrid:\n{0}", ex.Message) + "\n\n" +
                            (log != null ? L.F("The details are in the activity log:\n{0}", log.File) : L.T("Turn on the activity log (status bar) to keep the details if it happens again.")),
                            "WUGrid", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
