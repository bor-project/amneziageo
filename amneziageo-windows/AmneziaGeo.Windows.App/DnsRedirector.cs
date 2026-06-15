using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Points every active interface's DNS at the given servers and restores the originals on stop.
/// The applied baseline is persisted so a controlling process can revert it even if the tunnel process dies.
/// </summary>
internal sealed class DnsRedirector(ILogger<DnsRedirector> logger)
{
    private readonly Dictionary<uint, List<string>> _saved = [];

    /// <summary>
    /// Saves current DNS servers per interface and sets them to the redirect servers.
    /// </summary>
    public void Apply(IReadOnlyList<string> servers)
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

            var current = CurrentServers(nic, servers);
            if (RunDns($"Set-DnsClientServerAddress -InterfaceIndex {index.Value} -ServerAddresses {string.Join(",", servers)}"))
            {
                _saved[(uint)index.Value] = current;
            }

            RunDns($"Set-DnsClientServerAddress -InterfaceIndex {index.Value} -ServerAddresses ::1");
        }

        WriteState(_saved);
        logger.LogDebug("dns redirect applied -> {Servers}", string.Join(",", servers));
    }

    /// <summary>
    /// Restores the previously saved DNS servers on every interface.
    /// </summary>
    public void Restore()
    {
        foreach (var (index, saved) in _saved)
        {
            RestoreOne(index, saved);
        }

        _saved.Clear();
        ClearState();
    }

    /// <summary>
    /// Restores a redirect persisted by a previous run, even from another process; no-op if none is active.
    /// </summary>
    public void RestoreSaved()
    {
        var state = ReadState();
        if (state.Count == 0)
        {
            return;
        }

        foreach (var (index, saved) in state)
        {
            RestoreOne(index, saved);
        }

        ClearState();
        logger.LogDebug("dns redirect restored from persisted state");
    }

    private static void RestoreOne(uint index, List<string> saved)
    {
        if (saved.Count > 0)
        {
            RunDns($"Set-DnsClientServerAddress -InterfaceIndex {index} -ServerAddresses {string.Join(",", saved)}");
        }
        else
        {
            RunDns($"Set-DnsClientServerAddress -InterfaceIndex {index} -ResetServerAddresses");
        }
    }

    private static List<string> CurrentServers(NetworkInterface nic, IReadOnlyList<string> servers)
    {
        var current = new List<string>();
        foreach (var dns in nic.GetIPProperties().DnsAddresses)
        {
            if (dns.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(dns))
            {
                continue;
            }

            var text = dns.ToString();
            if (!servers.Contains(text))
            {
                current.Add(text);
            }
        }

        return current;
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

    private static void WriteState(Dictionary<uint, List<string>> state)
    {
        var path = TunnelPaths.DnsStateFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new List<string>();
        foreach (var (index, saved) in state)
        {
            lines.Add($"{index}={string.Join(",", saved)}");
        }

        File.WriteAllLines(path, lines);
    }

    private static Dictionary<uint, List<string>> ReadState()
    {
        var path = TunnelPaths.DnsStateFile();
        var state = new Dictionary<uint, List<string>>();
        if (!File.Exists(path))
        {
            return state;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var separator = line.IndexOf('=');
            if (separator < 0 || !uint.TryParse(line[..separator], out var index))
            {
                continue;
            }

            var saved = new List<string>();
            var value = line[(separator + 1)..];
            if (value.Length > 0)
            {
                saved.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries));
            }

            state[index] = saved;
        }

        return state;
    }

    private static void ClearState()
    {
        var path = TunnelPaths.DnsStateFile();
        if (File.Exists(path))
        {
            File.Delete(path);
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
