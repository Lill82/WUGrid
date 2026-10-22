using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using WUGrid.Core.Errors;
using WUGrid.Core.Verification;

namespace WUGrid.Core.Reports
{
    /// <summary>End-of-round report from a snapshot: one row per server (CSV), or a page with a summary table and the
    /// details of every server that needs attention (HTML, self-contained, to mail or attach to a change).</summary>
    public static class RoundReport
    {
        static readonly string[] Columns =
        {
            "Server", "State", "Note", "Last boot", "Boots in round", "Restarted by", "Build before", "Build now",
            "Updates installed", "Updates failed", "Updates not installed yet", "Services not running", "Expected stops", "Services removed", "Checked",
        };

        /// <param name="separator">Defaults to the culture's list separator, which is what Excel splits on.</param>
        public static string Csv(RoundSnapshot snapshot, string separator = null)
        {
            var sep = separator ?? CultureInfo.CurrentCulture.TextInfo.ListSeparator;
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(sep, Columns.Select(c => Quote(c, sep))));
            foreach (var row in snapshot.Rows)
                sb.AppendLine(string.Join(sep, Values(row).Select(v => Quote(v, sep))));
            return sb.ToString();
        }

        public static string Html(RoundSnapshot snapshot, string title = "WUGrid round report")
        {
            var rows = snapshot.Rows;
            var h = new StringBuilder();
            h.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(E(title)).Append("</title><style>")
             .Append("body{font:14px/1.45 'Segoe UI',system-ui,sans-serif;color:#1f2937;margin:24px;max-width:1400px}")
             .Append("h1{font-size:20px;margin:0 0 4px}h2{font-size:16px;margin:28px 0 6px}.muted{color:#6b7280}")
             .Append("table{border-collapse:collapse;width:100%;margin-top:12px}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid #e5e7eb;vertical-align:top}")
             .Append("th{background:#f3f4f6;color:#6b7280;font-weight:600;font-size:12px}")
             .Append("td:nth-child(4),td:nth-child(5),td:nth-child(7),td:nth-child(8){white-space:nowrap}")
             .Append(".s{display:inline-block;min-width:110px;text-align:center;color:#fff;font-weight:700;font-size:12px;padding:2px 6px;border-radius:3px}")
             .Append(".ok{background:#16a34a}.flag{background:#ea580c}.down{background:#dc2626}.boot{background:#ca8a04}.inst{background:#0891b2}.nr{background:#7c3aed}.np{background:#64748b}.chk{background:#9ca3af}")
             .Append(".bad{color:#b91c1c}.good{color:#15803d}ul{margin:4px 0 0 18px;padding:0}")
             .Append("</style></head><body>");
            h.Append("<h1>").Append(E(title)).Append("</h1><div class=\"muted\">")
             .Append(E($"{snapshot.Now:yyyy-MM-dd HH:mm} · cut-off {snapshot.Since:yyyy-MM-dd HH:mm} · {rows.Count} servers · "))
             .Append(E($"{rows.Count(r => r.Status.IsClean)} back clean, {rows.Count(r => r.Status.State == ServerState.Back && !r.Status.IsClean)} back flagged, "))
             .Append(E($"{rows.Count(r => r.Status.State != ServerState.Back)} not back")).Append("</div>");

            h.Append("<table><tr>");
            foreach (var c in new[] { "Server", "State", "Note", "Last boot", "Boots", "Restarted by", "Build", "Updates" }) h.Append("<th>").Append(c).Append("</th>");
            h.Append("</tr>");
            foreach (var r in rows)
            {
                var v = Current(r);
                var b = BuildChange(r);
                h.Append("<tr><td><b>").Append(E(r.Name)).Append("</b></td>")
                 .Append("<td><span class=\"s ").Append(CssClass(r.Status)).Append("\">").Append(E(r.Status.Label)).Append("</span></td>")
                 .Append("<td>").Append(E(r.Status.Note)).Append("</td>")
                 .Append("<td>").Append(E(r.Observation.LastBoot?.ToString("dd-MM HH:mm") ?? "")).Append("</td>")
                 .Append("<td>").Append(v == null ? "" : v.Boots.Count.ToString()).Append("</td>")
                 .Append("<td>").Append(E(RestartedBy(v))).Append("</td>")
                 .Append("<td>").Append(E(b)).Append("</td><td>");
                if (v != null)
                {
                    var ok = v.Updates.Count(u => u.Succeeded);
                    var failed = v.Updates.Count(u => !u.Succeeded);
                    if (ok > 0) h.Append("<span class=\"good\">").Append(ok).Append(" installed</span> ");
                    if (failed > 0) h.Append("<span class=\"bad\">").Append(failed).Append(" failed</span> ");
                }
                if (r.UpdatesInFlight.Count > 0) h.Append("<span class=\"muted\">").Append(r.UpdatesInFlight.Count).Append(" not installed yet</span>");
                h.Append("</td></tr>");
            }
            h.Append("</table>");

            var attention = rows.Where(r => !r.Status.IsClean || (Current(r)?.UnexpectedShutdown ?? false)).ToList();
            h.Append("<h2>Needs attention (").Append(attention.Count).Append(")</h2>");
            if (attention.Count == 0) h.Append("<p class=\"good\">Every server is back clean.</p>");
            foreach (var r in attention) Details(h, r);

            var expected = rows.Where(r => r.ExpectedStops.Count > 0).ToList();
            if (expected.Count > 0)
            {
                h.Append("<h2>Expected stops (").Append(expected.Sum(r => r.ExpectedStops.Count)).Append(")</h2>")
                 .Append("<p class=\"muted\">Services not running that the operator marked as expected: shown here, not flagged.</p><ul>");
                foreach (var r in expected)
                    foreach (var x in r.ExpectedStops)
                        h.Append("<li><b>").Append(E(r.Name)).Append("</b> ").Append(E(ExpectedLine(x))).Append("</li>");
                h.Append("</ul>");
            }

            h.Append("<p class=\"muted\" style=\"margin-top:32px\">Generated by WUGrid, read-only: nothing was changed on the servers. ")
             .Append("Error explanations from Microsoft Learn (CC BY 4.0) and WUGrid's field notes.</p></body></html>");
            return h.ToString();
        }

        static void Details(StringBuilder h, ServerRow r)
        {
            var v = Current(r);
            h.Append("<h2>").Append(E(r.Name)).Append(" <span class=\"s ").Append(CssClass(r.Status)).Append("\">")
             .Append(E(r.Status.Label)).Append("</span></h2><div>").Append(E(r.Status.Note)).Append("</div>");
            if (r.Status.State == ServerState.Booting && r.Observation.WmiHResult != 0)
                Explain(h, r.Observation.WmiHResult);
            if (v == null) return;
            var b = BuildChange(r);
            if (b != "") h.Append("<div class=\"muted\">Build: ").Append(E(b)).Append("</div>");
            foreach (var u in v.Failed)
            {
                h.Append("<div class=\"bad\" style=\"margin-top:8px\"><b>Failed</b> ").Append(E($"{u.Time:dd-MM HH:mm} {u.Title}")).Append("</div>");
                Explain(h, u.ErrorCode);
            }
            if (r.UnexpectedStops.Count > 0)
            {
                h.Append("<div style=\"margin-top:8px\"><b>Services not running</b> <span class=\"muted\">")
                 .Append(v.ComparedToBaseline ? E($"({NotRunningOf(r)})") : "(Automatic, not running)").Append("</span><ul>");
                foreach (var i in r.UnexpectedStops) h.Append("<li>").Append(E(ServiceLine(i))).Append(Advice(i)).Append("</li>");
                h.Append("</ul></div>");
            }
            if (r.ExpectedStops.Count > 0)
            {
                h.Append("<div class=\"muted\" style=\"margin-top:8px\"><b>Not running, expected</b> (marked by the operator)<ul>");
                foreach (var x in r.ExpectedStops) h.Append("<li>").Append(E(ExpectedLine(x))).Append(Advice(x.Issue)).Append("</li>");
                h.Append("</ul></div>");
            }
            if (v.RemovedServices.Count > 0)
            {
                h.Append("<div class=\"muted\" style=\"margin-top:8px\"><b>Removed as planned</b> (marked for deletion before the restart)<ul>");
                foreach (var s in v.RemovedServices) h.Append("<li>").Append(E(RemovedLine(s))).Append("</li>");
                h.Append("</ul></div>");
            }
            if (v.UnexpectedShutdown) h.Append("<div class=\"bad\" style=\"margin-top:8px\">An unexpected shutdown was logged during the round (event 6008).</div>");
            if (v.Restarts.Count > 0)
            {
                h.Append("<div style=\"margin-top:8px\"><b>Restarts</b><ul>");
                foreach (var x in v.Restarts) h.Append("<li>").Append(E(RestartLine(x))).Append("</li>");
                h.Append("</ul></div>");
            }
        }

        static string Advice(ServiceIssue i) =>
            i.Failure == null || i.Failure.Advice == "" ? "" : "<div class=\"muted\">" + E(i.Failure.Advice) + "</div>";

        static void Explain(StringBuilder h, int code)
        {
            foreach (var e in ErrorCatalog.Lookup(code))
            {
                h.Append("<div class=\"muted\" style=\"margin-left:18px\">").Append(E($"{e.Hex} {e.Name}: {e.Description}"));
                if (e.Mitigation != "") h.Append("<br>What to do: ").Append(E(e.Mitigation));
                h.Append("</div>");
            }
        }

        public static string ServiceLine(ServiceIssue i)
        {
            var s = i.Service;
            var line = $"{s.DisplayName} ({s.Name}): {s.State}" + (s.StartMode != "" ? $", {s.StartMode}" : "");
            var c = i.LastStartModeChange;
            if (c != null) line += $"; start type changed {c.Time:dd-MM HH:mm} by {(c.User != "" ? c.User : "?")}: {c.At(1)} → {c.At(2)}";
            var f = i.Failure;
            if (f != null) line += "; " + f.Reason + (f.Earlier != "" ? ", " + f.Earlier : "");
            return line;
        }

        /// <summary>"3 of 24 that ran before the restart": the flagged stops against the baseline's expected count.</summary>
        public static string NotRunningOf(ServerRow r)
        {
            var b = r.Observation.Baseline;
            return b != null ? $"{r.UnexpectedStops.Count} of {b.RunningAutoServices.Count} that ran before the restart" : "ran before the restart";
        }

        /// <summary>A stop the operator marked as expected: the service line, on every server or not, and the reason.</summary>
        public static string ExpectedLine(ExpectedServiceStop x) =>
            ServiceLine(x.Issue) + (x.Entry.Server == null ? " — expected on every server" : " — expected") + (x.Entry.Comment != "" ? ": " + x.Entry.Comment : "");

        /// <summary>A service the baseline found marked for deletion: gone now, or still registered until its last handle closes.</summary>
        public static string RemovedLine(ServiceRecord s) =>
            $"{s.DisplayName} ({s.Name}): " + (s.State == "Missing" ? "gone" : $"still registered, {s.State}");

        public static string RestartLine(RestartRecord x) =>
            $"{x.Time:dd-MM HH:mm} by {x.User} — {x.Process} — {x.ReasonCode} {x.Reason}" + (x.Comment != "" ? $" — \"{x.Comment}\"" : "");

        static IEnumerable<string> Values(ServerRow r)
        {
            var v = Current(r);
            yield return r.Name;
            yield return r.Status.Label;
            yield return r.Status.Note;
            yield return r.Observation.LastBoot?.ToString("yyyy-MM-dd HH:mm") ?? "";
            yield return v == null ? "" : v.Boots.Count.ToString(CultureInfo.InvariantCulture);
            yield return RestartedBy(v);
            yield return r.Observation.Baseline?.Build ?? "";
            yield return r.Observation.Build ?? "";
            yield return v == null ? "" : string.Join(" ", v.Updates.Where(u => u.Succeeded).Select(u => u.Label));
            yield return v == null ? "" : string.Join(" ", v.Failed.Select(u => $"{u.Label}:{ErrorCatalog.Hex(u.ErrorCode)}"));
            yield return string.Join(" ", r.UpdatesInFlight.Select(u => $"{u.Label}:{(u.Installing ? "installing" : "downloading")}"));
            yield return string.Join(", ", r.UnexpectedStops.Select(i => i.Service.Name));
            yield return string.Join(", ", r.ExpectedStops.Select(x => x.Issue.Service.Name));
            yield return v == null ? "" : string.Join(", ", v.RemovedServices.Select(s => s.Name));
            yield return v?.CheckedAt.ToString("yyyy-MM-dd HH:mm") ?? "";
        }

        /// <summary>The verification only when it belongs to the server's current boot.</summary>
        static ServerVerification Current(ServerRow r)
        {
            var v = r.Observation.Verification;
            return v != null && r.Observation.LastBoot == v.Boot ? v : null;
        }

        static string RestartedBy(ServerVerification v)
        {
            var first = v?.Restarts.FirstOrDefault();
            return first == null ? "" : $"{first.User} ({ShortProcess(first.Process)})";
        }

        static string ShortProcess(string process)
        {
            var exe = process.Split(' ')[0];
            var slash = exe.LastIndexOf('\\');
            return slash >= 0 ? exe.Substring(slash + 1) + process.Substring(exe.Length) : process;
        }

        public static string BuildChange(ServerRow r)
        {
            var before = r.Observation.Baseline?.Build;
            var now = r.Observation.Build;
            if (now == null) return before ?? "";
            return before != null && before != now ? $"{before} → {now}" : now;
        }

        /// <summary>For a grid column: "17763.8880", or "17763.8644 → 8880" when the build moved since the baseline.</summary>
        /// <param name="arrow">"->" where "→" may not show (a console code page).</param>
        public static string ShortBuildChange(Observation o, string arrow = "→")
        {
            var before = o.Baseline?.Build;
            var now = o.Build;
            if (now == null || before == null || before == now) return now ?? before ?? "";
            var b = before.Split('.');
            var n = now.Split('.');
            return b.Length == 2 && n.Length == 2 && b[0] == n[0] ? $"{before} {arrow} {n[1]}" : $"{before} {arrow} {now}";
        }

        static string CssClass(ServerStatus s)
        {
            switch (s.State)
            {
                case ServerState.Back: return s.IsClean ? "ok" : "flag";
                case ServerState.Down: return "down";
                case ServerState.Booting: return "boot";
                case ServerState.Installing: return "inst";
                case ServerState.RestartPending: return "nr";
                case ServerState.NoRestartPending: return "np";
                default: return "chk";
            }
        }

        static string Quote(string value, string sep)
        {
            var v = value ?? "";
            return v.Contains(sep) || v.Contains("\"") || v.Contains("\n") || v.Contains("\r") ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        static string E(string s) => WebUtility.HtmlEncode(s ?? "");
    }
}
