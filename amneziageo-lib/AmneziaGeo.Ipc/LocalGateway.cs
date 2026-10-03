using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AmneziaGeo.Ipc;

/// <summary>
/// The router this machine leaves through. The nearest leg of a channel check aims at it, so a bad Wi-Fi is
/// separated from a bad provider before anything past the house is blamed.
/// </summary>
public static class LocalGateway
{
    /// <summary>
    /// The first physical IPv4 gateway an operational adapter declares, or null when the system declares none -
    /// android hands out no gateway through this interface and supplies its own. An adapter the caller names as
    /// the client's own is passed over.
    /// </summary>
    public static string? Find(Func<NetworkInterface, bool>? own = null)
    {
        try
        {
            var declared = new List<(bool Own, IReadOnlyList<IPAddress> Gateways)>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                declared.Add((own?.Invoke(adapter) ?? false, [.. adapter.GetIPProperties().GatewayAddresses.Select(gateway => gateway.Address)]));
            }

            return Pick(declared);
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
        }

        return null;
    }

    /// <summary>
    /// The first IPv4 gateway declared by an adapter that is not the client's own.
    /// </summary>
    internal static string? Pick(IEnumerable<(bool Own, IReadOnlyList<IPAddress> Gateways)> adapters)
    {
        foreach (var (own, gateways) in adapters)
        {
            if (own)
            {
                continue;
            }

            foreach (var address in gateways)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any))
                {
                    return address.ToString();
                }
            }
        }

        return null;
    }
}
