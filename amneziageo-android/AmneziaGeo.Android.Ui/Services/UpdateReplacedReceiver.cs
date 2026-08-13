using Android.App;
using Android.Content;
using Android.OS;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Возврат в приложение после замены пакета: открывает окно, когда установку запускал пользователь,
/// и оставляет уведомление, когда система не дала открыть его из фона.
/// </summary>
[BroadcastReceiver(Name = "org.amneziageo.android.UpdateReplacedReceiver", Exported = false)]
[IntentFilter(new[] { Intent.ActionMyPackageReplaced })]
internal sealed class UpdateReplacedReceiver : BroadcastReceiver
{
    private const string ChannelId = "amneziageo-update";
    private const int NotificationId = 0x7A20;
    private const string MarkerName = "update-reopen";

    /// <summary>
    /// Метка, которую обновлятель кладёт перед установкой из окна: переживает смерть процесса.
    /// </summary>
    public static void Arm(Context context)
    {
        try
        {
            System.IO.File.WriteAllText(MarkerPath(context), "1");
        }
        catch (Exception)
        {
        }
    }

    /// <inheritdoc/>
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || !string.Equals(intent?.Action, Intent.ActionMyPackageReplaced, StringComparison.Ordinal))
        {
            return;
        }

        var marker = MarkerPath(context);
        if (!System.IO.File.Exists(marker))
        {
            return;
        }

        try
        {
            System.IO.File.Delete(marker);
        }
        catch (Exception)
        {
        }

        var pending = GoAsync();
        _ = ReopenAsync(context, pending);
    }

    private static string MarkerPath(Context context)
        => System.IO.Path.Combine(context.FilesDir?.AbsolutePath ?? System.IO.Path.GetTempPath(), MarkerName);

    // Старт окна из фона система может отбросить молча, поэтому итог проверяется по живой активности.
    private static async Task ReopenAsync(Context context, PendingResult? pending)
    {
        try
        {
            Launch(context);
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (MainActivity.Current is null)
            {
                Notify(context);
            }
        }
        catch (Exception)
        {
            Notify(context);
        }
        finally
        {
            pending?.Finish();
        }
    }

    private static void Launch(Context context)
    {
        if (LaunchIntent(context) is not { } intent)
        {
            return;
        }

        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        context.StartActivity(intent);
    }

    private static void Notify(Context context)
    {
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channel = new NotificationChannel(ChannelId, "AmneziaGeo", NotificationImportance.Default);
                (context.GetSystemService(Context.NotificationService) as NotificationManager)?.CreateNotificationChannel(channel);
            }

            var launch = LaunchIntent(context);
            launch?.AddFlags(ActivityFlags.NewTask);
            var content = PendingIntent.GetActivity(
                context,
                0,
                launch ?? new Intent(),
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

            var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
                ? new Notification.Builder(context, ChannelId)
                : new Notification.Builder(context);
            var notification = builder
                .SetContentTitle("AmneziaGeo")
                .SetContentText(Loc.Instance.Get("Android_UpdateReopen"))
                .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
                .SetContentIntent(content)
                .SetAutoCancel(true)
                .Build();
            (context.GetSystemService(Context.NotificationService) as NotificationManager)?.Notify(NotificationId, notification);
        }
        catch (Exception)
        {
        }
    }

    private static Intent? LaunchIntent(Context context)
        => context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? string.Empty);
}
