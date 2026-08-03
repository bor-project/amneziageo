using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
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
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc/>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // Register the CameraX camera scanner so the config/routing import can scan QR codes.
            AndroidQrScanning.Register();

            // Offer an in-app exit: a TV has no window frame to close. Finish the activity only, so the tunnel
            // keeps running in its foreground service.
            AppExitHost.Register(() => MainActivity.Current?.Finish());

            var prefs = UiPreferences.Load();
            RequestedThemeVariant = prefs.Theme switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
            Loc.Instance.ApplyStartupCulture(prefs.Language);

            var connection = new AndroidAgentConnection();
            _connection = connection;
            var viewModel = new MainWindowViewModel(connection, prefs);
            var mainView = new SharedMainView
            {
                DataContext = viewModel,
            };
            singleView.MainView = new MobileSelectHost(mainView);
            viewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
