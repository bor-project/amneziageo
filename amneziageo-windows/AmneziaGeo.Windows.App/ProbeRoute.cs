using System.Net;
using System.Net.Sockets;

using AmneziaGeo.Ipc;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Measures one destination against the routing cache that decides where its address goes. That cache belongs to
/// the process running the tunnel, so a run without it can hold nothing and only reports where the rules stand.
/// </summary>
internal static class ProbeRoute
{
    /// <summary>
    /// Holds the target on the path asked for, measures it, and puts it back under the rules that own it.
    /// </summary>
    public static async Task<ProbeReport> RunAsync(
        RoutingCache? cache, string target, string path, string uploadUrl, CancellationToken ct)
    {
        var address = await AddressAsync(target, ct).ConfigureAwait(false);
        var held = Hold(cache, address, path);
        try
        {
            var options = new TargetProbeOptions(target, path, Taken(cache, address, path), uploadUrl);
            return await TargetProbe.RunAsync(options, ct).ConfigureAwait(false);
        }
        finally
        {
            Release(cache, address, held);
        }
    }

    // Holds the address on the path asked for; auto holds nothing, and without a live cache there is nothing to
    // hold it with.
    private static bool Hold(RoutingCache? cache, IPAddress? address, string path)
    {
        if (cache is null || address is null || path == ProbePaths.Auto)
        {
            return false;
        }

        cache.Note(address, path == ProbePaths.Tunnel ? RouteVerdict.Proxy : RouteVerdict.Direct);
        return true;
    }

    // Puts the address back under the rules that own it.
    private static void Release(RoutingCache? cache, IPAddress? address, bool held)
    {
        if (!held || cache is null || address is null)
        {
            return;
        }

        cache.Note(address, cache.Classify(address));
    }

    // Where the run went: the path forced, or - for auto - what the rules in force make of the address.
    private static string Taken(RoutingCache? cache, IPAddress? address, string path)
    {
        if (path != ProbePaths.Auto)
        {
            return path;
        }

        if (cache is null || address is null)
        {
            return "bypass, no live routing to ask";
        }

        return cache.Classify(address) switch
        {
            RouteVerdict.Proxy => "tunnel by rule",
            RouteVerdict.Direct => "bypass by rule",
            RouteVerdict.Block => "blocked by rule",
            _ => cache.Split ? "bypass by default" : "tunnel by default",
        };
    }

    // The address a target stands for, as the tunnel resolves it.
    private static async Task<IPAddress?> AddressAsync(string target, CancellationToken ct)
    {
        if (IPAddress.TryParse(target, out var parsed))
        {
            return parsed;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target, ct).ConfigureAwait(false);
            return addresses.FirstOrDefault(one => one.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException)
        {
            return null;
        }
    }
}
