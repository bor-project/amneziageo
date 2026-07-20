using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// Launcher activity hosting the Avalonia app.
/// </summary>
[Activity(
    Label = "AmneziaGeo",
    Theme = "@style/AppTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity<App>
{
}
