using System;
using System.Linq;
using System.Management;
using System.Security.Principal;
using WUGrid.Core.Probing;

namespace WUGrid.Core.Restart
{
    public enum RestartOutcome { Requested, WouldRestart, Skipped, Failed }

    public sealed class RestartOptions
    {
        /// <summary>Wait between the request and the restart, during which it can be aborted on the server with shutdown /a.</summary>
        public int WarningSeconds { get; set; } = 60;

        /// <summary>Added to the comment WUGrid writes (it lands in System event 1074 on the server).</summary>
        public string Comment { get; set; } = "";

        /// <summary>Do everything except the restart call: connect with privileges, re-check, report.</summary>
        public bool CheckOnly { get; set; }
    }

    public sealed class RestartResult
    {
        public RestartResult(string server, RestartOutcome outcome, string detail, int code = 0)
        {
            Server = server;
            Outcome = outcome;
            Detail = detail ?? "";
            Code = code;
        }

        public string Server { get; }
        public RestartOutcome Outcome { get; }
        public string Detail { get; }

        /// <summary>HRESULT for failures (look it up in the error catalog), 0 otherwise.</summary>
        public int Code { get; }
    }

    /// <summary>
    /// Restarts one server, never forced: Win32_OperatingSystem.Win32ShutdownTracker with flags 2 (reboot, no force), so
    /// running programs can delay or cancel it, and a warning period during which it can be aborted on the server
    /// (shutdown /a). Before sending it, the server is read again over a fresh connection, with who is signed in, and the
    /// <see cref="RestartGuard"/> applied to that read: anything that started update work in the meantime is skipped, and
    /// so is a server with a user signed in, which Windows would refuse with ERROR_SHUTDOWN_USERS_LOGGED_ON.
    /// </summary>
    public static class RestartAction
    {
        /// <summary>SHTDN_REASON_FLAG_PLANNED | SHTDN_REASON_MAJOR_OPERATINGSYSTEM | SHTDN_REASON_MINOR_HOTFIX:
        /// "Operating System: Hotfix (Planned)" in event 1074.</summary>
        public const uint ReasonCode = 0x80020011;

        /// <summary>Reboot without EWX_FORCE.</summary>
        public const int RebootNotForced = 2;

        static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

        public static string Operator => WindowsIdentity.GetCurrent().Name;

        public static string CommentFor(RestartOptions options)
        {
            var text = $"Restart requested from WUGrid by {Operator} on {Environment.MachineName}" +
                       (string.IsNullOrWhiteSpace(options.Comment) ? "" : ": " + options.Comment.Trim());
            return text.Length > 500 ? text.Substring(0, 500) : text;   // the shutdown comment holds 512 characters
        }

        public static RestartResult Execute(string server, DateTime since, RestartOptions options)
        {
            ManagementScope scope;
            ManagementClass registry = null;
            try
            {
                scope = Wmi.Connect(server, Timeout, privileges: true);
                registry = Wmi.Registry(scope);
                var o = new Observation { Ping = true };
                ServerProbe.ReadState(scope, registry, Timeout, o);
                o.SignedIn = SessionReader.Read(scope, Timeout);
                var check = RestartGuard.Check(o, StateClassifier.Classify(o, since, DateTime.Now));
                if (!check.Allowed) return new RestartResult(server, RestartOutcome.Skipped, check.Reason);
                if (options.CheckOnly)
                    return new RestartResult(server, RestartOutcome.WouldRestart, Describe(options) + (check.Reason != "" ? $" ({check.Reason})" : ""));

                var rv = Shutdown(scope, options);
                return rv == 0
                    ? new RestartResult(server, RestartOutcome.Requested, Describe(options))
                    : new RestartResult(server, RestartOutcome.Failed, $"Win32ShutdownTracker returned {rv}", unchecked((int)(0x80070000u | (rv & 0xFFFF))));
            }
            catch (Exception ex)
            {
                return new RestartResult(server, RestartOutcome.Failed, Wmi.FirstLine(ex.Message), Wmi.HResultOf(ex));
            }
            finally
            {
                registry?.Dispose();
            }
        }

        static string Describe(RestartOptions o) =>
            o.WarningSeconds > 0 ? $"restart, not forced, {o.WarningSeconds} s warning" : "restart, not forced, no warning";

        static uint Shutdown(ManagementScope scope, RestartOptions options)
        {
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name FROM Win32_OperatingSystem")))
            using (var results = searcher.Get())
            {
                var os = results.Cast<ManagementObject>().First();
                using (os)
                using (var args = os.GetMethodParameters("Win32ShutdownTracker"))
                {
                    args["Timeout"] = (uint)Math.Max(0, options.WarningSeconds);
                    args["Comment"] = CommentFor(options);
                    args["ReasonCode"] = ReasonCode;
                    args["Flags"] = RebootNotForced;
                    using (var result = os.InvokeMethod("Win32ShutdownTracker", args, null))
                        return Convert.ToUInt32(result["ReturnValue"]);
                }
            }
        }
    }
}
