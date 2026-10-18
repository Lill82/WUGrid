using System;
using System.Collections.Generic;
using System.Management;
using System.Threading.Tasks;

namespace WUGrid.Core.Probing
{
    /// <summary>WMI over DCOM (System.Management): WinRM is not needed. Everything here is read-only.</summary>
    static class Wmi
    {
        public const uint HKLM = 0x80000002;

        /// <summary>root\cimv2 on the server. StdRegProv lives there too (Server 2008 and later), so one connection does all.</summary>
        /// <param name="privileges">Enable the caller's privileges for the calls (the restart needs SeRemoteShutdownPrivilege);
        /// observing never does.</param>
        public static ManagementScope Connect(string host, TimeSpan timeout, bool privileges = false)
        {
            var options = new ConnectionOptions
            {
                Impersonation = ImpersonationLevel.Impersonate,
                Authentication = AuthenticationLevel.PacketPrivacy,
                Timeout = timeout,
                EnablePrivileges = privileges,
            };
            var scope = new ManagementScope($@"\\{host}\root\cimv2", options);
            scope.Connect();
            return scope;
        }

        public static IEnumerable<ManagementObject> Query(ManagementScope scope, string wql, TimeSpan timeout)
        {
            var options = new EnumerationOptions { Timeout = timeout, ReturnImmediately = true, Rewindable = false };
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), options))
            using (var results = searcher.Get())
                foreach (ManagementObject mo in results)
                    using (mo)
                        yield return mo;
        }

        /// <summary>Subkey names of an HKLM key; empty when the key is missing or has none.</summary>
        public static string[] SubKeys(ManagementClass registry, string path, TimeSpan timeout)
        {
            using (var args = registry.GetMethodParameters("EnumKey"))
            {
                args["hDefKey"] = HKLM;
                args["sSubKeyName"] = path;
                using (var result = registry.InvokeMethod("EnumKey", args, new InvokeMethodOptions(null, timeout)))
                    return result?["sNames"] as string[] ?? new string[0];   // null for a key without subkeys (the legacy @($null) trap)
            }
        }

        public static ManagementClass Registry(ManagementScope scope) => new ManagementClass(scope, new ManagementPath("StdRegProv"), null);

        /// <summary>A REG_SZ / REG_DWORD value under HKLM, or null when missing.</summary>
        public static object Value(ManagementClass registry, string path, string name, bool dword, TimeSpan timeout)
        {
            var method = dword ? "GetDWORDValue" : "GetStringValue";
            using (var args = registry.GetMethodParameters(method))
            {
                args["hDefKey"] = HKLM;
                args["sSubKeyName"] = path;
                args["sValueName"] = name;
                using (var result = registry.InvokeMethod(method, args, new InvokeMethodOptions(null, timeout)))
                    return result != null && Convert.ToUInt32(result["ReturnValue"]) == 0 ? result[dword ? "uValue" : "sValue"] : null;
            }
        }

        /// <summary>WMI failures carry their WBEM code in ErrorCode; DCOM/RPC ones (access denied, RPC unavailable) in HResult;
        /// socket errors (DNS) only in their Win32 code, returned here as an HRESULT.</summary>
        public static int HResultOf(Exception ex) =>
            ex is ManagementException me ? (int)me.ErrorCode
            : ex is System.Net.Sockets.SocketException se ? unchecked((int)(0x80070000u | (uint)(se.ErrorCode & 0xFFFF)))
            : ex.HResult;

        public static string FirstLine(string message) => (message ?? "").Split('\n')[0].Trim();

        /// <summary>Runs a call that may block on DCOM/SMB for much longer than wanted; the abandoned call finishes on its own.</summary>
        public static T WithTimeout<T>(Func<T> call, TimeSpan timeout)
        {
            var task = Task.Run(call);
            if (!task.Wait(timeout)) throw new TimeoutException($"No answer in {timeout.TotalSeconds:0} s.");
            return task.Result;
        }
    }
}
