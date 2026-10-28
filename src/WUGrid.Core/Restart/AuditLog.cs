using System;
using System.IO;
using System.Text;

namespace WUGrid.Core.Restart
{
    /// <summary>
    /// Append-only record of every action WUGrid sends to a server, one JSON object per line: who, from where, what,
    /// to which server, with which parameters, and what came of it. Check-only runs are recorded too.
    /// </summary>
    public sealed class AuditLog
    {
        static readonly object Gate = new object();

        public AuditLog(string path) { Path = path; }

        public string Path { get; }

        public static string DefaultPath =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WUGrid", "audit.jsonl");

        public void Restart(RestartResult result, RestartOptions options, DateTime now)
        {
            var line = Line(result, options, now, RestartAction.Operator, Environment.MachineName);
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                File.AppendAllText(Path, line + "\n", new UTF8Encoding(false));
            }
        }

        internal static string Line(RestartResult r, RestartOptions o, DateTime now, string user, string workstation) =>
            new JsonObject()
                .Time("time", now)
                .Str("operator", user ?? "")
                .Str("workstation", workstation ?? "")
                .Str("action", "restart")
                .Str("server", r.Server)
                .Bool("checkOnly", o.CheckOnly)
                .Bool("force", r.Forced)
                .Bool("forceIfSignedIn", o.ForceIfSignedIn)
                .Num("warningSeconds", o.WarningSeconds)
                .Str("reasonCode", "0x" + RestartAction.ReasonCode.ToString("X8"))
                .Str("comment", o.Comment ?? "")
                .Str("outcome", r.Outcome.ToString())
                .Str("detail", r.Detail ?? "")
                .Str("code", Errors.ErrorCatalog.Hex(r.Code))
                .ToString();
    }
}
