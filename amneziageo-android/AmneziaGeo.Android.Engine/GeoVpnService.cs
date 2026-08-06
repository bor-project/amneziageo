using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using AmneziaGeo.Geo;
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
/// behind a closed window is the tunnel alone.
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
    private const int TcpProtocol = 6;
    private const int ExitDelayMs = 1_000;

    // Ends the process after the service is gone. An empty cached process keeps the whole runtime resident, and the
    // head reads a live tunnel off the process list.
    private static readonly Handler _exit = new(Looper.MainLooper!);

    private int _handle = -1;
    private int _proxyPort;
    private ProxyRelay? _relay;
    private CancellationTokenSource? _reports;
    private VpnBridge.Listener? _queries;
    private VpnBridge.Listener? _stops;
    private VpnStage _stage = VpnStage.Disconnected;
    private string? _detail;

    /// <inheritdoc/>
    public override void OnCreate()
    {
        base.OnCreate();
        _queries = new VpnBridge.Listener { Handler = _ => Publish(_stage, _detail) };
        VpnBridge.Listen(this, _queries, VpnBridge.ActionQuery);
        _stops = new VpnBridge.Listener { Handler = _ => Teardown(VpnStage.Disconnected, null) };
        VpnBridge.Listen(this, _stops, VpnBridge.ActionStop);
    }

    /// <inheritdoc/>
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // A connect that lands inside the exit window keeps the process.
        _exit.RemoveCallbacksAndMessages(null);
        if (intent?.Action == ActionDisconnect)
        {
            Teardown(VpnStage.Disconnected, null);
            return StartCommandResult.NotSticky;
        }

        var config = intent?.GetStringExtra(ExtraConfig);
        var name = intent?.GetStringExtra(ExtraName) ?? "AmneziaGeo";
        var appMode = intent?.GetStringExtra(ExtraAppMode);
        var appList = intent?.GetStringArrayExtra(ExtraAppList);
        var mtu = intent?.GetIntExtra(ExtraMtu, 0) ?? 0;
        var ipv6 = intent?.GetBooleanExtra(ExtraIpv6, false) ?? false;
        if (string.IsNullOrEmpty(config))
        {
            Teardown(VpnStage.Failed, "no config");
            return StartCommandResult.NotSticky;
        }

        StartForegroundNotification(name);
        Publish(VpnStage.Connecting, name);
        var plan = VpnBridge.ReadPlan();
        Task.Run(() => BringUpAsync(plan, config, name, appMode, appList, mtu, ipv6));
        return StartCommandResult.NotSticky;
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

        base.OnDestroy();
        _exit.PostDelayed(Exit, ExitDelayMs);
    }

    /// <inheritdoc/>
    public override void OnRevoke()
    {
        Teardown(VpnStage.Disconnected, null);
        base.OnRevoke();
    }

    private async Task BringUpAsync(GeoRoutingPlan plan, string config, string name, string? appMode, string[]? appList, int mtu, bool ipv6)
    {
        try
        {
            // A connect on top of a live session takes the old one down first, or its relay and its sockets stay behind.
            Release();
            var resolved = ResolveEndpoint(config);
            var uapi = WgQuickToUapi.Convert(resolved);
            if (uapi is null)
            {
                Teardown(VpnStage.Failed, "invalid config");
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
                Teardown(VpnStage.Failed, $"too many routes: {rules.Tunneled.Count} of {RouteBudget.Max}");
                return;
            }

            var pfd = BuildTunnel(resolved, name, appMode, appList, mtu, ipv6, rules.Tunneled, servers, _proxyPort);
            if (pfd is null)
            {
                Teardown(VpnStage.Failed, "establish failed");
                return;
            }

            var tunFd = pfd.DetachFd();
            var handle = AwgEngine.TurnOn(Restrict(uapi, rules.Allowed), tunFd);
            if (handle < 0)
            {
                ParcelFileDescriptor.AdoptFd(tunFd)?.Close();
                Teardown(VpnStage.Failed, "engine start failed");
                return;
            }

            _handle = handle;
            var socket = AwgEngine.GetSocketV4(handle);
            if (socket >= 0)
            {
                Protect(socket);
            }

            Publish(VpnStage.Connected, name);
            if (relay is not null && _proxyPort > 0)
            {
                Report($"local proxy on {ProxyHost}:{_proxyPort} offered to the applications, "
                    + $"route ttl {plan.TtlSeconds} s");
                var reports = new CancellationTokenSource();
                _reports = reports;
                _ = Task.Run(() => ReportShareAsync(relay, reports.Token));
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("GeoVpnService", "bring-up failed: " + ex);
            Teardown(VpnStage.Failed, ex.Message);
        }
    }

    /// <summary>
    /// The route lists a session is built from.
    /// </summary>
    private readonly record struct Materialized(IReadOnlyList<string> Tunneled, IReadOnlyList<string> Allowed);

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
        return new Materialized(tunneled, allowed);
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
    private void Publish(VpnStage stage, string? detail)
    {
        _stage = stage;
        _detail = detail;
        VpnBridge.Publish(this, stage, detail);
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
        int proxyPort)
    {
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
                    if (cidr is not null)
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

    private static string? Subnet(UnicastIPAddressInformation unicast)
    {
        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var prefix = unicast.PrefixLength;
        if (prefix is <= 0 or > 32 || !GeoIpRanges.TryToNumeric(unicast.Address, out var value))
        {
            return null;
        }

        var mask = prefix == 32 ? uint.MaxValue : uint.MaxValue << (32 - prefix);
        return GeoIpRanges.Format(value & mask) + "/" + prefix;
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

            var tunnel = TunnelBytes(AwgEngine.GetConfig(handle));
            var share = tunnel > 0 ? relay.Bytes * 100 / tunnel : 0;
            Report($"{relay.Snapshot()}; tunnel {tunnel / 1024} KiB, relayed {share}%");
        }
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
    // "exclude" = every app but these. A stale/uninstalled package is skipped so it cannot fail establish.
    private static void ApplyAppSplit(Builder builder, string? mode, string[]? packages)
    {
        if (packages is not { Length: > 0 } || string.IsNullOrEmpty(mode))
        {
            return;
        }

        var exclude = string.Equals(mode, "exclude", StringComparison.Ordinal);
        foreach (var package in packages)
        {
            if (string.IsNullOrWhiteSpace(package))
            {
                continue;
            }

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
            }
            catch (global::Android.Content.PM.PackageManager.NameNotFoundException)
            {
            }
        }
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

    private void StartForegroundNotification(string name)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            var channel = new NotificationChannel(ChannelId, "VPN", NotificationImportance.Low);
            manager?.CreateNotificationChannel(channel);
        }

        var notification = BuildNotification(name);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        }
        else
        {
            StartForeground(NotificationId, notification);
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

    private void Teardown(VpnStage stage, string? detail)
    {
        Release();
        Publish(stage, detail);
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    private static void Exit()
    {
        global::Android.Util.Log.Info("GeoVpnService", "tunnel process exits, its memory goes back to the system");
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }

    private void Release()
    {
        _reports?.Cancel();
        _reports?.Dispose();
        _reports = null;
        _relay?.Dispose();
        _relay = null;
        _proxyPort = 0;
        if (_handle >= 0)
        {
            AwgEngine.TurnOff(_handle);
            _handle = -1;
        }
    }
}
