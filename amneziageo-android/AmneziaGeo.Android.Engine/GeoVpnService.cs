using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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
    /// Package-name list extra for the applications the tunnel leaves alone: their sockets stay off the tun and
    /// the system shows them no vpn at all.
    /// </summary>
    public const string ExtraBypassApps = "bypass-apps";

    /// <summary>
    /// Tunnel MTU extra; absent or 0 takes the MTU from the config text.
    /// </summary>
    public const string ExtraMtu = "mtu";

    /// <summary>
    /// How the MTU is picked: 0 auto, 1 from the config text, 2 the size above.
    /// </summary>
    public const string ExtraMtuMode = "mtu-mode";

    /// <summary>
    /// WebSocket front extra: a host or a whole wss:// URL. Absent leaves the tunnel on plain UDP.
    /// </summary>
    public const string ExtraWsHost = "ws-host";

    /// <summary>
    /// WebSocket front port extra.
    /// </summary>
    public const string ExtraWsPort = "ws-port";

    /// <summary>
    /// WebSocket front extra: the front is the one the server of the config offers, and the token proves its keys.
    /// </summary>
    public const string ExtraWsOffered = "ws-offered";

    /// <summary>
    /// IPv6 opt-in extra. Off by default: a peer that hands out an address but routes no IPv6 turns every
    /// v6-capable name into a stall, and a family the tun does not carry is unreachable rather than leaked.
    /// </summary>
    public const string ExtraIpv6 = "ipv6";

    /// <summary>
    /// Уровень, на котором движок пишет о себе: молчит, только ошибки, каждое решение.
    /// </summary>
    public const string ExtraEngineLog = "engine-log";

    /// <summary>
    /// Whether a stream to a direct range leaves on a protected socket instead of riding the tunnel.
    /// </summary>
    public const string ExtraDirectTcp = "direct-tcp";

    /// <summary>
    /// Whether the network the device sits on stays inside the tun and leaves it on a protected socket.
    /// </summary>
    public const string ExtraLocalInTunnel = "local-in-tunnel";

    /// <summary>
    /// Whether the hot direct addresses are left outside the tun by name; API 33 and above.
    /// </summary>
    public const string ExtraExcludeRoutes = "exclude-routes";

    private const string ChannelId = "amneziageo.vpn";
    private const int NotificationId = 1001;
    private const string DefaultDns = "1.1.1.1";
    private const string ProxyHost = "127.0.0.1";
    private const int ReportIntervalMs = 15_000;
    private const int LinkIntervalMs = 5_000;
    // How long the tunnel may stay unvalidated before that is noted.
    private const int UnvalidatedNoteSeconds = 30;
    // Screen-off time below which waking is not noted, and how long after waking the session is looked at again.
    private const int WakeNoteMs = 60_000;
    private const int WakeFollowMs = 20_000;
    // How long renewed session keys wait for their first answer while the head is told no handshake.
    private const int RenewHoldMs = 20_000;
    private const int HandshakeWaitSeconds = 30;
    // How long the server of a configuration the head wants to move to is waited for.
    private const int SwitchWaitMs = 7_000;
    private const int HandshakePollMs = 500;
    private const int TrafficWaitSeconds = 20;
    private const int TrafficPollMs = 250;
    private const string HotFile = "hot-direct.txt";
    private const int HotMax = 1024;
    // Decided addresses carried into the next session, and the file they wait in.
    private const string LiveFile = "live-cache.txt";
    private const int LiveMax = 2048;
    private const int HotTtlSeconds = 3600;
    private const int KeepaliveSeconds = 25;
    private const int NameBudgetMs = 15_000;
    private const int TcpProtocol = 6;
    private const int OwnerOther = 0;
    private const int OwnerSelf = 1;
    private const int OwnerNamed = 2;
    private const int OwnerHoldMs = 3_000;
    private const int OwnersHeld = 4096;
    // How long an address refused by name waits before the engine takes it, so a burst of them costs one rebuild.
    private const int RefusedDelayMs = 1_000;
    private const int RefusedHeld = 1024;
    private const int ExitDelayMs = 1_000;

    // Ends the process after the service is gone. An empty cached process keeps the whole runtime resident, and the
    // head reads a live tunnel off the process list.
    private static readonly Handler _exit = new(Looper.MainLooper!);

    private readonly ConcurrentDictionary<int, string> _packages = new();
    private readonly ConcurrentDictionary<ulong, (int Verdict, long Until)> _owners = new();
    private readonly HashSet<string> _tunnelApps = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
    private string _verdicts = string.Empty;
    private int _refusing;
    private int _handle = -1;
    private int _proxyPort;
    private ConnectivityManager? _connectivity;
    private InetSocketAddress? _proxyEnd;
    private WsCarrier? _carrier;
    private ProxyRelay? _relay;
    private SessionReport? _routed;
    private LocalProxyServer? _proxy;
    private TunShape? _shape;
    private readonly object _swapGate = new();
    private IReadOnlyList<string> _excluded = [];
    private bool _liveTun;
    private int _ttlSeconds = 300;
    private VpnBridge.Listener? _proxySettings;
    private VpnBridge.Listener? _routeTtl;
    private VpnBridge.Listener? _probes;
    private VpnBridge.Listener? _cards;
    private VpnBridge.Listener? _switches;
    private CancellationTokenSource? _reports;
    private CancellationTokenSource? _keepalive;
    private VpnBridge.Listener? _queries;
    private VpnBridge.Listener? _stops;
    private ConnectivityManager.NetworkCallback? _underlay;

    // The network under the tunnel the session stands on.
    private string? _underKey;
    private List<string> _carved = [];

    // Set when the engine refused to decide on the packet, so the next raise carves the local network out again.
    private bool _localCarveForced;
    private int _reraising;

    // What takes the dial in flight back, and its failed attempts in a row as the head is told them.
    private CancellationTokenSource? _dial;
    private int _retry;

    // The dial in flight; the next one starts once it has ended.
    private readonly object _dialGate = new();
    private Task _dialing = Task.CompletedTask;
    private readonly object _releaseGate = new();

    // What ends the pause between two attempts, and the network the pause began on.
    private CancellationTokenSource? _wake;
    private string? _wakeKey;

    // The ladder a link that has stopped carrying is repaired by: another source port for the engine, then the
    // session raised again.
    private readonly LinkRecovery _recovery = new([RecoveryStep.Rebind, RecoveryStep.Restart]);

    // What the session went through since it came up, written where the head reads it for its archive.
    private readonly SessionMarks _session = new();

    // Server names and the IPv4 addresses they resolved to last.
    private static readonly ConcurrentDictionary<string, string> _resolvedHosts = new(StringComparer.OrdinalIgnoreCase);
    private VpnStage _stage = VpnStage.Disconnected;
    private string? _detail;
    private string? _reason;

    // The link last told to the head, told again to a head that asks after a restart.
    private long _linkHandshake;
    private LinkReading _linkReading = LinkReading.Empty;

    // Sleep since boot at the last look, the handshake it is counted from, and the sleep that stood at that handshake.
    private readonly object _sleepGate = new();
    private long _sleepSeen = -1;
    private long _answered;
    private long _sleepAtAnswer;

    // The handshake whose keys were renewed, and until when the head is told none.
    private long _renewedAfter;
    private long _renewUntil;

    // The age the config of the session renews its keys at, 0 when it names none.
    private int _rekeySeconds;

    // What the notes last said about the networks, kept across sessions so a raise does not repeat it.
    private string? _underNoted;
    private string? _privateDnsNoted;

    // When the tunnel was first seen unvalidated, and whether that is noted.
    private long _unvalidatedSince;
    private bool _unvalidatedNoted;

    // When the screen went off, on the clock that counts sleep and on the one that does not.
    private long _screenOffElapsed = -1;
    private long _screenOffUptime;
    private VpnBridge.Listener? _screen;

    /// <inheritdoc/>
    public override void OnCreate()
    {
        base.OnCreate();
        _queries = new VpnBridge.Listener { Handler = _ => Answer() };
        VpnBridge.Listen(this, _queries, VpnBridge.ActionQuery);
        _stops = new VpnBridge.Listener { Handler = _ => Stop() };
        VpnBridge.Listen(this, _stops, VpnBridge.ActionStop);
        _proxySettings = new VpnBridge.Listener { Handler = _ => ApplyProxy() };
        VpnBridge.Listen(this, _proxySettings, VpnBridge.ActionProxy);
        _routeTtl = new VpnBridge.Listener { Handler = _ => ApplyRouteTtl() };
        VpnBridge.Listen(this, _routeTtl, VpnBridge.ActionRouteTtl);
        _probes = new VpnBridge.Listener { Handler = _ => RunProbe() };
        VpnBridge.Listen(this, _probes, VpnBridge.ActionProbe);
        _cards = new VpnBridge.Listener { Handler = _ => RunCards() };
        VpnBridge.Listen(this, _cards, VpnBridge.ActionCards);
        _switches = new VpnBridge.Listener { Handler = _ => RunSwitch() };
        VpnBridge.Listen(this, _switches, VpnBridge.ActionSwitch);
        WatchUnderlay();
        _screen = new VpnBridge.Listener { Handler = OnScreen };
        VpnBridge.Listen(this, _screen, Intent.ActionScreenOff);
        VpnBridge.Listen(this, _screen, Intent.ActionScreenOn);
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

        // The dial this connect replaces is taken back before the head hears of the new one.
        Interlocked.Exchange(ref _dial, null)?.Cancel();
        if (carried is not null)
        {
            VpnBridge.WriteRequest(carried);
        }

        if (!StartForegroundNotification(request.Name))
        {
            Teardown(VpnStage.Failed, "foreground refused", nameof(ConnectFailureReason.ServiceStartFailed));
            return StartCommandResult.NotSticky;
        }

        _retry = 0;
        _session.Closed();
        KeepMarks();
        Publish(VpnStage.Connecting, request.Name);

        // A connect the user asked for is a fresh start, whatever the previous session was being repaired for.
        _recovery.Reset();
        Dial(VpnBridge.ReadPlan(), request, repair: false);
        return StartCommandResult.RedeliverIntent;
    }

    /// <inheritdoc/>
    public override void OnDestroy()
    {
        Interlocked.Exchange(ref _dial, null)?.Cancel();
        Release();

        // A service stopped from outside says goodbye itself, or the head keeps showing a tunnel that is gone.
        if (_stage is VpnStage.Connecting or VpnStage.Connected)
        {
            _session.Closed();
            KeepMarks();
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

        if (_proxySettings is not null)
        {
            UnregisterReceiver(_proxySettings);
            _proxySettings = null;
        }

        if (_routeTtl is not null)
        {
            UnregisterReceiver(_routeTtl);
            _routeTtl = null;
        }

        if (_probes is not null)
        {
            UnregisterReceiver(_probes);
            _probes = null;
        }

        if (_cards is not null)
        {
            UnregisterReceiver(_cards);
            _cards = null;
        }

        if (_switches is not null)
        {
            UnregisterReceiver(_switches);
            _switches = null;
        }

        if (_screen is not null)
        {
            UnregisterReceiver(_screen);
            _screen = null;
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
        WakeDial();
        if (_stage != VpnStage.Connected || _handle < 0)
        {
            return;
        }

        // Skips the callbacks of the tunnel's own network, of the same network under it, and of no network at all.
        var key = AndroidNetworks.Read(this).UnderKey;
        if (key is null
            || string.Equals(Interlocked.Exchange(ref _underKey, key), key, StringComparison.Ordinal)
            || key == NetworkSnapshot.NoNetwork)
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
        var shape = _shape;
        if (shape is null)
        {
            return;
        }

        // A mobile network has nobody beside the device on it.
        var mobile = MobileSubnets();
        var carved = _carved;
        var joined = new List<string>(LocalSubnets()).FindAll(subnet => !carved.Contains(subnet) && !mobile.Contains(subnet));
        var swallowed = SystemRoutes.Captured(shape.Routes, joined);
        if (swallowed.Count == 0)
        {
            return;
        }

        Recarve(swallowed);
    }

    // Takes the networks the device has joined out of the running tunnel; the session is raised again only where
    // that cannot be done under it.
    private void Recarve(IReadOnlyList<string> joined)
    {
        var sits = $"the device now sits on {string.Join(", ", joined)}, which this tunnel carries";
        lock (_swapGate)
        {
            var shape = _shape;
            var handle = _handle;
            if (shape is null || handle < 0)
            {
                return;
            }

            if (Refit(shape, handle, joined))
            {
                _carved = [.. _carved, .. joined];
                var kept = $"{sits}; it leaves the tunnel on its own and the session stays";
                Report(kept);
                Note("tunnel", kept);
                return;
            }
        }

        Reraise($"{sits}; raising the session again so the network around the device stays reachable");
    }

    // Hands the networks to the engine where it sends the local network out itself, and rebuilds the tun without
    // them otherwise.
    private bool Refit(TunShape shape, int handle, IReadOnlyList<string> joined)
    {
        if (shape.LocalInside)
        {
            var verdicts = new StringBuilder(_verdicts);
            foreach (var subnet in joined)
            {
                verdicts.Append(subnet).Append("=direct\n");
            }

            var spec = new StringBuilder(verdicts.ToString());
            foreach (var address in Refused())
            {
                spec.Append('\n').Append(address).Append("/32=block");
            }

            if (!AwgEngine.SetVerdicts(handle, spec.ToString()))
            {
                return false;
            }

            _verdicts = verdicts.ToString();
            return true;
        }

        var routes = SystemRoutes.Without(shape.Routes, joined);
        var next = shape with { Routes = routes };
        if (routes.Count == 0
            || (shape.ProxyPort == 0 && routes.Count > RouteBudget.Max)
            || !Swap(handle, next, _excluded, out _))
        {
            return false;
        }

        _shape = next;
        return true;
    }

    // Raises the running session again, one at a time. What asks for it differs - the device changed networks, or
    // the link stopped carrying - and what it takes does not.
    private bool Reraise(string why)
    {
        if (_stage != VpnStage.Connected || _handle < 0)
        {
            return false;
        }

        // Leaves the session standing while no network is under it.
        if (AndroidNetworks.Read(this).Under == NetworkSnapshot.NoNetwork)
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
        Note("tunnel", why);
        _session.Dropped(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        KeepMarks();
        Publish(VpnStage.Connecting, request.Name);
        Dial(VpnBridge.ReadPlan(), request, repair: true, () => Interlocked.Exchange(ref _reraising, 0));

        return true;
    }

    // Dials the session until it stands, fails on a cause another attempt does not get past, or is taken back.
    private void Dial(GeoRoutingPlan plan, VpnRequest request, bool repair, Action? done = null)
    {
        var dial = new CancellationTokenSource();
        Interlocked.Exchange(ref _dial, dial)?.Cancel();
        var ct = dial.Token;
        var steps = new DialSteps(
            () => AndroidNetworks.Read(this).Under != NetworkSnapshot.NoNetwork,
            _ => BringUpAsync(plan, request.Config, request.Name, request.AppMode, request.AppList, request.Mtu,
                request.MtuMode, request.Ipv6, request.WsHost, request.WsPort, request.WsOffered, request.EngineLog,
                request.DirectTcp, request.ExcludeRoutes, request.BypassApps, request.LocalInTunnel, repair, ct),
            PauseAsync,
            () => Tell($"this device is on no network, so {request.Name} is not dialled; it is dialled as soon as one is there"),
            () => Tell($"a network is there, so {request.Name} is dialled"),
            (failures, delay, outcome) => Retrying(request.Name, failures, delay, outcome, ct));
        lock (_dialGate)
        {
            var previous = _dialing;
            _dialing = Task.Run(() => DialAsync(previous, steps, done, ct));
        }
    }

    // Runs a dial once the one before it has ended, and ends the session on a cause no attempt gets past.
    private async Task DialAsync(Task previous, DialSteps steps, Action? done, CancellationToken ct)
    {
        try
        {
            await previous.ConfigureAwait(false);
            var end = await ConnectRetry.RunAsync(steps, ct).ConfigureAwait(false);
            if (end is null)
            {
                Release();
            }
            else if (!end.Value.Up && !ct.IsCancellationRequested)
            {
                Teardown(VpnStage.Failed, end.Value.Detail, end.Value.Reason.ToString());
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("GeoVpnService", "the dial failed: " + ex);
            if (!ct.IsCancellationRequested)
            {
                Teardown(VpnStage.Failed, ex.Message, nameof(ConnectFailureReason.Unknown));
            }
        }
        finally
        {
            done?.Invoke();
        }
    }

    // Takes down what the failed attempt left and tells the head the dial goes on.
    private void Retrying(string name, int failures, TimeSpan delay, DialOutcome outcome, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        Release();
        _retry = failures;
        Publish(VpnStage.Connecting, name);
        var cause = outcome.Detail.Length > 0 ? outcome.Detail : outcome.Reason.ToString();
        var when = delay > TimeSpan.Zero ? $"in {(int)delay.TotalSeconds} s" : "at once";
        Tell($"could not reach the server of {name}: {cause}; trying again {when}, attempt {failures + 1}");
    }

    // Waits the time out; a network under the device other than the one the wait began on ends it sooner.
    private async Task PauseAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        using (var wake = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            _wakeKey = AndroidNetworks.Read(this).UnderKey;
            _wake = wake;
            try
            {
                await Task.Delay(delay, wake.Token).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
            }
            finally
            {
                _wake = null;
            }
        }
    }

    // Ends the pause of the dial once the network under the device is another one.
    private void WakeDial()
    {
        var wake = _wake;
        if (wake is null)
        {
            return;
        }

        var key = AndroidNetworks.Read(this).UnderKey;
        if (key is null || string.Equals(key, _wakeKey, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            wake.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Writes a line of the dial to the routing log and to the journal.
    private static void Tell(string text)
    {
        Report(text);
        Note("tunnel", text);
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

    // Raises the session once and tells what the attempt ended with.
    private async Task<DialOutcome> BringUpAsync(GeoRoutingPlan plan, string config, string name, string? appMode, string[]? appList, int mtu, int mtuMode, bool ipv6, string? wsHost, int wsPort, bool wsOffered, int engineLog, bool directTcp, bool excludeRoutes, string[]? bypassApps, bool localInTunnel, bool repair, CancellationToken ct)
    {
        try
        {
            // A connect on top of a live session takes the old one down first, or its relay and its sockets stay behind.
            Release();
            // Makes the peer answer on its own, so a quiet link is neither dropped by the provider nor mistaken
            // for a live one.
            var resolved = WgConfigEditor.EnsurePersistentKeepalive(ResolveEndpoint(config), KeepaliveSeconds);
            // The size is read off the link to the server, which the carrier is about to hide behind the loopback.
            var underlay = resolved;
            var carrier = StartCarrier(config, wsHost, wsPort, wsOffered);
            if (carrier is not null)
            {
                _carrier = carrier;
                resolved = WgConfigEditor.SetEndpoint(resolved, $"{ProxyHost}:{carrier.LocalPort}");
            }

            var uapi = WgQuickToUapi.Convert(resolved);
            if (uapi is null)
            {
                return DialOutcome.Failed(ConnectFailureReason.ConfigInvalid, "invalid config");
            }

            var servers = DnsServers(resolved);
            var relay = NeedsRelay(plan) ? new ProxyRelay(plan, Protect, Report, ResolveOwner, Refuse) : null;
            _proxyPort = relay?.Start() ?? 0;
            _relay = relay;
            // Live tun replacement from Android 13.
            _liveTun = excludeRoutes && Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu;
            var hot = excludeRoutes ? HotDirect() : [];
            // The network the device sits on stays inside the tun only where the engine sends it out itself.
            var carveLocal = !localInTunnel || _localCarveForced || !(directTcp || _proxyPort > 0);
            var rules = await MaterializeAsync(plan, resolved, servers, _proxyPort > 0, _liveTun ? [] : hot, carveLocal).ConfigureAwait(false);
            _routed = relay is null ? RoutedReport(plan, rules) : null;
            if (_proxyPort == 0 && rules.Tunneled.Count > RouteBudget.Max)
            {
                Report($"{rules.Tunneled.Count} routes are more than the {RouteBudget.Max} this android takes in one "
                    + "transaction; shorten the routing list");
                return DialOutcome.Failed(ConnectFailureReason.TooManyRoutes, $"{rules.Tunneled.Count} of {RouteBudget.Max}");
            }

            // A dial taken back by now builds no tun.
            if (ct.IsCancellationRequested)
            {
                return DialOutcome.Failed(ConnectFailureReason.Unknown);
            }

            // The mode says where the size comes from: the link, the config text, or the one stored for it.
            var size = MtuPlan.ResolveForLink(MtuModes.From(mtuMode), mtu, underlay, carrier is not null);
            Report($"packets leave at {size} bytes ({MtuModes.Text(MtuModes.From(mtuMode))})");
            var excluded = _liveTun ? hot : [];
            var pfd = BuildTunnel(resolved, name, appMode, appList, bypassApps, size, ipv6, rules.Tunneled, servers,
                _proxyPort, excluded, out var establishError);
            if (pfd is null)
            {
                return DialOutcome.Failed(ConnectFailureReason.TunnelSetupFailed, establishError ?? "establish failed");
            }

            // What this tun leaves outside itself, held for as long as it lives: its route list is fixed now and the
            // networks under it are not.
            _carved = new List<string>(rules.Local);
            _shape = new TunShape(resolved, name, appMode, appList, bypassApps, size, ipv6, rules.Tunneled, servers,
                _proxyPort, !carveLocal);
            _excluded = excluded;

            var tunFd = pfd.DetachFd();
            var handle = AwgEngine.TurnOn(Restrict(uapi, rules.Allowed), tunFd, engineLog);
            if (handle < 0)
            {
                ParcelFileDescriptor.AdoptFd(tunFd)?.Close();
                return DialOutcome.Failed(ConnectFailureReason.EngineStartFailed, "engine start failed");
            }

            _handle = handle;
            var socket = AwgEngine.GetSocketV4(handle);
            if (socket >= 0)
            {
                Protect(socket);
            }

            // The protector goes in before the ranges: a direct datagram sent on an unprotected socket comes
            // straight back into the tun.
            AwgEngine.SetProtector(handle, Protect);
            _verdicts = rules.Verdicts;
            var decided = rules.Verdicts.Length > 0 && AwgEngine.SetVerdicts(handle, rules.Verdicts);
            if (decided)
            {
                Report($"{plan.BlockRoutes.Count} blocked and {plan.DirectRoutes.Count} direct range(s) handed to "
                    + "the engine, which decides them on the packet");
            }

            // What the previous session used: the engine takes these addresses back before the first packet, so a
            // reconnect and a restart find them decided instead of unknown. A range the rules name now overrides
            // the role the file carries.
            var preloaded = AwgEngine.PreloadLive(handle, LiveCache());
            if (preloaded > 0)
            {
                Report($"{preloaded} address(es) used before this session are decided from the start");
            }

            var streams = (directTcp || _proxyPort > 0) && AwgEngine.SetTcpDirect(handle, true);
            if (streams)
            {
                Report("a stream to a direct range leaves on a protected socket as well, so the relay is no longer "
                    + "the only way past the tunnel");
            }

            // A local network inside the tun rides the tunnel unless the engine sends it out itself.
            if (!carveLocal && !(decided && streams))
            {
                Report("the engine decides no destination on the packet here, so the network the device sits on "
                    + "leaves the tun again");
                _localCarveForced = true;
                return await BringUpAsync(plan, config, name, appMode, appList, mtu, mtuMode, ipv6, wsHost, wsPort, wsOffered,
                    engineLog, directTcp, excludeRoutes, bypassApps, localInTunnel, repair, ct).ConfigureAwait(false);
            }

            // All UDP on the tunnel leaves no datagram to the owner check.
            if (_proxyPort > 0 && AwgEngine.SetRelay(handle, _proxyPort, !plan.FullTunnel && !plan.AllUdp, Owner))
            {
                Report("streams are taken off the tun and decided in the relay, so the applications are offered no "
                    + "proxy and see none");
            }

            // Passes the idle window to the engine.
            _ttlSeconds = plan.TtlSeconds;
            AwgEngine.SetVerdictTtl(handle, plan.TtlSeconds);
            VpnBridge.WriteRouteTtl(plan.TtlSeconds);

            // The peer has to answer before the session counts as up: the tun and the engine start over a dead
            // server just as well, and the head would paint a live connection over nothing.
            var handshake = await WaitForHandshakeAsync(handle, repair, ct).ConfigureAwait(false);
            if (handshake <= 0)
            {
                return DialOutcome.Failed(ConnectFailureReason.NoHandshake, "no handshake");
            }

            var keepalive = new CancellationTokenSource();
            _keepalive = keepalive;
            // What the tunnel loses: the peer counters keep no trace of a packet that never arrived, so the far
            // end is echoed every few seconds - the peer where the server gives it an address, and otherwise the
            // resolvers, which the tunnel carries even where it carries nothing else of that subnet.
            var loss = new LinkLossProbe(LinkLossProbe.Targets(WgConfigEditor.GetAddresses(resolved), WgConfigEditor.GetDns(resolved)));
            _ = Task.Run(() => loss.RunAsync(keepalive.Token));

            // The handshake proves the channel, not the path to it: the system takes a fresh network into use a
            // while after establish() returns, and until then the applications go beside the tunnel. The stage
            // waits for the first byte that came back through it.
            var carried = await WaitForTrafficAsync(loss, handle, ct).ConfigureAwait(false);
            if (_handle != handle || ct.IsCancellationRequested)
            {
                return DialOutcome.Raised;
            }

            Report(carried
                ? "the tunnel carries traffic both ways"
                : $"the peer answered, but nothing has come back through the tun in {TrafficWaitSeconds} s; the "
                    + "session is reported as up on the handshake alone");
            _underKey = AndroidNetworks.Read(this).UnderKey;
            _session.Raised(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), name);
            KeepMarks();
            Publish(VpnStage.Connected, name);
            PublishLink(handshake, LinkReading.Empty);
            var rekey = WgConfigEditor.GetRekeyAfterSeconds(resolved);
            _rekeySeconds = rekey;
            _ = Task.Run(() => ReportLinkAsync(loss, LinkHealth.ChurnPerMinuteFor(rekey), keepalive.Token));
            if (relay is not null && _proxyPort > 0)
            {
                Report($"streams are decided on {ProxyHost}:{_proxyPort}, which no application is told about, "
                    + $"route ttl {plan.TtlSeconds} s");
            }

            var reports = new CancellationTokenSource();
            _reports = reports;
            _ = Task.Run(() => ReportShareAsync(relay, reports.Token));
            if (_liveTun)
            {
                Report("a destination decided direct leaves this tun on its own exclusion, and comes back to it "
                    + "when the cache releases it");
                _ = Task.Run(() => RefreshTunAsync(reports.Token));
            }

            // The port the user set up: it opens with the tunnel, because everything it carries leaves through it.
            _proxy = new LocalProxyServer((IProxyOutbound?)relay ?? new DirectProxyOutbound(), Report);
            ApplyProxy();
            return DialOutcome.Raised;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("GeoVpnService", "bring-up failed: " + ex);
            return DialOutcome.Failed(ReasonFor(ex), NetworkFailure.Describe(ex) ?? ex.Message);
        }
    }

    // Меряет серверы карточек, оставленные головой. Сокет от туннеля освобождает только этот процесс, поэтому
    // замер отдают сюда, а не пробуют там.
    private void RunCards()
    {
        var request = VpnBridge.ReadCards();
        if (request is null)
        {
            return;
        }

        VpnBridge.ClearCards();
        _ = Task.Run(async () =>
        {
            try
            {
                var payload = await CardProbe
                    .RunAsync(request.Servers, request.CarriesDefault, socket => Protect(socket.Handle.ToInt32()), CancellationToken.None)
                    .ConfigureAwait(false);
                VpnBridge.WriteCardsResult(payload);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("GeoVpnService", "the card probe failed: " + ex);
                VpnBridge.WriteCardsResult(CardProbe.Path(bypassed: false, request.CarriesDefault));
            }
        });
    }

    // Asks the server of the configuration the head wants to move to. The session that stands is left alone: the
    // engine that asks carries nothing, and only this process dials past the tunnel.
    private void RunSwitch()
    {
        var request = VpnBridge.ReadSwitch();
        if (request is null)
        {
            return;
        }

        VpnBridge.ClearSwitch();
        var standing = _detail ?? string.Empty;
        _ = Task.Run(() =>
        {
            var verdict = Asked(request);
            if (verdict == SwitchVerdict.Answered)
            {
                Report($"the server of {request.Name} answered, so the tunnel leaves {standing} for it");
            }
            else
            {
                Tell(verdict == SwitchVerdict.Silent
                    ? $"the server of {request.Name} did not answer in {SwitchWaitMs / 1000} s, so the tunnel stays on {standing}"
                    : $"the server of {request.Name} could not be asked before the tunnel leaves {standing}");
            }

            VpnBridge.WriteSwitchResult(verdict);
        });
    }

    // What the server of a configuration said to a handshake.
    private string Asked(SwitchRequest request)
    {
        try
        {
            return SwitchVerdict.Of(Answers(request));
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("GeoVpnService", "asking the server before the switch failed: " + ex);
            return SwitchVerdict.Unknown;
        }
    }

    // Whether the server of a configuration answers a handshake; null when it could not be asked.
    private bool? Answers(SwitchRequest request)
    {
        var resolved = Resolved(request.Config);
        if (resolved is null)
        {
            return false;
        }

        var carrier = StartCarrier(request.Config, request.WsHost, request.WsPort, request.WsOffered);
        try
        {
            var dialled = carrier is null ? resolved : WgConfigEditor.SetEndpoint(resolved, $"{ProxyHost}:{carrier.LocalPort}");
            var uapi = WgQuickToUapi.Convert(dialled);
            return uapi is null ? null : AwgEngine.Probe(uapi, Protect, SwitchWaitMs, request.EngineLog);
        }
        finally
        {
            carrier?.Dispose();
        }
    }

    // The configuration with the name of its server resolved; null when the name does not resolve.
    private static string? Resolved(string config)
    {
        try
        {
            return WgConfigEditor.EnsurePersistentKeepalive(ResolveEndpoint(config), KeepaliveSeconds);
        }
        catch (UnknownHostException)
        {
            return null;
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
                    socket => Protect(socket.Handle.ToInt32()), request.OwnUpload);
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
    private static ConnectFailureReason ReasonFor(Exception ex)
    {
        return ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            ? ConnectFailureReason.EngineUnavailable
            : ConnectFailureReason.Unknown;
    }

    /// <summary>
    /// The route lists a session is built from, and the local networks it keeps out of the tun.
    /// </summary>
    private readonly record struct Materialized(
        IReadOnlyList<string> Tunneled,
        IReadOnlyList<string> Allowed,
        IReadOnlyList<string> Local,
        string Verdicts);

    // What the route table alone decides, as the journal reads it: the ranges handed to the tun and the engine.
    // Nothing is met here, so no row carries a clock.
    private static SessionReport RoutedReport(GeoRoutingPlan plan, Materialized rules)
    {
        var rows = new List<LiveSession>();
        var listed = plan.HasRules || plan.TunnelApps.Count > 0;
        var reason = listed ? LiveSession.ReasonRange : LiveSession.ReasonConfig;
        foreach (var range in plan.BlockRoutes)
        {
            rows.Add(new LiveSession(range, "block", Path: LiveSession.PathBlock, Reason: reason));
        }

        foreach (var range in plan.DirectRoutes)
        {
            rows.Add(new LiveSession(range, "direct", Path: LiveSession.PathDirect, Reason: reason));
        }

        foreach (var range in listed ? plan.ProxyRoutes : rules.Tunneled)
        {
            rows.Add(new LiveSession(range, "proxy", Path: LiveSession.PathTunnel, Reason: reason));
        }

        var mode = plan.FullTunnel
            ? (listed ? SessionReport.ModeFull : SessionReport.ModeOff)
            : SessionReport.ModeSplit;
        return new SessionReport(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [.. rows.Take(SessionReport.MaxRows)],
            rows.Count,
            0,
            0,
            listed ? plan.ProxyRoutes.Count : rules.Tunneled.Count,
            plan.DirectRoutes.Count,
            plan.BlockRoutes.Count,
            mode);
    }

    // Whether the session raises the relay. Every byte it carries crosses userspace twice, so it stands only where
    // the route table cannot say the same thing: a connection to attribute to an application, or more ranges than
    // establish() takes. A configuration can also refuse it outright, and then the route table decides alone.
    private static bool NeedsRelay(GeoRoutingPlan plan)
    {
        if (!RouteBudget.Relayable)
        {
            return false;
        }

        if (!plan.UseRouter)
        {
            Report("the configuration keeps the relay down, so the route table decides every destination and no "
                + "byte crosses userspace twice");
            return false;
        }

        if (plan.TunnelApps.Count > 0)
        {
            return true;
        }

        if (plan.AllUdp)
        {
            Report("every datagram rides the tunnel, so the relay stands to take the streams off it");
            return true;
        }

        var routes = SystemRoutes.Tunneled(plan.FullTunnel, plan.ProxyRoutes, plan.DirectRoutes, plan.BlockRoutes).Count;
        var names = plan.ProxyDomains.Count + plan.DirectDomains.Count + plan.BlockDomains.Count;
        if (RouteBudget.Fits(routes, names))
        {
            Report($"{routes} route(s) carry the rules on their own, so the relay stays down and nothing crosses it");
            return false;
        }

        Report($"{routes} route(s) are more than establish() takes, so the relay decides the destinations instead");
        return true;
    }

    // Parts the session tun is built from.
    private sealed record TunShape(
        string Config,
        string Name,
        string? AppMode,
        string[]? AppList,
        string[]? BypassApps,
        int Mtu,
        bool Ipv6,
        IReadOnlyList<string> Routes,
        IReadOnlyList<string> Servers,
        int ProxyPort,
        bool LocalInside);

    // Turns the rules into the two address lists a tunnel is built from. Behind the relay a destination is decided
    // while the session runs, so no name is resolved at connect - the mode only says where a destination no rule
    // named belongs. A route table holds addresses and not protocols, so the tun there carries every datagram
    // except the direct ranges it leaves out. Without the relay a name has to become an address here and stay that
    // way for the session: a route table cannot be edited once the tun is established.
    private static async Task<Materialized> MaterializeAsync(GeoRoutingPlan plan, string config, IReadOnlyList<string> servers, bool relayed, IReadOnlyList<string> hot, bool carveLocal)
    {
        var proxy = new List<string>(plan.ProxyRoutes);
        var direct = relayed || !carveLocal ? new List<string>() : new List<string>(plan.DirectRoutes);
        var block = new List<string>(plan.BlockRoutes);

        // The resolver rides the tunnel, so a query is answered where the traffic goes and not where the device sits.
        foreach (var server in servers)
        {
            proxy.Add(server + "/32");
        }

        // The segment the box sits on, as the interfaces report it.
        var local = new List<string>(LocalSubnets());
        if (local.Count == 0)
        {
            Report("no local subnet found, the tun will carry the network the device sits on as well");
        }

        // The private networks the configuration itself reaches, less the ones the device stands in.
        foreach (var network in PrivateNetworks.ForTunnel(config, local))
        {
            if (!proxy.Contains(network, StringComparer.OrdinalIgnoreCase))
            {
                proxy.Add(network);
            }
        }

        // Ranges the tun leaves out whatever the budget holds: the network the device sits on, and the addresses a
        // direct name resolved to.
        var kept = new List<string>();

        // Ranges the tun carries while the engine sends them out itself.
        var onPacket = new List<string>();
        if (carveLocal)
        {
            direct.AddRange(local);
            kept.AddRange(local);
        }
        else
        {
            onPacket.AddRange(local);
            onPacket.AddRange(plan.DirectRoutes);
            Report($"{local.Count + plan.DirectRoutes.Count} local and direct range(s) stay inside the tun and "
                + "leave it on a protected socket, so an application reaches the network the device sits on");
        }

        if (relayed)
        {
            Report($"{Mode(plan)} tunnel behind the local proxy: "
                + $"{plan.ProxyRoutes.Count + plan.DirectRoutes.Count} range(s) and "
                + $"{plan.ProxyDomains.Count + plan.DirectDomains.Count + plan.BlockDomains.Count} name rule(s) "
                + $"decided on contact; a destination no rule names goes "
                + $"{(plan.FullTunnel ? "through the tunnel" : "direct")}");

            // A blocked name is refused on the stream that carries it. It is not resolved here: the answer would
            // come from the resolver the device sits behind rather than the tunnel's, and a catalogue of names
            // costs a query each before the tun is even up.
            if (plan.BlockDomains.Count > 0)
            {
                Report($"{plan.BlockDomains.Count} blocked name(s) are refused on the relay; a datagram that never "
                    + "reaches it is stopped by a blocked range alone");
            }

            // A direct range stays out of the route table: the shim decides it on the packet and sends the
            // datagram on its own protected socket, so establish() carries the local subnets alone.
            Report($"{plan.DirectRoutes.Count} direct range(s) are decided on the packet, so a datagram to one of "
                + "them leaves on a protected socket while the table stays short");

            if (plan.AllUdp)
            {
                Report("every datagram rides the tunnel, whoever sent it: only a direct range takes one off the "
                    + "route table");
            }
            else
            {
                Report("a socket that ignores the proxy still rides the tunnel: only datagrams take the direct "
                    + "path off the route table");
            }
        }
        else if (plan.HasDomains)
        {
            var clock = Stopwatch.StartNew();
            // The names become addresses under a deadline, so a catalogue of them cannot hold up the connect.
            using var budget = new CancellationTokenSource(NameBudgetMs);
            var resolver = new GeoDomainRouteResolver();
            var names = plan.ProxyDomains.Count + plan.DirectDomains.Count + plan.BlockDomains.Count;
            var named = await resolver.ResolveAsync(plan.DirectDomains, budget.Token).ConfigureAwait(false);
            proxy.AddRange(await resolver.ResolveAsync(plan.ProxyDomains, budget.Token).ConfigureAwait(false));
            if (carveLocal)
            {
                direct.AddRange(named);
                kept.AddRange(named);
            }
            else
            {
                onPacket.AddRange(named);
            }
            block.AddRange(await resolver.ResolveAsync(plan.BlockDomains, budget.Token).ConfigureAwait(false));
            Report($"{names} name rule(s) resolved to addresses in {clock.ElapsedMilliseconds} ms"
                + (budget.IsCancellationRequested ? ", the rest ran out of their time" : string.Empty)
                + "; a name that moves to another address will no longer match");
        }

        // Excludes the last session's direct addresses at connect.
        var taken = hot.Count > 0 && carveLocal
            ? SystemRoutes.Fit(plan.FullTunnel || relayed, proxy, direct, block, hot, RouteBudget.Max)
            : 0;
        if (taken > 0)
        {
            direct.AddRange(hot.Take(taken));
            kept.AddRange(hot.Take(taken));
            Report($"{taken} of {hot.Count} address(es) the last session used leave the tun from the start, so the "
                + "kernel carries them instead of the shim");
        }

        var tunneled = SystemRoutes.Tunneled(plan.FullTunnel || relayed || !carveLocal, proxy, direct, block);
        if (!relayed && tunneled.Count > RouteBudget.Max)
        {
            // establish() takes the table in one transaction, so the direct ranges leave it altogether and the
            // shim decides them on the packet instead.
            direct = new List<string>(kept);
            tunneled = SystemRoutes.Tunneled(plan.FullTunnel, proxy, direct, block);
            Report($"{plan.DirectRoutes.Count} direct range(s) do not fit the {RouteBudget.Max} route(s) "
                + "establish() takes, so they are decided on the packet");
        }

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
        var decided = new List<string>(plan.DirectRoutes);
        decided.AddRange(onPacket);

        return new Materialized(tunneled, allowed, local, Verdicts(plan.ProxyRoutes, decided, block));
    }

    // What the shim decides on the packet: block wins over direct, direct over proxy. The ranges stay inside the
    // process, so their number costs nothing here.
    private static string Verdicts(
        IReadOnlyList<string> proxy,
        IReadOnlyList<string> direct,
        IReadOnlyList<string> block)
    {
        var text = new StringBuilder();
        Append(text, block, "block");
        Append(text, direct, "direct");
        Append(text, proxy, "proxy");
        return text.ToString();

        static void Append(StringBuilder text, IReadOnlyList<string> ranges, string role)
        {
            foreach (var range in ranges)
            {
                if (range.Contains(':', StringComparison.Ordinal))
                {
                    continue;
                }

                text.Append(range).Append('=').Append(role).Append('\n');
            }
        }
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
        if (stage != VpnStage.Connecting)
        {
            _retry = 0;
        }

        VpnBridge.WriteStage(stage, detail);
        // Only a running tunnel can be asked whether the system holds it as the always-on one.
        var alwaysOn = Build.VERSION.SdkInt >= BuildVersionCodes.Q && IsAlwaysOn;
        VpnBridge.Publish(this, stage, detail, reason, alwaysOn, alwaysOn && IsLockdownEnabled, _retry);
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
        string[]? bypassApps,
        int mtu,
        bool ipv6,
        IReadOnlyList<string> routes,
        IReadOnlyList<string> servers,
        int proxyPort,
        IReadOnlyList<string> excluded,
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

            if (excluded.Count > 0)
            {
                Exclude(builder, excluded);
            }

            foreach (var server in servers)
            {
                builder.AddDnsServer(server);
            }

            builder.SetMtu(mtu);

            // The tunnel takes its meteredness from the network under it.
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                builder.SetMetered(false);
            }

            _tunnelApps.Clear();
            foreach (var package in appList ?? [])
            {
                _tunnelApps.Add(package);
            }

            _owners.Clear();

            var allowListed = ApplyAppSplit(builder, appMode, appList);
            ApplyAppBypass(builder, bypassApps, allowListed);

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

    private static void Exclude(Builder builder, IReadOnlyList<string> addresses)
    {
        try
        {
            foreach (var address in addresses)
            {
                builder.ExcludeRoute(new IpPrefix(InetAddress.GetByName(address)!, 32));
            }

            Report($"{addresses.Count} address(es) the last session used stay outside the tun, so the kernel "
                + "carries them instead of the shim");
        }
        catch (Java.Lang.Exception ex)
        {
            global::Android.Util.Log.Warn("GeoVpnService", "excluding the addresses of the last session failed: " + ex);
        }
    }

    // Rebuilds the tun on the cache sweep step.
    private async Task RefreshTunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Math.Clamp(_ttlSeconds / 5, 5, 60) * 1000, ct).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                return;
            }

            RefreshTun();
        }
    }

    // Rebuilds the tun around the addresses the cache holds now.
    private void RefreshTun()
    {
        lock (_swapGate)
        {
            var shape = _shape;
            var handle = _handle;
            if (shape is null || handle < 0 || _stage != VpnStage.Connected)
            {
                return;
            }

            var wanted = DirectAddresses(AwgEngine.LiveAddresses(handle));
            if (wanted.Count == _excluded.Count && new HashSet<string>(wanted).SetEquals(_excluded))
            {
                return;
            }

            if (!Swap(handle, shape, wanted, out var error))
            {
                Report($"the tun could not be rebuilt around {wanted.Count} direct address(es): {error}");
                return;
            }

            var added = wanted.Count - _excluded.Count;
            _excluded = wanted;
            Report($"{wanted.Count} address(es) decided direct now leave the tun on their own ({added:+#;-#;0})");
        }
    }

    // Puts a tun of this shape under the running engine.
    private bool Swap(int handle, TunShape shape, IReadOnlyList<string> excluded, out string? error)
    {
        // Announces the swap before the replacement is established.
        AwgEngine.PrepareSwap(handle, true);
        var pfd = BuildTunnel(shape.Config, shape.Name, shape.AppMode, shape.AppList, shape.BypassApps, shape.Mtu,
            shape.Ipv6, shape.Routes, shape.Servers, shape.ProxyPort, excluded, out error);
        if (pfd is null)
        {
            AwgEngine.PrepareSwap(handle, false);
            return false;
        }

        var tunFd = pfd.DetachFd();
        if (AwgEngine.SwapTun(handle, tunFd))
        {
            return true;
        }

        AwgEngine.PrepareSwap(handle, false);
        ParcelFileDescriptor.AdoptFd(tunFd)?.Close();
        error = "the engine refused the rebuilt tun";
        return false;
    }

    // Addresses the engine decided direct, freshest first.
    private static IReadOnlyList<string> DirectAddresses(string? live)
    {
        if (string.IsNullOrEmpty(live))
        {
            return [];
        }

        var hot = new List<(string Address, int Age)>();
        foreach (var line in live.Split('\n'))
        {
            var parts = line.Split(' ');
            if (parts.Length == 3 && parts[1] == "direct"
                && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var age)
                && age <= HotTtlSeconds)
            {
                hot.Add((parts[0], age));
            }
        }

        hot.Sort((left, right) => left.Age.CompareTo(right.Age));
        return [.. hot.Take(HotMax).Select(item => item.Address)];
    }

    // Saves the session's direct addresses for the next one.
    private void KeepHotDirect()
    {
        try
        {
            var hot = DirectAddresses(AwgEngine.LiveAddresses(_handle));
            var path = System.IO.Path.Combine(FilesDir!.AbsolutePath!, HotFile);
            if (hot.Count == 0)
            {
                System.IO.File.Delete(path);
                return;
            }

            System.IO.File.WriteAllLines(path, hot);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("GeoVpnService", "keeping the addresses of this session failed: " + ex);
        }
    }

    // Keeps the addresses this session decided for the next one, freshest first.
    private void KeepLive()
    {
        try
        {
            var path = System.IO.Path.Combine(FilesDir!.AbsolutePath!, LiveFile);
            var live = LiveLines(AwgEngine.LiveAddresses(_handle));
            if (live.Count == 0)
            {
                System.IO.File.Delete(path);
                return;
            }

            System.IO.File.WriteAllLines(path, live);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("GeoVpnService", "keeping the decided addresses of this session failed: " + ex);
        }
    }

    // The freshest lines of a snapshot, no more than the next session takes back.
    private static IReadOnlyList<string> LiveLines(string? live)
    {
        if (string.IsNullOrEmpty(live))
        {
            return [];
        }

        var rows = new List<(string Line, int Age)>();
        foreach (var line in live.Split('\n'))
        {
            var parts = line.Split(' ');
            if (parts.Length == 3 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var age))
            {
                rows.Add((line, age));
            }
        }

        rows.Sort((left, right) => left.Age.CompareTo(right.Age));
        return [.. rows.Take(LiveMax).Select(row => row.Line)];
    }

    // The addresses of the last session, aged by the time the tunnel spent down.
    private string LiveCache()
    {
        try
        {
            var path = System.IO.Path.Combine(FilesDir!.AbsolutePath!, LiveFile);
            if (!System.IO.File.Exists(path))
            {
                return string.Empty;
            }

            var down = (int)Math.Max((DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(path)).TotalSeconds, 0);
            var lines = new List<string>();
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                var parts = line.Split(' ');
                if (parts.Length == 3 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var age))
                {
                    lines.Add($"{parts[0]} {parts[1]} {age + down}");
                }
            }

            return string.Join('\n', lines);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("GeoVpnService", "reading the decided addresses of the last session failed: " + ex);
            return string.Empty;
        }
    }

    private IReadOnlyList<string> HotDirect()
    {
        try
        {
            var path = System.IO.Path.Combine(FilesDir!.AbsolutePath!, HotFile);
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllLines(path) : [];
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("GeoVpnService", "reading the addresses of the last session failed: " + ex);
            return [];
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

    // The IPv4 networks of the mobile links.
    private List<string> MobileSubnets()
    {
        var found = new List<string>();
        try
        {
            var manager = _connectivity ??= (ConnectivityManager?)GetSystemService(ConnectivityService);
            foreach (var network in manager?.GetAllNetworks() ?? [])
            {
                if (manager!.GetNetworkCapabilities(network) is not { } capabilities
                    || !capabilities.HasTransport(TransportType.Cellular)
                    || manager.GetLinkProperties(network) is not { } link)
                {
                    continue;
                }

                foreach (var entry in link.LinkAddresses)
                {
                    if (System.Net.IPAddress.TryParse(entry.Address?.HostAddress ?? string.Empty, out var address)
                        && Subnet(address, entry.PrefixLength) is { } cidr)
                    {
                        found.Add(cidr);
                    }
                }
            }
        }
        catch (Java.Lang.Exception ex)
        {
            global::Android.Util.Log.Warn("GeoVpnService", "reading the mobile networks failed: " + ex);
        }

        return found;
    }

    private static string? Subnet(UnicastIPAddressInformation unicast) => Subnet(unicast.Address, unicast.PrefixLength);

    private static string? Subnet(System.Net.IPAddress address, int prefix)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        // /31 and /32 name a host and /0 the whole internet; neither is a network the device shares with anything.
        if (prefix is <= 0 or >= 31 || !GeoIpRanges.TryToNumeric(address, out var value))
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

    // Names the application behind a connection; null when the system will not tell. A stream handed over by the tun
    // names the pair it was opened for, a proxy request names its loopback end. The manager, the proxy end of the
    // pair and the packages a uid owns are held: each costs a round trip into another process, and none of them
    // changes while the tunnel stands.
    private string? ResolveOwner(System.Net.IPEndPoint peer, System.Net.IPEndPoint? destination)
    {
        try
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.Q || (destination is null && _proxyPort == 0))
            {
                return null;
            }

            var manager = _connectivity ??= (ConnectivityManager?)GetSystemService(ConnectivityService);
            var remote = destination is null
                ? _proxyEnd ??= new InetSocketAddress(ProxyHost, _proxyPort)
                : new InetSocketAddress(destination.Address.ToString(), destination.Port);
            var local = new InetSocketAddress(peer.Address.ToString(), peer.Port);
            var uid = manager?.GetConnectionOwnerUid(TcpProtocol, local, remote) ?? -1;
            if (uid < 0)
            {
                return null;
            }

            return _packages.GetOrAdd(uid, static (id, service) => service.Named(id), this);
        }
        catch (Java.Lang.Exception)
        {
            return null;
        }
    }

    // Whose flow this is, held for a moment: a program that ends between two datagrams takes its socket out of the
    // table the system answers from, and the answer it earned stands in for it until the moment passes.
    private int Owner(int protocol, uint source, ushort sourcePort, uint destination, ushort destinationPort)
    {
        var flow = ((ulong)(uint)protocol << 48) | ((ulong)sourcePort << 32) | destination;
        var now = System.Environment.TickCount64;
        if (_owners.TryGetValue(flow, out var held) && held.Until > now)
        {
            return held.Verdict;
        }

        var verdict = Ask(protocol, source, sourcePort, destination, destinationPort);
        if (verdict == OwnerOther)
        {
            return verdict;
        }

        if (_owners.Count > OwnersHeld)
        {
            Forget(now);
        }

        _owners[flow] = (verdict, now + OwnerHoldMs);
        return verdict;
    }

    // Drops the verdicts whose moment has passed.
    private void Forget(long now)
    {
        foreach (var pair in _owners)
        {
            if (pair.Value.Until <= now)
            {
                _owners.TryRemove(pair.Key, out _);
            }
        }
    }

    // Whose connection this is: this process, an application the rules name, or neither. Our own connections ride
    // the tunnel, or they would come back into the hand-over they came from; a named application rides it because
    // the rules say so, and its datagrams go where its streams go.
    private int Ask(int protocol, uint source, ushort sourcePort, uint destination, ushort destinationPort)
    {
        try
        {
            var manager = _connectivity ??= (ConnectivityManager?)GetSystemService(ConnectivityService);
            var local = new InetSocketAddress(Dotted(source), sourcePort);
            var remote = new InetSocketAddress(Dotted(destination), destinationPort);
            var uid = manager?.GetConnectionOwnerUid(protocol, local, remote) ?? -1;
            if (uid < 0)
            {
                return OwnerOther;
            }

            if (uid == global::Android.OS.Process.MyUid())
            {
                return OwnerSelf;
            }

            var named = _packages.GetOrAdd(uid, static (id, service) => service.Named(id), this);
            return _tunnelApps.Contains(named) ? OwnerNamed : OwnerOther;
        }
        catch (Java.Lang.Exception)
        {
            return OwnerOther;
        }
    }

    // An address written the way the system takes it.
    private static string Dotted(uint address) =>
        $"{(address >> 24) & 0xFF}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}";

    // The package a uid owns, or the uid itself where the system names none.
    private string Named(int uid)
    {
        var packages = PackageManager?.GetPackagesForUid(uid);
        return packages is { Length: > 0 } ? packages[0] : "uid:" + uid;
    }

    // Logs what the tunnel carried and how the engine decided the packets; the relay adds its own share when it runs.
    private async Task ReportShareAsync(ProxyRelay? relay, CancellationToken ct)
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
            if (relay is not null)
            {
                VpnBridge.WriteSessions(relay.Sessions());
                var share = tunnel > 0 ? relay.Bytes * 100 / tunnel : 0;
                Report($"{relay.Snapshot()}; tunnel {tunnel / 1024} KiB, relayed {share}%");
            }
            else
            {
                if (_routed is { } routed)
                {
                    VpnBridge.WriteSessions(routed with { UnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                }

                Report($"tunnel {tunnel / 1024} KiB");
            }

            var stats = AwgEngine.Stats(handle);
            if (!string.IsNullOrEmpty(stats))
            {
                Report("verdicts: " + stats);
            }
        }
    }

    // Waits for the first echo to come back through the tun; false when none does inside the window or the
    // session is gone. What it proves is the path the applications take, which the handshake does not.
    private async Task<bool> WaitForTrafficAsync(LinkLossProbe loss, int handle, CancellationToken ct)
    {
        for (var attempt = 0; attempt < TrafficWaitSeconds * 1000 / TrafficPollMs; attempt++)
        {
            if (_handle != handle || ct.IsCancellationRequested)
            {
                return false;
            }

            if (loss.Answering)
            {
                return true;
            }

            await Task.Delay(TrafficPollMs).ConfigureAwait(false);
        }

        return false;
    }

    // Waits for the peer's first answer, the only proof the session carries anything; 0 when none comes in time or
    // the session is gone.
    private async Task<long> WaitForHandshakeAsync(int handle, bool untilAnswered, CancellationToken ct)
    {
        for (var attempt = 0; untilAnswered || attempt < HandshakeWaitSeconds * 1000 / HandshakePollMs; attempt++)
        {
            if (_handle != handle || ct.IsCancellationRequested)
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
    private async Task ReportLinkAsync(LinkLossProbe loss, int churnPerMinute, CancellationToken ct)
    {
        // Counts the sleep of this session from its first look.
        lock (_sleepGate)
        {
            _sleepSeen = -1;
        }

        var meter = new LinkMeter { ChurnPerMinute = churnPerMinute };
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
            RenewAfterSleep(handle, uapi);
            var seen = PeerHandshake(uapi);
            var (rx, tx) = PeerBytes(uapi);
            var reading = meter.Sample(rx, tx, seen, loss.Percent, loss.RttMs, loss.Streak);
            var moved = new LinkSample(tx > lastTx, rx > lastRx, loss.RecentPercent, reading.Churning,
                seen > 0 ? (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seen) : 0,
                lastTx < 0 ? 0 : Math.Max(0, tx - lastTx));
            lastRx = rx;
            lastTx = tx;
            WatchNetworks(seen, reading);

            // A session that has not been answered yet is still coming up, and the ladder judges nothing until it is.
            if (seen > 0
                && _recovery.Sample(moved, System.Environment.TickCount64) is { } step
                && Repair(handle, step))
            {
                return;
            }

            if (_recovery.GivenUp && !gaveUp)
            {
                gaveUp = true;
                var text = $"{_recovery.Reason}, and {_recovery.Attempt} attempts to raise the session again did not "
                    + "bring it back; nothing further is tried until the network changes or you connect again";
                Report(text);
                Note("tunnel", text);
            }

            var told = Told(seen);
            if (told == handshake && !reading.DiffersFrom(reported))
            {
                continue;
            }

            handshake = told;
            reported = reading;
            PublishLink(told, reading);
        }
    }

    // Carries out a step of the ladder and tells whether the session is being raised again.
    private bool Repair(int handle, RecoveryStep step)
    {
        if (step != RecoveryStep.Rebind)
        {
            return Reraise($"{_recovery.Reason}; raising the session again (attempt {_recovery.Attempt})");
        }

        // A carried tunnel dials its carrier on the loopback, where another port changes nothing.
        if (_carrier is not null)
        {
            return false;
        }

        var rebound = AwgEngine.Rebind(handle);
        var text = $"{_recovery.Reason}; binding the tunnel to another source port (attempt {_recovery.Attempt})"
            + (rebound ? string.Empty : " - the engine would not take it");
        Report(text);
        Note("tunnel", text);
        if (rebound)
        {
            _session.Repaired(step);
            KeepMarks();
        }

        return false;
    }

    // Writes the marks of the session where the head reads them.
    private void KeepMarks()
    {
        VpnBridge.WriteMarks(_session.ToPayload());
    }

    // Notes what changed around the tunnel since the last look: the network the device sits on, how names resolve,
    // and whether the system still reaches the internet through the tunnel.
    private void WatchNetworks(long handshake, LinkReading reading)
    {
        var view = AndroidNetworks.Read(this);
        if (view.Under.Length > 0)
        {
            var under = view.UnderText;
            if (_underNoted is not null && !string.Equals(under, _underNoted, StringComparison.Ordinal))
            {
                Note("network", $"the device now sits on {under} (was {_underNoted})");
            }

            _underNoted = under;
        }

        var now = SystemClock.ElapsedRealtime();
        if (view.TunnelValidated == false)
        {
            if (_unvalidatedSince == 0)
            {
                _unvalidatedSince = now;
            }

            var seconds = (now - _unvalidatedSince) / 1000;
            if (!_unvalidatedNoted && seconds >= UnvalidatedNoteSeconds)
            {
                _unvalidatedNoted = true;
                var age = handshake > 0 ? (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - handshake) : -1;
                var snapshot = new NetworkSnapshot(ConnectionStatus.Connected, age, reading.RxBitsPerSecond,
                    reading.LossPercent, reading.Churning, view.Under, view.UnderValidated, view.TunnelValidated,
                    view.PrivateDnsHost, view.PrivateDnsActive);
                Note("network", $"the system has not validated the tunnel for {seconds} s: {snapshot.Describe()}");
            }

            return;
        }

        if (view.TunnelValidated != true)
        {
            return;
        }

        if (_unvalidatedNoted)
        {
            Note("network", $"the system validated the tunnel again after {(now - _unvalidatedSince) / 1000} s");
        }

        _unvalidatedSince = 0;
        _unvalidatedNoted = false;

        // Private DNS is read on a validated tunnel only: a fresh one has not upgraded to it yet.
        var dns = view.PrivateDnsText;
        if (view.PrivateDnsActive is not null && !string.Equals(dns, _privateDnsNoted, StringComparison.Ordinal))
        {
            if (_privateDnsNoted is not null || view.PrivateDnsHost is not null)
            {
                Note("network", _privateDnsNoted is null ? $"private DNS {dns}" : $"private DNS {dns} (was {_privateDnsNoted})");
            }

            _privateDnsNoted = dns;
        }
    }

    // Remembers when the screen went off and, once it comes back after a while, tells how long the device slept and
    // what the session looked like on waking.
    private void OnScreen(Intent intent)
    {
        if (intent.Action == Intent.ActionScreenOff)
        {
            _screenOffElapsed = SystemClock.ElapsedRealtime();
            _screenOffUptime = SystemClock.UptimeMillis();
            return;
        }

        if (intent.Action != Intent.ActionScreenOn || _screenOffElapsed < 0)
        {
            return;
        }

        var off = SystemClock.ElapsedRealtime() - _screenOffElapsed;
        var asleep = Math.Max(0, off - (SystemClock.UptimeMillis() - _screenOffUptime));
        _screenOffElapsed = -1;
        var handle = _handle;
        if (off < WakeNoteMs || _stage != VpnStage.Connected || handle < 0)
        {
            return;
        }

        var uapi = AwgEngine.GetConfig(handle);
        var (rx, _) = PeerBytes(uapi);
        Note("sleep", $"the screen came on after {Span(off)} off, {Span(asleep)} of it asleep; the peer last answered "
            + Age(PeerHandshake(uapi)));
        RenewAfterSleep(handle, uapi);
        _ = Task.Run(() => FollowWakeAsync(handle, rx));
    }

    // Tells whether the session carried again shortly after waking.
    private async Task FollowWakeAsync(int handle, long rx)
    {
        await Task.Delay(WakeFollowMs).ConfigureAwait(false);
        var after = $"{WakeFollowMs / 1000} s after waking";
        if (_handle != handle)
        {
            Note("sleep", _handle < 0 ? $"{after} the tunnel is down" : $"{after} the session has been raised again");
            return;
        }

        var uapi = AwgEngine.GetConfig(handle);
        var (received, _) = PeerBytes(uapi);
        Note("sleep", $"{after} the tunnel received {received - rx} bytes; the peer last answered {Age(PeerHandshake(uapi))}");
    }

    // Renews the session keys once the sleep since the last handshake has left the engine counting them younger than
    // the server does.
    private void RenewAfterSleep(int handle, string? uapi)
    {
        var seen = PeerHandshake(uapi);
        var sleep = SystemClock.ElapsedRealtime() - SystemClock.UptimeMillis();
        lock (_sleepGate)
        {
            if (seen != _answered)
            {
                _answered = seen;
                _sleepAtAnswer = _sleepSeen >= 0 ? _sleepSeen : sleep;
            }

            _sleepSeen = sleep;
            var age = seen > 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seen : -1;
            var slept = sleep - _sleepAtAnswer;
            if (seen == _renewedAfter || !HandshakeAge.OutlivedBySleep(age, slept, _rekeySeconds) || !Renew(handle, uapi))
            {
                return;
            }

            _renewedAfter = seen;
            _renewUntil = System.Environment.TickCount64 + RenewHoldMs;
            var text = $"the session is {age} s old, {Span(slept)} of it asleep, which the engine does not count; its keys "
                + "were renewed, so the next packet opens a handshake";
            Report(text);
            Note("tunnel", text);
            PublishLink(0, _linkReading);
        }
    }

    // The handshake the head is told: none while renewed keys wait for their first answer.
    private long Told(long seen)
    {
        lock (_sleepGate)
        {
            return seen == _renewedAfter && System.Environment.TickCount64 < _renewUntil ? 0 : seen;
        }
    }

    // Sets a stray private key and the own one back, which drops the session keys of every peer.
    private static bool Renew(int handle, string? uapi)
    {
        var own = (uapi ?? string.Empty).Split('\n').FirstOrDefault(line => line.StartsWith("private_key=", StringComparison.Ordinal));
        if (own is null)
        {
            return false;
        }

        var stray = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        return AwgEngine.SetConfig(handle, $"private_key={stray}\n{own}\n");
    }

    // A stretch of time in the unit it reads best in.
    private static string Span(long ms)
    {
        var seconds = ms / 1000;
        if (seconds < 120)
        {
            return $"{seconds} s";
        }

        var minutes = seconds / 60;
        return minutes < 120 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
    }

    // How long ago the peer answered, by the wall clock.
    private static string Age(long unixSeconds)
    {
        return unixSeconds > 0 ? $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixSeconds} s ago" : "never";
    }

    // Tells the head an event it keeps whatever its capture floor is, and keeps it for a head that is not there.
    private static void Note(string source, string text)
    {
        global::Android.Util.Log.Warn("GeoVpnService", source + " " + text);
        VpnBridge.KeepNote(source, text);
        VpnBridge.PublishNote(global::Android.App.Application.Context, source, text);
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
    private bool ApplyAppSplit(Builder builder, string? mode, string[]? packages)
    {
        if (packages is not { Length: > 0 } || string.IsNullOrEmpty(mode))
        {
            return false;
        }

        var exclude = string.Equals(mode, "exclude", StringComparison.Ordinal);
        // The relay names the application behind every connection, so the named ones ride the tunnel from there and
        // the tun keeps carrying them all. An allow list here would send every other application past every rule.
        if (!exclude && _proxyPort > 0)
        {
            Report($"{packages.Length} named application(s) ride the tunnel by connection, not by an allow list");
            return false;
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

        return !exclude && applied > 0;
    }

    // Keeps the named applications out of the tunnel altogether: the system routes them off the tun and reports
    // them a plain network, so an application that refuses to run under a vpn sees none.
    private void ApplyAppBypass(Builder builder, string[]? packages, bool allowListed)
    {
        if (packages is not { Length: > 0 })
        {
            return;
        }

        if (allowListed)
        {
            Report($"{packages.Length} application(s) named off the tunnel are outside it already, since only the "
                + "listed ones enter it");
            return;
        }

        var applied = 0;
        foreach (var package in packages)
        {
            if (string.IsNullOrWhiteSpace(package))
            {
                continue;
            }

            if (Listed(builder, true, package))
            {
                applied++;
            }
            else
            {
                Report($"the routing list keeps {package} off the tunnel, which is not installed here");
            }
        }

        if (applied > 0)
        {
            Report($"{applied} application(s) stay off the tunnel and see no vpn on this device");
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

    // The websocket the tunnel is carried inside when the configuration asks for one: at the front its server
    // offers, which takes the token of the config, else at the address the head passes. The front is resolved
    // here, while the machine still answers lookups of its own, and the carrier's socket is excused from the
    // tunnel, or it would be asked to carry itself.
    private WsCarrier? StartCarrier(string config, string? host, int port, bool offered)
    {
        if (string.IsNullOrEmpty(host) || port <= 0)
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

        var named = WsEndpoint.Of(host, port, endpoint, string.Empty);
        var front = offered ? named with { Offered = true } : named;
        var address = ResolveHostV4(front.Host);
        if (address is null || !System.Net.IPAddress.TryParse(address, out var parsed))
        {
            Report($"the websocket front {front.Display()} has no address to dial");
            return null;
        }

        var carrier = WsCarrier.Start(front, parsed, targetPort, front.Offered ? ServiceToken.HeaderOf(config) : null, socket => Protect(socket.Handle.ToInt32()),
            (message, ex) => Report(ex is null ? message : $"{message}: {ex.Message}"));
        Report($"the tunnel is carried inside a websocket to {front.Display()}; the engine dials it on {ProxyHost}:{carrier.LocalPort}");
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

        var ip = ResolveOrRecall(host);
        return ip is null ? config : WgConfigEditor.SetEndpoint(config, $"{ip}:{port}");
    }

    // Resolves the server name, or takes the address it resolved to last when the name does not resolve now.
    private static string? ResolveOrRecall(string host)
    {
        try
        {
            var ip = ResolveHostV4(host);
            if (ip is not null)
            {
                _resolvedHosts[host] = ip;
            }

            return ip;
        }
        catch (UnknownHostException) when (_resolvedHosts.TryGetValue(host, out var known))
        {
            Report($"{host} does not resolve now, so the session is raised on {known}, the address it had last time");
            return known;
        }
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
            intent.GetIntExtra(ExtraWsPort, 0),
            intent.GetIntExtra(ExtraEngineLog, AwgEngine.LogError),
            intent.GetIntExtra(ExtraMtuMode, 0),
            intent.GetBooleanExtra(ExtraDirectTcp, true),
            intent.GetBooleanExtra(ExtraExcludeRoutes, false),
            intent.GetStringArrayExtra(ExtraBypassApps),
            intent.GetBooleanExtra(ExtraLocalInTunnel, false),
            intent.GetBooleanExtra(ExtraWsOffered, false));
    }

    // The stop the user asked for: what it takes down must not come back with always-on or after a kill.
    private void Stop()
    {
        VpnBridge.ClearRequest();
        Teardown(VpnStage.Disconnected, null);
    }

    // Tells the head the stage and, on a live session, the link it was last told.
    private void Answer()
    {
        Publish(_stage, _detail, _reason);
        if (_stage == VpnStage.Connected && _linkHandshake > 0)
        {
            VpnBridge.PublishLink(this, _linkHandshake, _linkReading);
        }
    }

    // Tells the head the link and keeps it for the next question.
    private void PublishLink(long handshake, LinkReading reading)
    {
        _linkHandshake = handshake;
        _linkReading = reading;
        VpnBridge.PublishLink(this, handshake, reading);
    }

    private void Teardown(VpnStage stage, string? detail, string? reason = null)
    {
        Interlocked.Exchange(ref _dial, null)?.Cancel();
        _linkHandshake = 0;
        _linkReading = LinkReading.Empty;
        _unvalidatedSince = 0;
        _unvalidatedNoted = false;
        Release();
        _session.Closed();
        KeepMarks();
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

    // Applies the idle window to both caches.
    // Takes the address behind a name the relay refused: the engine refuses its datagrams too, so a name in the
    // block list is not walked around over QUIC.
    private void Refuse(System.Net.IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || _verdicts.Length == 0)
        {
            return;
        }

        lock (_refused)
        {
            if (_refused.Count >= RefusedHeld || !_refused.Add(address.ToString()))
            {
                return;
            }
        }

        if (Interlocked.Exchange(ref _refusing, 1) == 0)
        {
            _ = Task.Run(HandRefusedAsync);
        }
    }

    private string[] Refused()
    {
        lock (_refused)
        {
            return [.. _refused];
        }
    }

    // Hands what has gathered to the engine in one go.
    private async Task HandRefusedAsync()
    {
        await Task.Delay(RefusedDelayMs).ConfigureAwait(false);
        Interlocked.Exchange(ref _refusing, 0);
        var handle = _handle;
        string[] addresses;
        lock (_refused)
        {
            addresses = [.. _refused];
        }

        if (handle < 0 || addresses.Length == 0)
        {
            return;
        }

        var spec = new StringBuilder(_verdicts);
        foreach (var address in addresses)
        {
            spec.Append('\n').Append(address).Append("/32=block");
        }

        if (AwgEngine.SetVerdicts(handle, spec.ToString()))
        {
            Report($"{addresses.Length} address(es) behind blocked names are refused to datagrams as well");
        }
    }

    private void ApplyRouteTtl()
    {
        var seconds = VpnBridge.ReadRouteTtl();
        if (seconds <= 0)
        {
            return;
        }

        _ttlSeconds = seconds;
        _relay?.SetTtl(seconds);
        var handle = _handle;
        if (handle >= 0)
        {
            AwgEngine.SetVerdictTtl(handle, seconds);
        }

        Report($"a destination unused for {seconds} s is now released and decided again on the next contact");
    }

    private void Release()
    {
        lock (_releaseGate)
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
            _shape = null;
            _excluded = [];
            _liveTun = false;
            _proxyPort = 0;
            _proxyEnd = null;
            _packages.Clear();
            _owners.Clear();
            _verdicts = string.Empty;
            lock (_refused)
            {
                _refused.Clear();
            }

            _carrier?.Dispose();
            _carrier = null;
            if (_handle >= 0)
            {
                KeepHotDirect();
                KeepLive();
                AwgEngine.TurnOff(_handle);
                _handle = -1;
            }
        }
    }
}
