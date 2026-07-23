using Android.App;
using Android.Content;
using Android.Content.PM;
using Avalonia.Android;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// Launcher activity hosting the Avalonia app; also brokers the VpnService consent dialog.
/// </summary>
[Activity(
    Label = "AmneziaGeo",
    Theme = "@style/AppTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity<App>
{
    private const int VpnRequestCode = 0x7A11;
    private static TaskCompletionSource<bool>? _vpnPermission;

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

    /// <inheritdoc/>
    protected override void OnResume()
    {
        base.OnResume();
        Current = this;
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
}
