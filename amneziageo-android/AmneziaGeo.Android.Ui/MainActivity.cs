using Android.App;
using Android.Content;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using AmneziaGeo.Android.Engine;
using AmneziaGeo.Android.Ui.Services;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// Launcher activity hosting the Avalonia app; also brokers the VpnService consent and the camera and
/// storage permission dialogs.
/// </summary>
[Activity(
    Label = "AmneziaGeo",
    Icon = "@mipmap/appicon",
    Banner = "@drawable/banner",
    Theme = "@style/AppTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
[IntentFilter(new[] { Intent.ActionMain }, Categories = new[] { "android.intent.category.LEANBACK_LAUNCHER" })]
public sealed class MainActivity : AvaloniaMainActivity<App>
{
    private const int VpnRequestCode = 0x7A11;
    private const int CameraRequestCode = 0x7A12;
    private const int StorageRequestCode = 0x7A13;
    private const int NotificationRequestCode = 0x7A14;
    private static TaskCompletionSource<bool>? _vpnPermission;
    private static TaskCompletionSource<bool>? _cameraPermission;
    private static TaskCompletionSource<bool>? _storagePermission;
    private static TaskCompletionSource<bool>? _notificationPermission;
    private static bool _notificationRequested;

    /// <summary>
    /// Raised when the activity comes back to the foreground; screens that sent the user to system settings
    /// re-read what changed.
    /// </summary>
    public static event Action? Resumed;

    /// <summary>
    /// Raised when the window comes to the screen and when it leaves it.
    /// </summary>
    public static event Action<bool>? ShownChanged;

    /// <summary>
    /// The foreground activity, used to launch the VpnService consent dialog.
    /// </summary>
    public static MainActivity? Current { get; private set; }

    /// <summary>
    /// Whether the window is on the screen.
    /// </summary>
    public static bool Shown { get; private set; }

    /// <summary>
    /// Picks the drawing path, overridable by a `render` file holding software or vulkan next to the app data:
    /// the Mali blobs in TV boxes leave stale pixels on screen, and only the device shows which path draws right.
    /// </summary>
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Текст, и моноширинный тоже, берёт у системных шрифтов начертание запрошенного веса.
        return base.CustomizeAppBuilder(builder)
            .With(new AndroidPlatformOptions { RenderingMode = RenderingModes() })
            .With(WeightedSystemFonts.Options("sans-serif", "monospace"))
            .AfterPlatformServicesSetup(_ => WeightedSystemFonts.Register(FontManager.Current));
    }

    private static IReadOnlyList<AndroidRenderingMode> RenderingModes()
    {
        var requested = ReadRenderOverride();
        if (string.Equals(requested, "software", StringComparison.OrdinalIgnoreCase))
        {
            return [AndroidRenderingMode.Software];
        }

        if (string.Equals(requested, "vulkan", StringComparison.OrdinalIgnoreCase))
        {
            return [AndroidRenderingMode.Vulkan, AndroidRenderingMode.Egl, AndroidRenderingMode.Software];
        }

        return [AndroidRenderingMode.Egl, AndroidRenderingMode.Software];
    }

    private static string ReadRenderOverride()
    {
        try
        {
            var folder = global::Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
            if (folder is null)
            {
                return string.Empty;
            }

            var path = Path.Combine(folder, "render");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Launches the VpnService consent dialog and completes when the user answers.
    /// </summary>
    public Task<bool> RequestVpnPermissionAsync(Intent intent)
    {
        var tcs = new TaskCompletionSource<bool>();
        _vpnPermission = tcs;
        RunOnUiThread(() => StartActivityForResult(intent, VpnRequestCode));
        return tcs.Task;
    }

    /// <summary>
    /// Requests the camera permission and completes with whether it was granted.
    /// </summary>
    public Task<bool> RequestCameraPermissionAsync()
    {
        if (ContextCompat.CheckSelfPermission(this, global::Android.Manifest.Permission.Camera) == Permission.Granted)
        {
            return Task.FromResult(true);
        }

        var tcs = new TaskCompletionSource<bool>();
        _cameraPermission = tcs;
        RunOnUiThread(() => ActivityCompat.RequestPermissions(this, [global::Android.Manifest.Permission.Camera], CameraRequestCode));
        return tcs.Task;
    }

    /// <summary>
    /// Requests read access to shared storage and completes with whether it was granted.
    /// </summary>
    public Task<bool> RequestStoragePermissionAsync()
    {
        if (ContextCompat.CheckSelfPermission(this, global::Android.Manifest.Permission.ReadExternalStorage) == Permission.Granted)
        {
            return Task.FromResult(true);
        }

        var tcs = new TaskCompletionSource<bool>();
        _storagePermission = tcs;
        RunOnUiThread(() => ActivityCompat.RequestPermissions(
            this,
            [global::Android.Manifest.Permission.ReadExternalStorage, global::Android.Manifest.Permission.WriteExternalStorage],
            StorageRequestCode));
        return tcs.Task;
    }

    /// <summary>
    /// Requests the notification permission and completes with whether it was granted; below API 33 the
    /// manifest declaration is enough on its own.
    /// </summary>
    public Task<bool> RequestNotificationPermissionAsync()
    {
        if (global::Android.OS.Build.VERSION.SdkInt < global::Android.OS.BuildVersionCodes.Tiramisu)
        {
            return Task.FromResult(true);
        }

        if (ContextCompat.CheckSelfPermission(this, global::Android.Manifest.Permission.PostNotifications) == Permission.Granted)
        {
            return Task.FromResult(true);
        }

        var tcs = new TaskCompletionSource<bool>();
        _notificationPermission = tcs;
        RunOnUiThread(() => ActivityCompat.RequestPermissions(this, [global::Android.Manifest.Permission.PostNotifications], NotificationRequestCode));
        return tcs.Task;
    }

    /// <inheritdoc/>
    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        CrashLog.Install();
        base.OnCreate(savedInstanceState);

        // Avalonia is up by now, so the theme it opened with is the one the bars are painted in.
        AndroidSystemBars.Attach(this);
        FrameRest.Attach();
        App.Stage("activity", clock);
    }

    /// <inheritdoc/>
    protected override void OnStart()
    {
        base.OnStart();
        Shown = true;
        TunnelNotices.Cancel(this, TunnelNotices.StoppedId);
        ShownChanged?.Invoke(true);
    }

    /// <inheritdoc/>
    protected override void OnStop()
    {
        base.OnStop();
        Shown = false;
        ShownChanged?.Invoke(false);
    }

    /// <inheritdoc/>
    protected override void OnResume()
    {
        base.OnResume();
        Current = this;
        App.FollowSystemTheme(Resources?.Configuration);
        AndroidSystemBars.Refresh();
        Resumed?.Invoke();

        // Asked once per process: the update and VPN notices otherwise never post past API 33.
        if (!_notificationRequested)
        {
            _notificationRequested = true;
            _ = RequestNotificationPermissionAsync();
        }
    }

    /// <inheritdoc/>
    public override bool DispatchTouchEvent(global::Android.Views.MotionEvent? ev)
    {
        FrameRest.Wake();
        return base.DispatchTouchEvent(ev);
    }

    /// <inheritdoc/>
    public override bool DispatchKeyEvent(global::Android.Views.KeyEvent? e)
    {
        FrameRest.Wake();
        return base.DispatchKeyEvent(e);
    }

    /// <inheritdoc/>
    public override bool DispatchGenericMotionEvent(global::Android.Views.MotionEvent? ev)
    {
        FrameRest.Wake();
        return base.DispatchGenericMotionEvent(ev);
    }

    /// <inheritdoc/>
    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);

        // The activity declares the ui mode among the changes it handles, so it is not recreated and the theme
        // is picked up here.
        App.FollowSystemTheme(newConfig);
    }

    /// <inheritdoc/>
    protected override void OnDestroy()
    {
        base.OnDestroy();
        AndroidSystemBars.Detach(this);

        // Unloads the head: the tunnel runs in a process of its own, so nothing here has to be kept around after
        // the user has closed the window. A system-driven restart (a configuration change) is left alone.
        if (IsFinishing)
        {
            global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
            return;
        }

        ReleaseSingleView();
    }

    // Frees the single view from the root going away: the next activity attaches the same instance, and a
    // control that still has a parent is refused (#196). Avalonia must already be up, so this cannot move
    // ahead of base.OnCreate - touching it earlier builds the shared dispatcher on a stub that runs nothing.
    private static void ReleaseSingleView()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not ISingleViewApplicationLifetime { MainView: { } view })
        {
            return;
        }

        if (view.GetVisualRoot() is ContentControl root)
        {
            root.Content = null;
        }

        if (view.GetVisualParent() is ContentPresenter presenter)
        {
            presenter.UpdateChild();
        }
    }

    /// <inheritdoc/>
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == VpnRequestCode)
        {
            _vpnPermission?.TrySetResult(resultCode == Result.Ok);
            _vpnPermission = null;
        }
    }

    /// <inheritdoc/>
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == CameraRequestCode)
        {
            _cameraPermission?.TrySetResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
            _cameraPermission = null;
        }

        if (requestCode == StorageRequestCode)
        {
            _storagePermission?.TrySetResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
            _storagePermission = null;
        }

        if (requestCode == NotificationRequestCode)
        {
            _notificationPermission?.TrySetResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
            _notificationPermission = null;
        }
    }
}
