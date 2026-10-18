using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;

namespace WUGrid.Core.Probing
{
    public enum CheckStatus { Pending, Ok, Failed, Skipped }

    public sealed class CheckOutcome
    {
        public static readonly CheckOutcome Pending = new CheckOutcome(CheckStatus.Pending, "");

        public CheckOutcome(CheckStatus state, string detail, int hresult = 0)
        {
            State = state;
            Detail = detail ?? "";
            HResult = hresult;
        }

        public CheckStatus State { get; }

        /// <summary>What was found, or the error message (localized on the target: display only).</summary>
        public string Detail { get; }

        /// <summary>Set for failures that have one; look it up in <see cref="Errors.ErrorCatalog"/>.</summary>
        public int HResult { get; }
    }

    public sealed class AccessCheckResult
    {
        public AccessCheckResult(string server) { Server = server; }

        public string Server { get; }
        public CheckOutcome Dns { get; internal set; } = CheckOutcome.Pending;
        public CheckOutcome Ping { get; internal set; } = CheckOutcome.Pending;
        public CheckOutcome Wmi { get; internal set; } = CheckOutcome.Pending;
        public CheckOutcome AdminShare { get; internal set; } = CheckOutcome.Pending;
        public DateTime CheckedAt { get; internal set; }

        public bool Done => new[] { Dns, Ping, Wmi, AdminShare }.All(c => c.State != CheckStatus.Pending);
        public bool AllOk => new[] { Dns, Ping, Wmi, AdminShare }.All(c => c.State == CheckStatus.Ok);
    }

    /// <summary>
    /// One-shot, read-only reachability check for a server about to be watched: name resolves, ping answers, WMI over
    /// DCOM answers (OS query plus a registry read, which is what the grid needs) and admin$ can be listed. Each step has
    /// its own time limit, so a silent firewall costs seconds, not minutes.
    /// </summary>
    public static class AccessCheck
    {
        public static AccessCheckResult Run(string server, TimeSpan timeout, Action<AccessCheckResult> progress = null)
        {
            var r = new AccessCheckResult(server);
            Step(r, progress, x => x.Dns = Resolve(server, timeout));
            if (r.Dns.State != CheckStatus.Ok)
            {
                var skipped = new CheckOutcome(CheckStatus.Skipped, "name does not resolve");
                r.Ping = r.Wmi = r.AdminShare = skipped;
            }
            else
            {
                Step(r, progress, x => x.Ping = PingOnce(server));
                Step(r, progress, x => x.Wmi = Attempt(() => QueryWmi(server, timeout), timeout));
                Step(r, progress, x => x.AdminShare = Attempt(() => ListAdminShare(server), timeout));
            }
            r.CheckedAt = DateTime.Now;
            progress?.Invoke(r);
            return r;
        }

        static void Step(AccessCheckResult r, Action<AccessCheckResult> progress, Action<AccessCheckResult> step)
        {
            step(r);
            progress?.Invoke(r);
        }

        static CheckOutcome Resolve(string server, TimeSpan timeout) => Attempt(() =>
        {
            try
            {
                var addresses = Dns.GetHostAddresses(server).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToList();
                if (addresses.Count == 0) addresses = Dns.GetHostAddresses(server).ToList();
                return new CheckOutcome(CheckStatus.Ok, string.Join(", ", addresses.Select(a => a.ToString())));
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                // Short wording of our own for the column; the code's explanation says the rest
                return new CheckOutcome(CheckStatus.Failed, "name does not resolve", Probing.Wmi.HResultOf(ex));
            }
        }, timeout);

        static CheckOutcome PingOnce(string server)
        {
            try
            {
                using (var ping = new Ping())
                {
                    var reply = ping.Send(server, 1000);
                    return reply.Status == IPStatus.Success
                        ? new CheckOutcome(CheckStatus.Ok, $"{reply.RoundtripTime} ms")
                        : new CheckOutcome(CheckStatus.Failed, $"{reply.Status}: the grid will show it as DOWN");
                }
            }
            catch (Exception ex) { return Failed(ex); }
        }

        static CheckOutcome QueryWmi(string server, TimeSpan timeout)
        {
            var scope = Probing.Wmi.Connect(server, timeout);
            var os = Probing.Wmi.Query(scope, "SELECT Caption, Version, LastBootUpTime FROM Win32_OperatingSystem", timeout)
                         .Select(mo => new
                         {
                             Caption = ((string)mo["Caption"] ?? "").Replace("Microsoft ", "").Trim(),
                             Version = (string)mo["Version"],
                             Boot = ManagementDateTimeConverter.ToDateTime((string)mo["LastBootUpTime"]),
                         })
                         .First();
            using (var registry = Probing.Wmi.Registry(scope))
                Probing.Wmi.SubKeys(registry, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing", timeout);
            return new CheckOutcome(CheckStatus.Ok, $"{os.Caption} ({os.Version}), up {Durations.Format(DateTime.Now - os.Boot)}");
        }

        static CheckOutcome ListAdminShare(string server)
        {
            Directory.EnumerateFileSystemEntries($@"\\{server}\admin$").Take(1).ToList();
            return new CheckOutcome(CheckStatus.Ok, "readable");
        }

        static CheckOutcome Attempt(Func<CheckOutcome> check, TimeSpan timeout)
        {
            try { return Probing.Wmi.WithTimeout(check, timeout); }
            catch (AggregateException ex) { return Failed(ex.InnerException ?? ex); }
            catch (TimeoutException ex) { return new CheckOutcome(CheckStatus.Failed, ex.Message, unchecked((int)0x800705B4)); }
            catch (Exception ex) { return Failed(ex); }
        }

        static CheckOutcome Failed(Exception ex) => new CheckOutcome(CheckStatus.Failed, Probing.Wmi.FirstLine(ex.Message), Probing.Wmi.HResultOf(ex));
    }
}
