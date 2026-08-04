using System.Diagnostics;
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
/// Tunnel lifecycle stage reported to the in-process agent.
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
/// amneziawg-go, and protects the handshake socket.
/// </summary>
[Service(
    Name = "org.amneziageo.android.GeoVpnService",
    Permission = "android.permission.BIND_VPN_SERVICE",
    Exported = false,
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

    /// <summary>
    /// Reports tunnel stage and an optional detail (session name or failure reason) to the agent.
    /// </summary>
    public static event Action<VpnStage, string?>? StateChanged;

    /// <summary>
    /// Receives what the session decided about routing, for the routing log.
    /// </summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>
    /// Rules the next session routes by. Set by the in-process agent, which lives in this same process, so the
    /// lists never have to survive a Binder transaction.
    /// </summary>
    public static GeoRoutingPlan Plan { get; set; } = GeoRoutingPlan.Full;

    private int _handle = -1;

    /// <inheritdoc/>
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
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
        StateChanged?.Invoke(VpnStage.Connecting, name);
        Task.Run(() => BringUpAsync(config, name, appMode, appList, mtu, ipv6));
        return StartCommandResult.NotSticky;
    }

    /// <inheritdoc/>
    public override void OnDestroy()
    {
        Release();
        base.OnDestroy();
    }

    /// <inheritdoc/>
    public override void OnRevoke()
    {
        Teardown(VpnStage.Disconnected, null);
        base.OnRevoke();
    }

    private async Task BringUpAsync(string config, string name, string? appMode, string[]? appList, int mtu, bool ipv6)
    {
        try
        {
            var resolved = ResolveEndpoint(config);
            var uapi = WgQuickToUapi.Convert(resolved);
            if (uapi is null)
            {
                Teardown(VpnStage.Failed, "invalid config");
                return;
            }

            var servers = DnsServers(resolved);
            var rules = await MaterializeAsync(Plan, servers).ConfigureAwait(false);
            var pfd = BuildTunnel(resolved, name, appMode, appList, mtu, ipv6, rules.Tunneled, servers);
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

            StateChanged?.Invoke(VpnStage.Connected, name);
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

    // Turns the rules into the two address lists a tunnel is built from. Names have to become addresses here and
    // stay that way for the session: a route table cannot be edited once the tun is established.
    private static async Task<Materialized> MaterializeAsync(GeoRoutingPlan plan, IReadOnlyList<string> servers)
    {
        var proxy = new List<string>(plan.ProxyRoutes);
        var direct = new List<string>(plan.DirectRoutes);
        var block = new List<string>(plan.BlockRoutes);

        // The resolver rides the tunnel, so a query is answered where the traffic goes and not where the device sits.
        foreach (var server in servers)
        {
            proxy.Add(server + "/32");
        }

        if (plan.HasDomains)
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

        var tunneled = SystemRoutes.Tunneled(plan.FullTunnel, proxy, direct, block);
        if (tunneled.Count == 0)
        {
            Report("the rules capture nothing, running the whole tunnel instead");
            tunneled = ["0.0.0.0/0"];
        }

        Report($"{(plan.FullTunnel ? "full" : "split")}: {tunneled.Count} route(s) into the tunnel, "
            + $"{block.Count} range(s) blocked");
        return new Materialized(tunneled, SystemRoutes.Allowed(block));
    }

    // Narrows what the peer may carry so a blocked destination is dropped by the engine's own address lookup.
    private static string Restrict(string uapi, IReadOnlyList<string> allowed)
    {
        if (allowed is [{ } single] && single == "0.0.0.0/0")
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

    private static void Report(string text)
    {
        global::Android.Util.Log.Info("GeoVpnService", text);
        Trace?.Invoke(text);
    }

    // The tun captures only what the rules send through the tunnel; everything else never enters it and leaves on
    // the physical path at full speed. A family the tunnel does not carry is left off the tun altogether - the
    // applications then get an unreachable address family instead of a silent stall, and the VPN holds every uid,
    // so nothing slips out beside it.
    private ParcelFileDescriptor? BuildTunnel(
        string config,
        string name,
        string? appMode,
        string[]? appList,
        int mtu,
        bool ipv6,
        IReadOnlyList<string> routes,
        IReadOnlyList<string> servers)
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
        StateChanged?.Invoke(stage, detail);
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    private void Release()
    {
        if (_handle >= 0)
        {
            AwgEngine.TurnOff(_handle);
            _handle = -1;
        }
    }
}
