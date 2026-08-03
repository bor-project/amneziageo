using Android.App;
using Android.Content;
using Android.Content.PM;
using Avalonia.Android;
using AndroidX.Core.App;
using AndroidX.Core.Content;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// Launcher activity hosting the Avalonia app; also brokers the VpnService consent and camera-permission dialogs.
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
    private static TaskCompletionSource<bool>? _vpnPermission;
    private static TaskCompletionSource<bool>? _cameraPermission;

    /// <summary>
    /// Raised when the activity comes back to the foreground; screens that sent the user to system settings
    /// re-read what changed.
    /// </summary>
    public static event Action? Resumed;

    /// <summary>
    /// The foreground activity, used to launch the VpnService consent dialog.
    /// </summary>
    public static MainActivity? Current { get; private set; }

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

    /// <inheritdoc/>
    protected override void OnResume()
    {
        base.OnResume();
        Current = this;
        Resumed?.Invoke();
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
    }
}
