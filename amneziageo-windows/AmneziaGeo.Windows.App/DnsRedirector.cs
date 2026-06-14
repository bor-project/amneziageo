using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Points every active interface's DNS at the given servers and restores the originals on stop.
/// </summary>
internal sealed class DnsRedirector(IReadOnlyList<string> servers)
{
    private readonly Dictionary<uint, List<string>> _saved = [];

    /// <summary>
    /// Saves current DNS servers per interface and sets them to the loopback proxy.
    /// </summary>
    public void Apply()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var index = Ipv4Index(nic);
            if (index is null)
            {
                continue;
            }

            var current = CurrentServers(nic);
            if (RunDns($"Set-DnsClientServerAddress -InterfaceIndex {index.Value} -ServerAddresses {string.Join(",", servers)}"))
            {
                _saved[(uint)index.Value] = current;
            }
        }
    }

    /// <summary>
    /// Restores the previously saved DNS servers on every interface.
    /// </summary>
    public void Restore()
    {
        foreach (var (index, servers) in _saved)
        {
            if (servers.Count > 0)
            {
                RunDns($"Set-DnsClientServerAddress -InterfaceIndex {index} -ServerAddresses {string.Join(",", servers)}");
            }
            else
            {
                RunDns($"Set-DnsClientServerAddress -InterfaceIndex {index} -ResetServerAddresses");
            }
        }

        _saved.Clear();
    }

    private static List<string> CurrentServers(NetworkInterface nic)
    {
        var servers = new List<string>();
        foreach (var dns in nic.GetIPProperties().DnsAddresses)
        {
            if (dns.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(dns))
            {
                servers.Add(dns.ToString());
            }
        }

        return servers;
    }

    private static int? Ipv4Index(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GetIPv4Properties()?.Index;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static bool RunDns(string command)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using (var process = Process.Start(startInfo)!)
        {
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
    }
}
