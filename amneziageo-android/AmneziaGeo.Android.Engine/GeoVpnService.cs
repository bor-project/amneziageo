using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using AmneziaGeo.Geo;
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

    private const string ChannelId = "amneziageo.vpn";
    private const int NotificationId = 1001;
    private const int DefaultMtu = 1420;

    /// <summary>
    /// Reports tunnel stage and an optional detail (session name or failure reason) to the agent.
    /// </summary>
    public static event Action<VpnStage, string?>? StateChanged;

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
        if (string.IsNullOrEmpty(config))
        {
            Teardown(VpnStage.Failed, "no config");
            return StartCommandResult.NotSticky;
        }

        StartForegroundNotification(name);
        StateChanged?.Invoke(VpnStage.Connecting, name);
        Task.Run(() => BringUp(config, name));
        return StartCommandResult.NotSticky;
    }

    /// <inheritdoc/>
    public override void OnDestroy()
    {
        if (_handle >= 0)
        {
            AwgEngine.TurnOff(_handle);
            _handle = -1;
        }

        base.OnDestroy();
    }

    /// <inheritdoc/>
    public override void OnRevoke()
    {
        Teardown(VpnStage.Disconnected, null);
        base.OnRevoke();
    }

    private void BringUp(string config, string name)
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

            var pfd = BuildTunnel(resolved, name);
            if (pfd is null)
            {
                Teardown(VpnStage.Failed, "establish failed");
                return;
            }

            var tunFd = pfd.DetachFd();
            var handle = AwgEngine.TurnOn(uapi, tunFd);
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

    private ParcelFileDescriptor? BuildTunnel(string config, string name)
    {
        var builder = new Builder(this);
        builder.SetSession(name);

        foreach (var address in WgConfigEditor.GetAddresses(config))
        {
            var (ip, prefix) = SplitCidr(address);
            builder.AddAddress(ip, prefix);
        }

        var allowed = WgConfigEditor.GetAllowedIps(config);
        var routes = allowed.Count > 0 ? allowed : new List<string> { "0.0.0.0/0" };
        foreach (var route in routes)
        {
            var (ip, prefix) = SplitCidr(route);
            builder.AddRoute(ip, prefix);
        }

        foreach (var dns in WgConfigEditor.GetDns(config))
        {
            builder.AddDnsServer(dns);
        }

        var mtu = WgConfigEditor.GetMtu(config);
        builder.SetMtu(mtu > 0 ? mtu : DefaultMtu);
        builder.SetBlocking(true);

        return builder.Establish();
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
        if (_handle >= 0)
        {
            AwgEngine.TurnOff(_handle);
            _handle = -1;
        }

        StateChanged?.Invoke(stage, detail);
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }
}
