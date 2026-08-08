using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using AmneziaGeo.Android.Ui.Services;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using SharedMainView = AmneziaGeo.Ui.MainView;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// The Avalonia application.
/// </summary>
public sealed partial class App : Avalonia.Application
{
    private AndroidAgentConnection? _connection;

    /// <inheritdoc/>
    public override void Initialize()
    {
        var clock = Stopwatch.StartNew();
        AvaloniaXamlLoader.Load(this);
        Stage("styles", clock);
    }

    /// <inheritdoc/>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            var clock = Stopwatch.StartNew();

            // Tell the shared UI which device it draws on, then drop the D-pad focus rings on a phone.
            UiPlatform.IsTelevision = global::Android.App.Application.Context.PackageManager?
                .HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureLeanback) == true;
            UiPlatform.SupportsWebSocket = false;
            UiPlatform.SupportsGeoPreview = false;
            if (!UiPlatform.IsTelevision)
            {
                Styles.Add(new StyleInclude(new Uri("avares://AmneziaGeo.Android.Ui/"))
                {
                    Source = new Uri("avares://AmneziaGeo.Ui/Themes/FlatFocus.axaml"),
                });
            }

            // Register the CameraX camera scanner so the config/routing import can scan QR codes.
            AndroidQrScanning.Register();

            // Offer an in-app exit: a TV has no window frame to close. Drops the task and the head with it, while
            // the tunnel goes on running in its own process.
            AppExitHost.Register(() => MainActivity.Current?.FinishAndRemoveTask());

            var prefs = UiPreferences.Load();
            RequestedThemeVariant = prefs.Theme switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
            Loc.Instance.ApplyStartupCulture(prefs.Language);
            Stage("preferences", clock);

            var connection = new AndroidAgentConnection();
            _connection = connection;
            var viewModel = new MainWindowViewModel(connection, prefs);
            Stage("view models", clock);

            var mainView = new SharedMainView
            {
                DataContext = viewModel,
            };
            Stage("views", clock);

            singleView.MainView = new MobileSelectHost(mainView);
            Stage("host", clock);

            // Brings the agent up after the first frame: opening the stores and projecting the first snapshot
            // cost seconds on a TV, and home already stands on its loader until the agent answers.
            Dispatcher.UIThread.Post(() =>
            {
                var agentClock = Stopwatch.StartNew();
                viewModel.Start();
                Stage("agent", agentClock);
            }, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Times one startup step into logcat: a weak TV spends seconds here and only the device shows which step.
    internal static void Stage(string name, Stopwatch clock)
    {
        global::Android.Util.Log.Info("AmneziaGeoStart", $"{name} {clock.ElapsedMilliseconds} ms");
        clock.Restart();
    }
}
