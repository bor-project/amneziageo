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

    private static (IPAddress? Gateway, uint InterfaceIndex) FindPhysicalGateway(IPAddress endpoint)
    {
        var destination = BitConverter.ToUInt32(endpoint.GetAddressBytes(), 0);
        if (GetBestInterface(destination, out var interfaceIndex) != 0)
        {
            return (null, 0);
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = nic.GetIPProperties();
            if (properties.GetIPv4Properties()?.Index != interfaceIndex)
            {
                continue;
            }

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
        var startInfo = new ProcessStartInfo("route.exe")
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
