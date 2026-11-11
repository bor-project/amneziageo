using System.Net;
using Android.App;
using Android.Net;
using AmneziaGeo.Android.Engine;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using ParcelFileDescriptor = Android.OS.ParcelFileDescriptor;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Confines hello to the VPN network of a configuration.
/// </summary>
internal static class TunnelHello
{
    internal static bool Reaches(string text, IPAddress address) =>
        Network(text) is { } network && AndroidNetworks.Carries(Application.Context, network, address) == true;

    internal static Task<HelloReply> AskAsync(string text, ServiceTarget point, CancellationToken ct) =>
        ServerHello.AskAsync(point, (context, token) => TunnelServiceConnection.ConnectAsync(context.DnsEndPoint,
            (address, socket, _) =>
            {
                if (Network(text) is not { } network || AndroidNetworks.Carries(Application.Context, network, address) != true)
                {
                    return ValueTask.FromResult(false);
                }

                try
                {
                    using var copy = ParcelFileDescriptor.FromFd(socket.Handle.ToInt32());
                    if (copy?.FileDescriptor is not { } descriptor)
                    {
                        return ValueTask.FromResult(false);
                    }

                    network.BindSocket(descriptor);
                    return ValueTask.FromResult(true);
                }
                catch (Java.Lang.Exception ex)
                {
                    throw new IOException("The VPN network did not bind the service socket.", ex);
                }
            }, token), ct);

    private static Network? Network(string text) => AndroidNetworks.Tunnel(Application.Context,
        TunnelInbound.Hosts(WgConfigEditor.GetAddresses(text)).Select(IPAddress.Parse).ToArray());
}
