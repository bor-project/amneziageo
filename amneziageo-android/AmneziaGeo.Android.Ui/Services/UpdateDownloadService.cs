using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Держит процесс и процессор, пока качается пакет обновления: на погасшем экране система замораживает
/// приложение вместе с загрузкой.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
internal sealed class UpdateDownloadService : Service
{
    private const string ChannelId = "amneziageo.update";
    private const int NotificationId = 4711;

    private PowerManager.WakeLock? _wake;

    /// <summary>
    /// Поднимает службу на время загрузки.
    /// </summary>
    public static void Start(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(UpdateDownloadService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("AmneziaGeo", "the download service was refused: " + ex);
        }
    }

    /// <summary>
    /// Опускает её, чем бы загрузка ни кончилась.
    /// </summary>
    public static void Stop(Context context)
    {
        try
        {
            context.StopService(new Intent(context, typeof(UpdateDownloadService)));
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("AmneziaGeo", "the download service did not stop: " + ex);
        }
    }

    /// <inheritdoc/>
    public override IBinder? OnBind(Intent? intent) => null;

    /// <inheritdoc/>
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (!Announce())
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        Hold();
        return StartCommandResult.NotSticky;
    }

    /// <inheritdoc/>
    public override void OnDestroy()
    {
        Release();
        base.OnDestroy();
    }

    // Старт без нотификации система обрывает за секунды, поэтому отказ называется здесь.
    private bool Announce()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            var channel = new NotificationChannel(ChannelId, "Update", NotificationImportance.Low);
            manager?.CreateNotificationChannel(channel);
        }

        try
        {
            var notification = Build.VERSION.SdkInt >= BuildVersionCodes.O
                ? new Notification.Builder(this, ChannelId)
                : new Notification.Builder(this);
            var built = notification
                .SetContentTitle("AmneziaGeo")
                .SetContentText(Loc.Instance.Get("Android_UpdateDownloadNotification"))
                .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
                .SetOngoing(true)
                .Build();

            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
            {
                StartForeground(NotificationId, built, ForegroundService.TypeDataSync);
            }
            else
            {
                StartForeground(NotificationId, built);
            }

            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("AmneziaGeo", "the foreground start was refused: " + ex);
            return false;
        }
    }

    // Срок задан на случай, если загрузка оборвётся, не сняв блокировку.
    private void Hold()
    {
        if (_wake is not null)
        {
            return;
        }

        var power = (PowerManager?)GetSystemService(PowerService);
        _wake = power?.NewWakeLock(WakeLockFlags.Partial, "amneziageo:update");
        _wake?.Acquire((long)TimeSpan.FromHours(2).TotalMilliseconds);
    }

    private void Release()
    {
        if (_wake is { IsHeld: true })
        {
            _wake.Release();
        }

        _wake = null;
    }
}
