using Android.App;
using Android.Content;
using AmneziaGeo.Android.Engine;
using AmneziaGeo.Decl;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Уведомление о найденной версии приложения: единственное, что ставит отметку на значок, и держится, пока эта
/// версия не установлена.
/// </summary>
internal static class UpdateFound
{
    private const string ChannelId = "amneziageo.update-found";
    private const int NotificationId = 0x7A21;
    private const string MarkerName = "update-found";

    /// <summary>
    /// Сообщает о найденной версии один раз; пустая версия снимает уведомление.
    /// </summary>
    public static void Tell(Context context, string version)
    {
        if (version.Length == 0)
        {
            Withdraw(context);
            return;
        }

        if (!string.Equals(Announced(context), version, StringComparison.Ordinal) && Post(context, version))
        {
            Mark(context, version);
        }
    }

    /// <summary>
    /// Снимает уведомление о версии, которая уже стоит.
    /// </summary>
    public static void Settle(Context context)
    {
        var announced = Announced(context);
        if (announced.Length > 0 && !UpdateFeed.IsUpdate(announced, Installed(context)))
        {
            Withdraw(context);
        }
    }

    private static bool Post(Context context, string version)
    {
        try
        {
            if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
            {
                return false;
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                manager.CreateNotificationChannel(
                    new NotificationChannel(ChannelId, Loc.Instance.Get("Android_UpdateFoundChannel"), NotificationImportance.Low));
            }

            var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? new Notification.Builder(context, ChannelId)
                : new Notification.Builder(context);
            builder
                .SetContentTitle("AmneziaGeo")
                .SetContentText(Loc.Instance.Get("Android_UpdateFound", version))
                .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownloadDone)
                .SetShowWhen(false);
            if (TunnelNotices.Icon(context) is { } launch)
            {
                builder.SetContentIntent(PendingIntent.GetActivity(
                    context, NotificationId, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
            }

            manager.Notify(NotificationId, builder.Build());
            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("AmneziaGeo", "the notification of the update was not posted: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Снимает уведомление и забывает версию, о которой сказано.
    /// </summary>
    public static void Withdraw(Context context)
    {
        try
        {
            (context.GetSystemService(Context.NotificationService) as NotificationManager)?.Cancel(NotificationId);
            System.IO.File.Delete(MarkerPath(context));
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("AmneziaGeo", "the notification of the update was not taken off: " + ex);
        }
    }

    // Версия, о которой уже сказано.
    private static string Announced(Context context)
    {
        try
        {
            var marker = MarkerPath(context);
            return System.IO.File.Exists(marker) ? System.IO.File.ReadAllText(marker).Trim() : string.Empty;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("AmneziaGeo", "the announced update was not read: " + ex);
            return string.Empty;
        }
    }

    private static void Mark(Context context, string version)
    {
        try
        {
            System.IO.File.WriteAllText(MarkerPath(context), version);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("AmneziaGeo", "the announced update was not kept: " + ex);
        }
    }

    // Версия этой сборки по словам системы.
    private static string Installed(Context context)
    {
        try
        {
            var name = context.PackageName;
            return (name is null ? null : context.PackageManager?.GetPackageInfo(name, 0)?.VersionName) ?? string.Empty;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("AmneziaGeo", "the installed version was not read: " + ex);
            return string.Empty;
        }
    }

    private static string MarkerPath(Context context)
        => System.IO.Path.Combine(context.FilesDir?.AbsolutePath ?? System.IO.Path.GetTempPath(), MarkerName);
}
