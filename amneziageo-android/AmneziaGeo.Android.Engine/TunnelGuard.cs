using Android.App;
using Android.Content;
using Android.OS;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// Raises a tunnel whose process the system killed. While a session is wanted the service keeps pushing an alarm
/// of the system ahead of itself; once the process is gone the alarm comes due, the system starts this receiver in
/// a new tunnel process, and the receiver starts the service on the session the disk holds. A tunnel that keeps
/// dying is given up, and a notification says it has stopped.
/// </summary>
[BroadcastReceiver(Name = "org.amneziageo.android.TunnelGuard", Exported = false, Enabled = true, Process = ":vpn")]
public sealed class TunnelGuard : BroadcastReceiver
{
    /// <summary>
    /// How often the service pushes the alarm ahead, in milliseconds.
    /// </summary>
    public const int PushEveryMs = 60_000;

    private const string Action = "org.amneziageo.android.GUARD";
    private const string TallyFile = "guard.txt";
    private const int Code = 5;
    private const long AheadMs = 120_000;
    private const int LeaveDelayMs = 1_000;

    /// <summary>
    /// Pushes the alarm ahead: it comes due only when nobody pushes it again.
    /// </summary>
    public static void Arm(Context context)
    {
        try
        {
            var alarms = (AlarmManager?)context.GetSystemService(Context.AlarmService);
            if (Pending(context) is { } pending)
            {
                alarms?.Set(AlarmType.ElapsedRealtime, SystemClock.ElapsedRealtime() + AheadMs, pending);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("TunnelGuard", "the alarm was not set: " + ex);
        }
    }

    /// <summary>
    /// Takes the alarm back: the tunnel is down because somebody wanted it down.
    /// </summary>
    public static void Disarm(Context context)
    {
        try
        {
            var alarms = (AlarmManager?)context.GetSystemService(Context.AlarmService);
            if (Pending(context) is { } pending)
            {
                alarms?.Cancel(pending);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("TunnelGuard", "the alarm was not taken back: " + ex);
        }
    }

    /// <summary>
    /// Forgets the raises made so far: the session stands.
    /// </summary>
    public static void Stood(Context context)
    {
        try
        {
            File.Delete(TallyPath(context));
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("TunnelGuard", "the raises were not forgotten: " + ex);
        }
    }

    /// <inheritdoc/>
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null)
        {
            return;
        }

        if (!Wanted())
        {
            Leave();
            return;
        }

        // The service pushes the alarm itself while its process lives; one that came due all the same is pushed on.
        if (GeoVpnService.Alive)
        {
            Arm(context);
            return;
        }

        var tally = Tally(context);
        if (GuardTally.Spent(tally))
        {
            GiveUp(context, "the tunnel process was killed again and again");
            Leave();
            return;
        }

        try
        {
            File.WriteAllText(TallyPath(context), GuardTally.Next(tally));
            var start = new Intent(context, typeof(GeoVpnService));
            start.SetAction(GeoVpnService.ActionConnect);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                context.StartForegroundService(start);
            }
            else
            {
                context.StartService(start);
            }

            Note(context, "the tunnel process was gone with a session wanted, the session is raised again");
            Arm(context);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("TunnelGuard", "the service did not start: " + ex);
            GiveUp(context, "the system refused to start the tunnel");
            Leave();
        }
    }

    // Ends a tunnel process that was started for the alarm alone: an empty process keeps the whole runtime in
    // memory, and the head reads a live tunnel off the process list.
    private static void Leave()
    {
        if (GeoVpnService.Alive)
        {
            return;
        }

        new Handler(Looper.MainLooper!).PostDelayed(
            () =>
            {
                if (!GeoVpnService.Alive)
                {
                    Process.KillProcess(Process.MyPid());
                }
            },
            LeaveDelayMs);
    }

    // Whether a session is wanted: nobody took the tunnel down, and it was up or coming up when last heard of.
    private static bool Wanted()
    {
        return VpnBridge.HasRequest() && VpnBridge.ReadStage() is { Stage: VpnStage.Connecting or VpnStage.Connected };
    }

    // Leaves the tunnel down and says so on the shade.
    private static void GiveUp(Context context, string why)
    {
        var request = VpnBridge.ReadRequest();
        VpnBridge.WriteStage(VpnStage.Disconnected, null);
        Note(context, why + ", so it stays down until it is connected again");
        Stood(context);
        TunnelNotices.Cancel(context, TunnelNotices.TunnelId);
        if (request is null)
        {
            return;
        }

        var words = request.Notice ?? NoticeWords.Plain;
        TunnelNotices.Post(context, TunnelNotices.StoppedId, TunnelNotices.Compose(context, words, request.Name,
            NoticeStage.Stopped, 0, 0, 0, Java.Lang.JavaSystem.CurrentTimeMillis()));
    }

    // Tells an event to the journal of the system and to the head, and keeps it for a head that is not there.
    private static void Note(Context context, string text)
    {
        global::Android.Util.Log.Warn("TunnelGuard", text);
        VpnBridge.KeepNote("tunnel", text);
        VpnBridge.PublishNote(context, "tunnel", text);
    }

    private static string? Tally(Context context)
    {
        try
        {
            var path = TallyPath(context);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("TunnelGuard", "the raises were not read: " + ex);
            return null;
        }
    }

    private static string TallyPath(Context context) => Path.Combine(context.FilesDir!.AbsolutePath, TallyFile);

    private static PendingIntent? Pending(Context context)
    {
        var intent = new Intent(context, typeof(TunnelGuard));
        intent.SetAction(Action);
        return PendingIntent.GetBroadcast(context, Code, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
    }
}
