using System.Management;
using System.Net.Security;
using System.Runtime.InteropServices;
using TokenViewer.Core.Services;

namespace TokenViewer.App.Services;

/// <summary>
/// Finds the Antigravity language server on Windows: command lines via WMI (replaces <c>ps</c>)
/// and listening TCP ports via <c>GetExtendedTcpTable</c> (replaces <c>lsof</c>).
/// </summary>
public sealed class WindowsLanguageServerProbe : ILanguageServerProbe
{
    public LanguageServerProcess? FindLanguageServer()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE 'language_server%'");
            foreach (var obj in searcher.Get().OfType<ManagementObject>())
            {
                using (obj)
                {
                    if (obj["CommandLine"] is not string cmd) continue;
                    if (AntigravityQuotaService.ExtractCsrfToken(cmd) is not { } token) continue;
                    return new LanguageServerProcess(Convert.ToInt32(obj["ProcessId"]), token);
                }
            }
        }
        catch (ManagementException)
        {
            // WMI unavailable: treat as not running.
        }
        return null;
    }

    public IReadOnlyList<int> FindListeningPorts(int pid)
    {
        const int afInet = 2;
        const int tcpTableOwnerPidListener = 3;

        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, afInet, tcpTableOwnerPidListener, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, afInet, tcpTableOwnerPidListener, 0) != 0) return [];

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            var ports = new SortedSet<int>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buffer + 4 + i * rowSize);
                if (row.OwningPid != pid) continue;
                // dwLocalPort holds the port in network byte order in its low 16 bits.
                ports.Add((int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF)));
            }
            return ports.ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// HttpClient for the language server's localhost RPC: accepts its self-signed certificate
    /// only when the request host is 127.0.0.1 / localhost.
    /// </summary>
    public static HttpClient CreateLocalhostHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            SslOptions =
            {
                RemoteCertificateValidationCallback = (sender, _, _, errors) =>
                    errors == SslPolicyErrors.None ||
                    (sender is SslStream { TargetHostName: "127.0.0.1" or "localhost" }),
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);
}
