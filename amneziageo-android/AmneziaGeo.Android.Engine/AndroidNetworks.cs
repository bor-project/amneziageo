using Android.Content;
using Android.Net;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// The networks as the system sees them: the one the device sits on, the tunnel's own, and how names resolve.
/// </summary>
/// <param name="Under">The network the device sits on: Wi-Fi, mobile, ethernet, other, none; empty when not read.</param>
/// <param name="UnderValidated">Whether the system reaches the internet on it; null when not read.</param>
/// <param name="TunnelValidated">Whether the system reaches the internet through the tunnel; null without one.</param>
/// <param name="PrivateDnsHost">The host of a strict private DNS; null when none is set.</param>
/// <param name="PrivateDnsActive">Whether names resolve over private DNS; null below Android 9.</param>
/// <param name="UnderKey">The network the device sits on, its interface and IPv4 addresses; null when not read.</param>
public sealed record NetworkView(
    string Under,
    bool? UnderValidated,
    bool? TunnelValidated,
    string? PrivateDnsHost,
    bool? PrivateDnsActive,
    string? UnderKey = null)
{
    /// <summary>
    /// The network the device sits on in one phrase.
    /// </summary>
    public string UnderText => UnderValidated == false ? $"{Under} without internet" : Under;

    /// <summary>
    /// How names resolve in one phrase.
    /// </summary>
    public string PrivateDnsText => PrivateDnsHost is { Length: > 0 } host
        ? $"strict {host}"
        : PrivateDnsActive == true ? "automatic, in use" : "not in use";
}

/// <summary>
/// Reads the networks off the connectivity service.
/// </summary>
public static class AndroidNetworks
{
    private const string Tag = "AndroidNetworks";

    /// <summary>
    /// The networks right now; a view with nothing read when the service does not answer.
    /// </summary>
    public static NetworkView Read(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager manager)
            {
                return new NetworkView(string.Empty, null, null, null, null);
            }

            var under = NetworkSnapshot.NoNetwork;
            var underValidated = default(bool?);
            var underRank = int.MaxValue;
            var underLink = default(LinkProperties);
            var underNetwork = default(Network);
            var tunnelValidated = default(bool?);
            var tunnelLink = default(LinkProperties);
            foreach (var network in manager.GetAllNetworks())
            {
                if (manager.GetNetworkCapabilities(network) is not { } capabilities
                    || !capabilities.HasCapability(NetCapability.Internet))
                {
                    continue;
                }

                var validated = capabilities.HasCapability(NetCapability.Validated);
                if (capabilities.HasTransport(TransportType.Vpn))
                {
                    tunnelValidated = validated;
                    tunnelLink = manager.GetLinkProperties(network);
                    continue;
                }

                // The system prefers a validated network, and among them a wire over Wi-Fi over mobile.
                var rank = Rank(capabilities) + (validated ? 0 : 10);
                if (rank < underRank)
                {
                    underRank = rank;
                    under = Name(capabilities);
                    underValidated = validated;
                    underLink = manager.GetLinkProperties(network);
                    underNetwork = network;
                }
            }

            var (host, active) = PrivateDns(tunnelLink ?? underLink);
            var key = underNetwork is null ? under : Key(underNetwork, underLink);
            return new NetworkView(under, underValidated, tunnelValidated, host, active, key);
        }
        catch (Java.Lang.Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "reading the networks failed: " + ex);
            return new NetworkView(string.Empty, null, null, null, null);
        }
    }

    // The network handle, its interface and its IPv4 addresses in one line.
    private static string Key(Network network, LinkProperties? link)
    {
        var addresses = (link?.LinkAddresses ?? [])
            .Select(address => address.Address)
            .OfType<Java.Net.Inet4Address>()
            .Select(address => address.HostAddress ?? string.Empty)
            .Order(StringComparer.Ordinal);
        return $"{network.NetworkHandle}|{link?.InterfaceName}|{string.Join(",", addresses)}";
    }

    // The strict host and whether private DNS is in use on one link.
    private static (string? Host, bool? Active) PrivateDns(LinkProperties? link)
    {
        if (link is null || !OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            return (null, null);
        }

        var host = link.PrivateDnsServerName;
        return (string.IsNullOrEmpty(host) ? null : host, link.IsPrivateDnsActive);
    }

    private static int Rank(NetworkCapabilities capabilities)
    {
        if (capabilities.HasTransport(TransportType.Ethernet))
        {
            return 0;
        }

        if (capabilities.HasTransport(TransportType.Wifi))
        {
            return 1;
        }

        return capabilities.HasTransport(TransportType.Cellular) ? 2 : 3;
    }

    private static string Name(NetworkCapabilities capabilities)
    {
        if (capabilities.HasTransport(TransportType.Ethernet))
        {
            return "ethernet";
        }

        if (capabilities.HasTransport(TransportType.Wifi))
        {
            return "Wi-Fi";
        }

        return capabilities.HasTransport(TransportType.Cellular) ? "mobile" : "other";
    }
}
