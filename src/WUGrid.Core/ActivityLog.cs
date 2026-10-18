using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace WUGrid.Core
{
    public enum LogLevel { Info, Warn, Error }

    /// <summary>
    /// Optional activity log, for working out afterwards what went wrong: off unless the operator turns it on (GUI status
    /// bar, <c>--log</c>, or the saved setting). One text file per program per day in <see cref="DefaultDirectory"/>, kept
    /// <see cref="KeepDays"/> days. It records what changed — servers added, ping and WMI coming and going, verification
    /// passes, round events, actions — and every error with its stack trace; never one line per poll.
    ///
    /// Logging never fails the caller: a file that cannot be written is reported once through <see cref="Problem"/>.
    /// Local only: nothing in it is sent anywhere.
    /// </summary>
    public sealed class ActivityLog : IDisposable
    {
        public const int KeepDays = 30;

        /// <summary>The saved on/off choice, shared by WUGrid.exe and wugrid-cli.</summary>
        public const string SettingName = "activity-log";

        static ActivityLog current;

        readonly object gate = new object();
        readonly Func<DateTime> clock;
        StreamWriter writer;
        DateTime day;

        ActivityLog(string directory, string program, Func<DateTime> clock)
        {
            Directory = directory;
            Program = program;
            this.clock = clock ?? (() => DateTime.Now);
        }

        public static string DefaultDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WUGrid", "logs");

        /// <summary>The running log, or null when logging is off.</summary>
        public static ActivityLog Current => Volatile.Read(ref current);

        public static bool Enabled => Current != null;

        public string Directory { get; }

        /// <summary>File name prefix: "WUGrid", "wugrid-cli".</summary>
        public string Program { get; }

        /// <summary>The file being written, once the first line is in.</summary>
        public string File { get; private set; }

        /// <summary>Why the log could not be written, or null.</summary>
        public string Problem { get; private set; }

        /// <summary>Turns logging on (replacing a log already running), prunes old files and writes the session header.</summary>
        /// <param name="header">What this session is: version, command line, settings.</param>
        public static ActivityLog Start(string directory, string program, string header, Func<DateTime> clock = null)
        {
            var log = new ActivityLog(directory, program, clock);
            Interlocked.Exchange(ref current, log)?.Dispose();
            log.Prune();
            log.Write(LogLevel.Info, null, $"--- {header} | {Environment.UserDomainName}\\{Environment.UserName} on {Environment.MachineName}, " +
                                           $"{WindowsVersion()}, CLR {Environment.Version}, pid {System.Diagnostics.Process.GetCurrentProcess().Id}", null);
            return log;
        }

        // Environment.OSVersion says 6.2.9200 to a program without a compatibility manifest (wugrid-cli)
        static string WindowsVersion()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    if (key?.GetValue("CurrentBuild") is string build)
                        return $"{key.GetValue("ProductName")} build {build}.{key.GetValue("UBR")}";
            }
            catch (Exception e) when (e is System.Security.SecurityException || e is IOException || e is UnauthorizedAccessException) { }
            return Environment.OSVersion.VersionString;
        }

        /// <summary>Turns logging off.</summary>
        public static void Stop()
        {
            var log = Interlocked.Exchange(ref current, null);
            if (log == null) return;
            log.Write(LogLevel.Info, null, "--- activity log turned off", null);
            log.Dispose();
        }

        /// <param name="server">The server the line is about; null for the program itself.</param>
        public static void Info(string server, string text) => Current?.Write(LogLevel.Info, server, text, null);

        public static void Warn(string server, string text, Exception ex = null) => Current?.Write(LogLevel.Warn, server, text, ex);

        public static void Error(string server, string text, Exception ex = null) => Current?.Write(LogLevel.Error, server, text, ex);

        /// <summary>Unhandled exceptions on any thread go to the log, if it is on, before the process ends.</summary>
        public static void CatchUnhandled()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Error(null, e.IsTerminating ? "unhandled exception: the program stops" : "unhandled exception", e.ExceptionObject as Exception);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => Error(null, "background task failed unobserved", e.Exception);
        }

        public void Write(LogLevel level, string server, string text, Exception ex)
        {
            lock (gate)
            {
                try
                {
                    var now = clock();
                    if (writer == null || now.Date != day) Open(now.Date);
                    writer.Write(Format(now, level, server, text, ex));
                    Problem = null;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ObjectDisposedException)
                {
                    Problem = $"Cannot write the activity log in {Directory}: {e.Message}";
                    Close();
                }
            }
        }

        // A second copy of the program running the same day gets a file of its own rather than interleaved lines
        void Open(DateTime date)
        {
            Close();
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, FileName(Program, date));
            FileStream stream;
            try { stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete); }
            catch (IOException)
            {
                path = Path.Combine(Directory, FileName(Program, date, System.Diagnostics.Process.GetCurrentProcess().Id));
                stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
            }
            // Flushed every line: what matters most is the last thing written before a crash
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            day = date;
            File = path;
        }

        void Close()
        {
            try { writer?.Dispose(); }
            catch (IOException) { }
            writer = null;
        }

        /// <summary>Deletes this program's files older than <see cref="KeepDays"/>, going by the date in the name.</summary>
        void Prune()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory)) return;
                var cut = clock().Date.AddDays(-KeepDays);
                foreach (var f in System.IO.Directory.GetFiles(Directory, Program + "-*.log"))
                    if (DateOf(Path.GetFileName(f), Program) is DateTime d && d < cut)
                        System.IO.File.Delete(f);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }   // tried again next start
        }

        public void Dispose()
        {
            lock (gate) Close();
        }

        internal static string FileName(string program, DateTime date, int? pid = null) =>
            $"{program}-{date:yyyy-MM-dd}" + (pid.HasValue ? $"-{pid}" : "") + ".log";

        /// <summary>The date in one of this program's file names, or null for any other file.</summary>
        internal static DateTime? DateOf(string fileName, string program)
        {
            if (!fileName.StartsWith(program + "-", StringComparison.OrdinalIgnoreCase) || fileName.Length < program.Length + 11) return null;
            return DateTime.TryParseExact(fileName.Substring(program.Length + 1, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : (DateTime?)null;
        }

        /// <summary>One entry: time, level, server, text; an exception follows on indented lines, stack trace and all.</summary>
        internal static string Format(DateTime time, LogLevel level, string server, string text, Exception ex)
        {
            var sb = new StringBuilder();
            sb.Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append(' ').Append(level.ToString().ToUpperInvariant().PadRight(5))
              .Append(' ').Append(string.IsNullOrEmpty(server) ? "-" : server)
              .Append("  ").Append(OneLine(text));
            if (ex != null)
            {
                sb.Append($" [{ex.GetType().Name} 0x{ex.HResult:X8}]");
                foreach (var line in ex.ToString().Split('\n')) sb.Append("\r\n    ").Append(line.TrimEnd('\r'));
            }
            return sb.Append("\r\n").ToString();
        }

        // Remote error text can carry line breaks; an entry stays one line so the file can be searched line by line
        static string OneLine(string s) => string.Join(" ", (s ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()));
    }
}
