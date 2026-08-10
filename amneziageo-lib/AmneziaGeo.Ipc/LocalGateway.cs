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
    /// android hands out no gateway through this interface and supplies its own.
    /// </summary>
    public static string? Find()
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var gateway in adapter.GetIPProperties().GatewayAddresses)
                {
                    if (gateway.Address is { AddressFamily: AddressFamily.InterNetwork } address
                        && !address.Equals(IPAddress.Any))
                    {
                        return address.ToString();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
        }

        return null;
    }
}
