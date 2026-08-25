using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Routing;
using Java.Net;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// Tunnel lifecycle stage reported to the head.
/// </summary>
public enum VpnStage
{
    Connecting,
    Connected,
    Disconnected,
    Failed,
}

/// <summary>
/// Hosts the AmneziaWG tunnel over Android VpnService: builds the tun, applies the UAPI config to
/// amneziawg-go, and protects the handshake socket. Runs in its own process, so what stays in memory
/// behind a closed window is the tunnel alone. Raises the last session by itself where the system starts
/// it with a bare intent: always-on, a boot, and a process the system killed all arrive that way.
/// </summary>
[Service(
    Name = "org.amneziageo.android.GeoVpnService",
    Permission = "android.permission.BIND_VPN_SERVICE",
    Exported = false,
    Process = ":vpn",
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
[IntentFilter(new[] { "android.net.VpnService" })]
public sealed class GeoVpnService : VpnService
{
    /// <summary>
    /// Start action carrying the config text and session name.
    /// </summary>
    public const string ActionConnect = "org.amneziageo.android.CONNECT";

    /// <summary>
    /// Start action tearing the tunnel down.
    /// </summary>
    public const string ActionDisconnect = "org.amneziageo.android.DISCONNECT";

    /// <summary>
    /// Config text extra key.
    /// </summary>
    public const string ExtraConfig = "config";

    /// <summary>
    /// Session name extra key.
    /// </summary>
    public const string ExtraName = "name";

    /// <summary>
    /// Per-app split mode extra: "include" (only these apps tunneled) or "exclude" (these apps bypass).
    /// </summary>
    public const string ExtraAppMode = "app-mode";

    /// <summary>
    /// Per-app package-name list extra.
    /// </summary>
    public const string ExtraAppList = "app-list";

    /// <summary>
    /// Tunnel MTU extra; absent or 0 takes the MTU from the config text.
    /// </summary>
    public const string ExtraMtu = "mtu";

    /// <summary>
    /// WebSocket front extra: a host or a whole wss:// URL. Absent leaves the tunnel on plain UDP.
    /// </summary>
    public const string ExtraWsHost = "ws-host";

    /// <summary>
    /// WebSocket front port extra.
    /// </summary>
    public const string ExtraWsPort = "ws-port";

    /// <summary>
    /// IPv6 opt-in extra. Off by default: a peer that hands out an address but routes no IPv6 turns every
    /// v6-capable name into a stall, and a family the tun does not carry is unreachable rather than leaked.
    /// </summary>
    public const string ExtraIpv6 = "ipv6";

    private const string ChannelId = "amneziageo.vpn";
    private const int NotificationId = 1001;
    private const int DefaultMtu = 1420;
    private const string DefaultDns = "1.1.1.1";
    private const string ProxyHost = "127.0.0.1";
    private const int ReportIntervalMs = 15_000;
    private const int LinkIntervalMs = 5_000;
    private const int HandshakeWaitSeconds = 30;
    private const int HandshakePollMs = 500;
    private const int KeepaliveSeconds = 25;
    private const int TcpProtocol = 6;
    private const int ExitDelayMs = 1_000;

    // Ends the process after the service is gone. An empty cached process keeps the whole runtime resident, and the
    // head reads a live tunnel off the process list.
    private static readonly Handler _exit = new(Looper.MainLooper!);

    private int _handle = -1;
    private int _proxyPort;
    private WsCarrier? _carrier;
    private ProxyRelay? _relay;
    private LocalProxyServer? _proxy;
    private VpnBridge.Listener? _proxySettings;
    private VpnBridge.Listener? _probes;
    private CancellationTokenSource? _reports;
    private CancellationTokenSource? _keepalive;
    private VpnBridge.Listener? _queries;
    private VpnBridge.Listener? _stops;
    private ConnectivityManager.NetworkCallback? _underlay;
    private List<string> _carved = [];
    private int _reraising;

    // The ladder a link that has stopped carrying is repaired by. This platform hands the engine a tun and a
    // config and takes nothing back: neither the socket nor the endpoint of a running session can be moved from
    // here, so the one repair left is raising the session again - and the ladder is what keeps that from becoming
    // a loop, spacing the attempts and standing down when they stop helping.
    private readonly LinkRecovery _recovery = new([RecoveryStep.Restart]);
    private VpnStage _stage = VpnStage.Disconnected;
    private string? _detail;
    private string? _reason;

    /// <inheritdoc/>
    public override void OnCreate()
    {
        base.OnCreate();
        _queries = new VpnBridge.Listener { Handler = _ => Publish(_stage, _detail, _reason) };
        VpnBridge.Listen(this, _queries, VpnBridge.ActionQuery);
        _stops = new VpnBridge.Listener { Handler = _ => Stop() };
        VpnBridge.Listen(this, _stops, VpnBridge.ActionStop);
        _proxySettings = new VpnBridge.Listener { Handler = _ => ApplyProxy() };
        VpnBridge.Listen(this, _proxySettings, VpnBridge.ActionProxy);
        _probes = new VpnBridge.Listener { Handler = _ => RunProbe() };
        VpnBridge.Listen(this, _probes, VpnBridge.ActionProbe);
        WatchUnderlay();
    }

    /// <inheritdoc/>
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // A connect that lands inside the exit window keeps the process.
        _exit.RemoveCallbacksAndMessages(null);
        if (intent?.Action == ActionDisconnect)
        {
            Stop();
            return StartCommandResult.NotSticky;
        }

        // Always-on starts the tunnel with a bare intent and the head is not there to fill it: what the last
        // connect ran on comes off the disk instead.
        var carried = FromIntent(intent);
        var request = carried ?? VpnBridge.ReadRequest();
        if (request is null)
        {
            if (intent?.Action == ActionConnect)
            {
                Teardown(VpnStage.Failed, "no config", nameof(ConnectFailureReason.ConfigMissing));
            }
            else
            {
                // The user has no session to raise: a failure here would only make the system try again.
                StopSelf();
            }

            return StartCommandResult.NotSticky;
        }

        if (carried is not null)
        {
            VpnBridge.WriteRequest(carried);
        }

        if (!StartForegroundNotification(request.Name))
        {
            Teardown(VpnStage.Failed, "foreground refused", nameof(ConnectFailureReason.ServiceStartFailed));
            return StartCommandResult.NotSticky;
        }

        Publish(VpnStage.Connecting, request.Name);

        // A connect the user asked for is a fresh start, whatever the previous session was being repaired for.
        _recovery.Reset();
        var plan = VpnBridge.ReadPlan();
        Task.Run(() => BringUpAsync(plan, request.Config, request.Name, request.AppMode, request.AppList,
            request.Mtu, request.Ipv6, request.WsHost, request.WsPort));
        return StartCommandResult.RedeliverIntent;
    }

    /// <inheritdoc/>
    public override void OnDestroy()
    {
        Release();

        // A service stopped from outside says goodbye itself, or the head keeps showing a tunnel that is gone.
        if (_stage is VpnStage.Connecting or VpnStage.Connected)
        {
            Publish(VpnStage.Disconnected, null);
        }

        if (_queries is not null)
        {
            UnregisterReceiver(_queries);
            _queries = null;
        }

        if (_stops is not null)
        {
            UnregisterReceiver(_stops);
            _stops = null;
        }

        if (_probes is not null)
        {
            UnregisterReceiver(_probes);
            _probes = null;
        }

        DropUnderlayWatch();

        base.OnDestroy();
        _exit.PostDelayed(Exit, ExitDelayMs);
    }

    /// <inheritdoc/>
    public override void OnRevoke()
    {
        Stop();
        base.OnRevoke();
    }

    // Follows the networks under the tunnel. Android fixes a tun's routes when it is established, so the carve-out
    // that keeps the device on its own segment goes stale as soon as the box joins another network.
    private void WatchUnderlay()
    {
        try
        {
            var manager = (ConnectivityManager?)GetSystemService(ConnectivityService);
            if (manager is null)
            {
                return;
            }

            var watch = new UnderlayWatch { Changed = OnUnderlayChanged };
            manager.RegisterDefaultNetworkCallback(watch);
            _underlay = watch;
        }
        catch (Java.Lang.Exception ex)
        {
            global::Android.Util.Log.Warn("GeoVpnService", "watching the network under the tunnel failed: " + ex);
        }
    }

    private void DropUnderlayWatch()
    {
        if (_underlay is null)
        {
            return;
        }

        try
        {
            ((ConnectivityManager?)GetSystemService(ConnectivityService))?.UnregisterNetworkCallback(_underlay);
        }
        catch (Java.Lang.Exception ex)
        {
            global::Android.Util.Log.Warn("GeoVpnService", "dropping the network watch failed: " + ex);
        }

        _underlay = null;
    }

    // Only a local network the tun swallows is worth acting on: a carve-out left over from the previous network costs
    // nothing, while a segment the tun covers takes away the router, the printer and everything else beside the box.
    private void OnUnderlayChanged()
    {
        if (_stage != VpnStage.Connected || _handle < 0)
        {
            return;
        }

        // The network under the tunnel is another one now, so a link that was being repaired is worth one
        // attempt straight away rather than at the end of a wait the old network earned.
        if (_recovery.Repairing)
        {
            _recovery.Reset();
            Reraise("the network under the tunnel changed while the link was being repaired; raising the session again");
            return;
        }

        _recovery.Reset();
        var swallowed = new List<string>(LocalSubnets()).FindAll(subnet => !_carved.Contains(subnet));
        if (swallowed.Count == 0)
        {
            return;
        }

        Reraise($"the device now sits on {string.Join(", ", swallowed)}, which this tunnel carries; raising the "
            + "session again so the network around the device stays reachable");
    }

    // Raises the running session again, one at a time. What asks for it differs - the device changed networks, or
    // the link stopped carrying - and what it takes does not.
    private bool Reraise(string why)
    {
        if (_stage != VpnStage.Connected || _handle < 0)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _reraising, 1) == 1)
        {
            return false;
        }

        var request = VpnBridge.ReadRequest();
        if (request is null)
        {
            Interlocked.Exchange(ref _reraising, 0);
            return false;
        }

        Report(why);
        Publish(VpnStage.Connecting, request.Name);
        var plan = VpnBridge.ReadPlan();
        _ = Task.Run(async () =>
        {
            try
            {
                await BringUpAsync(plan, request.Config, request.Name, request.AppMode, request.AppList, request.Mtu,
                    request.Ipv6, request.WsHost, request.WsPort).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _reraising, 0);
            }
        });

        return true;
    }

    /// <summary>
    /// Callback handing every change of the network under the tunnel to a delegate.
    /// </summary>
    private sealed class UnderlayWatch : ConnectivityManager.NetworkCallback
    {
        /// <summary>
        /// Called on each change.
        /// </summary>
        public Action? Changed { get; set; }

        /// <inheritdoc/>
        public override void OnAvailable(Network network) => Changed?.Invoke();

        /// <inheritdoc/>
        public override void OnLost(Network network) => Changed?.Invoke();

        /// <inheritdoc/>
        public override void OnLinkPropertiesChanged(Network network, LinkProperties linkProperties) => Changed?.Invoke();
    }

    private async Task BringUpAsync(GeoRoutingPlan plan, string config, string name, string? appMode, string[]? appList, int mtu, bool ipv6, string? wsHost, int wsPort)
    {
        try
        {
            // A connect on top of a live session takes the old one down first, or its relay and its sockets stay behind.
            Release();
            // Makes the peer answer on its own, so a quiet link is neither dropped by the provider nor mistaken
            // for a live one.
            var resolved = WgConfigEditor.EnsurePersistentKeepalive(ResolveEndpoint(config), KeepaliveSeconds);
            var carrier = StartCarrier(config, wsHost, wsPort);
            if (carrier is not null)
            {
                _carrier = carrier;
                resolved = WgConfigEditor.SetEndpoint(resolved, $"{ProxyHost}:{carrier.LocalPort}");
            }

            var uapi = WgQuickToUapi.Convert(resolved);
            if (uapi is null)
            {
                Teardown(VpnStage.Failed, "invalid config", nameof(ConnectFailureReason.ConfigInvalid));
                return;
            }

            var servers = DnsServers(resolved);
            // A relay the platform cannot hand to the applications decides nothing, so the tunnel is built the old
            // way instead: the lists become addresses at connect.
            var relay = Build.VERSION.SdkInt >= BuildVersionCodes.Q
                ? new ProxyRelay(plan, Protect, Report, ResolveOwner)
                : null;
            _proxyPort = relay?.Start() ?? 0;
            _relay = relay;
            var rules = await MaterializeAsync(plan, servers, _proxyPort > 0).ConfigureAwait(false);
            if (RouteBudget.Applies && rules.Tunneled.Count > RouteBudget.Max)
            {
                Report($"{rules.Tunneled.Count} routes are more than the {RouteBudget.Max} this android takes in one "
                    + "transaction; shorten the routing list or run it on android 10 or newer");
                Teardown(VpnStage.Failed, $"{rules.Tunneled.Count} of {RouteBudget.Max}",
                    nameof(ConnectFailureReason.TooManyRoutes));
                return;
            }

            var pfd = BuildTunnel(resolved, name, appMode, appList, mtu, ipv6, rules.Tunneled, servers, _proxyPort,
                out var establishError);
            if (pfd is null)
            {
                Teardown(VpnStage.Failed, establishError ?? "establish failed",
                    nameof(ConnectFailureReason.TunnelSetupFailed));
                return;
            }

            // What this tun leaves outside itself, held for as long as it lives: its route list is fixed now and the
            // networks under it are not.
            _carved = new List<string>(rules.Local);

            var tunFd = pfd.DetachFd();
            var handle = AwgEngine.TurnOn(Restrict(uapi, rules.Allowed), tunFd);
            if (handle < 0)
            {
                ParcelFileDescriptor.AdoptFd(tunFd)?.Close();
                Teardown(VpnStage.Failed, "engine start failed", nameof(ConnectFailureReason.EngineStartFailed));
                return;
            }

            _handle = handle;
            var socket = AwgEngine.GetSocketV4(handle);
            if (socket >= 0)
            {
                Protect(socket);
            }

            // The peer has to answer before the session counts as up: the tun and the engine start over a dead
            // server just as well, and the head would paint a live connection over nothing.
            var handshake = await WaitForHandshakeAsync(handle).ConfigureAwait(false);
            if (handshake <= 0)
            {
                // A session the user has already stopped is not a failure to report.
                if (_handle == handle)
                {
                    Teardown(VpnStage.Failed, "no handshake", nameof(ConnectFailureReason.NoHandshake));
                }

                return;
            }

            Publish(VpnStage.Connected, name);
            VpnBridge.PublishLink(this, handshake, LinkReading.Empty);
            var keepalive = new CancellationTokenSource();
            _keepalive = keepalive;
            // What the tunnel loses: the peer counters keep no trace of a packet that never arrived, so the far
            // end is echoed once a second - the peer where the server gives it an address, and otherwise the
            // resolvers, which the tunnel carries even where it carries nothing else of that subnet.
            var loss = new LinkLossProbe(LinkLossProbe.Targets(WgConfigEditor.GetAddresses(resolved), WgConfigEditor.GetDns(resolved)));
            _ = Task.Run(() => loss.RunAsync(keepalive.Token));
            _ = Task.Run(() => ReportLinkAsync(loss, keepalive.Token));
            if (relay is not null && _proxyPort > 0)
            {
                Report($"local proxy on {ProxyHost}:{_proxyPort} offered to the applications, "
                    + $"route ttl {plan.TtlSeconds} s");
                var reports = new CancellationTokenSource();
                _reports = reports;
                _ = Task.Run(() => ReportShareAsync(relay, reports.Token));
            }

            // The port the user set up: it opens with the tunnel, because everything it carries leaves through it.
            _proxy = new LocalProxyServer((IProxyOutbound?)relay ?? new DirectProxyOutbound(), Report);
            ApplyProxy();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("GeoVpnService", "bring-up failed: " + ex);
            Teardown(VpnStage.Failed, ex.Message, ReasonFor(ex));
        }
    }

    // Measures the destination the head left here. Only this process can excuse a socket from the tunnel, so a
    // run past it is handed over instead of attempted there.
    private void RunProbe()
    {
        var request = VpnBridge.ReadProbe();
        if (request is null)
        {
            return;
        }

        VpnBridge.ClearProbe();
        _ = Task.Run(async () =>
        {
            try
            {
                var options = new TargetProbeOptions(request.Target, request.Path, request.Taken, request.UploadUrl,
                    socket => Protect(socket.Handle.ToInt32()));
                var report = await TargetProbe.RunAsync(options, CancellationToken.None).ConfigureAwait(false);
                VpnBridge.WriteProbeResult(report.ToPayload());
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("GeoVpnService", "the probe failed: " + ex);
                VpnBridge.WriteProbeResult(TargetProbe
                    .Refused(request.Target, request.Path, ProbeVerdicts.PathUnavailable)
                    .ToPayload());
            }
        });
    }

    // Names the causes worth telling apart; the rest stay unclassified and get the generic notice.
    private static string ReasonFor(Exception ex)
    {
        return ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            ? nameof(ConnectFailureReason.EngineUnavailable)
            : nameof(ConnectFailureReason.Unknown);
    }

    /// <summary>
    /// The route lists a session is built from, and the local networks it keeps out of the tun.
    /// </summary>
    private readonly record struct Materialized(IReadOnlyList<string> Tunneled, IReadOnlyList<string> Allowed, IReadOnlyList<string> Local);

    // Turns the rules into the two address lists a tunnel is built from. Behind the relay the tun carries everything
    // and every destination is decided while the session runs, so no name is resolved and no range becomes a route at
    // connect - the mode only says where a destination no rule named belongs. A route table holds addresses and not
    // protocols, so this puts every datagram in the tunnel as well. Without the relay a name has to become an address
    // here and stay that way for the session: a route table cannot be edited once the tun is established.
    private static async Task<Materialized> MaterializeAsync(GeoRoutingPlan plan, IReadOnlyList<string> servers, bool relayed)
    {
        var proxy = new List<string>(plan.ProxyRoutes);
        var direct = relayed ? [] : new List<string>(plan.DirectRoutes);
        var block = new List<string>(plan.BlockRoutes);

        // The resolver rides the tunnel, so a query is answered where the traffic goes and not where the device sits.
        foreach (var server in servers)
        {
            proxy.Add(server + "/32");
        }

        // The segment the box sits on never enters the tun, or a tunnel that carries everything cuts the device off
        // its own network.
        var local = new List<string>(LocalSubnets());
        if (local.Count == 0)
        {
            Report("no local subnet found, the tun will carry the network the device sits on as well");
        }

        direct.AddRange(local);

        if (relayed)
        {
            Report($"{Mode(plan)} tunnel behind the local proxy: the tun carries everything, "
                + $"{plan.ProxyRoutes.Count + plan.DirectRoutes.Count} range(s) and "
                + $"{plan.ProxyDomains.Count + plan.DirectDomains.Count + plan.BlockDomains.Count} name rule(s) "
                + $"decided on contact, none at connect; a destination no rule names goes "
                + $"{(plan.FullTunnel ? "through the tunnel" : "direct")}");

            if (!plan.AllUdp)
            {
                Report("every datagram still rides the tunnel: the tun is what captures udp and it captures all of it");
            }

            // A blocked name is refused by the relay, but traffic that never reaches the relay is stopped by the
            // peer's address list alone, so blocked names become addresses even here.
            if (plan.BlockDomains.Count > 0)
            {
                var resolver = new GeoDomainRouteResolver();
                block.AddRange(await resolver.ResolveAsync(plan.BlockDomains).ConfigureAwait(false));
                Report($"{plan.BlockDomains.Count} blocked name(s) resolved to addresses as well, so what bypasses "
                    + "the relay is dropped too");
            }
        }
        else if (plan.HasDomains)
        {
            var clock = Stopwatch.StartNew();
            var resolver = new GeoDomainRouteResolver();
            var names = plan.ProxyDomains.Count + plan.DirectDomains.Count + plan.BlockDomains.Count;
            proxy.AddRange(await resolver.ResolveAsync(plan.ProxyDomains).ConfigureAwait(false));
            direct.AddRange(await resolver.ResolveAsync(plan.DirectDomains).ConfigureAwait(false));
            block.AddRange(await resolver.ResolveAsync(plan.BlockDomains).ConfigureAwait(false));
            Report($"{names} name rule(s) resolved to addresses in {clock.ElapsedMilliseconds} ms; "
                + "a name that moves to another address will no longer match");
        }

        var tunneled = SystemRoutes.Tunneled(plan.FullTunnel || relayed, proxy, direct, block);
        if (tunneled.Count == 0)
        {
            Report("the rules capture nothing, running the whole tunnel instead");
            tunneled = ["0.0.0.0/0"];
        }

        // A tun that carries everything needs a peer that carries everything, or a destination the rules never
        // named is dropped by the engine instead of leaving on a protected socket.
        var allowed = plan.FullTunnel || relayed || block.Count > 0 ? SystemRoutes.Allowed(block) : [];
        Report($"{Mode(plan)}: {tunneled.Count} route(s) into the tunnel, {block.Count} range(s) blocked, "
            + $"peer carries {(allowed.Count == 0 ? "what the config says" : allowed.Count + " range(s)")}");
        return new Materialized(tunneled, allowed, local);
    }

    private static string Mode(GeoRoutingPlan plan) => plan.FullTunnel ? "full" : "split";

    // Sets what the peer may carry so a blocked destination is dropped by the engine's own address lookup; an empty
    // list leaves the config's own one in place.
    private static string Restrict(string uapi, IReadOnlyList<string> allowed)
    {
        if (allowed.Count == 0)
        {
            return uapi;
        }

        var lines = new List<string>();
        var written = false;
        foreach (var line in uapi.Split('\n'))
        {
            if (!line.StartsWith("allowed_ip=", StringComparison.Ordinal) || line.Contains(':', StringComparison.Ordinal))
            {
                lines.Add(line);
                continue;
            }

            if (written)
            {
                continue;
            }

            foreach (var entry in allowed)
            {
                lines.Add("allowed_ip=" + entry);
            }

            written = true;
        }

        return string.Join('\n', lines);
    }

    // Reports a stage to the head and keeps it as the answer to a later query.
    private void Publish(VpnStage stage, string? detail, string? reason = null)
    {
        _stage = stage;
        _detail = detail;
        _reason = reason;
        // Only a running tunnel can be asked whether the system holds it as the always-on one.
        var alwaysOn = Build.VERSION.SdkInt >= BuildVersionCodes.Q && IsAlwaysOn;
        VpnBridge.Publish(this, stage, detail, reason, alwaysOn, alwaysOn && IsLockdownEnabled);
    }

    private static void Report(string text)
    {
        global::Android.Util.Log.Info("GeoVpnService", text);
        VpnBridge.PublishTrace(global::Android.App.Application.Context, text);
    }

    // The tun captures the routes it is given; behind the relay that is everything but the local segment, and what
    // leaves on the physical path does so on a protected socket. A family the tunnel does not carry is left off the
    // tun altogether - the applications then get an unreachable address family instead of a silent stall, and the
    // VPN holds every uid, so nothing slips out beside it.
    private ParcelFileDescriptor? BuildTunnel(
        string config,
        string name,
        string? appMode,
        string[]? appList,
        int mtu,
        bool ipv6,
        IReadOnlyList<string> routes,
        IReadOnlyList<string> servers,
        int proxyPort,
        out string? error)
    {
        error = null;
        try
        {
            var builder = new Builder(this);
            builder.SetSession(name);

            foreach (var address in WgConfigEditor.GetAddresses(config))
            {
                if (!ipv6 && IsIpv6(address))
                {
                    continue;
                }

                var (ip, prefix) = SplitCidr(address);
                builder.AddAddress(ip, prefix);
            }

            foreach (var route in routes)
            {
                var (ip, prefix) = SplitCidr(route);
                builder.AddRoute(ip, prefix);
            }

            if (ipv6)
            {
                builder.AddRoute("::", 0);
            }

            foreach (var server in servers)
            {
                builder.AddDnsServer(server);
            }

            // The saved per-config MTU wins; without one the config text decides.
            var configMtu = WgConfigEditor.GetMtu(config);
            builder.SetMtu(mtu > 0 ? mtu : configMtu > 0 ? configMtu : DefaultMtu);

            // Hands the applications a proxy of our own: a request then arrives as a name, before the client has
            // resolved it, which is the only per-destination signal this platform gives a session in progress.
            var proxy = proxyPort > 0 && Build.VERSION.SdkInt >= BuildVersionCodes.Q
                ? ProxyInfo.BuildDirectProxy(ProxyHost, proxyPort)
                : null;
            if (proxy is not null)
            {
                builder.SetHttpProxy(proxy);
            }

            ApplyAppSplit(builder, appMode, appList);

            builder.SetBlocking(true);

            return builder.Establish();
        }
        catch (Java.Lang.Exception ex)
        {
            global::Android.Util.Log.Error("GeoVpnService", "establish failed: " + ex);
            error = ex.GetType().Name;
            return null;
        }
    }

    private static bool IsIpv6(string cidr) => cidr.Contains(':', StringComparison.Ordinal);

    /// <summary>
    /// Lists the IPv4 networks the physical interfaces sit on, as CIDRs.
    /// </summary>
    public static IEnumerable<string> LocalSubnets()
    {
        var found = new List<string>();
        try
        {
            foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || adapter.Name.StartsWith("tun", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    var cidr = Subnet(unicast);
                    if (cidr is not null && !found.Contains(cidr))
                    {
                        found.Add(cidr);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("GeoVpnService", "reading local subnets failed: " + ex);
        }

        return found;
    }

    /// <summary>
    /// Addresses of this device a client on the same network points at the local proxy. The connectivity service
    /// answers for every link, where the interface list an application reads itself may hold none of them; a
    /// tunnel of ours is left out, its address answers to nobody on this network.
    /// </summary>
    public static IReadOnlyList<string> ReachableAddresses()
    {
        var links = new List<LocalProxyServer.AdapterView>();
        try
        {
            if (Application.Context.GetSystemService(Context.ConnectivityService) is ConnectivityManager manager)
            {
                foreach (var network in manager.GetAllNetworks())
                {
                    var link = Link(manager, network);
                    if (link is not null)
                    {
                        links.Add(link);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("GeoVpnService", "reading reachable addresses failed: " + ex);
        }

        var offered = LocalProxyServer.Usable(links);
        return offered.Count > 0 ? offered : LocalProxyServer.UsableAddresses();
    }

    // One network as the address pick reads it; null for a tunnel and for a network that carries no address.
    private static LocalProxyServer.AdapterView? Link(ConnectivityManager manager, Network network)
    {
        if (manager.GetNetworkCapabilities(network) is not { } capabilities
            || capabilities.HasTransport(TransportType.Vpn)
            || manager.GetLinkProperties(network) is not { } properties)
        {
            return null;
        }

        var addresses = new List<System.Net.IPAddress>();
        foreach (var entry in properties.LinkAddresses)
        {
            if (System.Net.IPAddress.TryParse(entry.Address?.HostAddress ?? string.Empty, out var address))
            {
                addresses.Add(address);
            }
        }

        return addresses.Count > 0
            ? new LocalProxyServer.AdapterView(NetworkInterfaceType.Ethernet,
                properties.Routes.Any(route => route.IsDefaultRoute), addresses)
            : null;
    }

    private static string? Subnet(UnicastIPAddressInformation unicast)
    {
        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        // /31 and /32 name a host and /0 the whole internet; neither is a network the device shares with anything.
        var prefix = unicast.PrefixLength;
        if (prefix is <= 0 or >= 31 || !GeoIpRanges.TryToNumeric(unicast.Address, out var value))
        {
            return null;
        }

        var network = value & (uint.MaxValue << (32 - prefix));

        // APIPA 169.254/16 is a link with nothing behind it.
        if ((network & 0xFFFF0000u) == 0xA9FE0000u)
        {
            return null;
        }

        return GeoIpRanges.Format(network) + "/" + prefix;
    }

    // Names the application behind a loopback connection to the proxy; null when the system will not tell.
    private string? ResolveOwner(System.Net.IPEndPoint peer)
    {
        try
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.Q || _proxyPort == 0)
            {
                return null;
            }

            var manager = (ConnectivityManager?)GetSystemService(ConnectivityService);
            var local = new InetSocketAddress(peer.Address.ToString(), peer.Port);
            var remote = new InetSocketAddress(ProxyHost, _proxyPort);
            var uid = manager?.GetConnectionOwnerUid(TcpProtocol, local, remote) ?? -1;
            if (uid < 0)
            {
                return null;
            }

            var packages = PackageManager?.GetPackagesForUid(uid);
            return packages is { Length: > 0 } ? packages[0] : "uid:" + uid;
        }
        catch (Java.Lang.Exception)
        {
            return null;
        }
    }

    // Logs what the relay carried against what the tunnel carried, so the relayed share can be judged.
    private async Task ReportShareAsync(ProxyRelay relay, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ReportIntervalMs, ct).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                return;
            }

            var handle = _handle;
            if (handle < 0)
            {
                return;
            }

            VpnBridge.WriteSessions(relay.Sessions());
            var tunnel = TunnelBytes(AwgEngine.GetConfig(handle));
            var share = tunnel > 0 ? relay.Bytes * 100 / tunnel : 0;
            Report($"{relay.Snapshot()}; tunnel {tunnel / 1024} KiB, relayed {share}%");
        }
    }

    // Waits for the peer's first answer, the only proof the session carries anything; 0 when none comes or the
    // session is gone.
    private async Task<long> WaitForHandshakeAsync(int handle)
    {
        for (var attempt = 0; attempt < HandshakeWaitSeconds * 1000 / HandshakePollMs; attempt++)
        {
            if (_handle != handle)
            {
                return 0;
            }

            var seen = PeerHandshake(AwgEngine.GetConfig(handle));
            if (seen > 0)
            {
                return seen;
            }

            await Task.Delay(HandshakePollMs).ConfigureAwait(false);
        }

        return 0;
    }

    // Tells the head when the peer last answered, what the link carries, and how often the session is
    // re-established, so a tunnel that is up but dead shows as such there.
    private async Task ReportLinkAsync(LinkLossProbe loss, CancellationToken ct)
    {
        var meter = new LinkMeter();
        var reported = LinkReading.Empty;
        var handshake = 0L;
        var lastRx = -1L;
        var lastTx = -1L;
        var gaveUp = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(LinkIntervalMs, ct).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                return;
            }

            var handle = _handle;
            if (handle < 0)
            {
                return;
            }

            var uapi = AwgEngine.GetConfig(handle);
            var seen = PeerHandshake(uapi);
            var (rx, tx) = PeerBytes(uapi);
            var reading = meter.Sample(rx, tx, seen, loss.Percent, loss.RttMs);
            var moved = new LinkSample(tx > lastTx, rx > lastRx, loss.Percent, reading.HandshakesPerMinute,
                seen > 0 ? (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seen) : 0);
            lastRx = rx;
            lastTx = tx;

            // A session that has not been answered yet is still coming up, and the ladder judges nothing until it is.
            if (seen > 0
                && _recovery.Sample(moved, System.Environment.TickCount64) is not null
                && Reraise($"{_recovery.Reason}; raising the session again (attempt {_recovery.Attempt})"))
            {
                return;
            }

            if (_recovery.GivenUp && !gaveUp)
            {
                gaveUp = true;
                Report($"{_recovery.Reason}, and {_recovery.Attempt} attempts to raise the session again did not "
                    + "bring it back; nothing further is tried until the network changes or you connect again");
            }

            if (seen == handshake && !reading.DiffersFrom(reported))
            {
                continue;
            }

            handshake = seen;
            reported = reading;
            VpnBridge.PublishLink(this, seen, reading);
        }
    }

    // What the peer has carried, received and sent apart.
    private static (long Rx, long Tx) PeerBytes(string? uapi)
    {
        var rx = 0L;
        var tx = 0L;
        foreach (var line in (uapi ?? string.Empty).Split('\n'))
        {
            if (line.StartsWith("rx_bytes=", StringComparison.Ordinal)
                && long.TryParse(line[(line.IndexOf('=') + 1)..].Trim(), out var received))
            {
                rx += received;
            }
            else if (line.StartsWith("tx_bytes=", StringComparison.Ordinal)
                && long.TryParse(line[(line.IndexOf('=') + 1)..].Trim(), out var sent))
            {
                tx += sent;
            }
        }

        return (rx, tx);
    }

    // The peer's last handshake in unix seconds; 0 before it has ever answered.
    private static long PeerHandshake(string? uapi)
    {
        foreach (var line in (uapi ?? string.Empty).Split('\n'))
        {
            if (line.StartsWith("last_handshake_time_sec=", StringComparison.Ordinal)
                && long.TryParse(line[(line.IndexOf('=') + 1)..].Trim(), out var seconds))
            {
                return seconds;
            }
        }

        return 0;
    }

    // Sums what the peer has carried in both directions.
    private static long TunnelBytes(string? uapi)
    {
        var total = 0L;
        foreach (var line in (uapi ?? string.Empty).Split('\n'))
        {
            if (!line.StartsWith("rx_bytes=", StringComparison.Ordinal) && !line.StartsWith("tx_bytes=", StringComparison.Ordinal))
            {
                continue;
            }

            if (long.TryParse(line[(line.IndexOf('=') + 1)..].Trim(), out var value))
            {
                total += value;
            }
        }

        return total;
    }

    // The resolvers handed to the applications: the config's IPv4 servers, or a public one when it names none.
    private static IReadOnlyList<string> DnsServers(string config)
    {
        var servers = new List<string>();
        foreach (var server in WgConfigEditor.GetDns(config))
        {
            if (System.Net.IPAddress.TryParse(server.Trim(), out var parsed)
                && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                servers.Add(parsed.ToString());
            }
        }

        if (servers.Count == 0)
        {
            servers.Add(DefaultDns);
        }

        return servers;
    }

    // Restricts the tunnel to (or excludes) the given app packages: "include" = only these apps use the tunnel,
    // "exclude" = every app but these. A stale/uninstalled package is skipped so it cannot fail establish. An
    // allow list carries this application too: the relay serves the proxy the tunnel offers, and left off the list
    // it sends every proxied byte beside the tunnel instead of into it.
    private void ApplyAppSplit(Builder builder, string? mode, string[]? packages)
    {
        if (packages is not { Length: > 0 } || string.IsNullOrEmpty(mode))
        {
            return;
        }

        var exclude = string.Equals(mode, "exclude", StringComparison.Ordinal);
        // The relay names the application behind every connection, so the named ones ride the tunnel from there and
        // the tun keeps carrying them all. An allow list here would send every other application past every rule.
        if (!exclude && _proxyPort > 0)
        {
            Report($"{packages.Length} named application(s) ride the tunnel by connection, not by an allow list");
            return;
        }

        var applied = 0;
        foreach (var package in packages)
        {
            if (string.IsNullOrWhiteSpace(package))
            {
                continue;
            }

            if (Listed(builder, exclude, package))
            {
                applied++;
            }
            else
            {
                Report($"the routing list names {package}, which is not installed here");
            }
        }

        if (!exclude && applied > 0 && PackageName is { Length: > 0 } self && Listed(builder, false, self))
        {
            Report($"{applied} application(s) ride the tunnel, and this one with them to carry their proxy");
        }
    }

    // Puts one package on the builder's allow or deny list; false when it is not installed here.
    private static bool Listed(Builder builder, bool exclude, string package)
    {
        try
        {
            if (exclude)
            {
                builder.AddDisallowedApplication(package);
            }
            else
            {
                builder.AddAllowedApplication(package);
            }

            return true;
        }
        catch (global::Android.Content.PM.PackageManager.NameNotFoundException)
        {
            return false;
        }
    }

    // The websocket the tunnel is carried inside when the configuration asks for one. The front is resolved
    // here, while the machine still answers lookups of its own, and the carrier's socket is excused from the
    // tunnel, or it would be asked to carry itself.
    private WsCarrier? StartCarrier(string config, string? host, int port)
    {
        if (host is null && port <= 0)
        {
            return null;
        }

        var endpoint = WgConfigEditor.GetEndpoint(config) ?? string.Empty;
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetPort))
        {
            Report("this configuration asks to be carried inside a websocket, but its Endpoint names no port");
            return null;
        }

        var front = WsEndpoint.Parse(host, port, endpoint[..colon].Trim('[', ']'));
        var address = ResolveHostV4(front.Host);
        if (front.Port <= 0 || address is null || !System.Net.IPAddress.TryParse(address, out var parsed))
        {
            Report($"the websocket front {front.Host}:{front.Port} has no address to dial");
            return null;
        }

        var carrier = WsCarrier.Start(front, parsed, targetPort, socket => Protect(socket.Handle.ToInt32()),
            (message, ex) => Report(ex is null ? message : $"{message}: {ex.Message}"));
        Report($"the tunnel is carried inside a websocket to {front.Host}:{front.Port}; the engine dials it on {ProxyHost}:{carrier.LocalPort}");
        return carrier;
    }

    private static string ResolveEndpoint(string config)
    {
        var endpoint = WgConfigEditor.GetEndpoint(config);
        if (string.IsNullOrEmpty(endpoint))
        {
            return config;
        }

        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0)
        {
            return config;
        }

        var host = endpoint[..colon];
        var port = endpoint[(colon + 1)..];
        if (System.Net.IPAddress.TryParse(host, out _))
        {
            return config;
        }

        var ip = ResolveHostV4(host);
        return ip is null ? config : WgConfigEditor.SetEndpoint(config, $"{ip}:{port}");
    }

    private static string? ResolveHostV4(string host)
    {
        foreach (var address in InetAddress.GetAllByName(host) ?? [])
        {
            if (address is Inet4Address v4)
            {
                return v4.HostAddress;
            }
        }

        return null;
    }

    private static (string Ip, int Prefix) SplitCidr(string cidr)
    {
        var slash = cidr.IndexOf('/');
        if (slash < 0)
        {
            return (cidr, cidr.Contains(':') ? 128 : 32);
        }

        var ip = cidr[..slash];
        return int.TryParse(cidr[(slash + 1)..], out var prefix) ? (ip, prefix) : (ip, ip.Contains(':') ? 128 : 32);
    }

    private bool StartForegroundNotification(string name)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            var channel = new NotificationChannel(ChannelId, "VPN", NotificationImportance.Low);
            manager?.CreateNotificationChannel(channel);
        }

        // A start the system refuses ends the service within seconds; failing here names the cause instead.
        try
        {
            var notification = BuildNotification(name);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
            {
                StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }

            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("GeoVpnService", "the foreground start was refused: " + ex);
            return false;
        }
    }

    private Notification BuildNotification(string name)
    {
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);
        return builder
            .SetContentTitle("AmneziaGeo")
            .SetContentText(name)
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
            .SetOngoing(true)
            .Build();
    }

    private static VpnRequest? FromIntent(Intent? intent)
    {
        var config = intent?.GetStringExtra(ExtraConfig);
        if (intent is null || string.IsNullOrEmpty(config))
        {
            return null;
        }

        return new VpnRequest(
            config,
            intent.GetStringExtra(ExtraName) ?? "AmneziaGeo",
            intent.GetStringExtra(ExtraAppMode),
            intent.GetStringArrayExtra(ExtraAppList),
            intent.GetIntExtra(ExtraMtu, 0),
            intent.GetBooleanExtra(ExtraIpv6, false),
            intent.GetStringExtra(ExtraWsHost),
            intent.GetIntExtra(ExtraWsPort, 0));
    }

    // The stop the user asked for: what it takes down must not come back with always-on or after a kill.
    private void Stop()
    {
        VpnBridge.ClearRequest();
        Teardown(VpnStage.Disconnected, null);
    }

    private void Teardown(VpnStage stage, string? detail, string? reason = null)
    {
        Release();
        Publish(stage, detail, reason);
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    private static void Exit()
    {
        global::Android.Util.Log.Info("GeoVpnService", "tunnel process exits, its memory goes back to the system");
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }

    // Moves the listener to the settings the head last wrote and tells it whether the ports were taken.
    private void ApplyProxy()
    {
        var proxy = _proxy;
        if (proxy is null)
        {
            return;
        }

        var options = VpnBridge.ReadProxy();
        proxy.Apply(options);
        VpnBridge.WriteProxyState(proxy.Running, proxy.Error);
        if (options.Enabled && !proxy.Running)
        {
            Report($"the local proxy did not start: {proxy.Error}");
        }
    }

    private void Release()
    {
        _reports?.Cancel();
        _reports?.Dispose();
        _reports = null;
        _keepalive?.Cancel();
        _keepalive?.Dispose();
        _keepalive = null;
        _relay?.Dispose();
        _relay = null;
        _proxy?.Dispose();
        _proxy = null;
        VpnBridge.WriteProxyState(false, string.Empty);
        VpnBridge.ClearSessions();
        _proxyPort = 0;
        _carrier?.Dispose();
        _carrier = null;
        if (_handle >= 0)
        {
            AwgEngine.TurnOff(_handle);
            _handle = -1;
        }
    }
}
