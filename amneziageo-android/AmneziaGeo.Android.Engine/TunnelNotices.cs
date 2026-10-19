using System.Runtime.Versioning;
using Android.App;
using Android.Content;
using Android.OS;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// Builds and posts the notifications of the tunnel: the one the service runs under, the one a tunnel taken down
/// leaves behind, and the one of a tunnel that stopped on its own.
/// </summary>
public static class TunnelNotices
{
    /// <summary>
    /// Category of the notification the service runs under.
    /// </summary>
    public const string TunnelChannel = "amneziageo.tunnel";

    /// <summary>
    /// Category of the notification of a tunnel that stopped on its own.
    /// </summary>
    public const string StoppedChannel = "amneziageo.tunnel-stopped";

    /// <summary>
    /// Id of the notification the service runs under and leaves behind.
    /// </summary>
    public const int TunnelId = 1001;

    /// <summary>
    /// Id of the notification of a tunnel that stopped on its own.
    /// </summary>
    public const int StoppedId = 1002;

    /// <summary>
    /// Action of the head: raise the selected configuration.
    /// </summary>
    public const string ActionConnect = "org.amneziageo.android.NOTICE_CONNECT";

    /// <summary>
    /// Class of the receiver in the process of the window that carries the action out.
    /// </summary>
    public const string ReceiverName = "org.amneziageo.android.NoticeReceiver";

    private const string TunnelChannelName = "VPN";
    private const int OpenCode = 1;
    private const int DisconnectCode = 2;
    private const int ConnectCode = 3;

    // Categories earlier builds posted under.
    private static readonly string[] Retired = ["amneziageo.vpn", "amneziageo.stopped", "amneziageo.update", "amneziageo-update"];

    // The name the categories were last created under in this process.
    private static string? _channelsNamed;

    /// <summary>
    /// Composes the notification of a stage: the name of the configuration, the stage line, the routing list and
    /// the action the stage allows.
    /// </summary>
    public static Notification Compose(Context context, NoticeWords words, string name, NoticeStage stage, int retry,
        long rxBitsPerSecond, long txBitsPerSecond, long since)
    {
        EnsureChannels(context, words);
        var stopped = stage == NoticeStage.Stopped;
        var running = stage is NoticeStage.Connecting or NoticeStage.Connected;
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(context, stopped ? StoppedChannel : TunnelChannel)
            : new Notification.Builder(context);
        var text = TunnelNotice.Text(words, stage, retry, rxBitsPerSecond, txBitsPerSecond);
        builder
            .SetContentTitle(name)
            .SetContentText(text)
            .SetSmallIcon(Resource.Drawable.ic_stat_tunnel)
            .SetOnlyAlertOnce(true)
            .SetOngoing(running)
            .SetShowWhen(true)
            .SetWhen(since)
            .SetContentIntent(Open(context));
        if (!stopped && TunnelNotice.Routing(words) is { } routing)
        {
            builder.SetStyle(new Notification.BigTextStyle().BigText(text + "\n" + routing));
        }

        builder.AddAction(running
            ? Action(words.Disconnect, Service(context))
            : Action(words.Connect, Head(context, ActionConnect, ConnectCode)));
        return builder.Build();
    }

    /// <summary>
    /// Posts a notification under the id given; false when the system refused it.
    /// </summary>
    public static bool Post(Context context, int id, Notification notification)
    {
        try
        {
            Manager(context)?.Notify(id, notification);
            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("TunnelNotices", "the notification was not posted: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Takes the notification with the id given off the shade.
    /// </summary>
    public static void Cancel(Context context, int id)
    {
        try
        {
            Manager(context)?.Cancel(id);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("TunnelNotices", "the notification was not taken off: " + ex);
        }
    }

    /// <summary>
    /// Whether the notification with the id given is on the shade.
    /// </summary>
    public static bool Shown(Context context, int id)
    {
        try
        {
            var active = Manager(context)?.GetActiveNotifications();
            return active is not null && active.Any(one => one.Id == id);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("TunnelNotices", "the notifications on the shade were not read: " + ex);
            return false;
        }
    }

    // Creates the two categories once for a name; the name of the second one comes in the language of the window.
    private static void EnsureChannels(Context context, NoticeWords words)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26) || string.Equals(_channelsNamed, words.StoppedChannel, StringComparison.Ordinal))
        {
            return;
        }

        if (Manager(context) is not { } manager)
        {
            return;
        }

        manager.CreateNotificationChannel(Unmarked(TunnelChannel, TunnelChannelName, NotificationImportance.Low));
        manager.CreateNotificationChannel(Unmarked(StoppedChannel, words.StoppedChannel, NotificationImportance.Default));
        Retire(manager);
        _channelsNamed = words.StoppedChannel;
    }

    /// <summary>
    /// Category whose notifications leave no mark on the icon of the application.
    /// </summary>
    [SupportedOSPlatform("android26.0")]
    public static NotificationChannel Unmarked(string id, string name, NotificationImportance importance)
    {
        var channel = new NotificationChannel(id, name, importance);
        channel.SetShowBadge(false);
        return channel;
    }

    // Deletes the categories earlier builds posted under, together with their notifications on the shade.
    [SupportedOSPlatform("android26.0")]
    private static void Retire(NotificationManager manager)
    {
        foreach (var id in Retired)
        {
            try
            {
                manager.DeleteNotificationChannel(id);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("TunnelNotices", "the category " + id + " was not deleted: " + ex);
            }
        }
    }

    private static NotificationManager? Manager(Context context) =>
        (NotificationManager?)context.GetSystemService(Context.NotificationService);

    private static Notification.Action Action(string label, PendingIntent? intent) =>
        new Notification.Action.Builder((global::Android.Graphics.Drawables.Icon?)null, label, intent).Build();

    /// <summary>
    /// The intent the icon of the application starts it with; null where the system names no launcher activity.
    /// </summary>
    public static Intent? Icon(Context context)
    {
        var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? string.Empty);
        launch?.SetPackage(null);
        launch?.SetFlags(ActivityFlags.NewTask | ActivityFlags.ResetTaskIfNeeded);
        return launch;
    }

    // Opens the application the way its icon does.
    private static PendingIntent? Open(Context context)
    {
        var launch = Icon(context);
        return launch is null
            ? null
            : PendingIntent.GetActivity(context, OpenCode, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
    }

    // Takes the tunnel down in the service itself.
    private static PendingIntent? Service(Context context)
    {
        var intent = new Intent(context, typeof(GeoVpnService));
        intent.SetAction(GeoVpnService.ActionDisconnect);
        return PendingIntent.GetService(context, DisconnectCode, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
    }

    // Hands an action to the head, which lives in the process of the window.
    private static PendingIntent? Head(Context context, string action, int code)
    {
        var intent = new Intent(action);
        intent.SetClassName(context.PackageName ?? string.Empty, ReceiverName);
        return PendingIntent.GetBroadcast(context, code, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
    }
}
