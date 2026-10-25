using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using AndroidX.Core.App;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Называет ограничения фона на устройстве и открывает их системные экраны.
/// </summary>
internal static class AndroidBackgroundLimits
{
    private const string Tag = "AmneziaGeo";

    // Приложение MIUI с экраном автозапуска.
    private const string SecurityCenter = "com.miui.securitycenter";

    private const string AutostartScreen = "com.miui.permcenter.autostart.AutoStartManagementActivity";

    // Какие строки есть на этом устройстве.
    private static bool? _television;
    private static bool? _hasBatteryScreen;
    private static bool? _hasAutostartScreen;

    /// <summary>
    /// Отдаёт окну ограничения этого устройства.
    /// </summary>
    public static void Register() => BackgroundLimitsBridge.Register(Read, Open);

    /// <summary>
    /// Называет ограничения, которые есть на устройстве; состояние - только у уведомлений.
    /// </summary>
    public static IReadOnlyList<BackgroundLimit> Read()
    {
        var context = Application.Context;
        var phone = !(_television ??= IsTelevision(context));
        var limits = new List<BackgroundLimit>();
        if (phone)
        {
            limits.Add(new(BackgroundLimitKind.Notifications, Told(NotificationsOn(context))));
        }

        if (_hasBatteryScreen ??= phone && Resolves(context, BatterySettings()))
        {
            limits.Add(new(BackgroundLimitKind.Battery, BackgroundLimitState.Unknown));
        }

        if (_hasAutostartScreen ??= Resolves(context, AutostartIntent()))
        {
            limits.Add(new(BackgroundLimitKind.Autostart, BackgroundLimitState.Unknown));
        }

        return limits;
    }

    /// <summary>
    /// Открывает системный экран ограничения.
    /// </summary>
    public static void Open(BackgroundLimitKind kind)
    {
        var context = Application.Context;
        switch (kind)
        {
            case BackgroundLimitKind.Notifications:
                OpenNotifications(context);
                break;
            case BackgroundLimitKind.Battery:
                Launch(BatterySettings());
                break;
            default:
                Launch(AutostartIntent());
                break;
        }
    }

    private static BackgroundLimitState Told(bool free)
    {
        return free ? BackgroundLimitState.Free : BackgroundLimitState.Limited;
    }

    private static bool IsTelevision(Context context)
    {
        return context.PackageManager?.HasSystemFeature(PackageManager.FeatureLeanback) == true;
    }

    private static bool NotificationsOn(Context context)
    {
        return NotificationManagerCompat.From(context)?.AreNotificationsEnabled() ?? true;
    }

    private static void OpenNotifications(Context context)
    {
        if (!NotificationsOn(context) && OperatingSystem.IsAndroidVersionAtLeast(33) && MainActivity.Current is { } activity)
        {
            _ = AskNotificationsAsync(activity);
            return;
        }

        Launch(NotificationSettings(context));
    }

    // Просит разрешение на уведомления, а когда система вопроса не показывает, открывает их настройки.
    private static async Task AskNotificationsAsync(MainActivity activity)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            return;
        }

        var permission = global::Android.Manifest.Permission.PostNotifications;
        var refused = ActivityCompat.ShouldShowRequestPermissionRationale(activity, permission);
        var granted = await activity.RequestNotificationPermissionAsync().ConfigureAwait(false);
        if (!granted && !refused && !ActivityCompat.ShouldShowRequestPermissionRationale(activity, permission))
        {
            Launch(NotificationSettings(activity));
        }
    }

    // Экран уведомлений приложения, а где его нет, экран сведений о приложении.
    private static Intent NotificationSettings(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return AppDetails(context);
        }

        var intent = new Intent(Settings.ActionAppNotificationSettings);
        intent.PutExtra(Settings.ExtraAppPackage, context.PackageName);
        return intent;
    }

    // Системный список исключений из экономии батареи.
    private static Intent BatterySettings()
    {
        return new Intent(Settings.ActionIgnoreBatteryOptimizationSettings);
    }

    // Экран сведений о приложении.
    private static Intent AppDetails(Context context)
    {
        return new Intent(Settings.ActionApplicationDetailsSettings, PackageUri(context));
    }

    // Экран автозапуска MIUI.
    private static Intent AutostartIntent()
    {
        var intent = new Intent();
        intent.SetComponent(new ComponentName(SecurityCenter, AutostartScreen));
        return intent;
    }

    private static global::Android.Net.Uri? PackageUri(Context context)
    {
        return global::Android.Net.Uri.Parse("package:" + context.PackageName);
    }

    // Есть ли на устройстве экран, который откроет это намерение.
    private static bool Resolves(Context context, Intent intent)
    {
        if (context.PackageManager is not { } manager)
        {
            return false;
        }

        var found = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? manager.ResolveActivity(intent, PackageManager.ResolveInfoFlags.Of((long)PackageInfoFlags.MatchDefaultOnly))
            : manager.ResolveActivity(intent, PackageInfoFlags.MatchDefaultOnly);
        return found?.ActivityInfo is { Exported: true };
    }

    // Открывает экран поверх окна, а без окна отдельной задачей.
    private static void Launch(Intent intent)
    {
        try
        {
            if (MainActivity.Current is { } activity)
            {
                activity.StartActivity(intent);
                return;
            }

            intent.AddFlags(ActivityFlags.NewTask);
            Application.Context.StartActivity(intent);
        }
        catch (Exception ex) when (ex is ActivityNotFoundException or Java.Lang.SecurityException)
        {
            global::Android.Util.Log.Warn(Tag, "the system screen did not open: " + ex);
        }
    }
}
