using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Probes the Windows transport runtime.
/// </summary>
internal static class WindowsTransportProxyProbe
{
    public static async Task<TransportProxyProbeResult> CheckAsync(CatalogTransportProxy record, IStateStore store, CancellationToken ct)
    {
        if (record.Proxy.Transport != "quic" || record.Legacy)
        {
            var target = !record.Legacy && record.Proxy.Transport == "websocket"
                ? await SelectedDestinationAsync(record, store, ct).ConfigureAwait(false) : null;
            return await TransportProxyProbes.CheckAsync(record, ct, target: target, legacyConfig: record.Legacy
                ? await store.GetConfigTextAsync(record.LegacyPublisher, ct).ConfigureAwait(false) : null).ConfigureAwait(false);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(record.Proxy.Host, ct).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(one => one.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            return address is null ? new TransportProxyProbeResult(TransportProxyAvailability.Unavailable, "proxy-no-address")
                : await XHttpProcess.ProbeQuicAsync(record.Proxy, address, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return new TransportProxyProbeResult(TransportProxyAvailability.Unavailable, "proxy-no-address");
        }
    }

    private static async Task<ProxyDestination?> SelectedDestinationAsync(CatalogTransportProxy record, IStateStore store, CancellationToken ct)
    {
        var state = await store.GetTransportProxyStateAsync(ct).ConfigureAwait(false);
        foreach (var selection in state.Selections.Where(one => one.Reference == record.Reference))
        {
            var text = await store.GetConfigTextAsync(selection.Config, ct).ConfigureAwait(false);
            var target = ConfigServices.Target(text);
            var port = ConfigServices.EndpointPort(text);
            if (target is not null && port is > 0 and <= 65535)
            {
                return new(target.Host, port);
            }
        }

        return null;
    }
}
