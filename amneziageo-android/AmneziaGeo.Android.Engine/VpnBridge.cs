using System.Text.Json;
using Android.App;
using Android.Content;
using Android.OS;
using AmneziaGeo.Ipc;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// Session the tunnel raises when no head asks for it: the system starts an always-on tunnel with a bare intent,
/// and a tunnel the system killed comes back the same way.
/// </summary>
public sealed record VpnRequest(
    string Config,
    string Name,
    string? AppMode,
    string[]? AppList,
    int Mtu,
    bool Ipv6,
    string? WsHost,
    int WsPort);

/// <summary>
/// Carries the routing rules and the tunnel stage between the head and the tunnel, which run in separate
/// processes: the head can then be unloaded whole while the tunnel keeps running.
/// </summary>
public static class VpnBridge
{
    /// <summary>
    /// Broadcast the tunnel sends on every stage change and every routing line.
    /// </summary>
    public const string ActionEvent = "org.amneziageo.android.VPN_EVENT";

    /// <summary>
    /// Broadcast that makes a running tunnel report its stage again.
    /// </summary>
    public const string ActionQuery = "org.amneziageo.android.VPN_QUERY";

    /// <summary>
    /// Broadcast that stops a running tunnel.
    /// </summary>
    public const string ActionStop = "org.amneziageo.android.VPN_STOP";

    /// <summary>
    /// Stage extra, a <see cref="VpnStage"/> value.
    /// </summary>
    public const string ExtraStage = "stage";

    /// <summary>
    /// Session name or failure reason extra.
    /// </summary>
    public const string ExtraDetail = "detail";

    /// <summary>
    /// Routing line extra.
    /// </summary>
    public const string ExtraTrace = "trace";

    /// <summary>
    /// Failure reason extra, a <see cref="AmneziaGeo.Ipc.ConnectFailureReason"/> name.
    /// </summary>
    public const string ExtraReason = "reason";

    /// <summary>
    /// Peer handshake extra: unix seconds of the server's last answer.
    /// </summary>
    public const string ExtraHandshake = "handshake";

    /// <summary>
    /// Receive rate extra: bits per second the peer carries in.
    /// </summary>
    public const string ExtraRxBits = "rxbits";

    /// <summary>
    /// Send rate extra: bits per second the peer carries out.
    /// </summary>
    public const string ExtraTxBits = "txbits";

    /// <summary>
    /// Handshake rate extra: sessions established per minute.
    /// </summary>
    public const string ExtraChurn = "churn";

    /// <summary>
    /// Loss extra: share of the tunnel's own echoes that never came back.
    /// </summary>
    public const string ExtraLoss = "loss";

    /// <summary>
    /// Round trip extra: milliseconds the tunnel's own echo took to the far end.
    /// </summary>
    public const string ExtraRtt = "rtt";

    /// <summary>
    /// Always-on extra: whether the system runs this tunnel as its always-on VPN.
    /// </summary>
    public const string ExtraAlwaysOn = "alwayson";

    /// <summary>
    /// Lockdown extra: whether always-on also blocks what would leave outside the tunnel.
    /// </summary>
    public const string ExtraLockdown = "lockdown";

    /// <summary>
    /// Broadcast that makes a running tunnel take the local proxy settings again.
    /// </summary>
    public const string ActionProxy = "org.amneziageo.android.VPN_PROXY";

    private const string PlanFile = "plan.json";
    private const string ProxyFile = "proxy.json";
    private const string ProxyStateFile = "proxy-state.json";
    private const string SessionsFile = "sessions.txt";
    private const string RequestFile = "session.json";
    private const string ProcessSuffix = ":vpn";

    /// <summary>
    /// Reports a stage to the head.
    /// </summary>
    public static void Publish(Context context, VpnStage stage, string? detail, string? reason = null,
        bool alwaysOn = false, bool lockdown = false)
    {
        var intent = Broadcast(context, ActionEvent);
        intent.PutExtra(ExtraStage, (int)stage);
        intent.PutExtra(ExtraAlwaysOn, alwaysOn);
        intent.PutExtra(ExtraLockdown, lockdown);
        if (detail is not null)
        {
            intent.PutExtra(ExtraDetail, detail);
        }

        if (reason is not null)
        {
            intent.PutExtra(ExtraReason, reason);
        }

        context.SendBroadcast(intent);
    }

    /// <summary>
    /// Reports the peer's last handshake and what the link carries to the head.
    /// </summary>
    public static void PublishLink(Context context, long unixSeconds, LinkReading reading)
    {
        var intent = Broadcast(context, ActionEvent);
        intent.PutExtra(ExtraHandshake, unixSeconds);
        intent.PutExtra(ExtraRxBits, reading.RxBitsPerSecond);
        intent.PutExtra(ExtraTxBits, reading.TxBitsPerSecond);
        intent.PutExtra(ExtraChurn, reading.HandshakesPerMinute);
        intent.PutExtra(ExtraLoss, reading.LossPercent);
        intent.PutExtra(ExtraRtt, reading.RttMs);
        context.SendBroadcast(intent);
    }

    /// <summary>
    /// Reports a routing line to the head.
    /// </summary>
    public static void PublishTrace(Context context, string line)
    {
        var intent = Broadcast(context, ActionEvent);
        intent.PutExtra(ExtraTrace, line);
        context.SendBroadcast(intent);
    }

    /// <summary>
    /// Asks a running tunnel to report its stage; a tunnel that is not running answers nothing.
    /// </summary>
    public static void RequestState(Context context) => context.SendBroadcast(Broadcast(context, ActionQuery));

    /// <summary>
    /// Asks a running tunnel to stop; a service the head could start instead is barred from the background.
    /// </summary>
    public static void RequestStop(Context context) => context.SendBroadcast(Broadcast(context, ActionStop));

    /// <summary>
    /// Whether the tunnel process is alive; a tunnel the system has killed answers no query.
    /// </summary>
    public static bool IsRunning(Context context)
    {
        var manager = (ActivityManager?)context.GetSystemService(Context.ActivityService);
        foreach (var process in manager?.RunningAppProcesses ?? [])
        {
            if (process.ProcessName is { } name && name.EndsWith(ProcessSuffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Subscribes a receiver to one action inside the application.
    /// </summary>
    public static void Listen(Context context, BroadcastReceiver receiver, string action)
    {
        var filter = new IntentFilter(action);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        }
        else
        {
            context.RegisterReceiver(receiver, filter);
        }
    }

    /// <summary>
    /// Writes the rules the next session routes by; the lists are too large for a Binder transaction.
    /// </summary>
    public static void WritePlan(GeoRoutingPlan plan)
    {
        try
        {
            using var stream = File.Create(PlanPath());
            JsonSerializer.Serialize(stream, plan);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnBridge", "writing the routing plan failed: " + ex);
        }
    }

    /// <summary>
    /// Reads the rules written for this session, the whole tunnel when there are none.
    /// </summary>
    public static GeoRoutingPlan ReadPlan()
    {
        try
        {
            var path = PlanPath();
            if (!File.Exists(path))
            {
                return GeoRoutingPlan.Full;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<GeoRoutingPlan>(stream) ?? GeoRoutingPlan.Full;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnBridge", "reading the routing plan failed: " + ex);
            return GeoRoutingPlan.Full;
        }
    }

    /// <summary>
    /// Writes the session to raise again without a head.
    /// </summary>
    public static void WriteRequest(VpnRequest request)
    {
        try
        {
            using var stream = File.Create(RequestPath());
            JsonSerializer.Serialize(stream, request);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnBridge", "writing the session failed: " + ex);
        }
    }

    /// <summary>
    /// Reads the session the last connect ran on, nothing once the user has taken the tunnel down.
    /// </summary>
    public static VpnRequest? ReadRequest()
    {
        try
        {
            var path = RequestPath();
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            var request = JsonSerializer.Deserialize<VpnRequest>(stream);
            return request is { Config.Length: > 0 } ? request : null;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnBridge", "reading the session failed: " + ex);
            return null;
        }
    }

    /// <summary>
    /// Drops the session, so a tunnel the user stopped stays down.
    /// </summary>
    public static void ClearRequest()
    {
        try
        {
            File.Delete(RequestPath());
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnBridge", "dropping the session failed: " + ex);
        }
    }

    /// <summary>
    /// Writes the local proxy settings for the tunnel to listen by.
    /// </summary>
    public static void WriteProxy(LocalProxyOptions options)
    {
        try
        {
            using var stream = File.Create(ProxyPath());
            JsonSerializer.Serialize(stream, options);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnBridge", "writing the proxy settings failed: " + ex);
        }
    }

    /// <summary>
    /// Reads the local proxy settings; a tunnel that never got any listens on nothing.
    /// </summary>
    public static LocalProxyOptions ReadProxy()
    {
        try
        {
            var path = ProxyPath();
            if (!File.Exists(path))
            {
                return new LocalProxyOptions();
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<LocalProxyOptions>(stream) ?? new LocalProxyOptions();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnBridge", "reading the proxy settings failed: " + ex);
            return new LocalProxyOptions();
        }
    }

    /// <summary>
    /// Asks a running tunnel to take the proxy settings again; a tunnel that is not running takes them at start.
    /// </summary>
    public static void RequestProxy(Context context) => context.SendBroadcast(Broadcast(context, ActionProxy));

    /// <summary>
    /// Writes whether the listener came up, for the head to show.
    /// </summary>
    public static void WriteProxyState(bool running, string error)
    {
        try
        {
            File.WriteAllText(ProxyStatePath(), $"{(running ? "1" : "0")}\n{error}");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnBridge", "writing the proxy state failed: " + ex);
        }
    }

    /// <summary>
    /// Reads what the tunnel said about its listener.
    /// </summary>
    public static (bool Running, string Error) ReadProxyState()
    {
        try
        {
            var path = ProxyStatePath();
            if (!File.Exists(path))
            {
                return (false, string.Empty);
            }

            var lines = File.ReadAllLines(path);
            return (lines.Length > 0 && lines[0] == "1", lines.Length > 1 ? lines[1] : string.Empty);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnBridge", "reading the proxy state failed: " + ex);
            return (false, string.Empty);
        }
    }

    /// <summary>
    /// Writes what the relay holds for the head to read; a broadcast carries a stage, not a table.
    /// </summary>
    public static void WriteSessions(SessionReport report)
    {
        try
        {
            File.WriteAllText(SessionsPath(), report.ToPayload());
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnBridge", "writing the relay snapshot failed: " + ex);
        }
    }

    /// <summary>
    /// Reads the last snapshot the tunnel wrote; one older than the window says nothing about what runs now.
    /// </summary>
    public static SessionReport ReadSessions(int freshSeconds)
    {
        try
        {
            var path = SessionsPath();
            if (!File.Exists(path))
            {
                return SessionReport.Empty;
            }

            var report = SessionReport.Parse(File.ReadAllText(path));
            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - report.UnixMs;
            return age >= 0 && age <= freshSeconds * 1000L ? report : SessionReport.Empty;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnBridge", "reading the relay snapshot failed: " + ex);
            return SessionReport.Empty;
        }
    }

    /// <summary>
    /// Drops the snapshot a stopped tunnel left behind.
    /// </summary>
    public static void ClearSessions()
    {
        try
        {
            File.Delete(SessionsPath());
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnBridge", "dropping the relay snapshot failed: " + ex);
        }
    }

    private static Intent Broadcast(Context context, string action)
    {
        var intent = new Intent(action);
        intent.SetPackage(context.PackageName);
        return intent;
    }

    private static string PlanPath() =>
        Path.Combine(Application.Context.FilesDir?.AbsolutePath ?? ".", PlanFile);

    private static string ProxyPath() =>
        Path.Combine(Application.Context.FilesDir?.AbsolutePath ?? ".", ProxyFile);

    private static string ProxyStatePath() =>
        Path.Combine(Application.Context.FilesDir?.AbsolutePath ?? ".", ProxyStateFile);

    private static string SessionsPath() =>
        Path.Combine(Application.Context.FilesDir?.AbsolutePath ?? ".", SessionsFile);

    private static string RequestPath() =>
        Path.Combine(Application.Context.FilesDir?.AbsolutePath ?? ".", RequestFile);

    /// <summary>
    /// Receiver handing every broadcast to a delegate.
    /// </summary>
    public sealed class Listener : BroadcastReceiver
    {
        /// <summary>
        /// Called with each broadcast received.
        /// </summary>
        public Action<Intent>? Handler { get; set; }

        /// <inheritdoc/>
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent is not null)
            {
                Handler?.Invoke(intent);
            }
        }
    }
}
