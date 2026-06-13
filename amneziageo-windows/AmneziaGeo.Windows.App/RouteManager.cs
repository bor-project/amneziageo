using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Manages the endpoint-exclusion host route that keeps tunnel underlay packets off the tunnel.
/// </summary>
internal static partial class RouteManager
{
    /// <summary>
    /// Adds a host route for the endpoint via the current physical gateway.
    /// </summary>
    public static bool AddEndpointExclusion(IPAddress endpoint)
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
        return result == 0;
    }

    /// <summary>
    /// Removes the endpoint host route.
    /// </summary>
    public static void RemoveEndpointExclusion(IPAddress endpoint)
    {
        Route("delete", endpoint.ToString());
    }

    /// <summary>
    /// Adds an on-link host route for an IP through the tunnel interface.
    /// </summary>
    public static bool AddTunnelRoute(IPAddress ip, uint tunnelInterfaceIndex)
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
    public static void RemoveTunnelRoute(IPAddress ip, uint tunnelInterfaceIndex)
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
    public static uint? FindInterfaceIndex(string adapterName)
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
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using (var process = Process.Start(startInfo)!)
        {
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetBestInterface(uint destAddr, out uint bestIfIndex);
}
