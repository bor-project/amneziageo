using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Manages the endpoint-exclusion host route that keeps tunnel underlay packets off the tunnel.
/// </summary>
internal sealed partial class RouteManager
{
    /// <summary>
    /// Adds a host route for the endpoint via the current physical gateway.
    /// </summary>
    public bool AddEndpointExclusion(IPAddress endpoint)
    {
        var (gateway, interfaceIndex) = FindPhysicalGateway(endpoint);
        if (gateway is null)
        {
            return false;
        }

        var result = Route(
            "add",
            endpoint.ToString(),
            "mask",
            "255.255.255.255",
            gateway.ToString(),
            "if",
            interfaceIndex.ToString(),
            "metric",
            "1");
        if (result == 0)
        {
            // Persist so the route can be reverted even if this process exits without teardown.
            // The endpoint route sits on the physical adapter and does not vanish with the tunnel.
            UpdateState(endpoint.ToString(), add: true);
        }

        return result == 0;
    }

    /// <summary>
    /// Removes the endpoint host route.
    /// </summary>
    public void RemoveEndpointExclusion(IPAddress endpoint)
    {
        Route("delete", endpoint.ToString());
        UpdateState(endpoint.ToString(), add: false);
    }

    /// <summary>
    /// Removes any endpoint-exclusion routes persisted by a previous run, even from another process;
    /// no-op if none are recorded. Used to revert leftovers from a tunnel that was killed.
    /// </summary>
    public void RestoreSavedExclusions()
    {
        var saved = ReadState();
        if (saved.Count == 0)
        {
            return;
        }

        foreach (var endpoint in saved)
        {
            Route("delete", endpoint);
        }

        ClearState();
    }

    /// <summary>
    /// Adds an on-link host route for an IP through the tunnel interface.
    /// </summary>
    public bool AddTunnelRoute(IPAddress ip, uint tunnelInterfaceIndex)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return Netsh("interface", "ipv6", "add", "route", $"{ip}/128", $"interface={tunnelInterfaceIndex}") == 0;
        }

        var result = Route(
            "add",
            ip.ToString(),
            "mask",
            "255.255.255.255",
            "0.0.0.0",
            "if",
            tunnelInterfaceIndex.ToString(),
            "metric",
            "1");
        return result == 0;
    }

    /// <summary>
    /// Removes a host route for an IP from the tunnel interface.
    /// </summary>
    public void RemoveTunnelRoute(IPAddress ip, uint tunnelInterfaceIndex)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Netsh("interface", "ipv6", "delete", "route", $"{ip}/128", $"interface={tunnelInterfaceIndex}");
            return;
        }

        Route("delete", ip.ToString());
    }

    /// <summary>
    /// Returns the IPv4 interface index of a network adapter by name.
    /// </summary>
    public uint? FindInterfaceIndex(string adapterName)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.Name != adapterName)
            {
                continue;
            }

            var index = Ipv4Index(nic);
            if (index is not null)
            {
                return (uint)index.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Disables IPv6 on the tunnel adapter. AmneziaWG tunnels here are IPv4-only, yet Windows still
    /// hands a v4-only adapter the dead site-local fec0:0:0:ffff:: IPv6 DNS servers; because the tunnel
    /// is the lowest-metric interface the system resolver tries those first and stalls roughly a second
    /// per lookup. Turning the adapter's IPv6 binding off removes the bogus resolvers outright. The
    /// adapter is recreated on every tunnel start, so this is reapplied per run and needs no restore.
    /// </summary>
    public void DisableIpv6(string adapterName)
    {
        Run("powershell.exe", ["-NoProfile", "-Command", $"Disable-NetAdapterBinding -Name '{adapterName}' -ComponentID ms_tcpip6"]);
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

    private static (IPAddress? Gateway, uint InterfaceIndex) FindPhysicalGateway(IPAddress endpoint)
    {
        var destination = BitConverter.ToUInt32(endpoint.GetAddressBytes(), 0);
        if (GetBestInterface(destination, out var interfaceIndex) != 0)
        {
            return (null, 0);
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (Ipv4Index(nic) != interfaceIndex)
            {
                continue;
            }

            var properties = nic.GetIPProperties();
            foreach (var gateway in properties.GatewayAddresses)
            {
                if (gateway.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return (gateway.Address, interfaceIndex);
                }
            }
        }

        return (null, 0);
    }

    private static int Route(params string[] arguments)
    {
        return Run("route.exe", arguments);
    }

    private static int Netsh(params string[] arguments)
    {
        return Run("netsh", arguments);
    }

    private static int Run(string fileName, string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using (var process = System.Diagnostics.Process.Start(startInfo)!)
        {
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    private static void UpdateState(string endpoint, bool add)
    {
        var saved = ReadState();
        if (add)
        {
            if (!saved.Contains(endpoint))
            {
                saved.Add(endpoint);
            }
        }
        else
        {
            saved.Remove(endpoint);
        }

        if (saved.Count == 0)
        {
            ClearState();
            return;
        }

        var path = TunnelPaths.RouteStateFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, saved);
    }

    private static List<string> ReadState()
    {
        var path = TunnelPaths.RouteStateFile();
        if (!File.Exists(path))
        {
            return [];
        }

        var saved = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            var endpoint = line.Trim();
            if (endpoint.Length > 0 && !saved.Contains(endpoint))
            {
                saved.Add(endpoint);
            }
        }

        return saved;
    }

    private static void ClearState()
    {
        var path = TunnelPaths.RouteStateFile();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetBestInterface(uint destAddr, out uint bestIfIndex);
}
