using System.Text.Json;
using Android.App;
using Android.Content;
using Android.OS;

namespace AmneziaGeo.Android.Engine;

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

    private const string PlanFile = "plan.json";
    private const string ProcessSuffix = ":vpn";

    /// <summary>
    /// Reports a stage to the head.
    /// </summary>
    public static void Publish(Context context, VpnStage stage, string? detail, string? reason = null)
    {
        var intent = Broadcast(context, ActionEvent);
        intent.PutExtra(ExtraStage, (int)stage);
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
    /// Reports the peer's last handshake to the head.
    /// </summary>
    public static void PublishHandshake(Context context, long unixSeconds)
    {
        var intent = Broadcast(context, ActionEvent);
        intent.PutExtra(ExtraHandshake, unixSeconds);
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

    private static Intent Broadcast(Context context, string action)
    {
        var intent = new Intent(action);
        intent.SetPackage(context.PackageName);
        return intent;
    }

    private static string PlanPath() =>
        Path.Combine(Application.Context.FilesDir?.AbsolutePath ?? ".", PlanFile);

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
