using System.Net;
using System.Net.Sockets;

using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;

using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runs the diagnostic checks and keeps their answers where the support archive picks them up. A run is stored
/// whole rather than logged: the capture floor is errors by default, so a healthy run would leave no trace at all.
/// </summary>
internal sealed class CheckService(AgentControl control, RuntimeInspector inspector, SqliteLogStore logStore, ILogger<CheckService> logger)
{
    /// <summary>
    /// Runs the ladder from the local gateway out to a download, and returns the measured legs with the verdict.
    /// Nothing here watches per-destination traffic, so the destination to time the tunnel against is the one
    /// the caller names.
    /// </summary>
    public async Task<IpcAck> ChannelAsync(IStateStore store, string config, string source, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NoConfigSelected"));
        }

        var text = await store.GetConfigTextAsync(config, ct).ConfigureAwait(false) ?? string.Empty;
        var connected = Connected(config);
        var (_, split) = await ActiveListAsync(store, config, ct).ConfigureAwait(false);
        var transport = await store.GetConfigTransportAsync(config, ct).ConfigureAwait(false);
        var carrier = Carrier(text, transport);
        var options = new ChannelProbeOptions(
            config,
            connected,
            LocalGateway.Find(),
            await ResolveAsync(carrier.Host, ct).ConfigureAwait(false),
            LinkLossProbe.PeerTargets(WgConfigEditor.GetAddresses(text)),
            LinkLossProbe.BeyondTargets(WgConfigEditor.GetDns(text)),
            !split,
            true,
            connected ? control.HandshakeAge : -1,
            connected ? control.Link.HandshakesPerMinute : -1,
            SourceHost: source,
            ConfiguredMtu: TunnelRunner.EffectiveMtu(transport?.Mtu ?? 0),
            CarrierPort: carrier.Port);

        var report = await ChannelProbe.RunAsync(options, ct).ConfigureAwait(false);
        await RecordAsync(report.Render(), report.Culprit.Length > 0, report.Advice, ct).ConfigureAwait(false);
        return new IpcAck(true, report.ToPayload());
    }

    /// <summary>
    /// Measures every saved server with the legs that cost only echoes, and names the one to be on.
    /// </summary>
    public async Task<IpcAck> ServersAsync(IStateStore store, CancellationToken ct)
    {
        var servers = new List<SweepServer>();
        foreach (var name in await store.ListConfigNamesAsync(ct).ConfigureAwait(false))
        {
            var text = await store.GetConfigTextAsync(name, ct).ConfigureAwait(false) ?? string.Empty;
            var transport = await store.GetConfigTransportAsync(name, ct).ConfigureAwait(false);
            var carrier = Carrier(text, transport);
            servers.Add(new SweepServer(
                name,
                await ResolveAsync(carrier.Host, ct).ConfigureAwait(false),
                carrier.Port,
                Connected(name)));
        }

        var live = servers.FirstOrDefault(server => server.Live);
        var full = live is not null && !(await ActiveListAsync(store, live.Name, ct).ConfigureAwait(false)).Split;
        var report = await ServerSweep
            .RunAsync(servers, new SweepOptions(LocalGateway.Find(), live is not null, full), ct)
            .ConfigureAwait(false);

        await RecordAsync(report.Render(), report.VerdictKey != CheckVerdicts.SweepBest, null, ct).ConfigureAwait(false);
        return new IpcAck(true, report.ToPayload());
    }

    /// <summary>
    /// Says why one address, name, application or category goes where it goes.
    /// </summary>
    public async Task<IpcAck> TargetAsync(IStateStore store, string config, string target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new IpcAck(false, "check-target requires a domain, an address, an app token or a geo rule");
        }

        var (list, split) = await ActiveListAsync(store, config, ct).ConfigureAwait(false);
        var held = Held(config);
        // An app rule here adds the addresses its application reaches; the rest of the list keeps deciding.
        var apps = list is { Apps.Count: > 0 } ? AppScope.Additive : AppScope.None;
        var report = await new TargetInspector(list, split, apps)
            .InspectAsync(target, config, new TargetProbes(address => held.GetValueOrDefault(address.ToString())), ct)
            .ConfigureAwait(false);

        await RecordAsync(report.Render(), report.VerdictKey != TargetVerdicts.Proxy, null, ct).ConfigureAwait(false);
        return new IpcAck(true, report.ToPayload());
    }

    // Stores the whole run in the checks table - the capture floor never reaches it - and puts its closing line
    // in the agent log too, where a reader following the tunnel will see it in context. An MTU that does not fit
    // the measured path is warned about on its own: it survives whoever the run blames.
    private async Task RecordAsync(string rendered, bool blamed, MtuAdvice? advice, CancellationToken ct)
    {
        logStore.AppendCheck(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rendered.TrimEnd());
        await logStore.FlushAsync(ct).ConfigureAwait(false);
        if (advice is not null)
        {
            logger.LogWarning("check: {Advice}", advice.Describe());
        }

        var closing = rendered.TrimEnd().Split('\n')[^1].Trim();
        if (blamed)
        {
            logger.LogWarning("check: {Verdict}", closing);
            return;
        }

        logger.LogInformation("check: {Verdict}", closing);
    }

    private bool Connected(string config)
    {
        return control.Running && string.Equals(control.RunningTarget ?? control.Target, config, StringComparison.Ordinal);
    }

    // The list the tunnel decides by: the one the running tunnel materialized, else the one the next connect uses.
    private static async Task<(RoutingList? List, bool Split)> ActiveListAsync(IStateStore store, string config, CancellationToken ct)
    {
        var listId = await store.GetActiveRoutingListIdAsync(config, ct).ConfigureAwait(false)
            ?? await store.GetSelectedRoutingListAsync(ct).ConfigureAwait(false);
        var list = listId is long id ? await store.GetRoutingListAsync(id, ct).ConfigureAwait(false) : null;
        var routing = listId is long settings ? await store.GetRoutingSettingsAsync(settings, ct).ConfigureAwait(false) : null;
        var geo = await store.GetActiveTunnelGeoAsync(config, ct).ConfigureAwait(false);
        return (list, list is not null ? !(routing?.UseGlobalProxy ?? false) : geo?.GeoSplit ?? false);
    }

    // The host the tunnel dials and the port to knock on: a websocket carrier stands at its own address, and
    // the endpoint in the config is only what the server hands the tunnel to behind it. Without a carrier the
    // port stays zero - AmneziaWG answers a real handshake and nothing else.
    private static (string Host, int Port) Carrier(string text, ConfigTransport? transport)
    {
        var endpoint = WgConfigEditor.GetEndpoint(text) ?? string.Empty;
        var colon = endpoint.LastIndexOf(':');
        var host = (colon > 0 ? endpoint[..colon] : endpoint).Trim('[', ']');
        if (transport?.UseWebSocket != true)
        {
            return (host, 0);
        }

        var ws = WsEndpoint.Parse(transport.WebSocketHost, transport.WebSocketPort, host);
        return (ws.Host, ws.Port);
    }

    // One address for a host, as the tunnel resolves it.
    private static async Task<string?> ResolveAsync(string host, CancellationToken ct)
    {
        if (host.Length == 0)
        {
            return null;
        }

        if (IPAddress.TryParse(host, out var parsed))
        {
            return parsed.ToString();
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses.FirstOrDefault(one => one.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException)
        {
            return null;
        }
    }

    // What the running tunnel holds per address, read from this process or from the service that owns it.
    private Dictionary<string, HeldRoute> Held(string config)
    {
        var map = new Dictionary<string, HeldRoute>(StringComparer.Ordinal);
        foreach (var entry in inspector.Held(config).Entries)
        {
            if (entry.Kind is "state" or "domain")
            {
                continue;
            }

            map[entry.Key] = new HeldRoute(Role(entry), $"held as {entry.Kind}, {entry.Value}");
        }

        return map;
    }

    // The role the installed path amounts to; the verdict answers for an entry that installed nothing.
    private static RoleToken Role(RuntimeInspector.CacheEntry entry)
    {
        return (entry.Path.Length > 0 ? entry.Path : entry.Kind) switch
        {
            AmneziaGeo.Ipc.LiveSession.PathTunnel or "proxy" => RoleToken.Proxy,
            AmneziaGeo.Ipc.LiveSession.PathDirect => RoleToken.Direct,
            AmneziaGeo.Ipc.LiveSession.PathBlock => RoleToken.Block,
            _ => RoleToken.None,
        };
    }
}
